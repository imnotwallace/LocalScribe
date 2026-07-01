# Stage 2a: Schema, Persistence & Projection Layer — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the complete files-as-truth data layer — every JSON schema (session.json v3, meta.json, matter.json + index, edits.json, speakers.json, settings.json v2), their read/write/migration, and the deterministic projection engine that renders `transcript.md` / `transcript.txt` / `session.txt` from `jsonl + speakers.json + edits.json + vocabulary` — all pure and unit-tested with zero hardware.

**Architecture:** Extends the existing `LocalScribe.Core` library with three new namespaces: `Model` (immutable record types + enums), `Storage` (System.Text.Json persistence, schema-version migration, path layout, per-file stores), and `Projection` (name resolution, the canonical apply-order engine, and the Markdown/plain-text/session-text renderers), plus a `Vocabulary` provider. All logic is pure C# behind the interfaces the design defines (Humble Object) — the audio pipeline (Stage 2b) and later stages consume these stores and the `SessionWriter` facade. No new NuGet packages: System.Text.Json and `System.TimeProvider` are in the BCL.

**Tech Stack:** .NET 10 (`net10.0-windows`), System.Text.Json (framework), `System.TimeProvider` (framework), xUnit.

This is **Plan 2a of 2** for design build-sequence Stage 2 ("Offline pipeline + entity schemas"). **Plan 2b** (VAD → Whisper.net → merge → offline runner + real-ML adapters + phantom-bleed dedup + the marker/error taxonomy those raise) builds on the interfaces this plan produces. Authoritative sources: `docs/plans/2026-06-30-localscribe-design.md` (design) and `docs/specs/localscribe-specs.md` (contractual specs, cited by section as §N below).

---

## Global Constraints

These apply to **every** task; each task's requirements implicitly include them.

- **Target framework:** `net10.0-windows` for all projects. Requires the .NET 10 SDK (`dotnet --version` >= 10.0.1xx).
- **No new packages.** Persistence uses `System.Text.Json` (framework); wall-clock uses `System.TimeProvider` (framework, .NET 8+). The **only** test-side helper is a hand-rolled `ManualUtcTimeProvider : TimeProvider` (Task 6) — do **not** add `Microsoft.Extensions.TimeProvider.Testing`.
- **ASCII-only source (literals + identifiers).** No Unicode emojis anywhere. Identifiers and **string/char literals** in C# source and test code are plain ASCII (project rule, carried from Stage 1): a spec-mandated output glyph inside a string literal is written as a `\uXXXX` escape so the compiled string is exact while the source stays ASCII — the `transcript.md` header separator is `\u00B7` (renders `·`, §6), the `pinned microphone unavailable` marker arrow is `\u2192` (renders `→`, §8.1), and the default-title em-dash is `\u2014` (renders `—`, §1.4). Comment / XML-doc prose MAY reference spec sections with `§`. **When reproducing a code block below, copy the `\uXXXX` escapes verbatim — never substitute the raw glyph.**
- **Shared JSON conventions (Task 1).** All persistence goes through `LocalScribeJson.Options`: `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`, `WriteIndented = true`, `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`, a `UtcIso8601Converter`, and a registered `JsonStringEnumConverter<T>` per enum. **Consequence of `WhenWritingNull`:** null optional fields are **omitted** (not written as `null`); tests assert omission. Enum values are controlled by `[JsonStringEnumMemberName]`, never by the property naming policy.
- **Migration re-serialization rule.** When migrating, mutate the `JsonObject`, then **`Deserialize<T>` into the typed model and re-`Serialize` the typed model** via `LocalScribeJson.Options`. Never persist a migrated file by calling `JsonObject.ToJsonString(options)` — per the docs, that path applies **only** custom converters, dropping the naming policy and null-omission.
- **Schema-version policy (§Schema-version policy).** Every persisted JSON carries an integer `schemaVersion`; each file versions independently. A reader **rejects** (throws) a version **higher** than it understands and **migrates** lower ones on load. JSONL lines tolerate unknown fields (System.Text.Json skips unmapped members by default).
- **Storage layout (reconciliation).** Follow **specs §9**: sessions live under `<root>/sessions/<id>/` and matters under `<root>/matters/`. This supersedes the flat layout sketched in the design doc's "Storage format" section (design predates the specs rev; specs §9 is contractual).
- **Determinism.** No `DateTime.Now`/`DateTimeOffset.UtcNow`/`Guid.NewGuid()` inside logic — inject `TimeProvider` for wall-clock and the existing `IClock` for session-relative time. Temp directories for filesystem tests.
- **Invariant formatting.** Every on-disk identifier and every rendered date/timestamp formats with `CultureInfo.InvariantCulture` — never the ambient culture (a machine defaulting to a non-Gregorian calendar or native digits must produce byte-identical folder ids, titles, and projections). This applies to `SessionId`, `SessionMeta.CreateDefault`, `TimestampFormat`, and all three renderers; `UtcIso8601Converter` already complies.
- **Timestamp precision (spec §1.2).** `*AtUtc` fields serialize as whole-second ISO-8601; sub-second precision is intentionally truncated on write. Millisecond precision lives only in `durationMs`/`startMs`/`endMs`, so `endedAtUtc − startedAtUtc` may disagree with `durationMs` by <1s — never assert or rely on fractional seconds in `*AtUtc`.
- **Commits:** Conventional commits (`feat:`/`test:`/`chore:`/`docs:`). One commit per task step marked *Commit*. Every commit message ends with the project trailer (append it to each `git commit -m` below):
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- **Verification:** all tasks here are `[UNIT]` — they run under `dotnet test` with zero hardware. Run the named filter after each implement step, and the full suite before the final commit of each task.

## Scope boundary (what is NOT in 2a)

Deferred to **Plan 2b / later stages** — do not build here:
- VAD (`IVadSegmenter`/Silero), transcription (`ITranscriptionEngine`/Whisper.net), the backend cascade + VRAM-OOM downgrade, the live incremental `TranscriptMerger`, FLAC/WAV audio writing, the offline pipeline runner, and the golden-corpus E2E run — **2b**.
- The real phantom-bleed dedup heuristic (needs real-audio energy tuning): 2a ships only the `IRenderDedup` seam + a `NoOpDedup` so the apply-order is complete; the working heuristic is **2b** (§5).
- The marker/error *emission* taxonomy raised by capture/model/backend (`SILENT_SOURCE`, `VRAM_OOM`, `degraded: system-audio loopback`, `transcription lagging`, device-swap/pinned-mic markers) — 2a defines the `Markers` string constants (shared) and renders markers; **2b + Stage 7** raise them.
- Diarisation (`sherpa-onnx`) — **Stage 5**; matter session-count recompute + the two-level manager UI — **Stage 4**; `.docx` export — fast-follow; launch-time recovery *scan orchestration* — **Stage 3/4** (2a provides the per-session `RecoverIfNeededAsync` it will call).

## Enum wire-value reference (single source of truth for Task 1)

| Enum | Members -> JSON string | Where used |
|---|---|---|
| `SourceKind` (existing, in `Audio`) | `Local`,`Remote` | session `sources`/`retainedAudioSources`, participant `side`, diarised sources |
| `TranscriptSource` (new) | `Local`,`Remote`,`System` | JSONL `source` |
| `TranscriptKind` (new) | `Segment`->`segment`, `Marker`->`marker` | JSONL `kind` |
| `Medium` | `Webex`,`Zoom`,`Teams`,`Phone`,`InPerson`->`In-person`,`Other` | meta `medium` |
| `RemoteMode` | `Auto`->`auto`,`PerProcess`->`perProcess`,`SystemMix`->`systemMix` | settings/`devices.remote.mode` |
| `MicMode` | `FollowDefault`->`followDefault`,`Pinned`->`pinned` | settings/`devices.mic.mode` |
| `Backend` | `Auto`->`auto`,`Cuda`->`cuda`,`Vulkan`->`vulkan`,`Cpu`->`cpu` | settings `backend` (system `session.backend` is a free string, e.g. `"CUDA"`) |
| `AppKind` | `Teams`,`Zoom`,`Webex`,`Manual`,`Browser` | session `app` |
| `AudioFormat` | `Flac`->`flac`,`Wav`->`wav` | settings `audioFormat` |

`session.json` `model`/`backend`/`language` are **free strings** (system-recorded actuals like `"small.en"`, `"CUDA"`), not enums — this avoids the casing conflict between the settings preference (`"cuda"`) and the recorded actual (`"CUDA"`). `settings.audioRetention` and `settings.timestamps` are also free strings (`audioRetention` because of the `days:N` form; §7).

---

## Task 1: Shared JSON conventions — enums, options, UTC converter  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Model/Enums.cs`, `src/LocalScribe.Core/Model/TranscriptSource.cs`, `src/LocalScribe.Core/Storage/UtcIso8601Converter.cs`, `src/LocalScribe.Core/Storage/LocalScribeJson.cs`
- Test: `tests/LocalScribe.Core.Tests/JsonConventionsTests.cs`

**Interfaces:**
- Consumes: existing `LocalScribe.Core.Audio.SourceKind`.
- Produces:
  - enums `TranscriptSource {Local,Remote,System}`, `TranscriptKind {Segment,Marker}`, `Medium`, `RemoteMode`, `MicMode`, `Backend`, `AppKind`, `AudioFormat` (wire values per the reference table) in namespace `LocalScribe.Core.Model`.
  - `sealed class UtcIso8601Converter : JsonConverter<DateTimeOffset>` (namespace `LocalScribe.Core.Storage`).
  - `static class LocalScribeJson { static JsonSerializerOptions Options { get; } }`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/JsonConventionsTests.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class JsonConventionsTests
{
    private sealed record Probe
    {
        public RemoteMode Remote { get; init; }
        public MicMode Mic { get; init; }
        public Medium Medium { get; init; }
        public Backend Backend { get; init; }
        public SourceKind Side { get; init; }
        public AudioFormat AudioFormat { get; init; }
        public DateTimeOffset When { get; init; }
        public string? Optional { get; init; }
    }

    [Fact]
    public void Enums_serialize_to_spec_wire_strings()
    {
        var p = new Probe
        {
            Remote = RemoteMode.PerProcess,
            Mic = MicMode.FollowDefault,
            Medium = Medium.InPerson,
            Backend = Backend.Cuda,
            Side = SourceKind.Remote,
            AudioFormat = AudioFormat.Flac,
            When = new DateTimeOffset(2026, 7, 2, 14, 32, 5, TimeSpan.Zero),
        };
        string json = JsonSerializer.Serialize(p, LocalScribeJson.Options);

        Assert.Contains("\"remote\": \"perProcess\"", json);
        Assert.Contains("\"mic\": \"followDefault\"", json);
        Assert.Contains("\"medium\": \"In-person\"", json);
        Assert.Contains("\"backend\": \"cuda\"", json);
        Assert.Contains("\"side\": \"Remote\"", json);
        Assert.Contains("\"audioFormat\": \"flac\"", json);
    }

    [Fact]
    public void DateTimeOffset_writes_utc_z_and_roundtrips()
    {
        var p = new Probe { When = new DateTimeOffset(2026, 7, 2, 14, 32, 5, TimeSpan.Zero) };
        string json = JsonSerializer.Serialize(p, LocalScribeJson.Options);
        Assert.Contains("\"when\": \"2026-07-02T14:32:05Z\"", json);

        var back = JsonSerializer.Deserialize<Probe>(json, LocalScribeJson.Options)!;
        Assert.Equal(p.When, back.When);
    }

    [Fact]
    public void Null_optional_is_omitted_and_property_names_are_camelCase()
    {
        var p = new Probe { Optional = null };
        string json = JsonSerializer.Serialize(p, LocalScribeJson.Options);
        Assert.DoesNotContain("optional", json);
        Assert.Contains("\"remote\":", json);   // camelCase property name present
        Assert.DoesNotContain("\"Remote\":", json);
    }

    [Fact]
    public void Unknown_fields_are_ignored_on_read()
    {
        string json = "{\"remote\":\"auto\",\"unknownField\":123}";
        var back = JsonSerializer.Deserialize<Probe>(json, LocalScribeJson.Options)!;
        Assert.Equal(RemoteMode.Auto, back.Remote);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter JsonConventionsTests` -> FAIL (types not defined).

- [ ] **Step 3: Implement the enums**

```csharp
// src/LocalScribe.Core/Model/Enums.cs
using System.Text.Json.Serialization;
namespace LocalScribe.Core.Model;

public enum AppKind { Teams, Zoom, Webex, Manual, Browser }

public enum Medium
{
    Webex, Zoom, Teams, Phone,
    [JsonStringEnumMemberName("In-person")] InPerson,
    Other,
}

public enum RemoteMode
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("perProcess")] PerProcess,
    [JsonStringEnumMemberName("systemMix")] SystemMix,
}

public enum MicMode
{
    [JsonStringEnumMemberName("followDefault")] FollowDefault,
    [JsonStringEnumMemberName("pinned")] Pinned,
}

public enum Backend
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("cuda")] Cuda,
    [JsonStringEnumMemberName("vulkan")] Vulkan,
    [JsonStringEnumMemberName("cpu")] Cpu,
}

public enum AudioFormat
{
    [JsonStringEnumMemberName("flac")] Flac,
    [JsonStringEnumMemberName("wav")] Wav,
}

public enum TranscriptKind
{
    [JsonStringEnumMemberName("segment")] Segment,
    [JsonStringEnumMemberName("marker")] Marker,
}
```
```csharp
// src/LocalScribe.Core/Model/TranscriptSource.cs
namespace LocalScribe.Core.Model;
public enum TranscriptSource { Local, Remote, System }
```

- [ ] **Step 4: Implement the UTC converter**

```csharp
// src/LocalScribe.Core/Storage/UtcIso8601Converter.cs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace LocalScribe.Core.Storage;

/// <summary>Serializes DateTimeOffset as UTC ISO-8601 with a trailing 'Z'
/// (e.g. 2026-07-02T14:32:05Z), matching the spec timestamp shape. System.Text.Json
/// reuses this converter for DateTimeOffset? automatically.
/// Sub-second precision is INTENTIONALLY truncated on write (spec §1.2 timestamp precision):
/// milliseconds live only in durationMs/startMs/endMs, so endedAtUtc - startedAtUtc may
/// disagree with durationMs by up to 1s. Never rely on fractional seconds in *AtUtc.</summary>
public sealed class UtcIso8601Converter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ssZ";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
```

- [ ] **Step 5: Implement the shared options**

```csharp
// src/LocalScribe.Core/Storage/LocalScribeJson.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>The single JsonSerializerOptions every LocalScribe persistence path uses.</summary>
public static class LocalScribeJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new UtcIso8601Converter());
        o.Converters.Add(new JsonStringEnumConverter<SourceKind>());
        o.Converters.Add(new JsonStringEnumConverter<TranscriptSource>());
        o.Converters.Add(new JsonStringEnumConverter<TranscriptKind>());
        o.Converters.Add(new JsonStringEnumConverter<Medium>());
        o.Converters.Add(new JsonStringEnumConverter<RemoteMode>());
        o.Converters.Add(new JsonStringEnumConverter<MicMode>());
        o.Converters.Add(new JsonStringEnumConverter<Backend>());
        o.Converters.Add(new JsonStringEnumConverter<AppKind>());
        o.Converters.Add(new JsonStringEnumConverter<AudioFormat>());
        return o;
    }
}
```

- [ ] **Step 6: Run to verify pass** — `dotnet test --filter JsonConventionsTests` -> PASS.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Model tests/LocalScribe.Core.Tests/JsonConventionsTests.cs \
        src/LocalScribe.Core/Storage/UtcIso8601Converter.cs src/LocalScribe.Core/Storage/LocalScribeJson.cs
git commit -m "feat: shared System.Text.Json conventions (enums, camelCase, UTC-Z, null-omit)"
```

---

## Task 2: TranscriptLine (JSONL record) + Markers constants  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Model/TranscriptLine.cs`, `src/LocalScribe.Core/Model/Markers.cs`
- Test: `tests/LocalScribe.Core.Tests/TranscriptLineTests.cs`

