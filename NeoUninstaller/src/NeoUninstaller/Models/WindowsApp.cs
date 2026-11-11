using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace NeoUninstaller.Models;

/// <summary>Une appli Windows (Microsoft Store / préinstallée), au format MSIX/AppX.</summary>
public class WindowsApp : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string FullName { get; init; }
    /// <summary>Nom d'identité, ex. « Microsoft.BingWeather ».</summary>
    public required string IdentityName { get; init; }
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public DateTime? InstallDate { get; init; }
    public Uri? LogoUri { get; init; }
    public bool IsEssential { get; init; }
    public bool IsCommonBloat { get; init; }
    public bool IsMicrosoft => IdentityName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                               || IdentityName.StartsWith("Clipchamp", StringComparison.OrdinalIgnoreCase);

    public string PublisherText => string.IsNullOrWhiteSpace(Publisher) ? "Éditeur inconnu" : Publisher!;
    public string DetailsText => string.Join(" · ", new[] { PublisherText, Version, InstallDate?.ToString("dd/MM/yyyy") }.Where(s => !string.IsNullOrEmpty(s)));
    public string TagText => IsEssential ? "Essentielle" : IsCommonBloat ? "Souvent inutile" : IsMicrosoft ? "Windows" : "";
    public bool HasTag => TagText.Length > 0;

    public ImageSource? Logo { get; set; }

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
}
