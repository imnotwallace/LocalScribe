# Transcript export: export scope & the dialog (Round 2)

Date: 2026-08-04
Status: designed, awaiting implementation plan
Follows: `docs/superpowers/specs/2026-08-03-transcript-export-document-design.md`
(Round 1, merged `d753ea0`), whose "Round 2 backlog" section is the input to this spec.

## Problem

Round 1 reshaped *what an exported transcript looks like*. Nothing it did touched
*what you can choose to export, or how the dialog behaves*. That surface has six
gaps:

- There is no `.txt` export. The three formats are Zip, `.docx`, `.md`.
- `ExportDialogViewModel` resets format and every toggle each time the dialog
  opens, so a user who exports twenty sessions as timestamped `.docx` re-picks
  those choices twenty times.
- The extra-timestamp cadence is hardcoded: `private const int CadenceIntervalMs
  = 15000` (`ExportDialogViewModel.cs:32`), with "every 15 seconds" spelled out in
  the XAML label.
- The default filename is `ExportFileNames.Sanitize(title)` and nothing else. A
  user filing by matter or date renames every export by hand.
- There is no way to export part of a transcript.
- The assistant's summary reaches no export. `SessionTextView.Summary` is
  hardcoded `null` at `SessionProjectionLoader.cs:107`.

## Correction of record: `SummaryRef` is dead, not unwired

Round 1's backlog recorded the summary gap as "needs `SummaryRef` wiring", on the
basis that `SessionMeta` already carries `SummaryRef` / `SummaryGeneratedAtUtc` /
`SummaryModel` (`SessionMeta.cs:25-27`). That is not what those fields are.

They are written by **nobody**. The only reference outside the declaration is
`SessionMigrator.cs:74`, which sets `SummaryRef = null`. They are vestigial,
superseded by `assistant\summaries.json` behind `SummaryStore` — a versioned,
append-only file whose `SummaryVersion` carries `ContentMarkdown`, `Stale`,
`SourceTranscriptVersion`, `Model` (file + sha256 + backend actually used) and
`CudaFellToCpu`. That store is what the Assistant tab, the Sessions/Matters
summary-status columns and the matter-summary context builder all read.

So item 7 is not a plumbing job on `SessionMeta`. It is a content decision: which
version an export gets, and what a stale one does.

The three dead fields are **left in place**. Removing them changes `meta.json`'s
written shape for no benefit in this round; they gain an XML-doc note pointing at
`SummaryStore` as the truth.

## Decisions

All user-approved during brainstorming on 2026-08-04:

1. Excerpt export ships as **time range only**. The single-speaker variant is
   dropped: removing one side of a conversation produces a document that reads as
   complete, which is the genuinely misleading shape. A contiguous window does not.
2. Excerpt carries a mandatory `EXCERPT — not the complete transcript.` banner on
   every page, per the locked no-content-deletion rule.
3. The matter-level combined transcript (Round 1 backlog item 2) is **deferred to
   Round 3**. It is the only backlog item that is not the session export dialog,
   and it inherits every decision the other six make — which formats exist, what
   the filename template is, whether summaries ride along, whether excerpt applies.
4. Round 2 is the remaining six, in this order: `.txt` + remembered choices +
   cadence knob (one pass over `ExportDialogViewModel`), then filename template,
   then summary section, then time-range excerpt last.
5. `.txt` uses CRLF and never hard-wraps.
6. The cadence knob is a preset list, not free numeric entry.
7. The filename template lives on the Settings page, not in the export dialog.
8. The summary is opt-in, default OFF.

## Scope

In scope: `.txt` export; remembered export choices; a cadence-interval knob; a
filename template; an opt-in assistant-summary section; time-range excerpt export.

Out of scope, deferred to Round 3: the matter-level combined transcript
(`ExportMatterArchiveAsync`, `MaintenanceService.cs:1062`, still produces a `.zip`
of raw session folders rather than a readable bundle).

Never in scope: single-speaker excerpt export (decision 1); removing
`SessionMeta.SummaryRef` and its two siblings; PDF rendering (Round 1, permanent);
hashing recorded-session audio at export time (Round 1, permanent).

## Design

### 1. Where each concern attaches

