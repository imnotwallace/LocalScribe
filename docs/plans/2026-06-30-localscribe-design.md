# LocalScribe — Design

- **Date:** 2026-06-30
- **Status:** Validated design — Stage 1 (Capture spike) **DONE** (net10, hardware-validated);
  implementing Stage 2. (rev: **2026-07-02 design session** folded in — manual-primary trigger
  with auto-detect deferred to a seam, legal Matter/session/roster domain model, correction-only
  non-destructive transcript editing, keep-audio-by-default retention, tray-supplementing
  recording overlay, `.zip`/`.docx` export, and custom vocabulary. Earlier rev incorporated the
  2026-06-30 design-review decisions.)
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

> **Primary use case (anchors v1):** a lawyer recording Webex calls with incarcerated clients.
> Two-party-consent and attorney–client privilege are load-bearing — they drive the legal domain
> model (**Matters**), the **correction-only** (never-destructive) transcript editing,
> **keep-audio-by-default** retention, the **screen-capture-excluded recording overlay**, and the
> **export**/**custom-vocabulary** features folded in on 2026-07-02. The generic "me vs them"
> transcription still stands on its own; recording stays matter-agnostic (record first, classify
> later).

### Locked decisions (one-liner)

> Live (VAD-segmented near-real-time) · structural Me/Them via dual-stream +
> optional on-demand Split-speakers · portable whisper.cpp backend · C#/.NET ·
> **manual Start/Stop/Pause primary** (meeting auto-detect deferred to an interface seam).

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

- AI summaries / chat over transcripts (deferred; a null `summary.md` placeholder is reserved,
  but nothing is generated — transcripts are LLM-ready Markdown).
- Live/streaming diarisation during the call (on-demand batch is higher quality).
- NPU/DirectML acceleration (seam exists; tooling is immature in 2026).
- True word-by-word streaming captions.
- Mid-meeting language switching (session language is probed then locked; see specs §3).
- Scraping participant **names** from the Teams/Zoom UI (fragile; future experiment).
- Cross-session **acoustic** speaker identity / voiceprints. (The Matter-scoped participant
  roster reuses *names*, which is declarative metadata — **not** acoustic identity; no audio
  identity ever crosses sessions.)
- **Deletion / hiding / redaction of transcript content** — dropped entirely for evidentiary
  integrity (editing is **correction-only**; whole-session delete for records management stays).
- **Meeting auto-detection** — deferred out of the v1 contract to an `IMeetingDetector` seam;
  v1 ships manual-only.
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

Accepted tradeoff: live VAD-segmented transcription is lower-quality than a single
batch pass over the whole recording (as decision 2 notes), and v1 accepts that —
mitigated by the regression quality bar in **Testing strategy**.

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

**Declared participant count gates/seeds Split — it does NOT touch VAD.** Each side carries a
declared participant count (defaults **Local = 1, Remote = 1** — lawyer + client). Silero VAD
stays per-source and speaker-count-agnostic; the count only (i) decides whether Split-speakers is
even *offered* for a side and (ii) **seeds the diarisation cluster-count K as a soft prior**. A
side declared as a single participant uses that participant's name as the display label **for free
— with no no-op diarisation pass** — falling through to the normal `speakers.json` flow on
multi-speaker sides, with Me/Them as the terminal fallback. Diarisation stays strictly on-demand
and never auto-runs (honours the live/streaming-diarisation non-goal, L45).

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
  just `CiscoCollabHost.exe`'s render — **Webex, the primary target**) via CsWin32 bindings to
  `ActivateAudioInterfaceAsync` with `PROCESS_LOOPBACK` — the one piece needing low-level interop,
  and **hardware-validated in Stage 1** (direct-to-16 kHz mono via `AUTOCONVERTPCM`,
  `mode=DirectMono16k`, confirmed on real audio). Remote is a `{auto | perProcess | systemMix}`
  **app/mode picker over ONE logical stream**: per-process stays the default/cleanest for
  Webex/Zoom, but **Teams** (`ms-teams.exe` renders all-zeros to per-process loopback — a Microsoft
  bug) and **browser calls** (shared Chromium audio) **auto-fall-back to full-system EXCLUDE-self
  mix**, with a visible warning + a `degraded: system-audio loopback` marker in the transcript. A
  legal recording must never silently produce an empty `remote.flac`.
- **VAD:** Silero VAD (ONNX) via `Microsoft.ML.OnnxRuntime`.
- **Transcription:** Whisper.net (whisper.cpp) behind `ITranscriptionEngine`, with a bounded
  (~200-token) **custom-vocabulary initial-prompt bias** (global legal dictionary + per-Matter
  term list) and a deterministic **heard→correct** post-pass at the projection layer (see
  decision 11).
- **Diarisation:** sherpa-onnx (.NET bindings), on-demand.
- **Shell / UI:** WPF tray-first app — **WPF-UI** (MIT) for the Windows 11 Fluent look,
  **H.NotifyIcon** for the tray, **CommunityToolkit.Mvvm** for binding. (See decision 6.)

Rationale: single language, native WASAPI access, whisper.cpp portability
(CPU/CUDA/Vulkan + ARM64) matching the NVIDIA-now/NPU-later path, .NET first-class on
Windows ARM64, and clean unpackaged distribution (see decision 7 — a lightweight
installer/updater rather than MSIX, so files-as-truth and a configurable storageRoot
aren't fighting MSIX's `%APPDATA%` virtualization).

### 5. Trigger — manual Start/Stop/Pause primary (auto-detect deferred to a seam)

**v1 ships manual-only.** Manual Start/Stop/Pause from the tray, main window, overlay, and
hotkeys is the single, primary trigger — the load-bearing consent posture is that the user
**deliberately starts** each recording. Meeting **auto-detection is deferred out of the v1
contract**: only the `IMeetingDetector` interface seam is kept, so a detector can drop in later
as a fast-follow without reworking the pipeline. `settings.autoDetect.enabled` defaults to
**`false`** and there is **no shipping detector in v1**. (Rationale: manual-primary removes an
unreliable subsystem — Teams' all-zeros per-process loopback, browser shared-Chromium audio —
from the v1 critical path, and eliminates any surprise-recording consent risk.)

When the detector is eventually implemented behind the seam it will watch `IAudioSessionManager2`
for active audio sessions over the closed native set {Teams, Zoom, Webex} (an
enable/disable-toggle set, not a way to add arbitrary processes), with a min-duration debounce, and
will call the **same** `SessionManager.StartSession()` / `StopSession()` the manual controls
already use — so the trigger stays fully decoupled from the pipeline. **v1 is single-session:** a
second start while already Recording is ignored (surface a tray hint); a manual Stop always wins.

### 6. UI — WPF + WPF-UI + H.NotifyIcon (tray + optional overlay)

WPF is the most mature .NET desktop framework (largest contributor pool, best data
binding and list virtualization for the live transcript) and is ARM64-safe. **WPF-UI**
(MIT) is "borrowed" to get the Windows 11 Fluent look (Mica, NavigationView, fluent
controls) with minimal hand-styling; **H.NotifyIcon** provides the tray;
**CommunityToolkit.Mvvm** the binding. WinUI 3 looks marginally nicer natively but has
tray-first lifecycle friction and heavier Windows App SDK packaging; Avalonia is a
strong modern alternative with a smaller ecosystem, and its cross-platform edge is
muted because audio capture is Windows-only regardless. See **UI design** below.

The **tray icon stays the immovable, load-bearing consent indicator**; a minimal always-on-top
**recording overlay** (decision 12 / **UI design**) *supplements* it as an opt-out convenience.
All three surfaces (tray, overlay, live view) bind one `SessionViewModel` and route Pause/Stop to
the same `SessionManager`.

### 7. Build target — Windows-only, single solution, built on Windows

LocalScribe only exists on Windows (its reason for being is WASAPI per-process loopback),
so cross-platform portability of the *product* is a non-goal, not a deferred goal. We
therefore keep a **single `net10.0-windows` solution** (Core, capture, UI, and tests all
Windows-targeted) and treat **Windows as the build/test/CI source of truth** — exercising
the real runtime and hitting the risky WASAPI interop first. The valuable testability
comes from the `ICaptureSource` seam + fakes (Humble Object), which is independent of any
cross-platform assembly; a pure `net10.0` core could be extracted later in an afternoon if
cheaper Linux CI or a compile-enforced boundary is ever wanted, but that is **YAGNI** for
now. Consequence: the unit suite targets `net10.0-windows` and runs on Windows CI.

**Packaging: unpackaged app + lightweight installer/updater, not MSIX.** Ship an
unpackaged build wrapped by a small installer/updater (e.g. **Velopack**) and code-sign
it with free OSS signing (SignPath / Azure Trusted Signing). MSIX is rejected because its
`%APPDATA%` virtualization fights the files-as-truth model and a user-configurable
`storageRoot`; going unpackaged resolves that conflict and gives the configurable root for
free. **Model weights live outside the updatable package** (so app updates stay small and
don't re-ship multi-GB weights). Run-at-login is a registry **Run** key / startup shortcut
(decision 13), not an MSIX `StartupTask`.

### 8. Legal domain model — Matters, sessions, and reusable participant rosters (record first, classify later)

The primary use case makes the raw transcript a **legal record** organised by matter (case), not
just a standalone file. v1 adds a lightweight legal domain model on top of the files-as-truth
store, but keeps recording completely matter-agnostic: **nothing is required before you can hit
Record.**

- **Matter entity** (`matter.json`): a case/engagement `{Name, Reference, Description, DateCreated,
  participant roster}`. A small `matters-index.json` lists them for the picker.
- **Matter ↔ Session is many-to-many (tagging).** A session carries `matterIds[]`; a call that
  touches two matters can be tagged with both, and a matter aggregates all its sessions.
  Assignment is **post-hoc and editable** — record first, classify later.
- **Participants are Matter-scoped reusable rosters.** A matter owns a roster of people (client,
  opposing counsel, witnesses…). A session's participants are picked from the **union of its
  matters' rosters** (dropdown) *or* typed **free-text** (an unknown caller you rename later);
  each is tagged `Local` or `Remote` for that session. Adding someone inline creates them in the
  Matter roster. This roster reuse is **name metadata**, explicitly *not* the acoustic
  cross-session-voiceprint non-goal (L50) — no audio identity ever crosses sessions.
- **Self-identity autofill.** A `self` object in Settings (`{name, role?}`) auto-fills the Local
  "Me" participant, **snapshotted** into each session at start (so old privileged records don't
  mutate when the profile later changes).
- **The roster is the source of truth for names; readable renderings resolve ids→names live**, and
  the resolved names are also **snapshotted into the session** (see `session.txt`) so an exported
  folder is portable and human-readable with no app.

### 9. Session metadata model — user truth vs system truth (the `meta.json` / `session.json` split)

Session metadata splits across two files to separate **human-asserted** from **machine-measured**
facts (evidentiary value) and to eliminate the race where a background writer (finalize, relabel,
retention cleanup) would clobber an in-flight user edit on the same file:

- **`meta.json` — user-owned truth:** `title` (Session name), `participants[]`, `description`,
  `medium`, `matterIds[]`, `localCount`/`remoteCount`, `summaryRef`, and edited flags. Its own
  `schemaVersion`. `title` **relocates here** from `session.json`.
- **`session.json` — system-owned truth (→ schema v3):** times, `app`, model, backend, sources,
  `retainedAudioSources`, `recovered`, counts, and the resolved **device snapshot** (decision 12).
  No user-editable fields.

Field notes:

- **Medium** is a **separate user-editable field**, enum `{Webex | Zoom | Teams | Phone |
  In-person | Other}`, **default-derived from the system `app`** at start but never overwriting it
  — `app` stays the closed system capture-path truth that recovery/detector key on. This lets
  Webex-in-browser, phone-on-speaker, and in-person captures be labelled honestly.
- **Summary** is a **reserved placeholder only**: a `summary.md` filename (absent until generated)
  plus a nullable `summaryRef` pointer. AI summaries stay a locked non-goal (L44) — no generation
  in v1; the stub just gives a future `.docx`/`.md` a clean source.
- **Date/Time** is the existing read-only system `startedAtUtc / endedAtUtc / durationMs`
  (`durationMs` still includes paused time; paused/resumed markers annotate the gap).

### 10. Transcript editing — correction only, non-destructive (evidentiary integrity)

Because a transcript is a **privileged legal record**, v1 has **no deletion, hiding, or redaction
of transcript content** — those notions are dropped entirely. Editing is strictly **correction**:

- **In-place text correction** of a mis-transcribed segment, and **per-segment speaker
  reassignment** ("this line was actually Bob").
- Both are non-destructive, written to a new **`edits.json`** overlay keyed by the immutable
  `seq` (the structural twin of `speakers.json`, with its own `schemaVersion`, and **no
  tombstone/hide records**). `transcript.jsonl` stays append-only and immutable; the machine
  original is always preserved — the retained original + tracked correction is an **evidentiary
  strength**, not a liability.
- `transcript.md` / exports become a projection of **`jsonl + speakers.json + edits.json`**
  (+ the vocabulary heard→correct pass). Apply-order: load JSONL → apply `edits.json` text
  overrides → render-layer dedup → resolve names via `speakers.json` → group consecutive
  same-speaker. A human edit beats the auto dedup (user intent wins).
- Text edits live in `edits.json`; a **manual pinned speaker reassignment** writes
  `assignments[source][seq]` + a `pinned` marker in `speakers.json`, and **re-diarisation
  preserves pinned entries**, rewriting only unpinned ones — one authority per field, no second
  speaker-resolution path.
- Editing is allowed only on **finalized/recovered** sessions, never a live Recording/Paused one.
- **Whole-session delete stays** (records management for accidental/test recordings) — that is
  coarse folder deletion, not per-segment content erasure. Segment split / merge / insert /
  reorder edits stay **deferred** (they fight `seq`-immutability and the per-source structural
  model).

### 11. Custom vocabulary — global + per-Matter (bias + correction)

Legal calls are full of names and jargon Whisper mangles. v1 adds a two-layer custom vocabulary,
tied to the Matter entity:

- A **global legal dictionary** (settings-adjacent) layered with a **per-Matter term list**
  (client / opposing-counsel names, case jargon) held on the Matter.
- Applied two ways: (1) fed to whisper.cpp as an **initial-prompt bias** — a bounded (~200-token)
  curated shortlist so the prompt budget isn't blown — to nudge recognition; and (2) a
  **deterministic `heard→correct` post-pass at the projection layer** that rewrites known
  mis-hearings after transcription. The post-pass is non-destructive (projection-only; JSONL keeps
  the raw machine text).

### 12. Recording overlay + device config

- **Recording overlay** — a minimal always-on-top **pill** (state dot + elapsed timer + a
  Local/Remote "audio present" **two-bar** indicator + **Pause/Stop**) that **supplements** the
  tray. It confirms at a glance that the un-repeatable jail-call is actually capturing *both*
  streams. **Session name/participants are suppressed by default** (opt-in, tooltip-only) —
  rendering privileged matter on an always-on-top surface is a leak. The **tray stays the
  immovable, load-bearing consent indicator**; the overlay is opt-out convenience. It is
  **excluded from screen-capture** (`WDA_EXCLUDEFROMCAPTURE`, **default on**) so a lawyer sharing
  their screen to the client over Webex gets a clean share and the recording signal stays local.
  Rendered `Topmost`, `ShowInTaskbar=false`, with `WS_EX_NOACTIVATE` + `ShowActivated=false` so it
  never steals focus mid-call; PerMonitorV2 declared; remembered position clamped into the current
  virtual screen. (Start stays on tray/main/hotkey; the overlay shows Pause/Stop only, during
  Recording/Paused.)
- **Device config.** Persistence = a global default in `settings.json` + an optional per-session
  override at manual Start (which does **not** mutate the global) + the resolved actuals
  snapshotted into `session.json`. **Mic** follows the Windows **Communications default** with an
  optional explicit **pin** (store device **ID** + **friendly name**); a *pinned* device that
  vanishes falls back to default **and writes a marker** — a pin is never silently rebound (the
  follow-default mode is the only one that auto-rebinds on hot-swap). **Remote** is the
  `{auto | perProcess | systemMix}` app/mode picker from decision 4. A **pre-flight ~1 s peak
  probe** at Start (reusing the Stage-1 SpikeRunner `localPeak`/`remotePeak`) warns if a source is
  silent before committing.

### 13. Export — self-contained `.zip` archive (v1) + `.docx` transcript (fast-follow)

Two export types share one session/Matter picker:

- **`.zip` archive (v1):** bundle a **self-contained session folder** (audio + transcript +
  `session.txt` + metadata) into a zip, operable **per session or per Matter** (all sessions
  tagged with it). Audio defaults to **FLAC** (neutral, ~half of WAV); **WAV** is a settings
  option for maximum compatibility. Every session folder therefore **always** also carries a
  neutral, human-readable **`session.txt`** (name, matter(s), participants, date/time, medium,
  description, summary) alongside the readable `transcript.md`, so the folder opens in Notepad +
  a media player with no app; the precise JSON layers stay the app's internal truth.
- **`.docx` transcript (fast-follow):** a formatted document projection via
  `DocumentFormat.OpenXml` (MIT, no Word/COM dependency, ARM64/headless-safe) — metadata header,
  per-page "PRIVILEGED & CONFIDENTIAL" footer, a **non-optional** machine-generated-accuracy
  disclaimer, and timestamped speaker turns. It renders the **resolved, edited** text
  (`jsonl + speakers.json + edits.json`) and reads **participants from the user-curated roster,
  not diarised clusters** (a silent attendee produces no cluster; a shared mic produces unnamed
  clusters — conflating them misrepresents who was on a filed legal document). It is
  **export-on-demand to a chosen path, not a tracked file** (a binary would churn under sync and
  break files-as-truth).

---

## Architecture & data flow

Every session runs the same pipeline; the trigger only decides *when* it starts.

```
[Tray / overlay / hotkeys: manual Start / Stop / Pause] ──start/stop──┐
[IMeetingDetector seam — auto-detect DEFERRED (not shipped in v1)] ····┤
                                                                      ▼
                                                              SessionManager
                       ┌──────────────────────────────────────────────┴─────────────────────────┐
              Mic (WASAPI capture)                        Per-process loopback (Webex / CiscoCollabHost.exe)
                source = Local                            remote = {auto|perProcess|systemMix}, source = Remote
                       │                                  (Teams / browser auto-fall-back to system-mix + marker)
             → 16 kHz mono (AUTOCONVERTPCM)                       → 16 kHz mono (AUTOCONVERTPCM)
                       │                                                │
              Silero VAD → segments                          Silero VAD → segments
                       └───────────────────┬────────────────────────────┘
                                           ▼
              Bounded queue → Whisper.net (whisper.cpp engine) + vocabulary-bias prompt
                                           ▼
                       Merge by session clock (sorted insert)
                              ┌────────────┴────────────┐
                    Live view + overlay          Incremental store (crash-safe)
```

Two independent capture streams (mic = Local, per-process loopback = Remote) are each
converted to 16 kHz mono (direct via `AUTOCONVERTPCM`, Stage-1 validated), then sliced into
utterance-sized segments by Silero VAD. Finalized segments `{source, startMs, endMs, pcm}` go onto
a shared bounded queue; a transcription worker runs whisper.cpp on each (with the
custom-vocabulary initial-prompt bias) and returns text. The merger does a sorted insert by
**session-relative clock**, then fans out to the live view / overlay and an append-only file.

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
| **SessionManager** | Owns lifecycle (`Start/Stop/Pause`), holds the session clock, wires the pipeline. Resolves device config (mic follow-default/pin + Remote `{auto\|perProcess\|systemMix}`), runs the pre-flight peak probe, and owns the session record split (`meta.json` user truth + `session.json` v3 system truth). |
| **IMeetingDetector** | **DEFERRED — interface seam only in v1 (no shipping detector); `autoDetect.enabled` defaults `false`.** When implemented as a fast-follow: watches `IAudioSessionManager2` for {Teams/Zoom/Webex} → `MeetingStarted(app, pid)`, min-duration debounce, single-session, calling the same `SessionManager.StartSession()`/`StopSession()`. |
| **ICaptureSource** ×2 | `MicCaptureSource` (Local; follow-Communications-default or pinned device, ID + friendly name) + `ProcessLoopbackCaptureSource` (Remote; `{auto\|perProcess\|systemMix}`, Webex/`CiscoCollabHost.exe` per-process default, Teams/browser auto-fall-back to system-mix EXCLUDE-self with `degraded` marker). Emits 16 kHz mono PCM (direct via `AUTOCONVERTPCM`) stamped on the session clock. |
| **IVadSegmenter** | Silero VAD (ONNX) → finalized `AudioSegment`, with max-length cap + padding. Per-source and speaker-count-agnostic. An in-progress utterance is force-flushed on Stop/Pause/idle/EOF (specs §4). |
| **ITranscriptionEngine** | `WhisperNetEngine` behind a bounded `Channel` worker (caps VRAM), fed the `IVocabularyProvider` initial-prompt bias. NPU/DirectML drops in here later. |
| **TranscriptMerger + TranscriptStore** | Sorted-insert by clock → live view + append-only `transcript.jsonl`. Projection = `jsonl + speakers.json + edits.json` (+ vocabulary heard→correct pass) → `transcript.md` + `session.txt`. |
| **IEditStore** | Non-destructive `edits.json` correction overlay (seq-keyed **text corrections** + drives per-segment **speaker reassignment**; **no tombstones/hide**). Only on finalized/recovered sessions. |
| **IDiarisationEngine** | `SherpaOnnxDiariser`, on-demand, per selected source. Gated by the 1-vs-many count; declared count seeds cluster-K as a soft prior; preserves manual `pinned` assignments in `speakers.json`. |
| **IMatterStore** | CRUD over Matters (`matter.json` + `matters-index.json`); owns the per-Matter participant roster and per-Matter vocabulary term list. Matter ↔ session is many-to-many via `meta.matterIds[]`. |
| **IMetadataStore** | Reads/writes user-owned `meta.json` (title, participants[], description, medium, matterIds, summaryRef, edited flags), kept separate from system `session.json` to avoid the writer race. |
| **IVocabularyProvider** | Global + per-Matter term lists → a bounded (~200-token) initial-prompt shortlist (bias) and a deterministic `heard→correct` map (post-pass at projection). |
| **ITranscriptExporter** | `.zip` self-contained archive (v1, per session or per Matter; FLAC default / WAV option) + `.docx` transcript (fast-follow, OpenXML, export-on-demand). |
| **OverlayWindow** | Minimal always-on-top recording pill (state + timer + Local/Remote audio-present bars + Pause/Stop); excluded from screen-capture; binds the shared `SessionViewModel`. |
| **TrayApp** | Recording indicator (load-bearing consent), live transcript, Matter→Session manager + metadata editing, correction editing, "Split speakers" dialog, export picker, settings. |

```csharp
enum SourceKind { Local, Remote }
enum RemoteMode { Auto, PerProcess, SystemMix }
enum Medium { Webex, Zoom, Teams, Phone, InPerson, Other }

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

interface IMeetingDetector {                         // DEFERRED: seam only in v1, Start() a no-op
    event Action<MeetingInfo> MeetingStarted;        // app, pid
    event Action MeetingEnded;
    void Start();
    void Stop();
}

interface IDiarisationEngine {
    Task<DiarisationResult> DiariseAsync(
        IReadOnlyList<AudioSegment> segments, DiarisationOptions options, CancellationToken ct);
}

// --- 2026-07-02 additions ---

record Matter(string Id, string Name, string? Reference, string? Description,
              DateTimeOffset DateCreated, IReadOnlyList<Participant> Roster);
record Participant(string Id, string Name, SourceKind Side, string? Role, bool IsSelf);
record SessionMeta(string Title, IReadOnlyList<Participant> Participants, string? Description,
                   Medium Medium, IReadOnlyList<string> MatterIds, string? SummaryRef);

interface IMatterStore {
    Task<Matter> CreateAsync(Matter matter, CancellationToken ct);
    Task<IReadOnlyList<Matter>> ListAsync(CancellationToken ct);
    Task SaveAsync(Matter matter, CancellationToken ct);
}

interface IMetadataStore {                            // user-owned meta.json (split from session.json)
    Task<SessionMeta> LoadAsync(string sessionId, CancellationToken ct);
    Task SaveAsync(string sessionId, SessionMeta meta, CancellationToken ct);
}

interface IEditStore {                                // non-destructive edits.json (correction only)
    Task ApplyTextCorrectionAsync(string sessionId, int seq, string correctedText, CancellationToken ct);
    Task ReassignSpeakerAsync(string sessionId, int seq, string speakerId, CancellationToken ct);
}

interface IVocabularyProvider {
    string BuildInitialPrompt(IReadOnlyList<string> matterIds);            // bounded ~200-token bias shortlist
    string ApplyCorrections(string text, IReadOnlyList<string> matterIds); // heard->correct post-pass
}

interface ITranscriptExporter {
    Task ExportZipAsync(IReadOnlyList<string> sessionIds, string destPath,
                        AudioFormat audio, CancellationToken ct);          // v1, per session or per Matter
    Task ExportDocxAsync(string sessionId, string destPath, CancellationToken ct); // fast-follow
}

record AudioSegment(SourceKind Source, long StartMs, long EndMs, ReadOnlyMemory<float> Pcm);
record TranscriptSegment(int Seq, SourceKind Source, long StartMs, long EndMs,
                         string Text, string SpeakerLabel, string? SpeakerId);
```

---

## UI design

WPF, **tray + optional always-on-top overlay**, styled with **WPF-UI** (Windows 11 Fluent — Mica,
NavigationView, fluent controls) so it looks native with minimal hand-styling. Tray via
**H.NotifyIcon**; MVVM via **CommunityToolkit.Mvvm**; **Segoe Fluent Icons** (ships with Win11).
All MIT-licensed and ARM64-safe.

Surfaces:

- **Tray icon + flyout** — the **load-bearing consent** surface. Recording-state indicator
  (idle / recording / paused), current session at a glance, quick Start/Stop/Pause,
  and a menu to open the main window, session history, and settings.
- **Recording overlay** — a minimal, draggable, always-on-top pill (state dot + elapsed timer +
  Local/Remote "audio present" two-bar meter + Pause/Stop) that **supplements** the tray. Session
  name/participants off by default (opt-in, tooltip-only); **excluded from screen-capture** by
  default; never steals focus. Shown only during Recording/Paused.
- **Live transcript window** — a virtualized, auto-scrolling list of speaker-labelled
  segments updating in near-real-time (chat-like: `[mm:ss] Speaker: text`), with a
  lag/queue indicator and manual Pause. Backed by the `TranscriptMerger`'s observable
  segment collection.
- **Matter → Session manager** — a **two-level organizer** (Matter → its sessions) to
  browse / tag / rename / describe / export / archive. Sessions show title, app/medium, date,
  duration, diarised?; open any into a read view; rename, delete (whole-session), reveal-in-Explorer.
- **Metadata editor** — edits the user-owned `meta.json`: session name (`title`), participants
  (pick from the union of the session's matters' rosters or free-text; tag Local/Remote), medium,
  description, and matter tags (`matterIds[]`). Adding a participant inline creates them in the
  Matter roster.
- **Transcript correction (read view)** — **correction-only**, non-destructive: in-place text
  correction of a mis-transcribed segment and per-segment speaker reassignment, written to
  `edits.json` / pinned in `speakers.json`. No delete/hide/redact of content. Enabled only on
  finalized/recovered sessions.
- **Split-speakers dialog** — pick source(s) (Local / Remote / both), run diarisation (offered only
  where the declared count is many; declared count seeds cluster-K), then a small speaker-map editor
  to assign names to clusters; non-destructive re-render on apply, preserving manual pinned
  assignments.
- **Export** — a session/Matter picker driving the `.zip` self-contained archive (v1; FLAC default /
  WAV option) and the `.docx` transcript (fast-follow, Save-As).
- **Settings** — the surface listed below.

Optional later polish: a small live audio-level meter per source (ScottPlot /
LiveCharts2).

---

## Storage format

**Guiding principle: files are the source of truth.** Plain, readable files — not a
database — so they can be backed up, `grep`-ed, and re-rendered, with no lock-in.
Putting that root inside a sync folder (Dropbox/OneDrive/git) is **optional and
user-opt-in**, not a default virtue — and since it would push audio/transcripts off the
machine (against the "no audio leaving the machine" goal, L34), onboarding/settings
**warn when the chosen root resolves under a known sync provider**. A DB comes later
only as a *rebuildable search index*. At-rest encryption is explicitly deferred.

One folder per session, under a configurable root defaulting to
`%USERPROFILE%\LocalScribe\` (not `Documents\`, which is commonly OneDrive-redirected).
Matters live in a sibling `matters\` folder under the same root:

```
%USERPROFILE%\LocalScribe\
  matters\
     <matterId>.json      ← Matter record (name, reference, description, participant roster, vocabulary)
     matters-index.json   ← lightweight index for the Matter picker
  2026-07-02_1432_Webex_smith-v-jones\
     transcript.jsonl     ← source of truth, append-only, immutable
     edits.json           ← correction overlay (seq-keyed text corrections; non-destructive; NO tombstones)
     speakers.json        ← diarisation + name overrides + manual pinned assignments (non-destructive)
     session.json         ← SYSTEM-owned truth — schema v3 (times, app, model, backend, sources,
                            retainedAudioSources, counts, device snapshot, schemaVersion)
     meta.json            ← USER-owned truth (title, participants[], description, medium, matterIds,
                            summaryRef, edited flags, schemaVersion)
     session.txt          ← neutral human-readable metadata (opens in Notepad, no app)
     transcript.md        ← rendered view, regenerated from jsonl + speakers.json + edits.json
     summary.md           ← reserved placeholder (absent until generated; AI summary is a locked non-goal)
     audio\
        local.flac        ← retained per-source audio (enables Split-speakers); WAV a settings option
        remote.flac
```

- `transcript.jsonl` is written segment-by-segment and **never rewritten**
  (crash-safe append log).
- Diarisation and renaming touch only `speakers.json` (cluster→segment assignments +
  `label→name` map + manual `pinned` assignments); **text corrections and speaker
  reassignments** touch only `edits.json` / `speakers.json` — all **non-destructive**.
- `transcript.md` (and the neutral `session.txt`) is a pure *projection* of
  `JSONL + speakers.json + edits.json` (+ the vocabulary `heard→correct` pass), re-rendered
  whenever labels/edits change. `session.txt` is always regenerated so the folder stays
  portable/readable with no app.
- Session metadata splits: **`meta.json`** (user-owned) beside **`session.json` v3** (system-owned)
  — see decision 9. `title` lives in `meta.json`. Matter ↔ session is many-to-many via
  `meta.matterIds[]`.

```jsonl
{"seq":17,"source":"Remote","startMs":85320,"endMs":89110,"text":"I pushed the auth changes last night.","speakerLabel":"Them"}
```
```markdown
**[01:25] Alice:** I pushed the auth changes last night.
```
(`Alice` comes from the resolved participant roster / `speakers.json` after diarise + name; a
corrected line comes from `edits.json`; known mis-hearings are fixed by the vocabulary post-pass.
Consecutive same-speaker segments merge into a paragraph.)

**Audio retention (keep-by-default).** On-demand Split-speakers needs the audio to exist later, so
per-source **FLAC is retained by default** (lossless, ~half of WAV ≈ 55 MB/hr/source,
re-transcribable and diarisable). For a legal record the audio is primary evidence, so **the
default policy is now `keep` (never auto-delete)** and any deletion is an explicit **opt-in**. The
configurable policy is `keep | afterDiarisation | days:N | forever | never` (`forever` = legacy
synonym for `keep`); the auto-deleting variants remain
available for privacy-conscious non-legal users. Retention is **per-source** — cleanup removes one
source's audio at a time. `afterDiarisation` is **per-source and triggers on speaker-map
confirm/lock** (not the first diarise run), deleting only the just-confirmed source's audio;
sessions that are never diarised expire only under `days:N`. Under `never` (privacy-max), no audio
is kept and the **Split control is visibly disabled with a reason**; Split-speakers otherwise stays
available **indefinitely**. Audio format is **FLAC by default** with a **WAV** settings option for
maximum compatibility (archival/export); Opus (~10 MB/hr/source) is a future space-saver.

**Crash recovery.** A session folder lacking `endedAt` in `session.json` on next
launch is flagged "recovered" and its Markdown re-rendered from the existing JSONL
(+ any `speakers.json`/`edits.json`). Nothing is lost.

---

## Error handling & edge cases

Theme: **degrade gracefully, mark it in the transcript, never drop audio silently.**

**1. Capture resilience**
- **Pre-flight peak probe at Start** → capture ~1 s per source and assert a non-zero peak (reusing
  the Stage-1 SpikeRunner `localPeak`/`remotePeak`); warn + suggest a fix **before committing** so
  an un-repeatable jail-call never records silence.
- **Device hot-swap mid-call** (e.g. plugging in headphones when the call starts):
  subscribe to `IMMNotificationClient`, re-bind to the new default device, write an
  `[audio device changed]` marker. **This auto-rebind applies only in follow-default mode**; a
  **pinned** mic that vanishes falls back to the default **and writes a marker** — a pin is never
  silently rebound (decision 12).
- **Per-process loopback fails / all-zeros / browser audio** → automatic fallback to full-system
  EXCLUDE-self loopback, flagged **"degraded (system audio)"** with a `degraded: system-audio
  loopback` marker. Teams (`ms-teams.exe`) and browser calls take this path by design; explicit
  `perProcess` for the known all-zeros set **still** auto-falls-back (a legal recording must never
  produce a silent-empty `remote.flac`).
- **Speakers instead of headphones** → remote voices bleed from speakers into the Local
  mic. This is a **correctness** problem, not just muddiness: the bled remote audio can be
  transcribed a *second* time on the Local stream and mis-attributed as "Me" (phantom
  self-attribution). The meeting app's echo cancellation may not apply to our raw WASAPI
  capture, so it can't be relied on. Per-process loopback keeps Remote clean regardless;
  **recommend headphones**. v1 measures the bleed in **Stage 1**; **Stage 2** adds a
  non-destructive dedup at the `.md`-projection layer (JSONL keeps both copies).
- **Capture never blocks on transcription** — decoupled via the bounded queue; spill
  to disk rather than drop.

**2. Detection robustness** *(applies to the DEFERRED auto-detect fast-follow; v1 is manual-only and
sidesteps all of these.)*
- **False triggers** (notification "dings", warm sessions) → min-duration debounce +
  require sustained audio.
- **Ambiguous end** (hold music/lobby) → idle-timeout; manual Stop always wins.
- **Browser calls** (Google Meet, Slack huddle) — audio is `chrome.exe`, not cleanly
  auto-detectable → covered by manual Start + the system-mix Remote mode. In v1 **all**
  sessions start manually; the closed native set {Teams, Zoom, Webex} + toggles return with the
  detector fast-follow.

**3. Model & backend**
- **First run / model supply** → the **smallest Whisper default is bundled in the
  installer** (offline-capable first run). Every weight file is **SHA-pinned inside the
  signed binary** and verified after download. Larger Whisper models **and** the
  sherpa-onnx diarisation models (segmentation + embedding) are **lazy-downloaded** from
  upstream (HF `ggerganov/whisper.cpp`, `k2-fsa` sherpa-onnx) with progress + retry and a
  **manual-path fallback**; offline thereafter. Provenance + license vetting covers the
  diarisation models too: **all bundled/auto-fetched models must be Apache/MIT — reject
  CC-BY-NC / research-only.** An offline-first **Split** surfaces the existing
  `MODEL_DOWNLOAD_FAILED` + manual-model-path flow rather than dead-ending.
- **Backend cascade** CUDA → Vulkan → CPU; **VRAM OOM** on the 4–6 GB target →
  auto-downgrade model (e.g. small→base) or move to CPU, with a warning. Chosen
  backend logged + overridable.
- **Can't keep up (RTF > 1)** → growing queue detected → warn + optional
  auto-downgrade; transcript still completes, just trails.
- **Silence/noise hallucinations** ("Thank you", "[music]") → VAD-gating already
  suppresses most, plus a no-speech-probability threshold drops the rest.

**4. System lifecycle**
- **Sleep/resume** mid-call → `[paused: system sleep]` marker, auto-resume if the
  meeting's still live. The session clock keeps ticking through Pause/sleep
  (`durationMs = endedAt − startedAt`); the paused/resumed markers annotate the gap.
  (Because Pause stops capture, a lawyer can pause for a privileged sidebar and nothing is
  transcribed — the existing model already protects privilege.)
- **Crash** → recovery from append-only JSONL.
- **Disk full** → stop retaining audio, keep the transcript, warn.
- **Mic permission denied** → detect, prompt to enable in Windows Settings.

**5. Privacy & consent**
- **Always-visible recording indicator** (tray, load-bearing) — never capture silently; the
  overlay *supplements* it and is excluded from screen-capture by default.
- **First-run consent notice** + local-only storage + easy **Pause** and
  **delete-session**. The notice states **prominently that recording others is the
  user's legal responsibility** — consent law is **jurisdiction-dependent** and many
  regions are **two-party / all-party-consent**; the app makes recording state obvious
  but **can't enforce the law**. A lightweight **per-session start reminder** is a possible
  reinforcement.

**6. Diagnostics & logging**
- **Structured, rotating local log** with a **configurable level** (`info` default).
  **Transcript text is redacted by default** (`includeTranscriptText:false`) so logs
  stay shareable; **one-click export** bundles logs for bug reports. Logs are local-only,
  like everything else.

---

## Testing strategy

**Headline principle — the Humble Object pattern.** All hardware-, GPU-, and
OS-specific code (WASAPI interop, Whisper.net, sherpa-onnx) lives in razor-thin
adapters behind the interfaces above. **All logic sits in a deterministic core that
tests with zero hardware.** Testability was a design output, not an afterthought.

| Layer | Covers | Fakes / inputs |
|---|---|---|
| **Unit** (fast, every PR, no GPU) | Merger interleave order; Store JSONL→MD projection + crash-recovery + non-destructive relabel; **`edits.json` correction overlay + projection apply-order**; **vocabulary heard→correct pass**; **`meta.json`/`session.json` split (no writer race) + v2→v3 migration**; **Matter↔session tagging**; VAD boundary/cap logic; backend cascade + VRAM-OOM downgrade; retention policy (default `keep`); *(deferred detector: debounce + idle-timeout state machine, seam-tested when built)* | `FakeCaptureSource` (replays WAV), `FakeTranscriptionEngine` (scripted text), injected `IClock`, temp dirs |
| **Fixture integration** (opt-in, needs models, CPU-only) | Real VAD → segment boundaries; real Whisper → **WER** threshold vs reference; sherpa-onnx → **DER**/cluster-count on an N-speaker clip; silence clips → assert *no* hallucination | small audio corpus, in-repo or downloaded |
| **End-to-end (synthetic)** | Whole pipeline wired for real, fed a scripted two-stream `local.wav`+`remote.wav` "mock meeting" → assert `transcript.jsonl`/`.md` ≈ golden within fuzz | pre-recorded stream pair |
| **Manual matrix** (pre-release) | Real Webex 1:1 + group, Teams (system-mix), Zoom, browser manual-start, headphones vs speakers, device hot-swap + pinned-device vanish, sleep/resume, first-run download, CUDA→CPU fallback, overlay no-focus-steal + capture-exclusion | real devices/apps — documented checklist |

