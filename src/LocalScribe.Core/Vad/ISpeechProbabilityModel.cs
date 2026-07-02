namespace LocalScribe.Core.Vad;

/// <summary>Humble-object seam: the only thing the ONNX adapter provides. One call per
/// 512-sample window; stateful models keep their recurrence internally.</summary>
public interface ISpeechProbabilityModel
{
    float SpeechProbability(ReadOnlySpan<float> window);
    void Reset();
}
