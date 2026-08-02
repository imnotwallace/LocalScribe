# No Blank Dropdowns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox ("- [ ]") syntax for tracking.

**Goal:** Every ComboBox in LocalScribe shows a selected, member-of-list value (or an honest sentinel/watermark) on first open and after every rebuild — spec `docs/superpowers/specs/2026-08-02-ux-round-design.md` section 3, all 11 fix sites.

**Architecture:** All fixes live in `src\LocalScribe.App` ViewModels (WPF-free, CommunityToolkit.Mvvm) plus small XAML edits; three mechanisms are repaired per-site: async-items-synchronous-selection (display fallback / seed-at-construction), selected-value-not-in-list (insert the value into the list — the mic-picker "(not connected)" pattern), and no-default-at-all (explicit sentinel rows). The SessionsPage null-vs-"" sentinel contradiction is settled in favour of SearchPage's `""` convention (spec Note, settled — do not reopen).

**Tech Stack:** .NET / WPF, CommunityToolkit.Mvvm (`[ObservableProperty]`, `RelayCommand`/`AsyncRelayCommand`), Wpf.Ui chrome, xUnit (headless VM-level tests only).

## Global Constraints

- Strict TDD: write the failing test, run it, watch it fail, then implement — always.
- No Unicode emojis anywhere in code or test scripts.
- VMs stay WPF-free: no `System.Windows` usings in any `ViewModels\*.cs` file.
- No bool-inverting converter exists — any new conditional XAML visibility uses Style + DataTrigger (house rule).
- `[ObservableProperty]` equality-gates same-value sets — after a collection Clear()+refill, re-raise the selection's PropertyChanged manually when the value is unchanged.
- Invariant culture in all export text (no export text is touched by this plan; keep any new formatting invariant anyway).
- Transcripts are evidence — never destructive; none of these fixes may rewrite settings.json or any session artifact on mere page-open (display-coerce only; commits happen only on explicit user action).
- Close any running LocalScribe.App.exe before building — a running app locks Core.dll -> MSB3027.
- View-layer visual behavior (adorner watermark, XAML overlay rendering) cannot be unit-tested here — such tasks end with a smoke-runbook checkbox addition instead of a fake test.
- Line anchors below were verified against live code on 2026-08-02; re-verify each anchor with Grep before editing (they drift as earlier tasks land).
- Test commands: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~<TestClass>"` (App) / `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~<TestClass>"` (Core).
- Cross-plan order: execute AFTER `2026-08-02-model-descriptions-plan.md` — Tasks 8 and 10 below are written against the catalog-shaped pickers it produces (`ModelChoices : IReadOnlyList<WhisperModelInfo>`, `WhisperModelCatalog`, `WhisperModelItemTemplate`, `SelectedValuePath="Name"`). Every other task is order-independent.

---

### Task 1: Settings > Assistant model — display-fallback to the first installed chat model

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs` (the `AssistantModel` property, lines 662-679)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\SettingsPageViewModelAssistantTests.cs`

**Interfaces:**
- Consumes: `ObservableCollection<string> AssistantModelChoices` (SettingsPageViewModel.cs:629, filled by `LoadAssistantModelsAsync` at :684-718, which already ends with `OnPropertyChanged(nameof(AssistantModel))` at :705); `Task AssistantModelsLoad` (:627, awaitable load seam); `AssistantModelManifest.DefaultCanonicalName = "Qwen3-4B-Instruct-2507"` (`src\LocalScribe.Core\Assistant\AssistantModels.cs:42`); Core's runtime fallback `chat.FirstOrDefault()` (AssistantModels.cs:87-88) — the behaviour the picker must mirror.
- Produces: `SettingsPageViewModel.AssistantModel` getter that always returns a member of `AssistantModelChoices` whenever that collection is non-empty. No new members.

**Steps:**

- [ ] Add the failing test to `SettingsPageViewModelAssistantTests.cs` (it already has `Qwen17`, `MakeVm`, `_settings` — see :27-48):

```csharp
    [Fact]
    public async Task Picker_displays_the_first_installed_chat_model_when_the_saved_default_is_not_installed()
    {
        // UX round 2026-08-02 item 3.1: the user never picked a model (Assistant.Model == null),
        // so the getter returns the locked default name - but only Qwen3-1.7B is installed. Core
        // resolves this exact situation to chat.FirstOrDefault() (AssistantModels.cs:87-88), so
        // the app RUNS Qwen3-1.7B while the picker painted blank. Display must agree with Core.
        var cache = new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([Qwen17], Qwen17, [])));
        var vm = MakeVm(cache);
        await vm.AssistantModelsLoad;

        Assert.Equal("Qwen3-1.7B-Instruct", vm.AssistantModel);
        Assert.Contains(vm.AssistantModel, vm.AssistantModelChoices);
        // Display-coerce ONLY: page-open never rewrites settings.json (evidentiary rule).
        Assert.Equal(0, _settings.SaveCount);
        Assert.Null(_settings.Current.Assistant.Model);
    }

    [Fact]
    public async Task Picker_keeps_the_saved_name_before_load_and_when_no_chat_model_is_installed()
    {
        // Before the manifest scan lands the choices are empty - the getter must still return a
        // non-null string (the box is disabled via HasAssistantModels until then, so the
        // transient state is a DISABLED box, never an enabled blank one).
        var vm = MakeVm(new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([], null, []))));
        Assert.Equal("Qwen3-4B-Instruct-2507", vm.AssistantModel);   // at construction

        await vm.AssistantModelsLoad;
        Assert.Equal("Qwen3-4B-Instruct-2507", vm.AssistantModel);   // empty manifest: unchanged
        Assert.False(vm.HasAssistantModels);
        Assert.Equal(0, _settings.SaveCount);
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsPageViewModelAssistantTests"` — expect the first new test to FAIL with `Assert.Equal() Failure ... Expected: "Qwen3-1.7B-Instruct" / Actual: "Qwen3-4B-Instruct-2507"` (the second may already pass — that is fine, it pins the boundary).

- [ ] Implement: replace the `AssistantModel` getter (SettingsPageViewModel.cs:662-679; the setter is untouched):

```csharp
    /// <summary>Model picker over manifest canonical names. Storing the locked default
    /// stores null (the "no explicit pick" sentinel), so a future default change follows.
    /// Display fallback (UX round 2026-08-02 item 3.1): when the stored/default name has no
    /// installed match, show the FIRST installed chat model - the same resolution Core applies
    /// at run time (AssistantModels.cs chat.FirstOrDefault()), so the picker never disagrees
    /// with what actually runs. Display-coerce ONLY: nothing is committed by reading this.</summary>
    public string AssistantModel
    {
        get
        {
            string stored = _settings.Current.Assistant.Model
                            ?? AssistantModelManifest.DefaultCanonicalName;
            return AssistantModelChoices.Count > 0 && !AssistantModelChoices.Contains(stored)
                ? AssistantModelChoices[0]
                : stored;
        }
        set
        {
            Commit(s => s with
            {
                Assistant = s.Assistant with
                {
                    Model = string.IsNullOrWhiteSpace(value)
                            || value == AssistantModelManifest.DefaultCanonicalName ? null : value,
                },
            });
            OnPropertyChanged();
        }
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsPageViewModelAssistantTests"` — expect ALL PASS (including the pre-existing `Toggle_and_model_pick_persist_via_the_commit_pattern`, `Installed_models_populate_the_picker`, `Embedding_model_is_excluded_from_the_chat_picker`, `No_model_shows_fetch_instructions_and_disables_the_picker`).

- [ ] Commit:
```
git add src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs tests\LocalScribe.App.Tests\SettingsPageViewModelAssistantTests.cs
git commit -m "fix(settings): assistant model picker displays the first installed chat model"
```

---

### Task 2: Record console > Remote target — insert the synthesized option into the list

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\RecordingConsoleViewModel.cs` (`OptionFor`, lines 189-200)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\RecordingConsoleViewModelTests.cs`

**Interfaces:**
- Consumes: `ObservableCollection<RemoteTargetOption> RemoteTargetOptions` (:74, rebuilt by `RebuildRemoteTargetOptions` at :146-187 which always appends the System-mix row LAST at :173-174, and whose selection-preserve fallback at :184 calls `OptionFor`); `sealed record RemoteTargetOption(string Label, RemoteSetting Setting, bool IsSystemMix)` (:20); `RemoteCapturePlanner.KnownTargets` = `[("Webex", "CiscoCollabHost"), ("Zoom", "Zoom")]` (`src\LocalScribe.Core\Live\RemoteCapturePlanner.cs:26-27` — note the image for the "Webex" friendly entry is `CiscoCollabHost`, so a pinned App of literally `"Webex"` has NO matching option and reproduces the bug); pattern precedent: the Settings mic picker's synthetic "(not connected)" insert (`SettingsPageViewModel.cs:375-379`).
- Produces: `OptionFor(RemoteSetting)` that never returns an option absent from `RemoteTargetOptions`; the synthesized row is re-inserted on every rebuild (the :184 fallback re-enters `OptionFor`). No signature changes.

**Steps:**

- [ ] Add the failing tests to `RecordingConsoleViewModelTests.cs` (helpers `MakeConsole`, `PerProcess`, `Auto` exist at :35-74):

