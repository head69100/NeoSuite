using System.Text.Json;

namespace NeoSoft.Services;

/// <summary>Paramètres, enregistrés dans %LocalAppData%\NeoSoft\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoSoft");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; } = Load();

    /// <summary>0 = système, 1 = clair, 2 = sombre.</summary>
    public int Theme { get; set; }

    /// <summary>Chercher les mises à jour des logiciels installés à l'ouverture.</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

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
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
