# Tier 1C: Trustworthy output - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make an exported LocalScribe transcript defensible. Three deliverables from
`docs/superpowers/specs/2026-08-05-tier1-hardening-design.md`: **T1-6** disclose which engine actually
produced a live recording (and fix the three engine-ladder defects found alongside the owner ruling),
**T1-7** seal the session folder with an integrity manifest that also records the silence the app
itself fabricated, **T1-8** complete the export provenance and disclose the human editing layer.

**Architecture:** Everything new is a pure Core type plus one composition point.
`ModelLadder`/`TranscriptionWorker` fixes are local. Engine disclosure is one shared string helper
(`EngineDisclosure.Line`) consumed by a transcript marker, the ready-card chip and export metadata,
so the three can never word the same fact differently. The manifest is `SessionManifest` +
`ManifestStore` + `ManifestBuilder`, written through the existing `AtomicFile`/`JsonFile` primitives
and refreshed from the single choke point every mutation already calls
(`SessionWriter.RegenerateProjectionsAsync`). Export renderers stay **pure serializers**: every new
fact arrives on `ExportProvenance`/`ExportOptions`, is formatted once in `MetadataFormat`, and is
rendered by all three formats in the same task.

**Tech Stack:** C# / .NET 10, WPF (+ Wpf.Ui), CommunityToolkit.Mvvm, DocumentFormat.OpenXml, xUnit.

## Global Constraints

- **Build/test:** `dotnet build` / `dotnet test` against `F:\LocalScribe\LocalScribe.slnx`. A running
  `LocalScribe.App.exe` locks `Core.dll` -> `MSB3027`. Close it; **never blanket-kill processes** -
  target the specific PID.
- **Test baseline (measured 2026-08-05, `--filter "Category!=Fixture"`):** Core **1186/1186**, App
  **984/984**, Mcp **6/6** = **2176**, zero failures, zero skips. **Judge regressions by failing test
  NAME, never by count.** Fixture-gated tests (`Category=Fixture`) need model weights and private
  corpora and are excluded.
- **ASCII source files.** Non-ASCII in string literals MUST be `\u` escapes; Fluent glyphs follow
  `TrayIconHost.cs:188-191`. The Edit tool silently converts escapes to literal glyphs - byte-scan
  every touched file before committing (zero bytes > 127, CRLF intact).
- **Stage files by name.** Never `git add -A` / `git add .` / `git commit -a`, never `git clean` -
  `tools/diar-eval/`, `.ai-code-review/` and `.claude/` are deliberately untracked.
- **Comment idiom:** name the design doc/date, state the REJECTED alternative and why, CAPITALISE the
  load-bearing word, use ` - ` not an em-dash. Roughly a third of `App.xaml.cs` is comment; match it.
- **Service shape:** `public sealed class X(deps) : IY` primary constructors; delegates (`Func`/
  `Action`) rather than concrete services wherever a test needs to gate them; `TimeProvider` always
  injected, never `DateTime.Now`.
- **Additive settings need no schema bump.** Add the property with a default and the sentence
  "Additive - existing v3 files without it load at this default (the SectionGapMs precedent), so no
  schema bump/migration is required." `SchemaVersion` has stayed 3 across six additive rounds.
- **Tests:** xunit `[Fact]`, `public sealed class XTests : IDisposable`, GUID temp root named
  `ls-<slug>-<guid>` under `Path.GetTempPath()`, swallow-everything `Dispose`. App.Tests writes
  `using Xunit;` explicitly; Core.Tests has `<Using Include="Xunit" />` and must not.
- **Transcripts are legal evidence.** No path may drop, reorder or silently rewrite content.
- **Spec:** `docs/superpowers/specs/2026-08-05-tier1-hardening-design.md`.
- **Shared contract:** every "SHARED-CONTRACT section N" reference in this plan means
  `docs/superpowers/specs/2026-08-05-tier1-shared-contract.md`. It is FIXED and **created by Plan A**
  (`2026-08-05-tier1a-diagnosability.md`), which must merge first. Do not redefine any of it here.

### Additional constraints specific to this plan

- **The live model cap is a RULING, not a bug.** `BackendSelector.cs:45-51` capping auto at
  `small.en` (CUDA) / `base.en` (Vulkan) is a deliberate realtime-factor decision (spec
  `:54-64`). **No task here raises it.** Note there are TWO unrelated ladders and they must never be
  conflated: `BackendSelector.Ladder` is `["tiny.en", "base.en", "small.en"]`, English-only, used
  only to pick the best model at or below the auto ceiling; `ModelLadder.Rungs` is the mid-session
  downgrade ladder. Task 1 edits `ModelLadder` **only**.
- **Hashing at EXPORT stays permanently out of scope.**
  `docs/superpowers/specs/2026-08-04-transcript-export-scope-dialog-design.md:78` rules out hashing a
  recorded session's multi-GB FLAC on every export. This plan hashes **once at finalize** and reads
  the stored value forward at export time. That does not re-open the ruling and a reviewer must not
  flag it: the export path in Task 12 performs exactly one small `manifest.json` read and never opens
  an audio file.
- **`FileShare.Read` excludes WRITERS.** Every stream this plan opens over a session file uses
  `FileShare.ReadWrite | FileShare.Delete`. This defect has already been fixed twice in this repo
  (`SessionArchiver.cs:34-43`).
- **Word `pPr` children are schema-ordered:** `pStyle(1) -> widowControl(6) -> numPr(7) ->
  suppressLineNumbers(8) -> pBdr(9) -> tabs(11) -> spacing(22) -> ind(23)`. The OpenXML SDK accepts
  any order and tests pass; Word calls the file corrupt. **Microsoft Learn's `pPr` pages list
  children ALPHABETICALLY - that is NOT schema order.** Route every new metadata line through the
  existing `MetaLine(label, value)` helper, which already applies `SuppressLineNumbers`.
- **Never put anything into the docx turn-label NAME run.** `STYLEREF "Transcript Speaker"` in the
  page header returns that run's text verbatim, so a per-turn edit mark placed there would appear in
  the running head of every page.
- **Invariant culture** for every exported and persisted string
  (`string.Create(CultureInfo.InvariantCulture, $"...")`).
- **Branch:** `feat/tier1c-tier-c-trustworthy-output-2026-08-05`. Create it from `master` before
  Task 1: `git switch -c feat/tier1c-tier-c-trustworthy-output-2026-08-05`.
- **Depends on Plan A** (`2026-08-05-tier1a-diagnosability.md`) only for `IDiagnosticLog`, and
  **deliberately does not consume it.** This is an EXPLICIT deviation from SHARED-CONTRACT section 1,
  which frames that interface as "the seam B/C/D write into", and the clause being relaxed is named
  here rather than left implied. The reason: everything this plan adds lives in `LocalScribe.Core`
  (`ManifestBuilder`, `IntegrityVerifier`, `SessionWriter`, the three renderers), so threading
  `IDiagnosticLog` through them would make **every Core task below fail to compile until Plan A has
  landed** - and the four plans are written to be executable independently, on separate branches.
  The deviation is safe because no Plan C failure path is silent. A manifest WRITE that fails throws
  out of `RegenerateProjectionsAsync` exactly as the three projection writes above it already do, and
  reaches the user through `IUiErrorReporter`, which Plan A instruments. A manifest this build cannot
  READ degrades to "unsealed" (Task 12 Step 6), and Verify integrity then says so in words rather
  than reporting a pass. If a later round adds log lines here, the ONE sink is
  `AppComposition.Log` - SHARED-CONTRACT section 3a, amended 2026-08-05 - reached as `comp.Log`,
  never a local and never a second instance.

---

## File Structure

**Created:**
- `src/LocalScribe.Core/Transcription/EngineDisclosure.cs` - the ONE composed
  "model (BACKEND), accuracy tier" string, shared by the start marker, the ready-card chip and export.
- `src/LocalScribe.Core/Model/SessionManifest.cs` - `SessionManifest`, `ManifestFile`,
  `FabricatedSpan`, `FabricatedSilenceRecord`: the sealed-file record and the fabricated-silence record.
- `src/LocalScribe.Core/Storage/ManifestStore.cs` - read/write `manifest.json` through
  `JsonFile`/`SchemaGuard`, the `SessionStore` shape.
- `src/LocalScribe.Core/Storage/ManifestBuilder.cs` - enumerates the sealed files, streams SHA-256
  with `FileShare.ReadWrite`, carries audio hashes forward on a size+mtime match.
- `src/LocalScribe.Core/Storage/IntegrityVerifier.cs` - `IntegrityReport` / `IntegrityCheck` /
  `IntegrityStatus` plus the per-file OK/CHANGED/MISSING comparison and its one-line summary.
- `tests/LocalScribe.Core.Tests/EngineDisclosureTests.cs`
- `tests/LocalScribe.Core.Tests/ManifestBuilderTests.cs`
- `tests/LocalScribe.Core.Tests/IntegrityVerifierTests.cs`
- `tests/LocalScribe.Core.Tests/HumanLayerLineTests.cs`
- `tests/LocalScribe.App.Tests/SessionsPageVerifyIntegrityTests.cs`

**Modified:**
- `src/LocalScribe.Core/Transcription/ModelLadder.cs:7` - `large-v3-turbo` becomes the top rung.
- `src/LocalScribe.Core/Transcription/TranscriptionWorker.cs:7-14,29,94-134` - re-armable lagging
  downgrade, capped CPU-floor OOM retry, two new options.
- `src/LocalScribe.Core/Transcription/WhisperModelCatalog.cs:3-8,35-44` - amended class doc (the
  tier IS exported now) plus `AccuracyTier`.
- `src/LocalScribe.Core/Model/Markers.cs` - `TranscriptionEngine` start marker.
- `src/LocalScribe.Core/Live/SessionController.cs:507-518,1259-1280` - writes the start marker;
  passes the fabricated-silence map into the finalize regenerate.
- `src/LocalScribe.Core/Live/AlignedAudioWriter.cs` - records the ranges it fabricates.
- `src/LocalScribe.Core/Storage/StoragePaths.cs` - `ManifestJson(id)` / `ManifestJson(id, versionId)`.
- `src/LocalScribe.Core/Storage/SessionWriter.cs:19-33` - refreshes EVERY version's manifest after
  each projection regenerate (the choke point every overlay write already calls), plus a public
  `ResealAsync` for the two session.json/speakers.json writers that deliberately skip that choke
  point.
- `src/LocalScribe.Core/Storage/SessionProjectionLoader.cs:12-24,86,109-110` - `LoadedProjection`
  carries the dedup-suppressed count.
- `src/LocalScribe.Core/Projection/TranscriptProjection.cs:13-16,49` - `Build` overload that
  surfaces the suppressed count.
- `src/LocalScribe.Core/Projection/ExportProvenance.cs` - session id, exported-at, app version,
  weights file, model accuracy, transcript hash, recorded-audio hashes, human-layer counts.
- `src/LocalScribe.Core/Projection/ExportOptions.cs` - `MarkCorrectedTurns`.
- `src/LocalScribe.Core/Projection/ExportNotices.cs` - `CorrectedTurnMark`.
- `src/LocalScribe.Core/Projection/MetadataFormat.cs` - `HumanLayerLine`, `RecordedAudioLines`.
- `src/LocalScribe.Core/Projection/DocxRenderer.cs:53-81,274-281,96-107` - new metadata lines,
  per-turn edit mark.
- `src/LocalScribe.Core/Projection/MarkdownRenderer.cs:48-74,101-132` - same lines, same mark.
- `src/LocalScribe.Core/Projection/PlainTextRenderer.cs:51-74,93-113` - same lines, same mark.
- `src/LocalScribe.Core/Model/Settings.cs` - `ExportSetting.MarkCorrectedTurns`.
- `src/LocalScribe.App/Services/MaintenanceService.cs:1003-1090` - manifest read per export,
  `ProvenanceFor` gains `TimeProvider` + manifest, `VerifyIntegrityAsync`; plus a reseal at the two
  writers that skip `RegenerateProjectionsAsync` (`SetActiveVersionCoreAsync:166-178` and
  `PurgeAllVoiceprintsAsync:762-770`).
- `src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs:99-103,267-280` - accuracy tier on
  the ready-card engine chip.
- `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs` - `VerifyIntegrityCommand`.
- `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs` - `MarkCorrectedTurns` seed/persist.
- `src/LocalScribe.App/Pages/SessionsPage.xaml` - Verify-integrity button + context-menu item.
- `src/LocalScribe.App/LiveViewWindow.xaml:60` - the ready-card engine chip gains the
  re-transcribe-for-accuracy ToolTip.
- `src/LocalScribe.App/ExportDialog.xaml` - mark-corrected-turns checkbox.
- Tests: `BackendSelectorTests.cs`, `TranscriptionWorkerTests.cs`, `SessionControllerTests.cs`,
  `SessionWriterTests.cs`, `AlignedAudioWriterTests.cs`, `StoragePathsTests.cs`,
  `RecordingConsoleViewModelTests.cs`, `DocxRendererTests.cs`, `MarkdownRendererWriteTests.cs`,
  `PlainTextRendererWriteTests.cs`, `MetadataFormatTests.cs`, `MaintenanceServiceProvenanceTests.cs`,
  `MaintenanceServiceVersionsTests.cs`, `ExportDialogViewModelTests.cs`,
  `TranscriptProjectionTests.cs`, `TranscriptLinesViewModelTests.cs`.

**All line numbers in this plan are PRE-ROUND**, measured against `master` on 2026-08-05. Tasks 12,
13 and 14 each insert lines into the metadata block of all three renderers, so by the time Task 15
runs those files have shifted by roughly the number of metadata lines added. Every step that edits a
shifted file therefore gives a CONTENT anchor (the exact line of code to find) as well as the
pre-round number - follow the anchor, not the number.

---

## Task 1: `large-v3-turbo` joins the downgrade ladder

`ModelLadder.Rungs` is `{large-v3, medium, small, base, tiny}` with no turbo entry, so
`Downgrade("large-v3-turbo")` returns `null` (`Array.IndexOf` < 0). A user who explicitly picks the
catalog-recommended model therefore gets **no** VRAM-OOM ladder step at all - only
`DowngradeAsync`'s fall-to-CPU, which at the CPU floor is a no-op that recreates the same engine.

**Files:**
- Modify: `src/LocalScribe.Core/Transcription/ModelLadder.cs:7`
- Test: `tests/LocalScribe.Core.Tests/BackendSelectorTests.cs` (append after the existing
  `Ladder_steps_down_and_stops_at_floor` theory at `:66-73`)

**Interfaces:**
- Consumes: nothing.
- Produces: no new names. `ModelLadder.Downgrade("large-v3-turbo")` now returns `"large-v3"`;
  `ModelLadder.IsKnownStem("large-v3-turbo")` now returns `true`;
  `ModelLadder.HasEnglishVariant("large-v3-turbo")` stays `false`. Task 3 relies on `Downgrade`
  returning non-null for turbo.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/BackendSelectorTests.cs`, inside the class:

```csharp
    [Theory]
    [InlineData("large-v3-turbo", "large-v3")]
    [InlineData("large-v3", "medium")]
    [InlineData("medium", "small")]
    [InlineData("small", "base")]
    [InlineData("base", "tiny")]
    [InlineData("tiny", null)]
    public void Turbo_is_the_top_rung_so_an_explicit_turbo_pick_can_still_step_down(
        string from, string? expected)
    {
        // Tier 1 T1-6 (spec 2026-08-05 :78-81): with turbo absent from Rungs, Downgrade returned
        // null and DowngradeAsync's null branch only flipped Backend to Cpu - so an explicit
        // large-v3-turbo pick that VRAM-OOMed on CUDA fell straight to CPU with no ladder step,
        // and a floor OOM then retried the SAME segment forever.
        Assert.Equal(expected, ModelLadder.Downgrade(from));
    }

    [Fact]
    public void Turbo_is_a_known_stem_but_has_no_english_weights()
    {
        // Finding I2 guard: there is no ggml-large-v3-turbo.en.bin. If HasEnglishVariant ever
        // returns true for it, TranscriptionWorker's language-lock swap (:164) tries to create an
        // engine over a nonexistent file and fails SILENTLY (the swap's catch only raises
        // MODEL_DOWNLOAD_FAILED). A green suite would not otherwise catch that.
        Assert.True(ModelLadder.IsKnownStem("large-v3-turbo"));
        Assert.False(ModelLadder.HasEnglishVariant("large-v3-turbo"));
        Assert.False(ModelLadder.HasEnglishVariant("large-v3"));
    }

    [Fact]
    public void Adding_turbo_to_the_downgrade_ladder_does_not_move_the_live_ceiling()
    {
        // Owner ruling 2026-08-05: the live cap stays. BackendSelector.Ladder is a SEPARATE,
        // English-only, 3-rung array; turbo present on disk must NOT raise the CUDA auto ceiling
        // above small.en. Standing guard alongside Big_nvidia_gets_cuda_small_en above.
        var (plan, downgradedFrom) = BackendSelector.Select(new HardwareInfo(true, 12000, true, 16),
            S(), Present("large-v3-turbo", "large-v3", "small.en", "base.en", "tiny.en"));
        Assert.Equal("small.en", plan.ModelName);
        Assert.Null(downgradedFrom);
    }
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~BackendSelectorTests"`

Expected: FAIL. `Turbo_is_the_top_rung_...` case `("large-v3-turbo", "large-v3")` fails with
`Assert.Equal() Failure: Values differ / Expected: "large-v3" / Actual: (null)`, and
`Turbo_is_a_known_stem_but_has_no_english_weights` fails on
`Assert.True(ModelLadder.IsKnownStem("large-v3-turbo"))`.

- [ ] **Step 3: Write the minimal implementation**

In `src/LocalScribe.Core/Transcription/ModelLadder.cs`, replace line 7:

```csharp
    // Best -> worst. large-v3-turbo leads (Tier 1 T1-6, spec 2026-08-05 :78-81): it is Rank 0 in
    // WhisperModelCatalog (more accurate per second than large-v3) and it is the IMPORT default, so
    // it is the model an explicit picker most often chooses - and it was the one name for which
    // Downgrade returned null, leaving that user with no VRAM-OOM ladder at all. REJECTED: adding
    // it to BackendSelector's own 3-rung Ladder, which would raise the LIVE ceiling and break the
    // owner's 2026-08-05 ruling that the realtime-factor cap stays. These two arrays are unrelated.
    private static readonly string[] Rungs = { "large-v3-turbo", "large-v3", "medium", "small", "base", "tiny" };
```

Leave `Downgrade`, `IsKnownStem` and `HasEnglishVariant` untouched. `HasEnglishVariant` must stay
`stem is "tiny" or "base" or "small" or "medium"` - a hand-typed `"large-v3-turbo.en"` names no real
weights file, and Start's presence gate (`SessionController.cs:430-435`) refuses it before the worker
ever runs.

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~BackendSelectorTests"`

Expected: PASS, all of them, including the pre-existing `Ladder_steps_down_and_stops_at_floor`
theory (its `("large-v3", "medium")` case is unchanged by the insertion) and
`Big_nvidia_gets_cuda_small_en`.

- [ ] **Step 5: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Transcription/ModelLadder.cs tests/LocalScribe.Core.Tests/BackendSelectorTests.cs
git commit -m "fix(transcription): large-v3-turbo is the top downgrade-ladder rung"
```

---

## Task 2: the sustained-RTF downgrade re-arms, bounded

`_laggingRaised` is set once and never reset (`TranscriptionWorker.cs:29,126` are its only two
occurrences), so the sustained-RTF marker and downgrade fire **exactly once per session**. A session
that is still lagging after its first ladder step gets neither a second marker nor a second step -
and a two-hour call that degrades halfway through leaves no trace of it.

The naive fix (delete the flag) would fire per-segment. The trigger already clears `_rtfWindow` after
each firing, so a **counter** with a hard limit gives the right behaviour: a firing needs a full
fresh window of consistently-lagging segments, and there can be at most `LaggingRearmLimit` of them.

**Files:**
- Modify: `src/LocalScribe.Core/Transcription/TranscriptionWorker.cs:7-14` (options), `:16-19`
  (class doc), `:29` (field), `:43-48` (`RecentRtf` doc), `:121-134` (trigger block)
- Test: `tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs:91-118` (REPLACE the existing
  `Sustained_rtf_over_one_raises_lagging_marker_once_and_downgrades`), plus one new fact

**Interfaces:**
- Consumes: `ModelLadder.Downgrade` (Task 1) - unchanged signature.
- Produces: `TranscriptionWorkerOptions.LaggingRearmLimit { get; init; } = 3`. Task 3 adds a second
  option to the same record; both are `{ get; init; }` with inline defaults, so a test overrides one
  field at a time (`new TranscriptionWorkerOptions { LaggingWindow = 3 }`).

- [ ] **Step 1: Write the failing tests**

In `tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs`, **delete** the whole
`Sustained_rtf_over_one_raises_lagging_marker_once_and_downgrades` method (`:91-118`) and put these
two in its place:

```csharp
    [Fact]
    public async Task Sustained_rtf_re_arms_once_the_window_refills_with_fresh_data()
    {
        // Tier 1 T1-6 (spec 2026-08-05 :82-83): _laggingRaised was set once and never reset, so a
        // session that kept lagging after its first ladder step got no second marker and no second
        // step. Re-arming is gated on the window REFILLING (the trigger already clears it), so with
        // LaggingWindow=3 and six uniformly slow segments the trigger fires exactly twice: at
        // segment index 2 and again at index 5.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
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

        Assert.Equal(2, markers.Count(m => m == Markers.TranscriptionLagging));
        // Each ladder step IS a weights change and is traced as one (review finding 2026-07-13):
        // small.en -> base.en, then base.en -> tiny.en.
        Assert.Equal(
            new[]
            {
                string.Format(Markers.TranscriptionWeightsChanged, "ggml-small.en.bin", "ggml-base.en.bin"),
                string.Format(Markers.TranscriptionWeightsChanged, "ggml-base.en.bin", "ggml-tiny.en.bin"),
            },
            markers.Where(m => m != Markers.TranscriptionLagging));
        Assert.Equal(3, factory.Created.Count);                              // initial + two downgrades
        Assert.Equal("base.en", factory.Created[1].Plan.ModelName);
        Assert.Equal("tiny.en", factory.Created[2].Plan.ModelName);
    }

    [Fact]
    public async Task Lagging_downgrades_stop_at_the_rearm_limit_instead_of_cascading()
    {
        // The cap is the whole reason re-arming is safe: without it a genuinely slow machine walks
        // small.en -> base.en -> tiny.en -> CPU and keeps recreating engines at the floor, each step
        // silently degrading the evidentiary record. Two firings, then silence, over ten segments.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.ElapsedMs += 2 * (s.EndMs - s.StartMs);
            return new TranscriptionResult("slow", "en", 0.0);
        }));
        var markers = new List<string>();
        var worker = Worker(factory, clock,
            new TranscriptionWorkerOptions { LaggingWindow = 2, LaggingRearmLimit = 2 });
        worker.MarkerRaised += markers.Add;

        var run = worker.RunAsync(default);
        for (int i = 0; i < 10; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
        worker.Complete();
        await run;

        Assert.Equal(2, markers.Count(m => m == Markers.TranscriptionLagging));
        Assert.Equal(3, factory.Created.Count);                              // capped, not one per window
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~TranscriptionWorkerTests"`

Expected: FAIL. `Lagging_downgrades_stop_at_the_rearm_limit_instead_of_cascading` fails to COMPILE
first (`CS0117: 'TranscriptionWorkerOptions' does not contain a definition for
'LaggingRearmLimit'`). After the option exists, `Sustained_rtf_re_arms_...` fails with
`Assert.Equal() Failure / Expected: 2 / Actual: 1`.

- [ ] **Step 3: Write the minimal implementation**

In `src/LocalScribe.Core/Transcription/TranscriptionWorker.cs`, add the option to the record at
`:7-14`, after `LaggingWindow`:

```csharp
    /// <summary>How many times the sustained-RTF trigger may fire in ONE session (Tier 1 T1-6,
    /// spec 2026-08-05 :82-83). Was structurally 1 (a never-reset bool), so a session that kept
    /// lagging after its first ladder step left NO further trace. Each firing needs a full fresh
    /// window of consistently-lagging segments, because the trigger clears _rtfWindow. The cap is
    /// what makes re-arming safe: uncapped, a slow machine walks small.en -> base.en -> tiny.en ->
    /// CPU inside one call, each step silently degrading the evidentiary record.</summary>
    public int LaggingRearmLimit { get; init; } = 3;
```

Replace the field at `:29`:

```csharp
    // How many times the sustained-RTF trigger has fired this session (Tier 1 T1-6). REPLACES the
    // one-shot bool _laggingRaised, which was set at :126 and reset NOWHERE.
    private int _laggingFirings;
```

Replace the trigger block at `:121-134`:

```csharp
                if (_laggingFirings < _o.LaggingRearmLimit
                    && _rtfWindow.Count >= _o.LaggingWindow
                    && _rtfWindow.All(r => r > _o.LaggingRtfThreshold))
                {
                    // Marker + one ladder step (spec 3/8.1), re-armable up to LaggingRearmLimit.
                    _laggingFirings++;
                    MarkerRaised?.Invoke(Markers.TranscriptionLagging);
                    ErrorRaised?.Invoke("RTF_LAGGING");
                    engine = await DowngradeAsync(engine, ct);
                    _rtfWindow.Clear();
                    // Fresh window: the pre-downgrade engine's average must not keep the keep-up
                    // chip red after the ladder step already replaced that engine. It is ALSO the
                    // re-arm gate - the next firing cannot happen until LaggingWindow fresh,
                    // post-downgrade segments have all measured above the threshold.
                    Volatile.Write(ref _recentRtf, double.NaN);
                }
```

Then fix the two doc comments this change makes false - a stale comment is a defect in this codebase
(the same rule Task 4 Step 3 applies to `WhisperModelInfo`). In the class summary at `:16-19`,
replace the clause `sustained-RTF downgrade with a one-shot \`transcription lagging\` marker (spec
section 3/section 8)` with:

```csharp
/// lock (recreate once), VRAM-OOM downgrade + same-segment retry, sustained-RTF downgrade raising a
/// RE-ARMABLE `transcription lagging` marker capped at LaggingRearmLimit (Tier 1 T1-6, spec
/// 2026-08-05 :82-83; spec section 3/section 8).</summary>
```

and in the `RecentRtf` doc at `:43-48`, replace `and again right after the one-shot lagging downgrade
clears the window` with:

```csharp
    /// tracked segment and again right after EACH lagging downgrade clears the window (Tier 1 T1-6:
    /// the trigger re-arms, so this reset is no longer a once-per-session event)
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~TranscriptionWorkerTests"`

Expected: PASS, all tests in the class. If `Vram_oom_downgrades_one_step_and_retries_same_segment`
fails, the trigger block was moved relative to `TrackRtf` - restore the original ordering
(`TrackRtf` runs on the success path only, before this block).

- [ ] **Step 5: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Transcription/TranscriptionWorker.cs tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs
git commit -m "fix(transcription): sustained-RTF downgrade re-arms, capped at LaggingRearmLimit"
```

---

## Task 3: cap the CPU-floor OOM retry loop

`TranscriptionWorker.cs:99-119` is a `while (true)` with no attempt counter. At the CPU floor
`DowngradeAsync` only flips `Backend` to `Cpu` (already `Cpu`), so a persistent floor OOM recreates
the engine and retries the SAME segment forever - no marker, no escalation, just a repeated
`VRAM_OOM` error code and a live recording whose transcript stops growing.

The escape must not drop audio. Rethrowing after the cap faults `workerLoop`, which
`SessionController.cs:643-659` already handles: it writes `Markers.TranscriptionFailed`, raises
`TRANSCRIPTION_FAILED`, shows "Live transcription stopped - audio is still recording", and
deliberately does **not** cancel `captureCts`. That converts an invisible infinite spin into a
recorded, visible transcription failure with the audio intact.

**Files:**
- Modify: `src/LocalScribe.Core/Transcription/TranscriptionWorker.cs:7-14` (options), `:94-119`
- Test: `tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs` (append)

**Interfaces:**
- Consumes: `TranscriptionWorkerOptions.LaggingRearmLimit` (Task 2) - same record, do not remove it.
- Produces: `TranscriptionWorkerOptions.MaxOomRetries { get; init; } = 5`. After the cap
  `RunAsync` faults with the original `VramOutOfMemoryException`.

- [ ] **Step 1: Write the failing test**

Append to `tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task A_persistent_oom_gives_up_after_the_cap_instead_of_spinning_forever()
    {
        // Tier 1 T1-6 (spec 2026-08-05 :142): the pre-cap `while (true)` at :99-119 retried the SAME
        // segment forever at the CPU floor - no marker, no escalation, a live recording whose
        // transcript silently stopped growing. EVERY engine this factory makes throws, so without
        // the cap this test HANGS rather than failing; run it with the filter below, not the suite.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            _ => throw new VramOutOfMemoryException("oom")));
        var errors = new List<string>();
        var worker = Worker(factory, clock, new TranscriptionWorkerOptions { MaxOomRetries = 2 });
        worker.ErrorRaised += errors.Add;

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);
        worker.Complete();

        // The fault escapes RunAsync so SessionController's OnlyOnFaulted continuation
        // (SessionController.cs:643-659) writes "transcription failed" and keeps AUDIO recording -
        // audio is never dropped, which is the 2026-07-02 user decision this cap has to respect.
        await Assert.ThrowsAsync<VramOutOfMemoryException>(() => run);
        Assert.Equal(3, errors.Count(e => e == "VRAM_OOM"));   // two retries plus the fatal one
        Assert.Equal(3, factory.Created.Count);                // initial engine + two recreations
    }
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~A_persistent_oom_gives_up_after_the_cap"`

