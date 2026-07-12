# Capture Scope Control Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the user real control over the remote capture target — Auto / a specific app / full system mix — both at Start (a live-refreshing picker in the Record console) and mid-recording (hot-swap the remote leg with a confirm gate for system mix), recorded evidentiarily as transcript markers.

**Architecture:** A generalized per-session `RemoteTargetOverride` (holding a full `RemoteSetting`) composes over the one live `Func<Settings>` seam, so Start/Resume adopt the chosen target with zero new plumbing. A new capture-provider overload builds a source for an *explicit* target, and a new `SessionController.SetRemoteCaptureAsync` hot-swaps the remote leg (build-before-commit, VAD-flush, silence-padded) exactly like the existing `SetLocalMuteAsync`/`ResumeAsync` templates, emitting a marker per the actually-resolved plan. The Record console's free-text app box is replaced by a live-refreshing `RemoteTargetOption` picker (friendly labels via `AppKindResolver`, known fallbacks pinned), and the recording toolbar gains a "Change target" row routed through a confirm dialog for system mix.

**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, xUnit.

## Global Constraints
- Target branch: `feat/capture-scope-control` (the design spec is already committed there at `docs/plans/2026-07-12-capture-scope-control-design.md`).
- 0-warning build gate must hold.
- Tests: xUnit. Run a filtered test with: `dotnet test "<testproj.csproj>" --filter "FullyQualifiedName~<Name>" --nologo`
- IMPORTANT: the LocalScribe app may be running and LOCK its bin DLL/exe (MSB3027 copy error — NOT a compile error). When that happens, build/test to an isolated output so the lock is avoided: append `-p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` to the dotnet test command. Never kill the user's app.
- Never use Unicode emojis in test code or scripts (project rule).
- Two test projects are in play: `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj` (Core) and `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj` (App).
- Commit messages follow the repo style: `fix(app)`/`feat(app)`/`test(app)`/`docs(...)`. Every commit message MUST end with these two trailer lines EXACTLY:
```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
```

---

## Task 1: Known-targets table + friendly labels + public FullMix check (Core, pure)

**Files:**
- Modify `src\LocalScribe.Core\Live\RemoteCapturePlanner.cs` (add `KnownTargets` at ~20; make `IsFullMix` public at ~61).
- Modify `src\LocalScribe.Core\Live\AppKindResolver.cs` (add `FriendlyName` after `FromProcessImage`, ~22).
- Modify `tests\LocalScribe.Core.Tests\RemoteCapturePlannerTests.cs` (add table + IsFullMix tests).
- Modify `tests\LocalScribe.Core.Tests\AppKindResolverTests.cs` (add `FriendlyName` theory).

**Interfaces:**
- Produces: `RemoteCapturePlanner.KnownTargets : IReadOnlyList<(string Friendly, string Image)>`; `RemoteCapturePlanner.IsFullMix(string image) : bool` (now public); `AppKindResolver.FriendlyName(string? image) : string?`.
- Consumes: existing `AppKindResolver.FromProcessImage`, `AppKind`.
- `SuggestedPerProcessApps` STAYS (do not remove it): besides the console it is also consumed by the Settings-page persistent-default picker (`SettingsPageViewModel.RemoteAppSuggestions` at `:152` -> `SettingsPage.xaml:74`, pinned by `SettingsPageViewModelTests.cs:257`), which is out of this feature's scope. Only the console's `AppSuggestions` property is removed (Task 6).

Steps:
- [ ] Add the failing tests to `AppKindResolverTests.cs` (append inside the class):
```csharp
    [Theory]
    [InlineData("CiscoCollabHost", "Webex")]
    [InlineData("Webex", "Webex")]
    [InlineData("Zoom", "Zoom")]
    [InlineData("ms-teams", "Teams")]
    [InlineData("chrome", "Browser")]
    [InlineData("msedgewebview2", "Browser")]
    [InlineData("Spotify", null)]           // unknown -> no friendly suffix
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FriendlyName_maps_known_images(string? image, string? expected)
        => Assert.Equal(expected, AppKindResolver.FriendlyName(image));
```
- [ ] Add the failing tests to `RemoteCapturePlannerTests.cs` (append inside the class):
```csharp
    [Fact]
    public void KnownTargets_single_sources_the_friendly_fallbacks()
    {
        Assert.Contains(("Webex", "CiscoCollabHost"), RemoteCapturePlanner.KnownTargets);
        Assert.Contains(("Zoom", "Zoom"), RemoteCapturePlanner.KnownTargets);
    }

    [Fact]
    public void IsFullMix_flags_shared_audio_apps_only()
    {
        Assert.True(RemoteCapturePlanner.IsFullMix("chrome"));
        Assert.True(RemoteCapturePlanner.IsFullMix("ms-teams"));
        Assert.False(RemoteCapturePlanner.IsFullMix("CiscoCollabHost"));
        Assert.False(RemoteCapturePlanner.IsFullMix("Zoom"));
    }
```
- [ ] Run them and see them FAIL (KnownTargets/IsFullMix/FriendlyName do not exist / are not accessible):
```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AppKindResolverTests|FullyQualifiedName~RemoteCapturePlannerTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
Expected: compile error / `FriendlyName`, `KnownTargets`, `IsFullMix` not found.
- [ ] Implement `KnownTargets` in `RemoteCapturePlanner.cs`. Add directly under the existing `SuggestedPerProcessApps` property (after line 20):
```csharp
    /// <summary>The friendly-name -> capture-image table for the console's Remote-target picker
    /// (design 2026-07-12 section 1): the ONLY per-process fallbacks always offered even when not
    /// live. "Webex" targets CiscoCollabHost.exe (Stage-1 finding); "Zoom" targets Zoom. Single-
    /// sourced so labels/fallbacks are testable and never drift from the planner's own matching.</summary>
    public static IReadOnlyList<(string Friendly, string Image)> KnownTargets { get; } =
        [("Webex", "CiscoCollabHost"), ("Zoom", "Zoom")];
```
- [ ] Make `IsFullMix` public in `RemoteCapturePlanner.cs`. Change the existing line (~61):
```csharp
    private static bool IsFullMix(string image)
```
to:
```csharp
    /// <summary>True when per-process loopback for this image is silent (all-zeros) or bleeds
    /// (shared-audio browsers/webviews) and the planner therefore forces system mix. Public so the
    /// console picker can annotate such items "(captured as system mix)" (design section 1).</summary>
    public static bool IsFullMix(string image)
```
- [ ] Implement `FriendlyName` in `AppKindResolver.cs`. Add after the `FromProcessImage` method (after line 22, before the private `Has`):
```csharp
    /// <summary>The picker's friendly label for a live-discovered process image (design 2026-07-12
    /// section 1): "Webex"/"Zoom"/"Teams"/"Browser" for a recognized image, null for an unknown one
    /// (the picker then shows the bare process name). Derived from FromProcessImage so it never
    /// drifts from the AppKind mapping. Manual (unknown) resolves to null, not "Manual".</summary>
    public static string? FriendlyName(string? processImage)
        => FromProcessImage(processImage) switch
        {
            AppKind.Webex => "Webex",
            AppKind.Zoom => "Zoom",
            AppKind.Teams => "Teams",
            AppKind.Browser => "Browser",
            _ => null,
        };
```
- [ ] Re-run the same test command and see PASS.
- [ ] Commit:
```
git add src/LocalScribe.Core/Live/RemoteCapturePlanner.cs src/LocalScribe.Core/Live/AppKindResolver.cs tests/LocalScribe.Core.Tests/RemoteCapturePlannerTests.cs tests/LocalScribe.Core.Tests/AppKindResolverTests.cs
git commit -m "$(cat <<'EOF'
feat(core): known-targets table + AppKindResolver.FriendlyName + public IsFullMix

