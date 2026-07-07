# Transcript Editor Overhaul — Table Editing, Mid-Segment Split, Inline Speaker Assignment (design)

- **Status:** Validated design (brainstorm session 2026-07-07). Extends Stage 6.1 read-view
  editing (`docs/plans/2026-07-07-stage-6-corrections-vocab-export-design.md` §1) from
  modal-dialog corrections to an in-place table editor. Supersedes 6.1's "Segment
  split/merge/insert/reorder — OUT" line for the split case only (merge = revert-split;
  insert/reorder stay out).
- **Companion:** `docs/specs/localscribe-specs.md` (§1.6 edits). Spec deltas in §9 below.
- **Delivery:** one branch, subagent-driven TDD, per-task review, whole-branch adversarial
  review, `--no-ff` merge, smoke runbook. Sequence in the follow-on implementation plan.

## 0. Scope

| In | Out (unchanged non-goals / deferred) |
|---|---|
| Read⇄Edit mode toggle **inside** the existing per-session read window | A separate standalone editor window |
| Editable table: **Speaker \| Time \| Text** columns, sections as rows | Rich text / formatting in cells |
| Click a row → **expand to its constituent segments** as atomic sub-rows | Always-flat one-segment-per-row display (read view still merges) |
| **Mid-segment split** at cursor (Enter) via a non-destructive split overlay | Deletion / hiding / redaction of content (never) |
| **Revert a split** (merge children back to the machine original) | Cross-segment / cross-speaker manual merge; segment insert/reorder |
| Adjustable **derived** split timestamp, editable to **10 ms** (hundredths) | True per-word timestamps / forced word alignment (pipeline can't) |
| Inline **assign-only** speaker dropdown per row (pins/reassigns) | Full roster CRUD in the editor (stays in Session Details) |
| **"Manage speakers…"** button + **live roster sync** from Session Details | Manual refresh / reopen to see roster changes |
| Batched save through `MaintenanceService`; reload preserving scroll | Per-keystroke disk writes |

**Evidentiary invariants (restated, binding for this feature):** `transcript.jsonl` is never
mutated. Corrections, splits, and pins are additive overlays keyed by the immutable machine
`seq`. A split **partitions** an original segment into human-authored children but keeps the
original machine line as the recoverable floor; **revert restores the single original
segment**. Derived split timestamps are visibly flagged and never presented as machine
timing. Editing stays gated to finalized/recovered sessions.

## 1. Container — Edit mode in the Read view

The editor is a mode of the existing `ReadViewWindow` (`src/LocalScribe.App/ReadViewWindow.xaml`),
not a new window. A `Read ⇄ Edit` toggle in the header swaps the read-only `ListView` for the
editable table. Rationale: reuse the window's playback transport (needed to place split
boundaries by ear — §3.3), its placement/registry lifecycle, and `ReloadPreservingScrollAsync`
(`ReadViewWindow.xaml.cs:165`). One window per session; no new lifecycle.

- Entering Edit re-uses the already-loaded projection; leaving Edit (or Save/Cancel) returns
  to the read list at the same scroll offset.
