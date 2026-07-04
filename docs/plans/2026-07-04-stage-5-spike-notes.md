# Stage 5 Task 0 - Diarisation spike notes (GATE)

Date: 2026-07-04
Branch: `stage-5-diarisation`
Author: Task 0 spike (exploratory, not TDD)
Status: **PASS** - process-isolation architecture confirmed end-to-end on x64; win-arm64
binary published and verified structurally (runtime execution deferred to smoke D7).

This document records, VERBATIM, the empirically-confirmed APIs and the ORT-isolation
result. Tasks 2-5 depend on these exact names; where the plan's researched sketch
differs from reality, the deltas are called out under "Deviations from the plan" so
they can be reconciled before implementation.

Environment: Windows 11 x64 dev box, .NET SDK 10.0.301 (also 9.0.x present).
`org.k2fsa.sherpa.onnx` 1.13.3 restored OK (NuGet reachable). Build/publish used
`-p:NuGetAudit=false` (offline-audit convention). Zero warnings throughout.

---

## 1. ORT isolation result (the load-bearing question) - CONFIRMED

The sherpa helper and the app carry **different `onnxruntime.dll` builds in separate
binary directories**, and sherpa initialised + ran in its own process despite Core's
transitive Microsoft ORT being present. The two runtimes never coexist as the *active*
ORT in one process.

| Location | `onnxruntime.dll` FileVersion | Origin |
|---|---|---|
| `src/LocalScribe.Diarizer/bin/Release/net10.0-windows/win-x64/publish/` | **1.24.4** | sherpa (`org.k2fsa.sherpa.onnx.runtime.win-x64` 1.13.3) |
| `src/LocalScribe.Diarizer/bin/Release/net10.0-windows/win-arm64/publish/` | **1.24.4** | sherpa (`org.k2fsa.sherpa.onnx.runtime.win-arm64` 1.13.3) |
| `src/LocalScribe.App/bin/.../runtimes/win-x64/native/onnxruntime.dll` | **1.22.20250508.2.f217402** (= Microsoft ML OnnxRuntime 1.22.0) | Microsoft (transitive via Core, Silero VAD) |

Both Diarizer publish folders also contain `sherpa-onnx-c-api.dll` (no FileVersion
string) and `sherpa-onnx.dll` 1.13.3.0. The arm64 `sherpa-onnx-c-api.dll` is a genuine
ARM64 image (PE machine type `0xAA64`), confirming the arm64 publish is arch-correct.

App does NOT reference sherpa (verified `LocalScribe.App.csproj`); it gets Microsoft
ORT 1.22.0 only, transitively through `LocalScribe.Core`. So the App/capture process
stays on pristine 1.22.0; the Diarizer process runs on sherpa's 1.24.4. Confirmed.

### 1.1 IMPORTANT caveat found by the spike (feeds Task 2/3)

Because `LocalScribe.Diarizer` references `LocalScribe.Core` (it needs `FlacPcmReader` /
`FlacAudioSink` / `PcmConverter`), and Core references `Microsoft.ML.OnnxRuntime 1.22.0`,
the **Diarizer publish folder also contains Microsoft ORT bits**:

- `Microsoft.ML.OnnxRuntime.dll` (managed, FileVersion `1.22.0.0`) - DORMANT
- `onnxruntime_providers_shared.dll` (FileVersion `1.22.20250508.2.f217402`) - Microsoft's

alongside sherpa's native `onnxruntime.dll` (1.24.4). MSBuild resolved the same-named
native `onnxruntime.dll` file conflict **silently in favour of sherpa's 1.24.4**
(publish exit 0, no `NETSDK1152`). This is exactly the "silent winner" the design
warned about - but here it is **benign**, because:

1. It happens inside the *helper* process, which never constructs a Microsoft
   `InferenceSession` (no Silero on this path); the managed Microsoft assembly sits
   unused.
2. The empirical run (Section 4) PROVED sherpa initialises and completes with these
   mixed DLLs present.

