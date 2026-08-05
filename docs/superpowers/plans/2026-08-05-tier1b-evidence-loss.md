# Tier 1B: Stop Losing Evidence - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three ways LocalScribe currently loses evidentiary material without saying so: an
ordinary exit that orphans a recording (T1-2), a read-view editor with no unsaved-changes guard
(T1-3), and mid-recording capture death that is neither detected, recorded, nor recovered from
(T1-4).

**Architecture:** Every decision that can be tested is extracted into a pure, clock-free, WPF-free
policy class in `LocalScribe.Core` or `LocalScribe.App/Services`, driven externally by whoever owns
the threading - the `SilentLegMonitor` / `CallActivityWatcher` / `StopConfirmToastGuard` precedent.
`SessionController` keeps ownership of all locking, all marker writes and all leg lifecycle, because
`MarkerAt` is a private record and `Session.Outbox` is private state: nothing outside the controller
can append to a live transcript. Recovery re-derivation lives in `SessionWriter`, which already holds
`_paths`, `_settings` and `_time`. Exit sequencing is one `ExitSequence` class shared by the tray Exit
menu item and `Application.SessionEnding`, so the two paths can never diverge.

**Tech Stack:** C# / .NET 10, WPF (+ Wpf.Ui), CommunityToolkit.Mvvm, NAudio, CUETools.Codecs.FLAKE,
xUnit.

## Global Constraints

- **Build/test:** `dotnet build` / `dotnet test` against `F:\LocalScribe\LocalScribe.slnx`. A running
  `LocalScribe.App.exe` locks `Core.dll` -> `MSB3027`. Close it; **never blanket-kill processes** -
  target the specific PID.
- **Test baseline (measured 2026-08-05, `--filter "Category!=Fixture"`):** Core **1186/1186**, App
  **984/984**, Mcp **6/6** = **2176**, zero failures, zero skips. **Judge regressions by failing test
  NAME, never by count.** Fixture-gated tests (`Category=Fixture`) need model weights and private
  corpora and are excluded.
- **ASCII source files.** Non-ASCII in string literals MUST be `\u` escapes; Fluent glyphs follow
  `TrayIconHost.cs:188-191`. The Edit tool silently converts escapes to literal glyphs - byte-scan
  every touched file before committing (zero bytes > 127, CRLF intact).
- **Stage files by name.** Never `git add -A` / `git add .` / `git commit -a`, never `git clean` -
  `tools/diar-eval/`, `.ai-code-review/` and `.claude/` are deliberately untracked.
- **Comment idiom:** name the design doc/date, state the REJECTED alternative and why, CAPITALISE the
  load-bearing word, use ` - ` not an em-dash. Roughly a third of `App.xaml.cs` is comment; match it.
- **Service shape:** `public sealed class X(deps) : IY` primary constructors; delegates (`Func`/
  `Action`) rather than concrete services wherever a test needs to gate them; `TimeProvider` always
  injected, never `DateTime.Now`.
- **Additive settings need no schema bump.** Add the property with a default and the sentence
  "Additive - existing v3 files without it load at this default (the SectionGapMs precedent), so no
  schema bump/migration is required." `SchemaVersion` has stayed 3 across six additive rounds.
- **Tests:** xunit `[Fact]`, `public sealed class XTests : IDisposable`, GUID temp root named
  `ls-<slug>-<guid>` under `Path.GetTempPath()`, swallow-everything `Dispose`. App.Tests writes
  `using Xunit;` explicitly; Core.Tests has `<Using Include="Xunit" />` and must not.
- **Transcripts are legal evidence.** No path may drop, reorder or silently rewrite content.
- **Spec:** `docs/superpowers/specs/2026-08-05-tier1-hardening-design.md`.
- **Shared contract:** every "SHARED-CONTRACT section N" reference in this plan means
  `docs/superpowers/specs/2026-08-05-tier1-shared-contract.md`. It is FIXED and **created by Plan A**
  (`2026-08-05-tier1a-diagnosability.md`), which must merge first. Do not redefine any of it here.

### Additional constraints specific to this plan

- **Branch:** `feat/tier1b-tier-b-stop-losing-evidence-2026-08-05`, cut from `master`.
- **Plan A must be merged first.** This plan consumes `LocalScribe.Core.Diagnostics.IDiagnosticLog`
  exactly as the shared contract defines it and never redefines it:

  ```csharp
  public interface IDiagnosticLog
  {
      void Write(string level, string source, string message, string? detail = null);
      Task FlushAsync(CancellationToken ct);
  }
  ```

  Call-site form, the whole API surface B uses:
  `_log?.Write("warn", "capture", "Local leg stalled - no frames", $"gapMs={gap}");`
  Every constructor that takes it takes it as a **trailing optional `IDiagnosticLog? log = null`**, so
  every existing call site and every existing test keeps compiling untouched.

  `Write` never throws and never blocks on IO - the enqueue takes an uncontended lock and returns
  (shared contract section 1, AMENDED 2026-08-05: a single-writer chained drain, NOT a
  `SemaphoreSlim`). That is why it is safe from the capture frame loop and from `finally` blocks.
- **Reaching the one log instance - `comp.Log`, and nothing else.** The shared contract's section 3a
  (ADDED 2026-08-05) fixes this: Plan A adds **two** members to the `AppComposition` positional
  record (`CompositionRoot.cs:21-41`, single construction site `:175-178`) - `IDiagnosticLog Log` and
  `string BuildInfo`. `comp.Log` is the ONLY defined way to reach the single instance, and it is the
  form this plan uses everywhere outside `CompositionRoot.Build()` - `App.xaml.cs` above all, where a
  local declared inside `Build()` is simply not in scope. **No step in this plan may say "whatever
  Plan A called its local".**

  Two construction sites sit INSIDE `Build()` itself and therefore cannot spell `comp.Log` - the
  record does not exist yet at that point in the method: the `SessionController` at
  `CompositionRoot.cs:85-89` (Task 8) and the `MaintenanceService` at `CompositionRoot.cs:92`
  (Task 1). For those two, and only those two, the instance is referred to by the local Plan A
  assigns it to in `Build()`, spelled `log` in the steps below. Before making either edit, VERIFY it:
  open `CompositionRoot.cs:175-178` and read the identifier Plan A passes in the record's `Log`
  argument position. That identifier is, by the contract's definition, "the one instance", and it is
  the same object `comp.Log` returns. If it is not spelled `log`, use what is actually there - this
  is a two-second read of one line, not a hedge about an unknown.
- **`FlushAsync` has two mandated call sites.** The shared contract's section 1 documents
  `Task FlushAsync(CancellationToken ct)` as "Awaited by App.OnExit and by the tray Exit path". This
  plan REPLACES the tray Exit handler (Task 3) and adds a second exit path (`SessionEnding`, Task 12),
  so both must carry the flush or the contract's stated flush point ceases to exist. `ExitSequence`
  owns it: one `flushDiagnostics` seam, awaited last, on every path that reaches the drain. Task 3
  must be applied on top of **Plan A's** version of `TrayIconHost.cs`, not master's - see Task 3
  Step 5.
- **Test commands** (exact, PowerShell, from `F:\LocalScribe`). Filtered runs use the isolated output
  path; a FULL App-suite run must NOT, because `XamlHygieneTests.RepoPaths.SolutionRoot()` walks up
  for `.git` and the Temp path sits outside the repo (5 false failures):
  - one test / one class (Core): `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\`
  - one test / one class (App): `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\`
  - full App project: `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo`
  - whole suite: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
- **No new capture-source member.** `ICaptureSource` has four implementations plus four test
  wrappers; widening it touches all of them. The house idiom for an optional capability is a
  SEPARATE interface probed with a type test - `IEndpointMuteObservable`, probed at
  `SessionController.cs:361` as `if (micSource is not IEndpointMuteObservable m) return;`. Task 7
  copies that shape exactly.
- **`FakeCaptureSource` cannot be starved.** It lives in `src/LocalScribe.Core/Audio/`, replays every
  preset frame SYNCHRONOUSLY inside `Start()` and returns; `FakeClock` never advances on its own. So
  no end-to-end controller test can ever observe "frames stopped arriving" through the existing
  doubles. Every watchdog/disk decision therefore lives in a pure state machine with its own unit
  test class, and the controller exposes an explicit tick the tests call after setting
  `clock.ElapsedMs` - the authorised fallback `SilentLegMonitor` already took
  (`SilentLegMonitor.cs:11-19`). Do NOT modify `FakeCaptureSource`: `CapturePipelineTests`,
  `CaptureFrameBridgeTests`, `LiveSourcePipelineTests` and `FakeProvider` all depend on its exact
  behaviour.
- **Fire-and-forget work exposes an awaitable.** Production code that kicks off background work adds
  a `public Task PendingX { get; }` property so tests await it instead of polling - the
  `SessionController.PendingFinalize` / `SettingsPageViewModel.LastSave` /
  `SessionsPageViewModel.ContentFilterTask` precedent. Task 8 adds `PendingCaptureRestart` for
  exactly this reason.
- **Recovery must stay fail-soft and idempotent.** `MaintenanceService.RecoverAllAsync` catches any
  exception out of `RecoverIfNeededAsync` into a `failures` list and leaves `EndedAtUtc` null, so the
  session is re-scanned and re-fails on EVERY launch while the user sees only a tray balloon. Any new
  probe code must degrade to "no re-derived value" rather than throw.
- **Never shrink persisted truth.** `RetainedAudioSources` is UNIONED with the probe result, never
  replaced; `DurationMs` is `Math.Max` of the transcript-derived and audio-derived values, never the
  bare audio value.

---

## File Structure

**Created:**
- `src/LocalScribe.Core/Storage/RetainedAudioProbe.cs` - the Core-side, existence-based leg probe
  (FLAC then WAV, both legs, UNCONDITIONAL on the stale retained list). Core cannot reach
  `LocalScribe.App.Services.AudioLegProbe`, and `RetranscriptionRunner.ResolveLegs` is private AND
  gated on `retained.Contains(kind)` - structurally incapable of re-deriving `retained`.
- `src/LocalScribe.Core/Live/FrameArrivalWatchdog.cs` - pure per-leg "no frames arrived" state
  machine; decides exactly once, holds no clock and no thread.
- `src/LocalScribe.Core/Live/DiskSpaceGuard.cs` - pure Start-refusal + once-only low-space latch.
- `src/LocalScribe.Core/Audio/ICaptureHealthObservable.cs` - the optional "this source died" capability.
- `src/LocalScribe.App/Services/ExitSequence.cs` - the one stop-then-drain-then-exit sequence shared
  by tray Exit and `Application.SessionEnding`.
- `src/LocalScribe.App/Services/PowerTransitionCoordinator.cs` - suspend/resume policy with the
  wall-clock gap, WPF-free and `TimeProvider`-driven.
- `tests/LocalScribe.Core.Tests/RetainedAudioProbeTests.cs`
- `tests/LocalScribe.Core.Tests/FrameArrivalWatchdogTests.cs`
- `tests/LocalScribe.Core.Tests/DiskSpaceGuardTests.cs`
- `tests/LocalScribe.Core.Tests/SessionControllerCaptureHealthTests.cs`
- `tests/LocalScribe.App.Tests/ExitSequenceTests.cs`
- `tests/LocalScribe.App.Tests/ReadViewDirtyTests.cs`
- `tests/LocalScribe.App.Tests/PowerTransitionCoordinatorTests.cs`

**Modified:**
- `src/LocalScribe.Core/Model/Markers.cs` - four new constants; two already-declared ones finally get
  a writer.
- `src/LocalScribe.Core/Storage/SessionWriter.cs` - `RecoverIfNeededAsync` re-derives
  `RetainedAudioSources`, `DurationMs` and `EndedAtUtc`; optional `IDiagnosticLog`.
- `src/LocalScribe.Core/Live/LiveSourcePipeline.cs` - `LegFaulted` event off an `OnlyOnFaulted`
  continuation that halts the bridge first.
- `src/LocalScribe.Core/Live/SessionController.cs` - disk preflight, per-leg frame watchdogs,
  `PollCaptureHealth`, leg restart, writer-loop fault continuation, sleep/resume marker variants.
- `src/LocalScribe.Core/Audio/MicCaptureSource.cs` - subscribes `WasapiCapture.RecordingStopped`.
- `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs` - `HasUnsavedEdits`.
- `src/LocalScribe.App/ReadViewWindow.xaml.cs` - `OnClosing` guard + `ConfirmCloseAsync`.
- `src/LocalScribe.App/TrayIconHost.cs` - `drainFinalize` seam, `BuildExitSequence()`, Exit uses it.
- `src/LocalScribe.App/Services/MaintenanceService.cs` - trailing optional `IDiagnosticLog? log`,
  forwarded into the ONE `RecoverIfNeededAsync` call site (`:836-837`) so the recovery diagnostics
  Tasks 1 and 2 write are not a dead seam.
- `src/LocalScribe.App/ViewModels/SessionViewModel.cs` - `PollCaptureHealth` on the existing tick,
  `LowDiskSpace` + `MicCaptureDead`/`RemoteCaptureDead` banner state.
- `src/LocalScribe.App/LiveViewWindow.xaml` - the low-space and dead-leg warning rows.
- `src/LocalScribe.App/App.xaml.cs` - `SystemEvents.PowerModeChanged`, `SessionEnding`, wiring.
- `src/LocalScribe.App/CompositionRoot.cs` - pass the diagnostic log into `MaintenanceService`
  (Task 1) and `SessionController` (Task 8).
- `tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs` - the recovery-scan log assertion.
- `tests/LocalScribe.App.Tests/SessionViewModelTests.cs` - the low-space and dead-leg banner flags.
- `tests/LocalScribe.Core.Tests/SessionWriterTests.cs` - the missing `RetainedAudioSources`
  assertion plus the whole re-derive suite.
- `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs` - `ManualCaptureSource`, the only double that can
  emit a frame AFTER `StartLeg` returns and can die on demand.
- `tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs` - the audio-loop fault test.

---

## Task 1: Recovery re-derives `RetainedAudioSources`

Today `RecoverIfNeededAsync` rewrites exactly five fields - `Recovered`, `EndedAtUtc`, `DurationMs`,
`SegmentCount`, `MarkerCount` - and `RetainedAudioSources` is carried forward by `session with { }`
as the `[]` that `SessionBootstrap` never set. All four consumers short-circuit on
`retained.Contains(kind)` BEFORE any `File.Exists`, so real `local.flac`/`remote.flac` on disk become
invisible to playback, re-transcription, Split Speakers and import-time detection.

**Files:**
- Create: `src/LocalScribe.Core/Storage/RetainedAudioProbe.cs`
- Create: `tests/LocalScribe.Core.Tests/RetainedAudioProbeTests.cs`
- Modify: `src/LocalScribe.Core/Storage/SessionWriter.cs:1-5` (usings), `:16-17` (ctor), `:35-60`
  (`RecoverIfNeededAsync`)
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs:34-35` (primary ctor), `:836-837` (the
  one `RecoverIfNeededAsync` call site)
- Modify: `src/LocalScribe.App/CompositionRoot.cs:92` (the one `MaintenanceService` construction)
- Test: `tests/LocalScribe.Core.Tests/SessionWriterTests.cs:10-25` (`SeedAsync`), `:58-84`
  (`Recovery_finalizes_marks_and_appends_marker`)
- Test: `tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs` (append one fact)

**Interfaces:**
- Consumes: `LocalScribe.Core.Diagnostics.IDiagnosticLog` (Plan A, signature quoted in Global
  Constraints). `StoragePaths.AudioFile(string id, SourceKind source, AudioFormat format)` ->
  `<sessionDir>/local.flac|remote.flac|local.wav|remote.wav`. `SourceKind` is in
  `LocalScribe.Core.Audio`; `AudioFormat` is in `LocalScribe.Core.Model` (already imported by
  `SessionWriter`).
- Produces:
  - `LocalScribe.Core.Storage.RetainedAudioProbe.Legs(StoragePaths paths, string sessionId)`
    -> `IReadOnlyList<(SourceKind Kind, string Path)>`, Local first. Task 2 calls it.
  - `SessionWriter(StoragePaths paths, Settings settings, TimeProvider time, IDiagnosticLog? log = null)`
    - the fourth parameter is new and optional. There are **FOURTEEN** `new SessionWriter(` sites in
    `src/` (measured 2026-08-05), not two: ten in `MaintenanceService.cs`
    (`:108,134,214,260,337,361,564,699,836,961`) plus `OfflinePipelineRunner.cs:221`,
    `RetranscriptionRunner.cs:360`, `AudioImporter.cs:247` and `SessionController.cs:1279`. Thirteen
    of them are `RegenerateProjectionsAsync`/render calls this round does not touch and they stay
    exactly as they are - optional means optional.
    **`RecoverIfNeededAsync` has exactly ONE caller in the whole product**: `MaintenanceService.cs:836-837`
    inside `RecoverAllAsync`. That is the site Step 10 threads the log through, and it is the ONLY
    reason the `_log?.Write` lines added in this task and Task 2 are reachable in production at all -
    left unthreaded they would be a permanently dead seam, and the round's recovery diagnostics would
    never reach the log Plan A exists to provide.
  - `MaintenanceService(StoragePaths paths, ISettingsService settings, IRecycleBin recycleBin,
    TimeProvider time, IDiagnosticLog? log = null)` - trailing optional on the existing primary
    constructor (`MaintenanceService.cs:34-35`), so every test construction site keeps compiling.

- [ ] **Step 1: Add the missing assertion to the existing recovery test - it must FAIL**

This assertion's absence is exactly why a 2176-test green suite hid this bug. Open
`tests/LocalScribe.Core.Tests/SessionWriterTests.cs`. The test reads, verbatim, today:

```csharp
    [Fact]
    public async Task Recovery_finalizes_marks_and_appends_marker()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: null);        // crashed: no endedAt
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));

            Assert.True(await writer.RecoverIfNeededAsync("s1", default));

            var session = await new SessionStore(paths.SessionJson("s1")).ReadAsync(default);
            Assert.True(session!.Recovered);
            Assert.Equal(T0.AddMilliseconds(2000), session.EndedAtUtc);   // last segment endMs
            Assert.Equal(2000, session.DurationMs);
            Assert.Equal(1, session.MarkerCount);
            Assert.Equal(2, session.SegmentCount);

            var lines = await new TranscriptStore(paths.TranscriptJsonl("s1")).ReadAllAsync(default);
            Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.RecoveredSession);
            Assert.True(File.Exists(paths.TranscriptMd("s1")));           // regenerated

            Assert.False(await writer.RecoverIfNeededAsync("s1", default)); // idempotent
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
```

Make exactly two edits to it. First, seed a real FLAC leg immediately after `SeedAsync` - replace

```csharp
            await SeedAsync(paths, "s1", endedAtUtc: null);        // crashed: no endedAt
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
```

with

```csharp
            await SeedAsync(paths, "s1", endedAtUtc: null);        // crashed: no endedAt
            // A crashed session has FLACs on disk and RetainedAudioSources == [] in session.json
            // (SessionBootstrap never writes the field; only PersistFinalAsync does, and a crash
            // never reaches it). 1500 ms is deliberately SHORTER than the 2000 ms transcript end,
            // so this test pins the retained re-derive WITHOUT also asserting Task 2's duration
            // re-derive - the audio does not outlast the transcript here.
            WriteLeg(paths, "s1", SourceKind.Local, AudioFormat.Flac, 1500);
            WriteLeg(paths, "s1", SourceKind.Remote, AudioFormat.Flac, 1500);
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
```

Second, add the missing assertion immediately after `Assert.Equal(2, session.SegmentCount);`:

```csharp
            // THE ASSERTION THIS TEST HAS ALWAYS BEEN MISSING (spec 2026-08-05, T1-2). Recovery
            // asserted four rewritten fields and never this one, so recovery leaving the field at
            // its `[]` default stayed green for the entire life of the product - while playback,
            // re-transcription, Split Speakers and import-time speaker detection all silently
            // refused the session because every one of them gates on retained.Contains(kind)
            // BEFORE any File.Exists.
            Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, session.RetainedAudioSources);
```

Then add the fixture writer beside `SeedAsync` at the top of the class:

```csharp
    /// <summary>Writes a real, header-valid retained leg of <paramref name="ms"/> silence through
    /// the PRODUCTION sink (so the FLAC STREAMINFO total-samples field the probe reads is written
    /// exactly as a clean finalize would write it). Synchronous on purpose: the sinks are, and a
    /// fixture writer that has to be awaited would not compose with the non-async seeds below.</summary>
    private static void WriteLeg(StoragePaths paths, string id, SourceKind kind,
        AudioFormat format, int ms)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        using var sink = AudioSinkFactory.Create(paths.AudioFile(id, kind, format), format);
        sink.Write(new float[ms * WavSink.SampleRate / 1000]);   // silence, 16 kHz mono
    }
```

`SessionWriterTests.cs` already has `using LocalScribe.Core.Audio;` (for `SourceKind`) and
`using LocalScribe.Core.Model;` (for `AudioFormat`), so no using changes are needed.

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Recovery_finalizes_marks_and_appends_marker" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL with
`Assert.Equal() Failure: Collections differ ... Expected: [Local, Remote] Actual: []`.
If it passes, the `WriteLeg` calls did not land or a stale build is being run - do not proceed.

- [ ] **Step 3: Write the probe's failing tests**

Create `tests/LocalScribe.Core.Tests/RetainedAudioProbeTests.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

/// <summary>The Core-side retained-leg probe (Tier 1B, T1-2). It exists because neither existing
/// probe can re-derive `retained`: AudioLegProbe lives in LocalScribe.App.Services (Core cannot
/// reference it) and RetranscriptionRunner.ResolveLegs is private AND returns null the moment
/// !retained.Contains(kind) - i.e. both consult the very list this probe has to rebuild.</summary>
public sealed class RetainedAudioProbeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-legprobe-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public RetainedAudioProbeTests() => _paths = new StoragePaths(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private void WriteLeg(string id, SourceKind kind, AudioFormat format)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        using var sink = AudioSinkFactory.Create(_paths.AudioFile(id, kind, format), format);
        sink.Write(new float[1600]);                       // 100 ms of silence
    }

    [Fact]
    public void Finds_nothing_for_a_session_with_no_audio()
    {
        Directory.CreateDirectory(_paths.SessionDir("s1"));
        Assert.Empty(RetainedAudioProbe.Legs(_paths, "s1"));
    }

    [Fact]
    public void Finds_nothing_for_a_session_folder_that_does_not_exist()
        => Assert.Empty(RetainedAudioProbe.Legs(_paths, "never-existed"));

    [Fact]
    public void Returns_local_before_remote_matching_the_live_feed_order()
    {
        WriteLeg("s1", SourceKind.Remote, AudioFormat.Flac);
        WriteLeg("s1", SourceKind.Local, AudioFormat.Flac);

        var legs = RetainedAudioProbe.Legs(_paths, "s1");

        Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, legs.Select(l => l.Kind));
        Assert.Equal(_paths.AudioFile("s1", SourceKind.Local, AudioFormat.Flac), legs[0].Path);
    }

    [Fact]
    public void Falls_back_to_wav_so_a_session_recorded_before_a_format_change_still_resolves()
    {
        // SessionWriter is constructed with settings.Current, i.e. the format the user has
        // configured NOW - not the one the crashed session recorded in. Probing only the
        // preferred format would lose a WAV session on a machine since switched to FLAC.
        WriteLeg("s1", SourceKind.Local, AudioFormat.Wav);

        var leg = Assert.Single(RetainedAudioProbe.Legs(_paths, "s1"));

        Assert.Equal(SourceKind.Local, leg.Kind);
        Assert.EndsWith("local.wav", leg.Path);
    }

    [Fact]
    public void Prefers_flac_when_both_containers_somehow_exist()
    {
        WriteLeg("s1", SourceKind.Local, AudioFormat.Wav);
        WriteLeg("s1", SourceKind.Local, AudioFormat.Flac);

        var leg = Assert.Single(RetainedAudioProbe.Legs(_paths, "s1"));

        Assert.EndsWith("local.flac", leg.Path);
    }
}
```

- [ ] **Step 4: Run them and confirm they fail**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~RetainedAudioProbeTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL to build - `CS0103: The name 'RetainedAudioProbe' does not exist in the current context`.

- [ ] **Step 5: Create `RetainedAudioProbe`**

Create `src/LocalScribe.Core/Storage/RetainedAudioProbe.cs`:

```csharp
// src/LocalScribe.Core/Storage/RetainedAudioProbe.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Existence-based retained-leg probe for the Core side (Tier 1B design 2026-08-05, T1-2).
///
/// DELIBERATELY UNCONDITIONAL on SessionRecord.RetainedAudioSources - that is the entire point.
/// The two existing probes both consult the very list this one has to REBUILD:
/// LocalScribe.App.Services.AudioLegProbe.Resolve returns null when !retained.Contains(kind) before
/// it ever touches the filesystem (and lives in the App assembly, which Core cannot reference), and
/// RetranscriptionRunner.ResolveLegs is private with the identical gate. Reusing either would return
/// nothing for exactly the crashed sessions this exists to repair.
///
/// FLAC first then WAV, both checked for BOTH legs: SessionWriter is constructed with
/// settings.Current (MaintenanceService.RecoverAllAsync), i.e. the format configured NOW, not the
/// format the crashed session actually recorded in - so a preferred-format-only probe would lose a
/// WAV recording on a machine since switched to FLAC. Local first, matching the live pipeline's feed
/// order and RetranscriptionRunner.ResolveLegs.
///
/// Pure and fail-soft: no IO beyond File.Exists, never throws. A session recorded with
/// AudioRetention == "never" legitimately has no legs and correctly probes empty.</summary>
public static class RetainedAudioProbe
{
    public static IReadOnlyList<(SourceKind Kind, string Path)> Legs(StoragePaths paths, string sessionId)
    {
        var legs = new List<(SourceKind, string)>();
        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
        {
            try
            {
                string flac = paths.AudioFile(sessionId, kind, AudioFormat.Flac);
                string wav = paths.AudioFile(sessionId, kind, AudioFormat.Wav);
                if (File.Exists(flac)) legs.Add((kind, flac));
                else if (File.Exists(wav)) legs.Add((kind, wav));
            }
            catch
            {
                // Fail-soft (mirrors FlacPcmReader.DurationMs's own contract): a locked or
                // permission-denied leg degrades to "not found", never to an exception. An
                // exception escaping here would land in MaintenanceService.RecoverAllAsync's
                // failures list and strand the session unrecovered on EVERY subsequent launch.
            }
        }
        return legs;
    }
}
```

- [ ] **Step 6: Run the probe tests and confirm they pass**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~RetainedAudioProbeTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, 5/5.

- [ ] **Step 7: Add the never-narrow test and the retention-never test**

