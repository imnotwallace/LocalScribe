using System.Runtime.CompilerServices;
using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

/// <summary>Scripted stand-in for the foundation warm-helper chat session. LOCKED rule
/// (design 2026-07-18 section 8): real-model runs are smoke-only - every automated test goes
/// through these fakes. Linked into App.Tests via Compile Include (leaf-project pattern).</summary>
public sealed class FakeAssistantChatSession : IAssistantChatSession
{
    public List<string> Questions { get; } = [];
    public Queue<IReadOnlyList<AssistantEvent>> Scripted { get; } = new();
    public bool Disposed { get; private set; }

    public async IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Questions.Add(questionPayloadJson);
        IReadOnlyList<AssistantEvent> events = Scripted.Count > 0 ? Scripted.Dequeue() : [];
        foreach (AssistantEvent ev in events)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ev;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Mints one FakeAssistantChatSession per StartAsync, recording every warmup request.
/// ScriptPerSession pre-loads the Nth new session's first ask; tests can also enqueue directly
/// on Sessions[i].Scripted for follow-up asks on a reused session.</summary>
public sealed class FakeAssistantChatSessionFactory : IAssistantChatSessionFactory
{
    public List<AssistantRequest> Warmups { get; } = [];
    public List<FakeAssistantChatSession> Sessions { get; } = [];
    public Queue<IReadOnlyList<AssistantEvent>> ScriptPerSession { get; } = new();

    public Task<IAssistantChatSession> StartAsync(AssistantRequest warmupRequest, CancellationToken ct)
    {
        Warmups.Add(warmupRequest);
        var session = new FakeAssistantChatSession();
        if (ScriptPerSession.Count > 0) session.Scripted.Enqueue(ScriptPerSession.Dequeue());
        Sessions.Add(session);
        return Task.FromResult<IAssistantChatSession>(session);
    }
}
