# Stage 2b: Offline Pipeline — VAD, Whisper.net, Merge, Runner — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the complete offline transcription pipeline — Silero VAD segmentation, Whisper.net transcription behind the backend cascade with VRAM-OOM/RTF auto-downgrade, the seq-assigning `TranscriptMerger`, the real phantom-bleed `IRenderDedup`, FLAC/WAV retained-audio writing, and a console `OfflineRunner` that turns a `local.wav`+`remote.wav` pair into a complete, spec-shaped session folder — with all logic unit-tested hardware-free and the ML-dependent adapters gated behind fixture tests.

**Architecture:** Extends `LocalScribe.Core` with three new namespaces — `Vad` (pure §4 state machine + thin Silero ONNX adapter), `Transcription` (backend/model selection, `ITranscriptionEngine` + Whisper.net adapter, language lock, the bounded-channel worker), and `Pipeline` (`AudioSegment`, merger, offline runner) — plus `PhantomBleedDedup` in the existing `Projection` namespace and `IAudioFileSink` (WAV/FLAC) in `Audio`. Humble Object throughout: every ONNX/whisper.cpp/GPU touch lives in a razor-thin adapter behind an interface; every decision (VAD boundaries, cascade, downgrade, merge order, dedup) is pure C# tested with fakes. A new `LocalScribe.OfflineRunner` console app wires the real adapters for the Stage-2 demo.

**Tech Stack:** .NET 10 (`net10.0-windows`), Whisper.net 1.9.1 (+ CPU/CUDA/Vulkan runtimes), Microsoft.ML.OnnxRuntime (Silero VAD), CUETools.Codecs.FLAKE (FLAC encode), NAudio 2.2.1 (existing), xUnit 2.9.3.

**Prerequisite:** the **Stage 2a plan is fully executed** (`docs/plans/2026-07-02-stage-2a-schema-persistence-projection.md`). Every 2a type this plan consumes — `TranscriptLine`, `Markers`, `TranscriptStore`, `SessionRecord`/`SessionStore`, `SessionMeta`/`MetadataStore`, `Settings`, `StoragePaths`, `SessionId`, `SessionWriter`, `IRenderDedup`/`ProjectedSegment`, `IVocabularyProvider`/`VocabularyProvider`, `AtomicFile`, enums (`Backend`, `AudioFormat`, `TranscriptSource`, `AppKind`) — must exist and its suite must be green before Task 1 here.

Authoritative sources: `docs/plans/2026-06-30-localscribe-design.md` (architecture, error handling, testing strategy) and `docs/specs/localscribe-specs.md` (cited §N: §3 model table, §4 VAD, §5 merge, §8 markers/errors, §1.1 JSONL).

---

## Global Constraints

These apply to **every** task; each task's requirements implicitly include them.

- **Target framework:** `net10.0-windows` for all projects (.NET 10 SDK).
- **New packages (this stage only — exact versions):**
  - `LocalScribe.Core`: `Whisper.net` **1.9.1** (managed core, MIT), `Microsoft.ML.OnnxRuntime` **latest 1.x** (MIT), `CUETools.Codecs.FLAKE` **1.0.5** (LGPL-3.0 — see license note).
  - `LocalScribe.Core.Tests`: `Whisper.net.Runtime` **1.9.1** (CPU, for fixture tests).
  - `LocalScribe.OfflineRunner`: `Whisper.net.Runtime` **1.9.1**, `Whisper.net.Runtime.Cuda.Windows` **1.9.1**, `Whisper.net.Runtime.Vulkan` **1.9.1**.
  - Nothing else. NAudio 2.2.1 and CsWin32 stay as-is.
- **License note (CUETools.Codecs.FLAKE, LGPL-3.0):** managed-only, consumed as an unmodified NuGet assembly, dynamically linked. Do **not** IL-merge or trim it (`<TrimmerRootAssembly>`/no trimming for it at packaging time — Stage 6 concern, note it in the csproj comment). The app stays MIT.
- **Test categories.** Three tiers (design "Testing strategy"):
  - `[UNIT]` — default; no models, no GPU, no network. Runs in `dotnet test --filter "Category!=Fixture"` (the PR gate; existing Stage 1/2a tests carry no trait and are included automatically).
  - `[FIXTURE]` — real Silero/Whisper on CPU; needs model files fetched by `tools/fetch-models.ps1` (Task 0). Marked `[Trait("Category", "Fixture")]`, excluded from the PR gate, run explicitly with `dotnet test --filter "Category=Fixture"`. A fixture test that cannot find its model files **throws** with the fetch instruction — never a silent pass.
  - `[SMOKE]` — real GPU (CUDA/Vulkan cascade) via the OfflineRunner on a live box; manual runbook, not automated.
- **Model files are never committed.** They live under `<repo>/models/` (gitignored, Task 0) or the `LOCALSCRIBE_MODELS` env-var override; `ModelPaths` (Task 0) is the single resolver.
- **ASCII-only source (literals + identifiers).** No Unicode emojis anywhere. Spec glyphs only via `\uXXXX` escapes (carried from Stage 1/2a). No new glyphs are needed in 2b — marker text comes from the 2a `Markers` constants.
- **Invariant formatting.** Any on-disk identifier or rendered date/number formats with `CultureInfo.InvariantCulture` (carried from 2a).
- **Determinism.** No `DateTime.Now`/`Stopwatch` in logic — inject the existing `IClock` (session-relative ms) and `TimeProvider` (wall clock). ML assertions are fuzzy (thresholds/keywords), never exact-string, and pin model + quantization (design "Determinism controls").
- **Backpressure, never drop (design).** The transcription queue is bounded and **waits** (`BoundedChannelFullMode.Wait`); audio segments are never discarded because transcription lags.
- **Evidentiary invariant (spec §1.1).** Nothing in this stage rewrites `transcript.jsonl`; the phantom-bleed dedup is **render-layer only**.
- **Commits:** Conventional commits (`feat:`/`test:`/`chore:`/`docs:`). One commit per task step marked *Commit*. Every commit message ends with the project trailer (append to each `git commit -m` below):
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
- **Verification:** run the named `[UNIT]` filter after each implement step; run the full unit gate (`dotnet test --filter "Category!=Fixture"`) plus `dotnet build` (0 warnings) before each task's final commit. Fixture steps say so explicitly.

## Scope boundary (what is NOT in 2b)

- **Live wiring** — real-time capture→pipeline plumbing, the live-view UI binding, the recording overlay, pre-flight peak probe wiring, device hot-swap markers (`audio device changed`, `pinned microphone unavailable`, `paused*`/`resumed` emission) — **Stage 3/7**. 2b's merger exposes the sorted-insert seam Stage 3 binds.
- **Real hardware probing** — CUDA/VRAM/Vulkan detection runs through the `IHardwareProbe` seam; 2b ships `StaticHardwareProbe` (config-driven) and the pure §3 selector. Live probing lands with Stage 3; GPU behavior is `[SMOKE]` via the OfflineRunner.
- **Model download/SHA-pinning UX** — first-run bundled model + lazy download with retry is **Stage 7** (packaging); 2b uses local model files via `ModelPaths` + the dev fetch script.
- **Diarisation** (sherpa-onnx) — **Stage 5**. **`.zip`/`.docx` export** — Stage 6/fast-follow. **Meeting auto-detect** — deferred seam.
- **Mid-meeting language switching** — unsupported in v1 (spec §3); the language lock is probe-then-commit only.

## Type ledger (single source of truth for cross-task signatures)

| Type | Shape | Task |
|---|---|---|
| `AudioSegment` | `record AudioSegment(SourceKind Source, long StartMs, long EndMs, ReadOnlyMemory<float> Pcm)` — ns `LocalScribe.Core.Pipeline` | 1 |
| `SegmentAudio.RmsDb` | `static double RmsDb(ReadOnlySpan<float> pcm)` (floor −90.0) | 1 |
| `TranscriptLine.RmsDb` | `double? RmsDb` QA field + factory param (2a type, extended) | 2 |
| `VadOptions` | record: `Threshold=0.5f, MinSpeechMs=250, MinSilenceMs=500, SpeechPadMs=150, MaxSegmentMs=15000, WindowSizeSamples=512, SampleRate=16000` | 3 |
| `ISpeechProbabilityModel` | `float SpeechProbability(ReadOnlySpan<float> window); void Reset();` | 3 |
| `VadCore` | `IReadOnlyList<AudioSegment> Push(AudioFrame frame)`, `AudioSegment? Flush()` | 3 |
| `IVadSegmenter` / `SileroVadSegmenter` | `IAsyncEnumerable<AudioSegment> SegmentAsync(IAsyncEnumerable<AudioFrame>, CancellationToken)` | 4 |
| `SileroVadModel` | `ISpeechProbabilityModel` over ONNX Runtime | 4 |
| `HardwareInfo` | `record(bool HasCuda, int CudaVramMb, bool HasVulkan, int FastCores)` | 5 |
| `IHardwareProbe` / `StaticHardwareProbe` | `HardwareInfo Probe()` | 5 |
| `BackendPlan` | `record(Backend Backend, string ModelName)` (2a `Backend` enum) | 5 |
| `BackendSelector.Select` | `static BackendPlan Select(HardwareInfo hw, Settings settings)` | 5 |
| `ModelLadder.Downgrade` | `static string? Downgrade(string modelName)` (null at floor) | 5 |
| `TranscriptionResult` | `record(string Text, string? DetectedLanguage, double? NoSpeechProb)` | 6 |
| `ITranscriptionEngine` | `string ModelName { get; }` + `Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct)`; `IAsyncDisposable` | 6 |
| `VramOutOfMemoryException` | `Exception` subtype thrown by engines on VRAM OOM | 6 |
| `IEngineFactory` | `Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? initialPrompt, CancellationToken ct)` | 6 |
| `FakeTranscriptionEngine` | scripted `ITranscriptionEngine` (tests, in test project) | 6 |
| `WhisperNetEngine` | real adapter over Whisper.net | 6 |
| `LanguageResolver` | `Observe(string? detected)`, `bool IsLocked`, `string? Locked`, `bool UseEnglishOnlyModel` | 7 |
| `TranscribedSegment` | `record(AudioSegment Audio, TranscriptionResult Result, string ModelName)` | 8 |
| `TranscriptionWorker` | `EnqueueAsync`, `RunAsync(ct)`, `Complete()`, events `SegmentTranscribed`, `MarkerRaised(string)`, `ErrorRaised(string)` | 8 |
| `TranscriptMerger` | `InitializeAsync`, `AppendSegmentAsync(TranscribedSegment, ct)`, `AppendMarkerAsync(string, long, ct)`, `View`, `LineInserted(int, TranscriptLine)`, `static int FindInsertIndex(...)` | 9 |
| `TextDistance` | `static double NormalizedSimilarity(string a, string b)` 0..1; `static int Levenshtein<T>(...)`; `static string Normalize(string)` | 10 |
| `PhantomBleedDedup` | `IRenderDedup` (2a interface) + `PhantomBleedOptions` | 10 |
| `IAudioFileSink` | `void Write(ReadOnlySpan<float> mono16k)`; `IDisposable` — `WavAudioSink`, `FlacAudioSink`, `AudioSinkFactory` | 11 |
| `WavFileFrameReader` | `static IEnumerable<AudioFrame> ReadFrames(string wavPath, SourceKind source)` (512-sample frames) | 12 |
| `OfflinePipelineRunner` | `Task<string> RunAsync(OfflineRunOptions options, CancellationToken ct)` → sessionId; `record OfflineRunOptions { string? LocalWavPath; string? RemoteWavPath; VadOptions Vad; TranscriptionWorkerOptions Worker; }` | 12 |
| `WerCalculator.Wer` | `static double Wer(string reference, string hypothesis)` | 14 |
| `ModelPaths` | `static string Resolve(string fileName)` (env override → `<repo>/models`) | 0 |

---

## Task 0: Packages, model fetch script, fixture gating  [UNIT]

**Files:**
- Modify: `src/LocalScribe.Core/LocalScribe.Core.csproj`, `tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj`, `.gitignore`
- Create: `tools/fetch-models.ps1`, `src/LocalScribe.Core/Transcription/ModelPaths.cs`
- Test: `tests/LocalScribe.Core.Tests/ModelPathsTests.cs`

