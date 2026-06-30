# Stage 1: Capture Spike — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Prove LocalScribe can simultaneously capture the microphone (Local) and the
per-process loopback of a specific app like `Teams.exe` (Remote), and write each to its
own clean 16 kHz-mono WAV file — de-risking the one genuine unknown before anything is
built on top.

**Architecture:** A small `LocalScribe.Core` library exposes an `ICaptureSource`
abstraction with two real implementations — `MicCaptureSource` (NAudio WASAPI capture)
and `ProcessLoopbackCapture` (CsWin32 `ActivateAudioInterfaceAsync` with
`PROCESS_LOOPBACK`). Pure DSP/IO helpers (PCM conversion, resampling, WAV writing) sit
behind the interface and are unit-tested with zero hardware (Humble Object pattern). A
`SpikeRunner` console app wires both sources to two `WavSink`s for manual verification
against a real call.

**Tech Stack:** .NET 8 LTS (`net8.0-windows`; bump to a newer LTS if preferred — all
packages support 8+), NAudio 2.2.x, Microsoft.Windows.CsWin32 (source-generated
P/Invoke), xUnit.

---

## ⚠️ Verification environment (read first)

- **This plan executes on Windows 11.** The design and this plan were authored on Linux,
  where `net8.0-windows`, WASAPI, and CsWin32 **cannot be compiled or run**. Expect to
  fix minor compile issues, especially in the CsWin32 interop (Tasks 7–8).
- **Two verification modes, marked per task:**
  - **[UNIT]** — deterministic, runs under `dotnet test` (the bulk of the logic).
  - **[SMOKE]** — hardware/interop; verified by *running the SpikeRunner against a real
    call* and observing output. Cannot run in CI.
- **Per-process loopback (Task 8) is the highest-risk item.** The code given is a
  faithful skeleton to **adapt against Microsoft's canonical sample**, not
  copy-paste-and-it-compiles. Reference:
  `https://github.com/microsoft/Windows-classic-samples` →
  `Samples/ApplicationLoopback` (C++). Cross-reference it while implementing.

## Prerequisites

- Windows 11, .NET 8 SDK (`dotnet --version` ≥ 8.0).
- Microsoft Teams (or Zoom) installed, plus a second person / test meeting / an echo-bot
  to generate remote audio.
- **Headphones recommended** during smoke tests — on speakers, remote voices bleed from
  the speakers into the mic and muddy the "Local-is-only-me" check.
- Visual Studio 2022 or `dotnet` CLI + an editor.

## Project layout (created in Task 0)

```
LocalScribe.sln
src/
  LocalScribe.Core/           net8.0-windows  classlib  (capture + DSP)
  LocalScribe.SpikeRunner/    net8.0-windows  console   (manual smoke harness)
tests/
  LocalScribe.Core.Tests/     net8.0          xUnit     (UNIT tasks)
docs/plans/                   (this file)
```

## Commit message convention

Conventional commits: `feat:`, `test:`, `chore:`, `docs:`. One commit per task step that
changes code, as marked.

---

## Task 0: Solution & project scaffold  [setup]

**Files:**
- Create: `LocalScribe.sln`, `src/LocalScribe.Core/LocalScribe.Core.csproj`,
  `src/LocalScribe.SpikeRunner/LocalScribe.SpikeRunner.csproj`,
  `tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj`, `.gitignore`

**Step 1: Create the .NET `.gitignore`**

Run: `dotnet new gitignore` at repo root (creates a standard .NET ignore covering
`bin/`, `obj/`, `.vs/`).

**Step 2: Create solution and projects**

```bash
dotnet new sln -n LocalScribe
dotnet new classlib -o src/LocalScribe.Core -f net8.0-windows
dotnet new console  -o src/LocalScribe.SpikeRunner -f net8.0-windows
dotnet new xunit    -o tests/LocalScribe.Core.Tests -f net8.0
dotnet sln add src/LocalScribe.Core src/LocalScribe.SpikeRunner tests/LocalScribe.Core.Tests
dotnet add src/LocalScribe.SpikeRunner reference src/LocalScribe.Core
dotnet add tests/LocalScribe.Core.Tests reference src/LocalScribe.Core
```