Round 1's `ExportProvenance.InProgress` already solved the problem excerpt has —
stamp a completeness caveat on every page — and the mechanism is reused rather
than reinvented. In `DocxRenderer` (`:160-194`) that is:

- a bold notice paragraph **prepended** to the default header part, ahead of the
  matter/date/`STYLEREF` running-head paragraph, which is otherwise untouched;
- page 1 carries an intentionally **empty** first-page header part, so page 1's
  copy of the notice is the metadata-block line instead.

"Every page" therefore means *metadata block on page 1, header notice on pages 2+*.
Excerpt gets exactly this treatment, and when a session is both mid-recording and
excerpted the two notice paragraphs stack in that header.

The resulting boundaries, which every task in this round must respect:

- **Renderers stay pure serializers.** Row filtering for an excerpt happens in
  `MaintenanceService` before the render call. A renderer learns only *that* the
  document is an excerpt and what span it covers.
- **`ExportProvenance` grows `ExcerptSpan`** (`string?`, null = complete
  transcript) — the rendered span label, e.g. `00:12:30-00:18:45 of 01:47:12`,
  composed in `MaintenanceService`. It is a fact about the document's completeness,
  the same category as `InProgress`, and it belongs beside it. It is deliberately
  named differently from the `ExcerptRange` *input* record (section 8): one is a
  millisecond window the service selects rows with, the other is a string the
  renderers print.
- **The summary is content, not provenance**, so it rides as its own
  `ExportSummary?` renderer parameter, composed in `MaintenanceService` next to
  `ProvenanceFor` (`:1042`) — the same place, for the same reason: only the
  service has both the loaded projection and the export-time inputs, so the
  renderers cannot disagree.

### 2. Foundation: `ExportOptions` and `ExportNotices`

Two mechanical renames, done first so nothing later has to reach across a bad
boundary.

`DocxOptions` becomes **`ExportOptions`**. Three renderers already share it; this
round adds a fourth. The record's own doc comment already calls it
"format-neutral" — the type name has been wrong since Markdown export shipped.

The shared text constants — `DocxRenderer.Disclaimer` and
`DocxRenderer.InProgressNotice` — move to a new **`ExportNotices`** static class in
`LocalScribe.Core.Projection`, joined by this round's two additions:

```
ExportNotices.Disclaimer          (moved, unchanged text)
ExportNotices.InProgressNotice    (moved, unchanged text)
ExportNotices.ExcerptNotice       "EXCERPT — not the complete transcript."
ExportNotices.SummaryHeading      "Assistant summary"
```

`ExcerptNotice` is written with the `—` escape exactly as shown, never a
literal em dash — the same rule that governs `InProgressNotice` today
(`DocxRenderer.cs:54`). The docx summary bullet is likewise `•`. See Traps.

`MarkdownRenderer` already reaches into `DocxRenderer` for two of these. With a
third and fourth renderer doing the same it stops being a quirk and becomes a bad
dependency. No forwarding constants are left behind: references are updated at the
call sites, tests included. `ContinuationMaxChars` stays on `DocxRenderer` — it is
a genuine docx page-geometry constant that Markdown borrows deliberately.

The text of the two moved constants does not change by one byte.

### 3. `.txt` export

`ExportFormat` gains `Text`. `PlainTextRenderer` gains a `Write(...)` sibling to
its existing save-time `Render(...)`, exactly as `MarkdownRenderer` did:

```
Write(TranscriptHeader header, SessionTextView meta, ExportProvenance provenance,
      ExportSummary? summary, IReadOnlyList<DisplayRow> rows, string timestampsMode,
      ExportOptions options) -> string
```

Same metadata block content rules as `MarkdownRenderer.Write`, rendered as
`Label: value` lines rather than a bullet list. Same non-optional disclaimer, same
in-progress and excerpt notices, same `TimestampCadence.Chunk` + `(cont'd)`
continuation labels, no decoration.

Rules specific to this format:

- **No hard wrapping.** One line per turn; the viewer wraps. Hard-wrapping would
  insert newlines into evidentiary text.
- **CRLF line endings**, unlike every other renderer. `.txt` is the format that
  gets pasted into Windows tooling and email. The save-time `Render(...)` ->
  `transcript.txt` path keeps `\n` and is **not touched**: its byte-identity is
  load-bearing (`SessionProjectionLoader` doc comment, and the
  `SessionProjectionLoaderTests` guard).
