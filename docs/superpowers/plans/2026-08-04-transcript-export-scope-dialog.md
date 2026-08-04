# Transcript Export Round 2: Export Scope & Dialog — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `.txt` export, remembered export choices, a cadence-interval knob, a filename template, an opt-in assistant-summary section, and time-range excerpt export to LocalScribe's session export dialog.

**Architecture:** Renderers stay pure serializers — excerpt row filtering and summary composition happen in `MaintenanceService`, which is the only place holding both the loaded projection and the export-time inputs. `ExportProvenance` grows a completeness field (`ExcerptSpan`) beside the existing `InProgress`; the summary rides as its own `ExportSummary?` renderer parameter because it is content, not provenance. Preferences persist in one additive `ExportSetting` record on `Settings`.

**Tech Stack:** C# / .NET 10, WPF (+ Wpf.Ui), CommunityToolkit.Mvvm, DocumentFormat.OpenXml, xUnit.

## Global Constraints

- **Build/test:** `dotnet build` / `dotnet test` against `F:\LocalScribe\LocalScribe.slnx`. A running `LocalScribe.App.exe` locks `Core.dll` → `MSB3027`. Close it; never blanket-kill processes.
- **Test baseline:** App 928/928, Mcp 6/6, Core 1138/1140. The two Core failures are `DiarisationFixtureTests.Der_within_baseline_plus_epsilon` and `GoldenCorpusFixtureTests.Golden_pair_wer_stays_at_baseline` (environment-dependent fixtures needing model weights / golden corpora). `WhisperFixtureTests.Tiny_model_transcribes_synthetic_tone_...` is **intermittent**. **Judge regressions by failing test NAME, never by count.**
- **ASCII source files.** Non-ASCII in string literals MUST be `\u` escapes. The Edit tool silently converts escapes to literal glyphs — byte-scan every touched file before committing (zero bytes > 127, CRLF intact). This bit seven separate tasks in Round 1.
- **Stage files by name.** Never `git add -A` / `git add .` / `git commit -a`, never `git clean` — `tools/diar-eval/`, `.ai-code-review/` and `.claude/` are deliberately untracked.
- **Word `pPr` children are schema-ordered:** `pStyle(1) → widowControl(6) → numPr(7) → suppressLineNumbers(8) → pBdr(9) → tabs(11) → spacing(22) → ind(23)`. The OpenXML SDK accepts any order and tests pass; Word calls the file corrupt. **Microsoft Learn's `pPr` pages list children ALPHABETICALLY — that is NOT schema order.** Use the XSD.
- **`STYLEREF` takes the style NAME, not the styleId** — `"Transcript Speaker"`, not `"TranscriptSpeaker"`.
- **`FileShare.Read` excludes WRITERS.** Any new read path over a session folder must use `FileShare.ReadWrite`.
- **Transcripts are legal evidence.** No path may drop, reorder, or silently rewrite content. `TimestampCadence.Chunk` must keep returning `row.Text` verbatim for an unsplit row. The excerpt selector filters whole rows and never edits one.
- **Invariant culture** for every exported string (`CultureInfo.InvariantCulture`). Page size remains the ONE machine-locale dependence in the docx path.
- **Spec:** `docs/superpowers/specs/2026-08-04-transcript-export-scope-dialog-design.md`.
- **Branch:** `feat/export-scope-dialog-2026-08-04` (already created, spec committed at `329c68c`).

---

## File Structure

**Created:**
- `src/LocalScribe.Core/Projection/ExportOptions.cs` — the format-neutral export toggles (renamed from `DocxOptions`).
- `src/LocalScribe.Core/Projection/ExportNotices.cs` — fixed strings shared by all export renderers.
- `src/LocalScribe.Core/Projection/ExportSummary.cs` — the composed summary block handed to renderers.
- `src/LocalScribe.Core/Projection/ExcerptRange.cs` — the millisecond window (`FromMs`, `ToMs`).
- `src/LocalScribe.Core/Projection/ExcerptSelector.cs` — whole-row overlap selection + actual-span computation.
- `src/LocalScribe.Core/Model/ExportFormat.cs` — the persisted format enum, moved out of the App project.
- `tests/LocalScribe.Core.Tests/ExportNoticesTests.cs`
- `tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs`
- `tests/LocalScribe.Core.Tests/ExcerptSelectorTests.cs`
- `tests/LocalScribe.App.Tests/ExportFileNamesTests.cs`

**Modified:**
- `src/LocalScribe.Core/Projection/DocxRenderer.cs` — loses `DocxOptions` + two constants; gains summary section, excerpt metadata line + header paragraph.
- `src/LocalScribe.Core/Projection/MarkdownRenderer.cs` — constant references, summary section, excerpt notice.
- `src/LocalScribe.Core/Projection/PlainTextRenderer.cs` — gains `Write(...)`; save-time `Render(...)` untouched.
- `src/LocalScribe.Core/Projection/ExportProvenance.cs` — gains `ExcerptSpan`.
- `src/LocalScribe.Core/Model/Settings.cs` — gains `ExportSetting`.
- `src/LocalScribe.Core/Storage/LocalScribeJson.cs` — registers `JsonStringEnumConverter<ExportFormat>`.
- `src/LocalScribe.App/Services/MaintenanceService.cs` — `ExportTextAsync`, `FilenameTokensAsync`, `ResolveExcerptAsync`, `LatestSummaryProvider`, `SummaryFor`, excerpt threading.
- `src/LocalScribe.App/Services/ExportFileNames.cs` — gains `Expand`.
- `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs` — settings seam, `.txt`, cadence, summary toggle, excerpt fields.
- `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs` — filename-template property.
- `src/LocalScribe.App/ExportDialog.xaml`, `ExportFormatToBool.cs`, `SettingsPage.xaml`, `App.xaml.cs`.

---

## Task 1: `ExportOptions` + `ExportNotices` foundation

Mechanical rename and extraction. **No behaviour change** — every exported byte must be identical after this task.

