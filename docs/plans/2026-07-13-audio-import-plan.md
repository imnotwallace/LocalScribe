# Audio Import Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement design §4 (docs/plans/2026-07-13-meetily-round-design.md): import a lawyer-receivable audio file (WAV/FLAC/MP3/M4A/AAC/WMA/OGG) as a normal v1-root session — original bytes archived unmodified under `source\` with SHA-256 + timestamps, decoded via a SHA-pinned FFmpeg (decoded-stream truth, never container headers), a >1% decoded-vs-claimed duration mismatch gated Continue/Cancel with a transcript marker, stereo channel mapping (L→Local / R→Remote with swap, else mono downmix), FLAC legs + transcription via the existing `OfflinePipelineRunner`, staged progress with Cancel (cancel deletes only the partial session folder), and a Sessions-page "Import audio…" dialog whose editable recorded-date drives the session id/StartedAtUtc.

**Architecture:** Core gains an `Import\` namespace: `IAudioDecoder` (probe = container claims; decode = subprocess → PCM WAV at native rate/channels, truth read from the decoder's own output) with `FfmpegAudioDecoder` + `FfmpegLocator`; a pure `ChannelMapper` (plan + streaming 16 kHz mono leg WAVs); and `AudioImporter`, which bootstraps the session at the pinned recorded date (a frozen `TimeProvider` through the existing `SessionBootstrap`), copies+hashes the original, decodes, gates the duration mismatch through an injected `Func<DurationMismatchInfo, Task<bool>>`, writes markers, drives `OfflinePipelineRunner` into the pre-created folder via a new `OfflineRunOptions.ExistingSessionId`, then finalizes `session.json` (`Origin`/`ImportedSource`) and re-renders projections. `SessionRecord` gains additive `Origin` + `ImportedSource` (no schema bump — the `FellBackToDefault` precedent). The App adds an "Import audio…" button (disabled with a clear message when FFmpeg is absent — the Diarizer-helper pattern), a plain-Window `ImportDialog` + WPF-free `ImportDialogViewModel` (probe preview, editable title/recorded-date, Record-console matter picker, stereo question, staged progress, Cancel), wired in `App.xaml.cs` like the export dialog; completion upserts the row in place and opens the read view. `tools\fetch-ffmpeg.ps1` fetches the SHA-pinned BtbN LGPL shared build fail-closed.

**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, NAudio (WAV read/resample), CUETools.Codecs.FLAKE (FLAC legs), FFmpeg (LGPL shared, subprocess only), xUnit 2.9.3, PowerShell 7.

## Global Constraints
- Target branch: `feat/audio-import`, created off master AFTER `feat/retranscription-versions` merges. The design spec `docs/plans/2026-07-13-meetily-round-design.md` is already ON master (@ 7d6c88d); only this plan file `docs/plans/2026-07-13-audio-import-plan.md` needs adding to the branch, as its FIRST commit (`docs(plans): audio-import implementation plan` + trailer).
- 0-warning build gate must hold (`dotnet build --nologo -warnaserror` on the touched projects before every commit that changes production code).
- Tests: xUnit. Filtered run: `dotnet test "<testproj>" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\`
- IMPORTANT: the LocalScribe app may be running and LOCK bin DLLs (MSB3027 copy error — NOT a compile error): always use the isolated BaseOutputPath above; NEVER kill the user's running app. Caveat: `XamlHygieneTests` and the `Category=Fixture` tests locate the repo/models/ffmpeg by walking up from the test binary — under the isolated BaseOutputPath (which lives in %TEMP%, outside the repo) that walk finds nothing. If ONLY those tests fail under the isolated path, re-run the affected suite without `-p:BaseOutputPath` once the app is closed, or set `LOCALSCRIBE_FFMPEG`/`LOCALSCRIBE_MODELS` explicitly.
- Never use Unicode emojis in test code or scripts (project rule).
- Test projects: App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. App.Tests `<Compile Include>`-links `LiveTestDoubles.cs` from Core.Tests (so `LiveTestDoubles.MakeController/Options`, `GatedEngineFactory` are usable there). Fixture-gated tests carry `[Trait("Category", "Fixture")]` and, by repo convention (GoldenCorpusFixtureTests/DiarisationFixtureTests), THROW `FileNotFoundException` with a fetch instruction when their prerequisite binary is absent — they are excluded by `--filter "Category!=Fixture"` gates and count as "known fixture fails" otherwise.
- Commit style: `feat(core)`/`feat(app)`/`test(core)`/`test(app)`/`docs(...)`/`build(tools)`. Every commit message MUST end with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```
- Line anchors are grounded @ master 7d6c88d (which already includes the cpu-threads-quantized-weights merge: `SessionRecord.WeightsFile`, `BackendPlan(Backend, string ModelName, int? CpuThreads = null)`, `ModelFileResolver.CanonicalName`, `ITranscriptionEngine.WeightsFile`; `OfflinePipelineRunner` already flows `WeightsFile` into its finalize — nothing in this plan constructs a `BackendPlan` or bypasses that flow). This branch merges THIRD (after record-console-polish and retranscription-versions), so drift in `SessionRecord`/`SessionStore`/`SessionsPage`-adjacent anchors is still EXPECTED — every edit step quotes the current code generously; if a line number has drifted, locate by the quoted code, not the number. In particular `SessionRecord` may have gained `ActiveVersion`/`Versions` members and a schema bump from the retranscription branch: do NOT touch those, and do NOT define any transcript-version types in this branch — an imported session simply has default version state. The retranscription branch also ships `SessionController.ExternalEngineBusy` (a settable `Func<string?>` busy seam — non-null return = busy reason) and `Retranscription\RetranscriptionRunner` with `public string? RunningSessionId`: Tasks 8 and 10 consume exactly those signatures (they do NOT exist at 7d6c88d — code against them as given, and if the retranscription wiring named its App.xaml.cs instance differently, adapt the identifier at the marked call sites only).
- Evidentiary rules (design §1, locked): the original file is never modified/moved; session-root files are never rewritten destructively (the importer only writes into the session folder it is creating); deleting the PARTIAL import session folder on cancel/failure is permitted (not yet evidence); degradation (duration mismatch, multichannel downmix) is always surfaced as a transcript marker, never silent.
- DRY note: `EchoFactory`/`EnergyProbe`/`WriteBurstWav` are deliberately small per-file test helpers in Core.Tests (existing convention — `OfflinePipelineRunnerTests` keeps them `private`); new test files define their own copies rather than widening visibility.

---

### Task 1: Core model — `SessionRecord.Origin` + `ImportedSourceInfo`, `StoragePaths` source paths, import markers

