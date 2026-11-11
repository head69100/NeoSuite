using System.Management;

namespace NeoDriver.Services;

public static class RestorePointService
{
    private const int DeviceDriverInstall = 10;
    private const int BeginSystemChange = 100;

    /// <summary>
    /// Crée un point de restauration système (droits administrateur requis).
    /// Renvoie false si Windows n'en a pas créé de nouveau parce qu'un point a moins de 24 h.
    /// </summary>
    public static Task<bool> CreateAsync(string description) => Task.Run(() =>
    {
        var scope = new ManagementScope(@"\\.\root\default");
        var before = CountRestorePoints(scope);

        using var restore = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
        using var input = restore.GetMethodParameters("CreateRestorePoint");
        input["Description"] = description;
        input["RestorePointType"] = DeviceDriverInstall;
        input["EventType"] = BeginSystemChange;

        using var output = restore.InvokeMethod("CreateRestorePoint", input, null);
        var code = Convert.ToUInt32(output["ReturnValue"]);
        if (code != 0)
            throw new InvalidOperationException(Describe(code));

        return CountRestorePoints(scope) > before;
    });

    private static int CountRestorePoints(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT SequenceNumber FROM SystemRestore"));
        using var results = searcher.Get();
        return results.Count;
    }

    private static string Describe(uint code) => code switch
    {
        1058 => "La protection du système est désactivée sur ce PC.",
        1460 => "Délai dépassé pendant la création du point de restauration.",
        _ => $"Erreur Windows {code}.",
    };
}
