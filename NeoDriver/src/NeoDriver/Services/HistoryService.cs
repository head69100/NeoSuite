using System.Text.Json;
using NeoDriver.Models;

namespace NeoDriver.Services;

/// <summary>Historique des installations, enregistré dans %LocalAppData%\NeoDriver\history.json.</summary>
public static class HistoryService
{
    private static readonly string FilePath = Path.Combine(AppSettings.DataFolder, "history.json");

    public static List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception)
        {
            // Historique illisible : on l'ignore plutôt que de bloquer l'app
        }
        return [];
    }

    public static void Add(IEnumerable<HistoryEntry> entries)
    {
        var all = Load();
        all.InsertRange(0, entries);
        Save(all);
    }

    public static void Save(List<HistoryEntry> entries)
    {
        Directory.CreateDirectory(AppSettings.DataFolder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}
