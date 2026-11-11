using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using NeoBoost.Models;
using NeoBoost.Services;

namespace NeoBoost.Views;

public sealed partial class ProcessesPage : Page
{
    // Conservé entre deux visites de la page : la mesure du processeur se fait entre deux échantillons
    private static readonly ProcessMonitor Monitor = new();

    private readonly ObservableCollection<ProcessGroup> _items = [];
    private readonly DispatcherQueueTimer _timer;
    private List<ProcessGroup> _all = [];
    private bool _sampling;
    private bool _pointerOverList;

    public ProcessesPage()
    {
        InitializeComponent();
        ProcessList.ItemsSource = _items;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _timer.Start();
        await RefreshAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    private async Task RefreshAsync()
    {
        if (_sampling)
            return;
        _sampling = true;
        try
        {
            var measures = await Task.Run(Monitor.Sample);
            _all = Monitor.Apply(measures);
            ShowList(reorder: !_pointerOverList);
        }
        finally
        {
            _sampling = false;
        }
    }

    /// <summary>
    /// Met la liste affichée à jour sans la recréer. Quand la souris est dessus, l'ordre est figé
    /// pour que la ligne visée ne se dérobe pas au moment du clic.
    /// </summary>
    private void ShowList(bool reorder = true)
    {
        var query = SearchBox.Text.Trim();
        IEnumerable<ProcessGroup> visible = _all;
        if (ShowSystemCheck.IsChecked != true)
            visible = visible.Where(g => !g.IsProtected);
        if (query.Length > 0)
            visible = visible.Where(g => g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                      || g.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        visible = SortCombo.SelectedIndex switch
        {
            1 => visible.OrderByDescending(g => g.Memory),
            2 => visible.OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => visible.OrderByDescending(g => g.CpuPercent).ThenByDescending(g => g.Memory),
        };
        var target = visible.ToList();
        var wanted = target.ToHashSet();

        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(_items[i]))
                _items.RemoveAt(i);
        }

        if (!reorder)
        {
            foreach (var g in target.Where(g => !_items.Contains(g)))
                _items.Add(g);
            return;
        }

        for (var i = 0; i < target.Count; i++)
        {
            var current = _items.IndexOf(target[i]);
            if (current == -1)
                _items.Insert(i, target[i]);
            else if (current != i)
                _items.Move(current, i);
        }
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProcessGroup group)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Fermer {group.DisplayName} ?",
            Content = new TextBlock
            {
                Text = "NeoBoost lui demande d'abord de se fermer normalement. S'il ne répond pas, il sera arrêté de force : " +
                       "les modifications non enregistrées seraient alors perdues.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Fermer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (sender is Button button)
            button.IsEnabled = false;
        var remaining = await ProcessMonitor.CloseAsync(group.Name);

        ResultBar.Severity = remaining == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Title = remaining == 0 ? $"{group.DisplayName} est fermé" : $"{group.DisplayName} n'a pas pu être fermé";
        ResultBar.Message = remaining == 0 ? ""
            : AdminService.IsAdministrator ? "Windows a refusé l'arrêt de ce programme."
            : "Il a sans doute été lancé en administrateur : ouvrez NeoBoost en administrateur pour le fermer.";
        ResultBar.IsOpen = true;
        await RefreshAsync();
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ShowList();

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessList is not null)
            ShowList();
    }

    private void ShowSystem_Changed(object sender, RoutedEventArgs e) => ShowList();

    private void ProcessList_PointerEntered(object sender, PointerRoutedEventArgs e) => _pointerOverList = true;

    private void ProcessList_PointerExited(object sender, PointerRoutedEventArgs e) => _pointerOverList = false;
}
