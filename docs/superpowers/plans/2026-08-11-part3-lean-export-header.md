# Part 3 — Lean Export Header Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut the exported transcript header from ~22 possible lines to seven, move human-edit disclosure into the disclaimer as prose, and delete the per-turn `[text corrected]` mark end to end.

**Architecture:** Three renderers (`DocxRenderer`, `MarkdownRenderer`, `PlainTextRenderer`) compose the same metadata block in parallel from `ExportProvenance` + `SessionTextView`, using shared string builders in `MetadataFormat` and shared constants in `ExportNotices`. This plan changes the shared layer first (Tasks 1-2), drives each renderer from its own tests (Tasks 3-5), deletes the corrected-turn mark across Core + App + XAML (Task 6), then sweeps the code the trim made unreachable (Tasks 7-8) and updates the spec (Task 9).

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), xUnit, `DocumentFormat.OpenXml` 3.5.1, WPF + CommunityToolkit.Mvvm (`[ObservableProperty]` source generators).

**Source spec:** `docs/superpowers/specs/2026-08-11-import-resilience-line-insertion-lean-export-design.md` Part 3 (§3.1-§3.5).
**Handoff prompt:** `docs/superpowers/specs/NEXT-part3-lean-export-header-prompt.md`.

---

## Global Constraints

- **ASCII only in source files** (`CONTRIBUTING.md:118`). Non-ASCII in `src/` must be `\uXXXX` escapes. *Correction to the handoff: there is no automated byte-scan gate — I searched `build.ps1` and `.github/workflows/ci.yml` and neither scans for non-ASCII. It is a written convention enforced by review. Follow it anyway; the existing code does (`ExportNotices.cs:5-6`, `MarkdownRenderer.cs:6`). All new strings in this plan are pure ASCII.*
- **British spelling in user-facing strings and docs** (`CONTRIBUTING.md:117`). The two disclaimer variants below are owner-dictated verbatim and contain no en-GB/en-US divergence.
- **Update `docs\specs\localscribe-specs.md` when you change anything it documents** (`CONTRIBUTING.md:114-116`). Task 9 does this.
- **Gate:** `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`. **Measured baseline on `master` @ `a6eba76`: 2587 passed, 0 failed** — Core 1411, App 1170, Mcp 6, ~65 s wall clock. Never `--no-build` from a stale tree (`BuildVersionTests` derives the expected version from the props file and fails against stale binaries).
- **Standing criterion from Part 1:** also run the gate with `LOCALSCRIBE_MODELS` pointed at a *fresh empty directory*. Two Part 1 defects passed only because this machine holds every ladder rung; CI fetches no models. Task 10 does this once for the whole branch.
- **Warning baseline:** `DocxRendererTests.cs` already emits 7 `CS8602` warnings (lines 805, 824, 847, 884, 905, 925, 939). Do not add more; do not "fix" these as a side quest.
- **Do not run `LocalScribe.App.exe` while building** — it locks `Core.dll` and the build fails with `MSB3027`.
- **Branch off `master`; never commit to `master` directly.** Suggested name: `feat/part3-lean-export-header`.
- **Do not touch `.claude/worktrees/`.** Two stale worktrees (`feat+tier1a-...`, `feat+tier1d-...`) contain byte-identical copies of every file this plan edits. `Glob`/`grep` will surface them. Only edit paths under `F:\LocalScribe\src`, `F:\LocalScribe\tests` and `F:\LocalScribe\docs`.
- **Evidentiary rule (locked):** row text is emitted verbatim — never filtered, escaped, or reworded. This plan changes only the *header*, never the body text.
- **`session.txt` is OUT of scope.** It is rendered by a different class (`SessionTextRenderer`) and legitimately keeps its `Participants:` line. `RendererTests.cs:89` and `SessionProjectionLoaderTests.cs:73` pin it. **A repo-wide search-and-replace on "Participants" will break both.**
- **The save-time `transcript.md` / `transcript.txt` one-line header is OUT of scope.** That is `MarkdownRenderer.Render(...)` / `PlainTextRenderer.Render(...)`, a different method from `Write(...)`. It keeps `App`, the date, the duration and the engine segment.

---

## Owner decisions — settled, do not re-litigate

1. **`Speakers heard` replaces `Participants`.** Derived from the rows, so it cannot drift from the body. A silent attendee disappearing from the document is accepted (spec §3.5).
2. **`Source:` renders for IMPORTS ONLY.** A recorded call's legs are the hardcoded constants `local.flac` / `remote.flac` (`StoragePaths.AudioFile`, `StoragePaths.cs:72-77` — verified), so printing them tells a reader nothing. A recorded session has no `Source` line, the way a session with no matter has no `Matter` line. **This overrides spec §3.3 trap 2**, which said `Source` must cover both.
3. **Human-edit disclosure moves into the disclaimer as prose**, in two variants so it never overclaims.
4. **Suppressed duplicates print only when non-zero**, as `Omitted: N duplicate turns (echo)`. A machine omission, not a human act, so the disclaimer does not cover it — and it is the one count the spec says would read as concealment if hidden.
5. **`[text corrected]` is deleted entirely**, not defaulted off.
6. **Technical provenance is not lost, it moves.** The `.zip` bundle is a verbatim folder copy (`SessionArchiver.AddSessionFolderAsync:18-48`), so `session.json`, `meta.json` and `manifest.json` still carry model, backend, weights file and every hash. `Verify integrity` is untouched.

## Plan-level decisions (made here, flagged for review)

These were left open by the spec and the handoff. Each is called out at its task.

- **D-A — `Matter(s)` keeps its label.** The spec's sample block writes `Matter:` (singular) but its own kept-list and the handoff both write `Matter(s)`, and the code has always emitted `Matter(s)`. Renaming would churn tests for no stated benefit. **Keeping `Matter(s):`.**
- **D-B — The excerpt/in-progress ordering asymmetry is PRESERVED, not fixed.** `.docx` emits the in-progress line *before* the `Excerpt:` line (`DocxRenderer.cs:84-90`); `.md`/`.txt` emit `Excerpt:` *before* the in-progress notice (`MarkdownRenderer.cs:91-101`, `PlainTextRenderer.cs:92-98`). This predates Part 3 and is out of the owner's stated scope. Every format keeps its existing notice order exactly; only the labelled metadata run above it changes.
- **D-C — `Omitted:` is the LAST labelled metadata line**, immediately after `Description` and before each format's notice block. It belongs in the run a reader scans by label, and this placement holds identically in all three formats.
- **D-D — `Omitted:` pluralises.** The owner's literal is `Omitted: N duplicate turns (echo)`; house discipline pluralises counts (`MetadataFormat.Count`, and `MetadataFormatTests.One_fabricated_span_reads_singular`). So `Omitted: 1 duplicate turn (echo)` / `Omitted: 4 duplicate turns (echo)`. **Note the nuance:** `SuppressedDuplicates` counts *segments* (`loaded.SuppressedSegmentCount`), and the old line said "auto-suppressed duplicate segments". The owner chose reader-facing plain language ("turns") over internal precision. Owner's wording wins.
- **D-E — The disclaimer's "edited" variant is chosen from the four HUMAN counts only** (`Corrections`, `Splits`, `SpeakerPins`, `SpeakerNames`) — **never** `SuppressedDuplicates`, which is a machine act. A session whose only non-zero count is `SuppressedDuplicates` must not claim it was "reviewed and corrected by a person". A null `HumanLayer` (every all-default `ExportProvenance`, i.e. every renderer unit test) selects the untouched variant.
- **D-F — `ExportProvenance` IS trimmed** (Task 8). See that task for the reasoning and for how to drop it if the reviewer disagrees. This is the choice the handoff asked to be made explicitly.

## Anchor verification — three corrections to the handoff

Checked against `master` @ `a6eba76`.

| Handoff claim | Verified? |
|---|---|
| Model name welded into `Transcript version`, emitted unconditionally | **Confirmed.** `MetadataFormat.cs:39-42`; rendered with no null guard at `MarkdownRenderer.cs:75`, `PlainTextRenderer.cs:75`, `DocxRenderer.cs:67`. |
| Excerpt line ordered differently across renderers | **Confirmed.** `DocxRenderer.cs:84-90` vs `MarkdownRenderer.cs:91-101` / `PlainTextRenderer.cs:92-98`. |
| Docx emits in-progress + excerpt notices twice | **Confirmed.** Body `:84`/`:88-89`, running header part `:165-170`. Both notices are KEPT by Part 3, so this trap only bites if you "tidy" them. Don't. |
| `CorrectedMark` called from two places | **Confirmed.** Defined `DocxRenderer.cs:294-295`; called at `:304` (label suffix) and `:122` (continuation suffix). |
| `ExportDialog.xaml:64` binding must be deleted by hand | **Confirmed** — and Task 6 adds a `XamlHygieneTests` guard so the suite catches it next time. |
| `DisplayRow.HasCorrection` does not become dead | **Confirmed.** `ReadViewWindow.xaml:571` binds it; `ReadViewViewModelTests.cs:388` asserts it. `DisplayRow.cs:16` stays. |
| `HumanLayerCounts` must survive | **Confirmed.** Only `MetadataFormat.HumanLayerLine` goes. |
| No settings migration needed | **Confirmed.** `ExportSetting.MarkCorrectedTurns` (`Settings.cs:113`) is additive with no schema bump. |
| `DocxRendererTests.cs:679` asserts `DoesNotContain("Audio:")` | **Confirmed and re-read.** It is inside `Audio_provenance_renders_for_an_imported_session_and_is_absent_otherwise` (`:662`). Under decision 2 the premise holds, but the assertion becomes *vacuous* (no `Audio:` label exists anywhere afterwards). Task 5 replaces it with `DoesNotContain("Source:")`, which is the assertion that actually has teeth. |
| **`AppendEngine` becomes dead** | **WRONG — it does not.** `AppendEngine` is called only from the *save-time* `Render(...)` (`MarkdownRenderer.cs:19`, `PlainTextRenderer.cs:19`). `Write(...)` never calls it. It stays, untouched. |
| **"a byte-scan gate fails the build"** | **WRONG — no such gate exists.** Neither `build.ps1` nor `.github/workflows/ci.yml` scans for non-ASCII. It is a `CONTRIBUTING.md` convention. Still follow it. |
| **`MaintenanceServiceVersionsTests.cs:210` is in the blast radius** | **Only nominally.** The assertion is `Assert.DoesNotContain("Transcript version", footer1)` against the docx *footer*, which never carried it. It keeps passing unchanged. No edit needed. |

---

## File Structure

**Core — modify:**
- `src/LocalScribe.Core/Projection/ExportNotices.cs` — two disclaimer constants replace one; `CorrectedTurnMark` deleted.
- `src/LocalScribe.Core/Projection/MetadataFormat.cs` — `DisclaimerFor` + `OmittedLine` added; `HumanLayerLine` deleted (Task 7); `VersionLine` / `ExportedLine` / `RecordedAudioLines` deleted (Task 8).
- `src/LocalScribe.Core/Projection/ExportOptions.cs` — `MarkCorrectedTurns` deleted.
- `src/LocalScribe.Core/Projection/ExportProvenance.cs` — trimmed (Task 8).
- `src/LocalScribe.Core/Projection/MarkdownRenderer.cs` — `Write` header block; `Render` untouched.
- `src/LocalScribe.Core/Projection/PlainTextRenderer.cs` — `Write` header block; `Render` untouched.
- `src/LocalScribe.Core/Projection/DocxRenderer.cs` — `Write` header block, `CorrectedMark` deleted, two label sites.
- `src/LocalScribe.Core/Model/Settings.cs` — `ExportSetting.MarkCorrectedTurns` deleted.

**App — modify:**
- `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs` — property + 3 wiring sites deleted.
- `src/LocalScribe.App/ExportDialog.xaml` — the `Mark corrected turns` CheckBox deleted.
- `src/LocalScribe.App/Services/MaintenanceService.cs` — `ProvenanceFor` trimmed, `RecordedLegs` + `ReadManifestForExportAsync` deleted (Task 8).