**Interfaces:**
- Consumes: `TranscriptSource`, `TranscriptKind`, `LocalScribeJson.Options`.
- Produces:
  - `sealed record TranscriptLine { int Seq; TranscriptKind Kind = Segment; TranscriptSource Source; long StartMs; long EndMs; string Text; string? SpeakerLabel; string? Lang; double? NoSpeechProb; }` with factories `static TranscriptLine Segment(int seq, TranscriptSource source, long startMs, long endMs, string text, string speakerLabel, string? lang = null, double? noSpeechProb = null)` and `static TranscriptLine Marker(int seq, long atMs, string message)`.
  - `static class Markers` — the §8.1 message constants (`RecoveredSession`, `PausedByUser`, `Resumed`, `PausedSystemSleep`, `AudioDeviceChanged`, `DegradedSystemAudioLoopback`, `TranscriptionLagging`, `PinnedMicUnavailable`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/TranscriptLineTests.cs
using System.Text.Json;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class TranscriptLineTests
{
    [Fact]
    public void Segment_serializes_with_spec_fields_and_camelCase()
    {
        var seg = TranscriptLine.Segment(17, TranscriptSource.Remote, 85320, 89110,
            "I pushed the auth changes last night.", "Them", lang: "en", noSpeechProb: 0.02);
        string json = JsonSerializer.Serialize(seg, LocalScribeJson.Options);

        Assert.Contains("\"seq\": 17", json);
        Assert.Contains("\"kind\": \"segment\"", json);
        Assert.Contains("\"source\": \"Remote\"", json);
        Assert.Contains("\"speakerLabel\": \"Them\"", json);
        Assert.Contains("\"noSpeechProb\": 0.02", json);
    }

    [Fact]
    public void Legacy_line_without_kind_reads_as_segment()
    {
        // The Stage-1 design example line carries no "kind" field.
        string line = "{\"seq\":17,\"source\":\"Remote\",\"startMs\":85320,\"endMs\":89110,"
                    + "\"text\":\"hi\",\"speakerLabel\":\"Them\"}";
        var seg = JsonSerializer.Deserialize<TranscriptLine>(line, LocalScribeJson.Options)!;
        Assert.Equal(TranscriptKind.Segment, seg.Kind);
        Assert.Equal(TranscriptSource.Remote, seg.Source);
    }

    [Fact]
    public void Marker_has_equal_start_end_system_source_and_no_speaker_label()
    {
        var m = TranscriptLine.Marker(40, 91000, Markers.AudioDeviceChanged);
        Assert.Equal(TranscriptKind.Marker, m.Kind);
        Assert.Equal(TranscriptSource.System, m.Source);
        Assert.Equal(91000, m.StartMs);
        Assert.Equal(91000, m.EndMs);
        Assert.Equal("audio device changed", m.Text);

        string json = JsonSerializer.Serialize(m, LocalScribeJson.Options);
        Assert.DoesNotContain("speakerLabel", json);   // null -> omitted
    }

    [Fact]
    public void Pinned_mic_marker_renders_arrow_glyph()
    {
        Assert.Equal("pinned microphone unavailable \u2192 default", Markers.PinnedMicUnavailable);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter TranscriptLineTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Model/TranscriptLine.cs
namespace LocalScribe.Core.Model;

/// <summary>One line of transcript.jsonl (source of truth, append-only). Two kinds
/// discriminated by <see cref="Kind"/>: transcribed segments and system markers (spec §1.1).
/// An absent "kind" on read defaults to Segment (back-compat).</summary>
public sealed record TranscriptLine
{
    public int Seq { get; init; }
    public TranscriptKind Kind { get; init; } = TranscriptKind.Segment;
    public TranscriptSource Source { get; init; }
    public long StartMs { get; init; }
    public long EndMs { get; init; }
    public string Text { get; init; } = "";
    public string? SpeakerLabel { get; init; }
    public string? Lang { get; init; }
    public double? NoSpeechProb { get; init; }

    public static TranscriptLine Segment(int seq, TranscriptSource source, long startMs, long endMs,
        string text, string speakerLabel, string? lang = null, double? noSpeechProb = null)
        => new()
        {
            Seq = seq, Kind = TranscriptKind.Segment, Source = source,
            StartMs = startMs, EndMs = endMs, Text = text,
            SpeakerLabel = speakerLabel, Lang = lang, NoSpeechProb = noSpeechProb,
        };

    public static TranscriptLine Marker(int seq, long atMs, string message)
        => new()
        {
            Seq = seq, Kind = TranscriptKind.Marker, Source = TranscriptSource.System,
            StartMs = atMs, EndMs = atMs, Text = message,
        };
}
```
```csharp
// src/LocalScribe.Core/Model/Markers.cs
namespace LocalScribe.Core.Model;

/// <summary>Canonical in-transcript marker messages (spec §8.1). The arrow in
/// <see cref="PinnedMicUnavailable"/> is written as a \u escape so this source file stays ASCII;
/// the rendered string is the spec glyph. Only RecoveredSession is emitted in Stage 2a (recovery);
/// the rest are raised by Stage 2b / Stage 7 and shared from here.</summary>
public static class Markers
{
    public const string AudioDeviceChanged = "audio device changed";
    public const string PausedSystemSleep = "paused: system sleep";
    public const string Resumed = "resumed";
    public const string PausedByUser = "paused by user";
    public const string DegradedSystemAudioLoopback = "degraded: system-audio loopback";
    public const string PinnedMicUnavailable = "pinned microphone unavailable \u2192 default";
    public const string TranscriptionLagging = "transcription lagging";
    public const string RecoveredSession = "recovered session";
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter TranscriptLineTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Model/TranscriptLine.cs src/LocalScribe.Core/Model/Markers.cs \
        tests/LocalScribe.Core.Tests/TranscriptLineTests.cs
git commit -m "feat: TranscriptLine JSONL record (segment/marker) + marker taxonomy constants"
```

---

## Task 3: AtomicFile/JsonFile atomic IO + SchemaGuard version check  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Storage/AtomicFile.cs`, `src/LocalScribe.Core/Storage/JsonFile.cs`, `src/LocalScribe.Core/Storage/SchemaGuard.cs`
- Test: `tests/LocalScribe.Core.Tests/JsonFileTests.cs`

**Interfaces:**
- Consumes: `LocalScribeJson.Options`.
- Produces:
  - `static class AtomicFile` — `Task WriteAllTextAsync(string path, string text, CancellationToken ct)` (atomic: create parent directory, write `path + ".tmp"`, then `File.Move(tmp, path, overwrite:true)`). The single "never leave a half-written file" primitive — used by `JsonFile` here and by the projection writers in Task 16.
  - `static class JsonFile` — `Task<T?> ReadAsync<T>(string path, CancellationToken ct)` (returns `default` if the file is absent), `Task WriteAsync<T>(string path, T value, CancellationToken ct)` (serialize via the shared options, then `AtomicFile.WriteAllTextAsync`).
  - `static class SchemaGuard` — `int ReadVersion(JsonObject obj)` (reads `schemaVersion`, defaults `1` if absent), `void RejectIfNewer(int fileVersion, int maxSupported, string fileKind)` (throws `NotSupportedException` when `fileVersion > maxSupported`), and `Task<JsonObject?> ReadObjectAsync(string path, CancellationToken ct)` (parse to `JsonObject`, or null if absent).
- **Exception type produced:** `NotSupportedException` with message `"{fileKind} schemaVersion {v} is newer than supported ({max})"` — later tasks assert on this.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/JsonFileTests.cs
using System.Text.Json.Nodes;
using LocalScribe.Core.Storage;

public class JsonFileTests
{
    private sealed record Doc { public int SchemaVersion { get; init; } public string Name { get; init; } = ""; }

    [Fact]
    public async Task Read_of_absent_file_returns_default()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "missing.json");
        Assert.Null(await JsonFile.ReadAsync<Doc>(path, default));
    }

    [Fact]
    public async Task Write_then_read_roundtrips_and_creates_directory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "doc.json");
        try
        {
            await JsonFile.WriteAsync(path, new Doc { SchemaVersion = 3, Name = "x" }, default);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));           // temp cleaned up by the move
            var back = await JsonFile.ReadAsync<Doc>(path, default);
            Assert.Equal(3, back!.SchemaVersion);
            Assert.Equal("x", back.Name);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadVersion_defaults_to_1_when_absent()
    {
        Assert.Equal(1, SchemaGuard.ReadVersion(JsonNode.Parse("{}")!.AsObject()));
        Assert.Equal(3, SchemaGuard.ReadVersion(JsonNode.Parse("{\"schemaVersion\":3}")!.AsObject()));
    }

    [Fact]
    public void RejectIfNewer_throws_only_when_version_exceeds_supported()
    {
        SchemaGuard.RejectIfNewer(3, 3, "session.json");   // no throw
        var ex = Assert.Throws<NotSupportedException>(() => SchemaGuard.RejectIfNewer(4, 3, "session.json"));
        Assert.Contains("newer than supported", ex.Message);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter JsonFileTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Storage/AtomicFile.cs
namespace LocalScribe.Core.Storage;

/// <summary>The one atomic-write primitive: write a sibling ".tmp" then move into place, so a
/// crash never leaves a half-written file. Every whole-file write (JSON truth AND readable
/// projections) goes through here.</summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string text, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, text, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
```
```csharp
// src/LocalScribe.Core/Storage/JsonFile.cs
using System.Text.Json;
namespace LocalScribe.Core.Storage;

/// <summary>Atomic JSON file IO through the shared options (via AtomicFile).</summary>
public static class JsonFile
{
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        string text = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<T>(text, LocalScribeJson.Options);
    }

    public static Task WriteAsync<T>(string path, T value, CancellationToken ct)
        => AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(value, LocalScribeJson.Options), ct);
}
```
```csharp
// src/LocalScribe.Core/Storage/SchemaGuard.cs
using System.Text.Json.Nodes;
namespace LocalScribe.Core.Storage;

/// <summary>Shared schema-version reading + forward-incompatibility guard (spec Schema-version policy).</summary>
public static class SchemaGuard
{
    public static async Task<JsonObject?> ReadObjectAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        string text = await File.ReadAllTextAsync(path, ct);
        return JsonNode.Parse(text)?.AsObject();
    }

    public static int ReadVersion(JsonObject obj)
        => obj.TryGetPropertyValue("schemaVersion", out JsonNode? v) && v is not null
            ? v.GetValue<int>()
            : 1;

    public static void RejectIfNewer(int fileVersion, int maxSupported, string fileKind)
    {
        if (fileVersion > maxSupported)
            throw new NotSupportedException(
                $"{fileKind} schemaVersion {fileVersion} is newer than supported ({maxSupported})");
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter JsonFileTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Storage/AtomicFile.cs src/LocalScribe.Core/Storage/JsonFile.cs \
        src/LocalScribe.Core/Storage/SchemaGuard.cs tests/LocalScribe.Core.Tests/JsonFileTests.cs
git commit -m "feat: AtomicFile write primitive + JsonFile IO + SchemaGuard version guard"
```

---

## Task 4: TranscriptStore (append-only JSONL)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Storage/TranscriptStore.cs`
- Test: `tests/LocalScribe.Core.Tests/TranscriptStoreTests.cs`

**Interfaces:**
- Consumes: `TranscriptLine`, `LocalScribeJson.Options`.
- Produces (`sealed class TranscriptStore` + `sealed record TranscriptReadResult`):
  - `TranscriptStore(string jsonlPath)`.
  - `Task AppendAsync(TranscriptLine line, CancellationToken ct)` — serializes the line **compact (single line, no indentation)** and appends `line + "\n"`; never rewrites existing content. **Self-healing line termination (spec §1.1 torn-tail durability):** if the file exists and does not already end with `\n` (a crash tore the last append), prepend `"\n"` to this append so the new record never lands on the same physical line as the torn tail.
  - `sealed record TranscriptReadResult(IReadOnlyList<TranscriptLine> Lines, int MalformedLineCount)`.
  - `Task<TranscriptReadResult> ReadAllDetailedAsync(CancellationToken ct)` — all parseable lines in write order plus a count of lines that failed to parse (torn tail). Malformed lines are **skipped, never rewritten or deleted** — the bytes stay on disk.
  - `Task<IReadOnlyList<TranscriptLine>> ReadAllAsync(CancellationToken ct)` — convenience: `(await ReadAllDetailedAsync(ct)).Lines`.
  - `Task<int> NextSeqAsync(CancellationToken ct)` — `max(seq)+1` over existing parseable lines, or `0` when empty (write-order counter for recovery / re-open).
- **Note:** the JSONL writer uses a **compact** serializer (one record per physical line), distinct from the indented `LocalScribeJson.Options` used for the standalone `.json` files. Build a compact clone in the store.
- **Why torn-tail tolerance is load-bearing:** a partial final line is exactly what a crash mid-append leaves, and crash recovery (Task 16) reads this file — a throwing reader would make recovery fail in the one scenario it exists for.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/TranscriptStoreTests.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class TranscriptStoreTests
{
    private static string TempJsonl() =>
        Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "transcript.jsonl");

    [Fact]
    public async Task Append_writes_one_physical_line_per_record()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "hi", "Me"), default);
            await store.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Remote, 500, 1500, "yo", "Them"), default);

            string[] physical = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, physical.Length);                 // compact: exactly two lines
            Assert.DoesNotContain("\n", physical[0]);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task ReadAll_returns_write_order_and_append_is_additive()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "a", "Me"), default);
            var reopened = new TranscriptStore(path);          // simulate re-open (crash/restart)
            await reopened.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 2, 3, "b", "Me"), default);

            var all = await reopened.ReadAllAsync(default);
            Assert.Equal(new[] { 0, 1 }, all.Select(l => l.Seq));
            Assert.Equal("a", all[0].Text);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task NextSeq_is_zero_when_empty_and_max_plus_one_otherwise()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            Assert.Equal(0, await store.NextSeqAsync(default));
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "a", "Me"), default);
            await store.AppendAsync(TranscriptLine.Marker(1, 5, Markers.RecoveredSession), default);
            Assert.Equal(2, await store.NextSeqAsync(default));
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Torn_final_line_is_skipped_and_counted_not_thrown()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "a", "Me"), default);
            // Simulate a crash mid-append: partial JSON, no trailing newline.
            await File.AppendAllTextAsync(path, "{\"seq\":1,\"source\":\"Rem");

            var result = await store.ReadAllDetailedAsync(default);
            Assert.Single(result.Lines);                       // the good line survives
            Assert.Equal(1, result.MalformedLineCount);        // the torn tail is surfaced
            Assert.Equal(1, await store.NextSeqAsync(default)); // seq counter unaffected by the tear
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Append_after_torn_tail_self_heals_onto_a_new_line()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "a", "Me"), default);
            await File.AppendAllTextAsync(path, "{\"seq\":1,\"source\":\"Rem");   // torn tail

            await store.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 2, 3, "b", "Me"), default);

            var result = await store.ReadAllDetailedAsync(default);
            Assert.Equal(new[] { 0, 1 }, result.Lines.Select(l => l.Seq));   // new record intact
            Assert.Equal(1, result.MalformedLineCount);                      // torn bytes preserved on disk
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

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter TranscriptStoreTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Storage/TranscriptStore.cs
using System.Text.Json;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Append-only JSONL writer/reader for transcript.jsonl (spec §1.1). One compact
/// JSON object per physical line, in write order; never rewritten (evidentiary invariant).
/// Tolerates a torn tail from a crash mid-append: reads skip+count malformed lines (the bytes
/// stay on disk), and appends self-heal line termination (spec §1.1 torn-tail durability).</summary>
public sealed class TranscriptStore
{
    // Compact clone of the shared options: same converters/naming, but single-line output.
    private static readonly JsonSerializerOptions Compact = new(LocalScribeJson.Options) { WriteIndented = false };

    private readonly string _path;
    public TranscriptStore(string jsonlPath) => _path = jsonlPath;

    public async Task AppendAsync(TranscriptLine line, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(line, Compact);
        string prefix = NeedsNewlinePrefix() ? "\n" : "";
        await File.AppendAllTextAsync(_path, prefix + json + "\n", ct);
    }

    public async Task<TranscriptReadResult> ReadAllDetailedAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new TranscriptReadResult(Array.Empty<TranscriptLine>(), 0);
        var lines = new List<TranscriptLine>();
        int malformed = 0;
        foreach (string raw in await File.ReadAllLinesAsync(_path, ct))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var line = JsonSerializer.Deserialize<TranscriptLine>(raw, Compact);
                if (line is not null) lines.Add(line); else malformed++;
            }
            catch (JsonException) { malformed++; }   // torn tail: skip, never rewrite
        }
        return new TranscriptReadResult(lines, malformed);
    }

    public async Task<IReadOnlyList<TranscriptLine>> ReadAllAsync(CancellationToken ct)
        => (await ReadAllDetailedAsync(ct)).Lines;

    public async Task<int> NextSeqAsync(CancellationToken ct)
    {
        var all = await ReadAllAsync(ct);
        return all.Count == 0 ? 0 : all.Max(l => l.Seq) + 1;
    }

    // True when the file ends without '\n' - a crash tore the previous append (spec 1.1).
    private bool NeedsNewlinePrefix()
    {
        if (!File.Exists(_path)) return false;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return false;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() != '\n';
    }
}

/// <summary>Result of a tolerant JSONL read: parseable lines in write order + how many lines
/// failed to parse (a crash's torn tail). A non-zero count is diagnostic, not fatal.</summary>
public sealed record TranscriptReadResult(IReadOnlyList<TranscriptLine> Lines, int MalformedLineCount);
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter TranscriptStoreTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Storage/TranscriptStore.cs tests/LocalScribe.Core.Tests/TranscriptStoreTests.cs
git commit -m "feat: append-only TranscriptStore (compact JSONL, torn-tail tolerant, NextSeq)"
```

---

## Task 5: SessionRecord (session.json v3) + SessionStore (write/read at v3)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Model/SessionRecord.cs`, `src/LocalScribe.Core/Storage/SessionStore.cs`
- Test: `tests/LocalScribe.Core.Tests/SessionStoreTests.cs`

**Interfaces:**
- Consumes: `AppKind`, `SourceKind`, `LocalScribeJson.Options`, `JsonFile`, `SchemaGuard`.
- Produces:
  - `sealed record SessionRecord` with `int SchemaVersion = 3; string Id; AppKind App; DateTimeOffset StartedAtUtc; DateTimeOffset? EndedAtUtc; string? TimeZoneId; int? UtcOffsetMinutes; long DurationMs; IReadOnlyList<SourceKind> Sources; string Model; string Backend; string Language; IReadOnlyList<SourceKind> RetainedAudioSources; bool Diarised; int SegmentCount; int MarkerCount; bool Recovered; string AppVersion; DeviceSnapshot Devices;`
  - `TimeZoneId`/`UtcOffsetMinutes` (spec §1.2): the Windows time-zone ID and the DST-resolved UTC offset in force at Start — captured by the session-start flow (Stage 2b/3) via `TimeZoneInfo.Local`; 2a persists them and uses `UtcOffsetMinutes` to derive local display time (Task 16). Both null (omitted) for pre-v3 records.
  - `sealed record DeviceSnapshot { MicSnapshot Mic; RemoteSnapshot Remote; }`
  - `sealed record MicSnapshot { MicMode Mode = FollowDefault; string? Id; string? Name; }`
  - `sealed record RemoteSnapshot { RemoteMode Mode = Auto; string? App; bool FellBackToSystemMix; }`
  - `sealed class SessionStore(string sessionJsonPath)` — `const int Version = 3`; `Task SaveAsync(SessionRecord, CancellationToken)`; `Task<SessionRecord?> ReadAsync(CancellationToken)` — reads v3, rejects >3 via `SchemaGuard` (migration wiring lands in Task 7).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/SessionStoreTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SessionStoreTests
{
    private static SessionRecord Sample() => new()
    {
        // Spec 1.2 example: 06:32Z at +480 (Singapore) => local 14:32, matching the id.
        Id = "2026-07-02_1432_Webex_doe-intake",
        App = AppKind.Webex,
        StartedAtUtc = new DateTimeOffset(2026, 7, 2, 6, 32, 5, TimeSpan.Zero),
        EndedAtUtc = new DateTimeOffset(2026, 7, 2, 7, 9, 11, TimeSpan.Zero),
        TimeZoneId = "Singapore Standard Time",
        UtcOffsetMinutes = 480,
        DurationMs = 2226000,
        Sources = new[] { SourceKind.Local, SourceKind.Remote },
        Model = "small.en",
        Backend = "CUDA",
        Language = "auto",
        RetainedAudioSources = new[] { SourceKind.Local, SourceKind.Remote },
        Diarised = false,
        SegmentCount = 312,
        MarkerCount = 6,
        Recovered = false,
        AppVersion = "0.1.0",
        Devices = new DeviceSnapshot
        {
            Mic = new MicSnapshot { Mode = MicMode.FollowDefault, Id = "{0.0.1.00000000}.{guid}", Name = "Shure MV7" },
            Remote = new RemoteSnapshot { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost.exe", FellBackToSystemMix = false },
        },
    };

    [Fact]
    public async Task Roundtrips_all_fields_at_v3()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "session.json");
        try
        {
            var store = new SessionStore(path);
            await store.SaveAsync(Sample(), default);
            var back = await store.ReadAsync(default);

            Assert.Equal(3, back!.SchemaVersion);
            Assert.Equal(AppKind.Webex, back.App);
            Assert.Equal("CUDA", back.Backend);                       // free-string actual, preserved
            Assert.Equal("Singapore Standard Time", back.TimeZoneId);
            Assert.Equal(480, back.UtcOffsetMinutes);
            Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, back.RetainedAudioSources);
            Assert.Equal("CiscoCollabHost.exe", back.Devices.Remote.App);
            Assert.False(back.Devices.Remote.FellBackToSystemMix);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Written_json_uses_spec_shape()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "session.json");
        try
        {
            await new SessionStore(path).SaveAsync(Sample(), default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"schemaVersion\": 3", json);
            Assert.Contains("\"app\": \"Webex\"", json);
            Assert.Contains("\"startedAtUtc\": \"2026-07-02T06:32:05Z\"", json);
            Assert.Contains("\"timeZoneId\": \"Singapore Standard Time\"", json);
            Assert.Contains("\"utcOffsetMinutes\": 480", json);
            Assert.Contains("\"mode\": \"perProcess\"", json);
            Assert.DoesNotContain("\"title\"", json);                  // title lives in meta.json (spec 1.2)
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Rejects_newer_schema_version()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "session.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":4}");
            await Assert.ThrowsAsync<NotSupportedException>(() => new SessionStore(path).ReadAsync(default));
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

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter SessionStoreTests` -> FAIL.

- [ ] **Step 3: Implement the record**

```csharp
// src/LocalScribe.Core/Model/SessionRecord.cs
using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Model;

