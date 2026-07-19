using System.IO;
using System.IO.Pipes;
using System.Text;
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public class DeepLinkChannelTests
{
    // Guid-unique names: xUnit runs classes in parallel and pipe names are machine-global.
    private static string UniqueName() => "LocalScribe.DeepLink.test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void PipeNameFor_composes_the_per_user_name()
        => Assert.Equal("LocalScribe.DeepLink.S-1-5-21-x", DeepLinkChannel.PipeNameFor("S-1-5-21-x"));

    [Fact]
    public void Round_trips_one_argv_line_from_a_second_instance()
    {
        string name = UniqueName();
        var received = new List<string>();
        using var gate = new ManualResetEventSlim(false);
        using var server = DeepLinkChannel.StartServer(name,
            line => { lock (received) received.Add(line); gate.Set(); });

        Assert.True(DeepLinkChannel.TrySend(name, "localscribe://record/start?name=x"),
            "TrySend must reach the listening first instance");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(5)), "server never received the forwarded line");
        lock (received) Assert.Equal("localscribe://record/start?name=x", Assert.Single(received));
    }

    [Fact]
    public void TrySend_returns_false_when_no_server_is_listening()
        // Never throws - the second instance exits either way (the SignalExisting contract).
        => Assert.False(DeepLinkChannel.TrySend(UniqueName(), "localscribe://record/stop", timeoutMs: 250));

    [Fact]
    public void Server_survives_a_client_that_connects_and_writes_nothing()
    {
        string name = UniqueName();
        var received = new List<string>();
        using var gate = new ManualResetEventSlim(false);
        using var server = DeepLinkChannel.StartServer(name,
            line => { lock (received) received.Add(line); gate.Set(); });

        // A hostile/broken client: connect, write nothing, vanish. The listener's bounded read
        // (2 s) plus the fail-open loop must bring the pipe back up for the next real client.
        using (var silent = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly))
            silent.Connect(3000);

        Assert.True(DeepLinkChannel.TrySend(name, "localscribe://record/stop", timeoutMs: 8000),
            "a silent client must not wedge the listener");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(10)), "server never recovered after the silent client");
        lock (received) Assert.Equal("localscribe://record/stop", Assert.Single(received));
    }

    [Fact]
    public void Server_recovers_from_a_client_that_connects_and_holds_the_pipe_open()
    {
        // The test above ("...writes_nothing") disconnects immediately, which the server sees as
        // EOF - it recovers via the EOF path and never actually exercises the 2 s bounded read.
        // The real DoS vector is a client that connects and HOLDS the pipe open: never writes,
        // never disconnects. The only way the listener can move past that client is the bounded
        // read (ReadLineAsync(...).WaitAsync(2 s)) in DeepLinkChannel.Listen timing out and the
        // fail-open loop re-listening. This test keeps the holder's stream open for the whole
        // test body (disposed only in `finally`, after the assertions) so that path is the only
        // way the second, legitimate client can ever get served.
        string name = UniqueName();
        var received = new List<string>();
        using var gate = new ManualResetEventSlim(false);
        using var server = DeepLinkChannel.StartServer(name,
            line => { lock (received) received.Add(line); gate.Set(); });

        NamedPipeClientStream? holder = null;
        try
        {
            holder = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            holder.Connect(3000);
            // holder is now connected and stays connected - no write, no dispose - until finally.

            // A second, legitimate client. The server has only one pipe instance, so this
            // Connect() blocks until the listener abandons the holder (~2 s) and loops back to a
            // fresh NamedPipeServerStream. The bound here (8 s) is generous headroom above that
            // 2 s floor: if the bounded-read regressed (e.g. removed or lengthened), this Connect
            // times out and the test fails cleanly rather than hanging the run.
            using (var legit = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly))
            {
                legit.Connect(8000);
                using var writer = new StreamWriter(legit, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                { AutoFlush = true };
                writer.WriteLine("localscribe://record/start");
            }

            Assert.True(gate.Wait(TimeSpan.FromSeconds(6)),
                "the legitimate client's line never arrived - the listener may be wedged by the connect-and-hold client");
            lock (received) Assert.Equal("localscribe://record/start", Assert.Single(received));
        }
        finally
        {
            holder?.Dispose();
        }
    }
}