- **UTF-8 without BOM**, via `Encoding.UTF8.GetBytes` — the `ExportMarkdownAsync`
  precedent (`MaintenanceService.cs:1032`).
- **No line numbers and no footer.** `.txt` has no pages, so page:line citation
  does not exist there. This is the "give me the words" format.

`MaintenanceService.ExportTextAsync` is a line-for-line mirror of
`ExportMarkdownAsync`: session gate, `ExportWithOutputCleanupAsync`, document
rendered *before* the output stream opens so a projection failure leaves a
pre-existing Save-As target intact.

### 4. Remembered choices

One new additive record on `Settings`, following the `SectionGapMs` precedent
(`Settings.cs:22-26`) — `System.Text.Json` skips unmapped members, so existing v3
files load at the defaults and **no schema bump or migration is required**:

```csharp
public sealed record ExportSetting
{
    public ExportFormat Format { get; init; } = ExportFormat.Zip;
    public bool IncludeTimestamps { get; init; } = true;
    public bool IncludeMarkers { get; init; } = true;
    public bool ExtraTimestamps { get; init; }
    public int CadenceIntervalMs { get; init; } = 15000;
    public string FilenameTemplate { get; init; } = "{title}";
    public bool IncludeSummary { get; init; }
}
```

Every default reproduces today's behaviour exactly.

`ExportFormat` **moves from `LocalScribe.App.ViewModels` to
`LocalScribe.Core.Model`**. It is persisted domain state now, not a view-model
detail, and Core cannot reference App. It persists as a string via a
`JsonStringEnumConverter<ExportFormat>` registered in `LocalScribeJson`
(`:28-37`) — the house pattern that `AudioFormat`, `Backend`, `MicMode` and six
others already follow.

`ExportDialogViewModel` takes `ISettingsService` (it needs it for this item, the
cadence knob, the filename template and the excerpt-range timestamp mode) and:

- seeds `Format` and the three toggles from `settings.Current.Export` on
  construction;
- **persists on successful export only** — never on dialog open, never on cancel,
  never on a failed export. The semantic is "remember what you last actually did".
- A settings-save failure is reported through `IUiErrorReporter` but **never fails
  an export that already succeeded**, and never suppresses the success `Info` or
  the reveal.

Not remembered, deliberately: the excerpt checkbox and its range (section 8).

### 5. Cadence interval

`CadenceIntervalMs` stops being a `const` and becomes a bound preset choice:
**10 s / 15 s / 30 s / 60 s**, defaulting to 15 s. The XAML label becomes
`Extra timestamp every [15 s v]` — the hardcoded "15 seconds" text goes.

A preset list rather than free numeric entry: 1 s puts a stamp on every sentence
and 3600 s does nothing, and neither is worth a validation story. The chosen value
persists in `ExportSetting.CadenceIntervalMs`.

The existing subordination is preserved unchanged: the cadence choice rides
`IncludeTimestamps`, so unchecking timestamps forces the interval to 0 even while
the (disabled) cadence checkbox is still ticked.

An `ExportSetting.CadenceIntervalMs` loaded from disk that is not one of the four
presets stays the effective value and is used as-is; the dropdown displays the
nearest preset. Picking a preset replaces it, and the next successful export
persists that choice. The file is user-editable and a hand-typed 20000 must not be
silently rewritten to 15000 before the user has chosen anything.

### 6. Filename template

`ExportFileNames` gains `Expand(template, tokens)`, applied before the existing
`Sanitize`. Tokens:

| Token | Value |
|---|---|
| `{title}` | session title |
| `{date}` | session start, `yyyy-MM-dd` |
| `{time}` | session start, `HHmm` |
| `{matter}` | first matter display (reference if set, else name); empty when untagged |
| `{version}` | transcript version id (`v1`, `v2`, ...) |
| `{id}` | session id |

Expansion rules:

- An **unknown** `{foo}` is left **literal**. The user then sees their typo in the
  Save-As default name and fixes it; silently dropping it hides the mistake.
- An **empty** value expands to empty. Runs of space / `-` / `_` in the result then
  collapse to one and are trimmed from both ends, so `{matter}-{title}` on an
  untagged session yields `Title`, not `-Title`.
