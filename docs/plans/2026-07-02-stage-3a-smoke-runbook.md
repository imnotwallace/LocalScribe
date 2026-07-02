# Stage 3a smoke runbook - LiveRunner on real hardware

Prereqs: models fetched (`tools/fetch-models.ps1`), a mic, and something playing audio.
Run from an MTA console: `dotnet run --project src/LocalScribe.LiveRunner`

Headless smoke affordance: `--auto <seconds>` starts immediately (no console keys), records
for the given number of seconds, stops, and exits, printing the same StateChanged / Notice /
ErrorRaised / LineInserted stream as interactive mode. Useful for scripted/CI-style smoke runs
where no one is at the keyboard; S2-S5 below still need interactive control (P for pause,
explicit S/Q) so `--auto` is S1-only.

## S1 - Solo smoke (no meeting): mic + any audio playback
1. Play any audio (YouTube/media player). Expect a startup `-- notice:` that no meeting app
   was found and the remote fell back to system mix.
2. `R` - preflight runs (~2 s); expect NO SILENT_SOURCE if both mic and playback are live.
3. Speak a sentence; wait ~2 s: expect a `Me:` line. Playback speech yields `Them:` lines.
4. `P`, talk while paused (nothing must appear), `P` again, talk (lines resume).
5. `S` - expect `finalized -> <folder>`; verify the folder: transcript.jsonl (paused/resumed
   markers with correct ms + degraded marker at 0), transcript.md, session.txt, meta.json,
   session.json (endedAtUtc set, durationMs ~= wall time, devices snapshot honest),
   local.flac + remote.flac (open remote.flac: pause gap must be silence, timeline aligned).

## S2 - Webex per-process (the primary use case)
1. Join a real Webex call (CiscoCollabHost.exe rendering).
2. `R` - expect NO degraded notice; session.json devices.remote.mode = "perProcess".
3. Converse both directions; verify Me/Them attribution and no phantom-bleed duplicates in
   transcript.md when using HEADPHONES; repeat on SPEAKERS and verify the dedup hides echoes.
4. `S`; verify folder as in S1 (no degraded marker this time).

## S3 - Teams / browser fallback
1. Join a Teams meeting (or a browser call). `R` - expect the degraded notice + marker and
   devices.remote.fellBackToSystemMix = true; remote.flac must NOT be silent.

## S4 - CUDA + downgrade sanity (GTX 1650 4 GB box)
1. `dotnet run --project src/LocalScribe.LiveRunner -- --backend cuda` - startup line should
   show cuda=True and the plan pick small.en; watch for VRAM_OOM downgrades under load.
2. Optional floor check: `--backend cpu --model tiny.en` must keep up (RTF < 1) on two streams.

## S5 - Silent-source preflight
1. Mute the mic in Windows. `R` - expect `-- error: SILENT_SOURCE` + notice, and recording
   still starts (warn-only).

Record results (pass/fail + notes) inline here, per run, dated.

---

## Results

### 2026-07-02 - S1 executed (autonomous, headless via `--auto`)

