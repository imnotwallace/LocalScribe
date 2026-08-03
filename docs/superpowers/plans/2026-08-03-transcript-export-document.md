# Transcript Export Document Round Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the exported `.docx`/`.md` transcript read as a usable legal document — clean footer, Arial, real spacing, page:line citation, speaker attribution that survives a page flip — and let the user launch an export from the Record console and the read view instead of only the Sessions page.

**Architecture:** All rendering changes land in `LocalScribe.Core/Projection` (`DocxRenderer`, `MarkdownRenderer`, `TimestampCadence`, a new `MetadataFormat` helper and a new `ExportProvenance` record). The renderers stay **pure serializers**: every composed string arrives as a parameter, composed in `MaintenanceService` where `footerText` composes today. UI wiring is three files in `LocalScribe.App` and reuses the existing `ExportDialogViewModel`/`ExportDialog` verbatim.

**Tech Stack:** .NET 10, WPF (`Wpf.Ui`), `DocumentFormat.OpenXml`, xUnit. Solution is `LocalScribe.slnx`.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-03-transcript-export-document-design.md`. Read it before starting.
- **Evidentiary rules (locked, project-wide):** transcripts are evidence. Never delete, hide, or redact transcript content. `transcript.jsonl` is append-only and is never rewritten.
- **All exported TEXT is invariant-culture.** The only machine-locale dependence in the whole export path is page size (`RegionInfo` → `DocxPageSize`), resolved at the `MaintenanceService` call site. Do not add another.
- **ASCII source files.** Non-ASCII characters in string literals use `\u` escapes (e.g. `\u00B7` for the middle dot). This is the existing house rule — see `MarkdownRenderer.cs:8`.
- **Font size stays pinned at 11pt** (`FontSize { Val = "22" }`). `DocxRenderer.TextColumnTwips` arithmetic depends on it.
- **Comment style:** this codebase writes dense `///` summaries explaining *why*, citing the design doc and section. Match it. Cite `design 2026-08-03` for everything in this plan.
- **Commit style:** `feat(export): ...`, `fix(export): ...`, `test(export): ...`, `refactor(export): ...`. One commit per task.
- **Build gotcha:** a running `App.exe` locks `Core.dll` and the build fails with `MSB3027`. Close the app before building.
- **Test commands:**
  - Whole solution: `dotnet test F:\LocalScribe\LocalScribe.slnx`
  - One class: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"`
  - One test: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests.Footer_carries_the_title_and_a_page_x_of_y_field"`

## File Structure

**Created:**
- `src/LocalScribe.Core/Projection/ExportProvenance.cs` — export-only provenance record. Deliberately separate from `SessionTextView`, which is documented as the *neutral, app-independent* projection behind `session.txt` and must not grow export-specific fields.
- `src/LocalScribe.Core/Projection/MetadataFormat.cs` — the shared date-line composer, so `session.txt` and the two export renderers cannot drift.

**Modified:**
- `src/LocalScribe.Core/Projection/DocxRenderer.cs` — the bulk of the work.
- `src/LocalScribe.Core/Projection/MarkdownRenderer.cs` — parity for everything format-neutral.
- `src/LocalScribe.Core/Projection/TimestampCadence.cs` — second chunk trigger.
- `src/LocalScribe.Core/Projection/SessionTextRenderer.cs` — date line delegates to `MetadataFormat`.
- `src/LocalScribe.Core/Storage/SessionProjectionLoader.cs:91-92` — participants without side tags.
- `src/LocalScribe.Core/Storage/TranscriptStore.cs:31` — `FileShare.ReadWrite` on the read path.
- `src/LocalScribe.Core/Model/Settings.cs:27-30` — delete `DocxFooterText`.
- `src/LocalScribe.App/Services/MaintenanceService.cs:997-1054` — compose `ExportProvenance`, drop `footerText`.
- `src/LocalScribe.App/App.xaml.cs` — hoist `openExport`, thread it into the read view and console.
- `src/LocalScribe.App/ReadViewWindow.xaml` + `.xaml.cs` — Export button.
- `src/LocalScribe.App/LiveViewWindow.xaml` + `.xaml.cs` — Export button.
- `src/LocalScribe.App/TrayIconHost.cs:115` — pass the callback through.

**Markdown parity is not a separate task.** Each task changes *both* renderers together, so parity is enforced per behaviour rather than bolted on at the end.

---

### Task 1: Shared date-line helper

`session.txt` composes `start - end (N min)` at `SessionTextRenderer.cs:22-26`. The export renderers print a start time only. Extract the composer so all three share it.

**Files:**
- Create: `src/LocalScribe.Core/Projection/MetadataFormat.cs`
- Modify: `src/LocalScribe.Core/Projection/SessionTextRenderer.cs:19-27`
- Test: `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs`

**Interfaces:**
- Produces: `MetadataFormat.DateLine(SessionTextView v) -> string`. Returns `"2026-06-30 14:32 - 15:09 (37 min)"` when `EndedAtLocal` is set, `"2026-06-30 14:32 (37 min)"` when null.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs`:

```csharp
// tests/LocalScribe.Core.Tests/MetadataFormatTests.cs
using LocalScribe.Core.Projection;

public class MetadataFormatTests
{
    private static readonly DateTimeOffset Started = new(2026, 6, 30, 14, 32, 0, TimeSpan.Zero);

    private static SessionTextView View(DateTimeOffset? ended, long durationMs)
        => new("Weekly Sync", [], [], Started, ended, durationMs, "Teams", "", null);

    [Fact]
    public void DateLine_pairs_start_and_end_with_rounded_minutes()
        => Assert.Equal("2026-06-30 14:32 - 15:09 (37 min)",
            MetadataFormat.DateLine(View(Started.AddMinutes(37), 2220000)));

