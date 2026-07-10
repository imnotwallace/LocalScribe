# UIA app-mute spike runbook (2026-07-10)

## What this is for

LocalScribe cannot reliably "follow" Webex's or Teams' own mute button -- no conferencing app
tells Windows when you mute yourself inside the app. The plan (`docs/plans/2026-07-10-mute-
controls-and-echo-dedup-design.md`, section 3) is to add an ADVISORY banner that scrapes the
meeting app's on-screen mute control via UI Automation (UIA) -- the same technique tools like
MuteDeck use. That banner is best-effort and can break whenever Webex/Teams changes its UI, so
before any of that code gets written we need real evidence of what the UIA tree actually looks
like during a live call, muted and unmuted.

This runbook drives a small read-only probe tool (`tools/UiaProbe`) that walks the UIA tree of
the Webex/Teams window and dumps it to a text file. It does NOT click anything, does NOT focus
any window, and does NOT change anything about your call. It only reads.

**Hard rule (already decided, not up for revisiting here):** the advisory watcher this spike
feeds into will never write transcript markers. It only ever shows a banner like "Webex looks
muted -- note that LocalScribe still records your microphone." The mute button that actually
stops recording is LocalScribe's own "Mute my side" control, not this signal. This runbook exists
only to gather evidence for how reliable that advisory banner can be -- do not build the watcher
from anything other than these findings.

## Prerequisites

- Windows box with the .NET 10 SDK installed (the same one used to build LocalScribe).
- A real Webex meeting you can join (a second device or a colleague/test meeting works; a
  solo "Personal Room" meeting is fine too).
- Optional: a real Teams meeting, if you want to cover both apps in one pass.
- This repo checked out locally with the `mute-controls-echo-dedup` branch checked out.

You do not need to build or run the LocalScribe app itself for this -- the probe tool is
completely separate and does not touch LocalScribe.Core or LocalScribe.App.

## Running the probe

1. Open a terminal in the repo root (`F:\LocalScribe` or wherever you checked it out).
2. Run:

   `dotnet run --project tools/UiaProbe`

   The first run will download the FlaUI.UIA3 NuGet package plus its transitive dependencies
   and compile; that is normal and only happens once. Subsequent runs are fast.

   If the probe appears stuck for more than about 30 seconds, press Ctrl+C and re-run it: it
   walks every top-level window on the desktop, and one unresponsive app can stall a COM call
   mid-walk.
3. The tool prints one line when it finishes, for example:

   `wrote F:\LocalScribe\tools\UiaProbe\bin\Debug\net10.0-windows\uia-dump-20260710-153000.txt (48213 chars)`

   That is the dump file. The filename is timestamped so repeat runs never overwrite each other.
4. By default the tool looks for windows belonging to processes named `CiscoCollabHost`
   (Webex), `ms-teams`, or `Teams`. If it finds none of those running, it still writes a file --
   just a very small or empty one -- and exits cleanly (that is the "no meeting open" baseline
   case, already verified as part of building this tool).
5. If you want to target a different process by name, pass it as an argument, e.g.:
   `dotnet run --project tools/UiaProbe ms-teams`

## The capture sequence

Do these steps in order, during ONE real Webex meeting. Run the probe command from step 1
above at each of the four points below, and keep every dump file it produces (do not delete
between runs -- the timestamp in the filename keeps them separate).

