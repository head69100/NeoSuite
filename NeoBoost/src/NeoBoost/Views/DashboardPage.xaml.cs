using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoBoost.Services;

namespace NeoBoost.Views;

public sealed partial class DashboardPage : Page
{
    private sealed record Advice(string Id, InfoBarSeverity Severity, string Title, string Message, string? ActionText = null, Action? Action = null);

    private readonly DispatcherQueueTimer _timer;
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _ramHistory = new();
    private DiskKind _diskKind = DiskKind.Unknown;
    private Guid _activeScheme;
    private string _adviceSignature = "";

    public DashboardPage()
    {
        InitializeComponent();
        CpuNameText.Text = SystemMonitor.CpuName;
        DiskTitle.Text = $"Disque système ({SystemMonitor.SystemDrive.TrimEnd('\\')})";

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => Refresh();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        SystemMonitor.Sample(); // première mesure : sert de base au calcul du processeur
        if (App.MainWindow is { } window)
            window.BoostChanged += OnBoostChanged;
        UpdateBoost();
        _timer.Start();
        Refresh();
        _diskKind = await SystemMonitor.GetSystemDiskKindAsync();
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        if (App.MainWindow is { } window)
            window.BoostChanged -= OnBoostChanged;
    }

    private void OnBoostChanged(object? sender, EventArgs e) => UpdateBoost();

    private void Refresh()
    {
        var s = SystemMonitor.Sample();
        Push(_cpuHistory, s.CpuPercent);
        Push(_ramHistory, s.RamPercent);

        CpuText.Text = $"{s.CpuPercent:0} %";
        CpuBar.Value = s.CpuPercent;

        RamText.Text = $"{s.RamPercent:0} %";
        RamBar.Value = s.RamPercent;
        RamDetail.Text = $"{SystemMonitor.FormatBytes(s.RamUsed)} utilisés sur {SystemMonitor.FormatBytes(s.RamTotal)}";

        DiskText.Text = $"{SystemMonitor.FormatBytes(s.DiskFree)} libres";
        DiskBar.Value = 100 - s.DiskFreePercent;
        DiskDetail.Text = $"sur {SystemMonitor.FormatBytes(s.DiskTotal)}" + _diskKind switch
        {
            DiskKind.Ssd => " · SSD",
            DiskKind.Hdd => " · disque dur",
            _ => "",
        };

        UptimeText.Text = SystemMonitor.FormatDuration(s.Uptime);
        _activeScheme = PowerService.GetActiveScheme();
        var scheme = PowerService.GetSchemes().FirstOrDefault(p => p.IsActive);
        PowerText.Text = (scheme is null ? "" : $"Alimentation : {scheme.Name}") + (s.OnBattery == true ? " · sur batterie" : "");
        BatteryWarning.Visibility = s.OnBattery == true && !PowerService.IsBoostActive ? Visibility.Visible : Visibility.Collapsed;

        ShowAdvice(BuildAdvice(s));
    }

