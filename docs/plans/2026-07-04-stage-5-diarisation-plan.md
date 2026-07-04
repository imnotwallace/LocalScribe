# Stage 5 - Split-speakers (On-demand Diarisation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add on-demand speaker diarisation ("Split speakers") that refines a recorded source leg into distinct, user-named speakers, stored as a non-destructive `speakers.json` overlay, driven from the read view and sessions list.

**Architecture:** A dedicated out-of-process helper (`LocalScribe.Diarizer.exe`) owns the `sherpa-onnx` NuGet - and therefore its own ONNX Runtime - so it never collides with Core's in-process `Microsoft.ML.OnnxRuntime 1.22.0` (Silero VAD). Core talks to the helper through an `IDiarisationEngine` humble-object seam that spawns the process, streams progress, and kills it to cancel. Cluster-to-segment mapping and the pin-preserving `speakers.json` merge are pure Core logic; the write is one `MaintenanceService` single-flight gate hold. The WPF Split-speakers dialog runs the engine, names clusters (with transcript preview + audio snippet), and confirms.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF + WPF-UI 4.0.3 + CommunityToolkit.Mvvm 8.4.0, xUnit 2.9.3 (hand-written fakes, no mocking library), `sherpa-onnx` 1.13.3 (helper only), `CUETools.Codecs.FLAKE` 1.0.5 (FLAC decode, already referenced), `Microsoft.ML.OnnxRuntime` 1.22.0 (Core, untouched by this stage).

**Design doc:** `docs/plans/2026-07-04-stage-5-diarisation-design.md` (approved 2026-07-04). Read it before starting; this plan implements it.

## Global Constraints

Every task's requirements implicitly include these. Values are copied verbatim from the design/specs and the repo.

- **Target framework:** `net10.0-windows`. `ImplicitUsings=enable`, `Nullable=enable` on every project. Only `LocalScribe.Core.csproj` sets `<LangVersion>latest</LangVersion>`; add it to a new project only if it needs newer C# features.
- **No global `TreatWarningsAsErrors`** exists; the zero-warning bar is discipline + scoped `<NoWarn>`. There is no `Directory.Build.props`/`.editorconfig` - each `.csproj` is standalone.
- **Test framework:** xUnit 2.9.3. Core.Tests has `<Using Include="Xunit" />` (test files omit `using Xunit;` and may be namespace-less). App.Tests does NOT - its files write `using Xunit;` and `namespace LocalScribe.App.Tests;`.
- **No mocking library** (no Moq/NSubstitute). All doubles are hand-written `public sealed class Fake<Name> : I<Name>` exposing public `List<...>`/counter fields.
- **ViewModels are WPF-free:** inject `dispatch` (type `Action<Action>`; tests pass `dispatch: a => a()`) and `System.TimeProvider` (tests pass `TimeProvider.System` or `ManualUtcTimeProvider`). No `DateTime.Now`/`Guid.NewGuid()` in logic.
- **Fixture tests** (real models) carry class-level `[Trait("Category", "Fixture")]` (capital C, capital F). The dev/CI gate is exactly: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`.
- **Two source enums, never conflate:** `LocalScribe.Core.Audio.SourceKind { Local, Remote }` (used by `SessionRecord.RetainedAudioSources`, `Speakers.DiarisedSources`, `StoragePaths.AudioFile`, `SessionParticipant.Side`) and `LocalScribe.Core.Model.TranscriptSource { Local, Remote, System }` (on `TranscriptLine`). **`speakers.json` `Assignments`/`Pinned` outer key is `TranscriptSource.ToString()` = `"Local"`/`"Remote"`; inner key is `seq.ToString()`.** `Names` key is the full clusterKey `"<source>:<clusterId>"`. `NameResolver` parses clusterId after the FIRST `':'` and looks up `Names` by the whole clusterKey.
- **Evidentiary invariants:** `transcript.jsonl` is append-only, never rewritten/redacted/reordered. All speaker data is an additive overlay keyed by immutable `seq`. Manual pinned reassignments in `speakers.json` `Pinned` survive every re-diarise verbatim. **The diarise commit performs NO audio deletion for any `AudioRetention` value** (the specs' `afterDiarisation` retention seam is deliberately not wired). No content deletion/hide/redact anywhere.
- **All UI disk mutation routes through `MaintenanceService`** (the one owner). ViewModels never call `SpeakersStore`/`SessionStore`/`SessionWriter` directly. `SessionWriter` is always constructed fresh from `settings.Current` inside a `RunForSessionAsync` lambda.
- **Offline / licensing:** model download is the only network touch; models are SHA-pinned and Apache/MIT-only (segmentation pyannote-3.0 = MIT; embedding `campplus_sv_zh_en_16k-common_advanced` = Apache-2.0, non-VoxCeleb). `sherpa-onnx` is Apache-2.0. `CUETools.Codecs.FLAKE` (LGPL-3.0) stays an unmodified, dynamically-linked assembly - never IL-merged or trimmed.
- **Humble Object** for every native/ML touch. All shipped libraries ARM64-safe; the helper publishes for win-x64 and win-arm64.
- **Commit style:** conventional commits with a stage/task tag, e.g. `feat: [Stage5 T3] add SherpaHelperDiariser`. Append the repo's `Co-Authored-By` / `Claude-Session` trailers.

---

## File Structure

**New - `LocalScribe.Core` (`src/LocalScribe.Core/Diarisation/`):**
- `IDiarisationEngine.cs` - the seam + `DiarisationRequest`, `DiarisationResult`, `DiarisedSegment` records.
- `DiarisationException.cs` - `DiarisationException` + `DiarisationErrorCode` enum.
- `DiarisationWire.cs` - JSON wire DTOs shared by Core and the helper exe (`DiarisationJob`, `DiarisationProgress`, `DiarisationResultPayload`, `DiarisationErrorPayload`) + `DiarisationJson.Options`.
- `FlacPcmReader.cs` - decodes a 16k/mono/16-bit FLAC (or WAV) leg to `float[]` via `FlakeReader`.
- `ClusterAssigner.cs` - pure: maps `DiarisedSegment[]` + transcript lines -> `seq -> clusterKey` for one source (max-overlap, tie-break, uncovered left out).
- `DiarisationCommit.cs` - the record the dialog hands to `MaintenanceService` (per-source assignments, names, sources, method, timestamp).
- `SpeakersMerge.cs` - pure: merges a `DiarisationCommit` into an existing `Speakers` (pin-preserving, non-pinned `Names` reset per re-diarised source, per-side default labels applied by the caller).
- `IDiarisationHelper.cs` - the process seam (`RunAsync(job, onStdoutLine, ct) -> Task<int>`).
- `SherpaHelperDiariser.cs` - `IDiarisationEngine` impl: orchestrates `IDiarisationHelper`, parses stdout, maps to the contract, kills on cancel.

**New - `LocalScribe.Diarizer` (`src/LocalScribe.Diarizer/`), the helper exe:**
- `LocalScribe.Diarizer.csproj` - console Exe, references Core + `org.k2fsa.sherpa.onnx` (+ win-x64/win-arm64 runtimes).
- `Program.cs` - reads the job from stdin, decodes FLAC, runs the runner, streams progress + result/error JSON to stdout.
- `SherpaDiarisationRunner.cs` - the ONLY code touching sherpa types (humble object on the exe side).

**New - `LocalScribe.App`:**
- `ViewModels/SplitSpeakersViewModel.cs` - the dialog VM (WPF-free).
- `SplitSpeakersWindow.xaml` / `.xaml.cs` - the dialog window (capture-excluded).
- `Services/ProcessDiarisationHelper.cs` - production `IDiarisationHelper` that spawns `LocalScribe.Diarizer.exe` (humble; not unit-tested).

**Modified:**
- `LocalScribe.slnx` - register the new projects.
- `src/LocalScribe.App/Services/MaintenanceService.cs` - add `SaveDiarisationAsync`.
- `src/LocalScribe.App/CompositionRoot.cs` + `App.xaml.cs` - construct/wire the engine.
- `src/LocalScribe.App/ReadViewWindow.xaml`(`.cs`) + `ViewModels/ReadViewViewModel.cs` - "Split speakers..." button.
- `src/LocalScribe.App/Pages/SessionsPage.xaml` + `ViewModels/SessionsPageViewModel.cs` - context-menu item; `Diarised` badge already bound.
- `tools/fetch-models.ps1` - add the two models + SHA verification.
- `README.md` + `docs/specs/localscribe-specs.md` - roadmap tick + spec amendment.

**New tests:**
- `tests/LocalScribe.Core.Tests/`: `DiarisationWireTests.cs`, `FlacPcmReaderTests.cs`, `ClusterAssignerTests.cs`, `SpeakersMergeTests.cs`, `SherpaHelperDiariserTests.cs`, `DiarisationFixtureTests.cs` (opt-in).
- `tests/LocalScribe.App.Tests/`: `MaintenanceServiceDiarisationTests.cs`, `SplitSpeakersViewModelTests.cs`.

---

## Task 0: Spike - prove the process-isolation architecture (GATE)

**This task is an exploratory spike, not TDD.** It exists to de-risk the two empirically-unverified areas (the ONNX Runtime collision and the exact `sherpa-onnx` C# API + `FlakeReader` API) before any production code depends on them. Its deliverable is a throwaway helper that runs end-to-end plus a short decision record. Nothing downstream proceeds until this passes.

**Files:**
- Create (throwaway, on a spike branch): `src/LocalScribe.Diarizer/` minimal Exe.
- Create: `docs/plans/2026-07-04-stage-5-spike-notes.md` (kept - records confirmed API signatures + the decision).

- [ ] **Step 1: Scaffold the helper project**

Create `src/LocalScribe.Diarizer/LocalScribe.Diarizer.csproj` mirroring `LocalScribe.OfflineRunner.csproj`, but referencing sherpa-onnx instead of the Whisper runtimes:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <Platforms>x64;ARM64</Platforms>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\LocalScribe.Core\LocalScribe.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- Apache-2.0. Owns its OWN onnxruntime.dll (1.24.4) in THIS process only. -->
    <PackageReference Include="org.k2fsa.sherpa.onnx" Version="1.13.3" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Confirm ORT isolation (the load-bearing question)**

Add the project to `LocalScribe.slnx` (a `<Project Path="src/LocalScribe.Diarizer/LocalScribe.Diarizer.csproj" />` line inside `<Folder Name="/src/">`). Then:

```bash
dotnet publish src/LocalScribe.Diarizer -r win-x64 -c Release
dotnet publish src/LocalScribe.Diarizer -r win-arm64 -c Release
```

Confirm BOTH publish outputs contain `sherpa-onnx-c-api.dll` and their OWN `onnxruntime.dll` (1.24.4), and that this is a SEPARATE binary directory from `LocalScribe.App` (which carries Microsoft's 1.22.0). Record the exact published `onnxruntime.dll` versions. Expected: two independent binaries, no shared `onnxruntime.dll` - confirming the two runtimes never coexist in one process.

- [ ] **Step 3: Confirm the sherpa C# API and run one real diarisation**

Fetch the two models manually (segmentation `sherpa-onnx-pyannote-segmentation-3-0`, embedding `3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced` - URLs in Task 1). Write a throwaway `Program.cs` that decodes a real 16k/mono FLAC leg (from any retained test session) and runs sherpa's offline speaker diarisation. Confirm the actual API surface and record it verbatim in the spike notes:
- The diarisation class + config type names (research indicates `OfflineSpeakerDiarization` + `OfflineSpeakerDiarizationConfig` with `Segmentation.Pyannote.Model`, `Embedding.Model`, `Clustering` = `FastClusteringConfig { NumClusters, Threshold }`).
- The process/run method + progress-callback signature (research indicates `ProcessWithCallback(float[] samples, callback)` where the callback receives processed/total chunk counts; the return value is ignored - no cooperative cancel).
- The result shape (segments with start/end seconds + speaker index).
- The `FlakeReader` read-loop API from `CUETools.Codecs.FLAKE` (ctor, `PCM` config, `Read(AudioBuffer, maxLength)`, how to get int16 bytes out).

- [ ] **Step 4: Record the decision**

Write `docs/plans/2026-07-04-stage-5-spike-notes.md` capturing: the confirmed sherpa API names, the confirmed `FlakeReader` API, the confirmed ORT isolation result, and the measured real-time factor on this machine. **Exit criterion:** the helper runs a full diarisation on both arches (or, if ARM64 fails, the notes record falling back to Approach B/CLI for ARM64 per the design). If the spike shows in-process is somehow required, STOP and re-brainstorm - do not proceed.

- [ ] **Step 5: Commit the spike notes; reset the throwaway code**

```bash
git add docs/plans/2026-07-04-stage-5-spike-notes.md
git commit -m "docs: [Stage5 T0] diarisation spike notes - confirm ORT isolation + sherpa/FlakeReader API"
```

Keep `LocalScribe.Diarizer.csproj` and its slnx entry (Task 2 builds on them); delete the throwaway `Program.cs` body (Task 2 rewrites it via TDD). Reconcile any API-name drift from Step 3 into Tasks 2 and later before implementing them.

---

## Task 1: Extend fetch-models.ps1 with the two SHA-pinned diarisation models

**Files:**
- Modify: `tools/fetch-models.ps1`

**Interfaces:**
- Produces: two model files in `<repo>/models/` resolvable via `ModelPaths.Require("sherpa-onnx-pyannote-segmentation-3-0/model.onnx")` and `ModelPaths.Require("3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx")` (exact resolved names decided in Step 1).

- [ ] **Step 1: Decide the on-disk model filenames**

The segmentation model ships as a `.tar.bz2` containing `model.onnx`; the embedding model is a bare `.onnx`. Decide the extracted layout under `models/`: segmentation extracted to `models/sherpa-onnx-pyannote-segmentation-3-0/model.onnx`, embedding saved as `models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx`. These exact relative names are what the VM passes as `SegmentationModelPath`/`EmbeddingModelPath` in the `DiarisationJob` (resolved via `ModelPaths.Resolve(...)` in Task 8); the helper reads them from the stdin job, not from CLI flags.

- [ ] **Step 2: Add the models + SHA-256 verification to the script**

The current `$files` entries are `@{ Name; Url }` with no hash check. Add the two diarisation models with pinned SHA-256 and make the download fail closed on mismatch. Append to the `$files` array and extend the loop:

```powershell
# --- Stage 5 diarisation models (Apache/MIT only, SHA-pinned) ---
# Embedding: 3D-Speaker CAM++ zh+en common (Apache-2.0, non-VoxCeleb). Vendor checksum.txt.
@{ Name = '3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
   Url  = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
   Sha256 = 'aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2' }
