# Export Cadence + Courtroom Docx Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox ("- [ ]") syntax for tracking.

**Goal:** Add an optional fixed-15-second timestamp cadence to both textual exports and replace the flat .docx export layout with a courtroom transcript layout (hanging-indent turn column, count-by-5 per-page line numbers, footer page numbers), per spec items 5+6 of `docs/superpowers/specs/2026-08-02-ux-round-design.md`.

**Architecture:** A new pure static chunker `TimestampCadence` in `LocalScribe.Core/Projection` splits a grouped `DisplayRow` into stamp-anchored chunks at segment boundaries; BOTH export renderers (`MarkdownRenderer.Write`, `DocxRenderer.Write`) consume it — chunk 0 renders as today, chunks 1..n render as stamp-only continuation paragraphs. The knob travels as `TimestampIntervalMs` (int, default 0) on the shared format-neutral `DocxOptions` record, set by one new export-dialog checkbox (fixed 15 000 ms, not persisted). `DocxRenderer` additionally gains a `StyleDefinitionsPart` (`DocDefaults` + named `TranscriptTurn` style), tabbed hanging-indent turn paragraphs with an auto-sized text column, explicit page margins, `LineNumberType` line numbering with `SuppressLineNumbers` on the metadata header, and a footer `PAGE` field. `MaintenanceService` plumbing, the .zip export, and the save-time `transcript.md`/`.txt` are untouched.

**Tech Stack:** .NET 10 (`net10.0-windows`, LangVersion latest), WPF + CommunityToolkit.Mvvm (`[ObservableProperty]`) for the dialog, DocumentFormat.OpenXml **3.5.1** (already referenced in `src\LocalScribe.Core\LocalScribe.Core.csproj:19` — no new dependency), xUnit.

**Task order:** 1 → 2 → 3 are sequential (chunker → options+markdown → dialog). 4 → 5 → 6 → 7 are sequential edits to `DocxRenderer.cs` and its test file. Task 4 may start any time after Task 2 (it needs the new `DocxOptions` shape); Task 7 needs Tasks 1 and 4. Tasks 8–9 last.

## Global Constraints

- **Strict TDD:** write the failing test, run it, watch it fail with the expected message, THEN implement — every task, no exceptions.
- **No Unicode emojis** anywhere in code, tests, comments, or scripts.
- **VMs stay WPF-free:** nothing under `src\LocalScribe.App\ViewModels` or `src\LocalScribe.Core` may reference WPF types.
- **No bool-inverting converter exists** (house rule) — inverted-visibility XAML uses Style + DataTrigger; this plan needs neither (the new checkbox binds `IsEnabled` to a plain bool directly).
- **`[ObservableProperty]` equality-gates same-value sets** — re-raise manually after collection rebuilds when needed (no collection rebuilds in this plan; stated for workers).
- **Invariant culture in all export text**; the A4/Letter page size chosen from `RegionInfo` at the call site is the single permitted machine-locale dependence (spec 11.2).
- **Transcripts are evidence — never destructive:** renderers emit row text VERBATIM (locked rule, `MarkdownRenderer.cs:40-43`); cadence changes only where paragraphs break, never the words; the single-space chunk join is byte-identical to `SectionGrouper.cs:34`.
- **Close any running LocalScribe.App.exe before building** — a running app locks Core.dll and the build fails with MSB3027.
- **View-layer visual behavior cannot be unit-tested here** (no STA/WPF harness; how Word paints the layout is out of test reach) — such verification is a smoke-runbook checkbox (Task 8), never a fake test.
- **`MaintenanceService.ExportDocxAsync` (`MaintenanceService.cs:999-1024`) and `ExportMarkdownAsync` (`:1031-1054`) are documented line-for-line mirrors.** This plan adds NO plumbing to either (the new option rides the existing `DocxOptions` parameter). If a worker ever must touch one, the identical change lands in both.
- **Untouched by design:** `.zip` export (`ExportSessionArchiveAsync`), save-time `transcript.md`/`.txt` (`SessionWriter.cs`), the save-time `MarkdownRenderer.Render` dialect, `PlainTextRenderer`, read-view paragraphing, `RendererTests` byte-identity goldens.

---

### Task 1: `TimestampCadence.Chunk` — pure Core chunker

**Files:**
- Create: `src\LocalScribe.Core\Projection\TimestampCadence.cs`
- Test: Create `tests\LocalScribe.Core.Tests\TimestampCadenceTests.cs`

**Interfaces:**
- Consumes (existing): `DisplayRow` (`src\LocalScribe.Core\Projection\DisplayRow.cs:7-19` — `IsMarker`, `StartMs`, `Text`, `Segments`), `RowSegment` (`src\LocalScribe.Core\Projection\RowSegment.cs:10-17` — positional record `(int Seq, TranscriptSource Source, long StartMs, long EndMs, string ProjectedText, string RawText, bool IsCorrected, bool IsPinned, ...)`), `PreRow` (`src\LocalScribe.Core\Projection\PreRow.cs:8-10`), `SectionGrouper.Group(IReadOnlyList<PreRow>, int)` (`SectionGrouper.cs:12`).
- Produces: `public static IReadOnlyList<CadenceChunk> TimestampCadence.Chunk(DisplayRow row, int intervalMs)` and `public sealed record CadenceChunk(long StampMs, string Text, IReadOnlyList<RowSegment> Segments)` — both in namespace `LocalScribe.Core.Projection`. Tasks 2 and 7 call `Chunk`.

**Steps:**

- [ ] 1. Write the failing test file `tests\LocalScribe.Core.Tests\TimestampCadenceTests.cs` (Core.Tests convention: no namespace, xUnit via global using):

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class TimestampCadenceTests
{
    private static RowSegment Seg(int seq, long start, long end, string text) =>
        new(seq, TranscriptSource.Local, start, end, text, text, false, false);

    private static DisplayRow Row(params RowSegment[] segs) => new()
    {
        StartMs = segs[0].StartMs, EndMs = segs[^1].EndMs, DisplayName = "Me",
        Text = string.Join(" ", segs.Select(s => s.ProjectedText)), Segments = segs,
    };

    [Fact]
    public void Non_positive_interval_returns_one_whole_row_chunk()
    {
        var row = Row(Seg(0, 0, 4000, "one"), Seg(1, 20000, 24000, "two"));
        foreach (int interval in new[] { 0, -1 })
        {
            var only = Assert.Single(TimestampCadence.Chunk(row, interval));
            Assert.Equal(row.StartMs, only.StampMs);
            Assert.Equal(row.Text, only.Text);
            Assert.Same(row.Segments, only.Segments);
        }
    }

    [Fact]
    public void Marker_rows_pass_through_as_one_chunk()
    {
        var row = new DisplayRow
        { IsMarker = true, StartMs = 30000, EndMs = 30000, Text = "audio device changed" };
        var only = Assert.Single(TimestampCadence.Chunk(row, 15000));
        Assert.Equal("audio device changed", only.Text);
        Assert.Equal(30000L, only.StampMs);
    }

    [Fact]
    public void Rows_without_segments_pass_through_as_one_chunk()
    {
        // Live rows and the legacy renderer fixtures carry Text only (Segments empty).
        var row = new DisplayRow { StartMs = 1000, EndMs = 90000, DisplayName = "Me", Text = "long text" };
        var only = Assert.Single(TimestampCadence.Chunk(row, 15000));
        Assert.Equal("long text", only.Text);
        Assert.Equal(1000L, only.StampMs);
    }

    [Fact]
    public void No_boundary_crossing_the_interval_returns_row_text_verbatim()
    {
        // The whole-row chunk must carry row.Text VERBATIM, not the Segments re-join - proven by
        // a row whose Text deliberately differs from the join (SectionGrouper's null-payload
        // merge can contribute text without a Segment, SectionGrouper.cs:36).
        var row = new DisplayRow
        {
            StartMs = 0, EndMs = 9000, DisplayName = "Me", Text = "one lost two",
            Segments = new[] { Seg(0, 0, 4000, "one"), Seg(1, 4400, 9000, "two") },
        };
        var only = Assert.Single(TimestampCadence.Chunk(row, 15000));
        Assert.Equal("one lost two", only.Text);
    }

    [Fact]
    public void Boundary_at_exactly_the_interval_starts_a_new_chunk()
    {
        var row = Row(Seg(0, 0, 7000, "one"), Seg(1, 15000, 20000, "two"));   // 15000 - 0 == interval
        var chunks = TimestampCadence.Chunk(row, 15000);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(0L, chunks[0].StampMs);
        Assert.Equal("one", chunks[0].Text);
        Assert.Equal(15000L, chunks[1].StampMs);
        Assert.Equal("two", chunks[1].Text);
    }

    [Fact]
    public void Elapsed_time_measures_from_the_last_shown_stamp_not_the_previous_segment()
    {
        // Breaks at 15100 (>= 15000 since stamp 0) and at 30200 (>= 15000 since stamp 15100);
        // 18400 does NOT break (only 3300 since the 15100 stamp).
        var row = Row(
            Seg(0, 0, 4000, "a"), Seg(1, 4400, 9000, "b"),
            Seg(2, 15100, 18000, "c"), Seg(3, 18400, 22000, "d"),
            Seg(4, 30200, 31000, "e"));
        var chunks = TimestampCadence.Chunk(row, 15000);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(new[] { 0L, 15100L, 30200L }, chunks.Select(c => c.StampMs));
        Assert.Equal(new[] { "a b", "c d", "e" }, chunks.Select(c => c.Text));
    }

    [Fact]
    public void Chunk_texts_rejoin_byte_identically_to_a_section_grouper_row()
    {
        // Join fidelity: the chunker's single-space join must be byte-identical to
        // SectionGrouper's prev.Text + " " + p.Text merge (SectionGrouper.cs:34).
        var pre = new[]
        {
            new PreRow(0, 4000, 0, 0, "Me", "one", false, Seg(0, 0, 4000, "one")),
            new PreRow(4400, 9000, 0, 1, "Me", "two", false, Seg(1, 4400, 9000, "two")),
            new PreRow(19400, 24000, 0, 2, "Me", "three", false, Seg(2, 19400, 24000, "three")),
        };
        var row = Assert.Single(SectionGrouper.Group(pre, gapMs: 30000));
        var chunks = TimestampCadence.Chunk(row, 15000);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(row.Text, string.Join(" ", chunks.Select(c => c.Text)));
    }
}
```

- [ ] 2. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~TimestampCadenceTests"` — expect a BUILD failure: `error CS0246: The type or namespace name 'TimestampCadence' could not be found` (and the same for `CadenceChunk`).

