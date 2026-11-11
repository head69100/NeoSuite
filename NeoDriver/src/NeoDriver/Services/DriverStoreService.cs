using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace NeoDriver.Services;

/// <summary>Un paquet de pilote tiers du magasin de pilotes de Windows (ce que liste « pnputil /enum-drivers »).</summary>
public sealed record StoredDriver(string Published, string Original, string Provider, string ClassName, DateTime? Date, string VersionText)
{
    public Version? ParsedVersion => Version.TryParse(VersionText, out var v) ? v : null;

    public string Display => $"{Provider} · {Original} · {VersionText}" + (Date is { } d ? $" · {d:dd/MM/yyyy}" : "");
}

/// <summary>Un ancien paquet qu'on peut supprimer sans risque : un pilote plus récent est en place et celui-ci n'est utilisé par rien.</summary>
public sealed record CleanupCandidate(StoredDriver Driver, StoredDriver Kept, long Size);

/// <summary>Lecture du magasin de pilotes (sans droits) et nettoyage des anciennes versions (avec).</summary>
public static partial class DriverStoreService
{
    private static string Repository { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");

    public static async Task<List<StoredDriver>> EnumerateAsync()
    {
        var (_, output) = await ConsoleTool.RunAsync("pnputil.exe", "/enum-drivers");
        return Parse(output);
    }

    /// <summary>
    /// Découpe la sortie de pnputil. Les libellés sont traduits (« Nom publié », « Published Name »…) : on se fie à l'ordre
    /// des champs — publié, d'origine, fournisseur, classe, GUID, version — et à la forme de la ligne « date version ».
    /// </summary>
    public static List<StoredDriver> Parse(string text)
    {
        var result = new List<StoredDriver>();
        List<string>? fields = null;

        void Finish()
        {
            if (fields is not { Count: >= 5 })
                return;
            DateTime? date = null;
            var version = "";
            foreach (var value in fields.Skip(4))
            {
                var m = DateAndVersion().Match(value);
                if (!m.Success)
                    continue;
                if (DateTime.TryParseExact($"{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}", "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    date = d;
                version = m.Groups[4].Value;
                break;
            }
            result.Add(new StoredDriver(fields[0], fields[1], fields[2], fields[3], date, version));
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var value = line[(colon + 1)..].Trim();
            if (PublishedName().IsMatch(value))
            {
                Finish();
                fields = [];
            }
            fields?.Add(value);
        }
        Finish();
        return result;
    }

    /// <summary>Paquets utilisés par au moins un périphérique (même absent) : on n'y touche jamais.</summary>
    public static HashSet<string> InUseInfNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT InfName FROM Win32_PnPSignedDriver");
        using var results = searcher.Get();
        foreach (ManagementBaseObject mo in results)
        {
            if (mo["InfName"] is string inf)
                set.Add(inf);
        }
        return set;
    }

    /// <summary>
    /// Anciennes versions : mêmes nom d'origine, fournisseur et classe qu'un paquet plus récent, et utilisées par aucun
    /// périphérique. La version la plus récente de chaque pilote est toujours conservée.
    /// </summary>
    public static async Task<List<CleanupCandidate>> FindCleanupCandidatesAsync()
    {
        var drivers = await EnumerateAsync();
        var inUse = await Task.Run(InUseInfNames);

        return await Task.Run(() =>
        {
            var candidates = new List<CleanupCandidate>();
            var groups = drivers.GroupBy(d => (d.Original.ToLowerInvariant(), d.Provider.ToLowerInvariant(), d.ClassName.ToLowerInvariant()));
            foreach (var group in groups.Where(g => g.Count() > 1))
            {
                var ordered = group.OrderByDescending(d => d.ParsedVersion).ThenByDescending(d => d.Date).ToList();
                var newest = ordered[0];
                foreach (var old in ordered.Skip(1).Where(d => !inUse.Contains(d.Published)))
                    candidates.Add(new CleanupCandidate(old, newest, SizeOf(old)));
            }
            return candidates.OrderByDescending(c => c.Size).ThenBy(c => c.Driver.Original).ToList();
        });
    }

    /// <summary>
    /// Taille du dossier du paquet dans le magasin (FileRepository\nom.inf_amd64_empreinte) : plusieurs versions du même
    /// pilote ont chacune le leur, on retrouve le bon grâce à la ligne DriverVer de son .inf. 0 si introuvable.
    /// </summary>
    private static long SizeOf(StoredDriver driver)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Repository, driver.Original + "_*"))
            {
                var inf = Path.Combine(dir, driver.Original);
                if (!File.Exists(inf))
                    continue;
                var m = DriverVerLine().Match(File.ReadAllText(inf));
                if (m.Success && m.Groups[2].Value == driver.VersionText.Split(' ').LastOrDefault())
                    return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
        }
        catch (Exception)
        {
            // Dossier illisible : taille inconnue
        }
        return 0;
    }

    /// <summary>Supprime ces paquets (droits administrateur). Sans « force » : Windows refuse ceux qu'un périphérique utilise.</summary>
    public static async Task<(ScriptResult Run, Dictionary<string, int> Codes)> DeleteAsync(IEnumerable<string> published)
    {
        var list = string.Join(",", published.Select(ElevatedScript.Quote));
        var script = $$"""
            foreach ($p in @({{list}})) {
                & pnputil.exe /delete-driver $p | Out-Null
                "NEO:RESULT:${p}:$LASTEXITCODE"
            }
            """;
        var run = await ElevatedScript.RunAsync(script);
        var codes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in run.Lines.Where(l => l.StartsWith("NEO:RESULT:")))
        {
            var parts = line["NEO:RESULT:".Length..].Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out var code))
                codes[parts[0]] = code;
        }
        return (run, codes);
    }

    [GeneratedRegex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex PublishedName();

    /// <summary>« 08/21/2025 25.30.0.1 » : date au format américain, puis version.</summary>
    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})/(\d{4})\s+(\S+)$")]
    private static partial Regex DateAndVersion();

    [GeneratedRegex(@"^\s*DriverVer\s*=\s*(\d{1,2}/\d{1,2}/\d{4})\s*,\s*([\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DriverVerLine();
}
