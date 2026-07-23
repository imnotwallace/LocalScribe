# LLM foundation + summaries (Steno round 6/7) - manual smoke runbook (user)

Everything below is a **real-model** run - the only kind this feature's automated tests
deliberately never do (Core/App tests use `FakeRunner`/scripted stubs throughout). Nothing here
is exercised by `dotnet test`; it needs an actual GGUF loaded by an actual
`LocalScribe.Assistant.exe` process.

## Prerequisites

**1. Fetch the model.**
```powershell
pwsh tools/fetch-models.ps1 -Assistant
```
Downloads the LOCKED default `Qwen3-4B-Instruct-2507-Q4_K_M.gguf` (~2.5 GB, Apache-2.0) into
`models\`, SHA-pinned from the Hugging Face LFS pointer and verified fail-closed, and writes
`models\assistant-manifest.json`. Run this and confirm `assistant-manifest.json` now lists the
default model before doing anything else.

> **Provisioned 2026-07-22/23.** The planned Hugging Face path was wrong and failed loudly at
> the pin step (never silently) - corrected in `tools/fetch-models.ps1`:
> - **Default 4B**: `Qwen/Qwen3-4B-Instruct-2507-GGUF` does not exist (Qwen publishes no
>   first-party GGUF of it; HF answers 401 for absent-or-private). Now
>   `lmstudio-community/Qwen3-4B-Instruct-2507-GGUF` - bartowski's quant of the real
>   `Qwen/Qwen3-4B-Instruct-2507`. **User-chosen** source, 2026-07-22.
>
> Provenance enforcement is unchanged - the file is still SHA-pinned from the LFS pointer and
> verified fail-closed.
>
> Optional models removed 2026-07-23: the engine's ChatML non-thinking wrapper is only correct
> for the locked default (1.7B thinks; Gemma is not ChatML). Already-downloaded optional GGUFs
> are inert and may be deleted by hand if the disk space matters.

**2. Publish and deploy the helper (FOLDER publish into an assistant\ subfolder - revised 2026-07-23).**
```powershell
dotnet publish src/LocalScribe.Assistant -c Release -r win-x64 --self-contained `
    -o src\LocalScribe.App\bin\Debug\net10.0-windows\assistant
pwsh tools/verify-assistant-publish.ps1 -PublishDir src\LocalScribe.App\bin\Debug\net10.0-windows\assistant
```
Single-file is IMPOSSIBLE for this helper (unlike Diarizer): LLamaSharp probes its
runtimes/<rid>/native/<variant>/ layout relative to the helper's own directory; self-extract
lands the natives where that probe never looks, and every request fails at NativeApi init.
The assistant\ subfolder keeps the helper's own onnxruntime.dll isolated from the App's.
The guard script MUST pass before smoking: a silent layout regression ships noavx, which
turns a ~2-minute summary into one that does not finish in 10.

## A - Settings and model availability
- A1 Settings > Assistant card: master toggle, and "Qwen3-4B-Instruct-2507" appears in the model
  picker once step 1 above has run.
- A2 Delete or rename `models\assistant-manifest.json` (or the GGUF file it points at): the card
  shows the fetch instructions and the model picker disables. Restore the file/model afterward.

## B - First summary + backend-report honesty (design 7.1/7.7)
- B1 On a CUDA-capable box, open a real session's Session Details > Assistant tab, hit
  Regenerate: phases stream (`load-cuda` / `prefill`), chunks stream live, the result renders
  under the locked label "AI-generated draft - not a transcript; verify against the record.",
  and the provenance line reads `(CUDA)`. `assistant\summaries.json` appears in that session's
  folder; transcript files (`transcript.jsonl`, `edits.json`, `session.json`, ...) are untouched -
  compare mtimes before/after.
- B2 **Backend-report honesty (locked, do not skip):** the `(CUDA)` / `(CPU)` badge shown in the
  tab must be the backend the helper **actually used** (`done.stats.backend` on the wire), never
  the backend that was merely *requested*. Force the dishonest case to fail on purpose: with
  `Settings backend = auto` on a box with no CUDA (or with CUDA hidden - e.g. disable/remove the
  GPU in Device Manager, or rename `nvcuda.dll`), Regenerate must still complete, and the
  provenance must read `(CPU)` - never `(CUDA)`. If it ever shows `(CUDA)` on a box that actually
  ran on the CPU floor, that is a real regression in the floor-fall provenance path, not a
  cosmetic bug.