- [ ] 3. Create `src\LocalScribe.Core\Projection\TimestampCadence.cs`:

```csharp
namespace LocalScribe.Core.Projection;

/// <summary>One cadence chunk of a grouped turn (design 2026-08-02 item 5): the stamp shown at the
/// chunk's head, the chunk's text, and its constituent segments. Chunk 0 renders as the normal
/// turn; chunks 1..n render as stamp-only continuation paragraphs (the name is not repeated).</summary>
public sealed record CadenceChunk(long StampMs, string Text, IReadOnlyList<RowSegment> Segments);

/// <summary>Splits a grouped DisplayRow into export chunks at the segment boundaries where at
/// least intervalMs of wall time has elapsed since the LAST SHOWN stamp (design 2026-08-02
/// item 5). Pure and export-only: transcript.jsonl, the read view, and the save-time projections
/// never see chunks. A row passes through as ONE whole-row chunk when intervalMs is not positive,
/// the row is a marker, the row has no Segments payload (live rows, legacy test fixtures), or no
/// boundary crosses the interval. The whole-row chunk carries row.Text VERBATIM - never the
/// Segments re-join - so uncadenced output stays byte-identical (SectionGrouper's null-payload
/// merge means Segments-derived text is not guaranteed to equal row.Text). Split chunk text uses
/// the single-space join byte-identical to SectionGrouper.cs:34.</summary>
public static class TimestampCadence
{
    public static IReadOnlyList<CadenceChunk> Chunk(DisplayRow row, int intervalMs)
    {
        if (intervalMs <= 0 || row.IsMarker || row.Segments.Count == 0)
            return [WholeRow(row)];

        var chunks = new List<CadenceChunk>();
        var current = new List<RowSegment>();
        long lastStampMs = row.StartMs;
        long chunkStampMs = row.StartMs;
        foreach (var seg in row.Segments)
        {
            if (current.Count > 0 && seg.StartMs - lastStampMs >= intervalMs)
            {
                chunks.Add(Close(chunkStampMs, current));
                current = [];
                chunkStampMs = seg.StartMs;
                lastStampMs = seg.StartMs;
            }
            current.Add(seg);
        }
        chunks.Add(Close(chunkStampMs, current));
        return chunks.Count == 1 ? [WholeRow(row)] : chunks;
    }

    private static CadenceChunk WholeRow(DisplayRow row) => new(row.StartMs, row.Text, row.Segments);

    private static CadenceChunk Close(long stampMs, List<RowSegment> segments)
        => new(stampMs, string.Join(" ", segments.Select(s => s.ProjectedText)), segments);
}
```

- [ ] 4. Re-run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~TimestampCadenceTests"` — expect PASS, 7 tests.

- [ ] 5. Commit:
```
git add src\LocalScribe.Core\Projection\TimestampCadence.cs tests\LocalScribe.Core.Tests\TimestampCadenceTests.cs
git commit -m "feat(export): add pure TimestampCadence chunker for cadence stamps"
```

---

### Task 2: `DocxOptions.TimestampIntervalMs` + markdown continuation paragraphs

**Files:**
- Modify: `src\LocalScribe.Core\Projection\DocxRenderer.cs` (the `DocxOptions` record only, currently lines 7-13)
- Modify: `src\LocalScribe.Core\Projection\MarkdownRenderer.cs` (the `Write` turn loop, currently lines 60-73; doc comment lines 34-43)
- Test: `tests\LocalScribe.Core.Tests\MarkdownRendererWriteTests.cs` (add 3 tests; the 4 existing tests must pass byte-unchanged)

**Interfaces:**
- Consumes: `TimestampCadence.Chunk(DisplayRow, int) -> IReadOnlyList<CadenceChunk>` (Task 1); `TimestampFormat.Stamp(long startMs, string mode, DateTimeOffset startedAtLocal) -> string` (`TimestampFormat.cs:9`).
- Produces: `DocxOptions.TimestampIntervalMs` — `public int TimestampIntervalMs { get; init; } = 0;` on the shared record. Tasks 3, 4, 7 rely on this exact name/type/default. Markdown continuation dialect: `**[03:15]** text` (bold bracketed stamp, two spaces of markdown syntax, single space, chunk text).

**Steps:**

- [ ] 1. Add three failing tests to `tests\LocalScribe.Core.Tests\MarkdownRendererWriteTests.cs`. First add the using and the segment helper (below the existing `Sample()` method at lines 10-23):

```csharp
using LocalScribe.Core.Model;
```
(at the top, beside `using LocalScribe.Core.Projection;`), then inside the class:

```csharp
    private static RowSegment Seg(int seq, long start, long end, string text) =>
        new(seq, TranscriptSource.Local, start, end, text, text, false, false);

    /// <summary>A single Sam turn whose third segment starts 15.2s after the first - one cadence
    /// break at 16200ms with the default 15s interval.</summary>
    private static DisplayRow[] LongTurn() => new[]
    {
        new DisplayRow
        {
            StartMs = 1000, EndMs = 21000, DisplayName = "Sam",
            Text = "First part. Second part. Third part.",
            Segments = new[]
            {
                Seg(0, 1000, 5000, "First part."),
                Seg(1, 5400, 10000, "Second part."),
                Seg(2, 16200, 21000, "Third part."),
            },
        },
    };

    [Fact]
    public void Cadence_splits_a_long_turn_into_stamp_only_continuation_paragraphs()
    {
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, LongTurn(), "relative", "",
            new DocxOptions { TimestampIntervalMs = 15000 });

        string expected =
            "# Weekly Sync\n" +
            "\n" +
            "- **App:** Teams\n" +
            "- **Date:** 2026-06-30 14:32\n" +
            "- **Matter(s):** Acme (2026-014)\n" +
            "- **Participants:** Sam (Local), Bob (Remote)\n" +
            "- **Medium:** Teams\n" +
            "\n" +
            "_" + DocxRenderer.Disclaimer + "_\n" +
            "\n" +
            "**[00:01] Sam:** First part. Second part.\n" +
            "\n" +
            "**[00:16]** Third part.\n";
        Assert.Equal(expected, md);
    }

    [Fact]
    public void Cadence_is_ignored_when_timestamps_are_off()
    {
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, LongTurn(), "relative", "",
            new DocxOptions { IncludeTimestamps = false, TimestampIntervalMs = 15000 });
        Assert.Contains("**Sam:** First part. Second part. Third part.\n", md);
        Assert.DoesNotContain("[00:16]", md);
    }

    [Fact]
    public void Default_interval_zero_keeps_the_turn_as_one_paragraph()
    {
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, LongTurn(), "relative", "", new DocxOptions());
        Assert.Contains("**[00:01] Sam:** First part. Second part. Third part.\n", md);
        Assert.DoesNotContain("**[00:16]**", md);
    }
```

- [ ] 2. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~MarkdownRendererWriteTests"` — expect BUILD failure: `error CS0117: 'DocxOptions' does not contain a definition for 'TimestampIntervalMs'`.

- [ ] 3. Extend the record in `src\LocalScribe.Core\Projection\DocxRenderer.cs` (replace lines 7-13):

```csharp
/// <summary>The user-facing export toggles (design 3.3 + 2026-08-02 item 5). House style mirrors
/// PhantomBleedOptions: sealed record + { get; init; } with inline defaults. Format-neutral and
/// shared deliberately by the .docx and .md export renderers.</summary>
public sealed record DocxOptions
{
    public bool IncludeTimestamps { get; init; } = true;
    public bool IncludeMarkers { get; init; } = true;
    /// <summary>Extra mid-turn stamp cadence (design 2026-08-02 item 5): a stamp-only continuation
    /// paragraph starts at the first segment boundary at/after this many ms since the last shown
    /// stamp. 0 (default) = off. Renderers force it off when IncludeTimestamps is false.</summary>
    public int TimestampIntervalMs { get; init; } = 0;
}
```

- [ ] 4. Rewrite the turn loop in `MarkdownRenderer.Write` (replace the `foreach` at lines 60-73):

