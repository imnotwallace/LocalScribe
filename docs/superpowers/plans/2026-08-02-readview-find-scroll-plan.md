# Read-View Edit-Aware Find + No-Scroll-Reset Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox ("- [ ]") syntax for tracking.

**Goal:** Make the read-view Find bar fully functional during Edit mode (live-buffer search, edit-list highlights, caret jump-in) and stop Edit/Save from bouncing the transcript back to the top.

**Architecture:** Items 1 and 2 of the approved UX-round spec (`docs\superpowers\specs\2026-08-02-ux-round-design.md` sections 1-2). The WPF-free `ReadViewViewModel` gains a mode-aware find recompute whose match indices live in `Rows` space (read) or `EditSections` space (edit), with the corpus rule "expanded section = live joined `EditedText`, collapsed section = loaded `Row.Text`". The view layer (`ReadViewWindow.xaml.cs`) gains a mode-aware scroll helper, a one-shot caret-selection attached behavior, and topmost-visible-item anchor helpers that preserve scroll position across the RowList/EditList swap.

**Tech Stack:** .NET WPF, CommunityToolkit.Mvvm (`[ObservableProperty]`), Wpf.Ui, xUnit (headless VM-level only).

**Cross-plan coordination:** The playback-transport plan (spec items 7-9) also touches `ReadViewWindow.xaml.cs`. **Execute THIS plan first** — it owns the `FindScrollViewer` hoist into `ScrollHelpers` (Task 7); the transport plan consumes `ScrollHelpers.FindScrollViewer` and must rebase on this plan's window changes.

## Global Constraints

- Strict TDD: write the failing test and SEE it fail before writing any implementation code, in every task.
- No Unicode emojis anywhere — code, tests, scripts, docs, commit messages.
- ViewModels stay WPF-free: nothing under `src\LocalScribe.App\ViewModels\` may reference System.Windows types.
- House rule in `ReadViewWindow.xaml`: no bool-inverting converter — any IsEditMode-conditional XAML uses the Style + DataTrigger pattern (see `ReadViewWindow.xaml:48-66`); the `InverseBooleanConverter` that exists for the Split-speakers dialog must NOT be spread here.
- `[ObservableProperty]` equality-gates same-value sets — after a collection rebuild or an unchanged-index recompute, re-stamp flags/status explicitly (the existing "Unchanged index" branch in `RecomputeFindMatches` is the model).
- Invariant culture in all export/formatted text (`CultureInfo.InvariantCulture`); this plan formats nothing new but must not regress it.
- Transcripts are evidence — never destructive. This plan adds NO new write path; Task 5 pins with a regression test that find-driven section expansion writes nothing on save.
- Close any running `LocalScribe.App.exe` before building — a running app locks Core.dll (MSB3027). Kill ONLY that specific process, never all dotnet/npm processes.
- View-layer scroll/caret/visual behavior cannot be unit-tested here (no STA/WPF harness exists and none may be added) — such tasks end with a smoke-runbook checkbox addition to `docs\plans\2026-07-07-transcript-editor-smoke-runbook.md` instead of a fake test.
- Test commands: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~<TestClass>"` (App) / `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~<TestClass>"` (Core). Build with `dotnet build F:\LocalScribe\LocalScribe.slnx` (repo root has a .slnx, not a .sln).

---

### Task 1: EditableSectionViewModel find surface (flags, corpus rule, change notification)

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\EditableSectionViewModel.cs` (class head lines 12-26: flags + event + `SearchText` + ctor becomes a block body with a `CollectionChanged` subscription)
- Test: `tests\LocalScribe.App.Tests\EditableSectionFindTests.cs` (Create)

**Interfaces:**
- Consumes: existing `EditableSectionViewModel.Row` (`DisplayRow`), `Segments` (`ObservableCollection<EditableSegmentViewModel>`), `IsEditing`, `BeginEdit(string timestampsMode, DateTimeOffset startedAt, ...)`; `EditableSegmentViewModel.EditedText` (`[ObservableProperty] string`).
- Produces: `public bool IsFindMatch` / `public bool IsCurrentFindMatch` (observable, on `EditableSectionViewModel`); `public string SearchText { get; }`; `public event Action? LiveTextChanged;`. Tasks 2/3/6 rely on these exact names.

- [ ] **Step 1: Write the failing tests**

Create `tests\LocalScribe.App.Tests\EditableSectionFindTests.cs`:

```csharp
// tests/LocalScribe.App.Tests/EditableSectionFindTests.cs
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Item 1 (UX round 2026-08-02): the edit-mode find corpus rule and its change
/// notification live on EditableSectionViewModel - section-level, no session fixture needed.</summary>
public sealed class EditableSectionFindTests
{
    /// <summary>Two Local segments; Row.Text is exactly SectionGrouper's single-space join.</summary>
    private static DisplayRow MakeRow() => new()
    {
        StartMs = 0,
        EndMs = 3000,
        DisplayName = "Sam",
        Text = "hello world goodbye",
        Segments = new[]
        {
            new RowSegment(0, TranscriptSource.Local, 0, 1500, "hello world", "hello world",
                IsCorrected: false, IsPinned: false),
            new RowSegment(1, TranscriptSource.Local, 1600, 3000, "goodbye", "goodbye",
                IsCorrected: false, IsPinned: false),
        },
    };

    [Fact]
    public void SearchText_uses_row_text_collapsed_and_live_joined_text_expanded()
    {
        var section = new EditableSectionViewModel(MakeRow());
        Assert.Equal("hello world goodbye", section.SearchText);      // collapsed: loaded Row.Text

        section.BeginEdit("relative", default);
        Assert.Equal("hello world goodbye", section.SearchText);      // expanded, untouched: same join

        section.Segments[0].EditedText = "changed text";
        Assert.Equal("changed text goodbye", section.SearchText);     // expanded: LIVE buffer wins
    }

    [Fact]
    public void Find_flags_are_observable()
    {
        var section = new EditableSectionViewModel(MakeRow());
        var raised = new List<string?>();
        section.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        section.IsFindMatch = true;
        section.IsCurrentFindMatch = true;

        Assert.Contains(nameof(EditableSectionViewModel.IsFindMatch), raised);
        Assert.Contains(nameof(EditableSectionViewModel.IsCurrentFindMatch), raised);
    }