Expected: FAIL to compile - `CS0117: 'TranscriptionWorkerOptions' does not contain a definition for
'MaxOomRetries'`. **Do not run this test against the un-capped worker without the filter**: with
`MaxOomRetries` present but the loop still uncapped it never terminates.

- [ ] **Step 3: Write the minimal implementation**

Add to `TranscriptionWorkerOptions` (after `LaggingRearmLimit` from Task 2):

```csharp
    /// <summary>Consecutive VRAM-OOM retries allowed on ONE segment before the worker gives up
    /// (Tier 1 T1-6, spec 2026-08-05 :142). The pre-cap loop retried forever at the CPU floor,
    /// where DowngradeAsync only re-flips an already-Cpu backend - an invisible spin, since the
    /// only symptom was a repeated VRAM_OOM error code. REJECTED: dropping the segment, which
    /// violates the 2026-07-02 "never drop audio" decision. Instead the fault escapes RunAsync,
    /// where the existing OnlyOnFaulted handler writes the "transcription failed" marker and lets
    /// AUDIO keep recording. Counted per segment, so an early OOM does not penalise a later one.</summary>
    public int MaxOomRetries { get; init; } = 5;
```

In `RunAsync`, replace `:94-119` (the `await foreach` header through the `while (true)` block):

```csharp
            await foreach (var segment in _queue.Reader.ReadAllAsync(ct))
            {
                TranscriptionResult result;
                string producedBy;
                string producedByWeights;
                int oomRetries = 0;                  // per SEGMENT, not per session
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
                        // Tier 1 T1-6: bounded. A real floor OOM implies system RAM exhaustion, and
                        // retrying it forever is indistinguishable from a hang. Rethrowing surfaces
                        // it through the worker fault path, which marks the transcript and leaves
                        // capture running (audio is never dropped - user decision 2026-07-02).
                        if (++oomRetries > _o.MaxOomRetries) throw;
                        engine = await DowngradeAsync(engine, ct);
                        continue;                        // retry the SAME segment
                    }
                    TrackRtf(_clock.ElapsedMs - t0, segment.EndMs - segment.StartMs);
                    producedBy = engine.ModelName;         // capture before any later downgrade
                    producedByWeights = engine.WeightsFile;
                    break;
                }
```

- [ ] **Step 4: Run the test and confirm it passes**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~TranscriptionWorkerTests"`

Expected: PASS, all tests in the class including
`Vram_oom_downgrades_one_step_and_retries_same_segment` (one retry, well under the default 5).

- [ ] **Step 5: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Transcription/TranscriptionWorker.cs tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs
git commit -m "fix(transcription): cap the CPU-floor OOM retry loop at MaxOomRetries"
```

---

## Task 4: `EngineDisclosure` + the session-start engine marker

Nothing in a transcript records which engine produced it. `session.json` holds `Model`/`Backend`, but
both are **last-wins summaries** of how the session ENDED (`SessionController.cs:1267-1275`), so a
session that downgraded mid-call names the model it finished on. The only record of the engine the
session BEGAN on must therefore live in the transcript itself.

**Files:**
- Create: `src/LocalScribe.Core/Transcription/EngineDisclosure.cs`
- Create: `tests/LocalScribe.Core.Tests/EngineDisclosureTests.cs`
- Modify: `src/LocalScribe.Core/Transcription/WhisperModelCatalog.cs:3-8` (class doc), `:35-44`
  (add `AccuracyTier`)
- Modify: `src/LocalScribe.Core/Model/Markers.cs` (add `TranscriptionEngine`)
- Modify: `src/LocalScribe.Core/Live/SessionController.cs:514-518`
- Test: `tests/LocalScribe.Core.Tests/SessionControllerTests.cs` (append)
- Test: `tests/LocalScribe.App.Tests/TranscriptLinesViewModelTests.cs:47` (UPDATE - the marker
  becomes `Lines[0]`; see Step 7)

**Interfaces:**
- Consumes: `BackendPlan(Backend Backend, string ModelName, int? CpuThreads = null)` and
  `WhisperModelCatalog.Describe(string name) : WhisperModelInfo(Name, Subtitle, Rank, EnglishOnly)`,
  both existing.
- Produces:
  - `LocalScribe.Core.Transcription.WhisperModelCatalog.AccuracyTier(string name) : string` -
    `"Basic accuracy"` for `base.en`, `""` for an uncatalogued or sentinel name. Task 5 and Task 12
    call it.
  - `LocalScribe.Core.Transcription.EngineDisclosure.Line(string modelName, Backend backend) : string`
    - `"base.en (CPU), Basic accuracy"`. Only this task calls it.
  - `LocalScribe.Core.Model.Markers.TranscriptionEngine` - `"transcription engine: {0}"`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/EngineDisclosureTests.cs`:

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;

/// <summary>The Start-time engine disclosure (Tier 1 T1-6, spec 2026-08-05 :66-72). The owner
/// ruling froze the live model cap, so the divergence between live (small.en/base.en) and import
/// (large-v3-turbo) must be DISCLOSED instead of removed - in the UI at Start, in a transcript
/// marker, and in export metadata. This is the one string all three derive from.</summary>
public class EngineDisclosureTests
{
    [Theory]
    [InlineData("large-v3-turbo", "Best accuracy at fast speed")]
    [InlineData("large-v3", "Best accuracy")]
    [InlineData("medium.en", "Good accuracy")]
    [InlineData("small.en", "Decent accuracy")]
    [InlineData("base.en", "Basic accuracy")]
    [InlineData("tiny", "Lowest accuracy")]
    public void AccuracyTier_is_the_leading_phrase_of_the_catalog_subtitle(string name, string tier)
        => Assert.Equal(tier, WhisperModelCatalog.AccuracyTier(name));

    [Fact]
    public void AccuracyTier_is_empty_for_the_auto_sentinel_and_for_unknown_weights()
    {
        // Describe() never throws and never returns null: an unknown user-dropped ggml gets
        // Subtitle "". "auto" is a Settings-only sentinel that BackendSelector always resolves to a
        // concrete name before anything reaches here, so it can only appear by mistake - and an
        // accuracy claim about "auto" would be meaningless.
        Assert.Equal("", WhisperModelCatalog.AccuracyTier("distil-large-v3.5"));
        Assert.Equal("", WhisperModelCatalog.AccuracyTier("auto"));
    }

    [Fact]
    public void Line_names_the_model_the_backend_and_the_tier()
        => Assert.Equal("base.en (CPU), Basic accuracy",
            EngineDisclosure.Line("base.en", Backend.Cpu));

