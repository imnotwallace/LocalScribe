# Stage 6 — Correction Editing, Custom Vocabulary, Export (design)

- **Status:** Validated design (brainstorm session 2026-07-07). Implements build-sequence
  step 6 of `docs/plans/2026-06-30-localscribe-design.md`, expanded by three user decisions:
  `.docx` export folded in (was fast-follow), per-segment speaker pinning folded in, and a
  Record-console matter picker so per-matter vocabulary terms reach the Whisper prompt.
  **Phases 6.1 (read-view text corrections + speaker pins) and 6.2 (custom vocabulary UI +
  Record-console matter picker) IMPLEMENTED** on branch `stage-6-corrections-vocab-export`
  (plans `docs/plans/2026-07-07-stage-6.1-read-view-editing-plan.md` and
  `docs/plans/2026-07-07-stage-6.2-vocabulary-record-picker-plan.md`; smoke runbook Parts A & B
  `docs/plans/2026-07-07-stage-6-smoke-runbook.md` pending user). Only Phase 6.3 (loader extraction +
  `.zip`/`.docx` export) remains; the whole-branch adversarial review + `--no-ff` merge come after 6.3.
- **Companion:** `docs/specs/localscribe-specs.md` (§1.6 edits, §1.7/§10.1 vocabulary,
  §11 export). Spec deltas this stage introduces are listed in §8 below.
- **Delivery:** one branch, three sequential phases (6.1 → 6.2 → 6.3), per-phase plan +
  review, whole-branch adversarial review, `--no-ff` merge, consolidated smoke runbook.

## 0. Scope

| In | Out (unchanged non-goals / deferred) |
|---|---|
| Inline transcript text correction in the read view (`edits.json` overlay UI) | Segment split/merge/insert/reorder |
| Per-segment speaker reassignment = pinned `speakers.json` assignments UI | Deletion/hiding/redaction of content (never) |
| Correction/pin **revert** (overlay removal; JSONL untouched) | `.docx` import round-trip |
| Custom vocabulary editing UI — global (Settings) + per-Matter (Matters page) | Auto-cascade re-render on every vocab commit |
| Record-console optional matter picker → pre-tagged sessions + per-matter prompt bias | Offline-runner per-matter prompt bias (stays global-only) |
| `.zip` archive export (per session, per matter) | Grid multi-select "export N sessions" |
| `.docx` transcript export (OpenXml, legal chrome) | AI summaries, FTS search, auto-detect (later stages) |
| Shared `SessionProjectionLoader` extraction (kills the SessionWriter/ReadView duplication) | Live-view vocabulary corrections (render-time only, by design) |

Evidentiary invariants restated for the whole stage: `transcript.jsonl` is never mutated;
corrections and pins are additive overlays keyed by immutable `seq`; revert is overlay
removal (the machine original is always the recoverable floor); exports are strictly
read-only projections of session folders with **no** filtering/redaction affordances.

## 1. Phase 6.1 — Read-view editing (text corrections + speaker pins)

### 1.1 The granularity problem and its fix

The read view renders **grouped speaker turns**: `SectionGrouper.Group`
(`Core/Projection/SectionGrouper.cs:12-43`) concatenates consecutive same-speaker segments
and drops `seq`/`source`. Corrections and pins are keyed per-`seq`
(`Core/Storage/EditStore.cs`). Fix: `DisplayRow` gains an **additive** constituent-segment
list —

```
DisplayRow + Segments : IReadOnlyList<RowSegment>   // empty for markers
RowSegment { int Seq, TranscriptSource Source, long StartMs, long EndMs,
             string ProjectedText, string RawText, bool IsCorrected, bool IsPinned }
```

`SectionGrouper` populates it while grouping. `MarkdownRenderer`/`PlainTextRenderer`/live
view ignore the new property (existing exact-string renderer tests prove no output change).
`RawText` is the JSONL machine original; `ProjectedText` is post-vocabulary/post-edits —
the distinction matters because the correction editor must show both (§1.3).

### 1.2 Interaction model

- **Gesture:** right-click a transcript row → context menu: **"Correct text…"**,
  **"Reassign speaker…"**. Double-click stays click-to-seek (`ReadViewWindow.xaml.cs:112`).
  Menu items disabled on marker rows and while the session is not finalized/recovered.
- **Modal dialogs, not in-place editing** — deliberate: the ListView uses Recycling
  virtualization (`ReadViewWindow.xaml:115-121`); in-place TextBox template swaps are a
  known recycling footgun, and a dialog gives room for the per-segment breakdown.
  Both dialogs are plain `Window`s (WPF-UI FluentWindow startup gotcha).
