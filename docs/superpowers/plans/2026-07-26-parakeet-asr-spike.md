# Parakeet ASR Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the AsrBench measurement harness and run the capability-led evaluation that decides whether Parakeet TDT v3 (via sherpa-onnx) joins whisper.cpp as a second engine family — per approved spec `docs/superpowers/specs/2026-07-26-parakeet-asr-spike-design.md`. Non-adoption is a first-class outcome; this spike ships **no production changes**.

**Architecture:** Two new console projects on a spike branch. `src/LocalScribe.AsrBench` (references Core) does VAD segmentation, drives the whisper lane in-process through the production `IEngineFactory` seam, spawns the Parakeet lane, scores WER, and runs the config matrix. `src/LocalScribe.ParakeetLane` (references sherpa-onnx ONLY — **never Core**) is a persistent stdio child hosting the sherpa `OfflineRecognizer`. This split is mandatory: Core pins ORT 1.22.0 (Silero VAD) and sherpa bundles its own ORT 1.24.4 — one output dir holds one `onnxruntime.dll`, so they must never share a process or an output directory. The child reports in-engine decode time; the parent records round-trip time; the difference is the process-boundary overhead number the adoption hosting decision needs.

**Tech Stack:** .NET 10 (`net10.0-windows`), xunit 2.9.3, Whisper.net 1.9.1 (+ `Whisper.net.Runtime` CPU natives), `org.k2fsa.sherpa.onnx` 1.13.3, NAudio 2.2.1, PowerShell fetch/prep scripts, ffmpeg (already fetched via `tools/fetch-ffmpeg.ps1` into `tools/ffmpeg/`).

## Global Constraints

- **ORT isolation (hard):** Core stays on `Microsoft.ML.OnnxRuntime` 1.22.0. `LocalScribe.ParakeetLane` must never reference `LocalScribe.Core` or any ORT package besides what sherpa bundles. `LocalScribe.AsrBench` must never reference `org.k2fsa.sherpa.onnx`.
- **No production changes:** nothing under `src/LocalScribe.Core`, `src/LocalScribe.App`, or any shipped project changes in this spike (the two new projects and `tools/` scripts only). Stop semantics, floor-fall, live path untouched.
- **Privileged audio:** `bench-corpus/` is gitignored; real-call audio, transcripts, and Tier-1 results never enter a commit. The committed report quotes aggregates and public-audio examples only.
- **No Unicode emojis in test scripts** (user rule). The BPE word-boundary character `▁` in the token-merge code is a functional data constant, not an emoji — write it as the escape `'▁'`, never as a literal glyph.
- **Package versions verbatim:** sherpa-onnx `1.13.3`, Whisper.net `1.9.1`, NAudio `2.2.1`, xunit `2.9.3`, Microsoft.NET.Test.Sdk `17.14.1`, xunit.runner.visualstudio `3.1.4`.
- **sherpa C# API caveat (Diarizer precedent):** the Diarizer's own Task-0 spike found the planned sherpa API sketch had compile errors that empirical testing corrected (`SherpaDiarisationRunner.cs` header comment). The sherpa code in Task 2/6 is the best-known sketch; **Task 2 empirically confirms it and its findings supersede this plan's sketch.** Fix-forward in Task 2, do not silently drift later tasks.
- **Model names:** whisper CPU-floor model is `small.en` (worst→best ladder `tiny.en, base.en, small.en` in `BackendSelector`); `ModelFileResolver` picks the q8_0 file on CPU automatically. Parakeet canonical dir name: `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`.
- Repo conventions: console runners live in `src/` (`OfflineRunner`, `SpikeRunner` precedent), fetch scripts in `tools/`, tests in `tests/<Project>.Tests` with xunit. Branch: `spike/parakeet-asr-bench` off master, merged `--no-ff` at the end regardless of verdict (spec stop-path).

---

### Task 1: Spike branch, gitignore, AsrBench + tests scaffolds, slnx wiring

**Files:**
- Modify: `.gitignore` (append at end, after the `tools/ffmpeg/` block)
- Create: `src/LocalScribe.AsrBench/LocalScribe.AsrBench.csproj`
- Create: `src/LocalScribe.AsrBench/Program.cs`
- Create: `tests/LocalScribe.AsrBench.Tests/LocalScribe.AsrBench.Tests.csproj`
- Create: `tests/LocalScribe.AsrBench.Tests/SmokeTest.cs`
- Modify: `LocalScribe.slnx`

**Interfaces:**
- Produces: `LocalScribe.AsrBench` exe with subcommand routing (`segment` | `run` | `score` | `matrix`), each dispatching to a static `RunAsync(string[] args)` on a per-command class added in later tasks. Tests project referencing AsrBench.

- [ ] **Step 1: Create the branch**

```powershell
git checkout -b spike/parakeet-asr-bench
```

- [ ] **Step 2: Append to `.gitignore`**

```gitignore

# ASR spike corpus + results (real-call audio/transcripts are privileged; never committed)
bench-corpus/
```

- [ ] **Step 3: Create `src/LocalScribe.AsrBench/LocalScribe.AsrBench.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\LocalScribe.Core\LocalScribe.Core.csproj" />
    <!-- CPU whisper natives for the bench host (Core.Tests precedent). CPU-only runtime:
         the bench must never accidentally measure CUDA. -->
    <PackageReference Include="Whisper.net.Runtime" Version="1.9.1" />
  </ItemGroup>

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

</Project>
```

- [ ] **Step 4: Create `src/LocalScribe.AsrBench/Program.cs`**

```csharp
// src/LocalScribe.AsrBench/Program.cs
// ASR spike bench harness (spec docs/superpowers/specs/2026-07-26-parakeet-asr-spike-design.md).
// Subcommands: segment | run | score | matrix. Never shipped; never referenced by App/Core.
using LocalScribe.AsrBench;

return args.FirstOrDefault() switch
{
    "segment" => await SegmentCommand.RunAsync(args.Skip(1).ToArray()),
    "run" => await RunCommand.RunAsync(args.Skip(1).ToArray()),
    "score" => ScoreCommand.Run(args.Skip(1).ToArray()),
    "matrix" => await MatrixCommand.RunAsync(args.Skip(1).ToArray()),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: LocalScribe.AsrBench <segment|run|score|matrix> [options]");
    return 2;
}
```

This will not compile until the command classes exist. For this task, create four one-line stubs so the scaffold builds; later tasks replace each stub with the real implementation:

Create `src/LocalScribe.AsrBench/Stubs.cs`:

```csharp
// src/LocalScribe.AsrBench/Stubs.cs -- placeholder command entry points; each is replaced
// by its own task (segment: T3, run: T5/T7, score: T8, matrix: T9). Delete this file when
// the last stub is replaced.
namespace LocalScribe.AsrBench;

internal static class SegmentCommand { public static Task<int> RunAsync(string[] a) => Task.FromResult(Fail()); private static int Fail() { Console.Error.WriteLine("segment: not implemented yet"); return 2; } }
internal static class RunCommand { public static Task<int> RunAsync(string[] a) => Task.FromResult(Fail()); private static int Fail() { Console.Error.WriteLine("run: not implemented yet"); return 2; } }
internal static class ScoreCommand { public static int Run(string[] a) { Console.Error.WriteLine("score: not implemented yet"); return 2; } }
internal static class MatrixCommand { public static Task<int> RunAsync(string[] a) => Task.FromResult(Fail()); private static int Fail() { Console.Error.WriteLine("matrix: not implemented yet"); return 2; } }
```

- [ ] **Step 5: Create `tests/LocalScribe.AsrBench.Tests/LocalScribe.AsrBench.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\LocalScribe.AsrBench\LocalScribe.AsrBench.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 6: Create `tests/LocalScribe.AsrBench.Tests/SmokeTest.cs`**

```csharp
namespace LocalScribe.AsrBench.Tests;

public class SmokeTest
{
    [Fact]
    public void Scaffold_builds() => Assert.True(true);
}
```

- [ ] **Step 7: Wire both projects into `LocalScribe.slnx`**

Add inside `<Folder Name="/src/">`:

```xml
    <Project Path="src/LocalScribe.AsrBench/LocalScribe.AsrBench.csproj" />
```

Add inside `<Folder Name="/tests/">`:

```xml
    <Project Path="tests/LocalScribe.AsrBench.Tests/LocalScribe.AsrBench.Tests.csproj" />
