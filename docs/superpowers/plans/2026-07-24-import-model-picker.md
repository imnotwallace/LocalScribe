# Import-time Whisper Model Picker + Bundled Large Models — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-import Whisper model + language picker to the Import dialog (default `large-v3-turbo` / auto-detect) and bundle `large-v3-turbo` + `medium.en` (f16 + q5_0) so imports can be transcribed with a large, high-quality model without touching live recording or global settings.

**Architecture:** Copy the proven Re-transcribe per-run override seam — thread a `Model`/`Language` pick through `ImportRequest` into `AudioImporter`, which builds `_settings with { Model, Language }` and hands it to the existing `OfflinePipelineRunner`. A fail-fast presence gate refuses an uninstalled model before any folder is created. Packaging is delivered as fetch-script acquisition + a layout guard; the app finds bundled weights via `ModelPaths.ModelsRoot`'s beside-the-binary branch with no code change.

**Tech Stack:** .NET / WPF (Wpf.Ui), CommunityToolkit.Mvvm (`[ObservableProperty]`, `AsyncRelayCommand`), xUnit, PowerShell (fetch/guard scripts), whisper.cpp ggml weights.

**Spec:** `docs/superpowers/specs/2026-07-24-import-model-picker-design.md`

## Global Constraints

- **Import-only override.** No changes to live recording, global `auto`, `BackendSelector`, `ModelLadder`, or model enums. Turbo works via the explicit-override pass-through + defensive `Downgrade`/`HasEnglishVariant` defaults (verified in the spec).
- **Models offered/bundled:** `large-v3-turbo` and `medium.en`, both f16 **and** q5_0. Filenames from `huggingface.co/ggerganov/whisper.cpp`: `ggml-large-v3-turbo.bin`, `ggml-large-v3-turbo-q5_0.bin`, `ggml-medium.en.bin`, `ggml-medium.en-q5_0.bin`.
- **Defaults:** import model picker defaults to `large-v3-turbo` (falls back `medium.en` → first-available); language defaults to `"auto"` (auto-detect).
- **`ModelFileResolver` bijection must hold:** `large-v3-turbo` has no quant suffix (canonical is idempotent); `*-q5_0` collapses to the canonical name.
- **No Unicode emojis in any test or PowerShell script** (user global rule).
- **Packaging scope:** acquisition (`fetch-models.ps1 -LargeModels`) + layout guard only. The full MSI/setup installer is Stage 7 and out of scope.
- **TDD, frequent commits, build must stay 0-warn.** Build: `dotnet build LocalScribe.slnx`. Tests: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj` and `.../LocalScribe.App.Tests/LocalScribe.App.Tests.csproj` with `--filter`.

## File Structure

- `src/LocalScribe.Core/Import/AudioImporter.cs` — `ImportRequest` gains `Model`/`Language`; `ImportAsync` builds `runSettings`, presence-gates, passes `runSettings` to the runner; ctor gains optional injected `availableModels`.
- `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs` — picker members + ctor param; `StartAsync` writes `Model`/`Language`.
- `src/LocalScribe.App/ImportDialog.xaml` — two combo boxes.
- `src/LocalScribe.App/App.xaml.cs` — pass `ModelPaths.AvailableModels` into the import VM.
- `tools/fetch-models.ps1` — `-LargeModels` switch, SHA-pinned.
- `tools/verify-import-models.ps1` (new) — bundled-models layout guard.
- Tests: `tests/LocalScribe.Core.Tests/AudioImporterTests.cs`, `.../ModelFileResolverTests.cs`, `tests/LocalScribe.App.Tests/ImportDialogViewModelTests.cs`.

---

### Task 1: Thread a per-import model/language override through `ImportRequest` → `AudioImporter`

**Files:**
- Modify: `src/LocalScribe.Core/Import/AudioImporter.cs` (`ImportRequest` record ~line 14; `ImportAsync` ~line 60 and the runner construction ~line 137)
- Test: `tests/LocalScribe.Core.Tests/AudioImporterTests.cs`

**Interfaces:**
- Produces: `ImportRequest { string? Model; string? Language; }` (both nullable, null ⇒ fall back to global `Settings`). `AudioImporter.ImportAsync` transcribes with `_settings with { Model = request.Model ?? _settings.Model, Language = request.Language ?? _settings.Language }`.
- Consumes: existing `OfflinePipelineRunner(StoragePaths, Settings, IEngineFactory, Func<ISpeechProbabilityModel>, IHardwareProbe, IClock, TimeProvider, string)` (2nd arg is the settings snapshot).

- [ ] **Step 1: Add optional model/language to the test `Request` helper**

In `AudioImporterTests.cs`, replace the `Request` helper (currently ~line 90):

```csharp
private static ImportRequest Request(string sourcePath, string title = "Client call",
    StereoMapping stereo = StereoMapping.Downmix, string? model = null, string? language = null) => new()
{
    SourcePath = sourcePath, Title = title,
    RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.FromHours(10)),
    MatterIds = ["M-2026-001"], Stereo = stereo, Model = model, Language = language,
};
```

- [ ] **Step 2: Write the failing tests**

Add to `AudioImporterTests.cs`:

```csharp
[Fact]
public async Task Import_honors_an_explicit_model_override()
{
    string source = Path.Combine(_root, "override.mp3");
    await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
    var decoder = new FakeDecoder
    {
        DecodedWavPath = WriteBurstWav("decoded-ov.wav", 16000, 1, 0),
        Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1 },
    };

    // Global settings are Model=auto; the explicit "tiny.en" override must win.
    string id = await MakeImporter(decoder).ImportAsync(
        Request(source, model: "tiny.en", language: "en"),
        progress: null, _ => Task.FromResult(true), CancellationToken.None);

    var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
    Assert.Equal("tiny.en", session!.Model);
    Assert.Equal("ggml-tiny.en.bin", session.WeightsFile);   // fake engine names the file from the model
}

