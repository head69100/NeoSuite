using System.Management;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;
using NeoDriver.Models;

namespace NeoDriver.Services;

/// <summary>Collecte les informations matérielles (WMI + registre), façon CPU-Z.</summary>
public static class HardwareInfoService
{
    private static Task<List<HardwareSection>>? _cached;

    public static Task<List<HardwareSection>> GetAsync(bool refresh = false)
    {
        if (refresh || _cached is { IsFaulted: true })
            _cached = null;
        return _cached ??= Task.Run(Collect);
    }

    private static List<HardwareSection> Collect() =>
    [
        Safe("Processeur", "", CollectCpu),
        Safe("Carte mère", "", CollectMotherboard),
        Safe("Mémoire", "", CollectMemory),
        Safe("Carte graphique", "", CollectGpu),
        Safe("Stockage", "", CollectStorage),
        Safe("Périphériques", "", CollectOthers),
    ];

    /// <summary>Une section en erreur n'empêche pas d'afficher les autres.</summary>
    private static HardwareSection Safe(string title, string glyph, Action<HardwareSection> fill)
    {
        var section = new HardwareSection(title, glyph);
        try
        {
            fill(section);
        }
        catch (Exception ex)
        {
            section.AddGroup("Erreur").Add("Détail", ex.Message);
        }
        return section;
    }

    // ---------- Processeur ----------

