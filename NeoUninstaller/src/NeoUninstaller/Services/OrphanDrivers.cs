using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace NeoUninstaller.Services;

public sealed record DriverPackage(string PublishedName, string OriginalName, string Provider, string ClassName, string Version, DateTime? Date)
{
    public string Description => $"{ClassName} · {Provider} · version {Version}" + (Date is { } d ? $" · {d:dd/MM/yyyy}" : "");
}

/// <summary>
/// Pilotes orphelins : paquets de pilotes tiers du magasin Windows (oemNN.inf) qu'aucun périphérique n'utilise plus
/// (matériel débranché, ancienne version remplacée). Leur suppression libère de la place et ne touche pas au matériel branché.
/// </summary>
public static partial class OrphanDrivers
{
    public static Task<List<DriverPackage>> FindAsync() => Task.Run(() =>
    {
        var used = UsedInfNames();
        return Enumerate()
            .Where(p => !used.Contains(p.PublishedName))
            .Where(p => !p.Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.ClassName).ThenBy(p => p.Provider)
            .ToList();
    });

    /// <summary>
    /// Tous les paquets référencés par un appareil, même déconnecté ou masqué.
    /// On lit pnputil /enum-devices (inventaire complet) et on complète avec WMI :
    /// ainsi un pilote encore attribué à un matériel présent ou non n'est jamais proposé à la suppression.
    /// </summary>
    private static HashSet<string> UsedInfNames()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            using var p = Process.Start(new ProcessStartInfo("pnputil.exe", "/enum-devices /drivers")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = ansi,
            })!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (Match m in UsedInfRegex().Matches(text))
                used.Add(m.Value);
        }
        catch (Exception)
        {
            // pnputil indisponible : on s'en remet à WMI ci-dessous
        }
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT InfName FROM Win32_PnPSignedDriver");
            using var rows = searcher.Get();
            foreach (ManagementBaseObject row in rows)
                if (row["InfName"] is string inf && inf.Length > 0)
                    used.Add(inf);
        }
        catch (Exception)
        {
        }
        return used;
    }

    /// <summary>pnputil /enum-drivers : on lit les blocs par position, les libellés dépendent de la langue de Windows.</summary>
    private static List<DriverPackage> Enumerate()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        using var p = Process.Start(new ProcessStartInfo("pnputil.exe", "/enum-drivers")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            StandardOutputEncoding = ansi,
        })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        var packages = new List<DriverPackage>();
        List<string>? block = null;
        void Flush()
        {
            if (block is { Count: >= 6 })
            {
                DateTime? date = null;
                var version = block[5];
                var m = VersionRegex().Match(version);
                if (m.Success)
                {
                    date = DateTime.TryParseExact(m.Groups[1].Value, ["MM/dd/yyyy", "dd/MM/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                    version = m.Groups[2].Value;
                }
                packages.Add(new DriverPackage(block[0], block[1], block[2], block[3], version, date));
            }
            block = null;
        }

        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                if (line.Trim().Length == 0)
                    Flush();
                continue;
            }
            var value = line[(colon + 1)..].Trim();
            if (OemRegex().IsMatch(value))
            {
                Flush();
                block = [];
            }
            block?.Add(value);
        }
        Flush();
        return packages;
    }

    /// <summary>Supprime un paquet du magasin (refusé par Windows s'il est encore utilisé). Administrateur requis.</summary>
    public static Task<(bool Ok, string Message)> DeleteAsync(DriverPackage package) => Task.Run(() =>
    {
        using var p = Process.Start(new ProcessStartInfo("pnputil.exe", $"/delete-driver {package.PublishedName} /uninstall")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode == 0, output.Trim());
    });

    [GeneratedRegex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex OemRegex();

    [GeneratedRegex(@"oem\d+\.inf", RegexOptions.IgnoreCase)]
    private static partial Regex UsedInfRegex();

    [GeneratedRegex(@"^(\d+/\d+/\d+)\s+(\S+)")]
    private static partial Regex VersionRegex();
}