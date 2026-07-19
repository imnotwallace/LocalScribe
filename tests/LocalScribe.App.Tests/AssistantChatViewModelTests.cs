using System.IO;
using System.Runtime.CompilerServices;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Tests;
using Xunit;

public class AssistantChatViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }

    private static DisplayRow Row(int seq, long startMs, long endMs, string name, string text) => new()
    {
        StartMs = startMs, EndMs = endMs, DisplayName = name, Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Remote, startMs, endMs, text, text, false, false)]
    };

    // CONTRACT-DRIFT: QaScope grew two trailing fields (SpeakerPreamble, ContextText) since the
    // plan snippet was written - AssistantQaService.AskAsync now feeds them straight into
    // AssistantPrompts.BuildAnswerPrompt for the per-question payload. Empty strings are
    // behavior-preserving here: no test asserts on the payload text sent to the fake session.
    private static QaScope SessionScope(IReadOnlyList<DisplayRow> rows) => new(
        new AssistantRequest(Op: "answer", ModelPath: @"C:\m.gguf", CtxTokens: 8192,
            Backend: "auto", KeepAlive: true, PayloadJson: "P1"),
        "m.gguf", "3", false, null, false, "s1", rows, null, ["s1"], [], [], "", "");

    private (AssistantChatViewModel Vm, FakeAssistantChatSessionFactory Factory,
        AssistantChatStore Store, FakeReporter Reporter)
        MakeChat(Func<string?>? busyReason = null, bool modelInstalled = true)
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var factory = new FakeAssistantChatSessionFactory();
        var store = new AssistantChatStore(Path.Combine(_root, "assistant", "chats.json"));
        var reporter = new FakeReporter();
        Func<AssistantQaService?> serviceFactory = modelInstalled
            ? () => new AssistantQaService(factory, store,
                ct => Task.FromResult<IAsyncDisposable>(new NoopLease()),
                (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System)
            : () => null;
        var vm = new AssistantChatViewModel(serviceFactory, store, reporter, a => a(), busyReason);
        return (vm, factory, store, reporter);
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Branch-7 fix: chat recording-preemption (design 7.1 reverse direction). A local blocking
    // double rather than reusing Core.Tests' one - AssistantQaServiceTests.cs (where that double
    // lives, nested private) is not linked into this leaf test project (only AssistantChatFakes.cs
    // is; see the csproj Compile Include comments), so this mirrors it verbatim. Yields one chunk
    // (past the lease/warmup) then blocks forever unless the token it is actually given cancels -
    // exercises the VM's forwarding of CancelForRecording through to the real AssistantQaService.
    private sealed class BlockingChatSession : IAssistantChatSession
    {
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new AssistantChunk("partial");
            await Task.Delay(Timeout.Infinite, ct);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SingleSessionFactory(IAssistantChatSession session) : IAssistantChatSessionFactory
    {
        public Task<IAssistantChatSession> StartAsync(AssistantRequest warmupRequest, CancellationToken ct)
            => Task.FromResult(session);
    }

    [Fact]
    public async Task Ask_streams_then_lands_a_validated_turn_and_clears_the_question()
    {
        var (vm, factory, _, reporter) = MakeChat();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("The parties agreed to settle for ten thousand "),
            new AssistantChunk("dollars [00:01:05]"),
            new AssistantDone("cuda", 10, 5),
        });
        var streamed = new List<string>();
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(vm.StreamingText) && vm.StreamingText.Length > 0) streamed.Add(vm.StreamingText); };

        vm.QuestionText = "what was the settlement";
        await vm.AskCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        var turn = Assert.Single(vm.Turns);
        Assert.Equal("what was the settlement", turn.Question);
        Assert.Equal(0, turn.UnverifiableClaims);
        var chip = Assert.Single(turn.Lines.Single(l => l.IsClaim).Chips);
        Assert.True(chip.Verified);
        Assert.Equal("", vm.QuestionText);                       // cleared on success
        Assert.False(vm.IsAsking);
        Assert.Equal("", vm.StreamingText);                      // preview cleared once the turn lands
        Assert.Contains(streamed, s => s.EndsWith("ten thousand "));   // streamed incrementally
    }

    [Fact]
    public async Task Turn_view_carries_the_ai_draft_label_and_provenance()
    {
        var (vm, factory, _, _) = MakeChat();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("The parties agreed to settle for ten thousand dollars [00:01:05]"),
            new AssistantDone("cpu", 10, 5),
        });
        vm.QuestionText = "q";
        await vm.AskCommand.ExecuteAsync(null);

        var turn = Assert.Single(vm.Turns);
        Assert.Equal(AssistantChatViewModel.AiDraftLabel, turn.AiLabel);   // LOCKED: label on ALL chat output
        Assert.Equal("m.gguf \u00B7 CPU \u00B7 prompt 3", turn.ProvenanceLine);   // middle-dot escapes: ASCII test source
    }

    [Fact]
    public async Task No_model_flips_to_the_unavailable_explainer_without_throwing()
    {
        var (vm, _, _, reporter) = MakeChat(modelInstalled: false);
        vm.QuestionText = "q";
        await vm.AskCommand.ExecuteAsync(null);

        Assert.False(vm.IsAvailable);
        Assert.Equal(AssistantChatViewModel.UnavailableText, vm.StatusText);
        Assert.Empty(vm.Turns);
        Assert.Empty(reporter.Errors);                           // an explainer, not an error
    }

    [Fact]
    public async Task Helper_error_reports_and_adds_nothing()
    {
        var (vm, factory, store, reporter) = MakeChat();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("half"), new AssistantError("helper crashed"),
        });
        vm.QuestionText = "q";
        await vm.AskCommand.ExecuteAsync(null);

        Assert.Single(reporter.Errors);                          // surfaced (design 7.7)
        Assert.Empty(vm.Turns);                                  // nothing persisted, nothing rendered
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);
        Assert.False(vm.IsAsking);
        Assert.Equal("q", vm.QuestionText);                      // the question is NOT lost on failure
    }

    [Fact]
    public void Citation_chip_click_raises_navigation_only_for_clickable_chips()
    {
        var (vm, _, _, _) = MakeChat();
        (string Sid, int Seq, string Term)? raised = null;
        vm.CitationNavigationRequested += (sid, seq, term) => raised = (sid, seq, term);

        vm.NavigateChipCommand.Execute(new CitationChip("00:01:05", true, "s1", 3, "settle"));
        Assert.Equal(("s1", 3, "settle"), raised);

        raised = null;
        vm.NavigateChipCommand.Execute(new CitationChip("00:59:59", false, null, -1, ""));
        Assert.Null(raised);                                     // unverified chips never navigate
    }

    [Fact]
    public async Task History_loads_from_the_store()
    {
        var (vm, _, store, _) = MakeChat();
        await store.AppendAsync(new AssistantChatTurn("t1",
            new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero), "old question", "old answer",
            [new AnswerLine("old answer", [], true, true, "no citation")],
            "m.gguf", "cpu", "3", false, null, ["s1"], [], [], 1), CancellationToken.None);

        await vm.LoadHistoryAsync(CancellationToken.None);
        var turn = Assert.Single(vm.Turns);
        Assert.Equal("old question", turn.Question);
        Assert.Equal(1, turn.UnverifiableClaims);                // verdicts render exactly as persisted
    }

    [Fact]
    public async Task Busy_reason_surfaces_as_the_queued_status_while_asking()
    {
        var (vm, factory, _, _) = MakeChat(
            busyReason: () => "Waiting for the recording to finish - the assistant runs one heavy engine at a time.");
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1),
        });
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(vm.StatusText)) statuses.Add(vm.StatusText); };

        vm.QuestionText = "q";
        await vm.AskCommand.ExecuteAsync(null);
        Assert.Contains("Waiting for the recording to finish - the assistant runs one heavy engine at a time.", statuses);
        Assert.Equal("", vm.StatusText);                         // cleared after the turn
    }

    // Review follow-up (Important #1 + Minor #4): the earlier provenance test only compared the
    // const to itself through ChatTurnViewModel.AiLabel, which stays green even if the label text
    // is mutated. Pin the actual locked evidentiary string directly.
    [Fact]
    public void Ai_draft_label_is_the_exact_locked_string()
        => Assert.Equal("AI-generated draft \u2014 not a transcript; verify against the record.",
                        AssistantChatViewModel.AiDraftLabel);

    // Review follow-up (Important #2): InvalidateContext/Shutdown (design 7.1 stale-content
    // re-warm/teardown) had zero coverage. The VM caches _service across asks; InvalidateContext
    // must null it so the NEXT ask calls serviceFactory() again, which (per MakeChat) mints a
    // brand-new AssistantQaService whose internal warm session starts cold - so the next ask must
    // re-StartAsync even though the scripted scope's WarmupRequest.PayloadJson is unchanged across
    // both asks. factory.Warmups.Count going 1 -> 2 is the discriminator: if InvalidateContext were
    // a no-op, the SAME AssistantQaService instance would survive and its ALREADY-warm session
    // (matching payload "P1") would be reused, holding Warmups.Count at 1.
    [Fact]
    public async Task InvalidateContext_forces_the_next_ask_to_rebuild_the_service_and_rewarm()
    {
        var (vm, factory, _, reporter) = MakeChat();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("first answer [00:01:05]"), new AssistantDone("cpu", 1, 1),
        });
        vm.QuestionText = "q1";
        await vm.AskCommand.ExecuteAsync(null);
        Assert.Single(vm.Turns);
        Assert.Single(factory.Warmups);
        Assert.Single(factory.Sessions);

        vm.InvalidateContext();

        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("second answer [00:01:05]"), new AssistantDone("cpu", 1, 1),
        });
        vm.QuestionText = "q2";
        await vm.AskCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        Assert.Equal(2, vm.Turns.Count);
        Assert.Equal(2, factory.Warmups.Count);    // a NEW warm session was started - proves rebuild + rewarm
        Assert.Equal(2, factory.Sessions.Count);
    }

    // Shutdown delegates to InvalidateContext today; this test independently exercises the
    // Shutdown entry point (not just the shared helper) so a future divergence between the two
    // is caught, and doubles as the "tears down without throwing" smoke test.
    [Fact]
    public async Task Shutdown_tears_down_without_throwing_and_forces_a_rewarm_on_the_next_ask()
    {
        var (vm, factory, _, reporter) = MakeChat();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("first answer [00:01:05]"), new AssistantDone("cpu", 1, 1),
        });
        vm.QuestionText = "q1";
        await vm.AskCommand.ExecuteAsync(null);
        Assert.Single(vm.Turns);
        Assert.Single(factory.Warmups);

        Exception? thrown = Record.Exception(() => vm.Shutdown());
        Assert.Null(thrown);
        Assert.False(vm.IsAsking);

        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("second answer [00:01:05]"), new AssistantDone("cpu", 1, 1),
        });
        vm.QuestionText = "q2";
        await vm.AskCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        Assert.Equal(2, vm.Turns.Count);
        Assert.Equal(2, factory.Warmups.Count);    // proves Shutdown disposed rather than no-op'd
    }

    // Branch-7 fix: chat recording-preemption (design 7.1 reverse direction, mirrors the
    // SummarizationService fix already merged for the summarizer). CancelForRecording must reach
    // the running AssistantQaService.AskAsync so a recording start does not let a heavy chat
    // answer keep contending with live Whisper - and a deliberate preempt must NOT surface as a
    // reported error (Design 7.7 is for genuine failures only).
    [Fact]
    public async Task Recording_preempt_cancels_the_ask_without_reporting_an_error()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var session = new BlockingChatSession();
        var factory = new SingleSessionFactory(session);
        var store = new AssistantChatStore(Path.Combine(_root, "assistant", "chats.json"));
        var reporter = new FakeReporter();
        Func<AssistantQaService?> serviceFactory = () => new AssistantQaService(factory, store,
            ct => Task.FromResult<IAsyncDisposable>(new NoopLease()),
            (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System);
        var vm = new AssistantChatViewModel(serviceFactory, store, reporter, a => a());

        var chunkSeen = new TaskCompletionSource();
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(vm.StreamingText) && vm.StreamingText.Length > 0) chunkSeen.TrySetResult(); };

        vm.QuestionText = "q";
        Task askTask = vm.AskCommand.ExecuteAsync(null);
        await chunkSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));   // genuinely mid-run, past the lease

        vm.CancelForRecording();

        await askTask.WaitAsync(TimeSpan.FromSeconds(5));          // AskAsync catches OCE - must not throw out

        Assert.Empty(reporter.Errors);                             // a deliberate preempt is not an error
        Assert.Empty(vm.Turns);                                    // nothing persisted, nothing rendered
        Assert.Equal("q", vm.QuestionText);                        // kept so the user can retry
        Assert.False(vm.IsAsking);
    }

    [Fact]
    public void CancelForRecording_is_a_safe_no_op_before_any_ask()
    {
        var (vm, _, _, _) = MakeChat();
        Exception? thrown = Record.Exception(() => vm.CancelForRecording());
        Assert.Null(thrown);
    }
}
