// Factory that selects the concrete IInferenceProvider based on the requested type.
// Static, no DI needed — the assistant process calls it once at startup.
namespace LocalScribe.Inference;

public static class InferenceProviderFactory
{
    /// <summary>Create an inference provider. Local returns LocalInferenceProvider;
    /// Remote returns RemoteInferenceProvider (both throw NotImplementedException for now —
    /// Task 2 and Task 3 fill them in).</summary>
    public static IInferenceProvider Create(InferenceProviderType type, ProviderOptions? options = null)
    {
        options ??= new ProviderOptions(string.Empty, null, null, TimeSpan.Zero);
        return type switch
        {
            InferenceProviderType.Local   => throw new NotImplementedException("LocalInferenceProvider — Task 2"),
            InferenceProviderType.Remote  => throw new NotImplementedException("RemoteInferenceProvider — Task 3"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown provider type"),
        };
    }
}
