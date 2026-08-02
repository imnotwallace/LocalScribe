# Playback Transport (Sync Toggle, Go-To Box, Contextual Mixer) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox ("- [ ]") syntax for tracking.

**Goal:** Implement spec sections 7, 8 and 9 of `docs/superpowers/specs/2026-08-02-ux-round-design.md` as one transport-bar change: a "Sync transcript" follow-along pill, a type-to-jump go-to-timestamp box, and a contextual channel mixer that only shows per-leg controls when both legs exist.

**Architecture:** All new state lives on the WPF-free view models (`PlaybackViewModel` gains `SyncTranscript`, `HasBothLegs`/`HasSingleLeg`/`SingleLegVolume`; `ReadViewViewModel` gains the go-to parse/seek surface); a new pure `TimestampParser` in Core inverts `TimestampFormat.Stamp`. All scrolling, focus, and disengage-gesture detection is view-layer code in `ReadViewWindow.xaml.cs`, verified by a new smoke runbook because no STA/WPF test harness exists.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm (`[ObservableProperty]`), Wpf.Ui 4.0.3 (`ui:SymbolIcon`, `PillToggleButton` style), xUnit (headless VM-level only).

**Cross-plan note:** The find-scroll plan (spec items 1-2) executes FIRST and may (a) hoist the private `FindScrollViewer` (currently `ReadViewWindow.xaml.cs:468-477`, duplicated at `LiveViewWindow.xaml.cs:200-209`) into a shared static helper, and (b) add branches to `OnVmPropertyChanged` (`ReadViewWindow.xaml.cs:310-318`). Reference `FindScrollViewer` from wherever it lives at execution time, and merge new `else if` branches into whatever shape `OnVmPropertyChanged` has by then — the branches in this plan are additive and order-independent with respect to the find branches. All line anchors below were verified against the live code on 2026-08-02; re-verify before editing (the find-scroll plan will drift them).

## Global Constraints

- Strict TDD: write the failing test before any implementation, always (view-layer-only tasks substitute a build gate + smoke-runbook checkbox).
- No Unicode emojis anywhere in code, test scripts, or runbooks.
- VMs stay WPF-free: nothing under `src\LocalScribe.App\ViewModels` or `src\LocalScribe.Core` may reference WPF types.
- No bool-inverting converter exists — house rule is Style + DataTrigger (see the comment at `ReadViewWindow.xaml:48-51`).
- `[ObservableProperty]` equality-gates same-value sets — re-raise manually after collection rebuilds when needed (this plan relies on the gate: `PlayingSectionIndex` fires once per row advance).
- Invariant culture in all export/parse text: `TimestampParser` uses `CultureInfo.InvariantCulture` exclusively.
- Transcripts are evidence — never destructive; nothing in this plan writes any session file.
- Close any running `LocalScribe.App.exe` before building — a running app locks `Core.dll` and fails the build with MSB3027.
- View-layer scroll/caret/focus/visual behavior cannot be unit-tested here (no STA harness) — such tasks end with a smoke-runbook checkbox addition instead of a fake test.

---

### Task 1: `SyncTranscript` flag on `PlaybackViewModel` + follow-contract pin tests

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\PlaybackViewModel.cs` (insert after the `_playingIndex` field, currently line 59)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\PlaybackViewModelTests.cs` (append new facts before the `FakePlayer` nested class, currently line 570)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\ReadViewViewModelTests.cs` (append new facts before the `FakePlayer` nested class, currently line 507)

**Interfaces:**
- Consumes: existing `PlaybackViewModel` (ctor `PlaybackViewModel(IDualAudioPlayer player, Action<Action> dispatch, Func<long>? wallClock = null)`), existing test harnesses `PlaybackViewModelTests.MakeVm()` (line 21) and `ReadViewViewModelTests.MakeVm()` (lines 81-82) + `WriteFixtureSessionAsync(string id)` (lines 88-136).
- Produces: `public bool SyncTranscript { get; set; }` (generated from `[ObservableProperty] private bool _syncTranscript;`) — Tasks 2 and 5 bind/read it; nothing else.

- [ ] **Step 1: Write the failing tests**

Append to `PlaybackViewModelTests.cs` (inside the class, before the `FakePlayer` nested class):

```csharp
    [Fact]
    public void SyncTranscript_defaults_off_and_raises_PropertyChanged_on_toggle()
    {
        // Item 7 (UX round 2026-08-02): the follow toggle is VM state so it survives edit-mode
        // round trips, but it is deliberately NOT persisted - off by default per window.
        var vm = MakeVm();
        Assert.False(vm.SyncTranscript);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SyncTranscript = true;
        Assert.True(vm.SyncTranscript);
        Assert.Contains(nameof(PlaybackViewModel.SyncTranscript), raised);
    }
```

Append to `ReadViewViewModelTests.cs` (inside the class, before the `FakePlayer` nested class):

```csharp
    [Fact]
    public async Task SyncTranscript_survives_an_edit_mode_round_trip()
    {
        // Item 7: the toggle is inert while editing (view-layer disable) but its STATE must
        // survive Edit -> Cancel so follow re-engages on return to read mode.
        await WriteFixtureSessionAsync("read-sync");
        var vm = MakeVm();
        await vm.LoadAsync("read-sync", CancellationToken.None);

        vm.Playback.SyncTranscript = true;
        vm.EnterEditMode();
        Assert.True(vm.IsEditMode);
        Assert.True(vm.Playback.SyncTranscript);
        vm.CancelEdit();
        Assert.False(vm.IsEditMode);
        Assert.True(vm.Playback.SyncTranscript);
    }

    [Fact]
    public void PlayingSectionIndex_fires_once_per_row_advance_not_per_tick()
    {
        // Pin the contract the window's follow-scroll hook depends on: [ObservableProperty]
        // equality-gates same-value sets, so PropertyChanged fires once per row ADVANCE, never
        // per 150 ms tick - and the -1 sentinel (before the first row) never fires from -1.
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 1000, EndMs = 1500, DisplayName = "Sam", Text = "a" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 1600, EndMs = 3000, DisplayName = "Sam", Text = "b" }));

        int fired = 0;
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(ReadViewViewModel.PlayingSectionIndex)) fired++; };

        _player.PositionMs = 0;    vm.TickPlayback();   // before the first row: stays -1
        Assert.Equal(0, fired);
        Assert.Equal(-1, vm.PlayingSectionIndex);        // -1 sentinel: the window must never scroll
        _player.PositionMs = 1000; vm.TickPlayback();   // -1 -> 0
        _player.PositionMs = 1200; vm.TickPlayback();   // same row: equality-gated, no event
        _player.PositionMs = 1400; vm.TickPlayback();
        Assert.Equal(1, fired);
        _player.PositionMs = 1600; vm.TickPlayback();   // 0 -> 1
        Assert.Equal(2, fired);
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~PlaybackViewModelTests"`
Expected: build FAILURE with `error CS1061: 'PlaybackViewModel' does not contain a definition for 'SyncTranscript'` (the pin test compiles but cannot run until the build is green).

- [ ] **Step 3: Minimal implementation**

In `PlaybackViewModel.cs`, insert directly after the `_playingIndex` field (currently line 59, before the `PlayPauseCaption` property):

