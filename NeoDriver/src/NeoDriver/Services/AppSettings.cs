using System.Text.Json;

namespace NeoDriver.Services;

/// <summary>Paramètres utilisateur, enregistrés dans %LocalAppData%\NeoDriver\settings.json.</summary>
public sealed class AppSettings
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoDriver");

    private static readonly string FilePath = Path.Combine(DataFolder, "settings.json");

    public static AppSettings Current { get; } = Load();

    /// <summary>0 = système, 1 = clair, 2 = sombre.</summary>
    public int Theme { get; set; }
    public bool CreateRestorePoint { get; set; } = true;
    public bool ScanOnStartup { get; set; }
    public bool SignedDriversOnly { get; set; } = true;

    /// <summary>Pilotes anciens que l'utilisateur ignore (« nom|version ») : leur fabricant n'en propose pas de plus récent.</summary>
    public List<string> IgnoredOldDrivers { get; set; } = [];

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
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
