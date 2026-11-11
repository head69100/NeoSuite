using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeoSoft.Services;

/// <summary>Résultat d'une opération winget.</summary>
public sealed record WingetResult(bool Success, string Message);

/// <summary>
/// Pilote winget, le gestionnaire de paquets de Windows : chaque logiciel est téléchargé depuis
/// la source officielle de son éditeur, son empreinte vérifiée, puis installé en silence.
/// </summary>
public static partial class WingetService
{
    private const string CommonArgs = "--accept-source-agreements --disable-interactivity";

    public static bool IsAvailable()
    {
        try
        {
            return RunAsync("--version").GetAwaiter().GetResult().ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Une ligne de « winget list » : tout programme installé, connu de winget ou non (Id « ARP\… »).</summary>
    public sealed record InstalledRow(string Name, string Id, string Version);

    /// <summary>
    /// Paquets winget reconnus par l'export : identifiant → version. Fiable, mais ne voit pas les programmes
    /// que winget n'a pas su rattacher à un paquet (ex. Chrome ou Discord installés depuis leur site).
    /// </summary>
    public static async Task<Dictionary<string, string>> GetExportedAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(Path.GetTempPath(), $"neosoft-{Guid.NewGuid():N}.json");
        try
        {
            await RunAsync($"export -o \"{file}\" --source winget --include-versions {CommonArgs}");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                foreach (var source in doc.RootElement.GetProperty("Sources").EnumerateArray())
                {
                    foreach (var package in source.GetProperty("Packages").EnumerateArray())
                    {
                        var id = package.GetProperty("PackageIdentifier").GetString();
                        if (id is not null)
                            result[id] = package.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
                    }
                }
            }
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
        return result;
    }

    /// <summary>Tous les programmes installés (tableau de « winget list », découpé selon les colonnes de l'en-tête).</summary>
    public static async Task<List<InstalledRow>> ListAsync()
    {
        var (_, output) = await RunAsync($"list {CommonArgs}");
        var rows = new List<InstalledRow>();
        foreach (var cells in ParseTable(output))
        {
            var id = cells[1];
            if (id.Length > 0)
                rows.Add(new InstalledRow(cells[0], id, cells[2]));
        }
        return rows;
    }

    /// <summary>Une ligne de « winget search » : nom, identifiant et version d'un paquet du dépôt.</summary>
    public sealed record WingetSearchRow(string Name, string Id, string Version);

    /// <summary>
    /// Recherche complète dans tout le dépôt winget, au-delà du catalogue NeoSoft. Ces résultats ne sont
    /// pas vérifiés par NeoSuite : l'interface doit le signaler à l'utilisateur.
    /// </summary>
    public static async Task<List<WingetSearchRow>> SearchAsync(string query, CancellationToken cancel)
    {
        query = query.Trim().Replace("\"", "");
        if (query.Length == 0)
            return [];
        var (_, output) = await RunAsync($"search \"{query}\" --source winget --count 30 {CommonArgs}", cancel);
        var rows = new List<WingetSearchRow>();
        foreach (var cells in ParseTable(output))
        {
            // Les lignes d'information n'ont pas d'identifiant contenant un point (ex. « VideoLAN.VLC »)
            if (cells[0].Length > 0 && cells[1].Contains('.'))
                rows.Add(new WingetSearchRow(cells[0], cells[1], cells[2]));
        }
        return rows;
    }

    /// <summary>
    /// Découpe la table d'une sortie winget (en-tête localisé Nom/Name/ID…, ligne « ---- », puis les lignes) :
    /// seules les positions des colonnes de l'en-tête comptent, quel que soit le langage de winget.
    /// </summary>
    private static List<string[]> ParseTable(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var rows = new List<string[]>();
        var separator = lines.FindIndex(l => l.StartsWith("-----"));
        if (separator < 1)
            return rows;

        var header = lines[separator - 1];
        var starts = Regex.Matches(header, @"(?<=^|\s)\S").Select(m => m.Index).ToList();
        if (starts.Count < 3)
            return rows;

        foreach (var line in lines.Skip(separator + 1).Where(l => l.Trim().Length > 0))
        {
            var cells = new string[starts.Count];
            for (var i = 0; i < starts.Count; i++)
            {
                var start = starts[i];
                if (start >= line.Length)
                {
                    cells[i] = "";
                    continue;
                }
                var end = i + 1 < starts.Count ? Math.Min(starts[i + 1], line.Length) : line.Length;
                cells[i] = line[start..end].Trim();
            }
            rows.Add(cells);
        }
        return rows;
    }

    /// <summary>Mises à jour disponibles pour les identifiants demandés : identifiant → nouvelle version.</summary>
    public static async Task<Dictionary<string, string>> GetUpgradesAsync(IEnumerable<string> ids)
    {
        var (_, output) = await RunAsync($"upgrade --include-unknown {CommonArgs}");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            // Colonnes : Nom  Id  Version  Disponible
            var match = Regex.Match(output, $@"\s{Regex.Escape(id)}\s+(?:<\s)?\S+\s+(?<available>\S+)", RegexOptions.IgnoreCase);
            if (match.Success)
                result[id] = match.Groups["available"].Value;
        }
        return result;
    }

    public static Task<WingetResult> InstallAsync(string id, IProgress<(string Text, double Percent)> progress, CancellationToken cancel) =>
        RunOperationAsync($"install --id {id} -e --source winget --silent --accept-package-agreements {CommonArgs}", progress, "installé", cancel);

