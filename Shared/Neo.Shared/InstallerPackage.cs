using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Neo.Shared;

/// <summary>Ce que contient un installateur de NeoSuite : sa version et ses nouveautés.</summary>
public sealed record InstallerInfo(Version SuiteVersion, string? Built, IReadOnlyList<string> Notes);

/// <summary>
/// Les fichiers d'un installateur de NeoSuite (archive ZIP : NeoSuite/, Packages/, info.json).
/// L'archive est dans un fichier <c>.pak</c> posé à côté de l'installateur — Windows n'analyse en entier un
/// programme inconnu qu'à son premier lancement, et c'est long sur 700 Mo — ou, à défaut, collée à la fin de
/// l'exécutable, suivie de 16 octets : la position de l'archive (Int64) puis le marqueur « NEOSETUP ».
/// </summary>
public sealed class InstallerPackage : IDisposable
{
    public const string Magic = "NEOSETUP";
    public const string DataExtension = ".pak";

    private readonly FileStream _file;

    public ZipArchive Archive { get; }

    /// <summary>Octets de l'exécutable qui sont le programme lui-même, sans les données (copiés pour faire le désinstallateur).</summary>
    public long StubLength { get; }

    private InstallerPackage(FileStream file, long offset, long length, long stubLength)
    {
        _file = file;
        StubLength = stubLength;
        Archive = new ZipArchive(new SubStream(file, offset, length), ZipArchiveMode.Read);
    }

    /// <summary>Fichier de données associé à un installateur : « NeoSuite.exe » → « NeoSuite.pak ».</summary>
    public static string DataPathFor(string exePath) => Path.ChangeExtension(exePath, DataExtension);

    /// <summary>
    /// Les données de l'installateur <paramref name="exePath"/> : collées à la fin du programme, ou dans le fichier
    /// <c>.pak</c> voisin. Renvoie null s'il n'y en a pas (c'est le désinstallateur, ou un programme quelconque).
    /// </summary>
    public static InstallerPackage? Open(string exePath)
    {
        FileStream? file = null;
        try
        {
            file = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length >= 16)
            {
                var trailer = new byte[16];
                file.Seek(-16, SeekOrigin.End);
                file.ReadExactly(trailer);
                if (Encoding.ASCII.GetString(trailer, 8, 8) == Magic)
                {
                    var offset = BitConverter.ToInt64(trailer, 0);
                    var package = new InstallerPackage(file, offset, file.Length - 16 - offset, offset);
                    file = null;
                    return package;
                }
            }
            var stubLength = file.Length;
            file.Dispose();
            file = null;

            var pak = DataPathFor(exePath);
            return File.Exists(pak) ? OpenData(pak, stubLength) : null;
        }
        finally
        {
            file?.Dispose();
        }
    }

    /// <summary>Un fichier de données <c>.pak</c> seul.</summary>
    public static InstallerPackage OpenData(string pakPath, long stubLength = 0)
    {
        var file = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new InstallerPackage(file, 0, file.Length, stubLength);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Version et nouveautés. Les installateurs d'avant « info.json » n'ont que NeoSuite.exe : on en lit la version.
    /// </summary>
    public InstallerInfo ReadInfo()
    {
        if (Archive.GetEntry("info.json") is { } entry)
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("suiteVersion", out var v) && ParseVersion(v.GetString()) is { } version)
            {
                var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.Array
                    ? n.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList()
                    : [];
                return new InstallerInfo(version, root.TryGetProperty("built", out var b) ? b.GetString() : null, notes);
            }
        }

        var temp = Path.Combine(Path.GetTempPath(), $"neosuite-version-{Guid.NewGuid():N}.exe");
        try
        {
            if (Archive.GetEntry("NeoSuite/NeoSuite.exe") is { } exe)
            {
                exe.ExtractToFile(temp);
                if (ParseVersion(FileVersionInfo.GetVersionInfo(temp).ProductVersion) is { } found)
                    return new InstallerInfo(found, null, []);
            }
        }
        catch (Exception)
        {
            // Version illisible : traitée comme inconnue
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
        return new InstallerInfo(new Version(0, 0, 0, 0), null, []);
    }

    /// <summary>Applis proposées (nom → version), lues dans Packages/catalog.json.</summary>
    public IReadOnlyList<(string Name, string Version)> Apps()
    {
        var entry = Archive.GetEntry("Packages/catalog.json");
        if (entry is null)
            return [];
        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateObject()
            .Select(p => (p.Name, p.Value.TryGetProperty("version", out var v) ? v.GetString() ?? "" : ""))
            .OrderBy(a => a.Name)
            .ToList();
    }

    /// <summary>« 0.2.0 », « 0.2.0+abc123 » → <c>0.2.0.0</c> (null si illisible).</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var clean = text.Split('+', '-')[0].Trim();
        return Version.TryParse(clean, out var version) ? Normalize(version) : null;
    }

    /// <summary>Complète les parties absentes : sinon 0.2.0 (révision -1) serait inférieure à 0.2.0.0.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public void Dispose()
    {
        Archive.Dispose();
        _file.Dispose();
    }

    /// <summary>Vue en lecture seule d'une portion d'un fichier.</summary>
    private sealed class SubStream(Stream inner, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _position;
            if (remaining <= 0)
                return 0;
            inner.Position = start + _position;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
