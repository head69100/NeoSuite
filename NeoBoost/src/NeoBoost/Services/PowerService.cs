using System.Runtime.InteropServices;

namespace NeoBoost.Services;

public sealed record PowerScheme(Guid Id, string Name, bool IsActive)
{
    public string Hint => Id == PowerService.Balanced ? "Recommandé par Windows : s'adapte à la charge"
        : Id == PowerService.HighPerformance ? "Le processeur reste prêt à monter en fréquence, consomme plus"
        : Id == PowerService.PowerSaver ? "Limite le processeur pour économiser l'énergie : ralentit le PC"
        : PowerService.IsUltimate(this) ? "Supprime les micro-économies d'énergie : pour les PC fixes puissants"
        : "Mode personnalisé";
}

/// <summary>
/// Modes de gestion de l'alimentation de Windows (powrprof.dll) et curseur « Mode d'alimentation »
/// de Windows 10/11 (surcouche qui ne s'applique qu'au mode Utilisation normale).
/// </summary>
public static class PowerService
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-cf4e2d8a5d6a");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    // Curseur « Mode d'alimentation » des Paramètres de Windows
    public static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid OverlayBalanced = Guid.Empty;
    public static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static List<PowerScheme> GetSchemes()
    {
        var active = GetActiveScheme();
        var schemes = new List<PowerScheme>();
        for (uint i = 0; ; i++)
        {
            var size = (uint)Marshal.SizeOf<Guid>();
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, i, buffer, ref size) != 0)
                    break;
                var id = Marshal.PtrToStructure<Guid>(buffer);
                schemes.Add(new PowerScheme(id, ReadName(id), id == active));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return schemes;
    }

    public static Guid GetActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0)
            return Guid.Empty;
        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    public static void SetActiveScheme(Guid id)
    {
        var error = PowerSetActiveScheme(IntPtr.Zero, ref id);
        if (error != 0)
            throw new InvalidOperationException($"Windows a refusé de changer de mode d'alimentation (erreur {error}).");
    }

    /// <summary>Le curseur « Mode d'alimentation » est disponible (mode Utilisation normale actif) ?</summary>
    public static bool OverlayAvailable => GetActiveScheme() == Balanced;

    public static Guid? GetOverlay() => PowerGetEffectiveOverlayScheme(out var id) == 0 ? id : null;

    public static void SetOverlay(Guid id)
    {
        var error = PowerSetActiveOverlayScheme(id);
        if (error != 0)
            throw new InvalidOperationException($"Windows a refusé de changer le mode d'alimentation (erreur {error}).");
    }

    /// <summary>Crée le mode « Performances optimales » (caché par Windows). Droits administrateur requis.</summary>
    public static Guid AddUltimatePerformance()
    {
        var template = UltimateTemplate;
        var created = IntPtr.Zero;
        var error = PowerDuplicateScheme(IntPtr.Zero, ref template, ref created);
        if (error != 0)
            throw new InvalidOperationException(error == 5
                ? "Créer ce mode demande les droits administrateur."
                : $"Windows n'a pas pu créer ce mode (erreur {error}). Il n'est pas proposé sur tous les PC.");
        try
        {
            return Marshal.PtrToStructure<Guid>(created);
        }
        finally
        {
            LocalFree(created);
        }
    }

    /// <summary>Copie du mode « Performances optimales » (Windows lui donne un nouvel identifiant à chaque copie).</summary>
    public static bool IsUltimate(PowerScheme scheme) =>
        scheme.Id == UltimateTemplate
        || scheme.Name.Contains("optimal", StringComparison.OrdinalIgnoreCase)
        || scheme.Name.Contains("ultimate", StringComparison.OrdinalIgnoreCase);

    // ---------- Mode Boost ----------

    public static bool IsBoostActive => AppSettings.Current.Boost is not null;

    /// <summary>
    /// Passe le PC en performances maximales : mode « Performances optimales » ou « Performances élevées »
    /// s'il existe, sinon le curseur de Windows sur « Meilleures performances ». L'état précédent est mémorisé.
    /// </summary>
    public static string EnableBoost()
    {
        var settings = AppSettings.Current;
        var schemes = GetSchemes();
        var snapshot = new BoostSnapshot(GetActiveScheme(), GetOverlay());

        var target = schemes.FirstOrDefault(IsUltimate) ?? schemes.FirstOrDefault(s => s.Id == HighPerformance);
        if (target is { IsActive: true })
            return $"Le PC est déjà en mode « {target.Name} » : le mode Boost n'apporterait rien de plus.";
        if (target is null && snapshot.PreviousScheme == Balanced && snapshot.PreviousOverlay == OverlayBestPerformance)
            return "Le PC est déjà réglé sur « Meilleures performances » : le mode Boost n'apporterait rien de plus.";

        string description;
        if (target is not null)
        {
            SetActiveScheme(target.Id);
            description = $"Mode d'alimentation « {target.Name} » activé.";
        }
        else
        {
            if (snapshot.PreviousScheme != Balanced)
                SetActiveScheme(Balanced);
            SetOverlay(OverlayBestPerformance);
            description = "Mode d'alimentation « Meilleures performances » activé.";
        }

        settings.Boost = snapshot;
        settings.Save();
        return description;
    }

    /// <summary>Rétablit le mode d'alimentation d'avant le Boost.</summary>
    public static void DisableBoost()
    {
        var settings = AppSettings.Current;
        if (settings.Boost is not { } snapshot)
            return;
        try
        {
            SetActiveScheme(snapshot.PreviousScheme);
            if (snapshot.PreviousOverlay is { } overlay && snapshot.PreviousScheme == Balanced)
                SetOverlay(overlay);
        }
        finally
        {
            settings.Boost = null;
            settings.Save();
        }
    }

    private static string ReadName(Guid id)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0)
            return id.ToString();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Marshal.PtrToStringUni(buffer) ?? id.ToString()
                : id.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootKey, IntPtr schemeGuid, IntPtr subGroup, uint accessFlags, uint index, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootKey, ref Guid scheme, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr rootKey, ref Guid sourceScheme, ref IntPtr destinationScheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