/// <summary>session.json - system-owned truth (spec §1.2, schema v3). No user-editable fields
/// (those live in meta.json). Rewritten on finalize/relabel/recovery.</summary>
public sealed record SessionRecord
{
    public int SchemaVersion { get; init; } = 3;
    public string Id { get; init; } = "";
    public AppKind App { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }

    /// <summary>Windows time-zone ID + DST-resolved UTC offset in force at Start (spec §1.2).
    /// Local display time = StartedAtUtc + UtcOffsetMinutes; both null for pre-v3 records
    /// (renderers then fall back to the machine's current zone).</summary>
    public string? TimeZoneId { get; init; }
    public int? UtcOffsetMinutes { get; init; }

    public long DurationMs { get; init; }
    public IReadOnlyList<SourceKind> Sources { get; init; } = [];
    public string Model { get; init; } = "";
    public string Backend { get; init; } = "";
    public string Language { get; init; } = "";
    public IReadOnlyList<SourceKind> RetainedAudioSources { get; init; } = [];
    public bool Diarised { get; init; }
    public int SegmentCount { get; init; }
    public int MarkerCount { get; init; }
    public bool Recovered { get; init; }
    public string AppVersion { get; init; } = "";
    public DeviceSnapshot Devices { get; init; } = new();
}

/// <summary>Resolved device actuals captured at Start (spec §1.2/§12).</summary>
public sealed record DeviceSnapshot
{
    public MicSnapshot Mic { get; init; } = new();
    public RemoteSnapshot Remote { get; init; } = new();
}

public sealed record MicSnapshot
{
    public MicMode Mode { get; init; } = MicMode.FollowDefault;
    public string? Id { get; init; }
    public string? Name { get; init; }
}

public sealed record RemoteSnapshot
{
    public RemoteMode Mode { get; init; } = RemoteMode.Auto;
    public string? App { get; init; }
    public bool FellBackToSystemMix { get; init; }
}
```

- [ ] **Step 4: Implement the store**

```csharp
// src/LocalScribe.Core/Storage/SessionStore.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes session.json (spec §1.2). Rejects a newer schema; migration of
/// v1/v2 records is layered on in Task 7 (SessionMigrator).</summary>
public sealed class SessionStore
{
    public const int Version = 3;
    private readonly string _path;
    public SessionStore(string sessionJsonPath) => _path = sessionJsonPath;

    public Task SaveAsync(SessionRecord record, CancellationToken ct)
        => JsonFile.WriteAsync(_path, record with { SchemaVersion = Version }, ct);

    public async Task<SessionRecord?> ReadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "session.json");
        return await JsonFile.ReadAsync<SessionRecord>(_path, ct);
    }
}
```

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter SessionStoreTests` -> PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Model/SessionRecord.cs src/LocalScribe.Core/Storage/SessionStore.cs \
        tests/LocalScribe.Core.Tests/SessionStoreTests.cs
git commit -m "feat: SessionRecord v3 + SessionStore (spec-shape roundtrip, reject-newer)"
```

---

## Task 6: SessionMeta (meta.json) + MetadataStore + ManualUtcTimeProvider  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Model/SessionMeta.cs`, `src/LocalScribe.Core/Model/SessionParticipant.cs`, `src/LocalScribe.Core/Storage/MetadataStore.cs`, `tests/LocalScribe.Core.Tests/ManualUtcTimeProvider.cs`
- Test: `tests/LocalScribe.Core.Tests/MetadataStoreTests.cs`

**Interfaces:**
- Consumes: `SourceKind`, `Medium`, `AppKind`, `JsonFile`, `SchemaGuard`.
- Produces:
  - `sealed record SessionParticipant { string Id; string Name; SourceKind Side; string? Role; bool IsSelf; string? ClusterKey; }`
  - `sealed record SessionMeta { int SchemaVersion = 1; string Title; string Description = ""; Medium Medium; IReadOnlyList<string> MatterIds; IReadOnlyList<SessionParticipant> Participants; int LocalCount = 1; int RemoteCount = 1; string? SummaryRef; DateTimeOffset? SummaryGeneratedAtUtc; string? SummaryModel; bool Edited; DateTimeOffset? LastEditedAtUtc; }` plus `static SessionMeta CreateDefault(AppKind app, DateTimeOffset startedAtLocal, SessionParticipant? self)`.
  - `sealed class MetadataStore(string metaJsonPath)` — `const int Version = 1`; `LoadAsync`/`SaveAsync`, reject-newer.
  - Test helper `sealed class ManualUtcTimeProvider(DateTimeOffset initial) : TimeProvider` overriding `GetUtcNow()` with a settable field.
- **CreateDefault contract:** `Title = "{app} — {startedAtLocal:yyyy-MM-dd HH:mm}"` (matching the `{app} — {startedAt local}` default in §1.4), formatted with `CultureInfo.InvariantCulture` (Global Constraints — a non-Gregorian ambient calendar must not change the title); `Medium = Enum.TryParse<Medium>(app.ToString())` when the app name maps to a Medium member else `Medium.Other`; `Participants = self is null ? [] : [self]`; counts `1/1`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/ManualUtcTimeProvider.cs
public sealed class ManualUtcTimeProvider(DateTimeOffset initial) : System.TimeProvider
{
    private DateTimeOffset _now = initial;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset value) => _now = value;   // no forward-only restriction (unlike FakeTimeProvider)
}
```
```csharp
// tests/LocalScribe.Core.Tests/MetadataStoreTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class MetadataStoreTests
{
    [Fact]
    public async Task Roundtrips_participants_and_matter_tags()
    {
        var meta = new SessionMeta
        {
            Title = "Doe intake \u2014 Webex",
            Description = "Initial client interview; custody status.",
            Medium = Medium.Webex,
            MatterIds = new[] { "M-2026-014" },
            Participants = new[]
            {
                new SessionParticipant { Id = "p-self", Name = "Sam", Side = SourceKind.Local, Role = "Attorney", IsSelf = true },
                new SessionParticipant { Id = "p-alice", Name = "Alice Client", Side = SourceKind.Remote, Role = "Client" },
            },
            LocalCount = 1, RemoteCount = 1,
        };
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "meta.json");
        try
        {
            var store = new MetadataStore(path);
            await store.SaveAsync(meta, default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"side\": \"Local\"", json);
            Assert.Contains("\"isSelf\": true", json);
            Assert.DoesNotContain("clusterKey", json);          // null -> omitted

            var back = await store.LoadAsync(default);
            Assert.Equal("M-2026-014", back!.MatterIds[0]);
            Assert.Equal(2, back.Participants.Count);
            Assert.True(back.Participants[0].IsSelf);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public void CreateDefault_derives_title_medium_and_self_participant()
    {
        var self = new SessionParticipant { Id = "p-self", Name = "Sam", Side = SourceKind.Local, IsSelf = true };
        var startedLocal = new DateTimeOffset(2026, 7, 2, 14, 32, 0, TimeSpan.FromHours(8));
        var meta = SessionMeta.CreateDefault(AppKind.Webex, startedLocal, self);

        Assert.Equal("Webex \u2014 2026-07-02 14:32", meta.Title);
        Assert.Equal(Medium.Webex, meta.Medium);
        Assert.Single(meta.Participants);
        Assert.True(meta.Participants[0].IsSelf);
        Assert.Equal(1, meta.LocalCount);
        Assert.Equal(1, meta.RemoteCount);
    }

    [Fact]
    public void CreateDefault_falls_back_to_Other_medium_for_non_medium_app()
    {
        var meta = SessionMeta.CreateDefault(AppKind.Manual, new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero), self: null);
        Assert.Equal(Medium.Other, meta.Medium);
        Assert.Empty(meta.Participants);
    }

    private static void CleanParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter MetadataStoreTests` -> FAIL.

- [ ] **Step 3: Implement the records**

```csharp
// src/LocalScribe.Core/Model/SessionParticipant.cs
using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Model;

/// <summary>A session's participant snapshot (spec §1.4/§10). clusterKey is reserved (null in v1).</summary>
public sealed record SessionParticipant
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public SourceKind Side { get; init; }
    public string? Role { get; init; }
    public bool IsSelf { get; init; }
    public string? ClusterKey { get; init; }
}
```
```csharp
// src/LocalScribe.Core/Model/SessionMeta.cs
using System.Globalization;
using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Model;

/// <summary>meta.json - user-owned truth (spec §1.4). The only file user metadata edits touch.</summary>
public sealed record SessionMeta
{
    public int SchemaVersion { get; init; } = 1;
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public Medium Medium { get; init; }
    public IReadOnlyList<string> MatterIds { get; init; } = [];
    public IReadOnlyList<SessionParticipant> Participants { get; init; } = [];
    public int LocalCount { get; init; } = 1;
    public int RemoteCount { get; init; } = 1;
    public string? SummaryRef { get; init; }
    public DateTimeOffset? SummaryGeneratedAtUtc { get; init; }
    public string? SummaryModel { get; init; }
    public bool Edited { get; init; }
    public DateTimeOffset? LastEditedAtUtc { get; init; }

    /// <summary>Fresh meta at session start: title/medium derived from the system app,
    /// self auto-filled as the Local "Me" participant (spec §1.4/§8/§10).</summary>
    public static SessionMeta CreateDefault(AppKind app, DateTimeOffset startedAtLocal, SessionParticipant? self)
        => new()
        {
            Title = string.Create(CultureInfo.InvariantCulture,
                $"{app} \u2014 {startedAtLocal:yyyy-MM-dd HH:mm}"),
            Medium = Enum.TryParse(app.ToString(), out Medium m) ? m : Medium.Other,
            Participants = self is null ? [] : [self],
        };
}
```

- [ ] **Step 4: Implement the store**

```csharp
// src/LocalScribe.Core/Storage/MetadataStore.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes meta.json (spec §1.4) - the only file user edits touch.</summary>
public sealed class MetadataStore
{
    public const int Version = 1;
    private readonly string _path;
    public MetadataStore(string metaJsonPath) => _path = metaJsonPath;

    public Task SaveAsync(SessionMeta meta, CancellationToken ct)
        => JsonFile.WriteAsync(_path, meta with { SchemaVersion = Version }, ct);

    public async Task<SessionMeta?> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "meta.json");
        return await JsonFile.ReadAsync<SessionMeta>(_path, ct);
    }
}
```

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter MetadataStoreTests` -> PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Model/SessionMeta.cs src/LocalScribe.Core/Model/SessionParticipant.cs \
        src/LocalScribe.Core/Storage/MetadataStore.cs \
        tests/LocalScribe.Core.Tests/MetadataStoreTests.cs tests/LocalScribe.Core.Tests/ManualUtcTimeProvider.cs
git commit -m "feat: SessionMeta/meta.json + MetadataStore + CreateDefault; test ManualUtcTimeProvider"
```

---

## Task 7: Session schema migration v1 -> v2 -> v3 (+ meta.json synthesis)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Storage/SessionMigrator.cs`
- Modify: `src/LocalScribe.Core/Storage/SessionStore.cs` (wire migration into `ReadAsync`; add a self-aware read that can synthesize meta.json)
- Test: `tests/LocalScribe.Core.Tests/SessionMigratorTests.cs`

**Migration rules (spec §Schema-version policy):**
- **v1 -> v2:** `audioRetained:true` => `retainedAudioSources = sources`; `audioRetained:false` => `[]`. Drop `audioRetained`.
- **v2 -> v3:** move the user-owned fields out to a synthesized `SessionMeta`: `title` copies across then drops from session.json; `participants = self is null ? [] : [self]`; `description = ""`; `medium = app`; `matterIds = []`; `summaryRef = null`. session.json keeps only system fields and gains a `devices` snapshot defaulted to **unknown/legacy** (`mic.mode=followDefault, id=null, name="legacy"`; `remote.mode=systemMix, app=null, fellBackToSystemMix=false`) for pre-v3 records.
- Reject `schemaVersion > 3`.

**Interfaces:**
- Consumes: `SchemaGuard`, `JsonObject`, `SessionRecord`, `SessionMeta`, `SessionParticipant`, `SourceKind`, `AppKind`, `LocalScribeJson.Options`.
- Produces (`static class SessionMigrator`):
  - `record MigrationResult(SessionRecord Session, SessionMeta? SynthesizedMeta)` — `SynthesizedMeta` is non-null **only** when a v2->v3 migration ran (so the caller writes a new meta.json); null for already-v3 or when the migration path did not produce meta.
  - `MigrationResult Migrate(JsonObject raw, SessionParticipant? self)` — pure. Reads the version, applies the hops to a mutated `JsonObject`, then **`Deserialize<SessionRecord>` the mutated node** (per the migration re-serialization rule) and returns it with any synthesized meta. Throws `NotSupportedException` for version > 3.
- Modify `SessionStore` to add:
  - `Task<SessionRecord?> ReadAsync(SessionParticipant? selfForMigration, CancellationToken ct)` — if the on-disk version < 3, run the migrator, persist the results, delegating from the no-arg `ReadAsync` with `selfForMigration: null`.
  - **Crash-safe write order:** when a `SynthesizedMeta` came back, write `meta.json` **first** (if none exists yet, via a `MetadataStore` built from the sibling path), and only **then** rewrite `session.json` at v3. The v2→v3 hop moves `title` out of `session.json` — if the process died between the two writes in the opposite order, the title would be gone from both files. With meta-first, a crash in between just re-runs the migration on next load and the `File.Exists` guard keeps the already-written meta.
  - Legacy records have no timezone capture: `timeZoneId`/`utcOffsetMinutes` stay null (omitted) after migration — renderers fall back to the machine's current zone (spec §1.2).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/SessionMigratorTests.cs