```csharp
    [Fact]
    public async Task Pinned_unknown_app_option_is_inserted_into_the_picker_and_survives_rebuilds()
    {
        // UX round 2026-08-02 item 3.2: a per-process pin for an app that is not currently
        // rendering audio and is not a KnownTargets image ("Webex" here - KnownTargets images
        // are only CiscoCollabHost/Zoom) used to get a DETACHED RemoteTargetOption. WPF cannot
        // select an item that is not in ItemsSource, so both console pickers painted blank and
        // the 2 s rebuild never self-healed.
        var (console, _, _, _, _, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.NotNull(console.SelectedRemoteTarget);
        Assert.Contains(console.SelectedRemoteTarget, console.RemoteTargetOptions);
        Assert.Equal("Webex", console.SelectedRemoteTarget.Setting.App);
        Assert.True(console.RemoteTargetOptions[^1].IsSystemMix);   // System mix stays last

        // The visible-poll rebuild must RE-insert it (no scan hit: _scanner.Active is empty).
        await console.RefreshRemoteTargetsAsync();
        Assert.Contains(console.SelectedRemoteTarget, console.RemoteTargetOptions);
        Assert.Equal("Webex", console.SelectedRemoteTarget.Setting.App);
        Assert.True(console.RemoteTargetOptions[^1].IsSystemMix);
    }

    [Fact]
    public void Detected_target_with_no_picker_entry_is_inserted_not_detached()
    {
        var (console, _, _, _, _, _, _) = MakeConsole(Auto(null));
        console.ApplyDetectedTarget("SomeNewCallApp");
        Assert.Contains(console.SelectedRemoteTarget, console.RemoteTargetOptions);
        Assert.Equal("SomeNewCallApp", console.SelectedRemoteTarget.Setting.App);
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~RecordingConsoleViewModelTests"` — expect both new tests to FAIL with `Assert.Contains() Failure ... Not found: RemoteTargetOption { Label = Webex ... }` (and `SomeNewCallApp` respectively).

- [ ] Implement: replace `OptionFor` (RecordingConsoleViewModel.cs:189-200):

```csharp
    /// <summary>The option matching a RemoteSetting, creating an app option if the image is not in
    /// the current list (an unknown pinned app). Blank-dropdown fix (UX round 2026-08-02 item
    /// 3.2): the created option is INSERTED into RemoteTargetOptions (just above the trailing
    /// System-mix row - the Settings mic picker's "(not connected)" precedent) instead of being
    /// returned detached; a detached selection is unselectable by WPF and painted both console
    /// pickers blank. RebuildRemoteTargetOptions' fallback path re-enters here, so the synthesized
    /// row is re-inserted on every 2 s rebuild and the selection stays a list member.</summary>
    private RemoteTargetOption OptionFor(RemoteSetting r)
    {
        if (r.Mode == RemoteMode.SystemMix)
            return RemoteTargetOptions.First(o => o.IsSystemMix);
        if (r.Mode == RemoteMode.PerProcess && !string.IsNullOrEmpty(r.App))
        {
            var match = RemoteTargetOptions.FirstOrDefault(o => o.Setting.Mode == RemoteMode.PerProcess
                    && string.Equals(o.Setting.App, r.App, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
            var synthesized = new RemoteTargetOption(r.App!,
                new RemoteSetting { Mode = RemoteMode.PerProcess, App = r.App }, false);
            RemoteTargetOptions.Insert(RemoteTargetOptions.Count - 1, synthesized);
            return synthesized;
        }
        return RemoteTargetOptions.First(o => o.Setting.Mode == RemoteMode.Auto);
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~RecordingConsoleViewModelTests"` — expect ALL PASS (the pre-existing `Seeds_selection_and_override_from_settings` and `ApplyDetectedTarget_selects_and_arms_the_override_only_while_idle` keep passing — they assert `Setting.App` only, which is unchanged).

- [ ] Run the sibling suite that shares the ctor: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~RecordingConsoleAppSelectorTests"` — expect PASS.

- [ ] Commit:
```
git add src\LocalScribe.App\ViewModels\RecordingConsoleViewModel.cs tests\LocalScribe.App.Tests\RecordingConsoleViewModelTests.cs
git commit -m "fix(console): pinned-but-not-running remote target stays selectable in the picker"
```

---

### Task 3: Session Details > roster pickers — "(choose a person)" sentinel + Add gating

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\MetadataEditorViewModel.cs` (selection fields :108-109, command construction :191-194, ctor tail near :197, `Attach` else-branch :278, `RefreshMatterDataAsync` dispatch :691-697)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\MetadataEditorSpeakerListsTests.cs` (new tests + SpinWait updates :147/:162/:179), `F:\LocalScribe\tests\LocalScribe.App.Tests\MetadataEditorViewModelTests.cs` (SpinWait update :193)

**Interfaces:**
- Consumes: `sealed record RosterPick(string MatterId, string MemberId, string Display)` (MetadataEditorViewModel.cs:13 — record, so value equality drives `Contains`); `ObservableCollection<RosterPick> RosterPicks` (:116); `AddFromRoster(string matterId, string rosterMemberId, SourceKind side)` (:338); the two `IAsyncRelayCommand` properties `AddLocalFromRosterCommand`/`AddRemoteFromRosterCommand` (:132-133).
- Produces: `public static readonly RosterPick ChoosePersonSentinel` (value `new("", "", "(choose a person)")`) — Task 12's runbook references it; `AddLocalFromRosterCommand`/`AddRemoteFromRosterCommand` gain CanExecute gates (false while the sentinel is selected).

**Steps:**

- [ ] Add the failing test to `MetadataEditorSpeakerListsTests.cs` (harness `MakeEditor` :45-46, `SeedSessionTaggedToMatterWithRoster` :87-110 exist):

```csharp
    [Fact]
    public async Task Roster_pickers_default_to_the_choose_person_sentinel_and_gate_Add()
    {
        // UX round 2026-08-02 item 3.3: both "Add from roster" boxes were ALWAYS blank (nullable
        // selection, no default, Clear()+refill with no re-assert). Auto-selecting a real person
        // risks mis-adding to an evidentiary participant list, so the default is a sentinel row
        // and Add stays disabled until a real person is picked.
        string id = await SeedSessionTaggedToMatterWithRoster("Paralegal");
        var editor = MakeEditor();

        // At construction, before any load lands: sentinel seeded and selected on both sides.
        Assert.Contains(MetadataEditorViewModel.ChoosePersonSentinel, editor.RosterPicks);
        Assert.Equal(MetadataEditorViewModel.ChoosePersonSentinel, editor.LocalSelectedRosterPick);
        Assert.Equal(MetadataEditorViewModel.ChoosePersonSentinel, editor.RemoteSelectedRosterPick);
        Assert.False(editor.AddLocalFromRosterCommand.CanExecute(null));
        Assert.False(editor.AddRemoteFromRosterCommand.CanExecute(null));

        await editor.LoadAsync(id, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => editor.RosterPicks.Count > 1, TimeSpan.FromSeconds(10)));

        // After the roster refresh: sentinel row first, still selected, Add still gated.
        Assert.Equal(MetadataEditorViewModel.ChoosePersonSentinel, editor.RosterPicks[0]);
        Assert.Equal(MetadataEditorViewModel.ChoosePersonSentinel, editor.LocalSelectedRosterPick);
        Assert.False(editor.AddLocalFromRosterCommand.CanExecute(null));

        // A real pick enables Add and adding still works exactly as before.
        editor.LocalSelectedRosterPick = editor.RosterPicks.First(r => r.Display.Contains("Paralegal"));
        Assert.True(editor.AddLocalFromRosterCommand.CanExecute(null));
        await editor.AddLocalFromRosterCommand.ExecuteAsync(null);
        Assert.Contains(editor.Participants, p => p.Name == "Paralegal");
    }

    [Fact]
    public async Task Roster_refresh_keeps_a_real_selection_and_reasserts_it()
    {
        string id = await SeedSessionTaggedToMatterWithRoster("Paralegal");
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => editor.RosterPicks.Count > 1, TimeSpan.FromSeconds(10)));
        editor.LocalSelectedRosterPick = editor.RosterPicks.First(r => r.Display.Contains("Paralegal"));

        // A second load Clear()+refills RosterPicks; the still-offered pick must survive by value
        // (RosterPick is a record) instead of blanking.
        await editor.LoadAsync(id, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => editor.RosterPicks.Count > 1, TimeSpan.FromSeconds(10)));
        Assert.NotNull(editor.LocalSelectedRosterPick);
        Assert.Contains(editor.LocalSelectedRosterPick!, editor.RosterPicks);
        Assert.Contains("Paralegal", editor.LocalSelectedRosterPick!.Display);
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~MetadataEditorSpeakerListsTests"` — expect the two new tests to FAIL compiling? No — `ChoosePersonSentinel` does not exist yet, so expect a COMPILE error `CS0117: 'MetadataEditorViewModel' does not contain a definition for 'ChoosePersonSentinel'`. That is the failing state for this cycle.

- [ ] Implement in `MetadataEditorViewModel.cs`:

  1. Below the `RosterPicks` declaration (:116), add:

```csharp
    /// <summary>Default row for both "Add from roster" pickers (UX round 2026-08-02 item 3.3):
    /// a nullable selection with no default painted a permanently blank box, and auto-selecting
    /// a real person would risk one mis-click adding the wrong person to an evidentiary
    /// participant list. Selected by default, re-asserted after every roster refresh; both Add
    /// commands are disabled while it is selected. Static readonly (not const - records cannot
    /// be const); matched by value like every other RosterPick.</summary>
    public static readonly RosterPick ChoosePersonSentinel = new("", "", "(choose a person)");
```

  2. Change the two selection fields (:108-109) to default to the sentinel:

```csharp
    [ObservableProperty] private RosterPick? _localSelectedRosterPick = ChoosePersonSentinel;
    [ObservableProperty] private RosterPick? _remoteSelectedRosterPick = ChoosePersonSentinel;
```

  3. Replace the two command constructions (:191-194) with gated versions, and add the partial change handlers next to them (anywhere in the class body):

```csharp
        AddLocalFromRosterCommand = new AsyncRelayCommand(
            () => LocalSelectedRosterPick is { } p && p != ChoosePersonSentinel
                ? AddFromRoster(p.MatterId, p.MemberId, SourceKind.Local) : Task.CompletedTask,
            () => LocalSelectedRosterPick is { } p && p != ChoosePersonSentinel);
        AddRemoteFromRosterCommand = new AsyncRelayCommand(
            () => RemoteSelectedRosterPick is { } p && p != ChoosePersonSentinel
                ? AddFromRoster(p.MatterId, p.MemberId, SourceKind.Remote) : Task.CompletedTask,
            () => RemoteSelectedRosterPick is { } p && p != ChoosePersonSentinel);
```