    [Fact]
    public void Line_degrades_to_model_and_backend_when_the_model_is_not_cataloged()
    {
        // Open-set rule: a user-dropped ggml must still record WHAT ran. A dangling ", " would be
        // worse than no tier at all in an evidentiary line.
        Assert.Equal("distil-large-v3.5 (CUDA)",
            EngineDisclosure.Line("distil-large-v3.5", Backend.Cuda));
    }
}
```

Append to `tests/LocalScribe.Core.Tests/SessionControllerTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task Session_start_writes_the_engine_marker_as_the_first_transcript_line()
    {
        // Tier 1 T1-6 (spec 2026-08-05 :70-71): session.json's Model/Backend are LAST-WINS
        // summaries, so a downgraded session names the model it ENDED on. The marker is the only
        // record of the engine the session BEGAN on. Stamped at 0 ms explicitly (MarkerAt), not at
        // lastEndMs, so it is unambiguously the session-start fact.
        var (c, _, paths, _) = LiveTestDoubles.MakeController(_root);

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;

        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        var first = lines[0];                       // JSONL is seq order; the marker is queued first
        Assert.Equal(TranscriptKind.Marker, first.Kind);
        Assert.Equal(0, first.StartMs);
        // MakeController's StaticHardwareProbe(false, 0, false, 4) + Model=auto over
        // {base.en, tiny.en} resolves to CPU / base.en.
        Assert.Equal("transcription engine: base.en (CPU), Basic accuracy", first.Text);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~EngineDisclosureTests"`

Expected: FAIL to compile - `CS0103: The name 'EngineDisclosure' does not exist in the current
context` and `CS0117: 'WhisperModelCatalog' does not contain a definition for 'AccuracyTier'`.

- [ ] **Step 3: Add `AccuracyTier` and amend the catalog's class doc**

In `src/LocalScribe.Core/Transcription/WhisperModelCatalog.cs`, replace the `WhisperModelInfo` doc
comment at `:3-8` (the record declaration itself is UNCHANGED - three call sites outside this file
construct it positionally with four arguments):

```csharp
/// <summary>One Whisper model as the pickers present it: the canonical technical name (primary
/// and evidentiary - it is what SessionRecord.Model persists), a plain-language subtitle, an
/// accuracy Rank (lower = more accurate; drives "best available on disk" defaults), and whether
/// the weights are English-only. Display metadata PLUS, since Tier 1 T1-6 (spec 2026-08-05
/// :66-72), an EXPORTED fact: the owner ruling froze the live model cap, so the accuracy tier
/// derived from Subtitle is disclosed at Start, in a transcript marker and in export metadata.
/// (This doc previously read "never persisted, never exported"; that is no longer true, and a
/// stale comment is a defect in this codebase.) Rank and EnglishOnly remain display-only.</summary>
public sealed record WhisperModelInfo(string Name, string Subtitle, int Rank, bool EnglishOnly);
```

Then append to the `WhisperModelCatalog` class, after `DescribeAll`:

```csharp
    /// <summary>The accuracy TIER alone: the leading phrase of the catalog subtitle, up to the
    /// first comma or " - " ("Decent accuracy, English only - quick" -> "Decent accuracy"). Empty
    /// for the "auto" sentinel (Rank -1 - an accuracy claim about it would be meaningless) and for
    /// any uncatalogued name (empty Subtitle), so callers must handle "" rather than assume a tier.
    /// DERIVED from Subtitle rather than stored as a fifth record member: WhisperModelInfo is a
    /// POSITIONAL record constructed with four arguments at three sites outside this file
    /// (ImportDialogViewModel.cs:75, RetranscribeDialogViewModel.cs:40,
    /// SettingsPageViewModel.cs:221), and a fifth member would break all three (Tier 1 T1-6).</summary>
    public static string AccuracyTier(string name)
    {
        var info = Describe(name);
        if (info.Rank < 0 || info.Subtitle.Length == 0) return "";
        string s = info.Subtitle;
        int comma = s.IndexOf(',', StringComparison.Ordinal);
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        int cut = comma < 0 ? dash : dash < 0 ? comma : Math.Min(comma, dash);
        return cut < 0 ? s : s[..cut];
    }
```

- [ ] **Step 4: Create `EngineDisclosure`**

Create `src/LocalScribe.Core/Transcription/EngineDisclosure.cs`:

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Transcription;

/// <summary>The ONE composed "which engine produced this" string (Tier 1 T1-6, spec 2026-08-05
/// :66-72). The owner ruled on 2026-08-05 that the live model cap (small.en on CUDA, base.en on
/// Vulkan) is a deliberate realtime-factor decision and STAYS; what follows is that the divergence
/// from import's large-v3-turbo default must be DISCLOSED. Shared by the session-start transcript
/// marker and, through WhisperModelCatalog.AccuracyTier, by the ready-card chip and the export
/// metadata block - REJECTED: composing the sentence at each site, which is exactly the drift
/// MetadataFormat exists to prevent.</summary>
public static class EngineDisclosure
{
    /// <summary>"base.en (CPU), Basic accuracy" - or "distil-large-v3.5 (CUDA)" when the model is
    /// not in the catalog. The backend is upper-cased to match how PersistFinalAsync stores it in
    /// session.json and how the read-view footer renders it.</summary>
    public static string Line(string modelName, Backend backend)
    {
        string head = modelName + " (" + backend.ToString().ToUpperInvariant() + ")";
        string tier = WhisperModelCatalog.AccuracyTier(modelName);
        return tier.Length == 0 ? head : head + ", " + tier;
    }
}
```

- [ ] **Step 5: Add the marker constant**

In `src/LocalScribe.Core/Model/Markers.cs`, add immediately after the
`TranscriptionWeightsChanged` constant:

```csharp
    /// <summary>Format: {0} = EngineDisclosure.Line(model, backend), e.g. "base.en (CPU), Basic
    /// accuracy". Written ONCE at 0 ms at session start (Tier 1 T1-6, spec 2026-08-05 :70-71).
    /// session.json's Model/Backend are LAST-WINS summaries of how the session ENDED, so a session
    /// that downgraded mid-call names the model it finished on - this marker is the only record of
    /// the engine it BEGAN on, and it lives in the transcript so the evidence travels with the
    /// document. REJECTED: raising it from TranscriptionWorker.Adopt, which runs on every engine
    /// recreation and is deliberately silent on the first one.</summary>
    public const string TranscriptionEngine = "transcription engine: {0}";
```

- [ ] **Step 6: Write the marker at Start**

In `src/LocalScribe.Core/Live/SessionController.cs`, insert immediately after
`worker.ErrorRaised += e => ErrorRaised?.Invoke(e);` (`:516`) and before
`writerLoop = Task.Run(async () =>` (`:518`):

```csharp
                // Tier 1 T1-6 (spec 2026-08-05 :70-71): the engine that STARTED this session, at an
                // explicit 0 ms. MarkerAt (not a bare string) because the bare-string branch of the
                // writer loop stamps at lastEndMs - which is also 0 here, but only by accident.
                // Queued before writerLoop exists, which is safe: ob is UNBOUNDED and FIFO, so this
                // is the first item drained and therefore seq 0, the transcript's first line.
                ob.Writer.TryWrite(new MarkerAt(
                    string.Format(Markers.TranscriptionEngine,
                        EngineDisclosure.Line(plan.ModelName, plan.Backend)), 0));
```

`SessionController.cs` already carries `using LocalScribe.Core.Model;` and
`using LocalScribe.Core.Transcription;`.

- [ ] **Step 7: Fix the App-side test the new first line breaks**

The marker is now seq 0 of **every live session**, so any assertion that indexes position 0 of a live
controller's output now lands on the marker instead of on a segment. **That positional shape -
`Lines[0]` / `stored[0]` / `.First()` over a live controller - is the regression class to grep for**,
and it is invisible to Core-only filters. Exactly one test has it today:
`tests/LocalScribe.App.Tests/TranscriptLinesViewModelTests.cs:47-49`
(`Lines_arrive_at_merger_sorted_positions_and_format`) does `var first = vm.Lines[0];` and then
`Assert.Contains(first.Speaker, new[] { "Me", "Them" })` - and a marker line's `Speaker` is `""`,
pinned by that same file's `Marker_line_maps_with_IsMarker_true_and_mmss_format`.

Replace line 47 with:

```csharp
        // Tier 1 T1-6 (spec 2026-08-05 :70-71): every live session now OPENS with the
        // `transcription engine: ...` marker at 0 ms, so position 0 is a marker and its Speaker is
        // "" by design. This test is about how SEGMENTS map, so select the first non-marker line -
        // the count assertion above already filters markers for the same reason.
        var first = vm.Lines.First(l => !l.IsMarker);
```

Leave lines 48-50 (`Assert.Matches` / `Assert.Contains` / `Assert.NotEqual`) exactly as they are, and
leave `Assert.Equal(2, vm.Lines.Count(l => !l.IsMarker));` at `:46` alone - it already excludes
markers.

Then grep for any other positional read of a live transcript, and fix the same way if one has
appeared since this plan was written:

```bash
cd F:/LocalScribe && grep -rn "Lines\[0\]\|stored\[0\]\|View\[0\]" tests/LocalScribe.App.Tests tests/LocalScribe.Core.Tests
```

- [ ] **Step 8: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~EngineDisclosureTests|FullyQualifiedName~SessionControllerTests|FullyQualifiedName~WhisperModelCatalogTests|FullyQualifiedName~TranscriptLinesViewModelTests"`

Expected: PASS. The `TranscriptLinesViewModelTests` filter is NOT optional - it is an App-project
class that a Core-only filter silently skips, and it is the one this task breaks.
`SessionControllerTests.Start_then_stop_produces_finalized_session_folder` asserts `SegmentCount == 2`
and does not assert `MarkerCount`, so it is unaffected. If any test asserts a marker COUNT for a live
session, raise its expected value by one - the start marker is a real, deliberate extra line, not a
regression.

- [ ] **Step 9: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Transcription/EngineDisclosure.cs src/LocalScribe.Core/Transcription/WhisperModelCatalog.cs src/LocalScribe.Core/Model/Markers.cs src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/EngineDisclosureTests.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs tests/LocalScribe.App.Tests/TranscriptLinesViewModelTests.cs
git commit -m "feat(live): record the start-time engine and accuracy tier as a transcript marker"
```

---

## Task 5: the accuracy tier on the ready-card engine chip

The `downgradedFrom` notice (`SessionController.cs:436-438`) terminates in a **tray balloon**
(`SessionViewModel.cs:172` -> `TrayIconHost.cs:163`), which Focus Assist suppresses - and it is only
ever produced on the `Model == "auto"` branch, so a user who picked a model by hand gets nothing at
all. The ready card's engine chip is the one always-visible Start-time surface, and today it shows
`model . BACKEND` with no accuracy claim.

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs:99-103` (doc), `:234` (new
  public static beside `PreflightLine`), `:274` (the assignment)
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml:60` (the chip's `TextBlock`)
- Test: `tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs:461-462` (UPDATE the pinned
  assertion) plus two new facts

**Interfaces:**
- Consumes: `WhisperModelCatalog.AccuracyTier(string) : string` (Task 4);
  `SessionViewModel.FormatEngineChip(BackendPlan plan, string? modelName = null, Backend? backend = null)`
  (existing, **not** modified - `SessionViewModelTests.cs:457-468` pins its shape).
- Produces: `RecordingConsoleViewModel.AccuracySuffix(string modelName) : string` -
  `" \u00B7 Basic accuracy"` or `""`; `RecordingConsoleViewModel.EngineTooltip : const string` - the
  re-transcribe remedy sentence. Nothing later consumes either.

- [ ] **Step 1: Write the failing tests**

In `tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs`, replace the pinned assertion at
`:461-462` with:

```csharp
        // MakeConsole's controller: StaticHardwareProbe -> Cpu; Model=auto over {base.en,tiny.en}.
        // Tier 1 T1-6 (spec 2026-08-05 :68-69): the chip now names the CATALOG ACCURACY TIER too.
        // The owner ruling froze the cap, so this chip is where a solicitor sees, before pressing
        // Record, that the live engine is "Basic accuracy" - the only Start-time surface before
        // this was a tray balloon, which Focus Assist suppresses and which BackendSelector never
        // produces for an explicit model pick.
        Assert.Equal("base.en \u00B7 CPU \u00B7 Basic accuracy", console.EngineSummary);
```

Then append to the same class:

```csharp
    [Fact]
    public void AccuracySuffix_is_empty_for_a_model_the_catalog_does_not_know()
    {
        // Open-set rule: a user-dropped ggml still records model + backend on the chip. A dangling
        // middle dot with nothing after it would read as a rendering bug.
        Assert.Equal(" \u00B7 Decent accuracy", RecordingConsoleViewModel.AccuracySuffix("small.en"));
        Assert.Equal("", RecordingConsoleViewModel.AccuracySuffix("distil-large-v3.5"));
    }

    [Fact]
    public void The_engine_chip_tooltip_names_the_remedy_not_just_the_limitation()
    {
        // Owner ruling 2026-08-05, fourth bullet (spec :73-74): "re-transcribe at higher accuracy
        // is the documented follow-up path for a session that matters". Disclosing the cap without
        // naming the remedy is half a disclosure - the chip is where the user meets the cap, so it
        // is where the remedy belongs. Pinned as a string so a reword cannot silently drop it.
        Assert.Equal(
            "Live capture uses a faster model to keep up with realtime. For a session that matters, "
            + "re-transcribe it at higher accuracy afterwards (Sessions > Re-transcribe...).",
            RecordingConsoleViewModel.EngineTooltip);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~RecordingConsoleViewModelTests"`

Expected: FAIL to compile - `CS0117: 'RecordingConsoleViewModel' does not contain a definition for
'AccuracySuffix'`. Once the method exists,
`Preflight_and_engine_chip_populate_on_refresh_and_follow_the_picker` fails with
`Expected: base.en \u00B7 CPU \u00B7 Basic accuracy / Actual: base.en \u00B7 CPU`.

- [ ] **Step 3: Write the minimal implementation**

In `src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs`, replace the `EngineSummary` doc
comment at `:99-102`:

```csharp
    /// <summary>Ready-card engine chip (design 2026-07-13 section 5 item 4): the model+backend
    /// Start WOULD bind (settings + BackendSelector via SessionViewModel.PreviewEnginePlan), in the
    /// read-view footer's model-middledot-BACKEND shape, PLUS the catalog accuracy tier (Tier 1
    /// T1-6, spec 2026-08-05 :68-69). The owner ruling of 2026-08-05 froze the live model cap, so
    /// the divergence from import's large-v3-turbo default has to be DISCLOSED before recording
    /// begins - and this chip is the only always-visible Start-time surface. REJECTED: another
    /// Notice call, which ends in a tray balloon that Focus Assist suppresses and that
    /// BackendSelector only produces on the `auto` branch, never for an explicit pick.
    /// "" until the first refresh.</summary>
```

Add the pure helper directly below `PreflightLine`:

```csharp
    /// <summary>" \u00B7 Decent accuracy", or "" when the catalog does not know the model (Tier 1
    /// T1-6). Middle dot as a \u escape so this source file stays ASCII. Public static: tests drive
    /// it directly and it holds no console state - the RecordingConsoleViewModel.PreflightLine
    /// precedent (there is no InternalsVisibleTo in this repo).</summary>
    public static string AccuracySuffix(string modelName)
    {
        string tier = WhisperModelCatalog.AccuracyTier(modelName);
        return tier.Length == 0 ? "" : " \u00B7 " + tier;
    }
```

In `RefreshRemoteTargetsAsync` (`:274`), replace the assignment with:

```csharp
            EngineSummary = SessionViewModel.FormatEngineChip(plan) + AccuracySuffix(plan.ModelName);
```

Check the using block and add `using LocalScribe.Core.Transcription;` only if it is missing:

```bash
cd F:/LocalScribe && grep -n "using LocalScribe.Core.Transcription" src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs
```

- [ ] **Step 4: Point the chip at the remedy, not just the limitation**

The owner ruling's fourth bullet (spec `:73-74`) is that **"re-transcribe at higher accuracy" is the
documented follow-up path for a session that matters**, and nothing in this plan surfaced it. The
chip now discloses the cap in three places and, without this step, never tells the user what to do
about it - which is half the point of disclosing rather than removing.

Add the constant immediately above `AccuracySuffix`:

```csharp
    /// <summary>The ready-card engine chip's ToolTip (owner ruling 2026-08-05, spec :73-74). The cap
    /// is a deliberate realtime-factor decision, so the chip must name the REMEDY beside the
    /// limitation - versioned re-transcription already exists and is the documented path for a
    /// session that matters. REJECTED: another Notice, which ends in a tray balloon Focus Assist
    /// suppresses; and REJECTED: wording it in XAML, where no test can pin it and a reword would
    /// silently drop the remedy. ASCII only - "..." is three periods, not an ellipsis glyph.</summary>
    public const string EngineTooltip =
        "Live capture uses a faster model to keep up with realtime. For a session that matters, "
        + "re-transcribe it at higher accuracy afterwards (Sessions > Re-transcribe...).";
```

Then bind it in `src/LocalScribe.App/LiveViewWindow.xaml`, replacing the chip's `TextBlock` (`:60`,
inside the `Border` whose `DataTrigger` collapses it while `Console.EngineSummary` is `""`):

```xml
                    <TextBlock Text="{Binding Console.EngineSummary}" Style="{StaticResource MutedText}"
                               ToolTip="{Binding Console.EngineTooltipText}" />
```

and expose the constant as an INSTANCE property beside `EngineSummary` - a WPF `{Binding}` path
resolves instance members only, so a `public static string` here would bind to nothing and the
ToolTip would come up empty:

```csharp
    /// <summary>Binding surface for EngineTooltip. INSTANCE, not static: a WPF property path
    /// ("Console.EngineTooltipText") resolves instance members only. The XAML must not restate the
    /// sentence, or the test above stops pinning the text that actually ships.</summary>
    public string EngineTooltipText => EngineTooltip;
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~RecordingConsoleViewModelTests|FullyQualifiedName~SessionViewModelTests"`

Add `|FullyQualifiedName~XamlHygieneTests` to that filter as well - it walks the real XAML from the
repo root and is what catches a ToolTip bound to a property name the VM does not expose.

Expected: PASS.
`SessionViewModelTests.FormatEngineChip_backend_override_reflects_a_mid_session_floor_fall` must
still pass untouched - `FormatEngineChip` was not modified.

- [ ] **Step 6: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs src/LocalScribe.App/LiveViewWindow.xaml tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs
git commit -m "feat(console): ready-card engine chip names the accuracy tier and the re-transcribe remedy"
```

---

## Task 6: `AlignedAudioWriter` records the silence it fabricates

`AlignedAudioWriter.Write` zero-fills every clock gap (`:23-31`) and `PadToMs` appends zeros to the
session end (`:41-52`). It tracks only `SamplesWritten` - **nothing anywhere records WHERE**. A
SHA-256 that seals that FLAC without recording the fabricated ranges certifies machine-generated
silence as original recorded audio, which the spec (`:148-153`) calls worse than no hash at all.

**Files:**
- Create: `src/LocalScribe.Core/Model/SessionManifest.cs` (the `FabricatedSpan` half; Task 7 adds
  the rest of the file)
- Modify: `src/LocalScribe.Core/Live/AlignedAudioWriter.cs`
- Test: `tests/LocalScribe.Core.Tests/AlignedAudioWriterTests.cs` (append)

**Interfaces:**
- Consumes: `LocalScribe.Core.Audio.SourceKind` (`Local` / `Remote`), existing.
- Produces:
  - `LocalScribe.Core.Model.FabricatedSpan` - `sealed record` with
    `long StartSample`, `long EndSample`, `string Reason` (`{ get; init; }`).
  - `LocalScribe.Core.Model.FabricatedSilenceRecord(int SampleRate, IReadOnlyList<FabricatedSpan> Spans)` -
    positional `sealed record`.
  - `AlignedAudioWriter(IAudioFileSink sink, int sampleRate = 16000, SourceKind source = SourceKind.Local)`
    - the third parameter is TRAILING-OPTIONAL so the eleven existing `new AlignedAudioWriter(sink)`
    test sites keep compiling.
  - `AlignedAudioWriter.Source : SourceKind`, `AlignedAudioWriter.SampleRate : int`,
    `AlignedAudioWriter.FabricatedSilence : IReadOnlyList<FabricatedSpan>`.
  - Reason values, used verbatim by Task 8 and Task 12: `"clock-gap"` and `"end-pad"`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/AlignedAudioWriterTests.cs`, inside the class:

```csharp
    [Fact]
    public void One_clock_gap_is_recorded_as_one_coalesced_span_not_one_per_chunk()
    {
        // Tier 1 T1-7 (spec 2026-08-05 :148-153): a gap wider than the 1600-sample silence chunk is
        // filled by SEVERAL sink writes inside one while-loop. Recording per chunk would turn a
        // single 2-second dropout into 20 spans and make the manifest unreadable, so the span is
        // recorded once per Write() call, around the whole loop.
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink, 16000, SourceKind.Remote);

        w.Write(new AudioFrame(SourceKind.Remote, 2000, new float[1600]));   // 2 s gap, then 100 ms

        var span = Assert.Single(w.FabricatedSilence);
        Assert.Equal(0, span.StartSample);
        Assert.Equal(32000, span.EndSample);          // 2000 ms * 16000 / 1000
        Assert.Equal("clock-gap", span.Reason);
        Assert.Equal(SourceKind.Remote, w.Source);
        Assert.Equal(16000, w.SampleRate);
    }

    [Fact]
    public void The_end_pad_is_recorded_separately_from_a_mid_session_gap()
    {
        // A trailing pad and a mid-call dropout mean very different things to a reader, so the
        // Reason distinguishes them rather than merging both into one "silence" bucket.
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink, 16000);

        w.Write(new AudioFrame(SourceKind.Local, 0, new float[1600]));       // 0 - 100 ms, real
        w.Write(new AudioFrame(SourceKind.Local, 500, new float[1600]));     // 100 - 500 ms gap
        w.PadToMs(2000);                                                     // 600 - 2000 ms pad

        Assert.Equal(new[] { "clock-gap", "end-pad" }, w.FabricatedSilence.Select(s => s.Reason));
        Assert.Equal(1600, w.FabricatedSilence[0].StartSample);
        Assert.Equal(8000, w.FabricatedSilence[0].EndSample);
        Assert.Equal(9600, w.FabricatedSilence[1].StartSample);
        Assert.Equal(32000, w.FabricatedSilence[1].EndSample);
    }

    [Fact]
    public void A_gapless_session_records_no_fabricated_silence_at_all()
    {
        // The empty case has to be distinguishable from "not recorded" downstream: an empty list on
        // a leg the writer OWNED means the audio is entirely captured samples, which is the claim
        // the manifest makes when it says "no machine-generated silence".
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink, 16000);

        w.Write(new AudioFrame(SourceKind.Local, 0, new float[1600]));
        w.Write(new AudioFrame(SourceKind.Local, 100, new float[1600]));
        w.PadToMs(200);                                                      // already there: no-op

        Assert.Empty(w.FabricatedSilence);
    }
```

`CollectingSink` is the file's own private `IAudioFileSink` double (`AlignedAudioWriterTests.cs:9`)
and `AudioFrame` comes from the `using LocalScribe.Core.Audio;` already at the top. Do not add a
second sink double.

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~AlignedAudioWriterTests"`

Expected: FAIL to compile - `CS1061: 'AlignedAudioWriter' does not contain a definition for
'FabricatedSilence'` (and the same for `Source` / `SampleRate`), plus `CS1729` on the three-argument
constructor.

- [ ] **Step 3: Create the fabricated-silence records**

Create `src/LocalScribe.Core/Model/SessionManifest.cs` (Task 7 appends `ManifestFile` and
`SessionManifest` to this same file):

```csharp
namespace LocalScribe.Core.Model;

/// <summary>One run of MACHINE-GENERATED samples inside a retained audio leg (Tier 1 T1-7, spec
/// 2026-08-05 :148-153). AlignedAudioWriter zero-fills every clock gap and appends zeros to the
/// session end, and before this record nothing anywhere said where. A SHA-256 that seals the file
/// without this list certifies synthetic silence as original recorded audio - worse than no hash at
/// all, because it converts an absence of evidence into a false positive assertion.
/// Sample offsets, NOT milliseconds: the writer's arithmetic is exact in samples and a rounded ms
/// range would not identify the bytes it claims to describe. Divide by ManifestFile.SampleRate for
/// a readable time.</summary>
public sealed record FabricatedSpan
{
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    /// <summary>"clock-gap" (AlignedAudioWriter.Write filled a capture gap - a pause, a dropout or
    /// clock jitter) or "end-pad" (PadToMs appended zeros out to the stop instant so the file spans
    /// the whole session). A trailing pad and a mid-call dropout mean very different things to a
    /// reader, so they are never merged into one bucket.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>What ONE retained leg's writer fabricated, handed from SessionController to
/// ManifestBuilder at finalize (Tier 1 T1-7). Positional because it is a two-field carrier with no
/// serialization contract of its own - only its Spans reach manifest.json.</summary>
public sealed record FabricatedSilenceRecord(int SampleRate, IReadOnlyList<FabricatedSpan> Spans);
```

- [ ] **Step 4: Record the spans in the writer**

Replace `src/LocalScribe.Core/Live/AlignedAudioWriter.cs` in full:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Live;

/// <summary>Writes retained audio keeping the file sample-aligned to the session clock:
/// a frame stamped at StartMs always begins at sample StartMs * rate / 1000, with silence
/// padding for any gap (Pause stops capture but the clock keeps ticking - spec 2.1). This is
/// what lets Stage-5 diarisation seek the retained file by transcript startMs/endMs. Frames
/// arriving slightly early (ms-level capture jitter) are appended as-is; sub-frame drift is
/// accepted rather than resampled.
///
/// Tier 1 T1-7 (spec 2026-08-05 :148-153): every zero this class inserts is now RECORDED in
/// FabricatedSilence, because the integrity manifest hashes the resulting file and a hash that
/// seals fabricated silence as original audio is worse than no hash at all.</summary>
public sealed class AlignedAudioWriter : IDisposable
{
    private static readonly float[] SilenceChunk = new float[1600];   // 100 ms @ 16 kHz
    private readonly IAudioFileSink _sink;
    private readonly int _sampleRate;
    private readonly List<FabricatedSpan> _fabricated = new();

    public long SamplesWritten { get; private set; }

    /// <summary>Which leg this writer owns. Self-identifying so PersistFinalAsync can key the
    /// fabricated-silence map by source without depending on the order of Session.AudioWriters.</summary>
    public SourceKind Source { get; }

    public int SampleRate => _sampleRate;

    /// <summary>Every run of machine-generated samples in this file, in write order (Tier 1 T1-7).
    /// Appended only on a transition, never per silence chunk: one 2-second dropout is ONE span,
    /// not twenty. Both writers below sit on the synchronous capture path, so this must stay
    /// allocation-light - a gapless session never allocates a span at all.</summary>
    public IReadOnlyList<FabricatedSpan> FabricatedSilence => _fabricated;

    /// <summary>source is TRAILING-OPTIONAL (house idiom for adding a seam without touching
    /// existing call sites): eleven existing tests construct this with just a sink.</summary>
    public AlignedAudioWriter(IAudioFileSink sink, int sampleRate = 16000,
        SourceKind source = SourceKind.Local)
        => (_sink, _sampleRate, Source) = (sink, sampleRate, source);

    public void Write(AudioFrame frame)
    {
        long expectedStart = frame.StartMs * _sampleRate / 1000;
        long gap = expectedStart - SamplesWritten;
        if (gap > 0) Fill(gap, "clock-gap");
        _sink.Write(frame.Samples);
        SamplesWritten += frame.Samples.Length;
    }

    /// <summary>Stage 5.4 Phase 3 (write-side fix): pad the retained file with silence up to the
    /// session clock, so retained audio always spans the full session (observed: ~23.6 s audio vs
    /// 25.3 s session clock because the last frame precedes Stop). STRICTLY additive: appends zeros
    /// after the last recorded sample, never seeks, never rewrites; a target at or behind
    /// SamplesWritten is a no-op. Same ms-to-sample arithmetic as Write's expectedStart.</summary>
    public void PadToMs(long endMs)
    {
        long gap = endMs * _sampleRate / 1000 - SamplesWritten;
        if (gap > 0) Fill(gap, "end-pad");
    }

    /// <summary>Writes `samples` zeros in SilenceChunk-sized pieces and records the whole run as
    /// ONE span. Coalescing happens here, around the loop, rather than per chunk (Tier 1 T1-7).</summary>
    private void Fill(long samples, string reason)
    {
        long start = SamplesWritten;
        while (samples > 0)
        {
            int chunk = (int)Math.Min(samples, SilenceChunk.Length);
            _sink.Write(SilenceChunk.AsSpan(0, chunk));
            SamplesWritten += chunk;
            samples -= chunk;
        }
        _fabricated.Add(new FabricatedSpan
        { StartSample = start, EndSample = SamplesWritten, Reason = reason });
    }

    public void Dispose() => _sink.Dispose();
}
```

- [ ] **Step 5: Pass the source at both construction sites**

In `src/LocalScribe.Core/Live/SessionController.cs:555-560`, name each leg:

```csharp
                    localWriter = new AlignedAudioWriter(AudioSinkFactory.Create(
                        _paths.AudioFile(boot.Id, SourceKind.Local, settings.AudioFormat), settings.AudioFormat),
                        source: SourceKind.Local);
                    audioWriters.Add(localWriter);
                    remoteWriter = new AlignedAudioWriter(AudioSinkFactory.Create(
                        _paths.AudioFile(boot.Id, SourceKind.Remote, settings.AudioFormat), settings.AudioFormat),
                        source: SourceKind.Remote);
                    audioWriters.Add(remoteWriter);
```

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~AlignedAudioWriterTests|FullyQualifiedName~LiveSourcePipelineTests|FullyQualifiedName~SessionControllerTests"`

Expected: PASS. The existing `AlignedAudioWriterTests` alignment assertions must pass **untouched** -
`Fill` writes exactly the same bytes in exactly the same chunk sizes as the two former inline loops.
If a byte-level assertion fails, the refactor changed the write pattern; restore it.

- [ ] **Step 7: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Model/SessionManifest.cs src/LocalScribe.Core/Live/AlignedAudioWriter.cs src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/AlignedAudioWriterTests.cs
git commit -m "feat(audio): AlignedAudioWriter records every range of silence it fabricates"
```

---

## Task 7: `SessionManifest`, `ManifestStore` and the storage path

`src/LocalScribe.Core/Storage/` contains no hashing and no manifest of any kind. This task adds the
persisted shape and its store; Task 8 fills it.

**Files:**
- Modify: `src/LocalScribe.Core/Model/SessionManifest.cs` (append to Task 6's file)
- Create: `src/LocalScribe.Core/Storage/ManifestStore.cs`
- Modify: `src/LocalScribe.Core/Storage/StoragePaths.cs` (after the `TranscriptTxt(id, versionId)`
  getter at `:60`)
- Test: `tests/LocalScribe.Core.Tests/StoragePathsTests.cs` (append), and
  `tests/LocalScribe.Core.Tests/ManifestBuilderTests.cs` (create - store round-trip half)

**Interfaces:**
- Consumes: `FabricatedSpan` (Task 6); `JsonFile.WriteAsync<T>` / `SchemaGuard.ReadObjectAsync` /
  `SchemaGuard.ReadVersion` / `SchemaGuard.RejectIfNewer` (existing);
  `TranscriptVersions.Root` (existing, `LocalScribe.Core.Model`).
- Produces:
  - `LocalScribe.Core.Model.ManifestFile` - `{ string Name; string Sha256; long SizeBytes;
    DateTimeOffset ModifiedUtc; int SampleRate; bool FabricatedSilenceKnown;
    IReadOnlyList<FabricatedSpan> FabricatedSilence }`, all `{ get; init; }`.
  - `LocalScribe.Core.Model.SessionManifest` - `{ int SchemaVersion; string SessionId;
    string VersionId; DateTimeOffset WrittenAtUtc; IReadOnlyList<ManifestFile> Files }`.
  - `LocalScribe.Core.Storage.ManifestStore(string path)` with `public const int Version = 1`,
    `Task<SessionManifest?> ReadAsync(CancellationToken ct)` and
    `Task SaveAsync(SessionManifest manifest, CancellationToken ct)`.
  - `StoragePaths.ManifestJson(string id)` and `StoragePaths.ManifestJson(string id, string versionId)`.
  Tasks 8, 9, 10 and 12 all consume these exact names.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/StoragePathsTests.cs`, inside the class:

```csharp
    [Fact]
    public void Manifest_lives_beside_the_transcript_it_seals_in_every_version_layout()
    {
        // Tier 1 T1-7: the manifest seals a VERSION's transcript/edits/speakers, so it lives in
        // that version's folder. "v1" degenerates to the session root exactly like every other
        // version-aware getter, so a pre-versioning session needs no special case.
        var paths = new StoragePaths(@"C:\root");
        Assert.Equal(Path.Combine(paths.SessionDir("s1"), "manifest.json"), paths.ManifestJson("s1"));
        Assert.Equal(paths.ManifestJson("s1"), paths.ManifestJson("s1", TranscriptVersions.Root));
        Assert.Equal(Path.Combine(paths.VersionDir("s1", "v2-x"), "manifest.json"),
            paths.ManifestJson("s1", "v2-x"));
    }
```

Create `tests/LocalScribe.Core.Tests/ManifestBuilderTests.cs` with the store round-trip only (Task 8
appends the builder tests to this same file):

```csharp
using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

/// <summary>manifest.json: the integrity seal over one transcript version's evidentiary files
/// (Tier 1 T1-7, spec 2026-08-05 :146-153). Hashing happens ONCE at finalize; the export path only
/// ever reads the stored value, so the 2026-08-04 ruling against hashing recorded audio AT EXPORT
/// TIME stands untouched.</summary>
public sealed class ManifestBuilderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-manifest-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public ManifestBuilderTests() { _paths = new StoragePaths(_root); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Manifest_round_trips_and_carries_the_schema_stamp_on_disk()
    {
        var store = new ManifestStore(_paths.ManifestJson("s1"));
        var manifest = new SessionManifest
        {
            SessionId = "s1",
            WrittenAtUtc = new DateTimeOffset(2026, 8, 5, 10, 22, 0, TimeSpan.Zero),
            Files =
            [
                new ManifestFile
                {
                    Name = "local.flac", Sha256 = "abc123", SizeBytes = 4096,
                    ModifiedUtc = new DateTimeOffset(2026, 8, 5, 10, 21, 0, TimeSpan.Zero),
                    SampleRate = 16000, FabricatedSilenceKnown = true,
                    FabricatedSilence =
                        [new FabricatedSpan { StartSample = 0, EndSample = 32000, Reason = "clock-gap" }],
                },
            ],
        };

        await store.SaveAsync(manifest, CancellationToken.None);

        // Field-by-field, NEVER Assert.Equal over the whole SessionManifest. Both it and
        // ManifestFile carry IReadOnlyList members, and the compiler-generated record Equals
        // compares those with EqualityComparer<IReadOnlyList<T>>.Default - REFERENCE equality on
        // the backing list. An in-memory collection expression and a freshly deserialized List can
        // never be reference-equal, so a whole-record assertion here is unreachable by
        // construction. Assert.Equal over an IEnumerable IS element-wise, which is why the two
        // list comparisons below are safe (FabricatedSpan has no collection members of its own).
        var read = await store.ReadAsync(CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(ManifestStore.Version, read!.SchemaVersion);
        Assert.Equal("s1", read.SessionId);
        Assert.Equal(TranscriptVersions.Root, read.VersionId);
        Assert.Equal(manifest.WrittenAtUtc, read.WrittenAtUtc);
        var readFile = Assert.Single(read.Files);
        Assert.Equal("local.flac", readFile.Name);
        Assert.Equal("abc123", readFile.Sha256);
        Assert.Equal(4096, readFile.SizeBytes);
        Assert.Equal(manifest.Files[0].ModifiedUtc, readFile.ModifiedUtc);
        Assert.Equal(16000, readFile.SampleRate);
        Assert.True(readFile.FabricatedSilenceKnown);
        Assert.Equal(manifest.Files[0].FabricatedSilence, readFile.FabricatedSilence);

        var obj = JsonNode.Parse(File.ReadAllText(_paths.ManifestJson("s1")))!.AsObject();
        Assert.Equal(ManifestStore.Version, obj["schemaVersion"]!.GetValue<int>());
        Assert.Equal("v1", obj["versionId"]!.GetValue<string>());
        var file = obj["files"]!.AsArray()[0]!.AsObject();
        Assert.Equal("abc123", file["sha256"]!.GetValue<string>());
        Assert.Equal("clock-gap", file["fabricatedSilence"]!.AsArray()[0]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_absent_manifest_reads_as_null_and_a_newer_schema_is_rejected_not_mangled()
    {
        // Absent is the normal state for every session recorded before this feature - it must read
        // as "unsealed", never as a crash and never as an empty seal that would report every file
        // as verified.
        var store = new ManifestStore(_paths.ManifestJson("s-absent"));
        Assert.Null(await store.ReadAsync(CancellationToken.None));

        Directory.CreateDirectory(_paths.SessionDir("s-newer"));
        File.WriteAllText(_paths.ManifestJson("s-newer"), "{\"schemaVersion\": 99, \"files\": []}");
        await Assert.ThrowsAsync<NotSupportedException>(
            () => new ManifestStore(_paths.ManifestJson("s-newer")).ReadAsync(CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ManifestBuilderTests|FullyQualifiedName~Manifest_lives_beside_the_transcript"`

Expected: FAIL to compile - `CS0246: The type or namespace name 'ManifestStore' could not be found`
and `CS1061: 'StoragePaths' does not contain a definition for 'ManifestJson'`.

- [ ] **Step 3: Append the persisted shape**

Append to `src/LocalScribe.Core/Model/SessionManifest.cs`:

```csharp
/// <summary>One sealed file inside a session folder (Tier 1 T1-7, spec 2026-08-05 :146-153).
/// Size and mtime ride along beside the hash for two reasons: they make a CHANGED verdict cheap to
/// explain to a reader, and they are what lets ManifestBuilder carry a large FLAC's hash forward
/// across an overlay write instead of re-hashing gigabytes every time a correction is saved.</summary>
public sealed record ManifestFile
{
    /// <summary>Session-folder-relative, '/'-separated - the same naming SessionArchiver uses for
    /// zip entries, so "versions/v2-.../transcript.jsonl" reads identically in both artefacts.</summary>
    public string Name { get; init; } = "";
    /// <summary>Lowercase hex (Convert.ToHexStringLower), matching ImportedSourceInfo.Sha256's
    /// documented contract so the two hashes are comparable by eye.</summary>
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
    /// <summary>Retained audio legs only; 0 for text files. Divides FabricatedSilence's sample
    /// offsets into a readable time.</summary>
    public int SampleRate { get; init; }
    /// <summary>True only when the writer that PRODUCED this file reported its fabricated ranges
    /// (a live finalize), or when this entry was carried forward from such a write. False for
    /// imported audio, crash-recovered sessions and anything sealed by a build older than this
    /// feature. The distinction is the whole point: "no fabricated silence" and "we do not know"
    /// are different claims and an evidentiary artefact must never conflate them.</summary>
    public bool FabricatedSilenceKnown { get; init; }
    public IReadOnlyList<FabricatedSpan> FabricatedSilence { get; init; } = [];
}

/// <summary>manifest.json - the integrity seal over one transcript version's evidentiary files
/// (Tier 1 T1-7). Written atomically at finalize and refreshed after every overlay write and at
/// each new version. DERIVED in the sense that it can be recomputed, but never deleted as
/// housekeeping: its absence is what distinguishes an unsealed session from a tampered one.</summary>
public sealed record SessionManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = "";
    public string VersionId { get; init; } = TranscriptVersions.Root;
    public DateTimeOffset WrittenAtUtc { get; init; }
    public IReadOnlyList<ManifestFile> Files { get; init; } = [];
}
```

- [ ] **Step 4: Add the storage path**

In `src/LocalScribe.Core/Storage/StoragePaths.cs`, add directly after
`public string TranscriptTxt(string id, string versionId) => ...` (`:60`):

```csharp
    /// <summary>Integrity manifest (Tier 1 T1-7, spec 2026-08-05 :146-153): SHA-256 + size + mtime
    /// for this version's evidentiary files, plus the sample ranges AlignedAudioWriter fabricated.
    /// Version-aware like the transcript it seals - "v1" degenerates to the session root, so a
    /// pre-versioning session needs no special case. Rides into a .zip export automatically
    /// (SessionArchiver walks AllDirectories), which is deliberate: the seal must travel with the
    /// evidence.</summary>
    public string ManifestJson(string id) => Path.Combine(SessionDir(id), "manifest.json");
    public string ManifestJson(string id, string versionId)
        => Path.Combine(VersionDir(id, versionId), "manifest.json");
```

- [ ] **Step 5: Create `ManifestStore`**

Create `src/LocalScribe.Core/Storage/ManifestStore.cs`:

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>manifest.json per transcript version (Tier 1 T1-7). SessionStore's shape exactly:
/// JsonFile over LocalScribeJson (camelCase, indented, WhenWritingNull, UTC ISO-8601) with the
/// SchemaVersion stamped ON WRITE, and SchemaGuard rejecting a forward version rather than
/// silently mangling it. Writes go through AtomicFile, so a crash mid-refresh leaves the previous
/// seal intact rather than a truncated one.</summary>
public sealed class ManifestStore(string path)
{
    public const int Version = 1;

    /// <summary>Null when no manifest exists - the normal state for every session recorded before
    /// this feature, and a state the verifier reports as "not sealed" rather than as a pass.</summary>
    public async Task<SessionManifest?> ReadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(path, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "manifest.json");
        return await JsonFile.ReadAsync<SessionManifest>(path, ct);
    }

    public Task SaveAsync(SessionManifest manifest, CancellationToken ct)
        => JsonFile.WriteAsync(path, manifest with { SchemaVersion = Version }, ct);
}
```

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ManifestBuilderTests|FullyQualifiedName~StoragePathsTests"`

Expected: PASS.

- [ ] **Step 7: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Model/SessionManifest.cs src/LocalScribe.Core/Storage/ManifestStore.cs src/LocalScribe.Core/Storage/StoragePaths.cs tests/LocalScribe.Core.Tests/ManifestBuilderTests.cs tests/LocalScribe.Core.Tests/StoragePathsTests.cs
git commit -m "feat(storage): manifest.json shape, store and version-aware path"
```

---

## Task 8: `ManifestBuilder` - streaming SHA-256 with carry-forward

The only streaming SHA-256 in the solution is `AudioImporter.CopyWithSha256Async`
(`AudioImporter.cs:263-276`), which uses `IncrementalHash` with a 64 KiB buffer and
`Convert.ToHexStringLower`. This task copies that idiom for hash-only use, with two changes it must
make: `FileShare.ReadWrite | FileShare.Delete` (the importer's `FileShare.Read` locks out writers -
a defect already fixed twice in this repo), and audio **carry-forward** so a correction save does not
re-hash a multi-GB FLAC.

**Files:**
- Create: `src/LocalScribe.Core/Storage/ManifestBuilder.cs`
- Test: `tests/LocalScribe.Core.Tests/ManifestBuilderTests.cs` (append to Task 7's file)

**Interfaces:**
- Consumes: `SessionManifest`, `ManifestFile`, `FabricatedSpan`, `FabricatedSilenceRecord` (Tasks 6-7);
  `ManifestStore` (Task 7); `StoragePaths.ManifestJson/SessionJson/MetaJson/TranscriptJsonl/
  EditsJson/SpeakersJson/AudioFile/VersionDir/SessionDir` (existing);
  `LocalScribe.Core.Audio.SourceKind` and `LocalScribe.Core.Audio.AudioFormat` (existing).
- Produces:
  `ManifestBuilder.BuildAsync(StoragePaths paths, string sessionId, string versionId,
  DateTimeOffset nowUtc, IReadOnlyDictionary<SourceKind, FabricatedSilenceRecord>? fabricated,
  bool sealAudio, CancellationToken ct) : Task<SessionManifest>` and
  `ManifestBuilder.WriteAsync(...)` with the same parameters returning `Task`. `sealAudio` is
  REQUIRED, not defaulted: it decides whether a leg that has never been hashed gets hashed now, and
  a silent default would let the launch-time recovery scan hash the whole library (Step 3's cost
  gate). Task 9 calls `WriteAsync`; Task 10 reads the result through `ManifestStore`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/ManifestBuilderTests.cs`, inside the class:

```csharp
    /// <summary>A minimal on-disk session: the five text files the manifest seals plus one "audio"
    /// leg. The leg is not real FLAC - the builder only ever hashes BYTES, so any file with the
    /// right name exercises the same path and keeps the fixture fast.</summary>
    private void Seed(string id, string localAudio = "AAAA")
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        File.WriteAllText(_paths.SessionJson(id), "{\"schemaVersion\":4,\"id\":\"" + id + "\"}");
        File.WriteAllText(_paths.MetaJson(id), "{\"schemaVersion\":3}");
        File.WriteAllText(_paths.TranscriptJsonl(id), "{\"seq\":0}\n");
        File.WriteAllText(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), localAudio);
    }

    [Fact]
    public async Task Seals_every_file_present_and_skips_the_ones_that_are_not()
    {
        // edits.json and speakers.json are absent-until-used, and an absent file must NOT appear as
        // an entry - a manifest naming a file that never existed would report MISSING forever.
        Seed("s1");

        var manifest = await ManifestBuilder.BuildAsync(_paths, "s1", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: true, CancellationToken.None);

        Assert.Equal(new[] { "local.flac", "meta.json", "session.json", "transcript.jsonl" },
            manifest.Files.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
        var transcript = manifest.Files.Single(f => f.Name == "transcript.jsonl");
        Assert.Equal(64, transcript.Sha256.Length);                        // 32 bytes, lowercase hex
        Assert.Equal(transcript.Sha256, transcript.Sha256.ToLowerInvariant());
        Assert.Equal(new FileInfo(_paths.TranscriptJsonl("s1")).Length, transcript.SizeBytes);
    }

    [Fact]
    public async Task The_fabricated_ranges_the_writer_reported_are_sealed_with_the_audio()
    {
        // Tier 1 T1-7 (spec 2026-08-05 :148-153): the whole point. A hash over local.flac without
        // this list would certify machine-generated zeros as original recorded audio.
        Seed("s2");
        var spans = new[] { new FabricatedSpan { StartSample = 0, EndSample = 32000, Reason = "clock-gap" } };

        var manifest = await ManifestBuilder.BuildAsync(_paths, "s2", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero),
            new Dictionary<SourceKind, FabricatedSilenceRecord>
            { [SourceKind.Local] = new(16000, spans) },
            sealAudio: true, CancellationToken.None);

        var leg = manifest.Files.Single(f => f.Name == "local.flac");
        Assert.True(leg.FabricatedSilenceKnown);
        Assert.Equal(16000, leg.SampleRate);
        Assert.Equal(spans, leg.FabricatedSilence);
    }

    [Fact]
    public async Task Audio_with_no_reported_ranges_is_sealed_as_UNKNOWN_not_as_clean()
    {
        // An imported or crash-recovered leg has no writer to report ranges. Recording it as an
        // empty list would be an assertion nobody made; FabricatedSilenceKnown=false says so.
        Seed("s3");

        var manifest = await ManifestBuilder.BuildAsync(_paths, "s3", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: true, CancellationToken.None);

        var leg = manifest.Files.Single(f => f.Name == "local.flac");
        Assert.False(leg.FabricatedSilenceKnown);
        Assert.Empty(leg.FabricatedSilence);
    }

    [Fact]
    public async Task A_regenerate_over_an_unsealed_session_seals_the_text_and_never_opens_the_audio()
    {
        // Tier 1 T1-7 cost gate. RegenerateProjectionsAsync is reached from the LAUNCH-TIME
        // recovery scan (SessionWriter.RecoverIfNeededAsync, run by StartupOrchestrator) and from
        // "Regenerate all" (MaintenanceService.cs:962). Without the gate, the first run after this
        // ships would stream a SHA-256 over every retained leg in the library - an unbounded,
        // un-cancellable, unconsented multi-hour read that the spec (:146-147) never asked for; it
        // asks for a seal at FINALIZE, refreshed after overlay writes.
        // The proof is mechanical: the leg is held open with FileShare.None, so ANY attempt by the
        // builder to read it would throw IOException rather than quietly succeed.
        Seed("s6");
        using (var _ = new FileStream(_paths.AudioFile("s6", SourceKind.Local, AudioFormat.Flac),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var manifest = await ManifestBuilder.BuildAsync(_paths, "s6", TranscriptVersions.Root,
                new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero), fabricated: null,
                sealAudio: false, CancellationToken.None);

            Assert.Equal(new[] { "meta.json", "session.json", "transcript.jsonl" },
                manifest.Files.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task A_refresh_carries_the_audio_hash_and_its_ranges_forward_unchanged()
    {
        // This is what keeps the 2026-08-04 no-hashing-at-export ruling honoured in spirit: an
        // overlay write must not re-hash gigabytes of FLAC, and it must not LOSE the fabricated
        // ranges either. Same size + same mtime => the bytes did not move, so reuse the entry.
        Seed("s4");
        await ManifestBuilder.WriteAsync(_paths, "s4", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero),
            new Dictionary<SourceKind, FabricatedSilenceRecord>
            {
                [SourceKind.Local] = new(16000,
                    [new FabricatedSpan { StartSample = 0, EndSample = 32000, Reason = "end-pad" }]),
            },
            sealAudio: true, CancellationToken.None);
        var first = (await new ManifestStore(_paths.ManifestJson("s4")).ReadAsync(CancellationToken.None))!;

        // A later overlay write: edits.json appears, nothing else moves, and NO fabricated map.
        File.WriteAllText(_paths.EditsJson("s4"), "{\"schemaVersion\":1}");
        await ManifestBuilder.WriteAsync(_paths, "s4", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 11, 0, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: false, CancellationToken.None);
        var second = (await new ManifestStore(_paths.ManifestJson("s4")).ReadAsync(CancellationToken.None))!;

        // Field-by-field, NOT Assert.Equal over the two ManifestFile records: ManifestFile carries
        // an IReadOnlyList<FabricatedSpan>, and the compiler-generated Equals compares that with
        // EqualityComparer<IReadOnlyList<T>>.Default - i.e. REFERENCE equality on the backing list.
        // Both sides here are separate deserializations, so a whole-record assertion could never
        // pass. Assert.Equal over the two lists IS element-wise, so the ranges are still compared.
        var a = first.Files.Single(f => f.Name == "local.flac");
        var b = second.Files.Single(f => f.Name == "local.flac");
        Assert.Equal(a.Sha256, b.Sha256);                                // never re-hashed
        Assert.Equal(a.SizeBytes, b.SizeBytes);
        Assert.Equal(a.ModifiedUtc, b.ModifiedUtc);
        Assert.Equal(a.SampleRate, b.SampleRate);
        Assert.True(b.FabricatedSilenceKnown);
        Assert.Equal(a.FabricatedSilence, b.FabricatedSilence);          // the ranges survived
        Assert.Contains(second.Files, f => f.Name == "edits.json");      // the new overlay is sealed
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 11, 0, 0, TimeSpan.Zero), second.WrittenAtUtc);
    }

    [Fact]
    public async Task Audio_that_actually_changed_on_disk_is_re_hashed()
    {
        // Carry-forward is keyed on size + mtime, never on presence alone: a re-transcription or a
        // repaired leg must produce a NEW hash, or the seal would certify bytes it never read.
        // The second write passes sealAudio:false ON PURPOSE - the cost gate must not suppress a
        // change to a leg that was ALREADY sealed, only the first hash of one that never was.
        Seed("s5", localAudio: "AAAA");
        await ManifestBuilder.WriteAsync(_paths, "s5", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: true, CancellationToken.None);
        var before = (await new ManifestStore(_paths.ManifestJson("s5")).ReadAsync(CancellationToken.None))!
            .Files.Single(f => f.Name == "local.flac");

        File.WriteAllText(_paths.AudioFile("s5", SourceKind.Local, AudioFormat.Flac), "BBBBBBBB");
        await ManifestBuilder.WriteAsync(_paths, "s5", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 11, 0, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: false, CancellationToken.None);
        var after = (await new ManifestStore(_paths.ManifestJson("s5")).ReadAsync(CancellationToken.None))!
            .Files.Single(f => f.Name == "local.flac");

        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.Equal(8, after.SizeBytes);
    }
```

Add `using LocalScribe.Core.Audio;` to the top of the file (for `SourceKind`/`AudioFormat`).

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ManifestBuilderTests"`

Expected: FAIL to compile - `CS0103: The name 'ManifestBuilder' does not exist in the current
context`.

- [ ] **Step 3: Write the builder**

Create `src/LocalScribe.Core/Storage/ManifestBuilder.cs`:

```csharp
using System.Security.Cryptography;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Builds manifest.json for one transcript version (Tier 1 T1-7, spec 2026-08-05
/// :146-153): SHA-256 + size + mtime for session.json, meta.json and the version's
/// transcript.jsonl / edits.json / speakers.json, plus every retained audio leg on disk and the
/// sample ranges AlignedAudioWriter fabricated inside it.
///
/// This does NOT re-open the 2026-08-04 ruling that recorded audio is never hashed AT EXPORT TIME
/// (transcript-export-scope-dialog-design :78). Audio is hashed at FINALIZE, once; every later
/// refresh carries the value forward on a size+mtime match, and the export path (Task 12) only
/// reads the stored number. A reviewer seeing "SHA-256 over a FLAC" here should read this
/// paragraph before flagging it.
///
/// COST RULING (Tier 1 T1-7): the first hash of a leg that has never been sealed happens only when
/// the caller passes sealAudio:true - the live finalize. Every other caller (the launch-time
/// recovery scan, "Regenerate all", every overlay write) passes false, so opening the app after
/// this ships does NOT retro-hash the library. The spec (:146-147) asks for a seal at finalize
/// refreshed after overlay writes; it never asked for a retroactive whole-library hash, and such a
/// hash would be unbounded, un-cancellable and unconsented.</summary>
public static class ManifestBuilder
{
    /// <summary>Compose the manifest without writing it. nowUtc comes from the caller's injected
    /// TimeProvider - never DateTime.UtcNow. <paramref name="sealAudio"/> is the cost gate above:
    /// REQUIRED rather than defaulted, because a silent default is exactly how the recovery scan
    /// would end up hashing gigabytes nobody asked it to.</summary>
    public static async Task<SessionManifest> BuildAsync(StoragePaths paths, string sessionId,
        string versionId, DateTimeOffset nowUtc,
        IReadOnlyDictionary<SourceKind, FabricatedSilenceRecord>? fabricated, bool sealAudio,
        CancellationToken ct)
    {
        string sessionDir = paths.SessionDir(sessionId);
        var previous = await new ManifestStore(paths.ManifestJson(sessionId, versionId)).ReadAsync(ct);
        var previousByName = previous is null
            ? new Dictionary<string, ManifestFile>(StringComparer.Ordinal)
            : previous.Files.ToDictionary(f => f.Name, StringComparer.Ordinal);

        // Audio is SESSION-level: local.flac is the same bytes whichever version is being sealed.
        // A version created by re-transcription starts with no manifest of its own, so it INHERITS
        // the session-root seal's audio entry rather than re-hashing - REJECTED: hashing per
        // version, which multiplies the one affordable hash by the version count for zero new
        // information, and would leave a v2 export with no audio hashes at all under the cost gate.
        var rootByName = previousByName;
        if (versionId != TranscriptVersions.Root)
        {
            var root = await new ManifestStore(paths.ManifestJson(sessionId)).ReadAsync(ct);
            rootByName = root is null
                ? new Dictionary<string, ManifestFile>(StringComparer.Ordinal)
                : root.Files.ToDictionary(f => f.Name, StringComparer.Ordinal);
        }

        var files = new List<ManifestFile>();

        // Text truth: always re-hashed. These are kilobytes, and an overlay write is exactly the
        // event a stale hash would hide.
        foreach (string path in new[]
                 {
                     paths.SessionJson(sessionId), paths.MetaJson(sessionId),
                     paths.TranscriptJsonl(sessionId, versionId),
                     paths.EditsJson(sessionId, versionId),
                     paths.SpeakersJson(sessionId, versionId),
                 })
        {
            if (!File.Exists(path)) continue;   // edits/speakers are absent-until-used
            files.Add(await SealAsync(sessionDir, path, ct));
        }

        // Retained audio: considered whenever the FILE EXISTS, deliberately NOT gated on
        // SessionRecord.RetainedAudioSources. That list is empty on every crash-recovered session
        // (SessionWriter.RecoverIfNeededAsync never writes it), and a leg on disk that no manifest
        // mentions is precisely the gap this feature closes. Whether it is HASHED is a separate
        // question - see the cost gate below.
        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
            foreach (var format in new[] { AudioFormat.Flac, AudioFormat.Wav })
            {
                string path = paths.AudioFile(sessionId, kind, format);
                if (!File.Exists(path)) continue;
                string name = Relative(sessionDir, path);
                var info = new FileInfo(path);
                if (!previousByName.TryGetValue(name, out var prior)) rootByName.TryGetValue(name, out prior);

                // Carry-forward: same size AND same mtime means the bytes did not move, so reuse
                // the whole entry - hash and fabricated ranges together. Re-hashing a multi-GB FLAC
                // on every saved correction is what makes a per-overlay refresh affordable at all.
                bool unchanged = prior is not null
                    && prior.SizeBytes == info.Length
                    && prior.ModifiedUtc == new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

                var silence = fabricated is not null && fabricated.TryGetValue(kind, out var rec)
                    ? rec
                    : null;

                // The cost gate (see the class doc). It bites ONLY on a leg that has never been
                // sealed: a leg whose seal exists but whose bytes MOVED is always re-hashed, because
                // that is precisely the event a seal exists to catch and it is rare. REJECTED:
                // sealing an unhashed leg with an empty or inherited hash, which would certify bytes
                // nobody read - the file is simply left out, and Verify integrity then makes no
                // claim about it rather than a false one.
                if (!unchanged && prior is null && !sealAudio) continue;

                if (unchanged && silence is null) { files.Add(prior!); continue; }

                var sealedFile = unchanged
                    ? prior! with { }                                  // reuse the hash we already have
                    : await SealAsync(sessionDir, path, ct);
                files.Add(silence is not null
                    ? sealedFile with
                    {
                        SampleRate = silence.SampleRate,
                        FabricatedSilenceKnown = true,
                        FabricatedSilence = silence.Spans,
                    }
                    // No writer reported ranges for this leg: carry the prior claim if there was
                    // one, otherwise say UNKNOWN. Never fabricate an empty list, which would read
                    // as "we checked and there is none".
                    : sealedFile with
                    {
                        SampleRate = prior?.SampleRate ?? 0,
                        FabricatedSilenceKnown = prior?.FabricatedSilenceKnown ?? false,
                        FabricatedSilence = prior?.FabricatedSilence ?? [],
                    });
            }

        return new SessionManifest
        {
            SessionId = sessionId,
            VersionId = versionId,
            WrittenAtUtc = nowUtc,
            Files = files.OrderBy(f => f.Name, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>Build and persist atomically. Never throws for a missing session folder - a
    /// manifest over nothing is simply an empty Files list.</summary>
    public static async Task WriteAsync(StoragePaths paths, string sessionId, string versionId,
        DateTimeOffset nowUtc, IReadOnlyDictionary<SourceKind, FabricatedSilenceRecord>? fabricated,
        bool sealAudio, CancellationToken ct)
    {
        var manifest = await BuildAsync(paths, sessionId, versionId, nowUtc, fabricated, sealAudio, ct);
        await new ManifestStore(paths.ManifestJson(sessionId, versionId)).SaveAsync(manifest, ct);
    }

    private static async Task<ManifestFile> SealAsync(string sessionDir, string path,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        return new ManifestFile
        {
            Name = Relative(sessionDir, path),
            Sha256 = await Sha256Async(path, ct),
            SizeBytes = info.Length,
            ModifiedUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
        };
    }

    /// <summary>'/'-separated session-folder-relative name, matching SessionArchiver's zip entries
    /// so "versions/v2-.../transcript.jsonl" reads identically in both artefacts.</summary>
    private static string Relative(string sessionDir, string path)
        => Path.GetRelativePath(sessionDir, path).Replace('\\', '/');

    /// <summary>Streaming SHA-256, the AudioImporter.CopyWithSha256Async idiom (:263-276) with the
    /// copy half dropped - lowercase hex via Convert.ToHexStringLower, 64 KiB buffer, so a multi-GB
    /// FLAC never lands in memory. FileShare.ReadWrite | Delete, NOT FileShare.Read: the importer's
    /// share mode is safe only because it reads a user file no LocalScribe process holds, whereas
    /// this reads inside a session folder whose capture pipeline may still hold local.flac and
    /// transcript.jsonl open for WRITING. That exact defect has been fixed twice in this repo
    /// (SessionArchiver.cs:34-43); Delete additionally tolerates an AtomicFile replace mid-read.</summary>
    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var src = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 16, useAsync: true);
        var buf = new byte[1 << 16];
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0) sha.AppendData(buf, 0, n);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ManifestBuilderTests"`

Expected: PASS, all eight (the two `ManifestStore` round-trip facts from Task 7 plus the six added
here). If `A_refresh_carries_the_audio_hash_and_its_ranges_forward_unchanged` fails on an mtime
mismatch, the fixture rewrote the audio file - it must not; only `edits.json` is touched between the
two writes. If
`A_regenerate_over_an_unsealed_session_seals_the_text_and_never_opens_the_audio` fails with an
`IOException` mentioning `local.flac`, the cost gate is not in place and the builder opened a leg it
had no prior seal for.

- [ ] **Step 5: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Storage/ManifestBuilder.cs tests/LocalScribe.Core.Tests/ManifestBuilderTests.cs
git commit -m "feat(storage): ManifestBuilder - streaming SHA-256 with audio carry-forward"
```

---

## Task 9: write the manifest at finalize and refresh it on every regenerate

`SessionWriter.RegenerateProjectionsAsync` is the method almost every mutation path already calls -
seventeen call sites covering finalize, recovery, import, re-transcription, corrections, splits,
speaker pins, renames and diarisation. Hooking the refresh there covers all seventeen at once.

**It is NOT, however, "true by construction", and this task must not claim that it is.** Two writers
deliberately skip that choke point, and both mutate a file the manifest seals:

- `MaintenanceService.SetActiveVersionCoreAsync` (`:166-178`) rewrites **session.json** and
  documents why it does not regen ("No projection regen: each version keeps its own rendered
  files"). Left alone, every version switch would make Verify integrity report `session.json
  CHANGED` on a completely untampered session.
- `MaintenanceService.PurgeAllVoiceprintsAsync` (`:762-770`) rewrites **speakers.json** for every
  version of every session in the library and never regenerates.

There is a second, structural half. session.json and meta.json are **session-level** files sealed
into a **per-version** manifest, and a regenerate only knows about `loaded.VersionId`. So a v1
manifest would go stale the moment any v2-era meta edit landed, and verifying v1 would report
`meta.json CHANGED`. The fix for both halves is one method: a reseal that rewrites **every** version's
manifest and nothing else, called from the regenerate choke point AND from the two writers above.

A false tamper verdict is the one outcome `IntegrityReport`'s own doc says this command must never
produce, so this is not a tidiness point.

**Files:**
- Modify: `src/LocalScribe.Core/Storage/SessionWriter.cs:1-33`
- Modify: `src/LocalScribe.Core/Live/SessionController.cs:1259-1280` (`PersistFinalAsync`)
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs:166-178` (`SetActiveVersionCoreAsync`)
  and `:762-770` (the version loop inside `PurgeAllVoiceprintsAsync`)
- Test: `tests/LocalScribe.Core.Tests/SessionControllerTests.cs` (append),
  `tests/LocalScribe.Core.Tests/SessionWriterTests.cs` (append),
  `tests/LocalScribe.App.Tests/MaintenanceServiceVersionsTests.cs` (append)

**Interfaces:**
- Consumes: `ManifestBuilder.WriteAsync(...)` and `ManifestStore` (Tasks 7-8);
  `AlignedAudioWriter.Source` / `.SampleRate` / `.FabricatedSilence` (Task 6);
  `SessionRecord.Versions` / `TranscriptVersion.Id` and `TranscriptVersions.Root` (existing).
- Produces:
  - `SessionWriter.RegenerateProjectionsAsync(string sessionId, CancellationToken ct,
    IReadOnlyDictionary<SourceKind, FabricatedSilenceRecord>? fabricated = null,
    bool sealAudio = false) : Task` - both new parameters are TRAILING-OPTIONAL, so all seventeen
    existing call sites keep compiling and keep carrying the ranges forward. Only `PersistFinalAsync`
    passes them.
  - `SessionWriter.ResealAsync(string sessionId, SessionRecord session, CancellationToken ct,
    IReadOnlyDictionary<SourceKind, FabricatedSilenceRecord>? fabricated = null,
    bool sealAudio = false) : Task` - rewrites every version's manifest.json and touches no other
    file. Task 9's two `MaintenanceService` sites call it; nothing else does.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/SessionControllerTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task Finalize_seals_the_session_folder_and_records_the_fabricated_silence()
    {
        // Tier 1 T1-7 (spec 2026-08-05 :146-153). The FakeProvider's frames leave clock gaps, so
        // this also proves the writer's ranges reach the manifest through PersistFinalAsync rather
        // than being computed (impossibly) from the finished file.
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root);

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 5000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;

        var manifest = await new ManifestStore(paths.ManifestJson(id!)).ReadAsync(CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal(id, manifest!.SessionId);
        Assert.Contains(manifest.Files, f => f.Name == "session.json" && f.Sha256.Length == 64);
        Assert.Contains(manifest.Files, f => f.Name == "transcript.jsonl" && f.Sha256.Length == 64);

        var local = manifest.Files.Single(f => f.Name == "local.flac");
        // PadToMs(5000) always runs on the clean Stop path, so a retained leg ALWAYS carries at
        // least the end-pad range - and it is always KNOWN, because the writer reported it.
        Assert.True(local.FabricatedSilenceKnown);
        Assert.Equal(16000, local.SampleRate);
        Assert.Contains(local.FabricatedSilence, s => s.Reason == "end-pad");
    }
```

Append to `tests/LocalScribe.Core.Tests/SessionWriterTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task Regenerating_projections_refreshes_the_manifest()
    {
        // The refresh lives inside RegenerateProjectionsAsync deliberately: it is the ONE method
        // every overlay write, recovery, import and re-transcription already calls, so a future
        // overlay writer cannot forget to reseal. REJECTED: hooking each of MaintenanceService's
        // seven overlay methods individually - seventeen call sites, seventeen chances to miss one.
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: T0.AddMinutes(1));
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));

            await writer.RegenerateProjectionsAsync("s1", default);
            var first = await new ManifestStore(paths.ManifestJson("s1")).ReadAsync(default);
            Assert.NotNull(first);
            Assert.Equal(T0, first!.WrittenAtUtc);
            string transcriptHash = first.Files.Single(f => f.Name == "transcript.jsonl").Sha256;

            // An overlay write lands, then the regen that every such write already performs.
            await new TranscriptStore(paths.TranscriptJsonl("s1")).AppendAsync(
                TranscriptLine.Segment(2, TranscriptSource.Local, 2000, 3000, "More.", "Me"), default);
            await writer.RegenerateProjectionsAsync("s1", default);

            var second = await new ManifestStore(paths.ManifestJson("s1")).ReadAsync(default);
            Assert.NotEqual(transcriptHash,
                second!.Files.Single(f => f.Name == "transcript.jsonl").Sha256);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_reseal_rewrites_EVERY_versions_manifest_not_just_the_active_one()
    {
        // Tier 1 T1-7. session.json and meta.json are SESSION-level but are sealed into a
        // PER-VERSION manifest, and a regenerate only knows loaded.VersionId. A seal that covered
        // the ACTIVE version alone would go stale for every other one the instant session.json
        // changed - and a version switch changes session.json with NO projection regen by design
        // (MaintenanceService.cs:166-178). Verifying v1 would then report session.json CHANGED on a
        // completely untampered session, which is the one verdict this feature must never invent.
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        const string vid = "v2-tiny.en-2026-08-05";
        try
        {
            await SeedAsync(paths, "s2", endedAtUtc: T0.AddMinutes(1));
            Directory.CreateDirectory(paths.VersionDir("s2", vid));
            await new TranscriptStore(paths.TranscriptJsonl("s2", vid)).AppendAsync(
                TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "V2 words.", "Me"), default);
            var store = new SessionStore(paths.SessionJson("s2"));
            var session = (await store.ReadAsync(default))! with
            {
                Versions = new[]
                {
                    new TranscriptVersion { Id = vid, Model = "tiny.en", Backend = "CPU", Language = "en" },
                },
            };
            await store.SaveAsync(session, default);

            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            await writer.ResealAsync("s2", session, default);
            string before = (await new ManifestStore(paths.ManifestJson("s2")).ReadAsync(default))!
                .Files.Single(f => f.Name == "session.json").Sha256;

            // The version switch: session.json changes, nothing regenerates, then the reseal.
            var switched = session with { ActiveVersion = vid };
            await store.SaveAsync(switched, default);
            await writer.ResealAsync("s2", switched, default);

            string root1 = (await new ManifestStore(paths.ManifestJson("s2")).ReadAsync(default))!
                .Files.Single(f => f.Name == "session.json").Sha256;
            string root2 = (await new ManifestStore(paths.ManifestJson("s2", vid)).ReadAsync(default))!
                .Files.Single(f => f.Name == "session.json").Sha256;
            Assert.NotEqual(before, root1);          // v1's manifest tracked the rewrite
            Assert.Equal(root1, root2);              // and so did v2's - one file, one hash
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
```

Append to `tests/LocalScribe.App.Tests/MaintenanceServiceVersionsTests.cs`, inside the class - this
is the one that proves the App-layer call site was actually wired:

```csharp
    [Fact]
    public async Task Switching_the_active_version_reseals_instead_of_stranding_the_manifest()
    {
        // MaintenanceService.SetActiveVersionCoreAsync writes session.json and deliberately skips
        // the projection regen, so it is the ONE mutation the Tier 1 T1-7 choke point does not
        // cover. Without an explicit reseal, "Verify integrity" reports session.json CHANGED on a
        // session nobody touched - a false tamper verdict.
        string id = await SeedVersionedAsync();
        var svc = MakeService();
        await new SessionWriter(_paths, new Settings(), TimeProvider.System).ResealAsync(
            id, (await new SessionStore(_paths.SessionJson(id)).ReadAsync(default))!, default);
        string before = (await new ManifestStore(_paths.ManifestJson(id)).ReadAsync(default))!
            .Files.Single(f => f.Name == "session.json").Sha256;

        Assert.True(await svc.SetActiveVersionAsync(id, "v1", CancellationToken.None));

        string after = (await new ManifestStore(_paths.ManifestJson(id)).ReadAsync(default))!
            .Files.Single(f => f.Name == "session.json").Sha256;
        Assert.NotEqual(before, after);
        Assert.Equal(after, (await new ManifestStore(_paths.ManifestJson(id, Vid)).ReadAsync(default))!
            .Files.Single(f => f.Name == "session.json").Sha256);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~SessionWriterTests|FullyQualifiedName~Finalize_seals_the_session_folder|FullyQualifiedName~Switching_the_active_version_reseals"`

Expected: FAIL to compile first - `CS1061: 'SessionWriter' does not contain a definition for
'ResealAsync'`. Once `ResealAsync` exists but nothing calls it,
`Regenerating_projections_refreshes_the_manifest` and `Finalize_seals_the_session_folder` fail with
`Assert.NotNull() Failure: Value is null` (nothing writes `manifest.json` yet), and
`Switching_the_active_version_reseals_instead_of_stranding_the_manifest` fails on
`Assert.NotEqual()` because the switch left the manifest untouched.

- [ ] **Step 3: Refresh the manifest from the regenerate choke point**

Replace `src/LocalScribe.Core/Storage/SessionWriter.cs:1-33` (the usings, class doc and
`RegenerateProjectionsAsync`; `RecoverIfNeededAsync` below is untouched):

```csharp
// src/LocalScribe.Core/Storage/SessionWriter.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Storage;

/// <summary>Regenerates the readable projections (transcript.md/.txt, session.txt) from the JSON
/// truth, and performs per-session crash recovery (spec section 2.1/section 6/Storage format). Pure orchestration
/// over the stores + projection; the launch-time recovery scan is wired in a later stage.</summary>
public sealed class SessionWriter
{
    private readonly StoragePaths _paths;
    private readonly Settings _settings;
    private readonly TimeProvider _time;

    public SessionWriter(StoragePaths paths, Settings settings, TimeProvider time)
        => (_paths, _settings, _time) = (paths, settings, time);

    /// <summary><paramref name="fabricated"/> (Tier 1 T1-7, spec 2026-08-05 :146-153) is the
    /// per-leg record of the silence AlignedAudioWriter inserted, and ONLY the live finalize path
    /// has it. <paramref name="sealAudio"/> is ManifestBuilder's cost gate - also finalize-only,
    /// because the launch-time recovery scan and "Regenerate all" both land here and neither may
    /// hash the library's audio. Both are trailing-optional so the seventeen existing call sites
    /// keep compiling: they pass null/false and ManifestBuilder carries the previously recorded
    /// hashes and ranges forward, which is correct - an overlay write does not change what the
    /// capture pipeline fabricated, or what its bytes hash to.</summary>
    public async Task RegenerateProjectionsAsync(string sessionId, CancellationToken ct,
        IReadOnlyDictionary<SourceKind, FabricatedSilenceRecord>? fabricated = null,
        bool sealAudio = false)
    {
        var loaded = await SessionProjectionLoader.LoadAsync(_paths, _settings, _time, sessionId, ct: ct);
        // Versioned sessions (design 2026-07-13 section 3.1): the transcript projections land
        // INSIDE the active version's folder ("v1" resolves to the session root, preserving the
        // pre-versioning layout byte-for-byte). session.txt is session-level metadata, not
        // transcript content - it always stays at the root. An INACTIVE version's rendered files
        // are never touched, so the v1 originals are immutable while v2+ is active.
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptMd(sessionId, loaded.VersionId),
            MarkdownRenderer.Render(loaded.Header, loaded.Rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptTxt(sessionId, loaded.VersionId),
            PlainTextRenderer.Render(loaded.Header, loaded.Rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.SessionTxt(sessionId),
            SessionTextRenderer.Render(loaded.TextView), ct);
        // Reseal LAST, after every file it hashes is on disk (Tier 1 T1-7). This method is the
        // choke point every overlay write, recovery, import and re-transcription already calls, so
        // hooking here covers seventeen call sites at once - REJECTED: adding a reseal to each of
        // MaintenanceService's overlay methods, which is seventeen chances to forget one. It is NOT
        // sufficient on its own: two writers deliberately skip this method and call ResealAsync
        // directly (see its doc).
        await ResealAsync(sessionId, loaded.Session, ct, fabricated, sealAudio);
    }

    /// <summary>Rewrite EVERY version's manifest.json and nothing else (Tier 1 T1-7, spec
    /// 2026-08-05 :146-153). Two facts force "every version" rather than just the active one:
    /// session.json and meta.json are SESSION-level files sealed into a PER-VERSION manifest, and
    /// MaintenanceService.SetActiveVersionCoreAsync (:166-178) and PurgeAllVoiceprintsAsync
    /// (:762-770) both rewrite a sealed file WITHOUT regenerating projections. Without this, a v1
    /// manifest goes stale the moment any v2-era edit lands and Verify integrity reports
    /// `meta.json CHANGED` on an untampered session - a FALSE tamper verdict, the one outcome
    /// IntegrityReport's doc forbids.
    /// The caller supplies the SessionRecord it already holds rather than having this re-read it:
    /// SetActiveVersionCoreAsync must seal the record it just WROTE, and a re-read here would also
    /// risk write-migrating the very file it is about to hash (the MCP read-only precedent).
    /// Cost: text only. Audio is carried forward per ManifestBuilder's size+mtime match, so N
    /// versions cost N small hashes, not N passes over the FLAC.</summary>
    public async Task ResealAsync(string sessionId, SessionRecord session, CancellationToken ct,
        IReadOnlyDictionary<SourceKind, FabricatedSilenceRecord>? fabricated = null,
        bool sealAudio = false)
    {
        // Root FIRST: a version manifest with no audio entry of its own inherits the ROOT
        // manifest's (ManifestBuilder's rootByName fallback), so the root must already be current
        // when the versions are rebuilt.
        await ManifestBuilder.WriteAsync(_paths, sessionId, TranscriptVersions.Root,
            _time.GetUtcNow(), fabricated, sealAudio, ct);
        foreach (var version in session.Versions)
        {
            if (version.Id == TranscriptVersions.Root) continue;   // already done above
            await ManifestBuilder.WriteAsync(_paths, sessionId, version.Id,
                _time.GetUtcNow(), fabricated, sealAudio, ct);
        }
    }
```

- [ ] **Step 4: Hand the fabricated ranges to the finalize regenerate**

In `src/LocalScribe.Core/Live/SessionController.cs`, replace the last line of `PersistFinalAsync`
(`:1279`):

```csharp
        // Tier 1 T1-7 (spec 2026-08-05 :148-153): the ONLY moment the fabricated-silence ranges
        // exist in memory. The writers are disposed by now, but Dispose only closes the sink - the
        // recorded ranges survive on the object. Keyed by AlignedAudioWriter.Source rather than by
        // position in AudioWriters, so the map cannot silently invert if the list order ever
        // changes. Empty when AudioRetention == "never", in which case there is no leg to seal.
        // sealAudio:true ONLY here - this is the one moment the spec (:146-147) asks for a hash, and
        // ManifestBuilder's cost gate exists so the recovery scan and "Regenerate all" never take it.
        await new SessionWriter(_paths, s.Settings, _time).RegenerateProjectionsAsync(s.Id, ct,
            s.AudioWriters.ToDictionary(w => w.Source,
                w => new FabricatedSilenceRecord(w.SampleRate, w.FabricatedSilence)),
            sealAudio: true);
```

- [ ] **Step 5: Reseal from the two writers that skip the choke point**

In `src/LocalScribe.App/Services/MaintenanceService.cs`, inside `SetActiveVersionCoreAsync`, replace
the write at `:176`:

```csharp
            var updated = session with { ActiveVersion = versionId };
            await store.SaveAsync(updated, inner);
            // Tier 1 T1-7: session.json is sealed by EVERY version's manifest, and this method
            // deliberately does not regenerate projections (see the doc above), so it is the one
            // mutation the reseal choke point cannot see. Without this, the next Verify integrity
            // reports `session.json CHANGED` on a session nobody touched. Reseal only - no
            // projection regen, so the "each version keeps its own rendered files" rule stands.
            await new SessionWriter(paths, settings.Current, time).ResealAsync(sessionId, updated, inner);
            return (Ok: true, Wrote: true);
```

and inside `PurgeAllVoiceprintsAsync`, after the `foreach (var versionId in versionIds)` loop closes
and before `return didAny;` (`:770`):

```csharp
                        // Tier 1 T1-7: the loop above rewrites speakers.json for every version and
                        // never regenerates, so each rewrite would otherwise strand that version's
                        // manifest. Read with persistMigration:false - a purge must not write-migrate
                        // a legacy session.json as a side effect (the MCP read-only precedent).
                        if (didAny)
                        {
                            var purged = await new SessionStore(paths.SessionJson(sessionId))
                                .ReadAsync(selfForMigration: null, persistMigration: false, inner);
                            if (purged is not null)
                                await new SessionWriter(paths, settings.Current, time)
                                    .ResealAsync(sessionId, purged, inner);
                        }
                        return didAny;
```

Then confirm no OTHER writer of a sealed file skips both paths:

```bash
cd F:/LocalScribe && grep -n "\.SaveAsync(\|RegenerateProjectionsAsync\|ResealAsync" src/LocalScribe.App/Services/MaintenanceService.cs
```

Expected: every `SessionStore`/`MetadataStore`/`SpeakersStore`/`EditStore` `SaveAsync` in that file is
followed, within its own `RunForSessionAsync` body, by a `RegenerateProjectionsAsync` or a
`ResealAsync`. The two exceptions this step just closed were `:176` and the purge loop.

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~SessionWriterTests|FullyQualifiedName~SessionControllerTests|FullyQualifiedName~SessionProjectionLoaderTests|FullyQualifiedName~MaintenanceServiceVersionsTests"`

Expected: PASS. `SessionProjectionLoaderTests` guards the byte-identity of transcript.md/.txt/
session.txt - those three writes are unchanged, so it must pass untouched.
`MaintenanceServiceVersionsTests` is in the App project, so a Core-only filter would silently skip
the one test that proves Step 5's wiring exists.

- [ ] **Step 7: Run the whole suite - this task touches seventeen call sites indirectly**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "Category!=Fixture"`

Expected: PASS. Watch specifically for `MaintenanceService*Tests`, `AudioImporterTests`,
`RetranscriptionRunnerTests` and `ReadView*Tests`: each of them drives a path that now writes
`manifest.json`. A failure there means a fixture asserted an exact file listing of a session folder -
add `manifest.json` to its expectation rather than suppressing the write.

- [ ] **Step 8: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Storage/SessionWriter.cs src/LocalScribe.Core/Live/SessionController.cs src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.Core.Tests/SessionWriterTests.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs tests/LocalScribe.App.Tests/MaintenanceServiceVersionsTests.cs
git commit -m "feat(storage): seal the session folder at finalize and reseal every version on mutation"
```

---

## Task 10: `IntegrityVerifier` - per-file OK / CHANGED / MISSING

A seal nobody can check is decoration. This is the pure Core half; Task 11 is the button.

**Files:**
- Create: `src/LocalScribe.Core/Storage/IntegrityVerifier.cs`
- Create: `tests/LocalScribe.Core.Tests/IntegrityVerifierTests.cs`

**Interfaces:**
- Consumes: `ManifestStore` (Task 7), `ManifestBuilder.HashAsync` (Task 8, made public in Step 4
  below), `StoragePaths.ManifestJson` (Task 7). Deliberately **not** `ManifestBuilder.BuildAsync` -
  see the `IntegrityVerifier` class doc: building and diffing would carry an audio hash forward on a
  size+mtime match and hand back the sealed value without re-reading a byte.
- Produces:
  - `LocalScribe.Core.Storage.IntegrityStatus` - `enum { Ok, Changed, Missing }`.
  - `LocalScribe.Core.Storage.IntegrityCheck(string Name, IntegrityStatus Status)` - positional
    `sealed record`.
  - `LocalScribe.Core.Storage.IntegrityReport(string SessionId, DateTimeOffset? SealedAtUtc,
    IReadOnlyList<IntegrityCheck> Checks)` - positional `sealed record` with
    `bool Sealed => SealedAtUtc is not null`, `bool Passed`, and
    `string Summarize(string sessionTitle)`.
  - `IntegrityVerifier.VerifyAsync(StoragePaths paths, string sessionId, string versionId,
    CancellationToken ct) : Task<IntegrityReport>` (no clock: it reports the seal's timestamp, and
    the moment the check ran is not persisted anywhere).
  - `ManifestBuilder.HashAsync(string path, CancellationToken ct) : Task<string>` - the private
    `Sha256Async` from Task 8, promoted to public in Step 4 below.
  Task 11 calls `VerifyAsync` and `Summarize`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/IntegrityVerifierTests.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

/// <summary>"Verify integrity" (Tier 1 T1-7, spec 2026-08-05 :143). Re-hashes what the manifest
/// sealed and reports per file. The central product claim - that this is a faithful local record -
/// is unfalsifiable in BOTH directions without this: nothing could prove tampering, and nothing
/// could prove its absence either.</summary>
public sealed class IntegrityVerifierTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-integrity-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 10, 22, 0, TimeSpan.Zero);

    public IntegrityVerifierTests() { _paths = new StoragePaths(_root); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private async Task SealAsync(string id)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        File.WriteAllText(_paths.SessionJson(id), "{\"schemaVersion\":4,\"id\":\"" + id + "\"}");
        File.WriteAllText(_paths.MetaJson(id), "{\"schemaVersion\":3}");
        File.WriteAllText(_paths.TranscriptJsonl(id), "{\"seq\":0}\n");
        File.WriteAllText(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), "AAAA");
        // sealAudio:true - this fixture stands in for the FINALIZE path, the only caller allowed to
        // take a leg's first hash (ManifestBuilder's cost gate). With false, local.flac would not be
        // in the manifest at all and the file counts below would be 3, not 4.
        await ManifestBuilder.WriteAsync(_paths, id, TranscriptVersions.Root, Now,
            fabricated: null, sealAudio: true, CancellationToken.None);
    }

    [Fact]
    public async Task An_untouched_session_passes_with_every_file_ok()
    {
        await SealAsync("s-clean");

        var report = await IntegrityVerifier.VerifyAsync(_paths, "s-clean", TranscriptVersions.Root,
            CancellationToken.None);

        Assert.True(report.Sealed);
        Assert.True(report.Passed);
        Assert.Equal(Now, report.SealedAtUtc);
        Assert.All(report.Checks, c => Assert.Equal(IntegrityStatus.Ok, c.Status));
        Assert.Equal(4, report.Checks.Count);
        Assert.Equal("Integrity check passed for \"Doe intake\": 4 files match the seal written 2026-08-05 10:22.",
            report.Summarize("Doe intake"));
    }

    [Fact]
    public async Task An_edited_file_reads_CHANGED_and_a_deleted_one_reads_MISSING()
    {
        await SealAsync("s-tampered");
        File.WriteAllText(_paths.TranscriptJsonl("s-tampered"), "{\"seq\":0,\"text\":\"different\"}\n");
        File.Delete(_paths.AudioFile("s-tampered", SourceKind.Local, AudioFormat.Flac));

        var report = await IntegrityVerifier.VerifyAsync(_paths, "s-tampered",
            TranscriptVersions.Root, CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal(IntegrityStatus.Changed,
            report.Checks.Single(c => c.Name == "transcript.jsonl").Status);
        Assert.Equal(IntegrityStatus.Missing,
            report.Checks.Single(c => c.Name == "local.flac").Status);
        Assert.Equal(
            "Integrity check FAILED for \"Doe intake\": local.flac MISSING; transcript.jsonl CHANGED. "
            + "2 of 4 files match the seal written 2026-08-05 10:22.",
            report.Summarize("Doe intake"));
    }

    [Fact]
    public async Task An_unsealed_session_says_so_instead_of_reporting_a_pass()
    {
        // Every session recorded before this feature is unsealed. Reporting "0 files, all OK" would
        // be a false assurance, which is the one outcome an integrity command must never produce.
        Directory.CreateDirectory(_paths.SessionDir("s-old"));
        File.WriteAllText(_paths.SessionJson("s-old"), "{\"schemaVersion\":4}");

        var report = await IntegrityVerifier.VerifyAsync(_paths, "s-old", TranscriptVersions.Root,
            CancellationToken.None);

        Assert.False(report.Sealed);
        Assert.False(report.Passed);
        Assert.Empty(report.Checks);
        Assert.Equal(
            "\"Doe intake\" has no integrity seal - it was recorded before integrity manifests "
            + "existed, or its manifest.json was deleted. Nothing can be verified.",
            report.Summarize("Doe intake"));
    }

    [Fact]
    public async Task A_session_json_rewrite_followed_by_a_reseal_still_PASSES_on_every_version()
    {
        // The end-to-end shape of Task 9's fix: session.json is sealed by every version's manifest,
        // so a version switch (which rewrites session.json and skips the projection regen) must
        // reseal or Verify reports CHANGED on an untampered session. This asserts the PASS the
        // whole reseal exists to preserve.
        const string vid = "v2-tiny.en-2026-08-05";
        await SealAsync("s-switch");
        Directory.CreateDirectory(_paths.VersionDir("s-switch", vid));
        File.WriteAllText(_paths.TranscriptJsonl("s-switch", vid), "{\"seq\":0}\n");
        var store = new SessionStore(_paths.SessionJson("s-switch"));
        var session = (await store.ReadAsync(CancellationToken.None))! with
        {
            Versions = new[]
            {
                new TranscriptVersion { Id = vid, Model = "tiny.en", Backend = "CPU", Language = "en" },
            },
        };
        var writer = new SessionWriter(_paths, new Settings(), new ManualUtcTimeProvider(Now));
        await store.SaveAsync(session, CancellationToken.None);
        await writer.ResealAsync("s-switch", session, CancellationToken.None);

        var switched = session with { ActiveVersion = vid };
        await store.SaveAsync(switched, CancellationToken.None);
        await writer.ResealAsync("s-switch", switched, CancellationToken.None);

        foreach (string v in new[] { TranscriptVersions.Root, vid })
        {
            var report = await IntegrityVerifier.VerifyAsync(_paths, "s-switch", v,
                CancellationToken.None);
            Assert.True(report.Sealed);
            Assert.True(report.Passed, v + ": " + report.Summarize("Doe intake"));
        }
    }
}
```

The file needs `using LocalScribe.Core.Live;`? No - `SessionWriter` and `SessionStore` are both in
`LocalScribe.Core.Storage`, already imported above, and `Settings`/`TranscriptVersion` come from the
`LocalScribe.Core.Model` using. `ManualUtcTimeProvider` is the Core.Tests fake used by
`SessionWriterTests`.

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~IntegrityVerifierTests"`

