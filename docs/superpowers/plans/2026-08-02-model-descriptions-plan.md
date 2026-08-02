# Non-Technical Model Descriptions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox ("- [ ]") syntax for tracking.

**Goal:** Give all three Whisper model pickers (Import dialog, Re-transcribe dialog, Settings page) a plain-language subtitle under each technical model name, driven by one shared `WhisperModelCatalog` in Core, without changing what is persisted, exported, or shown on any provenance surface.

**Architecture:** A new `WhisperModelCatalog` static class in `LocalScribe.Core/Transcription` maps canonical model names to `WhisperModelInfo(Name, Subtitle, Rank, EnglishOnly)` records, with a mandatory passthrough fallback because the model set is an open, disk-driven set. Each of the three picker VMs projects its existing name list through the catalog; the three ComboBoxes switch from `SelectedItem` (string) to `SelectedValuePath="Name"` + `SelectedValue` so every bound VM property stays a plain `string` and persistence/provenance code is untouched. A single shared two-line `DataTemplate` lives in `Styles/Fluent.Shared.xaml` (app-merged, visible to all three surfaces).

**Tech Stack:** .NET 8 / C# 12, WPF + Wpf.Ui, CommunityToolkit.Mvvm, xUnit (headless VM-level tests only).

## Global Constraints

- Strict TDD: write the failing test before the implementation, always.
- No Unicode emojis anywhere in code or test scripts.
- VMs stay WPF-free (no WPF types in any ViewModel or Core file).
- No bool-inverting converter exists in this repo — conditional XAML uses Style + DataTrigger (house rule).
- `[ObservableProperty]` equality-gates same-value sets — re-raise manually after collection rebuilds when needed (no collection rebuild in this plan re-assigns an observable property, but keep it in mind if a step is adapted).
- Invariant culture in all export text (no export text is touched by this plan; the constraint is why provenance files must stay untouched).
- Transcripts are evidence — never destructive; friendly model names are a DISPLAY concern only and must never reach `SessionRecord`, `TranscriptVersion`, export headers, the read-view footer, or version labels.
- The model set is OPEN (hard rule): any `ggml-*.bin` a user drops into `models/` must remain visible and selectable — `Describe` must never throw or filter on an unknown name.
- Copy is qualitative only: NO GB figures (the real file varies ~2x by backend: f16 on CUDA vs quantized on CPU/Vulkan) and NO invented benchmark numbers (house precedent: the diariser refuses invented ETAs).
- Close any running `LocalScribe.App.exe` before building — a running app locks `Core.dll` and fails the build with MSB3027.
- View-layer scroll/caret/visual behavior cannot be unit-tested here (no STA/WPF harness; none may be added) — XAML-only tasks end with a smoke-runbook checkbox addition instead of a fake test.
- Cross-plan order: this plan executes BEFORE `2026-08-02-blank-dropdowns-plan.md`. That plan's Tasks 8 and 10 are written against the shapes this plan produces (`ModelChoices : IReadOnlyList<WhisperModelInfo>`, `SelectedValuePath="Name"` combos, `WhisperModelItemTemplate`), and its Task 8 replaces the empty-disk half of the Re-transcribe pinned test this plan's Task 5 writes.

---

### Task 1: `WhisperModelCatalog` in Core — record, `Describe`, `DescribeAll`

**Files:**
- Create: `src\LocalScribe.Core\Transcription\WhisperModelCatalog.cs`
- Test: `tests\LocalScribe.Core.Tests\WhisperModelCatalogTests.cs` (new file)

**Interfaces:**
- Consumes: nothing new (pure static class; `StringComparer`/LINQ via implicit usings, same as `ModelPaths.cs`).
- Produces:
  - `public sealed record WhisperModelInfo(string Name, string Subtitle, int Rank, bool EnglishOnly)` in namespace `LocalScribe.Core.Transcription`
  - `public static WhisperModelInfo WhisperModelCatalog.Describe(string name)`
  - `public static IReadOnlyList<WhisperModelInfo> WhisperModelCatalog.DescribeAll(IEnumerable<string> names)`

