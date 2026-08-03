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
}