    [Fact]
    public void LiveTextChanged_fires_for_typing_and_survives_split_and_revert()
    {
        var section = new EditableSectionViewModel(MakeRow());
        int fired = 0;
        section.LiveTextChanged += () => fired++;

        section.BeginEdit("relative", default);
        Assert.True(fired > 0);                        // materialization changes the live corpus

        fired = 0;
        section.Segments[0].EditedText = "typed here";
        Assert.Equal(1, fired);                        // plain typing

        fired = 0;
        section.SplitSegment(section.Segments[0], caret: 5);
        Assert.True(fired > 0);                        // split REPLACES segment instances

        fired = 0;
        section.Segments[0].EditedText = "typed again";
        Assert.True(fired > 0);                        // the replacement instance is re-wired

        fired = 0;
        section.RevertSplit(0);
        Assert.True(fired > 0);                        // revert replaces instances too

        fired = 0;
        section.Segments[0].EditedText = "after revert";
        Assert.True(fired > 0);                        // the restored instance is re-wired
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~EditableSectionFindTests"`
Expected: BUILD FAILURE — `error CS1061: 'EditableSectionViewModel' does not contain a definition for 'SearchText'` (and the same for `LiveTextChanged`).

- [ ] **Step 3: Implement**

In `src\LocalScribe.App\ViewModels\EditableSectionViewModel.cs`:

(a) Add to the usings block at the top (after `using System.Collections.ObjectModel;`):

```csharp
using System.ComponentModel;
```

(b) Directly under `[ObservableProperty] private bool _isEditing;` (line 16), insert:

```csharp
    /// <summary>Item 1 (UX round 2026-08-02) edit-aware Find: mirror of ReadRow's two flags,
    /// stamped exclusively by ReadViewViewModel's find recompute; EditList's ItemContainerStyle
    /// tints off them exactly as RowList's does off ReadRow's.</summary>
    [ObservableProperty] private bool _isFindMatch;
    [ObservableProperty] private bool _isCurrentFindMatch;

    /// <summary>Raised whenever this section's live find corpus changed: a materialized segment's
    /// EditedText edit, or a segment-instance replacement (BeginEdit/split/revert/reindex all
    /// mutate the Segments collection). ReadViewViewModel debounces a find recompute off this.</summary>
    public event Action? LiveTextChanged;

    /// <summary>The corpus rule (item 1): an EXPANDED section is searched against what the user
    /// is typing (live EditedText, single-space join - the same join SectionGrouper renders); a
    /// collapsed one against the loaded Row.Text.</summary>
    public string SearchText => IsEditing
        ? string.Join(" ", Segments.Select(s => s.EditedText))
        : Row.Text;

    private readonly List<EditableSegmentViewModel> _liveTextSubscribed = new();
```

(c) Replace the expression-bodied ctor at line 26 (`public EditableSectionViewModel(DisplayRow row) => Row = row;`) with:

```csharp
    public EditableSectionViewModel(DisplayRow row)
    {
        Row = row;
        // Segment instances are REPLACED (not mutated) by BeginEdit/split/revert/reindex, so the
        // per-segment EditedText subscriptions are rebuilt on every collection change rather than
        // wired once - the only way they can never go stale.
        Segments.CollectionChanged += (_, _) =>
        {
            ResubscribeSegments();
            LiveTextChanged?.Invoke();
        };
    }

    private void ResubscribeSegments()
    {
        foreach (var s in _liveTextSubscribed) s.PropertyChanged -= OnSegmentEditedTextChanged;
        _liveTextSubscribed.Clear();
        foreach (var s in Segments)
        {
            s.PropertyChanged += OnSegmentEditedTextChanged;
            _liveTextSubscribed.Add(s);
        }
    }

    private void OnSegmentEditedTextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditableSegmentViewModel.EditedText)) LiveTextChanged?.Invoke();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~EditableSectionFindTests"`
Expected: PASS (3 tests). Also run `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewEditModeTests"` — expected PASS (the ctor change must not disturb existing edit-mode behavior).

- [ ] **Step 5: Commit**

```powershell
git add tests\LocalScribe.App.Tests\EditableSectionFindTests.cs src\LocalScribe.App\ViewModels\EditableSectionViewModel.cs
git commit -m "feat(readview): edit-section find flags + live SearchText corpus (item 1)"
```

---

### Task 2: Mode-aware find core — bar survives Edit/Save/Cancel, EditSections-space matches

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\ReadViewViewModel.cs` (`OnCurrentFindRowIndexChanged` 264-269, `OpenFind` 271-280, `CloseFind` 282-290, `RecomputeFindMatches` 335-364, `EnterEditMode` 488-500, `CancelEdit` 502-508, `SaveEditsAsync` tail 576-578; new private `EditSectionIndexOf`)
- Test: `tests\LocalScribe.App.Tests\ReadViewFindTests.cs` (pinned test at 130-149 rewritten; three new tests)

**Interfaces:**
- Consumes: Task 1's `EditableSectionViewModel.IsFindMatch` / `IsCurrentFindMatch` / `SearchText`.
- Produces: mode-space contract used by Tasks 3-6: `_findMatchRows` and `CurrentFindRowIndex` are `Rows` indices in read mode and `EditSections` indices in edit mode; `private int EditSectionIndexOf(DisplayRow data)` (ReferenceEquals lookup, -1 when absent).

- [ ] **Step 1: Rewrite the pinned test and add the new ones**

In `tests\LocalScribe.App.Tests\ReadViewFindTests.cs`, DELETE the whole test `Find_survives_a_rows_reload_and_edit_mode_closes_it` (lines 130-149) and add in its place:

```csharp
    [Fact]
    public async Task Find_survives_a_rows_reload_and_stays_open_across_edit_mode()
    {
        await WriteFixtureSessionAsync("find-3");
        var vm = MakeVm();
        await vm.LoadAsync("find-3", CancellationToken.None);

        vm.OpenFind("morning");
        Assert.Equal("1/2", vm.FindStatus);
        await vm.ReloadRowsAsync(CancellationToken.None);                 // rows are NEW objects
        Assert.Equal("1/2", vm.FindStatus);
        Assert.True(vm.Rows[0].IsFindMatch);                              // flags re-stamped on new rows

        vm.EnterEditMode();                                               // item 1: bar SURVIVES edit
        Assert.True(vm.IsEditMode);
        Assert.True(vm.IsFindOpen);
        Assert.Equal("1/2", vm.FindStatus);                               // recomputed in section space
        Assert.Equal(0, vm.CurrentFindRowIndex);                          // EditSections index now
        Assert.True(vm.EditSections[0].IsFindMatch);
        Assert.True(vm.EditSections[0].IsCurrentFindMatch);
        Assert.True(vm.EditSections[1].IsFindMatch);
        Assert.False(vm.EditSections[1].IsCurrentFindMatch);

        vm.CancelEdit();                                                  // back to Rows space
        Assert.True(vm.IsFindOpen);
        Assert.Empty(vm.EditSections);
        Assert.Equal("1/2", vm.FindStatus);
        Assert.True(vm.Rows[0].IsFindMatch);
        Assert.True(vm.Rows[0].IsCurrentFindMatch);
    }

    [Fact]
    public async Task OpenFind_works_while_editing_and_counts_skip_marker_rows()
    {
        await WriteFixtureSessionAsync("find-5");
        var vm = MakeVm();
        await vm.LoadAsync("find-5", CancellationToken.None);

        vm.EnterEditMode();
        vm.OpenFind("device");                       // matches ONLY the marker row's text
        Assert.True(vm.IsFindOpen);                  // the old IsEditMode guard is gone
        Assert.Equal("0/0", vm.FindStatus);          // markers are not editable: excluded here
        Assert.Equal(-1, vm.CurrentFindRowIndex);

        vm.FindText = "morning";
        Assert.Equal("1/2", vm.FindStatus);          // both non-marker sections match
        vm.CancelEdit();
        Assert.Equal("1/2", vm.FindStatus);          // read mode sees the same two rows

        vm.FindText = "device";                      // and the marker match is back in read mode
        Assert.Equal("1/1", vm.FindStatus);
        Assert.Equal(2, vm.CurrentFindRowIndex);
    }

    [Fact]
    public async Task Edit_transitions_keep_the_current_match_position_via_row_identity()
    {
        await WriteFixtureSessionAsync("find-6");
        var vm = MakeVm();
        await vm.LoadAsync("find-6", CancellationToken.None);

        vm.OpenFind("morning");
        vm.FindNext();                               // read space: current = row 1 (Jane)
        Assert.Equal(1, vm.CurrentFindRowIndex);

        vm.EnterEditMode();                          // mapped via ReferenceEquals(section.Row, row.Data)
        Assert.Equal(1, vm.CurrentFindRowIndex);     // section 1 wraps the same DisplayRow instance
        Assert.Equal("2/2", vm.FindStatus);

        vm.CancelEdit();                             // mapped back the same way
        Assert.Equal(1, vm.CurrentFindRowIndex);
        Assert.Equal("2/2", vm.FindStatus);
    }

    [Fact]
    public async Task Save_recomputes_matches_on_the_reloaded_rows_in_read_space()
    {
        await WriteFixtureSessionAsync("find-7");
        var vm = MakeVm();
        await vm.LoadAsync("find-7", CancellationToken.None);

        vm.OpenFind("morning");
        vm.EnterEditMode();
        var section = vm.EditSections[0];
        section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal);
        section.Segments[0].EditedText = "we spoke to the client this evening";  // kills row 0's match

        await vm.SaveEditsAsync(CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        Assert.False(vm.IsEditMode);
        Assert.True(vm.IsFindOpen);                                   // bar survived the save too
        Assert.Equal("1/1", vm.FindStatus);                           // only Jane's reloaded row matches
        Assert.True(vm.Rows[1].IsFindMatch);
        Assert.False(vm.Rows[0].IsFindMatch);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewFindTests"`
Expected: FAIL — `Find_survives_a_rows_reload_and_stays_open_across_edit_mode` fails at `Assert.True(vm.IsFindOpen)` after `EnterEditMode()` (actual: False, the bar was force-closed); `OpenFind_works_while_editing...` fails at `Assert.True(vm.IsFindOpen)` (OpenFind refused).

- [ ] **Step 3: Implement in `ReadViewViewModel.cs`**

(a) Replace `OnCurrentFindRowIndexChanged` (lines 264-269) with a mode-aware version. An out-of-space stale `oldValue` during a mode transition only ever writes `false`, which is a semantic no-op — the recompute has already cleared every flag:

```csharp
    partial void OnCurrentFindRowIndexChanged(int oldValue, int newValue)
    {
        if (IsEditMode)
        {
            if (oldValue >= 0 && oldValue < EditSections.Count) EditSections[oldValue].IsCurrentFindMatch = false;
            if (newValue >= 0 && newValue < EditSections.Count) EditSections[newValue].IsCurrentFindMatch = true;
        }
        else
        {
            if (oldValue >= 0 && oldValue < Rows.Count) Rows[oldValue].IsCurrentFindMatch = false;
            if (newValue >= 0 && newValue < Rows.Count) Rows[newValue].IsCurrentFindMatch = true;
        }
        UpdateFindStatus();
    }
```

(b) Replace `OpenFind` (lines 271-280) — the guard goes, the doc changes:

```csharp
    /// <summary>Opens the find bar - in BOTH read and edit mode (item 1, UX round 2026-08-02: the
    /// old edit-mode refusal is gone; matches land on whichever list is visible). With initialText
    /// (the search page's click-through term) the text change recomputes matches; re-opening with
    /// the same text recomputes explicitly so flags land on the current rows.</summary>
    public void OpenFind(string? initialText = null)
    {
        IsFindOpen = true;
        if (initialText is not null && initialText != FindText) FindText = initialText;
        else RecomputeFindMatches(moveToFirst: _findMatchRows.Count == 0);
    }
```

(c) In `CloseFind` (lines 282-290), after the `foreach (var r in Rows)` line, add the edit-side clear:

```csharp
        foreach (var s in EditSections) { s.IsFindMatch = false; s.IsCurrentFindMatch = false; }
```

(d) Replace `RecomputeFindMatches` (lines 335-364):

```csharp
    /// <summary>Mode-aware (item 1): read mode scans Rows (markers included - find-on-page over
    /// what the reader sees); edit mode scans EditSections' SearchText (live buffer for expanded
    /// sections, loaded text for collapsed; markers are absent there, so they drop out of the
    /// count). _findMatchRows and CurrentFindRowIndex are Rows-space indices in read mode and
    /// EditSections-space indices in edit mode - they NEVER transfer across a mode switch, the
    /// transition callers re-map by row identity instead.</summary>
    private void RecomputeFindMatches(bool moveToFirst)
    {
        foreach (var r in Rows) { r.IsFindMatch = false; r.IsCurrentFindMatch = false; }
        foreach (var s in EditSections) { s.IsFindMatch = false; s.IsCurrentFindMatch = false; }
        _findMatchRows.Clear();
        string needle = FindText.Trim();
        if (!IsFindOpen || needle.Length == 0)
        {
            CurrentFindRowIndex = -1;
            FindStatus = "";
            return;
        }
        int count = IsEditMode ? EditSections.Count : Rows.Count;
        for (int i = 0; i < count; i++)
        {
            bool hit = IsEditMode
                ? EditSections[i].SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase)
                : Rows[i].Data.Text.Contains(needle, StringComparison.OrdinalIgnoreCase);
            if (!hit) continue;
            _findMatchRows.Add(i);
            if (IsEditMode) EditSections[i].IsFindMatch = true;
            else Rows[i].IsFindMatch = true;
        }
        int current = -1;
        if (_findMatchRows.Count > 0)
            current = !moveToFirst && _findMatchRows.Contains(CurrentFindRowIndex)
                ? CurrentFindRowIndex
                : _findMatchRows[0];
        if (CurrentFindRowIndex == current)
        {
            // Unchanged index: the property setter won't fire, so re-stamp + refresh explicitly.
            if (current >= 0)
            {
                if (IsEditMode) EditSections[current].IsCurrentFindMatch = true;
                else Rows[current].IsCurrentFindMatch = true;
            }
            UpdateFindStatus();
        }
        else CurrentFindRowIndex = current;
    }
```

(e) Add the identity lookup next to `RecomputeFindMatches`:

```csharp
    /// <summary>Index of the section wrapping this EXACT DisplayRow instance. ReferenceEquals is
    /// mandatory: DisplayRow is a record (value equality) and two different rows can compare
    /// equal - the section wraps the same instance EnterEditMode read out of Rows.</summary>
    private int EditSectionIndexOf(DisplayRow data)
    {
        for (int i = 0; i < EditSections.Count; i++)
            if (ReferenceEquals(EditSections[i].Row, data)) return i;
        return -1;
    }
```

(f) Replace `EnterEditMode` (lines 488-500) — the `CloseFind()` goes, the transition recompute + position mapping arrive:

```csharp
    /// <summary>Enters Edit mode (design §3.2): gated on CanEdit and not already editing, so a
    /// stray second call is a no-op rather than clobbering in-progress section edits. Builds one
    /// EditableSectionViewModel per non-marker row - markers have no segments to correct/split.
    /// Item 1 (UX round 2026-08-02): the find bar now SURVIVES the mode switch; matches recompute
    /// in EditSections space and the current match maps across by row identity.</summary>
    public void EnterEditMode()
    {
        if (!CanEdit || IsEditMode) return;
        SaveError = null;                     // clear any stale failure from a prior session
        var anchorData = CurrentFindRowIndex >= 0 && CurrentFindRowIndex < Rows.Count
            ? Rows[CurrentFindRowIndex].Data : null;
        EditSections.Clear();
        foreach (var r in Rows)
            if (!r.Data.IsMarker) EditSections.Add(new EditableSectionViewModel(r.Data));
        IsEditMode = true;
        if (IsFindOpen)
        {
            RecomputeFindMatches(moveToFirst: true);
            if (anchorData is not null)
            {
                int si = EditSectionIndexOf(anchorData);
                if (si >= 0 && _findMatchRows.Contains(si)) CurrentFindRowIndex = si;
            }
        }
    }
```

(g) Replace `CancelEdit` (lines 502-508):

```csharp
    /// <summary>Drops all in-progress section edits without writing anything (design §3.2). The
    /// find bar stays open (item 1); the current match maps back to the read row by identity -
    /// Rows was untouched, so the DisplayRow references are still live.</summary>
    public void CancelEdit()
    {
        SaveError = null;
        var anchorData = CurrentFindRowIndex >= 0 && CurrentFindRowIndex < EditSections.Count
            ? EditSections[CurrentFindRowIndex].Row : null;
        EditSections.Clear();
        IsEditMode = false;
        if (IsFindOpen)
        {
            RecomputeFindMatches(moveToFirst: true);
            if (anchorData is not null)
                for (int i = 0; i < Rows.Count; i++)
                    if (ReferenceEquals(Rows[i].Data, anchorData) && _findMatchRows.Contains(i))
                    {
                        CurrentFindRowIndex = i;
                        break;
                    }
        }
    }
```

(h) In `SaveEditsAsync`, replace the success tail (lines 576-578):

```csharp
        SaveError = null;
        IsEditMode = false;
        EditSections.Clear();
        // Item 1: ApplyRows' own recompute (inside ReloadRowsAsync above) ran while IsEditMode was
        // still true, i.e. against the now-discarded sections - recompute once more in read space
        // so flags/status land on the reloaded rows. Rows were REBUILT, so identity mapping is
        // impossible here; moveToFirst:false keeps the index when it is still a match.
        if (IsFindOpen) RecomputeFindMatches(moveToFirst: false);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewFindTests"`
Expected: PASS (7 tests). Also run `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewEditModeTests"` — expected PASS (EnterEditMode/CancelEdit/SaveEditsAsync semantics for non-find flows unchanged).

- [ ] **Step 5: Commit**

```powershell
git add tests\LocalScribe.App.Tests\ReadViewFindTests.cs src\LocalScribe.App\ViewModels\ReadViewViewModel.cs
git commit -m "feat(readview): find bar survives edit mode; mode-aware match recompute (item 1)"
```

---

### Task 3: Debounced live-buffer recompute while typing in Edit mode

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\ReadViewViewModel.cs` (ctor 159-166 gains `int findDebounceMs = 250`; `EnterEditMode` wires `LiveTextChanged`; `CancelEdit`/`SaveEditsAsync` unwire; new `ScheduleFindRecompute`/`RunFindRecomputeAsync`/`PendingFindRecompute`)
- Test: `tests\LocalScribe.App.Tests\ReadViewFindTests.cs` (`MakeVm` at 31-32 gains the debounce seam; two new tests)

**Interfaces:**
- Consumes: Task 1's `EditableSectionViewModel.LiveTextChanged` event; Task 2's mode-aware `RecomputeFindMatches`.
- Produces: ctor signature `ReadViewViewModel(MaintenanceService, StoragePaths, ISettingsService, IUiErrorReporter, IDualAudioPlayer, Action<Action> dispatch, TimeProvider time, int findDebounceMs = 250)` (optional — the single production call site `App.xaml.cs:476-478` keeps compiling); `public Task? PendingFindRecompute { get; }` test seam (the `SearchPageViewModel.PendingSearch` precedent, `SearchPageViewModel.cs:79-81`).

- [ ] **Step 1: Write the failing tests**

In `tests\LocalScribe.App.Tests\ReadViewFindTests.cs`, replace `MakeVm` (lines 31-32) with:

```csharp
    private ReadViewViewModel MakeVm(int findDebounceMs = 0)
        => new(_maintenance, _paths, _settings, _reporter, new FakePlayer(), dispatch: a => a(),
            _time, findDebounceMs);
```

Add two tests:

```csharp
    [Fact]
    public async Task Typing_in_an_expanded_section_updates_match_counts_after_the_debounce()
    {
        await WriteFixtureSessionAsync("find-8");
        var vm = MakeVm();
        await vm.LoadAsync("find-8", CancellationToken.None);

        vm.OpenFind("morning");
        vm.EnterEditMode();
        Assert.Equal("1/2", vm.FindStatus);

        var section = vm.EditSections[0];
        section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal);
        section.Segments[0].EditedText = "we spoke to the client this evening";

        Assert.NotNull(vm.PendingFindRecompute);              // typing scheduled a recompute
        await vm.PendingFindRecompute!;
        Assert.Equal("1/1", vm.FindStatus);                   // live buffer: section 0 fell out
        Assert.False(vm.EditSections[0].IsFindMatch);
        Assert.True(vm.EditSections[1].IsFindMatch);

        section.Segments[0].EditedText = "unique zebra sighting this morning";
        await vm.PendingFindRecompute!;
        Assert.Equal("2/2", vm.FindStatus);                   // typed text is findable again

        vm.FindText = "zebra";                                // BRAND NEW text matches immediately
        Assert.Equal("1/1", vm.FindStatus);
    }

    [Fact]
    public async Task Typing_with_the_find_bar_closed_schedules_nothing()
    {
        await WriteFixtureSessionAsync("find-9");
        var vm = MakeVm();
        await vm.LoadAsync("find-9", CancellationToken.None);

        vm.EnterEditMode();                                   // bar never opened
        var section = vm.EditSections[0];
        section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal);
        section.Segments[0].EditedText = "changed";

        Assert.Null(vm.PendingFindRecompute);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewFindTests"`
Expected: BUILD FAILURE — `error CS1729: 'ReadViewViewModel' does not contain a constructor that takes 8 arguments` and `error CS1061: ... does not contain a definition for 'PendingFindRecompute'`.

- [ ] **Step 3: Implement in `ReadViewViewModel.cs`**

(a) Replace the ctor (lines 159-166):

```csharp
    /// <summary>findDebounceMs: item 1's edit-typing find recompute delay; tests pass 0 for a
    /// synchronous, deterministic recompute (the SearchPageViewModel debounce-seam pattern).</summary>
    public ReadViewViewModel(MaintenanceService maintenance, StoragePaths paths,
        ISettingsService settings, IUiErrorReporter reporter, IDualAudioPlayer player,
        Action<Action> dispatch, TimeProvider time, int findDebounceMs = 250)
    {
        (_maintenance, _paths, _settings, _reporter, _dispatch, _time)
            = (maintenance, paths, settings, reporter, dispatch, time);
        _findDebounceMs = findDebounceMs;
        Playback = new PlaybackViewModel(player, dispatch);
    }
```

(b) Beside the find-bar state block (after `private readonly List<int> _findMatchRows = new();`, line 260), add:

```csharp
    private readonly int _findDebounceMs;
    private CancellationTokenSource? _findRecomputeCts;

    /// <summary>Test seam (the SearchPageViewModel.PendingSearch precedent): the in-flight
    /// debounced edit-typing recompute, if any. Null until the first schedule.</summary>
    public Task? PendingFindRecompute { get; private set; }

    /// <summary>Item 1: every EditedText keystroke (via EditableSectionViewModel.LiveTextChanged)
    /// supersedes the previous pending recompute - counts refresh as the user types without a
    /// per-keystroke full scan. No-op while the bar is closed or in read mode.</summary>
    private void ScheduleFindRecompute()
    {
        if (!IsFindOpen || !IsEditMode) return;
        _findRecomputeCts?.Cancel();
        var cts = _findRecomputeCts = new CancellationTokenSource();
        PendingFindRecompute = RunFindRecomputeAsync(cts.Token);
    }

    private async Task RunFindRecomputeAsync(CancellationToken ct)
    {
        try
        {
            if (_findDebounceMs > 0) await Task.Delay(_findDebounceMs, ct);
            if (ct.IsCancellationRequested) return;
            _dispatch(() =>
            {
                if (ct.IsCancellationRequested || !IsEditMode) return;   // superseded / mode left
                RecomputeFindMatches(moveToFirst: false);
            });
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>Detach every section's LiveTextChanged and kill any pending recompute - called on
    /// both exits from Edit mode, right before EditSections is cleared.</summary>
    private void UnwireEditSections()
    {
        foreach (var s in EditSections) s.LiveTextChanged -= ScheduleFindRecompute;
        _findRecomputeCts?.Cancel();
    }
```

(c) In `EnterEditMode` (as written in Task 2), replace the section-building loop with the wired version:

```csharp
        EditSections.Clear();
        foreach (var r in Rows)
            if (!r.Data.IsMarker)
            {
                var section = new EditableSectionViewModel(r.Data);
                section.LiveTextChanged += ScheduleFindRecompute;   // item 1: live-corpus refresh
                EditSections.Add(section);
            }
```

(d) In `CancelEdit` (Task 2 version), insert `UnwireEditSections();` on the line directly above `EditSections.Clear();`.

(e) In `SaveEditsAsync`'s success tail (Task 2 version), insert `UnwireEditSections();` on the line directly above `EditSections.Clear();`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewFindTests"`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```powershell
git add tests\LocalScribe.App.Tests\ReadViewFindTests.cs src\LocalScribe.App\ViewModels\ReadViewViewModel.cs
git commit -m "feat(readview): debounced live-text find recompute while editing (item 1)"
```

---

### Task 4: Row-to-section mapping for MoveFindTo and scroll targets (citation free rider, VM half)

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\ReadViewViewModel.cs` (`MoveFindTo` 328-333 becomes mode-aware; new public `EditSectionIndexOfRow` + `FindScrollTargetForRow`)
- Test: `tests\LocalScribe.App.Tests\ReadViewFindTests.cs` (one new test)

**Interfaces:**
- Consumes: Task 2's `EditSectionIndexOf(DisplayRow)` and mode-space `_findMatchRows`.
- Produces: `public int EditSectionIndexOfRow(int rowIndex)` (Rows index -> EditSections index, marker falls FORWARD to the next non-marker row, -1 when nothing maps); `public int FindScrollTargetForRow(int rowIndex)` (identity in read mode, mapped in edit mode). Task 6's `ApplyFindTarget` consumes both names exactly.

- [ ] **Step 1: Write the failing test**

Add to `tests\LocalScribe.App.Tests\ReadViewFindTests.cs`:

```csharp
    [Fact]
    public async Task MoveFindTo_and_scroll_targets_map_rows_to_sections_in_edit_mode()
    {
        await WriteFixtureSessionAsync("find-10");
        var vm = MakeVm();
        await vm.LoadAsync("find-10", CancellationToken.None);

        vm.OpenFind("morning");
        vm.EnterEditMode();

        Assert.Equal(0, vm.EditSectionIndexOfRow(0));
        Assert.Equal(1, vm.EditSectionIndexOfRow(1));
        Assert.Equal(-1, vm.EditSectionIndexOfRow(2));    // trailing marker: nothing to fall on to

        vm.MoveFindTo(1);                                  // Rows-space input (search-page path)
        Assert.Equal(1, vm.CurrentFindRowIndex);           // landed on the EditSections index
        Assert.Equal("2/2", vm.FindStatus);

        Assert.Equal(1, vm.FindScrollTargetForRow(1));     // edit mode: mapped
        vm.CancelEdit();
        Assert.Equal(1, vm.FindScrollTargetForRow(1));     // read mode: identity
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewFindTests"`
Expected: BUILD FAILURE — `error CS1061: 'ReadViewViewModel' does not contain a definition for 'EditSectionIndexOfRow'` (and `FindScrollTargetForRow`).

- [ ] **Step 3: Implement in `ReadViewViewModel.cs`**

(a) Add beside `EditSectionIndexOf`:

```csharp
    /// <summary>The EditSections index for a Rows index, falling FORWARD past markers (a marker
    /// has no section; the next speaker turn is the natural landing spot). -1 when nothing maps
    /// (out of range, or a trailing marker) or in read mode before any sections exist.</summary>
    public int EditSectionIndexOfRow(int rowIndex)
    {
        for (int i = rowIndex; i >= 0 && i < Rows.Count; i++)
        {
            int si = EditSectionIndexOf(Rows[i].Data);
            if (si >= 0) return si;
        }
        return -1;
    }

    /// <summary>Row-space input -> current-mode find/scroll index (item 1 free rider: search-page
    /// and assistant-citation click-through stop no-oping during edit). Read mode: the row index
    /// itself. Edit mode: the mapped section index. The window scrolls whatever this returns via
    /// its mode-aware helper.</summary>
    public int FindScrollTargetForRow(int rowIndex)
        => IsEditMode ? EditSectionIndexOfRow(rowIndex) : rowIndex;
```

(b) Replace `MoveFindTo` (lines 328-333):

```csharp
    /// <summary>Points the current match at the given ROW (search-page click-through - the input
    /// is always a Rows index). In edit mode the row maps forward to its section first (item 1).
    /// When the target is itself a match it becomes the current match; otherwise - e.g. an
    /// original-text-only hit whose corrected text no longer contains the term - the current match
    /// advances to the first match AFTER the target, and is left unchanged only when no later match
    /// exists. Either way the caller still scrolls the window to the target (B4-4: doc drift).</summary>
    public void MoveFindTo(int rowIndex)
    {
        int target = IsEditMode ? EditSectionIndexOfRow(rowIndex) : rowIndex;
        if (target < 0) return;
        if (_findMatchRows.Contains(target)) { CurrentFindRowIndex = target; return; }
        int after = _findMatchRows.FirstOrDefault(i => i > target, -1);
        if (after >= 0) CurrentFindRowIndex = after;
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewFindTests"`
Expected: PASS (10 tests) — including the untouched pinned test `RowIndexOfSeq_and_MoveFindTo_target_the_snippet_row` (read-mode `MoveFindTo` semantics are byte-identical).

- [ ] **Step 5: Commit**

```powershell
git add tests\LocalScribe.App.Tests\ReadViewFindTests.cs src\LocalScribe.App\ViewModels\ReadViewViewModel.cs
git commit -m "feat(readview): map find targets row-to-section for edit mode (item 1)"
```

---

### Task 5: Caret jump-in (VM half) + the no-phantom-edits regression guarantee

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\EditableSegmentViewModel.cs` (selection-request members after the observable block, lines 41-44)
- Modify: `src\LocalScribe.App\ViewModels\EditableSectionViewModel.cs` (new `LocateMatch`)
- Modify: `src\LocalScribe.App\ViewModels\ReadViewViewModel.cs` (`FindNext`/`FindPrevious` 299-311; `CloseFind`; `CancelEdit`/`SaveEditsAsync` clear the request tracker; new `ExpandSection`, `JumpIntoCurrentEditMatch`, `EditFindJumpRequested`)
- Test: `tests\LocalScribe.App.Tests\EditableSectionFindTests.cs`, `tests\LocalScribe.App.Tests\ReadViewFindTests.cs` (fixture gains `withCorrection` parameter)

**Interfaces:**
- Consumes: Task 1's `SearchText`/`IsEditing`; Task 2's mode-space `CurrentFindRowIndex`; existing `BeginEdit(...)` 5-arg overload, `SpeakerChoicesForRemote()`/`SpeakerChoicesForLocal()` (`ReadViewViewModel.cs:730-733`), `CurrentSpeakerFor` (`ReadViewViewModel.cs:745-747`).
- Produces: `EditableSegmentViewModel.FindSelectionStart` (observable int, -1 = none), `FindSelectionLength` (plain int), `SetFindSelection(int start, int length)`, `ClearFindSelection()`; `EditableSectionViewModel.LocateMatch(string needle)` returning `(int SegmentIndex, int Start, int Length)?`; `ReadViewViewModel.ExpandSection(EditableSectionViewModel)` and `public event Action<int>? EditFindJumpRequested`. Task 6's attached behavior and window wiring consume these exact names.

- [ ] **Step 1: Write the failing section/segment tests**

Add to `tests\LocalScribe.App.Tests\EditableSectionFindTests.cs`:

```csharp
    [Fact]
    public void LocateMatch_maps_the_joined_hit_to_segment_and_offset()
    {
        var section = new EditableSectionViewModel(MakeRow());
        Assert.Null(section.LocateMatch("goodbye"));                  // collapsed: not materialized

        section.BeginEdit("relative", default);
        Assert.Equal((0, 6, 5), section.LocateMatch("world"));        // inside segment 0
        Assert.Equal((1, 0, 7), section.LocateMatch("goodbye"));      // segment 1, offset rebased
        Assert.Equal((0, 6, 5), section.LocateMatch("WORLD"));        // case-insensitive
        Assert.Null(section.LocateMatch("absent"));

        // A match spanning the join space is selectable only in the TextBox it starts in.
        Assert.Equal((0, 6, 5), section.LocateMatch("world goodbye"));
    }

    [Fact]
    public void SetFindSelection_orders_length_before_start_and_clear_resets()
    {
        var section = new EditableSectionViewModel(MakeRow());
        section.BeginEdit("relative", default);
        var seg = section.Segments[0];
        int lengthAtStartChange = -1;
        seg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditableSegmentViewModel.FindSelectionStart))
                lengthAtStartChange = seg.FindSelectionLength;
        };

        seg.SetFindSelection(6, 5);
        Assert.Equal(6, seg.FindSelectionStart);
        Assert.Equal(5, lengthAtStartChange);          // Length was already set when Start fired

        seg.ClearFindSelection();
        Assert.Equal(-1, seg.FindSelectionStart);
    }