```

(The ParakeetLane project is added to slnx in Task 2 alongside its creation.)

- [ ] **Step 8: Build + run tests**

Run: `dotnet build LocalScribe.slnx && dotnet test tests/LocalScribe.AsrBench.Tests`
Expected: build succeeds; 1 test passes.

- [ ] **Step 9: Commit**

```powershell
git add .gitignore LocalScribe.slnx src/LocalScribe.AsrBench tests/LocalScribe.AsrBench.Tests
git commit -m "chore(spike): AsrBench scaffold + tests project on spike branch"
```

---

### Task 2: fetch-parakeet.ps1 + ParakeetLane project + Phase-0 feasibility gate

**This task IS the spec's Phase-0 gate.** If sherpa 1.13.3 cannot load Parakeet TDT v3 and produce sane English text on the model's own `test_wavs/`, STOP: try the newest sherpa-onnx package version once; if still structurally broken, run the spec's Python `onnx-asr` attribution check (`pip install onnx-asr`, transcribe the same wav) solely to determine whether the blocker is sherpa's or the model's, write the finding into a short `docs/spikes/2026-07-26-parakeet-phase0-notes.md`, and end the spike per spec. No later task runs.

**Files:**
- Create: `tools/fetch-parakeet.ps1`
- Create: `src/LocalScribe.ParakeetLane/LocalScribe.ParakeetLane.csproj`
- Create: `src/LocalScribe.ParakeetLane/Program.cs`
- Create: `src/LocalScribe.ParakeetLane/WavReader.cs`
- Modify: `LocalScribe.slnx` (add ParakeetLane to `/src/`)

**Interfaces:**
- Produces: `tools/fetch-parakeet.ps1` → `models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/{encoder.int8.onnx,decoder.int8.onnx,joiner.int8.onnx,tokens.txt,test_wavs/}`.
- Produces: `LocalScribe.ParakeetLane --check <modelDir> <wav>` mode printing text + token timestamps (the gate). The persistent stdio protocol is Task 6; `--check` stays as a debug mode.
- Produces: `WavReader.ReadMono16k(string path) -> float[]` (throws `InvalidDataException` if not 16 kHz mono PCM WAV).

- [ ] **Step 1: Create `tools/fetch-parakeet.ps1`**

```powershell
# tools/fetch-parakeet.ps1
# Downloads the Parakeet TDT 0.6b v3 int8 ONNX model (sherpa-onnx release asset) into
# <repo>/models (gitignored). Spike tooling (spec 2026-07-26); adoption would promote
# this into the production fetch/verify pair.
param(
    # Skip the SHA-256 check (first-fetch bootstrap only; see step 3 below).
    [switch] $NoVerify
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root 'models'
New-Item -ItemType Directory -Force $models | Out-Null

$name = 'sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8'
$uri = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/$name.tar.bz2"
# SHA-256 of the release tarball, pinned on first verified fetch (step 3 records it).
$sha = 'PINNED-IN-STEP-3'

$dest = Join-Path $models $name
if (Test-Path (Join-Path $dest 'encoder.int8.onnx')) {
    Write-Host "already present: $dest"
    exit 0
}

$tar = Join-Path $models "$name.tar.bz2"
Write-Host "fetching $uri"
Invoke-WebRequest -Uri $uri -OutFile $tar

if (-not $NoVerify) {
    $actual = (Get-FileHash -Algorithm SHA256 $tar).Hash.ToLowerInvariant()
    if ($actual -ne $sha) { throw "SHA-256 mismatch for ${name}: expected $sha got $actual" }
}

# Windows 11 bsdtar handles .tar.bz2 natively.
tar -xjf $tar -C $models
if ($LASTEXITCODE -ne 0) { throw "tar extraction failed ($LASTEXITCODE)" }
Remove-Item $tar
foreach ($f in 'encoder.int8.onnx','decoder.int8.onnx','joiner.int8.onnx','tokens.txt') {
    if (-not (Test-Path (Join-Path $dest $f))) { throw "expected file missing after extract: $f" }
}
Write-Host "ready: $dest"
```

- [ ] **Step 2: Run the fetch (bootstrap, unverified)**

Run: `powershell -File tools/fetch-parakeet.ps1 -NoVerify`
Expected: `ready: F:\LocalScribe\models\sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` and the four files present plus `test_wavs\`. (~650 MB download; the archive also ships `test_wavs/` with reference speech.)

- [ ] **Step 3: Pin the SHA**

Run: `powershell -Command "(Get-FileHash -Algorithm SHA256 (Get-Item 'models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2' -ErrorAction SilentlyContinue) ).Hash"` — if the tarball was already deleted by step 2, re-download it with `Invoke-WebRequest` to a temp path and hash that.
Then edit `tools/fetch-parakeet.ps1` replacing `PINNED-IN-STEP-3` with the actual lowercase hash. Sanity: delete nothing; the script's `already present` early-exit means the pinned path is only exercised on fresh fetches.

- [ ] **Step 4: Create `src/LocalScribe.ParakeetLane/LocalScribe.ParakeetLane.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- HARD RULE: no LocalScribe.Core reference, ever. Core pins ORT 1.22.0 (Silero VAD);
       sherpa bundles its own onnxruntime (1.24.4). One output dir = one onnxruntime.dll,
       so this exe is the ONLY place sherpa's ORT lives, exactly like the Diarizer. -->
  <ItemGroup>
    <!-- Apache-2.0. Same package+version the Diarizer pins. -->
    <PackageReference Include="org.k2fsa.sherpa.onnx" Version="1.13.3" />
    <PackageReference Include="NAudio" Version="2.2.1" />
  </ItemGroup>

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

</Project>
```

- [ ] **Step 5: Create `src/LocalScribe.ParakeetLane/WavReader.cs`**

```csharp
// src/LocalScribe.ParakeetLane/WavReader.cs
// Minimal 16 kHz mono WAV reader. The lane deliberately does NOT resample or downmix:
// corpus prep (prep-librispeech.ps1 / tier-1 runbook) guarantees 16k mono inputs, and a
// silent mismatch here would corrupt every latency and WER number downstream.
using NAudio.Wave;

namespace LocalScribe.ParakeetLane;

internal static class WavReader
{
    public static float[] ReadMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1)
            throw new InvalidDataException(
                $"{path}: expected 16 kHz mono, got {reader.WaveFormat.SampleRate} Hz " +
                $"x{reader.WaveFormat.Channels}ch. Re-run corpus prep.");
        var all = new List<float>((int)(reader.Length / 4));
        var buf = new float[16000];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            all.AddRange(buf.AsSpan(0, n).ToArray());
        return all.ToArray();
    }
}
```

- [ ] **Step 6: Create `src/LocalScribe.ParakeetLane/Program.cs` (--check mode only for this task)**

Best-known sherpa C# API sketch — **empirically confirm in step 8 and fix here if it differs** (Diarizer Task-0 precedent; property/method names are the likely drift points, e.g. result access):

```csharp
// src/LocalScribe.ParakeetLane/Program.cs
// Parakeet TDT v3 lane. This task: --check <modelDir> <wav> debug mode (Phase-0 gate).
// Task 6 adds the persistent stdio protocol used by AsrBench `run --engine parakeet`.
using System.Diagnostics;
using SherpaOnnx;
using LocalScribe.ParakeetLane;

if (args.Length == 3 && args[0] == "--check")
{
    string modelDir = args[1], wav = args[2];
    var config = new OfflineRecognizerConfig();
    config.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
    config.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, "decoder.int8.onnx");
    config.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, "joiner.int8.onnx");
    config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
    config.ModelConfig.ModelType = "nemo_transducer";
    config.ModelConfig.NumThreads = 4;

    var loadSw = Stopwatch.StartNew();
    using var recognizer = new OfflineRecognizer(config);
    Console.WriteLine($"model loaded in {loadSw.ElapsedMilliseconds} ms");

    float[] samples = WavReader.ReadMono16k(wav);
    var decodeSw = Stopwatch.StartNew();
    using var stream = recognizer.CreateStream();
    stream.AcceptWaveform(16000, samples);
    recognizer.Decode(stream);
    var result = stream.Result;
    decodeSw.Stop();

    Console.WriteLine($"decode: {decodeSw.ElapsedMilliseconds} ms for {samples.Length / 16000.0:F1} s audio");
    Console.WriteLine($"text:   {result.Text}");
    Console.WriteLine($"tokens: {result.Tokens.Length}, timestamps: {result.Timestamps.Length}");
    for (int i = 0; i < Math.Min(10, result.Tokens.Length); i++)
        Console.WriteLine($"  {result.Timestamps[i]:F2}s  {result.Tokens[i]}");
    return 0;
}

Console.Error.WriteLine("usage: LocalScribe.ParakeetLane --check <modelDir> <wav>");
return 2;
```

- [ ] **Step 7: Add to `LocalScribe.slnx`** inside `<Folder Name="/src/">`:

```xml
    <Project Path="src/LocalScribe.ParakeetLane/LocalScribe.ParakeetLane.csproj" />
```

- [ ] **Step 8: THE GATE — build and run the check**

The model's `test_wavs` are not guaranteed 16 kHz mono; convert one first:

```powershell
tools/ffmpeg/ffmpeg.exe -i models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/test_wavs/en.wav -ar 16000 -ac 1 -acodec pcm_s16le bench-check.wav
dotnet run --project src/LocalScribe.ParakeetLane -- --check models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8 bench-check.wav
```

(If `test_wavs` has different file names, use any English wav in it — list the dir first.)
Expected: model loads; decode completes; `text:` is recognisably correct English for the sample; tokens and timestamps are non-empty with monotonically increasing times. If compile errors on the sherpa API: fix Program.cs to the actual API (this supersedes the sketch), note the corrections in the commit message. If it fails structurally (bad output, crash, unsupported model): follow the STOP procedure in this task's header.
Cleanup: `Remove-Item bench-check.wav`

- [ ] **Step 9: Commit**

```powershell
git add tools/fetch-parakeet.ps1 src/LocalScribe.ParakeetLane LocalScribe.slnx
git commit -m "feat(spike): ParakeetLane + fetch script; Phase-0 gate PASSED (sherpa 1.13.3 runs parakeet-tdt-v3 int8)"
```

---

### Task 3: `segment` subcommand — VAD to segments.jsonl

**Files:**
- Create: `src/LocalScribe.AsrBench/SegmentCommand.cs`
- Modify: `src/LocalScribe.AsrBench/Stubs.cs` (remove the SegmentCommand stub)
- Test: `tests/LocalScribe.AsrBench.Tests/SegmentRowTest.cs`

**Interfaces:**
- Consumes (Core): `WavFileFrameReader.ReadFrames(string, SourceKind) -> IEnumerable<AudioFrame>`; `SileroVadSegmenter(SourceKind, VadOptions, ISpeechProbabilityModel)` with `SegmentAsync(IAsyncEnumerable<AudioFrame>, CancellationToken) -> IAsyncEnumerable<AudioSegment>`; `SileroVadModel(ModelPaths.Require("silero_vad.onnx"))`.
- Produces: `SegmentRow(long StartMs, long EndMs)` JSON rows, one per line, written to `<wav>.segments.jsonl` next to the input wav (or `--out <path>`). Later tasks read this file — its shape is the contract.

- [ ] **Step 1: Write the failing test (row serialization round-trip — the cross-task contract)**

```csharp
// tests/LocalScribe.AsrBench.Tests/SegmentRowTest.cs
using System.Text.Json;

namespace LocalScribe.AsrBench.Tests;

