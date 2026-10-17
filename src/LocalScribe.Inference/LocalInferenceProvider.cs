// Wraps the existing LlamaEngine (GGUF model loaded via LLamaSharp) as an IInferenceProvider.
// The model is loaded LAZILY on the first InferAsync call and cached for reuse (same as the
// existing Program.cs "engine ??=" pattern).
//
// NOTE: This project does NOT reference LocalScribe.Assistant (avoids circular dependency).
// The engine is accessed via dynamic dispatch — the factory function returns an object that
// has InferAsync, LastPromptTokens etc. (the LlamaEngine type).
namespace LocalScribe.Inference;

/// <summary>Local GGUF inference provider backed by LlamaEngine. Loads the model lazily on
/// the first InferAsync call; caches it for reuse; disposes on DisposeAsync.</summary>
internal sealed class LocalInferenceProvider : IInferenceProvider
{
    private readonly Func<object> _engineFactory;
    private object? _engine;
    private readonly object _lock = new();

    /// <summary>Create a local provider. The engineFactory callback creates a LlamaEngine.
    /// Called once on the first InferAsync (lazy, same as the existing Program.cs pattern).</summary>
    public LocalInferenceProvider(Func<object> engineFactory)
    {
        _engineFactory = engineFactory;
    }

    public async Task<InferenceResult> InferAsync(InferenceRequest request, CancellationToken ct = default)
    {
        var engine = GetOrLoadEngine();

        // Build the prompt from the last message and extract maxTokens from the request options.
        var promptText = request.Messages.LastOrDefault()?.Content ?? "";
        var maxTokens = request.MaxTokens;

        // Stream inference and accumulate using dynamic dispatch.
        var sb = new System.Text.StringBuilder();
        int outputTokens = 0;
        var stream = ((dynamic)engine).InferAsync(promptText, maxTokens, ct);
        await foreach (string piece in (System.Collections.Generic.IAsyncEnumerable<string>)(object)stream)
        {
            sb.Append(piece);
            outputTokens++;
        }

        var result = new InferenceResult(
            Text: sb.ToString(),
            PromptTokens: (int)((dynamic)engine).LastPromptTokens,
            CompletionTokens: outputTokens,
            Elapsed: TimeSpan.Zero,
            ProviderType: "Local",
            ProviderInfo: "LlamaEngine");

        return result;
    }

    public Task<ProviderHealth> HealthCheckAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ProviderHealth(
            IsHealthy: true,
            AvailableModels: [],
            Error: null));
    }

    public async ValueTask DisposeAsync()
    {
        object? engine;
        lock (_lock)
        {
            engine = _engine;
            _engine = null;
        }
        if (engine is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (engine is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private object GetOrLoadEngine()
    {
        lock (_lock)
        {
            if (_engine is not null) return _engine;
            _engine = _engineFactory();
            return _engine;
        }
    }
}