1. **Join the meeting, UNMUTED**, with the normal meeting-controls toolbar visible on screen
   (move the mouse so it doesn't auto-hide). Run the probe. This is your "baseline unmuted"
   dump.
2. **Mute yourself inside Webex** (click Webex's own mute button, not anything in LocalScribe).
   Controls toolbar still visible. Run the probe again. This is your "muted, visible" dump.
3. **Let the Webex meeting-controls toolbar auto-hide** (stop moving the mouse over the video
   area until the controls disappear on their own), while still muted. Run the probe again.
   This is your "muted, controls hidden" dump.
4. **Unmute yourself again**, let the controls auto-hide, and run the probe one more time. This
   is your "unmuted, controls hidden" dump. (This tells us whether the mute toggle element is
   still reachable in the tree even when nothing is visibly on screen, which matters because a
   background poller cannot "move the mouse" to bring the toolbar back.)

If you can also join a Teams meeting (new Teams, i.e. the `ms-teams` process, not classic
Teams), repeat the same four steps there. Teams is a secondary target -- Webex is LocalScribe's
primary use case -- so this is optional but valuable if you have a spare few minutes.

You should end up with 4 dump files (or 8, if you also did Teams). Each filename has its own
timestamp so you can tell them apart -- just note in your own words which run was which step
(e.g. rename them or keep a short list: "...153000 = unmuted", "...153145 = muted", etc.).

## What to do with the dump files

Attach/send all the dump files you produced (do not edit them). If you want to sanity-check
them yourself first, each file is plain text -- open it in Notepad. You should see a block per
window like:

```
===== window: 'Webex Meetings' process=CiscoCollabHost class=Chrome_WidgetWin_1 =====
[Button] id='mute-button' name='Mute' class=''
  ...
```

## Findings to extract from the dumps (this is what the next design step needs answered)

Whoever reviews the dumps (could be you, could be handed to the next LocalScribe work session)
needs to answer these four questions, separately for each app (Webex, and Teams if captured):

1. **Is the mic-mute toggle in the UIA tree at all?** Some Electron/CEF-based apps (Webex and
   Teams both run on Chromium-derived frameworks) only populate accessibility trees when a
   screen reader is active, or hide certain controls from UIA entirely. If the mute button
   never shows up in any dump, the advisory watcher cannot be built for that app at all.
2. **Does it carry a stable, language-independent `AutomationId`?** `AutomationId` values (if
   present) are usually consistent across UI language and app version, unlike `Name`, which
   Webex/Teams may localize ("Mute" / "Coupez le son" / etc.) or otherwise change. Compare the
   `id='...'` value for the mute button across ALL your dumps (muted and unmuted) -- if it is
   the same identifier every time, that is the strongest possible selector to build the watcher
   on. If `id=''` (empty) in every dump, there is no AutomationId to rely on and the watcher
   will have to fall back to something weaker (position in the tree, `ClassName`, etc.).
3. **Does `TogglePattern` reflect the mute state, or does only the `Name` change?** Compare the
   mute-button line between your "unmuted" and "muted" dumps. If the line has a
   ` TOGGLE=On`/` TOGGLE=Off` suffix that flips between the two dumps, `TogglePattern` is a
   reliable, name-independent signal. If there is no `TOGGLE=` suffix at all (meaning
   `Patterns.Toggle.IsSupported` was false), the only difference between the two dumps will be
   the `name='...'` text itself (e.g. `name='Mute'` becomes `name='Unmute'`) -- which is a much
   weaker, language-dependent signal.
4. **Does the element survive controls auto-hide?** Compare a "controls visible" dump against
   its matching "controls auto-hidden" dump (steps 2 vs 3, and 1 vs 4, above). If the mute
   button's tree entry is present and unchanged (same id, same toggle state) in the hidden-
   controls dump, a background poller can keep reading it even when nothing is on screen. If
   the element disappears from the tree (or the whole toolbar subtree vanishes) when hidden,
   the watcher will need a way to force the controls to reappear, or will simply go
   `Unknown`/blank whenever the user isn't actively hovering the meeting window -- which may be
   most of the time in a real call.

## What happens next (and what does NOT happen from this runbook alone)

The findings above are handed to a follow-up planning pass that designs the actual
`IAppMuteWatcher` implementation and its per-app selectors (Webex adapter first, Teams second).
**Nothing gets built speculatively from assumptions** -- if a question above comes back
negative (e.g. no AutomationId, or the element vanishes on auto-hide), the plan adapts around
that limitation rather than pretending it doesn't exist. And regardless of how clean the
signal turns out to be, the watcher's output stays advisory-only per the locked design decision:
a banner, never a transcript marker, never a recording gate. If a future maintainer re-reads
this runbook and is tempted to "just add a marker since the signal looked solid" -- don't; that
decision was made deliberately (see design doc section 3) because an evidentiary transcript must
never carry a claim derived from a signal an app vendor could silently break tomorrow.
