using System.Text.Json;
using Neo.Shared;

namespace NeoSuite.Services;

/// <summary>Paramètres de NeoSuite, enregistrés dans %LocalAppData%\NeoSuite\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoSuite");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; } = Load();

    /// <summary>Raccourci d'ouverture du panneau, au format <see cref="HotKeyGesture.Serialize"/>.</summary>
    public string HotKey { get; set; } = HotKeyGesture.Default.Serialize();

    /// <summary>Délai avant de ranger le panneau quand la souris n'est plus dessus (0 = jamais).</summary>
    public int AutoCloseSeconds { get; set; } = 5;

    /// <summary>La bulle « NeoSuite est prêt » n'est montrée qu'au premier lancement.</summary>
    public bool WelcomeShown { get; set; }

    public HotKeyGesture GetHotKey() => HotKeyGesture.Parse(HotKey);

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