- An empty final result falls back to the existing `"export"`.
- `Sanitize` runs last, unchanged, so Windows-invalid characters are still replaced
  — this matters because `{matter}` commonly contains `/` (e.g. `2026/014`), which
  is exactly why `Sanitize` exists.

The template is edited on the **Settings page**, in an Export group, with a token
legend beside it. It is not in the export dialog: it is a set-once preference, and
the Save-As default name already *is* the live preview, so no extra dialog UI earns
its place.

The default `{title}` produces byte-identical filenames to today.

### 7. Summary section

**Source.** The latest version from `SummaryStore` — `versions[^1]`, the store
being append-only and newest-last. This is exactly how `App.xaml.cs:746-749`
(summary-status provider) and `App.xaml.cs:771-775` (matter summary sources)
already select one. Deliberately **not** the version selected in the Assistant
tab: the export dialog opens from the Sessions page and the Record console too,
where no tab exists.

**Plumbing.** `MaintenanceService` gets a settable seam, not a constructor
parameter:

```csharp
public Func<string, CancellationToken, Task<SummaryVersion?>>? LatestSummaryProvider { get; set; }
```

bound in `CompositionRoot` (`:92`) to the single composed `comp.Summaries`. Two
reasons. `MaintenanceService` is a primary-constructor class (`:33`) whose four
parameters are repeated in every test construction — a fifth breaks all of them.
And constructing a second `SummaryStore` violates the one-composed-store house
rule stated at `App.xaml.cs:766-770`. The precedents for a settable seam are on
this same class (`StartupScanTask`, `:56-60`) and in `SummaryStatusProvider`. A
null seam means "no summary", which is what every existing test gets for free.

**Opt-in, default OFF.** A checkbox in the toggles panel, remembered like the
others. The export is the document that leaves the building; attaching a
machine-written draft to it must be an act, not a default.

**Staleness is exported and labelled — never silently dropped, never silently
passed off as current.** Three states, resolved in `MaintenanceService` where both
`loaded.VersionId` and the summary version are in hand:

| Condition | Rendered notice |
|---|---|
| current | (none beyond the provenance line) |
| `Stale` flag set | `OUT OF DATE: the transcript changed after this summary was generated.` |
| `SourceTranscriptVersion != loaded.VersionId` | `Generated against transcript {v}; this document is {w}.` |

The second check is the one the `Stale` flag alone misses — a summary can be
un-stale against its own version while the export renders a different one — and
only the service can make it. When both hold, both are stated.

**The record passed to renderers:**

```csharp
public sealed record ExportSummary
{
    public string ContentMarkdown { get; init; } = "";
    /// "generated 2026-08-01 14:22, Qwen3-4B-Instruct-2507.gguf (CUDA)"
    /// (the model FILE, from AssistantModelRef.File - better provenance than the canonical name)
    public string ProvenanceLine { get; init; } = "";
    /// null when current; one or both notices from the table above.
    public string? StaleNotice { get; init; }
}
```

Composed in `MaintenanceService`, mirroring `ProvenanceFor`, so all three renderers
serialize the same decision.