Append to `tests/LocalScribe.Core.Tests/SessionWriterTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task Recovery_unions_retained_audio_and_never_narrows_an_existing_list()
    {
        // Union, never replace: a partially-written record (or an imported session) can already
        // carry a non-empty list. A momentarily unreadable leg must never DELETE a source from
        // evidentiary truth - the no-shrink rule that governs every store in this codebase.
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: null);
            var store = new SessionStore(paths.SessionJson("s1"));
            var seeded = await store.ReadAsync(default);
            await store.SaveAsync(seeded! with { RetainedAudioSources = new[] { SourceKind.Remote } }, default);
            WriteLeg(paths, "s1", SourceKind.Local, AudioFormat.Flac, 1000);   // ONLY local on disk

            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            Assert.True(await writer.RecoverIfNeededAsync("s1", default));

            var session = await store.ReadAsync(default);
            Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, session!.RetainedAudioSources);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Recovery_of_a_never_retained_session_invents_no_audio_sources()
    {
        // AudioRetention == "never" creates no AlignedAudioWriters at all (SessionController), so
        // there are legitimately no legs. The probe is existence-based, so it must find nothing
        // and the union must stay empty - never a fabricated source the UI would then offer.
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: null);
            var writer = new SessionWriter(paths, new Settings { AudioRetention = "never" },
                new ManualUtcTimeProvider(T0));

            Assert.True(await writer.RecoverIfNeededAsync("s1", default));

            var session = await new SessionStore(paths.SessionJson("s1")).ReadAsync(default);
            Assert.Empty(session!.RetainedAudioSources);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
```

- [ ] **Step 8: Implement the re-derive in `SessionWriter`**

In `src/LocalScribe.Core/Storage/SessionWriter.cs`, change the using block at `:1-5` to add the
audio and diagnostics namespaces:

```csharp
// src/LocalScribe.Core/Storage/SessionWriter.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Storage;
```

Replace the fields and constructor at `:12-17` with:

```csharp
    private readonly StoragePaths _paths;
    private readonly Settings _settings;
    private readonly TimeProvider _time;
    // Tier 1B (2026-08-05): optional so all FOURTEEN existing `new SessionWriter(` sites in src\
    // (plus eight more in tests\ - 22 measured against HEAD) keep compiling untouched. Only the
    // recovery site at MaintenanceService.cs:836 passes one.
    // Null = no diagnostics, never a null-ref: every use is `_log?.Write(...)`.
    private readonly IDiagnosticLog? _log;

    public SessionWriter(StoragePaths paths, Settings settings, TimeProvider time,
        IDiagnosticLog? log = null)
        => (_paths, _settings, _time, _log) = (paths, settings, time, log);
```

Replace the body of `RecoverIfNeededAsync` between the `lastEndMs` line and the marker append with
the retained re-derive, so the method reads:

```csharp
    public async Task<bool> RecoverIfNeededAsync(string sessionId, CancellationToken ct)
    {
        var sessionStore = new SessionStore(_paths.SessionJson(sessionId));
        var session = await sessionStore.ReadAsync(ct);
        if (session is null || session.EndedAtUtc is not null) return false;   // absent or already finalized

        var transcript = new TranscriptStore(_paths.TranscriptJsonl(sessionId));
        var before = await transcript.ReadAllAsync(ct);
        long lastEndMs = before.Count == 0 ? 0 : before.Max(l => l.EndMs);

        // Tier 1B T1-2 (design 2026-08-05): RetainedAudioSources is written ONLY by
        // SessionController.PersistFinalAsync, which runs LAST - after the whole transcription tail
        // drains. Kill the process at any point before that line and session.json still says `[]`
        // (SessionBootstrap never sets the field), which makes real FLACs on disk unreachable from
        // playback, re-transcription, Split Speakers AND import-time speaker detection: all four
        // gate on retained.Contains(kind) BEFORE any File.Exists. Re-derive from what is actually
        // on disk. UNION, never replace - a partially-written record can already carry sources, and
        // a momentarily unreadable leg must never delete one from evidentiary truth.
        var legs = RetainedAudioProbe.Legs(_paths, sessionId);
        var retained = new List<SourceKind>();
        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
            if (session.RetainedAudioSources.Contains(kind) || legs.Any(l => l.Kind == kind))
                retained.Add(kind);

        await transcript.AppendAsync(
            TranscriptLine.Marker(await transcript.NextSeqAsync(ct), lastEndMs, Markers.RecoveredSession), ct);

        var after = await transcript.ReadAllAsync(ct);
        await sessionStore.SaveAsync(session with
        {
            Recovered = true,
            EndedAtUtc = session.StartedAtUtc.AddMilliseconds(lastEndMs),
            DurationMs = lastEndMs,
            SegmentCount = after.Count(l => l.Kind == TranscriptKind.Segment),
            MarkerCount = after.Count(l => l.Kind == TranscriptKind.Marker),
            RetainedAudioSources = retained,
        }, ct);

        _log?.Write("info", "session", "Recovered an unended session",
            $"id={sessionId} lastEndMs={lastEndMs} retained={string.Join(",", retained)}");

        await RegenerateProjectionsAsync(sessionId, ct);
        return true;
    }
```

- [ ] **Step 9: Run the whole `SessionWriterTests` class and confirm it passes**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionWriterTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, including the untouched `Assert.False(await writer.RecoverIfNeededAsync(...))`
idempotence assertion at the end of the extended test.

- [ ] **Step 10: Thread the log into the ONE `RecoverIfNeededAsync` call site**

Without this step the `_log?.Write` line added in Step 8 (and the richer one in Task 2) is dead code:
`RecoverIfNeededAsync`'s only caller is `MaintenanceService.RecoverAllAsync`, which constructs its
`SessionWriter` with three arguments.

In `src/LocalScribe.App/Services/MaintenanceService.cs`, add `using LocalScribe.Core.Diagnostics;`
to the using block (alphabetically, after `using LocalScribe.Core.Assistant;` and
`using LocalScribe.Core.Audio;` - the block at `:6-12` is sorted) and add the trailing parameter to
the primary constructor at `:34-35`:

```csharp
/// <param name="log">Tier 1B (2026-08-05, T1-2): the process-wide diagnostic sink, forwarded into
/// the recovery writer at RecoverAllAsync so a launch-time recovery leaves a record of WHAT it
/// re-derived. Trailing optional - MaintenanceServiceTests and every other construction site pass
/// four arguments and keep compiling. REJECTED: giving SessionWriter the log and wiring nothing,
/// which is what a first draft of this plan did - RecoverIfNeededAsync has exactly ONE caller, so an
/// unthreaded parameter is a seam that can never fire.</param>
public sealed class MaintenanceService(StoragePaths paths, ISettingsService settings,
    IRecycleBin recycleBin, TimeProvider time, IDiagnosticLog? log = null)
```

Then forward it at the single recovery site (`:835-837`), changing only that one `new SessionWriter`:

```csharp
                bool did = await RunForSessionAsync(id,
                    inner => new SessionWriter(paths, settings.Current, time, log)
                        .RecoverIfNeededAsync(id, inner), ct);
```

Leave the other nine `new SessionWriter(...)` calls in this file alone: they are
`RegenerateProjectionsAsync`/render calls that write no diagnostics this round.

In `src/LocalScribe.App/CompositionRoot.cs`, pass the log at the single `MaintenanceService`
construction (`:92`):

```csharp
        // Tier 1B (2026-08-05, T1-2): the ONE process-wide log - the same instance Plan A puts in
        // the AppComposition.Log member at :175-178, and the same object comp.Log returns
        // everywhere outside this method (shared contract section 3a). REJECTED: a second
        // DiagnosticLog for the maintenance path - two writers appending to one diag-yyyyMM.jsonl
        // is exactly the interleaved-line corruption the single-writer drain exists to prevent.
        var maintenance = new MaintenanceService(paths, settingsService, recycleBin,
            TimeProvider.System, log);
```

Read `CompositionRoot.cs:175-178` first and confirm `log` is the identifier Plan A passes in the
record's `Log` argument position; if it is not, use the identifier that is actually there. Outside
`Build()` the log is ALWAYS reached as `comp.Log` and never any other way.

- [ ] **Step 11: Prove the recovery diagnostic actually reaches the log**

Append to `tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs`, inside the class:

```csharp
    /// <summary>Captures what production would persist, without touching a filesystem. The real
    /// DiagnosticLog's own drain/rotation/redaction is Plan A's business and is tested there; this
    /// fact exists only to prove the seam is WIRED - a log parameter that no call site ever supplies
    /// is indistinguishable from no log at all, and that is exactly the defect it guards.</summary>
    private sealed class CapturingLog : IDiagnosticLog
    {
        public List<(string Level, string Source, string Message, string? Detail)> Entries { get; } = new();
        public void Write(string level, string source, string message, string? detail = null)
            => Entries.Add((level, source, message, detail));
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task RecoverAllAsync_records_what_it_re_derived_in_the_diagnostic_log()
    {
        // Tier 1B (2026-08-05, T1-2). RecoverIfNeededAsync has ONE caller - this one - so if the
        // log is not forwarded here it is forwarded nowhere, and the whole recovery-diagnostics
        // deliverable is silently absent from a green suite.
        var log = new CapturingLog();
        var (svc, paths) = MakeService(log);
        const string id = "2026-07-03_0100_Webex_alpha";
        await WriteUnendedSessionAsync(paths, id);

        var result = await svc.RecoverAllAsync(CancellationToken.None);

        Assert.Contains(id, result.RecoveredIds);
        var entry = Assert.Single(log.Entries.Where(e => e.Source == "session"
            && e.Message == "Recovered an unended session"));
        Assert.Equal("info", entry.Level);
        Assert.Contains("id=" + id, entry.Detail);
    }
```

`WriteUnendedSessionAsync(StoragePaths, string)` at `MaintenanceServiceTests.cs:71` is the file's
existing unended-session fixture - the one `RecoverAllAsync_recovers_unended_sessions_and_isolates_failures`
already uses. Do NOT add a second seeding helper.

Add `using LocalScribe.Core.Diagnostics;` to that file's using block (after
`using LocalScribe.Core.Audio;`), and give the file's existing factory at `:19-25` a trailing
optional parameter, forwarded into the `MaintenanceService` it already builds - every one of its ~20
existing callers passes nothing and is untouched:

```csharp
    private (MaintenanceService Svc, StoragePaths Paths) MakeService(IDiagnosticLog? log = null)
    {
        var paths = new StoragePaths(_root);
        var svc = new MaintenanceService(paths, new FakeSettingsService(), new NoopRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero)), log);
        return (svc, paths);
    }
```

Run it:

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~RecoverAllAsync_records_what_it_re_derived" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL first (`CS1503` - `MaintenanceService` has no five-argument form) until Step 10 lands,
then PASS.

- [ ] **Step 12: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Storage/RetainedAudioProbe.cs src/LocalScribe.Core/Storage/SessionWriter.cs src/LocalScribe.App/Services/MaintenanceService.cs src/LocalScribe.App/CompositionRoot.cs tests/LocalScribe.Core.Tests/RetainedAudioProbeTests.cs tests/LocalScribe.Core.Tests/SessionWriterTests.cs tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs
git commit -m "fix(recovery): re-derive RetainedAudioSources from the audio actually on disk"
```

---

## Task 2: Recovery re-derives `DurationMs`/`EndedAtUtc`, and marks the discrepancy

`DurationMs` is derived from `lastEndMs` - the last TRANSCRIBED segment end. With the live worker
lagging (or dead after `TRANSCRIPTION_FAILED`), a 40-minute crashed recording persists
`DurationMs = 0` with 40 minutes of FLAC beside it. The audio is the harder evidence; the transcript
is the softer. Take `max` of the two, and **record the disagreement as a marker** rather than
silently correcting it.

**Files:**
- Modify: `src/LocalScribe.Core/Model/Markers.cs` (append one constant)
- Modify: `src/LocalScribe.Core/Storage/SessionWriter.cs` (`RecoverIfNeededAsync` + one private helper)
- Test: `tests/LocalScribe.Core.Tests/SessionWriterTests.cs`

**Interfaces:**
- Consumes: `RetainedAudioProbe.Legs(StoragePaths, string)` (Task 1);
  `LocalScribe.Core.Diagnostics.IDiagnosticLog` (Plan A);
  `LocalScribe.Core.Diarisation.FlacPcmReader.DurationMs(string path) : long` - a HEADER-only read
  (FLAC STREAMINFO total-samples via `FlakeReader.Length`, WAV via NAudio `AudioFileReader.TotalTime`)
  that returns `0` on ANY failure.
- Produces: `Markers.RecoveredAudioBeyondTranscript` (a `{0}`/`{1}` format string). Nothing later in
  this plan consumes it; Plan C's integrity manifest may.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/SessionWriterTests.cs`:

```csharp
    [Fact]
    public async Task Recovery_takes_the_duration_from_the_audio_when_it_outlasts_the_transcript()
    {
        // The evidence-loss case: the transcription worker died (or merely lagged) long before the
        // crash, so the transcript stops at 2 s while 40 s of FLAC sits on disk. The old recovery
        // persisted DurationMs = 2000 and a matching EndedAtUtc - a session.json that understates
        // the recording by 95%. Audio is the harder evidence; take the max of the two.
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: null);            // transcript ends at 2000 ms
            WriteLeg(paths, "s1", SourceKind.Local, AudioFormat.Flac, 40_000);

            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            Assert.True(await writer.RecoverIfNeededAsync("s1", default));

            var session = await new SessionStore(paths.SessionJson("s1")).ReadAsync(default);
            Assert.Equal(40_000, session!.DurationMs);
            Assert.Equal(T0.AddMilliseconds(40_000), session.EndedAtUtc);

            // The disagreement is EVIDENCE, not a silent correction: the user must be able to see
            // that 38 s of audio was never transcribed, and that Re-transcribe will recover it.
            var lines = await new TranscriptStore(paths.TranscriptJsonl("s1")).ReadAllAsync(default);
            var marker = Assert.Single(lines.Where(l => l.Kind == TranscriptKind.Marker
                && l.Text.StartsWith("recovered session: retained audio runs to", StringComparison.Ordinal)));
            Assert.Contains("00:00:40", marker.Text);       // audio end
            Assert.Contains("00:00:02", marker.Text);       // transcript end
            Assert.Equal(2, session.MarkerCount);           // RecoveredSession + this one
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Recovery_uses_the_longest_leg_not_the_sum_of_both()
    {
        // RetranscriptionRunner sums leg durations because it measures transcription WORK across
        // two legs fed sequentially. Both legs are sample-aligned to the SAME session clock
        // (AlignedAudioWriter), so summing them here would roughly DOUBLE a two-leg session's
        // recovered duration. MAX, not SUM.
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: null);
            WriteLeg(paths, "s1", SourceKind.Local, AudioFormat.Flac, 30_000);
            WriteLeg(paths, "s1", SourceKind.Remote, AudioFormat.Flac, 20_000);

            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            Assert.True(await writer.RecoverIfNeededAsync("s1", default));

            var session = await new SessionStore(paths.SessionJson("s1")).ReadAsync(default);
            Assert.Equal(30_000, session!.DurationMs);      // the LONGER leg, not 50_000
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Recovery_keeps_the_transcript_duration_when_a_leg_header_is_unreadable()
    {
        // A crashed session's FLAC was never Close()d by FlacAudioSink.Dispose, so its STREAMINFO
        // total-samples field is whatever FlakeWriter left there - FlacPcmReader.DurationMs can
        // return 0 for a file holding many minutes of audio, and its catch-all returns 0 for a
        // corrupt file too. Treat 0 as UNKNOWN. Writing it over a non-zero transcript duration
        // would make this "fix" LOSE duration rather than recover it.
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: null);
            Directory.CreateDirectory(paths.SessionDir("s1"));
            File.WriteAllBytes(paths.AudioFile("s1", SourceKind.Local, AudioFormat.Flac),
                new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00 });   // truncated "fLaC" header

            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            Assert.True(await writer.RecoverIfNeededAsync("s1", default));   // must not throw

            var session = await new SessionStore(paths.SessionJson("s1")).ReadAsync(default);
            Assert.Equal(2000, session!.DurationMs);                        // transcript-derived
            Assert.Equal(T0.AddMilliseconds(2000), session.EndedAtUtc);
            Assert.Equal(1, session.MarkerCount);                           // no discrepancy marker
            // The leg still counts as retained: the file EXISTS, so playback and re-transcription
            // must be offered it. Only its duration is unknown.
            Assert.Equal(new[] { SourceKind.Local }, session.RetainedAudioSources);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
```

- [ ] **Step 2: Run them and confirm they fail**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionWriterTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

The three new facts compile against types that all exist already (`Markers` is only read via a
string literal here), so the class BUILDS and every test in it runs.

Expected: exactly 2 failures out of the three added.
`Recovery_takes_the_duration_from_the_audio...` fails with
`Assert.Equal() Failure: Expected: 40000 Actual: 2000`;
`Recovery_uses_the_longest_leg...` fails with `Expected: 30000 Actual: 2000`;
`Recovery_keeps_the_transcript_duration_when_a_leg_header_is_unreadable` PASSES already (it pins
existing behaviour that must survive) - if it fails, stop and fix that first.

- [ ] **Step 3: Add the marker constant**

Append to the end of the `Markers` class in `src/LocalScribe.Core/Model/Markers.cs`, after
`SpeakerDetectionNoAudio`:

```csharp

    // Crash recovery re-derive (Tier 1B design 2026-08-05, T1-2). {0} = the end of the retained
    // audio, {1} = the end of the last transcript line, both h:mm:ss. Written ONLY when the audio
    // genuinely outlasts the transcript - the marker rule is that an outcome leaving no other trace
    // gets a marker, and a silent duration correction leaves none. It is not clutter on the normal
    // path: a clean stop pads audio to the stop instant (AlignedAudioWriter.PadToMs), so the two
    // agree and no marker is written.
    public const string RecoveredAudioBeyondTranscript =
        "recovered session: retained audio runs to {0} but the transcript stops at {1} - "
        + "the remainder was never transcribed; use Re-transcribe to recover it";
```

- [ ] **Step 4: Implement the duration re-derive**

In `src/LocalScribe.Core/Storage/SessionWriter.cs` add the diarisation using to the block from
Task 1 (`FlacPcmReader` lives in `LocalScribe.Core.Diarisation`):

```csharp
using LocalScribe.Core.Diarisation;
```

Then rewrite `RecoverIfNeededAsync` from the `legs` line down, so it reads:

```csharp
        var legs = RetainedAudioProbe.Legs(_paths, sessionId);
        var retained = new List<SourceKind>();
        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
            if (session.RetainedAudioSources.Contains(kind) || legs.Any(l => l.Kind == kind))
                retained.Add(kind);

        // MAX across legs, never SUM (RetranscriptionRunner sums because it measures transcription
        // WORK across two sequentially-fed legs; both legs here are sample-aligned to the SAME
        // session clock, so summing would roughly double a two-leg session). 0 means UNKNOWN, not
        // zero-length: a crashed FLAC was never Close()d, so its STREAMINFO total-samples is
        // whatever FlakeWriter left there, and FlacPcmReader.DurationMs also returns 0 for any read
        // failure. Math.Max below therefore degrades to today's transcript-derived duration.
        long audioMs = 0;
        foreach (var leg in legs)
        {
            long probed;
            try { probed = FlacPcmReader.DurationMs(leg.Path); }
            catch { probed = 0; }        // belt and braces: the reader already swallows, but an
                                          // exception escaping here strands the session forever
            if (probed > audioMs) audioMs = probed;
        }
        long durationMs = Math.Max(lastEndMs, audioMs);

        await transcript.AppendAsync(
            TranscriptLine.Marker(await transcript.NextSeqAsync(ct), lastEndMs, Markers.RecoveredSession), ct);

        if (audioMs > lastEndMs)
        {
            // NextSeqAsync re-reads the whole file (max Seq + 1), so a second marker needs a FRESH
            // call - reusing the first seq would collide. Anchored at lastEndMs, the same instant
            // as the recovery marker: the discrepancy is a fact about the whole tail, not an event
            // at the audio's end (where no transcript line exists to sit beside).
            await transcript.AppendAsync(TranscriptLine.Marker(await transcript.NextSeqAsync(ct), lastEndMs,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    Markers.RecoveredAudioBeyondTranscript, Hms(audioMs), Hms(lastEndMs))), ct);
        }

        var after = await transcript.ReadAllAsync(ct);
        await sessionStore.SaveAsync(session with
        {
            Recovered = true,
            EndedAtUtc = session.StartedAtUtc.AddMilliseconds(durationMs),
            DurationMs = durationMs,
            SegmentCount = after.Count(l => l.Kind == TranscriptKind.Segment),
            MarkerCount = after.Count(l => l.Kind == TranscriptKind.Marker),
            RetainedAudioSources = retained,
        }, ct);

        _log?.Write("info", "session", "Recovered an unended session",
            $"id={sessionId} lastEndMs={lastEndMs} audioMs={audioMs} durationMs={durationMs} "
            + $"retained={string.Join(",", retained)}");

        await RegenerateProjectionsAsync(sessionId, ct);
        return true;
    }

    /// <summary>h:mm:ss for a marker, zero-padded, invariant. Written from TOTAL hours rather than
    /// TimeSpan's "hh" custom format specifier, which TRUNCATES the day component instead of
    /// throwing - a 26-hour value would render as 02:00:00 (recorded lesson, export round
    /// 2026-08-04). Recovery durations are bounded by a single call in practice, but a wrong number
    /// in an evidentiary marker is worse than a long one.</summary>
    private static string Hms(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
            (int)span.TotalHours, span.Minutes, span.Seconds);
    }
```

- [ ] **Step 5: Run the class and confirm it passes**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionWriterTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, all tests in the class including the three from Task 1.

- [ ] **Step 6: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Model/Markers.cs src/LocalScribe.Core/Storage/SessionWriter.cs tests/LocalScribe.Core.Tests/SessionWriterTests.cs
git commit -m "fix(recovery): derive duration from the audio and mark the transcript shortfall"
```

---

## Task 3: `ExitSequence` - await `PendingFinalize` before `Shutdown()`

`StopAsync` finalizes audio synchronously then hands the transcript drain **and** the `session.json`
`EndedAtUtc`/`DurationMs` write to a background task and returns `Idle`. Nothing on any exit path
awaits `controller.PendingFinalize`. The tray Exit handler's comment claims it "never Shutdown() mid-
write", but what it awaits - `StopCommand.ExecutionTask`, i.e. `SessionController.StopAsync` - returns
the moment audio is closed. Every ordinary exit taken within seconds of Stop orphans a session into
crash recovery.

**Files:**
- Create: `src/LocalScribe.App/Services/ExitSequence.cs`
- Create: `tests/LocalScribe.App.Tests/ExitSequenceTests.cs`
- Modify: `src/LocalScribe.App/TrayIconHost.cs:28-49` (field + ctor), `:78-104` (the Exit item)
- Modify: `src/LocalScribe.App/App.xaml.cs:818-827` (the single `TrayIconHost` construction site)

**Interfaces:**
- Consumes: `LocalScribe.Core.Live.SessionState` (`Idle, Recording, Paused, Finalizing`);
  `SessionController.PendingFinalize` - a `Task` **property over a reassigned field**, so it must be
  re-read on every call and never cached; `LocalScribe.Core.Diagnostics.IDiagnosticLog` (Plan A),
  whose `FlushAsync` the shared contract documents as "Awaited by App.OnExit and by the tray Exit
  path".
- Produces:
  - `LocalScribe.App.Services.ExitSequence` with
    `public Task<bool> RunAsync()` - `true` means "the caller may now shut down", `false` means the
    user declined at the confirm prompt - and `public Task<bool> RunUnattendedAsync()`, the same
    sequence with the confirm prompt SKIPPED, for `Application.SessionEnding`.
  - `ExitSequence.ShutdownBudget : TimeSpan` - how long a caller that cannot block indefinitely
    (`SessionEnding`) may wait on the sequence. Default 8 s, constructor-injectable so the number is
    asserted in `ExitSequenceTests` rather than buried in an untested `App.xaml.cs` lambda.
  - `TrayIconHost.BuildExitSequence() : ExitSequence` - public so Task 12's
    `Application.SessionEnding` handler runs the IDENTICAL sequence.
  - `TrayIconHost`'s ctor gains two trailing optionals: `Func<Task>? drainFinalize = null` and
    `Func<Task>? flushDiagnostics = null`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.App.Tests/ExitSequenceTests.cs`:

```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The shared stop-then-drain-then-exit sequence (Tier 1B design 2026-08-05, T1-2).
/// Extracted from TrayIconHost's Exit menu item because that class has NO tests at all (there is no
/// TrayIconHostTests.cs and no STA harness in this suite), and because Application.SessionEnding
/// must run the SAME sequence - two copies of an evidentiary shutdown path would drift.</summary>
public sealed class ExitSequenceTests
{
    private sealed class Recorder
    {
        public readonly List<string> Calls = new();
        public SessionState State = SessionState.Idle;
        public bool Confirm = true;
        public Task? InFlight;
        public Exception? StopThrows;

        public ExitSequence Build() => new(
            state: () => State,
            stopRecording: () =>
            {
                Calls.Add("stop");
                return StopThrows is null ? Task.CompletedTask : Task.FromException(StopThrows);
            },
            inFlightStop: () => { Calls.Add("inflight"); return InFlight; },
            drainFinalize: () => { Calls.Add("drain"); return Task.CompletedTask; },
            confirmStopWhileRecording: () => { Calls.Add("confirm"); return Confirm; },
            notify: m => Calls.Add("notify:" + m),
            flushDiagnostics: () => { Calls.Add("flush"); return Task.CompletedTask; });
    }

    [Fact]
    public async Task An_idle_exit_still_drains_a_finalize_left_running_by_an_earlier_stop()
    {
        // THE BUG: StopAsync returns Idle the moment audio is closed and hands session.json +
        // the projection regen to a background task. Exiting seconds later - with State already
        // Idle - abandoned that write and turned a finished recording into a crash-recovery husk.
        var r = new Recorder { State = SessionState.Idle };

        Assert.True(await r.Build().RunAsync());

        Assert.Equal(new[] { "drain", "flush" }, r.Calls);
    }

    [Fact]
    public async Task A_recording_exit_confirms_then_stops_then_drains_then_flushes_in_that_order()
    {
        // The flush is LAST on purpose: every earlier step can write diagnostics (the stop, the
        // fault notice, the drain), so flushing before them would persist a log that stops short of
        // the very shutdown it is meant to explain. Shared contract section 1 names this path.
        var r = new Recorder { State = SessionState.Recording };

        Assert.True(await r.Build().RunAsync());

        Assert.Equal(new[] { "confirm", "stop", "drain", "flush" }, r.Calls);
    }

    [Fact]
    public async Task Declining_the_confirm_stops_nothing_drains_nothing_and_refuses_the_shutdown()
    {
        var r = new Recorder { State = SessionState.Recording, Confirm = false };

        Assert.False(await r.Build().RunAsync());          // caller must NOT call Shutdown()

        Assert.Equal(new[] { "confirm" }, r.Calls);        // and nothing is flushed: we are not exiting
    }

    [Fact]
    public async Task A_paused_session_takes_the_same_confirm_and_stop_path_as_a_recording_one()
    {
        var r = new Recorder { State = SessionState.Paused };

        Assert.True(await r.Build().RunAsync());

        Assert.Equal(new[] { "confirm", "stop", "drain", "flush" }, r.Calls);
    }

    [Fact]
    public async Task Finalizing_awaits_the_in_flight_stop_without_re_confirming()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r = new Recorder { State = SessionState.Finalizing, InFlight = gate.Task };

        Task<bool> run = r.Build().RunAsync();
        Assert.False(SpinWait.SpinUntil(() => r.Calls.Contains("drain"), TimeSpan.FromMilliseconds(200)));

        gate.SetResult();
        Assert.True(await run);
        Assert.Equal(new[] { "inflight", "drain", "flush" }, r.Calls);
    }

    [Fact]
    public async Task A_faulted_stop_is_surfaced_and_STILL_drains_and_still_permits_the_exit()
    {
        // The StopAsync FAULT path (a disk-full leg fault) never assigns _pendingFinalize, so the
        // drain await is a no-op there - but it must still HAPPEN, because the drain call sits
        // deliberately OUTSIDE the try/catch that swallows the stop fault. The recovery scan is
        // the documented safety net for the fault path, not this await.
        var r = new Recorder
        {
            State = SessionState.Recording,
            StopThrows = new IOException("There is not enough space on the disk."),
        };

        Assert.True(await r.Build().RunAsync());           // the user asked to exit; still exit

        Assert.Equal(new[] { "confirm", "stop", "notify:Error stopping recording: There is not enough space on the disk.", "drain", "flush" },
            r.Calls);
    }

    [Fact]
    public async Task An_unattended_run_stops_a_recording_session_without_ever_prompting()
    {
        // Windows logoff/shutdown (Application.SessionEnding). The attended path raises a modal
        // MessageBox; on the logoff path NOBODY CAN ANSWER IT - the OS is tearing the session down
        // and the caller can only wait a bounded time. A prompt there means the wait expires with
        // stopRecording never called and a live evidentiary session orphaned with no EndedAtUtc,
        // which is precisely the loss Task 13's log-off smoke item forbids. Windows has already
        // asked the user whether to log off; asking again is both impossible and redundant.
        var r = new Recorder { State = SessionState.Recording, Confirm = false };

        Assert.True(await r.Build().RunUnattendedAsync());   // Confirm=false is IGNORED here

        Assert.Equal(new[] { "stop", "drain", "flush" }, r.Calls);
        Assert.DoesNotContain("confirm", r.Calls);
    }

    [Fact]
    public async Task An_unattended_run_from_idle_still_drains_and_flushes()
    {
        var r = new Recorder { State = SessionState.Idle };

        Assert.True(await r.Build().RunUnattendedAsync());

        Assert.Equal(new[] { "drain", "flush" }, r.Calls);
    }

    [Fact]
    public void The_shutdown_budget_is_eight_seconds_by_default()
    {
        // The number lives HERE rather than as a literal in an App.xaml.cs lambda, because
        // App.xaml.cs has no test coverage in this repo at all (105 test files, no AppTests.cs).
        // 8 s sits inside the OS's own logoff grace and comfortably past a transcript drain plus a
        // session.json write. REJECTED: an unbounded wait - a hung drain would hold up the whole
        // machine's logoff, which is hostile and gets the app killed anyway.
        Assert.Equal(TimeSpan.FromSeconds(8), new Recorder().Build().ShutdownBudget);
    }
}
```