- **Edited indicator:** rows whose any constituent segment `IsCorrected` show a subtle
  pencil glyph in the header line. The existing window-level `Edited` badge
  (`meta.Edited`) is unchanged.

### 1.3 Correct-text dialog

- Lists the turn's constituent segments in order; each shows an editable TextBox seeded
  with the **displayed (projected) text** and, beneath it, the read-only **machine
  original** (`RawText`) for reference.
- **Save** writes only segments whose text differs from the currently-projected text.
  Semantics: a correction is "this line should read X" — stored verbatim in `edits.json`,
  superseding the vocabulary pass per the §6.1 apply-order (human wins).
- **Revert to original** (per segment, shown only when `IsCorrected`): removes the
  `edits.json` entry — the render falls back to machine original + vocabulary pass. New
  Core API: `EditStore.RemoveTextCorrectionAsync(int seq)` (same guards as Apply:
  finalized-only; removing a non-existent entry is a no-op). Overlay removal is
  evidentiary-clean — nothing in the JSONL record changes.
- Known quirk (accepted, runbook-noted): `PhantomBleedDedup` compares projected text, so
  correcting one half of a bled pair can make the hidden duplicate reappear. The JSONL
  always kept both; no mitigation in v1.

### 1.4 Reassign-speaker dialog

- "This line was actually…" — candidate list, in order:
  1. the session's **same-side participants** (`meta.participants` where `side` matches the
     segment's source) — the Phase 2 identity-first model;
  2. **existing named clusters** for that source (`speakers.Names` keys not already owned
     by a listed participant).
  Creating a brand-new person stays in Session Details (one identity-creation flow).
- Constituent segments listed with checkboxes (all checked) — pin a whole turn or a single
  line in one visit.
- **Pin to participant with a `clusterKey`:** `EditStore.ReassignSpeakerAsync(seq, source,
  key)` per checked seq (writes `assignments[source][seq]` + `pinned[source]`, flips
  `meta.Edited`). NameResolver tier 1a renders the participant's name.
- **Pin to participant without a `clusterKey`:** MaintenanceService mints a fresh
  collision-safe key for that source (avoiding all keys present in `speakers.Names`,
  `speakers.Assignments` values, and participant-owned keys — same avoidance discipline as
  `SpeakersMerge`), writes the pins, then records ownership in
  `meta.participants[i].clusterKey`. Crash window between speakers.json and meta.json
  writes leaves a pin rendering "Speaker N" — benign, re-pinning heals it (documented).
- **Pin to an existing named cluster:** pins only; no meta change (tier 1b renders).
- **Unpin** (shown for `IsPinned` segments): removes the `assignments` entry + the
  `pinned` entry — render falls back through the resolution tiers. New Core API:
  `EditStore.RemoveSpeakerPinAsync(int seq, TranscriptSource source)`.
- Sources with no participants and no clusters (undiarised "Me/Them" baseline with empty
  roster): the dialog explains that participants are added in Session Details, with a
  button that opens it. No free-text speaker creation here.

### 1.5 Persistence and refresh

- Two new `MaintenanceService` write methods cloned from the `SaveMetaAsync` shape
  (`App/Services/MaintenanceService.cs:66-87`): per-session gate → `File.Exists(session.json)`
  delete-race guard → EditStore calls → **one** `SessionWriter.RegenerateProjectionsAsync`
  under the same gate hold. Truth-first write order (SaveDiarisationAsync precedent). No
  matters-index delta.
  - `SaveTextCorrectionsAsync(sessionId, changes: IReadOnlyDictionary<int,string>,
    reverts: IReadOnlyList<int>)` — batched per dialog visit, one regen.
  - `SaveSpeakerPinsAsync(sessionId, source, seqs, target)` where `target` is a one-of
    `SpeakerPinTarget` record: `Participant(participantId)` (handles minting + ownership
    per §1.4) or `Cluster(clusterKey)`; `RemoveSpeakerPinsAsync(sessionId, source, seqs)`.
- **Rows-only reload:** after a save, `ReadViewViewModel` re-runs the projection load and
  patches `Rows` **without** re-running `Playback.Resolve` (re-resolving re-subscribes
  MediaPlayer events — `DualMediaPlayer.cs:42-45` — and restarts position). Scroll offset
  and playback state are preserved; `_nowPlayingRowIndex` recomputed via `SectionAt`.
