// Factory that selects the concrete IInferenceProvider based on the requested type.
// Static, no DI needed — the assistant process calls it once at startup.
namespace LocalScribe.Inference;

public static class InferenceProviderFactory
{
    /// <summary>Create a Local inference provider. The engineFactory callback is invoked
    /// once on the first InferAsync call to create and return a loaded LlamaEngine.</summary>
    /// <param name="engineFactory">Callback that creates the LlamaEngine. Called lazily on
    /// first inference. The callback is implemented in Program.cs where LlamaEngine is
    /// accessible.</param>
    public static IInferenceProvider CreateLocal(Func<object> engineFactory)
        => new LocalInferenceProvider(engineFactory);

    /// <summary>Create a Remote inference provider.</summary>
    public static IInferenceProvider CreateRemote(string endpoint, string? apiKey = null, string? model = null, TimeSpan? timeout = null)
        => new RemoteInferenceProvider(endpoint, apiKey, model, timeout ?? TimeSpan.FromSeconds(300));
}
