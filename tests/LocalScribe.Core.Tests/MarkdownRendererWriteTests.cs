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
    private static string Write(DocxOptions opts, ExportProvenance? provenance = null)
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
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), r, "relative", new DocxOptions());

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
            "_" + DocxRenderer.Disclaimer + "_\n" +
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
        string md = Write(new DocxOptions(), new ExportProvenance
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
        string md = Write(new DocxOptions());   // existing helper in this file
        Assert.DoesNotContain("\n---\n", md);
    }

    [Fact]
    public void Toggles_off_omit_timestamps_and_markers()
    {
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), r, "relative",
            new DocxOptions { IncludeTimestamps = false, IncludeMarkers = false });

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
            "relative", new DocxOptions());

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
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), rows, "relative", new DocxOptions());
        Assert.Contains("**[00:01] Sam:** Use **bold** and _underscores_ verbatim.\n", md);
    }

    [Fact]
    public void Cadence_splits_a_long_turn_into_named_contd_continuation_paragraphs()
    {
        // Task 9 (design 2026-08-03 section 8): the continuation label repeats the speaker name
        // with a " (cont'd)" suffix - parity with DocxRenderer's turn label - not a bare stamp.
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), LongTurn(), "relative",
            new DocxOptions { TimestampIntervalMs = 15000 });

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
            "_" + DocxRenderer.Disclaimer + "_\n" +
            "\n" +
            "**[00:01] Sam:** First part. Second part.\n" +
            "\n" +
            "**[00:16] Sam (cont'd):** Third part.\n";
        Assert.Equal(expected, md);
    }

    [Fact]
    public void Cadence_is_ignored_when_timestamps_are_off()
    {
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), LongTurn(), "relative",
            new DocxOptions { IncludeTimestamps = false, TimestampIntervalMs = 15000 });
        Assert.Contains("**Sam:** First part. Second part. Third part.\n", md);
        Assert.DoesNotContain("[00:16]", md);
    }

    [Fact]
    public void Default_interval_zero_keeps_the_turn_as_one_paragraph()
    {
        var (h, v, _) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), LongTurn(), "relative", new DocxOptions());
        Assert.Contains("**[00:01] Sam:** First part. Second part. Third part.\n", md);
        Assert.DoesNotContain("**[00:16]**", md);
    }
}
