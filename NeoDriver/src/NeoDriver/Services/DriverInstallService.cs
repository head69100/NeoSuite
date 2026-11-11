using System.Runtime.InteropServices;
using NeoDriver.Models;

namespace NeoDriver.Services;

public sealed record InstallProgress(DriverItem Item, DriverState State, string? Error = null);

public sealed record InstallOutcome(DriverItem Item, bool Success, string Message);

public sealed record InstallSummary(IReadOnlyList<InstallOutcome> Outcomes, bool RebootRequired)
{
    public int Succeeded => Outcomes.Count(o => o.Success);
    public int Failed => Outcomes.Count(o => !o.Success);
}

/// <summary>Téléchargement et installation des pilotes via Windows Update (droits administrateur requis).</summary>
public static class DriverInstallService
{
    private const int ResultSucceeded = 2;
    private const int ResultSucceededWithErrors = 3;

    /// <summary>Pilotes dont la licence n'a pas encore été acceptée, avec le texte de la licence.</summary>
    public static Task<List<(DriverItem Item, string Text)>> GetPendingEulasAsync(IEnumerable<DriverItem> items) => Task.Run(() =>
    {
        var pending = new List<(DriverItem, string)>();
        foreach (var item in items)
        {
            dynamic update = item.Update!;
            if (!(bool)update.EulaAccepted)
                pending.Add((item, (string)(update.EulaText ?? "")));
        }
        return pending;
    });

    public static Task AcceptEulaAsync(DriverItem item) => Task.Run(() =>
    {
        dynamic update = item.Update!;
        update.AcceptEula();
    });

    /// <summary>Les états sont remontés via <paramref name="progress"/> (à créer sur le thread UI).</summary>
    public static Task<InstallSummary> InstallAsync(IReadOnlyList<DriverItem> items, IProgress<InstallProgress> progress) => Task.Run(() =>
    {
        dynamic session = CreateComObject("Microsoft.Update.Session");
        session.ClientApplicationID = "NeoDriver";

        dynamic installer = session.CreateUpdateInstaller();
        if ((bool)installer.IsBusy)
            throw new InvalidOperationException("Une autre installation Windows Update est déjà en cours. Réessayez dans quelques minutes.");

        var outcomes = new List<InstallOutcome>();
        var downloaded = new List<DriverItem>();

        foreach (var item in items)
            progress.Report(new InstallProgress(item, DriverState.Pending));

        // 1. Téléchargement, pilote par pilote pour suivre la progression
        foreach (var item in items)
        {
            progress.Report(new InstallProgress(item, DriverState.Downloading));
            try
            {
                dynamic update = item.Update!;
                if (!(bool)update.IsDownloaded)
                {
                    dynamic collection = CreateComObject("Microsoft.Update.UpdateColl");
                    collection.Add(update);
                    dynamic downloader = session.CreateUpdateDownloader();
                    downloader.Updates = collection;
                    dynamic result = downloader.Download();
                    int code = result.ResultCode;
                    if (code != ResultSucceeded && code != ResultSucceededWithErrors)
                    {
                        Fail(item, $"Téléchargement échoué ({DescribeError((int)result.HResult)})");
                        continue;
                    }
                }
                downloaded.Add(item);
                progress.Report(new InstallProgress(item, DriverState.Pending));
            }
            catch (COMException ex)
            {
                Fail(item, $"Téléchargement échoué ({DescribeError(ex.HResult)})");
            }
        }

        if (downloaded.Count == 0)
            return new InstallSummary(outcomes, false);

        // 2. Installation groupée
        dynamic toInstall = CreateComObject("Microsoft.Update.UpdateColl");
        foreach (var item in downloaded)
        {
            toInstall.Add((dynamic)item.Update!);
            progress.Report(new InstallProgress(item, DriverState.Installing));
        }

        bool reboot = false;
        try
        {
            installer.Updates = toInstall;
            dynamic result = installer.Install();
            reboot = result.RebootRequired;
            for (int i = 0; i < downloaded.Count; i++)
            {
                dynamic updateResult = result.GetUpdateResult(i);
                int code = updateResult.ResultCode;
                if (code == ResultSucceeded || code == ResultSucceededWithErrors)
                {
                    outcomes.Add(new InstallOutcome(downloaded[i], true, (bool)updateResult.RebootRequired ? "Installé, redémarrage requis" : "Installé"));
                    progress.Report(new InstallProgress(downloaded[i], DriverState.Installed));
                }
                else
                {
                    Fail(downloaded[i], $"Installation échouée ({DescribeError((int)updateResult.HResult)})");
                }
            }
        }
        catch (COMException ex)
        {
            foreach (var item in downloaded)
                Fail(item, $"Installation échouée ({DescribeError(ex.HResult)})");
        }

        return new InstallSummary(outcomes, reboot);

        void Fail(DriverItem item, string message)
        {
            outcomes.Add(new InstallOutcome(item, false, message));
            progress.Report(new InstallProgress(item, DriverState.Failed, message));
        }
    });

    private static dynamic CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException("Le service Windows Update est indisponible sur ce PC.");
        return Activator.CreateInstance(type)!;
    }

    private static string DescribeError(int hresult) => unchecked((uint)hresult) switch
    {
        0 => "erreur inconnue",
        0x80070005 => "accès refusé, droits administrateur requis",
        0x80240016 => "une autre installation est en cours",
        0x80240017 => "pilote non applicable à ce PC",
        0x8024402C or 0x80244022 or 0x80072EE2 => "problème de connexion à Windows Update",
        0x80240022 => "toutes les mises à jour ont échoué",
        0x80070070 => "espace disque insuffisant",
        var code => $"code 0x{code:X8}",
    };
}