```csharp
    /// <summary>Item 7 (UX round 2026-08-02): "Sync transcript" follow-along toggle. Lives on
    /// this WPF-free VM (not the window) so it is testable and survives edit-mode round trips;
    /// the window owns every actual scroll. Deliberately NOT persisted - off by default per
    /// window (spec decision).</summary>
    [ObservableProperty] private bool _syncTranscript;
```

- [ ] **Step 4: Run to verify PASS**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~PlaybackViewModelTests"`
Expected: PASS (all facts, including the pre-existing ones).
Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"`
Expected: PASS, including `SyncTranscript_survives_an_edit_mode_round_trip` and `PlayingSectionIndex_fires_once_per_row_advance_not_per_tick`.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/PlaybackViewModel.cs tests/LocalScribe.App.Tests/PlaybackViewModelTests.cs tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs
git commit -m "feat(playback): SyncTranscript follow toggle state + follow-contract pin tests"
```

---

### Task 2: Sync pill XAML + follow/disengage/nudge scrolling in `ReadViewWindow`

View-layer only — no unit test is possible (Global Constraints); the gate is a clean build plus smoke-runbook checkboxes created in this task.

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ReadViewWindow.xaml` (Sync pill after the Stop button, currently lines 206-222; three new event attributes on `RowList`, currently lines 321-327)
- Modify: `F:\LocalScribe\src\LocalScribe.App\ReadViewWindow.xaml.cs` (tick wiring line 170, `OnPlaybackPropertyChanged` lines 203-208, `OnVmPropertyChanged` lines 310-318, new members after `OnSeekDragCompleted` line 493)
- Create: `F:\LocalScribe\docs\plans\2026-08-02-playback-transport-smoke-runbook.md`

**Interfaces:**
- Consumes: `PlaybackViewModel.SyncTranscript` (Task 1); `ReadViewViewModel.PlayingSectionIndex` (int, -1 sentinel, `ReadViewViewModel.cs:119`); `ReadViewViewModel.IsEditMode`; `FindScrollViewer(DependencyObject)` (currently private static at `ReadViewWindow.xaml.cs:468-477` — if the find-scroll plan hoisted it into a shared static helper, call the hoisted one); `PillToggleButton` style (`Styles\Fluent.Shared.xaml:105-139`).
- Produces: `private void ScrollRowToUpperThird(int index)` and `private bool _programmaticFollowScroll` — Task 5's one-shot go-to scroll reuses both.

- [ ] **Step 1: Add the Sync pill to the transport WrapPanel**

In `ReadViewWindow.xaml`, insert immediately after the Stop `</Button>` (currently line 222) and before the elapsed/seek/total `StackPanel`:

```xaml
                <!-- Item 7 (UX round 2026-08-02): follow-along toggle. ToggleButton cannot
                     BasedOn PillButton (it does not derive from Button), so the derived style
                     is BasedOn PillToggleButton and only adds the edit-mode disable: the read
                     list is collapsed while editing, so following it would scroll a hidden
                     control. No bool-inverting converter exists by house rule - the disable is
                     a Style + DataTrigger (same fallback as the version ComboBox above). The
                     icon-flip DataTrigger reads the pill's own IsChecked, the identical idiom
                     the mute pills below use, so checked shows the "arrows in motion" glyph. -->
                <ToggleButton Margin="0,0,12,4"
                              IsChecked="{Binding Playback.SyncTranscript}"
                              ToolTip="Keep the transcript scrolled to the line being played">
                    <ToggleButton.Style>
                        <Style TargetType="ToggleButton" BasedOn="{StaticResource PillToggleButton}">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding IsEditMode}" Value="True">
                                    <Setter Property="IsEnabled" Value="False" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </ToggleButton.Style>
                    <StackPanel Orientation="Horizontal">
                        <ui:SymbolIcon FontSize="18" Margin="0,0,6,0">
                            <ui:SymbolIcon.Style>
                                <Style TargetType="ui:SymbolIcon">
                                    <Setter Property="Symbol" Value="ArrowSyncOff20" />
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding IsChecked, RelativeSource={RelativeSource AncestorType=ToggleButton}}" Value="True">
                                            <Setter Property="Symbol" Value="ArrowSync24" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </ui:SymbolIcon.Style>
                        </ui:SymbolIcon>
                        <TextBlock Text="Sync" VerticalAlignment="Center" />
                    </StackPanel>
                </ToggleButton>
```

(`ArrowSyncOff20` and `ArrowSync24` were both verified present in Wpf.Ui 4.0.3's `SymbolRegular` enum on 2026-08-02; there is no `ArrowSyncOff24`, and both render at the pill's 18px `FontSize` regardless of the design-variant suffix.)

- [ ] **Step 2: Wire the disengage gestures on `RowList`**

In `ReadViewWindow.xaml`, add three attributes to the `RowList` element (currently lines 321-327), after `MouseDoubleClick="OnRowActivated"`:

```xaml
                  PreviewMouseWheel="OnRowListPreviewMouseWheel"
                  PreviewKeyDown="OnRowListPreviewKeyDown"
                  Thumb.DragStarted="OnRowListScrollThumbDragStarted"
```

