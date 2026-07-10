using LocalScribe.Core.Projection;

public class TextDistanceTests
{
    [Fact]
    public void Containment_finds_a_perfect_prefix_echo()
    {
        // The observed 2026-07-10 pair 1: whole-string similarity is only 0.50, but the shorter
        // text is contained verbatim - containment must catch it.
        double sim = TextDistance.ContainmentSimilarity(
            "So I'm gonna be testing sound.",
            "Okay, so I'm gonna be testing sound. I'm testing, I'm testing.");
        Assert.True(sim >= 0.99, $"expected ~1.0, got {sim}");
    }

    [Fact]
    public void Containment_refuses_short_interjections()
    {
        // "yeah"/"okay" are contained in nearly everything - the length guard must zero them out.
        Assert.Equal(0.0, TextDistance.ContainmentSimilarity("Yeah.", "yeah I think we should file it"));
        Assert.Equal(0.0, TextDistance.ContainmentSimilarity("okay so", "okay so let us begin the hearing"));
    }

    [Fact]
    public void Containment_does_not_rescue_a_garbled_echo()
    {
        // The observed pair 3: whisper garbled the two copies differently - no safe text gate
        // passes this, and the design accepts it stays visible (documented limitation).
        double sim = TextDistance.ContainmentSimilarity("hold on to my name", "Hold on my mind.");
        Assert.True(sim < 0.85, $"expected < 0.85, got {sim}");
    }

    [Fact]
    public void Containment_equals_whole_string_similarity_when_lengths_match()
    {
        // Equal token counts degenerate to a single window == the whole string, so containment
        // can never LOWER the effective gate for classic same-length pairs.
        string a = "I pushed the auth changes last night", b = "I pushed the auth change last night";
        Assert.Equal(TextDistance.NormalizedSimilarity(a, b),
                     TextDistance.ContainmentSimilarity(a, b), 3);
    }
}
