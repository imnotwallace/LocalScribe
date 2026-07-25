# Voice-Fingerprint Speaker Recognition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Suggest-only cross-session speaker recognition: at diarise time the app matches fresh cluster embeddings against a People registry (matter-scoped by default, opt-in global) and offers accept/dismiss suggestion chips; confirmed identities auto-enroll; all voiceprint data is retrospectively deletable.

**Architecture:** The Diarizer helper's diarise op additionally emits per-cluster mean CAM++ embeddings (plus a new `embed` op for backfill); Core persists them per session (`embeddings.json`, derived), matches in pure C# cosine similarity against a new `people\people.json` registry, and the Split Speakers dialog surfaces suggestions. Spec: `docs/superpowers/specs/2026-07-25-voice-fingerprint-design.md`.

**Tech Stack:** net10.0-windows, xUnit, CommunityToolkit.Mvvm, sherpa-onnx 1.13.3 (`org.k2fsa.sherpa.onnx`, Diarizer project only), System.Text.Json via `LocalScribeJson`/`DiarisationJson`.

## Global Constraints

- Evidentiary firewall: NEVER delete or modify audio legs, `transcript.jsonl`, or speaker *names*; voiceprint deletion touches only `people.json` enrollments, `embeddings.json` files, and `SuggestionProvenance` maps.
- Suggest-only: no code path may auto-assign a speaker name from a match; suggestions render as chips the user accepts or dismisses.
- Native isolation: sherpa-onnx / ORT 1.24.4 stay inside `LocalScribe.Diarizer`; Core gets NO new native dependency (matching is pure C#).
- VMs are WPF-free: observable mutation from background work goes through the injected `Action<Action> dispatch`; no `DateTime.Now`/`Guid.NewGuid` in VMs — `TimeProvider` + injected id factories only.
- VM tests use QUEUED dispatcher fakes, not synchronous ones (BeginInvoke stamp-ordering lesson, assistant-surfaces round).
- Match constants: `SuggestThreshold = 0.55`, `RunnerUpMargin = 0.05`, `MaxEnrollmentsPerPerson = 20`, `EmbeddingSamples.MaxSecondsPerCluster = 30`. Embedding method string: `"campplus-zh-en"`.
- Test files: flat in `tests/LocalScribe.Core.Tests/` or `tests/LocalScribe.App.Tests/`, no namespace, xUnit `[Fact]`. No Unicode emojis in test scripts.
- Wire JSON uses `DiarisationJson.Options` (camelCase, ignore-null); storage JSON uses `LocalScribeJson.Options` via `JsonFile`/`SchemaGuard`/`AtomicFile`.
- Commit after every task; task commits use `feat(voiceprint): ...` / `test(voiceprint): ...`.

**Spec deviations (agreed rationale, flag at review):**
1. Per-session "Enroll voice from this session" backfill is implemented as a single Settings action "Scan sessions and enroll known speakers" (batch over all sessions) — same capability, one button instead of a per-session affordance.
2. The inline "link to person: existing/new" picker is realized as: accepted suggestions link automatically; clusters named to a person-linked roster member enroll automatically; a per-row "Remember voice" checkbox (default OFF) creates/enrolls a person from the typed name. No silent person creation.
3. The People list shows an enrollment count/summary per person with "delete voiceprint" (all enrollments) and "delete person"; single-enrollment deletion is exposed as delete-oldest (the `RemoveEnrollment` op supports per-id deletion for a follow-up expandable list).

---

### Task 1: Wire records — EmitEmbeddings, ClusterEmbeddings, embed op

**Files:**
- Modify: `src/LocalScribe.Core/Diarisation/DiarisationWire.cs`
- Test: `tests/LocalScribe.Core.Tests/DiarisationWireTests.cs` (append)

**Interfaces:**
- Consumes: existing `DiarisationJob`, `DiarisationResultPayload`, `DiarisationJson.Options`.
- Produces: `DiarisationJob.EmitEmbeddings` (bool, default false); `DiarisationResultPayload.ClusterEmbeddings` (`IReadOnlyDictionary<string, float[]>?`, keys are cluster ids as strings e.g. `"0"`) and `.EmbeddingMethod` (`string?`); `EmbedRange(long StartMs, long EndMs)`; `EmbedJob(string Op, string FlacPath, IReadOnlyList<EmbedRange> Ranges, string EmbeddingModelPath)`; `EmbedResultPayload(float[] Embedding, string Method)`; `EmbeddingMethods.CampPlus = "campplus-zh-en"`.

- [ ] **Step 1: Write the failing tests** — append to `tests/LocalScribe.Core.Tests/DiarisationWireTests.cs`:

```csharp
[Fact]
public void Job_without_emitEmbeddings_deserializes_false()
{
    var job = JsonSerializer.Deserialize<DiarisationJob>(
        "{\"flacPath\":\"a.flac\",\"source\":\"Remote\",\"segmentationModelPath\":\"s\",\"embeddingModelPath\":\"e\",\"forcedClusterCount\":null}",
        DiarisationJson.Options)!;
    Assert.False(job.EmitEmbeddings);
}

[Fact]
public void Result_without_clusterEmbeddings_deserializes_null()
{
    var r = JsonSerializer.Deserialize<DiarisationResultPayload>(
        "{\"segments\":[],\"clusterCount\":0,\"method\":\"m\"}", DiarisationJson.Options)!;
    Assert.Null(r.ClusterEmbeddings);
    Assert.Null(r.EmbeddingMethod);
}

[Fact]
public void Result_with_clusterEmbeddings_round_trips()
{
    var payload = new DiarisationResultPayload([], 1, "m",
        new Dictionary<string, float[]> { ["0"] = [0.1f, 0.2f] }, EmbeddingMethods.CampPlus);
    var json = JsonSerializer.Serialize(payload, DiarisationJson.Options);
    var back = JsonSerializer.Deserialize<DiarisationResultPayload>(json, DiarisationJson.Options)!;
    Assert.Equal(0.2f, back.ClusterEmbeddings!["0"][1]);
    Assert.Equal("campplus-zh-en", back.EmbeddingMethod);
}

[Fact]
public void EmbedJob_and_result_round_trip()
{
    var job = new EmbedJob("embed", "a.flac", [new EmbedRange(0, 1500)], "e.onnx");
    var back = JsonSerializer.Deserialize<EmbedJob>(
        JsonSerializer.Serialize(job, DiarisationJson.Options), DiarisationJson.Options)!;
    Assert.Equal("embed", back.Op);
    Assert.Equal(1500, back.Ranges[0].EndMs);
    var res = JsonSerializer.Deserialize<EmbedResultPayload>(
        "{\"embedding\":[1.0,2.0],\"method\":\"campplus-zh-en\"}", DiarisationJson.Options)!;
    Assert.Equal(2f, res.Embedding[1]);
}
```

If the existing file lacks them, add `using System.Text.Json;` and `using LocalScribe.Core.Diarisation;` at the top.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter DiarisationWireTests -v q`
Expected: compile errors (`EmitEmbeddings`, `EmbedJob` not defined).

- [ ] **Step 3: Implement** — in `src/LocalScribe.Core/Diarisation/DiarisationWire.cs`, replace the `DiarisationJob` and `DiarisationResultPayload` records and append the new types:

```csharp
public sealed record DiarisationJob(
    string FlacPath,
    string Source,                 // "Local" / "Remote" (TranscriptSource string)
    string SegmentationModelPath,
    string EmbeddingModelPath,
    int? ForcedClusterCount,       // null = auto (threshold); N = force exactly N
    bool EmitEmbeddings = false);  // voiceprint design 2026-07-25: also emit per-cluster mean embeddings

public sealed record DiarisationResultPayload(
    IReadOnlyList<WireSegment> Segments,
    int ClusterCount,
    string Method,
    IReadOnlyDictionary<string, float[]>? ClusterEmbeddings = null,  // cluster id ("0") -> mean vector
    string? EmbeddingMethod = null);

/// <summary>The embed op (voiceprint design 2026-07-25): mean speaker embedding over the given
/// ranges of a FLAC leg. Routed by the helper on the presence of op=="embed"; a DiarisationJob
/// has no op property, so legacy jobs keep working unchanged.</summary>
public sealed record EmbedRange(long StartMs, long EndMs);

public sealed record EmbedJob(
    string Op,                      // always "embed"
    string FlacPath,
    IReadOnlyList<EmbedRange> Ranges,
    string EmbeddingModelPath);

public sealed record EmbedResultPayload(float[] Embedding, string Method);

public static class EmbeddingMethods
{
    /// <summary>The 3D-Speaker CAM++ zh-en model both diarisation clustering and voiceprint
    /// enrollment run on. Only same-method embeddings are comparable.</summary>
    public const string CampPlus = "campplus-zh-en";
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter DiarisationWireTests -v q`
Expected: PASS (all, including pre-existing).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Diarisation/DiarisationWire.cs tests/LocalScribe.Core.Tests/DiarisationWireTests.cs
git commit -m "feat(voiceprint): wire records for cluster embeddings + embed op"
```

---

### Task 2: Pure math — VoiceprintMath + EmbeddingSamples

**Files:**
- Create: `src/LocalScribe.Core/Diarisation/VoiceprintMath.cs`
- Create: `src/LocalScribe.Core/Diarisation/EmbeddingSamples.cs`
- Test: `tests/LocalScribe.Core.Tests/VoiceprintMathTests.cs`

**Interfaces:**
- Produces: `VoiceprintMath.Cosine(float[] a, float[] b) -> double` (0.0 on length mismatch, empty, or zero norm); `EmbeddingSamples.SampleRate = 16000`, `EmbeddingSamples.MaxSecondsPerCluster = 30`, `EmbeddingSamples.Slice(float[] samples16kMono, IEnumerable<EmbedRange> ranges) -> float[]` (clamps ranges to the buffer, concatenates in given order, truncates at the cap).

- [ ] **Step 1: Write the failing tests** — `tests/LocalScribe.Core.Tests/VoiceprintMathTests.cs`:

```csharp
using LocalScribe.Core.Diarisation;

public class VoiceprintMathTests
{
    [Fact]
    public void Cosine_identical_vectors_is_1()
        => Assert.Equal(1.0, VoiceprintMath.Cosine([1f, 2f, 3f], [1f, 2f, 3f]), 6);

    [Fact]
    public void Cosine_orthogonal_vectors_is_0()
        => Assert.Equal(0.0, VoiceprintMath.Cosine([1f, 0f], [0f, 1f]), 6);

    [Fact]
    public void Cosine_length_mismatch_or_zero_norm_is_0()
    {
        Assert.Equal(0.0, VoiceprintMath.Cosine([1f, 2f], [1f, 2f, 3f]));
        Assert.Equal(0.0, VoiceprintMath.Cosine([0f, 0f], [1f, 2f]));
        Assert.Equal(0.0, VoiceprintMath.Cosine([], []));
    }

    [Fact]
    public void Slice_clamps_concatenates_in_order()
    {
        float[] samples = new float[EmbeddingSamples.SampleRate * 2];       // 2s
        for (int i = 0; i < samples.Length; i++) samples[i] = i;
        var outp = EmbeddingSamples.Slice(samples,
            [new EmbedRange(1000, 1500), new EmbedRange(0, 250)]);
        Assert.Equal(EmbeddingSamples.SampleRate / 2 + EmbeddingSamples.SampleRate / 4, outp.Length);
        Assert.Equal(EmbeddingSamples.SampleRate, outp[0]);                 // 1000ms -> sample 16000
        Assert.Equal(0, outp[EmbeddingSamples.SampleRate / 2]);             // second range starts at 0
    }

    [Fact]
    public void Slice_out_of_bounds_range_is_clamped_not_thrown()
    {
        float[] samples = new float[EmbeddingSamples.SampleRate];           // 1s
        var outp = EmbeddingSamples.Slice(samples, [new EmbedRange(500, 99_000)]);
        Assert.Equal(EmbeddingSamples.SampleRate / 2, outp.Length);
    }

    [Fact]
    public void Slice_truncates_at_cap()
    {
        float[] samples = new float[EmbeddingSamples.SampleRate * 40];      // 40s
        var outp = EmbeddingSamples.Slice(samples, [new EmbedRange(0, 40_000)]);
        Assert.Equal(EmbeddingSamples.SampleRate * EmbeddingSamples.MaxSecondsPerCluster, outp.Length);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter VoiceprintMathTests -v q`
Expected: compile errors (types not defined).

- [ ] **Step 3: Implement**

`src/LocalScribe.Core/Diarisation/VoiceprintMath.cs`:

```csharp
namespace LocalScribe.Core.Diarisation;

/// <summary>Pure embedding-vector math for voiceprint matching (voiceprint design 2026-07-25).
/// Degenerate inputs (length mismatch, zero norm, empty) score 0.0 - "no similarity", never throw.</summary>
public static class VoiceprintMath
{
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0.0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0.0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
```

`src/LocalScribe.Core/Diarisation/EmbeddingSamples.cs`:

```csharp
namespace LocalScribe.Core.Diarisation;

/// <summary>Pure 16k-mono sample slicing shared by the Diarizer helper (per-cluster embedding
/// extraction) and tests. Ranges are clamped to the buffer; concatenation stops at
/// <see cref="MaxSecondsPerCluster"/> so a long cluster cannot balloon extraction time.</summary>
public static class EmbeddingSamples
{
    public const int SampleRate = 16000;
    public const int MaxSecondsPerCluster = 30;

    public static float[] Slice(float[] samples16kMono, IEnumerable<EmbedRange> ranges)
    {
        int cap = SampleRate * MaxSecondsPerCluster;
        var outp = new List<float>(Math.Min(cap, samples16kMono.Length));
        foreach (var r in ranges)
        {
            int start = (int)Math.Clamp(r.StartMs * SampleRate / 1000, 0, samples16kMono.Length);
            int end = (int)Math.Clamp(r.EndMs * SampleRate / 1000, 0, samples16kMono.Length);
            for (int i = start; i < end && outp.Count < cap; i++) outp.Add(samples16kMono[i]);
            if (outp.Count >= cap) break;
        }
        return [.. outp];
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter VoiceprintMathTests -v q`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Diarisation/VoiceprintMath.cs src/LocalScribe.Core/Diarisation/EmbeddingSamples.cs tests/LocalScribe.Core.Tests/VoiceprintMathTests.cs
git commit -m "feat(voiceprint): pure cosine + sample slicing"
```

---

### Task 3: Diarizer helper — embedding emission + embed op

**Files:**
- Create: `src/LocalScribe.Diarizer/SherpaEmbeddingRunner.cs`
- Modify: `src/LocalScribe.Diarizer/Program.cs`

**Interfaces:**
- Consumes: Task 1 wire records; Task 2 `EmbeddingSamples.Slice`; existing `FlacPcmReader.ReadMono16k`, `SherpaDiarisationRunner`.
- Produces: helper behavior — diarise jobs with `emitEmbeddings:true` emit `clusterEmbeddings`/`embeddingMethod` in the result line; stdin JSON containing `"op":"embed"` runs the embed op and emits one `EmbedResultPayload` line. No test project covers the Diarizer (humble object); correctness is compile + the fixture smoke in Task 12's runbook step.

- [ ] **Step 1: Verify the sherpa C# embedding API surface**

The plan assumes (from sherpa-onnx csharp-api examples): `SpeakerEmbeddingExtractorConfig { Model, NumThreads }`, `new SpeakerEmbeddingExtractor(config)`, `extractor.CreateStream()`, `stream.AcceptWaveform(int sampleRate, float[] samples)`, `stream.InputFinished()`, `extractor.Compute(stream) -> float[]`. Verify before coding:

Run (PowerShell):
```powershell
$dll = Get-ChildItem "$env:USERPROFILE\.nuget\packages\org.k2fsa.sherpa.onnx\1.13.3" -Recurse -Filter sherpa-onnx.dll | Select-Object -First 1
[System.Reflection.Assembly]::LoadFile($dll.FullName).GetTypes() |
  Where-Object { $_.Name -like '*Embedding*' } |
  ForEach-Object { $_.FullName; $_.GetMethods() | Where-Object DeclaringType -eq $_ | ForEach-Object { '  ' + $_.ToString() } }
```
Expected: `SherpaOnnx.SpeakerEmbeddingExtractor` with `CreateStream`/`Compute` (or `ComputeEmbedding`). If names differ, adapt Step 2's runner to the actual names — the wire contract from Task 1 must NOT change.

- [ ] **Step 2: Write the runner** — `src/LocalScribe.Diarizer/SherpaEmbeddingRunner.cs`:

```csharp
using SherpaOnnx;

namespace LocalScribe.Diarizer;

// Humble object over sherpa-onnx SpeakerEmbeddingExtractor (voiceprint design 2026-07-25).
// Same CAM++ model file the diarisation clustering uses; loaded once per helper invocation.
internal sealed class SherpaEmbeddingRunner : IDisposable
{
    private readonly SpeakerEmbeddingExtractor _extractor;

    public SherpaEmbeddingRunner(string embModelPath)
    {
        var config = new SpeakerEmbeddingExtractorConfig();
        config.Model = embModelPath;
        config.NumThreads = 1;
        _extractor = new SpeakerEmbeddingExtractor(config);
    }

    public float[] Compute(float[] samples16kMono)
    {
        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(16000, samples16kMono);
        stream.InputFinished();
        return _extractor.Compute(stream);
    }

    public void Dispose() => _extractor.Dispose();
}
```

(If Step 1 showed `OnlineStream` is not `IDisposable` or `Compute` takes different args, adapt here only.)

- [ ] **Step 3: Wire both paths in `Program.cs`** — replace the body of the `try` block (keep `Emit`/`Fail` and the catch):

```csharp
    string input = await Console.In.ReadToEndAsync();

    // Op routing (voiceprint design 2026-07-25): "embed" jobs carry op=="embed"; a legacy
    // DiarisationJob has no op property and takes the original path unchanged.
    var probe = System.Text.Json.Nodes.JsonNode.Parse(input)?.AsObject();
    if (probe is not null && probe.TryGetPropertyValue("op", out var opNode) && opNode?.GetValue<string>() == "embed")
    {
        var embedJob = JsonSerializer.Deserialize<EmbedJob>(input, DiarisationJson.Options)
                       ?? throw new InvalidDataException("empty embed job");
        if (!File.Exists(embedJob.EmbeddingModelPath))
            return Fail("MODEL_MISSING", "embedding model file not found");
        float[] embedSamples;
        try { embedSamples = FlacPcmReader.ReadMono16k(embedJob.FlacPath); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        { return Fail("BAD_AUDIO", ex.Message); }

        var sliced = EmbeddingSamples.Slice(embedSamples, embedJob.Ranges);
        if (sliced.Length == 0) return Fail("BAD_AUDIO", "embed ranges cover no audio");
        using var embedder = new SherpaEmbeddingRunner(embedJob.EmbeddingModelPath);
        Emit(new EmbedResultPayload(embedder.Compute(sliced), EmbeddingMethods.CampPlus));
        return 0;
    }

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

    if (job.EmitEmbeddings)
    {
        using var embedder = new SherpaEmbeddingRunner(job.EmbeddingModelPath);
        var byCluster = new Dictionary<string, float[]>();
        foreach (var group in result.Segments.GroupBy(s => s.Cluster))
        {
            var sliced = EmbeddingSamples.Slice(samples,
                group.Select(s => new EmbedRange(s.StartMs, s.EndMs)));
            if (sliced.Length > 0) byCluster[group.Key.ToString()] = embedder.Compute(sliced);
        }
        result = result with { ClusterEmbeddings = byCluster, EmbeddingMethod = EmbeddingMethods.CampPlus };
    }
    Emit(result);
    return 0;
```

Add `using System.Linq;` if not implicit.

- [ ] **Step 4: Build**

Run: `dotnet build src/LocalScribe.Diarizer -v q`
Expected: 0 errors. If `SpeakerEmbeddingExtractor` members mismatch, fix per Step 1's reflection output.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Diarizer/SherpaEmbeddingRunner.cs src/LocalScribe.Diarizer/Program.cs
git commit -m "feat(voiceprint): helper emits cluster embeddings + embed op"
```

---

### Task 4: ClusterEmbeddings model + store + StoragePaths

**Files:**
- Create: `src/LocalScribe.Core/Model/ClusterEmbeddings.cs`
- Create: `src/LocalScribe.Core/Storage/ClusterEmbeddingsStore.cs`
- Modify: `src/LocalScribe.Core/Storage/StoragePaths.cs`
- Test: `tests/LocalScribe.Core.Tests/ClusterEmbeddingsStoreTests.cs`

**Interfaces:**
- Produces: `ClusterEmbeddings { SchemaVersion=1, Method, ExtractedAtUtc, Entries: IReadOnlyDictionary<string, float[]> }` (keys are FULL post-remap clusterKeys, e.g. `"Remote:0"`); `ClusterEmbeddingsStore(string path)` with `SaveAsync`, `LoadAsync` (null when absent/corrupt-JSON — derived data never blocks), `Delete()`; `StoragePaths.EmbeddingsJson(string id)` and `EmbeddingsJson(string id, string versionId)`, `StoragePaths.PeopleDir`, `StoragePaths.PeopleJson` (used by Task 5).

- [ ] **Step 1: Write the failing tests** — `tests/LocalScribe.Core.Tests/ClusterEmbeddingsStoreTests.cs`:

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class ClusterEmbeddingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lsembtests").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private string PathFor() => Path.Combine(_dir, "embeddings.json");

    [Fact]
    public async Task Round_trips_entries_and_method()
    {
        var store = new ClusterEmbeddingsStore(PathFor());
        await store.SaveAsync(new ClusterEmbeddings
        {
            Method = "campplus-zh-en",
            ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]> { ["Remote:0"] = [1f, 2f] },
        }, default);
        var back = await store.LoadAsync(default);
        Assert.Equal(2f, back!.Entries["Remote:0"][1]);
        Assert.Equal("campplus-zh-en", back.Method);
    }

    [Fact]
    public async Task Absent_file_loads_null()
        => Assert.Null(await new ClusterEmbeddingsStore(PathFor()).LoadAsync(default));

    [Fact]
    public async Task Corrupt_file_loads_null_not_throw()
    {
        await File.WriteAllTextAsync(PathFor(), "{not json");
        Assert.Null(await new ClusterEmbeddingsStore(PathFor()).LoadAsync(default));
    }

    [Fact]
    public async Task Newer_schema_loads_null_not_throw()
    {
        await File.WriteAllTextAsync(PathFor(), "{\"schemaVersion\":99}");
        Assert.Null(await new ClusterEmbeddingsStore(PathFor()).LoadAsync(default));
    }

    [Fact]
    public async Task Delete_removes_file_and_is_idempotent()
    {
        var store = new ClusterEmbeddingsStore(PathFor());
        await store.SaveAsync(new ClusterEmbeddings(), default);
        store.Delete();
        Assert.False(File.Exists(PathFor()));
        store.Delete();   // no throw
    }

    [Fact]
    public void StoragePaths_layout()
    {
        var p = new StoragePaths(Path.Combine(_dir, "root"));
        Assert.EndsWith(Path.Combine("sessions", "s1", "embeddings.json"), p.EmbeddingsJson("s1"));
        Assert.EndsWith(Path.Combine("s1", "versions", "v2", "embeddings.json"), p.EmbeddingsJson("s1", "v2"));
        Assert.Equal(p.EmbeddingsJson("s1"), p.EmbeddingsJson("s1", LocalScribe.Core.Model.TranscriptVersions.Root));
        Assert.EndsWith(Path.Combine("people", "people.json"), p.PeopleJson);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter ClusterEmbeddingsStoreTests -v q`
Expected: compile errors.

- [ ] **Step 3: Implement**

`src/LocalScribe.Core/Model/ClusterEmbeddings.cs`:

```csharp
namespace LocalScribe.Core.Model;

/// <summary>embeddings.json - per-cluster mean speaker embeddings captured at diarise time
/// (voiceprint design 2026-07-25). DERIVED biometric data: rebuildable (re-diarise / embed op),
/// deletable by the voiceprint purge, never evidence. Keys are FULL post-remap clusterKeys
/// ("Remote:0") - written only after SpeakersMerge's collision remap is applied, so an entry can
/// never point at a different voice than speakers.json does.</summary>
public sealed record ClusterEmbeddings
{
    public int SchemaVersion { get; init; } = 1;
    public string Method { get; init; } = "";
    public DateTimeOffset ExtractedAtUtc { get; init; }
    public IReadOnlyDictionary<string, float[]> Entries { get; init; } = new Dictionary<string, float[]>();
}
```

`src/LocalScribe.Core/Storage/ClusterEmbeddingsStore.cs`:

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes a version-scoped embeddings.json. Derived data: absent, corrupt, or
/// forward-versioned files all load null (feature degrades to "no suggestions", never blocks).</summary>
public sealed class ClusterEmbeddingsStore
{
    public const int Version = 1;
    private readonly string _path;
    public ClusterEmbeddingsStore(string embeddingsJsonPath) => _path = embeddingsJsonPath;

    public Task SaveAsync(ClusterEmbeddings embeddings, CancellationToken ct)
        => JsonFile.WriteAsync(_path, embeddings with { SchemaVersion = Version }, ct);

    public async Task<ClusterEmbeddings?> LoadAsync(CancellationToken ct)
    {
        try
        {
            var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
            if (obj is null || SchemaGuard.ReadVersion(obj) > Version) return null;
            return await JsonFile.ReadAsync<ClusterEmbeddings>(_path, ct);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public void Delete() { if (File.Exists(_path)) File.Delete(_path); }
}
```

In `src/LocalScribe.Core/Storage/StoragePaths.cs`, after the `SpeakersJson(id, versionId)` line add:

```csharp
    /// <summary>Per-version cluster embeddings (voiceprint design 2026-07-25): DERIVED biometric
    /// data beside speakers.json - purge-deletable, never evidence.</summary>
    public string EmbeddingsJson(string id) => Path.Combine(SessionDir(id), "embeddings.json");
    public string EmbeddingsJson(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "embeddings.json");
```

and after `SearchIndexJson` add:

```csharp
    /// <summary>People registry (voiceprint design 2026-07-25): global person identities +
    /// voiceprint enrollments. USER data (not derived); enrollments are individually deletable.</summary>
    public string PeopleDir => Path.Combine(Root, "people");
    public string PeopleJson => Path.Combine(PeopleDir, "people.json");
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter ClusterEmbeddingsStoreTests -v q`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Model/ClusterEmbeddings.cs src/LocalScribe.Core/Storage/ClusterEmbeddingsStore.cs src/LocalScribe.Core/Storage/StoragePaths.cs tests/LocalScribe.Core.Tests/ClusterEmbeddingsStoreTests.cs
git commit -m "feat(voiceprint): embeddings.json model + store + paths"
```

---

### Task 5: People registry — records, ops, store, roster link

**Files:**
- Create: `src/LocalScribe.Core/Model/People.cs`
- Create: `src/LocalScribe.Core/Storage/PeopleStore.cs`
- Create: `src/LocalScribe.Core/People/PeopleRegistryOps.cs`
- Modify: `src/LocalScribe.Core/Model/Matter.cs` (RosterMember.PersonId)
- Test: `tests/LocalScribe.Core.Tests/PeopleRegistryTests.cs`

**Interfaces:**
- Produces: `VoiceprintEnrollment { Id, Embedding: float[], Method, SourceSessionId, SourceClusterKey, EnrolledAtUtc }`; `Person { Id, Name, Role?, Org?, CreatedUtc, Voiceprint: IReadOnlyList<VoiceprintEnrollment> }`; `PeopleRegistry { SchemaVersion=1, People: IReadOnlyList<Person> }`; `RosterMember.PersonId` (`string?`); `PeopleStore(string path)` with `SaveAsync`/`LoadAsync` (SchemaGuard: user data, newer-version THROWS like SpeakersStore); pure `PeopleRegistryOps`: `MaxEnrollmentsPerPerson=20`, `Enroll`, `RemoveEnrollment`, `DeleteVoiceprint(reg, personId)`, `RemovePerson`, `ClearAllVoiceprints`, `EnsurePerson(reg, name, newId, now) -> (PeopleRegistry, Person)` (exact-ordinal name match, else create), `FindByName(reg, name) -> Person?`.

- [ ] **Step 1: Write the failing tests** — `tests/LocalScribe.Core.Tests/PeopleRegistryTests.cs`:

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Storage;

public class PeopleRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lspeople").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static VoiceprintEnrollment E(string id) => new()
    {
        Id = id, Embedding = [1f], Method = "campplus-zh-en",
        SourceSessionId = "s1", SourceClusterKey = "Remote:0",
        EnrolledAtUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void EnsurePerson_creates_then_matches_exact_name()
    {
        var (reg, p1) = PeopleRegistryOps.EnsurePerson(new PeopleRegistry(), "Sarah Chen",
            () => "p1", DateTimeOffset.UnixEpoch);
        var (reg2, p2) = PeopleRegistryOps.EnsurePerson(reg, "Sarah Chen", () => "p2", DateTimeOffset.UnixEpoch);
        Assert.Equal("p1", p1.Id);
        Assert.Equal("p1", p2.Id);                       // matched, not re-created
        Assert.Single(reg2.People);
        Assert.Null(PeopleRegistryOps.FindByName(reg2, "sarah chen"));  // ordinal, not ci
    }

    [Fact]
    public void Enroll_appends_and_caps_at_20_evicting_oldest()
    {
        var (reg, p) = PeopleRegistryOps.EnsurePerson(new PeopleRegistry(), "A", () => "p1", DateTimeOffset.UnixEpoch);
        for (int i = 0; i < 25; i++) reg = PeopleRegistryOps.Enroll(reg, "p1", E($"e{i}"));
        var vp = reg.People.Single().Voiceprint;
        Assert.Equal(20, vp.Count);
        Assert.Equal("e5", vp[0].Id);                    // e0..e4 evicted
        Assert.Equal("e24", vp[^1].Id);
    }

    [Fact]
    public void Enroll_unknown_person_is_noop()
    {
        var reg = PeopleRegistryOps.Enroll(new PeopleRegistry(), "ghost", E("e1"));
        Assert.Empty(reg.People);
    }

    [Fact]
    public void Deletes_enrollment_voiceprint_and_person()
    {
        var (reg, _) = PeopleRegistryOps.EnsurePerson(new PeopleRegistry(), "A", () => "p1", DateTimeOffset.UnixEpoch);
        reg = PeopleRegistryOps.Enroll(reg, "p1", E("e1"));
        reg = PeopleRegistryOps.Enroll(reg, "p1", E("e2"));

        var afterOne = PeopleRegistryOps.RemoveEnrollment(reg, "p1", "e1");
        Assert.Single(afterOne.People.Single().Voiceprint);

        var afterVp = PeopleRegistryOps.DeleteVoiceprint(reg, "p1");
        Assert.Empty(afterVp.People.Single().Voiceprint);
        Assert.Equal("A", afterVp.People.Single().Name);   // person survives

        var afterPerson = PeopleRegistryOps.RemovePerson(reg, "p1");
        Assert.Empty(afterPerson.People);
    }

    [Fact]
    public void ClearAllVoiceprints_strips_every_enrollment_keeps_people()
    {
        var (reg, _) = PeopleRegistryOps.EnsurePerson(new PeopleRegistry(), "A", () => "p1", DateTimeOffset.UnixEpoch);
        (reg, _) = PeopleRegistryOps.EnsurePerson(reg, "B", () => "p2", DateTimeOffset.UnixEpoch);
        reg = PeopleRegistryOps.Enroll(reg, "p1", E("e1"));
        reg = PeopleRegistryOps.Enroll(reg, "p2", E("e2"));
        var cleared = PeopleRegistryOps.ClearAllVoiceprints(reg);
        Assert.Equal(2, cleared.People.Count);
        Assert.All(cleared.People, p => Assert.Empty(p.Voiceprint));
    }

    [Fact]
    public async Task Store_round_trips_and_rejects_newer_schema()
    {
        var path = Path.Combine(_dir, "people.json");
        var store = new PeopleStore(path);
        var (reg, _) = PeopleRegistryOps.EnsurePerson(new PeopleRegistry(), "A", () => "p1", DateTimeOffset.UnixEpoch);
        await store.SaveAsync(PeopleRegistryOps.Enroll(reg, "p1", E("e1")), default);
        var back = await store.LoadAsync(default);
        Assert.Equal(1f, back!.People.Single().Voiceprint.Single().Embedding[0]);

        await File.WriteAllTextAsync(path, "{\"schemaVersion\":99}");
        await Assert.ThrowsAsync<NotSupportedException>(() => store.LoadAsync(default));
    }

    [Fact]
    public void RosterMember_carries_optional_PersonId()
    {
        var m = new RosterMember { Id = "r1", Name = "A", PersonId = "p1" };
        Assert.Equal("p1", m.PersonId);
        Assert.Null(new RosterMember().PersonId);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter PeopleRegistryTests -v q`
Expected: compile errors.

- [ ] **Step 3: Implement**

`src/LocalScribe.Core/Model/People.cs`:

```csharp
namespace LocalScribe.Core.Model;

/// <summary>One captured voiceprint sample (voiceprint design 2026-07-25). The vector is COPIED
/// from the source session's embeddings.json at enrollment, so per-session purges and re-diarises
/// never invalidate it. Full provenance kept for the People UI and for targeted deletion.</summary>
public sealed record VoiceprintEnrollment
{
    public string Id { get; init; } = "";
    public float[] Embedding { get; init; } = [];
    public string Method { get; init; } = "";
    public string SourceSessionId { get; init; } = "";
    public string SourceClusterKey { get; init; } = "";
    public DateTimeOffset EnrolledAtUtc { get; init; }
}

/// <summary>A globally-known person (voiceprint design 2026-07-25): the identity anchor
/// voiceprints attach to. Matter RosterMembers link here via RosterMember.PersonId.</summary>
public sealed record Person
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Role { get; init; }
    public string? Org { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public IReadOnlyList<VoiceprintEnrollment> Voiceprint { get; init; } = [];
}

/// <summary>people\people.json - the People registry. USER data (never derived/rebuildable):
/// enrollments are deletable individually, per-person, or via the global purge.</summary>
public sealed record PeopleRegistry
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<Person> People { get; init; } = [];
}
```

In `src/LocalScribe.Core/Model/Matter.cs`, extend `RosterMember`:

```csharp
public sealed record RosterMember
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Role { get; init; }

    /// <summary>Optional link to a global Person (voiceprint design 2026-07-25). When set, this
    /// roster member's confirmed clusters enroll that person's voiceprint and matter-scoped
    /// matching includes them. Nullable + additive: absent in existing matter.json files.</summary>
    public string? PersonId { get; init; }
}
```

`src/LocalScribe.Core/Storage/PeopleStore.cs` (mirror `SpeakersStore` exactly — user data, newer schema throws):

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes people\people.json (voiceprint design 2026-07-25). Absent until the
/// first person is created.</summary>
public sealed class PeopleStore
{
    public const int Version = 1;
    private readonly string _path;
    public PeopleStore(string peopleJsonPath) => _path = peopleJsonPath;

    public Task SaveAsync(PeopleRegistry registry, CancellationToken ct)
        => JsonFile.WriteAsync(_path, registry with { SchemaVersion = Version }, ct);

    public async Task<PeopleRegistry?> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "people.json");
        return await JsonFile.ReadAsync<PeopleRegistry>(_path, ct);
    }
}
```

`src/LocalScribe.Core/People/PeopleRegistryOps.cs`:

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.Core.People;

/// <summary>Pure transformations over <see cref="PeopleRegistry"/> (voiceprint design
/// 2026-07-25). All methods return a new registry; inputs are never mutated. Person lookup by
/// name is EXACT ordinal - the same rule the Split dialog uses to match candidate names.</summary>
public static class PeopleRegistryOps
{
    public const int MaxEnrollmentsPerPerson = 20;

    public static Person? FindByName(PeopleRegistry reg, string name)
        => reg.People.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public static (PeopleRegistry Registry, Person Person) EnsurePerson(
        PeopleRegistry reg, string name, Func<string> newId, DateTimeOffset now)
    {
        var existing = FindByName(reg, name);
        if (existing is not null) return (reg, existing);
        var person = new Person { Id = newId(), Name = name, CreatedUtc = now };
        return (reg with { People = [.. reg.People, person] }, person);
    }

    public static PeopleRegistry Enroll(PeopleRegistry reg, string personId, VoiceprintEnrollment e)
        => Update(reg, personId, p =>
        {
            var list = new List<VoiceprintEnrollment>(p.Voiceprint) { e };
            while (list.Count > MaxEnrollmentsPerPerson) list.RemoveAt(0);   // FIFO eviction
            return p with { Voiceprint = list };
        });

    public static PeopleRegistry RemoveEnrollment(PeopleRegistry reg, string personId, string enrollmentId)
        => Update(reg, personId, p => p with
        { Voiceprint = p.Voiceprint.Where(e => e.Id != enrollmentId).ToList() });

    public static PeopleRegistry DeleteVoiceprint(PeopleRegistry reg, string personId)
        => Update(reg, personId, p => p with { Voiceprint = [] });

    public static PeopleRegistry RemovePerson(PeopleRegistry reg, string personId)
        => reg with { People = reg.People.Where(p => p.Id != personId).ToList() };

    public static PeopleRegistry ClearAllVoiceprints(PeopleRegistry reg)
        => reg with { People = reg.People.Select(p => p with { Voiceprint = [] }).ToList() };

    private static PeopleRegistry Update(PeopleRegistry reg, string personId, Func<Person, Person> f)
        => reg with { People = reg.People.Select(p => p.Id == personId ? f(p) : p).ToList() };
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter PeopleRegistryTests -v q`
Expected: PASS (7 tests). Also run `dotnet test tests/LocalScribe.Core.Tests --filter Matter -v q` to confirm no matter.json regression.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Model/People.cs src/LocalScribe.Core/Model/Matter.cs src/LocalScribe.Core/Storage/PeopleStore.cs src/LocalScribe.Core/People/PeopleRegistryOps.cs tests/LocalScribe.Core.Tests/PeopleRegistryTests.cs
git commit -m "feat(voiceprint): People registry + roster PersonId link"
```

---

### Task 6: Suggestion provenance — Speakers, commit, merge

**Files:**
- Modify: `src/LocalScribe.Core/Model/Speakers.cs`
- Modify: `src/LocalScribe.Core/Diarisation/DiarisationCommit.cs`
- Modify: `src/LocalScribe.Core/Diarisation/SpeakersMerge.cs`
- Test: `tests/LocalScribe.Core.Tests/SpeakersMergeTests.cs` (append)

**Interfaces:**
- Consumes: existing `Speakers`, `DiarisationCommit`, `SpeakersMerge.Merge`.
- Produces: `SuggestionProvenanceEntry(string PersonId, double Score, DateTimeOffset AcceptedAtUtc)` (in `LocalScribe.Core.Model`); `Speakers.SuggestionProvenance: IReadOnlyDictionary<string, SuggestionProvenanceEntry>` (clusterKey-keyed, default empty — additive, SchemaVersion stays 1); `DiarisationCommit` gains optional 6th positional param `IReadOnlyDictionary<string, SuggestionProvenanceEntry>? Provenance = null`. Merge semantics: provenance keys go through the same FreshKeyRemap as Names; existing provenance entries for a re-diarised source are DROPPED except for a pinned clusterKey, which keeps its entry verbatim (mirrors the Names pin exemption - a pinned key's identity is not re-asserted by a fresh run); other sources' entries pass through; commit provenance is applied last (never onto a pinned clusterKey, same guard as Names).

- [ ] **Step 1: Write the failing tests** — append to `tests/LocalScribe.Core.Tests/SpeakersMergeTests.cs` (match its existing helper style; if it has builders for commits, reuse them, else construct inline):

```csharp
[Fact]
public void Provenance_applies_remaps_and_drops_on_rediarise()
{
    var when = DateTimeOffset.UnixEpoch;
    // Existing: pinned seq 5 -> Remote:0 named+provenance'd.
    var existing = new Speakers
    {
        Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["5"] = "Remote:0" } },
        Pinned = new Dictionary<string, List<string>> { ["Remote"] = ["5"] },
        Names = new Dictionary<string, string> { ["Remote:0"] = "Sarah Chen" },
        SuggestionProvenance = new Dictionary<string, SuggestionProvenanceEntry>
            { ["Remote:0"] = new("p-sarah", 0.91, when) },
    };
    // Fresh run: cluster 0 again (collides with protected Remote:0) accepted as p-bob.
    var commit = new DiarisationCommit(
        [SourceKind.Remote],
        new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["Remote"] = new Dictionary<string, string> { ["7"] = "Remote:0" } },
        new Dictionary<string, string> { ["Remote:0"] = "Bob" },
        "m", when,
        new Dictionary<string, SuggestionProvenanceEntry> { ["Remote:0"] = new("p-bob", 0.8, when) });

    var result = SpeakersMerge.Merge(existing, commit, []);

    // Fresh Remote:0 was remapped (collision with the pinned key)...
    var newKey = result.FreshKeyRemap["Remote:0"];
    // ...pinned key keeps ITS name; provenance for the re-diarised source was dropped for the
    // pinned key (identity is re-asserted per run) and applied under the REMAPPED key.
    Assert.Equal("Sarah Chen", result.Speakers.Names["Remote:0"]);
    Assert.False(result.Speakers.SuggestionProvenance.ContainsKey("Remote:0"));
    Assert.Equal("p-bob", result.Speakers.SuggestionProvenance[newKey].PersonId);
}