```

- [ ] **Step 2: Write the failing VM tests**

In `tests\LocalScribe.App.Tests\ReadViewFindTests.cs`, change the fixture signature (line 38) from `private async Task WriteFixtureSessionAsync(string id)` to:

```csharp
    private async Task WriteFixtureSessionAsync(string id, bool withCorrection = true)
```

and wrap its last statement (the `EditStore ... ApplyTextCorrectionAsync` call, lines 65-66) as:

```csharp
        if (withCorrection)
            await new EditStore(_paths.SessionDir(id), _time)
                .ApplyTextCorrectionAsync(1, "the corrected words", CancellationToken.None);
```

Then add two tests:

```csharp
    [Fact]
    public async Task FindNext_in_edit_mode_expands_the_target_section_and_stamps_a_caret_request()
    {
        await WriteFixtureSessionAsync("find-11");
        var vm = MakeVm();
        await vm.LoadAsync("find-11", CancellationToken.None);
        int jumped = -1;
        vm.EditFindJumpRequested += i => jumped = i;

        vm.OpenFind("morning");
        vm.EnterEditMode();                              // current = section 0
        vm.FindNext();                                   // -> section 1 (Jane)

        Assert.Equal(1, jumped);
        var section = vm.EditSections[1];
        Assert.True(section.IsEditing);                  // auto-expanded (BeginEdit is idempotent)
        var seg = section.Segments[0];                   // "sounds good to me this morning"
        Assert.Equal(seg.EditedText.IndexOf("morning", StringComparison.OrdinalIgnoreCase),
            seg.FindSelectionStart);
        Assert.Equal("morning".Length, seg.FindSelectionLength);

        vm.FindNext();                                   // wraps to section 0
        Assert.Equal(0, jumped);
        Assert.Equal(-1, seg.FindSelectionStart);        // the previous request was cleared
    }

    [Fact]
    public async Task Find_expanded_untouched_sections_write_nothing_on_save()
    {
        await WriteFixtureSessionAsync("find-clean", withCorrection: false);
        var vm = MakeVm();
        await vm.LoadAsync("find-clean", CancellationToken.None);

        vm.OpenFind("morning");
        vm.EnterEditMode();
        vm.FindNext();                                        // jump-in expands section 1
        vm.FindNext();                                        // wraps: expands section 0
        Assert.All(vm.EditSections, s => Assert.True(s.IsEditing));

        await vm.SaveEditsAsync(CancellationToken.None);      // walks BOTH expanded sections

        Assert.Empty(_reporter.Errors);
        Assert.Null(vm.SaveError);
        Assert.False(vm.IsEditMode);
        Assert.False(vm.Edited);                              // no "Edited" badge materialized
        Assert.False(File.Exists(_paths.EditsJson("find-clean")));      // no phantom corrections
        Assert.False(File.Exists(_paths.SpeakersJson("find-clean")));   // no phantom pins
    }
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~EditableSectionFindTests"`
Expected: BUILD FAILURE — `error CS1061: 'EditableSectionViewModel' does not contain a definition for 'LocateMatch'` (and `FindSelectionStart`/`SetFindSelection`/`ClearFindSelection` on the segment, `EditFindJumpRequested` on the VM once the App.Tests project compiles the ReadViewFindTests additions).