```csharp
        foreach (var row in rows)
        {
            if (row.IsMarker)
            {
                if (options.IncludeMarkers)
                    sb.Append('\n').Append("_[").Append(row.Text).Append("]_").Append('\n');
                continue;   // toggled-off marker: dropped entirely, no stray blank line
            }
            // Cadence chunking (design 2026-08-02 item 5): chunk 0 renders exactly as before;
            // later chunks are stamp-only continuation paragraphs. Interval 0 (or timestamps off)
            // yields one whole-row chunk carrying row.Text verbatim - byte-identical output.
            var chunks = TimestampCadence.Chunk(row,
                options.IncludeTimestamps ? options.TimestampIntervalMs : 0);
            string label = options.IncludeTimestamps
                ? "[" + TimestampFormat.Stamp(row.StartMs, timestampsMode, header.StartedAtLocal)
                    + "] " + row.DisplayName
                : row.DisplayName ?? "";
            sb.Append('\n').Append("**").Append(label).Append(":** ").Append(chunks[0].Text).Append('\n');
            for (int i = 1; i < chunks.Count; i++)
                sb.Append('\n').Append("**[")
                  .Append(TimestampFormat.Stamp(chunks[i].StampMs, timestampsMode, header.StartedAtLocal))
                  .Append("]** ").Append(chunks[i].Text).Append('\n');
        }
```

Also update one phrase in the `Write` doc comment (lines 34-43): change `gated by the two DocxOptions toggles (the options record is format-neutral - two bools - and shared deliberately)` to `gated by the DocxOptions toggles (the options record is format-neutral and shared deliberately; TimestampIntervalMs adds stamp-only continuation paragraphs, design 2026-08-02 item 5)`.

- [ ] 5. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~MarkdownRendererWriteTests"` — expect PASS, 7 tests (4 pre-existing goldens byte-unchanged + 3 new).

- [ ] 6. Run the docx suite untouched-check: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"` — expect PASS, 3 tests (the record change is additive; the docx renderer still ignores the new field until Task 7).

- [ ] 7. Commit:
```
git add src\LocalScribe.Core\Projection\DocxRenderer.cs src\LocalScribe.Core\Projection\MarkdownRenderer.cs tests\LocalScribe.Core.Tests\MarkdownRendererWriteTests.cs
git commit -m "feat(export): markdown cadence continuation paragraphs via TimestampIntervalMs"
```

---

### Task 3: "Extra timestamp every 15 seconds" dialog toggle

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\ExportDialogViewModel.cs` (observable fields at lines 31-34; options build at lines 67-69; `ShowOptionToggles` doc comment at lines 37-39)
- Modify: `src\LocalScribe.App\ExportDialog.xaml` (toggle panel at lines 18-23)
- Test: `tests\LocalScribe.App.Tests\ExportDialogViewModelTests.cs` (add a seeding helper + 3 tests; existing 5 tests unchanged)

**Interfaces:**
- Consumes: `DocxOptions.TimestampIntervalMs` (Task 2); `TranscriptStore(string path)` / `TranscriptStore.AppendAsync(TranscriptLine, CancellationToken)` (`LocalScribe.Core.Storage`); `TranscriptLine.Segment(int seq, TranscriptSource source, long startMs, long endMs, string text, string speakerLabel, ...)` (`src\LocalScribe.Core\Model\TranscriptLine.cs:22-30`); `StoragePaths.TranscriptJsonl(string id)` (`StoragePaths.cs:18`).
- Produces: `ExportDialogViewModel.ExtraTimestamps` (`bool`, `[ObservableProperty]`, default `false`); private `const int CadenceIntervalMs = 15000`. Not persisted anywhere (matches the two existing toggles — the only persisted export state stays `WindowStateStore.LastExportDir`).

**Steps:**

- [ ] 1. Add the failing tests to `tests\LocalScribe.App.Tests\ExportDialogViewModelTests.cs`. The file already has `using LocalScribe.Core.Model;` and `using LocalScribe.Core.Storage;`. Add inside the class:

```csharp
    /// <summary>One same-speaker Local run with 400ms gaps (SectionGrouper merges it into a
    /// single row); the fifth segment starts 19.4s after the first, past the fixed 15s cadence.</summary>
    private static async Task SeedLongTurnAsync(StoragePaths paths)
    {
        long[][] times = [[0, 4000], [4400, 9000], [9400, 14000], [14400, 19000], [19400, 24000]];
        string[] words = ["one", "two", "three", "four", "five"];
        var store = new TranscriptStore(paths.TranscriptJsonl("s1"));
        for (int i = 0; i < words.Length; i++)
            await store.AppendAsync(TranscriptLine.Segment(i, TranscriptSource.Local,
                times[i][0], times[i][1], words[i], "Me"), default);
    }

    [Fact]
    public async Task Extra_timestamps_default_off_and_produce_no_continuation_stamps()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        string dest = Path.Combine(_root, "plain.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        Assert.False(vm.ExtraTimestamps);                                  // off by default
        await vm.ExportCommand.ExecuteAsync(null);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains(":** one two three four five\n", md);              // one unbroken paragraph
        Assert.DoesNotContain("**[00:19]**", md);
        Assert.Empty(rep.Errors);
    }

    [Fact]
    public async Task Extra_timestamps_add_continuation_paragraphs_to_the_export()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        string dest = Path.Combine(_root, "cadence.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExtraTimestamps = true };

        await vm.ExportCommand.ExecuteAsync(null);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains(":** one two three four\n", md);                   // chunk 0 keeps the label
        Assert.Contains("\n**[00:19]** five\n", md);                       // stamp-only continuation
        Assert.Empty(rep.Errors);
    }

    [Fact]
    public async Task Unchecking_include_timestamps_forces_the_cadence_off()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        string dest = Path.Combine(_root, "nostamps.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExtraTimestamps = true, IncludeTimestamps = false };

        await vm.ExportCommand.ExecuteAsync(null);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains("one two three four five\n", md);                  // still one paragraph
        Assert.DoesNotContain("[00:", md);                                 // no stamps anywhere
        Assert.Empty(rep.Errors);
    }
```

- [ ] 2. Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ExportDialogViewModelTests"` — expect BUILD failure: `error CS0117: 'ExportDialogViewModel' does not contain a definition for 'ExtraTimestamps'` (initializer) and `error CS1061` on `vm.ExtraTimestamps`.

- [ ] 3. Implement in `src\LocalScribe.App\ViewModels\ExportDialogViewModel.cs`. Replace the observable-field block (lines 31-34) with:

```csharp
    // Fixed 15s cadence (design 2026-08-02 item 5): no interval knob until someone needs one.
    private const int CadenceIntervalMs = 15000;

    [ObservableProperty] private ExportFormat _format = ExportFormat.Zip;
    [ObservableProperty] private bool _includeTimestamps = true;
    [ObservableProperty] private bool _includeMarkers = true;
    [ObservableProperty] private bool _extraTimestamps;
    [ObservableProperty] private bool _isBusy;
```

Update the `ShowOptionToggles` doc comment (lines 37-39): change `The dialog's IncludeTimestamps/IncludeMarkers checkboxes apply to BOTH textual` to `The dialog's IncludeTimestamps/IncludeMarkers/ExtraTimestamps checkboxes apply to BOTH textual`. Then replace the options build (lines 67-69) with:

```csharp
            // One options build for both textual formats - the checkboxes mean the same thing.
            // The cadence rides IncludeTimestamps: unchecking timestamps forces the interval off
            // even while the (disabled) cadence checkbox is still ticked.
            var options = new DocxOptions
            {
                IncludeTimestamps = IncludeTimestamps, IncludeMarkers = IncludeMarkers,
                TimestampIntervalMs = IncludeTimestamps && ExtraTimestamps ? CadenceIntervalMs : 0,
            };
```

- [ ] 4. Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ExportDialogViewModelTests"` — expect PASS, 8 tests.

- [ ] 5. Add the checkbox to `src\LocalScribe.App\ExportDialog.xaml`. Replace the panel (lines 18-23) with:

```xml
        <!-- The toggles apply to BOTH textual formats (design 2026-07-18 section 3 +
             2026-08-02 item 5): docx AND markdown; hidden for zip, which archives the session
             folder as-is. The cadence checkbox is subordinate to Include timestamps (plain bool
             IsEnabled binding - no inverting converter needed, per house rule). -->
        <StackPanel Margin="16,8,0,0" Visibility="{Binding ShowOptionToggles, Converter={StaticResource BoolToVis}}">
            <CheckBox Content="Include timestamps" IsChecked="{Binding IncludeTimestamps}" Margin="0,2" />
            <CheckBox Content="Extra timestamp every 15 seconds" IsChecked="{Binding ExtraTimestamps}"
                      IsEnabled="{Binding IncludeTimestamps}" Margin="16,2,0,2" />
            <CheckBox Content="Include system markers" IsChecked="{Binding IncludeMarkers}" Margin="0,2" />
        </StackPanel>
```

- [ ] 6. Re-run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ExportDialogViewModelTests"` — expect PASS, 8 tests (XAML compiles into the App assembly the tests reference).

- [ ] 7. Commit:
```
git add src\LocalScribe.App\ViewModels\ExportDialogViewModel.cs src\LocalScribe.App\ExportDialog.xaml tests\LocalScribe.App.Tests\ExportDialogViewModelTests.cs
git commit -m "feat(export): extra-timestamp-every-15s toggle in the export dialog"
```

