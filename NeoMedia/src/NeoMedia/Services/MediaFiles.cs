namespace NeoMedia.Services;

/// <summary>Formats que NeoMedia propose d'ouvrir (le moteur de VLC en lit bien d'autres).</summary>
public static class MediaFiles
{
    public static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".ogv", ".vob", ".divx", ".rm", ".rmvb", ".asf", ".f4v"];

    public static readonly string[] AudioExtensions =
        [".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".ape", ".mka", ".ac3", ".dts"];

    public static readonly string[] PlaylistExtensions = [".m3u", ".m3u8", ".pls", ".xspf"];

    public static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".smi"];

    public static IEnumerable<string> AllMedia => VideoExtensions.Concat(AudioExtensions).Concat(PlaylistExtensions);

    public static bool IsMedia(string path) => AllMedia.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsSubtitle(string path) => SubtitleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fichiers à lire à partir de chemins déposés ou passés en argument : les dossiers sont parcourus
    /// (fichiers multimédias, triés par nom comme dans l'Explorateur).
    /// </summary>
    public static List<string> Expand(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    result.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .Where(IsMedia)
                        .OrderBy(f => f, NaturalComparer.Instance));
                }
                catch (Exception)
                {
                    // Dossier inaccessible : ignoré
                }
            }
            else if (File.Exists(path) || Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                result.Add(path);
            }
        }
        return result;
    }

    public static string FormatTime(long milliseconds)
    {
        if (milliseconds < 0)
            milliseconds = 0;
        var t = TimeSpan.FromMilliseconds(milliseconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

/// <summary>Tri « naturel » : « Épisode 2 » avant « Épisode 10 ».</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
