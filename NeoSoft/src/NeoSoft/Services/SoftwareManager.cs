using System.Collections.ObjectModel;
using NeoSoft.Models;

namespace NeoSoft.Services;

public enum SoftwareOperation
{
    Install,
    Upgrade,
    Uninstall,
}

/// <summary>Une ligne de l'historique des opérations.</summary>
public sealed record ActivityEntry(DateTime Time, string Name, string Message, bool Success)
{
    public string TimeText => Time.ToString("HH:mm");
    public string Glyph => Success ? "" : "";
}

/// <summary>
/// Catalogue, état d'installation et file d'attente. Toutes les méthodes s'appellent depuis le thread UI :
/// la file s'y exécute aussi, une opération à la fois (winget n'en accepte pas plusieurs en parallèle).
/// </summary>
public static class SoftwareManager
{
    private static readonly List<(SoftwareItem Item, SoftwareOperation Operation)> Pending = [];
    private static bool _running;
    private static SoftwareItem? _currentItem;
    private static CancellationTokenSource? _currentCancel;

    public static List<SoftwareItem> Items { get; } = SoftwareCatalog.Load();

    public static ObservableCollection<ActivityEntry> Activity { get; } = [];

    public static bool IsRefreshing { get; private set; }
    public static DateTime? LastRefresh { get; private set; }

    /// <summary>Déclenché quand l'état d'installation ou la file change (compteurs, pastille NeoSuite…).</summary>
    public static event EventHandler? Changed;

    public static int UpdateCount => Items.Count(i => i.State == SoftwareState.UpdateAvailable
                                                     || (i.IsBusy && i.RestingState == SoftwareState.UpdateAvailable));
    public static int PendingCount => Items.Count(i => i.IsBusy);

