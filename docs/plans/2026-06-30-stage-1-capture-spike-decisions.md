# Stage 1 Capture Spike — Design Decisions (brainstorm output)

- **Date:** 2026-06-30
- **Status:** Validated design — approved, ready for plan revision via writing-plans.
- **Supersedes:** the open questions and naïve gate criteria in
  `docs/plans/2026-06-30-stage-1-capture-spike.md` (that plan's task bodies are otherwise
  carried forward; this document records what changed and why).
- **Grounded by:** a 5-track research pass (NAudio process-loopback state, WASAPI loopback
  silence semantics, the CsWin32 activation pattern, meeting-app render topology, and the
  .NET LTS timeline), 2026-06-30.

---

## 0. Why this document exists

The Stage 1 plan was authored on Linux and is sound, but it left several decisions open or
internally inconsistent. Brainstorming + research resolved them. This doc is the validated
design; `writing-plans` will fold it into a revised implementation plan.

**Primary use case (sets priorities):** a lawyer recording **Webex** calls with incarcerated
clients. Webex is the **#1 target app**, ahead of Teams/Zoom. The legal/consent story
(jurisdictional two-party/all-party consent, lawyer–client privilege, always-visible
recording indicator) is **out of scope for Stage 1** — Stage 1 proves *capture only* — but is
load-bearing for later stages (design decision 5, error-handling §5).

---

## 1. Resolved decisions

| # | Open question | Decision | Rationale |
|---|---|---|---|
| D1 | Target framework | **`net10.0-windows`** across all projects (was net8) | net8 (LTS) support ends ~Nov 2026 — right around v1's first ship; net10 (LTS) runs to Nov 2028. All packages (WPF, NAudio 2.2.x, CsWin32, xUnit) work on net10 with **no version changes**. Needs the .NET 10 SDK on dev + CI. |
| D2 | Priority target app | **Webex-first**, then Zoom, then Teams | The real use case is Webex. Bonus: the 2026 "new Teams" (`ms-teams.exe`) currently returns **silence/all-zeros** for per-process loopback (known bug) — so Webex-first dodges the worst-known target. Zoom is confirmed working. |
| D3 | Browser-Webex (user sometimes joins in Chrome/Edge) | **Desktop-app only for the Stage 1 gate**; browser deferred as a documented limitation | A browser renders *all* tabs through one shared Chromium audio-service process; per-process loopback can't isolate the Webex tab. The eventual browser path is manual-start + system/all-browser loopback — out of scope for the de-risk spike. |
| D4 | Tray shell (Task 10) | **Deferred to Stage 3** | The console SpikeRunner (Task 9) already meets the de-risk gate. Keeps Stage 1 focused on capture. |
| D5 | Stage 1 gate strictness | **Measure-and-record** (see §5) | Bleed/drift *acceptable* values depend on Stage-2 transcription, so they're recorded, not pass/fail. The hard gate is "clean, time-aligned, per-process Webex capture." |
| D6 | Loopback interop path | **CsWin32** (keep), reference NAudio 3 preview | NAudio 3's built-in `WithProcessLoopback` is a 4-day-old preview with no stable date and a breaking generation — not a dependency yet; cross-reference its PR (#1348) as a known-good .NET reference. |

---

## 2. PID-selection strategy (revises Task 9)

Replace the skeleton `ProcessTree.RootOf()` → activate-on-root approach with:

1. Enumerate **active render sessions** on the default playback endpoint; pick the first
   whose owning process image matches a meeting app. For Webex desktop that render session
   is **`CiscoCollabHost.exe`** (the media-workload process — a different PID from the UI).
2. Activate per-process loopback on **that render-session PID directly**, with
   `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`. (Targeting the precise render PID
   minimises cross-app bleed; INCLUDE_TREE still catches any child media subprocess.)
3. `RootOf()` is replaced by `BelongsToMeetingApp(pid)` — a membership check used only to
   confirm a bare `CiscoCollabHost.exe`/`msedgewebview2.exe` session belongs to a known
   meeting app. It walks ancestry to an **app-root allowlist**, validates parent
   creation-time (`GetProcessTimes`) to defeat **PID reuse**, stops at the allowlist, never
   ascends into `explorer`/`svchost`/`services`, and returns self on cycles.

---

## 3. The crux — interop specifics (revises Tasks 7–8)

- **Format:** never call `GetMixFormat`/`IsFormatSupported` on the loopback client (returns
  `E_NOTIMPL`). Hand-build a **16 kHz / 16-bit / mono `WAVEFORMATEX`** and initialize with
  `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY` so the
  format conversion happens in-stream (this is the Task-4 16k-mono target for the Remote path
  — no separate resampler needed on loopback).
- **Activation:** `ActivateAudioInterfaceAsync("VAD\\Process_Loopback", IID_IAudioClient,
  &activationParams, handler, out op)`, where `activationParams` is
  `AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK,
  ProcessLoopbackParams = { TargetProcessId = pid, ProcessLoopbackMode =
  INCLUDE_TARGET_PROCESS_TREE } }` wrapped in a `PROPVARIANT` **BLOB** — the one fragile,
  hand-tuned marshalling spot.
- **Async/threading:** the completion handler fires on an **MTA worker thread**. Model
  initialization as `await` over a `TaskCompletionSource`; keep the handler object rooted;
  never block an STA thread on the result.
