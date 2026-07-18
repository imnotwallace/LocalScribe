# UX Round 2026-07-18 Implementation Plan (pagination, search entry, Matters overhaul, console label)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the six approved items of `docs/plans/2026-07-18-ux-pagination-search-matters-design.md`: a shared classic pager (Sessions list, Search results, Matters tagged-sessions), the Search "All matters"/"All apps" display-sentinel fix, a visible Find button + "Search all sessions" escalation in the transcript read view, the Matters right-pane header+tabs overhaul with an Add-sessions picker and Open-opens-transcript, and the Record-console "Microphone" label truncation fix.

**Architecture:** One branch, `feat/ux-round-2026-07-18`, tasks land smallest-dependency-first: the WPF-free `PagerViewModel` first, then each screen consumes it. All VM changes stay WPF-free (injected `Action<Action>` dispatch, events consumed by `App.xaml.cs` wiring or page code-behind — the existing house pattern). Views stay humble shells.

**Tech Stack:** .NET 8 WPF, WPF-UI (`ui:` = `http://schemas.lepo.co/wpfui/2022/xaml`), CommunityToolkit.Mvvm (`[ObservableProperty]`, `RelayCommand`), xUnit.

## Global Constraints

- Branch: `feat/ux-round-2026-07-18` off `master`. Commit per task with the message given in the task.
- Build gate: `dotnet build LocalScribe.slnx` must produce **0 warnings** (repo rule). Running LocalScribe.App.exe locks Core.dll → MSB3027 copy error; that is not a compile error — close the app first.
- Tests: `dotnet test tests/LocalScribe.App.Tests` must be all-green. `dotnet test tests/LocalScribe.Core.Tests` has **2 known fixture failures** (pre-existing); no new failures allowed.
- ViewModels are WPF-free: no `System.Windows` types; UI mutations marshal through the injected `_dispatch`. Events raised by VMs are wired in `App.xaml.cs` or page code-behind.
- Never use Unicode emojis in test scripts (user rule). Pager buttons use the words "Previous"/"Next", not glyphs.
- Evidentiary rules: transcripts/audio are never deleted or hidden by anything in this plan; tagging writes ONLY `meta.json` via `MaintenanceService.SaveMetaAsync`.
- Locked user decisions (do not re-litigate): classic pager, default 50/page, sizes 25/50/100; Matters right pane = compact display-only header + tabs `Details | Sessions | Vocabulary | Advanced` opening on **Sessions**; Matters "Open" opens the **transcript** (Details demoted to secondary); Add-sessions = multi-select picker dialog; Search facet defaults are a display-sentinel bug fix, not a redesign.
- Existing test files own their VM-construction helpers. Where a task says "use the file's existing factory helper", read that test file first and reuse its existing `MakeVm`/`WriteSession`-style helpers verbatim (adapting only names) — do NOT invent a parallel harness.

---

### Task 1: `PagerViewModel` (shared, WPF-free) + tests

**Files:**
- Create: `src/LocalScribe.App/ViewModels/PagerViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/PagerViewModelTests.cs`

**Interfaces:**
- Consumes: nothing (leaf component).
- Produces (used by Tasks 2, 4, 7, and the Task 3 control):
  - `int CurrentPage` (1-based, observable), `int PageSize` (observable, default 50), `int TotalCount` (observable)
  - `int PageCount` (derived, min 1), `bool HasItems` (TotalCount > 0), `string PageText` ("Page N of M"), `bool CanGoPrev`, `bool CanGoNext`
  - `IRelayCommand PrevCommand`, `IRelayCommand NextCommand`
  - `static IReadOnlyList<int> PageSizeChoices` = [25, 50, 100]
  - `event Action? Changed` — raised ONLY on user-driven changes (Prev/Next/bound CurrentPage set, PageSize set); host re-slices in the handler
  - `void SetTotal(int count)` — updates TotalCount and clamps CurrentPage into [1, PageCount] WITHOUT raising Changed
  - `void Reset()` — CurrentPage = 1 WITHOUT raising Changed
  - `IReadOnlyList<T> Slice<T>(IReadOnlyList<T> items)` — the current page's window

- [ ] **Step 1: Create the branch**

```bash
git checkout master && git checkout -b feat/ux-round-2026-07-18
```

- [ ] **Step 2: Write the failing tests**

Create `tests/LocalScribe.App.Tests/PagerViewModelTests.cs`:

```csharp
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class PagerViewModelTests
{
    [Fact]
    public void Defaults_are_page_1_size_50_empty()
    {
        var p = new PagerViewModel();
        Assert.Equal(1, p.CurrentPage);
        Assert.Equal(50, p.PageSize);
        Assert.Equal(0, p.TotalCount);
        Assert.Equal(1, p.PageCount);          // min 1 even when empty
        Assert.False(p.HasItems);
        Assert.False(p.CanGoPrev);
        Assert.False(p.CanGoNext);
        Assert.Equal(new[] { 25, 50, 100 }, PagerViewModel.PageSizeChoices);
    }

    [Theory]
    [InlineData(0, 50, 1)]
    [InlineData(1, 50, 1)]
    [InlineData(50, 50, 1)]
    [InlineData(51, 50, 2)]
    [InlineData(100, 25, 4)]
    [InlineData(101, 25, 5)]
    public void PageCount_is_ceiling_of_total_over_size(int total, int size, int expected)
    {
        var p = new PagerViewModel { PageSize = size };
        p.SetTotal(total);
        Assert.Equal(expected, p.PageCount);
    }

    [Fact]
    public void Next_and_prev_move_within_bounds_and_raise_Changed()
    {
        var p = new PagerViewModel { PageSize = 25 };
        p.SetTotal(60);                        // 3 pages
        int changed = 0;
        p.Changed += () => changed++;

        Assert.True(p.NextCommand.CanExecute(null));
        p.NextCommand.Execute(null);
        Assert.Equal(2, p.CurrentPage);
        p.NextCommand.Execute(null);
        Assert.Equal(3, p.CurrentPage);
        Assert.False(p.CanGoNext);
        p.PrevCommand.Execute(null);
        Assert.Equal(2, p.CurrentPage);
        Assert.Equal(3, changed);
        Assert.Equal("Page 2 of 3", p.PageText);
    }

    [Fact]
    public void SetTotal_clamps_current_page_silently()
    {
        var p = new PagerViewModel { PageSize = 25 };
        p.SetTotal(100);                       // 4 pages
        p.CurrentPage = 4;
        int changed = 0;
        p.Changed += () => changed++;
        p.SetTotal(30);                        // now 2 pages -> clamp to 2
        Assert.Equal(2, p.CurrentPage);
        Assert.Equal(0, changed);              // silent: host re-slices right after SetTotal
    }

    [Fact]
    public void Reset_returns_to_page_1_silently()
    {
        var p = new PagerViewModel { PageSize = 25 };
        p.SetTotal(100);
        p.CurrentPage = 3;
        int changed = 0;
        p.Changed += () => changed++;
        p.Reset();
        Assert.Equal(1, p.CurrentPage);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void PageSize_change_resets_to_page_1_and_raises_Changed_once()
    {
        var p = new PagerViewModel { PageSize = 25 };
        p.SetTotal(100);
        p.CurrentPage = 3;
        int changed = 0;
        p.Changed += () => changed++;
        p.PageSize = 100;
        Assert.Equal(1, p.CurrentPage);
        Assert.Equal(1, changed);              // exactly one Changed for the whole size flip
    }

    [Fact]
    public void Slice_returns_the_current_page_window()
    {
        var p = new PagerViewModel { PageSize = 25 };
        var items = Enumerable.Range(0, 60).ToList();
        p.SetTotal(items.Count);
        Assert.Equal(Enumerable.Range(0, 25), p.Slice(items));
        p.CurrentPage = 3;
        Assert.Equal(Enumerable.Range(50, 10), p.Slice(items));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~PagerViewModelTests"`
Expected: FAIL — `PagerViewModel` does not exist (compile error).

- [ ] **Step 4: Implement `PagerViewModel`**

Create `src/LocalScribe.App/ViewModels/PagerViewModel.cs`:

```csharp
// src/LocalScribe.App/ViewModels/PagerViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace LocalScribe.App.ViewModels;

/// <summary>Classic pager state (design 2026-07-18 section 1), shared by the Sessions grid,
/// Search results, and the Matters tagged-sessions grid. WPF-free. Contract: SetTotal/Reset are
/// SILENT (they clamp/rewind without raising Changed - the host re-slices immediately after);
/// Changed fires only for user-driven page/size moves, and the host re-slices in the handler.</summary>
public sealed partial class PagerViewModel : ObservableObject
{
    /// <summary>Bound by PagerControl's page-size ComboBox. Default PageSize is 50.</summary>
    public static IReadOnlyList<int> PageSizeChoices { get; } = [25, 50, 100];

    [ObservableProperty] private int _currentPage = 1;   // 1-based
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _totalCount;

    private bool _silent;

    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public bool HasItems => TotalCount > 0;
    public string PageText => $"Page {CurrentPage} of {PageCount}";
    public bool CanGoPrev => CurrentPage > 1;
    public bool CanGoNext => CurrentPage < PageCount;

    public IRelayCommand PrevCommand { get; }
    public IRelayCommand NextCommand { get; }

    public event Action? Changed;

    public PagerViewModel()
    {
        PrevCommand = new RelayCommand(() => CurrentPage--, () => CanGoPrev);
        NextCommand = new RelayCommand(() => CurrentPage++, () => CanGoNext);
    }

    public void SetTotal(int count)
    {
        _silent = true;
        try
        {
            TotalCount = Math.Max(0, count);
            if (CurrentPage > PageCount) CurrentPage = PageCount;
        }
        finally { _silent = false; }
    }

    public void Reset()
    {
        _silent = true;
        try { CurrentPage = 1; }
        finally { _silent = false; }
    }

    public IReadOnlyList<T> Slice<T>(IReadOnlyList<T> items)
        => items.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();

    partial void OnCurrentPageChanged(int value)
    {
        NotifyDerived();
        if (!_silent) Changed?.Invoke();
    }

    partial void OnPageSizeChanged(int value)
    {
        // A size flip re-windows everything: rewind to page 1 (spec-accepted simplification)
        // inside the silent guard so the whole flip raises exactly one Changed.
        bool wasSilent = _silent;
        _silent = true;
        try { if (CurrentPage != 1) CurrentPage = 1; }
        finally { _silent = wasSilent; }
        NotifyDerived();
        if (!_silent) Changed?.Invoke();
    }

    partial void OnTotalCountChanged(int value) => NotifyDerived();

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        PrevCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~PagerViewModelTests"`
Expected: PASS (7 tests).

