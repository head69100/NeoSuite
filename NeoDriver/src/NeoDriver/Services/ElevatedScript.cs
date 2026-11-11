using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NeoDriver.Services;

public sealed record ScriptResult(int ExitCode, string Output, bool Cancelled)
{
    public bool Success => !Cancelled && ExitCode == 0;

    public IEnumerable<string> Lines => Output.Replace("\r\n", "\n").Split('\n');

    /// <summary>Texte qui suit <paramref name="prefix"/> sur la première ligne qui commence ainsi (null s'il n'y en a pas).</summary>
    public string? Marker(string prefix) =>
        Lines.Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l[prefix.Length..].Trim()).FirstOrDefault();

    /// <summary>Sortie sans les lignes « NEO:… » réservées au programme.</summary>
    public string VisibleOutput => string.Join(Environment.NewLine, Lines.Where(l => !l.StartsWith("NEO:", StringComparison.Ordinal))).Trim();
}

/// <summary>
/// Exécute un script PowerShell avec les droits administrateur, sans redémarrer NeoDriver : une seule confirmation
/// Windows (aucune si NeoDriver est déjà en administrateur). Un processus élevé ne peut pas renvoyer sa sortie à
/// un programme qui ne l'est pas : le script l'écrit dans un fichier, lu ensuite ici.
/// Le script fixe son code de sortie en écrivant <c>$global:NeoExit = ...</c> ; les lignes « NEO:… » sont des
/// repères pour NeoDriver.
/// </summary>
public static class ElevatedScript
{
    /// <summary>Texte entre apostrophes pour PowerShell (les apostrophes sont doublées).</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <param name="elevate">Null : élevé sauf si NeoDriver l'est déjà. False : jamais (essais sans droits).</param>
    public static async Task<ScriptResult> RunAsync(string script, bool? elevate = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "NeoDriver");
        Directory.CreateDirectory(folder);
        var outFile = Path.Combine(folder, $"out-{Guid.NewGuid():N}.txt");

        var full = $$"""
            $global:NeoExit = 0
            # pnputil écrit en ANSI quand sa sortie est redirigée : sans cela PowerShell la lit en OEM et abîme les accents
            try { [Console]::OutputEncoding = [Text.Encoding]::Default } catch { }
            & {
            {{script}}
            } *>&1 | Out-File -LiteralPath {{Quote(outFile)}} -Encoding utf8
            exit $global:NeoExit
            """;

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(full))}")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (elevate ?? !AdminService.IsAdministrator)
            psi.Verb = "runas";

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED : confirmation Windows refusée
        {
            return new ScriptResult(1223, "", Cancelled: true);
        }
        if (process is null)
            return new ScriptResult(-1, "Impossible de lancer PowerShell.", Cancelled: false);

        int exitCode;
        using (process)
        {
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }

        var output = "";
        try
        {
            if (File.Exists(outFile))
            {
                output = await File.ReadAllTextAsync(outFile);
                File.Delete(outFile);
            }
        }
        catch (IOException)
        {
        }
        return new ScriptResult(exitCode, output, Cancelled: false);
    }

    /// <summary>Point de restauration avant une opération sur les pilotes. En cas d'échec le script s'arrête (repère NEO:RESTORE:FAILED).</summary>
    public static string RestorePointBlock(string description) => $$"""
        try {
            Checkpoint-Computer -Description {{Quote(description)}} -RestorePointType DEVICE_DRIVER_INSTALL -ErrorAction Stop -WarningAction SilentlyContinue
            'NEO:RESTORE:OK'
        } catch {
            'NEO:RESTORE:FAILED:' + $_.Exception.Message
            $global:NeoExit = 90
            return
        }

        """;
}