Single-sources the console Remote-target picker's friendly fallbacks and
FullMix annotation for Capture Scope Control (design 2026-07-12 section 1).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
EOF
)"
```

---

## Task 2: Capture-provider overload for an explicit remote target (Core)

**Files:**
- Modify `src\LocalScribe.Core\Live\ICaptureSourceProvider.cs` (add overload to the interface, ~11).
- Modify `src\LocalScribe.Core\Live\WasapiCaptureSourceProvider.cs` (add the real overload after `CreateRemote`, ~47-55).
- Modify `tests\LocalScribe.Core.Tests\LiveTestDoubles.cs` (`FakeProvider`: add `ActiveSessions` + `CreateRemote(clock, setting)` overload).
- Test file `tests\LocalScribe.Core.Tests\RemoteCapturePlannerTests.cs` is NOT the home; add a focused fake-provider test to a new `tests\LocalScribe.Core.Tests\ExplicitRemoteTargetProviderTests.cs`.

**Interfaces:**
- Produces: `ICaptureSourceProvider.CreateRemote(IClock clock, RemoteSetting explicitSetting) : (ICaptureSource Source, RemoteSnapshot Snapshot)`.
- Produces (test seam): `FakeProvider.ActiveSessions : List<AudioSessionInfo>`; `FakeProvider.CreateRemote(IClock, RemoteSetting)` resolves the snapshot through `RemoteCapturePlanner.Plan(ActiveSessions, setting)` and honors `ThrowOnNextRemoteCreate`/increments `RemoteCreates`.
- Consumes: `RemoteCapturePlanner.Plan` (Task 1 unchanged), `RemoteSnapshot`, `RemoteSetting`.

Steps:
- [ ] Create the failing test `tests\LocalScribe.Core.Tests\ExplicitRemoteTargetProviderTests.cs`:
```csharp
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>Design 2026-07-12 section "Architecture 2": the live swap must build a source for a
/// SPECIFIC requested target, not whatever ambient settings resolve to. FakeProvider's explicit
/// overload resolves the snapshot through the real planner so controller/marker tests are honest.</summary>
public sealed class ExplicitRemoteTargetProviderTests
{
    [Fact]
    public void Explicit_per_app_resolves_that_app_through_the_planner()
    {
        var p = new FakeProvider
        { ActiveSessions = { } };
        p.ActiveSessions.Add(new AudioSessionInfo(5151, "Zoom"));
        var (src, snap) = p.CreateRemote(new FakeClock(),
            new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" });
        Assert.NotNull(src);
        Assert.Equal(RemoteMode.PerProcess, snap.Mode);
        Assert.Equal("Zoom", snap.App);
        Assert.False(snap.FellBackToSystemMix);
        Assert.Equal(1, p.RemoteCreates);
    }

    [Fact]
    public void Explicit_system_mix_resolves_system_mix_not_a_fallback()
    {
        var p = new FakeProvider();
        var (_, snap) = p.CreateRemote(new FakeClock(), new RemoteSetting { Mode = RemoteMode.SystemMix });
        Assert.Equal(RemoteMode.SystemMix, snap.Mode);
        Assert.False(snap.FellBackToSystemMix);
    }

    [Fact]
    public void Explicit_overload_can_be_forced_to_throw()
    {
        var p = new FakeProvider { ThrowOnNextRemoteCreate = true };
        Assert.Throws<System.InvalidOperationException>(
            () => p.CreateRemote(new FakeClock(), new RemoteSetting { Mode = RemoteMode.SystemMix }));
    }
}
```
- [ ] Run it and see it FAIL (no `CreateRemote(IClock, RemoteSetting)` overload; `ActiveSessions` missing):
```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~ExplicitRemoteTargetProviderTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
Expected: compile error — overload / `ActiveSessions` not found.
- [ ] Add the overload to the interface `ICaptureSourceProvider.cs` (after line 11, `CreateRemote(IClock clock)`):
```csharp
    /// <summary>Explicit-target variant (design 2026-07-12 section "Architecture 2"): builds a
    /// source for the REQUESTED remote target rather than whatever the ambient settings resolve to.
    /// Used by SessionController.SetRemoteCaptureAsync for the mid-recording hot-swap.</summary>
    (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock, RemoteSetting explicitSetting);
```
- [ ] Implement the real overload in `WasapiCaptureSourceProvider.cs`. Insert after the existing `CreateRemote(IClock clock)` method (after line 55, inside the class):
```csharp
    public (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock, RemoteSetting explicitSetting)
    {
        var plan = RemoteCapturePlanner.Plan(_scanner.Scan(), explicitSetting);
        ICaptureSource source = plan.Mode == RemoteMode.PerProcess
            ? new ProcessLoopbackCapture(plan.Pid!.Value, clock)
            : ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock);
        return (source, new RemoteSnapshot
        { Mode = plan.Mode, App = plan.App, FellBackToSystemMix = plan.FellBackToSystemMix });
    }
```
- [ ] Implement the fake overload in `LiveTestDoubles.cs`. In `FakeProvider`, add the field near the other public fields (after `MicSnapshot`, ~136):
```csharp
    // Explicit-target overload (design 2026-07-12): resolves the honest RemoteSnapshot through the
    // real planner over this active-session list, so SetRemoteCaptureAsync marker tests are truthful.
    public List<AudioSessionInfo> ActiveSessions = new()
    { new AudioSessionInfo(4242, "CiscoCollabHost"), new AudioSessionInfo(5151, "Zoom") };
```
Then add the method right after the existing `CreateRemote(IClock clock)` (after line 162):
```csharp
    public (ICaptureSource, RemoteSnapshot) CreateRemote(IClock clock, RemoteSetting setting)
    { RemoteCreates++;
      if (ThrowOnNextRemoteCreate)
      { ThrowOnNextRemoteCreate = false; throw new InvalidOperationException("remote capture unavailable"); }
      var plan = RemoteCapturePlanner.Plan(ActiveSessions, setting);
      LastRemote = new DisposalTrackingSource(new FakeCaptureSource(SourceKind.Remote, RemoteFrames()));
      return (LastRemote, new RemoteSnapshot
      { Mode = plan.Mode, App = plan.App, FellBackToSystemMix = plan.FellBackToSystemMix }); }
```
- [ ] Re-run the test command and see PASS.
- [ ] Commit:
```
git add src/LocalScribe.Core/Live/ICaptureSourceProvider.cs src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs tests/LocalScribe.Core.Tests/LiveTestDoubles.cs tests/LocalScribe.Core.Tests/ExplicitRemoteTargetProviderTests.cs
git commit -m "$(cat <<'EOF'
feat(core): CreateRemote(clock, explicitSetting) provider overload

Builds a capture source for a specific requested remote target, for the
mid-recording hot-swap (design 2026-07-12 architecture 2).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
EOF
)"
```

---

## Task 3: `SessionController.SetRemoteCaptureAsync` + live-switch markers (Core)

**Files:**
- Modify `src\LocalScribe.Core\Model\Markers.cs` (add two constants after line 21).
- Modify `src\LocalScribe.Core\Live\SessionController.cs` (add `CurrentRemoteTarget` to `Session` ~104-138; seed it in `StartAsync` ~515-525; keep it fresh in `ResumeAsync` ~697; add `SetRemoteCaptureAsync` + `WriteRemoteChangeMarker` after `SetLocalMuteAsync`, ~791).
- Create `tests\LocalScribe.Core.Tests\SessionControllerRemoteSwapTests.cs`.

**Interfaces:**
- Produces: `Markers.RemoteCaptureChangedSystemMix`, `Markers.RemoteCaptureChangedPerApp`; `SessionController.SetRemoteCaptureAsync(RemoteSetting target, CancellationToken ct) : Task`.
- Consumes: `ICaptureSourceProvider.CreateRemote(clock, setting)` (Task 2); `LiveSourcePipeline.StopLegAndFlushAsync`/`StartLeg`; `SilentLegMonitor.Reset`; `Markers.DegradedSystemAudioLoopback` (existing, reused for the fallback).

Steps:
- [ ] Add the two marker constants to `Markers.cs` (after line 21, `MicDeviceUnmuted`):
```csharp
    // Capture Scope Control (design 2026-07-12 section 3). "by user" marks these as DELIBERATE
    // live switches (parallel to PausedByUser / LocalMuted), distinguishing them from the
    // involuntary DegradedSystemAudioLoopback that the per-app->system-mix fallback reuses.
    public const string RemoteCaptureChangedSystemMix = "remote capture changed to full system mix by user (all machine audio)";
    public const string RemoteCaptureChangedPerApp    = "remote capture changed to per-app by user: {0}";
```
- [ ] Create the failing test file `tests\LocalScribe.Core.Tests\SessionControllerRemoteSwapTests.cs`:
```csharp
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
}
```
- [ ] Run and see FAIL (no `SetRemoteCaptureAsync`):
```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionControllerRemoteSwapTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
Expected: compile error — `SetRemoteCaptureAsync` not found.
- [ ] Add `CurrentRemoteTarget` to the `Session` class. In `SessionController.cs`, after the `RemoteDegraded;` field (line 123) add:
```csharp
        // Capture Scope Control (design 2026-07-12 section 2 point 6): the session's CURRENT
        // remote target, seeded at Start from the composed settings and updated by every live
        // SetRemoteCaptureAsync / Resume rebuild. RemoteSetting is a value record, so the
        // idempotency guard is a plain record-equality check. Mutated only under _gate.
        public required RemoteSetting CurrentRemoteTarget;
```
- [ ] Seed it in `StartAsync`'s `Session` initializer. In the `_session = new Session { ... }` block (line 515-525), add after `RemoteDegraded = remoteSnap.FellBackToSystemMix,`:
```csharp
                    CurrentRemoteTarget = settings.Remote,
```
- [ ] Keep it fresh across Resume. In `ResumeAsync`, after `s.Remote.StartLeg(remoteSource!, ...)` (line 697) add:
```csharp
            s.CurrentRemoteTarget = _settingsProvider().Remote;   // a paused override change is adopted here
```
- [ ] Implement `SetRemoteCaptureAsync` + `WriteRemoteChangeMarker`. Insert immediately after `SetLocalMuteAsync` closes (after line 791, before `SettleLegAsync`):
```csharp
    /// <summary>Mid-recording remote-target hot-swap (design 2026-07-12 section 2). Build-before-
    /// commit like ResumeAsync/unmute: a WASAPI activation throw from the explicit CreateRemote
    /// aborts with the OLD leg untouched and NO marker (the caller reverts the picker). On success
    /// the remote leg is VAD-flushed then restarted on the SAME pipeline (retained FLAC + transcript
    /// stay continuous; AlignedAudioWriter silence-pads the sub-second gap), the remote silent
    /// monitor is reset, and a marker is emitted per the ACTUALLY-RESOLVED plan. Requires Recording;
    /// idempotent when the requested target already matches the running leg. A Paused session takes
    /// no leg action here - the caller updates the override only, and Resume adopts it.</summary>
    public async Task SetRemoteCaptureAsync(RemoteSetting target, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State != SessionState.Recording || _session is null)
            {
                Notice?.Invoke("Not recording - cannot change the remote capture target.");
                return;
            }
            var s = _session;
            if (target == s.CurrentRemoteTarget) return;   // idempotent: value-equal request, nothing built

