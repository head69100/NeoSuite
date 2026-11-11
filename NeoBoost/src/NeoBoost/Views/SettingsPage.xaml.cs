using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using NeoBoost.Services;

namespace NeoBoost.Views;

public sealed partial class SettingsPage : Page
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeCombo.SelectedIndex = _settings.Theme;
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
}
