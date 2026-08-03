# Transcript export: the exported document (Round 1)

Date: 2026-08-03
Status: designed, awaiting implementation plan
Supersedes: the footer/participant/typography decisions in
`docs/plans/2026-07-07-stage-6.3-export-plan.md` and
`docs/plans/2026-07-19-markdown-export-plan.md`

## Problem

The exported `.docx`/`.md` transcript reads as a machine dump rather than a usable
legal document:

- The footer carries `PRIVILEGED & CONFIDENTIAL` plus, on re-transcribed sessions,
  a model description. Neither is wanted; the transcript's own name is absent.
- Participants render as `Sam (Local)`, `Bob (Counsel, Remote)`. The capture side is
  an implementation detail and means nothing to a reader.
- Word renders the document in Times New Roman. This was never chosen: `AddStyles`
  pins a font *size* but no face, and there is no theme part, so Word falls back.
- Turns are back-to-back paragraphs with zero spacing.
- A turn can run for pages with the speaker named only at the top, so flipping to a
  page mid-turn leaves the reader with no attribution.
- Export is reachable only from the Sessions page. Both transcript surfaces (the
  Record console's live view and the read view) force a navigate-away.

## Decisions

All user-approved during brainstorming on 2026-08-03:

1. Footer is exactly `{transcript name}` + page number. Version/model provenance
   moves **up into the first-page metadata block**, not deleted.
2. Participants drop the Local/Remote side tag. Role is kept when set.
3. Arial replaces the Times New Roman fallback.
4. Speaker continuity across pages is solved **twice over**: in-body `(cont'd)`
   labels *and* a Word running head. The labels are the correctness mechanism; the
   running head is the convenience.
5. Export buttons are added to **both** the Record console and the read view.
6. PDF export is out of scope permanently — Word exports to PDF natively.

## Scope

In scope (this spec): everything about what an exported transcript looks like, plus
the two new entry points and the live-export safety work they require.

Out of scope, deferred to Round 2 (`export scope & dialog`): `.txt` export,
matter-level combined transcript, excerpt export, remembering export choices, a
cadence-interval knob, filename templates, and the summary section (which needs
`SummaryRef` wiring that does not exist — `SessionTextView.Summary` is hardcoded
`null` at `SessionProjectionLoader.cs:103`).

Never in scope: hashing recorded-session audio at export time (would hash a large
FLAC on every export); PDF rendering.

## Design

### 1. A provenance boundary

`SessionTextView` is documented as the *neutral, app-independent* metadata
projection behind `session.txt`. Version ids, model names, audio hashes and the
in-progress flag are export concerns and do not belong there.

Introduce a separate record, composed in `MaintenanceService` where `footerText`
composes today, so both renderers stay pure serializers:

```csharp
/// <summary>Export-only provenance (design 2026-08-03). Deliberately NOT part of
/// SessionTextView: session.txt is the neutral projection and must not grow
/// export-specific fields.</summary>
public sealed record ExportProvenance
{
    public string VersionId { get; init; } = TranscriptVersions.Root;
    public string Model { get; init; } = "";
    public string Backend { get; init; } = "";
    /// <summary>Imported sessions only (ImportedSourceInfo); null for recorded.</summary>
    public string? AudioFileName { get; init; }
    public string? AudioSha256 { get; init; }
    /// <summary>Session has no EndedAtUtc - exported mid-recording.</summary>
    public bool InProgress { get; init; }
}
```

`DocxRenderer.Write` and `MarkdownRenderer.Write` take `ExportProvenance` and **lose
their `string footerText` parameter** — the footer is now derived from `meta.Title`.

### 2. Footer

`{meta.Title}` at the left margin, `Page N of M` at the existing right tab on the
usable width. The `NUMPAGES` field pairs with the existing `PAGE` field, both with
the cached `1` placeholder Word replaces on pagination.

`Settings.DocxFooterText` is **deleted**. This is safe: `LocalScribeJson.Options`
does not set `UnmappedMemberHandling`, so the default `Skip` means existing
`settings.json` files carrying `"docxFooterText"` load unchanged and ignore it. No
schema bump, no migration. `SettingsTests.cs:22` and `:171-182` assert the field and
must be removed; `MaintenanceServiceVersionsTests.cs:190-218` construct settings with
it and must be updated.

### 3. Page header and the running head

A new `HeaderPart`, referenced `Type = Default`:

```
{first matter · date}                                  {STYLEREF speaker}
────────────────────────────────────────────────────────────────────────
```

Left: the first matter display (or the title when untagged) and the start date,
composed by us so it can be truncated — `STYLEREF` cannot truncate. Right: a
right-tabbed `{ STYLEREF "Transcript Speaker" }` field. Bottom border on the header
paragraph.

> **Trap:** the field argument is the style **name** (`"Transcript Speaker"`), never
> the `styleId` (`TranscriptSpeaker`). Word's field parser only ever resolves
> `w:name` — the ID is an internal token it never exposes — so an ID argument
> resolves to nothing and every page from 2 on shows *Error! No text of specified
> style in document.* once Word paginates. This is the same name/ID split as
> `"Transcript Turn"` / `TranscriptTurn`.

The header is suppressed on page 1, where the metadata block already names
everything. That requires `TitlePg()` in `SectionProperties` **plus** a
`HeaderReference { Type = First }` pointing at an empty header part.

> **Trap:** with `TitlePg` on, page 1 also loses the footer unless a
> `FooterReference { Type = First }` is supplied. Point it at the *same* footer part
> id — page 1 must still show `Page 1 of N`.

Word's documented header behaviour is what makes this work: it searches the current
page top-to-bottom for the style, and *if not found on the page, searches from the
top of the page backward to the start of the document*. A page holding only
continuation text therefore still resolves to whoever is speaking.

> **Trap:** `TranscriptSpeaker` must be a **pure character style**
> (`Type = StyleValues.Character`), never a linked paragraph+character style. Word
> will not see a linked style applied to only part of a paragraph, and the speaker
> name is exactly that — a run inside the turn paragraph.

The default (no `\l` switch) returns the *first* labelled speaker on the page, which
can differ from the speaker at the very top when a new speaker starts partway down.
The continuation labels in §7 reduce this to a near-non-issue by guaranteeing a label
near the top of nearly every page; it is not otherwise correctable and is the reason
the in-body labels are the primary mechanism rather than the running head.

### 4. Typography

- **Arial** in `DocDefaults` → `RunPropertiesBaseStyle` → `RunFonts { Ascii,
  HighAnsi, ComplexScript }`, so headings, footer, header, markers and line numbers
  all inherit. Font size stays pinned at 11pt (`FontSize { Val = "22" }`) — the text
  column arithmetic in `TextColumnTwips` depends on it.
- **6pt after each turn**: `SpacingBetweenLines { After = "120" }` on the
  `TranscriptTurn` style (twentieths of a point).
- **Widow/orphan control**: explicit `WidowControl()` on `TranscriptTurn`. Keeping
  ≥2 lines together is sufficient to stop a lone speaker label stranding at a page
  bottom; `KeepLines` is deliberately *not* used, as it would push whole multi-page
  turns onto a new page.
- **Speaker names in caps** via the `Caps` run property on the `TranscriptSpeaker`
  character style.

> **Trap:** caps must come from the `Caps` *format*, never from uppercasing the
> string. `STYLEREF` returns the underlying text, so uppercasing the data would
> destroy the real name in the document body to achieve a display effect. Apply
> `Caps` to the header field's run as well so both places render caps while the text
> stays intact.

### 5. Line numbering

`LineNumberType.CountBy` changes from `5` to `1`. Restart-per-page is unchanged.
Every-line numbering is what page:line citation (`12:5`) requires. The fixed
25-lines-per-page deposition grid is deliberately **not** adopted — it would force
exact line spacing and constrain typography hard for a convention the user has not
asked for.

The metadata block keeps its existing `SuppressLineNumbers`, so line 1 is still the
first line of transcript content.

### 6. Metadata block

Both formats gain four things. Order:

```
{Title}                                          (heading)
App: Webex
Date: 2026-08-03 14:22 - 15:08 (46 min)          ← end + duration are new
Matter(s): Smith v Jones (SJ-2024-01)
Participants: Sam, Bob (Counsel)                 ← side tag dropped
Medium: Call
Description: ...
Transcript version: v2 · large-v3-turbo · cuda   ← new
Audio: recording.m4a                             ← new, imported sessions
Audio SHA-256: a1b2c3...                         ← new, imported sessions
Speakers heard: Alice Parker, Bob Jones          ← new
IN-PROGRESS RECORDING — ...                      ← new, live exports only
{disclaimer, italic, thin rule under}
```

The metadata block is **not** caps-styled in either format. The `Caps` run property
from §4 applies only to speaker names in turn labels and the running head.

- **Date line**: `session.txt` already composes `start - end (N min)` at
  `SessionTextRenderer.cs:22-26`. Extract that into a shared
  `MetadataFormat.DateLine(SessionTextView)` helper called by all three renderers, so
  they cannot drift. This is the one refactor this spec takes on in existing code.
- **Transcript version**: always rendered, originals included —
  `TranscriptVersions.ShortId("v1")` returns `"v1"`, so no special-casing.
- **Audio**: rendered only when `ImportedSourceInfo` exists (imported sessions), which
  already carries `FileName` and a `Sha256` computed at copy time. Recorded sessions
  render nothing here; hashing their audio is out of scope.
- **Speakers heard**: distinct `row.DisplayName` over non-marker rows, in first
  appearance order. Distinct from Participants, which is user-curated metadata.
- **In-progress banner**: see §8.

The disclaimer stays non-optional and stays last, keeping its bottom rule.

### 7. Participants

`SessionProjectionLoader.cs:91-92` becomes name-only, role-when-set:

```csharp
var participants = meta.Participants.Select(p =>
    string.IsNullOrEmpty(p.Role) ? p.Name : $"{p.Name} ({p.Role})").ToList();
```

This is the shared projection, so `session.txt` changes identically. That is
intentional — one source of truth; a renderer-local mapping would let the two drift.

### 8. Continuation labels

`TimestampCadence.Chunk(DisplayRow row, int intervalMs)` gains a second trigger:

```csharp
public static IReadOnlyList<CadenceChunk> Chunk(DisplayRow row, int intervalMs, int maxChars)
```

A new chunk starts at a segment boundary when **either** trigger fires:

- `maxChars` — **always on** at `ContinuationMaxChars = 900`. At 11pt Arial in the
  ~4.5" text column that is ~10-11 rendered lines, roughly a quarter page, so a label
  lands near the top of essentially every page. This is a correctness feature, not a
  preference, and is therefore not behind a checkbox.
- `intervalMs` — unchanged 15s cadence, still gated behind the existing "extra
  timestamps" checkbox and still forced off when timestamps are off.

Continuation labels now carry the name:

| timestamps | label |
|---|---|
| on | `[04:30] Alice Parker (cont'd):` |
| off | `Alice Parker (cont'd):` |

The label text is the stored name in both formats. `.docx` displays it in caps via
the `TranscriptSpeaker` character style; `.md` has no such mechanism and renders it
as stored.

Chunk 0 is unchanged. Breaks stay on segment boundaries, so a label never lands
mid-sentence.

> **Trap:** the existing contract that an unchunked row carries `row.Text`
> **verbatim** (never the `Segments` re-join, because `SectionGrouper`'s null-payload
> merge means they can differ) must be preserved. Rows with `Segments.Count == 0`
> — live rows and legacy fixtures — must still pass through whole regardless of
> `maxChars`. With `maxChars` always on, many more rows now split and take the
> re-join path; the existing guard is what keeps that safe.

`TextColumnTwips` measures the longest turn label to size the text column. It
currently skips continuation stamps because they are always narrower than the 1.5"
floor. Named continuation labels are *not* narrower, so the pre-pass must now measure
them too.

### 9. Markdown parity

Applies: participants, all metadata additions, `(cont'd)` labels, the in-progress
banner.

Does not apply: everything in §3–§5, which is page furniture with no meaning in
markdown.

**The markdown footer block is dropped entirely.** With the footer reduced to the
transcript name, and the name already the H1 at the top of the document, the trailing
`---` + name block is pure repetition. `MarkdownRenderer.Write` loses its
`footerText` parameter along with the block.

### 10. Export entry points

Both reuse `ExportDialogViewModel` and `ExportDialog` unchanged. The `openExport`
factory at `App.xaml.cs:715-721` is currently closed over `sessionsVm.Rows` for its
title lookup; generalise it to take `(sessionId, title)` so all three callers share
one factory.

- **Read view**: an `Export...` button in the existing toolbar beside `Ask`
  (`ReadViewWindow.xaml:117`). The session is always finalised here, so there is no
  special handling at all.
- **Record console**: an `Export` pill beside `Compact` in the
  Recording/Paused button row (`LiveViewWindow.xaml:319-326`). Gated on
  `Session.CurrentSessionId` being non-null. Deliberately **not** added to the
  compact pill, which is a minimal always-on-top surface.

  The console has no cached title to pass — `SessionViewModel` exposes
  `CurrentSessionId` (`:95`) but no title. The factory falls back to the session id
  for the default filename, matching what `openExport` already does when a row has
  dropped out of the cached list. No async meta load is introduced on this path.

### 11. Live-export safety

**This is a prerequisite for the console button, not a nice-to-have.**

`TranscriptStore.ReadAllDetailedAsync` reads via `File.ReadAllLinesAsync`, which
opens with `FileShare.Read`. That permits concurrent readers but **excludes writers**.
The live capture pipeline appends via `File.AppendAllTextAsync`, which needs write
access. While an export reads `transcript.jsonl`, a concurrent append can therefore
fail with `IOException` — losing an evidentiary transcript line.

Today this is latent because nothing reads a recording session. The console export
button makes it reachable, so the read path must open with `FileShare.ReadWrite`,
exactly as `NeedsNewlinePrefix` already does at `TranscriptStore.cs:57`:

```csharp
using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
using var reader = new StreamReader(fs);
```

Torn-tail tolerance already exists and needs no change — `ReadAllDetailedAsync`
catches `JsonException` per line and skips it.

Other live-session consequences, all handled by existing behaviour:

- `EndedAtUtc` is null → `endedLocal` is null → the shared date-line helper already
  renders the start-only form.
- Diarisation has not run, so speaker labels are the generic Local/Remote split.
- `DurationMs` is not final.

Because the resulting document is materially weaker than the same session exported
after Stop, a live export is labelled. `ExportProvenance.InProgress` is set when
`session.EndedAtUtc is null`, and drives:

- a bold line in the metadata block, both formats:
  `IN-PROGRESS RECORDING — transcript incomplete, speaker separation not yet applied.`
- in `.docx` only, a second header line carrying the same text, so **every** page
  states it. Since the header is suppressed on page 1, the metadata-block line is
  what covers page 1.

## Testing

Existing suites needing updates: `DocxRendererTests` (footer assertion at `:57`,
line-number `CountBy`, new styles), `MarkdownRendererWriteTests` (footer block
removal), `SettingsTests` (`:22`, `:171-182` — `DocxFooterText` removal),
`MaintenanceServiceVersionsTests` (`:190-218`).

New coverage, one test per behaviour:

- Footer contains the title and both `PAGE`/`NUMPAGES` fields; carries no
  privilege string and no model name for a re-transcribed version.
- Page-1 header reference resolves to the empty part; page-1 footer reference
  resolves to the shared footer part.
- `TranscriptSpeaker` is emitted with `Type = character`.
- `DocDefaults` carries Arial on all three script slots.
- `TranscriptTurn` carries 6pt after-spacing and widow control.
- `LineNumberType.CountBy == 1`.
- Metadata block renders end+duration, version line, speakers-heard; renders the
  audio lines for an imported session and omits them for a recorded one.
- Participants render without side tags, with role preserved — asserted in the
  loader, the two export renderers, and `session.txt`.
- `Chunk` splits on `maxChars` with `intervalMs = 0`; splits on whichever trigger
  fires first when both are set; passes a `Segments.Count == 0` row through whole
  even with `maxChars` set; preserves `row.Text` verbatim on an unchunked row.
- Continuation labels carry the name in both timestamp modes.
- `TextColumnTwips` accounts for named continuation labels.
- `InProgress` renders the banner in both formats and the extra `.docx` header line;
  a finalised session renders neither.
- `TranscriptStore` read succeeds while a writer holds the file open for append
  (the `FileShare.ReadWrite` regression test).

## Round 2 backlog

`.txt` export · matter-level combined transcript · excerpt export (requires a
mandatory *"EXCERPT — not the complete transcript"* banner on every page, per the
locked no-content-deletion rule) · remember export choices · cadence-interval knob ·
filename template · summary section (needs `SummaryRef` wiring).
