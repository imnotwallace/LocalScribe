using LocalScribe.Core.Audio;
using LocalScribe.Core.Diagnostics;
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

        public DiarisationJob? LastJob { get; private set; }

        public async Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct)
        {
            LastJob = job;
            foreach (var l in _lines) { ct.ThrowIfCancellationRequested(); onStdoutLine(l); await Task.Yield(); }
            if (_cancelMidRun is not null)
            {
                _cancelMidRun.Cancel();
                Cancelled = true;
                ct.ThrowIfCancellationRequested();
            }
            return _exit;
        }

        public EmbedJob? LastEmbedJob { get; private set; }

        public async Task<int> RunEmbedAsync(EmbedJob job, Action<string> onStdoutLine, CancellationToken ct)
        {
            LastEmbedJob = job;
            foreach (var l in _lines) { ct.ThrowIfCancellationRequested(); onStdoutLine(l); await Task.Yield(); }
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

    [Fact]
    public async Task EmitEmbeddings_flows_to_job_and_result()
    {
        var helper = new FakeHelper(0,
            "{\"segments\":[{\"startMs\":0,\"endMs\":1000,\"cluster\":0}],\"clusterCount\":1,\"method\":\"m\"," +
            "\"clusterEmbeddings\":{\"0\":[0.5,0.5]},\"embeddingMethod\":\"campplus-zh-en\"}");
        var req = new DiarisationRequest("r.flac", SourceKind.Remote, "s.onnx", "e.onnx", null, EmitEmbeddings: true);

        var result = await new SherpaHelperDiariser(helper).DiariseAsync(req, new Progress<double>(_ => { }), default);

        Assert.True(helper.LastJob!.EmitEmbeddings);
        Assert.Equal(0.5f, result.ClusterEmbeddings!["0"][0]);
        Assert.Equal("campplus-zh-en", result.EmbeddingMethod);
    }

    [Fact]
    public async Task Result_without_embeddings_stays_null_backcompat()
    {
        var helper = new FakeHelper(0,
            "{\"segments\":[],\"clusterCount\":0,\"method\":\"m\"}");
        var req = new DiarisationRequest("r.flac", SourceKind.Remote, "s.onnx", "e.onnx", null, EmitEmbeddings: true);
        var result = await new SherpaHelperDiariser(helper).DiariseAsync(req, new Progress<double>(_ => { }), default);
        Assert.Null(result.ClusterEmbeddings);   // old helper: silent degrade, no throw
    }

    [Fact]
    public async Task EmbedAsync_parses_embedding_result()
    {
        var helper = new FakeHelper(0, "{\"embedding\":[0.25,0.75],\"method\":\"campplus-zh-en\"}");
        var result = await new SherpaHelperDiariser(helper).EmbedAsync(
            new EmbedRequest("r.flac", [new EmbedRange(0, 1000)], "e.onnx"), default);
        Assert.Equal(0.75f, result.Embedding[1]);
        Assert.Equal("embed", helper.LastEmbedJob!.Op);
    }

    [Fact]
    public async Task EmbedAsync_error_line_throws_DiarisationException()
    {
        var helper = new FakeHelper(1, "{\"error\":\"BAD_AUDIO\",\"detail\":\"nope\"}");
        var ex = await Assert.ThrowsAsync<DiarisationException>(
            () => new SherpaHelperDiariser(helper).EmbedAsync(
                new EmbedRequest("r.flac", [new EmbedRange(0, 1000)], "e.onnx"), default));
        Assert.Equal(DiarisationErrorCode.BadAudio, ex.Code);
    }

    /// <summary>Records diagnostic lines. Mirrors AppServiceFakes.FakeDiagnosticLog on the App
    /// side; duplicated here rather than shared because Core.Tests has no shared-fakes file (house
    /// convention: no cross-file test helper).</summary>
    private sealed class RecordingLog : IDiagnosticLog
    {
        public readonly List<(string Level, string Source, string Message)> Entries = new();
        public void Write(string level, string source, string message, string? detail = null)
            => Entries.Add((level, source, message));
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_helper_crash_is_logged_as_a_warning_naming_its_exit_code()
    {
        // Spec item T1-1 lists "helper process exits". Today a crashed diarizer surfaces only as a
        // dialog the user has already dismissed by the time they ask for help.
        var log = new RecordingLog();
        var engine = new SherpaHelperDiariser(new FakeHelper(3, "{\"progress\":0.1}"), log);

        await Assert.ThrowsAsync<DiarisationException>(
            () => engine.DiariseAsync(Req(), new Progress<double>(_ => { }), default));

        var entry = Assert.Single(log.Entries);
        Assert.Equal("warn", entry.Level);
        Assert.Equal("diarizer", entry.Source);
        Assert.Contains("code 3", entry.Message);
    }

    [Fact]
    public async Task A_clean_run_logs_at_debug_so_the_default_level_drops_it()
    {
        // A voiceprint backfill runs hundreds of these; at the default "info" level a clean exit
        // must not flood the file, and DiagnosticLog gates it out before it is ever queued.
        var log = new RecordingLog();
        var helper = new FakeHelper(0,
            "{\"segments\":[{\"startMs\":0,\"endMs\":1000,\"cluster\":0}],\"clusterCount\":2,\"method\":\"sherpa\"}");

        await new SherpaHelperDiariser(helper, log).DiariseAsync(Req(), new Progress<double>(_ => { }), default);

        Assert.Equal("debug", Assert.Single(log.Entries).Level);
    }
}
