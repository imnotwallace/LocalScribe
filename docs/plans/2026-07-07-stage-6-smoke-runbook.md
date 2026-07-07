# Stage 6 — Smoke Runbook (grows per phase)

Branch: `stage-6-corrections-vocab-export`. Phases 6.1 (read-view editing), 6.2 (custom
vocabulary + Record-console picker), and 6.3 (export) are all implemented; Parts A, B, and
C below cover each.

## Part A: Phase 6.1 — read-view editing (user, GUI)

Prep: build + run the app, open any **finalized** session that has a transcript in the read
view (double-click a session on the Sessions page, or "View transcript"). A session that is
still recording is intentionally not editable.

- [ ] **I1 Correct text:** right-click a transcript row → "Correct text...". The dialog lists
  the row's constituent lines, each seeded with the displayed text, with the machine original
  shown beneath it. Change one line, click **Save**. The row updates in place, the view does
  **not** jump to the top, the header gains the "Edited" badge, and the row shows "(edited)".
- [ ] **I2 Files follow the edit:** "Reveal in Explorer" on the session → open `transcript.md`,
  `transcript.txt`, and `session.txt` — all contain the corrected text; `transcript.jsonl` still
  contains the **original** line (the machine record is never rewritten).
- [ ] **I3 Revert:** re-open "Correct text..." on the same row — the corrected line now shows a
  "Revert to machine original" checkbox. Tick it, **Save**. The original text (plus any
  vocabulary pass) is back; `edits.json` no longer has that entry.
- [ ] **I4 Empty guard:** open "Correct text...", blank a line to only spaces, **Save** — the
  dialog blocks with a validation message and writes nothing (transcript content is never
  removed).
- [ ] **I5 Diff-only save:** open "Correct text...", change nothing, **Save** — the dialog
  closes and no `edits.json` is written / the "Edited" badge does not appear for that reason.
- [ ] **I6 Reassign to a participant:** on a session that has a named participant on the same
  side as a row, right-click → "Reassign speaker...", pick the participant, check the line(s),
  **Save**. The row relabels to that participant. If the participant had no diarised cluster,
  open Session Details and confirm the slot now shows a bound cluster key.
- [ ] **I7 Pin survives re-diarise:** on a splittable session, after pinning a line, run
  "Split speakers..." on that source and confirm — the pinned line keeps its assigned label
  after the re-run (pins and participant-owned keys are merge-protected).
- [ ] **I8 Unpin:** right-click the pinned row → "Remove speaker pin..." → **Yes**. The label
  falls back to the automatic/diarised result; diarised labels on **other** rows are untouched.
- [ ] **I9 No candidates:** on a session with no participants and no clusters on that side, the
  reassign dialog explains that people are added in Session Details and offers an "Open session
  details" button that opens that window.
- [ ] **I10 Playback undisturbed:** start playback, make a correction mid-play — audio keeps
  playing from the same position, the moving "now playing" highlight continues, and
  double-click-to-seek still works after the edit.
- [ ] **I11 Markers inert:** right-clicking a system-marker row (e.g. "audio device changed")
  shows no context menu.

Notes / known accepted quirks (not bugs — see design §9):
- Correcting one half of a phantom-bleed pair can make the previously-hidden duplicate reappear
  (dedup compares projected text). The `transcript.jsonl` always kept both.
- The live view (during recording) shows uncorrected text — corrections are render-time only.

## Part B: Phase 6.2 — custom vocabulary + Record-console picker (user, GUI)

- [ ] **V1 Global terms:** Settings > Custom vocabulary. Add a term, add a heard->correct
  correction, remove one of each. Re-open Settings - they persisted (settings.json under
  %APPDATA%/LocalScribe shows them under "vocabulary").
- [ ] **V2 Global validation:** try to add a blank term, and a duplicate term differing only in
  case ("Auth" then "auth") - both are refused with an info message; nothing is added.
- [ ] **V3 Correction on render:** add a global correction (e.g. "acme" -> "ACME Corp"), open a
  finalized session whose transcript contains that word - the read view shows the corrected form
  (corrections apply at render time).
- [ ] **V4 Per-matter vocab:** Matters page, select a matter, add a matter term + correction.
  Re-select the matter - they persisted (matter.json shows them).
- [ ] **V5 Re-render tagged:** with a session tagged to that matter, press "Re-render tagged
  sessions" - the status shows progress then clears; the tagged session's transcript.md/.txt now
  reflect the matter correction. (A vocab edit alone does NOT cascade - only this button does.)
- [ ] **V6 Record-console picker:** open the Record console (idle). Search + tick a matter under
  "Matters (optional)"; the summary updates. Start recording, speak a matter term, stop - the
  session is tagged with the picked matter (Session Details shows it) and the term was biased in
  the prompt (best-effort; verify the tag at minimum).
- [ ] **V7 Picker reverts:** after that recording ends, the console picker shows nothing selected
  (the pick is per-session, never saved to Settings).
- [ ] **V8 Untagged default:** start a recording without picking a matter - it records fine,
  untagged, global vocabulary only.
- [ ] **V9 Picker refresh (no stale catalog):** with the Record console already shown once, create
  a NEW matter on the Matters page, then reopen / re-show the Record console (or end a recording so
  it returns to idle) - the new matter now appears in the picker WITHOUT an app restart. (The picker
  reloads its catalog each time the console becomes visible and on return-to-idle.)

## Part C: Phase 6.3 — export (user, GUI)

- [ ] **X1 Session zip:** Sessions page, select a finalized session, action bar
  "Export..." -> Zip -> Save-As to a path. Confirm the `.zip` opens and contains the
  audio (if present) + `transcript.md`/`.txt` + `session.txt` + the JSON metadata layers;
  confirm the app reveals the new file highlighted in Explorer.
- [ ] **X2 Session docx:** Sessions page, "Export..." -> Word document, toggle
  timestamps off and markers off, Save-As. Open the `.docx` in Word: metadata header
  shows the curated participant roster (not raw diarised clusters), the
  machine-generated-accuracy disclaimer is present, the footer reads "PRIVILEGED &
  CONFIDENTIAL", the page size matches the machine's regional default (A4/Letter), and
  the timestamps/markers toggles are honoured (both absent from the body).
- [ ] **X3 Row context-menu mirrors + guards:** right-click a finalized session row ->
  "Export..." opens the same dialog as the action-bar button. Right-click a
  pending-recovery row and the live-recording session in turn -> export is
  disabled/blocked with an Info message (never silently produces a broken archive).
- [ ] **X4 Matter zip (skip + progress + cancel):** Matters page, select a matter with 2+
  tagged sessions (start a recording on one of them first so it is still live), detail
  pane "Export matter archive..." -> Save-As. Confirm: the `.zip` has one folder per
  finalized session plus a root `matter.json` snapshot; the live/recovering session is
  skipped and reported in the completion message rather than failing the export;
  progress advances visibly during the run. Repeat and press Cancel partway through -
  the half-written output file is deleted, nothing under the storage root is touched.
- [ ] **X5 No-audio and wav sessions:** export a session whose audio was removed
  (retention) and a session recorded in `.wav` format (Settings > audioFormat) - both
  produce a correctly formed `.zip` (audio entry absent for the first, `.wav` entry
  present for the second; no errors, no missing text layers).
