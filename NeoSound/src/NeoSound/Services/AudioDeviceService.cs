using NAudio.CoreAudioApi;
using NeoSound.Models;

namespace NeoSound.Services;

/// <summary>
/// Liste les périphériques de sortie audio actifs et repère le périphérique par défaut.
/// S'appuie sur l'API WASAPI de Windows (via NAudio.CoreAudioApi).
///
/// Un casque Bluetooth apparaît deux fois dans Windows : une fois en stéréo (A2DP, bonne qualité)
/// et une fois en « mains libres » (profil téléphone, 16 kHz mono). Le second est repéré et signalé :
/// il ne peut pas jouer de musique correctement.
///
/// Windows ne dit nulle part qu'un périphérique est Bluetooth ; c'est le nom de son énumérateur
/// (`BTHENUM`) qui le révèle, et il sert aussi à savoir si un casque passe par USB ou par la carte
/// mère. L'interface s'en sert pour étiqueter les sorties.
/// </summary>
public static class AudioDeviceService
{
    /// <summary>
    /// Mots que Windows emploie pour le profil « mains libres » d'un casque Bluetooth. On ne se fie pas
    /// au nom seul : « Oreillette de casque » désigne aussi une sortie filaire parfaitement correcte
    /// (le canal « chat » d'un casque gaming, par exemple). C'est le format qui tranche.
    /// </summary>
    private static readonly string[] PhoneMarkers =
        ["hands-free", "hands free", "mains libres", "kit mains libres"];

    /// <summary>
    /// Clé de propriété « nom de l'énumérateur » (`DEVPKEY_Device_EnumeratorName`), que NAudio
    /// n'expose pas dans <see cref="PropertyKeys"/>. Sa valeur dit par quel matériel Windows atteint
    /// le périphérique : `BTHENUM` pour un casque Bluetooth, `USB` pour un casque USB, `HDAUDIO`
    /// pour une sortie de la carte mère ou de la carte graphique. C'est la seule façon fiable de
    /// distinguer un casque Bluetooth d'un casque filaire sans se fier à son nom.
    /// </summary>
    private static readonly PropertyKey EnumeratorNameKey =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    /// <summary>Sorties actives, périphérique par défaut en tête.</summary>
    public static IReadOnlyList<AudioDevice> GetRenderDevices()
    {
        var result = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            }
            catch (Exception)
            {
                // Aucun périphérique par défaut (cas rare) : on liste quand même le reste
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string description;
                try
                {
                    description = device.DeviceFriendlyName;
                }
                catch (Exception)
                {
                    description = "";
                }

                // Format du mélange Windows : c'est lui qui trahit le mode « mains libres »
                // (16 kHz mono) et qui indique comment convertir les canaux au démarrage.
                var sampleRate = 0;
                var channels = 0;
                try
                {
                    var format = device.AudioClient.MixFormat;
                    sampleRate = format.SampleRate;
                    channels = format.Channels;
                }
                catch (Exception)
                {
                    // Périphérique occupé ou pilote capricieux : on le liste quand même
                }

                var name = device.FriendlyName;
                result.Add(new AudioDevice
                {
                    Id = device.ID,
                    Name = name,
                    Description = description,
                    IsDefault = device.ID == defaultId,
                    SampleRate = sampleRate,
                    Channels = channels,
                    IsPhoneMode = IsPhoneMode(name, sampleRate, channels),
                    Transport = TransportOf(device),
                });
            }
        }
        catch (Exception)
        {
            // WASAPI indisponible : la liste reste vide, l'appelant l'annonce
        }

        var ordered = result.OrderByDescending(d => d.IsDefault).ToList();
        LinkBetterSiblings(ordered);
        return ordered;
    }

    /// <summary>
    /// Un périphérique de sortie est en mode « mains libres » s'il est mono, si sa fréquence est
    /// celle d'un appel (16 kHz au plus), ou si Windows l'annonce explicitement comme tel. Le casque
    /// stéréo du même appareil reste, lui, parfaitement utilisable.
    /// </summary>
    private static bool IsPhoneMode(string name, int sampleRate, int channels) =>
        channels == 1 ||
        sampleRate is > 0 and <= 16000 ||
        PhoneMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Associe chaque profil « mains libres » au périphérique stéréo du même appareil, pour pouvoir
    /// indiquer lequel choisir. Windows les expose côte à côte (« Casque (David Bose) » et son profil
    /// téléphone) et leur donne le même nom d'appareil : c'est ce nom qui les rapproche.
    /// </summary>
    private static void LinkBetterSiblings(List<AudioDevice> devices)
    {
        foreach (var phone in devices.Where(d => d.IsPhoneMode))
        {
            var baseName = phone.Name;
            var cut = PhoneMarkers
                .Select(marker => baseName.IndexOf(marker, StringComparison.OrdinalIgnoreCase))
                .Where(index => index > 0)
                .DefaultIfEmpty(-1)
                .Min();
            if (cut > 0)
                baseName = baseName[..cut].Trim();

            phone.BetterSiblingName = devices
                .FirstOrDefault(d => d.Id != phone.Id && !d.IsPhoneMode &&
                    ((phone.Description.Length > 0 &&
                      string.Equals(d.Description, phone.Description, StringComparison.OrdinalIgnoreCase)) ||
                     d.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) ||
                     baseName.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase)))
                ?.Name;
        }
    }

    /// <summary>
    /// Par quel matériel Windows atteint ce périphérique. Une valeur inconnue reste vide : mieux vaut
    /// ne rien dire que d'annoncer « Bluetooth » à tort.
    /// </summary>
    private static string TransportOf(MMDevice device)
    {
        string enumerator;
        try
        {
            var store = device.Properties;
            if (!store.Contains(EnumeratorNameKey))
                return "";
            enumerator = store[EnumeratorNameKey].Value as string ?? "";
        }
        catch (Exception)
        {
            return "";
        }

        return enumerator.ToUpperInvariant() switch
        {
            "BTHENUM" or "BTHHFENUM" => "Bluetooth",
            "USB" => "USB",
            "HDAUDIO" => "intégré",
            "PCI" => "carte interne",
            "SWD" or "ROOT" or "MMDEVAPI" => "virtuel",
            _ => "",
        };
    }
}
