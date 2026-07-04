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
        public bool Cancelled { get; private set; }
        public FakeHelper(int exit, params string[] lines) { _exit = exit; _lines = lines; }
        public async Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct)
        {
            foreach (var l in _lines) { ct.ThrowIfCancellationRequested(); onStdoutLine(l); await Task.Yield(); }
            return _exit;
        }
    }

    [Fact]
    public async Task Parses_progress_then_result()
    {
        var helper = new FakeHelper(0,
            "{\"progress\":0.5}",
            "{\"segments\":[{\"startMs\":0,\"endMs\":1000,\"cluster\":0}],\"clusterCount\":2,\"method\":\"sherpa\"}");
        var seen = new List<double>();
        var progress = new Progress<double>(seen.Add);

        var result = await new SherpaHelperDiariser(helper).DiariseAsync(Req(), progress, default);

        Assert.Equal(2, result.ClusterCount);
        Assert.Single(result.Segments);
        Assert.Equal(1000, result.Segments[0].EndMs);
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
}