**Files:**
- Modify `src\LocalScribe.Core\Model\SessionRecord.cs` (add two properties after line 38 `public DeviceSnapshot Devices { get; init; } = new();` — note the record now also carries `public string? WeightsFile { get; init; }` right after `Model`, added @ 7d6c88d: leave it untouched; add the `ImportedSourceInfo` record after the `SessionRecord` record's closing brace at line 39).
- Modify `src\LocalScribe.Core\Storage\StoragePaths.cs` (add `SourceDir`/`SourceFile` after line 24 `public string SummaryMd(string id) => Path.Combine(SessionDir(id), "summary.md");`).
- Modify `src\LocalScribe.Core\Model\Markers.cs` (add two consts before the closing brace at line 42, after line 41's `RemoteCaptureLost` — the class also gained `TranscriptionWeightsChanged` @ 7d6c88d, further up).
- Test: new `tests\LocalScribe.Core.Tests\ImportedSessionRecordTests.cs`.

**Interfaces:**
- Produces: `public string SessionRecord.Origin { get; init; } = "recorded";` and `public ImportedSourceInfo? SessionRecord.ImportedSource { get; init; }` (additive, no schema bump; `ImportedSource` omitted on disk for recorded sessions via `WhenWritingNull`).
- Produces: `public sealed record ImportedSourceInfo { string FileName; string Sha256; long FileSizeBytes; string ContainerFormat; DateTimeOffset? FileCreatedUtc; DateTimeOffset? FileModifiedUtc; DateTimeOffset? MediaCreatedUtc; long? ClaimedDurationMs; long DecodedDurationMs; int DecodedSampleRate; int DecodedChannels; string ChannelMapping; bool DurationMismatch; }` (all `{ get; init; }`).
- Produces: `public string StoragePaths.SourceDir(string id)` → `<session>\source`; `public string StoragePaths.SourceFile(string id, string originalFileName)`.
- Produces: `Markers.ImportedDurationMismatch = "imported audio duration mismatch: container claimed {0}, decoded {1}"`; `Markers.ImportedDownmixed = "imported audio downmixed to mono: source had {0} channels"`.
- Consumes: existing `SessionStore`, `LocalScribeJson` (camelCase + `WhenWritingNull`), `UtcIso8601Converter`.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\ImportedSessionRecordTests.cs`:
```csharp
using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public sealed class ImportedSessionRecordTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-import-record-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Origin_and_ImportedSource_roundtrip_through_session_json()
    {
        var paths = new StoragePaths(_root);
        Directory.CreateDirectory(paths.SessionDir("s-imp"));
        var store = new SessionStore(paths.SessionJson("s-imp"));
        var record = new SessionRecord
        {
            Id = "s-imp", App = AppKind.Manual,
            StartedAtUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
            Origin = "imported",
            ImportedSource = new ImportedSourceInfo
            {
                FileName = "hearing.mp3", Sha256 = "abc123", FileSizeBytes = 12345,
                ContainerFormat = "mp3",
                FileCreatedUtc = new DateTimeOffset(2026, 3, 5, 4, 0, 0, TimeSpan.Zero),
                FileModifiedUtc = new DateTimeOffset(2026, 3, 5, 5, 0, 0, TimeSpan.Zero),
                MediaCreatedUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
                ClaimedDurationMs = 600_000, DecodedDurationMs = 599_000,
                DecodedSampleRate = 44100, DecodedChannels = 2,
                ChannelMapping = "split", DurationMismatch = false,
            },
        };
        await store.SaveAsync(record, CancellationToken.None);

        var read = await store.ReadAsync(CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal("imported", read!.Origin);
        Assert.Equal(record.ImportedSource, read.ImportedSource);   // record value-equality

        // Wire shape: camelCase keys, so downstream tooling reads "origin"/"importedSource".
        string json = await File.ReadAllTextAsync(paths.SessionJson("s-imp"));
        Assert.Contains("\"origin\": \"imported\"", json);
        Assert.Contains("\"importedSource\"", json);
        Assert.Contains("\"sha256\": \"abc123\"", json);
    }

    [Fact]
    public async Task Recorded_sessions_default_to_origin_recorded_and_omit_importedSource()
    {
        var paths = new StoragePaths(_root);
        Directory.CreateDirectory(paths.SessionDir("s-rec"));
        var store = new SessionStore(paths.SessionJson("s-rec"));
        await store.SaveAsync(new SessionRecord
        {
            Id = "s-rec", App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        string json = await File.ReadAllTextAsync(paths.SessionJson("s-rec"));
        Assert.DoesNotContain("importedSource", json);              // WhenWritingNull omits the null

        // Back-compat: a session.json written BEFORE this field existed (no "origin" key at the
        // CURRENT schema version) must read as the "recorded" default. Strip the key from a
        // freshly-written current-version file so this stays valid across future schema bumps.
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("origin");
        await File.WriteAllTextAsync(paths.SessionJson("s-rec"), node.ToJsonString());
        var read = await store.ReadAsync(CancellationToken.None);
        Assert.Equal("recorded", read!.Origin);
        Assert.Null(read.ImportedSource);
    }

    [Fact]
    public void Source_paths_compose_under_the_session_folder()
    {
        var paths = new StoragePaths(_root);
        Assert.Equal(Path.Combine(paths.SessionDir("s-1"), "source"), paths.SourceDir("s-1"));
        Assert.Equal(Path.Combine(paths.SessionDir("s-1"), "source", "call.m4a"),
            paths.SourceFile("s-1", "call.m4a"));
    }

    [Fact]
    public void Import_marker_templates_format_cleanly()
    {
        Assert.Equal("imported audio duration mismatch: container claimed 10:00, decoded 5:00",
            string.Format(Markers.ImportedDurationMismatch, "10:00", "5:00"));
        Assert.Equal("imported audio downmixed to mono: source had 6 channels",
            string.Format(Markers.ImportedDownmixed, 6));
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~ImportedSessionRecordTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\` — expected: `error CS0117: 'SessionRecord' does not contain a definition for 'Origin'` (plus `ImportedSourceInfo`/`SourceDir`/`ImportedDurationMismatch` errors).
- [ ] **Add the SessionRecord fields.** In `src\LocalScribe.Core\Model\SessionRecord.cs`, immediately after line 38 (`public DeviceSnapshot Devices { get; init; } = new();`, the last property before the record's closing brace — NOTE: the retranscription-versions branch may have appended `ActiveVersion`/`Versions` here; if so, insert after THOSE, still inside the record) insert:
```csharp

    /// <summary>How this session came to exist (design 2026-07-13 section 4.1): "recorded" (the
    /// default - absent in every pre-existing session.json, so old files load unchanged) or
    /// "imported" (created by AudioImporter from a received file). Additive, no schema bump -
    /// the MicSnapshot.FellBackToDefault precedent.</summary>
    public string Origin { get; init; } = "recorded";

    /// <summary>Chain-of-custody metadata for an imported session's original file; null (and
    /// omitted on disk via WhenWritingNull) for recorded sessions.</summary>
    public ImportedSourceInfo? ImportedSource { get; init; }
```
- [ ] **Add the ImportedSourceInfo record.** In the same file, immediately after the `SessionRecord` record's closing brace (line 39, `}` — before the `/// <summary>Resolved device actuals...` comment) insert:
```csharp

/// <summary>Provenance of an imported session's original file (design 2026-07-13 section 4.1).
/// The original bytes live unmodified at source\{FileName}; Sha256 is computed over those bytes
/// at copy time. Claimed* fields are CONTAINER claims (ffprobe / WAV header); Decoded* fields are
/// decoded-stream truth (the verified Meetily bug class: never trust container headers).
/// DurationMismatch records that the >1 percent gate fired and the user chose Continue (the
/// transcript also carries Markers.ImportedDurationMismatch).</summary>
public sealed record ImportedSourceInfo
{
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";              // lowercase hex over the original bytes
    public long FileSizeBytes { get; init; }
    public string ContainerFormat { get; init; } = "";     // ffprobe format_name, e.g. "mp3"
    public DateTimeOffset? FileCreatedUtc { get; init; }
    public DateTimeOffset? FileModifiedUtc { get; init; }
    public DateTimeOffset? MediaCreatedUtc { get; init; }  // container media-creation tag, if any
    public long? ClaimedDurationMs { get; init; }          // null when the container states none
    public long DecodedDurationMs { get; init; }
    public int DecodedSampleRate { get; init; }
    public int DecodedChannels { get; init; }
    public string ChannelMapping { get; init; } = "";      // mono | split | split-swapped | downmix | downmix-multichannel
    public bool DurationMismatch { get; init; }
}
```
- [ ] **Add the storage paths.** In `src\LocalScribe.Core\Storage\StoragePaths.cs`, immediately after line 24 (`public string SummaryMd(string id) => Path.Combine(SessionDir(id), "summary.md");`) insert:
```csharp

    /// <summary>Imported-session provenance folder (design 2026-07-13 section 4.1): the original
    /// file is archived byte-for-byte as source\{original-filename}. Absent for recorded sessions.</summary>
    public string SourceDir(string id) => Path.Combine(SessionDir(id), "source");
    public string SourceFile(string id, string originalFileName)
        => Path.Combine(SourceDir(id), originalFileName);
```
- [ ] **Add the markers.** In `src\LocalScribe.Core\Model\Markers.cs`, immediately after the `RemoteCaptureLost` const (line 41) and before the class's closing brace (line 42) insert:
```csharp

    // Audio import (design 2026-07-13 section 4): decode-truth degradation is surfaced in the
    // transcript, never silent. {0}/{1} in ImportedDurationMismatch are h:mm:ss / m:ss durations
    // (claimed, decoded); {0} in ImportedDownmixed is the decoded channel count.
    public const string ImportedDurationMismatch =
        "imported audio duration mismatch: container claimed {0}, decoded {1}";
    public const string ImportedDownmixed =
        "imported audio downmixed to mono: source had {0} channels";
```
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed. Then run `--filter "FullyQualifiedName~SessionStoreTests|FullyQualifiedName~StoragePathsTests|FullyQualifiedName~SessionMigratorTests"` to prove no regression in the neighboring stores.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Model/SessionRecord.cs src/LocalScribe.Core/Storage/StoragePaths.cs src/LocalScribe.Core/Model/Markers.cs tests/LocalScribe.Core.Tests/ImportedSessionRecordTests.cs
git commit -m "feat(core): SessionRecord.Origin + ImportedSourceInfo provenance, source paths, import markers

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Core — `IAudioDecoder` contract, `FfmpegLocator`, `FfmpegAudioDecoder`

**Files:**
- New `src\LocalScribe.Core\Import\IAudioDecoder.cs` (interface + `AudioProbeResult` + `DecodedAudio`).
- New `src\LocalScribe.Core\Import\FfmpegLocator.cs`.
- New `src\LocalScribe.Core\Import\FfmpegAudioDecoder.cs`.
- Test: new `tests\LocalScribe.Core.Tests\FfmpegAudioDecoderTests.cs`.

**Interfaces:**
- Produces:
```csharp
namespace LocalScribe.Core.Import;
public interface IAudioDecoder
{
    Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct);
    Task<DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct);
}
public sealed record AudioProbeResult { string FormatName = ""; long FileSizeBytes; long? ClaimedDurationMs; int? ClaimedChannels; int? ClaimedSampleRate; DateTimeOffset? MediaCreatedUtc; DateTimeOffset? FileCreatedUtc; DateTimeOffset? FileModifiedUtc; }   // all { get; init; }
public sealed record DecodedAudio { string PcmWavPath = ""; int SampleRate; int Channels; long DurationMs; }   // all { get; init; }
public static class FfmpegLocator { const string MissingMessage; static string? FindToolsDir(); }
public sealed class FfmpegAudioDecoder(string? toolsDir, TimeSpan? timeout = null) : IAudioDecoder
```
- Consumes: NAudio `WaveFileReader` (WAV-native path), `System.Diagnostics.Process` (ffprobe/ffmpeg subprocess, kill-entire-tree on cancel — the `ProcessDiarisationHelper` idiom), `System.Text.Json` (ffprobe JSON).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\FfmpegAudioDecoderTests.cs`. Everything here exercises the FFmpeg-free WAV-native path (design 4.2: "WAV is read natively"), so no fixture is needed; the subprocess path is covered by Task 7's fixture test:
```csharp
using LocalScribe.Core.Import;
using NAudio.Wave;

namespace LocalScribe.Core.Tests;

public sealed class FfmpegAudioDecoderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-ffdec-" + Guid.NewGuid().ToString("N"));
    public FfmpegAudioDecoderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string WriteStereoWav(int rate, int ms)
    {
        string path = Path.Combine(_root, "in.wav");
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        int frames = rate * ms / 1000;
        var buf = new float[frames * 2];
        for (int f = 0; f < frames; f++)
            buf[f * 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / rate));   // left only
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    [Fact]
    public async Task Wav_probe_is_native_and_reports_claims_plus_file_timestamps()
    {
        string wav = WriteStereoWav(44100, 500);
        var decoder = new FfmpegAudioDecoder(toolsDir: null);       // no FFmpeg needed for WAV

        var probe = await decoder.ProbeAsync(wav, CancellationToken.None);

        Assert.Equal("wav", probe.FormatName);
        Assert.Equal(2, probe.ClaimedChannels);
        Assert.Equal(44100, probe.ClaimedSampleRate);
        Assert.InRange(probe.ClaimedDurationMs!.Value, 480, 520);
        Assert.Equal(new FileInfo(wav).Length, probe.FileSizeBytes);
        Assert.Null(probe.MediaCreatedUtc);                         // WAV carries no creation tag
        Assert.NotNull(probe.FileCreatedUtc);                       // fallback timestamps present
        Assert.NotNull(probe.FileModifiedUtc);
    }

    [Fact]
    public async Task Wav_decode_is_native_and_reads_truth_from_the_stream()
    {
        string wav = WriteStereoWav(44100, 500);
        var decoder = new FfmpegAudioDecoder(toolsDir: null);

        var decoded = await decoder.DecodeAsync(wav, _root, CancellationToken.None);

        Assert.Equal(wav, decoded.PcmWavPath);                      // read in place, never modified
        Assert.Equal(44100, decoded.SampleRate);
        Assert.Equal(2, decoded.Channels);
        Assert.InRange(decoded.DurationMs, 480, 520);
    }

    [Fact]
    public async Task NonWav_without_ffmpeg_fails_with_the_fetch_instruction()
    {
        string mp3 = Path.Combine(_root, "in.mp3");
        await File.WriteAllBytesAsync(mp3, new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
        var decoder = new FfmpegAudioDecoder(toolsDir: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => decoder.ProbeAsync(mp3, CancellationToken.None));
        Assert.Contains("fetch-ffmpeg.ps1", ex.Message);
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => decoder.DecodeAsync(mp3, _root, CancellationToken.None));
        Assert.Contains("LOCALSCRIBE_FFMPEG", ex2.Message);
    }

    [Fact]
    public async Task Missing_file_fails_fast()
    {
        var decoder = new FfmpegAudioDecoder(toolsDir: null);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => decoder.ProbeAsync(Path.Combine(_root, "absent.wav"), CancellationToken.None));
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~FfmpegAudioDecoderTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\` — expected: `error CS0246: The type or namespace name 'FfmpegAudioDecoder' could not be found` (and `LocalScribe.Core.Import`).
- [ ] **Create the contract.** New file `src\LocalScribe.Core\Import\IAudioDecoder.cs`:
```csharp
namespace LocalScribe.Core.Import;

/// <summary>Container-level CLAIMS (ffprobe / WAV header) plus the file's own timestamps, for the
/// import dialog preview, the recorded-date default, and the decoded-vs-claimed duration
/// cross-check (design 2026-07-13 section 4.1). Every Claimed* field is a claim, never truth.</summary>
public sealed record AudioProbeResult
{
    public string FormatName { get; init; } = "";          // ffprobe format_name / "wav"
    public long FileSizeBytes { get; init; }
    public long? ClaimedDurationMs { get; init; }
    public int? ClaimedChannels { get; init; }
    public int? ClaimedSampleRate { get; init; }
    public DateTimeOffset? MediaCreatedUtc { get; init; }  // container media-creation tag, if any
    public DateTimeOffset? FileCreatedUtc { get; init; }
    public DateTimeOffset? FileModifiedUtc { get; init; }
}

/// <summary>The decode result: PcmWavPath is PCM WAV at the stream's NATIVE rate/channel count
/// (for .wav inputs it is the INPUT path itself, opened read-only - never modified). SampleRate/
/// Channels/DurationMs are read from the decoder's own OUTPUT, never the source container
/// (decoded-stream truth, the verified Meetily bug class).</summary>
public sealed record DecodedAudio
{
    public string PcmWavPath { get; init; } = "";
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>Probe + decode seam so AudioImporter's unit tests run on a fake with no FFmpeg on
/// disk; FfmpegAudioDecoder is the production implementation (one fixture test drives it against
/// a real tiny MP3 - design section 4.5).</summary>
public interface IAudioDecoder
{
    Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct);
    Task<DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct);
}
```
- [ ] **Create the locator.** New file `src\LocalScribe.Core\Import\FfmpegLocator.cs`:
```csharp
namespace LocalScribe.Core.Import;

/// <summary>Resolves the ffmpeg/ffprobe tools folder the way ModelPaths resolves models: the
/// LOCALSCRIBE_FFMPEG env var, else "ffmpeg\" beside the binary (Stage 7 bundles it there, the
/// Diarizer.exe precedent), else "tools\ffmpeg\" at the repo root (dev: tools/fetch-ffmpeg.ps1's
/// output, found by walking up to LocalScribe.slnx). Null when neither exe is present - the App
/// then disables Import with MissingMessage instead of crashing (design section 4.2).</summary>
public static class FfmpegLocator
{
    public const string MissingMessage =
        "Run tools/fetch-ffmpeg.ps1 (or set LOCALSCRIBE_FFMPEG to a folder containing ffmpeg.exe and ffprobe.exe).";

    public static string? FindToolsDir()
    {
        string? env = Environment.GetEnvironmentVariable("LOCALSCRIBE_FFMPEG");
        if (!string.IsNullOrEmpty(env) && HasTools(env)) return Path.GetFullPath(env);

        string beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (HasTools(beside)) return beside;

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
            {
                string repoTools = Path.Combine(d.FullName, "tools", "ffmpeg");
                return HasTools(repoTools) ? repoTools : null;
            }
        return null;
    }

    private static bool HasTools(string dir)
        => File.Exists(Path.Combine(dir, "ffmpeg.exe")) && File.Exists(Path.Combine(dir, "ffprobe.exe"));
}
```
- [ ] **Create the decoder.** New file `src\LocalScribe.Core\Import\FfmpegAudioDecoder.cs`:
```csharp
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NAudio.Wave;
namespace LocalScribe.Core.Import;

/// <summary>Production IAudioDecoder (design 2026-07-13 section 4.2). WAV is probed/decoded
/// natively (NAudio, in place, read-only); everything else goes through ffprobe (JSON claims) and
/// an ffmpeg subprocess decoding to pcm_s16le WAV at the stream's native rate/channels - one
/// deterministic decode path across machines (MF codec availability varies by Windows SKU).
/// Subprocess handling mirrors ProcessDiarisationHelper: kill the entire process TREE on
/// cancel/timeout; stderr is captured and surfaced in the failure message for diagnostics.</summary>
public sealed class FfmpegAudioDecoder : IAudioDecoder
{
    private readonly string? _toolsDir;
    private readonly TimeSpan _timeout;

    public FfmpegAudioDecoder(string? toolsDir, TimeSpan? timeout = null)
        => (_toolsDir, _timeout) = (toolsDir, timeout ?? TimeSpan.FromMinutes(15));

    public async Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Audio file not found.", path);
        if (IsWav(path)) return ProbeWav(path, info);
        string json = await RunToolAsync("ffprobe.exe",
            $"-v error -print_format json -show_format -show_streams \"{path}\"", ct);
        return ParseProbeJson(json, info);
    }

    public async Task<DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found.", path);
        if (IsWav(path)) return DescribeWav(path);
        string outPath = Path.Combine(workDir, "decoded.wav");
        await RunToolAsync("ffmpeg.exe",
            $"-v error -nostdin -y -i \"{path}\" -vn -acodec pcm_s16le \"{outPath}\"", ct);
        return DescribeWav(outPath);
    }

    private static bool IsWav(string path)
        => Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);

    // The decoded-output description: for the ffmpeg path this reads ffmpeg's OWN output header
    // (written after it decoded every sample - decoded truth); for the wav-native path it reads
    // the input in place (the data chunk IS the stream; the channel mapper then consumes the
    // same reader path, so a lying WAV header surfaces as a decode error, not silent truncation).
    private static DecodedAudio DescribeWav(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        return new DecodedAudio
        {
            PcmWavPath = wavPath,
            SampleRate = reader.WaveFormat.SampleRate,
            Channels = reader.WaveFormat.Channels,
            DurationMs = (long)reader.TotalTime.TotalMilliseconds,
        };
    }

    private static AudioProbeResult ProbeWav(string path, FileInfo info)
    {
        using var reader = new WaveFileReader(path);
        return new AudioProbeResult
        {
            FormatName = "wav",
            FileSizeBytes = info.Length,
            ClaimedDurationMs = (long)reader.TotalTime.TotalMilliseconds,
            ClaimedChannels = reader.WaveFormat.Channels,
            ClaimedSampleRate = reader.WaveFormat.SampleRate,
            MediaCreatedUtc = null,
            FileCreatedUtc = info.CreationTimeUtc,
            FileModifiedUtc = info.LastWriteTimeUtc,
        };
    }

    private static AudioProbeResult ParseProbeJson(string json, FileInfo info)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string formatName = "";
        long? durationMs = null;
        DateTimeOffset? mediaCreated = null;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("format_name", out var fn)) formatName = fn.GetString() ?? "";
            if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
                durationMs = (long)(sec * 1000);
            if (format.TryGetProperty("tags", out var tags)
                && tags.TryGetProperty("creation_time", out var created)
                && DateTimeOffset.TryParse(created.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
                mediaCreated = when;
        }
        int? channels = null, sampleRate = null;
        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                if (!s.TryGetProperty("codec_type", out var t) || t.GetString() != "audio") continue;
                if (s.TryGetProperty("channels", out var ch)) channels = ch.GetInt32();
                if (s.TryGetProperty("sample_rate", out var sr)
                    && int.TryParse(sr.GetString(), out int srv)) sampleRate = srv;
                if (durationMs is null && s.TryGetProperty("duration", out var sd)
                    && double.TryParse(sd.GetString(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double ssec))
                    durationMs = (long)(ssec * 1000);
                break;                                             // first audio stream only
            }
        return new AudioProbeResult
        {
            FormatName = formatName, FileSizeBytes = info.Length,
            ClaimedDurationMs = durationMs, ClaimedChannels = channels,
            ClaimedSampleRate = sampleRate, MediaCreatedUtc = mediaCreated,
            FileCreatedUtc = info.CreationTimeUtc, FileModifiedUtc = info.LastWriteTimeUtc,
        };
    }

    private async Task<string> RunToolAsync(string exeName, string args, CancellationToken ct)
    {
        string? exe = _toolsDir is null ? null : Path.Combine(_toolsDir, exeName);
        if (exe is null || !File.Exists(exe))
            throw new InvalidOperationException($"FFmpeg not found ({exeName}). " + FfmpegLocator.MissingMessage);
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exeName}");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        await using var reg = timeoutCts.Token.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* may have exited between the check and the kill */ }
        });
        // Drain BOTH pipes concurrently - a full stderr pipe would deadlock the child otherwise.
        var stdout = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{exeName} timed out after {_timeout} (killed).");
        }
        string err;
        try { err = await stderr; } catch { err = ""; }
        if (proc.ExitCode != 0)
            throw new InvalidDataException(
                $"{exeName} exited with code {proc.ExitCode}: {(err.Length > 2000 ? err[^2000..] : err)}");
        return await stdout;
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed. Then build the whole Core project 0-warning: `dotnet build "src\LocalScribe.Core\LocalScribe.Core.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\`.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Import tests/LocalScribe.Core.Tests/FfmpegAudioDecoderTests.cs
git commit -m "feat(core): IAudioDecoder contract + FfmpegLocator + FfmpegAudioDecoder (WAV-native fast path)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Core — `SessionBootstrap` title parameter + `OfflinePipelineRunner.ExistingSessionId`

**Files:**
- Modify `src\LocalScribe.Core\Storage\SessionBootstrap.cs` (signature lines 13–16; meta construction line 26).
- Modify `src\LocalScribe.Core\Pipeline\OfflinePipelineRunner.cs` (`OfflineRunOptions` lines 10–16; the identity block lines 44–53).
- Tests: append one `[Fact]` to `tests\LocalScribe.Core.Tests\SessionBootstrapTests.cs`; append one `[Fact]` to `tests\LocalScribe.Core.Tests\OfflinePipelineRunnerTests.cs`.

**Interfaces:**
- Produces: `SessionBootstrap.StartAsync(..., IReadOnlyList<string>? matterIds = null, string? title = null)` — a non-empty title overrides `SessionMeta.CreateDefault`'s title AND therefore the id slug (`SessionId.New` derives from `meta.Title`).
- Produces: `public string? OfflineRunOptions.ExistingSessionId { get; init; }` — when set, `RunAsync` transcribes INTO that pre-created session (no bootstrap, no second folder) and preserves every field of the on-disk record it does not explicitly finalize (`Origin`/`ImportedSource` round-trip through `live with {...}`).
- Consumes: Task 1's `SessionRecord.Origin` (the preservation test), existing `SessionStore`, `SessionBootstrap`, test helpers `ManualUtcTimeProvider`/`FakeClock`/`StaticHardwareProbe` and the in-file `EchoFactory`/`EnergyProbe`/`WriteBurstWav` of `OfflinePipelineRunnerTests`.

Steps:
- [ ] **Write the failing bootstrap test.** Append inside `SessionBootstrapTests` (before the closing brace) in `tests\LocalScribe.Core.Tests\SessionBootstrapTests.cs` (the class already has `_root`, `Now`, and the `ManualUtcTimeProvider` import):
```csharp
    [Fact]
    public async Task StartAsync_honors_a_caller_supplied_title_in_meta_and_id_slug()
    {
        var paths = new StoragePaths(_root);
        var info = await SessionBootstrap.StartAsync(paths, new Settings(), AppKind.Manual,
            [SourceKind.Local], new DeviceSnapshot(), new ManualUtcTimeProvider(Now), "0.3.0",
            CancellationToken.None, matterIds: ["M-2026-001"], title: "Client call re: settlement");

        Assert.Equal("Client call re: settlement", info.Meta.Title);
        Assert.EndsWith("_Manual_client-call-re-settlement", info.Id);   // slug follows the title
        Assert.Equal(["M-2026-001"], info.Meta.MatterIds);
        var meta = await new MetadataStore(paths.MetaJson(info.Id)).LoadAsync(CancellationToken.None);
        Assert.Equal("Client call re: settlement", meta!.Title);
    }
```
- [ ] **Write the failing runner test.** Append inside `OfflinePipelineRunnerTests` (before the closing brace) in `tests\LocalScribe.Core.Tests\OfflinePipelineRunnerTests.cs`:
```csharp
    [Fact]
    public async Task ExistingSessionId_transcribes_into_the_precreated_folder_and_preserves_origin()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string localWav = Path.Combine(root, "in-local.wav");
            WriteBurstWav(localWav, (200, 1500));

            var paths = new StoragePaths(Path.Combine(root, "store"));
            var settings = new Settings { AudioFormat = AudioFormat.Wav, Language = "en" };
            var time = new ManualUtcTimeProvider(new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero));

            // The importer's shape: bootstrap first (custom title), stamp Origin, THEN transcribe in.
            var boot = await SessionBootstrap.StartAsync(paths, settings, AppKind.Manual,
                [SourceKind.Local], new DeviceSnapshot(), time, "0.2.0-test",
                CancellationToken.None, title: "Imported thing");
            await new SessionStore(paths.SessionJson(boot.Id)).SaveAsync(
                boot.LiveRecord with { Origin = "imported" }, CancellationToken.None);

            var runner = new OfflinePipelineRunner(paths, settings, new EchoFactory(),
                () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
                new FakeClock(), time, appVersion: "0.2.0-test");
            string id = await runner.RunAsync(new OfflineRunOptions
            { ExistingSessionId = boot.Id, LocalWavPath = localWav }, default);

            Assert.Equal(boot.Id, id);
            Assert.Single(Directory.EnumerateDirectories(paths.SessionsDir));   // no second folder
            var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
            Assert.Equal("imported", session!.Origin);        // finalize preserved the stamped field
            Assert.NotNull(session.EndedAtUtc);
            Assert.True(session.SegmentCount >= 1);
            // Weights provenance (7d6c88d): the runner's finalize writes the exact ggml file that
            // transcribed this run - the ExistingSessionId branch must not lose that flow.
            // FakeTranscriptionEngine defaults its WeightsFile to "ggml-{model}.bin".
            Assert.Equal($"ggml-{session.Model}.bin", session.WeightsFile);
            Assert.True(File.Exists(paths.TranscriptMd(id)));
            Assert.True(File.Exists(paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav)));
        }
        finally { Directory.Delete(root, true); }
    }
```
- [ ] **Run both and see them FAIL (build errors).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~StartAsync_honors_a_caller_supplied_title|FullyQualifiedName~ExistingSessionId_transcribes" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\` — expected: `error CS1739: The best overload for 'StartAsync' does not have a parameter named 'title'` and `error CS0117: 'OfflineRunOptions' does not contain a definition for 'ExistingSessionId'`.
- [ ] **Widen SessionBootstrap.** In `src\LocalScribe.Core\Storage\SessionBootstrap.cs` replace the signature (lines 13–16):
```csharp
    public static async Task<SessionStartInfo> StartAsync(StoragePaths paths, Settings settings,
        AppKind app, IReadOnlyList<SourceKind> sources, DeviceSnapshot devices,
        TimeProvider time, string appVersion, CancellationToken ct,
        IReadOnlyList<string>? matterIds = null, string? title = null)
```
and replace line 26 (`var meta = SessionMeta.CreateDefault(app, startedLocal, self) with { MatterIds = matterIds ?? [] };`) with:
```csharp
        var meta = SessionMeta.CreateDefault(app, startedLocal, self) with { MatterIds = matterIds ?? [] };
        // Audio import (design 2026-07-13 section 4.4): the dialog's editable title seeds BOTH
        // meta.Title and (via SessionId.New below) the folder-id slug. Null/blank = the default
        // "{App} - {local start}" title, exactly as before - every existing caller is unchanged.
        if (!string.IsNullOrWhiteSpace(title)) meta = meta with { Title = title };
