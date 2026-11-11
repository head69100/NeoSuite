using System.Management;
using NeoDriver.Models;

namespace NeoDriver.Services;

public sealed record RollbackResult(bool Success, string Message);

/// <summary>
/// « Revenir à l'ancien pilote » : retire le pilote installé par NeoDriver ; Windows reprend alors le meilleur pilote
/// qui lui reste (l'ancienne version, si elle est encore dans son magasin de pilotes).
/// </summary>
public static class DriverRollbackService
{
    private sealed record Installed(string InfName, string Version);

    public static async Task<RollbackResult> RollbackAsync(HistoryEntry entry, bool restorePoint)
    {
        if (string.IsNullOrEmpty(entry.PreviousVersion))
            return new RollbackResult(false, "Aucune ancienne version n'est enregistrée pour ce pilote.");

        var current = await Task.Run(() => Find(entry.DeviceName));
        if (current.Count == 0)
            return new RollbackResult(false, "Ce périphérique n'est plus détecté.");
        if (current.All(c => c.Version == entry.PreviousVersion))
            return new RollbackResult(true, $"Le pilote est déjà revenu à la version {entry.PreviousVersion}.");

        // Seuls les pilotes ajoutés après coup (oemNN.inf) se retirent ; ceux fournis par Windows non
        var target = current.FirstOrDefault(c => c.Version != entry.PreviousVersion && c.InfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return new RollbackResult(false, "Le pilote actuel est un pilote fourni par Windows : il ne peut pas être retiré.");

        var script = (restorePoint ? ElevatedScript.RestorePointBlock("NeoDriver - retour à l'ancien pilote") : "")
                     + $"& pnputil.exe /delete-driver {ElevatedScript.Quote(target.InfName)} /uninstall /force\r\n"
                     + "$global:NeoExit = $LASTEXITCODE\r\n"
                     + "& pnputil.exe /scan-devices | Out-Null";
        var run = await ElevatedScript.RunAsync(script);

        if (run.Cancelled)
            return new RollbackResult(false, "Sans la confirmation de Windows, NeoDriver ne peut pas modifier le pilote.");
        if (run.Marker("NEO:RESTORE:FAILED:") is { } restoreError)
            return new RollbackResult(false, "Point de restauration impossible : " + restoreError);
        if (run.ExitCode != 0 && run.ExitCode != 3010)
            return new RollbackResult(false, $"Windows a refusé de retirer le pilote (code {run.ExitCode}).");

        await Task.Delay(2500); // le temps que Windows réinstalle le meilleur pilote restant
        var after = await Task.Run(() => Find(entry.DeviceName));
        var now = after.FirstOrDefault()?.Version;
        return now == entry.PreviousVersion
            ? new RollbackResult(true, $"L'ancien pilote est rétabli (version {now}).")
            : now is null
                ? new RollbackResult(true, "Le pilote a été retiré. Redémarrez le PC : Windows réinstallera un pilote.")
                : new RollbackResult(true, $"Le pilote installé par NeoDriver a été retiré. Windows utilise maintenant la version {now} (l'ancienne version {entry.PreviousVersion} n'était plus disponible).");
    }

    private static List<Installed> Find(string deviceName)
    {
        var list = new List<Installed>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceName, DriverVersion, InfName FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL");
        using var results = searcher.Get();
        foreach (ManagementBaseObject mo in results)
        {
            if (string.Equals(mo["DeviceName"] as string, deviceName, StringComparison.OrdinalIgnoreCase)
                && mo["InfName"] is string inf && mo["DriverVersion"] is string version)
                list.Add(new Installed(inf, version));
        }
        return list;
    }
}
