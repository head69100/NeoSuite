using System.ComponentModel;

namespace NeoUninstaller.Models;

public enum LeftoverKind
{
    Folder,
    File,
    Shortcut,
    RegistryKey,
    RegistryValue,
    Service,
    ScheduledTask,
}

public enum Confidence
{
    /// <summary>Relié au programme de façon certaine (dossier d'installation, sa propre clé…) : coché par défaut.</summary>
    Certain,
    /// <summary>Correspond au nom du programme ou de l'éditeur : à vérifier, décoché par défaut.</summary>
    Probable,
}

/// <summary>Un reste trouvé après la désinstallation (fichier, dossier, clé de registre…).</summary>
public class Leftover : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required LeftoverKind Kind { get; init; }
    /// <summary>Chemin du fichier/dossier, ou chemin complet de la clé (HKEY_…\…).</summary>
    public required string Path { get; init; }
    /// <summary>Pour <see cref="LeftoverKind.RegistryValue"/> : nom de la valeur. Pour un service : son nom.</summary>
    public string? ValueName { get; init; }
    public required string Reason { get; init; }
    public required Confidence Confidence { get; init; }
    public bool RequiresAdmin { get; init; }
    public long SizeBytes { get; init; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    /// <summary>Cochable : un élément « administrateur » ne peut pas être supprimé sans les droits.</summary>
    public bool CanDelete { get; set; } = true;

    public string Glyph => Kind switch
    {
        LeftoverKind.Folder => "",
        LeftoverKind.File => "",
        LeftoverKind.Shortcut => "",
        LeftoverKind.RegistryKey => "",
        LeftoverKind.RegistryValue => "",
        LeftoverKind.Service => "",
        LeftoverKind.ScheduledTask => "\uE916",
        _ => "",
    };

    public string KindText => Kind switch
    {
        LeftoverKind.Folder => "Dossier",
        LeftoverKind.File => "Fichier",
        LeftoverKind.Shortcut => "Raccourci",
        LeftoverKind.RegistryKey => "Clé de registre",
        LeftoverKind.RegistryValue => "Valeur de registre",
        LeftoverKind.Service => "Service",
        LeftoverKind.ScheduledTask => "Tâche planifiée",
        _ => "",
    };

    public string DisplayPath => Kind == LeftoverKind.RegistryValue ? $"{Path}  →  {ValueName}"
        : Kind is LeftoverKind.Service or LeftoverKind.ScheduledTask ? $"{ValueName}  ({Path})"
        : Path;

    public string Details
    {
        get
        {
            var parts = new List<string> { KindText, Reason };
            if (SizeBytes > 0)
                parts.Add(InstalledProgram.FormatSize(SizeBytes));
            if (!CanDelete)
                parts.Add("droits administrateur requis");
            return string.Join(" · ", parts);
        }
    }

    public bool IsRegistry => Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue;

    public string ConfidenceText => Confidence == Confidence.Certain ? "Sûr" : "À vérifier";
}
