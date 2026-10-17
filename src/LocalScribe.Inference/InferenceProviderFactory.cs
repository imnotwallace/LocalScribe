// Factory that selects the concrete IInferenceProvider based on the requested type.
// Static, no DI needed — the assistant process calls it once at startup.
namespace LocalScribe.Inference;

public static class InferenceProviderFactory
{
    /// <summary>Create an inference provider. Local returns LocalInferenceProvider;
    /// Remote returns RemoteInferenceProvider.</summary>
    /// <param name="type">Which provider to create.</param>
    /// <param name="modelPath">Path to the GGUF model file (required for Local, ignored for Remote).</param>
    /// <param name="ctxTokens">Context window size (required for Local, ignored for Remote).</param>
    /// <param name="backend">GPU backend: "auto", "cuda", or "cpu" (required for Local, ignored for Remote).</param>
    /// <param name="progress">Progress phase callback (required for Local).</param>
    public static IInferenceProvider Create(
        InferenceProviderType type,
        string modelPath = "",
        int ctxTokens = 0,
        string backend = "auto",
        Action<string>? progress = null)
    {
        progress ??= _ => { };
        return type switch
        {
            InferenceProviderType.Local => new LocalInferenceProvider(modelPath, ctxTokens, backend, progress),
            InferenceProviderType.Remote => throw new NotImplementedException("Remote options not passed via this signature — use CreateRemote"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown provider type"),
        };
    }

    /// <summary>Convenience overload for creating a Remote provider.</summary>
    public static IInferenceProvider CreateRemote(string endpoint, string? apiKey = null, string? model = null, TimeSpan? timeout = null)
    {
        return new RemoteInferenceProvider(endpoint, apiKey, model, timeout ?? TimeSpan.FromSeconds(300));
    }
}
