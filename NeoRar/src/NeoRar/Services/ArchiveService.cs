using System.Text.RegularExpressions;
using SevenZip;

namespace NeoRar.Services;

public enum ArchiveKind
{
    SevenZip,
    Zip,
    Tar,
}

/// <summary>Un fichier ou dossier contenu dans une archive.</summary>
public sealed record ArchiveItem(int Index, string Path, bool IsDirectory, ulong Size, DateTime Modified, bool Encrypted);

/// <summary>Contenu et caractéristiques d'une archive ouverte.</summary>
public sealed record ArchiveInfo(
    string FilePath,
    string Format,
    bool IsSolid,
    long PackedSize,
    long UnpackedSize,
    bool HasEncryptedFiles,
    IReadOnlyList<ArchiveItem> Items,
    int VolumeCount = 1,
    long ArchiveSize = 0)
{
    /// <summary>On peut y ajouter ou supprimer des fichiers (formats 7z et ZIP).</summary>
    public bool CanModify => Format is "SevenZip" or "Zip";
}

/// <summary>Options de création d'une archive.</summary>
/// <param name="VolumeSize">Taille des volumes en octets (0 = archive en un seul fichier ; 7z uniquement).</param>
public sealed record CompressOptions(ArchiveKind Kind, CompressionLevel Level, string? Password, bool EncryptNames, long VolumeSize = 0);

/// <summary>Résultat d'une vérification d'intégrité (CRC) : conforme ou non, éléments contrôlés, fichiers endommagés.</summary>
public sealed record TestResult(bool Ok, int Checked, IReadOnlyList<string> Damaged);

/// <summary>
/// Progression à afficher : pourcentage (0-99), phase à afficher si elle change (null = inchangée), et barre
/// animée quand la mesure n'est plus fiable — le pourcentage du moteur est basé sur les octets lus, ce qui
/// atteint 100 % en un instant sur un gros fichier unique alors que la compression continue encore ~10 s.
/// </summary>
public sealed record ProgressInfo(double Percent, string? Phase, bool Indeterminate);

/// <summary>L'archive est protégée et le mot de passe est absent ou faux.</summary>
public sealed class PasswordRequiredException(string message) : Exception(message);

/// <summary>
/// Toutes les opérations sur les archives, via le moteur de 7-Zip (7z.dll) : lecture de RAR/RAR5, 7z, ZIP,
/// TAR, GZ, BZ2, XZ, ISO, CAB…, création de 7z, ZIP et TAR, chiffrement AES-256.
/// </summary>
public static class ArchiveService
{
    public static readonly string[] SupportedExtensions =
        [".rar", ".zip", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".zst", ".iso", ".cab", ".wim", ".lzh", ".arj", ".z", ".001"];

    private static readonly object InitLock = new();
    private static bool _initialized;

