using System.Management;
using Neo.Shared;
using NeoDriver.Models;

namespace NeoDriver.Services;

/// <summary>Recherche des mises à jour de pilotes via l'agent Windows Update (COM Microsoft.Update.Session).</summary>
public static class DriverUpdateService
{
    public static IReadOnlyList<DriverItem>? LastResult { get; private set; }
    public static DateTime? LastSearch { get; private set; }

    private static Task<IReadOnlyList<DriverItem>>? _running;

    public static bool IsSearching => _running is not null;

    /// <summary>Lance une recherche (ou rejoint celle déjà en cours).</summary>
    public static Task<IReadOnlyList<DriverItem>> SearchAsync()
    {
        return _running ??= RunSearchAsync();
    }

    /// <summary>Retire de la dernière recherche les pilotes qui viennent d'être installés.</summary>
    public static void RemoveInstalled(IEnumerable<DriverItem> installed)
    {
        if (LastResult is null)
            return;
        var ids = installed.Select(i => i.UpdateId).ToHashSet();
        LastResult = LastResult.Where(i => !ids.Contains(i.UpdateId)).ToList();
        PublishStatus(LastResult.Count);
    }

    /// <summary>État affiché par NeoSuite sur la tuile de NeoDriver, même quand NeoDriver est fermé.</summary>
    public static void PublishStatus(int count) =>
        NeoStatus.Write("NeoDriver", count switch { 0 => "À jour", 1 => "1 mise à jour", var n => $"{n} mises à jour" });

    private static async Task<IReadOnlyList<DriverItem>> RunSearchAsync()
    {
        try
        {
            var result = await Task.Run(Search);
            LastResult = result;
            LastSearch = DateTime.Now;
            PublishStatus(result.Count);
            return result;
        }
        finally
        {
            _running = null;
        }
    }

    private static IReadOnlyList<DriverItem> Search()
    {
        var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new InvalidOperationException("Le service Windows Update est indisponible sur ce PC.");
        dynamic session = Activator.CreateInstance(sessionType)!;
        session.ClientApplicationID = "NeoDriver";
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");

        var devices = DeviceInventory.Load();
        var items = new List<DriverItem>();

        dynamic updates = searchResult.Updates;
        int count = updates.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic update = updates[i];
            string hwid = update.DriverHardwareID ?? "";
            string driverClass = update.DriverClass ?? "";
            var device = devices.FindByHardwareId(hwid);
            var (category, glyph) = DescribeClass(driverClass);

            items.Add(new DriverItem
            {
                UpdateId = update.Identity.UpdateID,
                Title = update.Title,
                DeviceName = device?.Name ?? (string)(update.DriverModel ?? update.Title),
                Category = category,
                Manufacturer = update.DriverManufacturer ?? update.DriverProvider ?? "Inconnu",
                Glyph = glyph,
                HardwareId = hwid,
                CurrentVersion = device?.DriverVersion ?? "",
                CurrentDate = device?.DriverDate,
                NewDate = update.DriverVerDate is DateTime d ? d : null,
                DownloadSize = Convert.ToInt64(update.MaxDownloadSize),
                Update = update,
            });
        }

        return items;
    }

    private static (string Category, string Glyph) DescribeClass(string driverClass) => driverClass.ToLowerInvariant() switch
    {
        "display" => ("Carte graphique", ""),
        "monitor" => ("Écran", ""),
        "media" or "audioprocessingobject" or "audioendpoint" => ("Audio", ""),
        "net" or "networking" => ("Réseau", ""),
        "bluetooth" => ("Bluetooth", ""),
        "system" => ("Système / chipset", ""),
        "firmware" => ("Firmware", ""),
        "usb" => ("USB", ""),
        "hidclass" or "mouse" or "keyboard" => ("Périphérique d'entrée", ""),
        "printers" or "printer" => ("Imprimante", ""),
        "camera" or "image" => ("Caméra", ""),
        "diskdrive" or "hdc" or "scsiadapter" => ("Stockage", ""),
        "softwarecomponent" or "extension" => ("Composant logiciel", ""),
        _ => ("Autre matériel", ""),
    };

    /// <summary>Inventaire des périphériques et de leurs pilotes installés (WMI).</summary>
    private sealed class DeviceInventory
    {
        public sealed record Device(string Name, string? DriverVersion, DateTime? DriverDate, string[] Ids);

        private readonly List<Device> _devices = [];

        public static DeviceInventory Load()
        {
            var inventory = new DeviceInventory();

            var drivers = new Dictionary<string, (string? Version, DateTime? Date)>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher("SELECT DeviceID, DriverVersion, DriverDate FROM Win32_PnPSignedDriver"))
            using (var results = searcher.Get())
            {
                foreach (ManagementBaseObject mo in results)
                {
                    if (mo["DeviceID"] is not string id)
                        continue;
                    DateTime? date = mo["DriverDate"] is string dmtf ? ManagementDateTimeConverter.ToDateTime(dmtf) : null;
                    drivers[id] = (mo["DriverVersion"] as string, date);
                }
            }

            using (var searcher = new ManagementObjectSearcher("SELECT DeviceID, Name, HardwareID, CompatibleID FROM Win32_PnPEntity"))
            using (var results = searcher.Get())
            {
                foreach (ManagementBaseObject mo in results)
                {
                    var ids = ((mo["HardwareID"] as string[]) ?? [])
                        .Concat((mo["CompatibleID"] as string[]) ?? [])
                        .ToArray();
                    if (ids.Length == 0)
                        continue;
                    drivers.TryGetValue(mo["DeviceID"] as string ?? "", out var drv);
                    inventory._devices.Add(new Device(mo["Name"] as string ?? "Périphérique inconnu", drv.Version, drv.Date, ids));
                }
            }

            return inventory;
        }

        public Device? FindByHardwareId(string hwid)
        {
            if (string.IsNullOrEmpty(hwid))
                return null;
            return _devices.FirstOrDefault(d => d.Ids.Contains(hwid, StringComparer.OrdinalIgnoreCase));
        }
    }
}
