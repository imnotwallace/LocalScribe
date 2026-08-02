# UX round 2026-08-02 — design

Status: approved by user 2026-08-02 (items 1-6 in one pass, 7-9 added mid-session and approved separately).
Scope: nine user-reported UX improvements. Each item is independently shippable; items 5+6 share the
export-options surface and land together. All decisions below were confirmed with the user; the
"Decision" lines are not open questions.

Reference material: the user supplied two Word screenshots of the target transcript layout
(bold `[00:00] Speaker:` left column, hanging-indent text column, line numbers every 5 lines
restarting per page, numbered content only) and one read-view screenshot for the Sync button placement.

---

## 1. Find works while a transcript is being edited

**Today.** Edit mode hard-disables Find by design: `OpenFind` refuses (`ReadViewViewModel.cs:276`),
`EnterEditMode` force-closes the bar (`:495`), the Find button stays enabled but silently does nothing,
and Ctrl+F is swallowed (`ReadViewWindow.xaml.cs:286-295`). The edit list (`EditList`) has no match
flags, no highlight triggers, and no scroll path; `ReadViewFindTests.cs:130-149` pins the old behaviour.

**Decision.** Full edit-aware Find: search the **live edited text** and **jump the caret into the
matching textbox** (user picked "Live text + jump into the textbox").

**Design.**
- Remove the `IsEditMode` guard in `OpenFind` and the `CloseFind()` in `EnterEditMode`. The bar stays
  open across Edit/Save/Cancel, keeping term + position; matches recompute on each transition
  (`ApplyRows` already recomputes after save-reload).
- Corpus rule: a section the user has expanded is searched against its live segment text
  (`EditableSegmentViewModel.EditedText`, joined with single spaces); a collapsed section against its
  loaded `Row.Text`. Match counts refresh as the user types via a debounced recompute subscribed to
  `EditedText` changes (wired/unwired on split, revert, and reindex, which replace segment instances).
- `EditableSectionViewModel` gains `IsFindMatch`/`IsCurrentFindMatch` observable flags (mirror of
  `ReadRow`); `EditList`'s ItemContainerStyle gets the same two highlight DataTriggers `RowList` has.
- Edit-mode match indices live in `EditSections` space (markers are absent there; `Rows` indices do not
  transfer). Read-row <-> edit-section mapping uses `ReferenceEquals(section.Row, readRow.Data)` —
  the section wraps the same `DisplayRow` instance. Never `==` (DisplayRow is a record with value
  equality).
- Marker rows: excluded from the edit-mode match count (they are not editable and not rendered there).
- Scroll: `OnVmPropertyChanged` branches on `IsEditMode` and targets `EditList.ScrollIntoView`.
- Jump-in: on Enter/Shift+Enter navigation, auto-expand the target section if collapsed (`BeginEdit`
  is idempotent), scroll it into view, then on a deferred dispatcher turn select the matched substring
  in the segment's TextBox (`Select(start,len)` + focus). Implemented as an attached behavior
  (pattern: `SegmentText.cs`) so the VM stays WPF-free; tolerates unrealized containers (virtualized
  list) by deferring until layout.
- Side effect covered by test: auto-expanding sections widens what `SaveEditsAsync` walks. Verified
  harmless (`CollectCorrections` filters unchanged text; pins skip same-speaker targets) — pin with a
  regression test: "find-expanded but untouched sections produce no corrections on save".
- Free rider: search-page / assistant-citation click-through (`ApplyFindTarget`) routes through the
  same mode-aware scroll helper, so citations stop no-oping during edit.
- Ctrl+F is no longer swallowed in edit mode; the Find button needs no trigger changes.
- Update `ReadViewFindTests.Find_survives_a_rows_reload_and_edit_mode_closes_it` to assert the new
  behaviour; add corpus-rule, marker-exclusion, and debounce tests at VM level.

**Constraint.** No bool-inverting converter exists by house rule — any new IsEditMode-conditional
XAML uses the Style + DataTrigger pattern (see `ReadViewWindow.xaml:48-51`).

