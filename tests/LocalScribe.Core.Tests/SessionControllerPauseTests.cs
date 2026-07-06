using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using NAudio.Wave;
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
    public async Task Resume_that_falls_back_to_system_mix_writes_degraded_marker()
    {
        // Spec 12.1: the fallback must never be silent. A session that STARTED healthy
        // per-process can still degrade on a later Resume (e.g. the app's render session went
        // inactive during the pause) - that must get the same marker + Notice as a Start-time
        // fallback, exactly once even across further still-degraded pause/resume cycles.
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        var notices = new List<string>();
        c.Notice += notices.Add;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        // Remote render session goes away during the pause: the NEXT CreateRemote() call
        // (on Resume) falls back to system-mix.
        provider.RemoteSnapshot = new RemoteSnapshot
        { Mode = RemoteMode.SystemMix, App = "ms-teams", FellBackToSystemMix = true };

        clock.ElapsedMs = 2000;
        await c.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 5000;
        await c.ResumeAsync(CancellationToken.None);

        // Second pause/resume cycle while still degraded: no repeat marker.
        clock.ElapsedMs = 6000;
        await c.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 7000;
        await c.ResumeAsync(CancellationToken.None);

        clock.ElapsedMs = 8000;
        await c.StopAsync(CancellationToken.None);

        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        var degraded = lines.Where(l => l.Kind == TranscriptKind.Marker
            && l.Text == Markers.DegradedSystemAudioLoopback).ToList();
        Assert.Single(degraded);
        Assert.Equal(5000, degraded[0].StartMs);
        Assert.Contains(notices, n => n.Contains("system", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task Stop_while_paused_pads_audio_through_the_pause_tail()
    {
        // The ledger case: Pause stops capture but the session clock keeps ticking (spec 2.1),
        // so a Stop while paused ends with the clock far ahead of the last recorded sample.
        // Finalize must pad the retained files through the whole pause tail: 6000 ms at Stop
        // => 96000 samples per file, matching the recorded DurationMs exactly.
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(
            _root, new Settings { AudioFormat = AudioFormat.Wav });

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 6000;
        await c.StopAsync(CancellationToken.None);

        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
        {
            using var r = new WaveFileReader(paths.AudioFile(id!, kind, AudioFormat.Wav));
            Assert.Equal(96000, r.Length / r.WaveFormat.BlockAlign);   // 6000 ms * 16 samples/ms
        }

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(6000, record!.DurationMs);
    }
}
