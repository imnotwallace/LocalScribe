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
            "- **Date:** 2026-06-30 14:32 - 15:09 (37 min)\n" +
            "- **Matter(s):** Acme (2026-014)\n" +
            "- **Speakers heard:** Sam, Bob\n" +
            "- **Medium:** Teams\n" +
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
    public void Metadata_block_carries_speakers_heard_and_no_transcript_version()
    {
        // Part 3 (design 2026-08-11 section 3.3): the model name was welded into "Transcript
        // version" as "v2 <dot> large-v3-turbo <dot> cuda", so removing the model means removing
        // the whole line - there is nothing to edit around. The engine still rides in
        // session.json inside the .zip bundle; the reading copy stops naming it.
        string md = Write(new ExportOptions(), new ExportProvenance
        {
            VersionId = "v2-large-v3-turbo-2026-08-01",
            Model = "large-v3-turbo",
            Backend = "cuda",
        });

        Assert.Contains("- **Speakers heard:** Sam, Bob\n", md);
        Assert.DoesNotContain("Transcript version", md);
        Assert.DoesNotContain("large-v3-turbo", md);
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
    public void Empty_matters_render_as_none_and_participants_are_gone()
    {
        var h = new TranscriptHeader("T", "Webex", Started, 60000, "base.en", "CPU");
        var v = new SessionTextView("T", Array.Empty<string>(), new[] { "Declared Person" },
            Started, null, 60000, "Webex", "Initial interview.", null);
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, Array.Empty<DisplayRow>(),
            "relative", new ExportOptions());

        Assert.Contains("- **Matter(s):** (none)\n", md);
        Assert.Contains("- **Description:** Initial interview.\n", md);   // present only when set
        // Speakers heard replaces Participants (design 2026-08-11 section 3.3). The roster is
        // populated here and there are NO rows, so the accepted trade is visible in one test: a
        // declared attendee who never speaks disappears from the exported document.
        Assert.DoesNotContain("Participants", md);
        Assert.DoesNotContain("Declared Person", md);
        Assert.DoesNotContain("Speakers heard", md);   // no rows -> no line, not an empty one
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
            "- **Date:** 2026-06-30 14:32 - 15:09 (37 min)\n" +
            "- **Matter(s):** Acme (2026-014)\n" +
            "- **Speakers heard:** Sam\n" +
            "- **Medium:** Teams\n" +
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
    public void Summary_gets_a_scope_notice_when_the_transcript_is_excerpted()
    {
        // Fix 2 (whole-branch review): IncludeSummary and ExcerptRange are orthogonal options -
        // a user can tick both, and without this notice a reader cannot tell whether the summary
        // describes the excerpt or the whole session. Same blank-line-separated-paragraph
        // requirement as the stale notice above (CommonMark soft-break trap).
        string md = Write(new ExportOptions(),
            provenance: new ExportProvenance { ExcerptSpan = "00:12:30-00:18:45 of 01:47:12" },
            summary: Summary());

        Assert.Contains("\n\n**" + ExportNotices.SummaryCoversMoreThanExcerpt + "**\n", md);
    }

    [Fact]
    public void Summary_carries_no_scope_notice_when_not_excerpted()
    {
        string md = Write(new ExportOptions(), summary: Summary());

        Assert.DoesNotContain(ExportNotices.SummaryCoversMoreThanExcerpt, md);
    }

    [Fact]
    public void Summary_scope_notice_stacks_with_a_stale_notice()
    {
        // Independent of StaleNotice: a summary can be BOTH stale AND out of scope at once.
        string md = Write(new ExportOptions(),
            provenance: new ExportProvenance { ExcerptSpan = "00:12:30-00:18:45 of 01:47:12" },
            summary: Summary("OUT OF DATE: the transcript changed after this summary was generated."));

        Assert.Contains("**OUT OF DATE", md);
        Assert.Contains("**" + ExportNotices.SummaryCoversMoreThanExcerpt + "**", md);
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

    [Fact]
    public void Every_removed_provenance_line_is_absent_even_when_the_field_is_populated()
    {
        // The exact ABSENT set (design 2026-08-11 section 3.3). Populating every field and
        // asserting none of them reaches the document is the only way to prove the renderer
        // dropped the LINE rather than merely being handed nulls by this fixture.
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, new ExportProvenance
        {
            SessionId = "2026-07-03-webex-doe-intake",
            ExportedAtUtc = new DateTimeOffset(2026, 8, 5, 14, 7, 0, TimeSpan.Zero),
            AppVersion = "0.9.0",
            WeightsFile = "ggml-small.en-q8_0.bin",
            VersionId = "v2-large-v3-turbo-2026-08-01",
            Model = "large-v3-turbo",
            Backend = "cuda",
            ModelAccuracy = "Decent accuracy, English only - quick",
            AudioSha256 = "sourcehash",
            TranscriptSha256 = "deadbeef",
            RecordedAudio =
            [
                new RecordedAudioLeg
                { FileName = "local.flac", Sha256 = "aaa", Silence = new FabricatedSilenceSummary(2, 3000) },
            ],
            HumanLayer = new HumanLayerCounts { Corrections = 2, SpeakerNames = 1 },
        }, null, r, "relative", new ExportOptions());

        foreach (string absent in new[]
        {
            "App:", "Participants:", "Session ID:", "Exported:", "Transcript version",
            "Weights file", "Model accuracy", "Audio SHA-256", "Transcript SHA-256",
            "Human edits", "ggml-small.en-q8_0.bin", "deadbeef", "sourcehash",
        })
            Assert.DoesNotContain(absent, md);

        // The human layer is disclosed - as prose, not as counts.
        Assert.Contains(ExportNotices.DisclaimerEdited, md);
    }

    [Fact]
    public void Source_renders_for_an_import_and_a_recorded_session_has_no_source_line()
    {
        // Owner decision (2026-08-11): Source is IMPORTS ONLY. A recorded call's legs are the
        // hardcoded constants local.flac / remote.flac (StoragePaths.AudioFile), so printing them
        // tells a reader nothing - a recorded session has no Source line, the way a session with
        // no matter has no Matter line.
        var (h, v, r) = Sample();
        string imported = MarkdownRenderer.Write(h, v,
            new ExportProvenance { AudioFileName = "interview.mp4", AudioSha256 = "abc123" },
            null, r, "relative", new ExportOptions());
        Assert.Contains("- **Source:** interview.mp4\n", imported);
        Assert.DoesNotContain("abc123", imported);        // the hash rides in the .zip, not here
        Assert.DoesNotContain("Audio:", imported);        // the old label is gone, not renamed twice

        string recorded = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, r,
            "relative", new ExportOptions());
        Assert.DoesNotContain("Source:", recorded);
    }

    [Fact]
    public void Omitted_renders_only_when_duplicates_were_suppressed()
    {
        var (h, v, r) = Sample();

        string suppressed = MarkdownRenderer.Write(h, v,
            new ExportProvenance { HumanLayer = new HumanLayerCounts { SuppressedDuplicates = 4 } },
            null, r, "relative", new ExportOptions());
        Assert.Contains("- **Omitted:** 4 duplicate turns (echo)\n", suppressed);
        // A machine omission is not a human act: the disclaimer must NOT flip.
        Assert.Contains(ExportNotices.Disclaimer, suppressed);
        Assert.DoesNotContain(ExportNotices.DisclaimerEdited, suppressed);

        string clean = MarkdownRenderer.Write(h, v,
            new ExportProvenance { HumanLayer = new HumanLayerCounts() }, null, r, "relative",
            new ExportOptions());
        Assert.DoesNotContain("Omitted", clean);
    }

    [Fact]
    public void The_full_header_renders_the_seven_lines_in_the_locked_order()
    {
        // The one place the MAXIMAL header shape is pinned. The goldens above cover the minimal
        // shape; this covers every optional line at once, in order (design 2026-08-11 section 3.3).
        var h = new TranscriptHeader("Interview", "Other", Started, 1_620_000, "large-v3-turbo", "cuda");
        var v = new SessionTextView("Interview", ["Doe v Roe (2026/014)"], ["ignored roster"],
            Started, Started.AddMinutes(27), 1_620_000, "Other",
            "Second interview, custody suite.", null);
        var rows = new[] { new DisplayRow { StartMs = 1000, DisplayName = "Speaker A", Text = "Yes." } };

        string md = MarkdownRenderer.Write(h, v, new ExportProvenance
        {
            AudioFileName = "interview.mp4",
            HumanLayer = new HumanLayerCounts { SuppressedDuplicates = 4 },
        }, null, rows, "relative", new ExportOptions());

        Assert.StartsWith(
            "# Interview\n" +
            "\n" +
            "- **Date:** 2026-06-30 14:32 - 14:59 (27 min)\n" +
            "- **Matter(s):** Doe v Roe (2026/014)\n" +
            "- **Speakers heard:** Speaker A\n" +
            "- **Medium:** Other\n" +
            "- **Source:** interview.mp4\n" +
            "- **Description:** Second interview, custody suite.\n" +
            "- **Omitted:** 4 duplicate turns (echo)\n",
            md);
    }

    [Fact]
    public void A_corrected_turn_carries_no_per_turn_mark()
    {
        // Part 3 (design 2026-08-11 section 3.3): the per-turn "[text corrected]" mark is DELETED,
        // not defaulted off. Human-edit disclosure moved into the disclaimer as prose - a document
        // stippled with per-turn marks reads as a document under suspicion, and the sentence at
        // the top says the same thing once, in language a reader can quote.
        var row = new DisplayRow
        {
            StartMs = 1000, EndMs = 5000, DisplayName = "Sam", Text = "Corrected text.",
            Segments = [new RowSegment(0, TranscriptSource.Local, 1000, 5000, "Corrected text.",
                "Original text.", IsCorrected: true, IsPinned: false)],
        };
        var (h, v, _) = Sample();

        string md = MarkdownRenderer.Write(h, v, new ExportProvenance(), null, [row], "relative",
            new ExportOptions());
        Assert.Contains("**[00:01] Sam:** Corrected text.\n", md);
        Assert.DoesNotContain("[text corrected]", md);
    }
}