    private static void CollectCpu(HardwareSection s)
    {
        var cpus = Query("SELECT * FROM Win32_Processor");
        var cs = Query("SELECT HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault();

        foreach (var (cpu, index) in cpus.Select((c, i) => (c, i)))
        {
            var name = Str(cpu, "Name");
            s.AddGroup(cpus.Count > 1 ? $"Processeur {index + 1}" : "Processeur")
                .Add("Nom", name)
                .Add("Fabricant", Vendor(Str(cpu, "Manufacturer")))
                .Add("Socket", Str(cpu, "SocketDesignation"))
                .Add("Identification", Str(cpu, "Caption"))
                .Add("Architecture", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString());

            var virt = Bool(cpu, "VirtualizationFirmwareEnabled") || Bool(cs, "HypervisorPresent");
            s.AddGroup("Cœurs et fréquences")
                .Add("Cœurs", Str(cpu, "NumberOfCores"))
                .Add("Threads", Str(cpu, "NumberOfLogicalProcessors"))
                .Add("Fréquence de base", Mhz(cpu, "MaxClockSpeed"))
                .Add("Fréquence actuelle", Mhz(cpu, "CurrentClockSpeed"))
                .Add("Virtualisation", virt ? "Activée" : "Désactivée");

            s.AddGroup("Cache")
                .Add("Cache L2", Kb(cpu, "L2CacheSize"))
                .Add("Cache L3", Kb(cpu, "L3CacheSize"));
        }

        s.AddGroup("Jeux d'instructions").Add("Pris en charge", string.Join(", ", InstructionSets()));
    }

    private static IEnumerable<string> InstructionSets()
    {
        if (!X86Base.IsSupported)
            yield break;
        yield return "x86-64";
        if (Sse.IsSupported) yield return "SSE";
        if (Sse2.IsSupported) yield return "SSE2";
        if (Sse3.IsSupported) yield return "SSE3";
        if (Ssse3.IsSupported) yield return "SSSE3";
        if (Sse41.IsSupported) yield return "SSE4.1";
        if (Sse42.IsSupported) yield return "SSE4.2";
        if (Avx.IsSupported) yield return "AVX";
        if (Avx2.IsSupported) yield return "AVX2";
        if (Avx512F.IsSupported) yield return "AVX-512";
        if (Fma.IsSupported) yield return "FMA3";
        if (System.Runtime.Intrinsics.X86.Aes.IsSupported) yield return "AES";
        if (Pclmulqdq.IsSupported) yield return "PCLMUL";
        if (Bmi1.IsSupported) yield return "BMI1";
        if (Bmi2.IsSupported) yield return "BMI2";
        if (Popcnt.IsSupported) yield return "POPCNT";
        if (Lzcnt.IsSupported) yield return "LZCNT";
    }

    // ---------- Carte mère ----------

    private static void CollectMotherboard(HardwareSection s)
    {
        var board = Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS").FirstOrDefault();
        var system = Query("SELECT Manufacturer, Model, SystemType FROM Win32_ComputerSystem").FirstOrDefault();

        s.AddGroup("Carte mère")
            .Add("Fabricant", Str(board, "Manufacturer"))
            .Add("Modèle", Str(board, "Product"))
            .Add("Révision", Clean(Str(board, "Version")));

        s.AddGroup("BIOS")
            .Add("Éditeur", Str(bios, "Manufacturer"))
            .Add("Version", Str(bios, "SMBIOSBIOSVersion"))
            .Add("Date", Date(bios, "ReleaseDate"))
            .Add("SMBIOS", bios is null ? null : $"{Str(bios, "SMBIOSMajorVersion")}.{Str(bios, "SMBIOSMinorVersion")}")
            .Add("Mode de démarrage", FirmwareType());

        s.AddGroup("Système")
            .Add("Fabricant", Clean(Str(system, "Manufacturer")))
            .Add("Modèle", Clean(Str(system, "Model")))
            .Add("Type", Str(system, "SystemType"))
            .Add("Système d'exploitation", SystemInfoService.GetOsName())
            .Add("Build", SystemInfoService.GetOsBuild())
            .Add("Démarré depuis", FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)));
    }

    private static string? FirmwareType()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
        return key?.GetValue("PEFirmwareType") switch
        {
            1 => "BIOS (Legacy)",
            2 => "UEFI",
            _ => null,
        };
    }

    // ---------- Mémoire ----------

    private static void CollectMemory(HardwareSection s)
    {
        var modules = Query("SELECT * FROM Win32_PhysicalMemory");
        var array = Query("SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray").FirstOrDefault();
        var total = modules.Sum(m => Convert.ToInt64(m["Capacity"] ?? 0L));
        var first = modules.FirstOrDefault();

        s.AddGroup("Général")
            .Add("Taille totale", Size(total))
            .Add("Type", first is null ? null : MemoryType(Convert.ToInt32(first["SMBIOSMemoryType"] ?? 0)))
            .Add("Emplacements utilisés", array is null ? $"{modules.Count}" : $"{modules.Count} sur {Str(array, "MemoryDevices")}")
            .Add("Capacité maximale", array?["MaxCapacityEx"] is { } max ? Size(Convert.ToInt64(max) * 1024) : null)
            .Add("Canaux", modules.Count >= 2 && modules.Select(m => Str(m, "BankLabel")).Distinct().Count() > 1 ? "Dual channel ou plus" : "Simple canal");

        foreach (var (m, i) in modules.Select((m, i) => (m, i)))
        {
            var part = Str(m, "PartNumber")?.Trim();
            s.AddGroup($"Barrette {i + 1} · {Str(m, "DeviceLocator")}")
                .Add("Capacité", Size(Convert.ToInt64(m["Capacity"] ?? 0L)))
                .Add("Type", MemoryType(Convert.ToInt32(m["SMBIOSMemoryType"] ?? 0)))
                .Add("Vitesse", m["ConfiguredClockSpeed"] is { } cfg ? $"{cfg} MT/s" : null)
                .Add("Vitesse nominale", m["Speed"] is { } sp ? $"{sp} MT/s" : null)
                .Add("Fabricant", MemoryVendor(Str(m, "Manufacturer"), part))
                .Add("Référence", part)
                .Add("Emplacement", Str(m, "BankLabel"));
        }
    }

    private static string MemoryType(int smbios) => smbios switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "Inconnu",
    };

    private static string? MemoryVendor(string? reported, string? part)
    {
        if (!string.IsNullOrWhiteSpace(reported) && !reported.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return reported;
        if (string.IsNullOrEmpty(part))
            return null;
        // Le fabricant est souvent absent : on le déduit du préfixe de la référence
        (string Prefix, string Vendor)[] prefixes =
        [
            ("CM", "Corsair"), ("F4-", "G.Skill"), ("F5-", "G.Skill"), ("F3-", "G.Skill"),
            ("KF", "Kingston"), ("KHX", "Kingston"), ("KVR", "Kingston"),
            ("BL", "Crucial"), ("CT", "Crucial"), ("TF", "Team Group"), ("TLZ", "Team Group"),
            ("PV", "Patriot"), ("PSD", "Patriot"), ("M378", "Samsung"), ("M471", "Samsung"),
            ("HMA", "SK Hynix"), ("HMT", "SK Hynix"), ("MTA", "Micron"),
        ];
        return prefixes.FirstOrDefault(p => part.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).Vendor;
    }

    // ---------- Carte graphique ----------

    private static void CollectGpu(HardwareSection s)
    {
        var gpus = Query("SELECT * FROM Win32_VideoController");
        var vram = ReadVramFromRegistry();

        foreach (var gpu in gpus)
        {
            var name = Str(gpu, "Name") ?? "Carte graphique";
            var pnp = Str(gpu, "PNPDeviceID") ?? "";
            var width = gpu["CurrentHorizontalResolution"];

            s.AddGroup(name)
                .Add("Fabricant", GpuVendor(pnp) ?? Str(gpu, "AdapterCompatibility"))
                .Add("Processeur graphique", Str(gpu, "VideoProcessor"))
                .Add("Mémoire vidéo", vram.TryGetValue(name, out var bytes) ? Size(bytes) : Size(Convert.ToInt64(gpu["AdapterRAM"] ?? 0L)))
                .Add("Version du pilote", Str(gpu, "DriverVersion"))
                .Add("Date du pilote", Date(gpu, "DriverDate"))
                .Add("Résolution", width is null ? null : $"{width} × {gpu["CurrentVerticalResolution"]}")
                .Add("Fréquence d'affichage", gpu["CurrentRefreshRate"] is { } hz ? $"{hz} Hz" : null)
                .Add("Identifiant matériel", pnp.Split('\\').ElementAtOrDefault(1));
        }

        foreach (var (name, vendor) in Monitors())
            s.AddGroup("Écran").Add("Modèle", name).Add("Fabricant", vendor);
    }

    /// <summary>La mémoire vidéo réelle (Win32_VideoController plafonne à 4 Go).</summary>
    private static Dictionary<string, long> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null)
            return result;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            using var key = cls.OpenSubKey(sub);
            if (key?.GetValue("DriverDesc") is string desc && key.GetValue("HardwareInformation.qwMemorySize") is long size)
                result[desc] = size;
        }
        return result;
    }

    private static string? GpuVendor(string pnp)
    {
        var ven = pnp.ToUpperInvariant();
        if (ven.Contains("VEN_10DE")) return "NVIDIA";
        if (ven.Contains("VEN_1002")) return "AMD";
        if (ven.Contains("VEN_8086")) return "Intel";
        return null;
    }

    private static IEnumerable<(string Name, string? Vendor)> Monitors()
    {
        List<ManagementBaseObject> monitors;
        try
        {
            monitors = Query("SELECT UserFriendlyName, ManufacturerName FROM WmiMonitorID", @"root\wmi");
        }
        catch (ManagementException)
        {
            yield break;
        }
        foreach (var m in monitors)
        {
            var name = DecodeWmiString(m["UserFriendlyName"]);
            if (!string.IsNullOrEmpty(name))
                yield return (name, DecodeWmiString(m["ManufacturerName"]));
        }
    }

    private static string? DecodeWmiString(object? value) => value is ushort[] chars
        ? new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim()
        : null;

    // ---------- Stockage ----------

    private static void CollectStorage(HardwareSection s)
    {
        var disks = Query("SELECT FriendlyName, MediaType, BusType, Size, HealthStatus, FirmwareVersion, SpindleSpeed FROM MSFT_PhysicalDisk",
            @"root\Microsoft\Windows\Storage");

        foreach (var d in disks)
        {
            s.AddGroup(Str(d, "FriendlyName") ?? "Disque")
                .Add("Capacité", Size(Convert.ToInt64(d["Size"] ?? 0L), decimalUnits: true))
                .Add("Type", Convert.ToInt32(d["MediaType"] ?? 0) switch { 3 => "Disque dur (HDD)", 4 => "SSD", 5 => "SCM", _ => "Inconnu" })
                .Add("Interface", Convert.ToInt32(d["BusType"] ?? 0) switch
                {
                    7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 17 => "NVMe", _ => "Autre",
                })
                .Add("Firmware", Str(d, "FirmwareVersion"))
                .Add("État", Convert.ToInt32(d["HealthStatus"] ?? 0) switch { 0 => "Bon", 1 => "Avertissement", 2 => "Défaillant", _ => "Inconnu" });
        }

        foreach (var v in Query("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3"))
        {
            var size = Convert.ToInt64(v["Size"] ?? 0L);
            var free = Convert.ToInt64(v["FreeSpace"] ?? 0L);
            var label = Str(v, "VolumeName");
            s.AddGroup($"Volume {Str(v, "DeviceID")}{(string.IsNullOrEmpty(label) ? "" : $" ({label})")}")
                .Add("Système de fichiers", Str(v, "FileSystem"))
                .Add("Taille", Size(size, decimalUnits: true))
                .Add("Espace libre", size > 0 ? $"{Size(free, decimalUnits: true)} ({free * 100 / size} %)" : null);
        }
    }

    // ---------- Réseau, audio ----------

    private static void CollectOthers(HardwareSection s)
    {
        var net = s.AddGroup("Cartes réseau");
        foreach (var n in Query("SELECT Name, Speed, NetConnectionStatus FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
        {
            var connected = Convert.ToInt32(n["NetConnectionStatus"] ?? 0) == 2;
            var speed = n["Speed"] is { } sp && Convert.ToUInt64(sp) is > 0 and < ulong.MaxValue / 2 ? FormatSpeed(Convert.ToUInt64(sp)) : null;
            net.Add(Str(n, "Name") ?? "Carte réseau", connected ? $"Connectée{(speed is null ? "" : $" · {speed}")}" : "Déconnectée");
        }

        var audio = s.AddGroup("Audio");
        foreach (var a in Query("SELECT Name, Manufacturer FROM Win32_SoundDevice"))
            audio.Add(Str(a, "Name") ?? "Périphérique audio", Str(a, "Manufacturer"));

        var usb = s.AddGroup("Contrôleurs USB");
        foreach (var u in Query("SELECT Name, Manufacturer FROM Win32_USBController"))
            usb.Add(Str(u, "Name") ?? "Contrôleur USB", Str(u, "Manufacturer"));

        s.Groups.RemoveAll(g => g.Rows.Count == 0);
    }

    // ---------- Utilitaires ----------

    private static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        return searcher.Get().Cast<ManagementBaseObject>().ToList();
    }

    private static string? Str(ManagementBaseObject? mo, string prop) => mo?[prop]?.ToString()?.Trim();

    private static bool Bool(ManagementBaseObject? mo, string prop) => mo?[prop] is true;

    /// <summary>Ignore les valeurs de remplissage laissées par les fabricants.</summary>
    private static string? Clean(string? value) =>
        value is null || value.Contains("Default string", StringComparison.OrdinalIgnoreCase)
            || value.Contains("To be filled", StringComparison.OrdinalIgnoreCase)
            || value.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
            ? null : value;

    private static string? Vendor(string? manufacturer) => manufacturer switch
    {
        "AuthenticAMD" => "AMD",
        "GenuineIntel" => "Intel",
        _ => manufacturer,
    };

    private static string? Mhz(ManagementBaseObject mo, string prop) =>
        mo[prop] is { } v && Convert.ToInt32(v) > 0 ? $"{Convert.ToInt32(v) / 1000.0:0.00} GHz" : null;

    private static string? Kb(ManagementBaseObject mo, string prop) =>
        mo[prop] is { } v && Convert.ToInt64(v) > 0 ? Size(Convert.ToInt64(v) * 1024) : null;

    private static string? Date(ManagementBaseObject? mo, string prop) =>
        mo?[prop] is string dmtf ? ManagementDateTimeConverter.ToDateTime(dmtf).ToString("dd/MM/yyyy") : null;

    private static string? Size(long bytes, bool decimalUnits = false)
    {
        if (bytes <= 0)
            return null;
        double unit = decimalUnits ? 1000 : 1024;
        string[] names = ["octets", "Ko", "Mo", "Go", "To"];
        double value = bytes;
        var i = 0;
        while (value >= unit && i < names.Length - 1)
        {
            value /= unit;
            i++;
        }
        return $"{value:0.#} {names[i]}";
    }

    private static string FormatSpeed(ulong bitsPerSecond) => bitsPerSecond >= 1_000_000_000
        ? $"{bitsPerSecond / 1_000_000_000.0:0.#} Gbit/s"
        : $"{bitsPerSecond / 1_000_000.0:0} Mbit/s";

    private static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} j {t.Hours} h {t.Minutes} min" : $"{t.Hours} h {t.Minutes} min";
}
