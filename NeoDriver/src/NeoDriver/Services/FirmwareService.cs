using System.Globalization;
using System.Management;
using Microsoft.Win32;

namespace NeoDriver.Services;

/// <summary>Carte mère (ou PC de marque) et version de BIOS.</summary>
public sealed record BoardInfo(string Vendor, string Model, string Revision, string BiosVendor, string BiosVersion, DateTime? BiosDate)
{
    public string FullName => $"{Vendor} {Model}".Trim();

    /// <summary>Ancienneté du BIOS en mois (null si la date est inconnue).</summary>
    public int? BiosAgeMonths => BiosDate is { } d ? Math.Max(0, (DateTime.Now.Year - d.Year) * 12 + DateTime.Now.Month - d.Month) : null;
}

public sealed record GpuInfo(string Name, string Vendor, string DriverVersion, string FriendlyVersion, DateTime? DriverDate, string OfficialUrl);

public sealed record ChipsetInfo(string CpuVendor, string? InstalledName, string? InstalledVersion, string OfficialUrl);

public sealed record OldDriver(string DeviceName, string Manufacturer, string ClassName, string Version, DateTime Date);

/// <summary>
/// Cartes mère, BIOS, chipset, cartes graphiques et pilotes anciens, avec les liens vers les sites officiels.
/// Aucun lien de téléchargement direct : les fabricants n'en publient pas de stable. On ouvre une recherche limitée à leur
/// site (qui trouve la page du modèle exact), jamais une adresse devinée qui risquerait de ne mener nulle part.
/// </summary>
public static class FirmwareService
{
    private static readonly string[] Placeholders = ["system product name", "default string", "to be filled by o.e.m.", "o.e.m.", "not applicable", "none", "system version"];

    public static Task<BoardInfo?> GetBoardAsync() => Task.Run(() =>
    {
        var board = Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard").FirstOrDefault();
        var system = Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
        var bios = Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        if (board is null && system is null)
            return null;

        // PC de marque : le modèle du PC (« Inspiron 15 3520 ») parle plus que celui de sa carte (un code interne).
        // PC monté : le modèle du PC est un texte vide du constructeur, on garde la carte mère.
        var systemModel = Str(system, "Model");
        var usePc = systemModel is { Length: > 0 } && !IsPlaceholder(systemModel);
        var vendor = CleanVendor(usePc ? Str(system, "Manufacturer") : Str(board, "Manufacturer"));
        var model = usePc ? systemModel! : Str(board, "Product") ?? "";

        DateTime? date = bios?["ReleaseDate"] is string dmtf ? ManagementDateTimeConverter.ToDateTime(dmtf) : null;
        return new BoardInfo(vendor, model.Trim(), Str(board, "Version") is { } v && !IsPlaceholder(v) ? v : "",
            Str(bios, "Manufacturer") ?? "", Str(bios, "SMBIOSBIOSVersion") ?? "", date);
    });

