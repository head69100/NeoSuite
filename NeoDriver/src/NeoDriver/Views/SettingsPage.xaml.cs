using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeoDriver.Services;

namespace NeoDriver.Views;

public sealed partial class SettingsPage : Page
{
    private readonly AppSettings _settings = AppSettings.Current;
    private bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();

        ThemeCombo.SelectedIndex = _settings.Theme;
        RestorePointToggle.IsOn = _settings.CreateRestorePoint;
        ScanOnStartupToggle.IsOn = _settings.ScanOnStartup;
        SignedOnlyToggle.IsOn = _settings.SignedDriversOnly;
        _loaded = true;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3)}";
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
            return;
        _settings.Theme = ThemeCombo.SelectedIndex;
        _settings.Save();
        App.MainWindow?.SetTheme(_settings.Theme switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        });
    }

    private void Toggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        _settings.CreateRestorePoint = RestorePointToggle.IsOn;
        _settings.ScanOnStartup = ScanOnStartupToggle.IsOn;
        _settings.SignedDriversOnly = SignedOnlyToggle.IsOn;
        _settings.Save();
    }
}
