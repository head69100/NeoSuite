using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using NeoSuite.Models;

namespace NeoSuite.Services;

/// <summary>
/// Téléchargement et (dés)installation des applis Neo. Le téléchargement se fait sans droits ;
/// l'écriture dans Program Files passe par <see cref="InstallWorker"/>, lancé en administrateur (UAC).
/// </summary>
public static class AppInstaller
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "NeoSuite");

    /// <summary>Télécharge puis installe (ou met à jour) l'appli. Les changements d'état sont faits sur le thread appelant.</summary>
    public static async Task InstallAsync(NeoApp app)
    {
        if (app.PackageSource is null)
            throw new InvalidOperationException($"Aucun paquet d'installation n'est disponible pour {app.Name}.");

        Directory.CreateDirectory(TempDir);
        var zip = Path.Combine(TempDir, $"{app.Id}.zip");
        try
        {
            app.Progress = 0;
            app.Operation = AppOperation.Downloading;
            var progress = new Progress<double>(p => app.Progress = p);
            await DownloadAsync(app.PackageSource, zip, progress);
            await VerifyAsync(zip, app.PackageSha256);

            app.Operation = AppOperation.Installing;
            await RunWorkerAsync(InstallWorker.InstallArg, app.Id, Quote(app.Name), Quote(zip));
        }
        finally
        {
            TryDelete(zip);
            AppCatalog.Refresh(app);
            app.Operation = AppOperation.None;
        }
    }

    public static async Task UninstallAsync(NeoApp app, bool removeData)
    {
        try
        {
            app.Operation = AppOperation.Uninstalling;
            await RunWorkerAsync(InstallWorker.UninstallArg, app.Id, removeData ? InstallWorker.RemoveDataArg : null);
        }
        finally
        {
            AppCatalog.Refresh(app);
            app.Operation = AppOperation.None;
        }
    }

    /// <summary>Active ou retire le démarrage en administrateur sans UAC de l'appli (une confirmation UAC).</summary>
    public static async Task SetRunAsAdminAsync(NeoApp app, bool enable)
    {
        Directory.CreateDirectory(TempDir);
        await RunWorkerAsync(InstallWorker.AdminArg, app.Id, enable ? "on" : "off");
    }

    /// <summary>Copie le paquet avec la progression. Accepte un chemin local ou une adresse http(s).</summary>
    private static async Task DownloadAsync(string source, string destination, IProgress<double> progress)
    {
        Stream input;
        long total;
        HttpClient? http = null;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            // Paquet de la release GitHub (plusieurs dizaines de Mo) : délai large, et un nom de client comme le veut GitHub
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"NeoSuite/{UpdateService.CurrentVersion.ToString(3)}");
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            total = response.Content.Headers.ContentLength ?? 0;
            input = await response.Content.ReadAsStreamAsync();
        }
        else
        {
            input = File.OpenRead(source);
            total = input.Length;
        }

        try
        {
            await using var output = File.Create(destination);
            var buffer = new byte[1 << 20];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                copied += read;
                if (total > 0)
                    progress.Report(copied * 100.0 / total);
            }
        }
        finally
        {
            await input.DisposeAsync();
            http?.Dispose();
        }
    }

    private static async Task VerifyAsync(string file, string? expectedSha256)
    {
        if (string.IsNullOrEmpty(expectedSha256))
            return;
        await using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Le paquet téléchargé est corrompu (empreinte SHA-256 différente). Réessayez.");
    }

    /// <summary>Relance NeoSuite en administrateur pour la partie « Program Files » et attend son résultat.</summary>
    private static async Task RunWorkerAsync(string mode, params string?[] args)
    {
        var resultFile = Path.Combine(TempDir, $"result-{Guid.NewGuid():N}.txt");
        var arguments = string.Join(' ', new[] { mode }.Concat(args).Where(a => a is not null)) + $" {Quote(resultFile)}";

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Opération annulée : les droits administrateur ont été refusés.");
        }

        if (process is null)
            throw new InvalidOperationException("Impossible de lancer l'installation.");
        using (process)
            await process.WaitForExitAsync();

        var result = File.Exists(resultFile) ? await File.ReadAllTextAsync(resultFile) : null;
        TryDelete(resultFile);
        if (result != "OK")
            throw new InvalidOperationException(result?.StartsWith("ERR:") == true ? result[4..] : "L'opération a échoué.");
    }

    private static string Quote(string arg) => $"\"{arg}\"";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
