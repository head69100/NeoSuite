using System.Diagnostics;
using NeoBoost.Models;

namespace NeoBoost.Services;

/// <summary>Consommation processeur et mémoire de chaque programme, mesurée entre deux échantillons.</summary>
public sealed class ProcessMonitor
{
    // Fermer l'un de ceux-là ferait planter ou déconnecter la session
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "MemCompression", "Secure System",
        "audiodg", "conhost", "RuntimeBroker", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost",
        "TextInputHost", "taskhostw", "dllhost", "SecurityHealthSystray", "MsMpEng", "NisSrv", "spoolsv",
        "WUDFHost", "LsaIso", "NeoBoost", "NeoSuite",
    };

    private readonly Dictionary<int, TimeSpan> _lastCpu = [];
    private readonly Dictionary<string, ProcessGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _executables = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _session = Process.GetCurrentProcess().SessionId;
    private DateTime _lastSample;

    /// <summary>Mesure d'un programme, prise hors du thread UI.</summary>
    public sealed record Measure(string Name, string DisplayName, bool IsProtected, int Count, double Cpu, long Memory, string? ExecutablePath);

    /// <summary>Mesure tous les processus (lent : à appeler en arrière-plan, jamais deux fois en même temps).</summary>
    public List<Measure> Sample()
    {
        var now = DateTime.UtcNow;
        var elapsed = _lastSample == default ? 0 : (now - _lastSample).TotalMilliseconds;
        _lastSample = now;

        var totals = new Dictionary<string, (int Count, double Cpu, long Memory, bool System)>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4)
                    continue;
                seen.Add(p.Id);

                double cpu = 0;
                try
                {
                    var time = p.TotalProcessorTime;
                    if (elapsed > 0 && _lastCpu.TryGetValue(p.Id, out var previous))
                        cpu = (time - previous).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100;
                    _lastCpu[p.Id] = time;
                }
                catch (Exception)
                {
                    // Processus protégé (lancé en administrateur ou système) : processeur non mesurable sans droits
                }

                long memory = 0;
                bool system;
                try
                {
                    memory = p.WorkingSet64;
                    system = p.SessionId != _session;
                }
                catch (Exception)
                {
                    system = true;
                }

                var name = p.ProcessName;
                if (!_descriptions.ContainsKey(name))
                    _descriptions[name] = ReadDescription(p);
                if (!_executables.ContainsKey(name))
                    _executables[name] = ReadExecutablePath(p);

                var t = totals.GetValueOrDefault(name);
                totals[name] = (t.Count + 1, t.Cpu + Math.Max(0, cpu), t.Memory + memory, t.System || system);
            }
        }

        foreach (var pid in _lastCpu.Keys.Where(id => !seen.Contains(id)).ToList())
            _lastCpu.Remove(pid);

        return totals.Select(kv => new Measure(
            kv.Key,
            _descriptions[kv.Key] is { Length: > 0 } d ? d : kv.Key,
            kv.Value.System || Critical.Contains(kv.Key),
            kv.Value.Count,
            Math.Min(100, kv.Value.Cpu),
            kv.Value.Memory,
            _executables.GetValueOrDefault(kv.Key))).ToList();
    }

    /// <summary>Applique une mesure aux objets affichés (thread UI) ; renvoie les programmes du plus gourmand au moins gourmand.</summary>
    public List<ProcessGroup> Apply(List<Measure> measures)
    {
        var names = measures.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _groups.Keys.Where(n => !names.Contains(n)).ToList())
            _groups.Remove(gone);

        foreach (var m in measures)
        {
            if (!_groups.TryGetValue(m.Name, out var group))
            {
                group = new ProcessGroup(m.Name) { DisplayName = m.DisplayName, IsProtected = m.IsProtected };
                _groups[m.Name] = group;
                _ = ProcessIconService.LoadAsync(group, m.ExecutablePath);
            }
            group.Update(m.Count, m.Cpu, m.Memory);
        }

        return _groups.Values.OrderByDescending(g => g.CpuPercent).ThenByDescending(g => g.Memory).ToList();
    }

    private static string? ReadDescription(Process p)
    {
        try
        {
            return p.MainModule?.FileVersionInfo.FileDescription?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadExecutablePath(Process p)
    {
        try
        {
            return p.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Ferme proprement toutes les instances du programme (comme le bouton ✕ de sa fenêtre), puis force
    /// celles qui restent ouvertes au bout de quelques secondes. Renvoie le nombre de processus encore actifs.
    /// </summary>
    public static async Task<int> CloseAsync(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        p.CloseMainWindow();
                }
                catch (Exception)
                {
                }
            }

            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && processes.Any(IsAlive))
                await Task.Delay(250);

            foreach (var p in processes.Where(IsAlive))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync(new CancellationTokenSource(3000).Token);
                }
                catch (Exception)
                {
                    // Accès refusé (programme lancé en administrateur) ou déjà fermé
                }
            }
            return processes.Count(IsAlive);
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }

    private static bool IsAlive(Process p)
    {
        try
        {
            return !p.HasExited;
        }
        catch (Exception)
        {
            return true; // impossible à interroger : on le considère encore actif
        }
    }
}