**Files:**
- Create: `src/LocalScribe.Core/Projection/ExportOptions.cs`, `src/LocalScribe.Core/Projection/ExportNotices.cs`
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs:7-20` (delete `DocxOptions`), `:47-54` (delete both constants), and every `DocxOptions` / `Disclaimer` / `InProgressNotice` use
- Modify: `src/LocalScribe.Core/Projection/MarkdownRenderer.cs:43-107`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs:999,1021` (signatures)
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs:74`
- Test: `tests/LocalScribe.Core.Tests/ExportNoticesTests.cs` (create)

**Interfaces:**
- Produces: `LocalScribe.Core.Projection.ExportOptions` (`IncludeTimestamps`, `IncludeMarkers`, `TimestampIntervalMs`); `LocalScribe.Core.Projection.ExportNotices.Disclaimer` / `.InProgressNotice` / `.ExcerptNotice` / `.SummaryHeading`. Consumed by every later task.
- `DocxRenderer.ContinuationMaxChars` **stays on `DocxRenderer`** — it is genuine docx page geometry that other renderers borrow deliberately.

- [ ] **Step 1: Write the failing pinning test**

Create `tests/LocalScribe.Core.Tests/ExportNoticesTests.cs`:

```csharp
using LocalScribe.Core.Projection;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>Pins the two Round 1 strings across their move off DocxRenderer (design 2026-08-04
/// section 2). A move must not reword an evidentiary notice, and only an explicit assertion
/// makes that impossible to do by accident.</summary>
public sealed class ExportNoticesTests
{
    [Fact]
    public void Disclaimer_is_byte_identical_to_the_round_1_string()
        => Assert.Equal(
            "This transcript was generated by automated speech recognition and may contain errors. "
            + "It is not a certified record.",
            ExportNotices.Disclaimer);

    [Fact]
    public void In_progress_notice_is_byte_identical_to_the_round_1_string()
        => Assert.Equal(
            "IN-PROGRESS RECORDING \u2014 transcript incomplete, speaker separation not yet applied.",
            ExportNotices.InProgressNotice);

    [Fact]
    public void Excerpt_notice_is_the_locked_wording()
        => Assert.Equal("EXCERPT \u2014 not the complete transcript.", ExportNotices.ExcerptNotice);

    [Fact]
    public void Summary_heading_does_not_collide_with_the_content_own_first_header()
    {
        // AssistantPrompts.SectionHeaders[0] is literally "Summary"; the export section must not
        // repeat it (design 2026-08-04 section 7).
        Assert.Equal("Assistant summary", ExportNotices.SummaryHeading);
        Assert.NotEqual("Summary", ExportNotices.SummaryHeading);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportNoticesTests"`
Expected: FAIL — `ExportNotices` does not exist (compile error CS0103/CS0246).

- [ ] **Step 3: Create `ExportNotices`**

Create `src/LocalScribe.Core/Projection/ExportNotices.cs`:

```csharp
namespace LocalScribe.Core.Projection;

/// <summary>Fixed strings shared by every export renderer (design 2026-08-04 section 2). Moved
/// off DocxRenderer, which MarkdownRenderer already reached into for text and which a third and
/// fourth renderer would too. Non-ASCII via \u escapes (ASCII source rule) - a literal em dash
/// here breaks the byte-scan gate.</summary>
public static class ExportNotices
{
    /// <summary>Non-optional on every exported transcript (design 3.3).</summary>
    public const string Disclaimer =
        "This transcript was generated by automated speech recognition and may contain errors. "
        + "It is not a certified record.";

    /// <summary>Stamped on a session exported mid-recording (design 2026-08-03 section 11).</summary>
    public const string InProgressNotice =
        "IN-PROGRESS RECORDING \u2014 transcript incomplete, speaker separation not yet applied.";

    /// <summary>Mandatory on every page of a time-range excerpt (design 2026-08-04 section 8),
    /// per the locked no-content-deletion rule.</summary>
    public const string ExcerptNotice = "EXCERPT \u2014 not the complete transcript.";

    /// <summary>Deliberately NOT "Summary": the generated content's own first section header is
    /// literally "## Summary" (AssistantPrompts.SectionHeaders), and the two would collide.</summary>
    public const string SummaryHeading = "Assistant summary";
}
```

- [ ] **Step 4: Create `ExportOptions` and delete `DocxOptions`**

Create `src/LocalScribe.Core/Projection/ExportOptions.cs`:

```csharp
namespace LocalScribe.Core.Projection;

/// <summary>The user-facing export toggles (design 3.3 + 2026-08-02 item 5; renamed from
/// DocxOptions in design 2026-08-04 section 2, where a fourth renderer made the old name plainly
/// wrong). House style mirrors PhantomBleedOptions: sealed record + { get; init; } with inline
/// defaults. Format-neutral and shared by the .docx, .md and .txt export renderers.</summary>
public sealed record ExportOptions
{
    public bool IncludeTimestamps { get; init; } = true;
    public bool IncludeMarkers { get; init; } = true;
    /// <summary>Extra mid-turn stamp cadence (design 2026-08-02 item 5): a named "(cont'd)"
    /// continuation paragraph starts at the first segment boundary at/after this many ms since the
    /// last shown stamp. 0 (default) = off. Renderers force it off when IncludeTimestamps is
    /// false. Independent of - and additional to - the always-on ContinuationMaxChars trigger
    /// (design 2026-08-03 section 8).</summary>
    public int TimestampIntervalMs { get; init; } = 0;
}
```

Then delete lines 7-20 of `DocxRenderer.cs` (the `DocxOptions` record and its doc comment) and lines 47-54 (the `Disclaimer` and `InProgressNotice` constants with their doc comments). Leave `ContinuationMaxChars` in place.

- [ ] **Step 5: Update every reference**

In `DocxRenderer.cs`: replace `DocxOptions` with `ExportOptions` (3 sites: `Write` signature `:66`, `TextColumnTwips` `:257`, `TurnLabel` `:285`); replace bare `InProgressNotice` with `ExportNotices.InProgressNotice` (`:368`) and bare `Disclaimer` with `ExportNotices.Disclaimer` (`:376`).

In `MarkdownRenderer.cs`: replace `DocxOptions` → `ExportOptions` (`:45`); `DocxRenderer.InProgressNotice` → `ExportNotices.InProgressNotice` (`:67`); `DocxRenderer.Disclaimer` → `ExportNotices.Disclaimer` (`:68`). Leave `DocxRenderer.ContinuationMaxChars` (`:86`) alone.

In `MaintenanceService.cs`: `DocxOptions options` → `ExportOptions options` (`:999`, `:1021`).

In `ExportDialogViewModel.cs:74`: `new DocxOptions` → `new ExportOptions`.

Then find any remaining references in tests:

```bash
cd F:/LocalScribe && grep -rln "DocxOptions\|DocxRenderer\.Disclaimer\|DocxRenderer\.InProgressNotice" src tests
```

Update each hit the same way.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx`
Expected: PASS. App 928/928, Mcp 6/6, Core 1142/1144 (1138 + the 4 new `ExportNoticesTests`, with the same 2 known fixture failures **by name**).

Because this task is a pure rename, **every pre-existing renderer assertion must still pass untouched**. If a golden-output test fails, a string was reworded — revert and redo the move verbatim.

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/ExportOptions.cs src/LocalScribe.Core/Projection/ExportNotices.cs src/LocalScribe.Core/Projection/DocxRenderer.cs src/LocalScribe.Core/Projection/MarkdownRenderer.cs src/LocalScribe.App/Services/MaintenanceService.cs src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs tests/LocalScribe.Core.Tests/ExportNoticesTests.cs
# plus any test files the grep in Step 5 turned up
git commit -m "refactor(export): ExportOptions + ExportNotices, no behaviour change"
```

---

## Task 2: `PlainTextRenderer.Write`

**Files:**
- Modify: `src/LocalScribe.Core/Projection/PlainTextRenderer.cs`
- Test: `tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs` (create)

**Interfaces:**
- Consumes: `ExportOptions`, `ExportNotices` (Task 1).
- Produces: `PlainTextRenderer.Write(TranscriptHeader header, SessionTextView meta, ExportProvenance provenance, IReadOnlyList<DisplayRow> rows, string timestampsMode, ExportOptions options) : string`. Task 3 calls it; Task 9 adds an `ExportSummary? summary` parameter after `provenance`.

The existing `Render(...)` (save-time `transcript.txt`) is **not touched** — its byte-identity is load-bearing.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs`:

```csharp
using LocalScribe.Core.Projection;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>The .txt export dialect (design 2026-08-04 section 3): the MarkdownRenderer.Write
/// metadata/disclaimer/cadence contract with no decoration, CRLF line endings, and no hard
/// wrapping.</summary>
public sealed class PlainTextRendererWriteTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 3, 9, 0, 0, TimeSpan.Zero);

    private static TranscriptHeader Header() =>
        new("Doe intake", "Webex", Start, 1_800_000, "large-v3-turbo", "cuda");

    private static SessionTextView Meta() =>
        new("Doe intake", ["Doe v Roe (2026/014)"], ["Sam (Counsel)"], Start,
            Start.AddMinutes(30), 1_800_000, "Webex", "", Summary: null);

    private static DisplayRow Turn(long startMs, long endMs, string name, string text) =>
        new() { StartMs = startMs, EndMs = endMs, DisplayName = name, Text = text };

    [Fact]
    public void Uses_crlf_and_renders_undecorated_metadata_lines()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(),
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains("\r\n", txt);
        Assert.DoesNotContain("\n\n\n", txt.Replace("\r", ""));
        Assert.Contains("Matter(s): Doe v Roe (2026/014)\r\n", txt);
        Assert.Contains("Participants: Sam (Counsel)\r\n", txt);
        Assert.DoesNotContain("**", txt);                       // no markdown decoration
        Assert.DoesNotContain("- **", txt);
    }

    [Fact]
    public void Renders_the_non_optional_disclaimer()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(),
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());
        Assert.Contains(ExportNotices.Disclaimer, txt);
    }

    [Fact]
    public void In_progress_export_renders_the_notice_and_a_finalised_one_does_not()
    {
        var rows = new[] { Turn(0, 4000, "Sam", "hello") };
        string live = PlainTextRenderer.Write(Header(), Meta(),
            new ExportProvenance { InProgress = true }, rows, "relative", new ExportOptions());
        string done = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(),
            rows, "relative", new ExportOptions());

        Assert.Contains(ExportNotices.InProgressNotice, live);
        Assert.DoesNotContain(ExportNotices.InProgressNotice, done);
    }

    [Fact]
    public void Turn_renders_as_stamp_name_colon_text_on_one_unwrapped_line()
    {
        string longText = new string('a', 400) + " end";
        string txt = PlainTextRenderer.Write(Header(), Meta(),
            new ExportProvenance(), [Turn(0, 4000, "Sam", longText)], "relative", new ExportOptions());

        Assert.Contains("[00:00] Sam: " + longText + "\r\n", txt);   // never hard-wrapped
    }

    [Fact]
    public void Timestamps_off_drops_the_stamp_but_keeps_the_name()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(),
            [Turn(0, 4000, "Sam", "hello")], "relative",
            new ExportOptions { IncludeTimestamps = false });

        Assert.Contains("Sam: hello\r\n", txt);
        Assert.DoesNotContain("[00:00]", txt);
    }

    [Fact]
    public void Markers_render_bracketed_and_drop_entirely_when_toggled_off()
    {
        var rows = new DisplayRow[]
        {
            new() { IsMarker = true, StartMs = 1000, EndMs = 1000, Text = "Recording paused" },
            Turn(2000, 4000, "Sam", "hello"),
        };
        string on = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(),
            rows, "relative", new ExportOptions());
        string off = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(),
            rows, "relative", new ExportOptions { IncludeMarkers = false });

        Assert.Contains("[Recording paused]\r\n", on);
        Assert.DoesNotContain("Recording paused", off);
    }

    [Fact]
    public void Cadence_chunks_break_at_the_same_boundaries_as_markdown()
    {
        // The three formats must not disagree about where a turn breaks (design 2026-08-03
        // section 8): ContinuationMaxChars is shared, not redefined per renderer.
        var row = new DisplayRow
        {
            StartMs = 0, EndMs = 24000, DisplayName = "Sam", Text = "one two three four five",
            Segments =
            [
                new RowSegment { StartMs = 0, EndMs = 4000, Text = "one" },
                new RowSegment { StartMs = 4400, EndMs = 9000, Text = "two" },
                new RowSegment { StartMs = 9400, EndMs = 14000, Text = "three" },
                new RowSegment { StartMs = 14400, EndMs = 19000, Text = "four" },
                new RowSegment { StartMs = 19400, EndMs = 24000, Text = "five" },
            ],
        };
        var options = new ExportOptions { TimestampIntervalMs = 15000 };

        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(),
            [row], "relative", options);
        string md = MarkdownRenderer.Write(Header(), Meta(), new ExportProvenance(),
            [row], "relative", options);

        // Same split point, same continuation stamp, different decoration.
        Assert.Contains("[00:00] Sam: one two three four\r\n", txt);
        Assert.Contains("[00:19] Sam (cont'd): five\r\n", txt);
        Assert.Contains(":** one two three four\n", md);
        Assert.Contains("**[00:19] Sam (cont'd):** five\n", md);
    }

    [Fact]
    public void Save_time_render_is_untouched_and_still_uses_lf()
    {
        // transcript.txt byte-identity is load-bearing (SessionProjectionLoader doc comment).
        string saved = PlainTextRenderer.Render(Header(), [Turn(0, 4000, "Sam", "hello")], "relative");
        Assert.DoesNotContain("\r\n", saved);
        Assert.Contains("[00:00] Sam: hello\n", saved);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~PlainTextRendererWriteTests"`
Expected: FAIL — `PlainTextRenderer.Write` does not exist.

- [ ] **Step 3: Implement `Write`**

Append to `src/LocalScribe.Core/Projection/PlainTextRenderer.cs` (inside the class, after `Render`), and add `using LocalScribe.Core.Projection;` is unnecessary — same namespace:

```csharp
    /// <summary>CRLF, not LF (design 2026-08-04 section 3): .txt is the format that gets pasted
    /// into Windows tooling and email. The save-time Render above keeps LF because
    /// transcript.txt's byte-identity is load-bearing.</summary>
    private const string Nl = "\r\n";

    /// <summary>Full-document EXPORT render at MarkdownRenderer.Write parity (design 2026-08-04
    /// section 3): the SAME metadata block content rules, the SAME non-optional disclaimer, the
    /// SAME cadence chunking and (cont'd) labels - undecorated, and never hard-wrapped, because a
    /// hard wrap would insert newlines into evidentiary text. Rows arrive pre-resolved from
    /// TranscriptProjection.Build and are emitted VERBATIM. The save-time Render(...) path above
    /// is a separate, untouched surface. No line numbers and no footer: .txt has no pages, so
    /// page:line citation does not exist here.</summary>
    public static string Write(TranscriptHeader header, SessionTextView meta,
        ExportProvenance provenance, IReadOnlyList<DisplayRow> rows, string timestampsMode,
        ExportOptions options)
    {
        var sb = new StringBuilder();
        sb.Append(meta.Title).Append(Nl).Append(Nl);
        AppendMeta(sb, "App", header.App);
        AppendMeta(sb, "Date", MetadataFormat.DateLine(meta));
        AppendMeta(sb, "Matter(s)",
            meta.Matters.Count == 0 ? "(none)" : string.Join(", ", meta.Matters));
        AppendMeta(sb, "Participants",
            meta.Participants.Count == 0 ? "(none)" : string.Join(", ", meta.Participants));
        AppendMeta(sb, "Medium", meta.Medium);
        if (!string.IsNullOrEmpty(meta.Description)) AppendMeta(sb, "Description", meta.Description);
        AppendMeta(sb, "Transcript version", MetadataFormat.VersionLine(provenance));
        if (!string.IsNullOrEmpty(provenance.AudioFileName))
            AppendMeta(sb, "Audio", provenance.AudioFileName);
        if (!string.IsNullOrEmpty(provenance.AudioSha256))
            AppendMeta(sb, "Audio SHA-256", provenance.AudioSha256);
        string speakers = MetadataFormat.SpeakersHeard(rows);
        if (speakers.Length > 0) AppendMeta(sb, "Speakers heard", speakers);
        if (provenance.InProgress)
            sb.Append(Nl).Append(ExportNotices.InProgressNotice).Append(Nl);
        sb.Append(Nl).Append(ExportNotices.Disclaimer).Append(Nl);

        foreach (var row in rows)
        {
            if (row.IsMarker)
            {
                if (options.IncludeMarkers)
                    sb.Append(Nl).Append('[').Append(row.Text).Append(']').Append(Nl);
                continue;   // toggled-off marker: dropped entirely, no stray blank line
            }
            // Cadence chunking at MarkdownRenderer/DocxRenderer parity (design 2026-08-03
            // section 8): the three formats must not disagree about where a turn breaks, so
            // ContinuationMaxChars is shared rather than redefined here.
            var chunks = TimestampCadence.Chunk(row,
                options.IncludeTimestamps ? options.TimestampIntervalMs : 0,
                DocxRenderer.ContinuationMaxChars);
            sb.Append(Nl).Append(Label(row.DisplayName, row.StartMs, options, timestampsMode,
                header.StartedAtLocal)).Append(": ").Append(chunks[0].Text).Append(Nl);
            for (int i = 1; i < chunks.Count; i++)
                sb.Append(Nl).Append(Label(row.DisplayName, chunks[i].StampMs, options,
                    timestampsMode, header.StartedAtLocal))
                  .Append(" (cont'd): ").Append(chunks[i].Text).Append(Nl);
        }
        return sb.ToString();
    }

    private static string Label(string? name, long stampMs, ExportOptions options,
        string timestampsMode, DateTimeOffset startedAtLocal)
        => options.IncludeTimestamps
            ? "[" + TimestampFormat.Stamp(stampMs, timestampsMode, startedAtLocal) + "] " + name
            : name ?? "";

    private static void AppendMeta(StringBuilder sb, string label, string value)
        => sb.Append(label).Append(": ").Append(value).Append(Nl);
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~PlainTextRendererWriteTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/PlainTextRenderer.cs tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs
git commit -m "feat(export): .txt export renderer at markdown parity"
```

---

## Task 3: `.txt` end-to-end — `ExportTextAsync` + the fourth format

**Files:**
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (after `ExportMarkdownAsync`, `:1034`)
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs:7,44,57-64,79-90`
- Modify: `src/LocalScribe.App/ExportFormatToBool.cs:11-13`
- Modify: `src/LocalScribe.App/ExportDialog.xaml:17`
- Test: `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`

**Interfaces:**
- Consumes: `PlainTextRenderer.Write` (Task 2).
- Produces: `MaintenanceService.ExportTextAsync(string sessionId, string destPath, ExportOptions options, CancellationToken ct) : Task`; `ExportFormat.Text`.

- [ ] **Step 1: Write the failing test**

Append to `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`:

```csharp
    [Fact]
    public async Task Text_export_sanitized_txt_filename_filter_and_written_file()
    {
        // design 2026-08-04 section 3: same Save-As shape as markdown, .txt filter, CRLF, no BOM.
        var (svc, _, rep) = await MakeAsync();
        SavePathRequest? seen = null;
        string dest = Path.Combine(_root, "out.txt");
        var vm = new ExportDialogViewModel("s1", "Doe: intake/2026", svc,
            req => { seen = req; return dest; }, _ => { }, rep, a => a())
        { Format = ExportFormat.Text };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Doe_ intake_2026.txt", seen!.DefaultFileName);
        Assert.Equal("Plain text (*.txt)|*.txt", seen.Filter);
        Assert.True(File.Exists(dest));

        byte[] bytes = await File.ReadAllBytesAsync(dest);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        string txt = await File.ReadAllTextAsync(dest);
        Assert.StartsWith("Doe intake\r\n", txt);          // meta title, not the raw arg
        Assert.Single(rep.Infos);
        Assert.Empty(rep.Errors);
    }

    [Fact]
    public async Task Option_toggles_show_for_text_too()
    {
        var (svc, _, rep) = await MakeAsync();
        var vm = new ExportDialogViewModel("s1", "T", svc, _ => null, _ => { }, rep, a => a())
        { Format = ExportFormat.Text };
        Assert.True(vm.ShowOptionToggles);
        Assert.False(vm.IsDocx);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportDialogViewModelTests.Text_export"`
Expected: FAIL — `ExportFormat.Text` does not exist.

- [ ] **Step 3: Add `ExportTextAsync`**

Insert into `MaintenanceService.cs` immediately after `ExportMarkdownAsync` (after line 1034):

```csharp
    /// <summary>Export one session as a formatted .txt transcript (design 2026-08-04 section 3).
    /// Line-for-line mirror of ExportMarkdownAsync: session gate, output-file-only cleanup on
    /// failure, shared SessionProjectionLoader read, and the IDENTICAL ProvenanceFor composition.
    /// The document is rendered BEFORE the output stream opens, so a projection/render failure
    /// leaves a pre-existing Save-As target intact (markCreated contract). UTF-8 without BOM;
    /// PlainTextRenderer.Write supplies the CRLF line endings.</summary>
    public Task ExportTextAsync(string sessionId, string destPath, ExportOptions options,
        CancellationToken ct)
        => ExportWithOutputCleanupAsync(destPath, markCreated => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                throw new InvalidOperationException("The session no longer exists.");
            var loaded = await SessionProjectionLoader.LoadAsync(paths, settings.Current, time, sessionId, ct: inner);
            string text = PlainTextRenderer.Write(loaded.Header, loaded.TextView,
                ProvenanceFor(loaded), loaded.Rows, settings.Current.Timestamps, options);
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            markCreated();
            await fs.WriteAsync(Encoding.UTF8.GetBytes(text), inner);   // GetBytes emits no BOM
            return true;
        }, ct));
```

- [ ] **Step 4: Add the format to the VM, converter and XAML**

`ExportDialogViewModel.cs:7`:
```csharp
public enum ExportFormat { Zip, Docx, Markdown, Text }
```

`:44` — widen the toggle gate:
```csharp
    /// <summary>The IncludeTimestamps/IncludeMarkers/ExtraTimestamps checkboxes apply to ALL
    /// THREE textual formats (design 2026-07-18 section 3 + 2026-08-04 section 3) - docx, markdown
    /// AND plain text; hidden for zip, which archives the session folder as-is. This generalizes
    /// the old IsDocx visibility gate (kept above, unbroken).</summary>
    public bool ShowOptionToggles =>
        Format is ExportFormat.Docx or ExportFormat.Markdown or ExportFormat.Text;
```

`:57-64` — add the request case (insert before the `_ =>` docx default):
```csharp
            ExportFormat.Text => new SavePathRequest(
                ExportFileNames.Sanitize(_sessionTitle) + ".txt", "Plain text (*.txt)|*.txt"),
```

`:79-90` — add the switch case (before `default:`):
```csharp
                case ExportFormat.Text:
                    await _maintenance.ExportTextAsync(_sessionId, dest, options, CancellationToken.None);
                    break;
```

`ExportFormatToBool.cs` — add after line 13:
```csharp
    public static readonly ExportFormatToBool Text = new() { _target = ExportFormat.Text };
```

`ExportDialog.xaml` — insert after line 17:
```xml
        <RadioButton Content="Plain text (.txt transcript)" GroupName="Fmt"
                     IsChecked="{Binding Format, Converter={x:Static vm:ExportFormatToBool.Text}}" Margin="0,2" />
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportDialogViewModelTests"`
Expected: PASS (all, including the 8 pre-existing).

- [ ] **Step 6: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/Services/MaintenanceService.cs src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/ExportFormatToBool.cs src/LocalScribe.App/ExportDialog.xaml tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs
git commit -m "feat(export): .txt as a fourth export format"
```

---

## Task 4: `ExportFormat` moves to Core + `ExportSetting`

**Files:**
- Create: `src/LocalScribe.Core/Model/ExportFormat.cs`
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs:7` (delete enum), add `using LocalScribe.Core.Model;`
- Modify: `src/LocalScribe.App/ExportFormatToBool.cs` (add `using LocalScribe.Core.Model;`)
- Modify: `src/LocalScribe.Core/Model/Settings.cs`
- Modify: `src/LocalScribe.Core/Storage/LocalScribeJson.cs:36`
- Test: `tests/LocalScribe.Core.Tests/SettingsTests.cs`

**Interfaces:**
- Produces: `LocalScribe.Core.Model.ExportFormat { Zip, Docx, Markdown, Text }`; `LocalScribe.Core.Model.ExportSetting`; `Settings.Export`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/SettingsTests.cs`:

```csharp
    [Fact]
    public void Export_section_round_trips_through_settings_json()
    {
        var settings = new Settings
        {
            Export = new ExportSetting
            {
                Format = ExportFormat.Docx, IncludeTimestamps = false, IncludeMarkers = false,
                ExtraTimestamps = true, CadenceIntervalMs = 30000,
                FilenameTemplate = "{date} {title}", IncludeSummary = true,
            },
        };

        string json = JsonSerializer.Serialize(settings, LocalScribeJson.Options);
        var back = JsonSerializer.Deserialize<Settings>(json, LocalScribeJson.Options)!;

        Assert.Contains("\"format\": \"Docx\"", json);          // enum-as-string, house pattern
        Assert.Equal(ExportFormat.Docx, back.Export.Format);
        Assert.False(back.Export.IncludeTimestamps);
        Assert.False(back.Export.IncludeMarkers);
        Assert.True(back.Export.ExtraTimestamps);
        Assert.Equal(30000, back.Export.CadenceIntervalMs);
        Assert.Equal("{date} {title}", back.Export.FilenameTemplate);
        Assert.True(back.Export.IncludeSummary);
    }

    [Fact]
    public void A_v3_file_without_the_export_section_loads_at_the_documented_defaults()
    {
        // Field-absence semantics, the SectionGapMs precedent: no schema bump, no migration.
        const string json = """{"schemaVersion":3,"storageRoot":"%USERPROFILE%/LocalScribe"}""";
        var back = JsonSerializer.Deserialize<Settings>(json, LocalScribeJson.Options)!;

        Assert.Equal(ExportFormat.Zip, back.Export.Format);
        Assert.True(back.Export.IncludeTimestamps);
        Assert.True(back.Export.IncludeMarkers);
        Assert.False(back.Export.ExtraTimestamps);
        Assert.Equal(15000, back.Export.CadenceIntervalMs);
        Assert.Equal("{title}", back.Export.FilenameTemplate);
        Assert.False(back.Export.IncludeSummary);
    }
```

If `SettingsTests.cs` lacks them, add `using System.Text.Json;`, `using LocalScribe.Core.Model;`, `using LocalScribe.Core.Storage;` at the top.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~SettingsTests.Export_section|FullyQualifiedName~SettingsTests.A_v3_file_without"`
Expected: FAIL — `ExportSetting` does not exist.

- [ ] **Step 3: Move the enum to Core**

Create `src/LocalScribe.Core/Model/ExportFormat.cs`:

```csharp
namespace LocalScribe.Core.Model;

/// <summary>What the session export dialog produces. Lives in Core (not the App view-model layer
/// it started in) because it became PERSISTED domain state in design 2026-08-04 section 4, and
/// Core cannot reference App. Persists as a string via JsonStringEnumConverter, the house pattern
/// AudioFormat/Backend/MicMode already follow.</summary>
public enum ExportFormat { Zip, Docx, Markdown, Text }
```

Delete line 7 of `ExportDialogViewModel.cs` and add `using LocalScribe.Core.Model;` to its using block. Add the same using to `ExportFormatToBool.cs`.

Then find every other reference:

```bash
cd F:/LocalScribe && grep -rln "ExportFormat" src tests
```

Add `using LocalScribe.Core.Model;` to each file that now needs it (`ExportDialogViewModelTests.cs` at minimum).

- [ ] **Step 4: Add `ExportSetting` and register the converter**

Append to `src/LocalScribe.Core/Model/Settings.cs` (after `SemanticSearchSetting`, line 85):

```csharp
/// <summary>Remembered export choices + the export knobs (design 2026-08-04 sections 4-7).
/// Additive - existing v3 files without it load at these defaults (the SectionGapMs precedent),
/// so no schema bump/migration is required. Every default reproduces the pre-Round-2 behaviour
/// exactly. The excerpt range is deliberately NOT here: a remembered range would silently emit a
/// partial export of the next, unrelated session (design section 8).</summary>
public sealed record ExportSetting
{
    public ExportFormat Format { get; init; } = ExportFormat.Zip;
    public bool IncludeTimestamps { get; init; } = true;
    public bool IncludeMarkers { get; init; } = true;
    public bool ExtraTimestamps { get; init; }
    /// <summary>Extra-timestamp cadence. The dialog offers 10/15/30/60 s; a hand-typed value in
    /// settings.json is kept as the effective value rather than rewritten (design section 5).</summary>
    public int CadenceIntervalMs { get; init; } = 15000;
    /// <summary>Save-As default-name template. Tokens: {title} {date} {time} {matter} {version}
    /// {id}. Applies to the three TEXTUAL formats; the .zip keeps its session-id name.</summary>
    public string FilenameTemplate { get; init; } = "{title}";
    /// <summary>Attach the latest assistant summary. Default OFF: the export is the document that
    /// leaves the building, so attaching a machine-written draft must be an act (design 7).</summary>
    public bool IncludeSummary { get; init; }
}
```

Add the property to the `Settings` record (after `SemanticSearch`, line 55):

```csharp
    /// <summary>v3 (design 2026-08-04): remembered export choices + export knobs. Additive -
    /// existing v3 files without it load at the defaults (the SectionGapMs precedent).</summary>
    public ExportSetting Export { get; init; } = new();
```

Add to `LocalScribeJson.cs` after line 36:

```csharp
        o.Converters.Add(new JsonStringEnumConverter<ExportFormat>());
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~SettingsTests"`
Expected: PASS — including the pre-existing `Roundtrips_v2_with_spec_wire_values`, which must be unaffected.

- [ ] **Step 6: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Model/ExportFormat.cs src/LocalScribe.Core/Model/Settings.cs src/LocalScribe.Core/Storage/LocalScribeJson.cs src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/ExportFormatToBool.cs tests/LocalScribe.Core.Tests/SettingsTests.cs tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs
git commit -m "feat(export): ExportFormat moves to Core, additive ExportSetting"
```

---

## Task 5: Seed from settings and persist on success

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs:472-477`
- Test: `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`

**Interfaces:**
- Consumes: `ExportSetting` (Task 4); `ISettingsService` (`Current`, `SaveAsync`); `FakeSettingsService` with `SaveCount` (`tests/LocalScribe.App.Tests/AppServiceFakes.cs`).
- Produces: `ExportDialogViewModel(string sessionId, string sessionTitle, MaintenanceService maintenance, ISettingsService settings, Func<SavePathRequest, string?> pickSavePath, Action<string> revealFile, IUiErrorReporter errors, Action<Action> dispatch)` — `settings` is the **4th** parameter. Tasks 6, 7, 10 and 13 extend this VM.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`:

```csharp
    [Fact]
    public async Task Vm_seeds_format_and_toggles_from_settings()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService(new Settings
        {
            Export = new ExportSetting
            {
                Format = ExportFormat.Markdown, IncludeTimestamps = false,
                IncludeMarkers = false, ExtraTimestamps = true,
            },
        });

        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => null, _ => { }, rep, a => a());

        Assert.Equal(ExportFormat.Markdown, vm.Format);
        Assert.False(vm.IncludeTimestamps);
        Assert.False(vm.IncludeMarkers);
        Assert.True(vm.ExtraTimestamps);
    }

    [Fact]
    public async Task A_successful_export_persists_the_choices()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService();
        var vm = new ExportDialogViewModel("s1", "T", svc, settings,
            _ => Path.Combine(_root, "out.md"), _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, IncludeMarkers = false };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(1, settings.SaveCount);
        Assert.Equal(ExportFormat.Markdown, settings.Current.Export.Format);
        Assert.False(settings.Current.Export.IncludeMarkers);
    }

    [Fact]
    public async Task A_cancelled_save_as_persists_nothing()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService();
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => null, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task A_failed_export_persists_nothing()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService();
        // A directory path as the destination makes the FileStream open throw.
        string bad = Path.Combine(_root, "a-directory");
        Directory.CreateDirectory(bad);
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => bad, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.NotEmpty(rep.Errors);
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task A_settings_save_failure_is_reported_but_the_export_still_succeeds()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new ThrowingSettingsService();
        string dest = Path.Combine(_root, "out.md");
        string? revealed = null;
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => dest,
            p => revealed = p, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(dest));                 // the export itself landed
        Assert.Equal(dest, revealed);                   // reveal not suppressed
        Assert.Single(rep.Infos);                       // success Info not suppressed
        Assert.Contains(rep.Errors, e => e.StartsWith("Saving export choices", StringComparison.Ordinal));
    }

    private sealed class ThrowingSettingsService : ISettingsService
    {
        public Settings Current { get; } = new();
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        {
            Changed?.Invoke(Current, updated);          // keeps the compiler quiet about the event
            throw new IOException("settings.json is locked");
        }
    }