using System.Text.Json.Nodes;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SessionMigratorTests
{
    private static JsonObject V1(bool audioRetained) => JsonNode.Parse($@"{{
        ""schemaVersion"": 1,
        ""id"": ""2026-06-01_1000_Teams_old"",
        ""app"": ""Teams"",
        ""startedAtUtc"": ""2026-06-01T10:00:00Z"",
        ""endedAtUtc"": ""2026-06-01T10:30:00Z"",
        ""durationMs"": 1800000,
        ""sources"": [""Local"", ""Remote""],
        ""model"": ""small.en"",
        ""backend"": ""CPU"",
        ""language"": ""en"",
        ""audioRetained"": {(audioRetained ? "true" : "false")},
        ""title"": ""Old session"",
        ""segmentCount"": 10,
        ""markerCount"": 1
    }}")!.AsObject();

    [Fact]
    public void V1_true_maps_retained_sources_to_all_sources()
    {
        var r = SessionMigrator.Migrate(V1(audioRetained: true), self: null);
        Assert.Equal(3, r.Session.SchemaVersion);
        Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, r.Session.RetainedAudioSources);
    }

    [Fact]
    public void V1_false_maps_retained_sources_to_empty()
    {
        var r = SessionMigrator.Migrate(V1(audioRetained: false), self: null);
        Assert.Empty(r.Session.RetainedAudioSources);
    }

    [Fact]
    public void V2_to_v3_moves_title_to_synthesized_meta_and_defaults_devices()
    {
        var v2 = V1(audioRetained: true);
        v2["schemaVersion"] = 2;
        v2.Remove("audioRetained");
        v2["retainedAudioSources"] = new JsonArray("Local", "Remote");

        var self = new SessionParticipant { Id = "p-self", Name = "Sam", Side = SourceKind.Local, IsSelf = true };
        var r = SessionMigrator.Migrate(v2, self);

        // session.json no longer carries title; meta.json does.
        Assert.Equal(3, r.Session.SchemaVersion);
        Assert.NotNull(r.SynthesizedMeta);
        Assert.Equal("Old session", r.SynthesizedMeta!.Title);
        Assert.Equal(Medium.Teams, r.SynthesizedMeta.Medium);          // medium defaulted from app
        Assert.Single(r.SynthesizedMeta.Participants);                 // self only
        Assert.True(r.SynthesizedMeta.Participants[0].IsSelf);
        Assert.Empty(r.SynthesizedMeta.MatterIds);

        // legacy device snapshot
        Assert.Equal("legacy", r.Session.Devices.Mic.Name);
        Assert.Equal(RemoteMode.SystemMix, r.Session.Devices.Remote.Mode);

        // legacy records carry no timezone capture (spec 1.2): stays null/omitted
        Assert.Null(r.Session.TimeZoneId);
        Assert.Null(r.Session.UtcOffsetMinutes);
    }

    [Fact]
    public void V2_to_v3_with_no_self_yields_empty_participants()
    {
        var v2 = V1(audioRetained: false);
        v2["schemaVersion"] = 2;
        v2.Remove("audioRetained");
        v2["retainedAudioSources"] = new JsonArray();
        var r = SessionMigrator.Migrate(v2, self: null);
        Assert.NotNull(r.SynthesizedMeta);
        Assert.Empty(r.SynthesizedMeta!.Participants);
    }

    [Fact]
    public void Already_v3_returns_no_synthesized_meta()
    {
        var v3 = JsonNode.Parse(@"{""schemaVersion"":3,""id"":""x"",""app"":""Webex"",
            ""startedAtUtc"":""2026-07-02T14:32:05Z"",""durationMs"":0,""sources"":[],
            ""model"":"""",""backend"":"""",""language"":""auto"",""retainedAudioSources"":[],
            ""appVersion"":""0.1.0""}")!.AsObject();
        var r = SessionMigrator.Migrate(v3, self: null);
        Assert.Equal(3, r.Session.SchemaVersion);
        Assert.Null(r.SynthesizedMeta);
    }

    [Fact]
    public void Rejects_future_version()
        => Assert.Throws<NotSupportedException>(() =>
               SessionMigrator.Migrate(JsonNode.Parse("{\"schemaVersion\":4}")!.AsObject(), self: null));

    [Fact]
    public async Task Store_migrates_v2_folder_and_writes_meta_json()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        string sessionPath = Path.Combine(dir, "session.json");
        string metaPath = Path.Combine(dir, "meta.json");
        try
        {
            Directory.CreateDirectory(dir);
            var v2 = V1(audioRetained: true);
            v2["schemaVersion"] = 2;
            v2.Remove("audioRetained");
            v2["retainedAudioSources"] = new JsonArray("Local", "Remote");
            await File.WriteAllTextAsync(sessionPath, v2.ToJsonString());

            var self = new SessionParticipant { Id = "p-self", Name = "Sam", Side = SourceKind.Local, IsSelf = true };
            var migrated = await new SessionStore(sessionPath).ReadAsync(self, default);

            Assert.Equal(3, migrated!.SchemaVersion);
            Assert.True(File.Exists(metaPath));                              // meta.json synthesized on disk
            var meta = await new MetadataStore(metaPath).LoadAsync(default);
            Assert.Equal("Old session", meta!.Title);

            string rewritten = await File.ReadAllTextAsync(sessionPath);     // session.json rewritten at v3
            Assert.Contains("\"schemaVersion\": 3", rewritten);
            Assert.DoesNotContain("audioRetained", rewritten);
            Assert.DoesNotContain("\"title\"", rewritten);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter SessionMigratorTests` -> FAIL.

- [ ] **Step 3: Implement the migrator**

```csharp
// src/LocalScribe.Core/Storage/SessionMigrator.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Pure session.json migration v1 -> v2 -> v3 (spec Schema-version policy). v2 -> v3
/// splits the user-owned fields into a synthesized meta.json. Finishes by deserializing the
/// mutated node into the typed model so the shared options (naming/null-omit) apply on re-save.</summary>
public static class SessionMigrator
{
    public sealed record MigrationResult(SessionRecord Session, SessionMeta? SynthesizedMeta);

    public static MigrationResult Migrate(JsonObject raw, SessionParticipant? self)
    {
        int version = SchemaGuard.ReadVersion(raw);
        SchemaGuard.RejectIfNewer(version, SessionStore.Version, "session.json");

        SessionMeta? synthesized = null;

        if (version <= 1)
        {
            MigrateV1ToV2(raw);
            version = 2;
        }
        if (version == 2)
        {
            synthesized = MigrateV2ToV3(raw, self);
            version = 3;
        }

        raw["schemaVersion"] = 3;
        var session = raw.Deserialize<SessionRecord>(LocalScribeJson.Options)!;
        return new MigrationResult(session, synthesized);
    }

    private static void MigrateV1ToV2(JsonObject o)
    {
        bool retained = o.TryGetPropertyValue("audioRetained", out JsonNode? ar) && ar is not null && ar.GetValue<bool>();
        var sources = o["sources"]?.AsArray() ?? new JsonArray();
        var retainedArr = new JsonArray();
        if (retained)
            foreach (JsonNode? s in sources)
                retainedArr.Add(s!.GetValue<string>());
        o["retainedAudioSources"] = retainedArr;
        o.Remove("audioRetained");
    }

    private static SessionMeta MigrateV2ToV3(JsonObject o, SessionParticipant? self)
    {
        string title = o.TryGetPropertyValue("title", out JsonNode? t) && t is not null ? t.GetValue<string>() : "";
        string appName = o.TryGetPropertyValue("app", out JsonNode? a) && a is not null ? a.GetValue<string>() : "Manual";
        Medium medium = Enum.TryParse(appName, out Medium m) ? m : Medium.Other;

        o.Remove("title");                                    // title relocates to meta.json
        o["devices"] = new JsonObject                          // unknown/legacy snapshot
        {
            ["mic"] = new JsonObject { ["mode"] = "followDefault", ["name"] = "legacy" },
            ["remote"] = new JsonObject { ["mode"] = "systemMix", ["fellBackToSystemMix"] = false },
        };

        return new SessionMeta
        {
            Title = title,
            Description = "",
            Medium = medium,
            MatterIds = [],
            Participants = self is null ? [] : [self],
            SummaryRef = null,
        };
    }
}
```

- [ ] **Step 4: Wire migration into the store**

Replace `SessionStore.ReadAsync` with the migration-aware pair (keep `SaveAsync` and `Version` unchanged):

```csharp
// src/LocalScribe.Core/Storage/SessionStore.cs  (ReadAsync section)
    public Task<SessionRecord?> ReadAsync(CancellationToken ct) => ReadAsync(selfForMigration: null, ct);

    public async Task<SessionRecord?> ReadAsync(SessionParticipant? selfForMigration, CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;

        int version = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(version, Version, "session.json");
        if (version == Version) return await JsonFile.ReadAsync<SessionRecord>(_path, ct);

        var result = SessionMigrator.Migrate(obj, selfForMigration);

        // meta.json BEFORE session.json: the v2->v3 hop moves title out of session.json, so a
        // crash between the writes must never leave the title in neither file. If we die after
        // meta.json, session.json is still v2 and the migration re-runs; the Exists guard then
        // keeps this meta.
        if (result.SynthesizedMeta is not null)
        {
            string metaPath = Path.Combine(Path.GetDirectoryName(_path)!, "meta.json");
            if (!File.Exists(metaPath))
                await new MetadataStore(metaPath).SaveAsync(result.SynthesizedMeta, ct);
        }
        await JsonFile.WriteAsync(_path, result.Session, ct);          // rewrite at v3 via typed model
        return result.Session;
    }
```

`SessionMigrator` requires `SessionParticipant`; add `using LocalScribe.Core.Model;` (already present). Ensure the class still opens with `public const int Version = 3;` and the constructor.

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter "SessionMigratorTests|SessionStoreTests"` -> PASS (Task 5's tests still green).

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Storage/SessionMigrator.cs src/LocalScribe.Core/Storage/SessionStore.cs \
        tests/LocalScribe.Core.Tests/SessionMigratorTests.cs
git commit -m "feat: session.json v1->v2->v3 migration with meta.json synthesis"
```

---

## Task 8: Matter + matters index + MatterStore  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Model/Matter.cs`, `src/LocalScribe.Core/Model/Vocabulary.cs`, `src/LocalScribe.Core/Model/MattersIndex.cs`, `src/LocalScribe.Core/Storage/MatterStore.cs`
- Test: `tests/LocalScribe.Core.Tests/MatterStoreTests.cs`

**Interfaces:**
- Consumes: `JsonFile`, `SchemaGuard`, `LocalScribeJson.Options`.
- Produces:
  - `sealed record Vocabulary { IReadOnlyList<string> Terms = []; IReadOnlyDictionary<string,string> Corrections = {}; }`
  - `sealed record RosterMember { string Id; string Name; string? Role; }`
  - `sealed record Matter { int SchemaVersion = 1; string Id; string Name; string? Reference; string? Description; DateTimeOffset DateCreatedUtc; IReadOnlyList<RosterMember> Roster; Vocabulary Vocabulary; }`
  - `sealed record MattersIndexEntry { string Id; string Name; string? Reference; int SessionCount; }`
  - `sealed record MattersIndex { int SchemaVersion = 1; IReadOnlyList<MattersIndexEntry> Matters = []; }`
  - `sealed class MatterStore(string mattersDir)` — `const int Version = 1`; `Task CreateAsync(Matter)`, `Task SaveAsync(Matter)`, `Task<Matter?> LoadAsync(string matterId)`, `Task<MattersIndex> ListAsync()`. Writes `<mattersDir>/<id>/matter.json` and upserts `<mattersDir>/matters.json`. **`SessionCount` is stored as-is** (0 on create); recompute against session `matterIds` is **Stage 4** (note in code).
- **Known, accepted drift window:** `matter.json` and the index are two writes (each individually atomic, together not). A crash in between leaves a matter invisible to `ListAsync` until its next save; Stage 4's index recompute/rebuild is the designated self-heal. 2a's `ListAsync` trusts the index — do not add scan-the-folders fallback here.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/MatterStoreTests.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class MatterStoreTests
{
    private static Matter Sample() => new()
    {
        Id = "M-2026-014",
        Name = "Doe v. State",
        Reference = "CR-2026-014",
        Description = "Custody / bail proceedings.",
        DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
        Roster = new[]
        {
            new RosterMember { Id = "p-self", Name = "Sam", Role = "Attorney" },
            new RosterMember { Id = "p-alice", Name = "Alice Client", Role = "Client" },
        },
        Vocabulary = new Vocabulary { Terms = new[] { "arraignment" }, Corrections = new Dictionary<string, string> { ["auth"] = "OAuth" } },
    };

    [Fact]
    public async Task Create_writes_matter_and_index_entry()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            var store = new MatterStore(dir);
            await store.CreateAsync(Sample());

            var loaded = await store.LoadAsync("M-2026-014");
            Assert.Equal("Doe v. State", loaded!.Name);
            Assert.Equal("OAuth", loaded.Vocabulary.Corrections["auth"]);
            Assert.Equal(2, loaded.Roster.Count);

            var index = await store.ListAsync();
            Assert.Single(index.Matters);
            Assert.Equal("M-2026-014", index.Matters[0].Id);
            Assert.Equal(0, index.Matters[0].SessionCount);          // recompute deferred to Stage 4
        }
        finally { CleanRoot(dir); }
    }

    [Fact]
    public async Task Save_upserts_existing_index_entry_without_duplicating()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            var store = new MatterStore(dir);
            await store.CreateAsync(Sample());
            await store.SaveAsync(Sample() with { Name = "Doe v. State (renamed)" });

            var index = await store.ListAsync();
            Assert.Single(index.Matters);                            // still one entry
            Assert.Equal("Doe v. State (renamed)", index.Matters[0].Name);
        }
        finally { CleanRoot(dir); }
    }

    [Fact]
    public async Task List_on_empty_store_returns_empty_index()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            var index = await new MatterStore(dir).ListAsync();
            Assert.Empty(index.Matters);
        }
        finally { CleanRoot(dir); }
    }

    private static void CleanRoot(string mattersDir)
    {
        string? root = Path.GetDirectoryName(mattersDir);
        if (root is not null && Directory.Exists(root)) Directory.Delete(root, true);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter MatterStoreTests` -> FAIL.

- [ ] **Step 3: Implement the records**

```csharp
// src/LocalScribe.Core/Model/Vocabulary.cs
namespace LocalScribe.Core.Model;

/// <summary>A custom-vocabulary layer: bias terms + a deterministic heard->correct map (spec §1.7/§10).</summary>
public sealed record Vocabulary
{
    public IReadOnlyList<string> Terms { get; init; } = [];
    public IReadOnlyDictionary<string, string> Corrections { get; init; } = new Dictionary<string, string>();
}
```
```csharp
// src/LocalScribe.Core/Model/Matter.cs
namespace LocalScribe.Core.Model;

/// <summary>A Matter roster member - the durable, reusable source of truth for a name (spec §1.5/§10).</summary>
public sealed record RosterMember
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Role { get; init; }
}

/// <summary>matter.json - the legal-case grouping, with a reusable participant roster and
/// per-Matter vocabulary (spec §1.5). Session<->Matter is many-to-many via meta.matterIds.</summary>
public sealed record Matter
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Reference { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset DateCreatedUtc { get; init; }
    public IReadOnlyList<RosterMember> Roster { get; init; } = [];
    public Vocabulary Vocabulary { get; init; } = new();
}
```
```csharp
// src/LocalScribe.Core/Model/MattersIndex.cs
namespace LocalScribe.Core.Model;

/// <summary>matters/matters.json - lightweight index for the Matter picker (spec §1.5).</summary>
public sealed record MattersIndex
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<MattersIndexEntry> Matters { get; init; } = [];
}

public sealed record MattersIndexEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Reference { get; init; }
    public int SessionCount { get; init; }
}
```

- [ ] **Step 4: Implement the store**

```csharp
// src/LocalScribe.Core/Storage/MatterStore.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>CRUD over matters (spec §1.5). Owns matter.json files and the matters.json index.
/// SessionCount is persisted as given; recompute against session matterIds is Stage 4.
/// matter.json + index are two atomic writes with a crash window in between: a matter can be
/// missing from ListAsync until its next save. Stage 4's index rebuild is the self-heal.</summary>
public sealed class MatterStore
{
    public const int Version = 1;
    private readonly string _mattersDir;
    public MatterStore(string mattersDir) => _mattersDir = mattersDir;

    private string IndexPath => Path.Combine(_mattersDir, "matters.json");
    private string MatterPath(string id) => Path.Combine(_mattersDir, id, "matter.json");

    public Task CreateAsync(Matter matter, CancellationToken ct = default) => SaveAsync(matter, ct);

    public async Task SaveAsync(Matter matter, CancellationToken ct = default)
    {
        await JsonFile.WriteAsync(MatterPath(matter.Id), matter with { SchemaVersion = Version }, ct);
        await UpsertIndexAsync(matter, ct);
    }

    public async Task<Matter?> LoadAsync(string matterId, CancellationToken ct = default)
    {
        var obj = await SchemaGuard.ReadObjectAsync(MatterPath(matterId), ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "matter.json");
        return await JsonFile.ReadAsync<Matter>(MatterPath(matterId), ct);
    }

    public async Task<MattersIndex> ListAsync(CancellationToken ct = default)
    {
        var obj = await SchemaGuard.ReadObjectAsync(IndexPath, ct);
        if (obj is null) return new MattersIndex();
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "matters.json");
        return await JsonFile.ReadAsync<MattersIndex>(IndexPath, ct) ?? new MattersIndex();
    }

    private async Task UpsertIndexAsync(Matter matter, CancellationToken ct)
    {
        var index = await ListAsync(ct);
        var entries = index.Matters.ToList();
        int existing = entries.FindIndex(e => e.Id == matter.Id);
        var entry = new MattersIndexEntry
        {
            Id = matter.Id,
            Name = matter.Name,
            Reference = matter.Reference,
            SessionCount = existing >= 0 ? entries[existing].SessionCount : 0,
        };
        if (existing >= 0) entries[existing] = entry; else entries.Add(entry);
        await JsonFile.WriteAsync(IndexPath, index with { SchemaVersion = Version, Matters = entries }, ct);
    }
}
```

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter MatterStoreTests` -> PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Model/Matter.cs src/LocalScribe.Core/Model/Vocabulary.cs \
        src/LocalScribe.Core/Model/MattersIndex.cs src/LocalScribe.Core/Storage/MatterStore.cs \
        tests/LocalScribe.Core.Tests/MatterStoreTests.cs
git commit -m "feat: Matter entity + matters index + MatterStore (CRUD, index upsert)"
```

---

## Task 9: Speakers (speakers.json) + SpeakersStore + NameResolver  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Model/Speakers.cs`, `src/LocalScribe.Core/Storage/SpeakersStore.cs`, `src/LocalScribe.Core/Projection/NameResolver.cs`
- Test: `tests/LocalScribe.Core.Tests/NameResolverTests.cs`

**Interfaces:**
- Consumes: `TranscriptLine`, `TranscriptSource`, `SessionMeta`, `SessionParticipant`, `SourceKind`, `JsonFile`, `SchemaGuard`.
- Produces:
  - `sealed record Speakers { int SchemaVersion = 1; IReadOnlyDictionary<string,string> Names; IReadOnlyDictionary<string,Dictionary<string,string>> Assignments; IReadOnlyDictionary<string,List<string>> Pinned; IReadOnlyList<SourceKind> DiarisedSources; string? Method; DateTimeOffset? DiarisedAtUtc; IReadOnlyDictionary<string,double> Confidence; }` (all dictionaries default empty).
  - `sealed class SpeakersStore(string speakersJsonPath)` — `const int Version = 1`; `LoadAsync` (null when absent), `SaveAsync`.
  - `static class NameResolver` — `string Resolve(TranscriptLine segment, Speakers? speakers, SessionMeta meta)` implementing the spec §1.3 resolution order.
- **Resolution order (spec §1.3), for a `segment` whose `source` maps to `SourceKind side` (`Local`/`Remote`; `System` markers never reach the resolver):**
  1. If `speakers?.Assignments[source][seq]` exists -> `clusterKey`; return `Names[clusterKey]` if present, else `"Speaker " + clusterId` where `clusterId` is the part after `':'`.
  2. Else if the declared count for that side (`side==Local ? meta.LocalCount : meta.RemoteCount`) is **exactly 1** and a participant with `Side==side` exists -> that participant's `Name`.
  3. Else the baseline `segment.SpeakerLabel` (which is `Me`/`Them`); if null/empty, derive `Local->"Me"`, `Remote->"Them"`.
- `source` string in `Assignments`/`Pinned` is the `TranscriptSource` name (`"Local"`/`"Remote"`); `seq` keys are the integer seq as a string.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/NameResolverTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;

public class NameResolverTests
{
    private static SessionMeta Meta(int local, int remote, params SessionParticipant[] ps) => new()
    { LocalCount = local, RemoteCount = remote, Participants = ps };

    private static TranscriptLine Seg(int seq, TranscriptSource src, string label) =>
        TranscriptLine.Segment(seq, src, 0, 1, "text", label);

    [Fact]
    public void Assignment_to_named_cluster_wins()
    {
        var speakers = new Speakers
        {
            Names = new Dictionary<string, string> { ["Remote:2"] = "Bob" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["17"] = "Remote:2" } },
        };
        Assert.Equal("Bob", NameResolver.Resolve(Seg(17, TranscriptSource.Remote, "Them"),
            speakers, Meta(1, 2)));
    }

    [Fact]
    public void Assignment_to_unnamed_cluster_renders_speaker_n()
    {
        var speakers = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["19"] = "Remote:3" } },
        };
        Assert.Equal("Speaker 3", NameResolver.Resolve(Seg(19, TranscriptSource.Remote, "Them"),
            speakers, Meta(1, 2)));
    }

    [Fact]
    public void Single_declared_participant_supplies_the_name_without_diarisation()
    {
        var meta = Meta(1, 1,
            new SessionParticipant { Id = "p-a", Name = "Alice Client", Side = SourceKind.Remote });
        Assert.Equal("Alice Client", NameResolver.Resolve(Seg(5, TranscriptSource.Remote, "Them"),
            speakers: null, meta));
    }

    [Fact]
    public void Multi_declared_side_falls_through_to_baseline_even_with_one_listed_participant()
    {
        var meta = Meta(1, 2,
            new SessionParticipant { Id = "p-a", Name = "Alice Client", Side = SourceKind.Remote });
        Assert.Equal("Them", NameResolver.Resolve(Seg(5, TranscriptSource.Remote, "Them"),
            speakers: null, meta));
    }

    [Fact]
    public void Terminal_fallback_is_baseline_label_then_derived()
    {
        Assert.Equal("Me", NameResolver.Resolve(Seg(1, TranscriptSource.Local, "Me"), null, Meta(2, 2)));
        // empty label -> derived from source
        Assert.Equal("Them", NameResolver.Resolve(Seg(2, TranscriptSource.Remote, ""), null, Meta(2, 2)));
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter NameResolverTests` -> FAIL.

- [ ] **Step 3: Implement the record + store**

```csharp
// src/LocalScribe.Core/Model/Speakers.cs
using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Model;

/// <summary>speakers.json - diarisation clusters + name overrides + manual pinned assignments
/// (spec §1.3). Non-destructive; absent until used. The sole speaker-name authority.</summary>
public sealed record Speakers
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, Dictionary<string, string>> Assignments { get; init; }
        = new Dictionary<string, Dictionary<string, string>>();
    public IReadOnlyDictionary<string, List<string>> Pinned { get; init; }
        = new Dictionary<string, List<string>>();
    public IReadOnlyList<SourceKind> DiarisedSources { get; init; } = [];
    public string? Method { get; init; }
    public DateTimeOffset? DiarisedAtUtc { get; init; }
    public IReadOnlyDictionary<string, double> Confidence { get; init; } = new Dictionary<string, double>();
}
```
```csharp
// src/LocalScribe.Core/Storage/SpeakersStore.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes speakers.json (spec §1.3). Absent until diarisation or a pinned reassignment.</summary>
public sealed class SpeakersStore
{
    public const int Version = 1;
    private readonly string _path;
    public SpeakersStore(string speakersJsonPath) => _path = speakersJsonPath;