- [ ] **Step 6: Build clean and commit**

Run: `dotnet build LocalScribe.slnx` — Expected: 0 warnings.

```bash
git add src/LocalScribe.App/ViewModels/PagerViewModel.cs tests/LocalScribe.App.Tests/PagerViewModelTests.cs
git commit -m "feat(pager): shared PagerViewModel (page/size/slice, silent clamp contract)"
```

---

### Task 2: Sessions list paging (VM)

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs` (add tests; reuse the file's existing VM factory + session-writing helpers)

**Interfaces:**
- Consumes: `PagerViewModel` (Task 1).
- Produces: `public PagerViewModel Pager { get; }` on `SessionsPageViewModel` (bound by Task 3's XAML). Behavior contract: `Rows` holds only the current page of the filtered list; filter changes rewind to page 1; `UpsertRowAsync`/`RefreshRowAsync`/`LoadAsync` keep the current page (clamped); no collection Reset ever (scroll/selection survive in-place updates).

- [ ] **Step 1: Read the existing test file's helpers**

Open `tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs` and identify its existing VM factory helper (temp `StoragePaths` root + session-folder writers + `SessionsPageViewModel` construction with `dispatch: a => a()`), mirroring the `SearchPageViewModelTests` harness. Reuse those helpers for the new tests below.

- [ ] **Step 2: Write the failing tests**

Add to `tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs` (adapt the factory/writer helper names to the file's existing ones; the assertions are exact):

```csharp
[Fact]
public async Task Paging_slices_rows_newest_first_and_navigates()
{
    // Write 7 finalized sessions with distinct StartedAtUtc values via the file's session writer.
    // ids s1..s7, s7 newest.
    var vm = /* file's factory */;
    await vm.OnNavigatedToAsync();
    vm.Pager.PageSize = 3;                       // Changed -> re-slice

    Assert.Equal(3, vm.Rows.Count);
    Assert.Equal(7, vm.Pager.TotalCount);
    Assert.Equal("Page 1 of 3", vm.Pager.PageText);
    Assert.Equal("s7", vm.Rows[0].Id);           // newest-first preserved

    vm.Pager.NextCommand.Execute(null);
    Assert.Equal(3, vm.Rows.Count);
    Assert.Equal("s4", vm.Rows[0].Id);

    vm.Pager.NextCommand.Execute(null);
    Assert.Single(vm.Rows);                      // 7 = 3+3+1
    Assert.Equal("s1", vm.Rows[0].Id);
}

[Fact]
public async Task Filter_change_rewinds_to_page_1()
{
    // 7 sessions as above.
    var vm = /* file's factory */;
    await vm.OnNavigatedToAsync();
    vm.Pager.PageSize = 3;
    vm.Pager.NextCommand.Execute(null);
    Assert.Equal(2, vm.Pager.CurrentPage);

    vm.FilterText = "";                          // no-op text but still a filter change
    Assert.Equal(1, vm.Pager.CurrentPage);
}

[Fact]
public async Task Upsert_keeps_the_current_page()
{
    // 7 sessions; upsert an existing session on page 2.
    var vm = /* file's factory */;
    await vm.OnNavigatedToAsync();
    vm.Pager.PageSize = 3;
    vm.Pager.NextCommand.Execute(null);

    await vm.UpsertRowAsync("s4");               // present on page 2
    Assert.Equal(2, vm.Pager.CurrentPage);
    Assert.Equal(3, vm.Rows.Count);
    Assert.Equal("s4", vm.Rows[0].Id);
}

