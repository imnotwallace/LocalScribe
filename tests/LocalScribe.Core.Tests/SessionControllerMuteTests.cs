using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using NAudio.Wave;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerMuteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-mute-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Mute_and_unmute_write_markers_and_restart_a_fresh_local_leg()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.False(c.LocalMuted);

        clock.ElapsedMs = 2000;
        await c.SetLocalMuteAsync(true, CancellationToken.None);
        Assert.True(c.LocalMuted);
        Assert.Equal(SessionState.Recording, c.State);          // mute is not Pause: session keeps recording
        Assert.Equal(1, provider.MicCreates);                   // leg stopped, no new source

        clock.ElapsedMs = 5000;
        await c.SetLocalMuteAsync(false, CancellationToken.None);
        Assert.False(c.LocalMuted);
        Assert.Equal(2, provider.MicCreates);                   // fresh local leg, like Resume

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.LocalMuted && l.StartMs == 2000);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.LocalUnmuted && l.StartMs == 5000);
    }

    [Fact]
    public async Task Mute_is_idempotent_no_duplicate_markers()
    {
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.SetLocalMuteAsync(true, CancellationToken.None);
        await c.SetLocalMuteAsync(true, CancellationToken.None);   // second call: no-op
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Single(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.LocalMuted);
    }

    [Fact]
    public async Task Resume_honors_mute_and_never_silently_unmutes()
    {
        var (c, provider, _, clock) = LiveTestDoubles.MakeController(_root);
        await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.SetLocalMuteAsync(true, CancellationToken.None);
        clock.ElapsedMs = 2000;
        await c.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 3000;
        await c.ResumeAsync(CancellationToken.None);

        Assert.True(c.LocalMuted);                              // still muted after Resume
        Assert.Equal(1, provider.MicCreates);                   // local leg NOT restarted
        Assert.Equal(2, provider.RemoteCreates);                // remote restarted normally

        await c.SetLocalMuteAsync(false, CancellationToken.None);
        Assert.Equal(2, provider.MicCreates);                   // explicit unmute restarts it
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }

    [Fact]
    public async Task Mute_while_paused_flips_state_and_marker_only()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 2000;
        await c.SetLocalMuteAsync(true, CancellationToken.None); // no legs run while paused
        Assert.True(c.LocalMuted);
        Assert.Equal(1, provider.MicCreates);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.LocalMuted && l.StartMs == 2000);
    }

    [Fact]
    public async Task Stop_while_muted_finalizes_with_audio_padded_to_the_stop_instant()
    {
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(
            _root, new Settings { AudioFormat = AudioFormat.Wav });
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.SetLocalMuteAsync(true, CancellationToken.None);
        clock.ElapsedMs = 6000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;

        using var r = new WaveFileReader(paths.AudioFile(id!, SourceKind.Local, AudioFormat.Wav));
        Assert.Equal(96000, r.Length / r.WaveFormat.BlockAlign);   // 6000 ms * 16 samples/ms — muted tail is silence
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(6000, record!.DurationMs);
    }

    [Fact]
    public async Task Mute_when_idle_is_a_noop_with_notice()
    {
        var (c, _, _, _) = LiveTestDoubles.MakeController(_root);
        var notices = new List<string>();
        c.Notice += notices.Add;
        await c.SetLocalMuteAsync(true, CancellationToken.None);
        Assert.False(c.LocalMuted);
        Assert.Contains(notices, n => n.Contains("mute", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Fake_capture_source_raises_device_mute_events()
    {
        var fake = new Audio.FakeCaptureSource(Audio.SourceKind.Local, new[] { new float[512] });
        var seen = new List<bool>();
        ((Audio.IEndpointMuteObservable)fake).DeviceMuteChanged += seen.Add;
        fake.RaiseDeviceMute(true);
        fake.RaiseDeviceMute(false);
        Assert.Equal(new[] { true, false }, seen);
        Assert.False(((Audio.IEndpointMuteObservable)fake).DeviceMuted);
    }

    [Fact]
    public async Task Device_mute_change_writes_marker_and_raises_event()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        var events = new List<bool>();
        c.MicDeviceMuteChanged += events.Add;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        clock.ElapsedMs = 3000;
        provider.LastMicFake!.RaiseDeviceMute(true);
        clock.ElapsedMs = 4000;
        provider.LastMicFake!.RaiseDeviceMute(false);

        Assert.Equal(new[] { true, false }, events);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.MicDeviceMuted && l.StartMs == 3000);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.MicDeviceUnmuted && l.StartMs == 4000);
    }

    [Fact]
    public async Task Device_already_muted_at_start_is_surfaced_immediately()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
        provider.NextMicDeviceMuted = true;
        var events = new List<bool>();
        c.MicDeviceMuteChanged += events.Add;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.Equal(new[] { true }, events);                    // no waiting for a change
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.MicDeviceMuted);
    }

    [Fact]
    public async Task Device_already_muted_at_resume_is_surfaced_immediately()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        var events = new List<bool>();
        c.MicDeviceMuteChanged += events.Add;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.PauseAsync(CancellationToken.None);
        provider.NextMicDeviceMuted = true;                      // endpoint muted while paused
        clock.ElapsedMs = 2000;
        await c.ResumeAsync(CancellationToken.None);
        Assert.Equal(new[] { true }, events);                    // surfaced at Resume, not next transition
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.MicDeviceMuted && l.StartMs == 2000);
    }

    [Fact]
    public async Task Device_mute_is_suppressed_while_locally_muted()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        var events = new List<bool>();
        c.MicDeviceMuteChanged += events.Add;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.SetLocalMuteAsync(true, CancellationToken.None); // user muted deliberately
        provider.LastMicFake!.RaiseDeviceMute(true);             // device mute while our leg is stopped
        Assert.Empty(events);                                    // nothing to warn about
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.DoesNotContain(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.MicDeviceMuted);
    }
}
