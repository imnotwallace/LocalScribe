# Assistant Side Panel - Phase 2 (session panel) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Relocate session chat + summary into a collapsible right-side "Ask" panel inside the
transcript read view, with named-thread management, and remove the Session Details Assistant tab.

**Architecture:** A new reusable `AssistantSidePanel` UserControl (Summary expander + thread
selector + the existing `AssistantChatPanel`) bound to a new `AssistantSidePanelViewModel`, hosted
in `ReadViewWindow` as a splitter-resizable Grid column. The chat lifecycle wiring moves from the
`openSessionDetails` factory in `App.xaml.cs` to `openReadView`. Panel open/width persists in
`window-state.json` via a new `assistantPanel` side-map keyed per window family.

**Tech Stack:** .NET 10 WPF, WPF-UI (`ui:` namespace), CommunityToolkit.Mvvm
(`ObservableObject`/`[ObservableProperty]`/RelayCommand), xUnit.

**Spec:** `docs/superpowers/specs/2026-07-24-assistant-chat-surfaces-design.md` - "Architecture >
App", the Phase 2 entry in "Phasing", and the whole "Addendum - Phase 2-4 UX + structural
decisions (2026-07-25)". The addendum is binding.

## Global Constraints

- No Unicode emojis anywhere (code, tests, docs). ASCII source; middle dot only as `·` escape.
- File-scoped namespaces. `///` doc comments explain WHY, not what.
- Build gate: `dotnet build LocalScribe.slnx` must stay 0 warnings / 0 errors.
- Test gate: `dotnet test tests/LocalScribe.Core.Tests` green except the 2 known privileged-fixture
  fails (DiarisationFixtureTests, GoldenCorpusFixtureTests); `dotnet test tests/LocalScribe.App.Tests`
  fully green (Stop_upserts flake: re-run once before calling it a regression).
- Evidentiary posture (LOCKED): the AI-draft label (`AssistantChatViewModel.AiDraftLabel` /
  `AssistantPrompts.DraftLabel`) rides every rendered artifact; nothing persists on a failed or
  cancelled ask; degradation is surfaced, never silent; transcripts are never edited by this work.
- Keep the three Phase-1 convenience bridges: `AssistantChatStore.AppendAsync`,
  `AssistantChatLog.[JsonIgnore] Turns`, `AssistantQaService` 3-arg `AskAsync`. Tests rely on them.
- At most ONE warm chat helper globally; a recording start cancels chat; threads within one scope
  share the one helper (never reload the transcript on thread switch).
- Branch: `feat/assistant-surfaces-phase2` off master, in a DEDICATED git worktree. Never push.
- Close LocalScribe.App.exe before building (a running app locks Core.dll -> MSB3027).
- XAML theme resources only - no ARGB literals (XamlHygiene). WPF-free VMs; dispatch injected.

## File Structure

- Create: `src/LocalScribe.App/ViewModels/AssistantChatThreadsViewModel.cs` - thread list +
  New/Rename/Archive/ShowArchived over `AssistantChatStore`, wrapping `AssistantChatViewModel`.
- Create: `src/LocalScribe.App/ViewModels/AssistantSidePanelViewModel.cs` - panel composite
  (optional Summary + Threads + IsOpen + CoverageText slot).
- Create: `src/LocalScribe.App/Controls/AssistantSidePanel.xaml(.cs)` - the reusable panel control.
- Modify: `src/LocalScribe.App/ViewModels/WindowStateStore.cs` - `assistantPanel` side-map.
- Modify: `src/LocalScribe.App/ViewModels/AssistantChatViewModel.cs` - `SelectThreadAsync` +
  `IsReadOnly` gate.
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml(.cs)` - host the panel + Ask toggle + splitter.
- Modify: `src/LocalScribe.App/App.xaml.cs` - move chat lifecycle openSessionDetails -> openReadView.
- Modify: `src/LocalScribe.App/SessionDetailsWindow.xaml` - remove the Assistant tab.
- Modify: `src/LocalScribe.App/ViewModels/MetadataEditorViewModel.cs` - drop Assistant/Chat.
- Tests: `tests/LocalScribe.App.Tests/WindowStateStoreTests.cs` (extend),
  `tests/LocalScribe.App.Tests/AssistantChatViewModelTests.cs` (extend),
  `tests/LocalScribe.App.Tests/AssistantChatThreadsViewModelTests.cs` (new),
  `tests/LocalScribe.App.Tests/MetadataEditor*Tests.cs` (adjust).

---

### Task 1: WindowStateStore assistantPanel side-map

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/WindowStateStore.cs`
- Test: `tests/LocalScribe.App.Tests/WindowStateStoreTests.cs`

**Interfaces:**
- Consumes: existing `FileShape`/`Placement` private records, `JsonOpts`.
- Produces (later tasks rely on these exact signatures):
  - `public sealed record AssistantPanelState(bool Open, double Width);`
  - `public AssistantPanelState? LoadAssistantPanel(string key)` - null when absent/corrupt.
  - `public void SaveAssistantPanel(string key, AssistantPanelState state)` - read-modify-write,
    never clobbers window placements or LastExportDir; all failures swallowed (file is never truth).
  - Keys in use: `"readView"` (this phase), `"matters"` (Phase 3).

- [ ] **Step 1: Write the failing tests** (append to `WindowStateStoreTests.cs`, following its
  existing temp-file pattern):

```csharp
[Fact]
public void AssistantPanel_roundtrips_per_key()
{
    string path = Path.Combine(TempDir(), "window-state.json");
    var store = new WindowStateStore(path);
    store.SaveAssistantPanel("readView", new AssistantPanelState(true, 420));
    store.SaveAssistantPanel("matters", new AssistantPanelState(false, 300));
    var read = new WindowStateStore(path);
    Assert.Equal(new AssistantPanelState(true, 420), read.LoadAssistantPanel("readView"));
    Assert.Equal(new AssistantPanelState(false, 300), read.LoadAssistantPanel("matters"));
    Assert.Null(read.LoadAssistantPanel("other"));
}

[Fact]
public void AssistantPanel_save_preserves_placements_and_export_dir()
{
    string path = Path.Combine(TempDir(), "window-state.json");
    var store = new WindowStateStore(path);
    store.Save("main", new WindowPlacement(1, 2, 3, 4));
    store.SaveLastExportDir(@"C:\exports");
    store.SaveAssistantPanel("readView", new AssistantPanelState(true, 400));
    var read = new WindowStateStore(path);
    Assert.Equal(new WindowPlacement(1, 2, 3, 4), read.Load("main"));
    Assert.Equal(@"C:\exports", read.LoadLastExportDir());
    Assert.Equal(new AssistantPanelState(true, 400), read.LoadAssistantPanel("readView"));
}

[Fact]
public void Placement_save_preserves_assistant_panel()
{
    string path = Path.Combine(TempDir(), "window-state.json");
    var store = new WindowStateStore(path);
    store.SaveAssistantPanel("readView", new AssistantPanelState(true, 400));
    store.Save("main", new WindowPlacement(1, 2));
    store.SaveLastExportDir(@"C:\exports");
    Assert.Equal(new AssistantPanelState(true, 400),
        new WindowStateStore(path).LoadAssistantPanel("readView"));
}
```