[Fact]
public void Provenance_for_other_source_passes_through()
{
    var when = DateTimeOffset.UnixEpoch;
    var existing = new Speakers
    {
        SuggestionProvenance = new Dictionary<string, SuggestionProvenanceEntry>
            { ["Local:0"] = new("p-me", 0.9, when) },
    };
    var commit = new DiarisationCommit([SourceKind.Remote],
        new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["Remote"] = new Dictionary<string, string> { ["1"] = "Remote:0" } },
        new Dictionary<string, string> { ["Remote:0"] = "X" }, "m", when);

    var result = SpeakersMerge.Merge(existing, commit, []);
    Assert.Equal("p-me", result.Speakers.SuggestionProvenance["Local:0"].PersonId);
}

[Fact]
public void Null_commit_provenance_leaves_map_wellformed()
{
    var commit = new DiarisationCommit([SourceKind.Remote],
        new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["Remote"] = new Dictionary<string, string> { ["1"] = "Remote:0" } },
        new Dictionary<string, string> { ["Remote:0"] = "X" }, "m", DateTimeOffset.UnixEpoch);
    var result = SpeakersMerge.Merge(null, commit, []);
    Assert.Empty(result.Speakers.SuggestionProvenance);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter SpeakersMergeTests -v q`
Expected: compile errors (`SuggestionProvenance`, 6-arg commit).

- [ ] **Step 3: Implement**

In `src/LocalScribe.Core/Model/Speakers.cs` add inside the record (and the entry record below it):

```csharp
    /// <summary>Accepted voiceprint-suggestion provenance (voiceprint design 2026-07-25):
    /// clusterKey -> who was suggested + score + when accepted. Recorded ONLY on accept, so an
    /// accepted match is never indistinguishable from a hand-typed name. Cleared by the
    /// voiceprint purge; additive - SchemaVersion stays 1.</summary>
    public IReadOnlyDictionary<string, SuggestionProvenanceEntry> SuggestionProvenance { get; init; }
        = new Dictionary<string, SuggestionProvenanceEntry>();