```

The pinned hashes here come from the research pass (2026-07-04); **before committing, re-verify each by downloading once and running `Get-FileHash -Algorithm SHA256`, and record the values in the spike notes.** For the segmentation model (a `.tar.bz2` with no vendor hash), pin the SHA-256 of the extracted `model.onnx` self-computed once, and extract the tarball in the script. Update the `foreach` loop so that when an entry has a `Sha256` key, after download it computes `(Get-FileHash -Algorithm SHA256 $dest).Hash` and `throw` if it does not case-insensitively equal the pin (delete the bad file first). Leave the existing whisper/silero entries (no `Sha256` key) behaving as today. Note the deliberate upstream typo `speaker-recongition-models` in the embedding URL - do not "fix" it.

- [ ] **Step 3: Run the script and verify both models land and verify**

Run: `pwsh tools/fetch-models.ps1`
Expected: both diarisation files download, hash-verify (no throw), and land at the Step 1 paths; a re-run prints `exists:` and re-verifies without re-downloading.

- [ ] **Step 4: Commit**

```bash
git add tools/fetch-models.ps1
git commit -m "feat: [Stage5 T1] fetch-models: SHA-pinned pyannote + CAM++ diarisation models"
```

---

## Task 2: Diarisation wire contract + FLAC decode (Core)

Builds the shared JSON contract both sides speak, and the FLAC->float decode the helper needs. Both are pure Core logic, unit-testable without any process or model.

**Files:**
- Create: `src/LocalScribe.Core/Diarisation/DiarisationWire.cs`
- Create: `src/LocalScribe.Core/Diarisation/FlacPcmReader.cs`
- Test: `tests/LocalScribe.Core.Tests/DiarisationWireTests.cs`, `tests/LocalScribe.Core.Tests/FlacPcmReaderTests.cs`

**Interfaces:**
- Produces:
  - `DiarisationJob(string FlacPath, string Source, string SegmentationModelPath, string EmbeddingModelPath, int? ForcedClusterCount)` - stdin job spec. `Source` is `"Local"`/`"Remote"`.
  - `DiarisationProgress(double Progress)` - a `{"progress":0.42}` stdout line.
  - `DiarisationResultPayload(IReadOnlyList<WireSegment> Segments, int ClusterCount, string Method)` with `WireSegment(long StartMs, long EndMs, int Cluster)` - the final stdout object.
  - `DiarisationErrorPayload(string Error, string? Detail)` - the failure stdout object.
  - `static class DiarisationJson { JsonSerializerOptions Options }` - camelCase, shared by both sides.
  - `static class FlacPcmReader { float[] ReadMono16k(string path) }` - decodes a 16k/mono FLAC or WAV to float samples; throws `InvalidDataException` if not 16000 Hz mono.

- [ ] **Step 1: Write the failing wire-contract test**

`tests/LocalScribe.Core.Tests/DiarisationWireTests.cs`:

```csharp
using System.Text.Json;
using LocalScribe.Core.Diarisation;

public class DiarisationWireTests
{
    [Fact]
    public void Job_round_trips_camelCase()
    {
        var job = new DiarisationJob("C:\\s\\remote.flac", "Remote", "seg.onnx", "emb.onnx", 3);
        string json = JsonSerializer.Serialize(job, DiarisationJson.Options);
        Assert.Contains("\"flacPath\"", json);
        Assert.Contains("\"forcedClusterCount\":3", json);
        var back = JsonSerializer.Deserialize<DiarisationJob>(json, DiarisationJson.Options)!;
        Assert.Equal("Remote", back.Source);
        Assert.Equal(3, back.ForcedClusterCount);
    }