---

### Task 4: Courtroom docx geometry — `TranscriptTurn` style, hanging indent, auto text column, page margins

**Files:**
- Modify: `src\LocalScribe.Core\Projection\DocxRenderer.cs` (full rewrite of `Write` at lines 38-86 and helpers at lines 88-95; class doc comment at lines 19-24; new consts beside lines 27-29)
- Test: `tests\LocalScribe.Core.Tests\DocxRendererTests.cs` — REWRITTEN (the two `Body.InnerText` substring tests necessarily break: `TabChar` contributes no characters to `InnerText`, so `"[00:01] Sam: "` can never match again)

**Interfaces:**
- Consumes: `DocxOptions` incl. `TimestampIntervalMs` (Task 2, ignored by docx until Task 7); `TimestampFormat.Stamp` (`TimestampFormat.cs:9`); DocumentFormat.OpenXml 3.5.1 types — verified property types: `Indentation.Left/.Hanging` are `StringValue`; `TabStop.Position` is `Int32Value`; `PageMargin.Top/.Bottom` are `Int32Value` while `.Left/.Right/.Header/.Footer/.Gutter` are `UInt32Value`.
- Produces (private, relied on by Tasks 5-7): `string TurnLabel(DisplayRow row, DocxOptions options, string timestampsMode, DateTimeOffset startedAtLocal)`; `Paragraph TurnParagraph(string label, string text)`; `Paragraph MarkerLine(string text, int textCol)`; `int TextColumnTwips(IReadOnlyList<DisplayRow> rows, DocxOptions options, string timestampsMode, DateTimeOffset startedAtLocal)`; `void AddStyles(MainDocumentPart mainPart, int textCol)`; consts `MarginTwips = 1440`, `HeaderFooterTwips = 720`, `TwipsPerLabelChar = 120`, `LabelPadTwips = 240`, `MinTextColTwips = 2160`, `MaxTextColTwips = 4320`; style id `"TranscriptTurn"`; body size `FontSize Val "22"` (11pt half-points, default theme face kept).

**Steps:**

- [ ] 1. Rewrite `tests\LocalScribe.Core.Tests\DocxRendererTests.cs` in full (the new structural tests fail against the current flat renderer; the metadata test stays green by design — it pins what must NOT change):

```csharp
// tests/LocalScribe.Core.Tests/DocxRendererTests.cs
using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LocalScribe.Core.Projection;

public class DocxRendererTests
{
    private static readonly DateTimeOffset Started = new(2026, 6, 30, 14, 32, 0, TimeSpan.Zero);

    private static (TranscriptHeader H, SessionTextView V, DisplayRow[] R) Sample()
    {
        var h = new TranscriptHeader("Weekly Sync", "Teams", Started, 2220000, "small.en", "CUDA");
        var v = new SessionTextView("Weekly Sync", new[] { "Acme (2026-014)" },
            new[] { "Sam (Local)", "Bob (Remote)" }, Started, Started.AddMinutes(37), 2220000,
            "Teams", "", null);
        var r = new[]
        {
            new DisplayRow { StartMs = 1000, DisplayName = "Sam", Text = "Morning everyone." },
            new DisplayRow { IsMarker = true, StartMs = 30000, Text = "audio device changed" },
            new DisplayRow { StartMs = 38000, DisplayName = "Bob", Text = "Question on tokens." },
        };
        return (h, v, r);
    }

    private static byte[] Render(string mode, string footer, DocxPageSize size, DocxOptions opts)
    {
        var (h, v, r) = Sample();
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v, r, mode, footer, size, opts);
        return ms.ToArray();   // valid even after the document disposed/closed the stream
    }

    private static WordprocessingDocument Open(byte[] bytes)
        => WordprocessingDocument.Open(new MemoryStream(bytes), false);

    private static Style TurnStyle(WordprocessingDocument doc)
        => doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!.Elements<Style>()
            .Single(s => s.StyleId == "TranscriptTurn");

    [Fact]
    public void Renders_metadata_disclaimer_marker_footer_and_a4_pagesize()
    {
        byte[] bytes = Render("relative", "PRIVILEGED & CONFIDENTIAL", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;
        string text = main.Document!.Body!.InnerText;

        Assert.Contains("Weekly Sync", text);
        Assert.Contains("Participants: Sam (Local), Bob (Remote)", text);
        Assert.Contains("Matter(s): Acme (2026-014)", text);
        Assert.Contains(DocxRenderer.Disclaimer, text);
        Assert.Contains("Morning everyone.", text);
        Assert.Contains("[audio device changed]", text);

        Assert.Equal("PRIVILEGED & CONFIDENTIAL", main.FooterParts.Single().Footer!.InnerText);
        var pageSize = main.Document.Body!.GetFirstChild<SectionProperties>()!.GetFirstChild<PageSize>()!;
        Assert.Equal(11906u, pageSize.Width!.Value);          // A4 width in twips
    }

    [Fact]
    public void Turn_paragraphs_reference_the_turn_style_with_bold_label_tab_text_runs()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);

        // The named style carries the geometry so recipients can retune it in Word.
        var style = TurnStyle(doc);
        var ind = style.StyleParagraphProperties!.GetFirstChild<Indentation>()!;
        Assert.Equal("2160", ind.Left!.Value);      // short sample labels clamp to the 1.5" floor
        Assert.Equal("2160", ind.Hanging!.Value);   // hanging == left: wrapped lines align at the column
        var tab = style.StyleParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().Single();
        Assert.Equal(TabStopValues.Left, tab.Val!.Value);
        Assert.Equal(2160, tab.Position!.Value);

        var turn = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText.StartsWith("[00:01] Sam:", StringComparison.Ordinal));
        Assert.Equal("TranscriptTurn", turn.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        var runs = turn.Elements<Run>().ToList();
        Assert.Equal(3, runs.Count);
        Assert.NotNull(runs[0].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal("[00:01] Sam:", runs[0].InnerText);      // no trailing space - the tab separates
        Assert.NotNull(runs[1].GetFirstChild<TabChar>());
        Assert.Equal("Morning everyone.", runs[2].InnerText);
    }

    [Fact]
    public void Toggles_off_render_bold_name_only_labels_and_drop_markers_letter_pagesize()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.Letter,
            new DocxOptions { IncludeTimestamps = false, IncludeMarkers = false });
        using var doc = Open(bytes);
        var body = doc.MainDocumentPart!.Document!.Body!;

        Assert.DoesNotContain("[00:01]", body.InnerText);
        Assert.DoesNotContain("audio device changed", body.InnerText);
        var turn = body.Elements<Paragraph>()
            .Single(p => p.InnerText.StartsWith("Sam:", StringComparison.Ordinal));
        var runs = turn.Elements<Run>().ToList();
        Assert.NotNull(runs[0].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal("Sam:", runs[0].InnerText);              // stamp-less label, same geometry
        Assert.NotNull(runs[1].GetFirstChild<TabChar>());
        Assert.Equal("2160", TurnStyle(doc).StyleParagraphProperties!
            .GetFirstChild<Indentation>()!.Left!.Value);      // name-only labels still get the floor
        Assert.Equal(12240u, body.GetFirstChild<SectionProperties>()!
            .GetFirstChild<PageSize>()!.Width!.Value);        // Letter width in twips
    }

    [Fact]
    public void Text_column_scales_with_the_longest_label_and_clamps_at_three_inches()
    {
        var (h, v, _) = Sample();

        // "[00:01] Barrister Wentworth:" = 28 chars -> 28*120 + 240 = 3600 twips (2.5", unclamped).
        var mid = new[] { new DisplayRow
        { StartMs = 1000, DisplayName = "Barrister Wentworth", Text = "Yes." } };
        using var ms1 = new MemoryStream();
        DocxRenderer.Write(ms1, h, v, mid, "relative", "", DocxPageSize.A4, new DocxOptions());
        using var doc1 = Open(ms1.ToArray());
        Assert.Equal("3600",
            TurnStyle(doc1).StyleParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);

        // 53-char label -> 6600 twips, clamped to the 3.0" ceiling (4320).
        var longRow = new[] { new DisplayRow { StartMs = 1000,
            DisplayName = "Ms. Alexandra Fitzgerald-Whitmore de la Vega", Text = "Present." } };
        using var ms2 = new MemoryStream();
        DocxRenderer.Write(ms2, h, v, longRow, "relative", "", DocxPageSize.A4, new DocxOptions());
        using var doc2 = Open(ms2.ToArray());
        Assert.Equal("4320",
            TurnStyle(doc2).StyleParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);
    }

    [Fact]
    public void Markers_render_italic_in_the_text_column()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var marker = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText == "[audio device changed]");
        Assert.Equal("2160", marker.ParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);
        Assert.NotNull(marker.Elements<Run>().Single().RunProperties?.GetFirstChild<Italic>());
    }

    [Fact]
    public void Page_margins_are_explicit_one_inch_with_half_inch_header_footer()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var margin = doc.MainDocumentPart!.Document!.Body!
            .GetFirstChild<SectionProperties>()!.GetFirstChild<PageMargin>()!;
        Assert.Equal(1440, margin.Top!.Value);
        Assert.Equal(1440u, margin.Right!.Value);
        Assert.Equal(1440, margin.Bottom!.Value);
        Assert.Equal(1440u, margin.Left!.Value);
        Assert.Equal(720u, margin.Header!.Value);
        Assert.Equal(720u, margin.Footer!.Value);
    }

    [Fact]
    public void Doc_defaults_pin_the_body_size_and_keep_the_default_face()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var rPr = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!
            .DocDefaults!.RunPropertiesDefault!.RunPropertiesBaseStyle!;
        Assert.Equal("22", rPr.FontSize!.Val!.Value);          // 11pt in half-points
        Assert.Null(rPr.RunFonts);                             // default theme face deliberately kept
    }

    [Fact]
    public void PageSizeForRegion_maps_US_CA_to_letter_else_A4()
    {
        Assert.Equal(DocxPageSize.Letter, DocxRenderer.PageSizeForRegion(new RegionInfo("US")));
        Assert.Equal(DocxPageSize.Letter, DocxRenderer.PageSizeForRegion(new RegionInfo("CA")));
        Assert.Equal(DocxPageSize.A4, DocxRenderer.PageSizeForRegion(new RegionInfo("GB")));
        Assert.Equal(DocxPageSize.A4, DocxRenderer.PageSizeForRegion(new RegionInfo("SG")));
    }
}
```

