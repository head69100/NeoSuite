using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>
/// Surveillance en direct : toutes les 45 secondes, compare le démarrage automatique (clés Run, dossiers Démarrage,
/// tâches d'ouverture de session) avec l'état précédent et signale ce qui vient d'être ajouté.
/// Active tant que NeoUninstaller est ouvert.
/// </summary>
public sealed class StartupWatcher : IDisposable
{
    private readonly Timer _timer;
    private HashSet<string>? _known;
    private int _busy;

    /// <summary>Programmes ajoutés depuis la dernière vérification.</summary>
    public event Action<List<StartupItem>>? Added;

    public StartupWatcher()
    {
        _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(45));
    }

    private static string KeyOf(StartupItem i) => $"{i.Source}|{i.Name}";

    private void Check()
    {
        if (!AppSettings.Current.WatchStartup || Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            var items = StartupManager.Load();
            if (_known is null)
            {
                _known = items.Select(KeyOf).ToHashSet();
                return;
            }
            var fresh = items.Where(i => !_known.Contains(KeyOf(i))).ToList();
            foreach (var i in fresh)
                _known.Add(KeyOf(i));
            if (fresh.Count > 0)
                Added?.Invoke(fresh);
        }
        catch (Exception)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}