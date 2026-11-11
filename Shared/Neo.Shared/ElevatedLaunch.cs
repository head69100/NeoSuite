using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace Neo.Shared;

/// <summary>
/// Démarrage d'une appli Neo en administrateur sans invite UAC : une tâche planifiée « niveau le plus élevé »
/// est créée une fois (avec l'accord UAC, depuis NeoSuite ou l'appli), puis l'appli est lancée à travers elle.
/// La tâche porte le nom de l'appli et pointe vers sa copie installée (Program Files\Neo\&lt;App&gt;).
/// </summary>
public static class ElevatedLaunch
{
    public static bool IsAdministrator { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static string InstallDir(string appId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Neo", appId);

    private static string InstalledExe(string appId) => Path.Combine(InstallDir(appId), $"{appId}.exe");

    private static string ArgsFile(string appId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appId, "launch-args.txt");

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Le démarrage en administrateur est-il activé pour cette appli ?</summary>
    public static bool IsEnabled(string appId) => RunSchtasks($"/Query /TN \"{appId}\"") == 0;

    /// <summary>Crée ou met à jour la tâche (droits administrateur requis).</summary>
    public static void Register(string appId)
    {
        var exe = InstalledExe(appId);
        if (!File.Exists(exe))
            throw new FileNotFoundException($"{appId} n'est pas installé.", exe);

        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Démarre {appId} avec les droits administrateur sans invite UAC (suite Neo).</Description>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(InstallDir(appId))}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var xmlPath = Path.Combine(Path.GetTempPath(), $"{appId}-task.xml");
        File.WriteAllText(xmlPath, xml, Encoding.Unicode);
        try
        {
            if (RunSchtasks($"/Create /TN \"{appId}\" /XML \"{xmlPath}\" /F") != 0)
                throw new InvalidOperationException("Windows a refusé la création de la tâche de démarrage.");
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    /// <summary>Supprime la tâche (droits administrateur requis). Sans effet si elle n'existe pas.</summary>
    public static void Unregister(string appId)
    {
        if (IsEnabled(appId) && RunSchtasks($"/Delete /TN \"{appId}\" /F") != 0)
            throw new InvalidOperationException("Windows a refusé la suppression de la tâche de démarrage.");
    }

    /// <summary>
    /// Lance l'appli en administrateur via sa tâche, sans UAC, en lui transmettant <paramref name="arguments"/>
    /// (une ligne de commande). Renvoie false si la tâche n'existe pas.
    /// </summary>
    public static bool TryLaunch(string appId, string arguments)
    {
        // La tâche ne peut pas recevoir d'arguments : ils passent par un fichier
        var argsFile = ArgsFile(appId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(argsFile)!);
            File.WriteAllText(argsFile, arguments);
        }
        catch (IOException)
        {
            return false;
        }

        AllowSetForegroundWindow(-1); // ASFW_ANY : la nouvelle fenêtre pourra passer au premier plan
        if (RunSchtasks($"/Run /TN \"{appId}\"") == 0)
            return true;

        TryDelete(argsFile);
        return false;
    }

    /// <summary>
    /// À appeler au démarrage de l'appli. Renvoie les arguments à utiliser, ou null si l'appli vient
    /// d'être relancée en administrateur via sa tâche (l'instance actuelle doit alors se fermer).
    /// </summary>
    public static string[]? HandleStartup(string appId)
    {
        var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // Lancée par la tâche : jamais de nouvelle relance, même si Windows n'a pas accordé les droits
        if (ConsumeLaunchArgs(appId) is { } forwarded)
            return [.. cmdArgs, .. forwarded];

        if (!IsAdministrator && IsInstalledCopy(appId) && TryLaunch(appId, JoinArgs(cmdArgs)))
            return null;

        return cmdArgs;
    }

    /// <summary>
    /// Le processus actuel est-il la copie installée de l'appli ? La tâche lance toujours celle-ci :
    /// une version de développement ne doit pas basculer vers elle.
    /// </summary>
    public static bool IsInstalledCopy(string appId) =>
        string.Equals(Environment.ProcessPath, InstalledExe(appId), StringComparison.OrdinalIgnoreCase);

    public static string JoinArgs(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length > 0 && !a.Any(c => c is ' ' or '\t' or '"') ? a : $"\"{a.Replace("\"", "\\\"")}\""));

    /// <summary>Arguments transmis par l'instance qui a lancé la tâche, ou null si l'appli n'a pas été lancée ainsi.</summary>
    private static string[]? ConsumeLaunchArgs(string appId)
    {
        var argsFile = ArgsFile(appId);
        try
        {
            if (!File.Exists(argsFile))
                return null;
            var fresh = DateTime.Now - File.GetLastWriteTime(argsFile) < TimeSpan.FromMinutes(1);
            var text = File.ReadAllText(argsFile);
            File.Delete(argsFile);
            return fresh ? SplitArgs(text) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string[] SplitArgs(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];
        // Préfixe factice : CommandLineToArgvW traite le premier élément comme le nom du programme
        var argv = CommandLineToArgvW("x " + commandLine, out var argc);
        if (argv == IntPtr.Zero)
            return [];
        try
        {
            return Enumerable.Range(1, argc - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static int RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
