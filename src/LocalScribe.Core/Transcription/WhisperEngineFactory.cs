// src/LocalScribe.Core/Transcription/WhisperEngineFactory.cs
namespace LocalScribe.Core.Transcription;

/// <summary>Creates WhisperNetEngine instances from a BackendPlan. Model files resolve via
/// ModelPaths (ggml-{model}.bin); the process-wide native backend order (CUDA -> Vulkan -> CPU)
/// is configured once by the host (OfflineRunner) through Whisper.net RuntimeOptions.</summary>
public sealed class WhisperEngineFactory : IEngineFactory
{
    public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? initialPrompt, CancellationToken ct)
    {
        string path = ModelPaths.Require($"ggml-{plan.ModelName}.bin");
        return Task.FromResult<ITranscriptionEngine>(
            new WhisperNetEngine(path, plan.ModelName, language, initialPrompt));
    }
}
