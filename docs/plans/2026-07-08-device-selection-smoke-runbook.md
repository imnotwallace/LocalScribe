# Device Selection smoke runbook (2026-07-08)

Prereq: build + run the real app (close any running LocalScribe.App.exe first, then
`dotnet build LocalScribe.slnx -c Debug --nologo` and launch LocalScribe.App). At least two
input devices connected (e.g. laptop mic + a USB headset).

## Part A - Settings persistent pin
- A1. Settings > Recording > Microphone: the dropdown lists "Windows Communications default
      (follow)" plus every connected input device by friendly name. Expected: all mics present.
- A2. Pin the USB headset; reopen Settings. Expected: settings.json `mic` = {mode:"pinned",
      id, name}; the dropdown re-opens with the headset selected.
- A3. Record a short session. Expected: session.json `devices.mic` = {mode:"pinned", id, name}
      of the headset, `fellBackToDefault:false`; local.wav is the headset's audio.

## Part B - Pinned-mic-gone fall-back + marker (the evidentiary path)
- B1. With the headset still pinned, UNPLUG it. Reopen Settings. Expected: the dropdown shows
      "{headset name} (not connected)" and keeps it selected (pin never silently dropped).
- B2. Record a short session with the headset unplugged. Expected: capture uses the
      Communications default; the transcript contains the `pinned microphone unavailable ->
      default` marker; session.json `devices.mic.mode` = "followDefault",
      `fellBackToDefault:true`; a tray/console Notice appeared.
- B3. Re-plug the headset, record again. Expected: back to Part A3 behavior (pinned, no marker).

## Part C - Record console per-session override
- C1. Set Settings mic to follow-default. Open the Record console. Expected: the Microphone
      dropdown shows follow-default selected; MicSummary reads "follows the Windows
      Communications default".
- C2. In the console, pick the headset; Start; Stop. Expected: THAT session captured the
      headset (session.json devices.mic); Settings mic is STILL follow-default (override never
      persisted).
- C3. After Stop (Idle), reopen the console. Expected: the mic dropdown reverted to
      follow-default (the per-session override cleared).
- C4. Pin the headset in Settings, then in the console override BACK to follow-default; record.
      Expected: that session captured the default; Settings still shows the headset pinned.

## Part D - Remote-app availability in Auto
- D1. Settings > Remote capture = Auto. Open the console. Expected: the "Record this app"
      selector is VISIBLE (was hidden pre-change) and empty.
- D2. With Webex/Zoom running, type/pick that app in the console; Start. Expected: session.json
      `devices.remote` = {mode:"perProcess", app:<that app>} - the chosen app captured per-app
      even though the base mode is Auto.
- D3. Set Remote capture = System mix. Open the console. Expected: the app selector is HIDDEN.
