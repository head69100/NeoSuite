using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace NeoRar.Services;

/// <summary>Icône Windows d'un type de fichier (par son extension, sans que le fichier existe), mise en cache.</summary>
public static class FileIconService
{
    private static readonly ConcurrentDictionary<string, byte[]?> Png = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource> Images = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>À appeler depuis le thread UI.</summary>
    public static async Task<ImageSource?> GetAsync(string fileName, bool isFolder)
    {
        var key = isFolder ? "<dossier>" : Path.GetExtension(fileName) is { Length: > 0 } ext ? ext : "<fichier>";
        if (Images.TryGetValue(key, out var cached))
            return cached;

        var png = await Task.Run(() => Png.GetOrAdd(key, _ => Extract(isFolder, key)));
        if (png is null)
            return null;
        if (Images.TryGetValue(key, out cached))
            return cached;

        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        await image.SetSourceAsync(stream.AsRandomAccessStream());
        Images[key] = image;
        return image;
    }

    private static byte[]? Extract(bool isFolder, string extension)
    {
        try
        {
            var info = new ShFileInfo();
            var name = isFolder ? "dossier" : "fichier" + (extension.StartsWith('.') ? extension : "");
            var attributes = isFolder ? 0x10u /* FILE_ATTRIBUTE_DIRECTORY */ : 0x80u /* FILE_ATTRIBUTE_NORMAL */;
            const uint flags = 0x100 /* SHGFI_ICON */ | 0x0 /* SHGFI_LARGEICON */ | 0x10 /* SHGFI_USEFILEATTRIBUTES */;
            if (SHGetFileInfo(name, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags) == IntPtr.Zero || info.Icon == IntPtr.Zero)
                return null;
            try
            {
                using var icon = Icon.FromHandle(info.Icon);
                using var bitmap = icon.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            finally
            {
                DestroyIcon(info.Icon);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>Type de fichier tel que Windows le décrit (« Document texte »…), mis en cache par extension.</summary>
    public static string GetTypeName(string fileName, bool isFolder)
    {
        if (isFolder)
            return "Dossier de fichiers";
        var ext = Path.GetExtension(fileName);
        return TypeNames.GetOrAdd(ext, e =>
        {
            var info = new ShFileInfo();
            const uint flags = 0x400 /* SHGFI_TYPENAME */ | 0x10 /* SHGFI_USEFILEATTRIBUTES */;
            SHGetFileInfo("fichier" + e, 0x80, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
            return string.IsNullOrEmpty(info.TypeName) ? (e.Length > 1 ? $"Fichier {e[1..].ToUpperInvariant()}" : "Fichier") : info.TypeName;
        });
    }

    private static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);
}