`System.IO` (`IOException`), `System.Threading` (`TaskCompletionSource`, `SpinWait`) and
`System.Linq` need no using directives: `tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj:5`
sets `<ImplicitUsings>enable</ImplicitUsings>`. The block above is the file's FINAL using block.

- [ ] **Step 2: Run them and confirm they fail**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ExitSequenceTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL to build - `CS0246: The type or namespace name 'ExitSequence' could not be found`.

- [ ] **Step 3: Create `ExitSequence`**

Create `src/LocalScribe.App/Services/ExitSequence.cs`:

```csharp
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Live;
namespace LocalScribe.App.Services;

/// <summary>The one stop-then-drain-then-exit sequence, shared by the tray Exit menu item and
/// Application.SessionEnding (Tier 1B design 2026-08-05, T1-2).
///
/// THE BUG IT CLOSES: StopAsync finalizes audio synchronously, hands the transcript drain AND the
/// session.json EndedAtUtc/DurationMs write to a background task, flips to Idle and returns. Tray
/// Exit awaited StopCommand.ExecutionTask - which IS StopAsync - and then called Shutdown(),
/// abandoning that write; the Idle branch awaited nothing at all. The result is a never-ended
/// session that goes through crash recovery on the next launch.
///
/// WPF-free and extracted rather than inlined: TrayIconHost has no test coverage in this repo (no
/// TrayIconHostTests.cs, no STA harness), so anything left in that class is permanently untestable -
/// the StopConfirmToastGuard precedent. The MessageBox and the Shutdown() call stay at the call
/// site; every decision lives here.</summary>
public sealed class ExitSequence(
    Func<SessionState> state,
    Func<Task> stopRecording,
    Func<Task?> inFlightStop,
    Func<Task> drainFinalize,
    Func<bool> confirmStopWhileRecording,
    Action<string> notify,
    Func<Task>? flushDiagnostics = null,
    IDiagnosticLog? log = null,
    TimeSpan? shutdownBudget = null)
{
    /// <summary>How long a caller that CANNOT block indefinitely may wait on this sequence -
    /// Application.SessionEnding, where the OS is waiting on the UI thread. Lives here rather than
    /// as a literal in an App.xaml.cs lambda because App.xaml.cs has no test coverage in this repo
    /// (105 test files, no AppTests.cs), so a number left there is a number nothing asserts.
    /// REJECTED: an unbounded wait - a hung drain would hold up the machine's logoff, which is
    /// hostile and ends in the app being killed regardless.</summary>
    public TimeSpan ShutdownBudget { get; } = shutdownBudget ?? TimeSpan.FromSeconds(8);

    /// <summary>Runs the sequence with the user present. Returns true when the caller may proceed
    /// to Shutdown(), false only when the user declined the "a recording is in progress" prompt.
    /// Never throws.</summary>
    public Task<bool> RunAsync() => RunCoreAsync(confirm: true);

    /// <summary>The SAME sequence with the confirm prompt SKIPPED - for Application.SessionEnding
    /// (Windows logging off or shutting down). NOBODY CAN ANSWER A MODAL BOX during logoff: the OS
    /// is tearing the session down and the caller can only wait ShutdownBudget, so a prompt there
    /// expires with stopRecording never called and a live evidentiary session orphaned with no
    /// EndedAtUtc - the exact loss this whole task exists to close. Windows has already asked the
    /// user whether to log off, so a second question would be redundant even if it could be seen.
    /// REJECTED: a second hand-written unattended sequence - the confirm is the ONLY difference,
    /// and two copies of an evidentiary shutdown path drift.</summary>
    public Task<bool> RunUnattendedAsync() => RunCoreAsync(confirm: false);

    private async Task<bool> RunCoreAsync(bool confirm)
    {
        try
        {
            var s = state();
            if (s is SessionState.Recording or SessionState.Paused)
            {
                // Attended only: never kill a live recording silently while the user is there to
                // be asked. Unattended, stopping IS the protective act.
                if (confirm && !confirmStopWhileRecording()) return false;
                log?.Write("info", "session", "Exit requested while recording - stopping first",
                    $"confirmed={confirm}");
                await stopRecording();
            }
            else if (s == SessionState.Finalizing)
            {
                // A stop is already in flight (Exit clicked right after Stop): do not re-confirm.
                if (inFlightStop() is { } finalize) await finalize;
            }
        }
        catch (Exception ex)
        {
            // A StopAsync fault must not become an unhandled async-void exception, and must not
            // block the exit the user already asked for.
            log?.Write("error", "session", "Stop failed on the exit path", ex.ToString());
            notify("Error stopping recording: " + ex.Message);
        }

        // DELIBERATELY OUTSIDE the try/catch above, and deliberately unconditional.
        // - Outside, because a faulted stop must still reach this line.
        // - Unconditional, because the Idle branch is the common case: a Stop seconds ago has
        //   already returned Idle while its background finalize is still writing session.json.
        // - AFTER the stop, never before: StopAsync assigns _pendingFinalize synchronously before
        //   returning, so awaiting first would await the PREVIOUS session's completed task.
        // The delegate re-reads SessionController.PendingFinalize on every call - it is a property
        // over a reassigned field, so a captured Task would be permanently stale.
        // KNOWN LIMITATION, stated rather than papered over: on the StopAsync FAULT path
        // _pendingFinalize is never assigned at all, so this await returns instantly with
        // session.json unwritten. The launch-time recovery scan is the documented safety net for
        // that path (SessionController's own FinalizeInBackgroundAsync catch says so), and Task 1
        // of this plan is what makes that recovery non-lossy.
        try { await drainFinalize(); }
        catch { /* FinalizeInBackgroundAsync swallows everything; this can only be a wiring fault */ }

        // LAST, and only on a path that is genuinely exiting. The shared contract (section 1) names
        // this as one of IDiagnosticLog.FlushAsync's two mandated call sites - "Awaited by
        // App.OnExit and by the tray Exit path" - and this class IS the tray Exit path now, as well
        // as the SessionEnding path. After the drain, never before: the stop, the fault notice and
        // the drain all write diagnostics, so flushing earlier would persist a log that stops short
        // of the shutdown it exists to explain. Null-safe, so an ExitSequence built with no log
        // (every unit test that does not care) still runs.
        try { await (flushDiagnostics?.Invoke() ?? Task.CompletedTask); }
        catch { /* FlushAsync never throws by contract; a wiring fault must not block the exit */ }
        return true;
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ExitSequenceTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, 9/9.

- [ ] **Step 5: Wire it into `TrayIconHost`**

**Apply this step on top of PLAN A's `TrayIconHost.cs`, not master's.** The quoted Exit item below
is master's text (verified byte-for-byte against HEAD at `:78-104`), but Plan A lands first and the
shared contract puts an `IDiagnosticLog.FlushAsync` await on "the tray Exit path". Before deleting
anything, READ the current handler: if Plan A added a flush await there, it is NOT lost - it moves
into `ExitSequence` via the `flushDiagnostics` seam wired below, which is where both exit paths now
get it. If the handler no longer matches the quote in any other way, port the CURRENT shape.

In `src/LocalScribe.App/TrayIconHost.cs`, add two fields after `_openExport` (`:31`):

```csharp
    // Tier 1B (2026-08-05, T1-2): re-reads SessionController.PendingFinalize on every call - it is
    // a property over a REASSIGNED field, so a captured Task would be permanently stale. Nullable
    // with a no-op default, following this file's own _openExport precedent, so the existing
    // construction site and any future caller that wires no controller still builds.
    private readonly Func<Task>? _drainFinalize;
    // Tier 1B (2026-08-05, T1-2): IDiagnosticLog.FlushAsync, which the shared contract documents as
    // "Awaited by App.OnExit and by the tray Exit path". Held as a delegate rather than as the log
    // itself so this WPF class keeps no reference to a Core service it does not otherwise use, and
    // so ExitSequence - which owns the ordering - is the only thing that decides WHEN it runs.
    private readonly Func<Task>? _flushDiagnostics;
```

Add the two trailing parameters to the constructor signature (`:35-39`) and to its tuple assignment
(`:48-49`):

```csharp
    public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, StoragePaths paths,
        ISettingsService settingsService, WindowStateStore windowState,
        Action<string, string>? openExport,
        Func<MainWindow> mainWindowFactory,
        Func<Task>? drainFinalize = null,
        Func<Task>? flushDiagnostics = null)
```

```csharp
        (_session, _lines, _console, _paths, _settingsService, _windowState, _openExport, _mainWindowFactory)
            = (session, lines, console, paths, settingsService, windowState, openExport, mainWindowFactory);
        _drainFinalize = drainFinalize;
        _flushDiagnostics = flushDiagnostics;
