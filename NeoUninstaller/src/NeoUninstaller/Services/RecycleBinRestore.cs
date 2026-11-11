namespace NeoUninstaller.Services;

/// <summary>Remet à leur place les fichiers et dossiers qu'un nettoyage a envoyés à la Corbeille.</summary>
public static class RecycleBinRestore
{
    public static Task<(int Restored, int Missing)> RestoreAsync(IEnumerable<string> originalPaths) => Task.Run(() =>
    {
        var wanted = originalPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int restored = 0;
        if (wanted.Count == 0)
            return (0, 0);

        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic bin = shell!.NameSpace(10);   // 10 = Corbeille
            foreach (dynamic item in bin.Items())
            {
                try
                {
                    string folder = bin.GetDetailsOf(item, 1);       // emplacement d'origine
                    string name = item.Name;
                    var original = Path.Combine(folder, name);
                    string current = item.Path;
                    // Le nom affiché peut omettre l'extension : on essaie aussi avec celle du fichier réel
                    if (!wanted.Contains(original) && !wanted.Contains(original + Path.GetExtension(current)))
                        continue;
                    var target = wanted.Contains(original) ? original : original + Path.GetExtension(current);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (Directory.Exists(target) || File.Exists(target))
                        continue;
                    if (Directory.Exists(current))
                        Directory.Move(current, target);
                    else
                        File.Move(current, target);
                    restored++;
                }
                catch (Exception)
                {
                    // élément inaccessible : on passe au suivant
                }
            }
        }
        catch (Exception)
        {
        }
        return (restored, wanted.Count - restored);
    });
}