**Binding note for Task 3:** keep `LocalScribe.Diarizer` free of any
`Microsoft.ML.OnnxRuntime` usage (it only needs CUETools + sherpa). If a future
tidy-up is wanted, the helper could depend on a leaf audio assembly instead of all of
Core to stop dragging Microsoft ORT into the publish folder - **not required for
correctness**, just hygiene.

### 1.2 Publish commands used (both succeeded, 0 warnings)

```
dotnet publish src/LocalScribe.Diarizer -r win-x64   -c Release -p:NuGetAudit=false
dotnet publish src/LocalScribe.Diarizer -r win-arm64 -c Release -p:NuGetAudit=false
```

---

## 2. sherpa-onnx C# API - CONFIRMED VERBATIM

Package `org.k2fsa.sherpa.onnx` **1.13.3**; managed assembly **`sherpa-onnx.dll`**;
namespace **`SherpaOnnx`**. Confirmed by (a) reflection dump of the restored assembly
and (b) the run harness compiling **and executing** against these exact types.

### 2.1 Config types (all are `struct` value types, parameterless ctor)

```
SherpaOnnx.OfflineSpeakerDiarizationConfig            (struct)
    OfflineSpeakerSegmentationModelConfig Segmentation   // field
    SpeakerEmbeddingExtractorConfig       Embedding      // field
    FastClusteringConfig                  Clustering     // field
    float                                 MinDurationOn  // field
    float                                 MinDurationOff // field

SherpaOnnx.OfflineSpeakerSegmentationModelConfig     (struct)
    OfflineSpeakerSegmentationPyannoteModelConfig Pyannote  // field
    int    NumThreads
    int    Debug
    string Provider

SherpaOnnx.OfflineSpeakerSegmentationPyannoteModelConfig (struct)
    string Model            // <-- pyannote segmentation model.onnx path

SherpaOnnx.SpeakerEmbeddingExtractorConfig           (struct)
    string Model            // <-- CAM++ embedding .onnx path
    int    NumThreads
    int    Debug
    string Provider

SherpaOnnx.FastClusteringConfig                      (struct)
    int   NumClusters       // > 0 forces exactly N clusters
    float Threshold         // auto/threshold mode when NumClusters <= 0
```

Config setup that COMPILES + RUNS (mutation on a **local** struct variable is legal
because these are fields, not properties):

```csharp
var config = new OfflineSpeakerDiarizationConfig();
config.Segmentation.Pyannote.Model = segModelPath;
config.Segmentation.NumThreads     = 1;
config.Embedding.Model             = embModelPath;
config.Embedding.NumThreads        = 1;
config.Clustering.Threshold        = 0.5f;   // auto
// forced-N alternative: config.Clustering.NumClusters = k;
```

### 2.2 The engine class

```
SherpaOnnx.OfflineSpeakerDiarization                 (class)
    .ctor(OfflineSpeakerDiarizationConfig config)
    int  SampleRate { get; }                         // returns 16000 for these models
    void SetConfig(OfflineSpeakerDiarizationConfig config)
    OfflineSpeakerDiarizationSegment[] Process(float[] samples)
    OfflineSpeakerDiarizationSegment[] ProcessWithCallback(
        float[] samples,
        OfflineSpeakerDiarizationProgressCallback callback,
        IntPtr arg)                                  // <-- NOTE the 3rd param
    void Dispose()
```

### 2.3 Progress callback (named delegate)

```
SherpaOnnx.OfflineSpeakerDiarizationProgressCallback (delegate)
    int Invoke(int numProcessedChunks, int numTotalChunks, IntPtr arg)
```

A lambda binds to it; the returned int is IGNORED upstream (no cooperative cancel -
this is why the design kills the child process to cancel):

```csharp
OfflineSpeakerDiarizationProgressCallback cb = (processed, total, _) =>
{
    if (total > 0) onProgress(Math.Clamp((double)processed / total, 0, 1));
    return 0;
};
var segments = sd.ProcessWithCallback(samples16kMono, cb, IntPtr.Zero);
```

### 2.4 Result segment shape