- On a CUDA box (NVIDIA driver present, ~2.6 GB VRAM free): provenance line says CUDA; the
  helper stderr (if captured) contains `load_tensors: offloaded 37/37 layers to GPU`.
  Baseline: ~80s for a 1,145-token summary (4B Q4_K_M), ~13s of that model load.
- On a CPU-only box (or GPU busy): a "GPU unavailable - continuing on CPU" phase appears
  while generating, the provenance line says CPU and states the fall explicitly
  ("fell to CPU"). Baseline: ~112s (avx2). MINUTES-long with no progress = the noavx
  layout regression - re-run tools/verify-assistant-publish.ps1.
- Settings > Assistant shows the helper path when deployed, and the publish command when not.

## C - Stale + versions
- C1 Save a correction in the Read view for a session with an existing summary: the Assistant
  tab shows the stale badge on reload (this is `MaintenanceService.SessionContentChanged` ->
  `SummaryStore.MarkAllStaleAsync` - the App.xaml.cs wiring this task adds).
- C2 Regenerate: a second version appears in the version switcher; the old (now-stale) version
  is still selectable and its content still renders correctly.
- C3 Re-transcribe the session to a new version, then finalize a fresh recording (if applicable):
  both also flip the stale badge - these are the other two legs of the same trigger trio
  (`SessionFinalizeCompleted`, `RetranscriptionCompleted`), alongside the correction-save leg
  already covered by C1.

## D - Queued behind recording
- D1 Start a recording, then hit Regenerate on some other session's Assistant tab: a visible
  "Waiting for the recording to finish..." state appears, and **no** `LocalScribe.Assistant.exe`
  process spawns (check Task Manager). Stop the recording: the queued job proceeds by itself with
  no further user action.
- D2 Confirm the reverse never happens: starting a NEW recording while a summary job is running
  is never blocked or delayed by the assistant job (`AssistantGate` is probe-only and never
  extends `ExternalEngineBusy`).

## E - CPU floor + envelope
- E1 With `Settings backend = auto` and CUDA unavailable (see B2's forcing technique), a full run
  completes with provenance `(CPU)` and the UI shows the "may take several minutes" note. A ~1 h
  session should land inside the section-7.2 envelope (roughly 4-8 minutes on CPU).

## F - 2 h map-reduce path
- F1 A long session (2+ hours, or a re-transcribed concatenation of several) goes down the map
  path: progress reads `map i/N` then `reduce`. The finished result still carries all four
  locked section headers.

## G - Failure posture (design 7.7 - nothing persists on failure)
- G1 Rename the GGUF file mid-run: a visible error appears in the tab; `summaries.json` is
  unchanged (byte-for-byte - compare before/after) or (if this is the session's first-ever
  summary) still absent.
- G2 Kill `LocalScribe.Assistant.exe` mid-run from Task Manager: a visible "exited before
  completing" error appears; nothing is persisted to `summaries.json`.

## H - Export boundary (design 7.3 - zip includes, docx excludes)
- H1 Export a session as a `.zip`: the archive contains `assistant/summaries.json` alongside the
  transcript files, in its own clearly separate `assistant/` folder (automated pin:
  `SessionArchiverAssistantTests.Zip_includes_the_assistant_folder_automatically`).
- H2 Export the same session as a `.docx`: the document contains no assistant/summary content at
  all - it is the transcript, not the AI draft (true by construction: `DocxRenderer` only ever
  consumes the `Header`/`TextView`/`Rows` triple, and `SessionProjectionLoader` never reads
  `assistant\`; no summary text is even loaded into the export path to leak).

## I - UTF-8 wire round-trip
- I1 Use a session whose roster/title has non-ASCII content (an accented participant name, or a
  title/correction saved with a curly-quote/apostrophe pasted from Word - e.g. an
  `'`-typographic apostrophe or "smart quotes"). Regenerate a summary: the streamed result and
  the persisted `summaries.json` must render that text correctly, not as `?`, mojibake, or a
  replacement character. The stdio wire is JSON-lines UTF-8 end-to-end (request payload, `chunk`
  events, and the persisted markdown); this exercises that path with real non-ASCII bytes instead
  of the ASCII-only fixtures the automated tests use.