(`Thumb.DragStarted` is an attached routed event that bubbles up from the vertical scrollbar's thumb — the same attribute the `SeekSlider` already uses at line 235. It fires only for the RowList's own scrollbar here, never the seek slider's thumb.)

- [ ] **Step 3: Add the follow-scroll machinery to the code-behind**

In `ReadViewWindow.xaml.cs`, insert after `OnSeekDragCompleted` (currently ends line 493), before `OnClosed`:

```csharp
    // ---- Item 7 (UX round 2026-08-02): Sync-transcript follow scrolling -----------------------

    /// <summary>True while a follow/go-to scroll THIS window issued is still settling.
    /// ScrollIntoView and the deferred centering both raise ScrollChanged, and the 150 ms nudge
    /// below could otherwise measure the container mid-flight and re-scroll every tick - so the
    /// flag is set before each programmatic scroll and cleared on a deferred dispatcher turn
    /// AFTER the centering pass has run (mandatory per the spec's disengage design).</summary>
    private bool _programmaticFollowScroll;

    /// <summary>Shared by the item-7 follow, the enable-snap, and the item-8 go-to jump: bring
    /// Rows[index] into view, then on a deferred pass place it ~1/3 from the viewport top.
    /// Plain ScrollIntoView with pixel scrolling scrolls to the NEAREST edge, so forward
    /// playback would pin each newly-current row to the BOTTOM edge and the reader would never
    /// see upcoming text. The centering must be a second, dispatched pass: under recycling
    /// virtualization the row's container may not exist until ScrollIntoView has run a layout,
    /// and ContainerFromIndex can still return null (tolerated - the ScrollIntoView result
    /// stands). Range-guards internally so the -1 sentinel never scrolls.</summary>
    private void ScrollRowToUpperThird(int index)
    {
        if (index < 0 || index >= _vm.Rows.Count) return;
        _programmaticFollowScroll = true;
        RowList.ScrollIntoView(_vm.Rows[index]);
        _ = Dispatcher.InvokeAsync(() =>
        {
            var scroll = FindScrollViewer(RowList);
            if (scroll is not null
                && RowList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement item)
            {
                double itemTop = item.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + itemTop - scroll.ViewportHeight / 3);
            }
            // Clear on ANOTHER deferred turn: the offset change above publishes its
            // ScrollChanged only after this delegate returns.
            _ = Dispatcher.InvokeAsync(() => _programmaticFollowScroll = false,
                DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Long-monologue nudge, driven by the same 150 ms timer as TickPlayback:
    /// PlayingSectionIndex only fires on a row ADVANCE, so once the playing row's container has
    /// left the viewport for any non-user reason (window resize, panel open/close, a reload's
    /// offset restore - which never re-fires PlayingSectionIndex) nothing else would bring it
    /// back. Skipped while a follow scroll is still settling. A container that exists and is
    /// even partially visible is left alone - no per-tick recentering churn.</summary>
    private void NudgeFollowIfNeeded()
    {
        if (!_vm.Playback.SyncTranscript || _vm.IsEditMode || _programmaticFollowScroll) return;
        int index = _vm.PlayingSectionIndex;
        if (index < 0 || index >= _vm.Rows.Count) return;
        var scroll = FindScrollViewer(RowList);
        if (scroll is null) return;
        if (RowList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement item)
        {
            ScrollRowToUpperThird(index);            // virtualized away entirely: off-screen for sure
            return;
        }
        double top = item.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
        if (top + item.ActualHeight < 0 || top > scroll.ViewportHeight)
            ScrollRowToUpperThird(index);
    }

    // Item 7 disengage: a real user scroll intent turns the follow toggle off. These three
    // gestures can ONLY originate from the user - programmatic ScrollIntoView /
    // ScrollToVerticalOffset raise ScrollChanged but never PreviewMouseWheel,
    // Thumb.DragStarted, or PreviewKeyDown - so the handlers need no guard-flag check; the
    // _programmaticFollowScroll flag protects the nudge path instead.
    private void OnRowListPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        => DisengageSync();

    private void OnRowListScrollThumbDragStarted(object sender, RoutedEventArgs e) => DisengageSync();

    private void OnRowListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.PageUp or System.Windows.Input.Key.PageDown)
            DisengageSync();
    }

    private void DisengageSync()
    {
        if (_vm.Playback.SyncTranscript) _vm.Playback.SyncTranscript = false;
    }
```

- [ ] **Step 4: Hook follow into the property-changed handlers and the tick**

In `ReadViewWindow.xaml.cs`, replace `OnPlaybackPropertyChanged` (currently lines 203-208) with:

```csharp
    private void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackViewModel.IsAvailable)
            && _vm.Playback.IsAvailable && !_tick.IsEnabled)
            _tick.Start();
        // Item 7: enabling the toggle snaps to the current row immediately (spec decision);
        // disabling does nothing. ScrollRowToUpperThird range-guards the -1 sentinel itself.
        else if (e.PropertyName == nameof(PlaybackViewModel.SyncTranscript)
            && _vm.Playback.SyncTranscript && !_vm.IsEditMode)
            ScrollRowToUpperThird(_vm.PlayingSectionIndex);
    }
```

(keep the existing "Idempotent:" comment block above the method as-is). Then add a new branch at the end of `OnVmPropertyChanged` (currently lines 310-318 — if the find-scroll plan already reshaped this method, append the branch to its current shape):

```csharp
        // Item 7 follow: PlayingSectionIndex fires once per row ADVANCE (equality-gated), so
        // this scrolls once per section, never per 150 ms tick. -1 (before the first row /
        // after the media ends) never scrolls; edit mode never scrolls (read list collapsed).
        else if (e.PropertyName == nameof(ReadViewViewModel.PlayingSectionIndex)
            && _vm.Playback.SyncTranscript && !_vm.IsEditMode
            && _vm.PlayingSectionIndex >= 0 && _vm.PlayingSectionIndex < _vm.Rows.Count)
            ScrollRowToUpperThird(_vm.PlayingSectionIndex);
```

Finally replace the tick wiring at the end of the constructor (currently line 170):

```csharp
        _tick.Tick += (_, _) =>
        {
            _vm.TickPlayback();
            NudgeFollowIfNeeded();
        };
```

- [ ] **Step 5: Build gate**

Close any running `LocalScribe.App.exe`, then run: `dotnet build LocalScribe.slnx`
Expected: Build succeeded, 0 warnings (the repo runs a 0-warning gate).

- [ ] **Step 6: Create the smoke runbook**

Create `F:\LocalScribe\docs\plans\2026-08-02-playback-transport-smoke-runbook.md` with exactly:

```markdown
# Read-View Playback Transport - Smoke Runbook (UX round 2026-08-02, items 7-9)

Feature: transport-bar changes from `docs/superpowers/specs/2026-08-02-ux-round-design.md`
sections 7 (Sync transcript follow toggle), 8 (go-to timestamp box), 9 (contextual channel
mixer). Run after the plan's automated gates (App + Core test suites, solution build) are
green.

## Prep

- Build and run the app (close any previously running `LocalScribe.App.exe` first).
- Have a finalized DUAL-LEG session (both Local and Remote audio retained) whose transcript is
  long enough to scroll well past two viewport heights, including at least one long
  single-speaker monologue section.
- Have a SINGLE-LEG session (e.g. an imported audio file - import produces one leg).

## Part A: Sync transcript follow toggle (item 7)

- [ ] **A1 Pill placement:** open the dual-leg session's read view. A "Sync" pill with a
  sync-arrows icon sits in the transport bar directly after Stop. Tooltip reads "Keep the
  transcript scrolled to the line being played". It starts OFF on every fresh window.
- [ ] **A2 Follow during play:** press Play, enable Sync, let playback cross several section
  boundaries. Each time the highlight advances to a new row, the list scrolls so the playing
  row sits roughly one third from the viewport top (never pinned at the bottom edge).
- [ ] **A3 Snap on enable:** with playback deep in the transcript and Sync OFF, scroll far
  away manually, then enable Sync - the list snaps to the playing row immediately, without
  waiting for the next section boundary.
- [ ] **A4 Wheel disengages:** with Sync ON during play, scroll the mouse wheel over the
  transcript - the Sync pill turns itself OFF and the list stays where you put it.
- [ ] **A5 Scrollbar thumb disengages:** re-enable Sync, then drag the transcript scrollbar
  thumb - Sync turns OFF.
- [ ] **A6 PageUp/PageDown disengages:** re-enable Sync, focus the list, press PageUp -
  Sync turns OFF. (Repeat with PageDown.)
- [ ] **A7 Follow does not self-disengage:** enable Sync and let playback run hands-off
  across at least five section advances - the pill stays ON the whole time (the toggle's own
  scrolls never count as user intent).
- [ ] **A8 Monologue nudge:** while a long single-speaker section is playing with Sync ON,
  resize the window (or open the Ask panel) so the playing row leaves the viewport - within
  a beat (~150 ms tick) the list is nudged so the playing row is visible again.
- [ ] **A9 Edit-mode inertness:** with Sync ON, click Edit. The Sync pill renders disabled;
  playback keeps running; the edit table does NOT scroll on section advances. Cancel -
  the pill is enabled again, still checked, and follow resumes on the next section advance.
- [ ] **A10 Scrub behaviour (accepted):** with Sync ON, drag the seek slider - the list
  freezes during the drag, then jumps once to the new playing row on release.
- [ ] **A11 -1 sentinel:** Stop playback (position 0, before the first row's window if your
  fixture starts late) - no scroll fires; enabling Sync with no current row does nothing.

## Part B: Go-to timestamp box (item 8) - added by Task 5

## Part C: Contextual channel mixer (item 9) - added by Task 7
```

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/ReadViewWindow.xaml.cs docs/plans/2026-08-02-playback-transport-smoke-runbook.md
git commit -m "feat(readview): Sync-transcript follow pill with upper-third follow scrolling"
```

---

### Task 3: `TimestampParser` in Core (inverse of `TimestampFormat.Stamp`)

**Files:**
- Create: `F:\LocalScribe\src\LocalScribe.Core\Projection\TimestampParser.cs`
- Test: `F:\LocalScribe\tests\LocalScribe.Core.Tests\TimestampParserTests.cs`

**Interfaces:**
- Consumes: `TimestampFormat.Stamp(long startMs, string mode, DateTimeOffset startedAtLocal)` (`src\LocalScribe.Core\Projection\TimestampFormat.cs:9`) — round-trip target only, no code dependency.
- Produces: `public static bool TimestampParser.TryParse(string? input, string mode, DateTimeOffset startedAtLocal, out long ms)` — Task 4's `ReadViewViewModel.GoToTimestamp` calls it. `mode` is the settings string `"relative"` / `"wallclock"` (`Settings.Timestamps`, `src\LocalScribe.Core\Model\Settings.cs:21`).

- [ ] **Step 1: Write the failing tests**

Create `F:\LocalScribe\tests\LocalScribe.Core.Tests\TimestampParserTests.cs` (Core.Tests has a project-wide `<Using Include="Xunit" />`, so no `using Xunit;` — match `RendererTests.cs` style):

```csharp
using LocalScribe.Core.Projection;

public class TimestampParserTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 6, 30, 14, 32, 0, TimeSpan.FromHours(8));   // fixed offset -> deterministic

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(59000)]
    [InlineData(60000)]
    [InlineData(85000)]
    [InlineData(3599000)]
    [InlineData(3600000)]
    [InlineData(3903000)]
    [InlineData(7322000)]
    public void Round_trips_TimestampFormat_Stamp_in_both_modes(long ms)
    {
        // Stamp truncates to whole seconds, so whole-second inputs must survive EXACTLY.
        foreach (string mode in new[] { "relative", "wallclock" })
        {
            string stamp = TimestampFormat.Stamp(ms, mode, Started);
            Assert.True(TimestampParser.TryParse(stamp, mode, Started, out long parsed));
            Assert.Equal(ms, parsed);
        }
    }

    [Theory]
    [InlineData("00:01", 1000)]
    [InlineData("0:01", 1000)]          // single-digit minutes accepted (m:ss)
    [InlineData("01:25", 85000)]
    [InlineData("90:00", 5400000)]      // >59 minutes without an hours field is legal input
    [InlineData("1:05:03", 3903000)]
    [InlineData(" 01:25 ", 85000)]      // surrounding whitespace trimmed
    public void Relative_inputs_parse(string input, long expected)
    {
        Assert.True(TimestampParser.TryParse(input, "relative", Started, out long ms));
        Assert.Equal(expected, ms);
    }

    [Fact]
    public void Wallclock_converts_via_the_session_local_start()
    {
        // Mirrors RendererTests.Wallclock_timestamp_adds_offset_to_start in reverse.
        Assert.True(TimestampParser.TryParse("14:33:25", "wallclock", Started, out long ms));
        Assert.Equal(85000, ms);
    }

    [Fact]
    public void Wallclock_wraps_past_midnight()
    {
        var lateStart = new DateTimeOffset(2026, 6, 30, 23, 50, 0, TimeSpan.FromHours(8));
        Assert.True(TimestampParser.TryParse("00:05:12", "wallclock", lateStart, out long ms));
        Assert.Equal(912000, ms);        // 15 min 12 s into the session, next calendar day
    }

    [Fact]
    public void Wallclock_before_start_reads_as_next_day_and_lets_the_caller_clamp()
    {
        // "14:31:00" one minute BEFORE a 14:32:00 start: the deterministic rule is "next day"
        // (23h59m in), which the VM's Playback.Seek clamp then pins to end-of-media. Documented
        // behaviour, not an error - midnight-crossing sessions make earlier-than-start ambiguous.
        Assert.True(TimestampParser.TryParse("14:31:00", "wallclock", Started, out long ms));
        Assert.Equal(86_340_000, ms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12")]                  // no colon
    [InlineData("1:60")]                // seconds out of range
    [InlineData("1:75:00")]             // minutes out of range when hours present
    [InlineData("1:2:3:4")]             // too many fields
    [InlineData(":")]
    [InlineData("12:")]
    [InlineData("-1:00")]               // signs rejected (NumberStyles.None)
    [InlineData("01.25")]
    [InlineData("999999999999:00")]     // overflow-length field returns false, never throws
    public void Garbage_returns_false_in_relative_mode(string input)
        => Assert.False(TimestampParser.TryParse(input, "relative", Started, out _));

    [Theory]
    [InlineData("14:33")]               // wallclock needs all three fields
    [InlineData("24:00:00")]            // hour out of range
    [InlineData("14:60:00")]
    [InlineData("14:00:60")]
    public void Garbage_returns_false_in_wallclock_mode(string input)
        => Assert.False(TimestampParser.TryParse(input, "wallclock", Started, out _));

    [Fact]
    public void Null_input_returns_false()
        => Assert.False(TimestampParser.TryParse(null, "relative", Started, out _));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~TimestampParserTests"`
Expected: build FAILURE with `error CS0103: The name 'TimestampParser' does not exist in the current context` (or CS0246).

- [ ] **Step 3: Minimal implementation**

Create `F:\LocalScribe\src\LocalScribe.Core\Projection\TimestampParser.cs`:

```csharp
using System.Globalization;
namespace LocalScribe.Core.Projection;

/// <summary>Inverse of <see cref="TimestampFormat.Stamp"/> for the read view's go-to box
/// (UX round 2026-08-02 item 8). Relative mode accepts m:ss / mm:ss / h:mm:ss; wallclock mode
/// accepts HH:mm:ss (a one-digit hour is tolerated) and converts via the session's local start.
/// A wallclock stamp EARLIER in the day than the session start is read as the NEXT day -
/// sessions can cross midnight, and the caller clamps to the media duration anyway.
/// Invariant culture throughout (Global Constraints); never throws - garbage returns false.</summary>
public static class TimestampParser
{
    public static bool TryParse(string? input, string mode, DateTimeOffset startedAtLocal, out long ms)
    {
        ms = 0;
        string[] parts = (input?.Trim() ?? "").Split(':');
        if (parts.Length is < 2 or > 3) return false;
        var n = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            // NumberStyles.None: digits only - no signs, whitespace, separators; TryParse also
            // absorbs overflow-length fields as false instead of throwing.
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]))
                return false;

        if (mode == "wallclock")
        {
            if (parts.Length != 3 || n[0] > 23 || n[1] > 59 || n[2] > 59) return false;
            var target = startedAtLocal - startedAtLocal.TimeOfDay + new TimeSpan(n[0], n[1], n[2]);
            if (target < startedAtLocal) target += TimeSpan.FromDays(1);   // crossed midnight
            ms = (long)(target - startedAtLocal).TotalMilliseconds;
            return true;
        }

        if (parts.Length == 2)                       // relative m:ss / mm:ss (minutes unbounded)
        {
            if (n[1] > 59) return false;
            ms = (n[0] * 60L + n[1]) * 1000L;
            return true;
        }
        if (n[1] > 59 || n[2] > 59) return false;    // relative h:mm:ss
        ms = ((n[0] * 60L + n[1]) * 60L + n[2]) * 1000L;
        return true;
    }
}
```

- [ ] **Step 4: Run to verify PASS**

Run: `dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~TimestampParserTests"`
Expected: PASS (34 test cases).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Projection/TimestampParser.cs tests/LocalScribe.Core.Tests/TimestampParserTests.cs
git commit -m "feat(projection): TimestampParser inverts TimestampFormat.Stamp for both display modes"
```

---

### Task 4: Go-to surface on `ReadViewViewModel` (parse, seek, one-shot scroll request)

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\ReadViewViewModel.cs` (insert after `UpdateFindStatus`, currently ends line 369, before the `LoadedView` record)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\ReadViewViewModelTests.cs` (append before the `FakePlayer` nested class)

**Interfaces:**
- Consumes: `TimestampParser.TryParse(string?, string, DateTimeOffset, out long)` (Task 3); existing private `SectionAt(long positionMs)` (`ReadViewViewModel.cs:177-187`); `Playback.Seek(long)` (clamps to `[0, DurationMs]` when duration is known, `PlaybackViewModel.cs:176-188`); `TimestampsMode` (defaults `"relative"`, line 108) and `StartedAtLocal` (line 109). `using LocalScribe.Core.Projection;` is already present (line 9).
- Produces: `public string GoToText { get; set; }`, `public bool GoToError { get; set; }` (both generated observables), `public event Action<int>? GoToRowScrollRequested`, `public void GoToTimestamp()` — Task 5 binds/calls all four.

- [ ] **Step 1: Write the failing tests**

Append to `ReadViewViewModelTests.cs`:

```csharp
    [Fact]
    public void GoToTimestamp_parses_seeks_and_requests_a_one_shot_scroll()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0,    EndMs = 1500, DisplayName = "Sam",  Text = "a" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 1600, EndMs = 3000, DisplayName = "Sam",  Text = "b" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 3200, EndMs = 4200, DisplayName = "Jane", Text = "c" }));

        int? scrolledTo = null;
        vm.GoToRowScrollRequested += i => scrolledTo = i;

        vm.GoToText = "00:03";                       // TimestampsMode defaults to "relative"
        vm.GoToTimestamp();

        Assert.False(vm.GoToError);
        Assert.Equal(3000, vm.Playback.PositionMs);
        Assert.Equal(1, scrolledTo);                 // row window [1600, 3200)
        vm.TickPlayback();                           // the highlight lands on the next tick
        Assert.Equal(1, vm.PlayingSectionIndex);
    }

    [Fact]
    public void GoToTimestamp_clamps_to_duration_and_targets_the_last_section()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0,    EndMs = 1500, DisplayName = "Sam",  Text = "a" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 1600, EndMs = 3000, DisplayName = "Sam",  Text = "b" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 3200, EndMs = 4200, DisplayName = "Jane", Text = "c" }));
        _player.DurationMs = 4200;
        _player.RaiseReady();                        // publishes DurationMs (dispatch runs inline)

        int? scrolledTo = null;
        vm.GoToRowScrollRequested += i => scrolledTo = i;
        vm.GoToText = "59:59";
        vm.GoToTimestamp();

        Assert.False(vm.GoToError);
        Assert.Equal(4200, vm.Playback.PositionMs);  // clamped by Seek, never past end-of-media
        Assert.Equal(2, scrolledTo);                 // last row owns its inclusive EndMs
    }

    [Fact]
    public void GoToTimestamp_invalid_input_sets_the_quiet_error_and_keeps_text_and_position()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0, EndMs = 1500, DisplayName = "Sam", Text = "a" }));
        bool scrolled = false;
        vm.GoToRowScrollRequested += _ => scrolled = true;

        vm.GoToText = "not a time";
        vm.GoToTimestamp();

        Assert.True(vm.GoToError);
        Assert.Equal("not a time", vm.GoToText);     // retained - quiet inline error, no dialog
        Assert.Equal(0, vm.Playback.PositionMs);     // no seek happened
        Assert.False(scrolled);

        vm.GoToText = "00:0";                        // ANY edit clears the error state
        Assert.False(vm.GoToError);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"`