    public static Task<WingetResult> UpgradeAsync(string id, IProgress<(string Text, double Percent)> progress, CancellationToken cancel) =>
        RunOperationAsync($"upgrade --id {id} -e --silent --accept-package-agreements {CommonArgs}", progress, "mis à jour", cancel);

    public static Task<WingetResult> UninstallAsync(string id, IProgress<(string Text, double Percent)> progress, CancellationToken cancel) =>
        RunOperationAsync($"uninstall --id {id} -e --silent {CommonArgs}", progress, "désinstallé", cancel);

    private static async Task<WingetResult> RunOperationAsync(string arguments, IProgress<(string Text, double Percent)> progress, string doneVerb, CancellationToken cancel)
    {
        var lastLine = "";
        var (exitCode, _) = await RunAsync(arguments, cancel, line =>
        {
            var text = line.Trim();
            if (text.Length == 0)
                return;

            // Barre de progression : « ██████   12.5 MB / 50.0 MB » ou « 45% »
            var sizes = SizeProgress().Match(text);
            if (sizes.Success && ParseSize(sizes.Groups[1].Value, sizes.Groups[2].Value) is { } done
                              && ParseSize(sizes.Groups[3].Value, sizes.Groups[4].Value) is { } total && total > 0)
            {
                progress.Report(($"Téléchargement… {sizes.Groups[1].Value} {sizes.Groups[2].Value} / {sizes.Groups[3].Value} {sizes.Groups[4].Value}", done * 100 / total));
                return;
            }
            var percent = PercentProgress().Match(text);
            if (percent.Success && double.TryParse(percent.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p))
            {
                progress.Report(("En cours…", p));
                return;
            }
            if (text.Any(char.IsLetter))
            {
                lastLine = text;
                progress.Report((Translate(text), -1));
            }
        });

        return exitCode switch
        {
            0 => new(true, $"Logiciel {doneVerb}."),
            unchecked((int)0x8A15002B) => new(true, "Déjà à jour."),               // APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE
            unchecked((int)0x8A150061) => new(true, "Déjà installé."),             // APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED
            unchecked((int)0x8A150014) => new(false, "Logiciel introuvable dans winget."),
            unchecked((int)0x8A150011) => new(false, "Le téléchargement a échoué : vérifiez la connexion Internet."),
            unchecked((int)0x8A150010) => new(false, "Le fichier téléchargé ne correspond pas à l'empreinte attendue : installation bloquée par sécurité."),
            unchecked((int)0x8A150109) => new(true, "Installé : un redémarrage du PC est nécessaire pour terminer."),
            1602 or 1223 or unchecked((int)0x8A150056) => new(false, "Installation annulée (droits administrateur refusés ?)."),
            _ => new(false, string.IsNullOrEmpty(lastLine) ? $"Échec (code {exitCode:X8})." : $"{Translate(lastLine)} (code {exitCode:X8})"),
        };
    }

    /// <summary>Traduit les étapes affichées par winget (qui peut répondre en anglais).</summary>
    private static string Translate(string line) => line switch
    {
        _ when line.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase) => "Téléchargement…",
        _ when line.StartsWith("Successfully verified", StringComparison.OrdinalIgnoreCase) => "Fichier vérifié",
        _ when line.StartsWith("Starting package install", StringComparison.OrdinalIgnoreCase) => "Installation…",
        _ when line.StartsWith("Starting package uninstall", StringComparison.OrdinalIgnoreCase) => "Désinstallation…",
        _ when line.StartsWith("Successfully installed", StringComparison.OrdinalIgnoreCase) => "Installé",
        _ when line.StartsWith("Found", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Trouvé", StringComparison.OrdinalIgnoreCase) => "Préparation…",
        _ when line.Contains("licensed to you", StringComparison.OrdinalIgnoreCase) => "Préparation…",
        _ => line.Length > 90 ? line[..90] + "…" : line,
    };

    private static double? ParseSize(string value, string unit)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return null;
        return unit.ToUpperInvariant() switch
        {
            "KB" => v * 1024,
            "MB" => v * 1024 * 1024,
            "GB" => v * 1024 * 1024 * 1024,
            _ => v,
        };
    }

    [GeneratedRegex(@"([\d.]+)\s*(KB|MB|GB|B)\s*/\s*([\d.]+)\s*(KB|MB|GB|B)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeProgress();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%")]
    private static partial Regex PercentProgress();

    private static Task<(int ExitCode, string Output)> RunAsync(string arguments) => RunAsync(arguments, CancellationToken.None);

    /// <summary>
    /// Lance winget. Une annulation arrête winget et l'installateur qu'il a lancé (arbre de processus),
    /// puis lève <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunAsync(string arguments, CancellationToken cancel, Action<string>? onLine = null)
    {
        cancel.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo("winget", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de lancer winget.");
        using var killOnCancel = cancel.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Déjà terminé
            }
        });
        var output = new StringBuilder();
        var stderr = process.StandardError.ReadToEndAsync();

        // Lecture caractère par caractère : winget redessine sa barre de progression avec \r
        var reader = process.StandardOutput;
        var line = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        var text = line.ToString();
                        output.AppendLine(text);
                        onLine?.Invoke(text);
                        line.Clear();
                    }
                }
                else
                {
                    line.Append(c);
                }
            }
        }
        if (line.Length > 0)
        {
            output.AppendLine(line.ToString());
            onLine?.Invoke(line.ToString());
        }

        await stderr;
        await process.WaitForExitAsync();
        cancel.ThrowIfCancellationRequested();
        return (process.ExitCode, output.ToString());
    }
}
