# Stage 4 smoke runbook - session/Matter manager on real hardware

Prereqs: models fetched; Stage 3b B-series previously passed on this box; the 5 real Webex
sessions from earlier smokes present under the storage root.
Run: `dotnet run --project src/LocalScribe.App`

Known limitation carried into this runbook: microphone DEVICE PICKING is not in the Stage 4
settings UI (no capture-device enumeration API exists yet; the Mic group is a read-only
display). C9 verifies the display, not a picker.

## C1 - First-run consent (fresh %APPDATA%, accept + decline paths)
Steps: close the app; rename `%APPDATA%\LocalScribe` aside (do NOT delete - it holds real
settings); launch.
Expected: the consent dialog appears BEFORE any tray icon exists; it shows the local-recording
summary and the prominent "Recording others is your responsibility" statement. Decline path:
click "Decline and exit" -> the app exits, no tray icon ever appeared, and settings.json (if
written at all) has NO consentNotice field. Relaunch -> dialog shows again (detection is
field-absence). Accept path: click "I understand - continue" -> tray appears, settings.json
gains `consentNotice.acknowledgedAtUtc` + `appVersion`. Relaunch -> NO dialog, straight to the
main window. Closing the dialog with the title-bar X must behave as decline.
Record: pass/fail, the acknowledgedAtUtc value, then RESTORE the original %APPDATA% folder.

## C2 - Session list over the 5 real Webex sessions
Steps: launch; open the Sessions page. Then drop a junk folder into `sessions/`
(`mkdir sessions\zz-junk` + a garbage `session.json` containing `not json`), refresh
(navigate away and back).
Expected: all 5 real sessions list newest-first; dates render in each session's stored
offset; App/Medium = Webex; badges correct (no System-mix badge on clean per-process
sessions); durations match the 3a/3b smokes. With the junk folder present: the list still
loads and a footer note reads "1 unreadable folder" - visible, not silent, not blocking.
Record: pass/fail + row count + footer text. Delete the junk folder afterwards (it contains
no session data - it was never a session).

## C3 - Edit/tag round-trip -> session.txt shows new title + matter
Steps: BEFORE editing, hash the truth files of one session:
`Get-FileHash sessions\<id>\transcript.jsonl, sessions\<id>\session.json`. In the detail
pane: change the title, tag a matter (create one inline if none), commit fields.
Expected: a subtle "Saved" indicator per committed field (no Save button); meta.json changes;
`session.txt` re-renders with the NEW title and "Matter: Name (Reference)"; transcript.md
header shows the new title. Re-hash: transcript.jsonl and session.json are BYTE-IDENTICAL
(evidentiary invariant - user edits touch meta.json only). meta.json `edited` stays false
(metadata edits never flip the Edited flag).
Record: pass/fail + before/after hashes.

## C4 - Matter create/roster/archive + repair index
Steps: Matters page: create a matter (note the minted id, e.g. M-2026-001), add two roster
members (one with a role), archive it, toggle "show archived", un-archive. Then corrupt the
index: edit `matters/matters.json` and set the matter's sessionCount to 99; click
"Repair index".
Expected: minted id follows M-{yyyy}-{NNN}; roster members get p-<slug> ids; archived matter
leaves the default list and the Sessions-page Matter filter, reappears under "show archived";
repair recomputes sessionCount back to truth and adopts/drops nothing unexpectedly.
Record: pass/fail + minted matter id + repaired count.

## C5 - Read view + dual-leg audio + capture exclusion INSIDE a Webex screen share (primary use case)
Steps: open a read view for a real Webex session; play audio; toggle Local/Remote mutes; seek.
Then start a real Webex meeting, share the FULL screen, and look at the shared preview with
the main window, the read view, and the live view all open.
Expected: transcript rows are grouped by speaker with timestamps per settings; markers inline;
model/backend in the footer; QA fields nowhere. Audio: both legs play together (hear the
conversation); muting Local isolates the remote leg and vice versa; seek keeps the legs
paired. In the share: ALL LocalScribe windows are INVISIBLE in the shared/recorded view while
visible locally. Flip Settings > Privacy > exclude-from-capture OFF -> windows become visible
in the share (restart windows if the implementation applies it on open).
Record: pass/fail per sub-check (rows, audio pairing, mutes, exclusion on, exclusion off).

## C6 - Delete-to-Recycle-Bin (verify restorable)
Steps: record a 10-second THROWAWAY scratch session (never one of the 5 real ones). Delete it
from the Sessions page; read the confirmation dialog; confirm. Open the Windows Recycle Bin,
restore the folder, refresh the list.
Expected: the dialog shows title, date, duration, matter tags, and states audio + transcript +
metadata are all included; after confirm the folder is GONE from sessions/ and PRESENT in the
Recycle Bin (not permanently unlinked); restore brings the row back after refresh; any open
read view of that session was closed before the delete.
Record: pass/fail + confirmation that restore round-tripped.

## C7 - Single-instance activation
Steps: with the app running and the main window minimized, run
`dotnet run --project src/LocalScribe.App` again from a second terminal.
Expected: the second process exits on its own (no second tray icon, no error), and the FIRST
instance's main window is restored and brought to the foreground.
Record: pass/fail + observed activation latency.

## C8 - Recovery scan (kill mid-recording, relaunch, balloon + badge)
Steps: start a scratch recording; note the app PID (`Get-Process LocalScribe.App`); kill THAT
PID ONLY (`Stop-Process -Id <pid> -Force` - target the specific process, never a blanket
process-name kill). Relaunch.
Expected: the Sessions page shows "checking for interrupted sessions..." briefly; a tray
balloon "Recovered 1 interrupted session(s)" appears; the row flips from "Recovering..." to
normal with the Recovered badge; duration is transcript-derived (badge tooltip says so); the
transcript ends with the recovered-session marker; Start remains available the whole time.
Record: pass/fail + balloon text + recovered session id.

## C9 - Settings round-trip incl. launch-at-login + restart-required root change
Steps: Settings page: set audio format = wav, language = en, self name; verify each commits
(check settings.json). Toggle launch-at-login off then on; after each toggle run
`reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v LocalScribe`.
Verify the Mic group is a read-only display (known limitation, header note above). Pick a new
storage root via the folder picker; then pick a folder under OneDrive.
Expected: settings.json reflects every commit; the Run-key value disappears/reappears with the
toggle; root change shows the restart-required note and the list does NOT change until
restart; after restart the list is empty (new root) and the old sessions are untouched in the
old root (point the root back afterwards); the OneDrive pick shows the sync-provider warning;
the stored root is the LITERAL picked path (no %VAR% re-tokenizing).
Record: pass/fail per sub-check + the reg query outputs.

Record results (pass/fail + notes) inline here, per run, dated.

---

## Results

(none yet)
