using System.IO;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

/// <summary>Mid-recording capture death (Tier 1B design 2026-08-05, T1-4a). Time is driven by
/// setting FakeClock.ElapsedMs and by calling PollCaptureHealth explicitly - the App's 150 ms
/// DispatcherTimer is what calls it in production, and there is no fake-timer package in this
/// repo.</summary>
public sealed class SessionControllerCaptureHealthTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-caphealth-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static LiveSessionOptions Options() =>
        LiveTestDoubles.Options() with { CaptureStallGraceMs = 8000 };

    /// <summary>THE GATE every clock-advancing fact in this class must pass through first.
    ///
    /// FakeCaptureSource pushes its preset frames into CaptureFrameBridge SYNCHRONOUSLY inside
    /// Start(), but LiveSourcePipeline's _audioLoop DRAINS them on a POOL THREAD and every drained
    /// frame calls EmitPeak -> PeakObserved -> OnFrameForWatchdog(clock.ElapsedMs). So
    /// `clock.ElapsedMs = 20_000; c.PollCaptureHealth();` issued straight after StartAsync returns
    /// is a RACE: if the drain has not finished, those frames stamp OnFrame(20_000) and the watchdog
    /// either never trips or is cleared the instant after it did - the "passes alone, fails under
    /// full-suite load" family this repo has already paid for five times. Wait for the frames to be
    /// OBSERVED, not for a duration.
    ///
    /// FakeProvider gives each leg SpeechThenSilence(4, 3) = 7 frames, so a started session emits
    /// 14 peaks in total (7 local + 7 remote). SpinWait.SpinUntil is the house idiom.
    ///
    /// Takes a READER DELEGATE, not the `ref int` the plan specified: a ref parameter cannot be
    /// used inside a lambda (CS1628), so the plan's signature could not compile. The counter itself
    /// still lives as a local in each fact and is still written with Interlocked.</summary>
    private static void AwaitFramesDrained(Func<int> peaks, int expected)
        => Assert.True(SpinWait.SpinUntil(() => peaks() >= expected, TimeSpan.FromSeconds(5)),
            $"capture frames never drained: saw {peaks()} of {expected} peaks");

    [Fact]
    public async Task A_leg_that_stops_producing_frames_is_marked_and_restarted()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        var stalled = new List<SourceKind>();
        c.CaptureStalled += k => { lock (stalled) stalled.Add(k); };
        int peaks = 0;
        c.PeakObserved += (_, _) => Interlocked.Increment(ref peaks);

        string? id = await c.StartAsync(Options(), CancellationToken.None);
        Assert.NotNull(id);
        Assert.Equal(1, provider.MicCreates);
        Assert.Equal(1, provider.RemoteCreates);

        // FakeCaptureSource emitted every frame synchronously inside StartLeg at clock 0 and nothing
        // can arrive after that - but the DRAIN is asynchronous, so gate on it before touching the
        // clock (see AwaitFramesDrained).
        AwaitFramesDrained(() => Volatile.Read(ref peaks), 14);

        clock.ElapsedMs = 20_000;
        c.PollCaptureHealth();
        await c.PendingCaptureRestart.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, stalled.OrderBy(k => k));
        Assert.Equal(2, provider.MicCreates);       // both legs rebuilt through the provider,
        Assert.Equal(2, provider.RemoteCreates);    // exactly as ResumeAsync rebuilds them

        clock.ElapsedMs = 30_000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;

        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        var markers = lines.Where(l => l.Kind == TranscriptKind.Marker
            && l.Text == Markers.AudioDeviceChanged).ToList();
        Assert.Equal(2, markers.Count);                                  // one per dead leg
        Assert.All(markers, m => Assert.Equal(20_000, m.StartMs));       // stamped at the detection instant
    }

    [Fact]
    public async Task A_stall_is_reported_once_not_on_every_tick()
    {
        var (c, _, _, clock) = LiveTestDoubles.MakeController(_root);
        int raised = 0;
        c.CaptureStalled += _ => Interlocked.Increment(ref raised);
        int peaks = 0;
        c.PeakObserved += (_, _) => Interlocked.Increment(ref peaks);

        await c.StartAsync(Options(), CancellationToken.None);
        AwaitFramesDrained(() => Volatile.Read(ref peaks), 14);

        clock.ElapsedMs = 20_000;
        c.PollCaptureHealth();
        await c.PendingCaptureRestart.WaitAsync(TimeSpan.FromSeconds(10));
        int afterFirst = Volatile.Read(ref raised);

        // The restarted legs re-seeded the watchdogs at 20_000 and their fresh fake sources emit 7
        // more frames each - gate on those too, then tick INSIDE the fresh grace window, which must
        // add nothing.
        AwaitFramesDrained(() => Volatile.Read(ref peaks), 28);
        clock.ElapsedMs = 25_000;
        c.PollCaptureHealth();
        await c.PendingCaptureRestart.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, afterFirst);                                     // one per leg, once
        Assert.Equal(afterFirst, Volatile.Read(ref raised));
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }

    [Fact]
    public async Task A_leg_that_never_recovers_is_restarted_at_most_CaptureRestartLimit_times()
    {
        // THE HAMMERING BUG. Both restart methods re-arm the watchdog on success, so a leg whose
        // source REBUILDS fine but still delivers no frames (dead endpoint, wedged driver, a
        // per-process target whose render session is gone) re-trips every CaptureStallGraceMs -
        // writing a FRESH Markers.AudioDeviceChanged into transcript.jsonl and firing a fresh tray
        // Notice every 8 seconds for the rest of the call. A 40-minute call would interleave ~300
        // identical markers with the evidence.
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        int peaks = 0;
        c.PeakObserved += (_, _) => Interlocked.Increment(ref peaks);

        string? id = await c.StartAsync(Options(), CancellationToken.None);
        AwaitFramesDrained(() => Volatile.Read(ref peaks), 14);

        // Every rebuilt source from here on is FRAMELESS, so each restart "succeeds" and the leg
        // still never produces anything - exactly the wedged-driver shape.
        provider.LocalFrames = Array.Empty<float[]>;
        provider.RemoteFrames = Array.Empty<float[]>;

        // Five stalls' worth of ticks. Attempts are spaced structurally - a restart re-arms the
        // watchdog, so a tick can only trip once another CaptureStallGraceMs of silence has
        // elapsed - and 120 s per tick is far beyond that grace, so every tick trips.
        for (int i = 1; i <= 5; i++)
        {
            clock.ElapsedMs = i * 120_000;
            c.PollCaptureHealth();
            await c.PendingCaptureRestart.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // 1 initial + 3 restarts, then the budget is spent and the leg stays flagged.
        Assert.Equal(4, provider.MicCreates);
        Assert.Equal(4, provider.RemoteCreates);

        clock.ElapsedMs = 700_000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;

        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        // Three device-changed markers per leg (one per attempted restart), then ONE terminal
        // marker per leg - six plus two, NOT ten. The evidence records the outage and its
        // abandonment; it does not become a log file.
        Assert.Equal(6, lines.Count(l => l.Kind == TranscriptKind.Marker
            && l.Text == Markers.AudioDeviceChanged));
        Assert.Equal(2, lines.Count(l => l.Kind == TranscriptKind.Marker
            && l.Text.StartsWith("capture did not come back", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_live_remote_re_target_re_arms_the_watchdog_instead_of_being_declared_dead()
    {
        // A fresh leg gets a FRESH window. Without the reseed in SetRemoteCaptureAsync, the new
        // leg inherits the old leg's last-frame stamp and is torn down by the watchdog it just
        // escaped if it takes longer than CaptureStallGraceMs to deliver its first frame - which is
        // ordinary for a per-process WASAPI activation on a busy machine.
        var (c, provider, _, clock) = LiveTestDoubles.MakeController(_root);
        int peaks = 0;
        c.PeakObserved += (_, _) => Interlocked.Increment(ref peaks);

        await c.StartAsync(Options(), CancellationToken.None);
        AwaitFramesDrained(() => Volatile.Read(ref peaks), 14);

        // The re-targeted leg produces NOTHING, so only the explicit reseed can re-arm it - a leg
        // that emitted frames would re-arm itself and prove nothing.
        provider.RemoteFrames = Array.Empty<float[]>;
        clock.ElapsedMs = 30_000;
        await c.SetRemoteCaptureAsync(
            new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" }, CancellationToken.None);

        clock.ElapsedMs = 37_000;                    // 7 s after the re-target: inside the grace
        c.PollCaptureHealth();
        await c.PendingCaptureRestart.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, provider.RemoteCreates);     // the re-target itself, and NO watchdog rebuild
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }

    [Fact]
    public async Task An_unmute_re_arms_the_local_watchdog_from_the_moment_the_fresh_leg_starts()
    {
        // Same rule on the mute path: SetLocalMuteAsync(false) builds and starts a BRAND NEW mic
        // leg, which must be measured from its own start instant. PollCaptureHealth re-arms a MUTED
        // leg on every tick, but nothing polls between the mute and the unmute in this test - so
        // without the reseed the fresh leg inherits a stamp from before the mute.
        var (c, provider, _, clock) = LiveTestDoubles.MakeController(_root);
        int peaks = 0;
        c.PeakObserved += (_, _) => Interlocked.Increment(ref peaks);

        await c.StartAsync(Options(), CancellationToken.None);
        AwaitFramesDrained(() => Volatile.Read(ref peaks), 14);

        await c.SetLocalMuteAsync(true, CancellationToken.None);
        provider.LocalFrames = Array.Empty<float[]>;     // the unmuted leg emits nothing
        clock.ElapsedMs = 60_000;
        await c.SetLocalMuteAsync(false, CancellationToken.None);
        Assert.Equal(2, provider.MicCreates);            // the unmute's own fresh leg

        clock.ElapsedMs = 67_000;                        // 7 s after the unmute: inside the grace
        c.PollCaptureHealth();
        await c.PendingCaptureRestart.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, provider.MicCreates);            // no watchdog rebuild
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }

    [Fact]
    public async Task Polling_while_idle_does_nothing_and_never_throws()
    {
        var (c, provider, _, _) = LiveTestDoubles.MakeController(_root);

        c.PollCaptureHealth();                                           // never started
        await c.PendingCaptureRestart;

        Assert.Equal(0, provider.MicCreates);
        Assert.Equal(SessionState.Idle, c.State);
    }

    [Fact]
    public async Task A_paused_session_never_trips_the_watchdog()
    {
        // Pause STOPS both legs, so zero frames is the correct, deliberate state. Restarting a leg
        // here would resume a recording the user paused - the worst possible false positive on a
        // privilege-protection feature.
        var (c, provider, _, clock) = LiveTestDoubles.MakeController(_root);
        await c.StartAsync(Options(), CancellationToken.None);
        await c.PauseAsync(CancellationToken.None);

        clock.ElapsedMs = 60_000;
        c.PollCaptureHealth();
        await c.PendingCaptureRestart;

        Assert.Equal(1, provider.MicCreates);                            // no rebuild
        Assert.Equal(SessionState.Paused, c.State);
    }

    [Fact]
    public async Task A_sleep_pause_and_resume_record_the_reason_and_the_lost_wall_clock_time()
    {
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(Options(), CancellationToken.None);

        clock.ElapsedMs = 5_000;
        await c.PauseAsync(CancellationToken.None, systemSleep: true);
        clock.ElapsedMs = 9_000;
        await c.ResumeAsync(CancellationToken.None, sleepGap: TimeSpan.FromMinutes(37));
        clock.ElapsedMs = 12_000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;

        var markers = (await new TranscriptStore(paths.TranscriptJsonl(id!))
            .ReadAllAsync(CancellationToken.None))
            .Where(l => l.Kind == TranscriptKind.Marker).ToList();

        // "paused: system sleep", not "paused by user" - a reader months later must be able to tell
        // a deliberate privileged pause from the machine suspending itself.
        Assert.Contains(markers, m => m.Text == Markers.PausedSystemSleep && m.StartMs == 5_000);
        Assert.DoesNotContain(markers, m => m.Text == Markers.PausedByUser);
        // The gap is the WALL-CLOCK time the machine was asleep, which the monotonic session clock
        // cannot see: it is measured by the App-side coordinator and passed in.
        Assert.Contains(markers, m => m.Text == "resumed after system sleep: 00:37:00 was not recorded"
            && m.StartMs == 9_000);
        Assert.DoesNotContain(markers, m => m.Text == Markers.Resumed);
    }

    [Fact]
    public async Task An_ordinary_pause_and_resume_still_write_the_ordinary_markers()
    {
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(Options(), CancellationToken.None);

        clock.ElapsedMs = 2_000;
        await c.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 8_000;
        await c.ResumeAsync(CancellationToken.None);
        clock.ElapsedMs = 10_000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;

        var markers = (await new TranscriptStore(paths.TranscriptJsonl(id!))
            .ReadAllAsync(CancellationToken.None))
            .Where(l => l.Kind == TranscriptKind.Marker).ToList();

        Assert.Contains(markers, m => m.Text == Markers.PausedByUser && m.StartMs == 2_000);
        Assert.Contains(markers, m => m.Text == Markers.Resumed && m.StartMs == 8_000);
    }

    [Fact]
    public async Task Start_is_refused_below_the_disk_floor_and_nothing_is_created()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root,
            freeBytesProbe: _ => 300L * 1024 * 1024);          // 300 MB free
        string? notice = null;
        c.Notice += n => notice = n;

        string? id = await c.StartAsync(Options(), CancellationToken.None);

        Assert.Null(id);                                        // refused exactly like the other guards
        Assert.Equal(SessionState.Idle, c.State);
        Assert.Equal(0, provider.MicCreates);                   // nothing built, no folder, no session.json
        Assert.False(Directory.Exists(paths.SessionsDir));
        Assert.Contains("Not enough free disk space", notice);
    }

    [Fact]
    public async Task Start_proceeds_when_free_space_cannot_be_measured()
    {
        var (c, _, _, _) = LiveTestDoubles.MakeController(_root, freeBytesProbe: _ => null);

        string? id = await c.StartAsync(Options(), CancellationToken.None);

        Assert.NotNull(id);                                     // fail OPEN, never on a guess
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }

    [Fact]
    public async Task Free_space_falling_mid_session_marks_and_warns_exactly_once()
    {
        long free = 8L * 1024 * 1024 * 1024;
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root, freeBytesProbe: _ => free);
        int warned = 0;
        c.LowDiskSpaceDetected += () => Interlocked.Increment(ref warned);

        string? id = await c.StartAsync(Options(), CancellationToken.None);

        free = 400L * 1024 * 1024;                              // the drive fills up mid-call
        clock.ElapsedMs = 60_000;                               // past the 30 s disk-poll interval
        c.PollCaptureHealth();
        await c.PendingCaptureRestart.WaitAsync(TimeSpan.FromSeconds(10));
        clock.ElapsedMs = 120_000;
        c.PollCaptureHealth();
        await c.PendingCaptureRestart.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref warned));             // once, not on every tick

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Single(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text.StartsWith("low disk space", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_muted_local_leg_is_never_restarted_by_the_watchdog()
    {
        // "Mute my side" deliberately stops the local leg and leaves it stopped - Resume itself
        // honours that. A watchdog restart would silently un-mute a user who muted for a
        // privileged aside: an evidentiary violation, not a recovery.
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        int peaks = 0;
        c.PeakObserved += (_, _) => Interlocked.Increment(ref peaks);

        string? id = await c.StartAsync(Options(), CancellationToken.None);
        // SetLocalMuteAsync awaits the LOCAL leg's flush, but the REMOTE leg is still draining on a
        // pool thread - and this test's whole point is that the remote leg IS restarted. Gate both.
        AwaitFramesDrained(() => Volatile.Read(ref peaks), 14);
        await c.SetLocalMuteAsync(true, CancellationToken.None);

        clock.ElapsedMs = 20_000;
        c.PollCaptureHealth();
        await c.PendingCaptureRestart.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, provider.MicCreates);                            // local NOT rebuilt
        Assert.Equal(2, provider.RemoteCreates);                         // remote still recovered

        clock.ElapsedMs = 30_000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;

        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Single(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text == Markers.AudioDeviceChanged);                   // remote only
    }
}
