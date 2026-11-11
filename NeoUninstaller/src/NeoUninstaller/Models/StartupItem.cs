using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace NeoUninstaller.Models;

public enum StartupSource
{
    RegistryUser,      // HKCU\...\Run
    RegistryMachine,   // HKLM\...\Run (64 bits)
    RegistryMachine32, // HKLM\WOW6432Node\...\Run
    FolderUser,        // shell:startup
    FolderCommon,      // shell:common startup
    ScheduledTask,     // tâche planifiée déclenchée à l'ouverture de session / au démarrage
}

/// <summary>Un programme lancé automatiquement au démarrage de Windows.</summary>
public class StartupItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required StartupSource Source { get; init; }
    /// <summary>Nom de la valeur de registre, du fichier du dossier Démarrage ou chemin de la tâche.</summary>
    public required string Name { get; init; }
    public required string Command { get; init; }
    public string? ExecutablePath { get; init; }
    public string? Publisher { get; init; }
    public string? Description { get; init; }

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;
            _isEnabled = value;
            Notify(nameof(IsEnabled), nameof(StatusText));
        }
    }

    public bool RequiresAdmin => Source is StartupSource.RegistryMachine or StartupSource.RegistryMachine32 or StartupSource.FolderCommon
        || (Source == StartupSource.ScheduledTask && !IsUserTask);

    /// <summary>Tâche créée par/pour l'utilisateur courant (modifiable sans droits administrateur).</summary>
    public bool IsUserTask { get; init; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Description) ? Description! : Path.GetFileNameWithoutExtension(Name);
    public string PublisherText => string.IsNullOrWhiteSpace(Publisher) ? "Éditeur inconnu" : Publisher!;
    public string StatusText => IsEnabled ? "Activé" : "Désactivé";

    public string SourceText => Source switch
    {
        StartupSource.RegistryUser => "Registre (utilisateur)",
        StartupSource.RegistryMachine => "Registre (tous les utilisateurs)",
        StartupSource.RegistryMachine32 => "Registre 32 bits (tous les utilisateurs)",
        StartupSource.FolderUser => "Dossier Démarrage",
        StartupSource.FolderCommon => "Dossier Démarrage commun",
        StartupSource.ScheduledTask => "Tâche planifiée",
        _ => "",
    };

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Notify(nameof(Icon), nameof(HasNoIcon));
        }
    }

    public bool HasNoIcon => _icon is null;

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
