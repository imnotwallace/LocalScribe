// src/LocalScribe.Core/Transcription/WhisperEngineFactory.cs
namespace LocalScribe.Core.Transcription;

/// <summary>Creates WhisperNetEngine instances from a BackendPlan. Model files resolve via
/// ModelFileResolver (quantized weights preferred on CPU/Vulkan, fp16 on CUDA) + ModelPaths;
/// the process-wide native backend order (CUDA -> Vulkan -> CPU) is configured once by the
/// host (OfflineRunner) through Whisper.net RuntimeOptions.</summary>
public sealed class WhisperEngineFactory : IEngineFactory
{
    public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? initialPrompt, CancellationToken ct)
    {
        string file = ModelFileResolver.Resolve(plan.Backend, plan.ModelName,
            f => File.Exists(ModelPaths.Resolve(f)));
        string path = ModelPaths.Require(file);
        return Task.FromResult<ITranscriptionEngine>(
            new WhisperNetEngine(path, plan.ModelName, language, initialPrompt, plan.EffectiveThreads));
    }
}
