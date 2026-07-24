# Matters Assistant Panel - Phase 3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mirror the Phase 2 assistant panel (Chat-only) in the Matters detail, remove the matter
Assistant tab, add a Summary column to the matter's Sessions tab, and fix the tab strip to a
single non-wrapping row.

**Architecture:** `MattersPage`'s detail column becomes a 3-column grid hosting the SAME
`AssistantSidePanel` (null Summary section). `MatterAssistantViewModel` wraps its chat in an
`AssistantChatThreadsViewModel` + `AssistantSidePanelViewModel` and forwards its coverage
disclosure into the panel's `CoverageText` slot. Summary status for tagged sessions comes from a
shared `SummaryStatus` provider seam reading `SummaryStore` (also used by Phase 4). The
generation route becomes open-read-view-and-regenerate via a new `ReadViewWindow` entry point.

**Tech Stack:** .NET 10 WPF, WPF-UI, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-24-assistant-chat-surfaces-design.md` - Phase 3 entries
plus the 2026-07-25 addendum (binding). **Prerequisite: Phase 2 is merged** (`AssistantSidePanel`,
`AssistantChatThreadsViewModel`, `AssistantSidePanelViewModel`, `WindowStateStore.
Load/SaveAssistantPanel` all exist).

## Global Constraints

Same as the Phase 2 plan (no emojis; file-scoped namespaces; /// = WHY; 0-warn build; Core green
except 2 known fixture fails; App green; evidentiary rules; one warm helper; bridges kept; theme
resources only; WPF-free VMs). Branch: `feat/assistant-surfaces-phase3` off master (after Phase 2
merges), dedicated worktree, never push. Close LocalScribe.App.exe before building.

## File Structure

- Create: `src/LocalScribe.App/ViewModels/SummaryStatus.cs` - shared enum + provider delegate.
- Modify: `src/LocalScribe.App/ViewModels/MatterAssistantViewModel.cs` - grow Threads/Panel.
- Modify: `src/LocalScribe.App/ViewModels/MattersPageViewModel.cs` - TaggedSessionItem status,
  panel-state seam, provider seam.
- Modify: `src/LocalScribe.App/Pages/MattersPage.xaml(.cs)` - panel column, Ask toggle, tab strip,
  remove Assistant tab, Summary column.
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml.cs` - `ShowAssistantSummary(bool regenerate)`.
- Modify: `src/LocalScribe.App/App.xaml.cs` - provider + panel-state wiring, generation route.
- Tests: `tests/LocalScribe.App.Tests/MatterAssistantViewModelTests.cs` (extend),
  `MattersPageViewModelTests.cs` (extend).

---

### Task 1: SummaryStatus enum + provider seam

**Files:**
- Create: `src/LocalScribe.App/ViewModels/SummaryStatus.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs`

**Interfaces:**
- Produces (Tasks 3/5 here and Phase 4 rely on these EXACT names):

```csharp
namespace LocalScribe.App.ViewModels;

/// <summary>A session's summary standing for the overview columns (design Phase 3/4): None =
/// never generated, Done = latest version current, Stale = latest version predates a transcript
/// change. Presentation-only - the truth lives in SummaryStore (latest version + Stale flag).</summary>
public enum SummaryStatus { None, Done, Stale }

/// <summary>Reads one session's summary standing. A seam (not a service class) so page VMs stay
/// WPF-free and tests can stub it without a store; the App composition binds it to the single
/// composed SummaryStore instance (comp.Summaries - never a second store, house rule).</summary>
public delegate Task<SummaryStatus> SummaryStatusProvider(string sessionId, CancellationToken ct);
```

- [ ] **Step 1: Create the file** with the content above.

- [ ] **Step 2: Bind it in App.xaml.cs** - add near the matters wiring (before
  `mattersVm.AssistantFactory`), one instance reused by Phase 4 later:

```csharp
        // Summary-status provider (design Phases 3-4): one JSON read per session via the single
        // composed SummaryStore. Callers run it in background stamping passes - never on the UI
        // thread, never blocking a scan.
        ViewModels.SummaryStatusProvider summaryStatusFor = async (sid, ct) =>
        {
            var versions = await comp.Summaries.LoadAsync(sid, ct);
            var latest = versions.Count > 0 ? versions[^1] : null;
            return latest is null ? ViewModels.SummaryStatus.None
                : latest.Stale ? ViewModels.SummaryStatus.Stale : ViewModels.SummaryStatus.Done;
        };
```

- [ ] **Step 3: Build** - `dotnet build LocalScribe.slnx 2>&1 | tail -4` - expect 0/0 (the local
  is briefly unused; if the 0-warning gate flags CS0219/unused-local, wire Task 3's consumption
  first or suppress by assigning it to the VM seam in this same commit - preferred: do Step 2 of
  Task 3 (the seam assignment) in this commit).

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SummaryStatus.cs src/LocalScribe.App/App.xaml.cs
git commit -m "feat(summary): SummaryStatus enum + provider seam over the composed SummaryStore"
```

---

### Task 2: MatterAssistantViewModel grows Threads + Panel + coverage forwarding

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/MatterAssistantViewModel.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs` (AssistantFactory - pass TimeProvider)
- Test: `tests/LocalScribe.App.Tests/MatterAssistantViewModelTests.cs`

**Interfaces:**
- Consumes: `AssistantChatThreadsViewModel`, `AssistantSidePanelViewModel` (Phase 2).
- Produces:
  - `AssistantChatThreadsViewModel Threads { get; }` (wraps the existing `Chat`)
  - `AssistantSidePanelViewModel Panel { get; }` (summary: null - matter scope is Chat-only)
  - Ctor gains a trailing `TimeProvider time` parameter (before the optional busyReason):
    `MatterAssistantViewModel(string matterId, ..., IUiErrorReporter reporter,
    Action<Action> dispatch, TimeProvider time, Func<string?>? busyReason = null)`
  - Coverage forwarding: `UpdateCoverage` also sets `Panel.CoverageText` (the panel's disclosure
    slot); the old `CoverageText` property REMAINS (tests bind it) and the two stay equal.

- [ ] **Step 1: Write the failing test** (append to `MatterAssistantViewModelTests.cs`, reusing
  its existing fakes/turn builders):

```csharp
[Fact]
public void Coverage_forwards_into_panel_slot()
{
    var vm = CreateVm();              // the file's existing factory helper, adjusted for the new ctor
    RaiseTurnCompleted(vm);           // however existing coverage tests feed a turn - reuse that idiom
    Assert.Equal(vm.CoverageText, vm.Panel.CoverageText);
    Assert.NotEqual("", vm.Panel.CoverageText);
}

[Fact]
public void Panel_is_chat_only()
{
    var vm = CreateVm();
    Assert.Null(vm.Panel.Summary);
    Assert.Same(vm.Chat, vm.Panel.Chat);
}
```

- [ ] **Step 2: Run to verify failure** - compile error (`Threads`/`Panel` undefined).

- [ ] **Step 3: Implement.** In the ctor, after `Chat = ...`:

```csharp
        Threads = new AssistantChatThreadsViewModel(Chat, store, reporter, dispatch, time);
        Panel = new AssistantSidePanelViewModel(summary: null, Threads);
```

Properties beside `Chat`:

```csharp
    /// <summary>Thread management around Chat (Phase 3 mirrors the session panel identically -
    /// learn once). The panel is Chat-only: matter summaries render as a status COLUMN on the
    /// Sessions tab now, not inside the panel.</summary>
    public AssistantChatThreadsViewModel Threads { get; }
    public AssistantSidePanelViewModel Panel { get; }
```

At the end of `UpdateCoverage`'s dispatch: `_dispatch(() => { CoverageText = ...; Panel.CoverageText = CoverageText; });`

Update `App.xaml.cs` `AssistantFactory` construction to pass `TimeProvider.System` for the new
parameter. Update every test construction site for the widened ctor.

- [ ] **Step 4: Run** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MatterAssistantViewModelTests" 2>&1 | tail -5` - PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/MatterAssistantViewModel.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/MatterAssistantViewModelTests.cs
git commit -m "feat(matters): MatterAssistantViewModel grows thread management + chat-only side panel"
```

---

### Task 3: TaggedSessionItem summary status + background stamping pass

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/MattersPageViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs`