Expected: build FAILURE with `error CS1061: 'ReadViewViewModel' does not contain a definition for 'GoToRowScrollRequested'` (and for `GoToText`/`GoToError`/`GoToTimestamp`).

- [ ] **Step 3: Minimal implementation**

In `ReadViewViewModel.cs`, insert after `UpdateFindStatus` (currently ends line 369), before the `LoadedView` record:

```csharp
    // ---- Type-to-jump timestamp box (UX round 2026-08-02 item 8) -----------------------------

    [ObservableProperty] private string _goToText = "";
    /// <summary>Quiet inline error state (red outline + retained text - never a dialog):
    /// flipped on by a failed GoToTimestamp, cleared the moment the user edits the text.</summary>
    [ObservableProperty] private bool _goToError;

    partial void OnGoToTextChanged(string value) => GoToError = false;

    /// <summary>One-shot scroll request for a committed jump: the window centers this row
    /// REGARDLESS of the Sync toggle (an explicit jump is its own intent; Sync state is left
    /// untouched). Raised only for an in-range row.</summary>
    public event Action<int>? GoToRowScrollRequested;

    /// <summary>Enter in the go-to box: parse per the display mode (relative m:ss/mm:ss/h:mm:ss,
    /// or wallclock HH:mm:ss converted via the session's local start), seek (Playback.Seek
    /// clamps to [0, DurationMs]), and request the one-shot scroll. The now-playing highlight
    /// lands on the next 150 ms tick - nothing forces it here. Does not start playback.</summary>
    public void GoToTimestamp()
    {
        if (!TimestampParser.TryParse(GoToText, TimestampsMode, StartedAtLocal, out long ms))
        {
            GoToError = true;
            return;
        }
        GoToError = false;
        Playback.Seek(ms);
        int row = SectionAt(Playback.PositionMs);    // the CLAMPED position, not the raw parse
        if (row >= 0) GoToRowScrollRequested?.Invoke(row);
    }
```

