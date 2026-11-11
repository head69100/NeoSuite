using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Neo.Shared;

namespace NeoSoft.Services;

/// <summary>État d'une appli Neo sur ce PC.</summary>
public sealed record NeoPackageState(string? InstalledVersion, string? AvailableVersion, bool UpdateAvailable, bool PackageAvailable);

/// <summary>
/// Applis de la suite Neo proposées dans NeoSoft (NeoRar…). Elles ne sont pas dans winget : leurs paquets sont
/// ceux de NeoSuite (dernière release GitHub, ou catalogue local), installés dans Program Files\Neo par NeoSuite
/// en mode « worker ».
/// </summary>
public static class NeoPackageService
{
    private sealed record Package(string Zip, string? Version, string? Sha256);

    private static string InstallDir(string id) => ElevatedLaunch.InstallDir(id);
    private static string InstalledExe(string id) => Path.Combine(InstallDir(id), $"{id}.exe");

    public static NeoPackageState GetState(string id)
    {
        var package = LoadCatalog().GetValueOrDefault(id);
        var exe = InstalledExe(id);
        string? installed = null;
        string? installedSha = null;
        if (File.Exists(exe))
        {
            installed = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] ?? "";
            var hashFile = Path.Combine(InstallDir(id), "neo-package.sha256");
            installedSha = File.Exists(hashFile) ? File.ReadAllText(hashFile).Trim() : null;
        }