- The Stage 6.1 row context menu ("Correct text…", "Reassign speaker…", "Remove speaker
  pin…") **stays** as the quick single-row path in Read mode; Edit mode is the bulk/structural
  path. Both write the same overlays, so they compose.
- Edit mode is disabled (toggle hidden/greyed) when the session is not finalized/recovered,
  matching `EditStore.EnsureFinalizedAsync` (`EditStore.cs:188`).

## 2. Data model — the split overlay

A new non-destructive overlay in `edits.json`, mirroring how `Corrections` already overlay by
`seq` (`Core/Storage/Edits.cs`, `Core/Storage/EditStore.cs`). No new file; no JSONL mutation.

### 2.1 Schema

```
edits.json
  corrections: { "<seq>": { text, editedAtUtc }, ... }        // existing, unchanged
  splits:      { "<seq>": {                                    // NEW
                   source: "Local" | "Remote",
                   editedAtUtc,
                   parts: [                                     // length >= 2, display order
                     { text, startMs, speakerClusterKey?, derivedStart } , ...
                   ] } }
```

Invariants enforced in `EditStore` (parallel to the correction validators):

- `parts.length >= 2`; a 1-part "split" is meaningless and rejected.
- `parts[0].startMs == originalLine.StartMs` and `parts[0].derivedStart == false` — the first
  child inherits the machine start; only later boundaries are human-derived.
- `parts[i].startMs` strictly increasing and within `(originalLine.StartMs, originalLine.EndMs]`;
  `parts[i>=1].derivedStart == true`. Stored full-ms; the UI constrains edits to 10 ms steps.
- Each `part.text` non-empty, non-whitespace — the **same** "a correction must correct, never
  blank content" rule as `EditStore.cs:65`. Content is partitioned, never removed.
- `speakerClusterKey` optional and source-prefixed (`"Remote:2"`), same shape as pins
  (`EditStore.cs:101`); `null` means the child inherits the seq's normal resolved speaker.
- Split target must be an existing JSONL **segment** of the named source — reuses
  `EnsureSegmentAsync` (`EditStore.cs:158`).

### 2.2 Projection expansion

`TranscriptProjection.Build` step 1 (`Core/Projection/TranscriptProjection.cs:20`) currently
maps each line 1:1 to a `ProjectedSegment`, applying `Corrections[seq]` if present. New rule:

- If `splits[seq]` exists, emit **one `ProjectedSegment` per part** instead of one for the
  line. Each child: `Text = part.text`, `StartMs = part.startMs`,
  `EndMs = nextPart.startMs ?? originalLine.EndMs`, `RawText = originalLine.Text` (the shared
  machine original, for the "machine original" reference and revert), plus new flags
  `IsSplitChild = true` and `PartIndex = i`, and an optional resolved speaker override.
- **Precedence:** a split supersedes a plain `Corrections[seq]`. Creating a split **removes**
  any `Corrections[seq]` (its text is absorbed into `parts`); reverting a split restores the
  single original machine segment and does **not** resurrect a prior correction (the machine
  floor is the revert target — no stale-overlay ambiguity).
- Everything downstream (dedup, `SectionGrouper`, sort, export via `SessionProjectionLoader`)
  consumes `ProjectedSegment`/`DisplayRow` unchanged, so split children section, render, jump,
  and export with no per-consumer changes. Split children of the same speaker with a
  sub-`gapMs` boundary will re-merge in the read view's grouper — expected and harmless; the
  edit table always shows them expanded.

### 2.3 `RowSegment` / `DisplayRow` additions

`RowSegment` (`Core/Projection/RowSegment.cs`) gains `bool IsSplitChild` and `int PartIndex`
so the editor can address a child for re-edit, re-split, or revert. `DisplayRow.HasSplit`
(any child `IsSplitChild`) drives a subtle "split" badge, alongside the existing
`HasCorrection`/`HasPin`. Renderers ignore the new fields (existing exact-string renderer
tests prove byte-identical output for un-split sessions).

### 2.4 Speaker on a split child

A child's speaker lives **in the split entry** (`part.speakerClusterKey`), not in
`speakers.json`. This deliberately keeps `speakers.json` keyed by plain integer seqs — no
composite `"<seq>.1"` keys rippling through `Pinned`, `Assignments`, `NameResolver`, and the
`EnsureSegment` validators. `NameResolver.Resolve` (`Core/Projection/NameResolver.cs:10`)
gains a tier-0 check: if the projected child carries a `speakerClusterKey` override, resolve
that (participant-ownership → `Names` → derived, same tiers), else fall back to the original
seq's normal resolution. Whole-section (non-split) speaker changes still use the existing pin
path (`speakers.json`); only split-child speakers route through the overlay.

## 3. Table interaction model

### 3.1 Layout & altitude

Columns **Speaker \| Time \| Text**; rows are merged sections by default (readable, matches
the read view). A row shows the section's speaker, its start stamp (`mm:ss` / `h:mm:ss`), and
the concatenated turn text. Badges (edited / pinned / split) sit in the row as today.

### 3.2 Expand-on-edit (and the recycling footgun)

Clicking a row enters edit for that section: the row **expands to its constituent segments**
as atomic sub-rows, each with an editable text box, a speaker dropdown (§4), and its own time.

Stage 6.1 avoided in-place editing because the virtualized list recycles containers and
"in-place TextBox template swaps are a known recycling footgun" (6.1 design §1.2). We resolve
that instead of avoiding it: **all edit state lives on the row/segment view-models**
(`IsEditing`, the materialized child-segment VM collection, each child's editable text and
chosen speaker), and the cell templates are **data-triggered** off those VMs. Because state is
in the data item, not the visual container, container recycling simply re-binds `DataContext`
and the correct expanded/collapsed state follows the item. Child sub-row VMs are materialized
**only** while a section `IsEditing`, so an idle multi-hour transcript pays nothing (§5).

### 3.3 Enter-to-split

Because edit mode already expands a section into its per-segment sub-rows, machine-boundary
separation needs no gesture — the segments are already distinct rows. The only split operation
is therefore **mid-segment**. With the caret inside a segment sub-row's text box, **Enter
splits at the caret**:

- Create (or extend) a split overlay for that seq. The text partitions at the caret; the first
  child keeps the machine start; the new child's start is **auto-estimated by caret character
  offset** across the segment's `[start, end]`, rounded to 10 ms, `derivedStart=true`. Both
  become sub-rows immediately. Splitting an already-split child partitions that part further
  (parts list grows, times stay monotonic).
- **Degenerate caret rejected:** Enter at the very start or end of the text (which would
  produce an empty child) is a no-op — the empty/whitespace-child invariant (§2.1) forbids it.
- The derived time is shown in an **editable field constrained to 10 ms steps**, visibly
  flagged as estimated; the user can nudge it or scrub the window's playback transport to the
  moment and set it. Full ms is stored.
- **Revert / merge:** a split child offers "merge back" → removes `splits[seq]`, restoring the
  single machine segment. This is the only merge (no cross-segment merge).

### 3.4 Save / cancel

Edit mode accumulates a batch — text corrections on unsplit segments, split overlays (with
their parts and any child speakers; **editing a split child's text rewrites its `part.text`**,
not `Corrections`), and whole-section pins — and commits them in one pass via
a batched `MaintenanceService` call under the per-session single-flight gate
(`MaintenanceService.RunForSessionAsync`, `MaintenanceService.cs:37`), then one projection
regen and `ReloadPreservingScrollAsync`. Cancel discards the in-memory batch; nothing is
written. No-op batches write nothing and don't flip `meta.Edited` (existing behavior,
`EditStore.cs:85`).

