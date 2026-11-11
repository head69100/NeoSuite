namespace NeoDriver.Models;

/// <summary>Un onglet de la page Matériel (Processeur, Carte mère…).</summary>
public class HardwareSection(string title, string glyph)
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public List<HardwareGroup> Groups { get; } = [];

    public HardwareGroup AddGroup(string title)
    {
        var group = new HardwareGroup(title);
        Groups.Add(group);
        return group;
    }
}

/// <summary>Une carte de propriétés (ex. « Cache », « Barrette 1 »).</summary>
public class HardwareGroup(string title)
{
    public string Title { get; } = title;
    public List<KeyValuePair<string, string>> Rows { get; } = [];

    public HardwareGroup Add(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            Rows.Add(new(label, value.Trim()));
        return this;
    }
}
