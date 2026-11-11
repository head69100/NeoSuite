using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoDriver.Models;
using NeoDriver.Services;
using Windows.ApplicationModel.DataTransfer;

namespace NeoDriver.Views;

public sealed partial class HardwarePage : Page
{
    private List<HardwareSection> _sections = [];

    public HardwarePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (_sections.Count == 0)
            await LoadAsync(refresh: false);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync(refresh: true);

    private async Task LoadAsync(bool refresh)
    {
        RefreshButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        LoadingPanel.Visibility = Visibility.Visible;
        ContentScroll.Visibility = Visibility.Collapsed;

        var selected = SectionBar.SelectedItem?.Text;
        try
        {
            _sections = await HardwareInfoService.GetAsync(refresh);
        }
        catch (Exception ex)
        {
            _sections = [new HardwareSection("Erreur", "")];
            _sections[0].AddGroup("Analyse impossible").Add("Détail", ex.Message);
        }

        SectionBar.Items.Clear();
        foreach (var section in _sections)
            SectionBar.Items.Add(new SelectorBarItem { Text = section.Title, Icon = new FontIcon { Glyph = section.Glyph }, Tag = section });

        SectionBar.SelectedItem = SectionBar.Items.FirstOrDefault(i => i.Text == selected) ?? SectionBar.Items.FirstOrDefault();

        LoadingPanel.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
    }

    private void SectionBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        GroupsPanel.Children.Clear();
        if (sender.SelectedItem?.Tag is not HardwareSection section)
            return;

        foreach (var group in section.Groups)
            GroupsPanel.Children.Add(BuildCard(group));
        ContentScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static Border BuildCard(HardwareGroup group)
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < group.Rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = group.Rows[i].Key,
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
                TextWrapping = TextWrapping.Wrap,
            };
            var value = new TextBlock
            {
                Text = group.Rows[i].Value,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = group.Title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        panel.Children.Add(grid);

        return new Border
        {
            Style = (Style)Application.Current.Resources["CardStyle"],
            Child = panel,
        };
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Rapport matériel NeoDriver · {Environment.MachineName} · {DateTime.Now:dd/MM/yyyy HH:mm}");
        foreach (var section in _sections)
        {
            sb.AppendLine().AppendLine($"=== {section.Title.ToUpperInvariant()} ===");
            foreach (var group in section.Groups)
            {
                sb.AppendLine().AppendLine($"[{group.Title}]");
                foreach (var row in group.Rows)
                    sb.AppendLine($"  {row.Key,-26}{row.Value}");
            }
        }

        var package = new DataPackage();
        package.SetText(sb.ToString());
        Clipboard.SetContent(package);

        CopyText.Text = "Copié !";
        await Task.Delay(1500);
        CopyText.Text = "Copier le rapport";
    }
}
