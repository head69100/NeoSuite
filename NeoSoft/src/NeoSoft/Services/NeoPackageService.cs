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
/// ceux de NeoSuite (packages\catalog.json), installés dans Program Files\Neo par NeoSuite en mode « worker ».
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
        if (LoadCatalog().GetValueOrDefault(id) is not { } package || !File.Exists(package.Zip))
            return new(false, "Le paquet d'installation n'est pas disponible.");

        // Copie locale vérifiée : le paquet installé est exactement celui du catalogue
        progress.Report(("Vérification du paquet…", -1));
        var temp = Path.Combine(Path.GetTempPath(), "NeoSoft", $"{id}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            await using (var source = File.OpenRead(package.Zip))
            await using (var target = File.Create(temp))
                await source.CopyToAsync(target, cancel);
            if (package.Sha256 is { } expected)
            {
                await using var stream = File.OpenRead(temp);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
                if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return new(false, "Le paquet est corrompu (empreinte SHA-256 différente).");
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

    private static Dictionary<string, Package> LoadCatalog()
    {
        var result = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        if (NeoSuiteInfo.PackagesCatalog is not { } catalogPath)
            return result;
        try
        {
            var folder = Path.GetDirectoryName(catalogPath)!;
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var e = entry.Value;
                result[entry.Name] = new Package(
                    Path.Combine(folder, $"{entry.Name}.zip"),
                    e.TryGetProperty("version", out var v) ? v.GetString() : null,
                    e.TryGetProperty("sha256", out var s) ? s.GetString() : null);
            }
        }
        catch (Exception)
        {
            // Catalogue illisible : aucune appli Neo proposée
        }
        return result;
    }

    private static bool IsNewer(string? available, string installed) =>
        Version.TryParse(available, out var a) && Version.TryParse(installed, out var i) && a > i;
}
