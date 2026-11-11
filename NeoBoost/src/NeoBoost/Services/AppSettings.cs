using System.Text.Json;

namespace NeoBoost.Services;

/// <summary>Paramètres, enregistrés dans %LocalAppData%\NeoBoost\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoBoost");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; } = Load();

    /// <summary>0 = système, 1 = clair, 2 = sombre.</summary>
    public int Theme { get; set; }

    /// <summary>Mode d'alimentation actif avant le mode Boost, rétabli à sa désactivation (null = Boost inactif).</summary>
    public BoostSnapshot? Boost { get; set; }

    /// <summary>État d'origine de chaque réglage Windows modifié par NeoBoost, pour « Tout rétablir ».</summary>
    public Dictionary<string, bool> TweakOriginals { get; set; } = [];

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

public sealed record BoostSnapshot(Guid PreviousScheme, Guid? PreviousOverlay);