(If the test class has no `TempDir()` helper, reuse whatever temp-path idiom its existing tests
use - do not invent a new pattern.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~WindowStateStoreTests" 2>&1 | tail -5`
Expected: compile error - `AssistantPanelState` not defined.

- [ ] **Step 3: Implement.** In `WindowStateStore.cs`:

Add after the `WindowPlacement` record (file top level):

```csharp
/// <summary>Remembered assistant-panel state per window FAMILY (addendum 2026-07-25): one bit +
/// width for all read views, one for the matters page - NOT per session/matter (the placement
/// store's own single-key-per-family scheme). Presence of an entry means the user made an
/// EXPLICIT choice; absence means the open-iff-history heuristic applies.</summary>
public sealed record AssistantPanelState(bool Open, double Width);
```

Extend `FileShape` with the side-map (same nullable-with-default pattern as `LastExportDir`):

```csharp
    private sealed record FileShape(
        Dictionary<string, Placement>? Windows = null, double? X = null, double? Y = null,
        string? LastExportDir = null,
        Dictionary<string, PanelState>? AssistantPanel = null);

    private sealed record PanelState(bool Open, double Width);
```

Rework the three writers so each preserves the other two fields. Replace the bodies of `Save` and
`SaveLastExportDir`, and add `SaveAssistantPanel`/`LoadAssistantPanel`:

```csharp
    public void Save(string key, WindowPlacement placement)
    {
        try
        {
            // Read-modify-write so saving one window's placement never drops another's
            // (and folds a legacy bare {x,y} file into the keyed map as "overlay").
            var map = ReadMap() ?? new Dictionary<string, Placement>(StringComparer.Ordinal);
            map[key] = new Placement(placement.X, placement.Y, placement.Width, placement.Height);
            var shape = ReadShape();
            Write(new FileShape(map, LastExportDir: shape?.LastExportDir,
                AssistantPanel: shape?.AssistantPanel));
        }
        catch { /* volatile state - losing it costs one re-drag */ }
    }

    public void SaveLastExportDir(string dir)
    {
        try
        {
            var shape = ReadShape();
            Write(new FileShape(ReadMap(), LastExportDir: dir, AssistantPanel: shape?.AssistantPanel));
        }
        catch { /* volatile state - losing it costs one re-pick */ }
    }

    public AssistantPanelState? LoadAssistantPanel(string key)
    {
        var panels = ReadShape()?.AssistantPanel;
        return panels is not null && panels.TryGetValue(key, out var p)
            ? new AssistantPanelState(p.Open, p.Width) : null;
    }

    public void SaveAssistantPanel(string key, AssistantPanelState state)
    {
        try
        {
            var shape = ReadShape();
            var panels = shape?.AssistantPanel is { } existing
                ? new Dictionary<string, PanelState>(existing, StringComparer.Ordinal)
                : new Dictionary<string, PanelState>(StringComparer.Ordinal);
            panels[key] = new PanelState(state.Open, state.Width);
            Write(new FileShape(ReadMap(), LastExportDir: shape?.LastExportDir,
                AssistantPanel: panels));
        }
        catch { /* volatile state - losing it costs one re-toggle */ }
    }

    private void Write(FileShape shape)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(shape, JsonOpts));
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~WindowStateStoreTests" 2>&1 | tail -5`
Expected: PASS (all, including the pre-existing placement tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/WindowStateStore.cs tests/LocalScribe.App.Tests/WindowStateStoreTests.cs
git commit -m "feat(panel): WindowStateStore assistantPanel side-map (open/width per window family)"
```

---

### Task 2: AssistantChatViewModel thread-switch API + read-only gate

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/AssistantChatViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/AssistantChatViewModelTests.cs`

**Interfaces:**
- Consumes: `AssistantChatStore.LoadAsync`, existing `_activeThreadId` pinning.
- Produces (Task 3 relies on these):
  - `public Task SelectThreadAsync(string threadId, CancellationToken ct)` - loads that thread's
    turns into `Turns`, pins `_activeThreadId`; unknown id = no-op (keeps current view). Never
    touches `_service` (warm helper untouched - LOCKED).
  - `[ObservableProperty] bool IsReadOnly` - true gates `AskCommand.CanExecute` (archived thread
    is read-only until unarchived, addendum).

- [ ] **Step 1: Write the failing tests** (append to `AssistantChatViewModelTests.cs`, reusing its
  existing fakes/store-seeding idioms - it already seeds v2 stores via `AssistantChatStore`):

```csharp
[Fact]
public async Task SelectThreadAsync_swaps_rendered_turns_and_pins_thread()
{
    // Seed a two-thread store: thread A one turn, thread B two turns.
    // (Build AssistantChatLog directly and SaveAsync it - same idiom the Phase 1 tests use.)
    var store = new AssistantChatStore(Path.Combine(TempDir(), "chats.json"));
    var a = AssistantChatStore.NewThread("Chat 1", DateTimeOffset.UtcNow) with { Turns = [Turn("qA")] };
    var b = AssistantChatStore.NewThread("Chat 2", DateTimeOffset.UtcNow) with { Turns = [Turn("qB1"), Turn("qB2")] };
    await store.SaveAsync(new AssistantChatLog { Chats = [a, b] }, CancellationToken.None);
    var vm = new AssistantChatViewModel(() => null, store, Reporter(), a2 => a2());
    await vm.LoadHistoryAsync(CancellationToken.None);
    Assert.Single(vm.Turns);                       // active = first non-archived = A
    await vm.SelectThreadAsync(b.Id, CancellationToken.None);
    Assert.Equal(2, vm.Turns.Count);
    Assert.Equal("qB1", vm.Turns[0].Question);
}

[Fact]
public async Task SelectThreadAsync_unknown_id_is_a_noop()
{
    var store = new AssistantChatStore(Path.Combine(TempDir(), "chats.json"));
    var a = AssistantChatStore.NewThread("Chat 1", DateTimeOffset.UtcNow) with { Turns = [Turn("qA")] };
    await store.SaveAsync(new AssistantChatLog { Chats = [a] }, CancellationToken.None);
    var vm = new AssistantChatViewModel(() => null, store, Reporter(), a2 => a2());
    await vm.LoadHistoryAsync(CancellationToken.None);
    await vm.SelectThreadAsync("no-such-id", CancellationToken.None);
    Assert.Single(vm.Turns);                       // unchanged
}

[Fact]
public void IsReadOnly_gates_ask()
{
    var store = new AssistantChatStore(Path.Combine(TempDir(), "chats.json"));
    var vm = new AssistantChatViewModel(() => null, store, Reporter(), a2 => a2());
    vm.QuestionText = "q";
    Assert.True(vm.AskCommand.CanExecute(null));
    vm.IsReadOnly = true;
    Assert.False(vm.AskCommand.CanExecute(null));
}
```

`Turn(...)` / `Reporter()` / `TempDir()`: reuse the test file's existing helpers for building an
`AssistantChatTurn`, a fake `IUiErrorReporter`, and temp paths - they exist from Phase 1; match
their exact names rather than these placeholders.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~AssistantChatViewModelTests" 2>&1 | tail -5`
Expected: compile error - `SelectThreadAsync`/`IsReadOnly` not defined.

- [ ] **Step 3: Implement.** In `AssistantChatViewModel.cs`:

Add the property (beside `_isAvailable`):

```csharp
    /// <summary>True while an ARCHIVED thread is selected (addendum 2026-07-25): archived threads
    /// are read-only until unarchived, so the Ask gate must refuse - archiving is "hide, keep on
    /// disk", and appending to a hidden-by-default thread would silently grow evidence the user
    /// believes is closed.</summary>
    [ObservableProperty] private bool _isReadOnly;
```

Update the CanExecute in the ctor:

```csharp
        AskCommand = new AsyncRelayCommand(AskAsync,
            () => !IsAsking && !IsReadOnly && QuestionText.Trim().Length > 0);
```

Add beside the other partials:

```csharp
    partial void OnIsReadOnlyChanged(bool value) => AskCommand.NotifyCanExecuteChanged();
```

Add after `LoadHistoryAsync`:

```csharp
    /// <summary>Thread switch (Phase 2 selector): swap the RENDERED turn list to the given thread
    /// and pin future asks to it. Deliberately never touches _service - the warm helper's KV
    /// prefix is the scope context, shared by every thread of this scope (design "Architecture >
    /// One warm helper"), so switching threads must never reload the transcript. An unknown id
    /// (thread deleted/renamed underneath a stale selector item) keeps the current view.</summary>
    public async Task SelectThreadAsync(string threadId, CancellationToken ct)
    {
        try
        {
            var log = await Task.Run(() => _store.LoadAsync(ct), ct);
            var thread = log.Chats.FirstOrDefault(c => c.Id == threadId);
            if (thread is null) return;
            _activeThreadId = thread.Id;
            _dispatch(() =>
            {
                Turns.Clear();
                foreach (var t in thread.Turns) Turns.Add(new ChatTurnViewModel(t));
            });
        }
        catch (Exception ex) { _reporter.Report("Load assistant chat thread", ex); }
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~AssistantChatViewModelTests" 2>&1 | tail -5`
Expected: PASS (new + all pre-existing).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/AssistantChatViewModel.cs tests/LocalScribe.App.Tests/AssistantChatViewModelTests.cs
git commit -m "feat(chat): SelectThreadAsync thread switch + IsReadOnly ask gate"
```

---

### Task 3: AssistantChatThreadsViewModel (thread management)

**Files:**
- Create: `src/LocalScribe.App/ViewModels/AssistantChatThreadsViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/AssistantChatThreadsViewModelTests.cs` (new)

**Interfaces:**
- Consumes: `AssistantChatViewModel.SelectThreadAsync/IsReadOnly` (Task 2),
  `AssistantChatStore.LoadAsync/SaveAsync/NewThread`, `AssistantChatThread` record.
- Produces (Task 4's XAML + Task 6's wiring bind these exact names):
  - `public sealed record ThreadListItem(string Id, string Name, bool Archived, bool HasRecap)`
    with `public string Display => Archived ? Name + " (archived)" : Name;`
  - `AssistantChatViewModel Chat { get; }` (the wrapped VM)
  - `ObservableCollection<ThreadListItem> Threads { get; }` (honors ShowArchived)
  - `ThreadListItem? SelectedThread { get; set; }` ([ObservableProperty])
  - `bool ShowArchived { get; set; }` ([ObservableProperty]; toggling rebuilds Threads)
  - `bool HasRecap { get; }` ([ObservableProperty]; selected thread's condense indicator)
  - `bool HasAnyHistory { get; }` ([ObservableProperty]; any thread (archived or not) with >= 1
    turn - the panel-open heuristic input)
  - `bool IsRenaming { get; set; }`, `string RenameText { get; set; }` ([ObservableProperty])
  - Commands: `IAsyncRelayCommand NewChatCommand`, `IRelayCommand BeginRenameCommand`,
    `IAsyncRelayCommand CommitRenameCommand`, `IRelayCommand CancelRenameCommand`,
    `IAsyncRelayCommand ArchiveCommand`, `IAsyncRelayCommand UnarchiveCommand`
  - `Task LoadAsync(CancellationToken ct)` - loads the store, builds Threads, selects the first
    non-archived thread (or keeps the current selection by id), pushes the selection into Chat.
  - Ctor: `AssistantChatThreadsViewModel(AssistantChatViewModel chat, AssistantChatStore store,
    IUiErrorReporter reporter, Action<Action> dispatch, TimeProvider time)`

**Behavioral rules (from the addendum):**
- Archived threads appear in Threads only when ShowArchived; selecting one sets
  `Chat.IsReadOnly = true`; `UnarchiveCommand` clears the flag and re-selects it editable.
- `ArchiveCommand` archives the SELECTED thread, then selects the first remaining non-archived
  thread (or null if none - the next ask mints "Chat 1" via the service, and the TurnCompleted
  refresh picks it up).
- New chat name: "Chat N" where N = (max numeric suffix over existing "Chat k" names) + 1.
- Rename is inline: BeginRename seeds RenameText from the selection, CommitRename persists via
  load-modify-save, CancelRename discards. Empty/whitespace RenameText = no-op commit.
- `Chat.TurnCompleted += _ => _ = LoadAsync(CancellationToken.None);` - refreshes HasRecap (a
  condense may have landed) and adopts a service-minted "Chat 1" on a previously empty store.
- All store mutations are full load-modify-save via `SaveAsync` (v2 is never blind-appended).
- Every mutation catches into `reporter.Report(...)`; nothing throws to the UI.

- [ ] **Step 1: Write the failing tests** (new file; reuse `AssistantChatViewModelTests`' fakes
  idiom for reporter/dispatch/store seeding):

```csharp
// Tests to write (full bodies, one [Fact] each; seed stores exactly like Task 2's tests):
// 1. LoadAsync_lists_non_archived_and_selects_first - 2 threads + 1 archived: Threads has 2,
//    SelectedThread.Id == first.Id, Chat.Turns shows first thread's turns.
// 2. ShowArchived_reveals_archived_with_suffix - toggling ShowArchived=true adds the archived
//    item; its Display ends with " (archived)".
// 3. Selecting_archived_sets_chat_readonly - select the archived item; Chat.IsReadOnly true;
//    re-select a live one; false.
// 4. NewChat_appends_and_selects - store had "Chat 1","Chat 3": NewChatCommand creates "Chat 4",
//    store on disk has 3 threads, SelectedThread is the new one, Chat.Turns empty.
// 5. Rename_persists - BeginRename, RenameText="Strategy", CommitRename: store thread Name
//    updated, Threads item shows "Strategy", selection kept.
// 6. Archive_hides_and_selects_next - archive selected of 2: on disk Archived=true, Threads
//    drops it (ShowArchived=false), selection moves to the remaining thread.
// 7. Archive_last_thread_leaves_no_selection - single thread archived: SelectedThread null,
//    Threads empty.
// 8. Unarchive_restores_editable - with ShowArchived, select archived, UnarchiveCommand: on
//    disk Archived=false, Chat.IsReadOnly false.
// 9. HasRecap_follows_selection - thread A Recap=null, thread B Recap="r": select A -> false,
//    select B -> true.
// 10. HasAnyHistory_counts_archived_turns - only an ARCHIVED thread has turns: true.
```

Write all ten as real test methods with real asserts - the comment block above is the checklist,
not the deliverable.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~AssistantChatThreadsViewModelTests" 2>&1 | tail -5`
Expected: compile error - type not defined.

- [ ] **Step 3: Implement** `AssistantChatThreadsViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;
namespace LocalScribe.App.ViewModels;

/// <summary>One selector row. Display carries the "(archived)" suffix so the dropdown needs no
/// template trigger; HasRecap rides along so selection can flip the condense indicator without a
/// second store read.</summary>
public sealed record ThreadListItem(string Id, string Name, bool Archived, bool HasRecap)
{
    public string Display => Archived ? Name + " (archived)" : Name;
}

/// <summary>Thread management around one scope's AssistantChatViewModel (addendum 2026-07-25):
/// the dropdown list, New/Rename/Archive/Unarchive, ShowArchived, and the condense indicator.
/// All store writes are full load-modify-save (v2 rule); the wrapped Chat VM keeps sole ownership
/// of asking and of the warm helper - this VM only ever tells it WHICH thread renders.</summary>
public sealed partial class AssistantChatThreadsViewModel : ObservableObject
{
    private readonly AssistantChatStore _store;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;

    public AssistantChatViewModel Chat { get; }
    public ObservableCollection<ThreadListItem> Threads { get; } = [];
    [ObservableProperty] private ThreadListItem? _selectedThread;
    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private bool _hasRecap;
    [ObservableProperty] private bool _hasAnyHistory;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    public IAsyncRelayCommand NewChatCommand { get; }
    public IRelayCommand BeginRenameCommand { get; }
    public IAsyncRelayCommand CommitRenameCommand { get; }
    public IRelayCommand CancelRenameCommand { get; }
    public IAsyncRelayCommand ArchiveCommand { get; }
    public IAsyncRelayCommand UnarchiveCommand { get; }

    public AssistantChatThreadsViewModel(AssistantChatViewModel chat, AssistantChatStore store,
        IUiErrorReporter reporter, Action<Action> dispatch, TimeProvider time)
    {
        (Chat, _store, _reporter, _dispatch, _time) = (chat, store, reporter, dispatch, time);
        NewChatCommand = new AsyncRelayCommand(NewChatAsync);
        BeginRenameCommand = new RelayCommand(() =>
        {
            if (SelectedThread is null) return;
            RenameText = SelectedThread.Name;
            IsRenaming = true;
        }, () => SelectedThread is not null);
        CommitRenameCommand = new AsyncRelayCommand(CommitRenameAsync);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        ArchiveCommand = new AsyncRelayCommand(
            () => SetArchivedAsync(archived: true), () => SelectedThread is { Archived: false });
        UnarchiveCommand = new AsyncRelayCommand(
            () => SetArchivedAsync(archived: false), () => SelectedThread is { Archived: true });
        // A finished turn may have minted "Chat 1" on an empty store or folded turns into a recap
        // mid-ask (condense) - refresh so the selector and indicator reflect on-disk truth.
        Chat.TurnCompleted += _ => _ = LoadAsync(CancellationToken.None);
    }

    partial void OnSelectedThreadChanged(ThreadListItem? value)
    {
        Chat.IsReadOnly = value?.Archived ?? false;
        HasRecap = value?.HasRecap ?? false;
        IsRenaming = false;
        BeginRenameCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
        UnarchiveCommand.NotifyCanExecuteChanged();
        if (value is not null) _ = Chat.SelectThreadAsync(value.Id, CancellationToken.None);
    }

    partial void OnShowArchivedChanged(bool value) => _ = LoadAsync(CancellationToken.None);

    /// <summary>Build the selector from the store, keeping the current selection by id when it
    /// still qualifies; else the first non-archived thread; else none (empty scope - the next ask
    /// mints "Chat 1" service-side and the TurnCompleted refresh adopts it).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var log = await Task.Run(() => _store.LoadAsync(ct), ct);
            var items = log.Chats
                .Where(c => ShowArchived || !c.Archived)
                .Select(c => new ThreadListItem(c.Id, c.Name, c.Archived, c.Recap is not null))
                .ToList();
            bool anyHistory = log.Chats.Any(c => c.Turns.Count > 0);
            _dispatch(() =>
            {
                string? keep = SelectedThread?.Id;
                Threads.Clear();
                foreach (var i in items) Threads.Add(i);
                HasAnyHistory = anyHistory;
                SelectedThread = items.FirstOrDefault(i => i.Id == keep)
                    ?? items.FirstOrDefault(i => !i.Archived);
            });
        }
        catch (Exception ex) { _reporter.Report("Load assistant chat threads", ex); }
    }

    private async Task NewChatAsync()
    {
        try
        {
            var log = await _store.LoadAsync(CancellationToken.None);
            int max = 0;
            foreach (var c in log.Chats)
                if (c.Name.StartsWith("Chat ", StringComparison.Ordinal)
                    && int.TryParse(c.Name.AsSpan(5), out int n) && n > max) max = n;
            var thread = AssistantChatStore.NewThread("Chat " + (max + 1), _time.GetUtcNow());
            await _store.SaveAsync(log with { Chats = [.. log.Chats, thread] }, CancellationToken.None);
            await LoadAsync(CancellationToken.None);
            _dispatch(() => SelectedThread = Threads.FirstOrDefault(t => t.Id == thread.Id));
        }
        catch (Exception ex) { _reporter.Report("New chat thread", ex); }
    }

    private async Task CommitRenameAsync()
    {
        string name = RenameText.Trim();
        if (SelectedThread is not { } sel || name.Length == 0) { IsRenaming = false; return; }
        await MutateAsync(sel.Id, t => t with { Name = name }, "Rename chat thread");
        IsRenaming = false;
    }

    private async Task SetArchivedAsync(bool archived)
    {
        if (SelectedThread is not { } sel) return;
        await MutateAsync(sel.Id, t => t with { Archived = archived },
            archived ? "Archive chat thread" : "Unarchive chat thread");
    }

    /// <summary>Load-modify-save one thread's metadata; turns are never touched here (they stay
    /// append-only within a thread - the store's own v2 rule).</summary>
    private async Task MutateAsync(string id, Func<AssistantChatThread, AssistantChatThread> mutate,
        string activity)
    {
        try
        {
            var log = await _store.LoadAsync(CancellationToken.None);
            var target = log.Chats.FirstOrDefault(c => c.Id == id);
            if (target is null) return;
            var chats = log.Chats.ToList();
            chats[chats.IndexOf(target)] = mutate(target);
            await _store.SaveAsync(log with { Chats = chats }, CancellationToken.None);
            await LoadAsync(CancellationToken.None);
        }
        catch (Exception ex) { _reporter.Report(activity, ex); }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~AssistantChatThreadsViewModelTests" 2>&1 | tail -5`
Expected: PASS x10.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/AssistantChatThreadsViewModel.cs tests/LocalScribe.App.Tests/AssistantChatThreadsViewModelTests.cs
git commit -m "feat(chat): AssistantChatThreadsViewModel - thread list, new/rename/archive, show-archived"
```

---

### Task 4: AssistantSidePanelViewModel + AssistantSidePanel control

**Files:**
- Create: `src/LocalScribe.App/ViewModels/AssistantSidePanelViewModel.cs`
- Create: `src/LocalScribe.App/Controls/AssistantSidePanel.xaml`
- Create: `src/LocalScribe.App/Controls/AssistantSidePanel.xaml.cs`
- Test: none beyond the VM's trivial surface (XAML is smoke-tested); add a small VM test file ONLY
  if the implementer adds logic beyond property plumbing.

**Interfaces:**
- Consumes: `AssistantTabViewModel` (unchanged), `AssistantChatThreadsViewModel` (Task 3),
  `AssistantChatPanel` (unchanged).
- Produces (Tasks 5/6 and Phase 3 rely on):
  - `AssistantSidePanelViewModel(AssistantTabViewModel? summary, AssistantChatThreadsViewModel threads)`
  - `AssistantTabViewModel? Summary { get; }`, `bool HasSummarySection => Summary is not null;`
  - `AssistantChatThreadsViewModel Threads { get; }`, `AssistantChatViewModel Chat => Threads.Chat;`
  - `[ObservableProperty] bool IsOpen` - the host binds its Ask toggle + column to this.
  - `[ObservableProperty] string CoverageText` - "" in session scope; Phase 3 forwards the matter
    coverage disclosure into it.
  - `Task LoadAsync(string? summarySessionId, CancellationToken ct)` - runs
    `Summary.LoadAsync(summarySessionId)` (when both non-null) then `Threads.LoadAsync`.

- [ ] **Step 1: Implement the VM** (`AssistantSidePanelViewModel.cs`):

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
namespace LocalScribe.App.ViewModels;

/// <summary>Composite state for the reusable Assistant side panel (addendum 2026-07-25): an
/// optional Summary section (session scope only - matter scope passes null and the Expander
/// collapses away), the thread-managed chat, the open/closed bit the host persists per window
/// family, and a coverage-text slot the matter host forwards its disclosure into. Deliberately
/// logic-free: every behavior lives on the wrapped VMs so both hosts stay identical.</summary>
public sealed partial class AssistantSidePanelViewModel : ObservableObject
{
    public AssistantSidePanelViewModel(AssistantTabViewModel? summary,
        AssistantChatThreadsViewModel threads)
        => (Summary, Threads) = (summary, threads);

    public AssistantTabViewModel? Summary { get; }
    public bool HasSummarySection => Summary is not null;
    public AssistantChatThreadsViewModel Threads { get; }
    public AssistantChatViewModel Chat => Threads.Chat;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _coverageText = "";

    public async Task LoadAsync(string? summarySessionId, CancellationToken ct)
    {
        if (Summary is not null && summarySessionId is not null)
            await Summary.LoadAsync(summarySessionId, ct);
        await Threads.LoadAsync(ct);
    }
}
```

- [ ] **Step 2: Implement the control XAML** (`AssistantSidePanel.xaml`). The Summary section is
  the Session Details Assistant tab's summary Card MOVED nearly verbatim (bindings retargeted
  `Assistant.*` -> `Summary.*`), wrapped in an Expander. The chat section is the existing
  `AssistantChatPanel` under the thread row:

```xml
<UserControl x:Class="LocalScribe.App.Controls.AssistantSidePanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:controls="clr-namespace:LocalScribe.App.Controls">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
    </UserControl.Resources>
    <DockPanel Margin="8,0,0,0">
        <!-- Summary (session scope only). An Expander, not a tab: the addendum's stacked layout
             keeps summary and chat visible together; collapsing to the one-line header (with the
             stale badge still visible) is the "I'm chatting now" state. -->
        <Expander DockPanel.Dock="Top" IsExpanded="True" Margin="0,0,0,8"
                  Visibility="{Binding HasSummarySection, Converter={StaticResource BoolToVis}}">
            <Expander.Header>
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="Summary" FontWeight="SemiBold" VerticalAlignment="Center" />
                    <Border Background="{DynamicResource SystemFillColorCautionBackgroundBrush}"
                            CornerRadius="8" Padding="6,1" Margin="8,0,0,0" VerticalAlignment="Center"
                            ToolTip="The transcript changed after this summary was generated."
                            Visibility="{Binding Summary.IsStale, Converter={StaticResource BoolToVis}}">
                        <TextBlock Text="stale" FontSize="11" />
                    </Border>
                </StackPanel>
            </Expander.Header>
            <!-- Body: the summary surface moved from SessionDetailsWindow's Assistant tab.
                 Bindings are Summary.* instead of Assistant.*; content otherwise unchanged
                 (explainer, version row, stale banner, waiting/running/error states, rendered
                 text with the LOCKED draft label). Copy each element from the removed tab
                 (Task 7 deletes the original) and retarget the binding paths. -->
            <ScrollViewer VerticalScrollBarVisibility="Auto" MaxHeight="320">
                <StackPanel>
                    <TextBlock Text="{Binding Summary.DisabledExplainer}" TextWrapping="Wrap"
                               Margin="0,0,0,8">
                        <TextBlock.Style>
                            <Style TargetType="TextBlock" BasedOn="{StaticResource WarningText}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding Summary.DisabledExplainer}" Value="">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </TextBlock.Style>
                    </TextBlock>
                    <StackPanel IsEnabled="{Binding Summary.AssistantAvailable}">
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
                            <ComboBox ItemsSource="{Binding Summary.Versions}"
                                      SelectedItem="{Binding Summary.SelectedVersion}"
                                      DisplayMemberPath="Id" MinWidth="110" />
                            <ui:Button Appearance="Primary" Content="Regenerate"
                                       Command="{Binding Summary.RegenerateCommand}"
                                       Margin="8,0,0,0" />
                        </StackPanel>
                        <Border Background="{DynamicResource SystemFillColorCautionBackgroundBrush}"
                                CornerRadius="4" Padding="8" Margin="0,6,0,0"
                                Visibility="{Binding Summary.IsStale, Converter={StaticResource BoolToVis}}">
                            <TextBlock Text="Stale - the transcript changed after this summary was generated. Regenerate to refresh; older versions stay available."
                                       TextWrapping="Wrap" />
                        </Border>
                        <TextBlock Text="{Binding Summary.WaitingText}" TextWrapping="Wrap" Margin="0,6,0,0">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource WarningText}">
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Summary.WaitingText}" Value="">
                                            <Setter Property="Visibility" Value="Collapsed" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                        <StackPanel Visibility="{Binding Summary.IsRunning, Converter={StaticResource BoolToVis}}">
                            <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                                <ProgressBar Width="100" Height="6" IsIndeterminate="True"
                                             VerticalAlignment="Center" Margin="0,0,8,0" />
                                <TextBlock Text="{Binding Summary.PhaseText}" VerticalAlignment="Center" />
                            </StackPanel>
                            <TextBlock Text="{Binding Summary.DraftLabel}" FontStyle="Italic"
                                       Style="{StaticResource Note}" Margin="0,4,0,0" />
                            <TextBox Text="{Binding Summary.StreamText, Mode=OneWay}" IsReadOnly="True"
                                     TextWrapping="Wrap" BorderThickness="0" MaxHeight="160"
                                     VerticalScrollBarVisibility="Auto" Margin="0,4,0,0" />
                        </StackPanel>
                        <TextBlock Text="{Binding Summary.ErrorText}" TextWrapping="Wrap" Margin="0,6,0,0">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource WarningText}">
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Summary.ErrorText}" Value="">
                                            <Setter Property="Visibility" Value="Collapsed" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                        <StackPanel Visibility="{Binding Summary.HasSummary, Converter={StaticResource BoolToVis}}"
                                    Margin="0,8,0,0">
                            <TextBlock Text="{Binding Summary.DraftLabel}" FontStyle="Italic"
                                       Style="{StaticResource Note}" />
                            <TextBlock Text="{Binding Summary.VersionInfo}"
                                       Style="{StaticResource MutedText}" Margin="0,2,0,6"
                                       TextWrapping="Wrap" />
                            <TextBox Text="{Binding Summary.ContentText, Mode=OneWay}" IsReadOnly="True"
                                     TextWrapping="Wrap" BorderThickness="0" />
                        </StackPanel>
                    </StackPanel>
                </StackPanel>
            </ScrollViewer>
        </Expander>

        <!-- Thread row: [dropdown][new][overflow]. Rename swaps the dropdown for an inline
             TextBox (IsRenaming) - one row either way, per the addendum's compact layout. -->
        <Grid DockPanel.Dock="Top" Margin="0,0,0,6">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <ComboBox Grid.Column="0" ItemsSource="{Binding Threads.Threads}"
                      SelectedItem="{Binding Threads.SelectedThread}"
                      DisplayMemberPath="Display">
                <ComboBox.Style>
                    <Style TargetType="ComboBox">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Threads.IsRenaming}" Value="True">
                                <Setter Property="Visibility" Value="Collapsed" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </ComboBox.Style>
            </ComboBox>
            <!-- Inline rename editor: Enter commits, Esc cancels (code-behind - KeyBindings in a
                 Style don't wire; the ReadViewWindow find-box precedent). -->
            <ui:TextBox Grid.Column="0" x:Name="RenameBox"
                        Text="{Binding Threads.RenameText, UpdateSourceTrigger=PropertyChanged}"
                        PreviewKeyDown="OnRenameBoxPreviewKeyDown"
                        Visibility="{Binding Threads.IsRenaming, Converter={StaticResource BoolToVis}}" />
            <ui:Button Grid.Column="1" Content="+" Margin="6,0,0,0" Padding="10,4"
                       ToolTip="New chat" Command="{Binding Threads.NewChatCommand}" />
            <ui:Button Grid.Column="2" x:Name="OverflowButton" Content="..." Margin="6,0,0,0"
                       Padding="10,4" ToolTip="Thread options" Click="OnOverflowClick">
                <ui:Button.ContextMenu>
                    <ContextMenu>
                        <MenuItem Header="Rename" Command="{Binding Threads.BeginRenameCommand}" />
                        <MenuItem Header="Archive" Command="{Binding Threads.ArchiveCommand}" />
                        <MenuItem Header="Unarchive" Command="{Binding Threads.UnarchiveCommand}" />
                        <Separator />
                        <MenuItem Header="Show archived" IsCheckable="True"
                                  IsChecked="{Binding Threads.ShowArchived}" />
                    </ContextMenu>
                </ui:Button.ContextMenu>
            </ui:Button>
        </Grid>

        <!-- Condense indicator (LOCKED: overflow degradation is surfaced, never silent). -->
        <TextBlock DockPanel.Dock="Top" Text="Earlier turns were condensed into a running recap."
                   FontSize="11" Opacity="0.7" Margin="0,0,0,4" TextWrapping="Wrap"
                   ToolTip="This thread outgrew the model's context window; its oldest turns were summarized into a recap the model still sees. The verbatim turns remain on disk."
                   Visibility="{Binding Threads.HasRecap, Converter={StaticResource BoolToVis}}" />

        <!-- Matter coverage disclosure slot (Phase 3 forwards CoverageText; "" collapses). -->
        <TextBlock DockPanel.Dock="Top" Text="{Binding CoverageText}" TextWrapping="Wrap"
                   FontSize="12" Margin="0,0,0,6">
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding CoverageText}" Value="">
                            <Setter Property="Visibility" Value="Collapsed" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>

        <!-- The existing chat surface, unchanged (turn history, streaming, chips, input row). -->
        <controls:AssistantChatPanel DataContext="{Binding Chat}" />
    </DockPanel>
</UserControl>
```

- [ ] **Step 3: Implement the code-behind** (`AssistantSidePanel.xaml.cs`):

```csharp
using System.Windows;
using System.Windows.Controls;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App.Controls;

/// <summary>The reusable Assistant side panel (addendum 2026-07-25). Code-behind exists only for
/// the two things bindings cannot do: opening the overflow ContextMenu from a left-click, and the
/// rename box's Enter/Esc keys (KeyBindings outside the visual tree don't resolve - the
/// ReadViewWindow find-box precedent).</summary>
public partial class AssistantSidePanel : UserControl
{
    public AssistantSidePanel() => InitializeComponent();

    private AssistantSidePanelViewModel? Vm => DataContext as AssistantSidePanelViewModel;

    private void OnOverflowClick(object sender, RoutedEventArgs e)
    {
        if (OverflowButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = OverflowButton;
        menu.DataContext = DataContext;   // ContextMenu is not in the visual tree - inherit manually
        menu.IsOpen = true;
    }

    private void OnRenameBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == System.Windows.Input.Key.Enter)
        { Vm.Threads.CommitRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape)
        { Vm.Threads.CancelRenameCommand.Execute(null); e.Handled = true; }
    }
}
```

- [ ] **Step 4: Build to verify 0 warnings / 0 errors**

Run: `dotnet build LocalScribe.slnx 2>&1 | tail -4`
Expected: `0 Warning(s)`, `0 Error(s)`. (No new tests - the control is exercised by the Task 8
runbook; the VM is plumbing.)

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/AssistantSidePanelViewModel.cs src/LocalScribe.App/Controls/AssistantSidePanel.xaml src/LocalScribe.App/Controls/AssistantSidePanel.xaml.cs
git commit -m "feat(panel): AssistantSidePanel control + composite VM (summary expander, thread row, chat)"
```

---

### Task 5: Host the panel in ReadViewWindow (toggle, splitter, persistence, reflow)

**Files:**
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml` (header WrapPanel ~line 108; transcript Grid
  ~line 303)
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml.cs`

**Interfaces:**
- Consumes: `AssistantSidePanelViewModel` (Task 4), `WindowStateStore.Load/SaveAssistantPanel`
  (Task 1, key `"readView"`).
- Produces (Task 6 relies on): `ReadViewWindow` ctor gains a trailing parameter
  `AssistantSidePanelViewModel panelVm`; a public `AssistantSidePanelViewModel Panel { get; }`;
  the window calls `Panel.LoadAsync(sessionId, ct)` in its Loaded handler AFTER `_vm.LoadAsync`.

**Behavior (addendum, binding):** default width 400, min 280, max 60% of window width; open state:
explicit saved choice wins, else open iff `Panel.Summary?.HasSummary == true ||
Panel.Threads.HasAnyHistory`; the open bit + width save to key `"readView"` on the LAST closed
read view (piggyback the existing `OpenCount == 0` placement block) and ONLY once a state exists
or the user explicitly toggled; transcript column is star-sized so reflow is automatic (the
ListView already disables horizontal scroll and wraps).

- [ ] **Step 1: XAML - Ask toggle.** In the header `WrapPanel`, after the "Manage speakers..."
  button (line ~109), add:

```xml
                <ToggleButton Content="Ask" Margin="0,0,8,4"
                              ToolTip="Ask the assistant about this session (summary + chat)"
                              IsChecked="{Binding Panel.IsOpen, ElementName=Self, Mode=TwoWay}"
                              Click="OnAskToggleClick" />
```

- [ ] **Step 2: XAML - panel column.** Replace the bare `<Grid>` opening tag at ~line 303 (the one
  holding RowList/EditList) and its closing `</Grid>` (~line 525) with a 3-column outer grid. The
  existing transcript Grid moves intact into column 0:

```xml
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" MinWidth="200" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition x:Name="PanelColumn" Width="0" />
            </Grid.ColumnDefinitions>
            <Grid Grid.Column="0">
                <!-- ... RowList and EditList exactly as before ... -->
            </Grid>
            <GridSplitter Grid.Column="1" Width="6" HorizontalAlignment="Stretch"
                          VerticalAlignment="Stretch" ResizeBehavior="PreviousAndNext"
                          Background="{DynamicResource ControlFillColorSecondaryBrush}"
                          Visibility="{Binding Panel.IsOpen, ElementName=Self, Converter={StaticResource BoolToVis}}" />
            <controls:AssistantSidePanel Grid.Column="2"
                          DataContext="{Binding Panel, ElementName=Self}"
                          Visibility="{Binding Panel.IsOpen, ElementName=Self, Converter={StaticResource BoolToVis}}" />
        </Grid>
```

Add `xmlns:controls="clr-namespace:LocalScribe.App.Controls"` to the window element.

- [ ] **Step 3: Code-behind.** In `ReadViewWindow.xaml.cs`:

Add fields + the public property:

```csharp
    /// <summary>Panel state for THIS window; XAML binds via ElementName=Self (an ObservableObject,
    /// so path updates propagate). Constructed by the openReadView composition (Task 6).</summary>
    public AssistantSidePanelViewModel Panel { get; }
    /// <summary>True once the user has clicked the Ask toggle in ANY read view this app run OR a
    /// persisted assistantPanel entry already existed: only then does OnClosed write the state.
    /// Before any explicit choice the heuristic must keep deciding (addendum precedence rule).</summary>
    private bool _panelChoiceIsExplicit;
    private const string PanelKey = "readView";
    private const double PanelDefaultWidth = 400;
    private const double PanelMinWidth = 280;
```

Widen the ctor signature (trailing param): `..., Action<string> openSessionDetails,
AssistantSidePanelViewModel panelVm)`; assign `Panel = panelVm;` FIRST in the ctor body (before
`InitializeComponent()` - the XAML ElementName bindings resolve at InitializeComponent, and Panel
must be non-null then). Subscribe `Panel.PropertyChanged += OnPanelPropertyChanged;` after
`InitializeComponent()`.

In the existing `Loaded` handler, after `await _vm.LoadAsync(...)` add:

```csharp
            await Panel.LoadAsync(_sessionId, CancellationToken.None);
            var savedPanel = _stateStore.LoadAssistantPanel(PanelKey);
            _panelChoiceIsExplicit = savedPanel is not null;
            ApplyPanelWidth(savedPanel?.Width ?? PanelDefaultWidth);
            // Explicit persisted choice wins; the heuristic (open iff the scope already has a
            // summary or chat history) applies only while no choice was ever recorded.
            Panel.IsOpen = savedPanel?.Open
                ?? (Panel.Summary?.HasSummary == true || Panel.Threads.HasAnyHistory);