        var update = installed is not null && package is not null
            && (IsNewer(package.Version, installed)
                || (package.Version == installed && package.Sha256 is not null && !string.Equals(package.Sha256, installedSha, StringComparison.OrdinalIgnoreCase)));
        return new NeoPackageState(installed, update ? package!.Version : null, update, package is not null);
    }

    public static async Task<WingetResult> InstallAsync(string id, string name, IProgress<(string Text, double Percent)> progress, CancellationToken cancel)
    {
        if (NeoSuiteInfo.SuiteExe is not { } suite)
            return new(false, "NeoSuite est introuvable : ouvrez NeoSuite une fois, puis réessayez.");
        if (await Task.Run(() => LoadCatalog().GetValueOrDefault(id), cancel) is not { } package
            || (!IsUrl(package.Zip) && !File.Exists(package.Zip)))
            return new(false, "Le paquet d'installation n'est pas disponible (connexion à Internet nécessaire).");

        // Copie locale vérifiée (téléchargée depuis GitHub, ou copiée du PC) : le paquet installé est exactement
        // celui du catalogue
        var temp = Path.Combine(Path.GetTempPath(), "NeoSoft", $"{id}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            if (IsUrl(package.Zip))
            {
                progress.Report(("Téléchargement…", 0));
                using var response = await Http.Value.GetAsync(package.Zip, HttpCompletionOption.ResponseHeadersRead, cancel);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var target = File.Create(temp);
                var buffer = new byte[1 << 20];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    copied += read;
                    if (total > 0)
                        progress.Report(($"Téléchargement… {copied * 100 / total} %", copied * 100.0 / total));
                }
            }
            else
            {
                progress.Report(("Vérification du paquet…", -1));
                await using var source = File.OpenRead(package.Zip);
                await using var target = File.Create(temp);
                await source.CopyToAsync(target, cancel);
            }

            if (package.Sha256 is { } expected)
            {
                progress.Report(("Vérification du paquet…", -1));
                await using var stream = File.OpenRead(temp);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
                if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return new(false, "Le paquet téléchargé est corrompu ou vient d'être remplacé (empreinte SHA-256 différente) : réessayez.");
            }

            progress.Report(("Installation…", -1));
            return await RunWorkerAsync(suite, $"--install-worker {id} \"{name}\" \"{temp}\"", "installé", cancel);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    public static Task<WingetResult> UninstallAsync(string id, IProgress<(string Text, double Percent)> progress, CancellationToken cancel)
    {
        if (NeoSuiteInfo.SuiteExe is not { } suite)
            return Task.FromResult(new WingetResult(false, "NeoSuite est introuvable : ouvrez NeoSuite une fois, puis réessayez."));
        progress.Report(("Désinstallation…", -1));
        return RunWorkerAsync(suite, $"--uninstall-worker {id}", "désinstallé", cancel);
    }

    /// <summary>NeoSuite en administrateur (une confirmation Windows) pour écrire dans Program Files.</summary>
    private static async Task<WingetResult> RunWorkerAsync(string suite, string arguments, string doneVerb, CancellationToken cancel)
    {
        var resultFile = Path.Combine(Path.GetTempPath(), "NeoSoft", $"result-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(suite, $"{arguments} \"{resultFile}\"") { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, "droits administrateur refusés.");
        }
        if (process is null)
            return new(false, "impossible de lancer NeoSuite.");

        using (process)
            await process.WaitForExitAsync(cancel);

        var result = File.Exists(resultFile) ? await File.ReadAllTextAsync(resultFile, CancellationToken.None) : null;
        try { File.Delete(resultFile); } catch (IOException) { }
        return result == "OK" ? new(true, $"Logiciel {doneVerb}.")
            : new(false, result?.StartsWith("ERR:") == true ? result[4..] : "l'opération a échoué.");
    }

    /// <summary>
    /// Paquets disponibles : ceux du PC (catalogue local de NeoSuite, s'il en a un) complétés par ceux de la
    /// dernière release GitHub, retenus quand ils sont plus récents.
    /// </summary>
    private static Dictionary<string, Package> LoadCatalog()
    {
        var result = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        if (NeoSuiteInfo.PackagesCatalog is { } catalogPath)
        {
            try
            {
                var folder = Path.GetDirectoryName(catalogPath)!;
                foreach (var (name, version, sha) in ParseCatalog(File.ReadAllText(catalogPath)))
                    result[name] = new Package(Path.Combine(folder, $"{name}.zip"), version, sha);
            }
            catch (Exception)
            {
                // Catalogue local illisible : on s'en tient à celui en ligne
            }
        }

        foreach (var (name, package) in LoadOnlineCatalog())
        {
            if (!result.TryGetValue(name, out var local) || IsNewer(package.Version, local.Version ?? "0.0"))
                result[name] = package;
        }
        return result;
    }

    private static IEnumerable<(string Name, string? Version, string? Sha256)> ParseCatalog(string json)
    {
        // catalog.json est écrit avec une marque d'encodage (BOM)
        using var doc = JsonDocument.Parse(json.TrimStart('﻿'));
        return doc.RootElement.EnumerateObject()
            .Select(e => (e.Name,
                e.Value.TryGetProperty("version", out var v) ? v.GetString() : null,
                e.Value.TryGetProperty("sha256", out var s) ? s.GetString() : null))
            .ToList();
    }

    // ---- Catalogue en ligne : dernière release du dépôt public head69100/NeoSuite ----

    private const string LatestRelease = "https://github.com/head69100/NeoSuite/releases/latest/download/";
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NeoSoft");
        return http;
    });
    private static readonly object OnlineLock = new();
    private static Dictionary<string, Package> _online = [];
    private static DateTime _onlineAt;

    /// <summary>
    /// catalog.json de la dernière release (relu au plus toutes les 10 minutes ; vide sans connexion).
    /// Appelé hors du thread de l'interface. Si une release sort entre la lecture du catalogue et le téléchargement,
    /// l'empreinte SHA-256 ne correspond plus et l'installation demande de réessayer.
    /// </summary>
    private static Dictionary<string, Package> LoadOnlineCatalog()
    {
        lock (OnlineLock)
        {
            if (DateTime.Now - _onlineAt < TimeSpan.FromMinutes(10))
                return _online;
            _onlineAt = DateTime.Now;
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = Http.Value.GetAsync(LatestRelease + "catalog.json", cancel.Token).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var json = response.Content.ReadAsStringAsync(cancel.Token).GetAwaiter().GetResult();
                _online = ParseCatalog(json).ToDictionary(p => p.Name, p => new Package(LatestRelease + $"{p.Name}.zip", p.Version, p.Sha256), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Pas de connexion ou release illisible : on réessaiera dans une minute
                _onlineAt = DateTime.Now - TimeSpan.FromMinutes(9);
            }
            return _online;
        }
    }

    private static bool IsUrl(string source) => source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsNewer(string? available, string installed) =>
        Version.TryParse(available, out var a) && Version.TryParse(installed, out var i) && a > i;
}
