using NeoUninstaller.Models;
using NeoUninstaller.Services;

namespace NeoUninstaller;

/// <summary>Données partagées entre les pages : la liste des programmes installés.</summary>
public static class AppState
{
    public static List<InstalledProgram> Programs { get; private set; } = [];

    private static Task<List<InstalledProgram>>? _loading;

    /// <summary>Recharge la liste (une seule lecture du registre à la fois).</summary>
    public static Task<List<InstalledProgram>> RefreshAsync()
    {
        return _loading ??= LoadAsync();

        static async Task<List<InstalledProgram>> LoadAsync()
        {
            try
            {
                Programs = await ProgramInventory.LoadAsync();
                return Programs;
            }
            finally
            {
                _loading = null;
            }
        }
    }

    /// <summary>Un programme a été désinstallé : relire le registre au prochain affichage de la liste.</summary>
    public static bool ListIsStale { get; set; }

    /// <summary>Programme à désinstaller dès le démarrage (après une relance en administrateur).</summary>
    public static string? PendingUninstallKey { get; set; }
}