**Interfaces:**
- Consumes: `SummaryStatusProvider` (Task 1).
- Produces:
  - `TaggedSessionItem` becomes a partial ObservableObject CLASS (was a record): same readonly
    props (`SessionId`, `Title`, `DateDisplay`, `DurationDisplay`, `IsPendingRecovery`) plus
    `[ObservableProperty] SummaryStatus? _summaryStatus` (null = not yet probed - renders blank,
    never a false "None").
  - `MattersPageViewModel.SummaryStatusProvider` settable seam (the `AssistantFactory` precedent):
    `public SummaryStatusProvider? SummaryStatusProvider { get; set; }`
  - `public event Action<string, bool>? OpenSummaryRequested;` - (sessionId, regenerate) raised by
    the Summary column's chip (regenerate=false) / Generate (regenerate=true) actions.
  - `public IRelayCommand<TaggedSessionItem> OpenSummaryCommand { get; }` and
    `public IRelayCommand<TaggedSessionItem> GenerateSummaryCommand { get; }` raising that event.

- [ ] **Step 1: Write the failing tests:**

```csharp
// 1. Tagged_rows_stamp_summary_status: select a matter with 2 tagged sessions; provider returns
//    Done for one id, Stale for the other; after SelectAsync (+ letting the stamping pass run -
//    the seam is awaited inside SelectAsync's flow, so awaiting SelectAsync suffices with the
//    synchronous test dispatch), both rows' SummaryStatus match.
// 2. Provider_absent_leaves_status_null: no seam set -> SummaryStatus stays null (blank cell).
// 3. Provider_fault_leaves_status_null_and_reports_nothing_fatal: provider throws -> rows keep
//    null, no exception escapes SelectAsync.
// 4. Generate_and_open_raise_event: OpenSummaryCommand raises (id, false); GenerateSummaryCommand
//    raises (id, true).
```

Write all four as real tests with real asserts against the file's existing fake-maintenance
fixtures.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement.**

Convert the record (keep the doc comment, add WHY):

```csharp
/// <summary>One tagged-session row. A class with ONE mutable slot (the ContentSnippet precedent,
/// SessionRowViewModel): SummaryStatus is stamped by a background pass AFTER the rows render, so
/// the tagged list never waits on N summaries.json reads; null renders blank (unknown), never a
/// false "no summary" claim.</summary>
public sealed partial class TaggedSessionItem : ObservableObject
{
    public TaggedSessionItem(string sessionId, string title, string dateDisplay,
        string durationDisplay, bool isPendingRecovery)
        => (SessionId, Title, DateDisplay, DurationDisplay, IsPendingRecovery)
            = (sessionId, title, dateDisplay, durationDisplay, isPendingRecovery);

    public string SessionId { get; }
    public string Title { get; }
    public string DateDisplay { get; }
    public string DurationDisplay { get; }
    public bool IsPendingRecovery { get; }
    [ObservableProperty] private SummaryStatus? _summaryStatus;
}
```

Fix the construction site in `SelectAsync` (positional `new TaggedSessionItem(...)` still
compiles). In `SelectAsync`, after `_taggedAll` is built and dispatched, kick the stamping pass:

```csharp
        _ = StampSummaryStatusAsync(_taggedAll, CancellationToken.None);
```

```csharp
    /// <summary>Background stamping (ContentSnippet precedent): one provider read per row, results
    /// marshalled per-row so early rows light up while later ones still read. Faults leave null
    /// (blank cell) - a status column must never invent a state it could not read.</summary>
    private async Task StampSummaryStatusAsync(IReadOnlyList<TaggedSessionItem> rows, CancellationToken ct)
    {
        if (SummaryStatusProvider is not { } provider) return;
        foreach (var row in rows)
        {
            try
            {
                var status = await provider(row.SessionId, ct);
                _dispatch(() => row.SummaryStatus = status);
            }
            catch { /* unknown stays blank; the read view remains the truth surface */ }
        }
    }
```