    public Task SaveAsync(Speakers speakers, CancellationToken ct)
        => JsonFile.WriteAsync(_path, speakers with { SchemaVersion = Version }, ct);

    public async Task<Speakers?> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "speakers.json");
        return await JsonFile.ReadAsync<Speakers>(_path, ct);
    }
}
```

- [ ] **Step 4: Implement the resolver**

```csharp
// src/LocalScribe.Core/Projection/NameResolver.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>Resolves a segment's display name per spec §1.3: pinned/diarised assignment ->
/// single-declared-participant -> baseline Me/Them.</summary>
public static class NameResolver
{
    public static string Resolve(TranscriptLine segment, Speakers? speakers, SessionMeta meta)
    {
        string sourceKey = segment.Source.ToString();          // "Local" / "Remote"
        SourceKind side = segment.Source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;

        // 1) diarisation / pinned assignment
        if (speakers is not null
            && speakers.Assignments.TryGetValue(sourceKey, out var bySeq)
            && bySeq.TryGetValue(segment.Seq.ToString(), out string? clusterKey))
        {
            if (speakers.Names.TryGetValue(clusterKey, out string? named)) return named;
            int colon = clusterKey.IndexOf(':');
            string clusterId = colon >= 0 ? clusterKey[(colon + 1)..] : clusterKey;
            return "Speaker " + clusterId;
        }

        // 2) single declared participant on that side
        int declared = side == SourceKind.Local ? meta.LocalCount : meta.RemoteCount;
        if (declared == 1)
        {
            var only = meta.Participants.FirstOrDefault(p => p.Side == side);
            if (only is not null) return only.Name;
        }

        // 3) baseline label, else derive from source
        if (!string.IsNullOrEmpty(segment.SpeakerLabel)) return segment.SpeakerLabel;
        return side == SourceKind.Local ? "Me" : "Them";
    }
}
```

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter NameResolverTests` -> PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Model/Speakers.cs src/LocalScribe.Core/Storage/SpeakersStore.cs \
        src/LocalScribe.Core/Projection/NameResolver.cs tests/LocalScribe.Core.Tests/NameResolverTests.cs
git commit -m "feat: speakers.json + SpeakersStore + NameResolver (spec 1.3 resolution order)"
```

---

## Task 10: Edits (edits.json) + EditStore (correction-only, finalized-only)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Model/Edits.cs`, `src/LocalScribe.Core/Storage/EditStore.cs`
- Test: `tests/LocalScribe.Core.Tests/EditStoreTests.cs`

**Interfaces:**
- Consumes: `SessionStore`, `MetadataStore`, `SpeakersStore`, `TranscriptSource`, `JsonFile`, `SchemaGuard`, `TimeProvider`.
- Produces:
  - `sealed record Correction { string Text; DateTimeOffset EditedAtUtc; }`
  - `sealed record Edits { int SchemaVersion = 1; IReadOnlyDictionary<string,Correction> Corrections = {}; }`
  - `sealed class EditStore(string sessionDir, TimeProvider time)` — `const int Version = 1`; operating on the canonical §9 file names inside `sessionDir`:
    - `Task ApplyTextCorrectionAsync(int seq, string correctedText, CancellationToken ct)` — writes `edits.json[seq]` (text corrections only; **never** touches JSONL).
    - `Task ReassignSpeakerAsync(int seq, TranscriptSource source, string clusterKey, CancellationToken ct)` — writes `speakers.json` `Assignments[source][seq]=clusterKey` **and** pins `seq` under `Pinned[source]` (one authority per field, spec §1.3/§1.6).
    - `Task<Edits?> LoadAsync(CancellationToken ct)`.
    - Both mutators first assert the session is **finalized/recovered** (`session.json` `EndedAtUtc != null`), else throw `InvalidOperationException`; both set `meta.json` `edited=true`, `lastEditedAtUtc=time.GetUtcNow()` (spec §1.4).
    - **Seq validation (correction-only posture):** both mutators verify `seq` refers to an existing `transcript.jsonl` line of kind **segment** — corrections against a nonexistent seq or a system **marker** throw `ArgumentException` before anything is written. `ReassignSpeakerAsync` additionally requires the segment's `Source` to equal the given `source` (assignments are keyed per stream, spec §1.3).
- **Note:** `EditStore` uses literal `session.json`/`edits.json`/`speakers.json`/`meta.json` names — the same §9 filenames centralized in `StoragePaths` (Task 12).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/EditStoreTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class EditStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 15, 0, 0, TimeSpan.Zero);

    private static async Task<string> FinalizedSessionDirAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await new SessionStore(Path.Combine(dir, "session.json")).SaveAsync(new SessionRecord
        {
            Id = "s", App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = T0.AddMinutes(30),
        }, default);
        await new MetadataStore(Path.Combine(dir, "meta.json")).SaveAsync(
            SessionMeta.CreateDefault(AppKind.Webex, T0, self: null), default);
        var transcript = new TranscriptStore(Path.Combine(dir, "transcript.jsonl"));
        await transcript.AppendAsync(
            TranscriptLine.Segment(17, TranscriptSource.Remote, 85000, 89000, "the arraignment is thursday", "Them"), default);
        await transcript.AppendAsync(TranscriptLine.Marker(18, 90000, Markers.PausedByUser), default);
        return dir;
    }

    [Fact]
    public async Task Text_correction_writes_edits_json_and_marks_meta_edited()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var time = new ManualUtcTimeProvider(T0.AddMinutes(45));
            var store = new EditStore(dir, time);
            await store.ApplyTextCorrectionAsync(17, "The arraignment is on Thursday.", default);

            var edits = await store.LoadAsync(default);
            Assert.Equal("The arraignment is on Thursday.", edits!.Corrections["17"].Text);
            Assert.Equal(T0.AddMinutes(45), edits.Corrections["17"].EditedAtUtc);

            var meta = await new MetadataStore(Path.Combine(dir, "meta.json")).LoadAsync(default);
            Assert.True(meta!.Edited);
            Assert.Equal(T0.AddMinutes(45), meta.LastEditedAtUtc);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Reassign_writes_pinned_assignment_in_speakers_json()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await store.ReassignSpeakerAsync(17, TranscriptSource.Remote, "Remote:2", default);

            var speakers = await new SpeakersStore(Path.Combine(dir, "speakers.json")).LoadAsync(default);
            Assert.Equal("Remote:2", speakers!.Assignments["Remote"]["17"]);
            Assert.Contains("17", speakers.Pinned["Remote"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Correcting_a_nonexistent_seq_throws_before_writing()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.ApplyTextCorrectionAsync(99, "x", default));
            Assert.False(File.Exists(Path.Combine(dir, "edits.json")));   // nothing written
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Correcting_a_marker_line_throws()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.ApplyTextCorrectionAsync(18, "x", default));   // seq 18 is a marker
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Reassign_with_wrong_source_stream_throws()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(                   // seq 17 is Remote, not Local
                () => store.ReassignSpeakerAsync(17, TranscriptSource.Local, "Local:1", default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Editing_a_live_session_throws()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            await new SessionStore(Path.Combine(dir, "session.json")).SaveAsync(new SessionRecord
            {
                Id = "s", App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = null,   // live
            }, default);

            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ApplyTextCorrectionAsync(1, "x", default));
        }
        finally { Directory.Delete(dir, true); }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter EditStoreTests` -> FAIL.

- [ ] **Step 3: Implement the record**

```csharp
// src/LocalScribe.Core/Model/Edits.cs
namespace LocalScribe.Core.Model;

/// <summary>A single in-place text correction, keyed by the immutable seq (spec §1.6).</summary>
public sealed record Correction
{
    public string Text { get; init; } = "";
    public DateTimeOffset EditedAtUtc { get; init; }
}

/// <summary>edits.json - non-destructive text-correction overlay (spec §1.6). No tombstones/
/// hide/delete. Speaker corrections live in speakers.json, not here.</summary>
public sealed record Edits
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyDictionary<string, Correction> Corrections { get; init; } = new Dictionary<string, Correction>();
}
```

- [ ] **Step 4: Implement the store**

```csharp
// src/LocalScribe.Core/Storage/EditStore.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Correction-only, non-destructive edit facade (spec §1.6/§10). Text corrections go to
/// edits.json; per-segment speaker reassignments go to speakers.json (pinned). Allowed only on
/// finalized/recovered sessions, and only against an existing JSONL segment.</summary>
public sealed class EditStore
{
    public const int Version = 1;
    private readonly string _dir;
    private readonly TimeProvider _time;
    public EditStore(string sessionDir, TimeProvider time) => (_dir, _time) = (sessionDir, time);

    private string EditsPath => Path.Combine(_dir, "edits.json");
    private string SpeakersPath => Path.Combine(_dir, "speakers.json");
    private string SessionPath => Path.Combine(_dir, "session.json");
    private string MetaPath => Path.Combine(_dir, "meta.json");
    private string JsonlPath => Path.Combine(_dir, "transcript.jsonl");

    public async Task ApplyTextCorrectionAsync(int seq, string correctedText, CancellationToken ct)
    {
        await EnsureFinalizedAsync(ct);
        await EnsureSegmentAsync(seq, expectedSource: null, ct);
        var edits = await LoadAsync(ct) ?? new Edits();
        var corrections = new Dictionary<string, Correction>(edits.Corrections)
        {
            [seq.ToString()] = new Correction { Text = correctedText, EditedAtUtc = _time.GetUtcNow() },
        };
        await JsonFile.WriteAsync(EditsPath, edits with { SchemaVersion = Version, Corrections = corrections }, ct);
        await MarkEditedAsync(ct);
    }

    public async Task ReassignSpeakerAsync(int seq, TranscriptSource source, string clusterKey, CancellationToken ct)
    {
        await EnsureFinalizedAsync(ct);
        await EnsureSegmentAsync(seq, expectedSource: source, ct);
        var store = new SpeakersStore(SpeakersPath);
        var speakers = await store.LoadAsync(ct) ?? new Speakers();
        string key = source.ToString();

        var assignments = speakers.Assignments.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        if (!assignments.TryGetValue(key, out var bySeq)) assignments[key] = bySeq = new();
        bySeq[seq.ToString()] = clusterKey;

        var pinned = speakers.Pinned.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));
        if (!pinned.TryGetValue(key, out var pins)) pinned[key] = pins = new();
        if (!pins.Contains(seq.ToString())) pins.Add(seq.ToString());

        await store.SaveAsync(speakers with { Assignments = assignments, Pinned = pinned }, ct);
        await MarkEditedAsync(ct);
    }

    public async Task<Edits?> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(EditsPath, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "edits.json");
        return await JsonFile.ReadAsync<Edits>(EditsPath, ct);
    }

    private async Task EnsureFinalizedAsync(CancellationToken ct)
    {
        var session = await new SessionStore(SessionPath).ReadAsync(ct);
        if (session is null || session.EndedAtUtc is null)
            throw new InvalidOperationException("Editing is allowed only on finalized or recovered sessions (spec §1.6).");
    }

    private async Task EnsureSegmentAsync(int seq, TranscriptSource? expectedSource, CancellationToken ct)
    {
        var lines = await new TranscriptStore(JsonlPath).ReadAllAsync(ct);
        var line = lines.FirstOrDefault(l => l.Seq == seq)
            ?? throw new ArgumentException($"No transcript line with seq {seq}.", nameof(seq));
        if (line.Kind != TranscriptKind.Segment)
            throw new ArgumentException($"seq {seq} is a system marker; only segments are correctable (spec §1.6).", nameof(seq));
        if (expectedSource is { } src && line.Source != src)
            throw new ArgumentException($"seq {seq} belongs to the {line.Source} stream, not {src} (spec §1.3).", nameof(seq));
    }

    private async Task MarkEditedAsync(CancellationToken ct)
    {
        var metaStore = new MetadataStore(MetaPath);
        var meta = await metaStore.LoadAsync(ct);
        if (meta is null) return;
        await metaStore.SaveAsync(meta with { Edited = true, LastEditedAtUtc = _time.GetUtcNow() }, ct);
    }
}
```

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter EditStoreTests` -> PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Model/Edits.cs src/LocalScribe.Core/Storage/EditStore.cs \
        tests/LocalScribe.Core.Tests/EditStoreTests.cs
git commit -m "feat: edits.json + EditStore (correction-only, seq-validated, finalized-only)"
```

---

## Task 11: Settings (settings.json v2) + SettingsStore + v1 -> v2 migration  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Model/Settings.cs`, `src/LocalScribe.Core/Storage/SettingsStore.cs`, `src/LocalScribe.Core/Storage/SettingsMigrator.cs`
- Test: `tests/LocalScribe.Core.Tests/SettingsTests.cs`

**Interfaces:**
- Consumes: `AudioFormat`, `Backend`, `RemoteMode`, `MicMode`, `Vocabulary`, `JsonFile`, `SchemaGuard`, `JsonObject`.
- Produces:
  - `sealed record Settings` (v2, all fields per §7) + nested `SelfIdentity`, `RemoteSetting`, `MicSetting`, `AutoDetectSetting`, `OverlaySetting`, `HotkeysSetting`, `LoggingSetting`.
  - `sealed class SettingsStore(string settingsJsonPath)` — `const int Version = 2`; `SaveAsync`; `Task<Settings> LoadOrDefaultAsync(CancellationToken ct)` (fresh install -> defaults; migrates v1; rejects v3).
  - `static class SettingsMigrator` — `Settings Migrate(JsonObject raw)`.
- **v1 -> v2 rules (spec §7 / Schema-version policy):** add `self`/`overlay`/`remote`/`mic`/`audioFormat`/`vocabulary` at defaults; set `autoDetect.enabled=false`; **preserve** an explicitly stored `audioRetention` (do not flip a v1 `days:30` to `keep` — only fresh installs get `keep`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/SettingsTests.cs
using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SettingsTests
{
    [Fact]
    public async Task Fresh_install_returns_keep_default_and_v2_shape()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(2, s.SchemaVersion);
            Assert.Equal("keep", s.AudioRetention);
            Assert.Equal(AudioFormat.Flac, s.AudioFormat);
            Assert.False(s.AutoDetect.Enabled);
            Assert.True(s.Overlay.ExcludeFromCapture);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Roundtrips_v2_with_spec_wire_values()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            await new SettingsStore(path).SaveAsync(new Settings(), default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"audioRetention\": \"keep\"", json);
            Assert.Contains("\"audioFormat\": \"flac\"", json);
            Assert.Contains("\"backend\": \"auto\"", json);
            Assert.Contains("\"mode\": \"followDefault\"", json);   // mic
            Assert.Contains("\"startStop\": \"Ctrl+Alt+R\"", json);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public void Migration_v1_to_v2_preserves_retention_flips_autodetect_adds_sections()
    {
        var v1 = JsonNode.Parse(@"{
            ""schemaVersion"": 1,
            ""storageRoot"": ""%USERPROFILE%/LocalScribe"",
            ""audioRetention"": ""days:30"",
            ""model"": ""auto"", ""backend"": ""auto"", ""language"": ""auto"",
            ""autoDetect"": { ""enabled"": true, ""apps"": [""Teams"",""Zoom"",""Webex""] },
            ""hotkeys"": { ""startStop"": ""Ctrl+Alt+R"", ""pause"": ""Ctrl+Alt+P"" },
            ""timestamps"": ""relative"", ""recordingIndicator"": true, ""launchAtLogin"": true,
            ""logging"": { ""level"": ""info"", ""includeTranscriptText"": false }
        }")!.AsObject();

        var s = SettingsMigrator.Migrate(v1);
        Assert.Equal(2, s.SchemaVersion);
        Assert.Equal("days:30", s.AudioRetention);      // preserved, NOT flipped to keep
        Assert.False(s.AutoDetect.Enabled);             // flipped
        Assert.Equal(AudioFormat.Flac, s.AudioFormat);  // added at default
        Assert.True(s.Overlay.ExcludeFromCapture);      // added at default
        Assert.Equal(RemoteMode.Auto, s.Remote.Mode);   // added at default
    }

    [Fact]
    public async Task Store_migrates_v1_file_on_load_and_rewrites_v2()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"audioRetention\":\"never\"}");
            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(2, s.SchemaVersion);
            Assert.Equal("never", s.AudioRetention);
            Assert.Contains("\"schemaVersion\": 2", await File.ReadAllTextAsync(path));
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Rejects_newer_settings_version()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":3}");
            await Assert.ThrowsAsync<NotSupportedException>(
                () => new SettingsStore(path).LoadOrDefaultAsync(default));
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

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter SettingsTests` -> FAIL.

- [ ] **Step 3: Implement the records**

```csharp
// src/LocalScribe.Core/Model/Settings.cs
namespace LocalScribe.Core.Model;

/// <summary>settings.json (spec §7, schema v2), in %APPDATA%/LocalScribe.</summary>
public sealed record Settings
{
    public int SchemaVersion { get; init; } = 2;
    public string StorageRoot { get; init; } = "%USERPROFILE%/LocalScribe";
    public string AudioRetention { get; init; } = "keep";
    public AudioFormat AudioFormat { get; init; } = AudioFormat.Flac;
    public SelfIdentity Self { get; init; } = new();
    public string Model { get; init; } = "auto";
    public Backend Backend { get; init; } = Backend.Auto;
    public string Language { get; init; } = "auto";
    public RemoteSetting Remote { get; init; } = new();
    public MicSetting Mic { get; init; } = new();
    public AutoDetectSetting AutoDetect { get; init; } = new();
    public OverlaySetting Overlay { get; init; } = new();
    public Vocabulary Vocabulary { get; init; } = new();
    public HotkeysSetting Hotkeys { get; init; } = new();
    public string Timestamps { get; init; } = "relative";
    public bool RecordingIndicator { get; init; } = true;
    public bool LaunchAtLogin { get; init; } = true;
    public LoggingSetting Logging { get; init; } = new();
}

