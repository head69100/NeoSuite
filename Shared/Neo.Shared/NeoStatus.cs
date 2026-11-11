using System.Text.Json;

namespace Neo.Shared;

/// <summary>Dernier état connu d'une appli (« 3 mises à jour »), et quand il a été relevé.</summary>
public sealed record NeoStatusInfo(string Text, DateTime Updated);

/// <summary>
/// État d'une appli, déposé dans %LocalAppData%\Neo\status\&lt;Appli&gt;.json : NeoSuite l'affiche sur la tuile
/// même quand l'appli est fermée (ex. NeoDriver après sa recherche hebdomadaire de pilotes).
/// </summary>
public static class NeoStatus
{
    private static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "status");

    private static string PathFor(string appId) => Path.Combine(Folder, appId + ".json");

    /// <summary>Enregistre l'état (null ou vide : l'efface).</summary>
    public static void Write(string appId, string? text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                File.Delete(PathFor(appId));
                return;
            }
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(appId), JsonSerializer.Serialize(new NeoStatusInfo(text, DateTime.Now)));
        }
        catch (Exception)
        {
            // Un état non enregistré n'a aucune conséquence : l'appli le recalculera
        }
    }

    public static NeoStatusInfo? Read(string appId)
    {
        try
        {
            var path = PathFor(appId);
            return File.Exists(path) ? JsonSerializer.Deserialize<NeoStatusInfo>(File.ReadAllText(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