```csharp
    partial void OnLocalSelectedRosterPickChanged(RosterPick? value)
        => AddLocalFromRosterCommand.NotifyCanExecuteChanged();

    partial void OnRemoteSelectedRosterPickChanged(RosterPick? value)
        => AddRemoteFromRosterCommand.NotifyCanExecuteChanged();
```

  4. At the END of the constructor (after the last command assignment), seed the collection:

```csharp
        RosterPicks.Add(ChoosePersonSentinel);   // never an empty ItemsSource (item 3.3)
```

  5. In `Attach` (:278), extend the detach branch:

```csharp
        else
        {
            MatterOptions.Clear(); TaggedMatters.Clear();
            RosterPicks.Clear();
            RosterPicks.Add(ChoosePersonSentinel);
            LocalSelectedRosterPick = ChoosePersonSentinel;
            RemoteSelectedRosterPick = ChoosePersonSentinel;
        }
```

  6. In `RefreshMatterDataAsync` (:691-697), replace the dispatch body:

```csharp
            _dispatch(() =>
            {
                _matterEntries = index.Matters;
                RebuildMatterOptions();
                var keepLocal = LocalSelectedRosterPick;
                var keepRemote = RemoteSelectedRosterPick;
                RosterPicks.Clear();
                RosterPicks.Add(ChoosePersonSentinel);
                foreach (var p in picks) RosterPicks.Add(p);
                // Re-assert after the rebuild (item 3.3): keep a still-offered pick by value,
                // else fall back to the sentinel. The generated setters equality-gate, so
                // re-raise manually for the unchanged case - the bound ComboBox can null its
                // selection on Clear() and needs the re-point either way.
                LocalSelectedRosterPick = keepLocal is not null && RosterPicks.Contains(keepLocal)
                    ? keepLocal : ChoosePersonSentinel;
                OnPropertyChanged(nameof(LocalSelectedRosterPick));
                RemoteSelectedRosterPick = keepRemote is not null && RosterPicks.Contains(keepRemote)
                    ? keepRemote : ChoosePersonSentinel;
                OnPropertyChanged(nameof(RemoteSelectedRosterPick));
            });
```

- [ ] Update the three pre-existing SpinWaits that now see the sentinel occupy one slot (they would otherwise pass instantly and race the refresh, or hang):
  - `MetadataEditorSpeakerListsTests.cs:147`: `editor.RosterPicks.Count > 0` -> `editor.RosterPicks.Count > 1`
  - `MetadataEditorSpeakerListsTests.cs:162`: `editor.RosterPicks.Count > 0` -> `editor.RosterPicks.Count > 1`
  - `MetadataEditorSpeakerListsTests.cs:179`: `editor.RosterPicks.Count >= 2` -> `editor.RosterPicks.Count >= 3`
  - `MetadataEditorViewModelTests.cs:193`: `ed.RosterPicks.Count == 1` -> `ed.RosterPicks.Count == 2`

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~MetadataEditorSpeakerListsTests"` then `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~MetadataEditorViewModelTests"` — expect ALL PASS. Also run the other MetadataEditor suites (they share the VM): `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~MetadataEditor"` — expect ALL PASS.

- [ ] Commit:
```
git add src\LocalScribe.App\ViewModels\MetadataEditorViewModel.cs tests\LocalScribe.App.Tests\MetadataEditorSpeakerListsTests.cs tests\LocalScribe.App.Tests\MetadataEditorViewModelTests.cs
git commit -m "fix(sessiondetails): roster pickers default to a (choose a person) sentinel with Add gating"
```

---

### Task 4: Assistant panel > summary version — "(no summaries yet)" sentinel

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\AssistantTabViewModel.cs` (field block :40-53, `OnSelectedVersionChanged` :57-65, `LoadAsync` dispatch :97-99, `RegenerateAsync` insert :115)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\AssistantTabViewModelTests.cs` (new test + updates at :133 and :201)

**Interfaces:**
- Consumes: `sealed record SummaryVersion(string Id, DateTimeOffset CreatedAt, string SourceTranscriptVersion, AssistantModelRef Model, int PromptVersion, string ContentMarkdown, bool Stale, bool CudaFellToCpu = false)` (`src\LocalScribe.Core\Assistant\SummaryStore.cs:12-14`); `sealed record AssistantModelRef(string File, string Sha256, string Backend)` (`src\LocalScribe.Core\Assistant\AssistantModels.cs:13`); the panel binds `ItemsSource="{Binding Summary.Versions}"` with `DisplayMemberPath="Id"` (`src\LocalScribe.App\Controls\AssistantSidePanel.xaml:47-49`), so the sentinel's Id IS its display text.
- Produces: `public static readonly SummaryVersion NoSummariesSentinel` on `AssistantTabViewModel` (reference-matched, never value-matched); `HasSummary`/`ContentText`/`VersionInfo`/`IsStale` stay false/empty while the sentinel is selected.

**Steps:**

- [ ] Add the failing test to `AssistantTabViewModelTests.cs` (harness `MakeVm` at :46-62):

```csharp
    [Fact]
    public async Task No_summaries_yet_selects_the_sentinel_instead_of_a_blank_box()
    {
        // UX round 2026-08-02 item 3.4: a session that has never been summarised (the
        // overwhelmingly common case) left Versions empty and SelectedVersion null - a
        // permanently blank ComboBox next to Regenerate.
        var vm = MakeVm();
        await vm.LoadAsync("s1", CancellationToken.None);

        var only = Assert.Single(vm.Versions);
        Assert.Same(AssistantTabViewModel.NoSummariesSentinel, only);
        Assert.Same(AssistantTabViewModel.NoSummariesSentinel, vm.SelectedVersion);
        Assert.Equal("(no summaries yet)", only.Id);   // DisplayMemberPath=Id renders this text
        Assert.False(vm.HasSummary);                   // the empty state is otherwise unchanged
        Assert.Equal("", vm.VersionInfo);
        Assert.Equal("", vm.ContentText);
        Assert.False(vm.IsStale);
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~AssistantTabViewModelTests"` — expect a COMPILE error `CS0117: 'AssistantTabViewModel' does not contain a definition for 'NoSummariesSentinel'`.

- [ ] Implement in `AssistantTabViewModel.cs`:

  1. Below the `Versions` declaration (:40), add:

```csharp
    /// <summary>Placeholder row for a session with no summaries yet (UX round 2026-08-02 item
    /// 3.4) - an empty ItemsSource painted a permanently blank ComboBox next to Regenerate.
    /// Matched by REFERENCE everywhere (SummaryVersion is a record with value equality, and a
    /// real version must never be mistaken for the placeholder). Selecting it renders the
    /// existing empty state: HasSummary stays false, nothing is persisted or displayed.</summary>
    public static readonly SummaryVersion NoSummariesSentinel = new("(no summaries yet)",
        DateTimeOffset.MinValue, "", new AssistantModelRef("", "", ""), 0, "", false);
```

  2. Replace `OnSelectedVersionChanged` (:57-65):

```csharp
    partial void OnSelectedVersionChanged(SummaryVersion? value)
    {
        bool isSentinel = ReferenceEquals(value, NoSummariesSentinel);
        ContentText = isSentinel ? "" : value?.ContentMarkdown ?? "";
        IsStale = !isSentinel && (value?.Stale ?? false);
        HasSummary = value is not null && !isSentinel;
        VersionInfo = value is null || isSentinel ? "" : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{value.Id} \u00B7 {value.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} \u00B7 {value.Model.File} ({value.Model.Backend.ToUpperInvariant()}{(value.CudaFellToCpu ? " - GPU unavailable, fell to CPU" : "")}) \u00B7 transcript {value.SourceTranscriptVersion}");
    }
```

  3. In the `LoadAsync` dispatch (:97-99), replace the refill tail:

```csharp
                Versions.Clear();
                foreach (var v in versions.Reverse()) Versions.Add(v);   // newest first
                if (Versions.Count == 0) Versions.Add(NoSummariesSentinel);   // item 3.4
                SelectedVersion = Versions.FirstOrDefault();
                OnPropertyChanged(nameof(SelectedVersion));   // re-point after Clear(): gated setter
```

  4. In `RegenerateAsync` (:115), remove the sentinel when the first real version lands:

```csharp
            _dispatch(() => { Versions.Remove(NoSummariesSentinel); Versions.Insert(0, v); SelectedVersion = v; });
```

- [ ] Update the two pre-existing asserts that pinned the empty-collection state (deliberate behaviour change):
  - `AssistantTabViewModelTests.cs:133` — in `Regenerate_streams_persists_and_selects_the_new_version_with_the_label`, replace `Assert.Empty(vm.Versions);` with:

```csharp
        Assert.Same(AssistantTabViewModel.NoSummariesSentinel, Assert.Single(vm.Versions));
```

  - `AssistantTabViewModelTests.cs:201` — in `Error_is_visible_and_persists_nothing`, replace `Assert.Empty(vm.Versions);` with:

```csharp
        Assert.Same(AssistantTabViewModel.NoSummariesSentinel, Assert.Single(vm.Versions));
        Assert.False(vm.HasSummary);
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~AssistantTabViewModelTests"` — expect ALL PASS (the regenerate test's `Assert.Single(vm.Versions)` after the run still holds: sentinel removed, one real version).

- [ ] Commit:
```
git add src\LocalScribe.App\ViewModels\AssistantTabViewModel.cs tests\LocalScribe.App.Tests\AssistantTabViewModelTests.cs
git commit -m "fix(assistant): summary version picker shows (no summaries yet) instead of blank"
```

---

### Task 5: Assistant panel > chat thread — "(no conversations yet)" sentinel

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\AssistantChatThreadsViewModel.cs` (record :11-14 untouched; ctor :43-63, `OnSelectedThreadChanged` :65-74, `LoadAsync` dispatch :91-99, command gates :48-59)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\AssistantChatThreadsViewModelTests.cs` (new test + rewrite of `Archive_last_thread_leaves_no_selection` :238-251)

**Interfaces:**
- Consumes: `sealed record ThreadListItem(string Id, string Name, bool Archived, bool HasRecap)` with `Display` (:11-14); `AssistantChatViewModel.SelectThreadAsync(string id, CancellationToken ct)` (called fire-and-forget at :73); the panel ComboBox binds `Threads.Threads`/`Threads.SelectedThread` with `DisplayMemberPath="Display"` (`AssistantSidePanel.xaml:117-119`).
- Produces: `public static readonly ThreadListItem NoThreadsSentinel` (Id `""` — the "is sentinel" marker: every real thread id is a minted GUID string); `BeginRenameCommand`/`ArchiveCommand` CanExecute exclude the sentinel.

**Steps:**

- [ ] Add the failing test to `AssistantChatThreadsViewModelTests.cs` (harness `Make()` at :41-51):

```csharp
    [Fact]
    public async Task Empty_store_selects_the_no_conversations_sentinel()
    {
        // UX round 2026-08-02 item 3.5: first ever use (no chats.json) left the thread picker
        // blank until the first ask minted "Chat 1". The sentinel is seeded at construction
        // (never a blank first paint) and survives a load over an empty store.
        var (vm, _, _, reporter, _) = Make();

        Assert.NotNull(vm.SelectedThread);                       // at construction, pre-load
        Assert.Contains(vm.SelectedThread, vm.Threads);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Empty(reporter.Errors);
        var only = Assert.Single(vm.Threads);
        Assert.Same(AssistantChatThreadsViewModel.NoThreadsSentinel, only);
        Assert.Same(AssistantChatThreadsViewModel.NoThreadsSentinel, vm.SelectedThread);
        Assert.Equal("(no conversations yet)", only.Display);
        Assert.False(vm.BeginRenameCommand.CanExecute(null));    // no thread to rename
        Assert.False(vm.ArchiveCommand.CanExecute(null));        // nor to archive
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~AssistantChatThreadsViewModelTests"` — expect a COMPILE error `CS0117: 'AssistantChatThreadsViewModel' does not contain a definition for 'NoThreadsSentinel'`.

- [ ] Implement in `AssistantChatThreadsViewModel.cs`:

  1. Below the `Threads` declaration (:28), add:

```csharp
    /// <summary>Placeholder row while the scope has no selectable thread - first ever use, or
    /// every listed thread archived (UX round 2026-08-02 item 3.5). Id "" marks it: real ids
    /// are minted GUID strings, so the commands gate on Id.Length > 0 and
    /// OnSelectedThreadChanged never asks Chat to load it. The next ask still mints "Chat 1"
    /// service-side and the TurnCompleted refresh adopts it, exactly as before.</summary>
    public static readonly ThreadListItem NoThreadsSentinel =
        new("", "(no conversations yet)", false, false);
```

  2. In the ctor, replace the `BeginRenameCommand` and `ArchiveCommand` constructions (:48-57):

```csharp
        BeginRenameCommand = new RelayCommand(() =>
        {
            if (SelectedThread is not { } t || t.Id.Length == 0) return;
            RenameText = t.Name;
            IsRenaming = true;
        }, () => SelectedThread is { Id.Length: > 0 });
        CommitRenameCommand = new AsyncRelayCommand(CommitRenameAsync);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        ArchiveCommand = new AsyncRelayCommand(
            () => SetArchivedAsync(archived: true),
            () => SelectedThread is { Archived: false, Id.Length: > 0 });
```

  3. At the END of the ctor (after the `Chat.TurnCompleted` subscription), seed:

```csharp
        // Sentinel seed (item 3.5): the picker must never paint blank - not before the first
        // LoadAsync lands and not on an empty store. Backing-field write, not the property:
        // there is no thread to select yet, so OnSelectedThreadChanged's side effects
        // (Chat.SelectThreadAsync, CanExecute notifications) must not fire during construction.
        Threads.Add(NoThreadsSentinel);
        _selectedThread = NoThreadsSentinel;
```

  4. In `OnSelectedThreadChanged` (:65-74), guard the Chat call (last line only changes):

```csharp
        if (value is { Id.Length: > 0 }) _ = Chat.SelectThreadAsync(value.Id, CancellationToken.None);
```

  5. In the `LoadAsync` dispatch (:91-99), replace the body:

```csharp
            _dispatch(() =>
            {
                string? keep = SelectedThread?.Id;
                Threads.Clear();
                foreach (var i in items) Threads.Add(i);
                HasAnyHistory = anyHistory;
                var selected = items.FirstOrDefault(i => i.Id == keep)
                    ?? items.FirstOrDefault(i => !i.Archived);
                if (selected is null)
                {
                    // Empty scope, or every listed thread is archived: keep the picker
                    // non-blank with the sentinel row (item 3.5).
                    Threads.Add(NoThreadsSentinel);
                    selected = NoThreadsSentinel;
                }
                SelectedThread = selected;
                OnPropertyChanged(nameof(SelectedThread));   // re-point after Clear(): gated setter
            });
```

- [ ] Rewrite the pre-existing `Archive_last_thread_leaves_no_selection` (:238-251) — deliberate behaviour change from null-selection to sentinel:

```csharp
    [Fact]
    public async Task Archive_last_thread_falls_back_to_the_sentinel()
    {
        var (vm, _, store, reporter, _) = Make();
        var only = AssistantChatStore.NewThread("Only", DateTimeOffset.UtcNow);
        await store.SaveAsync(new AssistantChatLog { Chats = [only] }, CancellationToken.None);
        await vm.LoadAsync(CancellationToken.None);

        await vm.ArchiveCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        var row = Assert.Single(vm.Threads);
        Assert.Same(AssistantChatThreadsViewModel.NoThreadsSentinel, row);
        Assert.Same(AssistantChatThreadsViewModel.NoThreadsSentinel, vm.SelectedThread);
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~AssistantChatThreadsViewModelTests"` — expect ALL PASS (`LoadAsync_lists_non_archived_and_selects_first` keeps passing: two real threads, no sentinel, count still 2).

- [ ] Commit:
```
git add src\LocalScribe.App\ViewModels\AssistantChatThreadsViewModel.cs tests\LocalScribe.App.Tests\AssistantChatThreadsViewModelTests.cs
git commit -m "fix(assistant): chat thread picker shows (no conversations yet) instead of blank"
```

---

### Task 6: Sessions page > matter filter — adopt the SearchPage "" sentinel + unconditional re-assert

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs` (`_matterFilterId` :86, `PassesFilters` :355-364, `RebuildMatterOptions` :366-403)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs` (new test + update `Filters_recompute_rows_from_cached_list` :162-174), `F:\LocalScribe\tests\LocalScribe.App.Tests\SessionsPageMatterFilterSearchTests.cs` (doc-comment only, :13)

**Interfaces:**
- Consumes: `sealed record MatterFilterOption(string? Id, string Label)` (SessionsPageViewModel.cs:14 — shared with SearchPage); `public const string NoMatterSentinel = "(none)"` (:26); the SearchPage convention documented at `SearchPageViewModel.cs:57-59` ("" = all; null SelectedValue cannot select a ComboBox item) and its re-assert idiom at :158-161; XAML binding `SelectedValuePath="Id" SelectedValue="{Binding MatterFilterId}"` (`Pages\SessionsPage.xaml:41-42`, no XAML change needed).
- Produces: `MatterFilterId` defaults to `""`; `""` (and transient null) mean "All matters"; the "All matters" option carries `Id=""`; `RebuildMatterOptions` ends with an unconditional PropertyChanged raise for `MatterFilterId`.

**Steps:**

- [ ] Add the failing test to `SessionsPageViewModelTests.cs` (helpers `MakeVm`/`Rec`/`Meta`/`WriteSessionAsync` at :51-90):

```csharp
    [Fact]
    public async Task Matter_filter_defaults_to_the_selectable_All_sentinel_and_reasserts_after_rebuild()
    {
        // UX round 2026-08-02 item 3.6 (settled): the "All matters" sentinel becomes Id="" -
        // SearchPageViewModel.cs:57-59 documents that a null SelectedValue cannot select a
        // ComboBox item, which is exactly why this filter painted blank; and the old
        // null==null re-assert was a no-op, so nothing re-pointed the selection after Clear().
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-1", t, 480), Meta("Tagged", matterIds: "M-2026-001"));
        await WriteSessionAsync(Rec("s-2", t.AddHours(1), 480), Meta("Untagged"));
        var (vm, _, _, _) = MakeVm();

        Assert.Equal("", vm.MatterFilterId);                    // construction default: "", not null

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(vm.MatterFilterId)) raised.Add(vm.MatterFilterId); };

        await vm.OnNavigatedToAsync();

        Assert.Equal("", vm.MatterFilterId);                    // still the sentinel after rebuild
        Assert.Contains(vm.MatterFilterOptions, o => o.Id == "" && o.Label == "All matters");
        Assert.Contains(vm.MatterFilterOptions, o => o.Id == vm.MatterFilterId);   // member of list
        Assert.Contains("", raised);   // UNCONDITIONAL re-assert: raised even though unchanged
        Assert.Equal(2, vm.Rows.Count);                         // sentinel-consumption: "" = ALL
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SessionsPageViewModelTests"` — expect the new test to FAIL with `Assert.Equal() Failure ... Expected: "" / Actual: (null)` on the first assert.

- [ ] Implement in `SessionsPageViewModel.cs`:

  1. Default the field (:86):

```csharp
    // "" = All matters (the WPF-selectable sentinel - SearchPageViewModel.cs:57-59's rule,
    // adopted here by UX round 2026-08-02 item 3.6; the old null sentinel could never be
    // re-selected after a Clear()). Nullable stays: WPF can write a transient null through
    // SelectedValue during a rebuild, and PassesFilters treats it as All.
    [ObservableProperty] private string? _matterFilterId = "";
```

  2. In `PassesFilters` (:361-362), only a REAL id filters:

```csharp
        if (MatterFilterId == NoMatterSentinel) return row.MatterIds.Count == 0;
        // "" (and a transient null from a ComboBox Clear() writeback) mean ALL matters.
        if (MatterFilterId is { Length: > 0 } matterId && !row.MatterIds.Contains(matterId)) return false;
        return true;
```

  3. In `RebuildMatterOptions` (:366-403): change the `selectedLabel` switch's null arm, the "All matters" option, the stale check, and the re-assert tail:

```csharp
        string selectedLabel = current switch
        {
            null or "" => "All matters",
            NoMatterSentinel => "No matter",
            _ => MatterLabel(current),
        };
        if (string.Equals(query, selectedLabel, StringComparison.Ordinal)) query = "";
        MatterFilterOptions.Clear();
        MatterFilterOptions.Add(new MatterFilterOption("", "All matters"));
        MatterFilterOptions.Add(new MatterFilterOption(NoMatterSentinel, "No matter"));
```

     and, replacing the tail at :395-402 (keep the sanctioned-exception comment, reworded for ""):

```csharp
        // Sanctioned exception (design 2): this fallback cascades OnMatterFilterIdChanged ->
        // ApplyFilters -> Rows.Clear() (a Reset), the ONE case UpsertRowAsync's never-Reset
        // guarantee doesn't cover - the active specific-matter filter just lost its last
        // session, so it legitimately falls back to "All matters" and the list changes anyway.
        if (current is { Length: > 0 } && MatterFilterOptions.All(o => o.Id != current))
            MatterFilterId = "";        // stale filter (matter no longer tagged anywhere) -> All
        else if (MatterFilterId != current)
            MatterFilterId = current;   // re-assert: a bound ComboBox can null selection on Clear()
        else
            // Unconditional re-assert (item 3.6): the equality-gated generated setter is a no-op
            // for an unchanged value, but the bound ComboBox still needs the re-point after Clear().
            OnPropertyChanged(nameof(MatterFilterId));
```

  4. Note: `NoMatterSentinel` (`"(none)"`) has `Length > 0` but is always present in the rebuilt options, so the stale check never fires for it — no special-casing needed.

- [ ] Update `Filters_recompute_rows_from_cached_list` (SessionsPageViewModelTests.cs:162-174) to the new convention:
  - `:166` `vm.MatterFilterId = null;` -> `vm.MatterFilterId = "";`
  - `:173-174` becomes:

```csharp
        Assert.Equal(new string?[] { "", SessionsPageViewModel.NoMatterSentinel, "M-2026-001" },
            vm.MatterFilterOptions.Select(o => o.Id).ToArray());
```

- [ ] Update the stale doc comment at `SessionsPageMatterFilterSearchTests.cs:13`: replace `(MatterFilterId -> ApplyFilters, null = all)` with `(MatterFilterId -> ApplyFilters, "" = all)`.

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SessionsPageViewModelTests"`, then `--filter "FullyQualifiedName~SessionsPageMatterFilterSearchTests"`, then `--filter "FullyQualifiedName~SessionsPage"` (covers content-filter and label suites too) — expect ALL PASS.

- [ ] Commit:
```
git add src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs tests\LocalScribe.App.Tests\SessionsPageMatterFilterSearchTests.cs
git commit -m "fix(sessions): matter filter adopts the empty-string All sentinel with unconditional re-assert"
```

---

### Task 7: Search page > matter facet — seed the sentinel at construction

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\SearchPageViewModel.cs` (ctor :83-102)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\SearchPageViewModelTests.cs`

**Interfaces:**
- Consumes: `ObservableCollection<MatterFilterOption> MatterOptions` (:49); `OnNavigatedToAsync` (:143-165) which already Clear()s and re-adds `new MatterFilterOption("", "All matters")` and re-asserts; `_matterFilterId` default `""` (:66).
- Produces: `MatterOptions` non-empty from the instant of construction (one "All matters" row). No new members.

**Steps:**

- [ ] Add the failing test to `SearchPageViewModelTests.cs` (harness `MakeVmAsync` at :68-80):

```csharp
    [Fact]
    public async Task Matter_facet_offers_the_All_sentinel_before_the_first_navigation_load()
    {
        // UX round 2026-08-02 item 3.7: MatterOptions was empty until RefreshMattersAsync landed
        // on page navigation, so the facet's first paint was blank. Seed the sentinel at
        // construction; OnNavigatedToAsync still rebuilds wholesale (no duplicate).
        var (vm, _, _) = await MakeVmAsync();

        var seeded = Assert.Single(vm.MatterOptions);
        Assert.Equal("", seeded.Id);
        Assert.Equal("All matters", seeded.Label);
        Assert.Equal("", vm.MatterFilterId);
        Assert.Contains(vm.MatterOptions, o => o.Id == vm.MatterFilterId);   // member at construction

        await vm.OnNavigatedToAsync();
        Assert.Single(vm.MatterOptions, o => o.Id == "");    // rebuilt, not duplicated
        Assert.Equal("", vm.MatterFilterId);
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SearchPageViewModelTests"` — expect the new test to FAIL with `Assert.Single() Failure: The collection was empty`.

- [ ] Implement: at the end of the `SearchPageViewModel` ctor (after the `index.ReadyChanged` subscription block ending at :101), add:

```csharp
        // Seed the All-matters sentinel at construction (UX round 2026-08-02 item 3.7) so the
        // facet's first paint is never blank while RefreshMattersAsync loads on navigation;
        // OnNavigatedToAsync rebuilds the list wholesale and re-asserts, exactly as before.
        MatterOptions.Add(new MatterFilterOption("", "All matters"));
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SearchPageViewModelTests"` then `--filter "FullyQualifiedName~SearchPageViewModelSemanticTests"` — expect ALL PASS.

- [ ] Commit:
```
git add src\LocalScribe.App\ViewModels\SearchPageViewModel.cs tests\LocalScribe.App.Tests\SearchPageViewModelTests.cs
git commit -m "fix(search): seed the All-matters facet sentinel at construction"
```

---

### Task 8: Import / Re-transcribe model pickers — disabled "(no models found)" sentinel

**Cross-plan prerequisite:** `2026-08-02-model-descriptions-plan.md` has landed — `ModelChoices` on both dialog VMs is the catalog projection `IReadOnlyList<WhisperModelInfo>` and both model combos already use `WhisperModelItemTemplate` + `SelectedValuePath="Name"`.

**Files:**
- Create: `F:\LocalScribe\src\LocalScribe.App\ViewModels\ModelPickerSentinel.cs`
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs` (ctor `ModelChoices` build + default selection, `CanStart`, request build), `F:\LocalScribe\src\LocalScribe.App\ViewModels\RetranscribeDialogViewModel.cs` (ctor build + `StartCommand` gate), `F:\LocalScribe\src\LocalScribe.App\ImportDialog.xaml` (model ComboBox), `F:\LocalScribe\src\LocalScribe.App\RetranscribeDialog.xaml` (model ComboBox)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\ImportDialogViewModelTests.cs`, `F:\LocalScribe\tests\LocalScribe.App.Tests\RetranscribeDialogViewModelTests.cs` (replace the empty-disk half of `ModelChoices_list_only_disk_models_and_gate_Start` as rewritten by model-descriptions Task 5)

**Interfaces:**
- Consumes: `record WhisperModelInfo(string Name, string Subtitle, int Rank, bool EnglishOnly)` and `WhisperModelCatalog.DescribeAll` (model-descriptions Task 1); the catalog-projected `ModelChoices` builds and string `SelectedModel` defaults (its Tasks 3/5); `WhisperModelItemTemplate` combos (its Tasks 4/6).
- Produces: `public static class ModelPickerSentinel { public const string NoModelsFound = "(no models found)"; }`; `public bool HasModels { get; }` on BOTH dialog VMs (XAML binds it to the ComboBox `IsEnabled`); Start stays disabled whenever `HasModels` is false; the sentinel row itself is a passthrough `WhisperModelInfo(NoModelsFound, "", int.MaxValue, false)`.

**Steps:**

- [ ] Add the failing tests. To `ImportDialogViewModelTests.cs` (harness `MakeVm` :64-79):

```csharp
    [Fact]
    public async Task Zero_models_on_disk_shows_the_disabled_sentinel_and_blocks_start()
    {
        // UX round 2026-08-02 item 3.8: an empty models folder left the picker an empty blank
        // box. Show a selected, disabled "(no models found)" row instead; Start stays disabled
        // even when every OTHER CanStart condition is satisfied.
        var (vm, decoder, _) = MakeVm(pickedPath: @"C:\evidence\call.mp3",
            models: new HashSet<string>());
        var only = Assert.Single(vm.ModelChoices);
        Assert.Equal(ModelPickerSentinel.NoModelsFound, only.Name);
        Assert.Equal("", only.Subtitle);                                     // renders as one line
        Assert.Equal(ModelPickerSentinel.NoModelsFound, vm.SelectedModel);   // selected, not blank
        Assert.False(vm.HasModels);                                          // XAML disables the box

        decoder.Probe = new AudioProbeResult
        {
            FormatName = "mp3", ClaimedChannels = 1,
            MediaCreatedUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
        };
        await vm.PickFileCommand.ExecuteAsync(null);       // file + title + date all satisfied
        Assert.False(vm.StartCommand.CanExecute(null));    // models alone gate Start
    }

    [Fact]
    public void Default_model_selection_is_always_a_member_of_the_choices()
    {
        var (vm, _, _) = MakeVm();
        Assert.True(vm.HasModels);
        Assert.NotNull(vm.SelectedModel);
        Assert.Contains(vm.ModelChoices, c => c.Name == vm.SelectedModel);
    }
```

  And in `RetranscribeDialogViewModelTests.cs`, replace the empty-disk block of `ModelChoices_list_only_disk_models_and_gate_Start` (as rewritten by model-descriptions Task 5 — the `Make(id, models: new HashSet<string>())` block asserting `Assert.Empty`/`Assert.Null`):

```csharp
        var (empty, _, _, _) = Make(id, models: new HashSet<string>());
        var only = Assert.Single(empty.ModelChoices);
        Assert.Equal(ModelPickerSentinel.NoModelsFound, only.Name);      // UX round item 3.8
        Assert.Equal(ModelPickerSentinel.NoModelsFound, empty.SelectedModel);   // selected, not blank
        Assert.False(empty.HasModels);                                   // XAML disables the box
        Assert.False(empty.StartCommand.CanExecute(null));               // nothing on disk -> no Start
        empty.Dispose();
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ImportDialogViewModelTests"` — expect a COMPILE error `CS0103: The name 'ModelPickerSentinel' does not exist in the current context`.

- [ ] Implement:

  1. Create `src\LocalScribe.App\ViewModels\ModelPickerSentinel.cs`:

```csharp
namespace LocalScribe.App.ViewModels;

/// <summary>The single disabled row both disk-model pickers (Import, Re-transcribe) show when
/// no ggml model is on disk (UX round 2026-08-02 item 3.8): an empty ItemsSource paints a blank
/// ComboBox that reads as a bug; a selected-but-disabled explanatory row does not. The row is
/// injected as a passthrough WhisperModelInfo (empty subtitle, worst rank) so the catalog-shaped
/// pickers render it one-line. Start stays gated off in both dialogs while this is the only
/// entry. Single-sourced so the two dialogs' sentinels can never drift (the
/// SearchPage-vs-SessionsPage sentinel divergence lesson).</summary>
public static class ModelPickerSentinel
{
    public const string NoModelsFound = "(no models found)";
}
```

  2. `ImportDialogViewModel.cs` ctor: directly after the `ModelChoices = WhisperModelCatalog.DescribeAll(availableModels());` line (landed by model-descriptions Task 3) and BEFORE the default-selection line, insert:

```csharp
        // Zero models -> a single disabled "(no models found)" row instead of an empty blank box
        // (UX round 2026-08-02 item 3.8); HasModels gates both the ComboBox and Start. The
        // default-selection line below then resolves SelectedModel to the sentinel's Name, so
        // the row shows selected, not blank.
        HasModels = ModelChoices.Count > 0;
        if (!HasModels)
            ModelChoices = [new WhisperModelInfo(ModelPickerSentinel.NoModelsFound, "", int.MaxValue, false)];
```

  3. `ImportDialogViewModel.cs`: add the property next to `ModelChoices` (:90):

```csharp
    /// <summary>False when no ggml model is on disk - the model ComboBox is disabled (its only
    /// row is the sentinel) and CanStart refuses (item 3.8).</summary>
    public bool HasModels { get; }
```

  4. `ImportDialogViewModel.cs` `CanStart` (:180-181):

```csharp
    private bool CanStart() => HasFile && !IsBusy && HasModels && Title.Trim().Length > 0
        && ParseRecordedAt() is not null;
```

  5. `ImportDialogViewModel.cs` request build (:261): replace `Model = SelectedModel,` with:

```csharp
                Model = HasModels ? SelectedModel : null,   // belt-and-braces: Start is gated anyway
```

  6. `RetranscribeDialogViewModel.cs` ctor: after the `ModelChoices = WhisperModelCatalog.DescribeAll(availableModels());` assignment (landed by model-descriptions Task 5) and BEFORE its best-Rank default line, insert:

```csharp
        // Item 3.8: the best-Rank default line below then selects the sentinel's Name.
        HasModels = ModelChoices.Count > 0;
        if (!HasModels)
            ModelChoices = [new WhisperModelInfo(ModelPickerSentinel.NoModelsFound, "", int.MaxValue, false)];
```

     change the `StartCommand` construction (:40) to:

```csharp
        StartCommand = new AsyncRelayCommand(StartAsync,
            () => HasModels && SelectedModel is not null && !IsRunning);
```

     and add next to `ModelChoices` (:57):

```csharp
    /// <summary>False when no ggml model is on disk - see ModelPickerSentinel (item 3.8).</summary>
    public bool HasModels { get; }
```

  7. `ImportDialog.xaml` — add `IsEnabled` to the model ComboBox landed by model-descriptions Task 4; final markup:

```xml
            <ComboBox ItemsSource="{Binding ModelChoices}"
                      ItemTemplate="{StaticResource WhisperModelItemTemplate}"
                      SelectedValuePath="Name" SelectedValue="{Binding SelectedModel}"
                      IsEnabled="{Binding HasModels}" />
```

  8. `RetranscribeDialog.xaml` — add `IsEnabled` to the model ComboBox landed by model-descriptions Task 6; final markup:

```xml
        <ComboBox ItemsSource="{Binding ModelChoices}"
                  ItemTemplate="{StaticResource WhisperModelItemTemplate}"
                  SelectedValuePath="Name" SelectedValue="{Binding SelectedModel}"
                  IsEnabled="{Binding HasModels}" Margin="0,0,0,8" />
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ImportDialogViewModelTests"`, `--filter "FullyQualifiedName~ImportDialogSpeakerDetectionTests"`, and `--filter "FullyQualifiedName~RetranscribeDialogViewModelTests"` — expect ALL PASS.

- [ ] Commit:
```
git add src\LocalScribe.App\ViewModels\ModelPickerSentinel.cs src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs src\LocalScribe.App\ViewModels\RetranscribeDialogViewModel.cs src\LocalScribe.App\ImportDialog.xaml src\LocalScribe.App\RetranscribeDialog.xaml tests\LocalScribe.App.Tests\ImportDialogViewModelTests.cs tests\LocalScribe.App.Tests\RetranscribeDialogViewModelTests.cs
git commit -m "fix(dialogs): model pickers show a disabled (no models found) sentinel when the models folder is empty"
```

---

### Task 9: Settings > Per-app target — editable-ComboBox watermark ("e.g. Webex, Zoom")

View-layer only (adorner + XAML) — no headless test is possible; this task ends with smoke-runbook checkboxes instead of a fake test.

**Files:**
- Create: `F:\LocalScribe\src\LocalScribe.App\ComboBoxWatermark.cs`, `F:\LocalScribe\docs\plans\2026-08-02-ux-round-smoke-runbook.md` (create if absent; if another plan in this round already created it, append the section instead)
- Modify: `F:\LocalScribe\src\LocalScribe.App\SettingsPage.xaml` (root element :1-4 for the xmlns, the Per-app combo :73-75)

**Interfaces:**
- Consumes: attached-behavior pattern precedent `src\LocalScribe.App\SegmentText.cs` (static class + `DependencyProperty.RegisterAttached`, namespace `LocalScribe.App`); the Per-app editable ComboBox (`SettingsPage.xaml:73-75`, `Text="{Binding RemoteApp, UpdateSourceTrigger=LostFocus}"`); `Settings.Remote.App` default null (`src\LocalScribe.Core\Model\Settings.cs:63-64`) so `RemoteApp` returns `""` out of the box (`SettingsPageViewModel.cs:326-328`).
- Produces: attached property `ComboBoxWatermark.Text` usable on any editable ComboBox; the spec-fixed watermark string `"e.g. Webex, Zoom"` on this one.

**Steps:**

- [ ] Create `src\LocalScribe.App\ComboBoxWatermark.cs`:

```csharp
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
namespace LocalScribe.App;

/// <summary>Attached watermark for EDITABLE ComboBoxes (UX round 2026-08-02 item 3.9). WPF's
/// ComboBox has no PlaceholderText and Wpf.Ui does not add one, so an empty free-text combo
/// (Settings > Per-app target) painted a blank box that read as a bug - and a real default
/// value would be wrong there. The watermark is an adorner shown only while ComboBox.Text is
/// empty; it is hit-test-invisible and never takes focus, so typing and selection behaviour
/// are untouched. Attached-behavior pattern: SegmentText.cs (the VM stays WPF-free).</summary>
public static class ComboBoxWatermark
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ComboBoxWatermark), new PropertyMetadata(null, OnTextChanged));
    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;
        // Idempotent re-wire: remove-then-add so a re-applied style never double-subscribes.
        combo.Loaded -= OnLoaded;
        combo.Loaded += OnLoaded;
        combo.RemoveHandler(TextBoxBase.TextChangedEvent, (TextChangedEventHandler)OnEditTextChanged);
        combo.AddHandler(TextBoxBase.TextChangedEvent, (TextChangedEventHandler)OnEditTextChanged);
        if (combo.IsLoaded) Update(combo);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Update((ComboBox)sender);

    // TextBoxBase.TextChanged bubbles up from the template's PART_EditableTextBox - no template
    // walking needed, and it fires for typing, suggestion picks, and programmatic Text sets alike.
    private static void OnEditTextChanged(object sender, TextChangedEventArgs e) => Update((ComboBox)sender);

    private static void Update(ComboBox combo)
    {
        var layer = AdornerLayer.GetAdornerLayer(combo);
        if (layer is null) return;                        // not in a visual tree yet
        var existing = layer.GetAdorners(combo)?.OfType<WatermarkAdorner>().FirstOrDefault();
        bool wanted = string.IsNullOrEmpty(combo.Text) && GetText(combo) is { Length: > 0 };
        if (wanted && existing is null) layer.Add(new WatermarkAdorner(combo, GetText(combo)!));
        else if (!wanted && existing is not null) layer.Remove(existing);
    }

    /// <summary>Muted, hit-test-invisible label over the combo's text area. Inherits the combo's
    /// Foreground so it stays legible in both themes (no ARGB literals - house XAML hygiene).</summary>
    private sealed class WatermarkAdorner : Adorner
    {
        private readonly TextBlock _label;

        public WatermarkAdorner(ComboBox adorned, string text) : base(adorned)
        {
            IsHitTestVisible = false;
            _label = new TextBlock
            {
                Text = text,
                Opacity = 0.6,
                Foreground = adorned.Foreground,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                // Left edge aligns with the editable text box's caret; right leaves the
                // drop-down button clear.
                Margin = new Thickness(10, 0, 30, 0),
            };
            AddVisualChild(_label);
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _label;

        protected override Size MeasureOverride(Size constraint)
        {
            _label.Measure(constraint);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _label.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }
}
```

- [ ] Modify `SettingsPage.xaml`: add the local xmlns to the root element (:1-4):

```xml
<UserControl x:Class="LocalScribe.App.SettingsPage"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:local="clr-namespace:LocalScribe.App">
```

  and apply the watermark to the Per-app combo (:73-75):

```xml
                        <ComboBox IsEditable="True" IsTextSearchEnabled="False"
                                  ItemsSource="{Binding RemoteAppSuggestions}"
                                  Text="{Binding RemoteApp, UpdateSourceTrigger=LostFocus}" MinWidth="200"
                                  local:ComboBoxWatermark.Text="e.g. Webex, Zoom" />
```

- [ ] Build to prove the XAML and the new class compile (close any running LocalScribe.App.exe first): `dotnet build src\LocalScribe.App` — expect `Build succeeded`.

- [ ] Create `docs\plans\2026-08-02-ux-round-smoke-runbook.md` (or append the section if the file already exists):

```markdown
# UX round 2026-08-02 - smoke runbook, item 3 (no blank dropdowns)

Manual checks for the view-layer pieces headless VM tests cannot cover.

## 3.9 Per-app target watermark (Settings > Recording, Remote capture = perProcess)
- [ ] Fresh settings (Remote.App unset): the Per-app target combo shows the muted watermark "e.g. Webex, Zoom" instead of an empty box.
- [ ] Typing hides the watermark at the first character; clearing the text brings it back after focus leaves the box.
- [ ] Picking a suggestion (CiscoCollabHost) hides the watermark; clicks land in the edit box normally (the watermark never intercepts the mouse).
- [ ] Legible in both light and dark themes.
```

- [ ] Commit:
```
git add src\LocalScribe.App\ComboBoxWatermark.cs src\LocalScribe.App\SettingsPage.xaml docs\plans\2026-08-02-ux-round-smoke-runbook.md
git commit -m "feat(settings): watermark for the editable per-app target combo"
```

---

### Task 10: Settings > Model / Language — mark stale persisted values as "(not installed)"

**Cross-plan prerequisite:** `2026-08-02-model-descriptions-plan.md` Task 7 has landed — `SettingsPageViewModel.ModelChoices` is `IReadOnlyList<WhisperModelInfo>` and the Settings combo binds `SelectedValuePath="Name"`. The spec's "rendered as name (not installed)" intent is met by the two-line row: the REAL canonical name on line 1 (so selection matches with zero mapping code) and the "(not installed)" mark on the subtitle line. The Language picker keeps the original string-decoration form (it is `LanguageChoice(Code, Name)`-shaped and untouched by the models plan).

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs` (ctor :212, `LanguageChoices` :415-416)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\SettingsPageViewModelTests.cs`

**Interfaces:**
- Consumes: `BuildModelChoices(string modelsRoot)` as landed by model-descriptions Task 7 (returns `IReadOnlyList<WhisperModelInfo>`: "auto" + catalog-projected on-disk names); `record WhisperModelInfo(string Name, string Subtitle, int Rank, bool EnglishOnly)` (its Task 1); `ModelFileResolver.CanonicalName(string)` (used by the untouched `Model` getter); `LanguageChoice.All` (:26-48, curated 20 incl. `"auto"`); the mic-picker insert precedent (:375-379).
- Produces: `ModelChoices` containing an injected `WhisperModelInfo(storedCanonical, "(not installed)", int.MaxValue, ...)` row at index 1 when the persisted model's weights are absent — the row keeps the REAL canonical `Name`, so `SelectedValue` selects it and the existing `Model` getter/setter need no mapping; instance `LanguageChoices` with an injected `LanguageChoice(savedCode, "{code} (not installed)")` at index 1 when the saved code is outside the curated list. Nothing is committed on page-open.

**Steps:**

- [ ] Add the failing tests to `SettingsPageViewModelTests.cs` (harness `MakeVm` :30-54; `_settings.SaveCount` exists — see :76):

```csharp
    [Fact]
    public void Stale_persisted_model_is_injected_as_a_not_installed_choice_and_selected()
    {
        // UX round 2026-08-02 item 3.10: weights deleted but settings.json still pins the model.
        // The raw value matched nothing -> blank ComboBox. Mic-picker pattern: inject a truthful
        // row and select it; NEVER silently rewrite the saved setting. Catalog shape: the row
        // keeps the real canonical Name (so SelectedValuePath="Name" matches with no mapping)
        // and carries the "(not installed)" mark on its subtitle line.
        var vm = MakeVm(new Settings { Model = "large-v3" });      // no ggml files on disk
        Assert.Equal("large-v3", vm.Model);
        Assert.Equal(new[] { "auto", "large-v3" }, vm.ModelChoices.Select(c => c.Name));
        Assert.Equal("(not installed)", vm.ModelChoices[1].Subtitle);
        Assert.Equal(0, _settings.SaveCount);                      // display-only on page-open
    }

    [Fact]
    public async Task Reselecting_the_not_installed_model_entry_commits_the_real_name()
    {
        var vm = MakeVm(new Settings { Model = "large-v3" });
        vm.Model = "large-v3";                                     // user re-picks the injected row
        await vm.LastSave;
        Assert.Equal("large-v3", _settings.Current.Model);         // bare name; subtitle never persisted
    }

    [Fact]
    public void Stale_persisted_language_is_injected_and_selected_by_code()
    {
        // "sv" is a valid Whisper code outside the curated 20 (hand-edited settings.json or an
        // older build) - SelectedValuePath="Code" matched nothing -> blank ComboBox.
        var vm = MakeVm(new Settings { Language = "sv" });
        Assert.Equal("sv", vm.Language);
        Assert.Contains(vm.LanguageChoices, c => c.Code == "sv" && c.Name == "sv (not installed)");
        Assert.Equal(0, _settings.SaveCount);
    }

    [Fact]
    public void Installed_model_and_curated_language_get_no_injected_entries()
    {
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-small.en.bin"), new byte[] { 1 });
        var vm = MakeVm(new Settings { Model = "small.en", Language = "en" });
        Assert.Equal(new[] { "auto", "small.en" }, vm.ModelChoices.Select(c => c.Name));
        Assert.DoesNotContain(vm.ModelChoices, c => c.Subtitle == "(not installed)");
        Assert.DoesNotContain(vm.LanguageChoices, c => c.Name.Contains("(not installed)"));
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsPageViewModelTests"` — expect the new tests to FAIL: the names-array mismatch (`Expected: ["auto", "large-v3"] / Actual: ["auto"]`), the subtitle `Assert.Equal() Failure`, and `Assert.Contains() Failure` for the language.

- [ ] Implement in `SettingsPageViewModel.cs`:

  1. In the ctor, directly after `ModelChoices = BuildModelChoices(modelsRoot ?? ModelPaths.ModelsRoot);` (:212), add:

```csharp
        string storedModel = ModelFileResolver.CanonicalName(settings.Current.Model);
        if (!ModelChoices.Any(c => c.Name == storedModel))
        {
            // Stale pin (weights deleted / different root): inject the saved model as a truthful
            // "(not installed)" row at index 1 (after "auto"), mirroring the mic picker. The row
            // keeps the REAL canonical name so SelectedValuePath="Name" selects it and the
            // existing setter commits it verbatim - nothing is rewritten on page-open (item 3.10).
            var withMissing = ModelChoices.ToList();
            withMissing.Insert(1, new WhisperModelInfo(storedModel, "(not installed)",
                int.MaxValue, storedModel.EndsWith(".en", StringComparison.Ordinal)));
            ModelChoices = withMissing;
        }
        LanguageChoices = BuildLanguageChoices(settings.Current.Language);
```

  2. The `Model` property (canonicalize-on-get, `Commit`-on-set) is NOT touched: the canonical getter value equals the injected row's `Name`, so `SelectedValue` matches, and the setter already commits the bare canonical name (the "(not installed)" text lives only on the row's subtitle, never in a bound value).

  3. Replace the `LanguageChoices` property (:415-416) and add the builder below `BuildModelChoices`:

```csharp
    /// <summary>See LanguageChoice.All - shared with the Re-transcribe dialog. Instance-built:
    /// a saved code outside the curated list gets an injected "(not installed)" entry
    /// (item 3.10) so the ComboBox (SelectedValuePath=Code) still selects it truthfully.</summary>
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }
```

```csharp
    /// <summary>LanguageChoice.All plus, when settings.json carries a code outside the curated
    /// list (hand-edited, or an older build's value), an injected "{code} (not installed)" entry
    /// at index 1 - selected by Code, so no setter mapping is needed and nothing is rewritten.</summary>
    private static IReadOnlyList<LanguageChoice> BuildLanguageChoices(string saved)
    {
        if (LanguageChoice.All.Any(c => c.Code == saved)) return LanguageChoice.All;
        var choices = LanguageChoice.All.ToList();
        choices.Insert(1, new LanguageChoice(saved, saved + " (not installed)"));
        return choices;
    }
```

  4. `ModelChoices` stays `public IReadOnlyList<WhisperModelInfo> ModelChoices { get; }` (as landed by model-descriptions Task 7) — the ctor reassignment above is legal for a get-only auto-property.

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsPageViewModelTests"` — expect ALL PASS, including the pre-existing `Persisted_quantized_model_name_displays_as_its_canonical_choice` (:149-159 — its weights file EXISTS, so no injection fires and the canonical entry still matches) and `Model_choices_enumerate_only_installed_ggml_files_plus_auto`.