public sealed record SelfIdentity { public string Name { get; init; } = ""; public string? Role { get; init; } }
public sealed record RemoteSetting { public RemoteMode Mode { get; init; } = RemoteMode.Auto; public string? App { get; init; } }
public sealed record MicSetting { public MicMode Mode { get; init; } = MicMode.FollowDefault; public string? Id { get; init; } public string? Name { get; init; } }
public sealed record AutoDetectSetting { public bool Enabled { get; init; } public IReadOnlyList<string> Apps { get; init; } = ["Teams", "Zoom", "Webex"]; }
public sealed record OverlaySetting { public bool Enabled { get; init; } = true; public bool ShowSessionName { get; init; } public bool ShowLevelMeter { get; init; } = true; public bool ExcludeFromCapture { get; init; } = true; }
public sealed record HotkeysSetting { public string StartStop { get; init; } = "Ctrl+Alt+R"; public string Pause { get; init; } = "Ctrl+Alt+P"; }
public sealed record LoggingSetting { public string Level { get; init; } = "info"; public bool IncludeTranscriptText { get; init; } }
```

- [ ] **Step 4: Implement the migrator + store**

```csharp
// src/LocalScribe.Core/Storage/SettingsMigrator.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>settings.json v1 -> v2 (spec §7). Adds new sections at defaults, disables auto-detect,
/// preserves an explicitly stored audioRetention. Deserializes into the typed model so the shared
/// options apply on re-save (migration re-serialization rule).</summary>
public static class SettingsMigrator
{
    public static Settings Migrate(JsonObject raw)
    {
        int v = SchemaGuard.ReadVersion(raw);
        SchemaGuard.RejectIfNewer(v, SettingsStore.Version, "settings.json");

        if (v <= 1)
        {
            raw["audioFormat"] ??= "flac";
            raw["self"] ??= new JsonObject { ["name"] = "" };
            raw["remote"] ??= new JsonObject { ["mode"] = "auto" };
            raw["mic"] ??= new JsonObject { ["mode"] = "followDefault" };
            raw["overlay"] ??= new JsonObject
            {
                ["enabled"] = true, ["showSessionName"] = false,
                ["showLevelMeter"] = true, ["excludeFromCapture"] = true,
            };
            raw["vocabulary"] ??= new JsonObject { ["terms"] = new JsonArray(), ["corrections"] = new JsonObject() };

            if (raw["autoDetect"] is JsonObject ad) ad["enabled"] = false;
            else raw["autoDetect"] = new JsonObject { ["enabled"] = false, ["apps"] = new JsonArray("Teams", "Zoom", "Webex") };

            // audioRetention preserved verbatim (fresh installs never reach the migrator).
            raw["schemaVersion"] = 2;
        }
        return raw.Deserialize<Settings>(LocalScribeJson.Options)!;
    }
}
```
```csharp
// src/LocalScribe.Core/Storage/SettingsStore.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes settings.json (spec §7). Fresh install -> defaults (keep); migrates v1;
/// rejects a newer schema.</summary>
public sealed class SettingsStore
{
    public const int Version = 2;
    private readonly string _path;
    public SettingsStore(string settingsJsonPath) => _path = settingsJsonPath;

    public Task SaveAsync(Settings settings, CancellationToken ct)
        => JsonFile.WriteAsync(_path, settings with { SchemaVersion = Version }, ct);

    public async Task<Settings> LoadOrDefaultAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return new Settings();                       // fresh install -> keep default

        int v = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(v, Version, "settings.json");
        if (v < Version)
        {
            var migrated = SettingsMigrator.Migrate(obj);
            await SaveAsync(migrated, ct);
            return migrated;
        }
        return await JsonFile.ReadAsync<Settings>(_path, ct) ?? new Settings();
    }
}
```

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter SettingsTests` -> PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Model/Settings.cs src/LocalScribe.Core/Storage/SettingsStore.cs \
        src/LocalScribe.Core/Storage/SettingsMigrator.cs tests/LocalScribe.Core.Tests/SettingsTests.cs
git commit -m "feat: settings.json v2 + SettingsStore + v1->v2 migration (retention preserved)"
```

---

## Task 12: StoragePaths + SessionId + SyncProviderCheck  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Storage/StoragePaths.cs`, `src/LocalScribe.Core/Storage/SessionId.cs`, `src/LocalScribe.Core/Storage/SyncProviderCheck.cs`
- Test: `tests/LocalScribe.Core.Tests/StoragePathsTests.cs`

**Interfaces:**
- Consumes: `AppKind`, `SourceKind`, `AudioFormat`.
- Produces:
  - `sealed class StoragePaths(string configuredRoot)` — expands env vars + `Path.GetFullPath`; exposes `Root`, `SessionsDir`, `MattersDir`, `SessionDir(id)`, and per-file getters `SessionJson/MetaJson/TranscriptJsonl/EditsJson/SpeakersJson/TranscriptMd/TranscriptTxt/SessionTxt/SummaryMd(id)`, `AudioFile(id, SourceKind, AudioFormat)`, `MattersIndexJson`, `MatterJson(matterId)` — the §9 layout.
  - `static class SessionId` — `string New(DateTimeOffset startedAtLocal, AppKind app, string title)` -> `yyyy-MM-dd_HHmm_{App}_{slug}` from the **local wall-clock start time** (spec §9: the caller passes `startedAtUtc.ToOffset(utcOffsetMinutes)` — folder names match how the user remembers the meeting), formatted with `CultureInfo.InvariantCulture`; `string Slug(string)` (lowercase ASCII, non-alphanumerics -> single `-`, trimmed, `"session"` fallback); `string EnsureUnique(string candidate, Func<string,bool> exists)` — returns `candidate`, or `candidate-2`, `-3`, ... for the first free name (spec §9 collision policy; the session-start flow passes `id => Directory.Exists(paths.SessionDir(id))`).
  - `static class SyncProviderCheck` — `bool ResolvesUnderSyncProvider(string expandedPath, out string? provider)` (+ an overload taking an explicit `knownNames` list for testability) matching a path segment equal to or beginning `"<name> -"` (business folders like `OneDrive - Contoso`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/StoragePathsTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class StoragePathsTests
{
    [Fact]
    public void Root_expands_env_and_is_absolute_with_spec_layout()
    {
        var p = new StoragePaths("%USERPROFILE%/LocalScribe");
        Assert.True(Path.IsPathFullyQualified(p.Root));
        Assert.DoesNotContain("%", p.Root);
        Assert.EndsWith("LocalScribe", p.Root.TrimEnd('\\', '/'));
        Assert.Equal(Path.Combine(p.Root, "sessions"), p.SessionsDir);
        Assert.Equal(Path.Combine(p.Root, "matters"), p.MattersDir);
    }

    [Fact]
    public void Per_file_paths_follow_section_9()
    {
        var p = new StoragePaths(@"C:\Data\LocalScribe");
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\transcript.jsonl", p.TranscriptJsonl("s1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\session.json", p.SessionJson("s1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\session.txt", p.SessionTxt("s1"));
        Assert.Equal(@"C:\Data\LocalScribe\matters\matters.json", p.MattersIndexJson);
        Assert.Equal(@"C:\Data\LocalScribe\matters\M-1\matter.json", p.MatterJson("M-1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\local.flac", p.AudioFile("s1", SourceKind.Local, AudioFormat.Flac));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\remote.wav", p.AudioFile("s1", SourceKind.Remote, AudioFormat.Wav));
    }

    [Fact]
    public void SessionId_uses_local_wall_clock_time()
    {
        // Spec 1.2 example: started 06:32:05Z at +08:00 (Singapore) -> local 14:32 -> id 1432.
        var startedLocal = new DateTimeOffset(2026, 7, 2, 14, 32, 5, TimeSpan.FromHours(8));
        Assert.Equal("2026-07-02_1432_Webex_doe-intake",
            SessionId.New(startedLocal, AppKind.Webex, "Doe intake"));
    }

    [Theory]
    [InlineData("Doe v. State", "doe-v-state")]
    [InlineData("  Weekly  Sync!! ", "weekly-sync")]
    [InlineData("***", "session")]
    public void Slug_normalizes(string input, string expected)
        => Assert.Equal(expected, SessionId.Slug(input));

    [Fact]
    public void EnsureUnique_returns_candidate_or_first_free_numeric_suffix()
    {
        Assert.Equal("2026-07-02_1432_Webex_doe-intake",
            SessionId.EnsureUnique("2026-07-02_1432_Webex_doe-intake", _ => false));

        var taken = new HashSet<string> { "2026-07-02_1432_Webex_doe-intake", "2026-07-02_1432_Webex_doe-intake-2" };
        Assert.Equal("2026-07-02_1432_Webex_doe-intake-3",
            SessionId.EnsureUnique("2026-07-02_1432_Webex_doe-intake", taken.Contains));
    }

    [Theory]
    [InlineData(@"C:\Users\sam\OneDrive\LocalScribe", true, "OneDrive")]
    [InlineData(@"C:\Users\sam\OneDrive - Contoso\LocalScribe", true, "OneDrive")]
    [InlineData(@"C:\Users\sam\Dropbox\LocalScribe", true, "Dropbox")]
    [InlineData(@"C:\Users\sam\LocalScribe", false, null)]
    public void SyncProviderCheck_flags_known_providers(string path, bool expected, string? provider)
    {
        bool got = SyncProviderCheck.ResolvesUnderSyncProvider(path, out string? p);
        Assert.Equal(expected, got);
        Assert.Equal(provider, p);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter StoragePathsTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Storage/StoragePaths.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Resolves the storage root and the §9 session/matter folder layout. All getters are pure.</summary>
public sealed class StoragePaths
{
    public string Root { get; }
    public StoragePaths(string configuredRoot)
        => Root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredRoot));

    public string SessionsDir => Path.Combine(Root, "sessions");
    public string MattersDir => Path.Combine(Root, "matters");
    public string SessionDir(string id) => Path.Combine(SessionsDir, id);

    public string SessionJson(string id) => Path.Combine(SessionDir(id), "session.json");
    public string MetaJson(string id) => Path.Combine(SessionDir(id), "meta.json");
    public string TranscriptJsonl(string id) => Path.Combine(SessionDir(id), "transcript.jsonl");
    public string EditsJson(string id) => Path.Combine(SessionDir(id), "edits.json");
    public string SpeakersJson(string id) => Path.Combine(SessionDir(id), "speakers.json");
    public string TranscriptMd(string id) => Path.Combine(SessionDir(id), "transcript.md");
    public string TranscriptTxt(string id) => Path.Combine(SessionDir(id), "transcript.txt");
    public string SessionTxt(string id) => Path.Combine(SessionDir(id), "session.txt");
    public string SummaryMd(string id) => Path.Combine(SessionDir(id), "summary.md");

    public string AudioFile(string id, SourceKind source, AudioFormat format)
    {
        string stem = source == SourceKind.Local ? "local" : "remote";
        string ext = format == AudioFormat.Flac ? "flac" : "wav";
        return Path.Combine(SessionDir(id), $"{stem}.{ext}");
    }

    public string MattersIndexJson => Path.Combine(MattersDir, "matters.json");
    public string MatterJson(string matterId) => Path.Combine(MattersDir, matterId, "matter.json");
}
```
```csharp
// src/LocalScribe.Core/Storage/SessionId.cs
using System.Globalization;
using System.Text;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Deterministic session-folder id: yyyy-MM-dd_HHmm_{App}_{slug} on the LOCAL
/// wall-clock start time (spec §9) - the caller applies the session's utcOffsetMinutes.
/// Invariant culture: folder names must be identical regardless of the machine's calendar.</summary>
public static class SessionId
{
    public static string New(DateTimeOffset startedAtLocal, AppKind app, string title)
        => $"{startedAtLocal.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture)}_{app}_{Slug(title)}";

    /// <summary>Spec §9 collision policy: same minute + app + slug gets -2, -3, ...</summary>
    public static string EnsureUnique(string candidate, Func<string, bool> exists)
    {
        if (!exists(candidate)) return candidate;
        for (int n = 2; ; n++)
        {
            string alt = $"{candidate}-{n}";
            if (!exists(alt)) return alt;
        }
    }

    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        bool pendingDash = false;
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (c < 128 && char.IsLetterOrDigit(c))
            {
                if (pendingDash && sb.Length > 0) sb.Append('-');
                sb.Append(c);
                pendingDash = false;
            }
            else if (sb.Length > 0)
            {
                pendingDash = true;   // collapse runs of separators into a single dash, deferred
            }
        }
        string slug = sb.ToString();
        return slug.Length == 0 ? "session" : slug;
    }
}
```
```csharp
// src/LocalScribe.Core/Storage/SyncProviderCheck.cs
namespace LocalScribe.Core.Storage;

/// <summary>Warns when the storage root resolves under a known sync provider (spec §7/Storage
/// format) - pushing audio/transcripts off-machine fights the local-only goal.</summary>
public static class SyncProviderCheck
{
    private static readonly string[] Known = { "OneDrive", "Dropbox", "Google Drive", "GoogleDrive" };

    public static bool ResolvesUnderSyncProvider(string expandedPath, out string? provider)
        => ResolvesUnderSyncProvider(expandedPath, Known, out provider);

    public static bool ResolvesUnderSyncProvider(string expandedPath, IReadOnlyList<string> knownNames, out string? provider)
    {
        string[] segments = expandedPath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (string name in knownNames)
        {
            bool hit = segments.Any(seg =>
                seg.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                seg.StartsWith(name + " -", StringComparison.OrdinalIgnoreCase));   // "OneDrive - Contoso"
            if (hit) { provider = name; return true; }
        }
        provider = null;
        return false;
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter StoragePathsTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Storage/StoragePaths.cs src/LocalScribe.Core/Storage/SessionId.cs \
        src/LocalScribe.Core/Storage/SyncProviderCheck.cs tests/LocalScribe.Core.Tests/StoragePathsTests.cs
git commit -m "feat: StoragePaths (spec 9 layout) + local-time SessionId with collision suffix + sync-provider warning"
```

---

## Task 13: IVocabularyProvider + VocabularyProvider (bias + heard->correct)  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Vocabulary/IVocabularyProvider.cs`, `src/LocalScribe.Core/Vocabulary/VocabularyProvider.cs`
- Test: `tests/LocalScribe.Core.Tests/VocabularyProviderTests.cs`

**Interfaces:**
- Consumes: `Vocabulary`, `Matter`.
- Produces:
  - `interface IVocabularyProvider { string BuildInitialPrompt(IReadOnlyList<string> matterIds); string ApplyCorrections(string text, IReadOnlyList<string> matterIds); }`
  - `sealed class VocabularyProvider(Vocabulary global, IReadOnlyDictionary<string,Matter> mattersById, int maxPromptTokens = 200) : IVocabularyProvider`.
- **Semantics (spec §1.7/§10):** effective vocabulary = **global ∪ matters(session)**. Terms: global first, then each matter's, de-duplicated case-insensitively (first occurrence wins). Corrections: merged, **matter overrides global** on key conflict. `BuildInitialPrompt` joins effective terms with `", "` up to an approximate `maxPromptTokens` budget (token estimate = whitespace-word count per term; the real tokenizer is Whisper's, so this is a conservative shortlist bound; §3/§10). `ApplyCorrections` replaces each `heard` key **whole-word, case-insensitive**, applied longest-key-first so a longer phrase is not clobbered by a shorter sub-match. Corrections are projection-only — they never mutate JSONL.
- **Word boundaries via lookarounds, not `\b`:** the match pattern is `(?<!\w){key}(?!\w)` — `\b` anchors silently never match when a key starts/ends with a non-word character (`c#`, `.net`), which would make such corrections dead entries. Lookarounds give the same whole-word behavior for plain keys and correct behavior for punctuation-edged ones.
- **Documented determinism caveat:** replacements apply sequentially longest-key-first over the running text, so one rule's *output* can legally match a later rule's key (chained rewrite). This is deterministic and accepted; keep correction values out of other keys to avoid surprises.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/VocabularyProviderTests.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Vocabulary;

public class VocabularyProviderTests
{
    private static Matter MatterWith(string id, Vocabulary v) => new() { Id = id, Name = id, Vocabulary = v };

    [Fact]
    public void Prompt_unions_global_and_matter_terms_deduped()
    {
        var global = new Vocabulary { Terms = new[] { "OAuth", "arraignment" } };
        var matter = MatterWith("M1", new Vocabulary { Terms = new[] { "arraignment", "Doe" } });   // dupe drops
        var vp = new VocabularyProvider(global, new Dictionary<string, Matter> { ["M1"] = matter });

        string prompt = vp.BuildInitialPrompt(new[] { "M1" });
        Assert.Equal("OAuth, arraignment, Doe", prompt);
    }

    [Fact]
    public void Prompt_is_bounded_by_max_tokens()
    {
        var global = new Vocabulary { Terms = new[] { "one", "two", "three", "four" } };
        var vp = new VocabularyProvider(global, new Dictionary<string, Matter>(), maxPromptTokens: 2);
        Assert.Equal("one, two", vp.BuildInitialPrompt(Array.Empty<string>()));
    }

    [Fact]
    public void Corrections_apply_whole_word_case_insensitive_and_matter_overrides_global()
    {
        var global = new Vocabulary { Corrections = new Dictionary<string, string> { ["auth"] = "OAuth" } };
        var matter = MatterWith("M1", new Vocabulary { Corrections = new Dictionary<string, string> { ["auth"] = "AUTH-OVERRIDE" } });
        var vp = new VocabularyProvider(global, new Dictionary<string, Matter> { ["M1"] = matter });

        // matter override wins; whole word only (authentication untouched); case-insensitive match
        Assert.Equal("AUTH-OVERRIDE and authentication",
            vp.ApplyCorrections("Auth and authentication", new[] { "M1" }));
    }

    [Fact]
    public void Corrections_with_punctuation_edged_keys_still_match()
    {
        // \b would silently never match "c#" (non-word edge); lookarounds must.
        var global = new Vocabulary { Corrections = new Dictionary<string, string> { ["c#"] = "C#" } };
        var vp = new VocabularyProvider(global, new Dictionary<string, Matter>());
        Assert.Equal("we use C# daily", vp.ApplyCorrections("we use c# daily", Array.Empty<string>()));
        Assert.Equal("c#x untouched", vp.ApplyCorrections("c#x untouched", Array.Empty<string>()));   // not whole-word
    }