```

Add the handlers:

```csharp
    private double _panelWidth = PanelDefaultWidth;

    private void ApplyPanelWidth(double width)
        => _panelWidth = Math.Max(PanelMinWidth, Math.Min(width, ActualWidth * 0.6));

    private void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssistantSidePanelViewModel.IsOpen)) return;
        if (Panel.IsOpen)
        {
            PanelColumn.Width = new GridLength(_panelWidth);
            PanelColumn.MinWidth = PanelMinWidth;
            PanelColumn.MaxWidth = Math.Max(PanelMinWidth, ActualWidth * 0.6);
        }
        else
        {
            if (PanelColumn.Width.Value > 0) _panelWidth = PanelColumn.Width.Value;
            PanelColumn.MinWidth = 0;
            PanelColumn.Width = new GridLength(0);
        }
    }

    /// <summary>An actual user click on the Ask toggle (not the heuristic) makes the choice
    /// explicit - from now on it persists and the heuristic stops deciding.</summary>
    private void OnAskToggleClick(object sender, RoutedEventArgs e) => _panelChoiceIsExplicit = true;
```

In `OnClosed`, inside the existing `if (_registry.OpenCount == 0)` block, after the placement save:

```csharp
        {
            _stateStore.Save("readViewDefault", new WindowPlacement(Left, Top, Width, Height));
            if (_panelChoiceIsExplicit)
                _stateStore.SaveAssistantPanel(PanelKey, new AssistantPanelState(Panel.IsOpen,
                    Panel.IsOpen ? PanelColumn.Width.Value : _panelWidth));
        }
