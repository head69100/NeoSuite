namespace Neo.Shared;

[Flags]
public enum HotKeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>Une combinaison de touches (ex. Ctrl + Alt + N), stockable en texte dans les paramètres.</summary>
public sealed record HotKeyGesture(HotKeyModifiers Modifiers, int VirtualKey)
{
    public static HotKeyGesture Default { get; } = new(HotKeyModifiers.Control | HotKeyModifiers.Alt, 'N');

    /// <summary>Un raccourci global doit avoir au moins un modificateur, sinon il bloquerait une touche normale.</summary>
    public bool IsValid => Modifiers != HotKeyModifiers.None && VirtualKey != 0 && !IsModifierKey(VirtualKey);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotKeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotKeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotKeyModifiers.Shift)) parts.Add("Maj");
        if (Modifiers.HasFlag(HotKeyModifiers.Windows)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    /// <summary>Format de sauvegarde : « 3:78 » (modificateurs:code de touche).</summary>
    public string Serialize() => $"{(int)Modifiers}:{VirtualKey}";

    public static HotKeyGesture Parse(string? value)
    {
        var parts = value?.Split(':');
        if (parts is { Length: 2 } && int.TryParse(parts[0], out var mods) && int.TryParse(parts[1], out var vk))
        {
            var gesture = new HotKeyGesture((HotKeyModifiers)mods, vk);
            if (gesture.IsValid)
                return gesture;
        }
        return Default;
    }

    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static string KeyName(int vk) => vk switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        >= 0x60 and <= 0x69 => $"Pavé {vk - 0x60}",
        0x20 => "Espace",
        0x0D => "Entrée",
        0x09 => "Tab",
        0x1B => "Échap",
        0x08 => "Retour",
        0x2E => "Suppr",
        0x2D => "Inser",
        0x24 => "Début",
        0x23 => "Fin",
        0x21 => "Page préc.",
        0x22 => "Page suiv.",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0xC0 => "²",
        _ => $"Touche {vk}",
    };
}
