using System.ComponentModel;

namespace NeoDriver.Models;

public enum DriverState
{
    Available,
    Pending,
    Downloading,
    Installing,
    Installed,
    Failed,
}

public class DriverItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string UpdateId { get; set; } = "";
    public string Title { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string HardwareId { get; set; } = "";

    /// <summary>Version du pilote actuellement installé (vide si le périphérique n'a pas de pilote).</summary>
    public string CurrentVersion { get; set; } = "";
    public DateTime? CurrentDate { get; set; }
    public DateTime? NewDate { get; set; }
    public long DownloadSize { get; set; }

    /// <summary>Objet COM IUpdate de Windows Update, conservé pour l'installation.</summary>
    public object? Update { get; set; }

    private DriverState _state;
    public DriverState State
    {
        get => _state;
        set
        {
            if (_state == value)
                return;
            _state = value;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(ShowButton));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(StateText));
        }
    }

    private string _errorMessage = "";
    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            _errorMessage = value;
            OnPropertyChanged(nameof(StateText));
        }
    }

    public bool IsMissing => string.IsNullOrEmpty(CurrentVersion);

    public bool ShowButton => State is DriverState.Available or DriverState.Failed;
    public bool IsBusy => State is DriverState.Pending or DriverState.Downloading or DriverState.Installing;
    public bool IsInstalled => State == DriverState.Installed;
    public bool IsFailed => State == DriverState.Failed;

    public string StateText => State switch
    {
        DriverState.Pending => "En attente…",
        DriverState.Downloading => "Téléchargement…",
        DriverState.Installing => "Installation…",
        DriverState.Installed => "Installé",
        DriverState.Failed => ErrorMessage,
        _ => "",
    };

    public string Subtitle => $"{Category} · {Manufacturer}";

    public string CurrentText => IsMissing
        ? "Aucun pilote installé"
        : $"Installé : {CurrentVersion}{FormatDate(CurrentDate)}";

    public string NewText => $"Disponible{FormatDate(NewDate)} · {FormatSize(DownloadSize)}";

    private static string FormatDate(DateTime? date) => date is null ? "" : $" du {date:dd/MM/yyyy}";

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "taille inconnue",
        < 1024 * 1024 => $"{bytes / 1024.0:0} Ko",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} Mo",
    };

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
