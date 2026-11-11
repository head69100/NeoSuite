using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Neo.Shared;

/// <summary>Une action qu'une appli Neo propose dans NeoSuite (clic droit sur sa tuile).</summary>
public sealed record NeoAction(string Command, string Text, string Glyph);

/// <summary>Ce qu'une appli Neo en cours d'exécution raconte à NeoSuite.</summary>
public sealed record NeoAppInfo(string? Status, IReadOnlyList<NeoAction> Actions);

/// <summary>Résultat d'une action, que NeoSuite affiche en notification.</summary>
public sealed record NeoActionResult(string? Title, string? Message);

/// <summary>
/// Communication entre les applis Neo et NeoSuite, par tube nommé (« Neo.&lt;App&gt; »).
/// Une requête = une ligne de texte, une réponse = une ligne de JSON.
/// Les droits du tube autorisent l'utilisateur courant, même si l'appli tourne en administrateur.
/// </summary>
public static class NeoIpc
{
    public const string DescribeRequest = "describe";
    public const string RunPrefix = "run:";

    private const string SuiteMutexName = @"Local\NeoSuite.SingleInstance";

    private static string PipeName(string appId) => $"Neo.{appId}";

    /// <summary>NeoSuite est-il lancé ? (les applis Neo cachent alors leur icône de barre système)</summary>
    public static bool IsSuiteRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(SuiteMutexName, out var mutex))
            {
                mutex.Dispose();
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // le verrou existe, mais appartient à un NeoSuite lancé en administrateur
        }
    }

    // ---------- Côté appli (serveur) ----------

    /// <summary>
    /// Écoute les requêtes de NeoSuite en arrière-plan. <paramref name="describe"/> et <paramref name="run"/>
    /// sont appelés sur un thread secondaire : à eux de passer sur le thread UI si besoin.
    /// </summary>
    public static void StartServer(string appId, Func<Task<NeoAppInfo>> describe, Func<string, Task<NeoActionResult?>> run)
    {
        var thread = new Thread(() => ServerLoop(appId, describe, run)) { IsBackground = true, Name = $"Neo.Ipc.{appId}" };
        thread.Start();
    }

    private static void ServerLoop(string appId, Func<Task<NeoAppInfo>> describe, Func<string, Task<NeoActionResult?>> run)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));

        while (true)
        {
            try
            {
                using var server = NamedPipeServerStreamAcl.Create(PipeName(appId), PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                server.WaitForConnection();

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var request = reader.ReadLine() ?? "";

                string response;
                if (request == DescribeRequest)
                    response = JsonSerializer.Serialize(describe().GetAwaiter().GetResult());
                else if (request.StartsWith(RunPrefix))
                    response = JsonSerializer.Serialize(run(request[RunPrefix.Length..]).GetAwaiter().GetResult());
                else
                    response = "null";

                writer.WriteLine(response);
                server.WaitForPipeDrain();
            }
            catch (Exception)
            {
                // Client parti en cours de route, tube cassé… on attend la requête suivante
                Thread.Sleep(100);
            }
        }
    }

    // ---------- Côté NeoSuite (client) ----------

    /// <summary>Demande à une appli lancée son état et ses actions ; null si elle ne répond pas.</summary>
    public static async Task<NeoAppInfo?> DescribeAsync(string appId, int timeoutMs = 800)
    {
        var json = await SendAsync(appId, DescribeRequest, timeoutMs);
        return json is null ? null : JsonSerializer.Deserialize<NeoAppInfo>(json);
    }

    /// <summary>Exécute une action dans l'appli ; certaines (recherche de mises à jour…) prennent du temps.</summary>
    public static async Task<NeoActionResult?> RunAsync(string appId, string command, int timeoutMs = 120_000)
    {
        var json = await SendAsync(appId, RunPrefix + command, timeoutMs);
        return json is null ? null : JsonSerializer.Deserialize<NeoActionResult>(json);
    }

    private static async Task<string?> SendAsync(string appId, string request, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await using var client = new NamedPipeClientStream(".", PipeName(appId), PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(Math.Min(timeoutMs, 500), cts.Token);

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(request.AsMemory(), cts.Token);
            return await reader.ReadLineAsync(cts.Token);
        }
        catch (Exception)
        {
            return null; // appli fermée, ancienne version sans communication, délai dépassé…
        }
    }
}
