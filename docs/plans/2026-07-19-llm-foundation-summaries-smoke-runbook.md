# LLM foundation + summaries (Steno round 6/7) - manual smoke runbook (user)

Everything below is a **real-model** run - the only kind this feature's automated tests
deliberately never do (Core/App tests use `FakeRunner`/scripted stubs throughout). Nothing here
is exercised by `dotnet test`; it needs an actual GGUF loaded by an actual
`LocalScribe.Assistant.exe` process.

## Prerequisites

**1. Fetch the model(s) - run the two switches SEPARATELY, not combined.**
```powershell
pwsh tools/fetch-models.ps1 -Assistant
```
Downloads the LOCKED default `Qwen3-4B-Instruct-2507-Q4_K_M.gguf` (~2.5 GB, Apache-2.0) into
`models\`, SHA-pinned from the Hugging Face LFS pointer and verified fail-closed, and writes
`models\assistant-manifest.json`. Run this one first, on its own, and confirm
`assistant-manifest.json` now lists the default model before doing anything else.

```powershell
pwsh tools/fetch-models.ps1 -AssistantOptional
```
Adds `Qwen3-1.7B-Instruct` (q4_K_M, ~1 GB) and `Gemma-4-E2B-QAT`. Run this **separately**, only
after the `-Assistant` run above has already succeeded. Reason: `fetch-models.ps1`'s assistant
block only writes `assistant-manifest.json` once, after its whole model loop completes: if an
optional entry's Hugging Face pointer 404s or the URL has drifted, the script throws mid-loop and
the manifest write is skipped **entirely** for that run - including the default model's entry,
even though its multi-GB blob may have already downloaded and SHA-verified fine. Running
`-Assistant` alone first locks in a working manifest with just the default model; a subsequent
failed `-AssistantOptional` run then only fails to add the extras - it can no longer take the
already-verified default down with it.

**2. Publish and deploy the helper (single file, self-contained, both backends in the one exe).**
```powershell
dotnet publish src/LocalScribe.Assistant -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o C:\temp\assistant-publish
Copy-Item C:\temp\assistant-publish\LocalScribe.Assistant.exe `
    src\LocalScribe.App\bin\Debug\net10.0-windows\LocalScribe.Assistant.exe
```
Copy **only** the `.exe` - never the publish folder (same native-DLL collision rule as the
Diarizer helper: a loose-folder copy would overwrite the app's own `onnxruntime.dll`/CUDA
runtime with an incompatible build). The resulting single-file exe bundles LLamaSharp's managed
and native (CPU + CUDA) backends together - **it is the same one exe for every box**, roughly
**441 MB** on disk. That size is a real installer-size cost worth keeping in mind: LocalScribe's
existing footprint (Diarizer + whisper/diarisation models) is far smaller, and this helper alone
is now the single largest packaged artifact. No separate CPU-only / CUDA-only build exists or is
planned - backend selection is runtime ("auto" tries CUDA, falls to CPU), not build-time.

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
