// tests/LocalScribe.Core.Tests/WhisperFixtureTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

[Trait("Category", "Fixture")]
public class WhisperFixtureTests
{
    [Fact]
    public async Task Tiny_model_transcribes_synthetic_tone_to_something_or_nothing_but_never_throws()
    {
        // Smoke-level: engine loads, processes 2 s of low-level noise, returns without throwing.
        // Content assertions live in the golden-corpus E2E (Task 14) on real speech.
        var factory = new WhisperEngineFactory();
        await using var engine = await factory.CreateAsync(
            new BackendPlan(Backend.Cpu, "tiny.en"), "en", initialPrompt: null, default);

        var rng = new Random(42);
        var pcm = new float[32000];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(rng.NextDouble() * 0.01 - 0.005);

        var result = await engine.TranscribeAsync(
            new AudioSegment(SourceKind.Local, 0, 2000, pcm), default);
        Assert.NotNull(result.Text);                    // may be empty - that is fine (noise)

        // Finding I1: the live engine must populate NoSpeechProb from whisper.cpp segments so
        // the worker's hallucination gate (>= 0.6) is not permanently inert in production. This
        // noise fixture may legitimately yield zero segments (Text == ""), in which case the
        // empty-text gate already drops it and NoSpeechProb staying null is correct - so pin
        // only the non-empty invariant: any produced text must carry a no-speech probability.
        Assert.True(result.Text.Length == 0 || result.NoSpeechProb is not null);
    }
}