```
- [ ] **Add ExistingSessionId to the options.** In `src\LocalScribe.Core\Pipeline\OfflinePipelineRunner.cs` replace the `OfflineRunOptions` record (lines 10–16):
```csharp
public sealed record OfflineRunOptions
{
    public string? LocalWavPath { get; init; }
    public string? RemoteWavPath { get; init; }
    public VadOptions Vad { get; init; } = new();
    public TranscriptionWorkerOptions Worker { get; init; } = new();

    /// <summary>Audio import (design 2026-07-13 section 4): transcribe INTO this pre-created
    /// session (AudioImporter bootstrapped it with the pinned recorded date, source copy, hash and
    /// Origin metadata) instead of bootstrapping a fresh one. Null = the Stage-2 behavior.</summary>
    public string? ExistingSessionId { get; init; }
}
```
- [ ] **Branch the identity step.** In `RunAsync`, replace lines 44–53 (the block starting `var sources = new List<SourceKind>();` through `var sessionStore = new SessionStore(_paths.SessionJson(id));` — quoted here in full so drift is detectable):
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
with:
```csharp
        var sources = new List<SourceKind>();
        if (options.LocalWavPath is not null) sources.Add(SourceKind.Local);
        if (options.RemoteWavPath is not null) sources.Add(SourceKind.Remote);

        string id;
        SessionRecord live;
        if (options.ExistingSessionId is { } existingId)
        {
            // Audio import (design 2026-07-13 section 4): the importer already bootstrapped this
            // folder (source copy + SHA-256 + Origin/ImportedSource stamped on the live record),
            // so load disk truth and transcribe into it. The finalize below writes `live with
            // {...}`, which preserves every field it does not name - Origin/ImportedSource survive.
            id = existingId;
            live = await new SessionStore(_paths.SessionJson(id)).ReadAsync(ct)
                ?? throw new InvalidOperationException($"session.json missing for {id}");
        }
        else
        {
            var boot = await SessionBootstrap.StartAsync(_paths, _settings, AppKind.Manual,
                sources, new DeviceSnapshot(), _time, _appVersion, ct);
            id = boot.Id;
            live = boot.LiveRecord;
        }
        var startedUtc = live.StartedAtUtc;
        var sessionStore = new SessionStore(_paths.SessionJson(id));
```
- [ ] **Run tests and see PASS.** Same filter — expected: 2 passed. Then run the neighbors to prove no regression: `--filter "FullyQualifiedName~OfflinePipelineRunnerTests|FullyQualifiedName~SessionBootstrapTests"`.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Storage/SessionBootstrap.cs src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs tests/LocalScribe.Core.Tests/SessionBootstrapTests.cs tests/LocalScribe.Core.Tests/OfflinePipelineRunnerTests.cs
git commit -m "feat(core): SessionBootstrap title override + OfflinePipelineRunner.ExistingSessionId for import

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Core — `ChannelMapper` (pure plan + streaming leg writer)

**Files:**
- New `src\LocalScribe.Core\Import\ChannelMapper.cs` (also defines `StereoMapping`, `LegPlan`, `ChannelMapPlan`).
- Test: new `tests\LocalScribe.Core.Tests\ChannelMapperTests.cs`.

**Interfaces:**
- Produces:
```csharp
namespace LocalScribe.Core.Import;
public enum StereoMapping { Downmix, Split, SplitSwapped }
public sealed record LegPlan(SourceKind Kind, int? Channel);            // null = average all channels
public sealed record ChannelMapPlan(IReadOnlyList<LegPlan> Legs, bool DownmixedMultichannel);
public static class ChannelMapper
{
    public static ChannelMapPlan Plan(int decodedChannels, StereoMapping stereo);
    public static IReadOnlyList<(SourceKind Kind, string WavPath)> WriteLegs(
        string decodedWavPath, ChannelMapPlan plan, string workDir, CancellationToken ct);
}
```
- Consumes: NAudio `AudioFileReader` (float samples, any PCM/float WAV), existing `MonoResampler16k` (stateful per-leg streaming resample), existing `WavSink` (16 kHz mono writer), `SourceKind`.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\ChannelMapperTests.cs`:
```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Import;
using NAudio.Wave;

namespace LocalScribe.Core.Tests;

public sealed class ChannelMapperTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-chanmap-" + Guid.NewGuid().ToString("N"));
    public ChannelMapperTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // Tone (300 Hz, 0.5 amplitude) only on the channels named in toneChannels; the rest silent.
    private string WriteWav(string name, int rate, int channels, int ms, params int[] toneChannels)
    {
        string path = Path.Combine(_root, name);
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        int frames = rate * ms / 1000;
        var buf = new float[frames * channels];
        for (int f = 0; f < frames; f++)
            foreach (int ch in toneChannels)
                buf[f * channels + ch] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / rate));
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    private static float PeakOf(string wavPath)
    {
        using var r = new AudioFileReader(wavPath);
        float peak = 0;
        var buf = new float[16000];
        int n;
        while ((n = r.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
        return peak;
    }

    [Fact]
    public void Plan_covers_every_channel_and_answer_combination()
    {
        var mono = ChannelMapper.Plan(1, StereoMapping.Split);       // decode truth wins over the answer
        Assert.Equal([new LegPlan(SourceKind.Local, null)], mono.Legs);
        Assert.False(mono.DownmixedMultichannel);

        var split = ChannelMapper.Plan(2, StereoMapping.Split);
        Assert.Equal([new LegPlan(SourceKind.Local, 0), new LegPlan(SourceKind.Remote, 1)], split.Legs);

        var swapped = ChannelMapper.Plan(2, StereoMapping.SplitSwapped);
        Assert.Equal([new LegPlan(SourceKind.Local, 1), new LegPlan(SourceKind.Remote, 0)], swapped.Legs);

        var downmix = ChannelMapper.Plan(2, StereoMapping.Downmix);
        Assert.Equal([new LegPlan(SourceKind.Local, null)], downmix.Legs);
        Assert.False(downmix.DownmixedMultichannel);

        var surround = ChannelMapper.Plan(6, StereoMapping.Downmix);
        Assert.Equal([new LegPlan(SourceKind.Local, null)], surround.Legs);
        Assert.True(surround.DownmixedMultichannel);                 // drives the "with a note" marker
    }

    [Fact]
    public void WriteLegs_split_keeps_each_party_on_its_own_leg()
    {
        string stereo = WriteWav("stereo.wav", 16000, 2, 500, 0);    // tone LEFT only
        var legs = ChannelMapper.WriteLegs(stereo,
            ChannelMapper.Plan(2, StereoMapping.Split), _root, CancellationToken.None);

        Assert.Equal(2, legs.Count);
        string local = legs.Single(l => l.Kind == SourceKind.Local).WavPath;
        string remote = legs.Single(l => l.Kind == SourceKind.Remote).WavPath;
        Assert.True(PeakOf(local) > 0.3f, "left (tone) must land on the Local leg");
        Assert.True(PeakOf(remote) < 0.01f, "right (silence) must land on the Remote leg");

        var swappedLegs = ChannelMapper.WriteLegs(stereo,
            ChannelMapper.Plan(2, StereoMapping.SplitSwapped),
            Directory.CreateDirectory(Path.Combine(_root, "sw")).FullName, CancellationToken.None);
        Assert.True(PeakOf(swappedLegs.Single(l => l.Kind == SourceKind.Remote).WavPath) > 0.3f);
        Assert.True(PeakOf(swappedLegs.Single(l => l.Kind == SourceKind.Local).WavPath) < 0.01f);
    }

    [Fact]
    public void WriteLegs_resamples_to_16k_and_downmixes_multichannel()
    {
        string surround = WriteWav("four.wav", 44100, 4, 1000, 0, 1, 2, 3);
        var legs = ChannelMapper.WriteLegs(surround,
            ChannelMapper.Plan(4, StereoMapping.Downmix), _root, CancellationToken.None);

        var leg = Assert.Single(legs);
        Assert.Equal(SourceKind.Local, leg.Kind);
        using var r = new WaveFileReader(leg.WavPath);
        Assert.Equal(16000, r.WaveFormat.SampleRate);                // resampled
        Assert.Equal(1, r.WaveFormat.Channels);                      // mono
        Assert.InRange(r.TotalTime.TotalMilliseconds, 900, 1100);    // ~1 s survives the resample
        Assert.True(PeakOf(leg.WavPath) > 0.3f);                     // averaged energy present
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~ChannelMapperTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\` — expected: `error CS0246: The type or namespace name 'ChannelMapper' could not be found` (and `StereoMapping`/`LegPlan`).
- [ ] **Create the mapper.** New file `src\LocalScribe.Core\Import\ChannelMapper.cs`:
```csharp
using LocalScribe.Core.Audio;
using NAudio.Wave;
namespace LocalScribe.Core.Import;

/// <summary>The import dialog's stereo answer (design 2026-07-13 section 4.3). Downmix is the
/// default and the "No/unsure" answer; Split = L is me / R is the other party; SplitSwapped =
/// the swap control. The DECODED channel count always wins: Plan(1, Split) is still one mono leg.</summary>
public enum StereoMapping { Downmix, Split, SplitSwapped }

/// <summary>One output leg: which side it becomes and which decoded channel feeds it
/// (null = the average of ALL decoded channels).</summary>
public sealed record LegPlan(SourceKind Kind, int? Channel);

/// <summary>DownmixedMultichannel flags a &gt;2-channel source that was averaged to mono - the
/// importer surfaces it as Markers.ImportedDownmixed (degradation is never silent).</summary>
public sealed record ChannelMapPlan(IReadOnlyList<LegPlan> Legs, bool DownmixedMultichannel);

/// <summary>Pure channel-mapping planner + streaming leg writer (design 2026-07-13 section 4.3):
/// decoded PCM WAV (native rate/channels) becomes one or two 16 kHz mono WAV legs, each resampled
/// with its own stateful MonoResampler16k and written through WavSink - the exact frame format
/// WavFileFrameReader and the retained-audio step already consume.</summary>
public static class ChannelMapper
{
    public static ChannelMapPlan Plan(int decodedChannels, StereoMapping stereo)
    {
        if (decodedChannels == 2 && stereo != StereoMapping.Downmix)
        {
            bool swap = stereo == StereoMapping.SplitSwapped;
            return new ChannelMapPlan(
                [new LegPlan(SourceKind.Local, swap ? 1 : 0), new LegPlan(SourceKind.Remote, swap ? 0 : 1)],
                DownmixedMultichannel: false);
        }
        return new ChannelMapPlan([new LegPlan(SourceKind.Local, null)],
            DownmixedMultichannel: decodedChannels > 2);
    }

    public static IReadOnlyList<(SourceKind Kind, string WavPath)> WriteLegs(
        string decodedWavPath, ChannelMapPlan plan, string workDir, CancellationToken ct)
    {
        using var reader = new AudioFileReader(decodedWavPath);      // float samples, interleaved
        int channels = reader.WaveFormat.Channels;
        int rate = reader.WaveFormat.SampleRate;

        var legs = new List<(SourceKind Kind, string WavPath)>();
        var sinks = new List<WavSink>();
        var resamplers = new List<MonoResampler16k?>();
        try
        {
            foreach (var leg in plan.Legs)
            {
                string path = Path.Combine(workDir,
                    leg.Kind == SourceKind.Local ? "local-16k.wav" : "remote-16k.wav");
                legs.Add((leg.Kind, path));
                sinks.Add(new WavSink(path));
                resamplers.Add(rate == 16000 ? null : new MonoResampler16k(rate));
            }

            var buf = new float[rate * channels];                    // ~1 s per read
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int frames = n / channels;
                for (int i = 0; i < plan.Legs.Count; i++)
                {
                    float[] mono = SelectChannel(buf.AsSpan(0, frames * channels), channels,
                        plan.Legs[i].Channel, frames);
                    sinks[i].Write(resamplers[i] is { } r ? r.Process(mono) : mono);
                }
            }
        }
        finally
        {
            foreach (var s in sinks) s.Dispose();                    // finalize WAV headers always
        }
        return legs;
    }

    private static float[] SelectChannel(ReadOnlySpan<float> interleaved, int channels,
        int? channel, int frames)
    {
        var mono = new float[frames];
        if (channel is int c)
        {
            for (int f = 0; f < frames; f++) mono[f] = interleaved[f * channels + c];
        }
        else
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++) sum += interleaved[f * channels + ch];
                mono[f] = sum / channels;
            }
        }
        return mono;
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 3 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Import/ChannelMapper.cs tests/LocalScribe.Core.Tests/ChannelMapperTests.cs
git commit -m "feat(core): ChannelMapper - stereo split/swap/downmix plan + streaming 16k mono leg writer

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Core — `AudioImporter` happy path (provenance, recorded-date identity, stereo mapping)

**Files:**
- New `src\LocalScribe.Core\Import\AudioImporter.cs` (also defines `ImportRequest`, `ImportStage`, `DurationMismatchInfo`).
- Test: new `tests\LocalScribe.Core.Tests\AudioImporterTests.cs` (its own `EchoFactory`/`EnergyProbe`/`FakeDecoder`/`FixedZoneTime` helpers — the per-file convention).

**Interfaces:**
- Produces:
```csharp
namespace LocalScribe.Core.Import;
public sealed record ImportRequest { required string SourcePath; required string Title; required DateTimeOffset RecordedAtLocal; IReadOnlyList<string> MatterIds = []; StereoMapping Stereo = StereoMapping.Downmix; }   // all { get; init; }
public enum ImportStage { Copy, Decode, Transcribe, Save }
public sealed record DurationMismatchInfo(long ClaimedDurationMs, long DecodedDurationMs);
public sealed class AudioImporter
{
    public AudioImporter(StoragePaths paths, Settings settings, IAudioDecoder decoder,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, Func<IClock> clockFactory, TimeProvider machineTime, string appVersion);
    public Task<string> ImportAsync(ImportRequest request, IProgress<ImportStage>? progress,
        Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct);
}
```
`ImportAsync` returns the session id; throws `OperationCanceledException` on cancel OR a declined mismatch gate; ANY failure deletes the partial session folder (import is atomic — an unfinished import is derived output, not evidence; the original file is never touched). The imported `session.json` also records `WeightsFile` — the exact ggml file that transcribed it (same evidentiary provenance as a live session): the runner's finalize writes it (@ 7d6c88d) and the importer's Save-stage `record with {...}` rewrite preserves it untouched.
- Consumes: Tasks 1–4 (`Origin`/`ImportedSource`/`SourceDir`/markers, `IAudioDecoder`, `ExistingSessionId`/title bootstrap, `ChannelMapper`), existing `SessionBootstrap`, `SessionStore`, `TranscriptStore`, `TranscriptLine.Marker`, `SessionWriter.RegenerateProjectionsAsync`, `OfflinePipelineRunner`, `IncrementalHash` (SHA-256 while copying).

