using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;

/// <summary>The Start-time engine disclosure (Tier 1 T1-6, spec 2026-08-05 :66-72). The owner
/// ruling froze the live model cap, so the divergence between live (small.en/base.en) and import
/// (large-v3-turbo) must be DISCLOSED instead of removed - in the UI at Start, in a transcript
/// marker, and in export metadata. This is the one string all three derive from.</summary>
public class EngineDisclosureTests
{
    [Theory]
    [InlineData("large-v3-turbo", "Best accuracy at fast speed")]
    [InlineData("large-v3", "Best accuracy")]
    [InlineData("medium.en", "Good accuracy")]
    [InlineData("small.en", "Decent accuracy")]
    [InlineData("base.en", "Basic accuracy")]
    [InlineData("tiny", "Lowest accuracy")]
    public void AccuracyTier_is_the_leading_phrase_of_the_catalog_subtitle(string name, string tier)
        => Assert.Equal(tier, WhisperModelCatalog.AccuracyTier(name));

    [Fact]
    public void AccuracyTier_is_empty_for_the_auto_sentinel_and_for_unknown_weights()
    {
        // Describe() never throws and never returns null: an unknown user-dropped ggml gets
        // Subtitle "". "auto" is a Settings-only sentinel that BackendSelector always resolves to a
        // concrete name before anything reaches here, so it can only appear by mistake - and an
        // accuracy claim about "auto" would be meaningless.
        Assert.Equal("", WhisperModelCatalog.AccuracyTier("distil-large-v3.5"));
        Assert.Equal("", WhisperModelCatalog.AccuracyTier("auto"));
    }

    [Fact]
    public void Line_names_the_model_the_backend_and_the_tier()
        => Assert.Equal("base.en (CPU), Basic accuracy",
            EngineDisclosure.Line("base.en", Backend.Cpu));

    [Fact]
    public void Line_degrades_to_model_and_backend_when_the_model_is_not_cataloged()
    {
        // Open-set rule: a user-dropped ggml must still record WHAT ran. A dangling ", " would be
        // worse than no tier at all in an evidentiary line.
        Assert.Equal("distil-large-v3.5 (CUDA)",
            EngineDisclosure.Line("distil-large-v3.5", Backend.Cuda));
    }
}
