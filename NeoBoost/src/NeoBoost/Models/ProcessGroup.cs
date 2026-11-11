using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using NeoBoost.Services;

namespace NeoBoost.Models;

/// <summary>Un programme en cours d'exécution (toutes ses instances regroupées, comme dans le Gestionnaire des tâches).</summary>
public sealed class ProcessGroup(string name) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; } = name;

    /// <summary>Nom lisible (description du fichier), ou le nom du processus à défaut.</summary>
    public string DisplayName { get; set; } = name;

    private ImageSource? _icon;
    /// <summary>Icône du programme (extraite de son exécutable), pour le reconnaître d'un coup d'œil.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
        }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>Processus de Windows ou service : NeoBoost ne propose pas de le fermer.</summary>
    public bool IsProtected { get; set; }

    public bool CanClose => !IsProtected;
    public string KindText => IsProtected ? "Windows / service" : Count > 1 ? $"{Count} processus" : "Programme";

    public int Count { get; private set; }
    public double CpuPercent { get; private set; }
    public long Memory { get; private set; }

    public string CpuText => CpuPercent < 0.1 ? "0 %" : $"{CpuPercent:0.0} %";
    public string MemoryText => SystemMonitor.FormatBytes(Memory);

    public void Update(int count, double cpu, long memory)
    {
        Count = count;
        CpuPercent = cpu;
        Memory = memory;
        foreach (var prop in new[] { nameof(Count), nameof(CpuPercent), nameof(Memory), nameof(CpuText), nameof(MemoryText), nameof(KindText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
