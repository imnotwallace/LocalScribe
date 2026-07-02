# Stage 3a: Live Pipeline — Real-Time Capture Wiring, Session Lifecycle, Live Probe — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the Stage-2b pipeline to REAL capture in real time — mic + process-loopback frames flow through VAD -> Whisper -> merge -> store while the meeting is happening — under a `SessionController` that implements the spec 2.1 lifecycle (Idle/Recording/Paused/Finalizing) with Pause/Resume markers, the single-session guard, pre-flight peak probe, remote auto/fallback resolution with the `degraded` marker, live CUDA/Vulkan hardware probing, session-clock-aligned retained audio, and the PhantomBleedDedup swap into SessionWriter — demoable headless via a new `LocalScribe.LiveRunner` console app on a real Webex call.

**Architecture:** A new `LocalScribe.Core.Live` namespace. `CaptureFrameBridge` turns `ICaptureSource.FrameAvailable` events into the `IAsyncEnumerable<AudioFrame>` the existing `SileroVadSegmenter` consumes (unbounded channel: capture never blocks — backpressure from the bounded transcription queue piles up here, never on the capture thread). `LiveSourcePipeline` runs one source's capture "leg" (source -> bridge -> VAD -> `worker.EnqueueAsync`), tapping frames to an `AlignedAudioWriter` (retained audio stays sample-aligned to the session clock across pauses) and raising per-frame peaks (feeds 3b's level bars). `SessionController` composes two of them per session, owns the state machine + finalize (mirroring `OfflinePipelineRunner`'s outbox/writer-loop/C1 fault-guard patterns), and re-exposes `TranscriptMerger.LineInserted` as the live-view seam. Pause STOPS capture (privilege protection); Resume starts a **fresh leg** via factories (fresh `MicCaptureSource` also re-resolves the default device). Humble Object throughout: `RemoteCapturePlanner` (pure) + `WasapiSessionScanner` (thin), `NvidiaSmi.ParseVramMb` (pure) + `LiveHardwareProbe` (thin), and every controller behavior tests hardware-free with `FakeCaptureSource`/`FakeTranscriptionEngine`/`FakeClock`.

**Tech Stack:** .NET 10 (`net10.0-windows`), existing packages only (NAudio 2.2.1, Whisper.net 1.9.1, Microsoft.ML.OnnxRuntime, CUETools.Codecs.FLAKE). **No new packages.** The live probe shells out to `nvidia-smi` and uses `NativeLibrary.TryLoad` for Vulkan.

**Prerequisite:** Stage 2b fully merged (it is — master at `e23b6e6` + VAD fix `94c386b`). Authoritative sources: `docs/plans/2026-06-30-localscribe-design.md`, `docs/specs/localscribe-specs.md` (cited as spec N).

---

## Global Constraints

These apply to **every** task; each task's requirements implicitly include them.

- **Target framework:** `net10.0-windows` for all projects (.NET 10 SDK). New console project `LocalScribe.LiveRunner` matches.
- **No new NuGet packages.** The live hardware probe uses `System.Diagnostics.Process` (nvidia-smi) and `System.Runtime.InteropServices.NativeLibrary` (vulkan-1.dll). Everything else reuses Stage 1/2a/2b code.
- **ASCII-only source — literals, identifiers, comments, and XML-doc** (user decision 2026-07-02: e.g. write "section 4", never a section glyph). Spec glyphs only via `\uXXXX` escapes. Markdown docs are exempt.
- **Invariant formatting.** Any on-disk identifier or rendered date/number formats with `CultureInfo.InvariantCulture`.
- **Determinism.** No `DateTime.Now`/`Stopwatch` in logic — inject `IClock` (session-relative ms, via a `Func<IClock>` factory since the clock is per-session) and `TimeProvider` (wall clock). ML assertions are fuzzy, never exact-string.
- **Backpressure, never drop (design).** The transcription queue stays bounded (`FullMode.Wait`); the capture-side bridge channel is **unbounded** so the capture callback thread never blocks. Audio segments are never discarded because transcription lags.
- **Evidentiary invariants (spec 1.1 + user decisions 2026-07-02):** nothing rewrites `transcript.jsonl`; `VadCore.Flush()` force-emits ANY in-progress utterance on Stop/Pause/EOF (already implemented — this plan must *route* every flush into the worker, never drop it); floor-OOM retries forever (already in `TranscriptionWorker`); dedup is render-layer only.
- **Test categories** (carried from 2b): `[UNIT]` default, no models/GPU/network, PR gate = `dotnet test --filter "Category!=Fixture"`; `[FIXTURE]` = `[Trait("Category", "Fixture")]`, real models on CPU, run explicitly; `[SMOKE]` = real devices/GPU via LiveRunner, manual runbook.
- **Commits:** conventional commits, one per task step marked *Commit*. Every commit message ends with the project trailer:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
- **Verification:** run the named `[UNIT]` filter after each implement step; run the full unit gate (`dotnet test --filter "Category!=Fixture"`) plus `dotnet build` (0 warnings) before each task's final commit.

## Scope boundary (what is NOT in 3a)

- **All UI** — WPF tray app, live-view window, recording overlay, `SessionViewModel`, hotkeys — **Stage 3b** (`docs/plans/2026-07-02-stage-3b-tray-liveview-overlay.md`). 3a's `SessionController` events (`StateChanged`, `LineInserted`, `PeakObserved`, `Notice`, `ErrorRaised`) are the exact seam 3b binds.
- **Device hot-swap mid-call** (`IMMNotificationClient`, `audio device changed` + `pinned microphone unavailable` markers), **sleep/resume markers**, **disk-full handling**, **live low-energy watchdog** — Stage 7 (design build sequence: "Hardening"). 3a emits only `paused by user`/`resumed`/`degraded: system-audio loopback` plus the worker's existing `transcription lagging`.
- **Pinned-mic selection** (`mic.mode = pinned`) — the `MicSnapshot` honestly records `FollowDefault` + the resolved device; pin support rides with the Stage 7 hot-swap work that gives pins their meaning. Settings already carry the field.
- **Startup recovery scan** (driving `SessionWriter.RecoverIfNeededAsync` at launch), **session history/Matter manager, metadata editing** — Stage 4. **Diarisation** — Stage 5. **Export** — Stage 6. **Model download UX, installer** — Stage 7.
- **Idle-timeout auto-stop and `IMeetingDetector`** — deferred with auto-detect; manual Stop is the only stop.
- **Golden-corpus WER baseline** — still the open 2b DoD item (`docs/plans/2026-07-02-stage-2b-golden-corpus.md`); not blocked by and not blocking 3a, but `PhantomBleedDedup`'s conservative `TextOnlyMinSimilarity=0.975` must be tuned against it before trusting (Task 10 keeps it conservative).

## Type ledger (single source of truth for cross-task signatures)

All new types in ns `LocalScribe.Core.Live` (folder `src/LocalScribe.Core/Live/`) unless noted.

| Type | Shape | Task |
|---|---|---|
| `SessionBootstrap` | ns `.Storage`: `static Task<SessionStartInfo> StartAsync(StoragePaths, Settings, AppKind, IReadOnlyList<SourceKind> sources, DeviceSnapshot devices, TimeProvider, string appVersion, CancellationToken)` | 1 |
| `SessionStartInfo` | ns `.Storage`: `record(string Id, SessionMeta Meta, SessionRecord LiveRecord)` | 1 |
| `CaptureFrameBridge` | `ctor(ICaptureSource)`; `IAsyncEnumerable<AudioFrame> ReadAllAsync(CancellationToken)`; `void Complete()`; `IDisposable` | 2 |
| `AlignedAudioWriter` | `ctor(IAudioFileSink sink, int sampleRate = 16000)`; `void Write(AudioFrame)`; `long SamplesWritten { get; }`; `IDisposable` | 3 |
| `AudioSessionInfo` | `record(uint Pid, string ProcessName)` | 4 |
| `RemotePlan` | `record(RemoteMode Mode, uint? Pid, string? App, bool FellBackToSystemMix, string? Notice)` | 4 |
| `RemoteCapturePlanner` | `static RemotePlan Plan(IReadOnlyList<AudioSessionInfo> active, RemoteSetting setting)` | 4 |
| `IAudioSessionScanner` / `WasapiSessionScanner` | `IReadOnlyList<AudioSessionInfo> Scan()` | 4 |
| `ICaptureSourceProvider` | `(ICaptureSource Source, MicSnapshot Snapshot) CreateMic(IClock clock);`<br>`(ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock)` | 4 |
| `WasapiCaptureSourceProvider` | `ICaptureSourceProvider` over `MicCaptureSource` + `ProcessLoopbackCapture` + planner | 4 |
| `PreflightProbe` | `static Task<float> MeasurePeakAsync(ICaptureSource, TimeSpan window, CancellationToken)`; `const float SilencePeakThreshold = 1e-4f` | 5 |
| `NvidiaSmi` | ns `.Transcription`: `static int? ParseVramMb(string? output)` | 6 |
| `LiveHardwareProbe` | ns `.Transcription`: `IHardwareProbe`; test ctor `(Func<string?> nvidiaSmi, Func<bool> vulkanPresent, int processorCount)` | 6 |
| `LiveSourcePipeline` | `ctor(SourceKind, VadOptions, Func<ISpeechProbabilityModel>, TranscriptionWorker, AlignedAudioWriter?)`; `event Action<SourceKind, float>? PeakObserved`; `void StartLeg(ICaptureSource, CancellationToken)`; `Task StopLegAndFlushAsync()` | 7 |
| `SessionState` | `enum { Idle, Recording, Paused, Finalizing }` | 8 |
| `LiveSessionOptions` | `record { AppKind App = Manual; VadOptions Vad = new(); TranscriptionWorkerOptions Worker = new(); bool RunPreflightProbe = true; }` | 8 |
| `SessionController` | see Task 8 (ctor, `State`, `CurrentSessionId`, `StartAsync`/`PauseAsync`/`ResumeAsync`/`StopAsync`, events `StateChanged`/`LineInserted`/`PeakObserved`/`ErrorRaised`/`Notice`) | 8 |
| `FakeEngineFactory` | tests: `IEngineFactory` returning a scripted `FakeTranscriptionEngine` | 7 |
| `AmplitudeSpeechModel` | tests: `ISpeechProbabilityModel` — probability 1 when any sample is non-zero, else 0 | 7 |

Existing types consumed (2a/2b, exact — do not re-declare): `ICaptureSource`, `AudioFrame(SourceKind, long StartMs, float[] Samples)`, `IClock`/`StopwatchClock`/`FakeClock`, `MicCaptureSource(IClock, Role)` (+ `.DeviceName`), `ProcessLoopbackCapture(uint, IClock)` / `.SystemLoopbackExcludingSelf(IClock)`, `FakeCaptureSource(SourceKind, float[][])`, `IAudioFileSink`/`AudioSinkFactory.Create(path, AudioFormat)`, `VadOptions`, `ISpeechProbabilityModel`, `SileroVadSegmenter(SourceKind, VadOptions, ISpeechProbabilityModel)`, `SileroVadModel(string)`, `TranscriptionWorker(IEngineFactory, BackendPlan, LanguageResolver, IClock, TranscriptionWorkerOptions)` (+ `EnqueueAsync`/`Complete`/`RunAsync`, events `SegmentTranscribed`/`MarkerRaised`/`ErrorRaised`), `BackendSelector.Select(HardwareInfo, Settings)`, `LanguageResolver(string)`, `ModelPaths.Require(string)`, `WhisperEngineFactory`, `TranscriptMerger(TranscriptStore)` (+ `InitializeAsync`/`AppendSegmentAsync`/`AppendMarkerAsync`/`View`/`LineInserted`), `TranscribedSegment(AudioSegment, TranscriptionResult, string)`, `Markers.*`, `StoragePaths`, `SessionStore`/`SessionRecord`/`DeviceSnapshot`/`MicSnapshot`/`RemoteSnapshot`, `MetadataStore`/`SessionMeta.CreateDefault(AppKind, DateTimeOffset, SessionParticipant?)`, `SessionId.New/EnsureUnique`, `SessionWriter(StoragePaths, Settings, TimeProvider)`, `Settings` (+ `Remote: RemoteSetting { RemoteMode Mode; string? App; }`, `Mic`, `AudioRetention`, `AudioFormat`, `Self`, `Vocabulary`, `Language`), `VocabularyProvider(Vocabulary, IReadOnlyDictionary<string, Matter>)`, `PhantomBleedDedup`, `NoOpDedup`, enums `SourceKind`/`AppKind`/`Backend`/`AudioFormat`/`RemoteMode`/`MicMode`/`TranscriptKind`.

---

## Task 1: Extract `SessionBootstrap` from `OfflinePipelineRunner`  [UNIT]

The live controller needs the same session-identity dance the offline runner does inline (wall-clock + timezone capture, self participant, default meta, collision-safe id, folder, live `session.json`). Extract it so both share one implementation (DRY), adding the `DeviceSnapshot` parameter the live path needs (offline passes the default).

**Files:**
- Create: `src/LocalScribe.Core/Storage/SessionBootstrap.cs`
- Modify: `src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs` (replace its inline steps 1 with the call)
- Test: `tests/LocalScribe.Core.Tests/SessionBootstrapTests.cs`

**Interfaces:**
- Consumes: `SessionMeta.CreateDefault`, `SessionId`, `SessionStore`, `MetadataStore`, `StoragePaths`, `Settings.Self`, `DeviceSnapshot`.
- Produces: `SessionBootstrap.StartAsync(StoragePaths paths, Settings settings, AppKind app, IReadOnlyList<SourceKind> sources, DeviceSnapshot devices, TimeProvider time, string appVersion, CancellationToken ct)` returning `SessionStartInfo(string Id, SessionMeta Meta, SessionRecord LiveRecord)`. The live record is already saved to disk (recovery-compatible: `EndedAtUtc == null`) and the session folder + `meta.json` exist on return. Tasks 8/9 and the (unchanged-behavior) offline runner consume this.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/SessionBootstrapTests.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-boot-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly DateTimeOffset Now = new(2026, 7, 2, 6, 32, 5, TimeSpan.Zero);

    [Fact]
    public async Task StartAsync_creates_folder_meta_and_live_record()
    {
        var paths = new StoragePaths(_root);
        var settings = new Settings { Self = new SelfIdentity { Name = "Sam", Role = "Attorney" } };
        var devices = new DeviceSnapshot
        {
            Mic = new MicSnapshot { Mode = MicMode.FollowDefault, Name = "Shure MV7" },
            Remote = new RemoteSnapshot { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost", FellBackToSystemMix = false },
        };
        var time = new ManualUtcTimeProvider(Now);

        var info = await SessionBootstrap.StartAsync(paths, settings, AppKind.Webex,
            [SourceKind.Local, SourceKind.Remote], devices, time, "0.3.0", CancellationToken.None);

        Assert.True(Directory.Exists(paths.SessionDir(info.Id)));
        Assert.StartsWith("2026-07-02_", info.Id);                 // local-wall-clock id (spec 9)
        Assert.Contains("Webex", info.Id);

        var meta = await new MetadataStore(paths.MetaJson(info.Id)).LoadAsync(CancellationToken.None);
        Assert.NotNull(meta);
        Assert.Contains(meta!.Participants, p => p.IsSelf && p.Name == "Sam" && p.Side == SourceKind.Local);

        var live = await new SessionStore(paths.SessionJson(info.Id)).ReadAsync(CancellationToken.None);
        Assert.NotNull(live);
        Assert.Null(live!.EndedAtUtc);                             // recovery-compatible live record
        Assert.Equal(AppKind.Webex, live.App);
        Assert.Equal([SourceKind.Local, SourceKind.Remote], live.Sources);
        Assert.Equal("Shure MV7", live.Devices.Mic.Name);
        Assert.Equal(RemoteMode.PerProcess, live.Devices.Remote.Mode);
        Assert.Equal("0.3.0", live.AppVersion);
    }

    [Fact]
    public async Task StartAsync_collision_gets_numeric_suffix()
    {
        var paths = new StoragePaths(_root);
        var settings = new Settings();
        var time = new ManualUtcTimeProvider(Now);

        var a = await SessionBootstrap.StartAsync(paths, settings, AppKind.Manual,
            [SourceKind.Local], new DeviceSnapshot(), time, "0.3.0", CancellationToken.None);
        var b = await SessionBootstrap.StartAsync(paths, settings, AppKind.Manual,
            [SourceKind.Local], new DeviceSnapshot(), time, "0.3.0", CancellationToken.None);

        Assert.NotEqual(a.Id, b.Id);
        Assert.StartsWith(a.Id, b.Id);                             // "...-2" suffix (spec 9)
    }
}
```

Note: `ManualUtcTimeProvider` already exists in the test project (2b). If its constructor differs (e.g. takes `DateTimeOffset` start), match it — read the existing file first and adjust only the construction line, not the assertions.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SessionBootstrapTests"`
Expected: FAIL — `SessionBootstrap` does not exist (compile error).

- [ ] **Step 3: Implement `SessionBootstrap`**

Create `src/LocalScribe.Core/Storage/SessionBootstrap.cs` — this is the offline runner's step 1, verbatim, parameterized by `app`/`sources`/`devices`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

public sealed record SessionStartInfo(string Id, SessionMeta Meta, SessionRecord LiveRecord);

/// <summary>Shared session-start bootstrap (spec 1.2/1.4/9): wall-clock + timezone capture,
/// self participant from settings, default meta.json, collision-safe folder id, and the
/// recovery-compatible live session.json (EndedAtUtc == null). Used by both the offline
/// runner and the live SessionController; only the frame source differs between them.</summary>
public static class SessionBootstrap
{
    public static async Task<SessionStartInfo> StartAsync(StoragePaths paths, Settings settings,
        AppKind app, IReadOnlyList<SourceKind> sources, DeviceSnapshot devices,
        TimeProvider time, string appVersion, CancellationToken ct)
    {
        var startedUtc = time.GetUtcNow();
        var tz = time.LocalTimeZone;
        var offset = tz.GetUtcOffset(startedUtc);
        var startedLocal = startedUtc.ToOffset(offset);

        SessionParticipant? self = string.IsNullOrEmpty(settings.Self.Name) ? null
            : new SessionParticipant
            { Id = "p-self", Name = settings.Self.Name, Role = settings.Self.Role, Side = SourceKind.Local, IsSelf = true };
        var meta = SessionMeta.CreateDefault(app, startedLocal, self);

        string id = SessionId.EnsureUnique(
            SessionId.New(startedLocal, app, meta.Title),
            x => Directory.Exists(paths.SessionDir(x)));
        Directory.CreateDirectory(paths.SessionDir(id));
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(meta, ct);

        var live = new SessionRecord
        {
            Id = id, App = app, StartedAtUtc = startedUtc,
            TimeZoneId = tz.Id, UtcOffsetMinutes = (int)offset.TotalMinutes,
            Sources = sources, AppVersion = appVersion, Language = settings.Language,
            Devices = devices,
        };
        await new SessionStore(paths.SessionJson(id)).SaveAsync(live, ct);
        return new SessionStartInfo(id, meta, live);
    }
}
```

If `SessionId.New`'s collision suffix format differs from the `StartsWith` assertion (check `SessionId.EnsureUnique`'s actual suffix separator), fix the ASSERTION to match the existing store behavior — the store is the authority, not this plan.

- [ ] **Step 4: Refactor `OfflinePipelineRunner.RunAsync` to consume it**

In `src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs`, replace the identity block (the code from `var startedUtc = _time.GetUtcNow();` through `await sessionStore.SaveAsync(live, ct);`, currently lines ~44-71) with:

```csharp
        var sources = new List<SourceKind>();
        if (options.LocalWavPath is not null) sources.Add(SourceKind.Local);
        if (options.RemoteWavPath is not null) sources.Add(SourceKind.Remote);

        var boot = await SessionBootstrap.StartAsync(_paths, _settings, AppKind.Manual,
            sources, new DeviceSnapshot(), _time, _appVersion, ct);
        string id = boot.Id;
        var live = boot.LiveRecord;
        var startedUtc = live.StartedAtUtc;
        var sessionStore = new SessionStore(_paths.SessionJson(id));
```

Add `using LocalScribe.Core.Storage;` if not present (it is). Everything downstream (`live with { ... }` finalize, `startedUtc.AddMilliseconds(duration)`) compiles unchanged.

- [ ] **Step 5: Run the new tests and the full unit gate**

Run: `dotnet test --filter "FullyQualifiedName~SessionBootstrapTests"` — Expected: PASS.
Run: `dotnet test --filter "Category!=Fixture"` — Expected: PASS (offline-runner tests prove the refactor changed nothing).
Run: `dotnet build` — Expected: 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Storage/SessionBootstrap.cs src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs tests/LocalScribe.Core.Tests/SessionBootstrapTests.cs
git commit -m "refactor: extract SessionBootstrap shared by offline and live session start"
```

---

## Task 2: `CaptureFrameBridge` — capture events to async frames  [UNIT]

`SileroVadSegmenter.SegmentAsync` consumes `IAsyncEnumerable<AudioFrame>`; real sources push `FrameAvailable` events from a capture thread. The bridge is the adapter: an unbounded channel so the capture callback NEVER blocks (design: capture never blocks on transcription; the bounded worker queue's backpressure piles up here in memory instead).

**Files:**
- Create: `src/LocalScribe.Core/Live/CaptureFrameBridge.cs`
- Test: `tests/LocalScribe.Core.Tests/CaptureFrameBridgeTests.cs`

**Interfaces:**
- Consumes: `ICaptureSource` (event only — the bridge does NOT Start/Stop/Dispose the source; the leg owner does).
- Produces: `CaptureFrameBridge(ICaptureSource source)`; `IAsyncEnumerable<AudioFrame> ReadAllAsync(CancellationToken ct)`; `void Complete()` (idempotent: unsubscribes + completes the channel so `ReadAllAsync` finishes, which is what triggers the segmenter's EOF flush); `Dispose()` = `Complete()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/CaptureFrameBridgeTests.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class CaptureFrameBridgeTests
{
    private static float[] Frame(float value) => Enumerable.Repeat(value, 512).ToArray();

    [Fact]
    public async Task Frames_pushed_before_and_after_read_starts_all_arrive_in_order()
    {
        var source = new FakeCaptureSource(SourceKind.Local, [Frame(0.1f), Frame(0.2f), Frame(0.3f)]);
        using var bridge = new CaptureFrameBridge(source);

        source.Start();                       // synchronous replay: frames land before reading
        bridge.Complete();

        var got = new List<AudioFrame>();
        await foreach (var f in bridge.ReadAllAsync(CancellationToken.None)) got.Add(f);

        Assert.Equal(3, got.Count);
        Assert.Equal([0.1f, 0.2f, 0.3f], got.Select(f => f.Samples[0]));
        Assert.Equal(0, got[0].StartMs);
        Assert.Equal(32, got[1].StartMs);     // 512 samples @ 16 kHz = 32 ms
    }

    [Fact]
    public async Task Complete_ends_enumeration_and_detaches_handler()
    {
        var source = new FakeCaptureSource(SourceKind.Local, [Frame(0.5f)]);
        using var bridge = new CaptureFrameBridge(source);
        bridge.Complete();
        bridge.Complete();                    // idempotent

        source.Start();                       // fires after Complete: must NOT throw or enqueue

        var got = new List<AudioFrame>();
        await foreach (var f in bridge.ReadAllAsync(CancellationToken.None)) got.Add(f);
        Assert.Empty(got);
    }

    [Fact]
    public async Task Cancellation_stops_enumeration()
    {
        var source = new FakeCaptureSource(SourceKind.Local, []);
        using var bridge = new CaptureFrameBridge(source);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in bridge.ReadAllAsync(cts.Token)) { }
        });
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CaptureFrameBridgeTests"`
Expected: FAIL — `LocalScribe.Core.Live` namespace does not exist (compile error).

- [ ] **Step 3: Implement the bridge**

Create `src/LocalScribe.Core/Live/CaptureFrameBridge.cs`:

```csharp
using System.Threading.Channels;
using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Live;

/// <summary>Adapts push (ICaptureSource.FrameAvailable, capture thread) to pull
/// (IAsyncEnumerable for the VAD segmenter). Unbounded on purpose: the capture callback must
/// NEVER block (design: "capture never blocks on transcription") - when transcription lags,
/// the bounded worker queue's backpressure accumulates here in memory, not on the audio
/// thread. Complete() ends the stream, which is what triggers the segmenter's EOF flush.</summary>
public sealed class CaptureFrameBridge : IDisposable
{
    private readonly ICaptureSource _source;
    private readonly Channel<AudioFrame> _channel = Channel.CreateUnbounded<AudioFrame>(
        new UnboundedChannelOptions { SingleReader = true });
    private int _completed;

    public CaptureFrameBridge(ICaptureSource source)
    {
        _source = source;
        _source.FrameAvailable += OnFrame;
    }

    private void OnFrame(AudioFrame frame) => _channel.Writer.TryWrite(frame);

    public IAsyncEnumerable<AudioFrame> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1) return;
        _source.FrameAvailable -= OnFrame;
        _channel.Writer.TryComplete();
    }

    public void Dispose() => Complete();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CaptureFrameBridgeTests"` — Expected: PASS.
Run: `dotnet test --filter "Category!=Fixture"` and `dotnet build` — Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Live/CaptureFrameBridge.cs tests/LocalScribe.Core.Tests/CaptureFrameBridgeTests.cs
git commit -m "feat: CaptureFrameBridge - capture events to async frame stream, never blocking capture"
```

---

## Task 3: `AlignedAudioWriter` — session-clock-aligned retained audio  [UNIT]

Live retained audio is written as frames arrive (the offline runner re-reads the WAV afterwards — a live session has no WAV to re-read). Pause creates a real gap in frames while the session clock keeps ticking (spec 2.1); Stage-5 diarisation maps transcript `startMs`/`endMs` back onto the retained audio by offset, so the file must stay **sample-aligned to the session clock**: before writing a frame, pad with silence up to the frame's clock position. FLAC compresses the silence to almost nothing.

**Files:**
- Create: `src/LocalScribe.Core/Live/AlignedAudioWriter.cs`
- Test: `tests/LocalScribe.Core.Tests/AlignedAudioWriterTests.cs`

**Interfaces:**
- Consumes: `IAudioFileSink` (Task owner constructs via `AudioSinkFactory.Create`).
- Produces: `AlignedAudioWriter(IAudioFileSink sink, int sampleRate = 16000)`; `void Write(AudioFrame frame)`; `long SamplesWritten { get; }` (includes padding); `Dispose()` disposes the sink.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/AlignedAudioWriterTests.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class AlignedAudioWriterTests
{
    private sealed class CollectingSink : IAudioFileSink
    {
        public readonly List<float> Samples = [];
        public bool Disposed;
        public void Write(ReadOnlySpan<float> mono16k) => Samples.AddRange(mono16k.ToArray());
        public void Dispose() => Disposed = true;
    }

    private static AudioFrame FrameAt(long startMs, float value, int samples = 512)
        => new(SourceKind.Local, startMs, Enumerable.Repeat(value, samples).ToArray());

    [Fact]
    public void Contiguous_frames_write_without_padding()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.Write(FrameAt(0, 0.1f));
        w.Write(FrameAt(32, 0.2f));           // 512 @ 16 kHz = 32 ms: exactly contiguous
        Assert.Equal(1024, w.SamplesWritten);
        Assert.Equal(1024, sink.Samples.Count);
        Assert.DoesNotContain(0f, sink.Samples);
    }

    [Fact]
    public void Gap_is_padded_with_silence_to_keep_clock_alignment()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.Write(FrameAt(0, 0.1f));
        w.Write(FrameAt(1000, 0.2f));         // pause gap: resumes at 1000 ms

        // Frame at 1000 ms must start at sample 16000 exactly.
        Assert.Equal(16000 + 512, w.SamplesWritten);
        Assert.Equal(0f, sink.Samples[512]);            // padding is silence
        Assert.Equal(0f, sink.Samples[15999]);
        Assert.Equal(0.2f, sink.Samples[16000]);        // resumed audio lands on-clock
    }

    [Fact]
    public void Small_negative_jitter_appends_without_padding_or_throwing()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.Write(FrameAt(0, 0.1f));
        w.Write(FrameAt(30, 0.2f));           // stamped 2 ms early vs 32 ms sample position
        Assert.Equal(1024, w.SamplesWritten); // appended as-is; ms-level drift accepted
    }

    [Fact]
    public void Dispose_disposes_sink()
    {
        var sink = new CollectingSink();
        new AlignedAudioWriter(sink).Dispose();
        Assert.True(sink.Disposed);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AlignedAudioWriterTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the writer**

Create `src/LocalScribe.Core/Live/AlignedAudioWriter.cs`:

```csharp
using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Live;

/// <summary>Writes retained audio keeping the file sample-aligned to the session clock:
/// a frame stamped at StartMs always begins at sample StartMs * rate / 1000, with silence
/// padding for any gap (Pause stops capture but the clock keeps ticking - spec 2.1). This is
/// what lets Stage-5 diarisation seek the retained file by transcript startMs/endMs. Frames
/// arriving slightly early (ms-level capture jitter) are appended as-is; sub-frame drift is
/// accepted rather than resampled.</summary>
public sealed class AlignedAudioWriter : IDisposable
{
    private static readonly float[] SilenceChunk = new float[1600];   // 100 ms @ 16 kHz
    private readonly IAudioFileSink _sink;
    private readonly int _sampleRate;

    public long SamplesWritten { get; private set; }

    public AlignedAudioWriter(IAudioFileSink sink, int sampleRate = 16000)
        => (_sink, _sampleRate) = (sink, sampleRate);

    public void Write(AudioFrame frame)
    {
        long expectedStart = frame.StartMs * _sampleRate / 1000;
        long gap = expectedStart - SamplesWritten;
        while (gap > 0)
        {
            int chunk = (int)Math.Min(gap, SilenceChunk.Length);
            _sink.Write(SilenceChunk.AsSpan(0, chunk));
            SamplesWritten += chunk;
            gap -= chunk;
        }
        _sink.Write(frame.Samples);
        SamplesWritten += frame.Samples.Length;
    }

    public void Dispose() => _sink.Dispose();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AlignedAudioWriterTests"` — Expected: PASS.
Run: `dotnet test --filter "Category!=Fixture"` and `dotnet build` — Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Live/AlignedAudioWriter.cs tests/LocalScribe.Core.Tests/AlignedAudioWriterTests.cs
git commit -m "feat: AlignedAudioWriter - session-clock-aligned retained audio with silence-padded pauses"
```

---

## Task 4: Remote resolution + capture-source provider  [UNIT]

Extract the Stage-1 SpikeRunner scan/match/fallback policy into a pure, tested `RemoteCapturePlanner` (spec 12.1) plus thin adapters: `WasapiSessionScanner` (enumerates active render sessions) and `WasapiCaptureSourceProvider` (constructs real sources + honest `session.json` device snapshots). The provider interface is the controller's hardware seam — tests hand it fakes.

**Files:**
- Create: `src/LocalScribe.Core/Live/RemoteCapturePlanner.cs`, `src/LocalScribe.Core/Live/WasapiSessionScanner.cs`, `src/LocalScribe.Core/Live/ICaptureSourceProvider.cs`, `src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs`
- Test: `tests/LocalScribe.Core.Tests/RemoteCapturePlannerTests.cs`

**Interfaces:**
- Consumes: `RemoteSetting` (`Settings.Remote`), `RemoteMode`, `MicSnapshot`/`RemoteSnapshot`, `MicCaptureSource`, `ProcessLoopbackCapture`.
- Produces (for Tasks 5/8/9 and the LiveRunner):
  - `record AudioSessionInfo(uint Pid, string ProcessName)` — `ProcessName` is the extensionless image name (e.g. `CiscoCollabHost`).
  - `record RemotePlan(RemoteMode Mode, uint? Pid, string? App, bool FellBackToSystemMix, string? Notice)` — `Mode` is the RESOLVED mode (`PerProcess` or `SystemMix`, never `Auto`); `Notice` is a human-readable reason when something non-default happened (fallback, no session found).
  - `static RemotePlan RemoteCapturePlanner.Plan(IReadOnlyList<AudioSessionInfo> active, RemoteSetting setting)` — pure.
  - `interface IAudioSessionScanner { IReadOnlyList<AudioSessionInfo> Scan(); }` + `WasapiSessionScanner`.
  - `interface ICaptureSourceProvider { (ICaptureSource Source, MicSnapshot Snapshot) CreateMic(IClock clock); (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock); }` + `WasapiCaptureSourceProvider(Settings settings, IAudioSessionScanner scanner)`.

**Planner policy (spec 12.1 + Stage-1 findings, encode exactly):**
- Known **priority list** (per-process candidates, first match on case-insensitive substring wins): `CiscoCollabHost`, `Webex`, `Zoom`, `ms-teams`, `msedgewebview2`, `Teams`.
- Known **full-mix set** (all-zeros / shared-audio, ALWAYS falls back even when explicitly requested — a legal recording must never silently produce an empty remote): `ms-teams`, `Teams`, `msedgewebview2`, `chrome`, `msedge`, `firefox`, `brave`, `opera`.
- `mode = SystemMix` → `SystemMix`, no notice, `FellBackToSystemMix = false` (chosen, not a fallback).
- `mode = PerProcess` with `setting.App` set → match that app among active sessions; if the matched image (or the requested app itself) is in the full-mix set, or no session matches → `SystemMix` + `FellBackToSystemMix = true` + notice.
- `mode = Auto` → scan by priority list; match not in full-mix set → `PerProcess(pid)`; match in full-mix set → `SystemMix` + `FellBackToSystemMix = true` + notice; **no match at all** → `SystemMix` + `FellBackToSystemMix = true` + notice ("no meeting app render session found; capturing full system mix").

- [ ] **Step 1: Write the failing planner tests**

Create `tests/LocalScribe.Core.Tests/RemoteCapturePlannerTests.cs`:

```csharp
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class RemoteCapturePlannerTests
{
    private static readonly AudioSessionInfo Webex = new(4242, "CiscoCollabHost");
    private static readonly AudioSessionInfo Zoom = new(5151, "Zoom");
    private static readonly AudioSessionInfo Teams = new(6161, "ms-teams");
    private static readonly AudioSessionInfo Chrome = new(7171, "chrome");

    [Fact]
    public void Auto_prefers_webex_per_process_over_zoom()
    {
        var plan = RemoteCapturePlanner.Plan([Zoom, Webex], new RemoteSetting { Mode = RemoteMode.Auto });
        Assert.Equal(RemoteMode.PerProcess, plan.Mode);
        Assert.Equal(4242u, plan.Pid);
        Assert.Equal("CiscoCollabHost", plan.App);
        Assert.False(plan.FellBackToSystemMix);
        Assert.Null(plan.Notice);
    }

    [Fact]
    public void Auto_teams_falls_back_to_system_mix_with_notice()
    {
        var plan = RemoteCapturePlanner.Plan([Teams], new RemoteSetting { Mode = RemoteMode.Auto });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.True(plan.FellBackToSystemMix);
        Assert.NotNull(plan.Notice);
        Assert.Equal("ms-teams", plan.App);
    }

    [Fact]
    public void Auto_browser_falls_back_to_system_mix()
    {
        var plan = RemoteCapturePlanner.Plan([Chrome], new RemoteSetting { Mode = RemoteMode.Auto });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.True(plan.FellBackToSystemMix);
    }

    [Fact]
    public void Auto_no_active_sessions_falls_back_to_system_mix_with_notice()
    {
        var plan = RemoteCapturePlanner.Plan([], new RemoteSetting { Mode = RemoteMode.Auto });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.True(plan.FellBackToSystemMix);
        Assert.NotNull(plan.Notice);
        Assert.Null(plan.Pid);
    }

    [Fact]
    public void Explicit_per_process_matches_requested_app()
    {
        var plan = RemoteCapturePlanner.Plan([Zoom, Webex],
            new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" });
        Assert.Equal(RemoteMode.PerProcess, plan.Mode);
        Assert.Equal(5151u, plan.Pid);
    }

    [Fact]
    public void Explicit_per_process_on_known_all_zeros_app_STILL_falls_back()
    {
        // Spec 12.1: an explicit perProcess for the known all-zeros set still auto-falls-back -
        // a legal recording must never silently produce an empty remote.
        var plan = RemoteCapturePlanner.Plan([Teams],
            new RemoteSetting { Mode = RemoteMode.PerProcess, App = "ms-teams" });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.True(plan.FellBackToSystemMix);
    }

    [Fact]
    public void Explicit_per_process_app_not_running_falls_back_with_notice()
    {
        var plan = RemoteCapturePlanner.Plan([Zoom],
            new RemoteSetting { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost" });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.True(plan.FellBackToSystemMix);
        Assert.NotNull(plan.Notice);
    }

    [Fact]
    public void Explicit_system_mix_is_chosen_not_a_fallback()
    {
        var plan = RemoteCapturePlanner.Plan([Webex], new RemoteSetting { Mode = RemoteMode.SystemMix });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.False(plan.FellBackToSystemMix);
        Assert.Null(plan.Notice);
    }

    [Fact]
    public void Matching_is_case_insensitive_substring()
    {
        var plan = RemoteCapturePlanner.Plan([new AudioSessionInfo(9, "ciscocollabhost")],
            new RemoteSetting { Mode = RemoteMode.Auto });
        Assert.Equal(RemoteMode.PerProcess, plan.Mode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RemoteCapturePlannerTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the planner (pure)**

Create `src/LocalScribe.Core/Live/RemoteCapturePlanner.cs`:

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Live;

/// <summary>An active render audio session: pid + extensionless image name.</summary>
public sealed record AudioSessionInfo(uint Pid, string ProcessName);

/// <summary>The resolved remote capture decision (spec 12.1). Mode is never Auto here.</summary>
public sealed record RemotePlan(RemoteMode Mode, uint? Pid, string? App, bool FellBackToSystemMix, string? Notice);

/// <summary>Pure spec-12.1 remote resolution: per-process for clean apps (Webex/Zoom), ALWAYS
/// system-mix for the known all-zeros/shared-audio set (Teams, browsers) - even when the user
/// explicitly asked for perProcess - and system-mix fallback when nothing matches. A legal
/// recording must never silently produce an empty remote stream.</summary>
public static class RemoteCapturePlanner
{
    // Priority order for Auto (Stage-1 finding: Webex renders call audio in CiscoCollabHost.exe).
    private static readonly string[] Priority =
        ["CiscoCollabHost", "Webex", "Zoom", "ms-teams", "msedgewebview2", "Teams"];

    // Known all-zeros (Teams registers two render sessions on one PID) or shared-audio-process
    // (browsers/webviews) images: per-process loopback is silent or bleeds - use system mix.
    private static readonly string[] FullMix =
        ["ms-teams", "Teams", "msedgewebview2", "chrome", "msedge", "firefox", "brave", "opera"];

    public static RemotePlan Plan(IReadOnlyList<AudioSessionInfo> active, RemoteSetting setting)
    {
        if (setting.Mode == RemoteMode.SystemMix)
            return new RemotePlan(RemoteMode.SystemMix, null, setting.App, FellBackToSystemMix: false, Notice: null);

        if (setting.Mode == RemoteMode.PerProcess && !string.IsNullOrEmpty(setting.App))
        {
            var match = FirstMatch(active, [setting.App]);
            if (match is null)
                return Fallback(setting.App,
                    $"requested app '{setting.App}' has no active render session; capturing full system mix");
            if (IsFullMix(match.ProcessName) || IsFullMix(setting.App))
                return Fallback(match.ProcessName,
                    $"'{match.ProcessName}' cannot be captured per-process (all-zeros/shared audio); capturing full system mix");
            return new RemotePlan(RemoteMode.PerProcess, match.Pid, match.ProcessName, false, null);
        }

        // Auto: scan by priority.
        var found = FirstMatch(active, Priority);
        if (found is null)
            return Fallback(null, "no meeting app render session found; capturing full system mix");
        if (IsFullMix(found.ProcessName))
            return Fallback(found.ProcessName,
                $"'{found.ProcessName}' cannot be captured per-process (all-zeros/shared audio); capturing full system mix");
        return new RemotePlan(RemoteMode.PerProcess, found.Pid, found.ProcessName, false, null);
    }

    private static RemotePlan Fallback(string? app, string notice)
        => new(RemoteMode.SystemMix, null, app, FellBackToSystemMix: true, Notice: notice);

    private static bool IsFullMix(string image)
        => FullMix.Any(n => image.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static AudioSessionInfo? FirstMatch(IReadOnlyList<AudioSessionInfo> active, string[] names)
    {
        foreach (var name in names)
            foreach (var s in active)
                if (s.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return s;
        return null;
    }
}
```

- [ ] **Step 4: Run planner tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RemoteCapturePlannerTests"` — Expected: PASS.

- [ ] **Step 5: Implement the thin adapters (no unit tests — Humble Objects, exercised by the LiveRunner smoke)**

Create `src/LocalScribe.Core/Live/WasapiSessionScanner.cs` — the SpikeRunner scan loop (Program.cs lines 87-110), verbatim behavior:

```csharp
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace LocalScribe.Core.Live;

public interface IAudioSessionScanner
{
    IReadOnlyList<AudioSessionInfo> Scan();
}

/// <summary>Thin adapter (Humble Object): enumerates ACTIVE render audio sessions across ALL
/// active render endpoints (meeting apps often render to the Communications device, not the
/// Multimedia default). Validated by the Stage-1 spike; exercised live by the LiveRunner
/// smoke, not unit tests.</summary>
public sealed class WasapiSessionScanner : IAudioSessionScanner
{
    public IReadOnlyList<AudioSessionInfo> Scan()
    {
        var enumerator = new MMDeviceEnumerator();
        var active = new List<AudioSessionInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            SessionCollection sessions;
            try { sessions = device.AudioSessionManager.Sessions; }
            catch { continue; }
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State != AudioSessionState.AudioSessionStateActive) continue;
                uint pid;
                try { pid = s.GetProcessID; } catch { continue; }
                if (pid == 0) continue;
                string image;
                try { image = Process.GetProcessById((int)pid).ProcessName; }
                catch { continue; }                       // process may have just exited
                active.Add(new AudioSessionInfo(pid, image));
            }
        }
        return active;
    }
}
```

Create `src/LocalScribe.Core/Live/ICaptureSourceProvider.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Live;

/// <summary>The controller's hardware seam: creates fresh capture sources per leg (a fresh
/// MicCaptureSource re-resolves the default device on Resume) plus the honest device snapshot
/// for session.json (spec 1.2/12). Tests substitute fakes.</summary>
public interface ICaptureSourceProvider
{
    (ICaptureSource Source, MicSnapshot Snapshot) CreateMic(IClock clock);
    (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock);
}
```

Create `src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using NAudio.CoreAudioApi;
namespace LocalScribe.Core.Live;

/// <summary>Thin adapter (Humble Object) over the real WASAPI sources. Re-plans the remote on
/// every call (a Resume leg re-scans - the meeting app may have changed); the caller snapshots
/// the FIRST leg's result into session.json. Pinned-mic mode is a Stage 7 concern: 3a always
/// follows the Communications default and records that honestly.</summary>
public sealed class WasapiCaptureSourceProvider : ICaptureSourceProvider
{
    private readonly Settings _settings;
    private readonly IAudioSessionScanner _scanner;

    public WasapiCaptureSourceProvider(Settings settings, IAudioSessionScanner scanner)
        => (_settings, _scanner) = (settings, scanner);

    public (ICaptureSource Source, MicSnapshot Snapshot) CreateMic(IClock clock)
    {
        var mic = new MicCaptureSource(clock, Role.Communications);
        return (mic, new MicSnapshot { Mode = MicMode.FollowDefault, Name = mic.DeviceName });
    }

    public (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock)
    {
        var plan = RemoteCapturePlanner.Plan(_scanner.Scan(), _settings.Remote);
        ICaptureSource source = plan.Mode == RemoteMode.PerProcess
            ? new ProcessLoopbackCapture(plan.Pid!.Value, clock)
            : ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock);
        return (source, new RemoteSnapshot
        { Mode = plan.Mode, App = plan.App, FellBackToSystemMix = plan.FellBackToSystemMix });
    }
}
```

Note: `MicSnapshot.Id` stays null in 3a — `MicCaptureSource` exposes only `DeviceName` today; the device ID plumbs through with the Stage-7 pin work.

- [ ] **Step 6: Full gate + commit**

Run: `dotnet test --filter "Category!=Fixture"` and `dotnet build` — Expected: PASS, 0 warnings.

```bash
git add src/LocalScribe.Core/Live/RemoteCapturePlanner.cs src/LocalScribe.Core/Live/WasapiSessionScanner.cs src/LocalScribe.Core/Live/ICaptureSourceProvider.cs src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs tests/LocalScribe.Core.Tests/RemoteCapturePlannerTests.cs
git commit -m "feat: remote capture planner (spec 12.1) + WASAPI capture-source provider seam"
```

---

## Task 5: `PreflightProbe` — ~1 s peak check before committing to record  [UNIT]

Spec 12.3: at Start, capture ~1 s per source and assert a non-zero peak (the Stage-1 SpikeRunner `localPeak`/`remotePeak` pattern) so an un-repeatable jail call never records silence. Warn-only (`SILENT_SOURCE`, spec 8.2) — the controller surfaces it, never blocks Start.

**Files:**
- Create: `src/LocalScribe.Core/Live/PreflightProbe.cs`
- Test: `tests/LocalScribe.Core.Tests/PreflightProbeTests.cs`

**Interfaces:**
- Consumes: `ICaptureSource` (probe starts/stops it; the CALLER disposes it — probe sources are throwaway instances from the provider).
- Produces: `static Task<float> PreflightProbe.MeasurePeakAsync(ICaptureSource source, TimeSpan window, CancellationToken ct)` returning the max `|sample|` observed; `const float PreflightProbe.SilencePeakThreshold = 1e-4f` (about -80 dBFS: a truly dead endpoint delivers exact zeros; a quiet-but-live mic still has room noise above this — deliberately conservative to avoid false alarms; tune with Stage-7 watchdog work).

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/PreflightProbeTests.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class PreflightProbeTests
{
    private static float[] Frame(float value) => Enumerable.Repeat(value, 512).ToArray();

    [Fact]
    public async Task Returns_peak_of_observed_frames()
    {
        var source = new FakeCaptureSource(SourceKind.Local, [Frame(0.1f), Frame(-0.6f), Frame(0.3f)]);
        float peak = await PreflightProbe.MeasurePeakAsync(source, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.Equal(0.6f, peak, precision: 5);
    }

    [Fact]
    public async Task All_zeros_source_reports_zero_peak_below_threshold()
    {
        var source = new FakeCaptureSource(SourceKind.Remote, [Frame(0f), Frame(0f)]);
        float peak = await PreflightProbe.MeasurePeakAsync(source, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.Equal(0f, peak);
        Assert.True(peak < PreflightProbe.SilencePeakThreshold);
    }

    [Fact]
    public async Task Does_not_dispose_the_source()
    {
        var source = new FakeCaptureSource(SourceKind.Local, [Frame(0.2f)]);
        await PreflightProbe.MeasurePeakAsync(source, TimeSpan.FromMilliseconds(10), CancellationToken.None);
        source.Start();                       // still usable: no ObjectDisposedException
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PreflightProbeTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the probe**

Create `src/LocalScribe.Core/Live/PreflightProbe.cs`:

```csharp
using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Live;

/// <summary>Spec 12.3 pre-flight peak probe: run a source for ~1 s and report the peak
/// amplitude so a silent endpoint (all-zeros loopback, muted/dead mic) is caught BEFORE an
/// un-repeatable call is recorded. Warn-only: the caller raises SILENT_SOURCE (spec 8.2) and
/// proceeds. Starts/stops the source; the caller owns disposal.</summary>
public static class PreflightProbe
{
    /// <summary>About -80 dBFS. A dead endpoint delivers exact zeros; real room noise on a
    /// live mic sits well above this. Conservative on purpose - false silence warnings before
    /// a legal call would train the user to ignore the warning.</summary>
    public const float SilencePeakThreshold = 1e-4f;

    public static async Task<float> MeasurePeakAsync(ICaptureSource source, TimeSpan window, CancellationToken ct)
    {
        float peak = 0f;
        void OnFrame(AudioFrame f)
        {
            for (int i = 0; i < f.Samples.Length; i++)
            {
                float a = Math.Abs(f.Samples[i]);
                if (a > peak) peak = a;       // benign race: monotonic max, torn floats impossible on x64
            }
        }

        source.FrameAvailable += OnFrame;
        try
        {
            source.Start();
            await Task.Delay(window, ct);
            source.Stop();
        }
        finally
        {
            source.FrameAvailable -= OnFrame;
        }
        return peak;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~PreflightProbeTests"` — Expected: PASS.
Run: `dotnet test --filter "Category!=Fixture"` and `dotnet build` — Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Live/PreflightProbe.cs tests/LocalScribe.Core.Tests/PreflightProbeTests.cs
git commit -m "feat: pre-flight peak probe (spec 12.3) - catch silent sources before recording"
```

---

## Task 6: `LiveHardwareProbe` — real CUDA/Vulkan/core detection  [UNIT]

2b shipped only `StaticHardwareProbe` (config-driven); the design says the live probe is the Stage-3 adapter. Pure parser (`NvidiaSmi.ParseVramMb`) + thin probe with injectable seams. Detection strategy: CUDA = `nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits` (present on every NVIDIA driver install; first GPU line wins); Vulkan = `NativeLibrary.TryLoad("vulkan-1.dll")` (loader presence — a lying loader is caught downstream by the existing `BACKEND_INIT_FAILED` cascade); `FastCores = Environment.ProcessorCount / 2` (physical-core heuristic, same default the OfflineRunner uses).

**Files:**
- Create: `src/LocalScribe.Core/Transcription/LiveHardwareProbe.cs`
- Test: `tests/LocalScribe.Core.Tests/LiveHardwareProbeTests.cs`

**Interfaces:**
- Consumes: `IHardwareProbe`, `HardwareInfo(bool HasCuda, int CudaVramMb, bool HasVulkan, int FastCores)`.
- Produces: `NvidiaSmi.ParseVramMb(string? output)` (pure) and `LiveHardwareProbe` — production ctor `()`, test ctor `(Func<string?> nvidiaSmi, Func<bool> vulkanPresent, int processorCount)`; `Probe()` caches its result (probing shells out — once per process is plenty).

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/LiveHardwareProbeTests.cs`:

```csharp
using LocalScribe.Core.Transcription;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class LiveHardwareProbeTests
{
    [Theory]
    [InlineData("4096", 4096)]
    [InlineData("4096\n", 4096)]
    [InlineData("24576\n24576\n", 24576)]     // multi-GPU: first line wins
    [InlineData(" 8192 ", 8192)]
    public void ParseVramMb_parses_nvidia_smi_output(string output, int expected)
        => Assert.Equal(expected, NvidiaSmi.ParseVramMb(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NVIDIA-SMI has failed")]
    [InlineData("not a number")]
    public void ParseVramMb_returns_null_on_garbage(string? output)
        => Assert.Null(NvidiaSmi.ParseVramMb(output));

    [Fact]
    public void Probe_with_cuda_and_vulkan()
    {
        var probe = new LiveHardwareProbe(() => "4096\n", () => true, processorCount: 12);
        var hw = probe.Probe();
        Assert.True(hw.HasCuda);
        Assert.Equal(4096, hw.CudaVramMb);
        Assert.True(hw.HasVulkan);
        Assert.Equal(6, hw.FastCores);
    }

    [Fact]
    public void Probe_without_nvidia_smi_reports_no_cuda()
    {
        var probe = new LiveHardwareProbe(() => null, () => false, processorCount: 8);
        var hw = probe.Probe();
        Assert.False(hw.HasCuda);
        Assert.Equal(0, hw.CudaVramMb);
        Assert.False(hw.HasVulkan);
        Assert.Equal(4, hw.FastCores);
    }

    [Fact]
    public void Probe_caches_and_runs_detection_once()
    {
        int calls = 0;
        var probe = new LiveHardwareProbe(() => { calls++; return "2048"; }, () => true, 4);
        probe.Probe();
        probe.Probe();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FastCores_is_at_least_one()
    {
        var probe = new LiveHardwareProbe(() => null, () => false, processorCount: 1);
        Assert.Equal(1, probe.Probe().FastCores);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~LiveHardwareProbeTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement parser + probe**

Create `src/LocalScribe.Core/Transcription/LiveHardwareProbe.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
namespace LocalScribe.Core.Transcription;

/// <summary>Pure parser for `nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits`
/// output: one integer (MiB) per GPU line; first GPU wins.</summary>
public static class NvidiaSmi
{
    public static int? ParseVramMb(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        string first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                          StringSplitOptions.TrimEntries)[0];
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mb)
            ? mb : null;
    }
}

/// <summary>Live hardware probe (the Stage-3 adapter behind IHardwareProbe): CUDA via
/// nvidia-smi (ships with every NVIDIA driver), Vulkan via loader presence (a lying loader is
/// caught downstream by the BACKEND_INIT_FAILED cascade - spec 8.2), fast cores via the
/// ProcessorCount/2 physical-core heuristic. Result cached: detection shells out.</summary>
public sealed class LiveHardwareProbe : IHardwareProbe
{
    private readonly Func<string?> _nvidiaSmi;
    private readonly Func<bool> _vulkanPresent;
    private readonly int _processorCount;
    private HardwareInfo? _cached;

    public LiveHardwareProbe()
        : this(RunNvidiaSmi, VulkanLoaderPresent, Environment.ProcessorCount) { }

    public LiveHardwareProbe(Func<string?> nvidiaSmi, Func<bool> vulkanPresent, int processorCount)
        => (_nvidiaSmi, _vulkanPresent, _processorCount) = (nvidiaSmi, vulkanPresent, processorCount);

    public HardwareInfo Probe()
    {
        if (_cached is not null) return _cached;
        int? vram = NvidiaSmi.ParseVramMb(_nvidiaSmi());
        _cached = new HardwareInfo(
            HasCuda: vram is > 0,
            CudaVramMb: vram ?? 0,
            HasVulkan: _vulkanPresent(),
            FastCores: Math.Max(1, _processorCount / 2));
        return _cached;
    }

    private static string? RunNvidiaSmi()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;                      // nvidia-smi absent = no NVIDIA driver = no CUDA
        }
    }

    private static bool VulkanLoaderPresent()
    {
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out nint handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~LiveHardwareProbeTests"` — Expected: PASS.
Run: `dotnet test --filter "Category!=Fixture"` and `dotnet build` — Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Transcription/LiveHardwareProbe.cs tests/LocalScribe.Core.Tests/LiveHardwareProbeTests.cs
git commit -m "feat: LiveHardwareProbe - real CUDA/Vulkan/core detection behind IHardwareProbe"
```

---

## Task 7: `LiveSourcePipeline` — one source's capture leg  [UNIT]

The per-source assembly the controller composes twice: a leg = fresh `ICaptureSource` -> `CaptureFrameBridge` -> (tap: retained-audio write + peak event) -> `SileroVadSegmenter` -> `worker.EnqueueAsync`. Stopping a leg completes the bridge, which ends the frame stream, which triggers the VAD EOF flush (user decision: `Flush()` force-emits ANY in-progress utterance — trailing words of a call are never dropped), and the flushed segment is enqueued before `StopLegAndFlushAsync` returns. `StartLeg` after a stop begins a new leg (Resume).

**Files:**
- Create: `src/LocalScribe.Core/Live/LiveSourcePipeline.cs`
- Test: `tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs` (+ shared test doubles `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs`)

**Interfaces:**
- Consumes: `CaptureFrameBridge` (Task 2), `AlignedAudioWriter` (Task 3), `SileroVadSegmenter`, `TranscriptionWorker.EnqueueAsync`.
- Produces: `LiveSourcePipeline(SourceKind source, VadOptions vad, Func<ISpeechProbabilityModel> vadModelFactory, TranscriptionWorker worker, AlignedAudioWriter? audioWriter)`; `event Action<SourceKind, float>? PeakObserved` (per-frame max-abs — 3b's level bars; raised from the feed task, subscribers must be cheap); `void StartLeg(ICaptureSource source, CancellationToken ct)` (takes ownership of the source for the leg — Starts it, Disposes it at leg end; throws `InvalidOperationException` if a leg is already running); `Task StopLegAndFlushAsync()` (Stop source -> Complete bridge -> await feed task, so the EOF flush segment is IN the worker queue on return; no-op when no leg is running).
- Produces (test doubles for Tasks 7-9): `AmplitudeSpeechModel` (`ISpeechProbabilityModel` returning 1 when any window sample is non-zero else 0 — turns fake frames into deterministic VAD decisions) and `FakeEngineFactory` (`IEngineFactory` returning a `FakeTranscriptionEngine`).

- [ ] **Step 1: Write the shared test doubles**

Create `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs`:

```csharp
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

namespace LocalScribe.Core.Tests;

/// <summary>Deterministic VAD stand-in: speech prob 1.0 when any sample in the window is
/// non-zero, else 0.0. Lets tests drive VadCore with amplitude-shaped fake frames.</summary>
public sealed class AmplitudeSpeechModel : ISpeechProbabilityModel
{
    public float SpeechProbability(ReadOnlySpan<float> window)
    {
        for (int i = 0; i < window.Length; i++)
            if (window[i] != 0f) return 1f;
        return 0f;
    }
    public void Reset() { }
}

/// <summary>IEngineFactory over the existing FakeTranscriptionEngine. Default transcriber
/// echoes segment identity so assertions can tie output lines to input audio.</summary>
public sealed class FakeEngineFactory : IEngineFactory
{
    private readonly Func<AudioSegment, TranscriptionResult> _transcribe;
    public int CreateCalls;

    public FakeEngineFactory(Func<AudioSegment, TranscriptionResult>? transcribe = null)
        => _transcribe = transcribe ?? (s => new TranscriptionResult(
            $"{s.Source} {s.StartMs}-{s.EndMs}", "en", 0.0));

    public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language,
        string? initialPrompt, CancellationToken ct)
    {
        CreateCalls++;
        return Task.FromResult<ITranscriptionEngine>(new FakeTranscriptionEngine(_transcribe));
    }
}
```

If the existing `FakeTranscriptionEngine` constructor signature differs (2b defined it with either a `Func<AudioSegment, TranscriptionResult>` or a scripted `IEnumerable<object>`), adapt this wrapper to the real signature — read `tests/LocalScribe.Core.Tests/FakeTranscriptionEngine.cs` first. Also check whether 2b's `TranscriptionWorkerTests` already contains an equivalent factory fake; if it does, promote/reuse it here instead of duplicating (rename into this file, update usages).

- [ ] **Step 2: Write the failing pipeline tests**

Create `tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs`. Test VAD options: `MinSpeechMs=64, MinSilenceMs=64, SpeechPadMs=0, MaxSegmentMs=15000` — with 32 ms frames (512 samples), 4 speech frames (128 ms) then 3 silence frames (96 ms) finalizes one segment; speech with no trailing silence exercises the flush path.

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class LiveSourcePipelineTests
{
    private static readonly VadOptions TestVad = new()
    { Threshold = 0.5f, MinSpeechMs = 64, MinSilenceMs = 64, SpeechPadMs = 0, MaxSegmentMs = 15000 };

    private static float[][] SpeechThenSilence(int speechFrames, int silenceFrames)
    {
        var frames = new List<float[]>();
        for (int i = 0; i < speechFrames; i++) frames.Add(Enumerable.Repeat(0.5f, 512).ToArray());
        for (int i = 0; i < silenceFrames; i++) frames.Add(new float[512]);
        return frames.ToArray();
    }

    private static (TranscriptionWorker Worker, List<TranscribedSegment> Out, Task Loop, CancellationTokenSource Cts)
        StartWorker()
    {
        var worker = new TranscriptionWorker(new FakeEngineFactory(),
            new BackendPlan(Backend.Cpu, "tiny.en"), new LanguageResolver("en"),
            new FakeClock(), new TranscriptionWorkerOptions());
        var output = new List<TranscribedSegment>();
        worker.SegmentTranscribed += ts => { lock (output) output.Add(ts); };
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return (worker, output, worker.RunAsync(cts.Token), cts);
    }

    [Fact]
    public async Task Leg_feeds_vad_segments_into_the_worker()
    {
        var (worker, output, loop, cts) = StartWorker();
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, audioWriter: null);

        var source = new FakeCaptureSource(SourceKind.Local, SpeechThenSilence(4, 3));
        pipeline.StartLeg(source, cts.Token);
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;

        Assert.Single(output);
        Assert.Equal(SourceKind.Local, output[0].Audio.Source);
    }

    [Fact]
    public async Task Stop_flushes_the_in_progress_utterance()
    {
        // Speech right up to the stop - no trailing silence. The EOF flush (user decision
        // 2026-07-02: never drop trailing audio on Stop/Pause) must still emit it.
        var (worker, output, loop, cts) = StartWorker();
        var pipeline = new LiveSourcePipeline(SourceKind.Remote, TestVad,
            () => new AmplitudeSpeechModel(), worker, audioWriter: null);

        pipeline.StartLeg(new FakeCaptureSource(SourceKind.Remote, SpeechThenSilence(6, 0)), cts.Token);
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;

        Assert.Single(output);
    }

    [Fact]
    public async Task Two_legs_produce_two_segments_and_tap_writes_audio()
    {
        var (worker, output, loop, cts) = StartWorker();
        var sinkSamples = new List<float>();
        var sink = new DelegateSink(s => sinkSamples.AddRange(s.ToArray()));
        using var audio = new AlignedAudioWriter(sink);
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, audio);
        float lastPeak = 0f;
        pipeline.PeakObserved += (_, p) => lastPeak = Math.Max(lastPeak, p);

        pipeline.StartLeg(new FakeCaptureSource(SourceKind.Local, SpeechThenSilence(4, 3)), cts.Token);
        await pipeline.StopLegAndFlushAsync();
        pipeline.StartLeg(new FakeCaptureSource(SourceKind.Local, SpeechThenSilence(4, 3)), cts.Token);
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;

        Assert.Equal(2, output.Count);
        Assert.True(sinkSamples.Count >= 2 * 7 * 512);   // both legs' frames written
        Assert.Equal(0.5f, lastPeak);
    }

    [Fact]
    public async Task StartLeg_while_running_throws()
    {
        var (worker, _, loop, cts) = StartWorker();
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, null);
        var idle = new IdleCaptureSource(SourceKind.Local);    // never emits, never completes
        pipeline.StartLeg(idle, cts.Token);
        Assert.Throws<InvalidOperationException>(
            () => pipeline.StartLeg(new FakeCaptureSource(SourceKind.Local, []), cts.Token));
        await pipeline.StopLegAndFlushAsync();
        worker.Complete();
        await loop;
    }

    [Fact]
    public async Task StopLegAndFlush_when_no_leg_is_noop()
    {
        var (worker, _, loop, _) = StartWorker();
        var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
            () => new AmplitudeSpeechModel(), worker, null);
        await pipeline.StopLegAndFlushAsync();               // must not throw
        worker.Complete();
        await loop;
    }

    private sealed class DelegateSink(Action<ReadOnlyMemory<float>> onWrite) : IAudioFileSink
    {
        public void Write(ReadOnlySpan<float> mono16k) => onWrite(mono16k.ToArray());
        public void Dispose() { }
    }

    private sealed class IdleCaptureSource(SourceKind source) : ICaptureSource
    {
        public SourceKind Source => source;
        public event Action<AudioFrame>? FrameAvailable { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~LiveSourcePipelineTests"`
Expected: FAIL — `LiveSourcePipeline` does not exist.

- [ ] **Step 4: Implement the pipeline**

Create `src/LocalScribe.Core/Live/LiveSourcePipeline.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.Core.Live;

/// <summary>One source's live capture leg: fresh ICaptureSource -> CaptureFrameBridge ->
/// tap (retained audio + peak event) -> SileroVadSegmenter -> worker.EnqueueAsync. A leg ends
/// by completing the bridge: the frame stream finishes, VadCore.Flush() force-emits the
/// in-progress utterance (never drop trailing audio - user decision 2026-07-02), and the
/// flushed segment is enqueued before StopLegAndFlushAsync returns. Start/Stop pairs may
/// repeat (Pause/Resume legs); each leg gets a fresh source and a fresh VAD model.</summary>
public sealed class LiveSourcePipeline
{
    private readonly SourceKind _source;
    private readonly VadOptions _vad;
    private readonly Func<ISpeechProbabilityModel> _vadModelFactory;
    private readonly TranscriptionWorker _worker;
    private readonly AlignedAudioWriter? _audioWriter;

    private ICaptureSource? _legSource;
    private CaptureFrameBridge? _bridge;
    private Task? _feed;

    public event Action<SourceKind, float>? PeakObserved;

    public LiveSourcePipeline(SourceKind source, VadOptions vad,
        Func<ISpeechProbabilityModel> vadModelFactory, TranscriptionWorker worker,
        AlignedAudioWriter? audioWriter)
        => (_source, _vad, _vadModelFactory, _worker, _audioWriter)
         = (source, vad, vadModelFactory, worker, audioWriter);

    public void StartLeg(ICaptureSource source, CancellationToken ct)
    {
        if (_legSource is not null)
            throw new InvalidOperationException($"{_source} leg already running.");

        _legSource = source;
        _bridge = new CaptureFrameBridge(source);
        var segmenter = new SileroVadSegmenter(_source, _vad, _vadModelFactory());
        var frames = Tap(_bridge.ReadAllAsync(ct));

        _feed = Task.Run(async () =>
        {
            await foreach (var segment in segmenter.SegmentAsync(frames, ct))
                await _worker.EnqueueAsync(segment, ct);
        }, CancellationToken.None);

        source.Start();                       // start LAST: bridge is already listening
    }

    public async Task StopLegAndFlushAsync()
    {
        if (_legSource is null) return;
        _legSource.Stop();
        _bridge!.Complete();                  // ends the stream -> segmenter EOF flush
        try
        {
            await _feed!;                     // flush segment is enqueued when this returns
        }
        finally
        {
            _bridge.Dispose();
            _legSource.Dispose();
            (_legSource, _bridge, _feed) = (null, null, null);
        }
    }

    private async IAsyncEnumerable<AudioFrame> Tap(IAsyncEnumerable<AudioFrame> frames)
    {
        await foreach (var f in frames)
        {
            _audioWriter?.Write(f);
            if (PeakObserved is { } handler)
            {
                float peak = 0f;
                for (int i = 0; i < f.Samples.Length; i++)
                {
                    float a = Math.Abs(f.Samples[i]);
                    if (a > peak) peak = a;
                }
                handler(_source, peak);
            }
            yield return f;
        }
    }
}
```

Note the `ct` passed to `StartLeg` is the session-wide feed token (the controller's C1 fault-guard token): cancelling it aborts the leg without a flush — that only happens when the worker has already faulted and the session is coming down.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~LiveSourcePipelineTests"` — Expected: PASS.
Run: `dotnet test --filter "Category!=Fixture"` and `dotnet build` — Expected: PASS, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Live/LiveSourcePipeline.cs tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs tests/LocalScribe.Core.Tests/LiveTestDoubles.cs
git commit -m "feat: LiveSourcePipeline - per-source capture leg with VAD flush on stop"
```

---

## Task 8: `SessionController` — Start/Stop lifecycle + finalize  [UNIT]

The heart of Stage 3: owns the spec-2.1 state machine and composes everything. Mirrors `OfflinePipelineRunner.RunAsync` exactly where they overlap (bootstrap, backend plan, outbox/writer loop, C1 fault guard, finalize + projections) — read that file side-by-side while implementing. This task lands Start -> Recording -> Stop -> Finalizing -> Idle with a complete, spec-shaped session folder; Task 9 adds Pause/Resume, guards, preflight, and the degraded marker.

**Files:**
- Create: `src/LocalScribe.Core/Live/SessionController.cs` (+ `SessionState`, `LiveSessionOptions` in the same file)
- Test: `tests/LocalScribe.Core.Tests/SessionControllerTests.cs`

**Interfaces:**
- Consumes: everything above — `SessionBootstrap` (1), `AlignedAudioWriter` (3), `ICaptureSourceProvider` (4), `PreflightProbe` (5, used in Task 9), `LiveSourcePipeline` (7) — plus 2a/2b: `BackendSelector`, `LanguageResolver`, `VocabularyProvider`, `TranscriptionWorker`, `TranscriptMerger`, `TranscriptStore`, `SessionStore`, `SessionWriter`, `AudioSinkFactory`, `Markers`.
- Produces (the exact seam Stage 3b binds):

```csharp
public enum SessionState { Idle, Recording, Paused, Finalizing }

public sealed record LiveSessionOptions
{
    public AppKind App { get; init; } = AppKind.Manual;
    public VadOptions Vad { get; init; } = new();
    public TranscriptionWorkerOptions Worker { get; init; } = new();
    public bool RunPreflightProbe { get; init; } = true;      // honored in Task 9
}

public sealed class SessionController
{
    public SessionController(StoragePaths paths, Settings settings, IEngineFactory engineFactory,
        Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware,
        ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion);

    public SessionState State { get; }
    public string? CurrentSessionId { get; }

    public event Action<SessionState>? StateChanged;
    public event Action<int, TranscriptLine>? LineInserted;    // re-exposed merger event (live view)
    public event Action<SourceKind, float>? PeakObserved;      // re-exposed pipeline peaks (level bars)
    public event Action<string>? ErrorRaised;                  // spec 8.2 codes (forwarded + own)
    public event Action<string>? Notice;                       // human hints (tray balloon text)

    public Task<string?> StartAsync(LiveSessionOptions options, CancellationToken ct);  // session id; null = ignored
    public Task PauseAsync(CancellationToken ct);              // Task 9
    public Task ResumeAsync(CancellationToken ct);             // Task 9
    public Task<string?> StopAsync(CancellationToken ct);      // finalized id; null = ignored
}
```

**Behavior contract (this task):**
- `StartAsync` (from Idle): create the per-session clock via `clockFactory`; create mic + remote sources via the provider (capturing both snapshots); `SessionBootstrap.StartAsync` with `sources = [Local, Remote]` and the real `DeviceSnapshot`; select the backend plan; build the vocabulary prompt (global vocabulary, no matters — matter tagging is post-hoc, Stage 4); construct worker/merger/outbox/writer-loop exactly like the offline runner (same C1 fault-guard: a linked `feedCts` cancelled when `workerLoop` faults); open `AlignedAudioWriter`s via `AudioSinkFactory` unless `AudioRetention == "never"`; start both `LiveSourcePipeline` legs; set `Recording`; return the id.
- `StopAsync` (from Recording; Paused handled in Task 9): set `Finalizing`; stop+flush both legs; `worker.Complete()`; await `workerLoop` (fault handling per offline runner: primary fault wins, writer loop never orphaned); complete the outbox; await the writer loop; dispose audio writers; finalize `session.json` (`EndedAtUtc = time.GetUtcNow()`, `DurationMs = clock.ElapsedMs` — wall duration INCLUDING pauses per spec 2.1, NOT max segment end; counts from `merger.View`; `Model` = last transcribed model ?? plan model; `Backend` = plan backend uppercased; `Language` = locked ?? settings; `RetainedAudioSources`); `SessionWriter.RegenerateProjectionsAsync`; set `Idle`; return the id.
- The outbox is `Channel.CreateUnbounded<object>()` accepting `TranscribedSegment` (append via merger, track `lastEndMs`/`lastModel`), `string` (worker marker at `lastEndMs`, offline-runner semantics), and `MarkerAt(string Message, long AtMs)` (controller markers at an explicit time — Task 9 uses it for pause/resume/degraded).
- All public methods serialize on one `SemaphoreSlim(1,1)` — states never race.
- `StartAsync` while not Idle / `StopAsync` while not Recording-or-Paused: return null + `Notice` (single-session guard, design decision 5). Wrong-state `PauseAsync`/`ResumeAsync` (Task 9): silent no-op with a `Notice`.
- Worker `ErrorRaised` forwards to controller `ErrorRaised`; worker `MarkerRaised` -> outbox; merger `LineInserted` -> controller `LineInserted`; pipeline `PeakObserved` -> controller `PeakObserved`.
- If `StartAsync` fails partway (e.g. bootstrap IO error), dispose whatever was created and rethrow with `State` back at `Idle`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/SessionControllerTests.cs`. The fake provider replays speech-then-silence through `FakeCaptureSource` (synchronous replay on `Start()` — so by the time `StartAsync` returns, all frames are already through VAD and enqueued; `StopAsync` then drains deterministically):

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-live-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly VadOptions TestVad = new()
    { Threshold = 0.5f, MinSpeechMs = 64, MinSilenceMs = 64, SpeechPadMs = 0, MaxSegmentMs = 15000 };

    private static float[][] SpeechThenSilence(int speech, int silence)
    {
        var frames = new List<float[]>();
        for (int i = 0; i < speech; i++) frames.Add(Enumerable.Repeat(0.5f, 512).ToArray());
        for (int i = 0; i < silence; i++) frames.Add(new float[512]);
        return frames.ToArray();
    }

    private sealed class FakeProvider : ICaptureSourceProvider
    {
        public Func<float[][]> LocalFrames = () => SpeechThenSilence(4, 3);
        public Func<float[][]> RemoteFrames = () => SpeechThenSilence(4, 3);
        public RemoteSnapshot RemoteSnapshot = new()
        { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost", FellBackToSystemMix = false };
        public int MicCreates, RemoteCreates;

        public (ICaptureSource, MicSnapshot) CreateMic(IClock clock)
        { MicCreates++; return (new FakeCaptureSource(SourceKind.Local, LocalFrames()),
            new MicSnapshot { Mode = MicMode.FollowDefault, Name = "Fake Mic" }); }

        public (ICaptureSource, RemoteSnapshot) CreateRemote(IClock clock)
        { RemoteCreates++; return (new FakeCaptureSource(SourceKind.Remote, RemoteFrames()), RemoteSnapshot); }
    }

    private (SessionController Controller, FakeProvider Provider, StoragePaths Paths, FakeClock Clock)
        MakeController(Settings? settings = null)
    {
        settings ??= new Settings();
        var paths = new StoragePaths(_root);
        var provider = new FakeProvider();
        var clock = new FakeClock();
        var controller = new SessionController(paths, settings, new FakeEngineFactory(),
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            provider, () => clock, new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 2, 6, 0, 0, TimeSpan.Zero)),
            "0.3.0");
        return (controller, provider, paths, clock);
    }

    private static LiveSessionOptions Options() => new()
    { App = AppKind.Webex, Vad = TestVad, RunPreflightProbe = false };

    [Fact]
    public async Task Start_then_stop_produces_finalized_session_folder()
    {
        var (c, _, paths, clock) = MakeController();
        var states = new List<SessionState>();
        c.StateChanged += s => states.Add(s);

        string? id = await c.StartAsync(Options(), CancellationToken.None);
        Assert.NotNull(id);
        Assert.Equal(SessionState.Recording, c.State);
        Assert.Equal(id, c.CurrentSessionId);

        clock.ElapsedMs = 5000;
        string? stopped = await c.StopAsync(CancellationToken.None);
        Assert.Equal(id, stopped);
        Assert.Equal(SessionState.Idle, c.State);
        Assert.Null(c.CurrentSessionId);
        Assert.Equal([SessionState.Recording, SessionState.Finalizing, SessionState.Idle], states);

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.NotNull(record!.EndedAtUtc);
        Assert.Equal(5000, record.DurationMs);              // wall clock, not max segment end
        Assert.Equal(2, record.SegmentCount);               // one per source
        Assert.Equal(AppKind.Webex, record.App);
        Assert.Equal("Fake Mic", record.Devices.Mic.Name);
        Assert.Equal([SourceKind.Local, SourceKind.Remote], record.RetainedAudioSources);
        Assert.True(File.Exists(paths.TranscriptMd(id!)));
        Assert.True(File.Exists(paths.SessionTxt(id!)));
        Assert.True(File.Exists(paths.AudioFile(id!, SourceKind.Local, AudioFormat.Flac)));
        Assert.True(File.Exists(paths.AudioFile(id!, SourceKind.Remote, AudioFormat.Flac)));
    }

    [Fact]
    public async Task Lines_flow_to_LineInserted_and_transcript_jsonl()
    {
        var (c, _, paths, _) = MakeController();
        var lines = new List<TranscriptLine>();
        c.LineInserted += (_, l) => { lock (lines) lines.Add(l); };

        string? id = await c.StartAsync(Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        Assert.Equal(2, lines.Count(l => l.Kind == TranscriptKind.Segment));
        Assert.Contains(lines, l => l.Source == TranscriptSource.Local && l.SpeakerLabel == "Me");
        Assert.Contains(lines, l => l.Source == TranscriptSource.Remote && l.SpeakerLabel == "Them");
        var stored = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Equal(2, stored.Count(l => l.Kind == TranscriptKind.Segment));
    }

    [Fact]
    public async Task Retention_never_skips_audio_files()
    {
        var (c, _, paths, _) = MakeController(new Settings { AudioRetention = "never" });
        string? id = await c.StartAsync(Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(paths.AudioFile(id!, SourceKind.Local, AudioFormat.Flac)));
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Empty(record!.RetainedAudioSources);
    }

    [Fact]
    public async Task Second_start_is_ignored_with_notice()
    {
        var (c, provider, _, _) = MakeController();
        string? notice = null;
        c.Notice += n => notice = n;

        string? first = await c.StartAsync(Options(), CancellationToken.None);
        string? second = await c.StartAsync(Options(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);                                 // single-session guard (design 5)
        Assert.NotNull(notice);
        Assert.Equal(1, provider.MicCreates);
        await c.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_when_idle_is_ignored_with_notice()
    {
        var (c, _, _, _) = MakeController();
        string? notice = null;
        c.Notice += n => notice = n;
        Assert.Null(await c.StopAsync(CancellationToken.None));
        Assert.NotNull(notice);
        Assert.Equal(SessionState.Idle, c.State);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SessionControllerTests"`
Expected: FAIL — `SessionController` does not exist.

- [ ] **Step 3: Implement the controller**

Create `src/LocalScribe.Core/Live/SessionController.cs`. Complete implementation (Pause/Resume/preflight bodies land in Task 9 — here they throw `NotImplementedException` stubs is NOT acceptable; instead implement them as the Task-9 contract describes but WITHOUT the preflight call and degraded marker, which Task 9 adds — the state plumbing below already supports them):

```csharp
using System.Threading.Channels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Live;

public enum SessionState { Idle, Recording, Paused, Finalizing }

public sealed record LiveSessionOptions
{
    public AppKind App { get; init; } = AppKind.Manual;
    public VadOptions Vad { get; init; } = new();
    public TranscriptionWorkerOptions Worker { get; init; } = new();
    public bool RunPreflightProbe { get; init; } = true;
}

/// <summary>The live session lifecycle (spec 2.1): Idle -> Recording <-> Paused -> Finalizing
/// -> Idle. Composes two LiveSourcePipelines over the shared TranscriptionWorker/TranscriptMerger,
/// mirroring OfflinePipelineRunner's outbox/writer-loop and C1 fault-guard patterns. Pause STOPS
/// capture (privilege protection - nothing is transcribed during a paused sidebar); Resume starts
/// fresh legs. The session clock keeps ticking through Pause: durationMs = wall time at Stop.
/// All public methods serialize on one semaphore; events fire from worker threads - UI adapters
/// (Stage 3b) must marshal to their dispatcher.</summary>
public sealed class SessionController
{
    private readonly StoragePaths _paths;
    private readonly Settings _settings;
    private readonly IEngineFactory _engineFactory;
    private readonly Func<ISpeechProbabilityModel> _vadModelFactory;
    private readonly IHardwareProbe _hardware;
    private readonly ICaptureSourceProvider _captureProvider;
    private readonly Func<IClock> _clockFactory;
    private readonly TimeProvider _time;
    private readonly string _appVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record MarkerAt(string Message, long AtMs);

    // Per-session state (null when Idle).
    private Session? _session;

    private sealed class Session
    {
        public required string Id;
        public required SessionRecord LiveRecord;
        public required IClock Clock;
        public required BackendPlan Plan;
        public required LanguageResolver Language;
        public required TranscriptionWorker Worker;
        public required TranscriptMerger Merger;
        public required Channel<object> Outbox;
        public required Task WriterLoop;
        public required Task WorkerLoop;
        public required CancellationTokenSource FeedCts;
        public required LiveSourcePipeline Local;
        public required LiveSourcePipeline Remote;
        public required List<AlignedAudioWriter> AudioWriters;
        public required List<SourceKind> Retained;
        public string? LastModel;
    }

    public SessionState State { get; private set; } = SessionState.Idle;
    public string? CurrentSessionId => _session?.Id;

    public event Action<SessionState>? StateChanged;
    public event Action<int, TranscriptLine>? LineInserted;
    public event Action<SourceKind, float>? PeakObserved;
    public event Action<string>? ErrorRaised;
    public event Action<string>? Notice;

    public SessionController(StoragePaths paths, Settings settings, IEngineFactory engineFactory,
        Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware,
        ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion)
        => (_paths, _settings, _engineFactory, _vadModelFactory, _hardware, _captureProvider,
            _clockFactory, _time, _appVersion)
         = (paths, settings, engineFactory, vadModelFactory, hardware, captureProvider,
            clockFactory, time, appVersion);

    private void SetState(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public async Task<string?> StartAsync(LiveSessionOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State != SessionState.Idle)
            {
                Notice?.Invoke("Already recording - stop the current session first.");
                return null;
            }

            var clock = _clockFactory();

            // Task 9 inserts the pre-flight peak probe here (options.RunPreflightProbe).

            var (micSource, micSnap) = _captureProvider.CreateMic(clock);
            var (remoteSource, remoteSnap) = _captureProvider.CreateRemote(clock);
            var devices = new DeviceSnapshot { Mic = micSnap, Remote = remoteSnap };

            var boot = await SessionBootstrap.StartAsync(_paths, _settings, options.App,
                [SourceKind.Local, SourceKind.Remote], devices, _time, _appVersion, ct);

            var plan = BackendSelector.Select(_hardware.Probe(), _settings);
            var language = new LanguageResolver(_settings.Language);
            string prompt = new VocabularyProvider(_settings.Vocabulary, new Dictionary<string, Matter>())
                .BuildInitialPrompt([]);
            var worker = new TranscriptionWorker(_engineFactory, plan, language, clock,
                options.Worker with { InitialPrompt = prompt.Length == 0 ? null : prompt });

            var merger = new TranscriptMerger(new TranscriptStore(_paths.TranscriptJsonl(boot.Id)));
            await merger.InitializeAsync(ct);
            merger.LineInserted += (i, l) => LineInserted?.Invoke(i, l);

            var outbox = Channel.CreateUnbounded<object>();
            var session = new Session
            {
                Id = boot.Id, LiveRecord = boot.LiveRecord, Clock = clock, Plan = plan,
                Language = language, Worker = worker, Merger = merger, Outbox = outbox,
                WriterLoop = Task.CompletedTask, WorkerLoop = Task.CompletedTask,
                FeedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None),
                Local = null!, Remote = null!, AudioWriters = [], Retained = [],
            };

            worker.SegmentTranscribed += ts => outbox.Writer.TryWrite(ts);
            worker.MarkerRaised += m => outbox.Writer.TryWrite(m);
            worker.ErrorRaised += e => ErrorRaised?.Invoke(e);

            session.WriterLoop = Task.Run(async () =>
            {
                long lastEndMs = 0;
                await foreach (object item in outbox.Reader.ReadAllAsync(CancellationToken.None))
                {
                    if (item is TranscribedSegment ts)
                    {
                        var line = await merger.AppendSegmentAsync(ts, CancellationToken.None);
                        lastEndMs = Math.Max(lastEndMs, line.EndMs);
                        session.LastModel = ts.ModelName;
                    }
                    else if (item is MarkerAt at)
                    {
                        await merger.AppendMarkerAsync(at.Message, at.AtMs, CancellationToken.None);
                    }
                    else if (item is string marker)
                    {
                        await merger.AppendMarkerAsync(marker, lastEndMs, CancellationToken.None);
                    }
                }
            }, CancellationToken.None);

            session.WorkerLoop = worker.RunAsync(session.FeedCts.Token);
            // C1 fault guard (see OfflinePipelineRunner): if the worker faults, the feed loops
            // are the bounded queue's only producers with no reader left - cancel feeding so
            // they abort promptly; the real exception is recovered by awaiting WorkerLoop.
            _ = session.WorkerLoop.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Cancel(),
                session.FeedCts, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            AlignedAudioWriter? localWriter = null, remoteWriter = null;
            if (_settings.AudioRetention != "never")
            {
                localWriter = new AlignedAudioWriter(AudioSinkFactory.Create(
                    _paths.AudioFile(boot.Id, SourceKind.Local, _settings.AudioFormat), _settings.AudioFormat));
                remoteWriter = new AlignedAudioWriter(AudioSinkFactory.Create(
                    _paths.AudioFile(boot.Id, SourceKind.Remote, _settings.AudioFormat), _settings.AudioFormat));
                session.AudioWriters.AddRange([localWriter, remoteWriter]);
                session.Retained.AddRange([SourceKind.Local, SourceKind.Remote]);
            }

            session.Local = new LiveSourcePipeline(SourceKind.Local, options.Vad,
                _vadModelFactory, worker, localWriter);
            session.Remote = new LiveSourcePipeline(SourceKind.Remote, options.Vad,
                _vadModelFactory, worker, remoteWriter);
            session.Local.PeakObserved += (s, p) => PeakObserved?.Invoke(s, p);
            session.Remote.PeakObserved += (s, p) => PeakObserved?.Invoke(s, p);

            // Task 9 emits the degraded marker here when remoteSnap.FellBackToSystemMix.

            session.Local.StartLeg(micSource, session.FeedCts.Token);
            session.Remote.StartLeg(remoteSource, session.FeedCts.Token);

            _session = session;
            SetState(SessionState.Recording);
            return session.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State != SessionState.Recording || _session is null)
            {
                Notice?.Invoke("Nothing to pause.");
                return;
            }
            var s = _session;
            await s.Local.StopLegAndFlushAsync();               // VAD flush: trailing words kept
            await s.Remote.StopLegAndFlushAsync();
            s.Outbox.Writer.TryWrite(new MarkerAt(Markers.PausedByUser, s.Clock.ElapsedMs));
            SetState(SessionState.Paused);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State != SessionState.Paused || _session is null)
            {
                Notice?.Invoke("Nothing to resume.");
                return;
            }
            var s = _session;
            s.Outbox.Writer.TryWrite(new MarkerAt(Markers.Resumed, s.Clock.ElapsedMs));
            var (micSource, _) = _captureProvider.CreateMic(s.Clock);      // fresh leg: re-resolves device
            var (remoteSource, _) = _captureProvider.CreateRemote(s.Clock);
            s.Local.StartLeg(micSource, s.FeedCts.Token);
            s.Remote.StartLeg(remoteSource, s.FeedCts.Token);
            SetState(SessionState.Recording);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State is not (SessionState.Recording or SessionState.Paused) || _session is null)
            {
                Notice?.Invoke("Nothing to stop.");
                return null;
            }
            var s = _session;
            bool wasPaused = State == SessionState.Paused;   // capture BEFORE SetState below
            SetState(SessionState.Finalizing);

            bool faulted = false;
            try
            {
                if (!wasPaused)                     // paused legs are already stopped+flushed
                {
                    await s.Local.StopLegAndFlushAsync();
                    await s.Remote.StopLegAndFlushAsync();
                }
                s.Worker.Complete();
                await s.WorkerLoop;                               // drained (spec 2.1 flush)
            }
            catch
            {
                faulted = true;
                throw;
            }
            finally
            {
                s.Outbox.Writer.TryComplete();
                if (faulted) { try { await s.WriterLoop; } catch { } }
                else await s.WriterLoop;

                foreach (var w in s.AudioWriters) w.Dispose();
                s.FeedCts.Dispose();
                _session = null;
            }

            long duration = s.Clock.ElapsedMs;                    // wall time incl. pauses (spec 2.1)
            await new SessionStore(_paths.SessionJson(s.Id)).SaveAsync(s.LiveRecord with
            {
                EndedAtUtc = _time.GetUtcNow(),
                DurationMs = duration,
                SegmentCount = s.Merger.View.Count(l => l.Kind == TranscriptKind.Segment),
                MarkerCount = s.Merger.View.Count(l => l.Kind == TranscriptKind.Marker),
                Model = s.LastModel ?? s.Plan.ModelName,
                Backend = s.Plan.Backend.ToString().ToUpperInvariant(),
                Language = s.Language.Locked ?? _settings.Language,
                RetainedAudioSources = s.Retained,
            }, ct);
            await new SessionWriter(_paths, _settings, _time).RegenerateProjectionsAsync(s.Id, ct);

            SetState(SessionState.Idle);
            return s.Id;
        }
        catch
        {
            // A finalize fault must not strand the controller in Finalizing forever.
            _session = null;
            SetState(SessionState.Idle);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

Implementation notes for the executor:
- The `wasPaused` capture in `StopAsync` must stay BEFORE `SetState(Finalizing)` — moving the check after it would always re-flush (harmless today only because `StopLegAndFlushAsync` no-ops without a leg, but wrong by contract).
- The `Session` class uses `required` fields with two `null!` temporaries (`Local`/`Remote`) assigned a few lines later — if the compiler or your taste objects, construct the pipelines first and inline them; behavior is identical.
- Do not hold the gate across long waits you do not control: every await inside the gate here is bounded by the session's own drain (acceptable — Stop IS the drain).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SessionControllerTests"` — Expected: PASS.
Run: `dotnet test --filter "Category!=Fixture"` and `dotnet build` — Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs
git commit -m "feat: SessionController - live Start/Stop lifecycle, finalize, spec-shaped session folder"
```

---

## Task 9: Pause/Resume, preflight, degraded marker, guard hardening  [UNIT]

Completes the controller contract: pause/resume markers + fresh legs, the pre-flight peak probe with `SILENT_SOURCE`, the `degraded: system-audio loopback` marker + notice when the remote fell back, and stop-from-Paused.

**Files:**
- Modify: `src/LocalScribe.Core/Live/SessionController.cs`
- Test: `tests/LocalScribe.Core.Tests/SessionControllerPauseTests.cs`

**Interfaces:**
- Consumes: `PreflightProbe` (Task 5), `Markers.PausedByUser`/`Resumed`/`DegradedSystemAudioLoopback`.
- Produces: final `SessionController` behavior — no signature changes.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/SessionControllerPauseTests.cs` (reuses `FakeProvider` — move it and the shared helpers from `SessionControllerTests` into `LiveTestDoubles.cs` now, making them internal to the test project, and update `SessionControllerTests` usings):

```csharp
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

    // Uses FakeProvider / AmplitudeSpeechModel / FakeEngineFactory / MakeController-style helper
    // from LiveTestDoubles.cs - identical wiring to SessionControllerTests.MakeController, with
    // the FakeClock and FakeProvider returned for manipulation.

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
```

`LiveTestDoubles.MakeController(string root)` is the `MakeController` helper from Task 8 moved verbatim (returning the same tuple, `FakeProvider` promoted to a top-level internal class), and `LiveTestDoubles.Options()` is the `Options()` helper. Keep one copy only.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SessionControllerPauseTests"`
Expected: `Degraded_remote_writes_marker_and_notice` and `Silent_source_raises_SILENT_SOURCE_but_still_starts` FAIL (marker/probe not implemented); the pause tests may already pass from Task 8's plumbing — that is fine.

- [ ] **Step 3: Implement the remaining behavior**

In `SessionController.StartAsync`, at the `// Task 9 inserts the pre-flight peak probe here` comment, insert:

```csharp
            if (options.RunPreflightProbe)
            {
                var (probeMic, _) = _captureProvider.CreateMic(clock);
                var (probeRemote, _) = _captureProvider.CreateRemote(clock);
                try
                {
                    float localPeak = await PreflightProbe.MeasurePeakAsync(
                        probeMic, TimeSpan.FromSeconds(1), ct);
                    float remotePeak = await PreflightProbe.MeasurePeakAsync(
                        probeRemote, TimeSpan.FromSeconds(1), ct);
                    if (localPeak < PreflightProbe.SilencePeakThreshold)
                    {
                        ErrorRaised?.Invoke("SILENT_SOURCE");
                        Notice?.Invoke("Microphone level is near zero - check mute/input device before relying on this recording.");
                    }
                    if (remotePeak < PreflightProbe.SilencePeakThreshold)
                    {
                        ErrorRaised?.Invoke("SILENT_SOURCE");
                        Notice?.Invoke("Remote audio level is near zero - is meeting audio actually playing?");
                    }
                }
                finally
                {
                    probeMic.Dispose();
                    probeRemote.Dispose();
                }
            }
```

At the `// Task 9 emits the degraded marker here` comment, insert:

```csharp
            if (remoteSnap.FellBackToSystemMix)
            {
                outbox.Writer.TryWrite(new MarkerAt(Markers.DegradedSystemAudioLoopback, clock.ElapsedMs));
                Notice?.Invoke("Per-process capture unavailable - recording full system audio for the remote stream (possible bleed; use headphones).");
            }
```

Note on the preflight test expectation: `FakeCaptureSource` replays ALL its frames synchronously inside the probe's `Start()`, so the probe's 1-second window costs 1 real second per source in that test — if that makes the suite feel slow, add an optional `TimeSpan probeWindow` to `LiveSessionOptions` (default 1 s) and pass `TimeSpan.FromMilliseconds(20)` in tests. Prefer that over mocking delays.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SessionControllerPauseTests"` — Expected: PASS.
Run: `dotnet test --filter "FullyQualifiedName~SessionControllerTests"` — Expected: still PASS.
Run: `dotnet test --filter "Category!=Fixture"` and `dotnet build` — Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerPauseTests.cs tests/LocalScribe.Core.Tests/LiveTestDoubles.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs
git commit -m "feat: SessionController pause/resume markers, preflight SILENT_SOURCE, degraded fallback marker"
```

---

## Task 10: Swap `PhantomBleedDedup` into `SessionWriter`  [UNIT]

The 2b runbook left `SessionWriter` on `NoOpDedup` with an explicit note: "Stage 3 swaps it." The real `PhantomBleedDedup` is implemented + tested; this task is the one-line swap plus a projection-level regression test proving the render layer now hides a phantom-bleed echo while `transcript.jsonl` keeps both lines (spec 5: render-layer only, non-destructive). Options stay at the class defaults — `TextOnlyMinSimilarity = 0.975` is deliberately conservative until tuned against the golden corpus (open 2b DoD item).

**Files:**
- Modify: `src/LocalScribe.Core/Storage/SessionWriter.cs` (line ~47)
- Test: `tests/LocalScribe.Core.Tests/SessionWriterTests.cs` (add one test)

**Interfaces:**
- Consumes: `PhantomBleedDedup()` (defaults: `NearWindowMs=750, MinSimilarity=0.85, MinRmsGapDb=3.0, TextOnlyMinSimilarity=0.975`).
- Produces: no signature change — `SessionWriter` behavior only.

- [ ] **Step 1: Write the failing test**

Add to `tests/LocalScribe.Core.Tests/SessionWriterTests.cs` (match the existing file's helper style for constructing a session folder — it already builds temp sessions for its other tests; reuse those helpers):

```csharp
    [Fact]
    public async Task Regenerate_hides_phantom_bleed_echo_in_md_but_jsonl_keeps_both()
    {
        // Remote says it loud; the mic hears the speakers say the SAME text quieter and later
        // within the near-window: classic phantom bleed (design: speakers-instead-of-headphones).
        var (paths, id) = await CreateFinalizedSessionAsync();   // existing helper pattern
        var store = new TranscriptStore(paths.TranscriptJsonl(id));
        await store.AppendAsync(TranscriptLine.Segment(seq: 0, TranscriptSource.Remote,
            startMs: 1000, endMs: 3000, "I pushed the auth changes last night.", "Them",
            lang: "en", noSpeechProb: 0.01, rmsDb: -20.0), CancellationToken.None);
        await store.AppendAsync(TranscriptLine.Segment(seq: 1, TranscriptSource.Local,
            startMs: 1200, endMs: 3100, "I pushed the auth changes last night.", "Me",
            lang: "en", noSpeechProb: 0.01, rmsDb: -31.0), CancellationToken.None);

        await new SessionWriter(paths, new Settings(), TimeProvider.System)
            .RegenerateProjectionsAsync(id, CancellationToken.None);

        string md = await File.ReadAllTextAsync(paths.TranscriptMd(id));
        Assert.Single(SplitOccurrences(md, "I pushed the auth changes last night."));
        Assert.Contains("Them:", md);                            // the louder Remote line survives
        Assert.DoesNotContain("Me:", md);                        // the bleed echo is hidden

        var lines = await store.ReadAllAsync(CancellationToken.None);
        Assert.Equal(2, lines.Count);                            // JSONL keeps both (spec 1.1)
    }

    private static string[] SplitOccurrences(string haystack, string needle)
        => haystack.Split(needle).Skip(1).Select(_ => needle).ToArray();
```

Adapt the two `TranscriptLine.Segment(...)` calls to the factory's real parameter order/names (check `src/LocalScribe.Core/Model/TranscriptLine.cs`), and `CreateFinalizedSessionAsync` to whatever helper the existing tests in this file actually use — the assertions are the contract, not the scaffolding. If the existing `SessionWriterTests` fixture writes lines BEFORE finalizing, follow that order.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SessionWriterTests"`
Expected: the new test FAILS (both lines render — NoOpDedup); existing tests PASS.

- [ ] **Step 3: Make the swap**

In `src/LocalScribe.Core/Storage/SessionWriter.cs`, change:

```csharp
        var projection = new TranscriptProjection(
            new VocabularyProvider(_settings.Vocabulary, mattersById), new NoOpDedup());
```

to:

```csharp
        // Render-layer phantom-bleed dedup (spec 5): non-destructive - JSONL keeps both copies.
        // Defaults are known-conservative (TextOnlyMinSimilarity 0.975, user decision 2026-07-02);
        // tune against the golden corpus before loosening.
        var projection = new TranscriptProjection(
            new VocabularyProvider(_settings.Vocabulary, mattersById), new PhantomBleedDedup());
```

- [ ] **Step 4: Run the full unit gate**

Run: `dotnet test --filter "Category!=Fixture"` — Expected: PASS. If any EXISTING projection/renderer test fails because its fixture accidentally looks like phantom bleed (same text, close times, RMS gap), that test's fixture data — not the dedup — should be adjusted to be honestly distinct (different text or comparable energy), preserving the test's original intent.
Run: `dotnet build` — Expected: 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Storage/SessionWriter.cs tests/LocalScribe.Core.Tests/SessionWriterTests.cs
git commit -m "feat: enable PhantomBleedDedup in SessionWriter projections (render-layer, conservative)"
```

---

## Task 11: `LocalScribe.LiveRunner` console app + smoke runbook  [SMOKE]

The Stage-3a demo: a console app that records a REAL meeting live — keyboard Start/Pause/Resume/Stop, live transcript lines printing as they finalize, level peaks, and a finished spec-shaped session folder. This is the manual smoke gate before 3b puts a UI on the same controller. Console apps default to an MTA main thread, which `ProcessLoopbackCapture.Start()` requires (Stage-1 note) — 3b's WPF host must create sources off the STA thread; the LiveRunner needs no special handling.

**Files:**
- Create: `src/LocalScribe.LiveRunner/LocalScribe.LiveRunner.csproj`, `src/LocalScribe.LiveRunner/Program.cs`
- Modify: `LocalScribe.slnx` (add the project — copy the existing OfflineRunner entry's format)
- Create: `docs/plans/2026-07-02-stage-3a-smoke-runbook.md`

**Interfaces:**
- Consumes: `SessionController` + `WasapiCaptureSourceProvider` + `WasapiSessionScanner` + `LiveHardwareProbe` + `WhisperEngineFactory` + `SileroVadModel` + `ModelPaths` + `SettingsStore`.
- Produces: the demo binary; no library surface.

- [ ] **Step 1: Create the project**

Create `src/LocalScribe.LiveRunner/LocalScribe.LiveRunner.csproj` (mirror the OfflineRunner csproj exactly — same TFM, same three `Whisper.net.Runtime*` package refs, project ref to Core):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\LocalScribe.Core\LocalScribe.Core.csproj" />
    <PackageReference Include="Whisper.net.Runtime" Version="1.9.1" />
    <PackageReference Include="Whisper.net.Runtime.Cuda.Windows" Version="1.9.1" />
    <PackageReference Include="Whisper.net.Runtime.Vulkan" Version="1.9.1" />
  </ItemGroup>
</Project>
```

Add it to `LocalScribe.slnx` following the existing project entries' XML shape. Verify with `dotnet build` at the solution root.

- [ ] **Step 2: Implement `Program.cs`**

Create `src/LocalScribe.LiveRunner/Program.cs`:

```csharp
// src/LocalScribe.LiveRunner/Program.cs
//
// Stage 3a manual smoke harness: records a REAL meeting live through the full pipeline.
// MTA main thread (console default, no [STAThread]) - required by ProcessLoopbackCapture.
//
// Keys:  R = start   P = pause/resume   S = stop (finalize)   Q = quit
// Flags: --model <name>  --backend <auto|cuda|vulkan|cpu>  --vram <mb>  --no-preflight
//        --app <image>   (explicit perProcess target, e.g. CiscoCollabHost)
//        --system-mix    (force full-system EXCLUDE-self remote capture)

using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using Whisper.net.LibraryLoader;

// Host responsibility (see OfflineRunner): set the native backend order once.
RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var settingsPath = Path.Combine(Environment.GetFolderPath(
    Environment.SpecialFolder.ApplicationData), "LocalScribe", "settings.json");
var settings = await new SettingsStore(settingsPath).LoadOrDefaultAsync(default);
if (Arg("--model") is { } model) settings = settings with { Model = model };
if (Arg("--backend") is { } backend)
    settings = settings with { Backend = Enum.Parse<Backend>(backend, ignoreCase: true) };
if (args.Contains("--system-mix"))
    settings = settings with { Remote = settings.Remote with { Mode = RemoteMode.SystemMix } };
else if (Arg("--app") is { } app)
    settings = settings with { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = app } };

IHardwareProbe hardware = Arg("--vram") is { } vram && int.TryParse(vram, out int mb)
    ? new StaticHardwareProbe(new HardwareInfo(mb > 0, mb, false, Environment.ProcessorCount / 2))
    : new LiveHardwareProbe();
var hw = hardware.Probe();
Console.WriteLine($"Hardware: cuda={hw.HasCuda} vram={hw.CudaVramMb}MB vulkan={hw.HasVulkan} fastCores={hw.FastCores}");
Console.WriteLine($"Backend plan: {BackendSelector.Select(hw, settings)}");

string appVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
var controller = new SessionController(
    new StoragePaths(settings.StorageRoot), settings, new WhisperEngineFactory(),
    () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
    hardware, new WasapiCaptureSourceProvider(settings, new WasapiSessionScanner()),
    () => new StopwatchClock(), TimeProvider.System, appVersion);

static string Ts(long ms) => TimeSpan.FromMilliseconds(ms).ToString(
    ms >= 3_600_000 ? @"h\:mm\:ss" : @"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);

controller.StateChanged += s => Console.WriteLine($"-- state: {s}");
controller.Notice += n => Console.WriteLine($"-- notice: {n}");
controller.ErrorRaised += e => Console.WriteLine($"-- error: {e}");
controller.LineInserted += (_, line) => Console.WriteLine(
    line.Kind == TranscriptKind.Marker
        ? $"  [{Ts(line.StartMs)}] _[{line.Text}]_"
        : $"  [{Ts(line.StartMs)}] {line.SpeakerLabel}: {line.Text}");

var options = new LiveSessionOptions
{ App = AppKind.Webex, RunPreflightProbe = !args.Contains("--no-preflight") };

Console.WriteLine("R = start, P = pause/resume, S = stop, Q = quit");
while (true)
{
    var key = Console.ReadKey(intercept: true).Key;
    try
    {
        switch (key)
        {
            case ConsoleKey.R:
                string? id = await controller.StartAsync(options, default);
                if (id is not null) Console.WriteLine($"recording -> {id}");
                break;
            case ConsoleKey.P:
                if (controller.State == SessionState.Paused) await controller.ResumeAsync(default);
                else await controller.PauseAsync(default);
                break;
            case ConsoleKey.S:
                string? stopped = await controller.StopAsync(default);
                if (stopped is not null)
                    Console.WriteLine($"finalized -> {new StoragePaths(settings.StorageRoot).SessionDir(stopped)}");
                break;
            case ConsoleKey.Q:
                if (controller.State is SessionState.Recording or SessionState.Paused)
                    await controller.StopAsync(default);
                return 0;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAULT: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
}
```

If `TranscriptSource`/`TranscriptKind` usings or the `RuntimeOptions` API shape differ from the OfflineRunner's actual usage, mirror the OfflineRunner — it is the working reference for the Whisper.net host setup.

- [ ] **Step 3: Build + unit gate**

Run: `dotnet build` — Expected: 0 warnings, LiveRunner compiles.
Run: `dotnet test --filter "Category!=Fixture"` — Expected: PASS.

- [ ] **Step 4: Write the smoke runbook**

Create `docs/plans/2026-07-02-stage-3a-smoke-runbook.md`:

```markdown
# Stage 3a smoke runbook — LiveRunner on real hardware

Prereqs: models fetched (`tools/fetch-models.ps1`), a mic, and something playing audio.
Run from an MTA console: `dotnet run --project src/LocalScribe.LiveRunner`

## S1 - Solo smoke (no meeting): mic + any audio playback
1. Play any audio (YouTube/media player). Expect a startup `-- notice:` that no meeting app
   was found and the remote fell back to system mix.
2. `R` - preflight runs (~2 s); expect NO SILENT_SOURCE if both mic and playback are live.
3. Speak a sentence; wait ~2 s: expect a `Me:` line. Playback speech yields `Them:` lines.
4. `P`, talk while paused (nothing must appear), `P` again, talk (lines resume).
5. `S` - expect `finalized -> <folder>`; verify the folder: transcript.jsonl (paused/resumed
   markers with correct ms + degraded marker at 0), transcript.md, session.txt, meta.json,
   session.json (endedAtUtc set, durationMs ~= wall time, devices snapshot honest),
   local.flac + remote.flac (open remote.flac: pause gap must be silence, timeline aligned).

## S2 - Webex per-process (the primary use case)
1. Join a real Webex call (CiscoCollabHost.exe rendering).
2. `R` - expect NO degraded notice; session.json devices.remote.mode = "perProcess".
3. Converse both directions; verify Me/Them attribution and no phantom-bleed duplicates in
   transcript.md when using HEADPHONES; repeat on SPEAKERS and verify the dedup hides echoes.
4. `S`; verify folder as in S1 (no degraded marker this time).

## S3 - Teams / browser fallback
1. Join a Teams meeting (or a browser call). `R` - expect the degraded notice + marker and
   devices.remote.fellBackToSystemMix = true; remote.flac must NOT be silent.

## S4 - CUDA + downgrade sanity (GTX 1650 4 GB box)
1. `dotnet run --project src/LocalScribe.LiveRunner -- --backend cuda` - startup line should
   show cuda=True and the plan pick small.en; watch for VRAM_OOM downgrades under load.
2. Optional floor check: `--backend cpu --model tiny.en` must keep up (RTF < 1) on two streams.

## S5 - Silent-source preflight
1. Mute the mic in Windows. `R` - expect `-- error: SILENT_SOURCE` + notice, and recording
   still starts (warn-only).

Record results (pass/fail + notes) inline here, per run, dated.
```

- [ ] **Step 5: Execute the smoke (S1 minimum) on the dev box**

Run S1 end-to-end. S2 needs a real Webex call — schedule with the user if none is available at execution time; S1 + unit suite is the merge bar, S2 before Stage 3b's own smoke.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.LiveRunner/ LocalScribe.slnx docs/plans/2026-07-02-stage-3a-smoke-runbook.md
git commit -m "feat: LiveRunner console app + Stage 3a smoke runbook (real live-capture demo)"
```

---

## Task ordering & parallelism

Dependencies: 1 -> 8; 2 -> 3? no — 2, 3, 4, 5, 6 are mutually independent (all consumed by 7/8); 7 needs 2+3; 8 needs 1+4+7; 9 needs 5+8; 10 is independent of everything (any time); 11 needs 6+9+10.

Suggested order for a single executor: 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11. For parallel subagents: {1, 2+3, 4, 5, 6, 10} in wave one, {7} then {8, 9} then {11}.

## Definition of Done (Stage 3a)

- Full unit gate green (`dotnet test --filter "Category!=Fixture"`), `dotnet build` 0 warnings.
- Fixture suite still green (`dotnet test --filter "Category=Fixture"` — models required).
- Smoke S1 executed and recorded in the runbook; S2 (real Webex) before 3b merges.
- `SessionWriter` renders through `PhantomBleedDedup`; `transcript.jsonl` untouched by it.
- No new packages; ASCII-only source; every commit carries the project trailer.




