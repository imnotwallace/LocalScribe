# Stage 1: Capture Spike — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove LocalScribe can simultaneously capture the microphone (Local) and the per-process loopback of a specific meeting app — **Webex (`CiscoCollabHost.exe`) first** — and write each to its own clean, **time-aligned** 16 kHz-mono WAV, de-risking the one genuine unknown before anything is built on top.

**Architecture:** A `LocalScribe.Core` library exposes an `ICaptureSource` abstraction with two real implementations — `MicCaptureSource` (NAudio WASAPI capture) and `ProcessLoopbackCapture` (CsWin32 `ActivateAudioInterfaceAsync` with `PROCESS_LOOPBACK`). Pure DSP/IO helpers (PCM conversion, resampling, WAV writing, **silence-gap filling**) sit behind the interface and are unit-tested with zero hardware (Humble Object pattern). A `SpikeRunner` console app wires both sources to two `WavSink`s for manual verification against a real call. The Remote PID is resolved from the **active render audio session** (by process image), then per-process loopback is activated on that PID directly with `INCLUDE_TARGET_PROCESS_TREE`.

**Tech Stack:** .NET 10 LTS (`net10.0-windows`), NAudio 2.2.x, Microsoft.Windows.CsWin32 (source-generated P/Invoke), xUnit.

This plan supersedes the pre-brainstorm draft. The validated design and rationale live in `docs/plans/2026-06-30-stage-1-capture-spike-decisions.md`; cross-cutting contracts in `docs/specs/localscribe-specs.md`.

## Global Constraints

These apply to **every** task; each task's requirements implicitly include them.

- **Target framework:** `net10.0-windows` for **all** projects (Core, SpikeRunner, Tests). Requires the **.NET 10 SDK** installed (`dotnet --version` >= 10.0.1xx).
- **Minimum OS at runtime:** Windows 10 build **20348+** (per-process loopback requirement). Dev/test box is Windows 11.
- **Canonical capture format:** **16000 Hz, mono, 16-bit PCM**. Every WAV the spike writes is this format.
- **Packages (pinned):** `NAudio` 2.2.1 (do **NOT** use NAudio 3.x preview); `Microsoft.Windows.CsWin32` latest 0.3.x; `xunit` (template default).
- **Spike output directory:** `%USERPROFILE%\LocalScribe\spike` (off the OneDrive-redirected `Documents`).
- **No Unicode emojis** anywhere in C# source or test code (project rule). Plain ASCII identifiers and strings.
- **Verification modes:** `[UNIT]` runs under `dotnet test` (deterministic, no hardware). `[SMOKE]` is hardware/interop, verified by running `SpikeRunner` against a real call — cannot run in CI.
- **Interop caveat:** Tasks 8–9 (CsWin32 + process loopback) were authored without a Windows compiler in the loop. The loopback code is a **faithful skeleton to adapt against Microsoft's `ApplicationLoopback` C++ sample** (`https://github.com/microsoft/Windows-classic-samples` -> `Samples/ApplicationLoopback`) and the **CsWin32-generated** type names — not guaranteed copy-paste-compile. Cross-reference the sample while implementing. NAudio 3 PR #1348 is a known-good C# reference for the same activation.
- **Commits:** Conventional commits (`feat:`, `test:`, `chore:`, `docs:`). One commit per task step that changes code, as marked. Every commit message ends with the project trailer:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`

## Prerequisites (confirm BEFORE Task 0)

- **.NET 10 SDK** installed. (Box currently has only 9.0.x — install before starting.)
- **Repeatable call rig:** Webex desktop app + a second device to join the same call on demand.
- **Headphones** for smoke tests (so remote voices do not bleed from speakers into the mic).
- Git `safe.directory` is set for the repo (already done for `F:/LocalScribe`).

## Project layout (created in Task 0)

```
LocalScribe.sln
src/
  LocalScribe.Core/           net10.0-windows  classlib  (capture + DSP)
  LocalScribe.SpikeRunner/    net10.0-windows  console   (manual smoke harness)
tests/
  LocalScribe.Core.Tests/     net10.0-windows  xUnit     (UNIT tasks)
docs/plans/                   (this file + the decisions doc)
```

---

## Task 0: Solution and project scaffold  [setup]

**Files:**
- Create: `LocalScribe.sln`, `src/LocalScribe.Core/LocalScribe.Core.csproj`, `src/LocalScribe.SpikeRunner/LocalScribe.SpikeRunner.csproj`, `tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj`, `.gitignore`

**Interfaces:**
- Produces: a building, empty solution with the three projects referenced and NAudio added to Core.

- [ ] **Step 1: Confirm the SDK**

Run: `dotnet --version`
Expected: `10.0.1xx` or higher. If it prints `9.x`, stop and install the .NET 10 SDK first.

- [ ] **Step 2: Create the .NET `.gitignore`**

Run: `dotnet new gitignore`
Expected: creates a standard .NET ignore (covers `bin/`, `obj/`, `.vs/`).

- [ ] **Step 3: Create solution and projects**

```bash
dotnet new sln -n LocalScribe
dotnet new classlib -o src/LocalScribe.Core -f net10.0-windows
dotnet new console  -o src/LocalScribe.SpikeRunner -f net10.0-windows
dotnet new xunit    -o tests/LocalScribe.Core.Tests -f net10.0-windows
dotnet sln add src/LocalScribe.Core src/LocalScribe.SpikeRunner tests/LocalScribe.Core.Tests
dotnet add src/LocalScribe.SpikeRunner reference src/LocalScribe.Core
dotnet add tests/LocalScribe.Core.Tests reference src/LocalScribe.Core
```

- [ ] **Step 4: Add NAudio to Core**

```bash
dotnet add src/LocalScribe.Core package NAudio --version 2.2.1
```

- [ ] **Step 5: Delete template stub files**

Remove `src/LocalScribe.Core/Class1.cs` and `tests/LocalScribe.Core.Tests/UnitTest1.cs`.

- [ ] **Step 6: Build and test baseline**

Run: `dotnet build`
Expected: build succeeds (all three projects, `net10.0-windows`).
Run: `dotnet test`
Expected: passes with 0 tests.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "chore: scaffold solution (Core, SpikeRunner, Tests) on net10.0-windows"
```