```

Add the public builder immediately after the constructor:

```csharp
    /// <summary>The exit sequence this host's Exit menu item runs. PUBLIC so
    /// Application.SessionEnding (App.xaml.cs) runs the IDENTICAL sequence - two hand-written
    /// copies of an evidentiary shutdown path would drift, and only one of them would ever be
    /// exercised by hand. SessionEnding calls RunUnattendedAsync on the object this returns, so the
    /// MessageBox below is reached only when a human is actually there to answer it.</summary>
    public ExitSequence BuildExitSequence() => new(
        state: () => _session.State,
        stopRecording: () => _session.StopCommand.ExecuteAsync(null),
        inFlightStop: () => _session.StopCommand.ExecutionTask,
        drainFinalize: _drainFinalize ?? (() => Task.CompletedTask),
        confirmStopWhileRecording: () => MessageBox.Show(
            "A recording is in progress. Stop and exit?", "LocalScribe",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
        notify: m => _icon.ShowNotification("LocalScribe", m),
        flushDiagnostics: _flushDiagnostics);
```

Replace the whole Exit menu item (`:78-104`, master's text) with:

```csharp
        menu.Items.Add(Item("Exit", async (_, _) =>
        {
            // Tier 1B (2026-08-05): the decision logic moved into the tested ExitSequence. This
            // handler is now confirm-free glue - the sequence owns the confirm, the stop, the
            // fault surfacing AND the PendingFinalize drain that this path never had.
            if (await BuildExitSequence().RunAsync()) Application.Current.Shutdown();
        }));
```

- [ ] **Step 6: Wire the controller's task into `App.xaml.cs`**

In `src/LocalScribe.App/App.xaml.cs`, at the single `TrayIconHost` construction site (`:818`), add
the two new arguments after `mainWindowFactory:`. Both are inside `OnStartup`, where `comp` is in
scope (`comp.Paths` and `comp.Settings` are already arguments to this same call), so the log is
reached as `comp.Log` - the shared contract's section 3a member, and the only defined way to reach
the single instance from here:

```csharp
        _tray = new TrayIconHost(session, lines, console, comp.Paths, comp.Settings, windowState,
            openExport,
            mainWindowFactory: () => new MainWindow(mainVm, windowState, comp.Settings,
                new StaticPageProvider(new Dictionary<Type, object>
                {
                    [typeof(Pages.SessionsPage)] = new Pages.SessionsPage(sessionsVm),
                    [typeof(Pages.SearchPage)] = new Pages.SearchPage(searchVm),
                    [typeof(Pages.MattersPage)] = new Pages.MattersPage(mattersVm),
                    [typeof(Pages.SettingsPage)] = new Pages.SettingsPage(settingsVm),
                })),
            // Tier 1B (2026-08-05, T1-2): the lambda re-reads the property every call. OnExit
            // cannot do this - it is a synchronous override with no `comp` in scope, and a
            // .GetAwaiter().GetResult() there would block the dispatcher during shutdown - so the
            // drain belongs on the two paths that CAN await: this one and SessionEnding.
            drainFinalize: () => comp.Controller.PendingFinalize,
            // Shared contract section 1: FlushAsync is "Awaited by App.OnExit and by the tray Exit
            // path". comp.Log is the ONE instance (contract section 3a) - never a second
            // DiagnosticLog, and never a local from CompositionRoot.Build(), which is not in scope
            // in this method. CancellationToken.None deliberately: a flush that gave up early would
            // discard exactly the lines describing the shutdown being diagnosed.
            flushDiagnostics: () => comp.Log.FlushAsync(CancellationToken.None));
```

- [ ] **Step 7: Build and run the App suite**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo
```

Expected: PASS, App 994/994 (984 baseline + 1 from Task 1's recovery-log fact + 9 new here). No
isolated `BaseOutputPath` on this run - `XamlHygieneTests` needs the repo-internal path.

- [ ] **Step 8: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/Services/ExitSequence.cs src/LocalScribe.App/TrayIconHost.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/ExitSequenceTests.cs
git commit -m "fix(exit): drain PendingFinalize before Shutdown via a shared ExitSequence"
```

---

## Task 4: `ReadViewViewModel.HasUnsavedEdits`

The only editor in the product with no close protection is the one that edits evidence.
`SessionDetailsWindow` has a full force-commit Save/Discard/Cancel guard; `ReadViewWindow` has no
`OnClosing`, no `Closing=` and no dirty flag of any kind. There is no `IsDirty` on
`ReadViewViewModel` and nothing resembling one - edit state lives in `EditSections` and is harvested
only inside `SaveEditsAsync`. This task builds the flag; Task 5 consumes it.

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs` (add one computed property beside
  `HasSaveError` at `:66-68`; it uses the existing private `SameSpeakerTarget` at `:1010-1013`)
- Test: `tests/LocalScribe.App.Tests/ReadViewDirtyTests.cs` (create)

**Interfaces:**
- Consumes: `EditableSectionViewModel.IsEditing` (bool), `.Row` (`DisplayRow`), `.Segments`
  (`ObservableCollection<EditableSegmentViewModel>`), `.CollectSplitReverts() : IReadOnlyCollection<int>`,
  `.SplitSegment(EditableSegmentViewModel seg, int caret)`, `.RevertSplit(int seq)`;
  `ReadViewViewModel.ExpandSection(EditableSectionViewModel section) : void` (`:372-374`) - the VM's
  own expand seam, documented there as "Public: find jump-in and tests share it". **The tests must go
  through it and must NOT call `section.BeginEdit(...)` directly.** `BeginEdit`'s three trailing
  arguments are optional and `EditableSectionViewModel.BeginEdit` (`:100-125`) coalesces them to
  `_remoteChoices = []` / `_localChoices = []`, so a direct call with them omitted materializes every
  `EditableSegmentViewModel` with an EMPTY `SpeakerChoices` list and a null `Speaker` - the speaker
  leg of this task would then be untestable no matter what the fixture seeds. `ExpandSection` passes
  `SpeakerChoicesForRemote()`, `SpeakerChoicesForLocal()` and `CurrentSpeakerFor`, which is exactly
  what the window's own click path passes;
  `EditableSegmentViewModel.Seq` (int), `.EditedText` (settable), `.ProjectedText` (get-only, seeded
  at construction), `.Speaker` (settable `SpeakerChoice?`), `.OriginalSpeaker` (get-only),
  `.IsSplitChild`.
- Produces: `ReadViewViewModel.HasUnsavedEdits : bool` - Task 5 reads it in `OnClosing` and again
  after an attempted save.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.App.Tests/ReadViewDirtyTests.cs`:

```csharp
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The read-view close guard's dirty signal (Tier 1B design 2026-08-05, T1-3).
/// ReadViewViewModel had no IsDirty and no equivalent; edit state lives in EditSections and was
/// harvested only inside SaveEditsAsync. The window code-behind that consumes this is untestable in
/// this suite (no STA harness anywhere in tests/LocalScribe.App.Tests), so every decidable part of
/// the guard lives here instead.</summary>
public sealed class ReadViewDirtyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-dirty-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakeSettings _settings;
    private readonly FakeReporter _reporter = new();
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly MaintenanceService _maintenance;
    private readonly FakePlayer _player = new();

    public ReadViewDirtyTests()
    {
        _paths = new StoragePaths(_root);
        _settings = new FakeSettings(new Settings { StorageRoot = _root });
        _maintenance = new MaintenanceService(_paths, _settings, new FakeBin(), _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>A finalized two-turn session: EndedAtUtc set (CanEdit gates on it) and two turns
    /// 10 minutes apart, well past the SectionGapMs default, so TranscriptProjection always groups
    /// them into two DISTINCT rows - one editable section per turn.</summary>
    private async Task<ReadViewViewModel> LoadAsync()
    {
        Directory.CreateDirectory(_paths.SessionDir("s1"));
        await new SessionStore(_paths.SessionJson("s1")).SaveAsync(new SessionRecord
        {
            Id = "s1", App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, default);
        await new MetadataStore(_paths.MetaJson("s1")).SaveAsync(new SessionMeta
        {
            Title = "Doe intake",
            // TWO named Local participants, seeded UNCONDITIONALLY. SpeakerChoices.Build emits a
            // leading "Automatic (Me / Them)" choice and then one entry per NAMED participant on the
            // matching side, so a fixture with no participants yields a single-entry list and
            // A_speaker_reassignment_alone_makes_it_dirty has nothing to reassign TO. Both segments
            // below are TranscriptSource.Local, so both slots must be Side = SourceKind.Local.
            // REJECTED: seeding this only if the test throws - a conditional fixture makes the one
            // leg this task calls "easiest to forget" the one leg that silently stops running.
            Participants =
            [
                new SessionParticipant { Id = "p-me", Name = "Me", Side = SourceKind.Local, IsSelf = true },
                new SessionParticipant { Id = "p-roe", Name = "Ms Roe", Side = SourceKind.Local },
            ],
            LocalCount = 2,
        }, default);
        var store = new TranscriptStore(_paths.TranscriptJsonl("s1"));
        await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 4000,
            "Good morning.", "Me"), default);
        await store.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 600_000, 604_000,
            "That concludes it.", "Me"), default);

        var vm = new ReadViewViewModel(_maintenance, _paths, _settings, _reporter, _player,
            dispatch: a => a(), _time);
        await vm.LoadAsync("s1", default);
        return vm;
    }

    /// <summary>Expands through the VM's OWN seam. ReadViewViewModel.ExpandSection (:372-374) is
    /// documented "Public: find jump-in and tests share it" and passes SpeakerChoicesForRemote(),
    /// SpeakerChoicesForLocal() and CurrentSpeakerFor. REJECTED: section.BeginEdit("relative",
    /// vm.StartedAtLocal) - BeginEdit's three trailing arguments are optional and coalesce to `[]`
    /// (EditableSectionViewModel.cs:100-125), so every materialized segment would get an EMPTY
    /// SpeakerChoices list and a null Speaker, and the reassignment fact could never find an
    /// alternative choice no matter what meta.json holds.</summary>
    private static EditableSectionViewModel Expand(ReadViewViewModel vm, int index)
    {
        var section = vm.EditSections[index];
        vm.ExpandSection(section);
        return section;
    }

    [Fact]
    public async Task Not_dirty_before_edit_mode_is_even_entered()
    {
        var vm = await LoadAsync();
        Assert.False(vm.IsEditMode);
        Assert.False(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task Not_dirty_after_entering_edit_mode_and_expanding_a_section_but_typing_nothing()
    {
        // The most important negative: merely OPENING the editor must never prompt on close. A
        // false "unsaved changes" dialog on every read-view close trains the user to click through
        // it, which is how a real one gets dismissed.
        var vm = await LoadAsync();
        vm.EnterEditMode();
        Expand(vm, 0);

        Assert.True(vm.IsEditMode);
        Assert.False(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task A_text_correction_makes_it_dirty()
    {
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);

        section.Segments[0].EditedText = "Good morning, Mr Doe.";

        Assert.True(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task Whitespace_only_retyping_is_not_dirty()
    {
        // Matches the correction no-op guard SaveEditsAsync uses (EditedText.Trim() vs
        // ProjectedText.Trim()): a stray trailing space must not manufacture a phantom edit and
        // must not manufacture a phantom close prompt either.
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);

        section.Segments[0].EditedText = "  Good morning.  ";

        Assert.False(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task A_new_split_makes_it_dirty()
    {
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);

        section.SplitSegment(section.Segments[0], caret: 5);

        Assert.Equal(2, section.Segments.Count);
        Assert.True(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task A_split_revert_makes_it_dirty()
    {
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);
        section.SplitSegment(section.Segments[0], caret: 5);

        section.RevertSplit(section.Segments[0].Seq);

        // Part count is back to one, so the count comparison alone would read CLEAN - the pending
        // revert is only visible through CollectSplitReverts, which is why it is checked separately.
        Assert.Single(section.Segments);
        Assert.True(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task A_speaker_reassignment_alone_makes_it_dirty()
    {
        // The leg that is easiest to forget: a pure re-attribution changes no text and creates no
        // split, and SaveEditsAsync detects it in a SEPARATE loop via SameSpeakerTarget. Missing
        // it would let a whole session's re-attribution close silently.
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);
        var seg = section.Segments[0];
        var current = seg.Speaker;

        // The fixture seeds two named Local participants, so Build() returns
        // [Automatic (Me / Them), Me, Ms Roe] and there is ALWAYS a genuine alternative. Asserted
        // rather than null-coalesced into a throw: if this ever comes back empty the fixture broke,
        // and that must read as a failure here, not as a confusing exception inside the act.
        var other = seg.SpeakerChoices.FirstOrDefault(c => !SameTarget(c, current));
        Assert.NotNull(other);

        seg.Speaker = other;

        Assert.True(vm.HasUnsavedEdits);
    }

    /// <summary>Local mirror of ReadViewViewModel's private SameSpeakerTarget, used only to pick a
    /// choice that genuinely DIFFERS from the pre-selected one - comparing by target, not display
    /// text, so a renamed participant is not mistaken for a different one.</summary>
    private static bool SameTarget(SpeakerChoice? a, SpeakerChoice? b) =>
        (a?.IsUnassign ?? false) == (b?.IsUnassign ?? false)
        && string.Equals(a?.ParticipantId, b?.ParticipantId, StringComparison.Ordinal)
        && string.Equals(a?.ClusterKey, b?.ClusterKey, StringComparison.Ordinal);

    [Fact]
    public async Task Cancelling_the_edit_clears_the_dirty_flag()
    {
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);
        section.Segments[0].EditedText = "Good morning, Mr Doe.";
        Assert.True(vm.HasUnsavedEdits);

        vm.CancelEdit();

        Assert.False(vm.IsEditMode);
        Assert.False(vm.HasUnsavedEdits);                       // EditSections cleared
    }

    [Fact]
    public async Task A_successful_save_clears_the_dirty_flag()
    {
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);
        section.Segments[0].EditedText = "Good morning, Mr Doe.";

        await vm.SaveEditsAsync(default);

        Assert.Null(vm.SaveError);
        Assert.False(vm.IsEditMode);
        Assert.False(vm.HasUnsavedEdits);                       // the close guard may now proceed
    }
}
```

Then copy the four per-file fakes to the BOTTOM of the same file (inside the class), mirroring
`ReadViewEditModeTests.cs:534-565` - house convention is to duplicate and point at the sibling copy,
never to extract a shared helper:

```csharp
    // Duplicated from ReadViewEditModeTests.cs:534-565 per the house convention (no cross-file test
    // helper); kept identical so a change to the VM's seams surfaces in both places at once.
    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        {
            var old = Current;
            Current = updated;
            Changed?.Invoke(old, updated);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBin : IRecycleBin
    {
        public void SendToRecycleBin(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }

    private sealed class FakePlayer : IDualAudioPlayer
    {
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public event Action? MediaReady;
        public event Action? MediaEnded;
        public void Load(string? localPath, string? remotePath) { }
        public void Play() { }
        public void Pause() { }
        public void SeekMs(long ms) => PositionMs = ms;
        public void SetLegMuted(bool local, bool muted) { }
        public void SetLegVolume(bool local, double volume) { }
        public void Dispose() { }
        public void RaiseReady() => MediaReady?.Invoke();
        public void RaiseEnded() => MediaEnded?.Invoke();
    }
```

- [ ] **Step 2: Run them and confirm they fail**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ReadViewDirtyTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL to build - `CS1061: 'ReadViewViewModel' does not contain a definition for 'HasUnsavedEdits'`.

- [ ] **Step 3: Implement `HasUnsavedEdits`**

In `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`, insert immediately after the
`EditSections` declaration at `:68`:

```csharp
    /// <summary>True when Edit mode holds work that would be LOST by closing the window (Tier 1B
    /// design 2026-08-05, T1-3). Computed on demand, deliberately NOT an [ObservableProperty]:
    /// nothing binds to it, the close guard reads it exactly twice (once in OnClosing, once after
    /// an attempted save), and making it observable would mean raising PropertyChanged on every
    /// keystroke across every expanded section for no consumer.
    ///
    /// Derived from the four things SaveEditsAsync actually writes, and deliberately NOT from the
    /// Collect* trio wholesale:
    /// - CollectSplits() is UNUSABLE here: it returns every seq that HAS a split child, INCLUDING
    ///   splits already persisted and merely re-materialized by BeginEdit - so simply re-opening a
    ///   session that was split last week would read as dirty and prompt on every close. A NEW
    ///   split is instead detected as "this seq's materialized part count no longer matches the
    ///   loaded row's part count for that seq".
    /// - CollectCorrections() is not used because it deliberately EXCLUDES split children and gates
    ///   on a single-segment group, so a text edit typed into a split part would read clean. The
    ///   per-segment EditedText-vs-ProjectedText compare below is strictly broader and applies the
    ///   same Trim() no-op rule, so a whitespace-only retype is still clean.
    /// - The speaker leg is checked explicitly: a pure re-attribution changes no text and creates
    ///   no split, and SaveEditsAsync only notices it through its own separate SameSpeakerTarget
    ///   loop. Missing it would let a whole session's re-attribution close silently.
    /// Filtered on IsEditing exactly as SaveEditsAsync is: a collapsed section was never
    /// materialized (Segments is empty) and can hold no edits.</summary>
    public bool HasUnsavedEdits
    {
        get
        {
            if (!IsEditMode) return false;
            foreach (var sec in EditSections.Where(s => s.IsEditing))
            {
                if (sec.CollectSplitReverts().Count > 0) return true;   // a revert leaves no other trace
                foreach (var g in sec.Segments.GroupBy(s => s.Seq))
                    if (g.Count() != sec.Row.Segments.Count(s => s.Seq == g.Key))
                        return true;                                     // a split made in THIS session
                foreach (var seg in sec.Segments)
                {
                    if (seg.EditedText.Trim() != seg.ProjectedText.Trim()) return true;
                    if (!SameSpeakerTarget(seg.Speaker, seg.OriginalSpeaker)) return true;
                }
            }
            return false;
        }
    }
```

- [ ] **Step 4: Run them and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ReadViewDirtyTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, 9/9.

- [ ] **Step 5: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/ReadViewViewModel.cs tests/LocalScribe.App.Tests/ReadViewDirtyTests.cs
git commit -m "feat(read-view): derive HasUnsavedEdits from every edit SaveEditsAsync writes"
```

---

## Task 5: Port the close guard to `ReadViewWindow`

`SessionDetailsWindow` cancels a dirty close and re-closes from an async continuation, because WPF
cannot await inside `OnClosing`. Port that shape. **This task has no automated test** - there is no
STA harness, no `DispatcherFrame` helper and no fake `Window` anywhere in
`tests/LocalScribe.App.Tests`, and the donor guard itself has zero tests. Everything decidable moved
into Task 4; what remains is plumbing, verified by the smoke checklist in Task 13 rather than by a
test that would have to be faked.

**Files:**
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml.cs:36-40` (field), and append `OnClosing` +
  `ConfirmCloseAsync` immediately BEFORE the existing `OnClosed` at `:933`
- Donor (read only, never edited): `src/LocalScribe.App/SessionDetailsWindow.xaml.cs:30` (the
  `_closeConfirmed` field), `:81-98` (`OnClosing`), `:108-134` (`ConfirmCloseAsync`)

**Interfaces:**
- Consumes: `ReadViewViewModel.HasUnsavedEdits : bool` (Task 4);
  `ReadViewViewModel.SaveEditsAsync(CancellationToken ct) : Task` - catches its own exceptions, sets
  `SaveError` and returns with `IsEditMode` still true on failure, so it must be probed by re-reading
  the dirty flag, never by catching; `ReadViewViewModel.CancelEdit() : void` - synchronous, discards
  and always clears `SaveError`.
- Produces: nothing consumed later.

- [ ] **Step 1: Read the donor and confirm it is unchanged**

Open `src/LocalScribe.App/SessionDetailsWindow.xaml.cs:81-98` (`ConfirmCloseAsync` follows at
`:108-134`). The method body is quoted below BYTE-FOR-BYTE INCLUDING ITS INLINE COMMENTS - measured
against HEAD 2026-08-05 - so a diff against the real file is meaningful. Only the leading `///`
summary block (`:75-80`) is elided. If the body has changed, port the CURRENT shape, not this quote:

```csharp
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed) return;
        // Force-commit a focused LostFocus-bound TextBox, if any, so IsDirty and the VM working
        // copy reflect what is on screen before we decide anything.
        if (Keyboard.FocusedElement is TextBox tb)
        {
            // A participant name box binds Text OneTime and commits via LostFocus->RenameParticipant,
            // which never fires if the user types then closes with X while still focused. Commit it
            // here so the rename (and its dirty flag) is captured before the save/discard decision.
            if (tb.DataContext is ParticipantRow row) _vm.RenameParticipant(row, tb.Text);
            else tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
        if (!_vm.IsDirty) return;               // clean: let the close proceed
        e.Cancel = true;                        // dirty: stop THIS close; decide via the async dialog
        _ = ConfirmCloseAsync();
    }
```

The `ParticipantRow` branch is deliberately NOT ported: `ReadViewWindow` has no participant name
boxes. The `UpdateSource()` branch IS ported, for the reason Step 3's doc comment gives.

- [ ] **Step 2: Add the re-entrancy flag**

In `src/LocalScribe.App/ReadViewWindow.xaml.cs`, beside the other private fields at `:36-40`
(`_vm`, `_sessionId`, `_registry`, ...), add:

```csharp
    // Tier 1B (2026-08-05, T1-3): set by ConfirmCloseAsync (Save-clean or Discard) so the
    // re-entrant Close() it issues skips the prompt instead of looping. Same field, same purpose
    // and same one-line comment as SessionDetailsWindow.xaml.cs:30, the guard this is ported from.
    private bool _closeConfirmed;
```

- [ ] **Step 3: Add `OnClosing` and `ConfirmCloseAsync`**

Insert immediately BEFORE `protected override void OnClosed(EventArgs e)` in the same file:

```csharp
    /// <summary>Close guard, ported from SessionDetailsWindow.xaml.cs:81-98 (Tier 1B design
    /// 2026-08-05, T1-3). Until now the ONLY editor in the product with no close protection was the
    /// one that edits evidence: a whole session's corrections, splits and re-attributions vanished
    /// on an X-click with no prompt.
    ///
    /// WPF cannot await inside OnClosing, so a dirty editor CANCELS this close and hands off to
    /// ConfirmCloseAsync, which shows the dialog and re-Closes (with _closeConfirmed set) only on
    /// Save-that-settled-clean or Discard.
    ///
    /// The focused-box force-commit stays HERE, BEFORE the dirty gate. In this window the edit
    /// TextBox already binds EditedText with UpdateSourceTrigger=PropertyChanged
    /// (ReadViewWindow.xaml:658), so today it is belt-and-braces - but the donor's rule is that a
    /// LostFocus-bound box never commits on an X-close, and committing AFTER the gate could drop a
    /// half-typed edit that is the only change. Any future LostFocus-bound field in this window is
    /// then covered by construction rather than by remembering to revisit this method.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed) return;
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (!_vm.HasUnsavedEdits) return;       // clean: let the close proceed
        e.Cancel = true;                        // dirty: stop THIS close; decide via the async dialog
        _ = ConfirmCloseAsync();
    }

    /// <summary>Themed unsaved-changes prompt (WPF-UI 4.0.3 Wpf.Ui.Controls.MessageBox) - the donor's
    /// ConfirmCloseAsync with two deliberate substitutions.
    ///
    /// (1) It calls _vm.SaveEditsAsync DIRECTLY rather than the window's SaveEditsCommand: that
    /// command routes through SaveEditsPreservingScrollAsync, which captures a scroll anchor and
    /// re-scrolls the rebuilt list on a Dispatcher.BeginInvoke(DispatcherPriority.Loaded)
    /// continuation - pointless work on a window that is about to close, and a continuation queued
    /// against a closing window is exactly the kind of thing that throws later.
    ///
    /// (2) It re-reads HasUnsavedEdits instead of catching, because SaveEditsAsync NEVER throws: it
    /// catches, sets SaveError and returns with IsEditMode still true. A failed or partially-failed
    /// save therefore leaves the editor dirty and the window OPEN, with the in-window SaveError
    /// InfoBar already explaining why - the same semantics the donor gets from re-reading IsDirty.
    ///
    /// Secondary (Discard) reverts via CancelEdit and closes. None (Cancel / Esc / title-bar close)
    /// stays open. The dialog is shown on a user close action, long after the message pump is up, so
    /// the Wpf.Ui Mica-window-before-pump rendering gotcha does not apply.</summary>
    private async System.Threading.Tasks.Task ConfirmCloseAsync()
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Owner = this,
            Title = "Unsaved changes",
            Content = "Save your transcript edits before closing?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
        };
        switch (await dialog.ShowDialogAsync())
        {
            case Wpf.Ui.Controls.MessageBoxResult.Primary:      // Save
                await _vm.SaveEditsAsync(System.Threading.CancellationToken.None);
                if (_vm.HasUnsavedEdits) return;                // save failed - stay open, SaveError shows why
                _closeConfirmed = true;
                Close();
                break;
            case Wpf.Ui.Controls.MessageBoxResult.Secondary:    // Discard
                _vm.CancelEdit();                               // revert; also clears SaveError
                _closeConfirmed = true;
                Close();
                break;
            // MessageBoxResult.None (Cancel / Esc / title-bar close): keep editing - do nothing.
        }
    }
```

Every WPF type here is fully qualified on purpose: `ReadViewWindow.xaml.cs` imports
`CommunityToolkit.Mvvm.Input`, and adding `using System.Windows.Input;` risks an ambiguity the
implementer would then have to debug. The donor does the same for `System.Threading.Tasks.Task`.

- [ ] **Step 4: Confirm the teardown still runs exactly once**

No code change - read `OnClosed` (`:933-961`) and confirm it is untouched. `e.Cancel = true` means
`OnClosed` does NOT run for the cancelled pass; the re-`Close()` from `ConfirmCloseAsync` runs the
whole teardown exactly once (timer stop, glide cancel, five unsubscribes, `_vm.Dispose()`,
`_registry.Unregister`). A guard that ran teardown on the cancelled pass would leave a live window
holding disposed media players.

- [ ] **Step 5: Build and run the App suite**

```
dotnet build src/LocalScribe.App/LocalScribe.App.csproj --nologo
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo
```

Expected: build clean; App 1003/1003 (994 + the 9 from Task 4). `XamlHygieneTests` stays green - no
XAML changed in this task.

- [ ] **Step 6: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ReadViewWindow.xaml.cs
git commit -m "fix(read-view): unsaved-changes close guard ported from Session Details"
```

---

## Task 6: `FrameArrivalWatchdog` - the pure "no frames arrived" state machine

`SilentLegMonitor` is driven off `PeakObserved`, which is emitted inside the frame loop
(`LiveSourcePipeline.EmitPeak`). Zero frames therefore means zero calls, so the dead-leg case is
structurally invisible to it. This task adds the missing detector as a clock-free, thread-unsafe pure
state machine, exactly as `SilentLegMonitor` was extracted; Task 8 wires it.

**Files:**
- Create: `src/LocalScribe.Core/Live/FrameArrivalWatchdog.cs`
- Create: `tests/LocalScribe.Core.Tests/FrameArrivalWatchdogTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `LocalScribe.Core.Live.FrameArrivalWatchdog` with
  `FrameArrivalWatchdog(long graceMs, long startMs)`, `bool Stalled { get; }`,
  `bool OnFrame(long nowMs)`, `bool Tick(long nowMs)`, `bool Reset(long nowMs)`,
  `void ForceStale(long nowMs)`. Task 8 owns the locking and the events.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/FrameArrivalWatchdogTests.cs`:

```csharp
using LocalScribe.Core.Live;

namespace LocalScribe.Core.Tests;

/// <summary>Per-leg frame-arrival watchdog (Tier 1B design 2026-08-05, T1-4a). Tested in isolation
/// for the same reason SilentLegMonitor is: FakeCaptureSource replays every frame SYNCHRONOUSLY
/// inside Start() and FakeClock never advances on its own, so no end-to-end controller test can
/// starve a leg and then observe a timeout. The controller's wiring is thin pass-through onto this
/// class under a lock, covered by SessionControllerCaptureHealthTests.</summary>
public sealed class FrameArrivalWatchdogTests
{
    [Fact]
    public void Does_not_trip_inside_the_grace_window()
    {
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);

        Assert.False(w.Tick(4000));
        Assert.False(w.Tick(8000));       // exactly at the boundary: not yet EXCEEDED
        Assert.False(w.Stalled);
    }

    [Fact]
    public void Trips_exactly_once_past_the_grace_window()
    {
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);

        Assert.True(w.Tick(8001));        // raises
        Assert.True(w.Stalled);
        Assert.False(w.Tick(20_000));     // persistent, never re-raised
        Assert.False(w.Tick(60_000));
    }

    [Fact]
    public void A_frame_resets_the_window_so_a_healthy_leg_never_trips()
    {
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);

        for (long t = 1000; t <= 60_000; t += 1000)
        {
            w.OnFrame(t);
            Assert.False(w.Tick(t));
        }
        Assert.False(w.Stalled);
    }

    [Fact]
    public void A_frame_after_a_stall_clears_it_exactly_once()
    {
        // Notification symmetry, the rule SilentLegMonitor.Reset's comment states: every raised
        // "stalled" must have exactly one matching "recovered", or a banner driven off the pair
        // stays stuck on after the leg comes back.
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);
        Assert.True(w.Tick(9000));

        Assert.True(w.OnFrame(9500));     // clears - exactly once
        Assert.False(w.Stalled);
        Assert.False(w.OnFrame(9600));    // every later frame is unremarkable
    }

    [Fact]
    public void A_frame_while_healthy_reports_no_transition()
    {
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);
        Assert.False(w.OnFrame(100));
    }

    [Fact]
    public void Reset_rearms_the_window_from_now_and_reports_whether_it_was_stalled()
    {
        // Called at every point a fresh leg starts (Resume, unmute, remote re-target, watchdog
        // restart). The return value lets the caller raise the matching "recovered" for a leg that
        // was flagged at the moment it was replaced.
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);
        Assert.True(w.Tick(9000));

        Assert.True(w.Reset(10_000));     // was stalled
        Assert.False(w.Stalled);
        Assert.False(w.Tick(18_000));     // window restarted from 10_000
        Assert.True(w.Tick(18_001));

        Assert.True(w.Reset(20_000));
        Assert.False(w.Reset(20_000));    // second reset: nothing to clear
    }

    [Fact]
    public void ForceStale_makes_the_next_tick_trip_but_never_un_flags_a_leg_already_reported()
    {
        // The fast path: a source that has told us it is dead (ICaptureHealthObservable) should not
        // have to wait out the grace window. ForceStale rewinds the last-frame stamp so the NEXT
        // Tick trips - but it must be inert once the leg is ALREADY flagged. Reset() cannot be used
        // for this: Reset CLEARS _stalled, so an already-reported leg would be silently un-flagged,
        // re-reported and re-marked a second time, and its matching CaptureRecovered swallowed.
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);
        w.OnFrame(10_000);

        w.ForceStale(10_000);
        Assert.False(w.Stalled);          // ForceStale itself raises nothing - Tick decides
        Assert.True(w.Tick(10_001));      // no grace left to wait out
        Assert.True(w.Stalled);

        w.ForceStale(20_000);             // already flagged and already reported
        Assert.True(w.Stalled);           // NOT un-flagged
        Assert.False(w.Tick(20_001));     // and NOT re-raised
    }

    [Fact]
    public void A_clock_that_appears_to_move_backwards_never_trips_it()
    {
        // The session clock is monotonic (StopwatchClock/QPC), but the watchdog is also driven from
        // a UI DispatcherTimer reading it across threads. A negative delta must read as "a frame
        // arrived very recently", never as a huge positive gap.
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 30_000);

        Assert.False(w.Tick(1000));
        Assert.False(w.Stalled);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~FrameArrivalWatchdogTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL to build - `CS0246: The type or namespace name 'FrameArrivalWatchdog' could not be found`.

- [ ] **Step 3: Create the class**

Create `src/LocalScribe.Core/Live/FrameArrivalWatchdog.cs`:

```csharp
namespace LocalScribe.Core.Live;

/// <summary>Pure state machine behind the per-leg frame-arrival watchdog (Tier 1B design
/// 2026-08-05, T1-4a): detects a capture leg that has stopped producing ANY frames at all.
///
/// THE HOLE IT FILLS: SilentLegMonitor detects sustained NO SPEECH, but it is driven from
/// PeakObserved, which LiveSourcePipeline emits from inside the frame loop - one call per arriving
/// frame. Zero frames therefore means zero calls, so a WASAPI stream that dies mid-session (device
/// unplugged, endpoint invalidated, driver reset) is structurally invisible to it: the leg simply
/// stops calling OnData, AlignedAudioWriter silence-fills the gap on the next frame that never
/// comes, and PadToMs makes the file look the right length at Stop. Nothing anywhere says the
/// microphone died forty minutes ago.
///
/// Extracted rather than inlined for the reason SilentLegMonitor records at its own :11-19:
/// FakeCaptureSource replays every frame SYNCHRONOUSLY inside Start() and FakeClock never advances
/// by itself, so an end-to-end controller test can neither starve a leg nor time one out. This class
/// is unit-tested directly; SessionController owns all threading and does the locking (frames arrive
/// on the capture thread, Tick comes from the App's 150 ms DispatcherTimer). NOT thread-safe on its
/// own - by contract, exactly like SilentLegMonitor.</summary>
public sealed class FrameArrivalWatchdog
{
    private readonly long _graceMs;
    private long _lastFrameMs;
    private bool _stalled;

    /// <param name="graceMs">How long a leg may produce NO frames before it is called stalled.</param>
    /// <param name="startMs">Seeded to the session clock at leg start, BEFORE the first frame - so a
    /// leg that never produces a single frame still measures from a real timestamp, not from 0.</param>
    public FrameArrivalWatchdog(long graceMs, long startMs)
    {
        _graceMs = graceMs;
        _lastFrameMs = startMs;
    }

    /// <summary>True while this leg is currently flagged as stalled.</summary>
    public bool Stalled => _stalled;

    /// <summary>Call for every frame observed on this leg while Recording. Returns true EXACTLY
    /// once - when this frame clears a raised stall - so the caller can report the recovery; false
    /// on every ordinary frame.</summary>
    public bool OnFrame(long nowMs)
    {
        _lastFrameMs = nowMs;
        if (!_stalled) return false;
        _stalled = false;
        return true;
    }

    /// <summary>External tick (the App's existing 150 ms DispatcherTimer, via
    /// SessionController.PollCaptureHealth - never a Timer inside Core, per the CallActivityWatcher
    /// rule). Returns true EXACTLY once, the first tick on which the grace window has been exceeded
    /// with no frame since; false forever after while still stalled, so the caller reports once and
    /// attempts one restart rather than hammering.</summary>
    public bool Tick(long nowMs)
    {
        if (_stalled) return false;
        if (nowMs - _lastFrameMs <= _graceMs) return false;   // a negative delta is <= grace: never trips
        _stalled = true;
        return true;
    }

    /// <summary>Re-arms from now and drops any flag - called wherever a FRESH leg starts. Every one
    /// of those sites is wired by Task 8: StartAsync's seed, ResumeAsync, SetLocalMuteAsync's UNMUTE
    /// branch, SetRemoteCaptureAsync's live re-target, and the watchdog's own restart. Returns
    /// whether the leg was flagged at reset time, so the caller can raise the matching "recovered"
    /// notification: every stall report must have exactly one clear, or a banner driven off the pair
    /// stays stuck showing a dead leg that has already been replaced (the SilentLegMonitor.Reset
    /// rule).</summary>
    public bool Reset(long nowMs)
    {
        bool wasStalled = _stalled;
        _lastFrameMs = nowMs;
        _stalled = false;
        return wasStalled;
    }

    /// <summary>Collapses the remaining grace so the NEXT Tick trips - for a source that has
    /// self-reported its death (ICaptureHealthObservable), where there is nothing left to wait for.
    /// Raises nothing itself: Tick stays the single place a stall is DECIDED, so the report and the
    /// restart keep running on the controller's tick under its own lock.
    ///
    /// Inert while already stalled, and that is the whole point. REJECTED: reusing
    /// Reset(now - grace - 1), which was this plan's first draft - Reset CLEARS _stalled, so a leg
    /// already flagged and already reported would be silently un-flagged, then reported and MARKED a
    /// SECOND time on the next tick, and its matching CaptureRecovered would be swallowed. A
    /// duplicate "audio device changed" marker in an evidentiary transcript is a false record of a
    /// second outage that never happened.</summary>
    public void ForceStale(long nowMs)
    {
        if (_stalled) return;
        long rewound = nowMs - _graceMs - 1;
        if (rewound < _lastFrameMs) _lastFrameMs = rewound;
    }
}
```

- [ ] **Step 4: Run them and confirm they pass**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~FrameArrivalWatchdogTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, 8/8.

- [ ] **Step 5: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Live/FrameArrivalWatchdog.cs tests/LocalScribe.Core.Tests/FrameArrivalWatchdogTests.cs
git commit -m "feat(capture): pure per-leg frame-arrival watchdog state machine"
```

---

## Task 7: `ICaptureHealthObservable` - the fast death signal

`MicCaptureSource` subscribes only `DataAvailable`. NAudio's `WasapiCapture` raises
`RecordingStopped` with an `Exception` when the device is lost, and that signal is discarded
everywhere in the repo (zero `RecordingStopped` hits across `src` and `tests` outside unrelated
call-detect methods). Subscribing it turns a device death into an immediate report instead of an
8-second wait for the watchdog.

**Files:**
- Create: `src/LocalScribe.Core/Audio/ICaptureHealthObservable.cs`
- Modify: `src/LocalScribe.Core/Audio/MicCaptureSource.cs:8` (interface list), `:19` (beside
  `FrameAvailable`), `:70` (`_capture.DataAvailable += OnData;`, the last line of the private ctor's
  format block at `:53-81`), `:90` (`OnData`, the handler goes beside it), `:134-141` (`Dispose`).
  All five re-measured against HEAD 2026-08-05.
- Modify: `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs` (add `ManualCaptureSource` at the end)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `LocalScribe.Core.Audio.ICaptureHealthObservable` with `event Action<Exception?>? CaptureStopped;`
  - `LocalScribe.Core.Tests.ManualCaptureSource` (test double) with
    `ManualCaptureSource(SourceKind source)`, `void Emit(long startMs, int samples = 512)`,
    `void RaiseStopped(Exception? ex)`, `int StartCount`, `int StopCount`, `bool Disposed`. Task 8
    and Task 9 both use it.

- [ ] **Step 1: Write the failing test**

Append to `tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task A_manual_source_can_emit_frames_after_StartLeg_returns()
    {
        // Guards the new double itself (Tier 1B design 2026-08-05, T1-4). FakeCaptureSource replays
        // everything synchronously inside Start() and can never emit again, which is precisely why
        // no existing test can express "frames stopped arriving". If this ever regresses, every
        // capture-health test silently becomes vacuous.
        var (worker, _, loop, cts) = StartWorker();
        long written = 0;
        var sink = new DelegateSink(mem => written += mem.Length);
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, new AlignedAudioWriter(sink));

        var source = new ManualCaptureSource(SourceKind.Local);
        pipeline.StartLeg(source, cts.Token, cts.Token);
        Assert.Equal(1, source.StartCount);
        Assert.Equal(0, written);                                  // nothing emitted yet

        source.Emit(startMs: 0);
        source.Emit(startMs: 32);
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;

        Assert.True(written >= 1024);                              // both frames reached the writer
        Assert.Equal(1, source.StopCount);
        Assert.True(source.Disposed);
    }
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~A_manual_source_can_emit_frames_after_StartLeg_returns" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL to build - `CS0246: The type or namespace name 'ManualCaptureSource' could not be found`.

- [ ] **Step 3: Create the capability interface**

Create `src/LocalScribe.Core/Audio/ICaptureHealthObservable.cs`:

```csharp
// src/LocalScribe.Core/Audio/ICaptureHealthObservable.cs
namespace LocalScribe.Core.Audio;

/// <summary>Optional capability of a capture source that can report its own death (Tier 1B design
/// 2026-08-05, T1-4a). A SEPARATE interface probed with a type test, not a new member on
/// ICaptureSource: that interface has four implementations plus four test wrappers and widening it
/// would touch all of them - the same reason IEndpointMuteObservable exists and is probed as
/// `if (micSource is not IEndpointMuteObservable m) return;` (SessionController.cs:361).
///
/// The frame-arrival watchdog is the BACKSTOP and works for every source; this is the FAST path for
/// the one source that can actually tell us. Events may fire on arbitrary (WASAPI callback)
/// threads; consumers marshal - the same contract as FrameAvailable and DeviceMuteChanged.</summary>
public interface ICaptureHealthObservable
{
    /// <summary>Raised when the underlying capture stream has stopped on its own. The argument is
    /// the driver-supplied exception when there was one, and null for an ordinary stop (which every
    /// consumer must therefore ignore while it is deliberately stopping a leg).</summary>
    event Action<Exception?>? CaptureStopped;
}
```

- [ ] **Step 4: Subscribe `RecordingStopped` in `MicCaptureSource`**

In `src/LocalScribe.Core/Audio/MicCaptureSource.cs`, extend the interface list at `:8`:

```csharp
public sealed class MicCaptureSource : ICaptureSource, IEndpointMuteObservable, ICaptureHealthObservable
```

Add the event immediately after `FrameAvailable` (`:19`):

```csharp
    /// <summary>Tier 1B (2026-08-05, T1-4a): NAudio raises WasapiCapture.RecordingStopped with an
    /// Exception when the endpoint is lost (device unplugged, driver reset, session invalidated).
    /// Before this, that signal was discarded everywhere in the repo and a dead mic was completely
    /// silent - OnData simply stopped being called, AlignedAudioWriter silence-filled the gap, and
    /// PadToMs made the file look the right length at Stop.</summary>
    public event Action<Exception?>? CaptureStopped;
```

Add the subscription in the private constructor (`:53-81`) immediately after the existing
`_capture.DataAvailable += OnData;` line (`:70`) and BEFORE the `// Fail-open:` comment block that
follows it:

```csharp
        _capture.RecordingStopped += OnRecordingStopped;
```

Add the handler immediately after `OnData` (`:90-114`):

```csharp
    // Fail-open, like every other handler in this file: a throwing subscriber must never take down
    // the capture callback thread. NAudio raises this for an ORDINARY Stop() too (with a null
    // Exception), so the argument is forwarded as-is and the consumer decides - SessionController
    // ignores it while it is deliberately tearing a leg down.
    private void OnRecordingStopped(object? _, StoppedEventArgs e)
    {
        try { CaptureStopped?.Invoke(e.Exception); } catch { }
    }
```

In `Dispose` (`:134-141`), insert the unhook immediately AFTER the existing
`DeviceMuteChanged = null;` line (`:137`) and BEFORE `_capture.DataAvailable -= OnData;` (`:138`):

```csharp
        _capture.RecordingStopped -= OnRecordingStopped;
        CaptureStopped = null;
```

Not "first line": `Dispose`'s actual first statement is the endpoint-volume unhook
(`try { _device.AudioEndpointVolume.OnVolumeNotification -= OnEndpointVolume; } catch { }`, `:136`),
so the two unhooks sit together in the same detach-the-events block, mirroring the pair above them.
Both unhooks must precede `_capture.Dispose()` (`:139`).

