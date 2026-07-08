# Live-Recording Robustness — model resolution, silent-capture detection, audio/transcription decoupling (design)

- **Status:** Validated design (2026-07-08). Root-caused live on real hardware via the `LiveRunner`
  console harness after "recording never worked": every real session finalized as `Recovered` with
  `segmentCount: 0` and no usable audio.
- **Companion:** `docs/specs/localscribe-specs.md` (§2.1 lifecycle, §3 backend/model, §8.2 SILENT_SOURCE,
  §12.3 pre-flight probe).
- **Delivery:** one branch (`live-recording-robustness`), subagent-driven TDD, per-task + whole-branch
  review, `--no-ff` merge, user pushes.

## 0. Root cause (evidence)

Three independent defects, found by driving the real capture→VAD→Whisper pipeline with `LiveRunner --auto`:

1. **Model resolution records dead air.** `settings.Model = "auto"` on a CUDA box resolves to `small.en`
   (`BackendSelector.cs:23`), but `ggml-small.en.bin` was not downloaded (`fetch-models.ps1` only fetches
   `tiny.en` + `base.en`). `WhisperEngineFactory.CreateAsync` calls `ModelPaths.Require(...)` which throws
   `FileNotFoundException` — but LAZILY, on the worker's first engine creation, deferred until
   `StopAsync` awaits `WorkerLoop` (`SessionController.cs:450`). Cascade: the C1 fault-guard cancels
   `feedCts` → capture legs abort within ms → **no audio, no segments** → the fault surfaces only when the
   user presses Stop → **Stop throws instead of finalizing** → `EndedAtUtc` stays null → the session comes
   back **Recovered** with 0 segments. Confirmed: with `--model base.en` (present) the fault vanished,
   Stop finalized cleanly, 253 KB of audio was written.

2. **Silent capture is under-detected.** `followDefault` opens the Windows *Default Communications Device*
   (`MicCaptureSource.cs:29`, `Role.Communications`). On this machine that endpoint is not the user's
   working mic (a SteelSeries Arctis — a headset that splits endpoints), so capture recorded a noise floor
   with no speech; VAD correctly produced 0 segments. Pinning the SteelSeries **by ID** (the Stage-6
   device-selection feature) captured the voice and transcribed it. The Start-time peak probe
   (`PreflightProbe` + `SessionController.cs:174-183`) already raises `SILENT_SOURCE`, but only below
   `SilencePeakThreshold` (1e-4, ~−80 dBFS) — a noise-floor endpoint peaks ABOVE that, so the warning does
   not fire for the wrong-device case.

3. **Raw audio is coupled to transcription.** `LiveSourcePipeline.StartLeg` (`LiveSourcePipeline.cs:32-49`)
   reads capture frames under ONE token (`feedCts`) that both writes FLAC (`Tap` → `_audioWriter.Write`)
   and feeds VAD→worker. When the worker faults, the C1 guard cancels `feedCts` → the frame loop stops →
   **audio recording stops with it.** For an evidentiary tool, a transcription failure must never destroy
   the raw recording.

## 1. Fix 1 — model resolution never records dead air

**Locked behavior (user):** auto **downgrades** to the best present model; an explicit missing pick refuses.

- **`ModelPaths.AvailableModels()`** (new, `Transcription/ModelPaths.cs`): returns the set of present model
  basenames (enumerate `ggml-*.bin` in `ModelsRoot`, strip the `ggml-`/`.bin` affixes). Empty on a missing
  dir. WPF-free, pure-ish (filesystem read); the interface consumed by the selector is a plain
  `IReadOnlySet<string>` so the selector stays unit-testable with an injected set.
