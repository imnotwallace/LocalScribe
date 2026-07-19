using System.IO.Pipes;
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
}
