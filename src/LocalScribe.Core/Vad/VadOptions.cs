namespace LocalScribe.Core.Vad;

/// <summary>Silero VAD segmentation parameters - spec section 4 defaults ("starting defaults,
/// tune in Stage 2"). All tuning happens here, never inline.</summary>
public sealed record VadOptions
{
    public float Threshold { get; init; } = 0.5f;
    public int MinSpeechMs { get; init; } = 250;
    public int MinSilenceMs { get; init; } = 500;
    public int SpeechPadMs { get; init; } = 150;
    public int MaxSegmentMs { get; init; } = 15000;
    public int WindowSizeSamples { get; init; } = 512;
    public int SampleRate { get; init; } = 16000;
}
