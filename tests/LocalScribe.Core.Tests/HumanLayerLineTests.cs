using LocalScribe.Core.Projection;

/// <summary>The human-layer disclosure line (Tier 1 T1-8, spec 2026-08-05 :161-166). A .docx served
/// on the other side must not read as fully machine-generated while carrying rewritten lines and
/// omitting auto-deduped ones.</summary>
public class HumanLayerLineTests
{
    [Fact]
    public void Every_category_is_named_and_pluralised()
        => Assert.Equal(
            "3 text corrections, 1 split turn, 5 manual speaker assignments, 2 named speakers, "
            + "4 auto-suppressed duplicate segments",
            MetadataFormat.HumanLayerLine(new HumanLayerCounts
            {
                Corrections = 3, Splits = 1, SpeakerPins = 5, SpeakerNames = 2,
                SuppressedDuplicates = 4,
            }));

    [Fact]
    public void Zero_categories_collapse_rather_than_leaving_stray_separators()
        => Assert.Equal("2 text corrections, 1 auto-suppressed duplicate segment",
            MetadataFormat.HumanLayerLine(new HumanLayerCounts
            { Corrections = 2, SuppressedDuplicates = 1 }));

    [Fact]
    public void An_untouched_transcript_says_none_rather_than_rendering_an_empty_list()
    {
        // "none" is a POSITIVE statement and it is the point: absence of the LINE means an old
        // build, absence of EDITS means this sentence. Conflating the two would let a document with
        // twelve rewritten lines look identical to one with none.
        Assert.Equal("none", MetadataFormat.HumanLayerLine(new HumanLayerCounts()));
    }
}
