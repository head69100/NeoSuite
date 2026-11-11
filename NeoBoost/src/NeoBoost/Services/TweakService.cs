using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NeoBoost.Services;

/// <summary>Un réglage de Windows qui influe réellement sur la réactivité, avec son effet annoncé sans exagération.</summary>
public sealed class Tweak
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    /// <summary>Ce qu'on peut honnêtement en attendre.</summary>
    public required string Impact { get; init; }
    /// <summary>État affiché quand le réglage est optimisé (ex. « Désactivées »).</summary>
    public required string OptimizedText { get; init; }
    public required string DefaultText { get; init; }
    public string Glyph { get; init; } = "";
    public bool NeedsAdmin { get; init; }
    public bool NeedsRestart { get; init; }

    public required Func<bool> IsOptimized { get; init; }
    public required Action<bool> SetOptimized { get; init; }
}

public static class TweakService
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameBar = @"Software\Microsoft\GameBar";
    private const string Desktop = @"Control Panel\Desktop";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    public static IReadOnlyList<Tweak> All { get; } =
    [
        new()
        {
            Id = "animations",
            Title = "Animations des fenêtres",
            Description = "Ouverture, réduction et agrandissement animés des fenêtres et des menus.",
            Impact = "Les fenêtres apparaissent instantanément. Gain surtout sensible sur les PC modestes.",
            OptimizedText = "Désactivées",
            DefaultText = "Activées",
            Glyph = "",
            IsOptimized = () => !GetAnimations(),
            SetOptimized = optimized => SetAnimations(!optimized),
        },
        new()
        {
            Id = "transparency",
            Title = "Effets de transparence",
            Description = "Effet de verre de la barre des tâches, du menu Démarrer et des fenêtres.",
            Impact = "Allège un peu la carte graphique intégrée des portables et PC d'entrée de gamme.",
            OptimizedText = "Désactivés",
            DefaultText = "Activés",
            Glyph = "",
            IsOptimized = () => ReadDword(Registry.CurrentUser, Personalize, "EnableTransparency", 1) == 0,
            SetOptimized = optimized =>
            {
                WriteDword(Registry.CurrentUser, Personalize, "EnableTransparency", optimized ? 0 : 1);
                BroadcastSettingChange("ImmersiveColorSet");
            },
        },
        new()
        {
            Id = "menudelay",
            Title = "Délai d'ouverture des menus",
            Description = "Windows attend 0,4 seconde avant d'ouvrir un sous-menu au survol de la souris.",
            Impact = "Les menus s'ouvrent plus vite : meilleure réactivité ressentie, pas de puissance en plus. Prend effet à la prochaine ouverture de session.",
            OptimizedText = "Court (0,1 s)",
            DefaultText = "Normal (0,4 s)",
            Glyph = "",
            IsOptimized = () => int.TryParse(Registry.CurrentUser.OpenSubKey(Desktop)?.GetValue("MenuShowDelay") as string, out var ms) && ms <= 150,
            SetOptimized = optimized =>
            {
                using var key = Registry.CurrentUser.CreateSubKey(Desktop);
                key.SetValue("MenuShowDelay", optimized ? "100" : "400", RegistryValueKind.String);
            },
        },
        new()
        {
            Id = "gamemode",
            Title = "Mode jeu de Windows",
            Description = "Donne la priorité au jeu en cours et suspend les mises à jour Windows pendant la partie.",
            Impact = "Images par seconde plus régulières quand des tâches tournent en fond. Activé par défaut : à laisser activé.",
            OptimizedText = "Activé",
            DefaultText = "Désactivé",
            Glyph = "",
            IsOptimized = () => ReadDword(Registry.CurrentUser, GameBar, "AutoGameModeEnabled", 1) != 0,
            SetOptimized = optimized => WriteDword(Registry.CurrentUser, GameBar, "AutoGameModeEnabled", optimized ? 1 : 0),
        },
        new()
        {
            Id = "gamedvr",
            Title = "Enregistrement des jeux en arrière-plan",
            Description = "La Xbox Game Bar filme en permanence les dernières minutes de jeu pour pouvoir les sauvegarder.",
            Impact = "Libère processeur et carte graphique pendant les jeux. Désactivé par défaut sur la plupart des PC.",
            OptimizedText = "Désactivé",
            DefaultText = "Activé",
            Glyph = "",
            IsOptimized = () => ReadDword(Registry.CurrentUser, GameDvr, "HistoricalCaptureEnabled", 0) == 0,
            SetOptimized = optimized => WriteDword(Registry.CurrentUser, GameDvr, "HistoricalCaptureEnabled", optimized ? 0 : 1),
        },
        new()
        {
            Id = "hags",
            Title = "Planification GPU à accélération matérielle",
            Description = "La carte graphique gère elle-même sa file de travail au lieu de Windows.",
            Impact = "Peut réduire la latence et améliorer certains jeux ; sans effet, voire instable, sur d'autres. Carte graphique récente requise. Redémarrage nécessaire.",
            OptimizedText = "Activée",
            DefaultText = "Désactivée",
            Glyph = "",
            NeedsAdmin = true,
            NeedsRestart = true,
            IsOptimized = () => ReadDword(Registry.LocalMachine, GraphicsDrivers, "HwSchMode", 1) == 2,
            SetOptimized = optimized => WriteDword(Registry.LocalMachine, GraphicsDrivers, "HwSchMode", optimized ? 2 : 1),
        },
    ];

    /// <summary>Applique un réglage en mémorisant son état d'origine (pour « Tout rétablir »).</summary>
    public static void Apply(Tweak tweak, bool optimized)
    {
        var settings = AppSettings.Current;
        if (!settings.TweakOriginals.ContainsKey(tweak.Id))
            settings.TweakOriginals[tweak.Id] = tweak.IsOptimized();
        tweak.SetOptimized(optimized);
        settings.Save();
    }

    /// <summary>Remet chaque réglage modifié dans son état d'origine. Renvoie les réglages qui n'ont pas pu l'être.</summary>
    public static List<string> RestoreAll()
    {
        var settings = AppSettings.Current;
        var failed = new List<string>();
        foreach (var (id, original) in settings.TweakOriginals.ToList())
        {
            var tweak = All.FirstOrDefault(t => t.Id == id);
            if (tweak is null)
            {
                settings.TweakOriginals.Remove(id);
                continue;
            }
            if (tweak.NeedsAdmin && !AdminService.IsAdministrator)
            {
                failed.Add(tweak.Title);
                continue;
            }
            try
            {
                tweak.SetOptimized(original);
                settings.TweakOriginals.Remove(id);
            }
            catch (Exception)
            {
                failed.Add(tweak.Title);
            }
        }
        settings.Save();
        return failed;
    }

    // ---------- Accès Windows ----------

    private static int ReadDword(RegistryKey root, string path, string name, int fallback)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) is int value ? value : fallback;
    }

    private static void WriteDword(RegistryKey root, string path, string name, int value)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private const uint SPI_SETANIMATION = 0x0049;
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    private const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
    private const uint SPIF_UPDATEINIFILE_SENDCHANGE = 0x01 | 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int MinAnimate;
    }

    private static bool GetAnimations() => SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out bool on, 0) && on;

    private static void SetAnimations(bool on)
    {
        SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, on ? 1 : 0, SPIF_UPDATEINIFILE_SENDCHANGE);
        var info = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>(), MinAnimate = on ? 1 : 0 };
        SystemParametersInfo(SPI_SETANIMATION, info.Size, ref info, SPIF_UPDATEINIFILE_SENDCHANGE);
    }

    private static void BroadcastSettingChange(string area) =>
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, area, 0x0002 /* SMTO_ABORTIFHUNG */, 1000, out _);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, nint value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref AnimationInfo value, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
