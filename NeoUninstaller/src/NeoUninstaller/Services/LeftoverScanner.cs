using System.Text.RegularExpressions;
using Microsoft.Win32;
using NeoUninstaller.Models;

namespace NeoUninstaller.Services;

/// <summary>
/// Recherche les restes d'un programme désinstallé : dossiers, raccourcis, clés de registre, démarrage, services.
/// Règles de sécurité : jamais de dossier système ni de dossier partagé (Microsoft, Common Files…), jamais rien
/// qui appartienne à un autre programme installé ; seuls les éléments reliés de façon certaine sont cochés.
/// </summary>
public static partial class LeftoverScanner
{
    // Dossiers et noms qu'on ne propose jamais, même s'ils correspondent au nom du programme
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Microsoft Shared", "WindowsApps", "Temp", "Packages", "Programs",
        "Installer", "Package Cache", "Microsoft.NET", "dotnet", "Windows Defender", "Windows NT", "System32",
        "Intel", "AMD", "NVIDIA", "NVIDIA Corporation", "Realtek", "Google", "Mozilla", "Apple", "Adobe",
        "Classes", "Policies", "Clients", "RegisteredApplications", "WOW6432Node", "Wow6432Node", "ODBC", "Setup",
        // Mots trop génériques pour identifier un programme à coup sûr
        "Update", "Updater", "Common", "Shared", "Data", "Config", "Cache", "Logs", "Tools", "Plugins", "Bin", "Lib",
        "Drivers", "Driver", "Games", "Launcher", "Runtime", "Helper", "Service", "Services", "Uninstall",
    };

    private static readonly string[] PublisherSuffixes =
    [
        "corporation", "corp", "inc", "incorporated", "llc", "ltd", "limited", "gmbh", "co", "company", "sa", "sas",
        "sarl", "srl", "ag", "bv", "ab", "oy", "pty", "plc", "technologies", "technology", "software", "studios", "games",
    ];

    public static Task<List<Leftover>> ScanAsync(InstalledProgram program, IReadOnlyList<InstalledProgram> allPrograms, bool isAdmin) =>
        Task.Run(() => new Scan(program, allPrograms, isAdmin).Run());

    private sealed class Scan
    {
        private readonly InstalledProgram _program;
        private readonly bool _isAdmin;
        private readonly List<Leftover> _results = [];
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _names;            // noms normalisés du programme
        private readonly string? _publisher;                // nom d'éditeur normalisé lisible
        private string? _installDir;
        private readonly List<string> _otherLocations;      // dossiers des AUTRES programmes installés
        private readonly HashSet<string> _protectedPaths;

        public Scan(InstalledProgram program, IReadOnlyList<InstalledProgram> allPrograms, bool isAdmin)
        {
            _program = program;
            _isAdmin = isAdmin;
            _installDir = ResolveInstallDir(program);
            _publisher = NormalizePublisher(program.Publisher);

            _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddName(program.DisplayName);
            AddName(StripVersion(program.DisplayName));
            if (_installDir is not null)
                AddName(Path.GetFileName(_installDir));

            _otherLocations = allPrograms
                .Where(p => p.Key != program.Key && !p.DisplayName.Equals(program.DisplayName, StringComparison.OrdinalIgnoreCase))
                .Select(ResolveInstallDir)
                .Where(d => d is not null)
                .Select(d => d!.TrimEnd('\\'))
                .ToList();

            _protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Enum.GetValues<Environment.SpecialFolder>())
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path))
                    _protectedPaths.Add(path.TrimEnd('\\'));
            }
            _protectedPaths.Add(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'));
            _protectedPaths.Add(Path.GetTempPath().TrimEnd('\\'));

            // Dossier trop large (ex. « C:\Program Files ») ou partagé avec d'autres programmes :
            // tout ce qui « pointe dedans » ne lui appartient pas forcément, on ne s'en sert pas pour chercher
            if (_installDir is not null && (_protectedPaths.Contains(_installDir) || _installDir.Count(c => c == '\\') < 2
                || ProtectedNames.Contains(Path.GetFileName(_installDir))
                || _otherLocations.Any(o => o.Equals(_installDir, StringComparison.OrdinalIgnoreCase) || o.StartsWith(_installDir + "\\", StringComparison.OrdinalIgnoreCase))))
            {
                _installDir = null;
            }
        }

        private void AddName(string? name)
        {
            var n = Normalize(name);
            if (n.Length >= 3 && !ProtectedNames.Contains(name!.Trim()))
                _names.Add(n);
        }

        public List<Leftover> Run()
        {
            ScanFolders();
            ScanShortcuts();
            ScanRegistry();
            ScanRegistryDeep();
            ScanStartup();
            ScanServices();
            ScanTasks();
            return _results
                .OrderBy(r => r.Confidence)
                .ThenBy(r => r.IsRegistry)
                .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ---------- Dossiers ----------

        private void ScanFolders()
        {
            if (_installDir is not null)
                AddFolder(_installDir, "Dossier d'installation", Confidence.Certain);

            string[] bases =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ];

            foreach (var root in bases.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var isDocuments = root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase);
                foreach (var dir in SafeDirectories(root))
                {
                    var leaf = Path.GetFileName(dir);
                    if (MatchesName(leaf))
                    {
                        // Dans Documents, ce sont souvent des fichiers de l'utilisateur (sauvegardes, projets) : à vérifier
                        AddFolder(dir, isDocuments ? "Dossier dans Documents" : "Dossier de données", isDocuments ? Confidence.Probable : Confidence.Certain);
                    }
                    else if (_publisher is not null && Normalize(leaf) == Normalize(_publisher) && !ProtectedNames.Contains(leaf))
                    {
                        // Dossier de l'éditeur : on regarde ses sous-dossiers
                        var children = SafeDirectories(dir).ToList();
                        foreach (var child in children.Where(c => MatchesName(Path.GetFileName(c))))
                            AddFolder(child, "Dossier de l'éditeur", Confidence.Certain);

                        // Le dossier éditeur lui-même seulement s'il ne contient rien d'autre
                        var others = children.Where(c => !MatchesName(Path.GetFileName(c))).ToList();
                        if (others.Count == 0 && !isDocuments)
                            AddFolder(dir, "Dossier de l'éditeur (vide ou propre à ce programme)", Confidence.Probable);
                    }
                }
            }
        }

        private void AddFolder(string path, string reason, Confidence confidence)
        {
            path = path.TrimEnd('\\');
            if (!Directory.Exists(path) || !IsSafeFolder(path) || !_seen.Add(path))
                return;
            // Inutile de lister un sous-dossier d'un dossier déjà proposé
            if (_results.Any(r => r.Kind == LeftoverKind.Folder && path.StartsWith(r.Path + "\\", StringComparison.OrdinalIgnoreCase)))
                return;
            Add(new Leftover
            {
                Kind = LeftoverKind.Folder,
                Path = path,
                Reason = reason,
                Confidence = confidence,
                RequiresAdmin = PathRequiresAdmin(path),
                SizeBytes = FolderSize(path),
            });
        }

        private bool IsSafeFolder(string path)
        {
            if (_protectedPaths.Contains(path) || ProtectedNames.Contains(Path.GetFileName(path)))
                return false;
            if (path.Count(c => c == '\\') < 2) // ex. C:\Jeux : trop haut dans l'arborescence
                return false;
            // Jamais un dossier qui contient, ou se trouve dans, le dossier d'un autre programme installé
            foreach (var other in _otherLocations)
            {
                if (other.Equals(path, StringComparison.OrdinalIgnoreCase)
                    || other.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        // ---------- Raccourcis ----------

        private void ScanShortcuts()
        {
            string[] roots =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            ];

            dynamic? shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            }
            catch (Exception)
            {
            }

            foreach (var root in roots.Where(Directory.Exists))
            {
                var isDesktop = root.EndsWith("Desktop", StringComparison.OrdinalIgnoreCase) || root.EndsWith("Bureau", StringComparison.OrdinalIgnoreCase);

                // Dossier du menu Démarrer au nom du programme ou de l'éditeur
                if (!isDesktop)
                {
                    foreach (var dir in SafeDirectories(root))
                    {
                        var leaf = Path.GetFileName(dir);
                        if (MatchesName(leaf) || (_publisher is not null && Normalize(leaf) == Normalize(_publisher) && !ProtectedNames.Contains(leaf)))
                            AddShortcutFolder(dir);
                    }
                }

                foreach (var link in SafeFiles(root, "*.lnk", recursive: !isDesktop))
                {
                    if (_seen.Contains(link) || _results.Any(r => link.StartsWith(r.Path + "\\", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var target = ResolveShortcut(shell, link);
                    var pointsInside = target is not null && _installDir is not null && target.StartsWith(_installDir + "\\", StringComparison.OrdinalIgnoreCase);
                    var brokenNamed = target is not null && !File.Exists(target) && !Directory.Exists(target) && MatchesName(Path.GetFileNameWithoutExtension(link));
                    if (pointsInside || brokenNamed)
                    {
                        _seen.Add(link);
                        Add(new Leftover
                        {
                            Kind = LeftoverKind.Shortcut,
                            Path = link,
                            Reason = pointsInside ? "Raccourci vers le programme" : "Raccourci cassé portant son nom",
                            Confidence = pointsInside ? Confidence.Certain : Confidence.Probable,
                            RequiresAdmin = PathRequiresAdmin(link),
                        });
                    }
                }
            }
        }

        private void AddShortcutFolder(string dir)
        {
            if (!_seen.Add(dir))
                return;
            // Le dossier entier seulement s'il ne contient que des raccourcis
            var onlyLinks = SafeFiles(dir, "*", recursive: true).All(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase) || f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase));
            Add(new Leftover
            {
                Kind = LeftoverKind.Folder,
                Path = dir,
                Reason = "Dossier du menu Démarrer",
                Confidence = onlyLinks ? Confidence.Certain : Confidence.Probable,
                RequiresAdmin = PathRequiresAdmin(dir),
            });
        }

        private static string? ResolveShortcut(dynamic? shell, string link)
        {
            if (shell is null)
                return null;
            try
            {
                string target = shell.CreateShortcut(link).TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------- Registre ----------

        private void ScanRegistry()
        {
            // L'entrée « Applications installées » restée après la désinstallation
            if (ProgramInventory.StillInstalled(_program))
            {
                AddRegistryKey(_program.UninstallKeyPath, "Entrée « Applications installées » restante", Confidence.Certain);
            }

            (RegistryHive Hive, string Path)[] roots =
            [
                (RegistryHive.CurrentUser, @"Software"),
                (RegistryHive.LocalMachine, @"SOFTWARE"),
                (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node"),
            ];

            foreach (var (hive, rootPath) in roots)
            {
                using var root = OpenKey(hive, rootPath);
                if (root is null)
                    continue;

                foreach (var sub in SafeSubKeys(root))
                {
                    if (ProtectedNames.Contains(sub))
                        continue;
                    if (MatchesName(sub))
                    {
                        AddRegistryKey(FullPath(hive, $@"{rootPath}\{sub}"), "Clé du programme", Confidence.Certain);
                    }
                    else if (_publisher is not null && Normalize(sub) == Normalize(_publisher))
                    {
                        using var publisherKey = root.OpenSubKey(sub);
                        if (publisherKey is null)
                            continue;
                        var children = SafeSubKeys(publisherKey).ToList();
                        foreach (var child in children.Where(MatchesName))
                            AddRegistryKey(FullPath(hive, $@"{rootPath}\{sub}\{child}"), "Clé de l'éditeur", Confidence.Certain);
                        if (children.All(MatchesName))
                            AddRegistryKey(FullPath(hive, $@"{rootPath}\{sub}"), "Clé de l'éditeur (propre à ce programme)", Confidence.Probable);
                    }
                }
            }

            // App Paths : raccourci « Exécuter » vers un exe du programme
            if (_installDir is null)
                return;
            foreach (var (hive, path) in new[] { (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"), (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\App Paths") })
            {
                using var appPaths = OpenKey(hive, path);
                if (appPaths is null)
                    continue;
                foreach (var exe in SafeSubKeys(appPaths))
                {
                    using var key = appPaths.OpenSubKey(exe);
                    if (key?.GetValue(null) is string target && IsInsideInstallDir(target))
                        AddRegistryKey(FullPath(hive, $@"{path}\{exe}"), "Chemin d'application (Exécuter)", Confidence.Certain);
                }
            }
        }

        private void AddRegistryKey(string fullPath, string reason, Confidence confidence)
        {
            if (!_seen.Add(fullPath))
                return;
            // Inutile de lister une sous-clé d'une clé déjà proposée
            if (_results.Any(r => r.Kind == LeftoverKind.RegistryKey && fullPath.StartsWith(r.Path + "\\", StringComparison.OrdinalIgnoreCase)))
                return;
            Add(new Leftover
            {
                Kind = LeftoverKind.RegistryKey,
                Path = fullPath,
                Reason = reason,
                Confidence = confidence,
                RequiresAdmin = fullPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase),
            });
        }

        // ---------- Registre en profondeur (tout ce qui pointe vers le dossier du programme) ----------

        private void ScanRegistryDeep()
        {
            if (_installDir is null)
                return;

            // Composants COM : CLSID dont le serveur est dans le dossier du programme
            foreach (var (hive, classes) in ClassesRoots())
            {
                using var clsid = OpenKey(hive, $@"{classes}\CLSID");
                if (clsid is null)
                    continue;
                foreach (var id in SafeSubKeys(clsid))
                {
                    using var key = clsid.OpenSubKey(id);
                    if (key is null)
                        continue;
                    foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                    {
                        using var s = key.OpenSubKey(server);
                        if (s?.GetValue(null) is string path && IsInsideInstallDir(Environment.ExpandEnvironmentVariables(path)))
                        {
                            AddRegistryKey(FullPath(hive, $@"{classes}\CLSID\{id}"), "Composant COM du programme", Confidence.Certain);
                            break;
                        }
                    }
                }

                // Types de fichiers et « Ouvrir avec » dont la commande lance le programme
                using var classRoot = OpenKey(hive, classes);
                if (classRoot is null)
                    continue;
                foreach (var progId in SafeSubKeys(classRoot))
                {
                    if (progId.StartsWith('.') || progId is "CLSID" or "Interface" or "TypeLib" or "*" or "Directory" or "Folder" or "AllFilesystemObjects")
                        continue;
                    var isApplications = progId == "Applications";
                    if (isApplications)
                    {
                        using var apps = classRoot.OpenSubKey(progId);
                        if (apps is null)
                            continue;
                        foreach (var exe in SafeSubKeys(apps))
                        {
                            if (OpenCommandInside(apps, exe))
                                AddRegistryKey(FullPath(hive, $@"{classes}\Applications\{exe}"), "« Ouvrir avec » vers le programme", Confidence.Certain);
                        }
                    }
                    else if (OpenCommandInside(classRoot, progId))
                    {
                        AddRegistryKey(FullPath(hive, $@"{classes}\{progId}"), "Type de fichier associé au programme", Confidence.Certain);
                    }
                }
            }

            // Valeurs dont le NOM est un chemin du programme : MuiCache, DLL partagées, compatibilité
            (RegistryHive, string, string, bool)[] valueLists =
            [
                (RegistryHive.CurrentUser, @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", "Cache du nom de l'application", false),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "Réglage de compatibilité", false),
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "Réglage de compatibilité", true),
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", "DLL partagée du programme", true),
                (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs", "DLL partagée du programme", true),
            ];
            foreach (var (hive, path, reason, admin) in valueLists)
            {
                using var key = OpenKey(hive, path);
                if (key is null)
                    continue;
                foreach (var name in key.GetValueNames().Where(n => IsInsideInstallDir(n)))
                    AddRegistryValue(FullPath(hive, path), name, reason, admin);
            }

            // Règles du pare-feu Windows créées pour les exe du programme
            using var firewall = OpenKey(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
            if (firewall is not null)
            {
                foreach (var name in firewall.GetValueNames())
                {
                    if (firewall.GetValue(name) is string rule && rule.Contains("|App=" + _installDir + "\\", StringComparison.OrdinalIgnoreCase))
                        AddRegistryValue(FullPath(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"),
                            name, "Règle du pare-feu Windows", true);
                }
            }
        }

        private static IEnumerable<(RegistryHive, string)> ClassesRoots() =>
        [
            (RegistryHive.CurrentUser, @"Software\Classes"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Classes"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Classes"),
        ];

        /// <summary>La commande « shell\open\command » de cette clé lance-t-elle un exe du programme ?</summary>
        private bool OpenCommandInside(RegistryKey parent, string subKey)
        {
            try
            {
                using var command = parent.OpenSubKey($@"{subKey}\shell\open\command");
                return command?.GetValue(null) is string cmd && IsInsideInstallDir(Environment.ExpandEnvironmentVariables(cmd));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void AddRegistryValue(string keyPath, string valueName, string reason, bool admin)
        {
            if (!_seen.Add($"{keyPath}|{valueName}"))
                return;
            Add(new Leftover
            {
                Kind = LeftoverKind.RegistryValue,
                Path = keyPath,
                ValueName = valueName,
                Reason = reason,
                Confidence = Confidence.Certain,
                RequiresAdmin = admin,
            });
        }

        // ---------- Démarrage automatique ----------

        private void ScanStartup()
        {
            if (_installDir is null)
                return;
            (RegistryHive, string)[] runKeys =
            [
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
            ];
            foreach (var (hive, path) in runKeys)
            {
                using var key = OpenKey(hive, path);
                if (key is null)
                    continue;
                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is string data && IsInsideInstallDir(data))
                    {
                        Add(new Leftover
                        {
                            Kind = LeftoverKind.RegistryValue,
                            Path = FullPath(hive, path),
                            ValueName = name,
                            Reason = "Lancement au démarrage de Windows",
                            Confidence = Confidence.Certain,
                            RequiresAdmin = hive == RegistryHive.LocalMachine,
                        });
                    }
                }
            }
        }

        // ---------- Services ----------

        private void ScanServices()
        {
            if (_installDir is null)
                return;
            using var services = OpenKey(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services");
            if (services is null)
                return;
            foreach (var name in SafeSubKeys(services))
            {
                using var service = services.OpenSubKey(name);
                if (service?.GetValue("ImagePath") is string image && IsInsideInstallDir(Environment.ExpandEnvironmentVariables(image)))
                {
                    Add(new Leftover
                    {
                        Kind = LeftoverKind.Service,
                        Path = image.Trim('"'),
                        ValueName = name,
                        Reason = "Service Windows du programme",
                        Confidence = Confidence.Certain,
                        RequiresAdmin = true,
                    });
                }
            }
        }

        // ---------- Tâches planifiées ----------

        private void ScanTasks()
        {
            if (_installDir is null)
                return;
            foreach (var task in ScheduledTasks.List())
            {
                if (task.Command.Length > 0 && IsInsideInstallDir(Environment.ExpandEnvironmentVariables(task.Command)))
                {
                    Add(new Leftover
                    {
                        Kind = LeftoverKind.ScheduledTask,
                        Path = task.Command.Trim('"'),
                        ValueName = task.Name,
                        Reason = "Tâche planifiée du programme",
                        Confidence = Confidence.Certain,
                        RequiresAdmin = true,
                    });
                }
            }
        }

        // ---------- Utilitaires ----------

        private void Add(Leftover leftover)
        {
            leftover.CanDelete = !leftover.RequiresAdmin || _isAdmin;
            leftover.IsChecked = leftover.Confidence == Confidence.Certain && leftover.CanDelete;
            _results.Add(leftover);
        }

        private bool MatchesName(string name) => _names.Contains(Normalize(name));

        /// <summary>Le chemin (ou la commande, éventuellement guillemetée) désigne-t-il un fichier du dossier du programme ?</summary>
        private bool IsInsideInstallDir(string commandOrPath) =>
            _installDir is not null && _installDir.Length > 3
            && commandOrPath.TrimStart().Trim('"').StartsWith(_installDir + "\\", StringComparison.OrdinalIgnoreCase);

        private static bool PathRequiresAdmin(string path)
        {
            string[] userRoots =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ];
            return !userRoots.Any(r => path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
        }

        private static long FolderSize(string path)
        {
            long total = 0;
            try
            {
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    total += file.Length;
            }
            catch (Exception)
            {
            }
            return total;
        }

        private static IEnumerable<string> SafeDirectories(string path)
        {
            try
            {
                return Directory.EnumerateDirectories(path).ToList();
            }
            catch (Exception)
            {
                return [];
            }
        }

        private static IEnumerable<string> SafeFiles(string path, string pattern, bool recursive)
        {
            try
            {
                return Directory.EnumerateFiles(path, pattern, new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true }).ToList();
            }
            catch (Exception)
            {
                return [];
            }
        }

        private static IEnumerable<string> SafeSubKeys(RegistryKey key)
        {
            try
            {
                return key.GetSubKeyNames();
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    // ---------- Fonctions partagées ----------

    internal static RegistryKey? OpenKey(RegistryHive hive, string path, bool writable = false)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            return root.OpenSubKey(path, writable);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string FullPath(RegistryHive hive, string path) =>
        $@"{(hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE")}\{path}";

    /// <summary>Dossier d'installation : InstallLocation, sinon dossier du désinstalleur ou de l'icône s'il porte le nom du programme.</summary>
    internal static string? ResolveInstallDir(InstalledProgram program)
    {
        if (!string.IsNullOrEmpty(program.InstallLocation))
            return program.InstallLocation.TrimEnd('\\');

        var name = Normalize(StripVersion(program.DisplayName));
        foreach (var source in new[] { program.UninstallString, program.DisplayIcon })
        {
            var (file, _) = IconService.ParseIconLocation(source);
            if (file is null || file.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
                continue;
            var dir = Path.GetDirectoryName(file);
            // On remonte d'un cran si le désinstalleur est dans un sous-dossier « uninstall »
            if (dir is not null && Path.GetFileName(dir).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                dir = Path.GetDirectoryName(dir);
            if (dir is not null && Normalize(Path.GetFileName(dir)) is var leaf && leaf.Length >= 3
                && (leaf.Contains(name) || name.Contains(leaf)))
                return dir.TrimEnd('\\');
        }
        return null;
    }

    /// <summary>Minuscules, sans espaces ni ponctuation : « Notepad++ 8.6 (64-bit) » → « notepad86bit ».</summary>
    internal static string Normalize(string? value) =>
        value is null ? "" : NonAlnumRegex().Replace(value.ToLowerInvariant(), "");

    /// <summary>« 7-Zip 23.01 (x64) » → « 7-Zip ».</summary>
    internal static string StripVersion(string name)
    {
        var stripped = VersionRegex().Replace(name, "");
        stripped = ArchRegex().Replace(stripped, "");
        return stripped.Trim(' ', '-', '–', '_', '.', ',');
    }

    private static string? NormalizePublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
            return null;
        var words = PublisherWordRegex().Split(publisher.Trim()).Where(w => w.Length > 0).ToList();
        while (words.Count > 1 && PublisherSuffixes.Contains(words[^1].ToLowerInvariant().Trim('.')))
            words.RemoveAt(words.Count - 1);
        var result = string.Join(" ", words).Trim();
        return result.Length >= 3 && !ProtectedNames.Contains(result) ? result : null;
    }

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnumRegex();

    [GeneratedRegex(@"\s+v?\d+([.\-]\d+)*(\s*(beta|alpha|rc)\s*\d*)?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\s*[\(\[]?\s*(x64|x86|64[- ]?bits?|32[- ]?bits?|amd64|arm64)\s*[\)\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex ArchRegex();

    [GeneratedRegex(@"[\s,]+")]
    private static partial Regex PublisherWordRegex();
}
