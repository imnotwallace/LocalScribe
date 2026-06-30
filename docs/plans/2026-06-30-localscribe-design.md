# LocalScribe — Design

- **Date:** 2026-06-30
- **Status:** Validated design — ready for implementation planning
- **Target:** Windows 11 desktop, open-source, local-only

## Overview

LocalScribe is a background desktop app for Windows 11 that captures the audio of
your online meetings and produces a single, locally-stored, speaker-labelled
transcript — powered entirely by a **locally-run Whisper** model. No cloud, no
subscription.

It is, in effect, an open-source take on Granola's transcription layer, minus the
cloud and minus the AI summary (transcripts are clean Markdown that can be fed to
any LLM later if summaries are ever wanted).

The defining trick: it captures **two audio streams at once** — your microphone
(*you*) and the meeting app's playback via loopback (*the remote participants*) —
and merges them by timestamp into one interleaved transcript. Because speaker
attribution comes from *which stream* the audio arrived on, basic "me vs them"
diarisation is structural and free, with no ML required.

### Locked decisions (one-liner)

> Live (VAD-segmented near-real-time) · structural Me/Them via dual-stream +
> optional on-demand Split-speakers · portable whisper.cpp backend · C#/.NET ·
> configurable auto-detect of meetings + manual override.

## Goals

- Capture online-meeting audio in the background on Windows 11 and produce an
  accurate, timestamped, speaker-labelled transcript stored locally.
- Run Whisper **locally** — no per-seat subscription, no audio leaving the machine.
- Distinguish *you* from *the remote side* out of the box; allow optional deeper
  speaker splitting on demand.
- Near-real-time: text appears within a few seconds of each utterance.
- Run on the developer's current NVIDIA (4–6 GB) box **and** survive a future move
  to a modern Lenovo laptop (possibly NPU / ARM64) without a rewrite.

## Non-goals (v1)

- AI summaries / chat over transcripts (deferred; transcripts are LLM-ready Markdown).
- Live/streaming diarisation during the call (on-demand batch is higher quality).
- NPU/DirectML acceleration (seam exists; tooling is immature in 2026).
- True word-by-word streaming captions.
- Scraping participant **names** from the Teams/Zoom UI (fragile; future experiment).
- Cross-session speaker identity / voiceprints.
- Full-text search across meetings (files-as-truth → add a SQLite/FTS5 index later).
- Cloud sync, sharing, multi-device.
- macOS / Linux (whisper.cpp is portable, but capture is Windows-specific).

---

## Key decisions & rationale

### 1. Timing model — live, VAD-segmented near-real-time

There are two very different notions of "live":

- **True word-by-word streaming** (text appears syllable-by-syllable) — genuinely
  hard, GPU-hungry. **Out of scope.**
- **VAD-segmented near-real-time** — continuously listen; a Voice Activity Detector
  (Silero) detects when an utterance ends; that 2–15 s chunk goes to Whisper
  immediately; text appears 1–3 s later. This is what Granola/Otter effectively do.
  **This is v1.**

This choice also dissolves the "lengthy audio" worry: nothing ever buffers a whole
meeting — only utterance-sized chunks — and the transcript is persisted
incrementally (crash-safe).

### 2. Diarisation — structural Me/Them, with optional on-demand Split-speakers

- **The free part:** capturing two separate streams gives **Local** (mic = "Me") vs
  **Remote** (loopback = "Them") for nothing — it is which *stream* the audio came
  from, so it is always correct. A 1:1 call is therefore a perfect two-speaker
  transcript.
- **The fundamental limit:** by the time remote audio reaches Windows, the meeting
  app has **already mixed all remote participants into one render stream**. WASAPI
  loopback only ever sees the mix. *No OS-capture tool can un-mix Alice from Bob* —
  the separation was discarded upstream. So a 3-person meeting is `Me / Others`
  until further processing.
- **Separating further** must come from either acoustic diarisation (ML, anonymous
  "Speaker N", imperfect, best run as a batch pass) or scraping the app's
  active-speaker UI (names, but fragile/app-specific). Only the former is in plan.

**The key abstraction — separate _source_ from _speaker label_:**

- **Source** = `Local` (mic) or `Remote` (loopback). Structural, fixed, always correct.
- **Speaker label** = what's shown. Defaults to "Me"/"Them", but is *refinable* by
  diarisation or by the user typing a name.

This separation makes the conference-room case (multiple people sharing one mic) a
non-special-case: **Split-speakers is a generic "diarise source X" operation**, and
the user can run it on **Local, Remote, or both**. Each source is clustered
*independently* (different mics/codecs make joint clustering unreliable), producing
per-source speakers the user then names:

```
Local  → Speaker 1 → "Sam",   Speaker 2 → "Priya"   (people in your room)
Remote → Speaker 1 → "Alice",  Speaker 2 → "Bob"     (people on the call)
```

Honest caveat: a shared room mic is acoustically harder (distance, reverb, overlap),
so Local-split accuracy will trail a clean headset. A native-C# path exists when we
want it: **sherpa-onnx** (ONNX segmentation + embedding + clustering, with .NET
bindings) — no Python, stays in-stack.

A genuine bonus of the clean Local stream: when you and a remote person talk over
each other, both halves are captured on independent streams and both get
transcribed — exactly the case single-stream tools garble.

### 3. Hardware & portability — swappable engine, portable default

Dev box is NVIDIA 4–6 GB; the eventual target is a modern Lenovo laptop, possibly
with an NPU and possibly **Snapdragon X (ARM64 Windows)**. Implication: **do not
hard-couple to CUDA.** faster-whisper / CTranslate2 (the fastest NVIDIA option) does
*not* accelerate on NPUs or iGPUs and has a weak ARM64 story — building on it would
make the Lenovo move a rewrite.

Two principles:

1. **The transcription engine is a swappable backend** behind `ITranscriptionEngine`
   (`audio segment → text`). One backend ships in v1; others drop in later.
2. **The default backend is portable by construction: whisper.cpp** (via Whisper.net)
   — CPU everywhere, CUDA on the dev box, **Vulkan** on AMD/Intel iGPUs, native
   **ARM64** builds, and the most likely to gain clean NPU execution providers.

NPU is treated as a *later, optional* backend: real but immature in 2026 (Qualcomm AI
Hub has Whisper-base/small for Hexagon; Intel via OpenVINO), with fiddly tooling and
small-model limits.

### 4. Tech stack — C#/.NET

