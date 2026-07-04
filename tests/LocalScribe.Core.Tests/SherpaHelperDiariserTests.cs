using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;

public class SherpaHelperDiariserTests
{
    private static DiarisationRequest Req() =>
        new("remote.flac", SourceKind.Remote, "seg.onnx", "emb.onnx", null);

    private sealed class FakeHelper : IDiarisationHelper
    {
        private readonly string[] _lines;
        private readonly int _exit;
        private readonly CancellationTokenSource? _cancelMidRun;

        // Set true once RunAsync has observed/triggered cancellation mid-run - i.e. the
        // "helper process killed while running" path, as opposed to the up-front
        // ct.ThrowIfCancellationRequested() guard the caller checks before RunAsync is
        // ever invoked.
        public bool Cancelled { get; private set; }

        public FakeHelper(int exit, params string[] lines) { _exit = exit; _lines = lines; }

        // Overload for the mid-run-cancel scenario: after emitting the given lines, the
        // fake cancels the supplied CTS itself (simulating the helper process being
        // killed) and then observes that cancellation via ct.ThrowIfCancellationRequested(),
        // which is the real cancel path DiariseAsync must propagate.
        public FakeHelper(int exit, CancellationTokenSource cancelMidRun, params string[] lines)
        {
            _exit = exit;
            _lines = lines;
            _cancelMidRun = cancelMidRun;
        }

        public async Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct)
        {
            foreach (var l in _lines) { ct.ThrowIfCancellationRequested(); onStdoutLine(l); await Task.Yield(); }
            if (_cancelMidRun is not null)
            {
                _cancelMidRun.Cancel();
                Cancelled = true;
                ct.ThrowIfCancellationRequested();
            }
            return _exit;
        }
    }

    // Synchronous fake IProgress<double> - System.Progress<double> reports via the
    // captured SynchronizationContext (or thread pool), which is racy to assert on
    // immediately after await. This fake reports inline so assertions are deterministic.
    private sealed class SyncProgress : IProgress<double>
    {
        public List<double> Reported { get; } = new();
        public void Report(double value) => Reported.Add(value);
    }

    [Fact]
    public async Task Parses_progress_then_result()
    {
        var helper = new FakeHelper(0,
            "{\"progress\":0.5}",
            "{\"segments\":[{\"startMs\":0,\"endMs\":1000,\"cluster\":0}],\"clusterCount\":2,\"method\":\"sherpa\"}");
        var progress = new SyncProgress();

        var result = await new SherpaHelperDiariser(helper).DiariseAsync(Req(), progress, default);

        Assert.Equal(2, result.ClusterCount);
        Assert.Single(result.Segments);
        Assert.Equal(1000, result.Segments[0].EndMs);
        Assert.Contains(0.5, progress.Reported);
    }

    [Fact]
    public async Task Error_line_maps_MODEL_MISSING_to_ModelDownloadFailed()
    {
        var helper = new FakeHelper(1, "{\"error\":\"MODEL_MISSING\",\"detail\":\"nope\"}");
        var ex = await Assert.ThrowsAsync<DiarisationException>(
            () => new SherpaHelperDiariser(helper).DiariseAsync(Req(), new Progress<double>(_ => { }), default));
        Assert.Equal(DiarisationErrorCode.ModelDownloadFailed, ex.Code);
    }

    [Fact]
    public async Task Nonzero_exit_without_error_line_is_HelperCrash()
    {
        var helper = new FakeHelper(3, "{\"progress\":0.1}");
        var ex = await Assert.ThrowsAsync<DiarisationException>(
            () => new SherpaHelperDiariser(helper).DiariseAsync(Req(), new Progress<double>(_ => { }), default));
        Assert.Equal(DiarisationErrorCode.HelperCrash, ex.Code);
    }

    [Fact]
    public async Task Cancellation_propagates_as_OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var helper = new FakeHelper(0, "{\"progress\":0.1}");
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new SherpaHelperDiariser(helper).DiariseAsync(Req(), new Progress<double>(_ => { }), cts.Token));
    }

    [Fact]
    public async Task Mid_run_cancellation_propagates_as_OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var helper = new FakeHelper(0, cts, "{\"progress\":0.1}");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new SherpaHelperDiariser(helper).DiariseAsync(Req(), new Progress<double>(_ => { }), cts.Token));

        Assert.True(helper.Cancelled);
    }

    [Fact]
    public async Task Malformed_segments_line_is_ignored_and_yields_HelperCrash()
    {
        var helper = new FakeHelper(0, "{\"segments\":");
        var ex = await Assert.ThrowsAsync<DiarisationException>(
            () => new SherpaHelperDiariser(helper).DiariseAsync(Req(), new Progress<double>(_ => { }), default));
        Assert.Equal(DiarisationErrorCode.HelperCrash, ex.Code);
    }
}
