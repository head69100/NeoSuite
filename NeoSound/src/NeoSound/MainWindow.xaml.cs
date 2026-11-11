using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Neo.Shared;
using NeoSound.Models;
using NeoSound.Services;
using Windows.Graphics;

namespace NeoSound;

/// <summary>
/// La fenêtre de NeoSound : choisir la source, un ou deux casques, régler la latence, démarrer.
/// Tout se pilote aussi depuis NeoSuite (clic droit sur la tuile) par la communication Neo (NeoIpc).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly WindowMinSize _minSize;
    private readonly AudioDuplicator _duplicator = new();

    /// <summary>Vérifie, peu après le démarrage, ce que les casques endurent réellement.</summary>
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private IReadOnlyList<AudioDevice> _devices = [];
    private bool _loading = true; // vrai pendant le remplissage des listes : on ignore les événements

    /// <summary>
    /// Les casques demandés au dernier démarrage. Sert à rappeler une limite de Windows (deux sorties
    /// Bluetooth) même une fois passé le premier message d'état.
    /// </summary>
    private IReadOnlyList<string> _requestedTargetIds = []; // vrai pendant le remplissage des listes : on ignore les événements

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(940, 760));
        _minSize = new WindowMinSize(this, 760, 560);

        _duplicator.Faulted += OnDuplicatorFaulted;
        _duplicator.OutputLost += OnOutputLost;
        _healthTimer.Tick += (_, _) =>
        {
            _healthTimer.Stop();
            ReportHealth();
        };
        Closed += (_, _) => Shutdown();

        LoadDevices();

        _loading = false;
        UpdateUi();

        // NeoSuite affiche l'état de NeoSound et ses actions (clic droit sur sa tuile)
        NeoIpc.StartServer("NeoSound",
            () => Task.FromResult(DescribeForSuite()),
            command => Task.FromResult(RunSuiteCommand(command)));
    }

    // ---------- Périphériques ----------

    private void LoadDevices()
    {
        _devices = AudioDeviceService.GetRenderDevices();

        SourceCombo.ItemsSource = _devices;
        Target1Combo.ItemsSource = _devices;
        Target2Combo.ItemsSource = _devices;

        var fallback = _devices.FirstOrDefault(d => d.IsDefault) ?? _devices.FirstOrDefault();

        SourceCombo.SelectedItem = Pick(_settings.SourceDeviceId) ?? fallback;
        Target1Combo.SelectedItem = Pick(_settings.Target1DeviceId) ?? FirstOther(SourceCombo.SelectedItem as AudioDevice);
        Target2Combo.SelectedItem = Pick(_settings.Target2DeviceId)
            ?? FirstOther(SourceCombo.SelectedItem as AudioDevice, Target1Combo.SelectedItem as AudioDevice);

        SecondCheck.IsChecked = _settings.DuplicateToSecond;
        LatencySlider.Value = Math.Clamp(_settings.LatencyMs, (int)LatencySlider.Minimum, (int)LatencySlider.Maximum);

        if (_devices.Count == 0)
            ShowStatus(InfoBarSeverity.Warning, "Aucun périphérique de sortie audio n'a été trouvé.");
        else if (_devices.Count == 1)
            ShowStatus(InfoBarSeverity.Warning, "Un seul périphérique de sortie est disponible : branchez ou connectez un second casque.");
    }

    private AudioDevice? Pick(string? id) =>
        id is null ? null : _devices.FirstOrDefault(d => d.Id == id);

    private AudioDevice? FirstOther(params AudioDevice?[] exclude) =>
        _devices.FirstOrDefault(d => !exclude.Contains(d));

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var wasRunning = _duplicator.IsRunning;
        StopDuplication(quiet: true);
        _loading = true;
        LoadDevices();
        _loading = false;
        UpdateUi();
        if (!wasRunning)
            ShowStatus(InfoBarSeverity.Informational, "Liste des périphériques actualisée.");
    }

    // ---------- Démarrage / arrêt ----------

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_duplicator.IsRunning)
            StopDuplication();
        else
            TryStart();
    }

    private void TryStart()
    {
        if (SourceCombo.SelectedItem is not AudioDevice source || Target1Combo.SelectedItem is not AudioDevice target1)
        {
            ShowStatus(InfoBarSeverity.Warning, "Choisissez une source et au moins un casque.");
            return;
        }

        if (target1.Id == source.Id)
        {
            ShowStatus(InfoBarSeverity.Warning, "Le casque 1 ne peut pas être le périphérique source : choisissez un casque différent (le source joue déjà le son).");
            return;
        }

        var targets = new List<string> { target1.Id };
        if (SecondCheck.IsChecked == true && Target2Combo.SelectedItem is AudioDevice target2)
        {
            if (target2.Id == source.Id)
            {
                ShowStatus(InfoBarSeverity.Warning, "Le casque 2 ne peut pas être le périphérique source.");
                return;
            }
            if (target2.Id == target1.Id)
            {
                ShowStatus(InfoBarSeverity.Warning, "Les deux casques sont identiques.");
                return;
            }
            targets.Add(target2.Id);
        }

        _requestedTargetIds = targets;

        // Casque Bluetooth en « mains libres » : Windows l'expose à côté du profil stéréo, mais il
        // joue en 16 kHz mono, avec la qualité d'un appel. Quand le même casque a un profil stéréo,
        // on refuse poliment et on indique lequel choisir.
        foreach (var target in targets.Select(id => _devices.FirstOrDefault(d => d.Id == id)))
        {
            if (target is not { IsPhoneMode: true })
                continue;
            if (target.BetterSiblingName is { } better)
            {
                ShowStatus(InfoBarSeverity.Warning,
                    $"« {target.Name} » est le profil « mains libres » de ce casque ({target.SampleRate / 1000} kHz mono, qualité d'appel) : " +
                    $"choisissez « {better} », le profil stéréo du même casque.");
                return;
            }
            ShowStatus(InfoBarSeverity.Warning,
                $"« {target.Name} » est un périphérique « mains libres » : le son aura la qualité d'un appel téléphonique. " +
                "Si Windows propose un profil stéréo du même appareil, choisissez-le à la place.");
        }

        try
        {
            _duplicator.Start(source.Id, targets, (int)LatencySlider.Value);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Démarrage impossible : " + ex.Message);
            UpdateUi();
            return;
        }

        SaveSettings(source, target1);

        // Un casque qui n'a pas pu être ouvert resterait muet sans un mot : on le dit.
        var failure = _duplicator.LastError is { } reason ? $" Casque non ouvert — {reason}." : "";
        var phoneNote = source.IsPhoneMode ? " La source est en « mains libres » : le son capturé aura une qualité d'appel." : "";
        var bluetoothNote = BluetoothNote(targets);
        ShowStatus(failure.Length > 0 || source.IsPhoneMode || bluetoothNote.Length > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            $"Duplication active vers {_duplicator.TargetNames.Count} casque(s) — source « {_duplicator.SourceName} ».{phoneNote}{failure}{bluetoothNote}");
        NeoStatus.Write("NeoSound", $"Duplication · {_duplicator.TargetNames.Count} casque(s)");
        _healthTimer.Start();
        UpdateUi();
    }

    /// <summary>
    /// Deux sorties Bluetooth demandées, une seule ouverte : Windows ne tient le plus souvent qu'une
    /// seule liaison stéréo (A2DP) à la fois, et aucun pilote virtuel n'y change quoi que ce soit.
    /// Le moteur tente les deux ; quand la seconde reste muette, il faut le dire, sinon la panne est
    /// cherchée au mauvais endroit.
    /// </summary>
    private string BluetoothNote(IReadOnlyList<string> targetIds)
    {
        var requested = targetIds
            .Select(id => _devices.FirstOrDefault(d => d.Id == id))
            .Count(d => d is { IsBluetooth: true });

        if (requested < 2 || _duplicator.TargetNames.Count >= 2)
            return "";

        return " Deux casques Bluetooth ont été demandés : Windows n'ouvre en général qu'un seul casque " +
               "Bluetooth à la fois (une seule liaison stéréo par adaptateur). Pour en écouter deux en même " +
               "temps, il faut deux adaptateurs Bluetooth ; un pilote audio virtuel n'y changerait rien.";
    }

    /// <summary>
    /// Quelques secondes après le démarrage, on rapporte ce que les casques endurent vraiment. La latence
    /// tenue dépasse parfois celle demandée : un pilote peut réclamer des blocs bien plus gros, et le
    /// tampon doit alors s'y adapter. Et si un casque manque encore de son, c'est ici qu'on le dit.
    /// </summary>
    private void ReportHealth()
    {
        if (!_duplicator.IsRunning)
            return;

        var health = _duplicator.Health;
        if (health.Count == 0)
            return;

        // Ce qui manque au tout début n'est que le remplissage des tampons : on ne s'alarme qu'au-delà.
        var starving = health.Where(h => h.StarvedMs > 400).ToList();
        if (starving.Count > 0)
        {
            var worst = starving.MaxBy(h => h.StarvedMs)!;
            ShowStatus(InfoBarSeverity.Warning,
                $"« {worst.Name} » manque de son ({worst.StarvedMs / 1000:0.0} s depuis le démarrage) : " +
                "montez la latence d'un cran et redémarrez.");
            return;
        }

        var failure = _duplicator.LastError is { } reason ? $" Casque non ouvert — {reason}." : "";
        failure += BluetoothNote(_requestedTargetIds);
        var block = health.Max(h => h.MaxBlockMs);
        var note = block > 0
            ? $" Ce pilote ne joue pas par blocs plus petits que {block:0} ms : le retard ajouté est d'environ {health.Max(h => h.TargetMs):0} ms."
            : "";
        ShowStatus(failure.Length > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            $"Duplication active vers {_duplicator.TargetNames.Count} casque(s) — source « {_duplicator.SourceName} ».{failure}{note}");
    }

    private void StopDuplication(bool quiet = false)
    {
        _healthTimer.Stop();
        if (_duplicator.IsRunning)
        {
            _duplicator.Stop();
            NeoStatus.Write("NeoSound", null);
            if (!quiet)
                ShowStatus(InfoBarSeverity.Informational, "Duplication arrêtée.");
        }
        UpdateUi();
    }

    private void OnDuplicatorFaulted(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowStatus(InfoBarSeverity.Error, "La duplication s'est arrêtée : " + message);
            NeoStatus.Write("NeoSound", null);
            UpdateUi();
        });
    }

    /// <summary>
    /// Un casque a lâché en cours de route (débranché, Bluetooth déconnecté, pilote réinitialisé) :
    /// les autres continuent de jouer, et la fenêtre dit lequel a disparu plutôt que d'annoncer
    /// tranquillement deux casques alors qu'un seul joue encore.
    /// </summary>
    private void OnOutputLost(string name, string reason)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var left = _duplicator.TargetNames.Count;
            ShowStatus(InfoBarSeverity.Warning,
                $"« {name} » a lâché : {reason}. " +
                (left > 0
                    ? $"La duplication continue vers {left} casque(s) ; rebranchez ou reconnectez ce casque, puis relancez."
                    : "Plus aucun casque ne joue."));
            NeoStatus.Write("NeoSound", left > 0 ? $"Duplication · {left} casque(s)" : null);
            UpdateUi();
        });
    }

    private void SaveSettings(AudioDevice source, AudioDevice target1)
    {
        _settings.SourceDeviceId = source.Id;
        _settings.Target1DeviceId = target1.Id;
        _settings.Target2DeviceId = (Target2Combo.SelectedItem as AudioDevice)?.Id;
        _settings.LatencyMs = (int)LatencySlider.Value;
        _settings.DuplicateToSecond = SecondCheck.IsChecked == true;
        _settings.Save();
    }

    // ---------- Interface ----------

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        // Le casque source change : on évite de laisser un casque identique à la source
        WarnIfTargetsMatchSource();
        UpdateUi();
    }

    private void SecondCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            UpdateUi();
    }

    private void LatencySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateLatencyText();
    }

    private void WarnIfTargetsMatchSource()
    {
        if (SourceCombo.SelectedItem is AudioDevice source &&
            (ReferenceEquals(source, Target1Combo.SelectedItem) || ReferenceEquals(source, Target2Combo.SelectedItem)))
        {
            ShowStatus(InfoBarSeverity.Warning,
                "Le périphérique source est aussi sélectionné comme casque : choisissez un casque différent avant de démarrer.");
        }
    }

    private void UpdateUi()
    {
        var running = _duplicator.IsRunning;

        StartButton.Content = running ? "Arrêter la duplication" : "Démarrer la duplication";
        SourceCombo.IsEnabled = !running;
        Target1Combo.IsEnabled = !running;
        LatencySlider.IsEnabled = !running;
        SecondCheck.IsEnabled = !running;
        Target2Combo.IsEnabled = !running && SecondCheck.IsChecked == true;

        TitleStatusText.Text = running
            ? $"Duplication vers {_duplicator.TargetNames.Count} casque(s)"
            : "";
    }

    private void UpdateLatencyText()
    {
        if (LatencyText is null)
            return;
        var ms = (int)LatencySlider.Value;
        var hint = ms <= 15 ? "très réactif" : ms <= 35 ? "équilibré" : ms <= 80 ? "sûr" : "très sûr (Bluetooth qui décroche)";
        LatencyText.Text = $"Latence demandée : {ms} ms — {hint} ; certains pilotes en imposent davantage.";
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    // ---------- Intégration NeoSuite ----------

    private NeoAppInfo DescribeForSuite()
    {
        NeoAction[] actions = _duplicator.IsRunning
            ?
            [
                new("open", "Ouvrir NeoSound", "\uE8BD"),
                new("stop", "Arrêter la duplication", "\uE71A"),
                new("quit", "Quitter NeoSound", "\uE7E8"),
            ]
            :
            [
                new("open", "Ouvrir NeoSound", "\uE8BD"),
                new("start", "Démarrer la duplication", "\uE768"),
                new("quit", "Quitter NeoSound", "\uE7E8"),
            ];

        var status = _duplicator.IsRunning ? $"Duplication · {_duplicator.TargetNames.Count} casque(s)" : null;
        return new NeoAppInfo(status, actions);
    }

    private NeoActionResult? RunSuiteCommand(string command)
    {
        switch (command)
        {
            case "open":
                DispatcherQueue.TryEnqueue(BringToFront);
                break;
            case "start":
                DispatcherQueue.TryEnqueue(() => { if (!_duplicator.IsRunning) TryStart(); });
                return new NeoActionResult("NeoSound", "Duplication démarrée.");
            case "stop":
                DispatcherQueue.TryEnqueue(() => StopDuplication());
                return new NeoActionResult("NeoSound", "Duplication arrêtée.");
            case "quit":
                DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
                break;
        }
        return null;
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private void Shutdown()
    {
        _duplicator.Faulted -= OnDuplicatorFaulted;
        _duplicator.OutputLost -= OnOutputLost;
        _duplicator.Dispose();
        NeoStatus.Write("NeoSound", null);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