**Interfaces:**
- Produces: `static class ModelPaths { static string ModelsRoot { get; } static string Resolve(string fileName); }` (ns `LocalScribe.Core.Transcription`). `ModelsRoot` = `%LOCALSCRIBE_MODELS%` when set, else `<current dir upward-searched repo root>/models` — implemented as: env var, else walk up from `AppContext.BaseDirectory` to the first directory containing `LocalScribe.slnx` and use `<that>/models`, else `<BaseDirectory>/models`. `Resolve` combines and returns the full path (existence is the caller's concern — fixture tests throw a helpful message).

- [ ] **Step 1: Add the packages**

In `src/LocalScribe.Core/LocalScribe.Core.csproj`, add to the existing `<ItemGroup>` with PackageReferences:

```xml
    <PackageReference Include="Whisper.net" Version="1.9.1" />
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.22.0" />
    <!-- LGPL-3.0: unmodified NuGet assembly, dynamically linked. Never IL-merge or trim
         this assembly (Stage 6 packaging must exclude it from trimming). -->
    <PackageReference Include="CUETools.Codecs.FLAKE" Version="1.0.5" />
```

(If `Microsoft.ML.OnnxRuntime` 1.22.0 is not the latest 1.x at implementation time, use the newest 1.x.)

In `tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj`, add:

```xml
    <PackageReference Include="Whisper.net.Runtime" Version="1.9.1" />
```

- [ ] **Step 2: Gitignore the models dir**

Append to `.gitignore`:

```
# ML model files (fetched by tools/fetch-models.ps1; never committed)
models/
```

- [ ] **Step 3: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/ModelPathsTests.cs
using LocalScribe.Core.Transcription;

public class ModelPathsTests
{
    [Fact]
    public void Env_override_wins()
    {
        string prev = Environment.GetEnvironmentVariable("LOCALSCRIBE_MODELS") ?? "";
        try
        {
            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", @"C:\mlmodels");
            Assert.Equal(@"C:\mlmodels\silero_vad.onnx", ModelPaths.Resolve("silero_vad.onnx"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS",
                prev.Length == 0 ? null : prev);
        }
    }

    [Fact]
    public void Default_root_ends_with_models_and_is_absolute()
    {
        string prev = Environment.GetEnvironmentVariable("LOCALSCRIBE_MODELS") ?? "";
        try
        {
            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", null);
            string p = ModelPaths.Resolve("ggml-tiny.en.bin");
            Assert.True(Path.IsPathFullyQualified(p));
            Assert.Equal("models", Path.GetFileName(Path.GetDirectoryName(p)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS",
                prev.Length == 0 ? null : prev);
        }
    }
}
```

- [ ] **Step 4: Run to verify failure** — `dotnet test --filter ModelPathsTests` -> FAIL (type not defined).

- [ ] **Step 5: Implement**

```csharp
// src/LocalScribe.Core/Transcription/ModelPaths.cs
namespace LocalScribe.Core.Transcription;

/// <summary>Single resolver for local ML model files (dev/fixture use; Stage 7 owns
/// download + SHA pinning). Env var LOCALSCRIBE_MODELS overrides; else "models/" at the
/// repo root (found by walking up to LocalScribe.slnx); else "models/" beside the binary.</summary>
public static class ModelPaths
{
    public static string ModelsRoot
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("LOCALSCRIBE_MODELS");
            if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var d = dir; d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
                    return Path.Combine(d.FullName, "models");
            return Path.Combine(AppContext.BaseDirectory, "models");
        }
    }

    public static string Resolve(string fileName) => Path.Combine(ModelsRoot, fileName);

    /// <summary>Fixture-test guard: returns the path or throws with the fetch instruction.</summary>
    public static string Require(string fileName)
    {
        string path = Resolve(fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Model file missing: {path}. Run tools/fetch-models.ps1 first " +
                "(or set LOCALSCRIBE_MODELS).", path);
        return path;
    }
}
```

- [ ] **Step 6: Write the fetch script**

```powershell
# tools/fetch-models.ps1
# Downloads the dev/fixture model files into <repo>/models (gitignored).
# Stage 7 (packaging) owns production download + SHA pinning; this is dev tooling only.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root 'models'
New-Item -ItemType Directory -Force $models | Out-Null

$files = @(
    @{ Name = 'silero_vad.onnx'
       Url  = 'https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx' },
    @{ Name = 'ggml-tiny.en.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin' },
    @{ Name = 'ggml-base.en.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin' }
)

foreach ($f in $files) {
    $dest = Join-Path $models $f.Name
    if (Test-Path $dest) { Write-Host "exists: $($f.Name)"; continue }
    Write-Host "fetching: $($f.Name)"
    Invoke-WebRequest -Uri $f.Url -OutFile $dest
    $sha = (Get-FileHash $dest -Algorithm SHA256).Hash
    Write-Host "  sha256: $sha"
}
Write-Host "done -> $models"
```

- [ ] **Step 7: Run to verify pass** — `dotnet test --filter ModelPathsTests` -> PASS. Also `dotnet build` -> 0 warnings (packages restore cleanly).

- [ ] **Step 8: Commit**

```bash
git add src/LocalScribe.Core/LocalScribe.Core.csproj tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj \
        .gitignore tools/fetch-models.ps1 src/LocalScribe.Core/Transcription/ModelPaths.cs \
        tests/LocalScribe.Core.Tests/ModelPathsTests.cs
git commit -m "chore: Stage 2b packages (Whisper.net, OnnxRuntime, FLAKE) + model fetch tooling"
```

---

## Task 1: AudioSegment + SegmentAudio (RMS dB)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Pipeline/AudioSegment.cs`, `src/LocalScribe.Core/Pipeline/SegmentAudio.cs`
- Test: `tests/LocalScribe.Core.Tests/SegmentAudioTests.cs`

**Interfaces:**
- Consumes: existing `LocalScribe.Core.Audio.SourceKind`.
- Produces (ns `LocalScribe.Core.Pipeline`):
  - `sealed record AudioSegment(SourceKind Source, long StartMs, long EndMs, ReadOnlyMemory<float> Pcm)` — the design's VAD-output contract, verbatim.
  - `static class SegmentAudio { static double RmsDb(ReadOnlySpan<float> pcm); }` — 20·log10(rms), clamped to a **−90.0 floor** (silence/empty input returns −90.0). Used for the JSONL `rmsDb` QA field (Task 2) and the phantom-bleed energy clause (Task 10).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/SegmentAudioTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;

public class SegmentAudioTests
{
    [Fact]
    public void Full_scale_square_wave_is_zero_db()
    {
        var pcm = new float[1600];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = i % 2 == 0 ? 1f : -1f;
        Assert.Equal(0.0, SegmentAudio.RmsDb(pcm), 1);
    }

    [Fact]
    public void Half_scale_is_about_minus_six_db()
    {
        var pcm = new float[1600];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = i % 2 == 0 ? 0.5f : -0.5f;
        Assert.Equal(-6.02, SegmentAudio.RmsDb(pcm), 1);
    }

    [Fact]
    public void Silence_and_empty_clamp_to_floor()
    {
        Assert.Equal(-90.0, SegmentAudio.RmsDb(new float[1600]));
        Assert.Equal(-90.0, SegmentAudio.RmsDb(ReadOnlySpan<float>.Empty));
    }

    [Fact]
    public void AudioSegment_carries_the_design_contract()
    {
        var seg = new AudioSegment(SourceKind.Remote, 1000, 2000, new float[16000]);
        Assert.Equal(SourceKind.Remote, seg.Source);
        Assert.Equal(1000, seg.StartMs);
        Assert.Equal(2000, seg.EndMs);
        Assert.Equal(16000, seg.Pcm.Length);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter SegmentAudioTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Pipeline/AudioSegment.cs
using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Pipeline;

/// <summary>A finalized VAD utterance: 16 kHz mono PCM with session-relative padded
/// onset/offset times (design "Components & interfaces"; spec §4).</summary>
public sealed record AudioSegment(SourceKind Source, long StartMs, long EndMs, ReadOnlyMemory<float> Pcm);
```
```csharp
// src/LocalScribe.Core/Pipeline/SegmentAudio.cs
namespace LocalScribe.Core.Pipeline;

/// <summary>PCM-level measurements on a segment.</summary>
public static class SegmentAudio
{
    public const double FloorDb = -90.0;

    /// <summary>RMS energy in dBFS (0 dB = full-scale), clamped to the -90 dB floor.</summary>
    public static double RmsDb(ReadOnlySpan<float> pcm)
    {
        if (pcm.Length == 0) return FloorDb;
        double sum = 0;
        foreach (float s in pcm) sum += (double)s * s;
        double rms = Math.Sqrt(sum / pcm.Length);
        if (rms <= 0) return FloorDb;
        return Math.Max(FloorDb, 20.0 * Math.Log10(rms));
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter SegmentAudioTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Pipeline tests/LocalScribe.Core.Tests/SegmentAudioTests.cs
git commit -m "feat: AudioSegment record + RMS dB measurement"
```

---

## Task 2: TranscriptLine.rmsDb QA field (+ spec table row)  [UNIT]

The design's phantom-bleed dedup clause is energy-aware ("a Local segment that closely matches a near-simultaneous **lower-energy** Remote segment"). Energy exists only at pipeline time (PCM in hand), so the merger persists a per-segment RMS as a **QA field** in the JSONL — additive, tolerated-unknown by all readers (spec §1.1 JSONL policy), null for markers and for pre-2b lines. No schema-version bump is needed.

**Files:**
- Modify: `src/LocalScribe.Core/Model/TranscriptLine.cs` (2a Task 2 output), `docs/specs/localscribe-specs.md` (§1.1 field table)
- Test: extend `tests/LocalScribe.Core.Tests/TranscriptLineTests.cs`

**Interfaces:**
- Produces: `TranscriptLine` gains `public double? RmsDb { get; init; }`; the `Segment(...)` factory gains a final optional parameter `double? rmsDb = null`. The `Marker(...)` factory never sets it.

- [ ] **Step 1: Write the failing tests** (append to `TranscriptLineTests`)

```csharp
    [Fact]
    public void RmsDb_roundtrips_and_is_omitted_when_null()
    {
        var with = TranscriptLine.Segment(1, TranscriptSource.Local, 0, 500, "hi", "Me", rmsDb: -23.4);
        string json = JsonSerializer.Serialize(with, LocalScribeJson.Options);
        Assert.Contains("\"rmsDb\": -23.4", json);

        var without = TranscriptLine.Segment(2, TranscriptSource.Local, 0, 500, "hi", "Me");
        Assert.DoesNotContain("rmsDb", JsonSerializer.Serialize(without, LocalScribeJson.Options));

        var back = JsonSerializer.Deserialize<TranscriptLine>(json, LocalScribeJson.Options)!;
        Assert.Equal(-23.4, back.RmsDb);
    }
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter TranscriptLineTests` -> FAIL (no `RmsDb`).

- [ ] **Step 3: Implement**

In `src/LocalScribe.Core/Model/TranscriptLine.cs`: add the property after `NoSpeechProb`:

```csharp
    /// <summary>Segment RMS energy in dBFS at transcription time (QA field, spec §1.1).
    /// Feeds the render-layer phantom-bleed dedup; null for markers and legacy lines.</summary>
    public double? RmsDb { get; init; }
```

Change the `Segment` factory signature to:

```csharp
    public static TranscriptLine Segment(int seq, TranscriptSource source, long startMs, long endMs,
        string text, string speakerLabel, string? lang = null, double? noSpeechProb = null,
        double? rmsDb = null)
```

and add `RmsDb = rmsDb,` to its initializer block.

- [ ] **Step 4: Update the spec table**

In `docs/specs/localscribe-specs.md` §1.1, add a row to the field table directly after the `noSpeechProb` row:

```markdown
| `rmsDb` | float? | Segment RMS energy in dBFS at transcription time (QA field; feeds the render-layer phantom-bleed dedup — §5). Null for markers and pre-2b lines. |
```

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter TranscriptLineTests` -> PASS (all 2a assertions still green).

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Model/TranscriptLine.cs tests/LocalScribe.Core.Tests/TranscriptLineTests.cs \
        docs/specs/localscribe-specs.md
git commit -m "feat: rmsDb QA field on transcript segments (spec 1.1) for energy-aware dedup"
```

---

## Task 3: VadCore — pure spec-4 segmentation state machine  [UNIT]

The heart of 2b. All boundary logic (threshold, min-speech, min-silence, padding, max-cut, flush) is a pure class fed 512-sample windows and a scripted probability model; the ONNX adapter (Task 4) merely supplies real probabilities.

**Files:**
- Create: `src/LocalScribe.Core/Vad/VadOptions.cs`, `src/LocalScribe.Core/Vad/ISpeechProbabilityModel.cs`, `src/LocalScribe.Core/Vad/VadCore.cs`
- Test: `tests/LocalScribe.Core.Tests/VadCoreTests.cs`

**Interfaces:**
- Consumes: `AudioFrame`, `SourceKind` (Stage 1), `AudioSegment` (Task 1).
- Produces (ns `LocalScribe.Core.Vad`):
  - `sealed record VadOptions { float Threshold = 0.5f; int MinSpeechMs = 250; int MinSilenceMs = 500; int SpeechPadMs = 150; int MaxSegmentMs = 15000; int WindowSizeSamples = 512; int SampleRate = 16000; }` (spec §4 defaults verbatim).
  - `interface ISpeechProbabilityModel { float SpeechProbability(ReadOnlySpan<float> window); void Reset(); }` — window length = `WindowSizeSamples`.
  - `sealed class VadCore(SourceKind source, VadOptions options, ISpeechProbabilityModel model)`:
    - `IReadOnlyList<AudioSegment> Push(AudioFrame frame)` — buffers samples, processes whole 512-sample windows, returns zero or more finalized segments.
    - `AudioSegment? Flush()` — force-emits the in-progress padded utterance (Stop/Pause/EOF, spec §4); null when nothing qualifying is in progress. Resets state either way.
- **Window math (documented contract):** one window = 512 samples = **32 ms** @ 16 kHz. Ms thresholds convert to windows by ceiling division: pad = ceil(150/32) = **5**, minSilence = ceil(500/32) = **16**, minSpeech = ceil(250/32) = **8**, maxSegment = floor(15000/32) = **468** windows. Times: `startMs = anchor + firstPaddedWindowIndex * 32`, `endMs = startMs + emittedWindowCount * 32`, where `anchor` is the first pushed frame's `StartMs`.
- **Behavior (spec §4, exactly):**
  1. Below threshold while idle → window joins a rolling **pre-roll** of `pad` windows.
  2. First window ≥ threshold → utterance starts; the pre-roll (≤ pad windows) is prepended (leading pad).
  3. While in speech, every window is accumulated. Sub-threshold windows increment a silence run (and record the **last dip** position); a window ≥ threshold resets the run.
  4. Silence run reaching `minSilence` windows ends the utterance: keep the trailing **pad** silence windows, drop the rest of the run. If the utterance's **speech** windows (count of ≥-threshold windows) < `minSpeech` → drop it entirely (blip).
  5. Reaching `maxSegment` accumulated windows force-cuts: at the **last dip** if one exists, else a hard cut at max; the remainder seeds the next in-progress utterance (still in speech).
  6. `Flush()` emits the in-progress utterance as-is if its speech windows ≥ `minSpeech`.
- VAD is **per source and speaker-count-agnostic** — one `VadCore` per stream, no participant-count input (spec §4).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/VadCoreTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Vad;

public class VadCoreTests
{
    private const int Win = 512;                      // 32 ms @ 16 kHz
    private const int WinMs = 32;

    /// <summary>Scripted model: probability per window index; default 0.</summary>
    private sealed class ScriptedProbe : ISpeechProbabilityModel
    {
        private readonly Func<int, float> _probOf;
        private int _i;
        public ScriptedProbe(Func<int, float> probOf) => _probOf = probOf;
        public float SpeechProbability(ReadOnlySpan<float> window) => _probOf(_i++);
        public void Reset() => _i = 0;
    }

    private static VadCore Sut(Func<int, float> probOf, VadOptions? o = null) =>
        new(SourceKind.Local, o ?? new VadOptions(), new ScriptedProbe(probOf));

    /// <summary>Push n windows of dummy PCM as one frame starting at startMs.</summary>
    private static List<AudioSegment> PushWindows(VadCore vad, int n, long startMs = 0)
    {
        var all = new List<AudioSegment>();
        all.AddRange(vad.Push(new AudioFrame(SourceKind.Local, startMs, new float[n * Win])));
        return all;
    }

    [Fact]
    public void Speech_burst_emits_one_padded_segment()
    {
        // windows 10..29 speech (20 windows = 640 ms), silence elsewhere.
        var vad = Sut(i => i is >= 10 and < 30 ? 0.9f : 0.1f);
        var segs = PushWindows(vad, 60);

        var s = Assert.Single(segs);
        // leading pad: 5 windows before onset -> starts at window 5
        Assert.Equal(5 * WinMs, s.StartMs);
        // 5 pre-roll + 20 speech + 5 trailing pad = 30 windows
        Assert.Equal(30 * Win, s.Pcm.Length);
        Assert.Equal(s.StartMs + 30 * WinMs, s.EndMs);
    }

    [Fact]
    public void Short_blip_below_minSpeech_is_dropped()
    {
        // 4 speech windows (128 ms) < minSpeech (250 ms -> 8 windows)
        var vad = Sut(i => i is >= 10 and < 14 ? 0.9f : 0.1f);
        Assert.Empty(PushWindows(vad, 60));
        Assert.Null(vad.Flush());
    }

    [Fact]
    public void Silence_only_emits_nothing()
    {
        var vad = Sut(_ => 0.05f);
        Assert.Empty(PushWindows(vad, 100));
        Assert.Null(vad.Flush());
    }

    [Fact]
    public void Long_monologue_is_force_cut_at_max_and_continues()
    {
        // continuous speech for 600 windows (19.2 s) then silence
        var vad = Sut(i => i < 600 ? 0.9f : 0.1f);
        var segs = PushWindows(vad, 700);

        Assert.Equal(2, segs.Count);
        // first segment hard-cut at maxSegment (no dip existed): 468 windows
        Assert.Equal(468 * Win, segs[0].Pcm.Length);
        // remainder continues seamlessly: second starts where the first ended
        Assert.Equal(segs[0].EndMs, segs[1].StartMs);
    }

    [Fact]
    public void Flush_force_emits_in_progress_utterance()
    {
        // speech from window 10, stream ends mid-utterance
        var vad = Sut(i => i >= 10 ? 0.9f : 0.1f);
        Assert.Empty(PushWindows(vad, 40));           // still in speech, nothing final
        var s = vad.Flush();
        Assert.NotNull(s);
        Assert.Equal(5 * WinMs, s!.StartMs);          // leading pad honored
        Assert.Equal(35 * Win, s.Pcm.Length);         // 5 pre-roll + 30 speech windows
    }

    [Fact]
    public void Anchor_comes_from_first_frame_startMs()
    {
        var vad = Sut(i => i is >= 1 and < 12 ? 0.9f : 0.1f);
        var segs = PushWindows(vad, 40, startMs: 100_000);
        var s = Assert.Single(segs);
        Assert.Equal(100_000 + 0 * WinMs, s.StartMs); // onset at window 1; pre-roll has 1 window -> starts at window 0
    }

    [Fact]
    public void Partial_frames_are_carried_across_pushes()
    {
        // Two frames of 1.5 windows each -> 3 whole windows total processed.
        var vad = Sut(_ => 0.9f);
        vad.Push(new AudioFrame(SourceKind.Local, 0, new float[Win + Win / 2]));
        vad.Push(new AudioFrame(SourceKind.Local, 48, new float[Win + Win / 2]));
        var s = vad.Flush();
        Assert.NotNull(s);
        Assert.Equal(3 * Win, s!.Pcm.Length);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter VadCoreTests` -> FAIL.

- [ ] **Step 3: Implement the options + model seam**

```csharp
// src/LocalScribe.Core/Vad/VadOptions.cs
namespace LocalScribe.Core.Vad;

/// <summary>Silero VAD segmentation parameters - spec §4 defaults ("starting defaults,
/// tune in Stage 2"). All tuning happens here, never inline.</summary>
public sealed record VadOptions
{
    public float Threshold { get; init; } = 0.5f;
    public int MinSpeechMs { get; init; } = 250;
    public int MinSilenceMs { get; init; } = 500;
    public int SpeechPadMs { get; init; } = 150;
    public int MaxSegmentMs { get; init; } = 15000;
    public int WindowSizeSamples { get; init; } = 512;
    public int SampleRate { get; init; } = 16000;
}
```
```csharp
// src/LocalScribe.Core/Vad/ISpeechProbabilityModel.cs
namespace LocalScribe.Core.Vad;

/// <summary>Humble-object seam: the only thing the ONNX adapter provides. One call per
/// 512-sample window; stateful models keep their recurrence internally.</summary>
public interface ISpeechProbabilityModel
{
    float SpeechProbability(ReadOnlySpan<float> window);
    void Reset();
}
```

- [ ] **Step 4: Implement VadCore**

```csharp
// src/LocalScribe.Core/Vad/VadCore.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Vad;

/// <summary>Pure spec-§4 utterance segmentation over 512-sample windows. Per source,
/// speaker-count-agnostic. All times derive from the first frame's StartMs anchor plus
/// a window counter (32 ms per window @ 16 kHz) - no wall clock.</summary>
public sealed class VadCore
{
    private readonly SourceKind _source;
    private readonly VadOptions _o;
    private readonly ISpeechProbabilityModel _model;
    private readonly int _padWin, _minSilenceWin, _minSpeechWin, _maxWin;
    private readonly long _winMs;

    private readonly List<float> _carry = new();          // partial-window carry between pushes
    private readonly Queue<float[]> _preRoll = new();     // rolling pad windows while idle
    private readonly List<float[]> _current = new();      // padded in-progress utterance
    private long _anchorMs = long.MinValue;               // stream time of window index 0
    private long _windowIndex;                            // absolute processed-window counter
    private long _utteranceStartWin;                      // absolute index of first padded window
    private int _speechWin;                               // windows >= threshold in current utterance
    private int _silenceRun;                              // consecutive sub-threshold windows (in speech)
    private int _lastDipIndex = -1;                       // index into _current of last sub-threshold window

    public VadCore(SourceKind source, VadOptions options, ISpeechProbabilityModel model)
    {
        (_source, _o, _model) = (source, options, model);
        _winMs = 1000L * _o.WindowSizeSamples / _o.SampleRate;
        _padWin = (int)Math.Ceiling(_o.SpeechPadMs / (double)_winMs);
        _minSilenceWin = (int)Math.Ceiling(_o.MinSilenceMs / (double)_winMs);
        _minSpeechWin = (int)Math.Ceiling(_o.MinSpeechMs / (double)_winMs);
        _maxWin = (int)(_o.MaxSegmentMs / _winMs);
        _model.Reset();
    }

    private bool InSpeech => _current.Count > 0;

    public IReadOnlyList<AudioSegment> Push(AudioFrame frame)
    {
        if (_anchorMs == long.MinValue) _anchorMs = frame.StartMs;
        _carry.AddRange(frame.Samples);

        List<AudioSegment>? emitted = null;
        while (_carry.Count >= _o.WindowSizeSamples)
        {
            var win = _carry.GetRange(0, _o.WindowSizeSamples).ToArray();
            _carry.RemoveRange(0, _o.WindowSizeSamples);
            var seg = ProcessWindow(win);
            if (seg is not null) (emitted ??= new()).Add(seg);
        }
        return (IReadOnlyList<AudioSegment>?)emitted ?? Array.Empty<AudioSegment>();
    }

    public AudioSegment? Flush()
    {
        AudioSegment? seg = null;
        if (InSpeech && _speechWin >= _minSpeechWin)
            seg = Emit(_current.Count);
        ResetUtterance(clearPreRoll: true);
        _model.Reset();
        _anchorMs = long.MinValue;
        _windowIndex = 0;
        _carry.Clear();
        return seg;
    }

    private AudioSegment? ProcessWindow(float[] win)
    {
        float p = _model.SpeechProbability(win);
        AudioSegment? emitted = null;

        if (!InSpeech)
        {
            _preRoll.Enqueue(win);
            while (_preRoll.Count > _padWin) _preRoll.Dequeue();
            if (p >= _o.Threshold)
            {
                _current.AddRange(_preRoll);                       // leading pad (spec 4)
                _utteranceStartWin = _windowIndex - (_preRoll.Count - 1);
                _preRoll.Clear();
                _speechWin = 1;
                _silenceRun = 0;
                _lastDipIndex = -1;
            }
        }
        else
        {
            _current.Add(win);
            if (p >= _o.Threshold)
            {
                _speechWin++;
                _silenceRun = 0;
            }
            else
            {
                _silenceRun++;
                _lastDipIndex = _current.Count - 1;
                if (_silenceRun >= _minSilenceWin)
                {
                    // end of utterance: keep pad windows of the silence tail, drop the rest
                    int keep = _current.Count - (_silenceRun - _padWin);
                    emitted = _speechWin >= _minSpeechWin ? Emit(keep) : null;
                    ResetUtterance(clearPreRoll: false);
                }
            }

            if (InSpeech && _current.Count >= _maxWin)
                emitted = ForceCut() ?? emitted;
        }

        _windowIndex++;
        return emitted;
    }

    private AudioSegment? ForceCut()
    {
        // cut at the last dip if one exists, else hard cut at max (spec 4)
        int cut = _lastDipIndex > 0 ? _lastDipIndex : _current.Count;
        var seg = _speechWin >= _minSpeechWin ? Emit(cut) : null;

        // remainder seeds the next in-progress utterance, still in speech
        var remainder = _current.GetRange(cut, _current.Count - cut);
        long remainderStart = _utteranceStartWin + cut;
        ResetUtterance(clearPreRoll: false);
        if (remainder.Count > 0)
        {
            _current.AddRange(remainder);
            _utteranceStartWin = remainderStart;
            _speechWin = remainder.Count;              // conservative: treat carried windows as speech
        }
        return seg;
    }

    private AudioSegment Emit(int windowCount)
    {
        windowCount = Math.Min(windowCount, _current.Count);
        var pcm = new float[windowCount * _o.WindowSizeSamples];
        for (int i = 0; i < windowCount; i++)
            _current[i].CopyTo(pcm, i * _o.WindowSizeSamples);
        long startMs = _anchorMs + _utteranceStartWin * _winMs;
        long endMs = startMs + windowCount * _winMs;
        return new AudioSegment(_source, startMs, endMs, pcm);
    }

    private void ResetUtterance(bool clearPreRoll)
    {
        _current.Clear();
        _speechWin = 0;
        _silenceRun = 0;
        _lastDipIndex = -1;
        if (clearPreRoll) _preRoll.Clear();
    }
}
```

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter VadCoreTests` -> PASS. If a boundary assertion is off by one window, fix the **code** to the documented window math (the tests encode the contract), not the tests.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Vad tests/LocalScribe.Core.Tests/VadCoreTests.cs
git commit -m "feat: pure VAD segmentation core (threshold/pad/min-silence/max-cut/flush, spec 4)"
```

---

## Task 4: SileroVadModel (ONNX adapter) + IVadSegmenter wrapper  [UNIT + FIXTURE]

**Files:**
- Create: `src/LocalScribe.Core/Vad/SileroVadModel.cs`, `src/LocalScribe.Core/Vad/IVadSegmenter.cs`, `src/LocalScribe.Core/Vad/SileroVadSegmenter.cs`
- Test: `tests/LocalScribe.Core.Tests/SileroVadFixtureTests.cs` (fixture), `tests/LocalScribe.Core.Tests/VadSegmenterTests.cs` (unit)

**Interfaces:**
- Consumes: `ISpeechProbabilityModel`, `VadCore`, `VadOptions`, `AudioFrame`, `AudioSegment`, `ModelPaths`.
- Produces (ns `LocalScribe.Core.Vad`):
  - `sealed class SileroVadModel : ISpeechProbabilityModel, IDisposable` — `SileroVadModel(string onnxPath)`. Wraps `Microsoft.ML.OnnxRuntime.InferenceSession` over `silero_vad.onnx` (v5 graph): inputs `input` `[1,512]` float, `state` `[2,1,128]` float, `sr` `[1]` int64 (16000); outputs `output` `[1,1]` probability and `stateN` (fed back). `Reset()` zeroes the state.
  - `interface IVadSegmenter { IAsyncEnumerable<AudioSegment> SegmentAsync(IAsyncEnumerable<AudioFrame> frames, CancellationToken ct); }` — the design's interface, stream-in/segments-out.
  - `sealed class SileroVadSegmenter(SourceKind source, VadOptions options, ISpeechProbabilityModel model) : IVadSegmenter` — thin async wrapper over `VadCore`: pushes each frame, yields finalized segments, and yields the `Flush()` residual at end-of-stream (spec §4 EOF flush).

- [ ] **Step 1: Write the unit test for the wrapper** (scripted model; no ONNX)

```csharp
// tests/LocalScribe.Core.Tests/VadSegmenterTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Vad;

public class VadSegmenterTests
{
    private sealed class AlwaysSpeech : ISpeechProbabilityModel
    {
        public float SpeechProbability(ReadOnlySpan<float> window) => 0.9f;
        public void Reset() { }
    }

    private static async IAsyncEnumerable<AudioFrame> Frames(int windows)
    {
        yield return new AudioFrame(SourceKind.Remote, 0, new float[windows * 512]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task End_of_stream_flushes_the_residual_utterance()
    {
        var seg = new SileroVadSegmenter(SourceKind.Remote, new VadOptions(), new AlwaysSpeech());
        var got = new List<LocalScribe.Core.Pipeline.AudioSegment>();
        await foreach (var s in seg.SegmentAsync(Frames(windows: 20), default))
            got.Add(s);

        var only = Assert.Single(got);                 // nothing finalized live; EOF flush emits
        Assert.Equal(SourceKind.Remote, only.Source);
        Assert.Equal(20 * 512, only.Pcm.Length);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter VadSegmenterTests` -> FAIL.

- [ ] **Step 3: Implement the interface + wrapper**

```csharp
// src/LocalScribe.Core/Vad/IVadSegmenter.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Vad;

/// <summary>Design seam: an async frame stream in, finalized utterances out (spec §4).</summary>
public interface IVadSegmenter
{
    IAsyncEnumerable<AudioSegment> SegmentAsync(IAsyncEnumerable<AudioFrame> frames, CancellationToken ct);
}
```
```csharp
// src/LocalScribe.Core/Vad/SileroVadSegmenter.cs
using System.Runtime.CompilerServices;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Vad;

/// <summary>Thin async wrapper over VadCore: pushes frames, yields finalized segments,
/// and force-flushes the in-progress utterance at end-of-stream (spec §4 EOF flush).</summary>
public sealed class SileroVadSegmenter : IVadSegmenter
{
    private readonly SourceKind _source;
    private readonly VadOptions _options;
    private readonly ISpeechProbabilityModel _model;

    public SileroVadSegmenter(SourceKind source, VadOptions options, ISpeechProbabilityModel model)
        => (_source, _options, _model) = (source, options, model);

    public async IAsyncEnumerable<AudioSegment> SegmentAsync(
        IAsyncEnumerable<AudioFrame> frames, [EnumeratorCancellation] CancellationToken ct)
    {
        var core = new VadCore(_source, _options, _model);
        await foreach (var frame in frames.WithCancellation(ct))
            foreach (var seg in core.Push(frame))
                yield return seg;

        if (core.Flush() is { } residual)
            yield return residual;
    }
}
```

- [ ] **Step 4: Implement the ONNX adapter**

```csharp
// src/LocalScribe.Core/Vad/SileroVadModel.cs
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
namespace LocalScribe.Core.Vad;

/// <summary>Razor-thin Silero VAD v5 ONNX adapter (Humble Object - no logic here).
/// Graph: input [1,512] f32, state [2,1,128] f32, sr [1] i64 -> output [1,1], stateN.</summary>
public sealed class SileroVadModel : ISpeechProbabilityModel, IDisposable
{
    private readonly InferenceSession _session;
    private float[] _state = new float[2 * 1 * 128];
    private static readonly long[] SrValue = { 16000 };

    public SileroVadModel(string onnxPath) => _session = new InferenceSession(onnxPath);

    public float SpeechProbability(ReadOnlySpan<float> window)
    {
        var input = new DenseTensor<float>(window.ToArray(), new[] { 1, window.Length });
        var state = new DenseTensor<float>(_state, new[] { 2, 1, 128 });
        var sr = new DenseTensor<long>(SrValue, new[] { 1 });

        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input", input),
            NamedOnnxValue.CreateFromTensor("state", state),
            NamedOnnxValue.CreateFromTensor("sr", sr),
        });

        float prob = results.First(r => r.Name == "output").AsEnumerable<float>().First();
        _state = results.First(r => r.Name == "stateN").AsEnumerable<float>().ToArray();
        return prob;
    }

    public void Reset() => _state = new float[2 * 1 * 128];

    public void Dispose() => _session.Dispose();
}
```

If the model file's actual input/output names differ at implementation time (older silero graphs use `h`/`c` instead of `state`), inspect `_session.InputMetadata` and adapt the adapter — the seam and tests are unaffected.

- [ ] **Step 5: Write the fixture test**

```csharp
// tests/LocalScribe.Core.Tests/SileroVadFixtureTests.cs
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

[Trait("Category", "Fixture")]
public class SileroVadFixtureTests
{
    [Fact]
    public void Tone_vs_silence_probabilities_separate()
    {
        using var model = new SileroVadModel(ModelPaths.Require("silero_vad.onnx"));

        // Synthetic excitation: a 220 Hz sawtooth is not speech, so we only assert the
        // *silence* side hard plus the relative ordering, never absolute speech scores.
        var silence = new float[512];
        var buzz = new float[512];
        for (int i = 0; i < buzz.Length; i++)
            buzz[i] = (float)(0.6 * ((i * 220.0 / 16000.0) % 1.0 * 2 - 1));

        model.Reset();
        float pSilence = 0f;
        for (int w = 0; w < 10; w++) pSilence = model.SpeechProbability(silence);

        model.Reset();
        float pBuzz = 0f;
        for (int w = 0; w < 10; w++) pBuzz = model.SpeechProbability(buzz);

        Assert.True(pSilence < 0.3f, $"silence prob {pSilence} not low");
        Assert.True(pBuzz >= pSilence, "signal should not score below silence");
    }
}
```

- [ ] **Step 6: Run to verify** — `dotnet test --filter VadSegmenterTests` -> PASS. Then `tools/fetch-models.ps1` and `dotnet test --filter "Category=Fixture&FullyQualifiedName~SileroVad"` -> PASS.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Vad tests/LocalScribe.Core.Tests/VadSegmenterTests.cs \
        tests/LocalScribe.Core.Tests/SileroVadFixtureTests.cs
git commit -m "feat: Silero ONNX adapter + IVadSegmenter async wrapper (EOF flush)"
```

---

## Task 5: BackendSelector + ModelLadder (spec-3 table, pure)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Transcription/HardwareInfo.cs`, `src/LocalScribe.Core/Transcription/BackendSelector.cs`, `src/LocalScribe.Core/Transcription/ModelLadder.cs`
- Test: `tests/LocalScribe.Core.Tests/BackendSelectorTests.cs`

**Interfaces:**
- Consumes: 2a `Settings` (`Model`, `Backend`, `Language` fields), 2a `Backend` enum.
- Produces (ns `LocalScribe.Core.Transcription`):
  - `sealed record HardwareInfo(bool HasCuda, int CudaVramMb, bool HasVulkan, int FastCores)`.
  - `interface IHardwareProbe { HardwareInfo Probe(); }` + `sealed class StaticHardwareProbe(HardwareInfo info) : IHardwareProbe` (config-driven; the live probe is Stage 3).
  - `sealed record BackendPlan(Backend Backend, string ModelName)`.
  - `static class BackendSelector { static BackendPlan Select(HardwareInfo hw, Settings settings); }` — the §3 table: explicit user overrides (`settings.Backend != Auto`, `settings.Model != "auto"`) always win; else CUDA ≥ 8 GB → `small.en`; CUDA 4–6 GB (i.e. ≥ 4096 MB) → `small.en`; Vulkan → `base.en`; CPU → `base.en` (`small.en` when `FastCores >= 8`). `.en` model names when `settings.Language` is `"en"` or `"auto"`; strip the `.en` suffix otherwise (multilingual weights, §3).
  - `static class ModelLadder { static string? Downgrade(string modelName); }` — one step down the §3 ladder preserving the `.en` suffix: `large-v3 -> medium -> small -> base -> tiny -> null`. Unknown names return null (caller treats as floor).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/BackendSelectorTests.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;

public class BackendSelectorTests
{
    private static Settings S(Backend backend = Backend.Auto, string model = "auto", string language = "auto")
        => new() { Backend = backend, Model = model, Language = language };

    [Fact]
    public void Big_nvidia_gets_cuda_small_en()
    {
        var plan = BackendSelector.Select(new HardwareInfo(true, 12000, true, 16), S());
        Assert.Equal(Backend.Cuda, plan.Backend);
        Assert.Equal("small.en", plan.ModelName);
    }

    [Fact]
    public void Mid_nvidia_4_to_6_gb_gets_cuda_small_en()
    {
        var plan = BackendSelector.Select(new HardwareInfo(true, 4096, false, 8), S());
        Assert.Equal(Backend.Cuda, plan.Backend);
        Assert.Equal("small.en", plan.ModelName);
    }

    [Fact]
    public void Igpu_gets_vulkan_base_en()
    {
        var plan = BackendSelector.Select(new HardwareInfo(false, 0, true, 8), S());
        Assert.Equal(Backend.Vulkan, plan.Backend);
        Assert.Equal("base.en", plan.ModelName);
    }

    [Theory]
    [InlineData(4, "base.en")]
    [InlineData(8, "small.en")]
    public void Cpu_model_scales_with_fast_cores(int cores, string expected)
    {
        var plan = BackendSelector.Select(new HardwareInfo(false, 0, false, cores), S());
        Assert.Equal(Backend.Cpu, plan.Backend);
        Assert.Equal(expected, plan.ModelName);
    }

    [Fact]
    public void Explicit_user_overrides_always_win()
    {
        var plan = BackendSelector.Select(new HardwareInfo(true, 12000, true, 16),
            S(backend: Backend.Cpu, model: "tiny.en"));
        Assert.Equal(Backend.Cpu, plan.Backend);
        Assert.Equal("tiny.en", plan.ModelName);
    }

    [Fact]
    public void Non_english_language_gets_multilingual_weights()
    {
        var plan = BackendSelector.Select(new HardwareInfo(true, 12000, false, 8), S(language: "de"));
        Assert.Equal("small", plan.ModelName);         // no .en suffix (spec 3)
    }

    [Theory]
    [InlineData("small.en", "base.en")]
    [InlineData("base.en", "tiny.en")]
    [InlineData("tiny.en", null)]
    [InlineData("large-v3", "medium")]
    [InlineData("unknown-model", null)]
    public void Ladder_steps_down_and_stops_at_floor(string from, string? expected)
        => Assert.Equal(expected, ModelLadder.Downgrade(from));
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter BackendSelectorTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Transcription/HardwareInfo.cs
namespace LocalScribe.Core.Transcription;

/// <summary>Probe result feeding the spec §3 selection table. The live probe is a Stage 3
/// adapter; 2b uses StaticHardwareProbe (config/CLI-driven).</summary>
public sealed record HardwareInfo(bool HasCuda, int CudaVramMb, bool HasVulkan, int FastCores);

public interface IHardwareProbe { HardwareInfo Probe(); }

public sealed class StaticHardwareProbe(HardwareInfo info) : IHardwareProbe
{
    public HardwareInfo Probe() => info;
}
```
```csharp
// src/LocalScribe.Core/Transcription/BackendSelector.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Transcription;

/// <summary>The chosen engine configuration: backend + ggml model name (spec §3).</summary>
public sealed record BackendPlan(Backend Backend, string ModelName);

/// <summary>Pure spec-§3 selection: probe order CUDA -> Vulkan -> CPU, model per tier,
/// explicit user overrides always win, .en weights when the session language is English.</summary>
public static class BackendSelector
{
    public static BackendPlan Select(HardwareInfo hw, Settings settings)
    {
        Backend backend = settings.Backend != Backend.Auto
            ? settings.Backend
            : hw.HasCuda && hw.CudaVramMb >= 4096 ? Backend.Cuda
            : hw.HasVulkan ? Backend.Vulkan
            : Backend.Cpu;

        string model = settings.Model != "auto"
            ? settings.Model
            : backend switch
            {
                Backend.Cuda => "small.en",
                Backend.Vulkan => "base.en",
                _ => hw.FastCores >= 8 ? "small.en" : "base.en",
            };

        bool english = settings.Language is "en" or "auto";
        if (!english && model.EndsWith(".en", StringComparison.Ordinal))
            model = model[..^3];                       // multilingual weights (spec 3)

        return new BackendPlan(backend, model);
    }
}
```
```csharp
// src/LocalScribe.Core/Transcription/ModelLadder.cs
namespace LocalScribe.Core.Transcription;

/// <summary>One-step model downgrade for VRAM-OOM / sustained-RTF pressure (spec §3
/// auto-downgrade triggers). Preserves the .en suffix; null at the floor or for unknown names.</summary>
public static class ModelLadder
{
    private static readonly string[] Rungs = { "large-v3", "medium", "small", "base", "tiny" };

    public static string? Downgrade(string modelName)
    {
        bool en = modelName.EndsWith(".en", StringComparison.Ordinal);
        string stem = en ? modelName[..^3] : modelName;
        int i = Array.IndexOf(Rungs, stem);
        if (i < 0 || i == Rungs.Length - 1) return null;
        string next = Rungs[i + 1];
        return en ? next + ".en" : next;
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter BackendSelectorTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Transcription/HardwareInfo.cs src/LocalScribe.Core/Transcription/BackendSelector.cs \
        src/LocalScribe.Core/Transcription/ModelLadder.cs tests/LocalScribe.Core.Tests/BackendSelectorTests.cs
git commit -m "feat: backend cascade selector + model downgrade ladder (spec 3)"
```

---

## Task 6: ITranscriptionEngine + FakeTranscriptionEngine + WhisperNetEngine  [UNIT + FIXTURE]

**Files:**
- Create: `src/LocalScribe.Core/Transcription/ITranscriptionEngine.cs`, `src/LocalScribe.Core/Transcription/WhisperNetEngine.cs`, `src/LocalScribe.Core/Transcription/WhisperEngineFactory.cs`, `tests/LocalScribe.Core.Tests/FakeTranscriptionEngine.cs`
- Test: `tests/LocalScribe.Core.Tests/WhisperFixtureTests.cs` (fixture)

**Interfaces:**
- Consumes: `AudioSegment`, `BackendPlan`, `ModelPaths`, 2a `Backend` enum.
- Produces (ns `LocalScribe.Core.Transcription`):
  - `sealed record TranscriptionResult(string Text, string? DetectedLanguage, double? NoSpeechProb)`.
  - `interface ITranscriptionEngine : IAsyncDisposable { string ModelName { get; } Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct); }` — language + initial prompt are **fixed at engine creation** (whisper.cpp sets them at context/processor build; the language lock recreates the engine once, Task 7/8).
  - `sealed class VramOutOfMemoryException(string message, Exception? inner = null) : Exception(message, inner)` — thrown by engines when the backend reports VRAM exhaustion; the worker (Task 8) catches it to downgrade.
  - `interface IEngineFactory { Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? initialPrompt, CancellationToken ct); }`.
  - `sealed class WhisperEngineFactory : IEngineFactory` — resolves `ggml-{plan.ModelName}.bin` via `ModelPaths.Require`, builds a `WhisperNetEngine`. Backend selection at the Whisper.net level is done **once per process** via `RuntimeOptions` library ordering (set by the OfflineRunner, Task 13) — the factory records the plan for bookkeeping.
  - Test-side `sealed class FakeTranscriptionEngine : ITranscriptionEngine` — scripted: constructed with a `Func<AudioSegment, TranscriptionResult>` **or** a queue of results/exceptions, so worker tests can trigger `VramOutOfMemoryException` on demand. Lives in the **test project**.
- **Whisper.net API note:** built against Whisper.net 1.9.1 — `WhisperFactory.FromPath(modelPath)`, `factory.CreateBuilder().WithLanguage(lang)/.WithLanguageDetection()/.WithPrompt(prompt).Build()`, `await foreach (var s in processor.ProcessAsync(float[] samples, ct))` yielding segment data with `Text` and `Language`. If a member name differs at build time (e.g. the no-speech probability property), adapt the **adapter only** — the seam and all unit tests are engine-agnostic. Map whisper native OOM failures (message containing "out of memory"/"CUDA" alloc errors) to `VramOutOfMemoryException`.

- [ ] **Step 1: Write the fake (compile-first — it is the seam contract)**

```csharp
// tests/LocalScribe.Core.Tests/FakeTranscriptionEngine.cs
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

/// <summary>Scripted engine for worker/merger/runner tests. Either a per-segment function
/// or a FIFO of outcomes (results or exceptions to throw).</summary>
public sealed class FakeTranscriptionEngine : ITranscriptionEngine
{
    private readonly Func<AudioSegment, TranscriptionResult>? _fn;
    private readonly Queue<object>? _script;           // TranscriptionResult | Exception
    public string ModelName { get; }
    public int Calls { get; private set; }

    public FakeTranscriptionEngine(string modelName, Func<AudioSegment, TranscriptionResult> fn)
        => (ModelName, _fn) = (modelName, fn);

    public FakeTranscriptionEngine(string modelName, IEnumerable<object> script)
        => (ModelName, _script) = (modelName, new Queue<object>(script));

    public Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct)
    {
        Calls++;
        if (_fn is not null) return Task.FromResult(_fn(segment));
        object next = _script!.Dequeue();
        if (next is Exception ex) throw ex;
        return Task.FromResult((TranscriptionResult)next);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 2: Implement the seam types**

```csharp
// src/LocalScribe.Core/Transcription/ITranscriptionEngine.cs
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Transcription;

/// <summary>One transcribed utterance. DetectedLanguage feeds the probe-then-commit
/// language lock (spec §3); NoSpeechProb feeds the hallucination gate (design "Model & backend").</summary>
public sealed record TranscriptionResult(string Text, string? DetectedLanguage, double? NoSpeechProb);

/// <summary>Humble-object seam over whisper.cpp (or any future NPU/DirectML engine).
/// Language + initial-prompt bias are fixed at creation; the worker recreates the engine
/// on language lock or model downgrade.</summary>
public interface ITranscriptionEngine : IAsyncDisposable
{
    string ModelName { get; }
    Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct);
}

/// <summary>Backend ran out of GPU memory; the worker downgrades one ladder step (spec §3/§8.2 VRAM_OOM).</summary>
public sealed class VramOutOfMemoryException : Exception
{
    public VramOutOfMemoryException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IEngineFactory
{
    Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? initialPrompt, CancellationToken ct);
}
```

- [ ] **Step 3: Implement the Whisper.net adapter + factory**

```csharp
// src/LocalScribe.Core/Transcription/WhisperNetEngine.cs
using LocalScribe.Core.Pipeline;
using Whisper.net;
namespace LocalScribe.Core.Transcription;

/// <summary>Razor-thin Whisper.net adapter. No decisions here: model path, language, and
/// prompt arrive resolved; failures map to the seam's exception types.</summary>
public sealed class WhisperNetEngine : ITranscriptionEngine
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    public string ModelName { get; }

    public WhisperNetEngine(string modelPath, string modelName, string? language, string? initialPrompt)
    {
        ModelName = modelName;
        _factory = WhisperFactory.FromPath(modelPath);
        var builder = _factory.CreateBuilder();
        builder = language is null or "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        if (!string.IsNullOrEmpty(initialPrompt)) builder = builder.WithPrompt(initialPrompt);
        _processor = builder.Build();
    }

    public async Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct)
    {
        var parts = new List<string>();
        string? lang = null;
        try
        {
            await foreach (var s in _processor.ProcessAsync(segment.Pcm.ToArray(), ct))
            {
                if (!string.IsNullOrWhiteSpace(s.Text)) parts.Add(s.Text.Trim());
                lang ??= s.Language;
            }
        }
        catch (Exception ex) when (LooksLikeVramOom(ex))
        {
            throw new VramOutOfMemoryException($"whisper backend OOM on {ModelName}", ex);
        }
        return new TranscriptionResult(string.Join(" ", parts), lang, NoSpeechProb: null);
    }

    private static bool LooksLikeVramOom(Exception ex)
        => ex.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("CUDA", StringComparison.OrdinalIgnoreCase)
              && ex.Message.Contains("alloc", StringComparison.OrdinalIgnoreCase);

    public ValueTask DisposeAsync()
    {
        _processor.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
```
```csharp
// src/LocalScribe.Core/Transcription/WhisperEngineFactory.cs
namespace LocalScribe.Core.Transcription;

/// <summary>Creates WhisperNetEngine instances from a BackendPlan. Model files resolve via
/// ModelPaths (ggml-{model}.bin); the process-wide native backend order (CUDA -> Vulkan -> CPU)
/// is configured once by the host (OfflineRunner) through Whisper.net RuntimeOptions.</summary>
public sealed class WhisperEngineFactory : IEngineFactory
{
    public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? initialPrompt, CancellationToken ct)
    {
        string path = ModelPaths.Require($"ggml-{plan.ModelName}.bin");
        return Task.FromResult<ITranscriptionEngine>(
            new WhisperNetEngine(path, plan.ModelName, language, initialPrompt));
    }
}
```

- [ ] **Step 4: Build check** — `dotnet build` -> 0 warnings (adjust the adapter to the actual 1.9.1 member names if the compiler disagrees; the seam must not change).

- [ ] **Step 5: Write the fixture test**

```csharp
// tests/LocalScribe.Core.Tests/WhisperFixtureTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

[Trait("Category", "Fixture")]
public class WhisperFixtureTests
{
    [Fact]
    public async Task Tiny_model_transcribes_synthetic_tone_to_something_or_nothing_but_never_throws()
    {
        // Smoke-level: engine loads, processes 2 s of low-level noise, returns without throwing.
        // Content assertions live in the golden-corpus E2E (Task 14) on real speech.
        var factory = new WhisperEngineFactory();
        await using var engine = await factory.CreateAsync(
            new BackendPlan(Backend.Cpu, "tiny.en"), "en", initialPrompt: null, default);

        var rng = new Random(42);
        var pcm = new float[32000];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(rng.NextDouble() * 0.01 - 0.005);

        var result = await engine.TranscribeAsync(
            new AudioSegment(SourceKind.Local, 0, 2000, pcm), default);
        Assert.NotNull(result.Text);                    // may be empty - that is fine (noise)
    }
}
```

- [ ] **Step 6: Run to verify** — `dotnet test --filter "Category=Fixture&FullyQualifiedName~WhisperFixture"` -> PASS (models fetched). Unit gate still green: `dotnet test --filter "Category!=Fixture"`.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Transcription/ITranscriptionEngine.cs \
        src/LocalScribe.Core/Transcription/WhisperNetEngine.cs \
        src/LocalScribe.Core/Transcription/WhisperEngineFactory.cs \
        tests/LocalScribe.Core.Tests/FakeTranscriptionEngine.cs tests/LocalScribe.Core.Tests/WhisperFixtureTests.cs
git commit -m "feat: ITranscriptionEngine seam + Whisper.net adapter + scripted fake"
```

---

## Task 7: LanguageResolver (probe-then-commit lock)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Transcription/LanguageResolver.cs`
- Test: `tests/LocalScribe.Core.Tests/LanguageResolverTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (ns `LocalScribe.Core.Transcription`): `sealed class LanguageResolver(string settingsLanguage, int probeCount = 3)`:
  - `bool IsLocked` — true immediately when `settingsLanguage != "auto"` (fixed language, no probe).
  - `string? Locked` — the session-locked code (the fixed setting, or the majority of the first `probeCount` detections; ties resolve to the most recent).
  - `void Observe(string? detectedLanguage)` — feed each result's `DetectedLanguage` until locked; null detections are ignored (do not consume a probe slot).
  - `bool UseEnglishOnlyModel` — `Locked == "en"` (spec §3: switch to `.en` weights only if detected == en).
- The **caller** (worker, Task 8) recreates the engine once on lock; the resolved code is persisted to `session.json.language` by the runner (Task 12). Mid-meeting switching unsupported (v1 non-goal).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/LanguageResolverTests.cs
using LocalScribe.Core.Transcription;

public class LanguageResolverTests
{
    [Fact]
    public void Fixed_setting_locks_immediately()
    {
        var r = new LanguageResolver("de");
        Assert.True(r.IsLocked);
        Assert.Equal("de", r.Locked);
        Assert.False(r.UseEnglishOnlyModel);
    }

    [Fact]
    public void Auto_locks_on_majority_of_first_three_detections()
    {
        var r = new LanguageResolver("auto");
        Assert.False(r.IsLocked);
        r.Observe("en");
        r.Observe("de");
        Assert.False(r.IsLocked);
        r.Observe("en");
        Assert.True(r.IsLocked);
        Assert.Equal("en", r.Locked);
        Assert.True(r.UseEnglishOnlyModel);
    }

    [Fact]
    public void Null_detections_do_not_consume_probe_slots()
    {
        var r = new LanguageResolver("auto");
        r.Observe(null);
        r.Observe(null);
        r.Observe("en");
        r.Observe("en");
        Assert.False(r.IsLocked);                       // only 2 real observations so far
        r.Observe("en");
        Assert.True(r.IsLocked);
    }

    [Fact]
    public void Observations_after_lock_are_ignored()
    {
        var r = new LanguageResolver("auto", probeCount: 1);
        r.Observe("en");
        Assert.True(r.IsLocked);
        r.Observe("de");
        Assert.Equal("en", r.Locked);                   // locked stays locked (v1 non-goal)
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter LanguageResolverTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Transcription/LanguageResolver.cs
namespace LocalScribe.Core.Transcription;

/// <summary>Probe-then-commit per-session language lock (spec §3): transcribe the first
/// few utterances with detection on, take the majority, lock for the session. A fixed
/// (non-auto) setting locks immediately. Mid-meeting switching is a v1 non-goal.</summary>
public sealed class LanguageResolver
{
    private readonly int _probeCount;
    private readonly List<string> _seen = new();

    public LanguageResolver(string settingsLanguage, int probeCount = 3)
    {
        _probeCount = probeCount;
        if (settingsLanguage != "auto") Locked = settingsLanguage;
    }

    public string? Locked { get; private set; }
    public bool IsLocked => Locked is not null;
    public bool UseEnglishOnlyModel => Locked == "en";

    public void Observe(string? detectedLanguage)
    {
        if (IsLocked || string.IsNullOrEmpty(detectedLanguage)) return;
        _seen.Add(detectedLanguage);
        if (_seen.Count < _probeCount) return;

        Locked = _seen.GroupBy(l => l)
                      .OrderByDescending(g => g.Count())
                      .ThenByDescending(g => _seen.LastIndexOf(g.Key))
                      .First().Key;
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter LanguageResolverTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Transcription/LanguageResolver.cs tests/LocalScribe.Core.Tests/LanguageResolverTests.cs
git commit -m "feat: probe-then-commit session language lock (spec 3)"
```

---

## Task 8: TranscriptionWorker — bounded channel, RTF monitor, OOM downgrade  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Transcription/TranscriptionWorker.cs`, `src/LocalScribe.Core/Transcription/TranscribedSegment.cs`
- Test: `tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs`

**Interfaces:**
- Consumes: `ITranscriptionEngine`, `IEngineFactory`, `BackendPlan`, `ModelLadder`, `LanguageResolver`, `AudioSegment`, `IClock` (Stage 1), 2a `Markers`, 2a `Backend`.
- Produces (ns `LocalScribe.Core.Transcription`):
  - `sealed record TranscribedSegment(AudioSegment Audio, TranscriptionResult Result, string ModelName)`.
  - `sealed record TranscriptionWorkerOptions { int QueueCapacity = 64; double NoSpeechDropThreshold = 0.6; double LaggingRtfThreshold = 1.0; int LaggingWindow = 8; string? InitialPrompt; }`
  - `sealed class TranscriptionWorker(IEngineFactory factory, BackendPlan initialPlan, LanguageResolver language, IClock clock, TranscriptionWorkerOptions options)`:
    - `ValueTask EnqueueAsync(AudioSegment segment, CancellationToken ct)` — bounded, **waits** when full (never drops; design backpressure rule).
    - `void Complete()` — no more input.
    - `Task RunAsync(CancellationToken ct)` — the single consumer loop; returns when the queue is completed and drained (Finalizing's "write queue drained", spec §2.1).
    - `event Action<TranscribedSegment>? SegmentTranscribed` — fired for every kept result.
    - `event Action<string>? MarkerRaised` — marker **message** (from 2a `Markers`) to be appended by the merger; 2b raises only `Markers.TranscriptionLagging`.
    - `event Action<string>? ErrorRaised` — §8.2 error **codes** for the log/UI (`"VRAM_OOM"`); never written to the transcript.
  - **Behavior:**
    1. **Hallucination gate:** a result with empty/whitespace `Text`, or `NoSpeechProb >= NoSpeechDropThreshold`, is dropped silently (design "Silence/noise hallucinations").
    2. **Language lock:** each kept result feeds `language.Observe(...)`; when the resolver transitions to locked, the worker **recreates the engine once** with the locked language (and `.en` weights via the current plan's name when `UseEnglishOnlyModel` — apply `ModelLadder`-independent rename: if locked == en and model has no `.en` suffix and `{model}.en` is a known rung, use it).
    3. **VRAM OOM:** `VramOutOfMemoryException` → raise `ErrorRaised("VRAM_OOM")`, downgrade one `ModelLadder` step (at the floor: switch plan to `Backend.Cpu` with the same model), recreate the engine, **retry the same segment** (never dropped).
    4. **RTF monitor:** per segment, RTF = processing ms (from `IClock` before/after) ÷ audio ms. Keep the last `LaggingWindow` RTFs; when **all** of a full window exceed `LaggingRtfThreshold`, raise `MarkerRaised(Markers.TranscriptionLagging)` + `ErrorRaised("RTF_LAGGING")` **once**, downgrade one ladder step, recreate the engine, and reset the window (spec §3 auto-downgrade; §8.1 marker).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

public class TranscriptionWorkerTests
{
    private sealed class ScriptedFactory : IEngineFactory
    {
        public readonly List<(BackendPlan Plan, string? Language)> Created = new();
        private readonly Func<BackendPlan, ITranscriptionEngine> _make;
        public ScriptedFactory(Func<BackendPlan, ITranscriptionEngine> make) => _make = make;
        public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? prompt, CancellationToken ct)
        {
            Created.Add((plan, language));
            return Task.FromResult(_make(plan));
        }
    }

    private static AudioSegment Seg(long startMs = 0, int ms = 1000) =>
        new(SourceKind.Local, startMs, startMs + ms, new float[16 * ms]);

    private static TranscriptionWorker Worker(
        IEngineFactory factory, FakeClock clock, TranscriptionWorkerOptions? o = null,
        LanguageResolver? lang = null) =>
        new(factory, new BackendPlan(Backend.Cpu, "small.en"),
            lang ?? new LanguageResolver("en"), clock, o ?? new TranscriptionWorkerOptions());

    [Fact]
    public async Task Transcribes_and_fires_kept_segments_in_order()
    {
        var clock = new FakeClock();
        var factory = new ScriptedFactory(_ => new FakeTranscriptionEngine("small.en",
            s => new TranscriptionResult($"seg@{s.StartMs}", "en", 0.01)));
        var worker = Worker(factory, clock);
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);
        await worker.EnqueueAsync(Seg(1000), default);
        worker.Complete();
        await run;

        Assert.Equal(new[] { "seg@0", "seg@1000" }, got.Select(g => g.Result.Text));
    }

    [Fact]
    public async Task High_no_speech_prob_and_empty_text_are_dropped()
    {
        var clock = new FakeClock();
        var script = new Queue<TranscriptionResult>(new[]
        {
            new TranscriptionResult("", "en", 0.0),            // empty -> dropped
            new TranscriptionResult("Thank you.", "en", 0.95), // hallucination -> dropped
            new TranscriptionResult("real words", "en", 0.05), // kept
        });
        var factory = new ScriptedFactory(_ => new FakeTranscriptionEngine("small.en", _ => script.Dequeue()));
        var worker = Worker(factory, clock);
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        for (int i = 0; i < 3; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Equal("real words", Assert.Single(got).Result.Text);
    }

    [Fact]
    public async Task Vram_oom_downgrades_one_step_and_retries_same_segment()
    {
        var clock = new FakeClock();
        var errors = new List<string>();
        var factory = new ScriptedFactory(plan => plan.ModelName == "small.en"
            ? new FakeTranscriptionEngine("small.en", new object[]
                { new VramOutOfMemoryException("oom") })
            : new FakeTranscriptionEngine(plan.ModelName,
                s => new TranscriptionResult("recovered", "en", 0.0)));
        var worker = Worker(factory, clock);
        worker.ErrorRaised += errors.Add;
        var got = new List<TranscribedSegment>();
        worker.SegmentTranscribed += got.Add;

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);
        worker.Complete();
        await run;

        Assert.Contains("VRAM_OOM", errors);
        var only = Assert.Single(got);
        Assert.Equal("recovered", only.Result.Text);
        Assert.Equal("base.en", only.ModelName);               // one ladder step down
        Assert.Equal(2, factory.Created.Count);                // recreated once
    }

    [Fact]
    public async Task Sustained_rtf_over_one_raises_lagging_marker_once_and_downgrades()
    {
        var clock = new FakeClock();
        var factory = new ScriptedFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.ElapsedMs += 2 * (s.EndMs - s.StartMs);      // RTF = 2 on every segment
            return new TranscriptionResult("slow", "en", 0.0);
        }));
        var markers = new List<string>();
        var worker = Worker(factory, clock, new TranscriptionWorkerOptions { LaggingWindow = 3 });
        worker.MarkerRaised += markers.Add;

        var run = worker.RunAsync(default);
        for (int i = 0; i < 6; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Equal(Markers.TranscriptionLagging, Assert.Single(markers));  // once, not per segment
        Assert.Equal(2, factory.Created.Count);                              // downgraded engine
        Assert.Equal("base.en", factory.Created[1].Plan.ModelName);
    }

    [Fact]
    public async Task Language_lock_recreates_engine_with_locked_language()
    {
        var clock = new FakeClock();
        var factory = new ScriptedFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            s => new TranscriptionResult("hallo", "de", 0.0)));
        var worker = Worker(factory, clock, lang: new LanguageResolver("auto", probeCount: 2));

        var run = worker.RunAsync(default);
        for (int i = 0; i < 3; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Equal(2, factory.Created.Count);
        Assert.Null(factory.Created[0].Language);              // probing: detection on
        Assert.Equal("de", factory.Created[1].Language);       // locked
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter TranscriptionWorkerTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Transcription/TranscribedSegment.cs
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Transcription;

/// <summary>A segment paired with its transcription and the model that produced it.</summary>
public sealed record TranscribedSegment(AudioSegment Audio, TranscriptionResult Result, string ModelName);
```
```csharp
// src/LocalScribe.Core/Transcription/TranscriptionWorker.cs
using System.Threading.Channels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Transcription;

public sealed record TranscriptionWorkerOptions
{
    public int QueueCapacity { get; init; } = 64;
    public double NoSpeechDropThreshold { get; init; } = 0.6;
    public double LaggingRtfThreshold { get; init; } = 1.0;
    public int LaggingWindow { get; init; } = 8;
    public string? InitialPrompt { get; init; }
}

/// <summary>Single-consumer transcription worker over a bounded channel (design:
/// "Backpressure, never drop"). Owns the engine lifecycle: hallucination gate, language
/// lock (recreate once), VRAM-OOM downgrade + same-segment retry, sustained-RTF downgrade
/// with a one-shot `transcription lagging` marker (spec §3/§8).</summary>
public sealed class TranscriptionWorker
{
    private readonly IEngineFactory _factory;
    private readonly LanguageResolver _language;
    private readonly IClock _clock;
    private readonly TranscriptionWorkerOptions _o;
    private readonly Channel<AudioSegment> _queue;
    private readonly Queue<double> _rtfWindow = new();
    private BackendPlan _plan;
    private bool _laggingRaised;

    public event Action<TranscribedSegment>? SegmentTranscribed;
    public event Action<string>? MarkerRaised;
    public event Action<string>? ErrorRaised;

    public TranscriptionWorker(IEngineFactory factory, BackendPlan initialPlan,
        LanguageResolver language, IClock clock, TranscriptionWorkerOptions options)
    {
        (_factory, _plan, _language, _clock, _o) = (factory, initialPlan, language, clock, options);
        _queue = Channel.CreateBounded<AudioSegment>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,     // absorb lag; never drop audio
            SingleReader = true,
        });
    }

    public ValueTask EnqueueAsync(AudioSegment segment, CancellationToken ct)
        => _queue.Writer.WriteAsync(segment, ct);

    public void Complete() => _queue.Writer.Complete();

    public async Task RunAsync(CancellationToken ct)
    {
        var engine = await CreateEngineAsync(ct);
        try
        {
            await foreach (var segment in _queue.Reader.ReadAllAsync(ct))
            {
                TranscriptionResult result;
                while (true)
                {
                    long t0 = _clock.ElapsedMs;
                    try
                    {
                        result = await engine.TranscribeAsync(segment, ct);
                    }
                    catch (VramOutOfMemoryException)
                    {
                        ErrorRaised?.Invoke("VRAM_OOM");
                        engine = await DowngradeAsync(engine, ct);
                        continue;                        // retry the SAME segment
                    }
                    TrackRtf(_clock.ElapsedMs - t0, segment.EndMs - segment.StartMs);
                    break;
                }

                if (!_laggingRaised
                    && _rtfWindow.Count >= _o.LaggingWindow
                    && _rtfWindow.All(r => r > _o.LaggingRtfThreshold))
                {
                    // one-shot in 2b: marker + a single downgrade step (spec 3/8.1)
                    _laggingRaised = true;
                    MarkerRaised?.Invoke(Markers.TranscriptionLagging);
                    ErrorRaised?.Invoke("RTF_LAGGING");
                    engine = await DowngradeAsync(engine, ct);
                    _rtfWindow.Clear();
                }

                if (string.IsNullOrWhiteSpace(result.Text)) continue;
                if (result.NoSpeechProb is { } p && p >= _o.NoSpeechDropThreshold) continue;

                bool wasLocked = _language.IsLocked;
                _language.Observe(result.DetectedLanguage);
                SegmentTranscribed?.Invoke(new TranscribedSegment(segment, result, engine.ModelName));

                if (!wasLocked && _language.IsLocked)
                    engine = await RecreateAsync(engine, ct);   // language lock: rebuild once
            }
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    private void TrackRtf(long processingMs, long audioMs)
    {
        if (audioMs <= 0) return;
        _rtfWindow.Enqueue(processingMs / (double)audioMs);
        while (_rtfWindow.Count > _o.LaggingWindow) _rtfWindow.Dequeue();
    }

    private async Task<ITranscriptionEngine> DowngradeAsync(ITranscriptionEngine current, CancellationToken ct)
    {
        string? next = ModelLadder.Downgrade(_plan.ModelName);
        _plan = next is not null
            ? _plan with { ModelName = next }
            : _plan with { Backend = Backend.Cpu };     // at the floor: fall to CPU (design)
        return await RecreateAsync(current, ct);
    }

    private async Task<ITranscriptionEngine> RecreateAsync(ITranscriptionEngine current, CancellationToken ct)
    {
        await current.DisposeAsync();
        return await CreateEngineAsync(ct);
    }

    private Task<ITranscriptionEngine> CreateEngineAsync(CancellationToken ct)
        => _factory.CreateAsync(_plan, _language.IsLocked ? _language.Locked : null, _o.InitialPrompt, ct);
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter TranscriptionWorkerTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Transcription/TranscriptionWorker.cs \
        src/LocalScribe.Core/Transcription/TranscribedSegment.cs \
        tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs
git commit -m "feat: bounded transcription worker (OOM/RTF downgrade, lagging marker, language lock)"
```

---

## Task 9: TranscriptMerger — seq assignment, JSONL append, sorted live view  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Pipeline/TranscriptMerger.cs`
- Test: `tests/LocalScribe.Core.Tests/TranscriptMergerTests.cs`

**Interfaces:**
- Consumes: 2a `TranscriptStore`, `TranscriptLine`, `TranscriptSource`; `TranscribedSegment`, `SegmentAudio` (Tasks 1/8); Stage 1 `SourceKind`.
- Produces (ns `LocalScribe.Core.Pipeline`), `sealed class TranscriptMerger(TranscriptStore store)`:
  - `Task InitializeAsync(CancellationToken ct)` — seeds the seq counter from `store.NextSeqAsync` (crash/re-open safe).
  - `Task<TranscriptLine> AppendSegmentAsync(TranscribedSegment ts, CancellationToken ct)` — builds `TranscriptLine.Segment(seq++, source, startMs, endMs, text, label, lang, noSpeechProb, rmsDb)` where `source` maps `SourceKind.Local -> TranscriptSource.Local` / `Remote -> Remote`, `label` = `"Me"`/`"Them"` (structural attribution), `rmsDb = SegmentAudio.RmsDb(ts.Audio.Pcm.Span)` rounded to 1 decimal; appends to JSONL (**write order = finalization order**, spec §1.1); inserts into the live view.
  - `Task<TranscriptLine> AppendMarkerAsync(string message, long atMs, CancellationToken ct)` — `TranscriptLine.Marker(seq++, atMs, message)`, appended + inserted.
  - `IReadOnlyList<TranscriptLine> View` — the display-ordered collection (spec §5: `startMs` asc, tie-break source `Local < Remote < System`, then `seq`).
  - `event Action<int, TranscriptLine>? LineInserted` — index + line of each sorted insert (Stage 3 binds the live view to this; a record may insert *behind* the newest — expected, spec §5).
  - `static int FindInsertIndex(IReadOnlyList<TranscriptLine> view, TranscriptLine line)` — the pure §5 comparison, exposed for reuse.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/TranscriptMergerTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;

public class TranscriptMergerTests
{
    private static TranscribedSegment Ts(SourceKind src, long startMs, long endMs, string text)
    {
        var pcm = new float[160];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = 0.5f;
        return new TranscribedSegment(new AudioSegment(src, startMs, endMs, pcm),
            new TranscriptionResult(text, "en", 0.02), "small.en");
    }

    private static string TempJsonl() =>
        Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "transcript.jsonl");

    [Fact]
    public async Task Seq_is_finalization_order_but_view_is_startMs_order()
    {
        string path = TempJsonl();
        try
        {
            var merger = new TranscriptMerger(new TranscriptStore(path));
            await merger.InitializeAsync(default);

            // Remote finalizes FIRST but starts LATER; Local finalizes second, starts earlier.
            var a = await merger.AppendSegmentAsync(Ts(SourceKind.Remote, 500, 1500, "remote"), default);
            var b = await merger.AppendSegmentAsync(Ts(SourceKind.Local, 0, 400, "local"), default);

            Assert.Equal(0, a.Seq);                              // write order
            Assert.Equal(1, b.Seq);
            Assert.Equal(new[] { "local", "remote" },            // display order (spec 5)
                merger.View.Select(l => l.Text));

            var onDisk = await new TranscriptStore(path).ReadAllAsync(default);
            Assert.Equal(new[] { 0, 1 }, onDisk.Select(l => l.Seq));   // JSONL stays write-order
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Insert_event_reports_the_sorted_position()
    {
        string path = TempJsonl();
        try
        {
            var merger = new TranscriptMerger(new TranscriptStore(path));
            await merger.InitializeAsync(default);
            var inserts = new List<int>();
            merger.LineInserted += (i, _) => inserts.Add(i);

            await merger.AppendSegmentAsync(Ts(SourceKind.Remote, 500, 1500, "later"), default);
            await merger.AppendSegmentAsync(Ts(SourceKind.Local, 0, 400, "earlier"), default);

            Assert.Equal(new[] { 0, 0 }, inserts);               // second lands BEHIND the first
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Segment_line_carries_label_lang_noSpeech_and_rms()
    {
        string path = TempJsonl();
        try
        {
            var merger = new TranscriptMerger(new TranscriptStore(path));
            await merger.InitializeAsync(default);
            var line = await merger.AppendSegmentAsync(Ts(SourceKind.Remote, 0, 1000, "hi"), default);

            Assert.Equal(TranscriptSource.Remote, line.Source);
            Assert.Equal("Them", line.SpeakerLabel);             // structural attribution
            Assert.Equal("en", line.Lang);
            Assert.Equal(0.02, line.NoSpeechProb);
            Assert.Equal(-6.0, line.RmsDb!.Value, 1);            // 0.5 amplitude ~ -6 dB
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Markers_interleave_and_tie_break_after_segments()
    {
        string path = TempJsonl();
        try
        {
            var merger = new TranscriptMerger(new TranscriptStore(path));
            await merger.InitializeAsync(default);
            await merger.AppendSegmentAsync(Ts(SourceKind.Local, 1000, 2000, "a"), default);
            await merger.AppendMarkerAsync(Markers.TranscriptionLagging, 1000, default);

            Assert.Equal(2, merger.View.Count);
            Assert.Equal(TranscriptKind.Segment, merger.View[0].Kind);   // same startMs: System sorts last
            Assert.Equal(TranscriptKind.Marker, merger.View[1].Kind);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Initialize_seeds_seq_from_existing_jsonl()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "old", "Me"), default);

            var merger = new TranscriptMerger(store);
            await merger.InitializeAsync(default);
            var line = await merger.AppendSegmentAsync(Ts(SourceKind.Local, 5, 6, "new"), default);
            Assert.Equal(1, line.Seq);
        }
        finally { CleanParent(path); }
    }

    private static void CleanParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter TranscriptMergerTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Pipeline/TranscriptMerger.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
namespace LocalScribe.Core.Pipeline;

/// <summary>Sorted-insert merge by session clock (design; spec §5). JSONL stays in
/// finalization (seq) order - the view is the render-time computation. Single-threaded
/// consumer: call only from the pipeline's consumer loop.</summary>
public sealed class TranscriptMerger
{
    private readonly TranscriptStore _store;
    private readonly List<TranscriptLine> _view = new();
    private int _nextSeq;

    public TranscriptMerger(TranscriptStore store) => _store = store;

    public IReadOnlyList<TranscriptLine> View => _view;
    public event Action<int, TranscriptLine>? LineInserted;

    public async Task InitializeAsync(CancellationToken ct)
        => _nextSeq = await _store.NextSeqAsync(ct);

    public async Task<TranscriptLine> AppendSegmentAsync(TranscribedSegment ts, CancellationToken ct)
    {
        var source = ts.Audio.Source == SourceKind.Local ? TranscriptSource.Local : TranscriptSource.Remote;
        string label = ts.Audio.Source == SourceKind.Local ? "Me" : "Them";
        var line = TranscriptLine.Segment(_nextSeq++, source, ts.Audio.StartMs, ts.Audio.EndMs,
            ts.Result.Text, label, ts.Result.DetectedLanguage, ts.Result.NoSpeechProb,
            Math.Round(SegmentAudio.RmsDb(ts.Audio.Pcm.Span), 1));
        await _store.AppendAsync(line, ct);
        Insert(line);
        return line;
    }

    public async Task<TranscriptLine> AppendMarkerAsync(string message, long atMs, CancellationToken ct)
    {
        var line = TranscriptLine.Marker(_nextSeq++, atMs, message);
        await _store.AppendAsync(line, ct);
        Insert(line);
        return line;
    }

    private void Insert(TranscriptLine line)
    {
        int i = FindInsertIndex(_view, line);
        _view.Insert(i, line);
        LineInserted?.Invoke(i, line);
    }

    /// <summary>Spec §5 display order: startMs asc, then source (Local &lt; Remote &lt; System), then seq.</summary>
    public static int FindInsertIndex(IReadOnlyList<TranscriptLine> view, TranscriptLine line)
    {
        int lo = 0, hi = view.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Compare(view[mid], line) <= 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static int Compare(TranscriptLine a, TranscriptLine b)
    {
        int c = a.StartMs.CompareTo(b.StartMs);
        if (c != 0) return c;
        c = Rank(a.Source).CompareTo(Rank(b.Source));
        return c != 0 ? c : a.Seq.CompareTo(b.Seq);
    }

    private static int Rank(TranscriptSource s)
        => s switch { TranscriptSource.Local => 0, TranscriptSource.Remote => 1, _ => 2 };
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter TranscriptMergerTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Pipeline/TranscriptMerger.cs tests/LocalScribe.Core.Tests/TranscriptMergerTests.cs
git commit -m "feat: TranscriptMerger (finalization-order seq, spec-5 sorted live view)"
```

---

## Task 10: TextDistance + PhantomBleedDedup (real IRenderDedup)  [UNIT]

Replaces the 2a `NoOpDedup` seam-filler in projection wiring. Physics recap (design "Error handling" #1): with speakers instead of headphones, remote voices bleed into the local mic and get transcribed a **second** time on the Local stream, mis-attributed as "Me". The bled Local copy overlaps the direct Remote copy in time, carries near-identical text, and is **quieter**. The dedup hides that Local copy at render time only — JSONL keeps both (spec §5); genuine overlap (distinct words, comparable energy) is never suppressed.

**Files:**
- Create: `src/LocalScribe.Core/Projection/TextDistance.cs`, `src/LocalScribe.Core/Projection/PhantomBleedDedup.cs`
- Test: `tests/LocalScribe.Core.Tests/PhantomBleedDedupTests.cs`

**Interfaces:**
- Consumes: 2a `IRenderDedup`, `ProjectedSegment` (which exposes `Line`, `Text`, `Seq`, `Source`, `StartMs`, `EndMs`), `TranscriptSource`, `TranscriptLine.RmsDb` (Task 2).
- Produces (ns `LocalScribe.Core.Projection`):
  - `static class TextDistance` — `static int Levenshtein<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, IEqualityComparer<T>? cmp = null)` and `static double NormalizedSimilarity(string a, string b)` (normalize: lowercase, non-alphanumerics to single spaces, trim; similarity = `1 - dist/maxLen` over chars; two empty strings → 1.0). Shared with `WerCalculator` (Task 14).
  - `sealed record PhantomBleedOptions { int NearWindowMs = 750; double MinSimilarity = 0.85; double MinRmsGapDb = 3.0; double TextOnlyMinSimilarity = 0.92; }` — **starting values; tune against the golden corpus (Task 14) before changing.**
  - `sealed class PhantomBleedDedup(PhantomBleedOptions? options = null) : IRenderDedup` — `Filter` hides a **Local** segment iff some **Remote** segment satisfies all of:
    1. **Near-simultaneous:** time ranges overlap after widening the Remote range by `NearWindowMs` on both sides.
    2. **Matching text:** `NormalizedSimilarity >= MinSimilarity`.
    3. **Energy gap:** both `RmsDb` present → Local is quieter by at least `MinRmsGapDb` (comparable energy → never suppressed). Either `RmsDb` missing (legacy lines) → fall back to the stricter `TextOnlyMinSimilarity` instead.
  - Remote segments are never hidden. Markers never reach the dedup (2a projection partitions them first).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/PhantomBleedDedupTests.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class PhantomBleedDedupTests
{
    private static ProjectedSegment Seg(TranscriptSource src, int seq, long startMs, long endMs,
        string text, double? rmsDb) =>
        new(TranscriptLine.Segment(seq, src, startMs, endMs, text,
            src == TranscriptSource.Local ? "Me" : "Them", rmsDb: rmsDb), text);

    [Fact]
    public void Quieter_matching_local_copy_is_hidden()
    {
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "I pushed the auth changes last night.", -18.0);
        var bleed = Seg(TranscriptSource.Local, 1, 1150, 4100, "I pushed the auth changes last night", -31.5);
        var kept = new PhantomBleedDedup().Filter(new[] { remote, bleed });

        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Remote, only.Source);
    }

    [Fact]
    public void Comparable_energy_is_never_suppressed_even_with_matching_text()
    {
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "yes exactly", -20.0);
        var local = Seg(TranscriptSource.Local, 1, 1100, 3900, "yes exactly", -21.0);   // 1 dB gap only
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Distinct_words_are_never_suppressed()
    {
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "the hearing moved to Thursday", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1000, 4000, "okay I will tell the client", -30.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Far_apart_in_time_is_never_suppressed()
    {
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2000, "same words here", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 10_000, 11_000, "same words here", -30.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Missing_rms_uses_the_stricter_text_only_bar()
    {
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "I pushed the auth changes last night.", null);
        var nearMatch = Seg(TranscriptSource.Local, 1, 1100, 4000, "I pushed the auth change last night", null);
        var exact = Seg(TranscriptSource.Local, 2, 1100, 4000, "I pushed the auth changes last night.", null);

        var kept = new PhantomBleedDedup().Filter(new[] { remote, nearMatch, exact });
        // exact copy (sim 1.0) hidden; near-match (~0.9 < 0.92) kept without energy evidence
        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, s => s.Seq == 2);
        Assert.Contains(kept, s => s.Seq == 1);
    }

    [Fact]
    public void Remote_segments_are_never_hidden()
    {
        var loud = Seg(TranscriptSource.Local, 0, 1000, 4000, "same words", -10.0);
        var quiet = Seg(TranscriptSource.Remote, 1, 1000, 4000, "same words", -40.0);
        var kept = new PhantomBleedDedup().Filter(new[] { loud, quiet });
        Assert.Contains(kept, s => s.Source == TranscriptSource.Remote);   // quiet remote survives
    }

    [Theory]
    [InlineData("Hello, World!", "hello world", 1.0)]
    [InlineData("abcd", "abxd", 0.75)]
    [InlineData("", "", 1.0)]
    public void Normalized_similarity(string a, string b, double expected)
        => Assert.Equal(expected, TextDistance.NormalizedSimilarity(a, b), 2);
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter PhantomBleedDedupTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Projection/TextDistance.cs
using System.Text;
namespace LocalScribe.Core.Projection;

/// <summary>Edit-distance utilities shared by the phantom-bleed dedup and the WER calculator.</summary>
public static class TextDistance
{
    public static int Levenshtein<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, IEqualityComparer<T>? cmp = null)
    {
        cmp ??= EqualityComparer<T>.Default;
        var prev = new int[b.Count + 1];
        var cur = new int[b.Count + 1];
        for (int j = 0; j <= b.Count; j++) prev[j] = j;
        for (int i = 1; i <= a.Count; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Count; j++)
            {
                int sub = prev[j - 1] + (cmp.Equals(a[i - 1], b[j - 1]) ? 0 : 1);
                cur[j] = Math.Min(sub, Math.Min(prev[j] + 1, cur[j - 1] + 1));
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Count];
    }

    /// <summary>Char-level similarity in [0,1] over normalized text (lowercase, punctuation
    /// collapsed to single spaces). Two empty strings are identical (1.0).</summary>
    public static double NormalizedSimilarity(string a, string b)
    {
        string na = Normalize(a), nb = Normalize(b);
        if (na.Length == 0 && nb.Length == 0) return 1.0;
        int max = Math.Max(na.Length, nb.Length);
        return 1.0 - Levenshtein(na.ToCharArray(), nb.ToCharArray()) / (double)max;
    }

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                sb.Append(c);
                pendingSpace = false;
            }
            else pendingSpace = true;
        }
        return sb.ToString();
    }
}
```
```csharp
// src/LocalScribe.Core/Projection/PhantomBleedDedup.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>Starting thresholds for the phantom-bleed heuristic (spec §5). Tune ONLY against
/// the golden corpus (Stage 2b Task 14) - never ad hoc.</summary>
public sealed record PhantomBleedOptions
{
    public int NearWindowMs { get; init; } = 750;
    public double MinSimilarity { get; init; } = 0.85;
    public double MinRmsGapDb { get; init; } = 3.0;
    public double TextOnlyMinSimilarity { get; init; } = 0.92;
}

/// <summary>Render-layer phantom-bleed suppression (spec §5; design "speakers instead of
/// headphones"). Hides a Local segment that closely matches a near-simultaneous Remote
/// segment when the Local copy is clearly quieter (the bled copy). Non-destructive: JSONL
/// keeps both; genuine overlap (distinct words or comparable energy) is never suppressed.</summary>
public sealed class PhantomBleedDedup : IRenderDedup
{
    private readonly PhantomBleedOptions _o;
    public PhantomBleedDedup(PhantomBleedOptions? options = null) => _o = options ?? new();

    public IReadOnlyList<ProjectedSegment> Filter(IReadOnlyList<ProjectedSegment> segments)
    {
        var remotes = segments.Where(s => s.Source == TranscriptSource.Remote).ToList();
        if (remotes.Count == 0) return segments;

        var kept = new List<ProjectedSegment>(segments.Count);
        foreach (var s in segments)
        {
            if (s.Source == TranscriptSource.Local && remotes.Any(r => IsBleedOf(s, r)))
                continue;                               // hidden at render; JSONL untouched
            kept.Add(s);
        }
        return kept;
    }

    private bool IsBleedOf(ProjectedSegment local, ProjectedSegment remote)
    {
        bool near = local.StartMs < remote.EndMs + _o.NearWindowMs
                 && remote.StartMs - _o.NearWindowMs < local.EndMs;
        if (!near) return false;

        double similarity = TextDistance.NormalizedSimilarity(local.Text, remote.Text);
        double? localRms = local.Line.RmsDb, remoteRms = remote.Line.RmsDb;

        if (localRms is { } lr && remoteRms is { } rr)
            return similarity >= _o.MinSimilarity && lr <= rr - _o.MinRmsGapDb;

        return similarity >= _o.TextOnlyMinSimilarity;  // no energy evidence: stricter text bar
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter PhantomBleedDedupTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Projection/TextDistance.cs src/LocalScribe.Core/Projection/PhantomBleedDedup.cs \
        tests/LocalScribe.Core.Tests/PhantomBleedDedupTests.cs
git commit -m "feat: phantom-bleed render dedup (time+text+energy heuristic, spec 5)"
```

---

## Task 11: IAudioFileSink — WAV + FLAC retained-audio writers  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Audio/IAudioFileSink.cs`, `src/LocalScribe.Core/Audio/FlacAudioSink.cs`
- Test: `tests/LocalScribe.Core.Tests/AudioSinkTests.cs`

**Interfaces:**
- Consumes: existing `WavSink`, `PcmConverter`; 2a `AudioFormat` enum.
- Produces (ns `LocalScribe.Core.Audio`):
  - `interface IAudioFileSink : IDisposable { void Write(ReadOnlySpan<float> mono16k); }`
  - `sealed class WavAudioSink(string path) : IAudioFileSink` — wraps the existing `WavSink`.
  - `sealed class FlacAudioSink(string path) : IAudioFileSink` — 16-bit/mono/16 kHz FLAC via `CUETools.Codecs.FLAKE`'s `FlakeWriter`. Dispose **must** finalize the stream (`Close()`).
  - `static class AudioSinkFactory { static IAudioFileSink Create(string path, AudioFormat format); }` — `path` already carries the right extension (the 2a `StoragePaths.AudioFile(id, source, format)` produces it).
- **FLAKE API note:** the expected shape is `new FlakeWriter(path, new AudioPCMConfig(16, 1, 16000))`, fill an `AudioBuffer` from interleaved 16-bit little-endian bytes (`buffer.Prepare(bytes, sampleCount)`), `writer.Write(buffer)`, `writer.Close()`. If member names differ in 1.0.5, adapt inside `FlacAudioSink` only — the magic-bytes test below pins the observable contract, and `IAudioFileSink` shields every consumer.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/AudioSinkTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;

public class AudioSinkTests
{
    private static float[] Sine(int seconds)
    {
        var pcm = new float[16000 * seconds];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / 16000.0));
        return pcm;
    }

    [Fact]
    public void Flac_sink_writes_a_flac_stream_smaller_than_wav()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string flac = Path.Combine(dir, "local.flac");
            string wav = Path.Combine(dir, "local.wav");
            var pcm = Sine(2);

            using (var f = AudioSinkFactory.Create(flac, AudioFormat.Flac)) f.Write(pcm);
            using (var w = AudioSinkFactory.Create(wav, AudioFormat.Wav)) w.Write(pcm);

            byte[] head = new byte[4];
            using (var fs = File.OpenRead(flac)) fs.ReadExactly(head);
            Assert.Equal("fLaC"u8.ToArray(), head);                       // FLAC magic
            Assert.True(new FileInfo(flac).Length < new FileInfo(wav).Length,
                "FLAC of a tonal signal must compress below WAV");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Wav_sink_roundtrips_through_existing_wavsink_format()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string wav = Path.Combine(dir, "x.wav");
            using (var w = AudioSinkFactory.Create(wav, AudioFormat.Wav)) w.Write(Sine(1));
            using var reader = new NAudio.Wave.WaveFileReader(wav);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter AudioSinkTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Audio/IAudioFileSink.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Audio;

/// <summary>Retained-audio writer seam: 16 kHz mono float in, session audio file out
/// (local.flac/remote.flac per settings.audioFormat - spec §9).</summary>
public interface IAudioFileSink : IDisposable
{
    void Write(ReadOnlySpan<float> mono16k);
}

/// <summary>WAV variant - wraps the Stage-1 WavSink.</summary>
public sealed class WavAudioSink : IAudioFileSink
{
    private readonly WavSink _inner;
    public WavAudioSink(string path) => _inner = new WavSink(path);
    public void Write(ReadOnlySpan<float> mono16k) => _inner.Write(mono16k);
    public void Dispose() => _inner.Dispose();
}

public static class AudioSinkFactory
{
    public static IAudioFileSink Create(string path, AudioFormat format)
        => format == AudioFormat.Flac ? new FlacAudioSink(path) : new WavAudioSink(path);
}
```
```csharp
// src/LocalScribe.Core/Audio/FlacAudioSink.cs
using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
namespace LocalScribe.Core.Audio;

/// <summary>FLAC retained-audio writer (16-bit/mono/16 kHz) via the managed Flake encoder.
/// CUETools.Codecs.FLAKE is LGPL-3.0: consumed unmodified, dynamically linked, never trimmed.</summary>
public sealed class FlacAudioSink : IAudioFileSink
{
    private static readonly AudioPCMConfig Config = new(16, 1, 16000);
    private readonly FlakeWriter _writer;

    public FlacAudioSink(string path) => _writer = new FlakeWriter(path, Config);

    public void Write(ReadOnlySpan<float> mono16k)
    {
        if (mono16k.Length == 0) return;
        byte[] bytes = PcmConverter.FloatToInt16Bytes(mono16k);
        var buffer = new AudioBuffer(Config, mono16k.Length);
        buffer.Prepare(bytes, mono16k.Length);
        _writer.Write(buffer);
    }

    public void Dispose() => _writer.Close();
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter AudioSinkTests` -> PASS (adapt `FlacAudioSink` internals to the actual 1.0.5 API if the compiler disagrees; the tests must not change).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio/IAudioFileSink.cs src/LocalScribe.Core/Audio/FlacAudioSink.cs \
        tests/LocalScribe.Core.Tests/AudioSinkTests.cs
git commit -m "feat: IAudioFileSink with WAV + FLAC (Flake) retained-audio writers"
```

---

## Task 12: WavFileFrameReader + OfflinePipelineRunner (core orchestration)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Pipeline/WavFileFrameReader.cs`, `src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs`
- Test: `tests/LocalScribe.Core.Tests/WavFileFrameReaderTests.cs`, `tests/LocalScribe.Core.Tests/OfflinePipelineRunnerTests.cs`

**Interfaces:**
- Consumes: everything above, plus 2a `StoragePaths`, `Settings`, `SessionId`, `SessionStore`/`SessionRecord`, `MetadataStore`/`SessionMeta`/`SessionParticipant`, `SessionWriter`, `VocabularyProvider`, `AppKind`, `SourceKind`, Stage-1 `MonoResampler16k`/`PcmConverter`/`StopwatchClock`.
- Produces (ns `LocalScribe.Core.Pipeline`):
  - `static class WavFileFrameReader { static IEnumerable<AudioFrame> ReadFrames(string wavPath, SourceKind source); }` — reads any WAV via NAudio `AudioFileReader` (float samples), downmixes stereo (`PcmConverter.StereoToMono`; >2 channels throws `NotSupportedException`), resamples to 16 kHz when needed (`MonoResampler16k`), and yields **512-sample** frames with `StartMs = emittedSamples * 1000 / 16000`. The trailing partial window is dropped (< 32 ms; VAD ignores it anyway).
  - `sealed record OfflineRunOptions { string? LocalWavPath; string? RemoteWavPath; VadOptions Vad = new(); TranscriptionWorkerOptions Worker = new(); }` (at least one path required).
  - `sealed class OfflinePipelineRunner(StoragePaths paths, Settings settings, IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware, IClock clock, TimeProvider time, string appVersion)`:
    - `Task<string> RunAsync(OfflineRunOptions options, CancellationToken ct)` — returns the created **sessionId**. Orchestration:
      1. `startedUtc = time.GetUtcNow()`; `tz = time.LocalTimeZone`; `offset = tz.GetUtcOffset(startedUtc)`; `startedLocal = startedUtc.ToOffset(offset)`.
      2. Self participant from `settings.Self` when `Name` is non-empty (`Id="p-self"`, `Side=Local`, `IsSelf=true`); `meta = SessionMeta.CreateDefault(AppKind.Manual, startedLocal, self)`.
      3. `id = SessionId.EnsureUnique(SessionId.New(startedLocal, AppKind.Manual, meta.Title), x => Directory.Exists(paths.SessionDir(x)))`; create the folder; save `meta.json`; save an **initial live** `session.json` v3 (`EndedAtUtc=null`, `TimeZoneId=tz.Id`, `UtcOffsetMinutes=(int)offset.TotalMinutes`, `Sources` per provided paths, `AppVersion=appVersion`) — same write pattern the live pipeline will use, so recovery semantics hold if the runner dies.
      4. `plan = BackendSelector.Select(hardware.Probe(), settings)`; `resolver = new LanguageResolver(settings.Language)`; initial prompt = `new VocabularyProvider(settings.Vocabulary, new Dictionary<string, Matter>()).BuildInitialPrompt(Array.Empty<string>())` (global vocabulary only — matter tagging happens post-hoc in the UI, Stage 4).
      5. `worker = new TranscriptionWorker(engineFactory, plan, resolver, clock, options.Worker with { InitialPrompt = prompt })`; `merger = new TranscriptMerger(new TranscriptStore(paths.TranscriptJsonl(id)))` + `InitializeAsync`. Worker events forward into an **unbounded output channel** (`SegmentTranscribed` → segment item, `MarkerRaised` → marker item); a single writer loop drains it through the merger (markers stamped at the last appended segment's `EndMs`, or 0). Events must not `await` — the channel decouples them.
      6. Per provided source (Local first, then Remote): frames → `SileroVadSegmenter(source, options.Vad, vadModelFactory())` → `worker.EnqueueAsync`. Then `worker.Complete()`, await the worker + writer loops (queue drained = Finalizing's flush, spec §2.1).
      7. **Retained audio** (unless `settings.AudioRetention == "never"`): re-read each source's frames and write through `AudioSinkFactory.Create(paths.AudioFile(id, source, settings.AudioFormat), settings.AudioFormat)`; record `RetainedAudioSources`.
      8. Finalize `session.json`: `EndedAtUtc = startedUtc + lastEndMs`, `DurationMs = lastEndMs` (max `EndMs` over appended lines, 0 if none), `SegmentCount`/`MarkerCount` from the merger view, `Model` = last kept segment's `ModelName` (else `plan.ModelName`), `Backend = plan.Backend` wire-name uppercased (recorded actual, e.g. `"CPU"`), `Language = resolver.Locked ?? settings.Language`.
      9. `new SessionWriter(paths, settings, time).RegenerateProjectionsAsync(id, ct)` → `transcript.md`/`.txt`/`session.txt`.
- The runner is **deterministic given fakes**: fake engine + energy-threshold VAD probe + `FakeClock` + `ManualUtcTimeProvider` — the unit test covers the whole pipeline end-to-end with zero ML.

- [ ] **Step 1: Write the failing reader test**

```csharp
// tests/LocalScribe.Core.Tests/WavFileFrameReaderTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;

public class WavFileFrameReaderTests
{
    [Fact]
    public void Reads_16k_mono_wav_into_512_sample_frames_with_running_timestamps()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string wav = Path.Combine(dir, "in.wav");
            var pcm = new float[16000];                            // 1 s
            for (int i = 0; i < pcm.Length; i++) pcm[i] = 0.25f;
            using (var sink = new WavSink(wav)) sink.Write(pcm);

            var frames = WavFileFrameReader.ReadFrames(wav, SourceKind.Remote).ToList();

            Assert.Equal(16000 / 512, frames.Count);               // 31 whole windows; tail dropped
            Assert.All(frames, f => Assert.Equal(512, f.Samples.Length));
            Assert.Equal(SourceKind.Remote, frames[0].Source);
            Assert.Equal(0, frames[0].StartMs);
            Assert.Equal(32, frames[1].StartMs);
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter WavFileFrameReaderTests` -> FAIL.

- [ ] **Step 3: Implement the reader**

```csharp
// src/LocalScribe.Core/Pipeline/WavFileFrameReader.cs
using LocalScribe.Core.Audio;
using NAudio.Wave;
namespace LocalScribe.Core.Pipeline;

/// <summary>Offline frame source: WAV file -> 16 kHz mono 512-sample AudioFrames with
/// sample-counted StartMs. Mirrors what the live capture emits, so the same VAD/worker/merger
/// pipeline runs unchanged (design "walking skeleton").</summary>
public static class WavFileFrameReader
{
    private const int FrameSamples = 512;

    public static IEnumerable<AudioFrame> ReadFrames(string wavPath, SourceKind source)
    {
        using var reader = new AudioFileReader(wavPath);           // float samples
        int channels = reader.WaveFormat.Channels;
        if (channels > 2)
            throw new NotSupportedException($"{channels}-channel WAV is not supported (mono/stereo only).");
        int rate = reader.WaveFormat.SampleRate;
        var resampler = rate == 16000 ? null : new MonoResampler16k(rate);

        var pending = new List<float>();
        var readBuf = new float[rate * channels];                  // ~1 s per read
        long emitted = 0;
        int n;
        while ((n = reader.Read(readBuf, 0, readBuf.Length)) > 0)
        {
            float[] mono = channels == 2
                ? PcmConverter.StereoToMono(readBuf.AsSpan(0, n))
                : readBuf.AsSpan(0, n).ToArray();
            pending.AddRange(resampler is null ? mono : resampler.Process(mono));

            while (pending.Count >= FrameSamples)
            {
                var frame = pending.GetRange(0, FrameSamples).ToArray();
                pending.RemoveRange(0, FrameSamples);
                yield return new AudioFrame(source, emitted * 1000 / 16000, frame);
                emitted += FrameSamples;
            }
        }
        // trailing partial window (< 32 ms) intentionally dropped
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter WavFileFrameReaderTests` -> PASS.

- [ ] **Step 5: Write the failing runner test**

```csharp
// tests/LocalScribe.Core.Tests/OfflinePipelineRunnerTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

public class OfflinePipelineRunnerTests
{
    /// <summary>Energy-threshold probe: loud window = speech. Deterministic, no ONNX.</summary>
    private sealed class EnergyProbe : ISpeechProbabilityModel
    {
        public float SpeechProbability(ReadOnlySpan<float> window)
            => SegmentAudio.RmsDb(window) > -30.0 ? 0.95f : 0.02f;
        public void Reset() { }
    }

    private sealed class EchoFactory : IEngineFactory
    {
        public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? prompt, CancellationToken ct)
            => Task.FromResult<ITranscriptionEngine>(new FakeTranscriptionEngine(plan.ModelName,
                s => new TranscriptionResult($"[{s.Source} {s.StartMs}-{s.EndMs}]", "en", 0.01)));
    }

    private static void WriteBurstWav(string path, params (int SilenceMs, int SpeechMs)[] pattern)
    {
        using var sink = new WavSink(path);
        foreach (var (silence, speech) in pattern)
        {
            sink.Write(new float[16 * silence]);
            var burst = new float[16 * speech];
            for (int i = 0; i < burst.Length; i++)
                burst[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * i / 16000.0));
            sink.Write(burst);
        }
        sink.Write(new float[16 * 1000]);                          // trailing second of silence
    }

    [Fact]
    public async Task Wav_pair_becomes_a_complete_finalized_session_folder()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string localWav = Path.Combine(root, "in-local.wav");
            string remoteWav = Path.Combine(root, "in-remote.wav");
            WriteBurstWav(localWav, (200, 1500));                  // one Local utterance
            WriteBurstWav(remoteWav, (2500, 1500));                // one later Remote utterance

            var paths = new StoragePaths(Path.Combine(root, "store"));
            // Wav: no FLAC dependency here. Language fixed to "en": with only 2 segments the
            // auto probe (3 utterances) never locks and session.language would stay "auto".
            var settings = new Settings { AudioFormat = AudioFormat.Wav, Language = "en" };
            var time = new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 2, 6, 32, 5, TimeSpan.Zero));
            var runner = new OfflinePipelineRunner(paths, settings, new EchoFactory(),
                () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
                new FakeClock(), time, appVersion: "0.2.0-test");

            string id = await runner.RunAsync(new OfflineRunOptions
            { LocalWavPath = localWav, RemoteWavPath = remoteWav }, default);

            // JSONL: one segment per burst, Local finalized first (fed first)
            var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
            Assert.Equal(2, lines.Count);
            Assert.Equal(TranscriptSource.Local, lines[0].Source);
            Assert.Equal("Me", lines[0].SpeakerLabel);
            Assert.Equal(TranscriptSource.Remote, lines[1].Source);
            Assert.NotNull(lines[0].RmsDb);

            // session.json finalized with counts, duration, recorded actuals, timezone
            var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
            Assert.NotNull(session!.EndedAtUtc);
            Assert.Equal(2, session.SegmentCount);
            Assert.Equal(0, session.MarkerCount);
            Assert.True(session.DurationMs >= 3500);
            Assert.Equal("CPU", session.Backend);
            Assert.Equal("en", session.Language);
            Assert.NotNull(session.TimeZoneId);
            Assert.Equal(AppKind.Manual, session.App);

            // projections + retained audio on disk (spec 9 self-contained folder)
            Assert.True(File.Exists(paths.TranscriptMd(id)));
            Assert.True(File.Exists(paths.TranscriptTxt(id)));
            Assert.True(File.Exists(paths.SessionTxt(id)));
            Assert.True(File.Exists(paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav)));
            Assert.True(File.Exists(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Wav)));
            Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, session.RetainedAudioSources);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Retention_never_skips_audio_files()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string localWav = Path.Combine(root, "in-local.wav");
            WriteBurstWav(localWav, (200, 1000));
            var paths = new StoragePaths(Path.Combine(root, "store"));
            var settings = new Settings { AudioFormat = AudioFormat.Wav, AudioRetention = "never" };
            var runner = new OfflinePipelineRunner(paths, settings, new EchoFactory(),
                () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
                new FakeClock(), new ManualUtcTimeProvider(DateTimeOffset.UnixEpoch), "0.2.0-test");

            string id = await runner.RunAsync(new OfflineRunOptions { LocalWavPath = localWav }, default);

            Assert.False(File.Exists(paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav)));
            var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
            Assert.Empty(session!.RetainedAudioSources);
            Assert.Single(await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default));
        }
        finally { Directory.Delete(root, true); }
    }
}
```

- [ ] **Step 6: Run to verify failure** — `dotnet test --filter OfflinePipelineRunnerTests` -> FAIL.

- [ ] **Step 7: Implement the runner**

```csharp
// src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs
using System.Threading.Channels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Pipeline;

public sealed record OfflineRunOptions
{
    public string? LocalWavPath { get; init; }
    public string? RemoteWavPath { get; init; }
    public VadOptions Vad { get; init; } = new();
    public TranscriptionWorkerOptions Worker { get; init; } = new();
}

/// <summary>Stage-2 walking skeleton: WAV pair -> VAD -> Whisper -> merge -> a complete,
/// finalized, spec-shaped session folder. Same components the live pipeline (Stage 3) wires
/// to real capture; only the frame source differs.</summary>
public sealed class OfflinePipelineRunner
{
    private readonly StoragePaths _paths;
    private readonly Settings _settings;
    private readonly IEngineFactory _engineFactory;
    private readonly Func<ISpeechProbabilityModel> _vadModelFactory;
    private readonly IHardwareProbe _hardware;
    private readonly IClock _clock;
    private readonly TimeProvider _time;
    private readonly string _appVersion;

    public OfflinePipelineRunner(StoragePaths paths, Settings settings, IEngineFactory engineFactory,
        Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware,
        IClock clock, TimeProvider time, string appVersion)
        => (_paths, _settings, _engineFactory, _vadModelFactory, _hardware, _clock, _time, _appVersion)
         = (paths, settings, engineFactory, vadModelFactory, hardware, clock, time, appVersion);

    public async Task<string> RunAsync(OfflineRunOptions options, CancellationToken ct)
    {
        if (options.LocalWavPath is null && options.RemoteWavPath is null)
            throw new ArgumentException("At least one of LocalWavPath/RemoteWavPath is required.");

        // 1) identity: wall-clock start, timezone capture (spec 1.2), collision-safe id (spec 9)
        var startedUtc = _time.GetUtcNow();
        var tz = _time.LocalTimeZone;
        var offset = tz.GetUtcOffset(startedUtc);
        var startedLocal = startedUtc.ToOffset(offset);

        SessionParticipant? self = string.IsNullOrEmpty(_settings.Self.Name) ? null
            : new SessionParticipant
            { Id = "p-self", Name = _settings.Self.Name, Role = _settings.Self.Role, Side = SourceKind.Local, IsSelf = true };
        var meta = SessionMeta.CreateDefault(AppKind.Manual, startedLocal, self);

        string id = SessionId.EnsureUnique(
            SessionId.New(startedLocal, AppKind.Manual, meta.Title),
            x => Directory.Exists(_paths.SessionDir(x)));
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(meta, ct);

        var sources = new List<SourceKind>();
        if (options.LocalWavPath is not null) sources.Add(SourceKind.Local);
        if (options.RemoteWavPath is not null) sources.Add(SourceKind.Remote);

        var sessionStore = new SessionStore(_paths.SessionJson(id));
        var live = new SessionRecord
        {
            Id = id, App = AppKind.Manual, StartedAtUtc = startedUtc,
            TimeZoneId = tz.Id, UtcOffsetMinutes = (int)offset.TotalMinutes,
            Sources = sources, AppVersion = _appVersion, Language = _settings.Language,
        };
        await sessionStore.SaveAsync(live, ct);                     // live record: recovery-compatible

        // 2) pipeline
        var plan = BackendSelector.Select(_hardware.Probe(), _settings);
        var resolver = new LanguageResolver(_settings.Language);
        string prompt = new VocabularyProvider(_settings.Vocabulary, new Dictionary<string, Matter>())
            .BuildInitialPrompt(Array.Empty<string>());
        var worker = new TranscriptionWorker(_engineFactory, plan, resolver, _clock,
            options.Worker with { InitialPrompt = prompt.Length == 0 ? null : prompt });

        var merger = new TranscriptMerger(new TranscriptStore(_paths.TranscriptJsonl(id)));
        await merger.InitializeAsync(ct);

        // events -> single writer loop (event handlers must not await)
        var outbox = Channel.CreateUnbounded<object>();             // TranscribedSegment | string marker
        string? lastModel = null;
        worker.SegmentTranscribed += ts => outbox.Writer.TryWrite(ts);
        worker.MarkerRaised += m => outbox.Writer.TryWrite(m);

        var writerLoop = Task.Run(async () =>
        {
            long lastEndMs = 0;
            await foreach (object item in outbox.Reader.ReadAllAsync(ct))
            {
                if (item is TranscribedSegment ts)
                {
                    var line = await merger.AppendSegmentAsync(ts, ct);
                    lastEndMs = Math.Max(lastEndMs, line.EndMs);
                    lastModel = ts.ModelName;
                }
                else if (item is string marker)
                {
                    await merger.AppendMarkerAsync(marker, lastEndMs, ct);
                }
            }
        }, ct);

        var workerLoop = worker.RunAsync(ct);
        foreach (var (path, kind) in EnumerateInputs(options))
        {
            var segmenter = new SileroVadSegmenter(kind, options.Vad, _vadModelFactory());
            await foreach (var segment in segmenter.SegmentAsync(ToAsync(WavFileFrameReader.ReadFrames(path, kind)), ct))
                await worker.EnqueueAsync(segment, ct);
        }
        worker.Complete();
        await workerLoop;                                           // queue drained (spec 2.1 flush)
        outbox.Writer.Complete();
        await writerLoop;

        // 3) retained audio (keep by default; "never" skips - spec 7)
        var retained = new List<SourceKind>();
        if (_settings.AudioRetention != "never")
        {
            foreach (var (path, kind) in EnumerateInputs(options))
            {
                using var sink = AudioSinkFactory.Create(
                    _paths.AudioFile(id, kind, _settings.AudioFormat), _settings.AudioFormat);
                foreach (var frame in WavFileFrameReader.ReadFrames(path, kind))
                    sink.Write(frame.Samples);
                retained.Add(kind);
            }
        }

        // 4) finalize + project
        long duration = merger.View.Count == 0 ? 0 : merger.View.Max(l => l.EndMs);
        await sessionStore.SaveAsync(live with
        {
            EndedAtUtc = startedUtc.AddMilliseconds(duration),
            DurationMs = duration,
            SegmentCount = merger.View.Count(l => l.Kind == TranscriptKind.Segment),
            MarkerCount = merger.View.Count(l => l.Kind == TranscriptKind.Marker),
            Model = lastModel ?? plan.ModelName,
            Backend = plan.Backend.ToString().ToUpperInvariant(),   // recorded actual, e.g. "CPU"
            Language = resolver.Locked ?? _settings.Language,
            RetainedAudioSources = retained,
        }, ct);

        await new SessionWriter(_paths, _settings, _time).RegenerateProjectionsAsync(id, ct);
        return id;
    }

    private static IEnumerable<(string Path, SourceKind Kind)> EnumerateInputs(OfflineRunOptions o)
    {
        if (o.LocalWavPath is not null) yield return (o.LocalWavPath, SourceKind.Local);
        if (o.RemoteWavPath is not null) yield return (o.RemoteWavPath, SourceKind.Remote);
    }

    private static async IAsyncEnumerable<AudioFrame> ToAsync(IEnumerable<AudioFrame> frames)
    {
        foreach (var f in frames) yield return f;
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 8: Run to verify pass** — `dotnet test --filter "OfflinePipelineRunnerTests|WavFileFrameReaderTests"` -> PASS. Then the full unit gate: `dotnet test --filter "Category!=Fixture"` -> PASS.

- [ ] **Step 9: Commit**

```bash
git add src/LocalScribe.Core/Pipeline/WavFileFrameReader.cs src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs \
        tests/LocalScribe.Core.Tests/WavFileFrameReaderTests.cs tests/LocalScribe.Core.Tests/OfflinePipelineRunnerTests.cs
git commit -m "feat: offline pipeline runner (WAV pair -> finalized session folder)"
```

---

## Task 13: LocalScribe.OfflineRunner console app  [SMOKE]

The demoable Stage-2 deliverable: a console command that runs the real pipeline (Silero + Whisper, backend cascade) on a WAV pair. No automated test — the wiring is 10 lines over already-tested components; verification is the manual run + the Task 14 fixture.

**Files:**
- Create: `src/LocalScribe.OfflineRunner/LocalScribe.OfflineRunner.csproj`, `src/LocalScribe.OfflineRunner/Program.cs`
- Modify: `LocalScribe.slnx` (add project)

**Interfaces:**
- Consumes: `OfflinePipelineRunner`, `WhisperEngineFactory`, `SileroVadModel`, `ModelPaths`, `StaticHardwareProbe`, 2a `SettingsStore`/`StoragePaths`, Stage-1 `StopwatchClock`.
- CLI: `LocalScribe.OfflineRunner --local <wav> [--remote <wav>] [--out <storageRoot>] [--model <name>] [--backend auto|cuda|vulkan|cpu] [--vram <mb>] [--cores <n>]`

- [ ] **Step 1: Create the project**

```bash
dotnet new console -n LocalScribe.OfflineRunner -o src/LocalScribe.OfflineRunner -f net10.0
dotnet sln LocalScribe.slnx add src/LocalScribe.OfflineRunner
dotnet add src/LocalScribe.OfflineRunner reference src/LocalScribe.Core
dotnet add src/LocalScribe.OfflineRunner package Whisper.net.Runtime --version 1.9.1
dotnet add src/LocalScribe.OfflineRunner package Whisper.net.Runtime.Cuda.Windows --version 1.9.1
dotnet add src/LocalScribe.OfflineRunner package Whisper.net.Runtime.Vulkan --version 1.9.1
```

Then retarget the csproj `<TargetFramework>` to `net10.0-windows` (the template rejects `-windows` TFMs — Stage-1 quirk, see implementation notes).

- [ ] **Step 2: Implement Program.cs**

```csharp
// src/LocalScribe.OfflineRunner/Program.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

static string? Arg(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string? local = Arg(args, "--local");
string? remote = Arg(args, "--remote");
if (local is null && remote is null)
{
    Console.Error.WriteLine("usage: LocalScribe.OfflineRunner --local <wav> [--remote <wav>] " +
        "[--out <storageRoot>] [--model <name>] [--backend auto|cuda|vulkan|cpu] [--vram <mb>] [--cores <n>]");
    return 2;
}

// Native backend preference: CUDA -> Vulkan -> CPU (spec 3 cascade at the whisper.cpp level).
// Whisper.net probes this order and falls through automatically when a runtime cannot load.
Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
[
    Whisper.net.LibraryLoader.RuntimeLibrary.Cuda,
    Whisper.net.LibraryLoader.RuntimeLibrary.Vulkan,
    Whisper.net.LibraryLoader.RuntimeLibrary.Cpu,
];

var settingsStore = new SettingsStore(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalScribe", "settings.json"));
var settings = await settingsStore.LoadOrDefaultAsync(default);
if (Arg(args, "--out") is { } outRoot) settings = settings with { StorageRoot = outRoot };
if (Arg(args, "--model") is { } model) settings = settings with { Model = model };
if (Arg(args, "--backend") is { } backend)
    settings = settings with { Backend = Enum.Parse<Backend>(backend, ignoreCase: true) };

var hardware = new StaticHardwareProbe(new HardwareInfo(
    HasCuda: int.TryParse(Arg(args, "--vram"), out int vram) && vram > 0,
    CudaVramMb: vram,
    HasVulkan: false,
    FastCores: int.TryParse(Arg(args, "--cores"), out int cores) ? cores : Environment.ProcessorCount / 2));

var runner = new OfflinePipelineRunner(
    new StoragePaths(settings.StorageRoot), settings,
    new WhisperEngineFactory(),
    () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
    hardware, new StopwatchClock(), TimeProvider.System,
    appVersion: typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

string id = await runner.RunAsync(new OfflineRunOptions { LocalWavPath = local, RemoteWavPath = remote }, default);
var paths = new StoragePaths(settings.StorageRoot);
Console.WriteLine($"session: {id}");
Console.WriteLine($"folder:  {paths.SessionDir(id)}");
Console.WriteLine($"read:    {paths.TranscriptMd(id)}");
return 0;
```

- [ ] **Step 3: Build + manual smoke** — `dotnet build` -> 0 warnings. Then (models fetched, using any Stage-1 capture WAV):

```
dotnet run --project src/LocalScribe.OfflineRunner -- --local <local.wav> --remote <remote.wav> --out %TEMP%\ls-smoke
```

Expected: prints the session id; the folder contains `session.json` (finalized), `meta.json`, `transcript.jsonl`, `transcript.md`/`.txt`, `session.txt`, `local.flac`/`remote.flac`. Open `transcript.md` — interleaved `Me`/`Them` turns. GPU boxes: re-run with `--vram 8192` and confirm CUDA engages ([SMOKE], runbook-style).

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.OfflineRunner LocalScribe.slnx
git commit -m "feat: OfflineRunner console app (real Silero+Whisper offline pipeline demo)"
```

---

## Task 14: WerCalculator + golden-corpus E2E + zero-hallucination gate  [UNIT + FIXTURE]

**Files:**
- Create: `src/LocalScribe.Core/Pipeline/WerCalculator.cs`, `tests/LocalScribe.Core.Tests/GoldenCorpusFixtureTests.cs`, `docs/plans/2026-07-02-stage-2b-golden-corpus.md` (corpus README)
- Test: `tests/LocalScribe.Core.Tests/WerCalculatorTests.cs` (unit)

**Interfaces:**
- Consumes: `TextDistance` (Task 10), `OfflinePipelineRunner` + real adapters, `ModelPaths`.
- Produces: `static class WerCalculator { static double Wer(string reference, string hypothesis); }` — word-level edit distance over `TextDistance.Normalize`d text, divided by the reference word count (empty reference: 0 when hypothesis is empty too, else 1).
- **Corpus layout** (never committed — real call audio is privileged): `<ModelsRoot>/golden/local.wav`, `remote.wav`, `reference-local.txt`, `reference-remote.txt`, and `baseline.json` (committed **numbers are allowed** — the baseline file lives in the corpus dir next to the audio). Sourced from the Stage-1 smoke captures per the runbook.
- **Regression protocol (design "Quality bar"):** the fixture computes per-source WER; if `baseline.json` is missing it **writes** `{ "werLocal": x, "werRemote": y }` and fails with "baseline recorded — re-run"; on later runs it asserts `wer <= baseline + 0.05`. The one hard absolute: **zero hallucination on silence**.

- [ ] **Step 1: Write the failing unit tests**

```csharp
// tests/LocalScribe.Core.Tests/WerCalculatorTests.cs
using LocalScribe.Core.Pipeline;

public class WerCalculatorTests
{
    [Fact]
    public void Identical_text_is_zero_wer()
        => Assert.Equal(0.0, WerCalculator.Wer("the hearing is on Thursday", "The hearing is on Thursday."));

    [Fact]
    public void One_substitution_in_four_words_is_25_percent()
        => Assert.Equal(0.25, WerCalculator.Wer("the hearing on thursday", "the hearing on friday"), 2);

    [Fact]
    public void Empty_hypothesis_is_total_error()
        => Assert.Equal(1.0, WerCalculator.Wer("some reference words", ""));

    [Fact]
    public void Both_empty_is_zero()
        => Assert.Equal(0.0, WerCalculator.Wer("", ""));
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter WerCalculatorTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Pipeline/WerCalculator.cs
using LocalScribe.Core.Projection;
namespace LocalScribe.Core.Pipeline;

/// <summary>Word Error Rate over normalized text (design "Quality bar": regression
/// baselines, not absolute targets - thresholds live with the golden corpus).</summary>
public static class WerCalculator
{
    public static double Wer(string reference, string hypothesis)
    {
        string[] r = Split(reference);
        string[] h = Split(hypothesis);
        if (r.Length == 0) return h.Length == 0 ? 0.0 : 1.0;
        return TextDistance.Levenshtein(r, h) / (double)r.Length;
    }

    private static string[] Split(string text)
        => TextDistance.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter WerCalculatorTests` -> PASS.

- [ ] **Step 5: Write the corpus README**

```markdown
<!-- docs/plans/2026-07-02-stage-2b-golden-corpus.md -->
# Stage 2b Golden Corpus

The fixture E2E (`GoldenCorpusFixtureTests`) runs the real pipeline (Silero VAD +
Whisper `base.en`, CPU) over a paired two-stream recording and holds WER at the
first-measured baseline (design "Quality bar": baseline + 0.05 epsilon, never a fixed
absolute). The corpus contains REAL call audio (privileged) and is therefore NEVER
committed - it lives beside the models:

    <ModelsRoot>/golden/
      local.wav             # Stage-1 smoke capture, mic side
      remote.wav            # Stage-1 smoke capture, loopback side
      reference-local.txt   # human transcript of local.wav (plain text)
      reference-remote.txt  # human transcript of remote.wav
      baseline.json         # written by the fixture on first run; commit-free

Setup: copy a Stage-1 hardware-gate capture pair (see
docs/plans/2026-07-01-stage-1-implementation-notes.md runbook) into the folder and
hand-transcribe the two sides once. Keep the pair short (1-3 min).

The silence gate needs no corpus: the fixture synthesizes 30 s of silence and asserts
the pipeline yields ZERO segments (the one hard absolute).
```

- [ ] **Step 6: Write the fixture test**

```csharp
// tests/LocalScribe.Core.Tests/GoldenCorpusFixtureTests.cs
using System.Text.Json;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

[Trait("Category", "Fixture")]
public class GoldenCorpusFixtureTests
{
    private const double Epsilon = 0.05;

    private static OfflinePipelineRunner RealRunner(StoragePaths paths, Settings settings) =>
        new(paths, settings, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, Environment.ProcessorCount / 2)),
            new StopwatchClock(), TimeProvider.System, "fixture");

    [Fact]
    public async Task Silence_produces_zero_segments_hard_bar()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string wav = Path.Combine(root, "silence.wav");
            using (var sink = new WavSink(wav)) sink.Write(new float[16000 * 30]);   // 30 s silence

            var paths = new StoragePaths(Path.Combine(root, "store"));
            var settings = new Settings { Model = "base.en", AudioFormat = AudioFormat.Wav };
            string id = await RealRunner(paths, settings).RunAsync(
                new OfflineRunOptions { LocalWavPath = wav }, default);

            var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
            Assert.Empty(lines);                          // zero hallucination on silence
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Golden_pair_wer_stays_at_baseline()
    {
        string golden = Path.Combine(ModelPaths.ModelsRoot, "golden");
        string localWav = Path.Combine(golden, "local.wav");
        string remoteWav = Path.Combine(golden, "remote.wav");
        if (!File.Exists(localWav) || !File.Exists(remoteWav))
            throw new FileNotFoundException(
                $"Golden corpus missing under {golden} - see docs/plans/2026-07-02-stage-2b-golden-corpus.md");

        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new StoragePaths(Path.Combine(root, "store"));
            var settings = new Settings { Model = "base.en", AudioFormat = AudioFormat.Wav };
            string id = await RealRunner(paths, settings).RunAsync(
                new OfflineRunOptions { LocalWavPath = localWav, RemoteWavPath = remoteWav }, default);

            var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
            string HypFor(TranscriptSource src) => string.Join(" ",
                lines.Where(l => l.Kind == TranscriptKind.Segment && l.Source == src).Select(l => l.Text));

            double werLocal = WerCalculator.Wer(
                await File.ReadAllTextAsync(Path.Combine(golden, "reference-local.txt")),
                HypFor(TranscriptSource.Local));
            double werRemote = WerCalculator.Wer(
                await File.ReadAllTextAsync(Path.Combine(golden, "reference-remote.txt")),
                HypFor(TranscriptSource.Remote));

            string baselinePath = Path.Combine(golden, "baseline.json");
            if (!File.Exists(baselinePath))
            {
                await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(
                    new { werLocal, werRemote }, new JsonSerializerOptions { WriteIndented = true }));
                Assert.Fail($"Baseline recorded (local={werLocal:F3}, remote={werRemote:F3}) - re-run to assert.");
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath));
            double baseLocal = doc.RootElement.GetProperty("werLocal").GetDouble();
            double baseRemote = doc.RootElement.GetProperty("werRemote").GetDouble();
            Assert.True(werLocal <= baseLocal + Epsilon, $"Local WER regressed: {werLocal:F3} > {baseLocal:F3}+{Epsilon}");
            Assert.True(werRemote <= baseRemote + Epsilon, $"Remote WER regressed: {werRemote:F3} > {baseRemote:F3}+{Epsilon}");
        }
        finally { Directory.Delete(root, true); }
    }
}
```

- [ ] **Step 7: Run the fixtures** — `dotnet test --filter "Category=Fixture"` (models + corpus in place): silence gate PASS; golden pair records the baseline on first run (expected fail with the message), PASS on re-run. Record the measured baseline numbers in the corpus README's commit message — the design's v1 Definition-of-Done ship bar is set from this first measurement.

- [ ] **Step 8: Full unit gate + commit**

Run: `dotnet test --filter "Category!=Fixture"` -> all PASS; `dotnet build` -> 0 warnings.

```bash
git add src/LocalScribe.Core/Pipeline/WerCalculator.cs tests/LocalScribe.Core.Tests/WerCalculatorTests.cs \
        tests/LocalScribe.Core.Tests/GoldenCorpusFixtureTests.cs docs/plans/2026-07-02-stage-2b-golden-corpus.md
git commit -m "feat: WER calculator + golden-corpus regression fixture + silence hard bar"
```

---

## Stage 2b — Definition of Done

- [ ] `dotnet build` clean across all projects (`net10.0-windows`), **0 warnings**; only the declared packages added.
- [ ] `dotnet test --filter "Category!=Fixture"` green — the whole Stage-1 + 2a + 2b unit suite, zero hardware, zero model files.
- [ ] `dotnet test --filter "Category=Fixture"` green on a box with fetched models: real Silero probabilities, Whisper tiny/base load-and-run, golden-pair WER at baseline, **zero segments on 30 s of silence** (the hard bar).
- [ ] VAD honors every §4 behavior: threshold, min-speech drop, min-silence end, dual-side padding, max-segment force-cut at the last dip, force-flush on EOF.
- [ ] Backend cascade + downgrade per §3: table selection, user overrides win, `.en` model choice by language, VRAM-OOM → one ladder step + same-segment retry (never dropped), sustained RTF > 1 → one-shot `transcription lagging` marker + downgrade.
- [ ] Merge per §5: seq = finalization order in JSONL; view = `startMs` asc, `Local < Remote < System`, then seq; sorted-insert event carries the index for Stage 3's live view.
- [ ] Phantom-bleed dedup is render-only (JSONL keeps both copies), suppresses only near-simultaneous + matching-text + clearly-quieter Local segments, and never touches distinct-word or comparable-energy overlap.
- [ ] The OfflineRunner turns a Stage-1 WAV pair into a self-contained spec-§9 session folder (finalized `session.json` with recorded actuals + timezone, `meta.json`, JSONL with `rmsDb`, three projections, retained audio in the configured format).
- [ ] `[SMOKE]`: OfflineRunner verified once on real hardware (CPU box minimum; CUDA box if available) per Task 13 Step 3.

## Public surface produced for Stage 3 (interface index)

Stage 3 (live wiring + overlay + live view) consumes, from `LocalScribe.Core`:
- **Vad:** `IVadSegmenter`/`SileroVadSegmenter`, `VadCore` (per-source), `VadOptions`, `ISpeechProbabilityModel`/`SileroVadModel` — live capture feeds `ICaptureSource.FrameAvailable` frames into the same `VadCore.Push`; Pause/Stop call `Flush()` (spec §4).
- **Transcription:** `ITranscriptionEngine`/`WhisperNetEngine`, `IEngineFactory`/`WhisperEngineFactory`, `BackendSelector` (Stage 3 supplies a real `IHardwareProbe`), `ModelLadder`, `LanguageResolver`, `TranscriptionWorker` (+ its `MarkerRaised`/`ErrorRaised` events — Stage 3 routes `ErrorRaised` codes to the UI per §8.2), `TranscribedSegment`, `ModelPaths`.
- **Pipeline:** `AudioSegment`, `SegmentAudio`, `TranscriptMerger` (bind the live view to `LineInserted(index, line)`; a record inserting behind the newest is expected — spec §5), `OfflinePipelineRunner` (import-a-recording feature), `WavFileFrameReader`, `WerCalculator`.
- **Projection:** `PhantomBleedDedup` — Stage 3/4 wire it in place of `NoOpDedup` when constructing `TranscriptProjection` (2a's `SessionWriter` keeps `NoOpDedup` until then; swapping it is a one-line Stage 3 change plus corpus-tuned `PhantomBleedOptions`).
- **Audio:** `IAudioFileSink`/`WavAudioSink`/`FlacAudioSink`/`AudioSinkFactory` — the live pipeline streams retained audio through the same sinks.

Capture-side marker emission (`audio device changed`, `paused*`, `degraded: system-audio loopback`, `pinned microphone unavailable`) goes through `TranscriptMerger.AppendMarkerAsync` — the constants already exist in `Markers` (2a); Stage 3/7 raise them.

## Explicitly NOT in Stage 2b (recap)

Live capture wiring/overlay/live-view UI (Stage 3) · real `IHardwareProbe` (Stage 3) · capture-side marker emission + pre-flight probe wiring (Stage 3/7) · model download/SHA-pinning UX (Stage 7) · diarisation (Stage 5) · `.zip`/`.docx` export (Stage 6/fast-follow) · meeting auto-detect (deferred seam) · mid-meeting language switching (v1 non-goal).
