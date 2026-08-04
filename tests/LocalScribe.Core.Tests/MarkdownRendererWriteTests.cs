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
    private static string Write(ExportOptions opts, ExportProvenance? provenance = null)
    {
        var (h, v, r) = Sample();
        return MarkdownRenderer.Write(h, v, provenance ?? new ExportProvenance(), r, "relative", opts);
    }

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
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), r, "relative", new ExportOptions());

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
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), r, "relative",
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
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), Array.Empty<DisplayRow>(),
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
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), rows, "relative", new ExportOptions());
        Assert.Contains("**[00:01] Sam:** Use **bold** and _underscores_ verbatim.\n", md);
    }

    [Fact]
    public void Cadence_splits_a_long_turn_into_named_contd_continuation_paragraphs()
    {
        // Task 9 (design 2026-08-03 section 8): the continuation label repeats the speaker name
        // with a " (cont'd)" suffix - parity with DocxRenderer's turn label - not a bare stamp.
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), LongTurn(), "relative",
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

        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), rows, "relative", new ExportOptions());
        var mdStamps = Regex.Matches(md, @"\*\*\[(\d\d:\d\d)\] Sam \(cont'd\):\*\*")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(mdStamps);   // the length trigger actually fired at least once

        using var docxStream = new MemoryStream();
        DocxRenderer.Write(docxStream, h, v, new ExportProvenance(), rows, "relative",
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
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), LongTurn(), "relative",
            new ExportOptions { IncludeTimestamps = false, TimestampIntervalMs = 15000 });
        Assert.Contains("**Sam:** First part. Second part. Third part.\n", md);
        Assert.DoesNotContain("[00:16]", md);
    }

    [Fact]
    public void Default_interval_zero_keeps_the_turn_as_one_paragraph()
    {
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), LongTurn(), "relative", new ExportOptions());
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
}