- [ ] **Step 4: Run to verify PASS**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"`
Expected: PASS, including the three new facts.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ReadViewViewModel.cs tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs
git commit -m "feat(readview): go-to timestamp parse/seek/one-shot-scroll VM surface"
```

---

### Task 5: Go-to box XAML, Ctrl+G, Esc, error outline, one-shot scroll wiring

View-layer only — build gate + smoke-runbook additions.

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ReadViewWindow.xaml` (inside the elapsed/seek/total cluster, currently lines 223-237)
- Modify: `F:\LocalScribe\src\LocalScribe.App\ReadViewWindow.xaml.cs` (ctor subscription near line 146, `OnPreviewKeyDown` lines 286-295, new handlers, `OnClosed` unsubscribe lines 495-517)
- Modify: `F:\LocalScribe\docs\plans\2026-08-02-playback-transport-smoke-runbook.md` (fill Part B)

**Interfaces:**
- Consumes: `ReadViewViewModel.GoToText` / `GoToError` / `GoToTimestamp()` / `GoToRowScrollRequested` (Task 4); `ScrollRowToUpperThird(int)` (Task 2); `EditList` / `RowList` named elements.
- Produces: nothing later tasks rely on.

- [ ] **Step 1: Add the go-to cluster to the XAML**

In `ReadViewWindow.xaml`, inside the elapsed/seek/total `StackPanel` (currently lines 223-237), insert after the `DurationDisplay` TextBlock (line 236) and before the `</StackPanel>`:

```xaml
                    <!-- Item 8 go-to box (Ctrl+G): type a stamp the way the transcript shows it
                         (relative m:ss / h:mm:ss, or wallclock HH:mm:ss), Enter jumps. The error
                         state is a quiet red outline on this wrapper Border with the text
                         retained - never a dialog - and clears on the next keystroke (VM
                         OnGoToTextChanged). A wrapper Border rather than a TextBox style so no
                         assumption is made about Wpf.Ui's implicit TextBox template. -->
                    <TextBlock Text="Go to" VerticalAlignment="Center" Margin="12,0,4,0" Opacity="0.8" />
                    <Border CornerRadius="4" BorderThickness="1" VerticalAlignment="Center">
                        <Border.Style>
                            <Style TargetType="Border">
                                <Setter Property="BorderBrush" Value="Transparent" />
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding GoToError}" Value="True">
                                        <Setter Property="BorderBrush" Value="{DynamicResource SystemFillColorCriticalBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <TextBox x:Name="GoToBox" Width="72" VerticalContentAlignment="Center"
                                 Text="{Binding GoToText, UpdateSourceTrigger=PropertyChanged}"
                                 ToolTip="Jump to a time (Ctrl+G): type it the way the transcript shows it, then press Enter"
                                 PreviewKeyDown="OnGoToBoxPreviewKeyDown" />
                    </Border>
```

- [ ] **Step 2: Wire the code-behind**

In `ReadViewWindow.xaml.cs`:

(a) In the constructor, directly after `_vm.PropertyChanged += OnVmPropertyChanged;` (currently line 146), add:

```csharp
        // Item 8 one-shot go-to scroll. Per-session window that genuinely closes - OnClosed
        // MUST unsubscribe (house rule).
        _vm.GoToRowScrollRequested += OnGoToRowScrollRequested;
```

(b) Replace `OnPreviewKeyDown` (currently lines 286-295 — if the find-scroll plan changed the Ctrl+F body, keep its version of that branch and add only the Ctrl+G branch):

```csharp
    /// <summary>Ctrl+F opens the find bar; Ctrl+G focuses the go-to box (item 8). A window-level
    /// override rather than an InputBinding: KeyBindings sit outside the visual tree, where
    /// neither ElementName=Self nor the VM DataContext reliably resolves (the
    /// OnSegmentTextBoxPreviewKeyDown precedent).</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.F
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            _vm.OpenFind();
            e.Handled = true;
        }
        // Item 8: guarded on IsAvailable - the whole transport bar (and the box with it) is
        // collapsed when the session has no playable audio, and focusing a collapsed box no-ops
        // confusingly instead of doing nothing visibly.
        else if (e.Key == System.Windows.Input.Key.G
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
            && _vm.Playback.IsAvailable)
        {
            GoToBox.Focus();
            GoToBox.SelectAll();
            e.Handled = true;
        }
    }
```

(c) Add the two handlers after `OnFindBoxPreviewKeyDown` (currently ends line 308):

```csharp
    /// <summary>Enter commits the jump; Esc returns focus to the transcript list (design item
    /// 8). Code-behind on the box for the same reason as OnFindBoxPreviewKeyDown: it is a
    /// direct child, so the XAML compiler wires the handler.</summary>
    private void OnGoToBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            _vm.GoToTimestamp();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (_vm.IsEditMode) EditList.Focus(); else RowList.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Item 8 one-shot scroll for a committed go-to jump - deliberately NOT gated on
    /// the Sync toggle (spec: the jump scrolls "regardless of the Sync toggle"). Reuses the
    /// follow scroll's centering + programmatic guard so the item-7 nudge cannot fight the
    /// settling scroll.</summary>
    private void OnGoToRowScrollRequested(int index) => ScrollRowToUpperThird(index);
```

(d) In `OnClosed` (currently lines 495-517), directly after `_vm.PropertyChanged -= OnVmPropertyChanged;`, add:

```csharp
        _vm.GoToRowScrollRequested -= OnGoToRowScrollRequested;
```

- [ ] **Step 3: Build gate**

Close any running `LocalScribe.App.exe`, then run: `dotnet build LocalScribe.slnx`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Fill Part B of the smoke runbook**

In `docs\plans\2026-08-02-playback-transport-smoke-runbook.md`, replace the line `## Part B: Go-to timestamp box (item 8) - added by Task 5` with:

```markdown
## Part B: Go-to timestamp box (item 8)

- [ ] **B1 Placement + focus:** in the dual-leg session's read view, a "Go to" label and a
  small text box sit after the total-duration label in the transport bar. Press Ctrl+G from
  anywhere in the window - the box gets focus with any existing text selected.
- [ ] **B2 Relative jump:** with the timestamps setting on "relative", type a mid-transcript
  stamp exactly as a row label shows it (e.g. `03:15`) and press Enter - playback position
  and the seek slider jump there, the list scrolls to the target row (about one third from
  the top) even though Sync is OFF, and the row highlight lands within a beat.
- [ ] **B3 Sync state untouched:** repeat B2 once with Sync ON and once with Sync OFF - the
  pill's state is identical before and after the jump in both cases.
- [ ] **B4 Wallclock jump:** switch Settings > Timestamps to wall-clock, reopen the read
  view, type a stamp as displayed (HH:mm:ss) and press Enter - it lands on the matching row.
  Switch the setting back afterwards.
- [ ] **B5 Clamp:** type a stamp far past the end of the audio (e.g. `59:59`) - playback
  lands at end-of-media, scrolled to the last section; no error state.
- [ ] **B6 Quiet error:** type `garbage` and press Enter - the box gets a red outline, the
  text stays exactly as typed, NO dialog appears, and playback does not move. Type one more
  character - the red outline clears immediately.
- [ ] **B7 Esc:** press Esc in the box - focus returns to the transcript list (arrow keys
  now move the list selection). In Edit mode, Esc focuses the edit table instead.
```

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/ReadViewWindow.xaml.cs docs/plans/2026-08-02-playback-transport-smoke-runbook.md
git commit -m "feat(readview): go-to timestamp box with Ctrl+G, quiet error outline, one-shot scroll"
```

---

### Task 6: Leg-shape derivation on `PlaybackViewModel` (`HasBothLegs` / `HasSingleLeg` / `SingleLegVolume`)

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ViewModels\PlaybackViewModel.cs` (extend the volume partials at currently lines 230-233; new members directly after them)
- Test: `F:\LocalScribe\tests\LocalScribe.App.Tests\PlaybackViewModelTests.cs` (append before the `FakePlayer` nested class)

**Interfaces:**
- Consumes: existing `HasLocalLeg`/`HasRemoteLeg`/`LocalVolume`/`RemoteVolume` observables (`PlaybackViewModel.cs:50-55`); test harness `WriteAudio(string, SourceKind, AudioFormat)` (`PlaybackViewModelTests.cs:23-27`) and the `FakePlayer.Calls` log (`Vol:local:<v>` / `Vol:remote:<v>` strings, lines 591-592).
- Produces: `public bool HasBothLegs { get; }`, `public bool HasSingleLeg { get; }` (XOR — exactly one leg), `public double SingleLegVolume { get; set; }` (forwards to the lone leg) — Task 7's XAML binds all three.