```

Also unsubscribe `Panel.PropertyChanged -= OnPanelPropertyChanged;` in `OnClosed` (house rule).

NOTE for the implementer: this task does NOT compile alone - the ctor gained a parameter and
`App.xaml.cs` still calls the old signature. Task 6 is the other half; build/commit them together
if the intermediate build breaks, OR give the new ctor parameter a temporary default of null with
a null-tolerant `Panel` guard and remove it in Task 6. Preferred: implement Tasks 5+6 as two
commits on one build (commit XAML+code-behind first only if it compiles via a default parameter).

- [ ] **Step 4: Build** (jointly with Task 6 if the default-parameter route is not taken)

Run: `dotnet build LocalScribe.slnx 2>&1 | tail -4`
Expected: 0/0 once Task 6 lands.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/ReadViewWindow.xaml.cs
git commit -m "feat(readview): host AssistantSidePanel - Ask toggle, splitter column, per-family persistence"
```

---

### Task 6: Move the chat lifecycle into openReadView (App.xaml.cs) + citation short-circuit

**Files:**
- Modify: `src/LocalScribe.App/App.xaml.cs` (openSessionDetails ~lines 336-417; openReadView
  ~lines 422-451)

**Interfaces:**
- Consumes: everything above; the existing `chatServiceFactory` pattern (lines 375-387), 
  `assistantBusyReason`, `qaScopeFactoryFor`, `acquireAssistantLease`, `navigateToCitation`.