public class SegmentRowTest
{
    [Fact]
    public void Round_trips_camelCase_json()
    {
        var row = new SegmentRow(1500, 4200);
        string json = JsonSerializer.Serialize(row, BenchJson.Options);
        Assert.Equal("""{"startMs":1500,"endMs":4200}""", json);
        Assert.Equal(row, JsonSerializer.Deserialize<SegmentRow>(json, BenchJson.Options));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LocalScribe.AsrBench.Tests --filter SegmentRowTest`
Expected: FAIL — `SegmentRow`/`BenchJson` do not exist.

- [ ] **Step 3: Implement**

Create `src/LocalScribe.AsrBench/BenchJson.cs`:

```csharp
// src/LocalScribe.AsrBench/BenchJson.cs
using System.Text.Json;

namespace LocalScribe.AsrBench;

public sealed record SegmentRow(long StartMs, long EndMs);

public static class BenchJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
```

Create `src/LocalScribe.AsrBench/SegmentCommand.cs` (and delete the stub from `Stubs.cs`):

```csharp
// src/LocalScribe.AsrBench/SegmentCommand.cs
// `segment --wav <path> [--out <path>]`: run the production VAD over a wav and persist
// the utterance boundaries. Both engine lanes replay EXACTLY these segments, so the
// comparison can never be skewed by different segmentation.
using System.Text.Json;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

namespace LocalScribe.AsrBench;

internal static class SegmentCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? wav = Arg(args, "--wav");
        if (wav is null) { Console.Error.WriteLine("usage: segment --wav <path> [--out <path>]"); return 2; }
        string outPath = Arg(args, "--out") ?? wav + ".segments.jsonl";

        var segmenter = new SileroVadSegmenter(SourceKind.Local, new VadOptions(),
            new SileroVadModel(ModelPaths.Require("silero_vad.onnx")));

        await using var writer = new StreamWriter(outPath);
        int count = 0;
        await foreach (var seg in segmenter.SegmentAsync(
            ToAsync(WavFileFrameReader.ReadFrames(wav, SourceKind.Local)), CancellationToken.None))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new SegmentRow(seg.StartMs, seg.EndMs), BenchJson.Options));
            count++;
        }
        Console.WriteLine($"{count} segments -> {outPath}");
        return 0;
    }

    internal static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static async IAsyncEnumerable<AudioFrame> ToAsync(IEnumerable<AudioFrame> frames)
    {
        foreach (var f in frames) yield return f;
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Run tests + smoke**

Run: `dotnet test tests/LocalScribe.AsrBench.Tests`
Expected: PASS.
Smoke (uses the Phase-0 wav conversion trick from Task 2 step 8 against any speech wav; the parakeet `test_wavs` sample works):

```powershell
tools/ffmpeg/ffmpeg.exe -i models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/test_wavs/en.wav -ar 16000 -ac 1 -acodec pcm_s16le bench-smoke.wav
dotnet run --project src/LocalScribe.AsrBench -- segment --wav bench-smoke.wav
```

Expected: `N segments -> bench-smoke.wav.segments.jsonl` with N >= 1; rows are `{"startMs":..,"endMs":..}` with start < end, monotonic. Cleanup both files after.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalScribe.AsrBench tests/LocalScribe.AsrBench.Tests
git commit -m "feat(spike): segment subcommand - production VAD to segments.jsonl"
```

---

### Task 4: Bench primitives — PCM slicing, cadence math, result rows (TDD)

**Files:**
- Create: `src/LocalScribe.AsrBench/PcmSlicer.cs`
- Create: `src/LocalScribe.AsrBench/Cadence.cs`
- Create: `src/LocalScribe.AsrBench/BenchRow.cs`
- Test: `tests/LocalScribe.AsrBench.Tests/PcmSlicerTest.cs`
- Test: `tests/LocalScribe.AsrBench.Tests/CadenceTest.cs`

**Interfaces:**
- Produces: `PcmSlicer.LoadMono16k(string wavPath) -> float[]` (via Core's `WavFileFrameReader`, so bench slices match pipeline framing) and `PcmSlicer.Slice(float[] pcm, long startMs, long endMs) -> ReadOnlyMemory<float>`.
- Produces: `Cadence.DelayMs(long segmentEndMs, double elapsedMs) -> double` — how long a live-cadence replay must wait before offering this segment (0 in batch mode or when already due).
- Produces: `BenchRow(string Engine, string Model, string WeightsFile, int Threads, string Mode, string Wav, long StartMs, long EndMs, double OfferedAtMs, double LatencyMs, double DecodeMs, string Text)` — the results.jsonl contract every later task reads. `LatencyMs` = parent-observed offered->completed; `DecodeMs` = in-engine time (whisper: equals LatencyMs; parakeet: child-reported, so `LatencyMs - DecodeMs` is the process-boundary overhead).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.AsrBench.Tests/PcmSlicerTest.cs
namespace LocalScribe.AsrBench.Tests;

public class PcmSlicerTest
{
    [Fact]
    public void Slices_by_ms_at_16k()
    {
        float[] pcm = new float[16000 * 2];              // 2 s
        pcm[16000] = 0.5f;                               // first sample of second 1
        var slice = PcmSlicer.Slice(pcm, 1000, 1500);
        Assert.Equal(8000, slice.Length);                // 500 ms = 8000 samples
        Assert.Equal(0.5f, slice.Span[0]);
    }

    [Fact]
    public void Clamps_end_beyond_audio()
    {
        float[] pcm = new float[16000];                  // 1 s
        var slice = PcmSlicer.Slice(pcm, 500, 5000);
        Assert.Equal(8000, slice.Length);                // clamped to audio end
    }
}
```

```csharp
// tests/LocalScribe.AsrBench.Tests/CadenceTest.cs
namespace LocalScribe.AsrBench.Tests;

public class CadenceTest
{
    [Fact]
    public void Waits_until_segment_would_end_live()
        => Assert.Equal(2500, Cadence.DelayMs(segmentEndMs: 4000, elapsedMs: 1500));

    [Fact]
    public void No_wait_when_already_due()
        => Assert.Equal(0, Cadence.DelayMs(segmentEndMs: 4000, elapsedMs: 6000));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/LocalScribe.AsrBench.Tests --filter "PcmSlicerTest|CadenceTest"`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.AsrBench/PcmSlicer.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;

namespace LocalScribe.AsrBench;

public static class PcmSlicer
{
    /// <summary>Whole file as one 16 kHz mono buffer, decoded through the SAME frame
    /// reader the pipeline uses (resample/downmix behaviour identical to production).</summary>
    public static float[] LoadMono16k(string wavPath)
        => WavFileFrameReader.ReadFrames(wavPath, SourceKind.Local)
            .SelectMany(f => f.Samples).ToArray();

    public static ReadOnlyMemory<float> Slice(float[] pcm, long startMs, long endMs)
    {
        int start = (int)Math.Clamp(startMs * 16, 0, pcm.Length);
        int end = (int)Math.Clamp(endMs * 16, start, pcm.Length);
        return pcm.AsMemory(start, end - start);
    }
}
```

```csharp
// src/LocalScribe.AsrBench/Cadence.cs
namespace LocalScribe.AsrBench;

public static class Cadence
{
    /// <summary>Live replay: a segment only becomes available when it would have ended in
    /// a real session (VAD emits at utterance end). Batch mode simply skips the delay.</summary>
    public static double DelayMs(long segmentEndMs, double elapsedMs)
        => Math.Max(0, segmentEndMs - elapsedMs);
}
```

```csharp
// src/LocalScribe.AsrBench/BenchRow.cs
namespace LocalScribe.AsrBench;

/// <summary>One transcribed segment in results.jsonl - the contract between `run`,
/// `score`, and the report. DecodeMs is in-engine time; LatencyMs is parent-observed
/// offered-to-completed, so (LatencyMs - DecodeMs) on the parakeet lane is the
/// process-boundary overhead the adoption hosting decision needs.</summary>
public sealed record BenchRow(
    string Engine, string Model, string WeightsFile, int Threads, string Mode, string Wav,
    long StartMs, long EndMs, double OfferedAtMs, double LatencyMs, double DecodeMs, string Text);
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/LocalScribe.AsrBench.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalScribe.AsrBench tests/LocalScribe.AsrBench.Tests
git commit -m "feat(spike): bench primitives - pcm slicing, cadence math, results row contract"
```

---

### Task 5: Whisper lane — `run --engine whisper`

**Files:**
- Create: `src/LocalScribe.AsrBench/RunCommand.cs` (whisper path; parakeet path added in Task 7)
- Modify: `src/LocalScribe.AsrBench/Stubs.cs` (remove RunCommand stub)

**Interfaces:**
- Consumes (Core): `WhisperEngineFactory().CreateAsync(BackendPlan, language, initialPrompt, ct) -> ITranscriptionEngine` with `.WeightsFile` and `.TranscribeAsync(AudioSegment, ct) -> TranscriptionResult(Text, DetectedLanguage, NoSpeechProb)`; `BackendPlan(Backend.Cpu, modelName, int? CpuThreads)`; `AudioSegment(SourceKind, StartMs, EndMs, ReadOnlyMemory<float>)`.
- Consumes (Task 3/4): `SegmentRow`, `PcmSlicer`, `Cadence`, `BenchRow`, `BenchJson`, `SegmentCommand.Arg`.
- Produces: `run --engine whisper --wav <w> --segments <s.jsonl> --model small.en --threads N --mode live|batch --out results.jsonl` appending one `BenchRow` JSON line per segment. Also produces the shared helpers `RunCommand.ReadSegments` and `RunCommand.AppendRowsAsync` that Task 7 reuses.

- [ ] **Step 1: Implement (wiring — no unit test; verified by smoke + Task 12 sanity)**

```csharp
// src/LocalScribe.AsrBench/RunCommand.cs
// `run`: replay a segments.jsonl through one engine and append BenchRows.
// Whisper lane runs IN-PROCESS through the production factory (the baseline is the code
// that ships). Parakeet lane (Task 7) is ALWAYS the out-of-process ParakeetLane child:
// sherpa's bundled ORT must never share an output dir with Core's ORT 1.22.0.
using System.Diagnostics;
using System.Text.Json;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

namespace LocalScribe.AsrBench;

internal static class RunCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? engine = SegmentCommand.Arg(args, "--engine");
        string? wav = SegmentCommand.Arg(args, "--wav");
        string? segPath = SegmentCommand.Arg(args, "--segments");
        string mode = SegmentCommand.Arg(args, "--mode") ?? "batch";
        int threads = int.TryParse(SegmentCommand.Arg(args, "--threads"), out int t) ? t : 4;
        string outPath = SegmentCommand.Arg(args, "--out") ?? "results.jsonl";
        if (engine is null || wav is null || segPath is null)
        {
            Console.Error.WriteLine("usage: run --engine whisper|parakeet --wav <w> --segments <s> " +
                "[--model small.en] [--parakeet-dir <dir>] [--threads 4] [--mode live|batch] [--out results.jsonl]");
            return 2;
        }

        var segments = ReadSegments(segPath);
        var rows = engine switch
        {
            "whisper" => await RunWhisperAsync(wav, segments,
                SegmentCommand.Arg(args, "--model") ?? "small.en", threads, mode),
            "parakeet" => await ParakeetDriver.RunAsync(wav, segments,
                SegmentCommand.Arg(args, "--parakeet-dir")
                    ?? Path.Combine(ModelPaths.ModelsRoot, "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"),
                threads, mode),
            _ => throw new ArgumentException($"unknown engine {engine}"),
        };
        await AppendRowsAsync(outPath, rows);
        Console.WriteLine($"{rows.Count} rows -> {outPath}");
        return 0;
    }

    private static async Task<List<BenchRow>> RunWhisperAsync(
        string wav, List<SegmentRow> segments, string model, int threads, string mode)
    {
        // CPU only - the bench must never accidentally measure CUDA/Vulkan.
        Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
            [Whisper.net.LibraryLoader.RuntimeLibrary.Cpu];

        var plan = new BackendPlan(Backend.Cpu, model, threads > 0 ? threads : null);
        await using var eng = await new WhisperEngineFactory()
            .CreateAsync(plan, "en", null, CancellationToken.None);

        float[] pcm = PcmSlicer.LoadMono16k(wav);
        var rows = new List<BenchRow>();
        var wall = Stopwatch.StartNew();
        foreach (var seg in segments)
        {
            if (mode == "live")
            {
                double delay = Cadence.DelayMs(seg.EndMs, wall.Elapsed.TotalMilliseconds);
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay));
            }
            double offeredAt = wall.Elapsed.TotalMilliseconds;
            var result = await eng.TranscribeAsync(
                new AudioSegment(SourceKind.Local, seg.StartMs, seg.EndMs,
                    PcmSlicer.Slice(pcm, seg.StartMs, seg.EndMs)),
                CancellationToken.None);
            double latency = wall.Elapsed.TotalMilliseconds - offeredAt;
            rows.Add(new BenchRow("whisper", eng.ModelName, eng.WeightsFile, threads, mode,
                Path.GetFileName(wav), seg.StartMs, seg.EndMs, offeredAt, latency, latency,
                result.Text));
        }
        return rows;
    }

    internal static List<SegmentRow> ReadSegments(string path)
        => File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<SegmentRow>(l, BenchJson.Options)!)
            .ToList();

    internal static async Task AppendRowsAsync(string path, IEnumerable<BenchRow> rows)
    {
        await using var w = new StreamWriter(path, append: true);
        foreach (var r in rows)
            await w.WriteLineAsync(JsonSerializer.Serialize(r, BenchJson.Options));
    }
}
```

Also create a placeholder `ParakeetDriver` so this compiles (replaced for real in Task 7):

```csharp
// src/LocalScribe.AsrBench/ParakeetDriver.cs -- real implementation lands in Task 7.
namespace LocalScribe.AsrBench;

