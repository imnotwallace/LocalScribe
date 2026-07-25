using LocalScribe.Core.Assistant;
using LocalScribe.Core.Search.Semantic;

public sealed class AssistantEmbeddingClientTests
{
    private sealed class FakeProcess(IEnumerable<string> lines) : IAssistantProcess
    {
        private readonly Queue<string> _lines = new(lines);
        public List<string> Written { get; } = [];
        public bool Killed { get; private set; }
        public Task WriteRequestLineAsync(string requestJson, CancellationToken ct)
        { Written.Add(requestJson); return Task.CompletedTask; }
        public Task<string?> ReadEventLineAsync(CancellationToken ct)
            => Task.FromResult(_lines.Count > 0 ? _lines.Dequeue() : null);
        public void Kill() => Killed = true;
        public ValueTask DisposeAsync() { Kill(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeFactory(Func<IAssistantProcess> make) : IAssistantProcessFactory
    {
        public int Starts { get; private set; }
        public IAssistantProcess? Last { get; private set; }
        public Task<IAssistantProcess> StartAsync(CancellationToken ct)
        { Starts++; Last = make(); return Task.FromResult(Last!); }
    }

    private sealed class ThrowingWriteProcess : IAssistantProcess
    {
        public bool Killed { get; private set; }
        public Task WriteRequestLineAsync(string requestJson, CancellationToken ct)
            => throw new IOException("pipe broken");
        public Task<string?> ReadEventLineAsync(CancellationToken ct)
            => Task.FromResult<string?>(null);
        public void Kill() => Killed = true;
        public ValueTask DisposeAsync() { Kill(); return ValueTask.CompletedTask; }
    }

    private static string EmbedResultLine(string method, params float[][] vectors)
        => AssistantWire.SerializeEvent(new AssistantEmbedResult(vectors, method));
    private static string DoneLine()
        => AssistantWire.SerializeEvent(new AssistantDone("cpu", 0, 1));
    private static string ErrorLine(string msg)
        => AssistantWire.SerializeEvent(new AssistantError(msg));

    [Fact]
    public async Task Embed_returns_vectors_and_method_and_sends_a_keepalive_embed_request()
    {
        var factory = new FakeFactory(() => new FakeProcess(
            [EmbedResultLine("m@2", [1f, 0f]), DoneLine(),
             EmbedResultLine("m@2", [0f, 1f]), DoneLine()]));
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2);

        var batch = await client.EmbedAsync("document", ["hello"], CancellationToken.None);

        Assert.Equal("m@2", batch.Method);
        Assert.Equal(new[] { 1f, 0f }, Assert.Single(batch.Embeddings));
        string sent = Assert.Single(((FakeProcess)factory.Last!).Written);
        Assert.Contains("\"op\":\"embed\"", sent);
        Assert.Contains("\"keepAlive\":true", sent);
        Assert.Contains("\"backend\":\"cpu\"", sent);

        // second call reuses the SAME warm process (no new StartAsync)
        await client.EmbedAsync("query", ["again"], CancellationToken.None);
        Assert.Equal(1, factory.Starts);
        Assert.Equal(2, ((FakeProcess)factory.Last!).Written.Count);
    }

    [Fact]
    public async Task Error_event_throws_and_kills_the_process_for_a_fresh_respawn()
    {
        int made = 0;
        var factory = new FakeFactory(() => { made++; return new FakeProcess(
            made == 1 ? [ErrorLine("boom")] : [EmbedResultLine("m@2", [1f, 0f]), DoneLine()]); });
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2);

        await Assert.ThrowsAsync<AssistantException>(
            () => client.EmbedAsync("document", ["x"], CancellationToken.None));

        // next call starts a NEW process and succeeds
        var batch = await client.EmbedAsync("document", ["x"], CancellationToken.None);
        Assert.Single(batch.Embeddings);
        Assert.Equal(2, factory.Starts);
    }

    [Fact]
    public async Task Eof_before_terminal_throws_AssistantException()
    {
        var factory = new FakeFactory(() => new FakeProcess([]));   // immediate EOF
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2);
        await Assert.ThrowsAsync<AssistantException>(
            () => client.EmbedAsync("document", ["x"], CancellationToken.None));
    }

    [Fact]
    public async Task Release_kills_the_warm_process_and_the_next_call_respawns()
    {
        var factory = new FakeFactory(() => new FakeProcess(
            [EmbedResultLine("m@2", [1f, 0f]), DoneLine()]));
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2);
        await client.EmbedAsync("document", ["x"], CancellationToken.None);
        var first = (FakeProcess)factory.Last!;

        await client.ReleaseAsync();

        Assert.True(first.Killed);
        await client.EmbedAsync("document", ["y"], CancellationToken.None);
        Assert.Equal(2, factory.Starts);
    }

    [Fact]
    public async Task Write_failure_surfaces_as_AssistantException_and_kills_the_process()
    {
        var factory = new FakeFactory(() => new ThrowingWriteProcess());
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2);
        await Assert.ThrowsAsync<AssistantException>(
            () => client.EmbedAsync("document", ["x"], CancellationToken.None));
        Assert.True(((ThrowingWriteProcess)factory.Last!).Killed);
    }

    [Fact]
    public async Task Done_without_an_embedResult_throws()
    {
        var factory = new FakeFactory(() => new FakeProcess([DoneLine()]));
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2);
        await Assert.ThrowsAsync<AssistantException>(
            () => client.EmbedAsync("document", ["x"], CancellationToken.None));
    }

    // Idle reclaim (final review 2026-07-25): distinct from the mid-request hang guard covered by
    // the tests above - this is the "nobody has called in a while" timer that kills a warm-but-
    // unused helper so the next call respawns fresh.
    [Fact]
    public async Task Idle_reclaim_kills_the_warm_process_after_the_inactivity_window()
    {
        var factory = new FakeFactory(() => new FakeProcess(
            [EmbedResultLine("m@2", [1f, 0f]), DoneLine()]));
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2,
            inactivityTimeout: TimeSpan.FromMilliseconds(50));

        await client.EmbedAsync("document", ["x"], CancellationToken.None);
        var first = (FakeProcess)factory.Last!;

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!first.Killed && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(first.Killed);
    }

    [Fact]
    public async Task A_call_before_the_idle_window_expires_reuses_the_same_process()
    {
        var factory = new FakeFactory(() => new FakeProcess(
            [EmbedResultLine("m@2", [1f, 0f]), DoneLine(),
             EmbedResultLine("m@2", [0f, 1f]), DoneLine()]));
        // Long timeout so the idle reclaim never fires during this test's own wall-clock run -
        // flakiness here would be a false positive for a real defect, so bias generously.
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2,
            inactivityTimeout: TimeSpan.FromSeconds(10));

        await client.EmbedAsync("document", ["x"], CancellationToken.None);
        await client.EmbedAsync("document", ["y"], CancellationToken.None);

        Assert.Equal(1, factory.Starts);
    }
}
