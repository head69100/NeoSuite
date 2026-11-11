using System.Text.Json;

namespace NeoRar.Services;

/// <summary>Paramètres, enregistrés dans %LocalAppData%\NeoRar\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoRar");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; } = Load();

    /// <summary>0 = système, 1 = clair, 2 = sombre.</summary>
    public int Theme { get; set; }

    /// <summary>Format et niveau proposés par défaut pour une nouvelle archive.</summary>
    public ArchiveKind DefaultKind { get; set; } = ArchiveKind.SevenZip;
    public int DefaultLevel { get; set; } = 3; // index dans la liste Stockage…Ultra (3 = Normal)

    /// <summary>Intégration à l'Explorateur voulue (null = pas encore décidé : activée au premier lancement de la copie installée).</summary>
    public bool? ShellIntegration { get; set; }

    public List<string> RecentArchives { get; set; } = [];

    /// <summary>Dernier dossier du disque affiché, rouvert au lancement suivant.</summary>
    public string? LastFolder { get; set; }

    public void AddRecent(string path)
    {
        RecentArchives.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentArchives.Insert(0, path);
        if (RecentArchives.Count > 8)
            RecentArchives.RemoveRange(8, RecentArchives.Count - 8);
        Save();
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
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
            // Plusieurs fenêtres NeoRar peuvent enregistrer en même temps : sans gravité
        }
    }
}