`StoppedEventArgs` comes from `NAudio.Wave`, which this file already imports.

- [ ] **Step 5: Add the `ManualCaptureSource` double**

Append to `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs`, at the end of the file (same namespace,
beside the other doubles):

```csharp
/// <summary>The only capture double that can emit a frame AFTER StartLeg returns, and the only one
/// that can die on demand (Tier 1B design 2026-08-05, T1-4). FakeCaptureSource - which lives in
/// src/LocalScribe.Core/Audio and is depended on by CapturePipelineTests, CaptureFrameBridgeTests,
/// LiveSourcePipelineTests and FakeProvider - replays every preset frame SYNCHRONOUSLY inside
/// Start() and returns, so it can express neither "frames keep arriving" nor "frames stopped". It is
/// deliberately NOT modified; this is a new, additive double.
///
/// Frames carry the caller-supplied startMs so a test drives capture time explicitly, exactly as it
/// drives FakeClock.ElapsedMs - no wall-clock dependence anywhere.</summary>
internal sealed class ManualCaptureSource(SourceKind source) : ICaptureSource, ICaptureHealthObservable
{
    public SourceKind Source => source;
    public event Action<AudioFrame>? FrameAvailable;
    public event Action<Exception?>? CaptureStopped;

    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Emit one frame of silence stamped at <paramref name="startMs"/>. 512 samples is the
    /// frame size every other double in this file uses (32 ms at 16 kHz).</summary>
    public void Emit(long startMs, int samples = 512)
        => FrameAvailable?.Invoke(new AudioFrame(source, startMs, new float[samples]));

    /// <summary>Simulate NAudio's RecordingStopped: pass an exception for a device loss, null for
    /// an ordinary stop.</summary>
    public void RaiseStopped(Exception? ex) => CaptureStopped?.Invoke(ex);

    public void Start() => StartCount++;
    public void Stop() => StopCount++;
    public void Dispose() => Disposed = true;
}
```

- [ ] **Step 6: Run the pipeline tests and confirm they pass**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~LiveSourcePipelineTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS - the existing tests plus the new one.

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Audio/ICaptureHealthObservable.cs src/LocalScribe.Core/Audio/MicCaptureSource.cs tests/LocalScribe.Core.Tests/LiveTestDoubles.cs tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs
git commit -m "feat(capture): surface WasapiCapture.RecordingStopped via ICaptureHealthObservable"
```

---

## Task 8: Wire the watchdog into `SessionController` - detect, mark, restart

**Files:**
- Modify: `src/LocalScribe.Core/Live/SessionController.cs` - `LiveSessionOptions` (`:15-47`), the
  `Session` class (`:94-151`), the events block (`:246-258`), `StartAsync` leg wiring (`:569-636`),
  `ResumeAsync`'s monitor reset (`:766-775`), `SetLocalMuteAsync`'s UNMUTE branch (`:889-917`),
  `SetRemoteCaptureAsync`'s fresh-leg reseed (`:1013-1019`)
- Modify: `src/LocalScribe.App/CompositionRoot.cs:85-89` (pass the log into `SessionController`)
- Modify: `src/LocalScribe.App/ViewModels/SessionViewModel.cs` (`TimerTick`)
- Test: `tests/LocalScribe.Core.Tests/SessionControllerCaptureHealthTests.cs` (create)

`StopAsync`'s teardown (`:1143-1153`) is deliberately NOT in this list: the watchdogs live on the
`Session`, which `StopAsync` drops wholesale when it nulls `_session`, so there is nothing to tear
down there and no step in this plan touches it. (An earlier draft listed it; verified against HEAD -
that teardown block disposes audio writers and the capture CTS only.)

**Interfaces:**
- Consumes: `FrameArrivalWatchdog(long graceMs, long startMs)` / `.OnFrame` / `.Tick` / `.Reset` /
  `.Stalled` (Task 6); `ICaptureHealthObservable.CaptureStopped` (Task 7);
  `Markers.AudioDeviceChanged` - **already declared** at `Markers.cs:9` and written by no production
  code anywhere; this task is its first writer; `IDiagnosticLog` (Plan A);
  `ICaptureSourceProvider.CreateMic(IClock)` / `.CreateRemote(IClock)` / `.CreateRemote(IClock, RemoteSetting)`;
  `LiveSourcePipeline.StartLeg(ICaptureSource, CancellationToken captureCt, CancellationToken feedCt)`
  and `.StopLegAndFlushAsync()`.
- Produces: `SessionController.PollCaptureHealth() : void`;
  `SessionController.PendingCaptureRestart : Task`;
  `event Action<SourceKind>? CaptureStalled` / `CaptureRecovered`;
  `LiveSessionOptions.CaptureStallGraceMs : int` and `.CaptureRestartLimit : int`. Task 10 extends
  `PollCaptureHealth`'s body; Task 12 binds `CaptureStalled`/`CaptureRecovered` to a persistent
  `SessionViewModel` banner pair (`MicCaptureDead`/`RemoteCaptureDead`), which is what the
  `Raise*ForTest` hooks below exist to drive.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.Core.Tests/SessionControllerCaptureHealthTests.cs`:

```csharp
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
    /// Start(), but LiveSourcePipeline's _audioLoop DRAINS them on a POOL THREAD
    /// (LiveSourcePipeline.cs:63-80) and every drained frame calls EmitPeak -> PeakObserved ->
    /// OnFrameForWatchdog(clock.ElapsedMs). So `clock.ElapsedMs = 20_000; c.PollCaptureHealth();`
    /// issued straight after StartAsync returns is a RACE: if the drain has not finished, those
    /// frames stamp OnFrame(20_000) and the watchdog either never trips or is cleared the instant
    /// after it did - the "passes alone, fails under full-suite load" family this repo has already
    /// paid for five times. Wait for the frames to be OBSERVED, not for a duration.
    ///
    /// FakeProvider gives each leg SpeechThenSilence(4, 3) = 7 frames, so a started session emits
    /// 14 peaks in total (7 local + 7 remote). SpinWait.SpinUntil is the house idiom
    /// (MaintenanceServiceTests.cs:84-108).</summary>
    private static void AwaitFramesDrained(SessionController c, ref int peaks, int expected)
        => Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref peaks) >= expected,
            TimeSpan.FromSeconds(5)),
            $"capture frames never drained: saw {Volatile.Read(ref peaks)} of {expected} peaks");

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
        AwaitFramesDrained(c, ref peaks, 14);

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
        AwaitFramesDrained(c, ref peaks, 14);

        clock.ElapsedMs = 20_000;
        c.PollCaptureHealth();
        await c.PendingCaptureRestart.WaitAsync(TimeSpan.FromSeconds(10));
        int afterFirst = Volatile.Read(ref raised);

        // The restarted legs re-seeded the watchdogs at 20_000 and their fresh fake sources emit 7
        // more frames each - gate on those too, then tick INSIDE the fresh grace window, which must
        // add nothing.
        AwaitFramesDrained(c, ref peaks, 28);
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
        AwaitFramesDrained(c, ref peaks, 14);

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
        AwaitFramesDrained(c, ref peaks, 14);

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
        AwaitFramesDrained(c, ref peaks, 14);

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
        AwaitFramesDrained(c, ref peaks, 14);
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
        Assert.Single(lines.Where(l => l.Kind == TranscriptKind.Marker
            && l.Text == Markers.AudioDeviceChanged));                   // remote only
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionControllerCaptureHealthTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL to build - `CS0117: 'LiveSessionOptions' does not contain a definition for 'CaptureStallGraceMs'`
and `CS1061: 'SessionController' does not contain a definition for 'PollCaptureHealth'`.

- [ ] **Step 3: Add the option knobs and the terminal marker**

First, append to the end of the `Markers` class in `src/LocalScribe.Core/Model/Markers.cs`:

```csharp

    // Capture abandoned after the restart budget (Tier 1B design 2026-08-05, T1-4a).
    // {0} = "microphone" | "remote", {1} = the attempt count. Written ONCE per leg, and only after
    // CaptureRestartLimit rebuilds have each been followed by silence. Distinct from
    // AudioDeviceChanged, which says "this leg died and we are reconnecting it": this one says we
    // have stopped trying, which is the fact a reader months later actually needs - the tail of the
    // recording has no audio from that side, and AlignedAudioWriter.PadToMs will have silence-filled
    // the file to full length so nothing else on disk says so.
    public const string CaptureNotRecovered =
        "capture did not come back for the {0} stream after {1} reconnection attempts - "
        + "the remainder of this session has no {0} audio";
```

Then, in `src/LocalScribe.Core/Live/SessionController.cs`, append to `LiveSessionOptions` after
`SilentLegGraceMs` (`:46`):

```csharp
    /// <summary>Tier 1B (2026-08-05, T1-4a): how long a leg may produce NO FRAMES AT ALL before it
    /// is declared dead, marked and restarted. Distinct from SilentLegGraceMs, which is about
    /// sustained no SPEECH and cannot fire at all when frames stop (it is driven from PeakObserved,
    /// i.e. from inside the frame loop).
    ///
    /// 8000 ms chosen against the ONE competing recovery already in the system: ProcessLoopbackCapture
    /// runs its own pump-thread recovery loop that drops the client, backs off up to 1 s per iteration
    /// and re-activates - and Option B re-activation probes four candidate formats on freshly
    /// activated clients. An outer restart racing that internal recovery would tear down a leg that
    /// was about to heal itself, so the grace sits several times above its worst case. It also stays
    /// comfortably BELOW SilentLegGraceMs (15 s) so the specific diagnosis ("the device died") is
    /// reported before the vague one ("no speech detected") - and the vague one cannot fire anyway
    /// while frames are absent. REJECTED: 3 s, which reliably fights ProcessLoopbackCapture's own
    /// recovery on a busy machine.</summary>
    public int CaptureStallGraceMs { get; init; } = 8000;

    /// <summary>Tier 1B (2026-08-05, T1-4a): how many times ONE leg may be rebuilt in a session
    /// before the watchdog gives up on it. The restart re-arms the watchdog on success, so a leg
    /// whose source rebuilds cleanly but still delivers no frames - a wedged driver, an invalidated
    /// endpoint, a per-process target whose render session has gone - would otherwise re-trip every
    /// CaptureStallGraceMs FOREVER: a fresh Markers.AudioDeviceChanged and a fresh tray Notice every
    /// 8 s, so a 40-minute call interleaves ~300 identical markers with the evidence and the
    /// transcript becomes a log file.
    ///
    /// 3 attempts, spaced BY CONSTRUCTION rather than by a timer: a restart re-arms the leg's
    /// watchdog, so Tick cannot trip again until a further CaptureStallGraceMs of silence has
    /// passed. Consecutive attempts are therefore always >= 8 s apart and the whole budget is
    /// spent inside about half a minute - enough to ride out a device re-enumeration or a driver
    /// reset, and it gives up rather than nagging for the rest of a call. On exhaustion the leg is
    /// left flagged and ONE terminal marker/Notice is written.
    /// REJECTED: an explicit exponential "not before" instant (grace, 2x, 4x), which was this
    /// plan's first draft - see Step 4. It STRANDS the leg: the backoff instant is computed from
    /// the failed attempt, so a leg that recovers on its own between attempts still sits out the
    /// remaining window, and the structural spacing above already delivers the same cadence with
    /// no extra state to get wrong.
    /// REJECTED: unlimited retries with a de-duplicated marker - the tray Notice would still fire
    /// every 8 s, and a leg being rebuilt every 8 s for 40 minutes is not "recovering", it is
    /// thrashing a dead device.</summary>
    public int CaptureRestartLimit { get; init; } = 3;
```

- [ ] **Step 4: Add the controller state, events and tick**

First give `SessionController` the diagnostic sink every later step in this task and Task 9/10 uses.
Add `using LocalScribe.Core.Diagnostics;` to the file's using block, a field beside `_availableModels`
(`:69`), and a trailing optional parameter to BOTH constructors (`:273-292`) - the second one
forwards to the first, so both need it and neither existing call site changes:

```csharp
    // Tier 1B (2026-08-05): optional so CompositionRoot's construction site, LiveTestDoubles.
    // MakeController and every existing SessionController test keep compiling untouched. Null = no
    // diagnostics, never a null-ref: every use is `_log?.Write(...)`.
    private readonly IDiagnosticLog? _log;
```

```csharp
    public SessionController(StoragePaths paths, Func<Settings> settingsProvider,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion, Func<IReadOnlySet<string>>? availableModels = null,
        IDiagnosticLog? log = null)
        => (_paths, _settingsProvider, _engineFactory, _vadModelFactory, _hardware, _captureProvider,
            _clockFactory, _time, _appVersion, _availableModels, _log)
         = (paths, settingsProvider, engineFactory, vadModelFactory, hardware, captureProvider,
            clockFactory, time, appVersion, availableModels ?? ModelPaths.AvailableModels, log);

    /// <summary>Convenience overload: a fixed Settings snapshot. Keeps every pre-Stage-4 call
    /// site and test compiling unchanged; production passes a live provider (design 6.2) so
    /// per-session inputs resolve at StartAsync, not at construction.</summary>
    public SessionController(StoragePaths paths, Settings settings, IEngineFactory engineFactory,
        Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware,
        ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion, Func<IReadOnlySet<string>>? availableModels = null,
        IDiagnosticLog? log = null)
        : this(paths, () => settings, engineFactory, vadModelFactory, hardware, captureProvider,
            clockFactory, time, appVersion, availableModels, log)
    {
    }
```

Then in `src/LocalScribe.App/CompositionRoot.cs:85-89`, pass the log. This is the SECOND of the two
sites that sit inside `Build()` (Task 1 did the `MaintenanceService` at `:92`), so the same rule from
Global Constraints applies: read `CompositionRoot.cs:175-178` and use the identifier Plan A passes in
the `AppComposition` record's `Log` argument position - spelled `log` below - and NEVER invent a
second instance. Outside `Build()` the log is always `comp.Log`:

```csharp
        // Tier 1B (2026-08-05, T1-4): the SAME instance that becomes AppComposition.Log (shared
        // contract section 3a) and that Task 1 already handed to MaintenanceService at :92 - one
        // process-wide sink, one diag-yyyyMM.jsonl, one single-writer drain. REJECTED: a
        // Core-private log for the controller - two writers appending to one file is the
        // interleaved-line corruption the single-writer drain exists to prevent.
        var controller = new SessionController(paths, current, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(),
            new WasapiCaptureSourceProvider(current, scanner, deviceEnumerator),
            () => new StopwatchClock(), TimeProvider.System, appVersion,
            availableModels: null, log: log);
```

Now add to the private `Session` class, beside `LocalSilentMonitor`/`RemoteSilentMonitor` (`:117-119`):

```csharp
        // Tier 1B (2026-08-05, T1-4a): per-leg frame-arrival watchdogs, seeded at leg start and
        // re-seeded wherever a fresh leg starts (Resume, unmute, remote re-target, watchdog restart).
        public required FrameArrivalWatchdog LocalFrameWatchdog;
        public required FrameArrivalWatchdog RemoteFrameWatchdog;

        // Tier 1B (2026-08-05, T1-4a): per-leg restart budget - attempts consumed so far. Touched
        // only from PollCaptureHealth (the App's UI tick) and from the serialized restart chain,
        // never from a capture thread.
        //
        // NO separate backoff timer, deliberately. The spacing is already structural: a restart
        // re-arms that leg's watchdog, and FrameArrivalWatchdog.Tick cannot trip again until
        // CaptureStallGraceMs has passed with no frame - so consecutive attempts are >= 8 s apart
        // by construction, and the whole budget is spent inside about half a minute.
        // REJECTED: an explicit exponential "not before" instant, which was this plan's first draft.
        // It STRANDS the leg: Tick raises exactly ONCE per stall, so a poll that finds the leg
        // flagged but inside the backoff window would decline the restart and then never see
        // another trip - no further attempt, no budget exhaustion, and therefore no terminal marker
        // either. A leg silently abandoned with nothing in the transcript is the precise failure
        // this feature exists to prevent.
        public int LocalRestarts, RemoteRestarts;
        // Set once per leg when the budget is spent, so the terminal marker/Notice is written ONCE.
        public bool LocalCaptureAbandoned, RemoteCaptureAbandoned;
```

Add beside `_silentGate` (`:263`):

```csharp
    // Guards FrameArrivalWatchdog access: OnFrame fires on the capture thread, Tick comes from the
    // App's DispatcherTimer via PollCaptureHealth, Reset happens under _gate. Separate from
    // _silentGate so a per-frame watchdog update never contends with the silent-leg state machine.
    private readonly object _healthGate = new();

    // Tier 1B (2026-08-05, T1-4a): the in-flight watchdog restart, exposed the way PendingFinalize
    // is (and SettingsPageViewModel.LastSave, SessionsPageViewModel.ContentFilterTask). Production
    // fires-and-forgets; tests await it so no restart is in flight when they assert. Task.CompletedTask
    // when none is running. A PROPERTY over a reassigned field - always re-read, never cache.
    private Task _captureRestart = Task.CompletedTask;
    public Task PendingCaptureRestart => _captureRestart;
```

Add beside `SilentLegDetected`/`SilentLegCleared` (`:249-250`):

```csharp
    // Tier 1B (2026-08-05, T1-4a): a leg produced NO FRAMES for CaptureStallGraceMs. Raised once
    // per stall (not per tick); CaptureRecovered follows when frames resume, so a banner driven off
    // the pair can never stick on. Distinct from SilentLegDetected, which means "frames but no
    // speech" and is structurally incapable of firing when frames stop.
    public event Action<SourceKind>? CaptureStalled;
    public event Action<SourceKind>? CaptureRecovered;

    // Same rationale as RaiseSilentLegDetectedForTest above: field-like events are invocable only
    // inside the declaring class and there is no InternalsVisibleTo between Core and the test
    // assemblies, so an App.Tests VM test needs a public hook. Production code never calls these.
    public void RaiseCaptureStalledForTest(SourceKind kind) => CaptureStalled?.Invoke(kind);
    public void RaiseCaptureRecoveredForTest(SourceKind kind) => CaptureRecovered?.Invoke(kind);
```

Add the tick as a public method beside `CheckSilentLeg` (`:339`). Its per-frame companion
`OnFrameForWatchdog` is defined ONCE, in Step 6, where the two watchdog locals it takes are in
scope - do not write a `Session`-taking variant here:

```csharp
    /// <summary>External health tick (Tier 1B design 2026-08-05, T1-4). Driven by the App's existing
    /// 150 ms DispatcherTimer via SessionViewModel.TimerTick - never a Timer inside Core, and never a
    /// self-timing Task.Delay loop: the house rule (CallActivityWatcher.cs:17) is that a Core watcher
    /// is polled externally so tests can call it directly, and this repo has no fake-timer package.
    /// Idempotent, allocation-free on the healthy path, and safe to call while Idle.</summary>
    public void PollCaptureHealth()
    {
        var s = _session;
        if (s is null || State != SessionState.Recording) return;   // Paused stops both legs on purpose
        long now = s.Clock.ElapsedMs;

        var stalled = new List<SourceKind>();
        lock (_healthGate)
        {
            // A deliberately muted local leg is STOPPED, not dead: "Mute my side" leaves it stopped
            // and even Resume honours that, so restarting it here would silently un-mute a user who
            // muted for a privileged aside. Keep its window re-armed so it cannot fire the instant
            // the user unmutes either.
            if (s.LocalMuted) s.LocalFrameWatchdog.Reset(now);
            else if (s.LocalFrameWatchdog.Tick(now)) stalled.Add(SourceKind.Local);
            if (s.RemoteFrameWatchdog.Tick(now)) stalled.Add(SourceKind.Remote);
        }
        if (stalled.Count == 0) return;

        // The stall is ALWAYS reported and ALWAYS raised - the leg really did die. What the budget
        // gates is whether we try to rebuild it AGAIN, and (because a marker only earns its place
        // when it records something new) whether it earns another marker.
        var toRestart = new List<SourceKind>();
        foreach (var kind in stalled)
        {
            bool local = kind == SourceKind.Local;
            int used = local ? s.LocalRestarts : s.RemoteRestarts;
            if (used < s.RestartLimit)
            {
                // Marked BEFORE the restart is attempted: the loss of audio is a fact regardless of
                // whether recovery succeeds, and a marker written only on success would omit exactly
                // the worst case. Markers.AudioDeviceChanged has been DECLARED since Stage 2b with
                // no writer anywhere in the product - this is its first one.
                s.Outbox.Writer.TryWrite(new MarkerAt(Markers.AudioDeviceChanged, now));
                _log?.Write("warn", "capture", "Capture leg stalled - no frames arrived",
                    $"leg={kind} atMs={now} graceMs={s.StallGraceMs} attempt={used + 1}/{s.RestartLimit}");
                CaptureStalled?.Invoke(kind);
                Notice?.Invoke(local
                    ? "The microphone stopped producing audio - reconnecting it. Check the device if this repeats."
                    : "The meeting/system audio stream stopped - reconnecting it. Check that audio is still playing.");
                toRestart.Add(kind);
                continue;
            }

            // Budget spent. ONE terminal marker and ONE notice for the rest of the session: a leg
            // that rebuilds cleanly and still delivers nothing is a dead device, and re-marking it
            // every CaptureStallGraceMs would bury the evidence under ~300 identical lines on a
            // 40-minute call. The leg is left FLAGGED (the watchdog is not re-armed), so it stops
            // re-raising, and the banner Task 12 binds to CaptureStalled stays on - the honest
            // surface for "this is still dead".
            if (local ? s.LocalCaptureAbandoned : s.RemoteCaptureAbandoned) continue;
            if (local) s.LocalCaptureAbandoned = true; else s.RemoteCaptureAbandoned = true;
            string leg = local ? "microphone" : "remote";
            s.Outbox.Writer.TryWrite(new MarkerAt(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                Markers.CaptureNotRecovered, leg, s.RestartLimit), now));
            _log?.Write("error", "capture", "Capture leg abandoned - restart budget exhausted",
                $"leg={kind} atMs={now} attempts={s.RestartLimit}");
            CaptureStalled?.Invoke(kind);
            Notice?.Invoke(local
                ? "The microphone did not come back after several attempts - the rest of this recording has no microphone audio. Check the device, then stop and start a new recording."
                : "The meeting/system audio did not come back after several attempts - the rest of this recording has no remote audio. Check that audio is playing, then stop and start a new recording.");
        }
        if (toRestart.Count == 0) return;

        // Fire-and-forget onto the awaitable seam: PollCaptureHealth runs on the UI dispatcher and
        // the restart needs _gate, which the controller's own public methods hold. Chained rather
        // than replaced so a second stall cannot start a restart while the first is mid-teardown.
        var previous = _captureRestart;
        _captureRestart = RestartLegsAsync(previous, toRestart);
    }
