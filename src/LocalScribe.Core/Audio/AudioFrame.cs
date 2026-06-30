namespace LocalScribe.Core.Audio;
/// <summary>16 kHz mono float samples, stamped with session-relative start time.</summary>
public readonly record struct AudioFrame(SourceKind Source, long StartMs, float[] Samples);
