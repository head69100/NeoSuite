using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using NeoBoost.Services;

namespace NeoBoost.Views;

public sealed partial class TweaksPage : Page
{
    public TweaksPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Load();

    private void Load()
    {
        TweaksPanel.Children.Clear();
        foreach (var tweak in TweakService.All)
            TweaksPanel.Children.Add(BuildCard(tweak));
        RestoreAllButton.Visibility = AppSettings.Current.TweakOriginals.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildCard(Tweak tweak)
    {
        var optimized = SafeIsOptimized(tweak);
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new FontIcon { Glyph = tweak.Glyph, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });

        var text = new StackPanel { Spacing = 2 };
        var title = new TextBlock { Text = tweak.Title };
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = tweak.Description, TextWrapping = TextWrapping.Wrap, Style = caption, Foreground = secondary });
        text.Children.Add(new TextBlock
        {
            Text = "Effet : " + tweak.Impact,
            TextWrapping = TextWrapping.Wrap,
            Style = caption,
            Foreground = secondary,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
        });
        var state = new TextBlock
        {
            Text = $"Actuellement : {(optimized ? tweak.OptimizedText : tweak.DefaultText)}"
                   + (tweak.NeedsAdmin && !AdminService.IsAdministrator ? " · droits administrateur requis" : ""),
            Style = caption,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = optimized ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] : secondary,
        };
        text.Children.Add(state);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var button = new Button
        {
            Content = optimized ? "Revenir au réglage par défaut" : "Optimiser",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 120,
        };
        if (!optimized)
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => Toggle(tweak, !optimized);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);

        return new Border { Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(16), Child = grid };
    }

    private void Toggle(Tweak tweak, bool optimized)
    {
        ResultBar.IsOpen = false;
        if (tweak.NeedsAdmin && !AdminService.IsAdministrator)
        {
            if (AdminService.RelaunchElevated("--page=Tweaks"))
                App.MainWindow?.Quit();
            else
                ShowResult(InfoBarSeverity.Warning, "Droits administrateur refusés", $"« {tweak.Title} » ne peut être modifié qu'en administrateur.");
            return;
        }

        try
        {
            TweakService.Apply(tweak, optimized);
            if (tweak.NeedsRestart)
                ShowResult(InfoBarSeverity.Informational, "Redémarrage nécessaire", $"« {tweak.Title} » prendra effet au prochain redémarrage du PC.");
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, $"Impossible de modifier « {tweak.Title} »", ex.Message);
        }
        Load();
    }

    private void RestoreAllButton_Click(object sender, RoutedEventArgs e)
    {
        var failed = TweakService.RestoreAll();
        if (failed.Count == 0)
            ShowResult(InfoBarSeverity.Success, "Réglages rétablis", "Tous les réglages modifiés par NeoBoost ont retrouvé leur état d'origine.");
        else
            ShowResult(InfoBarSeverity.Warning, "Rétablissement partiel",
                $"À rétablir en administrateur : {string.Join(", ", failed)}.");
        Load();
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    private static bool SafeIsOptimized(Tweak tweak)
    {
        try
        {
            return tweak.IsOptimized();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
