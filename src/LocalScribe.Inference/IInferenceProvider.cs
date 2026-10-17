// Abstraction for switching inference between a local GGUF model and a remote
// OpenAI-compatible endpoint (e.g. llama.cpp at localhost). Both implementations
// live in this library; the factory selects one at startup based on settings.
// LOCKED: callers (Program.cs, SummarizationService) only see InferAsync and the
// event emission contract — they never depend on which provider is wired.
namespace LocalScribe.Inference;

/// <summary>Unified inference abstraction. One instance per assistant process lifetime;
/// callers create it via InferenceProviderFactory and dispose at shutdown.</summary>
public interface IInferenceProvider : IAsyncDisposable
{
    /// <summary>Run inference on the given messages and return the accumulated result.</summary>
    Task<InferenceResult> InferAsync(InferenceRequest request, CancellationToken ct = default);

    /// <summary>Health-check the provider (lightweight: for remote, a /v1/models GET; for
    /// local, a model-load probe). Never throws — returns IsHealthy=false on failure.</summary>
    Task<ProviderHealth> HealthCheckAsync(CancellationToken ct = default);
}

/// <summary>A single inference request: a list of messages and tuning parameters.</summary>
public sealed record InferenceRequest(
    IReadOnlyList<Message> Messages,
    double Temperature,
    int MaxTokens,
    ProviderOptions Options);

/// <summary>A single message in a conversation.</summary>
public sealed record Message(string Role, string Content);

/// <summary>The result of a single inference call.</summary>
public sealed record InferenceResult(
    string Text,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Elapsed,
    string ProviderType,
    string ProviderInfo);

/// <summary>Health status returned by HealthCheckAsync.</summary>
public sealed record ProviderHealth(
    bool IsHealthy,
    IReadOnlyList<string> AvailableModels,
    string? Error);

/// <summary>Provider-agnostic configuration. Each implementation may ignore fields it
/// does not need (local ignores Endpoint/ApiKey; remote ignores Model).</summary>
public sealed record ProviderOptions(
    string Endpoint,
    string? ApiKey,
    string? Model,
    TimeSpan Timeout);

/// <summary>Which provider the factory should create.</summary>
public enum InferenceProviderType
{
    /// <summary>Local GGUF model loaded via LLamaSharp (existing LlamaEngine).</summary>
    Local,

    /// <summary>Remote OpenAI-compatible endpoint (HTTP POST /v1/chat/completions).</summary>
    Remote,
}