---

## 2. Entering Edit (and Save) must not scroll to the top

**Today.** Read and edit modes are two separate ListViews with independent ScrollViewers swapped by
visibility; `EnterEditMode` rebuilds `EditSections` (collection Reset -> offset 0). Save has the same
bug: `SaveEditsAsync` reloads `Rows` without the `ReloadPreservingScrollAsync` wrapper the four
context-menu edit paths already use. Cancel likely preserves position already (runbook A3).

**Decision.** View-layer anchor fix (no VM changes).

**Design.**
- New helpers beside `FindScrollViewer` in `ReadViewWindow.xaml.cs`: capture the topmost visible item
  (realized containers only, transform-to-viewport Y closest to 0) and scroll-item-to-same-Y.
- Edit: capture anchor `ReadRow` from `RowList` -> `EnterEditMode()` -> deferred dispatcher turn
  (`DispatcherPriority.Loaded` + `UpdateLayout`; the list has never measured while collapsed) ->
  find the twin section via `ReferenceEquals(section.Row, anchor.Data)` -> scroll it to the anchor's
  previous viewport Y. Marker anchor: fall forward to the next non-marker row.
- Cancel: symmetric (references still valid). First verify on a long transcript whether Cancel is
  already correct; only add restore if it is not.
- Save: rows are rebuilt, so re-anchor by value (`StartMs` / first segment `Seq`), and route the Save
  reload through the same deferral pattern as `ReloadPreservingScrollAsync`.
- Hoist the duplicated `FindScrollViewer` (ReadViewWindow + LiveViewWindow copies) into one shared
  static helper while touching this code.
- No WPF/STA test harness exists: verification is manual via new checkboxes in
  `docs/plans/2026-07-07-transcript-editor-smoke-runbook.md` (enter/cancel/save at depth, with and
  without markers above the fold).

---

## 3. No blank dropdowns on first open

**Today.** 24 ComboBoxes total; 14 always have a value. Every blank one traces to one of three
mechanisms: (a) async ItemsSource filled after the selection binds, (b) selected value not a member of
the list, (c) nullable selection with no default. Two contradictory "All" sentinel conventions exist
(SearchPage `""` vs SessionsPage `null`); the null one is why the Sessions filter cannot re-select.
User-reported pain point: the Import dialog; decision is to fix the whole class.

**Decision.** Fix every blank-capable dropdown; mechanism-appropriate fix per site; regression tests
for the class.

**Fix list** (priority order):
1. Settings > Assistant model — blank on every first open until the async manifest scan lands, and
   stays blank when the saved/default model is not installed. After load, if the current value has no
   matching item, display the first installed chat model — matching Core's own runtime resolution
   (`AssistantModels.cs:87-88`) so the picker agrees with what actually runs. Display-coerce only;
   settings.json is never rewritten by page-open.
2. Record console > Remote target (idle + live combos) — `OptionFor` returns a detached option for a
   pinned-but-not-running app; it must be inserted into `RemoteTargetOptions` and re-inserted on every
   rebuild (the Settings mic picker's "(not connected)" pattern, `SettingsPageViewModel.cs:375-379`).
3. Session Details > both "Add from roster" pickers — "(choose a person)" sentinel row, selected by
   default and re-asserted after every roster refresh; Add button disabled while the sentinel is
   selected (auto-selecting a real person risks mis-adding to an evidentiary participant list).
4. Assistant panel > summary version — "(no summaries yet)" sentinel when the session has none.
5. Assistant panel > chat thread — "(no conversations yet)" sentinel until the first thread exists.
6. Sessions page > matter filter — adopt the SearchPage sentinel convention: "All matters" with
   `Id=""` (not null), default `""`, unconditional selection re-assert after each rebuild.
7. Search page > matter facet — seed the "All matters" sentinel at construction so the first paint is
   never blank while matters load.
8. Import / Re-transcribe model pickers — already defaulted normally; with zero models on disk show a
   disabled "(no models found)" sentinel instead of an empty box (Start stays disabled).
9. Settings > Per-app target (editable free-text combo) — watermark placeholder (shared attached
   watermark style for editable ComboBoxes; WPF/Wpf.Ui has no ComboBox PlaceholderText), e.g.
   "e.g. Webex, Zoom". A real default would be wrong here.
10. Settings > Model / Language with a stale persisted value (weights deleted; language outside the
    curated 20) — inject the saved value into the list rendered as "name (not installed)" and select
    it (mic-picker pattern). Truthful display; no silent settings rewrite.
11. Edit mode > split-part speaker with no override — renders blank by design (inherits parent);
    display "(inherits parent's speaker)" instead so it stops looking broken. Underlying null-means-
    inherit semantics unchanged.

**Tests.** Per touched VM: selection is non-null AND a member of the choices collection immediately
after construction and after awaiting the VM's load seam (template:
`SettingsPageViewModelTests.cs:149-159`). Sentinel-behaviour tests where consumption points must treat
the sentinel as "none" (matter filter, roster Add gating).

