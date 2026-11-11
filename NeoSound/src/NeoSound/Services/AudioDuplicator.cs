using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NeoSound.Services;

/// <summary>
/// Ce qu'une sortie a enduré depuis le démarrage : le son complété par du silence (elle manquait de son),
/// le son jeté pour résorber une avance (elle allait plus vite que la source), la latence réellement
/// tenue et la taille du plus gros bloc qu'elle réclame — c'est lui qui fixe la latence atteignable.
/// </summary>
public sealed record OutputHealth(string Name, double StarvedMs, double DroppedMs, double MaxBlockMs, double TargetMs);

/// <summary>
/// Le cœur du module : il capture en « loopback » ce qui est joué sur un périphérique de sortie,
/// puis renvoie le même son vers un ou plusieurs casques, chacun avec son propre tampon.
///
/// Fonctionnement (WASAPI, mode partagé, le plus bas retard raisonnablement stable) :
///   source (lecture par défaut) ── capture loopback ──► DriftBuffer ──► rééchantillonnage/canaux ──► WasapiOut
/// On ne redirige jamais vers la source elle-même : cela créerait une boucle (le son capturé serait
/// rejoué puis recapturé sans fin).
///
/// Chaque casque vit sa vie : s'il lâche en cours de route (débranché, Bluetooth déconnecté, pilote
/// réinitialisé), sa sortie est retirée et les autres continuent de jouer.
/// </summary>
public sealed class AudioDuplicator : IDisposable
{
    /// <summary>
    /// Protège les listes de sorties : la capture les parcourt à chaque bloc reçu, pendant qu'un casque
    /// peut disparaître de son côté.
    /// </summary>
    private readonly object _sync = new();

    private readonly List<WasapiOut> _outputs = [];
    private readonly List<DriftBuffer> _buffers = [];
    private readonly List<string> _targetNames = [];
    private readonly List<string> _lostOutputs = [];

    private WasapiLoopbackCapture? _capture;
    private MMDeviceEnumerator? _enumerator;

    /// <summary>Vrai pendant un arrêt : les sorties que l'on arrête nous-mêmes ne sont pas des casques perdus.</summary>
    private volatile bool _stopping;

    /// <summary>Vrai pendant l'ouverture des sorties, pour ne pas conclure un arrêt au milieu d'un démarrage.</summary>
    private volatile bool _starting;

    /// <summary>Prévient l'interface que la duplication elle-même s'est arrêtée (plus rien à alimenter, source perdue).</summary>
    public event Action<string>? Faulted;

    /// <summary>Prévient l'interface qu'un casque a lâché, sans interrompre les autres.</summary>
    public event Action<string, string>? OutputLost;

    public bool IsRunning { get; private set; }
    public string SourceName { get; private set; } = "";

    /// <summary>Casques réellement alimentés : une sortie perdue en cours de route n'y figure plus.</summary>
    public IReadOnlyList<string> TargetNames
    {
        get
        {
            lock (_sync)
                return _targetNames.ToArray();
        }
    }

    /// <summary>Casques perdus depuis le démarrage, sous la forme « nom : raison ».</summary>
    public IReadOnlyList<string> LostOutputs
    {
        get
        {
            lock (_sync)
                return _lostOutputs.ToArray();
        }
    }

