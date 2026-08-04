using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>The .txt export dialect (design 2026-08-04 section 3): the MarkdownRenderer.Write
/// metadata/disclaimer/cadence contract with no decoration, CRLF line endings, and no hard
/// wrapping.</summary>
public sealed class PlainTextRendererWriteTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 3, 9, 0, 0, TimeSpan.Zero);

    private static TranscriptHeader Header() =>
        new("Doe intake", "Webex", Start, 1_800_000, "large-v3-turbo", "cuda");

    private static SessionTextView Meta() =>
        new("Doe intake", ["Doe v Roe (2026/014)"], ["Sam (Counsel)"], Start,
            Start.AddMinutes(30), 1_800_000, "Webex", "", Summary: null);

    private static DisplayRow Turn(long startMs, long endMs, string name, string text) =>
        new() { StartMs = startMs, EndMs = endMs, DisplayName = name, Text = text };

    // Matches MarkdownRendererWriteTests.Seg: RowSegment is a positional record, so an object
    // initializer with a bare "Text" property does not compile - ProjectedText and RawText are
    // set to the same value here since none of these tests exercise correction display.
    private static RowSegment Seg(int seq, long start, long end, string text) =>
        new(seq, TranscriptSource.Local, start, end, text, text, false, false);

    [Fact]
    public void Uses_crlf_and_renders_undecorated_metadata_lines()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains("\r\n", txt);
        Assert.DoesNotContain("\n\n\n", txt.Replace("\r", ""));
        Assert.Contains("Matter(s): Doe v Roe (2026/014)\r\n", txt);
        Assert.Contains("Participants: Sam (Counsel)\r\n", txt);
        Assert.DoesNotContain("**", txt);                       // no markdown decoration
        Assert.DoesNotContain("- **", txt);
    }

    [Fact]
    public void Renders_the_non_optional_disclaimer()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());
        Assert.Contains(ExportNotices.Disclaimer, txt);
    }

    [Fact]
    public void In_progress_export_renders_the_notice_and_a_finalised_one_does_not()
    {
        var rows = new[] { Turn(0, 4000, "Sam", "hello") };
        string live = PlainTextRenderer.Write(Header(), Meta(),
            new ExportProvenance { InProgress = true }, null, rows, "relative", new ExportOptions());
        string done = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            rows, "relative", new ExportOptions());

        Assert.Contains(ExportNotices.InProgressNotice, live);
        Assert.DoesNotContain(ExportNotices.InProgressNotice, done);
    }

    [Fact]
    public void Turn_renders_as_stamp_name_colon_text_on_one_unwrapped_line()
    {
        string longText = new string('a', 400) + " end";
        string txt = PlainTextRenderer.Write(Header(), Meta(),
            new ExportProvenance(), null, [Turn(0, 4000, "Sam", longText)], "relative", new ExportOptions());

        Assert.Contains("[00:00] Sam: " + longText + "\r\n", txt);   // never hard-wrapped
    }

    [Fact]
    public void Timestamps_off_drops_the_stamp_but_keeps_the_name()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative",
            new ExportOptions { IncludeTimestamps = false });

        Assert.Contains("Sam: hello\r\n", txt);
        Assert.DoesNotContain("[00:00]", txt);
    }

    [Fact]
    public void Markers_render_bracketed_and_drop_entirely_when_toggled_off()
    {
        var rows = new DisplayRow[]
        {
            new() { IsMarker = true, StartMs = 1000, EndMs = 1000, Text = "Recording paused" },
            Turn(2000, 4000, "Sam", "hello"),
        };
        string on = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            rows, "relative", new ExportOptions());
        string off = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            rows, "relative", new ExportOptions { IncludeMarkers = false });

        Assert.Contains("[Recording paused]\r\n", on);
        Assert.DoesNotContain("Recording paused", off);
    }

    [Fact]
    public void Cadence_chunks_break_at_the_same_boundaries_as_markdown()
    {
        // The three formats must not disagree about where a turn breaks (design 2026-08-03
        // section 8): ContinuationMaxChars is shared, not redefined per renderer.
        var row = new DisplayRow
        {
            StartMs = 0, EndMs = 24000, DisplayName = "Sam", Text = "one two three four five",
            Segments =
            [
                Seg(0, 0, 4000, "one"),
                Seg(1, 4400, 9000, "two"),
                Seg(2, 9400, 14000, "three"),
                Seg(3, 14400, 19000, "four"),
                Seg(4, 19400, 24000, "five"),
            ],
        };
        var options = new ExportOptions { TimestampIntervalMs = 15000 };

        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [row], "relative", options);
        string md = MarkdownRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [row], "relative", options);

        // Same split point, same continuation stamp, different decoration.
        Assert.Contains("[00:00] Sam: one two three four\r\n", txt);
        Assert.Contains("[00:19] Sam (cont'd): five\r\n", txt);
        Assert.Contains(":** one two three four\n", md);
        Assert.Contains("**[00:19] Sam (cont'd):** five\n", md);
    }

    [Fact]
    public void Save_time_render_is_untouched_and_still_uses_lf()
    {
        // transcript.txt byte-identity is load-bearing (SessionProjectionLoader doc comment).
        string saved = PlainTextRenderer.Render(Header(), [Turn(0, 4000, "Sam", "hello")], "relative");
        Assert.DoesNotContain("\r\n", saved);
        Assert.Contains("[00:00] Sam: hello\n", saved);
    }

    private static ExportSummary Summary(string? stale = null) => new()
    {
        ContentMarkdown = "## Summary\nThey agreed to file.\n\n## Key topics\n- costs\n",
        ProvenanceLine = "generated 2026-08-01 14:22, Qwen3-4B-Instruct-2507.gguf (CUDA)",
        StaleNotice = stale,
    };

    [Fact]
    public void Summary_renders_under_the_heading_with_the_locked_draft_label()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), Summary(),
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains(ExportNotices.SummaryHeading, txt);
        Assert.Contains(LocalScribe.Core.Assistant.AssistantPrompts.DraftLabel, txt);
        Assert.Contains("generated 2026-08-01 14:22", txt);
        Assert.Contains("They agreed to file.", txt);
    }

    [Fact]
    public void A_null_summary_renders_no_summary_section()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.DoesNotContain(ExportNotices.SummaryHeading, txt);
        Assert.DoesNotContain(LocalScribe.Core.Assistant.AssistantPrompts.DraftLabel, txt);
    }

    [Fact]
    public void The_stale_notice_renders_when_present()
    {
        string txt = PlainTextRenderer.Write(Header(), Meta(), new ExportProvenance(),
            Summary("OUT OF DATE: the transcript changed after this summary was generated."),
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains("OUT OF DATE", txt);
    }
}
