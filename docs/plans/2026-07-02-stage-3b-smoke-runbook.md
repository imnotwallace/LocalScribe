# Stage 3b smoke runbook - WPF shell on real hardware

Prereqs: models fetched; 3a smoke S1 previously passed on this box.
Run: `dotnet run --project src/LocalScribe.App`

## B1 - Tray consent surface
Gray dot idle; Start -> red + RECORDING tooltip; Pause -> orange; Stop -> gray.
Notices appear as balloons (start with no meeting app: expect the system-mix fallback balloon).
Exit while recording prompts, finalizes (verify folder), then quits. Exit while idle just quits.

## B2 - Live view
Lines appear within a few seconds of speech, `[mm:ss] Me/Them: text`, markers italic.
Out-of-order finalization inserts ABOVE newer lines (talk over the remote side to force it).
Auto-scroll sticks to bottom; scrolling up stops following; closing the window does not stop
the recording.

## B3 - Overlay pill (the un-repeatable-call check, design decision 12)
Visible only while Recording/Paused. Timer ticks through Pause. Two bars: speak -> top bar
flicks; remote audio -> bottom bar flicks (this is the at-a-glance "both streams alive" check).
Pause/Stop work WITHOUT stealing focus from the foreground app (type in Notepad, click Pause,
keep typing - caret must not leave Notepad). No taskbar/alt-tab entry.
Tooltip shows NO session name by default; flip overlay.showSessionName in settings.json and
confirm tooltip-only opt-in.

## B4 - Screen-capture exclusion
Share the full screen in a Webex/Teams/Zoom call (or OBS display capture): the pill must be
INVISIBLE in the shared/recorded view while visible locally. Flip overlay.excludeFromCapture
to false, restart, confirm it becomes visible in the share.

## B5 - Position memory + clamp
Drag the pill somewhere, exit, relaunch, start: same spot. Fake a monitor change: edit
window-state.json to x=99999, relaunch, start: pill clamps back on-screen.

## B6 - End-to-end Webex (primary use case)
Real Webex 1:1: tray start, overlay confirms both bars alive, live view transcribes both
sides, stop from the OVERLAY, folder verifies as in 3a S2 (per-process, no degraded marker).

Record results (pass/fail + notes) inline here, per run, dated.

---

## Results

### 2026-07-02 - autonomous launch-stability smoke (build + unit gate + headless process check)

**Why this run looks different from B1-B6:** Stage 3a's LiveRunner is a console app with an
`--auto <seconds>` flag, so its smoke could be driven headlessly end-to-end (see the 3a
runbook). LocalScribe.App is a GUI tray app - Start/Pause/Stop, the overlay pill, and the tray
balloons are only reachable by real mouse/keyboard input against a real desktop session, and
there is no equivalent `--auto` affordance (nor should there be - the tray IS the consent
surface, so faking input into it would defeat the point of B1). **B1-B5 were NOT executed in
this run; they remain desktop/human verification items, exactly as flagged in the task
context.** What follows is the subset that genuinely can run unattended: build, the full unit
gate, and a real process-launch stability check of the composition root this box can perform
without a human clicking anything.

**1. Build.** `dotnet build LocalScribe.slnx` (all 7 projects: Core, OfflineRunner, SpikeRunner,
LiveRunner, App, Core.Tests, App.Tests):

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

`LocalScribe.App.exe` is produced at
`src/LocalScribe.App/bin/Debug/net10.0-windows/LocalScribe.App.exe`.

**2. Unit gate.** `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`:

```
Passed!  - Failed: 0, Passed: 21, Skipped: 0, Total: 21, Duration: 579 ms - LocalScribe.App.Tests.dll (net10.0)
Passed!  - Failed: 0, Passed: 213, Skipped: 0, Total: 213, Duration: 1 s - LocalScribe.Core.Tests.dll (net10.0)
```

213 + 21 = 234 - matches the Definition of Done gate (`Core.Tests + App.Tests`, 234 green).

**3. Launch-stability smoke (real desktop, this dev box).** This box does have an interactive
desktop session available to the agent (not session 0 / no-window-station), so rather than
skipping this step as environmentally impossible, the built exe was actually launched and
observed:

- Launched `LocalScribe.App.exe` directly (`Start-Process -PassThru`, stdout/stderr
  redirected to files), PID 24752, at 2026-07-02T20:27:53+10:00.
- Waited 9 seconds, then re-checked the process: **still running, `Responding = True`**
  (a hung/deadlocked WPF message pump would show `Responding = False`).
- Inspected loaded modules on the live process: `PresentationFramework.dll`,
  `PresentationCore.dll`, `WindowsBase.dll`, `Wpf.Ui.dll`, `H.NotifyIcon.Wpf.dll`,
  `H.NotifyIcon.dll` were all loaded - i.e. the real composition root ran
  (`CompositionRoot.Build()` -> `SessionController` construction), `App.OnStartup` built the
  `SessionViewModel`/`TranscriptLinesViewModel`, the WPF-UI theme resources loaded, and
  `TrayIconHost` created the actual `H.NotifyIcon` tray icon - without an unhandled exception
  anywhere in that path. Working set ~101.5 MB; `MainWindowTitle` empty, which is expected for
  a tray-only app with no visible top-level window at idle (the overlay window exists but stays
  `Hidden` until a session starts, per `App.xaml.cs`'s `IsVisible` toggle).
- stdout and stderr redirect files were both empty - expected for a `WinExe`/WPF process (no
  console is attached, so nothing is written there even on success); no exception text, no
  Watson/WER crash-dump prompt, no `FAULT:`-style output.
- Terminated only PID 24752 via `Stop-Process -Id 24752 -Force` (confirmed by PID, not a
  blanket `dotnet`/process-name kill, per the standing rule against killing all
  dotnet/npm processes); a follow-up `Get-Process -Id 24752` after the stop confirmed the PID
  was gone. Note this was a forced kill (verifying "launches and stays alive, and this exact
  process can be torn down cleanly by PID"), not a graceful Exit through the tray's own guarded
  exit path - that graceful/prompt-on-recording behavior is B1's job and needs a human clicking
  the tray menu.

**Outcome: PASS for everything that can run unattended** - clean 0-warning build, 234/234 unit
tests green, and a real launch of the actual GUI executable on real hardware that starts,
initializes the full WPF/tray/composition-root stack, stays alive and responsive for the
observation window, and tears down cleanly when targeted by PID. This is a genuine integration
smoke of startup wiring (DI/composition root, WPF-UI theme resources, H.NotifyIcon tray
creation) even though no human verified the tray dot's color or the overlay's pixels in this
run.

**What this run explicitly did NOT verify (documented as open items, not failures):**
- **B1-B5 require a human at this box's interactive desktop** - tray dot colors/balloons,
  live-view line ordering and auto-scroll, the overlay pill's two-bar meters and pause/stop
  without focus-steal, screen-capture exclusion (needs a real Webex/Teams/Zoom share or OBS),
  and drag-to-remember + clamp-on-fake-monitor-change. None of these have a programmatic/`--auto`
  driving path in a GUI tray app, and building one would compromise the thing being tested
  (consent-surface authenticity), so they were correctly left for interactive execution rather
  than faked.
- **B6 (real Webex end-to-end) requires an actual Webex 1:1 call** - a user action item, same as
  3a's S2/S3 before it.
- These are exactly the items called out in this task's brief as the merge bar's remaining
  pieces: B1-B5 (desktop human verification) before the 3b branch merges, B6 (real Webex) before
  Stage 3 overall is declared done. Nothing here was worked around or assumed passing.
