using System.IO;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerStopFinalizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-stopfin-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Stop_records_duration_at_the_stop_instant_not_after_the_drain()
    {
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root);
        clock.ElapsedMs = 0;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);

        clock.ElapsedMs = 34_000;                              // user talks 34s, then clicks Stop
        string? stopped = await c.StopAsync(CancellationToken.None);
        clock.ElapsedMs = 49_000;                              // clock would keep ticking during a drain

        // The transcription tail (and therefore the final session.json write) now finishes on a
        // background task, so wait for it before reading the persisted duration.
        await c.PendingFinalize;
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(34_000, record!.DurationMs);              // NOT 49_000 (the snapshot is taken before any drain)
    }

    [Fact]
    public async Task Stop_returns_before_transcription_finishes_then_backfills_the_transcript()
    {
        var gated = new GatedEngineFactory();                 // engine build blocked = transcription cannot drain
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);
        clock.ElapsedMs = 5_000;

        // Stop must return promptly and go Idle even though the engine build (and thus the drain) is blocked.
        string? stopped = await c.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, stopped);
        Assert.Equal(SessionState.Idle, c.State);
        Assert.False(c.PendingFinalize.IsCompleted);          // tail still draining in the background

        var flac = new FileInfo(paths.AudioFile(id!, SourceKind.Local, AudioFormat.Flac));
        Assert.True(flac.Length > 0);                         // audio already finalized at Stop

        gated.CreateGate.Set();                               // let the background drain + re-finalize complete
        await c.PendingFinalize.WaitAsync(TimeSpan.FromSeconds(10));
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.NotNull(record!.EndedAtUtc);                   // session finalized after the tail landed
    }

    [Fact]
    public async Task Start_waits_for_a_prior_background_finalize()
    {
        var gated = new GatedEngineFactory();   // engine build (and thus the drain + finalize) stays blocked
        var (c, _, _, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);

        string? id1 = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id1);
        clock.ElapsedMs = 3000;
        await c.StopAsync(CancellationToken.None);          // audio finalized; transcription tail deferred
        Assert.False(c.PendingFinalize.IsCompleted);        // finalize blocked on the gated engine build

        // A new Start must WAIT for the prior finalize (one engine at a time): it cannot complete while blocked.
        var start2 = c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await Task.Delay(200);
        Assert.False(start2.IsCompleted);                   // gated on _pendingFinalize

        gated.CreateGate.Set();                             // unblocks session 1's finalize AND session 2's build
        string? id2 = await start2.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(id2);
        Assert.NotEqual(id1, id2);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }
}