- **Audio capture:** NAudio (WASAPI mic + loopback); **per-process loopback** (capture
  just `Teams.exe`'s render) via CsWin32 bindings to `ActivateAudioInterfaceAsync`
  with `PROCESS_LOOPBACK` — the one piece needing low-level interop. Falls back to
  full-system loopback.
- **VAD:** Silero VAD (ONNX) via `Microsoft.ML.OnnxRuntime`.
- **Transcription:** Whisper.net (whisper.cpp) behind `ITranscriptionEngine`.
- **Diarisation:** sherpa-onnx (.NET bindings), on-demand.
- **Shell / UI:** WPF tray-first app — **WPF-UI** (MIT) for the Windows 11 Fluent look,
  **H.NotifyIcon** for the tray, **CommunityToolkit.Mvvm** for binding. (See decision 6.)

Rationale: single language, native WASAPI access, whisper.cpp portability
(CPU/CUDA/Vulkan + ARM64) matching the NVIDIA-now/NPU-later path, .NET first-class on
Windows ARM64, easy MSIX packaging.

### 5. Trigger — configurable auto-detect + manual override

Auto-detect meeting apps (Teams/Zoom/Webex) by watching `IAudioSessionManager2` for
active audio sessions, with a clear tray recording indicator and manual
Start/Stop/Pause always available. **Auto-detect is a setting (default on) and can be
turned off**, with an editable per-app list. When off, only manual controls drive
sessions. Both the detector and the tray call the same
`SessionManager.StartSession()` / `StopSession()`, so the trigger is fully decoupled
from the pipeline.

### 6. UI — WPF + WPF-UI + H.NotifyIcon

WPF is the most mature .NET desktop framework (largest contributor pool, best data
binding and list virtualization for the live transcript) and is ARM64-safe. **WPF-UI**
(MIT) is "borrowed" to get the Windows 11 Fluent look (Mica, NavigationView, fluent
controls) with minimal hand-styling; **H.NotifyIcon** provides the tray;
**CommunityToolkit.Mvvm** the binding. WinUI 3 looks marginally nicer natively but has
tray-first lifecycle friction and heavier Windows App SDK packaging; Avalonia is a
strong modern alternative with a smaller ecosystem, and its cross-platform edge is
muted because audio capture is Windows-only regardless. See **UI design** below.

---

## Architecture & data flow

Every session runs the same pipeline; the trigger only decides *when* it starts.

```
[Meeting-app audio-session watcher] ──start/stop──┐
[Tray: manual Start / Stop / Pause] ──────────────┤
                                                   ▼
                                            SessionManager
                       ┌───────────────────────────┴──────────────────────────┐
              Mic (WASAPI capture)                          Per-process loopback (Teams.exe)
                source = Local                                    source = Remote
                       │                                                │
             resample → 16 kHz mono                          resample → 16 kHz mono
                       │                                                │
              Silero VAD → segments                          Silero VAD → segments
                       └───────────────────┬────────────────────────────┘
                                           ▼
                    Bounded queue → Whisper.net (whisper.cpp engine)
                                           ▼
                       Merge by session clock (sorted insert)
                              ┌────────────┴────────────┐
                         Live view              Incremental store (crash-safe)
```

Two independent capture streams (mic = Local, per-process loopback = Remote) are each
resampled to 16 kHz mono, then sliced into utterance-sized segments by Silero VAD.
Finalized segments `{source, startMs, endMs, pcm}` go onto a shared bounded queue; a
transcription worker runs whisper.cpp on each and returns text. The merger does a
sorted insert by **session-relative clock**, then fans out to the live view and an
append-only file.

**Four properties that matter:**

- **Common clock → deterministic interleave.** Both streams stamped against one
  monotonic session clock; merging is a sorted insert.
- **Speaker tags are structural, not ML** — which *stream* a segment came from.
- **Incremental persistence** → crash-safe; nothing buffers the whole meeting.
- **Backpressure, never drop.** If transcription lags on slower hardware, the queue
  absorbs it (spill to disk if needed); the transcript trails real-time but loses no
  audio.

---

## Components & interfaces

| Component | Responsibility |
|---|---|
| **SessionManager** | Owns lifecycle (`Start/Stop/Pause`), holds the session clock, wires the pipeline. |
| **IMeetingDetector** | Watches `IAudioSessionManager2` for Teams/Zoom/Webex going active → `MeetingStarted(app, pid)`. No-op when auto-detect disabled. Min-duration heuristic kills false triggers. |
| **ICaptureSource** ×2 | `MicCaptureSource` (Local) + `ProcessLoopbackCaptureSource(pid)` (Remote, via CsWin32; full-system fallback). Emits 16 kHz mono PCM stamped on the session clock. |
| **IVadSegmenter** | Silero VAD (ONNX) → finalized `AudioSegment`, with max-length cap + padding. |
| **ITranscriptionEngine** | `WhisperNetEngine` behind a bounded `Channel` worker (caps VRAM). NPU/DirectML drops in here later. |
| **TranscriptMerger + TranscriptStore** | Sorted-insert by clock → live view + append-only file. |
| **IDiarisationEngine** | `SherpaOnnxDiariser`, on-demand, per selected source. |
| **TrayApp** | Recording indicator, live transcript, session list, "Split speakers" dialog, settings. |

```csharp
enum SourceKind { Local, Remote }

interface ICaptureSource {
    SourceKind Source { get; }
    event Action<PcmFrame> FrameAvailable;          // 16 kHz mono, session-clock stamped
    void Start();
    void Stop();
}

interface IVadSegmenter {
    IAsyncEnumerable<AudioSegment> Segment(ICaptureSource source, CancellationToken ct);
}

interface ITranscriptionEngine {
    Task<string> TranscribeAsync(AudioSegment segment, CancellationToken ct);
}

interface IMeetingDetector {                         // Start() is a no-op when disabled in settings
    event Action<MeetingInfo> MeetingStarted;        // app, pid
    event Action MeetingEnded;
    void Start();
    void Stop();
}

interface IDiarisationEngine {
    Task<DiarisationResult> DiariseAsync(
        IReadOnlyList<AudioSegment> segments, DiarisationOptions options, CancellationToken ct);
}

record AudioSegment(SourceKind Source, long StartMs, long EndMs, ReadOnlyMemory<float> Pcm);
record TranscriptSegment(int Seq, SourceKind Source, long StartMs, long EndMs,
                         string Text, string SpeakerLabel, string? SpeakerId);
```

---

## UI design

WPF, tray-first, styled with **WPF-UI** (Windows 11 Fluent — Mica, NavigationView,
fluent controls) so it looks native with minimal hand-styling. Tray via
**H.NotifyIcon**; MVVM via **CommunityToolkit.Mvvm**; **Segoe Fluent Icons** (ships
with Win11). All MIT-licensed and ARM64-safe.

Surfaces:

- **Tray icon + flyout** — the primary surface. Recording-state indicator
  (idle / recording / paused), current session at a glance, quick Start/Stop/Pause,
  and a menu to open the main window, session history, and settings.
- **Live transcript window** — a virtualized, auto-scrolling list of speaker-labelled
  segments updating in near-real-time (chat-like: `[mm:ss] Speaker: text`), with a
  lag/queue indicator and manual Pause. Backed by the `TranscriptMerger`'s observable
  segment collection.
- **Session history** — past sessions (title, app, date, duration, diarised?), opening
  any into a read view; rename, delete, reveal-in-Explorer.
- **Split-speakers dialog** — pick source(s) (Local / Remote / both), run diarisation,
  then a small speaker-map editor to assign names to clusters; non-destructive
  re-render on apply.
- **Settings** — the surface listed below.

Optional later polish: a small live audio-level meter per source (ScottPlot /
LiveCharts2).

---

## Storage format

**Guiding principle: files are the source of truth.** Plain, readable files — not a
database — so they can be backed up, synced (Dropbox/git), `grep`-ed, with no
lock-in. A DB comes later only as a *rebuildable search index*.

One folder per session, under a configurable root defaulting to
`Documents\LocalScribe\`:

```
Documents\LocalScribe\
  2026-06-30_1432_Teams_weekly-sync\
     transcript.jsonl     ← source of truth, append-only, immutable
     speakers.json        ← diarisation + name overrides (non-destructive)
     transcript.md        ← rendered view, regenerated from the two above
     session.json         ← metadata (app, times, model, schemaVersion, sources)
     audio\
        local.flac        ← retained per-source audio (enables Split-speakers)
        remote.flac
```

- `transcript.jsonl` is written segment-by-segment and **never rewritten**
  (crash-safe append log).
- Diarisation and renaming are **non-destructive** — they touch only `speakers.json`
  (cluster→segment assignments + `label→name` map).
- `transcript.md` is a pure *projection* of JSONL + speakers.json, re-rendered
  whenever labels change.

```jsonl
{"seq":17,"source":"Remote","startMs":85320,"endMs":89110,"text":"I pushed the auth changes last night.","speakerLabel":"Them"}
```
```markdown
**[01:25] Alice:** I pushed the auth changes last night.
```
(`Alice` comes from `speakers.json` after diarise + name. Consecutive same-speaker
segments merge into a paragraph.)

**Audio retention (the one real trade-off).** On-demand Split-speakers needs the audio
to exist later, so per-source **FLAC is retained by default** (lossless, ~half of WAV
≈ 55 MB/hr/source, re-transcribable and diarisable), with a configurable cleanup
policy: *delete after diarisation* / *after N days* / *keep forever* / *never keep*
(the privacy-max option, which forgoes post-session diarisation). Opus (~10 MB/hr/
source) is a future space-saver.

**Crash recovery.** A session folder lacking `endedAt` in `session.json` on next
launch is flagged "recovered" and its Markdown re-rendered from the existing JSONL.
Nothing is lost.

---

## Error handling & edge cases

Theme: **degrade gracefully, mark it in the transcript, never drop audio silently.**

**1. Capture resilience**
- **Device hot-swap mid-call** (e.g. plugging in headphones when the call starts):
  subscribe to `IMMNotificationClient`, re-bind to the new default device, write an
  `[audio device changed]` marker.
- **Per-process loopback fails** → automatic fallback to full-system loopback, flagged
  "degraded (system audio)".
- **Speakers instead of headphones** → remote voices bleed from speakers into the
  Local mic. Per-process loopback keeps Remote clean regardless; lean on the meeting
  app's echo cancellation and **recommend headphones** (documented limitation).
- **Capture never blocks on transcription** — decoupled via the bounded queue; spill
  to disk rather than drop.

**2. Detection robustness**
- **False triggers** (notification "dings", warm sessions) → min-duration debounce +
  require sustained audio.
- **Ambiguous end** (hold music/lobby) → idle-timeout; manual Stop always wins.
- **Browser calls** (Google Meet, Slack huddle) — audio is `chrome.exe`, not cleanly
  auto-detectable. **v1: browser calls use manual Start.** Native apps auto-detect;
  the app list is user-editable.

**3. Model & backend**
- **First run** → model download with progress + checksum + retry; offline thereafter.
- **Backend cascade** CUDA → Vulkan → CPU; **VRAM OOM** on the 4–6 GB target →
  auto-downgrade model (e.g. small→base) or move to CPU, with a warning. Chosen
  backend logged + overridable.
- **Can't keep up (RTF > 1)** → growing queue detected → warn + optional
  auto-downgrade; transcript still completes, just trails.
- **Silence/noise hallucinations** ("Thank you", "[music]") → VAD-gating already
  suppresses most, plus a no-speech-probability threshold drops the rest.

**4. System lifecycle**
- **Sleep/resume** mid-call → `[paused: system sleep]` marker, auto-resume if the
  meeting's still live.
- **Crash** → recovery from append-only JSONL.
- **Disk full** → stop retaining audio, keep the transcript, warn.
- **Mic permission denied** → detect, prompt to enable in Windows Settings.

**5. Privacy & consent**
- **Always-visible recording indicator** (tray) — never capture silently.
- **First-run consent notice** (recording others is jurisdiction-dependent; the app
  makes state obvious but can't enforce law) + local-only storage + easy **Pause** and
  **delete-session**.

---

## Testing strategy

**Headline principle — the Humble Object pattern.** All hardware-, GPU-, and
OS-specific code (WASAPI interop, Whisper.net, sherpa-onnx) lives in razor-thin
adapters behind the interfaces above. **All logic sits in a deterministic core that
tests with zero hardware.** Testability was a design output, not an afterthought.

| Layer | Covers | Fakes / inputs |
|---|---|---|
| **Unit** (fast, every PR, no GPU) | Merger interleave order; Store JSONL→MD projection + crash-recovery + non-destructive relabel; VAD boundary/cap logic; Detector debounce + idle-timeout state machine; backend cascade + VRAM-OOM downgrade; retention policy | `FakeCaptureSource` (replays WAV), `FakeTranscriptionEngine` (scripted text), injected `IClock`, temp dirs |
| **Fixture integration** (opt-in, needs models, CPU-only) | Real VAD → segment boundaries; real Whisper → **WER** threshold vs reference; sherpa-onnx → **DER**/cluster-count on an N-speaker clip; silence clips → assert *no* hallucination | small audio corpus, in-repo or downloaded |
| **End-to-end (synthetic)** | Whole pipeline wired for real, fed a scripted two-stream `local.wav`+`remote.wav` "mock meeting" → assert `transcript.jsonl`/`.md` ≈ golden within fuzz | pre-recorded stream pair |
| **Manual matrix** (pre-release) | Real Teams 1:1 + group, Zoom, Webex, browser manual-start, headphones vs speakers, device hot-swap, sleep/resume, first-run download, CUDA→CPU fallback | real devices/apps — documented checklist |

**Determinism controls:** inject `IClock` (no `DateTime.Now` in logic), temp
filesystem, **pin model version + quantization** so WER/DER thresholds stay stable,
fuzzy assertions (thresholds, keyword presence) for ML — never exact-string.

**Deliberately NOT automated:** real WASAPI capture and GPU backends need devices CI
can't provide → validated by the manual matrix, with adapters kept thin so almost no
logic hides in the untestable zone.

**CI shape:** PR gate = unit suite (fast, no models, runs even on Linux); nightly =
fixture + E2E (CPU, small model); manual matrix before releases. The deterministic
core is ideal for **test-first** development.

---

## Settings surface

- Storage root (default `Documents\LocalScribe\`)
- Audio retention policy (after-diarisation / after-N-days / forever / never)
- Model choice + backend override (auto / CUDA / Vulkan / CPU)
- Language (auto / fixed)
- **Auto-detect meetings (on/off) + editable app list**
- Recording indicator + manual-control hotkeys

---

## v1 scope & build sequence

### In scope
Tray background app · dual-stream capture (mic Local + per-process loopback Remote,
full-system fallback) · VAD-segmented near-real-time transcription (Whisper.net,
backend cascade) · configurable auto-detect + manual controls · structural Me/Them ·
on-demand Split-speakers (sherpa-onnx, per-source, name assignment, non-destructive) ·
files-as-truth storage + retained FLAC + cleanup policy · live view + session list ·
crash recovery · device-swap handling · recording indicator · first-run model
download · settings surface.

### Deferred (seam already exists)
AI summaries · live/streaming diarisation · NPU/DirectML backend · word-by-word
captions · Teams/Zoom name-scraping · cross-session voiceprints · full-text search ·
cloud sync/sharing · macOS/Linux.

### Build sequence (walking skeleton — riskiest thing first, each stage demoable)

1. **Capture spike** — tray shell + prove the scary part: per-process WASAPI loopback
   (CsWin32) → two clean WAVs from a real Teams call.
2. **Offline pipeline** — WAV → VAD → Whisper.net → JSONL/MD with Me/Them merge.
   *Most logic + unit tests land here.*
3. **Live wiring** — real-time capture→pipeline, live view, bounded-queue
   backpressure, backend cascade.
4. **Trigger** — `IMeetingDetector` + debounce + manual controls + configurable
   toggle/list + session lifecycle.
5. **Split-speakers** — sherpa-onnx, per-source selection, name UI, non-destructive
   re-render.
6. **Hardening** — device-swap, sleep/resume, disk-full, first-run UX, settings,
   consent notice, manual test matrix, MSIX packaging (x64 + ARM64).
