using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

[Trait("Category", "Fixture")]
public class SileroVadFixtureTests
{
    [Fact]
    public void Tone_vs_silence_probabilities_separate()
    {
        using var model = new SileroVadModel(ModelPaths.Require("silero_vad.onnx"));

        // Synthetic excitation: a 220 Hz sawtooth is not speech, so we assert both sides
        // stay below the speech threshold and the silence side hard.
        // ADJUDICATED EDIT (2026-07-02 runbook): the original relative-ordering assertion
        // (buzz >= silence on the last window) was falsified once the adapter gained the
        // v5 context window - the reference-correct model actively suppresses a sustained
        // non-speech buzz BELOW the silence residual (measured buzz seq 0.055 -> 0.0006 vs
        // silence ~0.005). The ordering only held under the broken no-context adapter.
        // True invariant kept: neither silence nor a non-speech buzz classifies as speech.
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
        Assert.True(pBuzz < 0.5f, $"non-speech buzz prob {pBuzz} classified as speech");
    }

    [Fact]
    public void Speech_like_vowel_scores_above_threshold()
    {
        // Regression guard for the v5 context-window contract: the reference OnnxWrapper
        // prepends the previous window's last 64 samples to every 512-sample input. Without
        // that context the model scores real speech near zero (observed max 0.10 on TTS
        // speech, 0.002 on this vowel) while still passing the relative tone-vs-silence
        // check above. A synthetic vowel (f0 120 Hz + vibrato, formant envelope at
        // 700/1150/2600 Hz) scores ~0.76 through the correct adapter, so >= 0.5 is a
        // stable absolute bar that fails hard when context handling regresses.
        using var model = new SileroVadModel(ModelPaths.Require("silero_vad.onnx"));

        const int sampleCount = 16000; // 1 s at 16 kHz
        var vowel = new float[sampleCount];
        double phase = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            double t = i / 16000.0;
            double f0 = 120 + 8 * Math.Sin(2 * Math.PI * 5 * t); // vibrato
            phase += 2 * Math.PI * f0 / 16000.0;
            double s = 0;
            foreach (var (fc, amp) in new[] { (700.0, 1.0), (1150.0, 0.6), (2600.0, 0.25) })
                for (int h = 1; h * f0 < 4000; h++)
                {
                    double w = amp * Math.Exp(-Math.Pow((h * f0 - fc) / 250.0, 2));
                    if (w > 0.01) s += w * Math.Sin(h * phase);
                }
            double fade = Math.Min(1.0, Math.Min(i / 800.0, (sampleCount - i) / 800.0));
            vowel[i] = (float)(0.25 * s * fade);
        }

        model.Reset();
        float max = 0f;
        for (int off = 0; off + 512 <= sampleCount; off += 512)
            max = Math.Max(max, model.SpeechProbability(vowel.AsSpan(off, 512)));

        Assert.True(max >= 0.5f, $"speech-like vowel max prob {max} below 0.5 - " +
            "silero context-window handling likely regressed");
    }
}