    [Fact]
    public void Result_and_error_payloads_deserialize_from_helper_lines()
    {
        string resultLine = "{\"segments\":[{\"startMs\":0,\"endMs\":1500,\"cluster\":0}],\"clusterCount\":2,\"method\":\"sherpa\"}";
        var r = JsonSerializer.Deserialize<DiarisationResultPayload>(resultLine, DiarisationJson.Options)!;
        Assert.Equal(2, r.ClusterCount);
        Assert.Single(r.Segments);
        Assert.Equal(1500, r.Segments[0].EndMs);

        string errLine = "{\"error\":\"MODEL_MISSING\",\"detail\":\"no file\"}";
        var e = JsonSerializer.Deserialize<DiarisationErrorPayload>(errLine, DiarisationJson.Options)!;
        Assert.Equal("MODEL_MISSING", e.Error);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~DiarisationWireTests"`
Expected: FAIL to compile - `DiarisationJob`/`DiarisationJson` do not exist.

- [ ] **Step 3: Implement the wire contract**

`src/LocalScribe.Core/Diarisation/DiarisationWire.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalScribe.Core.Diarisation;

public sealed record DiarisationJob(
    string FlacPath,
    string Source,                 // "Local" / "Remote" (TranscriptSource string)
    string SegmentationModelPath,
    string EmbeddingModelPath,
    int? ForcedClusterCount);      // null = auto (threshold); N = force exactly N

public sealed record DiarisationProgress(double Progress);

public sealed record WireSegment(long StartMs, long EndMs, int Cluster);

public sealed record DiarisationResultPayload(
    IReadOnlyList<WireSegment> Segments,
    int ClusterCount,
    string Method);

public sealed record DiarisationErrorPayload(string Error, string? Detail);

public static class DiarisationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
```

- [ ] **Step 4: Run the wire test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~DiarisationWireTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing FLAC round-trip test**

This test encodes known samples with the existing `FlacAudioSink`, then decodes with the new reader - a same-library round-trip that both proves correctness and pins the exact `FlakeReader` API. `tests/LocalScribe.Core.Tests/FlacPcmReaderTests.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;

public class FlacPcmReaderTests
{
    [Fact]
    public void Decodes_flac_written_by_FlacAudioSink_within_pcm_tolerance()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "remote.flac");
        try
        {
            // 8000 samples (0.5s @ 16k) of a low-amplitude ramp/sine, mono.
            var samples = new float[8000];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = 0.25f * MathF.Sin(i * 0.05f);

            using (var sink = new FlacAudioSink(path))
                sink.Write(samples);          // FLAC is lossless for 16-bit PCM

            float[] decoded = FlacPcmReader.ReadMono16k(path);

            Assert.Equal(samples.Length, decoded.Length);
            for (int i = 0; i < samples.Length; i++)
                Assert.True(Math.Abs(samples[i] - decoded[i]) < 1.0f / 32768f + 1e-6f,
                    $"sample {i}: {samples[i]} vs {decoded[i]}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Rejects_non_16k_or_multichannel()
    {
        // A WAV at the wrong rate must throw InvalidDataException (guard for foreign files).
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "bad.wav");
        try
        {
            WriteWavHeaderOnly(path, sampleRate: 44100, channels: 2);
            Assert.Throws<InvalidDataException>(() => FlacPcmReader.ReadMono16k(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void WriteWavHeaderOnly(string path, int sampleRate, short channels)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels);
        w.Write(sampleRate); w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(0);
    }
}
```

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~FlacPcmReaderTests"`
Expected: FAIL - `FlacPcmReader` does not exist.

- [ ] **Step 7: Implement `FlacPcmReader`**

Use `CUETools.Codecs.FLAKE.FlakeReader` for `.flac`; for `.wav` reuse NAudio (as `WavFileFrameReader` does) to keep the guard cheap. Convert int16 bytes via `PcmConverter.Int16BytesToFloat`. `src/LocalScribe.Core/Diarisation/FlacPcmReader.cs`:

```csharp
using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using LocalScribe.Core.Audio;
using NAudio.Wave;

namespace LocalScribe.Core.Diarisation;

// Decodes a retained 16 kHz / mono / 16-bit leg to float samples for diarisation.
// Counts samples from file start with NO leading trim, so sampleIndex = ms * 16000 / 1000
// stays valid against the AlignedAudioWriter mapping.
public static class FlacPcmReader
{
    public static float[] ReadMono16k(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".wav" ? ReadWav(path) : ReadFlac(path);
    }

    private static float[] ReadFlac(string path)
    {
        using var reader = new FlakeReader(path, null);
        AudioPCMConfig pcm = reader.PCM;
        if (pcm.SampleRate != 16000 || pcm.ChannelCount != 1)
            throw new InvalidDataException(
                $"Diarisation input must be 16 kHz mono; got {pcm.SampleRate} Hz / {pcm.ChannelCount} ch: {path}");

        var samples = new List<float>((int)Math.Max(0, reader.Length));
        var buffer = new AudioBuffer(pcm, 16384);
        int n;
        while ((n = reader.Read(buffer, 16384)) > 0)
        {
            // AudioBuffer exposes interleaved int16 little-endian bytes for a 16-bit config.
            ReadOnlySpan<byte> bytes = buffer.Bytes.AsSpan(0, n * pcm.BlockAlign);
            samples.AddRange(PcmConverter.Int16BytesToFloat(bytes));
        }
        return samples.ToArray();
    }

    private static float[] ReadWav(string path)
    {
        using var reader = new AudioFileReader(path);
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1)
            throw new InvalidDataException(
                $"Diarisation input must be 16 kHz mono; got {reader.WaveFormat.SampleRate} Hz / {reader.WaveFormat.Channels} ch: {path}");
        var all = new List<float>();
        var buf = new float[16000];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            all.AddRange(buf.AsSpan(0, n).ToArray());
        return all.ToArray();
    }
}
```

> NOTE for the implementer: `FlakeReader`'s exact read API (`buffer.Bytes`, `Read(buffer, count)` returning sample count, `reader.PCM`, `reader.Length`) is confirmed in the Task 0 spike. If a member name differs, reconcile here - the round-trip test in Step 5 is the oracle: make it pass without changing the assertion.

- [ ] **Step 8: Run both FLAC tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~FlacPcmReaderTests"`
Expected: PASS (both).

- [ ] **Step 9: Commit**

```bash
git add src/LocalScribe.Core/Diarisation/DiarisationWire.cs src/LocalScribe.Core/Diarisation/FlacPcmReader.cs tests/LocalScribe.Core.Tests/DiarisationWireTests.cs tests/LocalScribe.Core.Tests/FlacPcmReaderTests.cs
git commit -m "feat: [Stage5 T2] diarisation wire contract + FLAC decode"
```

---

## Task 3: The helper exe (LocalScribe.Diarizer)

Turns the throwaway spike into the real helper: stdin job -> decode -> sherpa run -> streamed progress + result/error JSON. The sherpa-touching code is isolated in `SherpaDiarisationRunner`. This task has no Core unit test (it is the process boundary / native touch, exercised by the Task 9 fixture); its correctness gate is a manual run against a real leg.

**Files:**
- Modify: `src/LocalScribe.Diarizer/LocalScribe.Diarizer.csproj` (from Task 0)
- Create: `src/LocalScribe.Diarizer/Program.cs`
- Create: `src/LocalScribe.Diarizer/SherpaDiarisationRunner.cs`

**Interfaces:**
- Consumes: `DiarisationJob`, `DiarisationProgress`, `DiarisationResultPayload`, `WireSegment`, `DiarisationErrorPayload`, `DiarisationJson.Options`, `FlacPcmReader.ReadMono16k` (Task 2).
- Produces: an executable whose stdin is one `DiarisationJob` JSON object and whose stdout is zero or more `{"progress":..}` lines followed by exactly one result or error object; exit code 0 on success, non-zero on error.

- [ ] **Step 1: Write `SherpaDiarisationRunner` (the only sherpa-touching type)**

`src/LocalScribe.Diarizer/SherpaDiarisationRunner.cs`. Use the exact API names confirmed in the Task 0 spike; the shape below matches the researched surface:

```csharp
using SherpaOnnx;   // confirm namespace in the spike
using LocalScribe.Core.Diarisation;

namespace LocalScribe.Diarizer;

// Humble object over sherpa-onnx OfflineSpeakerDiarization. No LocalScribe logic here.
internal sealed class SherpaDiarisationRunner
{
    public DiarisationResultPayload Run(
        float[] samples16kMono,
        string segModelPath,
        string embModelPath,
        int? forcedClusterCount,
        Action<double> onProgress)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segModelPath;
        config.Embedding.Model = embModelPath;
        if (forcedClusterCount is int k && k > 0)
            config.Clustering.NumClusters = k;      // hard forced count
        else
            config.Clustering.Threshold = 0.5f;      // auto; threshold pinned in spike

        using var sd = new OfflineSpeakerDiarization(config);
        // Progress callback receives processed/total chunk counts; return value is ignored.
        var result = sd.ProcessWithCallback(samples16kMono, (processed, total, _) =>
        {
            if (total > 0) onProgress(Math.Clamp((double)processed / total, 0, 1));
            return 0;
        });

        var segments = result.SortByStartTime()
            .Select(s => new WireSegment(
                StartMs: (long)Math.Round(s.Start * 1000),
                EndMs:   (long)Math.Round(s.End * 1000),
                Cluster: s.Speaker))
            .ToList();
        int clusterCount = segments.Select(s => s.Cluster).DefaultIfEmpty(-1).Max() + 1;
        return new DiarisationResultPayload(segments, clusterCount,
            "sherpa-onnx:pyannote-seg-3.0+campplus-zh-en");
    }
}
```

- [ ] **Step 2: Write `Program.cs` (stdin/stdout framing + error taxonomy)**

`src/LocalScribe.Diarizer/Program.cs`:

```csharp
using System.Text.Json;
using LocalScribe.Core.Diarisation;
using LocalScribe.Diarizer;

var stdout = Console.Out;

void Emit(object payload) => stdout.WriteLine(JsonSerializer.Serialize(payload, DiarisationJson.Options));
int Fail(string code, string detail) { Emit(new DiarisationErrorPayload(code, detail)); return 1; }

try
{
    string input = await Console.In.ReadToEndAsync();
    var job = JsonSerializer.Deserialize<DiarisationJob>(input, DiarisationJson.Options)
              ?? throw new InvalidDataException("empty job");

    if (!File.Exists(job.SegmentationModelPath) || !File.Exists(job.EmbeddingModelPath))
        return Fail("MODEL_MISSING", "segmentation or embedding model file not found");

    float[] samples;
    try { samples = FlacPcmReader.ReadMono16k(job.FlacPath); }
    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
    { return Fail("BAD_AUDIO", ex.Message); }

    var runner = new SherpaDiarisationRunner();
    var result = runner.Run(samples, job.SegmentationModelPath, job.EmbeddingModelPath,
        job.ForcedClusterCount, p => Emit(new DiarisationProgress(p)));
    Emit(result);
    return 0;
}
catch (Exception ex)
{
    return Fail("HELPER_CRASH", ex.Message);
}
```

- [ ] **Step 3: Build the helper**

Run: `dotnet build src/LocalScribe.Diarizer`
Expected: build succeeds, zero warnings.

- [ ] **Step 4: Manual end-to-end run against a real leg**

With the Task 1 models fetched and a retained 16k FLAC leg available, pipe a job in:

```bash
echo '{"flacPath":"<path>\\remote.flac","source":"Remote","segmentationModelPath":"<models>\\sherpa-onnx-pyannote-segmentation-3-0\\model.onnx","embeddingModelPath":"<models>\\3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx","forcedClusterCount":null}' | dotnet run --project src/LocalScribe.Diarizer
```

Expected: a stream of `{"progress":...}` lines then one `{"segments":[...],"clusterCount":N,"method":"..."}` line; exit code 0. Try a missing model path -> a single `{"error":"MODEL_MISSING",...}` line and exit 1.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Diarizer/
git commit -m "feat: [Stage5 T3] LocalScribe.Diarizer helper: stdin job -> sherpa -> streamed JSON"
```

---

## Task 4: IDiarisationEngine seam + SherpaHelperDiariser (Core)

The humble object Core uses to talk to the helper: spawn (behind an injectable `IDiarisationHelper`), parse stdout, map to the typed contract, kill on cancel. Fully unit-testable via a fake helper that emits canned lines.

**Files:**
- Create: `src/LocalScribe.Core/Diarisation/IDiarisationEngine.cs`
- Create: `src/LocalScribe.Core/Diarisation/DiarisationException.cs`
- Create: `src/LocalScribe.Core/Diarisation/IDiarisationHelper.cs`
- Create: `src/LocalScribe.Core/Diarisation/SherpaHelperDiariser.cs`
- Test: `tests/LocalScribe.Core.Tests/SherpaHelperDiariserTests.cs`

**Interfaces:**
- Consumes: `DiarisationJob`, `DiarisationProgress`, `DiarisationResultPayload`, `DiarisationErrorPayload`, `DiarisationJson` (Task 2); `SourceKind` (Core.Audio).
- Produces:
  - `interface IDiarisationEngine { Task<DiarisationResult> DiariseAsync(DiarisationRequest request, IProgress<double> progress, CancellationToken ct); }`
  - `DiarisationRequest(string FlacPath, SourceKind Source, string SegmentationModelPath, string EmbeddingModelPath, int? ForcedClusterCount)`
  - `DiarisationResult(IReadOnlyList<DiarisedSegment> Segments, int ClusterCount, string Method)`, `DiarisedSegment(long StartMs, long EndMs, int Cluster)`
  - `enum DiarisationErrorCode { ModelDownloadFailed, BadAudio, HelperCrash }`, `DiarisationException(DiarisationErrorCode Code, string Message)`
  - `interface IDiarisationHelper { Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct); }`
  - `sealed class SherpaHelperDiariser(IDiarisationHelper helper) : IDiarisationEngine`

- [ ] **Step 1: Write the failing test**

`tests/LocalScribe.Core.Tests/SherpaHelperDiariserTests.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;

public class SherpaHelperDiariserTests
{
    private static DiarisationRequest Req() =>
        new("remote.flac", SourceKind.Remote, "seg.onnx", "emb.onnx", null);