- [ ] 2. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"` — expect FAIL: the five structural tests throw `System.NullReferenceException` (no `StyleDefinitionsPart`, no `ParagraphProperties`, no `PageMargin` exist on the flat renderer); `Renders_metadata...` and `PageSizeForRegion...` pass.

- [ ] 3. Rewrite `src\LocalScribe.Core\Projection\DocxRenderer.cs` in full (the `DocxOptions` record from Task 2 is unchanged; the footer block stays as today until Task 6):

```csharp
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
namespace LocalScribe.Core.Projection;

/// <summary>The user-facing export toggles (design 3.3 + 2026-08-02 item 5). House style mirrors
/// PhantomBleedOptions: sealed record + { get; init; } with inline defaults. Format-neutral and
/// shared deliberately by the .docx and .md export renderers.</summary>
public sealed record DocxOptions
{
    public bool IncludeTimestamps { get; init; } = true;
    public bool IncludeMarkers { get; init; } = true;
    /// <summary>Extra mid-turn stamp cadence (design 2026-08-02 item 5): a stamp-only continuation
    /// paragraph starts at the first segment boundary at/after this many ms since the last shown
    /// stamp. 0 (default) = off. Renderers force it off when IncludeTimestamps is false.</summary>
    public int TimestampIntervalMs { get; init; } = 0;
}

/// <summary>Page size for an exported .docx. Chosen from the machine locale AT the export call site
/// (RegionInfo) and passed in - the ONLY machine-locale dependence, scoped to page size (spec 11.2).</summary>
public enum DocxPageSize { A4, Letter }

/// <summary>Serializes a .docx transcript projection (spec 11.2, design 2026-08-02 item 6) into a
/// stream, in the courtroom layout: each turn is one paragraph in the named TranscriptTurn style -
/// bold "[00:00] Name:" label, tab, text - with a hanging indent so wrapped lines align at a text
/// column auto-sized from the longest label. Same render model as MarkdownRenderer (TranscriptHeader
/// + rows) plus the SessionTextView metadata block (user-curated participants, NEVER speakers.json
/// clusters) and a NON-OPTIONAL machine-generated-accuracy disclaimer. All TEXT is invariant-culture;
/// markers render italic in the text column. Rows arrive pre-resolved from TranscriptProjection.Build
/// - this never re-runs vocabulary/edits/dedup/NameResolver.</summary>
public static class DocxRenderer
{
    // Word page dimensions are in twips (1/1440 inch). A4 = 210x297mm; Letter = 8.5x11in.
    private const int A4WidthTwips = 11906, A4HeightTwips = 16838;
    private const int LetterWidthTwips = 12240, LetterHeightTwips = 15840;
    // Courtroom page geometry (design 2026-08-02 item 6): 1" margins, 0.5" header/footer.
    private const int MarginTwips = 1440, HeaderFooterTwips = 720;
    // Text column sizing: estimated bold-11pt character advance plus label padding, clamped to
    // [1.5", 3.0"] so short labels still get a real gutter and marathon names cannot eat the page
    // (overlong labels overrun one line gracefully; the hanging indent keeps wrapped lines aligned).
    private const int TwipsPerLabelChar = 120, LabelPadTwips = 240;
    private const int MinTextColTwips = 2160, MaxTextColTwips = 4320;

    public const string Disclaimer =
        "This transcript was generated by automated speech recognition and may contain errors. "
        + "It is not a certified record.";

    public static DocxPageSize PageSizeForRegion(RegionInfo region)
        => region.TwoLetterISORegionName is "US" or "CA" ? DocxPageSize.Letter : DocxPageSize.A4;

    public static void Write(Stream output, TranscriptHeader header, SessionTextView meta,
        IReadOnlyList<DisplayRow> rows, string timestampsMode, string footerText,
        DocxPageSize pageSize, DocxOptions options)
    {
        using var doc = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = mainPart.Document.AppendChild(new Body());

        int textCol = TextColumnTwips(rows, options, timestampsMode, header.StartedAtLocal);
        AddStyles(mainPart, textCol);

        // Metadata header block.
        body.AppendChild(Heading(meta.Title));
        body.AppendChild(MetaLine("App", header.App));
        body.AppendChild(MetaLine("Date",
            header.StartedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        body.AppendChild(MetaLine("Matter(s)",
            meta.Matters.Count == 0 ? "(none)" : string.Join(", ", meta.Matters)));
        body.AppendChild(MetaLine("Participants",
            meta.Participants.Count == 0 ? "(none)" : string.Join(", ", meta.Participants)));
        body.AppendChild(MetaLine("Medium", meta.Medium));
        if (!string.IsNullOrEmpty(meta.Description)) body.AppendChild(MetaLine("Description", meta.Description));
        body.AppendChild(ItalicLine(Disclaimer));
        body.AppendChild(new Paragraph());   // spacer before the turns

        foreach (var row in rows)
        {
            if (row.IsMarker)
            {
                if (options.IncludeMarkers) body.AppendChild(MarkerLine(row.Text, textCol));
                continue;
            }
            body.AppendChild(TurnParagraph(
                TurnLabel(row, options, timestampsMode, header.StartedAtLocal), row.Text));
        }

        // Per-page footer + locale page size in section properties (MUST be the last child of body).
        var footerPart = mainPart.AddNewPart<FooterPart>();
        footerPart.Footer = new Footer(new Paragraph(new Run(MakeText(footerText))));
        string footerId = mainPart.GetIdOfPart(footerPart);
        (int w, int h) = pageSize == DocxPageSize.Letter
            ? (LetterWidthTwips, LetterHeightTwips) : (A4WidthTwips, A4HeightTwips);
        body.AppendChild(new SectionProperties(
            new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
            new PageSize { Width = (UInt32Value)(uint)w, Height = (UInt32Value)(uint)h },
            // Explicit margins make the tab geometry predictable (sectPr schema order: pgSz, pgMar).
            new PageMargin
            {
                Top = MarginTwips, Right = (uint)MarginTwips,
                Bottom = MarginTwips, Left = (uint)MarginTwips,
                Header = (uint)HeaderFooterTwips, Footer = (uint)HeaderFooterTwips, Gutter = 0U,
            }));
    }

    /// <summary>O(n) pre-pass (design 2026-08-02 item 6): size the text column off the longest turn
    /// label. Continuation stamps ("[1:02:03]") are always narrower than the 1.5" floor, so only
    /// full labels need measuring.</summary>
    private static int TextColumnTwips(IReadOnlyList<DisplayRow> rows, DocxOptions options,
        string timestampsMode, DateTimeOffset startedAtLocal)
    {
        int longest = 0;
        foreach (var row in rows)
            if (!row.IsMarker)
                longest = Math.Max(longest,
                    TurnLabel(row, options, timestampsMode, startedAtLocal).Length);
        return Math.Clamp(longest * TwipsPerLabelChar + LabelPadTwips, MinTextColTwips, MaxTextColTwips);
    }

    private static string TurnLabel(DisplayRow row, DocxOptions options, string timestampsMode,
        DateTimeOffset startedAtLocal)
        => options.IncludeTimestamps
            ? "[" + TimestampFormat.Stamp(row.StartMs, timestampsMode, startedAtLocal) + "] "
                + row.DisplayName + ":"
            : row.DisplayName + ":";

    /// <summary>Bold label -> tab -> text. The TranscriptTurn style carries the hanging indent and
    /// tab stop, so a recipient can retune the whole document by editing one style in Word.</summary>
    private static Paragraph TurnParagraph(string label, string text)
        => new(new ParagraphProperties(new ParagraphStyleId { Val = "TranscriptTurn" }),
            new Run(new RunProperties(new Bold()), MakeText(label)),
            new Run(new TabChar()),
            new Run(MakeText(text)));

    private static Paragraph MarkerLine(string text, int textCol)
        => new(new ParagraphProperties(
                new Indentation { Left = textCol.ToString(CultureInfo.InvariantCulture) }),
            new Run(new RunProperties(new Italic()), MakeText("[" + text + "]")));

    private static void AddStyles(MainDocumentPart mainPart, int textCol)
    {
        string col = textCol.ToString(CultureInfo.InvariantCulture);
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            // Default theme face kept deliberately (the reference doc uses Word's default face);
            // the size is pinned at 11pt so the column math holds across machines.
            new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new FontSize { Val = "22" }, new FontSizeComplexScript { Val = "22" }))),
            new Style(
                new StyleName { Val = "Transcript Turn" },
                new StyleParagraphProperties(
                    new Tabs(new TabStop { Val = TabStopValues.Left, Position = textCol }),
                    new Indentation { Left = col, Hanging = col }))
            { Type = StyleValues.Paragraph, StyleId = "TranscriptTurn" });
    }

    private static Text MakeText(string s) => new(s) { Space = SpaceProcessingModeValues.Preserve };
    private static Paragraph Heading(string title)
        => new(new Run(new RunProperties(new Bold(), new FontSize { Val = "32" }), MakeText(title)));
    private static Paragraph MetaLine(string label, string value)
        => new(new Run(new RunProperties(new Bold()), MakeText(label + ": ")), new Run(MakeText(value)));
    private static Paragraph ItalicLine(string s)
        => new(new Run(new RunProperties(new Italic()), MakeText(s)));
}
```