Add the seam property, the event, and the two commands (ctor-constructed like the file's other
commands):

```csharp
    public SummaryStatusProvider? SummaryStatusProvider { get; set; }
    /// <summary>(sessionId, regenerate): the Summary column's click-throughs. Routed by the App
    /// composition to the read view's assistant panel (the generation surface after Phase 2).</summary>
    public event Action<string, bool>? OpenSummaryRequested;
    public IRelayCommand<TaggedSessionItem> OpenSummaryCommand { get; }
    public IRelayCommand<TaggedSessionItem> GenerateSummaryCommand { get; }
```

```csharp
        OpenSummaryCommand = new RelayCommand<TaggedSessionItem>(r =>
        { if (r is not null) OpenSummaryRequested?.Invoke(r.SessionId, false); });
        GenerateSummaryCommand = new RelayCommand<TaggedSessionItem>(r =>
        { if (r is not null) OpenSummaryRequested?.Invoke(r.SessionId, true); });
```

Then in `App.xaml.cs`: `mattersVm.SummaryStatusProvider = summaryStatusFor;` (beside the
AssistantFactory assignment). Re-stamp on summary-affecting refreshes is NOT wired here - the row
list rebuilds (and re-stamps) on every `SelectAsync`, which every tag/untag/refresh path already
calls; that is sufficient freshness for a scan column.

- [ ] **Step 4: Run the tests** - PASS; run the full MattersPage suites too
  (`--filter "FullyQualifiedName~MattersPage"`).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/MattersPageViewModel.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs
git commit -m "feat(matters): tagged-session SummaryStatus stamping + open/generate summary events"
```

---

### Task 4: ReadViewWindow.ShowAssistantSummary + generation route

**Files:**
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs`

**Interfaces:**
- Consumes: Phase 2's `Panel` on ReadViewWindow, `_pendingFindTarget` stash pattern.
- Produces: `public void ShowAssistantSummary(bool regenerate)` - opens the panel (programmatic,
  does NOT mark the choice explicit) and, when regenerate, fires `Panel.Summary.RegenerateCommand`
  once loaded; callable before Loaded finished (stash-and-apply, the ShowFindAt precedent).
  In App.xaml.cs: `Action<string, bool> openSessionSummary` composing openReadView + this method;
  `mattersVm` wiring: `OpenSummaryRequested += openSessionSummary` and the Phase 2 interim
  `vm.SummaryGenerationRequested += openReadView` upgraded to
  `vm.SummaryGenerationRequested += sid => openSessionSummary(sid, true);`.

- [ ] **Step 1: Implement the window method** (mirror `_pendingFindTarget`):

```csharp
    private bool? _pendingSummaryRegenerate;

    /// <summary>Summary-column click-through (Phase 3/4): open the panel on this window - a
    /// PROGRAMMATIC open, so it never counts as the user's explicit choice - and optionally start
    /// a regeneration. Callable before the initial load; stashed and applied after (the
    /// ShowFindAt precedent).</summary>
    public void ShowAssistantSummary(bool regenerate)
    {
        if (!_vm.IsLoaded) { _pendingSummaryRegenerate = regenerate; return; }
        ApplySummaryAction(regenerate);
    }

    private void ApplySummaryAction(bool regenerate)
    {
        Panel.IsOpen = true;
        if (regenerate && Panel.Summary is { } summary && summary.RegenerateCommand.CanExecute(null))
            summary.RegenerateCommand.Execute(null);
    }
```

In the Loaded handler, after the Phase 2 panel-restore block:

```csharp
            if (_pendingSummaryRegenerate is { } regen)
            {
                ApplySummaryAction(regen);
                _pendingSummaryRegenerate = null;
            }
```

- [ ] **Step 2: Compose the route in App.xaml.cs** (after `navigateToCitation` is assigned):

```csharp
        // Summary-column click-through (Phases 3-4): open or activate the session's read view and
        // land on its assistant panel; regenerate=true also starts a generation there (the only
        // generation surface since the Session Details Assistant tab was removed).
        Action<string, bool> openSessionSummary = (sessionId, regenerate) =>
        {
            openReadView(sessionId);
            if (readViews.TryGetValue(sessionId, out var window))
                window.ShowAssistantSummary(regenerate);
        };
        mattersVm.OpenSummaryRequested += (sid, regen) => openSessionSummary(sid, regen);
```