- Produces: `openReadView` constructs and owns the full assistant stack per read view; 
  `openSessionDetails` no longer constructs ANY assistant object.

- [ ] **Step 1: Move the wiring.** Inside `openReadView` (after `readVm` is built, before the
  window), add - this is the openSessionDetails block relocated, plus the threads/panel VMs and
  the citation short-circuit:

```csharp
            // Phase 2 (addendum 2026-07-25): the session assistant stack moved here from
            // openSessionDetails - summary + chat now live in the read view's side panel. The
            // scope reloads the projection PER QUESTION through the same per-session gate every
            // reader uses; the warm session is reused while that context is byte-identical.
            var assistantTab = new ViewModels.AssistantTabViewModel(comp.Summarizer, comp.Summaries,
                comp.AssistantModels, comp.Settings, errors, dispatch);
            var chatStore = new LocalScribe.Core.Assistant.AssistantChatStore(comp.Paths.SessionChatsJson(sessionId));
            Func<LocalScribe.Core.Assistant.AssistantQaService?> chatServiceFactory = () =>
                qaScopeFactoryFor() is { } scopes
                    ? new LocalScribe.Core.Assistant.AssistantQaService(comp.AssistantChat, chatStore,
                        acquireAssistantLease,
                        (question, ct) => scopes.ForSessionAsync(sessionId,
                            inner => comp.Maintenance.RunForSessionAsync(sessionId, async gated =>
                                (IReadOnlyList<LocalScribe.Core.Projection.DisplayRow>)
                                (await LocalScribe.Core.Storage.SessionProjectionLoader.LoadAsync(
                                    comp.Paths, comp.Settings.Current, TimeProvider.System, sessionId, gated)).Rows,
                                inner),
                            question, ct),
                        TimeProvider.System)
                    : null;
            var chatVm = new ViewModels.AssistantChatViewModel(chatServiceFactory, chatStore, errors, dispatch, assistantBusyReason);
            var threadsVm = new ViewModels.AssistantChatThreadsViewModel(chatVm, chatStore, errors,
                dispatch, TimeProvider.System);
            var panelVm = new ViewModels.AssistantSidePanelViewModel(assistantTab, threadsVm);
            Action<string> chatInvalidate = id => { if (id == sessionId) chatVm.InvalidateContext(); };
            comp.Maintenance.SessionContentChanged += chatInvalidate;
            Action<LocalScribe.Core.Live.SessionState> chatRecordingPreempt = s =>
            { if (s != LocalScribe.Core.Live.SessionState.Idle) chatVm.CancelForRecording(); };
            comp.Controller.StateChanged += chatRecordingPreempt;
```

