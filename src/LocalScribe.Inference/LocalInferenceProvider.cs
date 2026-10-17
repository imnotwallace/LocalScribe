// Wraps the existing LlamaEngine (GGUF model loaded via LLamaSharp) as an IInferenceProvider.
// The model is loaded LAZILY on the first InferAsync call and cached for reuse (same as the
// existing Program.cs "engine ??=" pattern). ConfigureNativeLoad is called at first use —
// the caller must ensure the factory creates this provider BEFORE any native API touch.
using LocalScribe.Assistant;
using LocalScribe.Core.Assistant;
namespace LocalScribe.Inference;

/// <summary>Local GGUF inference provider backed by LlamaEngine. Loads the model lazily on
/// the first InferAsync call; caches it for reuse; disposes on DisposeAsync.</summary>
internal sealed class LocalInferenceProvider : IInferenceProvider
{
    private readonly string _modelPath;
    private readonly int _ctxTokens;
    private readonly string _backend;
    private LlamaEngine? _engine;
    private readonly object _lock = new();
    private readonly Action<string> _progress;

    /// <summary>Create a local provider. Does NOT load the model yet — that happens on
    /// the first InferAsync call (lazy, same as the existing Program.cs pattern).</summary>
    /// <param name="modelPath">Absolute path to the GGUF model file.</param>
    /// <param name="ctxTokens">Context window size for the model.</param>
    /// <param name="backend">GPU backend request: "auto", "cuda", or "cpu".</param>
    /// <param name="progress">Progress callback (phases like "load-cuda", "prefill").</param>
    public LocalInferenceProvider(
        string modelPath,
        int ctxTokens,
        string backend,
        Action<string> progress)
    {
        _modelPath = modelPath;
        _ctxTokens = ctxTokens;
        _backend = backend;
        _progress = progress;
    }

    public async Task<InferenceResult> InferAsync(InferenceRequest request, CancellationToken ct = default)
    {
        var engine = GetOrLoadEngine();

        // Build the payload JSON that LlamaEngine.ReadPayload expects.
        var payload = AssistantWire.PromptPayload(request.Messages.LastOrDefault()?.Content ?? "", request.MaxTokens);
        var (prompt, maxTokens) = LlamaEngine.ReadPayload(payload);

        // Emit prefill phase.
        _progress("prefill");

        // Stream inference and accumulate.
        var sb = new System.Text.StringBuilder();
        int outputTokens = 0;
        await foreach (string piece in engine.InferAsync(prompt, maxTokens, ct))
        {
            sb.Append(piece);
            outputTokens++;
        }

        var result = new InferenceResult(
            Text: sb.ToString(),
            PromptTokens: engine.LastPromptTokens,
            CompletionTokens: outputTokens,
            Elapsed: TimeSpan.Zero, // not tracked in local path (no stopwatch in LlamaEngine)
            ProviderType: "Local",
            ProviderInfo: Path.GetFileName(_modelPath));

        return result;
    }

    public Task<ProviderHealth> HealthCheckAsync(CancellationToken ct = default)
    {
        // Local is always healthy — failure to load is a job-level error, not a health issue.
        return Task.FromResult(new ProviderHealth(
            IsHealthy: true,
            AvailableModels: [],
            Error: null));
    }

    public async ValueTask DisposeAsync()
    {
        LlamaEngine? engine;
        lock (_lock)
        {
            engine = _engine;
            _engine = null;
        }
        engine?.Dispose();
    }

    private LlamaEngine GetOrLoadEngine()
    {
        lock (_lock)
        {
            if (_engine is not null) return _engine;

            // ConfigureNativeLoad must happen ONCE per process, BEFORE the first native API touch.
            // The factory creates this provider at startup, so we're safe to call it here.
            LlamaEngine.ConfigureNativeLoad(_backend);

            _engine = LlamaEngine.Load(_modelPath, _ctxTokens, _backend, _progress);
            return _engine;
        }
    }
}