Rank semantics (used by Task 5's default): lower Rank = more accurate; 0 is the best real model (`large-v3-turbo`); unknown names get `int.MaxValue` so a "best available" default never prefers an unknown model; the Settings-only `"auto"` sentinel gets `-1` (it is never in `ModelPaths.AvailableModels()`, so it can never win a best-Rank default in the dialogs).

- [ ] Write the failing test. Create `tests\LocalScribe.Core.Tests\WhisperModelCatalogTests.cs` with exactly (note: the Core test project has implicit `Xunit` usings and files in it carry no namespace — match `ModelPathsTests.cs`):

```csharp
using LocalScribe.Core.Transcription;

public class WhisperModelCatalogTests
{
    [Fact]
    public void Describe_returns_the_user_approved_copy_for_the_recommended_model()
    {
        var info = WhisperModelCatalog.Describe("large-v3-turbo");
        Assert.Equal("large-v3-turbo", info.Name);
        Assert.Equal("Best accuracy at fast speed - recommended", info.Subtitle);
        Assert.Equal(0, info.Rank);           // best rank of the real models
        Assert.False(info.EnglishOnly);       // turbo is multilingual
    }

    [Fact]
    public void Describe_covers_every_fetchable_model_and_the_settings_auto_sentinel()
    {
        // Every stem fetch-models.ps1 covers (tiny/base/small/medium, .en variants) plus the
        // two large import models must carry a real subtitle - a cataloged model must never
        // render a bare row.
        string[] cataloged =
        [
            "tiny", "tiny.en", "base", "base.en", "small", "small.en",
            "medium", "medium.en", "large-v3", "large-v3-turbo",
        ];
        foreach (string name in cataloged)
        {
            var info = WhisperModelCatalog.Describe(name);
            Assert.Equal(name, info.Name);
            Assert.NotEqual("", info.Subtitle);
            Assert.InRange(info.Rank, 0, 9);
        }
        var auto = WhisperModelCatalog.Describe("auto");
        Assert.Equal("Choose automatically for this PC", auto.Subtitle);
        Assert.Equal(-1, auto.Rank);          // sentinel: sorts ahead but never in AvailableModels
    }

    [Fact]
    public void Describe_ranks_strictly_by_accuracy_and_flags_english_only_weights()
    {
        // Rank drives "best available on disk" defaults: lower = more accurate. Same accuracy
        // ordering as ModelLadder.Rungs, with the .en variant ranked just ahead of its
        // multilingual sibling and large-v3-turbo ahead of everything.
        string[] byRank =
        [
            "large-v3-turbo", "large-v3", "medium.en", "medium", "small.en",
            "small", "base.en", "base", "tiny.en", "tiny",
        ];
        for (int i = 0; i < byRank.Length; i++)
            Assert.Equal(i, WhisperModelCatalog.Describe(byRank[i]).Rank);
        foreach (string name in byRank)
            Assert.Equal(name.EndsWith(".en", StringComparison.Ordinal),
                WhisperModelCatalog.Describe(name).EnglishOnly);
    }

    [Fact]
    public void Describe_passes_unknown_names_through_with_worst_rank()
    {
        // OPEN-set hard rule: any user-dropped ggml file must stay selectable. Unknown names
        // get a passthrough entry (never a throw or filter), an empty subtitle, and the worst
        // Rank so a best-Rank default never prefers them over a cataloged model.
        var info = WhisperModelCatalog.Describe("distil-large-v3.5");
        Assert.Equal("distil-large-v3.5", info.Name);
        Assert.Equal("", info.Subtitle);
        Assert.Equal(int.MaxValue, info.Rank);
        Assert.False(info.EnglishOnly);

        Assert.True(WhisperModelCatalog.Describe("custom.en").EnglishOnly);   // ".en" convention
    }

    [Fact]
    public void DescribeAll_projects_ordinal_sorted_by_name()
    {
        // The shared picker projection keeps the Ordinal-by-name ordering all three pickers
        // already used, so pinned picker-content orderings survive the type change.
        var all = WhisperModelCatalog.DescribeAll(["small.en", "large-v3-turbo", "zz-custom"]);
        Assert.Equal(new[] { "large-v3-turbo", "small.en", "zz-custom" },
            all.Select(i => i.Name));
        Assert.Equal("Best accuracy at fast speed - recommended", all[0].Subtitle);
        Assert.Equal("", all[2].Subtitle);    // passthrough rides along
    }
}
```

- [ ] Run it and confirm the exact failure:

```
dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~WhisperModelCatalogTests"
```

Expected: build failure `error CS0246: The type or namespace name 'WhisperModelCatalog' could not be found` (the test project fails to compile — that is the red state for a new type).

- [ ] Minimal implementation. Create `src\LocalScribe.Core\Transcription\WhisperModelCatalog.cs` with exactly:

```csharp
namespace LocalScribe.Core.Transcription;

/// <summary>One Whisper model as the pickers present it: the canonical technical name (primary
/// and evidentiary - it is what SessionRecord.Model persists), a plain-language subtitle, an
/// accuracy Rank (lower = more accurate; drives "best available on disk" defaults), and whether
/// the weights are English-only. DISPLAY metadata only - never persisted, never exported
/// (UX round 2026-08-02 item 4).</summary>
public sealed record WhisperModelInfo(string Name, string Subtitle, int Rank, bool EnglishOnly);

/// <summary>The shared catalog behind all three model pickers (Import, Re-transcribe, Settings)
/// - same never-drift rule as LanguageChoice.All. The model set stays OPEN: Describe falls back
/// to a passthrough entry for any name it does not know, mirroring ModelFileResolver's "unknown
/// suffixes stay raw everywhere and load verbatim" rule, so a user-dropped ggml file is always
/// selectable. Copy is qualitative only: no sizes (the real file varies ~2x by backend - f16 on
/// CUDA, quantized on CPU/Vulkan) and no invented benchmark numbers (house precedent: the
/// diariser refuses invented ETAs).</summary>
public static class WhisperModelCatalog
{
    /// <summary>"auto" (Rank -1) is the Settings-only sentinel - never returned by
    /// ModelPaths.AvailableModels, so it can never win a best-Rank default in the dialogs.</summary>
    private static readonly Dictionary<string, WhisperModelInfo> Known = new[]
    {
        new WhisperModelInfo("auto", "Choose automatically for this PC", -1, false),
        new WhisperModelInfo("large-v3-turbo", "Best accuracy at fast speed - recommended", 0, false),
        new WhisperModelInfo("large-v3", "Best accuracy - much slower than the recommended option", 1, false),
        new WhisperModelInfo("medium.en", "Good accuracy, English only - slower", 2, true),
        new WhisperModelInfo("medium", "Good accuracy, any language - slower", 3, false),
        new WhisperModelInfo("small.en", "Decent accuracy, English only - quick", 4, true),
        new WhisperModelInfo("small", "Decent accuracy, any language - quick", 5, false),
        new WhisperModelInfo("base.en", "Basic accuracy, English only - very fast", 6, true),
        new WhisperModelInfo("base", "Basic accuracy, any language - very fast", 7, false),
        new WhisperModelInfo("tiny.en", "Lowest accuracy, English only - fastest, for quick drafts", 8, true),
        new WhisperModelInfo("tiny", "Lowest accuracy, any language - fastest, for quick drafts", 9, false),
    }.ToDictionary(m => m.Name, StringComparer.Ordinal);

    /// <summary>Catalog hit, else a passthrough entry: the name verbatim, no subtitle, worst
    /// Rank (an unknown model must never outrank a cataloged one in a best-available default),
    /// EnglishOnly from the ".en" naming convention.</summary>
    public static WhisperModelInfo Describe(string name)
        => Known.TryGetValue(name, out var info)
            ? info
            : new WhisperModelInfo(name, "", int.MaxValue,
                name.EndsWith(".en", StringComparison.Ordinal));

    /// <summary>The shared picker projection: one entry per name, Ordinal-sorted by Name (the
    /// ordering all three pickers used before the catalog existed).</summary>
    public static IReadOnlyList<WhisperModelInfo> DescribeAll(IEnumerable<string> names)
        => names.OrderBy(n => n, StringComparer.Ordinal).Select(Describe).ToList();
}
```

- [ ] Run again, expect PASS (5 passed):

```
dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~WhisperModelCatalogTests"
```

- [ ] Commit:

```
git add src\LocalScribe.Core\Transcription\WhisperModelCatalog.cs tests\LocalScribe.Core.Tests\WhisperModelCatalogTests.cs
git commit -m "feat(models): shared WhisperModelCatalog with plain-language subtitles and open-set passthrough"
```

---

### Task 2: `ModelPaths.AvailableModels(string modelsRoot)` overload

**Files:**
- Modify: `src\LocalScribe.Core\Transcription\ModelPaths.cs` (the `AvailableModels()` body, currently lines 42-56)
- Test: `tests\LocalScribe.Core.Tests\ModelPathsTests.cs` (append one fact before the closing brace, currently line 102)

**Interfaces:**
- Consumes: `ModelFileResolver.CanonicalName(string)` (existing, `ModelFileResolver.cs:43`).
- Produces: `public static IReadOnlySet<string> ModelPaths.AvailableModels(string modelsRoot)` — the seam Task 7's `BuildModelChoices` delegates to. MUST be an overload, not an optional parameter: `App.xaml.cs:383` and `App.xaml.cs:680` inject the method group `ModelPaths.AvailableModels` as `Func<IReadOnlySet<string>>`, and method-group conversion cannot apply optional parameters (an optional parameter would break both call sites).

- [ ] Write the failing test. In `tests\LocalScribe.Core.Tests\ModelPathsTests.cs`, add this fact immediately after `AvailableModels_EmptyWhenDirMissing` (before the class's closing brace at line 102):

```csharp
    [Fact]
    public void AvailableModels_WithExplicitRoot_ScansThatRootWithTheSameRules()
    {
        // Overload seam for SettingsPageViewModel.BuildModelChoices (UX round 2026-08-02
        // item 4): the Settings page's hermetic modelsRoot must reach the SAME
        // glob+canonicalize rule as every other surface, not a duplicated inline scan.
        string dir = Path.Combine(Path.GetTempPath(), "ls-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ggml-medium.en-q5_0.bin"), "x");   // quantized only
            File.WriteAllText(Path.Combine(dir, "silero_vad.onnx"), "x");           // not a ggml model
            var models = ModelPaths.AvailableModels(dir);
            Assert.Contains("medium.en", models);   // canonicalized, quantized-only disk still counts
            Assert.Single(models);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
```

- [ ] Run it and confirm the exact failure:

```
dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~ModelPathsTests"
```

Expected: build failure `error CS1501: No overload for method 'AvailableModels' takes 1 arguments`.

- [ ] Minimal implementation. In `src\LocalScribe.Core\Transcription\ModelPaths.cs`, replace the whole `AvailableModels()` method (lines 42-56, keeping its existing doc comment on the parameterless form) with:

```csharp
    public static IReadOnlySet<string> AvailableModels() => AvailableModels(ModelsRoot);

    /// <summary>Same enumeration against an explicit root - the delegation seam for
    /// SettingsPageViewModel.BuildModelChoices and its hermetic tests. A distinct overload
    /// (not an optional parameter) so the existing Func&lt;IReadOnlySet&lt;string&gt;&gt;
    /// method-group injections (App.xaml.cs) keep compiling.</summary>
    public static IReadOnlySet<string> AvailableModels(string modelsRoot)
    {
        try
        {
            if (!Directory.Exists(modelsRoot)) return new HashSet<string>();
            return Directory.EnumerateFiles(modelsRoot, "ggml-*.bin")
                .Select(f => Path.GetFileNameWithoutExtension(f)["ggml-".Length..])
                .Select(ModelFileResolver.CanonicalName)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>();   // missing/unreadable models dir -> no models (never throw)
        }
    }
```

- [ ] Run again, expect PASS (all `ModelPathsTests` green, including the four pre-existing facts):

```
dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~ModelPathsTests"
```

- [ ] Commit:

```
git add src\LocalScribe.Core\Transcription\ModelPaths.cs tests\LocalScribe.Core.Tests\ModelPathsTests.cs
git commit -m "feat(models): AvailableModels overload with an explicit root for the Settings delegation seam"
```

---

### Task 3: Import dialog VM projects the catalog

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs` (usings block lines 1-9; `ModelChoices` build at line 65; default at line 72; property at line 90)
- Test: `tests\LocalScribe.App.Tests\ImportDialogViewModelTests.cs` (pinned test `ModelChoices_populate_sorted_and_default_to_turbo` at lines 338-346; add one new fact)

**Interfaces:**
- Consumes: `WhisperModelCatalog.DescribeAll(IEnumerable<string>)` (Task 1).
- Produces: `public IReadOnlyList<WhisperModelInfo> ModelChoices { get; }` on `ImportDialogViewModel` (type change from `IReadOnlyList<string>`); `SelectedModel` stays `string?` and `ImportRequest.Model` at line 261 is untouched.

- [ ] Write the failing tests. In `tests\LocalScribe.App.Tests\ImportDialogViewModelTests.cs`, replace the whole fact at lines 338-346 (`ModelChoices_populate_sorted_and_default_to_turbo`) with these two facts:

```csharp
    [Fact]
    public void ModelChoices_populate_sorted_with_subtitles_and_default_to_turbo()
    {
        var (vm, _, _) = MakeVm(models: new HashSet<string> { "small.en", "large-v3-turbo", "medium.en" });
        Assert.Equal(new[] { "large-v3-turbo", "medium.en", "small.en" },
            vm.ModelChoices.Select(c => c.Name));                              // Ordinal
        Assert.Equal("Best accuracy at fast speed - recommended", vm.ModelChoices[0].Subtitle);
        Assert.Equal("large-v3-turbo", vm.SelectedModel);
        Assert.Equal("auto", vm.Language);
        Assert.Same(LanguageChoice.All, vm.LanguageChoices);
    }

    [Fact]
    public void ModelChoices_keep_user_dropped_models_selectable_with_no_subtitle()
    {
        // OPEN-set hard rule at the VM level: a ggml file the catalog does not know still gets
        // a row (blank subtitle, so the two-line template collapses to one line) and is still
        // the default when it is the only model on disk.
        var (vm, _, _) = MakeVm(models: new HashSet<string> { "my-custom-finetune" });
        var choice = Assert.Single(vm.ModelChoices);
        Assert.Equal("my-custom-finetune", choice.Name);
        Assert.Equal("", choice.Subtitle);
        Assert.Equal("my-custom-finetune", vm.SelectedModel);
    }
```

- [ ] Run and confirm the exact failure:

```
dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ImportDialogViewModelTests"
```

Expected: build failure `error CS1061: 'string' does not contain a definition for 'Name'` (ModelChoices is still `IReadOnlyList<string>`).

- [ ] Minimal implementation in `src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs`. First add the Core.Transcription using — the usings block (lines 1-9) becomes:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;
```

Then replace the `ModelChoices` build (comment + assignment, lines 63-65):

```csharp
        // Canonical names of models on disk (ModelPaths.AvailableModels collapses quantized
        // files) projected through the shared catalog (UX round 2026-08-02 item 4): every Name
        // is one BackendSelector accepts and the importer's presence gate recognizes; names the
        // catalog does not know ride along as passthrough rows (open-set rule).
        ModelChoices = WhisperModelCatalog.DescribeAll(availableModels());
```

Then replace the default selection (line 72, under its existing "Default to the highest-quality bundled model" comment):

```csharp
        SelectedModel = PreferredDefaults.FirstOrDefault(m => ModelChoices.Any(c => c.Name == m))
            ?? ModelChoices.FirstOrDefault()?.Name;
```

Then change the property (line 90):

```csharp
    public IReadOnlyList<WhisperModelInfo> ModelChoices { get; }
```

- [ ] Run again, expect PASS (the whole class, including the untouched `Default_model_falls_back_when_turbo_is_absent` and `Start_writes_the_selected_model_and_language_onto_the_request`):

```
dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ImportDialogViewModelTests"
```

- [ ] Commit:

```
git add src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs tests\LocalScribe.App.Tests\ImportDialogViewModelTests.cs
git commit -m "feat(import): project the model picker through WhisperModelCatalog"
```

---

### Task 4: Shared two-line ItemTemplate + Import dialog XAML + plain-language helper sentence

**Files:**
- Modify: `src\LocalScribe.App\Styles\Fluent.Shared.xaml` (append the template before the closing `</ResourceDictionary>`, currently line 141)
- Modify: `src\LocalScribe.App\ImportDialog.xaml` (ComboBox at line 46; helper TextBlock at lines 47-48)
- Create: `docs\plans\2026-08-02-ux-round-smoke-runbook.md` (create if absent — a parallel plan for another spec item may have created it first; in that case append the section)

**Interfaces:**
- Consumes: `ImportDialogViewModel.ModelChoices : IReadOnlyList<WhisperModelInfo>` and `SelectedModel : string?` (Task 3); `WhisperModelInfo.Name/.Subtitle` (Task 1).
- Produces: app-level `DataTemplate` keyed `WhisperModelItemTemplate` — Tasks 6 and 8 reference it by the same key. Resource lookup walks Window -> Application, so a template merged via `App.xaml`'s `Fluent.Shared.xaml` dictionary resolves in the plain `ImportDialog`/`RetranscribeDialog` Windows and in `SettingsPage`.

This task is view-layer only (no VM logic): no unit test exists or may be added; it ends with a build check plus smoke-runbook checkboxes.

- [ ] In `src\LocalScribe.App\Styles\Fluent.Shared.xaml`, insert immediately before the closing `</ResourceDictionary>` (after the `PillToggleButton` style, line 139):

```xml
    <!-- UX round 2026-08-02 item 4: two-line Whisper-model picker row shared by the Import,
         Re-transcribe and Settings ComboBoxes (never-drift rule, same as LanguageChoice). Line 1
         is the canonical technical name (primary - it is the evidentiary identity the record
         keeps); line 2 is the catalog's plain-language subtitle, collapsed for user-dropped
         models the catalog does not know (WhisperModelCatalog passthrough returns Subtitle="").
         Secondary-text brush only - the Stage 5.4 4.3 rule forbids referencing the primary-text
         brush from this dictionary. -->
    <DataTemplate x:Key="WhisperModelItemTemplate">
        <StackPanel>
            <TextBlock Text="{Binding Name}" />
            <TextBlock Text="{Binding Subtitle}" FontSize="11"
                       Foreground="{DynamicResource TextFillColorSecondaryBrush}">
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Subtitle}" Value="">
                                <Setter Property="Visibility" Value="Collapsed" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
        </StackPanel>
    </DataTemplate>
```

- [ ] In `src\LocalScribe.App\ImportDialog.xaml`, replace lines 46-48 (the model ComboBox and the ID-leaking helper sentence) with:

```xml
            <ComboBox ItemsSource="{Binding ModelChoices}"
                      ItemTemplate="{StaticResource WhisperModelItemTemplate}"
                      SelectedValuePath="Name" SelectedValue="{Binding SelectedModel}" />
            <TextBlock Style="{StaticResource MutedText}" TextWrapping="Wrap" Margin="0,4,0,0"
                       Text="Imports are not live, so there is time for the most accurate option. Pick the recommended model unless you need the transcript quickly." />
```

(`SelectedValuePath="Name"` + `SelectedValue` keeps the bound `SelectedModel` a plain string — the same idiom the Language combo two lines below already uses with `SelectedValuePath="Code"`.)

- [ ] Build to prove the XAML compiles (close any running `LocalScribe.App.exe` first — MSB3027):

```
dotnet build src\LocalScribe.App
```

Expected: `Build succeeded.`

- [ ] Create `docs\plans\2026-08-02-ux-round-smoke-runbook.md` with the content below. If the file already exists (another item's plan created it first), append only the `## M - ...` section:

```markdown
# UX round 2026-08-02 - manual smoke runbook (user)

## M - Model descriptions (item 4)
- [ ] M1 Import dialog: every model row shows the technical name with a one-line plain-language
      description under it; large-v3-turbo reads "Best accuracy at fast speed - recommended".
- [ ] M2 Import dialog: turbo preselected when present; the collapsed (closed) combo showing two
      lines is acceptable; the helper sentence below the combo names no raw model IDs.
- [ ] M3 Import a file with the default selection: the run works and the read-view footer /
      version label show the technical name exactly as before (no subtitle text anywhere).
```

- [ ] Commit:

```
git add src\LocalScribe.App\Styles\Fluent.Shared.xaml src\LocalScribe.App\ImportDialog.xaml docs\plans\2026-08-02-ux-round-smoke-runbook.md
git commit -m "feat(import): two-line model rows via shared WhisperModelItemTemplate; plain-language helper text"
```

---

### Task 5: Re-transcribe dialog VM — catalog projection + best-Rank default (deliberate behavior change)

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\RetranscribeDialogViewModel.cs` (usings block lines 1-5; `ModelChoices` build at line 34; default at line 42; property at line 57)
- Test: `tests\LocalScribe.App.Tests\RetranscribeDialogViewModelTests.cs` (pinned test `ModelChoices_list_only_disk_models_and_gate_Start` at lines 70-87; add one new fact)

**Interfaces:**
- Consumes: `WhisperModelCatalog.DescribeAll(IEnumerable<string>)`, `WhisperModelInfo.Rank` (Task 1).
- Produces: `public IReadOnlyList<WhisperModelInfo> ModelChoices { get; }` on `RetranscribeDialogViewModel`; `SelectedModel` stays `string?` (its `OnSelectedModelChanged` CanExecute wiring at line 76 and `StartAsync`'s use at line 122 are untouched).

- [ ] Write the failing tests. In `tests\LocalScribe.App.Tests\RetranscribeDialogViewModelTests.cs`, replace the whole fact at lines 70-87 with these two facts:

```csharp
    [Fact]
    public async Task ModelChoices_list_only_disk_models_and_gate_Start()
    {
        string id = await SeedFinalizedAsync();
        var (vm, _, _, _) = Make(id);
        Assert.Equal(new[] { "base.en", "tiny.en" },
            vm.ModelChoices.Select(c => c.Name));                       // Ordinal-sorted, no "auto"
        Assert.Equal("Basic accuracy, English only - very fast", vm.ModelChoices[0].Subtitle);
        Assert.Equal("base.en", vm.SelectedModel);   // best Rank on a base/tiny-only disk
        Assert.Equal("auto", vm.Language);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.CancelRunCommand.CanExecute(null));
        vm.Dispose();

        var (empty, _, _, _) = Make(id, models: new HashSet<string>());
        Assert.Empty(empty.ModelChoices);
        Assert.Null(empty.SelectedModel);
        Assert.False(empty.StartCommand.CanExecute(null));               // nothing on disk -> no Start
        empty.Dispose();
    }

    [Fact]
    public async Task Default_model_is_the_best_ranked_on_disk_not_alphabetical_first()
    {
        // DELIBERATE behaviour change (UX round 2026-08-02 item 4): the old
        // ModelChoices.FirstOrDefault() default preselected base.en over large-v3-turbo purely
        // because "b" < "l" ordinally. The default is now the best-Rank model present; unknown
        // (passthrough) models rank worst, so they only win when nothing cataloged is on disk.
        string id = await SeedFinalizedAsync();
        var (vm, _, _, _) = Make(id, models: new HashSet<string> { "base.en", "large-v3-turbo" });
        Assert.Equal("large-v3-turbo", vm.SelectedModel);
        vm.Dispose();

        var (custom, _, _, _) = Make(id, models: new HashSet<string> { "zz-finetune", "tiny.en" });
        Assert.Equal("tiny.en", custom.SelectedModel);   // cataloged beats unknown
        custom.Dispose();
    }
```

- [ ] Run and confirm the exact failure:

```
dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~RetranscribeDialogViewModelTests"
```

Expected: build failure `error CS1061: 'string' does not contain a definition for 'Name'`.

- [ ] Minimal implementation in `src\LocalScribe.App\ViewModels\RetranscribeDialogViewModel.cs`. The usings block (lines 1-5) becomes:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Retranscription;
using LocalScribe.Core.Transcription;
```

Replace the `ModelChoices` build (comment + assignment, lines 31-34):

```csharp
        // availableModels = ModelPaths.AvailableModels in production: CANONICAL model names
        // (quantized files collapse via ModelFileResolver.CanonicalName) projected through the
        // shared catalog for the two-line picker rows, so every pick here is a Name
        // BackendSelector.Select accepts and the runner's presence gate recognizes.
        ModelChoices = WhisperModelCatalog.DescribeAll(availableModels());
```

Replace the default (line 42, which currently reads `SelectedModel = ModelChoices.FirstOrDefault();`):

```csharp
        // Best model on disk, not alphabetical-first (UX round 2026-08-02 item 4: FirstOrDefault
        // used to preselect base.en over large-v3-turbo). All-unknown disks tie at
        // Rank int.MaxValue and fall back to ordinal name order for determinism.
        SelectedModel = ModelChoices.OrderBy(c => c.Rank)
            .ThenBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault()?.Name;
```

Change the property (line 57):

```csharp
    public IReadOnlyList<WhisperModelInfo> ModelChoices { get; }
```

- [ ] Run again, expect PASS (whole class):

```
dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~RetranscribeDialogViewModelTests"
```

- [ ] Commit:

```
git add src\LocalScribe.App\ViewModels\RetranscribeDialogViewModel.cs tests\LocalScribe.App.Tests\RetranscribeDialogViewModelTests.cs
git commit -m "feat(retranscribe): catalog-backed picker rows; default to the best-ranked model on disk"
```

---

### Task 6: Re-transcribe dialog XAML

**Files:**
- Modify: `src\LocalScribe.App\RetranscribeDialog.xaml` (ComboBox at lines 12-13)
- Modify: `docs\plans\2026-08-02-ux-round-smoke-runbook.md` (append two checkboxes to the `## M` section)

**Interfaces:**
- Consumes: `WhisperModelItemTemplate` (Task 4), `RetranscribeDialogViewModel.ModelChoices : IReadOnlyList<WhisperModelInfo>` and `SelectedModel : string?` (Task 5).
- Produces: nothing new.

View-layer only: build check + smoke checkboxes, no fake test.

- [ ] In `src\LocalScribe.App\RetranscribeDialog.xaml`, replace the model ComboBox (lines 12-13) with:

```xml
        <ComboBox ItemsSource="{Binding ModelChoices}"
                  ItemTemplate="{StaticResource WhisperModelItemTemplate}"
                  SelectedValuePath="Name" SelectedValue="{Binding SelectedModel}"
                  Margin="0,0,0,8" />
```

(The dialog is a plain `Window` of this Application, so the app-merged `Fluent.Shared.xaml` template resolves by `StaticResource` here exactly as `MutedText` does in `ImportDialog`.)

- [ ] Build (close any running `LocalScribe.App.exe` first — MSB3027):

```
dotnet build src\LocalScribe.App
```

Expected: `Build succeeded.`

- [ ] Append to the `## M - Model descriptions (item 4)` section of `docs\plans\2026-08-02-ux-round-smoke-runbook.md`:

```markdown
- [ ] M4 Re-transcribe dialog: two-line rows; with large-v3-turbo on disk the default is turbo
      (no longer base.en); "Current transcript: vN - model - date" line unchanged.
- [ ] M5 Re-transcribe with the default: run completes; the new version's label in the read-view
      version dropdown shows the bare technical name.
```

- [ ] Commit:

```
git add src\LocalScribe.App\RetranscribeDialog.xaml docs\plans\2026-08-02-ux-round-smoke-runbook.md
git commit -m "feat(retranscribe): two-line model rows in the dialog picker"
```

---

### Task 7: Settings VM — `BuildModelChoices` delegates to `ModelPaths.AvailableModels` + catalog projection

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs` (`ModelChoices` property at line 397; `BuildModelChoices` at lines 428-446; the ctor assignment at line 212 stays as-is — `BuildModelChoices(modelsRoot ?? ModelPaths.ModelsRoot)`)
- Test: `tests\LocalScribe.App.Tests\SettingsPageViewModelTests.cs` (pinned facts at lines 124-135, 137-147, 149-159)

**Interfaces:**
- Consumes: `ModelPaths.AvailableModels(string)` (Task 2), `WhisperModelCatalog.Describe/DescribeAll` (Task 1). `SettingsPageViewModel` already has `using LocalScribe.Core.Transcription;` (line 15).
- Produces: `public IReadOnlyList<WhisperModelInfo> ModelChoices { get; }` on `SettingsPageViewModel`. The `Model` property (lines 398-405: canonicalize-on-get, `Commit`-on-set) is NOT touched — the canonicalization invariant must keep holding, now expressed as "the canonical getter value matches a choice's `Name`".

- [ ] Write the failing tests. In `tests\LocalScribe.App.Tests\SettingsPageViewModelTests.cs`, replace the three pinned facts (lines 124-159) with:

```csharp
    [Fact]
    public async Task Model_choices_enumerate_only_installed_ggml_files_plus_auto()
    {
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-tiny.en.bin"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-small.bin"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(_root, "models", "silero_vad.onnx"), "x");   // not a whisper model
        var vm = MakeVm();
        Assert.Equal(new[] { "auto", "small", "tiny.en" }, vm.ModelChoices.Select(c => c.Name));
        Assert.Equal("Choose automatically for this PC", vm.ModelChoices[0].Subtitle);
        vm.Model = "tiny.en";
        await vm.LastSave;
        Assert.Equal("tiny.en", _settings.Current.Model);
    }

    [Fact]
    public void Model_choices_dedupe_quantized_files_to_canonical_names()
    {
        // Quantization is a file-level detail (WhisperEngineFactory picks the best file per
        // backend); the picker must offer canonical model names only, once each. Enumeration
        // now delegates to ModelPaths.AvailableModels (UX round 2026-08-02 item 4) - same rule,
        // one implementation.
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-tiny.en.bin"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-tiny.en-q8_0.bin"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-base.en-q5_1.bin"), new byte[] { 1 });
        var vm = MakeVm();
        Assert.Equal(new[] { "auto", "base.en", "tiny.en" }, vm.ModelChoices.Select(c => c.Name));
    }

    [Fact]
    public void Persisted_quantized_model_name_displays_as_its_canonical_choice()
    {
        // Re-verify finding (2026-07-13): a pre-branch/hand-edited Model="small.en-q8_0" is
        // valid at Start (Select canonicalizes) but ModelChoices holds canonical names only -
        // the raw getter value matched nothing and the ComboBox rendered blank. Still pinned
        // after the SelectedValuePath="Name" switch: the canonical getter value must match a
        // choice's Name or SelectedValue selects nothing.
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-small.en-q8_0.bin"), new byte[] { 1 });
        var vm = MakeVm(new Settings { Model = "small.en-q8_0" });
        Assert.Equal("small.en", vm.Model);
        Assert.Contains(vm.ModelChoices, c => c.Name == vm.Model);
    }
```

- [ ] Run and confirm the exact failure:

```
dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsPageViewModelTests"
```

Expected: build failure `error CS1061: 'string' does not contain a definition for 'Name'`.

- [ ] Minimal implementation in `src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs`. Change the property (line 397):

```csharp
    public IReadOnlyList<WhisperModelInfo> ModelChoices { get; }
```

Replace `BuildModelChoices` and its doc comment (lines 428-446) with:

```csharp
    /// <summary>"auto" + only the models actually on disk (design 6.1: an absent model cannot
    /// be selected; model-download UX is Stage 7). Enumeration delegates to
    /// ModelPaths.AvailableModels - the one glob+canonicalize rule every surface uses (quantized
    /// ggml variants collapse; WhisperEngineFactory picks the best file per backend) - then
    /// projects through the shared catalog for the two-line picker rows (UX round 2026-08-02
    /// item 4; the old inline scan was the exact drift LanguageChoice's doc comment warns about).</summary>
    private static IReadOnlyList<WhisperModelInfo> BuildModelChoices(string modelsRoot)
    {
        var choices = new List<WhisperModelInfo> { WhisperModelCatalog.Describe("auto") };
        choices.AddRange(WhisperModelCatalog.DescribeAll(ModelPaths.AvailableModels(modelsRoot)));
        return choices;
    }
```

- [ ] Run again, expect PASS — and verify the canonicalization invariant fact `Persisted_quantized_model_name_displays_as_its_canonical_choice` is among the passing tests (it is the invariant the SelectedValuePath switch must preserve):

```
dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsPageViewModelTests"
```

- [ ] Commit:

```
git add src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs tests\LocalScribe.App.Tests\SettingsPageViewModelTests.cs
git commit -m "feat(settings): BuildModelChoices delegates to ModelPaths.AvailableModels and projects the catalog"
```

---

### Task 8: Settings page XAML — template, `SelectedValue`, widen the combo

**Files:**
- Modify: `src\LocalScribe.App\SettingsPage.xaml` (Model ComboBox at lines 102-103)
- Modify: `docs\plans\2026-08-02-ux-round-smoke-runbook.md` (append four checkboxes)

**Interfaces:**
- Consumes: `WhisperModelItemTemplate` (Task 4); `SettingsPageViewModel.ModelChoices : IReadOnlyList<WhisperModelInfo>` and `Model : string` (Task 7).
- Produces: nothing new.

View-layer only: build check + smoke checkboxes, no fake test. `MinWidth` goes 140 -> 260: the spec widens the app's narrowest model combo so subtitles like "Best accuracy at fast speed - recommended" fit without ellipsis; the Backend/Language combos beside it keep 140.

- [ ] In `src\LocalScribe.App\SettingsPage.xaml`, replace the Model ComboBox (lines 102-103) with:

```xml
                        <ComboBox ItemsSource="{Binding ModelChoices}"
                                  ItemTemplate="{StaticResource WhisperModelItemTemplate}"
                                  SelectedValuePath="Name" SelectedValue="{Binding Model}"
                                  MinWidth="260" />
```

- [ ] Build (close any running `LocalScribe.App.exe` first — MSB3027):

```
dotnet build src\LocalScribe.App
```

Expected: `Build succeeded.`

- [ ] Append to the `## M - Model descriptions (item 4)` section of `docs\plans\2026-08-02-ux-round-smoke-runbook.md`:

```markdown
- [ ] M6 Settings > Transcription: "auto" row reads "Choose automatically for this PC"; all rows
      two-line; the combo is wide enough that no subtitle ellipsizes.
- [ ] M7 Settings: pick a model, restart the app - the pick persisted and the row is selected
      (settings.json holds the bare technical name, no subtitle text).
- [ ] M8 Drop any foreign ggml file (e.g. rename one to ggml-myfinetune.bin) into models\ and
      reopen Settings + both dialogs: "myfinetune" appears as a single-line row and is selectable.
- [ ] M9 Provenance spot-check: read-view footer "model - BACKEND", version dropdown labels,
      Record console engine chip, and an exported transcript.md header line all show bare
      technical names - zero plain-language copy outside the three pickers.
```

- [ ] Commit:

```
git add src\LocalScribe.App\SettingsPage.xaml docs\plans\2026-08-02-ux-round-smoke-runbook.md
git commit -m "feat(settings): two-line model rows; widen the model combo for subtitles"
```

---

### Task 9: Provenance byte-identity verification (no code changes)

**Files:**
- Modify: none — this task PROVES the provenance surfaces were not touched. If any check below fails, the fix is to revert the offending change, never to "update" a provenance surface.
- Test: existing pinned suites only.

**Interfaces:**
- Consumes: the pinned provenance facts — `ReadViewVersionSwitchTests` (footer `"tiny.en · CPU"` etc. at lines 86/107/118), `ReadViewViewModelTests` (line 186), `SessionViewModelTests.FormatEngineChip_backend_override_reflects_a_mid_session_floor_fall` (lines 457-465), and the Core renderer suites that pin the `{Model}/{Backend}` export header (`RendererTests`, `MarkdownRendererWriteTests`, `DocxRendererTests`).
- Produces: nothing.

- [ ] Confirm the catalog leaked nowhere: run

```
git grep -l "WhisperModelCatalog\|WhisperModelInfo" -- src tests
```

Expected output is EXACTLY these five files and no others (the three App test files touch the type only through `c => c.Name` lambdas, so they carry no literal reference; in particular there must be NO hit in `ReadViewViewModel.cs`, `SessionViewModel.cs`, `RecordingConsoleViewModel.cs`, `MarkdownRenderer.cs`, `DocxRenderer.cs`, `SessionRecord.cs`, `RetranscriptionRunner.cs`, `AudioImporter.cs`):

```
src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs
src/LocalScribe.App/ViewModels/RetranscribeDialogViewModel.cs
src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs
src/LocalScribe.Core/Transcription/WhisperModelCatalog.cs
tests/LocalScribe.Core.Tests/WhisperModelCatalogTests.cs
```

(Any extra hit is acceptable ONLY if it is one of the three App test files this plan edited spelling the type name out explicitly; a hit anywhere else is a leak — revert it.)

- [ ] Confirm no plan commit touched a provenance file: run

```
git log -5 --oneline -- src\LocalScribe.App\ViewModels\ReadViewViewModel.cs src\LocalScribe.App\ViewModels\SessionViewModel.cs src\LocalScribe.Core\Projection\MarkdownRenderer.cs src\LocalScribe.Core\Projection\DocxRenderer.cs src\LocalScribe.Core\Model\SessionRecord.cs
```

Expected: none of this plan's commit messages (`feat(models)`, `feat(import)`, `feat(retranscribe)`, `feat(settings)`) appear — every listed commit predates this plan.

- [ ] Run the provenance-pinning suites and confirm all PASS with zero edits made to them by this plan:

```
dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~Renderer"
dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewVersionSwitchTests|FullyQualifiedName~ReadViewViewModelTests|FullyQualifiedName~SessionViewModelTests"
```

Expected: all green — the read-view footer stays `"{Model} · {Backend}"`, version labels stay `"v1 · {Model}"` / `"{shortId} · {Model}"`, the engine chip stays `"{model} · {BACKEND}"`, and the export header stays `"{Model}/{Backend}"`, all byte-identical.

- [ ] No commit (nothing changed). Tick this task's boxes in the plan file only.

---

### Task 10: Full suites + wrap-up

**Files:**
- Modify: only whatever a regression fix requires (expected: none).

**Interfaces:** none new.

- [ ] Close any running `LocalScribe.App.exe` (running app locks `Core.dll` -> MSB3027), then run BOTH full suites:

```
dotnet test tests\LocalScribe.Core.Tests
dotnet test tests\LocalScribe.App.Tests
```

Expected: 100% pass (Core was 1015 and App 838 before this plan; this plan adds 6 Core facts — 5 catalog + 1 ModelPaths overload — and nets +2 App facts: Import 1 fact became 2, Re-transcribe 1 became 2, Settings 3 stayed 3). Fix any regression at its root (systematic debugging, not assertion edits) and re-run until green.

- [ ] Verify the smoke runbook section is complete: `docs\plans\2026-08-02-ux-round-smoke-runbook.md` contains checkboxes M1-M9 (Tasks 4, 6, 8). These are USER-run smokes — leave them unticked.

- [ ] If a regression fix changed any file, commit it:

```
git add <the exact files the fix touched>
git commit -m "fix(models): <what the regression was and why the fix is at the root>"
```