Widen the window construction:

```csharp
            var window = new ReadViewWindow(readVm, sessionId, comp.Windows, windowState,
                comp.Settings, openSplitSpeakers, openSessionDetails, panelVm);
```

After `readViews[sessionId] = window;`, wire the citation short-circuit (needs `window`):

```csharp
            // Citation short-circuit (addendum): a chip for THIS session scrolls THIS window via
            // ShowFindAt - never a second read view. Foreign-session chips (possible on history
            // rendered from a matter-scope answer) keep the global open+target route.
            chatVm.CitationNavigationRequested += (sid, seq, term) =>
            {
                if (sid == sessionId) { if (seq >= 0) window.ShowFindAt(seq, term); }
                else navigateToCitation?.Invoke(sid, seq, term);
            };
```

Extend the existing `window.Closed` handler:

```csharp
            window.Closed += (_, _) =>
            {
                readViews.Remove(sessionId);
                comp.Maintenance.SessionContentChanged -= chatInvalidate;
                comp.Controller.StateChanged -= chatRecordingPreempt;
                chatVm.Shutdown();                          // warm-helper teardown on chat close (design 7.1)
                readVm.Dispose();
            };
```

- [ ] **Step 1b: Retarget the matter Generate route.** The matters Assistant-tab factory wires
  `vm.SummaryGenerationRequested += openSessionDetails` (~line 622) - that lands on the Session
  Details Assistant tab, which THIS phase removes, so the route would dead-end. Phase-2-safe
  retarget (Phase 3 upgrades it to auto-regenerate):