- **`BackendSelector.Select(HardwareInfo, Settings, IReadOnlySet<string> availableModels)`**
  (`Transcription/BackendSelector.cs:11`): the `Backend` choice is unchanged. The MODEL choice becomes
  availability-aware:
  - Ladder (worst→best): `tiny.en < base.en < small.en`. Ceiling per resolved backend:
    `Cuda|Vulkan|fastCores>=8 → small.en`, else `base.en` (preserves today's tiers).
  - `settings.Model != "auto"` (explicit): return that name verbatim (present or not — Start validates).
  - `auto`: pick the **largest present model at or below the ceiling**; if none present at/below the
    ceiling, the **smallest present** model; the English/multilingual `.en`-trim rule
    (`BackendSelector.cs:28-30`) still applies last.
  - The result carries whether a downgrade happened, so Start can notice it. Represent as a richer return
    `BackendPlan` gains no fields (keep it a pure config record); instead `Select` returns
    `(BackendPlan Plan, string? DowngradedFrom)` — `DowngradedFrom` is the ceiling model when auto used a
    smaller present one, else null. (Existing 2-arg call sites — none outside the two runners — update to
    pass `ModelPaths.AvailableModels()`.)
- **Fail-fast at Start** (`SessionController.StartAsync`, after resolving the plan, BEFORE
  `SessionBootstrap.StartAsync`): resolve the model file path; if it does not exist —
  `ModelPaths.Resolve($"ggml-{plan.ModelName}.bin")` absent (an explicit pick not downloaded, or auto with
  zero present models) — refuse: `Notice("Model 'X' is not downloaded. Pick an available model in Settings, or run tools/fetch-models.ps1.")`,
  return `null`, `State` stays `Idle`, **no session folder, no dead-air recording.** This runs before the
  preflight probe so nothing is created.
- **Downgrade notice:** when `DowngradedFrom` is non-null, `Notice("Recording with {plan.ModelName}; {DowngradedFrom} is not downloaded (better accuracy if you fetch it).")`.
- **Provisioning:** add `ggml-small.en.bin` (HuggingFace `ggerganov/whisper.cpp`) to `tools/fetch-models.ps1`
  so auto's CUDA/fast-CPU default is actually supplied on a fresh setup.

**Tests (Core, unit):** `BackendSelector.Select` — auto picks largest-present-under-ceiling; auto with only
`base.en` present on a CUDA box → `base.en` + `DowngradedFrom == "small.en"`; explicit pick returned verbatim;
zero present → smallest-present or (empty set) the ceiling name unchanged (Start handles the refuse).
`SessionController.StartAsync` — resolved-model-file-absent → returns null, no session dir, `State == Idle`,
Notice emitted (fake provider + a models-root with/without the file); present model → normal Start; downgrade
path emits the notice. `ModelPaths.AvailableModels()` — lists present basenames from a temp models dir.

## 2. Fix 2 — sustained-no-speech indicator during recording

**Locked behavior (user):** warn at Start (exists) AND a persistent indicator if a leg stays silent into the
recording.

- **Keep** the Start peak warning (`SessionController.cs:174-183`) unchanged — it catches dead/all-zeros
  endpoints (−80 dBFS floor is deliberately conservative, `PreflightProbe.cs:11-14`).
- **Add a during-recording monitor** in `SessionController`, driven by the existing per-frame
  `PeakObserved(SourceKind, peak)` events (the `Tap` raises one continuously for every captured frame — so a
  leg that captures noise floor still ticks, which is exactly the case we must catch). Track per `SourceKind`
  the session-clock time of that leg's last transcript segment (`lastSegmentMs[source]`, updated from the
  writer loop / `LineInserted`; stored as a `volatile`/`Interlocked` field since it is written on the writer
  thread and read on the capture thread). **On each `PeakObserved` while `State == Recording`**, if
  `s.Clock.ElapsedMs - lastSegmentMs[source] > SilentLegGraceMs` (default 15000 ms) and not already flagged,
  raise `SilentLegDetected(SourceKind)` ONCE; when a segment from that leg arrives, raise
  `SilentLegCleared(SourceKind)` and reset the flag. This needs **no timer thread** and is deterministic
  under a fake clock. `lastSegmentMs[source]` is seeded to the leg's start time so the grace window counts
  from Start/Resume; Pause (legs stopped) naturally halts the ticks. The truly-dead-endpoint case (zero
  frames, hence zero peaks) is already covered by the Start peak probe (SILENT_SOURCE); this monitor covers
  frames-arrive-but-no-speech.
- **App surface:** the Record console / status strip subscribes to `SilentLegDetected`/`SilentLegCleared`
  and shows a persistent, dismissible-on-clear warning per leg: "No speech detected from the microphone —
  check the selected device (Settings > Recording)." This is where the wrong-endpoint case becomes visible
  even though the peak probe passed.
