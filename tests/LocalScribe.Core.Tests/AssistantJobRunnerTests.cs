using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public class AssistantJobRunnerTests
{
    /// <summary>In-process fake of the process seam (SherpaHelperDiariserTests.FakeHelper
    /// strategy): replays canned stdout lines per written request; records requests and kills.</summary>
    private sealed class FakeProcess : IAssistantProcess
    {
        private readonly Func<string, IEnumerable<string?>> _script;
        private readonly Queue<string?> _pending = new();
        public List<string> Requests { get; } = [];
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public bool HangAfterScript { get; init; }   // watchdog test: reads block forever

        public FakeProcess(Func<string, IEnumerable<string?>> script) => _script = script;

        public Task WriteRequestLineAsync(string requestJson, CancellationToken ct)
        {
            Requests.Add(requestJson);
            foreach (var line in _script(requestJson)) _pending.Enqueue(line);
            return Task.CompletedTask;
        }

        public async Task<string?> ReadEventLineAsync(CancellationToken ct)
        {
            if (_pending.Count > 0) { await Task.Yield(); return _pending.Dequeue(); }
            if (HangAfterScript) { await Task.Delay(Timeout.Infinite, ct); }
            return null;   // EOF
        }

        public void Kill() => Killed = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeFactory(FakeProcess proc) : IAssistantProcessFactory
    {
        public int Starts { get; private set; }
        public Task<IAssistantProcess> StartAsync(CancellationToken ct)
        { Starts++; return Task.FromResult<IAssistantProcess>(proc); }
    }

    private static AssistantRequest Req(bool keepAlive = false)
        => new("summarize", @"C:\models\q.gguf", 8192, "auto", keepAlive, "{\"prompt\":\"p\",\"maxTokens\":600}");

    private static async Task<List<AssistantEvent>> Collect(IAsyncEnumerable<AssistantEvent> stream)
    {
        var list = new List<AssistantEvent>();
        await foreach (var e in stream) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Run_streams_typed_events_skips_noise_and_stops_at_done()
    {
        var proc = new FakeProcess(_ => new string?[]
        {
            "{\"type\":\"progress\",\"phase\":\"prefill\",\"current\":1,\"total\":2}",
            "llama.cpp native noise line",                                   // skipped, never fatal
            "{\"type\":\"chunk\",\"text\":\"Hello\"}",
            "{\"type\":\"done\",\"stats\":{\"backend\":\"cuda\",\"promptTokens\":9,\"outputTokens\":1}}",
        });
        var runner = new AssistantJobRunner(new FakeFactory(proc));
        var events = await Collect(runner.RunAsync(Req(), CancellationToken.None));

        Assert.Equal(new AssistantEvent[]
            { new AssistantProgress("prefill", 1, 2), new AssistantChunk("Hello"), new AssistantDone("cuda", 9, 1) },
            events);
        Assert.Single(proc.Requests);
        Assert.Contains("\"kvQuant\":\"q8_0\"", proc.Requests[0]);   // the locked wire rode through
        Assert.True(proc.Disposed);                                   // spawn-per-job: torn down after
    }

    [Fact]
    public async Task Eof_before_a_terminal_event_synthesizes_a_visible_error()
    {
        // Design 7.7: helper crash -> visible error, nothing persisted.
        var proc = new FakeProcess(_ => new string?[] { "{\"type\":\"chunk\",\"text\":\"par\"}" });
        var events = await Collect(new AssistantJobRunner(new FakeFactory(proc))
            .RunAsync(Req(), CancellationToken.None));
        var err = Assert.IsType<AssistantError>(events[^1]);
        Assert.Contains("exited before completing", err.Message);
    }

    [Fact]
    public async Task Watchdog_kills_a_silent_helper_and_surfaces_an_error()
    {
        var proc = new FakeProcess(_ => Array.Empty<string?>()) { HangAfterScript = true };
        var runner = new AssistantJobRunner(new FakeFactory(proc), inactivityTimeout: TimeSpan.FromMilliseconds(100));
        var events = await Collect(runner.RunAsync(Req(), CancellationToken.None));
        var err = Assert.IsType<AssistantError>(Assert.Single(events));
        Assert.Contains("watchdog", err.Message);
        Assert.True(proc.Killed);
    }

    [Fact]
    public async Task Cancellation_kills_the_process_and_throws()
    {
        var proc = new FakeProcess(_ => Array.Empty<string?>()) { HangAfterScript = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Collect(new AssistantJobRunner(new FakeFactory(proc)).RunAsync(Req(), cts.Token)));
        Assert.True(proc.Killed);
    }

    [Fact]
    public async Task Chat_session_warms_once_then_answers_on_the_live_process()
    {
        // Design 7.1 warm-chat contract: warmup prefilled once; AskAsync reuses model+KV.
        var proc = new FakeProcess(req => new string?[]
        {
            req.Contains("\"op\":\"answer\"")
                ? "{\"type\":\"chunk\",\"text\":\"A1\"}"
                : "{\"type\":\"progress\",\"phase\":\"prefill\",\"current\":1,\"total\":1}",
            "{\"type\":\"done\",\"stats\":{\"backend\":\"cpu\",\"promptTokens\":5,\"outputTokens\":1}}",
        });
        var factory = new FakeFactory(proc);
        var sessions = new AssistantChatSessionFactory(factory);

        await using var chat = await sessions.StartAsync(Req(keepAlive: true), CancellationToken.None);
        Assert.Equal(1, factory.Starts);
        Assert.Single(proc.Requests);                                  // the warmup request
        Assert.Contains("\"keepAlive\":true", proc.Requests[0]);       // warmup FORCED keep-alive

        var a1 = await Collect(chat.AskAsync("{\"prompt\":\"q1\",\"maxTokens\":400}", CancellationToken.None));
        var a2 = await Collect(chat.AskAsync("{\"prompt\":\"q2\",\"maxTokens\":400}", CancellationToken.None));
        Assert.Equal(new AssistantChunk("A1"), a1[0]);
        Assert.Equal(new AssistantChunk("A1"), a2[0]);
        Assert.Equal(3, proc.Requests.Count);                          // warmup + 2 answers, ONE process
        Assert.Equal(1, factory.Starts);
        Assert.All(proc.Requests.Skip(1), r => Assert.Contains("\"op\":\"answer\"", r));

        await chat.DisposeAsync();
        Assert.True(proc.Killed);                                      // teardown = process kill
    }

    [Fact]
    public async Task Chat_warmup_that_fell_from_cuda_to_cpu_is_captured_on_the_session()
    {
        // The cuda-fell-to-cpu event fires during MODEL LOAD, which for chat happens inside the
        // warmup drain (AskAsync reuses the loaded model, so no fall fires there). The captured
        // verdict must ride on the returned session for every turn it serves (design 2026-07-23).
        var proc = new FakeProcess(_ => new string?[]
        {
            AssistantWire.SerializeEvent(new AssistantProgress(AssistantWire.CudaFellPhase, 0, 0)),
            "{\"type\":\"done\",\"stats\":{\"backend\":\"cpu\",\"promptTokens\":5,\"outputTokens\":1}}",
        });
        await using var chat = await new AssistantChatSessionFactory(new FakeFactory(proc))
            .StartAsync(Req(keepAlive: true), CancellationToken.None);
        Assert.True(chat.CudaFellToCpu);
    }

    [Fact]
    public async Task Chat_warmup_with_no_fall_reports_a_clean_session()
    {
        var proc = new FakeProcess(_ => new string?[]
        {
            "{\"type\":\"progress\",\"phase\":\"load-cpu\",\"current\":1,\"total\":1}",
            "{\"type\":\"done\",\"stats\":{\"backend\":\"cpu\",\"promptTokens\":5,\"outputTokens\":1}}",
        });
        await using var chat = await new AssistantChatSessionFactory(new FakeFactory(proc))
            .StartAsync(Req(keepAlive: true), CancellationToken.None);
        Assert.False(chat.CudaFellToCpu);
    }

    [Fact]
    public async Task Chat_warmup_failure_disposes_the_process_and_throws()
    {
        var proc = new FakeProcess(_ => new string?[] { "{\"type\":\"error\",\"message\":\"MODEL_MISSING\"}" });
        var ex = await Assert.ThrowsAsync<AssistantException>(() =>
            new AssistantChatSessionFactory(new FakeFactory(proc))
                .StartAsync(Req(keepAlive: true), CancellationToken.None));
        Assert.Contains("MODEL_MISSING", ex.Message);
        Assert.True(proc.Disposed);
    }
}