```csharp
            // Generation route (Phase 2 interim): the Session Details Assistant tab is gone, so
            // land on the read view - its side panel carries the Regenerate CTA. Phase 3 upgrades
            // this to open-and-regenerate in one step.
            vm.SummaryGenerationRequested += openReadView;
```

- [ ] **Step 2: Strip openSessionDetails.** Remove from the `openSessionDetails` factory: the
  `assistantTab` construction (lines ~343-344), the `assistant: assistantTab` ctor argument (the
  `MetadataEditorViewModel` call keeps its other args - Task 7 changes the VM signature), the
  whole chat block (chatStore, chatServiceFactory, chatVm, `chatVm.CitationNavigationRequested`,
  `chatInvalidate` + subscription, `chatRecordingPreempt` + subscription, `detailEditor.Chat =`,
  `LoadHistoryAsync` - lines ~370-401), and in its `Closed` handler the two unsubscribes +
  `chatVm.Shutdown()` (lines ~410-412). Keep everything else (DiariseRequested, Saved wiring,
  Dispose, RefreshRowAsync backstop).

- [ ] **Step 3: Build**

Run: `dotnet build LocalScribe.slnx 2>&1 | tail -4`
Expected: 0 Warning(s) / 0 Error(s) IF Task 7's MetadataEditorViewModel change is folded here for
the ctor argument; otherwise leave `assistant: null` temporarily and let Task 7 remove the
parameter. Prefer `assistant: null` here so Tasks 6 and 7 stay independently committable.