Steps:
- [ ] **Write the failing tests (happy path + stereo).** Create `tests\LocalScribe.Core.Tests\AudioImporterTests.cs`:
```csharp
using System.Security.Cryptography;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using NAudio.Wave;

namespace LocalScribe.Core.Tests;

public sealed class AudioImporterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-importer-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public AudioImporterTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new StoragePaths(Path.Combine(_root, "store"));
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // --- per-file helpers (OfflinePipelineRunnerTests convention: small private copies) ---

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

    /// <summary>Deterministic machine zone (+10:00, no DST) so recorded-date identity asserts are
    /// machine-independent - AudioImporter only reads LocalTimeZone from this provider.</summary>
    private sealed class FixedZoneTime : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
            "import-test-zone", TimeSpan.FromHours(10), "import-test-zone", "import-test-zone");
    }

    private sealed class FakeDecoder : IAudioDecoder
    {
        public AudioProbeResult Probe { get; set; } = new();
        public string? DecodedWavPath { get; set; }
        public Func<CancellationToken, Task>? BeforeDecode { get; set; }
        public Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct) => Task.FromResult(Probe);
        public async Task<DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct)
        {
            if (BeforeDecode is not null) await BeforeDecode(ct);
            using var r = new WaveFileReader(DecodedWavPath!);
            return new DecodedAudio
            {
                PcmWavPath = DecodedWavPath!,
                SampleRate = r.WaveFormat.SampleRate,
                Channels = r.WaveFormat.Channels,
                DurationMs = (long)r.TotalTime.TotalMilliseconds,
            };
        }
    }

    // 200 ms silence + 1500 ms tone + 1000 ms silence (2700 ms total): EnergyProbe segments the
    // burst; the trailing silence closes it (the WriteBurstWav idiom, widened to N channels).
    private string WriteBurstWav(string name, int rate, int channels, params int[] toneChannels)
    {
        string path = Path.Combine(_root, name);
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        int silence = rate / 5, speech = rate * 3 / 2, tail = rate;
        var buf = new float[(silence + speech + tail) * channels];
        for (int f = 0; f < speech; f++)
            foreach (int ch in toneChannels)
                buf[(silence + f) * channels + ch] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / rate));
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    private AudioImporter MakeImporter(FakeDecoder decoder, Settings? settings = null)
        => new(_paths, settings ?? new Settings { Language = "en" }, decoder, new EchoFactory(),
            () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(), new FixedZoneTime(), appVersion: "0.2.0-test");

    private static ImportRequest Request(string sourcePath, string title = "Client call",
        StereoMapping stereo = StereoMapping.Downmix) => new()
    {
        SourcePath = sourcePath, Title = title,
        RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.FromHours(10)),
        MatterIds = ["M-2026-001"], Stereo = stereo,
    };

    [Fact]
    public async Task Import_creates_a_finalized_session_with_provenance_at_the_recorded_date()
    {
        // The "original" is arbitrary bytes with an .mp3 name - the fake decoder never reads it,
        // which proves the importer hashes/copies the ORIGINAL and decodes via the seam.
        string source = Path.Combine(_root, "hearing recording.mp3");
        byte[] originalBytes = new byte[4096];
        Random.Shared.NextBytes(originalBytes);
        await File.WriteAllBytesAsync(source, originalBytes);
        var originalWrite = File.GetLastWriteTimeUtc(source);

        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-mono.wav", 44100, 1, 0),
            Probe = new AudioProbeResult
            {
                FormatName = "mp3", FileSizeBytes = originalBytes.Length,
                ClaimedDurationMs = 2700, ClaimedChannels = 1, ClaimedSampleRate = 44100,
                MediaCreatedUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
            },
        };
        var stages = new List<ImportStage>();
        bool confirmCalled = false;

        string id = await MakeImporter(decoder).ImportAsync(Request(source),
            new SynchronousProgress<ImportStage>(stages.Add),
            _ => { confirmCalled = true; return Task.FromResult(true); },
            CancellationToken.None);

        // Identity: the RECORDED date (2026-03-05 14:30 +10:00) drives the id and StartedAtUtc.
        Assert.Equal("2026-03-05_1430_Manual_client-call", id);
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero), session!.StartedAtUtc);
        Assert.Equal(600, session.UtcOffsetMinutes);
        Assert.Equal(session.StartedAtUtc.AddMilliseconds(session.ImportedSource!.DecodedDurationMs),
            session.EndedAtUtc);

        // Provenance: byte-identical copy, hash over the original bytes, original untouched.
        string copy = _paths.SourceFile(id, "hearing recording.mp3");
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(copy));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(originalBytes)),
            session.ImportedSource.Sha256);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(originalWrite, File.GetLastWriteTimeUtc(source));
        Assert.Equal(originalWrite, File.GetLastWriteTimeUtc(copy));   // timestamps mirrored on the copy

        Assert.Equal("imported", session.Origin);
        Assert.Equal("hearing recording.mp3", session.ImportedSource.FileName);
        Assert.Equal("mp3", session.ImportedSource.ContainerFormat);
        Assert.Equal(2700, session.ImportedSource.ClaimedDurationMs);
        Assert.InRange(session.ImportedSource.DecodedDurationMs, 2600, 2800);
        Assert.Equal(44100, session.ImportedSource.DecodedSampleRate);
        Assert.Equal(1, session.ImportedSource.DecodedChannels);
        Assert.Equal("mono", session.ImportedSource.ChannelMapping);
        Assert.False(session.ImportedSource.DurationMismatch);
        Assert.False(confirmCalled);                                  // within 1 percent: no gate

        // A NORMAL v1-root session: transcript + FLAC leg + projections + meta.
        Assert.Equal([SourceKind.Local], session.Sources);
        Assert.Equal([SourceKind.Local], session.RetainedAudioSources);
        Assert.True(session.SegmentCount >= 1);
        // Weights provenance (7d6c88d): the runner records the exact ggml file at its finalize
        // and the Save-stage `record with {...}` preserves it - an imported session carries the
        // same WeightsFile evidence as a live one (FakeTranscriptionEngine defaults to
        // "ggml-{model}.bin").
        Assert.Equal($"ggml-{session.Model}.bin", session.WeightsFile);
        Assert.True(File.Exists(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac)));
        Assert.True(File.Exists(_paths.TranscriptMd(id)));
        var meta = await new MetadataStore(_paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal("Client call", meta!.Title);
        Assert.Equal(["M-2026-001"], meta.MatterIds);

        Assert.Equal([ImportStage.Copy, ImportStage.Decode, ImportStage.Transcribe, ImportStage.Save],
            stages);
    }

    [Fact]
    public async Task Stereo_split_maps_left_to_local_right_to_remote_and_swap_reverses()
    {
        string source = Path.Combine(_root, "call.m4a");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-stereo.wav", 16000, 2, 0),   // tone LEFT only
            Probe = new AudioProbeResult { FormatName = "m4a", ClaimedDurationMs = 2700, ClaimedChannels = 2 },
        };

        string id = await MakeImporter(decoder).ImportAsync(
            Request(source, title: "Split call", stereo: StereoMapping.Split),
            progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("split", session!.ImportedSource!.ChannelMapping);
        Assert.Equal([SourceKind.Local, SourceKind.Remote], session.Sources);
        float localPeak = FlacPcmReader.ReadMono16k(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac))
            .Max(MathF.Abs);
        float remotePeak = FlacPcmReader.ReadMono16k(_paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac))
            .Max(MathF.Abs);
        Assert.True(localPeak > 0.3f && remotePeak < 0.01f, $"local={localPeak} remote={remotePeak}");

        string id2 = await MakeImporter(decoder).ImportAsync(
            Request(source, title: "Swapped call", stereo: StereoMapping.SplitSwapped),
            progress: null, _ => Task.FromResult(true), CancellationToken.None);
        var session2 = await new SessionStore(_paths.SessionJson(id2)).ReadAsync(default);
        Assert.Equal("split-swapped", session2!.ImportedSource!.ChannelMapping);
        float remotePeak2 = FlacPcmReader.ReadMono16k(_paths.AudioFile(id2, SourceKind.Remote, AudioFormat.Flac))
            .Max(MathF.Abs);
        Assert.True(remotePeak2 > 0.3f, "swap: the left tone must land on the Remote leg");
    }

    /// <summary>IProgress that invokes inline (Progress&lt;T&gt; posts to a SynchronizationContext
    /// that unit tests do not have, making report order racy).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AudioImporterTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\` — expected: `error CS0246: The type or namespace name 'AudioImporter' could not be found` (and `ImportRequest`/`ImportStage`).
- [ ] **Create the importer.** New file `src\LocalScribe.Core\Import\AudioImporter.cs`:
```csharp
using System.Globalization;
using System.Security.Cryptography;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.Core.Import;

/// <summary>One import job (design 2026-07-13 section 4.4). RecordedAtLocal is when the call
/// HAPPENED (user-editable; defaults from the container media-creation tag, then file timestamps)
/// - it drives the session id and StartedAtUtc so list ordering is by recording time.</summary>
public sealed record ImportRequest
{
    public required string SourcePath { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset RecordedAtLocal { get; init; }
    public IReadOnlyList<string> MatterIds { get; init; } = [];
    public StereoMapping Stereo { get; init; } = StereoMapping.Downmix;
}

/// <summary>The staged-progress vocabulary (design 2026-07-13 section 4.4): reported once at the
/// START of each stage.</summary>
public enum ImportStage { Copy, Decode, Transcribe, Save }

/// <summary>Payload for the &gt;1 percent decoded-vs-claimed Continue/Cancel gate.</summary>
public sealed record DurationMismatchInfo(long ClaimedDurationMs, long DecodedDurationMs);

/// <summary>Orchestrates design 2026-07-13 section 4: copy-original+hash into source\ -> decode
/// (decoded-stream truth) -> duration-mismatch gate -> channel mapping -> transcription via the
/// existing OfflinePipelineRunner INTO the pre-created folder (which also writes the FLAC legs
/// from the mapped mono WAVs, exactly like a recorded session) -> finalize session.json
/// (Origin/ImportedSource, decoded duration) -> re-render projections. Import is ATOMIC: any
/// failure, cancellation, or a declined gate deletes the partial session folder (design section 1
/// - an unfinished import is a derived output, not evidence; the original file is never touched).
/// KNOWN behavior: a hard crash mid-import leaves an un-ended folder that the startup recovery
/// scan finalizes as a recovered (possibly empty) session - the same semantics as a crashed live
/// recording; the user deletes it like any other row.</summary>
public sealed class AudioImporter
{
    private readonly StoragePaths _paths;
    private readonly Settings _settings;
    private readonly IAudioDecoder _decoder;
    private readonly IEngineFactory _engineFactory;
    private readonly Func<ISpeechProbabilityModel> _vadModelFactory;
    private readonly IHardwareProbe _hardware;
    private readonly Func<IClock> _clockFactory;
    private readonly TimeProvider _machineTime;
    private readonly string _appVersion;

    public AudioImporter(StoragePaths paths, Settings settings, IAudioDecoder decoder,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, Func<IClock> clockFactory, TimeProvider machineTime, string appVersion)
        => (_paths, _settings, _decoder, _engineFactory, _vadModelFactory, _hardware, _clockFactory,
                _machineTime, _appVersion)
         = (paths, settings, decoder, engineFactory, vadModelFactory, hardware, clockFactory,
                machineTime, appVersion);

