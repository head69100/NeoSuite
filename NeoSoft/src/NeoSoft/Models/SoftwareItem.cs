using System.ComponentModel;

namespace NeoSoft.Models;

public enum SoftwareState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    Queued,
    Working,
}

/// <summary>Un logiciel du catalogue NeoSoft, installé via winget depuis sa source officielle.</summary>
public sealed class SoftwareItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Identifiant winget (ex. VideoLAN.VLC).</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Publisher { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    /// <summary>Site de l'éditeur, sert à afficher son icône.</summary>
    public required string Domain { get; init; }

    /// <summary>Autres identifiants winget du même logiciel (version Beta, version Store…).</summary>
    public string[] AltIds { get; init; } = [];

    /// <summary>Identifiant sous lequel le logiciel est réellement installé (utilisé pour mettre à jour et désinstaller).</summary>
    public string? InstalledId { get; set; }

    public IEnumerable<string> AllIds => AltIds.Prepend(Id);

    /// <summary>Nom affiché dans « Applications installées » de Windows, s'il diffère de <see cref="Name"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Le nom affiché par Windows correspond-il à ce logiciel ? « Mozilla Firefox (x64 fr) » ou
    /// « Python 3.13.2 (64-bit) » oui, « GitHub Desktop » pour Git non (lettre juste après le nom).
    /// </summary>
    public bool MatchesDisplayName(string windowsName)
    {
        var name = DisplayName ?? Name;
        if (!windowsName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            return false;
        return windowsName.Length == name.Length || !char.IsLetter(windowsName[name.Length]);
    }

    /// <summary>Appli de la suite Neo (installée par NeoSuite, pas par winget).</summary>
    public bool IsNeo { get; init; }

    /// <summary>
    /// Hors catalogue NeoSoft : paquet trouvé par la recherche winget complète, non vérifié par la suite.
    /// Ces éléments ne sont pas sélectionnables en masse et n'ont pas d'icône d'éditeur.
    /// </summary>
    public bool IsExternal { get; init; }

    /// <summary>Les paquets winget n'ont pas d'icône d'éditeur : seule la lettre de l'initiale est montrée.</summary>
    public bool ShowIcon => !IsExternal;

    public string IconUrl => IsNeo ? $"ms-appx:///Assets/Apps/{Id}.png"
        // Icône locale jamais affichée (ShowIcon = false) : seule la validité du lien compte pour x:Bind
        : IsExternal ? "ms-appx:///Assets/AppIcon.png"
        : $"https://www.google.com/s2/favicons?domain={Domain}&sz=64";
    public string Initial => Name[..1].ToUpperInvariant();

    private string? _installedVersion;
    public string? InstalledVersion
    {
        get => _installedVersion;
        set { _installedVersion = value; Notify(nameof(InstalledVersion), nameof(VersionText)); }
    }

    private string? _availableVersion;
    public string? AvailableVersion
    {
        get => _availableVersion;
        set { _availableVersion = value; Notify(nameof(AvailableVersion), nameof(VersionText)); }
    }

    private SoftwareState _state;
    public SoftwareState State
    {
        get => _state;
        set
        {
            _state = value;
            Notify(nameof(State), nameof(ActionText), nameof(IsBusy), nameof(IsIdle), nameof(IsInstalled), nameof(CanSelect),
                nameof(ShowUninstall), nameof(HasUpdate), nameof(CanAct), nameof(VersionText));
        }
    }

    /// <summary>État stable à retrouver après une opération (ou en cas d'annulation dans la file).</summary>
    public SoftwareState RestingState { get; set; }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        set { _progressText = value; Notify(nameof(ProgressText)); }
    }

    private double _progress = -1;
    /// <summary>Progression du téléchargement en %, ou -1 si inconnue.</summary>
    public double Progress
    {
        get => _progress;
        set { _progress = value; Notify(nameof(Progress), nameof(IsIndeterminate)); }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            Notify(nameof(IsSelected));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public static event EventHandler? SelectionChanged;

    public bool IsBusy => State is SoftwareState.Queued or SoftwareState.Working;
    public bool IsIdle => !IsBusy;
    public bool IsIndeterminate => Progress < 0;
    public bool IsInstalled => State is SoftwareState.Installed or SoftwareState.UpdateAvailable
        || (IsBusy && RestingState is SoftwareState.Installed or SoftwareState.UpdateAvailable);
    // Les paquets winget hors catalogue s'installent un par un (pas de sélection multiple)
    public bool CanSelect => !IsExternal && State is SoftwareState.NotInstalled or SoftwareState.UpdateAvailable;
    public bool ShowUninstall => State is SoftwareState.Installed or SoftwareState.UpdateAvailable;
    public bool HasUpdate => State == SoftwareState.UpdateAvailable;
    /// <summary>Le bouton principal (Installer / Mettre à jour) est cliquable.</summary>
    public bool CanAct => State is SoftwareState.NotInstalled or SoftwareState.UpdateAvailable;

    public string ActionText => State switch
    {
        SoftwareState.Installed => "Installé",
        SoftwareState.UpdateAvailable => "Mettre à jour",
        SoftwareState.Queued => "En attente…",
        SoftwareState.Working => "En cours…",
        _ => "Installer",
    };

    public string VersionText => State == SoftwareState.UpdateAvailable && AvailableVersion is not null
        ? $"{InstalledVersion} → {AvailableVersion}"
        : InstalledVersion ?? "";

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
