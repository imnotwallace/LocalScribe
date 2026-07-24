# Import-time Whisper model picker + bundled large models — design

Date: 2026-07-24
Status: approved (brainstorming), ready for implementation plan
Author: pairing session

## Problem

Audio import currently transcribes with whatever the **global** `Settings.Model` is
(default `auto`, which tops out at `small.en`). Because an import is not real-time, the
user has time to spend on quality and wants to deliberately choose a larger, higher-quality
Whisper model for a given import — without changing the global setting or slowing down live
recording. Today there is no import-specific model choice anywhere in the chain, and the
large models are neither recognised in the default ladder nor packaged.

## Goal

1. Add a **per-import model picker** (and a paired language picker) to the Import dialog.
2. **Package** larger, higher-quality models with the app so the picker has real choices
   offline from day one.

## Decisions (locked during brainstorming 2026-07-24)

- **Models offered:** `large-v3-turbo` and `medium.en`, plus `small.en` (already on disk).
  The picker lists whatever `ModelPaths.AvailableModels()` finds — canonical names — so any
  present model shows up with no code change.
  - Rejected: `large-v3` (not chosen), multilingual `medium` (not bundled).
- **Delivery:** **bundled inside the installer** — weights present offline from day one, no
  network needed. (`ModelPaths.ModelsRoot` finds a `models\` folder beside the binary in an
  installed app.)
- **Precision:** ship **both f16 and q5_0** for turbo and medium.en (~4.2–4.4 GB total). The
  per-backend `ModelFileResolver` then loads f16 on CUDA and q5_0 on CPU/Vulkan automatically —
  each backend gets its ideal file.
- **Scope:** **import-only** per-run override via `settings with { Model, Language }` (the
  proven `RetranscriptionRunner` pattern). Global settings and live recording are untouched.
  The picker **defaults to `large-v3-turbo`** so imports get top quality by default.
- **Language:** a language picker in the dialog, **default auto-detect (`"auto"`)**.
- **Packaging scope THIS round:** acquisition (`fetch-models.ps1 -LargeModels`) + publish/
  layout-guard wiring. A full MSI/setup installer remains **Stage 7** and is out of scope here.

## Verified code facts this design relies on

These were confirmed by reading the source (file:line), not assumed:

- **`BackendSelector.Select` (`src/LocalScribe.Core/Transcription/BackendSelector.cs:36-61`):**
  an explicit `settings.Model != "auto"` is canonicalised and **always wins** over the auto
  ladder; unknown/version names (e.g. `large-v3-turbo`) pass through verbatim. Line 57:
  `bool english = settings.Language is "en" or "auto";` — so **language `"auto"` counts as
  English** for the `.en`→multilingual strip. Consequences:
  - `large-v3-turbo` + `auto`: turbo has no `.en` suffix → stays multilingual → whisper
    auto-detects. **The default combo works.**
  - `medium.en` + `auto`: no strip → stays `medium.en` (English) → present → gate passes.
  - `medium.en` + explicit non-English: strips `medium.en → medium`, which we do **not**
    bundle → hits the presence gate (correct refusal; route non-English to turbo).
- **`RetranscriptionRunner` (`src/LocalScribe.Core/Retranscription/RetranscriptionRunner.cs:140-153`):**
  the exact override+gate pattern to copy —
  `BackendSelector.Select(hw, settings with { Model, Language }, available)` then
  `if (!available.Contains(plan.ModelName)) Notice("...not downloaded...")`.
- **`ModelLadder` (`src/LocalScribe.Core/Transcription/ModelLadder.cs`):** `Downgrade` returns
  `null` for unknown stems and `HasEnglishVariant` returns `false` for unknown stems — both
  correct defaults for turbo.
- **`TranscriptionWorker.DowngradeAsync` (`src/LocalScribe.Core/Transcription/TranscriptionWorker.cs:190-198`):**
  `Downgrade("large-v3-turbo")` → `null` → takes the `_plan with { Backend = Cpu }` branch →
  recreates on CPU with turbo. So a VRAM-OOM on turbo **floor-falls to CPU cleanly** (CPU then
  loads the bundled q5_0). The English language-lock swap
  (`TranscriptionWorker.cs:164`) is gated on `HasEnglishVariant`, which is `false` for turbo,
  so it never looks for a nonexistent `ggml-large-v3-turbo.en.bin`.
  **⇒ `ModelLadder`, `BackendSelector`, and the model enums need NO changes.**
- **`ModelFileResolver` bijection:** turbo's canonical name has no quant suffix (idempotent);
  `ggml-large-v3-turbo-q5_0.bin` and `ggml-medium.en-q5_0.bin` collapse to `large-v3-turbo`
  and `medium.en`. CUDA candidate order is f16-first, CPU/Vulkan is quantized-first, so
  bundling both f16+q5_0 gives each backend its preferred file.
- **Wiring:** `App.xaml.cs:545` constructs `ImportDialogViewModel(...)`; `App.xaml.cs:303`
  already passes `ModelPaths.AvailableModels` to `RetranscribeDialogViewModel` — the same
  argument the import VM will take.

## Design

### 1. UX — two combo boxes in the existing Import dialog

Mirror the Re-transcribe dialog. In `ImportDialog.xaml`, below the title / recorded-date
fields (and above or beside the matter picker), add:

- **Model** combo bound to `ModelChoices` / `SelectedModel`.
- **Language** combo bound to `LanguageChoices` (`LanguageChoice.All`) / `Language`.

Both are disabled while `IsBusy` (transcription in flight). No new window — same
`ImportDialog` plain `Window`.

Defaults: `SelectedModel = "large-v3-turbo"` if present, else `"medium.en"` if present, else
`ModelChoices.FirstOrDefault()`. `Language = "auto"`.

### 2. Data flow — thread the pick through (additive)

- **`ImportRequest`** (`src/LocalScribe.Core/Import/AudioImporter.cs:14`) gains two nullable
  fields:
  ```csharp
  public string? Model { get; init; }      // null => fall back to global Settings.Model
  public string? Language { get; init; }   // null => fall back to global Settings.Language
  ```
  Nullable + defaulted so existing callers/tests that build an `ImportRequest` without them
  compile and behave exactly as before.

- **`ImportDialogViewModel`** (`src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs`):
  - New ctor param `Func<IReadOnlySet<string>> availableModels` (mirrors Retranscribe).
  - New members: `IReadOnlyList<string> ModelChoices` (from `availableModels().OrderBy(...)`),
    `IReadOnlyList<LanguageChoice> LanguageChoices = LanguageChoice.All`,
    `[ObservableProperty] string? _selectedModel`, `[ObservableProperty] string _language = "auto"`.
  - `StartAsync` sets `Model = SelectedModel` and `Language = Language` on the built
    `ImportRequest`. The `ImportRunner` delegate signature is **unchanged** (it already carries
    the whole `ImportRequest`).

- **`AudioImporter.ImportAsync`** (`src/LocalScribe.Core/Import/AudioImporter.cs:60`):
  - Compute an effective settings snapshot once:
    ```csharp
    var runSettings = _settings with {
        Model    = request.Model    ?? _settings.Model,
        Language = request.Language ?? _settings.Language,
    };
    ```
  - Pass **`runSettings`** (not `_settings`) into the `OfflinePipelineRunner` ctor
    (`AudioImporter.cs:137`). The runner's own `BackendSelector.Select` then resolves the
    chosen model. Nothing else in the runner changes.

- **`App.xaml.cs` `openImport`** (`App.xaml.cs:545`): add `ModelPaths.AvailableModels` to the
  `ImportDialogViewModel` ctor call (the argument `openRetranscribe` already passes).

### 3. Model resolution — no ladder/enum changes

Because the explicit override passes the chosen name through verbatim and the resolver's
bijection guarantees a present file loads, `large-v3-turbo` and `medium.en` become selectable
the moment their `ggml-*.bin` files are on disk. Turbo's OOM floor-fall and language-lock are
handled by the defensive `null`/`false` defaults verified above. `ModelLadder`,
`BackendSelector`, and the model enums are **untouched**.

### 4. Presence gate + error handling

Add a fail-fast gate at the **top** of `AudioImporter.ImportAsync`, before any folder / copy /
decode work:

```csharp
var available = ModelPaths.AvailableModels();
var (plan, _) = BackendSelector.Select(_hardware.Probe(), runSettings, available);
if (!available.Contains(plan.ModelName))
    throw new InvalidOperationException($"The selected model '{plan.ModelName}' isn't installed. …");
```

The message is surfaced by the dialog's existing `catch (Exception ex) => _errors.Report(...)`.
Because the picker lists only present models and `auto` keeps `medium.en` English, the **only**
UI path that trips this is picking `medium.en` **and** switching to an explicit non-English
language (strips to multilingual `medium`, not bundled). The message should say exactly that,
e.g.:

> "medium.en is English-only. For {language} use large-v3-turbo (multilingual); the
> multilingual 'medium' weights aren't installed."

Atomicity is unchanged: gating before any folder is created means the common refusal costs no
partial folder and no wasted copy/decode; any later fault still deletes the partial session
folder (`AudioImporter.cs:175-179`), and the original file is never touched.

### 5. Packaging (this round: acquisition + layout)

No production installer exists yet (`fetch-models.ps1` is dev-only; Stage 7 owns real
packaging). This round delivers:

- **Acquisition** — extend `tools/fetch-models.ps1` with a new `-LargeModels` switch (parallel
  to `-Assistant`, so a default dev run stays lean) that downloads the four files from
  `huggingface.co/ggerganov/whisper.cpp`:
  - `ggml-large-v3-turbo.bin` (f16)
  - `ggml-large-v3-turbo-q5_0.bin`
  - `ggml-medium.en.bin` (f16)
  - `ggml-medium.en-q5_0.bin`

  **SHA-pinned** via the HF LFS pointer using the existing `Get-HfPinnedSha256` +
  `Assert-Sha256` fail-closed helpers. Exact quantized filenames and pins are confirmed against
  the repo during implementation (q5_0 is the confirmed-available quant for these sizes; if a
  size only ships one of the two variants, ship what exists — the resolver copes).

- **Bundle / layout** — the publish/packaging step places these four files (plus today's
  tiny/base/small.en set) into `models\` **beside the binary**, which is exactly where
  `ModelPaths.ModelsRoot` looks in an installed app. Extend the existing publish layout guard
  (the same guard the assistant deploy uses) to include them so a missing/renamed file fails
  the build rather than shipping a picker entry that can't load.

Out of scope this round: the full MSI/setup installer (Stage 7) and any whisper load-time
re-verification manifest (the assistant path has one; whisper does not, and adding one is
Stage 7).

### 6. Testing

- **Core (`tests/LocalScribe.Core.Tests`):**
  - `AudioImporter` applies `request.Model` / `request.Language` as an override into the run
    (the runner resolves the chosen model, not the global one).
  - `request.Model == null` falls back to `_settings.Model` (back-compat).
  - Presence gate refuses a missing model **before** any session folder is created (assert no
    folder left behind, clear message).
  - `ModelFileResolver` cases: `large-v3-turbo` canonical is idempotent (no quant suffix) and
    resolves q5_0 on CPU / f16 on CUDA when both present; `medium.en-q5_0` collapses to
    `medium.en`.
- **App (`tests/LocalScribe.App.Tests`):**
  - `ImportDialogViewModel` populates `ModelChoices` from the injected func, defaults
    `SelectedModel` to `large-v3-turbo` when present (and falls back predictably when absent),
    defaults `Language` to `"auto"`, and writes both onto the `ImportRequest` in `StartAsync`.
- **`fetch-models.ps1`:** manual smoke (download + SHA verify) — not unit-tested, per house
  style. No Unicode emojis in any test script (global rule).

### 7. Out of scope

- No change to live recording or global `auto` behaviour.
- No `large-v3`, no multilingual `medium`.
- No new model enums or `ModelLadder` rungs.
- No whisper load-time re-verification manifest; no MSI/setup (both Stage 7).

## Files touched

- `src/LocalScribe.Core/Import/AudioImporter.cs` — `ImportRequest` fields; `runSettings`
  override; presence gate.
- `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs` — picker members + ctor param;
  `StartAsync` writes Model/Language.
- `src/LocalScribe.App/ImportDialog.xaml` (+ its thin code-behind) — two combo boxes.
- `src/LocalScribe.App/App.xaml.cs` — pass `ModelPaths.AvailableModels` into the import VM.
- `tools/fetch-models.ps1` — `-LargeModels` switch, SHA-pinned.
- Publish/layout guard — include the four new files.
- `tests/LocalScribe.Core.Tests/*` and `tests/LocalScribe.App.Tests/*` — the tests above.

## Open items for the planning step

- Confirm the exact publish/layout-guard mechanism in the repo and how it enumerates expected
  files (memory: the assistant deploy used a layout guard of ~27 files).
- Confirm exact HF filenames + LFS pointer paths + pinned SHAs for the four weights.
- Confirm the `ImportDialog.xaml` layout region for the two combo boxes.
