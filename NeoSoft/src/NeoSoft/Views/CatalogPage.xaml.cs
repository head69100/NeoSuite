using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NeoSoft.Models;
using NeoSoft.Services;

namespace NeoSoft.Views;

public sealed partial class CatalogPage : Page
{
    private const string AllCategories = "Tous";

    // Vérifié une seule fois par session
    private static bool? _wingetAvailable;

    // Recherche winget complète (hors catalogue) : anti-rebond et annulation des recherches obsolètes
    private readonly List<SoftwareItem> _external = [];
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private CancellationTokenSource? _searchCts;
    private int _searchSeq;

    public CatalogPage()
    {
        InitializeComponent();

        _searchTimer.Tick += SearchTimer_Tick;
        foreach (var category in new[] { AllCategories }.Concat(SoftwareCatalog.Categories))
            CategoryBar.Items.Add(new SelectorBarItem { Text = category, Tag = category });
        CategoryBar.SelectedItem = CategoryBar.Items[0];
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        SoftwareItem.SelectionChanged += OnSelectionChanged;
        ApplyFilter();
        UpdateSelectionBar();

        _wingetAvailable ??= await Task.Run(WingetService.IsAvailable);
        WingetMissing.IsOpen = _wingetAvailable == false;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        SoftwareItem.SelectionChanged -= OnSelectionChanged;
        _searchTimer.Stop();
        _searchCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateSelectionBar();

    private void ApplyFilter()
    {
        var category = CategoryBar.SelectedItem?.Tag as string ?? AllCategories;
        var query = SearchBox.Text.Trim();

        var visible = SoftwareManager.Items.Where(i =>
            (category == AllCategories || i.Category == category)
            && (query.Length == 0
                || i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || i.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || i.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || i.Id.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || i.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();

        // Les paquets winget trouvés par la recherche complète viennent après le catalogue local,
        // uniquement dans la vue « Tous » (ils n'appartiennent à aucune catégorie vérifiée)
        if (category == AllCategories)
            visible.AddRange(_external);

        SoftwareGrid.ItemsSource = visible;
        NoResultText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSelectionBar()
    {
        var count = SoftwareManager.Items.Count(i => i.IsSelected);
        SelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = count == 1 ? "1 logiciel sélectionné" : $"{count} logiciels sélectionnés";
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Une recherche porte sur tout le catalogue
        if (sender.Text.Length > 0 && CategoryBar.SelectedItem != CategoryBar.Items[0])
            CategoryBar.SelectedItem = CategoryBar.Items[0];

        DropExternalResults();
        ApplyFilter();

        // winget n'est interrogé qu'après 450 ms de silence : une frappe lente ne lance pas une recherche par lettre
        _searchTimer.Stop();
        if (sender.Text.Trim().Length >= 2 && _wingetAvailable != false)
            _searchTimer.Start();
    }

    /// <summary>Oublie les résultats winget affichés et invalide toute recherche en vol.</summary>
    private void DropExternalResults()
    {
        _searchSeq++;
        _searchCts?.Cancel(); // arrête winget s'il travaille encore sur l'ancienne requête
        if (_external.Count > 0)
            _external.Clear();
    }

    private async void SearchTimer_Tick(object? sender, object e)
    {
        _searchTimer.Stop();
        await SearchWingetAsync(SearchBox.Text.Trim());
    }

    /// <summary>
    /// Recherche winget complète en arrière-plan, au-delà du catalogue NeoSoft : les paquets inconnus
    /// de la suite sont ajoutés après le catalogue local, avec la mention « Non vérifié par NeoSuite ».
    /// </summary>
    private async Task SearchWingetAsync(string query)
    {
        if (query.Length < 2 || _wingetAvailable == false)
            return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var seq = ++_searchSeq;
        var ct = _searchCts.Token;

        List<WingetService.WingetSearchRow> rows;
        try
        {
            rows = await WingetService.SearchAsync(query, ct);
        }
        catch (Exception)
        {
            // winget absent, interrompu ou en échec : les seuls résultats locaux restent affichés
            return;
        }
        if (seq != _searchSeq || ct.IsCancellationRequested)
            return; // la saisie a changé entre-temps

        var known = new HashSet<string>(SoftwareManager.Items.SelectMany(i => i.AllIds), StringComparer.OrdinalIgnoreCase);
        _external.Clear();
        foreach (var row in rows)
        {
            if (!known.Add(row.Id))
                continue; // déjà couvert par le catalogue local (y compris ses identifiants alternatifs)
            var dot = row.Id.LastIndexOf('.');
            _external.Add(new SoftwareItem
            {
                Id = row.Id,
                Name = row.Name,
                Publisher = dot > 0 ? row.Id[..dot] : "winget",
                Description = $"Paquet winget {row.Id} · version {row.Version}",
                Category = AllCategories,
                Domain = "",
                IsExternal = true,
            });
        }
        ApplyFilter();
    }

    private void CategoryBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (SoftwareGrid is not null)
            ApplyFilter();
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SoftwareItem item)
            return;
        SoftwareManager.Enqueue(item, item.State == SoftwareState.UpdateAvailable ? SoftwareOperation.Upgrade : SoftwareOperation.Install);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SoftwareItem item)
            SoftwareManager.Cancel(item);
    }

    private void InstallSelection_Click(object sender, RoutedEventArgs e)
    {
        SoftwareManager.EnqueueSelection();
        UpdateSelectionBar();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SoftwareManager.Items)
            item.IsSelected = false;
        UpdateSelectionBar();
    }
}