- [ ] **Step 4: Run the full App test suite** (composition changes have wide blast radius)

Run: `dotnet test tests/LocalScribe.App.Tests 2>&1 | tail -4`
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/App.xaml.cs
git commit -m "feat(readview): relocate session assistant lifecycle from Session Details to the read view"
```

---

### Task 7: Remove the Session Details Assistant tab

**Files:**
- Modify: `src/LocalScribe.App/SessionDetailsWindow.xaml` (TabItem ~lines 258-360)
- Modify: `src/LocalScribe.App/ViewModels/MetadataEditorViewModel.cs` (Assistant ~line 176, Chat
  ~line 182, ctor param ~line 186/190, LoadAsync call ~line 336)
- Test: `tests/LocalScribe.App.Tests/MetadataEditorLoadAsyncTests.cs` and any other
  MetadataEditor*/SessionDetails* test that passes `assistant:` or touches `.Assistant`/`.Chat`
  (grep first: `grep -rn "assistant:" tests/ ; grep -rn "\.Assistant\b\|\.Chat\b" tests/LocalScribe.App.Tests/MetadataEditor*`)

- [ ] **Step 1: XAML.** Delete the whole `<TabItem Header="Assistant">...</TabItem>` block
  including its leading comment (lines ~258-360). Then check whether `xmlns:controls` is still
  used elsewhere in the file (`grep -n "controls:" src/LocalScribe.App/SessionDetailsWindow.xaml`);
  if not, remove the namespace declaration too (0-warning gate does not flag unused xmlns, but
  house hygiene does).

- [ ] **Step 2: VM.** In `MetadataEditorViewModel.cs`: delete the `Assistant` property + doc
  comment, the `Chat` property + doc comment, the `assistant` ctor parameter and its assignment,
  and the `if (Assistant is not null) await Assistant.LoadAsync(sessionId, ct);` line in
  `LoadAsync`. In `App.xaml.cs` remove the now-dangling `assistant: null` argument from Task 6.

- [ ] **Step 3: Fix tests.** Update any test constructing `MetadataEditorViewModel` with an
  `assistant:` argument or asserting on `.Assistant`/`.Chat`. Delete assertions that only checked
  the pass-through (the tab is gone); keep `AssistantTabViewModelTests` untouched (the VM lives on
  in the panel).

- [ ] **Step 4: Build + full test run**

Run: `dotnet build LocalScribe.slnx 2>&1 | tail -4 && dotnet test tests/LocalScribe.App.Tests 2>&1 | tail -4`
Expected: 0/0 and all green.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/SessionDetailsWindow.xaml src/LocalScribe.App/ViewModels/MetadataEditorViewModel.cs src/LocalScribe.App/App.xaml.cs tests/
git commit -m "feat(details): remove the Session Details Assistant tab (summary + chat live in the read view)"
```

---

### Task 8: Phase gate + smoke runbook

**Files:**
- Create: `docs/plans/2026-07-25-assistant-panel-smoke-runbook.md`

- [ ] **Step 1: Full gate**

Run: `dotnet build LocalScribe.slnx 2>&1 | tail -4`
Expected: 0/0.
Run: `dotnet test tests/LocalScribe.Core.Tests 2>&1 | tail -4`
Expected: only the 2 known fixture fails.
Run: `dotnet test tests/LocalScribe.App.Tests 2>&1 | tail -4`
Expected: all green.

- [ ] **Step 2: Write the smoke runbook** (for the USER to execute - Claude never runs GUI
  smokes). Cover, as numbered steps with expected results:
  P2-1 Ask toggle opens/closes the panel; transcript text reflows live (no horizontal scroll).
  P2-2 Splitter drag resizes; width respects min 280 / max 60%; reopening the app restores
       open-state + width after an explicit toggle (window-state.json shows assistantPanel).
  P2-3 Heuristic: a session with no summary/chat opens with the panel closed; one WITH history
       opens with it open (before any explicit toggle - delete assistantPanel from
       window-state.json to reset).
  P2-4 Summary expander: versions switch, Regenerate streams with the draft label, stale badge
       shows on both header and body.
  P2-5 Threads: New creates "Chat N"; inline Rename (Enter commits, Esc cancels); Archive hides;
       Show archived reveals "(archived)" read-only (Ask disabled); Unarchive restores.
  P2-6 Thread switch is instant (no transcript reload - watch for no model re-prime on the next
       ask in the same scope).
  P2-7 Citation chip click scrolls THIS window's transcript (find bar opens on the term); no
       second read view appears.
  P2-8 Recording start cancels an in-flight answer (nothing persisted, question kept).
  P2-9 Session Details no longer has an Assistant tab; Details/Speakers/Matters still work.
  P2-10 A long thread triggers a condense: the "Earlier turns were condensed" indicator appears
       and chats.json's thread gains a Recap.

- [ ] **Step 3: Commit**

```bash
git add docs/plans/2026-07-25-assistant-panel-smoke-runbook.md
git commit -m "docs(smoke): Phase 2 assistant side panel smoke runbook"
```