**Tests — modify:**
- `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`, `PlainTextRendererWriteTests.cs`, `DocxRendererTests.cs`, `MetadataFormatTests.cs`, `ExportNoticesTests.cs`
- `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`, `XamlHygieneTests.cs`, `MaintenanceServiceProvenanceTests.cs`

**Tests — delete:**
- `tests/LocalScribe.Core.Tests/HumanLayerLineTests.cs` (Task 7)

**Docs — modify:**
- `docs/specs/localscribe-specs.md` (Task 9)

---

## The target header

Seven possible labelled lines, then each format's existing notice block, then the disclaimer.

```
Interview recording 12 March

Date: 2026-03-12 13:57 - 14:24 (27 min)
Matter(s): Doe v Roe (2026/014)
Speakers heard: Speaker A, Speaker B, Speaker C
Medium: Other
Source: interview.mp4
Description: Second interview, custody suite.
Omitted: 4 duplicate turns (echo)

This transcript was generated by automated speech recognition and reviewed and
corrected by a person. It may contain errors and is not a certified record.
```

Emission rules, identical in all three formats:

| Line | Condition |
|---|---|
| title | always |
| `Date` | always |
| `Matter(s)` | always; `(none)` when empty |
| `Speakers heard` | only when at least one non-marker row has a `DisplayName` |
| `Medium` | always |
| `Source` | only when `provenance.AudioFileName` is non-empty (imports) |
| `Description` | only when `meta.Description` is non-empty |
| `Omitted` | only when `provenance.HumanLayer?.SuppressedDuplicates > 0` |

Then, **preserving each format's existing order (D-B)**:
- `.md` / `.txt`: `Excerpt:` line → in-progress notice → excerpt notice → summary → disclaimer
- `.docx`: in-progress line → `Excerpt:` line + excerpt notice → summary → disclaimer

---

## Task 1: Two disclaimer variants and the selector

Adds the strings and the chooser. No renderer is wired yet, so the whole suite stays green except the one `ExportNoticesTests` assertion this task rewrites.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/ExportNotices.cs:9-12`
- Modify: `src/LocalScribe.Core/Projection/MetadataFormat.cs` (add after `SpeakersHeard`, ~line 35)
- Test: `tests/LocalScribe.Core.Tests/ExportNoticesTests.cs:11-16`
- Test: `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs` (add methods)

**Interfaces:**
- Consumes: `HumanLayerCounts` (`ExportProvenance.cs:35-48`) — existing, unchanged.
- Produces:
  - `ExportNotices.Disclaimer : string` (const) — reworded? **No.** Byte-identical to today's string. It becomes the *untouched* variant.
  - `ExportNotices.DisclaimerEdited : string` (const) — new.
  - `MetadataFormat.DisclaimerFor(HumanLayerCounts? counts) : string` — returns one of the two.

- [ ] **Step 1: Write the failing tests**

Replace `tests/LocalScribe.Core.Tests/ExportNoticesTests.cs:11-16` (the single `Disclaimer_is_byte_identical_to_the_round_1_string` fact) with two facts:

```csharp
    [Fact]
    public void Untouched_disclaimer_is_byte_identical_to_the_round_1_string()
        => Assert.Equal(
            "This transcript was generated by automated speech recognition and may contain errors. "
            + "It is not a certified record.",
            ExportNotices.Disclaimer);

    [Fact]
    public void Edited_disclaimer_is_the_locked_wording()
        // Part 3: human-edit disclosure moved off the "Human edits" counts line and into the
        // disclaimer as prose. A reader cannot tell the text is conditional; it is simply accurate
        // in both cases, and it never claims a person reviewed a transcript nobody touched.
        => Assert.Equal(
            "This transcript was generated by automated speech recognition and reviewed and "
            + "corrected by a person. It may contain errors and is not a certified record.",
            ExportNotices.DisclaimerEdited);
