using System.Text.Json;

namespace NeoRar.Services;

/// <summary>Une opération passée, affichée dans l'historique.</summary>
public sealed record HistoryEntry(DateTime When, string Action, string Target, string Detail, bool Ok);

/// <summary>Historique des opérations (200 dernières), enregistré dans %LocalAppData%\NeoRar\history.json.</summary>
public static class HistoryService
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoRar");
    private static readonly string FilePath = Path.Combine(Folder, "history.json");
    private static readonly object Lock = new();

    public static List<HistoryEntry> Entries { get; } = Load();

    /// <summary>Ajoute une opération en tête de liste et l'enregistre sur le disque.</summary>
    public static void Add(string action, string target, string detail, bool ok)
    {
        lock (Lock)
        {
            Entries.Insert(0, new HistoryEntry(DateTime.Now, action, target, detail, ok));
            if (Entries.Count > 200)
                Entries.RemoveRange(200, Entries.Count - 200);
            Save();
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Entries.Clear();
            Save();
        }
    }

    private static List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception)
        {
            // Fichier illisible : on repart d'un historique vide
        }
        return [];
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Disque indisponible : l'historique reste en mémoire pour la session en cours
        }
    }
}
