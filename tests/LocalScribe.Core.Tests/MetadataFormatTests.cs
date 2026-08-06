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
    public void RecordedAudioLines_states_the_fabricated_silence_beside_every_hash()
    {
        // Tier 1 T1-7 (spec 2026-08-05 :148-153): AlignedAudioWriter inserts zeros for every clock
        // gap and pads to the session end. A hash presented WITHOUT that fact certifies synthetic
        // silence as original recorded audio - the sentence has to travel with the number, in one
        // place, so the three formats cannot word it differently.
        var p = new ExportProvenance
        {
            RecordedAudio =
            [
                new RecordedAudioLeg
                { FileName = "local.flac", Sha256 = "aaa", Silence = new FabricatedSilenceSummary(3, 42_000) },
                new RecordedAudioLeg
                { FileName = "remote.flac", Sha256 = "bbb", Silence = new FabricatedSilenceSummary(0, 0) },
                new RecordedAudioLeg { FileName = "local.wav", Sha256 = "ccc", Silence = null },
            ],
        };

        Assert.Equal(
            new[]
            {
                ("Audio SHA-256 (local.flac)",
                    "aaa (includes 3 machine-generated silence spans, 00:00:42 total)"),
                ("Audio SHA-256 (remote.flac)", "bbb (no machine-generated silence)"),
                ("Audio SHA-256 (local.wav)",
                    "ccc (machine-generated silence not recorded for this file)"),
            },
            MetadataFormat.RecordedAudioLines(p));
    }

    [Fact]
    public void RecordedAudioLines_is_empty_for_a_session_with_no_sealed_audio()
        => Assert.Empty(MetadataFormat.RecordedAudioLines(new ExportProvenance()));

    [Fact]
    public void One_fabricated_span_reads_singular()
    {
        var p = new ExportProvenance
        {
            RecordedAudio =
                [new RecordedAudioLeg
                { FileName = "local.flac", Sha256 = "aaa", Silence = new FabricatedSilenceSummary(1, 1500) }],
        };
        Assert.Equal("aaa (includes 1 machine-generated silence span, 00:00:01 total)",
            MetadataFormat.RecordedAudioLines(p).Single().Value);
    }
}