## 4. Speaker assignment + live roster sync

- The per-row **speaker dropdown is assign-only**, populated from the current roster
  (same-side named participants first, then named clusters no participant owns — the candidate
  logic already in `ReassignSpeakerViewModel.cs:59`). Choosing a name pins/reassigns via the
  existing `MaintenanceService.SaveSpeakerPinsAsync` (`MaintenanceService.cs:139`) for whole
  sections, or writes `part.speakerClusterKey` for a split child (§2.4).
- A **"Manage speakers…"** button opens Session Details (reusing the window's existing
  `_openSessionDetails` callback, `ReadViewWindow.xaml.cs:43`).
- **Live sync:** a `RosterChanged(sessionId)` notification (a session-scoped event on the
  shared window/registry plumbing, in the spirit of `ISettingsService.Changed` and the
  existing `RefreshRowAsync` fire-and-forgets) is raised when Session Details saves a roster
  change. The edit view subscribes and re-populates the dropdown and re-resolves displayed
  names in place — no reopen, no manual refresh. Unsubscribed in `OnClosed` alongside the
  existing settings unsubscribe (`ReadViewWindow.xaml.cs:62`).

## 5. Long sessions (>1 hr)

- Timestamps already render `h:mm:ss` past an hour (`Core/Projection/TimestampFormat.cs:15`).
- The edit table stays **UI-virtualized** (`VirtualizingPanel`, `ScrollUnit="Pixel"`, as the
  read list already is, `ReadViewWindow.xaml:120`). Recycling is safe because edit state is on
  the VM (§3.2). A multi-hour, few-thousand-segment session realizes only on-screen rows.