```
SherpaOnnx.OfflineSpeakerDiarizationSegment          (class)
    float Start      // START time in SECONDS
    float End        // END   time in SECONDS
    int   Speaker    // 0-based cluster index
```

`ProcessWithCallback` returns a plain `OfflineSpeakerDiarizationSegment[]` (already one
segment per contiguous single-speaker span). To get ms + sort:

```csharp
var ordered = segments.OrderBy(s => s.Start).ToArray();
long startMs = (long)Math.Round(s.Start * 1000);
long endMs   = (long)Math.Round(s.End   * 1000);
int cluster  = s.Speaker;
```

Related types present but NOT needed for diarisation (for reference):
`SpeakerEmbeddingExtractor`, `SpeakerEmbeddingManager`, `SpeechSegment` -
these are the standalone speaker-ID / VAD APIs, not the offline diariser.

---

## 3. FlakeReader / AudioBuffer API - CONFIRMED VERBATIM

`CUETools.Codecs.FLAKE` **1.0.5** (assembly `CUETools.Codecs.FLAKE.dll`) +
`CUETools.Codecs` **1.0.2** (assembly `CUETools.Codecs.dll`). Confirmed by reflection
and by a same-library round-trip (write via `FlacAudioSink` -> read via `FlakeReader`,
max abs error **2.724e-5**, i.e. < 1 LSB / lossless for 16-bit PCM).

```
CUETools.Codecs.FLAKE.FlakeReader                    (class)
    .ctor(string path, Stream IO)     // pass IO = null to open by path
    .ctor(AudioPCMConfig _pcm)
    AudioPCMConfig PCM      { get; set; }   // decoded stream format
    long           Length   { get; set; }   // total frames (sample count per channel)
    long           Remaining { get; }
    long           Position  { get; set; }
    int[]          Samples   { get; }
    int  Read(AudioBuffer buff, int maxLength)   // returns #frames read; 0 at EOF
    void Close()
    void Dispose()

CUETools.Codecs.AudioBuffer                          (class)
    .ctor(AudioPCMConfig _pcm, int _size)
    byte[]   Bytes   { get; }    // interleaved little-endian PCM (int16 LE for 16-bit)
    int[,]   Samples { get; }    // [frame, channel] int samples
    float[,] Float   { get; }    // [frame, channel] float samples (alt to Bytes)
    int            Length    { get; set; }
    AudioPCMConfig PCM       { get; }
    int            ByteLength { get; }

CUETools.Codecs.AudioPCMConfig                       (class)
    .ctor(int bitsPerSample, int channelCount, int sampleRate)
    int  BitsPerSample { get; }
    int  ChannelCount  { get; }
    int  SampleRate    { get; }
    int  BlockAlign    { get; }   // = channels * bytesPerSample; 2 for 16-bit mono
    bool IsRedBook     { get; }
```

### 3.1 Confirmed decode read-loop (matches the plan's Task 2 `FlacPcmReader`)

```csharp
using var reader = new FlakeReader(path, null);
AudioPCMConfig pcm = reader.PCM;                 // pcm.SampleRate, pcm.ChannelCount
if (pcm.SampleRate != 16000 || pcm.ChannelCount != 1) throw new InvalidDataException(...);
var buffer = new AudioBuffer(pcm, 16384);
int n;
while ((n = reader.Read(buffer, 16384)) > 0)
{
    ReadOnlySpan<byte> bytes = buffer.Bytes.AsSpan(0, n * pcm.BlockAlign);
    floats.AddRange(PcmConverter.Int16BytesToFloat(bytes));   // Core helper, already exists
}
```

The plan's Task 2 `FlacPcmReader` (`new FlakeReader(path, null)`, `reader.PCM`,
`Read(buffer, 16384)`, `buffer.Bytes`, `pcm.BlockAlign`) is **exactly correct** - no
change needed. (`AudioBuffer.Float` is an available shortcut but the Bytes +
`PcmConverter.Int16BytesToFloat` path works and is what the plan uses.)

---

## 4. End-to-end run result (synthesized clip) - PASS