            // Build first (fallible): a COMException from ProcessLoopbackCapture.Start ->
            // ActivateAudioInterfaceAsync propagates here, leaving the running leg untouched and
            // writing no marker (same fail-safe as ResumeAsync / unmute).
            var (newSource, snap) = _captureProvider.CreateRemote(s.Clock, target);

            // Commit: flush the old leg (trailing words kept), start the new one on the same pipeline.
            await s.Remote.StopLegAndFlushAsync();
            s.Remote.StartLeg(newSource, s.CaptureCts.Token, s.FeedCts.Token);

            // Fresh leg: reseed the silent monitor (drop any stale "no speech" flag) and abandon the
            // start-only peak probe, exactly like ResumeAsync's remote half.
            bool wasFlagged;
            lock (_silentGate) { wasFlagged = s.RemoteSilentMonitor.Reset(s.Clock.ElapsedMs); }
            if (wasFlagged) SilentLegCleared?.Invoke(SourceKind.Remote);
            _remoteStartPeak = null;

            s.CurrentRemoteTarget = target;
            WriteRemoteChangeMarker(s, snap);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Emits the design-2026-07-12 section 3 marker for a live remote switch, from the
    /// actually-resolved snapshot so the record never lies. An app that FELL BACK to system mix is
    /// an involuntary degrade - reuse the existing DegradedSystemAudioLoopback (no "by user"),
    /// marked once per degradation like ResumeAsync. Explicit system mix is a deliberate scope
    /// change; a clean per-app capture is a marked recovery. Both clear RemoteDegraded so a later
    /// involuntary fallback can mark again.</summary>
    private void WriteRemoteChangeMarker(Session s, RemoteSnapshot snap)
    {
        if (snap.FellBackToSystemMix)
        {
            if (!s.RemoteDegraded)
            {
                s.RemoteDegraded = true;
                s.Outbox.Writer.TryWrite(new MarkerAt(Markers.DegradedSystemAudioLoopback, s.Clock.ElapsedMs));
                Notice?.Invoke("Per-process capture unavailable - recording full system audio for the remote stream (possible bleed; use headphones).");
            }
            return;
        }
        if (snap.Mode == RemoteMode.SystemMix)
        {
            s.RemoteDegraded = false;
            s.Outbox.Writer.TryWrite(new MarkerAt(Markers.RemoteCaptureChangedSystemMix, s.Clock.ElapsedMs));
            return;
        }
        s.RemoteDegraded = false;
        s.Outbox.Writer.TryWrite(new MarkerAt(
            string.Format(Markers.RemoteCaptureChangedPerApp, snap.App), s.Clock.ElapsedMs));
    }
```
- [ ] Re-run the test command and see PASS (all 6).
- [ ] Commit:
```
git add src/LocalScribe.Core/Model/Markers.cs src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerRemoteSwapTests.cs
git commit -m "$(cat <<'EOF'
feat(core): SessionController.SetRemoteCaptureAsync live remote hot-swap

Build-before-commit remote-leg swap with a marker per resolved plan
(system-mix / per-app / reused degrade). Design 2026-07-12 sections 2-3.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
EOF
)"
```

---

## Task 4: Generalize `RemoteAppOverride` -> `RemoteTargetOverride` (App)

Keeps the console's free-text selector working over the widened override so the build stays green; the picker replaces the free-text box in Task 6.

**Files:**
- Rename/rewrite `src\LocalScribe.App\Services\RemoteAppOverride.cs` -> `src\LocalScribe.App\Services\RemoteTargetOverride.cs`.
- Modify `src\LocalScribe.App\CompositionRoot.cs` (record field type ~25; construction ~55; return ~101-103).
- Modify `src\LocalScribe.App\ViewModels\RecordingConsoleViewModel.cs` (field type ~29; the three `_remoteOverride.App` write sites at ~120, ~221, ~232; `RemoteSummary` reads `.Apply` unchanged).
- Rename/rewrite `tests\LocalScribe.App.Tests\RemoteAppOverrideTests.cs` -> `tests\LocalScribe.App.Tests\RemoteTargetOverrideTests.cs`.
- Modify `tests\LocalScribe.App.Tests\RecordingConsoleViewModelTests.cs` and `tests\LocalScribe.App.Tests\RecordingConsoleAppSelectorTests.cs` (ctor arg type `RemoteAppOverride` -> `RemoteTargetOverride`; assertions `over.App` -> `over.Override?.App`).

**Interfaces:**
- Produces: `RemoteTargetOverride` with `RemoteSetting? Override { get; set; }` and `Settings Apply(Settings s)` (replaces the whole `Remote` when set).
- Consumes: `RemoteSetting`, `RemoteMode` (existing).

Steps:
- [ ] `git mv src/LocalScribe.App/Services/RemoteAppOverride.cs src/LocalScribe.App/Services/RemoteTargetOverride.cs` then replace its contents entirely:
```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.App.Services;

/// <summary>The Record console's per-session Remote-target override (design 2026-07-12), the exact
/// twin of MicOverride (which holds a full MicSetting). Widened from the old app-string-only
/// RemoteAppOverride to carry a full RemoteSetting (Mode + App), so Auto / a specific app / system
/// mix are all expressible for THIS recording only. Seeds from Settings.Remote when the console
/// opens, is set as the user picks (and by a live switch), clears on Idle, and NEVER writes back to
/// settings.json - Settings stays the single persistent source of truth. CompositionRoot composes
/// Apply over the one live Func&lt;Settings&gt; that SessionController and WasapiCaptureSourceProvider
/// resolve at Start/Resume, so the override reaches capture planning with zero Core changes. WPF-free;
/// written from the UI thread (picker) and read at capture-plan time, hence the volatile field.</summary>
public sealed class RemoteTargetOverride
{
    private volatile RemoteSetting? _override;

    /// <summary>The session's chosen remote target, or null to let the persistent Settings.Remote
    /// stand. Written from the UI thread; read at capture-plan time (Start/Resume) - mirrors
    /// MicOverride's cross-thread pattern.</summary>
    public RemoteSetting? Override { get => _override; set => _override = value; }