```

Every pre-existing test in this file must be updated to pass `new FakeSettingsService()` as the 4th constructor argument.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportDialogViewModelTests"`
Expected: FAIL — no 8-parameter constructor.

- [ ] **Step 3: Add the seam and seed the VM**

In `ExportDialogViewModel.cs`, add the field and constructor parameter:

```csharp
    private readonly ISettingsService _settings;

    public ExportDialogViewModel(string sessionId, string sessionTitle, MaintenanceService maintenance,
        ISettingsService settings, Func<SavePathRequest, string?> pickSavePath, Action<string> revealFile,
        IUiErrorReporter errors, Action<Action> dispatch)
    {
        (_sessionId, _sessionTitle, _maintenance, _settings, _pickSavePath, _revealFile, _errors, _dispatch)
            = (sessionId, sessionTitle, maintenance, settings, pickSavePath, revealFile, errors, dispatch);
        // Seed the BACKING FIELDS, not the properties: the generated setters raise
        // PropertyChanged and OnFormatChanged before ExportCommand below exists.
        var e = settings.Current.Export;
        (_format, _includeTimestamps, _includeMarkers, _extraTimestamps)
            = (e.Format, e.IncludeTimestamps, e.IncludeMarkers, e.ExtraTimestamps);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy);
    }
```

Delete the `CadenceIntervalMs` const at `:32` and replace the options build (`:74-78`) to read the setting:

```csharp
            var options = new ExportOptions
            {
                IncludeTimestamps = IncludeTimestamps, IncludeMarkers = IncludeMarkers,
                TimestampIntervalMs = IncludeTimestamps && ExtraTimestamps
                    ? _settings.Current.Export.CadenceIntervalMs : 0,
            };
```

Add the persist helper at the end of the class:

```csharp
    /// <summary>Remember what the user last ACTUALLY did (design 2026-08-04 section 4): called
    /// only after a successful export, never on dialog-open and never on cancel. A save failure is
    /// reported but must never fail an export that already succeeded, so this is awaited AFTER the
    /// success Info and the reveal.</summary>
    private async Task PersistChoicesAsync()
    {
        try
        {
            await _settings.SaveAsync(_settings.Current with
            {
                Export = _settings.Current.Export with
                {
                    Format = Format,
                    IncludeTimestamps = IncludeTimestamps,
                    IncludeMarkers = IncludeMarkers,
                    ExtraTimestamps = ExtraTimestamps,
                },
            }, CancellationToken.None);
        }
        catch (Exception ex) { _errors.Report("Saving export choices", ex); }
    }
```

Call it in `ExportAsync`, replacing the success tail:

```csharp
            _errors.Info("Exported to " + dest);
            _revealFile(dest);
            await PersistChoicesAsync();
            _dispatch(() => Closed?.Invoke());
```

- [ ] **Step 4: Update the composition root**

`App.xaml.cs:474`:

```csharp
            var exportVm = new ViewModels.ExportDialogViewModel(sessionId, title, comp.Maintenance,
                comp.Settings, pickSavePath, revealFile, errors, dispatch);
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportDialogViewModelTests"`
Expected: PASS.

- [ ] **Step 6: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs
git commit -m "feat(export): remember format and toggles across dialog opens"
```

---

## Task 6: Cadence-interval knob

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs`
- Modify: `src/LocalScribe.App/ExportDialog.xaml:24-25`
- Test: `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`

**Interfaces:**
- Produces: `ExportDialogViewModel.CadenceChoices : IReadOnlyList<CadenceChoice>`, `CadenceChoice(int Ms, string Label)`, `CadenceIntervalMs : int`, `SelectedCadenceMs : int`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`:

```csharp
    [Fact]
    public async Task Cadence_offers_four_presets_and_defaults_to_fifteen_seconds()
    {
        var (svc, _, rep) = await MakeAsync();
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => null, _ => { }, rep, a => a());

        Assert.Equal([10000, 15000, 30000, 60000], vm.CadenceChoices.Select(c => c.Ms));
        Assert.Equal(["10 s", "15 s", "30 s", "60 s"], vm.CadenceChoices.Select(c => c.Label));
        Assert.Equal(15000, vm.CadenceIntervalMs);
        Assert.Equal(15000, vm.SelectedCadenceMs);
    }

    [Fact]
    public async Task A_non_preset_settings_value_stays_effective_and_displays_as_the_nearest_preset()
    {
        // settings.json is user-editable: a hand-typed 20000 must not be rewritten to 15000
        // before the user has chosen anything (design 2026-08-04 section 5).
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService(new Settings
        { Export = new ExportSetting { CadenceIntervalMs = 20000 } });
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => null, _ => { }, rep, a => a());

        Assert.Equal(20000, vm.CadenceIntervalMs);       // effective value preserved
        Assert.Equal(15000, vm.SelectedCadenceMs);       // nearest preset for DISPLAY only
    }

    [Fact]
    public async Task Picking_a_preset_replaces_the_effective_value_and_persists_on_export()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        var settings = new FakeSettingsService();
        string dest = Path.Combine(_root, "cad.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExtraTimestamps = true };

        vm.SelectedCadenceMs = 10000;
        Assert.Equal(10000, vm.CadenceIntervalMs);

        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal(10000, settings.Current.Export.CadenceIntervalMs);

        // The 10s cadence splits the seeded turn earlier than the 15s default did.
        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains("(cont'd):", md);
    }
```

Ensure `using System.Linq;` is present in the test file.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportDialogViewModelTests.Cadence|FullyQualifiedName~ExportDialogViewModelTests.A_non_preset|FullyQualifiedName~ExportDialogViewModelTests.Picking_a_preset"`
Expected: FAIL — `CadenceChoices` does not exist.