    public async Task<string> ImportAsync(ImportRequest request, IProgress<ImportStage>? progress,
        Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct)
    {
        string workDir = Path.Combine(Path.GetTempPath(), "localscribe-import",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        string? sessionId = null;
        try
        {
            // ---- Copy: bootstrap at the PINNED recorded date, then archive the original ----
            progress?.Report(ImportStage.Copy);
            var pinnedTime = new PinnedTimeProvider(request.RecordedAtLocal.ToUniversalTime(),
                _machineTime.LocalTimeZone);
            var original = new FileInfo(request.SourcePath);
            if (!original.Exists) throw new FileNotFoundException("Audio file not found.", request.SourcePath);
            var probe = await _decoder.ProbeAsync(request.SourcePath, ct);

            var boot = await SessionBootstrap.StartAsync(_paths, _settings, AppKind.Manual,
                [SourceKind.Local], new DeviceSnapshot(), pinnedTime, _appVersion, ct,
                request.MatterIds, request.Title);
            sessionId = boot.Id;

            Directory.CreateDirectory(_paths.SourceDir(sessionId));
            string copyPath = _paths.SourceFile(sessionId, original.Name);
            string sha256 = await CopyWithSha256Async(request.SourcePath, copyPath, ct);
            // Mirror the original's timestamps onto the archived copy (chain of custody); they are
            // ALSO recorded in session.json below, which is the evidentiary record.
            File.SetCreationTimeUtc(copyPath, original.CreationTimeUtc);
            File.SetLastWriteTimeUtc(copyPath, original.LastWriteTimeUtc);

            var imported = new ImportedSourceInfo
            {
                FileName = original.Name, Sha256 = sha256, FileSizeBytes = original.Length,
                ContainerFormat = probe.FormatName,
                FileCreatedUtc = original.CreationTimeUtc, FileModifiedUtc = original.LastWriteTimeUtc,
                MediaCreatedUtc = probe.MediaCreatedUtc, ClaimedDurationMs = probe.ClaimedDurationMs,
            };
            var sessionStore = new SessionStore(_paths.SessionJson(sessionId));
            await sessionStore.SaveAsync(
                boot.LiveRecord with { Origin = "imported", ImportedSource = imported }, ct);

            // ---- Decode: decode the ARCHIVED copy (proves the archived bytes decode) ----
            progress?.Report(ImportStage.Decode);
            var decoded = await _decoder.DecodeAsync(copyPath, workDir, ct);

            bool mismatch = false;
            if (probe.ClaimedDurationMs is long claimed && claimed > 0
                && Math.Abs(decoded.DurationMs - claimed) * 100 > claimed)   // > 1 percent
            {
                // Design 4.1: pause AFTER Decode with a Continue/Cancel gate; continuing records a
                // transcript marker; declining is a cancel (the partial folder is deleted below).
                if (!await confirmDurationMismatch(new DurationMismatchInfo(claimed, decoded.DurationMs)))
                    throw new OperationCanceledException("import declined at the duration-mismatch gate");
                mismatch = true;
            }

            var plan = ChannelMapper.Plan(decoded.Channels, request.Stereo);
            var legs = await Task.Run(
                () => ChannelMapper.WriteLegs(decoded.PcmWavPath, plan, workDir, ct), ct);

            // Markers BEFORE transcription: TranscriptMerger.InitializeAsync continues the seq
            // after existing lines, and the Save-stage recount below fixes MarkerCount.
            var transcript = new TranscriptStore(_paths.TranscriptJsonl(sessionId));
            if (mismatch)
                await transcript.AppendAsync(TranscriptLine.Marker(
                    await transcript.NextSeqAsync(ct), 0,
                    string.Format(CultureInfo.InvariantCulture, Markers.ImportedDurationMismatch,
                        FormatDuration(probe.ClaimedDurationMs!.Value), FormatDuration(decoded.DurationMs))), ct);
            if (plan.DownmixedMultichannel)
                await transcript.AppendAsync(TranscriptLine.Marker(
                    await transcript.NextSeqAsync(ct), 0,
                    string.Format(CultureInfo.InvariantCulture, Markers.ImportedDownmixed,
                        decoded.Channels)), ct);

            // ---- Transcribe (the runner also writes the retained FLAC legs from the mono WAVs) ----
            progress?.Report(ImportStage.Transcribe);
            var runner = new OfflinePipelineRunner(_paths, _settings, _engineFactory,
                _vadModelFactory, _hardware, _clockFactory(), pinnedTime, _appVersion);
            await runner.RunAsync(new OfflineRunOptions
            {
                ExistingSessionId = sessionId,
                LocalWavPath = legs.FirstOrDefault(l => l.Kind == SourceKind.Local).WavPath,
                RemoteWavPath = legs.FirstOrDefault(l => l.Kind == SourceKind.Remote).WavPath,
            }, ct);

            // ---- Save: decoded-truth duration + full recount + provenance completion ----
            // The `record with {...}` below preserves every runner-finalized field it does not
            // name - including WeightsFile (7d6c88d), the exact ggml file that transcribed this
            // import: the same evidentiary provenance a live session records.
            progress?.Report(ImportStage.Save);
            var lines = await transcript.ReadAllAsync(ct);
            var record = await sessionStore.ReadAsync(ct)
                ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
            await sessionStore.SaveAsync(record with
            {
                Sources = legs.Select(l => l.Kind).ToArray(),
                DurationMs = decoded.DurationMs,                     // decoded truth, not last-speech
                EndedAtUtc = record.StartedAtUtc.AddMilliseconds(decoded.DurationMs),
                SegmentCount = lines.Count(l => l.Kind == TranscriptKind.Segment),
                MarkerCount = lines.Count(l => l.Kind == TranscriptKind.Marker),
                ImportedSource = imported with
                {
                    DecodedDurationMs = decoded.DurationMs,
                    DecodedSampleRate = decoded.SampleRate,
                    DecodedChannels = decoded.Channels,
                    ChannelMapping = MappingLabel(decoded.Channels, plan),
                    DurationMismatch = mismatch,
                },
            }, ct);
            await new SessionWriter(_paths, _settings, _machineTime)
                .RegenerateProjectionsAsync(sessionId, ct);
            return sessionId;
        }
        catch
        {
            if (sessionId is not null)
                try { Directory.Delete(_paths.SessionDir(sessionId), recursive: true); } catch { }
            throw;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static async Task<string> CopyWithSha256Async(string sourcePath, string destPath,
        CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var dst = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buf = new byte[1 << 16];
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, n);
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static string MappingLabel(int decodedChannels, ChannelMapPlan plan) => decodedChannels switch
    {
        <= 1 => "mono",
        2 when plan.Legs.Count == 2 => plan.Legs[0].Channel == 0 ? "split" : "split-swapped",
        2 => "downmix",
        _ => "downmix-multichannel",
    };

    private static string FormatDuration(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>The recorded-date pin: GetUtcNow() is frozen at the user-chosen instant so
    /// SessionBootstrap derives the id/StartedAtUtc from when the call HAPPENED; LocalTimeZone is
    /// the real machine zone so session.json's UtcOffsetMinutes is DST-resolved for that historic
    /// date (legally meaningful) and TimeZoneId stays a real zone id.</summary>
    private sealed class PinnedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 2 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Import/AudioImporter.cs tests/LocalScribe.Core.Tests/AudioImporterTests.cs
git commit -m "feat(core): AudioImporter - copy+hash provenance, pinned recorded-date identity, stereo legs, runner reuse

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: Core — `AudioImporter` gates & cleanup (mismatch marker, decline/cancel deletes, multichannel note)

**Files:**
- Test only: append four `[Fact]`s to `tests\LocalScribe.Core.Tests\AudioImporterTests.cs`. (The Task 5 implementation already contains these paths — these tests PIN them; if any fails, fix `AudioImporter`, never weaken the test.)

**Interfaces:**
- Consumes: Task 5's `AudioImporter.ImportAsync`, Task 1's markers, `FakeDecoder.BeforeDecode` hook.
- Produces: no new surface — behavioral pins.

Steps:
- [ ] **Write the tests.** Append inside `AudioImporterTests` (before the `SynchronousProgress` helper class):
```csharp
    [Fact]
    public async Task Duration_mismatch_continue_writes_the_marker_and_flags_provenance()
    {
        string source = Path.Combine(_root, "lying.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-short.wav", 16000, 1, 0),   // ~2700 ms decoded
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 10_000 },
        };
        DurationMismatchInfo? seen = null;

        string id = await MakeImporter(decoder).ImportAsync(Request(source, title: "Mismatch"),
            progress: null,
            info => { seen = info; return Task.FromResult(true); },   // Continue
            CancellationToken.None);

        Assert.Equal(10_000, seen!.ClaimedDurationMs);
        Assert.InRange(seen.DecodedDurationMs, 2600, 2800);
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(id)).ReadAllAsync(default);
        var marker = lines.Single(l => l.Kind == TranscriptKind.Marker
            && l.Text.StartsWith("imported audio duration mismatch", StringComparison.Ordinal));
        Assert.Equal(string.Format(Markers.ImportedDurationMismatch, "0:10",
            FormatShort(seen.DecodedDurationMs)), marker.Text);
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.True(session!.ImportedSource!.DurationMismatch);
        Assert.Equal(lines.Count(l => l.Kind == TranscriptKind.Marker), session.MarkerCount);
    }

    private static string FormatShort(long ms)
        => TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Duration_mismatch_decline_deletes_the_partial_folder()
    {
        string source = Path.Combine(_root, "declined.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-decline.wav", 16000, 1, 0),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 10_000 },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MakeImporter(decoder).ImportAsync(Request(source), progress: null,
                _ => Task.FromResult(false), CancellationToken.None));   // Cancel at the gate

        Assert.True(!Directory.Exists(_paths.SessionsDir)
            || !Directory.EnumerateDirectories(_paths.SessionsDir).Any());
        Assert.True(File.Exists(source));                                // original untouched
    }

    [Fact]
    public async Task Cancel_during_decode_deletes_the_partial_folder()
    {
        string source = Path.Combine(_root, "cancelled.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        using var cts = new CancellationTokenSource();
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-cancel.wav", 16000, 1, 0),
            BeforeDecode = ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MakeImporter(decoder).ImportAsync(Request(source), progress: null,
                _ => Task.FromResult(true), cts.Token));

        Assert.True(!Directory.Exists(_paths.SessionsDir)
            || !Directory.EnumerateDirectories(_paths.SessionsDir).Any());
    }

    [Fact]
    public async Task Multichannel_downmixes_with_a_note_and_no_claim_means_no_gate()
    {
        string source = Path.Combine(_root, "surround.wma");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-4ch.wav", 16000, 4, 0, 1, 2, 3),
            Probe = new AudioProbeResult { FormatName = "wma", ClaimedDurationMs = null },   // no claim
        };
        bool confirmCalled = false;

        string id = await MakeImporter(decoder).ImportAsync(Request(source, title: "Surround"),
            progress: null,
            _ => { confirmCalled = true; return Task.FromResult(true); }, CancellationToken.None);

        Assert.False(confirmCalled);                                     // nothing to cross-check
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("downmix-multichannel", session!.ImportedSource!.ChannelMapping);
        Assert.Equal(4, session.ImportedSource.DecodedChannels);
        Assert.False(session.ImportedSource.DurationMismatch);
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(id)).ReadAllAsync(default);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text == string.Format(Markers.ImportedDownmixed, 4));
        Assert.Equal([SourceKind.Local], session.Sources);               // one downmixed leg
    }
```
- [ ] **Run and see PASS** (these pin Task 5's implementation; a failure means an `AudioImporter` bug — debug there): `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AudioImporterTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\` — expected: 6 passed. Then the full Core suite: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=...` — expected green except the pre-existing known fixture fails.
- [ ] **Commit.**
```
git add tests/LocalScribe.Core.Tests/AudioImporterTests.cs
git commit -m "test(core): pin AudioImporter mismatch gate, cancel/decline cleanup, multichannel note

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: `tools\fetch-ffmpeg.ps1` (SHA-pinned, fail-closed) + `.gitignore` + real-FFmpeg fixture test

**Files:**
- New `tools\fetch-ffmpeg.ps1`.
- Modify `.gitignore` (append after the final `models/` block).
- Test: new `tests\LocalScribe.Core.Tests\AudioImportFixtureTests.cs` (`Category=Fixture` — the design 4.5 "one fixture-gated test with a real small MP3 through the real FFmpeg").

**Interfaces:**
- Produces: `tools\ffmpeg\` containing `ffmpeg.exe`, `ffprobe.exe`, the shared-build DLLs, and `LICENSE.txt` (LGPL — kept for Stage 7's third-party notices, the pyannote-LICENSE precedent). Pin flow: `$PinnedTag`/`$PinnedAsset`/`$PinnedSha256` script constants start EMPTY; the first (unpinned) run resolves the latest BtbN LGPL win64 SHARED asset, downloads it, computes and PRINTS the three values, and fails closed WITHOUT extracting; after pasting the pin, every run verifies the zip SHA-256 and deletes + throws on mismatch (the `fetch-models.ps1` Assert-Sha256 pattern).
- Consumes: `FfmpegLocator` (Task 2) resolves `tools\ffmpeg\` via the repo walk; the fixture test consumes `FfmpegAudioDecoder` + `AudioImporter`.

Steps:
- [ ] **Write the fetch script.** Create `tools\fetch-ffmpeg.ps1`:
```powershell
# tools/fetch-ffmpeg.ps1
# Fetches the SHA-pinned FFmpeg LGPL SHARED win64 build (ffmpeg.exe + ffprobe.exe + runtime DLLs
# + LICENSE.txt) into <repo>/tools/ffmpeg (gitignored). LGPL shared ONLY - never a GPL or static
# asset (Stage 7 bundles this folder beside the app with its license text; LocalScribe invokes
# ffmpeg strictly as a separate process).
#
# PIN FLOW (fail-closed, mirrors fetch-models.ps1's Assert-Sha256): the three Pinned* constants
# below start EMPTY. The FIRST run resolves the latest BtbN/FFmpeg-Builds LGPL win64 shared zip,
# downloads it, computes its SHA-256, PRINTS the tag/asset/sha to paste into the constants, and
# EXITS WITH AN ERROR - nothing is extracted from an unpinned archive. After pasting the pin,
# every run verifies the download against the pinned SHA-256 and deletes + throws on mismatch.
$ErrorActionPreference = 'Stop'

$PinnedTag    = ''   # pinned at first fetch - the unpinned run prints the value to paste here
$PinnedAsset  = ''   # pinned at first fetch - e.g. an ffmpeg-*-win64-lgpl-shared*.zip asset name
$PinnedSha256 = ''   # pinned at first fetch - SHA-256 of that exact zip

$dest = Join-Path $PSScriptRoot 'ffmpeg'
if ((Test-Path (Join-Path $dest 'ffmpeg.exe')) -and (Test-Path (Join-Path $dest 'ffprobe.exe'))) {
    Write-Host "exists: $dest (delete the folder to force a re-fetch)"
    exit 0
}

# Verifies $Path against $ExpectedSha256 (case-insensitive). Deletes the file and throws on
# mismatch - fail closed, never let a corrupt/tampered binary pass through.
function Assert-Sha256 {
    param([string] $Path, [string] $ExpectedSha256)
    $actual = (Get-FileHash -Algorithm SHA256 $Path).Hash
    Write-Host "  sha256: $actual"
    if ($actual.ToUpperInvariant() -ne $ExpectedSha256.ToUpperInvariant()) {
        Remove-Item -Force $Path
        throw "SHA256 mismatch for $Path (expected $ExpectedSha256, got $actual) - file deleted"
    }
    Write-Host "  verified: $Path"
}

if ($PinnedTag -eq '' -or $PinnedAsset -eq '' -or $PinnedSha256 -eq '') {
    Write-Host 'no pin set - resolving the latest BtbN LGPL win64 SHARED build to compute one...'
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest'
    $asset = $release.assets |
        Where-Object { $_.name -like '*win64-lgpl-shared*.zip' -and $_.name -notlike '*gpl-shared-*gpl*' } |
        Select-Object -First 1
    if ($null -eq $asset) { throw 'no win64-lgpl-shared zip asset found on the latest BtbN release' }
    $zip = Join-Path $env:TEMP $asset.name
    Write-Host "downloading: $($asset.name) (tag $($release.tag_name))"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip
    $sha = (Get-FileHash -Algorithm SHA256 $zip).Hash
    Write-Host ''
    Write-Host 'Pin computed. Paste these three values into tools/fetch-ffmpeg.ps1 and re-run:'
    Write-Host "  `$PinnedTag    = '$($release.tag_name)'"
    Write-Host "  `$PinnedAsset  = '$($asset.name)'"
    Write-Host "  `$PinnedSha256 = '$sha'"
    throw 'unpinned run: pin the values above, then re-run (nothing was extracted)'
}

$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$PinnedTag/$PinnedAsset"
$zip = Join-Path $env:TEMP $PinnedAsset
if (-not (Test-Path $zip)) {
    Write-Host "fetching: $url"
    Invoke-WebRequest -Uri $url -OutFile $zip
}
Assert-Sha256 -Path $zip -ExpectedSha256 $PinnedSha256

$extract = Join-Path $env:TEMP ("ffmpeg-extract-" + [Guid]::NewGuid().ToString('N'))
Expand-Archive -Path $zip -DestinationPath $extract
# BtbN zip layout: <build-name>/bin/{ffmpeg.exe, ffprobe.exe, av*.dll, sw*.dll}, <build-name>/LICENSE.txt
$ffmpegExe = Get-ChildItem -Path $extract -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
if ($null -eq $ffmpegExe) { throw 'ffmpeg.exe not found inside the archive' }
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item -Path (Join-Path $ffmpegExe.DirectoryName '*') -Destination $dest -Recurse -Force
$license = Get-ChildItem -Path $extract -Recurse -Filter 'LICENSE.txt' | Select-Object -First 1
if ($null -ne $license) { Copy-Item $license.FullName -Destination $dest -Force }
Remove-Item -Recurse -Force $extract
if (-not (Test-Path (Join-Path $dest 'ffprobe.exe'))) { throw 'ffprobe.exe missing after extract' }
Write-Host "done -> $dest"
```
- [ ] **Gitignore the fetched binaries.** In `.gitignore`, immediately after the final lines:
```
# ML model files (fetched by tools/fetch-models.ps1; never committed)
models/
```
append:
```

# FFmpeg LGPL shared build (fetched by tools/fetch-ffmpeg.ps1; never committed)
tools/ffmpeg/
```
- [ ] **Run the pin flow once** (this is the implementation-time pinning step, not a placeholder): `pwsh -File tools\fetch-ffmpeg.ps1` → it prints `$PinnedTag`/`$PinnedAsset`/`$PinnedSha256` and fails closed. Paste the three printed values into the script's constants. Run `pwsh -File tools\fetch-ffmpeg.ps1` again → expected: `verified:` + `done -> ...\tools\ffmpeg`, with `ffmpeg.exe`, `ffprobe.exe`, and `LICENSE.txt` present. Run once more → expected: `exists:` (idempotent). Verify `git status` shows only `tools/fetch-ffmpeg.ps1` and `.gitignore` (no binaries).
- [ ] **Write the fixture test.** Create `tests\LocalScribe.Core.Tests\AudioImportFixtureTests.cs`:
```csharp
using System.Diagnostics;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using NAudio.Wave;

namespace LocalScribe.Core.Tests;

/// <summary>Design 2026-07-13 section 4.5's one real-FFmpeg test: a generated stereo tone is
/// encoded to a tiny real MP3 BY the fetched ffmpeg, then imported end-to-end through the real
/// FfmpegAudioDecoder (probe + subprocess decode) and AudioImporter (echo engine - no Whisper
/// model needed). Repo convention (GoldenCorpus/Diarisation): throws FileNotFoundException with
/// the fetch instruction when FFmpeg is absent; excluded by "Category!=Fixture" gates. NOTE:
/// under an isolated BaseOutputPath the repo walk cannot find tools\ffmpeg - set
/// LOCALSCRIBE_FFMPEG or run from the normal bin.</summary>
[Trait("Category", "Fixture")]
public sealed class AudioImportFixtureTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-import-fixture-" + Guid.NewGuid().ToString("N"));
    public AudioImportFixtureTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class EnergyProbe : ISpeechProbabilityModel
    {
        public float SpeechProbability(ReadOnlySpan<float> window)
            => Pipeline.SegmentAudio.RmsDb(window) > -30.0 ? 0.95f : 0.02f;
        public void Reset() { }
    }

    private sealed class EchoFactory : IEngineFactory
    {
        public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? prompt, CancellationToken ct)
            => Task.FromResult<ITranscriptionEngine>(new FakeTranscriptionEngine(plan.ModelName,
                s => new Transcription.TranscriptionResult($"[{s.Source} {s.StartMs}-{s.EndMs}]", "en", 0.01)));
    }

    [Fact]
    public async Task RealFfmpeg_imports_a_generated_stereo_mp3_end_to_end()
    {
        string? tools = FfmpegLocator.FindToolsDir();
        if (tools is null)
            throw new FileNotFoundException(
                "FFmpeg missing. Run tools/fetch-ffmpeg.ps1 (two-run pin flow), or set LOCALSCRIBE_FFMPEG.");

        // Generate the source: 200 ms silence + 1500 ms LEFT-only tone + 1000 ms silence, stereo
        // 44.1 kHz, then let the REAL ffmpeg encode the tiny MP3 (LGPL builds include libmp3lame).
        string wav = Path.Combine(_root, "tone.wav");
        using (var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)))
        {
            int silence = 8820, speech = 66150, tail = 44100;
            var buf = new float[(silence + speech + tail) * 2];
            for (int f = 0; f < speech; f++)
                buf[(silence + f) * 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / 44100.0));
            w.WriteSamples(buf, 0, buf.Length);
        }
        string mp3 = Path.Combine(_root, "phone recording.mp3");
        var encode = Process.Start(new ProcessStartInfo(Path.Combine(tools, "ffmpeg.exe"),
            $"-v error -nostdin -y -i \"{wav}\" -codec:a libmp3lame -b:a 64k \"{mp3}\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        await encode.WaitForExitAsync();
        Assert.Equal(0, encode.ExitCode);

        var paths = new StoragePaths(Path.Combine(_root, "store"));
        var importer = new AudioImporter(paths, new Settings { Language = "en" },
            new FfmpegAudioDecoder(tools), new EchoFactory(), () => new EnergyProbe(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(), TimeProvider.System, "fixture");

        // MP3 encoder padding can push claimed-vs-decoded past 1 percent on a 2.7 s file, so the
        // gate MAY fire - always Continue; do not assert DurationMismatch either way.
        string id = await importer.ImportAsync(new ImportRequest
        {
            SourcePath = mp3, Title = "Fixture call",
            RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0,
                TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 3, 5, 14, 30, 0))),
            Stereo = StereoMapping.Split,
        }, progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("imported", session!.Origin);
        Assert.Equal("phone recording.mp3", session.ImportedSource!.FileName);
        Assert.Contains("mp3", session.ImportedSource.ContainerFormat);
        Assert.Equal(44100, session.ImportedSource.DecodedSampleRate);   // decoded-stream truth
        Assert.Equal(2, session.ImportedSource.DecodedChannels);
        Assert.Equal("split", session.ImportedSource.ChannelMapping);
        Assert.InRange(session.ImportedSource.DecodedDurationMs, 2400, 3200);

        float localPeak = FlacPcmReader.ReadMono16k(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac))
            .Max(MathF.Abs);
        float remotePeak = FlacPcmReader.ReadMono16k(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac))
            .Max(MathF.Abs);
        Assert.True(localPeak > 0.2f && remotePeak < 0.05f, $"local={localPeak} remote={remotePeak}");
        Assert.True(File.Exists(paths.TranscriptMd(id)));
        Assert.True(session.SegmentCount >= 1);
    }
}
```
- [ ] **Run the fixture and see PASS** (repo bin, not the isolated path, so the repo walk finds `tools\ffmpeg` — or export the env var): `$env:LOCALSCRIBE_FFMPEG = (Resolve-Path tools\ffmpeg); dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AudioImportFixtureTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\` — expected: 1 passed. (On a box without the fetched FFmpeg this test is a known fixture fail with the fetch instruction, joining the existing 2.)
- [ ] **Commit.**
```
git add tools/fetch-ffmpeg.ps1 .gitignore tests/LocalScribe.Core.Tests/AudioImportFixtureTests.cs
git commit -m "build(tools): SHA-pinned fail-closed fetch-ffmpeg.ps1 + real-FFmpeg MP3 import fixture test

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: App — Sessions-page import surface + "Imported — MP3" Source display

**Files:**
- Modify `src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs` (ctor signature at lines 102–104; ctor body after the command constructions ~line 116; new members near the other commands).
- Modify `src\LocalScribe.App\ViewModels\SessionRowViewModel.cs` (the Source/system-mix block at lines 96–104; add `using System.IO;`).
- Tests: append one `[Fact]` to `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs`; append one `[Fact]` to `tests\LocalScribe.App.Tests\SessionRowSourceTests.cs`.

**Interfaces:**
- Produces: `SessionsPageViewModel` ctor gains two optional params `bool importAvailable = false, Func<string?>? retranscribingSessionId = null` (every existing call site keeps compiling); `public bool ImportAvailable { get; }`; `public string ImportTooltip { get; }`; `public IRelayCommand ImportAudioCommand { get; }`; `public event Action? ImportRequested;` — raised only when available AND idle: not Recording/Paused/Finalizing, no in-flight background finalize, and no re-transcription running (one engine at a time, in every direction).
- Produces: `SessionRowViewModel.Source == "Imported — MP3"` (extension of `ImportedSource.FileName`, upper-cased; falls back to `ContainerFormat`, then plain `"Imported"`) for `Origin == "imported"` rows; `SourceTooltip` carries the original file name; `IsSystemMix` stays false for imported rows.
- Consumes: Task 1's `Origin`/`ImportedSource`; `FfmpegLocator.MissingMessage` (Task 2); existing `_session` (`SessionViewModel.State`, `FinalizingSessionId`), `IUiErrorReporter.Info`; **from the retranscription-versions plan** (merged before this branch; not present @ 7d6c88d): `RetranscriptionRunner.RunningSessionId : string?` — consumed here only through the injected `retranscribingSessionId` probe, so this VM compiles and tests without that type.

Steps:
- [ ] **Write the failing row test.** Append inside `SessionRowSourceTests` (before the closing brace) in `tests\LocalScribe.App.Tests\SessionRowSourceTests.cs`:
```csharp
    [Fact]
    public void Imported_sessions_show_the_import_format_as_source()
    {
        var started = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero);
        var session = new SessionRecord
        {
            Id = "s-imp", App = AppKind.Manual, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(10), UtcOffsetMinutes = 600, DurationMs = 600_000,
            Origin = "imported",
            ImportedSource = new ImportedSourceInfo { FileName = "hearing.mp3", ContainerFormat = "mp3" },
        };
        var row = new SessionRowViewModel(
            new SessionListItem("s-imp", session, new SessionMeta { Title = "T" }), TimeProvider.System);

        Assert.Equal("Imported — MP3", row.Source);
        Assert.False(row.IsSystemMix);
        Assert.Null(row.SystemMixTooltip);                 // never a false capture-mode claim
        Assert.Contains("hearing.mp3", row.SourceTooltip); // provenance recoverable on hover
    }
```
- [ ] **Write the failing page-VM test.** Append inside `SessionsPageViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs` (uses the in-file `FakeSettings`/`NoopBin`/`RecordingErrors` and the linked `LiveTestDoubles`):
```csharp
    [Fact]
    public async Task ImportAudioCommand_raises_only_when_idle_and_available()
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var (controller, _, _, clock) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var errors = new RecordingErrors();
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(), errors,
            dispatch: a => a(), TimeProvider.System, revealInExplorer: _ => { },
            importAvailable: true);
        Assert.True(vm.ImportAvailable);
        Assert.DoesNotContain("fetch-ffmpeg", vm.ImportTooltip);
        int raised = 0;
        vm.ImportRequested += () => raised++;

        await session.StartCommand.ExecuteAsync(null);      // recording
        vm.ImportAudioCommand.Execute(null);
        Assert.Equal(0, raised);                            // refused: one engine at a time

        clock.ElapsedMs = 5000;
        await session.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;                   // background finalize settled
        vm.ImportAudioCommand.Execute(null);
        Assert.Equal(1, raised);                            // idle: the dialog opens

        var unavailable = new SessionsPageViewModel(maintenance, session, new WindowRegistry(),
            errors, dispatch: a => a(), TimeProvider.System, revealInExplorer: _ => { });
        Assert.False(unavailable.ImportAvailable);          // ctor default: FFmpeg not found
        Assert.Contains("fetch-ffmpeg.ps1", unavailable.ImportTooltip);
        int raised2 = 0;
        unavailable.ImportRequested += () => raised2++;
        unavailable.ImportAudioCommand.Execute(null);
        Assert.Equal(0, raised2);

        // One-engine-at-a-time vs re-transcription (retranscription-versions plan): the probe
        // stands in for RetranscriptionRunner.RunningSessionId - non-null must refuse the import.
        string? retransBusy = "2026-01-01_0900_Webex_x";
        var guarded = new SessionsPageViewModel(maintenance, session, new WindowRegistry(), errors,
            dispatch: a => a(), TimeProvider.System, revealInExplorer: _ => { },
            importAvailable: true, retranscribingSessionId: () => retransBusy);
        int raised3 = 0;
        guarded.ImportRequested += () => raised3++;
        guarded.ImportAudioCommand.Execute(null);
        Assert.Equal(0, raised3);                           // refused: engine busy re-transcribing
        retransBusy = null;
        guarded.ImportAudioCommand.Execute(null);
        Assert.Equal(1, raised3);                           // clear: the dialog opens
        session.Dispose();
    }
```
- [ ] **Run both and see them FAIL (build errors).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~Imported_sessions_show_the_import_format|FullyQualifiedName~ImportAudioCommand_raises_only" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\` — expected: `error CS1739` (`importAvailable`), `CS1061` (`ImportAvailable`/`ImportTooltip`/`ImportAudioCommand`/`ImportRequested`).
- [ ] **Rework the row Source block.** In `src\LocalScribe.App\ViewModels\SessionRowViewModel.cs`, add `using System.IO;` to the usings (after `using System.Globalization;`), then replace lines 96–104 (quoted in full — the block from the `// 3.2: chosen system-mix...` comment through the `SourceTooltip` assignment):
```csharp
        // 3.2: chosen system-mix has identical bleed characteristics to a fallback - both badge.
        IsSystemMix = session.Devices.Remote.Mode == RemoteMode.SystemMix
                      || session.Devices.Remote.FellBackToSystemMix;
        SystemMixTooltip = !IsSystemMix ? null
            : session.Devices.Remote.Mode == RemoteMode.SystemMix
                ? "System mix was the selected capture mode; other app audio may be included"
                : "Per-app capture fell back to system mix; other app audio may be included";
        Source = AppMedium + (IsSystemMix ? " — system mix" : " — per-app");
        SourceTooltip = SystemMixTooltip is null ? Source : Source + "\n" + SystemMixTooltip;
```
with:
```csharp
        if (session.Origin == "imported")
        {
            // Audio import (design 2026-07-13 section 4.4): an imported row's Source is its
            // provenance ("Imported - MP3"), never a capture-mode claim - no mic/loopback ever
            // ran, so the per-app/system-mix labels (and their evidentiary caveats) would be
            // false statements about how this audio was obtained.
            IsSystemMix = false;
            SystemMixTooltip = null;
            string ext = Path.GetExtension(session.ImportedSource?.FileName ?? "").TrimStart('.');
            string fmt = (ext.Length > 0 ? ext : session.ImportedSource?.ContainerFormat ?? "")
                .ToUpperInvariant();
            Source = fmt.Length == 0 ? "Imported" : $"Imported — {fmt}";
            SourceTooltip = session.ImportedSource is { FileName.Length: > 0 } src
                ? $"{Source}\nOriginal file: {src.FileName}" : Source;
        }
        else
        {
            // 3.2: chosen system-mix has identical bleed characteristics to a fallback - both badge.
            IsSystemMix = session.Devices.Remote.Mode == RemoteMode.SystemMix
                          || session.Devices.Remote.FellBackToSystemMix;
            SystemMixTooltip = !IsSystemMix ? null
                : session.Devices.Remote.Mode == RemoteMode.SystemMix
                    ? "System mix was the selected capture mode; other app audio may be included"
                    : "Per-app capture fell back to system mix; other app audio may be included";
            Source = AppMedium + (IsSystemMix ? " — system mix" : " — per-app");
            SourceTooltip = SystemMixTooltip is null ? Source : Source + "\n" + SystemMixTooltip;
        }
```
- [ ] **Widen the page VM.** MERGE RECONCILIATION (this branch lands AFTER feat/retranscription-versions): that branch has ALREADY appended `Func<string?>? retranscribingSessionId = null` as this ctor's final param, declared `private readonly Func<string?>? _retranscribingSessionId;`, and assigned it in the ctor body (for the IsRetranscribing chip). When they are already present: do NOT re-add any of the three — append only `bool importAvailable = false` as the new FINAL param (after `retranscribingSessionId`; every existing call site uses named arguments, so ordering is safe), add only the `ImportAvailable = importAvailable;` and `ImportAudioCommand = new RelayCommand(ImportAudio);` ctor-body lines, and SKIP the duplicate `_retranscribingSessionId` field in the member block below — the guard code reuses the existing field unchanged. The signatures quoted next are the 7d6c88d shapes and WILL have drifted; locate by quoted code. In `src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs`, replace the ctor signature (lines 102–104 @ 7d6c88d):
```csharp
    public SessionsPageViewModel(MaintenanceService maintenance, SessionViewModel session,
        WindowRegistry registry, IUiErrorReporter errors, Action<Action> dispatch,
        TimeProvider time, Action<string> revealInExplorer)
```
with (post-merge: keep the existing `retranscribingSessionId` param and put `importAvailable` after it):
```csharp
    public SessionsPageViewModel(MaintenanceService maintenance, SessionViewModel session,
        WindowRegistry registry, IUiErrorReporter errors, Action<Action> dispatch,
        TimeProvider time, Action<string> revealInExplorer,
        Func<string?>? retranscribingSessionId = null, bool importAvailable = false)
```
Then, immediately after the line `ExportSessionCommand = new RelayCommand<SessionRowViewModel>(RequestExport);` (line 116) insert (post-merge: the `_retranscribingSessionId` assignment already exists — add only the other two lines):
```csharp
        ImportAvailable = importAvailable;
        _retranscribingSessionId = retranscribingSessionId;
        ImportAudioCommand = new RelayCommand(ImportAudio);
```
Then, immediately after the `ExportRequested` event declaration (the `public event Action<string>? ExportRequested;` at line 88) insert:
```csharp

    /// <summary>Audio import (design 2026-07-13 section 4.4). False when FFmpeg was not found at
    /// startup (FfmpegLocator) - the Import button then stays visible but DISABLED with
    /// ImportTooltip pointing at the fetch script (the Diarizer-helper degrade pattern; nothing
    /// crashes). Fixed for the app's lifetime, like the diarizer path.</summary>
    public bool ImportAvailable { get; }

    public string ImportTooltip => ImportAvailable
        ? "Import an audio file (WAV, FLAC, MP3, M4A, WMA, OGG) as a new session"
        : "Import is unavailable - FFmpeg was not found. " + LocalScribe.Core.Import.FfmpegLocator.MissingMessage;

    public IRelayCommand ImportAudioCommand { get; }

    /// <summary>Raised from the action bar's "Import audio..." button; the window layer owns the
    /// ImportDialog (mirrors ExportRequested). Only raised when ImportAvailable and NO other
    /// engine is in flight - import loads its own Whisper engine, and the one-engine-at-a-time
    /// rule holds in every direction (the RequestExport guard's pattern): not while recording or
    /// a background finalize drains, and not while a re-transcription runs. The reverse direction
    /// (live/re-transcription refusing while an import transcribes) is the App.xaml.cs
    /// ExternalEngineBusy registration in the import wiring task.</summary>
    public event Action? ImportRequested;

    // MERGE RECONCILIATION: SKIP this field if `_retranscribingSessionId` already exists (the
    // retranscription-versions branch declares it for the IsRetranscribing chip) - the guard in
    // ImportAudio() below reuses that existing field as-is.
    /// <summary>Probe for a running re-transcription (retranscription-versions plan:
    /// `() => retranscriptionRunner.RunningSessionId`). Null func (the default) = feature absent
    /// or not wired - treated as not running, so this VM tests without that branch's types.</summary>
    private readonly Func<string?>? _retranscribingSessionId;

    private void ImportAudio()
    {
        if (!ImportAvailable) return;
        if (_session.State is SessionState.Recording or SessionState.Paused or SessionState.Finalizing
            || _session.FinalizingSessionId is not null)
        {
            _errors.Info("Cannot import while a recording is in progress. Stop the recording first.");
            return;
        }
        if (_retranscribingSessionId?.Invoke() is not null)
        {
            _errors.Info("Cannot import while a re-transcription is running. Wait for it to finish first.");
            return;
        }
        ImportRequested?.Invoke();
    }
```
- [ ] **Run tests and see PASS.** Same filter — expected: 2 passed. Then run the neighbors: `--filter "FullyQualifiedName~SessionRowSourceTests|FullyQualifiedName~SessionsPageViewModelTests"` — all green (the optional ctor param keeps every existing construction compiling).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs src/LocalScribe.App/ViewModels/SessionRowViewModel.cs tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs tests/LocalScribe.App.Tests/SessionRowSourceTests.cs
git commit -m "feat(app): Sessions-page import command surface + Imported source display

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: App — `ImportDialogViewModel` (+ `OpenPathRequest` seam)

**Files:**
- New `src\LocalScribe.App\Services\OpenPathRequest.cs`.
- New `src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs` (also defines the `ImportRunner` delegate).
- Test: new `tests\LocalScribe.App.Tests\ImportDialogViewModelTests.cs`.

**Interfaces:**
- Produces: `public sealed record OpenPathRequest(string Filter);` (Services — the `SavePathRequest` twin for `Microsoft.Win32.OpenFileDialog`).
- Produces:
```csharp
namespace LocalScribe.App.ViewModels;
public delegate Task<string> ImportRunner(ImportRequest request, IProgress<ImportStage> progress,
    Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct);
public sealed partial class ImportDialogViewModel : ObservableObject
{
    public ImportDialogViewModel(IAudioDecoder decoder, ImportRunner runImport,
        MaintenanceService maintenance, Func<OpenPathRequest, string?> pickOpenPath,
        Func<DurationMismatchInfo, Task<bool>> confirmMismatch,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time);
    public const string FileFilter;                       // WAV/FLAC/MP3/M4A/AAC/WMA/OGG + All files
    // observable: SourcePath, FileNameDisplay, DurationDisplay, SizeDisplay, FormatDisplay,
    //             Title, RecordedAtText (+ RecordedAtError), IsStereo, EachPartyOwnChannel,
    //             SwapSides, IsBusy, StageText, MatterPickerQuery; MatterOptions collection
    public bool HasFile { get; }
    public IAsyncRelayCommand PickFileCommand { get; }    // probe -> preview + defaults
    public IAsyncRelayCommand StartCommand { get; }       // gated: file + title + valid date + !busy
    public IRelayCommand CancelCommand { get; }           // busy: cancel import; idle: CloseRequested
    public IRelayCommand<MatterPickRow> ToggleMatterCommand { get; }
    public Task LoadMattersAsync();
    public event Action<string>? Completed;               // session id, after success
    public event Action? CloseRequested;
}
```
`AudioImporter.ImportAsync` (Task 5) matches `ImportRunner`'s shape — the window layer wraps it in a thin lambda that additionally registers the run on the controller's `ExternalEngineBusy` seam (Task 10); tests pass a fake.
- Consumes: `IAudioDecoder`/`AudioProbeResult` (Task 2), `ImportRequest`/`ImportStage`/`StereoMapping`/`DurationMismatchInfo` (Tasks 4–5), existing `MatterPickRow` + `MatterSearch.Matches` + `MaintenanceService.ListMattersAsync` (the Record-console picker trio), `IUiErrorReporter`, `TimeProvider.LocalTimeZone` (recorded-date parsing).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\ImportDialogViewModelTests.cs`:
```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ImportDialogViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-importdlg-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public ImportDialogViewModelTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeSettings2 : ISettingsService
    {
        public Settings Current { get; private set; } = new();
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }
    private sealed class NoopBin2 : IRecycleBin { public void SendToRecycleBin(string path) { } }
    private sealed class RecordingErrors2 : IUiErrorReporter
    {
        public List<string> Reports { get; } = [];
        public List<string> Infos { get; } = [];
        public void Report(string context, Exception ex) => Reports.Add(context + ": " + ex.Message);
        public void Info(string message) => Infos.Add(message);
    }
    private sealed class FakeDecoder : IAudioDecoder
    {
        public AudioProbeResult Probe { get; set; } = new();
        public Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct) => Task.FromResult(Probe);
        public Task<DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct)
            => throw new NotSupportedException("dialog VM never decodes");
    }
    /// <summary>Fixed +10:00 zone so date-default and parse asserts are machine-independent.</summary>
    private sealed class FixedZoneTime : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
            "dlg-test-zone", TimeSpan.FromHours(10), "dlg-test-zone", "dlg-test-zone");
    }

    private (ImportDialogViewModel Vm, FakeDecoder Decoder, RecordingErrors2 Errors)
        MakeVm(ImportRunner? runner = null, string? pickedPath = null)
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettings2(), new NoopBin2(),
            TimeProvider.System);
        var decoder = new FakeDecoder();
        var errors = new RecordingErrors2();
        var vm = new ImportDialogViewModel(decoder,
            runner ?? ((req, progress, confirm, ct) => Task.FromResult("session-1")),
            maintenance, pickOpenPath: _ => pickedPath, confirmMismatch: _ => Task.FromResult(true),
            errors, dispatch: a => a(), new FixedZoneTime());
        return (vm, decoder, errors);
    }

    [Fact]
    public async Task PickFile_probes_and_defaults_title_and_recorded_date_from_media_tag()
    {
        var (vm, decoder, _) = MakeVm(pickedPath: @"C:\evidence\hearing recording.m4a");
        decoder.Probe = new AudioProbeResult
        {
            FormatName = "mov,mp4,m4a,3gp,3g2,mj2", FileSizeBytes = 3_500_000,
            ClaimedDurationMs = 754_000, ClaimedChannels = 1, ClaimedSampleRate = 44100,
            MediaCreatedUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
        };
        Assert.False(vm.StartCommand.CanExecute(null));      // no file yet

        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.True(vm.HasFile);
        Assert.Equal("hearing recording.m4a", vm.FileNameDisplay);
        Assert.Equal("hearing recording", vm.Title);         // filename stem
        Assert.Equal("12:34", vm.DurationDisplay);
        Assert.Equal("3.3 MB", vm.SizeDisplay);
        Assert.Equal("MOV", vm.FormatDisplay);               // first format_name token
        Assert.Equal("2026-03-05 14:30", vm.RecordedAtText); // media tag -> +10:00 wall time
        Assert.False(vm.IsStereo);                           // 1 channel: no stereo question
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecordedAt_falls_back_to_earliest_file_timestamp_and_validates()
    {
        var (vm, decoder, _) = MakeVm(pickedPath: @"C:\evidence\call.mp3");
        decoder.Probe = new AudioProbeResult
        {
            FormatName = "mp3", ClaimedChannels = 2,
            FileCreatedUtc = new DateTimeOffset(2026, 3, 6, 2, 0, 0, TimeSpan.Zero),
            FileModifiedUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),   // earlier
        };
        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.Equal("2026-03-05 14:30", vm.RecordedAtText); // earliest timestamp, +10:00 wall time
        Assert.True(vm.IsStereo);                            // 2 claimed channels: ask the question
        Assert.Null(vm.RecordedAtError);

        vm.RecordedAtText = "not a date";
        Assert.NotNull(vm.RecordedAtError);
        Assert.False(vm.StartCommand.CanExecute(null));
        vm.RecordedAtText = "2026-03-05 15:00";
        Assert.Null(vm.RecordedAtError);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_builds_the_request_reports_stages_and_completes()
    {
        ImportRequest? captured = null;
        ImportRunner runner = (req, progress, confirm, ct) =>
        {
            captured = req;
            progress.Report(ImportStage.Copy);
            progress.Report(ImportStage.Decode);
            progress.Report(ImportStage.Transcribe);
            progress.Report(ImportStage.Save);
            return Task.FromResult("2026-03-05_1430_Manual_hearing");
        };
        var (vm, decoder, errors) = MakeVm(runner, pickedPath: @"C:\evidence\hearing.mp3");
        decoder.Probe = new AudioProbeResult { FormatName = "mp3", ClaimedChannels = 2 };
        await vm.PickFileCommand.ExecuteAsync(null);
        await vm.LoadMattersAsync();                          // empty catalog: no matters, no crash

        vm.Title = "  Hearing day 1  ";
        vm.RecordedAtText = "2026-03-05 14:30";
        vm.EachPartyOwnChannel = true;
        vm.SwapSides = true;
        var stages = new List<string>();
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(vm.StageText) && vm.StageText.Length > 0) stages.Add(vm.StageText); };
        string? completedId = null;
        bool closed = false;
        vm.Completed += id => completedId = id;
        vm.CloseRequested += () => closed = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\evidence\hearing.mp3", captured!.SourcePath);
        Assert.Equal("Hearing day 1", captured.Title);        // trimmed
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.FromHours(10)),
            captured.RecordedAtLocal);                        // FixedZoneTime offset applied
        Assert.Equal(StereoMapping.SplitSwapped, captured.Stereo);
        Assert.Empty(captured.MatterIds);
        Assert.Equal(4, stages.Count);                        // one text per stage
        Assert.Equal("2026-03-05_1430_Manual_hearing", completedId);
        Assert.True(closed);
        Assert.False(vm.IsBusy);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Stereo_answers_map_to_the_three_mappings()
    {
        var mappings = new List<StereoMapping>();
        ImportRunner runner = (req, p, c, ct) => { mappings.Add(req.Stereo); return Task.FromResult("s"); };
        var (vm, decoder, _) = MakeVm(runner, pickedPath: @"C:\a.mp3");
        decoder.Probe = new AudioProbeResult { FormatName = "mp3", ClaimedChannels = 2 };
        await vm.PickFileCommand.ExecuteAsync(null);
        vm.RecordedAtText = "2026-03-05 14:30";

        vm.EachPartyOwnChannel = false;                       // "No/unsure"
        await vm.StartCommand.ExecuteAsync(null);
        vm.EachPartyOwnChannel = true; vm.SwapSides = false;   // Yes, L = me
        await vm.StartCommand.ExecuteAsync(null);
        vm.EachPartyOwnChannel = true; vm.SwapSides = true;    // Yes, swapped
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal([StereoMapping.Downmix, StereoMapping.Split, StereoMapping.SplitSwapped], mappings);
    }

    [Fact]
    public async Task Cancel_during_import_cancels_the_token_and_reports_info_not_error()
    {
        var started = new TaskCompletionSource();
        ImportRunner runner = async (req, p, c, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);           // parks until cancelled
            return "never";
        };
        var (vm, decoder, errors) = MakeVm(runner, pickedPath: @"C:\a.mp3");
        decoder.Probe = new AudioProbeResult { FormatName = "mp3" };
        await vm.PickFileCommand.ExecuteAsync(null);
        vm.RecordedAtText = "2026-03-05 14:30";
        bool completed = false;
        vm.Completed += _ => completed = true;

        var run = vm.StartCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.IsBusy);
        vm.CancelCommand.Execute(null);                       // busy: cancels, does NOT close
        await run;

        Assert.False(vm.IsBusy);
        Assert.False(completed);
        Assert.Empty(errors.Reports);                         // cancellation is not an error
        Assert.Contains(errors.Infos, m => m.Contains("cancelled"));

        bool closed = false;
        vm.CloseRequested += () => closed = true;
        vm.CancelCommand.Execute(null);                       // idle: requests close
        Assert.True(closed);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ImportDialogViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\` — expected: `error CS0246: The type or namespace name 'ImportDialogViewModel' could not be found` (and `OpenPathRequest`/`ImportRunner`).
- [ ] **Create the open-path seam.** New file `src\LocalScribe.App\Services\OpenPathRequest.cs`:
```csharp
namespace LocalScribe.App.Services;

