using System.Text.Json;

namespace NeoUninstaller.Services;

/// <summary>Paramètres, enregistrés dans %LocalAppData%\NeoUninstaller\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoUninstaller");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; } = Load();

    /// <summary>0 = système, 1 = clair, 2 = sombre.</summary>
    public int Theme { get; set; }
    public bool CreateRestorePoint { get; set; } = true;
    /// <summary>Après la désinstallation, lancer automatiquement l'analyse des restes.</summary>
    public bool AutoScan { get; set; } = true;
    /// <summary>Signaler les programmes qui s'ajoutent au démarrage de Windows pendant que NeoUninstaller est ouvert.</summary>
    public bool WatchStartup { get; set; } = true;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
