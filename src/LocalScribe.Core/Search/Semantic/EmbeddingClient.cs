using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Search.Semantic;

public sealed record EmbeddingBatch(IReadOnlyList<float[]> Embeddings, string Method);

/// <summary>The Core seam over the helper's embed op. ONE warm process max (memory rule):
/// backfill and queries share this client; ReleaseAsync kills the helper (recording start,
/// shutdown) and the next EmbedAsync respawns it lazily.</summary>
public interface IEmbeddingClient : IAsyncDisposable
{
    Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts, CancellationToken ct);
    ValueTask ReleaseAsync();
}

/// <summary>Production IEmbeddingClient: keepAlive embed requests on a persistent-stdin helper
/// (the AssistantChatSessionFactory transport shape, without KV state - embed jobs are stateless,
/// so a killed process costs only the ~2s model reload). Serialized: one in-flight batch at a
/// time; a query issued mid-backfill waits behind at most one batch (~1s). Any failure kills the
/// process and throws AssistantException - the caller owns retry policy.</summary>
public sealed class AssistantEmbeddingClient(IAssistantProcessFactory factory, string modelPath,
    int dim, TimeSpan? inactivityTimeout = null) : IEmbeddingClient
{
    private readonly TimeSpan _inactivityTimeout = inactivityTimeout ?? TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAssistantProcess? _proc;

    public async Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _proc ??= await factory.StartAsync(ct);
            var request = new AssistantRequest("embed", modelPath, CtxTokens: 2048,
                Backend: "cpu", KeepAlive: true,
                AssistantWire.EmbedPayload(kind, texts, dim));
            try
            {
                await _proc.WriteRequestLineAsync(AssistantWire.SerializeRequest(request), ct);
                AssistantEmbedResult? result = null;
                await foreach (var evt in AssistantEventStream.ReadUntilTerminalAsync(
                    _proc, _inactivityTimeout, ct))
                {
                    if (evt is AssistantEmbedResult r) result = r;
                    if (evt is AssistantError err) throw new AssistantException(err.Message);
                    if (evt is AssistantDone)
                        return result is not null
                            ? new EmbeddingBatch(result.Embeddings, result.Method)
                            : throw new AssistantException("embed done without an embedResult");
                }
                throw new AssistantException("embed helper exited before completing the batch");
            }
            catch (OperationCanceledException)
            {
                await KillAndForgetAsync();   // poisoned pipe either way: next call respawns fresh
                throw;
            }
            catch (AssistantException)
            {
                await KillAndForgetAsync();
                throw;
            }
            catch (Exception ex)
            {
                await KillAndForgetAsync();
                throw new AssistantException("embed helper transport failure: " + ex.Message);
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask ReleaseAsync()
    {
        await _gate.WaitAsync();
        try { await KillAndForgetAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();

    private async Task KillAndForgetAsync()
    {
        if (_proc is { } p)
        {
            _proc = null;
            p.Kill();
            await p.DisposeAsync();
        }
    }
}
