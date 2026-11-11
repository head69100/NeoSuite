using System.Net.Http.Headers;
using System.Text.Json;

namespace NeoSuite.Services;

/// <summary>Un fichier joint à une release GitHub.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>La dernière release publiée sur GitHub : version de NeoSuite, nouveautés et fichiers joints.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, IReadOnlyList<string> Notes, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) =>
        Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Mises à jour en ligne : la dernière release du dépôt GitHub de NeoSuite.
/// Une release « vX.Y.Z » (X.Y.Z = version de NeoSuite) contient l'installateur (NeoSuite.exe + NeoSuite.pak),
/// le paquet de chaque appli (&lt;App&gt;.zip) et leur catalogue (catalog.json) : voir tools\publish-release.ps1.
/// Dépôt public : aucune clé ni identifiant n'est nécessaire.
/// </summary>
public static class GitHubReleases
{
    public const string Owner = "head69100";
    public const string Repository = "NeoSuite";
    public static string RepositoryUrl => $"https://github.com/{Owner}/{Repository}";

    private static readonly HttpClient Http = CreateClient();
    private static ReleaseInfo? _cached;
    private static DateTime _cachedAt;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>Dernière release (mise en cache 10 minutes) ; null sans connexion ou si aucune release n'est publiée.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(bool force = false)
    {
        await Lock.WaitAsync();
        try
        {
            if (!force && _cached is not null && DateTime.Now - _cachedAt < TimeSpan.FromMinutes(10))
                return _cached;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return _cached; // pas de release, limite d'appels atteinte… on garde l'ancien résultat
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var version))
                return _cached;

            var assets = root.GetProperty("assets").EnumerateArray()
                .Select(a => new ReleaseAsset(
                    a.GetProperty("name").GetString() ?? "",
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.GetProperty("size").GetInt64()))
                .ToList();

            _cached = new ReleaseInfo(Normalize(version), tag, root.GetProperty("html_url").GetString() ?? RepositoryUrl,
                ParseNotes(root.TryGetProperty("body", out var body) ? body.GetString() : null), assets);
            _cachedAt = DateTime.Now;
            return _cached;
        }
        catch (Exception)
        {
            return _cached; // hors ligne : pas de mise à jour en ligne, sans erreur affichée
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>Lit un petit fichier joint (catalog.json) ; null en cas d'échec.</summary>
    public static async Task<string?> DownloadTextAsync(ReleaseAsset asset)
    {
        try
        {
            return await Http.GetStringAsync(asset.Url);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Télécharge un fichier joint vers <paramref name="destination"/>, avec la progression (0 à 1).</summary>
    public static async Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<double>? progress, CancellationToken cancel = default)
    {
        // Déjà téléchargé en entier (relance après une coupure ou un refus UAC) : rien à refaire
        if (File.Exists(destination) && new FileInfo(destination).Length == asset.Size)
        {
            progress?.Report(1);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";
        using (var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = File.Create(partial);
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancel)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0)
                    progress?.Report((double)done / total);
            }
        }
        File.Move(partial, destination, overwrite: true);
    }

    /// <summary>Les puces « - … » du texte de la release (même format que les CHANGELOG.md).</summary>
    private static List<string> ParseNotes(string? body) =>
        (body ?? "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- "))
            .Select(l => l[2..].Trim())
            .ToList();

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        // L'API GitHub refuse les requêtes sans User-Agent
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NeoSuite", UpdateService.CurrentVersion.ToString(3)));
        return client;
    }
}