```

Append to `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs`, before the closing brace:

```csharp
    [Fact]
    public void DisclaimerFor_null_counts_reads_as_untouched()
        // Every all-default ExportProvenance (i.e. every renderer unit test) lands here.
        => Assert.Equal(ExportNotices.Disclaimer, MetadataFormat.DisclaimerFor(null));

    [Fact]
    public void DisclaimerFor_all_zero_counts_reads_as_untouched()
        => Assert.Equal(ExportNotices.Disclaimer,
            MetadataFormat.DisclaimerFor(new HumanLayerCounts()));

    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, 0, 0, 1)]
    public void Any_single_human_act_flips_the_disclaimer_to_the_edited_variant(
        int corrections, int splits, int pins, int names)
        // All four are human acts that change what the document says or who it attributes speech
        // to. Naming a speaker is not a text correction, but it IS a person's judgement rendered
        // into the body, and understating that is the overclaim of purity the variant exists to
        // prevent.
        => Assert.Equal(ExportNotices.DisclaimerEdited,
            MetadataFormat.DisclaimerFor(new HumanLayerCounts
            {
                Corrections = corrections, Splits = splits,
                SpeakerPins = pins, SpeakerNames = names,
            }));

    [Fact]
    public void Suppressed_duplicates_alone_never_claim_a_person_reviewed_the_transcript()
    {
        // PhantomBleedDedup is a MACHINE omission. A document whose only non-zero count is
        // SuppressedDuplicates was touched by nobody, and saying otherwise would be a false
        // claim in a document served on the other side. The Omitted: line discloses it instead.
        Assert.Equal(ExportNotices.Disclaimer,
            MetadataFormat.DisclaimerFor(new HumanLayerCounts { SuppressedDuplicates = 9 }));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~ExportNoticesTests|FullyQualifiedName~MetadataFormatTests"`
Expected: BUILD FAILURE — `error CS0117: 'ExportNotices' does not contain a definition for 'DisclaimerEdited'` and `error CS0117: 'MetadataFormat' does not contain a definition for 'DisclaimerFor'`.

- [ ] **Step 3: Add the second constant**

In `src/LocalScribe.Core/Projection/ExportNotices.cs`, replace lines 9-12 with:

```csharp
    /// <summary>Non-optional on every exported transcript (design 3.3). The variant for a
    /// transcript NO PERSON has touched. Part 3 (design 2026-08-11 section 3.3) replaced the
    /// "Human edits" counts line with these two variants: the counts were four numbers a reader
    /// had to interpret, the prose is a sentence they can quote. Choose between them with
    /// MetadataFormat.DisclaimerFor - never by hand, or the two call sites will drift.</summary>
    public const string Disclaimer =
        "This transcript was generated by automated speech recognition and may contain errors. "
        + "It is not a certified record.";

    /// <summary>The variant for a transcript a person corrected, split, pinned or renamed
    /// (design 2026-08-11 section 3.3). This is what carries the Tier 1 T1-8 anti-concealment
    /// duty now that the counts line is gone. It must NEVER be selected for a transcript whose
    /// only change was machine dedup - see MetadataFormat.DisclaimerFor.</summary>
    public const string DisclaimerEdited =
        "This transcript was generated by automated speech recognition and reviewed and "
        + "corrected by a person. It may contain errors and is not a certified record.";
```

- [ ] **Step 4: Add the selector**

In `src/LocalScribe.Core/Projection/MetadataFormat.cs`, insert after `SpeakersHeard` (after line 35):

```csharp
    /// <summary>Pick the disclaimer variant from the human-layer counts (design 2026-08-11
    /// section 3.3). SuppressedDuplicates is DELIBERATELY excluded: PhantomBleedDedup is a machine
    /// act, and a document whose only non-zero count is that one was reviewed by nobody. Claiming
    /// otherwise in a document served on the other side is a false statement about who touched the
    /// evidence - the Omitted: line discloses the dedup instead. Null counts (every all-default
    /// ExportProvenance) read as untouched, which keeps pre-feature output unchanged.</summary>
    public static string DisclaimerFor(HumanLayerCounts? counts)
        => counts is { } c
           && (c.Corrections > 0 || c.Splits > 0 || c.SpeakerPins > 0 || c.SpeakerNames > 0)
            ? ExportNotices.DisclaimerEdited
            : ExportNotices.Disclaimer;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~ExportNoticesTests|FullyQualifiedName~MetadataFormatTests"`
Expected: PASS.

- [ ] **Step 6: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: 2587 passed, 0 failed. (Net test count is unchanged: `ExportNoticesTests` gained one fact, `MetadataFormatTests` gained seven — 2587 becomes 2595. Confirm the *failed* count is 0 and record the new total.)

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Projection/ExportNotices.cs src/LocalScribe.Core/Projection/MetadataFormat.cs tests/LocalScribe.Core.Tests/ExportNoticesTests.cs tests/LocalScribe.Core.Tests/MetadataFormatTests.cs
git commit -m "feat(export): two disclaimer variants chosen from the human-layer counts"
```

---

## Task 2: The `Omitted:` duplicate-turns line

**Files:**
- Modify: `src/LocalScribe.Core/Projection/MetadataFormat.cs` (add after `DisclaimerFor`)
- Test: `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs`

**Interfaces:**
- Consumes: `HumanLayerCounts.SuppressedDuplicates` (`ExportProvenance.cs:47`); the existing private `MetadataFormat.Count(int, string, string)` helper (`MetadataFormat.cs:100-101`).
- Produces: `MetadataFormat.OmittedLine(HumanLayerCounts? counts) : string?` — the VALUE half only (`"4 duplicate turns (echo)"`); each renderer supplies the `Omitted` label through its own `AppendMeta`/`MetaLine`. `null` means "emit no line", matching the `ExportedLine` precedent at `MetadataFormat.cs:49`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs`:

```csharp
    [Fact]
    public void OmittedLine_is_null_when_nothing_was_suppressed()
    {
        // Silent on the common case, and silent on a single-leg import, which suppresses nothing.
        Assert.Null(MetadataFormat.OmittedLine(null));
        Assert.Null(MetadataFormat.OmittedLine(new HumanLayerCounts()));
        Assert.Null(MetadataFormat.OmittedLine(new HumanLayerCounts { Corrections = 3 }));
    }

    [Fact]
    public void OmittedLine_names_the_count_and_the_reason()
        // The one count the spec calls concealment if hidden (Tier 1 T1-8): a reader can tell
        // hiding happened and how often, even though the hidden text is not in the document.
        => Assert.Equal("4 duplicate turns (echo)",
            MetadataFormat.OmittedLine(new HumanLayerCounts { SuppressedDuplicates = 4 }));

    [Fact]
    public void One_omitted_turn_reads_singular()
        => Assert.Equal("1 duplicate turn (echo)",
            MetadataFormat.OmittedLine(new HumanLayerCounts { SuppressedDuplicates = 1 }));
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~MetadataFormatTests"`
Expected: BUILD FAILURE — `error CS0117: 'MetadataFormat' does not contain a definition for 'OmittedLine'`.

- [ ] **Step 3: Implement**

In `src/LocalScribe.Core/Projection/MetadataFormat.cs`, insert after `DisclaimerFor`:

```csharp
    /// <summary>"4 duplicate turns (echo)", or null when nothing was suppressed (design
    /// 2026-08-11 section 3.3). The VALUE half only - each renderer supplies the "Omitted" label
    /// through its own metadata-line helper, the ExportedLine precedent. Null rather than "" so a
    /// renderer's `is { }` pattern omits the whole line rather than printing an empty label.
    ///
    /// This is the one disclosure Part 3 KEPT off the disclaimer: PhantomBleedDedup is a machine
    /// omission, so "reviewed and corrected by a person" would not cover it, and the spec calls
    /// its absence concealment. Says "turns", not "segments" (the unit SuppressedDuplicates
    /// actually counts): the owner chose the reader's word over the internal one, and a reader of
    /// an exported document has never heard of a segment.</summary>
    public static string? OmittedLine(HumanLayerCounts? counts)
        => counts is { SuppressedDuplicates: > 0 } c
            ? Count(c.SuppressedDuplicates, "duplicate turn (echo)", "duplicate turns (echo)")
            : null;
```

> `Count` is declared below `HumanLayerLine` at `MetadataFormat.cs:100`. C# does not care about declaration order within a type, so no move is needed. **Task 7 deletes `HumanLayerLine` but must NOT delete `Count`** — `OmittedLine` is its remaining caller.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~MetadataFormatTests"`
Expected: PASS.

- [ ] **Step 5: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: 0 failed.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Projection/MetadataFormat.cs tests/LocalScribe.Core.Tests/MetadataFormatTests.cs
git commit -m "feat(export): Omitted line discloses auto-suppressed duplicate turns"
```

---

## Task 3: The markdown header (the driver)

Start here, as the handoff advises: the two full-document `Assert.Equal` goldens are the only place the whole header shape is pinned in one string, and rewriting them first makes the target unambiguous.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/MarkdownRenderer.cs:64-91` (the header block inside `Write`)
- Modify: `src/LocalScribe.Core/Projection/MarkdownRenderer.cs:126` (the disclaimer line)
- Test: `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`

**Interfaces:**
- Consumes: `MetadataFormat.DateLine`, `MetadataFormat.SpeakersHeard`, `MetadataFormat.DisclaimerFor` (Task 1), `MetadataFormat.OmittedLine` (Task 2), `ExportProvenance.AudioFileName` / `.HumanLayer` / `.ExcerptSpan` / `.InProgress`.
- Produces: nothing new. `MarkdownRenderer.Write`'s signature is unchanged.

- [ ] **Step 1: Rewrite the two goldens**

In `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`, replace the `expected` string in `Writes_metadata_disclaimer_and_turns` (lines 84-101) with:

```csharp
        string expected =
            "# Weekly Sync\n" +
            "\n" +
            "- **Date:** 2026-06-30 14:32 - 15:09 (37 min)\n" +
            "- **Matter(s):** Acme (2026-014)\n" +
            "- **Speakers heard:** Sam, Bob\n" +
            "- **Medium:** Teams\n" +
            "\n" +
            "_" + ExportNotices.Disclaimer + "_\n" +
            "\n" +
            "**[00:01] Sam:** Morning everyone.\n" +
            "\n" +
            "_[audio device changed]_\n" +
            "\n" +
            "**[00:38] Bob:** Question on tokens.\n";
```

And the `expected` string in `Cadence_splits_a_long_turn_into_named_contd_continuation_paragraphs` (lines 183-198) with:

```csharp
        string expected =
            "# Weekly Sync\n" +
            "\n" +
            "- **Date:** 2026-06-30 14:32 - 15:09 (37 min)\n" +
            "- **Matter(s):** Acme (2026-014)\n" +
            "- **Speakers heard:** Sam\n" +
            "- **Medium:** Teams\n" +
            "\n" +
            "_" + ExportNotices.Disclaimer + "_\n" +
            "\n" +
            "**[00:01] Sam:** First part. Second part.\n" +
            "\n" +
            "**[00:16] Sam (cont'd):** Third part.\n";
```

> `Sample()` supplies no `Description`, no `AudioFileName` and a default `ExportProvenance` (so `HumanLayer` is null) — hence four labelled lines and the untouched disclaimer.

- [ ] **Step 2: Rewrite the surviving per-line tests**

Replace `Metadata_block_carries_version_and_speakers_heard` (lines 105-120) with:

```csharp
    [Fact]
    public void Metadata_block_carries_speakers_heard_and_no_transcript_version()
    {
        // Part 3 (design 2026-08-11 section 3.3): the model name was welded into "Transcript
        // version" as "v2 <dot> large-v3-turbo <dot> cuda", so removing the model means removing
        // the whole line - there is nothing to edit around. The engine still rides in
        // session.json inside the .zip bundle; the reading copy stops naming it.
        string md = Write(new ExportOptions(), new ExportProvenance
        {
            VersionId = "v2-large-v3-turbo-2026-08-01",
            Model = "large-v3-turbo",
            Backend = "cuda",
        });

        Assert.Contains("- **Speakers heard:** Sam, Bob\n", md);
        Assert.DoesNotContain("Transcript version", md);
        Assert.DoesNotContain("large-v3-turbo", md);
    }
```

Replace `Empty_matters_and_participants_render_as_none` (lines 145-160) with:

```csharp
    [Fact]
    public void Empty_matters_render_as_none_and_participants_are_gone()
    {
        var h = new TranscriptHeader("T", "Webex", Started, 60000, "base.en", "CPU");
        var v = new SessionTextView("T", Array.Empty<string>(), new[] { "Declared Person" },
            Started, null, 60000, "Webex", "Initial interview.", null);
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, Array.Empty<DisplayRow>(),
            "relative", new ExportOptions());

        Assert.Contains("- **Matter(s):** (none)\n", md);
        Assert.Contains("- **Description:** Initial interview.\n", md);   // present only when set
        // Speakers heard replaces Participants (design 2026-08-11 section 3.3). The roster is
        // populated here and there are NO rows, so the accepted trade is visible in one test: a
        // declared attendee who never speaks disappears from the exported document.
        Assert.DoesNotContain("Participants", md);
        Assert.DoesNotContain("Declared Person", md);
        Assert.DoesNotContain("Speakers heard", md);   // no rows -> no line, not an empty one
    }
```

- [ ] **Step 3: Replace the two provenance tests with one absent-set test and one Source test**

Delete `Recorded_audio_and_transcript_hashes_render_as_bullets_and_are_absent_by_default` (lines 377-398) and `Full_provenance_renders_as_bullets_and_is_absent_by_default` (lines 399-421). Replace both with:

```csharp
    [Fact]
    public void Every_removed_provenance_line_is_absent_even_when_the_field_is_populated()
    {
        // The exact ABSENT set (design 2026-08-11 section 3.3). Populating every field and
        // asserting none of them reaches the document is the only way to prove the renderer
        // dropped the LINE rather than merely being handed nulls by this fixture.
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance
        {
            SessionId = "2026-07-03-webex-doe-intake",
            ExportedAtUtc = new DateTimeOffset(2026, 8, 5, 14, 7, 0, TimeSpan.Zero),
            AppVersion = "0.9.0",
            WeightsFile = "ggml-small.en-q8_0.bin",
            VersionId = "v2-large-v3-turbo-2026-08-01",
            Model = "large-v3-turbo",
            Backend = "cuda",
            ModelAccuracy = "Decent accuracy, English only - quick",
            AudioSha256 = "sourcehash",
            TranscriptSha256 = "deadbeef",
            RecordedAudio =
            [
                new RecordedAudioLeg
                { FileName = "local.flac", Sha256 = "aaa", Silence = new FabricatedSilenceSummary(2, 3000) },
            ],
            HumanLayer = new HumanLayerCounts { Corrections = 2, SpeakerNames = 1 },
        }, null, r, "relative", new ExportOptions());

        foreach (string absent in new[]
        {
            "App:", "Participants:", "Session ID:", "Exported:", "Transcript version",
            "Weights file", "Model accuracy", "Audio SHA-256", "Transcript SHA-256",
            "Human edits", "ggml-small.en-q8_0.bin", "deadbeef", "sourcehash",
        })
            Assert.DoesNotContain(absent, md);

        // The human layer is disclosed - as prose, not as counts.
        Assert.Contains(ExportNotices.DisclaimerEdited, md);
    }

    [Fact]
    public void Source_renders_for_an_import_and_a_recorded_session_has_no_source_line()
    {
        // Owner decision (2026-08-11): Source is IMPORTS ONLY. A recorded call's legs are the
        // hardcoded constants local.flac / remote.flac (StoragePaths.AudioFile), so printing them
        // tells a reader nothing - a recorded session has no Source line, the way a session with
        // no matter has no Matter line.
        var (h, v, r) = Sample();
        string imported = MarkdownRenderer.Write(h, v,
            new ExportProvenance { AudioFileName = "interview.mp4", AudioSha256 = "abc123" },
            null, r, "relative", new ExportOptions());
        Assert.Contains("- **Source:** interview.mp4\n", imported);
        Assert.DoesNotContain("abc123", imported);        // the hash rides in the .zip, not here
        Assert.DoesNotContain("Audio:", imported);        // the old label is gone, not renamed twice

        string recorded = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, r,
            "relative", new ExportOptions());
        Assert.DoesNotContain("Source:", recorded);
    }
```

- [ ] **Step 4: Replace the human-layer bullet test with an `Omitted` test**

Replace `The_human_layer_line_renders_as_a_bullet_and_is_absent_by_default` (lines 422-434) with:

```csharp
    [Fact]
    public void Omitted_renders_only_when_duplicates_were_suppressed()
    {
        var (h, v, r) = Sample();

        string suppressed = MarkdownRenderer.Write(h, v,
            new ExportProvenance { HumanLayer = new HumanLayerCounts { SuppressedDuplicates = 4 } },
            null, r, "relative", new ExportOptions());
        Assert.Contains("- **Omitted:** 4 duplicate turns (echo)\n", suppressed);
        // A machine omission is not a human act: the disclaimer must NOT flip.
        Assert.Contains(ExportNotices.Disclaimer, suppressed);
        Assert.DoesNotContain(ExportNotices.DisclaimerEdited, suppressed);

        string clean = MarkdownRenderer.Write(h, v,
            new ExportProvenance { HumanLayer = new HumanLayerCounts() }, null, r, "relative",
            new ExportOptions());
        Assert.DoesNotContain("Omitted", clean);
    }
```

- [ ] **Step 5: Add the header-order test**

Append inside the class (the ordering is not otherwise pinned once the goldens no longer carry `Source`, `Description` or `Omitted` together):

```csharp
    [Fact]
    public void The_full_header_renders_the_seven_lines_in_the_locked_order()
    {
        // The one place the MAXIMAL header shape is pinned. The goldens above cover the minimal
        // shape; this covers every optional line at once, in order (design 2026-08-11 section 3.3).
        var h = new TranscriptHeader("Interview", "Other", Started, 1_620_000, "large-v3-turbo", "cuda");
        var v = new SessionTextView("Interview", ["Doe v Roe (2026/014)"], ["ignored roster"],
            Started, Started.AddMinutes(27), 1_620_000, "Other",
            "Second interview, custody suite.", null);
        var rows = new[] { new DisplayRow { StartMs = 1000, DisplayName = "Speaker A", Text = "Yes." } };

        string md = MarkdownRenderer.Write(h, v, new ExportProvenance
        {
            AudioFileName = "interview.mp4",
            HumanLayer = new HumanLayerCounts { SuppressedDuplicates = 4 },
        }, null, rows, "relative", new ExportOptions());

        Assert.StartsWith(
            "# Interview\n" +
            "\n" +
            "- **Date:** 2026-06-30 14:32 - 14:59 (27 min)\n" +
            "- **Matter(s):** Doe v Roe (2026/014)\n" +
            "- **Speakers heard:** Speaker A\n" +
            "- **Medium:** Other\n" +
            "- **Source:** interview.mp4\n" +
            "- **Description:** Second interview, custody suite.\n" +
            "- **Omitted:** 4 duplicate turns (echo)\n",
            md);
    }
```

- [ ] **Step 6: Run the markdown tests to verify they fail**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~MarkdownRendererWriteTests"`
Expected: multiple FAILures — the goldens report `Assert.Equal()` mismatches showing `- **App:** Teams` and `- **Participants:** ...` still present, and `The_full_header_renders_the_seven_lines_in_the_locked_order` fails on `Assert.StartsWith`.

- [ ] **Step 7: Rewrite the markdown header block**

In `src/LocalScribe.Core/Projection/MarkdownRenderer.cs`, replace lines 64-91 (from `sb.Append("# ")` through the `Excerpt` line) with:

```csharp
        var sb = new StringBuilder();
        sb.Append("# ").Append(meta.Title).Append('\n').Append('\n');
        // The lean legal header (design 2026-08-11 section 3.3). Seven possible lines, in this
        // exact order, identical in .docx / .md / .txt. Everything technical - app version,
        // session id, export instant, transcript version + model + backend, weights file, model
        // accuracy, every SHA-256 - moved OUT of the reading copy and stayed in the .zip bundle,
        // which is a verbatim folder copy of session.json / meta.json / manifest.json. Verify
        // integrity is untouched. Nothing is lost; one artefact stops carrying it.
        AppendMeta(sb, "Date", MetadataFormat.DateLine(meta));
        AppendMeta(sb, "Matter(s)",
            meta.Matters.Count == 0 ? "(none)" : string.Join(", ", meta.Matters));
        // Replaces Participants (the user-curated roster). Derived from the ROWS, so it cannot
        // drift from the body - at the accepted cost that a declared attendee who never speaks
        // disappears from the document (design 2026-08-11 section 3.5).
        string speakers = MetadataFormat.SpeakersHeard(rows);
        if (speakers.Length > 0) AppendMeta(sb, "Speakers heard", speakers);
        AppendMeta(sb, "Medium", meta.Medium);
        // IMPORTS ONLY (owner decision 2026-08-11). A recorded call's legs are the hardcoded
        // local.flac / remote.flac (StoragePaths.AudioFile), so naming them tells a reader
        // nothing; a recorded session simply has no Source line.
        if (!string.IsNullOrEmpty(provenance.AudioFileName))
            AppendMeta(sb, "Source", provenance.AudioFileName);
        if (!string.IsNullOrEmpty(meta.Description)) AppendMeta(sb, "Description", meta.Description);
        // The one count that survived the Human edits line: a MACHINE omission, which the
        // disclaimer's "reviewed and corrected by a person" does not cover, and the one the spec
        // calls concealment if hidden. Silent at zero, and on single-leg imports.
        if (MetadataFormat.OmittedLine(provenance.HumanLayer) is { } omitted)
            AppendMeta(sb, "Omitted", omitted);
        if (provenance.ExcerptSpan is { } excerptSpan) AppendMeta(sb, "Excerpt", excerptSpan);
```

Then replace line 126 (the disclaimer) with:

```csharp
        sb.Append('\n').Append('_').Append(MetadataFormat.DisclaimerFor(provenance.HumanLayer))
          .Append('_').Append('\n');
```

> Leave the in-progress notice, the excerpt notice and the whole summary block exactly where they are (lines 92-125). Their order is unchanged (decision D-B).

- [ ] **Step 8: Run the markdown tests to verify they pass**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~MarkdownRendererWriteTests"`
Expected: PASS.

- [ ] **Step 9: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: 0 failed. `MaintenanceServiceTests.ExportMarkdown_...` asserts `Assert.Contains("_" + ExportNotices.Disclaimer + "_", md)` against an unedited session, which still selects the untouched variant — it should stay green untouched. **If it fails, stop and read why before editing it.**

- [ ] **Step 10: Commit**

```bash
git add src/LocalScribe.Core/Projection/MarkdownRenderer.cs tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs
git commit -m "feat(export): lean header for the markdown export"
```

---

## Task 4: The plain-text header

Same shape as Task 3, undecorated, CRLF.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/PlainTextRenderer.cs:64-92`, `:115`
- Test: `tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs`

**Interfaces:**
- Consumes: the same `MetadataFormat` members as Task 3.
- Produces: nothing new. `PlainTextRenderer.Write`'s signature is unchanged.

- [ ] **Step 1: Rewrite the metadata test**

In `tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs`, replace `Uses_crlf_and_renders_undecorated_metadata_lines` (lines 31-44) with:

```csharp
    [Fact]
    public void Uses_crlf_and_renders_undecorated_metadata_lines()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains("\r\n", txt);
        Assert.DoesNotContain("\n\n\n", txt.Replace("\r", ""));
        Assert.Contains("Matter(s): Doe v Roe (2026/014)\r\n", txt);
        Assert.Contains("Speakers heard: Sam\r\n", txt);
        Assert.DoesNotContain("Participants", txt);             // replaced by Speakers heard
        Assert.DoesNotContain("**", txt);                       // no markdown decoration
        Assert.DoesNotContain("- **", txt);
    }

    [Fact]
    public void The_full_header_renders_the_seven_lines_in_the_locked_order()
    {
        // Parity with MarkdownRendererWriteTests: same lines, same order, no decoration
        // (design 2026-08-11 section 3.3).
        var meta = new SessionTextView("Doe intake", ["Doe v Roe (2026/014)"], ["ignored roster"],
            Start, Start.AddMinutes(30), 1_800_000, "Webex",
            "Second interview, custody suite.", Summary: null);

        string txt = PlainTextRenderer.Write(Header(), meta, new ExportProvenance
        {
            AudioFileName = "interview.mp4",
            HumanLayer = new HumanLayerCounts { SuppressedDuplicates = 4 },
        }, null, [Turn(0, 4000, "Speaker A", "Yes.")], "relative", new ExportOptions());

        Assert.StartsWith(
            "Doe intake\r\n" +
            "\r\n" +
            "Date: 2026-07-03 09:00 - 09:30 (30 min)\r\n" +
            "Matter(s): Doe v Roe (2026/014)\r\n" +
            "Speakers heard: Speaker A\r\n" +
            "Medium: Webex\r\n" +
            "Source: interview.mp4\r\n" +
            "Description: Second interview, custody suite.\r\n" +
            "Omitted: 4 duplicate turns (echo)\r\n",
            txt);
    }
