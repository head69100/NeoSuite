namespace Neo.Shared;

/// <summary>
/// Une seule instance par application Neo : la relancer (raccourci, menu Démarrer, NeoSuite)
/// réaffiche la fenêtre existante, même cachée dans la barre système.
/// </summary>
public sealed class SingleInstance(string appId)
{
    private readonly string _mutexName = $@"Local\{appId}.SingleInstance";
    private readonly string _showEventName = $@"Local\{appId}.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;

    /// <param name="waitForPrevious">Relance (ex. en administrateur) : on laisse à l'ancienne instance le temps de se fermer.</param>
    public bool TryAcquire(bool waitForPrevious = false)
    {
        try
        {
            _mutex = new Mutex(false, _mutexName);
            return _mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true; // l'instance précédente s'est terminée sans libérer le verrou
        }
        catch (UnauthorizedAccessException)
        {
            // Instance existante en administrateur, inaccessible depuis une instance normale : on démarre quand même
            return true;
        }
    }

    /// <summary>Demande à l'instance déjà ouverte d'afficher sa fenêtre.</summary>
    public void SignalExisting()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(_showEventName);
            ev.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary><paramref name="onShow"/> est appelé sur un thread secondaire : le renvoyer vers le thread UI.</summary>
    public void ListenForShowRequests(Action onShow)
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _showEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                _showEvent.WaitOne();
                onShow();
            }
        })
        {
            IsBackground = true,
            Name = "Neo.SingleInstance",
        };
        thread.Start();
    }
}
