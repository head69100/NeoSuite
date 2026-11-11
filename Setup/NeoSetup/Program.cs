using Neo.Shared;

namespace NeoSetup;

/// <summary>
/// NeoSuite.exe : installe NeoSuite (ses fichiers sont collés à la fin du programme, voir InstallerPackage).
/// Les applis se téléchargent ensuite depuis NeoSuite.
/// Copié sans ses données en Program Files\Neo\NeoSuite-Uninstall.exe, il sert de désinstallateur.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Désinstallateur : demandé en argument, ou lancé sous son propre nom depuis « Applications installées »
        var self = Environment.ProcessPath!;
        var uninstall = args.Contains("--uninstall")
            || Path.GetFileNameWithoutExtension(self).Equals("NeoSuite-Uninstall", StringComparison.OrdinalIgnoreCase);

        InstallerPackage? package = null;
        if (!uninstall)
        {
            try
            {
                package = InstallerPackage.Open(self);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Les fichiers de l'installateur sont illisibles :\n\n{ex.Message}\n\nTéléchargez-les à nouveau.",
                    "NeoSuite", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        using (package)
            Application.Run(new SetupForm(package, uninstall));
    }
}