**Step 3: Add NAudio to Core**

```bash
dotnet add src/LocalScribe.Core package NAudio --version 2.2.1
```

**Step 4: Delete template stub files**

Remove `src/LocalScribe.Core/Class1.cs` and `tests/LocalScribe.Core.Tests/UnitTest1.cs`.

**Step 5: Build & test baseline**

Run: `dotnet build` → Expected: build succeeds.
Run: `dotnet test` → Expected: passes with 0 tests.

**Step 6: Commit**

```bash
git add -A
git commit -m "chore: scaffold solution (Core, SpikeRunner, Tests)"
```

---

## Task 1: Core capture types  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Audio/SourceKind.cs`, `AudioFrame.cs`,
  `ICaptureSource.cs`, `IClock.cs`
- Test: `tests/LocalScribe.Core.Tests/ClockTests.cs`

**Step 1: Write the failing test**

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

**Step 2: Run test to verify it fails**

Run: `dotnet test --filter ClockTests` → Expected: FAIL (FakeClock/types not defined).

**Step 3: Write minimal implementation**

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

/// <summary>Production clock: monotonic ms since construction (session start).</summary>
public sealed class StopwatchClock : IClock
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public long ElapsedMs => _sw.ElapsedMilliseconds;
}

/// <summary>Test double: caller sets the time.</summary>
public sealed class FakeClock : IClock { public long ElapsedMs { get; set; } }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test --filter ClockTests` → Expected: PASS.

**Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio tests/LocalScribe.Core.Tests/ClockTests.cs
git commit -m "feat: core capture types (SourceKind, AudioFrame, ICaptureSource, IClock)"
```

---

## Task 2: PcmConverter  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Audio/PcmConverter.cs`
- Test: `tests/LocalScribe.Core.Tests/PcmConverterTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/PcmConverterTests.cs
using LocalScribe.Core.Audio;
using Xunit;

public class PcmConverterTests
{
    [Fact]
    public void Int16BytesToFloat_maps_full_scale()
    {
        // 0x0000 -> 0.0 ; 0x7FFF -> ~+1.0 (little-endian bytes)
        byte[] bytes = { 0x00, 0x00, 0xFF, 0x7F };
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
            Assert.Equal(original[i], back[i], 3);   // 3 decimal places
    }
}
```

**Step 2: Run to verify failure** — `dotnet test --filter PcmConverterTests` → FAIL.

**Step 3: Implement**

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

**Step 4: Run to verify pass** — `dotnet test --filter PcmConverterTests` → PASS.

**Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio/PcmConverter.cs tests/LocalScribe.Core.Tests/PcmConverterTests.cs
git commit -m "feat: PCM int16/float conversion and stereo->mono downmix"
```

---

## Task 3: WavSink (16 kHz mono writer)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Audio/WavSink.cs`
- Test: `tests/LocalScribe.Core.Tests/WavSinkTests.cs`

**Step 1: Write the failing test** (write floats, read back with NAudio, assert format + roundtrip)

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

**Step 2: Run to verify failure** — `dotnet test --filter WavSinkTests` → FAIL.

**Step 3: Implement**

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

**Step 4: Run to verify pass** — `dotnet test --filter WavSinkTests` → PASS.

**Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio/WavSink.cs tests/LocalScribe.Core.Tests/WavSinkTests.cs
git commit -m "feat: WavSink writes 16kHz mono PCM WAV"
```

---

## Task 4: MonoResampler16k  [UNIT]

Used only by the **mic** path (loopback can request 16 kHz directly in Task 8).

**Files:**
- Create: `src/LocalScribe.Core/Audio/MonoResampler16k.cs`
- Test: `tests/LocalScribe.Core.Tests/MonoResampler16kTests.cs`

**Step 1: Write the failing test** (length-ratio is the deterministic property)

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

        // ~16000 samples (±1% for filter edge effects)
        Assert.InRange(outp.Length, 15840, 16160);
    }
}
```

