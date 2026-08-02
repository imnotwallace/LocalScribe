# Read-view per-segment navigation & timestamps — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make each segment of a merged speaker turn individually addressable in the read view — hover shows its `[mm:ss]`, double-click seeks to *that* segment, and the now-playing highlight tracks the exact segment under the playhead — without breaking flowing prose.

**Architecture:** Core/projection is untouched (`DisplayRow.Segments` already carries each `RowSegment`). App adds a `ReadSegment` observable wrapper, a `ReadRow.Segments` projection, VM logic (`SegmentAt` + `SeekSegment` + segment-level now-playing in `TickPlayback`), a thin `SegmentText` attached behavior that builds one interactive `Run` per segment, and a window-level `SeekSegmentCommand` reached from the item template via the existing `WindowProxy`.

**Tech Stack:** .NET 10, WPF, WPF-UI (FluentWindow), CommunityToolkit.Mvvm, xUnit.

## Global Constraints

- Core stays untouched — `DisplayRow`, `RowSegment`, projection output, and the file renderers do not change. This is the evidentiary-projection invariant.
- The 824 `LocalScribe.App.Tests` and 102 Core Assistant tests must stay green.
- XamlHygiene: theme resources only (`SystemAccentColor` etc.), never ARGB literals.
- No schema change, no migration, no Diarizer republish.
- Solution is `LocalScribe.slnx`. Build: `dotnet build LocalScribe.slnx -c Debug`. Test App project: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj`.
- If `LocalScribe.App.exe` is running it locks `bin\` (build fails MSB3027). Close it TARGETED (CloseMainWindow then Stop-Process by pid) — never a broad kill of dotnet/app/npm.
- Commit each task on `feat/smoke-followups-2026-07-31`.
- Interaction assumption (confirmed in brainstorming): the per-segment seek gesture is **double-click** (consistent with today's row double-click). Single-click still selects the row for its context menu.

---

### Task 1: `ReadSegment` wrapper + `ReadRow.Segments`

**Files:**
- Create: `src/LocalScribe.App/ViewModels/ReadSegment.cs`
- Modify: `src/LocalScribe.App/ViewModels/ReadRow.cs`
- Test: `tests/LocalScribe.App.Tests/ReadViewSegmentTests.cs` (new)

**Interfaces:**
- Consumes: `LocalScribe.Core.Projection.RowSegment` (`Seq, Source, StartMs, EndMs, ProjectedText, RawText, IsCorrected, IsPinned, IsSplitChild, PartIndex`), `DisplayRow.Segments`.
- Produces: `ReadSegment` (`RowSegment Data`, `bool IsNowPlaying`, `long StartMs`, `long EndMs`, `string Text`, `bool IsEstimatedStart`); `ReadRow.Segments` → `IReadOnlyList<ReadSegment>` (empty for markers / payload-less rows).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.App.Tests/ReadViewSegmentTests.cs
using System.Linq;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ReadViewSegmentTests
{
    private static RowSegment Seg(int seq, long start, long end, string text, bool split = false) =>
        new(seq, TranscriptSource.Local, start, end, text, text, IsCorrected: false, IsPinned: false,
            IsSplitChild: split);

    [Fact]
    public void ReadRow_maps_DisplayRow_segments_to_ReadSegments_in_order()
    {
        var row = new ReadRow(new DisplayRow
        {
            StartMs = 130208, EndMs = 143104, DisplayName = "Christine", Text = "a b c",
            Segments = new[] { Seg(25, 130208, 136320, "a"), Seg(27, 138720, 143104, "b", split: true) },
        });

        Assert.Equal(2, row.Segments.Count);
        Assert.Equal(130208, row.Segments[0].StartMs);
        Assert.Equal("a", row.Segments[0].Text);
        Assert.False(row.Segments[0].IsEstimatedStart);
        Assert.Equal(27, row.Segments[1].Data.Seq);
        Assert.True(row.Segments[1].IsEstimatedStart);   // split child carries an estimated start
    }

    [Fact]
    public void ReadRow_marker_has_no_segments()
    {
        var row = new ReadRow(new DisplayRow { IsMarker = true, StartMs = 0, EndMs = 0, Text = "marker" });
        Assert.Empty(row.Segments);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter ReadViewSegmentTests`
