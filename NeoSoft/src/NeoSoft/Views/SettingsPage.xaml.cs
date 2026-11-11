using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeoSoft.Services;

namespace NeoSoft.Views;

public sealed partial class SettingsPage : Page
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeCombo.SelectedIndex = _settings.Theme;
        CheckUpdatesToggle.IsOn = _settings.CheckUpdatesOnStartup;
        VersionText.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} · suite Neo";
        _loaded = true;
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
            return;
        _settings.Theme = ThemeCombo.SelectedIndex;
        _settings.Save();
        App.MainWindow?.SetTheme(_settings.Theme);
    }

    private void CheckUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        _settings.CheckUpdatesOnStartup = CheckUpdatesToggle.IsOn;
        _settings.Save();
    }
}
