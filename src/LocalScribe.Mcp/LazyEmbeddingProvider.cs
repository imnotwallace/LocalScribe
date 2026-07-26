using LocalScribe.Core.Assistant;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Transcription;

namespace LocalScribe.Mcp;

/// <summary>Resolves the embed helper on FIRST semantic call, not at startup: manifest load
/// hash-verifies the GGUF (seconds) and the server must come up instantly for lexical/read
/// tools. 90s idle reclaim (vs the App's 5min): MCP queries are bursty, and the shorter
/// window keeps any two-warm-helpers overlap with a running App brief (spec: Concurrency).
/// Contract guarantee (task-8 amendment 3): McpCorpus.SearchSemanticAsync trusts GetAsync to
/// throw only McpToolException/OperationCanceledException. The manifest load here can raise
/// raw IO/JSON exceptions (missing models dir, corrupt manifest, hash mismatch) — every other
/// failure is caught and rethrown as McpToolException so that contract holds.</summary>
public sealed class LazyEmbeddingProvider : IMcpEmbeddingProvider, IAsyncDisposable
{
    public const int SemanticDim = 256; // mirrors App.SemanticDim — the corpus-wide sidecar dim

    private readonly AssistantManifestCache _manifest =
        new(ct => AssistantModelManifest.LoadAsync(ModelPaths.ModelsRoot, ct));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (IEmbeddingClient Client, string Method)? _resolved;

    public async Task<(IEmbeddingClient Client, string Method)> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_resolved is { } r) return r;
            try
            {
                var manifest = await _manifest.GetAsync(ct);
                if (manifest.EmbeddingModel is not { } model)
                    throw new McpToolException(
                        "semantic unavailable: embedding model not installed (run tools/fetch-models.ps1)", "error");
                if (AssistantHelperLocator.FindExe() is not string exe)
                    throw new McpToolException(
                        "semantic unavailable: " + AssistantHelperLocator.MissingMessage, "error");
                var client = new AssistantEmbeddingClient(new ProcessAssistantHelper(exe),
                    model.FilePath, SemanticDim, TimeSpan.FromSeconds(90));
                _resolved = (client, EmbeddingMethod.For(model.FilePath, SemanticDim));
                return _resolved.Value;
            }
            catch (Exception ex) when (ex is not McpToolException and not OperationCanceledException)
            {
                throw new McpToolException($"semantic unavailable: {ex.Message}", "error");
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_resolved is { } r) await r.Client.DisposeAsync();
    }
}