**Step 2: Run to verify failure** — `dotnet test --filter MonoResampler16kTests` → FAIL.

**Step 3: Implement** (NAudio's managed WDL resampler; pure, cross-platform)

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

        // generous output buffer
        var outBuf = new float[(int)(monoInput.Length *
            ((double)WavSink.SampleRate / _inputRate) + 16)];
        int written = _resampler.ResampleOut(outBuf, 0, toCopy, outBuf.Length, 1);

        var result = new float[written];
        Array.Copy(outBuf, result, written);
        return result;
    }
}
```

> **Note:** WDL resampler API names can differ slightly across NAudio versions. If
> `Process` returns 0 on the first call (filter priming), feed a second block in the
> smoke test — for the **unit** test, the 1-second block above is well past priming.

**Step 4: Run to verify pass** — `dotnet test --filter MonoResampler16kTests` → PASS.
If the length assertion is off due to priming, widen the `InRange` to `[15000, 16500]`
and add a code comment explaining the filter-edge effect (do **not** loosen further).

**Step 5: Commit**

```bash
git add src/LocalScribe.Core/Audio/MonoResampler16k.cs tests/LocalScribe.Core.Tests/MonoResampler16kTests.cs
git commit -m "feat: MonoResampler16k (arbitrary rate -> 16kHz mono)"
```

---

## Task 5: FakeCaptureSource + deterministic pipeline test  [UNIT]

Proves the **whole seam** (source → sink → WAV) with zero hardware. This is the safety
net that lets Stage 2 build confidently.

**Files:**
- Create: `src/LocalScribe.Core/Audio/FakeCaptureSource.cs`
- Test: `tests/LocalScribe.Core.Tests/CapturePipelineTests.cs`

**Step 1: Write the failing test**

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
            Assert.Equal(4, read);       // 2 frames × 2 samples
            Assert.Equal(0.1f, buf[0], 3);
            Assert.Equal(-0.2f, buf[3], 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

**Step 2: Run to verify failure** — `dotnet test --filter CapturePipelineTests` → FAIL.

**Step 3: Implement**

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

**Step 4: Run to verify pass** — `dotnet test --filter CapturePipelineTests` → PASS.

**Step 5: Run the full unit suite & commit**

Run: `dotnet test` → Expected: all tests PASS (Tasks 1–5).

```bash
git add src/LocalScribe.Core/Audio/FakeCaptureSource.cs tests/LocalScribe.Core.Tests/CapturePipelineTests.cs
git commit -m "test: end-to-end fake-source -> sink -> WAV pipeline"
```

---

## Task 6: MicCaptureSource (WASAPI mic)  [SMOKE]

**Files:**
- Create: `src/LocalScribe.Core/Audio/MicCaptureSource.cs`

No unit test (real device). Verified by the SpikeRunner in Task 9.

**Step 1: Implement**

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
> - If the mix format is **not** 32-bit float (rare), branch on
>   `_capture.WaveFormat.Encoding`/`BitsPerSample` and use `PcmConverter.Int16BytesToFloat`.
> - For >2 channels, generalise `StereoToMono` to average all channels (YAGNI for the
>   spike unless your mic enumerates >2ch — note it in code if you hit it).

**Step 2: Build** — `dotnet build` → Expected: succeeds.

**Step 3: Commit**

```bash
git add src/LocalScribe.Core/Audio/MicCaptureSource.cs
git commit -m "feat: MicCaptureSource (WASAPI mic -> 16kHz mono frames)"
```

---

## Task 7: CsWin32 setup for process loopback  [build-verify]

**Files:**
- Create: `src/LocalScribe.Core/NativeMethods.txt`
- Modify: `src/LocalScribe.Core/LocalScribe.Core.csproj`

**Step 1: Add CsWin32**

```bash
dotnet add src/LocalScribe.Core package Microsoft.Windows.CsWin32 --version 0.3.106
```
(Use the latest 0.3.x.) In the `.csproj`, ensure `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`
and `<LangVersion>latest</LangVersion>` inside a `<PropertyGroup>`.

**Step 2: List the native surface**

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
PROPVARIANT
```