```

```csharp
/// <summary>One accepted voiceprint suggestion (voiceprint design 2026-07-25).</summary>
public sealed record SuggestionProvenanceEntry(string PersonId, double Score, DateTimeOffset AcceptedAtUtc);
```

In `src/LocalScribe.Core/Diarisation/DiarisationCommit.cs` replace the record declaration:

```csharp
public sealed record DiarisationCommit(
    IReadOnlyList<SourceKind> Sources,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Assignments, // "Local"/"Remote" -> seq -> clusterKey
    IReadOnlyDictionary<string, string> Names,                                    // clusterKey -> displayName
    string Method,
    DateTimeOffset DiarisedAtUtc,
    IReadOnlyDictionary<string, SuggestionProvenanceEntry>? Provenance = null);   // clusterKey -> accepted suggestion
```

(add `using LocalScribe.Core.Model;`.)

In `src/LocalScribe.Core/Diarisation/SpeakersMerge.cs`:

(a) In the remap-application block (`if (remap.Count > 0)`), also remap provenance keys — after the `commitNames` remapping add:

```csharp
        var commitProvenance = commit.Provenance;
        if (remap.Count > 0 && commitProvenance is not null)
        {
            var remappedProv = new Dictionary<string, SuggestionProvenanceEntry>();
            foreach (var (ck, entry) in commitProvenance)
                remappedProv[remap.TryGetValue(ck, out var nk) ? nk : ck] = entry;
            commitProvenance = remappedProv;
        }