- [ ] **Step 3: Implement the knob**

In `ExportDialogViewModel.cs`, add above the class:

```csharp
/// <summary>One entry in the export dialog's cadence dropdown (design 2026-08-04 section 5).
/// A preset list rather than free numeric entry: 1 s puts a stamp on every sentence and 3600 s
/// does nothing, and neither is worth a validation story.</summary>
public sealed record CadenceChoice(int Ms, string Label);
```

Inside the class, add:

```csharp
    public IReadOnlyList<CadenceChoice> CadenceChoices { get; } =
        [new(10000, "10 s"), new(15000, "15 s"), new(30000, "30 s"), new(60000, "60 s")];

    [ObservableProperty] private int _cadenceIntervalMs = 15000;

    /// <summary>What the dropdown shows and sets. Reading snaps a non-preset settings.json value
    /// to the nearest preset for DISPLAY only - CadenceIntervalMs keeps the loaded value until the
    /// user actually picks one (design 2026-08-04 section 5). Writing replaces it outright.</summary>
    public int SelectedCadenceMs
    {
        get => CadenceChoices.Any(c => c.Ms == CadenceIntervalMs)
            ? CadenceIntervalMs
            : CadenceChoices.MinBy(c => Math.Abs(c.Ms - CadenceIntervalMs))!.Ms;
        set { CadenceIntervalMs = value; OnPropertyChanged(); }
    }
```

Add `using System.Linq;` if absent.

Seed it in the constructor tuple:

```csharp
        (_format, _includeTimestamps, _includeMarkers, _extraTimestamps, _cadenceIntervalMs)
            = (e.Format, e.IncludeTimestamps, e.IncludeMarkers, e.ExtraTimestamps, e.CadenceIntervalMs);
```

Use it in the options build (replacing the `_settings.Current.Export.CadenceIntervalMs` read from Task 5):

```csharp
                TimestampIntervalMs = IncludeTimestamps && ExtraTimestamps ? CadenceIntervalMs : 0,
```

Add it to `PersistChoicesAsync`'s `Export with`:

```csharp
                    CadenceIntervalMs = CadenceIntervalMs,
```

- [ ] **Step 4: Update the XAML**

Replace `ExportDialog.xaml:24-25` with:

```xml
            <StackPanel Orientation="Horizontal" Margin="16,2,0,2">
                <CheckBox Content="Extra timestamp every" IsChecked="{Binding ExtraTimestamps}"
                          IsEnabled="{Binding IncludeTimestamps}" VerticalAlignment="Center" />
                <ComboBox ItemsSource="{Binding CadenceChoices}" DisplayMemberPath="Label"
                          SelectedValuePath="Ms" SelectedValue="{Binding SelectedCadenceMs}"
                          IsEnabled="{Binding IncludeTimestamps}" Width="80" Margin="8,0,0,0" />
            </StackPanel>
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportDialogViewModelTests"`
Expected: PASS. The pre-existing `Extra_timestamps_add_continuation_paragraphs_to_the_export` must still pass — the 15 s default is unchanged.

- [ ] **Step 6: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/ExportDialog.xaml tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs
git commit -m "feat(export): cadence-interval preset knob replaces the hardcoded 15s"
```

---

## Task 7: Filename template

**Files:**
- Modify: `src/LocalScribe.App/Services/ExportFileNames.cs`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (add `FilenameTokensAsync`)
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs`
- Modify: `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs`, `src/LocalScribe.App/SettingsPage.xaml`
- Test: `tests/LocalScribe.App.Tests/ExportFileNamesTests.cs` (create), `ExportDialogViewModelTests.cs`

**Interfaces:**
- Produces: `ExportFileNames.Expand(string template, IReadOnlyDictionary<string, string> tokens) : string`; `MaintenanceService.FilenameTokensAsync(string sessionId, CancellationToken ct) : Task<IReadOnlyDictionary<string, string>>`; `SettingsPageViewModel.ExportFilenameTemplate : string`.
- The template applies to the **three textual formats only**. Zip keeps `sessionId + ".zip"`, which is what makes the spec's "default `{title}` produces byte-identical filenames to today" true.

- [ ] **Step 1: Write the failing `Expand` tests**

Create `tests/LocalScribe.App.Tests/ExportFileNamesTests.cs`:

```csharp
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Filename-template expansion (design 2026-08-04 section 6). Pure string work; the
/// token VALUES come from MaintenanceService.FilenameTokensAsync.</summary>
public sealed class ExportFileNamesTests
{
    private static readonly Dictionary<string, string> Tokens = new(StringComparer.Ordinal)
    {
        ["title"] = "Doe intake", ["date"] = "2026-07-03", ["time"] = "0900",
        ["matter"] = "Doe v Roe (2026/014)", ["version"] = "v2", ["id"] = "s1",
    };

    private static Dictionary<string, string> Untagged()
    {
        var t = new Dictionary<string, string>(Tokens, StringComparer.Ordinal);
        t["matter"] = "";
        return t;
    }

    [Fact]
    public void Every_token_expands()
    {
        Assert.Equal("Doe intake", ExportFileNames.Expand("{title}", Tokens));
        Assert.Equal("2026-07-03", ExportFileNames.Expand("{date}", Tokens));
        Assert.Equal("0900", ExportFileNames.Expand("{time}", Tokens));
        Assert.Equal("Doe v Roe (2026/014)", ExportFileNames.Expand("{matter}", Tokens));
        Assert.Equal("v2", ExportFileNames.Expand("{version}", Tokens));
        Assert.Equal("s1", ExportFileNames.Expand("{id}", Tokens));
    }

    [Fact]
    public void An_unknown_token_is_left_literal_so_the_user_sees_the_typo()
    {
        Assert.Equal("Doe intake {ttle}", ExportFileNames.Expand("{title} {ttle}", Tokens));
    }

    [Fact]
    public void An_empty_token_swallows_the_separator_run_that_followed_it()
    {
        Assert.Equal("Doe intake", ExportFileNames.Expand("{matter}-{title}", Untagged()));
        Assert.Equal("Doe intake", ExportFileNames.Expand("{matter} - {title}", Untagged()));
        Assert.Equal("Doe intake", ExportFileNames.Expand("{title}-{matter}", Untagged()));
    }

    [Fact]
    public void Intentional_separators_between_non_empty_tokens_survive()
    {
        Assert.Equal("2026-07-03 - Doe intake",
            ExportFileNames.Expand("{date} - {title}", Tokens));
        Assert.Equal("2026-07-03_Doe intake", ExportFileNames.Expand("{date}_{title}", Tokens));
    }

    [Fact]
    public void A_template_expanding_to_nothing_falls_back_through_sanitize()
    {
        Assert.Equal("", ExportFileNames.Expand("{matter}", Untagged()));
        Assert.Equal("export", ExportFileNames.Sanitize(ExportFileNames.Expand("{matter}", Untagged())));
    }

    [Fact]
    public void Sanitize_still_runs_last_over_an_expanded_matter()
    {
        // Legal matter references commonly contain '/', which is exactly why Sanitize exists.
        Assert.Equal("Doe v Roe (2026_014)",
            ExportFileNames.Sanitize(ExportFileNames.Expand("{matter}", Tokens)));
    }

    [Fact]
    public void The_default_template_reproduces_the_pre_round_2_filename()
    {
        Assert.Equal(ExportFileNames.Sanitize("Doe intake"),
            ExportFileNames.Sanitize(ExportFileNames.Expand("{title}", Tokens)));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportFileNamesTests"`
Expected: FAIL — `Expand` does not exist.

- [ ] **Step 3: Implement `Expand`**

Add to `src/LocalScribe.App/Services/ExportFileNames.cs`, inside the class, and add `using System.Text;`:

```csharp
    /// <summary>Expand a Save-As filename template (design 2026-08-04 section 6). Three rules:
    /// an UNKNOWN token is left literal, so the user sees their typo in the Save-As default name
    /// and fixes it (silently dropping it hides the mistake); an EMPTY token swallows the
    /// separator run that followed it, so "{matter}-{title}" on an untagged session is "Title",
    /// not "-Title"; separators between non-empty tokens are untouched. Call Sanitize on the
    /// result - this method deliberately does not, so the two concerns stay testable apart.</summary>
    public static string Expand(string template, IReadOnlyDictionary<string, string> tokens)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                int close = template.IndexOf('}', i + 1);
                if (close > i)
                {
                    string name = template[(i + 1)..close];
                    if (tokens.TryGetValue(name, out string? value))
                    {
                        i = close + 1;
                        if (value.Length == 0)
                        {
                            while (i < template.Length && template[i] is ' ' or '-' or '_') i++;
                            continue;
                        }
                        sb.Append(value);
                        continue;
                    }
                    sb.Append(template, i, close - i + 1);   // unknown token: literal
                    i = close + 1;
                    continue;
                }
            }
            sb.Append(template[i]);
            i++;
        }
        return sb.ToString().Trim(' ', '_', '-');
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportFileNamesTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Add `FilenameTokensAsync` and wire the VM**

Add to `MaintenanceService.cs` after `ProvenanceFor` (`:1051`):

```csharp
    /// <summary>Filename-template tokens for one session (design 2026-08-04 section 6). Loaded
    /// under the session gate because {date}/{matter}/{version} live in the projection, which the
    /// export dialog does not hold - it has only a session id and a title. Called once, before
    /// Save-As. Invariant-culture date/time by construction, like every other exported string.</summary>
    public Task<IReadOnlyDictionary<string, string>> FilenameTokensAsync(string sessionId,
        CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            var loaded = await SessionProjectionLoader.LoadAsync(paths, settings.Current, time, sessionId, ct: inner);
            return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = loaded.Meta.Title,
                ["date"] = loaded.StartedLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["time"] = loaded.StartedLocal.ToString("HHmm", CultureInfo.InvariantCulture),
                ["matter"] = loaded.MatterDisplays.Count > 0 ? loaded.MatterDisplays[0] : "",
                ["version"] = loaded.VersionId,
                ["id"] = sessionId,
            };
        }, ct);
```

In `ExportDialogViewModel.ExportAsync`, replace the `request` switch with a token-aware build. Zip is unchanged:

```csharp
        SavePathRequest request;
        if (Format == ExportFormat.Zip)
        {
            // The .zip is the raw session folder; it keeps its session-id name so the default
            // template reproduces every pre-Round-2 filename byte-for-byte.
            request = new SavePathRequest(_sessionId + ".zip", "Zip archive (*.zip)|*.zip");
        }
        else
        {
            var tokens = await _maintenance.FilenameTokensAsync(_sessionId, CancellationToken.None);
            string stem = ExportFileNames.Sanitize(
                ExportFileNames.Expand(_settings.Current.Export.FilenameTemplate, tokens));
            (string ext, string filter) = Format switch
            {
                ExportFormat.Markdown => (".md", "Markdown (*.md)|*.md"),
                ExportFormat.Text => (".txt", "Plain text (*.txt)|*.txt"),
                _ => (".docx", "Word document (*.docx)|*.docx"),
            };
            request = new SavePathRequest(stem + ext, filter);
        }
```

This whole block must sit **inside** the existing `try` so a projection failure is reported rather than thrown at the command. Move `IsBusy = true;` and the `try {` above it, and keep `if (string.IsNullOrWhiteSpace(dest)) return;` immediately after `_pickSavePath(request)`.

> **Note for the implementer:** the pre-existing tests assert `"Doe_ intake_2026.docx"` from the VM's `_sessionTitle` argument, but `FilenameTokensAsync` returns the **meta** title (`"Doe intake"`). Update those two assertions to `"Doe intake.docx"` / `"Doe intake.md"` / `"Doe intake.txt"` — reading the real title from the projection instead of a caller-supplied string is the intended improvement, and `Markdown_export_...` already asserts the meta title reaches the document body for the same reason.

- [ ] **Step 6: Add the Settings-page field**

In `SettingsPageViewModel.cs`, add near `CompactConsoleOnStart` (`:543`):

```csharp
    /// <summary>Design 2026-08-04 section 6: Save-As default-name template for the three textual
    /// export formats. Set-once preference, so it lives here rather than in the export dialog -
    /// the Save-As default name is already the live preview.</summary>
    public string ExportFilenameTemplate
    {
        get => _settings.Current.Export.FilenameTemplate;
        set
        {
            Commit(s => s with { Export = s.Export with { FilenameTemplate = value } });
            OnPropertyChanged();
        }
    }

    public string ExportTemplateTokens { get; } =
        "Tokens: {title} {date} {time} {matter} {version} {id}. "
        + "An unknown token is left as typed. The .zip keeps its session-id name.";
```

In `SettingsPage.xaml`, insert a new card before the `"App"` card (line 399 region):

```xml
            <ui:Card Style="{StaticResource SectionCard}">
                <StackPanel>
                    <TextBlock Text="Export" FontWeight="SemiBold" Margin="0,0,0,8" />
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Filename template" Style="{StaticResource FieldLabel}" />
                        <TextBox Text="{Binding ExportFilenameTemplate, UpdateSourceTrigger=LostFocus}"
                                 Width="260" />
                    </StackPanel>
                    <TextBlock Text="{Binding ExportTemplateTokens, Mode=OneWay}"
                               Style="{StaticResource Note}" TextWrapping="Wrap" />
                </StackPanel>
            </ui:Card>
```

- [ ] **Step 7: Add the VM integration test**

Append to `ExportDialogViewModelTests.cs`:

```csharp
    [Fact]
    public async Task The_filename_template_drives_the_save_as_default_name()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService(new Settings
        { Export = new ExportSetting { FilenameTemplate = "{date} {title}" } });
        SavePathRequest? seen = null;
        var vm = new ExportDialogViewModel("s1", "ignored", svc, settings,
            req => { seen = req; return null; }, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("2026-07-03 Doe intake.md", seen!.DefaultFileName);
    }

    [Fact]
    public async Task Zip_ignores_the_template_and_keeps_the_session_id_name()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService(new Settings
        { Export = new ExportSetting { FilenameTemplate = "{date} {title}" } });
        SavePathRequest? seen = null;
        var vm = new ExportDialogViewModel("s1", "T", svc, settings,
            req => { seen = req; return null; }, _ => { }, rep, a => a());

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("s1.zip", seen!.DefaultFileName);
    }
```

- [ ] **Step 8: Run the full suite**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx`
Expected: PASS with the known 2 Core fixture failures by name.

- [ ] **Step 9: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/Services/ExportFileNames.cs src/LocalScribe.App/Services/MaintenanceService.cs src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs src/LocalScribe.App/SettingsPage.xaml tests/LocalScribe.App.Tests/ExportFileNamesTests.cs tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs
git commit -m "feat(export): filename template with token expansion"
```

---

## Task 8: `ExportSummary` + the `LatestSummaryProvider` seam

**Files:**
- Create: `src/LocalScribe.Core/Projection/ExportSummary.cs`
- Modify: `src/LocalScribe.Core/Model/SessionMeta.cs:25-27` (doc note only)
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs` (near `:226`)
- Test: `tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs`