[Fact]
public async Task Selection_survives_in_page_replacement_and_clears_across_pages()
{
    var vm = /* file's factory */;
    await vm.OnNavigatedToAsync();
    vm.Pager.PageSize = 3;
    vm.SelectedRow = vm.Rows[1];                 // s6
    await vm.RefreshRowAsync("s6");              // replaces the row object in place
    Assert.Equal("s6", vm.SelectedRow?.Id);      // re-selected by id

    vm.Pager.NextCommand.Execute(null);          // s6 not on page 2
    Assert.Null(vm.SelectedRow);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SessionsPageViewModelTests"`
Expected: FAIL — `Pager` not defined on `SessionsPageViewModel`.

- [ ] **Step 4: Implement paging in `SessionsPageViewModel`**

All edits in `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs`:

1. Add the pager + filtered cache. Near the `Rows` declaration (line ~54):

```csharp
    /// <summary>Classic pager over the filtered list (design 2026-07-18 section 1). Rows holds
    /// only the current page; _filtered is the full post-filter list the pager windows over.</summary>
    public PagerViewModel Pager { get; } = new();
    private List<SessionRowViewModel> _filtered = [];
```

2. In the constructor, after the command assignments (after `ImportAudioCommand = new RelayCommand(ImportAudio);`):

```csharp
        Pager.Changed += ApplyPage;              // user page/size moves re-slice only
```

3. Replace `ApplyFilters` (lines ~244-256) with:

```csharp
    /// <summary>Recomputes the full filtered list from the cached _all, then re-slices the
    /// current page into Rows (3.2 in-memory filters + design 2026-07-18 pager). Callers that
    /// represent a FILTER CHANGE must Pager.Reset() first; refresh/upsert paths call this
    /// directly so the reader's current page survives (SetTotal clamps if it shrank).</summary>
    private void ApplyFilters()
    {
        var filtered = new List<SessionRowViewModel>();
        foreach (var row in _all)
        {
            // Content-snippet stamping rides the same pass (design 2026-07-13 2.2): matched rows
            // show one snippet line under the title; everything else shows none.
            row.ContentSnippet = _contentMatches.TryGetValue(row.Id, out string? snip) ? snip : null;
            if (PassesFilters(row)) filtered.Add(row);
        }
        _filtered = filtered;
        Pager.SetTotal(_filtered.Count);
        ApplyPage();
    }

    /// <summary>Slices the current page into Rows without a collection Reset (per-index
    /// replace/add/remove), so DataGrid scroll offset survives; selection is re-pointed by id
    /// and clears when the selected session is not on the current page.</summary>
    private void ApplyPage()
    {
        string? keepId = SelectedRow?.Id;
        var target = Pager.Slice(_filtered);
        for (int i = 0; i < target.Count; i++)
        {
            if (i >= Rows.Count) Rows.Add(target[i]);
            else if (!ReferenceEquals(Rows[i], target[i])) Rows[i] = target[i];
        }
        while (Rows.Count > target.Count) Rows.RemoveAt(Rows.Count - 1);
        SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId);
    }
```

4. Filter-change hooks rewind the pager. Replace the four partials (lines ~232-239):

```csharp
    partial void OnFilterTextChanged(string value)
    {
        Pager.Reset();                         // a filter change always reads from page 1
        ApplyFilters();                        // instant title/metadata pass - unchanged behavior
        ScheduleContentFilter(value);          // debounced index consult (design 2026-07-13 2.2)
    }
    partial void OnMatterFilterIdChanged(string? value) { Pager.Reset(); ApplyFilters(); }
    partial void OnMatterFilterSearchTextChanged(string value) => RebuildMatterOptions();
    partial void OnShowArchivedChanged(bool value) { Pager.Reset(); ApplyFilters(); }
```

5. The content-filter result is also a filter change. In `ScheduleContentFilter`, in the empty-filter branch, insert `Pager.Reset();` immediately before its `ApplyFilters();`. In `RunContentFilterAsync`'s `_dispatch` block, insert `Pager.Reset();` immediately before its `ApplyFilters();`.

6. `UpsertRowAsync` becomes recompute-based (the page-aware in-place math is now centralized in ApplyPage). Replace its `_dispatch` lambda body with:

```csharp
            _dispatch(() =>
            {
                if (item is null)
                {
                    // Vanished session: drop from the cache; ApplyFilters re-slices in place.
                    _all = _all.Where(r => r.Id != sessionId).ToList();
                    RebuildMatterOptions();
                    ApplyFilters();
                    return;
                }
                var newRow = new SessionRowViewModel(item, _time, MatterLookup,
                    isFinalizing: sessionId == _session.FinalizingSessionId,
                    isRetranscribing: sessionId == _retranscribingSessionId?.Invoke());
                newRow.ContentSnippet = _contentMatches.TryGetValue(sessionId, out string? snip) ? snip : null;
                var list = _all.ToList();
                int i = list.FindIndex(r => r.Id == sessionId);
                if (i >= 0) list[i] = newRow;
                else list.Insert(SortedInsertIndex(list, newRow), newRow);
                _all = list;
                RebuildMatterOptions();
                ApplyFilters();                 // keeps the current page (SetTotal clamps)
            });
```

7. Delete the now-dead helpers `UpsertIntoRows`, `RowsInsertPosition`, `IndexInRows`, and `RemoveRowInPlace` (ApplyPage's per-index sync subsumes their no-Reset guarantee). Keep `SortedInsertIndex` and `CompareNewestFirst`. Update the `UpsertRowAsync` doc comment: it still never Resets the collection (per-index sync), but slicing is pager-windowed now.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SessionsPageViewModelTests"`
Expected: PASS — the new tests AND every pre-existing test in the file (the no-Reset/selection/upsert contracts must not regress).

- [ ] **Step 6: Full test project + build, then commit**

Run: `dotnet test tests/LocalScribe.App.Tests` and `dotnet build LocalScribe.slnx`
Expected: all green, 0 warnings.

```bash
git add src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs
git commit -m "feat(sessions): page the sessions grid over the filtered list (50/page default)"
```

---

### Task 3: `PagerControl` + Sessions page footer (view)

**Files:**
- Create: `src/LocalScribe.App/Controls/PagerControl.xaml`
- Create: `src/LocalScribe.App/Controls/PagerControl.xaml.cs`
- Modify: `src/LocalScribe.App/Pages/SessionsPage.xaml`

**Interfaces:**
- Consumes: `PagerViewModel` as its `DataContext` (Tasks 1-2).
- Produces: `<controls:PagerControl DataContext="{Binding <SomePager>}" />` — reused verbatim by Tasks 4 and 9.

- [ ] **Step 1: Create the control**

`src/LocalScribe.App/Controls/PagerControl.xaml`:

```xml
<UserControl x:Class="LocalScribe.App.Controls.PagerControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:vm="clr-namespace:LocalScribe.App.ViewModels">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
    </UserControl.Resources>
    <!-- DataContext is a PagerViewModel. Hidden entirely while the host list is empty. -->
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right"
                Visibility="{Binding HasItems, Converter={StaticResource BoolToVis}}">
        <ui:Button Content="Previous" Appearance="Secondary" Command="{Binding PrevCommand}" Margin="0,0,8,0" />
        <TextBlock Text="{Binding PageText}" VerticalAlignment="Center" Margin="0,0,8,0" />
        <ui:Button Content="Next" Appearance="Secondary" Command="{Binding NextCommand}" Margin="0,0,12,0" />
        <ComboBox ItemsSource="{x:Static vm:PagerViewModel.PageSizeChoices}"
                  SelectedItem="{Binding PageSize}" MinWidth="70" VerticalAlignment="Center" />
        <TextBlock Text="/ page" VerticalAlignment="Center" Margin="6,0,0,0" Opacity="0.7" />
    </StackPanel>
</UserControl>
```

`src/LocalScribe.App/Controls/PagerControl.xaml.cs`:

```csharp
namespace LocalScribe.App.Controls;

/// <summary>Shared pager footer (design 2026-07-18 section 1). Pure view over a PagerViewModel
/// DataContext - no code-behind logic.</summary>
public partial class PagerControl
{
    public PagerControl() => InitializeComponent();
}
```

- [ ] **Step 2: Add the footer to SessionsPage**

In `src/LocalScribe.App/Pages/SessionsPage.xaml`:

1. Add the namespace on the `<Page>` root element: `xmlns:controls="clr-namespace:LocalScribe.App.Controls"`.
2. Directly AFTER the closing `</TextBlock>` of the `DockPanel.Dock="Bottom"` unreadable-count element (line ~95), i.e. between it and the results `<Grid>`, insert:

```xml
        <!-- Pager footer (design 2026-07-18 section 1): pages the filtered list; hidden when empty. -->
        <controls:PagerControl DockPanel.Dock="Bottom" Margin="0,8,0,0" DataContext="{Binding Pager}" />
```

(DockPanel gives first-declared children the outermost edge; declaring the pager AFTER the unreadable-count line docks it innermost — directly under the grid, with the unreadable-count note below it.)

- [ ] **Step 3: Build and visually verify**

Run: `dotnet build LocalScribe.slnx` — Expected: 0 warnings.
Launch the app (`dotnet run --project src/LocalScribe.App` or the built exe), open Sessions: with more than 50 sessions the footer shows "Page 1 of N"; Previous/Next page through; the last visible row is no longer clipped mid-row (each page fits its rows; the DataGrid shows at most PageSize rows). Close the app before the next build.

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.App/Controls/ src/LocalScribe.App/Pages/SessionsPage.xaml
git commit -m "feat(sessions): PagerControl footer under the sessions grid"
```

---

### Task 4: Search results paging (VM + view)

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SearchPageViewModel.cs`
- Modify: `src/LocalScribe.App/Pages/SearchPage.xaml`
- Test: `tests/LocalScribe.App.Tests/SearchPageViewModelTests.cs` (add tests; the file's `MakeVmAsync`/`WriteSessionAsync` helpers are shown at its top)

**Interfaces:**
- Consumes: `PagerViewModel` (Task 1), `PagerControl` (Task 3).
- Produces: `public PagerViewModel Pager { get; }` on `SearchPageViewModel`. Contract: `Results` holds one page of result CARDS (a session's snippets never split across pages); a new query/facet run rewinds to page 1.

- [ ] **Step 1: Write the failing tests**

Add to `tests/LocalScribe.App.Tests/SearchPageViewModelTests.cs`, using its existing `WriteSessionAsync` + `MakeVmAsync` helpers:

```csharp
    [Fact]
    public async Task Results_page_by_cards_and_new_query_rewinds()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        for (int i = 1; i <= 5; i++)
            await WriteSessionAsync($"s-{i}", $"Session {i}", t.AddDays(i), texts: new[] { "acme line" });
        var (vm, _, errors) = await MakeVmAsync();

        vm.QueryText = "acme";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal(5, vm.Pager.TotalCount);
        Assert.Equal(5, vm.Results.Count);           // default size 50: all on page 1

        vm.Pager.PageSize = 25;                      // no re-query needed for a size flip
        Assert.Equal(5, vm.Results.Count);

        // Simulate a small page: 2 cards per page.
        // PageSizeChoices governs the UI; the property accepts any positive size.
        vm.Pager.PageSize = 2;
        Assert.Equal(2, vm.Results.Count);
        Assert.Equal("Page 1 of 3", vm.Pager.PageText);
        Assert.Equal("s-5", vm.Results[0].SessionId); // ranking preserved across slicing

        vm.Pager.NextCommand.Execute(null);
        Assert.Equal(2, vm.Results.Count);
        Assert.Equal("s-3", vm.Results[0].SessionId);

        vm.QueryText = "acme line";                  // new query
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal(1, vm.Pager.CurrentPage);       // rewound
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task No_results_state_uses_the_full_match_count_not_the_page()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "One", t, texts: new[] { "hello" });
        var (vm, _, _) = await MakeVmAsync();

        vm.QueryText = "zzz-no-match";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.True(vm.ShowNoResults);
        Assert.Equal(0, vm.Pager.TotalCount);
        Assert.Empty(vm.Results);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SearchPageViewModelTests"`
Expected: FAIL — `Pager` not defined.

- [ ] **Step 3: Implement paging in `SearchPageViewModel`**

1. Add near `Results` (line ~47):

```csharp
    /// <summary>Pager over result CARDS (design 2026-07-18 section 1): one card = one session
    /// with its snippets, never split across pages. The engine still returns the full ranked
    /// list (ranking needs the whole set); Results holds only the current page.</summary>
    public PagerViewModel Pager { get; } = new();
    private List<SearchResultCard> _allCards = [];
```

2. In the constructor, after `OpenSnippetCommand = ...` assignment block, add:

```csharp
        Pager.Changed += ApplyPage;
```

3. In `RunSearchAsync`, replace the `_dispatch(...)` block with:

```csharp
            _dispatch(() =>
            {
                if (ct.IsCancellationRequested) return;   // superseded by a newer keystroke/facet
                _allCards = results.Select(ToCard).ToList();
                Pager.Reset();                            // a new query always reads from page 1
                Pager.SetTotal(_allCards.Count);
                ApplyPage();
                ShowNoQuery = !hasQuery;
                ShowNoResults = hasQuery && _allCards.Count == 0 && !IsIndexing;
            });
```

4. Add the slicer method:

```csharp
    private void ApplyPage()
    {
        Results.Clear();
        foreach (var card in Pager.Slice(_allCards)) Results.Add(card);
    }
```

(Cards are immutable records and page flips should scroll to top anyway, so a Clear-and-refill is correct here — unlike the Sessions grid there is no in-place-update contract.)

- [ ] **Step 4: Add the footer to SearchPage**

In `src/LocalScribe.App/Pages/SearchPage.xaml`: add `xmlns:controls="clr-namespace:LocalScribe.App.Controls"` to the `<Page>` root, then directly BEFORE the results `<Grid>` (line ~33) insert:

```xml
            <!-- Pager footer (design 2026-07-18 section 1). -->
            <controls:PagerControl DockPanel.Dock="Bottom" Margin="0,8,0,0" DataContext="{Binding Pager}" />
```

- [ ] **Step 5: Run tests, build, commit**

Run: `dotnet test tests/LocalScribe.App.Tests` then `dotnet build LocalScribe.slnx`
Expected: all green, 0 warnings.

```bash
git add src/LocalScribe.App/ViewModels/SearchPageViewModel.cs src/LocalScribe.App/Pages/SearchPage.xaml tests/LocalScribe.App.Tests/SearchPageViewModelTests.cs
git commit -m "feat(search): page result cards with the shared pager"
```

---

### Task 5: Search "All matters"/"All apps" display-sentinel fix

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SearchPageViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/SearchPageViewModelTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `SearchPageViewModel.MatterFilterId`/`AppFilterId` now default to `""` (the "All" sentinel — empty string, NOT null); `""` maps to a null facet in the executed `SearchQuery`. Task 6's escalation wiring sets `""` to reset these facets.

**Background (verified during design):** both facet combos bind `SelectedValuePath="Id"` + `SelectedValue`, and the "All" options carry `Id = null`. WPF cannot select an item by a null `SelectedValue`, so the combo displays blank (and, because the page VM is an app-lifetime singleton, any previously clicked value like "Zoom" survives navigation). The fix: a non-null `""` sentinel that WPF can match, mapped back to `null` at query time.

- [ ] **Step 1: Write the failing tests**

Add to `tests/LocalScribe.App.Tests/SearchPageViewModelTests.cs`:

```csharp
    [Fact]
    public async Task Facets_default_to_all_and_the_all_sentinel_is_selectable()
    {
        var (vm, _, _) = await MakeVmAsync();
        await vm.OnNavigatedToAsync();

        Assert.Equal("", vm.MatterFilterId);            // "" = the All sentinel WPF can select
        Assert.Equal("", vm.AppFilterId);
        Assert.Null(vm.FromDate);
        Assert.Null(vm.ToDate);
        Assert.Equal("", vm.AppOptions[0].Id);
        Assert.Equal("All apps", vm.AppOptions[0].Label);
        Assert.Equal("", vm.MatterOptions[0].Id);
        Assert.Equal("All matters", vm.MatterOptions[0].Label);
    }

    [Fact]
    public async Task Empty_sentinel_queries_all_apps_and_a_real_app_filters()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-webex", "W", t, app: AppKind.Webex, texts: new[] { "acme" });
        await WriteSessionAsync("s-zoom", "Z", t.AddDays(1), app: AppKind.Zoom, texts: new[] { "acme" });
        var (vm, _, _) = await MakeVmAsync();

        vm.QueryText = "acme";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal(2, vm.Results.Count);              // "" facet = no app filter

        vm.AppFilterId = "Zoom";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Single(vm.Results);
        Assert.Equal("s-zoom", vm.Results[0].SessionId);

        vm.AppFilterId = "";                            // back to All
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal(2, vm.Results.Count);
    }

    [Fact]
    public async Task Navigation_refresh_keeps_the_all_sentinel_selected()
    {
        var (vm, _, _) = await MakeVmAsync();
        await vm.OnNavigatedToAsync();
        await vm.OnNavigatedToAsync();                  // options rebuilt; "" must survive
        Assert.Equal("", vm.MatterFilterId);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SearchPageViewModelTests"`
Expected: FAIL — defaults are null, options carry null ids.

- [ ] **Step 3: Implement the sentinel**

In `src/LocalScribe.App/ViewModels/SearchPageViewModel.cs`:

1. `AppOptions` (line ~52): change the first entry to `new MatterFilterOption("", "All apps")`. Update the doc comment: `/// <summary>App facet: "" = all (the WPF-selectable sentinel; null SelectedValue cannot select a ComboBox item). ...</summary>`
2. Defaults (lines ~58-59):

```csharp
    [ObservableProperty] private string? _matterFilterId = "";
    [ObservableProperty] private string? _appFilterId = "";
```

3. `OnNavigatedToAsync` (line ~116): `MatterOptions.Add(new MatterFilterOption("", "All matters"));` and the stale-selection fallback becomes:

```csharp
                if (!string.IsNullOrEmpty(current) && MatterOptions.All(o => o.Id != current))
                    MatterFilterId = "";                // stale selection -> All
                else if (MatterFilterId != current)
                    MatterFilterId = current;           // re-assert: a bound ComboBox can null on Clear()
```

4. In `RunSearchAsync` (line ~142), map the sentinel to a null facet:

```csharp
            var query = new SearchQuery(text, Facet(MatterFilterId), FacetFromUtc(), FacetToUtc(),
                Facet(AppFilterId));
```

and add the helper:

```csharp
    /// <summary>"" (the combo's "All" sentinel) and null both mean "no facet" to the engine.</summary>
    private static string? Facet(string? id) => string.IsNullOrEmpty(id) ? null : id;
```

No XAML change is needed — `SelectedValue=""` matches the `Id=""` option.

- [ ] **Step 4: Run tests, build, commit**

Run: `dotnet test tests/LocalScribe.App.Tests` then `dotnet build LocalScribe.slnx`
Expected: all green (including all pre-existing search tests), 0 warnings.

```bash
git add src/LocalScribe.App/ViewModels/SearchPageViewModel.cs tests/LocalScribe.App.Tests/SearchPageViewModelTests.cs
git commit -m "fix(search): non-null All sentinel so the facet combos display their defaults"
```

---

### Task 6: Read view — visible Find button + "Search all sessions" escalation

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml`
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml.cs`
- Modify: `src/LocalScribe.App/MainWindow.xaml.cs`
- Modify: `src/LocalScribe.App/TrayIconHost.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs`
- Test: `tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs` (add one test; reuse the file's existing VM factory)

**Interfaces:**
- Consumes: `SearchPageViewModel` facets (Task 5's `""` sentinel), `TrayIconHost.OpenMainWindow()` (existing).
- Produces:
  - `ReadViewViewModel`: `event Action<string>? SearchAllSessionsRequested;` + `void RequestSearchAllSessions()` (raises with the current `FindText`).
  - `MainWindow`: `void NavigateToSection(Type pageType)` — navigates now if loaded, else defers past the Loaded-time SessionsPage navigation.
  - `TrayIconHost`: `void OpenMainWindowAt(Type pageType)`.

- [ ] **Step 1: Write the failing test**

Add to `tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs` (reuse the file's existing VM factory helper):

```csharp
    [Fact]
    public async Task Search_all_sessions_carries_the_current_find_term()
    {
        var vm = /* file's factory; load any session */;
        vm.OpenFind("privilege");
        string? requested = null;
        vm.SearchAllSessionsRequested += term => requested = term;

        vm.RequestSearchAllSessions();
        Assert.Equal("privilege", requested);

        vm.FindText = "";                              // empty term is allowed (opens Search blank)
        vm.RequestSearchAllSessions();
        Assert.Equal("", requested);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"`
Expected: FAIL — event/method not defined.

- [ ] **Step 3: Implement the VM event**

In `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`, in the find-bar region (near `OpenFind`, line ~201):

```csharp
    /// <summary>Find-bar escalation (design 2026-07-18 section 3): the window layer navigates the
    /// main window to the Search page pre-filled with this term, facets reset to their defaults
    /// (all matters / all apps / all dates - never inherited from this session).</summary>
    public event Action<string>? SearchAllSessionsRequested;

    public void RequestSearchAllSessions() => SearchAllSessionsRequested?.Invoke(FindText);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"`
Expected: PASS.

- [ ] **Step 5: Window buttons**

In `src/LocalScribe.App/ReadViewWindow.xaml.cs`:

1. Add a command property next to the find-bar commands (line ~70):

```csharp
    public IRelayCommand SearchAllSessionsCommand { get; }
```

2. In the constructor, next to `CloseFindCommand = ...`:

```csharp
        SearchAllSessionsCommand = new RelayCommand(vm.RequestSearchAllSessions);
```

In `src/LocalScribe.App/ReadViewWindow.xaml`:

3. Header Find button — insert directly BEFORE the `<Button Content="Edit" ...>` element (line ~80):

```xml
                <Button Content="Find" ToolTip="Find in transcript (Ctrl+F)" Margin="0,0,8,4"
                        Command="{Binding OpenFindCommand, ElementName=Self}" />
```

4. Find-bar escalation button — insert directly AFTER the `<Button DockPanel.Dock="Right" Content="Previous" ...>` element (line ~149-151), so it docks as the leftmost of the right-hand cluster:

```xml
                <Button DockPanel.Dock="Right" Content="Search all sessions" Margin="8,0,0,0"
                        ToolTip="Search every session for this text on the Search page"
                        Command="{Binding SearchAllSessionsCommand, ElementName=Self}" />
```

- [ ] **Step 6: MainWindow deferred navigation**

In `src/LocalScribe.App/MainWindow.xaml.cs`:

1. Add a field next to `_hwndReady`:

```csharp
    private Type? _pendingNavigate;
```

2. Replace the ctor's `Loaded += (_, _) => RootNav.Navigate(typeof(Pages.SessionsPage));` with:

```csharp
        // A NavigateToSection issued before Loaded (fresh window from the tray factory) must not
        // be clobbered by this default landing - it is stashed and wins here.
        Loaded += (_, _) =>
        {
            RootNav.Navigate(_pendingNavigate ?? typeof(Pages.SessionsPage));
            _pendingNavigate = null;
        };
```

3. Add the public method (after the ctor):

```csharp
    /// <summary>Programmatic section navigation (read-view "Search all sessions" hand-off,
    /// design 2026-07-18 section 3). Navigating by page type moves the nav-rail selection too
    /// (Wpf.Ui matches TargetPageType), same as the Loaded-time landing.</summary>
    public void NavigateToSection(Type pageType)
    {
        if (IsLoaded) RootNav.Navigate(pageType);
        else _pendingNavigate = pageType;
    }
```

- [ ] **Step 7: TrayIconHost + App wiring**

In `src/LocalScribe.App/TrayIconHost.cs`, after `OpenMainWindow()` (line ~130):

```csharp
    /// <summary>Open/activate the main window, then land it on the given page (read-view
    /// "Search all sessions" hand-off).</summary>
    public void OpenMainWindowAt(Type pageType)
    {
        OpenMainWindow();
        _main!.NavigateToSection(pageType);
    }
```

In `src/LocalScribe.App/App.xaml.cs`, inside the `openReadView` factory (after `var readVm = new ViewModels.ReadViewViewModel(...)`, line ~329), add:

```csharp
            // Find-bar escalation (design 2026-07-18 section 3): pre-fill the Search page with
            // the current find term and RESET the facets to their defaults ("" = All sentinel,
            // Task 5) - "Search all sessions" means exactly that, never this session's facets.
            // _tray is assigned later in OnStartup but strictly before any read view can open.
            readVm.SearchAllSessionsRequested += term => dispatch(() =>
            {
                searchVm.MatterFilterId = "";
                searchVm.AppFilterId = "";
                searchVm.FromDate = null;
                searchVm.ToDate = null;
                searchVm.QueryText = term;
                _tray?.OpenMainWindowAt(typeof(Pages.SearchPage));
            });
```

(Verify the `_tray` field is nullable-typed; if it is `TrayIconHost` non-null, use `_tray.OpenMainWindowAt(...)` — the null-conditional is only defensive.)

- [ ] **Step 8: Build, run, verify both directions, commit**

Run: `dotnet build LocalScribe.slnx` (0 warnings), `dotnet test tests/LocalScribe.App.Tests` (green).
Manual: open a transcript → click Find (bar opens, box focused) → type a term → "Search all sessions" → main window comes up on Search with the term filled and combos showing "All matters"/"All apps". Also verify with the main window CLOSED first (tray factory path): the fresh window must land on Search, not Sessions. Close the app.

```bash
git add src/LocalScribe.App/ViewModels/ReadViewViewModel.cs src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/ReadViewWindow.xaml.cs src/LocalScribe.App/MainWindow.xaml.cs src/LocalScribe.App/TrayIconHost.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs
git commit -m "feat(readview): visible Find button + Search-all-sessions escalation to the Search page"
```

---

### Task 7: Matters VM — tagged-sessions list restructure (pager, filter, open-transcript)

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/MattersPageViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs` (add tests; reuse the file's existing factory/session-writing helpers)

**Interfaces:**
- Consumes: `PagerViewModel` (Task 1).
- Produces (bound/called by Task 9's XAML + code-behind):
  - `TaggedSessionItem` record widened to `(string SessionId, string Title, string DateDisplay, string DurationDisplay, bool IsPendingRecovery)`
  - `public PagerViewModel TaggedPager { get; }` — pages `TaggedSessions`
  - `[ObservableProperty] string _taggedFilterText` — title-substring filter, resets the pager to page 1
  - `[ObservableProperty] TaggedSessionItem? _selectedTagged` + `bool HasTaggedSelection`
  - `event Action<string>? OpenReadViewRequested` + `void OpenTranscript(string sessionId)` (refuses pending-recovery rows with an Info)
  - `[ObservableProperty] string _headerSummary` + `[ObservableProperty] string _headerCreatedDisplay` (compact header, Task 9)
  - `JumpToSession` (existing) stays the Details action.

- [ ] **Step 1: Write the failing tests**

Add to `tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs`, reusing its existing matter/session writers and VM factory (read the file first):

```csharp
    [Fact]
    public async Task Tagged_sessions_page_newest_first_and_filter_rewinds()
    {
        // Create a matter; write 5 finalized sessions tagged to it with ascending StartedAtUtc
        // (ids t1..t5, t5 newest, titles "Alpha 1".."Alpha 5"), via the file's writers.
        var vm = /* file's factory */;
        await vm.RefreshAsync();
        await vm.SelectAsync(matterId);

        vm.TaggedPager.PageSize = 2;
        Assert.Equal(5, vm.TaggedPager.TotalCount);
        Assert.Equal(2, vm.TaggedSessions.Count);
        Assert.Equal("t5", vm.TaggedSessions[0].SessionId);   // newest first

        vm.TaggedPager.NextCommand.Execute(null);
        Assert.Equal("t3", vm.TaggedSessions[0].SessionId);

        vm.TaggedFilterText = "Alpha 1";                       // filter change
        Assert.Equal(1, vm.TaggedPager.CurrentPage);           // rewound
        Assert.Single(vm.TaggedSessions);
        Assert.Equal("t1", vm.TaggedSessions[0].SessionId);
    }

    [Fact]
    public async Task Open_transcript_raises_for_finalized_and_refuses_pending_recovery()
    {
        // One finalized session (t-done) and one with EndedAtUtc == null (t-pending), both tagged.
        var vm = /* file's factory */;
        await vm.RefreshAsync();
        await vm.SelectAsync(matterId);

        string? opened = null;
        vm.OpenReadViewRequested += id => opened = id;

        vm.OpenTranscript("t-done");
        Assert.Equal("t-done", opened);

        opened = null;
        vm.OpenTranscript("t-pending");
        Assert.Null(opened);                                   // refused with an Info, no event
    }

    [Fact]
    public async Task Tagged_items_carry_duration_and_pending_flag()
    {
        // t-done written with DurationMs = 90_000 -> "01:30"; t-pending has EndedAtUtc null.
        var vm = /* file's factory */;
        await vm.RefreshAsync();
        await vm.SelectAsync(matterId);

        var done = vm.TaggedSessions.First(t => t.SessionId == "t-done");
        Assert.Equal("01:30", done.DurationDisplay);
        Assert.False(done.IsPendingRecovery);
        var pending = vm.TaggedSessions.First(t => t.SessionId == "t-pending");
        Assert.Equal("", pending.DurationDisplay);
        Assert.True(pending.IsPendingRecovery);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"`
Expected: FAIL (record shape + members missing).

- [ ] **Step 3: Implement**

In `src/LocalScribe.App/ViewModels/MattersPageViewModel.cs`:

1. Widen the record (line ~13):

```csharp
/// <summary>One row of the selected matter's tagged-sessions grid (design 2026-07-18 section 4).
/// DurationDisplay mirrors SessionRowViewModel's format (h:mm:ss over an hour, else mm:ss;
/// "" while pending recovery). IsPendingRecovery = EndedAtUtc is null (row opens Details but
/// not the transcript).</summary>
public sealed record TaggedSessionItem(string SessionId, string Title, string DateDisplay,
    string DurationDisplay, bool IsPendingRecovery);
```

2. Add state next to `TaggedSessions` (line ~33):

```csharp
    /// <summary>Pager + title filter over the tagged-sessions grid (design 2026-07-18 section 4).</summary>
    public PagerViewModel TaggedPager { get; } = new();
    private List<TaggedSessionItem> _taggedAll = [];
    private List<TaggedSessionItem> _taggedFiltered = [];
```

3. Add observable properties next to the existing ones:

```csharp
    [ObservableProperty] private string _taggedFilterText = "";
    [ObservableProperty] private TaggedSessionItem? _selectedTagged;
    [ObservableProperty] private string _headerSummary = "";
    [ObservableProperty] private string _headerCreatedDisplay = "";

    public bool HasTaggedSelection => SelectedTagged is not null;
    partial void OnSelectedTaggedChanged(TaggedSessionItem? value)
        => OnPropertyChanged(nameof(HasTaggedSelection));
    partial void OnTaggedFilterTextChanged(string value)
    {
        TaggedPager.Reset();
        ApplyTaggedFilter();
    }
```

4. In the constructor, after `RepairIndexCommand = ...`:

```csharp
        TaggedPager.Changed += ApplyTaggedPage;
```

5. In `SelectAsync`'s `_dispatch` block, replace the `TaggedSessions.Clear(); foreach (...) TaggedSessions.Add(...)` loop with:

```csharp
                _taggedAll = sessions.Sessions
                    .Where(s => s.Meta.MatterIds.Contains(matterId))
                    .OrderByDescending(s => s.Session.StartedAtUtc)
                    .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new TaggedSessionItem(s.Id, s.Meta.Title, DateDisplay(s.Session),
                        DurationDisplay(s.Session), s.Session.EndedAtUtc is null))
                    .ToList();
                TaggedPager.Reset();
                ApplyTaggedFilter();
                HeaderSummary = loaded.Roster.FirstOrDefault(m =>
                        string.Equals(m.Role, "Client", StringComparison.OrdinalIgnoreCase)) is { } client
                    ? "Client: " + client.Name
                    : loaded.Roster.Count + " member(s)";
                HeaderCreatedDisplay = "created "
                    + loaded.DateCreatedUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
```

6. Add the filter/page/duration helpers (near `DateDisplay`, line ~212):

```csharp
    private void ApplyTaggedFilter()
    {
        string q = TaggedFilterText.Trim();
        _taggedFiltered = q.Length == 0
            ? _taggedAll.ToList()
            : _taggedAll.Where(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        TaggedPager.SetTotal(_taggedFiltered.Count);
        ApplyTaggedPage();
    }

    private void ApplyTaggedPage() => _dispatch(() =>
    {
        string? keepId = SelectedTagged?.SessionId;
        TaggedSessions.Clear();
        foreach (var t in TaggedPager.Slice(_taggedFiltered)) TaggedSessions.Add(t);
        SelectedTagged = TaggedSessions.FirstOrDefault(t => t.SessionId == keepId);
    });

    /// <summary>SessionRowViewModel's exact duration format, duplicated here because that logic
    /// is embedded in its constructor: "" while pending recovery, h:mm:ss over an hour, else mm:ss.</summary>
    private static string DurationDisplay(SessionRecord session)
    {
        if (session.EndedAtUtc is null) return "";
        var span = TimeSpan.FromMilliseconds(session.DurationMs);
        return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    }
```

7. Add the open-transcript path (near `JumpToSession`, line ~150), and update `JumpToSession`'s doc comment to say it is now the SECONDARY "Details" action:

```csharp
    /// <summary>Primary tagged-session action (design 2026-07-18 section 4): opens the transcript
    /// read view. Reverses the Stage 5.2 details-only decision. Pending-recovery rows are refused
    /// with an actionable Info (same rule as the Sessions page's OpenReadView guard).</summary>
    public event Action<string>? OpenReadViewRequested;

    public void OpenTranscript(string sessionId)
    {
        if (_taggedAll.FirstOrDefault(t => t.SessionId == sessionId) is { IsPendingRecovery: true })
        {
            _reporter.Info("This session is still being recovered. Try again once recovery completes.");
            return;
        }
        OpenReadViewRequested?.Invoke(sessionId);
    }
```

8. Add `using LocalScribe.Core.Model;` is already present; ensure `CultureInfo` is imported (it is, line 3).

- [ ] **Step 4: Run tests, build, commit**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` then the full project + `dotnet build LocalScribe.slnx`.
Expected: all green, 0 warnings. (The existing `UntagSessionAsync` tests must still pass — untag re-runs `SelectAsync`, which now rebuilds `_taggedAll`.)

```bash
git add src/LocalScribe.App/ViewModels/MattersPageViewModel.cs tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs
git commit -m "feat(matters): paged+filtered tagged-sessions list, open-transcript action, header summaries"
```

---

### Task 8: Add-sessions picker VM + tagging batch

**Files:**
- Create: `src/LocalScribe.App/ViewModels/AddSessionsPickerViewModel.cs`
- Modify: `src/LocalScribe.App/ViewModels/MattersPageViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/AddSessionsPickerViewModelTests.cs` (new)
- Test: `tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs` (add tests)

**Interfaces:**
- Consumes: `MaintenanceService.ListSessionsAsync` / `LoadSessionItemAsync` / `SaveMetaAsync(sessionId, updatedMeta, previousMatterIds, ct)` (existing — the exact path `UntagSessionAsync` and Session Details use).
- Produces (used by Task 9's dialog + code-behind):
  - `PickerSessionItem` : observable `{ string Id; string Title; string DateDisplay; string Source; bool IsSelected }`
  - `AddSessionsPickerViewModel(IReadOnlyList<PickerSessionItem> candidates)` with `ObservableCollection<PickerSessionItem> Visible`, `string FilterText`, `IReadOnlyList<string> SelectedIds`
  - `MattersPageViewModel.ListUntaggedSessionsAsync()` → candidates (unarchived, not tagged to the selected matter, newest-first)
  - `MattersPageViewModel.AddSessionsAsync(IReadOnlyList<string> sessionIds)` + `event Action<string>? SessionTagged`

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.App.Tests/AddSessionsPickerViewModelTests.cs`:

```csharp
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class AddSessionsPickerViewModelTests
{
    private static AddSessionsPickerViewModel Make() => new(
    [
        new PickerSessionItem("a", "Webex call with Smith", "2026-07-01 10:00", "Webex"),
        new PickerSessionItem("b", "Manual note", "2026-07-02 11:00", "Manual"),
        new PickerSessionItem("c", "Webex follow-up", "2026-07-03 12:00", "Webex"),
    ]);

    [Fact]
    public void All_candidates_visible_initially_and_none_selected()
    {
        var vm = Make();
        Assert.Equal(3, vm.Visible.Count);
        Assert.Empty(vm.SelectedIds);
    }

    [Fact]
    public void Filter_narrows_by_title_case_insensitive()
    {
        var vm = Make();
        vm.FilterText = "webex";
        Assert.Equal(new[] { "a", "c" }, vm.Visible.Select(i => i.Id));
        vm.FilterText = "";
        Assert.Equal(3, vm.Visible.Count);
    }

    [Fact]
    public void Selection_survives_filtering()
    {
        var vm = Make();
        vm.Visible.First(i => i.Id == "b").IsSelected = true;
        vm.FilterText = "webex";                       // b hidden but stays selected
        vm.Visible.First(i => i.Id == "a").IsSelected = true;
        Assert.Equal(new[] { "a", "b" }, vm.SelectedIds.OrderBy(x => x));
    }
}
```

Add to `tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs` (file's helpers again):

```csharp
    [Fact]
    public async Task ListUntagged_excludes_tagged_and_archived_and_orders_newest_first()
    {
        // matter M; sessions: u1 (untagged, oldest), u2 (untagged, newest),
        // tagged1 (tagged to M), arch1 (untagged but meta.Archived = true).
        var vm = /* file's factory */;
        await vm.RefreshAsync();
        await vm.SelectAsync(matterId);

        var candidates = await vm.ListUntaggedSessionsAsync();
        Assert.Equal(new[] { "u2", "u1" }, candidates.Select(c => c.Id));
    }

    [Fact]
    public async Task AddSessions_tags_on_disk_raises_events_and_refreshes_the_list()
    {
        // matter M; untagged sessions u1, u2; nonexistent id "ghost".
        var vm = /* file's factory */;
        await vm.RefreshAsync();
        await vm.SelectAsync(matterId);
        var tagged = new List<string>();
        vm.SessionTagged += tagged.Add;

        await vm.AddSessionsAsync(["u1", "ghost", "u2"]);

        Assert.Equal(new[] { "u1", "u2" }, tagged);              // ghost skipped, no abort
        Assert.Contains(vm.TaggedSessions, t => t.SessionId == "u1");
        Assert.Contains(vm.TaggedSessions, t => t.SessionId == "u2");
        // Disk truth: reload u1's meta via the file's store helpers and assert MatterIds contains M.
    }

    [Fact]
    public async Task AddSessions_already_tagged_is_a_silent_no_op()
    {
        // u1 already tagged to M by a racing save.
        var vm = /* file's factory */;
        await vm.RefreshAsync();
        await vm.SelectAsync(matterId);
        int events = 0;
        vm.SessionTagged += _ => events++;

        await vm.AddSessionsAsync(["u1"]);
        Assert.Equal(0, events);                                 // no delta -> no event, no write
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~AddSessionsPicker|FullyQualifiedName~MattersPageViewModelTests"`
Expected: FAIL (types/members missing).

- [ ] **Step 3: Implement the picker VM**

Create `src/LocalScribe.App/ViewModels/AddSessionsPickerViewModel.cs`:

```csharp
// src/LocalScribe.App/ViewModels/AddSessionsPickerViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
namespace LocalScribe.App.ViewModels;

/// <summary>One selectable row in the Matters-page "Add sessions..." picker
/// (design 2026-07-18 section 4).</summary>
public sealed partial class PickerSessionItem(string id, string title, string dateDisplay, string source)
    : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string DateDisplay { get; } = dateDisplay;
    public string Source { get; } = source;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Dialog VM for tagging existing sessions to the selected matter: candidates are
/// pre-filtered by the caller (untagged, unarchived); the filter here narrows by title only.
/// Selection is held on the items themselves, so checked rows survive filtering. WPF-free.</summary>
public sealed partial class AddSessionsPickerViewModel : ObservableObject
{
    private readonly IReadOnlyList<PickerSessionItem> _all;

    public ObservableCollection<PickerSessionItem> Visible { get; } = [];
    [ObservableProperty] private string _filterText = "";

    public AddSessionsPickerViewModel(IReadOnlyList<PickerSessionItem> candidates)
    {
        _all = candidates;
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = FilterText.Trim();
        Visible.Clear();
        foreach (var item in _all)
            if (q.Length == 0 || item.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                Visible.Add(item);
    }

    /// <summary>Checked ids across ALL candidates, including ones the filter currently hides.</summary>
    public IReadOnlyList<string> SelectedIds
        => _all.Where(i => i.IsSelected).Select(i => i.Id).ToList();
}
```

- [ ] **Step 4: Implement the Matters VM batch**

In `src/LocalScribe.App/ViewModels/MattersPageViewModel.cs`, after `UntagSessionAsync`:

```csharp
    /// <summary>Raised after AddSessionsAsync actually tagged a session on disk (grid coherence,
    /// mirror of SessionUntagged): App.xaml.cs routes this to SessionsPageViewModel.RefreshRowAsync.</summary>
    public event Action<string>? SessionTagged;

    /// <summary>Candidates for the Add-sessions picker (design 2026-07-18 section 4): unarchived
    /// sessions not already tagged with the selected matter, newest-first. Pending-recovery rows
    /// ARE included - tagging writes meta.json only, which is legal for them (same rule as the
    /// Session Details picker).</summary>
    public async Task<IReadOnlyList<PickerSessionItem>> ListUntaggedSessionsAsync()
    {
        if (SelectedMatterId is not string matterId) return [];
        var sessions = await _maintenance.ListSessionsAsync(CancellationToken.None);
        return sessions.Sessions
            .Where(s => !s.Meta.Archived && !s.Meta.MatterIds.Contains(matterId, StringComparer.Ordinal))
            .OrderByDescending(s => s.Session.StartedAtUtc)
            .ThenByDescending(s => s.Id, StringComparer.Ordinal)
            .Select(s => new PickerSessionItem(s.Id, s.Meta.Title, DateDisplay(s.Session),
                s.Session.App.ToString()))
            .ToList();
    }

    /// <summary>Tags each selected session to the selected matter through the SAME
    /// SaveMetaAsync tag-delta path Session Details and UntagSessionAsync use, so index and
    /// search semantics stay byte-identical. Loads each session FRESH from disk (the stale
    /// picker snapshot never feeds the delta); already-tagged and vanished sessions are silent
    /// no-ops; a per-session failure is reported and does NOT abort the rest. Organizational
    /// only: meta.json is the ONLY file written (evidentiary firewall).</summary>
    public async Task AddSessionsAsync(IReadOnlyList<string> sessionIds)
    {
        if (SelectedMatterId is not string matterId) return;
        foreach (string sessionId in sessionIds)
        {
            try
            {
                var item = await _maintenance.LoadSessionItemAsync(sessionId, CancellationToken.None);
                if (item is null) continue;                              // deleted underneath us
                var previous = item.Meta.MatterIds;
                if (previous.Contains(matterId, StringComparer.Ordinal)) continue;   // raced: already tagged
                var updated = item.Meta with { MatterIds = previous.Append(matterId).ToList() };
                await _maintenance.SaveMetaAsync(sessionId, updated, previous, CancellationToken.None);
                SessionTagged?.Invoke(sessionId);
            }
            catch (Exception ex) { _reporter.Report("Tag session " + sessionId, ex); }
        }
        await RefreshAsync();                                            // matter counts changed
        await SelectAsync(matterId);                                     // rebuild the tagged list
    }
```

Before finishing: open `MaintenanceService.cs` and confirm whether the Session-Details/`UntagSessionAsync` save path triggers any search-index update internally. Mirror exactly — do NOT add a separate `ReindexSessionAsync` call here unless the details-save path has one at the wiring layer (parity is the spec's requirement).

- [ ] **Step 5: Run tests, build, commit**

Run: `dotnet test tests/LocalScribe.App.Tests` then `dotnet build LocalScribe.slnx`
Expected: all green, 0 warnings.

```bash
git add src/LocalScribe.App/ViewModels/AddSessionsPickerViewModel.cs src/LocalScribe.App/ViewModels/MattersPageViewModel.cs tests/LocalScribe.App.Tests/AddSessionsPickerViewModelTests.cs tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs
git commit -m "feat(matters): Add-sessions picker VM + batch tagging via the SaveMetaAsync delta path"
```

---

### Task 9: Matters page overhaul (header + tabs + sessions grid + dialog + wiring)

**Files:**
- Modify: `src/LocalScribe.App/Pages/MattersPage.xaml` (right pane rebuilt)
- Modify: `src/LocalScribe.App/Pages/MattersPage.xaml.cs`
- Create: `src/LocalScribe.App/AddSessionsDialog.xaml`
- Create: `src/LocalScribe.App/AddSessionsDialog.xaml.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs` (lines ~447-455)

**Interfaces:**
- Consumes: everything Tasks 7-8 produced; `PagerControl` (Task 3); existing `openReadView` / `openSessionDetails` factories in `App.xaml.cs`.
- Produces: the shipped Matters UI. No new VM surface.

- [ ] **Step 1: Rebuild the right pane in `MattersPage.xaml`**

Add namespaces to the `<Page>` root: `xmlns:controls="clr-namespace:LocalScribe.App.Controls"`.

Replace the entire right-pane `<ScrollViewer Grid.Column="1" ...>...</ScrollViewer>` (lines 54-202) with the structure below. The section contents marked "moved as-is" are the EXACT existing card inner `<StackPanel>` contents — cut and paste them, do not retype them:

```xml
        <Grid Grid.Column="1" Visibility="{Binding HasSelection, Converter={StaticResource BoolToVis}}">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>

            <!-- Compact display-only header (design 2026-07-18 section 4): name, reference,
                 client/member chip, created date. Editing lives in the Details tab. -->
            <WrapPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,8">
                <TextBlock Text="{Binding EditName}" FontSize="16" FontWeight="SemiBold"
                           Margin="0,0,12,0" VerticalAlignment="Center" />
                <TextBlock Text="{Binding EditReference}" Opacity="0.7" Margin="0,0,12,0"
                           VerticalAlignment="Center" />
                <Border Background="{DynamicResource ControlFillColorSecondaryBrush}"
                        CornerRadius="10" Padding="8,2" Margin="0,0,12,0" VerticalAlignment="Center">
                    <TextBlock Text="{Binding HeaderSummary}" FontSize="12" />
                </Border>
                <TextBlock Text="{Binding HeaderCreatedDisplay}" Opacity="0.7" VerticalAlignment="Center" />
            </WrapPanel>

            <!-- Tabs (design 2026-07-18 section 4): Details | Sessions | Vocabulary | Advanced,
                 opening on Sessions (SelectedIndex=1). Each tab manages its own scrolling - the
                 old whole-pane ScrollViewer is gone. -->
            <TabControl Grid.Row="1" SelectedIndex="1">
                <TabItem Header="Details">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel Margin="0,8,0,0">
                            <ui:Card Style="{StaticResource SectionCard}">
                                <!-- The ENTIRE existing Details card inner StackPanel, moved as-is
                                     (Name/Reference/Description/Archived + CascadeStatus). -->
                            </ui:Card>
                            <ui:Card Style="{StaticResource SectionCard}">
                                <!-- The ENTIRE existing Roster card inner StackPanel, moved as-is. -->
                            </ui:Card>
                        </StackPanel>
                    </ScrollViewer>
                </TabItem>
                <TabItem Header="Sessions">
                    <DockPanel Margin="0,8,0,0">
                        <WrapPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
                            <ui:Button Content="Add sessions..." Appearance="Primary"
                                       Click="OnAddSessions" Margin="0,0,8,8" />
                            <ui:Button Content="Open" Appearance="Secondary" Margin="0,0,8,8"
                                       ToolTip="Open the transcript read view"
                                       IsEnabled="{Binding HasTaggedSelection}" Click="OnOpenTranscript" />
                            <ui:Button Content="Details" Appearance="Secondary" Margin="0,0,8,8"
                                       ToolTip="Open Session Details (title, speakers, matters)"
                                       IsEnabled="{Binding HasTaggedSelection}" Click="OnOpenDetails" />
                            <ui:Button Content="Untag" Appearance="Secondary" Margin="0,0,8,8"
                                       IsEnabled="{Binding HasTaggedSelection}" Click="OnUntagSelected" />
                            <ui:TextBox Width="180" Margin="0,0,0,8" VerticalAlignment="Center"
                                        PlaceholderText="Filter by title..."
                                        Text="{Binding TaggedFilterText, UpdateSourceTrigger=PropertyChanged}" />
                        </WrapPanel>
                        <controls:PagerControl DockPanel.Dock="Bottom" Margin="0,8,0,0"
                                               DataContext="{Binding TaggedPager}" />
                        <ui:DataGrid x:Name="TaggedGrid" ItemsSource="{Binding TaggedSessions}"
                                     SelectedItem="{Binding SelectedTagged}"
                                     AutoGenerateColumns="False" IsReadOnly="True"
                                     CanUserAddRows="False" GridLinesVisibility="None"
                                     HeadersVisibility="Column" SelectionMode="Single"
                                     VirtualizingPanel.IsVirtualizing="True"
                                     VirtualizingPanel.VirtualizationMode="Recycling"
                                     MouseDoubleClick="OnTaggedRowDoubleClick">
                            <ui:DataGrid.Columns>
                                <DataGridTextColumn Header="Title" Width="*"
                                                    Binding="{Binding Title, Mode=OneWay}" />
                                <DataGridTextColumn Header="Date" Width="Auto"
                                                    Binding="{Binding DateDisplay, Mode=OneWay}" />
                                <DataGridTextColumn Header="Duration" Width="Auto"
                                                    Binding="{Binding DurationDisplay, Mode=OneWay}" />
                            </ui:DataGrid.Columns>
                        </ui:DataGrid>
                    </DockPanel>
                </TabItem>
                <TabItem Header="Vocabulary">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                        <StackPanel Margin="0,8,0,0">
                            <ui:Card Style="{StaticResource SectionCard}">
                                <!-- The ENTIRE existing Custom-vocabulary card inner StackPanel,
                                     moved as-is (note + Terms + Corrections + Re-render button). -->
                            </ui:Card>
                        </StackPanel>
                    </ScrollViewer>
                </TabItem>
                <TabItem Header="Advanced">
                    <StackPanel Margin="0,8,0,0">
                        <ui:Card Style="{StaticResource SectionCard}">
                            <!-- The ENTIRE existing Advanced card inner StackPanel (Repair index),
                                 moved as-is. -->
                        </ui:Card>
                        <!-- The existing Export matter archive button + progress row + Delete matter
                             button, moved as-is. -->
                    </StackPanel>
                </TabItem>
            </TabControl>
        </Grid>
```

- [ ] **Step 2: Update the code-behind**

In `src/LocalScribe.App/Pages/MattersPage.xaml.cs`:

1. Replace `OnJumpToSession` with the selection-based handlers:

```csharp
    private void OnOpenTranscript(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTagged is { } t) _vm.OpenTranscript(t.SessionId);
    }

    private void OnTaggedRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm.SelectedTagged is { } t) _vm.OpenTranscript(t.SessionId);
    }

    private void OnOpenDetails(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTagged is { } t) _vm.JumpToSession(t.SessionId);
    }
```

2. Rework `OnUntagSession` into `OnUntagSelected` — same confirm + `CanUntag` pre-check flow, sourcing the id from `_vm.SelectedTagged` instead of the button `Tag`:

```csharp
    /// <summary>Untag confirm (design 5.4): Yes/No dialog mirroring OnDeleteMatter. The
    /// open-window pre-check answers "close it first" BEFORE the confirm; UntagSessionAsync
    /// re-checks at execution time (the authoritative, unit-tested guard).</summary>
    private async void OnUntagSelected(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTagged is not { } t) return;
        if (!_vm.CanUntag(t.SessionId))
        {
            MessageBox.Show(
                "This session is open in another window (Session Details or read view). Close it first, then untag.",
                "Untag session", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = MessageBox.Show(
            $"Untag this session from \"{_vm.EditName}\"? The session itself is kept; only the matter tag is removed.",
            "Untag session", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes) await _vm.UntagSessionAsync(t.SessionId);
    }
```

3. Add the picker-dialog handler:

```csharp
    /// <summary>Add-sessions picker (design 2026-07-18 section 4): dialog owned by the main
    /// window; OK applies the batch through the VM's SaveMetaAsync delta path.</summary>
    private async void OnAddSessions(object sender, RoutedEventArgs e)
    {
        var candidates = await _vm.ListUntaggedSessionsAsync();
        var picker = new AddSessionsPickerViewModel(candidates);
        var dialog = new AddSessionsDialog(picker) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await _vm.AddSessionsAsync(picker.SelectedIds);
    }
```

- [ ] **Step 3: Create the dialog**

`src/LocalScribe.App/AddSessionsDialog.xaml` (a plain `Window`, per the house startup-rendering rule for simple modal dialogs — the consent-dialog precedent):

```xml
<Window x:Class="LocalScribe.App.AddSessionsDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        Title="Add sessions to matter" Height="480" Width="560"
        MinHeight="320" MinWidth="420"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False">
    <DockPanel Margin="12" TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}">
        <ui:TextBox DockPanel.Dock="Top" Margin="0,0,0,8"
                    PlaceholderText="Filter by title..."
                    Text="{Binding FilterText, UpdateSourceTrigger=PropertyChanged}" />
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal"
                    HorizontalAlignment="Right" Margin="0,8,0,0">
            <ui:Button Content="Add selected" Appearance="Primary" MinWidth="110"
                       Margin="0,0,8,0" Click="OnOk" />
            <ui:Button Content="Cancel" Appearance="Secondary" MinWidth="80" IsCancel="True" />
        </StackPanel>
        <ListView ItemsSource="{Binding Visible}"
                  VirtualizingPanel.IsVirtualizing="True"
                  VirtualizingPanel.VirtualizationMode="Recycling">
            <ListView.ItemTemplate>
                <DataTemplate>
                    <StackPanel Orientation="Horizontal" Margin="0,2">
                        <CheckBox IsChecked="{Binding IsSelected}" VerticalAlignment="Center"
                                  Margin="0,0,8,0" />
                        <TextBlock Text="{Binding Title, Mode=OneWay}" VerticalAlignment="Center"
                                   Margin="0,0,8,0" />
                        <TextBlock Text="{Binding DateDisplay, Mode=OneWay}" Opacity="0.7"
                                   VerticalAlignment="Center" Margin="0,0,8,0" />
                        <TextBlock Text="{Binding Source, Mode=OneWay}" Opacity="0.7"
                                   VerticalAlignment="Center" />
                    </StackPanel>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </DockPanel>
</Window>
```

`src/LocalScribe.App/AddSessionsDialog.xaml.cs`:

```csharp
using System.Windows;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App;

/// <summary>Modal multi-select session picker for the Matters page (design 2026-07-18
/// section 4). Humble shell: all list/filter/selection logic lives in
/// AddSessionsPickerViewModel; OK with nothing checked is a harmless no-op batch.</summary>
public partial class AddSessionsDialog
{
    public AddSessionsDialog(AddSessionsPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
```

- [ ] **Step 4: App wiring**

In `src/LocalScribe.App/App.xaml.cs`, replace lines ~447-455 (the Matters wiring block) with:

```csharp
        // Matters-page tagged-session actions (design 2026-07-18 section 4): the primary "Open"
        // now opens the TRANSCRIPT read view - deliberately reversing the Stage 5.2 decision
        // that kept the read view Sessions-page-only. "Details" keeps the Session Details window
        // as the secondary action; both reuse the same dedup/activate factories above.
        mattersVm.OpenSessionDetailsRequested += openSessionDetails;
        mattersVm.OpenReadViewRequested += openReadView;

        // Stage 5.4 5.4 + design 2026-07-18: a tag/untag from the Matters page makes the Sessions
        // grid's matter chips for that row stale - refresh just that row in place (mirrors the
        // detailEditor.Saved wiring above). RefreshRowAsync catches its own faults.
        mattersVm.SessionUntagged += id => _ = sessionsVm.RefreshRowAsync(id);
        mattersVm.SessionTagged += id => _ = sessionsVm.RefreshRowAsync(id);
```

- [ ] **Step 5: Build, run, verify, commit**

Run: `dotnet build LocalScribe.slnx` (0 warnings), `dotnet test tests/LocalScribe.App.Tests` (green).
Manual: Matters page → select a matter → lands on the Sessions tab with the paged grid; Open/double-click opens the transcript; Details opens Session Details; Untag still confirms; Add sessions… lists only untagged+unarchived, multi-select works, OK tags and the grid + left-list counts refresh; Details/Vocabulary/Advanced tabs carry the old content; no whole-pane scrollbar anywhere. Close the app.

```bash
git add src/LocalScribe.App/Pages/MattersPage.xaml src/LocalScribe.App/Pages/MattersPage.xaml.cs src/LocalScribe.App/AddSessionsDialog.xaml src/LocalScribe.App/AddSessionsDialog.xaml.cs src/LocalScribe.App/App.xaml.cs
git commit -m "feat(matters): header + Details/Sessions/Vocabulary/Advanced tabs, paged sessions grid, Add-sessions dialog, Open opens the transcript"
```

---

### Task 10: Record console — label truncation fix

**Files:**
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml` (lines ~48-62, plus the mid-recording remote-target combo at ~300-305)

**Interfaces:** view-only; no VM change.

**Cause (verified):** the "Microphone"/"Remote target" rows are centered horizontal `StackPanel`s whose ComboBox has `MinWidth` but no `MaxWidth`; a long device name grows the combo past the window width and the centered panel clips BOTH edges — the label loses its left characters ("ophone").

- [ ] **Step 1: Cap the combos and ellipsize their content**

Replace lines ~48-62 with:

```xml
                <!-- MaxWidth caps the combo so a long device name can never push the centered row
                     past the window edges (the "ophone" clip); the ItemTemplate ellipsizes the
                     text inside the fixed box with the full name in a tooltip. ItemTemplate
                     replaces DisplayMemberPath (mutually exclusive in WPF). -->
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,0,0,12">
                    <TextBlock Text="Microphone" VerticalAlignment="Center" Margin="0,0,8,0" />
                    <ComboBox MinWidth="220" MaxWidth="340"
                              ItemsSource="{Binding Console.MicChoices}"
                              SelectedItem="{Binding Console.SelectedMic, Mode=TwoWay}">
                        <ComboBox.ItemTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis"
                                           ToolTip="{Binding Label}" />
                            </DataTemplate>
                        </ComboBox.ItemTemplate>
                    </ComboBox>
                </StackPanel>
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,0,0,4">
                    <TextBlock Text="Remote target" VerticalAlignment="Center" Margin="0,0,8,0" />
                    <ComboBox MinWidth="240" MaxWidth="340"
                              ItemsSource="{Binding Console.RemoteTargetOptions}"
                              SelectedItem="{Binding Console.SelectedRemoteTarget, Mode=OneWay}"
                              SelectionChanged="RemoteTargetCombo_SelectionChanged"
                              DropDownOpened="RemoteTargetCombo_DropDownOpened">
                        <ComboBox.ItemTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis"
                                           ToolTip="{Binding Label}" />
                            </DataTemplate>
                        </ComboBox.ItemTemplate>
                    </ComboBox>
                </StackPanel>
```

- [ ] **Step 2: Same treatment for the mid-recording remote-target combo**

At lines ~300-305 (the capture-scope Grid), add `MaxWidth="340"` to that ComboBox and replace its `DisplayMemberPath="Label"` with the same ellipsizing `ItemTemplate` as above.

- [ ] **Step 3: Build, verify, commit**

Run: `dotnet build LocalScribe.slnx` — 0 warnings.
Manual: open the Record console with the LifeCam ("Desktop Microphone (2- Microsoft® LifeCam HD-3000)") selected — the full word "Microphone" renders, the device name ellipsizes inside the combo, and the tooltip shows the full name. Check both the pre-record and mid-recording remote-target rows. Close the app.

```bash
git add src/LocalScribe.App/LiveViewWindow.xaml
git commit -m "fix(console): cap device combos + ellipsize so long device names never clip the row labels"
```

---

### Task 11: Final gate, smoke runbook, review

**Files:**
- Create: `docs/plans/2026-07-18-ux-round-smoke-runbook.md`

- [ ] **Step 1: Full gate**

Run, in order (app closed):

```bash
dotnet build LocalScribe.slnx
dotnet test tests/LocalScribe.App.Tests
dotnet test tests/LocalScribe.Core.Tests
```

Expected: 0 warnings; App tests all green; Core tests green except the 2 known fixture failures. Fix anything else before proceeding.

- [ ] **Step 2: Write the smoke runbook**

Create `docs/plans/2026-07-18-ux-round-smoke-runbook.md`:

```markdown
# UX round 2026-07-18 - manual smoke runbook (user)

Prereq: >50 sessions on disk to exercise multi-page states (or temporarily set the page size to 25).

## P - Pagination
- P1 Sessions page: footer shows "Page 1 of N"; Previous/Next page through; no half-clipped bottom row.
- P2 Page-size picker: 25/50/100 re-slices and rewinds to page 1.
- P3 Typing in "Filter sessions..." rewinds to page 1.
- P4 Stop a recording while on page 2: the page does not jump (finalizing row upserts in place or off-page).
- P5 Search page: query with many hits pages by session card; new query rewinds to page 1.

## S - Search defaults
- S1 Fresh app start -> Search page: combos SHOW "All matters" and "All apps"; both date pickers empty.
- S2 Pick an app facet, navigate away and back: the picked value survives (singleton VM) and still filters.

## F - Find escalation
- F1 Read view: Find button visible next to Edit; opens the bar with the box focused; Ctrl+F unchanged.
- F2 "Search all sessions" with a term: main window opens/activates on Search, term pre-filled, facets All/All/blank.
- F3 Same with the main window closed first (tray path): fresh window lands on Search, not Sessions.
- F4 Search-page snippet click still deep-links into the read view at the right segment.

## M - Matters overhaul
- M1 Select a matter: right pane = header (name, ref, client chip, created) + tabs, opening on Sessions.
- M2 Open / double-click a tagged session -> transcript read view; Details -> Session Details; Untag still confirms.
- M3 Open on a pending-recovery session -> Info toast, no window.
- M4 Add sessions...: lists only untagged+unarchived, filter works, multi-select tags all; grid + "N session(s)" count + Sessions-page chips all refresh.
- M5 Details/Vocabulary/Advanced tabs: old editors work (rename cascades, vocab add/remove, re-render, export, delete-blocked-while-tagged).
- M6 No whole-pane scrollbar; the sessions grid pages instead of scrolling forever.

## C - Record console
- C1 With the LifeCam mic selected: "Microphone" label fully visible; device name ellipsized with tooltip.
- C2 "Remote target" row same; mid-recording capture-scope combo same.
```

- [ ] **Step 3: Commit and request review**

```bash
git add docs/plans/2026-07-18-ux-round-smoke-runbook.md
git commit -m "docs: manual smoke runbook for the 2026-07-18 UX round"
```

Then run the whole-branch review per superpowers:requesting-code-review before merging (house rule: whole-branch review has repeatedly caught cross-task seam defects the per-task reviews missed — prioritize the seams: pager Changed/SetTotal contract vs each host's reset points; the Matters SelectAsync dispatch ordering; the App.xaml.cs wiring).

Merge only after review + user approval, `--no-ff` per house convention.
