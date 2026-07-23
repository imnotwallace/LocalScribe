# Assistant helper deployment + backend provenance - design (2026-07-23)

## Problem

The local-assistant feature (Steno round 7/7, merged @ `a545310`) shipped with a helper that
cannot run, a GPU path that has never worked on any box, and a provenance field that reports the
wrong answer. None of it was caught, because every automated test uses `FakeRunner` or a scripted
stub - no test has ever loaded a GGUF.

Found by real-model runs on 2026-07-22/23 (i7-9750H, GTX 1650 4 GB, 15.8 GB RAM):

1. **The deployed helper is non-functional.** The single-file `LocalScribe.Assistant.exe` fails
   every request with `JOB_FAILED: The type initializer for 'LLama.Native.NativeApi' threw an
   exception`. `IncludeNativeLibrariesForSelfExtract` extracts natives to
   `%TEMP%\.net\LocalScribe.Assistant\<hash>\runtimes\...`, but LLamaSharp does its OWN probing
   relative to the app directory rather than relying on the OS loader (unlike sherpa/Diarizer,
   whose single-file precedent this copied). Every Assistant UI action would have failed.

2. **CUDA has never been shippable.** `LLamaSharp.Backend.Cuda12.Windows` ships a complete
   `cuda12/` native set (`llama.dll`, `ggml.dll`, `ggml-base.dll`, `mtmd.dll`, `ggml-cuda.dll`),
   but four of those filenames collide with the CPU backend's. NuGet conflict resolution keeps the
   CPU copies, leaving an orphaned 288 MB `ggml-cuda.dll` with no CUDA `llama.dll` to load it.