    [Fact]
    public void No_vocab_is_identity()
    {
        var vp = new VocabularyProvider(new Vocabulary(), new Dictionary<string, Matter>());
        Assert.Equal("", vp.BuildInitialPrompt(Array.Empty<string>()));
        Assert.Equal("hello world", vp.ApplyCorrections("hello world", Array.Empty<string>()));
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter VocabularyProviderTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Vocabulary/IVocabularyProvider.cs
namespace LocalScribe.Core.Vocabulary;

public interface IVocabularyProvider
{
    /// <summary>Bounded ~N-token initial-prompt bias shortlist for whisper.cpp (spec §3/§10).</summary>
    string BuildInitialPrompt(IReadOnlyList<string> matterIds);

    /// <summary>Deterministic heard->correct post-pass (projection-only; spec §6.1 step 2).</summary>
    string ApplyCorrections(string text, IReadOnlyList<string> matterIds);
}
```
```csharp
// src/LocalScribe.Core/Vocabulary/VocabularyProvider.cs
using System.Text.RegularExpressions;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Vocabulary;

/// <summary>Effective vocabulary = global (settings) UNION matters(session), consumed two ways:
/// initial-prompt bias + a projection-time heard->correct pass (spec §1.7/§10).</summary>
public sealed class VocabularyProvider : IVocabularyProvider
{
    private readonly Model.Vocabulary _global;
    private readonly IReadOnlyDictionary<string, Matter> _mattersById;
    private readonly int _maxPromptTokens;

    public VocabularyProvider(Model.Vocabulary global, IReadOnlyDictionary<string, Matter> mattersById,
        int maxPromptTokens = 200)
        => (_global, _mattersById, _maxPromptTokens) = (global, mattersById, maxPromptTokens);

    public string BuildInitialPrompt(IReadOnlyList<string> matterIds)
    {
        var chosen = new List<string>();
        int tokens = 0;
        foreach (string term in EffectiveTerms(matterIds))
        {
            int t = term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (tokens + t > _maxPromptTokens) break;
            chosen.Add(term);
            tokens += t;
        }
        return string.Join(", ", chosen);
    }

    public string ApplyCorrections(string text, IReadOnlyList<string> matterIds)
    {
        // Sequential, longest-key-first over the running text: one rule's output can match a
        // later rule's key (deterministic, documented). Lookarounds instead of \b so keys with
        // non-word edges ("c#", ".net") still match whole-word.
        foreach (var kv in EffectiveCorrections(matterIds).OrderByDescending(k => k.Key.Length))
        {
            string replacement = kv.Value.Replace("$", "$$");   // escape $ in the regex replacement
            text = Regex.Replace(text, $@"(?<!\w){Regex.Escape(kv.Key)}(?!\w)", replacement, RegexOptions.IgnoreCase);
        }
        return text;
    }

    private List<string> EffectiveTerms(IReadOnlyList<string> matterIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        void Add(IEnumerable<string> terms)
        {
            foreach (string t in terms)
                if (t.Length > 0 && seen.Add(t)) result.Add(t);
        }
        Add(_global.Terms);
        foreach (string id in matterIds)
            if (_mattersById.TryGetValue(id, out Matter? m)) Add(m.Vocabulary.Terms);
        return result;
    }

    private Dictionary<string, string> EffectiveCorrections(IReadOnlyList<string> matterIds)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _global.Corrections) map[kv.Key] = kv.Value;
        foreach (string id in matterIds)
            if (_mattersById.TryGetValue(id, out Matter? m))
                foreach (var kv in m.Vocabulary.Corrections) map[kv.Key] = kv.Value;   // matter overrides global
        return map;
    }
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter VocabularyProviderTests` -> PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Vocabulary tests/LocalScribe.Core.Tests/VocabularyProviderTests.cs
git commit -m "feat: VocabularyProvider (global union matter, bounded prompt bias, heard->correct)"
```

---

## Task 14: IRenderDedup (+NoOp) + TranscriptProjection apply-order engine  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Projection/ProjectedSegment.cs`, `src/LocalScribe.Core/Projection/IRenderDedup.cs`, `src/LocalScribe.Core/Projection/DisplayRow.cs`, `src/LocalScribe.Core/Projection/TranscriptProjection.cs`
- Test: `tests/LocalScribe.Core.Tests/TranscriptProjectionTests.cs`

**Interfaces:**
- Consumes: `TranscriptLine`, `TranscriptKind`, `TranscriptSource`, `Speakers`, `Edits`, `SessionMeta`, `NameResolver`, `IVocabularyProvider`.
- Produces:
  - `sealed record ProjectedSegment(TranscriptLine Line, string Text)` with pass-through `Seq/Source/StartMs/EndMs`.
  - `interface IRenderDedup { IReadOnlyList<ProjectedSegment> Filter(IReadOnlyList<ProjectedSegment> segments); }` + `sealed class NoOpDedup : IRenderDedup` (returns input unchanged — the real phantom-bleed heuristic is Stage 2b, §5).
  - `sealed record DisplayRow { bool IsMarker; long StartMs; string? DisplayName; string Text; }`.
  - `sealed class TranscriptProjection(IVocabularyProvider vocab, IRenderDedup dedup)` — `IReadOnlyList<DisplayRow> Build(IReadOnlyList<TranscriptLine> lines, Speakers? speakers, Edits? edits, SessionMeta meta)` implementing the canonical §6.1 apply-order.
- **Apply-order (spec §6.1), exactly:** (1) partition segments/markers; (2) vocabulary `heard->correct` on each segment's text; (3) overlay `edits.json[seq].text` verbatim (human correction supersedes the vocabulary result); (4) `dedup.Filter` the segments; (5) resolve `DisplayName` via `NameResolver`; (6) order by `StartMs` asc, tie-break source (`Local`<`Remote`<`System`) then `seq`, and group consecutive same-`DisplayName` segments into one space-joined turn (markers are standalone rows and break grouping). QA fields are never surfaced.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/TranscriptProjectionTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Vocabulary;

public class TranscriptProjectionTests
{
    private static TranscriptProjection Sut(IVocabularyProvider? vocab = null) =>
        new(vocab ?? new VocabularyProvider(new Vocabulary(), new Dictionary<string, Matter>()), new NoOpDedup());

    private static SessionMeta Meta(int local = 2, int remote = 2, params SessionParticipant[] ps) =>
        new() { LocalCount = local, RemoteCount = remote, Participants = ps };

    [Fact]
    public void Consecutive_same_speaker_segments_group_into_one_turn()
    {
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Morning.", "Me"),
            TranscriptLine.Segment(1, TranscriptSource.Local, 1000, 2000, "Quick recap.", "Me"),
            TranscriptLine.Segment(2, TranscriptSource.Remote, 2000, 3000, "Sure.", "Them"),
        };
        var rows = Sut().Build(lines, speakers: null, edits: null, Meta());
        Assert.Equal(2, rows.Count);
        Assert.Equal("Me", rows[0].DisplayName);
        Assert.Equal("Morning. Quick recap.", rows[0].Text);   // space-joined
        Assert.Equal("Them", rows[1].DisplayName);
    }

    [Fact]
    public void Markers_sort_into_timeline_by_startMs_and_break_grouping()
    {
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "a", "Me"),
            TranscriptLine.Marker(1, 1500, Markers.AudioDeviceChanged),
            TranscriptLine.Segment(2, TranscriptSource.Local, 2000, 3000, "b", "Me"),
        };
        var rows = Sut().Build(lines, null, null, Meta());
        Assert.Equal(3, rows.Count);                            // marker splits the two "Me" turns
        Assert.False(rows[0].IsMarker);
        Assert.True(rows[1].IsMarker);
        Assert.Equal("audio device changed", rows[1].Text);
        Assert.False(rows[2].IsMarker);
    }

    [Fact]
    public void Display_order_is_startMs_then_local_before_remote()
    {
        // Remote finalized first (lower seq) but starts later; Local starts earlier.
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Remote, 500, 1500, "remote", "Them"),
            TranscriptLine.Segment(1, TranscriptSource.Local, 0, 400, "local", "Me"),
        };
        var rows = Sut().Build(lines, null, null, Meta());
        Assert.Equal("Me", rows[0].DisplayName);                // startMs 0 first
        Assert.Equal("Them", rows[1].DisplayName);
    }

    [Fact]
    public void Vocabulary_then_edits_supersede_with_human_winning()
    {
        var global = new Vocabulary { Corrections = new Dictionary<string, string> { ["auth"] = "OAuth" } };
        var vocab = new VocabularyProvider(global, new Dictionary<string, Matter>());
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "the auth change", "Them"),
            TranscriptLine.Segment(1, TranscriptSource.Remote, 1000, 2000, "the auth change", "Them"),
        };
        var edits = new Edits { Corrections = new Dictionary<string, Correction> { ["1"] = new() { Text = "HUMAN EDIT" } } };
        var rows = Sut(vocab).Build(lines, null, edits, Meta(remote: 2));

        // seq 0: vocabulary applied -> "the OAuth change"; seq 1: human edit wins verbatim.
        // Both are "Them" so they group: "the OAuth change HUMAN EDIT"
        Assert.Single(rows);
        Assert.Equal("the OAuth change HUMAN EDIT", rows[0].Text);
    }

    [Fact]
    public void Single_declared_participant_name_flows_through()
    {
        var meta = Meta(1, 1, new SessionParticipant { Id = "p", Name = "Alice Client", Side = SourceKind.Remote });
        var lines = new[] { TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "hi", "Them") };
        var rows = Sut().Build(lines, null, null, meta);
        Assert.Equal("Alice Client", rows[0].DisplayName);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter TranscriptProjectionTests` -> FAIL.

- [ ] **Step 3: Implement the supporting types**

```csharp
// src/LocalScribe.Core/Projection/ProjectedSegment.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>A segment paired with its projected text (post vocabulary + edits), carrying the
/// original line so name resolution (which needs seq/source/speakerLabel) still works.</summary>
public sealed record ProjectedSegment(TranscriptLine Line, string Text)
{
    public int Seq => Line.Seq;
    public TranscriptSource Source => Line.Source;
    public long StartMs => Line.StartMs;
    public long EndMs => Line.EndMs;
}
```
```csharp
// src/LocalScribe.Core/Projection/IRenderDedup.cs
namespace LocalScribe.Core.Projection;

/// <summary>Apply-order step 4 (spec §5/§6.1): MAY hide phantom-bleed segments. Stage 2a ships the
/// no-op; the real energy/text heuristic lands in Stage 2b where golden-corpus data exists to tune it.</summary>
public interface IRenderDedup
{
    IReadOnlyList<ProjectedSegment> Filter(IReadOnlyList<ProjectedSegment> segments);
}

public sealed class NoOpDedup : IRenderDedup
{
    public IReadOnlyList<ProjectedSegment> Filter(IReadOnlyList<ProjectedSegment> segments) => segments;
}
```
```csharp
// src/LocalScribe.Core/Projection/DisplayRow.cs
namespace LocalScribe.Core.Projection;

/// <summary>One rendered row: a grouped speaker turn (IsMarker=false, DisplayName set) or a
/// standalone system marker (IsMarker=true, DisplayName null).</summary>
public sealed record DisplayRow
{
    public bool IsMarker { get; init; }
    public long StartMs { get; init; }
    public string? DisplayName { get; init; }
    public string Text { get; init; } = "";
}
```

- [ ] **Step 4: Implement the projection engine**

```csharp
// src/LocalScribe.Core/Projection/TranscriptProjection.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Projection;

/// <summary>The canonical projection apply-order (spec §6.1) shared by transcript.md/.txt,
/// session.txt, live view, and .docx. Pure - no IO.</summary>
public sealed class TranscriptProjection
{
    private readonly IVocabularyProvider _vocab;
    private readonly IRenderDedup _dedup;
    public TranscriptProjection(IVocabularyProvider vocab, IRenderDedup dedup) => (_vocab, _dedup) = (vocab, dedup);

    private sealed record PreRow(long StartMs, int SourceRank, int Seq, string? Name, string Text, bool IsMarker);

    public IReadOnlyList<DisplayRow> Build(
        IReadOnlyList<TranscriptLine> lines, Speakers? speakers, Edits? edits, SessionMeta meta)
    {
        var matterIds = meta.MatterIds;

        // (1)-(3): partition; vocabulary pass; edits overlay (human verbatim wins).
        var projected = new List<ProjectedSegment>();
        var markers = new List<TranscriptLine>();
        foreach (var line in lines)
        {
            if (line.Kind == TranscriptKind.Marker) { markers.Add(line); continue; }
            string text = _vocab.ApplyCorrections(line.Text, matterIds);
            if (edits is not null && edits.Corrections.TryGetValue(line.Seq.ToString(), out Correction? c))
                text = c.Text;
            projected.Add(new ProjectedSegment(line, text));
        }

        // (4): dedup.
        var kept = _dedup.Filter(projected);

        // (5): name resolution -> flat pre-rows for segments and markers.
        var pre = new List<PreRow>();
        foreach (var s in kept)
            pre.Add(new PreRow(s.StartMs, Rank(s.Source), s.Seq,
                NameResolver.Resolve(s.Line, speakers, meta), s.Text, IsMarker: false));
        foreach (var m in markers)
            pre.Add(new PreRow(m.StartMs, Rank(m.Source), m.Seq, Name: null, m.Text, IsMarker: true));

        // (6): order (startMs, source rank, seq) then group consecutive same-name segments.
        pre.Sort((a, b) =>
        {
            int c = a.StartMs.CompareTo(b.StartMs);
            if (c != 0) return c;
            c = a.SourceRank.CompareTo(b.SourceRank);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        });

        var rows = new List<DisplayRow>();
        foreach (var p in pre)
        {
            if (p.IsMarker)
            {
                rows.Add(new DisplayRow { IsMarker = true, StartMs = p.StartMs, Text = p.Text });
                continue;
            }
            if (rows.Count > 0 && rows[^1] is { IsMarker: false } last && last.DisplayName == p.Name)
                rows[^1] = last with { Text = last.Text + " " + p.Text };
            else
                rows.Add(new DisplayRow { IsMarker = false, StartMs = p.StartMs, DisplayName = p.Name, Text = p.Text });
        }
        return rows;
    }

    private static int Rank(TranscriptSource s) => s switch
    {
        TranscriptSource.Local => 0,
        TranscriptSource.Remote => 1,
        _ => 2,   // System (markers)
    };
}
```

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter TranscriptProjectionTests` -> PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Projection/ProjectedSegment.cs src/LocalScribe.Core/Projection/IRenderDedup.cs \
        src/LocalScribe.Core/Projection/DisplayRow.cs src/LocalScribe.Core/Projection/TranscriptProjection.cs \
        tests/LocalScribe.Core.Tests/TranscriptProjectionTests.cs
git commit -m "feat: TranscriptProjection apply-order engine + IRenderDedup/NoOp (spec 6.1)"
```

---

## Task 15: Renderers — Markdown, plain text, session.txt  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Projection/TranscriptHeader.cs`, `src/LocalScribe.Core/Projection/TimestampFormat.cs`, `src/LocalScribe.Core/Projection/MarkdownRenderer.cs`, `src/LocalScribe.Core/Projection/PlainTextRenderer.cs`, `src/LocalScribe.Core/Projection/SessionTextRenderer.cs`
- Test: `tests/LocalScribe.Core.Tests/RendererTests.cs`

**Interfaces:**
- Consumes: `DisplayRow`.
- Produces:
  - `sealed record TranscriptHeader(string Title, string App, DateTimeOffset StartedAtLocal, long DurationMs, string Model, string Backend)`.
  - `static class TimestampFormat` — `string Stamp(long startMs, string mode, DateTimeOffset startedAtLocal)`: `relative` -> `mm:ss` (or `h:mm:ss` at >= 1h) from `startMs`; `wallclock` -> `HH:mm:ss` of `startedAtLocal + startMs`.
  - `static class MarkdownRenderer` — `string Render(TranscriptHeader header, IReadOnlyList<DisplayRow> rows, string timestampsMode)` per §6: `# {title}` / `{app} · {date} · {dur} min · {model}/{backend}` / blank / `**[ts] Name:** text` turns / `_[msg]_` markers. The `·` separator is emitted via a `\u00B7` escape (ASCII source).
  - `static class PlainTextRenderer` — same content without Markdown decoration: `[ts] Name: text` turns / `[msg]` markers.
  - `sealed record SessionTextView(string Title, IReadOnlyList<string> Matters, IReadOnlyList<string> Participants, DateTimeOffset StartedAtLocal, DateTimeOffset? EndedAtLocal, long DurationMs, string Medium, string Description, string? Summary)` + `static class SessionTextRenderer { string Render(SessionTextView view); }` per §6.2 (neutral metadata block; `Summary: (none)` when null).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/RendererTests.cs
using LocalScribe.Core.Projection;

public class RendererTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 6, 30, 14, 32, 0, TimeSpan.Zero);   // fixed offset -> deterministic

    [Theory]
    [InlineData(1000, "00:01")]
    [InlineData(85320, "01:25")]
    [InlineData(3903000, "1:05:03")]   // >= 1h -> h:mm:ss
    public void Relative_timestamps_format(long ms, string expected)
        => Assert.Equal(expected, TimestampFormat.Stamp(ms, "relative", Started));

    [Fact]
    public void Wallclock_timestamp_adds_offset_to_start()
        => Assert.Equal("14:33:25", TimestampFormat.Stamp(85320, "wallclock", Started));

    [Fact]
    public void Markdown_renders_header_turns_and_markers()
    {
        var header = new TranscriptHeader("Weekly Sync", "Teams", Started, 2220000, "small.en", "CUDA");
        var rows = new[]
        {
            new DisplayRow { StartMs = 1000, DisplayName = "Sam", Text = "Morning everyone." },
            new DisplayRow { IsMarker = true, StartMs = 30000, Text = "audio device changed" },
            new DisplayRow { StartMs = 38000, DisplayName = "Bob", Text = "Question on tokens." },
        };
        string md = MarkdownRenderer.Render(header, rows, "relative");

        string expected =
            "# Weekly Sync\n" +
            "Teams \u00B7 2026-06-30 14:32 \u00B7 37 min \u00B7 small.en/CUDA\n" +
            "\n" +
            "**[00:01] Sam:** Morning everyone.\n" +
            "_[audio device changed]_\n" +
            "**[00:38] Bob:** Question on tokens.\n";
        Assert.Equal(expected, md);
    }

    [Fact]
    public void PlainText_has_no_markdown_decoration()
    {
        var header = new TranscriptHeader("Weekly Sync", "Teams", Started, 2220000, "small.en", "CUDA");
        var rows = new[]
        {
            new DisplayRow { StartMs = 1000, DisplayName = "Sam", Text = "Morning." },
            new DisplayRow { IsMarker = true, StartMs = 5000, Text = "paused by user" },
        };
        string txt = PlainTextRenderer.Render(header, rows, "relative");
        Assert.Contains("[00:01] Sam: Morning.", txt);
        Assert.Contains("[paused by user]", txt);
        Assert.DoesNotContain("**", txt);
        Assert.DoesNotContain("_[", txt);
    }

    [Fact]
    public void SessionText_renders_neutral_metadata_block()
    {
        var view = new SessionTextView(
            Title: "Doe intake \u2014 Webex",
            Matters: new[] { "Doe v. State (CR-2026-014)" },
            Participants: new[] { "Sam (Attorney, Local)", "Alice Client (Client, Remote)" },
            StartedAtLocal: Started,
            EndedAtLocal: Started.AddMinutes(37),
            DurationMs: 2220000,
            Medium: "Webex",
            Description: "Initial client interview.",
            Summary: null);
        string txt = SessionTextRenderer.Render(view);

        Assert.Contains("Doe intake \u2014 Webex", txt);
        Assert.Contains("Matter(s): Doe v. State (CR-2026-014)", txt);
        Assert.Contains("Participants: Sam (Attorney, Local), Alice Client (Client, Remote)", txt);
        Assert.Contains("Medium: Webex", txt);
        Assert.Contains("Summary: (none)", txt);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter RendererTests` -> FAIL.

- [ ] **Step 3: Implement the header + timestamp helper**

```csharp
// src/LocalScribe.Core/Projection/TranscriptHeader.cs
namespace LocalScribe.Core.Projection;

/// <summary>Header view-model for transcript.md/.txt (spec §6).</summary>
public sealed record TranscriptHeader(
    string Title, string App, DateTimeOffset StartedAtLocal, long DurationMs, string Model, string Backend);
```
```csharp
// src/LocalScribe.Core/Projection/TimestampFormat.cs
using System.Globalization;
namespace LocalScribe.Core.Projection;

/// <summary>mm:ss (or h:mm:ss >= 1h) relative to session start, or HH:mm:ss wall-clock (spec §6).
/// Invariant culture throughout (Global Constraints): projections must render byte-identical
/// regardless of the machine's calendar or digit substitution.</summary>
public static class TimestampFormat
{
    public static string Stamp(long startMs, string mode, DateTimeOffset startedAtLocal)
    {
        if (mode == "wallclock")
            return startedAtLocal.AddMilliseconds(startMs).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var span = TimeSpan.FromMilliseconds(startMs);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes:00}:{span.Seconds:00}");
    }
}
```

- [ ] **Step 4: Implement the three renderers**

```csharp
// src/LocalScribe.Core/Projection/MarkdownRenderer.cs
using System.Globalization;
using System.Text;
namespace LocalScribe.Core.Projection;

