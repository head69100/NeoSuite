using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>
/// Trouve l'option « sans fenêtres » d'un désinstalleur quand le programme ne la déclare pas :
/// Inno Setup (/VERYSILENT), NSIS (/S) ou Windows Installer (/qn).
/// </summary>
public static class SilentUninstall
{
    private static readonly Dictionary<string, string?> Cache = [];

    /// <summary>Options à ajouter à la commande de désinstallation, ou null si aucune n'est connue.</summary>
    public static string? SwitchesFor(InstalledProgram program)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(program.Key, out var cached))
                return cached;
        }

        string? result = null;
        try
        {
            var (file, args) = UninstallRunner.BaseCommand(program);
            result = Detect(file, args);
        }
        catch (Exception)
        {
        }
        lock (Cache)
            Cache[program.Key] = result;
        return result;
    }

    private static string? Detect(string file, string existingArgs)
    {
        if (existingArgs.Contains("/S", StringComparison.Ordinal) || existingArgs.Contains("SILENT", StringComparison.OrdinalIgnoreCase))
            return null; // déjà silencieux ou option déjà présente
        if (!File.Exists(file) || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var stream = File.OpenRead(file);
            var buffer = new byte[Math.Min(stream.Length, 3 * 1024 * 1024)];
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = System.Text.Encoding.Latin1.GetString(buffer, 0, read);
            if (text.Contains("Inno Setup", StringComparison.Ordinal))
                return "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
            if (text.Contains("Nullsoft Install System", StringComparison.Ordinal))
                return "/S";
        }
        catch (Exception)
        {
        }
        return null;
    }
}