- **Threshold rationale:** this is segment-presence based, not peak based — real speech reliably produces a
  VAD segment within 15 s; a noise-floor endpoint never does. No change to `SilencePeakThreshold`.

**Tests (Core, unit):** with a fake provider whose leg emits audio but the worker yields no segments, the
controller raises `SilentLegDetected(Local)` after the grace window (fake clock) and never on the healthy
path; a segment arrival raises `SilentLegCleared`. App VM test: the console reflects the indicator state.

## 3. Fix 3 — raw audio survives a transcriber failure

**Locked behavior (user):** transcriber failure keeps audio recording, warns, finalizes on Stop.

- **Two tokens in `LiveSourcePipeline.StartLeg(ICaptureSource, CancellationToken captureCt, CancellationToken feedCt)`:**
  - The frame loop reads the bridge under `captureCt` (cancelled only at Stop/Pause) and ALWAYS writes
    audio + emits peak — the audio-retention guarantee.
  - VAD→worker feeding is gated by `feedCt`: each frame is offered to the segmenter path only while
    `!feedCt.IsCancellationRequested`; once the worker faults (C1 guard cancels `feedCt`), the loop stops
    feeding VAD but keeps writing audio. Structurally: the frame loop pushes frames into a bounded
    segment-input channel drained by the segmenter+enqueue task under `feedCt`; the loop stops pushing (not
    reading) when `feedCt` trips, so no unbounded growth and no audio interruption.
  - `StopLegAndFlushAsync` completes the bridge (ends the capture loop → final audio flush) and awaits the
    feed task; the VAD `Flush()` trailing-segment guarantee is preserved on the clean path.
- **`SessionController`:** create a `captureCts` (Stop/Pause-scoped) separate from `feedCts`
  (worker-fault-scoped). `StartLeg`/`ResumeLeg` pass both. On worker fault the C1 continuation cancels
  `feedCts` only (never `captureCts`). Add:
  - a `TranscriptionFailed` flag on the `Session`; when the worker faults mid-Recording, set it, emit
    `ErrorRaised("TRANSCRIPTION_FAILED")` + `Notice("Live transcription stopped — audio is still recording. You can re-transcribe this session later.")`, and write a `transcription failed` marker via the outbox. **Stay Recording** (audio continues).
  - `StopAsync`: when `TranscriptionFailed`, do NOT rethrow the worker fault as a Stop failure — settle the
    legs (audio flush), finalize `session.json` normally (`EndedAtUtc`, `DurationMs` = clock at Stop, padded
    audio, `RetainedAudioSources`, the segments that DID land), keep `Recovered = false` (this WAS a clean
    stop). The worker fault is recorded (marker + a session flag), not raised.
- **New marker:** `Markers.TranscriptionFailed = "transcription failed"` (`Model/Markers.cs`).

**Tests (Core, unit):** fake provider whose worker faults ~immediately after Start →
- the leg's `AlignedAudioWriter` receives frames AFTER the fault (audio complete, not truncated at fault);
- `StopAsync` returns the id (does not throw), `session.json` has `recovered == false` and a
  `transcription failed` marker;
- healthy path is byte-identical (existing `SessionController`/`LiveSourcePipeline` tests stay green — the
  two-token split defaults to the same behavior when nothing faults).

## 4. Non-goals

- No model download UI / SHA pinning (Stage 7); `fetch-models.ps1` + the fail-fast message are enough.
- No auto-retry of a faulted transcription engine mid-session (keep-audio + offline re-transcribe covers it).
- No change to the −80 dBFS Start peak threshold or the Communications-vs-Console default endpoint choice
  (pinning by ID is the supported fix for a wrong default; Fix 2's indicator surfaces it).
- No mid-recording device hot-swap (Stage 7).

## 5. Spec deltas (`docs/specs/localscribe-specs.md`)

- §3: `auto` model selection resolves to the best model **present on disk** at/below the hardware tier;
  Start refuses (no dead-air recording) when the resolved model is absent.
- §8.2: add the `transcription failed` marker and the sustained-no-speech indicator alongside SILENT_SOURCE.
- §2.1: a transcription-engine failure no longer aborts the session — raw audio recording continues and the
  session finalizes normally on Stop (transcription re-runnable offline).