- [ ] **Step 4: Implement**

(a) `src\LocalScribe.App\ViewModels\EditableSegmentViewModel.cs` — after the `[ObservableProperty]` block (lines 41-44), add:

```csharp
    /// <summary>Item 1 (UX round 2026-08-02) find jump-in: a one-shot caret-selection REQUEST
    /// stamped by ReadViewViewModel on Enter/Shift+Enter navigation. -1 = none. The FindSelection
    /// attached behavior consumes it (Select + Focus once the TextBox exists) and then clears it,
    /// so a recycled container scrolling back can never replay a stale focus-steal. Length is a
    /// plain property set BEFORE Start on purpose: the behavior reacts to Start's PropertyChanged
    /// and reads Length in the same handler - never torn.</summary>
    [ObservableProperty] private int _findSelectionStart = -1;
    public int FindSelectionLength { get; private set; }

    public void SetFindSelection(int start, int length)
    {
        FindSelectionLength = length;
        FindSelectionStart = start;
    }

    public void ClearFindSelection() => SetFindSelection(-1, 0);
```

(b) `src\LocalScribe.App\ViewModels\EditableSectionViewModel.cs` — add below `SearchText`:

```csharp
    /// <summary>Locates the FIRST case-insensitive occurrence of needle in the live joined text,
    /// as (segment index, char offset within that segment's EditedText, selectable length). The
    /// length is clipped at the segment boundary - a match spanning the join can only be selected
    /// inside the TextBox it starts in. Null when not materialized (call BeginEdit first), the
    /// needle is empty, or there is no hit. The needle must be pre-trimmed (FindText.Trim()): the
    /// join separator is a space, so a trimmed needle can never START on a join boundary.</summary>
    public (int SegmentIndex, int Start, int Length)? LocateMatch(string needle)
    {
        if (!IsEditing || needle.Length == 0) return null;
        string joined = string.Join(" ", Segments.Select(s => s.EditedText));
        int at = joined.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        int offset = 0;
        for (int i = 0; i < Segments.Count; i++)
        {
            int len = Segments[i].EditedText.Length;
            if (at < offset + len)
            {
                int start = at - offset;
                return (i, start, Math.Min(needle.Length, len - start));
            }
            offset += len + 1;                                    // + the single join space
        }
        return null;
    }
```

