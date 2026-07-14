using System.IO;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class TranscriptLinesViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-lv-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Rebuild_groups_same_speaker_within_gap_and_splits_on_silence()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new TranscriptLinesViewModel(controller, new FakeSettingsService(), a => a());

        var view = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "one", "Me"),
            TranscriptLine.Segment(1, TranscriptSource.Local, 1500, 2500, "two", "Me"),    // gap 500 -> merge
            TranscriptLine.Segment(2, TranscriptSource.Local, 9000, 10000, "later", "Me"), // gap 6500 -> split
        };
        vm.RebuildFrom(view, gapMs: 5000);

        Assert.Equal(2, vm.Lines.Count);
        Assert.Equal("one two", vm.Lines[0].Text);
        Assert.Equal("Me", vm.Lines[0].Speaker);
        Assert.Equal("later", vm.Lines[1].Text);
    }

    [Fact]
    public async Task Lines_arrive_at_merger_sorted_positions_and_format()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new TranscriptLinesViewModel(controller, new FakeSettingsService(), a => a());

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await controller.StopAsync(CancellationToken.None);
        await controller.PendingFinalize;                        // segments now reach LineInserted via the background drain

        Assert.Equal(2, vm.Lines.Count(l => !l.IsMarker));       // one segment per source
        var first = vm.Lines[0];
        Assert.Matches(@"^\d{2}:\d{2}$", first.Timestamp);
        Assert.Contains(first.Speaker, new[] { "Me", "Them" });
        Assert.NotEqual("", first.Text);
    }

    [Fact]
    public async Task New_session_clears_previous_lines()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new TranscriptLinesViewModel(controller, new FakeSettingsService(), a => a());

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await controller.StopAsync(CancellationToken.None);
        await controller.PendingFinalize;                        // first session's segments land via the background drain
        int afterFirst = vm.Lines.Count;
        Assert.True(afterFirst > 0);

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await controller.StopAsync(CancellationToken.None);
        await controller.PendingFinalize;                        // second session refills after the background drain
        Assert.Equal(afterFirst, vm.Lines.Count);                // cleared, then refilled
    }

    // NOTE: the plan's original third test ("Out_of_range_insert_clamps_to_append") only
    // exercised Clear() on an empty list - there is no public seam to inject an out-of-range
    // index (the merger always hands the VM its own real insert position), so that assertion
    // was checking nothing beyond "Clear empties an already-empty list". Replaced per task
    // instruction with a test that drives a genuine marker line through the real controller
    // (Pause/Resume emit TranscriptKind.Marker lines - see SessionController.PauseAsync/
    // ResumeAsync) and asserts the mapping this VM is actually responsible for: IsMarker=true
    // and the mm:ss StartMs formatting, landing at the merger-sorted position alongside the
    // segment lines.
    [Fact]
    public async Task Marker_line_maps_with_IsMarker_true_and_mmss_format()
    {
        var (controller, _, _, clock) = LiveTestDoubles.MakeController(_root);
        var vm = new TranscriptLinesViewModel(controller, new FakeSettingsService(), a => a());

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 2000;
        await controller.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 3000;
        await controller.ResumeAsync(CancellationToken.None);
        await controller.StopAsync(CancellationToken.None);
        await controller.PendingFinalize;                        // segment + marker lines settle after the background drain

        var markers = vm.Lines.Where(l => l.IsMarker).ToList();
        Assert.NotEmpty(markers);
        Assert.All(vm.Lines, l => Assert.Matches(@"^\d{2}:\d{2}$", l.Timestamp));
        Assert.Contains(markers, m => m.Timestamp == "00:02");  // PausedByUser at clock=2000ms
        Assert.All(markers, m => Assert.Equal("", m.Speaker));  // markers carry no speaker label
    }

    [Fact]
    public async Task Listening_hint_shows_only_while_recording_with_no_lines()
    {
        // Design 2026-07-13 section 5 item 1. GatedEngineFactory holds the engine build closed, so
        // no transcript line can land while the gate is shut - the hint window is observable and
        // deterministic (markers would also clear it, but a clean per-process fake Start writes none).
        var gated = new GatedEngineFactory();
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var vm = new TranscriptLinesViewModel(controller, new FakeSettingsService(), a => a());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(vm.ShowListeningHint);                       // Idle: never shown

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.True(vm.ShowListeningHint);                        // Recording + zero lines
        Assert.Contains(nameof(TranscriptLinesViewModel.ShowListeningHint), raised);

        gated.CreateGate.Set();                                   // release transcription
        Assert.True(SpinWait.SpinUntil(() => vm.Lines.Count > 0, TimeSpan.FromSeconds(5)),
            "no transcript line ever arrived");
        Assert.False(vm.ShowListeningHint);                       // dropped at the FIRST line

        await controller.StopAsync(CancellationToken.None);
        await controller.PendingFinalize;
        Assert.False(vm.ShowListeningHint);                       // Idle again (and lines present)
    }

    [Fact]
    public async Task A_marker_as_the_first_line_also_drops_the_listening_hint()
    {
        // B1-5: a capture-degraded-first session's first transcript line can be a MARKER, not a
        // segment - both share the Insert -> RebuildFrom path. Only the segment path was covered;
        // pin that a marker first line clears the "Listening" hint too (evidentiary-relevant).
        var gated = new GatedEngineFactory();
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var vm = new TranscriptLinesViewModel(controller, new FakeSettingsService(), a => a());

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.True(vm.ShowListeningHint);                       // Recording, no lines yet

        vm.RebuildFrom(new[] { TranscriptLine.Marker(0, 0, "capture degraded") }, gapMs: 5000);

        Assert.False(vm.ShowListeningHint);                      // a marker first line drops the hint too
        Assert.True(Assert.Single(vm.Lines).IsMarker);
    }
}