    private sealed class FakeHelper : IDiarisationHelper
    {
        private readonly string[] _lines;
        private readonly int _exit;
        public bool Cancelled { get; private set; }
        public FakeHelper(int exit, params string[] lines) { _exit = exit; _lines = lines; }
        public async Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct)
        {
            foreach (var l in _lines) { ct.ThrowIfCancellationRequested(); onStdoutLine(l); await Task.Yield(); }
            return _exit;
        }
    }

    [Fact]
    public async Task Parses_progress_then_result()
    {
        var helper = new FakeHelper(0,
            "{\"progress\":0.5}",
            "{\"segments\":[{\"startMs\":0,\"endMs\":1000,\"cluster\":0}],\"clusterCount\":2,\"method\":\"sherpa\"}");
        var seen = new List<double>();
        var progress = new Progress<double>(seen.Add);

        var result = await new SherpaHelperDiariser(helper).DiariseAsync(Req(), progress, default);

        Assert.Equal(2, result.ClusterCount);
        Assert.Single(result.Segments);
        Assert.Equal(1000, result.Segments[0].EndMs);
    }

    [Fact]
    public async Task Error_line_maps_MODEL_MISSING_to_ModelDownloadFailed()
    {
        var helper = new FakeHelper(1, "{\"error\":\"MODEL_MISSING\",\"detail\":\"nope\"}");
        var ex = await Assert.ThrowsAsync<DiarisationException>(
            () => new SherpaHelperDiariser(helper).DiariseAsync(Req(), new Progress<double>(_ => { }), default));
        Assert.Equal(DiarisationErrorCode.ModelDownloadFailed, ex.Code);
    }

    [Fact]
    public async Task Nonzero_exit_without_error_line_is_HelperCrash()
    {
        var helper = new FakeHelper(3, "{\"progress\":0.1}");
        var ex = await Assert.ThrowsAsync<DiarisationException>(
            () => new SherpaHelperDiariser(helper).DiariseAsync(Req(), new Progress<double>(_ => { }), default));
        Assert.Equal(DiarisationErrorCode.HelperCrash, ex.Code);
    }

    [Fact]
    public async Task Cancellation_propagates_as_OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var helper = new FakeHelper(0, "{\"progress\":0.1}");
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new SherpaHelperDiariser(helper).DiariseAsync(Req(), new Progress<double>(_ => { }), cts.Token));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SherpaHelperDiariserTests"`
Expected: FAIL to compile - types missing.

- [ ] **Step 3: Implement the seam types**

`src/LocalScribe.Core/Diarisation/IDiarisationEngine.cs`:

```csharp
using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Diarisation;

public interface IDiarisationEngine
{
    Task<DiarisationResult> DiariseAsync(
        DiarisationRequest request, IProgress<double> progress, CancellationToken ct);
}

public sealed record DiarisationRequest(
    string FlacPath, SourceKind Source,
    string SegmentationModelPath, string EmbeddingModelPath,
    int? ForcedClusterCount);

public sealed record DiarisedSegment(long StartMs, long EndMs, int Cluster);

public sealed record DiarisationResult(
    IReadOnlyList<DiarisedSegment> Segments, int ClusterCount, string Method);
```

`src/LocalScribe.Core/Diarisation/DiarisationException.cs`:

```csharp
namespace LocalScribe.Core.Diarisation;

public enum DiarisationErrorCode { ModelDownloadFailed, BadAudio, HelperCrash }

public sealed class DiarisationException(DiarisationErrorCode code, string message)
    : Exception(message)
{
    public DiarisationErrorCode Code { get; } = code;
}
```

`src/LocalScribe.Core/Diarisation/IDiarisationHelper.cs`:

```csharp
namespace LocalScribe.Core.Diarisation;

// Process-boundary seam. Production impl (App) spawns LocalScribe.Diarizer.exe and,
// on cancellation, kills it. Tests supply a fake that emits canned stdout lines.
public interface IDiarisationHelper
{
    Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct);
}
```

- [ ] **Step 4: Implement `SherpaHelperDiariser`**

`src/LocalScribe.Core/Diarisation/SherpaHelperDiariser.cs`:

```csharp
using System.Text.Json;

namespace LocalScribe.Core.Diarisation;

public sealed class SherpaHelperDiariser(IDiarisationHelper helper) : IDiarisationEngine
{
    public async Task<DiarisationResult> DiariseAsync(
        DiarisationRequest request, IProgress<double> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var job = new DiarisationJob(request.FlacPath, request.Source.ToString(),
            request.SegmentationModelPath, request.EmbeddingModelPath, request.ForcedClusterCount);

        DiarisationResultPayload? result = null;
        DiarisationErrorPayload? error = null;

        void OnLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // Peek at keys to route without exceptions on the hot progress path.
            if (line.Contains("\"progress\""))
            {
                var p = JsonSerializer.Deserialize<DiarisationProgress>(line, DiarisationJson.Options);
                if (p is not null) progress.Report(p.Progress);
            }
            else if (line.Contains("\"error\""))
                error = JsonSerializer.Deserialize<DiarisationErrorPayload>(line, DiarisationJson.Options);
            else if (line.Contains("\"segments\""))
                result = JsonSerializer.Deserialize<DiarisationResultPayload>(line, DiarisationJson.Options);
        }

        int exit = await helper.RunAsync(job, OnLine, ct);   // throws OperationCanceledException on cancel

        if (error is not null)
            throw new DiarisationException(MapError(error.Error), error.Detail ?? error.Error);
        if (exit != 0 || result is null)
            throw new DiarisationException(DiarisationErrorCode.HelperCrash,
                $"diarisation helper exited {exit} without a result");

        var segments = result.Segments
            .Select(s => new DiarisedSegment(s.StartMs, s.EndMs, s.Cluster)).ToList();
        return new DiarisationResult(segments, result.ClusterCount, result.Method);
    }

    private static DiarisationErrorCode MapError(string code) => code switch
    {
        "MODEL_MISSING" => DiarisationErrorCode.ModelDownloadFailed,
        "BAD_AUDIO" => DiarisationErrorCode.BadAudio,
        _ => DiarisationErrorCode.HelperCrash,
    };
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SherpaHelperDiariserTests"`
Expected: PASS (all four).

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Diarisation/IDiarisationEngine.cs src/LocalScribe.Core/Diarisation/DiarisationException.cs src/LocalScribe.Core/Diarisation/IDiarisationHelper.cs src/LocalScribe.Core/Diarisation/SherpaHelperDiariser.cs tests/LocalScribe.Core.Tests/SherpaHelperDiariserTests.cs
git commit -m "feat: [Stage5 T4] IDiarisationEngine + SherpaHelperDiariser (parse/map/cancel)"
```

---

## Task 5: ClusterAssigner - map diarised segments to transcript seqs (Core)

Pure logic: assign each of a source's transcript segments to the cluster with the greatest time overlap; deterministic tie-break; uncovered seqs left unassigned.

**Files:**
- Create: `src/LocalScribe.Core/Diarisation/ClusterAssigner.cs`
- Test: `tests/LocalScribe.Core.Tests/ClusterAssignerTests.cs`

**Interfaces:**
- Consumes: `DiarisedSegment` (Task 4), `TranscriptLine`, `TranscriptSource`, `SourceKind`.
- Produces: `static class ClusterAssigner { ClusterAssignment Assign(IReadOnlyList<TranscriptLine> lines, IReadOnlyList<DiarisedSegment> segments, SourceKind source) }` returning `ClusterAssignment(IReadOnlyDictionary<string,string> SeqToClusterKey, IReadOnlyList<string> ClusterKeys)`. Keys are `seq.ToString()`; clusterKey is `"<Source>:<clusterId>"` where `<Source>` is the `TranscriptSource` string.

- [ ] **Step 1: Write the failing test**

`tests/LocalScribe.Core.Tests/ClusterAssignerTests.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;

public class ClusterAssignerTests
{
    private static TranscriptLine Seg(int seq, long start, long end) =>
        TranscriptLine.Segment(seq, TranscriptSource.Remote, start, end, "x", "Them");

    [Fact]
    public void Assigns_each_seq_to_max_overlap_cluster()
    {
        var lines = new[] { Seg(1, 0, 1000), Seg(2, 1000, 2000) };
        var segs = new[]
        {
            new DiarisedSegment(0, 1100, 0),     // covers seq 1 fully, small bit of seq 2
            new DiarisedSegment(1100, 2000, 1),  // covers most of seq 2
        };
        var a = ClusterAssigner.Assign(lines, segs, SourceKind.Remote);

        Assert.Equal("Remote:0", a.SeqToClusterKey["1"]);
        Assert.Equal("Remote:1", a.SeqToClusterKey["2"]);
        Assert.Equal(new[] { "Remote:0", "Remote:1" }, a.ClusterKeys);
    }

    [Fact]
    public void Uncovered_seq_is_left_unassigned()
    {
        var lines = new[] { Seg(1, 0, 500), Seg(2, 5000, 5500) };   // seq 2 in a diariser gap
        var segs = new[] { new DiarisedSegment(0, 1000, 0) };
        var a = ClusterAssigner.Assign(lines, segs, SourceKind.Remote);

        Assert.True(a.SeqToClusterKey.ContainsKey("1"));
        Assert.False(a.SeqToClusterKey.ContainsKey("2"));
    }

    [Fact]
    public void Equal_overlap_breaks_to_lower_cluster_id()
    {
        var lines = new[] { Seg(1, 0, 1000) };
        var segs = new[]
        {
            new DiarisedSegment(0, 500, 1),   // 500ms overlap, cluster 1
            new DiarisedSegment(500, 1000, 0) // 500ms overlap, cluster 0
        };
        var a = ClusterAssigner.Assign(lines, segs, SourceKind.Remote);
        Assert.Equal("Remote:0", a.SeqToClusterKey["1"]);
    }

    [Fact]
    public void Ignores_other_source_and_markers()
    {
        var lines = new[]
        {
            TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "x", "Me"),
            TranscriptLine.Marker(2, 0, "paused"),
            Seg(3, 0, 1000),
        };
        var segs = new[] { new DiarisedSegment(0, 1000, 0) };
        var a = ClusterAssigner.Assign(lines, segs, SourceKind.Remote);
        Assert.Equal(new[] { "3" }, a.SeqToClusterKey.Keys.ToArray());
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ClusterAssignerTests"`
Expected: FAIL - `ClusterAssigner` missing.

- [ ] **Step 3: Implement `ClusterAssigner`**

`src/LocalScribe.Core/Diarisation/ClusterAssigner.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.Diarisation;

public sealed record ClusterAssignment(
    IReadOnlyDictionary<string, string> SeqToClusterKey,
    IReadOnlyList<string> ClusterKeys);

public static class ClusterAssigner
{
    public static ClusterAssignment Assign(
        IReadOnlyList<TranscriptLine> lines,
        IReadOnlyList<DiarisedSegment> segments,
        SourceKind source)
    {
        // speakers.json outer key uses the TranscriptSource string; Local/Remote match.
        var wanted = source == SourceKind.Local ? TranscriptSource.Local : TranscriptSource.Remote;
        string prefix = wanted.ToString();

        var seqToCluster = new Dictionary<string, string>();
        var clusterIds = new SortedSet<int>();

        foreach (var line in lines)
        {
            if (line.Kind != TranscriptKind.Segment || line.Source != wanted) continue;

            long bestOverlap = 0;
            int bestCluster = -1;
            foreach (var s in segments)
            {
                long overlap = Math.Min(line.EndMs, s.EndMs) - Math.Max(line.StartMs, s.StartMs);
                if (overlap <= 0) continue;
                // max overlap; tie -> lower cluster id
                if (overlap > bestOverlap || (overlap == bestOverlap && s.Cluster < bestCluster))
                {
                    bestOverlap = overlap;
                    bestCluster = s.Cluster;
                }
            }
            if (bestCluster < 0) continue;   // uncovered: leave unassigned

            seqToCluster[line.Seq.ToString()] = $"{prefix}:{bestCluster}";
            clusterIds.Add(bestCluster);
        }

        var clusterKeys = clusterIds.Select(id => $"{prefix}:{id}").ToList();
        return new ClusterAssignment(seqToCluster, clusterKeys);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ClusterAssignerTests"`
Expected: PASS (all four).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Diarisation/ClusterAssigner.cs tests/LocalScribe.Core.Tests/ClusterAssignerTests.cs
git commit -m "feat: [Stage5 T5] ClusterAssigner: max-overlap seq->cluster mapping"
```

---

## Task 6: SpeakersMerge - pin-preserving speakers.json merge (Core)

Pure logic: fold a `DiarisationCommit` into an existing `Speakers`, preserving `Pinned` verbatim, dropping non-pinned `Names` for re-diarised sources (no name rebinding), resetting non-pinned assignments for those sources, and applying default labels.

**Files:**
- Create: `src/LocalScribe.Core/Diarisation/DiarisationCommit.cs`
- Create: `src/LocalScribe.Core/Diarisation/SpeakersMerge.cs`
- Test: `tests/LocalScribe.Core.Tests/SpeakersMergeTests.cs`

**Interfaces:**
- Consumes: `Speakers`, `SourceKind`.
- Produces:
  - `DiarisationCommit(IReadOnlyList<SourceKind> Sources, IReadOnlyDictionary<string, IReadOnlyDictionary<string,string>> Assignments, IReadOnlyDictionary<string,string> Names, string Method, DateTimeOffset DiarisedAtUtc)` - `Assignments` outer key is `"Local"`/`"Remote"`; inner `seq -> clusterKey`; `Names` is `clusterKey -> displayName`.
  - `static class SpeakersMerge { Speakers Merge(Speakers? existing, DiarisationCommit commit) }`
  - `static class DefaultSpeakerLabels { string For(SourceKind source, int clusterId) }` -> `"Local Speaker {clusterId+1}"` / `"Remote Speaker {clusterId+1}"`.

- [ ] **Step 1: Write the failing test**

`tests/LocalScribe.Core.Tests/SpeakersMergeTests.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;

public class SpeakersMergeTests
{
    private static DiarisationCommit Commit(
        IReadOnlyDictionary<string, string> remoteSeqs,
        IReadOnlyDictionary<string, string> names) =>
        new([SourceKind.Remote],
            new Dictionary<string, IReadOnlyDictionary<string, string>> { ["Remote"] = remoteSeqs },
            names, "sherpa", DateTimeOffset.UnixEpoch);

    [Fact]
    public void First_run_writes_assignments_names_sources_method()
    {
        var commit = Commit(
            new Dictionary<string, string> { ["3"] = "Remote:0", ["4"] = "Remote:1" },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2" });

        var merged = SpeakersMerge.Merge(null, commit);

        Assert.Equal("Remote:0", merged.Assignments["Remote"]["3"]);
        Assert.Equal("Remote Speaker 2", merged.Names["Remote:1"]);
        Assert.Contains(SourceKind.Remote, merged.DiarisedSources);
        Assert.Equal("sherpa", merged.Method);
        Assert.Equal(DateTimeOffset.UnixEpoch, merged.DiarisedAtUtc);
    }

    [Fact]
    public void Rediarise_preserves_pinned_assignment_and_its_name_verbatim()
    {
        var existing = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["3"] = "Remote:9", ["4"] = "Remote:0" } },
            Pinned = new Dictionary<string, List<string>> { ["Remote"] = ["3"] },  // seq 3 pinned
            Names = new Dictionary<string, string> { ["Remote:9"] = "Judge Wu", ["Remote:0"] = "Remote Speaker 1" },
            DiarisedSources = [SourceKind.Remote],
        };
        // New run reassigns seq 4 and produces different cluster ids; seq 3 must not move.
        var commit = Commit(
            new Dictionary<string, string> { ["4"] = "Remote:1" },
            new Dictionary<string, string> { ["Remote:1"] = "Remote Speaker 1" });