**Note.** The SessionsPage null-vs-"" sentinel contradiction is settled in favour of `""` (the
SearchPage rule "null SelectedValue cannot select a ComboBox item" is documented in-code and relied on).

---

## 4. Non-technical model descriptions

**Today.** Three pickers (Import, Re-transcribe, Settings) render bare ggml stems (`large-v3-turbo`,
`medium.en`, ...) via plain-string ComboBoxes. No metadata type exists; three sites enumerate models
independently; Re-transcribe defaults to alphabetical-first (`base.en` beats `large-v3-turbo`);
Settings re-implements the disk scan inline. Model set is deliberately OPEN (any dropped ggml file
must remain selectable). Model identity strings are evidentiary (SessionRecord.Model / WeightsFile,
export headers, read-view footer).

**Decision.** Technical name stays primary; plain-language subtitle beneath (user-approved copy
shape: "large-v3-turbo / Best accuracy at fast speed - recommended").

**Design.**
- New `WhisperModelCatalog` in `LocalScribe.Core/Transcription`:
  `record WhisperModelInfo(string Name, string Subtitle, int Rank, bool EnglishOnly)` +
  `Describe(name)` with a mandatory passthrough fallback (`new(name, "", int.MaxValue, ...)`) for
  unknown models — the open-set rule and `ModelFileResolver`'s "unknown names pass through verbatim"
  rule are preserved. Catalog entries for tiny/base/small/medium(.en)/large-v3/large-v3-turbo plus the
  Settings-only `"auto"` -> "Choose automatically for this PC".
