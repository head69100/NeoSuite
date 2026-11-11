using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;

namespace NeoUninstaller.Models;

/// <summary>Un programme listé dans « Applications installées » (clé Uninstall du registre).</summary>
public class InstalledProgram : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required RegistryHive Hive { get; init; }
    public required RegistryView View { get; init; }
    public required string SubKeyName { get; init; }

    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public DateTime? InstallDate { get; init; }
    private long? _sizeBytes;
    /// <summary>Taille déclarée par le programme, ou calculée d'après son dossier si elle manque.</summary>
    public long? SizeBytes
    {
        get => _sizeBytes;
        set
        {
            _sizeBytes = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        }
    }

    /// <summary>Utiliser QuietUninstallString (sans fenêtres) pour cette désinstallation.</summary>
    public bool UseQuietUninstall { get; set; }
    public bool HasQuietUninstall => !string.IsNullOrWhiteSpace(QuietUninstallString) || IsMsi || (!IsForced && Services.SilentUninstall.SwitchesFor(this) is not null);
    public string? InstallLocation { get; init; }
    public string? DisplayIcon { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public bool IsMsi { get; init; }

    /// <summary>Programme forcé : pas d'entrée dans le registre, on ne fait qu'analyser ses restes.</summary>
    public bool IsForced { get; init; }

    /// <summary>Installé pour l'utilisateur seul (HKCU) : désinstallable sans droits administrateur.</summary>
    public bool IsPerUser => Hive == RegistryHive.CurrentUser;

    public string Key => $"{Hive}|{View}|{SubKeyName}";

    public string UninstallKeyPath =>
        $@"{(IsPerUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE")}\{UninstallKeyRelativePath}";

    public string UninstallKeyRelativePath => View == RegistryView.Registry32 && !IsPerUser
        ? $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{SubKeyName}"
        : $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{SubKeyName}";

    public string PublisherText => string.IsNullOrWhiteSpace(Publisher) ? "Éditeur inconnu" : Publisher!;
    public string VersionText => Version ?? "";
    public string SizeText => SizeBytes is > 0 ? FormatSize(SizeBytes.Value) : "";
    public string DateText => InstallDate?.ToString("dd/MM/yyyy") ?? "";
    public string ScopeText => IsPerUser ? "Utilisateur" : View == RegistryView.Registry32 ? "32 bits" : "";
    public bool HasScope => ScopeText.Length > 0;

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoIcon)));
        }
    }

    public bool HasNoIcon => _icon is null;

    private DateTime? _lastUsed;
    /// <summary>Dernière utilisation connue (dossier Prefetch, administrateur requis).</summary>
    public DateTime? LastUsed
    {
        get => _lastUsed;
        set
        {
            _lastUsed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastUsedText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUnused)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnusedText)));
        }
    }

    public string LastUsedText => _lastUsed is { } d ? $"utilisé le {d:dd/MM/yyyy}" : "";

    /// <summary>
    /// Pilote ou composant système : il n'est jamais « lancé » à la souris, le suivi de dernière utilisation
    /// ne s'applique pas à lui et aucun badge d'inutilisation ne doit s'afficher.
    /// </summary>
    public bool UsageNotApplicable
    {
        get => _usageNotApplicable;
        set
        {
            _usageNotApplicable = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUnused)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnusedText)));
        }
    }
    private bool _usageNotApplicable;

    /// <summary>N'a pas servi depuis 6 mois (ou jamais) : aide à décider quoi supprimer. Informatif seulement.</summary>
    public bool IsUnused => !UsageNotApplicable && Services.UsageService.Available && (_lastUsed is null || _lastUsed < DateTime.Now.AddMonths(-6));

    public string UnusedText => !IsUnused ? "" : _lastUsed is null ? "jamais utilisé" : "pas utilisé depuis 6 mois";

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} o",
        < 1024 * 1024 => $"{bytes / 1024.0:0} Ko",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} Mo",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} Go",
    };
}