        var merged = SpeakersMerge.Merge(existing, commit);

        Assert.Equal("Remote:9", merged.Assignments["Remote"]["3"]);   // pinned kept
        Assert.Equal("Judge Wu", merged.Names["Remote:9"]);            // pinned name kept
        Assert.Equal("Remote:1", merged.Assignments["Remote"]["4"]);   // non-pinned reset to new run
    }

    [Fact]
    public void Rediarise_drops_non_pinned_names_so_stale_name_cannot_rebind()
    {
        var existing = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["4"] = "Remote:0" } },
            Names = new Dictionary<string, string> { ["Remote:0"] = "Alice" },   // NOT pinned
            DiarisedSources = [SourceKind.Remote],
        };
        var commit = Commit(
            new Dictionary<string, string> { ["4"] = "Remote:0" },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1" });

        var merged = SpeakersMerge.Merge(existing, commit);
        // The stale "Alice" must be gone; only the new run's default remains.
        Assert.Equal("Remote Speaker 1", merged.Names["Remote:0"]);
    }

    [Fact]
    public void Other_source_data_is_untouched()
    {
        var existing = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Local"] = new() { ["1"] = "Local:0" } },
            Names = new Dictionary<string, string> { ["Local:0"] = "Me-A" },
            DiarisedSources = [SourceKind.Local],
        };
        var commit = Commit(
            new Dictionary<string, string> { ["3"] = "Remote:0" },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1" });

        var merged = SpeakersMerge.Merge(existing, commit);
        Assert.Equal("Local:0", merged.Assignments["Local"]["1"]);   // Local untouched
        Assert.Equal("Me-A", merged.Names["Local:0"]);
        Assert.Contains(SourceKind.Local, merged.DiarisedSources);
        Assert.Contains(SourceKind.Remote, merged.DiarisedSources);
    }

    [Fact]
    public void DefaultSpeakerLabels_are_one_based_and_per_side()
    {
        Assert.Equal("Remote Speaker 1", DefaultSpeakerLabels.For(SourceKind.Remote, 0));
        Assert.Equal("Local Speaker 3", DefaultSpeakerLabels.For(SourceKind.Local, 2));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SpeakersMergeTests"`
Expected: FAIL - types missing.

- [ ] **Step 3: Implement the commit record + labels + merge**

`src/LocalScribe.Core/Diarisation/DiarisationCommit.cs`:

```csharp
using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Diarisation;

public sealed record DiarisationCommit(
    IReadOnlyList<SourceKind> Sources,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Assignments, // "Local"/"Remote" -> seq -> clusterKey
    IReadOnlyDictionary<string, string> Names,                                    // clusterKey -> displayName
    string Method,
    DateTimeOffset DiarisedAtUtc);

public static class DefaultSpeakerLabels
{
    public static string For(SourceKind source, int clusterId) =>
        $"{source} Speaker {clusterId + 1}";   // 1-based, per-side ("Local"/"Remote")
}
```

`src/LocalScribe.Core/Diarisation/SpeakersMerge.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.Diarisation;

public static class SpeakersMerge
{
    public static Speakers Merge(Speakers? existing, DiarisationCommit commit)
    {
        existing ??= new Speakers();
        var reSources = commit.Sources.Select(s => s.ToString()).ToHashSet(); // "Local"/"Remote"

        var assignments = existing.Assignments.ToDictionary(
            kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        var pinned = existing.Pinned.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));
        var names = new Dictionary<string, string>(existing.Names);

        // clusterKeys still referenced by any pinned seq (across all sources) must keep their names.
        var pinnedClusterKeys = new HashSet<string>();
        foreach (var (src, seqs) in pinned)
            if (assignments.TryGetValue(src, out var bySeq))
                foreach (var seq in seqs)
                    if (bySeq.TryGetValue(seq, out var ck)) pinnedClusterKeys.Add(ck);

        foreach (var sourceKey in reSources)
        {
            var pinnedSeqs = pinned.TryGetValue(sourceKey, out var ps) ? new HashSet<string>(ps) : [];

            // Reset non-pinned assignments for this source: keep only pinned seqs...
            var kept = new Dictionary<string, string>();
            if (assignments.TryGetValue(sourceKey, out var old))
                foreach (var (seq, ck) in old)
                    if (pinnedSeqs.Contains(seq)) kept[seq] = ck;
            // ...then apply the new run (skip any seq that is pinned).
            if (commit.Assignments.TryGetValue(sourceKey, out var fresh))
                foreach (var (seq, ck) in fresh)
                    if (!pinnedSeqs.Contains(seq)) kept[seq] = ck;
            assignments[sourceKey] = kept;

            // Drop non-pinned Names whose clusterKey belongs to this source.
            foreach (var ck in names.Keys.ToList())
                if (ck.StartsWith(sourceKey + ":", StringComparison.Ordinal) && !pinnedClusterKeys.Contains(ck))
                    names.Remove(ck);
        }

        // Apply the run's names (defaults or user-typed).
        foreach (var (ck, name) in commit.Names) names[ck] = name;

        var diarisedSources = existing.DiarisedSources
            .Concat(commit.Sources).Distinct().ToList();

        return existing with
        {
            Assignments = assignments,
            Pinned = pinned,
            Names = names,
            DiarisedSources = diarisedSources,
            Method = commit.Method,
            DiarisedAtUtc = commit.DiarisedAtUtc,
        };
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SpeakersMergeTests"`
Expected: PASS (all five).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Diarisation/DiarisationCommit.cs src/LocalScribe.Core/Diarisation/SpeakersMerge.cs tests/LocalScribe.Core.Tests/SpeakersMergeTests.cs
git commit -m "feat: [Stage5 T6] SpeakersMerge: pin-preserving, no-rebind speakers.json merge"
```

---

## Task 7: MaintenanceService.SaveDiarisationAsync - the single-gate write (App)

Adds the one write path: load speakers, merge, save, flip `session.Diarised`, regenerate projections - all under a single `RunForSessionAsync` gate, with the no-delete firewall. Tested against real stores in a temp session.

**Files:**
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs`
- Test: `tests/LocalScribe.App.Tests/MaintenanceServiceDiarisationTests.cs`

**Interfaces:**
- Consumes: `DiarisationCommit`, `SpeakersMerge` (Task 6); `SpeakersStore`, `SessionStore`, `SessionWriter`, `StoragePaths` (existing); the `RunForSessionAsync<T>` pattern from `SaveMetaAsync`/`SetArchivedAsync`.
- Produces: `Task SaveDiarisationAsync(string sessionId, DiarisationCommit commit, CancellationToken ct)` on `MaintenanceService`.

- [ ] **Step 1: Write the failing test**

`tests/LocalScribe.App.Tests/MaintenanceServiceDiarisationTests.cs`. It builds a finalized session on disk, writes a retained leg, runs `SaveDiarisationAsync`, and asserts speakers.json, the `Diarised` flag, projection re-render, pin preservation, and that the audio leg still exists (firewall). Use the repo's existing store constructors directly to lay down the session:

```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MaintenanceServiceDiarisationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_diar_{Guid.NewGuid():N}");

    private (MaintenanceService svc, StoragePaths paths, string id) MakeFinalizedSession()
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        // Finalized session with a retained Remote leg + two remote segments.
        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Remote],
        }, default).GetAwaiter().GetResult();
        new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { RemoteCount = 2 }, default).GetAwaiter().GetResult();
        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(3, TranscriptSource.Remote, 0, 1000, "hello", "Them"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(4, TranscriptSource.Remote, 1000, 2000, "world", "Them"), default).GetAwaiter().GetResult();
        // A retained leg file so the no-delete firewall has something to protect.
        File.WriteAllBytes(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac), [1, 2, 3]);

        var settings = new FakeSettingsService(new Settings());
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(), TimeProvider.System);
        return (svc, paths, id);
    }

    private static DiarisationCommit RemoteCommit() => new(
        [SourceKind.Remote],
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        { ["Remote"] = new Dictionary<string, string> { ["3"] = "Remote:0", ["4"] = "Remote:1" } },
        new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2" },
        "sherpa", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Writes_speakers_flips_diarised_regenerates_and_keeps_audio()
    {
        var (svc, paths, id) = MakeFinalizedSession();

        await svc.SaveDiarisationAsync(id, RemoteCommit(), default);

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.NotNull(speakers);
        Assert.Equal("Remote:0", speakers!.Assignments["Remote"]["3"]);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.True(session!.Diarised);

        // Projection re-rendered with the resolved names.
        string md = await File.ReadAllTextAsync(paths.TranscriptTxt(id));
        Assert.Contains("Remote Speaker 1", md);

        // Firewall: the retained leg is untouched.
        Assert.True(File.Exists(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac)));
    }

    [Fact]
    public async Task Rediarise_preserves_a_prior_pinned_assignment()
    {
        var (svc, paths, id) = MakeFinalizedSession();
        // Seed a pinned reassignment on seq 3 via the existing EditStore path.
        await new EditStore(paths.SessionDir(id), TimeProvider.System)
            .ReassignSpeakerAsync(3, TranscriptSource.Remote, "Remote:custom", default);

        await svc.SaveDiarisationAsync(id, RemoteCommit(), default);

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Remote:custom", speakers!.Assignments["Remote"]["3"]);   // pin survived
        Assert.Equal("Remote:1", speakers.Assignments["Remote"]["4"]);          // non-pin took the run
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
```

> If any seeding constructor name differs (`MetadataStore`, `TranscriptStore.AppendAsync`), reconcile against the repo - these are the existing stores; do not invent new ones.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceDiarisationTests"`
Expected: FAIL - `SaveDiarisationAsync` does not exist.

- [ ] **Step 3: Implement `SaveDiarisationAsync`**

Add to `src/LocalScribe.App/Services/MaintenanceService.cs`, mirroring `SetArchivedAsync` (read-current -> rewrite -> regen, under the gate). The primary-constructor fields are `paths`, `settings`, `time`:

```csharp
public Task SaveDiarisationAsync(string sessionId, DiarisationCommit commit, CancellationToken ct) =>
    RunForSessionAsync(sessionId, async inner =>
    {
        if (!File.Exists(paths.SessionJson(sessionId))) return true;   // deleted mid-run guard

        // 1) merge into speakers.json (pin-preserving) and save FIRST (source of truth).
        var store = new SpeakersStore(paths.SpeakersJson(sessionId));
        var existing = await store.LoadAsync(inner);
        var merged = SpeakersMerge.Merge(existing, commit);
        await store.SaveAsync(merged, inner);

        // 2) flip session.Diarised (mirror the RecoverIfNeededAsync rewrite pattern).
        var sessionStore = new SessionStore(paths.SessionJson(sessionId));
        var session = await sessionStore.ReadAsync(inner);
        if (session is not null && !session.Diarised)
            await sessionStore.SaveAsync(session with { Diarised = true }, inner);

        // 3) re-render projections with the new speaker names.
        // NOTE: NO audio deletion here for any AudioRetention value (evidentiary firewall).
        await new SessionWriter(paths, settings.Current, time).RegenerateProjectionsAsync(sessionId, inner);
        return true;
    }, ct);
```

Add `using LocalScribe.Core.Diarisation;` at the top of the file if not already implied by ImplicitUsings.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceDiarisationTests"`
Expected: PASS (both).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceDiarisationTests.cs
git commit -m "feat: [Stage5 T7] MaintenanceService.SaveDiarisationAsync single-gate write + firewall"
```

---

## Task 8: SplitSpeakersViewModel - source gating, run, soft-prior, naming (App)

The WPF-free dialog VM: computes splittable sources, runs the engine per source with progress/cancel, applies the soft-prior force-N logic, exposes clusters for naming (with default labels + a play hook), and confirms via `SaveDiarisationAsync`.

**Files:**
- Create: `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/SplitSpeakersViewModelTests.cs`

**Interfaces:**
- Consumes: `IDiarisationEngine`, `DiarisationRequest`, `DiarisationResult`, `DiarisationException` (Tasks 4); `ClusterAssigner` (Task 5); `DiarisationCommit`, `DefaultSpeakerLabels` (Task 6); `MaintenanceService.SaveDiarisationAsync`, `MaintenanceService.ListSessionsAsync` (Task 7); `SessionListItem`, `StoragePaths`, `ISettingsService`, `IUiErrorReporter`; `dispatch: Action<Action>`, `TimeProvider`.
- Produces: `SplitSpeakersViewModel` with `Task LoadAsync(string sessionId, CancellationToken ct)`, `ObservableCollection<SplitSourceOption> Sources`, `RunCommand`, `CancelCommand`, `ForceCountCommand`, `ConfirmCommand`, `ObservableCollection<ClusterRowViewModel> Clusters`, `bool SystemMixWarning`, `double Progress`, and a `Func<SourceKind,long,Task>` `PlaySnippet` hook the window wires to the audio player.

- [ ] **Step 1: Write the failing test**

`tests/LocalScribe.App.Tests/SplitSpeakersViewModelTests.cs`. Reuses the finalized-session helper shape from Task 7 (extract it to a shared local helper, or duplicate the minimal setup). Uses a fake `IDiarisationEngine`:

```csharp
using System.Collections.ObjectModel;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SplitSpeakersViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_svm_{Guid.NewGuid():N}");

    private sealed class FakeEngine : IDiarisationEngine
    {
        public int? LastForced { get; private set; }
        public DiarisationResult Next { get; set; } =
            new([new DiarisedSegment(0, 2000, 0)], 1, "fake");
        public Task<DiarisationResult> DiariseAsync(DiarisationRequest r, IProgress<double> p, CancellationToken ct)
        {
            LastForced = r.ForcedClusterCount;
            p.Report(1.0);
            return Task.FromResult(Next);
        }
    }

    // ... MakeFinalizedSession(remoteCount, retained, systemMix) as in Task 7,
    // returning (MaintenanceService svc, StoragePaths paths, string id) plus a FakeEngine.

    [Fact]
    public async Task Only_offers_sources_with_count_gt_1_and_a_retained_leg()
    {
        // RemoteCount=2 retained; LocalCount=1 -> only Remote splittable.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);

        Assert.Single(vm.Sources);
        Assert.Equal(SourceKind.Remote, vm.Sources[0].Source);
    }

    [Fact]
    public async Task Run_auto_then_forceN_passes_declared_count()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 3, retained: [SourceKind.Remote]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;

        engine.Next = new DiarisationResult(new[] { new DiarisedSegment(0, 1000, 0) }, 1, "fake"); // auto found 1
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Null(engine.LastForced);                 // first pass is auto
        Assert.True(vm.CountMismatch);                  // 1 != declared 3

        await vm.ForceCountCommand.ExecuteAsync(null);  // "Use 3 speakers"
        Assert.Equal(3, engine.LastForced);
    }

    [Fact]
    public async Task Confirm_writes_diarisation_with_default_labels()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(new[]
        {
            new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)
        }, 2, "fake");
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Clusters.Count);
        Assert.Equal("Remote Speaker 1", vm.Clusters[0].Name);   // default label

        await vm.ConfirmCommand.ExecuteAsync(null);

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.True(speakers!.DiarisedSources.Contains(SourceKind.Remote));
    }

    [Fact]
    public async Task ForceN_suppressed_and_banner_shown_for_system_mix_leg()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 3, retained: [SourceKind.Remote], systemMix: true);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        Assert.True(vm.SystemMixWarning);

        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(new[] { new DiarisedSegment(0, 1000, 0) }, 1, "fake");
        await vm.RunCommand.ExecuteAsync(null);
        Assert.False(vm.CanForceCount);   // force-N disabled for a system-mix leg
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakersViewModelTests"`
Expected: FAIL - VM missing.

