# Sessions Summary Column - Phase 4 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Summary column (none / done / stale + Generate/open) on the main Sessions list - the
cross-session summaries overview.

**Architecture:** `SessionRowViewModel` gains a second mutable slot (`SummaryStatus?`, the
`ContentSnippet` precedent); `SessionsPageViewModel` stamps it in a background pass via the
Phase 3 `SummaryStatusProvider` seam and raises `OpenSummaryRequested(sessionId, regenerate)`;
`App.xaml.cs` routes that to the existing `openSessionSummary` (Phase 3). The XAML column mirrors
Phase 3's chip template exactly (learn once).

**Tech Stack:** .NET 10 WPF, WPF-UI, CommunityToolkit.Mvvm, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-24-assistant-chat-surfaces-design.md` - Phase 4 +
addendum "Summary columns". **Prerequisites: Phases 2 AND 3 merged** (`SummaryStatus`,
`SummaryStatusProvider`, `summaryStatusFor`, `openSessionSummary` all exist).

## Global Constraints

Same as the Phase 2/3 plans (no emojis; file-scoped namespaces; /// = WHY; 0-warn build; Core
green except 2 known fixture fails; App green; theme resources only; never a second SummaryStore).
Branch: `feat/assistant-surfaces-phase4` off master (after Phase 3 merges), dedicated worktree,
never push. Close LocalScribe.App.exe before building.

## Known accepted limitation (record it, do not fix here)

The column reflects summary standing as of the last scan / row refresh. Generating a summary in a
read view does not push a live update into an already-rendered grid row (SummaryStore has no
change event); the row corrects on the next refresh/upsert of that row or page reload. The smoke
runbook makes this visible; a store change event is future work, not this phase.

---

### Task 1: SessionRowViewModel.SummaryStatus + stamping pass + events

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SessionRowViewModel.cs`
- Modify: `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs`
- Test: `tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs` (extend)

**Interfaces:**
- Consumes: `SummaryStatus` / `SummaryStatusProvider` (Phase 3), `summaryStatusFor` +
  `openSessionSummary` in App.xaml.cs (Phase 3).
- Produces:
  - `SessionRowViewModel`: `[ObservableProperty] private SummaryStatus? _summaryStatus;` beside
    `_contentSnippet`, with a doc comment naming it the SECOND sanctioned mutable slot (same
    stamped-by-background-pass rule; null = unknown = blank cell, never a false "None").
  - `SessionsPageViewModel`:
    - `public SummaryStatusProvider? SummaryStatusProvider { get; set; }` (settable seam)
    - `public event Action<string, bool>? OpenSummaryRequested;`
    - `public IRelayCommand<SessionRowViewModel> OpenSummaryCommand { get; }` -> `(Id, false)`
    - `public IRelayCommand<SessionRowViewModel> GenerateSummaryCommand { get; }` -> `(Id, true)`

- [ ] **Step 1: Write the failing tests** (append to `SessionsPageViewModelTests.cs`, reusing its
  fake-maintenance + synchronous-dispatch fixtures):

```csharp
// 1. Load_stamps_summary_status_in_background: 2 sessions; provider maps one Done, one Stale;
//    after LoadAsync completes (synchronous dispatch), rows carry the statuses.
// 2. No_provider_leaves_null: seam unset -> all rows null.
// 3. Provider_fault_leaves_that_row_null: provider throws for one id -> that row null, the other
//    stamped; LoadAsync does not throw.
// 4. Upsert_restamps_the_row: UpsertRowAsync on a session whose provider result changed ->
//    the new row object carries the new status.
// 5. Open_and_generate_raise_event: OpenSummaryCommand raises (id,false), GenerateSummaryCommand
//    raises (id,true).
```

Write all five as real tests with real asserts.

- [ ] **Step 2: Run to verify failure** - compile errors.

- [ ] **Step 3: Implement.**

`SessionRowViewModel.cs` - beside `_contentSnippet`:

```csharp
    /// <summary>The SECOND sanctioned mutable slot (Phase 4, ContentSnippet precedent): summary
    /// standing stamped by SessionsPageViewModel's background pass so the scan never waits on N
    /// summaries.json reads. Null = not yet probed - renders blank, never a false "no summary".</summary>
    [ObservableProperty] private SummaryStatus? _summaryStatus;
```

`SessionsPageViewModel.cs` - seam + event + commands (ctor-built like its other commands):

```csharp
    public SummaryStatusProvider? SummaryStatusProvider { get; set; }
    /// <summary>(sessionId, regenerate): Summary-column click-throughs, routed by the App
    /// composition to the read view's assistant panel (the generation surface).</summary>
    public event Action<string, bool>? OpenSummaryRequested;
    public IRelayCommand<SessionRowViewModel> OpenSummaryCommand { get; }
    public IRelayCommand<SessionRowViewModel> GenerateSummaryCommand { get; }
```

```csharp
        OpenSummaryCommand = new RelayCommand<SessionRowViewModel>(r =>
        { if (r is not null) OpenSummaryRequested?.Invoke(r.Id, false); });
        GenerateSummaryCommand = new RelayCommand<SessionRowViewModel>(r =>
        { if (r is not null) OpenSummaryRequested?.Invoke(r.Id, true); });
```

Stamping pass - add, and call it fire-and-forget at the end of `LoadAsync`'s success path (after
`_all` is built and filters applied), passing the freshly built row list:

```csharp
    /// <summary>Background stamping (the ContentSnippet precedent): one provider read per row,
    /// marshalled per-row so early rows light up while later ones still read. Works on the row
    /// OBJECTS handed in (not indices) so a concurrent rebuild simply orphans the old pass
    /// harmlessly. Faults leave null - the column never invents a state it could not read.</summary>
    private async Task StampSummaryStatusAsync(IReadOnlyList<SessionRowViewModel> rows, CancellationToken ct)
    {
        if (SummaryStatusProvider is not { } provider) return;
        foreach (var row in rows)
        {
            try
            {
                var status = await provider(row.Id, ct);
                _dispatch(() => row.SummaryStatus = status);
            }
            catch { /* unknown stays blank */ }
        }
    }
```

In `UpsertRowAsync` and `RefreshRowAsync`, after the new row object is created (beside the
existing `ContentSnippet` re-stamp in Upsert), re-stamp just that row:

```csharp
                _ = StampSummaryStatusAsync([newRow], CancellationToken.None);
```

`App.xaml.cs` - beside the matters wiring:

```csharp
        sessionsVm.SummaryStatusProvider = summaryStatusFor;
        sessionsVm.OpenSummaryRequested += (sid, regen) => openSessionSummary(sid, regen);
```

(`openSessionSummary` is declared after `sessionsVm` exists but the subscription line must sit
AFTER `openSessionSummary`'s declaration - place it right next to the mattersVm equivalent.)

- [ ] **Step 4: Run** the SessionsPage test suites + full App suite - green.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SessionRowViewModel.cs src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs
git commit -m "feat(sessions): SummaryStatus stamping pass + open/generate summary events"
```

---

### Task 2: Sessions grid Summary column (XAML)

**Files:**
- Modify: `src/LocalScribe.App/Pages/SessionsPage.xaml` (columns block, lines ~175-303)
- Modify: `src/LocalScribe.App/Pages/SessionsPage.xaml.cs` (only if the command route needs it -
  it does NOT: this grid already routes row commands through the `VmProxy` BindingProxy)

**Interfaces:**
- Consumes: `SummaryStatus` (x:Static in triggers), `OpenSummaryCommand`/`GenerateSummaryCommand`
  via `{Binding Data.<Command>, Source={StaticResource VmProxy}}` (the page's established
  Style/DataTemplate command pattern), `Chip` style.

- [ ] **Step 1: Add the column** between Status and Matters (mirroring Phase 3's template; the
  ONLY differences are the VmProxy command route and the row type):

```xml
                <DataGridTemplateColumn Header="Summary" Width="110" MinWidth="90">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <WrapPanel VerticalAlignment="Center">
                                <Button x:Name="SummaryChip" Visibility="Collapsed"
                                        Command="{Binding Data.OpenSummaryCommand, Source={StaticResource VmProxy}}"
                                        CommandParameter="{Binding}"
                                        Padding="0" Margin="0" Background="Transparent"
                                        BorderThickness="0" Cursor="Hand">
                                    <Border x:Name="ChipBorder" Style="{StaticResource Chip}">
                                        <TextBlock Text="Summary" />
                                    </Border>
                                </Button>
                                <Button x:Name="GenerateLink" Visibility="Collapsed"
                                        Command="{Binding Data.GenerateSummaryCommand, Source={StaticResource VmProxy}}"
                                        CommandParameter="{Binding}"
                                        Padding="0" Margin="0" Background="Transparent"
                                        BorderThickness="0" Cursor="Hand"
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

Check the Page element already declares `xmlns:vm="clr-namespace:LocalScribe.App.ViewModels"`
(SessionsPage.xaml may not - add if missing). Grid column budget: Summary is fixed-width like
Status, inserted BEFORE the star-sized Matters column, so nothing is pushed off-screen (the
Matters star absorbs the slack; verify at min window width in smoke).

- [ ] **Step 2: Build + full App tests** - 0/0, green.

- [ ] **Step 3: Commit**

```bash
git add src/LocalScribe.App/Pages/SessionsPage.xaml src/LocalScribe.App/Pages/SessionsPage.xaml.cs
git commit -m "feat(sessions): Summary column - chip for done/stale, Generate link for none"
```

---

### Task 3: Phase gate + smoke additions

- [ ] **Step 1: Full gate** (build 0/0; Core = 2 known fixture fails only; App green; Stop_upserts
  flake re-run once if it surfaces).
- [ ] **Step 2: Append to `docs/plans/2026-07-25-assistant-panel-smoke-runbook.md`:**
  P4-1 Sessions list shows the Summary column: chip (done), caution chip (stale), Generate (none);
       blank while the background pass is still reading on a large library.
  P4-2 Chip click opens that session's read view with the panel open on the summary.
  P4-3 Generate opens the read view AND starts a generation (streaming visible, draft label).
  P4-4 Known limitation check: generate a summary in a read view; the grid row still shows its
       old state until that row refreshes (e.g. Session Details save) or the page reloads - this
       is the recorded accepted limitation, not a bug.
  P4-5 Narrow the window: Date/Duration/Status/Summary stay visible; Matters absorbs the slack;
       nothing clips off-screen.
- [ ] **Step 3: Commit** (`docs(smoke): Phase 4 additions`).
