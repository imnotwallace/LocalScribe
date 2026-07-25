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
    // Idle reclaim (final review 2026-07-25): a true "nobody has called in a while, kill the warm
    // helper" timer, re-armed at the end of every EmbedAsync (success or failure) and cancelled
    // at the start of the next one. This is DIFFERENT from the _inactivityTimeout passed into
    // ReadUntilTerminalAsync below - that one is a mid-request hang guard (a helper that stopped
    // responding partway through a batch); this one reclaims a helper that is sitting idle warm.
    private CancellationTokenSource? _idleCts;

    public async Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            CancelIdleReclaim();   // this call is about to use the process - the idle timer must not race it
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
        finally { ArmIdleReclaim(); _gate.Release(); }
    }

    public async ValueTask ReleaseAsync()
    {
        await _gate.WaitAsync();
        try { CancelIdleReclaim(); await KillAndForgetAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();

    /// <summary>(Re)arms the idle-reclaim timer: cancels+disposes whatever was pending and starts
    /// a fresh one. Called at the end of every EmbedAsync (including failed calls - a repeatedly
    /// failing helper must not stay "warm" forever either) so the LATEST call always owns the
    /// window before reclaim fires.</summary>
    private void ArmIdleReclaim()
    {
        var old = _idleCts;
        _idleCts = new CancellationTokenSource();
        old?.Cancel();
        old?.Dispose();
        _ = IdleReclaimAsync(_idleCts.Token);
    }

    private void CancelIdleReclaim()
    {
        if (_idleCts is { } cts) { cts.Cancel(); cts.Dispose(); _idleCts = null; }
    }

    private async Task IdleReclaimAsync(CancellationToken ct)
    {
        try { await Task.Delay(_inactivityTimeout, ct); }
        catch (OperationCanceledException) { return; }   // superseded by a newer call or dispose
        try { await ReleaseAsync(); } catch { }          // reclaim is best-effort
    }

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