- [ ] **Step 3: Implement the VM**

Create `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs`. Use `CommunityToolkit.Mvvm` (`ObservableObject`, `[ObservableProperty]`, `AsyncRelayCommand`) as the other VMs do. Key logic (encode all of it - no placeholders): `LoadAsync` reads the session+meta from `ListSessionsAsync` (or the stores) and builds `Sources` where `RemoteCount>1 && RetainedAudioSources.Contains(Remote)` (and same for Local); sets `SystemMixWarning` from `Devices.Remote.Mode == RemoteMode.SystemMix || Devices.Remote.FellBackToSystemMix`. `RunCommand` iterates selected sources, resolves the leg path via the same probe as `PlaybackViewModel.Resolve` (retained + on-disk format), calls `_engine.DiariseAsync(new DiarisationRequest(legPath, source, segPath, embPath, forced: null), progress, _cts.Token)`, runs `ClusterAssigner.Assign`, materialises `Clusters` with `DefaultSpeakerLabels.For(...)`, sets `CountMismatch = result.ClusterCount != declaredCount`, and `CanForceCount = CountMismatch && !SystemMixWarning`. `ForceCountCommand` re-runs with `ForcedClusterCount: declaredCount`. `CancelCommand` cancels `_cts` (the engine throws `OperationCanceledException` -> helper killed). `ConfirmCommand` builds a `DiarisationCommit` (per-source `Assignments` from the assigner, `Names` from the `Clusters` - user name or default, `Sources`, `Method`, `DiarisedAtUtc = _time.GetUtcNow()`) and calls `_maintenance.SaveDiarisationAsync`. Wrap engine/`DiarisationException` failures via `_reporter.Report("Split speakers", ex)` mapping `ModelDownloadFailed` to the "run fetch-models.ps1 / manual path" message. Model paths come via a ctor param `Func<string,string> resolveModel` (production passes `ModelPaths.Resolve`; tests pass identity) so the VM has no `ModelPaths` static dependency. Expose `Func<SourceKind,long,Task>? PlaySnippet` for the window to wire to audio.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakersViewModelTests"`
Expected: PASS (all four).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs tests/LocalScribe.App.Tests/SplitSpeakersViewModelTests.cs
git commit -m "feat: [Stage5 T8] SplitSpeakersViewModel: gating, run, soft-prior, naming, confirm"
```

---

## Task 9: SplitSpeakersWindow + ProcessDiarisationHelper + wiring (App)

The dialog window (capture-excluded), the production process launcher, the entry points, and the DI wiring. This task has no new unit test (WPF + process spawn are the untested humble edges, matching `MediaPlayerDualAudioPlayer`); its gate is a manual run.

**Files:**
- Create: `src/LocalScribe.App/SplitSpeakersWindow.xaml` / `.xaml.cs`
- Create: `src/LocalScribe.App/Services/ProcessDiarisationHelper.cs`
- Modify: `src/LocalScribe.App/CompositionRoot.cs`, `src/LocalScribe.App/App.xaml.cs`
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml`(`.cs`), `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`
- Modify: `src/LocalScribe.App/Pages/SessionsPage.xaml`, `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs`

