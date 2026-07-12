using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>Design 2026-07-12 section 2: SetRemoteCaptureAsync hot-swaps the remote leg while
/// Recording (build-before-commit, marker per resolved plan). Mirrors SessionControllerMuteTests'
/// harness (real controller over FakeProvider, FakeClock stamps, transcript read back at Stop).</summary>
public sealed class SessionControllerRemoteSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-remote-swap-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Switch_to_system_mix_writes_the_deliberate_marker()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        int afterStart = provider.RemoteCreates;

        clock.ElapsedMs = 4000;
        await c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.SystemMix }, CancellationToken.None);
        Assert.Equal(afterStart + 1, provider.RemoteCreates);          // fresh leg built + committed

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text == Markers.RemoteCaptureChangedSystemMix && l.StartMs == 4000);
    }

    [Fact]
    public async Task Switch_to_a_clean_app_writes_the_per_app_marker_with_the_resolved_image()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        clock.ElapsedMs = 3000;
        await c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" }, CancellationToken.None);

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text == "remote capture changed to per-app by user: Zoom" && l.StartMs == 3000);
    }

    [Fact]
    public async Task Switch_to_an_app_that_falls_back_reuses_the_degraded_marker_once()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        provider.ActiveSessions.Add(new AudioSessionInfo(6161, "ms-teams"));   // shared-audio -> fallback
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        clock.ElapsedMs = 2000;
        await c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.PerProcess, App = "ms-teams" }, CancellationToken.None);
        clock.ElapsedMs = 2500;
        await c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.PerProcess, App = "NotRunningApp" }, CancellationToken.None);

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Single(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.DegradedSystemAudioLoopback);
        Assert.DoesNotContain(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text.StartsWith("remote capture changed to per-app", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Build_before_commit_a_throwing_source_leaves_the_old_leg_and_writes_no_marker()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        clock.ElapsedMs = 3000;
        provider.ThrowOnNextRemoteCreate = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.SystemMix }, CancellationToken.None));
        Assert.Equal(SessionState.Recording, c.State);        // untouched, still recording

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.DoesNotContain(lines, l => l.Kind == TranscriptKind.Marker
            && (l.Text == Markers.RemoteCaptureChangedSystemMix || l.Text.StartsWith("remote capture changed", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StartLeg_activation_throw_falls_back_to_system_mix_so_remote_is_never_silently_lost()
    {
        // The defect this pins: ProcessLoopbackCapture's WASAPI activation runs in StartLeg/Start(),
        // AFTER StopLegAndFlushAsync has torn down the OLD working leg - not in the inert CreateRemote.
        // On that activation throw the old leg is gone; leaving remote dead would SILENTLY drop
        // counterparty audio (LOCKED evidentiary invariant). The fix falls back to full system mix so
        // remote keeps recording and records the involuntary degrade honestly (NOT a "by user" line).
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        var notices = new List<string>();
        c.Notice += notices.Add;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        clock.ElapsedMs = 4000;
        provider.ThrowOnNextLegStart = 1;   // the requested target's leg fails to ACTIVATE at StartLeg; the mix fallback succeeds
        await c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" }, CancellationToken.None);

        Assert.Equal(SessionState.Recording, c.State);   // remote still recording via system-mix fallback, not silently dead

        // CurrentRemoteTarget is now SystemMix: prove it via idempotency (a same-target re-request builds nothing).
        int afterFallback = provider.RemoteCreates;
        clock.ElapsedMs = 4500;
        await c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.SystemMix }, CancellationToken.None);
        Assert.Equal(afterFallback, provider.RemoteCreates);   // no-op -> CurrentRemoteTarget was set to SystemMix by the fallback

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        // The involuntary degrade marker is written exactly once; NO "by user" line and NO capture-lost line.
        Assert.Single(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.DegradedSystemAudioLoopback);
        Assert.DoesNotContain(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text.StartsWith("remote capture changed", StringComparison.Ordinal));   // no deliberate "by user" marker
        Assert.DoesNotContain(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.RemoteCaptureLost);
    }

    [Fact]
    public async Task StartLeg_and_system_mix_both_throw_records_remote_capture_lost_and_rethrows()
    {
        // The last-resort path (essentially never - whole-machine loopback): when BOTH the requested
        // target AND the system-mix fallback fail to activate, the remote leg is stopped but the loss
        // is RECORDED (RemoteCaptureLost marker + notice) instead of being silent, and the call
        // rethrows so the caller (SwitchRemoteTargetAsync) returns false and the picker reverts.
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        var notices = new List<string>();
        c.Notice += notices.Add;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        clock.ElapsedMs = 5000;
        provider.ThrowOnNextLegStart = 2;   // BOTH the target leg AND the system-mix fallback fail to activate at StartLeg
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" }, CancellationToken.None));

        Assert.Contains(notices, n => n.Contains("Remote capture stopped", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SessionState.Recording, c.State);   // local leg continues; state stays Recording

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text == Markers.RemoteCaptureLost && l.StartMs == 5000);
    }

    [Fact]
    public async Task Idempotent_same_target_is_a_noop()
    {
        var (c, provider, _, clock) = LiveTestDoubles.MakeController(_root);
        // Options() App=Webex but Settings.Remote defaults to Auto; start under Auto then re-request Auto.
        await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        int afterStart = provider.RemoteCreates;
        clock.ElapsedMs = 5000;
        await c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.Auto }, CancellationToken.None);
        Assert.Equal(afterStart, provider.RemoteCreates);      // same target as start -> nothing built
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }

    [Fact]
    public async Task Not_recording_is_a_noop_with_notice()
    {
        var (c, _, _, _) = LiveTestDoubles.MakeController(_root);
        var notices = new List<string>();
        c.Notice += notices.Add;
        await c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.SystemMix }, CancellationToken.None);
        Assert.Contains(notices, n => n.Contains("recording", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resume_readopts_the_settings_target_so_a_prior_live_switch_does_not_stick()
    {
        // Design section 5 (coverage gap): a live SetRemoteCaptureAsync diverges CurrentRemoteTarget
        // from the settings default (Auto). After Pause->Resume the remote leg is rebuilt from settings
        // (Auto), and ResumeAsync's `s.CurrentRemoteTarget = _settingsProvider().Remote;` must re-sync
        // CurrentRemoteTarget back to Auto - otherwise a later same-as-settings request would wrongly
        // rebuild. Proven by: after Resume, SetRemoteCaptureAsync(Auto) is an idempotent no-op.
        var (c, provider, _, clock) = LiveTestDoubles.MakeController(_root);   // Settings.Remote defaults to Auto
        await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        clock.ElapsedMs = 2000;
        await c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.SystemMix }, CancellationToken.None);
        // CurrentRemoteTarget is now SystemMix (diverged from settings Auto).

        // Pause then Resume: use whatever the controller's pause/resume API is (mirror
        // SessionControllerPauseTests). Resume rebuilds the remote leg from settings and must re-sync
        // CurrentRemoteTarget to Auto.
        await c.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 3000;
        await c.ResumeAsync(CancellationToken.None);
        int afterResume = provider.RemoteCreates;

        clock.ElapsedMs = 4000;
        await c.SetRemoteCaptureAsync(new RemoteSetting { Mode = RemoteMode.Auto }, CancellationToken.None);
        Assert.Equal(afterResume, provider.RemoteCreates);   // no-op -> CurrentRemoteTarget was re-synced to Auto at Resume

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }
}
