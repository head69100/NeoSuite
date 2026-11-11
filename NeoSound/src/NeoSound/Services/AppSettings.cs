using System.Text.Json;

namespace NeoSound.Services;

/// <summary>Paramètres, enregistrés dans %LocalAppData%\NeoSound\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoSound");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; } = Load();

    /// <summary>Périphérique dont on capture le son (null = choix automatique au lancement).</summary>
    public string? SourceDeviceId { get; set; }

    public string? Target1DeviceId { get; set; }
    public string? Target2DeviceId { get; set; }

    /// <summary>Latence visée, en millisecondes (10 = réactif, 150 = très sûr). C'est un minimum :
    /// certains pilotes réclament des blocs plus gros, et le moteur s'y adapte.</summary>
    public int LatencyMs { get; set; } = 30;

    /// <summary>Dupliquer aussi vers un second casque.</summary>
    public bool DuplicateToSecond { get; set; } = true;

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
            // Sans gravité : les réglages seront repris la prochaine fois
        }
    }
}
