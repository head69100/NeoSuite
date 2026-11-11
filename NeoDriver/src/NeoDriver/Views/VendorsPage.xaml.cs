using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using NeoDriver.Services;

namespace NeoDriver.Views;

public sealed partial class VendorsPage : Page
{
    private BoardInfo? _board;
    private ChipsetInfo? _chipset;
    private bool _loaded;

    public VendorsPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        // Les pilotes anciens sont relus à chaque visite : un pilote mis à jour disparaît de la liste
        if (_loaded)
        {
            await LoadOldDriversAsync();
            return;
        }
        _loaded = true;
        try
        {
            await Task.WhenAll(LoadBoardAsync(), LoadChipsetAsync(), LoadGpusAsync(), LoadOldDriversAsync());
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- Carte mère et BIOS ----------

    private async Task LoadBoardAsync()
    {
        _board = await FirmwareService.GetBoardAsync();
        if (_board is null)
        {
            BoardRows.Children.Add(Text("Carte mère introuvable : Windows ne communique pas son modèle."));
            return;
        }

        BoardRows.Children.Add(Row("Carte mère ou PC", _board.FullName));
        if (_board.Revision.Length > 0)
            BoardRows.Children.Add(Row("Révision", _board.Revision));

        var age = _board.BiosAgeMonths is { } m ? $" · il y a {(m < 1 ? "moins d'un mois" : m < 12 ? $"{m} mois" : $"{m / 12} an{(m >= 24 ? "s" : "")}")}" : "";
        BoardRows.Children.Add(Row("BIOS installé", $"{_board.BiosVersion} · {_board.BiosVendor}" + (_board.BiosDate is { } d ? $" · {d:dd/MM/yyyy}" : "") + age));

        if (FirmwareService.BiosAdvice(_board) is { } advice)
        {
            BiosAdviceText.Text = advice;
            BiosAdviceText.Visibility = Visibility.Visible;
        }
        BiosButton.IsEnabled = BoardDriversButton.IsEnabled = true;
    }

    private void BiosButton_Click(object sender, RoutedEventArgs e)
    {
        if (_board is not null)
            WebLinks.Open(FirmwareService.BiosSearchUrl(_board));
    }

    private void BoardDriversButton_Click(object sender, RoutedEventArgs e)
    {
        if (_board is not null)
            WebLinks.Open(FirmwareService.DriversSearchUrl(_board));
    }

    // ---------- Chipset ----------

    private async Task LoadChipsetAsync()
    {
        _chipset = await FirmwareService.GetChipsetAsync();
        ChipsetRows.Children.Add(Row("Processeur", _chipset.CpuVendor.Length > 0 ? _chipset.CpuVendor : "Inconnu"));
        ChipsetRows.Children.Add(Row("Pilote installé", _chipset.InstalledName is null
            ? "Aucun pilote de chipset du fabricant détecté (Windows utilise ses pilotes génériques)"
            : $"{_chipset.InstalledName} {_chipset.InstalledVersion}".Trim()));
        ChipsetNote.Text = _chipset.CpuVendor == "AMD"
            ? "Le pilote de chipset AMD (gestion de l'énergie, USB, stockage) se télécharge sur le site d'AMD, pas sur Windows Update. Choisissez votre chipset dans la liste du site."
            : "Le pilote de chipset Intel se télécharge sur le site d'Intel ou sur celui du fabricant de la carte mère.";
        ChipsetButton.Content = $"Télécharger le pilote {_chipset.CpuVendor} sur son site";
        ChipsetButton.IsEnabled = _chipset.CpuVendor is "AMD" or "Intel";
    }

    private void ChipsetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chipset is not null)
            WebLinks.Open(_chipset.OfficialUrl);
    }

    // ---------- Cartes graphiques ----------

    private async Task LoadGpusAsync()
    {
        var gpus = await FirmwareService.GetGpusAsync();
        if (gpus.Count == 0)
        {
            GpuPanel.Children.Add(Text("Aucune carte graphique détectée."));
            return;
        }

        foreach (var gpu in gpus)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(Row("Carte", gpu.Name));
            var version = gpu.Vendor == "NVIDIA" && gpu.FriendlyVersion != gpu.DriverVersion
                ? $"{gpu.FriendlyVersion} (version Windows {gpu.DriverVersion})"
                : gpu.DriverVersion;
            panel.Children.Add(Row("Pilote installé", version + (gpu.DriverDate is { } d ? $" · {d:dd/MM/yyyy}" : "")));
            var button = new Button { Content = $"Télécharger le dernier pilote {gpu.Vendor}", HorizontalAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => WebLinks.Open(gpu.OfficialUrl);
            panel.Children.Add(button);
            GpuPanel.Children.Add(panel);
        }
        GpuPanel.Children.Add(Text("Comparez la version installée à celle du site. Le pilote graphique est le plus souvent en retard sur Windows Update : le site du fabricant est la source de référence."));
    }

    // ---------- Pilotes anciens ----------

    private async void OldRefreshButton_Click(object sender, RoutedEventArgs e) => await LoadOldDriversAsync();

    private async void OldResetButton_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.IgnoredOldDrivers.Clear();
        AppSettings.Current.Save();
        await LoadOldDriversAsync();
    }

    private async Task LoadOldDriversAsync()
    {
        const int Shown = 25;
        OldRefreshButton.IsEnabled = false;
        try
        {
            var settings = AppSettings.Current;
            var all = await FirmwareService.GetOldDriversAsync();
            var old = all.Where(d => !settings.IgnoredOldDrivers.Contains(IgnoreKey(d))).ToList();
            var ignored = all.Count - old.Count;

            OldPanel.Children.Clear();
            OldResetButton.Visibility = ignored > 0 ? Visibility.Visible : Visibility.Collapsed;
            OldSummary.Text = old.Count == 0
                ? "Aucun pilote de réseau, audio, Bluetooth, chipset, graphique, USB ou stockage ne date de plus de 3 ans."
                : $"{old.Count} pilote{(old.Count > 1 ? "s" : "")} de plus de 3 ans (réseau, audio, Bluetooth, chipset, graphique, USB, stockage). Un pilote ancien n'est pas forcément mauvais : "
                  + "cette liste sert à repérer ceux que Windows Update ne met jamais à jour. Les pilotes de Microsoft n'y figurent pas. "
                  + "Si vous avez installé la dernière version du fabricant et qu'il reste là, c'est que le fabricant n'en propose pas de plus récente : « Ignorer » l'écarte."
                  + (old.Count > Shown ? $" Les {Shown} plus anciens sont affichés." : "");
            if (ignored > 0)
                OldSummary.Text += $" ({ignored} ignoré{(ignored > 1 ? "s" : "")}.)";

            var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
            foreach (var d in old.Take(Shown))
            {
                var text = new StackPanel { Spacing = 1 };
                text.Children.Add(new TextBlock { Text = d.DeviceName, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = $"{d.ClassName} · {d.Manufacturer} · version {d.Version} · {d.Date:dd/MM/yyyy}", Style = caption, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis });

                var search = new Button { Content = "Chercher une mise à jour", VerticalAlignment = VerticalAlignment.Center };
                search.Click += (_, _) => WebLinks.Search($"{d.Manufacturer} {d.DeviceName} driver download");
                var ignore = new Button { Content = "Ignorer", VerticalAlignment = VerticalAlignment.Center };
                ignore.Click += async (_, _) =>
                {
                    settings.IgnoredOldDrivers.Add(IgnoreKey(d));
                    settings.Save();
                    await LoadOldDriversAsync();
                };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { search, ignore } };

                var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(text);
                Grid.SetColumn(buttons, 1);
                grid.Children.Add(buttons);
                OldPanel.Children.Add(grid);
            }
        }
        finally
        {
            OldRefreshButton.IsEnabled = true;
        }
    }

    /// <summary>Un pilote ignoré revient dès que sa version change (nouvelle mise à jour du fabricant).</summary>
    private static string IgnoreKey(OldDriver d) => $"{d.DeviceName}|{d.Version}";
    // ---------- Utilitaires ----------

    private static Grid Row(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap });
        var v = new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        return grid;
    }

    private static TextBlock Text(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };
}
