namespace NeoBoost.Services;

/// <summary>Un point mesuré de l'historique de consommation.</summary>
public sealed record HistoryPoint(DateTime Time, double CpuPercent, double RamPercent);

/// <summary>
/// Historique processeur/mémoire des 30 dernières minutes, échantillonné toutes les 5 secondes
/// tant que NeoBoost est ouvert. Permet de voir quand le PC a ralenti.
/// </summary>
public static class UsageHistory
{
    private static readonly object Lock = new();
    private static readonly HistoryPoint[] Buffer = new HistoryPoint[360]; // 30 min toutes les 5 s
    private static int _count;
    private static int _next;
    private static System.Threading.Timer? _timer;

    public static TimeSpan Span { get; } = TimeSpan.FromMinutes(30);

    /// <summary>Démarre l'échantillonnage (une seule fois, sans effet si déjà lancé).</summary>
    public static void Start()
    {
        lock (Lock)
        {
            if (_timer is not null)
                return;
            _ = SystemMonitor.Sample(); // amorçage du calcul de pourcentage processeur
            _timer = new System.Threading.Timer(_ => Record(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    private static void Record()
    {
        try
        {
            var snapshot = SystemMonitor.Sample();
            var point = new HistoryPoint(DateTime.Now, snapshot.CpuPercent, snapshot.RamPercent);
            lock (Lock)
            {
                Buffer[_next] = point;
                _next = (_next + 1) % Buffer.Length;
                if (_count < Buffer.Length)
                    _count++;
            }
        }
        catch (Exception)
        {
            // Jamais de plantage pour un point d'historique
        }
    }

    /// <summary>Points enregistrés, du plus ancien au plus récent, limités à la fenêtre choisie.</summary>
    public static List<HistoryPoint> Snapshot(TimeSpan? window = null)
    {
        lock (Lock)
        {
            var cutoff = DateTime.Now - (window ?? Span);
            var result = new List<HistoryPoint>(_count);
            var start = (_next - _count + Buffer.Length * 2) % Buffer.Length;
            for (var i = 0; i < _count; i++)
            {
                var point = Buffer[(start + i) % Buffer.Length];
                if (point.Time >= cutoff)
                    result.Add(point);
            }
            return result;
        }
    }

    /// <summary>Le pic processeur de la période et son heure.</summary>
    public static (HistoryPoint Point, int Index) Peak(List<HistoryPoint> points)
    {
        if (points.Count == 0)
            return (new HistoryPoint(DateTime.Now, 0, 0), 0);
        var index = 0;
        for (var i = 1; i < points.Count; i++)
            if (points[i].CpuPercent > points[index].CpuPercent)
                index = i;
        return (points[index], index);
    }
}