```

(hoist `var commitProvenance = commit.Provenance;` above the `if (remap.Count > 0)` block and put the provenance remap inside it, mirroring `commitNames`.)

(b) After the per-source Names-drop loop, drop this source's provenance (ALL of it — identity is re-asserted per run; unlike Names there is no pin exemption because the accept event, not the label, is what provenance records). Inside the `foreach (var sourceKey in reSources)` loop, after the Names removal loop, add:

```csharp
            foreach (var ck in provenance.Keys.ToList())
                if (ck.StartsWith(sourceKey + ":", StringComparison.Ordinal))
                    provenance.Remove(ck);
```

with `var provenance = new Dictionary<string, SuggestionProvenanceEntry>(existing.SuggestionProvenance);` declared next to `var names = ...`.

(c) After the commit-Names application loop, apply commit provenance with the same pinned guard:

```csharp
        if (commitProvenance is not null)
            foreach (var (ck, entry) in commitProvenance)
                if (!pinnedClusterKeys.Contains(ck) &&
                    reSources.Any(src => ck.StartsWith(src + ":", StringComparison.Ordinal)))
                    provenance[ck] = entry;
```

(d) Include `SuggestionProvenance = provenance,` in the final `existing with { ... }`.

- [ ] **Step 4: Run the full merge suite**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter SpeakersMergeTests -v q`
Expected: PASS — all pre-existing merge tests must still pass (provenance is additive).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Model/Speakers.cs src/LocalScribe.Core/Diarisation/DiarisationCommit.cs src/LocalScribe.Core/Diarisation/SpeakersMerge.cs tests/LocalScribe.Core.Tests/SpeakersMergeTests.cs
git commit -m "feat(voiceprint): suggestion provenance through commit + merge"
```

---

### Task 7: VoiceprintMatcher

**Files:**
- Create: `src/LocalScribe.Core/People/VoiceprintMatcher.cs`
- Test: `tests/LocalScribe.Core.Tests/VoiceprintMatcherTests.cs`

**Interfaces:**
- Consumes: `VoiceprintMath.Cosine`, `Person`/`VoiceprintEnrollment` (Task 5).
- Produces: `VoiceprintSuggestion(string PersonId, string PersonName, double Score)`; `VoiceprintMatcher.SuggestThreshold = 0.55`, `RunnerUpMargin = 0.05`; `VoiceprintMatcher.Suggest(IReadOnlyDictionary<string, float[]> clusterEmbeddings, string method, IReadOnlyList<Person> candidates) -> IReadOnlyDictionary<string, VoiceprintSuggestion>` (clusterKey-keyed; at most one suggestion per cluster; person score = max cosine over that person's same-method enrollments; suggest only if score >= threshold AND (no runner-up person OR score - runnerUp >= margin)).

- [ ] **Step 1: Write the failing tests** — `tests/LocalScribe.Core.Tests/VoiceprintMatcherTests.cs`:

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.People;

public class VoiceprintMatcherTests
{
    private const string M = "campplus-zh-en";

    private static Person P(string id, string name, params float[][] vecs) => new()
    {
        Id = id, Name = name,
        Voiceprint = vecs.Select((v, i) => new VoiceprintEnrollment
        { Id = $"{id}-e{i}", Embedding = v, Method = M }).ToList(),
    };

    [Fact]
    public void Clear_match_is_suggested()
    {
        var suggestions = VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] }, M,
            [P("p1", "Sarah", [1f, 0.1f]), P("p2", "Bob", [0f, 1f])]);
        Assert.Equal("p1", suggestions["Remote:0"].PersonId);
        Assert.Equal("Sarah", suggestions["Remote:0"].PersonName);
        Assert.True(suggestions["Remote:0"].Score > 0.9);
    }

    [Fact]
    public void Below_threshold_is_not_suggested()
    {
        var s = VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] }, M,
            [P("p1", "A", [0.5f, 0.9f])]);   // cosine ~0.486
        Assert.Empty(s);
    }

    [Fact]
    public void Confusable_runners_up_suppress_the_suggestion()
    {
        // Two people nearly identical to the probe: margin < 0.05 -> no suggestion.
        var s = VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] }, M,
            [P("p1", "A", [1f, 0.01f]), P("p2", "B", [1f, 0.02f])]);
        Assert.Empty(s);
    }

    [Fact]
    public void Wrong_method_enrollments_are_skipped()
    {
        var stale = P("p1", "A", [1f, 0f]) with
        {
            Voiceprint = [new VoiceprintEnrollment { Id = "e", Embedding = [1f, 0f], Method = "other" }],
        };
        Assert.Empty(VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] }, M, [stale]));
    }

    [Fact]
    public void Person_score_is_max_over_enrollments()
    {
        var s = VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] }, M,
            [P("p1", "A", [0f, 1f], [1f, 0f])]);   // second enrollment is the match
        Assert.Equal("p1", s["Remote:0"].PersonId);
    }

    [Fact]
    public void Empty_pool_or_no_embeddings_yields_empty()
    {
        Assert.Empty(VoiceprintMatcher.Suggest(new Dictionary<string, float[]>(), M, [P("p1", "A", [1f])]));
        Assert.Empty(VoiceprintMatcher.Suggest(new Dictionary<string, float[]> { ["Remote:0"] = [1f] }, M, []));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter VoiceprintMatcherTests -v q`
Expected: compile errors.

- [ ] **Step 3: Implement** — `src/LocalScribe.Core/People/VoiceprintMatcher.cs`:

```csharp
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.People;

/// <summary>One advisory match for a diarised cluster (voiceprint design 2026-07-25). A UI hint,
/// never an identification claim; the app never auto-assigns from it.</summary>
public sealed record VoiceprintSuggestion(string PersonId, string PersonName, double Score);

/// <summary>Pure matcher: cluster embeddings vs the candidate People pool. Thresholds are named
/// constants pending tuning against real audio (smoke runbook).</summary>
public static class VoiceprintMatcher
{
    public const double SuggestThreshold = 0.55;
    public const double RunnerUpMargin = 0.05;

    public static IReadOnlyDictionary<string, VoiceprintSuggestion> Suggest(
        IReadOnlyDictionary<string, float[]> clusterEmbeddings,
        string method,
        IReadOnlyList<Person> candidates)
    {
        var result = new Dictionary<string, VoiceprintSuggestion>();
        foreach (var (clusterKey, probe) in clusterEmbeddings)
        {
            VoiceprintSuggestion? best = null;
            double runnerUp = double.MinValue;
            foreach (var person in candidates)
            {
                double score = double.MinValue;
                foreach (var e in person.Voiceprint)
                    if (string.Equals(e.Method, method, StringComparison.Ordinal))
                        score = Math.Max(score, VoiceprintMath.Cosine(probe, e.Embedding));
                if (score == double.MinValue) continue;      // no comparable enrollment

                if (best is null || score > best.Score)
                {
                    if (best is not null) runnerUp = Math.Max(runnerUp, best.Score);
                    best = new VoiceprintSuggestion(person.Id, person.Name, score);
                }
                else runnerUp = Math.Max(runnerUp, score);
            }
            if (best is null || best.Score < SuggestThreshold) continue;
            if (runnerUp != double.MinValue && best.Score - runnerUp < RunnerUpMargin) continue;
            result[clusterKey] = best;
        }
        return result;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter VoiceprintMatcherTests -v q`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/People/VoiceprintMatcher.cs tests/LocalScribe.Core.Tests/VoiceprintMatcherTests.cs
git commit -m "feat(voiceprint): pure matcher with threshold + margin"
```

---

### Task 8: Engine passthrough — request/result, helper embed seam

**Files:**
- Modify: `src/LocalScribe.Core/Diarisation/IDiarisationEngine.cs`
- Modify: `src/LocalScribe.Core/Diarisation/IDiarisationHelper.cs`
- Modify: `src/LocalScribe.Core/Diarisation/SherpaHelperDiariser.cs`
- Modify: `src/LocalScribe.App/Services/ProcessDiarisationHelper.cs`
- Test: `tests/LocalScribe.Core.Tests/SherpaHelperDiariserTests.cs` (append + extend FakeHelper)

**Interfaces:**
- Consumes: Task 1 wire records.
- Produces: `DiarisationRequest` gains `bool EmitEmbeddings = false`; `DiarisationResult` gains `IReadOnlyDictionary<string, float[]>? ClusterEmbeddings = null, string? EmbeddingMethod = null`; `IDiarisationHelper` gains `Task<int> RunEmbedAsync(EmbedJob job, Action<string> onStdoutLine, CancellationToken ct);`; new `IEmbeddingEngine { Task<EmbedResult> EmbedAsync(EmbedRequest request, CancellationToken ct); }` with `EmbedRequest(string FlacPath, IReadOnlyList<EmbedRange> Ranges, string EmbeddingModelPath)` and `EmbedResult(float[] Embedding, string Method)`; `SherpaHelperDiariser` implements both `IDiarisationEngine` and `IEmbeddingEngine`.

- [ ] **Step 1: Write the failing tests** — in `tests/LocalScribe.Core.Tests/SherpaHelperDiariserTests.cs`, add to `FakeHelper`:

```csharp
        public EmbedJob? LastEmbedJob { get; private set; }

        public async Task<int> RunEmbedAsync(EmbedJob job, Action<string> onStdoutLine, CancellationToken ct)
        {
            LastEmbedJob = job;
            foreach (var l in _lines) { ct.ThrowIfCancellationRequested(); onStdoutLine(l); await Task.Yield(); }
            return _exit;
        }

        public DiarisationJob? LastJob { get; private set; }