[Fact]
public async Task Import_with_no_override_uses_the_global_settings_model()
{
    string source = Path.Combine(_root, "global.mp3");
    await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
    var decoder = new FakeDecoder
    {
        DecodedWavPath = WriteBurstWav("decoded-gl.wav", 16000, 1, 0),
        Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1 },
    };

    string id = await MakeImporter(decoder, new Settings { Model = "base.en", Language = "en" })
        .ImportAsync(Request(source),   // Model/Language null -> global
            progress: null, _ => Task.FromResult(true), CancellationToken.None);

    var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
    Assert.Equal("base.en", session!.Model);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~AudioImporterTests.Import_honors_an_explicit_model_override|FullyQualifiedName~AudioImporterTests.Import_with_no_override_uses_the_global_settings_model"`
Expected: FAIL to compile — `ImportRequest` has no `Model`/`Language`.

- [ ] **Step 4: Add the record fields**

In `AudioImporter.cs`, extend the `ImportRequest` record (after `Stereo`):

```csharp
public sealed record ImportRequest
{
    public required string SourcePath { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset RecordedAtLocal { get; init; }
    public IReadOnlyList<string> MatterIds { get; init; } = [];
    public StereoMapping Stereo { get; init; } = StereoMapping.Downmix;
    /// <summary>Per-import model override (canonical name from the dialog picker); null = use the
    /// global Settings.Model. Design 2026-07-24.</summary>
    public string? Model { get; init; }
    /// <summary>Per-import language override ("auto" = auto-detect); null = global Settings.Language.</summary>
    public string? Language { get; init; }
}
```

- [ ] **Step 5: Build `runSettings` and pass it to the runner**

In `ImportAsync`, add the snapshot at the very top of the method body (before `string workDir = ...`):

```csharp
var runSettings = _settings with
{
    Model = request.Model ?? _settings.Model,
    Language = request.Language ?? _settings.Language,
};
```

Then change the runner construction (currently `new OfflinePipelineRunner(_paths, _settings, ...)`) to use `runSettings`:

