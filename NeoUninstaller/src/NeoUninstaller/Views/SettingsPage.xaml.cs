using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeoUninstaller.Services;

namespace NeoUninstaller.Views;

public sealed partial class SettingsPage : Page
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeCombo.SelectedIndex = _settings.Theme;
        RestorePointToggle.IsOn = _settings.CreateRestorePoint;
        AutoScanToggle.IsOn = _settings.AutoScan;
        WatchToggle.IsOn = _settings.WatchStartup;
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

    private void Toggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        _settings.CreateRestorePoint = RestorePointToggle.IsOn;
        _settings.AutoScan = AutoScanToggle.IsOn;
        _settings.WatchStartup = WatchToggle.IsOn;
        _settings.Save();
    }
}