```

and record the job in the existing `RunAsync` first line: `LastJob = job;`. Then append tests:

```csharp
    [Fact]
    public async Task EmitEmbeddings_flows_to_job_and_result()
    {
        var helper = new FakeHelper(0,
            "{\"segments\":[{\"startMs\":0,\"endMs\":1000,\"cluster\":0}],\"clusterCount\":1,\"method\":\"m\"," +
            "\"clusterEmbeddings\":{\"0\":[0.5,0.5]},\"embeddingMethod\":\"campplus-zh-en\"}");
        var req = new DiarisationRequest("r.flac", SourceKind.Remote, "s.onnx", "e.onnx", null, EmitEmbeddings: true);

        var result = await new SherpaHelperDiariser(helper).DiariseAsync(req, new Progress<double>(_ => { }), default);

        Assert.True(helper.LastJob!.EmitEmbeddings);
        Assert.Equal(0.5f, result.ClusterEmbeddings!["0"][0]);
        Assert.Equal("campplus-zh-en", result.EmbeddingMethod);
    }

    [Fact]
    public async Task Result_without_embeddings_stays_null_backcompat()
    {
        var helper = new FakeHelper(0,
            "{\"segments\":[],\"clusterCount\":0,\"method\":\"m\"}");
        var req = new DiarisationRequest("r.flac", SourceKind.Remote, "s.onnx", "e.onnx", null, EmitEmbeddings: true);
        var result = await new SherpaHelperDiariser(helper).DiariseAsync(req, new Progress<double>(_ => { }), default);
        Assert.Null(result.ClusterEmbeddings);   // old helper: silent degrade, no throw
    }

    [Fact]
    public async Task EmbedAsync_parses_embedding_result()
    {
        var helper = new FakeHelper(0, "{\"embedding\":[0.25,0.75],\"method\":\"campplus-zh-en\"}");
        var result = await new SherpaHelperDiariser(helper).EmbedAsync(
            new EmbedRequest("r.flac", [new EmbedRange(0, 1000)], "e.onnx"), default);
        Assert.Equal(0.75f, result.Embedding[1]);
        Assert.Equal("embed", helper.LastEmbedJob!.Op);
    }

    [Fact]
    public async Task EmbedAsync_error_line_throws_DiarisationException()
    {
        var helper = new FakeHelper(1, "{\"error\":\"BAD_AUDIO\",\"detail\":\"nope\"}");
        var ex = await Assert.ThrowsAsync<DiarisationException>(
            () => new SherpaHelperDiariser(helper).EmbedAsync(
                new EmbedRequest("r.flac", [new EmbedRange(0, 1000)], "e.onnx"), default));
        Assert.Equal(DiarisationErrorCode.BadAudio, ex.Code);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter SherpaHelperDiariserTests -v q`
Expected: compile errors.

- [ ] **Step 3: Implement**

`IDiarisationEngine.cs` — replace the two records and append the embed seam:

```csharp
public sealed record DiarisationRequest(
    string FlacPath, SourceKind Source,
    string SegmentationModelPath, string EmbeddingModelPath,
    int? ForcedClusterCount,
    bool EmitEmbeddings = false);

public sealed record DiarisationResult(
    IReadOnlyList<DiarisedSegment> Segments, int ClusterCount, string Method,
    IReadOnlyDictionary<string, float[]>? ClusterEmbeddings = null,   // cluster id ("0") -> vector
    string? EmbeddingMethod = null);

/// <summary>Backfill embedding extraction (voiceprint design 2026-07-25): mean speaker embedding
/// over explicit ranges of a FLAC leg, for enrolling from sessions diarised before embeddings.json
/// existed. Same helper process as diarisation.</summary>
public interface IEmbeddingEngine
{
    Task<EmbedResult> EmbedAsync(EmbedRequest request, CancellationToken ct);
}

public sealed record EmbedRequest(
    string FlacPath, IReadOnlyList<EmbedRange> Ranges, string EmbeddingModelPath);

public sealed record EmbedResult(float[] Embedding, string Method);
```

`IDiarisationHelper.cs` — add to the interface:

```csharp
    Task<int> RunEmbedAsync(EmbedJob job, Action<string> onStdoutLine, CancellationToken ct);
```

`SherpaHelperDiariser.cs` — declare `: IDiarisationEngine, IEmbeddingEngine`; pass the flag in the job; map embeddings through:

```csharp
        var job = new DiarisationJob(request.FlacPath, request.Source.ToString(),
            request.SegmentationModelPath, request.EmbeddingModelPath, request.ForcedClusterCount,
            request.EmitEmbeddings);
```

and at the end of `DiariseAsync`:

```csharp
        var segments = result.Segments
            .Select(s => new DiarisedSegment(s.StartMs, s.EndMs, s.Cluster)).ToList();
        return new DiarisationResult(segments, result.ClusterCount, result.Method,
            result.ClusterEmbeddings, result.EmbeddingMethod);
```

then add:

```csharp
    public async Task<EmbedResult> EmbedAsync(EmbedRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var job = new EmbedJob("embed", request.FlacPath, request.Ranges, request.EmbeddingModelPath);

        EmbedResultPayload? result = null;
        DiarisationErrorPayload? error = null;
        void OnLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try
            {
                if (line.Contains("\"error\""))
                    error = JsonSerializer.Deserialize<DiarisationErrorPayload>(line, DiarisationJson.Options);
                else if (line.Contains("\"embedding\""))
                    result = JsonSerializer.Deserialize<EmbedResultPayload>(line, DiarisationJson.Options);
            }
            catch (JsonException) { /* malformed line -> terminal checks classify as HelperCrash */ }
        }

        int exit = await helper.RunEmbedAsync(job, OnLine, ct);
        if (error is not null)
            throw new DiarisationException(MapError(error.Error), error.Detail ?? error.Error);
        if (exit != 0 || result is null)
            throw new DiarisationException(DiarisationErrorCode.HelperCrash,
                $"embed helper exited {exit} without a result");
        return new EmbedResult(result.Embedding, result.Method);
    }
```

Also fix `MapError` to map `"BAD_AUDIO"` if it is currently in the switch (it is — no change needed).

`ProcessDiarisationHelper.cs` — implement `RunEmbedAsync` by factoring the existing process-launch body into a private `RunProcessAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct)` and having both interface methods serialize their record with `DiarisationJson.Options` and call it. Keep the kill-process-tree-on-cancel registration identical.

- [ ] **Step 4: Run tests + build the App**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter SherpaHelperDiariserTests -v q` then `dotnet build src/LocalScribe.App -v q`
Expected: tests PASS; App builds (ProcessDiarisationHelper implements the new member). If other fakes implement `IDiarisationHelper` in tests, add the one-line `RunEmbedAsync` there too.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Diarisation/IDiarisationEngine.cs src/LocalScribe.Core/Diarisation/IDiarisationHelper.cs src/LocalScribe.Core/Diarisation/SherpaHelperDiariser.cs src/LocalScribe.App/Services/ProcessDiarisationHelper.cs tests/LocalScribe.Core.Tests/SherpaHelperDiariserTests.cs
git commit -m "feat(voiceprint): engine passthrough + embed seam"
```

---

### Task 9: Persistence — embeddings.json in SaveDiarisationAsync + purge

**Files:**
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs`
- Test: `tests/LocalScribe.App.Tests/MaintenanceServiceVoiceprintTests.cs` (new; reuse the existing MaintenanceService test fixture helpers if present — check `tests/LocalScribe.App.Tests` for an existing `MaintenanceService*Tests.cs` and mirror its setup)

**Interfaces:**
- Consumes: `ClusterEmbeddingsStore`, `PeopleStore`, `PeopleRegistryOps`, `SpeakersStore`, `SpeakersMerge` result remap.
- Produces: `SaveDiarisationAsync` gains one optional parameter (before `ct`): `IReadOnlyDictionary<string, DiarisationResult>? resultsBySource = null` — when non-null and a result carries `ClusterEmbeddings`, writes `embeddings.json` for the commit's `versionId`: entries keyed `"{Source}:{clusterId}"` translated through `result.FreshKeyRemap`, merged over any existing entries belonging to sources NOT in this commit; `Method` from the first non-null `EmbeddingMethod`; `ExtractedAtUtc = time.GetUtcNow()`. Also produces `Task<int> PurgeVoiceprintDataAsync(CancellationToken ct)`: for every session dir, under that session's gate, deletes `embeddings.json` in the root version and every `versions\*` dir, and rewrites any `speakers.json` whose `SuggestionProvenance` is non-empty with an empty map; then clears all People enrollments via `PeopleStore`; returns the count of sessions touched. NEVER touches audio, transcripts, or Names.

- [ ] **Step 1: Write the failing tests.** Mirror the existing MaintenanceService test setup (temp `StoragePaths` root + real stores). Core scenarios:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Storage;

public class MaintenanceServiceVoiceprintTests
{
    // Use the same construction helpers as the existing MaintenanceService tests in this
    // project (temp root, minimal session.json/meta.json seed). Scenarios:

    [Fact]
    public async Task Save_writes_embeddings_json_with_remapped_keys()
    {
        // Seed: session with pinned Remote:0 (so a fresh Remote:0 remaps to Remote:1).
        // Call SaveDiarisationAsync with a commit re-diarising Remote and resultsBySource
        // where Remote's DiarisationResult has ClusterEmbeddings {"0": [1,2]} and method set.
        // Assert: embeddings.json exists for the version; Entries key is "Remote:1" (remapped),
        // value [1,2]; Method == "campplus-zh-en".
    }

    [Fact]
    public async Task Save_preserves_other_sources_embeddings()
    {
        // Seed embeddings.json with {"Local:0": [9]}. Re-diarise Remote only with embeddings.
        // Assert: "Local:0" entry survives; "Remote:0" entry added.
    }

    [Fact]
    public async Task Save_without_results_leaves_embeddings_untouched()
    {
        // Seed embeddings.json; call SaveDiarisationAsync with resultsBySource: null.
        // Assert the file's content is unchanged (old-helper degrade path).
    }

    [Fact]
    public async Task Purge_deletes_embeddings_provenance_and_enrollments_only()
    {
        // Seed: session with embeddings.json (root + a versions\v2 copy), speakers.json with
        // one Name + one SuggestionProvenance entry, people.json with one enrolled person,
        // transcript.jsonl with content.
        // Call PurgeVoiceprintDataAsync.
        // Assert: both embeddings.json gone; speakers.json Names UNCHANGED and
        // SuggestionProvenance empty; person still exists with 0 enrollments;
        // transcript.jsonl byte-identical.
    }
}
```

Write these as real tests against the actual fixture pattern found in the existing App.Tests file (the plan intentionally specifies behavior, and the executor copies the concrete seed helpers from the neighbouring test class — they already exist for SaveDiarisationAsync).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.App.Tests --filter MaintenanceServiceVoiceprintTests -v q`
Expected: compile errors (new parameter/method missing).

- [ ] **Step 3: Implement in `MaintenanceService.cs`.**

(a) Change the signature:

```csharp
    public async Task<IReadOnlyDictionary<string, string>> SaveDiarisationAsync(
        string sessionId, DiarisationCommit commit, string versionId,
        IReadOnlyDictionary<string, string>? participantClusterKeys,
        IReadOnlyDictionary<string, DiarisationResult>? resultsBySource,
        CancellationToken ct)
```

Keep a 5-arg overload delegating with `resultsBySource: null` so existing callers/tests compile:

```csharp
    public Task<IReadOnlyDictionary<string, string>> SaveDiarisationAsync(
        string sessionId, DiarisationCommit commit, string versionId,
        IReadOnlyDictionary<string, string>? participantClusterKeys, CancellationToken ct)
        => SaveDiarisationAsync(sessionId, commit, versionId, participantClusterKeys, null, ct);
```

(b) Inside the gate, after step "1b" (participant ownership) and before step 2, write embeddings:

```csharp
            // 1c) per-cluster embeddings (voiceprint design 2026-07-25): DERIVED sidecar, keyed
            //     by the keys that actually landed in speakers.json (remap applied). Sources not
            //     in this commit keep their existing entries. Old helper / no embeddings -> the
            //     file is left exactly as-is (suggestions degrade silently).
            if (resultsBySource is not null &&
                resultsBySource.Values.Any(r => r.ClusterEmbeddings is { Count: > 0 }))
            {
                var embStore = new ClusterEmbeddingsStore(paths.EmbeddingsJson(sessionId, versionId));
                var existingEmb = await embStore.LoadAsync(inner);
                var entries = new Dictionary<string, float[]>();
                var rePrefixesEmb = commit.Sources.Select(s => s.ToString() + ":").ToList();
                if (existingEmb is not null)
                    foreach (var (k, v) in existingEmb.Entries)
                        if (!rePrefixesEmb.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
                            entries[k] = v;
                string method = "";
                foreach (var (sourceKey, dr) in resultsBySource)
                {
                    if (dr.ClusterEmbeddings is null) continue;
                    method = dr.EmbeddingMethod ?? method;
                    foreach (var (clusterId, vec) in dr.ClusterEmbeddings)
                    {
                        var rawKey = $"{sourceKey}:{clusterId}";
                        var finalKey = result.FreshKeyRemap.TryGetValue(rawKey, out var nk) ? nk : rawKey;
                        entries[finalKey] = vec;
                    }
                }
                await embStore.SaveAsync(new ClusterEmbeddings
                { Method = method, ExtractedAtUtc = time.GetUtcNow(), Entries = entries }, inner);
            }
```

