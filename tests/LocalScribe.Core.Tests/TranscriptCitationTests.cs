using LocalScribe.Core.Projection;

namespace LocalScribe.Core.Tests;

/// <summary>The read view's two clipboard payloads (Tier 1 plan D, T1-9, 2026-08-05). The
/// citation shape is defined ONCE, here, and composes only from values MetadataFormat /
/// ExportProvenance already produce. Pure and Core-side so it is testable without any window -
/// the App suite has no STA harness, so a payload built in code-behind would be untestable.</summary>
public sealed class TranscriptCitationTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 14, 9, 0, 0, TimeSpan.Zero);

    private static DisplayRow Turn(long startMs, string? name, string text)
        => new() { StartMs = startMs, EndMs = startMs + 4000, DisplayName = name, Text = text };

    private static DisplayRow Marker(long startMs, string text)
        => new() { IsMarker = true, StartMs = startMs, EndMs = startMs, Text = text };

    [Fact]
    public void A_citation_is_quote_speaker_stamp_session_and_version()
    {
        string cite = TranscriptCitation.Format(
            Turn(2_472_000, "J. Smith", "I never signed that."),
            "R v Smith call", Start, "v2-large-v3-turbo-2026-07-14");

        Assert.Equal(
            "\"I never signed that.\" - J. Smith, 00:41:12, R v Smith call of 2026-07-14 (transcript v2)",
            cite);
    }

    [Fact]
    public void An_original_transcript_cites_as_v1_with_no_special_casing()
    {
        // TranscriptVersions.ShortId("v1") returns "v1" - the same call handles both.
        string cite = TranscriptCitation.Format(Turn(0, "Me", "Morning."), "Doe intake", Start, "v1");
        Assert.EndsWith("(transcript v1)", cite);
    }

    [Fact]
    public void The_stamp_is_truncated_never_rounded()
    {
        // AssistantCitationFormat's locked rule: a rounded-up anchor could point PAST the segment
        // start, so 41:12.900 cites as 00:41:12, not 00:41:13.
        string cite = TranscriptCitation.Format(Turn(2_472_900, "J. Smith", "x"), "T", Start, "v1");
        Assert.Contains("00:41:12", cite);
        Assert.DoesNotContain("00:41:13", cite);
    }

    [Fact]
    public void An_unnamed_turn_drops_the_speaker_clause_rather_than_citing_an_empty_name()
    {
        string cite = TranscriptCitation.Format(Turn(1000, null, "unattributed"), "T", Start, "v1");
        Assert.Equal("\"unattributed\" - 00:00:01, T of 2026-07-14 (transcript v1)", cite);
    }

    [Fact]
    public void Plain_text_is_the_turn_text_verbatim_one_row_per_line_with_crlf()
    {
        string text = TranscriptCitation.PlainText(
            [Turn(0, "Me", "one"), Turn(5000, "Them", "two")]);

        Assert.Equal("one\r\ntwo", text);
    }

    [Fact]
    public void Markers_are_skipped_by_both_payloads()
    {
        // Extended selection means a marker row CAN be inside SelectedItems even though the row
        // context menu is suppressed for markers. A marker is machine bookkeeping, not evidence
        // a solicitor quotes.
        DisplayRow[] rows = [Turn(0, "Me", "one"), Marker(1000, "Recording paused"), Turn(5000, "Me", "two")];

        Assert.Equal("one\r\ntwo", TranscriptCitation.PlainText(rows));
        string cited = TranscriptCitation.WithCitations(rows, "T", Start, "v1");
        Assert.DoesNotContain("Recording paused", cited);
    }

    [Fact]
    public void Multiple_citations_are_blank_line_separated_in_row_order()
    {
        string cited = TranscriptCitation.WithCitations(
            [Turn(0, "Me", "one"), Turn(5000, "Them", "two")], "T", Start, "v1");

        Assert.Equal(
            "\"one\" - Me, 00:00:00, T of 2026-07-14 (transcript v1)\r\n\r\n"
            + "\"two\" - Them, 00:00:05, T of 2026-07-14 (transcript v1)",
            cited);
    }

    [Fact]
    public void An_empty_or_marker_only_selection_yields_an_empty_string_not_a_stray_separator()
    {
        Assert.Equal("", TranscriptCitation.PlainText([]));
        Assert.Equal("", TranscriptCitation.WithCitations([Marker(0, "Recording paused")], "T", Start, "v1"));
    }

    [Fact]
    public void Row_text_is_emitted_verbatim_and_is_never_trimmed_or_reflowed()
    {
        // Transcripts are evidence: a copy path may not silently normalise what it copies.
        const string awkward = "  spaced   out  ";
        Assert.Equal(awkward, TranscriptCitation.PlainText([Turn(0, "Me", awkward)]));
        Assert.Contains("\"" + awkward + "\"", TranscriptCitation.Format(Turn(0, "Me", awkward), "T", Start, "v1"));
    }
}