**Interfaces:**
- Consumes: `SummaryStore.LoadAsync(sessionId, ct) : Task<IReadOnlyList<SummaryVersion>>`; `SummaryVersion(string Id, DateTimeOffset CreatedAt, string SourceTranscriptVersion, AssistantModelRef Model, int PromptVersion, string ContentMarkdown, bool Stale, bool CudaFellToCpu = false)`; `AssistantModelRef(string File, string Sha256, string Backend)`.
- Produces: `ExportSummary { ContentMarkdown, ProvenanceLine, StaleNotice }`; `MaintenanceService.LatestSummaryProvider { get; set; }`; `MaintenanceService.SummaryFor(SummaryVersion?, string renderedVersionId, TimeSpan sessionOffset) : ExportSummary?`. Task 9 renders it; Task 10 toggles it.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs` (add `using LocalScribe.Core.Assistant;` and `using LocalScribe.Core.Projection;`):

```csharp
    private static SummaryVersion Version(bool stale = false, string sourceVersion = "v1") =>
        new("sum-1", new DateTimeOffset(2026, 8, 1, 14, 22, 0, TimeSpan.Zero), sourceVersion,
            new AssistantModelRef("Qwen3-4B-Instruct-2507.gguf", "abc123", "cuda"),
            2, "## Summary\nThey agreed to file.\n", stale);

    [Fact]
    public void Summary_for_a_current_version_carries_provenance_and_no_stale_notice()
    {
        var s = MaintenanceService.SummaryFor(Version(), "v1", TimeSpan.Zero);

        Assert.NotNull(s);
        Assert.Equal("## Summary\nThey agreed to file.\n", s!.ContentMarkdown);
        Assert.Equal("generated 2026-08-01 14:22, Qwen3-4B-Instruct-2507.gguf (CUDA)", s.ProvenanceLine);
        Assert.Null(s.StaleNotice);
    }

    [Fact]
    public void A_stale_flag_renders_the_out_of_date_notice()
    {
        var s = MaintenanceService.SummaryFor(Version(stale: true), "v1", TimeSpan.Zero);
        Assert.Contains("OUT OF DATE", s!.StaleNotice);
    }

    [Fact]
    public void A_version_mismatch_renders_even_when_the_stale_flag_is_clear()
    {
        // The check the Stale flag alone misses: un-stale against its own version, but the
        // export is rendering a different one (design 2026-08-04 section 7).
        var s = MaintenanceService.SummaryFor(Version(sourceVersion: "v1"), "v2", TimeSpan.Zero);
        Assert.Contains("Generated against transcript v1; this document is v2.", s!.StaleNotice);
    }

    [Fact]
    public void Both_conditions_render_both_notices()
    {
        var s = MaintenanceService.SummaryFor(Version(stale: true, sourceVersion: "v1"), "v2", TimeSpan.Zero);
        Assert.Contains("OUT OF DATE", s!.StaleNotice);
        Assert.Contains("this document is v2.", s.StaleNotice);
    }

    [Fact]
    public void No_version_and_an_empty_version_both_yield_no_summary()
    {
        Assert.Null(MaintenanceService.SummaryFor(null, "v1", TimeSpan.Zero));
        Assert.Null(MaintenanceService.SummaryFor(Version() with { ContentMarkdown = "  " }, "v1", TimeSpan.Zero));
    }

    [Fact]
    public void The_provenance_timestamp_uses_the_sessions_offset_not_the_machine_zone()
    {
        // Round 1 principle: page size is the ONE machine-locale dependence in an export.
        var s = MaintenanceService.SummaryFor(Version(), "v1", TimeSpan.FromHours(10));
        Assert.Contains("generated 2026-08-02 00:22", s!.ProvenanceLine);
    }

    [Fact]
    public void Latest_picks_the_last_version_not_the_first()
    {
        // summaries.json is APPEND-ONLY and newest-LAST - the same versions[^1] pick the
        // summary-status provider and the matter-summary sources already make.
        var first = Version() with { Id = "sum-1" };
        var newest = Version() with { Id = "sum-3" };
        Assert.Equal("sum-3", MaintenanceService.Latest([first, Version() with { Id = "sum-2" }, newest])!.Id);
    }

    [Fact]
    public void Latest_of_an_empty_store_is_null()
        => Assert.Null(MaintenanceService.Latest([]));

