# Transcript Editor Overhaul — Smoke Runbook

Branch/feature: transcript editor overhaul (table editing, mid-segment split, inline speaker
assignment — `docs/plans/2026-07-07-transcript-editor-overhaul-design.md`,
`docs/specs/localscribe-specs.md` §1.6/§1.7). Covers the Read⇄Edit toggle in the existing
per-session read view. Run after Task 18's automated gates (Core/App test suites + solution
build) are green.

**Known gap found during the Task 18 gate (read this first):** `EditableSectionViewModel
.RevertSplit(seq)` and the full persistence path down to `EditStore.RemoveSplitAsync` are
implemented and unit-tested, but no button, menu item, or key gesture in
`ReadViewWindow.xaml`/`.xaml.cs` currently calls `RevertSplit` from the GUI — there is no
visible "merge back" affordance on a split child. **Part C (Revert a split) below is expected
to currently fail/be blocked for this reason; it is not a runbook authoring mistake.** Confirm
this on your machine and report it back as a fast-follow fix rather than assuming user error.

## Prep

- Build and run the app (close any previously running `LocalScribe.App.exe` first).
- Have (or create) a **finalized** session with at least 3-4 speaker turns spanning both
  Local and Remote (a short 2-3 minute test recording is enough). A session still
  `Recording`/`Paused` is intentionally not editable — the **Edit** button stays hidden for it.
- Open that session's read view (Sessions page → double-click, or "View transcript").

## Part A: Enter/leave Edit mode + expand-on-edit

- [ ] **A1 Enter Edit:** with a finalized session's read view open, click **Edit** in the
  header. The read-only list is replaced by the editable table; **Edit** itself disappears and
  **Save**, **Cancel**, and **Manage speakers...** appear in its place.
- [ ] **A2 Edit hidden when not editable:** open a session that is still `Recording`/`Paused`
  (or a not-yet-finalized/recovered one if you have one) — the **Edit** button is hidden or
  disabled; there is no way to reach the table.
- [ ] **A3 Leave without saving:** in Edit mode, click **Cancel** with no changes made — the
  view returns to the read-only list at the same scroll position, no "Edited" badge appears,
  and `edits.json` in the session folder is unchanged (untouched or still absent).
- [ ] **A4 Expand-on-edit:** in Edit mode, click a collapsed row — it expands in place into one
  sub-row per constituent transcript segment, each with a Speaker dropdown, a time field, and
  an editable text box seeded with the row's current text. Collapse is per-row: other rows stay
  collapsed until you click them too.
- [ ] **A5 Recycling sanity:** with one row expanded, scroll the table far enough that the
  expanded row scrolls out of view and back into view (or scroll past enough rows to force
  container recycling) — the row's expanded/collapsed state and any in-progress text you typed
  are unchanged when it scrolls back.

## Part B: Mid-segment split + derived-time adjustment + speaker assignment

- [ ] **B1 Split at caret:** expand a row with a segment of a few sentences. Click inside the
  text box, place the caret **mid-sentence** (not at the very start or end), press **Enter**.
  The single sub-row becomes two sub-rows: the text splits at the caret, the first half keeps
  its original (non-estimated) time, and the second half shows a derived/estimated time field.
- [ ] **B2 Degenerate caret is a no-op:** place the caret at the very start of a segment's text
  and press Enter — nothing splits (no empty child is created). Repeat at the very end of the
  text — same no-op.
- [ ] **B3 Derived-time field format + 10 ms edit:** on the new (second) half from B1, confirm
  the time field is visibly flagged as estimated (e.g. an "(estimated)" label) and shows
  `mm:ss.ff` (or `h:mm:ss.ff` past an hour) — hundredths of a second, i.e. a 10 ms grid. Edit
  the hundredths digits directly (e.g. nudge by 0.10) and tab out — the field accepts the new
  value on the 10 ms grid without error.
- [ ] **B4 Re-split a child:** press Enter again inside one of the two halves from B1, at a
  valid mid-text caret — that half splits again into two, so the segment now has three parts
  total, all with strictly increasing start times.