    /// <summary>Returns settings with Remote replaced by the override when set, otherwise the input
    /// unchanged. Pure with respect to the input (records are immutable).</summary>
    public Settings Apply(Settings s) => _override is { } r ? s with { Remote = r } : s;
}
```
- [ ] Rewrite the override unit test. `git mv tests/LocalScribe.App.Tests/RemoteAppOverrideTests.cs tests/LocalScribe.App.Tests/RemoteTargetOverrideTests.cs` then replace its contents:
```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Design 2026-07-12 "Architecture 1": the per-session Remote-target override composes over
/// the ONE live Func&lt;Settings&gt; CompositionRoot hands to SessionController and the capture
/// provider. Apply replaces the whole Remote when set (Auto / app / system mix), is identity when
/// unset, and NEVER writes back to the settings service.</summary>
public sealed class RemoteTargetOverrideTests
{
    [Fact]
    public void Set_override_replaces_the_whole_remote()
    {
        var settings = new Settings { Remote = new RemoteSetting { Mode = RemoteMode.Auto } };
        var box = new RemoteTargetOverride
        { Override = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost" } };

        var applied = box.Apply(settings);

        Assert.Equal(RemoteMode.PerProcess, applied.Remote.Mode);
        Assert.Equal("CiscoCollabHost", applied.Remote.App);
        Assert.NotSame(settings, applied);
        Assert.Equal(RemoteMode.Auto, settings.Remote.Mode);        // input untouched
    }

    [Fact]
    public void System_mix_override_forces_system_mix_from_any_base()
    {
        var box = new RemoteTargetOverride { Override = new RemoteSetting { Mode = RemoteMode.SystemMix } };
        var perApp = new Settings { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" } };
        Assert.Equal(RemoteMode.SystemMix, box.Apply(perApp).Remote.Mode);
    }

    [Fact]
    public void Unset_override_is_identity()
    {
        var settings = new Settings { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" } };
        var box = new RemoteTargetOverride();
        Assert.Same(settings, box.Apply(settings));
    }

    [Fact]
    public void Override_never_touches_the_settings_service()
    {
        var service = new FakeSettingsService(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.Auto } });
        var box = new RemoteTargetOverride();
        Func<Settings> current = () => box.Apply(service.Current);

        box.Override = new RemoteSetting { Mode = RemoteMode.SystemMix };
        Assert.Equal(RemoteMode.SystemMix, current().Remote.Mode);
        Assert.Equal(RemoteMode.SystemMix, current().Remote.Mode);  // a Resume leg re-resolves
        Assert.Equal(0, service.SaveCount);
        Assert.Equal(RemoteMode.Auto, service.Current.Remote.Mode);
    }
}
```
- [ ] Update `CompositionRoot.cs`: change the record field (line 25) `RemoteAppOverride RemoteOverride,` -> `RemoteTargetOverride RemoteOverride,`; change the construction (line 55) `var remoteOverride = new RemoteAppOverride();` -> `var remoteOverride = new RemoteTargetOverride();`. (The `Func<Settings> current` line 69 and the return at 101-103 already pass `remoteOverride` by name — no change.)
- [ ] Update `RecordingConsoleViewModel.cs` minimally (still free-text this task): field type (line 29) `private readonly RemoteAppOverride _remoteOverride;` -> `private readonly RemoteTargetOverride _remoteOverride;`; ctor param (line 110) `RemoteAppOverride remoteOverride,` -> `RemoteTargetOverride remoteOverride,`. Replace the three `.App` write sites with the widened form. Add a private helper (next to `Normalize`, ~128):
```csharp
    private static RemoteSetting? PerProcessOrNull(string value)
        => string.IsNullOrWhiteSpace(value) ? null
            : new RemoteSetting { Mode = RemoteMode.PerProcess, App = value.Trim() };
```
Then:
  - ctor seed (line 120-121): replace
```csharp
        _remoteOverride.App = settings.Current.Remote.Mode == RemoteMode.PerProcess
            ? Normalize(_sessionTargetApp) : null;
```
with
```csharp
        _remoteOverride.Override = settings.Current.Remote.Mode == RemoteMode.PerProcess
            ? PerProcessOrNull(_sessionTargetApp) : null;
```
  - `OnSessionTargetAppChanged` (line 221): replace `_remoteOverride.App = Normalize(value);` with `_remoteOverride.Override = PerProcessOrNull(value);`.
  - `OnSessionChanged` Idle-reseed sets `SessionTargetApp` (line 232) which re-triggers `OnSessionTargetAppChanged` -> the override mirrors automatically; no direct `.App` write there. No change needed beyond the two above.
- [ ] Update the two console test files' compile surface:
  - In `RecordingConsoleViewModelTests.cs`: change the tuple type in `MakeConsole`'s return signature and the local `var over = new RemoteAppOverride();` -> `new RemoteTargetOverride();`, and the return-tuple element type `RemoteAppOverride Override` -> `RemoteTargetOverride Override`. Change every assertion `over.App` to `over.Override?.App` and `Assert.Null(over.App)` to `Assert.Null(over.Override)`. Concretely, the affected tests: `Seeds_selector_and_override_from_settings_at_construction`, `Auto_base_does_not_seed_the_override_until_the_user_picks`, `PerProcess_base_still_seeds_the_override`, `Selector_edit_mirrors_into_the_override_trimmed`, `Session_stop_reseeds_the_selector_to_the_saved_default`, `Settings_change_reseeds_an_untouched_selector`, `Settings_change_keeps_a_user_diverged_selector`, `Dispose_unsubscribes_settings_and_session`, `Switching_to_system_mix_clears_a_diverged_app_override`. Replacement mapping: `Assert.Equal("Webex", over.App)` -> `Assert.Equal("Webex", over.Override?.App)`; `Assert.Null(over.App)` / `Assert.Null(emptyOver.App)` -> `Assert.Null(over.Override)` / `Assert.Null(emptyOver.Override)`; `Assert.Equal("Zoom", over.App)` -> `Assert.Equal("Zoom", over.Override?.App)`.
  - In `RecordingConsoleAppSelectorTests.cs`: change `var over = new RemoteAppOverride();` -> `new RemoteTargetOverride();`.
- [ ] Run the App tests for the changed classes and see PASS:
```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~RemoteTargetOverrideTests|FullyQualifiedName~RecordingConsoleViewModelTests|FullyQualifiedName~RecordingConsoleAppSelectorTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
- [ ] Commit:
```
git add src/LocalScribe.App/Services/RemoteTargetOverride.cs src/LocalScribe.App/CompositionRoot.cs src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs tests/LocalScribe.App.Tests/RemoteTargetOverrideTests.cs tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs tests/LocalScribe.App.Tests/RecordingConsoleAppSelectorTests.cs
git commit -m "$(cat <<'EOF'
refactor(app): generalize RemoteAppOverride -> RemoteTargetOverride (full RemoteSetting)

Widens the per-session override to carry Mode+App so Auto / app / system mix
are all expressible; console/tests keep the free-text box for now. Design 2026-07-12.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
EOF
)"
```

---

## Task 5: `SessionViewModel.SwitchRemoteTargetAsync` (App)

**Files:**
- Modify `src\LocalScribe.App\ViewModels\SessionViewModel.cs` (add the method after `ToggleMuteAsync`, ~274).
- Modify `tests\LocalScribe.App.Tests\SessionViewModelTests.cs` (add tests).

**Interfaces:**
- Produces: `SessionViewModel.SwitchRemoteTargetAsync(RemoteSetting target) : Task<bool>` — true on success, false when the controller's build-before-commit threw (surfaces the message via `LastNotice`/`NoticeRaised`). Consumed by Task 6's `ChangeRemoteTargetCommand`.
- Consumes: `SessionController.SetRemoteCaptureAsync` (Task 3).

Steps:
- [ ] Add the failing tests to `SessionViewModelTests.cs` (append inside the class):
```csharp
    [Fact]
    public async Task SwitchRemoteTargetAsync_hot_swaps_and_returns_true()
    {
        var (vm, controller) = MakeVm();
        await vm.StartCommand.ExecuteAsync(null);
        bool ok = await vm.SwitchRemoteTargetAsync(
            new LocalScribe.Core.Model.RemoteSetting { Mode = LocalScribe.Core.Model.RemoteMode.SystemMix });
        Assert.True(ok);
        Assert.Equal(SessionState.Recording, vm.State);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task SwitchRemoteTargetAsync_returns_false_and_notices_on_build_failure()
    {
        var (controller, provider, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        await vm.StartCommand.ExecuteAsync(null);

        provider.ThrowOnNextRemoteCreate = true;
        bool ok = await vm.SwitchRemoteTargetAsync(
            new LocalScribe.Core.Model.RemoteSetting { Mode = LocalScribe.Core.Model.RemoteMode.SystemMix });

        Assert.False(ok);
        Assert.Equal(SessionState.Recording, vm.State);   // old leg untouched
        Assert.NotNull(vm.LastNotice);
        await vm.StopCommand.ExecuteAsync(null);
    }
```
(Note: `LiveTestDoubles` and `FakeProvider` are internal to `LocalScribe.Core.Tests`; `SessionViewModelTests` already references them via the existing `LiveTestDoubles.MakeController` calls, so the `provider` handle is available.)
- [ ] Run and see FAIL (no `SwitchRemoteTargetAsync`):
```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SessionViewModelTests.SwitchRemoteTargetAsync" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
Expected: compile error — `SwitchRemoteTargetAsync` not found.
- [ ] Implement it in `SessionViewModel.cs` (after `ToggleMuteAsync`, line 274):
```csharp
    /// <summary>Capture Scope Control (design 2026-07-12): mid-recording remote-target hot-swap.
    /// Mirrors ToggleMuteAsync's off-UI-thread controller call. Returns true on success; on the
    /// controller's build-before-commit throw (WASAPI activation failed) it swallows the exception,
    /// surfaces the message as a Notice, and returns false so the console reverts the picker.</summary>
    public async Task<bool> SwitchRemoteTargetAsync(RemoteSetting target)
    {
        try
        {
            await Task.Run(() => _controller.SetRemoteCaptureAsync(target, CancellationToken.None));
            return true;
        }
        catch (Exception ex)
        {
            _dispatch(() => { LastNotice = ex.Message; NoticeRaised?.Invoke(ex.Message); });
            return false;
        }
    }
```
- [ ] Re-run and see PASS.
- [ ] Commit:
```
git add src/LocalScribe.App/ViewModels/SessionViewModel.cs tests/LocalScribe.App.Tests/SessionViewModelTests.cs
git commit -m "$(cat <<'EOF'
feat(app): SessionViewModel.SwitchRemoteTargetAsync (returns false on build failure)

Off-UI-thread wrapper over SetRemoteCaptureAsync for the console's live picker.
Design 2026-07-12 UI wiring.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
EOF
)"
```

---

## Task 6: Replace the free-text app box with the Remote-target picker (App VM + wiring)

**Files:**
- Modify `src\LocalScribe.App\ViewModels\RecordingConsoleViewModel.cs` (remove `SessionTargetApp`/`ShowAppSelector`/`AppSelectorLabel`/`AppSelectorPlaceholder`/`AppSuggestions`/`OnSessionTargetAppChanged`; add `RemoteTargetOption`, `RemoteTargetOptions`, `SelectedRemoteTarget`, `RefreshRemoteTargetsAsync`, `ChangeRemoteTargetCommand`, `IAudioSessionScanner` + `Func<bool>` ctor params; rewrite `OnSettingsChanged`/`OnSessionChanged` reseed).
- Modify `src\LocalScribe.Core\Live\RemoteCapturePlanner.cs` (KEEP `SuggestedPerProcessApps` — still consumed by the Settings page; only the console's `AppSuggestions` is removed).
- Modify `src\LocalScribe.App\CompositionRoot.cs` (hoist the scanner into a shared var; add `IAudioSessionScanner Scanner` to `AppComposition`).
- Modify `src\LocalScribe.App\App.xaml.cs` (pass `comp.Scanner` + a system-mix confirm `MessageBox` lambda to the console ctor, ~92-94).
- Rewrite `tests\LocalScribe.App.Tests\RecordingConsoleViewModelTests.cs` (picker model) and `tests\LocalScribe.App.Tests\RecordingConsoleAppSelectorTests.cs` (friendly-label tests).

**Interfaces:**
- Produces: `RemoteTargetOption(string Label, RemoteSetting Setting, bool IsSystemMix)`; `RecordingConsoleViewModel.RemoteTargetOptions : ObservableCollection<RemoteTargetOption>`; `.SelectedRemoteTarget : RemoteTargetOption`; `.RefreshRemoteTargetsAsync() : Task`; `.ChangeRemoteTargetCommand : IAsyncRelayCommand<RemoteTargetOption>`. Consumed by Task 7's XAML/code-behind.
- Consumes: `RemoteCapturePlanner.KnownTargets`/`IsFullMix`, `AppKindResolver.FriendlyName` (Task 1); `RemoteTargetOverride` (Task 4); `SessionViewModel.SwitchRemoteTargetAsync` (Task 5); `IAudioSessionScanner.Scan` (existing).

Steps:
- [ ] Rewrite `RecordingConsoleViewModelTests.cs` for the picker (this is the failing-test step). Replace the whole file's `MakeConsole` + the remote-target-related tests; KEEP the matter-picker and mic tests unchanged except for the `MakeConsole` ctor call. New `MakeConsole` + a fake scanner + the picker tests:
```csharp
    private sealed class FakeScanner : IAudioSessionScanner
    {
        public List<AudioSessionInfo> Active = new();
        public IReadOnlyList<AudioSessionInfo> Scan() => Active;
    }

    private readonly FakeScanner _scanner = new();

    private (RecordingConsoleViewModel Console, FakeSettingsService Settings,
        SessionViewModel Session, RemoteTargetOverride Override, MaintenanceService Maintenance,
        MatterSelectionOverride MatterSelection, MicOverride Mic) MakeConsole(
            Settings? initial = null, Func<bool>? confirmSystemMix = null)
    {
        var settings = new FakeSettingsService(initial ?? PerProcess("Webex"));
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, settings.Current, dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var over = new RemoteTargetOverride();
        var maintenance = new MaintenanceService(new StoragePaths(_root), settings,
            new FakeRecycleBin(), TimeProvider.System);
        var matterSelection = new MatterSelectionOverride();
        var micOverride = new MicOverride();
        var console = new RecordingConsoleViewModel(settings, session, over, maintenance,
            matterSelection, _devices, micOverride, _scanner, confirmSystemMix ?? (() => true),
            dispatch: a => a());
        return (console, settings, session, over, maintenance, matterSelection, micOverride);
    }