**Step 3: Build to verify generation**

Run: `dotnet build src/LocalScribe.Core` → Expected: succeeds; CsWin32 generates the
`Windows.Win32.*` P/Invoke surface.

> If a symbol name is rejected, open the CsWin32-generated file list (build output) or
> consult `https://github.com/microsoft/CsWin32` and adjust the exact name. Some symbols
> live under `Windows.Win32.Media.Audio`. This is expected friction — note any renames.

**Step 4: Commit**

```bash
git add src/LocalScribe.Core/NativeMethods.txt src/LocalScribe.Core/LocalScribe.Core.csproj
git commit -m "chore: add CsWin32 + native surface for process loopback"
```

---

## Task 8: ProcessLoopbackCapture (the crux)  [SMOKE — highest risk]

**Files:**
- Create: `src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs`

> **This is the unverified-on-Linux interop.** Implement it against the **ApplicationLoopback**
> C++ sample (linked at top). The skeleton below shows the shape and the LocalScribe
> seam; you will adjust types to match the CsWin32-generated names from Task 7.

**Step 1: Implement the activation + completion handler + capture loop**

Key facts to encode:
- Activate the magic device string `VAD\Process_Loopback` via `ActivateAudioInterfaceAsync`,
  passing `AUDIOCLIENT_ACTIVATION_PARAMS` with
  `ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`,
  `ProcessLoopbackParams.TargetProcessId = pid`,
  `ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`,
  wrapped in a `PROPVARIANT` BLOB.
- `ActivateAudioInterfaceAsync` is **asynchronous**: implement
  `IActivateAudioInterfaceCompletionHandler.ActivateCompleted`, then call
  `GetActivateResult` to obtain the `IAudioClient`.
- `IAudioClient.Initialize(AUDCLNT_SHAREMODE_SHARED,
  AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, &format, null)`
  where **you provide** `format` — request **16 kHz, 16-bit, mono** to skip resampling on
  this path.
- Get `IAudioCaptureClient` via `GetService`; set the event handle via `SetEventHandle`;
  `Start()`; pump on a background thread: wait on the event → `GetBuffer` → copy →
  `ReleaseBuffer`, converting to float frames and emitting via `FrameAvailable`.