And inside `AssistantFactory`, replace the Phase 2 interim line:

```csharp
            vm.SummaryGenerationRequested += sid => openSessionSummary(sid, true);
```

(`openSessionSummary` must be declared before the `AssistantFactory` assignment - both live after
`openReadView`, so ordering works; verify with the compiler.)

- [ ] **Step 3: Build + full App tests** - 0/0 and green.

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.App/ReadViewWindow.xaml.cs src/LocalScribe.App/App.xaml.cs
git commit -m "feat(readview): ShowAssistantSummary open-and-regenerate route for summary columns"
```

---

### Task 5: MattersPage XAML - panel column, Ask toggle, Summary column, tab strip, remove tab

**Files:**
- Modify: `src/LocalScribe.App/Pages/MattersPage.xaml`
- Modify: `src/LocalScribe.App/Pages/MattersPage.xaml.cs`
- Modify: `src/LocalScribe.App/ViewModels/MattersPageViewModel.cs` (panel-state seam)
- Modify: `src/LocalScribe.App/App.xaml.cs` (seam wiring)

**Interfaces:**
- Consumes: `MatterAssistantViewModel.Panel` (Task 2), `AssistantSidePanel` control,
  `WindowStateStore.Load/SaveAssistantPanel` key `"matters"`, commands from Task 3.
- Produces: the matter detail hosts the panel; the Assistant tab is gone; the Sessions tab has a
  Summary column; the tab strip is a single row.

**5a - detail grid + panel column.** The detail `Grid` (line ~55, `Grid.Column="1"`) gains
columns. Restructure to:

```xml
        <Grid Grid.Column="1" Visibility="{Binding HasSelection, Converter={StaticResource BoolToVis}}">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" MinWidth="200" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition x:Name="PanelColumn" Width="0" />
            </Grid.ColumnDefinitions>
            <Grid Grid.Column="0">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto" />
                    <RowDefinition Height="*" />
                </Grid.RowDefinitions>
                <!-- header WrapPanel (unchanged) at Grid.Row=0; TabControl (unchanged position)
                     at Grid.Row=1 -->
            </Grid>
            <GridSplitter Grid.Column="1" Width="6" HorizontalAlignment="Stretch"
                          VerticalAlignment="Stretch" ResizeBehavior="PreviousAndNext"
                          Background="{DynamicResource ControlFillColorSecondaryBrush}"
                          Visibility="{Binding Assistant.Panel.IsOpen, Converter={StaticResource BoolToVis}, FallbackValue=Collapsed}" />
            <controls:AssistantSidePanel Grid.Column="2" DataContext="{Binding Assistant.Panel}"
                          Visibility="{Binding Assistant.Panel.IsOpen, Converter={StaticResource BoolToVis}, FallbackValue=Collapsed}" />
        </Grid>
```

(`FallbackValue=Collapsed` because `Assistant` is null until a matter with a composed assistant
stack is selected.)

**5b - Ask toggle** in the header WrapPanel (after `HeaderCreatedDisplay`):

```xml
                <ToggleButton Content="Ask" Margin="12,0,0,0"
                              ToolTip="Ask the assistant about this matter's summaries"
                              IsChecked="{Binding Assistant.Panel.IsOpen, Mode=TwoWay, FallbackValue=False}"
                              Click="OnAskToggleClick" />