- EditStore guard exceptions (unfinalized, stale seq, marker, wrong source) surface through
  `IUiErrorReporter` with friendly copy; the delete-race guard returns quietly (session
  vanished — window is closing anyway via WindowRegistry).

## 2. Phase 6.2 — Custom vocabulary UI + Record-console matter picker

### 2.1 What already exists (no schema work)

`Settings.Vocabulary` (`Core/Model/Settings.cs:19`, schema v3) and `Matter.Vocabulary`
(`Core/Model/Matter.cs:22`, schema v2) both persist today. The heard→correct render pass
is fully wired with matters (`TranscriptProjection.cs:25`); the initial-prompt bias is
wired **global-only** — `SessionController.cs:209-212` builds the prompt with an empty
matters map because sessions are tagged only after recording. `VocabularyProvider`
composes global ∪ matters with matter-overrides-global on key collision, ~200-word prompt
bound, longest-key-first whole-word case-insensitive replacement.

### 2.2 Settings page — global vocabulary card

- New "Vocabulary" `SectionCard` on the root `SettingsPage.xaml` UserControl (not the
  `Pages/` nav host): a **Terms** editor and a **Corrections** (heard → correct) editor,
  both in the roster-editor idiom (`MattersPage.xaml:79-97` — rows with a Remove button +
  an add row). Commits per row action through the existing chained
  `Commit(s => s with {…})` auto-save (`SettingsPageViewModel.cs:333-345`); tests await
  `vm.LastSave` per house pattern.