(c) `src\LocalScribe.App\ViewModels\ReadViewViewModel.cs` — add beside the find members:

```csharp
    /// <summary>Item 1: the window scrolls/realizes the target section when an Enter/Shift+Enter
    /// navigation jumped the caret. A dedicated event (not the CurrentFindRowIndex hook) because
    /// the index may NOT change - a single match navigated twice still needs the scroll+realize.</summary>
    public event Action<int>? EditFindJumpRequested;

    private EditableSegmentViewModel? _lastFindSelectionSegment;

    /// <summary>Expands a section with the same arguments the window's OnEditRowActivated click
    /// path passes (BeginEdit is idempotent). Public: find jump-in and tests share it. Only safe
    /// once loaded (SpeakerChoicesFor* rely on _loadedMeta) - Edit mode guarantees that.</summary>
    public void ExpandSection(EditableSectionViewModel section)
        => section.BeginEdit(TimestampsMode, StartedAtLocal,
            SpeakerChoicesForRemote(), SpeakerChoicesForLocal(), CurrentSpeakerFor);

    /// <summary>Enter/Shift+Enter in edit mode: auto-expand the current match's section and stamp
    /// a one-shot caret request on the segment containing the match. The previous request is
    /// cleared first so an unrealized container can never replay a stale focus-steal. Read mode:
    /// no-op (navigation there is scroll-only, as today).</summary>
    private void JumpIntoCurrentEditMatch()
    {
        if (!IsEditMode || !IsFindOpen) return;
        _lastFindSelectionSegment?.ClearFindSelection();
        _lastFindSelectionSegment = null;
        if (CurrentFindRowIndex < 0 || CurrentFindRowIndex >= EditSections.Count) return;
        var section = EditSections[CurrentFindRowIndex];
        ExpandSection(section);
        if (section.LocateMatch(FindText.Trim()) is not { } m) return;
        var seg = section.Segments[m.SegmentIndex];
        seg.SetFindSelection(m.Start, m.Length);
        _lastFindSelectionSegment = seg;
        EditFindJumpRequested?.Invoke(CurrentFindRowIndex);
    }
```