```
Then replace the remote-target tests (the ones that referenced `SessionTargetApp`/`ShowAppSelector`) with:
```csharp
    [Fact]
    public void Base_options_always_include_auto_fallbacks_and_system_mix()
    {
        var (console, _, _, _, _, _, _) = MakeConsole(Auto(null));
        Assert.Contains(console.RemoteTargetOptions, o => o.Setting.Mode == RemoteMode.Auto);
        Assert.Contains(console.RemoteTargetOptions, o => o.Label == "Webex" && o.Setting.App == "CiscoCollabHost");
        Assert.Contains(console.RemoteTargetOptions, o => o.Label == "Zoom" && o.Setting.App == "Zoom");
        Assert.Contains(console.RemoteTargetOptions, o => o.IsSystemMix);
    }

    [Fact]
    public void Seeds_selection_and_override_from_settings()
    {
        var (auto, _, _, autoOver, _, _, _) = MakeConsole(Auto(null));
        Assert.Equal(RemoteMode.Auto, auto.SelectedRemoteTarget.Setting.Mode);
        Assert.Null(autoOver.Override);                                  // untouched Auto -> follows settings

        var (per, _, _, perOver, _, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.Equal("Webex", per.SelectedRemoteTarget.Setting.App);
        Assert.Equal("Webex", perOver.Override?.App);
    }

    [Fact]
    public void Picking_an_option_mirrors_into_the_override()
    {
        var (console, settings, _, over, _, _, _) = MakeConsole(Auto(null));
        var zoom = console.RemoteTargetOptions.First(o => o.Setting.App == "Zoom");
        console.SelectedRemoteTarget = zoom;
        Assert.Equal(RemoteMode.PerProcess, over.Override?.Mode);
        Assert.Equal("Zoom", over.Override?.App);
        Assert.Equal(0, settings.SaveCount);
        Assert.Contains("per-app (Zoom)", console.RemoteSummary);
    }

    [Fact]
    public async Task Refresh_builds_friendly_labels_dedups_by_image_and_annotates_fullmix()
    {
        var (console, _, _, _, _, _, _) = MakeConsole(Auto(null));
        _scanner.Active.Add(new AudioSessionInfo(1, "CiscoCollabHost"));  // live Webex
        _scanner.Active.Add(new AudioSessionInfo(2, "chrome"));           // FullMix
        await console.RefreshRemoteTargetsAsync();

        Assert.Contains(console.RemoteTargetOptions, o => o.Label == "CiscoCollabHost - Webex");
        Assert.Contains(console.RemoteTargetOptions, o => o.Label == "chrome (captured as system mix)");
        // Webex fallback (image CiscoCollabHost) is deduped away by the live CiscoCollabHost entry.
        Assert.DoesNotContain(console.RemoteTargetOptions,
            o => o.Label == "Webex" && o.Setting.App == "CiscoCollabHost");
        Assert.Contains(console.RemoteTargetOptions, o => o.Label == "Zoom");   // Zoom fallback still pinned
    }

    [Fact]
    public async Task Live_pick_of_an_app_hot_swaps_and_updates_the_override()
    {
        var (console, _, session, over, _, _, _) = MakeConsole(Auto(null));
        await session.StartCommand.ExecuteAsync(null);
        var zoom = console.RemoteTargetOptions.First(o => o.Setting.App == "Zoom");
        await console.ChangeRemoteTargetCommand.ExecuteAsync(zoom);
        Assert.Equal("Zoom", over.Override?.App);
        Assert.Equal("Zoom", console.SelectedRemoteTarget.Setting.App);
        await session.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Live_switch_to_system_mix_is_gated_by_confirm()
    {
        var (console, _, session, over, _, _, _) = MakeConsole(Auto(null), confirmSystemMix: () => false);
        await session.StartCommand.ExecuteAsync(null);
        var before = console.SelectedRemoteTarget;
        var mix = console.RemoteTargetOptions.First(o => o.IsSystemMix);
        await console.ChangeRemoteTargetCommand.ExecuteAsync(mix);
        Assert.Equal(before, console.SelectedRemoteTarget);   // declined -> selection unchanged
        Assert.NotEqual(RemoteMode.SystemMix, over.Override?.Mode ?? RemoteMode.Auto);
        await session.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Live_app_switch_commits_the_selected_target()
    {
        // Happy path only: a live app pick reaches the controller and commits. The build-FAILURE
        // revert path is NOT asserted here (FakeProvider's throw seam is not surfaced through
        // MakeConsole); it is covered deterministically in Task 5's provider-visible controller test.
        var (console, _, session, _, _, _, _) = MakeConsole(Auto(null));
        await session.StartCommand.ExecuteAsync(null);
        await console.ChangeRemoteTargetCommand.ExecuteAsync(console.RemoteTargetOptions.First(o => o.Setting.App == "Zoom"));
        Assert.Equal("Zoom", console.SelectedRemoteTarget.Setting.App);   // normal pick commits
        await session.StopCommand.ExecuteAsync(null);
    }
```
(The build-failure revert path is covered deterministically by `SessionViewModelTests.SwitchRemoteTargetAsync_returns_false...` in Task 5; the console test above only asserts the happy commit, since `FakeProvider` is not surfaced through `MakeConsole`.)
- [ ] Rewrite `RecordingConsoleAppSelectorTests.cs` to test friendly labels instead of the removed label/placeholder props. Replace the two `[Fact]`s with:
```csharp
    [Fact]
    public async Task Live_item_shows_process_name_with_friendly_suffix()
    {
        var vm = MakeConsole(new Settings { Remote = new RemoteSetting { Mode = RemoteMode.Auto } });
        _devicesUnused();
        _scanner.Active.Add(new AudioSessionInfo(9, "CiscoCollabHost"));
        await vm.RefreshRemoteTargetsAsync();
        Assert.Contains(vm.RemoteTargetOptions, o => o.Label == "CiscoCollabHost - Webex");
    }

    [Fact]
    public void Unknown_live_process_shows_the_bare_name()
    {
        var vm = MakeConsole(new Settings { Remote = new RemoteSetting { Mode = RemoteMode.Auto } });
        _scanner.Active.Add(new AudioSessionInfo(9, "Spotify"));
        // no friendly suffix, no fullmix annotation
        vm.RefreshRemoteTargetsAsync().GetAwaiter().GetResult();
        Assert.Contains(vm.RemoteTargetOptions, o => o.Label == "Spotify");
    }
```
Update `RecordingConsoleAppSelectorTests.MakeConsole` to add the scanner + confirm args and a `FakeScanner _scanner` field (same shape as above), and drop the now-invalid `AppSelectorLabel`/`AppSelectorPlaceholder`/`ShowAppSelector` assertions. (Remove `_devicesUnused()`/placeholder helpers — shown here only to signal the fields; implement `MakeConsole` to construct with `_scanner` and `confirmSystemMix: () => true`.)
- [ ] Run and see FAIL (compile: new members/ctor params missing):
```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~RecordingConsoleViewModelTests|FullyQualifiedName~RecordingConsoleAppSelectorTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
- [ ] Rewrite `RecordingConsoleViewModel.cs`. Add the option record above the class (after `MatterPickRow`, line 14):
```csharp
/// <summary>One entry in the console's Remote-target picker (design 2026-07-12 section 1): a
/// display label plus the RemoteSetting it applies. IsSystemMix drives the live confirm gate.
/// Value-record equality lets it back a ComboBox SelectedItem and the dedup set.</summary>
public sealed record RemoteTargetOption(string Label, RemoteSetting Setting, bool IsSystemMix);
```
Replace the removed members. Delete lines 45-57 (the `_sessionTargetApp`, `ShowAppSelector`, `AppSuggestions`, `AppSelectorLabel`, `AppSelectorPlaceholder`) and line 219-223 (`OnSessionTargetAppChanged`). Add these members (near the mic members, after `SelectedMic`, ~97):
```csharp
    private readonly IAudioSessionScanner _scanner;
    private readonly Func<bool> _confirmSystemMix;
    private RemoteTargetOption _selectedRemoteTarget = null!;

    /// <summary>The Remote-target picker's items: Auto, live apps (friendly-labelled, FullMix
    /// annotated), the always-present Webex/Zoom fallbacks, and System mix. Rebuilt on refresh.</summary>
    public ObservableCollection<RemoteTargetOption> RemoteTargetOptions { get; } = new();

    /// <summary>The chosen Remote target for THIS session. The setter mirrors into
    /// RemoteTargetOverride (never settings.json) and refreshes RemoteSummary. Used by both the idle
    /// and live pickers; the live hot-swap + confirm gate live in ChangeRemoteTargetCommand.</summary>
    public RemoteTargetOption SelectedRemoteTarget
    {
        get => _selectedRemoteTarget;
        set
        {
            if (value is null || value == _selectedRemoteTarget) return;
            _selectedRemoteTarget = value;
            _remoteOverride.Override = value.Setting;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RemoteSummary));
        }
    }

    public IAsyncRelayCommand<RemoteTargetOption> ChangeRemoteTargetCommand { get; }
```
Rewrite the ctor (lines 109-126). Change the signature to add `IAudioSessionScanner scanner, Func<bool> confirmSystemMix` before `dispatch`, and replace the old free-text seeding with option-building + selection seeding:
```csharp
    public RecordingConsoleViewModel(ISettingsService settings, SessionViewModel session,
        RemoteTargetOverride remoteOverride, MaintenanceService maintenance,
        MatterSelectionOverride matterSelection, ICaptureDeviceEnumerator deviceEnumerator,
        MicOverride micOverride, IAudioSessionScanner scanner, Func<bool> confirmSystemMix,
        Action<Action> dispatch)
    {
        (_settings, Session, _remoteOverride, _maintenance, _matterSelection, _dispatch)
            = (settings, session, remoteOverride, maintenance, matterSelection, dispatch);
        _deviceEnumerator = deviceEnumerator;
        _micOverride = micOverride;
        _scanner = scanner;
        _confirmSystemMix = confirmSystemMix;
        RebuildRemoteTargetOptions(Array.Empty<AudioSessionInfo>());     // base options (no scan yet)
        SeedSelectedFromSettings();
        MicChoices = BuildMicChoices(out _selectedMic);
        ToggleMatterCommand = new RelayCommand<MatterPickRow>(ToggleMatter);
        ChangeRemoteTargetCommand = new AsyncRelayCommand<RemoteTargetOption>(ChangeRemoteTargetAsync);
        settings.Changed += OnSettingsChanged;
        session.PropertyChanged += OnSessionChanged;
    }
```
Add the option-building, seeding, refresh, and command logic (place after `Normalize`/`PerProcessOrNull`, and keep `RemoteSummary` as-is since it already reads `_remoteOverride.Apply(_settings.Current).Remote`):
```csharp
    /// <summary>Rebuilds RemoteTargetOptions: Auto, then live apps (deduped by image, friendly-
    /// labelled, FullMix annotated), then the known fallbacks whose image is not already live, then
    /// System mix. Preserves the current selection by value when it still exists.</summary>
    private void RebuildRemoteTargetOptions(IReadOnlyList<AudioSessionInfo> active)
    {
        var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<RemoteTargetOption>
        { new("Auto - detect the call app", new RemoteSetting { Mode = RemoteMode.Auto }, false) };

        foreach (var s in active)
        {
            if (!seenImages.Add(s.ProcessName)) continue;
            // FullMix apps (Teams/browsers) are captured as system mix regardless, so annotate the
            // bare image name and DO NOT append the friendly bucket (which for e.g. chrome is
            // "Browser") - matches design section 4 and the test's expected "chrome (captured as
            // system mix)". Non-FullMix apps get the "image - Friendly" disambiguation.
            string label = RemoteCapturePlanner.IsFullMix(s.ProcessName)
                ? $"{s.ProcessName} (captured as system mix)"
                : AppKindResolver.FriendlyName(s.ProcessName) is { } friendly
                    ? $"{s.ProcessName} - {friendly}"
                    : s.ProcessName;
            options.Add(new RemoteTargetOption(label,
                new RemoteSetting { Mode = RemoteMode.PerProcess, App = s.ProcessName }, false));
        }

        foreach (var (friendly, image) in RemoteCapturePlanner.KnownTargets)
            if (seenImages.Add(image))
                options.Add(new RemoteTargetOption(friendly,
                    new RemoteSetting { Mode = RemoteMode.PerProcess, App = image }, false));

        options.Add(new RemoteTargetOption("System mix - everything",
            new RemoteSetting { Mode = RemoteMode.SystemMix }, true));

        RemoteTargetOptions.Clear();
        foreach (var o in options) RemoteTargetOptions.Add(o);

        // Preserve the selection by value; re-point the field at the equal instance in the new list
        // so ComboBox SelectedItem stays bound. Falls back to the settings-derived option.
        if (_selectedRemoteTarget is not null)
        {
            var match = RemoteTargetOptions.FirstOrDefault(o => o.Setting == _selectedRemoteTarget.Setting);
            _selectedRemoteTarget = match ?? OptionFor(_settings.Current.Remote);
            OnPropertyChanged(nameof(SelectedRemoteTarget));
        }
    }

    /// <summary>The option matching a RemoteSetting, creating an app option if the image is not in
    /// the current list (an unknown pinned app).</summary>
    private RemoteTargetOption OptionFor(RemoteSetting r)
    {
        if (r.Mode == RemoteMode.SystemMix)
            return RemoteTargetOptions.First(o => o.IsSystemMix);
        if (r.Mode == RemoteMode.PerProcess && !string.IsNullOrEmpty(r.App))
            return RemoteTargetOptions.FirstOrDefault(o => o.Setting.Mode == RemoteMode.PerProcess
                    && string.Equals(o.Setting.App, r.App, StringComparison.OrdinalIgnoreCase))
                ?? new RemoteTargetOption(r.App!, new RemoteSetting { Mode = RemoteMode.PerProcess, App = r.App }, false);
        return RemoteTargetOptions.First(o => o.Setting.Mode == RemoteMode.Auto);
    }

    /// <summary>Seeds the selection (and, per the old semantics, the override) from Settings.Remote
    /// WITHOUT going through the public setter: an untouched Auto/SystemMix selector leaves the
    /// override null so a background settings change still flows to capture; a PerProcess base arms
    /// the override with that app (equivalent to the pre-picker seeding).</summary>
    private void SeedSelectedFromSettings()
    {
        var r = _settings.Current.Remote;
        _selectedRemoteTarget = OptionFor(r);
        _remoteOverride.Override = r.Mode == RemoteMode.PerProcess && !string.IsNullOrEmpty(r.App)
            ? new RemoteSetting { Mode = RemoteMode.PerProcess, App = r.App } : null;
        OnPropertyChanged(nameof(SelectedRemoteTarget));
        OnPropertyChanged(nameof(RemoteSummary));
    }

    /// <summary>Off-UI-thread scan (WasapiSessionScanner enumerates COM endpoints), then rebuild on
    /// the resumed context. Best-effort - a scan hiccup must never disturb the console.</summary>
    public async Task RefreshRemoteTargetsAsync()
    {
        try
        {
            var active = await Task.Run(() => _scanner.Scan());
            RebuildRemoteTargetOptions(active);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RefreshRemoteTargetsAsync failed: {ex}");
        }
    }

    /// <summary>The picker's selection handler (design 2026-07-12 section 4). Idempotent. While
    /// Recording a switch TO system mix is confirm-gated; the pick then applies the override and
    /// hot-swaps the remote leg, reverting the selection if the build fails. Idle/Paused apply the
    /// override only (Start/Resume adopt it).</summary>
    private async Task ChangeRemoteTargetAsync(RemoteTargetOption? option)
    {
        if (option is null || option == _selectedRemoteTarget) return;
        bool live = Session.State == SessionState.Recording;
        if (live && option.IsSystemMix && !_confirmSystemMix())
        {
            OnPropertyChanged(nameof(SelectedRemoteTarget));   // snap the ComboBox back to the current pick
            return;
        }
        var previous = _selectedRemoteTarget;
        SelectedRemoteTarget = option;                          // sets field + override + RemoteSummary
        if (live && !await Session.SwitchRemoteTargetAsync(option.Setting))
            SelectedRemoteTarget = previous;                    // build failed: revert
    }
```
Rewrite `OnSessionChanged` (Idle branch, lines 227-251) to reseed via `SeedSelectedFromSettings()` instead of `SessionTargetApp`:
```csharp
    private void OnSessionChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionViewModel.State)) return;
        if (Session.State == SessionState.Idle)
        {
            SeedSelectedFromSettings();      // next session reverts to the saved default
            _pickedMatterIds.Clear();
            _matterSelection.MatterIds = [];
            _micOverride.Override = null;
            _selectedMic = BuildSelectedFromSettings();
            OnPropertyChanged(nameof(SelectedMic));
            OnPropertyChanged(nameof(MicSummary));
            RebuildMatterOptions();
            _ = LoadMattersAsync();
            OnPropertyChanged(nameof(SelectedMatterSummary));
        }
    }
```
Rewrite `OnSettingsChanged` (lines 253-285) to reseed the picker when untouched (and always on SystemMix), dropping the removed property notifications:
```csharp
    private void OnSettingsChanged(Settings oldSettings, Settings newSettings)
        => _dispatch(() =>
        {
            // Reseed only an UNTOUCHED selector (still equal to the option matching the old default),
            // so a user's in-flight per-session pick is never clobbered by a background save. A switch
            // to SystemMix always reseeds: any armed app override MUST be dropped (SystemMix has no
            // per-app target), exactly as the old free-text selector forced "" on SystemMix.
            var oldOption = OptionFor(oldSettings.Remote);
            if (newSettings.Remote.Mode == RemoteMode.SystemMix || _selectedRemoteTarget == oldOption)
                SeedSelectedFromSettings();

            if (_micOverride.Override is null)
            {
                _selectedMic = BuildSelectedFromSettings();
                OnPropertyChanged(nameof(SelectedMic));
            }

            OnPropertyChanged(nameof(RemoteSummary));
            OnPropertyChanged(nameof(MicSummary));
        });
```
Add `using System.Linq;` if not already present (the file uses collection expressions; `FirstOrDefault`/`First` need Linq — confirm `using System.Linq;` at top, add if missing). Keep `PerProcessOrNull`/`Normalize` only if still referenced; if unused after removing `OnSessionTargetAppChanged`, delete them to hold the 0-warning gate.
- [ ] Do NOT remove `SuggestedPerProcessApps` from `RemoteCapturePlanner.cs`. It is still consumed by the Settings-page persistent-default picker (`SettingsPageViewModel.cs:152` -> `SettingsPage.xaml:74`, pinned by `SettingsPageViewModelTests.cs:257`), which is out of this feature's scope — removing it is an App build break + failing test. Only the console's `AppSuggestions` property was removed (step above); `KnownTargets` is the console's friendly-fallback source.
- [ ] Hoist the scanner in `CompositionRoot.cs`. Change line 74 from an inline `new WasapiSessionScanner()` to a shared local. Before the controller construction (before line 71) add:
```csharp
        var scanner = new WasapiSessionScanner();
```
and change line 74 `new WasapiCaptureSourceProvider(current, new WasapiSessionScanner(), deviceEnumerator)` to `new WasapiCaptureSourceProvider(current, scanner, deviceEnumerator)`. Add `IAudioSessionScanner Scanner` to the `AppComposition` record (after `ICaptureDeviceEnumerator DeviceEnumerator` at line 28, add `, IAudioSessionScanner Scanner`) and pass `scanner` in the `return new AppComposition(...)` at lines 101-103 (append `, scanner`).
- [ ] Wire the console ctor in `App.xaml.cs` (lines 92-94). Replace with:
```csharp
        var console = new ViewModels.RecordingConsoleViewModel(comp.Settings, session,
            comp.RemoteOverride, comp.Maintenance, comp.MatterSelection,
            comp.DeviceEnumerator, comp.MicOverride, comp.Scanner,
            confirmSystemMix: () => MessageBox.Show(
                "Capturing full system mix records ALL machine audio - other apps, notifications, " +
                "both sides through your speakers. A marker will be added to the transcript. Continue?",
                "Switch to system mix", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                == MessageBoxResult.OK,
            dispatch);
```
- [ ] Run the full App + Core changed-suite and see PASS:
```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~RecordingConsoleViewModelTests|FullyQualifiedName~RecordingConsoleAppSelectorTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~RemoteCapturePlannerTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
- [ ] Commit:
```
git add src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs src/LocalScribe.Core/Live/RemoteCapturePlanner.cs src/LocalScribe.App/CompositionRoot.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs tests/LocalScribe.App.Tests/RecordingConsoleAppSelectorTests.cs
git commit -m "$(cat <<'EOF'
feat(app): Remote-target picker replaces the free-text app box

Live-refreshing RemoteTargetOptions (friendly labels, pinned Webex/Zoom
fallbacks, FullMix annotation), a confirm-gated live switch, and the
scanner/confirm wiring. Design 2026-07-12 sections 1 & 4.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
EOF
)"
```

---

## Task 7: LiveViewWindow XAML — idle picker + live "Change target" row + 2 s poll (App, UI)

XAML/code-behind cannot be unit-tested; the deliverable is a precise manual smoke plus keeping `XamlHygieneTests` and every existing App test green.

**Files:**
- Modify `src\LocalScribe.App\LiveViewWindow.xaml` (idle app-selector block lines 36-51 -> Remote-target dropdown; recording toolbar area lines 186-225 -> add a "Change target" row).
- Modify `src\LocalScribe.App\LiveViewWindow.xaml.cs` (shared `SelectionChanged` handler; 2 s `DispatcherTimer` started on visible / stopped on hidden; refresh on dropdown-open).

**Interfaces:**
- Consumes: `RecordingConsoleViewModel.RemoteTargetOptions`, `.SelectedRemoteTarget`, `.ChangeRemoteTargetCommand`, `.RefreshRemoteTargetsAsync` (Task 6), bound through `LiveViewContext.Console`.

Steps:
- [ ] Replace the idle app-selector block in `LiveViewWindow.xaml` (lines 36-51, the app-selector `StackPanel` + the two placeholder `TextBlock`s) with a plain Remote-target dropdown, keeping the "Applies to this recording only" note:
```xml
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,0,0,4">
                    <TextBlock Text="Remote target" VerticalAlignment="Center" Margin="0,0,8,0" />
                    <ComboBox MinWidth="240"
                              ItemsSource="{Binding Console.RemoteTargetOptions}"
                              SelectedItem="{Binding Console.SelectedRemoteTarget, Mode=OneWay}"
                              DisplayMemberPath="Label"
                              SelectionChanged="RemoteTargetCombo_SelectionChanged"
                              DropDownOpened="RemoteTargetCombo_DropDownOpened" />
                </StackPanel>
                <TextBlock Text="Applies to this recording only. The saved default is unchanged (Settings &gt; Recording)."
                           Style="{StaticResource MutedText}" TextWrapping="Wrap" TextAlignment="Center"
                           HorizontalAlignment="Center" Margin="0,0,0,16" />
```
- [ ] Add the live "Change target" row in the recording DockPanel. Insert a new `DockPanel.Dock="Top"` `Grid` immediately AFTER the button `StackPanel` closes (after line 186) and BEFORE the silent-leg warning `StackPanel` (line 187 comment), mirroring the app-mute banner's 2-column layout (text `*`, control `Auto`):
```xml
                <!-- Capture Scope Control (design 2026-07-12 section 4): mid-recording remote-target
                     picker. Non-system-mix picks apply instantly; -> System mix pops the confirm
                     dialog first. 2-column Grid (text *, control Auto) so it does not clip at
                     MinWidth 420, mirroring the app-mute banner fix. -->
                <Grid DockPanel.Dock="Top" Margin="0,0,0,8">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="Remote target" VerticalAlignment="Center" />
                    <ComboBox Grid.Column="1" MinWidth="220"
                              ItemsSource="{Binding Console.RemoteTargetOptions}"
                              SelectedItem="{Binding Console.SelectedRemoteTarget, Mode=OneWay}"
                              DisplayMemberPath="Label"
                              SelectionChanged="RemoteTargetCombo_SelectionChanged"
                              DropDownOpened="RemoteTargetCombo_DropDownOpened" />
                </Grid>
```
- [ ] Add the code-behind handlers + poll timer in `LiveViewWindow.xaml.cs`. Add `using System.Windows.Threading;` and `using LocalScribe.Core.Live;` at the top if missing. Add a field near `_stickToBottom`:
```csharp
    private readonly DispatcherTimer _remoteTargetPoll = new() { Interval = TimeSpan.FromSeconds(2) };
```
In the ctor (after the existing `IsVisibleChanged` wiring, line 40) add the poll lifecycle + an immediate refresh on show, and start/stop with visibility:
```csharp
        _remoteTargetPoll.Tick += (_, _) => RefreshRemoteTargetsSafely();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) { RefreshRemoteTargetsSafely(); _remoteTargetPoll.Start(); }
            else _remoteTargetPoll.Stop();
        };
