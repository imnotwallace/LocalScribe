# Stage 1 Capture Spike — Implementation Notes & Hardware-Gate Runbook

Build-time facts and interop discoveries from implementing Tasks 0-10, plus the runbook for
the two hardware SMOKE gates that require a live Webex call. Feeds Stage 2 calibration
(per the Stage-1 Definition of Done).

- **Date:** 2026-07-01
- **Branch:** `feat/stage-1-capture-spike`
- **Status:** All code complete + build-verified (0 warnings/errors); unit suite 12/12 green.
  Hardware gates (live Webex) outstanding — see runbook below.

## Environment (as built)

| Component | Version |
|---|---|
| .NET SDK | 10.0.301 (`net10.0-windows`) |
| NAudio | 2.2.1 |
| Microsoft.Windows.CsWin32 | 0.3.298 |
| OS | Windows 11 (build >> 20348 gate) |

## Scaffold quirks (Task 0)

- `dotnet new <template> -f net10.0-windows` is **rejected** — the template `-f` enum only offers bare
  TFMs. Created projects with `-f net10.0`, then retargeted each csproj `<TargetFramework>` to
  `net10.0-windows`.
- The .NET 10 SDK emits the modern XML solution **`LocalScribe.slnx`** (not `.sln`); all `dotnet`
  commands work with it unchanged.

## NAudio WdlResampler quirk (Task 4)

NAudio 2.2.1's `WdlResampler` differs from the plan skeleton:
- `SetMode` takes **5** args: used `SetMode(true, 2, false, 0, 0)` (sinc=false, so the last two are unused).
- `SetFilterParms` used explicit `(0.693f, 0.707f)` — the standard WDL defaults.
Documented in `MonoResampler16k.cs`. The 48k->16k length test passed within the original
`InRange(15840, 16160)`; no widening needed.

## CsWin32 generation (Task 8) — VERIFIED on the box

Hybrid surface: CsWin32 generates `IAudioClient`, `IAudioCaptureClient`, the structs/enums/consts,
`WAVEFORMATEX`, `PROPVARIANT`; the **3 async-activation symbols are hand-declared** in
`ProcessLoopbackCapture.cs` (CsWin32 emits `IActivateAudioInterfaceCompletionHandler` as a
non-implementable consuming interface). Generated names confirmed by a build-time probe + reading
the emitted sources:

| Symbol | Generated as | Namespace |
|---|---|---|
| `IAudioClient`, `IAudioCaptureClient` | `internal`, `unsafe`, **not `[PreserveSig]`** (throw on HRESULT) | `Windows.Win32.Media.Audio` |
| `AUDIOCLIENT_ACTIVATION_PARAMS` | struct; nested union via `.Anonymous.ProcessLoopbackParams` | `Windows.Win32.Media.Audio` |
| `AUDIOCLIENT_ACTIVATION_TYPE`, `PROCESS_LOOPBACK_MODE`, `AUDCLNT_SHAREMODE` | real C enums | `Windows.Win32.Media.Audio` |
| `WAVEFORMATEX` | `internal` struct, `Pack=1`, fields `ushort/uint` | `Windows.Win32.Media.Audio` |
| `AUDCLNT_STREAMFLAGS_*` | `internal const uint` (LOOPBACK=0x20000, EVENTCALLBACK=0x40000, AUTOCONVERTPCM=0x80000000) | `Windows.Win32.PInvoke` |
| `PROPVARIANT` (+ `_unmanaged`) | generated but **unused** — we hand-roll a BLOB-only header | `Windows.Win32.System.Com.StructuredStorage` |

Key generated signatures used:
- `void IAudioClient.Initialize(AUDCLNT_SHAREMODE, uint, long, long, WAVEFORMATEX*, Guid*)` — throws on failure.
- `void IAudioClient.GetService(Guid*, out object)`, `void SetEventHandle(HANDLE)`.
- `void IAudioCaptureClient.GetBuffer(byte**, out uint, out uint, ulong*, ulong*)`, `GetNextPacketSize(out uint)` — both void (throw).

## Per-process loopback design (Task 9)

- **Device string** `VAD\\Process_Loopback` (verified by 3 primary sources; no GUID fallback).
- **OS gate 20348** (the `20438` on the function page is a doc typo).
- **Activation:** hand-declared `ActivateAudioInterfaceAsync` (Mmdevapi.dll) + hand-declared
  `IActivateAudioInterfaceCompletionHandler`/`...AsyncOperation`; params written as explicit 12 bytes
  on the unmanaged heap, wrapped in a hand-rolled VT_BLOB PROPVARIANT (`BlobSize == sizeof(params)`);
  MTA completion handler signalled through a `TaskCompletionSource` (`RunContinuationsAsynchronously`).
- **Dual format path (the one genuine unknown — resolved on the box):**
  - **Option A (primary):** Initialize 16 kHz/mono/16-bit with `AUTOCONVERTPCM`. Device position is in
    16 kHz frames; gap-fill is direct.
  - **Option B (fallback):** if Option A's `Initialize` throws, re-activate a fresh client and try native
    engine formats (48000/44100, float32, stereo/mono); downmix + resample to 16 kHz via
    `PcmConverter` + `MonoResampler16k`; gap-fill in native frames fed through the resampler.
  - `Initialize` is once-per-client AND throws, so each format attempt uses a freshly-activated client.
  - `ProcessLoopbackCapture.ActivationInfo` reports which mode/rate won (printed by SpikeRunner).
