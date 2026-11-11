using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using NeoRar.Services;

namespace NeoRar.Models;

/// <summary>Une ligne de la liste : fichier, ou dossier (réel ou déduit des chemins) du dossier affiché.</summary>
public sealed class ArchiveEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Name { get; init; }
    /// <summary>Chemin complet dans l'archive (séparateur « / »).</summary>
    public required string FullPath { get; init; }
    public required bool IsFolder { get; init; }
    public ulong Size { get; init; }
    public DateTime? Modified { get; init; }
    public bool Encrypted { get; init; }
    /// <summary>Index 7-Zip de l'élément (null pour un dossier déduit des chemins).</summary>
    public int? Index { get; init; }

    /// <summary>Ligne « .. » : remonte d'un dossier (de l'archive, puis du disque), comme dans WinRAR.</summary>
    public bool IsParent { get; init; }

    /// <summary>Élément du disque (navigation hors archive) : son chemin réel. Null pour un élément d'archive.</summary>
    public string? DiskPath { get; init; }

    /// <summary>Lecteur affiché dans « Ce PC ».</summary>
    public bool IsDrive { get; init; }

    /// <summary>Fichier du disque que NeoRar sait ouvrir comme une archive.</summary>
    public bool IsArchiveFile => DiskPath is not null && !IsFolder && ArchiveService.IsArchive(DiskPath);

    public string SizeText => IsFolder ? "" : FormatSize(Size);
    public string ModifiedText => Modified is { } date && date.Year > 1980 ? date.ToString("dd/MM/yyyy HH:mm") : "";
    public string TypeText => IsParent ? "Dossier parent" : IsDrive ? "Lecteur" : FileIconService.GetTypeName(Name, IsFolder);
    public string LockGlyph => Encrypted ? "" : "";

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public static string FormatSize(double bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (1L << 20):0.0} Mo",
        >= 1024 => $"{bytes / 1024:0} Ko",
        _ => $"{bytes:0} o",
    };
}
