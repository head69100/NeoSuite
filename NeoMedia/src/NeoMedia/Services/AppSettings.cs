using System.Text.Json;

namespace NeoMedia.Services;

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>Paramètres, enregistrés dans %LocalAppData%\NeoMedia\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoMedia");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; } = Load();

    /// <summary>Volume en % (0 à 150, comme VLC).</summary>
    public int Volume { get; set; } = 80;
    public bool Muted { get; set; }
    public RepeatMode Repeat { get; set; }

    /// <summary>Reprendre une vidéo là où on l'avait arrêtée.</summary>
    public bool ResumePlayback { get; set; } = true;

    /// <summary>Intégration à l'Explorateur voulue (null = pas encore décidé : activée à l'installation).</summary>
    public bool? ShellIntegration { get; set; }

    public List<string> Recent { get; set; } = [];

    /// <summary>Position d'arrêt (ms) des fichiers quittés en cours de lecture.</summary>
    public Dictionary<string, long> ResumePositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddRecent(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > 10)
            Recent.RemoveRange(10, Recent.Count - 10);
    }

    /// <summary>Mémorise où l'on s'est arrêté (ou l'oublie si le fichier a été vu presque en entier).</summary>
    public void SavePosition(string path, long time, long length)
    {
        if (length <= 0)
            return;
        if (time > 30_000 && time < length - 30_000 && length > 120_000)
            ResumePositions[path] = time;
        else
            ResumePositions.Remove(path);
        // Pas de liste sans fin : les 200 plus récentes suffisent
        while (ResumePositions.Count > 200)
            ResumePositions.Remove(ResumePositions.Keys.First());
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                settings.ResumePositions = new Dictionary<string, long>(settings.ResumePositions, StringComparer.OrdinalIgnoreCase);
                return settings;
            }
        }
        catch (Exception)
        {
            // Fichier corrompu : on repart des valeurs par défaut
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // Plusieurs fenêtres NeoMedia peuvent enregistrer en même temps : sans gravité
        }
    }
}
