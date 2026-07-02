// src/LocalScribe.Core/Transcription/ITranscriptionEngine.cs
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Transcription;

/// <summary>One transcribed utterance. DetectedLanguage feeds the probe-then-commit
/// language lock (spec section 3); NoSpeechProb feeds the hallucination gate (design "Model & backend").</summary>
public sealed record TranscriptionResult(string Text, string? DetectedLanguage, double? NoSpeechProb);

/// <summary>Humble-object seam over whisper.cpp (or any future NPU/DirectML engine).
/// Language + initial-prompt bias are fixed at creation; the worker recreates the engine
/// on language lock or model downgrade.</summary>
public interface ITranscriptionEngine : IAsyncDisposable
{
    string ModelName { get; }
    Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct);
}

/// <summary>Backend ran out of GPU memory; the worker downgrades one ladder step (spec section 3/section 8.2 VRAM_OOM).</summary>
public sealed class VramOutOfMemoryException : Exception
{
    public VramOutOfMemoryException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IEngineFactory
{
    Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? initialPrompt, CancellationToken ct);
}