```

- [ ] **Step 2: Replace the two provenance tests**

Delete `Recorded_audio_and_transcript_hashes_render_undecorated_and_are_absent_by_default` (lines 240-262) and `Full_provenance_renders_undecorated_and_is_absent_by_default` (lines 263-283). Replace both with:

```csharp
    [Fact]
    public void Every_removed_provenance_line_is_absent_even_when_the_field_is_populated()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance
        {
            SessionId = "2026-07-03-webex-doe-intake",
            ExportedAtUtc = new DateTimeOffset(2026, 8, 5, 14, 7, 0, TimeSpan.Zero),
            AppVersion = "0.9.0",
            WeightsFile = "ggml-small.en-q8_0.bin",
            VersionId = "v2-large-v3-turbo-2026-08-01",
            Model = "large-v3-turbo",
            Backend = "cuda",
            ModelAccuracy = "Decent accuracy, English only - quick",
            AudioSha256 = "sourcehash",
            TranscriptSha256 = "deadbeef",
            RecordedAudio =
                [new RecordedAudioLeg
                { FileName = "remote.flac", Sha256 = "bbb", Silence = null }],
            HumanLayer = new HumanLayerCounts { Splits = 1 },
        }, null, [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        foreach (string absent in new[]
        {
            "App:", "Participants:", "Session ID:", "Exported:", "Transcript version",
            "Weights file", "Model accuracy", "Audio SHA-256", "Transcript SHA-256",
            "Human edits", "ggml-small.en-q8_0.bin", "deadbeef", "sourcehash",
        })
            Assert.DoesNotContain(absent, txt);

        Assert.Contains(ExportNotices.DisclaimerEdited, txt);
    }

    [Fact]
    public void Source_renders_for_an_import_and_a_recorded_session_has_no_source_line()
    {
        string imported = PlainTextRenderer.Write(Header(), Meta(),
            new ExportProvenance { AudioFileName = "interview.mp4", AudioSha256 = "abc123" }, null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());
        Assert.Contains("Source: interview.mp4\r\n", imported);
        Assert.DoesNotContain("abc123", imported);
        Assert.DoesNotContain("Audio:", imported);

        string recorded = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());
        Assert.DoesNotContain("Source:", recorded);
    }
```

- [ ] **Step 3: Replace the human-layer test**

Replace `The_human_layer_line_renders_undecorated_and_is_absent_by_default` (lines 284-296) with:

```csharp
    [Fact]
    public void Omitted_renders_only_when_duplicates_were_suppressed()
    {
        var rows = new[] { Turn(0, 4000, "Sam", "hello") };

        string suppressed = PlainTextRenderer.Write(Header(), Meta(),
            new ExportProvenance { HumanLayer = new HumanLayerCounts { SuppressedDuplicates = 1 } },
            null, rows, "relative", new ExportOptions());
        Assert.Contains("Omitted: 1 duplicate turn (echo)\r\n", suppressed);
        Assert.Contains(ExportNotices.Disclaimer, suppressed);
        Assert.DoesNotContain(ExportNotices.DisclaimerEdited, suppressed);

        Assert.DoesNotContain("Omitted", PlainTextRenderer.Write(Header(), Meta(),
            new ExportProvenance { HumanLayer = new HumanLayerCounts() }, null, rows, "relative",
            new ExportOptions()));
    }
```

- [ ] **Step 4: Run the plain-text tests to verify they fail**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~PlainTextRendererWriteTests"`
Expected: FAIL — `Assert.DoesNotContain("Participants", txt)` finds `Participants: Sam (Counsel)`, and `The_full_header_...` fails on `Assert.StartsWith`.

- [ ] **Step 5: Rewrite the plain-text header block**

In `src/LocalScribe.Core/Projection/PlainTextRenderer.cs`, replace lines 64-92 with:

```csharp
        var sb = new StringBuilder();
        sb.Append(meta.Title).Append(Nl).Append(Nl);
        // The lean legal header at MarkdownRenderer.Write parity (design 2026-08-11 section 3.3):
        // the same seven lines in the same order, undecorated. See MarkdownRenderer.Write for why
        // each removed line moved into the .zip bundle rather than disappearing.
        AppendMeta(sb, "Date", MetadataFormat.DateLine(meta));
        AppendMeta(sb, "Matter(s)",
            meta.Matters.Count == 0 ? "(none)" : string.Join(", ", meta.Matters));
        string speakers = MetadataFormat.SpeakersHeard(rows);
        if (speakers.Length > 0) AppendMeta(sb, "Speakers heard", speakers);
        AppendMeta(sb, "Medium", meta.Medium);
        // IMPORTS ONLY (owner decision 2026-08-11) - see MarkdownRenderer.Write.
        if (!string.IsNullOrEmpty(provenance.AudioFileName))
            AppendMeta(sb, "Source", provenance.AudioFileName);
        if (!string.IsNullOrEmpty(meta.Description)) AppendMeta(sb, "Description", meta.Description);
        if (MetadataFormat.OmittedLine(provenance.HumanLayer) is { } omitted)
            AppendMeta(sb, "Omitted", omitted);
        if (provenance.ExcerptSpan is { } excerptSpan) AppendMeta(sb, "Excerpt", excerptSpan);
```

Then replace line 115 with:

```csharp
        sb.Append(Nl).Append(MetadataFormat.DisclaimerFor(provenance.HumanLayer)).Append(Nl);
```

- [ ] **Step 6: Run the plain-text tests to verify they pass**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~PlainTextRendererWriteTests"`
Expected: PASS.

- [ ] **Step 7: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: 0 failed.

- [ ] **Step 8: Commit**

```bash
git add src/LocalScribe.Core/Projection/PlainTextRenderer.cs tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs
git commit -m "feat(export): lean header for the plain-text export"
```

---

## Task 5: The docx header

**Files:**
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs:54-92`
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`

**Interfaces:**
- Consumes: the same `MetadataFormat` members as Tasks 3-4.
- Produces: nothing new. `DocxRenderer.Write`'s signature is unchanged.

**Two docx-specific traps:**
1. The in-progress and excerpt notices are emitted **twice** — body (`:84`, `:88-89`) and the running page-header part (`:165-170`). Part 3 keeps both notices, so **do not touch either site**. Trimming only the body would leave them on pages 2+ with nothing on page 1.
2. Every metadata line must go through `MetaLine`, which applies `SuppressLineNumbers`. A hand-built `Paragraph` would silently renumber the whole transcript (line numbers count transcript content only).

- [ ] **Step 1: Rewrite the metadata assertions**

In `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`, in `Renders_metadata_disclaimer_marker_footer_and_a4_pagesize` (line 99), replace line 108 (`Assert.Contains("Participants: Sam, Bob (Counsel)", text);`) with:

```csharp
        Assert.Contains("Speakers heard: Sam, Bob", text);
        Assert.DoesNotContain("Participants", text);
        Assert.DoesNotContain("App: Teams", text);
```

Replace `Metadata_block_carries_duration_version_and_speakers_heard` (lines 641-659) with:

```csharp
    [Fact]
    public void Metadata_block_carries_the_date_and_speakers_heard_and_no_transcript_version()
    {
        // Part 3 (design 2026-08-11 section 3.3): the model name was welded into "Transcript
        // version", so removing the model removes the whole line. The engine still rides in
        // session.json inside the .zip bundle.
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance
            {
                VersionId = "v2-large-v3-turbo-2026-08-01",
                Model = "large-v3-turbo",
                Backend = "cuda",
            });
        using var doc = Open(bytes);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.Contains("Date: 2026-06-30 14:32 - 15:09 (37 min)", text);
        Assert.Contains("Speakers heard: Sam, Bob", text);
        Assert.DoesNotContain("Transcript version", text);
        Assert.DoesNotContain("large-v3-turbo", text);
    }

    [Fact]
    public void The_full_header_renders_the_seven_lines_in_the_locked_order()
    {
        // Parity with the .md and .txt header-order tests. InnerText concatenates the
        // label run and the value run of each MetaLine paragraph with no separator, so the
        // expected string below is the labelled run read straight through.
        var meta = new SessionTextView("Interview", ["Doe v Roe (2026/014)"], ["ignored roster"],
            Started, Started.AddMinutes(27), 1_620_000, "Other",
            "Second interview, custody suite.", Summary: null);
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), meta, new ExportProvenance
        {
            AudioFileName = "interview.mp4",
            HumanLayer = new HumanLayerCounts { SuppressedDuplicates = 4 },
        }, null, [Turn(1000, 5000, "Speaker A", "Yes.")], "relative", DocxPageSize.A4,
            new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Select(p => p.InnerText).ToList();

        Assert.Equal(
            new[]
            {
                "Interview",
                "Date: 2026-06-30 14:32 - 14:59 (27 min)",
                "Matter(s): Doe v Roe (2026/014)",
                "Speakers heard: Speaker A",
                "Medium: Other",
                "Source: interview.mp4",
                "Description: Second interview, custody suite.",
                "Omitted: 4 duplicate turns (echo)",
            },
            paragraphs.Take(8));
    }
```

- [ ] **Step 2: Rewrite the audio-provenance test as a Source test**

Replace `Audio_provenance_renders_for_an_imported_session_and_is_absent_otherwise` (lines 661-683) with:

```csharp
    [Fact]
    public void Source_renders_for_an_import_and_a_recorded_session_has_no_source_line()
    {
        // Owner decision (2026-08-11): Source is IMPORTS ONLY. The pre-Part-3 assertion here was
        // DoesNotContain("Audio:") for the recorded case; that label no longer exists on ANY path,
        // so it would now pass vacuously. DoesNotContain("Source:") is the assertion with teeth.
        byte[] imported = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance { AudioFileName = "call.m4a", AudioSha256 = "abc123" });
        using (var doc = Open(imported))
        {
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Source: call.m4a", text);
            Assert.DoesNotContain("abc123", text);      // the hash rides in the .zip, not here
            Assert.DoesNotContain("Audio:", text);
        }

        byte[] recorded = Render("relative", DocxPageSize.A4, new ExportOptions());
        using (var doc = Open(recorded))
            Assert.DoesNotContain("Source:", doc.MainDocumentPart!.Document!.Body!.InnerText);
    }
```

- [ ] **Step 3: Replace the two provenance tests and the human-layer test**

Delete `Recorded_audio_hashes_the_transcript_hash_and_the_accuracy_tier_render_and_are_absent_by_default` (lines 1020-1051), `Full_provenance_renders_and_every_part_is_absent_by_default` (lines 1052-1085) and `The_human_layer_line_renders_when_counts_are_supplied_and_is_absent_by_default` (lines 1086-1104). Replace all three with:

```csharp
    [Fact]
    public void Every_removed_provenance_line_is_absent_even_when_the_field_is_populated()
    {
        // The exact ABSENT set (design 2026-08-11 section 3.3). Populating every field proves the
        // renderer dropped the LINE rather than merely being handed nulls by the fixture.
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance
            {
                SessionId = "2026-07-03-webex-doe-intake",
                ExportedAtUtc = new DateTimeOffset(2026, 8, 5, 14, 7, 0, TimeSpan.Zero),
                AppVersion = "0.9.0",
                WeightsFile = "ggml-small.en-q8_0.bin",
                VersionId = "v2-large-v3-turbo-2026-08-01",
                Model = "small.en",
                Backend = "cuda",
                ModelAccuracy = "Decent accuracy, English only - quick",
                AudioSha256 = "sourcehash",
                TranscriptSha256 = "deadbeef",
                RecordedAudio =
                [
                    new RecordedAudioLeg
                    { FileName = "local.flac", Sha256 = "aaa", Silence = new FabricatedSilenceSummary(2, 3000) },
                ],
                HumanLayer = new HumanLayerCounts { Corrections = 2, SpeakerPins = 3 },
            });
        using var doc = Open(bytes);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        foreach (string absent in new[]
        {
            "App:", "Participants:", "Session ID:", "Exported:", "Transcript version",
            "Weights file", "Model accuracy", "Audio SHA-256", "Transcript SHA-256",
            "Human edits", "ggml-small.en-q8_0.bin", "deadbeef", "sourcehash",
        })
            Assert.DoesNotContain(absent, text);

        Assert.Contains(ExportNotices.DisclaimerEdited, text);
    }

    [Fact]
    public void Omitted_renders_only_when_duplicates_were_suppressed()
    {
        byte[] suppressed = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance { HumanLayer = new HumanLayerCounts { SuppressedDuplicates = 4 } });
        using (var doc = Open(suppressed))
        {
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Omitted: 4 duplicate turns (echo)", text);
            // A machine omission is not a human act: the disclaimer must NOT flip.
            Assert.Contains(ExportNotices.Disclaimer, text);
            Assert.DoesNotContain(ExportNotices.DisclaimerEdited, text);
        }

        byte[] clean = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance { HumanLayer = new HumanLayerCounts() });
        using (var doc = Open(clean))
            Assert.DoesNotContain("Omitted", doc.MainDocumentPart!.Document!.Body!.InnerText);
    }
```

- [ ] **Step 4: Shrink the schema-validity test**

Rename `A_document_with_every_tier1c_metadata_line_stacked_is_schema_valid` (line 1150) to `A_document_with_every_remaining_metadata_line_stacked_is_schema_valid`, replace its `ExportProvenance` initializer with the set that still renders, and update the comment:

```csharp
    [Fact]
    public void A_document_with_every_remaining_metadata_line_stacked_is_schema_valid()
    {
        // Every optional line Part 3 KEPT, stacked, plus a split turn - the shape most likely to
        // trip Word's pPr child ordering, which the SDK accepts without complaint. Every metadata
        // line goes through MetaLine, which applies SuppressLineNumbers; a hand-built Paragraph
        // here would silently renumber the transcript.
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
                AudioFileName = "call.m4a",
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
        // Office2019, matching every other validation in this file: the bare constructor targets a
        // different (older) format version, so a mixed pair would validate two different contracts.
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(doc).ToList();

        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => e.Description + " @ " + e.Path?.XPath)));
    }
```

> `Meta()` has an empty `Description`, so that line is absent here — deliberate: `The_full_header_renders_the_seven_lines_in_the_locked_order` covers the maximal labelled run, and this test covers the maximal *notice* stack.

- [ ] **Step 5: Run the docx tests to verify they fail**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~DocxRendererTests"`
Expected: FAIL — `Assert.DoesNotContain("Participants", text)` finds `Participants: Sam, Bob (Counsel)`, and the header-order `Assert.Equal` reports the `App:` paragraph at index 1.

- [ ] **Step 6: Rewrite the docx header block**

In `src/LocalScribe.Core/Projection/DocxRenderer.cs`, replace lines 53-83 (from the `// Metadata header block.` comment through the `Human edits` line) with:

```csharp
        // Metadata header block - the lean legal header (design 2026-08-11 section 3.3). Seven
        // possible lines, in this exact order, identical in .docx / .md / .txt. Everything
        // technical - app version, session id, export instant, transcript version + model +
        // backend, weights file, model accuracy, every SHA-256 - moved OUT of the reading copy and
        // stayed in the .zip bundle, which is a verbatim folder copy of session.json / meta.json /
        // manifest.json. Verify integrity is untouched. Every line MUST go through MetaLine, which
        // applies SuppressLineNumbers; a hand-built Paragraph would silently renumber the
        // transcript, whose line numbering counts content only.
        body.AppendChild(Heading(meta.Title));
        body.AppendChild(MetaLine("Date", MetadataFormat.DateLine(meta)));
        body.AppendChild(MetaLine("Matter(s)",
            meta.Matters.Count == 0 ? "(none)" : string.Join(", ", meta.Matters)));
        // Replaces Participants (the user-curated roster). Derived from the ROWS, so it cannot
        // drift from the body - at the accepted cost that a declared attendee who never speaks
        // disappears from the document (design 2026-08-11 section 3.5).
        string speakers = MetadataFormat.SpeakersHeard(rows);
        if (speakers.Length > 0) body.AppendChild(MetaLine("Speakers heard", speakers));
        body.AppendChild(MetaLine("Medium", meta.Medium));
        // IMPORTS ONLY (owner decision 2026-08-11). A recorded call's legs are the hardcoded
        // local.flac / remote.flac (StoragePaths.AudioFile), so naming them tells a reader
        // nothing; a recorded session simply has no Source line.
        if (!string.IsNullOrEmpty(provenance.AudioFileName))
            body.AppendChild(MetaLine("Source", provenance.AudioFileName));
        if (!string.IsNullOrEmpty(meta.Description))
            body.AppendChild(MetaLine("Description", meta.Description));
        // The one count that survived the Human edits line: a MACHINE omission, which the
        // disclaimer's "reviewed and corrected by a person" does not cover, and the one the spec
        // calls concealment if hidden.
        if (MetadataFormat.OmittedLine(provenance.HumanLayer) is { } omitted)
            body.AppendChild(MetaLine("Omitted", omitted));
```

Then replace the `DisclaimerLine()` helper at lines 446-451 so it takes the provenance:

```csharp
    /// <summary>Italic disclaimer closed by a thin 0.5pt rule (design 2026-08-02 item 6) that
    /// separates the unnumbered metadata block from the numbered transcript body. The wording is
    /// one of two variants chosen from the human-layer counts (design 2026-08-11 section 3.3) -
    /// never composed here, so the three formats cannot word one evidentiary sentence
    /// differently.</summary>
    private static Paragraph DisclaimerLine(ExportProvenance provenance)
        => new(new ParagraphProperties(
                new SuppressLineNumbers(),
                new ParagraphBorders(new BottomBorder
                { Val = BorderValues.Single, Size = 4U, Space = 4U, Color = "auto" })),
            new Run(new RunProperties(new Italic()),
                MakeText(MetadataFormat.DisclaimerFor(provenance.HumanLayer))));
```

And update its call site at line 92:

```csharp
        body.AppendChild(DisclaimerLine(provenance));
```

> **Do not touch lines 84-90** (the in-progress line and the excerpt line + notice) or lines 163-173 (the running-header part). Their content and order are unchanged (decision D-B), and the two notices are deliberately emitted twice.

- [ ] **Step 7: Run the docx tests to verify they pass**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~DocxRendererTests"`
Expected: PASS, including both `OpenXmlValidator` tests.

- [ ] **Step 8: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: 0 failed.

- [ ] **Step 9: Commit**

```bash
git add src/LocalScribe.Core/Projection/DocxRenderer.cs tests/LocalScribe.Core.Tests/DocxRendererTests.cs
git commit -m "feat(export): lean header for the docx export"
```

---

## Task 6: Delete the corrected-turn mark end to end

Nine files across Core, App and XAML. The XAML is the one the compiler cannot help with.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/ExportNotices.cs:35-39` (delete `CorrectedTurnMark`)
- Modify: `src/LocalScribe.Core/Projection/ExportOptions.cs:21-27` (delete `MarkCorrectedTurns`)
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs:291-295`, `:122`, `:304`
- Modify: `src/LocalScribe.Core/Projection/MarkdownRenderer.cs:149-153`, `:161`
- Modify: `src/LocalScribe.Core/Projection/PlainTextRenderer.cs:131-136`, `:140`
- Modify: `src/LocalScribe.Core/Model/Settings.cs:110-113` (delete `ExportSetting.MarkCorrectedTurns`)
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs:38-41`, `:51-53`, `:227`, `:289`
- Modify: `src/LocalScribe.App/ExportDialog.xaml:64-65`
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs` (2 methods), `MarkdownRendererWriteTests.cs` (1), `PlainTextRendererWriteTests.cs` (1)
- Test: `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs:770-792` (delete)
- Test: `tests/LocalScribe.App.Tests/XamlHygieneTests.cs` (add guard)

