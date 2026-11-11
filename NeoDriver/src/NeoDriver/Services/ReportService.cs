using System.Management;
using System.Text;

namespace NeoDriver.Services;

/// <summary>Rapport du PC en texte : matériel, BIOS, pilotes installés et périphériques en erreur — à joindre quand on demande de l'aide.</summary>
public static class ReportService
{
    public static async Task<string> BuildAsync()
    {
        var sb = new StringBuilder();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        sb.AppendLine($"Rapport NeoDriver {version} · {Environment.MachineName} · {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Système : {SystemInfoService.GetOsName()} (build {SystemInfoService.GetOsBuild()}, {SystemInfoService.GetArchitecture()})");

        if (await FirmwareService.GetBoardAsync() is { } board)
        {
            sb.AppendLine().AppendLine("=== CARTE MÈRE ET BIOS ===");
            sb.AppendLine($"  Carte mère / PC   {board.FullName}{(board.Revision.Length > 0 ? " (rév. " + board.Revision + ")" : "")}");
            sb.AppendLine($"  BIOS              {board.BiosVendor} {board.BiosVersion}{(board.BiosDate is { } d ? " du " + d.ToString("dd/MM/yyyy") : "")}");
        }

        foreach (var section in await HardwareInfoService.GetAsync())
        {
            sb.AppendLine().AppendLine($"=== {section.Title.ToUpperInvariant()} ===");
            foreach (var group in section.Groups)
            {
                sb.AppendLine().AppendLine($"[{group.Title}]");
                foreach (var row in group.Rows)
                    sb.AppendLine($"  {row.Key,-26}{row.Value}");
            }
        }

        var problems = await DeviceHealthService.GetProblemsAsync();
        sb.AppendLine().AppendLine("=== PÉRIPHÉRIQUES EN ERREUR ===");
        if (problems.Count == 0)
            sb.AppendLine("  Aucun.");
        foreach (var p in problems)
            sb.AppendLine($"  {p.Name} · code {p.ErrorCode} · {p.Explanation} · {p.MainId}");

        var drivers = await Task.Run(ReadDrivers);
        sb.AppendLine().AppendLine($"=== PILOTES INSTALLÉS ({drivers.Count}) ===");
        sb.AppendLine($"  {"Classe",-14}{"Périphérique",-52}{"Fabricant",-30}{"Version",-18}{"Date",-12}Fichier");
        foreach (var d in drivers)
            sb.AppendLine($"  {Cut(d.Class, 13),-14}{Cut(d.Name, 51),-52}{Cut(d.Maker, 29),-30}{Cut(d.Version, 17),-18}{d.Date,-12}{d.Inf}");
        return sb.ToString();
    }

    private sealed record Row(string Class, string Name, string Maker, string Version, string Date, string Inf);

    private static List<Row> ReadDrivers()
    {
        var list = new List<Row>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceName, DeviceClass, Manufacturer, DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL");
        using var results = searcher.Get();
        foreach (ManagementBaseObject mo in results)
        {
            list.Add(new Row(
                mo["DeviceClass"] as string ?? "",
                mo["DeviceName"] as string ?? "",
                mo["Manufacturer"] as string ?? "",
                mo["DriverVersion"] as string ?? "",
                mo["DriverDate"] is string dmtf ? ManagementDateTimeConverter.ToDateTime(dmtf).ToString("dd/MM/yyyy") : "",
                mo["InfName"] as string ?? ""));
        }
        return list.OrderBy(r => r.Class).ThenBy(r => r.Name).ToList();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