/// <summary>An Open-file request for the pickOpenPath composition-root seam (design 2026-07-13
/// section 4.4) - the SavePathRequest twin: the VM supplies the dialog filter; the App-side
/// lambda wraps Microsoft.Win32.OpenFileDialog.</summary>
public sealed record OpenPathRequest(string Filter);
```
- [ ] **Create the dialog VM.** New file `src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Import;
namespace LocalScribe.App.ViewModels;

/// <summary>The AudioImporter.ImportAsync seam: the window layer passes the real importer's
/// method group; tests pass a fake so the VM is exercised with no FFmpeg/engine on disk.</summary>
public delegate Task<string> ImportRunner(ImportRequest request, IProgress<ImportStage> progress,
    Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct);

/// <summary>WPF-free VM behind the plain-Window import dialog (design 2026-07-13 section 4.4):
/// file pick -> probe preview (claims only), editable title (filename stem) and recorded-date
/// (media-creation tag, else the EARLIEST file timestamp; legally meaningful, so user-correctable
/// - it drives the session id/StartedAtUtc), optional matter tagging (the Record-console picker
/// trio), the stereo question when the container claims 2 channels, staged progress with Cancel.
/// The duration-mismatch gate is the injected confirmMismatch seam, passed through to the runner.</summary>
public sealed partial class ImportDialogViewModel : ObservableObject
{
    public const string FileFilter =
        "Audio files (*.wav;*.flac;*.mp3;*.m4a;*.aac;*.wma;*.ogg)|*.wav;*.flac;*.mp3;*.m4a;*.aac;*.wma;*.ogg|All files (*.*)|*.*";
    private const string DateFormat = "yyyy-MM-dd HH:mm";

