using System.Text.Json.Serialization;

namespace NeoDriver.Models;

public class HistoryEntry
{
    public DateTime Date { get; set; }
    public string DeviceName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string? PreviousVersion { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";

    /// <summary>Le pilote a été retiré depuis (retour à l'ancienne version).</summary>
    public bool RolledBack { get; set; }

    [JsonIgnore] public bool CanRollback => Success && !RolledBack && !string.IsNullOrEmpty(PreviousVersion);
    [JsonIgnore] public string DateText => Date.ToString("dd/MM/yyyy HH:mm");
    [JsonIgnore] public string Glyph => Success ? "" : "";
    [JsonIgnore] public string Details => string.IsNullOrEmpty(PreviousVersion)
        ? $"{Category} · {Message}"
        : $"{Category} · ancienne version {PreviousVersion} · {Message}";
}