- [ ] Run the assistant-settings suite too (same VM): `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsPageViewModelAssistantTests"` — expect ALL PASS.

- [ ] Commit:
```
git add src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs tests\LocalScribe.App.Tests\SettingsPageViewModelTests.cs
git commit -m "fix(settings): stale persisted model/language display as (not installed) entries"
```

---

### Task 11: Edit mode > split-child speaker — "(inherits parent's speaker)" display

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\EditableSegmentViewModel.cs` (add computed property + partial change handler), `F:\LocalScribe\src\LocalScribe.App\ReadViewWindow.xaml` (the per-segment speaker ComboBox :487-490), `F:\LocalScribe\docs\plans\2026-08-02-ux-round-smoke-runbook.md` (append)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\EditableSegmentViewModelTests.cs`

**Interfaces:**
- Consumes: `EditableSegmentViewModel` (`IsSplitChild` get-only :20, `[ObservableProperty] SpeakerChoice? _speaker` :44 — no `OnSpeakerChanged` partial exists yet); null-means-inherit semantics (`EditableSectionViewModel.SplitChildSpeaker` :63-66 — UNCHANGED by this task); `sealed record SpeakerChoice(string Display, string? ParticipantId, string? ClusterKey, bool IsUnassign = false)` (`SpeakerChoice.cs:12`).
- Produces: `public string SpeakerPlaceholder` on `EditableSegmentViewModel` — `"(inherits parent's speaker)"` exactly when `IsSplitChild && Speaker is null`, else `""`; re-raised whenever `Speaker` changes. Persistence (`CollectSplits` reading `s.Speaker?.ParticipantId`/`?.ClusterKey`) is untouched.

