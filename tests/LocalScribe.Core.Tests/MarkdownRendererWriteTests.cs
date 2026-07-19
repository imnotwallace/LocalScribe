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
            new[] { "Sam (Local)", "Bob (Remote)" }, Started, Started.AddMinutes(37), 2220000,
            "Teams", "", null);
        var r = new[]
        {
            new DisplayRow { StartMs = 1000, DisplayName = "Sam", Text = "Morning everyone." },
            new DisplayRow { IsMarker = true, StartMs = 30000, Text = "audio device changed" },
            new DisplayRow { StartMs = 38000, DisplayName = "Bob", Text = "Question on tokens." },
        };
        return (h, v, r);
    }

    [Fact]
    public void Writes_metadata_disclaimer_turns_and_footer()
    {
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, r, "relative", "PRIVILEGED & CONFIDENTIAL",
            new DocxOptions());

        string expected =
            "# Weekly Sync\n" +
            "\n" +
            "- **App:** Teams\n" +
            "- **Date:** 2026-06-30 14:32\n" +
            "- **Matter(s):** Acme (2026-014)\n" +
            "- **Participants:** Sam (Local), Bob (Remote)\n" +
            "- **Medium:** Teams\n" +
            "\n" +
            "_" + DocxRenderer.Disclaimer + "_\n" +
            "\n" +
            "**[00:01] Sam:** Morning everyone.\n" +
            "\n" +
            "_[audio device changed]_\n" +
            "\n" +
            "**[00:38] Bob:** Question on tokens.\n" +
            "\n" +
            "---\n" +
            "\n" +
            "PRIVILEGED & CONFIDENTIAL\n";
        Assert.Equal(expected, md);
    }

    [Fact]
    public void Toggles_off_omit_timestamps_and_markers()
    {
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, r, "relative", "F",
            new DocxOptions { IncludeTimestamps = false, IncludeMarkers = false });

        Assert.DoesNotContain("[00:01]", md);
        Assert.DoesNotContain("audio device changed", md);
        Assert.Contains("**Sam:** Morning everyone.\n", md);      // turn label present, no stamp
        Assert.Contains("**Bob:** Question on tokens.\n", md);
        Assert.DoesNotContain("\n\n\n", md);                      // dropped marker leaves no gap
    }

    [Fact]
    public void Empty_matters_participants_render_none_and_empty_footer_omits_the_rule()
    {
        var h = new TranscriptHeader("T", "Webex", Started, 60000, "base.en", "CPU");
        var v = new SessionTextView("T", Array.Empty<string>(), Array.Empty<string>(),
            Started, null, 60000, "Webex", "Initial interview.", null);
        string md = MarkdownRenderer.Write(h, v, Array.Empty<DisplayRow>(), "relative", "",
            new DocxOptions());

        Assert.Contains("- **Matter(s):** (none)\n", md);
        Assert.Contains("- **Participants:** (none)\n", md);
        Assert.Contains("- **Description:** Initial interview.\n", md);   // present only when set
        Assert.DoesNotContain("---", md);                         // empty footer -> no rule block
        Assert.EndsWith("_\n", md);                               // document ends at the disclaimer
    }

    [Fact]
    public void Row_text_is_verbatim_never_escaped_or_filtered()
    {
        // Evidentiary rule (design 2026-07-18 section 1): the renderer emits verbatim projected
        // text - even characters that happen to be markdown syntax are never escaped or dropped.
        var (h, v, _) = Sample();
        var rows = new[] { new DisplayRow { StartMs = 1000, DisplayName = "Sam",
            Text = "Use **bold** and _underscores_ verbatim." } };
        string md = MarkdownRenderer.Write(h, v, rows, "relative", "", new DocxOptions());
        Assert.Contains("**[00:01] Sam:** Use **bold** and _underscores_ verbatim.\n", md);
    }
}