- Child sub-row VMs exist only for the actively-edited section, so expansion cost is O(one
  section), not O(transcript).
- Projection is pure and already runs at export scale via `SessionProjectionLoader`; edits are
  batched per save (§3.4) — no per-keystroke or per-segment disk work.

## 6. Core / App change surface

- **Core:** `Edits` model + JSON (`splits`); `EditStore` `ApplySplitAsync` / `RemoveSplitAsync`
  + validators; `TranscriptProjection` split expansion; `RowSegment`/`DisplayRow` flags;
  `NameResolver` tier-0 child-speaker override.
- **App:** `MaintenanceService` batched save + split wrappers (single-flight gate, projection
  regen) + child-speaker→clusterKey minting reuse (`MintClusterKeyAsync`,
  `MaintenanceService.cs:215`); read-view Read⇄Edit toggle + editable-table VMs
  (section VM, segment-child VM) with data-triggered templates; `RosterChanged` notifier +
  subscription; "Manage speakers…" button.
- **Unchanged:** `transcript.jsonl` / `TranscriptStore` (append-only); exporters (consume the
  projection); `speakers.json` schema (no composite keys).

## 7. Evidentiary guarantees (reaffirmed)

Machine original always preserved (`transcript.jsonl` untouched; `RawText` carries the
original; revert restores the single machine segment). No deletion/hide/redaction — split
**partitions**, it never removes, and empty/whitespace children are rejected. Derived split
timestamps are flagged and never machine-presented. Editing gated to finalized/recovered
sessions. Exports stay read-only projections with no filtering affordances.

## 8. Testing

- **Core (unit, TDD):** split apply/revert round-trip; original preservation; revert restores
  exactly one segment; whitespace/empty child rejected; monotonic in-range `startMs`; first
  child inherits machine start; split supersedes + clears a prior correction; projection
  expands N children with correct text/time/flags; grouper re-merge behavior; `NameResolver`
  child-speaker override tiers; export byte-identity unchanged for un-split sessions.
- **App (VM):** section-edit expand materializes child VMs only when editing; Enter-at-caret
  boundary vs mid-segment paths; derived-time estimate + 10 ms constraint; assign-only
  dropdown → pin vs child-speaker routing; batched save composes corrections+splits+pins;
  cancel discards; `RosterChanged` live-updates the dropdown.
- **Adversarial whole-branch review** before merge (evidentiary lens: prove no path mutates
  JSONL, no path removes content, revert is total).
- **GUI smoke runbook** (user): mid-segment split on the TEST session, adjust the derived
  time, assign each half to a different speaker, save, reopen, verify persistence + revert;
  a >1 hr synthetic session for virtualization/scroll.

## 9. Spec deltas (`docs/specs/localscribe-specs.md`)

- §1.6 (edits): add the `splits` overlay — schema, invariants, projection expansion,
  correction precedence, revert semantics, derived-timestamp flagging.
- New subsection: read-view **Edit mode** (table, expand-on-edit, split, assign-only speaker,
  roster live-sync) as the structural-edit surface complementing 6.1's per-row dialogs.
- Reaffirm: split partitions and is fully revertable; `transcript.jsonl` and `speakers.json`
  schemas unchanged.

## 10. Out of scope (named, not built)

Segment insert/reorder; cross-segment/cross-speaker merge; per-word timestamps / forced
alignment; full speaker roster CRUD in the editor; `.docx` import; multi-select bulk export.