```
Add the handlers + safe wrapper (near `LoadMattersSafely`):
```csharp
    // Async-void safe wrapper: RefreshRemoteTargetsAsync is best-effort internally, but a handler
    // must never let an exception escape and crash this hide-on-close singleton.
    private async void RefreshRemoteTargetsSafely()
    {
        try { await _console.RefreshRemoteTargetsAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RefreshRemoteTargetsAsync failed: {ex}"); }
    }

    private void RemoteTargetCombo_DropDownOpened(object? sender, EventArgs e)
        => RefreshRemoteTargetsSafely();   // immediate refresh on open (design section 4)

    // Both the idle and live pickers route here. SelectedItem is OneWay, so a user pick is NOT yet
    // committed to the VM - fire the command (idle -> override only; recording -> confirm-gated
    // hot-swap). The command's idempotency guard absorbs the echo when it re-points the selection.
    private void RemoteTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var option = e.AddedItems[0] as ViewModels.RemoteTargetOption;
        if (option is not null) _console.ChangeRemoteTargetCommand.Execute(option);
    }
```
(`RemoteTargetOption` is fully-qualified as `ViewModels.RemoteTargetOption` above so it resolves without adding a `using`.)
- [ ] Build the App project to an isolated output (verifies XAML compiles + 0 warnings):
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
Expected: build succeeds, 0 warnings.
- [ ] Run the XAML hygiene + console suites and see PASS (LiveViewWindow.xaml keeps its `TextElement.Foreground` root marker; no hardcoded ARGB brushes introduced):
```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~XamlHygieneTests|FullyQualifiedName~RecordingConsoleViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
- [ ] MANUAL SMOKE (WPF, run the app normally — do not kill the user's instance; use a separate launch): (1) Open the Record console while idle: the "Remote target" dropdown lists Auto, any live call app with a friendly label, Webex + Zoom fallbacks, and "System mix - everything"; picking one updates the "Remote audio: ..." summary; the note "Applies to this recording only" is present. (2) Start recording, then use the "Change target" row: switching app->app applies instantly (transcript gets `remote capture changed to per-app by user: <image>`); switching -> System mix pops the confirm dialog — Cancel leaves the target unchanged, OK switches and adds `remote capture changed to full system mix by user (all machine audio)`. (3) With the console open, opening the dropdown or waiting ~2 s refreshes the live app list; hiding the window stops the poll (no CPU churn). (4) Pause, change target, Resume: the resumed remote leg uses the new target (no marker while paused; the switch takes effect at Resume).
- [ ] Commit:
```
git add src/LocalScribe.App/LiveViewWindow.xaml src/LocalScribe.App/LiveViewWindow.xaml.cs
git commit -m "$(cat <<'EOF'
feat(app): Record console Remote-target picker UI + live Change-target row

Idle dropdown replaces the free-text app box; a 2-column live "Change target"
row hot-swaps the remote leg (confirm-gated for system mix) with a 2 s
visible-only poll. Design 2026-07-12 section 4.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
EOF
)"
```

---

## Self-review

### (a) Spec coverage — every design section maps to a task
- Q4 live-refreshing picker with friendly labels + known fallbacks -> Tasks 1 (labels/table), 6 (`RemoteTargetOptions`/`RefreshRemoteTargetsAsync`), 7 (dropdown + 2 s poll).
- Q6 System mix as a first-class picker item (collapses into Q4) -> Task 6 (`System mix` option, `IsSystemMix`), 7 (idle dropdown + live row).
- Q11 mid-recording re-target -> Task 3 (`SetRemoteCaptureAsync`), 5 (`SwitchRemoteTargetAsync`), 6 (`ChangeRemoteTargetCommand`), 7 (live row + confirm).
- Architecture 1 (generalize override) -> Task 4. Architecture 2 (provider seam) -> Task 2. Architecture 3 (controller seam) -> Task 3.
- Data/control model: Remote target ∈ {Auto, App, System mix}; picker items; friendly labels; FullMix annotation; known-targets table + `FriendlyName` -> Tasks 1, 6.
- Live leg-swap mechanics (build-before-commit, VAD flush, AlignedAudioWriter pad, silent-monitor reset, Paused=override-only, idempotent, degrade-not-double-emitted) -> Task 3 (Core) + Task 6 (Paused/idle override-only path).
- Markers section 3 (two new constants; resolved-plan emit; fallback reuses `DegradedSystemAudioLoopback`; `RemoteSnapshot` not rewritten) -> Task 3.
- UI section 4 (idle dropdown, live row, 2-column Grid, 2 s poll + on-open/on-visible refresh, confirm dialog) -> Tasks 6 (VM refresh/confirm seam) + 7 (XAML/code-behind/timer/MessageBox).
- Wiring (inject the single `WasapiSessionScanner` as `IAudioSessionScanner`; selection handler on the VM calling the controller) -> Task 6 (CompositionRoot hoist + `AppComposition.Scanner` + App.xaml.cs) + 5/6 (`SwitchRemoteTargetAsync`/`ChangeRemoteTargetCommand`).
- Testing section 5 -> Core tests in Tasks 1-3; App tests in Tasks 4-6; pinned-test rename in Task 4; picker tests in Task 6.
- Resolved decisions (no free-text; 2 s poll; confirm only TO system mix; marker-only live changes; fallback reuses degrade) -> honored in Tasks 3/6/7. Out-of-scope items (focus auto-follow, snapshot rewrite, toolbar scraping) -> not implemented (correct).

### (b) Placeholder scan
No "TBD"/"similar to Task N"/"add error handling" left. One deliberate note: Task 6's console test file cannot reach `FakeProvider` through `MakeConsole`, so the build-failure *revert* assertion is delegated to Task 5's provider-visible `SwitchRemoteTargetAsync_returns_false...` test (which drives the exact same code path); the console happy-path test (`Live_app_switch_commits_the_selected_target`) is named accordingly. Task 7's handler code block is clean — the stray `is false` placeholder guard was removed, leaving the three real lines (`AddedItems.Count==0` guard / `as ViewModels.RemoteTargetOption` / `Execute`).

### (c) Type consistency across tasks
- `RemoteTargetOverride.Override : RemoteSetting?` (Task 4) — consumed by the console (Task 6) and asserted by tests (`over.Override?.App`).
- `ICaptureSourceProvider.CreateRemote(IClock, RemoteSetting)` (Task 2) — consumed by `SessionController.SetRemoteCaptureAsync` (Task 3) and implemented by both `WasapiCaptureSourceProvider` and `FakeProvider`.
- `Session.CurrentRemoteTarget : RemoteSetting` (Task 3) — `required`, seeded in `StartAsync`, refreshed in `ResumeAsync`, compared for idempotency.
- `Markers.RemoteCaptureChangedSystemMix` / `RemoteCaptureChangedPerApp` (Task 3) — asserted verbatim in Task 3 tests.
- `RemoteTargetOption(string Label, RemoteSetting Setting, bool IsSystemMix)` (Task 6) — value record; used as ComboBox `SelectedItem` (Task 7) and command parameter; `ChangeRemoteTargetCommand : IAsyncRelayCommand<RemoteTargetOption>`.
- `SessionViewModel.SwitchRemoteTargetAsync(RemoteSetting) : Task<bool>` (Task 5) — called by `ChangeRemoteTargetAsync` (Task 6).
- `AppComposition.Scanner : IAudioSessionScanner` (Task 6) — passed to the console ctor in App.xaml.cs.
- Console ctor param order is fixed once in Task 6 (`..., IAudioSessionScanner scanner, Func<bool> confirmSystemMix, Action<Action> dispatch`) and every call site (both test files + App.xaml.cs) is updated in the same task. `RemoteAppOverride`/`RemoteTargetOverride` type flip is atomic in Task 4 across service + CompositionRoot + console field/param + both console test files.

### (d) Known test-coverage gaps (from adversarial review — close during execution)
- **Paused → adopt-on-Resume (design section 5).** Task 3 adds `s.CurrentRemoteTarget = _settingsProvider().Remote;` in `ResumeAsync` (required so a paused override change is not stale — otherwise a later same-target `SetRemoteCaptureAsync` wrongly no-ops), but no Core test drives pause → change the composed remote setting → resume → assert the new leg is built and a subsequent same-target call is idempotent. Add that Core test in Task 3 (otherwise it is verified only by Task 7 manual smoke step 4).
- **Settings-reseed of the picker.** Task 6 removes the old `OnSettingsChanged` reseed tests (they referenced the deleted `SessionTargetApp`). Add a picker-equivalent reseed test: an untouched selection follows a `Settings.Changed`; a user-diverged selection is preserved; switching the base mode to `SystemMix` clears an armed app pick.
