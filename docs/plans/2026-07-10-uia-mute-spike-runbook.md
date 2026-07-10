# App-mute signal capture runbook (rev. 2026-07-11; originally the UIA spike runbook of 2026-07-10)

> **Revision note (2026-07-11):** the original runbook targeted the Webex/Teams meeting toolbars and
> included "controls auto-hidden" steps. Both premises changed on evidence: (1) Webex on this setup
> reports its in-app mute to Windows 11's call-mute integration (the taskbar mic icon showed
> "Muted: Webex"), which is a strictly better, app-agnostic signal than toolbar scraping; (2) the
> Webex meeting toolbar never auto-hides on this setup, so the auto-hide steps were pointless. The
> capture target is now the TASKBAR TRAY ICON, per `docs/plans/2026-07-11-app-mute-awareness-design.md`.

## What this is for

LocalScribe's planned tier-3 mute banner reads ONE shell-owned UIA element: the Windows 11 taskbar
microphone tray icon, whose flyout shows lines like "Muted: Webex" and "Apps using your microphone:
Webex" for call apps integrated with the Win+Alt+K call-mute feature. Tray icons expose their live
status text through their UIA `Name` (verified for other tray icons on 2026-07-11), but the EXACT
strings the mic icon exposes, muted and unmuted, still need to be captured from a real call. Those
strings parameterize the watcher's parser and its tests — that is this runbook's single deliverable.

The probe tool (`tools/UiaProbe`) is read-only: it does not click anything, does not focus any
window, and does not change anything about your call.

**Hard rule (already decided, not up for revisiting here):** the app-mute signal is advisory only.
It will never write transcript markers and never gate recording. The banner's buttons invoke
LocalScribe's own "Mute my side" — the markers those produce come from your click, not from this
signal. If a future maintainer is tempted to "just add a marker since the signal looked solid" —
don't; an evidentiary transcript must never carry a claim derived from a signal an app vendor or an
OS update could silently break tomorrow.

## Prerequisites

- Windows box with the .NET 10 SDK (the same one used to build LocalScribe).
- A real Webex meeting you can join (a second device or a colleague/test meeting works; a solo
  "Personal Room" meeting is fine too).
- Optional: a real Teams meeting WITH at least one other participant joined (see the Teams check).
- This repo checked out locally (master).

## Running the probe

1. Open a terminal in the repo root.
2. Run:

   `dotnet run --project tools/UiaProbe -- CiscoCollabHost explorer`

   (`explorer` adds the taskbar/tray windows to the dump; `CiscoCollabHost` keeps the Webex windows
   as secondary for-the-record evidence.) The first run downloads FlaUI.UIA3 and its transitive
   dependencies and compiles; that is normal and only happens once.

   If the probe appears stuck for more than about 30 seconds, press Ctrl+C and re-run it: it walks
   every top-level window on the desktop, and one unresponsive app can stall a COM call mid-walk.
3. The tool prints the dump-file path when it finishes (timestamped; repeat runs never overwrite).

## The capture sequence

During ONE real Webex meeting, run the probe at each point and keep every dump:

1. **Joined, UNMUTED.** Confirm the taskbar mic icon is present first (hover it: the flyout should
   name Webex). Run the probe. This is the "live" baseline.
2. **Mute yourself inside Webex** (Webex's own mute button or Win+Alt+K — not anything in
   LocalScribe). Hover the tray icon and confirm the flyout says "Muted: Webex". Run the probe.
3. **Unmute again**, run the probe once more (confirms the strings flip back rather than latch).

Note which dump was which step (rename or keep a short list).

## What to extract (the single deliverable)

In each dump, find the taskbar tray section (`SystemTray` ... `NotifyItemIcon` buttons, inside the
`explorer` windows) and locate the microphone icon's entry. Record, verbatim, for each step:

- the element's `name='...'` string (this is what the watcher will parse);
- its `id='...'` and `class='...'` (selector stability);
- whether the entry disappears entirely when no app is capturing (expected — that is the watcher's
  fail-open Unknown state).

Two things can come back negative, and each has a planned answer: if the mic icon exposes NO useful
Name text, the watcher design falls back to... nothing — tier 3 is then not buildable as designed
and the design gets revisited with that evidence (do not improvise a scraper). If the strings turn
out locale- or version-unstable, they still live in exactly one parser class and are cheap to update.

## Optional Teams check (zero new code either way)

During a real Teams meeting with at least one OTHER participant joined and audio flowing: hover the
taskbar mic icon. If the flyout says "Muted: Teams" when you mute inside Teams, then Teams is
call-mute-integrated in real meetings (the 2026-07-11 empty-lobby test was inconclusive because
Teams released the mic while waiting) and the SAME watcher covers Teams automatically. If it only
ever says "Teams is using your microphone", Teams simply has no tier-3 signal — nothing further is
built for it (per the design's no-per-app-code rule). Either observation is a one-line note; a probe
dump while muted in Teams is a nice-to-have bonus.

## What happens next

The captured strings are handed to the watcher implementation plan (TrayTextParser patterns + pinned
tests). Nothing about the watcher gets built before then, and nothing beyond the tray signal gets
built at all — the toolbar-scraping approach this runbook originally served is rejected, not
deferred (design section 3).