**Steps:**

- [ ] Add the failing tests to `EditableSegmentViewModelTests.cs`:

```csharp
    [Fact]
    public void SplitChild_WithoutOverride_ShowsTheInheritPlaceholder_UntilASpeakerIsPicked()
    {
        // UX round 2026-08-02 item 3.11: a split child with no persisted override deliberately
        // carries Speaker = null ("inherits the parent seq's name") - which painted a blank
        // ComboBox that looked broken. Display-only fix: the null-means-inherit persistence
        // semantics are untouched.
        var choices = new List<SpeakerChoice>
        { new("Automatic (Me / Them)", null, null, IsUnassign: true) };
        var child = new EditableSegmentViewModel(3, TranscriptSource.Remote, 1, "tail",
            15000, 17000, derivedStart: true, rawText: "head tail", speaker: null,
            isSplitChild: true, choices);
        Assert.Equal("(inherits parent's speaker)", child.SpeakerPlaceholder);

        var raised = new List<string?>();
        child.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        child.Speaker = choices[0];
        Assert.Equal("", child.SpeakerPlaceholder);          // picked -> placeholder clears
        Assert.Contains(nameof(EditableSegmentViewModel.SpeakerPlaceholder), raised);

        child.Speaker = null;                                // back to inherit
        Assert.Equal("(inherits parent's speaker)", child.SpeakerPlaceholder);
    }

    [Fact]
    public void WholeSegment_NeverShowsTheInheritPlaceholder()
    {
        var whole = new EditableSegmentViewModel(4, TranscriptSource.Remote, 0, "line",
            0, 1000, derivedStart: false, rawText: "line", speaker: null,
            isSplitChild: false, null);
        Assert.Equal("", whole.SpeakerPlaceholder);          // whole segments fall back to
                                                             // "Automatic (Me / Them)" elsewhere
    }
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~EditableSegmentViewModelTests"` — expect a COMPILE error `CS1061: 'EditableSegmentViewModel' does not contain a definition for 'SpeakerPlaceholder'`.

