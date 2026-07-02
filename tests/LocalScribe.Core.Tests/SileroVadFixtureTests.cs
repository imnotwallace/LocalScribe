using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

[Trait("Category", "Fixture")]
public class SileroVadFixtureTests
{
    [Fact]
    public void Tone_vs_silence_probabilities_separate()
    {
        using var model = new SileroVadModel(ModelPaths.Require("silero_vad.onnx"));

        // Synthetic excitation: a 220 Hz sawtooth is not speech, so we only assert the
        // *silence* side hard plus the relative ordering, never absolute speech scores.
        var silence = new float[512];
        var buzz = new float[512];
        for (int i = 0; i < buzz.Length; i++)
            buzz[i] = (float)(0.6 * ((i * 220.0 / 16000.0) % 1.0 * 2 - 1));

        model.Reset();
        float pSilence = 0f;
        for (int w = 0; w < 10; w++) pSilence = model.SpeechProbability(silence);

        model.Reset();
        float pBuzz = 0f;
        for (int w = 0; w < 10; w++) pBuzz = model.SpeechProbability(buzz);

        Assert.True(pSilence < 0.3f, $"silence prob {pSilence} not low");
        Assert.True(pBuzz >= pSilence, "signal should not score below silence");
    }
}