/// <summary>Renders transcript.md (spec §6). Non-ASCII separators via \u escapes (ASCII source).</summary>
public static class MarkdownRenderer
{
    private const string Dot = " \u00B7 ";   // middle dot separator

    public static string Render(TranscriptHeader header, IReadOnlyList<DisplayRow> rows, string timestampsMode)
    {
        long durationMin = (long)Math.Round(header.DurationMs / 60000.0);
        var sb = new StringBuilder();
        sb.Append('#').Append(' ').Append(header.Title).Append('\n');
        sb.Append(header.App).Append(Dot)
          .Append(header.StartedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(Dot)
          .Append(durationMin.ToString(CultureInfo.InvariantCulture)).Append(" min").Append(Dot)
          .Append(header.Model).Append('/').Append(header.Backend).Append('\n');
        sb.Append('\n');

        foreach (var row in rows)
        {
            if (row.IsMarker)
                sb.Append("_[").Append(row.Text).Append("]_").Append('\n');
            else
                sb.Append("**[").Append(TimestampFormat.Stamp(row.StartMs, timestampsMode, header.StartedAtLocal))
                  .Append("] ").Append(row.DisplayName).Append(":** ").Append(row.Text).Append('\n');
        }
        return sb.ToString();
    }
}
```
```csharp
// src/LocalScribe.Core/Projection/PlainTextRenderer.cs
using System.Globalization;
using System.Text;
namespace LocalScribe.Core.Projection;

/// <summary>Renders transcript.txt - the same content as §6 without Markdown decoration.</summary>
public static class PlainTextRenderer
{
    private const string Dot = " \u00B7 ";

    public static string Render(TranscriptHeader header, IReadOnlyList<DisplayRow> rows, string timestampsMode)
    {
        long durationMin = (long)Math.Round(header.DurationMs / 60000.0);
        var sb = new StringBuilder();
        sb.Append(header.Title).Append('\n');
        sb.Append(header.App).Append(Dot)
          .Append(header.StartedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(Dot)
          .Append(durationMin.ToString(CultureInfo.InvariantCulture)).Append(" min").Append(Dot)
          .Append(header.Model).Append('/').Append(header.Backend).Append('\n');
        sb.Append('\n');

        foreach (var row in rows)
        {
            if (row.IsMarker)
                sb.Append('[').Append(row.Text).Append(']').Append('\n');
            else
                sb.Append('[').Append(TimestampFormat.Stamp(row.StartMs, timestampsMode, header.StartedAtLocal))
                  .Append("] ").Append(row.DisplayName).Append(": ").Append(row.Text).Append('\n');
        }
        return sb.ToString();
    }
}
```
```csharp
// src/LocalScribe.Core/Projection/SessionTextRenderer.cs
using System.Globalization;
using System.Text;
namespace LocalScribe.Core.Projection;

/// <summary>Neutral, app-independent metadata projection - session.txt (spec §6.2).</summary>
public sealed record SessionTextView(
    string Title,
    IReadOnlyList<string> Matters,
    IReadOnlyList<string> Participants,
    DateTimeOffset StartedAtLocal,
    DateTimeOffset? EndedAtLocal,
    long DurationMs,
    string Medium,
    string Description,
    string? Summary);

public static class SessionTextRenderer
{
    public static string Render(SessionTextView v)
    {
        long durationMin = (long)Math.Round(v.DurationMs / 60000.0);
        string dateLine = v.EndedAtLocal is { } end
            ? string.Create(CultureInfo.InvariantCulture,
                $"{v.StartedAtLocal:yyyy-MM-dd HH:mm} - {end:HH:mm} ({durationMin} min)")
            : string.Create(CultureInfo.InvariantCulture,
                $"{v.StartedAtLocal:yyyy-MM-dd HH:mm} ({durationMin} min)");

        var sb = new StringBuilder();
        sb.Append(v.Title).Append('\n').Append('\n');
        sb.Append("Matter(s): ").Append(v.Matters.Count == 0 ? "(none)" : string.Join(", ", v.Matters)).Append('\n');
        sb.Append("Participants: ").Append(v.Participants.Count == 0 ? "(none)" : string.Join(", ", v.Participants)).Append('\n');
        sb.Append("Date: ").Append(dateLine).Append('\n');
        sb.Append("Medium: ").Append(v.Medium).Append('\n');
        sb.Append("Description: ").Append(string.IsNullOrEmpty(v.Description) ? "(none)" : v.Description).Append('\n');
        sb.Append("Summary: ").Append(string.IsNullOrEmpty(v.Summary) ? "(none)" : v.Summary).Append('\n');
        return sb.ToString();
    }
}
```

- [ ] **Step 5: Run to verify pass** — `dotnet test --filter RendererTests` -> PASS.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Projection/TranscriptHeader.cs src/LocalScribe.Core/Projection/TimestampFormat.cs \
        src/LocalScribe.Core/Projection/MarkdownRenderer.cs src/LocalScribe.Core/Projection/PlainTextRenderer.cs \
        src/LocalScribe.Core/Projection/SessionTextRenderer.cs tests/LocalScribe.Core.Tests/RendererTests.cs
git commit -m "feat: Markdown/PlainText/SessionText renderers (spec 6 / 6.2)"
```

---

## Task 16: SessionWriter facade + crash recovery  [UNIT]

**Files:**
- Create: `src/LocalScribe.Core/Storage/SessionWriter.cs`
- Test: `tests/LocalScribe.Core.Tests/SessionWriterTests.cs`

**Interfaces:**
- Consumes: `StoragePaths`, `Settings`, `SessionStore`, `MetadataStore`, `TranscriptStore`, `SpeakersStore`, `EditStore`, `MatterStore`, `VocabularyProvider`, `NoOpDedup`, `TranscriptProjection`, the renderers, `TimeProvider`, `Markers`.
- Produces (`sealed class SessionWriter(StoragePaths paths, Settings settings, TimeProvider time)`):
  - `Task RegenerateProjectionsAsync(string sessionId, CancellationToken ct)` — loads all truth (`session.json`, `meta.json`, `transcript.jsonl`, `speakers.json`, `edits.json`, referenced matters), builds the §6.1 projection, and (re)writes `transcript.md`, `transcript.txt`, `session.txt` **via `AtomicFile.WriteAllTextAsync`** (same never-half-written invariant as the JSON truth). Never writes `summary.md` (reserved). Displayed local start time = `StartedAtUtc.ToOffset(UtcOffsetMinutes)` when the session recorded its offset (spec §1.2) — deterministic and faithful to where the session happened; falls back to `ToLocalTime()` only for pre-v3 records with no offset.
  - `Task<bool> RecoverIfNeededAsync(string sessionId, CancellationToken ct)` — if `session.json` has `EndedAtUtc == null`: append a `recovered session` marker at the last segment's `endMs`, set `Recovered=true`, `EndedAtUtc = StartedAtUtc + lastEndMs`, `DurationMs = lastEndMs`, recompute `SegmentCount`/`MarkerCount`, then regenerate projections. Returns `true` when it recovered, `false` when already finalized/absent (idempotent).
- **Note (scope):** the launch-time *scan* that calls `RecoverIfNeededAsync` for every session folder is wired in Stage 3/4; 2a delivers the per-session operation + its unit test.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/LocalScribe.Core.Tests/SessionWriterTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SessionWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 14, 32, 0, TimeSpan.Zero);

    private static async Task SeedAsync(StoragePaths paths, string id, DateTimeOffset? endedAtUtc)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = endedAtUtc,
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            DurationMs = endedAtUtc is null ? 0 : 60000, Model = "small.en", Backend = "CUDA",
            Sources = new[] { SourceKind.Local, SourceKind.Remote },
        }, default);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Doe intake", Medium = Medium.Webex, LocalCount = 1, RemoteCount = 1 }, default);
        var t = new TranscriptStore(paths.TranscriptJsonl(id));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Hello there.", "Me"), default);
        await t.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Remote, 1000, 2000, "Hi.", "Them"), default);
    }

    [Fact]
    public async Task Regenerate_writes_the_three_readable_projections()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: T0.AddMinutes(1));
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            await writer.RegenerateProjectionsAsync("s1", default);

            Assert.True(File.Exists(paths.TranscriptMd("s1")));
            Assert.True(File.Exists(paths.TranscriptTxt("s1")));
            Assert.True(File.Exists(paths.SessionTxt("s1")));

            string md = await File.ReadAllTextAsync(paths.TranscriptMd("s1"));
            Assert.Contains("# Doe intake", md);
            Assert.Contains("Hello there.", md);
            // Local time from the STORED offset (14:32Z + 480 min), not the machine's zone.
            Assert.Contains("2026-07-02 22:32", md);
            Assert.False(File.Exists(paths.TranscriptMd("s1") + ".tmp"));   // atomic write cleaned up

            string sessionTxt = await File.ReadAllTextAsync(paths.SessionTxt("s1"));
            Assert.Contains("Doe intake", sessionTxt);
            Assert.Contains("Medium: Webex", sessionTxt);

            Assert.False(File.Exists(paths.SummaryMd("s1")));   // reserved, never generated
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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

    [Fact]
    public async Task Recovery_noop_on_already_finalized()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: T0.AddMinutes(1));
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            Assert.False(await writer.RecoverIfNeededAsync("s1", default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter SessionWriterTests` -> FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/LocalScribe.Core/Storage/SessionWriter.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Storage;

/// <summary>Regenerates the readable projections (transcript.md/.txt, session.txt) from the JSON
/// truth, and performs per-session crash recovery (spec §2.1/§6/Storage format). Pure orchestration
/// over the stores + projection; the launch-time recovery scan is wired in a later stage.</summary>
public sealed class SessionWriter
{
    private readonly StoragePaths _paths;
    private readonly Settings _settings;
    private readonly TimeProvider _time;

    public SessionWriter(StoragePaths paths, Settings settings, TimeProvider time)
        => (_paths, _settings, _time) = (paths, settings, time);

    public async Task RegenerateProjectionsAsync(string sessionId, CancellationToken ct)
    {
        var session = await new SessionStore(_paths.SessionJson(sessionId)).ReadAsync(ct)
                      ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
        // The session's own recorded offset (spec 1.2) keeps projections deterministic and
        // faithful to where the session happened; machine zone only for pre-v3 records.
        var startedLocal = session.UtcOffsetMinutes is int offsetMin
            ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : session.StartedAtUtc.ToLocalTime();
        var meta = await new MetadataStore(_paths.MetaJson(sessionId)).LoadAsync(ct)
                   ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(ct);
        var speakers = await new SpeakersStore(_paths.SpeakersJson(sessionId)).LoadAsync(ct);
        var edits = await new EditStore(_paths.SessionDir(sessionId), _time).LoadAsync(ct);

        // Resolve referenced matters (for vocabulary + the session.txt matter list).
        var matterStore = new MatterStore(_paths.MattersDir);
        var mattersById = new Dictionary<string, Matter>();
        var matterDisplays = new List<string>();
        foreach (string mid in meta.MatterIds)
        {
            var m = await matterStore.LoadAsync(mid, ct);
            if (m is null) { matterDisplays.Add(mid); continue; }
            mattersById[mid] = m;
            matterDisplays.Add(string.IsNullOrEmpty(m.Reference) ? m.Name : $"{m.Name} ({m.Reference})");
        }

        var projection = new TranscriptProjection(
            new VocabularyProvider(_settings.Vocabulary, mattersById), new NoOpDedup());
        var rows = projection.Build(lines, speakers, edits, meta);

        var header = new TranscriptHeader(meta.Title, session.App.ToString(), startedLocal,
            session.DurationMs, session.Model, session.Backend);

        await AtomicFile.WriteAllTextAsync(_paths.TranscriptMd(sessionId),
            MarkdownRenderer.Render(header, rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptTxt(sessionId),
            PlainTextRenderer.Render(header, rows, _settings.Timestamps), ct);

        var participants = meta.Participants.Select(p =>
            string.IsNullOrEmpty(p.Role) ? $"{p.Name} ({p.Side})" : $"{p.Name} ({p.Role}, {p.Side})").ToList();
        var view = new SessionTextView(meta.Title, matterDisplays, participants,
            startedLocal, session.EndedAtUtc?.ToLocalTime(), session.DurationMs,
            MediumDisplay(meta.Medium), meta.Description, Summary: null);   // summary reserved (Non-goal)
        await AtomicFile.WriteAllTextAsync(_paths.SessionTxt(sessionId), SessionTextRenderer.Render(view), ct);
    }

    public async Task<bool> RecoverIfNeededAsync(string sessionId, CancellationToken ct)
    {
        var sessionStore = new SessionStore(_paths.SessionJson(sessionId));
        var session = await sessionStore.ReadAsync(ct);
        if (session is null || session.EndedAtUtc is not null) return false;   // absent or already finalized

        var transcript = new TranscriptStore(_paths.TranscriptJsonl(sessionId));
        var before = await transcript.ReadAllAsync(ct);
        long lastEndMs = before.Count == 0 ? 0 : before.Max(l => l.EndMs);

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
        }, ct);

        await RegenerateProjectionsAsync(sessionId, ct);
        return true;
    }

    private static string MediumDisplay(Medium m) => m == Medium.InPerson ? "In-person" : m.ToString();
}
```

- [ ] **Step 4: Run to verify pass** — `dotnet test --filter SessionWriterTests` -> PASS.

- [ ] **Step 5: Run the full suite and commit**

Run: `dotnet test`
Expected: all Stage-1 + Stage-2a tests PASS (0 failures); `dotnet build` clean, 0 warnings.

```bash
git add src/LocalScribe.Core/Storage/SessionWriter.cs tests/LocalScribe.Core.Tests/SessionWriterTests.cs
git commit -m "feat: SessionWriter projection regeneration + per-session crash recovery"
```

---

## Stage 2a — Definition of Done

- [ ] `dotnet build` clean across all projects on `net10.0-windows`, **0 warnings**.
- [ ] `dotnet test` green — every task's `[UNIT]` suite (Tasks 1-16) passes with zero hardware.
- [ ] **No new NuGet packages** added to `LocalScribe.Core` (System.Text.Json + `TimeProvider` are framework); the only test-side addition is the hand-rolled `ManualUtcTimeProvider`.
- [ ] Every schema round-trips through `LocalScribeJson.Options` at its spec shape (camelCase, UTC-`Z` whole-second, enum wire strings, null-omission) and **rejects a newer `schemaVersion`**.
- [ ] Migrations verified: `session.json` v1->v2->v3 (with `meta.json` synthesis, **meta written before session** so a crash never drops the title) and `settings.json` v1->v2 (retention preserved, auto-detect disabled) — each finishing through the typed model (migration re-serialization rule).
- [ ] `transcript.jsonl` is torn-tail tolerant: a partial final line is skipped+counted on read (never rewritten), and appends self-heal line termination (spec §1.1).
- [ ] `session.json` v3 carries `timeZoneId`/`utcOffsetMinutes`; session ids derive from **local wall-clock time** with the `-2`/`-3` collision suffix (spec §9); all on-disk ids and rendered dates format with `CultureInfo.InvariantCulture`.
- [ ] Projection apply-order matches spec §6.1 (vocabulary -> edits-verbatim -> dedup seam -> name resolution -> order+group), and the three readable projections regenerate from JSON truth via `AtomicFile` (no half-written files anywhere).
- [ ] Per-session crash recovery reconstructs `EndedAtUtc`/counts, appends the `recovered session` marker, regenerates projections, is idempotent, and succeeds on a torn-tail JSONL.
- [ ] Edits are seq-validated (existing segment only; source-matched for reassignment) and finalized-only.
- [ ] ASCII-only source confirmed (spec output glyphs `·`/`→`/`—` emitted via `\u` escapes).

## Public surface produced for Stage 2b (interface index)

Stage 2b (VAD -> Whisper -> merge -> offline runner) consumes, from `LocalScribe.Core`:
- **Model:** `TranscriptLine` (+`TranscriptKind`/`TranscriptSource`), `Markers`, `SessionRecord`/`DeviceSnapshot`, `SessionMeta`/`SessionParticipant`, `Matter`/`RosterMember`/`Vocabulary`/`MattersIndex`, `Speakers`, `Edits`/`Correction`, `Settings` (+nested), enums.
- **Storage:** `LocalScribeJson`, `AtomicFile`, `JsonFile`, `SchemaGuard`, `TranscriptStore` (+`TranscriptReadResult`), `SessionStore`, `MetadataStore`, `SessionMigrator`, `MatterStore`, `SpeakersStore`, `EditStore`, `SettingsStore`/`SettingsMigrator`, `StoragePaths`, `SessionId` (incl. `EnsureUnique`), `SyncProviderCheck`, `SessionWriter`.
- **Projection:** `NameResolver`, `IRenderDedup`/`NoOpDedup`, `ProjectedSegment`, `DisplayRow`, `TranscriptProjection`, `TranscriptHeader`, `TimestampFormat`, the three renderers.
- **Vocabulary:** `IVocabularyProvider`/`VocabularyProvider` (2b feeds `BuildInitialPrompt` to the Whisper engine).

Note for 2b: the design's `AudioSegment(SourceKind Source, long StartMs, long EndMs, ReadOnlyMemory<float> Pcm)` is a **2b** type (VAD output); the pipeline turns each transcribed `AudioSegment` into a `TranscriptLine.Segment(...)` and appends it via `TranscriptStore`, assigning `seq` from `TranscriptStore.NextSeqAsync` / a merger-owned counter.

Note for 2b/3 (session start): the start flow must capture `TimeZoneInfo.Local.Id` and the DST-resolved offset (`TimeZoneInfo.Local.GetUtcOffset(startedAtUtc)`) into `SessionRecord.TimeZoneId`/`UtcOffsetMinutes`, derive the folder id via `SessionId.New(startedAtUtc.ToOffset(offset), app, title)`, and de-collide it with `SessionId.EnsureUnique(id, x => Directory.Exists(paths.SessionDir(x)))` (spec §1.2/§9).

## Explicitly NOT in Stage 2a (recap)

VAD/Whisper/backend-cascade/live-merger/audio-writing/offline-runner (2b) · real phantom-bleed dedup heuristic (2b) · marker/error *emission* by capture/model (2b/Stage 7) · diarisation (Stage 5) · matter session-count recompute + manager UI (Stage 4) · `.docx` export (fast-follow) · launch-time recovery scan orchestration (Stage 3/4).
