using System.Management;

namespace NeoBoost.Services;

/// <summary>État d'un disque physique : modèle, taille, type, santé déclarée par Windows et usure.</summary>
public sealed record DiskHealth(
    string Model,
    string? Interface,
    long Size,
    DiskKind Kind,
    bool? Healthy,
    string? Detail,
    double? WearPercent,
    ulong? PowerOnHours)
{
    public string HealthText => Healthy switch
    {
        true => "Sain",
        false => "À vérifier",
        _ => "Information non disponible",
    };
}

/// <summary>Rapport d'usure du PC : santé des disques, batterie, mémoire.</summary>
public static class HealthReport
{
    private const string StorageScope = @"\\.\root\Microsoft\Windows\Storage";

    public sealed record BatteryInfo(ushort? ChargePercent, uint? DesignCapacity, uint? FullCapacity, bool? Charging)
    {
        /// <summary>Capacité perdue par la batterie, en % (null si l'information n'est pas disponible).</summary>
        public double? WearPercent => DesignCapacity is > 0 && FullCapacity is > 0
            ? Math.Clamp(100 - FullCapacity.Value * 100.0 / DesignCapacity.Value, 0, 100)
            : null;
    }

    public static Task<List<DiskHealth>> DisksAsync() => Task.Run(ReadDisks);

    public static Task<BatteryInfo?> BatteryAsync() => Task.Run(ReadBattery);

    private static List<DiskHealth> ReadDisks()
    {
        var result = new List<DiskHealth>();
        try
        {
            var scope = new ManagementScope(StorageScope);

            // Un disque par identifiant : modèle, type (SSD/disque dur), état de santé
            using var disks = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, Size, BusType, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject disk in disks.Get())
            {
                var id = disk["DeviceId"] as string ?? "";
                var model = (disk["FriendlyName"] as string ?? "Disque").Trim();
                var size = Convert.ToInt64(disk["Size"] ?? 0);
                var kind = Convert.ToUInt16(disk["MediaType"] ?? 0) == 3 ? DiskKind.Hdd : DiskKind.Ssd;
                var bus = BusText(disk["BusType"] as ushort?);

                // HealthStatus : 0 = inconnu, 1 = autre, 2 = OK, 3 = dégradé, 4 = erreur, 5 = panne imminente…
                // Beaucoup de pilotes n'exposent pas l'état (0) : on s'appuie alors sur OperationalStatus (2 = OK).
                var operations = disk["OperationalStatus"] as ushort[];
                bool? healthy = disk["HealthStatus"] is ushort status
                    ? status switch
                    {
                        2 => true,
                        0 or 1 => operations is not null && operations.Contains((ushort)2),
                        _ => false,
                    }
                    : null;

                string? detail = null;
                if (healthy == false)
                    detail = "Windows signale ce disque comme dégradé : sauvegardez vos données et envisagez de le remplacer.";
                else if (healthy is null && operations is { Length: > 0 } && operations.Any(o => o is not 0 and not 2))
                    detail = "État opérationnel inhabituel : à surveiller.";

                // Fiabilité : heures de fonctionnement, usure (SSD)
                ulong? hours = null;
                double? wear = null;
                try
                {
                    using var reliability = new ManagementObjectSearcher(scope,
                        new ObjectQuery($"SELECT PowerOnHours, Wear, StartStopCycleCount FROM MSFT_StorageReliabilityCounter WHERE DeviceId = '{id}'"));
                    foreach (ManagementObject row in reliability.Get())
                    {
                        hours = row["PowerOnHours"] as ulong?;
                        if (row["Wear"] is byte w && w <= 100)
                            wear = 100 - w; // « Wear » = capacité restante en %
                    }
                }
                catch (Exception)
                {
                    // Ces compteurs ne sont pas exposés par tous les pilotes
                }

                result.Add(new DiskHealth(model, bus, size, kind, healthy, detail, wear, hours));
            }
        }
        catch (Exception)
        {
        }
        return result.OrderBy(d => d.Model).ToList();
    }

    private static string? BusText(ushort? bus) => bus switch
    {
        1 => "SCSI", 2 => "ATA", 3 => "1394", 4 => "SSA", 5 => "Fibre Channel", 6 => "USB",
        7 => "RAID", 8 => "iSCSI", 9 => "SAS", 10 => "SATA", 11 => "SD", 12 => "MMC",
        13 => "Virtual", 14 => "File Backed Virtual", 15 => "Storage Spaces", 17 => "NVMe",
        _ => null,
    };

    private static BatteryInfo? ReadBattery()
    {
        try
        {
            using var battery = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            var rows = battery.Get().Cast<ManagementObject>().ToList();
            if (rows.Count == 0)
                return null; // ordinateur de bureau : aucune batterie

            ushort? charge = null;
            foreach (var row in rows)
                if (row["EstimatedChargeRemaining"] is ushort c && c > 0)
                    charge = c;

            // BatteryStatus : 1 = déchargement, 2 = complètement chargée, 3 = faible, 6/7 = en charge
            bool? charging = rows.Select(r => r["BatteryStatus"] as ushort?).FirstOrDefault() is { } status
                ? status is 2 or 6 or 7 : null;

            uint? design = null, full = null;
            try
            {
                var scope = new ManagementScope(@"\\.\root\wmi");
                using var staticData = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DesignCapacity FROM BatteryStaticData"));
                design = staticData.Get().Cast<ManagementObject>().Select(b => b["DesignCapacity"] as uint?).FirstOrDefault(v => v is > 0);
                using var fullData = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FullChargeCapacity FROM BatteryFullChargedCapacity"));
                full = fullData.Get().Cast<ManagementObject>().Select(b => b["FullChargeCapacity"] as uint?).FirstOrDefault(v => v is > 0);
            }
            catch (Exception)
            {
            }

            return new BatteryInfo(charge, design, full, charging);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