    /// <summary>Relit ce qui est installé et les mises à jour disponibles.</summary>
    public static async Task RefreshAsync()
    {
        if (IsRefreshing)
            return;
        IsRefreshing = true;
        Changed?.Invoke(null, EventArgs.Empty);
        try
        {
            var exported = await Task.Run(WingetService.GetExportedAsync);
            var rows = await Task.Run(WingetService.ListAsync);
            var versions = new Dictionary<SoftwareItem, string>();
            var neoStates = await Task.Run(() => Items.Where(i => i.IsNeo).ToDictionary(i => i, i => NeoPackageService.GetState(i.Id)));
            foreach (var item in Items)
            {
                if (item.IsNeo)
                {
                    var neo = neoStates[item];
                    item.InstalledId = neo.InstalledVersion is null ? null : item.Id;
                    if (neo.InstalledVersion is not null)
                        versions[item] = neo.InstalledVersion;
                    continue;
                }

                // 1. Paquet winget reconnu  2. Identifiant dans la liste  3. Nom affiché par Windows
                if (item.AllIds.FirstOrDefault(exported.ContainsKey) is { } exportedId)
                {
                    item.InstalledId = exportedId;
                    versions[item] = exported[exportedId];
                }
                else if (rows.FirstOrDefault(r => item.AllIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase)) is { } byId)
                {
                    item.InstalledId = byId.Id;
                    versions[item] = byId.Version;
                }
                else if (rows.FirstOrDefault(r => item.MatchesDisplayName(r.Name)) is { } byName)
                {
                    item.InstalledId = item.Id; // winget sait le retrouver par son identifiant (« winget upgrade --id »)
                    versions[item] = byName.Version;
                }
                else
                {
                    item.InstalledId = null;
                }
            }

            var ids = Items.Where(i => !i.IsNeo && i.InstalledId is not null).Select(i => i.InstalledId!).ToList();
            var upgrades = ids.Count == 0 ? new Dictionary<string, string>() : await Task.Run(() => WingetService.GetUpgradesAsync(ids));

            foreach (var item in Items)
            {
                item.InstalledVersion = versions.GetValueOrDefault(item);
                item.AvailableVersion = item.IsNeo
                    ? (neoStates[item].UpdateAvailable ? neoStates[item].AvailableVersion ?? "nouvelle version" : null)
                    : item.InstalledId is { } uid ? upgrades.GetValueOrDefault(uid) : null;
                var state = item.InstalledVersion is null ? SoftwareState.NotInstalled
                    : item.AvailableVersion is not null ? SoftwareState.UpdateAvailable
                    : SoftwareState.Installed;
                item.RestingState = state;
                if (!item.IsBusy)
                    item.State = state;
            }
            LastRefresh = DateTime.Now;
        }
        finally
        {
            IsRefreshing = false;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static void Enqueue(SoftwareItem item, SoftwareOperation operation)
    {
        if (item.IsBusy)
            return;
        item.IsSelected = false;
        item.ProgressText = "En attente";
        item.Progress = -1;
        item.State = SoftwareState.Queued;
        Pending.Add((item, operation));
        Changed?.Invoke(null, EventArgs.Empty);
        if (!_running)
            _ = ProcessQueueAsync();
    }

    /// <summary>
    /// Retire le logiciel de la file, ou interrompt son installation en cours
    /// (winget et l'installateur qu'il a lancé sont arrêtés).
    /// </summary>
    public static void Cancel(SoftwareItem item)
    {
        var index = Pending.FindIndex(p => p.Item == item);
        if (index >= 0)
        {
            Pending.RemoveAt(index);
            ResetItem(item);
            Changed?.Invoke(null, EventArgs.Empty);
        }
        else if (_currentItem == item)
        {
            item.ProgressText = "Annulation…";
            _currentCancel?.Cancel();
        }
    }

    public static void CancelAll()
    {
        foreach (var (item, _) in Pending.ToList())
            Cancel(item);
        if (_currentItem is { } current)
            Cancel(current);
    }

    public static void ClearHistory()
    {
        Activity.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void ResetItem(SoftwareItem item)
    {
        item.State = item.RestingState;
        item.ProgressText = "";
        item.Progress = -1;
    }

    /// <summary>Installe ou met à jour tous les logiciels cochés.</summary>
    public static int EnqueueSelection()
    {
        var selected = Items.Where(i => i.IsSelected && i.CanSelect).ToList();
        foreach (var item in selected)
            Enqueue(item, item.State == SoftwareState.UpdateAvailable ? SoftwareOperation.Upgrade : SoftwareOperation.Install);
        return selected.Count;
    }

    public static int EnqueueAllUpdates()
    {
        var updates = Items.Where(i => i.State == SoftwareState.UpdateAvailable).ToList();
        foreach (var item in updates)
            Enqueue(item, SoftwareOperation.Upgrade);
        return updates.Count;
    }

    private static async Task ProcessQueueAsync()
    {
        _running = true;
        try
        {
            while (Pending.Count > 0)
            {
                var (item, operation) = Pending[0];
                Pending.RemoveAt(0);
                using var cancel = new CancellationTokenSource();
                _currentItem = item;
                _currentCancel = cancel;
                item.State = SoftwareState.Working;
                item.ProgressText = "Préparation…";
                // Créé sur le thread UI : les rapports de progression y reviennent
                var progress = new Progress<(string Text, double Percent)>(p =>
                {
                    item.ProgressText = p.Text;
                    item.Progress = p.Percent;
                });

                WingetResult result;
                var cancelled = false;
                try
                {
                    result = item.IsNeo ? operation switch
                    {
                        SoftwareOperation.Uninstall => await NeoPackageService.UninstallAsync(item.Id, progress, cancel.Token),
                        _ => await NeoPackageService.InstallAsync(item.Id, item.Name, progress, cancel.Token), // une mise à jour réinstalle
                    } : operation switch
                    {
                        SoftwareOperation.Upgrade => await WingetService.UpgradeAsync(item.InstalledId ?? item.Id, progress, cancel.Token),
                        SoftwareOperation.Uninstall => await WingetService.UninstallAsync(item.InstalledId ?? item.Id, progress, cancel.Token),
                        _ => await WingetService.InstallAsync(item.Id, progress, cancel.Token),
                    };
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    result = new WingetResult(false, "annulée.");
                }
                catch (Exception ex)
                {
                    result = new WingetResult(false, ex.Message);
                }
                finally
                {
                    _currentItem = null;
                    _currentCancel = null;
                }

                if (result.Success)
                {
                    item.RestingState = operation == SoftwareOperation.Uninstall ? SoftwareState.NotInstalled : SoftwareState.Installed;
                    if (operation == SoftwareOperation.Install)
                        item.InstalledId = item.Id;
                    if (operation == SoftwareOperation.Uninstall)
                    {
                        item.InstalledVersion = null;
                        item.InstalledId = null;
                    }
                    else if (item.AvailableVersion is not null)
                        item.InstalledVersion = item.AvailableVersion;
                    item.AvailableVersion = null;
                }
                ResetItem(item);

                var verb = operation switch
                {
                    SoftwareOperation.Upgrade => "Mise à jour",
                    SoftwareOperation.Uninstall => "Désinstallation",
                    _ => "Installation",
                };
                var message = result.Success ? $"{verb} réussie. {result.Message}"
                    : cancelled ? $"{verb} annulée."
                    : $"{verb} échouée : {result.Message}";
                Activity.Insert(0, new ActivityEntry(DateTime.Now, item.Name, message, result.Success));
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }
        finally
        {
            _running = false;
        }

        // Les versions installées ont changé : on relit l'état réel
        await RefreshAsync();
    }
}