- [ ] 4. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"` — expect PASS, 8 tests.

- [ ] 5. Prove the App-side docx tests are NOT broken yet (footer untouched until Task 6): `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceService"` — expect PASS (`ExportDocx_writes_a_valid_docx_with_footer_from_settings` asserts only `InnerText` containment of the title and footer equality, both still true).

- [ ] 6. Commit:
```
git add src\LocalScribe.Core\Projection\DocxRenderer.cs tests\LocalScribe.Core.Tests\DocxRendererTests.cs
git commit -m "feat(export): courtroom docx layout with TranscriptTurn style and auto text column"
```

---

### Task 5: Docx line numbering (CountBy=5, restart per page) + header suppression + disclaimer rule

**Files:**
- Modify: `src\LocalScribe.Core\Projection\DocxRenderer.cs` (header helpers `Heading`/`MetaLine`/`ItalicLine` and the spacer, plus the `SectionProperties` — all as left by Task 4)
- Test: `tests\LocalScribe.Core.Tests\DocxRendererTests.cs` (add 2 tests)

**Interfaces:**
- Consumes: Task 4's `Write` structure and helpers.
- Produces: `LineNumberType { CountBy = 5, Restart = LineNumberRestartValues.NewPage }` on the section (`CountBy` is `Int16Value` — the constant `5` converts implicitly); `SuppressLineNumbers` pPr on every metadata paragraph (title through disclaimer + spacer) and ONLY those; new private helper `Paragraph DisclaimerLine()` replacing the last `ItalicLine` use (a 0.5pt `BottomBorder`, `Size = 4U` eighths of a point). Turn, continuation, and marker paragraphs never suppress — they are numbered content.

**Steps:**

- [ ] 1. Add two failing tests to `tests\LocalScribe.Core.Tests\DocxRendererTests.cs`:

```csharp
    [Fact]
    public void Line_numbering_counts_by_five_per_page_and_skips_the_header_block_only()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var body = doc.MainDocumentPart!.Document!.Body!;

        var ln = body.GetFirstChild<SectionProperties>()!.GetFirstChild<LineNumberType>()!;
        Assert.Equal(5, (int)ln.CountBy!.Value);
        Assert.Equal(LineNumberRestartValues.NewPage, ln.Restart!.Value);

        // Every paragraph BEFORE the first turn (title..disclaimer + spacer) suppresses numbering;
        // turns and markers never do - they are numbered transcript content.
        var paragraphs = body.Elements<Paragraph>().ToList();
        int firstTurn = paragraphs.FindIndex(p =>
            p.InnerText.StartsWith("[00:01] Sam:", StringComparison.Ordinal));
        Assert.True(firstTurn > 0);
        Assert.All(paragraphs.Take(firstTurn),
            p => Assert.NotNull(p.ParagraphProperties?.GetFirstChild<SuppressLineNumbers>()));
        Assert.All(paragraphs.Skip(firstTurn),
            p => Assert.Null(p.ParagraphProperties?.GetFirstChild<SuppressLineNumbers>()));
    }

    [Fact]
    public void Disclaimer_paragraph_carries_the_thin_bottom_rule()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var disclaimer = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText == DocxRenderer.Disclaimer);
        var border = disclaimer.ParagraphProperties!.GetFirstChild<ParagraphBorders>()!
            .GetFirstChild<BottomBorder>()!;
        Assert.Equal(BorderValues.Single, border.Val!.Value);
        Assert.Equal(4u, border.Size!.Value);                  // eighths of a point -> 0.5pt rule
        Assert.NotNull(disclaimer.Elements<Run>().Single().RunProperties?.GetFirstChild<Italic>());
    }
```

- [ ] 2. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"` — expect 2 FAIL: `System.NullReferenceException` (`GetFirstChild<LineNumberType>()` and `GetFirstChild<ParagraphBorders>()` return null; the disclaimer paragraph has no `ParagraphProperties`).

- [ ] 3. Implement in `src\LocalScribe.Core\Projection\DocxRenderer.cs`:

(a) In `Write`, replace the disclaimer + spacer lines:
```csharp
        body.AppendChild(ItalicLine(Disclaimer));
        body.AppendChild(new Paragraph());   // spacer before the turns
```
with:
```csharp
        body.AppendChild(DisclaimerLine());
        // Spacer before the turns - suppressed like the rest of the header so line 1 is content.
        body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers())));
```

(b) In `Write`, extend the `SectionProperties` (append after `PageMargin`, keeping sectPr schema order pgSz -> pgMar -> lnNumType):
```csharp
        body.AppendChild(new SectionProperties(
            new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
            new PageSize { Width = (UInt32Value)(uint)w, Height = (UInt32Value)(uint)h },
            // Explicit margins make the tab geometry predictable (sectPr schema order: pgSz, pgMar).
            new PageMargin
            {
                Top = MarginTwips, Right = (uint)MarginTwips,
                Bottom = MarginTwips, Left = (uint)MarginTwips,
                Header = (uint)HeaderFooterTwips, Footer = (uint)HeaderFooterTwips, Gutter = 0U,
            },
            // Courtroom line numbers (design 2026-08-02 item 6): every 5th line, restart per page,
            // counting transcript content only (header paragraphs carry SuppressLineNumbers).
            new LineNumberType { CountBy = 5, Restart = LineNumberRestartValues.NewPage }));
```

(c) Replace the `Heading`/`MetaLine`/`ItalicLine` helpers (delete `ItalicLine` — after (a) nothing uses it):
```csharp
    private static Text MakeText(string s) => new(s) { Space = SpaceProcessingModeValues.Preserve };
    private static Paragraph Heading(string title)
        => new(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Bold(), new FontSize { Val = "32" }), MakeText(title)));
    private static Paragraph MetaLine(string label, string value)
        => new(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Bold()), MakeText(label + ": ")), new Run(MakeText(value)));
    /// <summary>Italic disclaimer closed by a thin 0.5pt rule (design 2026-08-02 item 6) that
    /// separates the unnumbered metadata block from the numbered transcript body.</summary>
    private static Paragraph DisclaimerLine()
        => new(new ParagraphProperties(
                new SuppressLineNumbers(),
                new ParagraphBorders(new BottomBorder
                { Val = BorderValues.Single, Size = 4U, Space = 4U, Color = "auto" })),
            new Run(new RunProperties(new Italic()), MakeText(Disclaimer)));
```

- [ ] 4. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"` — expect PASS, 10 tests.

- [ ] 5. Commit:
```
git add src\LocalScribe.Core\Projection\DocxRenderer.cs tests\LocalScribe.Core.Tests\DocxRendererTests.cs
git commit -m "feat(export): docx per-page line numbering counts transcript content only"
```

---

### Task 6: Docx footer — versioned text left, PAGE field at a right tab; update App-side asserts

**Files:**
- Modify: `src\LocalScribe.Core\Projection\DocxRenderer.cs` (the footer/sectPr block in `Write` as left by Task 5)
- Modify: `tests\LocalScribe.Core.Tests\DocxRendererTests.cs` (1 assert edit + 2 new tests)
- Modify: `tests\LocalScribe.App.Tests\MaintenanceServiceTests.cs` (`ExportDocx_writes_a_valid_docx_with_footer_from_settings`, currently lines 284-298)
- Modify: `tests\LocalScribe.App.Tests\MaintenanceServiceVersionsTests.cs` (`ExportDocx_footer_names_the_active_version_and_model`, currently lines 186-207)

**Interfaces:**
- Consumes: Task 4 consts `MarginTwips`, `MakeText`; `FieldChar.FieldCharType` (`EnumValue<FieldCharValues>`), `FieldCode(string)` (leaf text element with `Space`).
- Produces: footer paragraph shape relied on by no later task: pPr `Tabs(TabStop Right at usableWidth)`, runs `[footerText][tab][fldChar begin][instrText " PAGE "][fldChar separate]["1"][fldChar end]`; `usableWidth = pageWidth - 2*MarginTwips` (A4: 9026, Letter: 9360). NOTE: the field's `" PAGE "` instruction text and the cached `"1"` now contribute to `Footer.InnerText` — that is WHY the three exact-equality footer asserts below must change.

