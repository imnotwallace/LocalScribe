# Part 2 manual smoke runbook — human-authored transcript lines

**STATUS: OUTSTANDING. This has NOT been run. It must be run by the owner before this branch
(`feat/part2-line-insertion-2026-08-11`) is merged.**

## Why this exists and why it wasn't run as part of Task 12

Task 12 (spec audit, both automated gates, warning baseline, ASCII check) was completed by an
agent. The agent that did that work explicitly did **not** launch `LocalScribe.App.exe` and did
**not** perform any of the checks below. Two reasons, both load-bearing:

1. **No agent in this loop can observe a running WPF window.** XAML binding failures are silent —
   a broken `{Binding}` does not throw, does not fail a build, and does not fail a test in this
   suite. The only way to catch one is a human looking at the actual window. An agent "running" the
   app and reporting success would be reporting nothing.
2. **Running `LocalScribe.App.exe` locks `Core.dll`.** If that lock is left in place (e.g. the
   agent's process is killed rather than exited cleanly, or the window is left open across a
   subsequent build), the next `dotnet build` in this repo fails with `MSB3027` — a build break
   left behind by the very step meant to verify the branch is safe to merge.

Everything else in Task 12's brief (the spec audit, both `dotnet test` gates, the `--no-incremental`
warning count, the ASCII-in-`src/` diff against the branch point) was completed and is reported in
`.superpowers/sdd/2026-08-11-part2-human-authored-transcript-lines/task-12-report.md`. This document
is the one piece of Task 12 that is a human's to run, not an agent's.

## Before you start

- Build the branch: `dotnet build LocalScribe.slnx -c Debug` from `F:\LocalScribe`.
- Close every running `LocalScribe.App.exe` first — it locks `Core.dll`, and a stale lock will make
  the build above fail with `MSB3027`.
- Launch `LocalScribe.App.exe` and open (or create) a real **finalized** session — the insertion UI
  is gated on finalized/recovered sessions, same as every other edit-mode gesture (spec §1.6).

## The thirteen checks

Record PASS/FAIL for each. A failure here is a **defect in this branch**, not a note for later —
per the plan's own verification checklist, all thirteen must pass before merge.

Checks 11–13 were added after the whole-branch review (2026-08-11) found that every defect it
raised lived in a **pre-existing surface no task on this branch edited** — the read view's other
editors, search, the assistant — which now meet an inserted row for the first time. Each of the
three has an automated regression test with break-the-fix evidence; they are listed here as well
because the three are the only checks that exercise a **merged** row through the real window, and
merging is the ordinary case rather than an edge case: `SectionGrouper` merges purely on
`DisplayName` equality, so an inserted line attributed to a named participant joins that
participant's own machine turn, and `TranscriptProjection.InsertedRank = -1` puts it FIRST in it.

1. **Read view → right-click a speech row → `Add line here...` opens the dialog with the row's
   timestamp seeded.**
   *If this fails:* either the context menu item is missing/misrouted (check `WindowProxy` wiring
   in `ReadViewWindow.xaml`), or the seeded timestamp is wrong, which would let a person believe
   they are placing a line at a moment they are not — an evidentiary accuracy problem, not cosmetic.

2. **Type text, pick a speaker, Save. The line appears immediately before the row you clicked, with
   the chosen speaker's name.**
   *If this fails:* the insertion sort key (`TranscriptProjection.InsertedRank = -1`) or the
   speaker-attribution write path is broken — either the line lands in the wrong position (breaks
   the "insert before what I clicked" contract every user will assume) or under the wrong name,
   which is a misattribution in an evidentiary transcript.

3. **Hover its timestamp: the tooltip reads `~[stamp] (estimated)`.**
   *If this fails:* an inserted line's necessarily-estimated timestamp is being presented as if it
   were machine-derived — exactly the concealment the design's disclosure rules exist to prevent.

4. **Right-click a marker row: the menu is suppressed (existing behaviour, unchanged).**
   *If this fails:* Part 2 regressed existing, unrelated behaviour — the context menu's marker
   suppression predates this branch and must still hold.

5. **Enter Edit mode. The inserted line appears as a sub-row with `(estimated)` and a Remove button;
   machine sub-rows have no Remove button.**
   *If this fails:* either the edit-mode rendering of an insertion is wrong (missing the estimate
   flag misrepresents provenance, same as check 3), or a machine sub-row incorrectly offers Remove
   — which would expose a way to delete machine evidence, violating the no-deletion invariant
   (spec §1.1/§1.6).

