using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>
/// Lance le désinstalleur officiel du programme et attend qu'il soit vraiment terminé : beaucoup de
/// désinstalleurs (Inno Setup, NSIS…) se recopient dans %TEMP% et se relancent avant de quitter.
/// </summary>
public static partial class UninstallRunner
{
    /// <summary>Lance la désinstallation ; renvoie quand le désinstalleur et ses sous-processus sont terminés.</summary>
    public static async Task RunAsync(InstalledProgram program, CancellationToken stopWaiting)
    {
        var (file, args) = BuildCommand(program);

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = true, // respecte le manifeste du désinstalleur (demande UAC éventuelle)
                WorkingDirectory = SafeDirectory(file),
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("La désinstallation a été annulée (droits administrateur refusés).");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Impossible de lancer le désinstalleur : {ex.Message}");
        }

        if (process is null)
            return;
        await WaitForProcessTreeAsync(process, stopWaiting);
    }

    /// <summary>Commande de désinstallation, avec l'option « sans fenêtres » si elle est demandée et connue.</summary>
    internal static (string File, string Args) BuildCommand(InstalledProgram program)
    {
        var (file, args) = BaseCommand(program);
        if (program.UseQuietUninstall && string.IsNullOrWhiteSpace(program.QuietUninstallString)
            && !file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) && SilentUninstall.SwitchesFor(program) is { } switches)
            args = $"{args} {switches}".Trim();
        return (file, args);
    }

    /// <summary>Commande de base : MSI → msiexec /x {code produit}, sinon UninstallString découpée.</summary>
    internal static (string File, string Args) BaseCommand(InstalledProgram program)
    {
        if (program.IsMsi && ProductCodeRegex().Match(program.SubKeyName + " " + program.UninstallString) is { Success: true } code)
            return ("msiexec.exe", program.UseQuietUninstall ? $"/x {code.Value} /qn /norestart" : $"/x {code.Value}");

        var source = program.UseQuietUninstall && !string.IsNullOrWhiteSpace(program.QuietUninstallString) ? program.QuietUninstallString : program.UninstallString;
        var command = Environment.ExpandEnvironmentVariables(source?.Trim() ?? "");
        if (command.Length == 0)
            throw new InvalidOperationException("Ce programme n'indique pas de désinstalleur. Utilisez la désinstallation forcée.");

        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0)
                return (command[1..end], command[(end + 1)..].Trim());
        }

        // Chemin non guillemeté pouvant contenir des espaces : on coupe après « .exe »
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
            return (command[..(exe + 4)], command[(exe + 4)..].Trim());

        var space = command.IndexOf(' ');
        return space > 0 ? (command[..space], command[(space + 1)..]) : (command, "");
    }

    /// <summary>Attend la fin du processus et de tous ceux qu'il a lancés (suivi par ParentProcessId).</summary>
    private static async Task WaitForProcessTreeAsync(Process root, CancellationToken stopWaiting)
    {
        var tracked = new HashSet<int> { root.Id };
        var started = DateTime.Now.AddSeconds(-2);
        root.Dispose();

        while (!stopWaiting.IsCancellationRequested)
        {
            var alive = await Task.Run(() => SnapshotProcesses(started));

            // Nouveaux enfants de processus suivis (même s'ils ont survécu à leur parent)
            bool added;
            do
            {
                added = false;
                foreach (var (pid, parent) in alive)
                {
                    if (!tracked.Contains(pid) && tracked.Contains(parent))
                        added |= tracked.Add(pid);
                }
            }
            while (added);

            if (!alive.Any(p => tracked.Contains(p.Pid)))
                return;

            try
            {
                await Task.Delay(700, stopWaiting);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private static List<(int Pid, int Parent)> SnapshotProcesses(DateTime startedAfter)
    {
        var result = new List<(int, int)>();
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, CreationDate FROM Win32_Process");
        using var processes = searcher.Get();
        foreach (ManagementBaseObject p in processes)
        {
            // On ignore les processus plus anciens que le désinstalleur (évite les PID recyclés)
            if (p["CreationDate"] is string created && ManagementDateTimeConverter.ToDateTime(created) < startedAfter)
                continue;
            result.Add((Convert.ToInt32(p["ProcessId"]), Convert.ToInt32(p["ParentProcessId"])));
        }
        return result;
    }

    private static string SafeDirectory(string file)
    {
        try
        {
            var dir = Path.GetDirectoryName(file);
            return dir is not null && Directory.Exists(dir) ? dir : Environment.CurrentDirectory;
        }
        catch (ArgumentException)
        {
            return Environment.CurrentDirectory;
        }
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex ProductCodeRegex();
}