    private readonly IAudioDecoder _decoder;
    private readonly ImportRunner _runImport;
    private readonly MaintenanceService _maintenance;
    private readonly Func<OpenPathRequest, string?> _pickOpenPath;
    private readonly Func<DurationMismatchInfo, Task<bool>> _confirmMismatch;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly List<LocalScribe.Core.Model.MattersIndexEntry> _allMatters = new();
    private readonly HashSet<string> _pickedMatterIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;

    public ImportDialogViewModel(IAudioDecoder decoder, ImportRunner runImport,
        MaintenanceService maintenance, Func<OpenPathRequest, string?> pickOpenPath,
        Func<DurationMismatchInfo, Task<bool>> confirmMismatch,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time)
    {
        (_decoder, _runImport, _maintenance, _pickOpenPath, _confirmMismatch, _errors, _dispatch, _time)
            = (decoder, runImport, maintenance, pickOpenPath, confirmMismatch, errors, dispatch, time);
        PickFileCommand = new AsyncRelayCommand(PickFileAsync, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        CancelCommand = new RelayCommand(Cancel);
        ToggleMatterCommand = new RelayCommand<MatterPickRow>(ToggleMatter);
    }

    // --- file + probe preview (claims only - decode truth is the importer's job) ---
    [ObservableProperty] private string? _sourcePath;
    [ObservableProperty] private string _fileNameDisplay = "";
    [ObservableProperty] private string _durationDisplay = "";
    [ObservableProperty] private string _sizeDisplay = "";
    [ObservableProperty] private string _formatDisplay = "";
    public bool HasFile => SourcePath is not null;

    // --- editable fields ---
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _recordedAtText = "";
    /// <summary>Null when RecordedAtText parses (or is still empty); else the inline hint.</summary>
    public string? RecordedAtError =>
        RecordedAtText.Trim().Length == 0 || ParseRecordedAt() is not null
            ? null : "Enter the date as " + DateFormat;

    // --- the stereo question (design 4.3), shown only when the container claims 2 channels ---
    [ObservableProperty] private bool _isStereo;
    [ObservableProperty] private bool _eachPartyOwnChannel;
    [ObservableProperty] private bool _swapSides;

    // --- matter picker (the Record-console trio: MatterPickRow / MatterSearch / toggle) ---
    public ObservableCollection<MatterPickRow> MatterOptions { get; } = new();
    [ObservableProperty] private string _matterPickerQuery = "";
    public IRelayCommand<MatterPickRow> ToggleMatterCommand { get; }

    // --- staged progress ---
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _stageText = "";

    public IAsyncRelayCommand PickFileCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public event Action<string>? Completed;
    public event Action? CloseRequested;

    partial void OnTitleChanged(string value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnRecordedAtTextChanged(string value)
    {
        OnPropertyChanged(nameof(RecordedAtError));
        StartCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value)
    {
        PickFileCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }
    partial void OnMatterPickerQueryChanged(string value) => RebuildMatterOptions();

    private bool CanStart() => HasFile && !IsBusy && Title.Trim().Length > 0
        && ParseRecordedAt() is not null;

    private DateTimeOffset? ParseRecordedAt()
    {
        if (!DateTime.TryParseExact(RecordedAtText.Trim(), DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local)) return null;
        // The machine zone's DST-resolved offset AT that historic date - legally meaningful.
        return new DateTimeOffset(local, _time.LocalTimeZone.GetUtcOffset(local));
    }

    private async Task PickFileAsync()
    {
        string? path = _pickOpenPath(new OpenPathRequest(FileFilter));
        if (path is null) return;
        try
        {
            var probe = await _decoder.ProbeAsync(path, CancellationToken.None);
            _dispatch(() => Apply(path, probe));
        }
        catch (Exception ex) { _errors.Report("Reading audio file", ex); }
    }

    private void Apply(string path, AudioProbeResult probe)
    {
        SourcePath = path;
        FileNameDisplay = Path.GetFileName(path);
        Title = Path.GetFileNameWithoutExtension(path);
        DurationDisplay = probe.ClaimedDurationMs is long ms ? FormatDuration(ms) : "unknown";
        SizeDisplay = FormatSize(probe.FileSizeBytes);
        FormatDisplay = probe.FormatName.Split(',')[0].ToUpperInvariant();
        IsStereo = probe.ClaimedChannels == 2;
        EachPartyOwnChannel = false;
        SwapSides = false;
        // Recorded-date default (design 4.4): the container's media-creation tag, else the
        // EARLIEST of the file's own timestamps (a copy resets CreationTime; the earlier stamp is
        // the better guess at when the recording happened). Blank when nothing is known.
        DateTimeOffset? recorded = probe.MediaCreatedUtc ?? Earliest(probe.FileCreatedUtc, probe.FileModifiedUtc);
        RecordedAtText = recorded is { } r
            ? TimeZoneInfo.ConvertTime(r, _time.LocalTimeZone).ToString(DateFormat, CultureInfo.InvariantCulture)
            : "";
        OnPropertyChanged(nameof(HasFile));
        StartCommand.NotifyCanExecuteChanged();
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : (a < b ? a : b);

    private async Task StartAsync()
    {
        if (SourcePath is not { } source || ParseRecordedAt() is not { } recordedAt) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var request = new ImportRequest
            {
                SourcePath = source,
                Title = Title.Trim(),
                RecordedAtLocal = recordedAt,
                MatterIds = _pickedMatterIds.ToList(),
                Stereo = !IsStereo || !EachPartyOwnChannel ? StereoMapping.Downmix
                    : SwapSides ? StereoMapping.SplitSwapped : StereoMapping.Split,
            };
            string id = await _runImport(request, new DispatchProgress(this),
                _confirmMismatch, _cts.Token);
            _errors.Info($"Imported \"{request.Title}\".");
            _dispatch(() => { Completed?.Invoke(id); CloseRequested?.Invoke(); });
        }
        catch (OperationCanceledException)
        {
            _errors.Info("Import cancelled - the partial session was discarded; the original file is untouched.");
        }
        catch (Exception ex) { _errors.Report("Import audio", ex); }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            StageText = "";
        }
    }

    /// <summary>Busy: cancel the running import (the importer deletes the partial folder).
    /// Idle: ask the window to close. One button, two safe meanings.</summary>
    private void Cancel()
    {
        if (IsBusy) _cts?.Cancel();
        else CloseRequested?.Invoke();
    }

    /// <summary>Marshals stage reports through _dispatch explicitly (Progress&lt;T&gt; captures a
    /// SynchronizationContext the unit tests do not have).</summary>
    private sealed class DispatchProgress(ImportDialogViewModel owner) : IProgress<ImportStage>
    {
        public void Report(ImportStage value) => owner._dispatch(() => owner.StageText = value switch
        {
            ImportStage.Copy => "Copying original file...",
            ImportStage.Decode => "Decoding audio...",
            ImportStage.Transcribe => "Transcribing...",
            _ => "Saving session...",
        });
    }

    // --- matter picker (mirrors RecordingConsoleViewModel.LoadMattersAsync/Rebuild/Toggle) ---

    /// <summary>Best-effort catalog load (the picker is optional - tag later in Session Details);
    /// a failed read leaves the list empty rather than blocking the import.</summary>
    public async Task LoadMattersAsync()
    {
        try
        {
            var index = await _maintenance.ListMattersAsync(CancellationToken.None);
            _dispatch(() =>
            {
                _allMatters.Clear();
                _allMatters.AddRange(index.Matters.Where(m => !m.Archived));
                RebuildMatterOptions();
            });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LoadMattersAsync failed: {ex}"); }
    }

    private void RebuildMatterOptions()
    {
        string q = MatterPickerQuery.Trim();
        MatterOptions.Clear();
        foreach (var e in _allMatters)
            if (q.Length == 0 || MatterSearch.Matches(e, q))
                MatterOptions.Add(new MatterPickRow(e.Id,
                    string.IsNullOrEmpty(e.Reference) ? e.Name : $"{e.Name} ({e.Reference})",
                    _pickedMatterIds.Contains(e.Id)));
    }

    private void ToggleMatter(MatterPickRow? row)
    {
        if (row is null) return;
        if (!_pickedMatterIds.Remove(row.Id)) _pickedMatterIds.Add(row.Id);
        RebuildMatterOptions();
    }

    private static string FormatDuration(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (double)(1 << 30):0.#} GB",
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.#} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):0.#} KB",
        _ => $"{bytes} B",
    };
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 5 passed. (Note the `DurationDisplay` assert expects `"12:34"` for 754 000 ms via the `mm\:ss` branch, and `SizeDisplay` `"3.3 MB"` for 3 500 000 bytes — if either literal is off by a formatting nuance, fix the ASSERT to the actual correct rendering, never bend the formatter to a wrong expectation.)
- [ ] **Commit.**
```
git add src/LocalScribe.App/Services/OpenPathRequest.cs src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs tests/LocalScribe.App.Tests/ImportDialogViewModelTests.cs
git commit -m "feat(app): ImportDialogViewModel - probe preview, recorded-date default+validation, stereo question, staged progress

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: App — `ImportDialog.xaml`, Sessions-page button, `App.xaml.cs` wiring, full gate + manual smoke

**Files:**
- New `src\LocalScribe.App\ImportDialog.xaml` + `ImportDialog.xaml.cs` (plain Window — the ExportDialog pattern; NOT a FluentWindow, per the startup-rendering memory any modal shown off the main pump stays a plain Window).
- Modify `src\LocalScribe.App\Pages\SessionsPage.xaml` (add the button to the top WrapPanel after the Refresh button, lines 39–40).
- Modify `src\LocalScribe.App\App.xaml.cs` (pickOpenPath after pickSavePath ~line 120; `importAvailable` + ctor arg at the sessionsVm construction lines 136–145; confirmMismatch + openImport wiring after `sessionsVm.OpenReadViewRequested += openReadView;` at line 273).
- Modify `tests\LocalScribe.App.Tests\XamlHygieneTests.cs` (add `"ImportDialog.xaml"` to the roots array, lines 52–64).
- No new unit test (App.xaml.cs composition + XAML are not unit-tested). The "test" is: XamlHygiene + 0-warning build + full App/Core suites + the manual smoke below.

**Interfaces:**
- Consumes: `ImportDialogViewModel`/`ImportRunner`/`OpenPathRequest` (Task 9), `SessionsPageViewModel.ImportRequested`/`ImportAvailable`/`ImportTooltip`/`ImportAudioCommand`/`retranscribingSessionId:` (Task 8), `AudioImporter`/`FfmpegAudioDecoder`/`FfmpegLocator`/`DurationMismatchInfo` (Tasks 2, 5), existing `openReadView`, `sessionsVm.UpsertRowAsync`, `dispatch`, `errors`, `comp.*`, `MessageBox` (the confirmSystemMix house idiom); **from the retranscription-versions plan** (merged before this branch; not present @ 7d6c88d): `SessionController.ExternalEngineBusy` — a settable `Func<string?>` busy seam (non-null return = busy reason; the live engine and the re-transcription runner both refuse to start while it returns non-null) — and the `RetranscriptionRunner` instance that branch constructs in App.xaml.cs, with `public string? RunningSessionId`. If that wiring named its instance differently, adapt only the two marked call sites.
- Produces: no new public types beyond the window.

Steps:
- [ ] **Create the dialog window.** New file `src\LocalScribe.App\ImportDialog.xaml`:
```xml
<Window x:Class="LocalScribe.App.ImportDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        Title="Import audio" Width="480" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize">
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
    </Window.Resources>
    <StackPanel Margin="16" TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}">
        <StackPanel Orientation="Horizontal">
            <ui:Button Content="Choose file..." Command="{Binding PickFileCommand}"
                       MinWidth="110" Margin="0,0,8,0" />
            <TextBlock Text="{Binding FileNameDisplay}" VerticalAlignment="Center" MaxWidth="300"
                       TextTrimming="CharacterEllipsis" ToolTip="{Binding SourcePath}" />
        </StackPanel>

        <StackPanel Visibility="{Binding HasFile, Converter={StaticResource BoolToVis}}">
            <!-- Probe preview: container CLAIMS only; decode truth is checked during import. -->
            <TextBlock Margin="0,8,0,0" Style="{StaticResource MutedText}">
                <Run Text="Duration " /><Run Text="{Binding DurationDisplay, Mode=OneWay}" />
                <Run Text="   Size " /><Run Text="{Binding SizeDisplay, Mode=OneWay}" />
                <Run Text="   Format " /><Run Text="{Binding FormatDisplay, Mode=OneWay}" />
            </TextBlock>

            <TextBlock Text="Title" FontWeight="SemiBold" Margin="0,12,0,4" />
            <ui:TextBox Text="{Binding Title, UpdateSourceTrigger=PropertyChanged}" />

            <TextBlock Text="Recorded (when the call happened)" FontWeight="SemiBold" Margin="0,12,0,4" />
            <ui:TextBox Text="{Binding RecordedAtText, UpdateSourceTrigger=PropertyChanged}" />
            <TextBlock Text="{Binding RecordedAtError}" Foreground="{DynamicResource SystemFillColorCriticalBrush}">
                <TextBlock.Style>
                    <Style TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding RecordedAtError}" Value="{x:Null}">
                                <Setter Property="Visibility" Value="Collapsed" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
            <TextBlock Style="{StaticResource MutedText}" TextWrapping="Wrap"
                       Text="Sets the session's date and list position (yyyy-MM-dd HH:mm). Defaults from the file's own metadata - correct it if that is wrong." />

            <!-- The stereo question (design 4.3), only when the container claims 2 channels. -->
            <StackPanel Visibility="{Binding IsStereo, Converter={StaticResource BoolToVis}}" Margin="0,12,0,0">
                <CheckBox Content="Each party is on its own channel (left = me, right = the other party)"
                          IsChecked="{Binding EachPartyOwnChannel}" />
                <CheckBox Content="Swap sides (left = the other party)" Margin="20,4,0,0"
                          IsChecked="{Binding SwapSides}" IsEnabled="{Binding EachPartyOwnChannel}" />
                <TextBlock Style="{StaticResource MutedText}" TextWrapping="Wrap" Margin="0,4,0,0"
                           Text="Unticked: both channels are mixed to one track and Split speakers can be used later." />
            </StackPanel>

            <TextBlock Text="Matters (optional)" FontWeight="SemiBold" Margin="0,12,0,4" />
            <ui:TextBox PlaceholderText="Search matters"
                        Text="{Binding MatterPickerQuery, UpdateSourceTrigger=PropertyChanged}" />
            <Border BorderBrush="{DynamicResource ControlStrokeColorDefaultBrush}" BorderThickness="1"
                    CornerRadius="4" MaxHeight="120" Margin="0,4,0,0">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <ItemsControl ItemsSource="{Binding MatterOptions}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <CheckBox Content="{Binding Display}" IsChecked="{Binding IsSelected, Mode=OneWay}"
                                          Margin="4,2"
                                          Command="{Binding DataContext.ToggleMatterCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                          CommandParameter="{Binding}" />
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </ScrollViewer>
            </Border>
        </StackPanel>

