# Tier 1B smoke runbook (evidence-loss round)

Branch `feat/tier1b-tier-b-stop-losing-evidence-2026-08-05` @ `cf019a3`.
Static suite is green (Core 1268 / App 1082 / Mcp 6). **These nine items are the ones a static
suite cannot settle** - two have no automated test by design, the rest need real hardware.

Do not merge until items 1-4 pass. Items 5-9 need specific hardware/conditions; skip-with-a-note
is acceptable for those, failing is not.

---

## Setup (do this first - one of these WILL bite you)

**1. Point the app at the models.** This is the trap. `ModelPaths` walks up for `LocalScribe.slnx`
and then looks for `models\` beside it - and this worktree has its own `.slnx` but **no `models\`
folder**. Without the override, Start refuses with "Model 'small.en' is not downloaded" and you will
think the round broke recording.

```powershell
$env:LOCALSCRIBE_MODELS = 'F:\LocalScribe\models'
```

Set it in the SAME shell you launch from. (Verified 2026-08-06: worktree `models\` absent, main
repo `models\silero_vad.onnx` present.)

**2. Close any running LocalScribe.** The app holds a single-instance guard named `LocalScribe`, so
a second copy hands off its argv to the first and exits - you would be smoking the OLD build without
noticing.

```powershell
Get-Process -Name 'LocalScribe.App' -ErrorAction SilentlyContinue |
    Select-Object Id, Path        # expect nothing; if not, close it from its tray icon
```

**3. Launch this branch's build.**

```powershell
cd F:\LocalScribe\.claude\worktrees\feat+tier1a-tier-a-diagnosability-2026-08-05
dotnet run --project src/LocalScribe.App
```

Tray-first: look for the tray icon. Confirm you are on the right build via
**Settings > About** - it must show `0.9.0+g` and a SHA that starts `cf019a3` (or whatever
`git rev-parse --short HEAD` says).

**4. Know where to look.** Storage root defaults to `%USERPROFILE%\LocalScribe`:

- sessions: `%USERPROFILE%\LocalScribe\sessions\<session-id>\`
- the record: `session.json` (fields `endedAtUtc`, `durationMs`, `retainedAudioSources`, `recovered`)
- markers: `transcript.jsonl` (lines with `"kind":"marker"`)
- diagnostics: `%USERPROFILE%\LocalScribe\diagnostics\diag-<yyyyMM>.jsonl`

Paste this helper into your shell - every item below uses it:

```powershell
function Show-Session([string]$Id) {
  $d = Join-Path $env:USERPROFILE "LocalScribe\sessions\$Id"
  $s = Get-Content (Join-Path $d 'session.json') -Raw | ConvertFrom-Json
  "endedAtUtc  : $($s.endedAtUtc)"
  "durationMs  : $($s.durationMs)"
  "retained    : $($s.retainedAudioSources -join ', ')"
  "recovered   : $($s.recovered)"
  "--- markers ---"
  Get-Content (Join-Path $d 'transcript.jsonl') |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.kind -eq 'marker' } |
    ForEach-Object { "  [{0,7} ms] {1}" -f $_.startMs, $_.text }
}
# newest session id:
function Latest-Session { (Get-ChildItem "$env:USERPROFILE\LocalScribe\sessions" -Directory |
    Sort-Object LastWriteTime -Desc | Select-Object -First 1).Name }