- [ ] **B5 Assign each half to a different speaker:** use the per-sub-row Speaker dropdown to
  assign the first half to one participant/speaker and the second half to a **different** one.
  Both selections are independent — changing one does not change the other.
- [ ] **B6 Whole-row (unsplit segment) speaker change:** on a **different**, unsplit row/segment,
  change its Speaker dropdown selection. This is the whole-section pin path (`speakers.json`),
  a separate channel from the split-child speaker in B5 — confirm after Save (Part D) that both
  took effect independently.
- [ ] **B7 Manage speakers:** click **Manage speakers...** — Session Details opens for this
  session, scoped to managing the roster (not a general edit — closing it returns you to the
  still-open Edit-mode table with your in-progress batch intact).

## Part C: Revert a split (see the flagged gap at the top of this file)

- [ ] **C1 Revert affordance:** on the split you created in B1/B4, look for a "merge back" /
  "revert split" control on one of its child sub-rows or via right-click. **Expected per
  design (`docs/specs/localscribe-specs.md` §1.6 revert semantics):** the control removes the
  split overlay and collapses the children back into the single original segment, with its
  original (non-estimated) time and text restored verbatim, and this composes into the same
  Save batch as any other pending edit. **If no such control exists in the build under test,
  this confirms the known gap noted at the top of this file — record it as a defect, do not
  spend further time hunting for a hidden gesture.**

## Part D: Save / Cancel / persistence

- [ ] **D1 Save commits the batch:** with the split (B1), the two speaker assignments (B5,
  B6), and any text edits still pending, click **Save**. The table returns to the read-only
  list, the header gains the "Edited" badge, and the row(s) you touched show the new text/
  speakers/split children — one visible reload, no flicker back to the top of the list (scroll
  position preserved).
- [ ] **D2 transcript.jsonl untouched:** "Reveal in Explorer" on the session — open
  `transcript.jsonl` in a text editor and confirm the machine-original line(s) for every seq
  you edited are **byte-for-byte unchanged** (same `text`, `startMs`, `endMs`). Open
  `edits.json` and confirm it now has a `splits` entry for the split seq (with `>= 2 parts`,
  the first part's `startMs` matching the original line, `derivedStart:false` on the first
  part and `true` on the rest) and that any plain `corrections` entry for that same seq is
  **absent** (a split supersedes and clears a prior correction).
- [ ] **D3 Cancel discards:** re-enter Edit, make a text change and a new split on a different
  row, then click **Cancel** instead of Save. The view returns to the read-only list unchanged;
  `edits.json`'s modification time and contents are unchanged from before you entered Edit this
  time (nothing written for a discarded batch).
- [ ] **D4 Reopen persistence:** close the read-view window entirely and reopen the same session
  (Sessions page → "View transcript" again). The Save from D1 is still there: the split
  children, both speaker assignments, and any text corrections render exactly as they did right
  after D1 — persistence survives a full window close/reopen, not just the in-session reload.
- [ ] **D5 No-op Save writes nothing:** enter Edit, expand a row, change nothing, click Save —
  no "Edited" badge appears if the session didn't already have one, and `edits.json`'s
  modification time is unchanged (a no-op batch commits nothing, per spec §1.6/§1.7).

## Part E: Long session — virtualization + `h:mm:ss` stamps

A session over an hour long is impractical to record live for a smoke pass, so synthesize one
by extending a real finalized session's `transcript.jsonl`. This keeps every other file
(`session.json`, etc.) schema-correct — only the JSONL is regenerated.

- [ ] **E1 Build the synthetic session.** In PowerShell:

```powershell
# Point $src at any existing FINALIZED session folder (has endedAtUtc set).
$src = "$env:USERPROFILE\LocalScribe\sessions\<some-existing-finalized-session>"
$dst = "$env:USERPROFILE\LocalScribe\sessions\2026-07-08_0900_Manual_synthetic-long-session"

Copy-Item -Recurse $src $dst
Remove-Item "$dst\edits.json","$dst\speakers.json" -ErrorAction SilentlyContinue

$segments = Get-Content "$dst\transcript.jsonl" |
    Where-Object { $_.Trim() -ne "" } |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { -not $_.kind -or $_.kind -eq "segment" }
if (-not $segments -or $segments.Count -eq 0) { throw "source session has no segments" }

$targetMs = 3700000   # > 1 hour
$out = New-Object System.Collections.Generic.List[string]
$seq = 0
$cursor = 0
while ($cursor -lt $targetMs) {
    foreach ($s in $segments) {
        $dur = [math]::Max(400, $s.endMs - $s.startMs)
        $line = [ordered]@{
            seq = $seq; kind = "segment"; source = $s.source
            startMs = $cursor; endMs = $cursor + $dur
            text = "[$seq] $($s.text)"; speakerLabel = $s.speakerLabel; lang = $s.lang
        }
        $out.Add(($line | ConvertTo-Json -Compress))
        $cursor += $dur + 400
        $seq++
        if ($cursor -ge $targetMs) { break }
    }
}
Set-Content "$dst\transcript.jsonl" $out -Encoding utf8

$session = Get-Content "$dst\session.json" | ConvertFrom-Json
$session.id = Split-Path $dst -Leaf
$session.durationMs = $cursor
$session.segmentCount = $seq
$session.endedAtUtc = ([DateTimeOffset]$session.startedAtUtc).AddMilliseconds($cursor).ToString("yyyy-MM-ddTHH:mm:ssZ")
$session | ConvertTo-Json -Depth 10 | Set-Content "$dst\session.json" -Encoding utf8
```

  Expected: the script completes without error and `$dst\transcript.jsonl` has several
  thousand lines spanning more than one hour of `startMs`.
- [ ] **E2 Session list picks it up:** refresh/reopen the Sessions page — the synthetic session
  appears with a duration over 1 hour.
- [ ] **E3 `h:mm:ss` stamps:** open the synthetic session's read view. Timestamps on rows past
  the first hour render as `h:mm:ss` (not `mm:ss`) — e.g. `1:02:14`, not `62:14`.
- [ ] **E4 Read-mode scroll is smooth:** scroll the full length of the read-only list. No
  stutter/hang consistent with realizing every row at once; memory stays sane (Task Manager —
  the app should not balloon to gigabytes).
- [ ] **E5 Edit-mode virtualization:** click **Edit** on the synthetic session, then scroll the
  full length of the editable table. Same expectation as E4 — smooth scroll, no full-transcript
  materialization. Expand one row somewhere in the middle (not near the top) — only that row's
  child sub-rows materialize; scrolling far away and back preserves its state (same as A5, at
  scale).

## Part F: Roster live-sync

- [ ] **F1 Live-sync without reopen:** open a finalized session's read view, click **Edit**,
  expand a row so its Speaker dropdown is visible and open (or at least visibly populated).
  Without closing the read view, click **Manage speakers...** (or open Session Details for the
  same session another way) and rename one of the participants. Save Session Details, close it.
  Back in the still-open Edit-mode table (no reopen, no manual refresh), the Speaker dropdown's
  entry for that participant now shows the **new** name, and any row already displaying that
  participant's name updates in place.
- [ ] **F2 Unrelated session unaffected:** with a second read view open for a **different**
  session, renaming a participant in the first session's Session Details does not change
  anything in the second session's window (the sync is session-scoped).
- [ ] **F3 Unsubscribe on close:** close the read view from F1, then rename the same participant
  again from Session Details. No error/crash occurs (confirms the roster-changed subscription
  was cleanly unsubscribed on window close, not left dangling).

## Notes / accepted quirks (not bugs)

- Split children of the same speaker with a sub-`gapMs` gap can re-merge into one paragraph in
  the **read-only** projection (list view, `transcript.md`/`.txt`, exports) — the Edit-mode
  table always shows them expanded regardless. This is expected (spec §6.1 step 6).
- `transcript.md`/`.txt`/`session.txt` for a hand-synthesized session (Part E) will not reflect
  the synthetic content unless something re-renders them; the read view and Edit-mode table
  read from `transcript.jsonl` + overlays directly and are unaffected.
- Editing stays gated to finalized/recovered sessions throughout — this is intentional, not a
  bug to report.