    /// <summary>Pourquoi un casque n'a pas pu être ouvert (ou pourquoi il a lâché au démarrage).</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Démarre la duplication. <paramref name="sourceDeviceId"/> est le périphérique dont on capture le son ;
    /// <paramref name="targetDeviceIds"/> les casques qui le reçoivent (jamais la source).
    /// <paramref name="latencyMs"/> est la latence ajoutée par NeoSound (10 à 150 ms : réactif au minimum,
    /// plus sûr au-delà). Un casque dont la période est plus longue (Bluetooth) peut en réclamer davantage.
    /// </summary>
    public void Start(string sourceDeviceId, IReadOnlyList<string> targetDeviceIds, int latencyMs)
    {
        Stop();
        LastError = null;
        ClearLosses();

        var requestedMs = Math.Clamp(latencyMs, 10, 250);

        var enumerator = new MMDeviceEnumerator();
        _enumerator = enumerator;

        var source = enumerator.GetDevice(sourceDeviceId);
        _capture = new WasapiLoopbackCapture(source);
        var captureFormat = _capture.WaveFormat;
        SourceName = source.FriendlyName;

        _starting = true;
        foreach (var id in targetDeviceIds)
        {
            if (string.Equals(id, sourceDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                LastError = "Le périphérique source ne peut pas être aussi une sortie (cela créerait une boucle).";
                continue;
            }

            WasapiOut? output = null;
            try
            {
                var device = enumerator.GetDevice(id);
                var mix = device.AudioClient.MixFormat;
                var name = device.FriendlyName;

                var buffer = new DriftBuffer(captureFormat, requestedMs);
                ISampleProvider chain = buffer.ToSampleProvider();

                if (mix.SampleRate != captureFormat.SampleRate)
                    chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);

                chain = ConvertChannels(chain, captureFormat.Channels, mix.Channels);

                output = new WasapiOut(device, AudioClientShareMode.Shared, true, requestedMs);
                output.Init(ToOutput(chain));
                output.PlaybackStopped += (_, e) => OnOutputStopped(output, name, e);

                // Enregistrée avant Play() : si le casque lâche dès la première seconde, le gestionnaire
                // la retrouve dans la liste et peut la retirer proprement.
                lock (_sync)
                {
                    _buffers.Add(buffer);
                    _outputs.Add(output);
                    _targetNames.Add(name);
                }

                output.Play();
            }
            catch (Exception ex)
            {
                Forget(output);
                var failure = $"« {NameOf(enumerator, id)} » : {Explain(ex)}";
                LastError = LastError is null ? failure : LastError + " ; " + failure;
            }
        }
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        try
        {
            _capture.StartRecording();
        }
        catch (Exception)
        {
            // La source refuse de démarrer (déjà prise par une autre application, par exemple) : on
            // remet tout à plat avant de laisser remonter l'erreur, sinon le démarrage suivant
            // croirait encore être en train d'ouvrir ses sorties.
            _starting = false;
            Stop();
            throw;
        }

        // À partir d'ici, une sortie qui s'arrête est un incident de parcours et non plus un échec
        // d'ouverture. Les deux lectures se font sous le même verrou que celui des sorties, pour
        // qu'aucune ne puisse disparaître entre le constat et la conclusion.
        string? lostDuringStart;
        bool hungry;
        lock (_sync)
        {
            _starting = false;
            hungry = _outputs.Count == 0;
            lostDuringStart = _lostOutputs.Count == 0 ? null : string.Join(" ; ", _lostOutputs);
            if (!hungry)
                IsRunning = true;
        }

        // Un casque peut avoir lâché pendant l'ouverture : c'est un échec au démarrage, et il doit
        // être signalé comme tel plutôt que passé sous silence.
        if (lostDuringStart is not null)
            LastError = LastError is null ? lostDuringStart : LastError + " ; " + lostDuringStart;

        if (hungry)
        {
            var reason = LastError ?? "Aucun casque n'a pu être ouvert.";
            Stop();
            throw new InvalidOperationException(reason);
        }
    }

    /// <summary>
    /// Traduit les refus habituels du moteur audio : un code hexadécimal ne dit rien à personne, alors
    /// que la cause, si.
    /// </summary>
    private static string Explain(Exception ex) => ex.HResult switch
    {
        unchecked((int)0x88890004) =>
            "le périphérique a été invalidé en cours d'ouverture (fréquent quand deux sorties du même " +
            "appareil sont utilisées en même temps, par exemple les canaux « jeu » et « chat » d'un casque)",
        unchecked((int)0x8889000A) => "le périphérique est déjà occupé en mode exclusif par une autre application",
        unchecked((int)0x88890008) => "le pilote refuse le format du flux",
        unchecked((int)0x80070057) => "le pilote refuse le format du flux",
        _ => ex.Message,
    };

    /// <summary>Même travail, mais pour un casque qui lâche alors qu'il jouait déjà.</summary>
    private static string ExplainLost(Exception ex) => ex.HResult switch
    {
        unchecked((int)0x88890004) => "le périphérique a disparu (débranché, Bluetooth déconnecté ou pilote réinitialisé)",
        unchecked((int)0x8889000A) => "le périphérique est passé en mode exclusif dans une autre application",
        unchecked((int)0x88890008) => "le pilote a cessé de fonctionner",
        _ => ex.Message,
    };

    /// <summary>
    /// Adapte le nombre de canaux : la source peut être mono, stéréo ou 5.1, le casque presque toujours
    /// stéréo. Renvoyer un format que WASAPI refuse ferait échouer la sortie ; on répartit donc les canaux
    /// disponibles (une source 5.1 alimente les deux canaux du casque).
    /// </summary>
    private static ISampleProvider ConvertChannels(ISampleProvider provider, int from, int to)
    {
        if (from == to)
            return provider;
        if (from == 1 && to == 2)
            return new MonoToStereoSampleProvider(provider);
        if (from == 2 && to == 1)
            return new StereoToMonoSampleProvider(provider);

        // Cas inhabituel (source 5.1 vers un casque stéréo, par exemple) : on répartit les canaux
        // disponibles au lieu de laisser WASAPI refuser un format qui ne lui convient pas.
        var splitter = new MultiplexingSampleProvider([provider], to);
        for (var channel = 0; channel < to; channel++)
            splitter.ConnectInputToOutput(channel % from, channel);
        return splitter;
    }

    /// <summary>
    /// Prépare le flux à confier à WASAPI. Au-delà de deux canaux, le descriptif doit être
    /// « extensible » : un simple descriptif à six canaux est refusé par le moteur audio
    /// (« Value does not fall within the expected range ») et le casque reste muet.
    /// </summary>
    private static IWaveProvider ToOutput(ISampleProvider chain)
    {
        var wave = chain.ToWaveProvider();
        if (wave.WaveFormat.Channels <= 2)
            return wave;

        var header = new WaveFormatExtensible(
            wave.WaveFormat.SampleRate, wave.WaveFormat.BitsPerSample, wave.WaveFormat.Channels);
        return new WaveHeader(wave, header);
    }

