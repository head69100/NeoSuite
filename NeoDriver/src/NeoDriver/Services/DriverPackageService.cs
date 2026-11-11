namespace NeoDriver.Services;

public enum PackageOutcome
{
    /// <summary>Pilote ajouté et installé sur les périphériques concernés.</summary>
    Installed,

    /// <summary>Pilote ajouté à Windows, mais aucun périphérique de ce PC n'en a besoin (ou n'a de pilote moins bon).</summary>
    AddedNoDevice,

    RebootRequired,
    Failed,

    /// <summary>Le point de restauration n'a pas pu être créé : rien n'a été installé.</summary>
    RestoreFailed,

    /// <summary>La confirmation administrateur de Windows a été refusée.</summary>
    Cancelled,
}

public sealed record PackageResult(PackageOutcome Outcome, string Message, string Output);

/// <summary>
/// Installation manuelle de pilotes (fichier .inf ou dossier), sauvegarde et restauration, avec « pnputil » de Windows.
/// Chaque opération demande les droits administrateur (une confirmation Windows, sauf si NeoDriver les a déjà).
/// </summary>
public static class DriverPackageService
{
    /// <summary>Nombre de fichiers .inf d'un dossier et de ses sous-dossiers.</summary>
    public static Task<int> CountInfFilesAsync(string folder) => Task.Run(() =>
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Count();
        }
        catch (Exception)
        {
            return 0;
        }
    });

    /// <summary>Ajoute un fichier .inf, ou tous ceux d'un dossier (sous-dossiers compris), et l'installe sur les périphériques concernés.</summary>
    public static async Task<PackageResult> AddAsync(string path, bool isFolder, bool restorePoint)
    {
        if (isFolder ? !Directory.Exists(path) : !File.Exists(path))
            return new PackageResult(PackageOutcome.Failed, isFolder ? "Ce dossier n'existe plus." : "Ce fichier n'existe plus.", "");

        var target = isFolder ? Path.Combine(path, "*.inf") : path;
        var script = (restorePoint ? ElevatedScript.RestorePointBlock("NeoDriver - installation de pilote") : "")
                     + $"& pnputil.exe /add-driver {ElevatedScript.Quote(target)} {(isFolder ? "/subdirs " : "")}/install\r\n"
                     + "$global:NeoExit = $LASTEXITCODE";
        var run = await ElevatedScript.RunAsync(script);

        if (run.Cancelled)
            return new PackageResult(PackageOutcome.Cancelled, "Sans la confirmation de Windows, NeoDriver ne peut pas installer de pilote.", "");
        if (run.Marker("NEO:RESTORE:FAILED:") is { } restoreError)
            return new PackageResult(PackageOutcome.RestoreFailed, restoreError, run.VisibleOutput);

        return run.ExitCode switch
        {
            0 => new PackageResult(PackageOutcome.Installed, isFolder ? "Les pilotes du dossier ont été installés." : "Le pilote a été installé.", run.VisibleOutput),
            3010 => new PackageResult(PackageOutcome.RebootRequired, "Installé : redémarrez le PC pour terminer.", run.VisibleOutput),
            259 => new PackageResult(PackageOutcome.AddedNoDevice,
                "Le pilote a été ajouté à Windows, mais aucun périphérique de ce PC n'en a besoin (ou son pilote actuel est meilleur).", run.VisibleOutput),
            5 => new PackageResult(PackageOutcome.Failed, "Accès refusé : les droits administrateur sont nécessaires.", run.VisibleOutput),
            2 or 3 => new PackageResult(PackageOutcome.Failed, "Fichier introuvable.", run.VisibleOutput),
            var code => new PackageResult(PackageOutcome.Failed, $"L'installation a échoué (code {code}). Le pilote est peut-être non signé, ou ne convient pas à ce PC.", run.VisibleOutput),
        };
    }

    /// <summary>Exporte tous les pilotes tiers installés dans <paramref name="folder"/> (un sous-dossier par pilote).</summary>
    public static async Task<(bool Success, string Message, int Count, long Size)> BackupAsync(string folder)
    {
        var script = $"""
            [IO.Directory]::CreateDirectory({ElevatedScript.Quote(folder)}) | Out-Null
            & pnputil.exe /export-driver '*' {ElevatedScript.Quote(folder)}
            $global:NeoExit = $LASTEXITCODE
            """;
        // L'export ne fait que copier des fichiers lisibles : on essaie d'abord sans droits (donc sans confirmation Windows)
        var run = await ElevatedScript.RunAsync(script, elevate: false);
        if (run.ExitCode != 0)
            run = await ElevatedScript.RunAsync(script);
        if (run.Cancelled)
            return (false, "Sans la confirmation de Windows, NeoDriver ne peut pas exporter les pilotes.", 0, 0);

        var (count, size) = await Task.Run(() =>
        {
            try
            {
                var dirs = Directory.GetDirectories(folder);
                return (dirs.Length, dirs.Sum(d => new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)));
            }
            catch (Exception)
            {
                return (0, 0L);
            }
        });
        return run.ExitCode == 0 && count > 0
            ? (true, $"{count} pilotes sauvegardés.", count, size)
            : (false, $"La sauvegarde a échoué (code {run.ExitCode}). {run.VisibleOutput}".Trim(), count, size);
    }
}