- [ ] Implement in `EditableSegmentViewModel.cs` (below the `[ObservableProperty]` block at :41-44):

```csharp
    /// <summary>Overlay text for the Edit-mode speaker box of a split child with NO override
    /// (UX round 2026-08-02 item 3.11): null Speaker deliberately means "inherit the parent
    /// seq's name" (EditableSectionViewModel.SplitChildSpeaker), which painted a blank ComboBox
    /// that read as a bug. "" for every other state - the XAML trigger collapses the overlay on
    /// "". Display-only: the null-means-inherit persistence semantics are untouched.</summary>
    public string SpeakerPlaceholder =>
        IsSplitChild && Speaker is null ? "(inherits parent's speaker)" : "";

    partial void OnSpeakerChanged(SpeakerChoice? value)
        => OnPropertyChanged(nameof(SpeakerPlaceholder));
```

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~EditableSegmentViewModelTests"` — expect ALL PASS. Also run the section suite (it constructs many segments): `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~EditableSectionViewModelTests"` — expect ALL PASS.

- [ ] Modify `ReadViewWindow.xaml` (:487-490): wrap the speaker ComboBox in a Grid with the hit-test-invisible overlay (the ComboBox's `Margin="8,0,0,0"` moves to the Grid; Style + DataTrigger per house rule — no converter):

```xml
                                                <Grid Margin="8,0,0,0">
                                                    <ComboBox ItemsSource="{Binding SpeakerChoices}"
                                                              DisplayMemberPath="Display"
                                                              SelectedItem="{Binding Speaker}"
                                                              MinWidth="160" />
                                                    <!-- Item 3.11: split child with no override inherits the
                                                         parent seq's speaker; say so instead of painting blank. -->
                                                    <TextBlock Text="{Binding SpeakerPlaceholder, Mode=OneWay}"
                                                               IsHitTestVisible="False" Opacity="0.6"
                                                               VerticalAlignment="Center" Margin="10,0,28,0"
                                                               TextTrimming="CharacterEllipsis">
                                                        <TextBlock.Style>
                                                            <Style TargetType="TextBlock">
                                                                <Style.Triggers>
                                                                    <DataTrigger Binding="{Binding SpeakerPlaceholder}" Value="">
                                                                        <Setter Property="Visibility" Value="Collapsed" />
                                                                    </DataTrigger>
                                                                </Style.Triggers>
                                                            </Style>
                                                        </TextBlock.Style>
                                                    </TextBlock>
                                                </Grid>
```

- [ ] Build to prove the XAML compiles (close any running LocalScribe.App.exe first): `dotnet build src\LocalScribe.App` — expect `Build succeeded`.

- [ ] Append to `docs\plans\2026-08-02-ux-round-smoke-runbook.md`:

```markdown
## 3.11 Split-child speaker placeholder (read view > Edit mode)
- [ ] Split a line (expand a section, caret mid-text, Split): both children's speaker boxes show "(inherits parent's speaker)" until a speaker is picked.
- [ ] Picking a speaker on a child replaces the placeholder with the selection; Save then re-Edit shows the persisted speaker, not the placeholder.
- [ ] The placeholder never blocks opening the dropdown (click lands on the ComboBox).
```

- [ ] Commit:
```
git add src\LocalScribe.App\ViewModels\EditableSegmentViewModel.cs src\LocalScribe.App\ReadViewWindow.xaml tests\LocalScribe.App.Tests\EditableSegmentViewModelTests.cs docs\plans\2026-08-02-ux-round-smoke-runbook.md
git commit -m "fix(readview): split-child speaker shows (inherits parent's speaker) instead of blank"
```

---

### Task 12: Full-suite regression run + smoke-runbook sweep section

**Files:**
- Modify: `F:\LocalScribe\docs\plans\2026-08-02-ux-round-smoke-runbook.md` (append the end-to-end sweep)
- Test: BOTH full suites, no filter.

**Interfaces:**
- Consumes: everything produced by Tasks 1-11.
- Produces: green `LocalScribe.App.Tests` and `LocalScribe.Core.Tests`; the complete item-3 smoke checklist.

**Steps:**

- [ ] Close any running LocalScribe.App.exe (Task Manager or `Get-Process LocalScribe.App -ErrorAction SilentlyContinue | Stop-Process` — target ONLY that process, never a blanket kill).

- [ ] Run: `dotnet test tests\LocalScribe.App.Tests` — expect 0 failed. If anything fails, fix the regression in the task that introduced it (the failing test names map 1:1 to Tasks 1-11 above) before proceeding.

- [ ] Run: `dotnet test tests\LocalScribe.Core.Tests` — expect 0 failed (no Core source was touched; this guards against accidental cross-project drift).

- [ ] Append to `docs\plans\2026-08-02-ux-round-smoke-runbook.md`:

```markdown
## 3.x End-to-end dropdown sweep (one pass over every fixed site)
- [ ] Settings > Assistant > Model: first open with chat models installed shows a selected model within seconds (never an enabled-but-blank box); with only a non-default model installed, the picker shows that model (matches what the assistant actually runs).
- [ ] Record console: in Settings pin Remote capture = perProcess with app "Webex" while Webex is NOT running - both Remote target combos (ready card and live view) show "Webex" selected, and the selection survives the 2 s refresh and a dropdown open/close.
- [ ] Session Details > Speakers: both "Add from roster" pickers show "(choose a person)"; Add is greyed until a real person is picked; picking then Add adds exactly that person to the correct side.
- [ ] Read view assistant panel on a never-summarised session: summary version combo shows "(no summaries yet)"; thread combo shows "(no conversations yet)"; after the first Regenerate/ask both show the real entries.
- [ ] Sessions page: matter filter shows "All matters" immediately on first open and after Refresh; picking a matter filters the grid; clearing back to "All matters" restores it.
- [ ] Search page: matter facet shows "All matters" with no blank flash on first navigation.
- [ ] Import dialog and Re-transcribe dialog with an empty models folder: greyed "(no models found)" selected in the model picker, Start disabled.
- [ ] Settings > Transcription with the pinned model's weights file deleted: "name (not installed)" selected; hand-edit settings.json Language to "sv": "sv (not installed)" selected. Neither state rewrites settings.json until you explicitly change the field.
```

- [ ] Commit:
```
git add docs\plans\2026-08-02-ux-round-smoke-runbook.md
git commit -m "docs(ux): smoke checklist for the no-blank-dropdowns sweep"
```