(d) Replace `FindNext`/`FindPrevious` (lines 299-311):

```csharp
    public void FindNext()
    {
        if (_findMatchRows.Count == 0) return;
        int pos = _findMatchRows.IndexOf(CurrentFindRowIndex);
        CurrentFindRowIndex = _findMatchRows[(pos + 1) % _findMatchRows.Count];   // pos -1 -> first
        JumpIntoCurrentEditMatch();
    }

    public void FindPrevious()
    {
        if (_findMatchRows.Count == 0) return;
        int pos = _findMatchRows.IndexOf(CurrentFindRowIndex);
        CurrentFindRowIndex = _findMatchRows[pos <= 0 ? _findMatchRows.Count - 1 : pos - 1];
        JumpIntoCurrentEditMatch();
    }
```

(e) In `CloseFind`, after the `EditSections` flag-clearing loop (added in Task 2), insert:

```csharp
        _lastFindSelectionSegment?.ClearFindSelection();
        _lastFindSelectionSegment = null;
```

(f) In `CancelEdit` and in `SaveEditsAsync`'s success tail, insert `_lastFindSelectionSegment = null;` directly after their `UnwireEditSections();` line (the segments die with the sections; only the tracker must not dangle).

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~EditableSectionFindTests"`
Expected: PASS (5 tests).
Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewFindTests"`
Expected: PASS (12 tests).

- [ ] **Step 6: Commit**

```powershell
git add tests\LocalScribe.App.Tests\EditableSectionFindTests.cs tests\LocalScribe.App.Tests\ReadViewFindTests.cs src\LocalScribe.App\ViewModels\EditableSegmentViewModel.cs src\LocalScribe.App\ViewModels\EditableSectionViewModel.cs src\LocalScribe.App\ViewModels\ReadViewViewModel.cs
git commit -m "feat(readview): find jump-in caret request + no-phantom-edit regression pin (item 1)"
```

---

### Task 6: View wiring — EditList highlights, FindSelection behavior, mode-aware find scroll

View-layer only (attached behavior, XAML triggers, `ScrollIntoView` targeting): NOT unit-testable here. The task ends with a build gate and smoke-runbook additions instead of a fake test.

**Files:**
- Create: `src\LocalScribe.App\FindSelection.cs`
- Modify: `src\LocalScribe.App\ReadViewWindow.xaml` (EditList `ItemContainerStyle` 451-456; segment TextBox 517-519)
- Modify: `src\LocalScribe.App\ReadViewWindow.xaml.cs` (ctor subscription block around 146; `OnVmPropertyChanged` 310-318; `ApplyFindTarget` 329-341; `OnClosed` 495-517)
- Modify: `docs\plans\2026-07-07-transcript-editor-smoke-runbook.md` (new Part G appended after Part F)

