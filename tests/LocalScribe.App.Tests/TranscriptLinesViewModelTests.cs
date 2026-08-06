using System.IO;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
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
        // Tier 1 T1-6 (spec 2026-08-05 :70-71): every live session now OPENS with the
        // `transcription engine: ...` marker at 0 ms, so position 0 is a marker and its Speaker is
        // "" by design. This test is about how SEGMENTS map, so select the first non-marker line -
        // the count assertion above already filters markers for the same reason.
        var first = vm.Lines.First(l => !l.IsMarker);
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
        string? id1 = await controller.StopAsync(CancellationToken.None);
        await controller.PendingFinalize;                        // first session's segments land via the background drain
        int afterFirst = vm.Lines.Count;
        if (afterFirst <= 0) Assert.Fail(await DiagFAsync("afterFirst", afterFirst, id1));

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        string? id2 = await controller.StopAsync(CancellationToken.None);
        await controller.PendingFinalize;                        // second session refills after the background drain
        if (vm.Lines.Count != afterFirst)                        // cleared, then refilled
            Assert.Fail(await DiagFAsync($"finalLines(vs afterFirst={afterFirst})", vm.Lines.Count, id2));
    }

    /// <summary>Failure-only diagnostic for the ~1/150 flake investigated 2026-07-30 (root cause
    /// unconfirmed: this test's finalize path provably lands lines IF segments are produced, yet it
    /// flaked to zero lines once under full-suite load and could not be reproduced in isolation -
    /// 0/4320 instrumented, 0/~250 full-suite). Read ONLY on the failing branch, so no per-run cost.
    /// SegmentCount&gt;0 means segments WERE produced and the lines were lost after PendingFinalize;
    /// ==0 means no segment was ever produced (an upstream capture/VAD drop). Whichever it is pins
    /// the next investigation immediately.</summary>
    private async Task<string> DiagFAsync(string what, int lines, string? sessionId)
    {
        int segs = sessionId is null ? -2
            : (await new SessionStore(new StoragePaths(_root).SessionJson(sessionId)).ReadAsync(CancellationToken.None))?.SegmentCount ?? -1;
        return $"DIAG-F {what}={lines}, session.json SegmentCount={segs}, id={sessionId} "
             + "(SegmentCount>0 => lines lost after PendingFinalize; ==0 => no segment produced).";
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

    /// <summary>Completes once the session-start `transcription engine: ...` marker (Tier 1 T1-6,
    /// spec 2026-08-05 :70-71) has reached LineInserted. Both hint tests below need the
    /// deterministic "Recording, and the live list is empty" window, and this round falsified the
    /// premise they used to get it - that a clean per-process fake Start writes no markers. Every
    /// live Start now queues one at 0 ms and the writer loop drains it from a POOL thread, so the
    /// empty window is a race unless the test waits for that marker and then clears explicitly.
    /// Must be called BEFORE StartAsync so the subscription is in place when the marker lands.</summary>
    private static Task EngineMarkerArrivedAsync(SessionController controller)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.LineInserted += (_, l) =>
        { if (l.Kind == TranscriptKind.Marker) tcs.TrySetResult(); };
        return tcs.Task;
    }

    [Fact]
    public async Task Listening_hint_shows_only_while_recording_with_no_lines()
    {
        // Design 2026-07-13 section 5 item 1. GatedEngineFactory holds the engine build closed, so
        // no SEGMENT can land while the gate is shut; the start marker is handled above.
        var gated = new GatedEngineFactory();
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var vm = new TranscriptLinesViewModel(controller, new FakeSettingsService(), a => a());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(vm.ShowListeningHint);                       // Idle: never shown

        var markerLanded = EngineMarkerArrivedAsync(controller);
        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        // The Idle -> Recording flip raises the hint property, whichever side of the marker's
        // arrival it lands on - StateChanged is raised synchronously inside StartAsync.
        Assert.Contains(nameof(TranscriptLinesViewModel.ShowListeningHint), raised);

        await markerLanded.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Clear();                                               // the empty window, deterministically
        Assert.True(vm.ShowListeningHint);                        // Recording + zero lines

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
        // Tier 1 T1-6 turned that edge case into the NORMAL one: every live session now opens with
        // the engine marker, so this rule is what the user meets on every single Start.
        var gated = new GatedEngineFactory();
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var vm = new TranscriptLinesViewModel(controller, new FakeSettingsService(), a => a());

        var markerLanded = EngineMarkerArrivedAsync(controller);
        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await markerLanded.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Clear();
        Assert.True(vm.ShowListeningHint);                       // Recording, no lines yet

        vm.RebuildFrom(new[] { TranscriptLine.Marker(0, 0, "capture degraded") }, gapMs: 5000);

        Assert.False(vm.ShowListeningHint);                      // a marker first line drops the hint too
        Assert.True(Assert.Single(vm.Lines).IsMarker);
    }
}
