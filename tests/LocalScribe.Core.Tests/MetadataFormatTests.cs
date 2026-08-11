// tests/LocalScribe.Core.Tests/MetadataFormatTests.cs
using LocalScribe.Core.Projection;

public class MetadataFormatTests
{
    private static readonly DateTimeOffset Started = new(2026, 6, 30, 14, 32, 0, TimeSpan.Zero);

    private static SessionTextView View(DateTimeOffset? ended, long durationMs)
        => new("Weekly Sync", [], [], Started, ended, durationMs, "Teams", "", null);

    [Fact]
    public void DateLine_pairs_start_and_end_with_rounded_minutes()
        => Assert.Equal("2026-06-30 14:32 - 15:09 (37 min)",
            MetadataFormat.DateLine(View(Started.AddMinutes(37), 2220000)));

    [Fact]
    public void DateLine_omits_the_end_when_the_session_has_not_ended()
        => Assert.Equal("2026-06-30 14:32 (37 min)",
            MetadataFormat.DateLine(View(null, 2220000)));

    [Fact]
    public void SpeakersHeard_lists_distinct_names_in_first_appearance_order()
    {
        // Distinct from Participants, which is user-curated metadata: this is who actually speaks
        // in the rows (design 2026-08-03 section 6).
        var rows = new[]
        {
            new DisplayRow { StartMs = 0, DisplayName = "Bob", Text = "One." },
            new DisplayRow { IsMarker = true, StartMs = 10, Text = "device changed" },
            new DisplayRow { StartMs = 20, DisplayName = "Sam", Text = "Two." },
            new DisplayRow { StartMs = 30, DisplayName = "Bob", Text = "Three." },
        };
        Assert.Equal("Bob, Sam", MetadataFormat.SpeakersHeard(rows));
    }

    [Fact]
    public void DisclaimerFor_null_counts_reads_as_untouched()
        // Every all-default ExportProvenance (i.e. every renderer unit test) lands here.
        => Assert.Equal(ExportNotices.Disclaimer, MetadataFormat.DisclaimerFor(null));

    [Fact]
    public void DisclaimerFor_all_zero_counts_reads_as_untouched()
        => Assert.Equal(ExportNotices.Disclaimer,
            MetadataFormat.DisclaimerFor(new HumanLayerCounts()));

    [Theory]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, 1, 0, 0)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(0, 0, 0, 1)]
    public void Any_single_human_act_flips_the_disclaimer_to_the_edited_variant(
        int corrections, int splits, int pins, int names)
        // All four are human acts that change what the document says or who it attributes speech
        // to. Naming a speaker is not a text correction, but it IS a person's judgement rendered
        // into the body, and understating that is the overclaim of purity the variant exists to
        // prevent.
        => Assert.Equal(ExportNotices.DisclaimerEdited,
            MetadataFormat.DisclaimerFor(new HumanLayerCounts
            {
                Corrections = corrections, Splits = splits,
                SpeakerPins = pins, SpeakerNames = names,
            }));

    [Fact]
    public void Suppressed_duplicates_alone_never_claim_a_person_reviewed_the_transcript()
    {
        // PhantomBleedDedup is a MACHINE omission. A document whose only non-zero count is
        // SuppressedDuplicates was touched by nobody, and saying otherwise would be a false
        // claim in a document served on the other side. The Omitted: line discloses it instead.
        Assert.Equal(ExportNotices.Disclaimer,
            MetadataFormat.DisclaimerFor(new HumanLayerCounts { SuppressedDuplicates = 9 }));
    }

    [Fact]
    public void OmittedLine_is_null_when_nothing_was_suppressed()
    {
        // Silent on the common case, and silent on a single-leg import, which suppresses nothing.
        Assert.Null(MetadataFormat.OmittedLine(null));
        Assert.Null(MetadataFormat.OmittedLine(new HumanLayerCounts()));
        Assert.Null(MetadataFormat.OmittedLine(new HumanLayerCounts { Corrections = 3 }));
    }

    [Fact]
    public void OmittedLine_names_the_count_and_the_reason()
        // The one count the spec calls concealment if hidden (Tier 1 T1-8): a reader can tell
        // hiding happened and how often, even though the hidden text is not in the document.
        => Assert.Equal("4 duplicate turns (echo)",
            MetadataFormat.OmittedLine(new HumanLayerCounts { SuppressedDuplicates = 4 }));

    [Fact]
    public void One_omitted_turn_reads_singular()
        => Assert.Equal("1 duplicate turn (echo)",
            MetadataFormat.OmittedLine(new HumanLayerCounts { SuppressedDuplicates = 1 }));
}
