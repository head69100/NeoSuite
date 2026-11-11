using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>Icônes des programmes, extraites de DisplayIcon (ou de l'exécutable du désinstalleur).</summary>
public static class IconService
{
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Throttle = new(4);

    /// <summary>Charge l'icône en arrière-plan puis l'affecte au programme (à appeler depuis le thread UI).</summary>
    public static async Task LoadAsync(InstalledProgram program)
    {
        var (path, index) = ResolveIconSource(program);
        if (path is not null && await GetAsync(path, index) is { } image)
            program.Icon = image;
    }

    /// <summary>Icône d'un fichier (exe, dll, ico), ou null. À appeler depuis le thread UI.</summary>
    public static async Task<Microsoft.UI.Xaml.Media.ImageSource?> GetAsync(string path, int index = 0)
    {
        byte[]? png;
        await Throttle.WaitAsync();
        try
        {
            png = await Task.Run(() => Cache.GetOrAdd($"{path},{index}", _ => ExtractPng(path, index)));
        }
        finally
        {
            Throttle.Release();
        }
        if (png is null)
            return null;

        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        await image.SetSourceAsync(stream.AsRandomAccessStream());
        return image;
    }

    private static (string? Path, int Index) ResolveIconSource(InstalledProgram program)
    {
        foreach (var candidate in new[] { program.DisplayIcon, program.UninstallString })
        {
            var (path, index) = ParseIconLocation(candidate);
            if (path is not null && File.Exists(path) && !path.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                return (path, index);
        }
        return (null, 0);
    }

    /// <summary>« "C:\app\app.exe",0 » ou « C:\app\app.exe -uninstall » → chemin + index d'icône.</summary>
    internal static (string? Path, int Index) ParseIconLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, 0);
        value = Environment.ExpandEnvironmentVariables(value.Trim());

        string path;
        string rest;
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            path = end > 0 ? value[1..end] : value.Trim('"');
            rest = end > 0 ? value[(end + 1)..] : "";
        }
        else
        {
            var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var ico = value.IndexOf(".ico", StringComparison.OrdinalIgnoreCase);
            var cut = exe >= 0 ? exe + 4 : ico >= 0 ? ico + 4 : value.Length;
            var comma = value.LastIndexOf(',');
            if (exe < 0 && ico < 0 && comma > 0)
                cut = comma;
            path = value[..cut];
            rest = value[cut..];
        }

        var index = 0;
        var c = rest.IndexOf(',');
        if (c >= 0 && int.TryParse(rest[(c + 1)..].Trim(), out var i))
            index = i;
        return (path.Trim(), index);
    }

    private static byte[]? ExtractPng(string path, int index)
    {
        try
        {
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var ico = new Icon(path, 32, 32);
                return ToPng(ico.ToBitmap());
            }

            var large = new IntPtr[1];
            if (ExtractIconEx(path, index, large, null, 1) == 0 || large[0] == IntPtr.Zero)
                return null;
            try
            {
                using var icon = Icon.FromHandle(large[0]);
                return ToPng(icon.ToBitmap());
            }
            finally
            {
                DestroyIcon(large[0]);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using (bitmap)
        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
