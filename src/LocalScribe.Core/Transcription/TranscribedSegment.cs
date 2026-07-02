using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Transcription;

/// <summary>A segment paired with its transcription and the model that produced it.</summary>
public sealed record TranscribedSegment(AudioSegment Audio, TranscriptionResult Result, string ModelName);