Expected: FAIL to compile - `CS0103: The name 'IntegrityVerifier' does not exist in the current
context`.

- [ ] **Step 3: Write the verifier**

Create `src/LocalScribe.Core/Storage/IntegrityVerifier.cs`:

```csharp
using System.Globalization;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>One file's verdict against the seal (Tier 1 T1-7). Missing outranks Changed in the
/// summary ordering below because a deleted evidentiary file is the graver finding.</summary>
public enum IntegrityStatus { Ok, Changed, Missing }

public sealed record IntegrityCheck(string Name, IntegrityStatus Status);

/// <summary>The outcome of "Verify integrity" for one transcript version (Tier 1 T1-7, spec
/// 2026-08-05 :143). SealedAtUtc null means there is NO manifest - reported as its own outcome and
/// never as a pass, because "nothing to check" and "everything checks out" are opposite claims and
/// a false assurance is the one thing this command must not produce.</summary>
public sealed record IntegrityReport(string SessionId, DateTimeOffset? SealedAtUtc,
    IReadOnlyList<IntegrityCheck> Checks)
{
    public bool Sealed => SealedAtUtc is not null;

    /// <summary>An unsealed session never passes - see the record doc.</summary>
    public bool Passed => Sealed && Checks.All(c => c.Status == IntegrityStatus.Ok);

    /// <summary>One InfoBar line. Failures are listed by NAME (Missing first, then Changed, each
    /// Ordinal-sorted) rather than counted, because "2 files changed" tells a solicitor nothing
    /// about whether the transcript or a stray projection moved. Invariant culture, like every
    /// other evidentiary string in this codebase.</summary>
    public string Summarize(string sessionTitle)
    {
        if (!Sealed)
            return string.Create(CultureInfo.InvariantCulture,
                $"\"{sessionTitle}\" has no integrity seal - it was recorded before integrity manifests existed, or its manifest.json was deleted. Nothing can be verified.");

        string stamp = SealedAtUtc!.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (Passed)
            return string.Create(CultureInfo.InvariantCulture,
                $"Integrity check passed for \"{sessionTitle}\": {Checks.Count} files match the seal written {stamp}.");

        var bad = Checks.Where(c => c.Status != IntegrityStatus.Ok)
            .OrderBy(c => c.Status == IntegrityStatus.Missing ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => c.Name + " " + c.Status.ToString().ToUpperInvariant());
        int ok = Checks.Count(c => c.Status == IntegrityStatus.Ok);
        return string.Create(CultureInfo.InvariantCulture,
            $"Integrity check FAILED for \"{sessionTitle}\": {string.Join("; ", bad)}. {ok} of {Checks.Count} files match the seal written {stamp}.");
    }
}

/// <summary>Re-hashes what manifest.json sealed and compares (Tier 1 T1-7, spec 2026-08-05 :143).
/// Walks the SEALED list and re-reads each named file through ManifestBuilder.HashAsync - one
/// hashing implementation, so a verifier bug can never disagree with the sealer about how a file is
/// read. REJECTED: calling ManifestBuilder.BuildAsync and diffing the two manifests, which would
/// CARRY FORWARD any audio entry whose size+mtime still match and hand back the sealed hash without
/// re-reading a byte - a verifier that trusts the seal it is checking verifies nothing. Takes no
/// clock: the report states when the SEAL was written, and the moment the check ran is not
/// persisted anywhere.</summary>
public static class IntegrityVerifier
{
    public static async Task<IntegrityReport> VerifyAsync(StoragePaths paths, string sessionId,
        string versionId, CancellationToken ct)
    {
        var sealedManifest = await new ManifestStore(paths.ManifestJson(sessionId, versionId)).ReadAsync(ct);
        if (sealedManifest is null) return new IntegrityReport(sessionId, null, []);

        var checks = new List<IntegrityCheck>();
        string sessionDir = paths.SessionDir(sessionId);
        foreach (var file in sealedManifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(sessionDir, file.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { checks.Add(new IntegrityCheck(file.Name, IntegrityStatus.Missing)); continue; }
            var info = new FileInfo(path);
            // Size first: a cheap, certain CHANGED verdict that skips hashing a multi-GB leg whose
            // length already disagrees with the seal.
            if (info.Length != file.SizeBytes)
            { checks.Add(new IntegrityCheck(file.Name, IntegrityStatus.Changed)); continue; }
            string actual = await ManifestBuilder.HashAsync(path, ct);
            checks.Add(new IntegrityCheck(file.Name,
                string.Equals(actual, file.Sha256, StringComparison.Ordinal)
                    ? IntegrityStatus.Ok : IntegrityStatus.Changed));
        }
        return new IntegrityReport(sessionId, sealedManifest.WrittenAtUtc, checks);
    }
}
```