**Interfaces:**
- Consumes: Task 1's `IsFindMatch`/`IsCurrentFindMatch` (binding paths); Task 4's `FindScrollTargetForRow`; Task 5's `FindSelectionStart`/`FindSelectionLength`/`ClearFindSelection` and `EditFindJumpRequested`.
- Produces: `public static class FindSelection` with attached bool `Enable`; `private void ScrollFindTargetIntoView(int index)` on `ReadViewWindow` (Task 8 does not use it, but the transport plan's review should see one scroll entry point for find).

- [ ] **Step 1: Create the attached behavior `src\LocalScribe.App\FindSelection.cs`**

```csharp
// src/LocalScribe.App/FindSelection.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App;

/// <summary>Attached behavior that turns a segment VM's one-shot FindSelection request into
/// TextBox.Select + Focus (item 1, UX round 2026-08-02) - the SegmentText pattern, so the VM
/// stays WPF-free. Tolerates unrealized containers: a request stamped before the virtualized
/// EditList realized this row is applied on Loaded/DataContextChanged instead of being lost.
/// One-shot: the request is cleared after applying, so a recycled container scrolling back into
/// view can never steal focus again. Recycling-safe: DataContextChanged re-points the segment
/// subscription, tearing down the old handler first (ConditionalWeakTable, as SegmentText).</summary>
public static class FindSelection
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(FindSelection), new PropertyMetadata(false, OnEnableChanged));
    public static void SetEnable(DependencyObject o, bool v) => o.SetValue(EnableProperty, v);
    public static bool GetEnable(DependencyObject o) => (bool)o.GetValue(EnableProperty);

    private sealed class Hook
    {
        public EditableSegmentViewModel? Segment;
        public PropertyChangedEventHandler? Handler;
    }
    private static readonly ConditionalWeakTable<TextBox, Hook> _state = new();

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        if ((bool)e.NewValue)
        {
            tb.DataContextChanged += OnDataContextChanged;
            tb.Loaded += OnLoaded;
            Attach(tb, tb.DataContext as EditableSegmentViewModel);
        }
        else
        {
            tb.DataContextChanged -= OnDataContextChanged;
            tb.Loaded -= OnLoaded;
            Attach(tb, null);
        }
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb) Attach(tb, e.NewValue as EditableSegmentViewModel);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A container realized AFTER the navigation stamped the request (virtualized list).
        if (sender is TextBox tb && tb.DataContext is EditableSegmentViewModel seg) Apply(tb, seg);
    }

    private static void Attach(TextBox tb, EditableSegmentViewModel? seg)
    {
        if (_state.TryGetValue(tb, out var old))
        {
            if (old.Segment is not null && old.Handler is not null)
                old.Segment.PropertyChanged -= old.Handler;
            _state.Remove(tb);
        }
        if (seg is null) return;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(EditableSegmentViewModel.FindSelectionStart))
                Apply(tb, seg);
        };
        seg.PropertyChanged += handler;
        _state.Add(tb, new Hook { Segment = seg, Handler = handler });
        Apply(tb, seg);   // a request stamped before this container existed
    }

    /// <summary>Deferred so it runs after the expand/scroll layout pass; re-reads the request at
    /// apply time (a newer navigation may have moved or cleared it), then clears it (one-shot).</summary>
    private static void Apply(TextBox tb, EditableSegmentViewModel seg)
    {
        if (seg.FindSelectionStart < 0) return;
        tb.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            int start = seg.FindSelectionStart;
            if (start < 0) return;                              // superseded meanwhile
            if (!ReferenceEquals(tb.DataContext, seg)) return;   // container recycled meanwhile
            int clampedStart = Math.Min(start, tb.Text.Length);
            int len = Math.Max(0, Math.Min(seg.FindSelectionLength, tb.Text.Length - clampedStart));
            tb.Focus();
            tb.Select(clampedStart, len);
            tb.BringIntoView();
            seg.ClearFindSelection();
        });
    }
}
```

- [ ] **Step 2: XAML — highlight triggers + behavior opt-in**

In `src\LocalScribe.App\ReadViewWindow.xaml`, replace EditList's `ItemContainerStyle` (lines 451-456):

```xml
            <ListView.ItemContainerStyle>
                <Style TargetType="ListViewItem">
                    <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                    <Setter Property="Margin" Value="0,0,0,10" />
                    <Style.Triggers>
                        <!-- Edit-aware Find (UX round 2026-08-02 item 1): the same two tints as
                             RowList's find triggers, driven by the section VM's mirrored flags.
                             Theme resources only (no ARGB literals - XamlHygiene). -->
                        <DataTrigger Binding="{Binding IsFindMatch}" Value="True">
                            <Setter Property="Background">
                                <Setter.Value>
                                    <SolidColorBrush Color="{DynamicResource SystemAccentColor}" Opacity="0.14" />
                                </Setter.Value>
                            </Setter>
                        </DataTrigger>
                        <DataTrigger Binding="{Binding IsCurrentFindMatch}" Value="True">
                            <Setter Property="Background">
                                <Setter.Value>
                                    <SolidColorBrush Color="{DynamicResource SystemAccentColor}" Opacity="0.45" />
                                </Setter.Value>
                            </Setter>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </ListView.ItemContainerStyle>
```

And replace the segment TextBox (lines 517-519):

```xml
                                            <TextBox Text="{Binding EditedText, UpdateSourceTrigger=PropertyChanged}"
                                                     AcceptsReturn="False" TextWrapping="Wrap"
                                                     local:FindSelection.Enable="True"
                                                     PreviewKeyDown="OnSegmentTextBoxPreviewKeyDown" />
```

- [ ] **Step 3: Code-behind — mode-aware scroll + jump realization**

In `src\LocalScribe.App\ReadViewWindow.xaml.cs`:

(a) Replace `OnVmPropertyChanged` (lines 310-318) and add the helper + jump handler below it:

```csharp
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReadViewViewModel.IsFindOpen) && _vm.IsFindOpen)
            // The bar only just became visible - focus on the next dispatcher turn.
            Dispatcher.BeginInvoke(() => { FindBox.Focus(); FindBox.SelectAll(); });
        else if (e.PropertyName == nameof(ReadViewViewModel.CurrentFindRowIndex))
            ScrollFindTargetIntoView(_vm.CurrentFindRowIndex);
    }

    /// <summary>Mode-aware find scroll (item 1): the visible list is RowList in read mode and
    /// EditList in edit mode; the index is Rows-space or EditSections-space respectively (the
    /// VM's mode-space contract). Out-of-range indices (including -1) are ignored.</summary>
    private void ScrollFindTargetIntoView(int index)
    {
        if (_vm.IsEditMode)
        {
            if (index >= 0 && index < _vm.EditSections.Count)
                EditList.ScrollIntoView(_vm.EditSections[index]);
        }
        else if (index >= 0 && index < _vm.Rows.Count)
            RowList.ScrollIntoView(_vm.Rows[index]);
    }

    /// <summary>Item 1 jump-in: scroll + realize the target section so the FindSelection
    /// behavior's Loaded hook can apply the pending caret request - needed even when the current
    /// index did NOT change (a single match navigated twice raises no PropertyChanged).</summary>
    private void OnEditFindJump(int sectionIndex)
    {
        ScrollFindTargetIntoView(sectionIndex);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => EditList.UpdateLayout());
    }
```

(b) Replace `ApplyFindTarget` (lines 329-341):

```csharp
    private void ApplyFindTarget(int seq, string term)
    {
        _vm.OpenFind(term);
        int row = _vm.RowIndexOfSeq(seq);
        if (row < 0) return;
        _vm.MoveFindTo(row);
        // Scroll to the target row even when it is not itself a find match (an original-text-
        // only hit: the corrected text no longer contains the term, so the bar shows 0/0 -
        // truthful - but the reader still lands on the right segment). In edit mode the row maps
        // forward to its section (item 1 free rider: citations stop no-oping during edit).
        ScrollFindTargetIntoView(_vm.FindScrollTargetForRow(row));
    }
```

(c) In the ctor, directly under `_vm.PropertyChanged += OnVmPropertyChanged;` (line 146), add:

```csharp
        // Item 1 jump-in realization; same per-session lifecycle - OnClosed MUST unsubscribe.
        _vm.EditFindJumpRequested += OnEditFindJump;
```

(d) In `OnClosed`, directly under `_vm.PropertyChanged -= OnVmPropertyChanged;` (line 503), add:

```csharp
        _vm.EditFindJumpRequested -= OnEditFindJump;
```

- [ ] **Step 4: Build gate**

Close any running `LocalScribe.App.exe` first (locks Core.dll -> MSB3027).
Run: `dotnet build F:\LocalScribe\LocalScribe.slnx`
Expected: Build succeeded, 0 warnings (the repo runs a 0-warning gate).
Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewFindTests"`
Expected: PASS (no VM behavior changed in this task).

- [ ] **Step 5: Smoke-runbook additions**

Append to `docs\plans\2026-07-07-transcript-editor-smoke-runbook.md` (after Part F, before "Notes / accepted quirks"):

```markdown
## Part G: Find during edit (UX round 2026-08-02, item 1)

- [ ] **G1 Bar opens while editing:** enter Edit mode, press **Ctrl+F** (and separately click
  **Find**) — the find bar opens; it no longer silently refuses.
- [ ] **G2 Edit-list highlights:** type a term present in several visible sections — matching
  sections tint faintly, the current one strongly, and the count (e.g. "1/4") shows; markers do
  not count while editing.
- [ ] **G3 Jump-in caret:** press **Enter** in the find box — the current match's section
  expands (if collapsed), scrolls into view, and the matched word is SELECTED inside its text
  box with keyboard focus there. **Shift+Enter** walks backwards the same way.
- [ ] **G4 Live text is the corpus:** in an expanded section, type a nonsense word (e.g.
  "zzqq") into a text box, then find "zzqq" — 1/1 and the section tints. Delete the word —
  the count returns to 0/0 after a moment (debounced, ~250 ms).
- [ ] **G5 Bar survives mode changes:** with a term active, click Edit, then Cancel, then Edit,
  then Save — the bar stays open with the same term throughout and the counts recompute on
  each transition.
- [ ] **G6 Citation click-through during edit:** with the read view in Edit mode, click a
  search-page result (or assistant citation chip) for that session — the edit table scrolls to
  the target section instead of doing nothing.
- [ ] **G7 No phantom edits from Find:** on a never-edited session, enter Edit, find a term,
  Enter through every match (expanding several sections), then **Save** without typing — no
  "Edited" badge appears and `edits.json` is not created.
```

- [ ] **Step 6: Commit**

```powershell
git add src\LocalScribe.App\FindSelection.cs src\LocalScribe.App\ReadViewWindow.xaml src\LocalScribe.App\ReadViewWindow.xaml.cs docs\plans\2026-07-07-transcript-editor-smoke-runbook.md
git commit -m "feat(readview): edit-list find highlights, caret jump-in behavior, mode-aware scroll (item 1)"
```

---

### Task 7: Hoist the duplicated FindScrollViewer into a shared static helper

Pure view-layer refactor (visual-tree walk) — no unit test is possible; the gate is a clean build plus the final full-suite run. This plan owns the hoist; the playback-transport plan consumes `ScrollHelpers.FindScrollViewer`.

**Files:**
- Create: `src\LocalScribe.App\ScrollHelpers.cs`
- Modify: `src\LocalScribe.App\ReadViewWindow.xaml.cs` (delete private `FindScrollViewer` 468-477; retarget the call at 458)
- Modify: `src\LocalScribe.App\LiveViewWindow.xaml.cs` (delete private `FindScrollViewer` 200-209; retarget the call at 129)

**Interfaces:**
- Consumes: nothing new.
- Produces: `public static class ScrollHelpers { public static ScrollViewer? FindScrollViewer(DependencyObject root); }` — Task 8 and the future transport plan consume this exact name.

- [ ] **Step 1: Create `src\LocalScribe.App\ScrollHelpers.cs`**

```csharp
// src/LocalScribe.App/ScrollHelpers.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace LocalScribe.App;

/// <summary>Shared visual-tree scroll utilities (UX round 2026-08-02 items 1+2). Hoisted from the
/// two verbatim private FindScrollViewer copies in ReadViewWindow/LiveViewWindow so the read
/// view's anchor helpers (item 2) and the transport plan's sync-follow (items 7-9) build on ONE
/// lookup instead of a third copy.</summary>
public static class ScrollHelpers
{
    /// <summary>The first ScrollViewer beneath root (a ListView's template scroll host), or null
    /// before the control's template has been applied.</summary>
    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
```

- [ ] **Step 2: Retarget both windows**

In `src\LocalScribe.App\ReadViewWindow.xaml.cs`:
- Line 458: change `var scroll = FindScrollViewer(RowList);` to `var scroll = ScrollHelpers.FindScrollViewer(RowList);`
- Delete the whole private `FindScrollViewer` method (lines 468-477).

In `src\LocalScribe.App\LiveViewWindow.xaml.cs`:
- Line 129: change `if (FindScrollViewer(LineList) is { } sv)` to `if (ScrollHelpers.FindScrollViewer(LineList) is { } sv)`
- Delete the whole private `FindScrollViewer` method (lines 200-209).

- [ ] **Step 3: Build gate**

Close any running `LocalScribe.App.exe` first.
Run: `dotnet build F:\LocalScribe\LocalScribe.slnx`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```powershell
git add src\LocalScribe.App\ScrollHelpers.cs src\LocalScribe.App\ReadViewWindow.xaml.cs src\LocalScribe.App\LiveViewWindow.xaml.cs
git commit -m "feat(readview): hoist FindScrollViewer into shared ScrollHelpers (items 1+2 prep)"
```

---

### Task 8: Edit/Save scroll anchoring — topmost-visible anchor + same-viewport-Y restore

View-layer per the spec's explicit Decision ("View-layer anchor fix (no VM changes)") — no unit tests; the deliverable's verification is Part H of the smoke runbook. Cancel deliberately gets NO restore code: it does not rebuild `Rows` and never hides RowList's ScrollViewer offset, so per the spec it is verified first (H3) and only fixed if the smoke shows drift.

**Files:**
- Modify: `src\LocalScribe.App\ReadViewWindow.xaml.cs` (ctor command wiring 112-113 + the comment block 81-86; new `TopVisibleItem`, `ScrollItemToViewportY`, `EnterEditPreservingScroll`, `SaveEditsPreservingScrollAsync` beside `ReloadPreservingScrollAsync` at 456-466)
- Modify: `docs\plans\2026-07-07-transcript-editor-smoke-runbook.md` (Part H appended after Task 6's Part G)

**Interfaces:**
- Consumes: Task 7's `ScrollHelpers.FindScrollViewer`; existing `ReadViewViewModel.EnterEditMode()` / `SaveEditsAsync(CancellationToken)` / `IsEditMode` / `Rows` / `EditSections`; `EditableSectionViewModel.Row` (same `DisplayRow` instance as `ReadRow.Data` — ReferenceEquals mapping, never `==`).
- Produces: nothing consumed by later tasks (final shape of the window's Edit/Save paths).

- [ ] **Step 1: Add the two anchor helpers**

In `src\LocalScribe.App\ReadViewWindow.xaml.cs`, directly below `ReloadPreservingScrollAsync` (lines 456-466), add:

```csharp
    /// <summary>Item 2 (UX round 2026-08-02): the realized item whose container is topmost in the
    /// list's viewport, plus its Y offset within the viewport. Realized containers only - a
    /// virtualized list has no containers for off-screen items, and the anchor is by definition
    /// on screen. Null when the template has not applied yet or nothing is visible.</summary>
    private static (object Item, double ViewportY)? TopVisibleItem(ListView list)
    {
        if (ScrollHelpers.FindScrollViewer(list) is not { } sv) return null;
        object? best = null;
        double bestY = double.MaxValue;
        foreach (var item in list.Items)
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement c
                || !c.IsVisible)
                continue;
            double y = c.TransformToAncestor(sv).Transform(default).Y;
            if (y + c.ActualHeight <= 0 || y >= sv.ViewportHeight) continue;   // outside viewport
            if (y < bestY) { bestY = y; best = item; }
        }
        return best is null ? null : (best, bestY);
    }

    /// <summary>Scrolls the list so item's container lands at the given viewport Y. ScrollIntoView
    /// alone only guarantees edge visibility, so a correction pass re-aligns to the captured Y -
    /// pixel scrolling (both lists set ScrollUnit=Pixel) makes offset math exact.</summary>
    private static void ScrollItemToViewportY(ListView list, object item, double viewportY)
    {
        list.ScrollIntoView(item);
        list.UpdateLayout();                                          // realize the container first
        if (ScrollHelpers.FindScrollViewer(list) is not { } sv) return;
        if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement c) return;
        double y = c.TransformToAncestor(sv).Transform(default).Y;
        sv.ScrollToVerticalOffset(sv.VerticalOffset + (y - viewportY));
    }
```

- [ ] **Step 2: Add the Edit/Save wrappers and rewire the commands**

(a) Below the helpers from Step 1, add:

```csharp
    /// <summary>Item 2: capture the topmost visible read row, enter Edit, then scroll its twin
    /// section to the same viewport Y. Deferred to Loaded priority WITH an explicit UpdateLayout:
    /// EditList was Collapsed until IsEditMode flipped, so it has never measured - a synchronous
    /// scroll would clamp to offset 0. Twin lookup is ReferenceEquals on the shared DisplayRow
    /// instance (the section wraps the very object the ReadRow holds; DisplayRow is a record, so
    /// == is value equality and could hit a lookalike row). A marker anchor falls FORWARD to the
    /// next non-marker row - markers have no edit section.</summary>
    private void EnterEditPreservingScroll()
    {
        var anchor = TopVisibleItem(RowList);
        _vm.EnterEditMode();
        if (!_vm.IsEditMode || anchor is not { } a) return;   // gate refused, or nothing visible
        int i = _vm.Rows.IndexOf((ReadRow)a.Item);
        while (i >= 0 && i < _vm.Rows.Count && _vm.Rows[i].Data.IsMarker) i++;
        if (i < 0 || i >= _vm.Rows.Count) return;
        var data = _vm.Rows[i].Data;
        var section = _vm.EditSections.FirstOrDefault(s => ReferenceEquals(s.Row, data));
        if (section is null) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            EditList.UpdateLayout();
            ScrollItemToViewportY(EditList, section, a.ViewportY);
        });
    }

    /// <summary>Item 2: Save rebuilds Rows wholesale (ReloadRowsAsync inside SaveEditsAsync), so
    /// the pre-save anchor is re-found BY VALUE - first segment Seq, then StartMs - never by
    /// reference. Mirrors ReloadPreservingScrollAsync's deferral (layout must run over the new
    /// rows before any offset math is valid). A failed save keeps IsEditMode true: scroll nothing,
    /// the user is exactly where they were.</summary>
    private async Task SaveEditsPreservingScrollAsync()
    {
        var anchor = TopVisibleItem(EditList);
        long anchorStart = -1;
        int anchorSeq = -1;
        double viewportY = 0;
        if (anchor is { } a && a.Item is EditableSectionViewModel s)
        {
            anchorStart = s.Row.StartMs;
            anchorSeq = s.Row.Segments.Count > 0 ? s.Row.Segments[0].Seq : -1;
            viewportY = a.ViewportY;
        }
        await _vm.SaveEditsAsync(CancellationToken.None);
        if (_vm.IsEditMode || anchorStart < 0) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            RowList.UpdateLayout();
            var target = (anchorSeq >= 0
                    ? _vm.Rows.FirstOrDefault(r => r.Data.Segments.Any(seg => seg.Seq == anchorSeq))
                    : null)
                ?? _vm.Rows.FirstOrDefault(r => !r.Data.IsMarker && r.Data.StartMs >= anchorStart);
            if (target is not null) ScrollItemToViewportY(RowList, target, viewportY);
        });
    }
```

(b) In the ctor, replace lines 112-113:

```csharp
        EnterEditCommand = new RelayCommand(vm.EnterEditMode);
        SaveEditsCommand = new AsyncRelayCommand(() => vm.SaveEditsAsync(CancellationToken.None));
```

with:

```csharp
        // Item 2 (UX round 2026-08-02): Edit and Save route through the window's anchor-preserving
        // wrappers (instance methods are safe here - they run at click time, long after _vm is
        // assigned; only IMMEDIATE ctor-time invocation needs the `vm` parameter). Cancel stays a
        // bare passthrough: it does not rebuild Rows and RowList's own offset survives the
        // visibility swap (verified by runbook H3, per the spec's verify-first decision).
        EnterEditCommand = new RelayCommand(EnterEditPreservingScroll);
        SaveEditsCommand = new AsyncRelayCommand(SaveEditsPreservingScrollAsync);
```

(c) In the comment block at lines 81-86 (above `public IRelayCommand EnterEditCommand { get; }`), append one line to the existing comment so it stays truthful:

```csharp
    // Item 2: EnterEdit/SaveEdits now bind to anchor-preserving window methods (see
    // EnterEditPreservingScroll / SaveEditsPreservingScrollAsync below); Cancel remains a VM passthrough.
```

- [ ] **Step 3: Build gate**

Close any running `LocalScribe.App.exe` first.
Run: `dotnet build F:\LocalScribe\LocalScribe.slnx`
Expected: Build succeeded, 0 warnings.
Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewEditModeTests"`
Expected: PASS (the VM was not touched in this task).

- [ ] **Step 4: Smoke-runbook additions**

Append to `docs\plans\2026-07-07-transcript-editor-smoke-runbook.md`, after Part G (added by the find task):

```markdown
## Part H: Edit/Save scroll anchoring (UX round 2026-08-02, item 2)

Use a long session (the Part E synthetic one is ideal) so "at depth" means several screens down.

- [ ] **H1 Enter Edit holds position:** scroll to roughly the middle, note the topmost visible
  turn, click **Edit** — the editable table opens with that same turn at the same height, not
  at the top of the transcript.
- [ ] **H2 Enter Edit with a marker above the fold:** scroll so a marker row (e.g. a device-
  change note) is the topmost visible line, click **Edit** — the table lands on the next
  speaker turn below the marker (markers have no edit row), still at depth.
- [ ] **H3 Cancel returns at depth:** from H1's position, click **Cancel** — the read list is
  back at the same scroll position. Cancel deliberately has NO restore code (it never rebuilds
  the rows); if this drifts on a long transcript, record it as a defect so the symmetric
  restore can be added.
- [ ] **H4 Save returns at depth:** in Edit mode at depth, change one word in a visible
  section, click **Save** — the read list shows the saved text with the same turn still at the
  same height (rows were rebuilt; the anchor is re-found by value).
```

- [ ] **Step 5: Commit**

```powershell
git add src\LocalScribe.App\ReadViewWindow.xaml.cs docs\plans\2026-07-07-transcript-editor-smoke-runbook.md
git commit -m "fix(readview): keep scroll position across Edit/Save transitions (item 2)"
```

---

### Task 9: Full-suite gate

**Files:**
- Modify: none expected — only whatever a regression fix requires.
- Test: both full suites.

**Interfaces:**
- Consumes: everything above. Produces: a green branch.

- [ ] **Step 1: Close any running `LocalScribe.App.exe`** (locks Core.dll -> MSB3027). Target only that process:

```powershell
Get-Process LocalScribe.App -ErrorAction SilentlyContinue | Stop-Process
```

- [ ] **Step 2: Run the FULL App suite**

Run: `dotnet test tests\LocalScribe.App.Tests`
Expected: PASS, 0 failures (838 pre-existing tests + the ~12 added by Tasks 1-5). Pay attention to `ReadViewViewModelTests`, `ReadViewEditModeTests`, `SearchPageViewModelTests` — the classes most likely to notice the ctor/transition changes.

- [ ] **Step 3: Run the FULL Core suite**

Run: `dotnet test tests\LocalScribe.Core.Tests`
Expected: PASS, 0 failures (this plan never touches Core; any failure here is environmental or a rebase artifact — investigate, do not skip).

- [ ] **Step 4: Fix any regression found (test-first if it is a real behavior bug), re-run both suites to green.**

- [ ] **Step 5: Verify the runbook additions landed** — `docs\plans\2026-07-07-transcript-editor-smoke-runbook.md` must contain Part G (G1-G7) and Part H (H1-H4). If a fix in Step 4 changed behavior a runbook line describes, update that line in the same commit.

- [ ] **Step 6: Commit (only if Steps 4-5 changed anything)**

```powershell
git add -A
git commit -m "test(readview): full-suite gate for the edit-find + scroll-anchor round"
```