**Interfaces:**
- Consumes: `DisplayRow.HasCorrection` — **stays alive**, bound by `ReadViewWindow.xaml:571` and asserted by `ReadViewViewModelTests.cs:388`. Do not delete `DisplayRow.cs:16`.
- Produces: `ExportOptions` drops to four members (`IncludeTimestamps`, `IncludeMarkers`, `TimestampIntervalMs`, `IncludeSummary`); `ExportSetting` drops to seven.

**No settings migration.** `ExportSetting.MarkCorrectedTurns` was additive with no schema bump; `SettingsStore.Version` stays 3, a stale `markCorrectedTurns` key is ignored on deserialize and dropped on the next save.

- [ ] **Step 1: Write the XAML hygiene guard**

Append to `tests/LocalScribe.App.Tests/XamlHygieneTests.cs`, inside the `XamlHygieneTests` class:

```csharp
    [Fact]
    public void ExportDialog_has_no_binding_to_the_deleted_MarkCorrectedTurns_property()
    {
        // A WPF binding to a property that no longer exists fails SILENTLY at runtime: no build
        // error, no test failure anywhere else in the 2587-test suite. The checkbox would simply
        // stay visible and stop doing anything - an export toggle that lies. Nothing else in this
        // repo catches that class of defect, so it is caught here, by string.
        string xaml = File.ReadAllText(RepoPaths.AppXaml("ExportDialog.xaml"));
        Assert.DoesNotContain("MarkCorrectedTurns", xaml);
        Assert.DoesNotContain("Mark corrected turns", xaml);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~XamlHygieneTests"`
Expected: FAIL — `Assert.DoesNotContain() Failure: Found: "MarkCorrectedTurns"`.

- [ ] **Step 3: Update the three renderer tests**

`tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs` — replace `A_corrected_turn_is_marked_in_markdown_and_the_toggle_removes_it` (lines 435-461) with:

```csharp
    [Fact]
    public void A_corrected_turn_carries_no_per_turn_mark()
    {
        // Part 3 (design 2026-08-11 section 3.3): the per-turn "[text corrected]" mark is DELETED,
        // not defaulted off. Human-edit disclosure moved into the disclaimer as prose - a document
        // stippled with per-turn marks reads as a document under suspicion, and the sentence at
        // the top says the same thing once, in language a reader can quote.
        var row = new DisplayRow
        {
            StartMs = 1000, EndMs = 5000, DisplayName = "Sam", Text = "Corrected text.",
            Segments = [new RowSegment(0, TranscriptSource.Local, 1000, 5000, "Corrected text.",
                "Original text.", IsCorrected: true, IsPinned: false)],
        };
        var (h, v, _) = Sample();

        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, [row], "relative",
            new ExportOptions());
        Assert.Contains("**[00:01] Sam:** Corrected text.\n", md);
        Assert.DoesNotContain("[text corrected]", md);
    }
```

`tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs` — replace `A_corrected_turn_is_marked_in_plain_text_and_the_toggle_removes_it` (lines 297-313) with:

```csharp
    [Fact]
    public void A_corrected_turn_carries_no_per_turn_mark()
    {
        var row = new DisplayRow
        {
            StartMs = 1000, EndMs = 5000, DisplayName = "Sam", Text = "Corrected text.",
            Segments = [new RowSegment(0, TranscriptSource.Local, 1000, 5000, "Corrected text.",
                "Original text.", IsCorrected: true, IsPinned: false)],
        };

        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null, [row],
            "relative", new ExportOptions());
        Assert.Contains("[00:01] Sam: Corrected text.\r\n", txt);
        Assert.DoesNotContain("[text corrected]", txt);
    }
```

`tests/LocalScribe.Core.Tests/DocxRendererTests.cs` — replace `A_corrected_turn_is_marked_by_default_and_the_mark_never_reaches_the_running_head` (lines 1105-1128) **and** `The_correction_mark_can_be_switched_off_and_an_uncorrected_turn_never_carries_it` (lines 1129-1149) with a single:

```csharp
    [Fact]
    public void A_corrected_turn_carries_no_per_turn_mark_and_the_speaker_run_is_the_bare_name()
    {
        // Part 3: the mark is deleted. The speaker-run assertion is KEPT from the pre-Part-3 test
        // it replaces: STYLEREF "Transcript Speaker" in the page header returns that run's text
        // verbatim, so anything ever appended INSIDE it surfaces on every page. Proving the run is
        // exactly the name keeps that guarantee pinned after the suffix stopped being composed.
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
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
        Assert.Contains("Sam:", text);
        Assert.DoesNotContain("[text corrected]", text);
        var speakerRun = doc.MainDocumentPart.Document.Body.Descendants<Run>()
            .First(r => r.RunProperties?.RunStyle?.Val?.Value == "TranscriptSpeaker");
        Assert.Equal("Sam", speakerRun.InnerText);
    }
```

- [ ] **Step 4: Delete the App-layer test**

In `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`, delete the whole `Mark_corrected_turns_seeds_from_settings_defaults_on_and_persists` method (lines 770-792).

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~RendererTests|FullyQualifiedName~RendererWriteTests"`
Expected: FAIL — the three renderer tests report `Assert.DoesNotContain() Failure: Found: "[text corrected]"`, because `MarkCorrectedTurns` still defaults to `true`.

- [ ] **Step 6: Delete the Core surfaces**

`src/LocalScribe.Core/Projection/ExportNotices.cs` — delete lines 35-39 (the `CorrectedTurnMark` doc comment and constant).

`src/LocalScribe.Core/Projection/ExportOptions.cs` — delete lines 21-27 (the `MarkCorrectedTurns` doc comment and property).

`src/LocalScribe.Core/Projection/MarkdownRenderer.cs` — delete lines 149-152 (the `mark` local and its comment) and remove `.Append(mark)` from lines 153 and 161, so they read:

```csharp
            sb.Append('\n').Append("**").Append(label).Append(":** ")
              .Append(chunks[0].Text).Append('\n');
```

```csharp
                sb.Append('\n').Append("**").Append(contLabel).Append(" (cont'd):** ")
                  .Append(chunks[i].Text).Append('\n');
```

`src/LocalScribe.Core/Projection/PlainTextRenderer.cs` — delete lines 131-134 (the `mark` local and its comment) and remove `.Append(mark)` from lines 135-136 and 138-140, so they read:

```csharp
            sb.Append(Nl).Append(Label(row.DisplayName, row.StartMs, options, timestampsMode,
                header.StartedAtLocal)).Append(": ").Append(chunks[0].Text).Append(Nl);
            for (int i = 1; i < chunks.Count; i++)
                sb.Append(Nl).Append(Label(row.DisplayName, chunks[i].StampMs, options,
                    timestampsMode, header.StartedAtLocal))
                  .Append(" (cont'd): ").Append(chunks[i].Text).Append(Nl);
```

`src/LocalScribe.Core/Projection/DocxRenderer.cs` — **three edits**:

1. Delete lines 291-295 (the `CorrectedMark` doc comment and method).
2. Line 122 (the continuation suffix inside `Write`) becomes:

```csharp
                        Suffix = " (cont'd):",
```

   and delete the three-line comment above it (lines 119-121), which explained why the mark repeated.
3. `TurnLabel` (lines 297-304) — the suffix becomes a bare colon:

```csharp
    private static TurnLabelParts TurnLabel(DisplayRow row, ExportOptions options, string timestampsMode,
        DateTimeOffset startedAtLocal)
        => new(
            options.IncludeTimestamps
                ? "[" + TimestampFormat.Stamp(row.StartMs, timestampsMode, startedAtLocal) + "] "
                : "",
            row.DisplayName ?? "",
            ":");
```

> `TurnLabelParts.Suffix` and the three-run label split stay. The comment at `:284-289` explaining why the name run is kept separate is still correct and load-bearing (`STYLEREF`) — leave it.

- [ ] **Step 7: Delete the App surfaces**

`src/LocalScribe.Core/Model/Settings.cs` — delete lines 110-113 (the `MarkCorrectedTurns` doc comment and property on `ExportSetting`).

`src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs` — four edits:

1. Lines 38-41: drop `_markCorrectedTurns` from the tuple and `e.MarkCorrectedTurns` from the values:

```csharp
        var e = settings.Current.Export;
        (_format, _includeTimestamps, _includeMarkers, _extraTimestamps, _cadenceIntervalMs,
            _includeSummary)
            = (e.Format, e.IncludeTimestamps, e.IncludeMarkers, e.ExtraTimestamps,
               e.CadenceIntervalMs, e.IncludeSummary);
```

2. Delete lines 51-53 (the doc comment and `[ObservableProperty] private bool _markCorrectedTurns = true;`).
3. Delete line 227 (`MarkCorrectedTurns = MarkCorrectedTurns,` in the `ExportOptions` initializer).
4. Delete line 289 (`MarkCorrectedTurns = MarkCorrectedTurns,` in the `ExportSetting with` block).

`src/LocalScribe.App/ExportDialog.xaml` — delete lines 64-65 (the whole `<CheckBox Content="Mark corrected turns" .../>` element including its `ToolTip` attribute line).

- [ ] **Step 8: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: 0 failed. A `CS0117` on `MarkCorrectedTurns` anywhere means a call site was missed — the compiler catches every one except the XAML, which Step 1's guard now covers.

- [ ] **Step 9: Commit**

```bash
git add src/LocalScribe.Core/Projection/ExportNotices.cs src/LocalScribe.Core/Projection/ExportOptions.cs src/LocalScribe.Core/Projection/DocxRenderer.cs src/LocalScribe.Core/Projection/MarkdownRenderer.cs src/LocalScribe.Core/Projection/PlainTextRenderer.cs src/LocalScribe.Core/Model/Settings.cs src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/ExportDialog.xaml tests/
git commit -m "feat(export): delete the per-turn [text corrected] mark end to end"
```

---

## Task 7: Delete `MetadataFormat.HumanLayerLine`

Small and mandatory: after Tasks 3-5 nothing calls it.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/MetadataFormat.cs:82-98`
- Delete: `tests/LocalScribe.Core.Tests/HumanLayerLineTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing. `HumanLayerCounts` itself **survives** — `MetadataFormat.OmittedLine` reads `SuppressedDuplicates` from it and `MetadataFormat.DisclaimerFor` reads the other four.

- [ ] **Step 1: Confirm it is unreachable**

Run: `grep -rn "HumanLayerLine" src/ tests/`
Expected: hits only in `src/LocalScribe.Core/Projection/MetadataFormat.cs` and `tests/LocalScribe.Core.Tests/HumanLayerLineTests.cs`. If any renderer still calls it, Tasks 3-5 are incomplete — go back.

- [ ] **Step 2: Delete the method and its tests**

Delete `src/LocalScribe.Core/Projection/MetadataFormat.cs` lines 82-98 (the `HumanLayerLine` doc comment and method).

**Do NOT delete the `Count` helper at lines 100-101** — `OmittedLine` (Task 2) is its remaining caller.

```bash
git rm tests/LocalScribe.Core.Tests/HumanLayerLineTests.cs
```

