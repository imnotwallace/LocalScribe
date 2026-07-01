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

> **Status: Stage 1 (capture spike) built.** Dual-stream WASAPI capture — mic + per-process
> loopback (with Teams/browser fallback to system-wide loopback) — is implemented and
> unit-tested, with a headless spike runner for live verification. Stage 2a (the schemas,
> persistence, and projection layer) is planned in detail and is next; there is no end-user
> app yet.

## Why LocalScribe

- **Local & private** — audio and transcripts never leave your machine.
- **No subscription** — runs on your own hardware with open-source Whisper.
- **Us vs them, for free** — your mic and the meeting's audio are captured as *separate*
  streams, so "me" and "the remote side" are distinguished structurally, with no ML required.
  Optional on-demand speaker-splitting goes further.
- **Near-real-time** — text appears within a few seconds of each utterance (VAD-segmented).

## How it works

```
Your mic (Local) ─────┐                              ┌─→ live transcript view
                      ├─ VAD → Whisper → merge ──────┤
App loopback (Remote) ┘      (by session clock)       └─→ transcript.jsonl + .md (local)
```

Two audio streams — your microphone and the meeting app's **per-process loopback** — are
each sliced into utterances by voice-activity detection, transcribed locally by Whisper, and
merged by timestamp into one interleaved transcript. Because speaker attribution comes from
*which stream* the audio arrived on, basic "me / them" labelling is structural and free.

## Platform & requirements

- **Windows 11** — built on WASAPI per-process loopback; Windows-only by nature.
- A GPU helps: comfortable on any modern NVIDIA card (≥ 4–6 GB VRAM); also runs on an
  integrated GPU (Vulkan) or CPU with smaller models.
- .NET 10 (`net10.0-windows`). Today: NAudio + CsWin32 (capture); coming with the pipeline
  stages: Whisper.net, Silero VAD, sherpa-onnx, WPF.

## Roadmap

1. ~~**Capture spike** — prove dual-stream WASAPI capture → two clean WAVs~~ *(done)*
2. Offline pipeline — **2a:** schemas, persistence & projection *(next)* · **2b:** VAD →
   Whisper → merge → JSONL/Markdown
3. Live wiring — real-time transcript view
4. Manual record controls + session/Matter management (meeting auto-detection deferred)
5. On-demand "Split speakers" (diarisation)
6. Hardening + packaging (MSIX, x64 + ARM64)

## Documentation

- [Design](docs/plans/2026-06-30-localscribe-design.md) — decisions, architecture, components, UI
- [Specifications](docs/specs/localscribe-specs.md) — data schemas, state machines, model/VAD, render, settings, errors
- [Stage 1 plan](docs/plans/2026-06-30-stage-1-capture-spike.md) — the capture-spike build plan
  (+ [decisions](docs/plans/2026-06-30-stage-1-capture-spike-decisions.md),
  [implementation notes & smoke runbook](docs/plans/2026-07-01-stage-1-implementation-notes.md))
- [Stage 2a plan](docs/plans/2026-07-02-stage-2a-schema-persistence-projection.md) — schemas,
  persistence & projection layer
- [Stage 2b plan](docs/plans/2026-07-02-stage-2b-offline-pipeline.md) — offline pipeline: VAD,
  Whisper.net, merge, phantom-bleed dedup, FLAC/WAV, offline runner

## Privacy

LocalScribe stores everything locally and uploads nothing — by default to a non-synced
folder under your user profile (it warns if you point it at a cloud-synced location). A
visible tray indicator shows when it is recording.

**Recording others is your responsibility.** Many jurisdictions require the consent of some
or all parties before a conversation may be recorded (two-party / all-party consent).
LocalScribe makes the recording state obvious but cannot enforce the law or obtain consent
for you — disclosing the recording to the other participants is up to you.

## License

[MIT](LICENSE) © 2026 imnotwallace