internal static class ParakeetDriver
{
    public static Task<List<BenchRow>> RunAsync(string wav, List<SegmentRow> segments,
        string modelDir, int threads, string mode)
        => throw new NotImplementedException("parakeet lane lands in Task 7");
}
```

Delete the `RunCommand` stub from `Stubs.cs`.

- [ ] **Step 2: Build + existing tests still green**

Run: `dotnet build LocalScribe.slnx && dotnet test tests/LocalScribe.AsrBench.Tests`
Expected: PASS.

- [ ] **Step 3: Smoke (needs whisper models fetched — `tools/fetch-models.ps1` if `models/` lacks `ggml-small.en*`)**

```powershell
tools/ffmpeg/ffmpeg.exe -i models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/test_wavs/en.wav -ar 16000 -ac 1 -acodec pcm_s16le bench-smoke.wav
dotnet run --project src/LocalScribe.AsrBench -- segment --wav bench-smoke.wav
dotnet run --project src/LocalScribe.AsrBench -- run --engine whisper --wav bench-smoke.wav --segments bench-smoke.wav.segments.jsonl --threads 4 --mode batch --out bench-smoke.results.jsonl
```

Expected: `N rows -> bench-smoke.results.jsonl`; each row has `"engine":"whisper"`, a `"weightsFile"` naming the actual q8_0 file (q8_0 preferred on CPU by `ModelFileResolver`), non-empty text matching the sample's speech, `decodeMs == latencyMs`. Cleanup the three bench-smoke files.

- [ ] **Step 4: Commit**

```powershell
git add src/LocalScribe.AsrBench
git commit -m "feat(spike): whisper lane - production seam, forced CPU, cadence replay"
```

---

### Task 6: ParakeetLane persistent stdio protocol

**Files:**
- Modify: `src/LocalScribe.ParakeetLane/Program.cs` (add protocol mode; keep `--check`)
- Create: `src/LocalScribe.ParakeetLane/Protocol.cs`

**Interfaces:**
- Produces (the wire contract Task 7 consumes — mirrors the Diarizer's JSON-lines style and the adoption helper shape):
  - Parent -> child line 1 (init): `{"modelDir":"...","threads":4,"wavPath":"..."}`
  - Child -> parent: `{"ready":true,"loadMs":1234}` (or `{"error":"...","detail":"..."}` then exit 1)
  - Parent -> child per segment: `{"id":0,"startMs":1500,"endMs":4200}`
  - Child -> parent per segment: `{"id":0,"text":"...","tokens":["▁he","llo"],"timestampsSec":[0.0,0.24],"decodeMs":812.5}`
  - Parent closes stdin -> child exits 0.

- [ ] **Step 1: Create `src/LocalScribe.ParakeetLane/Protocol.cs`**

```csharp
// src/LocalScribe.ParakeetLane/Protocol.cs
// Wire records for the persistent stdio protocol. camelCase JSON lines, one object per
// line (Diarizer stdout-contract style). This IS the shape an adopted
// LocalScribe.Transcriber helper would speak - the spike measures its overhead for real.
using System.Text.Json;

namespace LocalScribe.ParakeetLane;

public sealed record InitMsg(string ModelDir, int Threads, string WavPath);
public sealed record ReadyMsg(bool Ready, double LoadMs);
public sealed record SegmentMsg(int Id, long StartMs, long EndMs);
public sealed record ResultMsg(int Id, string Text, string[] Tokens, float[] TimestampsSec, double DecodeMs);
public sealed record ErrorMsg(string Error, string Detail);