- [ ] **Step 3: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: 0 failed, total down by 3 (the deleted file's facts).

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.Core/Projection/MetadataFormat.cs tests/LocalScribe.Core.Tests/HumanLayerLineTests.cs
git commit -m "refactor(export): drop HumanLayerLine, unreachable since the lean header"
```

---

## Task 8: Trim `ExportProvenance` and its now-unreachable feeders

> **This is decision D-F — the choice the handoff asked to be made explicitly.**
>
> **Recommendation: do it.** After Tasks 3-7, ten `ExportProvenance` properties are written by `ProvenanceFor` and read by nothing. Worse, `MetadataFormat.RecordedAudioLines` carries an evidentiary invariant in its doc comment — *"no code path may ever emit a hash without its clause"* — that is now vacuous, and a future reader finding it will reasonably assume the export still prints hashes and re-wire it without the review context that produced the wording. Dead evidentiary code is more dangerous than dead ordinary code. The `.zip` story is unaffected either way: it reads `manifest.json` off disk, never `ExportProvenance`.
>
> **To drop this task instead:** skip it entirely and go to Task 9. Everything still compiles and every remaining test passes; the cost is ten write-only properties, two write-only record types, three unreachable `MetadataFormat` methods, and one pointless `manifest.json` read per export. Nothing downstream in this plan depends on Task 8. Adjust Task 9's spec wording accordingly (noted there).
>
> **To revert after landing:** this task is one commit; `git revert` it.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/ExportProvenance.cs`
- Modify: `src/LocalScribe.Core/Projection/MetadataFormat.cs` (delete `VersionLine`, `ExportedLine`, `RecordedAudioLines`, `Hms`)
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs:1062-1063`, `:1090-1091`, `:1117-1118`, `:1135-1171`, `:1173-1198`, `:1199-~1215`
- Test: `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs` (delete 3 methods)
- Test: `tests/LocalScribe.App.Tests/MaintenanceServiceProvenanceTests.cs` (delete 4 methods, keep 2)
- Test: `tests/LocalScribe.Core.Tests/{Docx,Markdown,PlainText}Renderer*Tests.cs` (shrink the absent-set initializers)

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `ExportProvenance` reduced to four members: `string? AudioFileName`, `HumanLayerCounts? HumanLayer`, `bool InProgress`, `string? ExcerptSpan`.
  - `MaintenanceService.ProvenanceFor(LoadedProjection loaded) : ExportProvenance` — `TimeProvider time` and `SessionManifest? manifest` parameters removed.
  - `RecordedAudioLeg` and `FabricatedSilenceSummary` deleted.

- [ ] **Step 1: Verify the manifest read has no other consumer**

Read `src/LocalScribe.App/Services/MaintenanceService.cs` lines 1055-1125. Confirm that at each of the three export methods the local `manifest` is used **only** as the third argument to `ProvenanceFor`. If it feeds anything else at any of the three sites, **stop** and report — the rest of this task assumes it does not.

- [ ] **Step 2: Shrink the tests first**

`tests/LocalScribe.Core.Tests/MetadataFormatTests.cs` — delete `RecordedAudioLines_states_the_fabricated_silence_beside_every_hash` (lines 36-65), `RecordedAudioLines_is_empty_for_a_session_with_no_sealed_audio` (lines 67-69) and `One_fabricated_span_reads_singular` (lines 71-82). Keep `DateLine_*`, `SpeakersHeard_*`, and everything Tasks 1-2 added.

`tests/LocalScribe.App.Tests/MaintenanceServiceProvenanceTests.cs` — delete `Model_backend_and_version_id_land_in_the_matching_fields`, `An_unsealed_session_carries_no_hashes_but_still_carries_the_accuracy_tier`, and `Session_id_app_version_weights_file_and_the_export_instant_are_all_filled`. Keep `InProgress_is_true_exactly_when_EndedAtUtc_is_null`, `Recorded_session_leaves_audio_fields_null_not_empty_string` (drop its `AudioSha256` assertion) and `Imported_session_carries_the_source_file_name_and_hash` (rename to `Imported_session_carries_the_source_file_name`, drop its `AudioSha256` assertion). Update the class doc comment to say the record now carries only what the lean header renders. Drop the now-unused `TimeProvider`/`ManualUtcTimeProvider` arguments from the surviving `ProvenanceFor` calls.

In all three renderer test files, shrink the `ExportProvenance` initializer inside `Every_removed_provenance_line_is_absent_even_when_the_field_is_populated` to the surviving members (`AudioFileName`, `HumanLayer`) — **keep every `Assert.DoesNotContain` line unchanged.** The assertions are the contract; the initializer was only ever proving the renderer ignored fields that now do not exist.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~MetadataFormatTests|FullyQualifiedName~MaintenanceServiceProvenanceTests"`
Expected: BUILD FAILURE — `error CS7036: There is no argument given that corresponds to the required parameter 'time'` at the surviving `ProvenanceFor` call sites.

- [ ] **Step 4: Trim `ExportProvenance`**

Replace the whole of `src/LocalScribe.Core/Projection/ExportProvenance.cs` with:

```csharp
namespace LocalScribe.Core.Projection;

/// <summary>What a PERSON changed after the machine produced this transcript, plus what the render
/// layer removed (Tier 1 T1-8). Five separate counts, not one total, because each maps to exactly
/// one on-disk structure.
/// Corrections and Splits are counted from edits.json SEPARATELY: a split's parts are emitted with
/// Corrected=false (TranscriptProjection), so counting ProjectedSegment.Corrected alone
/// undercounts the human layer. SpeakerPins and SpeakerNames come from speakers.json, which
/// edits.json knows nothing about.
///
/// The exported header stopped PRINTING these counts in Part 3 (design 2026-08-11 section 3.3),
/// but the record survives because two things read it: MetadataFormat.DisclaimerFor picks the
/// disclaimer variant from the four HUMAN counts, and MetadataFormat.OmittedLine discloses
/// SuppressedDuplicates.</summary>
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
    /// concealment - which is why it is the one that still prints, on the Omitted line.</summary>
    public int SuppressedDuplicates { get; init; }
}

/// <summary>Export-only provenance for a rendered transcript (design 2026-08-03 section 1).
/// Deliberately NOT folded into SessionTextView: that record is the neutral, app-independent
/// metadata projection behind session.txt and must not grow export-specific fields. Composed in
/// MaintenanceService.ProvenanceFor, so the renderers stay pure serializers.
///
/// Part 3 (design 2026-08-11 section 3.3) cut this from fourteen members to four. The removed
/// facts - app version, session id, export instant, transcript version, model, backend, weights
/// file, model accuracy, and every SHA-256 - were rendered ONLY into the exported header, and the
/// header stopped carrying them. They are not lost: the .zip bundle is a verbatim folder copy
/// (SessionArchiver.AddSessionFolderAsync), so session.json, meta.json and manifest.json still
/// carry all of them, and Verify integrity is untouched. The evidentiary archive keeps the full
/// record; the reading copy is lean. Do NOT re-add a field here without a renderer that prints
/// it - a write-only property reads as a promise the document does not keep.</summary>
public sealed record ExportProvenance
{
    /// <summary>Imported sessions only, from ImportedSourceInfo - the original file the audio came
    /// from, rendered as the "Source" line. Null for recorded sessions, whose legs are the
    /// hardcoded local.flac / remote.flac (StoragePaths.AudioFile) and therefore name nothing a
    /// reader could use; they get no Source line at all (owner decision 2026-08-11).</summary>
    public string? AudioFileName { get; init; }

    /// <summary>Null renders the UNTOUCHED disclaimer and no Omitted line, which is what an
    /// all-default instance (and therefore every renderer unit test) produces. ProvenanceFor
    /// always supplies it for a real export.</summary>
    public HumanLayerCounts? HumanLayer { get; init; }

    /// <summary>The session has no EndedAtUtc - exported mid-recording, so the transcript is
    /// incomplete and diarisation has not run (design 2026-08-03 section 11).</summary>
    public bool InProgress { get; init; }

    /// <summary>Non-null when this document is a TIME-RANGE EXCERPT (design 2026-08-04 section 8):
    /// the ACTUAL span the selected rows cover, e.g. "00:12:30-00:18:45 of 01:47:12" - snapped
    /// outward to whole turns, never the requested range. A fact about the document's
    /// completeness, the same category as InProgress, which is why it lives beside it. The INPUT
    /// window is ExcerptRange; renderers never see it and never filter rows.</summary>
    public string? ExcerptSpan { get; init; }
}
```

- [ ] **Step 5: Trim `MetadataFormat`**

In `src/LocalScribe.Core/Projection/MetadataFormat.cs`, delete:
- `VersionLine` (lines 37-42 as of `master`, plus its doc comment)
- `ExportedLine` (lines 44-54, plus its doc comment)
- `RecordedAudioLines` (lines 56-80, plus its doc comment)
- the private `Hms` helper (lines 103-112, plus its doc comment) — `RecordedAudioLines` was its only caller

Keep `DateLine`, `SpeakersHeard`, `DisclaimerFor`, `OmittedLine` and the private `Count`. Drop the now-unused `using System.Linq;` if the compiler reports it as unnecessary (it will not error either way; leave it if `SpeakersHeard` still needs it — check with the build).

- [ ] **Step 6: Trim `MaintenanceService`**

Replace `ProvenanceFor` (lines 1135-1171) with:

```csharp
    /// <summary>Compose the export-only provenance block (design 2026-08-03 section 1). Composed
    /// HERE, where footerText used to compose, so the renderers stay pure serializers. Shared by
    /// ALL THREE textual formats so they can never disagree.
    /// Part 3 (design 2026-08-11 section 3.3) cut this to four facts: the technical provenance it
    /// used to compose is rendered nowhere now, and it rides into the .zip bundle on session.json /
    /// meta.json / manifest.json instead. The TimeProvider and SessionManifest parameters went with
    /// it - nothing here reads a clock or a manifest any more. Public static: tests drive the
    /// mapping directly (no InternalsVisibleTo in this repo).</summary>
    public static ExportProvenance ProvenanceFor(LoadedProjection loaded)
        => new()
        {
            AudioFileName = loaded.Session.ImportedSource?.FileName,
            InProgress = loaded.Session.EndedAtUtc is null,
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
        };
```

Delete `RecordedLegs` (starting line 1173, through the end of the method) and `ReadManifestForExportAsync` (starting line 1199, through the end of the method).

At the three export call sites (`:1062-1063`, `:1090-1091`, `:1117-1118`), delete the `var manifest = await ReadManifestForExportAsync(...)` line and change the next line to:

```csharp
            var provenance = ProvenanceFor(loaded) with { ExcerptSpan = SpanLabel(rows, excerpt, loaded) };
```

> If the `time` field/parameter on the enclosing export method becomes unused, **leave it** — it is part of the service's injected surface and other methods use it. Only remove what the compiler flags as unreachable.

- [ ] **Step 7: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: 0 failed. Any `CS0246` for `RecordedAudioLeg` / `FabricatedSilenceSummary` means a test initializer was missed in Step 2.

- [ ] **Step 8: Confirm nothing write-only survives**

Run: `grep -rn "TranscriptSha256\|RecordedAudio\|ModelAccuracy\|WeightsFile\|FabricatedSilenceSummary\|RecordedAudioLeg" src/LocalScribe.Core/Projection src/LocalScribe.App/Services/MaintenanceService.cs`
Expected: no hits. (`WeightsFile` on `SessionRecord` and `SessionVersion` is a different, still-live property — hits in `src/LocalScribe.Core/Model/` are correct and expected. `ManifestBuilder` and `IntegrityVerifier` keep their own fabricated-silence types; those are the manifest's, not the export's, and are untouched.)

- [ ] **Step 9: Commit**

```bash
git add src/ tests/
git commit -m "refactor(export): trim ExportProvenance to what the lean header renders"
```

---

## Task 9: Update the spec

`docs\specs\localscribe-specs.md` documents the old header field-by-field in six places. Line numbers below are from `master` @ `a6eba76` and will have drifted — locate by quoted text, not by number.

**Files:**
- Modify: `docs/specs/localscribe-specs.md`

- [ ] **Step 1: Rewrite the §11.2 metadata-block order**

Find the bullet beginning `- **The metadata block, in order:** title heading, ` (around `:3605-3610`) and replace it with:

```markdown
- **The metadata block, in order:** title heading, `Date` (start – end (duration), or the
  start-only form for a live session), `Matter(s)`, `Speakers heard`?, `Medium`, `Source`?,
  `Description`?, `Omitted`?, the in-progress notice?, the `Excerpt` line + excerpt notice?, the
  opt-in assistant summary block, then the disclaimer. Seven possible labelled lines — the lean
  legal header (design 2026-08-11 §3.3). `Source` names the original imported file and renders for
  **imports only**: a recorded call's legs are the hardcoded `local.flac` / `remote.flac`, which
  name nothing a reader could use, so a recorded session simply has no `Source` line.
- **The `.docx` and the `.md`/`.txt` order the notice block differently and always have.** `.docx`
  emits the in-progress line *before* the `Excerpt` line; `.md`/`.txt` emit `Excerpt` *before* the
  in-progress notice. The labelled run above is identical in all three. Do not "fix" this without
  deciding to — the existing tests pin both orders.
```

- [ ] **Step 2: Rewrite the export-provenance bullet**

Find the bullet beginning `- **Export provenance** is its own layer (\`ExportProvenance\`)` (around `:3611-3621`) and replace the whole bullet with:

```markdown
- **Export provenance** is its own layer (`ExportProvenance`), composed once at the export call
  site and rendered identically by `.docx`, `.md` and `.txt`. Since design 2026-08-11 §3.3 it
  carries only what the lean header prints: the imported source file name, the human-layer counts
  (which choose the disclaimer variant and feed the `Omitted` line), the in-progress flag and the
  excerpt span. Absent facts render **no line** rather than an empty one.
- **The technical provenance moved, it was not dropped.** App version, session id, export instant,
  transcript version · model · backend, the exact ggml weights file, the model-accuracy tier, the
  transcript's SHA-256 and every per-leg audio SHA-256 are no longer printed in the reading copy.
  They still ride in the `.zip` bundle, which is a verbatim folder copy of the session folder
  (`SessionArchiver`), so `session.json`, `meta.json` and `manifest.json` carry all of them, and
  `Verify integrity` is unchanged. This supersedes the Tier 1 T1-7/T1-8 requirement that these
  appear in the exported **header** — the evidentiary archive keeps the full record, the reading
  copy is lean. A `.docx` alone therefore no longer proves which model produced it.
```

> **If Task 8 was dropped**, change "it carries only what the lean header prints" to "it still carries the full set, but only the four facts below are rendered", and add a sentence noting the unrendered fields remain on the record.

- [ ] **Step 3: Rewrite the fabricated-silence-clause bullet**

Find the bullet beginning `- **Every recorded-audio hash carries a fabricated-silence clause**` (around `:3622-3628`) and replace it with:

```markdown
- **Recorded-audio hashes are no longer printed in the exported header** (design 2026-08-11 §3.3),
  so the fabricated-silence clause that had to travel with every hash no longer has a hash to
  travel with. Both still live in `manifest.json`, which rides into the `.zip` bundle and which
  `Verify integrity` reads. The rule that produced the clause stands and binds any future surface
  that prints a hash: a hash presented without it would certify machine-generated zeros as
  original recorded audio.
```

- [ ] **Step 4: Rewrite the human-edits and corrected-turn bullets**

Find the bullet beginning `- **Human edits** counts five things separately` (around `:3629-3634`) and the bullet beginning `- **Corrected-turn disclosure:**` (around `:3635-3640`), and replace **both** with:

```markdown
- **Human-edit disclosure is prose, not counts** (design 2026-08-11 §3.3). The `Human edits` line
  and its five counts are gone; the non-optional disclaimer has two variants instead, chosen from
  the same counts. Untouched: "This transcript was generated by automated speech recognition and
  may contain errors. It is not a certified record." Human-edited: "This transcript was generated
  by automated speech recognition and reviewed and corrected by a person. It may contain errors and
  is not a certified record." A reader cannot tell the text is conditional; it is simply accurate
  in both cases, and this is what carries the T1-8 anti-concealment duty.
- **The variant is chosen from the four HUMAN counts only** — text corrections, split turns, manual
  speaker assignments, named speakers. `SuppressedDuplicates` is deliberately excluded: it is a
  machine act, and a transcript whose only change was dedup was reviewed by nobody.
- **Auto-suppressed duplicates print separately**, as `Omitted: N duplicate turns (echo)`, and only
  when `N > 0`. The disclaimer does not cover them because they are not a human act, and the spec
  calls their absence concealment. Silent on single-leg imports, which suppress nothing.
- **There is no per-turn `[text corrected]` mark.** It was deleted outright in design 2026-08-11
  §3.3 — along with `ExportOptions.MarkCorrectedTurns`, `ExportSetting.MarkCorrectedTurns` and the
  export dialog's checkbox — not merely defaulted off. The disclaimer says the same thing once, in
  language a reader can quote. `DisplayRow.HasCorrection` survives: the read view still shows a
  per-row indicator.
```

- [ ] **Step 5: Rewrite the participants bullet**

Find the bullet beginning `- **Participants in the header = the user-curated roster**` (around `:3639-3643`) and replace it with:

```markdown
- **`Speakers heard` replaced `Participants` in the exported header** (design 2026-08-11 §3.3):
  who actually speaks in the rows, first-appearance order, derived from the rows so it cannot drift
  from the body. `meta.json` keeps its user-curated participant roster — it drives the speaker UI
  and the matter mirror, and `session.txt` still prints it; only the exported document stopped.
  **Accepted consequence:** a declared attendee who never speaks disappears from the exported
  document. For body-worn footage and interviews that is correct — inaudible people are not in the
  transcript — but it is a deliberate trade, not an oversight.
```

- [ ] **Step 6: Fix the three cross-references to the deleted `Human edits` line**

Around `:2384` — find `rendered on the **Human edits** metadata line as` … `N auto-suppressed duplicate segments` … and replace that clause with:

```markdown
  export as part of the export provenance, rendered on its own **`Omitted`** metadata line as
  `N duplicate turns (echo)` in `.docx`, `.md` and `.txt`, and only when `N > 0`. A reader of an
  exported document can therefore tell that hiding happened and how often, even though the hidden
  text is not in the document.
```

Around `:2453` — find the `.md` export-dialect bullet listing `(App, Date, Matter(s), Participants, Medium, Description, Session ID, Exported, Transcript version, Weights file, Model accuracy, Audio + per-leg audio SHA-256 with the fabricated-silence clause, Transcript SHA-256, Speakers heard, Human edits, Excerpt)` and replace that parenthesis with:

```markdown
  metadata block (Date, Matter(s), Speakers heard, Medium, Source, Description, Omitted, Excerpt),
```

…and in the same bullet delete `, mark corrected turns` from the trailing list of export options.

Around `:2503` — find `that count is surfaced to the reader in the exports' \`Human edits\` line ("… N auto-suppressed duplicate segments")` and replace with:

```markdown
   this step removed; that count is surfaced to the reader on the exports' `Omitted` line
   ("N duplicate turns (echo)"), so the suppression is disclosed rather than silent.
```

- [ ] **Step 7: Check for stragglers**

Run: `grep -n "Human edits\|text corrected\|MarkCorrectedTurns\|Participants in the header" docs/specs/localscribe-specs.md`
Expected: no hits, or only hits inside clearly-dated historical/superseded sections. Fix any live prose the grep finds.

- [ ] **Step 8: Commit**

```bash
git add docs/specs/localscribe-specs.md
git commit -m "docs(spec): the exported header is lean; disclosure is prose"
```

---

## Task 10: Whole-branch verification

**Files:** none — verification only.

- [ ] **Step 1: Full clean gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: 0 failed. Record the exact total. Starting from 2587 and applying every add/delete in this plan, expect **~2590 ± 5**; the number matters less than *0 failed* and than being able to account for the delta.

- [ ] **Step 2: Gate with no models on disk**

The standing criterion added during Part 1: two defects passed only because this machine holds every ladder rung, and CI fetches no models.

```bash
mkdir -p "$TMPDIR/ls-empty-models" 2>/dev/null || mkdir -p /tmp/ls-empty-models
LOCALSCRIBE_MODELS="$TMPDIR/ls-empty-models" dotnet test LocalScribe.slnx --filter "Category!=Fixture"
```

PowerShell equivalent:

```powershell
$empty = Join-Path $env:TEMP "ls-empty-models"
New-Item -ItemType Directory -Force $empty | Out-Null
$env:LOCALSCRIBE_MODELS = $empty
dotnet test LocalScribe.slnx --filter "Category!=Fixture"
Remove-Item Env:\LOCALSCRIBE_MODELS
```

Expected: same result as Step 1. Part 3 touches no model-resolution code, so a difference here is a *pre-existing* environment-dependent test, not a Part 3 regression — report it rather than fixing it inside this branch.

- [ ] **Step 3: Confirm the warning baseline held**

Run: `dotnet build LocalScribe.slnx 2>&1 | grep -c "warning"`
Expected: the same 7 `CS8602` warnings in `DocxRendererTests.cs` (lines may have shifted) and nothing new. A new warning in a file this plan edited is a defect; investigate before proceeding.

- [ ] **Step 4: Eyeball a real export**

The suite asserts strings; nothing asserts that a Word document *opens*. `A_document_with_every_remaining_metadata_line_stacked_is_schema_valid` runs `OpenXmlValidator`, which is the closest automated proxy, but Word's own corruption heuristics are stricter than the schema.

Report to the owner that a manual smoke is outstanding: export one real session to `.docx`, `.md` and `.txt`, open the `.docx` in Word, and confirm (a) no corruption prompt, (b) the header is the seven lines, (c) pages 2+ still carry the running head, (d) the footer still reads `{title}  Page N of M`. **Do not launch `LocalScribe.App.exe` while a build is running** — it locks `Core.dll` (`MSB3027`).

- [ ] **Step 5: Report**

Summarise for the owner: the final test total and delta, whether Task 8 was taken or dropped, the outstanding manual smoke, and the open item below.

---

## Open item carried forward (not Part 3's problem)

No import path writes the `[transcription engine: ...]` marker — its only writer is `SessionController` (live). Successful imports compensate via `session.json`, but a **salvaged** import discloses the engine nowhere at all. Part 3 removes engine disclosure from the header *by design*, so this is not Part 3's to fix; it is recorded here because Part 3 is the last change that touches the disclosure story. The full follow-up list is in `.superpowers/sdd/2026-08-11-part1-import-resilience/progress.md`.

---

## Self-review

**Spec coverage (§3.1-§3.5):**

| Spec requirement | Task |
|---|---|
| Kept set: title, Date, Matter(s), Speakers heard, Medium, Source, Description?, notices, summary, disclaimer | 3, 4, 5 |
| Removed set: App, Participants, Session ID, Exported, Transcript version, Weights file, Model accuracy, Audio SHA-256, Transcript SHA-256, per-leg hashes, Human edits | 3, 4, 5 (absent-set tests), 7, 8 |
| Trap 1 — model name welded into `Transcript version` | 3, 4, 5 (`DoesNotContain("Transcript version")` + `DoesNotContain("large-v3-turbo")`) |
| Trap 2 — `Source` must be built | 3, 4, 5 — **narrowed by owner decision 2 to imports only** |
| Trap 3 — hash + fabricated-silence clause are one unit | 8 (both deleted together, in one commit) |
| Disclaimer, two variants | 1 |
| `Omitted:` only when `N > 0` | 2, and asserted per format in 3, 4, 5 |
| `[text corrected]` deleted (mark, `ExportOptions`, `ExportSetting`, VM, XAML) | 6 |
| "Settings migration drops the key" | **Not implemented — the handoff verified there is nothing to migrate.** Noted in Task 6. |
| Testing §: golden-header per format, `Source` survives, disclaimer variants, `Omitted` at N>0 and N==0, every `MarkCorrectedTurns` surface deleted | 1-6, 10 |
| Spec doc updated | 9 |

**Placeholder scan:** every code step carries the literal code. No "TBD", no "similar to Task N" (Tasks 4 and 5 repeat their header blocks in full rather than referring back to Task 3), no undefined types.

**Type consistency:** `MetadataFormat.DisclaimerFor(HumanLayerCounts?)` and `MetadataFormat.OmittedLine(HumanLayerCounts?)` are declared in Tasks 1-2 and called with `provenance.HumanLayer` in Tasks 3, 4, 5 — same name, same parameter type, both places. `ExportNotices.Disclaimer` / `.DisclaimerEdited` are consistent across Tasks 1, 3, 4, 5, 8. `ProvenanceFor(LoadedProjection)` in Task 8 matches its three call sites and its surviving tests.

**Known gap:** nothing automated proves Word opens the resulting `.docx` — `OpenXmlValidator` is a schema check, not Word's corruption heuristic. Task 10 Step 4 hands that to the owner as a manual smoke rather than pretending it is covered.