        <StackPanel Visibility="{Binding IsBusy, Converter={StaticResource BoolToVis}}" Margin="0,12,0,0">
            <TextBlock Text="{Binding StageText}" />
            <ProgressBar IsIndeterminate="True" Height="4" Margin="0,4,0,0" />
        </StackPanel>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <ui:Button Content="Import" Appearance="Primary" IsDefault="True" MinWidth="90"
                       Margin="0,0,8,0" Command="{Binding StartCommand}" />
            <ui:Button Content="Cancel" MinWidth="90" Command="{Binding CancelCommand}" />
        </StackPanel>
    </StackPanel>
</Window>
```
And `src\LocalScribe.App\ImportDialog.xaml.cs`:
```csharp
using System.Windows;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App;

public partial class ImportDialog : Window
{
    public ImportDialog(ImportDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += Close;
        // X / Alt+F4 while an import runs: first press CANCELS the import (the importer deletes
        // the partial folder); the dialog stays open until the cancellation unwinds, then a
        // second press (or the Cancel button, now idle) closes it. Never orphan a running import.
        Closing += (_, e) =>
        {
            if (vm.IsBusy) { vm.CancelCommand.Execute(null); e.Cancel = true; }
        };
    }
}
```
- [ ] **Register the dialog in the hygiene roots.** In `tests\LocalScribe.App.Tests\XamlHygieneTests.cs`, in the `roots` array (lines 52–64), after the entry `"ConsentDialog.xaml",` add:
```csharp
            "ImportDialog.xaml",
```
- [ ] **Add the Sessions-page button.** In `src\LocalScribe.App\Pages\SessionsPage.xaml`, immediately after the Refresh button (lines 39–40):
```xml
            <ui:Button Content="Refresh" Appearance="Secondary" Margin="0,0,8,8"
                       Command="{Binding RefreshCommand}" />
```
insert:
```xml
            <!-- Audio import (design 2026-07-13 section 4.4). Disabled-with-tooltip when FFmpeg is
                 absent (the Diarizer-helper degrade pattern); ShowOnDisabled so the fetch-script
                 pointer is discoverable exactly when it is needed. -->
            <ui:Button Content="Import audio..." Appearance="Secondary" Margin="0,0,8,8"
                       IsEnabled="{Binding ImportAvailable}"
                       ToolTip="{Binding ImportTooltip}"
                       ToolTipService.ShowOnDisabled="True"
                       Command="{Binding ImportAudioCommand}" />
```
- [ ] **Add the open-file seam.** In `src\LocalScribe.App\App.xaml.cs`, immediately after the `pickSavePath` lambda's closing `};` (line 120, before the `// Reveal-and-highlight...` comment) insert:
```csharp
        // Open-file seam for the audio-import dialog (design 2026-07-13 section 4.4): the
        // SavePathRequest twin. No last-dir memory - received recordings come from anywhere.
        Func<Services.OpenPathRequest, string?> pickOpenPath = req =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = req.Filter, CheckFileExists = true };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        };
```
- [ ] **Thread import availability into the sessions VM.** In `App.xaml.cs`, replace the `sessionsVm` construction (lines 136–145):
```csharp
        var sessionsVm = new ViewModels.SessionsPageViewModel(comp.Maintenance, session,
            comp.Windows, errors, dispatch, TimeProvider.System,
            revealInExplorer: id =>
            {
                // Same shell-out TrayIconHost's "Open sessions folder" uses; the path is
                // built via StoragePaths (spec 3.2), never assembled by the VM.
                string dir = comp.Paths.SessionDir(id);
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", dir);
            });
```
with:
```csharp
        // Audio import availability is resolved ONCE at startup (the diarizer-exe precedent):
        // FfmpegLocator checks LOCALSCRIBE_FFMPEG, then ffmpeg\ beside the app, then the repo's
        // tools\ffmpeg. Absent -> the Import button is disabled with the fetch-script tooltip.
        string? ffmpegDir = LocalScribe.Core.Import.FfmpegLocator.FindToolsDir();
        var sessionsVm = new ViewModels.SessionsPageViewModel(comp.Maintenance, session,
            comp.Windows, errors, dispatch, TimeProvider.System,
            revealInExplorer: id =>
            {
                // Same shell-out TrayIconHost's "Open sessions folder" uses; the path is
                // built via StoragePaths (spec 3.2), never assembled by the VM.
                string dir = comp.Paths.SessionDir(id);
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", dir);
            },
            importAvailable: ffmpegDir is not null,
            // MARKED CALL SITE (retranscription-versions plan): the guard probe over the
            // RetranscriptionRunner instance that branch's wiring constructs. If it is declared
            // BELOW this point in OnStartup, hoist its declaration above sessionsVm (the
            // openSplitSpeakers hoisting precedent - a lambda cannot reference a later local);
            // adapt the identifier if that wiring named it differently.
            retranscribingSessionId: () => retranscriptionRunner.RunningSessionId);
```
- [ ] **Wire the import dialog.** In `App.xaml.cs`, immediately after line 273 (`sessionsVm.OpenReadViewRequested += openReadView;`) insert:
```csharp

        // Audio import (design 2026-07-13 section 4): fresh decoder/importer/VM per request (the
        // openExport run-then-close pattern). The importer snapshots CURRENT settings at open,
        // like SessionViewModel snapshots at Start. The duration-mismatch gate is a modal OKCancel
        // (the confirmSystemMix house idiom) marshalled onto the UI thread - the importer awaits
        // the answer off-thread. Completion upserts the new row in place and opens the read view.
        Func<LocalScribe.Core.Import.DurationMismatchInfo, Task<bool>> confirmMismatch = info =>
        {
            var tcs = new TaskCompletionSource<bool>();
            dispatch(() =>
            {
                static string Fmt(long ms)
                {
                    var span = TimeSpan.FromMilliseconds(ms);
                    return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
                }
                tcs.SetResult(MessageBox.Show(
                    $"This file's container claims a duration of {Fmt(info.ClaimedDurationMs)}, but the decoded " +
                    $"audio is {Fmt(info.DecodedDurationMs)}. The container metadata is unreliable; the decoded " +
                    "audio is used either way. Continue and record a marker in the transcript, or cancel the import?",
                    "Imported duration mismatch", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                    == MessageBoxResult.OK);
            });
            return tcs.Task;
        };
        // One-engine-at-a-time, the REVERSE direction (retranscription-versions plan): while an
        // import transcribes, the live engine AND the re-transcription runner must refuse to
        // start. Both consult SessionController.ExternalEngineBusy (Func<string?>; non-null =
        // busy reason), which the retranscription wiring already set for its own runs - so CHAIN
        // over the prior delegate, never clobber it. `importBusy` is set/cleared by the runner
        // wrapper inside openImport below.
        string? importBusy = null;
        var priorEngineBusy = comp.Controller.ExternalEngineBusy;   // MARKED CALL SITE (seam name)
        comp.Controller.ExternalEngineBusy = () => importBusy ?? priorEngineBusy?.Invoke();
        Action openImport = () =>
        {
            var decoder = new LocalScribe.Core.Import.FfmpegAudioDecoder(
                LocalScribe.Core.Import.FfmpegLocator.FindToolsDir());
            var importer = new LocalScribe.Core.Import.AudioImporter(comp.Paths, comp.Settings.Current,
                decoder, new LocalScribe.Core.Transcription.WhisperEngineFactory(),
                () => new LocalScribe.Core.Vad.SileroVadModel(
                    LocalScribe.Core.Transcription.ModelPaths.Require("silero_vad.onnx")),
                new LocalScribe.Core.Transcription.LiveHardwareProbe(),
                () => new LocalScribe.Core.Audio.StopwatchClock(), TimeProvider.System, comp.AppVersion);
            // Register the whole import run on the busy seam (chained above): Start/Re-transcribe
            // read "audio import" as the refusal reason for exactly as long as ImportAsync runs.
            ViewModels.ImportRunner runImport = async (req, progress, confirm, ct) =>
            {
                importBusy = "audio import";
                try { return await importer.ImportAsync(req, progress, confirm, ct); }
                finally { importBusy = null; }
            };
            var importVm = new ViewModels.ImportDialogViewModel(decoder, runImport,
                comp.Maintenance, pickOpenPath, confirmMismatch, errors, dispatch, TimeProvider.System);
            importVm.Completed += id =>
            {
                _ = sessionsVm.UpsertRowAsync(id);            // in-place row, no scroll jump
                openReadView(id);                             // completion opens the session
            };
            _ = importVm.LoadMattersAsync();                  // best-effort; picker is optional
            new ImportDialog(importVm) { Owner = MainWindow }.ShowDialog();
        };
        sessionsVm.ImportRequested += openImport;
```
- [ ] **Build 0-warning + full App/Core suites green.** Run:
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\audio-import\
```
Expected: build 0 warnings; both suites green — Core's known fixture fails excepted, and if `XamlHygieneTests` alone fails under the isolated path (repo-walk cannot find `.git` from %TEMP%), re-run the App suite without `-p:BaseOutputPath` once the app is closed (see Global Constraints). The new ImportDialog.xaml must pass hygiene: no hardcoded ARGB, no keyless `<Style TargetType="TextBlock">` (the error style is `BasedOn`-keyed inline, which the shared-dictionary scan does not flag — it only scans `Fluent.Shared.xaml`), root carries the `TextElement.Foreground` marker, and it is listed in the roots array.
- [ ] **Manual smoke (WPF — not unit-testable).** Launch the app, then:
  1. **FFmpeg absent:** temporarily rename `tools\ffmpeg` → confirm the Sessions page shows "Import audio…" DISABLED with a tooltip naming `tools/fetch-ffmpeg.ps1`; nothing crashes. Rename back, restart → enabled.
  2. **Real stereo phone recording with channel mapping (the design's target case):** import a real stereo M4A/MP3 phone recording; confirm the probe preview (name/duration/size/format), tick "Each party is on its own channel", set the recorded date to the call's real date (not today), Import → staged progress Copy → Decode → Transcribe → Save, completion opens the read view, the Sessions row appears at the RECORDED date's list position with Source "Imported — M4A" (hover shows the original filename), playback plays, and side attribution follows the channels (left party = Me, right = Them). Verify the session folder holds `source\<original>` byte-identical and `session.json` shows `origin: "imported"` with the sha256.
  3. **Swap control:** re-import the same file with "Swap sides" ticked → the parties' sides flip.
  4. **Mono/downmix path:** import the same file with the stereo box UNTICKED → single-leg session; run Split speakers on it → works as on a recorded session.
  5. **Cancel mid-import:** start an import of a long file, press Cancel during Transcribe → dialog reports cancelled, NO row appears, the sessions folder has no partial, the original file is untouched.
  6. **One-engine-at-a-time, both directions:** start a recording, press "Import audio…" → refusal info message; stop, retry → dialog opens. Start a re-transcription, press "Import audio…" → refusal info message. Start a long import, then try Record / Re-transcribe while the import's Transcribe stage runs → both refuse with the "audio import" busy reason (the ExternalEngineBusy chain).
  7. **Duration-mismatch gate (best-effort):** if a corrupt/duration-lying file is available (e.g. a truncated MP3 whose header still claims full length), import it → the Continue/Cancel warning appears after Decode; Continue → the transcript's top shows the "imported audio duration mismatch" marker; re-import and Cancel → no session remains.
  8. **Theme:** open the dialog in light and dark themes → all text readable, error text visible on an invalid date.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ImportDialog.xaml src/LocalScribe.App/ImportDialog.xaml.cs src/LocalScribe.App/Pages/SessionsPage.xaml src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/XamlHygieneTests.cs
git commit -m "feat(app): ImportDialog window + Sessions-page Import button + composition wiring

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-review

**(a) Spec coverage — every design §4 requirement maps to a task:**
- §4.1 provenance: original copied unmodified to `source\<original-filename>` + SHA-256 + original timestamps recorded → **Task 5** (copy+hash single pass, timestamps into `ImportedSource` AND mirrored onto the copy) over **Task 1**'s `Origin`/`ImportedSourceInfo`/`SourceDir`. Weights provenance (master 7d6c88d): the imported `session.json` also records `WeightsFile` — the runner's finalize writes it and the importer's Save-stage `with` rewrite preserves it — pinned by asserts in **Tasks 3 and 5**, so an imported session carries the same what-actually-ran evidence as a live one. Decoded-stream truth (rate/channels/duration from the decoder's own output, never container headers) → **Task 2** (`DecodedAudio` contract + `DescribeWav` reads ffmpeg's own output) pinned end-to-end in **Task 7**'s fixture. Duration cross-check >1% → Continue/Cancel gate after Decode + marker on Continue → **Tasks 5/6** (`confirmDurationMismatch` seam, `Markers.ImportedDurationMismatch`, decline throws OCE and deletes the folder).
- §4.2 FFmpeg: SHA-pinned LGPL shared fetch script → **Task 7** (fail-closed two-run pin flow mirroring fetch-models.ps1); ffprobe preview + subprocess decode with timeout + stderr capture → **Task 2**; WAV read natively → **Task 2**; FFmpeg absent → Import disabled with a clear fetch-script message, nothing crashes → **Tasks 8/10** (`FfmpegLocator` + disabled button with ShowOnDisabled tooltip); `IAudioDecoder` + fake for unit tests, one `Category=Fixture` real-MP3 test → **Tasks 2/5/7**.
- §4.3 channel mapping: stereo Yes → L→Local/R→Remote with swap; No/unsure or mono → one mono leg; >2 ch → downmix + note; decode truth wins over the dialog answer → **Task 4** (`ChannelMapper.Plan` pins all five combinations) + **Tasks 5/6** (mapping label, `ImportedDownmixed` marker) + **Task 9** (checkbox → `StereoMapping` pinned by `Stereo_answers_map_to_the_three_mappings`).
- §4.4 UI: "Import audio…" button → **Tasks 8/10**; file picker with the named formats + All files → **Task 9** (`FileFilter`); probe preview name/duration/size/format → **Task 9**; editable title defaulting from filename → **Task 9**; editable recorded-date defaulting from media-creation tag then file timestamps, driving the id/StartedAtUtc → **Tasks 9** (default+parse) and **5** (PinnedTimeProvider through SessionBootstrap; `SessionId.New` derives from the local wall-clock — verified and pinned by `Import_creates_a_finalized_session_with_provenance_at_the_recorded_date`); matter tagging with the Record-console picker → **Task 9** (same `MatterPickRow`/`MatterSearch` trio); staged progress Copy→Decode→Transcribe→Save with Cancel deleting only the partial session folder → **Tasks 5/6/9/10** (X-close during busy cancels first, never orphans); completion opens the session → **Task 10** (`Completed` → `UpsertRowAsync` + `openReadView`); Source shows "Imported — MP3" → **Task 8**. Imported sessions enter the search index automatically → out of THIS branch's hands by design: search (§2) merges LAST and indexes all sessions including imported ones; the imported session is a normal finalized folder, which is the whole contract.
- §4.5 testing: channel-mapping variants (**Task 4**), duration-mismatch marker (**Task 6**), SHA-256 + origin recording (**Task 5**), ffmpeg-missing path (**Tasks 2/8**), cancel cleanup (**Task 6**), decoder-fake unit tests (**Tasks 5/6/9**), one fixture-gated real-MP3 test (**Task 7**).
- Binding sections: §1 evidentiary rules honored (original never touched — pinned by byte/timestamp asserts; partial-only deletion; markers for every degradation); §7 anti-patterns avoided (no container-header trust — the gate + `Decoded*` fields; no silent filtering; nothing expires). One-engine-at-a-time honored in BOTH directions: **Task 8**'s guard refuses import while recording/finalizing OR while a re-transcription runs (the injected `retranscribingSessionId` probe over `RetranscriptionRunner.RunningSessionId`), and **Task 10** registers a running import on `SessionController.ExternalEngineBusy` (chained over the retranscription branch's prior delegate, never clobbered) so the live engine and re-transcription refuse while an import transcribes.
- **Deviations (deliberate, argued):** (1) FLAC inputs go through FFmpeg, not `FlacPcmReader` — the existing reader hard-rejects non-16 kHz/mono, which arbitrary received FLACs are; FFmpeg is the design's own "one deterministic decode path". (2) Matter vocabulary bias is NOT applied to import transcription (the runner applies global vocabulary only); re-transcription (§3, merged before this branch) is the vocab-aware re-run path. (3) `AppKind.Manual` is reused for imported sessions rather than a new enum member — `Origin` is the discriminator and the Source column overrides the display.

**(b) Placeholder scan:** every step carries real, grounded code — exact current lines quoted from master @ 7d6c88d for every modification (`SessionRecord.cs` 38/39, `StoragePaths.cs` 24, `Markers.cs` 41/42, `SessionBootstrap.cs` 13–16/26, `OfflinePipelineRunner.cs` 10–16/44–53 — all four re-verified byte-identical or re-quoted after the cpu-threads-quantized-weights merge, `SessionRowViewModel.cs` 96–104, `SessionsPageViewModel.cs` 88/102–104/116, `SessionsPage.xaml` 39–40, `App.xaml.cs` 111–120/136–145/273, `XamlHygieneTests.cs` 52–64, `.gitignore` tail). The fetch script's empty `$Pinned*` constants are not placeholders but the designed fail-closed first-fetch sentinel (the instruction says the SHA-256 is computed and pinned at first fetch); the pin-flow step runs it and pastes real values before the commit. The retranscription-branch types (`ExternalEngineBusy`, `RetranscriptionRunner.RunningSessionId`) are declared Consumes with exact signatures and two MARKED call sites for identifier adaptation — deliberate cross-plan contracts, not placeholders; the VM-level guard compiles and tests without them via the injected probe. No "TBD"/"similar to"/"add error handling later" anywhere.

**(c) Type consistency across tasks:** `IAudioDecoder`/`AudioProbeResult`/`DecodedAudio` (Task 2) are consumed with identical shapes by `AudioImporter` (Task 5), the test fakes (Tasks 5/6/9), and `FfmpegAudioDecoder` (Tasks 2/7). `StereoMapping`/`LegPlan`/`ChannelMapPlan` (Task 4) flow into `ImportRequest.Stereo` (Task 5) and the dialog's checkbox mapping (Task 9). `ImportAsync(ImportRequest, IProgress<ImportStage>?, Func<DurationMismatchInfo, Task<bool>>, CancellationToken) : Task<string>` (Task 5) matches `ImportRunner` (Task 9 — nullable-annotation-only difference, an identity conversion); Task 10 invokes it through a wrapper lambda of the same shape that sets/clears the `importBusy` string consumed by the chained `ExternalEngineBusy` delegate (`Func<string?>`). `SessionsPageViewModel`'s two new ctor params are both optional (`bool importAvailable = false`, `Func<string?>? retranscribingSessionId = null`) and `retranscribingSessionId`'s return type matches `RetranscriptionRunner.RunningSessionId : string?` exactly. `SessionRecord.Origin : string` / `ImportedSource : ImportedSourceInfo?` (Task 1) are written by Task 5, preserved by Task 3's `live with {...}` branch, and read by Task 8's row VM. Both being optional, all pre-existing `SessionsPageViewModel` constructions (tests + composition) compile unchanged; Task 10 passes both by name. `OpenPathRequest` (Task 9) mirrors `SavePathRequest` and is consumed only via the `pickOpenPath` lambda (Task 10). `FormatDuration` exists in three places by design (Core `AudioImporter` for markers, App VM for preview, App.xaml.cs for the MessageBox) with the same `h\:mm\:ss`/`m\:ss` rendering — the marker test pins the Core one, the dialog test pins the VM's `mm\:ss` variant. All consistent.