```csharp
var runner = new OfflinePipelineRunner(_paths, runSettings, _engineFactory,
    _vadModelFactory, _hardware, _clockFactory(), pinnedTime, _appVersion);
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~AudioImporterTests"`
Expected: PASS (the two new tests plus all existing `AudioImporterTests`).

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Import/AudioImporter.cs tests/LocalScribe.Core.Tests/AudioImporterTests.cs
git commit -m "feat(import): per-import model/language override threaded through AudioImporter"
```

---

### Task 2: Presence-gate an uninstalled import model before any folder is created

**Files:**
- Modify: `src/LocalScribe.Core/Import/AudioImporter.cs` (ctor + `ImportAsync` top)
- Test: `tests/LocalScribe.Core.Tests/AudioImporterTests.cs` (`MakeImporter` + new tests)

**Interfaces:**
- Produces: `AudioImporter` ctor gains a trailing optional `Func<IReadOnlySet<string>>? availableModels = null` (defaults to `ModelPaths.AvailableModels`). `ImportAsync` throws `InvalidOperationException` if the resolved model is not present, before creating the session folder.
- Consumes: `BackendSelector.Select(HardwareInfo, Settings, IReadOnlySet<string>)` and `ModelPaths.AvailableModels` (both already `using`-imported in this file).

- [ ] **Step 1: Inject a hermetic model set into `MakeImporter`**

In `AudioImporterTests.cs`, replace `MakeImporter` (currently ~line 85):

```csharp
private AudioImporter MakeImporter(FakeDecoder decoder, Settings? settings = null,
    IReadOnlySet<string>? models = null)
    => new(_paths, settings ?? new Settings { Language = "en" }, decoder, new EchoFactory(),
        () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
        () => new FakeClock(), new FixedZoneTime(), appVersion: "0.2.0-test",
        availableModels: () => models ?? new HashSet<string> { "base.en", "tiny.en", "small.en" });
```

- [ ] **Step 2: Write the failing tests**

Add to `AudioImporterTests.cs`:

```csharp
[Fact]
public async Task Import_refuses_a_model_that_is_not_installed_before_creating_a_folder()
{
    string source = Path.Combine(_root, "missing-model.mp3");
    await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
    var decoder = new FakeDecoder
    {
        DecodedWavPath = WriteBurstWav("decoded-mm.wav", 16000, 1, 0),
        Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700 },
    };

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        MakeImporter(decoder, models: new HashSet<string> { "base.en" })   // turbo absent
            .ImportAsync(Request(source, model: "large-v3-turbo"),
                progress: null, _ => Task.FromResult(true), CancellationToken.None));

    Assert.Contains("large-v3-turbo", ex.Message);
    Assert.Contains("isn't installed", ex.Message);
    Assert.True(!Directory.Exists(_paths.SessionsDir)
        || !Directory.EnumerateDirectories(_paths.SessionsDir).Any());   // gated before any folder
    Assert.True(File.Exists(source));                                     // original untouched
}

[Fact]
public async Task Import_medium_en_with_a_non_english_language_refuses_with_a_multilingual_hint()
{
    string source = Path.Combine(_root, "spanish.mp3");
    await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
    var decoder = new FakeDecoder
    {
        DecodedWavPath = WriteBurstWav("decoded-es.wav", 16000, 1, 0),
        Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700 },
    };

    // medium.en present but NOT multilingual "medium": a non-English language strips
    // medium.en -> medium (BackendSelector), which is absent -> refuse with a routing hint.
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        MakeImporter(decoder, models: new HashSet<string> { "medium.en" })
            .ImportAsync(Request(source, model: "medium.en", language: "es"),
                progress: null, _ => Task.FromResult(true), CancellationToken.None));

    Assert.Contains("English-only", ex.Message);
    Assert.Contains("large-v3-turbo", ex.Message);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~AudioImporterTests.Import_refuses_a_model_that_is_not_installed_before_creating_a_folder|FullyQualifiedName~AudioImporterTests.Import_medium_en_with_a_non_english_language_refuses_with_a_multilingual_hint"`
Expected: FAIL to compile (`MakeImporter` passes an `availableModels` arg the ctor does not accept).

- [ ] **Step 4: Add the ctor param and field**