    private static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized)
                return;
            var dll = new[] { Path.Combine(AppContext.BaseDirectory, "x64", "7z.dll"), Path.Combine(AppContext.BaseDirectory, "7z.dll") }
                .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Le moteur de compression (7z.dll) est introuvable : réinstallez NeoRar.");
            SevenZipBase.SetLibraryPath(dll);
            _initialized = true;
        }
    }

    public static bool IsArchive(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(path, @"\.(part\d+\.rar|r\d\d|z\d\d)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Lit la liste des fichiers. Lève <see cref="PasswordRequiredException"/> si les noms eux-mêmes sont chiffrés.</summary>
    public static Task<ArchiveInfo> OpenAsync(string path, string? password) => Task.Run(() =>
    {
        EnsureInitialized();
        EnsureExists(path);
        try
        {
            using var extractor = Create(path, password);
            var items = extractor.ArchiveFileData
                .Select(f => new ArchiveItem(f.Index, Normalize(f.FileName), f.IsDirectory, f.Size, f.LastWriteTime, f.Encrypted))
                .ToList();

            // En volumes, la liste des fichiers .001/.002… donne le nombre de tomes et la taille réelle sur disque
            var volumes = 1;
            var archiveSize = 0L;
            try
            {
                var parts = extractor.VolumeFileNames;
                if (parts is { Count: > 1 })
                {
                    volumes = parts.Count;
                    archiveSize = parts.Sum(p => new FileInfo(p).Length);
                }
                else
                {
                    archiveSize = new FileInfo(path).Length;
                }
            }
            catch (Exception)
            {
                // Taille indisponible pour ce format : la ligne d'état affichera le reste
            }

            return new ArchiveInfo(path, extractor.Format.ToString(), extractor.IsSolid, extractor.PackedSize, extractor.UnpackedSize,
                items.Any(i => i.Encrypted), items, volumes, archiveSize);
        }
        catch (Exception ex) when (ex is not PasswordRequiredException && LooksLikePasswordProblem(ex, password, path))
        {
            throw new PasswordRequiredException(password is null
                ? "Cette archive est protégée par un mot de passe."
                : "Mot de passe incorrect.");
        }
        catch (SevenZipException ex)
        {
            throw new InvalidOperationException(Explain(ex), ex);
        }
    });

    /// <summary>
    /// Extrait toute l'archive (<paramref name="indexes"/> null) ou certains éléments dans <paramref name="destination"/>.
    /// </summary>
    public static Task ExtractAsync(string path, string? password, string destination, IReadOnlyCollection<int>? indexes,
        bool overwrite, IProgress<ProgressInfo>? progress, CancellationToken cancel) => Task.Run(() =>
    {
        EnsureInitialized();
        EnsureExists(path);
        Directory.CreateDirectory(destination);
        try
        {
            using var extractor = Create(path, password);
            extractor.Extracting += (_, e) => progress?.Report(new ProgressInfo(Math.Min((double)e.PercentDone, 99), null, e.PercentDone >= 99));
            extractor.FileExtractionStarted += (_, e) =>
            {
                if (cancel.IsCancellationRequested)
                    e.Cancel = true;
            };
            if (!overwrite)
                extractor.FileExists += (_, e) => e.FileName = null; // fichier déjà présent : on le garde

            if (indexes is null)
                extractor.ExtractArchive(destination);
            else
                extractor.ExtractFiles(destination, [.. indexes]);
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested && LooksLikePasswordProblem(ex, password, path))
        {
            throw new PasswordRequiredException(password is null ? "Cette archive est protégée par un mot de passe." : "Mot de passe incorrect.");
        }
        catch (SevenZipException ex) when (!cancel.IsCancellationRequested)
        {
            throw new InvalidOperationException(Explain(ex), ex);
        }
        cancel.ThrowIfCancellationRequested();
    }, CancellationToken.None);

    /// <summary>
    /// Vérifie l'intégrité (CRC) : toute l'archive (<paramref name="indexes"/> null), ou seulement les éléments
    /// demandés — extraits vers un flux vide, ce qui ne charge rien en mémoire.
    /// </summary>
    public static Task<TestResult> TestAsync(string path, string? password, IReadOnlyCollection<int>? indexes = null) => Task.Run(() =>
    {
        EnsureInitialized();
        EnsureExists(path);
        try
        {
            using var extractor = Create(path, password);
            if (indexes is null)
                return new TestResult(extractor.Check(), -1, Array.Empty<string>());

            var data = extractor.ArchiveFileData.ToDictionary(f => f.Index);
            var damaged = new List<string>();
            var checkedCount = 0;
            foreach (var index in indexes)
            {
                // Dossiers (sans données) et fichiers chiffrés sans mot de passe (impossibles à contrôler) : passés
                if (!data.TryGetValue(index, out var info) || info.IsDirectory || (info.Encrypted && string.IsNullOrEmpty(password)))
                    continue;
                checkedCount++;
                try
                {
                    extractor.ExtractFile(index, Stream.Null); // le moteur compare le CRC au fil de la lecture
                }
                catch (SevenZipException)
                {
                    damaged.Add(Normalize(info.FileName));
                }
            }
            return new TestResult(damaged.Count == 0, checkedCount, damaged);
        }
        catch (Exception ex) when (LooksLikePasswordProblem(ex, password, path))
        {
            throw new PasswordRequiredException(password is null ? "Cette archive est protégée par un mot de passe." : "Mot de passe incorrect.");
        }
    });

    /// <summary>Crée une archive à partir de fichiers et de dossiers (les dossiers sont ajoutés avec leur contenu).</summary>
    public static Task CompressAsync(IReadOnlyList<string> sources, string archivePath, CompressOptions options,
        IProgress<ProgressInfo>? progress, CancellationToken cancel) => Task.Run(() =>
    {
        progress?.Report(new ProgressInfo(0, "Analyse des fichiers…", true));
        EnsureInitialized();
        var entries = BuildEntries(sources, "", n => progress?.Report(new ProgressInfo(0, $"Analyse des fichiers… ({n:n0} fichiers)", true)));
        progress?.Report(new ProgressInfo(0, "Compression en cours…", true));
        var temp = archivePath + ".neorar-tmp";
        var folder = Path.GetDirectoryName(archivePath)!;
        var fileName = Path.GetFileName(archivePath);
        try
        {
            var compressor = CreateCompressor(options, cancel, progress);
            compressor.CompressFileDictionary(entries, temp, string.IsNullOrEmpty(options.Password) ? null : options.Password);
            cancel.ThrowIfCancellationRequested();

            if (options.VolumeSize > 0 && options.Kind == ArchiveKind.SevenZip)
            {
                // En volumes, le moteur écrit « temp.001 », « temp.002 »… : on efface les volumes d'une éventuelle
                // archive antérieure au même emplacement, puis on retire le suffixe temporaire des nouveaux tomes.
                foreach (var old in PartFiles(folder, fileName))
                    TryDelete(old);
                if (File.Exists(archivePath))
                    TryDelete(archivePath);
                var produced = PartFiles(folder, Path.GetFileName(temp));
                if (produced.Count == 0)
                    throw new InvalidOperationException("Le moteur n'a produit aucun tome : réessayez, ou changez le format en 7z.");
                foreach (var part in produced)
                    File.Move(part, Regex.Replace(part, @"\.neorar-tmp(\.\d{3})$", "$1", RegexOptions.IgnoreCase), overwrite: true);
            }
            else
            {
                // Sans volume : l'archive remplace l'ancienne — ses volumes d'avant aussi, s'il y en avait
                foreach (var old in PartFiles(folder, fileName))
                    TryDelete(old);
                File.Move(temp, archivePath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temp))
                TryDelete(temp);
            foreach (var part in PartFiles(folder, Path.GetFileName(temp)))
                TryDelete(part);
        }
    }, CancellationToken.None);

    /// <summary>Les fichiers « nom.001 », « nom.002 »… d'une archive en volumes (partagé avec l'affichage du résultat).</summary>
    internal static List<string> PartFiles(string folder, string fileName) =>
        Directory.EnumerateFiles(folder, fileName + ".*")
            .Where(f => Regex.IsMatch(Path.GetFileName(f), "^" + Regex.Escape(fileName) + @"\.\d{3}$"))
            .ToList();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Fichier ouvert ailleurs : les vieux volumes seront nettoyés à la prochaine écriture
        }
    }

    /// <summary>
    /// Ajoute des fichiers à une archive 7z ou ZIP existante, dans le dossier <paramref name="folder"/>
    /// de l'archive (chemin « a/b », vide pour la racine).
    /// </summary>
    public static Task AddAsync(ArchiveInfo archive, IReadOnlyList<string> sources, string folder, string? password,
        IProgress<ProgressInfo>? progress, CancellationToken cancel) => Task.Run(() =>
    {
        progress?.Report(new ProgressInfo(0, "Analyse des fichiers…", true));
        EnsureInitialized();
        var kind = archive.Format == "Zip" ? ArchiveKind.Zip : ArchiveKind.SevenZip;
        var entries = BuildEntries(sources, folder.Replace('/', '\\'), n => progress?.Report(new ProgressInfo(0, $"Analyse des fichiers… ({n:n0} fichiers)", true)));
        progress?.Report(new ProgressInfo(0, "Compression en cours…", true));
        var compressor = CreateCompressor(new CompressOptions(kind, CompressionLevel.Normal, password, false), cancel, progress);
        compressor.CompressionMode = CompressionMode.Append;
        compressor.CompressFileDictionary(entries, archive.FilePath, string.IsNullOrEmpty(password) ? null : password);
    }, CancellationToken.None);

    /// <summary>Supprime des éléments (et, pour un dossier, tout son contenu) d'une archive 7z ou ZIP.</summary>
    public static Task DeleteAsync(ArchiveInfo archive, IReadOnlyCollection<ArchiveItem> items, string? password) => Task.Run(() =>
    {
        EnsureInitialized();
        var folders = items.Where(i => i.IsDirectory).Select(i => i.Path + "/").ToList();
        var toDelete = archive.Items
            .Where(i => items.Any(s => s.Index == i.Index) || folders.Any(f => i.Path.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(i => i.Index, _ => (string)null!);
        var compressor = new SevenZipCompressor();
        compressor.ModifyArchive(archive.FilePath, toDelete, string.IsNullOrEmpty(password) ? null : password);
    });

    private static SevenZipCompressor CreateCompressor(CompressOptions options, CancellationToken cancel, IProgress<ProgressInfo>? progress)
    {
        var compressor = new SevenZipCompressor
        {
            ArchiveFormat = options.Kind switch
            {
                ArchiveKind.Zip => OutArchiveFormat.Zip,
                ArchiveKind.Tar => OutArchiveFormat.Tar,
                _ => OutArchiveFormat.SevenZip,
            },
            CompressionLevel = options.Kind == ArchiveKind.Tar ? CompressionLevel.None : options.Level,
            // Découpe en volumes .001/.002… : le moteur la refuse pour les autres formats (ZIP, TAR)
            VolumeSize = options.Kind == ArchiveKind.SevenZip ? options.VolumeSize : 0,
            IncludeEmptyDirectories = true,
            ZipEncryptionMethod = ZipEncryptionMethod.Aes256,
            EncryptHeaders = options.Kind == ArchiveKind.SevenZip && options.EncryptNames && !string.IsNullOrEmpty(options.Password),
        };
        compressor.Compressing += (_, e) =>
        {
            // Le pourcentage du moteur compte les octets LUS : sur un gros fichier il bondit à 100 % en
            // moins d'une seconde alors que la compression dure encore ~10 s (tous les blocs parallèles
            // finissent ensemble, la sortie n'écrit qu'à la fin). Au-delà de 99 % on n'affiche donc plus
            // de chiffre : barre animée jusqu'au retour réel du moteur, puis 100 %.
            progress?.Report(e.PercentDone >= 99
                ? new ProgressInfo(99, null, true)
                : new ProgressInfo(e.PercentDone, null, false));
        };
        compressor.FileCompressionStarted += (_, e) =>
        {
            if (cancel.IsCancellationRequested)
                e.Cancel = true;
        };
        return compressor;
    }

    /// <summary>
    /// Nom dans l'archive → fichier sur le disque (null pour un dossier vide), sous <paramref name="prefix"/>.
    /// Un seul passage récursif (l'ancien code ré-énumérait chaque sous-dossier pour trouver les dossiers vides).
    /// <paramref name="onFiles"/> est appelé tous les 1000 éléments pour l'affichage de l'analyse.
    /// </summary>
    private static Dictionary<string, string> BuildEntries(IReadOnlyList<string> sources, string prefix = "", Action<long>? onFiles = null)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long count = 0;
        void Add(string key, string? file)
        {
            entries[key] = file!;
            if (++count % 1000 == 0)
                onFiles?.Invoke(count);
        }
        foreach (var source in sources)
        {
            if (File.Exists(source))
            {
                Add(Path.Combine(prefix, Path.GetFileName(source)), source);
            }
            else if (Directory.Exists(source))
            {
                var root = Path.Combine(prefix, Path.GetFileName(source.TrimEnd('\\', '/')));
                var pending = new Stack<(string Dir, string Rel)>();
                pending.Push((source, root));
                while (pending.Count > 0)
                {
                    var (dir, rel) = pending.Pop();
                    var hasEntries = false;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                    {
                        hasEntries = true;
                        if (Directory.Exists(entry))
                            pending.Push((entry, Path.Combine(rel, Path.GetFileName(entry))));
                        else
                            Add(Path.Combine(rel, Path.GetFileName(entry)), entry);
                    }
                    if (!hasEntries)
                        Add(rel, null); // dossier vide
                }
            }
        }
        if (entries.Count == 0)
            throw new InvalidOperationException("Aucun fichier à compresser.");
        return entries;
    }

    private static SevenZipExtractor Create(string path, string? password) =>
        string.IsNullOrEmpty(password) ? new SevenZipExtractor(path) : new SevenZipExtractor(path, password);

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>
    /// 7-Zip ne distingue pas toujours « mot de passe faux » d'« archive abîmée » : on recoupe le message, et sinon
    /// on regarde si l'archive est réellement chiffrée (une archive abîmée ou absente n'appelle pas de mot de passe).
    /// </summary>
    private static bool LooksLikePasswordProblem(Exception ex, string? password, string path)
    {
        var text = ex.ToString();
        if (text.Contains("password", StringComparison.OrdinalIgnoreCase) || text.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            return true;
        return ex is SevenZipArchiveException or ExtractionFailedException && IsEncrypted(path);
    }

    /// <summary>Noms chiffrés (illisibles sans mot de passe) ou au moins un fichier chiffré.</summary>
    private static bool IsEncrypted(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var extractor = new SevenZipExtractor(path);
            return extractor.ArchiveFileData.Any(f => f.Encrypted);
        }
        catch (Exception ex) when (ex.ToString().Contains("password", StringComparison.OrdinalIgnoreCase)
                                   || ex.ToString().Contains("encrypted", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Messages du moteur 7-Zip (en anglais) traduits pour l'utilisateur.</summary>
    private static string Explain(SevenZipException ex)
    {
        var text = ex.Message;
        if (text.Contains("corrupt", StringComparison.OrdinalIgnoreCase) || text.Contains("Data error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("CRC", StringComparison.OrdinalIgnoreCase))
            return "L'archive est endommagée : une partie des données est illisible. Les fichiers intacts ont pu être extraits.";
        if (text.Contains("Invalid archive", StringComparison.OrdinalIgnoreCase) || text.Contains("not an archive", StringComparison.OrdinalIgnoreCase)
            || text.Contains("format", StringComparison.OrdinalIgnoreCase))
            return "Ce fichier n'est pas une archive reconnue, ou il est trop endommagé pour être ouvert.";
        if (text.Contains("Unexpected end", StringComparison.OrdinalIgnoreCase))
            return "L'archive est incomplète (téléchargement interrompu ?).";
        if (text.Contains("volume", StringComparison.OrdinalIgnoreCase))
            return "Il manque une partie de l'archive en plusieurs volumes : placez toutes les parties dans le même dossier.";
        return "Le moteur de compression a signalé une erreur : " + text;
    }

    private static void EnsureExists(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Archive introuvable : {path}");
    }

    /// <summary>Nom proposé pour une nouvelle archive à partir des éléments sélectionnés.</summary>
    public static string SuggestArchiveName(IReadOnlyList<string> sources, ArchiveKind kind)
    {
        var first = sources[0].TrimEnd('\\', '/');
        var baseName = sources.Count == 1
            ? (File.Exists(first) ? Path.GetFileNameWithoutExtension(first) : Path.GetFileName(first))
            : Path.GetFileName(Path.GetDirectoryName(first)) is { Length: > 0 } parent ? parent : "Archive";
        return baseName + Extension(kind);
    }

    public static string Extension(ArchiveKind kind) => kind switch
    {
        ArchiveKind.Zip => ".zip",
        ArchiveKind.Tar => ".tar",
        _ => ".7z",
    };

    /// <summary>Dossier d'extraction « vers NomDeLArchive\ » : sans les extensions d'archive (.tar.gz, .part1.rar…).</summary>
    public static string FolderNameFor(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        name = System.Text.RegularExpressions.Regex.Replace(name, @"(\.part\d+)?\.(rar|zip|7z|tar|gz|tgz|bz2|tbz2|xz|txz|zst|iso|cab|wim|lzh|arj|z|001)$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\.tar$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return name.Length > 0 ? name : "Extraction";
    }

    /// <summary>Chemin libre : « Nom », puis « Nom (2) », « Nom (3) »…</summary>
    public static string UniquePath(string path, bool isDirectory)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = isDirectory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDirectory ? "" : Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }
}