---

## Task 1: Core capture types  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Audio/SourceKind.cs`, `AudioFrame.cs`, `ICaptureSource.cs`, `IClock.cs`
- Test: `tests/LocalScribe.Core.Tests/ClockTests.cs`

**Interfaces:**
- Produces:
  - `enum SourceKind { Local, Remote }`
  - `readonly record struct AudioFrame(SourceKind Source, long StartMs, float[] Samples)`
  - `interface ICaptureSource : IDisposable { SourceKind Source { get; } event Action<AudioFrame>? FrameAvailable; void Start(); void Stop(); }`
  - `interface IClock { long ElapsedMs { get; } }`, `sealed class StopwatchClock : IClock`, `sealed class FakeClock : IClock { long ElapsedMs { get; set; } }`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/ClockTests.cs
using LocalScribe.Core.Audio;
using Xunit;

public class ClockTests
{
    [Fact]
    public void FakeClock_returns_configured_value_and_is_settable()
    {
        var clock = new FakeClock();
        Assert.Equal(0, clock.ElapsedMs);
        clock.ElapsedMs = 1500;
        Assert.Equal(1500, clock.ElapsedMs);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ClockTests`
Expected: FAIL (types not defined).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Audio/SourceKind.cs
namespace LocalScribe.Core.Audio;
public enum SourceKind { Local, Remote }
```
```csharp
// src/LocalScribe.Core/Audio/AudioFrame.cs
namespace LocalScribe.Core.Audio;
/// <summary>16 kHz mono float samples, stamped with session-relative start time.</summary>
public readonly record struct AudioFrame(SourceKind Source, long StartMs, float[] Samples);
```
```csharp
// src/LocalScribe.Core/Audio/ICaptureSource.cs
namespace LocalScribe.Core.Audio;
public interface ICaptureSource : IDisposable
{
    SourceKind Source { get; }
    event Action<AudioFrame>? FrameAvailable;
    void Start();
    void Stop();
}
```
```csharp
// src/LocalScribe.Core/Audio/IClock.cs
using System.Diagnostics;
namespace LocalScribe.Core.Audio;

public interface IClock { long ElapsedMs { get; } }

/// <summary>Production clock: monotonic ms since construction (QPC-backed via Stopwatch).</summary>
public sealed class StopwatchClock : IClock
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public long ElapsedMs => _sw.ElapsedMilliseconds;
}

/// <summary>Test double: caller sets the time.</summary>
public sealed class FakeClock : IClock { public long ElapsedMs { get; set; } }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ClockTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio tests/LocalScribe.Core.Tests/ClockTests.cs
git commit -m "feat: core capture types (SourceKind, AudioFrame, ICaptureSource, IClock)"
```

---

## Task 2: PcmConverter  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Audio/PcmConverter.cs`
- Test: `tests/LocalScribe.Core.Tests/PcmConverterTests.cs`

**Interfaces:**
- Produces (`static class PcmConverter`):
  - `float[] Int16BytesToFloat(ReadOnlySpan<byte> bytes)`
  - `float[] StereoToMono(ReadOnlySpan<float> interleaved)`
  - `byte[] FloatToInt16Bytes(ReadOnlySpan<float> samples)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/PcmConverterTests.cs
using LocalScribe.Core.Audio;
using Xunit;

public class PcmConverterTests
{
    [Fact]
    public void Int16BytesToFloat_maps_full_scale()
    {
        byte[] bytes = { 0x00, 0x00, 0xFF, 0x7F };   // 0x0000 -> 0.0 ; 0x7FFF -> ~+1.0 (LE)
        float[] f = PcmConverter.Int16BytesToFloat(bytes);
        Assert.Equal(2, f.Length);
        Assert.Equal(0f, f[0], 5);
        Assert.True(f[1] > 0.99f && f[1] <= 1.0f);
    }

    [Fact]
    public void StereoToMono_averages_channels()
    {
        float[] interleaved = { 1.0f, 0.0f,  0.0f, 1.0f };   // L,R, L,R
        float[] mono = PcmConverter.StereoToMono(interleaved);
        Assert.Equal(new[] { 0.5f, 0.5f }, mono);
    }

    [Fact]
    public void FloatToInt16Bytes_roundtrips_within_tolerance()
    {
        float[] original = { 0f, 0.5f, -0.5f, 0.999f };
        byte[] bytes = PcmConverter.FloatToInt16Bytes(original);
        float[] back = PcmConverter.Int16BytesToFloat(bytes);
        for (int i = 0; i < original.Length; i++)
            Assert.Equal(original[i], back[i], 3);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter PcmConverterTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Audio/PcmConverter.cs
using System.Buffers.Binary;
namespace LocalScribe.Core.Audio;

public static class PcmConverter
{
    public static float[] Int16BytesToFloat(ReadOnlySpan<byte> bytes)
    {
        int n = bytes.Length / 2;
        var outp = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2));
            outp[i] = s / 32768f;
        }
        return outp;
    }

    public static float[] StereoToMono(ReadOnlySpan<float> interleaved)
    {
        int n = interleaved.Length / 2;
        var outp = new float[n];
        for (int i = 0; i < n; i++)
            outp[i] = (interleaved[i * 2] + interleaved[i * 2 + 1]) * 0.5f;
        return outp;
    }

    public static byte[] FloatToInt16Bytes(ReadOnlySpan<float> samples)
    {
        var outp = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            short s = (short)Math.Round(clamped * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(outp.AsSpan(i * 2, 2), s);
        }
        return outp;
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter PcmConverterTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio/PcmConverter.cs tests/LocalScribe.Core.Tests/PcmConverterTests.cs
git commit -m "feat: PCM int16/float conversion and stereo->mono downmix"
```

---

## Task 3: WavSink (16 kHz mono writer)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Audio/WavSink.cs`
- Test: `tests/LocalScribe.Core.Tests/WavSinkTests.cs`

**Interfaces:**
- Consumes: `PcmConverter.FloatToInt16Bytes`.
- Produces (`sealed class WavSink : IDisposable`): `const int SampleRate = 16000`; `WavSink(string path)`; `void Write(ReadOnlySpan<float> mono16k)`; `void Dispose()`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/WavSinkTests.cs
using LocalScribe.Core.Audio;
using NAudio.Wave;
using Xunit;

public class WavSinkTests
{
    [Fact]
    public void Writes_16k_mono_pcm_and_roundtrips_samples()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}.wav");
        float[] samples = { 0f, 0.25f, -0.25f, 0.5f, -0.5f };
        try
        {
            using (var sink = new WavSink(path)) sink.Write(samples);

            using var reader = new AudioFileReader(path);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);

            var buf = new float[samples.Length];
            int read = reader.Read(buf, 0, buf.Length);
            Assert.Equal(samples.Length, read);
            for (int i = 0; i < samples.Length; i++)
                Assert.Equal(samples[i], buf[i], 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter WavSinkTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Audio/WavSink.cs
using NAudio.Wave;
namespace LocalScribe.Core.Audio;

/// <summary>Append-only 16 kHz mono 16-bit PCM WAV writer.</summary>
public sealed class WavSink : IDisposable
{
    public const int SampleRate = 16000;
    private readonly WaveFileWriter _writer;

    public WavSink(string path)
        => _writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));

    public void Write(ReadOnlySpan<float> mono16k)
    {
        byte[] bytes = PcmConverter.FloatToInt16Bytes(mono16k);
        _writer.Write(bytes, 0, bytes.Length);
    }

    public void Dispose() => _writer.Dispose();
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter WavSinkTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio/WavSink.cs tests/LocalScribe.Core.Tests/WavSinkTests.cs
git commit -m "feat: WavSink writes 16kHz mono PCM WAV"
```

---

## Task 4: MonoResampler16k  [UNIT]

Used only by the **mic** path (the loopback path requests 16 kHz directly in Task 9 via `AUTOCONVERTPCM`).

**Files:**
- Create: `src/LocalScribe.Core/Audio/MonoResampler16k.cs`
- Test: `tests/LocalScribe.Core.Tests/MonoResampler16kTests.cs`

**Interfaces:**
- Consumes: `WavSink.SampleRate`.
- Produces (`sealed class MonoResampler16k`): `MonoResampler16k(int inputSampleRate)`; `float[] Process(ReadOnlySpan<float> monoInput)`.

- [ ] **Step 1: Write the failing test** (length-ratio is the deterministic property)

```csharp
// tests/LocalScribe.Core.Tests/MonoResampler16kTests.cs
using LocalScribe.Core.Audio;
using Xunit;

public class MonoResampler16kTests
{
    [Fact]
    public void Downsamples_48k_to_16k_by_one_third_length()
    {
        var r = new MonoResampler16k(inputSampleRate: 48000);
        var input = new float[48000];            // 1 second @ 48k
        for (int i = 0; i < input.Length; i++)   // 440 Hz sine, harmless content
            input[i] = MathF.Sin(2 * MathF.PI * 440 * i / 48000f) * 0.5f;

        float[] outp = r.Process(input);

        Assert.InRange(outp.Length, 15840, 16160);   // ~16000 (+/-1% for filter edge effects)
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter MonoResampler16kTests` -> FAIL.

- [ ] **Step 3: Implement** (NAudio managed WDL resampler; pure, cross-platform)

```csharp
// src/LocalScribe.Core/Audio/MonoResampler16k.cs
using NAudio.Dsp;
namespace LocalScribe.Core.Audio;

/// <summary>Resamples mono float input at an arbitrary rate to 16 kHz mono.</summary>
public sealed class MonoResampler16k
{
    private readonly WdlResampler _resampler = new();
    private readonly int _inputRate;

    public MonoResampler16k(int inputSampleRate)
    {
        _inputRate = inputSampleRate;
        _resampler.SetMode(true, 2, false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(true);                      // input-driven
        _resampler.SetRates(_inputRate, WavSink.SampleRate);
    }

    public float[] Process(ReadOnlySpan<float> monoInput)
    {
        int needed = _resampler.ResamplePrepare(monoInput.Length, 1,
            out float[] inBuf, out int inOffset);
        int toCopy = Math.Min(needed, monoInput.Length);
        for (int i = 0; i < toCopy; i++) inBuf[inOffset + i] = monoInput[i];

        var outBuf = new float[(int)(monoInput.Length *
            ((double)WavSink.SampleRate / _inputRate) + 16)];   // generous output buffer
        int written = _resampler.ResampleOut(outBuf, 0, toCopy, outBuf.Length, 1);

        var result = new float[written];
        Array.Copy(outBuf, result, written);
        return result;
    }
}
```

> **Note:** WDL API names vary slightly across NAudio versions. If `Process` returns 0 on the first call (filter priming), the 1-second test block is well past priming. If the length assertion is off due to priming, widen `InRange` to `[15000, 16500]` and add a code comment explaining the filter-edge effect — do **not** loosen further.

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter MonoResampler16kTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio/MonoResampler16k.cs tests/LocalScribe.Core.Tests/MonoResampler16kTests.cs
git commit -m "feat: MonoResampler16k (arbitrary rate -> 16kHz mono)"
```

---

## Task 5: FakeCaptureSource + deterministic pipeline test  [UNIT]

Proves the whole seam (source -> sink -> WAV) with zero hardware.

**Files:**
- Create: `src/LocalScribe.Core/Audio/FakeCaptureSource.cs`
- Test: `tests/LocalScribe.Core.Tests/CapturePipelineTests.cs`

**Interfaces:**
- Consumes: `ICaptureSource`, `AudioFrame`, `WavSink`.
- Produces (`sealed class FakeCaptureSource : ICaptureSource`): `FakeCaptureSource(SourceKind source, float[][] framesOf)`; emits each frame synchronously on `Start()`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/CapturePipelineTests.cs
using LocalScribe.Core.Audio;
using NAudio.Wave;
using Xunit;

public class CapturePipelineTests
{
    [Fact]
    public void Fake_source_frames_flow_through_sink_to_a_readable_wav()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}.wav");
        var source = new FakeCaptureSource(SourceKind.Remote, framesOf:
            new[] { new float[] { 0.1f, 0.2f }, new float[] { -0.1f, -0.2f } });
        try
        {
            using (var sink = new WavSink(path))
            {
                source.FrameAvailable += f => sink.Write(f.Samples);
                source.Start();          // FakeCaptureSource emits synchronously
                source.Stop();
            }

            using var reader = new AudioFileReader(path);
            var buf = new float[4];
            int read = reader.Read(buf, 0, buf.Length);
            Assert.Equal(4, read);       // 2 frames x 2 samples
            Assert.Equal(0.1f, buf[0], 3);
            Assert.Equal(-0.2f, buf[3], 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter CapturePipelineTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Audio/FakeCaptureSource.cs
namespace LocalScribe.Core.Audio;

/// <summary>Test double: synchronously replays preset frames on Start().</summary>
public sealed class FakeCaptureSource : ICaptureSource
{
    private readonly float[][] _frames;
    private long _t;
    public SourceKind Source { get; }
    public event Action<AudioFrame>? FrameAvailable;

    public FakeCaptureSource(SourceKind source, float[][] framesOf)
        => (Source, _frames) = (source, framesOf);

    public void Start()
    {
        foreach (var f in _frames)
        {
            FrameAvailable?.Invoke(new AudioFrame(Source, _t, f));
            _t += (long)(1000.0 * f.Length / WavSink.SampleRate);
        }
    }

    public void Stop() { }
    public void Dispose() { }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter CapturePipelineTests` -> PASS.

- [ ] **Step 5: Run the full unit suite and commit**

Run: `dotnet test`
Expected: all tests PASS (Tasks 1–5).

```bash
git add src/LocalScribe.Core/Audio/FakeCaptureSource.cs tests/LocalScribe.Core.Tests/CapturePipelineTests.cs
git commit -m "test: end-to-end fake-source -> sink -> WAV pipeline"
```

---

## Task 6: SilenceGapFiller (stream time-alignment)  [UNIT]

**Why this exists:** per-process loopback delivers **no buffers while the target app is silent** (not silent-flagged frames — *no packets at all*). If we just append received audio, `remote.wav` runs **shorter** than the always-on `local.wav` and the two streams drift out of sample-alignment. The loopback pump (Task 9) reads the device sample position on each packet and uses this pure helper to compute how much silence to insert so the Remote stream stays continuous on its own device timeline.

**Files:**
- Create: `src/LocalScribe.Core/Audio/SilenceGapFiller.cs`
- Test: `tests/LocalScribe.Core.Tests/SilenceGapFillerTests.cs`

**Interfaces:**
- Produces (`static class SilenceGapFiller`):
  - `long SilenceFramesBefore(long writtenFrames, long devicePosFrames)` — frames of silence to insert before the new packet so the running written-frame count reaches the device-reported position. Clamped to `>= 0` (jitter/overlap never produces negative silence). `writtenFrames` and `devicePosFrames` are both measured from the stream's start anchor (the first packet's device position).
  - `float[] SilenceFrame(long frames)` — a zero-filled mono buffer of `frames` samples (helper for the pump). Returns `Array.Empty<float>()` for `frames <= 0`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/SilenceGapFillerTests.cs
using LocalScribe.Core.Audio;
using Xunit;

public class SilenceGapFillerTests
{
    [Fact]
    public void No_gap_when_device_position_matches_written()
    {
        Assert.Equal(0, SilenceGapFiller.SilenceFramesBefore(writtenFrames: 16000, devicePosFrames: 16000));
    }

    [Fact]
    public void Gap_is_device_position_minus_written()
    {
        // Target went silent for 0.5 s (8000 frames @ 16 kHz): device advanced, we did not write.
        Assert.Equal(8000, SilenceGapFiller.SilenceFramesBefore(writtenFrames: 16000, devicePosFrames: 24000));
    }

    [Fact]
    public void Negative_drift_is_clamped_to_zero()
    {
        // Device position behind written count (jitter/overlap) -> never insert negative silence.
        Assert.Equal(0, SilenceGapFiller.SilenceFramesBefore(writtenFrames: 24000, devicePosFrames: 16000));
    }

    [Fact]
    public void SilenceFrame_returns_zeros_of_requested_length()
    {
        float[] s = SilenceGapFiller.SilenceFrame(3);
        Assert.Equal(3, s.Length);
        Assert.All(s, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void SilenceFrame_of_nonpositive_length_is_empty()
    {
        Assert.Empty(SilenceGapFiller.SilenceFrame(0));
        Assert.Empty(SilenceGapFiller.SilenceFrame(-5));
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter SilenceGapFillerTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Audio/SilenceGapFiller.cs
namespace LocalScribe.Core.Audio;

/// <summary>
/// Pure time-alignment math for a gappy capture stream (per-process loopback).
/// The device reports a monotonically advancing sample position even across silence;
/// we insert exactly the missing frames so the written stream tracks that timeline.
/// </summary>
public static class SilenceGapFiller
{
    /// <summary>Silence frames to insert before a packet whose device position is
    /// <paramref name="devicePosFrames"/>, given we have written <paramref name="writtenFrames"/>
    /// frames so far. Both are measured from the stream's start anchor. Clamped to >= 0.</summary>
    public static long SilenceFramesBefore(long writtenFrames, long devicePosFrames)
        => Math.Max(0, devicePosFrames - writtenFrames);

    /// <summary>A zero-filled mono buffer of <paramref name="frames"/> samples (empty if &lt;= 0).</summary>
    public static float[] SilenceFrame(long frames)
        => frames <= 0 ? Array.Empty<float>() : new float[frames];
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter SilenceGapFillerTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio/SilenceGapFiller.cs tests/LocalScribe.Core.Tests/SilenceGapFillerTests.cs
git commit -m "feat: SilenceGapFiller for per-process loopback time-alignment"
```

---

## Task 7: MicCaptureSource (WASAPI mic)  [SMOKE]

**Files:**
- Create: `src/LocalScribe.Core/Audio/MicCaptureSource.cs`

No unit test (real device). Verified by the SpikeRunner in Task 10.

**Interfaces:**
- Consumes: `IClock`, `MonoResampler16k`, `PcmConverter.StereoToMono`, `AudioFrame`, `ICaptureSource`.
- Produces (`sealed class MicCaptureSource : ICaptureSource`): `MicCaptureSource(IClock clock)`; `Source => SourceKind.Local`; emits 16 kHz mono `AudioFrame`s stamped on the clock.

- [ ] **Step 1: Implement**

```csharp
// src/LocalScribe.Core/Audio/MicCaptureSource.cs
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace LocalScribe.Core.Audio;

/// <summary>Captures the default communications mic, downmixes + resamples to
/// 16 kHz mono, emits AudioFrames stamped on the session clock.</summary>
public sealed class MicCaptureSource : ICaptureSource
{
    private readonly IClock _clock;
    private readonly WasapiCapture _capture;
    private readonly MonoResampler16k _resampler;
    private readonly int _channels;

    public SourceKind Source => SourceKind.Local;
    public event Action<AudioFrame>? FrameAvailable;

    public MicCaptureSource(IClock clock)
    {
        _clock = clock;
        var device = new MMDeviceEnumerator()
            .GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        _capture = new WasapiCapture(device);             // device mix format
        _channels = _capture.WaveFormat.Channels;
        _resampler = new MonoResampler16k(_capture.WaveFormat.SampleRate);
        _capture.DataAvailable += OnData;
    }

    private void OnData(object? _, WaveInEventArgs e)
    {
        // WASAPI mix format is typically 32-bit float interleaved.
        int floatCount = e.BytesRecorded / 4;
        var interleaved = new float[floatCount];
        Buffer.BlockCopy(e.Buffer, 0, interleaved, 0, e.BytesRecorded);

        float[] mono = _channels == 1
            ? interleaved
            : PcmConverter.StereoToMono(interleaved);     // assumes 2ch; see note

        float[] mono16k = _resampler.Process(mono);
        if (mono16k.Length > 0)
            FrameAvailable?.Invoke(new AudioFrame(Source, _clock.ElapsedMs, mono16k));
    }

    public void Start() => _capture.StartRecording();
    public void Stop()  => _capture.StopRecording();
    public void Dispose() { _capture.DataAvailable -= OnData; _capture.Dispose(); }
}
```

> **Notes for the implementer:**
> - If the mix format is **not** 32-bit float (rare), branch on `_capture.WaveFormat.Encoding`/`BitsPerSample` and use `PcmConverter.Int16BytesToFloat`.
> - For >2 channels, generalise `StereoToMono` to average all channels (YAGNI for the spike unless your mic enumerates >2ch — note it in code if you hit it).
> - The mic stream is assumed **continuous** (WASAPI capture delivers gaplessly while recording), so no `SilenceGapFiller` on this path — only the loopback path (Task 9) has real gaps.

- [ ] **Step 2: Build** — `dotnet build` -> Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/LocalScribe.Core/Audio/MicCaptureSource.cs
git commit -m "feat: MicCaptureSource (WASAPI mic -> 16kHz mono frames)"
```

---

## Task 8: CsWin32 setup for process loopback  [build-verify]

**Files:**
- Create: `src/LocalScribe.Core/NativeMethods.txt`
- Modify: `src/LocalScribe.Core/LocalScribe.Core.csproj`

**Interfaces:**
- Produces: the CsWin32-generated `Windows.Win32.*` P/Invoke surface used by Task 9.

- [ ] **Step 1: Add CsWin32**

```bash
dotnet add src/LocalScribe.Core package Microsoft.Windows.CsWin32 --version 0.3.183
```
(Use the latest 0.3.x.) In `LocalScribe.Core.csproj`, ensure inside a `<PropertyGroup>`:
```xml
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
<LangVersion>latest</LangVersion>
```

- [ ] **Step 2: List the native surface**

```text
// src/LocalScribe.Core/NativeMethods.txt
ActivateAudioInterfaceAsync
IActivateAudioInterfaceCompletionHandler
IActivateAudioInterfaceAsyncOperation
IAudioClient
IAudioCaptureClient
AUDIOCLIENT_ACTIVATION_PARAMS
AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
AUDIOCLIENT_ACTIVATION_TYPE
PROCESS_LOOPBACK_MODE
WAVEFORMATEX
AUDCLNT_SHAREMODE
AUDCLNT_STREAMFLAGS_LOOPBACK
AUDCLNT_STREAMFLAGS_EVENTCALLBACK
AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
PROPVARIANT
```

- [ ] **Step 3: Build to verify generation**

Run: `dotnet build src/LocalScribe.Core`
Expected: succeeds; CsWin32 generates the `Windows.Win32.*` surface.

> Expected friction (record any renames in code comments): some symbols live under `Windows.Win32.Media.Audio`; `PROPVARIANT` may be under `Windows.Win32.System.Com.StructuredStorage`; some `AUDCLNT_STREAMFLAGS_*` are `const` values rather than enum members. If a symbol name is rejected, consult the CsWin32 build output / `https://github.com/microsoft/CsWin32` and adjust. For version stability you MAY instead hand-declare the two tiny COM interfaces (`IActivateAudioInterfaceCompletionHandler`, `IActivateAudioInterfaceAsyncOperation`) and the `Mmdevapi.dll` `ActivateAudioInterfaceAsync` P/Invoke, keeping CsWin32 for `IAudioClient`/structs/constants.

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.Core/NativeMethods.txt src/LocalScribe.Core/LocalScribe.Core.csproj
git commit -m "chore: add CsWin32 + native surface for process loopback"
```

---

## Task 9: ProcessLoopbackCapture (the crux)  [SMOKE — highest risk]

**Files:**
- Create: `src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs`

> **This is the unverified-on-Linux interop.** Implement it against the **ApplicationLoopback** C++ sample and the **CsWin32-generated** names from Task 8 (and NAudio 3 PR #1348 as a C# reference). The skeleton below shows the LocalScribe seam, the concrete activation facts, and the silence-gap wiring; you will adjust types to the generated names. The `targetPid` is the active render-session PID resolved in Task 10.

**Interfaces:**
- Consumes: `IClock`, `AudioFrame`, `ICaptureSource`, `SilenceGapFiller`, the Task 8 native surface.
- Produces (`sealed class ProcessLoopbackCapture : ICaptureSource`): `ProcessLoopbackCapture(uint targetPid, IClock clock)`; `static ProcessLoopbackCapture SystemLoopbackExcludingSelf(IClock clock)`; `Source => SourceKind.Remote`; emits 16 kHz mono `AudioFrame`s, **silence-filled across gaps** so the stream is continuous on its device timeline.

**Concrete facts to encode (from the design + research):**
- Activate the magic device string **`VAD\Process_Loopback`** via `ActivateAudioInterfaceAsync`, passing `AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK, ProcessLoopbackParams = { TargetProcessId = targetPid, ProcessLoopbackMode = INCLUDE_TARGET_PROCESS_TREE } }`, wrapped in a `PROPVARIANT` **BLOB** (the one fragile marshalling spot). Plan B uses `EXCLUDE_TARGET_PROCESS_TREE` with `TargetProcessId = (uint)Environment.ProcessId`.
- `ActivateAudioInterfaceAsync` is **asynchronous**: implement `IActivateAudioInterfaceCompletionHandler.ActivateCompleted`, then call `GetActivateResult` to obtain the `IAudioClient`. The callback runs on an **MTA worker thread** — model init as `await` over a `TaskCompletionSource`; keep the handler object rooted.
- **Do NOT call `GetMixFormat`/`IsFormatSupported`** on the loopback client (returns `E_NOTIMPL`). Hand-build a 16 kHz/16-bit/mono `WAVEFORMATEX` and `Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM, hnsBufferDuration, 0, &fmt16kMono, null)`.
- Get `IAudioCaptureClient` via `GetService`; `SetEventHandle(bufferReady)` **before** `Start()`; pump on a background thread: wait on the event, then drain with `GetNextPacketSize`/`GetBuffer`/`ReleaseBuffer`. On `AUDCLNT_E_RESOURCES_INVALIDATED`, re-activate. **Read the device position on each `GetBuffer`** (`pu64DevicePosition`) and use `SilenceGapFiller` to insert silence for gaps.

- [ ] **Step 1: Implement the activation + completion handler + gap-aware pump**

```csharp
// src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs
// SKELETON - adapt COM/struct types to CsWin32 output and the ApplicationLoopback sample.
using System.Threading;
// using Windows.Win32; using Windows.Win32.Media.Audio;  // from CsWin32
namespace LocalScribe.Core.Audio;

public sealed class ProcessLoopbackCapture : ICaptureSource
{
    private const int SampleRate = WavSink.SampleRate;   // 16000
    private readonly uint _targetPid;
    private readonly bool _excludeMode;                  // false = INCLUDE target; true = EXCLUDE self (Plan B)
    private readonly IClock _clock;
    private readonly EventWaitHandle _bufferReady = new(false, EventResetMode.AutoReset);
    private Thread? _pump;
    private volatile bool _running;
    private long _anchorPos = -1;     // first packet's device position (frames)
    private long _writtenFrames;      // frames emitted so far (real + inserted silence)
    // private IAudioClient _client; private IAudioCaptureClient _capture;   // CsWin32 COM

    public SourceKind Source => SourceKind.Remote;
    public event Action<AudioFrame>? FrameAvailable;

    public ProcessLoopbackCapture(uint targetPid, IClock clock)
        => (_targetPid, _excludeMode, _clock) = (targetPid, false, clock);

    private ProcessLoopbackCapture(uint targetPid, bool excludeMode, IClock clock)
        => (_targetPid, _excludeMode, _clock) = (targetPid, excludeMode, clock);

    /// <summary>Plan B: full-system loopback minus LocalScribe's own process tree.</summary>
    public static ProcessLoopbackCapture SystemLoopbackExcludingSelf(IClock clock)
        => new((uint)Environment.ProcessId, excludeMode: true, clock);

    public void Start()
    {
        // 1. Build AUDIOCLIENT_ACTIVATION_PARAMS with _targetPid and
        //    (_excludeMode ? EXCLUDE_TARGET_PROCESS_TREE : INCLUDE_TARGET_PROCESS_TREE).
        // 2. Wrap in PROPVARIANT (BLOB).
        // 3. ActivateAudioInterfaceAsync("VAD\\Process_Loopback", IID_IAudioClient, &params, handler, out op).
        // 4. In ActivateCompleted (MTA worker): GetActivateResult -> _client; signal a TaskCompletionSource.
        //    Await it here so Start() returns only once the client is live (or throws on E_* HRESULT).
        // 5. Build fmt16kMono = WAVEFORMATEX { wFormatTag=WAVE_FORMAT_PCM, nChannels=1,
        //    nSamplesPerSec=16000, wBitsPerSample=16, nBlockAlign=2, nAvgBytesPerSec=32000 }.
        // 6. _client.Initialize(SHARED, LOOPBACK|EVENTCALLBACK|AUTOCONVERTPCM, hnsBuffer, 0, &fmt16kMono, null).
        // 7. _capture = _client.GetService(IID_IAudioCaptureClient).
        // 8. _client.SetEventHandle(_bufferReady.SafeWaitHandle); _client.Start().
        _running = true;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "ProcLoopbackPump" };
        _pump.Start();
    }

    private void PumpLoop()
    {
        while (_running)
        {
            _bufferReady.WaitOne(200);
            // while (_capture.GetNextPacketSize(out uint packetFrames) == 0 && packetFrames != 0) {
            //   _capture.GetBuffer(out IntPtr pData, out uint frames, out uint flags,
            //                      out ulong devicePos, out _);
            //   if (_anchorPos < 0) _anchorPos = (long)devicePos;       // anchor at first packet
            //   long pos = (long)devicePos - _anchorPos;
            //
            //   long silence = SilenceGapFiller.SilenceFramesBefore(_writtenFrames, pos);
            //   if (silence > 0) { Emit(SilenceGapFiller.SilenceFrame(silence)); _writtenFrames += silence; }
            //
            //   bool silentFlag = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            //   float[] pcm = silentFlag
            //       ? SilenceGapFiller.SilenceFrame(frames)              // honour SILENT flag
            //       : PcmConverter.Int16BytesToFloat(CopyBytes(pData, (int)frames * 2));
            //   Emit(pcm);
            //   _writtenFrames += frames;
            //   _capture.ReleaseBuffer(frames);
            // }
        }
    }

    private void Emit(float[] pcm)
    {
        if (pcm.Length > 0)
            FrameAvailable?.Invoke(new AudioFrame(Source, _clock.ElapsedMs, pcm));
    }

    public void Stop()
    {
        _running = false;
        _bufferReady.Set();
        _pump?.Join(500);
        // _client?.Stop();
    }

    public void Dispose() { Stop(); _bufferReady.Dispose(); /* release COM objects */ }
}
```

- [ ] **Step 2: Build** — `dotnet build` -> Expected: succeeds after type adjustments.

- [ ] **Step 3: Isolated activation smoke test (gate before the full pipeline)**

In `SpikeRunner` (temporary `--activate-only <pid>` path), confirm `ActivateCompleted` fires and `GetActivateResult` yields a non-null `IAudioClient` for a **running Webex `CiscoCollabHost.exe` PID that is actively playing call audio**. Log `"loopback activated for pid {pid}"`.
Expected: the log line appears; no `E_*` HRESULT.

> If activation fails with `AUDCLNT_E_*`: verify the target PID is actually rendering audio, the OS build is >= 20348, and the app is not elevated beyond your process. This gate must pass before Task 10's full run.

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs
git commit -m "feat: ProcessLoopbackCapture via ActivateAudioInterfaceAsync (process loopback + silence gap-fill)"
```

---

## Task 10: SpikeRunner — dual capture to two WAVs  [SMOKE]  (the de-risk gate)

**Files:**
- Modify: `src/LocalScribe.SpikeRunner/Program.cs`

**Interfaces:**
- Consumes: `MicCaptureSource`, `ProcessLoopbackCapture` (incl. `SystemLoopbackExcludingSelf`), `WavSink`, `StopwatchClock`, NAudio `MMDeviceEnumerator`/`AudioSessionManager`.
- Produces: `local.wav` + `remote.wav` in `%USERPROFILE%\LocalScribe\spike`; a `--system-loopback` Plan B mode; console duration output.

- [ ] **Step 1: Implement**

```csharp
// src/LocalScribe.SpikeRunner/Program.cs
using System.Diagnostics;
using NAudio.CoreAudioApi;
using LocalScribe.Core.Audio;

// Identify the meeting app by IMAGE NAME among the ACTIVE RENDER sessions - not by window title.
// Webex renders call audio in the CiscoCollabHost.exe media process (a different PID from the UI).
// "new Teams" (ms-teams.exe) currently returns silence for per-process loopback (known issue) - it
// is included last and expected to need Plan B. The active render audio session is the production
// source of truth (what IMeetingDetector keys on).
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
string[] appNames = positional.Length > 0
    ? positional
    : new[] { "CiscoCollabHost", "Webex", "Zoom", "ms-teams", "msedgewebview2", "Teams" };

bool systemLoopback = args.Contains("--system-loopback");   // Plan B

string outDir = Path.Combine(Environment.GetFolderPath(
    Environment.SpecialFolder.UserProfile), "LocalScribe", "spike");
Directory.CreateDirectory(outDir);

// Enumerate active render sessions on the default playback endpoint and pick the first whose owning
// process image matches a meeting app. Activate per-process loopback on THAT pid directly with
// INCLUDE_TARGET_PROCESS_TREE (captures any child media subprocess; minimises cross-app bleed).
var render = new MMDeviceEnumerator()
    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
var sessions = render.AudioSessionManager.Sessions;

uint renderPid = 0;
string renderImage = "";
for (int i = 0; i < sessions.Count; i++)
{
    var s = sessions[i];
    if (s.State != AudioSessionState.AudioSessionStateActive) continue;
    string image;
    try { image = Process.GetProcessById((int)s.GetProcessID).ProcessName; }
    catch { continue; }                               // session may have just exited
    if (appNames.Any(n => image.Contains(n, StringComparison.OrdinalIgnoreCase)))
    { renderPid = s.GetProcessID; renderImage = image; break; }
}
if (renderPid == 0) { Console.WriteLine("No active meeting render session found."); return; }
Console.WriteLine($"Target render session: pid {renderPid} ({renderImage}.exe)" +
                  (systemLoopback ? "  [Plan B: system loopback]" : ""));

var clock = new StopwatchClock();
using var mic  = new MicCaptureSource(clock);
// Default path: per-process INCLUDE on the render pid. Plan B: full-system loopback minus our own pid.
using ICaptureSource loop = systemLoopback
    ? ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock)
    : new ProcessLoopbackCapture(renderPid, clock);

using var localSink  = new WavSink(Path.Combine(outDir, "local.wav"));
using var remoteSink = new WavSink(Path.Combine(outDir, "remote.wav"));

long localSamples = 0, remoteSamples = 0;
mic.FrameAvailable  += f => { lock (localSink)  { localSink.Write(f.Samples);  localSamples  += f.Samples.Length; } };
loop.FrameAvailable += f => { lock (remoteSink) { remoteSink.Write(f.Samples); remoteSamples += f.Samples.Length; } };

mic.Start(); loop.Start();
Console.WriteLine("Recording both streams. Press ENTER to stop...");
Console.ReadLine();
mic.Stop(); loop.Stop();

Console.WriteLine($"local.wav : {localSamples  / 16000.0:F1}s ({localSamples} samples)");
Console.WriteLine($"remote.wav: {remoteSamples / 16000.0:F1}s ({remoteSamples} samples)");
Console.WriteLine($"Files in: {outDir}");
```

> **Optional ancestry confirmation:** if the matched render image is a generic host (`msedgewebview2`), you MAY walk parent PIDs (Toolhelp32 `th32ParentProcessID`) up to a known app-root allowlist to confirm membership, validating parent start-time (`Process.StartTime`) to defeat PID reuse. Not required for Webex (`CiscoCollabHost` is distinctive). Keep it in `Program.cs` as SMOKE.

- [ ] **Step 2: Build** — `dotnet build` -> Expected: succeeds.

- [ ] **Step 3: MANUAL VERIFICATION (the whole point of Stage 1)**

1. Join a **Webex** test meeting from your second device. **Wear headphones.**
2. Run with no app argument so it scans the default list (CiscoCollabHost/Webex/Zoom/ms-teams/...):
   `dotnet run --project src/LocalScribe.SpikeRunner`
3. Speak a few sentences yourself; have the other side speak distinctly.
4. **(Optional — feeds Stage-2 calibration, not a gate)** While recording, make one shared transient audible to **both** the mic and the Webex render (a single clap, or a beep played out loud) near the start and again near the end. Afterwards, measure the mic-vs-loopback offset between `local.wav` and `remote.wav`, and whether it drifts over a ~30 min call.
5. Press ENTER. Open `%USERPROFILE%\LocalScribe\spike\`.

**Hard gate — ALL must hold (go/no-go):**
- [ ] `local.wav` and `remote.wav` both exist, correct **16 kHz mono** format, non-trivial size, durations approx the recording length (after silence-fill).
- [ ] `local.wav` plays back **your voice only**, clear, no chop.
- [ ] `remote.wav` plays back **the remote participant(s) only**, clear, no chop.
- [ ] **Per-process activation succeeded against the real Webex `CiscoCollabHost.exe` render session** — not a system-loopback fallback, not a by-name PID.
- [ ] **Zero sustained dropouts** in either stream; console prints non-zero durations for both.

**Measured and recorded (NOT pass/fail — write the numbers into the decisions doc / a notes file for Stage-2 calibration):**
- [ ] Cross-bleed as a **dBFS** figure (from the clap/known-signal: remote energy leaking into `local.wav`).
- [ ] Inter-stream **drift in ms/min** over a 30+ min call (from the start/end clap offsets).

**Dropped from Stage 1:** the "no phantom-Local transcription line" check needs Whisper -> Stage 2.

**Plan B — explicit go/no-go at this gate:** if per-process loopback cannot meet the hard gate on Webex, run `dotnet run --project src/LocalScribe.SpikeRunner -- --system-loopback`, confirm the fallback captures cleanly (accepting other-app bleed), and **record this as a deliberate go/no-go decision** in the decisions doc — never a silent fallback.

**If `remote.wav` is empty/silent:** first confirm you targeted the **active render session** (`CiscoCollabHost.exe` for Webex) and activated on that PID with `INCLUDE_TARGET_PROCESS_TREE` — a by-name PID can be the wrong/silent process. Only then revisit Task 9 activation/format (the `Initialize` format, the event pump, or the device-position gap-fill). **If it has Spotify/notification audio mixed in:** you used a system-loopback fallback, not the per-process path — re-check the render-session PID selection.

- [ ] **Step 4: Retain golden corpus and commit**

Copy 2–3 good `local.wav`+`remote.wav` pairs to a labelled folder (e.g. `%USERPROFILE%\LocalScribe\spike\golden\webex-1\`) as the **Stage-2 golden corpus**. Do not commit large WAVs to git (they are ignored); note their location and the measured bleed/drift figures in the decisions doc.

```bash
git add src/LocalScribe.SpikeRunner/Program.cs
git commit -m "feat: SpikeRunner captures mic + per-process loopback to two WAVs (Webex-first, Plan B mode)"
```

---

## Stage 1 — Definition of Done

- [ ] `dotnet test` green (Tasks 1–6: clock, PCM, WAV, resampler, fake pipeline, **SilenceGapFiller**).
- [ ] `dotnet build` clean across all projects on `net10.0-windows`.
- [ ] **Task 10 hard gate passes on a real Webex call:** clean `local.wav` (you) + clean `remote.wav` (them), correct format, time-aligned (silence-filled), zero sustained dropouts, **per-process activation confirmed against `CiscoCollabHost.exe`** (not a system-loopback fallback).
- [ ] Cross-bleed (dBFS) and inter-stream drift (ms/min) **measured and recorded** for Stage-2 calibration.
- [ ] If the hard gate can't be met on Webex, the **Plan B** go/no-go (system loopback as v1 baseline) is recorded deliberately.
- [ ] **Golden corpus retained:** 2–3 Webex `local.wav`+`remote.wav` pairs kept and labelled for Stage 2.
- [ ] Notes captured for any CsWin32 symbol renames / format quirks (feeds Stage 2).

## Explicitly NOT in Stage 1 (YAGNI — later stages)

VAD segmentation, Whisper transcription, the merge/clock-interleave, JSONL/Markdown store, FLAC retention, meeting auto-detection, diarisation, settings, **the WPF tray shell (deferred to Stage 3)**, **browser-Webex capture (deferred — shared Chromium audio service can't isolate one tab)**, device hot-swap/sleep handling, packaging, and the consent/legal UX. Stage 1 proves *capture* and nothing else.