    [Fact]
    public void DateLine_omits_the_end_when_the_session_has_not_ended()
        => Assert.Equal("2026-06-30 14:32 (37 min)",
            MetadataFormat.DateLine(View(null, 2220000)));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~MetadataFormatTests"`
Expected: FAIL — `MetadataFormat` does not exist (compile error `CS0103`).

- [ ] **Step 3: Write the helper**

Create `src/LocalScribe.Core/Projection/MetadataFormat.cs`:

```csharp
using System.Globalization;
namespace LocalScribe.Core.Projection;

/// <summary>Metadata strings shared by session.txt and BOTH export renderers (design 2026-08-03).
/// Extracted from SessionTextRenderer so the three surfaces cannot drift: the exports previously
/// printed a start time only while session.txt printed start-end-duration. Invariant-culture by
/// construction, like every other exported string.</summary>
public static class MetadataFormat
{
    /// <summary>"2026-06-30 14:32 - 15:09 (37 min)", or the start-only form when the session has
    /// no end (a live/unfinalized session exported mid-recording, design 2026-08-03 section 11).</summary>
    public static string DateLine(SessionTextView v)
    {
        long durationMin = (long)Math.Round(v.DurationMs / 60000.0);
        return v.EndedAtLocal is { } end
            ? string.Create(CultureInfo.InvariantCulture,
                $"{v.StartedAtLocal:yyyy-MM-dd HH:mm} - {end:HH:mm} ({durationMin} min)")
            : string.Create(CultureInfo.InvariantCulture,
                $"{v.StartedAtLocal:yyyy-MM-dd HH:mm} ({durationMin} min)");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~MetadataFormatTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Delegate session.txt to the helper**

In `src/LocalScribe.Core/Projection/SessionTextRenderer.cs`, replace the body of `Render` down to the `dateLine` local with a call. The method becomes:

```csharp
    public static string Render(SessionTextView v)
    {
        var sb = new StringBuilder();
        sb.Append(v.Title).Append('\n').Append('\n');
        sb.Append("Matter(s): ").Append(v.Matters.Count == 0 ? "(none)" : string.Join(", ", v.Matters)).Append('\n');
        sb.Append("Participants: ").Append(v.Participants.Count == 0 ? "(none)" : string.Join(", ", v.Participants)).Append('\n');
        sb.Append("Date: ").Append(MetadataFormat.DateLine(v)).Append('\n');
        sb.Append("Medium: ").Append(v.Medium).Append('\n');
        sb.Append("Description: ").Append(string.IsNullOrEmpty(v.Description) ? "(none)" : v.Description).Append('\n');
        sb.Append("Summary: ").Append(string.IsNullOrEmpty(v.Summary) ? "(none)" : v.Summary).Append('\n');
        return sb.ToString();
    }
```

Delete the now-unused `durationMin` local and the `using System.Globalization;` if nothing else in the file uses it (it does not).

- [ ] **Step 6: Run the full Core suite to prove session.txt output is unchanged**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests`
Expected: PASS, no regressions. This is a pure refactor — any `session.txt` assertion that changes means the extraction was not faithful.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Projection/MetadataFormat.cs src/LocalScribe.Core/Projection/SessionTextRenderer.cs tests/LocalScribe.Core.Tests/MetadataFormatTests.cs
git commit -m "refactor(export): extract the shared metadata date line

session.txt composed start-end-duration while both export renderers printed a
start time only. One helper so the three cannot drift (design 2026-08-03 section 6)."
```

---

### Task 2: Participants drop the Local/Remote side tag

**Files:**
- Modify: `src/LocalScribe.Core/Storage/SessionProjectionLoader.cs:91-92`
- Test: `tests/LocalScribe.Core.Tests/SessionProjectionLoaderTests.cs`

**Interfaces:**
- Produces: `LoadedProjection.TextView.Participants` entries formatted `"Name"` or `"Name (Role)"`. Consumed by `session.txt`, `DocxRenderer` and `MarkdownRenderer`, all of which just `string.Join(", ", ...)`.

- [ ] **Step 1: Write the failing test**

Append to `tests/LocalScribe.Core.Tests/SessionProjectionLoaderTests.cs`. Match the file's existing fixture-building helpers — read them first and reuse whatever it already uses to write a session folder; do not invent a second fixture style.

```csharp
    [Fact]
    public async Task Participants_render_without_the_capture_side_keeping_role()
    {
        // The side (Local/Remote) is a capture implementation detail and means nothing to a
        // reader of the document (design 2026-08-03 section 7). Role is user-entered and kept.
        var meta = new SessionMeta
        {
            Title = "Weekly Sync",
            Participants =
            [
                new SessionParticipant { Id = "p1", Name = "Sam", Side = SourceKind.Local },
                new SessionParticipant { Id = "p2", Name = "Bob", Role = "Counsel", Side = SourceKind.Remote },
            ],
        };
        var loaded = await LoadWithMetaAsync(meta);   // existing fixture helper in this file

        Assert.Equal(["Sam", "Bob (Counsel)"], loaded.TextView.Participants);
    }
```

> If the file has no `LoadWithMetaAsync`-shaped helper, use whatever it does have to write `meta.json` + `session.json` + an empty `transcript.jsonl` into a temp session folder and call `SessionProjectionLoader.LoadAsync`. The assertion is the point; the fixture plumbing must match the file's existing style.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~Participants_render_without_the_capture_side"`
Expected: FAIL — actual is `["Sam (Local)", "Bob (Counsel, Remote)"]`.

- [ ] **Step 3: Change the loader**

In `src/LocalScribe.Core/Storage/SessionProjectionLoader.cs`, replace lines 91-92:

```csharp
        // Name-only, role when set (design 2026-08-03 section 7): the capture side (Local/Remote)
        // is an implementation detail of how the audio was acquired and means nothing to a reader
        // of the exported document. Formatted HERE, in the shared projection, so session.txt and
        // both export renderers cannot disagree about how a participant is written.
        var participants = meta.Participants.Select(p =>
            string.IsNullOrEmpty(p.Role) ? p.Name : $"{p.Name} ({p.Role})").ToList();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~Participants_render_without_the_capture_side"`
Expected: PASS.

- [ ] **Step 5: Update the renderer fixtures that hard-code the old shape**

`tests/LocalScribe.Core.Tests/DocxRendererTests.cs:16` builds its `SessionTextView` with `new[] { "Sam (Local)", "Bob (Remote)" }` and `:51` asserts `"Participants: Sam (Local), Bob (Remote)"`. These are renderer tests receiving pre-formatted strings, so they still *pass* — but they now describe output the loader can never produce. Change the fixture to `new[] { "Sam", "Bob (Counsel)" }` and the assertion to `"Participants: Sam, Bob (Counsel)"`. Do the same for the equivalent fixture in `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`.

- [ ] **Step 6: Run the full Core suite**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Storage/SessionProjectionLoader.cs tests/LocalScribe.Core.Tests/
git commit -m "feat(export): participants render without the capture side

Local/Remote is an implementation detail of audio acquisition, not something a
reader of the transcript needs. Role is kept (design 2026-08-03 section 7).
Formatted in the shared projection, so session.txt changes identically."
```

---

### Task 3: `ExportProvenance`, the new footer, and deleting `DocxFooterText`

These are one change: the footer stops being a settings string, so the parameter carrying it disappears and the provenance it used to hold needs somewhere to live.

**Files:**
- Create: `src/LocalScribe.Core/Projection/ExportProvenance.cs`
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs:52-131`
- Modify: `src/LocalScribe.Core/Projection/MarkdownRenderer.cs:45-88`
- Modify: `src/LocalScribe.Core/Model/Settings.cs:27-30`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs:997-1054`
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`, `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`, `tests/LocalScribe.Core.Tests/SettingsTests.cs`, `tests/LocalScribe.App.Tests/MaintenanceServiceVersionsTests.cs`

**Interfaces:**
- Produces: `ExportProvenance` (record, all `{ get; init; }`) with `VersionId`, `Model`, `Backend`, `AudioFileName`, `AudioSha256`, `InProgress`.
- Produces: `DocxRenderer.Write(Stream output, TranscriptHeader header, SessionTextView meta, ExportProvenance provenance, IReadOnlyList<DisplayRow> rows, string timestampsMode, DocxPageSize pageSize, DocxOptions options)` — note `string footerText` is **gone**.
- Produces: `MarkdownRenderer.Write(TranscriptHeader header, SessionTextView meta, ExportProvenance provenance, IReadOnlyList<DisplayRow> rows, string timestampsMode, DocxOptions options)` — `footerText` gone.
- Consumed by Tasks 8 and 10.

- [ ] **Step 1: Write the failing tests**

Replace the footer assertion at `DocxRendererTests.cs:57` and add a new test. First update the file's `Render` helper — it currently takes a `string footer`:

```csharp
    private static byte[] Render(string mode, DocxPageSize size, DocxOptions opts,
        ExportProvenance? provenance = null)
    {
        var (h, v, r) = Sample();
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v, provenance ?? new ExportProvenance(), r, mode, size, opts);
        return ms.ToArray();   // valid even after the document disposed/closed the stream
    }
```

Update every existing call site in the file (they pass a footer string as the second argument — drop it). Then add:

```csharp
    [Fact]
    public void Footer_carries_the_title_and_a_page_x_of_y_field()
    {
        // design 2026-08-03 section 2: footer is exactly {transcript name} + Page N of M. The
        // privilege string and the model description are gone - version provenance moved up into
        // the metadata block, where it is stated once instead of on every page.
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions(),
            new ExportProvenance { VersionId = "v2-large-v3-turbo-2026-08-01", Model = "large-v3-turbo" });
        using var doc = Open(bytes);
        var footer = doc.MainDocumentPart!.FooterParts.First().Footer!;

        Assert.StartsWith("Weekly Sync", footer.InnerText);
        Assert.DoesNotContain("PRIVILEGED", footer.InnerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("large-v3-turbo", footer.InnerText);

        var codes = footer.Descendants<FieldCode>().Select(f => f.Text.Trim()).ToList();
        Assert.Contains("PAGE", codes);
        Assert.Contains("NUMPAGES", codes);
        Assert.Contains("of", footer.InnerText);
    }
```

And in `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`:

```csharp
    [Fact]
    public void Markdown_has_no_footer_block()
    {
        // design 2026-08-03 section 9: with the footer reduced to the transcript name, and the
        // name already the H1 at the top, a trailing rule + name block is pure repetition.
        string md = Write(new DocxOptions());   // existing helper in this file
        Assert.DoesNotContain("\n---\n", md);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests|FullyQualifiedName~MarkdownRendererWriteTests"`
Expected: FAIL to compile — `ExportProvenance` does not exist and `Write` still wants `footerText`.

- [ ] **Step 3: Create the provenance record**

Create `src/LocalScribe.Core/Projection/ExportProvenance.cs`:

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>Export-only provenance for a rendered transcript (design 2026-08-03 section 1).
/// Deliberately NOT folded into SessionTextView: that record is the neutral, app-independent
/// metadata projection behind session.txt and must not grow export-specific fields. Composed in
/// MaintenanceService (where the old footerText composed), so both renderers stay pure
/// serializers. House style mirrors DocxOptions: sealed record + { get; init; } with inline
/// defaults.</summary>
public sealed record ExportProvenance
{
    public string VersionId { get; init; } = TranscriptVersions.Root;
    public string Model { get; init; } = "";
    public string Backend { get; init; } = "";
    /// <summary>Imported sessions only, from ImportedSourceInfo. Null for recorded sessions -
    /// hashing recorded audio at export time is deliberately out of scope (it would hash a large
    /// FLAC on every export).</summary>
    public string? AudioFileName { get; init; }
    public string? AudioSha256 { get; init; }
    /// <summary>The session has no EndedAtUtc - exported mid-recording, so the transcript is
    /// incomplete and diarisation has not run (design 2026-08-03 section 11).</summary>
    public bool InProgress { get; init; }
}
```

- [ ] **Step 4: Rewrite the docx footer**

In `src/LocalScribe.Core/Projection/DocxRenderer.cs`, change the signature (drop `string footerText`, add `ExportProvenance provenance` after `meta`) and replace the footer construction at `:106-116`:

```csharp
        // design 2026-08-03 section 2: {transcript name} at the left margin, "Page N of M" at a
        // right tab on the usable width. The cached "1"/"1" results are the placeholders Word
        // replaces when it paginates. Field instruction text is invariant by construction.
        var footerPart = mainPart.AddNewPart<FooterPart>();
        footerPart.Footer = new Footer(new Paragraph(
            new ParagraphProperties(
                new Tabs(new TabStop { Val = TabStopValues.Right, Position = usableWidth })),
            new Run(MakeText(meta.Title)),
            new Run(new TabChar()),
            new Run(MakeText("Page ")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(MakeText("1")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End }),
            new Run(MakeText(" of ")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" NUMPAGES ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(MakeText("1")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
```

- [ ] **Step 5: Drop the markdown footer block**

In `src/LocalScribe.Core/Projection/MarkdownRenderer.cs`, change the `Write` signature the same way (drop `footerText`, add `ExportProvenance provenance` after `meta`) and delete lines 85-86 entirely:

```csharp
        // design 2026-08-03 section 9: no footer block. The title is already the H1 above, so a
        // trailing rule + name repeated it. Markdown has no pages, so there is nothing else the
        // docx footer carried that is meaningful here.
        return sb.ToString();
```

Update the `<summary>` on `Write` to drop its "footer text after a horizontal rule" sentence.

- [ ] **Step 6: Delete the setting**

In `src/LocalScribe.Core/Model/Settings.cs`, delete lines 27-30 (the `DocxFooterText` doc comment and property).

This needs no migration and no schema bump: `LocalScribeJson.Options` does not set `UnmappedMemberHandling`, so the `System.Text.Json` default of `Skip` means existing `settings.json` files carrying `"docxFooterText"` load unchanged and ignore the key.

Delete the assertion at `tests/LocalScribe.Core.Tests/SettingsTests.cs:22` and the whole `DocxFooterText_defaults_and_roundtrips` test at `:171-182`. The comments at `:191`, `:211` and `:253` cite `DocxFooterText` as the additive-field precedent — repoint them to `SectionGapMs`, which is the other half of the same precedent and still exists.

- [ ] **Step 7: Compose provenance in MaintenanceService**

In `src/LocalScribe.App/Services/MaintenanceService.cs`, replace the `versionNote`/`footerText` composition in **both** `ExportDocxAsync` (`:1011-1022`) and `ExportMarkdownAsync` (`:1038-1049`) with a shared local helper. Add this private static method to the class:

```csharp
    /// <summary>Compose the export-only provenance block (design 2026-08-03 section 1). Composed
    /// HERE, where footerText used to compose, so the renderers stay pure serializers. Shared by
    /// both textual exports so they can never disagree about provenance.</summary>
    private static ExportProvenance ProvenanceFor(LoadedProjection loaded)
        => new()
        {
            VersionId = loaded.VersionId,
            Model = loaded.Header.Model,
            Backend = loaded.Header.Backend,
            AudioFileName = loaded.Session.ImportedSource?.FileName,
            AudioSha256 = loaded.Session.ImportedSource?.Sha256,
            InProgress = loaded.Session.EndedAtUtc is null,
        };
```

Then the two call sites become:

```csharp
            DocxRenderer.Write(fs, loaded.Header, loaded.TextView, ProvenanceFor(loaded), loaded.Rows,
                settings.Current.Timestamps, pageSize, options);
```

```csharp
            string markdown = MarkdownRenderer.Write(loaded.Header, loaded.TextView,
                ProvenanceFor(loaded), loaded.Rows, settings.Current.Timestamps, options);
```

> Check the property name `LoadedProjection.Session` before writing this — `SessionProjectionLoader.cs:105` constructs it positionally as `new LoadedProjection(session, meta, ...)`. Use whatever the record actually names that first member.

- [ ] **Step 8: Fix the App test suite**

`tests/LocalScribe.App.Tests/MaintenanceServiceVersionsTests.cs:190` and `:218` construct `new Settings { DocxFooterText = "PRIVILEGED" }` and `:196`/`:207` assert on footer text. Rewrite them against the new footer: the settings construction drops the field, and the assertions become "the footer contains the session title and no model name".

- [ ] **Step 9: Run the full solution**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(export): footer is the transcript name plus Page N of M

Drops the PRIVILEGED & CONFIDENTIAL string and, on re-transcribed sessions, the
model description. Version provenance moves into the metadata block in a later
task; ExportProvenance carries it there without polluting SessionTextView, which
is the neutral session.txt projection.

Settings.DocxFooterText deleted - no migration needed, System.Text.Json skips
unmapped members by default so existing settings.json files load unchanged.
Markdown loses its footer block: the title is already the H1 (design 2026-08-03
sections 1, 2, 9)."
```

---

### Task 4: Arial, turn spacing, widow control

**Files:**
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs:167-182` (`AddStyles`)
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`

**Interfaces:**
- Consumes: the `Render` helper from Task 3.
- Produces: `DocDefaults` carrying `RunFonts`; `TranscriptTurn` carrying `SpacingBetweenLines` and `WidowControl`.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void Document_defaults_pin_arial_on_every_script_slot()
    {
        // Word was rendering Times New Roman - never a choice. AddStyles pinned a font SIZE but
        // no face and there is no theme part, so Word fell back (design 2026-08-03 section 4).
        // DocDefaults (not the turn style) so headings, footer, header, markers and line numbers
        // all inherit.
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var fonts = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!
            .GetFirstChild<DocDefaults>()!.RunPropertiesDefault!.RunPropertiesBaseStyle!
            .GetFirstChild<RunFonts>()!;

        Assert.Equal("Arial", fonts.Ascii!.Value);
        Assert.Equal("Arial", fonts.HighAnsi!.Value);
        Assert.Equal("Arial", fonts.ComplexScript!.Value);
    }

    [Fact]
    public void Turn_style_spaces_turns_apart_and_controls_widows()
    {
        // 6pt after each turn: turns were back-to-back paragraphs with zero spacing, which read as
        // one dense wall. WidowControl keeps >=2 lines together so a speaker label cannot strand
        // alone at a page bottom; KeepLines is deliberately NOT used - it would push a whole
        // multi-page turn onto a new page (design 2026-08-03 section 4).
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var props = TurnStyle(doc).StyleParagraphProperties!;

        Assert.Equal("120", props.GetFirstChild<SpacingBetweenLines>()!.After!.Value);   // 120/20 = 6pt
        Assert.NotNull(props.GetFirstChild<WidowControl>());
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"`
Expected: FAIL — `NullReferenceException` on the missing `RunFonts` / `SpacingBetweenLines`.

- [ ] **Step 3: Add the fonts and spacing**

In `AddStyles`, replace the `DocDefaults` and `TranscriptTurn` style construction:

```csharp
        stylesPart.Styles = new Styles(
            // Arial pinned at the document default (design 2026-08-03 section 4), NOT on the turn
            // style: with no rFonts and no theme part Word fell back to Times New Roman, and that
            // fallback reached headings, footer, header and line numbers too. Size stays at 11pt -
            // TextColumnTwips' character-advance arithmetic depends on it.
            new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
                new FontSize { Val = "22" }, new FontSizeComplexScript { Val = "22" }))),
            new Style(
                new StyleName { Val = "Transcript Turn" },
                new StyleParagraphProperties(
                    new Tabs(new TabStop { Val = TabStopValues.Left, Position = textCol }),
                    new Indentation { Left = col, Hanging = col },
                    // 6pt (120 twentieths of a point) after each turn.
                    new SpacingBetweenLines { After = "120" },
                    new WidowControl()))
            { Type = StyleValues.Paragraph, StyleId = "TranscriptTurn" });
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(export): Arial document default, 6pt turn spacing, widow control

Word rendered Times New Roman because AddStyles pinned a size but no face and
there is no theme part. Pinned at DocDefaults so every surface inherits
(design 2026-08-03 section 4)."
```

---

### Task 5: `TranscriptSpeaker` character style and caps

The speaker name becomes its own run in its own character style. This is what Task 6's running head reads, and it is the reason the label must be split into runs rather than emitted as one `"[00:01] Sam:"` string.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs` (`AddStyles`, `TurnParagraph`, `TurnLabel`, `TextColumnTwips`, `Write`)
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`

**Interfaces:**
- Produces: a `Style` with `StyleId = "TranscriptSpeaker"`, `Type = StyleValues.Character`.
- Produces: `private readonly record struct TurnLabelParts(string Stamp, string Name, string Suffix)` with `int Length => Stamp.Length + Name.Length + Suffix.Length`.
- Produces: `private static Paragraph TurnParagraph(TurnLabelParts label, string text)`.
- Consumed by Tasks 6 and 9.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void Speaker_style_is_a_pure_character_style_carrying_caps()
    {
        // MUST be type=character, never a linked paragraph+character style: Word will not see a
        // linked style applied to only part of a paragraph, and the speaker name is exactly that -
        // a run inside the turn paragraph. STYLEREF in the page header (Task 6) depends on this.
        // Caps is a FORMAT, never an uppercased string: STYLEREF returns the underlying text, so
        // uppercasing the data would destroy the real name to achieve a display effect
        // (design 2026-08-03 sections 3, 4).
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var style = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!.Elements<Style>()
            .Single(s => s.StyleId == "TranscriptSpeaker");

        Assert.Equal(StyleValues.Character, style.Type!.Value);
        Assert.NotNull(style.StyleRunProperties!.GetFirstChild<Caps>());
    }

    [Fact]
    public void Only_the_speaker_name_carries_the_speaker_style_not_the_stamp_or_colon()
    {
        // STYLEREF returns the styled run's text verbatim, so a combined "[00:01] Sam:" run would
        // put the stamp and colon in the running head. Three runs: stamp, name, colon.
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var turn = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText.StartsWith("[00:01] Sam:", StringComparison.Ordinal));

        var styled = turn.Elements<Run>()
            .Where(r => r.RunProperties?.GetFirstChild<RunStyle>()?.Val?.Value == "TranscriptSpeaker")
            .ToList();
        Assert.Equal("Sam", Assert.Single(styled).InnerText);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"`
Expected: FAIL — no `TranscriptSpeaker` style; the label is a single run.

- [ ] **Step 3: Add the character style**

Append to the `Styles` construction in `AddStyles`, after the `TranscriptTurn` style:

```csharp
            ,
            // Pure CHARACTER style (design 2026-08-03 sections 3-4). Two hard requirements:
            // (1) type=character, never linked - Word's STYLEREF cannot see a linked style applied
            //     to part of a paragraph, and this is applied to a run inside the turn paragraph;
            // (2) Caps as a FORMAT, never an uppercased string - STYLEREF returns the underlying
            //     text, so uppercasing the data would destroy the real name in the document body
            //     to achieve a display effect.
            new Style(
                new StyleName { Val = "Transcript Speaker" },
                new StyleRunProperties(new Bold(), new Caps()))
            { Type = StyleValues.Character, StyleId = "TranscriptSpeaker" }
```

- [ ] **Step 4: Split the turn label into runs**

Replace `TurnLabel`, `TurnParagraph` and `TextColumnTwips` in `DocxRenderer.cs`:

```csharp
    /// <summary>The three pieces of a turn label, kept separate because only the NAME may carry
    /// the TranscriptSpeaker character style - STYLEREF in the page header returns that run's text
    /// verbatim (design 2026-08-03 section 3). Length is what TextColumnTwips measures.</summary>
    private readonly record struct TurnLabelParts(string Stamp, string Name, string Suffix)
    {
        public int Length => Stamp.Length + Name.Length + Suffix.Length;
    }

    private static TurnLabelParts TurnLabel(DisplayRow row, DocxOptions options, string timestampsMode,
        DateTimeOffset startedAtLocal)
        => new(
            options.IncludeTimestamps
                ? "[" + TimestampFormat.Stamp(row.StartMs, timestampsMode, startedAtLocal) + "] "
                : "",
            row.DisplayName ?? "",
            ":");

    /// <summary>Bold stamp -> styled name -> bold suffix -> tab -> text. The TranscriptTurn style
    /// carries the hanging indent and tab stop, so a recipient can retune the whole document by
    /// editing one style in Word.</summary>
    private static Paragraph TurnParagraph(TurnLabelParts label, string text)
    {
        var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "TranscriptTurn" }));
        if (label.Stamp.Length > 0)
            p.AppendChild(new Run(new RunProperties(new Bold()), MakeText(label.Stamp)));
        if (label.Name.Length > 0)
            p.AppendChild(new Run(
                new RunProperties(new RunStyle { Val = "TranscriptSpeaker" }), MakeText(label.Name)));
        p.AppendChild(new Run(new RunProperties(new Bold()), MakeText(label.Suffix)));
        p.AppendChild(new Run(new TabChar()));
        p.AppendChild(new Run(MakeText(text)));
        return p;
    }
```

`TextColumnTwips` keeps its shape; only the measured value changes:

```csharp
        foreach (var row in rows)
            if (!row.IsMarker)
                longest = Math.Max(longest,
                    TurnLabel(row, options, timestampsMode, startedAtLocal).Length);
```

- [ ] **Step 5: Update the continuation-chunk call site**

In `Write`, the continuation paragraph at `:93-96` currently passes a bare stamp string. It must now pass parts. Until Task 9 gives continuations a name, the stamp goes in `Stamp` with an empty `Name` and empty `Suffix`:

```csharp
            for (int i = 1; i < chunks.Count; i++)
                body.AppendChild(TurnParagraph(
                    new TurnLabelParts(
                        "[" + TimestampFormat.Stamp(chunks[i].StampMs, timestampsMode, header.StartedAtLocal) + "]",
                        "", ""),
                    chunks[i].Text));
```

- [ ] **Step 6: Run the Core suite**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests`
Expected: PASS. The existing test at `DocxRendererTests.cs:77-80` asserts the turn paragraph's runs — it will need its run-count expectation updated to match the new three-run label.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(export): TranscriptSpeaker character style on the speaker name run

Splits the turn label into stamp / name / colon runs so only the name carries the
style. STYLEREF returns the styled run's text verbatim, so a combined label would
put the timestamp and colon in the running head (design 2026-08-03 sections 3-4)."
```

---

### Task 6: Page header with the STYLEREF running head

**Files:**
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs` (`Write`, new private helpers)
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`

**Interfaces:**
- Consumes: `TranscriptSpeaker` style from Task 5.
- Produces: a default `HeaderPart` and an empty first-page `HeaderPart`; `TitlePage` in `SectionProperties`; a first-page `FooterReference` reusing the default footer part id.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void Page_header_pairs_the_matter_and_date_with_a_styleref_running_head()
    {
        // design 2026-08-03 section 3. Word searches the current page top-to-bottom for the style
        // and, if it finds none, searches BACKWARD from the top of the page to the start of the
        // document - so a page holding only continuation text still resolves to whoever is
        // speaking. That fallback is the whole point of the running head.
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;
        var sect = main.Document!.Body!.GetFirstChild<SectionProperties>()!;

        string defaultId = sect.Elements<HeaderReference>()
            .Single(h => h.Type!.Value == HeaderFooterValues.Default).Id!.Value!;
        var header = ((HeaderPart)main.GetPartById(defaultId)).Header!;

        Assert.Contains("Acme (2026-014)", header.InnerText);
        Assert.Contains("2026-06-30", header.InnerText);
        Assert.Contains("STYLEREF \"TranscriptSpeaker\"",
            string.Concat(header.Descendants<FieldCode>().Select(f => f.Text)));
    }

    [Fact]
    public void First_page_suppresses_the_header_but_keeps_the_footer()
    {
        // The metadata block already names everything on page 1, so the running head there is
        // noise. TitlePg is what suppresses it - but TitlePg ALSO drops the page-1 footer unless a
        // first-page FooterReference is supplied, and page 1 must still show "Page 1 of N".
        // Pointing it at the SAME footer part id is what keeps them identical.
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;
        var sect = main.Document!.Body!.GetFirstChild<SectionProperties>()!;

        Assert.NotNull(sect.GetFirstChild<TitlePage>());

        string firstHeaderId = sect.Elements<HeaderReference>()
            .Single(h => h.Type!.Value == HeaderFooterValues.First).Id!.Value!;
        Assert.Equal("", ((HeaderPart)main.GetPartById(firstHeaderId)).Header!.InnerText);

        string defaultFooterId = sect.Elements<FooterReference>()
            .Single(f => f.Type!.Value == HeaderFooterValues.Default).Id!.Value!;
        string firstFooterId = sect.Elements<FooterReference>()
            .Single(f => f.Type!.Value == HeaderFooterValues.First).Id!.Value!;
        Assert.Equal(defaultFooterId, firstFooterId);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"`
Expected: FAIL — no header parts exist.

- [ ] **Step 3: Build the header parts**

In `DocxRenderer.Write`, immediately after the footer part is created and before `body.AppendChild(new SectionProperties(...))`:

```csharp
        // Running head (design 2026-08-03 section 3): matter + date at the left margin, the
        // current speaker at a right tab. The speaker is a STYLEREF field, not text we compose -
        // only Word knows where its own page breaks fall. The left half IS composed here, because
        // STYLEREF cannot truncate and a long matter would otherwise collide with the speaker.
        var headerPart = mainPart.AddNewPart<HeaderPart>();
        headerPart.Header = new Header(new Paragraph(
            new ParagraphProperties(
                new Tabs(new TabStop { Val = TabStopValues.Right, Position = usableWidth }),
                new ParagraphBorders(new BottomBorder
                { Val = BorderValues.Single, Size = 4U, Space = 4U, Color = "auto" })),
            new Run(MakeText(HeaderLeft(header, meta))),
            new Run(new TabChar()),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" STYLEREF \"TranscriptSpeaker\" ")
            { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            // Caps here too: STYLEREF returns the stored name, and the body shows it in caps via
            // the character style, so the head must match.
            new Run(new RunProperties(new Caps()), MakeText("")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
        string headerId = mainPart.GetIdOfPart(headerPart);

        // Page 1 carries the metadata block, which names everything the running head would - so
        // the head is suppressed there via TitlePg + an EMPTY first-page header.
        var firstHeaderPart = mainPart.AddNewPart<HeaderPart>();
        firstHeaderPart.Header = new Header(new Paragraph());
        string firstHeaderId = mainPart.GetIdOfPart(firstHeaderPart);
```

Add the private helper beside `MetaLine`:

```csharp
    /// <summary>The composed left half of the running head: first matter (or the title when the
    /// session is untagged) and the start date. Composed rather than STYLEREF'd because a long
    /// matter must be truncatable - STYLEREF cannot truncate (design 2026-08-03 section 3).</summary>
    private static string HeaderLeft(TranscriptHeader header, SessionTextView meta)
    {
        const int MaxChars = 60;
        string left = meta.Matters.Count > 0 ? meta.Matters[0] : meta.Title;
        if (left.Length > MaxChars) left = left[..(MaxChars - 1)] + "\u2026";
        return left + " \u00B7 "
            + header.StartedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
```

- [ ] **Step 4: Reference the header parts from `SectionProperties`**

`SectionProperties` has a schema element order: header/footer references come first, then `pgSz`, `pgMar`, then `lnNumType`, and `titlePg` comes after `pgMar`. Build it as:

```csharp
        body.AppendChild(new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = headerId },
            new HeaderReference { Type = HeaderFooterValues.First, Id = firstHeaderId },
            new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
            // Same part id as Default: TitlePg suppresses the page-1 footer unless a First
            // reference exists, and page 1 must still show "Page 1 of N".
            new FooterReference { Type = HeaderFooterValues.First, Id = footerId },
            new PageSize { Width = (UInt32Value)(uint)w, Height = (UInt32Value)(uint)h },
            new PageMargin
            {
                Top = MarginTwips, Right = (uint)MarginTwips,
                Bottom = MarginTwips, Left = (uint)MarginTwips,
                Header = (uint)HeaderFooterTwips, Footer = (uint)HeaderFooterTwips, Gutter = 0U,
            },
            new LineNumberType { CountBy = 5, Restart = LineNumberRestartValues.NewPage },
            new TitlePage()));
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"`
Expected: PASS. If Word later reports the file as corrupt, the cause is almost always `SectionProperties` child order — check it against the ECMA-376 `CT_SectPr` sequence.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(export): page header with a STYLEREF speaker running head

Word searches the page for the style and, finding none, searches backward to the
document start - so a page of pure continuation text still names its speaker.
Suppressed on page 1 via TitlePg, which needs a first-page FooterReference to the
same footer part or page 1 loses its page number (design 2026-08-03 section 3)."
```

---

### Task 7: Number every line

**Files:**
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs` (the `LineNumberType` added in Task 6)
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Every_line_is_numbered_for_page_line_citation()
    {
        // Page:line citation ("12:5") needs every line numbered, not every fifth. Restart-per-page
        // is unchanged. The fixed 25-lines-per-page deposition grid is deliberately NOT adopted -
        // it would force exact line spacing (design 2026-08-03 section 5).
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var ln = doc.MainDocumentPart!.Document!.Body!.GetFirstChild<SectionProperties>()!
            .GetFirstChild<LineNumberType>()!;

        Assert.Equal(1, ln.CountBy!.Value);
        Assert.Equal(LineNumberRestartValues.NewPage, ln.Restart!.Value);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~Every_line_is_numbered"`
Expected: FAIL — `Assert.Equal() Failure: Expected: 1, Actual: 5`.

- [ ] **Step 3: Change the count**

```csharp
            new LineNumberType { CountBy = 1, Restart = LineNumberRestartValues.NewPage },
```

Update the existing comment above it — it currently says "every 5th line".

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests"`
Expected: PASS. The existing test at `DocxRendererTests.cs` asserting `CountBy == 5` must be deleted — the new test replaces it.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(export): number every line for page:line citation

Deposition citation is page:line, which needs every line numbered rather than
every fifth (design 2026-08-03 section 5)."
```

---

### Task 8: Metadata block additions

**Files:**
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs:64-77`
- Modify: `src/LocalScribe.Core/Projection/MarkdownRenderer.cs:49-59`
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`, `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`

**Interfaces:**
- Consumes: `MetadataFormat.DateLine` (Task 1), `ExportProvenance` (Task 3).
- Produces: `private static string SpeakersHeard(IReadOnlyList<DisplayRow> rows)` in each renderer — or, preferably, one copy on `MetadataFormat` shared by both. Put it on `MetadataFormat`.

- [ ] **Step 1: Write the failing tests**

Add to `MetadataFormatTests.cs`:

```csharp
    [Fact]
    public void SpeakersHeard_lists_distinct_names_in_first_appearance_order()
    {
        // Distinct from Participants, which is user-curated metadata: this is who actually speaks
        // in the rows (design 2026-08-03 section 6).
        var rows = new[]
        {
            new DisplayRow { StartMs = 0, DisplayName = "Bob", Text = "One." },
            new DisplayRow { IsMarker = true, StartMs = 10, Text = "device changed" },
            new DisplayRow { StartMs = 20, DisplayName = "Sam", Text = "Two." },
            new DisplayRow { StartMs = 30, DisplayName = "Bob", Text = "Three." },
        };
        Assert.Equal("Bob, Sam", MetadataFormat.SpeakersHeard(rows));
    }
```

Add to `DocxRendererTests.cs`:

```csharp
    [Fact]
    public void Metadata_block_carries_duration_version_and_speakers_heard()
    {
        // Version/model provenance moved OFF the footer and up here, stated once (design
        // 2026-08-03 sections 2, 6).
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions(),
            new ExportProvenance
            {
                VersionId = "v2-large-v3-turbo-2026-08-01",
                Model = "large-v3-turbo",
                Backend = "cuda",
            });
        using var doc = Open(bytes);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.Contains("Date: 2026-06-30 14:32 - 15:09 (37 min)", text);
        Assert.Contains("Transcript version: v2 \u00B7 large-v3-turbo \u00B7 cuda", text);
        Assert.Contains("Speakers heard: Sam, Bob", text);
    }

    [Fact]
    public void Audio_provenance_renders_for_an_imported_session_and_is_absent_otherwise()
    {
        // ImportedSourceInfo already carries FileName + a Sha256 computed at copy time. Recorded
        // sessions have no hash and hashing their FLAC on every export is out of scope.
        byte[] imported = Render("relative", DocxPageSize.A4, new DocxOptions(),
            new ExportProvenance { AudioFileName = "call.m4a", AudioSha256 = "abc123" });
        using (var doc = Open(imported))
        {
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Audio: call.m4a", text);
            Assert.Contains("Audio SHA-256: abc123", text);
        }

        byte[] recorded = Render("relative", DocxPageSize.A4, new DocxOptions());
        using (var doc = Open(recorded))
        {
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.DoesNotContain("Audio:", text);
            Assert.DoesNotContain("Audio SHA-256:", text);
        }
    }
```

Add the markdown mirror to `MarkdownRendererWriteTests.cs`, asserting `"- **Transcript version:** v2 \u00B7 large-v3-turbo \u00B7 cuda"` and `"- **Speakers heard:** Sam, Bob"`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~MetadataFormatTests|FullyQualifiedName~DocxRendererTests|FullyQualifiedName~MarkdownRendererWriteTests"`
Expected: FAIL — `SpeakersHeard` missing; metadata lines absent.

- [ ] **Step 3: Add the shared helpers**

Append to `src/LocalScribe.Core/Projection/MetadataFormat.cs`:

```csharp
    /// <summary>Who actually speaks in the rows, distinct, in first-appearance order (design
    /// 2026-08-03 section 6). Deliberately distinct from SessionTextView.Participants, which is
    /// user-curated metadata and may name people who never speak (or omit people who do).</summary>
    public static string SpeakersHeard(IReadOnlyList<DisplayRow> rows)
    {
        var seen = new List<string>();
        foreach (var row in rows)
            if (!row.IsMarker && !string.IsNullOrEmpty(row.DisplayName)
                && !seen.Contains(row.DisplayName, StringComparer.Ordinal))
                seen.Add(row.DisplayName);
        return string.Join(", ", seen);
    }

    /// <summary>"v2 \u00B7 large-v3-turbo \u00B7 cuda". Rendered for originals too - ShortId("v1")
    /// returns "v1", so no special-casing (design 2026-08-03 section 6).</summary>
    public static string VersionLine(ExportProvenance p)
        => string.Join(" \u00B7 ",
            new[] { TranscriptVersions.ShortId(p.VersionId), p.Model, p.Backend }
                .Where(s => !string.IsNullOrEmpty(s)));
```

Add `using LocalScribe.Core.Model;` to the file for `TranscriptVersions`.

- [ ] **Step 4: Render the new lines in both renderers**

In `DocxRenderer.Write`, replace the metadata block (`:66-74`):

```csharp
        body.AppendChild(Heading(meta.Title));
        body.AppendChild(MetaLine("App", header.App));
        body.AppendChild(MetaLine("Date", MetadataFormat.DateLine(meta)));
        body.AppendChild(MetaLine("Matter(s)",
            meta.Matters.Count == 0 ? "(none)" : string.Join(", ", meta.Matters)));
        body.AppendChild(MetaLine("Participants",
            meta.Participants.Count == 0 ? "(none)" : string.Join(", ", meta.Participants)));
        body.AppendChild(MetaLine("Medium", meta.Medium));
        if (!string.IsNullOrEmpty(meta.Description)) body.AppendChild(MetaLine("Description", meta.Description));
        body.AppendChild(MetaLine("Transcript version", MetadataFormat.VersionLine(provenance)));
        if (!string.IsNullOrEmpty(provenance.AudioFileName))
            body.AppendChild(MetaLine("Audio", provenance.AudioFileName));
        if (!string.IsNullOrEmpty(provenance.AudioSha256))
            body.AppendChild(MetaLine("Audio SHA-256", provenance.AudioSha256));
        string speakers = MetadataFormat.SpeakersHeard(rows);
        if (speakers.Length > 0) body.AppendChild(MetaLine("Speakers heard", speakers));
        body.AppendChild(DisclaimerLine());
```

Mirror it in `MarkdownRenderer.Write` using the existing `AppendMeta` helper, in the same order, with `AppendMeta(sb, "Date", MetadataFormat.DateLine(meta))` replacing the current start-only date.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(export): metadata block carries duration, version, audio and speakers

End time and duration (session.txt already had them), the version provenance
moved off the footer, imported-audio filename + SHA-256, and who actually speaks
in the rows (design 2026-08-03 section 6)."
```

---

### Task 9: `(cont'd)` continuation labels

**Files:**
- Modify: `src/LocalScribe.Core/Projection/TimestampCadence.cs`
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs` (`Write`, `TextColumnTwips`)
- Modify: `src/LocalScribe.Core/Projection/MarkdownRenderer.cs` (`Write`)
- Test: `tests/LocalScribe.Core.Tests/TimestampCadenceTests.cs`, `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`

**Interfaces:**
- Produces: `TimestampCadence.Chunk(DisplayRow row, int intervalMs, int maxChars)` — a third parameter, no default (every call site is updated in this task).
- Produces: `public const int ContinuationMaxChars = 900;` on `DocxRenderer`.

- [ ] **Step 1: Write the failing tests**

Add to `TimestampCadenceTests.cs`:

```csharp
    [Fact]
    public void Chunk_splits_on_character_count_with_no_time_interval()
    {
        // The char trigger is ALWAYS on - it is the correctness mechanism behind (cont'd) labels,
        // not a preference, so it is not behind a checkbox. ~900 chars is ~10-11 rendered lines at
        // 11pt Arial, so a label lands near the top of essentially every page
        // (design 2026-08-03 section 8).
        var row = RowOf(("A", 0), ("B", 1000), ("C", 2000));   // existing fixture helper
        var chunks = TimestampCadence.Chunk(row, 0, maxChars: 2);

        Assert.True(chunks.Count > 1);
    }

    [Fact]
    public void Chunk_passes_a_payloadless_row_through_whole_even_with_maxChars()
    {
        // Live rows and legacy fixtures have no Segments. The whole-row chunk must carry row.Text
        // VERBATIM - SectionGrouper's null-payload merge means a Segments re-join is not
        // guaranteed to equal row.Text.
        var row = new DisplayRow { StartMs = 0, DisplayName = "Sam", Text = new string('x', 5000) };
        var chunks = TimestampCadence.Chunk(row, 0, maxChars: 10);

        Assert.Equal(row.Text, Assert.Single(chunks).Text);
    }
```

Add to `DocxRendererTests.cs`:

```csharp
    [Fact]
    public void Continuation_paragraphs_repeat_the_speaker_name_with_contd()
    {
        // A turn can run for pages with the name only at the top, so flipping to a page mid-turn
        // left the reader with no attribution. "(cont'd)" sits OUTSIDE the styled name run so the
        // running head shows the name alone (design 2026-08-03 sections 3, 8).
        var h = new TranscriptHeader("Long Turn", "Teams", Started, 600000, "small.en", "CUDA");
        var v = new SessionTextView("Long Turn", [], ["Sam"], Started, Started.AddMinutes(10),
            600000, "Teams", "", null);
        var segments = Enumerable.Range(0, 40)
            .Select(i => new RowSegment(i, TranscriptSource.Local, i * 1000L, i * 1000L + 900,
                new string('w', 100), new string('w', 100), false, false))
            .ToList();
        var rows = new[]
        {
            new DisplayRow
            {
                StartMs = 0, DisplayName = "Sam",
                Text = string.Join(" ", segments.Select(s => s.ProjectedText)),
                Segments = segments,
            },
        };

        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v, new ExportProvenance(), rows, "relative",
            DocxPageSize.A4, new DocxOptions());
        using var doc = Open(ms.ToArray());

        var contd = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Where(p => p.InnerText.Contains("(cont'd)", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(contd);

        // The name run is styled; "(cont'd):" is not - otherwise the running head would read
        // "SAM (CONT'D)".
        var styled = contd[0].Elements<Run>()
            .Where(r => r.RunProperties?.GetFirstChild<RunStyle>()?.Val?.Value == "TranscriptSpeaker");
        Assert.Equal("Sam", Assert.Single(styled).InnerText);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~TimestampCadenceTests|FullyQualifiedName~DocxRendererTests"`
Expected: FAIL to compile — `Chunk` takes two parameters.

- [ ] **Step 3: Add the second trigger**

Replace `TimestampCadence.Chunk`:

```csharp
    /// <summary>Splits a grouped DisplayRow into export chunks at segment boundaries. Two
    /// independent triggers, whichever fires first (design 2026-08-03 section 8):
    /// intervalMs of wall time since the last shown stamp (the 2026-08-02 item 5 cadence, still
    /// behind the export dialog's checkbox), or maxChars of accumulated text (ALWAYS on - it is
    /// what guarantees a (cont'd) label near the top of nearly every page, which is a correctness
    /// property, not a preference). A row still passes through as ONE whole-row chunk when BOTH
    /// triggers are off, the row is a marker, the row has no Segments payload (live rows, legacy
    /// fixtures), or no boundary crosses either threshold. The whole-row chunk carries row.Text
    /// VERBATIM - never the Segments re-join - so uncadenced output stays byte-identical
    /// (SectionGrouper's null-payload merge means Segments-derived text is not guaranteed to equal
    /// row.Text). Split chunk text uses the single-space join byte-identical to
    /// SectionGrouper.cs:34.</summary>
    public static IReadOnlyList<CadenceChunk> Chunk(DisplayRow row, int intervalMs, int maxChars)
    {
        if ((intervalMs <= 0 && maxChars <= 0) || row.IsMarker || row.Segments.Count == 0)
            return [WholeRow(row)];

        var chunks = new List<CadenceChunk>();
        var current = new List<RowSegment>();
        long lastStampMs = row.StartMs;
        long chunkStampMs = row.StartMs;
        int chars = 0;
        foreach (var seg in row.Segments)
        {
            bool byTime = intervalMs > 0 && seg.StartMs - lastStampMs >= intervalMs;
            bool byLength = maxChars > 0 && chars > 0 && chars + seg.ProjectedText.Length > maxChars;
            if (current.Count > 0 && (byTime || byLength))
            {
                chunks.Add(Close(chunkStampMs, current));
                current = [];
                chunkStampMs = seg.StartMs;
                lastStampMs = seg.StartMs;
                chars = 0;
            }
            current.Add(seg);
            chars += seg.ProjectedText.Length;
        }
        chunks.Add(Close(chunkStampMs, current));
        return chunks.Count == 1 ? [WholeRow(row)] : chunks;
    }
```

- [ ] **Step 4: Give continuations a name in both renderers**

In `DocxRenderer`, add the constant beside `Disclaimer`:

```csharp
    /// <summary>Always-on continuation trigger (design 2026-08-03 section 8): ~10-11 rendered
    /// lines at 11pt Arial in the text column, so a (cont'd) label lands near the top of
    /// essentially every page. This is what makes the STYLEREF running head reliable.</summary>
    public const int ContinuationMaxChars = 900;
```

Replace the chunk loop in `Write`:

```csharp
            var chunks = TimestampCadence.Chunk(row,
                options.IncludeTimestamps ? options.TimestampIntervalMs : 0, ContinuationMaxChars);
            var label = TurnLabel(row, options, timestampsMode, header.StartedAtLocal);
            body.AppendChild(TurnParagraph(label, chunks[0].Text));
            for (int i = 1; i < chunks.Count; i++)
                body.AppendChild(TurnParagraph(
                    label with
                    {
                        Stamp = options.IncludeTimestamps
                            ? "[" + TimestampFormat.Stamp(chunks[i].StampMs, timestampsMode,
                                header.StartedAtLocal) + "] "
                            : "",
                        Suffix = " (cont'd):",
                    },
                    chunks[i].Text));
```

`TextColumnTwips` must now measure the widest *continuation* label too, since a named continuation is no longer narrower than the 1.5" floor:

```csharp
        int longest = 0;
        foreach (var row in rows)
            if (!row.IsMarker)
            {
                var label = TurnLabel(row, options, timestampsMode, startedAtLocal);
                // Continuation labels add " (cont'd)" to the same stamp+name, so they are the
                // widest form a row can produce (design 2026-08-03 section 8).
                longest = Math.Max(longest, label.Length + " (cont'd)".Length);
            }
```

Mirror the label change in `MarkdownRenderer.Write`, passing `DocxRenderer.ContinuationMaxChars` as the third `Chunk` argument and emitting `**[04:30] Sam (cont'd):**` (or `**Sam (cont'd):**` with timestamps off).

- [ ] **Step 5: Run the full Core suite**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests`
Expected: PASS. Existing cadence tests calling `Chunk(row, interval)` need `, 0` appended to keep asserting the time trigger in isolation.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(export): (cont'd) labels keep a long turn attributed across pages

Second, always-on chunk trigger at 900 chars alongside the existing 15s cadence.
A turn could previously run for pages with the speaker named only at the top.
(cont'd) sits outside the styled name run so the running head shows the name
alone (design 2026-08-03 section 8)."
```

---

### Task 10: In-progress banner

**Files:**
- Modify: `src/LocalScribe.Core/Projection/DocxRenderer.cs` (metadata block + header part)
- Modify: `src/LocalScribe.Core/Projection/MarkdownRenderer.cs` (metadata block)
- Test: `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`, `tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs`

**Interfaces:**
- Consumes: `ExportProvenance.InProgress` (Task 3), the header part (Task 6).
- Produces: `public const string InProgressNotice` on `DocxRenderer`, shared by both renderers exactly as `Disclaimer` already is.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void In_progress_export_is_labelled_in_the_block_and_on_every_page()
    {
        // Exported mid-recording the document is materially weaker than the same session after
        // Stop: diarisation has not run, so speakers are the generic Local/Remote split, and the
        // transcript is incomplete. Every page says so - the header covers pages 2+, the metadata
        // block covers page 1 (where the header is suppressed) (design 2026-08-03 section 11).
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions(),
            new ExportProvenance { InProgress = true });
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;

        Assert.Contains(DocxRenderer.InProgressNotice, main.Document!.Body!.InnerText);

        string defaultId = main.Document.Body!.GetFirstChild<SectionProperties>()!
            .Elements<HeaderReference>()
            .Single(h => h.Type!.Value == HeaderFooterValues.Default).Id!.Value!;
        Assert.Contains(DocxRenderer.InProgressNotice,
            ((HeaderPart)main.GetPartById(defaultId)).Header!.InnerText);
    }

    [Fact]
    public void Finalised_export_carries_no_in_progress_notice()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;

        Assert.DoesNotContain(DocxRenderer.InProgressNotice, main.Document!.Body!.InnerText);

        string defaultId = main.Document.Body!.GetFirstChild<SectionProperties>()!
            .Elements<HeaderReference>()
            .Single(h => h.Type!.Value == HeaderFooterValues.Default).Id!.Value!;
        Assert.DoesNotContain(DocxRenderer.InProgressNotice,
            ((HeaderPart)main.GetPartById(defaultId)).Header!.InnerText);
    }
```

Mirror the positive/negative pair in `MarkdownRendererWriteTests.cs` against the rendered string.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~DocxRendererTests|FullyQualifiedName~MarkdownRendererWriteTests"`
Expected: FAIL — `InProgressNotice` does not exist.

- [ ] **Step 3: Add the notice**

Beside `Disclaimer` in `DocxRenderer`:

```csharp
    /// <summary>Stamped on a session exported mid-recording (design 2026-08-03 section 11). Shared
    /// with MarkdownRenderer exactly as Disclaimer is, so the two can never word it differently.</summary>
    public const string InProgressNotice =
        "IN-PROGRESS RECORDING \u2014 transcript incomplete, speaker separation not yet applied.";
```

Render it in the metadata block, immediately before `DisclaimerLine()`:

```csharp
        if (provenance.InProgress) body.AppendChild(InProgressLine());
```

```csharp
    /// <summary>Bold, above the disclaimer. Page 1 suppresses the running head, so this line is
    /// what covers page 1; the header covers pages 2+.</summary>
    private static Paragraph InProgressLine()
        => new(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Bold()), MakeText(InProgressNotice)));
```

- [ ] **Step 4: Add the header line**

The header part becomes a two-paragraph `Header` when `provenance.InProgress`. Wrap the existing header paragraph construction and prepend:

```csharp
        var headerParagraphs = new List<Paragraph>();
        if (provenance.InProgress)
            headerParagraphs.Add(new Paragraph(
                new Run(new RunProperties(new Bold()), MakeText(InProgressNotice))));
        headerParagraphs.Add(/* the existing matter/date/STYLEREF paragraph */);
        headerPart.Header = new Header(headerParagraphs.ToArray());
```

- [ ] **Step 5: Mirror in markdown**

In `MarkdownRenderer.Write`, before the disclaimer line:

```csharp
        if (provenance.InProgress)
            sb.Append('\n').Append("**").Append(DocxRenderer.InProgressNotice).Append("**").Append('\n');
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(export): label a mid-recording export on every page

Exported before Stop the transcript is incomplete and diarisation has not run, so
speakers are the generic Local/Remote split. The header covers pages 2+; the
metadata block covers page 1, where the header is suppressed
(design 2026-08-03 section 11)."
```

---

### Task 11: `TranscriptStore` read must not lock out the writer

**This is a prerequisite for Task 12, not a nice-to-have.** `File.ReadAllLinesAsync` opens with `FileShare.Read`, which permits other readers but **excludes writers**. The live capture pipeline appends via `File.AppendAllTextAsync`, which needs write access. Once a live session can be exported, a concurrent append can fail with `IOException` and drop an evidentiary transcript line.

**Files:**
- Modify: `src/LocalScribe.Core/Storage/TranscriptStore.cs:26-42`
- Test: `tests/LocalScribe.Core.Tests/TranscriptStoreTests.cs` (create if absent)

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task Read_succeeds_while_a_writer_holds_the_file_for_append()
    {
        // File.ReadAllLinesAsync opens FileShare.Read - other READERS are fine, writers are locked
        // out. Adding a live-session export made that reachable: an append landing during an
        // export would throw and lose an evidentiary line (design 2026-08-03 section 11).
        // NeedsNewlinePrefix in this same file already uses FileShare.ReadWrite.
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "transcript.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new TranscriptStore(path);
        await store.AppendAsync(new TranscriptLine { Seq = 0, Text = "one" }, default);

        // Hold the file exactly as File.AppendAllTextAsync does.
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

        var lines = await store.ReadAllAsync(default);
        Assert.Single(lines);
    }
```

> Build the `TranscriptLine` with whatever required members that record actually has — read `src/LocalScribe.Core/Model/` for its shape before writing this.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~Read_succeeds_while_a_writer_holds"`
Expected: FAIL with `IOException: The process cannot access the file ... because it is being used by another process.`

- [ ] **Step 3: Open the read path with `FileShare.ReadWrite`**

In `ReadAllDetailedAsync`, replace `File.ReadAllLinesAsync(_path, ct)` with an explicit stream, mirroring `NeedsNewlinePrefix` at `:57`:

```csharp
    public async Task<TranscriptReadResult> ReadAllDetailedAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new TranscriptReadResult(Array.Empty<TranscriptLine>(), 0);
        var lines = new List<TranscriptLine>();
        int malformed = 0;
        // FileShare.ReadWrite, NOT File.ReadAllLinesAsync (design 2026-08-03 section 11): that
        // helper opens FileShare.Read, which locks out the live capture pipeline's append and
        // would drop an evidentiary line whenever an export ran against a recording session.
        // NeedsNewlinePrefix below already opens this way.
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        while (await reader.ReadLineAsync(ct) is { } raw)
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests`
Expected: PASS, including every existing torn-tail test — the tolerance behaviour is unchanged, only the sharing mode.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(storage): transcript reads must not lock out the live appender

File.ReadAllLinesAsync opens FileShare.Read, which excludes writers. Latent until
now because nothing read a recording session; the live export button makes it
reachable, where a concurrent append would throw and drop an evidentiary line."
```

---

### Task 12: Export buttons in the read view and the Record console

**Files:**
- Modify: `src/LocalScribe.App/App.xaml.cs` (hoist `openExport`, thread it through)
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml:117`, `ReadViewWindow.xaml.cs`
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml:319-326`, `LiveViewWindow.xaml.cs:38-44`
- Modify: `src/LocalScribe.App/TrayIconHost.cs:115`

**Interfaces:**
- Consumes: `ExportDialogViewModel` and `ExportDialog`, both **unchanged**.
- Produces: `openExport` as `Action<string, string>` (sessionId, title) instead of `Action<string>`.

- [ ] **Step 1: Generalise and hoist the export factory**

`openExport` is defined at `App.xaml.cs:715-721`, but `openReadView` constructs `ReadViewWindow` at `:521` — earlier in the method. Hoist `openExport` above `openReadView`, exactly as the file already does for `openSessionDetails` (see the comment at `:708-710`, which records the same hoist for the same reason).

Change its shape so callers without a cached row can supply their own title:

```csharp
        // Export dialog (Task 9, design 3.4; entry points widened 2026-08-03): a fresh VM + plain
        // Window per request. Hoisted above openReadView so the read view and the Record console
        // can both close over it - the openSessionDetails precedent at the read-view wiring below.
        Action<string, string> openExport = (sessionId, title) =>
        {
            var exportVm = new ViewModels.ExportDialogViewModel(sessionId, title, comp.Maintenance,
                pickSavePath, revealFile, errors, dispatch);
            new ExportDialog(exportVm) { Owner = MainWindow }.ShowDialog();
        };
```

At `:722`, the Sessions-page subscription keeps its own title lookup:

```csharp
        sessionsVm.ExportRequested += sessionId =>
            openExport(sessionId, sessionsVm.Rows.FirstOrDefault(r => r.Id == sessionId)?.Title ?? sessionId);
```

- [ ] **Step 2: Add the read-view button**

In `ReadViewWindow.xaml`, after the `Ask` toggle at `:117-120`:

```xml
                <Button Content="Export..." Click="OnExport" Margin="0,0,8,4"
                        ToolTip="Export this transcript as .docx, .md or a .zip archive" />
```

In `ReadViewWindow.xaml.cs`, add the field beside `_openSessionDetails` (`:44`), assign it in the constructor, and add the handler beside `OnManageSpeakers` (`:304`):

```csharp
    private readonly Action<string, string> _openExport;
```

```csharp
    /// <summary>Export from the transcript you are already reading (design 2026-08-03 section 10).
    /// Reuses the SAME dialog the Sessions page opens - the session is always finalised here, so
    /// there is no live-export handling on this path.</summary>
    private void OnExport(object sender, RoutedEventArgs e) => _openExport(_sessionId, _vm.Title);
```

> Confirm `ReadViewViewModel` exposes a `Title`. If it does not, pass `_sessionId` for both arguments — `ExportDialogViewModel` only uses the title to seed the Save-As filename.

Update the `ReadViewWindow` constructor signature and the `new ReadViewWindow(...)` call at `App.xaml.cs:521-522` to pass `openExport`.

- [ ] **Step 3: Add the console button**

In `LiveViewWindow.xaml`, after the `Compact` button at `:319-326`:

```xml
                    <!-- design 2026-08-03 section 10: export without leaving the live transcript.
                         Deliberately NOT on the compact pill, which is a minimal always-on-top
                         surface. The exported document labels itself as an in-progress recording. -->
                    <Button Style="{StaticResource PillButton}" Click="OnExport"
                            ToolTip="Export this transcript so far">
                        <StackPanel Orientation="Horizontal">
                            <ui:SymbolIcon Symbol="ArrowExport24" FontSize="18" Margin="0,0,6,0" />
                            <TextBlock Text="Export" VerticalAlignment="Center" />
                        </StackPanel>
                    </Button>
```

In `LiveViewWindow.xaml.cs`, add an `Action<string, string>? openExport` parameter to the constructor (`:38-39`), store it, and add:

```csharp
    /// <summary>Export mid-recording (design 2026-08-03 section 10). The session id is the
    /// controller's current one; the console holds no title, so the id seeds the Save-As filename -
    /// the same fallback openExport already uses when a Sessions row has dropped out of the cache.
    /// No-ops when nothing is recording (the button is only reachable in Recording/Paused).</summary>
    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (DataContext is LiveViewContext ctx && ctx.Session.CurrentSessionId is { } id)
            _openExport?.Invoke(id, id);
    }
```

Thread the callback through `TrayIconHost.cs:115` (`new LiveViewWindow(_session, _lines, _console, _settingsService, _windowState, _openExport)`) and give `TrayIconHost` the field to carry it, set from `App.xaml.cs` wherever `TrayIconHost` is constructed.

> `ArrowExport24` may not exist in this `Wpf.Ui` version's `SymbolRegular` enum. If the build fails on it, use `Save24` or `Share24` — verify against the enum rather than guessing.

- [ ] **Step 4: Build and run the whole suite**

Run: `dotnet test F:\LocalScribe\LocalScribe.slnx`
Expected: PASS. Close `App.exe` first or the build fails with `MSB3027`.

- [ ] **Step 5: Manual smoke (cannot be unit-tested — WPF windows)**

1. Start a recording, let a few lines land, click **Export** in the console, choose `.docx`. Confirm the document opens, carries the `IN-PROGRESS RECORDING` line in the metadata block and on page 2's header, and that the recording keeps appending lines afterwards.
2. Stop, open the read view, click **Export...**, choose `.docx`. Confirm no in-progress notice, Arial throughout, `Page 1 of N` in the footer, the running head naming the speaker on a page that starts mid-turn, and `(cont'd)` labels inside a long turn.
3. Export the same session as `.md`. Confirm no trailing `---` footer block and that `(cont'd)` labels are present.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(export): export buttons in the read view and the Record console

Export was reachable only from the Sessions page, so both transcript surfaces
forced a navigate-away. Both reuse the existing dialog unchanged; openExport is
hoisted and widened to (sessionId, title) so callers without a cached row can
supply their own (design 2026-08-03 section 10)."
```

---

## Self-Review

**Spec coverage.** Section 1 → Task 3. Section 2 → Task 3. Section 3 → Tasks 5, 6. Section 4 → Tasks 4, 5. Section 5 → Task 7. Section 6 → Tasks 1, 8. Section 7 → Task 2. Section 8 → Task 9. Section 9 → Tasks 3, 8, 9, 10 (parity handled per-behaviour, by design). Section 10 → Task 12. Section 11 → Tasks 10, 11. No gaps.

**Type consistency.** `ExportProvenance` (Task 3) is consumed with the same property names in Tasks 8 and 10. `TurnLabelParts` (Task 5) is consumed by Task 9's `label with { Stamp = ..., Suffix = ... }`. `MetadataFormat.DateLine` (Task 1) is consumed in Task 8. `TimestampCadence.Chunk`'s third parameter (Task 9) is fed `DocxRenderer.ContinuationMaxChars`, defined in the same task. `openExport` is `Action<string, string>` at every call site in Task 12.

**Known ordering constraint.** Task 5 changes `TurnParagraph`'s signature and Task 9 changes its call sites again; Task 5's Step 5 keeps the continuation call site compiling in between. Task 6 must follow Task 5 (the running head reads the style Task 5 creates). Task 12 must follow Task 11.