- **Pump:** event-driven; per packet read `pu64DevicePosition`, insert `SilenceGapFiller` silence for
  gaps, honour `AUDCLNT_BUFFERFLAGS_SILENT` defensively; reactivate on
  `AUDCLNT_E_RESOURCES_INVALIDATED`/`DEVICE_INVALIDATED`.

---

## HARDWARE-GATE RUNBOOK (requires a live Webex call)

> Prereqs: Webex desktop app, a second device to join the same call, **headphones** (so remote
> voices don't bleed from speakers into the mic).

### Gate 1 — isolated activation (Task 9 Step 3)
1. Join a Webex test meeting; make sure call audio is actually playing.
2. Find the **CiscoCollabHost.exe** render-session PID (the media process, not the UI). Easiest: run the
   full SpikeRunner once (Gate 2 step 2) — it prints `Target render session: pid <N> (CiscoCollabHost.exe)`.
3. `dotnet run --project src/LocalScribe.SpikeRunner -- --activate-only <pid>`
   - PASS: prints `loopback activated for pid <pid>`, an `ActivationInfo: mode=...` line, and a non-zero
     `captured ~Xs` figure. Note whether **mode=DirectMono16k (Option A)** or **NativeResample (Option B)**.
   - If `ACTIVATION FAILED`: confirm the PID is actually rendering audio, OS build >= 20348, app not elevated.

### Gate 2 — dual capture (Task 10 Step 3, the de-risk gate)
1. Join the Webex call from the second device; **wear headphones**.
2. `dotnet run --project src/LocalScribe.SpikeRunner`  (no args -> scans CiscoCollabHost/Webex/Zoom/...)
3. Speak a few sentences yourself; have the other side speak distinctly.
4. (Optional, feeds Stage-2 calibration) Make one shared transient (a single clap) audible to BOTH mic
   and Webex render, near the start and again near the end — for offset/drift measurement.
5. Press ENTER. Open `%USERPROFILE%\LocalScribe\spike\`.

**Hard gate (go/no-go):**
- [ ] `local.wav` + `remote.wav` exist, **16 kHz mono**, durations ~ recording length (after silence-fill).
- [ ] `local.wav` = your voice only, clear, no chop.
- [ ] `remote.wav` = remote participant(s) only, clear, no chop.
- [ ] Per-process activation confirmed against real Webex **CiscoCollabHost.exe** (not system-loopback, not a by-name PID).
- [ ] Zero sustained dropouts; console prints non-zero durations for both.

**Plan B (explicit, recorded go/no-go):** if per-process can't meet the gate on Webex, run
`dotnet run --project src/LocalScribe.SpikeRunner -- --system-loopback`, confirm it captures (accepting
other-app bleed), and record it as a deliberate decision below — never a silent fallback.

### Troubleshooting
- **"No active meeting render session found" while the app is clearly in a call:** run
  `dotnet run --project src/LocalScribe.SpikeRunner -- --list` to dump every active render session
  (pid / image / device) across ALL output endpoints. The scan covers all active render endpoints
  (not just the Multimedia default) because comms apps often render to the Communications device
  (e.g. a headset). If your app appears under an unexpected image name, pass it explicitly:
  `dotnet run --project src/LocalScribe.SpikeRunner -- <ProcessName>`.
- **Teams (`ms-teams.exe`) captures silence:** confirmed known bug - Teams registers two render
  sessions on one PID and per-process loopback returns all-zeros (you can see the doubled session in
  `--list`). This is expected; Teams needs `--system-loopback` (Plan B). Webex/`CiscoCollabHost.exe`
  is the real target and is not affected.
- **`local.wav` has no mic / wrong mic:** `local.wav` records from the **Communications default**
  capture device, which is a SEPARATE Windows setting from the plain "Default Device". If your real
  mic is the Multimedia default (not the comms default), your voice will be missing. `--list` prints
  both defaults + all active mics; SpikeRunner prints the chosen mic at startup. Fixes: re-run with
  `--mic-default` (records from the Multimedia default), or set your mic as the Windows Default
  Communication Device.

### Box-verify checklist (resolve during the gates; record results)
- [ ] **AUTOCONVERTPCM cross-rate** — does Gate 1 report mode=DirectMono16k (Option A works) or
      NativeResample (fell back to Option B)? This resolves the whole capture-format question.
- [ ] Device position advances across silence (pause audio 3-5s mid-call; confirm `remote.wav` stays
      time-aligned, not shortened).
- [ ] Actual OS build where activation succeeds (>= 20348 expected).
- [ ] `Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>()` == 12 (printed in `ActivationInfo` as `paramsSize`).

### Measured & recorded (NOT pass/fail — Stage-2 calibration)
- Cross-bleed (dBFS, from the clap / known signal — remote energy leaking into `local.wav`): __________
- Inter-stream drift (ms/min over a 30+ min call, from start/end clap offsets): __________
- Activation mode used (A=DirectMono16k / B=NativeResample): __________
- Plan B invoked? (yes/no + why): __________

### Golden corpus
Copy 2-3 good `local.wav`+`remote.wav` pairs to e.g. `%USERPROFILE%\LocalScribe\spike\golden\webex-1\`
as the Stage-2 golden corpus (do NOT commit large WAVs — note their location here).
