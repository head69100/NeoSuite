using System.ComponentModel;

namespace NeoMedia.Models;

/// <summary>Un fichier (ou un lien) de la liste de lecture.</summary>
public sealed class PlaylistItem(string source) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Chemin du fichier, ou adresse d'un flux réseau.</summary>
    public string Source { get; } = source;

    public bool IsFile => !Uri.TryCreate(Source, UriKind.Absolute, out var uri) || uri.IsFile;

    public string Name => IsFile ? Path.GetFileNameWithoutExtension(Source) : Source;

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
        }
    }

    /// <summary>Icône « en cours » devant l'élément lu.</summary>
    public string Glyph => IsCurrent ? "" : "";

    private string _duration = "";
    public string Duration
    {
        get => _duration;
        set
        {
            _duration = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        }
    }
}
