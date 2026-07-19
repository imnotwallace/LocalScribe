using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class QaScopeFactoryTests
{
    private static DisplayRow Row(int seq, long startMs, string name, string text) => new()
    {
        StartMs = startMs, EndMs = startMs + 1000, DisplayName = name, Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Local, startMs, startMs + 1000, text, text, false, false)]
    };

    [Fact]
    public async Task Session_scope_builds_a_keepalive_answer_warmup_with_the_anchored_context()
    {
        var rows = new List<DisplayRow>
        {
            Row(0, 5_000, "Alice", "Hello there"),
            Row(1, 65_000, "Bob", "We agreed to settle"),
        };
        var factory = new QaScopeFactory(@"C:\models\m.gguf", "m.gguf", "auto", _ => []);
        var scope = await factory.ForSessionAsync("s1",
            ct => Task.FromResult<IReadOnlyList<DisplayRow>>(rows), "what was agreed",
            CancellationToken.None);

        Assert.False(scope.NoMatches);
        Assert.False(scope.ExcerptMode);
        Assert.Equal("answer", scope.WarmupRequest.Op);
        Assert.True(scope.WarmupRequest.KeepAlive);
        Assert.Equal(8192, scope.WarmupRequest.CtxTokens);       // tiny transcript -> smallest step
        Assert.Contains("[00:00:05] Alice: Hello there", scope.WarmupRequest.PayloadJson);
        Assert.Contains("[00:01:05] Bob: We agreed to settle", scope.WarmupRequest.PayloadJson);
        Assert.Same(rows, scope.SessionRows);
        Assert.Equal(new[] { "s1" }, scope.IncludedSessionIds);
    }

    [Fact]
    public async Task Session_scope_falls_to_disclosed_excerpts_when_the_ladder_is_exhausted()
    {
        // ~600k chars: over 64k tokens under ANY sane estimator -> the excerpt path, disclosed.
        var rows = new List<DisplayRow>();
        for (int i = 0; i < 60; i++)
            rows.Add(Row(i, i * 10_000, "Alice",
                (i == 30 ? "the settlement amount was discussed " : "") + new string('x', 10_000)));
        var searched = new List<string>();
        var factory = new QaScopeFactory(@"C:\models\m.gguf", "m.gguf", "auto", q =>
        {
            searched.Add(q.Text);
            var row = rows[30];
            var seg = row.Segments[0];
            return [new LocalScribe.Core.Search.SearchResult(
                new LocalScribe.Core.Search.SearchSessionEntry { SessionId = "s1" },
                [new LocalScribe.Core.Search.SearchHit(seg.Seq, seg.PartIndex, row.StartMs,
                    "Alice", row.Text, q.Text, false, false)], 1)];
        });
        var scope = await factory.ForSessionAsync("s1",
            ct => Task.FromResult<IReadOnlyList<DisplayRow>>(rows), "settlement amount",
            CancellationToken.None);

        Assert.True(scope.ExcerptMode);
        Assert.False(scope.NoMatches);
        Assert.Equal(ExcerptContextBuilder.DisclosureText, scope.Disclosure);
        Assert.Equal(32768, scope.WarmupRequest.CtxTokens);
        Assert.NotEmpty(searched);                                // the index was actually consulted
    }
}