- Copy is qualitative only: accuracy tier, speed, language coverage ("Best accuracy at fast speed -
  recommended", "Good accuracy, English only - slower", "Lowest accuracy - fastest, for quick
  drafts"). No GB figures (real size varies ~2x by backend: f16 on CUDA vs quantized on CPU/Vulkan)
  and no invented benchmark numbers (house precedent: the diariser refuses invented ETAs).
- All three pickers bind a shared projected list of `WhisperModelInfo` with a two-line ItemTemplate
  (name, muted subtitle). XAML switches from `SelectedItem` to `SelectedValuePath="Name"` +
  `SelectedValue` so the bound VM property stays a plain `string` — persistence, `ImportRequest.Model`,
  `SessionRecord`, and the canonicalization invariant are untouched. Selection-box rendering (closed
  combo) may show both lines; Settings' 140px combo is widened.
- Drift fixes riding along: Re-transcribe default becomes best-Rank-available (test update — deliberate
  behaviour change); `SettingsPageViewModel.BuildModelChoices` delegates to
  `ModelPaths.AvailableModels` instead of re-implementing the glob.
- Import dialog helper text rewritten in plain language (no raw IDs in the sentence; the IDs are now
  self-describing in the dropdown).
- Provenance surfaces (read-view footer, version labels, export headers, Session/TranscriptVersion
  records) keep technical names verbatim — no friendly names leak into records or exports.
- Tests: catalog describe/fallback; pinned picker-content tests updated (Import, Re-transcribe,
  Settings); Rank-default test for Re-transcribe.

---

## 5. Optional "timestamp at least every 15 seconds" export

**Today.** `SectionGrouper` merges consecutive same-speaker segments into one block with no duration
cap; exports stamp once per block at `row.StartMs`. Per-segment boundaries survive in
`DisplayRow.Segments` (unused by renderers). Export options are two unpersisted checkboxes shared by
.docx/.md via the format-neutral `DocxOptions`; spec §11.2 currently caps the dialog at two toggles.

**Decision.** New-paragraph-at-stamp style (user-approved preview): at the first segment boundary
where >= 15 s of wall time has elapsed since the last shown stamp within a same-speaker block, start a
**continuation paragraph** that begins with the stamp only — the speaker name is not repeated.
Optional, off by default.

**Design.**
- New pure static `TimestampCadence` in `LocalScribe.Core/Projection`:
  `Chunk(DisplayRow row, int intervalMs)` -> ordered chunks of (StampMs, Text, Segments slice).
  Rule: walking `Segments`, stamp when `seg.StartMs - lastStampMs >= intervalMs`; chunk text joined
  with single spaces (byte-identical to `SectionGrouper`'s join). Whole row = one chunk when
  `intervalMs <= 0`, `Segments` empty (live rows, legacy test fixtures), or row is a marker.
- Both export renderers consume the shared chunker (they are asserted mirrors): chunk 0 renders
  exactly as today; chunks 1..n render as new paragraphs prefixed with the stamp only —
  markdown `**[03:15]** text`, docx per the item-6 layout (stamp in the left column, no name).
- Option plumbing: `TimestampIntervalMs` (int, default 0 = off) on the shared options record; dialog
  gains one checkbox "Extra timestamp every 15 seconds", visible for Docx/Markdown, enabled only when
  "Include timestamps" is checked; interval fixed at 15 000 ms (no knob until someone needs one); not
  persisted (matches the existing toggles).
- Untouched by design: .zip export (bundles save-time files), save-time `transcript.md`/`.txt`
  (byte-identity tests), read view paragraphing, `MarkdownRenderer.Render` save dialect.
- Verbatim-rule note: paragraph breaks change where lines break, never the words — the locked
  "rows are emitted VERBATIM" rule is not touched (the rejected inline-marker variant would have).
- Spec §11.2 amended: third toggle documented; "at most two toggles" line updated.
- Tests: `TimestampCadence` unit tests (interval math, marker/empty-Segments passthrough, join
  fidelity vs `SectionGrouper`); renderer tests for continuation paragraphs in both formats; dialog VM
  test for the enable-gating.

---

## 6. Courtroom-style .docx layout (new default) + line numbers + page numbers

**Today.** `DocxRenderer` (DocumentFormat.OpenXml 3.5.1 — full WordprocessingML available, no new
dependency) emits one flat paragraph per turn: bold inline `[00:01] Name: ` label + text, no
ParagraphProperties, no styles part, no page margins, no line numbering; footer part holds the
versioned footer text only.

**Decision.** The layout from the user's screenshots becomes the **only** .docx layout (replaces the
flat one; no style picker). Word export = courtroom transcript. Added per user follow-ups: line
numbers at 5-line intervals restarting each page, counting transcript content only; page numbers
bottom-right in the footer.

**Design.**
- Turn paragraph geometry: `ParagraphProperties` with hanging indent (`Left = TextCol`,
  `Hanging = TextCol`) + a left tab stop at `TextCol`. Runs: bold `[00:00] Name:` -> tab -> plain
  text. Wrapped lines align at the text column (matches screenshot: text always right of the speaker
  name). Timestamps off -> bold `Name:` only, same geometry.
- Item-5 continuation paragraphs: bold `[00:17]` -> tab -> text; same indent, no name.
- `TextCol` auto-sized per document: cheap O(n) pre-pass over the longest `[stamp] Name:` label,
  clamped to [1.5", 3.0"] (stamp width varies by mode: mm:ss vs h:mm:ss vs wall-clock HH:mm:ss).
  Overlong names overrun one line gracefully (hanging indent keeps wrapped lines aligned).
- Markers: italic, positioned in the text column.
- Metadata header: unchanged content (Title, Date, Participants, Medium, disclaimer), plus a thin
  bottom-border rule under the disclaimer (matches screenshot). Disclaimer remains non-optional
  (spec-locked).
- Line numbering: `LineNumberType { CountBy = 5, Restart = NewPage }` on the existing
  `SectionProperties`; every metadata/header paragraph (title through disclaimer + spacer) gets
  `SuppressLineNumbers` so numbering starts at the first transcript paragraph, exactly as in the
  screenshots. Marker and continuation paragraphs count (they are content).
- Footer: existing versioned footer text left-aligned + right tab stop at usable width + `PAGE` field
  bottom-right. Footer text content rules unchanged.
- Page geometry: emit explicit `PageMargin` (1" all around; header/footer 0.5") — required for
  predictable tab positions; usable width computed from the existing A4/Letter `pageSize` argument.
- Styles: add a `StyleDefinitionsPart` with `DocDefaults` and a named `TranscriptTurn` paragraph style
  carrying the indent/tabs, so recipients can retune the whole document by editing one style in Word.
  Keep the current default-theme body font (the user's reference doc uses Word's default face);
  fix the size explicitly so column math is stable.
- Culture rules unchanged: invariant everywhere; page size remains the only locale dependence.
- Tests: `DocxRendererTests` substring asserts necessarily break (tabs contribute nothing to
  `InnerText`) — rewritten to assert structure: `Indentation`/`TabStop` values, run sequence
  (bold label run, tab, text run), `LineNumberType` presence + `CountBy=5`, `SuppressLineNumbers` on
  header paragraphs only, footer `PAGE` field, `PageMargin`. `MaintenanceServiceTests` docx assert
  updated the same way. Markdown suite untouched.
- Docs: spec §11.2 amended (layout description; toggle count; new footer/page-number/line-number
  facts); stage-6 design doc gets a superseded-by note.

---

## 7. "Sync transcript" follow-along playback toggle

**Today.** The read view polls playback on a 150 ms tick; `PlayingSectionIndex`
(`ReadViewViewModel.cs:119`, observable, fires once per row advance) drives the now-playing row tint.
Nothing scrolls to follow it. LiveView has a stick-to-bottom precedent but its detector cannot
distinguish programmatic from user scroll (it never needed to).

**Decision.** Pill toggle "Sync" next to Stop; follows the playing row; manual scroll disengages.

**Design.**
- `[ObservableProperty] bool SyncTranscript` on `PlaybackViewModel` (WPF-free, testable, survives
  edit-mode round trips). Not persisted; off by default per window.
- XAML: `PillToggleButton` + `ui:SymbolIcon` (sync arrows, icon-flip DataTrigger idiom from the mute
  pills) in the transport WrapPanel after Stop. Tooltip: "Keep the transcript scrolled to the line
  being played".
- Follow logic in `OnVmPropertyChanged` on `PlayingSectionIndex` change, guarded by
  `SyncTranscript && !IsEditMode && index in range` (`-1` sentinel never scrolls): `ScrollIntoView`
  then a deferred centering correction that places the playing row ~1/3 from the viewport top
  (plain `ScrollIntoView` with pixel scrolling pins new rows to the bottom edge — reader would never
  see upcoming text). Enabling the toggle snaps to the current row immediately. During a long
  monologue the 150 ms tick nudges the view if the playing row's container has left the viewport.
- Disengage: manual scroll intent (mouse wheel, scrollbar thumb drag, PageUp/PageDown) turns
  `SyncTranscript` off. A programmatic-scroll guard flag (set before each follow scroll, cleared on a
  deferred dispatcher turn) prevents the toggle's own scrolls from self-disengaging — mandatory,
  because `ScrollIntoView` raises `ScrollChanged`.
- Edit mode: read-mode feature; the toggle is inert (and visually disabled) while editing; playback
  itself keeps running as today.
- Scrubbing: position freezes during a drag (existing `IsScrubbing` behaviour), then the follow jumps
  once on release. Accepted.
- Tests: VM-level (`SyncTranscript` flag behaviour; `PlayingSectionIndex` interplay under
  `TickPlayback` with a fake player). Scroll behaviour is view-layer -> smoke runbook entries
  (follow during play; manual scroll disengages; toggle re-snap; edit-mode inertness).

---

## 8. Type-to-jump timestamp box

**Decision.** A small "Go to" entry in the transport cluster: type a stamp, Enter -> seek + scroll.

**Design.**
- Placement: in the elapsed/seek/total StackPanel cluster. `Ctrl+G` focuses it.
- Input format = whatever the transcript displays (`TimestampFormat` modes): relative `m:ss` /
  `mm:ss` / `h:mm:ss`, or wall-clock `HH:mm:ss` when the timestamps setting is `wallclock`
  (converted via the session's local start time). Parse mode follows the display mode.
- On Enter: parse -> clamp to `[0, DurationMs]` -> `Playback.Seek` -> one-shot scroll to the target
  row (regardless of the Sync toggle) -> highlight lands on the next tick. Invalid input: quiet
  inline error state (red-ish border + retained text), never a dialog; Esc returns focus to the list.
- Parsing lives in the VM layer (pure, testable): new `TimestampParser` (Core, beside
  `TimestampFormat`) with round-trip tests against `TimestampFormat.Stamp` for both modes.
- Tests: parser unit tests (formats, clamping, wallclock conversion, garbage input); VM test that a
  parsed jump moves `PositionMs`/`PlayingSectionIndex` on tick.

---

## 9. Channel mute/volume controls that only appear when they mean something

**Today.** The transport bar shows per-leg "Mute local"/"Mute remote" pills + "Local vol"/"Remote vol"
sliders, each visibility-gated on that leg existing. On a single-leg session the user sees a lone
"Mute local" + "Local vol" — muting/soloing the only channel is meaningless and reads like a stray
recording control.

**Decision.** Contextual mixer (user-approved, extended to volume sliders by follow-up): capability
kept for dual-leg sessions, noise removed for single-leg ones.

**Design.**
- Single-leg session (only one of `HasLocalLeg`/`HasRemoteLeg`): no mute pills; one slider labelled
  "Volume" bound to the lone leg's volume.
- Dual-leg session: a "Channels" group of two labelled rows — "Local (my side)" and
  "Remote (other party)" — each with its mute toggle + volume slider (existing bindings
  `LocalMuted`/`RemoteMuted`, `LocalVolume`/`RemoteVolume`; presentation regrouped so it reads as a
  playback mixer, not recording controls).
- `PlaybackViewModel` gains a derived `HasBothLegs` (or equivalent) for the visibility switch; no
  player-layer changes.
- Tests: VM-level visibility/derivation tests for single- vs dual-leg shapes.

---

## Cross-cutting

- **Order/batching.** Items 5+6 are one export change (same options record, dialog, renderer, spec
  section). Items 7+8+9 are one transport-bar change. Items 1+2 both touch the read-view scroll
  plumbing and should share the hoisted scroll helpers. Items 3 and 4 are independent sweeps.
- **TDD.** Every VM/Core change lands test-first (house rule). View-layer scroll/caret behaviour is
  untestable here (no STA harness) -> explicit smoke-runbook additions instead.
- **Spec amendments** (docs/specs/localscribe-specs.md §11.2): courtroom layout as the .docx format,
  third export toggle, line/page numbering, footer PAGE field. One amendment covering items 5+6.
- **Out of scope.** Persisting export toggles; a configurable cadence interval; friendly names in any
  persisted/exported artifact; per-segment (sub-row) sync scrolling; screenplay layout variants;
  the .zip export's bundled save-time files.
