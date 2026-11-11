using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NeoRar.Services;

/// <summary>
/// Intégration à l'Explorateur, pour l'utilisateur courant (HKCU, sans droits administrateur) :
/// « Ouvrir avec NeoRar » sur les archives, « Extraire ici » / « Extraire dans un dossier » en clic droit,
/// et « Ajouter à une archive » sur tous les fichiers et dossiers. Sous Windows 11, ces entrées sont
/// dans « Afficher plus d'options » (le menu court exige une appli empaquetée et signée).
/// </summary>
public static class ShellIntegration
{
    private const string Classes = @"Software\Classes";
    private const string ProgId = "NeoRar.Archive";
    private static readonly string[] Extensions = [".rar", ".zip", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso", ".cab", ".001"];

    private static string Exe => Environment.ProcessPath!;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ProgId}\shell\open\command");
            return key?.GetValue(null) is string command && command.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Enable()
    {
        using (var progId = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ProgId}"))
        {
            progId.SetValue(null, "Archive");
            progId.SetValue("FriendlyTypeName", "Archive");
            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{Exe}\",0");
            using (var open = progId.CreateSubKey(@"shell\open\command"))
                open.SetValue(null, $"\"{Exe}\" \"%1\"");
        }

        foreach (var ext in Extensions)
        {
            // Proposé dans « Ouvrir avec » sans voler l'association choisie par l'utilisateur
            using (var openWith = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ext}\OpenWithProgids"))
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

            var shell = $@"{Classes}\SystemFileAssociations\{ext}\shell";
            AddVerb(shell, "NeoRar.ExtractHere", "Extraire ici (NeoRar)", "--extract-here");
            AddVerb(shell, "NeoRar.ExtractFolder", "Extraire dans un dossier (NeoRar)", "--extract-folder");
            AddVerb(shell, "NeoRar.Open", "Ouvrir avec NeoRar", null);
        }

        AddVerb($@"{Classes}\*\shell", "NeoRar.Add", "Ajouter à une archive… (NeoRar)", "--add");
        AddVerb($@"{Classes}\Directory\shell", "NeoRar.Add", "Ajouter à une archive… (NeoRar)", "--add");

        NotifyShell();
    }

    public static void Disable()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{ProgId}", throwOnMissingSubKey: false);
        foreach (var ext in Extensions)
        {
            using (var openWith = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ext}\OpenWithProgids", writable: true))
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            foreach (var verb in new[] { "NeoRar.ExtractHere", "NeoRar.ExtractFolder", "NeoRar.Open" })
                Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\SystemFileAssociations\{ext}\shell\{verb}", throwOnMissingSubKey: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\*\shell\NeoRar.Add", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\Directory\shell\NeoRar.Add", throwOnMissingSubKey: false);
        NotifyShell();
    }

    private static void AddVerb(string shellKey, string verb, string text, string? argument)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{shellKey}\{verb}");
        key.SetValue("MUIVerb", text);
        key.SetValue("Icon", $"\"{Exe}\",0");
        using var command = key.CreateSubKey("command");
        command.SetValue(null, argument is null ? $"\"{Exe}\" \"%1\"" : $"\"{Exe}\" {argument} \"%1\"");
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
