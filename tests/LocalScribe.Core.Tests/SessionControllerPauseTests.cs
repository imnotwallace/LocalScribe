using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerPauseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-pause-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Pause_resume_stop_emits_markers_in_order_and_keeps_recording()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        clock.ElapsedMs = 2000;
        await c.PauseAsync(CancellationToken.None);
        Assert.Equal(SessionState.Paused, c.State);

        clock.ElapsedMs = 8000;
        await c.ResumeAsync(CancellationToken.None);
        Assert.Equal(SessionState.Recording, c.State);
        Assert.Equal(2, provider.MicCreates);               // fresh leg on resume

        clock.ElapsedMs = 10000;
        await c.StopAsync(CancellationToken.None);

        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        var markers = lines.Where(l => l.Kind == TranscriptKind.Marker).ToList();
        Assert.Contains(markers, m => m.Text == Markers.PausedByUser && m.StartMs == 2000);
        Assert.Contains(markers, m => m.Text == Markers.Resumed && m.StartMs == 8000);
        // Both legs' segments present: 2 sources x 2 legs = 4 segments.
        Assert.Equal(4, lines.Count(l => l.Kind == TranscriptKind.Segment));

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(10000, record!.DurationMs);            // clock ticks through pause (spec 2.1)
    }

    [Fact]
    public async Task Stop_while_paused_finalizes_without_double_flush()
    {
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 2000;
        await c.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 3000;
        string? stopped = await c.StopAsync(CancellationToken.None);

        Assert.Equal(id, stopped);
        Assert.Equal(SessionState.Idle, c.State);
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.NotNull(record!.EndedAtUtc);
        Assert.Equal(3000, record.DurationMs);
    }

    [Fact]
    public async Task Pause_when_idle_and_resume_when_recording_are_noops_with_notice()
    {
        var (c, _, _, _) = LiveTestDoubles.MakeController(_root);
        var notices = new List<string>();
        c.Notice += notices.Add;

        await c.PauseAsync(CancellationToken.None);          // idle: no-op
        Assert.Equal(SessionState.Idle, c.State);

        await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.ResumeAsync(CancellationToken.None);         // recording: no-op
        Assert.Equal(SessionState.Recording, c.State);
        Assert.Equal(2, notices.Count);
        await c.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Degraded_remote_writes_marker_and_notice()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
        provider.RemoteSnapshot = new RemoteSnapshot
        { Mode = RemoteMode.SystemMix, App = "ms-teams", FellBackToSystemMix = true };
        var notices = new List<string>();
        c.Notice += notices.Add;

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text == Markers.DegradedSystemAudioLoopback && l.StartMs == 0);
        Assert.Contains(notices, n => n.Contains("system", StringComparison.OrdinalIgnoreCase));

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.True(record!.Devices.Remote.FellBackToSystemMix);
    }

    [Fact]
    public async Task Silent_source_raises_SILENT_SOURCE_but_still_starts()
    {
        var (c, provider, _, _) = LiveTestDoubles.MakeController(_root);
        provider.LocalFrames = () => [new float[512], new float[512]];   // probe leg: all zeros
        var errors = new List<string>();
        c.ErrorRaised += errors.Add;

        string? id = await c.StartAsync(LiveTestDoubles.Options() with { RunPreflightProbe = true },
            CancellationToken.None);

        Assert.NotNull(id);                                  // warn-only: never blocks Start
        Assert.Contains("SILENT_SOURCE", errors);
        Assert.Equal(SessionState.Recording, c.State);
        // Probe consumed one throwaway source per side + one real source per side.
        Assert.Equal(2, provider.MicCreates);
        await c.StopAsync(CancellationToken.None);
    }
}