**Rendered parity, not just source parity.** The heading, draft label, provenance
line and stale notice must each stand alone in a *rendered* view, not merely on
their own source lines. In CommonMark, consecutive non-blank lines are soft breaks
inside one paragraph, so single-newline separation collapses them into one run-on
line and buries the staleness warning mid-sentence. Markdown therefore separates
them with blank lines (the same reason this renderer's metadata block already uses
a bullet list), matching `.txt`'s three CRLF lines and `.docx`'s three paragraphs.
*(Added 2026-08-04 after the Task 9 review; the original spec text specified only
source-line placement and the plan's snippet collapsed under it.)*

**Placement and heading.** Its own section after the metadata block and before the
transcript, headed **"Assistant summary"** — not "Summary", because the content's
own first section is literally `## Summary` (`AssistantPrompts.SectionHeaders`,
`:23-24`) and the two would collide. Immediately under the heading, the locked
`AssistantPrompts.DraftLabel` (`:16`), referenced as the constant and **never
re-worded**, then the provenance line, then the stale notice in bold when present.

**The docx trap.** Every paragraph of the summary section — heading, draft label,
provenance, stale notice, and all content paragraphs — carries
`SuppressLineNumbers`. Round 1's line numbering counts transcript content only, and
the metadata block already suppresses for exactly this reason
(`DocxRenderer.cs:96`). Miss it and inserting a summary silently renumbers the
entire transcript, invalidating every page:line citation into a document that looks
unchanged.

**Markdown content in a Word document.** `ContentMarkdown` goes in verbatim for
`.md` and `.txt`. For `.docx`, a deliberately minimal **line-level** transform:

- `#{1,6} text` -> bold paragraph
- a line starting `- ` or `* ` -> paragraph with a `•` bullet and a hanging indent
- anything else -> plain paragraph

**No inline parsing.** `**bold**` stays literal. `AssistantPrompts.BuildSummaryPrompt`
prescribes exactly four `##` headers with bullet bodies, so line-level handles the
real output shape; a half-working inline parser is worse than none, and the limit
is documented rather than left as a mystery.

**A summary inside an excerpt is labelled, not suppressed.** `IncludeSummary` and
the excerpt range are orthogonal, so a user can tick both — and the result is a
document banner-stamped `EXCERPT — not the complete transcript.` whose front
matter carries a summary generated over the *entire* transcript. Left alone, a
reader cannot tell whether the summary describes the excerpt or the session.

When `ExcerptSpan` is set and a summary is present, the summary block therefore
gains one further sentence, as a locked `ExportNotices` constant so the three
renderers cannot word it differently:

`Summarises the complete transcript, not this excerpt.`

It is **independent of `StaleNotice`**: a current summary in an excerpt still gets
it, and a stale one gets both. This follows the same rule staleness does — exported
and labelled, never silently passed off. *(Added 2026-08-04 by user ruling after
the whole-branch review; neither per-task review could see the seam, because the
summary and the excerpt were built by different tasks. Round 3's matter-level
bundle inherits this decision.)*

**`SessionTextView.Summary` stays `null`.** Populating it in
`SessionProjectionLoader` would make `session.txt` vary with assistant state and
require regeneration whenever a summary is generated — coupling the neutral,
app-independent projection to the assistant, which is precisely what that record's
doc comment forbids. The summary is export-only. `SessionTextRenderer`'s
`Summary: (none)` line is therefore unchanged, and session.txt's bytes do not move.

### 8. Time-range excerpt

**Selection never truncates a turn.** A row is included when it overlaps the
requested range (`row.StartMs < toMs && rowEndMs > fromMs`); whole rows only. The
exported span therefore snaps **outward** to turn boundaries, and the document
reports **the actual span, not the requested one**. Reporting the requested range
over outward-snapped content would be a small lie in an evidentiary document.
Markers inside the span are included subject to `IncludeMarkers` as usual.

**Banner.** `ExportNotices.ExcerptNotice`, rendered through the `InProgress`
machinery described in section 1: metadata-block line on page 1, bold header
paragraph on pages 2+, stacking with the in-progress notice when both apply.
Markdown and `.txt` have no pages, so the single metadata-block line is the whole
notice there — the parity `MarkdownRenderer` already keeps for `InProgressNotice`
(`MarkdownRenderer.cs:63-67`).

Plus a metadata line stating the real span:

```
Excerpt: 00:12:30-00:18:45 of 01:47:12
```

**Four safeguards, all deliberate:**

1. **Timestamps are forced on** when a range is set; the checkbox disables.
   Timestamps are the anchor that maps an excerpt back to the full transcript.
   Line numbers restart within the excerpt and do **not** map back, so an excerpt
   with no timestamps would be uncitable.
2. **The filename gets a forced `-excerpt` suffix**, outside template control. A
   file named identically to the full transcript is precisely how an excerpt gets
   filed as one.
3. **Never remembered.** The checkbox and both range boxes reset on every dialog
   open, even though format and toggles persist. A remembered range would silently
   emit a partial export of the next, unrelated session.
4. **A range selecting zero rows is refused** with a clear error, not written as an
   empty document.

**Not offered for Zip**, which archives the session folder as-is; the range
controls hide for Zip exactly as the toggles panel already does.

**Parsing and validation live in `MaintenanceService`, not the view model.** The VM
has only `sessionId` and `sessionTitle` — it has neither the session's local start
(needed for wallclock mode) nor its duration (needed for bounds). Rather than teach
it to load a projection, a new pre-flight runs **before** the Save-As picker so the
user learns about a bad range before choosing a destination:

```csharp
/// The millisecond window the service selects rows with. Distinct from
/// ExportProvenance.ExcerptSpan, which is the printed label (section 1).
public sealed record ExcerptRange(long FromMs, long ToMs);

public Task<ExcerptRange> ResolveExcerptAsync(string sessionId, string fromText,
                                              string toText, CancellationToken ct)
```

It loads under the same session gate, parses both strings with the existing
`TimestampParser.TryParse` against `settings.Current.Timestamps` and the session's
own `startedLocal`, and throws `InvalidOperationException` with a readable message
on unparseable input, `from >= to`, a range outside the session duration, or a
range that selects zero rows. Empty `from` means start, empty `to` means end. The
dialog's existing `catch (Exception ex) { _errors.Report("Export", ex); }` surfaces
it unchanged.

This keeps one parsing implementation, in the only place that holds the truth, and
makes it directly unit-testable without a VM.

The pre-flight and the export itself are two separate gate acquisitions, so the
projection is loaded twice. That is accepted: the resolved range is a pair of
millisecond offsets, which stays meaningful against a transcript that grew between
the two loads (a live session), and the export always re-derives its rows from its
own fresh load rather than caching the pre-flight's. The alternative — holding the
gate across the Save-As dialog — would block the capture pipeline on a modal
window, which is not acceptable.

**Range entry is two plain `TextBox`es**, deliberately *not* the read view's
auto-colon masked go-to box. That mask carried the unpadded-paste defect where
`1:02:03` normalised to ten hours (UX round 2026-08-02 item 8). `TimestampParser`
itself handles `1:02:03` correctly; the defect was in the mask, so this design uses
the parser and skips the mask.

### 9. Dialog layout

The dialog stays a plain `Window` with a WPF-free VM. Final shape:

```
Format
  ( ) Zip archive (audio + transcript + metadata)
  ( ) Word document (.docx transcript)
  ( ) Markdown (.md transcript)
  ( ) Plain text (.txt transcript)                     <- new

  [x] Include timestamps                               } hidden for Zip
    [x] Extra timestamp every [15 s v]                 }  (ShowOptionToggles)
  [x] Include system markers                           }
  [ ] Include assistant summary                        } <- new, default off

  [ ] Export a time range only                         } <- new
      From [        ]  To [        ]                   }  revealed when ticked

                                   [ Export... ]  [ Cancel ]
```

`ShowOptionToggles` already generalises the old `IsDocx` gate to "both textual
formats"; it becomes "all three textual formats" with the addition of `Text`. The
`IsDocx` property is kept, unbroken, as Round 1 left it.

## Testing

Existing suites needing updates: everything referencing `DocxOptions` (rename),
`DocxRenderer.Disclaimer` / `.InProgressNotice` (moved to `ExportNotices`),
`ExportDialogViewModelTests` (new constructor parameter), `SettingsTests`
(new `Export` section in the v3 round-trip).

New coverage, one test per behaviour:

**Foundation / `.txt`**
- `ExportNotices` constants are byte-identical to the `DocxRenderer` values they
  replaced (a pinning test, so the move cannot reword them).
- `PlainTextRenderer.Write` emits the metadata block, disclaimer, and turns with no
  markdown decoration; `Render` (save-time) output is unchanged.
- `.txt` export uses CRLF; `transcript.txt` save-time output still uses `\n`.
- `.txt` export writes UTF-8 with no BOM.
- A long turn splits into `(cont'd)` chunks in `.txt` at the same boundaries as
  `.md` for identical inputs.

**Preferences**
- The VM seeds format and all four toggles from `settings.Current.Export`.
- A successful export persists the choices; a cancelled Save-As does not; a failed
  export does not.
- A settings-save failure is reported but the export still reports success and
  reveals the file.
- `Settings` round-trips the new `Export` section; a v3 file *without* it loads at
  the documented defaults (field-absence semantics).
- A non-preset `CadenceIntervalMs` loaded from disk stays the effective value.

**Filename template**
- Each token expands; `{matter}` is empty on an untagged session.
- An unknown token stays literal.
- `{matter}-{title}` on an untagged session collapses the leading separator.
- A template expanding to empty falls back to `"export"`.
- `Sanitize` still runs last: a `{matter}` of `2026/014` yields `2026_014`.
- The default `{title}` template reproduces the pre-Round-2 filename exactly.

**Summary**
- The latest version is chosen from a multi-version store, not the first.
- A null `LatestSummaryProvider` yields no summary section and no crash.
- `IncludeSummary = false` yields no summary section even when one exists.
- The `Stale` flag renders the out-of-date notice.
- A `SourceTranscriptVersion` differing from the rendered version renders the
  version-mismatch notice; both conditions together render both.
- `AssistantPrompts.DraftLabel` appears verbatim above the content in all three
  textual formats.
- **Every summary paragraph in the `.docx` carries `SuppressLineNumbers`**, and the
  transcript's own line numbering is identical with and without a summary.
- The docx line-level markdown transform: `## x` bolds, `- x` bullets, `**x**`
  stays literal.
- `SessionTextView.Summary` is still `null` after a summary export, and
  `session.txt` bytes are unchanged.

**Excerpt**
- Whole-row overlap selection: a row straddling the `from` boundary is included
  whole and its text is byte-identical to `row.Text`.
- The reported span is the outward-snapped actual span, not the requested one.
- `ExportNotices.ExcerptNotice` renders in the page-1 metadata block and in the
  pages-2+ header part; a complete-transcript export renders neither.
- Excerpt and in-progress together produce two stacked header paragraphs ahead of
  the running head, in that order.
- Timestamps are forced on for an excerpt even when the toggle was unchecked.
- The filename carries `-excerpt` regardless of template.
- The excerpt checkbox and range are not persisted and are clear on a fresh VM.
- `ResolveExcerptAsync` throws on unparseable input, on `from >= to`, on a range
  past the session duration, and on a range selecting zero rows; empty `from`/`to`
  mean start/end.
- Wallclock mode parses against the session's own local start.

**Schema**
- The `OpenXmlValidator` check in `DocxRendererTests` runs against a document
  carrying a summary **and** an excerpt banner **and** an in-progress notice at
  once. Three stacked header paragraphs plus a new body section is the shape most
  likely to trip Word's `pPr` child ordering, and the SDK will accept an invalid
  order silently.

## Traps carried from Round 1

These bit Round 1 and every one of them is live again in this round:

- **ASCII source files.** Non-ASCII in string literals must be `\u` escapes — this
  round adds `ExcerptNotice` (em dash) and the docx bullet (`•`). The Edit tool
  silently converts escapes to literal glyphs; byte-scan every touched file before
  committing (zero bytes > 127, CRLF intact). This bit seven separate tasks in
  Round 1.
- **Word paragraph-property children are schema-ordered**:
  `widowControl(6) -> pBdr(9) -> tabs(11) -> spacing(22) -> ind(23)`. The OpenXML
  SDK accepts any order and tests pass; Word calls the file corrupt. Microsoft
  Learn's `pPr` pages list children **alphabetically**, which is not schema order.
  Use the XSD. The summary's bulleted paragraphs need `ind` and this is exactly
  where it bites.
- **`STYLEREF` takes the style NAME, not the styleId** — `"Transcript Speaker"`.
  Not touched by this round, but any header work is next to it.
- **`FileShare.Read` excludes writers.** Any new read path over a session folder
  must use `FileShare.ReadWrite`, or reading a live session can fail the capture
  pipeline's append and drop an evidentiary transcript line.
- **Transcripts are legal evidence.** No path may drop, reorder, or silently
  rewrite content. `TimestampCadence.Chunk` must keep returning `row.Text` verbatim
  for an unsplit row. The excerpt selector filters whole rows and never edits one.
- **Stage files by name.** Never `git add -A` / `git add .` / `git commit -a`,
  never `git clean` — `tools/diar-eval/`, `.ai-code-review/` and `.claude/` are
  deliberately untracked.

## Round 3 backlog

Matter-level combined transcript: `ExportMatterArchiveAsync`
(`MaintenanceService.cs:1062`) produces a `.zip` of raw session folders rather than
a readable bundle. It inherits this round's format set, filename template, summary
decision and excerpt rules, and should be designed against them once they have been
through a smoke pass.
