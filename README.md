# LocalScribe

**Local-first meeting transcription for Windows 11. Open-source. No cloud, no subscription.**

LocalScribe runs quietly in the system tray, captures both sides of your online meetings —
your microphone *and* the other participants — and turns them into a single, timestamped,
speaker-labelled transcript stored entirely on your machine. Transcription is powered by a
locally-run [Whisper](https://github.com/ggerganov/whisper.cpp) model; nothing is uploaded
anywhere.

Think of it as an open-source take on Granola's transcription layer, minus the cloud and
minus the subscription. (No AI summaries in v1 — but transcripts are clean Markdown you can
feed to any LLM yourself.)

> **Status: usable app — build sequence stages 1–5 complete and merged.** The full offline
> and live pipelines work, there is a real tray-first WPF app: a recording overlay, a live
> transcript window, and a main window to browse sessions, organise them into Matters, edit
> metadata, and read past transcripts with audio playback — and on-demand speaker-splitting
> ("Split speakers") is now wired end to end. Still to come: correction editing + custom
> vocabulary + export (Stage 6), and hardening + a signed installer (Stage 7). The app is not
> yet packaged — you run it from source today, and production packaging of the diarisation
> helper is part of Stage 7 (see the note under Getting started).

## Why LocalScribe

- **Local & private** — audio and transcripts never leave your machine.
- **No subscription** — runs on your own hardware with open-source Whisper.
- **Us vs them, for free** — your mic and the meeting's audio are captured as *separate*
  streams, so "me" and "the remote side" are distinguished structurally, with no ML required.
  Optional on-demand speaker-splitting goes further, telling multiple people apart *within*
  one side.
- **Near-real-time** — text appears within a few seconds of each utterance (VAD-segmented).
- **Files are the truth** — every session is a self-contained folder of plain JSON, Markdown,
  and audio you own. No database, no lock-in.

## What you can do today

- **Record on demand** — start, pause, and stop from the tray or the recording overlay. A
  colour-coded tray indicator (red while recording) makes the recording state impossible to
  miss. Recording is always manual — nothing starts capturing behind your back.
- **Watch it transcribe live** — a virtualised live-transcript window streams utterances as
  they are recognised, labelled "me" / "them" by which audio stream they arrived on.
- **Browse your sessions** — a main window lists every recording with its app, date, duration,
  and status badges (recovered, edited, system-mix).
- **Organise into Matters** — group related sessions under a Matter (a case/topic) with a
  reusable roster of participants; tag any session into one or more Matters; a built-in index
  keeps counts straight and self-heals on launch.
- **Edit metadata** — rename a session, set its medium, tag Matters, and curate participants
  in an auto-saving detail pane. The transcript itself is never rewritten — it is evidence.
- **Re-read with audio** — open any past session in a read view that renders the finalized
  transcript and plays back both audio legs together, with per-side mute and a seek bar.
- **Split speakers** — on a finalized session where you declared more than one participant on
  a side, run on-demand diarisation (sherpa-onnx) to tell them apart within that side, preview
  and name each detected speaker by playing a representative snippet, and confirm to write a
  non-destructive speaker overlay. Manual reassignments are pinned and survive a later
  re-diarise; nothing is ever deleted by a split.
- **Recover from crashes** — an interrupted recording is finalized automatically on the next
  launch, with a tray notice.
- **Delete safely** — deleting a whole session sends its folder to the Windows Recycle Bin
  (recoverable), never a silent permanent wipe.

Not yet built: transcript correction editing and custom vocabulary and `.zip`/`.docx` export
(Stage 6), and hardening + a packaged installer (Stage 7). Full-text search and cloud sync
are explicit non-goals for v1.

## How it works

```
Your mic (Local) ─────┐                          ┌─→ recording overlay (always-on-top pill)
                      ├─ VAD → Whisper → merge ───┼─→ live transcript window
App loopback (Remote) ┘      (by session clock)   └─→ session folder: transcript.jsonl + .md/.txt
                                                       + session.txt + local/remote audio (local disk)
```

Two audio streams — your microphone and the meeting app's **per-process loopback** — are each
sliced into utterances by [Silero](https://github.com/snakers4/silero-vad) voice-activity
detection, transcribed locally by Whisper, and merged by timestamp into one interleaved
transcript. Because speaker attribution comes from *which stream* the audio arrived on, basic
"me / them" labelling is structural and free. Per-process loopback isolates just the meeting
app's audio; for apps where that isn't reliable (Teams, and browser-based calls), LocalScribe
falls back to system-wide loopback and flags the session so you know its audio may include
other apps.

## Platform & requirements

- **Windows 11** — built on WASAPI per-process loopback; Windows-only by nature.
- **[.NET 10 SDK](https://dotnet.microsoft.com/download)** (`net10.0-windows`).
- A GPU helps but isn't required: Whisper runs on **CUDA** (NVIDIA), **Vulkan** (other/
  integrated GPUs), or **CPU**, chosen automatically in that order. A modern NVIDIA card
  (≥ 4–6 GB VRAM) is comfortable; smaller models run fine on CPU.
- Speech models are fetched separately (see below) and live in a git-ignored `models/` folder.

## Getting started

LocalScribe isn't packaged yet, so you run it from source.

```powershell
# 1. Fetch the speech models (Silero VAD + Whisper tiny.en/base.en) into ./models
pwsh tools/fetch-models.ps1

# 2. Build everything
dotnet build LocalScribe.slnx

# 3. Run the app (tray-first: look for the tray icon, right-click for controls)
dotnet run --project src/LocalScribe.App
```

On first launch the app shows a one-time consent notice (see [Privacy](#privacy)); accept it to
continue. Your recordings land under `%USERPROFILE%\LocalScribe` by default — the Settings page
lets you change the storage root, pick your microphone and models, and toggle launch-at-login.

`tools/fetch-models.ps1` now also fetches the two diarisation models ("Split speakers" needs):
pyannote-segmentation-3.0 and 3D-Speaker CAM++, both SHA-pinned, Apache-2.0/MIT-licensed. **The
in-app "Split speakers" dialog additionally needs `LocalScribe.Diarizer.exe` present beside the
running app's own output folder** — it is a separate out-of-process helper (kept isolated from
the app's own ONNX Runtime; see [Specifications §1.3](docs/specs/localscribe-specs.md)) that
running from source does **not** build or copy there automatically. Production packaging of the
helper is Stage 7's job; until then, publish it manually per the exact command in the
[Stage 5 smoke runbook](docs/plans/2026-07-04-stage-5-smoke-runbook.md#prerequisite). Without
it, Split-speakers still opens but reports a helper-crash error rather than corrupting anything.

**Console harnesses** (for development / headless verification):

- `dotnet run --project src/LocalScribe.LiveRunner` — drive the real live pipeline from the
  console (keys: `R` start, `P` pause/resume, `S` stop).
- `dotnet run --project src/LocalScribe.OfflineRunner -- --local <mic.wav> --remote <remote.wav>`
  — run the offline pipeline over pre-recorded WAVs.
- `dotnet run --project src/LocalScribe.SpikeRunner` — the Stage-1 dual-stream capture smoke test.

**Tests:**

```powershell
dotnet test LocalScribe.slnx --filter "Category!=Fixture"
```

Over 400 headless unit tests cover the Core domain and app logic. Fixture-gated tests
(`Category=Fixture`) exercise the real Whisper/VAD models plus a private golden-audio corpus and
a private multi-speaker diarisation corpus (DER regression), and are opt-in — they need model
files and provisioned audio that aren't in the repo.

## Where your data lives

Everything is plain files under your storage root (default `%USERPROFILE%\LocalScribe`):

```
LocalScribe/
├─ sessions/
│  └─ 2026-07-04_1830_Webex_client-call/
│     ├─ session.json      system-owned facts (times, app, model, counts)
│     ├─ meta.json         your metadata (title, participants, Matter tags)
│     ├─ transcript.jsonl  append-only source of truth (never rewritten)
│     ├─ edits.json        non-destructive corrections overlay (Stage 6)
│     ├─ speakers.json     diarisation speaker assignments + names (absent until you Split)
│     ├─ transcript.md / .txt / session.txt   readable projections
│     └─ local.flac / remote.flac             the two audio legs
└─ matters/
   ├─ matters.json         the Matter index
   └─ <matterId>/matter.json
```

The transcript is append-only and is treated as evidence: corrections and speaker labels are
layered on top non-destructively, and the only deletion is sending a whole session folder to the
Recycle Bin.

## Roadmap

1. ~~**Capture spike** — dual-stream WASAPI capture → two clean WAVs~~ *(done)*
2. ~~**Offline pipeline** — schemas & persistence, then Silero VAD → Whisper → merge → JSONL/Markdown~~ *(done)*
3. ~~**Live wiring** — real-time capture, recording overlay, live transcript window~~ *(done)*
4. ~~**Manual controls + session/Matter manager** — record controls, session browser, Matter organiser, metadata editing, read view + audio, settings, first-run consent, crash recovery~~ *(done)*
5. ~~**Split speakers** — on-demand diarisation (sherpa-onnx), count-gated, non-destructive~~ *(done)*
6. **Correction editing + vocabulary + export** — edit-overlay UI, custom vocabulary, `.zip` archive (`.docx` fast-follow)
7. **Hardening + packaging** — watchdog, device-swap / sleep-resume resilience, signed installer (x64 + ARM64)

## Tech stack

- **App:** WPF on `net10.0-windows`, with [WPF-UI](https://github.com/lepoco/wpfui) (Fluent),
  [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) (tray), and
  [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet).
- **Capture:** NAudio + [CsWin32](https://github.com/microsoft/CsWin32) for WASAPI per-process
  loopback.
- **Speech:** [Whisper.net](https://github.com/sandrohanea/whisper.net) (CUDA / Vulkan / CPU
  runtimes) for transcription; Silero VAD via [ONNX Runtime](https://onnxruntime.ai/) for
  segmentation; FLAC encoding via CUETools FLAKE.
- **Diarisation:** [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (pyannote-segmentation-3.0
  + 3D-Speaker CAM++) in a separate out-of-process helper (`LocalScribe.Diarizer.exe`) with its
  own ONNX Runtime build, so it never shares a runtime with the app's own Whisper/Silero.
- **Solution:** `LocalScribe.slnx` — `LocalScribe.Core` (domain + pipeline), `LocalScribe.App`
  (the WPF app), `LocalScribe.Diarizer` (the diarisation helper), three console runners, and two
  test projects.

## Documentation

- [Design](docs/plans/2026-06-30-localscribe-design.md) — decisions, architecture, components, UI, storage format, v1 scope & 7-stage build sequence
- [Specifications](docs/specs/localscribe-specs.md) — data schemas, state machines, model/VAD, render, settings, errors (living reference)
- **Stage 1 — Capture spike:** [plan](docs/plans/2026-06-30-stage-1-capture-spike.md) · [decisions](docs/plans/2026-06-30-stage-1-capture-spike-decisions.md) · [implementation notes & smoke runbook](docs/plans/2026-07-01-stage-1-implementation-notes.md)
- **Stage 2 — Offline pipeline:** [2a schemas, persistence & projection](docs/plans/2026-07-02-stage-2a-schema-persistence-projection.md) · [2b VAD → Whisper → merge](docs/plans/2026-07-02-stage-2b-offline-pipeline.md) · [golden-corpus fixture](docs/plans/2026-07-02-stage-2b-golden-corpus.md)
- **Stage 3 — Live wiring:** [3a live pipeline](docs/plans/2026-07-02-stage-3a-live-pipeline.md) ([smoke runbook](docs/plans/2026-07-02-stage-3a-smoke-runbook.md)) · [3b tray, live view & overlay](docs/plans/2026-07-02-stage-3b-tray-liveview-overlay.md) ([smoke runbook](docs/plans/2026-07-02-stage-3b-smoke-runbook.md))
- **Stage 4 — Session/Matter manager:** [design](docs/plans/2026-07-03-stage-4-manager-design.md) · [plan](docs/plans/2026-07-03-stage-4-manager-plan.md) · [smoke runbook](docs/plans/2026-07-03-stage-4-smoke-runbook.md)
- **Stage 5 — Split speakers (diarisation):** [design](docs/plans/2026-07-04-stage-5-diarisation-design.md) · [plan](docs/plans/2026-07-04-stage-5-diarisation-plan.md) · [smoke runbook](docs/plans/2026-07-04-stage-5-smoke-runbook.md)

## Privacy

LocalScribe stores everything locally and uploads nothing — by default to a non-synced
folder under your user profile (it warns if you point it at a cloud-synced location). A
visible tray indicator shows when it is recording, and the main window and read views are
excluded from screen capture by default so a shared screen never leaks your transcripts.

**Recording others is your responsibility.** Many jurisdictions require the consent of some
or all parties before a conversation may be recorded (two-party / all-party consent).
LocalScribe makes the recording state obvious but cannot enforce the law or obtain consent
for you — disclosing the recording to the other participants is up to you.

## License

[MIT](LICENSE) © 2026 imnotwallace