    private static bool IsPlaceholder(string text) => Placeholders.Any(p => text.Trim().Equals(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>« Gigabyte Technology Co., Ltd. » → « Gigabyte » ; « ASUSTeK COMPUTER INC. » → « ASUS » ; etc.</summary>
    public static string CleanVendor(string? raw)
    {
        var text = (raw ?? "").Trim();
        var lower = text.ToLowerInvariant();
        foreach (var (key, name) in KnownVendors)
        {
            if (lower.Contains(key))
                return name;
        }
        foreach (var suffix in new[] { "technology co., ltd.", "co., ltd.", "co.,ltd.", "corporation", "corp.", "inc.", "ltd.", "computer" })
            text = text.Replace(suffix, "", StringComparison.OrdinalIgnoreCase);
        return text.Trim(' ', ',', '.');
    }

    private static readonly (string Key, string Name)[] KnownVendors =
    [
        ("gigabyte", "Gigabyte"), ("asustek", "ASUS"), ("asus", "ASUS"), ("micro-star", "MSI"), ("msi", "MSI"), ("asrock", "ASRock"),
        ("biostar", "Biostar"), ("hewlett", "HP"), ("hp inc", "HP"), ("dell", "Dell"), ("lenovo", "Lenovo"), ("acer", "Acer"),
        ("samsung", "Samsung"), ("toshiba", "Toshiba"), ("dynabook", "Dynabook"), ("razer", "Razer"), ("microsoft", "Microsoft"),
        ("evga", "EVGA"), ("supermicro", "Supermicro"), ("intel", "Intel"), ("huawei", "Huawei"), ("fujitsu", "Fujitsu"), ("lg electronics", "LG"),
    ];

    private static readonly Dictionary<string, string> Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gigabyte"] = "gigabyte.com", ["ASUS"] = "asus.com", ["MSI"] = "msi.com", ["ASRock"] = "asrock.com", ["Biostar"] = "biostar.com.tw",
        ["HP"] = "support.hp.com", ["Dell"] = "dell.com", ["Lenovo"] = "lenovo.com", ["Acer"] = "acer.com", ["Samsung"] = "samsung.com",
        ["Toshiba"] = "dynabook.com", ["Dynabook"] = "dynabook.com", ["Razer"] = "razer.com", ["Microsoft"] = "microsoft.com", ["EVGA"] = "evga.com",
        ["Supermicro"] = "supermicro.com", ["Intel"] = "intel.com", ["Huawei"] = "huawei.com", ["Fujitsu"] = "fujitsu.com",
    };

    /// <summary>Recherche web limitée au site du fabricant : sa page « Support › BIOS » pour ce modèle.</summary>
    public static string BiosSearchUrl(BoardInfo board) => SearchUrl(board, "BIOS download");

    public static string DriversSearchUrl(BoardInfo board) => SearchUrl(board, "drivers download");

    private static string SearchUrl(BoardInfo board, string what)
    {
        var query = $"{board.FullName} {what}";
        if (Domains.TryGetValue(board.Vendor, out var domain))
            query += $" site:{domain}";
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
    }

    /// <summary>Conseil d'après l'âge du BIOS et son nom (null : rien à signaler).</summary>
    public static string? BiosAdvice(BoardInfo board)
    {
        var parts = new List<string>();
        if (board.BiosAgeMonths is >= 12 and var months)
            parts.Add($"Ce BIOS a {months / 12} an{(months >= 24 ? "s" : "")} : une version plus récente corrige souvent des problèmes de stabilité, de sécurité ou de compatibilité (nouveaux processeurs, mémoire).");
        // Chez Gigabyte, F8d = 8e version + « d » : lettre minuscule en suffixe = version bêta
        if (board.Vendor == "Gigabyte" && System.Text.RegularExpressions.Regex.IsMatch(board.BiosVersion, @"^[Ff]\d+[a-z]$"))
            parts.Add("Le suffixe en lettre minuscule désigne en général une version bêta chez Gigabyte.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    // ---------- Chipset ----------

    public static Task<ChipsetInfo> GetChipsetAsync() => Task.Run(() =>
    {
        var cpuVendor = Str(Query("SELECT Manufacturer FROM Win32_Processor").FirstOrDefault(), "Manufacturer") ?? "";
        var amd = cpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        var (name, version) = FindInstalled(n => n.Contains("Chipset", StringComparison.OrdinalIgnoreCase)
                                                 && n.Contains(amd ? "AMD" : "Intel", StringComparison.OrdinalIgnoreCase));
        return new ChipsetInfo(amd ? "AMD" : cpuVendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : cpuVendor, name, version,
            amd ? "https://www.amd.com/en/support/download/drivers.html" : "https://www.intel.com/content/www/us/en/download-center/home.html");
    });

    private static (string? Name, string? Version) FindInstalled(Func<string, bool> match)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root is null)
                continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("DisplayName") is string name && match(name))
                    return (name, key.GetValue("DisplayVersion") as string);
            }
        }
        return (null, null);
    }

    // ---------- Cartes graphiques ----------

    public static Task<List<GpuInfo>> GetGpusAsync() => Task.Run(() =>
    {
        var list = new List<GpuInfo>();
        foreach (var mo in Query("SELECT Name, DriverVersion, DriverDate, PNPDeviceID, AdapterCompatibility FROM Win32_VideoController"))
        {
            // Seulement les vraies cartes (PCI) : pas les écrans virtuels (bureau à distance, Parsec…)
            if (Str(mo, "PNPDeviceID") is not { } pnp || !pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                continue;
            var vendorId = System.Text.RegularExpressions.Regex.Match(pnp, "VEN_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();
            var (vendor, url) = vendorId switch
            {
                "10DE" => ("NVIDIA", "https://www.nvidia.com/en-us/drivers/"),
                "1002" => ("AMD", "https://www.amd.com/en/support/download/drivers.html"),
                "8086" => ("Intel", "https://www.intel.com/content/www/us/en/download-center/home.html"),
                _ => (Str(mo, "AdapterCompatibility") ?? "Fabricant inconnu", "https://www.google.com/search?q=" + Uri.EscapeDataString($"{Str(mo, "Name")} driver download")),
            };
            var version = Str(mo, "DriverVersion") ?? "";
            DateTime? date = mo["DriverDate"] is string dmtf ? ManagementDateTimeConverter.ToDateTime(dmtf) : null;
            list.Add(new GpuInfo(Str(mo, "Name") ?? "Carte graphique", vendor, version, vendor == "NVIDIA" ? NvidiaVersion(version) : version, date, url));
        }
        return list;
    });

    /// <summary>NVIDIA : le pilote Windows « 32.0.15.6094 » est publié sous le nom « 560.94 » (les cinq derniers chiffres).</summary>
    public static string NvidiaVersion(string windowsVersion)
    {
        var parts = windowsVersion.Split('.');
        if (parts.Length < 4)
            return windowsVersion;
        var digits = string.Concat(parts[^2], parts[^1]);
        return digits.Length >= 5 ? $"{digits[^5..^2]}.{digits[^2..]}" : windowsVersion;
    }

    // ---------- Pilotes anciens ----------

    private static readonly HashSet<string> WatchedClasses = new(StringComparer.OrdinalIgnoreCase)
        { "NET", "MEDIA", "BLUETOOTH", "SYSTEM", "DISPLAY", "USB", "HDC", "SCSIADAPTER" };

    /// <summary>
    /// Pilotes de réseau, audio, Bluetooth, chipset, graphique, USB et stockage datés de plus de <paramref name="years"/> ans.
    /// Ceux de Microsoft sont exclus : leur date ancienne est normale, Windows les met à jour avec lui-même.
    /// </summary>
    public static Task<List<OldDriver>> GetOldDriversAsync(int years = 3) => Task.Run(() =>
    {
        var limit = DateTime.Now.AddYears(-years);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<OldDriver>();
        foreach (var mo in Query("SELECT DeviceName, Manufacturer, DriverProviderName, DeviceClass, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL"))
        {
            var provider = Str(mo, "DriverProviderName") ?? Str(mo, "Manufacturer") ?? "";
            var cls = Str(mo, "DeviceClass") ?? "";
            if (!WatchedClasses.Contains(cls) || provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                || mo["DriverDate"] is not string dmtf || Str(mo, "DeviceName") is not { } name)
                continue;
            var date = ManagementDateTimeConverter.ToDateTime(dmtf);
            var version = Str(mo, "DriverVersion") ?? "";
            if (date >= limit || !seen.Add($"{name}|{version}"))
                continue;
            list.Add(new OldDriver(name, provider, Describe(cls), version, date));
        }
        return list.OrderBy(d => d.Date).ToList();
    });

    private static string Describe(string cls) => cls.ToUpperInvariant() switch
    {
        "NET" => "Réseau", "MEDIA" => "Audio", "BLUETOOTH" => "Bluetooth", "SYSTEM" => "Chipset / système", "DISPLAY" => "Graphique",
        "USB" => "USB", "HDC" or "SCSIADAPTER" => "Stockage", var other => other,
    };

    // ---------- WMI ----------

    private static List<ManagementBaseObject> Query(string query)
    {
        var list = new List<ManagementBaseObject>();
        using var searcher = new ManagementObjectSearcher(query);
        foreach (ManagementBaseObject mo in searcher.Get())
            list.Add(mo);
        return list;
    }

    private static string? Str(ManagementBaseObject? mo, string property) =>
        mo?[property] is { } value && !string.IsNullOrWhiteSpace(value.ToString()) ? Convert.ToString(value, CultureInfo.InvariantCulture)!.Trim() : null;
}
