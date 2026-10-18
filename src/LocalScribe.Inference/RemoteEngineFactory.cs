// Remote engine factory: implements IEngineFactory to create RemoteTranscriptionEngine
// instances for the transcription pipeline when remote backend is configured.
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

namespace LocalScribe.Inference;

/// <summary>Factory that creates RemoteTranscriptionEngine instances. Used by the transcription
// pipeline (OfflinePipelineRunner, RetranscriptionRunner) when remote transcription is configured
// in settings. The remote endpoint, API key, and model are configured at factory creation time.</summary>
public sealed class RemoteEngineFactory : IEngineFactory
{
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly string? _model;
    private readonly TimeSpan _timeout;

    /// <summary>Create a remote engine factory. All engines created by this factory share the same
    /// endpoint, API key, and model settings.</summary>
    /// <param name="endpoint">Remote Whisper API base URL.</param>
    /// <param name="apiKey">Optional API key.</param>
    /// <param name="model">Model name to send to the API. Null = use the model from the BackendPlan.</param>
    /// <param name="timeout">Request timeout (default 300s for long audio).</param>
    public RemoteEngineFactory(string endpoint, string? apiKey = null, string? model = null, TimeSpan? timeout = null)
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
        _model = model;
        _timeout = timeout ?? TimeSpan.FromSeconds(300);
    }

    public Task<ITranscriptionEngine> CreateAsync(
        BackendPlan plan, string? language, string? initialPrompt, CancellationToken ct)
    {
        // For remote, the BackendPlan's backend is irrelevant (no local GPU/CPU).
        // The model is either the configured default or overridden by the plan's model name.
        string model = _model ?? plan.ModelName;

        var engine = new RemoteTranscriptionEngine(
            endpoint: _endpoint,
            apiKey: _apiKey,
            model: model,
            timeout: _timeout,
            initialPrompt: initialPrompt);

        return Task.FromResult<ITranscriptionEngine>(engine);
    }
}