(`resultsBySource` keys are source strings `"Local"`/`"Remote"` — the same keys as `commit.Assignments`.)

(c) Add the purge (new public method on MaintenanceService; `using LocalScribe.Core.People;`):

```csharp
    /// <summary>Global voiceprint purge (voiceprint design 2026-07-25): deletes every session's
    /// embeddings.json (all versions), clears every SuggestionProvenance map, and strips all
    /// People enrollments. Deletes ONLY derived biometric data - audio, transcripts, and speaker
    /// NAMES are never touched (evidentiary firewall). Returns sessions touched.</summary>
    public async Task<int> PurgeVoiceprintDataAsync(CancellationToken ct)
    {
        int touched = 0;
        if (Directory.Exists(paths.SessionsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(paths.SessionsDir))
            {
                var sessionId = Path.GetFileName(dir);
                bool any = await RunForSessionAsync(sessionId, async inner =>
                {
                    bool didAny = false;
                    var versionIds = new List<string> { TranscriptVersions.Root };
                    var versionsDir = paths.VersionsDir(sessionId);
                    if (Directory.Exists(versionsDir))
                        versionIds.AddRange(Directory.EnumerateDirectories(versionsDir).Select(Path.GetFileName)!);
                    foreach (var versionId in versionIds)
                    {
                        var embStore = new ClusterEmbeddingsStore(paths.EmbeddingsJson(sessionId, versionId));
                        if (File.Exists(paths.EmbeddingsJson(sessionId, versionId))) { embStore.Delete(); didAny = true; }

                        var spStore = new SpeakersStore(paths.SpeakersJson(sessionId, versionId));
                        var speakers = await spStore.LoadAsync(inner);
                        if (speakers is not null && speakers.SuggestionProvenance.Count > 0)
                        {
                            await spStore.SaveAsync(speakers with
                            { SuggestionProvenance = new Dictionary<string, SuggestionProvenanceEntry>() }, inner);
                            didAny = true;
                        }
                    }
                    return didAny;
                }, ct);
                if (any) touched++;
            }
        }
        var peopleStore = new PeopleStore(paths.PeopleJson);
        var registry = await peopleStore.LoadAsync(ct);
        if (registry is not null && registry.People.Any(p => p.Voiceprint.Count > 0))
            await peopleStore.SaveAsync(PeopleRegistryOps.ClearAllVoiceprints(registry), ct);
        return touched;
    }
```

(Adapt `RunForSessionAsync<bool>` generic call shape to the existing overloads in this file.)

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/LocalScribe.App.Tests --filter MaintenanceServiceVoiceprintTests -v q` and then the full `dotnet test tests/LocalScribe.App.Tests --filter MaintenanceService -v q`
Expected: new tests PASS; all pre-existing SaveDiarisationAsync tests PASS (5-arg overload preserved).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceVoiceprintTests.cs
git commit -m "feat(voiceprint): persist embeddings.json + global purge"
```

---

### Task 10: VoiceprintEnrollmentService (confirm-time + batch backfill)

**Files:**
- Create: `src/LocalScribe.Core/People/VoiceprintEnrollmentService.cs`
- Test: `tests/LocalScribe.Core.Tests/VoiceprintEnrollmentServiceTests.cs`

**Interfaces:**
- Consumes: `PeopleStore`, `PeopleRegistryOps`, `ClusterEmbeddingsStore`, `IEmbeddingEngine`, `SpeakersStore`, `MetadataStore`, `SessionStore`, `TranscriptStore`, `MatterStore`, `StoragePaths`, `TimeProvider`.
- Produces:
  - `VoiceprintEnrollmentService(StoragePaths paths, TimeProvider time, Func<string> newId)` — `newId` yields enrollment/person ids (App passes `() => Guid.NewGuid().ToString("N")`).
  - `Task EnrollFromConfirmAsync(string sessionId, string versionId, IReadOnlyList<ClusterEnrollmentRequest> requests, CancellationToken ct)` with `ClusterEnrollmentRequest(string ClusterKey, string? PersonId, string? NewPersonName)` — exactly one of PersonId/NewPersonName set. Loads `embeddings.json` for the version; for each request with an embedding present: resolves/creates the person (`EnsurePerson` for NewPersonName), appends an enrollment (`Method` from the embeddings file). Requests whose clusterKey has no embedding are skipped silently. One registry load + one save.
  - `Task<BackfillReport> BackfillScanAsync(IEmbeddingEngine engine, string embeddingModelPath, Func<string, SourceKind, string?> resolveLeg, CancellationToken ct)` returning `BackfillReport(int SessionsScanned, int Enrolled, int Skipped)`: for each session dir — skip when `embeddings.json` already exists for the active version, when `speakers.json` is absent, or when no participant has both a `ClusterKey` and a resolvable person (participant.Name exact-ordinal matches a roster member with `PersonId` on one of the session's matters, or matches an existing Person name); otherwise: derive each owned cluster's ranges from the active version's transcript lines (seqs mapped to the cluster in `speakers.Assignments`, using each line's `StartMs`/`EndMs`), call `engine.EmbedAsync` per cluster against the source's leg path from `resolveLeg(sessionId, side)` (skip when null), and enroll. Per-session failures count as Skipped, never abort the scan.

- [ ] **Step 1: Write the failing tests** — `tests/LocalScribe.Core.Tests/VoiceprintEnrollmentServiceTests.cs`. Build a temp `StoragePaths` root with a seeded session (`session.json` minimal with ActiveVersion root, `meta.json` participants, `speakers.json`, `transcript.jsonl` lines with times, `embeddings.json`) plus `people.json`. Scenarios (write them fully, seeding via the real stores):

```csharp
    [Fact]
    public async Task Confirm_enrolls_existing_person_by_id() { /* embeddings has Remote:0; request PersonId=p1 -> p1 gains 1 enrollment with SourceSessionId/SourceClusterKey/method set */ }

    [Fact]
    public async Task Confirm_creates_person_for_new_name() { /* request NewPersonName="Zed" -> registry gains person Zed with 1 enrollment */ }

    [Fact]
    public async Task Confirm_skips_cluster_without_embedding() { /* request for Remote:9 (absent) -> registry unchanged */ }

    [Fact]
    public async Task Backfill_enrolls_owned_person_linked_cluster_via_engine()
    { /* no embeddings.json; participant Name="Sarah" ClusterKey="Remote:0"; matter roster "Sarah"->PersonId p1;
         fake IEmbeddingEngine returns [1,2]; assert p1 enrolled, engine saw ranges from the transcript lines,
         report.Enrolled==1 */ }

    [Fact]
    public async Task Backfill_skips_sessions_that_already_have_embeddings()
    { /* embeddings.json present -> engine never called, Skipped==0/Scanned==1 */ }
```

Use a simple `FakeEmbeddingEngine : IEmbeddingEngine` capturing requests and returning `new EmbedResult([1f, 2f], "campplus-zh-en")`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter VoiceprintEnrollmentServiceTests -v q`
Expected: compile errors.

- [ ] **Step 3: Implement** — `src/LocalScribe.Core/People/VoiceprintEnrollmentService.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.People;

public sealed record ClusterEnrollmentRequest(string ClusterKey, string? PersonId, string? NewPersonName);

public sealed record BackfillReport(int SessionsScanned, int Enrolled, int Skipped);

/// <summary>Enrollment orchestration (voiceprint design 2026-07-25). Confirm-time enrollment
/// copies vectors out of the session's embeddings.json; backfill extracts them via the embed op
/// for sessions diarised before embeddings existed. Enrollment is the consent gate: only
/// clusters the user explicitly confirmed to a person ever enroll.</summary>
public sealed class VoiceprintEnrollmentService(StoragePaths paths, TimeProvider time, Func<string> newId)
{
    public async Task EnrollFromConfirmAsync(
        string sessionId, string versionId,
        IReadOnlyList<ClusterEnrollmentRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return;
        var embeddings = await new ClusterEmbeddingsStore(paths.EmbeddingsJson(sessionId, versionId)).LoadAsync(ct);
        if (embeddings is null || embeddings.Entries.Count == 0) return;

        var store = new PeopleStore(paths.PeopleJson);
        var registry = await store.LoadAsync(ct) ?? new PeopleRegistry();
        bool changed = false;
        foreach (var request in requests)
        {
            if (!embeddings.Entries.TryGetValue(request.ClusterKey, out var vector)) continue;
            string personId;
            if (request.PersonId is not null) personId = request.PersonId;
            else if (request.NewPersonName is not null)
            {
                (registry, var person) = PeopleRegistryOps.EnsurePerson(
                    registry, request.NewPersonName, newId, time.GetUtcNow());
                personId = person.Id;
            }
            else continue;

            registry = PeopleRegistryOps.Enroll(registry, personId, new VoiceprintEnrollment
            {
                Id = newId(),
                Embedding = vector,
                Method = embeddings.Method,
                SourceSessionId = sessionId,
                SourceClusterKey = request.ClusterKey,
                EnrolledAtUtc = time.GetUtcNow(),
            });
            changed = true;
        }
        if (changed) await store.SaveAsync(registry, ct);
    }

    public async Task<BackfillReport> BackfillScanAsync(
        IEmbeddingEngine engine, string embeddingModelPath,
        Func<string, SourceKind, string?> resolveLeg, CancellationToken ct)
    {
        int scanned = 0, enrolled = 0, skipped = 0;
        if (!Directory.Exists(paths.SessionsDir)) return new BackfillReport(0, 0, 0);
        var peopleStore = new PeopleStore(paths.PeopleJson);
        var registry = await peopleStore.LoadAsync(ct) ?? new PeopleRegistry();
        bool changed = false;

        foreach (var dir in Directory.EnumerateDirectories(paths.SessionsDir))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;
            var sessionId = Path.GetFileName(dir);
            try
            {
                var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(ct);
                if (session is null) { skipped++; continue; }
                var versionId = session.ActiveVersion;
                if (File.Exists(paths.EmbeddingsJson(sessionId, versionId))) continue;
                var speakers = await new SpeakersStore(paths.SpeakersJson(sessionId, versionId)).LoadAsync(ct);
                var meta = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(ct);
                if (speakers is null || meta is null) { skipped++; continue; }

                // person resolution: participant.Name -> matter roster PersonId, else existing Person
                var rosterByName = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var matterId in meta.MatterIds)
                {
                    var matter = await new MatterStore(paths).LoadAsync(matterId, ct);
                    if (matter is null) continue;
                    foreach (var m in matter.Roster)
                        if (m.PersonId is not null && !rosterByName.ContainsKey(m.Name))
                            rosterByName[m.Name] = m.PersonId;
                }

                var lines = await new TranscriptStore(paths.TranscriptJsonl(sessionId, versionId)).ReadAllAsync(ct);
                foreach (var p in meta.Participants)
                {
                    if (p.ClusterKey is null || string.IsNullOrWhiteSpace(p.Name)) continue;
                    string? personId = rosterByName.TryGetValue(p.Name, out var viaRoster)
                        ? viaRoster
                        : PeopleRegistryOps.FindByName(registry, p.Name)?.Id;
                    if (personId is null) continue;

                    var sourceKey = p.ClusterKey[..p.ClusterKey.IndexOf(':')];
                    if (!speakers.Assignments.TryGetValue(sourceKey, out var bySeq)) continue;
                    var seqs = bySeq.Where(kv => kv.Value == p.ClusterKey)
                                    .Select(kv => long.Parse(kv.Key)).ToHashSet();
                    var ranges = lines.Where(l => seqs.Contains(l.Seq))
                                      .Select(l => new EmbedRange(l.StartMs, l.EndMs)).ToList();
                    var legPath = resolveLeg(sessionId, Enum.Parse<SourceKind>(sourceKey));
                    if (ranges.Count == 0 || legPath is null) continue;

                    var embed = await engine.EmbedAsync(new EmbedRequest(legPath, ranges, embeddingModelPath), ct);
                    registry = PeopleRegistryOps.Enroll(registry, personId, new VoiceprintEnrollment
                    {
                        Id = newId(), Embedding = embed.Embedding, Method = embed.Method,
                        SourceSessionId = sessionId, SourceClusterKey = p.ClusterKey,
                        EnrolledAtUtc = time.GetUtcNow(),
                    });
                    changed = true; enrolled++;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { skipped++; }
        }
        if (changed) await peopleStore.SaveAsync(registry, ct);
        return new BackfillReport(scanned, enrolled, skipped);
    }
}
```

Adjust `MatterStore` construction to its actual ctor (it takes `StoragePaths` or a path — check `src/LocalScribe.Core/Storage/MatterStore.cs` line 1-20 and match; same for `TranscriptStore.ReadAllAsync` name, `TranscriptLine.Seq` type).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter VoiceprintEnrollmentServiceTests -v q`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/People/VoiceprintEnrollmentService.cs tests/LocalScribe.Core.Tests/VoiceprintEnrollmentServiceTests.cs
git commit -m "feat(voiceprint): enrollment service (confirm + batch backfill)"
```

---

### Task 11: SplitSpeakersViewModel — suggestions, accept/dismiss, remember-voice, confirm plumbing

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/SplitSpeakersViewModelVoiceprintTests.cs` (new; copy the queued-dispatch fake + fixture setup style from the existing `SplitSpeakersViewModelTests.cs`)