```

**A note on your real data.** These items write new sessions into your real storage root. Nothing
here edits or deletes existing sessions. Item 8 (disk space) is the only one that puts the machine
under stress - read its warning before starting it.

---

## 1. Orphaned recording — the headline fix (T1-2)

This is the bug the round exists for: Stop returned the moment audio closed and handed the
`session.json` write to a background task, so exiting seconds later abandoned it.

1. Start a recording. Talk for ~60 seconds.
2. Press **Stop**.
3. **Immediately** (within 2-3 seconds) choose **Exit** from the tray menu.
4. Relaunch the app and run `Show-Session (Latest-Session)`.

**PASS:** `endedAtUtc` is a real timestamp, `durationMs` is roughly 60000, `retained` lists
`Local, Remote`, `recovered` is `False`, and there is **no** `recovered session` marker.

**FAIL:** `recovered: True` or a `recovered session` marker - the exit did not drain the finalize.

---

## 2. Recovery re-derive — a hard kill (T1-2)

Before this round, a crash-recovered session came back with `retainedAudioSources: []`, which made
playback, re-transcription and Split Speakers all silently refuse a session whose audio was sitting
right there.

1. Start a recording. Talk for ~3 minutes.
2. **Do not press Stop.** Kill `LocalScribe.App.exe` from Task Manager (End Task).
3. Relaunch. A tray notice should report the recovery.
4. `Show-Session (Latest-Session)`, then open the session in the app.

**PASS:** `recovered: True` with a `recovered session` marker (correct here - it really did crash);
`retained` lists the legs; and in the app **playback works, Re-transcribe is offered, and Split
Speakers is enabled**. Those four are the point - all were refused before.

**Also expect, if the transcript lagged the audio:** a marker reading
`recovered session: retained audio runs to HH:MM:SS but the transcript stops at HH:MM:SS - ...`
and a `durationMs` matching the **audio**, not the shorter transcript.

**FAIL:** empty `retained`, greyed-out Split Speakers, or a duration that matches only the transcript
while a much longer FLAC sits in the folder.

---

## 3. Read-view close guard — all four paths (T1-3)

No automated test exists for this by design (no STA harness in the suite), so it is only ever
verified here.

1. Open any finalized session > **Edit**.
2. Retype one line. Close the window with the **X**.
3. Expect a **Save / Discard / Cancel** prompt. Verify each:
   - **Cancel** - window stays open, your edit still there.
   - **Discard** - window closes, edit gone.
   - **Save** - window closes, edit persisted (reopen to confirm).
4. **The negative, and it matters as much:** open Edit, change **nothing**, close with the X.

**PASS:** step 4 shows **no prompt at all**. A prompt that fires on every close is a prompt users
learn to click through - which is how the real one gets dismissed.

---

## 4. Diagnostics reach disk, and carry no privileged text (T1-2 / Plan A)

1. After item 1's tray-Exit, open `%USERPROFILE%\LocalScribe\diagnostics\diag-<yyyyMM>.jsonl`.
2. The last lines of that session must be present - that is what the flush on the exit path
   guarantees.
3. Now grep the whole folder for something actually said on the call:

```powershell
Select-String -Path "$env:USERPROFILE\LocalScribe\diagnostics\*.jsonl" -Pattern '<a distinctive phrase you said>'
```

**PASS:** the session's closing lines are in the file, **and the grep returns nothing**.

**FAIL (serious):** any hit. The log is a support artefact users may send onward; transcript text
must never reach it.

---

## 5. Capture death and recovery — USB headset (T1-4a)

Needs a USB headset or webcam mic you can physically unplug.

1. Start a recording with the USB device selected as the mic.
2. Talk for ~30 seconds, then **unplug it** mid-recording.
3. Watch for ~15 seconds.

**PASS, within about 8 seconds:** a tray notice; a persistent row on the Record console reading
*"The microphone stopped producing audio - reconnecting it..."*; an `audio device changed` marker in
the transcript; and the local leg reconnecting to the fallback device.

4. **Plug it back in.** The console row must clear on its own, the session must still be recording,
   and new transcript lines must still appear.

**FAIL:** nothing happens at all (watchdog not firing), or the row never clears after the device
returns (the stall/recover pairing is broken).

---

## 6. Capture death that never recovers — the budget (T1-4a)

Same setup, but this is the anti-hammering check.

1. Start recording, unplug the headset, and **leave it out for two full minutes**.
2. Stop, then `Show-Session (Latest-Session)`.

**PASS:** **at most three** `audio device changed` markers, then **exactly one**
`capture did not come back for the microphone stream after 3 reconnection attempts - ...`, and then
silence.

**FAIL:** a fourth `audio device changed` marker, or one appearing every ~8 seconds. That means the
restart budget is not being consumed, and a 40-minute call would bury the evidence under ~300
identical lines.

---

## 7. Sleep and resume (T1-4d)

1. Start a recording. Talk for ~30 seconds.
2. Close the lid (or **Start > Sleep**). Wait a few minutes - long enough that you can check the gap.
3. Wake the machine. Stop the recording. `Show-Session (Latest-Session)`.

**PASS:** a `paused: system sleep` marker (**not** `paused by user`), a
`resumed after system sleep: HH:MM:SS was not recorded` marker whose figure **matches the wall-clock
time the machine was actually out**, and the session recording again after wake.

**FAIL:** `paused by user`, a plain `resumed`, or a gap figure of `00:00:00` for a multi-minute sleep.

---

## 8. Disk space (T1-4c)

> **Read first.** Filling a system drive can destabilise Windows. Strongly prefer a USB stick or a
> spare partition: set **Settings > storage root** to it, run the checks, then set the root back.
> Do not fill `C:`.

**Refusal at Start:**
1. Point the storage root at a drive with **under 2 GB** free.
2. Press **Start**.

**PASS:** it refuses with a message naming both figures ("*N* MB free, 2048 MB needed"), records
nothing, and no session folder is created.

**Mid-session warning:**
3. Point at a healthy drive, start recording, then fill that drive below 1 GB (copy a large file in).
4. Wait up to ~30 seconds (the disk poll is throttled).

**PASS:** a *"Low disk space"* row appears on the Record console, and the transcript carries a
`low disk space while recording - ...` marker. Exactly one, not one per tick.

---

## 9. Log off — no prompt (T1-4d)

The one with no automated test and the nastiest failure mode: a modal prompt during logoff is what
orphans a session, because nobody can answer it.

1. Start a recording. Talk for ~30 seconds.
2. **Log off Windows** (do not stop first).

**PASS:** **no "a recording is in progress" prompt appears.** Log back in, relaunch, and
`Show-Session (Latest-Session)`: `endedAtUtc` real, and **no** `recovered session` marker - it was
finalized cleanly, not crash-recovered.

**FAIL:** a prompt appears (the logoff path took the attended branch), or the session comes back
`recovered: True`.

---

## Reporting back

For each item: pass / fail / skipped-and-why. For any failure, the useful artefacts are:

- the `Show-Session` output for that session,
- the matching lines from `diagnostics\diag-<yyyyMM>.jsonl`,
- **Settings > Copy last error** if anything surfaced an error.

Items 1-4 are the merge gate. 5-9 can be reported as skipped if the hardware or conditions are not
available, but each one skipped is a behaviour shipping unverified - note which.