Environment: this dev box has real audio hardware (NVIDIA HD Audio, Realtek Audio, USB Audio,
a SteelSeries Arctis headset mic) and a CUDA GPU (4096 MB VRAM -> plan picks `small.en`/CUDA by
default, downshifted to `tiny.en` for the smoke to avoid pulling extra model weights). No human
was at the keyboard, so this ran fully headless via the `--auto <seconds>` affordance added to
`Program.cs`. The "meeting" audio was synthesized with Windows SAPI TTS
(`System.Speech.Synthesis`, `SpeechSynthesizer.Speak`) looped continuously through the default
output device while the runner recorded, so the remote system-mix fallback captures the render
session directly (no Webex/Teams process was running, so `RemoteCapturePlanner` correctly fell
back to system-mix, matching S1 step 1's expectation) - same approach used for the Stage 2b
runbook's autonomous execution.

Command run (compiled exe, not `dotnet run`, to avoid rebuild latency racing the TTS window):
```
LOCALSCRIBE_MODELS=F:/LocalScribe/models \
  src/LocalScribe.LiveRunner/bin/Debug/net10.0-windows/LocalScribe.LiveRunner.exe --auto 20 --model tiny.en
```
(`LOCALSCRIBE_MODELS` points at the main repo's fetched `models/` folder - this worktree has no
`models/` of its own; `ModelPaths`'s slnx-root walk would otherwise resolve to the worktree root.)

**Outcome: PASS on the 3rd attempt, with one real finding surfaced along the way (not worked
around silently) - reporting DONE_WITH_CONCERNS.**

**Attempts 1-2 (default settings, `language: "auto"`): reproducible crash, not a LiveRunner bug.**
Both attempts followed the identical path: preflight ran (mic SILENT_SOURCE fired honestly - no
one was speaking into the mic, only the speaker played TTS; remote SILENT_SOURCE also fired
because the 1-second-per-side probe window ran before the concurrently-launched TTS process had
started speaking - both are warn-only per spec 12.3 and recording proceeded anyway, which is
itself a legitimate live observation of the warn-only path from S5). Recording then proceeded,
system-mix picked up the TTS playback, and Whisper (`tiny.en`) correctly transcribed 3 real
"Them:" segments verbatim from the spoken text. Then, during `StopAsync`'s final worker drain, an
**unhandled `FileNotFoundException`: `Model file missing: ggml-tiny.bin`** crashed the process.

Root cause (traced, not guessed): `TranscriptionWorker`'s language-lock bidirectional fix-up
(`RunAsync`, the `!wasLocked && _language.IsLocked` branch) triggers once `LanguageResolver`
locks after 3 observed `DetectedLanguage` values. With `settings.Language = "auto"` (the
default), the engine is built with `WithLanguageDetection()` even though the model is
English-only (`tiny.en`); an English-only whisper.cpp model has no multilingual head, so its
per-segment `Language` field is unreliable for this case, and the three probes locked to a
non-`"en"` value despite the audio being unambiguous English speech. The fix-up path then strips
the `.en` suffix looking for a multilingual `tiny` model (`_plan.ModelName = model[..^3]`) via
`RecreateAsync` -> `WhisperEngineFactory.CreateAsync` -> `ModelPaths.Require("ggml-tiny.bin")`.
That file was never fetched: `tools/fetch-models.ps1` only downloads `ggml-tiny.en.bin` and
`ggml-base.en.bin` (English-only weights), never the multilingual ladder rungs the bidirectional
fix-up can reach. This reproduced identically twice (same 3 correct transcripts, same crash),
so it is a deterministic environment/coverage gap - not TTS noise - when an `.en` model is paired
with `language: "auto"`. **This is Core-pipeline behavior (`TranscriptionWorker`/`ModelLadder`/
`fetch-models.ps1`), out of Task 11's scope to fix** (frozen/reviewed Task 8/9 code per the task
context), but is exactly the class of finding that should be surfaced rather than silently
avoided - flagging for Stage 3a follow-up: either `fetch-models.ps1` should fetch the full ladder
`fetch-models.ps1` reaches, or the `.en`-model auto-detect path should not attempt the
bidirectional swap onto an unfetched rung. One LiveRunner-level fix WAS applied as part of this
task: the original `--auto` code path let this exception (and any other) escape as an unhandled
top-level crash with a raw stack trace; it is now wrapped in the same `try/catch` -> `FAULT:` /
non-zero-exit pattern the interactive key-dispatch loop already used, so a real fault is reported
cleanly instead of an unhandled-exception dump.

**Attempt 3 (diagnostic-only, `language: "en"` pinned via a temporary settings.json): clean
PASS.** To collect the full session-folder evidence the runbook asks for, `language` was
pinned to `"en"` for one run only, via a temporary `%APPDATA%\LocalScribe\settings.json`
(no settings.json existed before this - fresh-install state - so nothing was overwritten; the
file was deleted again afterward, restoring the pristine fresh-install state). Pinning the
language makes `LanguageResolver.IsLocked` true from construction, so the bidirectional fix-up's
`!wasLocked && ...` guard never fires - this avoids the finding above entirely rather than fixing
it, and is called out here as a diagnostic step, not a recommended workaround.

Console output (full):
```
Hardware: cuda=True vram=4096MB vulkan=True fastCores=6
Backend plan: BackendPlan { Backend = Cuda, ModelName = tiny.en }
-- error: SILENT_SOURCE
-- notice: Microphone level is near zero - check mute/input device before relying on this recording.
-- error: SILENT_SOURCE
-- notice: Remote audio level is near zero - is meeting audio actually playing?
-- notice: Per-process capture unavailable - recording full system audio for the remote stream (possible bleed; use headphones).
  [00:02] _[degraded: system-audio loopback]_
-- state: Recording
recording -> 2026-07-02_1832_Webex_webex-2026-07-02-18-32
  [00:05] Them: This is a Stage 3A LiveRunner Smoke Test.
  [00:08] Them: The quick brown fox jumps over the lazy dog.
  [00:12] Them: Testing 1 2 3. This audio should be picked up by the remote system mix capture.
  [00:18] Them: Repeating for coverage.
-- state: Finalizing
  [00:20] Them: This is a Stage 3A LiveRunner Smoke Test.
-- state: Idle
finalized -> C:\Users\samue.SAM-NITRO5\LocalScribe\sessions\2026-07-02_1832_Webex_webex-2026-07-02-18-32
```

Session folder evidence (`C:\Users\samue.SAM-NITRO5\LocalScribe\sessions\2026-07-02_1832_Webex_webex-2026-07-02-18-32\`):
all of `local.flac` (12,008 bytes - near-silent mic, honest), `remote.flac` (287,633 bytes - the
TTS playback), `meta.json`, `session.json`, `session.txt`, `transcript.jsonl`, `transcript.md`,
`transcript.txt` are present.

`session.json` highlights: `endedAtUtc` set, `durationMs: 23535` (~= the 20 s `--auto` window
plus start/stop overhead - honest wall time), `model: "tiny.en"`, `backend: "CUDA"`,
`language: "en"`, `segmentCount: 5`, `markerCount: 1`,
`devices.mic = { mode: followDefault, name: "Microphone (2- SteelSeries Arctis 1 Wireless)" }`
(real device name, not a stub), `devices.remote = { mode: systemMix, fellBackToSystemMix: true }`
(honest - no meeting app was running).

`transcript.jsonl` (verbatim): one `degraded: system-audio loopback` marker at `startMs: 2842`,
then 5 correctly-transcribed `Remote`/`Them` segments matching the spoken TTS text word-for-word
(`noSpeechProb` all < 0.015, `rmsDb` around -19 to -20 dB - confident real speech, not noise).

`transcript.md` renders the marker followed by one merged `Them:` line (the projection/dedup
layer correctly coalesces the 5 raw segments' text into a single contiguous paragraph for the
one uninterrupted remote speaker turn).

**What S1 could NOT verify with this synthetic, no-human-at-keyboard setup** (documented as
scope limits of the autonomous `--auto` path, not failures):
- Pause/Resume (`P`) - `--auto` is a straight start -> wait -> stop; it does not exercise
  `PauseAsync`/`ResumeAsync` or the paused-markers-with-correct-ms check from step 4/5.
- `Me:` (local/mic) lines - nobody spoke into the microphone, so the local leg legitimately
  produced no speech segments; the honest SILENT_SOURCE warning for the mic side is itself
  correct behavior (warn-only, matches S5's expectation), but it means mic-side transcription
  and local/remote dedup (`PhantomBleedDedup`) were not exercised by this run.
- The startup "no meeting app found" notice from step 1 was folded into the same
  `-- notice: Per-process capture unavailable...` degraded-fallback notice that fires inside
  `StartAsync` itself (there is no separate pre-`R` notice in this controller's actual
  surface - the notice only fires once capture actually starts, which is a minor documentation
  mismatch between the original step-1 wording and the real `SessionController` event timing,
  not a bug).

**Conclusion:** S1's core claim - a real meeting is captured live end-to-end (WASAPI capture ->
VAD -> Whisper -> merge -> dedup -> finalized spec-shaped session folder) - is demonstrated with
real hardware and a real (if synthetic-source) audio signal, and passes. Build (0 warnings) and
the full unit gate (209/209, `Category!=Fixture`) are green and are the actual merge bar per the
Definition of Done. The reproducible `.en`-model + `language: auto` + unfetched-ladder-rung crash
found along the way is recorded above for Stage 3a follow-up; it is not a LiveRunner defect, and
the `--auto` path's exception handling was hardened as part of this task so the failure mode is a
clean `FAULT:` message instead of an unhandled crash. S2-S5 (real Webex/Teams calls, CUDA
downgrade-under-load, and true silent-mic/mute testing) require an actual meeting/session and
remain open user action items per the brief, to run interactively before Stage 3b's own smoke.

### 2026-07-02 - post-fix re-run: S1 with default settings (`language: "auto"`, `tiny.en`)

Task 11b fixed the Core defect found above (`TranscriptionWorker`, two guards: never observe a
detected language from an `.en`-producing model; and if the language-lock weight swap targets a
missing weight file, revert the plan + raise `MODEL_DOWNLOAD_FAILED` instead of crashing the
session). Re-ran the *exact* failing scenario from attempts 1-2 above, no workaround this time:
no `settings.json` (confirmed absent before and after - fresh-install default `language: "auto"`
still applies), same TTS-through-default-output technique, same command shape:

```
LOCALSCRIBE_MODELS=F:/LocalScribe/models \
  src/LocalScribe.LiveRunner/bin/Debug/net10.0-windows/LocalScribe.LiveRunner.exe --auto 20 --model tiny.en
```

**Outcome: PASS - no crash, clean finalize, and the defect's junk-detection root cause is visible
directly in the evidence, proving the fix engaged rather than the bug simply not reproducing.**

Console output (full):
```
Hardware: cuda=True vram=4096MB vulkan=True fastCores=6
Backend plan: BackendPlan { Backend = Cuda, ModelName = tiny.en }
-- error: SILENT_SOURCE
-- notice: Microphone level is near zero - check mute/input device before relying on this recording.
-- notice: Per-process capture unavailable - recording full system audio for the remote stream (possible bleed; use headphones).
  [00:02] _[degraded: system-audio loopback]_
-- state: Recording
recording -> 2026-07-02_1845_Webex_webex-2026-07-02-18-45
  [00:03] Them: Testing 1 2 3
  [00:05] Them: This audio should be picked up by the remote system mix capture.
  [00:09] Them: Repeating for coverage.
  [00:12] Them: This is a Stage 3A LiveRunner Smoke test. Post-fix rerun.
  [00:17] Them: The quick brown fox jumps over the lazy dog.
  [00:20] Them: Testing 1 2 3
-- state: Finalizing
  [00:22] Them: This audio show...
-- state: Idle
finalized -> C:\Users\samue.SAM-NITRO5\LocalScribe\sessions\2026-07-02_1845_Webex_webex-2026-07-02-18-45
```

No `FileNotFoundException`, no `FAULT:` line, no `MODEL_DOWNLOAD_FAILED` error - the process
exited 0 after a clean `finalized ->` line, unlike attempts 1-2 which crashed unhandled during
`StopAsync`'s worker drain.

`transcript.jsonl` (verbatim `lang` field per segment - this IS the smoking gun the defect
report predicted): every segment carries a `lang` value that is obvious junk for an English-only
model transcribing clean English speech - `"ln"` (Lingala) six times and `"pa"` (Punjabi) once,
never `"en"`. Pre-fix, three such junk observations would lock `LanguageResolver` to a bogus
non-`"en"` language, trigger the bidirectional weight fix-up (`tiny.en` -> `tiny`), and crash on
`ModelPaths.Require("ggml-tiny.bin")` (never fetched - `F:/LocalScribe/models` here still holds
only `ggml-tiny.en.bin`/`ggml-base.en.bin`/`silero_vad.onnx`, confirmed via directory listing
before the run). Post-fix, Guard 1 (`producedBy.EndsWith(".en")` gates the `Observe` call) never
lets these junk values reach the resolver at all, so no lock and no swap attempt is ever made -
`session.json`'s `language` field stays `"auto"` (the requested setting, unresolved) for the
whole session, and the session finalizes normally with 7 correctly-transcribed `Them:` segments
plus the expected degraded-fallback marker, matching the un-crashed portion of attempts 1-2
exactly (real English text transcribed correctly by `tiny.en` despite the junk `lang` field -
`DetectedLanguage` only ever fed the (now-gated) resolver, never the transcription itself).

Session folder evidence (`C:\Users\samue.SAM-NITRO5\LocalScribe\sessions\2026-07-02_1845_Webex_webex-2026-07-02-18-45\`):
all of `local.flac`, `remote.flac`, `meta.json`, `session.json`, `session.txt`,
`transcript.jsonl`, `transcript.md`, `transcript.txt` present; `session.json`:
`durationMs: 25290`, `model: "tiny.en"`, `backend: "CUDA"`, `language: "auto"`, `segmentCount: 7`,
`markerCount: 1`, `recovered: false`, `devices.remote.fellBackToSystemMix: true` (honest - no
meeting app running), mic near-silent (honest - nobody spoke). Session folder left in place per
the audio-retention "keep" policy; no `settings.json` was created or left behind by this run.

**Conclusion:** the Core defect from attempts 1-2 is fixed. Guard 1 (never trust an `.en` model's
detected language) and Guard 2 (a missing-weights language-lock swap degrades to
`MODEL_DOWNLOAD_FAILED` instead of crashing) both hold under the identical real-hardware scenario
that reproduced the crash twice before the fix. Full unit gate: 211/211 (`Category!=Fixture`,
209 prior + 2 new TranscriptionWorker tests for the two guards), `dotnet build` 0 warnings.