**Interfaces:**
- Consumes: Tasks 5-10 types; existing VM structure.
- Produces:
  - `ClusterRowViewModel` gains: `[ObservableProperty] VoiceprintSuggestion? _suggestion;` `[ObservableProperty] bool _rememberVoice;` `public string? AcceptedPersonId { get; private set; }` `public double? AcceptedScore { get; private set; }` `public IRelayCommand AcceptSuggestionCommand { get; }` (sets `Name = Suggestion.PersonName`, `AcceptedPersonId/AcceptedScore`, then `Suggestion = null`) and `public IRelayCommand DismissSuggestionCommand { get; }` (just `Suggestion = null`). Editing `Name` after accept clears `AcceptedPersonId` (partial `OnNameChanged`).
  - `SplitSpeakersViewModel` ctor gains three params (after `resolveModel`): `PeopleStore people, Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<Matter>>> loadMatters, VoiceprintEnrollmentService enrollment`.
  - After a successful run, matter-pool suggestions are computed off-thread and applied inside the same dispatch that publishes `Clusters` (rows are constructed with their suggestion). `public IAsyncRelayCommand SearchAllPeopleCommand { get; }` re-matches ALL clusters against the full registry and fills suggestions for rows not accepted and still default-named.
  - `ConfirmAsync` additionally: builds `Provenance` (accepted rows: clusterKey -> entry with `AcceptedScore` and `_time.GetUtcNow()`), passes it in the `DiarisationCommit`; passes `resultsBySource` (source string -> stored `DiarisationResult`) to the new `SaveDiarisationAsync` overload; after save, computes enrollment requests — accepted rows (`PersonId = AcceptedPersonId`), rows whose effective name matches a person-linked roster member of the session's matters (`PersonId` via the link), and rows with `RememberVoice` ticked (`NewPersonName` = effective name) — translating each clusterKey through the returned remap, then calls `enrollment.EnrollFromConfirmAsync(sessionId, _versionId, requests, ct)`. A row matching several rules enrolls once (priority: accepted > roster-linked > remember-voice).
  - `RunAsync` requests come with `EmitEmbeddings: true`. Missing `ClusterEmbeddings` in results -> no suggestions, no error (one `Debug.WriteLine` max).

- [ ] **Step 1: Write the failing tests.** Copy the fixture style (fake engine returning canned `DiarisationResult`s, queued dispatch fake that records actions and is pumped explicitly) from `tests/LocalScribe.App.Tests/SplitSpeakersViewModelTests.cs`. Scenarios:

```csharp
    [Fact]
    public async Task Run_populates_matter_pool_suggestion_on_row()
    { /* engine result has ClusterEmbeddings {"0": v}; matter roster links person p1 whose
         voiceprint matches v; after RunAsync + pump, the Remote:0 row's Suggestion.PersonId == "p1" */ }

    [Fact]
    public async Task No_embeddings_means_no_suggestions_and_no_error()
    { /* result.ClusterEmbeddings null -> rows have null Suggestion; reporter saw nothing */ }

    [Fact]
    public async Task Accept_fills_name_and_clears_chip_and_records_person()
    { /* AcceptSuggestionCommand -> Name == person name, AcceptedPersonId set, Suggestion null;
         then editing Name clears AcceptedPersonId */ }

    [Fact]
    public async Task SearchAllPeople_matches_against_global_registry()
    { /* person NOT on any matter roster; matter-pool pass yields no suggestion; SearchAllPeopleCommand
         fills it */ }

    [Fact]
    public async Task Confirm_passes_provenance_results_and_enrolls()
    { /* accept a suggestion then ConfirmAsync; assert the fake MaintenanceService (or real one on a
         temp root, matching the existing test style) received a commit whose Provenance has the
         accepted entry, and people.json gained an enrollment with the post-remap clusterKey */ }

    [Fact]
    public async Task RememberVoice_creates_new_person_on_confirm()
    { /* type a free name, tick RememberVoice, confirm -> registry has a new person with 1 enrollment */ }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.App.Tests --filter SplitSpeakersViewModelVoiceprintTests -v q`
Expected: compile errors.

- [ ] **Step 3: Implement in `SplitSpeakersViewModel.cs`.** Key mechanics (full code for the non-obvious parts):

(a) `ClusterRowViewModel` additions:

```csharp
    [ObservableProperty] private VoiceprintSuggestion? _suggestion;
    [ObservableProperty] private bool _rememberVoice;
    public string? AcceptedPersonId { get; private set; }
    public double? AcceptedScore { get; private set; }
    public IRelayCommand AcceptSuggestionCommand { get; }
    public IRelayCommand DismissSuggestionCommand { get; }
```

constructed in the row's constructor body:

```csharp
        AcceptSuggestionCommand = new RelayCommand(() =>
        {
            if (Suggestion is null) return;
            AcceptedPersonId = Suggestion.PersonId;
            AcceptedScore = Suggestion.Score;
            Name = Suggestion.PersonName;     // OnNameChanged sees Accepted* already set
            Suggestion = null;
        });
        DismissSuggestionCommand = new RelayCommand(() => Suggestion = null);
```

```csharp
    partial void OnNameChanged(string value)
    {
        // A manual edit after accept breaks the person link (the provenance/enrollment must
        // only ever describe what the user actually accepted).
        if (AcceptedPersonId is not null &&
            !string.Equals(value, _acceptedName, StringComparison.Ordinal))
        { AcceptedPersonId = null; AcceptedScore = null; }
    }
    private string? _acceptedName;   // set inside AcceptSuggestionCommand before Name
```

(set `_acceptedName = Suggestion.PersonName;` before assigning `Name` in the accept command.)

(b) VM fields/ctor: add `private readonly PeopleStore _people; private readonly Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<Matter>>> _loadMatters; private readonly VoiceprintEnrollmentService _enrollment; private IReadOnlyList<string> _matterIds = [];` — capture `loaded.Meta.MatterIds` in `Apply`. Add `SearchAllPeopleCommand = new AsyncRelayCommand(SearchAllPeopleAsync, () => Clusters.Count > 0);` and poke it in `OnIsRunningChanged`/`Clusters.CollectionChanged`.

(c) In `RunAsync`: build requests with `EmitEmbeddings: true`. After the per-source loop (before the publish dispatch), compute suggestions:

```csharp
            var suggestions = await ComputeMatterPoolSuggestionsAsync(newResultBySource, ct);
```

then inside the publish dispatch, when adding rows: `row.Suggestion = suggestions.GetValueOrDefault(row.ClusterKey);` (set on the constructed `freshClusters` before `Clusters.Add`).

```csharp
    // Matter pool = persons linked from the session's matters' rosters. Runs OFF the dispatch;
    // returns clusterKey -> suggestion. Any failure degrades to "no suggestions".
    private async Task<IReadOnlyDictionary<string, VoiceprintSuggestion>> ComputeMatterPoolSuggestionsAsync(
        IReadOnlyDictionary<SourceKind, DiarisationResult> results, CancellationToken ct)
    {
        try
        {
            var registry = await _people.LoadAsync(ct);
            if (registry is null) return new Dictionary<string, VoiceprintSuggestion>();
            var matters = await _loadMatters(_matterIds, ct);
            var linkedIds = matters.SelectMany(m => m.Roster)
                .Where(r => r.PersonId is not null).Select(r => r.PersonId!).ToHashSet(StringComparer.Ordinal);
            var pool = registry.People.Where(p => linkedIds.Contains(p.Id)).ToList();
            return MatchAgainst(results, pool);
        }
        catch (Exception) { return new Dictionary<string, VoiceprintSuggestion>(); }
    }

    private static IReadOnlyDictionary<string, VoiceprintSuggestion> MatchAgainst(
        IReadOnlyDictionary<SourceKind, DiarisationResult> results, IReadOnlyList<Person> pool)
    {
        var all = new Dictionary<string, VoiceprintSuggestion>();
        foreach (var (source, result) in results)
        {
            if (result.ClusterEmbeddings is null || result.EmbeddingMethod is null) continue;
            var keyed = result.ClusterEmbeddings.ToDictionary(
                kv => $"{source}:{kv.Key}", kv => kv.Value);
            foreach (var (k, s) in VoiceprintMatcher.Suggest(keyed, result.EmbeddingMethod, pool))
                all[k] = s;
        }
        return all;
    }

    private async Task SearchAllPeopleAsync()
    {
        try
        {
            var registry = await _people.LoadAsync(CancellationToken.None);
            if (registry is null) return;
            var all = MatchAgainst(_resultBySource, registry.People);
            _dispatch(() =>
            {
                foreach (var row in Clusters)
                    if (row.AcceptedPersonId is null && all.TryGetValue(row.ClusterKey, out var s))
                        row.Suggestion = s;
            });
        }
        catch (Exception ex) { _reporter.Report("Split speakers", ex); }
    }
```

(d) `ConfirmAsync` additions — after `names` is built:

```csharp
            var provenance = new Dictionary<string, SuggestionProvenanceEntry>();
            foreach (var cluster in Clusters)
                if (cluster.AcceptedPersonId is not null && cluster.AcceptedScore is double score)
                    provenance[cluster.ClusterKey] =
                        new SuggestionProvenanceEntry(cluster.AcceptedPersonId, score, _time.GetUtcNow());
```

pass `provenance` as the commit's 6th arg and `_resultBySource.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)` as `resultsBySource` to the 6-arg `SaveDiarisationAsync`; capture the returned remap. Then, before raising `DiarisationSaved`:

```csharp
            // Enrollment (consent gate = the confirm). Priority per row: accepted suggestion >
            // person-linked roster name > RememberVoice new person. ClusterKeys are translated
            // through the merge's collision remap so enrollment reads the keys that actually
            // landed in embeddings.json.
            var rosterPersonByName = await RosterPersonLinksAsync();
            var requests = new List<ClusterEnrollmentRequest>();
            foreach (var cluster in Clusters)
            {
                string key = remap.TryGetValue(cluster.ClusterKey, out var nk) ? nk : cluster.ClusterKey;
                string effective = names[cluster.ClusterKey];
                if (cluster.AcceptedPersonId is not null)
                    requests.Add(new ClusterEnrollmentRequest(key, cluster.AcceptedPersonId, null));
                else if (rosterPersonByName.TryGetValue(effective, out var linkedId))
                    requests.Add(new ClusterEnrollmentRequest(key, linkedId, null));
                else if (cluster.RememberVoice && !string.Equals(effective, cluster.DefaultName, StringComparison.Ordinal))
                    requests.Add(new ClusterEnrollmentRequest(key, null, effective));
            }
            if (requests.Count > 0)
                await _enrollment.EnrollFromConfirmAsync(_sessionId, _versionId, requests, CancellationToken.None);
```

with:

```csharp
    private async Task<IReadOnlyDictionary<string, string>> RosterPersonLinksAsync()
    {
        try
        {
            var matters = await _loadMatters(_matterIds, CancellationToken.None);
            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in matters.SelectMany(m => m.Roster))
                if (r.PersonId is not null && !byName.ContainsKey(r.Name)) byName[r.Name] = r.PersonId;
            return byName;
        }
        catch (Exception) { return new Dictionary<string, string>(); }
    }
```

(`SaveDiarisationAsync` already returns the remap — the existing call discards it; keep `var remap = await _maintenance.SaveDiarisationAsync(...)`.)

- [ ] **Step 4: Run tests — new AND existing**

Run: `dotnet test tests/LocalScribe.App.Tests --filter SplitSpeakersViewModel -v q`
Expected: all PASS. Existing tests need the three new ctor args — pass a `PeopleStore` on a temp path, `(_, _) => Task.FromResult<IReadOnlyList<Matter>>([])`, and a `VoiceprintEnrollmentService` on the same temp `StoragePaths`.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs tests/LocalScribe.App.Tests/SplitSpeakersViewModelVoiceprintTests.cs tests/LocalScribe.App.Tests/SplitSpeakersViewModelTests.cs
git commit -m "feat(voiceprint): suggestions + enrollment in Split Speakers VM"
```

---

### Task 12: Split window XAML chip + App wiring

**Files:**
- Modify: `src/LocalScribe.App/SplitSpeakersWindow.xaml` (cluster row template)
- Modify: `src/LocalScribe.App/App.xaml.cs` (openSplitSpeakers factory, ~line 279)
- Modify: `docs/plans/smoke-runbook additions` — append to the CURRENT smoke runbook document a "Voiceprint" section (see Step 4)

**Interfaces:**
- Consumes: Task 11 row properties/commands.
- Produces: visible suggestion chip + Remember-voice checkbox per cluster row; "Search all people" button beside Run/Confirm; wired VM construction.

- [ ] **Step 1: XAML — cluster row chip.** In `SplitSpeakersWindow.xaml`, locate the `DataTemplate`/`ItemsControl` that renders `Clusters` rows (the block binding `Name` to an editable ComboBox with `ItemsSource="{Binding NameCandidates}"`). Directly below the naming ComboBox inside the row's vertical StackPanel, insert:

```xml
<!-- Voiceprint suggestion chip (design 2026-07-25): suggest-only, accept or dismiss. -->
<StackPanel Orientation="Horizontal" Margin="0,4,0,0"
            Visibility="{Binding Suggestion, Converter={StaticResource NullToCollapsedConverter}}">
    <TextBlock VerticalAlignment="Center">
        <Run Text="Sounds like " />
        <Run Text="{Binding Suggestion.PersonName, Mode=OneWay}" FontWeight="SemiBold" />
        <Run Text="{Binding Suggestion.Score, Mode=OneWay, StringFormat=' ({0:P0})'}" />
    </TextBlock>
    <Button Content="Accept" Margin="8,0,0,0" Padding="8,2"
            Command="{Binding AcceptSuggestionCommand}" />
    <Button Content="Dismiss" Margin="4,0,0,0" Padding="8,2"
            Command="{Binding DismissSuggestionCommand}" />
