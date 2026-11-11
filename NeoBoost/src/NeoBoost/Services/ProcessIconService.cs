using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace NeoBoost.Services;

/// <summary>Icônes des programmes actifs, extraites de leur fichier exécutable.</summary>
public static class ProcessIconService
{
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Throttle = new(4);

    /// <summary>Charge l'icône en arrière-plan puis l'affecte au groupe (à appeler depuis le thread UI).</summary>
    public static async Task LoadAsync(Models.ProcessGroup group, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return;
        if (await GetAsync(executablePath) is { } image)
            group.Icon = image;
    }

    /// <summary>Icône de l'exécutable, ou null. À appeler depuis le thread UI.</summary>
    public static async Task<Microsoft.UI.Xaml.Media.ImageSource?> GetAsync(string path)
    {
        byte[]? png;
        await Throttle.WaitAsync();
        try
        {
            png = await Task.Run(() => Cache.GetOrAdd(path, _ => ExtractPng(path)));
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

    private static byte[]? ExtractPng(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var large = new IntPtr[1];
            if (ExtractIconEx(path, 0, large, null, 1) == 0 || large[0] == IntPtr.Zero)
                return null;
            try
            {
                using var icon = Icon.FromHandle(large[0]);
                using var bitmap = icon.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                return ms.ToArray();
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