No real call audio exists on this box, so the harness synthesized a **10.0 s / 16 kHz /
mono / 16-bit FLAC** (two alternating synthetic "voices": different f0 + harmonic
weights, AM syllable envelope, silence gaps) via `FlacAudioSink`, decoded it back via
`FlakeReader`, and ran `OfflineSpeakerDiarization` in auto/threshold mode.

```
synth samples: 160000 (10.0s)  -> FLAC 178,987 bytes
decoded: 160000 samples  rate=16000 ch=1   round-trip max abs error 2.724e-5
OfflineSpeakerDiarization.SampleRate = 16000
progress 1/1 = 100%          (callback fired)
RESULT: 1 segment  [0.031s -> 9.953s]  speaker=1
wall=0.06s  audio=10.00s  RTF~0.01x
```

sherpa loaded segmentation + embedding + fast-clustering and returned a result WITHOUT
crashing - the load-bearing proof that the process-isolation architecture works with
sherpa's own 1.24.4 ORT active in-process.

**Do not read accuracy into this.** Pure synthetic tones are not real speech; the
segmenter collapsed the whole clip into one region. The RTF (~0.01x) is meaningless on
this input. Real-audio RTF/DER are explicitly deferred (Section 6).

### 4.1 Minor finding - clusterCount derivation

sherpa labelled the single region **`speaker=1`** (not 0). So computing
`clusterCount = maxSpeaker + 1` yields **2** while only **1** distinct cluster is
present. Recommendation for Task 3/4: derive `ClusterCount` (and the set of cluster
keys) from the **distinct** speaker indices actually present, not `max+1`, so a phantom
`Remote:0` / "Remote Speaker 1" default label is never materialised for an absent
cluster. (Task 5's `ClusterAssigner` already iterates distinct ids via `SortedSet`, so
it is robust; only the helper's reported `ClusterCount` and any label-materialisation
step need the distinct-count treatment.) On real multi-speaker audio sherpa normally
emits contiguous 0-based indices, but the helper should not assume it.

---

## 5. Deviations from the plan's researched API (RECONCILE before Task 3)

The plan's Task 3 `SherpaDiarisationRunner` sketch is *mostly* right but has two hard
compile-breaking errors and some clarifications:

1. **`ProcessWithCallback` takes a THIRD argument `IntPtr arg`.** Plan sketch called
   `sd.ProcessWithCallback(samples, callback)` (2 args). Reality:
   `ProcessWithCallback(float[] samples, OfflineSpeakerDiarizationProgressCallback callback, IntPtr arg)`.
   Pass `IntPtr.Zero`.

2. **No `.SortByStartTime()`.** Plan sketch did `result.SortByStartTime().Select(...)`.
   `ProcessWithCallback` returns a plain `OfflineSpeakerDiarizationSegment[]`; there is
   NO wrapper type and NO `SortByStartTime` method. Use `result.OrderBy(s => s.Start)`.

3. **Callback delegate type is named** `OfflineSpeakerDiarizationProgressCallback`
   `(int numProcessedChunks, int numTotalChunks, IntPtr arg) -> int`. A lambda binds
   fine; return `0`; the value is ignored (no cooperative cancel - confirmed).

4. **Config types are structs.** `config.Segmentation.Pyannote.Model = path` works
   only because `config` is a local variable (fields, not properties). Fine as written.

5. **Segment fields** `Start`/`End` are `float` **seconds**, `Speaker` is `int`
   0-based. Plan's `s.Start * 1000 -> ms` and `Cluster: s.Speaker` are correct.

