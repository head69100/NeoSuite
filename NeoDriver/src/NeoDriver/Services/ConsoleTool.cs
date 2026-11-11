using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NeoDriver.Services;

/// <summary>
/// Lance un programme console de Windows (pnputil…) et lit sa sortie dans la page de code ANSI du système
/// (Windows-1252 en français) : redirigée, la sortie de pnputil n'est ni en UTF-8 ni dans la page OEM de la console,
/// et sinon les accents sortent en « � » ou en caractères de dessin de tableau.
/// </summary>
public static class ConsoleTool
{
    static ConsoleTool() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    public static Encoding Ansi => Encoding.GetEncoding((int)GetACP());

    /// <summary>Sans droits particuliers : renvoie le code de sortie et tout ce que le programme a écrit.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Ansi,
            StandardErrorEncoding = Ansi,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Impossible de lancer {exe}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
}