In `AudioImporter.cs`, add the field and extend the ctor (append the optional param last so `App.xaml.cs`'s existing construction keeps compiling):

```csharp
private readonly Func<IReadOnlySet<string>> _availableModels;

public AudioImporter(StoragePaths paths, Settings settings, IAudioDecoder decoder,
    IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
    IHardwareProbe hardware, Func<IClock> clockFactory, TimeProvider machineTime, string appVersion,
    Func<IReadOnlySet<string>>? availableModels = null)
    => (_paths, _settings, _decoder, _engineFactory, _vadModelFactory, _hardware, _clockFactory,
            _machineTime, _appVersion, _availableModels)
     = (paths, settings, decoder, engineFactory, vadModelFactory, hardware, clockFactory,
            machineTime, appVersion, availableModels ?? ModelPaths.AvailableModels);
```

- [ ] **Step 5: Add the presence gate**

In `ImportAsync`, immediately after the `runSettings` snapshot (from Task 1) and **before** `string workDir = ...`:

```csharp
// Fail-fast presence gate (design 2026-07-24 section 4): refuse an uninstalled model before
// any copy/decode/folder work. Mirrors RetranscriptionRunner's gate; resolves through the SAME
// override BackendSelector applies (a non-English + ".en" model strips to multilingual weights).
var available = _availableModels();
var (plan, _) = BackendSelector.Select(_hardware.Probe(), runSettings, available);
if (!available.Contains(plan.ModelName))
{
    string picked = runSettings.Model;
    string hint = picked.EndsWith(".en", StringComparison.Ordinal) && plan.ModelName == picked[..^3]
        ? $" '{picked}' is English-only; for {runSettings.Language} choose a multilingual model such as large-v3-turbo."
        : "";
    throw new InvalidOperationException(
        $"The transcription model '{plan.ModelName}' isn't installed.{hint}");
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~AudioImporterTests"`
Expected: PASS — the two new refusal tests plus all Task 1 and pre-existing `AudioImporterTests` (the injected `{base.en,tiny.en,small.en}` set satisfies the auto-ladder gate for existing tests).

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Import/AudioImporter.cs tests/LocalScribe.Core.Tests/AudioImporterTests.cs
git commit -m "feat(import): presence-gate an uninstalled import model before folder creation"
```

---

### Task 3: Add the model + language picker to the Import dialog VM, wiring, and XAML

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs` (ctor + members + `StartAsync`)
- Modify: `src/LocalScribe.App/App.xaml.cs` (import VM construction ~line 545)
- Modify: `src/LocalScribe.App/ImportDialog.xaml` (two combo boxes)
- Test: `tests/LocalScribe.App.Tests/ImportDialogViewModelTests.cs` (`MakeVm` + new tests)

**Interfaces:**
- Consumes: `ImportRequest.Model`/`Language` (Task 1); `LanguageChoice(string Code, string Name)` + `LanguageChoice.All` (in `LocalScribe.App.ViewModels`, same namespace as the VM); `ModelPaths.AvailableModels`.
- Produces: `ImportDialogViewModel` ctor gains `Func<IReadOnlySet<string>> availableModels` as its 4th parameter (after `maintenance`). New public members `ModelChoices`, `LanguageChoices`, `SelectedModel`, `Language`.

- [ ] **Step 1: Inject a model set into the test `MakeVm`**

In `ImportDialogViewModelTests.cs`, replace `MakeVm` (currently ~line 64):

```csharp
private (ImportDialogViewModel Vm, FakeDecoder Decoder, RecordingErrors2 Errors)
    MakeVm(ImportRunner? runner = null, string? pickedPath = null, TimeProvider? time = null,
        IReadOnlySet<string>? models = null)
{
    var maintenance = new MaintenanceService(_paths, new FakeSettings2(), new NoopBin2(),
        TimeProvider.System);
    var decoder = new FakeDecoder();
    var errors = new RecordingErrors2();
    var vm = new ImportDialogViewModel(decoder,
        runner ?? ((req, progress, tp, confirm, ct) => Task.FromResult("session-1")),
        maintenance,
        availableModels: () => models ?? new HashSet<string> { "large-v3-turbo", "medium.en", "small.en" },
        pickOpenPath: _ => pickedPath, confirmMismatch: _ => Task.FromResult(true),
        errors, dispatch: a => a(), time ?? new FixedZoneTime());
    return (vm, decoder, errors);
}
```

- [ ] **Step 2: Write the failing tests**

Add to `ImportDialogViewModelTests.cs`:

```csharp
[Fact]
public void ModelChoices_populate_sorted_and_default_to_turbo()
{
    var (vm, _, _) = MakeVm();   // default set: large-v3-turbo, medium.en, small.en
    Assert.Equal(new[] { "large-v3-turbo", "medium.en", "small.en" }, vm.ModelChoices);   // Ordinal
    Assert.Equal("large-v3-turbo", vm.SelectedModel);
    Assert.Equal("auto", vm.Language);
    Assert.Same(LanguageChoice.All, vm.LanguageChoices);
}

[Fact]
public void Default_model_falls_back_when_turbo_is_absent()
{
    var (medium, _, _) = MakeVm(models: new HashSet<string> { "medium.en", "small.en" });
    Assert.Equal("medium.en", medium.SelectedModel);

    var (small, _, _) = MakeVm(models: new HashSet<string> { "small.en" });
    Assert.Equal("small.en", small.SelectedModel);

    var (none, _, _) = MakeVm(models: new HashSet<string>());
    Assert.Null(none.SelectedModel);
}

[Fact]
public async Task Start_writes_the_selected_model_and_language_onto_the_request()
{
    ImportRequest? captured = null;
    ImportRunner runner = (req, p, tp, c, ct) => { captured = req; return Task.FromResult("s"); };
    var (vm, decoder, _) = MakeVm(runner, pickedPath: @"C:\a.mp3");
    decoder.Probe = new AudioProbeResult { FormatName = "mp3" };
    await vm.PickFileCommand.ExecuteAsync(null);
    vm.RecordedAtText = "2026-03-05 14:30";
    vm.SelectedModel = "medium.en";
    vm.Language = "es";

    await vm.StartCommand.ExecuteAsync(null);

    Assert.Equal("medium.en", captured!.Model);
    Assert.Equal("es", captured.Language);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~ImportDialogViewModelTests.ModelChoices_populate_sorted_and_default_to_turbo|FullyQualifiedName~ImportDialogViewModelTests.Default_model_falls_back_when_turbo_is_absent|FullyQualifiedName~ImportDialogViewModelTests.Start_writes_the_selected_model_and_language_onto_the_request"`
Expected: FAIL to compile — the ctor has no `availableModels` param and the VM has no `ModelChoices`/`SelectedModel`/`Language`.

- [ ] **Step 4: Add the ctor param and picker members**

In `ImportDialogViewModel.cs`, add `availableModels` as the 4th ctor parameter and populate the picker. Replace the ctor and add the members below it:

```csharp
public ImportDialogViewModel(IAudioDecoder decoder, ImportRunner runImport,
    MaintenanceService maintenance, Func<IReadOnlySet<string>> availableModels,
    Func<OpenPathRequest, string?> pickOpenPath,
    Func<DurationMismatchInfo, Task<bool>> confirmMismatch,
    IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time)
{
    (_decoder, _runImport, _maintenance, _pickOpenPath, _confirmMismatch, _errors, _dispatch, _time)
        = (decoder, runImport, maintenance, pickOpenPath, confirmMismatch, errors, dispatch, time);
    // Canonical names of models on disk (ModelPaths.AvailableModels collapses quantized files);
    // every entry is a name BackendSelector accepts and the importer's presence gate recognizes.
    ModelChoices = availableModels().OrderBy(m => m, StringComparer.Ordinal).ToList();
    PickFileCommand = new AsyncRelayCommand(PickFileAsync, () => !IsBusy);
    StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
    CancelCommand = new RelayCommand(Cancel);
    ToggleMatterCommand = new RelayCommand<MatterPickRow>(ToggleMatter);
    // Default to the highest-quality bundled model present (imports have time for quality),
    // falling back down the preference list, then to whatever is on disk.
    SelectedModel = PreferredDefaults.FirstOrDefault(ModelChoices.Contains) ?? ModelChoices.FirstOrDefault();
}

/// <summary>Import default preference: turbo first (best quality for a "we can wait" churn),
/// then medium.en. Falls through to the first on-disk model when neither is present.</summary>
private static readonly string[] PreferredDefaults = ["large-v3-turbo", "medium.en"];

public IReadOnlyList<string> ModelChoices { get; }
public IReadOnlyList<LanguageChoice> LanguageChoices { get; } = LanguageChoice.All;
[ObservableProperty] private string? _selectedModel;
[ObservableProperty] private string _language = "auto";
```

- [ ] **Step 5: Write the pick onto the request in `StartAsync`**

In `ImportDialogViewModel.StartAsync`, extend the `new ImportRequest { ... }` initializer with the two new fields:

```csharp
var request = new ImportRequest
{
    SourcePath = source,
    Title = Title.Trim(),
    RecordedAtLocal = recordedAt,
    MatterIds = _pickedMatterIds.ToList(),
    Stereo = !IsStereo || !EachPartyOwnChannel ? StereoMapping.Downmix
        : SwapSides ? StereoMapping.SplitSwapped : StereoMapping.Split,
    Model = SelectedModel,       // null when nothing is on disk -> importer falls back to global
    Language = Language,
};
```

- [ ] **Step 6: Run VM tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~ImportDialogViewModelTests"`
Expected: PASS — the three new tests plus all pre-existing `ImportDialogViewModelTests`.

- [ ] **Step 7: Wire `AvailableModels` into the App construction**

In `App.xaml.cs`, update the import VM construction (currently ~line 545) to pass the models source as the 4th argument:

```csharp
var importVm = new ViewModels.ImportDialogViewModel(decoder, runImport,
    comp.Maintenance, LocalScribe.Core.Transcription.ModelPaths.AvailableModels,
    pickOpenPath, confirmMismatch, errors, dispatch, TimeProvider.System);
```

- [ ] **Step 8: Add the combo boxes to `ImportDialog.xaml`**

In `ImportDialog.xaml`, inside the `HasFile` StackPanel, add the model + language pickers right after the recorded-date helper `TextBlock` (the one ending "...correct it if that is wrong.") and before the stereo-question StackPanel:

```xml
<TextBlock Text="Transcription model" FontWeight="SemiBold" Margin="0,12,0,4" />
<ComboBox ItemsSource="{Binding ModelChoices}" SelectedItem="{Binding SelectedModel}" />
<TextBlock Style="{StaticResource MutedText}" TextWrapping="Wrap" Margin="0,4,0,0"
           Text="Imports have time for a high-quality model. large-v3-turbo (multilingual) or medium.en give the best results; smaller models are faster." />

<TextBlock Text="Language" FontWeight="SemiBold" Margin="0,12,0,4" />
<ComboBox ItemsSource="{Binding LanguageChoices}" DisplayMemberPath="Name"
          SelectedValuePath="Code" SelectedValue="{Binding Language}" />
```

- [ ] **Step 9: Build to verify the XAML binds and the solution is 0-warn**

Run: `dotnet build LocalScribe.slnx`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 10: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs src/LocalScribe.App/App.xaml.cs src/LocalScribe.App/ImportDialog.xaml tests/LocalScribe.App.Tests/ImportDialogViewModelTests.cs
git commit -m "feat(import): model + language picker in the Import dialog (default turbo/auto-detect)"
```

---

### Task 4: Characterize `large-v3-turbo` in `ModelFileResolver` (bijection + per-backend file)

**Files:**
- Test: `tests/LocalScribe.Core.Tests/ModelFileResolverTests.cs`

**Interfaces:**
- Consumes: existing `ModelFileResolver.CanonicalName(string)` and `ModelFileResolver.Resolve(Backend, string, Func<string,bool>)`. No production change — these are characterization tests proving the packaging-relevant behavior already holds.

- [ ] **Step 1: Write the failing tests**

In `ModelFileResolverTests.cs`, add two `InlineData` rows to `Canonical_name_strips_only_known_quant_suffixes` (after the `large-v3` row):

```csharp
[InlineData("large-v3-turbo", "large-v3-turbo")]        // "turbo" is not a quant suffix - no strip
[InlineData("large-v3-turbo-q5_0", "large-v3-turbo")]   // q5_0 collapses to the turbo canonical
```

Add one `InlineData` row to `Canonical_name_is_idempotent`:

```csharp
[InlineData("large-v3-turbo")]
```

Add a new fact:

```csharp
[Fact]
public void Turbo_resolves_f16_on_cuda_and_q5_0_on_cpu_when_both_are_bundled()
{
    // The bundle ships both files (design 2026-07-24): CUDA prefers plain f16, CPU/Vulkan
    // prefer quantized - so each backend loads its ideal file with no code change.
    var onDisk = new HashSet<string> { "ggml-large-v3-turbo.bin", "ggml-large-v3-turbo-q5_0.bin" };
    Assert.Equal("ggml-large-v3-turbo.bin",
        ModelFileResolver.Resolve(Backend.Cuda, "large-v3-turbo", onDisk.Contains));
    Assert.Equal("ggml-large-v3-turbo-q5_0.bin",
        ModelFileResolver.Resolve(Backend.Cpu, "large-v3-turbo", onDisk.Contains));
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~ModelFileResolverTests"`
Expected: PASS immediately — the resolver already handles turbo correctly (no quant suffix; standard candidate ordering). If any FAIL, the resolver's suffix handling regressed and must be investigated, not worked around.

- [ ] **Step 3: Commit**

```bash
git add tests/LocalScribe.Core.Tests/ModelFileResolverTests.cs
git commit -m "test(resolver): pin large-v3-turbo canonical + per-backend file selection"
```

---

### Task 5: Acquire the bundled models — `fetch-models.ps1 -LargeModels`

**Files:**
- Modify: `tools/fetch-models.ps1` (`param` block + a new download block near the end)

**Interfaces:**
- Consumes: existing `Get-RemoteFile`, `Assert-Sha256`, and `Get-HfPinnedSha256` helpers (all defined earlier in the script; `Get-HfPinnedSha256` at ~line 169 is outside the `-Assistant` block and thus reusable).

- [ ] **Step 1: Add the `-LargeModels` switch to the param block**

In `tools/fetch-models.ps1`, extend the `param(...)`:

```powershell
param(
    # Also fetch the LOCKED default assistant LLM (design 2026-07-18 section 7.2).
    [switch] $Assistant,
    # Also fetch the large IMPORT-TIME whisper models bundled with the app (design 2026-07-24):
    # large-v3-turbo + medium.en, each f16 (CUDA) and q5_0 (CPU/Vulkan). ~4.2-4.4 GB total.
    [switch] $LargeModels
)
```

- [ ] **Step 2: Add the download block**

Add immediately before the final `Write-Host "done -> $models"` line:

```powershell
# --- Large import-time whisper models (design 2026-07-24) --------------------------------
# Bundled with the app so the Import dialog's model picker has high-quality choices offline.
# Both f16 (CUDA prefers it) and q5_0 (CPU/Vulkan prefer it) per model, so ModelFileResolver
# loads each backend's ideal file. SHA pinned from the HF LFS pointer (raw/main), enforced
# fail-closed. If a q5_0 filename 404s, check the ggerganov/whisper.cpp repo for the actual
# quantized name and update this list AND tools/verify-import-models.ps1 together.
if ($LargeModels) {
    $largeModels = @(
        'ggml-large-v3-turbo.bin'
        'ggml-large-v3-turbo-q5_0.bin'
        'ggml-medium.en.bin'
        'ggml-medium.en-q5_0.bin'
    )
    foreach ($name in $largeModels) {
        $dest = Join-Path $models $name
        $ptr  = "https://huggingface.co/ggerganov/whisper.cpp/raw/main/$name"
        $url  = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$name"
        Write-Host "pin: $name"
        $pin = Get-HfPinnedSha256 -PointerUrl $ptr
        Write-Host "  pinned sha256: $pin"
        if (-not (Test-Path $dest)) {
            Write-Host "fetching: $name"
            Get-RemoteFile -Uris @($url) -OutFile $dest
        } else {
            Write-Host "exists: $name"
        }
        Assert-Sha256 -Path $dest -ExpectedSha256 $pin   # fail-closed: deletes on mismatch
    }
}
```

- [ ] **Step 3: Syntax-check the script (no download)**

Run: `pwsh -NoProfile -Command "$null = [System.Management.Automation.Language.Parser]::ParseFile('tools/fetch-models.ps1', [ref]$null, [ref]$null); Write-Host 'parsed OK'"`
Expected: `parsed OK` (parses with no errors).

- [ ] **Step 4: Manual smoke (real download — run once when preparing a build, not in CI)**

Run: `pwsh -File tools/fetch-models.ps1 -LargeModels`
Expected: four `ggml-*.bin` files land in `models/`, each printing a `verified:` line. This transfers ~4.2-4.4 GB; skip in automated execution and note it in the smoke checklist (Task 7).

- [ ] **Step 5: Commit**

```bash
git add tools/fetch-models.ps1
git commit -m "build(models): fetch-models.ps1 -LargeModels fetches turbo + medium.en (f16+q5_0), SHA-pinned"
```

---

### Task 6: Bundle layout guard — `tools/verify-import-models.ps1`

**Files:**
- Create: `tools/verify-import-models.ps1`

**Interfaces:**
- Standalone: takes `-ModelsDir <path>`, exits 0 when all bundled model files are present and non-empty, exits 1 otherwise. The installer build (Stage 7) runs this against the app's `models\` folder.

- [ ] **Step 1: Create the guard script**

Create `tools/verify-import-models.ps1` (no Unicode emojis):

```powershell
# tools/verify-import-models.ps1
# Layout guard for the bundled import-time transcription models (design 2026-07-24). The
# installer must place these ggml weights in the app's models\ folder BESIDE the binary, where
# ModelPaths.ModelsRoot finds them (no code change). This list mirrors what fetch-models.ps1
# -LargeModels downloads - update both or neither.
param([Parameter(Mandatory = $true)][string] $ModelsDir)
$ErrorActionPreference = 'Stop'

$required = @(
    'ggml-large-v3-turbo.bin'
    'ggml-large-v3-turbo-q5_0.bin'
    'ggml-medium.en.bin'
    'ggml-medium.en-q5_0.bin'
)

$missing = @()
foreach ($name in $required) {
    $p = Join-Path $ModelsDir $name
    if (-not (Test-Path $p) -or (Get-Item $p).Length -eq 0) { $missing += $name }
}

if ($missing.Count -gt 0) {
    Write-Host "FAIL: bundled models at '$ModelsDir' are incomplete - missing or empty:"
    $missing | ForEach-Object { Write-Host "  $_" }
    Write-Host "Run tools/fetch-models.ps1 -LargeModels, then ensure the installer copies models\ beside the binary."
    exit 1
}
Write-Host "PASS: bundled transcription models present ($($required.Count) files)."
exit 0
```

- [ ] **Step 2: Verify the FAIL path (deterministic, no download)**

Run: `pwsh -File tools/verify-import-models.ps1 -ModelsDir "$env:TEMP/ls-empty-models-check"` (a directory with none of the files)
Expected: prints `FAIL: ...` listing all four files and exits 1.

- [ ] **Step 3: Manual smoke — verify the PASS path after a real fetch**

Run: `pwsh -File tools/verify-import-models.ps1 -ModelsDir models`
Expected (after Task 5's manual smoke has populated `models/`): `PASS: bundled transcription models present (4 files).` Note in the smoke checklist; not part of automated execution.

- [ ] **Step 4: Commit**

```bash
git add tools/verify-import-models.ps1
git commit -m "build(models): layout guard for the bundled import-time whisper models"
```

---

### Task 7: Full gate + smoke checklist

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution, 0 warnings**

Run: `dotnet build LocalScribe.slnx`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 2: Run the full Core test suite**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj`
Expected: PASS (the 2 long-standing known fixture failures noted in project memory may remain; no new failures).

- [ ] **Step 3: Run the full App test suite**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj`
Expected: PASS, no new failures.

- [ ] **Step 4: Record the manual smoke items (require a GUI + a real fetch — user runs these)**

Document these in the branch's smoke notes; they are not automatable here:
- `pwsh -File tools/fetch-models.ps1 -LargeModels` downloads all four files and each prints `verified:`.
- `pwsh -File tools/verify-import-models.ps1 -ModelsDir models` prints `PASS ... (4 files)`.
- Launch the app, Import an audio file: the dialog shows a **Transcription model** combo defaulting to `large-v3-turbo` and a **Language** combo defaulting to `Auto-detect`.
- Import with `large-v3-turbo` + Auto-detect on a short English clip → completes; the session's transcript shows and `session.json` records `Model: large-v3-turbo` with the exact `WeightsFile` (f16 on a CUDA box, `-q5_0` on CPU).
- Switch the model to `medium.en` and the language to a non-English option → Import is refused with the "English-only ... choose ... large-v3-turbo" message and no session row is created.

- [ ] **Step 5: Commit any smoke-note doc (if added)**

```bash
git add docs/superpowers/plans/2026-07-24-import-model-picker.md
git commit -m "docs(import): record the import-model-picker manual smoke checklist"
```

---

## Self-Review

**Spec coverage:**
- §Decisions (turbo+medium.en offered; import-only override default turbo; language default auto-detect) → Tasks 1, 3.
- §1 UX two combos → Task 3 Step 8.
- §2 data-flow threading (`ImportRequest` fields, `runSettings`, VM ctor param, App wiring) → Tasks 1, 3.
- §3 no ladder/enum changes → honored (no such task; Task 4 only characterizes existing behavior).
- §4 presence gate + medium.en/non-English message → Task 2.
- §5 packaging (fetch `-LargeModels`, layout guard) → Tasks 5, 6.
- §6 testing (Core override/gate/fallback, resolver turbo, App picker) → Tasks 1, 2, 3, 4.
- §7 out-of-scope (no MSI, no re-verify manifest) → respected.

**Placeholder scan:** none — every code/test/script step contains complete content; the only deferred actions are the explicitly-labeled manual downloads (4.2 GB), which are inappropriate to run in automated execution.

**Type consistency:** `ImportRequest.Model`/`Language` (`string?`) defined in Task 1 and consumed in Task 3; `AudioImporter` `availableModels` param (Task 2) matches `Func<IReadOnlySet<string>>`; `ImportDialogViewModel` 4th ctor arg is `Func<IReadOnlySet<string>> availableModels` in both the VM (Task 3 Step 4), the test `MakeVm` (Step 1), and the App wiring (Step 7); `PreferredDefaults` / `SelectedModel` / `Language` names consistent across steps; the four ggml filenames are identical in `fetch-models.ps1` (Task 5) and `verify-import-models.ps1` (Task 6).