**Steps:**

- [ ] 1. Add two failing tests to `tests\LocalScribe.Core.Tests\DocxRendererTests.cs`:

```csharp
    [Fact]
    public void Footer_pairs_the_text_with_a_page_field_at_a_right_tab_on_the_usable_width()
    {
        byte[] bytes = Render("relative", "PRIVILEGED & CONFIDENTIAL", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var footer = doc.MainDocumentPart!.FooterParts.Single().Footer!;
        var par = footer.Elements<Paragraph>().Single();

        var tab = par.ParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().Single();
        Assert.Equal(TabStopValues.Right, tab.Val!.Value);
        Assert.Equal(9026, tab.Position!.Value);               // A4 11906 - 2x1440 margins

        Assert.StartsWith("PRIVILEGED & CONFIDENTIAL", footer.InnerText);
        Assert.Equal(" PAGE ", par.Descendants<FieldCode>().Single().Text);
        var fieldChars = par.Descendants<FieldChar>().ToList();
        Assert.Equal(3, fieldChars.Count);                     // begin / separate / end
        Assert.Equal(FieldCharValues.Begin, fieldChars[0].FieldCharType!.Value);
        Assert.Equal(FieldCharValues.Separate, fieldChars[1].FieldCharType!.Value);
        Assert.Equal(FieldCharValues.End, fieldChars[2].FieldCharType!.Value);
    }

    [Fact]
    public void Footer_right_tab_uses_the_letter_usable_width_on_letter_pages()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.Letter, new DocxOptions());
        using var doc = Open(bytes);
        var tab = doc.MainDocumentPart!.FooterParts.Single().Footer!.Elements<Paragraph>().Single()
            .ParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().Single();
        Assert.Equal(9360, tab.Position!.Value);               // Letter 12240 - 2x1440 margins
    }
```

- [ ] 2. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"` — expect 2 FAIL: `System.NullReferenceException` (the footer paragraph has no `ParagraphProperties`) and/or `System.InvalidOperationException : Sequence contains no elements` (no `FieldCode`).

- [ ] 3. Implement in `src\LocalScribe.Core\Projection\DocxRenderer.cs`. Replace the footer/sectPr block (from `// Per-page footer ...` through the end of the `SectionProperties` append) with — note `(w, h)` now computes BEFORE the footer because the right-tab position depends on the page width:

```csharp
        // Per-page footer + locale page size in section properties (sectPr MUST be the last child
        // of body). Footer = versioned text at the left margin + a PAGE field at a right tab on
        // the usable width (design 2026-08-02 item 6); the cached "1" is the placeholder result
        // Word replaces when it paginates. Field instruction text is invariant by construction.
        (int w, int h) = pageSize == DocxPageSize.Letter
            ? (LetterWidthTwips, LetterHeightTwips) : (A4WidthTwips, A4HeightTwips);
        int usableWidth = w - 2 * MarginTwips;
        var footerPart = mainPart.AddNewPart<FooterPart>();
        footerPart.Footer = new Footer(new Paragraph(
            new ParagraphProperties(
                new Tabs(new TabStop { Val = TabStopValues.Right, Position = usableWidth })),
            new Run(MakeText(footerText)),
            new Run(new TabChar()),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(MakeText("1")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
        string footerId = mainPart.GetIdOfPart(footerPart);
        body.AppendChild(new SectionProperties(
            new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
            new PageSize { Width = (UInt32Value)(uint)w, Height = (UInt32Value)(uint)h },
            // Explicit margins make the tab geometry predictable (sectPr schema order: pgSz, pgMar).
            new PageMargin
            {
                Top = MarginTwips, Right = (uint)MarginTwips,
                Bottom = MarginTwips, Left = (uint)MarginTwips,
                Header = (uint)HeaderFooterTwips, Footer = (uint)HeaderFooterTwips, Gutter = 0U,
            },
            // Courtroom line numbers (design 2026-08-02 item 6): every 5th line, restart per page,
            // counting transcript content only (header paragraphs carry SuppressLineNumbers).
            new LineNumberType { CountBy = 5, Restart = LineNumberRestartValues.NewPage }));
```

- [ ] 4. Fix the now-broken exact-equality footer assert in `tests\LocalScribe.Core.Tests\DocxRendererTests.cs` (`Renders_metadata_disclaimer_marker_footer_and_a4_pagesize`) — the PAGE field appends `" PAGE 1"` to `InnerText`. Replace:
```csharp
        Assert.Equal("PRIVILEGED & CONFIDENTIAL", main.FooterParts.Single().Footer!.InnerText);
```
with:
```csharp
        Assert.StartsWith("PRIVILEGED & CONFIDENTIAL", main.FooterParts.Single().Footer!.InnerText);
```

- [ ] 5. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"` — expect PASS, 12 tests. Then run `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceService"` — expect exactly 2 FAIL with `Assert.Equal() Failure` on footer `InnerText` (`ExportDocx_writes_a_valid_docx_with_footer_from_settings` and `ExportDocx_footer_names_the_active_version_and_model`); every other test passes.

- [ ] 6. Update `tests\LocalScribe.App.Tests\MaintenanceServiceTests.cs` — replace (currently lines 296-297):
```csharp
        Assert.Equal("PRIVILEGED & CONFIDENTIAL",
            doc.MainDocumentPart.FooterParts.Single().Footer!.InnerText);   // FakeSettingsService default
```
with:
```csharp
        var footer = doc.MainDocumentPart.FooterParts.Single().Footer!;
        Assert.StartsWith("PRIVILEGED & CONFIDENTIAL", footer.InnerText);   // FakeSettingsService default
        Assert.Single(footer.Descendants<DocumentFormat.OpenXml.Wordprocessing.FieldCode>());   // PAGE field
```

- [ ] 7. Update `tests\LocalScribe.App.Tests\MaintenanceServiceVersionsTests.cs` — in `ExportDocx_footer_names_the_active_version_and_model` the three `Assert.Contains` lines (footer contains "PRIVILEGED"/"v2"/"tiny.en") still pass; replace only the v1 exact-equality tail (currently lines 201-206):
```csharp
        // v1-active session: the footer is EXACTLY the configured text (no version note).
        await svc.SetActiveVersionAsync(id, "v1", CancellationToken.None);
        string dest1 = Path.Combine(_root, "out-v1.docx");
        await svc.ExportDocxAsync(id, dest1, new DocxOptions(), CancellationToken.None);
        using var doc1 = WordprocessingDocument.Open(dest1, false);
        Assert.Equal("PRIVILEGED", doc1.MainDocumentPart!.FooterParts.Single().Footer!.InnerText);
```
with:
```csharp
        // v1-active session: the footer TEXT is exactly the configured text (no version note).
        // The PAGE field appends " PAGE 1" to InnerText, so pin the prefix + the note's absence.
        await svc.SetActiveVersionAsync(id, "v1", CancellationToken.None);
        string dest1 = Path.Combine(_root, "out-v1.docx");
        await svc.ExportDocxAsync(id, dest1, new DocxOptions(), CancellationToken.None);
        using var doc1 = WordprocessingDocument.Open(dest1, false);
        string footer1 = doc1.MainDocumentPart!.FooterParts.Single().Footer!.InnerText;
        Assert.StartsWith("PRIVILEGED", footer1);
        Assert.DoesNotContain("Transcript version", footer1);
```

- [ ] 8. Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceService"` — expect PASS (both classes).

- [ ] 9. Commit:
```
git add src\LocalScribe.Core\Projection\DocxRenderer.cs tests\LocalScribe.Core.Tests\DocxRendererTests.cs tests\LocalScribe.App.Tests\MaintenanceServiceTests.cs tests\LocalScribe.App.Tests\MaintenanceServiceVersionsTests.cs
git commit -m "feat(export): docx footer PAGE field at a right tab on the usable width"
```

---

### Task 7: Docx cadence continuation paragraphs

**Files:**
- Modify: `src\LocalScribe.Core\Projection\DocxRenderer.cs` (the turn loop in `Write` as left by Task 4)
- Test: `tests\LocalScribe.Core.Tests\DocxRendererTests.cs` (add 1 test + 1 using)

**Interfaces:**
- Consumes: `TimestampCadence.Chunk` (Task 1), `TurnParagraph`/`TurnLabel` (Task 4), `DocxOptions.TimestampIntervalMs` (Task 2).
- Produces: docx continuation dialect — a `TranscriptTurn` paragraph whose bold run is `"[00:19]"` (stamp only, no name), then tab, then chunk text. Continuations never carry `SuppressLineNumbers` (they are numbered content) and never affect `TextColumnTwips`.

**Steps:**

- [ ] 1. Add `using LocalScribe.Core.Model;` to the top of `tests\LocalScribe.Core.Tests\DocxRendererTests.cs` (needed for `TranscriptSource`), then add the failing test:

