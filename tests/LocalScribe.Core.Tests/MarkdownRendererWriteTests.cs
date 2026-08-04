using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class MarkdownRendererWriteTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 6, 30, 14, 32, 0, TimeSpan.Zero);   // fixed offset -> deterministic

    /// <summary>The same sample data DocxRendererTests renders - parity is asserted
    /// against identical input (design 2026-07-18 section 3).</summary>
    private static (TranscriptHeader H, SessionTextView V, DisplayRow[] R) Sample()
    {
        var h = new TranscriptHeader("Weekly Sync", "Teams", Started, 2220000, "small.en", "CUDA");
        var v = new SessionTextView("Weekly Sync", new[] { "Acme (2026-014)" },
            new[] { "Sam", "Bob (Counsel)" }, Started, Started.AddMinutes(37), 2220000,
            "Teams", "", null);
        var r = new[]
        {
            new DisplayRow { StartMs = 1000, DisplayName = "Sam", Text = "Morning everyone." },
            new DisplayRow { IsMarker = true, StartMs = 30000, Text = "audio device changed" },
            new DisplayRow { StartMs = 38000, DisplayName = "Bob", Text = "Question on tokens." },
        };
        return (h, v, r);
    }

    /// <summary>Render the Sample() fixture at "relative" timestamps - the common case most tests
    /// below need. Tests that vary header/meta/rows call MarkdownRenderer.Write directly.</summary>
    private static string Write(ExportOptions opts, ExportProvenance? provenance = null,
        ExportSummary? summary = null)
    {
        var (h, v, r) = Sample();
        return MarkdownRenderer.Write(h, v, provenance ?? new ExportProvenance(), summary, r,
            "relative", opts);
    }

    private static ExportSummary Summary(string? stale = null) => new()
    {
        ContentMarkdown = "## Summary\nThey agreed to file.\n\n## Key topics\n- costs\n",
        ProvenanceLine = "generated 2026-08-01 14:22, Qwen3-4B-Instruct-2507.gguf (CUDA)",
        StaleNotice = stale,
    };

    // Small independent fixture for the excerpt tests below (Task 12), mirroring
    // PlainTextRendererWriteTests/DocxRendererTests Task 2/9 shape.
    private static TranscriptHeader Header() =>
        new("Doe intake", "Webex", Started, 1_800_000, "large-v3-turbo", "cuda");

    private static SessionTextView Meta() =>
        new("Doe intake", ["Doe v Roe (2026/014)"], ["Sam (Counsel)"], Started,
            Started.AddMinutes(30), 1_800_000, "Webex", "", Summary: null);

    private static DisplayRow Turn(long startMs, long endMs, string name, string text) =>
        new() { StartMs = startMs, EndMs = endMs, DisplayName = name, Text = text };

    private static RowSegment Seg(int seq, long start, long end, string text) =>
        new(seq, TranscriptSource.Local, start, end, text, text, false, false);

    /// <summary>A single Sam turn whose third segment starts 15.2s after the first - one cadence
    /// break at 16200ms with the default 15s interval.</summary>
    private static DisplayRow[] LongTurn() => new[]
    {
        new DisplayRow
        {
            StartMs = 1000, EndMs = 21000, DisplayName = "Sam",
            Text = "First part. Second part. Third part.",
            Segments = new[]
            {
                Seg(0, 1000, 5000, "First part."),
                Seg(1, 5400, 10000, "Second part."),
                Seg(2, 16200, 21000, "Third part."),
            },
        },
    };

    [Fact]
    public void Writes_metadata_disclaimer_and_turns()
    {
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, r, "relative", new ExportOptions());

        string expected =
            "# Weekly Sync\n" +
            "\n" +
            "- **App:** Teams\n" +
            "- **Date:** 2026-06-30 14:32 - 15:09 (37 min)\n" +
            "- **Matter(s):** Acme (2026-014)\n" +
            "- **Participants:** Sam, Bob (Counsel)\n" +
            "- **Medium:** Teams\n" +
            "- **Transcript version:** v1\n" +
            "- **Speakers heard:** Sam, Bob\n" +
            "\n" +
            "_" + ExportNotices.Disclaimer + "_\n" +
            "\n" +
            "**[00:01] Sam:** Morning everyone.\n" +
            "\n" +
            "_[audio device changed]_\n" +
            "\n" +
            "**[00:38] Bob:** Question on tokens.\n";
        Assert.Equal(expected, md);
    }

    [Fact]
    public void Metadata_block_carries_version_and_speakers_heard()
    {
        // Mirrors DocxRendererTests.Metadata_block_carries_duration_version_and_speakers_heard:
        // version/model provenance moved off the footer and up here, stated once (design
        // 2026-08-03 sections 2, 6).
        string md = Write(new ExportOptions(), new ExportProvenance
        {
            VersionId = "v2-large-v3-turbo-2026-08-01",
            Model = "large-v3-turbo",
            Backend = "cuda",
        });

        Assert.Contains("- **Transcript version:** v2 \u00B7 large-v3-turbo \u00B7 cuda\n", md);
        Assert.Contains("- **Speakers heard:** Sam, Bob\n", md);
    }

    [Fact]
    public void Markdown_has_no_footer_block()
    {
        // design 2026-08-03 section 9: with the footer reduced to the transcript name, and the
        // name already the H1 at the top, a trailing rule + name block is pure repetition.
        string md = Write(new ExportOptions());   // existing helper in this file
        Assert.DoesNotContain("\n---\n", md);
    }

    [Fact]
    public void Toggles_off_omit_timestamps_and_markers()
    {
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, r, "relative",
            new ExportOptions { IncludeTimestamps = false, IncludeMarkers = false });

        Assert.DoesNotContain("[00:01]", md);
        Assert.DoesNotContain("audio device changed", md);
        Assert.Contains("**Sam:** Morning everyone.\n", md);      // turn label present, no stamp
        Assert.Contains("**Bob:** Question on tokens.\n", md);
        Assert.DoesNotContain("\n\n\n", md);                      // dropped marker leaves no gap
    }

    [Fact]
    public void Empty_matters_and_participants_render_as_none()
    {
        var h = new TranscriptHeader("T", "Webex", Started, 60000, "base.en", "CPU");
        var v = new SessionTextView("T", Array.Empty<string>(), Array.Empty<string>(),
            Started, null, 60000, "Webex", "Initial interview.", null);
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, Array.Empty<DisplayRow>(),
            "relative", new ExportOptions());

        Assert.Contains("- **Matter(s):** (none)\n", md);
        Assert.Contains("- **Participants:** (none)\n", md);
        Assert.Contains("- **Description:** Initial interview.\n", md);   // present only when set
        // The old footerText param made "no footer" specific to THIS case (empty footer text);
        // now the footer block is gone unconditionally, so that guarantee belongs to
        // Markdown_has_no_footer_block instead - re-asserting it here would just be redundant.
    }

    [Fact]
    public void Row_text_is_verbatim_never_escaped_or_filtered()
    {
        // Evidentiary rule (design 2026-07-18 section 1): the renderer emits verbatim projected
        // text - even characters that happen to be markdown syntax are never escaped or dropped.
        var (h, v, _) = Sample();
        var rows = new[] { new DisplayRow { StartMs = 1000, DisplayName = "Sam",
            Text = "Use **bold** and _underscores_ verbatim." } };
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, rows, "relative", new ExportOptions());
        Assert.Contains("**[00:01] Sam:** Use **bold** and _underscores_ verbatim.\n", md);
    }

    [Fact]
    public void Cadence_splits_a_long_turn_into_named_contd_continuation_paragraphs()
    {
        // Task 9 (design 2026-08-03 section 8): the continuation label repeats the speaker name
        // with a " (cont'd)" suffix - parity with DocxRenderer's turn label - not a bare stamp.
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, LongTurn(), "relative",
            new ExportOptions { TimestampIntervalMs = 15000 });

        string expected =
            "# Weekly Sync\n" +
            "\n" +
            "- **App:** Teams\n" +
            "- **Date:** 2026-06-30 14:32 - 15:09 (37 min)\n" +
            "- **Matter(s):** Acme (2026-014)\n" +
            "- **Participants:** Sam, Bob (Counsel)\n" +
            "- **Medium:** Teams\n" +
            "- **Transcript version:** v1\n" +
            "- **Speakers heard:** Sam\n" +
            "\n" +
            "_" + ExportNotices.Disclaimer + "_\n" +
            "\n" +
            "**[00:01] Sam:** First part. Second part.\n" +
            "\n" +
            "**[00:16] Sam (cont'd):** Third part.\n";
        Assert.Equal(expected, md);
    }

    [Fact]
    public void Length_triggered_split_renders_a_contd_label_matching_docx_break_points()
    {
        // Every cadence test above drives TimestampIntervalMs; the always-on maxChars trigger -
        // the actual subject of Task 9 - was otherwise never exercised through this renderer.
        // ContinuationMaxChars is a single constant on DocxRenderer, referenced (not redefined)
        // here (design 2026-08-03 section 8) precisely so the two formats cannot silently
        // disagree about where a turn breaks; this test proves that by rendering the SAME rows
        // through both renderers and comparing the continuation stamps each one emits.
        var h = new TranscriptHeader("Long Turn", "Teams", Started, 600000, "small.en", "CUDA");
        var v = new SessionTextView("Long Turn", Array.Empty<string>(), new[] { "Sam" },
            Started, Started.AddMinutes(10), 600000, "Teams", "", null);
        var segments = Enumerable.Range(0, 40)
            .Select(i => Seg(i, i * 1000L, i * 1000L + 900, new string('w', 100)))
            .ToList();
        var rows = new[] { new DisplayRow
        {
            StartMs = 0, DisplayName = "Sam",
            Text = string.Join(" ", segments.Select(s => s.ProjectedText)),
            Segments = segments,
        } };

        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, rows, "relative", new ExportOptions());
        var mdStamps = Regex.Matches(md, @"\*\*\[(\d\d:\d\d)\] Sam \(cont'd\):\*\*")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(mdStamps);   // the length trigger actually fired at least once

        using var docxStream = new MemoryStream();
        DocxRenderer.Write(docxStream, h, v, new ExportProvenance(), null, rows, "relative",
            DocxPageSize.A4, new ExportOptions());
        using var doc = WordprocessingDocument.Open(new MemoryStream(docxStream.ToArray()), false);
        var docxStamps = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Where(p => p.InnerText.Contains("(cont'd)", StringComparison.Ordinal))
            .Select(p => p.InnerText[1..p.InnerText.IndexOf(']')])
            .ToList();

        Assert.Equal(docxStamps, mdStamps);
    }

    [Fact]
    public void Cadence_is_ignored_when_timestamps_are_off()
    {
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, LongTurn(), "relative",
            new ExportOptions { IncludeTimestamps = false, TimestampIntervalMs = 15000 });
        Assert.Contains("**Sam:** First part. Second part. Third part.\n", md);
        Assert.DoesNotContain("[00:16]", md);
    }

    [Fact]
    public void Default_interval_zero_keeps_the_turn_as_one_paragraph()
    {
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, LongTurn(), "relative", new ExportOptions());
        Assert.Contains("**[00:01] Sam:** First part. Second part. Third part.\n", md);
        Assert.DoesNotContain("**[00:16]**", md);
    }

    [Fact]
    public void In_progress_export_is_labelled_in_the_metadata_block()
    {
        // Mirrors DocxRendererTests.In_progress_export_is_labelled_in_the_block_and_on_every_page:
        // markdown has no pages, so the single metadata-block placement is the whole story
        // (design 2026-08-03 section 11).
        string md = Write(new ExportOptions(), new ExportProvenance { InProgress = true });
        Assert.Contains(ExportNotices.InProgressNotice, md);
    }

    [Fact]
    public void Finalised_export_carries_no_in_progress_notice()
    {
        string md = Write(new ExportOptions());
        Assert.DoesNotContain(ExportNotices.InProgressNotice, md);
    }

    [Fact]
    public void Summary_renders_under_the_heading_with_the_locked_draft_label()
    {
        string md = Write(new ExportOptions(), summary: Summary());

        Assert.Contains("## " + ExportNotices.SummaryHeading, md);
        Assert.Contains(LocalScribe.Core.Assistant.AssistantPrompts.DraftLabel, md);
        Assert.Contains("generated 2026-08-01 14:22", md);
        Assert.Contains("They agreed to file.", md);
    }

    [Fact]
    public void A_null_summary_renders_no_summary_section()
    {
        string md = Write(new ExportOptions());   // summary defaults to null

        Assert.DoesNotContain(ExportNotices.SummaryHeading, md);
        Assert.DoesNotContain(LocalScribe.Core.Assistant.AssistantPrompts.DraftLabel, md);
    }

    [Fact]
    public void The_stale_notice_renders_when_present()
    {
        string md = Write(new ExportOptions(),
            summary: Summary("OUT OF DATE: the transcript changed after this summary was generated."));

        Assert.Contains("OUT OF DATE", md);
    }

    [Fact]
    public void Summary_lines_are_paragraph_separated_not_soft_broken_together()
    {
        // CommonMark soft-break trap (task-9 review finding 1): consecutive non-blank lines with
        // only a single '\n' between them are ONE paragraph in every markdown viewer, so the draft
        // label / provenance line / stale notice would run together and bury the stale-notice
        // warning mid-sentence after the model name. Each line needs a BLANK line ahead of it so
        // it renders as its own paragraph - a single '\n' is not enough, so this asserts "\n\n",
        // not just Contains(text).
        string md = Write(new ExportOptions(),
            summary: Summary("OUT OF DATE: the transcript changed after this summary was generated."));

        Assert.Contains("\n\n_" + LocalScribe.Core.Assistant.AssistantPrompts.DraftLabel + "_\n", md);
        Assert.Contains("\n\n_generated 2026-08-01 14:22", md);
        Assert.Contains("\n\n**OUT OF DATE", md);
    }

    [Fact]
    public void An_excerpt_renders_the_span_and_the_notice()
    {
        string md = MarkdownRenderer.Write(Header(), Meta(),
            new ExportProvenance { ExcerptSpan = "00:12:30-00:18:45 of 01:47:12" }, null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.Contains("- **Excerpt:** 00:12:30-00:18:45 of 01:47:12\n", md);
        Assert.Contains("**" + ExportNotices.ExcerptNotice + "**", md);
    }

    [Fact]
    public void A_complete_transcript_renders_no_excerpt_lines()
    {
        string md = MarkdownRenderer.Write(Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", new ExportOptions());

        Assert.DoesNotContain("Excerpt", md);
    }
}