</StackPanel>
<CheckBox Content="Remember voice" Margin="0,4,0,0"
          IsChecked="{Binding RememberVoice}"
          ToolTip="Save this speaker's voiceprint under the typed name so future sessions can suggest them." />
```

If the project has no `NullToCollapsedConverter` resource, check `App.xaml`/theme dictionaries for an existing null-to-visibility converter and use its key; if none exists, add the standard one to the window resources:

```xml
<Window.Resources>
    <converters:NullToCollapsedConverter x:Key="NullToCollapsedConverter" />
</Window.Resources>
```

(create `src/LocalScribe.App/Converters/NullToCollapsedConverter.cs` returning `Visibility.Collapsed` for null, `Visible` otherwise, matching the namespace prefix conventions already used in this window.)

Beside the existing Run/Cancel/Confirm buttons add:

```xml
<Button Content="Search all people" Command="{Binding SearchAllPeopleCommand}"
        ToolTip="Match unnamed clusters against every saved voiceprint, not just this matter's people." />
```

- [ ] **Step 2: App wiring.** In `App.xaml.cs` `openSplitSpeakers` (~line 279), extend the VM construction:

```csharp
            var peopleStore = new LocalScribe.Core.Storage.PeopleStore(comp.Paths.PeopleJson);
            var enrollment = new LocalScribe.Core.People.VoiceprintEnrollmentService(
                comp.Paths, TimeProvider.System, () => Guid.NewGuid().ToString("N"));
            Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<LocalScribe.Core.Model.Matter>>> loadMatters =
                async (ids, ct) =>
                {
                    var store = new LocalScribe.Core.Storage.MatterStore(comp.Paths);
                    var list = new List<LocalScribe.Core.Model.Matter>();
                    foreach (var id in ids)
                        if (await store.LoadAsync(id, ct) is { } m) list.Add(m);
                    return list;
                };
            var splitVm = new ViewModels.SplitSpeakersViewModel(comp.Diarisation, comp.Maintenance,
                comp.Paths, comp.Settings, errors, dispatch, TimeProvider.System,
                LocalScribe.Core.Transcription.ModelPaths.Resolve,
                peopleStore, loadMatters, enrollment);
```

(match `MatterStore`'s real ctor; if it takes a path use `comp.Paths.MatterJson(id)` per-load as the existing pages do.)

- [ ] **Step 3: Build + run App tests**

Run: `dotnet build src/LocalScribe.App -v q && dotnet test tests/LocalScribe.App.Tests -v q`
Expected: build clean, all tests PASS.

- [ ] **Step 4: Smoke-runbook additions.** Append to the current smoke runbook (`docs/plans/2026-07-07-stage-5.4-smoke-runbook.md` or the latest runbook doc — whichever the repo's most recent round appended to):

```markdown
## Voiceprint smoke (design 2026-07-25)
- V1: Diarise a session on a matter whose roster has a person-linked member with an enrolled
  voiceprint of the same voice -> a "Sounds like <name> (NN%)" chip appears; Accept fills the name.
- V2: Confirm -> speakers.json gains suggestionProvenance for the accepted cluster;
  embeddings.json exists beside it; people.json enrollment count grew by the confirmed clusters.
- V3: "Search all people" surfaces a chip for a person NOT on the matter's rosters.
- V4: Delete the person's voiceprint in Settings -> re-running diarisation shows no chip.
- V5: Settings "Purge all voiceprint data" -> every embeddings.json gone, provenance maps empty,
  all enrollments empty; names, transcripts, audio untouched.
- V6: Settings "Scan sessions and enroll known speakers" on a pre-feature session with an owned,
  person-linked cluster -> enrollment appears with that session id.
- V7: Threshold sanity with real audio: same speaker across two sessions scores >= ~0.55; two
  different speakers score < 0.55. If not, tune VoiceprintMatcher constants before merge.
```

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/SplitSpeakersWindow.xaml src/LocalScribe.App/App.xaml.cs src/LocalScribe.App/Converters docs/plans
git commit -m "feat(voiceprint): suggestion chip UI + wiring + smoke additions"
```

---

### Task 13: Settings — Voiceprints section (People list, deletes, purge, backfill)

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs`
- Modify: `src/LocalScribe.App/SettingsPage.xaml`
- Modify: `src/LocalScribe.App/App.xaml.cs` (SettingsPageViewModel construction — find it via `new SettingsPageViewModel(` and append the new args)
- Test: `tests/LocalScribe.App.Tests/SettingsVoiceprintTests.cs`

**Interfaces:**
- Consumes: `PeopleStore`, `PeopleRegistryOps`, `MaintenanceService.PurgeVoiceprintDataAsync`, `VoiceprintEnrollmentService.BackfillScanAsync`, `IEmbeddingEngine` (the composition root's `SherpaHelperDiariser` already implements it after Task 8).
- Produces on `SettingsPageViewModel`:
  - `ObservableCollection<PersonRowViewModel> People` with `PersonRowViewModel(Person person)` exposing `Id`, `Name`, `EnrollmentCount`, `EnrollmentSummary` (e.g. `"3 voiceprints - latest 2026-07-25 from session <id>"`, empty when none), `NeedsReenroll` (true when it has enrollments but none with `Method == EmbeddingMethods.CampPlus`);
  - commands: `DeleteEnrollmentCommand(PersonRowViewModel)` — removes the OLDEST enrollment (per-enrollment granularity in the list UI is a follow-up; the row shows count), `DeleteVoiceprintCommand(PersonRowViewModel)`, `DeletePersonCommand(PersonRowViewModel)` (confirm-gated), `PurgeVoiceprintsCommand` (confirm-gated: `"Delete ALL voiceprint data? People keep their names; transcripts and audio are untouched."`), `BackfillScanCommand` with `[ObservableProperty] string _backfillStatus` (`"Scanned N sessions - enrolled K, skipped S."`);
  - ctor gains: `PeopleStore people, MaintenanceService maintenance (if not already a dep — check; it likely is not: pass it), VoiceprintEnrollmentService enrollment, IEmbeddingEngine embeddingEngine, Func<string, string> resolveModel (App passes ModelPaths.Resolve; skip if the VM already holds one for the model picker), Func<string, bool> confirm` — follow the existing ctor's dependency style; reuse the page's existing `Func<string,bool>`-style confirm if one exists, else inject.
  - Loading: populate `People` in the existing initialization path (`LoadAsync`/`InitializeAsync` — reuse whichever the VM has) and re-load after every mutating command.
  - All list mutation is dispatched; commands report failures through the VM's existing error reporter.

- [ ] **Step 1: Write the failing tests** — `tests/LocalScribe.App.Tests/SettingsVoiceprintTests.cs`, following the existing `SettingsPageViewModel` test fixture style (temp root + queued dispatch). Scenarios:

```csharp
    [Fact] public async Task People_list_shows_enrollment_counts() { }
    [Fact] public async Task DeleteVoiceprint_clears_enrollments_keeps_person() { }
    [Fact] public async Task DeletePerson_requires_confirm_and_removes() { /* confirm=false -> unchanged; true -> gone */ }
    [Fact] public async Task Purge_calls_maintenance_and_reloads_empty_counts() { }
    [Fact] public async Task Backfill_reports_scan_result() { /* fake IEmbeddingEngine; status string contains counts */ }
```

Fill each body concretely against the fixture (seed `people.json` via `PeopleStore` + `PeopleRegistryOps`).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.App.Tests --filter SettingsVoiceprintTests -v q`
Expected: compile errors.

- [ ] **Step 3: Implement VM + XAML.**

VM: add the members per the Interfaces block. Representative command bodies:

```csharp
    [RelayCommand]
    private async Task DeleteVoiceprintAsync(PersonRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            var reg = await _people.LoadAsync(CancellationToken.None);
            if (reg is null) return;
            await _people.SaveAsync(PeopleRegistryOps.DeleteVoiceprint(reg, row.Id), CancellationToken.None);
            await ReloadPeopleAsync();
        }
        catch (Exception ex) { _reporter.Report("Voiceprints", ex); }
    }

    [RelayCommand]
    private async Task PurgeVoiceprintsAsync()
    {
        if (!_confirm("Delete ALL voiceprint data? People keep their names; transcripts and audio are untouched."))
            return;
        try
        {
            await _maintenance.PurgeVoiceprintDataAsync(CancellationToken.None);
            await ReloadPeopleAsync();
        }
        catch (Exception ex) { _reporter.Report("Voiceprints", ex); }
    }

    [RelayCommand]
    private async Task BackfillScanAsync()
    {
        try
        {
            string embModel = _resolveModel("3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx");
            var report = await _enrollment.BackfillScanAsync(_embeddingEngine, embModel, ResolveLeg, CancellationToken.None);
            _dispatch(() => BackfillStatus =
                $"Scanned {report.SessionsScanned} sessions - enrolled {report.Enrolled}, skipped {report.Skipped}.");
            await ReloadPeopleAsync();
        }
        catch (Exception ex) { _reporter.Report("Voiceprints", ex); }
    }
```

`ResolveLeg` mirrors `SplitSpeakersViewModel.ProbeLeg` (retained sources come from each session's `session.json` inside the service; here just probe both formats):

```csharp
    private string? ResolveLeg(string sessionId, SourceKind kind)
    {
        foreach (var format in new[] { _settings.Current.AudioFormat,
                     _settings.Current.AudioFormat == AudioFormat.Flac ? AudioFormat.Wav : AudioFormat.Flac })
        {
            var path = _paths.AudioFile(sessionId, kind, format);
            if (File.Exists(path)) return path;
        }
        return null;
    }
```

XAML — new card in `SettingsPage.xaml` after the "Assistant" section, following the existing card/section markup pattern (`FontWeight="SemiBold"` header + `FieldLabel` styles):

```xml
<TextBlock Text="Voiceprints" FontWeight="SemiBold" Margin="0,0,0,8" />
<TextBlock TextWrapping="Wrap" Margin="0,0,0,8"
           Text="Voiceprints let LocalScribe suggest speaker names across sessions. They are stored only on this computer, are never used to auto-assign names, and can be deleted at any time." />
<ItemsControl ItemsSource="{Binding People}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <DockPanel Margin="0,2">
                <Button DockPanel.Dock="Right" Content="Delete person" Margin="4,0,0,0"
                        Command="{Binding DataContext.DeletePersonCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                        CommandParameter="{Binding}" />
                <Button DockPanel.Dock="Right" Content="Delete voiceprint" Margin="4,0,0,0"
                        Command="{Binding DataContext.DeleteVoiceprintCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                        CommandParameter="{Binding}" />
                <TextBlock VerticalAlignment="Center">
                    <Run Text="{Binding Name, Mode=OneWay}" FontWeight="SemiBold" />
                    <Run Text="{Binding EnrollmentSummary, Mode=OneWay}" />
                </TextBlock>
            </DockPanel>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
<StackPanel Orientation="Horizontal" Margin="0,8,0,0">
    <Button Content="Scan sessions and enroll known speakers" Command="{Binding BackfillScanCommand}" />
    <Button Content="Purge all voiceprint data" Margin="8,0,0,0" Command="{Binding PurgeVoiceprintsCommand}" />
</StackPanel>
<TextBlock Text="{Binding BackfillStatus}" Margin="0,4,0,0" />
```

App wiring: extend the `new SettingsPageViewModel(...)` call with the new dependencies (construct `PeopleStore`/`VoiceprintEnrollmentService` as in Task 12; `IEmbeddingEngine` is `comp.Diarisation` cast — expose it properly: in `CompositionRoot`, type the diarisation engine field as `SherpaHelperDiariser` or add a second property `public IEmbeddingEngine Embedding { get; }` returning the same instance; `confirm` = the same MessageBox-backed delegate the session-details editor receives).

- [ ] **Step 4: Run tests + full App suite**

Run: `dotnet test tests/LocalScribe.App.Tests -v q`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs src/LocalScribe.App/SettingsPage.xaml src/LocalScribe.App/App.xaml.cs src/LocalScribe.App/CompositionRoot.cs tests/LocalScribe.App.Tests/SettingsVoiceprintTests.cs
git commit -m "feat(voiceprint): Settings Voiceprints section (deletes, purge, backfill)"
```

---

### Task 14: Whole-branch gate

**Files:** none new.

- [ ] **Step 1: Full test run**

Run: `dotnet test tests/LocalScribe.Core.Tests -v q && dotnet test tests/LocalScribe.App.Tests -v q`
Expected: 0 failures (Core has 2 known pre-existing failures per project memory — confirm they are the SAME two, nothing new).

- [ ] **Step 2: Build everything including the helper**

Run: `dotnet build LocalScribe.slnx -v q`
Expected: 0 errors, 0 new warnings.

- [ ] **Step 3: Spec-coverage sweep**

Re-read `docs/superpowers/specs/2026-07-25-voice-fingerprint-design.md` section by section and confirm a task implemented each bullet; note the two declared deviations (batch backfill button; RememberVoice checkbox) in the final commit message.

- [ ] **Step 4: Commit any gate fixes, then request whole-branch review**

```bash
git commit -am "chore(voiceprint): gate fixes"
```

Then run the whole-branch code review per house convention before merge.
