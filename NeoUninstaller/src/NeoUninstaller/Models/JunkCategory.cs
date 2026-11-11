using System.ComponentModel;

namespace NeoUninstaller.Models;

/// <summary>Un emplacement à vider : dossier, motif de fichiers, âge minimum.</summary>
public sealed record JunkLocation(string Folder, string Pattern = "*", bool Recursive = true, int MinAgeHours = 0);

/// <summary>Une catégorie du nettoyeur (fichiers temporaires, cache d'un navigateur…).</summary>
public class JunkCategory : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Group { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public List<JunkLocation> Locations { get; init; } = [];
    public bool RequiresAdmin { get; init; }
    /// <summary>Catégorie spéciale : la Corbeille (vidée par l'API Windows).</summary>
    public bool IsRecycleBin { get; init; }
    /// <summary>Processus qui verrouillent ces fichiers (ex. chrome) : on prévient s'il tourne.</summary>
    public string? LockingProcess { get; init; }
    public string? Warning { get; init; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            Notify(nameof(IsChecked));
        }
    }

    public bool CanClean { get; set; } = true;

    private long _size = -1;
    /// <summary>Taille trouvée à l'analyse (-1 = pas encore analysé).</summary>
    public long SizeBytes
    {
        get => _size;
        set
        {
            _size = value;
            Notify(nameof(SizeBytes), nameof(SizeText));
        }
    }

    private int _fileCount;
    public int FileCount
    {
        get => _fileCount;
        set
        {
            _fileCount = value;
            Notify(nameof(FileCount), nameof(SizeText));
        }
    }

    private string? _status;
    public string? Status
    {
        get => _status;
        set
        {
            _status = value;
            Notify(nameof(Status), nameof(SizeText));
        }
    }

    public string SizeText => _status ?? (_size < 0 ? "" : _size == 0 ? "Vide" : $"{InstalledProgram.FormatSize(_size)} · {_fileCount} fichier(s)");

    public string DetailsText => Warning is null ? Description : $"{Description} · {Warning}";

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