6. **`using SherpaOnnx;`** is the correct namespace (plan's "confirm namespace" -> yes).

7. Plan Task 2 `FlacPcmReader` read-loop is correct as written (Section 3.1) - no
   change.

8. **clusterCount** - see Section 4.1 (prefer distinct-count over `max+1`).

Corrected minimal runner body (compiles + runs):

```csharp
using SherpaOnnx;
var config = new OfflineSpeakerDiarizationConfig();
config.Segmentation.Pyannote.Model = segModelPath;
config.Embedding.Model             = embModelPath;
if (forcedClusterCount is int k && k > 0) config.Clustering.NumClusters = k;
else                                      config.Clustering.Threshold   = 0.5f;

using var sd = new OfflineSpeakerDiarization(config);
OfflineSpeakerDiarizationProgressCallback cb = (processed, total, _) =>
{ if (total > 0) onProgress(Math.Clamp((double)processed / total, 0, 1)); return 0; };
var segs = sd.ProcessWithCallback(samples16kMono, cb, IntPtr.Zero)
             .OrderBy(s => s.Start)
             .Select(s => new WireSegment((long)Math.Round(s.Start * 1000),
                                          (long)Math.Round(s.End   * 1000), s.Speaker))
             .ToList();
int clusterCount = segs.Select(s => s.Cluster).Distinct().Count();   // distinct, not max+1
```

---

## 6. Deferred items (per controller scope / design)

- **win-arm64 runtime execution** - NOT run (x64 box). The arm64 binary published
  cleanly with its own arm64 `onnxruntime.dll` 1.24.4 + arm64 `sherpa-onnx-c-api.dll`
  (PE machine `0xAA64`). Functional arm64 run deferred to **smoke D7** / the smoke
  runbook. If arm64 fails there, fall back to Approach B (stock CLI sidecar) for arm64
  per the design.
- **Real-audio RTF + DER** - deferred to smoke D7. No recorded call FLAC exists on this
  box; the synthetic clip cannot measure either meaningfully. The DER fixture harness
  (plan Task 10, `Category=Fixture`) is the home for this once a corpus is recorded.

---

## 7. Model fetch confirmations (feed Task 1)

Both models fetched manually into `models/` (gitignored). Layout confirmed working with
the helper:

| Role | On-disk path | Size (bytes) | SHA-256 |
|---|---|---|---|
| Embedding (CAM++ zh+en, Apache-2.0) | `models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx` | 28,281,164 | `aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2` |
| Segmentation (pyannote-3.0, MIT) - extracted `model.onnx` | `models/sherpa-onnx-pyannote-segmentation-3-0/model.onnx` | 5,992,913 | `220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079` |

Notes for Task 1:
- The **embedding SHA `aa3cfc16...` MATCHES the plan's Task 1 pin** - the pin is correct.
  (An earlier apparent mismatch was a truncated download; the full 28,281,164-byte file
  hashes to the pinned value.)
- **Pin the segmentation `model.onnx` SHA as `220ad67c...`** (self-computed; the
  release ships no vendor hash). Size 5,992,913 bytes.
- Segmentation source is a **`.tar.bz2`**
  (`.../releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2`,
  6,958,444 bytes). `tar -xjf` extracts a top-level dir `sherpa-onnx-pyannote-segmentation-3-0/`
  containing `model.onnx`, `model.int8.onnx`, **`LICENSE` (MIT - carry into third-party
  notices)**, and example scripts. Target layout `models/sherpa-onnx-pyannote-segmentation-3-0/model.onnx`
  is what the helper expects.
- **Download reliability:** this box throttled GitHub release downloads to ~30-80 KB/s
  (the 28 MB embedding needed resume). The HuggingFace mirror
  `https://huggingface.co/csukuangfj/speaker-embedding-models/resolve/main/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx`
  serves byte-identical content (same X-Linked-Size + same SHA) and may be a more
  reliable fetch source; `fetch-models.ps1` should use `-C -`/resume + a retry loop.

---

## 8. Exit decision

**PASS - proceed with the out-of-process architecture.** ORT isolation holds (separate
1.24.4 vs 1.22.0 dirs, separate processes; sherpa runs with mixed DLLs present).
sherpa + FlakeReader APIs confirmed verbatim (Sections 2-3). In-process is NOT required.
No re-brainstorm needed. Reconcile the Section 5 deltas into Task 3 (and Section 4.1
into Task 3/4) before implementing.

Kept: `src/LocalScribe.Diarizer/LocalScribe.Diarizer.csproj` + its `LocalScribe.slnx`
entry (Task 2/3 build on them). The throwaway `Program.cs` harness was reset (Task 3
rewrites it via TDD) and is not committed.
