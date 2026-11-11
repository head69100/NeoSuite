using System.IO.Pipes;
using System.Text;

namespace NeoMedia.Services;

/// <summary>
/// « Lire avec NeoMedia » sur plusieurs fichiers : l'Explorateur lance un NeoMedia par fichier.
/// Le premier lancé reçoit les chemins des autres (tube nommé) pour en faire une seule liste de lecture ;
/// les suivants lui transmettent leur fichier puis se ferment.
/// </summary>
public static class FileCollector
{
    private static readonly string Name = $"NeoMedia.Open.{Environment.UserName}";

    /// <summary>
    /// Renvoie tous les chemins à lire si cette instance est la première, ou null si elle
    /// a transmis ses chemins à une autre (elle doit alors se fermer).
    /// </summary>
    public static List<string>? Collect(IReadOnlyList<string> paths)
    {
        var mutex = new Mutex(true, $@"Local\{Name}", out var first);
        if (!first)
        {
            mutex.Dispose();
            Send(paths);
            return null;
        }

        var all = new List<string>(paths);
        var quietUntil = DateTime.UtcNow.AddMilliseconds(700);
        while (DateTime.UtcNow < quietUntil)
        {
            using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var wait = server.WaitForConnectionAsync();
            if (!wait.Wait(quietUntil - DateTime.UtcNow))
                break;
            using var reader = new StreamReader(server, Encoding.UTF8);
            while (reader.ReadLine() is { Length: > 0 } line)
                all.Add(line);
            quietUntil = DateTime.UtcNow.AddMilliseconds(500); // d'autres fichiers arrivent peut-être
        }

        mutex.ReleaseMutex();
        mutex.Dispose();
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Send(IReadOnlyList<string> paths)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out);
                client.Connect(200);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                foreach (var path in paths)
                    writer.WriteLine(path);
                return;
            }
            catch (Exception)
            {
                // Le premier NeoMedia n'écoute pas encore, ou sert un autre fichier : on réessaie
            }
        }
    }
}