**Determinism controls:** inject `IClock` (no `DateTime.Now` in logic), temp
filesystem, **pin model version + quantization** so WER/DER thresholds stay stable,
fuzzy assertions (thresholds, keyword presence) for ML — never exact-string.

**Quality bar (regression baselines, not absolute targets):** WER/DER must stay
**≤ the first-measured baseline + ε** rather than chasing a fixed number; the one hard
absolute is **zero hallucination on silence**. The paired golden corpus is captured as a
**Stage-1 byproduct** (real two-stream recordings) plus **one synthetic TTS pair**, so
baselines exist before tuning starts. **Definition of Done (v1):** the owner sets the
ship bar **after the first Stage-2 measurement**, once real numbers exist. Low-confidence
diarisation **warns, never gates** — the structural Me/Them baseline is always
recoverable.

**Deliberately NOT automated:** real WASAPI capture and GPU backends need devices CI
can't provide → validated by the manual matrix, with adapters kept thin so almost no
logic hides in the untestable zone.

**CI shape:** PR gate = unit suite (fast, no models, no GPU; on Windows CI runners); nightly =
fixture + E2E (CPU, small model); manual matrix before releases. The deterministic
core is ideal for **test-first** development.

---

## Settings surface

- Storage root (default `%USERPROFILE%\LocalScribe\`; warn if it resolves under a sync
  provider — OneDrive/Dropbox/Google Drive)
- Audio retention policy (`keep` / after-diarisation / after-N-days / never; **default `keep`** —
  never auto-delete; the auto-deleting variants are an explicit opt-in)
- Export audio format (**FLAC default** / **WAV** for max compatibility)
- Model choice + backend override (auto / CUDA / Vulkan / CPU)
- Language (auto / fixed)
- **Self-identity `{name, role?}`** — auto-fills the Local "Me" participant (snapshotted per session)
- **Custom vocabulary — global legal dictionary** (per-Matter term lists live on the Matter)
- **Mic device — follow Communications default (default) or pinned device** (stores ID +
  friendly name; a vanished pin falls back to default + marker)
- **Remote capture mode `{auto | perProcess | systemMix}`** (+ optional app pin; Teams/browser
  always auto-fall-back to system-mix with a `degraded` marker)
- **Recording overlay `{enabled, showSessionName (default off), showLevelMeter,
  excludeFromCapture (default on)}`**
- **Auto-detect meetings — `autoDetect.enabled` defaults `false`; DEFERRED to an interface seam,
  no shipping detector in v1** (the native set {Teams, Zoom, Webex} + enable/disable toggles
  return with the detector fast-follow)
- **Launch at login (default on; disclosed at first run — registry Run key)**
- Logging level (default `info`; transcript text redacted by default)
- Recording indicator + manual-control hotkeys (the tray is the load-bearing indicator; the
  overlay supplements)

---

## v1 scope & build sequence

### In scope
Tray background app · dual-stream capture (mic Local + Remote `{auto|perProcess|systemMix}` —
Webex per-process default, Teams/browser system-mix fallback) · VAD-segmented near-real-time
transcription (Whisper.net, backend cascade) · **manual Start/Stop/Pause primary** (auto-detect
seam only) · structural Me/Them · **1-vs-many count gating Split** · on-demand Split-speakers
(sherpa-onnx, per-source, name assignment, non-destructive) · **legal Matter entity + two-level
Matter→Session manager + many-to-many tagging** · **Matter-scoped reusable participant rosters +
free-text + self-identity autofill** · **session metadata (name/participants/description/medium/
matterIds + null summary placeholder) with the `meta.json`/`session.json` v3 split** ·
**correction-only non-destructive transcript editing (`edits.json`)** · **custom vocabulary
(global + per-Matter, bias + correction)** · **device config (mic follow/pin + Remote mode) +
pre-flight peak probe** · **tray-supplementing recording overlay (screen-capture-excluded)** ·
files-as-truth storage + **keep-audio-by-default** retention · **neutral `session.txt` projection**
· **`.zip` self-contained archive export (per session/Matter)** · live view + session list ·
crash recovery · device-swap handling · recording indicator · first-run model download · settings
surface.

### Deferred (seam already exists)
**Meeting auto-detect (interface seam only)** · **`.docx` transcript export (fast-follow, OpenXML)**
· AI summaries (null placeholder only) · live/streaming diarisation · NPU/DirectML backend ·
word-by-word captions · Teams/Zoom name-scraping · cross-session acoustic voiceprints · segment
split/merge/insert/reorder edits · overlay hover-gated click-through + live low-energy watchdog ·
full-text search · cloud sync/sharing · macOS/Linux.

### Build sequence (walking skeleton — riskiest thing first, each stage demoable)

1. **Capture spike** — **DONE** (net10, hardware-validated): tray shell + the scary part —
   per-process WASAPI loopback (CsWin32) → two clean WAVs (mic + Webex/`CiscoCollabHost.exe`,
   direct-to-16 kHz via `AUTOCONVERTPCM`, `mode=DirectMono16k`), with system-mix fallback proven.
2. **Offline pipeline + entity schemas** — WAV → VAD → Whisper.net → JSONL/MD with Me/Them merge;
   land the schemas: `session.json` **v3** / `meta.json` / `matter.json` / `edits.json` /
   `settings.json` **v2**. *Most logic + unit tests land here.*
3. **Live wiring + recording overlay + live view** — real-time capture→pipeline, live view + the
   screen-capture-excluded overlay, bounded-queue backpressure, backend cascade.
4. **Manual controls + session lifecycle + Matter/session manager** — manual Start/Stop/Pause,
   session lifecycle, the two-level Matter→Session organizer, participant rosters + self-identity,
   and metadata editing. (Was "Trigger"; auto-detect removed to the seam.)
5. **Split-speakers** — sherpa-onnx, gated by the 1-vs-many count, per-source selection, name UI,
   non-destructive re-render preserving pinned assignments.
6. **Correction-edit overlay + vocabulary + `.zip` export** — `edits.json` correction editing,
   custom vocabulary (initial-prompt bias + heard→correct pass), and the `.zip` self-contained
   archive; **`.docx` transcript is the fast-follow**.
7. **Hardening** — pre-flight peak probe → live watchdog, device-swap, sleep/resume, disk-full,
   first-run UX, settings, consent notice, manual test matrix, unpackaged installer + signing
   (x64 + ARM64).
