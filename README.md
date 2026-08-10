# LocalScribe

**Local-first meeting transcription for Windows 11. Open-source. No cloud, no subscription, no network.**

LocalScribe runs quietly in the system tray, captures both sides of your online meetings — your
microphone *and* the other participants — and turns them into a single timestamped,
speaker-labelled transcript stored entirely on your machine. Transcription is powered by a locally
run [Whisper](https://github.com/ggerganov/whisper.cpp) model. Nothing is uploaded anywhere,
and that claim is checkable in one command (see [Privacy](#privacy)).

It is built for conversations that might later matter: the transcript is append-only and treated as
evidence, corrections are layered on top rather than overwriting it, exports carry full provenance,
and every session folder can be re-verified against a hash manifest.

> **Status: 0.9.0 — first packaged release.** Until now the only way to run LocalScribe was to
> build it from source. The installer is **not code-signed**, so Windows will warn you; see
> [Install](#install). It is 0.9.0 rather than 1.0 because this is the first time the app has been
> installed rather than run out of a build output.

## Why LocalScribe

- **Local and private** — audio and transcripts never leave your machine. There is no telemetry, no
  account, no auto-update, and no network code in the application at all.
- **No subscription** — runs on your own hardware with open-source models.
- **Us vs them, for free** — your mic and the meeting's audio are captured as *separate* streams, so
  "me" and "the remote side" are distinguished structurally, with no ML required. On-demand speaker
  splitting goes further, telling multiple people apart *within* one side.
- **Near-real-time** — text appears within a few seconds of each utterance.
- **Files are the truth** — every session is a self-contained folder of plain JSON, Markdown and
  audio you own. No database, no lock-in.
- **Built to be checked** — transcripts are never rewritten, exports state which engine and weights
  produced them, and audio hashes disclose exactly which spans are machine-generated silence.

## Install

Download **`LocalScribe-win-Setup.exe`** (~1.36 GB) from
[Releases](https://github.com/imnotwallace/LocalScribe/releases).

### This installer is not code-signed

Windows will show **"Windows protected your PC"** — click **More info**, then **Run anyway**. Your
browser may also warn that the file "isn't commonly downloaded". That is expected: a code-signing
certificate costs a few hundred pounds a year and this is an unpaid open-source project. The warning
is about provenance, not behaviour.

Rather than trusting the warning either way, verify the download:

```powershell
Get-FileHash -Algorithm SHA256 .\LocalScribe-win-Setup.exe
```

and compare against `SHA256SUMS.txt`, published alongside the release assets. If the hash matches you
have exactly the bytes the build produced. If it does not, do not run it.

Some managed corporate machines block unsigned executables outright, with no way to click past.

### What the installer does

- Installs **per-user** to `%LOCALAPPDATA%\LocalScribe`. **No administrator rights required.**
- Ships **self-contained** — you do not need the .NET runtime installed.
- Your recordings live in `%USERPROFILE%\LocalScribe` and settings in `%APPDATA%\LocalScribe`, both
  outside the install directory. Uninstalling never touches them.
- **No auto-update.** New versions are a manual download, on purpose.

### Bundled vs downloaded

The installer carries everything needed to record and transcribe immediately: ffmpeg (for importing
existing media), Whisper `tiny.en` and `base.en`, the two speaker-detection models, and voice-activity
detection.

Larger models are **not** bundled — the full library is around 12 GB and a GitHub release asset is
capped at 2 GB, so a fully offline installer is impossible on this channel. Open
**Settings > Components** to see what is installed and download what you want:

| Component | Download | Licence |
|---|---|---|
| Whisper `large-v3-turbo` | 1.62 GB | MIT |
| Whisper `large-v3-turbo` (q5_0) | 574 MB | MIT |
| Whisper `medium.en` | 1.53 GB | MIT |
| Whisper `medium.en` (q5_0) | 539 MB | MIT |
| Assistant model (Qwen3-4B-Instruct-2507 Q4_K_M) | 2.50 GB | Apache-2.0 |
| Semantic search (EmbeddingGemma-300m Q8_0) | 334 MB | **Gemma Terms of Use** |

All six total about **7.1 GB**; everything installed is roughly **9.7 GB** on disk. Each download is
verified against a pinned SHA-256 and **deleted if it does not match** — a model that downloaded wrong
would silently produce a worse transcript, which is not a failure you would ever notice. Interrupted
downloads resume rather than restarting. The licence is shown on each row before you press Download;
note the embedding model is under the Gemma Terms of Use, which is a use-restricted licence rather
than a standard open-source one.

Live transcription deliberately defaults to a smaller model than import does — live capture has to keep
up with realtime and import does not. The app tells you which model it used and offers
"re-transcribe at higher accuracy" for any session that turns out to matter.

### First run

A one-time consent notice explains what is recorded and where it is stored; accepting is required to
continue, and declining exits without saving anything. Recordings land under `%USERPROFILE%\LocalScribe`
by default — Settings lets you change the storage root, pick your microphone and models, and toggle
launch-at-sign-in. Note that changing the storage root **requires a restart and migrates nothing**:
existing sessions stay where they are.

## Build from source

```powershell
# 1. Fetch the models (Silero VAD, Whisper tiny/base/small.en, both diarisation models)
pwsh tools/fetch-models.ps1

# 2. Fetch ffmpeg - REQUIRED for importing anything but .wav
pwsh tools/fetch-ffmpeg.ps1

# 3. Build and run (tray-first: look for the tray icon, right-click for controls)
dotnet build LocalScribe.slnx
dotnet run --project src/LocalScribe.App
```

Optional model sets:

```powershell
pwsh tools/fetch-models.ps1 -LargeModels   # large-v3-turbo + medium.en - what Import defaults to
pwsh tools/fetch-models.ps1 -Assistant     # Qwen3-4B-Instruct-2507 (~2.5 GB), for the assistant
pwsh tools/fetch-models.ps1 -Embedding     # EmbeddingGemma-300m, for semantic search
```

`LOCALSCRIBE_MODELS`, `LOCALSCRIBE_FFMPEG`, `LOCALSCRIBE_ASSISTANT` and `LOCALSCRIBE_MCP` override where each component is
looked up, which is how a git worktree shares one 12 GB model library.

**Two helpers are not deployed automatically by a source build**, and the features that need them will
report a named reason until they are:

- `LocalScribe.Diarizer.exe` (Split Speakers, import-time speaker detection) must be published
  self-contained **single-file** and only that one exe copied beside the app — it carries its own ONNX
  Runtime and must not share one with the app.
- `LocalScribe.Assistant` (assistant, semantic search) must be a **folder** publish into `assistant\`,
  because LLamaSharp probes for its native runtimes relative to the app directory.

`tools/verify-diarizer.ps1`, `tools/verify-assistant-publish.ps1`, `tools/verify-mcp-publish.ps1` and
`tools/verify-import-models.ps1` check each layout. `./build.ps1` does all of this and packages the
installer (requires `dotnet tool install -g vpk`).

**Known build friction:** a running `LocalScribe.App.exe` locks `Core.dll` and the build fails with
MSB3027, which reads like a compile error and is not one. Close the app.

### Tests

```powershell
dotnet test LocalScribe.slnx --filter "Category!=Fixture"   # the model-free suite
dotnet test LocalScribe.slnx --filter "Category=Fixture"    # opt-in; needs models + private corpora
```

Around 2,500 headless tests cover the Core domain, app logic and the MCP server. Fixture-gated tests
exercise the real Whisper/VAD models, a private golden-audio corpus, a private multi-speaker
diarisation corpus (DER regression) and a real ffmpeg import; they need model files and provisioned
audio that are not in the repo, so they are opt-in and cannot run on a fresh clone.

CI builds and runs the model-free suite on every push and pull request; the fixture job is manual
dispatch only.

## What you can do

**Record**
- Start, pause and stop from the tray, the record console or the always-on-top overlay pill. Recording
  is always manual — nothing starts capturing behind your back.
- A colour-coded tray icon shows the recording state and cannot be turned off.
- Pick the remote target per session (auto-detect, a specific app, or the whole system mix), pin a
  microphone, and pre-tag Matters so their vocabulary biases the transcription.
- "Mute my side" (Ctrl+Shift+M) writes genuine silence into the record, bracketed by markers, so a
  privileged aside never enters it.
- An advisory toast offers to start recording when a call app starts using your mic. It never starts
  or stops anything itself.

**Stay recording**
- Watchdogs rebuild a dead audio stream, warn when a leg goes silent, warn below 1 GiB of disk, and
  refuse to start below 2 GiB rather than producing an unusable recording.
- Sleep, lid-close and log-off are handled explicitly: the session pauses and resumes, or stops and
  finalizes, and the gap is recorded rather than silently lost.
- An interrupted recording is finalized on the next launch, re-deriving which audio actually made it
  to disk.

**Import** — bring in recordings LocalScribe did not capture: WAV, FLAC, MP3, M4A, AAC, WMA, OGG and
common video containers (audio only). The original is archived byte-for-byte with its SHA-256, a
two-channel file can be split into "me"/"other party" legs, and you choose the model and language per
import.

**Browse and organise** — a sessions grid with search, filters and status chips; Matters that group
related sessions with a reusable participant roster and their own vocabulary; and metadata editing in
an explicit save/discard detail window.

**Read and correct** — a read view with audio playback (per-leg mute and volume), find, go-to-time,
synchronised follow-scroll, and per-segment seek. Corrections and splits are stored as an overlay:
`transcript.jsonl` is never rewritten, and a corrected turn is marked as such. Copy any turn with an
attributable citation.

**Separate speakers** — run diarisation on a finalized session, preview and name each detected voice by
playing a representative snippet, and confirm to write a non-destructive overlay. Manual assignments
are pinned and survive a later re-run. Optionally remember a voice, so future sessions *suggest* a name
— always as a suggestion you accept or dismiss, never an automatic identification.

**Re-transcribe** — run a session again at higher accuracy into a side-by-side version. Both versions
remain; the read view switches between them.

**Export** — `.docx`, `.md`, `.txt` or a `.zip` of the whole session folder. The Word output is built
for citation: one paragraph per turn, per-page restarting line numbers, a running header naming the
current speaker, and `(cont'd)` continuations. Every text export carries a metadata block with the
session id, the exporting build, the transcript version, the weights file, transcript and per-leg audio
SHA-256 hashes, a machine-generated-silence disclosure, human-edit counts, and a non-optional
disclaimer that the transcript was machine-generated. You can export a time-range excerpt, which snaps
outward to whole turns and labels itself as an excerpt on every page.

**Ask** — a local LLM assistant produces versioned session summaries and answers questions over a
transcript or across a Matter's summaries, with every claim cited to a timestamp and each citation
mechanically checked against the transcript. Failed citations are flagged in place rather than removed.
It runs entirely on your machine.

**Search** — keyword search across every session with matter, app and date facets, plus an optional
semantic "related discussion" section.

**Connect an MCP client** — an optional read-only [MCP](https://modelcontextprotocol.io) server exposes
six tools over your corpus to a client such as Claude Desktop. It is **off by default** and gated by
per-matter consent: absent or unreadable consent reads as disabled, and every call is written to an
append-only audit log.

**Verify** — re-hash a session against its manifest and get a per-file OK / CHANGED / MISSING verdict.
Sessions recorded before manifests existed report as unsealed, never as a pass.

## How it works

```
Your mic (Local) ─────┐                          ┌─→ overlay pill / record console
                      ├─ VAD → Whisper → merge ──┼─→ live transcript
App loopback (Remote) ┘      (by session clock)   └─→ session folder: transcript.jsonl (+ .md/.txt)
                                                       + manifest.json + local/remote audio
```

Two audio streams — your microphone and the meeting app's **per-process loopback** — are each sliced
into utterances by [Silero](https://github.com/snakers4/silero-vad) voice-activity detection,
transcribed locally by Whisper, and merged by timestamp into one interleaved transcript. Because
speaker attribution comes from *which stream* the audio arrived on, "me / them" labelling is structural
and free.

Per-process loopback isolates just the meeting app's audio. For apps where that is not reliable —
Microsoft Teams and every browser-based call (Chrome, Edge, Firefox, Brave, Opera, WebView2) —
LocalScribe falls back to capturing the whole system mix, writes a marker into the transcript and warns
you, because the remote file may then contain other applications' sound. Use headphones.

Both audio files are always padded to the full session length, so a leg that died five minutes into a
forty-minute call still *looks* forty minutes long. The transcript markers and the manifest's
machine-generated-silence ranges are what tell you otherwise — file length is never evidence of
continuous capture.

## Requirements

- **Windows 11, x64.** Remote capture needs Windows build 20348 or newer; below that there is no remote
  stream at all, not a degraded one. Only x64 is built — there is no ARM64 package.
- **Nothing else, for the installed build** — it is self-contained. Building from source needs the
  [.NET 10 SDK](https://dotnet.microsoft.com/download) (`net10.0-windows`).
- **A GPU is optional.** Whisper runs on CUDA (NVIDIA, when the first GPU reports ≥ 4 GB VRAM), Vulkan
  (other GPUs), or CPU, probed in that order. Picking a specific backend in Settings constrains what
  may load rather than merely labelling it — CPU stays available as a last resort so a vanished GPU
  driver can never cost you a recording — and takes effect on the next restart. Live transcription is capped at a small model regardless
  of card size, deliberately, so it keeps up with realtime — a bigger GPU does not raise the live
  ceiling, but import and re-transcription use larger models. The assistant uses CUDA when available
  without needing the CUDA toolkit installed, and falls back to CPU.
- **Disk.** Recording needs 2 GiB free to start and warns below 1 GiB. Audio costs roughly 230 MB/hour
  as WAV, about half that as FLAC. Audio is kept permanently — there is no auto-expiry.
- **ffmpeg** (bundled in the installer) for importing anything but `.wav`.

No minimum RAM is enforced anywhere in the code. The assistant is the heaviest component; on CPU it is
slow enough that the instruction set matters a great deal.

## Where your data lives

Everything is plain files under your storage root (default `%USERPROFILE%\LocalScribe`):

```
LocalScribe/
├─ sessions/
│  └─ 2026-08-07_1830_Webex_client-call/
│     ├─ session.json         system-owned facts (times, app, model, weights, devices, versions)
│     ├─ meta.json            your metadata (title, participants, Matter tags)
│     ├─ transcript.jsonl     append-only source of truth (never rewritten)
│     ├─ edits.json           non-destructive corrections + splits overlay
│     ├─ speakers.json        speaker assignments, names and pins (absent until used)
│     ├─ embeddings.json      derived voice vectors (excluded from every export)
│     ├─ manifest.json        SHA-256 seal + machine-generated-silence ranges
│     ├─ transcript.md/.txt   readable projections of the active version
│     ├─ session.txt          session-level metadata projection
│     ├─ local.flac           your microphone leg
│     ├─ remote.flac          the other party's leg
│     ├─ source/              imports only: the original file, byte-for-byte
│     ├─ assistant/           summaries.json + chats.json
│     └─ versions/            one folder per re-transcription
├─ matters/
│  ├─ matters.json            the Matter index (rebuildable)
│  └─ M-20260807-001/         matter.json + its own assistant/chats.json
├─ index/                     derived search caches - safe to delete
├─ mcp/                       consent.json + append-only audit log
├─ diagnostics/               one JSONL per month, transcript text redacted by default
└─ people/people.json         saved voiceprints (user data, individually deletable)
```

Settings live in `%APPDATA%\LocalScribe\settings.json`, outside the storage root.

The transcript is append-only and treated as evidence: corrections and speaker labels are layered on
top non-destructively, and the only deletion of session data is sending a whole session folder to the
Recycle Bin. Note that session folder names embed the session title, so they are not anonymous.

## Privacy

LocalScribe stores everything locally and uploads nothing — by default to a non-synced folder under
your user profile, and it warns if you point it at a cloud-synced location.

You do not have to take that on trust. From a clone of this repo:

```
git grep -nE "System\.Net|HttpClient|Socket|WebRequest|Dns" -- src/LocalScribe.App src/LocalScribe.Core
```

returns **nothing**. The two projects that make up the running application contain no network code at
all, and a test fails the build if that stops being true. Model downloads happen in a separate
executable (`LocalScribe.Fetch.exe`) started only when you press Download — which is the entire reason
it is a separate process rather than a class in the app.

A visible tray indicator shows when it is recording. The main window, record console, read views,
Session Details and Split Speakers are excluded from screen capture by default, so a shared screen
never leaks your transcripts. The diagnostic log excludes transcript text unless you explicitly turn
that on.

**Recording others is your responsibility.** Many jurisdictions require the consent of some or all
parties before a conversation may be recorded. LocalScribe makes the recording state obvious but cannot
enforce the law or obtain consent for you — disclosing the recording to the other participants is up to
you.

## Known limitations

Stated plainly, because most of these are deliberate trade-offs rather than bugs:

- **Unsigned installer** — SmartScreen warns everyone; some corporate machines block it outright.
- **Teams and browser calls** can only be captured as the whole system mix, so the remote leg may
  include other applications' audio.
- **Selection granularity is a whole turn.** You can copy a turn with or without a citation, but not
  select a phrase inside one.
- **No redacted export.** The master record is deliberately never edited destructively; a separate
  redacted copy is planned, not shipped.
- **Speaker detection quality depends heavily on recording conditions.** It assumes one speaker at a
  time within a leg, tops out at six voices, and re-running it drops the names of unpinned speakers —
  pin what you want to keep. Quality was tuned against a single private corpus.
- **Voiceprints are suggestions, never identification**, and their thresholds are still untuned against
  real-world audio.
- **The assistant reloads its model for every question** (a deliberate fix for an out-of-memory bug),
  answers Matter questions from summaries only, and validates citations heuristically. Exactly one chat
  model is supported.
- **The integrity manifest detects change; it does not prevent it.** It is plain JSON beside the data it
  seals — there is no key and no notarisation. Anyone who can alter a file can recompute it.
- **Live transcription is capped below import accuracy** by design; re-transcription is the remedy.
- Search is substring matching — no stemming, phrases or boolean syntax.
- Changing the storage root migrates nothing.
- Diagnostic and MCP audit logs are never pruned.

**Explicit non-goals:** cloud sync; deletion or redaction of transcript content; automatic audio
expiry; global system-wide hotkeys; and auto-starting or auto-stopping recording from detection.

## Tech stack

- **App:** WPF on `net10.0-windows`, with [WPF-UI](https://github.com/lepoco/wpfui) 4.0.3 (Fluent),
  [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) 2.3.0 (tray) and
  [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.0.
- **Capture:** NAudio 2.2.1 + [CsWin32](https://github.com/microsoft/CsWin32) for WASAPI per-process
  loopback.
- **Speech:** [Whisper.net](https://github.com/sandrohanea/whisper.net) 1.9.1 (CUDA / Vulkan / CPU) for
  transcription; Silero VAD v5 via [ONNX Runtime](https://onnxruntime.ai/) 1.22 for segmentation; FLAC
  via CUETools FLAKE 1.0.5.
- **Diarisation:** [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) 1.13.3 harvests
  pyannote-segmentation-3.0 boundaries and 3D-Speaker CAM++ embeddings; the clustering itself is
  LocalScribe's own deterministic weighted k-means with silhouette-based speaker counting. It runs in a
  separate process because it carries ONNX Runtime 1.24.4, which must never sit beside the app's 1.22.
- **Assistant & embeddings:** [LLamaSharp](https://github.com/SciSharp/LLamaSharp) 0.27.0 (CPU + CUDA 12)
  in an out-of-process helper, running Qwen3-4B-Instruct-2507 and EmbeddingGemma-300m.
- **Export:** DocumentFormat.OpenXml 3.5.1 — no Word installation needed.
- **MCP:** ModelContextProtocol 2.0.0-rc.1 over stdio.
- **Packaging:** [Velopack](https://github.com/velopack/velopack) 0.0.1298; ffmpeg from BtbN's LGPL
  shared build, SHA-pinned and redistributed with its licence.
- **Solution:** `LocalScribe.slnx` — `Core` (domain + pipeline), `App` (WPF), `Diarizer`, `Assistant`,
  `Mcp` and `Fetch` (the only project allowed to touch the network), three console runners, and three
  test projects.

Model licences: Whisper ggml **MIT**, pyannote-segmentation-3.0 **MIT**, 3D-Speaker CAM++
**Apache-2.0**, Qwen3-4B-Instruct-2507 **Apache-2.0**, EmbeddingGemma-300m **Gemma Terms of Use**,
FFmpeg **LGPL-3.0**.

## Documentation

- [Design](docs/plans/2026-06-30-localscribe-design.md) — the original architecture, storage format,
  v1 scope and staged build sequence.
- [Specifications](docs/specs/localscribe-specs.md) — the living cross-cutting reference: data schemas,
  state machines, model and VAD parameters, merge and render rules, settings, markers and error codes,
  storage layout, export and device configuration.

Design notes for everything built after the original staged sequence live in
[`docs/superpowers/specs/`](docs/superpowers/specs), with their implementation plans in
[`docs/superpowers/plans/`](docs/superpowers/plans):

| Area | Design note |
|---|---|
| Assistant deployment & provenance | [2026-07-23](docs/superpowers/specs/2026-07-23-assistant-deployment-provenance-design.md) |
| Assistant chat surfaces | [2026-07-24](docs/superpowers/specs/2026-07-24-assistant-chat-surfaces-design.md) |
| Import model picker | [2026-07-24](docs/superpowers/specs/2026-07-24-import-model-picker-design.md) |
| Semantic search | [2026-07-25](docs/superpowers/specs/2026-07-25-semantic-search-design.md) |
| Voice fingerprinting | [2026-07-25](docs/superpowers/specs/2026-07-25-voice-fingerprint-design.md) |
| MCP server | [2026-07-26](docs/superpowers/specs/2026-07-26-mcp-server-design.md) |
| Import-time diarisation | [2026-07-28](docs/superpowers/specs/2026-07-28-import-auto-diarisation-design.md) |
| In-house diarisation clustering | [2026-08-02](docs/superpowers/specs/2026-08-02-diarizer-inhouse-clustering-design.md) |
| Transcript export (document) | [2026-08-03](docs/superpowers/specs/2026-08-03-transcript-export-document-design.md) |
| Transcript export (scope dialog) | [2026-08-04](docs/superpowers/specs/2026-08-04-transcript-export-scope-dialog-design.md) |
| Tier 1 hardening (diagnosability, evidence loss, trustworthy output, reachability) | [2026-08-05](docs/superpowers/specs/2026-08-05-tier1-hardening-design.md) |
| Component acquisition & packaging | [2026-08-06](docs/superpowers/specs/2026-08-06-tier1d-component-acquisition-design.md) |

The staged build history (capture spike through corrections/vocabulary/export), its per-stage plans and
its manual smoke runbooks are in [`docs/plans/`](docs/plans). Release notes for the current version are
in [`docs/release-notes-0.9.0.md`](docs/release-notes-0.9.0.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). This is a hobby project with no warranty and no support
commitment, but issues are read.

## Licence

[MIT](LICENSE) © 2026 imnotwallace. Model weights and FFmpeg carry their own licences, listed above and
shown in-app before download.
