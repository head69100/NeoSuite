using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace NeoUninstaller.Services;

public sealed record TaskInfo(string Name, string Command);

/// <summary>Tâches planifiées de Windows, lues et supprimées avec schtasks.exe (sans droits pour la lecture).</summary>
public static partial class ScheduledTasks
{
    private static readonly Encoding Oem = ConsoleEncoding();

    /// <summary>Toutes les tâches visibles, avec la première commande lancée par chacune.</summary>
    public static List<TaskInfo> List()
    {
        var output = Run("/Query /XML ONE");
        var tasks = new List<TaskInfo>();
        foreach (var block in output.Split("<?xml", StringSplitOptions.RemoveEmptyEntries))
        {
            var uri = UriRegex().Match(block);
            if (!uri.Success)
                continue;
            var command = CommandRegex().Match(block);
            tasks.Add(new TaskInfo(uri.Groups[1].Value.Trim(), command.Success ? System.Net.WebUtility.HtmlDecode(command.Groups[1].Value.Trim()) : ""));
        }
        return tasks;
    }

    public static HashSet<string> Names() =>
        List().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Exists(string name) => Names().Contains(name);

    /// <summary>Définition XML d'une tâche, pour pouvoir la recréer.</summary>
    public static string? Export(string name)
    {
        var xml = Run($"/Query /TN \"{name}\" /XML");
        return xml.Contains("<Task", StringComparison.Ordinal) ? xml : null;
    }

    public static void Delete(string name) => Run($"/Delete /TN \"{name}\" /F", throwOnError: true);

    public static void Create(string name, string xmlFile) => Run($"/Create /TN \"{name}\" /XML \"{xmlFile}\" /F", throwOnError: true);

    private static string Run(string args, bool throwOnError = false)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Oem,
                StandardErrorEncoding = Oem,
            })!;
            var errors = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 && throwOnError)
                throw new InvalidOperationException(errors.Result.Trim().Length > 0 ? errors.Result.Trim() : $"schtasks a échoué (code {p.ExitCode}).");
            return output;
        }
        catch (InvalidOperationException) when (!throwOnError)
        {
            return "";
        }
    }

    private static Encoding ConsoleEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    [GeneratedRegex(@"<URI>(.*?)</URI>", RegexOptions.Singleline)]
    private static partial Regex UriRegex();

    [GeneratedRegex(@"<Command>(.*?)</Command>", RegexOptions.Singleline)]
    private static partial Regex CommandRegex();
}