- [ ] **Step 1: Write the failing tests**

Append to `PlaybackViewModelTests.cs`:

```csharp
    [Fact]
    public void Leg_shape_derives_single_vs_both_and_raises_change_notifications()
    {
        // Item 9 (UX round 2026-08-02): the mixer's visibility switch. Derived, not stored -
        // Resolve's HasLocalLeg/HasRemoteLeg stay the single source of truth.
        WriteAudio("s-shape", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Resolve(_paths, "s-shape", new[] { SourceKind.Local }, AudioFormat.Flac);
        Assert.True(vm.HasSingleLeg);
        Assert.False(vm.HasBothLegs);
        Assert.Contains(nameof(PlaybackViewModel.HasSingleLeg), raised);
        Assert.Contains(nameof(PlaybackViewModel.HasBothLegs), raised);

        WriteAudio("s-shape", SourceKind.Remote, AudioFormat.Flac);
        vm.Resolve(_paths, "s-shape", new[] { SourceKind.Local, SourceKind.Remote }, AudioFormat.Flac);
        Assert.True(vm.HasBothLegs);
        Assert.False(vm.HasSingleLeg);
    }

    [Fact]
    public void SingleLegVolume_forwards_to_whichever_lone_leg_exists()
    {
        WriteAudio("s-lone-r", SourceKind.Remote, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-lone-r", new[] { SourceKind.Remote }, AudioFormat.Flac);
        vm.SingleLegVolume = 0.25;
        Assert.Equal(0.25, vm.RemoteVolume);
        Assert.Equal(0.25, vm.SingleLegVolume);
        Assert.Contains("Vol:remote:0.25", _player.Calls);

        WriteAudio("s-lone-l", SourceKind.Local, AudioFormat.Flac);
        var vm2 = MakeVm();                          // shared _player: use a distinct volume
        vm2.Resolve(_paths, "s-lone-l", new[] { SourceKind.Local }, AudioFormat.Flac);
        vm2.SingleLegVolume = 0.5;
        Assert.Equal(0.5, vm2.LocalVolume);
        Assert.Contains("Vol:local:0.5", _player.Calls);
    }

    [Fact]
    public void SingleLegVolume_raises_when_the_underlying_leg_volume_changes()
    {
        // The TwoWay slider binding needs the echo: setting LocalVolume from anywhere must
        // re-publish SingleLegVolume or the lone slider would go stale.
        WriteAudio("s-vol-n", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-vol-n", new[] { SourceKind.Local }, AudioFormat.Flac);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.LocalVolume = 0.7;
        Assert.Contains(nameof(PlaybackViewModel.SingleLegVolume), raised);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~PlaybackViewModelTests"`
Expected: build FAILURE with `error CS1061: 'PlaybackViewModel' does not contain a definition for 'HasSingleLeg'` (and `HasBothLegs`/`SingleLegVolume`).

- [ ] **Step 3: Minimal implementation**

In `PlaybackViewModel.cs`, replace the two volume partials (currently lines 232-233):

```csharp
    partial void OnLocalVolumeChanged(double value)
    {
        _player.SetLegVolume(local: true, volume: value);
        OnPropertyChanged(nameof(SingleLegVolume));   // keep the lone-leg slider's echo fresh
    }

    partial void OnRemoteVolumeChanged(double value)
    {
        _player.SetLegVolume(local: false, volume: value);
        OnPropertyChanged(nameof(SingleLegVolume));
    }
```

then insert directly after them:

```csharp
    // ---- Item 9 (UX round 2026-08-02): contextual mixer shape --------------------------------

    /// <summary>Per-leg mute/volume only mean something when BOTH legs exist; the transport
    /// swaps to a single plain Volume slider otherwise. Derived (never stored) so Resolve's
    /// HasLocalLeg/HasRemoteLeg stay the single source of truth. No player-layer changes.</summary>
    public bool HasBothLegs => HasLocalLeg && HasRemoteLeg;

    /// <summary>EXACTLY one leg (XOR, not "at most one"): with no legs at all the whole
    /// transport is already hidden via IsAvailable, so this gates only the lone Volume slider.</summary>
    public bool HasSingleLeg => HasLocalLeg ^ HasRemoteLeg;

    /// <summary>The lone leg's volume, for the single-leg "Volume" slider - forwards to
    /// whichever leg exists so the XAML needs no leg-conditional binding. Meaningless (and
    /// unbound/collapsed) when both legs exist.</summary>
    public double SingleLegVolume
    {
        get => HasLocalLeg ? LocalVolume : RemoteVolume;
        set { if (HasLocalLeg) LocalVolume = value; else RemoteVolume = value; }
    }

    partial void OnHasLocalLegChanged(bool value) => RaiseLegShape();
    partial void OnHasRemoteLegChanged(bool value) => RaiseLegShape();

    /// <summary>Manual re-raise: the three derived members above have no backing
    /// [ObservableProperty], so leg flips must publish them explicitly.</summary>
    private void RaiseLegShape()
    {
        OnPropertyChanged(nameof(HasBothLegs));
        OnPropertyChanged(nameof(HasSingleLeg));
        OnPropertyChanged(nameof(SingleLegVolume));
    }
```

- [ ] **Step 4: Run to verify PASS**

