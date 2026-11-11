using System.Reflection;
using System.Text.Json;
using SevenZip;

namespace NeoRar.Services;

public enum BackupFrequency
{
    Daily,
    Weekly,
}

/// <summary>Une sauvegarde planifiée : ses dossiers sont compressés dans une archive, chaque jour ou chaque semaine.</summary>
public sealed class BackupJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<string> Sources { get; set; } = [];
    public string Destination { get; set; } = "";
    public ArchiveKind Kind { get; set; } = ArchiveKind.SevenZip;

    /// <summary>Index dans la liste des niveaux de TaskWindow (3 = Normale).</summary>
    public int Level { get; set; } = 3;

    public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;
    public int Hour { get; set; } = 20;
    public int Minute { get; set; }
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;

    public DateTime? LastRun { get; set; }
    public bool? LastOk { get; set; }

    private static readonly string[] DayNames = ["dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi"];

    /// <summary>Planification en toutes lettres, p. ex. « Hebdomadaire (lundi) à 20:00 ».</summary>
    public string ScheduleText
    {
        get
        {
            var day = DayNames[(int)Day];
            day = char.ToUpperInvariant(day[0]) + day[1..];
            return Frequency == BackupFrequency.Daily
                ? $"Quotidienne à {Hour:00}:{Minute:00}"
                : $"Hebdomadaire ({day}) à {Hour:00}:{Minute:00}";
        }
    }

    public string LastRunText => LastRun is { } when
        ? $"dernière exécution : {when:dd/MM/yyyy HH:mm} · {(LastOk == true ? "réussie" : "échouée")}"
        : "jamais exécutée";
}

/// <summary>
/// Sauvegardes planifiées : la liste vit dans %LocalAppData%\NeoRar\backups.json, et chaque sauvegarde est
/// enregistrée comme vraie tâche du planificateur de tâches de Windows — elle a lieu même quand NeoRar
/// n'est pas ouvert (la tâche relance NeoRar.exe avec l'argument --backup).
/// </summary>
public static class BackupService
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoRar");
    private static readonly string FilePath = Path.Combine(Folder, "backups.json");
    private static readonly object Lock = new();

    public static List<BackupJob> Jobs { get; } = Load();

    /// <summary>
    /// Options de compression d'une sauvegarde : pas de mot de passe (la tâche tourne sans interaction possible),
    /// et les volumes ne sont pas repris ici (choix de la création manuelle uniquement).
    /// </summary>
    public static CompressOptions OptionsFor(BackupJob job) => new(job.Kind, (CompressionLevel)job.Level, null, false);

    public static BackupJob? Find(Guid id)
    {
        lock (Lock)
            return Jobs.FirstOrDefault(j => j.Id == id);
    }

    /// <summary>Ajoute la sauvegarde, l'enregistre sur le disque et crée la tâche Windows correspondante.</summary>
    public static void Add(BackupJob job)
    {
        lock (Lock)
        {
            Jobs.Add(job);
            Save();
        }
        RegisterTask(job);
    }

    /// <summary>Retire la sauvegarde et sa tâche Windows.</summary>
    public static void Remove(BackupJob job)
    {
        lock (Lock)
        {
            Jobs.Remove(job);
            Save();
        }
        UnregisterTask(job);
    }

    /// <summary>Note l'heure et l'issue de la dernière exécution (affichées dans la liste).</summary>
    public static void MarkRun(BackupJob job, bool ok)
    {
        lock (Lock)
        {
            job.LastRun = DateTime.Now;
            job.LastOk = ok;
            Save();
        }
    }

    // ======================================================================
    // Tâches du planificateur Windows
    // ======================================================================

    private static string TaskName(BackupJob job) => $"NeoRar - Sauvegarde {job.Name}";

    /// <summary>Crée (ou remplace) la tâche Windows qui lancera NeoRar --backup à l'heure prévue.</summary>
    public static void RegisterTask(BackupJob job)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NeoRar.exe");
        var taskName = TaskName(job).Replace("\"", "'");
        var args = $"--backup {job.Id:D}";
        var tr = $"\"{exe}\" {args}";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        if (job.Frequency == BackupFrequency.Daily)
        {
            psi.Arguments = $"/Create /TN \"{taskName}\" /TR \"{tr}\" /SC DAILY /ST {job.Hour:D2}:{job.Minute:D2} /F";
        }
        else
        {
            var day = job.Day switch
            {
                DayOfWeek.Monday => "MON",
                DayOfWeek.Tuesday => "TUE",
                DayOfWeek.Wednesday => "WED",
                DayOfWeek.Thursday => "THU",
                DayOfWeek.Friday => "FRI",
                DayOfWeek.Saturday => "SAT",
                _ => "SUN",
            };
            psi.Arguments = $"/Create /TN \"{taskName}\" /TR \"{tr}\" /SC WEEKLY /D {day} /ST {job.Hour:D2}:{job.Minute:D2} /F";
        }

        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"schtasks a échoué (code {p.ExitCode}) : {err.Trim()}");
    }

    private static void UnregisterTask(BackupJob job)
    {
        try
        {
            var taskName = TaskName(job).Replace("\"", "'");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{taskName}\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch (Exception)
        {
        }
    }

    // ======================================================================
    // Stockage
    // ======================================================================

    private static List<BackupJob> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<BackupJob>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception)
        {
            // Fichier illisible : on repart d'une liste vide
        }
        return [];
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Jobs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Disque indisponible : la liste reste en mémoire pour la session en cours
        }
    }
}
