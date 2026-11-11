using NeoUninstaller.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace NeoUninstaller.Services;

/// <summary>
/// Applis Windows (Microsoft Store et préinstallées) de l'utilisateur, via PackageManager.
/// Supprimer une appli la retire pour l'utilisateur courant ; elle reste réinstallable depuis le Microsoft Store.
/// </summary>
public static class WindowsAppsService
{
    /// <summary>Indispensables au bon fonctionnement de Windows : suppression déconseillée (bloquée par défaut).</summary>
    private static readonly string[] Essential =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI",
        "Microsoft.Winget.Source", "Microsoft.WindowsAppRuntime", "Microsoft.ApplicationCompatibilityEnhancements",
        "MicrosoftWindows.Client.CBS",
    ];

    /// <summary>Applis préinstallées que beaucoup de gens n'utilisent pas.</summary>
    private static readonly string[] CommonBloat =
    [
        "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch", "Microsoft.BingTranslator", "Microsoft.GetHelp",
        "Microsoft.Getstarted", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.People", "Microsoft.ZuneVideo",
        "Clipchamp.Clipchamp", "Microsoft.MicrosoftOfficeHub", "Microsoft.Todos", "Microsoft.PowerAutomateDesktop",
        "Microsoft.549981C3F5F10", "Microsoft.MixedReality.Portal", "Microsoft.SkypeApp", "Microsoft.WindowsFeedbackHub",
        "Microsoft.WindowsMaps", "Microsoft.Windows.DevHome", "MicrosoftCorporationII.MicrosoftFamily", "Microsoft.MicrosoftJournal",
        "Microsoft.OutlookForWindows", "MSTeams", "Microsoft.Copilot", "Microsoft.YourPhone", "Microsoft.Messaging",
        "Microsoft.Microsoft3DViewer", "Microsoft.Print3D", "Microsoft.Wallet", "Microsoft.OneConnect",
    ];

    public static Task<List<WindowsApp>> LoadAsync() => Task.Run(() =>
    {
        var manager = new PackageManager();
        var apps = new List<WindowsApp>();
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.SignatureKind == PackageSignatureKind.System)
                    continue;
                var name = package.Id.Name;
                var displayName = package.DisplayName;
                if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    displayName = name;

                apps.Add(new WindowsApp
                {
                    FullName = package.Id.FullName,
                    IdentityName = name,
                    DisplayName = displayName,
                    Publisher = package.PublisherDisplayName,
                    Version = $"{package.Id.Version.Major}.{package.Id.Version.Minor}.{package.Id.Version.Build}",
                    InstallDate = package.InstalledDate.Year > 1601 ? package.InstalledDate.LocalDateTime : null,
                    LogoUri = SafeLogo(package),
                    IsEssential = Essential.Any(e => name.Equals(e, StringComparison.OrdinalIgnoreCase)) || name.Contains("Extension", StringComparison.OrdinalIgnoreCase),
                    IsCommonBloat = CommonBloat.Any(b => name.Equals(b, StringComparison.OrdinalIgnoreCase)),
                });
            }
            catch (Exception)
            {
                // Paquet en cours de mise à jour ou inaccessible : ignoré
            }
        }
        return apps
            .GroupBy(a => a.IdentityName)
            .Select(g => g.OrderByDescending(a => a.Version).First())
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    });

    /// <summary>Supprime l'appli pour l'utilisateur courant (ou pour tous, en administrateur).</summary>
    public static async Task RemoveAsync(WindowsApp app, bool allUsers)
    {
        var manager = new PackageManager();
        var options = allUsers ? RemovalOptions.RemoveForAllUsers : RemovalOptions.None;
        var result = await manager.RemovePackageAsync(app.FullName, options).AsTask();
        if (result.ExtendedErrorCode is not null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.ErrorText) ? result.ExtendedErrorCode.Message : result.ErrorText);
    }

    private static Uri? SafeLogo(Package package)
    {
        try
        {
            return package.Logo;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
