# Stage 6 — Smoke Runbook (grows per phase)

Branch: `stage-6-corrections-vocab-export`. Phase 6.1 (read-view editing) is implemented;
Parts B (6.2 vocabulary) and C (6.3 export) are appended when those phases land.

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

Parts B (6.2 vocabulary) and C (6.3 export) are appended by their phases.
