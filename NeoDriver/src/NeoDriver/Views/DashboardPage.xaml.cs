using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoDriver.Services;

namespace NeoDriver.Views;

public sealed partial class DashboardPage : Page
{
    private static int? _driverCount;
    private bool _scanning;

    public DashboardPage()
    {
        InitializeComponent();
        // Garde la page en mémoire : une analyse lancée ici continue d'afficher sa progression au retour
        NavigationCacheMode = NavigationCacheMode.Required;

        OsText.Text = SystemInfoService.GetOsName();
        BuildText.Text = SystemInfoService.GetOsBuild();
        PcNameText.Text = Environment.MachineName;
        CpuText.Text = SystemInfoService.GetProcessorName();
        ArchText.Text = SystemInfoService.GetArchitecture();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _ = LoadHealthAsync();

        if (_scanning)
            return;

        // Recherche lancée depuis une autre page : on affiche sa progression ici aussi
        if (DriverUpdateService.IsSearching)
            await ScanAsync(countDrivers: false);
        else
            ShowLastResult();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        _health = null;
        _ = LoadHealthAsync();
        await ScanAsync(countDrivers: true);
    }

    // ---------- Santé des périphériques ----------

    private static List<ProblemDevice>? _health;

    private async Task LoadHealthAsync()
    {
        try
        {
            _health ??= await DeviceHealthService.GetProblemsAsync();
        }
        catch (Exception)
        {
            HealthText.Text = "Impossible de lire l'état des périphériques.";
            return;
        }

        var problems = _health;
        HealthButton.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HealthIcon.Glyph = problems.Count > 0 ? "" : "";
        HealthIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[problems.Count > 0 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush"];
        HealthText.Text = problems.Count switch
        {
            0 => "Aucun problème : Windows reconnaît tous les périphériques de ce PC.",
            1 => $"1 périphérique a un problème : {problems[0].Name} — {problems[0].Explanation}",
            var n => $"{n} périphériques ont un problème (par exemple : {problems[0].Name}).",
        };
    }

    private void HealthButton_Click(object sender, RoutedEventArgs e)
    {
        InstallPage.DetectOnOpen = true;
        App.MainWindow?.NavigateTo("Install");
    }

    private void ShowLastResult()
    {
        if (_driverCount is { } count)
            DriverCountText.Text = count.ToString();

        if (DriverUpdateService.LastResult is { } updates)
        {
            UpdateCountText.Text = updates.Count.ToString();
            LastScanText.Text = DriverUpdateService.LastSearch?.ToString("HH:mm") ?? "Jamais";
            ShowUpdateStatus(updates.Count, _driverCount);
        }
    }

    private void ShowUpdateStatus(int updateCount, int? driverCount)
    {
        var drivers = driverCount is { } n ? $"{n} pilotes détectés" : "Dernière recherche Windows Update";
        if (updateCount == 0)
        {
            StatusTitle.Text = "Votre PC est à jour";
            StatusText.Text = $"{drivers}, aucune mise à jour proposée par Windows Update.";
        }
        else
        {
            StatusTitle.Text = updateCount == 1 ? "1 mise à jour disponible" : $"{updateCount} mises à jour disponibles";
            StatusText.Text = $"{drivers}. Ouvrez « Mises à jour » pour voir le détail.";
        }
    }

    private async Task ScanAsync(bool countDrivers)
    {
        _scanning = true;
        ScanButton.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        StatusTitle.Text = "Analyse en cours…";
        StatusText.Text = "Inventaire des pilotes et recherche sur Windows Update…";

        try
        {
            var updatesTask = DriverUpdateService.SearchAsync();
            if (countDrivers)
            {
                _driverCount = await SystemInfoService.CountInstalledDriversAsync();
                DriverCountText.Text = _driverCount.ToString();
            }
            var updates = await updatesTask;

            App.MainWindow?.SetUpdatesBadge(updates.Count);
            ShowLastResult();
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "L'analyse a échoué";
            StatusText.Text = ex.Message;
        }
        finally
        {
            ScanProgress.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = true;
            _scanning = false;
        }
    }
}