- **Stream:** event-driven — `AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK`,
  `SetEventHandle` **before** `Start()`.
- **Robustness:** re-activate on `AUDCLNT_E_RESOURCES_INVALIDATED`. **Minimum OS: Windows 10
  build 20348+.** Consider hand-declaring the two tiny COM interfaces +
  the `Mmdevapi.dll` `ActivateAudioInterfaceAsync` P/Invoke for version stability, keeping
  CsWin32 for `IAudioClient`/structs/constants.
- **Symbol note:** `PROPVARIANT` may live under `Windows.Win32.System.Com.StructuredStorage`
  in recent metadata; audio symbols under `Windows.Win32.Media.Audio`. Record any renames.

---

## 4. NEW — stream timeline reconstruction (the most important addition)

**Problem:** per-process loopback delivers **no buffers at all** while the target is silent
(not silent-flagged frames). Naïvely appending makes `remote.wav` run *shorter* than the
always-on `local.wav`, so they drift out of sample-alignment and the "durations ≈ recording
length" / drift criteria become meaningless.

**Design:**
- Anchor **both** streams to one **QPC session clock** at `Start()`.
- The loopback pump drains via `GetNextPacketSize > 0` and treats 0-packet intervals as
  **real silence gaps**. On each `GetBuffer`, read the device/QPC position
  (`pu64DevicePosition` / `pu64QPCPosition`) and detect gaps via a position jump or
  `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY`.
- For each gap, **insert exactly the missing silence frames** into the Remote stream *before*
  emitting the next real frame. The mic stream is continuous (no gap-fill needed for the
  spike — note the assumption). Result: both `WavSink`s stay dumb appenders and the two WAVs
  are sample-aligned by construction.
- **Humble-Object seam:** extract the gap math into a **pure `SilenceGapFiller`**
  (`lastPosFrames, newPosFrames, sampleRate → silenceFrameCount`) → a **new unit test, zero
  hardware** (new Task "5b"). This also produces the groundwork for the design's §5
  "calibrated mic↔loopback offset constant".
- The `silence.exe` system keep-alive trick does **not** help per-process INCLUDE capture
  (different PID, excluded from the target set) — do not plan around it.

---

## 5. Revised gate / Definition of Done (revises Task 9 + Stage-1 DoD)

**Hard go/no-go (pass/fail):**
- `local.wav` + `remote.wav` exist, correct **16 kHz mono** format, durations ≈ wall-clock
  (after silence-insertion).
- `local.wav` = your voice only; `remote.wav` = remote party only; zero sustained dropouts.
- **Per-process activation confirmed against real Webex `CiscoCollabHost.exe`** — not a
  system-loopback fallback, not a by-name PID.

**Measured & recorded (NOT pass/fail — feed Stage-2 calibration):**
- Cross-bleed as a **dBFS** figure (clap / known-signal based).
- Inter-stream drift as a **ms/min** figure over a 30+ min call (clap-sync near start & end).

**Dropped from Stage 1:** the "bled remote speech does not surface as a phantom Local
transcription line" check — it needs Whisper, which is Stage 2.

**Plan B is a *tested* path, never silent:** SpikeRunner gains a `--system-loopback` mode
(full-system loopback with `EXCLUDE_TARGET_PROCESS_TREE` on LocalScribe's own PID), exercised
at least once so the fallback is known-good. A go/no-go to fall back is an explicit, recorded
decision at the gate.

**Golden corpus:** retain 2–3 Webex `local`/`remote` pairs, labelled as the Stage-2 golden
corpus.

---

## 6. Revised task-list shape

`0` scaffold (**net10.0-windows**) · `1–5` deterministic core (unchanged) ·
**`5b` `SilenceGapFiller` (new, unit-tested)** · `6` `MicCaptureSource` (mic) · `7` CsWin32
setup · `8` `ProcessLoopbackCapture` (**+ QPC anchoring + silence gap-fill**, hand-built
WAVEFORMATEX, MTA completion handler) · `9` SpikeRunner (**Webex-first list, render-session
PID + INCLUDE_TREE, `BelongsToMeetingApp` membership check, `--system-loopback` Plan B mode,
measure-and-record gate**) · ~~`10` tray (deferred to Stage 3)~~.

---

## 7. Prerequisites before Task 0 executes

1. **Fix the shell/`dotnet` tooling** — currently failing with
   `EPERM … mkdir 'C:\Users\…\AppData\Local\Temp\claude\F--LocalScribe'`. Blocks every
   `dotnet`/`git` command.
2. **Install the .NET 10 SDK** (`dotnet --version` ≥ 10.0.1xx) on dev + CI.
3. **Confirm the rig:** Webex desktop app installed, second device available to join the same
   call for repeatable remote audio.

---

## 8. Carried-forward research caveats (for the implementer)

- New Teams (`ms-teams.exe`) returns silence for per-process loopback today (issue #414, OBS
  corroboration) — treat Teams as highest-risk; Plan B (system loopback) is its likely
  shipping path. Not a Stage-1 blocker since Webex/Zoom are the targets.
- Win11 22H2 `GetBuffer` crash on exclusive-fullscreen exit; ARM64 `GetNextPacketSize == 0`
  edge — add a watchdog/log path.
- NAudio WDL resampler API names vary across versions (Task 4 note still applies to the mic
  path).
