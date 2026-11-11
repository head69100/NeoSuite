using System.Diagnostics;

namespace NeoMedia.Services;

/// <summary>
/// Journal de diagnostic d'ouverture : %LocalAppData%\NeoMedia\diagnostic.log, une ligne par étape,
/// horodatée par l'âge du processus. Le premier enregistrement indique combien de temps s'est écoulé
/// entre le lancement du .exe et le démarrage réel de l'appli — un « 10 secondes » avant la première
/// ligne désigne le chargement du programme (antivirus, disque), au-delà c'est l'appli elle-même.
/// </summary>
internal static class Diag
{
    private static readonly object Gate = new();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoMedia", "diagnostic.log");

    public static void Log(string message)
    {
        try
        {
            var age = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            lock (Gate)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 512_000)
                    File.WriteAllText(LogPath, "");
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] +{age,8} ms  {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Le diagnostic ne doit jamais gêner la lecture
        }
    }
}
