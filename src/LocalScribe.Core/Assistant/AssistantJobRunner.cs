using System.Runtime.CompilerServices;

namespace LocalScribe.Core.Assistant;

/// <summary>Process-boundary seam (the IDiarisationHelper pattern, adapted for a persistent
/// stdin so keepAlive chat can send further requests on the live process). Production impl:
/// App's ProcessAssistantHelper (spawns LocalScribe.Assistant.exe, kills the whole tree).
/// Tests supply fakes that replay canned stdout lines.</summary>
public interface IAssistantProcess : IAsyncDisposable
{
    Task WriteRequestLineAsync(string requestJson, CancellationToken ct);
    /// <summary>Next stdout line, or null at EOF (helper exited).</summary>
    Task<string?> ReadEventLineAsync(CancellationToken ct);
    void Kill();
}

public interface IAssistantProcessFactory
{
    Task<IAssistantProcess> StartAsync(CancellationToken ct);
}

/// <summary>LOCKED contract - feat/matter-qa consumes this exact interface.</summary>
public interface IAssistantJobRunner
{
    IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request, CancellationToken ct);
}

/// <summary>LOCKED contract (warm-chat, design 7.1): the warmup request loads model + prefills
/// the scope context ONCE; each AskAsync sends only {"op":"answer",...} on the live process, so
/// per-question latency is generation-only. DisposeAsync (IAsyncDisposable) kills the helper.</summary>
public interface IAssistantChatSession : IAsyncDisposable
{
    IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson, CancellationToken ct);
}

public interface IAssistantChatSessionFactory
{
    Task<IAssistantChatSession> StartAsync(AssistantRequest warmupRequest, CancellationToken ct);
}

/// <summary>Typed assistant failure for callers needing throw semantics (design 7.7:
/// visible error, nothing persisted).</summary>
public sealed class AssistantException(string message) : Exception(message);

/// <summary>Shared line-pump: reads stdout lines, skips non-protocol noise, applies the
/// inactivity watchdog, synthesizes an error on EOF-before-terminal, ends after done/error.</summary>
internal static class AssistantEventStream
{
    public static async IAsyncEnumerable<AssistantEvent> ReadUntilTerminalAsync(
        IAssistantProcess proc, TimeSpan inactivityTimeout, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            string? line = null;
            bool watchdogTripped = false;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(inactivityTimeout);
                try { line = await proc.ReadEventLineAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { watchdogTripped = true; }
            }
            if (watchdogTripped)
            {
                proc.Kill();
                yield return new AssistantError(
                    $"assistant helper produced no output for {inactivityTimeout.TotalMinutes:0.#} min - killed (watchdog)");
                yield break;
            }
            if (line is null)
            {
                yield return new AssistantError("assistant helper exited before completing the job");
                yield break;
            }
            var evt = AssistantWire.ParseEventLine(line);
            if (evt is null) continue;   // native-lib stdout noise: skip, never fatal
            yield return evt;
            if (evt is AssistantDone or AssistantError) yield break;
        }
    }
}

/// <summary>Spawn-per-job runner (design 7.1: summarize jobs remain spawn-per-job). Cancel =
/// process kill; watchdog default 5 min (CPU prefill emits progress well inside that). Never
/// writes files - the App owns all persistence via AtomicFile.</summary>
public sealed class AssistantJobRunner(IAssistantProcessFactory factory, TimeSpan? inactivityTimeout = null)
    : IAssistantJobRunner
{
    private readonly TimeSpan _inactivityTimeout = inactivityTimeout ?? TimeSpan.FromMinutes(5);

    public async IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using IAssistantProcess proc = await factory.StartAsync(ct);
        using var reg = ct.Register(proc.Kill);   // cancel = kill (design 7.1)
        await proc.WriteRequestLineAsync(AssistantWire.SerializeRequest(request), ct);
        await foreach (var evt in AssistantEventStream.ReadUntilTerminalAsync(proc, _inactivityTimeout, ct))
            yield return evt;
    }
}

/// <summary>Warm keep-alive sessions (design 7.1). StartAsync forces keepAlive:true on the
/// warmup, drains it to done (throwing AssistantException on error/EOF), and hands back the
/// live session. Torn down by DisposeAsync - the CALLER owns idle-timeout/scope-change/
/// staleness teardown policy (matter-qa branch).</summary>
public sealed class AssistantChatSessionFactory(IAssistantProcessFactory factory, TimeSpan? inactivityTimeout = null)
    : IAssistantChatSessionFactory
{
    private readonly TimeSpan _inactivityTimeout = inactivityTimeout ?? TimeSpan.FromMinutes(5);

    public async Task<IAssistantChatSession> StartAsync(AssistantRequest warmupRequest, CancellationToken ct)
    {
        var proc = await factory.StartAsync(ct);
        try
        {
            using var reg = ct.Register(proc.Kill);
            var warm = warmupRequest with { KeepAlive = true };
            await proc.WriteRequestLineAsync(AssistantWire.SerializeRequest(warm), ct);
            await foreach (var evt in AssistantEventStream.ReadUntilTerminalAsync(proc, _inactivityTimeout, ct))
            {
                if (evt is AssistantError err) throw new AssistantException(err.Message);
                if (evt is AssistantDone) return new ChatSession(proc, warm, _inactivityTimeout);
            }
            throw new AssistantException("assistant helper exited during chat warmup");
        }
        catch
        {
            await proc.DisposeAsync();
            throw;
        }
    }

    private sealed class ChatSession(IAssistantProcess proc, AssistantRequest warm, TimeSpan inactivityTimeout)
        : IAssistantChatSession
    {
        public async IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson,
            [EnumeratorCancellation] CancellationToken ct)
        {
            using var reg = ct.Register(proc.Kill);
            var req = warm with { Op = "answer", PayloadJson = questionPayloadJson };
            await proc.WriteRequestLineAsync(AssistantWire.SerializeRequest(req), ct);
            await foreach (var evt in AssistantEventStream.ReadUntilTerminalAsync(proc, inactivityTimeout, ct))
                yield return evt;
        }

        public ValueTask DisposeAsync()
        {
            proc.Kill();   // teardown = process kill (design 7.1)
            return proc.DisposeAsync();
        }
    }
}