**Interfaces:**
- Consumes: `SplitSpeakersViewModel` (Task 8), `IDiarisationHelper` (Task 4), `DiarisationJob`/`DiarisationJson` (Task 2), `AppComposition`.
- Produces: `ProcessDiarisationHelper : IDiarisationHelper`; a Split-speakers launch path from both the read view and the sessions context menu; `WDA_EXCLUDEFROMCAPTURE` applied to the dialog.

- [ ] **Step 1: Implement `ProcessDiarisationHelper` (spawn + kill-on-cancel)**

`src/LocalScribe.App/Services/ProcessDiarisationHelper.cs`. Spawn `LocalScribe.Diarizer.exe` (resolved beside the app base dir), write the job JSON to stdin, forward each stdout line to `onStdoutLine`, and on cancellation `Kill(entireProcessTree: true)`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using LocalScribe.Core.Diarisation;

namespace LocalScribe.App.Services;

public sealed class ProcessDiarisationHelper(string exePath) : IDiarisationHelper
{
    public async Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start diarizer");
        await using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });

        await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(job, DiarisationJson.Options));
        proc.StandardInput.Close();

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            onStdoutLine(line);

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
}
```

- [ ] **Step 2: Create the dialog window (capture-excluded)**

`src/LocalScribe.App/SplitSpeakersWindow.xaml` - a `ui:FluentWindow` bound to `SplitSpeakersViewModel`: a source checklist (`Sources`), a Run button + progress bar + Cancel, a count-mismatch panel with a "Use N speakers" button (visible on `CountMismatch`, enabled on `CanForceCount`), a system-mix banner (visible on `SystemMixWarning`), and a `Clusters` list where each row shows the default/edited name (editable `TextBox`), a few preview utterances, and a Play button. Code-behind applies `WDA_EXCLUDEFROMCAPTURE` in the `Loaded` handler exactly as the other transcript-bearing windows do (reuse the existing capture-exclusion helper the app already uses for `ReadViewWindow`/`LiveViewWindow` - find it via grep for `WDA_EXCLUDEFROMCAPTURE` / `SetWindowDisplayAffinity`), and wires `vm.PlaySnippet` to the read view's audio player (or a fresh `MediaPlayerDualAudioPlayer` seeking `SeekMs(startMs)`).

- [ ] **Step 3: Wire the engine into composition**

In `CompositionRoot.Build()`: construct the engine and add it to `AppComposition`. Resolve the helper exe path beside the app; construct `new SherpaHelperDiariser(new ProcessDiarisationHelper(diarizerExePath))`:

```csharp
// after `var maintenance = ...`
string diarizerExe = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Diarizer.exe");
IDiarisationEngine diarisation = new SherpaHelperDiariser(new ProcessDiarisationHelper(diarizerExe));
```

Add `IDiarisationEngine Diarisation` to the `AppComposition` record and pass `diarisation` in. In `App.xaml.cs`, widen the read-view factory to construct the `SplitSpeakersWindow` on demand, injecting `comp.Diarisation`, `comp.Maintenance`, `comp.Paths`, `comp.Settings`, `errors`, `dispatch`, `TimeProvider.System`, and `ModelPaths.Resolve` for the two model filenames from Task 1.

- [ ] **Step 4: Add the entry points**

Add a "Split speakers..." button to `ReadViewWindow.xaml`'s header StackPanel (the badge row L17-28), gated visible/enabled by a new `ReadViewViewModel` flag `CanDiarise` (finalized + at least one splittable source), and a code-behind `Click` handler that opens the dialog for `SessionId`. Add a `<MenuItem Header="Split speakers..." Command="{Binding DiariseCommand}" CommandParameter="{Binding SelectedRow}" />` to `SessionsPage.xaml`'s context menu (L54-66), backed by a new `IAsyncRelayCommand<SessionRowViewModel> DiariseCommand` on `SessionsPageViewModel` that raises an event the App layer handles to open the dialog (mirror `OpenReadViewRequested`). Gate the command off for `IsPendingRecovery` rows. The `Diarised` badge is already bound (`SessionRowViewModel.IsDiarised`); it lights automatically once `SaveDiarisationAsync` flips the flag and the list refreshes.

- [ ] **Step 5: Build, run the app, diarise a real session end-to-end**

Run: `dotnet build LocalScribe.slnx` then `dotnet run --project src/LocalScribe.App`
Manually: open a finalized multi-remote-speaker session, click Split speakers, run, name clusters (play a snippet), confirm; verify the read view re-renders with names, the Diarised badge appears, and the audio legs still exist on disk.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/ tests/
git commit -m "feat: [Stage5 T9] Split-speakers dialog, process helper, entry points, DI wiring"
```

---

## Task 10: DER fixture harness + docs + specs amendment

The opt-in real-model regression harness, plus README/specs updates. The corpus recording is a user action (an open item), so the harness ships with a clear skip-when-absent path like `GoldenCorpusFixtureTests`.

**Files:**
- Create: `tests/LocalScribe.Core.Tests/DiarisationFixtureTests.cs`
- Create: `docs/plans/2026-07-04-stage-5-smoke-runbook.md`
- Modify: `README.md`, `docs/specs/localscribe-specs.md`

**Interfaces:**
- Consumes: `SherpaHelperDiariser` + `ProcessDiarisationHelper` (or a direct helper invocation), the real models, and a recorded multi-speaker fixture leg.

- [ ] **Step 1: Write the fixture harness (skips cleanly when assets absent)**

`tests/LocalScribe.Core.Tests/DiarisationFixtureTests.cs` with class-level `[Trait("Category", "Fixture")]`. It resolves a fixture leg + reference labels under `models/diar-fixture/`, runs the real helper, computes DER against a stored `baseline.json` with an `Epsilon`, and `throw new FileNotFoundException(...)` when the fixture is absent (so the gate keeps it out of CI), mirroring `GoldenCorpusFixtureTests` exactly:

```csharp
using LocalScribe.Core.Transcription;

[Trait("Category", "Fixture")]
public class DiarisationFixtureTests
{
    [Fact]
    public async Task Der_within_baseline_plus_epsilon()
    {
        string legPath = ModelPaths.Resolve(Path.Combine("diar-fixture", "remote.flac"));
        if (!File.Exists(legPath))
            throw new FileNotFoundException(
                "Diarisation fixture missing. Copy a real multi-remote-speaker leg + labels into models/diar-fixture/ (privileged, never committed).", legPath);
        // ... run real helper, compute DER vs models/diar-fixture/reference.rttm, assert <= baseline + Epsilon.
    }
}
```

- [ ] **Step 2: Run the model-free gate to confirm the fixture stays opt-in**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: PASS; the diarisation fixture is NOT executed (no FileNotFoundException surfaces in the gate).

- [ ] **Step 3: Update README + specs + write the smoke runbook**

In `README.md`, tick roadmap item 5 as delivered and add the fetch/run notes for diarisation. In `docs/specs/localscribe-specs.md`, amend section 1.3/1.4 to record the delivered behaviour (per-side default labels `Local/Remote Speaker N`, no name rebinding on re-diarise, `MODEL_DOWNLOAD_FAILED` for a missing model, the no-delete firewall, the `IDiarisationEngine` `DiarisationRequest`/`DiarisationResult` shape superseding the old `DiariseAsync(segments, options)` sketch). Write `docs/plans/2026-07-04-stage-5-smoke-runbook.md` covering the GUI-only checks: D1 split a real multi-remote-speaker Webex call, D2 cancel mid-run leaves the session unchanged, D3 name-by-snippet playback, D4 re-diarise preserves a pinned reassignment, D5 system-mix banner + force-N suppressed, D6 missing-model path shows the fetch hint, D7 win-arm64 run.

- [ ] **Step 4: Full build + gate green**

Run: `dotnet build LocalScribe.slnx` then `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: build zero warnings; all non-fixture tests green.

- [ ] **Step 5: Commit**

```bash
git add tests/LocalScribe.Core.Tests/DiarisationFixtureTests.cs docs/plans/2026-07-04-stage-5-smoke-runbook.md README.md docs/specs/localscribe-specs.md
git commit -m "feat: [Stage5 T10] DER fixture harness + README/specs/runbook"
```

---

## Self-Review Notes (for the plan author)

Coverage against the design's Section 8 build sequence: Task 0 = spike; Task 1 = fetch-models; Task 2 = wire + FLAC decode; Tasks 4 = engine seam; Task 5 = cluster mapping; Task 6 = speakers merge; Task 7 = single-gate write + firewall; Task 8 = dialog VM (source gating, run, soft-prior, naming); Task 9 = dialog window + process helper + entry points + `Diarised` badge; Task 10 = DER fixture + docs. Task 3 delivers the helper exe (design build-step 2). Projection regeneration (design build-step 5) is folded into Task 7's `RegenerateProjectionsAsync` call and asserted by its projection test.

All six design must-fix invariants are encoded as tests: no name rebinding (Task 6 test 3), per-side 1-based labels (Task 6 test 5), pin preservation (Tasks 6+7), no-delete firewall (Task 7 test 1), uncovered-seq unassigned (Task 5 test 2), `MODEL_MISSING`->`MODEL_DOWNLOAD_FAILED` (Task 4 test 2), system-mix force-N suppression + banner (Task 8 test 4), declared-count-and-retained-leg gating (Task 8 test 1).
