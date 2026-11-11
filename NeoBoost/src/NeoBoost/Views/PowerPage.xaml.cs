using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoBoost.Services;

namespace NeoBoost.Views;

public sealed partial class PowerPage : Page
{
    private static readonly Guid[] Overlays = [PowerService.OverlayBestEfficiency, PowerService.OverlayBalanced, PowerService.OverlayBestPerformance];

    private bool _loading;

    public PowerPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Load();

    private void Load()
    {
        _loading = true;
        try
        {
            var schemes = PowerService.GetSchemes();
            BoostInfo.IsOpen = PowerService.IsBoostActive;

            SchemesPanel.Children.Clear();
            foreach (var scheme in schemes)
                SchemesPanel.Children.Add(BuildSchemeCard(scheme));

            UltimateCard.Visibility = schemes.Any(PowerService.IsUltimate) ? Visibility.Collapsed : Visibility.Visible;

            var overlay = PowerService.OverlayAvailable ? PowerService.GetOverlay() : null;
            OverlaySection.Visibility = overlay is null ? Visibility.Collapsed : Visibility.Visible;
            if (overlay is { } current)
            {
                var index = Array.IndexOf(Overlays, current);
                OverlayCombo.SelectedIndex = index;
                OverlayHint.Text = index switch
                {
                    0 => "Le processeur ralentit pour économiser l'énergie.",
                    2 => "Le processeur monte plus vite en fréquence. Consomme et chauffe un peu plus.",
                    _ => "Le réglage par défaut de Windows.",
                };
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private Border BuildSchemeCard(PowerScheme scheme)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var radio = new RadioButton { IsChecked = scheme.IsActive, GroupName = "schemes", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        radio.Checked += (_, _) =>
        {
            if (!_loading)
                Activate(scheme);
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(radio, scheme.Name);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = scheme.Name });
        text.Children.Add(new TextBlock
        {
            Text = scheme.Hint,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(text, 1);

        grid.Children.Add(radio);
        grid.Children.Add(text);
        if (scheme.IsActive)
        {
            var tag = new TextBlock
            {
                Text = "Actif",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            };
            Grid.SetColumn(tag, 2);
            grid.Children.Add(tag);
        }

        return new Border { Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(16, 12, 16, 12), Child = grid };
    }

    private void Activate(PowerScheme scheme)
    {
        ErrorBar.IsOpen = false;
        try
        {
            PowerService.SetActiveScheme(scheme.Id);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        Load();
    }

    private void OverlayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OverlayCombo.SelectedIndex < 0)
            return;
        ErrorBar.IsOpen = false;
        try
        {
            PowerService.SetOverlay(Overlays[OverlayCombo.SelectedIndex]);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        Load();
    }

    private void UltimateButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorBar.IsOpen = false;
        if (!AdminService.IsAdministrator)
        {
            if (AdminService.RelaunchElevated("--page=Power"))
                App.MainWindow?.Quit();
            else
                ShowError("Sans les droits administrateur, Windows ne permet pas d'ajouter ce mode.");
            return;
        }
        try
        {
            var id = PowerService.AddUltimatePerformance();
            PowerService.SetActiveScheme(id);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        Load();
    }

    private void ShowError(string message)
    {
        ErrorBar.Title = "Impossible de changer le mode d'alimentation";
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