```csharp
// src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs
// SKELETON — adapt types to CsWin32 output and the ApplicationLoopback sample.
using System.Threading;
using LocalScribe.Core.Audio;
// using Windows.Win32; using Windows.Win32.Media.Audio; (from CsWin32)

namespace LocalScribe.Core.Audio;

public sealed class ProcessLoopbackCapture : ICaptureSource
{
    private readonly uint _pid;
    private readonly IClock _clock;
    private Thread? _pump;
    private volatile bool _running;
    // private IAudioClient _client; private IAudioCaptureClient _capture;
    // private EventWaitHandle _bufferReady = new(false, EventResetMode.AutoReset);

    public SourceKind Source => SourceKind.Remote;
    public event Action<AudioFrame>? FrameAvailable;

    public ProcessLoopbackCapture(uint targetPid, IClock clock)
        => (_pid, _clock) = (targetPid, clock);

    public void Start()
    {
        // 1. Build AUDIOCLIENT_ACTIVATION_PARAMS { ProcessLoopback, _pid, IncludeTree }.
        // 2. Wrap in PROPVARIANT (BLOB).
        // 3. ActivateAudioInterfaceAsync("VAD\\Process_Loopback", IID_IAudioClient,
        //      &params, completionHandler, out op).
        // 4. In ActivateCompleted: GetActivateResult -> _client.
        // 5. _client.Initialize(SHARED, LOOPBACK|EVENTCALLBACK, 0,0, &fmt16kMono, null).
        // 6. _capture = _client.GetService(IID_IAudioCaptureClient).
        // 7. _client.SetEventHandle(_bufferReady.SafeWaitHandle); _client.Start().
        _running = true;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "ProcLoopbackPump" };
        _pump.Start();
    }

    private void PumpLoop()
    {
        while (_running)
        {
            // _bufferReady.WaitOne(200);
            // while (_capture.GetNextPacketSize(out n) > 0 && n != 0) {
            //   _capture.GetBuffer(out pData, out frames, out flags, ...);
            //   var pcm = MarshalToFloat(pData, frames);   // already 16k mono from Initialize
            //   FrameAvailable?.Invoke(new AudioFrame(Source, _clock.ElapsedMs, pcm));
            //   _capture.ReleaseBuffer(frames);
            // }
        }
    }

    public void Stop() { _running = false; _pump?.Join(500); /* _client?.Stop(); */ }
    public void Dispose() { Stop(); /* release COM objects */ }
}
```

**Step 2: Build** — `dotnet build` → Expected: succeeds after type adjustments.

**Step 3: Isolated activation smoke test (before full pipeline)**

In `SpikeRunner` (temporary `--activate-only <pid>` path), confirm `ActivateCompleted`
fires and `GetActivateResult` yields a non-null `IAudioClient` for a **running Teams PID
that is actively playing audio**. Log `"loopback activated for pid {pid}"`.
Expected: the log line appears; no `E_*` HRESULT.

> If activation fails with `AUDCLNT_E_*`, verify the target PID is rendering audio and
> the app is not elevated beyond your process. This gate must pass before Step 4.

**Step 4: Commit**

```bash
git add src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs
git commit -m "feat: ProcessLoopbackCapture via ActivateAudioInterfaceAsync (process loopback)"
```

---

## Task 9: SpikeRunner — dual capture to two WAVs  [SMOKE]  ⭐ the de-risk gate

**Files:**
- Modify: `src/LocalScribe.SpikeRunner/Program.cs`

**Step 1: Implement**

```csharp
// src/LocalScribe.SpikeRunner/Program.cs
using System.Diagnostics;
using LocalScribe.Core.Audio;

string targetName = args.Length > 0 ? args[0] : "Teams";
string outDir = Path.Combine(Environment.GetFolderPath(
    Environment.SpecialFolder.MyDocuments), "LocalScribe", "spike");
Directory.CreateDirectory(outDir);

Process? target = Process.GetProcessesByName(targetName)
    .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
    ?? Process.GetProcessesByName(targetName).FirstOrDefault();
if (target is null) { Console.WriteLine($"No process named '{targetName}'."); return; }
Console.WriteLine($"Target: {target.ProcessName} (pid {target.Id})");

var clock = new StopwatchClock();
using var mic  = new MicCaptureSource(clock);
using var loop = new ProcessLoopbackCapture((uint)target.Id, clock);
using var localSink  = new WavSink(Path.Combine(outDir, "local.wav"));
using var remoteSink = new WavSink(Path.Combine(outDir, "remote.wav"));

long localSamples = 0, remoteSamples = 0;
mic.FrameAvailable  += f => { lock (localSink)  { localSink.Write(f.Samples);  localSamples  += f.Samples.Length; } };
loop.FrameAvailable += f => { lock (remoteSink) { remoteSink.Write(f.Samples); remoteSamples += f.Samples.Length; } };

mic.Start(); loop.Start();
Console.WriteLine("Recording both streams. Press ENTER to stop...");
Console.ReadLine();
mic.Stop(); loop.Stop();

Console.WriteLine($"local.wav : {localSamples / 16000.0:F1}s");
Console.WriteLine($"remote.wav: {remoteSamples / 16000.0:F1}s");
Console.WriteLine($"Files in: {outDir}");
```