```

Add two more `required` members to the `Session` class beside the watchdogs, both assigned in
`StartAsync`'s `Session` initializer (Step 6): `public required int StallGraceMs;` from
`options.CaptureStallGraceMs` (read only by the log lines above), and
`public required int RestartLimit;` from `options.CaptureRestartLimit`.

Note the ordering the counters rely on: `RestartLegsAsync` increments `LocalRestarts`/`RemoteRestarts`
on the restart chain, and `PollCaptureHealth` reads them on the UI tick. They cannot interleave
harmfully - a leg's watchdog is re-armed only at the END of a successful restart, so the next Tick
that could trip it is at least `CaptureStallGraceMs` after the increment. Plain `int` reads and
writes are atomic, and the worst case of a stale read is one extra attempt, never a lost terminal
marker.

- [ ] **Step 5: Add the restart ladder**

Add to `SessionController.cs`, immediately after `PollCaptureHealth`:

```csharp
    /// <summary>Rebuilds one or more dead legs, copying ResumeAsync's ladder verbatim (Tier 1B
    /// design 2026-08-05, T1-4a). Every rule that ladder encodes applies here too:
    /// - CreateMic/CreateRemote are INERT builds that can genuinely throw; build BEFORE tearing the
    ///   old leg down, so a build failure leaves the (dead but harmless) leg in place and commits
    ///   nothing.
    /// - StopLegAndFlushAsync FIRST or StartLeg throws "leg already running"; it awaits both loops
    ///   so the retry cannot race a stale task against the new bridge/channel.
    /// - NEVER rethrow. There is no caller to revert a picker and no user action to fail - a throw
    ///   from here would surface as an unobserved task fault. Every failure degrades and is
    ///   RECORDED (RemoteCaptureLost) rather than surfacing as an exception.
    /// - AlignedAudioWriter needs no change: the first frame from the restarted leg carries a later
    ///   StartMs, so the whole dead span is silence-filled and the file stays sample-aligned to the
    ///   session clock. The outage is therefore audible as silence AND marked in the transcript.
    /// Serialized on _gate like every other leg operation, and re-checks state after acquiring it -
    /// a Stop may have completed while this was queued.
    ///
    /// The per-leg budget is consumed on ATTEMPT, not on success: a leg whose CreateMic throws every
    /// time is exactly as dead as one that rebuilds and stays silent, and charging only successes
    /// would let the failing-build case retry for the whole call.</summary>
    private async Task RestartLegsAsync(Task previous, IReadOnlyList<SourceKind> kinds)
    {
        try { await previous; } catch { }          // never observe a prior restart's fault here
        foreach (var kind in kinds)
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                var s = _session;
                if (s is null || State != SessionState.Recording) return;   // stopped while queued
                if (kind == SourceKind.Local) { s.LocalRestarts++; await RestartLocalAsync(s); }
                else { s.RemoteRestarts++; await RestartRemoteAsync(s); }
            }
            catch (Exception ex)
            {
                _log?.Write("error", "capture", "Leg restart failed", ex.ToString());
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task RestartLocalAsync(Session s)
    {
        if (s.LocalMuted) return;                  // deliberately stopped - never silently un-mute
        ICaptureSource mic;
        try { (mic, _) = _captureProvider.CreateMic(s.Clock); }
        catch (Exception ex)
        {
            // Inert build failed: nothing was torn down, so the session is unchanged. Report and
            // leave the watchdog stalled - it will not re-raise, so this is reported once.
            _log?.Write("error", "capture", "Microphone rebuild failed", ex.ToString());
            Notice?.Invoke("The microphone could not be reconnected - only the remote side is still being recorded.");
            return;
        }
        try { await s.Local.StopLegAndFlushAsync(); }
        catch (Exception ex)
        {
            // ICaptureSource.Stop() runs BEFORE StopLegAndFlushAsync's try block, so a throwing
            // Stop leaves _legSource non-null and the retry StartLeg would throw "leg already
            // running" - the wedge M1 removed from Resume. Abandon the restart instead of wedging.
            mic.Dispose();
            _log?.Write("error", "capture", "Microphone leg teardown failed - restart abandoned", ex.ToString());
            return;
        }
        try
        {
            s.Local.StartLeg(mic, s.CaptureCts.Token, s.FeedCts.Token);
        }
        catch (Exception ex)
        {
            try { await s.Local.StopLegAndFlushAsync(); } catch { }
            _log?.Write("error", "capture", "Microphone leg failed to start after restart", ex.ToString());
            Notice?.Invoke("The microphone could not be reconnected - only the remote side is still being recorded.");
            return;
        }
        lock (_healthGate) { s.LocalFrameWatchdog.Reset(s.Clock.ElapsedMs); }
        lock (_silentGate) { s.LocalSilentMonitor.Reset(s.Clock.ElapsedMs); }
        CaptureRecovered?.Invoke(SourceKind.Local);
        HookCaptureHealth(mic, s, SourceKind.Local);
        HookDeviceMute(mic, s);                    // a fresh endpoint needs its own mute hook
    }

    private async Task RestartRemoteAsync(Session s)
    {
        ICaptureSource remote;
        try { (remote, _) = _captureProvider.CreateRemote(s.Clock); }
        catch (Exception ex)
        {
            _log?.Write("error", "capture", "Remote rebuild failed", ex.ToString());
            Notice?.Invoke("The meeting/system audio stream could not be reconnected - only your microphone is still being recorded.");
            return;
        }
        try { await s.Remote.StopLegAndFlushAsync(); }
        catch (Exception ex)
        {
            remote.Dispose();
            _log?.Write("error", "capture", "Remote leg teardown failed - restart abandoned", ex.ToString());
            return;
        }
        try
        {
            s.Remote.StartLeg(remote, s.CaptureCts.Token, s.FeedCts.Token);
        }
        catch
        {
            // The same degrade-never-wedge ladder ResumeAsync uses: reset the half-started leg, fall
            // back to full system mix so the counterparty is never silently dropped (LOCKED
            // evidentiary invariant), and if THAT also fails, RECORD the loss.
            try { await s.Remote.StopLegAndFlushAsync(); } catch { }
            try
            {
                var (mixSource, _) = _captureProvider.CreateRemote(s.Clock,
                    new RemoteSetting { Mode = RemoteMode.SystemMix });
                s.Remote.StartLeg(mixSource, s.CaptureCts.Token, s.FeedCts.Token);
                if (!s.RemoteDegraded)
                {
                    s.RemoteDegraded = true;
                    s.Outbox.Writer.TryWrite(new MarkerAt(Markers.DegradedSystemAudioLoopback, s.Clock.ElapsedMs));
                    Notice?.Invoke("Per-process capture unavailable after reconnecting - recording full system audio for the remote stream (possible bleed; use headphones).");
                }
            }
            catch
            {
                try { await s.Remote.StopLegAndFlushAsync(); } catch { }
                s.Outbox.Writer.TryWrite(new MarkerAt(Markers.RemoteCaptureLost, s.Clock.ElapsedMs));
                Notice?.Invoke("Remote capture could not be reconnected - the target and the system-mix fallback both failed to start. Only your microphone is still being recorded.");
                return;
            }
        }
        lock (_healthGate) { s.RemoteFrameWatchdog.Reset(s.Clock.ElapsedMs); }
        lock (_silentGate) { s.RemoteSilentMonitor.Reset(s.Clock.ElapsedMs); }
        CaptureRecovered?.Invoke(SourceKind.Remote);
        HookCaptureHealth(remote, s, SourceKind.Remote);
    }

    /// <summary>Subscribes a source's optional self-reported death (Tier 1B, T1-4a), the fast path
    /// ahead of the watchdog's CaptureStallGraceMs backstop. Probed by type test exactly as
    /// HookDeviceMute probes IEndpointMuteObservable - ICaptureSource must not grow a member.
    /// A null Exception means an ORDINARY stop (NAudio raises RecordingStopped for a deliberate
    /// Stop() too), so it is ignored: every deliberate teardown in this class calls Stop().</summary>
    private void HookCaptureHealth(ICaptureSource source, Session session, SourceKind kind)
    {
        if (source is not ICaptureHealthObservable h) return;
        h.CaptureStopped += ex =>
        {
            if (ex is null) return;                                     // deliberate stop
            if (!ReferenceEquals(_session, session)) return;             // stale leg
            if (State != SessionState.Recording) return;
            _log?.Write("warn", "capture", "Capture source reported it stopped", $"leg={kind} error={ex.Message}");
            // Collapse the remaining grace so the watchdog trips on the very next tick instead of
            // waiting it out: the source has told us it is dead, so there is nothing left to wait
            // for. The DECISION still belongs to Tick under PollCaptureHealth - this only moves the
            // deadline - so the marker, the budget and the restart all keep running in one place.
            // REJECTED: Reset(now - grace - 1), which this plan's first draft used. Reset CLEARS the
            // stalled flag, so a leg already flagged and already marked would be silently un-flagged
            // and then reported and MARKED a SECOND time on the next tick, with its matching
            // CaptureRecovered swallowed - a duplicate "audio device changed" line in an evidentiary
            // transcript is a false record of a second outage that never happened. ForceStale is
            // inert while already stalled, which is exactly the difference.
            lock (_healthGate)
            {
                var w = kind == SourceKind.Local ? session.LocalFrameWatchdog : session.RemoteFrameWatchdog;
                w.ForceStale(session.Clock.ElapsedMs);
            }
        };
    }
```

- [ ] **Step 6: Seed the watchdogs in `StartAsync` and reset them at every fresh leg**

In `StartAsync`, beside the silent monitors (`:582-586`), add:

```csharp
                // Tier 1B (2026-08-05, T1-4a): seeded to leg-start (clock.ElapsedMs now, before
                // either leg's first frame) for the same reason the silent monitors are - a leg
                // that never produces a single frame must still measure from a real timestamp.
                var localFrameWatchdog = new FrameArrivalWatchdog(options.CaptureStallGraceMs, clock.ElapsedMs);
                var remoteFrameWatchdog = new FrameArrivalWatchdog(options.CaptureStallGraceMs, clock.ElapsedMs);
```

In BOTH peak handlers (`:576-589`), add one call - the handlers already capture the monitors as
locals, so the watchdogs are captured the same way. Because `OnFrameForWatchdog` needs the `Session`
(which does not exist yet at this point), capture the watchdogs directly instead:

```csharp
                local.PeakObserved += (src, p) =>
                {
                    PeakObserved?.Invoke(src, p);
                    CheckSilentLeg(src, localSilentMonitor, remoteSilentMonitor, clock.ElapsedMs);
                    FeedStartPeak(src, p, clock.ElapsedMs);
                    // Tier 1B: one more call in the existing per-frame choke point - no new event,
                    // no per-frame allocation.
                    OnFrameForWatchdog(src, localFrameWatchdog, remoteFrameWatchdog, clock.ElapsedMs);
                };
```

and identically in the `remote.PeakObserved` handler. Now define `OnFrameForWatchdog` - this is its
ONLY definition in the plan and in the file - beside `CheckSilentLeg` (`:339`), taking the two
watchdogs rather than the `Session`, mirroring `CheckSilentLeg`'s own signature exactly:

```csharp
    /// <summary>Tier 1B (2026-08-05, T1-4a): records a frame arrival for a leg and reports a
    /// recovery exactly once. Called from the SAME PeakObserved handler CheckSilentLeg uses -
    /// PeakObserved is emitted once per frame inside LiveSourcePipeline's audio loop
    /// (LiveSourcePipeline.EmitPeak), so it is already the per-frame choke point and no new event
    /// and no per-frame allocation is needed. Takes the two watchdogs rather than the Session for
    /// the reason CheckSilentLeg takes the two monitors: these handlers are wired BEFORE the
    /// Session object exists. Mutates under the lock, raises outside it - the CheckSilentLeg
    /// idiom.</summary>
    private void OnFrameForWatchdog(SourceKind kind, FrameArrivalWatchdog local,
        FrameArrivalWatchdog remote, long nowMs)
    {
        var watchdog = kind == SourceKind.Local ? local : remote;
        bool recovered;
        lock (_healthGate) { recovered = watchdog.OnFrame(nowMs); }
        if (recovered) CaptureRecovered?.Invoke(kind);
    }
```

Add the four new members to the `Session` initializer (`:604-616`):

```csharp
                    LocalSilentMonitor = localSilentMonitor, RemoteSilentMonitor = remoteSilentMonitor,
                    LocalFrameWatchdog = localFrameWatchdog, RemoteFrameWatchdog = remoteFrameWatchdog,
                    StallGraceMs = options.CaptureStallGraceMs,
                    RestartLimit = options.CaptureRestartLimit,
```

Immediately after `HookDeviceMute(micSource, _session);` (`:618`), add:

```csharp
                HookCaptureHealth(micSource, _session, SourceKind.Local);
                HookCaptureHealth(remoteSource, _session, SourceKind.Remote);
```

In `ResumeAsync`, inside the existing `lock (_silentGate)` block that resets the silent monitors
(`:767-772`), add a sibling block immediately after it:

```csharp
            // Tier 1B: fresh legs, fresh frame windows - the same reason the silent monitors are
            // reset here. Raised outside the lock, like every other transition in this class.
            bool localWasStalled, remoteWasStalled;
            lock (_healthGate)
            {
                localWasStalled = s.LocalFrameWatchdog.Reset(s.Clock.ElapsedMs);
                remoteWasStalled = s.RemoteFrameWatchdog.Reset(s.Clock.ElapsedMs);
            }
            if (localWasStalled) CaptureRecovered?.Invoke(SourceKind.Local);
            if (remoteWasStalled) CaptureRecovered?.Invoke(SourceKind.Remote);
```

Add `HookCaptureHealth(micSource, s, SourceKind.Local);` beside the existing
`if (micSource is not null) HookDeviceMute(micSource, s);` at the end of `ResumeAsync`.

**The two remaining fresh-leg sites - both must be wired, or `FrameArrivalWatchdog.Reset`'s doc
comment is false and a leg that just started is torn down by the watchdog it never escaped.**

(a) `SetLocalMuteAsync`'s UNMUTE branch (`:889-918`). It builds a brand-new mic leg through
`_captureProvider.CreateMic` (`:903`) and starts it (`:904`); today only the silent monitor is reset.
Insert the watchdog reseed and the health hook between the existing
`if (wasFlagged) SilentLegCleared?.Invoke(SourceKind.Local);` (`:908`) and the
`// Hook AFTER the LocalMuted=false commit above` comment that precedes `HookDeviceMute(micSource, s);`
(`:909-917`) - so both hooks sit together and both are after the `LocalMuted = false` commit that
`HookDeviceMute`'s own comment depends on:

```csharp
                    // Tier 1B (2026-08-05, T1-4a): a fresh leg gets a FRESH window - the same rule
                    // ResumeAsync follows. Without this the new mic inherits a last-frame stamp from
                    // before the mute, so the first PollCaptureHealth after an unmute can declare a
                    // leg that started milliseconds ago dead and tear it down. The restart budget is
                    // reset too: this is a NEW leg by the user's own action, not a retry of a dead
                    // one, so it must not inherit a spent budget.
                    bool localWatchdogWasStalled;
                    lock (_healthGate)
                    {
                        localWatchdogWasStalled = s.LocalFrameWatchdog.Reset(s.Clock.ElapsedMs);
                        s.LocalRestarts = 0;
                        s.LocalCaptureAbandoned = false;
                    }
                    if (localWatchdogWasStalled) CaptureRecovered?.Invoke(SourceKind.Local);
                    HookCaptureHealth(micSource, s, SourceKind.Local);
```

(b) `SetRemoteCaptureAsync`'s fresh-leg block (`:1013-1019`), the one whose comment already reads
"Fresh leg (either the requested target or the system-mix fallback): reseed the silent monitor...".
There are exactly two `s.Remote.StartLeg(...)` calls in this method - `:973` with `newSource` (the
requested target) and `:994` with `mixSource` (the system-mix fallback) - and only one of them is
running by the time control reaches `:1013`. Record which: declare `ICaptureSource started;` beside
the existing `var (newSource, snap) = ...` build, assign `started = newSource;` immediately after
`:973` and `started = mixSource;` immediately after `:994`. Then add, immediately after the existing
`if (wasFlagged) SilentLegCleared?.Invoke(SourceKind.Remote);` (`:1018`):

```csharp
            // Tier 1B (2026-08-05, T1-4a): same fresh-leg rule as the unmute path above. A
            // per-process WASAPI activation can take well over a second on a busy machine, and
            // without this reseed the re-targeted leg is measured from the OLD leg's last frame -
            // so the watchdog can declare it dead and restart it before it has delivered anything.
            bool remoteWatchdogWasStalled;
            lock (_healthGate)
            {
                remoteWatchdogWasStalled = s.RemoteFrameWatchdog.Reset(s.Clock.ElapsedMs);
                s.RemoteRestarts = 0;
                s.RemoteCaptureAbandoned = false;
            }
            if (remoteWatchdogWasStalled) CaptureRecovered?.Invoke(SourceKind.Remote);
            HookCaptureHealth(started, s, SourceKind.Remote);
```

- [ ] **Step 7: Drive the tick from the App's existing timer**

In `src/LocalScribe.App/ViewModels/SessionViewModel.cs`, add one line to `TimerTick()` after
`RefreshEngineChips();`:

```csharp
        // Tier 1B (2026-08-05, T1-4): capture health rides the SAME 150 ms tick that already drives
        // Elapsed, the level decay and the engine chips - no new timer, no new thread, and Core
        // stays timer-free (the CallActivityWatcher.Poll rule). It returns immediately unless a
        // session is Recording, and is allocation-free on the healthy path.
        _controller.PollCaptureHealth();
```

- [ ] **Step 8: Run the new tests, then the whole Core project**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionControllerCaptureHealthTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "Category!=Fixture" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, 8/8 new; Core green. Pay particular attention to
`SessionControllerPauseTests.Pause_resume_stop_emits_markers_in_order_and_keeps_recording`,
`SessionControllerTests.Retention_never_skips_audio_files` and any `SetRemoteCaptureAsync` /
`SetLocalMuteAsync` marker test - if any fails, the watchdog is firing on a path where zero frames
is legitimate, or Step 6's `started` local was not threaded through both `StartLeg` sites.

- [ ] **Step 9: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Live/SessionController.cs src/LocalScribe.Core/Model/Markers.cs src/LocalScribe.App/CompositionRoot.cs src/LocalScribe.App/ViewModels/SessionViewModel.cs tests/LocalScribe.Core.Tests/SessionControllerCaptureHealthTests.cs
git commit -m "feat(capture): frame-arrival watchdog marks and restarts a dead leg mid-session"
```

---

## Task 9: `OnlyOnFaulted` continuations on the audio and writer loops

`LiveSourcePipeline.StartLeg` spins two bare `Task.Run(..., CancellationToken.None)` loops with no
fault continuation; a throw inside `_audioWriter.Write` (disk full, device removed mid-write) faults
`_audioLoop` and is observed only when `StopLegAndFlushAsync` awaits it - possibly an hour later. The
audio loop is the frame bridge's ONLY reader, so after it dies the capture callback keeps writing into
an unbounded channel forever. The same is true of `SessionController`'s writer loop over the unbounded
outbox.

**Files:**
- Modify: `src/LocalScribe.Core/Live/LiveSourcePipeline.cs:31` (event), `:63-83` (continuation)
- Modify: `src/LocalScribe.Core/Live/SessionController.cs` - `Session` (CAS flags), `StartAsync`
  (subscribe + writer-loop continuation), `Markers.cs` (one constant)
- Test: `tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs`

**Interfaces:**
- Consumes: `ManualCaptureSource` (Task 7); `IDiagnosticLog` (Plan A); the existing exactly-once idiom
  `Interlocked.CompareExchange(ref _flag, 1, 0) == 0` behind a bool getter
  (`SessionController.cs:143-151`).
- Produces: `LiveSourcePipeline.LegFaulted : event Action<SourceKind, Exception>?`;
  `Markers.AudioCaptureFailed` (a `{0}` format string, `{0}` = `"microphone"` or `"remote"`).

- [ ] **Step 1: Write the failing test**

Append to `tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs`:

```csharp
    [Fact]
    public async Task An_audio_write_fault_halts_the_bridge_and_reports_the_leg_once()
    {
        // Disk full mid-recording. Before Tier 1B this faulted _audioLoop silently: the fault was
        // observed only when StopLegAndFlushAsync awaited it (possibly an hour later), and in the
        // meantime the capture callback kept writing into the frame bridge's UNBOUNDED channel with
        // no reader left - memory growth on top of an already-failing recording.
        var (worker, _, loop, cts) = StartWorker();
        var boom = new IOException("There is not enough space on the disk.");
        var sink = new DelegateSink(_ => throw boom);
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, new AlignedAudioWriter(sink));

        var faults = new TaskCompletionSource<(SourceKind Kind, Exception Ex)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.LegFaulted += (k, ex) => faults.TrySetResult((k, ex));

        var source = new ManualCaptureSource(SourceKind.Local);
        pipeline.StartLeg(source, cts.Token, cts.Token);
        source.Emit(startMs: 0);

        var reported = await faults.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SourceKind.Local, reported.Kind);
        Assert.Same(boom, reported.Ex);

        // The bridge was COMPLETED by the continuation, which detaches FrameAvailable - so a frame
        // emitted after the fault reaches nothing at all and the channel cannot grow.
        source.Emit(startMs: 32);

        // The fault is NOT swallowed: Stop still surfaces it, unchanged, so StopAsync's existing
        // leg-fault handling (no pad, teardown, rethrow) behaves exactly as before.
        var thrown = await Assert.ThrowsAsync<IOException>(() => pipeline.StopLegAndFlushAsync());
        Assert.Same(boom, thrown);

        worker.Complete();
        await loop;
    }
```

No using change is needed for `IOException`:
`tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj:5` sets
`<ImplicitUsings>enable</ImplicitUsings>`, so `System.IO` is already in scope, and the file's
existing using block (`LiveSourcePipelineTests.cs:1-7`) stays exactly as it is.

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~An_audio_write_fault_halts_the_bridge" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL to build - `CS1061: 'LiveSourcePipeline' does not contain a definition for 'LegFaulted'`.

- [ ] **Step 3: Add the continuation to `LiveSourcePipeline`**

In `src/LocalScribe.Core/Live/LiveSourcePipeline.cs`, add beside `PeakObserved` (`:31`):

```csharp
    /// <summary>Tier 1B (2026-08-05, T1-4b): this leg's audio loop faulted - a disk-full or
    /// device-removed write. The bridge has ALREADY been completed by the time this fires, so no
    /// further frame is accepted. The same exception ALSO surfaces from StopLegAndFlushAsync's
    /// `await _audioLoop`, so a consumer must be exactly-once (SessionController uses an Interlocked
    /// CAS per leg, the TranscriptionFailed idiom).</summary>
    public event Action<SourceKind, Exception>? LegFaulted;
```

Replace the `_audioLoop` assignment block (`:63-80`) so the continuation is attached before
`source.Start()`:

```csharp
        _audioLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var f in _bridge.ReadAllAsync(captureCt))
                {
                    _audioWriter?.Write(f);                 // ALWAYS - audio never depends on the feed
                    EmitPeak(f);
                    if (!feedCt.IsCancellationRequested)
                        _segInput.Writer.TryWrite(f);       // stop feeding VAD once the worker is gone
                }
            }
            finally
            {
                _segInput.Writer.TryComplete();             // ALWAYS unblock the feed (even on an audio-write fault) -
                                                             // clean EOF -> VAD Flush emits trailing utterance
            }
        }, CancellationToken.None);

        // Tier 1B (2026-08-05, T1-4b): the audio loop is the frame bridge's ONLY reader. If it
        // faults, the capture callback keeps writing into a channel that is UNBOUNDED BY DESIGN
        // (capture must never block on transcription - CaptureFrameBridge.cs:5-9), so the queue
        // grows without limit on top of an already-failing recording. Complete the bridge FIRST -
        // that detaches FrameAvailable and closes the writer - and only then report.
        // The bridge is captured into a local because StopLegAndFlushAsync nulls the field, and this
        // continuation can run long after that. ExecuteSynchronously can run this INLINE at attach
        // time if the loop has already faulted; that is safe here because _bridge and _source are
        // both assigned above, and nothing here reads controller state.
        var faultedBridge = _bridge;
        _ = _audioLoop.ContinueWith(t =>
        {
            try { faultedBridge.Complete(); } catch { }
            // Wrapped: a throwing subscriber must never fault this unobserved continuation - the
            // same wrap SessionController.cs:1230-1238 established for SessionFinalizeCompleted.
            try { LegFaulted?.Invoke(_source, t.Exception!.GetBaseException()); } catch { }
        }, CancellationToken.None,
           TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
           TaskScheduler.Default);

        source.Start();                                 // start LAST: bridge is already listening
```

- [ ] **Step 4: Run it and confirm it passes**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~LiveSourcePipelineTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, whole class.

- [ ] **Step 5: Add the marker constant**

Append to the end of the `Markers` class in `src/LocalScribe.Core/Model/Markers.cs`:

```csharp

    // Capture-health faults (Tier 1B design 2026-08-05, T1-4b). {0} = "microphone" | "remote".
    // Written when a leg's AUDIO WRITE loop faults - disk full, or a device removed mid-write.
    // Recorded because it leaves no other trace: the leg's file simply stops growing, and on a
    // clean Stop AlignedAudioWriter.PadToMs then silence-fills it to the full session length, so
    // the file looks exactly the right size while holding fabricated silence for the whole tail.
    public const string AudioCaptureFailed =
        "audio recording stopped for the {0} stream - the remainder of this session has no {0} audio";
```

- [ ] **Step 6: Subscribe both legs and guard the writer loop in `SessionController`**

Add to the private `Session` class, beside `TryMarkTranscriptionFailed` (`:143-151`):

```csharp
        // Tier 1B (2026-08-05, T1-4b): exactly-once per leg. The SAME exception reaches two sites -
        // the OnlyOnFaulted continuation attached in StartAsync AND StopLegAndFlushAsync's
        // `await _audioLoop` inside StopAsync - so whichever gets there first owns the marker.
        // Interlocked CAS behind a bool getter, the TranscriptionFailed idiom above.
        private int _localLegFaulted, _remoteLegFaulted, _writerFaulted;

        // A ref ternary: the WHOLE conditional is parenthesised behind the `ref`. Written
        // `ref kind == SourceKind.Local ? ref _a : ref _b` it does not compile - `ref` would bind to
        // the comparison, not to the conditional's result.
        public bool TryMarkLegFaulted(SourceKind kind)
            => Interlocked.CompareExchange(
                ref (kind == SourceKind.Local ? ref _localLegFaulted : ref _remoteLegFaulted), 1, 0) == 0;

        public bool TryMarkWriterFailed()
            => Interlocked.CompareExchange(ref _writerFaulted, 1, 0) == 0;
```

Add the handler beside `OnDeviceMuteChanged` (`:366`):

```csharp
    /// <summary>Tier 1B (2026-08-05, T1-4b): a leg's audio-write loop faulted. The bridge is already
    /// halted (LiveSourcePipeline did that first), so this only has to RECORD it. Marked exactly
    /// once per leg via the CAS - the same exception also surfaces from StopAsync's leg settle.
    /// Guarded on session identity + Recording exactly as OnDeviceMuteChanged is; a fault during
    /// StartAsync's prologue (before _session is assigned) is deliberately not marked, because
    /// StartAsync's own catch tears the whole partial session down and never returns an id.</summary>
    private void OnLegFaulted(SourceKind kind, Exception ex)
    {
        var session = _session;
        if (session is null || State != SessionState.Recording) return;
        if (!session.TryMarkLegFaulted(kind)) return;
        string leg = kind == SourceKind.Local ? "microphone" : "remote";
        session.Outbox.Writer.TryWrite(new MarkerAt(string.Format(
            System.Globalization.CultureInfo.InvariantCulture, Markers.AudioCaptureFailed, leg),
            session.Clock.ElapsedMs));
        _log?.Write("error", "capture", "Audio write loop faulted", $"leg={kind} error={ex}");
        ErrorRaised?.Invoke("AUDIO_WRITE_FAILED");
        Notice?.Invoke(kind == SourceKind.Local
            ? "Recording your microphone audio failed - check free disk space. The transcript is still running."
            : "Recording the meeting audio failed - check free disk space. The transcript is still running.");
    }
```

Subscribe both pipelines in `StartAsync`, immediately after they are constructed (`:566-570`):

```csharp
                local.LegFaulted += OnLegFaulted;
                remote.LegFaulted += OnLegFaulted;
```

Add the writer-loop continuation immediately after the existing `workerLoop.ContinueWith` block
(`:642-659`), inside the same `var session = _session;` scope:

```csharp
                // Tier 1B (2026-08-05, T1-4b): the writer loop is the outbox's ONLY reader, and the
                // outbox is Channel.CreateUnbounded (:507). If it faults - transcript.jsonl write
                // failure, disk full - every subsequent segment and marker piles into a channel
                // nobody drains, forever. TryComplete() closes it so producers' TryWrite simply
                // returns false instead of accumulating.
                // NO MARKER on this path, deliberately: the thing that writes markers is exactly
                // what just died, so a marker write here would land in a completed channel and
                // vanish. The notice and the diagnostic log are the honest surfaces, and the
                // launch-time recovery scan finalizes whatever did reach disk.
                _ = writerLoop.ContinueWith(t =>
                {
                    ob.Writer.TryComplete();
                    if (State == SessionState.Recording && ReferenceEquals(_session, session)
                        && session.TryMarkWriterFailed())
                    {
                        _log?.Write("error", "session", "Transcript writer loop faulted",
                            t.Exception?.GetBaseException().ToString());
                        ErrorRaised?.Invoke("TRANSCRIPT_WRITE_FAILED");
                        Notice?.Invoke("Writing the transcript failed - audio is still recording. You can re-transcribe this session later.");
                    }
                }, CancellationToken.None,
                   TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                   TaskScheduler.Default);
```

- [ ] **Step 7: Run the Core project**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "Category!=Fixture" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS. `SessionControllerStopFinalizeTests` and any disk-full/leg-fault test must still
behave identically - the continuation reports, it never swallows.

- [ ] **Step 8: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Live/LiveSourcePipeline.cs src/LocalScribe.Core/Live/SessionController.cs src/LocalScribe.Core/Model/Markers.cs tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs
git commit -m "fix(capture): OnlyOnFaulted guards halt the bridge and outbox when a reader dies"
```

---

## Task 10: Disk-space preflight and the mid-session low-space warning

There is no disk-space check anywhere in the solution: a repo-wide grep for
`DriveInfo|AvailableFreeSpace|free space` returns zero code hits. Disk exhaustion is handled ONLY as
an unclassified leg fault surfacing at Stop - which, before Task 9, was silent until then.

**Files:**
- Create: `src/LocalScribe.Core/Live/DiskSpaceGuard.cs`
- Create: `tests/LocalScribe.Core.Tests/DiskSpaceGuardTests.cs`
- Modify: `src/LocalScribe.Core/Model/Markers.cs` (one constant)
- Modify: `src/LocalScribe.Core/Live/SessionController.cs` - `LiveSessionOptions`, ctor seam,
  `StartAsync` preflight, `PollCaptureHealth` body, `Session`
- Test: `tests/LocalScribe.Core.Tests/SessionControllerCaptureHealthTests.cs` (append)

**Interfaces:**
- Consumes: `IDiagnosticLog` (Plan A); `StoragePaths.Root : string`;
  `SessionController.PollCaptureHealth()` (Task 8); the `Session.Outbox`/`MarkerAt` marker idiom.