```

**5c - code-behind panel width/persistence** (MattersPage.xaml.cs). The page needs the store; add
a settable seam on `MattersPageViewModel` (`public WindowStateStore? PanelStateStore { get; set; }`
assigned in App.xaml.cs: `mattersVm.PanelStateStore = windowState;`) and mirror ReadViewWindow's
handlers with key `"matters"`, listening to `Assistant.Panel.PropertyChanged` - RE-SUBSCRIBED on
every `Assistant` swap (`MattersPageViewModel.Assistant` is `[ObservableProperty]`, so the page
subscribes `vm.PropertyChanged` for `nameof(MattersPageViewModel.Assistant)`). On Assistant swap:
apply the persisted/heuristic open state to the NEW panel (`saved?.Open ??
panel.Threads.HasAnyHistory` - matter scope has no summary section) after its `LoadAsync`
completes; simplest correct hook: `RebuildAssistant` already fires `Chat.LoadHistoryAsync` - change
it to call `assistant.Panel.LoadAsync(null, CancellationToken.None)` then (page-side) apply the
open bit when the `Assistant` property change fires. Persist on explicit toggle click +
`Unloaded`: save `(panel.IsOpen, PanelColumn.Width.Value or the cached width)` under `"matters"`
only when `_panelChoiceIsExplicit` (same rule as Phase 2).

In `RebuildAssistant` (MattersPageViewModel), replace the two loads:

```csharp
        if (Assistant is { } assistant)
        {
            _ = assistant.RefreshAsync(CancellationToken.None);
            _ = assistant.Panel.LoadAsync(null, CancellationToken.None);   // threads + chat history
        }
```

(`Panel.LoadAsync(null, ...)` skips the null Summary and runs `Threads.LoadAsync`, which renders
the active thread - `Chat.LoadHistoryAsync` becomes redundant here and is removed.)

**5d - Sessions tab Summary column** (after the Duration column in `TaggedGrid`). Chip language
mirrors SessionsPage's Status chips (`Chip` style), with the page-VM proxy pattern the grid's
context menu will not have - use `RelativeSource AncestorType=Page` bindings... NO: house pattern
for DataGrid cell commands on this page is direct `Click=` handlers in code-behind (see
OnOpenTranscript). Use a `DataGridTemplateColumn`:

```xml
                                <DataGridTemplateColumn Header="Summary" Width="110" MinWidth="90">
                                    <DataGridTemplateColumn.CellTemplate>
                                        <DataTemplate>
                                            <WrapPanel VerticalAlignment="Center">
                                                <!-- Done/Stale: a chip; click opens the read view's panel.
                                                     Stale gets the caution tint + tooltip (surfaced, never
                                                     silent). Null status renders NOTHING (unknown). -->
                                                <Button x:Name="SummaryChip" Visibility="Collapsed"
                                                        Click="OnOpenSummary" Padding="0" Margin="0"
                                                        Background="Transparent" BorderThickness="0"
                                                        Cursor="Hand">
                                                    <Border x:Name="ChipBorder" Style="{StaticResource Chip}">
                                                        <TextBlock Text="Summary" />
                                                    </Border>
                                                </Button>
                                                <Button x:Name="GenerateLink" Visibility="Collapsed"
                                                        Click="OnGenerateSummary" Padding="0" Margin="0"
                                                        Background="Transparent" BorderThickness="0"
                                                        Cursor="Hand"
                                                        ToolTip="Generate an AI summary draft for this session">
                                                    <TextBlock Text="Generate" FontSize="11" Opacity="0.7"
                                                               TextDecorations="Underline" />
                                                </Button>
                                            </WrapPanel>
                                            <DataTemplate.Triggers>
                                                <DataTrigger Binding="{Binding SummaryStatus}" Value="{x:Static vm:SummaryStatus.Done}">
                                                    <Setter TargetName="SummaryChip" Property="Visibility" Value="Visible" />
                                                    <Setter TargetName="SummaryChip" Property="ToolTip"
                                                            Value="An AI-generated draft summary exists - click to open it in the read view" />
                                                </DataTrigger>
                                                <DataTrigger Binding="{Binding SummaryStatus}" Value="{x:Static vm:SummaryStatus.Stale}">
                                                    <Setter TargetName="SummaryChip" Property="Visibility" Value="Visible" />
                                                    <Setter TargetName="ChipBorder" Property="Background"
                                                            Value="{DynamicResource SystemFillColorCautionBackgroundBrush}" />
                                                    <Setter TargetName="SummaryChip" Property="ToolTip"
                                                            Value="The transcript changed after this AI-generated draft summary - click to open and regenerate" />
                                                </DataTrigger>
                                                <DataTrigger Binding="{Binding SummaryStatus}" Value="{x:Static vm:SummaryStatus.None}">
                                                    <Setter TargetName="GenerateLink" Property="Visibility" Value="Visible" />
                                                </DataTrigger>
                                            </DataTemplate.Triggers>
                                        </DataTemplate>
                                    </DataGridTemplateColumn.CellTemplate>
                                </DataGridTemplateColumn>
