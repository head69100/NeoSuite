namespace NeoSound.Models;

/// <summary>Un périphérique de sortie audio actif (casque, haut-parleurs, câble virtuel…).</summary>
public sealed class AudioDevice
{
    /// <summary>Identifiant stable du point de terminaison (différent d'un branchement à l'autre).</summary>
    public required string Id { get; init; }

    /// <summary>Nom affiché par Windows (« Casque (Realtek Audio) »).</summary>
    public required string Name { get; init; }

    /// <summary>Contrôleur ou adaptateur auquel le périphérique est rattaché.</summary>
    public required string Description { get; init; }

    /// <summary>Périphérique de lecture par défaut de Windows.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Fréquence d'échantillonnage du mélange Windows pour ce périphérique.</summary>
    public int SampleRate { get; init; }

    /// <summary>Nombre de canaux du mélange Windows (1 = mono, 2 = stéréo…).</summary>
    public int Channels { get; init; }

    /// <summary>
    /// Vrai pour un périphérique Bluetooth en mode « mains libres » (profil téléphone) :
    /// 16 kHz mono, la qualité d'un appel. Windows l'expose à côté du périphérique stéréo.
    /// </summary>
    public bool IsPhoneMode { get; init; }

    /// <summary>
    /// Par quel matériel Windows atteint ce périphérique : « Bluetooth », « USB », « intégré »…
    /// Vide quand Windows ne le dit pas. Rempli par <c>AudioDeviceService</c>.
    /// </summary>
    public string Transport { get; init; } = "";

    /// <summary>Vrai si le périphérique passe par une liaison Bluetooth.</summary>
    public bool IsBluetooth => Transport == "Bluetooth";

    /// <summary>
    /// Périphérique stéréo à utiliser à la place quand <see cref="IsPhoneMode"/> est vrai
    /// (« David Bose » pour « David Bose Hands-Free »). Rempli par <c>AudioDeviceService</c>.
    /// </summary>
    public string? BetterSiblingName { get; set; }

    /// <summary>Texte des listes déroulantes.</summary>
    public string Display
    {
        get
        {
            var text = IsDefault ? $"{Name}  ·  par défaut" : Name;
            if (IsPhoneMode)
                return $"{text}  ·  mode mains libres (téléphone)";
            return IsBluetooth ? $"{text}  ·  Bluetooth" : text;
        }
    }

    public override string ToString() => Display;
}
