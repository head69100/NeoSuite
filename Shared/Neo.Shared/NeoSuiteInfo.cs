using Microsoft.Win32;

namespace Neo.Shared;

/// <summary>
/// Où trouver NeoSuite et ses paquets d'installation (HKCU\Software\Neo), pour les applis qui installent
/// d'autres applis Neo (NeoSoft). NeoSuite met ces valeurs à jour à chaque lancement.
/// </summary>
public static class NeoSuiteInfo
{
    private const string Key = @"Software\Neo";

    /// <summary>Exécutable de NeoSuite, capable de (dés)installer une appli Neo en mode « worker ».</summary>
    public static string? SuiteExe => Read("SuiteExe") is { } path && File.Exists(path) ? path : null;

    /// <summary>Fichier catalog.json des paquets disponibles.</summary>
    public static string? PackagesCatalog => Read("PackagesCatalog") is { } path && File.Exists(path) ? path : null;

    public static void Register(string suiteExe, string? packagesCatalog)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("SuiteExe", suiteExe);
            if (packagesCatalog is not null)
                key.SetValue("PackagesCatalog", packagesCatalog);
        }
        catch (Exception)
        {
            // Sans ces valeurs, NeoSoft ne propose simplement pas les applis Neo
        }
    }

    private static string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(name) as string;
    }
}
