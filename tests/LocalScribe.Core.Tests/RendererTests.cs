using LocalScribe.Core.Projection;

public class RendererTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 6, 30, 14, 32, 0, TimeSpan.Zero);   // fixed offset -> deterministic

    [Theory]
    [InlineData(1000, "00:01")]
    [InlineData(85320, "01:25")]
    [InlineData(3903000, "1:05:03")]   // >= 1h -> h:mm:ss
    public void Relative_timestamps_format(long ms, string expected)
        => Assert.Equal(expected, TimestampFormat.Stamp(ms, "relative", Started));

    [Fact]
    public void Wallclock_timestamp_adds_offset_to_start()
        => Assert.Equal("14:33:25", TimestampFormat.Stamp(85320, "wallclock", Started));

    [Fact]
    public void Markdown_renders_header_turns_and_markers()
    {
        var header = new TranscriptHeader("Weekly Sync", "Teams", Started, 2220000, "small.en", "CUDA");
        var rows = new[]
        {
            new DisplayRow { StartMs = 1000, DisplayName = "Sam", Text = "Morning everyone." },
            new DisplayRow { IsMarker = true, StartMs = 30000, Text = "audio device changed" },
            new DisplayRow { StartMs = 38000, DisplayName = "Bob", Text = "Question on tokens." },
        };
        string md = MarkdownRenderer.Render(header, rows, "relative");

        string expected =
            "# Weekly Sync\n" +
            "Teams \u00B7 2026-06-30 14:32 \u00B7 37 min \u00B7 small.en/CUDA\n" +
            "\n" +
            "**[00:01] Sam:** Morning everyone.\n" +
            "_[audio device changed]_\n" +
            "**[00:38] Bob:** Question on tokens.\n";
        Assert.Equal(expected, md);
    }

    [Fact]
    public void PlainText_has_no_markdown_decoration()
    {
        var header = new TranscriptHeader("Weekly Sync", "Teams", Started, 2220000, "small.en", "CUDA");
        var rows = new[]
        {
            new DisplayRow { StartMs = 1000, DisplayName = "Sam", Text = "Morning." },
            new DisplayRow { IsMarker = true, StartMs = 5000, Text = "paused by user" },
        };
        string txt = PlainTextRenderer.Render(header, rows, "relative");
        Assert.Contains("[00:01] Sam: Morning.", txt);
        Assert.Contains("[paused by user]", txt);
        Assert.DoesNotContain("**", txt);
        Assert.DoesNotContain("_[", txt);
    }

    [Fact]
    public void SessionText_renders_neutral_metadata_block()
    {
        var view = new SessionTextView(
            Title: "Doe intake \u2014 Webex",
            Matters: new[] { "Doe v. State (CR-2026-014)" },
            Participants: new[] { "Sam (Attorney, Local)", "Alice Client (Client, Remote)" },
            StartedAtLocal: Started,
            EndedAtLocal: Started.AddMinutes(37),
            DurationMs: 2220000,
            Medium: "Webex",
            Description: "Initial client interview.",
            Summary: null);
        string txt = SessionTextRenderer.Render(view);

        Assert.Contains("Doe intake \u2014 Webex", txt);
        Assert.Contains("Matter(s): Doe v. State (CR-2026-014)", txt);
        Assert.Contains("Participants: Sam (Attorney, Local), Alice Client (Client, Remote)", txt);
        Assert.Contains("Medium: Webex", txt);
        Assert.Contains("Summary: (none)", txt);
    }
}
