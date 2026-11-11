using System.Text.Json;
using Microsoft.UI.Xaml;
using Neo.Shared;
using NeoSound.Services;

namespace NeoSound;

/// <summary>
/// NeoSound duplique le son du PC vers plusieurs sorties en même temps (casque filaire + casque Bluetooth).
/// Arguments reconnus :
///   --list                 liste les sorties audio actives sur la console, puis quitte
///   --dump &lt;fichier&gt;      écrit la même liste en JSON dans un fichier, puis quitte
///   --probe &lt;secondes&gt; [rapport]  mesure la duplication en cours de route et rapporte ce que
///                          chaque casque a manqué ou jeté, puis quitte
///   --relaunched           relance demandée par une autre instance (elle se contente de réafficher la fenêtre)
/// </summary>
public partial class App : Application
{
    private readonly SingleInstance _singleInstance = new("NeoSound");

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // Modes sans interface (scripts, tests) : listage des périphériques
        if (cmdArgs.Contains("--list") || cmdArgs.Contains("--dump"))
        {
            DumpDevices(cmdArgs);
            Exit();
            return;
        }

        // Mesure de qualité : combien de son chaque casque a manqué ou jeté
        var probeIndex = Array.IndexOf(cmdArgs, "--probe");
        if (probeIndex >= 0)
        {
            Probe(cmdArgs, probeIndex);
            Exit();
            return;
        }

        // Une seule fenêtre : si NeoSound tourne déjà, on réaffiche l'existante
        if (!_singleInstance.TryAcquire(waitForPrevious: cmdArgs.Contains("--relaunched")))
        {
            _singleInstance.SignalExisting();
            Exit();
            return;
        }

        var window = new MainWindow();
        window.Activate();
        _singleInstance.ListenForShowRequests(() => window.DispatcherQueue.TryEnqueue(window.BringToFront));
    }

    /// <summary>Liste les périphériques de sortie actifs (console avec --list, fichier JSON avec --dump).</summary>
    private static void DumpDevices(string[] cmdArgs)
    {
        var devices = AudioDeviceService.GetRenderDevices();

        var dumpIndex = Array.IndexOf(cmdArgs, "--dump");
        if (dumpIndex >= 0 && dumpIndex + 1 < cmdArgs.Length)
        {
            var json = JsonSerializer.Serialize(
                devices.Select(d => new
                {
                    d.Id, d.Name, d.Description, d.IsDefault,
                    d.SampleRate, d.Channels, d.IsPhoneMode, d.Transport, d.BetterSiblingName,
                }),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(cmdArgs[dumpIndex + 1], json);
            return;
        }

        ConsoleHelper.Attach();
        Console.WriteLine("Périphériques de sortie actifs");
        foreach (var d in devices)
        {
            var phone = d.IsPhoneMode ? "  [mains libres : qualité d'appel]" : "";
            var transport = d.Transport.Length > 0 ? $", {d.Transport}" : "";
            Console.WriteLine($"  {(d.IsDefault ? "*" : " ")} {d.Name}  ({d.Description}, {d.SampleRate} Hz, {d.Channels} canal/aux{transport}){phone}");
        }
    }

    /// <summary>
    /// Mesure la duplication sans interface, avec les réglages enregistrés : le moteur tourne quelques
    /// secondes et rapporte ce que chaque casque a manqué (du silence) ou jeté (de l'avance). C'est ce
    /// qui distingue une latence trop basse d'un simple écart d'horloge entre les périphériques.
    ///   --probe &lt;secondes&gt; [rapport.json]
    /// Du son doit être joué sur la source pendant la mesure.
    /// </summary>
    private static void Probe(string[] cmdArgs, int index)
    {
        var seconds = index + 1 < cmdArgs.Length && double.TryParse(cmdArgs[index + 1], out var asked)
            ? Math.Clamp(asked, 1, 120)
            : 15;

        var settings = AppSettings.Current;
        var targets = new List<string>();
        if (settings.Target1DeviceId is { Length: > 0 } first)
            targets.Add(first);
        if (settings.DuplicateToSecond && settings.Target2DeviceId is { Length: > 0 } second)
            targets.Add(second);

        var source = settings.SourceDeviceId is { Length: > 0 } chosen
            ? chosen
            : AudioDeviceService.GetRenderDevices().FirstOrDefault(d => d.IsDefault)?.Id;

        ConsoleHelper.Attach();

        if (source is null || targets.Count == 0)
        {
            Console.WriteLine("Rien à mesurer : lancez NeoSound une fois pour qu'il enregistre la source et les casques.");
            return;
        }

        Console.WriteLine($"Mesure pendant {seconds:0} s à {settings.LatencyMs} ms — du son doit être joué sur la source.");

        // Sans contexte de synchronisation, chaque sortie annonce ses ennuis directement depuis son
        // propre fil : la mesure les voit tout de suite, au lieu de les laisser attendre une fenêtre
        // qui ne s'ouvre jamais. Ce mode quitte juste après, il n'y a rien à rétablir.
        SynchronizationContext.SetSynchronizationContext(null);

        using var duplicator = new AudioDuplicator();
        try
        {
            duplicator.Start(source, targets, settings.LatencyMs);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Démarrage impossible : " + ex.Message);
            return;
        }

        Thread.Sleep((int)(seconds * 1000));
        var health = duplicator.Health;
        var lost = duplicator.LostOutputs;
        duplicator.Stop();

        foreach (var output in health)
            Console.WriteLine($"  {output.Name} : manque {output.StarvedMs:0} ms, jeté {output.DroppedMs:0} ms, " +
                              $"blocs de {output.MaxBlockMs:0} ms, latence tenue {output.TargetMs:0} ms");

        foreach (var loss in lost)
            Console.WriteLine("  Casque perdu en cours de route — " + loss);

        if (duplicator.LastError is { } error)
            Console.WriteLine("  Sortie non ouverte : " + error);

        var worst = health.Count == 0 ? null : health.MaxBy(h => h.StarvedMs);
        Console.WriteLine(worst is { StarvedMs: > 50 }
            ? $"Verdict : « {worst.Name} » manque de son — une latence plus haute réglera le grésillement."
            : "Verdict : aucun manque notable — le tampon ne fait que suivre l'écart d'horloge.");

        var reportIndex = index + 2;
        if (reportIndex < cmdArgs.Length && !cmdArgs[reportIndex].StartsWith("--", StringComparison.Ordinal))
        {
            var report = JsonSerializer.Serialize(
                new
                {
                    Source = source,
                    settings.LatencyMs,
                    Seconds = seconds,
                    Error = duplicator.LastError,
                    Lost = lost,
                    Outputs = health.Select(h => new { h.Name, h.StarvedMs, h.DroppedMs, h.MaxBlockMs, h.TargetMs }),
                },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(cmdArgs[reportIndex], report);
        }
    }
}
