using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Transcription;

/// <summary>A segment paired with its transcription, the model that produced it (canonical
/// name), and the exact weights file that ran (evidentiary provenance - the file can differ
/// per backend and per engine recreation; review finding 2026-07-13).</summary>
public sealed record TranscribedSegment(AudioSegment Audio, TranscriptionResult Result, string ModelName,
    string WeightsFile = "");