public static class LaneJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
```

- [ ] **Step 2: Add the protocol loop to `Program.cs`**

Replace the final usage-error block of `Program.cs` (keep the `--check` branch above it) with:

```csharp
if (args.Length == 0)
{
    // Persistent protocol mode (see Protocol.cs). One init line, then segment lines
    // until stdin closes. All errors -> one ErrorMsg line + exit 1 (Diarizer contract).
    var stdout = Console.Out;
    void Emit(object o) => stdout.WriteLine(JsonSerializer.Serialize(o, LaneJson.Options));
    try
    {
        string? initLine = Console.In.ReadLine();
        var init = JsonSerializer.Deserialize<InitMsg>(initLine ?? "", LaneJson.Options)
                   ?? throw new InvalidDataException("missing init line");

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = Path.Combine(init.ModelDir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(init.ModelDir, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(init.ModelDir, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(init.ModelDir, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = init.Threads;

        var loadSw = Stopwatch.StartNew();
        using var recognizer = new OfflineRecognizer(config);
        float[] pcm = WavReader.ReadMono16k(init.WavPath);
        Emit(new ReadyMsg(true, loadSw.Elapsed.TotalMilliseconds));

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var seg = JsonSerializer.Deserialize<SegmentMsg>(line, LaneJson.Options)!;
            int start = (int)Math.Clamp(seg.StartMs * 16, 0, pcm.Length);
            int end = (int)Math.Clamp(seg.EndMs * 16, start, pcm.Length);
            var slice = new float[end - start];
            Array.Copy(pcm, start, slice, 0, slice.Length);

            var sw = Stopwatch.StartNew();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16000, slice);
            recognizer.Decode(stream);
            var r = stream.Result;
            sw.Stop();
            Emit(new ResultMsg(seg.Id, r.Text, r.Tokens, r.Timestamps, sw.Elapsed.TotalMilliseconds));
        }
        return 0;
    }
    catch (Exception ex)
    {
        Emit(new ErrorMsg("LANE_CRASH", ex.Message));
        return 1;
    }
}
```

(Adjust the sherpa result-access lines to whatever Task 2's empirical check settled on.)

- [ ] **Step 3: Build + manual protocol smoke**

Run: `dotnet build src/LocalScribe.ParakeetLane`
Then, using the Task 5 smoke wav (re-create if cleaned up):

```powershell
@'
{"modelDir":"models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8","threads":4,"wavPath":"bench-smoke.wav"}
{"id":0,"startMs":0,"endMs":3000}
'@ | dotnet run --project src/LocalScribe.ParakeetLane
```

Expected: a `{"ready":true,"loadMs":...}` line, then one result line with id 0, non-empty text, tokens/timestamps arrays, `decodeMs` > 0; exit 0.

- [ ] **Step 4: Commit**

```powershell
git add src/LocalScribe.ParakeetLane
git commit -m "feat(spike): ParakeetLane persistent stdio protocol (adoption-shaped)"
```

---

### Task 7: Parakeet driver in AsrBench + token-to-word merge (TDD)

**Files:**
- Modify: `src/LocalScribe.AsrBench/ParakeetDriver.cs` (replace the Task-5 placeholder)
- Create: `src/LocalScribe.AsrBench/WordMerge.cs`
- Test: `tests/LocalScribe.AsrBench.Tests/WordMergeTest.cs`

**Interfaces:**
- Consumes: the Task-6 wire contract (duplicate the wire records privately in the driver — AsrBench must NOT reference the ParakeetLane project, or its sherpa natives and ORT would flow into AsrBench's output dir).
- Produces: `ParakeetDriver.RunAsync(wav, segments, modelDir, threads, mode) -> List<BenchRow>` (signature fixed in Task 5) plus a side file `<out>.timestamps.jsonl` written by `run --engine parakeet` when `--timestamps <path>` is passed: rows `{"startMs":..,"endMs":..,"words":[{"word":"hello","startMs":1740,"endMs":1990}]}`.
- Produces: `WordMerge.Merge(string[] tokens, float[] timestampsSec, long segmentStartMs, long segmentEndMs) -> List<TimedWord>` with `TimedWord(string Word, long StartMs, long EndMs)`; tokens beginning with `'▁'` (BPE word boundary) start a new word; a word's EndMs is the next word's StartMs (last word: segmentEndMs); timestamps are segment-relative seconds converted to absolute ms.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.AsrBench.Tests/WordMergeTest.cs
namespace LocalScribe.AsrBench.Tests;

public class WordMergeTest
{
    [Fact]
    public void Merges_bpe_tokens_into_timed_words()
    {
        // "▁he" "llo" "▁world" at 0.10s / 0.30s / 0.52s in a segment at 1000..3000 ms
        string[] tokens = ["▁he", "llo", "▁world"];
        float[] times = [0.10f, 0.30f, 0.52f];
        var words = WordMerge.Merge(tokens, times, 1000, 3000);
        Assert.Equal(2, words.Count);
        Assert.Equal(new TimedWord("hello", 1100, 1520), words[0]);
        Assert.Equal(new TimedWord("world", 1520, 3000), words[1]);
    }

    [Fact]
    public void Empty_tokens_yield_no_words()
        => Assert.Empty(WordMerge.Merge([], [], 0, 1000));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/LocalScribe.AsrBench.Tests --filter WordMergeTest`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement `WordMerge`**

```csharp
// src/LocalScribe.AsrBench/WordMerge.cs
namespace LocalScribe.AsrBench;

public sealed record TimedWord(string Word, long StartMs, long EndMs);

/// <summary>Merge BPE tokens + per-token timestamps into word-level timings (Axis 3).
/// '▁' prefix marks a word start (sentencepiece convention).</summary>
public static class WordMerge
{
    public static List<TimedWord> Merge(
        string[] tokens, float[] timestampsSec, long segmentStartMs, long segmentEndMs)
    {
        var starts = new List<(string Word, long StartMs)>();
        foreach (var (tok, i) in tokens.Select((t, i) => (t, i)))
        {
            long at = segmentStartMs + (long)Math.Round(timestampsSec[i] * 1000);
            if (tok.StartsWith('▁'))
                starts.Add((tok[1..], at));
            else if (starts.Count > 0)
                starts[^1] = (starts[^1].Word + tok, starts[^1].StartMs);
            else
                starts.Add((tok, at));                    // degenerate: no boundary marker yet
        }
        var words = new List<TimedWord>();
        for (int i = 0; i < starts.Count; i++)
            words.Add(new TimedWord(starts[i].Word, starts[i].StartMs,
                i + 1 < starts.Count ? starts[i + 1].StartMs : segmentEndMs));
        return words;
    }
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test tests/LocalScribe.AsrBench.Tests --filter WordMergeTest`
Expected: PASS.

- [ ] **Step 5: Replace the `ParakeetDriver` placeholder**

```csharp
// src/LocalScribe.AsrBench/ParakeetDriver.cs
// Drives the ParakeetLane child over its stdio protocol. The wire records are duplicated
// here on purpose: referencing the ParakeetLane PROJECT would pull sherpa's natives (and
// its ORT) into AsrBench's output dir next to Core's ORT 1.22.0 - the exact collision
// the two-process split exists to prevent.
using System.Diagnostics;
using System.Text.Json;

namespace LocalScribe.AsrBench;

internal static class ParakeetDriver
{
    private sealed record InitMsg(string ModelDir, int Threads, string WavPath);
    private sealed record ReadyMsg(bool Ready, double LoadMs);
    private sealed record SegmentMsg(int Id, long StartMs, long EndMs);
    private sealed record ResultMsg(int Id, string Text, string[] Tokens, float[] TimestampsSec, double DecodeMs);

    public static async Task<List<BenchRow>> RunAsync(string wav, List<SegmentRow> segments,
        string modelDir, int threads, string mode, string? timestampsOut = null)
    {
        string laneExe = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "LocalScribe.ParakeetLane", "bin", "Debug", "net10.0-windows", "LocalScribe.ParakeetLane.exe");
        laneExe = Path.GetFullPath(laneExe);
        if (!File.Exists(laneExe))
            throw new FileNotFoundException(
                $"lane exe missing: {laneExe}. Run `dotnet build src/LocalScribe.ParakeetLane` first.");

        using var proc = Process.Start(new ProcessStartInfo(laneExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("failed to start lane");
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"[lane] {e.Data}"); };
        proc.BeginErrorReadLine();

        await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(
            new InitMsg(modelDir, threads, Path.GetFullPath(wav)), BenchJson.Options));

        string ready = await proc.StandardOutput.ReadLineAsync()
            ?? throw new InvalidOperationException("lane closed before ready");
        if (!ready.Contains("\"ready\":true"))
            throw new InvalidOperationException($"lane init failed: {ready}");

        var rows = new List<BenchRow>();
        var tsRows = new List<string>();
        var wall = Stopwatch.StartNew();
        foreach (var (seg, i) in segments.Select((s, i) => (s, i)))
        {
            if (mode == "live")
            {
                double delay = Cadence.DelayMs(seg.EndMs, wall.Elapsed.TotalMilliseconds);
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay));
            }
            double offeredAt = wall.Elapsed.TotalMilliseconds;
            await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(
                new SegmentMsg(i, seg.StartMs, seg.EndMs), BenchJson.Options));
            string? respLine = await proc.StandardOutput.ReadLineAsync()
                ?? throw new InvalidOperationException($"lane died at segment {i} (record as engine-failure finding)");
            double latency = wall.Elapsed.TotalMilliseconds - offeredAt;
            var resp = JsonSerializer.Deserialize<ResultMsg>(respLine, BenchJson.Options)
                ?? throw new InvalidDataException($"bad lane response: {respLine}");

            rows.Add(new BenchRow("parakeet", "parakeet-tdt-0.6b-v3", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
                threads, mode, Path.GetFileName(wav), seg.StartMs, seg.EndMs, offeredAt, latency,
                resp.DecodeMs, resp.Text));

            if (timestampsOut is not null)
                tsRows.Add(JsonSerializer.Serialize(new
                {
                    startMs = seg.StartMs,
                    endMs = seg.EndMs,
                    words = WordMerge.Merge(resp.Tokens, resp.TimestampsSec, seg.StartMs, seg.EndMs),
                }, BenchJson.Options));
        }
        proc.StandardInput.Close();
        await proc.WaitForExitAsync();
        if (timestampsOut is not null) await File.WriteAllLinesAsync(timestampsOut, tsRows);
        return rows;
    }
}
```

And in `RunCommand.RunAsync`, thread the optional flag through the parakeet arm:

```csharp
            "parakeet" => await ParakeetDriver.RunAsync(wav, segments,
                SegmentCommand.Arg(args, "--parakeet-dir")
                    ?? Path.Combine(ModelPaths.ModelsRoot, "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"),
                threads, mode, SegmentCommand.Arg(args, "--timestamps")),
```

Delete `Stubs.cs` entries as they empty out (file itself once all four are gone — score/matrix remain until Tasks 8/9).

- [ ] **Step 6: Full test run + smoke**

Run: `dotnet test tests/LocalScribe.AsrBench.Tests && dotnet build src/LocalScribe.ParakeetLane`
Smoke (same bench-smoke.wav recipe as Task 5):

```powershell
dotnet run --project src/LocalScribe.AsrBench -- run --engine parakeet --wav bench-smoke.wav --segments bench-smoke.wav.segments.jsonl --threads 4 --mode batch --out bench-smoke.results.jsonl --timestamps bench-smoke.timestamps.jsonl
```

Expected: rows with `"engine":"parakeet"`, non-empty text, `decodeMs < latencyMs` (round-trip includes pipe overhead), and a timestamps file with per-word ms timings. Cleanup smoke files.

- [ ] **Step 7: Commit**

```powershell
git add src/LocalScribe.AsrBench tests/LocalScribe.AsrBench.Tests
git commit -m "feat(spike): parakeet driver + word-timestamp merge; overhead measured as latency-decode delta"
```

---

### Task 8: `score` subcommand — WER + latency aggregates (TDD)

**Files:**
- Create: `src/LocalScribe.AsrBench/ScoreCommand.cs`
- Create: `src/LocalScribe.AsrBench/Aggregates.cs`
- Modify: `src/LocalScribe.AsrBench/Stubs.cs` (remove ScoreCommand stub)
- Test: `tests/LocalScribe.AsrBench.Tests/AggregatesTest.cs`

**Interfaces:**
- Consumes: `BenchRow` results.jsonl; reference text files named `<wav-basename>.txt` in a `--refs <dir>` directory (plain text, no timestamps — the Tier-1 runbook and prep-librispeech.ps1 both produce this shape); Core's `WerCalculator.Wer(reference, hypothesis) -> double`.
- Produces: `Aggregates.Percentile(IReadOnlyList<double> sorted, double p) -> double` (nearest-rank on a pre-sorted list); `Aggregates.WeightedWer(IEnumerable<(double Wer, int RefWords)> samples) -> double`. `score --results <r.jsonl> --refs <dir> [--out summary.md]` prints and writes a markdown table: one row per (engine, model, threads, mode, wav) with segments, audio s, p50/p95 latency ms, RTF (sum decodeMs / sum audio ms), overhead p50 (latency-decode), WER; plus a word-weighted aggregate WER row per engine config.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.AsrBench.Tests/AggregatesTest.cs
namespace LocalScribe.AsrBench.Tests;

public class AggregatesTest
{
    [Fact]
    public void Nearest_rank_percentiles()
    {
        double[] sorted = [10, 20, 30, 40, 100];
        Assert.Equal(30, Aggregates.Percentile(sorted, 50));
        Assert.Equal(100, Aggregates.Percentile(sorted, 95));
        Assert.Equal(10, Aggregates.Percentile(sorted, 0));
    }

    [Fact]
    public void Weighted_wer_weights_by_reference_length()
    {
        // 100-word sample at 10% and 10-word sample at 50% -> (10 + 5) / 110
        double agg = Aggregates.WeightedWer([(0.10, 100), (0.50, 10)]);
        Assert.Equal(15.0 / 110.0, agg, 10);
    }

    [Fact]
    public void Weighted_wer_empty_is_zero()
        => Assert.Equal(0.0, Aggregates.WeightedWer([]));
}
```

- [ ] **Step 2: Run to verify FAIL**

Run: `dotnet test tests/LocalScribe.AsrBench.Tests --filter AggregatesTest`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.AsrBench/Aggregates.cs
namespace LocalScribe.AsrBench;

public static class Aggregates
{
    /// <summary>Nearest-rank percentile over an ascending-sorted list.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    /// <summary>Aggregate WER weighted by reference word count (long samples count more).</summary>
    public static double WeightedWer(IEnumerable<(double Wer, int RefWords)> samples)
    {
        double err = 0; long words = 0;
        foreach (var (wer, refWords) in samples) { err += wer * refWords; words += refWords; }
        return words == 0 ? 0.0 : err / words;
    }
}
```

```csharp
// src/LocalScribe.AsrBench/ScoreCommand.cs
// `score --results <r.jsonl> --refs <dir> [--out summary.md]`.
// Reference files: <wav-basename>.txt (e.g. clean-000.wav -> clean-000.txt). A wav with
// no reference gets latency stats only, WER "-" (logged - no silent drops).
using System.Text;
using System.Text.Json;
using LocalScribe.Core.Pipeline;

namespace LocalScribe.AsrBench;

internal static class ScoreCommand
{
    public static int Run(string[] args)
    {
        string? results = SegmentCommand.Arg(args, "--results");
        string? refs = SegmentCommand.Arg(args, "--refs");
        if (results is null) { Console.Error.WriteLine("usage: score --results <r.jsonl> [--refs <dir>] [--out summary.md]"); return 2; }

        var rows = File.ReadLines(results)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<BenchRow>(l, BenchJson.Options)!)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("| engine | model | thr | mode | wav | segs | audio s | p50 ms | p95 ms | RTF | ovh p50 ms | WER |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

        var perConfigWer = new Dictionary<string, List<(double Wer, int RefWords)>>();
        foreach (var g in rows.GroupBy(r => (r.Engine, r.Model, r.Threads, r.Mode, r.Wav)))
        {
            var lat = g.Select(r => r.LatencyMs).OrderBy(x => x).ToList();
            var ovh = g.Select(r => r.LatencyMs - r.DecodeMs).OrderBy(x => x).ToList();
            double audioMs = g.Sum(r => r.EndMs - r.StartMs);
            double rtf = g.Sum(r => r.DecodeMs) / Math.Max(1, audioMs);

            string werCell = "-";
            string refPath = refs is null ? "" : Path.Combine(refs,
                Path.GetFileNameWithoutExtension(g.Key.Wav) + ".txt");
            if (refs is not null && File.Exists(refPath))
            {
                string reference = File.ReadAllText(refPath);
                string hypothesis = string.Join(" ", g.OrderBy(r => r.StartMs).Select(r => r.Text));
                double wer = WerCalculator.Wer(reference, hypothesis);
                int refWords = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                werCell = (wer * 100).ToString("F2") + "%";
                string cfg = $"{g.Key.Engine}/{g.Key.Model}/t{g.Key.Threads}/{g.Key.Mode}";
                if (!perConfigWer.TryGetValue(cfg, out var list)) perConfigWer[cfg] = list = [];
                list.Add((wer, refWords));
            }
            else if (refs is not null)
            {
                Console.Error.WriteLine($"note: no reference for {g.Key.Wav} (looked for {refPath}) - WER skipped");
            }

            sb.AppendLine($"| {g.Key.Engine} | {g.Key.Model} | {g.Key.Threads} | {g.Key.Mode} | {g.Key.Wav} " +
                $"| {g.Count()} | {audioMs / 1000:F1} | {Aggregates.Percentile(lat, 50):F0} " +
                $"| {Aggregates.Percentile(lat, 95):F0} | {rtf:F3} | {Aggregates.Percentile(ovh, 50):F1} | {werCell} |");
        }

        sb.AppendLine();
        sb.AppendLine("Aggregate WER (word-weighted):");
        foreach (var (cfg, list) in perConfigWer.OrderBy(kv => kv.Key))
            sb.AppendLine($"- {cfg}: {Aggregates.WeightedWer(list) * 100:F2}%");

        Console.Write(sb.ToString());
        if (SegmentCommand.Arg(args, "--out") is { } outPath) File.WriteAllText(outPath, sb.ToString());
        return 0;
    }
}
```

Remove the ScoreCommand stub from `Stubs.cs`.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/LocalScribe.AsrBench.Tests`
Expected: all PASS. Quick smoke: `score` over a Task-5/7 smoke results file with `--refs` omitted prints latency table with WER `-`.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalScribe.AsrBench tests/LocalScribe.AsrBench.Tests
git commit -m "feat(spike): score subcommand - latency percentiles, RTF, overhead, weighted WER"
```

---

### Task 9: Constraint rig (job-object CPU cap) + `matrix` runner (TDD on spec parsing)

**Files:**
- Create: `src/LocalScribe.AsrBench/CpuRateJob.cs`
- Create: `src/LocalScribe.AsrBench/MatrixCommand.cs`
- Create: `src/LocalScribe.AsrBench/RunSpec.cs`
- Modify: `src/LocalScribe.AsrBench/Stubs.cs` (delete the file — last stub gone)
- Test: `tests/LocalScribe.AsrBench.Tests/RunSpecTest.cs`

**Interfaces:**
- Produces: `RunSpec` deserialized from a JSON file:

```json
{
  "wavs": ["bench-corpus/tier1/call1.wav", "bench-corpus/librispeech/phone-concat-000.wav"],
  "refsDir": "bench-corpus/refs",
  "engines": ["whisper", "parakeet"],
  "whisperModel": "small.en",
  "parakeetDir": "models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
  "threads": [2, 4],
  "modes": ["live", "batch"],
  "cpuRatePct": null,
  "outDir": "bench-corpus/results/run-01"
}
```

- Produces: `matrix --spec <spec.json>`: applies the CPU cap (if any) to the whole process tree, then for each wav ensures `<wav>.segments.jsonl` exists (runs SegmentCommand logic if not), then loops engines x threads x modes appending to `<outDir>/results.jsonl`, then runs ScoreCommand into `<outDir>/summary.md`. Configs run sequentially — never two engines at once (one-engine-at-a-time, and parallel runs would corrupt latency numbers).
- Produces: `CpuRateJob.ApplyToSelf(int pct)` — job object with `JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP`; child processes (ParakeetLane) inherit the job automatically.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.AsrBench.Tests/RunSpecTest.cs
using System.Text.Json;

namespace LocalScribe.AsrBench.Tests;

public class RunSpecTest
{
    [Fact]
    public void Parses_full_spec()
    {
        string json = """
        {"wavs":["a.wav"],"refsDir":"refs","engines":["whisper","parakeet"],
         "whisperModel":"small.en","parakeetDir":"models/pk","threads":[2,4],
         "modes":["live","batch"],"cpuRatePct":50,"outDir":"out"}
        """;
        var spec = JsonSerializer.Deserialize<RunSpec>(json, BenchJson.Options)!;
        Assert.Equal(["a.wav"], spec.Wavs);
        Assert.Equal([2, 4], spec.Threads);
        Assert.Equal(50, spec.CpuRatePct);
        Assert.Equal(8, spec.Combos().Count);            // 1 wav x 2 engines x 2 threads x 2 modes
    }

    [Fact]
    public void Null_cpu_rate_allowed()
    {
        string json = """
        {"wavs":["a.wav"],"refsDir":null,"engines":["whisper"],"whisperModel":"small.en",
         "parakeetDir":"p","threads":[4],"modes":["batch"],"cpuRatePct":null,"outDir":"out"}
        """;
        Assert.Null(JsonSerializer.Deserialize<RunSpec>(json, BenchJson.Options)!.CpuRatePct);
    }
}
```

- [ ] **Step 2: Verify FAIL, then implement `RunSpec`**

Run: `dotnet test tests/LocalScribe.AsrBench.Tests --filter RunSpecTest` — FAIL.

```csharp
// src/LocalScribe.AsrBench/RunSpec.cs
namespace LocalScribe.AsrBench;

public sealed record RunSpec(
    string[] Wavs, string? RefsDir, string[] Engines, string WhisperModel,
    string ParakeetDir, int[] Threads, string[] Modes, int? CpuRatePct, string OutDir)
{
    public List<(string Wav, string Engine, int Threads, string Mode)> Combos()
        => (from w in Wavs from e in Engines from t in Threads from m in Modes
            select (w, e, t, m)).ToList();
}
```

Run the filter again — PASS.

- [ ] **Step 3: Implement `CpuRateJob`**

```csharp
// src/LocalScribe.AsrBench/CpuRateJob.cs
// Job-object hard CPU cap for "cheap laptop" simulation (spec section 4 constraint rig).
// Applied to the bench process itself; ParakeetLane children inherit the job. Verified
// manually via Task Manager during a capped run (no unit test - kernel behaviour).
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalScribe.AsrBench;

public static class CpuRateJob
{
    public static void ApplyToSelf(int pct)
    {
        if (pct is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pct));
        nint job = CreateJobObject(0, null);
        if (job == 0) throw new Win32Exception();
        var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = 0x1 | 0x4,                    // ENABLE | HARD_CAP
            CpuRate = (uint)(pct * 100),                 // percent * 100 per docs
        };
        if (!SetInformationJobObject(job, 15 /* JobObjectCpuRateControlInformation */,
                ref info, (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
            throw new Win32Exception();
        if (!AssignProcessToJobObject(job, GetCurrentProcess()))
            throw new Win32Exception();
        Console.WriteLine($"cpu rate hard-capped at {pct}% (job object; children inherit)");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION { public uint ControlFlags; public uint CpuRate; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateJobObject(nint attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int infoClass, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetCurrentProcess();
}
```

- [ ] **Step 4: Implement `MatrixCommand`** (delete `Stubs.cs`)

```csharp
// src/LocalScribe.AsrBench/MatrixCommand.cs
// `matrix --spec <spec.json>`: the whole measurement run, sequential by design.
using System.Text.Json;

namespace LocalScribe.AsrBench;

internal static class MatrixCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? specPath = SegmentCommand.Arg(args, "--spec");
        if (specPath is null) { Console.Error.WriteLine("usage: matrix --spec <spec.json>"); return 2; }
        var spec = JsonSerializer.Deserialize<RunSpec>(File.ReadAllText(specPath), BenchJson.Options)
                   ?? throw new InvalidDataException("empty spec");
        Directory.CreateDirectory(spec.OutDir);
        if (spec.CpuRatePct is { } pct) CpuRateJob.ApplyToSelf(pct);

        foreach (string wav in spec.Wavs)
            if (!File.Exists(wav + ".segments.jsonl"))
            {
                Console.WriteLine($"segmenting {wav}");
                int rc = await SegmentCommand.RunAsync(["--wav", wav]);
                if (rc != 0) return rc;
            }

        string results = Path.Combine(spec.OutDir, "results.jsonl");
        var combos = spec.Combos();
        int done = 0;
        foreach (var (wav, engine, threads, mode) in combos)
        {
            Console.WriteLine($"[{++done}/{combos.Count}] {engine} t{threads} {mode} {Path.GetFileName(wav)}");
            var runArgs = new List<string>
            {
                "--engine", engine, "--wav", wav, "--segments", wav + ".segments.jsonl",
                "--threads", threads.ToString(), "--mode", mode, "--out", results,
            };
            if (engine == "whisper") runArgs.AddRange(["--model", spec.WhisperModel]);
            else runArgs.AddRange(["--parakeet-dir", spec.ParakeetDir]);
            int rc = await RunCommand.RunAsync(runArgs.ToArray());
            if (rc != 0)
            {
                // Engine failure is a FINDING (spec Phase 2), not a silent retry.
                Console.Error.WriteLine($"ENGINE FAILURE: {engine} t{threads} {mode} {wav} rc={rc} - recorded, continuing");
                await File.AppendAllTextAsync(Path.Combine(spec.OutDir, "failures.log"),
                    $"{engine} t{threads} {mode} {wav} rc={rc}{Environment.NewLine}");
            }
        }

        var scoreArgs = new List<string> { "--results", results, "--out", Path.Combine(spec.OutDir, "summary.md") };
        if (spec.RefsDir is not null) scoreArgs.AddRange(["--refs", spec.RefsDir]);
        return ScoreCommand.Run(scoreArgs.ToArray());
    }
}
```

- [ ] **Step 5: Full build + tests**

Run: `dotnet build LocalScribe.slnx && dotnet test tests/LocalScribe.AsrBench.Tests`
Expected: PASS (and `Stubs.cs` is gone).

- [ ] **Step 6: Commit**

```powershell
git add src/LocalScribe.AsrBench tests/LocalScribe.AsrBench.Tests
git commit -m "feat(spike): matrix runner + job-object cpu cap; engine failures recorded as findings"
```

---

### Task 10: prep-librispeech.ps1 — Tier-2 corpus (clean + telephone-degraded)

**Files:**
- Create: `tools/prep-librispeech.ps1`

**Interfaces:**
- Produces layout (all under gitignored `bench-corpus/`):
  - `bench-corpus/librispeech/clean-concat-NNN.wav` and `phone-concat-NNN.wav` (~10-min concatenations, 16 kHz mono PCM)
  - `bench-corpus/refs/clean-concat-NNN.txt` and `phone-concat-NNN.txt` (concatenated reference text, same order)
- Uses: `tools/ffmpeg/ffmpeg.exe` (run `tools/fetch-ffmpeg.ps1` first if missing).

- [ ] **Step 1: Create `tools/prep-librispeech.ps1`**

```powershell
# tools/prep-librispeech.ps1
# Tier-2 spike corpus (spec 2026-07-26 section 5): LibriSpeech test-other -> ~10-minute
# concatenated 16k mono WAVs, clean + telephone-degraded (300-3400 Hz band-limit, mu-law
# compand, 8 kHz round-trip), with matching reference text files for the WER scorer.
# Everything lands in gitignored bench-corpus/; nothing here is ever committed.
param(
    # Utterance cap (deterministic: sorted utterance ids). test-other has 2939 utterances;
    # the default keeps prep + runs manageable. The cap is LOGGED (no silent truncation).
    [int] $Subset = 600,
    # Target seconds per concatenated wav.
    [int] $ChunkSeconds = 600
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ffmpeg = Join-Path $root 'tools/ffmpeg/ffmpeg.exe'
if (-not (Test-Path $ffmpeg)) { throw "ffmpeg missing - run tools/fetch-ffmpeg.ps1 first" }
$corpus = Join-Path $root 'bench-corpus'
$ls = Join-Path $corpus 'librispeech'
$refs = Join-Path $corpus 'refs'
$work = Join-Path $corpus 'work'
New-Item -ItemType Directory -Force $ls, $refs, $work | Out-Null

# 1) download + extract test-other (328 MB) once
$tarball = Join-Path $work 'test-other.tar.gz'
if (-not (Test-Path (Join-Path $work 'LibriSpeech/test-other'))) {
    if (-not (Test-Path $tarball)) {
        Write-Host 'fetching LibriSpeech test-other (328 MB)'
        Invoke-WebRequest -Uri 'https://www.openslr.org/resources/12/test-other.tar.gz' -OutFile $tarball
    }
    tar -xzf $tarball -C $work
    if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)" }
}

# 2) collect (flac, transcript) pairs, sorted by utterance id for determinism
$pairs = @{}
Get-ChildItem -Recurse (Join-Path $work 'LibriSpeech/test-other') -Filter '*.trans.txt' | ForEach-Object {
    $dir = $_.DirectoryName
    Get-Content $_.FullName | ForEach-Object {
        $sp = $_.IndexOf(' ')
        $id = $_.Substring(0, $sp)
        $pairs[$id] = @{ Flac = Join-Path $dir "$id.flac"; Text = $_.Substring($sp + 1) }
    }
}
$ids = $pairs.Keys | Sort-Object
$total = $ids.Count
if ($ids.Count -gt $Subset) { $ids = $ids[0..($Subset - 1)] }
Write-Host "using $($ids.Count) of $total utterances (deterministic sorted prefix; -Subset to change)"

# 3) per-utterance decode to 16k mono wav (clean) + degraded variant, then concatenate
function New-Chunks([string] $variant, [scriptblock] $convert) {
    $chunkIdx = 0; $chunkSec = 0.0
    $listFile = Join-Path $work "$variant-list.txt"; Set-Content $listFile ''
    $refText = New-Object System.Text.StringBuilder
    foreach ($id in $ids) {
        $wavOut = Join-Path $work "$variant-$id.wav"
        if (-not (Test-Path $wavOut)) { & $convert $pairs[$id].Flac $wavOut }
        $sec = [double](& $ffmpeg -i $wavOut -f null - 2>&1 |
            Select-String 'time=(\d+):(\d+):(\d+\.\d+)' | ForEach-Object {
                $m = $_.Matches[0]; 3600 * [int]$m.Groups[1].Value + 60 * [int]$m.Groups[2].Value + [double]$m.Groups[3].Value
            } | Select-Object -Last 1)
        Add-Content $listFile "file '$($wavOut -replace '\\','/')'"
        [void]$refText.Append($pairs[$id].Text + ' ')
        $chunkSec += $sec
        if ($chunkSec -ge $ChunkSeconds) {
            Emit-Chunk $variant $chunkIdx $listFile $refText.ToString()
            $chunkIdx++; $chunkSec = 0.0
            Set-Content $listFile ''; $refText = New-Object System.Text.StringBuilder
        }
    }
    if ($chunkSec -gt 0) { Emit-Chunk $variant $chunkIdx $listFile $refText.ToString() }
}

function Emit-Chunk([string] $variant, [int] $idx, [string] $listFile, [string] $text) {
    $name = '{0}-concat-{1:D3}' -f $variant, $idx
    & $ffmpeg -y -f concat -safe 0 -i $listFile -ar 16000 -ac 1 -acodec pcm_s16le (Join-Path $ls "$name.wav") 2>$null
    if ($LASTEXITCODE -ne 0) { throw "concat failed for $name" }
    Set-Content (Join-Path $refs "$name.txt") $text.Trim()
    Write-Host "  $name.wav"
}

Write-Host 'building clean chunks'
New-Chunks 'clean' { param($in, $out)
    & $ffmpeg -y -i $in -ar 16000 -ac 1 -acodec pcm_s16le $out 2>$null
    if ($LASTEXITCODE -ne 0) { throw "convert failed: $in" }
}

Write-Host 'building telephone-degraded chunks (300-3400 Hz, mu-law, 8 kHz round-trip)'
New-Chunks 'phone' { param($in, $out)
    $tmp = "$out.tmp.wav"
    & $ffmpeg -y -i $in -af 'highpass=f=300,lowpass=f=3400' -ar 8000 -acodec pcm_mulaw $tmp 2>$null
    if ($LASTEXITCODE -ne 0) { throw "degrade failed: $in" }
    & $ffmpeg -y -i $tmp -ar 16000 -ac 1 -acodec pcm_s16le $out 2>$null
    if ($LASTEXITCODE -ne 0) { throw "upsample failed: $in" }
    Remove-Item $tmp
}

Write-Host "done: $ls (wavs), $refs (references)"
```

- [ ] **Step 2: Run it**

Run: `powershell -File tools/prep-librispeech.ps1`
Expected: `using 600 of 2939 utterances...`, then `clean-concat-000.wav` ... and `phone-concat-...` files appearing (roughly 6 chunks per variant at the default subset), plus matching `.txt` refs. Spot-check one: `tools/ffmpeg/ffmpeg.exe -i bench-corpus/librispeech/phone-concat-000.wav` reports 16000 Hz mono; play a few seconds — it should sound like a phone call.

- [ ] **Step 3: Commit (script only — corpus is gitignored)**

```powershell
git add tools/prep-librispeech.ps1
git commit -m "feat(spike): tier-2 corpus prep - librispeech clean + telephone-degraded chunks"
```

---

### Task 11: Tier-1 runbook (real sessions — USER-BLOCKING for hand-correction)

**Files:**
- Create: `docs/spikes/2026-07-26-parakeet-bench-runbook.md`

**Interfaces:**
- Produces: `bench-corpus/tier1/<name>.wav` (16 kHz mono excerpts) + `bench-corpus/refs/<name>.txt` (hand-corrected references) — same shapes Tasks 8/9 consume.

- [ ] **Step 1: Write `docs/spikes/2026-07-26-parakeet-bench-runbook.md`**

```markdown
# Parakeet spike - Tier-1 corpus runbook (user steps)

Real-session excerpts are the realism anchor for the WER axis (spec section 5). Audio and
transcripts stay in gitignored `bench-corpus/` - NEVER commit them.

## 1. Pick 2-4 sessions
Representative of the target: Webex jail-call acoustics, telephone-band far end,
crosstalk. Note each session id from the app's Sessions grid.

## 2. Cut excerpts (~5-10 min total across all excerpts)
Retained audio lives at `<storageRoot>/sessions/<id>/` as FLAC per source. For each
chosen span (prefer spans with contiguous clear speech AND some crosstalk):

    tools/ffmpeg/ffmpeg.exe -ss <start e.g. 00:03:20> -t <len e.g. 120> -i "<session>/audio-remote.flac" -ar 16000 -ac 1 -acodec pcm_s16le bench-corpus/tier1/call1-remote-a.wav

Use the REMOTE leg for telephone-band realism; a LOCAL-leg excerpt is also worth
including for contrast. Name files `<callN>-<leg>-<x>.wav`.

## 3. Generate draft transcripts (CUDA large-v3-turbo - best available)

    dotnet run --project src/LocalScribe.OfflineRunner -- --local bench-corpus/tier1/call1-remote-a.wav --model large-v3-turbo --backend cuda --out bench-corpus/tier1/drafts

Open the printed `read:` path (transcript md) for each excerpt.

## 4. Hand-correct into references (the only user-blocking step)
For each excerpt create `bench-corpus/refs/<same-basename>.txt` containing ONLY the
corrected spoken words (no timestamps, no speaker labels, no markers). Correct what the
draft got wrong; do not paraphrase. Punctuation/casing do not matter (the scorer
normalizes) - word identity does.

## 5. Done when
Every `bench-corpus/tier1/*.wav` has a matching `bench-corpus/refs/*.txt`.
```

- [ ] **Step 2: Commit + hand off to user**

```powershell
git add docs/spikes/2026-07-26-parakeet-bench-runbook.md
git commit -m "docs(spike): tier-1 corpus runbook (user hand-correction step)"
```

Notify the user this task is theirs; Tasks 12 (Tier-2 only) can proceed in parallel, but Task 13's Tier-1 rows and the report's Axis-2 verdict block on it.

---

### Task 12: Sanity run — clean LibriSpeech vs published WER (harness gate)

**No new files** — this is an execution task. The harness is broken and no downstream number counts if this gate fails (spec Phase 2).

- [ ] **Step 1: Write the sanity spec** to `bench-corpus/sanity-spec.json`:

```json
{
  "wavs": ["bench-corpus/librispeech/clean-concat-000.wav",
           "bench-corpus/librispeech/clean-concat-001.wav"],
  "refsDir": "bench-corpus/refs",
  "engines": ["whisper", "parakeet"],
  "whisperModel": "small.en",
  "parakeetDir": "models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
  "threads": [0],
  "modes": ["batch"],
  "cpuRatePct": null,
  "outDir": "bench-corpus/results/sanity"
}
```

(`threads: [0]` = unconstrained; whisper maps 0 to `CpuThreads = null` = production auto.)

- [ ] **Step 2: Run**

Run: `dotnet build LocalScribe.slnx && dotnet run --project src/LocalScribe.AsrBench -- matrix --spec bench-corpus/sanity-spec.json`
Expected: completes without failures.log; `bench-corpus/results/sanity/summary.md` has aggregate WER lines for both engines.

- [ ] **Step 3: Judge the gate**

Published reference points (test-other, full set, official normalizers): whisper `small.en` around 7.5%; Parakeet TDT v3 English around 3-4%. Our numbers use a 600-utterance subset, VAD re-segmentation, concatenated scoring, and LocalScribe's normalizer — allow generous slack: **each engine within ±3 absolute points of its reference point passes.** Divergence beyond that (e.g. whisper at 20%) = harness bug (likely segmentation edge-cutting words, normalization mismatch, or slicing off-by-one); fix the harness, re-run, do NOT proceed on bad numbers.

- [ ] **Step 4: Record**

Append the sanity aggregates (numbers only — public audio) to a new `docs/spikes/2026-07-26-parakeet-spike-findings.md` scratch doc (grows through Tasks 13-14, feeds the final report):

```powershell
git add docs/spikes/2026-07-26-parakeet-spike-findings.md
git commit -m "docs(spike): sanity gate PASSED - harness numbers vs published WER recorded"
```

---

### Task 13: Full measurement matrix (Phase 2)

**No new files** — execution. Requires Task 11 complete (Tier-1 refs exist).

- [ ] **Step 1: Write `bench-corpus/matrix-spec.json`**

```json
{
  "wavs": ["bench-corpus/tier1/call1-remote-a.wav",
           "bench-corpus/tier1/call2-remote-a.wav",
           "bench-corpus/librispeech/phone-concat-000.wav",
           "bench-corpus/librispeech/phone-concat-001.wav",
           "bench-corpus/librispeech/phone-concat-002.wav"],
  "refsDir": "bench-corpus/refs",
  "engines": ["whisper", "parakeet"],
  "whisperModel": "small.en",
  "parakeetDir": "models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
  "threads": [2, 4],
  "modes": ["live", "batch"],
  "cpuRatePct": null,
  "outDir": "bench-corpus/results/matrix-uncapped"
}
```

(Adjust tier1 wav names to what Task 11 actually produced — list `bench-corpus/tier1/`.)

- [ ] **Step 2: Run the uncapped matrix**

Run: `dotnet run --project src/LocalScribe.AsrBench -- matrix --spec bench-corpus/matrix-spec.json`
Expected: 5 wavs x 2 engines x 2 threads x 2 modes = 40 configs, sequential; live-mode configs take ~audio duration each, so expect this to run for a few hours. Check `failures.log` — any entry is an Axis-1 finding, not a retry candidate.

- [ ] **Step 3: Run the cheap-laptop variant**

Copy the spec to `matrix-capped-spec.json`, change `"cpuRatePct": 50`, `"threads": [2]`, `"modes": ["live"]`, `"outDir": ".../matrix-capped"`. Run it. **Verify the cap is real:** while running, Task Manager should show the AsrBench + ParakeetLane process group pinned near 50%.

- [ ] **Step 4: Record aggregates**

Copy both `summary.md` tables into `docs/spikes/2026-07-26-parakeet-spike-findings.md` — **strip the per-wav rows for tier1 files down to numbers; never paste transcript text** (privileged). Commit:

```powershell
git add docs/spikes/2026-07-26-parakeet-spike-findings.md
git commit -m "docs(spike): phase-2 matrix aggregates (uncapped + 50pct-capped)"
```

---

### Task 14: Axis 3 — word-timestamp overlay assessment

**No new files beyond findings doc** — execution + written assessment.

- [ ] **Step 1: Produce a word-timing dump for a diarised Tier-1 excerpt**

Pick the Tier-1 excerpt whose parent session has diarisation (Split Speakers was run — check Session Details). Run:

```powershell
dotnet run --project src/LocalScribe.AsrBench -- run --engine parakeet --wav bench-corpus/tier1/call1-remote-a.wav --segments bench-corpus/tier1/call1-remote-a.wav.segments.jsonl --threads 4 --mode batch --out bench-corpus/results/axis3-results.jsonl --timestamps bench-corpus/results/axis3-words.jsonl
```

- [ ] **Step 2: Overlay against the session's diarisation turns**

The session's diarisation segments live in the session folder (the read view shows speaker turns with times). For 8-10 speaker-change boundaries in the excerpt window, tabulate (in the findings doc, times only — no transcript content): diarisation turn boundary ms vs the nearest Parakeet word boundary ms vs the current segment-level boundary the splits overlay would offer. The question per spec Axis 3: would word anchors materially improve split precision, diarisation alignment, and search anchors?

- [ ] **Step 3: Write the assessment** into the findings doc: a short section with the boundary table, plus 2-3 sentences each on splits overlay, diarisation alignment, and search anchors. Concrete examples by timestamp reference only. Commit:

```powershell
git add docs/spikes/2026-07-26-parakeet-spike-findings.md
git commit -m "docs(spike): axis-3 word-timestamp overlay assessment"
```

---

### Task 15: Spike report — verdict against the pre-committed bars

**Files:**
- Create: `docs/spikes/2026-07-26-parakeet-spike-report.md`

- [ ] **Step 1: Write the report** with exactly these sections (data from the findings doc; bars quoted verbatim from spec section 3 — they were fixed before measurement and may not be moved now):

1. **Verdict** — adopt / stop, one paragraph.
2. **Axis 1 latency** — the summary tables; explicit pass/fail against: whisper-struggles bar (p95 > segment duration or <1.5x headroom at 4 threads) and parakeet-decisive bar (>=2x headroom AND real-time at 2 threads). Include the measured process-boundary overhead (LatencyMs - DecodeMs p50) and model load time.
3. **Axis 2 WER** — real-session aggregate + phone-corpus aggregate per engine; pass/fail against the >1-point disqualifying regression and >=3-point decisive-win bars.
4. **Axis 3 word timestamps** — the assessment summary.
5. **Stability** — failures.log contents from all runs (empty = state that).
6. **Recommendation rule applied** — at least one decisive win AND no disqualifier? Show the logic.
7. **Costed adoption estimate** (spec section 6 priced into tasks) — only if the verdict is adopt; otherwise one line: "not costed - verdict is stop".
8. **Reproduce** — the exact commands (fetch-parakeet, prep-librispeech, matrix specs).

- [ ] **Step 2: Commit, merge, close out**

```powershell
git add docs/spikes/2026-07-26-parakeet-spike-report.md
git commit -m "docs(spike): parakeet spike report - verdict + evidence"
git checkout master
git merge --no-ff spike/parakeet-asr-bench -m "Merge spike/parakeet-asr-bench - AsrBench harness + parakeet spike verdict"
```

Then run the full gate on master: `dotnet build LocalScribe.slnx && dotnet test tests/LocalScribe.Core.Tests && dotnet test tests/LocalScribe.App.Tests && dotnet test tests/LocalScribe.Mcp.Tests && dotnet test tests/LocalScribe.AsrBench.Tests` — all green (Core has 2 known-env failures per memory; unchanged counts are the bar). The session's close-out (memory update: verdict into `parakeet-onnx-cpu-spike-stub` successor) happens outside this plan.

---

## Self-Review (performed while writing)

- **Spec coverage:** decision framework (T15 bars), harness (T1-T9), corpus both tiers (T10-T11), sanity gate (T12), matrix + capped configs (T13), Axis 3 (T14), report + stop-path merge (T15), Phase-0 gate (T2). ORT isolation constraint carried as a per-task hard rule; spec amended 2026-07-26 for the always-out-of-process lane.
- **Placeholders:** the fetch script's `PINNED-IN-STEP-3` is intentional and consumed by an explicit step in the same task; sherpa API sketch is flagged as empirically-confirmed-in-Task-2 per the Diarizer precedent.
- **Type consistency:** `SegmentRow`/`BenchRow`/`BenchJson` defined T3/T4, consumed T5/T7/T8/T9 with matching shapes; `ParakeetDriver.RunAsync` signature fixed in T5's placeholder and honoured in T7 (optional `timestampsOut` parameter added with a default — call sites from T5 remain valid); wire records duplicated in T7 by design (documented).
