using System.ComponentModel;
using System.Diagnostics;

namespace NeoSuite.Models;

public enum AppOperation
{
    None,
    Downloading,
    Installing,
    Uninstalling,
}

/// <summary>Une application de la suite Neo et son état d'installation.</summary>
public class NeoApp : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }

    /// <summary>Image de la tuile, dans Assets\Apps.</summary>
    public required string Icon { get; init; }

    /// <summary>Exécutable installé (Program Files\Neo\…), ou null si l'appli n'est pas installée.</summary>
    public string? ExecutablePath { get; set; }
    public Version? InstalledVersion { get; set; }

    /// <summary>L'appli démarre en administrateur sans invite UAC (tâche planifiée créée par NeoSuite).</summary>
    public bool RunsAsAdmin { get; set; }

    /// <summary>Paquet téléchargeable (null si aucun paquet n'est publié pour l'instant).</summary>
    public string? PackageSource { get; set; }
    public Version? AvailableVersion { get; set; }
    public long PackageSize { get; set; }
    public string? PackageSha256 { get; set; }

    public string IconUri => $"ms-appx:///Assets/Apps/{Icon}";
    public string NamePrefix => Name.StartsWith("Neo") ? "Neo" : "";
    public string NameSuffix => Name.StartsWith("Neo") ? Name[3..] : Name;

    public bool IsInstalled => ExecutablePath is not null;
    public bool CanInstall => PackageSource is not null;
    /// <summary>SHA-256 du paquet qui a servi à l'installation (null pour les installations d'avant ce suivi).</summary>
    public string? InstalledPackageSha256 { get; set; }

    /// <summary>Dernier état relevé par l'appli (« 3 mises à jour »), affiché même quand elle est fermée.</summary>
    public string? StoredStatus { get; set; }

    /// <summary>Argument qui fait chercher l'appli en silence, sans fenêtre (« --scan-only » pour NeoDriver), null si elle ne sait pas.</summary>
    public string? BackgroundCheckArg { get; init; }

    /// <summary>
    /// Nouvelle version, ou même version mais paquet reconstruit depuis (empreinte différente).
    /// Une version installée plus récente que le paquet n'est jamais « mise à jour » vers l'arrière.
    /// </summary>
    public bool HasUpdate => IsInstalled && CanInstall
        && (AvailableVersion > InstalledVersion
            || (AvailableVersion == InstalledVersion && PackageSha256 is not null
                && !string.Equals(InstalledPackageSha256, PackageSha256, StringComparison.OrdinalIgnoreCase)));
    public bool IsRunning => Process.GetProcessesByName(Id).Length > 0;

    private AppOperation _operation;
    public AppOperation Operation
    {
        get => _operation;
        set
        {
            _operation = value;
            Notify(nameof(Operation), nameof(IsBusy), nameof(ShowChip), nameof(IsIndeterminate), nameof(StatusText), nameof(IsEnabled));
        }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            Notify(nameof(Progress), nameof(StatusText));
        }
    }

    private string? _liveStatus;
    /// <summary>État donné par l'appli elle-même quand elle tourne (ex. « 2 mises à jour »).</summary>
    public string? LiveStatus
    {
        get => _liveStatus;
        set
        {
            _liveStatus = value;
            Notify(nameof(LiveStatus), nameof(StatusText));
        }
    }

    /// <summary>Actions proposées par l'appli quand elle tourne (son menu de barre système).</summary>
    public IReadOnlyList<Neo.Shared.NeoAction>? LiveActions { get; set; }

    public bool IsBusy => Operation != AppOperation.None;
    public bool ShowChip => !IsBusy;
    public bool IsIndeterminate => Operation != AppOperation.Downloading;
    public bool IsEnabled => IsInstalled || CanInstall;

    /// <summary>Texte de la pastille / de la progression.</summary>
    public string StatusText => Operation switch
    {
        AppOperation.Downloading => $"Téléchargement… {Progress:0} %",
        AppOperation.Installing => "Installation…",
        AppOperation.Uninstalling => "Désinstallation…",
        _ when HasUpdate => "Mettre à jour",
        _ when IsRunning && LiveStatus is not null => LiveStatus,
        _ when IsRunning => "Ouverte",
        _ when IsInstalled && StoredStatus is not null => StoredStatus,
        _ when IsInstalled => "Ouvrir",
        _ when CanInstall => PackageSize > 0 ? $"Installer · {PackageSize / 1024.0 / 1024.0:0} Mo" : "Installer",
        _ => "Bientôt",
    };

    /// <summary>À appeler après une (dés)installation pour rafraîchir la tuile.</summary>
    public void NotifyStateChanged() =>
        Notify(nameof(IsInstalled), nameof(HasUpdate), nameof(StatusText), nameof(IsEnabled), nameof(ShowChip));

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