3. **The backend provenance lies.** `LlamaEngine.Load` reports `"cuda"` whenever
   `LLamaWeights.LoadFromFile` does not throw. With no CUDA backend registered llama.cpp does not
   throw - it silently assigns every layer to CPU. Three real runs all reported `backend=cuda`;
   two were 100% CPU (`backend_ptrs.size() = 1`, `dev = CPU`). This violates design 7.7 ("CUDA
   fall to CPU is recorded, never silent") and it is the value that reaches summary provenance.

4. **The deployed CPU backend is `noavx`.** A RID-specific publish flattens
   `runtimes/<rid>/native/**` to the output root; the four CPU variants collapse and `noavx` wins
   alphabetically. Measured on a 1,145-token summary: `noavx` did not finish in 600 s; `avx2`
   finished in 111.6 s.

5. **The engine can only correctly prompt one model.** `LlamaEngine.InferAsync` hardcodes the
   ChatML template and assumes a non-thinking model. Only `Qwen3-4B-Instruct-2507` satisfies both.

Measured baseline, same 1,145-token prompt, 4B Q4_K_M:

| Config | Result |
| --- | --- |
| CUDA, 37/37 layers offloaded | 79.9 s (13.2 s load) |
| CPU `avx2` | 111.6 s (13.7 s load) |
| CPU `noavx` (what ships today) | did not finish in 600 s |

## Goals

- The helper runs when deployed, on CPU and on CUDA.
- The reported backend is what actually ran; a CUDA-to-CPU fall is recorded.
- A broken deployment fails loudly and visibly, not on first use.
- Only models the engine can correctly prompt are offered.

## Non-goals

- Changing the Diarizer's single-file deployment. It works; only this helper diverges.
- Multi-model template support. Deferred until a second model is actually wanted (see Models).
- Making the assistant fast. GPU is a bonus; CPU `avx2` is the supported floor.

## Design

### 1. Publish shape

Rename the csproj target `_PreserveLlamaCppNativeLayoutForSingleFile` to
`_PreserveLlamaCppNativeLayout` and drop its `Condition="'$(PublishSingleFile)' == 'true'"`.

The target already rewrites each llama.cpp native's `RelativePath` back to
`runtimes/<rid>/native/<variant>/`. Ungated, it also fixes the folder publish, whose flattening
causes both NETSDK1152 and the `noavx` pick. Result: `avx`, `avx2`, `avx512`, `noavx` and `cuda12`
all keep distinct paths, and LLamaSharp's loader probes them - its own supported layout - selecting
the CPU variant by runtime CPU detection and finding a complete CUDA set.

Publish command (replaces the single-file command in the runbook):

```powershell
dotnet publish src/LocalScribe.Assistant -c Release -r win-x64 --self-contained `
    -o src\LocalScribe.App\bin\Debug\net10.0-windows\assistant
```

### 2. Layout

The helper lives in an `assistant\` SUBFOLDER beside the app:

```
LocalScribe.App.exe
LocalScribe.Diarizer.exe          <- unchanged, still single-file
assistant\
  LocalScribe.Assistant.exe
  onnxruntime.dll                 <- its own copy, isolated by the folder
  runtimes\win-x64\native\
    avx\ avx2\ avx512\ noavx\
    cuda12\
```

The separate directory is what makes a folder deployment safe: the helper carries its own
`onnxruntime.dll` (12.4 MB, via the Core ProjectReference), and in a subfolder it can never
overwrite the App's. This is the same isolation goal as the Diarizer's single-file rule, reached by
a different means - and the reason for the divergence (LLamaSharp probes for its own natives;
sherpa relies on the OS loader) must be stated in both the csproj and `CompositionRoot` comments,
which currently say the opposite.

### 3. Locator

New `AssistantHelperLocator` in `src/LocalScribe.Core/Assistant/`, mirroring `FfmpegLocator`:

- `LOCALSCRIBE_ASSISTANT` env var -> a folder containing `LocalScribe.Assistant.exe`
- else `assistant\LocalScribe.Assistant.exe` beside the binary
- else repo-root dev fallback `tools\assistant\`, found by walking up to `LocalScribe.slnx`
- returns `null` when absent, with a `MissingMessage` naming the publish command

`CompositionRoot.cs:138` uses the locator instead of `Path.Combine(AppContext.BaseDirectory, ...)`.

### 4. Availability

Assistant availability becomes **model installed AND helper present**. `DisabledExplainer`
distinguishes them:

- no model -> the existing `NoAssistantModelsNote` (run `fetch-models.ps1 -Assistant`)
- no helper -> `AssistantHelperLocator.MissingMessage` (run the publish step)
- neither -> both, so one fix does not hide the other

Today a missing helper is indistinguishable from a working one until a request fails, which is
exactly how this shipped broken.

### 5. Backend provenance

Install a `NativeLogConfig.llama_log_set` callback at helper startup, before any load, capturing
llama.cpp's log into a thread-safe buffer reset per load. The callback fires from native threads.
It must keep writing to stderr and NEVER to stdout, which is the wire.

Parse the authoritative line llama.cpp emits during `load_tensors`:

```
load_tensors: offloaded 37/37 layers to GPU   -> full GPU
load_tensors: offloaded 22/37 layers to GPU   -> partial
(line absent)                                 -> no GPU
```

Rule: `Backend = "cuda"` iff `offloaded == total && total > 0`; otherwise `"cpu"`. A mixed run is
not a GPU run. The parse is a pure `string -> (offloaded, total)?` function, unit-testable.

Behaviour, by request:

| Request | Full GPU | Not full GPU |
| --- | --- | --- |
| `auto` | `backend=cuda` | emit `cuda-fell-to-cpu` progress event, `backend=cpu` |
| `cuda` | `backend=cuda` | **throw** - honors the documented "GPU or throw" contract |
| `cpu` | n/a | `backend=cpu` |

No reload on a fall: the loaded context is fine, only the label was wrong. `AssistantDone.Backend`
keeps its shape and type - the locked wire contract is unchanged, the field simply stops lying.

### 6. Models

`LlamaEngine.InferAsync` hardcodes ChatML (`<|im_start|>`) and assumes a non-thinking model.
Verified against both optional models:

- **Qwen3-1.7B** is a thinking model. It emitted `<think>` and consumed its entire 64-token budget
  reasoning, returning no answer. `LlamaEngine`'s comment ("Qwen3-*-Instruct-2507 is a NON-thinking
  model") is true for the 4B default but not for this one, which is not an Instruct-2507.
- **Gemma-4-E2B** does not use ChatML at all; it expects `<start_of_turn>`. Prompting it through the
  ChatML wrapper is incorrect.

**Decision: drop both optional models** from `fetch-models.ps1`, the manifest, and the picker,
leaving the locked default alone. This matches the existing "default LOCKED, no bake-off" decision.

The two GGUF files (4.6 GB) then sit unused in `models\`. Deleting them is optional cleanup and
must be confirmed by the user at the time - the implementation removes them from the manifest and
the picker, and does not delete downloaded weights on its own.

If a second model is wanted later, the path is per-model metadata in `assistant-manifest.json`
(template kind + thinking flag) with `LlamaEngine` selecting the wrapper - deliberately deferred as
YAGNI until there is a reason to want one.

### 7. UI

- The Assistant tab's provenance line shows the true backend, and states the fall explicitly when
  one occurred, so a silent CPU run is visible without reading logs.
- The Settings Assistant card reports helper-present/absent separately from model-installed.

## Testing

Every existing assistant test uses fakes, which is why all five defects shipped. The new guards
target the real seams:

- **Offload parser** - unit tests over the pure parse function, using the two real captured
  llama.cpp logs (CUDA full-offload and CPU) committed as fixtures.
- **Fall behaviour** - `auto` not-full-GPU emits the event and reports `cpu`; explicit `cuda`
  not-full-GPU throws.
- **`AssistantHelperLocator`** - tests mirroring the `FfmpegLocator` ones: env override, beside-binary,
  repo-root fallback, null + `MissingMessage` when absent.
- **Availability** - model-only, helper-only, neither, both.
- **Layout guard** - `tools/verify-assistant-publish.ps1` asserts a complete `cuda12/` set and all
  four CPU variants in a published tree; the pure check logic is unit-tested against a fake tree,
  and the runbook calls the script after publishing.
- **Real-model smoke** stays manual (runbook section B), now with a concrete A/B: a CUDA box reports
  `cuda` with 37/37, a CPU-only box reports `cpu` plus the fall event.

## Docs to update

- `docs/plans/2026-07-19-llm-foundation-summaries-smoke-runbook.md` - publish + deploy steps, the
  optional-model section, and the section-B expectations.
- `CompositionRoot.cs:133-138` - the comment says the helper resolves "beside the app exactly like
  Diarizer"; it no longer does, and the reason for the divergence belongs here.
- `LocalScribe.Assistant.csproj` - the single-file rationale, now describing a folder publish.
- `LlamaEngine.cs:74` - the non-thinking-model comment, scoped to the locked default.

## Risks

- **VRAM headroom is thin.** The 4B fully offloads into 4 GB with roughly 100 MB spare
  (2,375 MiB weights + 306 MiB KV + 302 MiB compute). Another GPU consumer can push it to partial
  offload, which now correctly reports `cpu` rather than failing - degradation is visible, not silent.
- **Folder size.** ~700 MB deployed instead of one 441 MB exe, of which 288 MB is `ggml-cuda.dll`.
  A CPU-only build option remains available if packaging size later matters.
- **The ungated csproj target is load-bearing.** If it regresses, the publish silently reverts to a
  flattened `noavx` layout. This is exactly what the layout guard script exists to catch.