- [ ] **Step 4: Expose the hash helper**

`IntegrityVerifier` needs the same reader the sealer uses. In
`src/LocalScribe.Core/Storage/ManifestBuilder.cs`, rename the private `Sha256Async` to a public
`HashAsync` (keep the entire doc comment) and update its SINGLE call site - the
`Sha256 = await Sha256Async(path, ct)` initializer inside `SealAsync`. There is no second call site
anywhere in the class:

```csharp
    /// <summary>Streaming SHA-256, the AudioImporter.CopyWithSha256Async idiom (:263-276) with the
    /// copy half dropped - lowercase hex via Convert.ToHexStringLower, 64 KiB buffer, so a multi-GB
    /// FLAC never lands in memory. FileShare.ReadWrite | Delete, NOT FileShare.Read: the importer's
    /// share mode is safe only because it reads a user file no LocalScribe process holds, whereas
    /// this reads inside a session folder whose capture pipeline may still hold local.flac and
    /// transcript.jsonl open for WRITING. That exact defect has been fixed twice in this repo
    /// (SessionArchiver.cs:34-43); Delete additionally tolerates an AtomicFile replace mid-read.
    /// PUBLIC so IntegrityVerifier re-reads files EXACTLY as the sealer wrote them - one
    /// implementation, so a verifier bug can never disagree with the seal about how a file is
    /// read (there is no InternalsVisibleTo in this repo).</summary>
    public static async Task<string> HashAsync(string path, CancellationToken ct)
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~IntegrityVerifierTests|FullyQualifiedName~ManifestBuilderTests|FullyQualifiedName~SessionWriterTests"`

Expected: PASS. If `An_untouched_session_passes_with_every_file_ok` reports 5 files instead of 4, the
fixture created `edits.json` or `speakers.json` - it must not. If it reports 3, the `SealAsync`
fixture lost its `sealAudio: true` and `local.flac` was skipped by the cost gate.

- [ ] **Step 6: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Storage/IntegrityVerifier.cs src/LocalScribe.Core/Storage/ManifestBuilder.cs tests/LocalScribe.Core.Tests/IntegrityVerifierTests.cs
git commit -m "feat(storage): IntegrityVerifier reports per-file OK/CHANGED/MISSING"
```

---

## Task 11: the Sessions-page "Verify integrity" command

**Files:**
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (add `VerifyIntegrityAsync` beside
  `ExportSessionArchiveAsync` at `:986`)
- Modify: `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs:114` (command property),
  `:200` (construction), plus the handler
- Modify: `src/LocalScribe.App/Pages/SessionsPage.xaml:77` (action bar), `:142` (context menu)
- Create: `tests/LocalScribe.App.Tests/SessionsPageVerifyIntegrityTests.cs`

**Interfaces:**
- Consumes: `IntegrityVerifier.VerifyAsync(...)`, `IntegrityReport.Summarize(string)` (Task 10);
  `MaintenanceService.RunForSessionAsync<T>(string, Func<CancellationToken, Task<T>>, CancellationToken)`
  (existing); `IUiErrorReporter.Info(string)` / `.Report(string, Exception)` (existing);
  `SessionRowViewModel.Id` / `.Title` (existing).
- Produces:
  - `MaintenanceService.VerifyIntegrityAsync(string sessionId, CancellationToken ct) : Task<IntegrityReport>`.
  - `SessionsPageViewModel.VerifyIntegrityCommand : IAsyncRelayCommand<SessionRowViewModel>`.
  Nothing later consumes these.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/SessionsPageVerifyIntegrityTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The Sessions-page integrity command (Tier 1 T1-7, spec 2026-08-05 :143). The outcome
/// goes through IUiErrorReporter.Info, not a bespoke dialog: it is a background-operation OUTCOME,
/// which is exactly what Info exists for (IUiErrorReporter's own doc), and it keeps this VM
/// WPF-free and headless-testable.</summary>
public sealed class SessionsPageVerifyIntegrityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-verify-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public SessionsPageVerifyIntegrityTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class CollectingReporter : IUiErrorReporter
    {
        public List<string> Infos { get; } = [];
        public List<string> Reports { get; } = [];
        public void Report(string context, Exception ex) => Reports.Add(context + ": " + ex.Message);
        public void Info(string message) => Infos.Add(message);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }

    private sealed class NoopBin : IRecycleBin { public void SendToRecycleBin(string path) { } }

    /// <summary>Lock-guarded stand-in for WPF's Dispatcher.BeginInvoke, pumped explicitly. Copied
    /// verbatim from SessionsPageContentFilterTests.cs:27-52 (post-mortem doc-comment :27-34, class
    /// :35-52), which carries the post-mortem: with a
    /// synchronous <c>a =&gt; a()</c> fake, THIS view model applies its results from a THREAD-POOL
    /// continuation and mutates Rows while the test thread is enumerating it - "Collection was
    /// modified" under full-suite load, one of the five flaky families fixed on 2026-07-30. A plain
    /// Queue&lt;Action&gt; would corrupt under the concurrent enqueue, so dequeue under the lock and
    /// invoke outside it.</summary>
    private sealed class QueuedDispatch
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _queue = new();
        public Action<Action> Dispatch => a => { lock (_gate) _queue.Enqueue(a); };
        public bool PumpOne()
        {
            Action next;
            lock (_gate)
            {
                if (_queue.Count == 0) return false;
                next = _queue.Dequeue();
            }
            next();
            return true;
        }
        public void Pump() { while (PumpOne()) { } }
    }

    private (SessionsPageViewModel Vm, CollectingReporter Errors, QueuedDispatch Dispatcher) MakeVm()
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), new ManualUtcTimeProvider(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero)));
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var errors = new CollectingReporter();
        var dispatcher = new QueuedDispatch();
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(), errors,
            dispatch: dispatcher.Dispatch, time: TimeProvider.System, revealInExplorer: _ => { });
        return (vm, errors, dispatcher);
    }

    /// <summary>A sealed session on disk: three text files plus one leg, then a manifest over them.</summary>
    private async Task SealAsync(string id)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Doe intake" }, CancellationToken.None);
        File.WriteAllText(_paths.TranscriptJsonl(id), "{\"seq\":0}\n");
        File.WriteAllText(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), "AAAA");
        // sealAudio:true - this fixture stands in for the finalize path (ManifestBuilder's cost
        // gate); with false, local.flac would never enter the manifest.
        await ManifestBuilder.WriteAsync(_paths, id, TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 22, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: true, CancellationToken.None);
    }

    [Fact]
    public async Task Verifying_an_untouched_session_reports_a_pass()
    {
        await SealAsync("s1");
        var (vm, errors, dispatcher) = MakeVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        dispatcher.Pump();

        await vm.VerifyIntegrityCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == "s1"));
        dispatcher.Pump();

        Assert.Empty(errors.Reports);
        Assert.Contains("Integrity check passed", Assert.Single(errors.Infos));
        Assert.Contains("Doe intake", errors.Infos[0]);
    }

    [Fact]
    public async Task Verifying_an_untouched_session_still_passes_after_a_verification()
    {
        // The verifier must not WRITE anything it is about to hash. SessionStore's two-argument
        // ReadAsync is persistMigration:TRUE, so a verifier using it would rewrite a legacy
        // session.json (and synthesize meta.json) before comparing, then report its own write as
        // `session.json CHANGED` on an untampered session. mtime is the cheapest proof that no
        // write happened at all - see Step 3's comment and the MCP read-only precedent.
        await SealAsync("s-ro");
        var before = File.GetLastWriteTimeUtc(_paths.SessionJson("s-ro"));
        var (vm, errors, dispatcher) = MakeVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        dispatcher.Pump();

        await vm.VerifyIntegrityCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == "s-ro"));
        dispatcher.Pump();
        await vm.VerifyIntegrityCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == "s-ro"));
        dispatcher.Pump();

        Assert.Equal(before, File.GetLastWriteTimeUtc(_paths.SessionJson("s-ro")));
        Assert.All(errors.Infos, i => Assert.Contains("Integrity check passed", i));
    }

    [Fact]
    public async Task Verifying_a_tampered_session_names_the_file_that_moved()
    {
        await SealAsync("s2");
        File.WriteAllText(_paths.TranscriptJsonl("s2"), "{\"seq\":0,\"text\":\"rewritten\"}\n");
        var (vm, errors, dispatcher) = MakeVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        dispatcher.Pump();

        await vm.VerifyIntegrityCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == "s2"));
        dispatcher.Pump();

        string info = Assert.Single(errors.Infos);
        Assert.Contains("Integrity check FAILED", info);
        Assert.Contains("transcript.jsonl CHANGED", info);
    }

    [Fact]
    public async Task A_null_row_is_a_no_op_rather_than_a_reported_error()
    {
        // Every other row command on this page tolerates the null the action bar can hand it before
        // a selection exists; a NullReferenceException surfaced as a red InfoBar would be noise.
        var (vm, errors, dispatcher) = MakeVm();
        await vm.VerifyIntegrityCommand.ExecuteAsync(null);
        dispatcher.Pump();
        Assert.Empty(errors.Infos);
        Assert.Empty(errors.Reports);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~SessionsPageVerifyIntegrityTests"`

Expected: FAIL to compile - `CS1061: 'SessionsPageViewModel' does not contain a definition for
'VerifyIntegrityCommand'`.

- [ ] **Step 3: Add the service method**

