using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Tests;
using Xunit;

public class MatterAssistantViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) { }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<MatterSummarySource> Sources() =>
    [
        new("a", "Session a", T0.AddDays(-1),
            "The parties agreed to settle for ten thousand dollars [00:01:05]", false),
        new("b", "Session b", T0.AddDays(-2), "Retainer summary [00:10:00]", true),
        new("c", "Session c", T0.AddDays(-3), null, false),
    ];

    private (MatterAssistantViewModel Vm, FakeAssistantChatSessionFactory Factory, FakeReporter Reporter)
        Make()
    {
        var factory = new FakeAssistantChatSessionFactory();
        var store = new AssistantChatStore(Path.Combine(_root, "assistant", "chats.json"));
        // CONTRACT-DRIFT: QaScope grew two trailing fields (SpeakerPreamble, ContextText) since
        // this plan snippet was written (see AssistantChatViewModelTests.cs's own adaptation) -
        // empty strings are behavior-preserving here: no test asserts on the payload text sent
        // to the fake session.
        var scope = new QaScope(
            new AssistantRequest(Op: "answer", ModelPath: @"C:\m.gguf", CtxTokens: 8192,
                Backend: "auto", KeepAlive: true, PayloadJson: "M1"),
            "m.gguf", "3", false, null, false, null, null,
            [Sources()[0]], ["a"], ["b"], ["c"], "", "");
        var reporter = new FakeReporter();
        var vm = new MatterAssistantViewModel("m1",
            ct => Task.FromResult(Sources()),
            () => new AssistantQaService(factory, store,
                ct => Task.FromResult<IAsyncDisposable>(new NoopLease()),
                (q, ct) => Task.FromResult(scope), TimeProvider.System),
            store, reporter, a => a());
        return (vm, factory, reporter);
    }

    [Fact]
    public async Task Refresh_maps_summary_status_rows_newest_first()
    {
        var (vm, _, reporter) = Make();
        await vm.RefreshAsync(CancellationToken.None);

        Assert.Empty(reporter.Errors);
        Assert.Equal(new[] { "a", "b", "c" }, vm.SummaryRows.Select(r => r.SessionId));
        Assert.Equal("Summary ready", vm.SummaryRows[0].StatusText);
        Assert.Equal("Summary out of date", vm.SummaryRows[1].StatusText);   // stale badged
        Assert.True(vm.SummaryRows[1].IsStale);
        Assert.Equal("No summary yet", vm.SummaryRows[2].StatusText);        // missing -> generate offer
        Assert.False(vm.SummaryRows[2].HasSummary);
        Assert.Equal("2026-06-30", vm.SummaryRows[0].DateDisplay);
        Assert.Equal("", vm.CoverageText);                                   // locked: empty until the first answer
    }

    [Fact]
    public async Task Generate_cta_raises_the_generation_request()
    {
        var (vm, _, _) = Make();
        await vm.RefreshAsync(CancellationToken.None);
        string? requested = null;
        vm.SummaryGenerationRequested += id => requested = id;

        vm.GenerateSummaryCommand.Execute(vm.SummaryRows[2]);
        Assert.Equal("c", requested);
    }

    [Fact]
    public async Task Coverage_text_discloses_included_omitted_and_missing_after_an_ask()
    {
        var (vm, factory, reporter) = Make();
        await vm.RefreshAsync(CancellationToken.None);
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("The parties agreed to settle for ten thousand dollars [00:01:05]"),
            new AssistantDone("cpu", 1, 1),
        });

        vm.Chat.QuestionText = "what was agreed";
        await vm.Chat.AskCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        Assert.Contains("summaries from 1 of 3 tagged sessions", vm.CoverageText);
        Assert.Contains("Omitted (context budget): Session b.", vm.CoverageText);
        Assert.Contains("No summary yet: Session c.", vm.CoverageText);      // never silent (design 7.5)
    }

    [Fact]
    public async Task Shutdown_tears_the_warm_helper_down()
    {
        var (vm, factory, _) = Make();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1),
        });
        vm.Chat.QuestionText = "q";
        await vm.Chat.AskCommand.ExecuteAsync(null);            // a warm session now exists

        vm.Shutdown();
        Assert.True(factory.Sessions[0].Disposed);              // scope-change/close teardown (design 7.1)
    }

    [Fact]
    public async Task Coverage_denominator_comes_from_the_turn_partition_not_the_cached_rows()
    {
        // The disclosure total must reflect the ASK-TIME scope (the turn's exhaustive
        // Included+Omitted+Missing partition), NOT the VM's cached SummaryRows - which here is EMPTY
        // (RefreshAsync deliberately not called). The old SummaryRows.Count denominator prints the
        // nonsensical "1 of 0"; the turn partition (Included=[a], Omitted=[b], Missing=[c]) is "of 3".
        var (vm, factory, reporter) = Make();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("The parties agreed to settle for ten thousand dollars [00:01:05]"),
            new AssistantDone("cpu", 1, 1),
        });

        Assert.Empty(vm.SummaryRows);                                   // no refresh -> cached rows empty
        vm.Chat.QuestionText = "what was agreed";
        await vm.Chat.AskCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        Assert.Contains("1 of 3 tagged sessions", vm.CoverageText);     // from the turn, not the 0 cached rows
        Assert.DoesNotContain("of 0", vm.CoverageText);
    }

    [Fact]
    public async Task RefreshAsync_surfaces_a_load_failure_without_crashing()
    {
        var factory = new FakeAssistantChatSessionFactory();
        var store = new AssistantChatStore(Path.Combine(_root, "assistant", "chats.json"));
        var reporter = new FakeReporter();
        var vm = new MatterAssistantViewModel("m1",
            ct => Task.FromException<IReadOnlyList<MatterSummarySource>>(
                new InvalidOperationException("disk gone")),
            () => null, store, reporter, a => a());

        await vm.RefreshAsync(CancellationToken.None);                  // must not throw

        Assert.Single(reporter.Errors);
        Assert.Equal("Load matter summary status", reporter.Errors[0].Context);
        Assert.Empty(vm.SummaryRows);                                  // left sane
    }
}