```

Add `xmlns:vm="clr-namespace:LocalScribe.App.ViewModels"` to the Page element. Code-behind:

```csharp
    private void OnOpenSummary(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaggedSessionItem row)
            _vm.OpenSummaryCommand.Execute(row);
    }

    private void OnGenerateSummary(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaggedSessionItem row)
            _vm.GenerateSummaryCommand.Execute(row);
    }
```

(Match the page's existing code-behind field name for the VM - it may be `_vm` or a property;
follow the file.)

**5e - remove the Assistant tab** (lines ~253-320 `<TabItem Header="Assistant">...`), keeping the
coverage/status surfaces OUT (the panel's CoverageText slot + the new Summary column replace
them).

**5f - single-row tab strip.** Four tabs remain; guarantee one row at ANY width by swapping the
header host for a horizontally scrolling single row:

```xml
            <TabControl Grid.Row="1" SelectedIndex="1">
                <TabControl.Template>
                    <ControlTemplate TargetType="TabControl">
                        <DockPanel>
                            <!-- One-row header (addendum): TabPanel wraps by design, so replace it
                                 with a horizontal StackPanel in a scroller - headers never stack
                                 into the broken-looking two-row grid, however narrow the panel
                                 makes the detail column. TabItem styling (WPF-UI) is untouched. -->
                            <ScrollViewer DockPanel.Dock="Top" HorizontalScrollBarVisibility="Auto"
                                          VerticalScrollBarVisibility="Disabled">
                                <StackPanel Orientation="Horizontal" IsItemsHost="True" />
                            </ScrollViewer>
                            <Border Background="{TemplateBinding Background}"
                                    BorderBrush="{TemplateBinding BorderBrush}"
                                    BorderThickness="{TemplateBinding BorderThickness}">
                                <ContentPresenter ContentSource="SelectedContent" />
                            </Border>
                        </DockPanel>
                    </ControlTemplate>
                </TabControl.Template>
                ...
```

- [ ] **Step 1: Apply 5a-5f** in that order, building after each lettered chunk
  (`dotnet build LocalScribe.slnx 2>&1 | tail -4` - 0/0).
- [ ] **Step 2: Full App tests** - green.
- [ ] **Step 3: Commit**

```bash
git add src/LocalScribe.App/Pages/MattersPage.xaml src/LocalScribe.App/Pages/MattersPage.xaml.cs src/LocalScribe.App/ViewModels/MattersPageViewModel.cs src/LocalScribe.App/App.xaml.cs
git commit -m "feat(matters): chat-only assistant panel, Summary column, single-row tab strip; Assistant tab removed"
```

---

### Task 6: Phase gate + smoke additions

- [ ] **Step 1: Full gate** (build 0/0; Core = 2 known fails only; App green).
- [ ] **Step 2: Append to `docs/plans/2026-07-25-assistant-panel-smoke-runbook.md`:**
  P3-1 Matter Ask toggle opens the chat-only panel (no Summary expander); content reflows; state
       persists per the `"matters"` key after an explicit toggle.
  P3-2 Matter switch swaps the panel's threads/history; warm helper torn down (next ask re-primes).
  P3-3 Coverage disclosure renders inside the panel after an answer.
  P3-4 Sessions-tab Summary column: chip for Done, caution chip for Stale, Generate link for None;
       chip click opens the read view with the panel open; Generate opens it AND starts generation.
  P3-5 Tab strip stays one row with the panel open at minimum window width (scrolls, never wraps).
  P3-6 The matter Assistant tab is gone; Details/Sessions/Vocabulary/Advanced intact.
- [ ] **Step 3: Commit** (`docs(smoke): Phase 3 additions`).