    private static void Push(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > 5)
            history.Dequeue();
    }

    /// <summary>Uniquement des constats mesurés, avec ce qui aide vraiment.</summary>
    private List<Advice> BuildAdvice(SystemSnapshot s)
    {
        var list = new List<Advice>();
        var window = App.MainWindow;

        if (_diskKind == DiskKind.Hdd)
            list.Add(new("hdd", InfoBarSeverity.Warning, "Windows est installé sur un disque dur classique",
                "C'est souvent la première cause de lenteur. Le remplacer par un SSD rend le démarrage et l'ouverture des programmes plusieurs fois plus rapides : aucun logiciel ne peut compenser un disque dur."));

        if (s.DiskTotal > 0 && (s.DiskFreePercent < 10 || s.DiskFree < 10L << 30))
            list.Add(new("disk", InfoBarSeverity.Warning, "Le disque système est presque plein",
                "Windows a besoin d'espace libre pour sa mémoire virtuelle et ses mises à jour. Libérez de la place en supprimant les fichiers inutiles et les programmes que vous n'utilisez plus.",
                "Nettoyer", () => OpenUninstaller("Cleaner")));

        if (_ramHistory.Count >= 3 && _ramHistory.Average() > 85)
            list.Add(new("ram", InfoBarSeverity.Warning, "La mémoire est presque pleine",
                "Windows doit utiliser le disque à la place, ce qui ralentit tout. Fermez les programmes inutilisés (les navigateurs avec beaucoup d'onglets sont souvent en cause).",
                "Voir les programmes", () => window?.NavigateTo("Processes")));

        if (_cpuHistory.Count >= 5 && _cpuHistory.Average() > 85)
            list.Add(new("cpu", InfoBarSeverity.Warning, "Le processeur est très sollicité",
                "Un programme travaille en continu. Regardez lequel dans la liste des programmes actifs.",
                "Voir les programmes", () => window?.NavigateTo("Processes")));

        if (s.RamTotal > 0 && s.RamTotal < 7.5 * (1L << 30))
            list.Add(new("lowram", InfoBarSeverity.Informational, $"{SystemMonitor.FormatBytes(s.RamTotal)} de mémoire, c'est juste pour Windows",
                "Avec un navigateur et quelques programmes ouverts, la mémoire se remplit vite. Si le PC le permet, passer à 16 Go est l'amélioration la plus efficace après un SSD."));

        if (_activeScheme == PowerService.PowerSaver)
            list.Add(new("saver", InfoBarSeverity.Warning, "Le mode Économie d'énergie bride le processeur",
                "Il est utile sur batterie, mais ralentit nettement le PC.",
                "Changer", () => window?.NavigateTo("Power")));

        if (s.Uptime > TimeSpan.FromDays(7))
            list.Add(new("uptime", InfoBarSeverity.Informational, $"Le PC n'a pas redémarré depuis {(int)s.Uptime.TotalDays} jours",
                "Un redémarrage libère la mémoire accaparée par les programmes et termine les mises à jour en attente. Avec le démarrage rapide, « Arrêter » ne remet pas Windows à zéro : seul « Redémarrer » le fait.",
                "Redémarrer…", () => _ = ConfirmRestartAsync()));

        if (list.Count == 0)
            list.Add(new("ok", InfoBarSeverity.Success, "Rien à signaler",
                "Mémoire, processeur et disque se portent bien. Si le PC vous semble lent, regardez les programmes au démarrage."));
        return list;
    }

    private void ShowAdvice(List<Advice> advice)
    {
        // Reconstruit seulement si la liste change (sinon les boutons clignoteraient toutes les 2 s)
        var signature = string.Join(",", advice.Select(a => a.Id + a.Title));
        if (signature == _adviceSignature)
            return;
        _adviceSignature = signature;

        AdvicePanel.Children.Clear();
        foreach (var a in advice)
        {
            var bar = new InfoBar
            {
                Severity = a.Severity,
                Title = a.Title,
                Message = a.Message,
                IsOpen = true,
                IsClosable = false,
            };
            if (a.ActionText is not null && a.Action is { } action)
            {
                var button = new Button { Content = a.ActionText };
                button.Click += (_, _) => action();
                bar.ActionButton = button;
            }
            AdvicePanel.Children.Add(bar);
        }
    }

    // ---------- Mode Boost ----------

    private void UpdateBoost()
    {
        var active = PowerService.IsBoostActive;
        BoostTitle.Text = active ? "Mode Boost actif" : "Mode Boost";
        BoostButton.Content = active ? "Désactiver le mode Boost" : "Activer le mode Boost";
    }

    private void BoostButton_Click(object sender, RoutedEventArgs e)
    {
        BoostError.IsOpen = false;
        try
        {
            if (PowerService.IsBoostActive)
                PowerService.DisableBoost();
            else if (PowerService.EnableBoost() is var message && !PowerService.IsBoostActive)
            {
                // Déjà au maximum : rien n'a été changé
                BoostError.Severity = InfoBarSeverity.Informational;
                BoostError.Title = "Déjà en performances maximales";
                BoostError.Message = message;
                BoostError.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            BoostError.Severity = InfoBarSeverity.Error;
            BoostError.Title = "Impossible de changer le mode d'alimentation";
            BoostError.Message = ex.Message;
            BoostError.IsOpen = true;
        }
        UpdateBoost();
        Refresh();
    }

    // ---------- Actions ----------

    private void OpenUninstaller(string page) =>
        UninstallerMissing.IsOpen = !AdminService.OpenNeoUninstaller(page);

    private void OpenStartup_Click(object sender, RoutedEventArgs e) => OpenUninstaller("Startup");

    private void OpenCleaner_Click(object sender, RoutedEventArgs e) => OpenUninstaller("Cleaner");

    private async Task ConfirmRestartAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Redémarrer le PC ?",
            Content = "Enregistrez votre travail avant de continuer : les programmes ouverts seront fermés.",
            PrimaryButtonText = "Redémarrer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
    }
}
