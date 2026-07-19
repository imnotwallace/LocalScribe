using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace LocalScribe.App.Services;

/// <summary>Second-instance deep-link forwarding (design 2026-07-18 section 4), living BESIDE the
/// existing SingleInstance mutex guard (which stays the instance arbiter): the FIRST instance
/// listens on a per-user named pipe; a second instance launched by the OS for a localscribe:// URL
/// TrySend()s its argv line and exits. OS IPC, not a socket - the zero-network posture holds -
/// and PipeOptions.CurrentUserOnly on BOTH ends makes the OS enforce same-user access (no ACL
/// code). onLine fires ON THE BACKGROUND LISTENER THREAD - callers pass a dispatch-wrapped
/// action, exactly the SingleInstance.TryAcquire callback contract. Fail-open: a malformed,
/// silent, or crashed client logs nothing and can never kill the listener; the read is bounded
/// (2 s) so a hostile connect-and-hold client cannot wedge Dispose either.</summary>
public sealed class DeepLinkChannel : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _listener;

    private DeepLinkChannel(string pipeName, Action<string> onLine)
    {
        _listener = new Thread(() => Listen(pipeName, onLine))
        { IsBackground = true, Name = "LocalScribe.DeepLinkChannel" };
        _listener.Start();
    }

    /// <summary>Pure name composition, split out for tests.</summary>
    public static string PipeNameFor(string userToken) => "LocalScribe.DeepLink." + userToken;

    /// <summary>Per-user pipe name (SID-suffixed): two Windows users on one machine each run
    /// their own LocalScribe without fighting over the channel - the SingleInstance "Local\"
    /// scoping rationale applied to pipes (which have no Local\ namespace).</summary>
    public static string CurrentUserPipeName()
        => PipeNameFor(WindowsIdentity.GetCurrent().User?.Value ?? "anonymous");

    public static DeepLinkChannel StartServer(string pipeName, Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        return new DeepLinkChannel(pipeName, onLine);
    }

    /// <summary>Second-instance path: connect, write the one argv line, exit. False when no
    /// holder is reachable (or anything else goes wrong) - the caller exits either way, so
    /// failure here must never throw (the SignalExisting contract).</summary>
    public static bool TrySend(string pipeName, string line, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Utf8NoBom) { AutoFlush = true };
            writer.WriteLine(line);
            return true;
        }
        catch { return false; }
    }

    private void Listen(string pipeName, Action<string> onLine)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                using var reader = new StreamReader(server, Encoding.UTF8);
                // Bounded read: a client that connects and never writes gets 2 s, then the loop
                // re-listens (TimeoutException lands in the generic catch). Cancellation (Dispose)
                // surfaces as OperationCanceledException and ends the loop.
                string? line = reader.ReadLineAsync(_cts.Token).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(line)) onLine(line);
            }
            catch (OperationCanceledException) { break; }
            catch { /* fail-open: next client gets a fresh pipe; nothing is logged (no URL leaks) */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Join();   // bounded: WaitForConnectionAsync honors the token; reads time out at 2 s
        _cts.Dispose();
    }
}
