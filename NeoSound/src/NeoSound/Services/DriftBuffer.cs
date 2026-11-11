using NAudio.Wave;

namespace NeoSound.Services;

/// <summary>
/// Tampon circulaire entre la capture (le son du PC) et une sortie (un casque).
///
/// La source et chaque casque ont leur propre horloge : elles ne battent pas exactement à la même
/// fréquence. Sans correction, le retard s'accumule (le son prend du retard) ou le tampon se vide
/// (le son saute). On garde donc le tampon autour d'un niveau cible : quand il déborde franchement,
/// on jette des trames anciennes ; quand il se vide, on complète par du silence.
///
/// La correction obéit à un seuil (hystérésis) : jeter des trames est un petit « saut » dans le son,
/// il vaut donc mieux le faire rarement et d'un coup que sans arrêt pour un ou deux échantillons —
/// c'est la différence entre une duplication propre et un grésillement continu.
///
/// Le niveau visé s'adapte enfin à la taille des blocs que la sortie réclame (voir <see cref="Read"/>) :
/// un périphérique qui demande ses blocs par paquets de 50 ms ne peut pas être servi avec 10 ms de
/// tampon, même si la latence demandée est de 10 ms.
/// </summary>
public sealed class DriftBuffer : IWaveProvider
{
    /// <summary>
    /// Marge au-dessus du niveau visé avant de corriger. Elle absorbe la gigue normale du système
    /// (les blocs arrivent par paquets de quelques millisecondes) sans rien jeter.
    /// </summary>
    private const int HysteresisMs = 25;

    /// <summary>
    /// Nombre de blocs que le niveau visé doit couvrir. La sortie consomme un bloc entier d'un coup, et
    /// il faut de quoi tenir jusqu'au suivant même si la source livre le sien juste après la lecture :
    /// deux blocs et demi est le minimum qui ne laisse pas de trou.
    /// </summary>
    private const double BlockSafety = 2.5;

    /// <summary>
    /// Capacité du tampon, indépendante de la latence demandée : le niveau visé peut monter en cours de
    /// route si la sortie réclame de gros blocs (voir <see cref="Read"/>).
    /// </summary>
    private const int CapacityMs = 400;

    private readonly object _gate = new();
    private readonly byte[] _ring;
    private readonly int _frame;        // taille d'une trame (canaux × octets par échantillon)
    private readonly double _bytesPerMs;
    private readonly int _maxBytes;     // garde-fou absolu (source bloquée puis reprise…)
    private int _targetBytes;           // niveau visé : la latence demandée, relevée si nécessaire
    private int _highBytes;             // au-delà, on résorbe le retard d'un seul coup
    private int _read;
    private int _count;

    public DriftBuffer(WaveFormat format, int latencyMs)
    {
        WaveFormat = format;
        _frame = Math.Max(1, format.BlockAlign);
        _bytesPerMs = Math.Max(1, format.AverageBytesPerSecond / 1000.0);

        _targetBytes = Align((int)(_bytesPerMs * Math.Max(1, latencyMs)));
        _highBytes = _targetBytes + Align((int)(_bytesPerMs * HysteresisMs));
        _maxBytes = Align((int)(_bytesPerMs * CapacityMs));

        _ring = new byte[_maxBytes + _frame * 4];
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Niveau visé, en millisecondes : la latence réellement tenue par ce casque. Elle peut dépasser la
    /// latence demandée si le pilote réclame de gros blocs (voir <see cref="Read"/>).
    /// </summary>
    public double TargetMs => _targetBytes / _bytesPerMs;

    /// <summary>
    /// Plus gros bloc réclamé par la sortie, en millisecondes : c'est lui qui fixe la latence réellement
    /// atteignable avec ce périphérique.
    /// </summary>
    public double MaxBlockMs { get; private set; }

    /// <summary>
    /// Millisecondes de silence que la sortie a dû jouer faute de son : la source n'a pas fourni assez
    /// vite. Un compteur qui grimpe veut dire que la latence demandée est trop basse pour ce casque.
    /// </summary>
    public double StarvedMs { get; private set; }

    /// <summary>
    /// Millisecondes de son jetées pour résorber une avance : la source et la sortie ne battent pas
    /// exactement à la même horloge. Quelques millisecondes par minute sont normales et inaudibles.
    /// </summary>
    public double DroppedMs { get; private set; }

    private int Align(int bytes) => Math.Max(_frame, bytes / _frame * _frame);

    /// <summary>Reçoit un bloc du son capturé et le met en file pour cette sortie.</summary>
    public void AddSamples(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (count <= 0)
                return;

            // Bloc plus gros que le tampon : on ne garde que la fin, alignée sur une trame
            if (count >= _ring.Length)
            {
                offset += (count - _ring.Length) / _frame * _frame;
                count = _ring.Length / _frame * _frame;
                _read = 0;
                _count = 0;
            }

            // Fait de la place : on jette les trames les plus anciennes si nécessaire
            var free = _ring.Length - _count;
            if (count > free)
                DropOldest(count - free);

            var write = (_read + _count) % _ring.Length;
            var first = Math.Min(count, _ring.Length - write);
            Array.Copy(buffer, offset, _ring, write, first);
            if (count > first)
                Array.Copy(buffer, offset + first, _ring, 0, count - first);
            _count += count;

            // La source a produit beaucoup d'un coup (reprise après une pause) : on resynchronise
            if (_count > _maxBytes)
                DropOldest(_count - _targetBytes);
        }
    }

    /// <summary>Donne au périphérique le prochain bloc à jouer (appelé par WASAPI).</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            MaxBlockMs = Math.Max(MaxBlockMs, count / _bytesPerMs);

            // Une sortie réclame ses blocs par paquets entiers, et un paquet peut être bien plus gros que
            // la latence demandée (c'est le tampon du moteur, pas la période annoncée). On ne peut pas
            // jouer un paquet sans l'avoir en entier : le niveau visé monte donc à plusieurs paquets.
            // En dessous, la sortie reçoit du silence à la fin de chaque paquet — le son se coupe et
            // grésille, quel que soit le réglage de latence.
            var minimum = Align((int)Math.Ceiling(count * BlockSafety));
            if (minimum > _targetBytes && minimum < _maxBytes)
            {
                _targetBytes = minimum;
                _highBytes = _targetBytes + Align((int)(_bytesPerMs * HysteresisMs));
            }

            // Le casque a pris de l'avance sur la source : on résorbe le retard, mais seulement
            // au-delà de la marge. En deçà, on ne touche à rien : la gigue de quelques millisecondes
            // est normale et corriger à chaque bloc produisait un grésillement continu.
            if (_count > _highBytes)
                DropOldest(_count - _targetBytes);

            var n = Math.Min(count, _count);
            if (n > 0)
            {
                var first = Math.Min(n, _ring.Length - _read);
                Array.Copy(_ring, _read, buffer, offset, first);
                if (n > first)
                    Array.Copy(_ring, 0, buffer, offset + first, n - first);
                _read = (_read + n) % _ring.Length;
                _count -= n;
            }

            // Source en retard : on complète par du silence plutôt que de rejouer du son
            if (n < count)
            {
                Array.Clear(buffer, offset + n, count - n);
                StarvedMs += (count - n) / _bytesPerMs;
            }

            return count;
        }
    }

    private void DropOldest(int bytes)
    {
        var drop = bytes / _frame * _frame;
        if (drop <= 0)
            return;
        if (drop > _count)
            drop = _count;
        _read = (_read + drop) % _ring.Length;
        _count -= drop;
        DroppedMs += drop / _bytesPerMs;
    }
}