```

And append this one to `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`, which has the verified `MakeAsync()` session-seeding helper:

```csharp
    [Fact]
    public async Task A_null_latest_summary_provider_exports_with_no_summary_and_no_crash()
    {
        // Every existing unit-test construction of MaintenanceService gets this for free.
        var (svc, _, _) = await MakeAsync();
        Assert.Null(svc.LatestSummaryProvider);
        string dest = Path.Combine(_root, "nosum.md");

        await svc.ExportMarkdownAsync("s1", dest,
            new ExportOptions { IncludeSummary = true }, default);

        Assert.DoesNotContain(ExportNotices.SummaryHeading, await File.ReadAllTextAsync(dest));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~MaintenanceServiceTests.Summary_for|FullyQualifiedName~MaintenanceServiceTests.A_stale_flag|FullyQualifiedName~MaintenanceServiceTests.A_version_mismatch|FullyQualifiedName~MaintenanceServiceTests.Both_conditions|FullyQualifiedName~MaintenanceServiceTests.No_version_and|FullyQualifiedName~MaintenanceServiceTests.The_provenance_timestamp"`
Expected: FAIL — `SummaryFor` does not exist.

- [ ] **Step 3: Create `ExportSummary`**

Create `src/LocalScribe.Core/Projection/ExportSummary.cs`:

```csharp
namespace LocalScribe.Core.Projection;

/// <summary>The assistant summary block handed to an export renderer (design 2026-08-04
/// section 7). Deliberately NOT folded into ExportProvenance: a summary is CONTENT, not a fact
/// about where the transcript came from. Composed in MaintenanceService - where ProvenanceFor
/// composes, for the same reason: only the service holds both the loaded projection and the
/// export-time inputs, so the three renderers cannot disagree about staleness. The renderers
/// prepend AssistantPrompts.DraftLabel above this content; that label is locked and is never
/// carried in this record.</summary>
public sealed record ExportSummary
{
    public string ContentMarkdown { get; init; } = "";
    /// <summary>e.g. "generated 2026-08-01 14:22, Qwen3-4B-Instruct-2507.gguf (CUDA)".</summary>
    public string ProvenanceLine { get; init; } = "";
    /// <summary>Null when the summary is current for the rendered transcript version. Otherwise
    /// the out-of-date and/or version-mismatch notices, which renderers show in bold.</summary>
    public string? StaleNotice { get; init; }
}
```

- [ ] **Step 4: Add the seam and the composer**

Add to `MaintenanceService.cs` after `ProvenanceFor` (`:1051`), with `using LocalScribe.Core.Assistant;` at the top:

```csharp
    /// <summary>Latest-summary seam (design 2026-08-04 section 7). A settable property, not a
    /// constructor parameter: this is a primary-constructor class whose four parameters are
    /// repeated in every test construction, and a fifth would break all of them (the
    /// StartupScanTask precedent above). Bound by the composition root to the SINGLE composed
    /// SummaryStore - never a second store (house rule). Null = no summary, which is what every
    /// unit test gets for free.</summary>
    public Func<string, CancellationToken, Task<SummaryVersion?>>? LatestSummaryProvider { get; set; }

    /// <summary>The newest summary version, or null. summaries.json is APPEND-ONLY and
    /// newest-LAST, so this is versions[^1] - the same pick App.xaml.cs already makes for the
    /// summary-status provider and the matter-summary sources. A named helper rather than an
    /// inline expression in the composition root so the choice is testable.</summary>
    public static SummaryVersion? Latest(IReadOnlyList<SummaryVersion> versions)
        => versions.Count > 0 ? versions[^1] : null;

    /// <summary>Compose the export summary block (design 2026-08-04 section 7). Staleness is
    /// EXPORTED and LABELLED - never silently dropped, never silently passed off as current.
    /// Two independent conditions, because the Stale flag alone misses the case where a summary
    /// is current against its own transcript version while the export renders a different one.
    /// sessionOffset (not ToLocalTime) keeps the rendered timestamp deterministic: Round 1 pinned
    /// page size as the ONE machine-locale dependence in an export. Public static so tests drive
    /// the mapping directly - the ProvenanceFor precedent (no InternalsVisibleTo in this repo).</summary>
    public static ExportSummary? SummaryFor(SummaryVersion? version, string renderedVersionId,
        TimeSpan sessionOffset)
    {
        if (version is null || string.IsNullOrWhiteSpace(version.ContentMarkdown)) return null;
        var notices = new List<string>();
        if (version.Stale)
            notices.Add("OUT OF DATE: the transcript changed after this summary was generated.");
        if (!string.Equals(version.SourceTranscriptVersion, renderedVersionId, StringComparison.Ordinal))
            notices.Add(string.Create(CultureInfo.InvariantCulture,
                $"Generated against transcript {version.SourceTranscriptVersion}; this document is {renderedVersionId}."));
        return new ExportSummary
        {
            ContentMarkdown = version.ContentMarkdown,
            ProvenanceLine = string.Create(CultureInfo.InvariantCulture,
                $"generated {version.CreatedAt.ToOffset(sessionOffset):yyyy-MM-dd HH:mm}, "
                + $"{version.Model.File} ({version.Model.Backend.ToUpperInvariant()})"),
            StaleNotice = notices.Count == 0 ? null : string.Join(" ", notices),
        };
    }

    /// <summary>Resolve the summary for one export: honours options.IncludeSummary (opt-in,
    /// default OFF) and a null LatestSummaryProvider. Called inside the session gate by the three
    /// textual export methods.</summary>
    private async Task<ExportSummary?> LoadSummaryAsync(string sessionId, ExportOptions options,
        LoadedProjection loaded, CancellationToken ct)
    {
        if (!options.IncludeSummary || LatestSummaryProvider is null) return null;
        var version = await LatestSummaryProvider(sessionId, ct);
        return SummaryFor(version, loaded.VersionId, loaded.StartedLocal.Offset);
    }
```

- [ ] **Step 5: Add `IncludeSummary` to `ExportOptions` and bind the provider**

In `src/LocalScribe.Core/Projection/ExportOptions.cs`, add:

```csharp
    /// <summary>Attach the latest assistant summary (design 2026-08-04 section 7). Default OFF:
    /// the export is the document that leaves the building, so attaching a machine-written draft
    /// must be an act, not a default.</summary>
    public bool IncludeSummary { get; init; }
```

In `App.xaml.cs`, immediately after line 226 (`comp.Maintenance.SessionContentChanged += markSummariesStale;`):

```csharp
        // Export summary source (design 2026-08-04 section 7): the LATEST version from the SINGLE
        // composed SummaryStore - the same versions[^1] pick the summary-status provider and the
        // matter-summary sources already make. Never a second store (house rule).
        comp.Maintenance.LatestSummaryProvider = async (id, ct) =>
            MaintenanceService.Latest(await comp.Summaries.LoadAsync(id, ct));
```

- [ ] **Step 6: Add the dead-field doc note**

In `src/LocalScribe.Core/Model/SessionMeta.cs`, replace lines 25-27 with:

```csharp
    /// <summary>DEAD FIELDS (design 2026-08-04, "Correction of record"). Written by nobody -
    /// the only other reference is SessionMigrator.cs:74 setting SummaryRef = null. The real
    /// summary lives in assistant\summaries.json behind SummaryStore, which is versioned,
    /// append-only and carries Stale + SourceTranscriptVersion + the model ref. Kept in place
    /// because removing them changes meta.json's written shape for no benefit; do NOT wire an
    /// export or any other consumer to them.</summary>
    public string? SummaryRef { get; init; }
    public DateTimeOffset? SummaryGeneratedAtUtc { get; init; }
    public string? SummaryModel { get; init; }
```

- [ ] **Step 7: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~MaintenanceServiceTests"`
Expected: PASS.

- [ ] **Step 8: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/ExportSummary.cs src/LocalScribe.Core/Projection/ExportOptions.cs src/LocalScribe.Core/Model/SessionMeta.cs src/LocalScribe.App/Services/MaintenanceService.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs
git commit -m "feat(export): ExportSummary composition + latest-summary seam"
```

---

## Task 9: Render the summary in all three formats

**Files:**
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs`, `MarkdownRenderer.cs`, `PlainTextRenderer.cs`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (3 render call sites)
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`, `MarkdownRendererWriteTests.cs`, `PlainTextRendererWriteTests.cs`

**Interfaces:**
- Consumes: `ExportSummary` (Task 8), `ExportNotices.SummaryHeading` (Task 1), `AssistantPrompts.DraftLabel` (`LocalScribe.Core.Assistant`).
- Produces: an `ExportSummary? summary` parameter added **after `provenance`** on all three `Write` signatures.

**Placement (all three formats):** metadata lines → in-progress notice → **summary section** → disclaimer → transcript. The disclaimer's bottom rule then cleanly separates all front matter from the numbered transcript.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs`:

```csharp
    private static ExportSummary Summary(string? stale = null) => new()
    {
        ContentMarkdown = "## Summary\nThey agreed to file.\n\n## Key topics\n- costs\n",
        ProvenanceLine = "generated 2026-08-01 14:22, Qwen3-4B-Instruct-2507.gguf (CUDA)",
        StaleNotice = stale,
    };

    [Fact]
    public void Summary_renders_under_the_heading_with_the_locked_draft_label()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), Summary(),
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains(ExportNotices.SummaryHeading, txt);
        Assert.Contains(LocalScribe.Core.Assistant.AssistantPrompts.DraftLabel, txt);
        Assert.Contains("generated 2026-08-01 14:22", txt);
        Assert.Contains("They agreed to file.", txt);
    }

    [Fact]
    public void A_null_summary_renders_no_summary_section()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.DoesNotContain(ExportNotices.SummaryHeading, txt);
        Assert.DoesNotContain(LocalScribe.Core.Assistant.AssistantPrompts.DraftLabel, txt);
    }

    [Fact]
    public void The_stale_notice_renders_when_present()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(),
            Summary("OUT OF DATE: the transcript changed after this summary was generated."),
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains("OUT OF DATE", txt);
    }
```

Add the mirrored trio to `MarkdownRendererWriteTests.cs` (asserting `"## " + ExportNotices.SummaryHeading`) and to `DocxRendererTests.cs`. The docx file additionally needs:

```csharp
    [Fact]
    public void Every_summary_paragraph_suppresses_line_numbers()
    {
        // Round 1's line numbering counts TRANSCRIPT CONTENT ONLY. Miss this and inserting a
        // summary silently renumbers the whole transcript, invalidating every page:line citation
        // into a document that looks unchanged (design 2026-08-04 section 7).
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(), new ExportProvenance(), Summary(),
            [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart!.Document.Body!;
        var paragraphs = body.Elements<Paragraph>().ToList();

        int headingIndex = paragraphs.FindIndex(p => p.InnerText.Contains(ExportNotices.SummaryHeading));
        int disclaimerIndex = paragraphs.FindIndex(p => p.InnerText.Contains(ExportNotices.Disclaimer));
        Assert.True(headingIndex >= 0);
        Assert.True(headingIndex < disclaimerIndex);   // summary sits ABOVE the closing rule

        for (int i = headingIndex; i < disclaimerIndex; i++)
            Assert.NotNull(paragraphs[i].ParagraphProperties?.SuppressLineNumbers);
    }

    [Fact]
    public void A_summary_does_not_change_the_transcripts_own_line_numbering()
    {
        static int NumberedParagraphs(ExportSummary? summary)
        {
            using var ms = new MemoryStream();
            DocxRenderer.Write(ms, Header(), Meta(), new ExportProvenance(), summary,
                [Turn(0, 4000, "Sam", "hello"), Turn(5000, 9000, "Bob", "hi")], "relative",
                DocxPageSize.A4, new ExportOptions());
            ms.Position = 0;
            using var doc = WordprocessingDocument.Open(ms, false);
            return doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>()
                .Count(p => p.ParagraphProperties?.SuppressLineNumbers is null);
        }

        Assert.Equal(NumberedParagraphs(null), NumberedParagraphs(Summary()));
    }

    [Fact]
    public void Markdown_content_gets_a_line_level_transform_and_no_inline_parsing()
    {
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(), new ExportProvenance(),
            Summary() with { ContentMarkdown = "## Key topics\n- costs\n**bold** stays literal\n" },
            [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        string text = doc.MainDocumentPart!.Document.Body!.InnerText;

        Assert.Contains("Key topics", text);
        Assert.DoesNotContain("## Key topics", text);      // heading marker consumed
        Assert.Contains("\u2022 costs", text);             // bullet rendered
        Assert.DoesNotContain("- costs", text);
        Assert.Contains("**bold** stays literal", text);   // NO inline parsing, documented limit
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~RendererTests|FullyQualifiedName~RendererWriteTests"`
Expected: FAIL — `Write` has no summary parameter.

- [ ] **Step 3: `PlainTextRenderer`**

Add the parameter after `provenance` and insert the section before the disclaimer:

```csharp
    public static string Write(TranscriptHeader header, SessionTextView meta,
        ExportProvenance provenance, ExportSummary? summary, IReadOnlyList<DisplayRow> rows,
        string timestampsMode, ExportOptions options)
```

```csharp
        if (provenance.InProgress)
            sb.Append(Nl).Append(ExportNotices.InProgressNotice).Append(Nl);
        if (summary is not null)
        {
            sb.Append(Nl).Append(ExportNotices.SummaryHeading).Append(Nl);
            sb.Append(AssistantPrompts.DraftLabel).Append(Nl);
            sb.Append(summary.ProvenanceLine).Append(Nl);
            if (summary.StaleNotice is { } staleNotice) sb.Append(staleNotice).Append(Nl);
            sb.Append(Nl).Append(summary.ContentMarkdown.Replace("\n", Nl).TrimEnd()).Append(Nl);
        }
        sb.Append(Nl).Append(ExportNotices.Disclaimer).Append(Nl);
```

Add `using LocalScribe.Core.Assistant;` at the top. Note `.Replace("\n", Nl)` normalises the stored markdown's LF to the file's CRLF; run it on content that has already had any CRLF collapsed:

```csharp
            string content = summary.ContentMarkdown.Replace("\r\n", "\n").Replace("\n", Nl).TrimEnd();
```

Use that `content` local in the append above.

- [ ] **Step 4: `MarkdownRenderer`**

Same parameter position. Insert before the disclaimer line (`:68`):

```csharp
        if (summary is not null)
        {
            sb.Append('\n').Append("## ").Append(ExportNotices.SummaryHeading).Append('\n');
            sb.Append('_').Append(AssistantPrompts.DraftLabel).Append("_\n");
            sb.Append('_').Append(summary.ProvenanceLine).Append("_\n");
            if (summary.StaleNotice is { } staleNotice)
                sb.Append("**").Append(staleNotice).Append("**\n");
            sb.Append('\n').Append(summary.ContentMarkdown.TrimEnd('\n')).Append('\n');
        }
```

- [ ] **Step 5: `DocxRenderer`**

Same parameter position on `Write`. Insert after the `InProgressLine()` call (`:93`), before `DisclaimerLine()`:

```csharp
        if (summary is not null) AppendSummary(body, summary);
```

Add the helpers near `InProgressLine` (`:368`):

```csharp
    /// <summary>The summary section (design 2026-08-04 section 7): heading, the LOCKED
    /// AssistantPrompts.DraftLabel, provenance, an optional stale notice, then the content.
    /// EVERY paragraph suppresses line numbers - Round 1's numbering counts transcript content
    /// only, and a numbered summary would silently renumber the whole transcript.</summary>
    private static void AppendSummary(Body body, ExportSummary summary)
    {
        body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Bold(), new FontSize { Val = "24" }),
                MakeText(ExportNotices.SummaryHeading))));
        body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Italic()), MakeText(AssistantPrompts.DraftLabel))));
        body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Italic()), MakeText(summary.ProvenanceLine))));
        if (summary.StaleNotice is { } staleNotice)
            body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
                new Run(new RunProperties(new Bold()), MakeText(staleNotice))));
        foreach (var p in SummaryContentParagraphs(summary.ContentMarkdown))
            body.AppendChild(p);
    }

    /// <summary>A deliberately MINIMAL line-level markdown transform. AssistantPrompts prescribes
    /// exactly four "##" headers with bullet bodies, so line-level covers the real output shape.
    /// There is NO inline parsing: "**bold**" stays literal. A half-working inline parser is worse
    /// than none, and this limit is documented rather than left as a mystery.</summary>
    private static IEnumerable<Paragraph> SummaryContentParagraphs(string markdown)
    {
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                yield return new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
                    new Run(new RunProperties(new Bold()), MakeText(line.TrimStart('#').TrimStart())));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal)
                     || line.StartsWith("* ", StringComparison.Ordinal))
            {
                // CT_PPrBase schema order: suppressLineNumbers(8) precedes ind(23). The SDK
                // accepts any order and tests pass; Word calls the file corrupt. Use the XSD,
                // NOT Microsoft Learn's alphabetical pPr page.
                yield return new Paragraph(
                    new ParagraphProperties(
                        new SuppressLineNumbers(),
                        new Indentation { Left = "360", Hanging = "360" }),
                    new Run(MakeText("\u2022 " + line[2..])));
            }
            else
            {
                yield return new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
                    new Run(MakeText(line)));
            }
        }
    }
```

Add `using LocalScribe.Core.Assistant;` at the top of `DocxRenderer.cs`.

- [ ] **Step 6: Thread it through the three export methods**

In `MaintenanceService.cs`, in each of `ExportDocxAsync`, `ExportMarkdownAsync` and `ExportTextAsync`, after the `LoadAsync` line add:

```csharp
            var summary = await LoadSummaryAsync(sessionId, options, loaded, inner);
```

and pass `summary` as the 4th render argument (after the `ProvenanceFor(loaded)` argument).

- [ ] **Step 7: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx`
Expected: PASS with the known 2 Core fixture failures by name.

- [ ] **Step 8: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file. **The `\u2022` bullet and any em dash MUST remain escapes** — this is the exact trap that hit seven Round 1 tasks.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/DocxRenderer.cs src/LocalScribe.Core/Projection/MarkdownRenderer.cs src/LocalScribe.Core/Projection/PlainTextRenderer.cs src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.Core.Tests/DocxRendererTests.cs tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs
git commit -m "feat(export): assistant-summary section in all three textual formats"
```

---

## Task 10: Summary opt-in toggle

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs`
- Modify: `src/LocalScribe.App/ExportDialog.xaml`
- Test: `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task Include_summary_is_off_by_default_and_persists_when_ticked()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService();
        string dest = Path.Combine(_root, "sum.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        Assert.False(vm.IncludeSummary);
        vm.IncludeSummary = true;
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(settings.Current.Export.IncludeSummary);
    }

    [Fact]
    public async Task Include_summary_off_produces_no_summary_section_even_when_one_exists()
    {
        var (svc, _, rep) = await MakeAsync();
        svc.LatestSummaryProvider = (_, _) => Task.FromResult<SummaryVersion?>(
            new SummaryVersion("sum-1", DateTimeOffset.UnixEpoch, "v1",
                new AssistantModelRef("m.gguf", "sha", "cpu"), 2, "## Summary\nx\n", false));
        string dest = Path.Combine(_root, "nosum.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.DoesNotContain(ExportNotices.SummaryHeading, await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task Include_summary_on_reaches_the_document()
    {
        var (svc, _, rep) = await MakeAsync();
        svc.LatestSummaryProvider = (_, _) => Task.FromResult<SummaryVersion?>(
            new SummaryVersion("sum-1", DateTimeOffset.UnixEpoch, "v1",
                new AssistantModelRef("m.gguf", "sha", "cpu"), 2, "## Summary\nThey agreed.\n", false));
        string dest = Path.Combine(_root, "withsum.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, IncludeSummary = true };

        await vm.ExportCommand.ExecuteAsync(null);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains(ExportNotices.SummaryHeading, md);
        Assert.Contains(AssistantPrompts.DraftLabel, md);
        Assert.Contains("They agreed.", md);
    }

    [Fact]
    public async Task A_summary_export_leaves_session_txt_byte_identical()
    {
        // The summary is EXPORT-ONLY: SessionTextView.Summary stays null, so session.txt does not
        // vary with assistant state and never needs regenerating when a summary is generated
        // (design 2026-08-04 section 7).
        var (svc, paths, rep) = await MakeAsync();
        // MaintenanceService has no per-session regenerate; RegenerateAllAsync covers the one
        // session this fixture seeds. StoragePaths.SessionTxt(id) is session.txt.
        await svc.RegenerateAllAsync(null, CancellationToken.None);
        byte[] before = await File.ReadAllBytesAsync(paths.SessionTxt("s1"));

        svc.LatestSummaryProvider = (_, _) => Task.FromResult<SummaryVersion?>(
            new SummaryVersion("sum-1", DateTimeOffset.UnixEpoch, "v1",
                new AssistantModelRef("m.gguf", "sha", "cpu"), 2, "## Summary\nThey agreed.\n", false));
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => Path.Combine(_root, "s.md"), _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, IncludeSummary = true };
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(before, await File.ReadAllBytesAsync(paths.SessionTxt("s1")));
    }
```

(Verified API: `MaintenanceService.RegenerateAllAsync(IProgress<int>?, CancellationToken)` at `MaintenanceService.cs:942` — there is no per-session overload; `StoragePaths.SessionTxt(string id)` at `StoragePaths.cs:23`. The per-session regenerate lives on `SessionWriter.RegenerateProjectionsAsync`, which view models do not call directly.)

Add `using LocalScribe.Core.Assistant;` and `using LocalScribe.Core.Projection;` to the test file.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportDialogViewModelTests.Include_summary"`
Expected: FAIL — `IncludeSummary` does not exist on the VM.

- [ ] **Step 3: Implement**

In `ExportDialogViewModel.cs` add the property beside the other toggles:

```csharp
    [ObservableProperty] private bool _includeSummary;
```

Seed it in the constructor tuple (extend the existing assignment):

```csharp
        (_format, _includeTimestamps, _includeMarkers, _extraTimestamps, _cadenceIntervalMs, _includeSummary)
            = (e.Format, e.IncludeTimestamps, e.IncludeMarkers, e.ExtraTimestamps,
               e.CadenceIntervalMs, e.IncludeSummary);
```

Add it to the options build:

```csharp
                IncludeSummary = IncludeSummary,
```

Add it to `PersistChoicesAsync`'s `Export with`:

```csharp
                    IncludeSummary = IncludeSummary,
```

In `ExportDialog.xaml`, add inside the toggles `StackPanel` after the markers checkbox:

```xml
            <CheckBox Content="Include assistant summary" IsChecked="{Binding IncludeSummary}" Margin="0,2" />
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportDialogViewModelTests"`
Expected: PASS.

- [ ] **Step 5: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/ExportDialog.xaml tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs
git commit -m "feat(export): opt-in assistant-summary toggle, default off"
```

---

## Task 11: Excerpt selection and `ResolveExcerptAsync`

**Files:**
- Create: `src/LocalScribe.Core/Projection/ExcerptRange.cs`, `src/LocalScribe.Core/Projection/ExcerptSelector.cs`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs`
- Test: `tests/LocalScribe.Core.Tests/ExcerptSelectorTests.cs` (create), `tests/LocalScribe.App.Tests/ResolveExcerptTests.cs` (create)

**Interfaces:**
- Produces: `ExcerptRange(long FromMs, long ToMs)`; `ExcerptSelector.Covers(DisplayRow, ExcerptRange) : bool`, `.Select(IReadOnlyList<DisplayRow>, ExcerptRange) : IReadOnlyList<DisplayRow>`, `.ActualSpan(IReadOnlyList<DisplayRow>) : (long FromMs, long ToMs)`; `MaintenanceService.ResolveExcerptAsync(string sessionId, string fromText, string toText, CancellationToken ct) : Task<ExcerptRange>`.

- [ ] **Step 1: Write the failing selector tests**

Create `tests/LocalScribe.Core.Tests/ExcerptSelectorTests.cs`:

```csharp
using LocalScribe.Core.Projection;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>Whole-row overlap selection (design 2026-08-04 section 8). Rows are never truncated,
/// so the exported span snaps OUTWARD to turn boundaries and the document must report the ACTUAL
/// span, not the requested one.</summary>
public sealed class ExcerptSelectorTests
{
    private static DisplayRow Turn(long startMs, long endMs, string text = "x") =>
        new() { StartMs = startMs, EndMs = endMs, DisplayName = "Sam", Text = text };

    [Fact]
    public void A_row_straddling_the_from_boundary_is_included_whole_and_verbatim()
    {
        var rows = new[] { Turn(0, 5000, "straddles the start") };
        var kept = ExcerptSelector.Select(rows, new ExcerptRange(3000, 9000));

        Assert.Single(kept);
        Assert.Equal("straddles the start", kept[0].Text);   // never truncated
        Assert.Equal(0, kept[0].StartMs);                    // original anchors preserved
    }

    [Fact]
    public void Rows_entirely_outside_the_range_are_excluded()
    {
        var rows = new[] { Turn(0, 1000), Turn(4000, 6000), Turn(20000, 22000) };
        var kept = ExcerptSelector.Select(rows, new ExcerptRange(3000, 9000));

        Assert.Single(kept);
        Assert.Equal(4000, kept[0].StartMs);
    }

    [Fact]
    public void A_row_touching_the_boundary_with_zero_overlap_is_excluded()
    {
        var rows = new[] { Turn(0, 3000) };
        Assert.Empty(ExcerptSelector.Select(rows, new ExcerptRange(3000, 9000)));
    }

    [Fact]
    public void A_zero_length_marker_row_inside_the_range_is_included()
    {
        var marker = new DisplayRow { IsMarker = true, StartMs = 4000, EndMs = 4000, Text = "Paused" };
        var kept = ExcerptSelector.Select([marker], new ExcerptRange(3000, 9000));
        Assert.Single(kept);
    }

    [Fact]
    public void A_zero_length_marker_row_outside_the_range_is_excluded()
    {
        var marker = new DisplayRow { IsMarker = true, StartMs = 12000, EndMs = 12000, Text = "Paused" };
        Assert.Empty(ExcerptSelector.Select([marker], new ExcerptRange(3000, 9000)));
    }

    [Fact]
    public void Actual_span_is_the_outward_snapped_boundary_of_the_selected_rows()
    {
        var rows = new[] { Turn(0, 5000), Turn(5500, 12000) };
        var kept = ExcerptSelector.Select(rows, new ExcerptRange(3000, 9000));

        Assert.Equal((0L, 12000L), ExcerptSelector.ActualSpan(kept));   // NOT (3000, 9000)
    }

    [Fact]
    public void Actual_span_of_nothing_is_zero()
        => Assert.Equal((0L, 0L), ExcerptSelector.ActualSpan([]));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExcerptSelectorTests"`
Expected: FAIL — `ExcerptSelector` does not exist.

- [ ] **Step 3: Implement the selector**

Create `src/LocalScribe.Core/Projection/ExcerptRange.cs`:

```csharp
namespace LocalScribe.Core.Projection;

/// <summary>The millisecond window an excerpt export selects rows with (design 2026-08-04
/// section 8). Deliberately named apart from ExportProvenance.ExcerptSpan: this is the INPUT
/// window the service filters with, that is the printed label the renderers show.</summary>
public sealed record ExcerptRange(long FromMs, long ToMs);
```

Create `src/LocalScribe.Core/Projection/ExcerptSelector.cs`. **`System.Linq` is NOT an implicit using in this project** — `MetadataFormat.cs:2` imports it explicitly, and `Where`/`Min`/`Max` below need it:

```csharp
using System.Linq;
namespace LocalScribe.Core.Projection;

/// <summary>Whole-row overlap selection for a time-range excerpt (design 2026-08-04 section 8).
/// A row is IN when it overlaps the range; rows are NEVER truncated - Text passes through
/// untouched - so the exported span snaps OUTWARD to turn boundaries. That is why the document
/// must report ActualSpan, not the requested range: reporting the request over outward-snapped
/// content would be a small lie in an evidentiary document.</summary>
public static class ExcerptSelector
{
    /// <summary>Half-open overlap [FromMs, ToMs). Zero-length rows - markers, which have
    /// StartMs == EndMs - are treated as POINTS, because a strict overlap test would drop every
    /// marker in the range.</summary>
    public static bool Covers(DisplayRow row, ExcerptRange range)
        => row.EndMs > row.StartMs
            ? row.StartMs < range.ToMs && row.EndMs > range.FromMs
            : row.StartMs >= range.FromMs && row.StartMs < range.ToMs;

    public static IReadOnlyList<DisplayRow> Select(IReadOnlyList<DisplayRow> rows, ExcerptRange range)
        => [.. rows.Where(r => Covers(r, range))];

    /// <summary>The span the SELECTED rows actually cover - what the document reports.</summary>
    public static (long FromMs, long ToMs) ActualSpan(IReadOnlyList<DisplayRow> selected)
        => selected.Count == 0
            ? (0L, 0L)
            : (selected.Min(r => r.StartMs), selected.Max(r => r.EndMs));
}
```

- [ ] **Step 4: Write the failing `ResolveExcerptAsync` tests**

Create `tests/LocalScribe.App.Tests/ResolveExcerptTests.cs` — self-contained, mirroring the verified `MakeAsync` + `SeedLongTurnAsync` shape from `ExportDialogViewModelTests.cs` (the session runs 0-24 s inside a 30-minute recording):

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Excerpt range parsing and validation (design 2026-08-04 section 8). Lives in the
/// service, not the view model, because only the service has the session's local start (wallclock
/// mode) and its duration (bounds).</summary>
public sealed class ResolveExcerptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-exc-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>A 30-minute session starting 09:00 local (UTC), with turns only in the first 24 s.</summary>
    private async Task<MaintenanceService> MakeAsync(string timestampsMode = "relative")
    {
        var paths = new StoragePaths(_root);
        var settings = new FakeSettingsService(new Settings { Timestamps = timestampsMode });
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero)));
        Directory.CreateDirectory(paths.SessionDir("s1"));
        await new SessionStore(paths.SessionJson("s1")).SaveAsync(new SessionRecord
        {
            Id = "s1", App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, default);
        await new MetadataStore(paths.MetaJson("s1")).SaveAsync(new SessionMeta { Title = "Doe intake" }, default);

        long[][] times = [[0, 4000], [4400, 9000], [9400, 14000], [14400, 19000], [19400, 24000]];
        string[] words = ["one", "two", "three", "four", "five"];
        var store = new TranscriptStore(paths.TranscriptJsonl("s1"));
        for (int i = 0; i < words.Length; i++)
            await store.AppendAsync(TranscriptLine.Segment(i, TranscriptSource.Local,
                times[i][0], times[i][1], words[i], "Me"), default);
        return svc;
    }

    [Fact]
    public async Task Parses_relative_stamps()
    {
        var svc = await MakeAsync();
        var range = await svc.ResolveExcerptAsync("s1", "00:05", "00:15", default);
        Assert.Equal(5000, range.FromMs);
        Assert.Equal(15000, range.ToMs);
    }

    [Fact]
    public async Task Empty_from_means_start_and_empty_to_means_end()
    {
        var svc = await MakeAsync();
        var range = await svc.ResolveExcerptAsync("s1", "", "", default);
        Assert.Equal(0, range.FromMs);
        Assert.Equal(1_800_000, range.ToMs);
    }

    [Fact]
    public async Task Wallclock_mode_parses_against_the_sessions_own_local_start()
    {
        // The session starts 09:00; 09:00:10 is 10 s in, NOT 9 hours (design 2026-08-04 section 8).
        var svc = await MakeAsync(timestampsMode: "wallclock");
        var range = await svc.ResolveExcerptAsync("s1", "09:00:05", "09:00:20", default);
        Assert.Equal(5000, range.FromMs);
        Assert.Equal(20000, range.ToMs);
    }

    [Fact]
    public async Task Rejects_unparseable_input()
    {
        var svc = await MakeAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveExcerptAsync("s1", "banana", "", default));
        Assert.Contains("banana", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_backwards_range()
    {
        var svc = await MakeAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveExcerptAsync("s1", "00:15", "00:05", default));
        Assert.Contains("before its end", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_range_past_the_recording()
    {
        var svc = await MakeAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveExcerptAsync("s1", "00:00", "99:00", default));
        Assert.Contains("outside the recording", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_range_with_no_transcript_content()
    {
        // An empty document is never written; the user is told instead. Turns stop at 24 s.
        var svc = await MakeAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveExcerptAsync("s1", "10:00", "11:00", default));
        Assert.Contains("no transcript content", ex.Message);
    }
}
```

- [ ] **Step 5: Implement `ResolveExcerptAsync`**

Add to `MaintenanceService.cs` after `FilenameTokensAsync`:

```csharp
    /// <summary>Parse and validate an excerpt range (design 2026-08-04 section 8). Lives HERE,
    /// not in the view model: the dialog has only a session id and a title - neither the session's
    /// local start (wallclock mode) nor its duration (bounds). Called BEFORE the Save-As picker so
    /// the user learns about a bad range before choosing a destination. One parsing
    /// implementation, in the only place that holds the truth, directly unit-testable without a VM.
    ///
    /// This is a SEPARATE gate acquisition from the export that follows, so the projection loads
    /// twice. Accepted: the resolved range is a pair of millisecond offsets, which stays
    /// meaningful against a transcript that grew between the two loads (a live session), and the
    /// export always re-derives its rows from its own fresh load. Holding the gate across a modal
    /// Save-As would block the capture pipeline.</summary>
    public Task<ExcerptRange> ResolveExcerptAsync(string sessionId, string fromText, string toText,
        CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                throw new InvalidOperationException("The session no longer exists.");
            var loaded = await SessionProjectionLoader.LoadAsync(paths, settings.Current, time, sessionId, ct: inner);
            string mode = settings.Current.Timestamps;
            // A live session has DurationMs 0 until it finalizes; fall back to the rows so a
            // mid-recording excerpt is still bounded by something real.
            long durationMs = Math.Max(loaded.Session.DurationMs,
                loaded.Rows.Count > 0 ? loaded.Rows.Max(r => r.EndMs) : 0);

            long from = 0, to = durationMs;
            if (!string.IsNullOrWhiteSpace(fromText)
                && !TimestampParser.TryParse(fromText, mode, loaded.StartedLocal, out from))
                throw new InvalidOperationException($"'{fromText}' is not a time this transcript uses.");
            if (!string.IsNullOrWhiteSpace(toText)
                && !TimestampParser.TryParse(toText, mode, loaded.StartedLocal, out to))
                throw new InvalidOperationException($"'{toText}' is not a time this transcript uses.");
            if (from >= to)
                throw new InvalidOperationException("The excerpt's start must come before its end.");
            if (from < 0 || to > durationMs)
                throw new InvalidOperationException("That range falls outside the recording.");

            var range = new ExcerptRange(from, to);
            if (ExcerptSelector.Select(loaded.Rows, range).Count == 0)
                throw new InvalidOperationException("That range contains no transcript content.");
            return range;
        }, ct);
```

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExcerptSelectorTests|FullyQualifiedName~ResolveExcerptTests"`
Expected: PASS (7 + 7 tests).

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/ExcerptRange.cs src/LocalScribe.Core/Projection/ExcerptSelector.cs src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.Core.Tests/ExcerptSelectorTests.cs tests/LocalScribe.App.Tests/ResolveExcerptTests.cs
git commit -m "feat(export): whole-row excerpt selection and range validation"
```

---

## Task 12: `ExcerptSpan` provenance + the banner in all three formats

**Files:**
- Modify: `src/LocalScribe.Core/Projection/ExportProvenance.cs`
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs`, `MarkdownRenderer.cs`, `PlainTextRenderer.cs`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (3 export methods gain `ExcerptRange? excerpt`)
- Test: the three renderer test files

**Interfaces:**
- Produces: `ExportProvenance.ExcerptSpan : string?`; `ExportDocxAsync/ExportMarkdownAsync/ExportTextAsync(..., ExportOptions options, ExcerptRange? excerpt, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`:

```csharp
    [Fact]
    public void An_excerpt_renders_the_notice_on_page_1_and_in_the_pages_2_plus_header()
    {
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(),
            new ExportProvenance { ExcerptSpan = "00:12:30-00:18:45 of 01:47:12" }, null,
            [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var main = doc.MainDocumentPart!;

        Assert.Contains("00:12:30-00:18:45 of 01:47:12", main.Document.Body!.InnerText);
        Assert.Contains(ExportNotices.ExcerptNotice, main.Document.Body!.InnerText);

        // The DEFAULT header part (pages 2+) carries it; the FIRST-page part stays empty.
        var sectPr = main.Document.Body!.Elements<SectionProperties>().Single();
        string defaultId = sectPr.Elements<HeaderReference>()
            .Single(h => h.Type is not null && h.Type.Value == HeaderFooterValues.Default).Id!;
        var defaultHeader = (HeaderPart)main.GetPartById(defaultId);
        Assert.Contains(ExportNotices.ExcerptNotice, defaultHeader.Header!.InnerText);
    }

    [Fact]
    public void A_complete_transcript_renders_no_excerpt_notice_anywhere()
    {
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        Assert.DoesNotContain("EXCERPT", doc.MainDocumentPart!.Document.Body!.InnerText);
    }

    [Fact]
    public void Excerpt_and_in_progress_stack_as_two_header_paragraphs_ahead_of_the_running_head()
    {
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(),
            new ExportProvenance { InProgress = true, ExcerptSpan = "00:00:00-00:00:04 of 00:30:00" },
            null, [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var main = doc.MainDocumentPart!;
        var sectPr = main.Document.Body!.Elements<SectionProperties>().Single();
        string defaultId = sectPr.Elements<HeaderReference>()
            .Single(h => h.Type is not null && h.Type.Value == HeaderFooterValues.Default).Id!;
        var paragraphs = ((HeaderPart)main.GetPartById(defaultId)).Header!.Elements<Paragraph>().ToList();

        Assert.Equal(3, paragraphs.Count);
        Assert.Contains(ExportNotices.InProgressNotice, paragraphs[0].InnerText);
        Assert.Contains(ExportNotices.ExcerptNotice, paragraphs[1].InnerText);
        Assert.Contains("STYLEREF", paragraphs[2].InnerXml);        // the running head, untouched
    }
```

Append to `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`:

```csharp
    [Fact]
    public void An_excerpt_renders_the_span_and_the_notice()
    {
        string md = MarkdownRenderer.Write(Header(), Meta(),
            new ExportProvenance { ExcerptSpan = "00:12:30-00:18:45 of 01:47:12" }, null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains("- **Excerpt:** 00:12:30-00:18:45 of 01:47:12\n", md);
        Assert.Contains("**" + ExportNotices.ExcerptNotice + "**", md);
    }

    [Fact]
    public void A_complete_transcript_renders_no_excerpt_lines()
    {
        string md = MarkdownRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.DoesNotContain("Excerpt", md);
    }
```

Append to `tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs`:

```csharp
    [Fact]
    public void An_excerpt_renders_the_span_and_the_notice_undecorated()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(),
            new ExportProvenance { ExcerptSpan = "00:12:30-00:18:45 of 01:47:12" }, null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains("Excerpt: 00:12:30-00:18:45 of 01:47:12\r\n", txt);
        Assert.Contains(ExportNotices.ExcerptNotice + "\r\n", txt);
        Assert.DoesNotContain("**", txt);
    }

    [Fact]
    public void A_complete_transcript_renders_no_excerpt_lines()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.DoesNotContain("Excerpt", txt);
    }
```

> Both files' `Write` calls now carry the Task 9 `ExportSummary? summary` parameter as the 4th argument (`null` here). If a mirrored helper such as `Header()`/`Meta()`/`Turn(...)` is missing from `MarkdownRendererWriteTests.cs`, copy the private statics from `PlainTextRendererWriteTests.cs` Task 2 Step 1 rather than inventing new fixture shapes.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~excerpt"`
Expected: FAIL — `ExcerptSpan` does not exist on `ExportProvenance`.

- [ ] **Step 3: Add the provenance field**

In `src/LocalScribe.Core/Projection/ExportProvenance.cs`, after `InProgress`:

```csharp
    /// <summary>Non-null when this document is a TIME-RANGE EXCERPT (design 2026-08-04 section 8):
    /// the ACTUAL span the selected rows cover, e.g. "00:12:30-00:18:45 of 01:47:12" - snapped
    /// outward to whole turns, never the requested range. A fact about the document's
    /// completeness, the same category as InProgress, which is why it lives beside it. The INPUT
    /// window is ExcerptRange; renderers never see it and never filter rows.</summary>
    public string? ExcerptSpan { get; init; }
```

- [ ] **Step 4: Render it**

`DocxRenderer.Write` — after the `InProgressLine()` call, before `AppendSummary`:

```csharp
        if (provenance.ExcerptSpan is { } excerptSpan)
        {
            body.AppendChild(MetaLine("Excerpt", excerptSpan));
            body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
                new Run(new RunProperties(new Bold()), MakeText(ExportNotices.ExcerptNotice))));
        }
```

In the header-part build (`:162-165`), after the in-progress paragraph:

```csharp
        if (provenance.ExcerptSpan is not null)
            headerParagraphs.Add(new Paragraph(
                new Run(new RunProperties(new Bold()), MakeText(ExportNotices.ExcerptNotice))));
```

`MarkdownRenderer.Write` — after the `SpeakersHeard` block, before the in-progress block:

```csharp
        if (provenance.ExcerptSpan is { } excerptSpan) AppendMeta(sb, "Excerpt", excerptSpan);
```

and after the in-progress notice:

```csharp
        if (provenance.ExcerptSpan is not null)
            sb.Append('\n').Append("**").Append(ExportNotices.ExcerptNotice).Append("**").Append('\n');
```

`PlainTextRenderer.Write` — the same two insertions, undecorated:

```csharp
        if (provenance.ExcerptSpan is { } excerptSpan) AppendMeta(sb, "Excerpt", excerptSpan);
```
```csharp
        if (provenance.ExcerptSpan is not null)
            sb.Append(Nl).Append(ExportNotices.ExcerptNotice).Append(Nl);
```

- [ ] **Step 5: Thread the range through the export methods**

In `MaintenanceService.cs`, add an `ExcerptRange? excerpt` parameter **before** `CancellationToken ct` on `ExportDocxAsync`, `ExportMarkdownAsync` and `ExportTextAsync`. In each, after the `LoadAsync` line:

```csharp
            var rows = excerpt is null ? loaded.Rows : ExcerptSelector.Select(loaded.Rows, excerpt);
            var provenance = ProvenanceFor(loaded) with { ExcerptSpan = SpanLabel(rows, excerpt, loaded) };
```

and pass `rows` and `provenance` to the renderer instead of `loaded.Rows` / `ProvenanceFor(loaded)`.

Add the label helper beside `ProvenanceFor`:

```csharp
    /// <summary>The excerpt span label (design 2026-08-04 section 8): the ACTUAL outward-snapped
    /// span of the selected rows, not the requested range - reporting the request over
    /// outward-snapped content would be a small lie in an evidentiary document. Null for a
    /// complete transcript.</summary>
    private static string? SpanLabel(IReadOnlyList<DisplayRow> rows, ExcerptRange? excerpt,
        LoadedProjection loaded)
    {
        if (excerpt is null) return null;
        (long fromMs, long toMs) = ExcerptSelector.ActualSpan(rows);
        long durationMs = Math.Max(loaded.Session.DurationMs,
            loaded.Rows.Count > 0 ? loaded.Rows.Max(r => r.EndMs) : 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Hms(fromMs)}-{Hms(toMs)} of {Hms(durationMs)}");
    }

    private static string Hms(long ms)
        => TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
```

Update the three call sites in `ExportDialogViewModel.ExportAsync` to pass `null` for now (Task 13 supplies the real range).

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx`
Expected: PASS with the known 2 Core fixture failures by name.

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/ExportProvenance.cs src/LocalScribe.Core/Projection/DocxRenderer.cs src/LocalScribe.Core/Projection/MarkdownRenderer.cs src/LocalScribe.Core/Projection/PlainTextRenderer.cs src/LocalScribe.App/Services/MaintenanceService.cs src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs tests/LocalScribe.Core.Tests/DocxRendererTests.cs tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs tests/LocalScribe.Core.Tests/PlainTextRendererWriteTests.cs
git commit -m "feat(export): excerpt banner and span label in all three formats"
```

---

## Task 13: Excerpt dialog UI

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs`
- Modify: `src/LocalScribe.App/ExportDialog.xaml`
- Test: `tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs`

**Interfaces:**
- Produces: `ExcerptEnabled : bool`, `ExcerptFrom : string`, `ExcerptTo : string`, `TimestampsToggleEnabled : bool`.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task Excerpt_forces_timestamps_on_and_disables_the_toggle()
    {
        // Timestamps are the anchor that maps an excerpt back to the full transcript; line
        // numbers restart within the excerpt and do NOT map back (design 2026-08-04 section 8).
        var (svc, _, rep) = await MakeAsync();
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => null, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, IncludeTimestamps = false };

        Assert.True(vm.TimestampsToggleEnabled);
        vm.ExcerptEnabled = true;

        Assert.True(vm.IncludeTimestamps);
        Assert.False(vm.TimestampsToggleEnabled);
    }

    [Fact]
    public async Task An_excerpt_filename_carries_the_forced_suffix_regardless_of_template()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        SavePathRequest? seen = null;
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            req => { seen = req; return null; }, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExcerptEnabled = true, ExcerptFrom = "00:00", ExcerptTo = "00:10" };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Doe intake-excerpt.md", seen!.DefaultFileName);
    }

    [Fact]
    public async Task A_bad_range_is_reported_before_the_save_as_picker_opens()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        bool pickerOpened = false;
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => { pickerOpened = true; return null; }, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExcerptEnabled = true, ExcerptFrom = "banana" };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.False(pickerOpened);
        Assert.NotEmpty(rep.Errors);
    }

    [Fact]
    public async Task The_excerpt_range_is_never_persisted()
    {
        // A remembered range would silently emit a partial export of the NEXT, unrelated session.
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        var settings = new FakeSettingsService();
        string dest = Path.Combine(_root, "exc.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExcerptEnabled = true, ExcerptFrom = "00:00", ExcerptTo = "00:10" };

        await vm.ExportCommand.ExecuteAsync(null);

        var fresh = new ExportDialogViewModel("s1", "T", svc, settings, _ => null, _ => { }, rep, a => a());
        Assert.False(fresh.ExcerptEnabled);
        Assert.Equal("", fresh.ExcerptFrom);
        Assert.Equal("", fresh.ExcerptTo);
    }

    [Fact]
    public async Task An_excerpt_export_carries_the_banner_and_the_span()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        string dest = Path.Combine(_root, "exc2.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExcerptEnabled = true, ExcerptFrom = "00:00", ExcerptTo = "00:10" };

        await vm.ExportCommand.ExecuteAsync(null);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains(ExportNotices.ExcerptNotice, md);
        Assert.Contains("Excerpt:", md);
        Assert.Empty(rep.Errors);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportDialogViewModelTests.Excerpt|FullyQualifiedName~ExportDialogViewModelTests.An_excerpt|FullyQualifiedName~ExportDialogViewModelTests.A_bad_range|FullyQualifiedName~ExportDialogViewModelTests.The_excerpt_range"`
Expected: FAIL — `ExcerptEnabled` does not exist.

- [ ] **Step 3: Implement**

In `ExportDialogViewModel.cs` add:

```csharp
    /// <summary>Time-range excerpt (design 2026-08-04 section 8). NEVER seeded from settings and
    /// never persisted: a remembered range would silently emit a partial export of the next,
    /// unrelated session.</summary>
    [ObservableProperty] private bool _excerptEnabled;
    [ObservableProperty] private string _excerptFrom = "";
    [ObservableProperty] private string _excerptTo = "";

    /// <summary>Timestamps are the anchor that maps an excerpt back to the full transcript - line
    /// numbers restart within the excerpt and do not - so an excerpt forces them on.</summary>
    public bool TimestampsToggleEnabled => !ExcerptEnabled;

    partial void OnExcerptEnabledChanged(bool value)
    {
        if (value) IncludeTimestamps = true;
        OnPropertyChanged(nameof(TimestampsToggleEnabled));
    }
```

In `ExportAsync`, resolve the range before the Save-As build (inside the `try`):

```csharp
            ExcerptRange? excerpt = null;
            if (ExcerptEnabled && Format != ExportFormat.Zip)
                excerpt = await _maintenance.ResolveExcerptAsync(_sessionId, ExcerptFrom, ExcerptTo,
                    CancellationToken.None);
```

Extend the filename stem build:

```csharp
            string stem = ExportFileNames.Sanitize(
                ExportFileNames.Expand(_settings.Current.Export.FilenameTemplate, tokens));
            // Forced, outside template control: a file named identically to the full transcript is
            // precisely how an excerpt gets filed as one.
            if (excerpt is not null) stem += "-excerpt";
```

Pass `excerpt` to the three `Export*Async` calls.

`PersistChoicesAsync` is left untouched — it must not learn about the excerpt fields.

- [ ] **Step 4: Update the XAML**

Add after the toggles `StackPanel` in `ExportDialog.xaml`:

```xml
        <StackPanel Margin="0,8,0,0" Visibility="{Binding ShowOptionToggles, Converter={StaticResource BoolToVis}}">
            <CheckBox Content="Export a time range only" IsChecked="{Binding ExcerptEnabled}" Margin="0,2" />
            <StackPanel Orientation="Horizontal" Margin="16,2,0,2"
                        Visibility="{Binding ExcerptEnabled, Converter={StaticResource BoolToVis}}">
                <TextBlock Text="From" VerticalAlignment="Center" Margin="0,0,6,0" />
                <TextBox Text="{Binding ExcerptFrom, UpdateSourceTrigger=PropertyChanged}" Width="80" />
                <TextBlock Text="To" VerticalAlignment="Center" Margin="12,0,6,0" />
                <TextBox Text="{Binding ExcerptTo, UpdateSourceTrigger=PropertyChanged}" Width="80" />
            </StackPanel>
        </StackPanel>
```

And gate the timestamps checkbox (`:23`) on the new property:

```xml
            <CheckBox Content="Include timestamps" IsChecked="{Binding IncludeTimestamps}"
                      IsEnabled="{Binding TimestampsToggleEnabled}" Margin="0,2" />
```

Plain `TextBox`es deliberately — **not** the read view's auto-colon masked go-to box, which carried the unpadded-paste defect where `1:02:03` normalised to ten hours. `TimestampParser` handles that string correctly; the defect was in the mask.

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~ExportDialogViewModelTests"`
Expected: PASS.

- [ ] **Step 6: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/ExportDialog.xaml tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs
git commit -m "feat(export): time-range excerpt UI with forced anchors and filename suffix"
```

---

## Task 14: Whole-round verification

**Files:**
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`

- [ ] **Step 1: Write the stacked-notice schema test**

Append to `DocxRendererTests.cs`:

```csharp
    [Fact]
    public void A_document_with_summary_excerpt_and_in_progress_all_at_once_is_schema_valid()
    {
        // Three stacked header paragraphs plus a new body section is the shape most likely to
        // trip Word's pPr child ordering, and the OpenXML SDK accepts an invalid order SILENTLY.
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(),
            new ExportProvenance
            {
                InProgress = true,
                ExcerptSpan = "00:00:00-00:00:04 of 00:30:00",
                AudioFileName = "intake.m4a",
                AudioSha256 = "abc123",
            },
            new ExportSummary
            {
                ContentMarkdown = "## Summary\nThey agreed.\n\n## Key topics\n- costs\n- timing\n",
                ProvenanceLine = "generated 2026-08-01 14:22, Qwen3-4B-Instruct-2507.gguf (CUDA)",
                StaleNotice = "OUT OF DATE: the transcript changed after this summary was generated.",
            },
            [Turn(0, 4000, "Sam", "hello"), Turn(5000, 9000, "Bob", "hi")],
            "relative", DocxPageSize.A4, new ExportOptions { TimestampIntervalMs = 15000 });

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var errors = new OpenXmlValidator().Validate(doc).ToList();

        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => e.Description + " @ " + e.Path?.XPath)));
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx --filter "FullyQualifiedName~A_document_with_summary_excerpt"`
Expected: PASS. If it fails on a `pPr` child-order complaint, fix the element order against the ECMA-376 XSD sequence in Global Constraints — **not** against Microsoft Learn's alphabetical listing.

- [ ] **Step 3: Full-suite run**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx`

Expected: App and Mcp fully green. Core green **except** `DiarisationFixtureTests.Der_within_baseline_plus_epsilon` and `GoldenCorpusFixtureTests.Golden_pair_wer_stays_at_baseline`. Judge by **name**: any other failing name is a regression from this round. `WhisperFixtureTests.Tiny_model_transcribes_synthetic_tone_...` is intermittent and may appear.

- [ ] **Step 4: Whole-branch ASCII byte-scan**

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

Expected: only `scan complete` — no `NON-ASCII` lines. Markdown docs under `docs/` are exempt; **source files are not**. If a `.cs` file reports non-ASCII, an escape was converted to a literal glyph: restore the `\uXXXX` form.

- [ ] **Step 5: Confirm line endings survived**

```powershell
cd F:\LocalScribe
git diff --stat master...HEAD
git diff --check master...HEAD
```
Expected: no whitespace errors, and a plausible changed-file list (no file showing as wholly rewritten, which would indicate a CRLF/LF flip).

- [ ] **Step 6: Commit**

```bash
cd F:/LocalScribe
git add tests/LocalScribe.Core.Tests/DocxRendererTests.cs
git commit -m "test(export): schema validation with summary, excerpt and in-progress stacked"
```

---

## Post-Implementation

Once all 14 tasks are green:

1. **Request code review** — use `superpowers:requesting-code-review`.
2. **Do NOT merge.** Round 1's flow was: implement → review → **user smoke in real desktop Word** → merge. The excerpt banner, the summary section and the `.txt` CRLF output all need eyes on a real document before this branch lands.
3. **Smoke checklist for the user:**
   - Export a session as `.txt`; open in Notepad — line endings clean, no BOM garbage at the top.
   - Set a filename template such as `{date} {matter} {title}`; confirm the Save-As default name and that an untagged session collapses the gap rather than showing `2026-07-03 -Title`.
   - Export with the summary ticked; in Word, confirm the summary sits above the rule, carries the draft label, and **the transcript's line numbers still start at 1 on the first turn**.
   - Export a mid-recording session as an excerpt; confirm both notices appear on page 2+ and page 1, and the span reads as whole turns.
   - Re-open the dialog and confirm format/toggles came back but the excerpt checkbox and range are clear.
4. **Round 3** is the matter-level combined transcript (`ExportMatterArchiveAsync`, `MaintenanceService.cs:1062`), which inherits this round's format set, filename template, summary decision and excerpt rules.