Run: `dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~PlaybackViewModelTests"`
Expected: PASS, including the three new facts.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/PlaybackViewModel.cs tests/LocalScribe.App.Tests/PlaybackViewModelTests.cs
git commit -m "feat(playback): derive single- vs dual-leg mixer shape with lone-leg volume forwarding"
```

---

### Task 7: Contextual mixer XAML (single-leg Volume slider vs dual-leg "Channels" group)

View-layer only — build gate + smoke-runbook additions.

**Files:**
- Modify: `F:\LocalScribe\src\LocalScribe.App\ReadViewWindow.xaml` (replace the mute-pill cluster + both volume clusters, currently lines 238-299 — the comment block starting `<!-- ToggleButton does NOT derive from Button...` through the closing `</StackPanel>` of the "Remote vol" cluster; anchors will have shifted after Tasks 2/5, locate by the `Mute local` / `Remote vol` content)
- Modify: `F:\LocalScribe\docs\plans\2026-08-02-playback-transport-smoke-runbook.md` (fill Part C)

**Interfaces:**
- Consumes: `Playback.HasSingleLeg` / `HasBothLegs` / `SingleLegVolume` (Task 6); existing `Playback.LocalMuted` / `RemoteMuted` / `LocalVolume` / `RemoteVolume` bindings; `PillToggleButton` style; `BoolToVis` converter (window resource, `ReadViewWindow.xaml:11`).
- Produces: nothing later tasks rely on.

- [ ] **Step 1: Replace the mixer XAML**

Delete the three sibling `StackPanel`s that today hold the two mute pills (lines 246-287), the "Local vol" cluster (288-293) and the "Remote vol" cluster (294-299), together with the `<!-- ToggleButton does NOT derive from Button... -->` comment block above them (238-245). In their place (still inside the transport `WrapPanel`, after the elapsed/seek/total cluster):

```xaml
                <!-- Item 9 (UX round 2026-08-02) contextual mixer. Single-leg session (exactly
                     one leg on disk): muting or soloing the only channel is meaningless, so
                     there are no mute pills - just one plain "Volume" slider bound to the lone
                     leg via the VM's forwarding SingleLegVolume. Dual-leg session: a "Channels"
                     group of two labelled rows so the per-leg controls read as a playback
                     mixer, not stray recording controls. The LocalMuted/RemoteMuted/
                     LocalVolume/RemoteVolume bindings are byte-identical to the pre-regroup
                     markup - only grouping and visibility changed. ToggleButton does NOT derive
                     from Button, so the pills use the mirrored PillToggleButton style (not
                     PillButton); each icon-flip DataTrigger reads its own pill's IsChecked
                     (RelativeSource AncestorType=ToggleButton), so identical markup serves both
                     legs. -->
                <StackPanel Orientation="Horizontal" Margin="0,0,12,4"
                            Visibility="{Binding Playback.HasSingleLeg, Converter={StaticResource BoolToVis}}">
                    <TextBlock Text="Volume" VerticalAlignment="Center" Margin="0,0,4,0" />
                    <Slider Width="80" Minimum="0" Maximum="1" VerticalAlignment="Center"
                            Value="{Binding Playback.SingleLegVolume}" />
                </StackPanel>
                <Border Margin="0,0,12,4" Padding="8,4" CornerRadius="4" BorderThickness="1"
                        BorderBrush="{DynamicResource ControlElevationBorderBrush}"
                        Visibility="{Binding Playback.HasBothLegs, Converter={StaticResource BoolToVis}}">
                    <StackPanel>
                        <TextBlock Text="Channels" FontSize="11" Opacity="0.7" Margin="0,0,0,2" />
                        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
                            <TextBlock Text="Local (my side)" VerticalAlignment="Center" MinWidth="130" Margin="0,0,8,0" />
                            <ToggleButton Style="{StaticResource PillToggleButton}"
                                          IsChecked="{Binding Playback.LocalMuted}" Margin="0,0,8,0"
                                          ToolTip="Silence the local (my microphone) leg">
                                <StackPanel Orientation="Horizontal">
                                    <ui:SymbolIcon FontSize="18" Margin="0,0,6,0">
                                        <ui:SymbolIcon.Style>
                                            <Style TargetType="ui:SymbolIcon">
                                                <Setter Property="Symbol" Value="Mic24" />
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding IsChecked, RelativeSource={RelativeSource AncestorType=ToggleButton}}" Value="True">
                                                        <Setter Property="Symbol" Value="MicOff24" />
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </ui:SymbolIcon.Style>
                                    </ui:SymbolIcon>
                                    <TextBlock Text="Mute" VerticalAlignment="Center" />
                                </StackPanel>
                            </ToggleButton>
                            <Slider Width="80" Minimum="0" Maximum="1" VerticalAlignment="Center"
                                    Value="{Binding Playback.LocalVolume}" />
                        </StackPanel>
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="Remote (other party)" VerticalAlignment="Center" MinWidth="130" Margin="0,0,8,0" />
                            <ToggleButton Style="{StaticResource PillToggleButton}"
                                          IsChecked="{Binding Playback.RemoteMuted}" Margin="0,0,8,0"
                                          ToolTip="Silence the remote (other party) leg">
                                <StackPanel Orientation="Horizontal">
                                    <ui:SymbolIcon FontSize="18" Margin="0,0,6,0">
                                        <ui:SymbolIcon.Style>
                                            <Style TargetType="ui:SymbolIcon">
                                                <Setter Property="Symbol" Value="Mic24" />
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding IsChecked, RelativeSource={RelativeSource AncestorType=ToggleButton}}" Value="True">
                                                        <Setter Property="Symbol" Value="MicOff24" />
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </ui:SymbolIcon.Style>
                                    </ui:SymbolIcon>
                                    <TextBlock Text="Mute" VerticalAlignment="Center" />
                                </StackPanel>
                            </ToggleButton>
                            <Slider Width="80" Minimum="0" Maximum="1" VerticalAlignment="Center"
                                    Value="{Binding Playback.RemoteVolume}" />
                        </StackPanel>
                    </StackPanel>
                </Border>
```

- [ ] **Step 2: Build gate**

Close any running `LocalScribe.App.exe`, then run: `dotnet build LocalScribe.slnx`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 3: Fill Part C of the smoke runbook**

In `docs\plans\2026-08-02-playback-transport-smoke-runbook.md`, replace the line `## Part C: Contextual channel mixer (item 9) - added by Task 7` with:

```markdown
## Part C: Contextual channel mixer (item 9)

- [ ] **C1 Dual-leg shape:** open the dual-leg session's read view. The transport shows a
  "Channels" group with two rows labelled "Local (my side)" and "Remote (other party)", each
  with a Mute pill and a volume slider. The old free-floating "Mute local"/"Local vol"
  clusters are gone.
- [ ] **C2 Dual-leg function:** during playback, toggle each Mute pill (icon flips to the
  crossed-out mic, pill fills accent) and drag each slider - the corresponding leg silences /
  changes level independently; the other leg is unaffected.
- [ ] **C3 Single-leg shape:** open the single-leg session's read view. NO mute pills appear
  anywhere in the transport; there is exactly one slider, labelled "Volume".
- [ ] **C4 Single-leg function:** during playback, drag the Volume slider to near zero and
  back - the audio level follows.
- [ ] **C5 No-audio session:** open a session with no retained audio - the entire transport
  bar (including mixer and go-to box) stays hidden, exactly as before this round.
- [ ] **C6 Narrow-window wrap:** narrow the window until the transport wraps - the Channels
  group wraps as one unit (its rows stay intact); nothing clips off the window edge.
```

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.App/ReadViewWindow.xaml docs/plans/2026-08-02-playback-transport-smoke-runbook.md
git commit -m "feat(readview): contextual channel mixer - Channels group for dual-leg, lone Volume for single-leg"
```

---

### Task 8: Full-suite regression gate

**Files:**
- Modify: none expected — fix any regression the suites surface (most likely candidates: `PlaybackViewModelTests` volume-call assertions if the `SingleLegVolume` echo changed call ordering, or `ReadViewViewModelTests` fixtures).
- Test: both entire suites.

**Interfaces:**
- Consumes: everything above.
- Produces: a green branch.

- [ ] **Step 1: Run the full App suite**

Close any running `LocalScribe.App.exe`, then run: `dotnet test tests\LocalScribe.App.Tests`
Expected: PASS, 0 failures. (This plan adds 9 App facts on top of whatever count the branch had when this plan started — the find-scroll plan runs first and adds its own; the number that matters is failures: 0.)

- [ ] **Step 2: Run the full Core suite**

Run: `dotnet test tests\LocalScribe.Core.Tests`
Expected: PASS, 0 failures (baseline 1015 plus the new `TimestampParserTests`).

- [ ] **Step 3: Build the whole solution once more**

Run: `dotnet build LocalScribe.slnx`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Verify docs are complete**

- `docs\plans\2026-08-02-playback-transport-smoke-runbook.md` has Prep + Parts A, B and C fully populated (no `added by Task N` placeholders remain).
- No `docs/specs/localscribe-specs.md` amendment is required for items 7-9 — the spec's Cross-cutting section assigns the only §11.2 amendment of this round to items 5+6 (the export plan).

- [ ] **Step 5: Commit any regression fixes**

Only if Step 1/2 forced changes:

```bash
git add -u
git commit -m "test(readview): fix regressions surfaced by the playback-transport full-suite gate"
```