- Produces: `LocalScribe.Core.Live.DiskSpaceGuard` with
  `const long DefaultStartFloorBytes` / `DefaultWarnFloorBytes`,
  `static string? RefusalFor(long? freeBytes, long floorBytes)`, `DiskSpaceGuard(long warnFloorBytes)`,
  `bool OnPoll(long? freeBytes)`;
  `LiveSessionOptions.DiskStartFloorBytes` / `.DiskWarnFloorBytes`;
  `SessionController`'s trailing ctor seam `Func<string, long?>? freeBytesProbe = null`;
  `event Action? LowDiskSpaceDetected` - Task 12's banner binds to it;
  `Markers.LowDiskSpace`.

- [ ] **Step 1: Write the failing guard tests**

Create `tests/LocalScribe.Core.Tests/DiskSpaceGuardTests.cs`:

```csharp
using LocalScribe.Core.Live;

namespace LocalScribe.Core.Tests;

/// <summary>Disk-space policy (Tier 1B design 2026-08-05, T1-4c). Pure and probe-free: the real
/// DriveInfo call is a delegate seam on SessionController, so nothing here touches a filesystem or
/// depends on the developer's free space.</summary>
public sealed class DiskSpaceGuardTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public void Refuses_below_the_floor_and_names_the_shortfall()
    {
        string? reason = DiskSpaceGuard.RefusalFor(300L * 1024 * 1024, 2 * Gib);

        Assert.NotNull(reason);
        Assert.Contains("300 MB", reason);      // what is free
        Assert.Contains("2048 MB", reason);     // what is needed
    }

    [Fact]
    public void Permits_at_and_above_the_floor()
    {
        Assert.Null(DiskSpaceGuard.RefusalFor(2 * Gib, 2 * Gib));
        Assert.Null(DiskSpaceGuard.RefusalFor(500 * Gib, 2 * Gib));
    }

    [Fact]
    public void An_unknown_free_space_never_refuses_a_recording()
    {
        // The probe returns null for a UNC path, an unmapped root, or any DriveInfo throw. Refusing
        // to record because we could not MEASURE the disk would block the primary use case on a
        // guess - and the mid-session warning plus the audio-write fault marker still cover the
        // real failure. Fail OPEN.
        Assert.Null(DiskSpaceGuard.RefusalFor(null, 2 * Gib));
    }

    [Fact]
    public void Warns_exactly_once_when_free_space_crosses_below_the_warn_floor()
    {
        var g = new DiskSpaceGuard(warnFloorBytes: Gib);

        Assert.False(g.OnPoll(4 * Gib));
        Assert.True(g.OnPoll(900L * 1024 * 1024));      // crossing: raises
        Assert.False(g.OnPoll(800L * 1024 * 1024));     // still low: never re-raised
        Assert.False(g.OnPoll(700L * 1024 * 1024));
    }

    [Fact]
    public void Recovering_above_the_floor_re_arms_the_warning()
    {
        // The user freed space mid-call. If it drops again that is a NEW fact and must be reported
        // again - the state machine latches the WARNING, not the session.
        var g = new DiskSpaceGuard(warnFloorBytes: Gib);
        Assert.True(g.OnPoll(500L * 1024 * 1024));

        Assert.False(g.OnPoll(8 * Gib));                // recovered - no event, just re-armed
        Assert.True(g.OnPoll(500L * 1024 * 1024));      // dropped again: reported again
    }

    [Fact]
    public void An_unknown_reading_neither_warns_nor_clears()
    {
        var g = new DiskSpaceGuard(warnFloorBytes: Gib);
        Assert.False(g.OnPoll(null));
        Assert.True(g.OnPoll(500L * 1024 * 1024));
        Assert.False(g.OnPoll(null));                   // a failed probe must not "recover" it
        Assert.False(g.OnPoll(400L * 1024 * 1024));     // still latched
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~DiskSpaceGuardTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL to build - `CS0246: The type or namespace name 'DiskSpaceGuard' could not be found`.

- [ ] **Step 3: Create `DiskSpaceGuard`**

Create `src/LocalScribe.Core/Live/DiskSpaceGuard.cs`:

```csharp
using System.Globalization;
namespace LocalScribe.Core.Live;

/// <summary>Disk-space policy for live recording (Tier 1B design 2026-08-05, T1-4c). Pure: the
/// DriveInfo call is a delegate seam on SessionController, so this class holds no IO and no clock.
///
/// WHY A HARD REFUSAL AND NOT A WARNING. Filling the disk mid-call faults the audio write loop; the
/// remainder of the recording is then lost whichever way we handle it, and (because
/// AlignedAudioWriter.PadToMs silence-fills to the stop instant) the file still looks the right
/// length. Losing a call at minute 40 is strictly worse than refusing it at minute 0, when the user
/// can free space and start again. REJECTED: warn-only, which converts a preventable refusal into an
/// unrecoverable evidentiary loss.
///
/// WHY 2 GiB. Retained audio is 16 kHz mono 16-bit per leg: 32 kB/s raw, two legs = 64 kB/s, so a
/// WAV session costs ~230 MB/hour and a FLAC one roughly half that (speech compresses ~50%). 2 GiB
/// is therefore about 9 hours of the WORST case (two-leg WAV) - comfortably past any single call,
/// with room for the transcript, the projections and Windows itself. The 1 GiB warn floor leaves
/// about 4 hours of that worst case after the banner appears, which is enough time to act without
/// nagging on a normally-full laptop.</summary>
public sealed class DiskSpaceGuard
{
    public const long DefaultStartFloorBytes = 2L * 1024 * 1024 * 1024;
    public const long DefaultWarnFloorBytes = 1L * 1024 * 1024 * 1024;

    private readonly long _warnFloorBytes;
    private bool _warned;

    public DiskSpaceGuard(long warnFloorBytes) => _warnFloorBytes = warnFloorBytes;

    /// <summary>A user-facing refusal reason, or null to permit the recording. A null
    /// <paramref name="freeBytes"/> means the probe could not measure (UNC path, unmapped root, a
    /// DriveInfo throw) and ALWAYS permits: refusing on a guess would block the primary use case,
    /// and the mid-session warning plus Task 9's audio-write marker still cover the real failure.</summary>
    public static string? RefusalFor(long? freeBytes, long floorBytes)
    {
        if (freeBytes is not { } free || free >= floorBytes) return null;
        return string.Format(CultureInfo.InvariantCulture,
            "Not enough free disk space to record: {0} MB free, {1} MB needed. "
            + "Free some space on the drive holding your LocalScribe folder and start again.",
            free / (1024 * 1024), floorBytes / (1024 * 1024));
    }

    /// <summary>Mid-session poll. Returns true EXACTLY once per crossing from "enough" to "low", so
    /// the caller marks and warns once rather than on every tick. Recovering above the floor
    /// re-arms it: a second dip is a new fact. An unmeasurable reading changes nothing at all -
    /// a failed probe must never look like a recovery.</summary>
    public bool OnPoll(long? freeBytes)
    {
        if (freeBytes is not { } free) return false;
        if (free >= _warnFloorBytes) { _warned = false; return false; }
        if (_warned) return false;
        _warned = true;
        return true;
    }
}
```

- [ ] **Step 4: Run them and confirm they pass**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~DiskSpaceGuardTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, 6/6.

- [ ] **Step 5: Write the failing controller tests**

Append to `tests/LocalScribe.Core.Tests/SessionControllerCaptureHealthTests.cs`:

```csharp
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
        Assert.Single(lines.Where(l => l.Kind == TranscriptKind.Marker
            && l.Text.StartsWith("low disk space", StringComparison.Ordinal)));
    }
```

- [ ] **Step 6: Add the seam to `LiveTestDoubles.MakeController`**

In `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs`, add a trailing optional parameter to
`MakeController` and forward it:

```csharp
    internal static (SessionController Controller, FakeProvider Provider, StoragePaths Paths, FakeClock Clock)
        MakeController(string root, Settings? settings = null, IEngineFactory? engineFactory = null,
            IReadOnlySet<string>? availableModels = null, Func<string, long?>? freeBytesProbe = null)
```

Pass it through to the `new SessionController(...)` inside that method as
`freeBytesProbe: freeBytesProbe`. Every existing caller uses positional or named arguments that stop
before it, so none changes. Default (`null`) means the controller uses its real `DriveInfo` probe -
which, on any developer machine with more than 2 GiB free, permits Start exactly as today.

- [ ] **Step 7: Wire the controller**

In `src/LocalScribe.Core/Live/SessionController.cs`, append to `LiveSessionOptions`:

```csharp
    /// <summary>Tier 1B (2026-08-05, T1-4c): Start is REFUSED below this. See DiskSpaceGuard for
    /// the arithmetic behind 2 GiB. On LiveSessionOptions rather than Settings deliberately - it is
    /// a safety floor, not a user preference, and putting it in settings.json would invite someone
    /// to set it to zero on the machine that most needs it.</summary>
    public long DiskStartFloorBytes { get; init; } = DiskSpaceGuard.DefaultStartFloorBytes;

    /// <summary>Tier 1B (2026-08-05, T1-4c): the mid-session banner + marker threshold.</summary>
    public long DiskWarnFloorBytes { get; init; } = DiskSpaceGuard.DefaultWarnFloorBytes;
```

Add the field, seam and default probe beside `_log`:

```csharp
    // Tier 1B (2026-08-05, T1-4c): injected so tests drive free space deterministically - the real
    // probe reads the developer's actual disk. Trailing-optional, so no existing call site changes.
    private readonly Func<string, long?> _freeBytes;

    /// <summary>The production free-space probe. Returns null - meaning UNKNOWN, which never
    /// refuses - for a UNC path, an unmapped root, or any DriveInfo throw.</summary>
    private static long? DefaultFreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }
```

Add `Func<string, long?>? freeBytesProbe = null` as the trailing parameter of BOTH constructors
(after `IDiagnosticLog? log = null`), assign `_freeBytes = freeBytesProbe ?? DefaultFreeBytes;` in the
primary one, and forward it from the convenience overload. Add `using System.IO;` if absent.

Add the event beside `CaptureStalled` (Task 8):

```csharp
    /// <summary>Tier 1B (2026-08-05, T1-4c): free space crossed below the warn floor mid-session.
    /// Raised once per crossing (DiskSpaceGuard re-arms if space recovers), never per tick.</summary>
    public event Action? LowDiskSpaceDetected;
```

Add `public required DiskSpaceGuard Disk;` and `public long LastDiskPollMs;` to the `Session` class,
and initialise them in the `Session` initializer:

```csharp
                    Disk = new DiskSpaceGuard(options.DiskWarnFloorBytes),
                    LastDiskPollMs = 0,
```

Add the preflight in `StartAsync`, immediately after the `ExternalEngineBusy` guard (`:390-395`) and
BEFORE the await-PendingFinalize block - nothing has been created at that point, so the refusal is a
clean early return exactly like the two guards above it:

```csharp
            // Tier 1B (2026-08-05, T1-4c): refuse rather than fill the disk mid-call. Same shape as
            // the two guards above - Notice + null, nothing created, State stays Idle.
            if (DiskSpaceGuard.RefusalFor(_freeBytes(_paths.Root), options.DiskStartFloorBytes)
                is string lowDisk)
            {
                _log?.Write("warn", "session", "Start refused - low disk space", lowDisk);
                Notice?.Invoke(lowDisk);
                ErrorRaised?.Invoke("LOW_DISK_SPACE");
                return null;
            }
```

Extend `PollCaptureHealth`, immediately after the `if (s is null || State != ...) return;` guard and
before the watchdog block:

```csharp
        // Disk poll is THROTTLED to every 30 s of session time: PollCaptureHealth runs on the 150 ms
        // UI tick, and DriveInfo.AvailableFreeSpace is a syscall - 6-7 per second on the UI thread
        // for the whole of a recording would be indefensible. Measured against the session clock,
        // so it is deterministic in tests (no wall clock anywhere in this method).
        if (now - s.LastDiskPollMs >= 30_000)
        {
            s.LastDiskPollMs = now;
            if (s.Disk.OnPoll(_freeBytes(_paths.Root)))
            {
                s.Outbox.Writer.TryWrite(new MarkerAt(Markers.LowDiskSpace, now));
                _log?.Write("warn", "session", "Low disk space during recording", $"atMs={now}");
                LowDiskSpaceDetected?.Invoke();
                Notice?.Invoke("Low disk space - this recording may stop before the call ends. Free some space now.");
            }
        }
```

Move the `long now = s.Clock.ElapsedMs;` line above this block if it is not already there.

- [ ] **Step 8: Add the marker constant**

Append to the `Markers` class:

```csharp

    // Low disk space during a live recording (Tier 1B design 2026-08-05, T1-4c). No placeholder:
    // the exact byte count is a diagnostic detail, and a marker is EVIDENCE - the fact that the
    // recording ran while the disk was nearly full is what matters to a reader months later.
    // Written once per crossing; DiskSpaceGuard re-arms if the user frees space and it drops again.
    public const string LowDiskSpace =
        "low disk space while recording - the remainder of this session may be incomplete";
```

- [ ] **Step 9: Run the Core project**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "Category!=Fixture" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS. If a large number of `SessionController*` tests suddenly fail with a null id, the
default `DriveInfo` probe is refusing Start - check the free space on the drive holding
`Path.GetTempPath()` before assuming a code fault.

- [ ] **Step 10: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Live/DiskSpaceGuard.cs src/LocalScribe.Core/Live/SessionController.cs src/LocalScribe.Core/Model/Markers.cs tests/LocalScribe.Core.Tests/DiskSpaceGuardTests.cs tests/LocalScribe.Core.Tests/SessionControllerCaptureHealthTests.cs tests/LocalScribe.Core.Tests/LiveTestDoubles.cs
git commit -m "feat(capture): disk-space preflight refusal plus a mid-session low-space marker"
```

---

## Task 11: Sleep/resume markers and the `PowerTransitionCoordinator`

`Markers.PausedSystemSleep` has been declared since Stage 2b and written by no code anywhere. There
is no `SystemEvents` subscription in the solution. A laptop lid closing mid-call today leaves capture
running into a suspended audio stack, and the transcript records nothing about the gap.

**Files:**
- Modify: `src/LocalScribe.Core/Model/Markers.cs` (one constant)
- Modify: `src/LocalScribe.Core/Live/SessionController.cs` - `PauseAsync` (`:693-713`), `ResumeAsync`
  (`:715` signature and its `Markers.Resumed` write at `:828`), plus one private formatter
- Create: `src/LocalScribe.App/Services/PowerTransitionCoordinator.cs`
- Create: `tests/LocalScribe.App.Tests/PowerTransitionCoordinatorTests.cs`
- Test: `tests/LocalScribe.Core.Tests/SessionControllerCaptureHealthTests.cs` (append)

**Interfaces:**
- Consumes: `SessionState`; `IDiagnosticLog` (Plan A); `ManualUtcTimeProvider` (linked into App.Tests).
- Produces:
  - `SessionController.PauseAsync(CancellationToken ct, bool systemSleep = false)` - trailing
    optional, so `SessionViewModel.PauseResumeAsync` and every test keep compiling.
  - `SessionController.ResumeAsync(CancellationToken ct, TimeSpan? sleepGap = null)` - same.
  - `Markers.ResumedAfterSleep` (`{0}` = the gap, h:mm:ss).
  - `LocalScribe.App.Services.PowerTransitionCoordinator` with `Task OnSuspendAsync()`,
    `Task OnResumeAsync()`, `Task OnPowerModeAsync(bool suspending)` and `bool AutoPaused { get; }`.
    Task 12 wires `OnPowerModeAsync` to `SystemEvents.PowerModeChanged` in one delegating line - the
    mode branch itself is decided (and tested) here, because `App.xaml.cs` has no test coverage in
    this repo at all.

- [ ] **Step 1: Write the failing controller test**

Append to `tests/LocalScribe.Core.Tests/SessionControllerCaptureHealthTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run them and confirm the first fails**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionControllerCaptureHealthTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: the TEST PROJECT FAILS TO COMPILE - `CS1739: The best overload for 'PauseAsync' does not
have a parameter named 'systemSleep'` (and the same for `ResumeAsync`/`sleepGap`). A compile error
means ZERO tests execute, so do not look for a pass or a failure count on this run.

After Step 4 lands, re-run the same command and confirm BOTH new facts pass -
`An_ordinary_pause_and_resume_still_write_the_ordinary_markers` pins behaviour that must not change,
so if it is the one that fails, the marker branch was made unconditional.

- [ ] **Step 3: Add the marker constant**

Append to the `Markers` class:

```csharp

    // System sleep (Tier 1B design 2026-08-05, T1-4d). PausedSystemSleep above has been DECLARED
    // since Stage 2b with no writer anywhere; this round gives it one. {0} is the WALL-CLOCK gap
    // (h:mm:ss) the machine spent suspended - the session clock is monotonic and simply does not
    // advance across a suspend, so without this the transcript would show a pause and a resume
    // three seconds apart for a call that was interrupted for half an hour.
    public const string ResumedAfterSleep = "resumed after system sleep: {0} was not recorded";
```

- [ ] **Step 4: Add the two controller parameters**

In `src/LocalScribe.Core/Live/SessionController.cs`, change `PauseAsync`'s signature and its marker
write (`:693`, `:706`):

```csharp
    /// <param name="systemSleep">True when the machine is suspending (Tier 1B 2026-08-05, T1-4d)
    /// rather than the user clicking Pause. Chooses the marker text ONLY - the leg teardown is
    /// identical, because the correct response to a suspend is exactly the correct response to a
    /// pause: stop capturing rather than record a suspended audio stack. Trailing-optional so
    /// SessionViewModel.PauseResumeAsync and every existing test keep compiling.</param>
    public async Task PauseAsync(CancellationToken ct, bool systemSleep = false)
```

```csharp
            s.Outbox.Writer.TryWrite(new MarkerAt(
                systemSleep ? Markers.PausedSystemSleep : Markers.PausedByUser, s.Clock.ElapsedMs));
```

Change `ResumeAsync`'s signature (`:715`) and its `Markers.Resumed` write (`:828`):

```csharp
    /// <param name="sleepGap">The WALL-CLOCK time the machine spent suspended, when this resume
    /// follows a system sleep (Tier 1B 2026-08-05, T1-4d). Measured App-side by
    /// PowerTransitionCoordinator against an injected TimeProvider, because the session clock is
    /// monotonic (StopwatchClock/QPC) and does not advance across a suspend - Core has no way to
    /// know. Null = an ordinary user resume, which keeps today's plain "resumed" marker.</param>
    public async Task ResumeAsync(CancellationToken ct, TimeSpan? sleepGap = null)
```

```csharp
            s.Outbox.Writer.TryWrite(new MarkerAt(sleepGap is { } gap
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    Markers.ResumedAfterSleep, HmsSpan(gap))
                : Markers.Resumed, s.Clock.ElapsedMs));
```

Add the formatter as a private static method on `SessionController`:

```csharp
    /// <summary>h:mm:ss for a marker, zero-padded, invariant. Built from TOTAL hours rather than
    /// TimeSpan's "hh" custom format specifier, which TRUNCATES the day component instead of
    /// throwing - an overnight suspend of 26 hours would otherwise render as 02:00:00 (recorded
    /// lesson, export round 2026-08-04). A laptop shut for a weekend is exactly the case this
    /// number exists for, so the truncating form is not merely theoretical here.</summary>
    private static string HmsSpan(TimeSpan span)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
            (int)span.TotalHours, span.Minutes, span.Seconds);
```

- [ ] **Step 5: Run the controller tests and confirm they pass**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionControllerCaptureHealthTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, whole class.

- [ ] **Step 6: Write the failing coordinator tests**

Create `tests/LocalScribe.App.Tests/PowerTransitionCoordinatorTests.cs`:

```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Suspend/resume policy (Tier 1B design 2026-08-05, T1-4d). Extracted from the
/// SystemEvents.PowerModeChanged handler for the StopConfirmToastGuard reason: App.xaml.cs has no
/// test coverage at all in this repo, so a decision left in an event handler is a decision that is
/// never tested. TimeProvider is injected because the wall-clock gap is the whole point.</summary>
public sealed class PowerTransitionCoordinatorTests
{
    private sealed class Harness
    {
        public SessionState State = SessionState.Recording;
        public readonly List<string> Calls = new();
        public readonly ManualUtcTimeProvider Time =
            new(new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero));
        public Exception? PauseThrows;

        public PowerTransitionCoordinator Build() => new(
            state: () => State,
            pauseForSleep: () =>
            {
                Calls.Add("pause");
                if (PauseThrows is not null) return Task.FromException(PauseThrows);
                State = SessionState.Paused;
                return Task.CompletedTask;
            },
            resumeAfterSleep: gap =>
            {
                Calls.Add("resume:" + gap.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
                State = SessionState.Recording;
                return Task.CompletedTask;
            },
            Time,
            notify: m => Calls.Add("notify:" + m));
    }

    [Fact]
    public async Task A_suspend_while_recording_pauses_and_a_resume_reports_the_wall_clock_gap()
    {
        var h = new Harness();
        var c = h.Build();

        await c.OnSuspendAsync();
        Assert.True(c.AutoPaused);

        h.Time.Set(new DateTimeOffset(2026, 8, 5, 10, 37, 0, TimeSpan.Zero));
        await c.OnResumeAsync();

        Assert.Equal("pause", h.Calls[0]);
        Assert.Contains("resume:00:37:00", h.Calls);
        Assert.False(c.AutoPaused);
    }

    [Fact]
    public async Task A_suspend_while_idle_does_nothing_at_all()
    {
        var h = new Harness { State = SessionState.Idle };
        var c = h.Build();

        await c.OnSuspendAsync();
        await c.OnResumeAsync();

        Assert.Empty(h.Calls);
        Assert.False(c.AutoPaused);
    }

