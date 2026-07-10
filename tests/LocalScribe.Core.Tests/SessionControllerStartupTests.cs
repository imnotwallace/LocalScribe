using System.IO;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-startup-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Start_does_not_block_on_the_engine_build()
    {
        var gated = new GatedEngineFactory();                 // CreateAsync blocks synchronously until the gate is set
        var (c, provider, _, _) = LiveTestDoubles.MakeController(_root, engineFactory: gated);

        // Start must return (capture legs created, State == Recording) even though the engine
        // build is still blocked - i.e. capture is live before the model has loaded.
        var start = c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        string? id = await start.WaitAsync(TimeSpan.FromSeconds(5));   // today this TIMES OUT (RunAsync blocks inline at :380)

        Assert.NotNull(id);
        Assert.Equal(SessionState.Recording, c.State);
        Assert.True(provider.MicCreates > 0 && provider.RemoteCreates > 0);   // capture legs really started

        gated.CreateGate.Set();                               // let the (now background) engine build finish
        await c.StopAsync(CancellationToken.None);
    }
}
