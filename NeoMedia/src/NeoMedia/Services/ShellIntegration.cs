using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NeoMedia.Services;

/// <summary>
/// Intégration à l'Explorateur pour l'utilisateur courant (HKCU, sans droits administrateur) : NeoMedia apparaît
/// dans « Ouvrir avec » des vidéos et musiques, avec « Lire avec NeoMedia » en clic droit, et « Lire avec NeoMedia »
/// sur les dossiers (tous leurs fichiers à la suite). Sous Windows 11 : dans « Afficher plus d'options ».
/// </summary>
public static class ShellIntegration
{
    private const string Classes = @"Software\Classes";
    private const string VideoProgId = "NeoMedia.Video";
    private const string AudioProgId = "NeoMedia.Audio";

    private static string Exe => Environment.ProcessPath!;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Classes}\{VideoProgId}\shell\open\command");
            return key?.GetValue(null) is string command && command.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Enable()
    {
        RegisterProgId(VideoProgId, "Fichier vidéo");
        RegisterProgId(AudioProgId, "Fichier audio");

        foreach (var ext in MediaFiles.VideoExtensions)
            Associate(ext, VideoProgId);
        foreach (var ext in MediaFiles.AudioExtensions)
            Associate(ext, AudioProgId);

        using (var folder = Registry.CurrentUser.CreateSubKey($@"{Classes}\Directory\shell\NeoMedia.Play"))
        {
            folder.SetValue("MUIVerb", "Lire avec NeoMedia");
            folder.SetValue("Icon", $"\"{Exe}\",0");
            using var command = folder.CreateSubKey("command");
            command.SetValue(null, $"\"{Exe}\" \"%1\"");
        }

        NotifyShell();
    }

    public static void Disable()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{VideoProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\{AudioProgId}", throwOnMissingSubKey: false);
        foreach (var ext in MediaFiles.VideoExtensions.Concat(MediaFiles.AudioExtensions))
        {
            using (var openWith = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ext}\OpenWithProgids", writable: true))
            {
                openWith?.DeleteValue(VideoProgId, throwOnMissingValue: false);
                openWith?.DeleteValue(AudioProgId, throwOnMissingValue: false);
            }
            Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\SystemFileAssociations\{ext}\shell\NeoMedia.Play", throwOnMissingSubKey: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}\Directory\shell\NeoMedia.Play", throwOnMissingSubKey: false);
        NotifyShell();
    }

    private static void RegisterProgId(string progId, string name)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Classes}\{progId}");
        key.SetValue(null, name);
        key.SetValue("FriendlyTypeName", name);
        using (var icon = key.CreateSubKey("DefaultIcon"))
            icon.SetValue(null, $"\"{Exe}\",0");
        using (var open = key.CreateSubKey(@"shell\open\command"))
            open.SetValue(null, $"\"{Exe}\" \"%1\"");
    }

    /// <summary>Proposé dans « Ouvrir avec » sans voler l'association choisie par l'utilisateur, plus « Lire avec NeoMedia ».</summary>
    private static void Associate(string ext, string progId)
    {
        using (var openWith = Registry.CurrentUser.CreateSubKey($@"{Classes}\{ext}\OpenWithProgids"))
            openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);

        using var verb = Registry.CurrentUser.CreateSubKey($@"{Classes}\SystemFileAssociations\{ext}\shell\NeoMedia.Play");
        verb.SetValue("MUIVerb", "Lire avec NeoMedia");
        verb.SetValue("Icon", $"\"{Exe}\",0");
        // Plusieurs fichiers sélectionnés : un seul NeoMedia les reçoit tous (voir FileCollector)
        verb.SetValue("MultiSelectModel", "Player");
        using var command = verb.CreateSubKey("command");
        command.SetValue(null, $"\"{Exe}\" \"%1\"");
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