6. **Click `+ Add line`, type text, Save. Both lines survive.**
   *If this fails:* the edit-mode insertion path (distinct code path from the Read-mode dialog in
   check 1-2) is broken, or a save is clobbering a previously-saved insertion instead of
   accumulating both — a silent data-loss defect.

7. **Remove the first inserted line, Save. It is gone; the machine rows are untouched.**
   *If this fails:* either removal isn't reaching `edits.json` (the line reappears on reload), or —
   far worse — removal is touching `transcript.jsonl` or a machine row's content, which would be a
   direct violation of the no-deletion-of-machine-evidence rule this entire app is built around.

8. **Export to `.docx`: the inserted line reads plainly in the body with no per-line mark, and the
   disclaimer is the "reviewed and corrected by a person" variant.**
   *If this fails:* either a stray per-line mark leaked into the export (the design deliberately
   deleted per-turn correction marks in Part 3 — an insertion must not resurrect that pattern), or
   the disclaimer is still the "untouched" variant, which would be a **false statement in an
   exported evidentiary document** — it would tell a reader no person touched a transcript a person
   just wrote into.

9. **Open `manifest.json`: `insertedLines` lists the surviving line; `schemaVersion` is `1`.**
   *If this fails:* either the manifest disclosure isn't wired to the real overlay (a gap between
   what was inserted and what the integrity seal discloses), or `schemaVersion` was bumped when it
   should not have been (per Task 6's ruling: `insertedLines` needs no schema bump — see the
   design spec §2.3 Provenance correction).

10. **Open `edits.json`: `schemaVersion` is `2` while an insertion exists.**
    *If this fails:* `EditStore.WriteVersion`'s conditional stamping (2 only when `insertions` is
    non-empty) is not behaving as designed — either it's stuck at 1 (an older build would silently
    drop the insertion it can't read a version-1 file into expecting) or it's unconditionally 2
    (which would lock every older build out of every session, insertions or not — see
    `EditStore.cs:15-29`).

11. **BLOCKING. With an inserted line present, right-click its row → `Correct text...`, change the
    text, Save, then scroll to the very top of the transcript.** The dialog must list only the
    MACHINE lines of that row — the inserted line must not appear in it at all — and the
    transcript's first machine turn must read exactly what it read before, with no "(edited)"
    state on it.
    *If this fails:* the person's inserted words have been written into `edits.json` as
    `corrections["0"]` and now REPLACE the first machine turn's text in the `.docx`/`.md`/`.txt`
    exports, in search and in the MCP corpus, flagged as a human correction of something the
    machine never said. An inserted line's `Seq` is a placeholder `0` and machine seqs also start
    at `0`, so nothing downstream can tell the two apart once the key is written — the storage
    layer's own guard passes, because machine seq 0 genuinely exists.
    (Guarded by `ReadViewViewModelTests.Correcting_an_inserted_lines_text_never_lands_on_machine_seq_0`.)

12. **BLOCKING. Add a line attributed to the SAME named speaker as the row you clicked. Confirm it
    merges into that row (one turn, that speaker's name, the inserted line first), then try
    `Reassign speaker...` and `Reassign all of this speaker...` on it.** Both dialogs must open
    with that speaker's own side's roster, with the row's real machine lines TICKABLE, and a save
    must complete without an error message.
    *If this fails:* reassignment is dead on an ordinary turn. The symptom to expect is every real
    line rendered greyed/disabled with only the inserted line selectable, the wrong side's list of
    people offered, and a raw exception string on save (`clusterKey 'Remote:1' is not
    System-prefixed`, or `seq 0 belongs to the Local stream, not System`).
    (Guarded by `ReadViewViewModelTests.CreateReassign*_takes_its_side_and_lines_from_the_machine_segments`.)

13. **BLOCKING. Search for a word that appears ONLY in an inserted line, then click the hit.** The
    read view must scroll to that human-authored line.
    *If this fails:* the click-through payload carried the placeholder `Seq` of 0 with no insertion
    id, so the reader lands on the transcript's first machine turn — a search result that silently
    points at the wrong moment in the recording.
    (Guarded by `SearchPageViewModelTests.A_hit_on_an_inserted_line_carries_its_insertion_id_through_to_the_click`.)

## After running

Update this document's STATUS line at the top (OUTSTANDING → PASSED / FAILED, with date and who
ran it), and record the per-check PASS/FAIL list. If any check fails, do not merge — file it as a
defect against the relevant task's commit and fix it with a real (non-manual) regression test where
one is feasible, per this branch's own discipline of TDD'd tasks.
