using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Pipeline;

/// <summary>A finalized VAD utterance: 16 kHz mono PCM with session-relative padded
/// onset/offset times (design "Components & interfaces"; spec section 4).</summary>
public sealed record AudioSegment(SourceKind Source, long StartMs, long EndMs, ReadOnlyMemory<float> Pcm);
