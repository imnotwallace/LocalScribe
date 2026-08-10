# LocalScribe 0.9.0

Draft for the GitHub release body. Paste into the release UI; keep the SHA-256 block in sync with
the generated `SHA256SUMS.txt` (build.ps1 writes it - do not transcribe hashes by hand).

---

Local-first meeting transcription for Windows 11. Your microphone and the meeting's audio are
captured as separate streams and turned into a single timestamped, speaker-labelled transcript
stored entirely on your machine.

This is the first packaged release. Until now the only way to run LocalScribe was to build it
from source.

## Before you download: this installer is not code-signed

Windows will warn you. You will see **"Windows protected your PC"** - click **More info**, then
**Run anyway**. Your browser may also warn that the file "isn't commonly downloaded".

That is expected, and I would rather say so up front than let you wonder. This is an unpaid
open-source hobby project and a code-signing certificate costs a few hundred pounds a year, so
the release is unsigned. Nothing about the app is degraded by that - the warning is about
provenance, not behaviour.

**What to do instead of trusting the warning:** verify the download yourself.

```powershell
Get-FileHash -Algorithm SHA256 .\LocalScribe-win-Setup.exe
```

and compare against `SHA256SUMS.txt`, published with the release assets. On Linux/WSL:

```
sha256sum -c SHA256SUMS.txt
```

If the hash matches, you have exactly the bytes this build produced. If it does not, do not run
it - tell me.

If you are on a managed work machine, be aware that some corporate policies block unsigned
executables outright, with no way to click past. Nothing I can do about that from this side.

## What you get

- **Installs per-user** to `%LOCALAPPDATA%\LocalScribe`. **No administrator rights required.**
- **No uninstaller surprises** - your recordings live in `%USERPROFILE%\LocalScribe` and settings
  in `%APPDATA%\LocalScribe`, both outside the install directory. Removing the app never touches
  them.
- Windows 11, x64.

## Download size, and what happens after

The installer is **~1.36 GB**. It carries everything needed to record and transcribe immediately:

- ffmpeg (for importing existing audio/video files)
- Whisper `tiny.en` and `base.en`, for live transcription
- the speaker-detection models
- voice-activity detection

**Larger models are not bundled and are fetched on demand.** The full model library is about
12 GB, and a GitHub release asset is capped at 2 GB, so bundling everything is not possible even
in principle. Open **Settings -> Components** to see what is installed and download what you
want:

| Component | Size | Licence |
|---|---|---|
| Whisper `large-v3-turbo` | 1.62 GB | MIT |
| Whisper `large-v3-turbo` (q5_0) | 574 MB | MIT |
| Whisper `medium.en` | 1.53 GB | MIT |
| Whisper `medium.en` (q5_0) | 539 MB | MIT |
| Assistant model (Qwen3-4B-Instruct-2507) | 2.50 GB | Apache-2.0 |
| Semantic search (EmbeddingGemma-300m) | 334 MB | **Gemma Terms of Use** |

All six total about 7.1 GB; with everything downloaded the install is roughly 9.7 GB on disk.

Each download is verified against a pinned SHA-256 and **deleted if it does not match** - a model
that downloaded wrong would silently produce a worse transcript, which is not a failure you would
ever notice. Interrupted downloads resume rather than starting over. The licence is shown on each
row before you press Download; note the embedding model is under the Gemma Terms of Use, not a
standard open-source licence.

Live transcription defaults to a smaller model than import does. That is deliberate - live
capture has to keep up with realtime, import does not - and the app tells you which model it used
and offers "re-transcribe at higher accuracy" for any session that matters.

## The privacy claim, and how to check it yourself

LocalScribe does not talk to the network. Not for telemetry, not for updates, not for
transcription.

You do not have to take my word for it. From a clone of this repo:

```
git grep -nE "System\.Net|HttpClient|Socket|WebRequest|Dns" -- src/LocalScribe.App src/LocalScribe.Core
```

That returns **nothing**. The two projects that make up the running application contain no
network code at all, and a test in the suite fails the build if that ever stops being true.

Model downloads happen in a **separate executable** (`LocalScribe.Fetch.exe`) that is started only
when you press Download and does one job per run. That is the entire reason it is a separate
process rather than a class in the app - it keeps the grep above honest.

There is no auto-update. New versions are a manual download, on purpose.

## Why 0.9.0 and not 1.0

Because this is the first time the app has been installed rather than run from a build output,
and I would rather find out what that breaks before calling it 1.0.

Known gaps in this release:

- **Unsigned** (see above).
- You can copy a whole turn from a transcript, with or without an attributable citation, but not
  select a phrase *inside* a turn. Selection granularity is the turn.
- No redacted-disclosure export. The master record is deliberately never edited destructively;
  a separate redacted copy is a planned feature, not a shipped one.
- Speaker detection quality depends heavily on recording conditions.

## Licence

The application is MIT. Model weights carry their own licences, listed above and shown in-app
before download.

---

*Found a bug? Open an issue. This is a hobby project - no warranty, no support commitment, but I
do read them.*