- A `Note` line surfaces the ~200-word prompt bound (terms beyond it don't bias) and that
  corrections apply when transcripts are rendered, not to live view.
- **Same commit must:** remove `"Vocabulary"` from the banned-property array in
  `SettingsPageViewModelTests.cs:214-221` (`Vm_exposes_no_dropped_setting_surfaces`) and
  update the design-6.1 doc comment at `SettingsPageViewModel.cs:18-22`.
- Global re-render path: the existing Settings **"Regenerate all projections"** button
  (its doc comment already cites vocabulary as a reason). No auto-cascade.

### 2.3 Matters page — per-matter vocabulary card

- New "Vocabulary" card in the detail pane with **both Terms and Corrections** editors,
  LostFocus/immediate-save idiom via the `_loaded`-with-mutation +
  `MaintenanceService.SaveMatterAsync` pattern (never bare `MatterStore`; vocab is
  index-invisible so no index churn).
- Card includes **"Re-render tagged sessions"** — reuses `CascadeMatterAsync` + the
  inline `CascadeStatus` progress text. Vocab commits do **not** auto-cascade (batchy
  editing would thrash whole-catalog rewrites); the read view recomputes on open anyway,
  so staleness is confined to the projection files until the button (or any later regen)
  runs.
- Editor rules (both surfaces): reject empty keys; reject case-insensitive duplicate keys
  within the same map; copy notes that a matter entry overrides a same-key global entry.

### 2.4 Record console — optional matter picker (per-matter terms reach the prompt)

- The Record console gains an optional multi-select matter picker (reusing the
  Phase 2 searchable-picker idiom + `MatterSearch.Matches`). Default: none selected.
- On Start, the picked matter ids are handed to session bootstrap:
  1. `meta.matterIds` is **seeded** at session creation — the session starts pre-tagged
     (still fully editable post-hoc; record-first-classify-later remains the default flow,
     the picker is a convenience).
  2. The picked matters' `matter.json` files are loaded and threaded into the
     initial-prompt build — `SessionController` constructs
     `VocabularyProvider(settings.Vocabulary, mattersById).BuildInitialPrompt(matterIds)`
     instead of today's empty-map call, so **per-matter terms now bias Whisper** for
     pre-tagged sessions.
- A matter picked at record time whose folder fails to load degrades to global-only bias
  (warn-log, never blocks Start). The offline dev runner (`OfflinePipelineRunner.cs:58-61`)
  deliberately stays global-only — documented, not wired.

## 3. Phase 6.3 — Export (`.zip` + `.docx`)

### 3.1 Shared loader extraction (prerequisite refactor)

The stores→matters→vocabulary→projection load pipeline is duplicated verbatim between
`SessionWriter.RegenerateProjectionsAsync` (`Core/Storage/SessionWriter.cs:19-74`) and
`ReadViewViewModel.LoadAsync` (`ReadViewViewModel.cs:128-165`); exporters would be a third
copy. Extract a Core **`SessionProjectionLoader`** consumed by all three. Strictly
behavior-preserving; a **parity test** proves regenerated `transcript.md`/`.txt`/
`session.txt` are byte-identical before/after (the invariant-culture byte-identical rule
is load-bearing here).

### 3.2 `.zip` archive

- **Session zip** (`SessionArchiver`, Core; `System.IO.Compression`, in-box): archives
  **whatever files exist** in `SessionDir(id)` — audio may be absent (retention) or
  flac/wav per session; edits/speakers/summary are absent-until-used. Strictly read-only;
  no temp files inside the session folder; audio entries stored `NoCompression` (FLAC is
  already compressed), text/JSON `Optimal`. Built while holding that session's maintenance
  gate so a zip never captures a half-written re-render.
- **Matter zip:** snapshot the tagged-session list (`SessionCatalog.ListAsync` filtered on
  `Meta.MatterIds` — the `CascadeMatterAsync` pattern), then gate-and-add one session at a
  time (gate released between sessions; a long export never blocks unrelated edits).
  Layout inside the zip: one folder per session (folder-id names) + a `matter.json`
  snapshot at the root for roster/vocabulary context. Sessions that are live-recording or
  pending-recovery are **skipped and reported** (mirrors the delete guards,
  `SessionsPageViewModel.cs:273-283`).
- Failure/cancel deletes the half-written **output** file — never anything under
  `storageRoot`. Matter zips get determinate progress + Cancel on the SplitSpeakers
  pattern (`DispatchedProgress`, `CancellationTokenSource`, never `System.Progress<T>`).

### 3.3 `.docx` transcript

- **`DocxRenderer`** in `Core/Projection` beside `MarkdownRenderer`/`PlainTextRenderer`,
  package `DocumentFormat.OpenXml` (MIT) referenced in Core.csproj. Input: the same
  `TranscriptHeader` + `SessionTextView` + `IReadOnlyList<DisplayRow>` render model +
  `DocxOptions { IncludeTimestamps, IncludeMarkers }`.
- Body: metadata header block (title, matters, **user-curated participants** — never
  `speakers.json` clusters, per spec §11.2), timestamped grouped speaker turns, markers
  italic. Timestamp format honours `settings.Timestamps` mode.
- Legal chrome (spec §11.2): locale page size — A4/Letter via `RegionInfo` at export time,
  the **one deliberate machine-locale dependence**, scoped to page size only (all text
  stays invariant-culture); per-page footer from a new settings override; a
  **non-optional** machine-generated-accuracy disclaimer.
- **Settings (additive, no schema bump — `SectionGapMs` precedent):**
  `docxFooterText: string` (default `"PRIVILEGED & CONFIDENTIAL"`), read-only elsewhere;
  spec §7 table + `SettingsMigrator`/JsonConventions tests updated to match.

### 3.4 Entry points and plumbing

- **Sessions page:** action bar + row context menu (both surfaces, same command —
  BindingProxy idiom) get **"Export…"** → one small export dialog (plain `Window`):
  format choice (.zip / .docx), the two docx toggles, destination via Save-As. Default
  filenames: `{folder-id}.zip`, `{title}.docx`. Disabled for pending-recovery rows and
  the live-recording session.
- **Matters page:** detail pane gets **"Export matter archive…"** (near the Tagged
  sessions card, code-behind Click idiom of that page).
- This **replaces** spec §11's single shared Session/Matter picker with context-driven
  entry points — documented deviation (§8).
- New injected seam from the composition root: `pickSavePath: Func<SavePathRequest,
  string?>` wrapping `Microsoft.Win32.SaveFileDialog` (the `pickFolder` pattern,
  `App.xaml.cs:111-116`; VMs stay WPF-free and headless-testable). Last-used directory is
  remembered in throwaway UI state beside the overlay's `window-state.json` — **not**
  `settings.json`.
- Orchestration on `MaintenanceService` (the one disk-mutation/gate owner):
  `ExportSessionArchiveAsync(id, destPath, ct)`,
  `ExportMatterArchiveAsync(matterId, destPath, IProgress<int>, ct)`,
  `ExportDocxAsync(id, destPath, DocxOptions, ct)`.
- Post-export: `IUiErrorReporter.Info` confirmation + reveal-in-Explorer of the output
  (`explorer.exe /select,` — new but trivial variant of the three existing
  `Process.Start("explorer.exe", …)` affordances).

## 4. Error handling

- All failures surface through `IUiErrorReporter` (InfoBar), per house pattern.
- EditStore guards → friendly copy: unfinalized ("session is still recording"), stale
  seq/marker/wrong-source ("transcript changed — reopen the session").
- Export: `DISK_FULL`/IO → error + output-file cleanup; matter zip reports per-session
  skips (recording / pending recovery / unreadable folder) in the completion message
  without failing the whole archive; unreadable session folders are skipped-and-counted
  (SessionCatalog tolerance pattern).
- Record-console matter load failure at Start → warn-log + global-only prompt (never
  blocks recording).

## 5. Testing strategy

House TDD (subagent-driven, per phase):

- **Core:** exact-output renderer tests for `DocxRenderer` (assert on the document XML
  parts: body paragraphs, footer, disclaimer presence, toggle behavior);
  `SessionProjectionLoader` **byte-identical parity test** for all three projection files;
  `EditStore` extension tests (remove-correction, remove-pin, guards, no-op removal);
  `RowSegment` population tests (grouping keeps seq/source/raw text; renderers'
  existing exact-string tests prove output unchanged); `SessionArchiver` tests on GUID
  temp roots (files-present matrix: no audio / wav / absent overlays; skip+report cases);
  prompt-threading test (SessionController builds the prompt with picked matters).
- **App (headless VM tests):** correct/reassign dialog VMs (seeding, changed-only saves,
  revert, candidate lists incl. the mint-ownership path); MaintenanceService new methods
  (gate held, write order, single regen, delete-race); vocabulary card VMs (dup-key
  rejection, LastSave awaiting, reflection-test update **in the same commit**); export VM
  (guards, cancel cleans output, progress).
- **Gate:** 0-warning build + full suites green (App 269+, Core 334+, the 2 known fixture
  fails excepted), per-phase review, whole-branch adversarial review before merge.

## 6. Delivery plan

- Branch `stage-6-corrections-vocab-export` off master.
- Phase 6.1 read-view editing → plan + implement + review.
- Phase 6.2 vocabulary + Record-console picker → plan + implement + review.
- Phase 6.3 loader extraction + export → plan + implement + review.
- Whole-branch adversarial review → fix → gate → `--no-ff` merge to master.
- Consolidated Stage 6 smoke runbook (user items: correction round-trip incl. revert,
  pin/unpin incl. re-diarise survival, vocab bias on a real recording via the Record
  console picker, matter zip of a real matter, `.docx` opened in Word).

## 7. New/changed units (summary)

| Unit | Layer | Kind |
|---|---|---|
| `DisplayRow.Segments` / `RowSegment` | Core/Projection | additive model |
| `EditStore.RemoveTextCorrectionAsync` / `RemoveSpeakerPinAsync` | Core/Storage | new APIs |
| `SessionProjectionLoader` | Core/Storage | extraction refactor |
| `DocxRenderer` + `DocxOptions` | Core/Projection | new (OpenXml) |
| `SessionArchiver` | Core/Storage | new |
| `SessionController` prompt build with matters | Core/Live | change |
| `Settings.DocxFooterText` | Core/Model | additive field |
| `MaintenanceService`: SaveTextCorrections / SaveSpeakerPins / RemoveSpeakerPins / Export×3 | App/Services | new methods |
| Correct-text + reassign-speaker dialogs (plain Windows) | App | new UI |
| Vocabulary cards (Settings + Matters) | App | new UI |
| Record-console matter picker | App | new UI |
| Export dialog + `pickSavePath` seam + last-dir UI state | App | new UI/seam |

## 8. Spec deltas (fold into `localscribe-specs.md` on merge)

1. §1.6: correction **revert** = removal of the overlay entry (not a tombstone; machine
   original + vocabulary pass become the render again). Same for pin removal in §1.3.
2. §1.3: per-segment pin UI semantics — participant-first candidates; minting + meta
   ownership for participants without clusters; unpin.
3. §7: `docxFooterText` settings row (additive, default `"PRIVILEGED & CONFIDENTIAL"`).
4. §10.1: Record-console matter picker — pre-tagged sessions feed per-matter terms into
   the initial prompt; offline runner remains global-only.
5. §11: context-driven export entry points replace the shared picker; matter zip includes
   a `matter.json` snapshot; docx page size is machine-locale-dependent by design.

## 9. Accepted quirks / documented non-fixes

- Correcting one half of a phantom-bleed pair can un-hide the duplicate (dedup compares
  projected text). JSONL always kept both.
- Live view shows uncorrected text (corrections are render-time only).
- Crash between a participant pin's speakers.json write and the meta ownership write
  renders "Speaker N" until re-pinned.
- Projection files go stale after vocab edits until an explicit re-render (read view is
  always fresh — it recomputes on open).