In `src/LocalScribe.App/Services/MaintenanceService.cs`, add directly above
`ExportSessionArchiveAsync` (`:983`):

```csharp
    /// <summary>Re-hash this session's ACTIVE version against manifest.json (Tier 1 T1-7, spec
    /// 2026-08-05 :143). Held under the per-session gate so a concurrent overlay write cannot
    /// reseal the folder halfway through the comparison and produce a phantom CHANGED. Reads the
    /// active version from session.json rather than assuming "v1": a re-transcribed session's
    /// evidence lives in the version the user is actually reading.
    /// persistMigration:FALSE is load-bearing, not tidiness. SessionStore's two-argument ReadAsync
    /// is persistMigration:true (SessionStore.cs:17-18), so on any session.json predating the
    /// current schema that read REWRITES session.json - and can synthesize meta.json - BEFORE the
    /// comparison runs. The verifier would then report its OWN write as `session.json CHANGED` on an
    /// untampered session: a false tamper verdict, the one outcome IntegrityReport's doc forbids.
    /// This is the standing rule the MCP round recorded (read-only consumers pass
    /// persistMigration:false; SessionProjectionLoader.LoadAsync carries the parameter for exactly
    /// this reason). A verifier that writes what it is about to hash verifies nothing.</summary>
    public Task<IntegrityReport> VerifyIntegrityAsync(string sessionId, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            var session = await new SessionStore(paths.SessionJson(sessionId))
                .ReadAsync(selfForMigration: null, persistMigration: false, inner);
            if (session is null) throw new InvalidOperationException("The session no longer exists.");
            return await IntegrityVerifier.VerifyAsync(paths, sessionId, session.ActiveVersion, inner);
        }, ct);
```

- [ ] **Step 4: Add the command**

In `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs`, add the property after
`ExportSessionCommand` (`:114`):

```csharp
    /// <summary>"Verify integrity" (Tier 1 T1-7, spec 2026-08-05 :143): re-hash the session folder
    /// against its seal and state the result. Async, unlike its neighbours, because it reads every
    /// sealed file - a long call's audio takes seconds. The outcome goes to IUiErrorReporter.Info
    /// (a background-operation OUTCOME, per that interface's own doc), never a modal dialog.</summary>
    public IAsyncRelayCommand<SessionRowViewModel> VerifyIntegrityCommand { get; }
```

Add to the constructor after the `ExportSessionCommand` assignment (`:200`):

```csharp
        VerifyIntegrityCommand = new AsyncRelayCommand<SessionRowViewModel>(VerifyIntegrityAsync);
```

Add the handler beside the other private command handlers:

```csharp
    /// <summary>Null row: the action bar can execute before a selection exists, and every other row
    /// command here tolerates that - a NullReferenceException surfaced as a red InfoBar would be
    /// pure noise. A verification FAILURE is not an exception: it is the answer, and it goes through
    /// Info like a pass does. Only a genuine fault (deleted session, unreadable manifest) reports.</summary>
    private async Task VerifyIntegrityAsync(SessionRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            var report = await _maintenance.VerifyIntegrityAsync(row.Id, CancellationToken.None);
            _errors.Info(report.Summarize(row.Title));
        }
        catch (Exception ex) { _errors.Report("Verifying integrity", ex); }
    }
```

Add `using LocalScribe.Core.Storage;` to the file if it is missing (check with
`grep -n "using LocalScribe.Core.Storage" src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs`).

- [ ] **Step 5: Wire the XAML**

In `src/LocalScribe.App/Pages/SessionsPage.xaml`, add to the action bar directly after the
`Re-transcribe...` button (`:77-79`):

```xml
            <ui:Button Content="Verify integrity" Margin="0,0,8,0"
                       IsEnabled="{Binding HasSelection}"
                       ToolTip="Re-check this session's files against the integrity manifest written when it was finalized."
                       Command="{Binding VerifyIntegrityCommand}" CommandParameter="{Binding SelectedRow}" />
```

and to the row context menu directly after the `Re-transcribe...` item (`:142-144`):

```xml
                                <MenuItem Header="Verify integrity"
                                          Command="{Binding Data.VerifyIntegrityCommand, Source={StaticResource VmProxy}}"
                                          CommandParameter="{Binding}" />
```

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~SessionsPageVerifyIntegrityTests|FullyQualifiedName~XamlHygieneTests|FullyQualifiedName~SessionsPageViewModelTests"`

Expected: PASS. `XamlHygieneTests` walks the real XAML from the repo root and will catch a malformed
binding or a command name that does not exist on the VM.

- [ ] **Step 7: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/Services/MaintenanceService.cs src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs src/LocalScribe.App/Pages/SessionsPage.xaml tests/LocalScribe.App.Tests/SessionsPageVerifyIntegrityTests.cs
git commit -m "feat(sessions): Verify integrity command reports per-file OK/CHANGED/MISSING"
```

---

## Task 12: surface the hashes and the accuracy tier in export metadata

The metadata block currently carries `Audio` / `Audio SHA-256` for **imported** sessions only
(`DocxRenderer.cs:64-67`). A recorded session shows nothing, because there was no hash to show. There
is one now.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/ExportProvenance.cs`
- Modify: `src/LocalScribe.Core/Projection/MetadataFormat.cs`
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs` - the metadata run from the
  `Transcript version` line through the `Audio SHA-256` line (`:63-67` pre-round)
- Modify: `src/LocalScribe.Core/Projection/MarkdownRenderer.cs` - same run (`:58-61` pre-round)
- Modify: `src/LocalScribe.Core/Projection/PlainTextRenderer.cs` - same run (`:61-65` pre-round)
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs:1003-1090`
- Test: `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs`, `DocxRendererTests.cs`,
  `MarkdownRendererWriteTests.cs`, `PlainTextRendererWriteTests.cs`

**Interfaces:**
- Consumes: `WhisperModelCatalog.Describe(string)` (existing) and `AccuracyTier` (Task 4);
  `ManifestStore` / `SessionManifest` / `ManifestFile` / `FabricatedSpan` (Tasks 6-7);
  `StoragePaths.ManifestJson(id, versionId)` (Task 7); `LoadedProjection` (existing).
- Produces:
  - `LocalScribe.Core.Projection.FabricatedSilenceSummary(int SpanCount, long TotalMs)` - positional
    `sealed record`.
  - `LocalScribe.Core.Projection.RecordedAudioLeg` - `{ string FileName; string Sha256;
    FabricatedSilenceSummary? Silence }`, all `{ get; init; }`.
  - `ExportProvenance.ModelAccuracy : string` (default `""`),
    `ExportProvenance.TranscriptSha256 : string?`,
    `ExportProvenance.RecordedAudio : IReadOnlyList<RecordedAudioLeg>` (default `[]`).
  - `MetadataFormat.RecordedAudioLines(ExportProvenance p) : IReadOnlyList<(string Label, string Value)>`.
  - `MaintenanceService.ProvenanceFor(LoadedProjection loaded, SessionManifest? manifest = null)`
    - Task 13 adds a `TimeProvider` parameter to this same method; both tasks edit the same
    signature, so read Task 13's Interfaces block before touching it if the tasks run out of order.
  Task 15 renders nothing from here.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs`, inside the class:

```csharp
    [Fact]
    public void RecordedAudioLines_states_the_fabricated_silence_beside_every_hash()
    {
        // Tier 1 T1-7 (spec 2026-08-05 :148-153): AlignedAudioWriter inserts zeros for every clock
        // gap and pads to the session end. A hash presented WITHOUT that fact certifies synthetic
        // silence as original recorded audio - the sentence has to travel with the number, in one
        // place, so the three formats cannot word it differently.
        var p = new ExportProvenance
        {
            RecordedAudio =
            [
                new RecordedAudioLeg
                { FileName = "local.flac", Sha256 = "aaa", Silence = new FabricatedSilenceSummary(3, 42_000) },
                new RecordedAudioLeg
                { FileName = "remote.flac", Sha256 = "bbb", Silence = new FabricatedSilenceSummary(0, 0) },
                new RecordedAudioLeg { FileName = "local.wav", Sha256 = "ccc", Silence = null },
            ],
        };

        Assert.Equal(
            new[]
            {
                ("Audio SHA-256 (local.flac)",
                    "aaa (includes 3 machine-generated silence spans, 00:00:42 total)"),
                ("Audio SHA-256 (remote.flac)", "bbb (no machine-generated silence)"),
                ("Audio SHA-256 (local.wav)",
                    "ccc (machine-generated silence not recorded for this file)"),
            },
            MetadataFormat.RecordedAudioLines(p));
    }

    [Fact]
    public void RecordedAudioLines_is_empty_for_a_session_with_no_sealed_audio()
        => Assert.Empty(MetadataFormat.RecordedAudioLines(new ExportProvenance()));

    [Fact]
    public void One_fabricated_span_reads_singular()
    {
        var p = new ExportProvenance
        {
            RecordedAudio =
                [new RecordedAudioLeg
                { FileName = "local.flac", Sha256 = "aaa", Silence = new FabricatedSilenceSummary(1, 1500) }],
        };
        Assert.Equal("aaa (includes 1 machine-generated silence span, 00:00:01 total)",
            MetadataFormat.RecordedAudioLines(p).Single().Value);
    }
```

Append to `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`, inside the class:

```csharp
    [Fact]
    public void Recorded_audio_hashes_the_transcript_hash_and_the_accuracy_tier_render_and_are_absent_by_default()
    {
        // Tier 1 T1-6/T1-7. Every optional metadata line gets BOTH halves: the failure mode being
        // guarded is an empty "Transcript SHA-256:" line in a document served on the other side.
        byte[] sealedDoc = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance
            {
                Model = "small.en",
                ModelAccuracy = "Decent accuracy, English only - quick",
                TranscriptSha256 = "deadbeef",
                RecordedAudio =
                    [new RecordedAudioLeg
                    { FileName = "local.flac", Sha256 = "aaa", Silence = new FabricatedSilenceSummary(2, 3000) }],
            });
        using (var doc = Open(sealedDoc))
        {
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Model accuracy: Decent accuracy, English only - quick", text);
            Assert.Contains("Transcript SHA-256: deadbeef", text);
            Assert.Contains("Audio SHA-256 (local.flac): aaa (includes 2 machine-generated silence spans, 00:00:03 total)", text);
        }

        byte[] plain = Render("relative", DocxPageSize.A4, new ExportOptions());
        using (var doc = Open(plain))
        {
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.DoesNotContain("Model accuracy:", text);
            Assert.DoesNotContain("Transcript SHA-256:", text);
            Assert.DoesNotContain("Audio SHA-256 (", text);
        }
    }
```

Append the same pair, in the format each file already uses, to
`tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`:

```csharp
    [Fact]
    public void Recorded_audio_and_transcript_hashes_render_as_bullets_and_are_absent_by_default()
    {
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance
        {
            ModelAccuracy = "Decent accuracy, English only - quick",
            TranscriptSha256 = "deadbeef",
            RecordedAudio =
                [new RecordedAudioLeg
                { FileName = "local.flac", Sha256 = "aaa", Silence = new FabricatedSilenceSummary(0, 0) }],
        }, null, r, "relative", new ExportOptions());

        Assert.Contains("- **Model accuracy:** Decent accuracy, English only - quick\n", md);
        Assert.Contains("- **Transcript SHA-256:** deadbeef\n", md);
        Assert.Contains("- **Audio SHA-256 (local.flac):** aaa (no machine-generated silence)\n", md);

        string bare = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, r, "relative",
            new ExportOptions());
        Assert.DoesNotContain("Transcript SHA-256", bare);
        Assert.DoesNotContain("Model accuracy", bare);
    }
```

and to `tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs`:

```csharp
    [Fact]
    public void Recorded_audio_and_transcript_hashes_render_undecorated_and_are_absent_by_default()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance
        {
            ModelAccuracy = "Decent accuracy, English only - quick",
            TranscriptSha256 = "deadbeef",
            RecordedAudio =
                [new RecordedAudioLeg
                { FileName = "remote.flac", Sha256 = "bbb", Silence = null }],
        }, null, [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains("Model accuracy: Decent accuracy, English only - quick\r\n", txt);
        Assert.Contains("Transcript SHA-256: deadbeef\r\n", txt);
        Assert.Contains(
            "Audio SHA-256 (remote.flac): bbb (machine-generated silence not recorded for this file)\r\n",
            txt);

        string bare = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());
        Assert.DoesNotContain("Transcript SHA-256", bare);
        Assert.DoesNotContain("Model accuracy", bare);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~MetadataFormatTests|FullyQualifiedName~DocxRendererTests|FullyQualifiedName~MarkdownRendererWriteTests|FullyQualifiedName~PlainTextRendererWriteTests"`

Expected: FAIL to compile - `CS0117: 'ExportProvenance' does not contain a definition for
'RecordedAudio'` / `'ModelAccuracy'` / `'TranscriptSha256'`, and
`CS0246: The type or namespace name 'RecordedAudioLeg' could not be found`.

- [ ] **Step 3: Extend `ExportProvenance`**

In `src/LocalScribe.Core/Projection/ExportProvenance.cs`, add above the `ExportProvenance` record:

```csharp
/// <summary>How much of a retained leg is machine-generated (Tier 1 T1-7, spec 2026-08-05
/// :148-153), summarised from the manifest's FabricatedSpan list for a reader who is not going to
/// open manifest.json. NULL means "not recorded" - a distinct claim from a zero count, and the two
/// must never be conflated in an evidentiary document.</summary>
public sealed record FabricatedSilenceSummary(int SpanCount, long TotalMs);

/// <summary>One retained audio leg's seal, read from manifest.json at export time (Tier 1 T1-7).
/// This does NOT re-open the 2026-08-04 ruling against hashing recorded audio AT EXPORT TIME: the
/// hash was computed once at finalize and this is a small JSON read of the stored value. No audio
/// file is opened on the export path.</summary>
public sealed record RecordedAudioLeg
{
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public FabricatedSilenceSummary? Silence { get; init; }
}
```

and add these members to `ExportProvenance` (after `AudioSha256`):

```csharp
    /// <summary>The catalog subtitle for Model, e.g. "Decent accuracy, English only - quick"
    /// (Tier 1 T1-6, spec 2026-08-05 :66-72). The owner ruled the live model cap stays, so the
    /// divergence from import's large-v3-turbo default is DISCLOSED here. "" for an uncatalogued
    /// model and for an all-default instance, which keeps pre-feature output byte-identical.</summary>
    public string ModelAccuracy { get; init; } = "";

    /// <summary>SHA-256 of the transcript.jsonl this document was rendered from, read from
    /// manifest.json (Tier 1 T1-7). Null when the session has no seal - every session recorded
    /// before manifests existed.</summary>
    public string? TranscriptSha256 { get; init; }

    /// <summary>Each retained leg's seal (Tier 1 T1-7). Empty for an imported session, whose audio
    /// provenance is the AudioFileName/AudioSha256 pair above, and for an unsealed one.</summary>
    public IReadOnlyList<RecordedAudioLeg> RecordedAudio { get; init; } = [];
```

Also amend the `AudioSha256` doc comment, which currently contradicts the new behaviour:

```csharp
    /// <summary>Imported sessions only, from ImportedSourceInfo. Null for recorded sessions -
    /// hashing recorded audio AT EXPORT TIME is deliberately out of scope (it would hash a large
    /// FLAC on every export), and that 2026-08-04 ruling STANDS. A recorded session's audio hash
    /// arrives on RecordedAudio instead, computed once at finalize (Tier 1 T1-7) and merely READ
    /// here - no audio file is opened on the export path.</summary>
```

- [ ] **Step 4: Add the shared formatter**

Append to `src/LocalScribe.Core/Projection/MetadataFormat.cs`, inside the class:

```csharp
    /// <summary>One "Audio SHA-256 (local.flac)" label/value pair per sealed leg (Tier 1 T1-7,
    /// spec 2026-08-05 :148-153). The fabricated-silence clause is NOT optional decoration: a hash
    /// presented without it certifies machine-generated zeros as original recorded audio, which the
    /// spec calls worse than no hash at all. Composed here, once, so the .docx, .md and .txt
    /// renderers cannot word the same disclosure differently.</summary>
    public static IReadOnlyList<(string Label, string Value)> RecordedAudioLines(ExportProvenance p)
    {
        var lines = new List<(string, string)>();
        foreach (var leg in p.RecordedAudio)
        {
            string clause;
            if (leg.Silence is null)
                clause = " (machine-generated silence not recorded for this file)";
            else if (leg.Silence.SpanCount == 0)
                clause = " (no machine-generated silence)";
            else
            {
                string spans = leg.Silence.SpanCount == 1 ? "span" : "spans";
                clause = string.Create(CultureInfo.InvariantCulture,
                    $" (includes {leg.Silence.SpanCount} machine-generated silence {spans}, {Hms(leg.Silence.TotalMs)} total)");
            }
            lines.Add(("Audio SHA-256 (" + leg.FileName + ")", leg.Sha256 + clause));
        }
        return lines;
    }

    /// <summary>HH:MM:SS with UNBOUNDED hours. Deliberately duplicated from MaintenanceService.Hms
    /// (design 2026-08-04 section 8 review finding 1), which is private and lives in the App layer
    /// while this is Core: TimeSpan's own "hh" specifier is the Hours COMPONENT (0-23), so a
    /// 25-hour figure would silently print "01:00:00". (long)TotalHours never wraps.</summary>
    private static string Hms(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(long)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}");
    }
```

- [ ] **Step 5: Render in all three formats**

In each renderer, replace the CONTIGUOUS BLOCK that runs from the `Transcript version` line through
the `Audio SHA-256` line - i.e. from `MetaLine("Transcript version", ...)` / `AppendMeta(sb,
"Transcript version", ...)` down to and including the `Audio SHA-256` emit, stopping BEFORE the
`string speakers = MetadataFormat.SpeakersHeard(rows);` line. Find that block by its content, not by
its line number: Tasks 13 and 14 shift all three files, and the numbers quoted here are pre-round.

`DocxRenderer.Write` (`:63-67` pre-round; keep `MetaLine` - it already applies
`SuppressLineNumbers`, and a hand-built `Paragraph` would silently renumber the whole transcript):

```csharp
        body.AppendChild(MetaLine("Transcript version", MetadataFormat.VersionLine(provenance)));
        if (!string.IsNullOrEmpty(provenance.ModelAccuracy))
            body.AppendChild(MetaLine("Model accuracy", provenance.ModelAccuracy));
        if (!string.IsNullOrEmpty(provenance.AudioFileName))
            body.AppendChild(MetaLine("Audio", provenance.AudioFileName));
        if (!string.IsNullOrEmpty(provenance.AudioSha256))
            body.AppendChild(MetaLine("Audio SHA-256", provenance.AudioSha256));
        if (!string.IsNullOrEmpty(provenance.TranscriptSha256))
            body.AppendChild(MetaLine("Transcript SHA-256", provenance.TranscriptSha256));
        foreach (var (label, value) in MetadataFormat.RecordedAudioLines(provenance))
            body.AppendChild(MetaLine(label, value));
```

`MarkdownRenderer.Write` (`:58-61` pre-round, same content anchor):

```csharp
        AppendMeta(sb, "Transcript version", MetadataFormat.VersionLine(provenance));
        if (!string.IsNullOrEmpty(provenance.ModelAccuracy))
            AppendMeta(sb, "Model accuracy", provenance.ModelAccuracy);
        if (!string.IsNullOrEmpty(provenance.AudioFileName)) AppendMeta(sb, "Audio", provenance.AudioFileName);
        if (!string.IsNullOrEmpty(provenance.AudioSha256))
            AppendMeta(sb, "Audio SHA-256", provenance.AudioSha256);
        if (!string.IsNullOrEmpty(provenance.TranscriptSha256))
            AppendMeta(sb, "Transcript SHA-256", provenance.TranscriptSha256);
        foreach (var (label, value) in MetadataFormat.RecordedAudioLines(provenance))
            AppendMeta(sb, label, value);
```

`PlainTextRenderer.Write` (`:61-65` pre-round, same content anchor):

```csharp
        AppendMeta(sb, "Transcript version", MetadataFormat.VersionLine(provenance));
        if (!string.IsNullOrEmpty(provenance.ModelAccuracy))
            AppendMeta(sb, "Model accuracy", provenance.ModelAccuracy);
        if (!string.IsNullOrEmpty(provenance.AudioFileName))
            AppendMeta(sb, "Audio", provenance.AudioFileName);
        if (!string.IsNullOrEmpty(provenance.AudioSha256))
            AppendMeta(sb, "Audio SHA-256", provenance.AudioSha256);
        if (!string.IsNullOrEmpty(provenance.TranscriptSha256))
            AppendMeta(sb, "Transcript SHA-256", provenance.TranscriptSha256);
        foreach (var (label, value) in MetadataFormat.RecordedAudioLines(provenance))
            AppendMeta(sb, label, value);
```

- [ ] **Step 6: Fill the fields at the composition point**

In `src/LocalScribe.App/Services/MaintenanceService.cs`, replace `ProvenanceFor` (`:1075-1090`):

```csharp
    /// <summary>Compose the export-only provenance block (design 2026-08-03 section 1). Composed
    /// HERE, where footerText used to compose, so the renderers stay pure serializers. Shared by
    /// ALL THREE textual formats so they can never disagree about provenance. Public static: tests
    /// drive the mapping directly (no InternalsVisibleTo in this repo - the
    /// RecordingConsoleViewModel.PreflightLine precedent).
    /// <paramref name="manifest"/> (Tier 1 T1-7) is manifest.json for the version being rendered,
    /// or null for an unsealed session. Reading it is a small JSON load, NOT a hash: the 2026-08-04
    /// ruling that recorded audio is never hashed at export time stands.</summary>
    public static ExportProvenance ProvenanceFor(LoadedProjection loaded, SessionManifest? manifest = null)
        => new()
        {
            VersionId = loaded.VersionId,
            Model = loaded.Header.Model,
            Backend = loaded.Header.Backend,
            // Tier 1 T1-6: the catalog's own words for how accurate this model is. Describe() never
            // throws and returns an empty Subtitle for an unknown user-dropped ggml, which the
            // renderers treat as "omit the line" rather than printing an empty claim.
            ModelAccuracy = WhisperModelCatalog.Describe(loaded.Header.Model).Subtitle,
            AudioFileName = loaded.Session.ImportedSource?.FileName,
            AudioSha256 = loaded.Session.ImportedSource?.Sha256,
            InProgress = loaded.Session.EndedAtUtc is null,
            TranscriptSha256 = manifest?.Files
                .FirstOrDefault(f => f.Name.EndsWith("transcript.jsonl", StringComparison.Ordinal))?.Sha256,
            RecordedAudio = manifest is null ? [] : RecordedLegs(manifest),
        };

    /// <summary>Project the manifest's audio entries into the export shape (Tier 1 T1-7). Matched
    /// by EXTENSION rather than by an expected name list, so a .wav-era session seals and reports
    /// exactly like a .flac one. TotalMs is derived from the sample offsets - the manifest stores
    /// samples because they are exact, and a reader needs a duration.</summary>
    private static List<RecordedAudioLeg> RecordedLegs(SessionManifest manifest)
    {
        var legs = new List<RecordedAudioLeg>();
        foreach (var f in manifest.Files)
        {
            if (!f.Name.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
                && !f.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;
            legs.Add(new RecordedAudioLeg
            {
                FileName = f.Name,
                Sha256 = f.Sha256,
                Silence = !f.FabricatedSilenceKnown
                    ? null
                    : new FabricatedSilenceSummary(f.FabricatedSilence.Count,
                        f.SampleRate <= 0
                            ? 0
                            : f.FabricatedSilence.Sum(s => s.EndSample - s.StartSample) * 1000L / f.SampleRate),
            });
        }
        return legs;
    }
```

Add `using LocalScribe.Core.Transcription;` to `MaintenanceService.cs` if it is missing.

Add the read helper beside `RecordedLegs`:

```csharp
    /// <summary>manifest.json for the version being exported, or null (Tier 1 T1-7). A manifest
    /// written by a NEWER build makes SchemaGuard.RejectIfNewer throw NotSupportedException, and
    /// that must NOT block the export: the manifest is a DERIVED sidecar, the transcript is the
    /// evidence, and refusing to hand a solicitor their document because a sidecar is from the
    /// future would be absurd. Degrading to null renders exactly what an unsealed session renders -
    /// no hash lines - which is honest: this build genuinely cannot read that seal. REJECTED:
    /// catching everything, which would also swallow a real IO fault; those still propagate to
    /// IUiErrorReporter and are surfaced to the user.</summary>
    private static async Task<SessionManifest?> ReadManifestForExportAsync(StoragePaths paths,
        string sessionId, string versionId, CancellationToken ct)
    {
        try { return await new ManifestStore(paths.ManifestJson(sessionId, versionId)).ReadAsync(ct); }
        catch (NotSupportedException) { return null; }
    }
```

Then, in **each** of `ExportDocxAsync` (`:1013`), `ExportMarkdownAsync` (`:1040`) and
`ExportTextAsync` (`:1066`), replace the provenance line with these two:

```csharp
            var manifest = await ReadManifestForExportAsync(paths, sessionId, loaded.VersionId, inner);
            var provenance = ProvenanceFor(loaded, manifest) with { ExcerptSpan = SpanLabel(rows, excerpt, loaded) };
```

- [ ] **Step 7: Update the existing provenance tests**

`tests/LocalScribe.App.Tests/MaintenanceServiceProvenanceTests.cs` calls
`MaintenanceService.ProvenanceFor(loaded)` at five sites. The new parameter is optional, so they all
still compile. Append one fact:

```csharp
    [Fact]
    public async Task An_unsealed_session_carries_no_hashes_but_still_carries_the_accuracy_tier()
    {
        // The tier comes from the model NAME through the catalog, so it is available even for a
        // session recorded long before integrity manifests existed. Hashes are not.
        var loaded = await SeedAndLoadAsync("s-unsealed",
            new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero), importedSource: null);

        var provenance = MaintenanceService.ProvenanceFor(loaded);

        Assert.Null(provenance.TranscriptSha256);
        Assert.Empty(provenance.RecordedAudio);
        Assert.Equal("", provenance.ModelAccuracy);      // "alpha-model" is not in the catalog
    }
```

- [ ] **Step 8: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~MetadataFormatTests|FullyQualifiedName~DocxRendererTests|FullyQualifiedName~MarkdownRendererWriteTests|FullyQualifiedName~PlainTextRendererWriteTests|FullyQualifiedName~MaintenanceServiceProvenanceTests"`

Expected: PASS. `MarkdownRendererWriteTests.Writes_metadata_disclaimer_and_turns` asserts the WHOLE
document against a golden string and uses `new ExportProvenance()`; every field added here defaults
to `""`/`null`/`[]`, so all four new lines are omitted and that golden must pass **unchanged**. If it
fails, a line was made unconditional - make it conditional.

