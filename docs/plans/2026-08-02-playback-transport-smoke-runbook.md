# Read-View Playback Transport - Smoke Runbook (UX round 2026-08-02, items 7-9)

Feature: transport-bar changes from `docs/superpowers/specs/2026-08-02-ux-round-design.md`
sections 7 (Sync transcript follow toggle), 8 (go-to timestamp box), 9 (contextual channel
mixer). Run after the plan's automated gates (App + Core test suites, solution build) are
green.

## Prep

- Build and run the app (close any previously running `LocalScribe.App.exe` first).
- Have a finalized DUAL-LEG session (both Local and Remote audio retained) whose transcript is
  long enough to scroll well past two viewport heights, including at least one long
  single-speaker monologue section.
- Have a SINGLE-LEG session (e.g. an imported audio file - import produces one leg).

## Part A: Sync transcript follow toggle (item 7)

- [ ] **A1 Pill placement:** open the dual-leg session's read view. A "Sync" pill with a
  sync-arrows icon sits in the transport bar directly after Stop. Tooltip reads "Keep the
  transcript scrolled to the line being played". It starts OFF on every fresh window.
- [ ] **A2 Follow during play:** press Play, enable Sync, let playback cross several section
  boundaries. Each time the highlight advances to a new row, the list scrolls so the playing
  row sits roughly one third from the viewport top (never pinned at the bottom edge).
- [ ] **A3 Snap on enable:** with playback deep in the transcript and Sync OFF, scroll far
  away manually, then enable Sync - the list snaps to the playing row immediately, without
  waiting for the next section boundary.
- [ ] **A4 Wheel disengages:** with Sync ON during play, scroll the mouse wheel over the
  transcript - the Sync pill turns itself OFF and the list stays where you put it.
- [ ] **A5 Scrollbar thumb disengages:** re-enable Sync, then drag the transcript scrollbar
  thumb - Sync turns OFF.
- [ ] **A6 PageUp/PageDown disengages:** re-enable Sync, focus the list, press PageUp -
  Sync turns OFF. (Repeat with PageDown.)
- [ ] **A7 Follow does not self-disengage:** enable Sync and let playback run hands-off
  across at least five section advances - the pill stays ON the whole time (the toggle's own
  scrolls never count as user intent).
- [ ] **A8 Monologue nudge:** while a long single-speaker section is playing with Sync ON,
  resize the window (or open the Ask panel) so the playing row leaves the viewport - within
  a beat (~150 ms tick) the list is nudged so the playing row is visible again.
- [ ] **A9 Edit-mode inertness:** with Sync ON, click Edit. The Sync pill renders disabled;
  playback keeps running; the edit table does NOT scroll on section advances. Cancel -
  the pill is enabled again, still checked, and follow resumes on the next section advance.
- [ ] **A10 Scrub behaviour (accepted):** with Sync ON, drag the seek slider - the list
  freezes during the drag, then jumps once to the new playing row on release.
- [ ] **A11 -1 sentinel:** Stop playback (position 0, before the first row's window if your
  fixture starts late) - no scroll fires; enabling Sync with no current row does nothing.
- [ ] A12 With Sync ON and the find bar open during playback: navigate to a find match, let
  playback advance a row - the view follows playback (find jump is overridden on the next
  advance). Confirm this feels acceptable; if not, file a follow-up to treat find navigation
  as a disengage gesture.

## Part B: Go-to timestamp box (item 8)

- [ ] **B1 Placement + focus:** in the dual-leg session's read view, a "Go to" label and a
  small text box sit after the total-duration label in the transport bar. Press Ctrl+G from
  anywhere in the window - the box gets focus with any existing text selected.
- [ ] **B2 Relative jump:** with the timestamps setting on "relative", type a mid-transcript
  stamp exactly as a row label shows it (e.g. `03:15`) and press Enter - playback position
  and the seek slider jump there, the list scrolls to the target row (about one third from
  the top) even though Sync is OFF, and the row highlight lands within a beat.
- [ ] **B3 Sync state untouched:** repeat B2 once with Sync ON and once with Sync OFF - the
  pill's state is identical before and after the jump in both cases.
- [ ] **B4 Wallclock jump:** switch Settings > Timestamps to wall-clock, reopen the read
  view, type a stamp as displayed (HH:mm:ss) and press Enter - it lands on the matching row.
  Switch the setting back afterwards.
- [ ] **B5 Clamp:** type a stamp far past the end of the audio (e.g. `59:59`) - playback
  lands at end-of-media, scrolled to the last section; no error state.
- [ ] **B6 Quiet error:** type `garbage` and press Enter - the box gets a red outline, the
  text stays exactly as typed, NO dialog appears, and playback does not move. Type one more
  character - the red outline clears immediately.
- [ ] **B7 Esc:** press Esc in the box - focus returns to the transcript list (arrow keys
  now move the list selection). In Edit mode, Esc focuses the edit table instead.
- [ ] B8 While in Edit mode, type a timestamp in Go to and press Enter - the EDIT list
  scrolls to the target section (transport stays visible during edit).

## Part C: Contextual channel mixer (item 9)

- [ ] **C1 Dual-leg shape:** open the dual-leg session's read view. The transport shows a
  "Channels" group with two rows labelled "Local (my side)" and "Remote (other party)", each
  with a Mute pill and a volume slider. The old free-floating "Mute local"/"Local vol"
  clusters are gone.
- [ ] **C2 Dual-leg function:** during playback, toggle each Mute pill (icon flips to the
  crossed-out mic, pill fills accent) and drag each slider - the corresponding leg silences /
  changes level independently; the other leg is unaffected.
- [ ] **C3 Single-leg shape:** open the single-leg session's read view. NO mute pills appear
  anywhere in the transport; there is exactly one slider, labelled "Volume".
- [ ] **C4 Single-leg function:** during playback, drag the Volume slider to near zero and
  back - the audio level follows.
- [ ] **C5 No-audio session:** open a session with no retained audio - the entire transport
  bar (including mixer and go-to box) stays hidden, exactly as before this round.
- [ ] **C6 Narrow-window wrap:** narrow the window until the transport wraps - the Channels
  group wraps as one unit (its rows stay intact); nothing clips off the window edge.
