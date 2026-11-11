using System.Management;
using System.Text.RegularExpressions;

namespace NeoDriver.Services;

/// <summary>Un périphérique que Windows signale en erreur (Gestionnaire de périphériques : point d'exclamation jaune).</summary>
public sealed record ProblemDevice(string Name, string PnpId, string ClassName, int ErrorCode, string Explanation, string[] HardwareIds, string Vendor)
{
    /// <summary>Le premier identifiant matériel (ex. PCI\VEN_10EC&amp;DEV_8168&amp;SUBSYS_…) : la meilleure clé de recherche web.</summary>
    public string MainId => HardwareIds.FirstOrDefault() ?? PnpId;

    public bool HasNoDriver => ErrorCode is 28 or 1;
}

/// <summary>Périphériques en erreur, avec l'explication du code, et leur fabricant d'après leur identifiant matériel.</summary>
public static partial class DeviceHealthService
{
    // Pas des pannes : désactivé volontairement, absent, débranché, retrait sécurisé
    private static readonly HashSet<int> NotProblems = [22, 24, 45, 47];

    public static Task<List<ProblemDevice>> GetProblemsAsync() => Task.Run(() =>
    {
        var list = new List<ProblemDevice>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID, ConfigManagerErrorCode, HardwareID, CompatibleID, PNPClass, Present FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        using var results = searcher.Get();
        foreach (ManagementBaseObject mo in results)
        {
            var code = Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0);
            // Les périphériques « fantômes » (anciens branchements) restent listés mais ne sont pas un problème actuel
            if (NotProblems.Contains(code) || mo["Present"] is false)
                continue;
            var ids = ((mo["HardwareID"] as string[]) ?? []).Concat((mo["CompatibleID"] as string[]) ?? []).ToArray();
            list.Add(new ProblemDevice(
                (mo["Name"] as string) is { Length: > 0 } name ? name : "Périphérique inconnu",
                mo["PNPDeviceID"] as string ?? "",
                mo["PNPClass"] as string ?? "",
                code,
                Explain(code),
                ids,
                DescribeVendor(ids)));
        }
        return list.OrderBy(d => d.ErrorCode == 28 ? 0 : 1).ThenBy(d => d.Name).ToList();
    });

    public static string Explain(int code) => code switch
    {
        1 => "Le périphérique n'est pas configuré correctement.",
        3 => "Le pilote est corrompu ou le PC manque de mémoire.",
        10 => "Le périphérique ne peut pas démarrer.",
        12 => "Conflit de ressources avec un autre périphérique.",
        14 => "Un redémarrage du PC est nécessaire.",
        18 => "Le pilote doit être réinstallé.",
        19 => "La configuration du périphérique est incomplète ou corrompue.",
        21 => "Windows est en train de retirer ce périphérique.",
        28 => "Aucun pilote n'est installé pour ce périphérique.",
        29 => "Le périphérique est désactivé par le BIOS.",
        31 => "Windows ne parvient pas à charger le pilote.",
        32 => "Le service du pilote est désactivé.",
        33 or 34 or 35 or 36 => "Windows n'arrive pas à déterminer les ressources du périphérique.",
        37 => "Le pilote a renvoyé une erreur au démarrage.",
        38 => "L'ancien pilote est encore en mémoire : redémarrez le PC.",
        39 => "Le pilote est corrompu ou manquant.",
        40 => "Des informations du pilote manquent dans le registre.",
        41 => "Le pilote est chargé mais le périphérique reste introuvable.",
        42 => "Un périphérique identique est déjà présent.",
        43 => "Windows a arrêté ce périphérique après qu'il a signalé un problème.",
        44 => "Une application ou un service a arrêté ce périphérique.",
        48 => "Ce pilote est bloqué : il n'est pas compatible avec cette version de Windows.",
        49 => "Le registre de Windows est trop volumineux.",
        50 => "Windows ne peut pas appliquer toutes les propriétés du périphérique.",
        51 => "Le périphérique attend un autre périphérique.",
        52 => "Windows ne peut pas vérifier la signature numérique du pilote.",
        var other => $"Erreur Windows {other}.",
    };

    // ---------- Fabricants d'après l'identifiant matériel ----------

    /// <summary>« PCI\VEN_10EC&amp;DEV_8168… » → « Realtek · PCI (modèle 8168) ». Vide si l'identifiant n'en dit rien.</summary>
    public static string DescribeVendor(IEnumerable<string> hardwareIds)
    {
        foreach (var id in hardwareIds)
        {
            var bus = id.Split('\\')[0].ToUpperInvariant();
            if (UsbId().Match(id) is { Success: true } usb && bus == "USB")
            {
                var vid = usb.Groups[1].Value.ToUpperInvariant();
                var product = usb.Groups[2].Value.ToUpperInvariant();
                return $"{(UsbVendors.TryGetValue(vid, out var n) ? n : $"fabricant {vid}")} · USB (produit {product})";
            }
            if (PciId().Match(id) is { Success: true } pci)
            {
                var ven = pci.Groups[1].Value.ToUpperInvariant();
                var dev = pci.Groups[2].Value.ToUpperInvariant();
                var kind = bus switch { "PCI" => "PCI", "HDAUDIO" => "audio HD", _ => bus };
                return $"{(PciVendors.TryGetValue(ven, out var n) ? n : $"fabricant {ven}")} · {kind} (modèle {dev})";
            }
        }
        return "";
    }

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbId();

    [GeneratedRegex(@"VEN_([0-9A-F]{4}).*?DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PciId();

    private static readonly Dictionary<string, string> PciVendors = new()
    {
        ["8086"] = "Intel", ["10DE"] = "NVIDIA", ["1002"] = "AMD (ATI)", ["1022"] = "AMD", ["10EC"] = "Realtek",
        ["14E4"] = "Broadcom", ["168C"] = "Qualcomm Atheros", ["17CB"] = "Qualcomm", ["14C3"] = "MediaTek", ["1969"] = "Qualcomm Atheros (Killer)",
        ["1B21"] = "ASMedia", ["1912"] = "Renesas", ["1106"] = "VIA", ["1B4B"] = "Marvell", ["11AB"] = "Marvell", ["144D"] = "Samsung",
        ["15B7"] = "SanDisk", ["1C5C"] = "SK hynix", ["1987"] = "Phison", ["126F"] = "Silicon Motion", ["1344"] = "Micron", ["1000"] = "Broadcom (LSI)",
        ["1033"] = "NEC", ["104C"] = "Texas Instruments", ["197B"] = "JMicron", ["1B73"] = "Fresco Logic", ["1AF4"] = "Red Hat (virtio)",
        ["15AD"] = "VMware", ["1D6A"] = "Aquantia", ["1D17"] = "Zhaoxin", ["1CC1"] = "ADATA", ["1E0F"] = "KIOXIA", ["1179"] = "Toshiba",
        ["10B5"] = "PLX", ["1414"] = "Microsoft", ["8087"] = "Intel", ["1C58"] = "HGST", ["1B36"] = "Red Hat (QEMU)",
    };

    private static readonly Dictionary<string, string> UsbVendors = new()
    {
        ["0BDA"] = "Realtek", ["8087"] = "Intel", ["0A5C"] = "Broadcom", ["0CF3"] = "Qualcomm Atheros", ["13D3"] = "IMC Networks", ["04CA"] = "Lite-On",
        ["0489"] = "Foxconn", ["0E8D"] = "MediaTek", ["046D"] = "Logitech", ["045E"] = "Microsoft", ["04F2"] = "Chicony", ["0951"] = "Kingston",
        ["058F"] = "Alcor Micro", ["1532"] = "Razer", ["046A"] = "Cherry", ["1B1C"] = "Corsair", ["0B05"] = "ASUS", ["04D9"] = "Holtek",
        ["258A"] = "Sino Wealth", ["1038"] = "SteelSeries", ["0781"] = "SanDisk", ["05AC"] = "Apple", ["04E8"] = "Samsung", ["18D1"] = "Google",
        ["2717"] = "Xiaomi", ["12D1"] = "Huawei", ["0B95"] = "ASIX", ["0424"] = "Microchip (SMSC)", ["2109"] = "VIA Labs", ["174C"] = "ASMedia",
        ["1D6B"] = "Linux Foundation", ["0403"] = "FTDI", ["10C4"] = "Silicon Labs", ["067B"] = "Prolific", ["1A86"] = "QinHeng (CH340)",
        ["03F0"] = "HP", ["04B8"] = "Epson", ["04A9"] = "Canon", ["04F9"] = "Brother", ["1044"] = "Gigabyte", ["0DB0"] = "MSI", ["1462"] = "MSI",
        ["0B6A"] = "Maxon", ["046E"] = "Behavior Tech", ["1BCF"] = "Sunplus", ["0C45"] = "Microdia", ["04B4"] = "Cypress", ["1E7D"] = "Roccat",
    };
}