    /// <summary>
    /// Présente un flux existant sous un autre descriptif de format. Le son est inchangé (même encodage),
    /// seul l'en-tête diffère : c'est ce qu'exige WASAPI au-delà de deux canaux.
    /// </summary>
    private sealed class WaveHeader(IWaveProvider source, WaveFormat header) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = header;

        public int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Un même bloc, recopié dans le tampon de chaque casque encore en place
        lock (_sync)
        {
            foreach (var buffer in _buffers)
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
            return;
        LastError = e.Exception.Message;
        var handler = Faulted;
        Stop();
        handler?.Invoke(LastError);
    }

    /// <summary>
    /// Un casque a lâché : sa sortie est retirée de la liste et l'interface est prévenue, mais les autres
    /// casques continuent de jouer. Quand plus rien ne reste à alimenter, la duplication n'a plus d'objet
    /// et s'arrête d'elle-même.
    /// </summary>
    private void OnOutputStopped(WasapiOut output, string name, StoppedEventArgs e)
    {
        if (_stopping)
            return; // arrêt demandé par nous-mêmes : le casque n'a pas lâché

        string reason;
        bool nothingLeft;
        lock (_sync)
        {
            var index = _outputs.IndexOf(output);
            if (index < 0)
                return; // déjà retirée : arrêt normal, échec d'ouverture, ou événement en double

            _outputs.RemoveAt(index);
            _buffers.RemoveAt(index);
            _targetNames.RemoveAt(index);

            reason = e.Exception is null ? "la sortie s'est arrêtée" : ExplainLost(e.Exception);
            _lostOutputs.Add($"{name} : {reason}");
            nothingLeft = _outputs.Count == 0;

            // En pleine ouverture : Start() conclura lui-même, et signalera l'échec comme tel.
            if (_starting)
                return;
        }

        OutputLost?.Invoke(name, reason);

        if (nothingLeft)
        {
            LastError = $"Plus aucun casque n'alimente la duplication ({name} : {reason}).";
            var handler = Faulted;
            Stop();
            handler?.Invoke(LastError);
        }
    }

    /// <summary>Retire une sortie des listes suivies (échec d'ouverture, nettoyage).</summary>
    private void Forget(WasapiOut? output)
    {
        if (output is null)
            return;
        lock (_sync)
        {
            var index = _outputs.IndexOf(output);
            if (index < 0)
                return;
            _outputs.RemoveAt(index);
            _buffers.RemoveAt(index);
            _targetNames.RemoveAt(index);
        }
    }

    private static string NameOf(MMDeviceEnumerator enumerator, string id)
    {
        try
        {
            return enumerator.GetDevice(id).FriendlyName;
        }
        catch (Exception)
        {
            return id;
        }
    }

    /// <summary>
    /// Ce que chaque sortie a enduré depuis le démarrage : le son qu'elle a dû compléter par du silence
    /// (elle manquait de son) et celui qu'elle a jeté pour résorber une avance (sa pendule avance sur
    /// celle de la source). Sert à savoir s'il faut plus de latence ou si tout se comporte normalement.
    /// </summary>
    public IReadOnlyList<OutputHealth> Health
    {
        get
        {
            lock (_sync)
            {
                var list = new List<OutputHealth>(_buffers.Count);
                for (var i = 0; i < _buffers.Count; i++)
                    list.Add(new OutputHealth(
                        _targetNames[i],
                        _buffers[i].StarvedMs,
                        _buffers[i].DroppedMs,
                        _buffers[i].MaxBlockMs,
                        _buffers[i].TargetMs));
                return list;
            }
        }
    }

    /// <summary>Oublie les pertes précédentes. Le journal reste lisible après un arrêt, d'où ce nettoyage
    /// explicite au démarrage suivant (et non dans <see cref="Stop"/>).</summary>
    private void ClearLosses()
    {
        lock (_sync)
            _lostOutputs.Clear();
    }

    /// <summary>Arrête la capture et libère les sorties. Sans effet si rien ne tourne.</summary>
    public void Stop()
    {
        _stopping = true;

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                _capture.StopRecording();
            }
            catch (Exception)
            {
                // Déjà arrêtée : rien à faire
            }
            _capture.Dispose();
            _capture = null;
        }

        // Les sorties quittent la liste avant d'être arrêtées : leur arrêt déclenche PlaybackStopped,
        // et le gestionnaire doit alors constater qu'elles ne sont plus suivies (ce n'est pas une perte).
        WasapiOut[] outputs;
        lock (_sync)
        {
            outputs = [.. _outputs];
            _outputs.Clear();
            _buffers.Clear();
            _targetNames.Clear();
        }

        foreach (var output in outputs)
        {
            try
            {
                output.Stop();
            }
            catch (Exception)
            {
                // Déjà arrêtée
            }
            output.Dispose();
        }

        _enumerator?.Dispose();
        _enumerator = null;
        IsRunning = false;
        _stopping = false;
    }

    public void Dispose() => Stop();
}
