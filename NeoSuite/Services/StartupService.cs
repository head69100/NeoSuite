using Microsoft.Win32;

namespace NeoSuite.Services;

/// <summary>Lancement de NeoSuite à l'ouverture de session (clé Run de l'utilisateur, sans droits admin).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NeoSuite";

    /// <summary>Argument passé au démarrage de Windows : NeoSuite démarre caché dans la barre système.</summary>
    public const string BackgroundArg = "--background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {BackgroundArg}");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Met à jour le chemin si l'exécutable a été déplacé depuis l'activation.</summary>
    public static void RefreshPath()
    {
        if (IsEnabled)
            SetEnabled(true);
    }
}