- [ ] **Step 9: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/ExportProvenance.cs src/LocalScribe.Core/Projection/MetadataFormat.cs src/LocalScribe.Core/Projection/DocxRenderer.cs src/LocalScribe.Core/Projection/MarkdownRenderer.cs src/LocalScribe.Core/Projection/PlainTextRenderer.cs src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.Core.Tests/MetadataFormatTests.cs tests/LocalScribe.Core.Tests/DocxRendererTests.cs tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs tests/LocalScribe.App.Tests/MaintenanceServiceProvenanceTests.cs
git commit -m "feat(export): surface transcript/audio hashes, fabricated silence and the accuracy tier"
```

---

## Task 13: complete the export provenance - session id, timestamp, app version, weights file

All four values are already in hand at the composition point (`MaintenanceService.cs:1081-1090`) and
none of them is read. A `.docx` served on the other side currently cannot be tied back to the session
folder it came from, does not say when it was produced, and does not name the exact weights file.

> **Line numbers below are PRE-ROUND.** Task 12 inserted metadata lines into all three renderers, so
> those files have already shifted by the time this task runs. Every edit here is anchored to the
> line of code it sits beside - follow the anchor.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/ExportProvenance.cs`
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs` (metadata block),
  `MarkdownRenderer.cs`, `PlainTextRenderer.cs`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (`ProvenanceFor` + three call sites)
- Test: `DocxRendererTests.cs`, `MarkdownRendererWriteTests.cs`, `PlainTextRendererWriteTests.cs`,
  `MaintenanceServiceProvenanceTests.cs`

**Interfaces:**
- Consumes: `MaintenanceService.ProvenanceFor(LoadedProjection loaded, SessionManifest? manifest = null)`
  (Task 12) - this task CHANGES that signature to
  `ProvenanceFor(LoadedProjection loaded, TimeProvider time, SessionManifest? manifest = null)`.
  `LoadedProjection.Session.Id / .AppVersion / .WeightsFile / .Versions` (existing);
  `TranscriptVersion.WeightsFile` (existing); `MaintenanceService`'s primary-constructor `time`
  parameter (existing).
- Produces: `ExportProvenance.SessionId : string` (default `""`),
  `ExportProvenance.ExportedAtUtc : DateTimeOffset?`,
  `ExportProvenance.AppVersion : string` (default `""`),
  `ExportProvenance.WeightsFile : string?`. Task 14 adds one more member to the same record.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`:

```csharp
    [Fact]
    public void Full_provenance_renders_and_every_part_is_absent_by_default()
    {
        // Tier 1 T1-8 (spec 2026-08-05 :161-166): a document served on the other side must be
        // tie-able back to the session folder it came from, must say when it was produced, and must
        // name the exact weights file - Model alone no longer determines it (ModelFileResolver picks
        // quantized variants per backend).
        byte[] full = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance
            {
                SessionId = "2026-07-03-webex-doe-intake",
                ExportedAtUtc = new DateTimeOffset(2026, 8, 5, 14, 7, 0, TimeSpan.Zero),
                AppVersion = "0.9.0",
                WeightsFile = "ggml-small.en-q8_0.bin",
            });
        using (var doc = Open(full))
        {
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Session ID: 2026-07-03-webex-doe-intake", text);
            Assert.Contains("Exported: 2026-08-05 14:07 UTC by LocalScribe 0.9.0", text);
            Assert.Contains("Weights file: ggml-small.en-q8_0.bin", text);
        }

        byte[] bare = Render("relative", DocxPageSize.A4, new ExportOptions());
        using (var doc = Open(bare))
        {
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.DoesNotContain("Session ID:", text);
            Assert.DoesNotContain("Exported:", text);
            Assert.DoesNotContain("Weights file:", text);
        }
    }
```

Append to `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`:

```csharp
    [Fact]
    public void Full_provenance_renders_as_bullets_and_is_absent_by_default()
    {
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance
        {
            SessionId = "2026-07-03-webex-doe-intake",
            ExportedAtUtc = new DateTimeOffset(2026, 8, 5, 14, 7, 0, TimeSpan.Zero),
            AppVersion = "0.9.0",
            WeightsFile = "ggml-small.en-q8_0.bin",
        }, null, r, "relative", new ExportOptions());

        Assert.Contains("- **Session ID:** 2026-07-03-webex-doe-intake\n", md);
        Assert.Contains("- **Exported:** 2026-08-05 14:07 UTC by LocalScribe 0.9.0\n", md);
        Assert.Contains("- **Weights file:** ggml-small.en-q8_0.bin\n", md);

        string bare = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, r, "relative",
            new ExportOptions());
        Assert.DoesNotContain("Session ID", bare);
        Assert.DoesNotContain("Exported:", bare);
        Assert.DoesNotContain("Weights file", bare);
    }
```

Append to `tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs`:

```csharp
    [Fact]
    public void Full_provenance_renders_undecorated_and_is_absent_by_default()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance
        {
            SessionId = "2026-07-03-webex-doe-intake",
            ExportedAtUtc = new DateTimeOffset(2026, 8, 5, 14, 7, 0, TimeSpan.Zero),
            AppVersion = "0.9.0",
            WeightsFile = "ggml-small.en-q8_0.bin",
        }, null, [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains("Session ID: 2026-07-03-webex-doe-intake\r\n", txt);
        Assert.Contains("Exported: 2026-08-05 14:07 UTC by LocalScribe 0.9.0\r\n", txt);
        Assert.Contains("Weights file: ggml-small.en-q8_0.bin\r\n", txt);

        string bare = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());
        Assert.DoesNotContain("Session ID", bare);
        Assert.DoesNotContain("Weights file", bare);
    }
```

Append to `tests/LocalScribe.App.Tests/MaintenanceServiceProvenanceTests.cs`:

```csharp
    [Fact]
    public async Task Session_id_app_version_weights_file_and_the_export_instant_are_all_filled()
    {
        var loaded = await SeedAndLoadAsync("s-full",
            new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero), importedSource: null);
        var clock = new ManualUtcTimeProvider(new DateTimeOffset(2026, 8, 5, 14, 7, 0, TimeSpan.Zero));

        var provenance = MaintenanceService.ProvenanceFor(loaded, clock);

        Assert.Equal("s-full", provenance.SessionId);
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 14, 7, 0, TimeSpan.Zero), provenance.ExportedAtUtc);
        Assert.Equal("charlie-version", provenance.AppVersion);
        Assert.Equal("delta-weights.bin", provenance.WeightsFile);
    }
```

and extend that file's `SeedAndLoadAsync` `SessionRecord` initializer with two distinct literals (so
a field swap fails loudly rather than lining up by coincidence):

```csharp
            AppVersion = "charlie-version", WeightsFile = "delta-weights.bin",
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~DocxRendererTests|FullyQualifiedName~MaintenanceServiceProvenanceTests"`

Expected: FAIL to compile - `CS0117: 'ExportProvenance' does not contain a definition for
'SessionId'`, and `CS1503` on `ProvenanceFor(loaded, clock)`.

- [ ] **Step 3: Extend the record**

Add to `src/LocalScribe.Core/Projection/ExportProvenance.cs`, after `VersionId`:

```csharp
    /// <summary>The session folder id (Tier 1 T1-8, spec 2026-08-05 :161-166). Without it a .docx
    /// served on the other side cannot be tied back to the record it was rendered from - the title
    /// is user-editable and several sessions may share one. "" for an all-default instance, which
    /// keeps pre-feature output byte-identical.</summary>
    public string SessionId { get; init; } = "";

    /// <summary>When this document was produced, from the injected TimeProvider - never
    /// DateTime.UtcNow (Tier 1 T1-8). Rendered in UTC beside AppVersion, because "which build made
    /// this" and "when" are one question in practice. Null for an all-default instance.</summary>
    public DateTimeOffset? ExportedAtUtc { get; init; }

    /// <summary>SessionRecord.AppVersion - the build that RECORDED the session, not the one
    /// exporting it. Those differ whenever an old session is re-exported, and the recording build
    /// is the evidentiary fact (Tier 1 T1-8).</summary>
    public string AppVersion { get; init; } = "";

    /// <summary>The exact ggml file that produced this transcript version, e.g.
    /// "ggml-small.en-q8_0.bin" (Tier 1 T1-8). Model alone no longer determines it -
    /// ModelFileResolver picks quantized variants per backend. Null for crash-recovered sessions
    /// and for sessions that never transcribed a segment, where the renderers omit the line rather
    /// than print an empty one.</summary>
    public string? WeightsFile { get; init; }
```

- [ ] **Step 4: Render in all three formats**

In each renderer, insert these lines immediately **before** the `Transcript version` line, so the
document reads identity-then-content:

`DocxRenderer.Write`:

```csharp
        if (!string.IsNullOrEmpty(provenance.SessionId))
            body.AppendChild(MetaLine("Session ID", provenance.SessionId));
        if (MetadataFormat.ExportedLine(provenance) is { } exported)
            body.AppendChild(MetaLine("Exported", exported));
        body.AppendChild(MetaLine("Transcript version", MetadataFormat.VersionLine(provenance)));
        if (!string.IsNullOrEmpty(provenance.WeightsFile))
            body.AppendChild(MetaLine("Weights file", provenance.WeightsFile));
```

`MarkdownRenderer.Write`:

```csharp
        if (!string.IsNullOrEmpty(provenance.SessionId)) AppendMeta(sb, "Session ID", provenance.SessionId);
        if (MetadataFormat.ExportedLine(provenance) is { } exported) AppendMeta(sb, "Exported", exported);
        AppendMeta(sb, "Transcript version", MetadataFormat.VersionLine(provenance));
        if (!string.IsNullOrEmpty(provenance.WeightsFile))
            AppendMeta(sb, "Weights file", provenance.WeightsFile);
```

`PlainTextRenderer.Write`:

```csharp
        if (!string.IsNullOrEmpty(provenance.SessionId)) AppendMeta(sb, "Session ID", provenance.SessionId);
        if (MetadataFormat.ExportedLine(provenance) is { } exported) AppendMeta(sb, "Exported", exported);
        AppendMeta(sb, "Transcript version", MetadataFormat.VersionLine(provenance));
        if (!string.IsNullOrEmpty(provenance.WeightsFile))
            AppendMeta(sb, "Weights file", provenance.WeightsFile);
```

In each case the existing `Transcript version` line moves rather than duplicating - delete the old
one and keep only the copy above.

Add the shared formatter to `MetadataFormat`:

```csharp
    /// <summary>"2026-08-05 14:07 UTC by LocalScribe 0.9.0", or the timestamp alone when the
    /// recording build is unknown, or null when there is no timestamp at all (Tier 1 T1-8).
    /// UTC, not local: an export can cross zones between production and reading, and a bare local
    /// time in an evidentiary document is ambiguous. Null - not "" - so a renderer's `is { }`
    /// pattern omits the whole line rather than printing an empty label.</summary>
    public static string? ExportedLine(ExportProvenance p)
    {
        if (p.ExportedAtUtc is not { } at) return null;
        string stamp = at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        return string.IsNullOrEmpty(p.AppVersion) ? stamp : stamp + " by LocalScribe " + p.AppVersion;
    }
```

- [ ] **Step 5: Fill the fields**

In `MaintenanceService.ProvenanceFor`, change the signature and add the four mappings:

```csharp
    public static ExportProvenance ProvenanceFor(LoadedProjection loaded, TimeProvider time,
        SessionManifest? manifest = null)
        => new()
        {
            SessionId = loaded.Session.Id,
            ExportedAtUtc = time.GetUtcNow(),
            AppVersion = loaded.Session.AppVersion,
            // Same version?.X ?? session.X shape SessionProjectionLoader.cs:89 already uses for
            // Model/Backend: a re-transcribed version has its OWN weights file, and reporting the
            // session-level one over a v2 document would name a file that produced different text.
            WeightsFile = loaded.Session.Versions
                              .FirstOrDefault(v => v.Id == loaded.VersionId)?.WeightsFile
                          ?? loaded.Session.WeightsFile,
            VersionId = loaded.VersionId,
            Model = loaded.Header.Model,
            Backend = loaded.Header.Backend,
            // Tier 1 T1-6: the catalog's own words for how accurate this model is. Describe() never
            // throws and returns an empty Subtitle for an unknown user-dropped ggml, which the
            // renderers treat as "omit the line" rather than printing an empty claim.
            ModelAccuracy = WhisperModelCatalog.Describe(loaded.Header.Model).Subtitle,
            AudioFileName = loaded.Session.ImportedSource?.FileName,
            AudioSha256 = loaded.Session.ImportedSource?.Sha256,
            InProgress = loaded.Session.EndedAtUtc is null,
            TranscriptSha256 = manifest?.Files
                .FirstOrDefault(f => f.Name.EndsWith("transcript.jsonl", StringComparison.Ordinal))?.Sha256,
            RecordedAudio = manifest is null ? [] : RecordedLegs(manifest),
        };
```

The eight members below `Model` are byte-identical to Task 12 Step 6 - they are reproduced in full
here rather than elided, so this block can be pasted as-is without reading Task 12 first.

Then update the three export methods to pass the clock:

```csharp
            var provenance = ProvenanceFor(loaded, time, manifest) with { ExcerptSpan = SpanLabel(rows, excerpt, loaded) };
```

`time` is `MaintenanceService`'s primary-constructor parameter and is in scope in all three.

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~DocxRendererTests|FullyQualifiedName~MarkdownRendererWriteTests|FullyQualifiedName~PlainTextRendererWriteTests|FullyQualifiedName~MaintenanceServiceProvenanceTests|FullyQualifiedName~MetadataFormatTests"`

Expected: PASS. **SIX** `ProvenanceFor(loaded)` call sites in `MaintenanceServiceProvenanceTests` no
longer compile - the five pre-existing ones (`:53`, `:67`, `:68`, `:77`, `:90`) **plus the one Task 12
Step 7 added**, `An_unsealed_session_carries_no_hashes_but_still_carries_the_accuracy_tier`. Add
`, TimeProvider.System` to each; none of them asserts anything about the timestamp. Fixing only five
leaves a `CS1503` on the sixth.
`MarkdownRendererWriteTests.Writes_metadata_disclaimer_and_turns` must still pass unchanged: every
new field is empty on an all-default instance.

- [ ] **Step 7: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/ExportProvenance.cs src/LocalScribe.Core/Projection/MetadataFormat.cs src/LocalScribe.Core/Projection/DocxRenderer.cs src/LocalScribe.Core/Projection/MarkdownRenderer.cs src/LocalScribe.Core/Projection/PlainTextRenderer.cs src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.Core.Tests/DocxRendererTests.cs tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs tests/LocalScribe.App.Tests/MaintenanceServiceProvenanceTests.cs
git commit -m "feat(export): session id, export timestamp, app version and weights file in provenance"
```

---

## Task 14: disclose the human layer and the auto-suppressed duplicates

`DisplayRow.HasCorrection`/`HasPin` exist and **no renderer reads them**. `PhantomBleedDedup` removes
segments from every visible surface including exports, and the count is computed at
`TranscriptProjection.cs:49` (`projected.Count - kept.Count`) and thrown away. The user problem is
specific: a `.docx` served on the other side looks fully machine-generated but contains rewritten
lines and omits suppressed ones, and when that emerges in cross-examination the omission reads as
concealment.

> **Line numbers below are PRE-ROUND.** Tasks 12 and 13 both inserted metadata lines into all three
> renderers, so those files have shifted twice by the time this task runs. Step 6 anchors on the
> `Speakers heard` line rather than an offset - follow the anchor.

Three traps this task must respect. A count taken from `ProjectedSegment.Corrected` alone
**undercounts**: split parts are emitted with `Corrected: false` (`TranscriptProjection.cs:33`).
Manual speaker work lives in `speakers.json`, not `edits.json`. And the suppressed count cannot be
recovered from `LoadedProjection.Rows`, which are already post-dedup.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/TranscriptProjection.cs:13-16,49,85`
- Modify: `src/LocalScribe.Core/Storage/SessionProjectionLoader.cs:12-24,86,109-110`
- Modify: `src/LocalScribe.Core/Projection/ExportProvenance.cs`
- Modify: `src/LocalScribe.Core/Projection/MetadataFormat.cs`
- Modify: the three renderers' metadata blocks
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (`ProvenanceFor`)
- Create: `tests/LocalScribe.Core.Tests/HumanLayerLineTests.cs`
- Test: `tests/LocalScribe.Core.Tests/TranscriptProjectionTests.cs`, `DocxRendererTests.cs`,
  `MarkdownRendererWriteTests.cs`, `PlainTextRendererWriteTests.cs`

**Interfaces:**
- Consumes: `TranscriptProjection.Build(IReadOnlyList<TranscriptLine>, Speakers?, Edits?, SessionMeta, int)`
  (existing); `Edits.Corrections` / `.Splits` (`IReadOnlyDictionary<string, ...>`),
  `Speakers.Pinned` (`IReadOnlyDictionary<string, List<string>>`), `Speakers.Names`
  (`IReadOnlyDictionary<string, string>`), all existing; `ExportProvenance` (Tasks 12-13).
- Produces:
  - `TranscriptProjection.Build(lines, speakers, edits, meta, int sectionGapMs, out int suppressedSegmentCount)`
    - a new OVERLOAD. The five-argument form keeps its `sectionGapMs = 5000` default and delegates,
    so its byte-identical output stays guarded by `SessionProjectionLoaderTests`.
  - `LoadedProjection.SuppressedSegmentCount : int` - a 13th positional member with a `= 0` default.
  - `LocalScribe.Core.Projection.HumanLayerCounts` - `{ int Corrections; int Splits;
    int SpeakerPins; int SpeakerNames; int SuppressedDuplicates }`, all `{ get; init; }`.
  - `ExportProvenance.HumanLayer : HumanLayerCounts?` (default `null`).
  - `MetadataFormat.HumanLayerLine(HumanLayerCounts c) : string`.
  Task 15 reads `DisplayRow.HasCorrection` but nothing from here.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/HumanLayerLineTests.cs`:

```csharp
using LocalScribe.Core.Projection;

/// <summary>The human-layer disclosure line (Tier 1 T1-8, spec 2026-08-05 :161-166). A .docx served
/// on the other side must not read as fully machine-generated while carrying rewritten lines and
/// omitting auto-deduped ones.</summary>
public class HumanLayerLineTests
{
    [Fact]
    public void Every_category_is_named_and_pluralised()
        => Assert.Equal(
            "3 text corrections, 1 split turn, 5 manual speaker assignments, 2 named speakers, "
            + "4 auto-suppressed duplicate segments",
            MetadataFormat.HumanLayerLine(new HumanLayerCounts
            {
                Corrections = 3, Splits = 1, SpeakerPins = 5, SpeakerNames = 2,
                SuppressedDuplicates = 4,
            }));

    [Fact]
    public void Zero_categories_collapse_rather_than_leaving_stray_separators()
        => Assert.Equal("2 text corrections, 1 auto-suppressed duplicate segment",
            MetadataFormat.HumanLayerLine(new HumanLayerCounts
            { Corrections = 2, SuppressedDuplicates = 1 }));

    [Fact]
    public void An_untouched_transcript_says_none_rather_than_rendering_an_empty_list()
    {
        // "none" is a POSITIVE statement and it is the point: absence of the LINE means an old
        // build, absence of EDITS means this sentence. Conflating the two would let a document with
        // twelve rewritten lines look identical to one with none.
        Assert.Equal("none", MetadataFormat.HumanLayerLine(new HumanLayerCounts()));
    }
}
```

Append to `tests/LocalScribe.Core.Tests/TranscriptProjectionTests.cs`:

```csharp
    [Fact]
    public void Build_surfaces_the_number_of_segments_dedup_suppressed()
    {
        // Tier 1 T1-8 (spec 2026-08-05 :161-166): PhantomBleedDedup removes content from EVERY
        // visible surface including exports, and the delta at TranscriptProjection.cs:49 was
        // computed and discarded. An export that silently omits segments is the omission that reads
        // as concealment in cross-examination.
        // The text must clear PhantomBleedOptions' short-utterance floor (MinAutoSuppressChars 12 /
        // MinAutoSuppressTokens 3, design 2026-07-18 section 2) or NOTHING is suppressed and this
        // fact silently asserts nothing: "hello there" normalizes to 11 chars / 2 tokens and is
        // exempt by design. With no RmsDb on either line, pass 2 cannot fire at all, so it is
        // pass 1 - the LOCAL copy of a near-simultaneous identical REMOTE - that is hidden, on the
        // text-only bar (TextOnlyMinSimilarity 0.975).
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Hello there, good morning.", "Me"),
            TranscriptLine.Segment(1, TranscriptSource.Remote, 0, 1000, "Hello there, good morning.", "Them"),
        };
        // Sut() is this file's own factory (TranscriptProjectionTests.cs:8) - it builds a real
        // VocabularyProvider over an empty Vocabulary. It pairs that with NoOpDedup, so this one
        // test constructs its own projection with the REAL PhantomBleedDedup; that is the whole
        // point of the fact and the only reason not to use Sut() here.
        var projection = new TranscriptProjection(
            new VocabularyProvider(new Vocabulary(), new Dictionary<string, Matter>()),
            new PhantomBleedDedup());

        var rows = projection.Build(lines, null, null, new SessionMeta(), 5000, out int suppressed);

        Assert.Equal(1, suppressed);                     // the bled Local copy of the Remote line
        Assert.Single(rows);
        // The five-argument overload still exists and still returns the identical rows.
        Assert.Equal(rows.Count, projection.Build(lines, null, null, new SessionMeta()).Count);
    }
```

Append to `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`:

```csharp
    [Fact]
    public void The_human_layer_line_renders_when_counts_are_supplied_and_is_absent_by_default()
    {
        byte[] edited = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance
            {
                HumanLayer = new HumanLayerCounts
                { Corrections = 2, Splits = 1, SpeakerPins = 3, SuppressedDuplicates = 4 },
            });
        using (var doc = Open(edited))
            Assert.Contains(
                "Human edits: 2 text corrections, 1 split turn, 3 manual speaker assignments, 4 auto-suppressed duplicate segments",
                doc.MainDocumentPart!.Document!.Body!.InnerText);

        byte[] bare = Render("relative", DocxPageSize.A4, new ExportOptions());
        using (var doc = Open(bare))
            Assert.DoesNotContain("Human edits:", doc.MainDocumentPart!.Document!.Body!.InnerText);
    }
```

Append the mirror to `MarkdownRendererWriteTests.cs`:

```csharp
    [Fact]
    public void The_human_layer_line_renders_as_a_bullet_and_is_absent_by_default()
    {
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v,
            new ExportProvenance { HumanLayer = new HumanLayerCounts() }, null, r, "relative",
            new ExportOptions());
        Assert.Contains("- **Human edits:** none\n", md);

        Assert.DoesNotContain("Human edits",
            MarkdownRenderer.Write(h, v, new ExportProvenance(), null, r, "relative", new ExportOptions()));
    }