**Step 2: Build** — `dotnet build` → Expected: succeeds.

**Step 3: 🔬 MANUAL VERIFICATION (the whole point of Stage 1)**

1. Join a Teams test meeting with a second participant (or an echo bot). **Wear headphones.**
2. Run: `dotnet run --project src/LocalScribe.SpikeRunner -- Teams`
3. Speak a few sentences yourself; have the other side speak distinctly.
4. Press ENTER. Open `%USERPROFILE%\Documents\LocalScribe\spike\`.

**Pass criteria — ALL must hold:**
- [ ] `local.wav` and `remote.wav` both exist, non-trivial size, durations ≈ recording length.
- [ ] `local.wav` plays back **your voice only**, clear, no chop.
- [ ] `remote.wav` plays back **the remote participant(s) only**, clear, no chop.
- [ ] Minimal cross-bleed (a little is OK on speakers; should be near-zero on headphones).
- [ ] No crash; console prints non-zero durations for both.

**If it passes:** Stage 1's unknown is de-risked. ✅
**If `remote.wav` is empty/silent:** revisit Task 8 activation/format (most likely the
`Initialize` format or the event-pump). **If it has Spotify/notification audio mixed in:**
expected for full-system loopback — confirm you used the **per-process** path with the
Teams PID, not a system loopback fallback.

**Step 4: Commit**

```bash
git add src/LocalScribe.SpikeRunner/Program.cs
git commit -m "feat: SpikeRunner captures mic + process loopback to two WAVs"
```

---

## Task 10 (optional): Minimal WPF tray shell  [SMOKE]

Satisfies "tray shell" from the build sequence; thin wrapper over the same capture. Skip
if you'd rather defer all UI to Stage 3 — the de-risk gate (Task 9) is already met.

**Files:**
- Create: `src/LocalScribe.Tray/` (WPF app, net8.0-windows), referencing `LocalScribe.Core`
- Packages: `WPF-UI`, `H.NotifyIcon.Wpf`, `CommunityToolkit.Mvvm`

**Steps (high level — full TDD not applicable to a tray shell):**
1. `dotnet new wpf -o src/LocalScribe.Tray -f net8.0-windows`; add references + packages;
   add to solution.
2. Add an `H.NotifyIcon` `TaskbarIcon` with a context menu: **Start spike / Stop / Exit**.
3. Apply WPF-UI theming (Fluent) to the tray menu/window.
4. Wire **Start** → same `MicCaptureSource` + `ProcessLoopbackCapture` + two `WavSink`s as
   Task 9; **Stop** → dispose + flush. Show a recording state in the tray tooltip/icon.
5. **Manual verify:** tray icon appears; Start during a Teams call → two WAVs as in Task 9;
   tooltip reflects recording state.
6. Commit: `feat: minimal WPF tray shell driving the capture spike`.

---

## Stage 1 — Definition of Done

- [ ] `dotnet test` green (Tasks 1–5: clock, PCM, WAV, resampler, fake pipeline).
- [ ] `dotnet build` clean across all projects.
- [ ] **Task 9 manual gate passes**: a real Teams call yields a clean `local.wav` (you)
      and a clean `remote.wav` (them), correctly separated. ← the real deliverable.
- [ ] Per-process loopback confirmed working (not silently falling back to system loopback).
- [ ] Notes captured for any CsWin32 symbol renames or format quirks (feeds Stage 2).

## Explicitly NOT in Stage 1 (YAGNI — later stages)

VAD segmentation · Whisper transcription · the merge/clock-interleave · JSONL/Markdown
store · FLAC retention · meeting auto-detection · diarisation · settings · full tray UI ·
device hot-swap / sleep handling · packaging. Stage 1 proves *capture* and nothing else.