    [Fact]
    public async Task A_session_the_user_had_already_paused_is_never_auto_resumed()
    {
        // The evidentiary rule: a user who paused for a privileged aside and then closed the lid
        // must NOT come back to a recording session. Only a pause this coordinator performed is
        // ever undone by it.
        var h = new Harness { State = SessionState.Paused };
        var c = h.Build();

        await c.OnSuspendAsync();
        await c.OnResumeAsync();

        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task A_resume_without_a_preceding_suspend_is_a_no_op()
    {
        var h = new Harness();
        var c = h.Build();

        await c.OnResumeAsync();

        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task A_second_resume_does_not_resume_twice()
    {
        // Windows can raise Resume more than once for one suspend (Resume + ResumeAutomatic), and
        // a second ResumeAsync against an already-recording session would only log "Nothing to
        // resume" - but the coordinator must not report a second, wrong gap either.
        var h = new Harness();
        var c = h.Build();
        await c.OnSuspendAsync();
        h.Time.Set(new DateTimeOffset(2026, 8, 5, 10, 5, 0, TimeSpan.Zero));

        await c.OnResumeAsync();
        await c.OnResumeAsync();

        Assert.Single(h.Calls.Where(x => x.StartsWith("resume:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_clock_that_appears_to_move_backwards_reports_a_zero_gap_not_a_negative_one()
    {
        var h = new Harness();
        var c = h.Build();
        await c.OnSuspendAsync();
        h.Time.Set(new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero));   // NTP correction

        await c.OnResumeAsync();

        Assert.Contains("resume:00:00:00", h.Calls);
    }

    [Fact]
    public async Task A_failing_pause_is_surfaced_and_never_thrown_at_the_system_events_callback()
    {
        // This runs from a SystemEvents callback during a suspend. An exception escaping there is
        // an unhandled exception on a thread nobody is watching, at the worst possible moment.
        var h = new Harness { PauseThrows = new InvalidOperationException("device gone") };
        var c = h.Build();

        await c.OnSuspendAsync();                      // must not throw

        Assert.Contains(h.Calls, x => x.StartsWith("notify:", StringComparison.Ordinal));
        Assert.False(c.AutoPaused);                    // the pause did not happen: never auto-resume
    }

    [Fact]
    public async Task The_power_mode_branch_itself_is_decided_here_not_in_an_App_xaml_lambda()
    {
        // SHARED-CONTRACT section 4 (trap 9): App.xaml.cs has NO test coverage in this repo - 105
        // test files, no AppTests.cs - so a suspend-vs-resume branch written into the
        // PowerModeChanged lambda is a branch nothing ever exercises. OnPowerModeAsync owns it; the
        // handler is left with one delegating line.
        var h = new Harness();
        var c = h.Build();

        await c.OnPowerModeAsync(suspending: true);
        Assert.True(c.AutoPaused);

        h.Time.Set(new DateTimeOffset(2026, 8, 5, 10, 12, 0, TimeSpan.Zero));
        await c.OnPowerModeAsync(suspending: false);

        Assert.Equal(new[] { "pause", "resume:00:12:00" }, h.Calls);
        Assert.False(c.AutoPaused);
    }
}
```

`System.Linq` needs no using directive here - App.Tests has `<ImplicitUsings>enable</ImplicitUsings>`
(`LocalScribe.App.Tests.csproj:5`). The block at the top of the file is its FINAL using block.

- [ ] **Step 7: Run them and confirm they fail**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~PowerTransitionCoordinatorTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL to build - `CS0246: The type or namespace name 'PowerTransitionCoordinator' could not be found`.

- [ ] **Step 8: Create the coordinator**

Create `src/LocalScribe.App/Services/PowerTransitionCoordinator.cs`:

```csharp
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Live;
namespace LocalScribe.App.Services;

/// <summary>Suspend/resume policy for a live recording (Tier 1B design 2026-08-05, T1-4d).
///
/// THE PROBLEM: closing a laptop lid mid-call leaves capture running into a suspended audio stack.
/// Nothing in the solution subscribed SystemEvents at all, and Markers.PausedSystemSleep has been
/// declared since Stage 2b with no writer. On wake the session simply carries on with an
/// unexplained hole - and because the session clock is MONOTONIC (StopwatchClock/QPC), it does not
/// advance across the suspend, so even the hole's size is invisible from Core.
///
/// Extracted rather than written inline in the PowerModeChanged handler for the StopConfirmToastGuard
/// reason recorded at App.xaml.cs:864-874: App.xaml.cs has no test coverage in this repo, so
/// anything decided there is decided untested. TimeProvider is injected because the wall-clock gap
/// is the entire deliverable.
///
/// Only ever undoes ITS OWN pause. A user who paused for a privileged aside and then closed the lid
/// must not come back to a recording session - that is an evidentiary violation, not a convenience.</summary>
public sealed class PowerTransitionCoordinator(
    Func<SessionState> state,
    Func<Task> pauseForSleep,
    Func<TimeSpan, Task> resumeAfterSleep,
    TimeProvider time,
    Action<string> notify,
    IDiagnosticLog? log = null)
{
    private DateTimeOffset? _suspendedAtUtc;

    /// <summary>True while a pause THIS coordinator performed is outstanding.</summary>
    public bool AutoPaused => _suspendedAtUtc is not null;

    /// <summary>The whole PowerModeChanged decision, so App.xaml.cs is left with one delegating
    /// line. The branch lives HERE because App.xaml.cs has no test coverage anywhere in this repo
    /// (105 test files, no AppTests.cs) - a branch written into that lambda is a branch nothing ever
    /// exercises. Only Suspend and Resume matter; PowerModes.StatusChange (a battery/AC transition)
    /// is deliberately ignored, and the caller passes only the two it cares about.</summary>
    public Task OnPowerModeAsync(bool suspending)
        => suspending ? OnSuspendAsync() : OnResumeAsync();

    /// <summary>The machine is suspending. Never throws: it runs from a SystemEvents callback
    /// during a suspend, where an escaping exception is unhandled on a thread nobody is watching at
    /// the worst possible moment.</summary>
    public async Task OnSuspendAsync()
    {
        if (state() != SessionState.Recording) return;   // Paused/Idle/Finalizing: nothing to protect
        var at = time.GetUtcNow();
        try
        {
            log?.Write("info", "session", "System suspending - pausing the recording");
            await pauseForSleep();
            _suspendedAtUtc = at;                        // set only on SUCCESS: a failed pause must
                                                          // never be "resumed" later
        }
        catch (Exception ex)
        {
            log?.Write("error", "session", "Pause on suspend failed", ex.ToString());
            notify("Could not pause the recording before the machine slept: " + ex.Message);
        }
    }

    /// <summary>The machine has woken. A no-op unless THIS coordinator paused. Never throws.</summary>
    public async Task OnResumeAsync()
    {
        if (_suspendedAtUtc is not { } at) return;
        // Cleared FIRST: Windows can raise Resume more than once for one suspend (Resume and
        // ResumeAutomatic), and a second pass must not report a second, wrong gap.
        _suspendedAtUtc = null;
        var gap = time.GetUtcNow() - at;
        if (gap < TimeSpan.Zero) gap = TimeSpan.Zero;    // an NTP correction must never read as negative
        try
        {
            log?.Write("info", "session", "System resumed - resuming the recording",
                $"gapSeconds={(long)gap.TotalSeconds}");
            await resumeAfterSleep(gap);
        }
        catch (Exception ex)
        {
            log?.Write("error", "session", "Resume after sleep failed", ex.ToString());
            notify("Could not resume the recording after the machine woke: " + ex.Message
                + " Use Resume on the record console.");
        }
    }
}
```

- [ ] **Step 9: Run them and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~PowerTransitionCoordinatorTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, 8/8.

- [ ] **Step 10: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Model/Markers.cs src/LocalScribe.Core/Live/SessionController.cs src/LocalScribe.App/Services/PowerTransitionCoordinator.cs tests/LocalScribe.Core.Tests/SessionControllerCaptureHealthTests.cs tests/LocalScribe.App.Tests/PowerTransitionCoordinatorTests.cs
git commit -m "feat(power): sleep/resume markers with the wall-clock gap, behind a tested policy"
```

---

## Task 12: App wiring - `PowerModeChanged`, `SessionEnding`, capture and low-space banners

The last mile: three subscriptions in `App.xaml.cs` and three warning rows. **Nothing decidable is
left in a lambda here** - the mode branch lives in `PowerTransitionCoordinator.OnPowerModeAsync`
(Task 11), the confirm-or-not and the shutdown budget live in `ExitSequence` (Task 3), and the
banner latching lives in `SessionViewModel`. That matters because SHARED-CONTRACT section 4 (trap 9)
records `App.xaml.cs` and `TrayIconHost.cs` as having NO test coverage in this repo: anything decided
in one of these lambdas is decided untested, permanently.

**`SystemEvents` is a STATIC event**: a subscription that is never removed keeps the whole `App`
alive and fires into a disposed world, so the unsubscribe in `OnExit` is not optional.

**Files:**
- Modify: `src/LocalScribe.App/App.xaml.cs` - field, wiring inside `OnStartup` (after the `_tray`
  construction at `:818-827`), `OnExit` (`:1132-1144`)
- Modify: `src/LocalScribe.App/ViewModels/SessionViewModel.cs` - `LowDiskSpace`,
  `MicCaptureDead`/`RemoteCaptureDead` state + named handlers
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml:365-400` (three warning rows)

**Interfaces:**
- Consumes: `TrayIconHost.BuildExitSequence() : ExitSequence`,
  `ExitSequence.RunUnattendedAsync() : Task<bool>` and `ExitSequence.ShutdownBudget : TimeSpan`
  (Task 3); `PowerTransitionCoordinator.OnPowerModeAsync(bool suspending)` (Task 11);
  `SessionController.PauseAsync(ct, bool systemSleep = false)` /
  `.ResumeAsync(ct, TimeSpan? sleepGap = null)` (Task 11);
  `SessionController.LowDiskSpaceDetected : event Action?` (Task 10);
  `SessionController.CaptureStalled` / `.CaptureRecovered : event Action<SourceKind>?` (Task 8);
  `AppComposition.Log : IDiagnosticLog` (Plan A, shared contract section 3a) - reached as `comp.Log`,
  which is in scope throughout `OnStartup`.
- Produces: `SessionViewModel.LowDiskSpace : bool`, `.MicCaptureDead : bool`,
  `.RemoteCaptureDead : bool` (all bound by `LiveViewWindow.xaml`).

- [ ] **Step 1: Write the failing view-model tests**

Append to `tests/LocalScribe.App.Tests/SessionViewModelTests.cs` (or create it following
`ReadViewDirtyTests`'s shape if it does not exist - it does; it already drives
`RaiseSilentLegDetectedForTest`):

```csharp
    [Fact]
    public void Low_disk_space_raises_a_persistent_banner_flag()
    {
        // Tier 1B (2026-08-05, T1-4c). Persistent, not a toast: the condition does not go away by
        // itself, and the tray balloon Notice that accompanies it is exactly what Focus Assist
        // suppresses. The flag is deliberately one-way for the session - DiskSpaceGuard already
        // de-duplicates the event, and a banner that flickered off on a transient reading would be
        // worse than one the user has to act on.
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());

        Assert.False(vm.LowDiskSpace);
        controller.RaiseLowDiskSpaceForTest();

        Assert.True(vm.LowDiskSpace);
    }

    [Fact]
    public void A_dead_capture_leg_raises_a_persistent_banner_and_a_recovery_clears_it()
    {
        // Tier 1B (2026-08-05, T1-4a). Capture death must NOT surface only as a tray balloon:
        // spec T1-5 records tray notices as suppressed by Focus Assist, and it would be absurd for
        // low disk space - the LESS severe condition - to get a persistent on-screen row while
        // "your microphone died forty minutes ago" gets a toast the user never saw. Mirrors the
        // MicSilent/RemoteSilent pair already bound at LiveViewWindow.xaml, including the CLEAR:
        // every CaptureStalled has exactly one matching CaptureRecovered (FrameArrivalWatchdog
        // guarantees the pairing), so this banner can never stick on after the leg comes back.
        // These are also the ONLY consumers of RaiseCaptureStalledForTest/RaiseCaptureRecoveredForTest -
        // without them those hooks would be produced by Task 8 and used by nothing.
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());

        controller.RaiseCaptureStalledForTest(SourceKind.Local);
        controller.RaiseCaptureStalledForTest(SourceKind.Remote);
        Assert.True(vm.MicCaptureDead);
        Assert.True(vm.RemoteCaptureDead);

        controller.RaiseCaptureRecoveredForTest(SourceKind.Local);

        Assert.False(vm.MicCaptureDead);
        Assert.True(vm.RemoteCaptureDead);          // per leg, never a single shared flag
    }
```

- [ ] **Step 2: Run them and confirm they fail**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SessionViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: FAIL to build - `CS1061: 'SessionController' does not contain a definition for
'RaiseLowDiskSpaceForTest'` and `CS1061: 'SessionViewModel' does not contain a definition for
'MicCaptureDead'`. A compile error means no test in the class runs; re-run after Step 3.

- [ ] **Step 3: Add the test hook and the VM state**

In `src/LocalScribe.Core/Live/SessionController.cs`, beside `RaiseCaptureStalledForTest` (Task 8):

```csharp
    // Same rationale as the RaiseSilentLeg*ForTest pair: no InternalsVisibleTo exists between Core
    // and the test assemblies, so an App.Tests VM test needs a public hook rather than a 30-second
    // disk-poll interval and a fake filesystem. Production code never calls this.
    public void RaiseLowDiskSpaceForTest() => LowDiskSpaceDetected?.Invoke();
```

In `src/LocalScribe.App/ViewModels/SessionViewModel.cs`, add the state beside `MicDeviceMuted`:

```csharp
    /// <summary>Tier 1B (2026-08-05, T1-4c): free space crossed below the warn floor during this
    /// recording. Persistent for the rest of the session - SessionController's DiskSpaceGuard
    /// already raises the event once per crossing, and a banner that flickered off on a transient
    /// reading would be worse than one the user has to act on. Cleared when a NEW session starts
    /// (the ctor's StateChanged handler), never mid-session.</summary>
    [ObservableProperty] private bool _lowDiskSpace;

    /// <summary>Tier 1B (2026-08-05, T1-4a): this leg produced NO FRAMES for CaptureStallGraceMs and
    /// is being (or has been) rebuilt. Distinct from MicSilent/RemoteSilent, which mean "frames but
    /// no speech" and are structurally incapable of firing when frames stop. Cleared by the matching
    /// CaptureRecovered - FrameArrivalWatchdog guarantees exactly one clear per raise, which is what
    /// lets a banner be driven off the pair at all.</summary>
    [ObservableProperty] private bool _micCaptureDead;
    /// <summary>Same as <see cref="MicCaptureDead"/> for the remote (system/app) capture leg.</summary>
    [ObservableProperty] private bool _remoteCaptureDead;
```

Add the named handlers beside `_onMicDeviceMuteChanged`:

```csharp
    // Tier 1B: named (not lambdas) so Dispose can detach them - _controller is the shared,
    // app-lifetime SessionController, exactly as for the four handlers above.
    private readonly Action _onLowDiskSpace;
    private readonly Action<SourceKind> _onCaptureStalled;
    private readonly Action<SourceKind> _onCaptureRecovered;
```

Wire them in the constructor beside `controller.MicDeviceMuteChanged += _onMicDeviceMuteChanged;`,
mirroring the `_onSilentLegDetected`/`_onSilentLegCleared` pair two lines above:

```csharp
        _onLowDiskSpace = () => _dispatch(() => LowDiskSpace = true);
        controller.LowDiskSpaceDetected += _onLowDiskSpace;
        _onCaptureStalled = kind => _dispatch(() =>
        { if (kind == SourceKind.Local) MicCaptureDead = true; else RemoteCaptureDead = true; });
        _onCaptureRecovered = kind => _dispatch(() =>
        { if (kind == SourceKind.Local) MicCaptureDead = false; else RemoteCaptureDead = false; });
        controller.CaptureStalled += _onCaptureStalled;
        controller.CaptureRecovered += _onCaptureRecovered;
```

Detach them in `Dispose` beside the other four:

```csharp
        _controller.LowDiskSpaceDetected -= _onLowDiskSpace;
        _controller.CaptureStalled -= _onCaptureStalled;
        _controller.CaptureRecovered -= _onCaptureRecovered;
```

Clear them on a fresh session inside the existing `controller.StateChanged` handler, in the same
`if (s != SessionState.Recording)` block that clears the app-mute banner - add one line there:

```csharp
                // A NEW recording starts clean. Same stale-flag-from-a-prior-session hazard the
                // MicSilent/RemoteSilent reset above records.
                if (s == SessionState.Idle) LowDiskSpace = MicCaptureDead = RemoteCaptureDead = false;
```

- [ ] **Step 4: Run the VM tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SessionViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS, including the two added here and every pre-existing fact in the class.

- [ ] **Step 5: Add the banner rows**

In `src/LocalScribe.App/LiveViewWindow.xaml`, inside the existing warning `StackPanel` (`:365-400`),
add three rows immediately after the `Session.MicDeviceMuted` TextBlock:

```xml
                    <!-- Tier 1B (2026-08-05, T1-4a): the capture leg itself died - no frames at all,
                         which SilentLegMonitor cannot see because it is driven from inside the frame
                         loop. Placed ABOVE the low-space row because it is the more severe fact.
                         Persistent and self-clearing: SessionController raises exactly one
                         CaptureRecovered per CaptureStalled, so this can never stick on. -->
                    <TextBlock Text="The microphone stopped producing audio - reconnecting it. Check the device if this repeats."
                               Visibility="{Binding Session.MicCaptureDead, Converter={StaticResource BoolToVis}}"
                               Style="{StaticResource WarningText}" TextWrapping="Wrap" />
                    <TextBlock Text="The meeting/system audio stream stopped - reconnecting it. Check that audio is still playing."
                               Visibility="{Binding Session.RemoteCaptureDead, Converter={StaticResource BoolToVis}}"
                               Style="{StaticResource WarningText}" TextWrapping="Wrap" />
                    <!-- Tier 1B (2026-08-05, T1-4c): low free space on the storage drive. A plain
                         WarningText row, matching every sibling here - the theme brush comes from
                         the shared style, so XamlHygiene's no-ARGB-literals rule is satisfied by
                         construction. Persistent by design (see SessionViewModel.LowDiskSpace);
                         the accompanying tray balloon is exactly what Focus Assist suppresses,
                         which is why the on-screen row exists at all. -->
                    <TextBlock Text="Low disk space - this recording may stop before the call ends. Free some space now."
                               Visibility="{Binding Session.LowDiskSpace, Converter={StaticResource BoolToVis}}"
                               Style="{StaticResource WarningText}" TextWrapping="Wrap" />
```

- [ ] **Step 6: Wire `PowerModeChanged` and `SessionEnding`**

In `src/LocalScribe.App/App.xaml.cs`, add a field beside `_tray` (`:17`):

```csharp
    // Tier 1B (2026-08-05, T1-4d). SystemEvents is a STATIC event: an un-removed subscription keeps
    // this App instance alive and fires into a disposed world, so OnExit MUST detach it. Held as a
    // field for exactly that reason (the _embeddingClient precedent at :29-38 - OnExit is a separate
    // method from OnStartup and can reach only fields).
    private Microsoft.Win32.PowerModeChangedEventHandler? _onPowerModeChanged;
```

Immediately after the `_tray = new TrayIconHost(...)` statement (`:818-827`), add:

```csharp
        // Tier 1B (2026-08-05, T1-4d): suspend/resume. Every decision lives in the tested
        // PowerTransitionCoordinator; this is subscription glue. PowerModeChanged fires on a
        // SystemEvents thread, so the coordinator's delegates go through Task.Run - the same shape
        // SessionViewModel.SwitchRemoteTargetAsync uses for controller calls off the UI thread -
        // and the coordinator itself never throws.
        var power = new PowerTransitionCoordinator(
            state: () => comp.Controller.State,
            pauseForSleep: () => Task.Run(() =>
                comp.Controller.PauseAsync(CancellationToken.None, systemSleep: true)),
            resumeAfterSleep: gap => Task.Run(() =>
                comp.Controller.ResumeAsync(CancellationToken.None, sleepGap: gap)),
            TimeProvider.System,
            notify: m => Dispatcher.BeginInvoke(() => _tray?.ShowNotice(m)),
            log: comp.Log);
        _onPowerModeChanged = (_, args) =>
        {
            // One delegating line per mode - the branch itself is decided and TESTED in
            // PowerTransitionCoordinator.OnPowerModeAsync. Suspend is the only mode that must
            // complete before the machine goes down and Windows gives a bounded window for it, so
            // it is awaited synchronously on this callback thread (NOT the UI thread: nothing here
            // touches WPF, and the App dispatcher may already be idle). Resume is fire-and-forget:
            // nothing is waiting on it. StatusChange is ignored.
            if (args.Mode == Microsoft.Win32.PowerModes.Suspend)
                power.OnPowerModeAsync(suspending: true).GetAwaiter().GetResult();
            else if (args.Mode == Microsoft.Win32.PowerModes.Resume)
                _ = power.OnPowerModeAsync(suspending: false);
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += _onPowerModeChanged;

        // Tier 1B (2026-08-05, T1-4d): Windows is logging off / shutting down. Run the SAME sequence
        // the tray Exit item runs - a second hand-written copy would drift, and only one of the two
        // would ever be exercised by hand.
        //
        // RunUnattendedAsync, NEVER RunAsync. The attended path's Recording/Paused branch raises a
        // modal MessageBox, and this call is on a thread-pool thread while the UI thread is blocked
        // in Wait(): NOBODY CAN ANSWER A DIALOG DURING LOGOFF. The wait would expire with the box
        // still up, stopRecording never called, and a live evidentiary session orphaned with no
        // EndedAtUtc - the exact loss Task 13's log-off smoke item forbids. Windows has already
        // asked the user whether to log off; stopping cleanly IS the protective act here.
        //
        // The budget comes from ExitSequence.ShutdownBudget rather than a literal, so the number is
        // asserted in ExitSequenceTests - App.xaml.cs has no test coverage in this repo.
        //
        // Blocking this handler is safe because App's dispatch seam is Dispatcher.BeginInvoke
        // (App.xaml.cs:119) - fire-and-forget, never a blocking Invoke - so nothing in the stop path
        // needs this thread to pump in order to complete. e.Cancel is deliberately NEVER set:
        // refusing a shutdown from a background tray app is hostile, and the drain either finishes
        // inside the budget or the recovery scan finishes it on the next launch (which Task 1 of
        // this round made non-lossy).
        SessionEnding += (_, _) =>
        {
            try
            {
                var exit = _tray!.BuildExitSequence();
                Task.Run(() => exit.RunUnattendedAsync()).Wait(exit.ShutdownBudget);
            }
            catch (Exception ex)
            {
                comp.Log.Write("error", "session", "SessionEnding drain failed", ex.ToString());
            }
        };
```

Add the unsubscribe to `OnExit`, before `_tray?.Dispose();`:

```csharp
        // MUST run: SystemEvents is a static event, so leaving this attached keeps the App instance
        // alive and delivers callbacks into a disposed world.
        if (_onPowerModeChanged is { } pm) Microsoft.Win32.SystemEvents.PowerModeChanged -= pm;
```

`comp.Log` is `AppComposition`'s new member (Plan A, shared contract section 3a) and is the ONLY way
this method may reach the log - a local declared inside `CompositionRoot.Build()` is not in scope
here. `comp` and `comp.Controller` are already in scope at `:818` (the deep-link router reads
`comp.Controller.State` at `:965`).

**Why this step ships without an automated test - a RULING, not an oversight.** Three things added
here are asserted by nothing: the `SessionEnding` "`e.Cancel` is deliberately NEVER set" policy, the
suspend-blocks / resume-fire-and-forget dispatch choice inside the `_onPowerModeChanged` lambda, and
the `SystemEvents` unsubscribe. All three live in `App.xaml.cs`, which has NO test coverage in this
repo (105 test files, no `AppTests.cs` - shared contract section 4, trap 9), and all three are driven
by real OS events (`SystemEvents.PowerModeChanged`, `Application.SessionEnding`) that no headless
harness in this solution can raise. Building an App-level harness to reach them is out of scope for
Tier 1B. **They are covered by the smoke checklist instead** - lid-close/resume and a Windows restart
with a recording in progress - and by the fact that the policy classes they delegate to
(`CaptureHealthWatchdog`, `SessionController.PauseAsync`/`ResumeAsync`) ARE tested. Do not fabricate
a test that only asserts the lambda was assigned; it would pin the wiring without exercising the
behaviour and would read as coverage that does not exist.

- [ ] **Step 7: Build and run the App suite**

```
dotnet build src/LocalScribe.App/LocalScribe.App.csproj --nologo
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo
```

Expected: build clean; App green - judge by failing test NAME, never by count.
`XamlHygieneTests.ShippedXaml_HasNoDisallowedHardcodedBrushes` and
`PageAndWindowRoots_SetInheritableForeground` must both stay green - the three new rows use only
`{StaticResource WarningText}` and `{StaticResource BoolToVis}`, both already declared for this
window.

- [ ] **Step 8: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/App.xaml.cs src/LocalScribe.App/LiveViewWindow.xaml src/LocalScribe.App/ViewModels/SessionViewModel.cs src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.App.Tests/SessionViewModelTests.cs
git commit -m "feat(power): pause on suspend, drain on session end, warn on low disk"
```

---

## Task 13: Whole-round verification

**Files:**
- Test: `tests/LocalScribe.Core.Tests/SessionWriterTests.cs`

- [ ] **Step 1: Write the stacked-recovery regression test**

The three re-derives interact: a crashed session with a WAV leg longer than the transcript, an
already-populated retained list, and a second recovery pass. Append to `SessionWriterTests.cs`:

```csharp
    [Fact]
    public async Task Recovery_with_a_wav_leg_a_seeded_list_and_a_shortfall_is_correct_and_idempotent()
    {
        // Every T1-2 leg stacked in one fixture, then re-run. The re-run is the part that matters:
        // recovery is retried on EVERY launch for anything still unended, and the second pass must
        // be a clean no-op rather than a second marker and a second duration.
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: null);
            var store = new SessionStore(paths.SessionJson("s1"));
            var seeded = await store.ReadAsync(default);
            await store.SaveAsync(seeded! with { RetainedAudioSources = new[] { SourceKind.Local } }, default);
            WriteLeg(paths, "s1", SourceKind.Remote, AudioFormat.Wav, 25_000);   // WAV, and only remote

            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            Assert.True(await writer.RecoverIfNeededAsync("s1", default));

            var first = await store.ReadAsync(default);
            Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, first!.RetainedAudioSources);
            Assert.Equal(25_000, first.DurationMs);
            Assert.Equal(T0.AddMilliseconds(25_000), first.EndedAtUtc);
            Assert.Equal(2, first.MarkerCount);

            Assert.False(await writer.RecoverIfNeededAsync("s1", default));      // gated on EndedAtUtc

            var second = await store.ReadAsync(default);
            Assert.Equal(first.RetainedAudioSources, second!.RetainedAudioSources);
            Assert.Equal(first.DurationMs, second.DurationMs);
            Assert.Equal(first.MarkerCount, second.MarkerCount);                 // no second marker
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
```

- [ ] **Step 2: Run it**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Recovery_with_a_wav_leg" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1b\
```

Expected: PASS. A failure on `MarkerCount` means the discrepancy marker is being written outside the
`audioMs > lastEndMs` guard or outside the `EndedAtUtc is null` gate.

- [ ] **Step 3: Whole-suite run**

```
dotnet test LocalScribe.slnx --filter "Category!=Fixture"
```

Expected: Core, App and Mcp fully green - **judge by failing test NAME, not by count**. Any failing
name that is not in this plan's new files is a regression from this round. The baseline was Core
1186 / App 984 / Mcp 6.

- [ ] **Step 4: Whole-branch ASCII byte-scan**

```powershell
cd F:\LocalScribe
$files = git diff --name-only master...HEAD
foreach ($f in $files) {
  if (Test-Path $f) {
    $b = [IO.File]::ReadAllBytes($f)
    $n = ($b | Where-Object { $_ -gt 127 }).Count
    if ($n -gt 0) { "NON-ASCII ($n bytes): $f" }
  }
}
"scan complete"
```

Expected: only `scan complete`. Markdown under `docs/` is exempt; **source files are not**. A `.cs`
file reporting non-ASCII means an escape was converted to a literal glyph - restore the `\uXXXX`.

- [ ] **Step 5: Confirm line endings survived**

```powershell
cd F:\LocalScribe
git diff --stat master...HEAD
git diff --check master...HEAD
```
Expected: no whitespace errors, and a plausible changed-file list - no file showing as wholly
rewritten, which would indicate a CRLF/LF flip.

- [ ] **Step 6: Confirm the two previously-dead markers now have writers**

```bash
cd F:/LocalScribe
grep -rn "Markers.AudioDeviceChanged\|Markers.PausedSystemSleep" src/
```

Expected: at least one hit in `src/LocalScribe.Core/Live/SessionController.cs` for each, on top of
their declarations in `Markers.cs`. Before this round both were declared and written nowhere.

- [ ] **Step 7: Commit**

```bash
cd F:/LocalScribe
git add tests/LocalScribe.Core.Tests/SessionWriterTests.cs
git commit -m "test(recovery): stacked WAV-leg, seeded-list and idempotence regression"
```

---

## Post-Implementation

Once all 13 tasks are green:

1. **Request code review** - use `superpowers:requesting-code-review`.
2. **Do NOT merge before the smoke run.** Six of this round's behaviours cannot be settled by a static
   suite: two of them (the close guard and the `SessionEnding` drain) have no automated test at all by
   design, and three more depend on real hardware.
3. **Smoke checklist for the user:**
   - **Orphaned recording (T1-2).** Record 60 seconds, press Stop, and immediately choose tray
     **Exit**. Relaunch and open the session: it must NOT carry a "recovered session" marker, and
     `session.json` must have a real `endedAtUtc`, `durationMs` and `retainedAudioSources`.
   - **Recovery re-derive (T1-2).** Record 3 minutes, then kill `LocalScribe.App.exe` from Task
     Manager (End Task - do not Stop first). Relaunch. The session must recover with playback
     available, Re-transcribe offered, and Split Speakers enabled - all four were refused before this
     round. If the transcript is short, expect the "retained audio runs to ... but the transcript
     stops at ..." marker and a duration matching the audio, not the transcript.
   - **Read-view guard (T1-3).** Open a session, Edit, retype one line, close with the X. Expect the
     Save / Discard / Cancel prompt. Verify all three: Cancel keeps the window open with the edit
     intact; Discard closes and the edit is gone; Save closes and the edit persists. Then open Edit,
     change nothing, and close - there must be NO prompt.
   - **Capture death (T1-4a).** Start a recording with a USB headset, then unplug it mid-recording.
     Within ~8 seconds expect a tray notice, a persistent "The microphone stopped producing audio"
     row on the Record console, an "audio device changed" marker in the transcript, and the local
     leg reconnecting to the fallback device. Plug it back in and confirm the row clears, the
     session is still recording and transcript lines are still being written.
   - **Capture death that does NOT recover (T1-4a).** Repeat the unplug and leave the headset out
     for two full minutes. Expect at most THREE "audio device changed" markers, then exactly one
     "capture did not come back for the microphone stream..." marker and one final notice - and then
     silence. A fourth device-changed marker, or a marker every 8 seconds, means the restart budget
     is not being consumed.
   - **Disk space (T1-4c).** Point Settings > storage at a nearly-full drive (or fill one) and press
     Start: it must refuse with the free/needed figures and record nothing. Then start on a healthy
     drive and fill it mid-recording: expect the low-space row on the Record console and the
     "low disk space while recording" marker.
   - **Sleep (T1-4d).** Start a recording, close the lid (or Start > Sleep), wait a few minutes, wake.
     Expect "paused: system sleep" and "resumed after system sleep: hh:mm:ss was not recorded" in the
     transcript, with a gap that matches the wall clock, and the session recording again.
   - **Log off (T1-4d).** Start a recording and log off Windows. There must be NO "a recording is in
     progress" prompt - the SessionEnding path runs `RunUnattendedAsync`, and a prompt nobody can
     answer is what orphans the session. Log back in and open the session: it must be finalized
     normally, with a real `endedAtUtc`, and it must NOT carry a "recovered session" marker.
   - **Diagnostics reach disk on exit (T1-2).** After the tray-Exit smoke above, open
     `<storage root>\diagnostics\diag-<yyyyMM>.jsonl`: the last lines of the session must be
     present, which is what the `FlushAsync` on the exit path exists to guarantee. Then grep the
     whole `diagnostics\` folder for a fragment of what was actually said on the call - there must
     be no hit.
4. **Plan C** (trustworthy output) depends on nothing here but shares `Markers.cs` and
   `SessionController`; rebase it on this branch before starting to avoid a three-way merge in the
   marker file.