```csharp
    [Fact]
    public void Cadence_continuations_render_stamp_only_paragraphs_in_the_turn_style()
    {
        var (h, v, _) = Sample();
        var rows = new[] { new DisplayRow
        {
            StartMs = 0, EndMs = 24000, DisplayName = "Sam",
            Text = "one two three four five",
            Segments = new[]
            {
                new RowSegment(0, TranscriptSource.Local, 0, 4000, "one", "one", false, false),
                new RowSegment(1, TranscriptSource.Local, 4400, 9000, "two", "two", false, false),
                new RowSegment(2, TranscriptSource.Local, 9400, 14000, "three", "three", false, false),
                new RowSegment(3, TranscriptSource.Local, 14400, 19000, "four", "four", false, false),
                new RowSegment(4, TranscriptSource.Local, 19400, 24000, "five", "five", false, false),
            },
        } };
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v, rows, "relative", "", DocxPageSize.A4,
            new DocxOptions { TimestampIntervalMs = 15000 });
        using var doc = Open(ms.ToArray());
        var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToList();

        Assert.Single(paragraphs, p => p.InnerText == "[00:00] Sam:one two three four");
        var cont = paragraphs.Single(p => p.InnerText == "[00:19]five");
        Assert.Equal("TranscriptTurn", cont.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.Null(cont.ParagraphProperties!.GetFirstChild<SuppressLineNumbers>());   // counts as content
        var runs = cont.Elements<Run>().ToList();
        Assert.Equal(3, runs.Count);
        Assert.NotNull(runs[0].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal("[00:19]", runs[0].InnerText);            // stamp only - the name is not repeated
        Assert.NotNull(runs[1].GetFirstChild<TabChar>());
        Assert.Equal("five", runs[2].InnerText);
    }
```

- [ ] 2. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"` — expect 1 FAIL: `System.InvalidOperationException : Sequence contains no matching element` (the whole row still renders as one paragraph).

- [ ] 3. Implement — replace the turn loop in `Write`:
```csharp
        foreach (var row in rows)
        {
            if (row.IsMarker)
            {
                if (options.IncludeMarkers) body.AppendChild(MarkerLine(row.Text, textCol));
                continue;
            }
            body.AppendChild(TurnParagraph(
                TurnLabel(row, options, timestampsMode, header.StartedAtLocal), row.Text));
        }
```
with:
```csharp
        foreach (var row in rows)
        {
            if (row.IsMarker)
            {
                if (options.IncludeMarkers) body.AppendChild(MarkerLine(row.Text, textCol));
                continue;
            }
            // Cadence chunking (design 2026-08-02 item 5): chunk 0 is the normal turn; later
            // chunks are stamp-only continuation paragraphs in the same geometry (no name).
            // Interval 0 (or timestamps off) yields one whole-row chunk - output unchanged.
            var chunks = TimestampCadence.Chunk(row,
                options.IncludeTimestamps ? options.TimestampIntervalMs : 0);
            body.AppendChild(TurnParagraph(
                TurnLabel(row, options, timestampsMode, header.StartedAtLocal), chunks[0].Text));
            for (int i = 1; i < chunks.Count; i++)
                body.AppendChild(TurnParagraph(
                    "[" + TimestampFormat.Stamp(chunks[i].StampMs, timestampsMode, header.StartedAtLocal) + "]",
                    chunks[i].Text));
        }
```

- [ ] 4. Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"` — expect PASS, 13 tests.

- [ ] 5. Commit:
```
git add src\LocalScribe.Core\Projection\DocxRenderer.cs tests\LocalScribe.Core.Tests\DocxRendererTests.cs
git commit -m "feat(export): docx cadence continuation paragraphs"
```

---

### Task 8: Docs — spec 11.2 amendment, stage-6 superseded note, smoke-runbook checkbox

**Files:**
- Modify: `docs\specs\localscribe-specs.md` (section 11.2, currently lines 1090-1113; the "Output" bullet is lines 1111-1113)
- Modify: `docs\plans\2026-07-07-stage-6-corrections-vocab-export-design.md` (insert after the `### 3.3 `.docx` transcript` heading, currently line 227)
- Modify: `docs\plans\2026-07-07-stage-6-smoke-runbook.md` (Part C, append after the X5 item ending at line 105)

**Interfaces:** none (documentation only). No test — verify by reading the diffs.

**Steps:**

- [ ] 1. In `docs\specs\localscribe-specs.md`, replace the final bullet of section 11.2:
```
- **Output:** Save-As to a user-chosen path, default filename `{title}.docx`, remember last
  directory. At most two toggles: timestamps on/off, markers on/off; honour
  `settings.timestamps`.
```
with:
```
- **Layout (2026-08-02, courtroom):** each turn is one paragraph in a named `TranscriptTurn`
  style — bold `[00:00] Name:` label, tab, spoken text — with a hanging indent and a left tab
  stop at a text column auto-sized from the longest label (clamped 1.5"-3.0"). Wrapped lines
  align at the text column; timestamps off renders a bold `Name:` label in the same geometry.
  Markers render italic in the text column; a thin 0.5pt rule closes the metadata block under
  the disclaimer. Line numbers count transcript content only (`lnNumType` count-by-5, restart
  per page; every header/metadata paragraph carries `suppressLineNumbers`). Footer = the
  configured text left + a `PAGE` field bottom-right at a right tab on the usable width.
  Explicit page margins: 1" all around, 0.5" header/footer. `DocDefaults` pins the body size
  (11pt) but keeps Word's default theme face.
- **Output:** Save-As to a user-chosen path, default filename `{title}.docx`, remember last
  directory. Three toggles: timestamps on/off, markers on/off, and "Extra timestamp every
  15 seconds" (enabled only while timestamps are on; fixed 15 s interval, never persisted;
  starts a stamp-only continuation paragraph — no repeated speaker name — at the first
  segment boundary at/after 15 s since the last shown stamp; applies to `.docx` AND `.md`,
  never to the `.zip`'s bundled save-time files); honour `settings.timestamps`.
```

- [ ] 2. In `docs\plans\2026-07-07-stage-6-corrections-vocab-export-design.md`, insert directly below the line `### 3.3 \`.docx\` transcript` (line 227) and its following blank line:
```
> **Superseded (2026-08-02):** the flat one-paragraph-per-turn layout described below was
> replaced by the courtroom layout — hanging-indent `TranscriptTurn` paragraphs with a tabbed
> label column, per-page count-by-5 line numbering (content only), a footer `PAGE` field,
> explicit page margins, a `StyleDefinitionsPart`, and an optional 15-second timestamp
> cadence. See `docs/superpowers/specs/2026-08-02-ux-round-design.md` items 5-6 and
> `docs/superpowers/plans/2026-08-02-export-cadence-courtroom-plan.md`.
```

- [ ] 3. In `docs\plans\2026-07-07-stage-6-smoke-runbook.md`, append to Part C after the X5 item (line 105) — this is the smoke coverage for the view-layer/Word-visual behavior no unit test here can reach:
```
- [ ] **X6 Courtroom docx + cadence (2026-08-02):** export a long finalized session as Word
  with all three toggles on ("Extra timestamp every 15 seconds" is enabled only while
  "Include timestamps" is checked; unchecking timestamps greys it out). Open in Word:
  bold `[00:00] Name:` labels sit left of an aligned hanging-indent text column (wrapped
  lines line up under the text, not the name); long same-speaker turns break into
  stamp-only continuation paragraphs about every 15s; markers are italic in the text
  column; a thin rule closes the metadata block under the disclaimer; line numbers appear
  every 5 lines restarting each page with the header/metadata unnumbered; the page number
  sits bottom-right on the same footer line as the PRIVILEGED text. Export the same
  session as Markdown with the cadence on - continuations render as `**[03:15]** text`.
```

- [ ] 4. Commit:
```
git add docs\specs\localscribe-specs.md docs\plans\2026-07-07-stage-6-corrections-vocab-export-design.md docs\plans\2026-07-07-stage-6-smoke-runbook.md
git commit -m "docs(export): spec 11.2 courtroom layout + cadence toggle; supersede stage-6 3.3"
```

---

### Task 9: Full-suite regression run

**Files:**
- Modify: none expected; fix-forward anything the full suites surface (any fix lands with its own failing-test-first cycle if it is a code defect, or as a plain assert update if it is a stale pin this plan already predicted).

**Interfaces:** none new.

**Steps:**

- [ ] 1. Close any running `LocalScribe.App.exe` (running app locks Core.dll -> MSB3027). Check with `tasklist | findstr LocalScribe` and close that specific process only (never kill broadly).
- [ ] 2. Run the FULL Core suite: `dotnet test tests\LocalScribe.Core.Tests` — expect 0 failures. Pay attention to `RendererTests` (save-time `transcript.md`/`.txt` byte-identity — must be untouched by this plan) and `SectionGrouperTests`.
- [ ] 3. Run the FULL App suite: `dotnet test tests\LocalScribe.App.Tests` — expect 0 failures (recent baseline is 838 passing; this plan adds 3).
- [ ] 4. If anything fails: diagnose root cause before touching code (no blind assert loosening); the only asserts this plan legitimately changes are the three footer pins updated in Task 6.
- [ ] 5. Sanity-grep the new/changed files for emojis and culture leaks: confirm no non-ASCII beyond the pre-existing `·` escape in `MarkdownRenderer.cs`, and that every new `ToString` on a number passes `CultureInfo.InvariantCulture` (the two sites are in `TextColumnTwips`'s callers via `MarkerLine`/`AddStyles`).
- [ ] 6. Commit only if steps 2-4 required fixes, e.g.:
```
git add <exact fixed paths>
git commit -m "fix(export): <what the full-suite run surfaced>"
```