Expected: FAIL — `ReadRow` has no `Segments` member (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `src/LocalScribe.App/ViewModels/ReadSegment.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.Core.Projection;
namespace LocalScribe.App.ViewModels;

/// <summary>Per-segment wrapper around a Core <see cref="RowSegment"/> for the read view's prose
/// inlines (ITEM 5, 2026-08-01). Adds a moving IsNowPlaying flag the SegmentText behavior tints on
/// the exact segment under the playhead - the same decoupled-from-selection pattern as
/// <see cref="ReadRow.IsNowPlaying"/>. IsEstimatedStart is true for a split child, whose start is a
/// character-proportion estimate, never a real token time. RowSegment stays untouched (canonical
/// projection payload).</summary>
public sealed partial class ReadSegment : ObservableObject
{
    public RowSegment Data { get; }
    [ObservableProperty] private bool _isNowPlaying;

    public long StartMs => Data.StartMs;
    public long EndMs => Data.EndMs;
    public string Text => Data.ProjectedText;
    public bool IsEstimatedStart => Data.IsSplitChild;

    public ReadSegment(RowSegment data) => Data = data;
}
```

Modify `src/LocalScribe.App/ViewModels/ReadRow.cs` — add usings and the `Segments` projection, expand the ctor:

```csharp
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.Core.Projection;
```

```csharp
    /// <summary>The turn's constituent segments as observable wrappers (ITEM 5): the read view
    /// renders one interactive inline per entry (hover time, double-click seek, per-segment
    /// now-playing tint). Empty for markers and payload-less (live) rows. Built once here; rows are
    /// replaced wholesale on every (re)load, never mutated in place.</summary>
    public IReadOnlyList<ReadSegment> Segments { get; }

    public ReadRow(DisplayRow data)
    {
        Data = data;
        Segments = data.Segments.Select(s => new ReadSegment(s)).ToArray();
    }
```

(Delete the old `public ReadRow(DisplayRow data) => Data = data;` one-liner.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter ReadViewSegmentTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ReadSegment.cs src/LocalScribe.App/ViewModels/ReadRow.cs tests/LocalScribe.App.Tests/ReadViewSegmentTests.cs
git commit -m "feat(readview): ReadSegment wrapper + ReadRow.Segments (ITEM 5)"
```

---

### Task 2: `SeekSegment` on the VM

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs` (near `JumpToSection`, ~line 170)
- Test: `tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs`

**Interfaces:**
- Consumes: `Playback.Seek(long)`, `Playback.IsPlaying`, `Playback.PlayPauseCommand` (as `JumpToSection` does).
- Produces: `void ReadViewViewModel.SeekSegment(long startMs)` — seeks to `startMs` and starts playback if paused.

- [ ] **Step 1: Write the failing test** (add to `ReadViewViewModelTests`)

```csharp
    [Fact]
    public void SeekSegment_seeks_to_the_given_ms_and_starts_playback()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0, EndMs = 4200, DisplayName = "Sam", Text = "a" }));

        vm.SeekSegment(138720);
        Assert.Equal(138720, vm.Playback.PositionMs);
        Assert.True(vm.Playback.IsPlaying);

        vm.SeekSegment(130208);                  // a second seek while playing stays playing, moves position
        Assert.Equal(130208, vm.Playback.PositionMs);
        Assert.True(vm.Playback.IsPlaying);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter SeekSegment_seeks`
Expected: FAIL — `SeekSegment` not defined.

- [ ] **Step 3: Write minimal implementation** (add directly after `JumpToSection` in `ReadViewViewModel.cs`)

```csharp
    /// <summary>Per-segment click-to-jump (ITEM 5): seek to a specific segment's start and begin
    /// playing. Mirrors <see cref="JumpToSection"/> but takes an absolute ms so the read view can
    /// target any inline within a merged turn, not only the turn's first segment.</summary>
    public void SeekSegment(long startMs)
    {
        Playback.Seek(startMs);
        if (!Playback.IsPlaying) Playback.PlayPauseCommand.Execute(null);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter SeekSegment_seeks`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ReadViewViewModel.cs tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs
git commit -m "feat(readview): SeekSegment(ms) VM entry point (ITEM 5)"
```

---

### Task 3: Segment-level now-playing in `TickPlayback`

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs` (`TickPlayback` ~line 151; add fields + `SegmentAt` + `UpdatePlayingSegment`)
- Test: `tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs`

**Interfaces:**
- Consumes: `Rows[i].Segments` (Task 1), `Playback.PositionMs`, existing `SectionAt`.
- Produces: after each `TickPlayback()`, exactly one `ReadSegment.IsNowPlaying == true` — the segment whose window `[StartMs, nextSegStartMs)` (last segment: through `EndMs`, and the trailing intra-row gap) contains the position — and it clears when the playing row changes.

- [ ] **Step 1: Write the failing test** (add to `ReadViewViewModelTests`)

```csharp
    [Fact]
    public void PlayingSegment_IsNowPlaying_follows_position_within_a_turn_and_clears_across_rows()
    {
        var vm = MakeVm();
        // One merged Christine turn (2 segments) then a Nel turn (1 segment).
        RowSegment S(int seq, long a, long b) =>
            new(seq, TranscriptSource.Local, a, b, "t", "t", false, false);
        vm.Rows.Add(new ReadRow(new DisplayRow
        {
            StartMs = 130208, EndMs = 143104, DisplayName = "Christine", Text = "a b",
            Segments = new[] { S(25, 130208, 136320), S(27, 138720, 143104) },
        }));
        vm.Rows.Add(new ReadRow(new DisplayRow
        {
            StartMs = 150000, EndMs = 152000, DisplayName = "Nel", Text = "c",
            Segments = new[] { S(30, 150000, 152000) },
        }));

        _player.PositionMs = 131000; vm.TickPlayback();             // inside seg 25
        Assert.True(vm.Rows[0].Segments[0].IsNowPlaying);
        Assert.False(vm.Rows[0].Segments[1].IsNowPlaying);

        _player.PositionMs = 137000; vm.TickPlayback();             // intra-turn gap (136320..138720): holds seg 25
        Assert.True(vm.Rows[0].Segments[0].IsNowPlaying);
        Assert.False(vm.Rows[0].Segments[1].IsNowPlaying);

        _player.PositionMs = 139000; vm.TickPlayback();             // inside seg 27
        Assert.False(vm.Rows[0].Segments[0].IsNowPlaying);
        Assert.True(vm.Rows[0].Segments[1].IsNowPlaying);

        _player.PositionMs = 151000; vm.TickPlayback();             // moved to the Nel turn
        Assert.False(vm.Rows[0].Segments[0].IsNowPlaying);
        Assert.False(vm.Rows[0].Segments[1].IsNowPlaying);          // prior turn's segment cleared
        Assert.True(vm.Rows[1].Segments[0].IsNowPlaying);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter PlayingSegment_IsNowPlaying`
Expected: FAIL — `IsNowPlaying` never set on any `ReadSegment` (all false).

- [ ] **Step 3: Write minimal implementation** in `ReadViewViewModel.cs`.

Add fields next to `_nowPlayingRowIndex` (~line 110):

```csharp
    // ITEM 5: the precise "now playing" cursor at SEGMENT granularity, (rowIndex, segIndex). Kept
    // alongside the row-level _nowPlayingRowIndex so the row can still drive scroll-into-view while
    // the visible tint lands on the exact segment under the playhead.
    private int _nowPlayingSegRow = -1;
    private int _nowPlayingSegIndex = -1;
```

Update `TickPlayback` to also resolve the segment:

```csharp
    public void TickPlayback()
    {
        Playback.Tick();
        PlayingSectionIndex = SectionAt(Playback.PositionMs);
        UpdatePlayingSegment(PlayingSectionIndex, Playback.PositionMs);
    }
```

Add both helpers below `SectionAt`:

```csharp
    /// <summary>The segment within a row whose window contains <paramref name="positionMs"/>, using
    /// the same greatest-match-wins-at-a-boundary rule as <see cref="SectionAt"/>: each segment owns
    /// [StartMs, nextSegStartMs); the last segment runs through its EndMs, and a position past the
    /// last EndMs (the trailing intra-row gap before the next turn) holds the last segment so the
    /// highlight does not flicker off. -1 when the row has no segments.</summary>
    private int SegmentAt(int rowIndex, long positionMs)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count) return -1;
        var segs = Rows[rowIndex].Segments;
        if (segs.Count == 0) return -1;
        int idx = -1;
        for (int i = 0; i < segs.Count; i++)
        {
            long start = segs[i].StartMs;
            long end = i + 1 < segs.Count ? segs[i + 1].StartMs : segs[i].EndMs;
            if (positionMs >= start && positionMs <= end) idx = i;
        }
        if (idx < 0 && positionMs > segs[^1].EndMs) idx = segs.Count - 1;
        return idx;
    }

    /// <summary>Moves the single per-segment IsNowPlaying flag to the segment under the playhead,
    /// clearing the previously-lit one (including when the playing row changed). O(1) via the
    /// (row, seg) cursor - no scan.</summary>
    private void UpdatePlayingSegment(int rowIndex, long positionMs)
    {
        int segIndex = SegmentAt(rowIndex, positionMs);
        if (rowIndex == _nowPlayingSegRow && segIndex == _nowPlayingSegIndex) return;

        if (_nowPlayingSegRow >= 0 && _nowPlayingSegRow < Rows.Count)
        {
            var prev = Rows[_nowPlayingSegRow].Segments;
            if (_nowPlayingSegIndex >= 0 && _nowPlayingSegIndex < prev.Count)
                prev[_nowPlayingSegIndex].IsNowPlaying = false;
        }
        if (rowIndex >= 0 && rowIndex < Rows.Count && segIndex >= 0)
        {
            var cur = Rows[rowIndex].Segments;
            if (segIndex < cur.Count) cur[segIndex].IsNowPlaying = true;
        }
        _nowPlayingSegRow = rowIndex;
        _nowPlayingSegIndex = segIndex;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter PlayingSegment_IsNowPlaying`
Expected: PASS. Then run the whole file to confirm no regression:
`dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter ReadViewViewModelTests`

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ReadViewViewModel.cs tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs
git commit -m "feat(readview): segment-level now-playing cursor in TickPlayback (ITEM 5)"
```

---

### Task 4: `SegmentText` attached behavior (view; compile + smoke)

**Files:**
- Create: `src/LocalScribe.App/SegmentText.cs`

**Interfaces:**
- Consumes: `ReadRow.Segments` (`IReadOnlyList<ReadSegment>`), `ReadSegment.IsNowPlaying/StartMs/Text/IsEstimatedStart`, `LocalScribe.Core.Projection.TimestampFormat.Stamp`, an `ICommand` taking a boxed `long` (Task 5's `SeekSegmentCommand`).
- Produces: attached properties `SegmentText.Segments`, `SegmentText.FallbackText`, `SegmentText.SeekCommand` on a `TextBlock`; builds one interactive `Run` per segment (else a single plain `Run` of `FallbackText`).

> No unit test — this is view glue. It must COMPILE and is verified by the runtime smoke in Task 5. Logic that can be tested lives in the VM (Tasks 1-3).

- [ ] **Step 1: Create the behavior**

```csharp
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Projection;
namespace LocalScribe.App;

/// <summary>Attached behavior that renders a read-view speaker turn as one interactive inline per
/// segment (ITEM 5, 2026-08-01): hover shows the segment's [mm:ss], double-click seeks to it, and
/// the segment under the playhead is tinted. Owns the target TextBlock's Inlines. Empty/null
/// Segments -> a single plain Run of FallbackText (markers, live rows), preserving today's look.
/// Recycling-safe: rebuilds and re-subscribes whenever Segments/FallbackText change (ListView
/// container reuse re-sets the bindings), tearing down old PropertyChanged handlers first.</summary>
public static class SegmentText
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<ReadSegment>), typeof(SegmentText),
        new PropertyMetadata(null, OnChanged));
    public static void SetSegments(DependencyObject o, IReadOnlyList<ReadSegment>? v) => o.SetValue(SegmentsProperty, v);
    public static IReadOnlyList<ReadSegment>? GetSegments(DependencyObject o) => (IReadOnlyList<ReadSegment>?)o.GetValue(SegmentsProperty);

    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.RegisterAttached(
        "FallbackText", typeof(string), typeof(SegmentText), new PropertyMetadata(null, OnChanged));
    public static void SetFallbackText(DependencyObject o, string? v) => o.SetValue(FallbackTextProperty, v);
    public static string? GetFallbackText(DependencyObject o) => (string?)o.GetValue(FallbackTextProperty);

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.RegisterAttached(
        "SeekCommand", typeof(ICommand), typeof(SegmentText), new PropertyMetadata(null));
    public static void SetSeekCommand(DependencyObject o, ICommand? v) => o.SetValue(SeekCommandProperty, v);
    public static ICommand? GetSeekCommand(DependencyObject o) => (ICommand?)o.GetValue(SeekCommandProperty);

    private sealed class Bindings
    {
        public readonly List<(ReadSegment Seg, Run Run, PropertyChangedEventHandler Handler)> Items = new();
    }
    private static readonly ConditionalWeakTable<TextBlock, Bindings> _state = new();

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock tb) Rebuild(tb);
    }

    private static void Rebuild(TextBlock tb)
    {
        if (_state.TryGetValue(tb, out var old))
        {
            foreach (var (seg, _, handler) in old.Items) seg.PropertyChanged -= handler;
            _state.Remove(tb);
        }
        tb.Inlines.Clear();

        var segments = GetSegments(tb);
        if (segments is null || segments.Count == 0)
        {
            tb.Inlines.Add(new Run(GetFallbackText(tb) ?? string.Empty));
            return;
        }

        var brush = NowPlayingBrush();
        var bindings = new Bindings();
        foreach (var seg in segments)
        {
            var run = new Run(seg.Text + " ") { Cursor = Cursors.Hand };
            string stamp = TimestampFormat.Stamp(seg.StartMs, "relative", default);
            run.ToolTip = seg.IsEstimatedStart ? $"~[{stamp}] (estimated)" : $"[{stamp}]";
            if (seg.IsNowPlaying) run.Background = brush;

            var captured = seg;
            var capturedRun = run;
            // Preview (tunneling) so this beats the ListViewItem's own double-click (JumpToSection).
            run.PreviewMouseLeftButtonDown += (_, args) =>
            {
                if (args.ClickCount != 2) return;
                var cmd = GetSeekCommand(tb);
                if (cmd is not null && cmd.CanExecute(captured.StartMs)) cmd.Execute(captured.StartMs);
                args.Handled = true;
            };
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName == nameof(ReadSegment.IsNowPlaying))
                    capturedRun.Background = captured.IsNowPlaying ? brush : null;
            };
            captured.PropertyChanged += handler;

            tb.Inlines.Add(run);
            bindings.Items.Add((captured, capturedRun, handler));
        }
        _state.Add(tb, bindings);
    }

    // Theme accent at the same hue as the row's now-playing trigger, built per rebuild so it tracks
    // the current theme at container-realization time. XamlHygiene: color comes from the resource,
    // never an ARGB literal.
    private static Brush NowPlayingBrush()
    {
        if (Application.Current?.TryFindResource("SystemAccentColor") is Color c)
            return new SolidColorBrush(c) { Opacity = 0.40 };
        return Brushes.Transparent;
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/LocalScribe.App/LocalScribe.App.csproj -c Debug`
Expected: 0 errors. (If MSB3027, close a running LocalScribe.App.exe TARGETED first.)

- [ ] **Step 3: Commit**

```bash
git add src/LocalScribe.App/SegmentText.cs
git commit -m "feat(readview): SegmentText attached behavior for per-segment inlines (ITEM 5)"
```

---

### Task 5: Wire the read-view template + window `SeekSegmentCommand` (view; smoke)

**Files:**
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml.cs` (add the command near the other `WindowProxy` commands, ~line 71; construct it in the ctor, ~line 102)
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml` (the paragraph `TextBlock` in the read `ItemTemplate`, ~line 412; confirm `xmlns:local`)

**Interfaces:**
- Consumes: `ReadViewViewModel.SeekSegment(long)` (Task 2), `ReadRow.Segments` (Task 1), `SegmentText.*` (Task 4).
- Produces: `ReadViewWindow.SeekSegmentCommand` (`IRelayCommand<long>`) reachable from the item template via `{Binding Data.SeekSegmentCommand, Source={StaticResource WindowProxy}}`.

> View wiring — verified by smoke, no unit test.

- [ ] **Step 1: Add the window command.** In `ReadViewWindow.xaml.cs`, beside the other proxy commands (~line 71-74):

```csharp
    /// <summary>ITEM 5: per-segment seek from a read-view inline. On the window (like the other
    /// WindowProxy commands) so the item template can reach it; forwards to the WPF-free VM. Takes
    /// the segment's absolute start ms (boxed long) the SegmentText behavior passes.</summary>
    public IRelayCommand<long> SeekSegmentCommand { get; }
```

In the ctor, beside `CorrectTextCommand = ...` (~line 102), using the `vm` parameter (the `_vm` field is not yet assigned there — same rule as the edit/find commands):

```csharp
        SeekSegmentCommand = new RelayCommand<long>(vm.SeekSegment);
```

Confirm `using CommunityToolkit.Mvvm.Input;` is present (it is — the file already uses `AsyncRelayCommand`/`RelayCommand`).

- [ ] **Step 2: Rewire the paragraph TextBlock.** In `ReadViewWindow.xaml`, replace the read-template body paragraph (currently `<TextBlock Text="{Binding Data.Text, Mode=OneWay}" ...>` at ~line 412) so the behavior owns its content. Keep the marker italic style:

```xml
<TextBlock TextWrapping="Wrap" Margin="0,2,0,0"
           local:SegmentText.Segments="{Binding Segments, Mode=OneWay}"
           local:SegmentText.FallbackText="{Binding Data.Text, Mode=OneWay}"
           local:SegmentText.SeekCommand="{Binding Data.SeekSegmentCommand, Source={StaticResource WindowProxy}}">
    <TextBlock.Style>
        <Style TargetType="TextBlock">
            <Style.Triggers>
                <DataTrigger Binding="{Binding Data.IsMarker}" Value="True">
                    <Setter Property="FontStyle" Value="Italic" />
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </TextBlock.Style>
</TextBlock>
```

Confirm the header declares `xmlns:local="clr-namespace:LocalScribe.App"` (it must — `local:BindingProxy` is already used at line 23). If the local xmlns uses a different prefix, use that prefix for `SegmentText`.

- [ ] **Step 3: Build**

Run: `dotnet build LocalScribe.slnx -c Debug`
Expected: 0/0. (Close a running app TARGETED if MSB3027.)

- [ ] **Step 4: Full App test run (no regressions)**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj`
Expected: all pass (824 prior + 5 new = 829).

- [ ] **Step 5: Runtime smoke (user-driven).** Launch the app, open `2026-03-19_0736_Manual_test-case-data-2`, and confirm on the merged Christine turn:
  - Hovering a sentence shows its `[mm:ss]` (split children read `~[mm:ss] (estimated)`).
  - Double-clicking the `seq 27` sentence seeks to 02:18 (its own start), not 02:10 (the block start).
  - During playback the tint tracks the exact sentence under the playhead.
  - Prose still wraps normally; markers and any live/no-segment rows render unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/ReadViewWindow.xaml.cs src/LocalScribe.App/ReadViewWindow.xaml
git commit -m "feat(readview): wire per-segment inlines + SeekSegmentCommand into read view (ITEM 5)"
```

---

## Self-Review

**Spec coverage:**
- "See — hover shows its `[mm:ss]`" → Task 4 (Run.ToolTip). ✓
- "Navigate — double-click seeks that segment" → Task 2 (`SeekSegment`) + Task 4 (Preview double-click) + Task 5 (`SeekSegmentCommand` wiring). ✓
- "Track — highlight follows the exact segment" → Task 3 (`SegmentAt`/`UpdatePlayingSegment`) + Task 4 (Background toggle). ✓
- "Core untouched" → only App files + tests touched. ✓
- "Empty segments fall back to plain text" → Task 4 (FallbackText branch). ✓
- "Split children show estimated tooltip" → Task 1 (`IsEstimatedStart`) + Task 4 (`~[mm:ss] (estimated)`). ✓
- "824 App.Tests + 102 Assistant tests stay green" → Task 5 Step 4 full run; Tasks 1-3 add 5 tests. ✓

**Placeholder scan:** none — every step has concrete code / exact commands.

**Type consistency:** `SeekSegment(long)` defined in Task 2 is consumed by `RelayCommand<long>(vm.SeekSegment)` in Task 5 and invoked as `cmd.Execute(captured.StartMs)` (long→boxed) in Task 4. `ReadRow.Segments : IReadOnlyList<ReadSegment>` (Task 1) is consumed as `SegmentText.Segments` (Task 4) and indexed in `SegmentAt`/`UpdatePlayingSegment` (Task 3). `ReadSegment.IsNowPlaying/StartMs/Text/IsEstimatedStart` (Task 1) are the members used in Tasks 3-4. Consistent.

## Notes / known follow-ups (out of scope here)

- Tooltip is the **relative** `[mm:ss]` even when the header is in wallclock mode; offset-from-start is unambiguous and matches the workflow session. Threading `TimestampsMode`/`StartedAtLocal` into the behavior is a small follow-up if wanted.
- Split-child seek lands on the **estimated** char-proportion start; a later refinement could seek to the parent machine segment's real start (the latent split-interpolation item from the ITEM 5 investigation).
- The now-playing tint stacks on the row-level `IsNowPlaying` tint (turn faintly lit + exact segment brighter). If a single cue is preferred, drop the row `DataTrigger` at `ReadViewWindow.xaml:363-369`.