```

and to `PlainTextRendererWriteTests.cs`:

```csharp
    [Fact]
    public void The_human_layer_line_renders_undecorated_and_is_absent_by_default()
    {
        var rows = new[] { Turn(0, 4000, "Sam", "hello") };
        string txt = PlainTextRenderer.Write(Header(), Meta(),
            new ExportProvenance { HumanLayer = new HumanLayerCounts { Corrections = 1 } }, null,
            rows, "relative", new ExportOptions());
        Assert.Contains("Human edits: 1 text correction\r\n", txt);

        Assert.DoesNotContain("Human edits", PlainTextRenderer.Write(Header(), Meta(),
            new ExportProvenance(), null, rows, "relative", new ExportOptions()));
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~HumanLayerLineTests|FullyQualifiedName~Build_surfaces_the_number_of_segments"`

Expected: FAIL to compile - `CS0246: The type or namespace name 'HumanLayerCounts' could not be
found` and `CS1501: No overload for method 'Build' takes 6 arguments`.

- [ ] **Step 3: Surface the suppressed count from the projection**

In `src/LocalScribe.Core/Projection/TranscriptProjection.cs`, replace the `Build` signature at
`:13-16` with a delegating pair, and the dedup line at `:49`:

```csharp
    /// <summary>The five-argument form every existing caller uses. Its output byte-identity is
    /// guarded by SessionProjectionLoaderTests and the SessionWriter/ReadView tests, so the
    /// suppressed count is surfaced through a new OVERLOAD rather than by changing this return
    /// type (Tier 1 T1-8, spec 2026-08-05 :161-166).</summary>
    public IReadOnlyList<DisplayRow> Build(
        IReadOnlyList<TranscriptLine> lines, Speakers? speakers, Edits? edits, SessionMeta meta,
        int sectionGapMs = 5000)
        => Build(lines, speakers, edits, meta, sectionGapMs, out _);

    /// <summary><paramref name="suppressedSegmentCount"/> is how many segments the render-layer
    /// dedup removed - content that is invisible on EVERY surface including exports, and whose
    /// count existed only as an unnamed expression difference before this. sectionGapMs carries no
    /// default here: an out parameter cannot be optional, so this overload spells it out.</summary>
    public IReadOnlyList<DisplayRow> Build(
        IReadOnlyList<TranscriptLine> lines, Speakers? speakers, Edits? edits, SessionMeta meta,
        int sectionGapMs, out int suppressedSegmentCount)
    {
```

and at the dedup step:

```csharp
        // (4): dedup.
        var kept = _dedup.Filter(projected);
        suppressedSegmentCount = projected.Count - kept.Count;
```

- [ ] **Step 4: Carry the count on `LoadedProjection`**

In `src/LocalScribe.Core/Storage/SessionProjectionLoader.cs`, add a 13th positional member with a
default (positional records accept parameter defaults, so no existing construction site breaks):

```csharp
    string VersionId,
    /// <summary>Segments the render-layer dedup removed from Rows (Tier 1 T1-8). Defaults to 0 so
    /// any construction that predates this member still compiles and reads as "none suppressed".</summary>
    int SuppressedSegmentCount = 0);
```

and at `:86` / `:109-110`:

```csharp
        var rows = projection.Build(lines, speakers, edits, meta, settings.SectionGapMs,
            out int suppressed);
```

```csharp
        return new LoadedProjection(session, meta, lines, speakers, edits, mattersById, matterDisplays,
            startedLocal, rows, header, view, resolved, suppressed);
```

Confirm nothing else constructs it:

```bash
cd F:/LocalScribe && grep -rn "new LoadedProjection(" src tests
```

- [ ] **Step 5: Add the counts type, the formatter and the provenance field**

Add to `src/LocalScribe.Core/Projection/ExportProvenance.cs`, above `ExportProvenance`:

```csharp
/// <summary>What a PERSON changed after the machine produced this transcript, plus what the render
/// layer removed (Tier 1 T1-8, spec 2026-08-05 :161-166). Five separate counts, not one total,
/// because each maps to exactly one on-disk structure and a reader asking "was this rewritten?"
/// wants a different answer than one asking "was anything left out?".
/// Corrections and Splits are counted from edits.json SEPARATELY: a split's parts are emitted with
/// Corrected=false (TranscriptProjection.cs:33), so counting ProjectedSegment.Corrected alone
/// undercounts the human layer. SpeakerPins and SpeakerNames come from speakers.json, which
/// edits.json knows nothing about.</summary>
public sealed record HumanLayerCounts
{
    public int Corrections { get; init; }
    public int Splits { get; init; }
    /// <summary>Segments a human pinned to a specific speaker (speakers.json Pinned, summed across
    /// sources) - NOT diarisation's own Assignments, which are machine output.</summary>
    public int SpeakerPins { get; init; }
    /// <summary>Clusters a human gave a name to (speakers.json Names).</summary>
    public int SpeakerNames { get; init; }
    /// <summary>Segments PhantomBleedDedup removed from every visible surface, this document
    /// included. The one count here that is not a human act, and the one whose absence reads as
    /// concealment.</summary>
    public int SuppressedDuplicates { get; init; }
}
```

and the field on `ExportProvenance`:

```csharp
    /// <summary>Null renders NO line, which is what an all-default instance (and therefore every
    /// pre-feature golden test) produces. ProvenanceFor always supplies it for a real export, so a
    /// genuinely untouched transcript still gets "Human edits: none" - a positive statement, not
    /// silence (Tier 1 T1-8).</summary>
    public HumanLayerCounts? HumanLayer { get; init; }
```

Add the formatter to `MetadataFormat`:

```csharp
    /// <summary>"3 text corrections, 1 split turn, 4 auto-suppressed duplicate segments", or
    /// "none" (Tier 1 T1-8, spec 2026-08-05 :161-166). Zero categories collapse rather than leaving
    /// stray separators - the same .Where(non-empty) discipline VersionLine uses. Composed here so
    /// the three formats cannot word one evidentiary sentence differently.</summary>
    public static string HumanLayerLine(HumanLayerCounts c)
    {
        var parts = new List<string>();
        if (c.Corrections > 0) parts.Add(Count(c.Corrections, "text correction", "text corrections"));
        if (c.Splits > 0) parts.Add(Count(c.Splits, "split turn", "split turns"));
        if (c.SpeakerPins > 0)
            parts.Add(Count(c.SpeakerPins, "manual speaker assignment", "manual speaker assignments"));
        if (c.SpeakerNames > 0) parts.Add(Count(c.SpeakerNames, "named speaker", "named speakers"));
        if (c.SuppressedDuplicates > 0)
            parts.Add(Count(c.SuppressedDuplicates,
                "auto-suppressed duplicate segment", "auto-suppressed duplicate segments"));
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static string Count(int n, string one, string many)
        => string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? one : many)}");
```

- [ ] **Step 6: Render the line in all three formats**

Insert in each renderer immediately after the `Speakers heard` line:

`DocxRenderer.Write`:

```csharp
        if (provenance.HumanLayer is { } humanLayer)
            body.AppendChild(MetaLine("Human edits", MetadataFormat.HumanLayerLine(humanLayer)));
```

`MarkdownRenderer.Write`:

```csharp
        if (provenance.HumanLayer is { } humanLayer)
            AppendMeta(sb, "Human edits", MetadataFormat.HumanLayerLine(humanLayer));
```

`PlainTextRenderer.Write`:

```csharp
        if (provenance.HumanLayer is { } humanLayer)
            AppendMeta(sb, "Human edits", MetadataFormat.HumanLayerLine(humanLayer));
```

- [ ] **Step 7: Fill the counts**

Add to `MaintenanceService.ProvenanceFor`'s initializer:

```csharp
            HumanLayer = new HumanLayerCounts
            {
                Corrections = loaded.Edits?.Corrections.Count ?? 0,
                Splits = loaded.Edits?.Splits.Count ?? 0,
                // Pinned is source -> list of seqs, so the human act count is the SUM of the lists,
                // not the dictionary's key count (which is at most 2).
                SpeakerPins = loaded.Speakers?.Pinned.Sum(p => p.Value.Count) ?? 0,
                SpeakerNames = loaded.Speakers?.Names.Count ?? 0,
                SuppressedDuplicates = loaded.SuppressedSegmentCount,
            },
```

- [ ] **Step 8: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "Category!=Fixture"`

Expected: PASS. This task touches `TranscriptProjection` and `LoadedProjection`, which the whole
projection/read-view/export surface depends on, so run the full suite rather than a filter. Judge by
NAME. `SessionProjectionLoaderTests` guards byte-identity of transcript.md/.txt/session.txt and must
pass untouched - the five-argument `Build` returns exactly what it always did.

- [ ] **Step 9: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/TranscriptProjection.cs src/LocalScribe.Core/Storage/SessionProjectionLoader.cs src/LocalScribe.Core/Projection/ExportProvenance.cs src/LocalScribe.Core/Projection/MetadataFormat.cs src/LocalScribe.Core/Projection/DocxRenderer.cs src/LocalScribe.Core/Projection/MarkdownRenderer.cs src/LocalScribe.Core/Projection/PlainTextRenderer.cs src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.Core.Tests/HumanLayerLineTests.cs tests/LocalScribe.Core.Tests/TranscriptProjectionTests.cs tests/LocalScribe.Core.Tests/DocxRendererTests.cs tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs
git commit -m "feat(export): disclose corrections, splits, speaker edits and suppressed duplicates"
```

---

## Task 15: mark corrected turns per turn, behind a toggle defaulted ON

The counts line says how many turns were rewritten; this says WHICH. `DisplayRow.HasCorrection`
already exists and no renderer reads it.

> **Line numbers below are PRE-ROUND and are the most shifted in this plan.** Tasks 12, 13 and 14
> each inserted metadata lines into all three renderers, so by now every offset in the turn-emission
> half of these files is off by roughly the number of metadata lines added. Every edit below names
> the code it replaces - find the block by content.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/ExportOptions.cs`
- Modify: `src/LocalScribe.Core/Projection/ExportNotices.cs`
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs` - `TurnLabel` and the `(cont'd)`
  continuation block inside `Write` (`:96-107,274-281` pre-round)
- Modify: `src/LocalScribe.Core/Projection/MarkdownRenderer.cs` - the turn-emission block from
  `string label = options.IncludeTimestamps` through the continuation loop (`:118-131` pre-round)
- Modify: `src/LocalScribe.Core/Projection/PlainTextRenderer.cs` - the turn-emission block from
  `sb.Append(Nl).Append(Label(...))` through the continuation loop (`:104-121` pre-round)
- Modify: `src/LocalScribe.Core/Model/Settings.cs` (`ExportSetting`)
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs:36-47,176-181,214-224`
- Modify: `src/LocalScribe.App/ExportDialog.xaml:40`
- Test: `DocxRendererTests.cs`, `MarkdownRendererWriteTests.cs`, `PlainTextRendererWriteTests.cs`,
  `ExportDialogViewModelTests.cs`

**Interfaces:**
- Consumes: `DisplayRow.HasCorrection` (existing, computed over `Segments.Any(s => s.IsCorrected)`);
  `RowSegment` (existing); `ExportOptions` (existing); `Settings.ExportSetting` (existing).
- Produces: `ExportOptions.MarkCorrectedTurns : bool` (default `true`);
  `ExportNotices.CorrectedTurnMark : string` (`" [text corrected]"`);
  `ExportSetting.MarkCorrectedTurns : bool` (default `true`);
  `ExportDialogViewModel.MarkCorrectedTurns : bool`. Nothing later consumes these.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`:

```csharp
    [Fact]
    public void A_corrected_turn_is_marked_by_default_and_the_mark_never_reaches_the_running_head()
    {
        // Tier 1 T1-8 (spec 2026-08-05 :163-166). The mark rides on the SUFFIX run, never the name
        // run: STYLEREF "Transcript Speaker" in the page header returns that run's text verbatim,
        // so a mark inside it would appear in the running head of every page.
        var row = new DisplayRow
        {
            StartMs = 1000, EndMs = 5000, DisplayName = "Sam", Text = "Corrected text.",
            Segments = [new RowSegment(0, TranscriptSource.Local, 1000, 5000, "Corrected text.",
                "Original text.", IsCorrected: true, IsPinned: false)],
        };
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(), new ExportProvenance(), null, [row], "relative",
            DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        Assert.Contains("Sam [text corrected]:", doc.MainDocumentPart!.Document!.Body!.InnerText);
        var speakerRun = doc.MainDocumentPart.Document.Body.Descendants<Run>()
            .First(r => r.RunProperties?.RunStyle?.Val?.Value == "TranscriptSpeaker");
        Assert.Equal("Sam", speakerRun.InnerText);
    }

    [Fact]
    public void The_correction_mark_can_be_switched_off_and_an_uncorrected_turn_never_carries_it()
    {
        var corrected = new DisplayRow
        {
            StartMs = 1000, EndMs = 5000, DisplayName = "Sam", Text = "Corrected text.",
            Segments = [new RowSegment(0, TranscriptSource.Local, 1000, 5000, "Corrected text.",
                "Original text.", IsCorrected: true, IsPinned: false)],
        };
        using var off = new MemoryStream();
        DocxRenderer.Write(off, Header(), Meta(), new ExportProvenance(), null, [corrected],
            "relative", DocxPageSize.A4, new ExportOptions { MarkCorrectedTurns = false });
        off.Position = 0;
        using (var doc = WordprocessingDocument.Open(off, false))
            Assert.DoesNotContain("[text corrected]", doc.MainDocumentPart!.Document!.Body!.InnerText);

        byte[] plain = Render("relative", DocxPageSize.A4, new ExportOptions());   // Sample() rows: no Segments
        using (var doc = Open(plain))
            Assert.DoesNotContain("[text corrected]", doc.MainDocumentPart!.Document!.Body!.InnerText);
    }
```

Append to `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`:

```csharp
    [Fact]
    public void A_corrected_turn_is_marked_in_markdown_and_the_toggle_removes_it()
    {
        var row = new DisplayRow
        {
            StartMs = 1000, EndMs = 5000, DisplayName = "Sam", Text = "Corrected text.",
            Segments = [new RowSegment(0, TranscriptSource.Local, 1000, 5000, "Corrected text.",
                "Original text.", IsCorrected: true, IsPinned: false)],
        };
        var (h, v, _) = Sample();

        Assert.Contains("**[00:01] Sam [text corrected]:** Corrected text.\n",
            MarkdownRenderer.Write(h, v, new ExportProvenance(), null, [row], "relative",
                new ExportOptions()));
        Assert.Contains("**[00:01] Sam:** Corrected text.\n",
            MarkdownRenderer.Write(h, v, new ExportProvenance(), null, [row], "relative",
                new ExportOptions { MarkCorrectedTurns = false }));
    }
```

Append to `tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs`:

```csharp
    [Fact]
    public void A_corrected_turn_is_marked_in_plain_text_and_the_toggle_removes_it()
    {
        var row = new DisplayRow
        {
            StartMs = 1000, EndMs = 5000, DisplayName = "Sam", Text = "Corrected text.",
            Segments = [new RowSegment(0, TranscriptSource.Local, 1000, 5000, "Corrected text.",
                "Original text.", IsCorrected: true, IsPinned: false)],
        };

        Assert.Contains("[00:01] Sam [text corrected]: Corrected text.\r\n",
            PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null, [row],
                "relative", new ExportOptions()));
        Assert.Contains("[00:01] Sam: Corrected text.\r\n",
            PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null, [row],
                "relative", new ExportOptions { MarkCorrectedTurns = false }));
    }
```

Append to `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`. That file has **no VM factory
at all** - every one of its ~20 tests does `var (svc, _, rep) = await MakeAsync();` (`:19`, which
seeds session `"s1"` on disk) and then constructs the VM inline. Follow
`A_successful_export_persists_the_choices` (`:265-279`) exactly:

```csharp
    [Fact]
    public async Task Mark_corrected_turns_seeds_from_settings_defaults_on_and_persists()
    {
        // Defaulted ON (spec 2026-08-05 :163): the document leaves the building, and a reader who
        // discovers a rewritten line that the document did not flag reads the omission as
        // concealment. Turning it off has to be a deliberate act, like every other export choice.
        // The pickSavePath fake MUST return a real path: a null/whitespace return makes ExportAsync
        // bail before PersistChoicesAsync ever runs, and the assertion below would then pass
        // vacuously against an untouched default.
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService();
        var vm = new ExportDialogViewModel("s1", "T", svc, settings,
            _ => Path.Combine(_root, "out.txt"), _ => { }, rep, a => a())
        { Format = ExportFormat.Text };
        Assert.True(vm.MarkCorrectedTurns);          // seeded from ExportSetting's ON default

        vm.MarkCorrectedTurns = false;
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Empty(rep.Errors);                    // the export really succeeded
        Assert.Equal(1, settings.SaveCount);
        Assert.False(settings.Current.Export.MarkCorrectedTurns);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~DocxRendererTests|FullyQualifiedName~ExportDialogViewModelTests"`

Expected: FAIL to compile - `CS0117: 'ExportOptions' does not contain a definition for
'MarkCorrectedTurns'`.

- [ ] **Step 3: Add the option and the mark string**

Append to `src/LocalScribe.Core/Projection/ExportOptions.cs`:

```csharp
    /// <summary>Flag each turn a human rewrote, in the turn label (Tier 1 T1-8, spec 2026-08-05
    /// :163). Default ON, unlike IncludeSummary: a summary is an ADDITION a user must opt into,
    /// whereas this is a DISCLOSURE about content already in the document, and silence about it is
    /// what reads as concealment in cross-examination. Additive - an all-default ExportOptions over
    /// rows with no Segments (every pre-feature fixture) marks nothing, so existing output is
    /// byte-identical.</summary>
    public bool MarkCorrectedTurns { get; init; } = true;
```

Append to `src/LocalScribe.Core/Projection/ExportNotices.cs`:

```csharp
    /// <summary>Appended to the turn label of a row whose text a human corrected (Tier 1 T1-8).
    /// Leading space included so callers concatenate without composing the spacing three times.
    /// Says "text corrected", not "edited": speaker reassignments and splits are also human edits
    /// and are counted on the Human edits metadata line - this mark is only about the WORDS.</summary>
    public const string CorrectedTurnMark = " [text corrected]";
```

- [ ] **Step 4: Mark the turn in the docx**

In `src/LocalScribe.Core/Projection/DocxRenderer.cs`, add a helper beside `TurnLabel` and use it in
both label sites:

```csharp
    /// <summary>ExportNotices.CorrectedTurnMark, or "" (Tier 1 T1-8). Placed on the SUFFIX, never on
    /// the name: STYLEREF "Transcript Speaker" in the page header returns the name run's text
    /// verbatim, so a mark inside it would surface in the running head of every page.</summary>
    private static string CorrectedMark(DisplayRow row, ExportOptions options)
        => options.MarkCorrectedTurns && row.HasCorrection ? ExportNotices.CorrectedTurnMark : "";

    private static TurnLabelParts TurnLabel(DisplayRow row, ExportOptions options, string timestampsMode,
        DateTimeOffset startedAtLocal)
        => new(
            options.IncludeTimestamps
                ? "[" + TimestampFormat.Stamp(row.StartMs, timestampsMode, startedAtLocal) + "] "
                : "",
            row.DisplayName ?? "",
            CorrectedMark(row, options) + ":");
```

and in `Write`, the continuation label - the `body.AppendChild(TurnParagraph(label with { ... },
chunks[i].Text));` inside the `for (int i = 1; i < chunks.Count; i++)` loop (`:97-107` pre-round):

```csharp
                body.AppendChild(TurnParagraph(
                    label with
                    {
                        Stamp = options.IncludeTimestamps
                            ? "[" + TimestampFormat.Stamp(chunks[i].StampMs, timestampsMode,
                                header.StartedAtLocal) + "] "
                            : "",
                        // The mark repeats on every continuation for the same reason the NAME does
                        // (design 2026-08-03 section 8): a reader who flips to a mid-turn page must
                        // see both who is speaking and that the turn was rewritten.
                        Suffix = CorrectedMark(row, options) + " (cont'd):",
                    },
                    chunks[i].Text));
```

`TextColumnTwips` measures through `TurnLabel`, so the wider label is accounted for automatically.

- [ ] **Step 5: Mark the turn in markdown and plain text**

`MarkdownRenderer.Write`, replacing the block from `string label = options.IncludeTimestamps`
through the closing brace of the continuation `for` loop (`:118-131` pre-round):

```csharp
            string label = options.IncludeTimestamps
                ? "[" + TimestampFormat.Stamp(row.StartMs, timestampsMode, header.StartedAtLocal)
                    + "] " + row.DisplayName
                : row.DisplayName ?? "";
            // Tier 1 T1-8: same mark, same position and same repeat-on-continuation rule as the
            // docx, shared through ExportNotices so the three formats cannot word it differently.
            string mark = options.MarkCorrectedTurns && row.HasCorrection
                ? ExportNotices.CorrectedTurnMark : "";
            sb.Append('\n').Append("**").Append(label).Append(mark).Append(":** ")
              .Append(chunks[0].Text).Append('\n');
            for (int i = 1; i < chunks.Count; i++)
            {
                string contLabel = options.IncludeTimestamps
                    ? "[" + TimestampFormat.Stamp(chunks[i].StampMs, timestampsMode, header.StartedAtLocal)
                        + "] " + row.DisplayName
                    : row.DisplayName ?? "";
                sb.Append('\n').Append("**").Append(contLabel).Append(mark).Append(" (cont'd):** ")
                  .Append(chunks[i].Text).Append('\n');
            }
```

`PlainTextRenderer.Write`, replacing the block from `sb.Append(Nl).Append(Label(row.DisplayName,
row.StartMs, ...))` through the continuation `for` loop (`:107-112` pre-round):

```csharp
            string mark = options.MarkCorrectedTurns && row.HasCorrection
                ? ExportNotices.CorrectedTurnMark : "";
            sb.Append(Nl).Append(Label(row.DisplayName, row.StartMs, options, timestampsMode,
                header.StartedAtLocal)).Append(mark).Append(": ").Append(chunks[0].Text).Append(Nl);
            for (int i = 1; i < chunks.Count; i++)
                sb.Append(Nl).Append(Label(row.DisplayName, chunks[i].StampMs, options,
                    timestampsMode, header.StartedAtLocal))
                  .Append(mark).Append(" (cont'd): ").Append(chunks[i].Text).Append(Nl);
```

- [ ] **Step 6: Remember the choice**

In `src/LocalScribe.Core/Model/Settings.cs`, append to `ExportSetting`:

```csharp
    /// <summary>Flag rewritten turns in the exported document (Tier 1 T1-8). Additive - existing
    /// v3 files without it load at this default (the SectionGapMs precedent), so no schema
    /// bump/migration is required. Default ON: see ExportOptions.MarkCorrectedTurns.</summary>
    public bool MarkCorrectedTurns { get; init; } = true;
```

In `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs`:

- add the observable property beside `_includeSummary` (`:47`):

```csharp
    [ObservableProperty] private bool _markCorrectedTurns = true;
```

- seed it in the constructor tuple (`:37-39`), extending both sides:

```csharp
        (_format, _includeTimestamps, _includeMarkers, _extraTimestamps, _cadenceIntervalMs,
            _includeSummary, _markCorrectedTurns)
            = (e.Format, e.IncludeTimestamps, e.IncludeMarkers, e.ExtraTimestamps,
               e.CadenceIntervalMs, e.IncludeSummary, e.MarkCorrectedTurns);
```

- add it to the options build (`:176-181`):

```csharp
                IncludeSummary = IncludeSummary,
                MarkCorrectedTurns = MarkCorrectedTurns,
```

- add it to `PersistChoicesAsync` (`:216-224`):

```csharp
                    IncludeSummary = IncludeSummary,
                    MarkCorrectedTurns = MarkCorrectedTurns,
```

In `src/LocalScribe.App/ExportDialog.xaml`, after the summary checkbox (`:40`):

```xml
            <CheckBox Content="Mark corrected turns" IsChecked="{Binding MarkCorrectedTurns}" Margin="0,2"
                      ToolTip="Flags each turn whose text a person corrected. On by default - an exported transcript that hides its edits reads as concealment." />
```

- [ ] **Step 7: Run the tests and confirm they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "Category!=Fixture"`

Expected: PASS. Two goldens are the ones to watch by NAME:
`MarkdownRendererWriteTests.Writes_metadata_disclaimer_and_turns` and
`...cadence...` at `:196-199`. Both build rows without `Segments`, so `HasCorrection` is false and
neither string changes. If either fails, the mark was made unconditional.

- [ ] **Step 8: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/ExportOptions.cs src/LocalScribe.Core/Projection/ExportNotices.cs src/LocalScribe.Core/Projection/DocxRenderer.cs src/LocalScribe.Core/Projection/MarkdownRenderer.cs src/LocalScribe.Core/Projection/PlainTextRenderer.cs src/LocalScribe.Core/Model/Settings.cs src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/ExportDialog.xaml tests/LocalScribe.Core.Tests/DocxRendererTests.cs tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs
git commit -m "feat(export): mark corrected turns per turn behind an on-by-default toggle"
```

---

## Task 16: whole-round verification

**Files:**
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`

**Interfaces:**
- Consumes: every type produced above.
- Produces: nothing.

- [ ] **Step 1: Write the stacked-metadata schema test**

The metadata block gained seven optional lines this round. The OpenXML SDK accepts an invalid `pPr`
child order SILENTLY and Word then calls the file corrupt, so the densest possible header is the
shape worth validating.

Append to `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`:

```csharp
    [Fact]
    public void A_document_with_every_tier1c_metadata_line_stacked_is_schema_valid()
    {
        // Seven new optional metadata lines (Tier 1 T1-6/T1-7/T1-8) plus a marked, split turn is
        // the shape most likely to trip Word's pPr child ordering, and the SDK accepts an invalid
        // order without complaint. Every metadata line goes through MetaLine, which already applies
        // SuppressLineNumbers - a hand-built Paragraph here would silently renumber the transcript.
        var row = new DisplayRow
        {
            StartMs = 0, EndMs = 4000, DisplayName = "Sam", Text = "Corrected text.",
            Segments = [new RowSegment(0, TranscriptSource.Local, 0, 4000, "Corrected text.",
                "Original text.", IsCorrected: true, IsPinned: false)],
        };
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(),
            new ExportProvenance
            {
                SessionId = "2026-07-03-webex-doe-intake",
                ExportedAtUtc = new DateTimeOffset(2026, 8, 5, 14, 7, 0, TimeSpan.Zero),
                AppVersion = "0.9.0",
                WeightsFile = "ggml-small.en-q8_0.bin",
                Model = "small.en",
                ModelAccuracy = "Decent accuracy, English only - quick",
                TranscriptSha256 = "deadbeef",
                RecordedAudio =
                [
                    new RecordedAudioLeg
                    { FileName = "local.flac", Sha256 = "aaa", Silence = new FabricatedSilenceSummary(2, 3000) },
                    new RecordedAudioLeg { FileName = "remote.flac", Sha256 = "bbb", Silence = null },
                ],
                HumanLayer = new HumanLayerCounts
                { Corrections = 1, Splits = 1, SpeakerPins = 2, SpeakerNames = 1, SuppressedDuplicates = 3 },
                InProgress = true,
                ExcerptSpan = "00:00:00-00:00:04 of 00:30:00",
            },
            Summary(stale: "OUT OF DATE: the transcript changed after this summary was generated."),
            [row, Turn(5000, 9000, "Bob", "hi")], "relative", DocxPageSize.A4,
            new ExportOptions { TimestampIntervalMs = 15000 });

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var errors = new OpenXmlValidator().Validate(doc).ToList();

        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => e.Description + " @ " + e.Path?.XPath)));
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~A_document_with_every_tier1c_metadata_line_stacked"`

Expected: PASS. A `pPr` child-order complaint means a bespoke `Paragraph` was constructed somewhere
instead of routing through `MetaLine`; fix the element order against the ECMA-376 XSD sequence in
Global Constraints, **not** against Microsoft Learn's alphabetical `pPr` page.

- [ ] **Step 3: Full-suite run**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "Category!=Fixture"`

Expected: PASS - Core, App and Mcp fully green. Baseline was 2176 and this round adds roughly sixty
test methods, one of which (`Sustained_rtf_over_one_raises_lagging_marker_once_and_downgrades`) is
DELETED by Task 2 and replaced.

**Judge by failing test NAME, never by count.** No exact figure is given here deliberately: a
`[Theory]` contributes one method and several cases, so any headline number is wrong the moment
someone adds a `[InlineData]`. A count that drifts invites an executor to go hunting for a
"missing" test that never existed. Each task's own Step "Run" line carries the count that matters -
the count for the file it just changed.

- [ ] **Step 4: Prove the export path opens no audio**

The 2026-08-04 ruling forbids hashing recorded audio at export time. Confirm mechanically that no
export method reaches the hasher:

```bash
cd F:/LocalScribe
grep -n "HashAsync\|IncrementalHash\|SHA256\|ManifestBuilder" src/LocalScribe.App/Services/MaintenanceService.cs src/LocalScribe.Core/Projection/*.cs
```

Expected: **no matches** (the grep is case-sensitive, so the `Sha256` properties and the
`"Audio SHA-256"` labels are correctly not hits). Hashing lives only in `ManifestBuilder`, reached
from `SessionWriter.RegenerateProjectionsAsync` / `SessionWriter.ResealAsync` and from
`IntegrityVerifier` - never from an export. The export path's only manifest contact is
`ReadManifestForExportAsync`, one small `ManifestStore.ReadAsync`.

- [ ] **Step 5: Whole-branch ASCII byte-scan**

```powershell
cd F:\LocalScribe
$files = git diff --name-only master...HEAD
foreach ($f in $files) {
  if (Test-Path $f) {
    $b = [IO.File]::ReadAllBytes($f)
    $n = ($b | Where-Object { $_ -gt 127 }).Count
    if ($n -gt 0) { "NON-ASCII ($n bytes): $f" }
  }
}
"scan complete"
```

Expected output, and nothing else:

```
NON-ASCII (3 bytes): src/LocalScribe.Core/Model/Markers.cs
scan complete
```

`src/LocalScribe.Core/Model/Markers.cs` carries a **PRE-EXISTING** literal U+2192 RIGHT ARROW at
byte offset 1471, inside
`TranscriptionWeightsChanged = "transcription weights changed: {0} -> {1}"` (the arrow is the
character between the two placeholders). Task 4 adds a constant to that file, so it enters
`git diff --name-only master...HEAD` and the scan reports it. **It is expected, it is not this
round's doing, and it MUST NOT be changed.** That exact string is persisted verbatim in every
existing `transcript.jsonl`; replacing the arrow with `->` would silently alter an evidentiary
string and break the existing weights-marker assertions. Converting it to a `\u2192` escape is a
separate, explicitly-scoped change that would have to prove byte-identity of the produced string
first - it is out of scope here.

Markdown under `docs/` is exempt; **other source files are not**. Any `.cs` file OTHER than
`Markers.cs` reporting non-ASCII means a `\uXXXX` escape was converted to a literal glyph - restore
it. The only escape this round introduces is `\u00B7` in
`RecordingConsoleViewModel.AccuracySuffix`.

- [ ] **Step 6: Confirm line endings survived**

```powershell
cd F:\LocalScribe
git diff --stat master...HEAD
git diff --check master...HEAD
```

Expected: no whitespace errors, and a plausible changed-file list - no file showing as wholly
rewritten, which would indicate a CRLF/LF flip.

- [ ] **Step 7: Commit**

```bash
cd F:/LocalScribe
git add tests/LocalScribe.Core.Tests/DocxRendererTests.cs
git commit -m "test(export): schema validation with every tier-1c metadata line stacked"
```

---

## Post-Implementation

Once all 16 tasks are green:

1. **Request code review** - use `superpowers:requesting-code-review`.
2. **Do NOT merge before the smoke.** Two of the spec's section-7 smoke items are assigned to this
   plan and a static suite cannot settle either.
3. **Smoke checklist for the user:**
   - **The one that matters most (spec `:213-218`):** record a real Webex call, then check which
     model `session.json` says it used and confirm the transcript's FIRST line is
     `transcription engine: <model> (<BACKEND>), <tier>`. Import the same audio at
     `large-v3-turbo` and read both transcripts side by side. Judge whether the divergence is
     material to a solicitor - the ruling that the live cap stays is revisited only on that evidence.
   - Before pressing Record, confirm the ready card's engine chip names the accuracy tier, and hover
     it: the tooltip must point at re-transcription as the remedy, not merely restate the cap.
   - After the recording finalizes, open `manifest.json` in the session folder: every retained leg
     must carry a `sha256`, `sampleRate`, `fabricatedSilenceKnown: true`, and at least one
     `end-pad` span. Confirm the numbers look sane (an `end-pad` of a few seconds, not minutes).
   - Correct a line in the read view, then re-run **Verify integrity**: it must still PASS, because
     the overlay write reseals through `RegenerateProjectionsAsync`.
   - Re-transcribe that session to create a v2, switch the active version back to v1 in the read
     view, then **Verify integrity** on BOTH versions: both must PASS. A `session.json CHANGED`
     here means the reseal at `SetActiveVersionCoreAsync` (Task 9 Step 5) did not land, and the
     command is inventing a tamper verdict on an untouched session.
   - Open an OLD session recorded before this round and run **Verify integrity**: it must say it has
     no seal (not a pass), and running it twice must not change `session.json`'s modified timestamp.
   - Edit `transcript.jsonl` by hand in Notepad, then **Verify integrity**: it must report
     `transcript.jsonl CHANGED` and name it. Undo the edit afterwards.
   - Export that session as `.docx` and read the metadata block in real Word: session id, exported
     stamp, weights file, model accuracy, transcript hash, both audio hashes with their silence
     clauses, and `Human edits: 1 text correction, ...`. Confirm the corrected turn carries
     `[text corrected]` and that **the running head on page 2+ shows the speaker name WITHOUT the
     mark**, and that the transcript's line numbers still start at 1 on the first turn.
   - Export the same session as `.md` and `.txt` and confirm all three carry the same lines.
4. **Follow-ups deliberately NOT in this plan:** raising the live model ceiling (ruled out),
   hashing recorded audio at export time (ruled out), and a redacted disclosure copy (Tier 2).
