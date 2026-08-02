// tests/LocalScribe.Core.Tests/DocxRendererTests.cs
using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class DocxRendererTests
{
    private static readonly DateTimeOffset Started = new(2026, 6, 30, 14, 32, 0, TimeSpan.Zero);

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

    private static byte[] Render(string mode, string footer, DocxPageSize size, DocxOptions opts)
    {
        var (h, v, r) = Sample();
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v, r, mode, footer, size, opts);
        return ms.ToArray();   // valid even after the document disposed/closed the stream
    }

    private static WordprocessingDocument Open(byte[] bytes)
        => WordprocessingDocument.Open(new MemoryStream(bytes), false);

    private static Style TurnStyle(WordprocessingDocument doc)
        => doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!.Elements<Style>()
            .Single(s => s.StyleId == "TranscriptTurn");

    [Fact]
    public void Renders_metadata_disclaimer_marker_footer_and_a4_pagesize()
    {
        byte[] bytes = Render("relative", "PRIVILEGED & CONFIDENTIAL", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;
        string text = main.Document!.Body!.InnerText;

        Assert.Contains("Weekly Sync", text);
        Assert.Contains("Participants: Sam (Local), Bob (Remote)", text);
        Assert.Contains("Matter(s): Acme (2026-014)", text);
        Assert.Contains(DocxRenderer.Disclaimer, text);
        Assert.Contains("Morning everyone.", text);
        Assert.Contains("[audio device changed]", text);

        Assert.StartsWith("PRIVILEGED & CONFIDENTIAL", main.FooterParts.Single().Footer!.InnerText);
        var pageSize = main.Document.Body!.GetFirstChild<SectionProperties>()!.GetFirstChild<PageSize>()!;
        Assert.Equal(11906u, pageSize.Width!.Value);          // A4 width in twips
    }

    [Fact]
    public void Turn_paragraphs_reference_the_turn_style_with_bold_label_tab_text_runs()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);

        // The named style carries the geometry so recipients can retune it in Word.
        var style = TurnStyle(doc);
        var ind = style.StyleParagraphProperties!.GetFirstChild<Indentation>()!;
        Assert.Equal("2160", ind.Left!.Value);      // short sample labels clamp to the 1.5" floor
        Assert.Equal("2160", ind.Hanging!.Value);   // hanging == left: wrapped lines align at the column
        var tab = style.StyleParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().Single();
        Assert.Equal(TabStopValues.Left, tab.Val!.Value);
        Assert.Equal(2160, tab.Position!.Value);

        var turn = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText.StartsWith("[00:01] Sam:", StringComparison.Ordinal));
        Assert.Equal("TranscriptTurn", turn.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        var runs = turn.Elements<Run>().ToList();
        Assert.Equal(3, runs.Count);
        Assert.NotNull(runs[0].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal("[00:01] Sam:", runs[0].InnerText);      // no trailing space - the tab separates
        Assert.NotNull(runs[1].GetFirstChild<TabChar>());
        Assert.Equal("Morning everyone.", runs[2].InnerText);
    }

    [Fact]
    public void Toggles_off_render_bold_name_only_labels_and_drop_markers_letter_pagesize()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.Letter,
            new DocxOptions { IncludeTimestamps = false, IncludeMarkers = false });
        using var doc = Open(bytes);
        var body = doc.MainDocumentPart!.Document!.Body!;

        Assert.DoesNotContain("[00:01]", body.InnerText);
        Assert.DoesNotContain("audio device changed", body.InnerText);
        var turn = body.Elements<Paragraph>()
            .Single(p => p.InnerText.StartsWith("Sam:", StringComparison.Ordinal));
        var runs = turn.Elements<Run>().ToList();
        Assert.NotNull(runs[0].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal("Sam:", runs[0].InnerText);              // stamp-less label, same geometry
        Assert.NotNull(runs[1].GetFirstChild<TabChar>());
        Assert.Equal("2160", TurnStyle(doc).StyleParagraphProperties!
            .GetFirstChild<Indentation>()!.Left!.Value);      // name-only labels still get the floor
        Assert.Equal(12240u, body.GetFirstChild<SectionProperties>()!
            .GetFirstChild<PageSize>()!.Width!.Value);        // Letter width in twips
    }

    [Fact]
    public void Text_column_scales_with_the_longest_label_and_clamps_at_three_inches()
    {
        var (h, v, _) = Sample();

        // "[00:01] Barrister Wentworth:" = 28 chars -> 28*120 + 240 = 3600 twips (2.5", unclamped).
        var mid = new[] { new DisplayRow
        { StartMs = 1000, DisplayName = "Barrister Wentworth", Text = "Yes." } };
        using var ms1 = new MemoryStream();
        DocxRenderer.Write(ms1, h, v, mid, "relative", "", DocxPageSize.A4, new DocxOptions());
        using var doc1 = Open(ms1.ToArray());
        Assert.Equal("3600",
            TurnStyle(doc1).StyleParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);

        // 53-char label -> 6600 twips, clamped to the 3.0" ceiling (4320).
        var longRow = new[] { new DisplayRow { StartMs = 1000,
            DisplayName = "Ms. Alexandra Fitzgerald-Whitmore de la Vega", Text = "Present." } };
        using var ms2 = new MemoryStream();
        DocxRenderer.Write(ms2, h, v, longRow, "relative", "", DocxPageSize.A4, new DocxOptions());
        using var doc2 = Open(ms2.ToArray());
        Assert.Equal("4320",
            TurnStyle(doc2).StyleParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);
    }

    [Fact]
    public void Markers_render_italic_in_the_text_column()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var marker = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText == "[audio device changed]");
        Assert.Equal("2160", marker.ParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);
        Assert.NotNull(marker.Elements<Run>().Single().RunProperties?.GetFirstChild<Italic>());
    }

    [Fact]
    public void Page_margins_are_explicit_one_inch_with_half_inch_header_footer()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var margin = doc.MainDocumentPart!.Document!.Body!
            .GetFirstChild<SectionProperties>()!.GetFirstChild<PageMargin>()!;
        Assert.Equal(1440, margin.Top!.Value);
        Assert.Equal(1440u, margin.Right!.Value);
        Assert.Equal(1440, margin.Bottom!.Value);
        Assert.Equal(1440u, margin.Left!.Value);
        Assert.Equal(720u, margin.Header!.Value);
        Assert.Equal(720u, margin.Footer!.Value);
    }

    [Fact]
    public void Doc_defaults_pin_the_body_size_and_keep_the_default_face()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var rPr = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!
            .DocDefaults!.RunPropertiesDefault!.RunPropertiesBaseStyle!;
        Assert.Equal("22", rPr.FontSize!.Val!.Value);          // 11pt in half-points
        Assert.Null(rPr.RunFonts);                             // default theme face deliberately kept
    }

    [Fact]
    public void PageSizeForRegion_maps_US_CA_to_letter_else_A4()
    {
        Assert.Equal(DocxPageSize.Letter, DocxRenderer.PageSizeForRegion(new RegionInfo("US")));
        Assert.Equal(DocxPageSize.Letter, DocxRenderer.PageSizeForRegion(new RegionInfo("CA")));
        Assert.Equal(DocxPageSize.A4, DocxRenderer.PageSizeForRegion(new RegionInfo("GB")));
        Assert.Equal(DocxPageSize.A4, DocxRenderer.PageSizeForRegion(new RegionInfo("SG")));
    }

    [Fact]
    public void Line_numbering_counts_by_five_per_page_and_skips_the_header_block_only()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var body = doc.MainDocumentPart!.Document!.Body!;

        var ln = body.GetFirstChild<SectionProperties>()!.GetFirstChild<LineNumberType>()!;
        Assert.Equal(5, (int)ln.CountBy!.Value);
        Assert.Equal(LineNumberRestartValues.NewPage, ln.Restart!.Value);

        // Every paragraph BEFORE the first turn (title..disclaimer + spacer) suppresses numbering;
        // turns and markers never do - they are numbered transcript content.
        var paragraphs = body.Elements<Paragraph>().ToList();
        int firstTurn = paragraphs.FindIndex(p =>
            p.InnerText.StartsWith("[00:01] Sam:", StringComparison.Ordinal));
        Assert.True(firstTurn > 0);
        Assert.All(paragraphs.Take(firstTurn),
            p => Assert.NotNull(p.ParagraphProperties?.GetFirstChild<SuppressLineNumbers>()));
        Assert.All(paragraphs.Skip(firstTurn),
            p => Assert.Null(p.ParagraphProperties?.GetFirstChild<SuppressLineNumbers>()));
    }

    [Fact]
    public void Disclaimer_paragraph_carries_the_thin_bottom_rule()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var disclaimer = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText == DocxRenderer.Disclaimer);
        var border = disclaimer.ParagraphProperties!.GetFirstChild<ParagraphBorders>()!
            .GetFirstChild<BottomBorder>()!;
        Assert.Equal(BorderValues.Single, border.Val!.Value);
        Assert.Equal(4u, border.Size!.Value);                  // eighths of a point -> 0.5pt rule
        Assert.NotNull(disclaimer.Elements<Run>().Single().RunProperties?.GetFirstChild<Italic>());
    }

    [Fact]
    public void Footer_pairs_the_text_with_a_page_field_at_a_right_tab_on_the_usable_width()
    {
        byte[] bytes = Render("relative", "PRIVILEGED & CONFIDENTIAL", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var footer = doc.MainDocumentPart!.FooterParts.Single().Footer!;
        var par = footer.Elements<Paragraph>().Single();

        var tab = par.ParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().Single();
        Assert.Equal(TabStopValues.Right, tab.Val!.Value);
        Assert.Equal(9026, tab.Position!.Value);               // A4 11906 - 2x1440 margins

        Assert.StartsWith("PRIVILEGED & CONFIDENTIAL", footer.InnerText);
        Assert.Equal(" PAGE ", par.Descendants<FieldCode>().Single().Text);
        var fieldChars = par.Descendants<FieldChar>().ToList();
        Assert.Equal(3, fieldChars.Count);                     // begin / separate / end
        Assert.Equal(FieldCharValues.Begin, fieldChars[0].FieldCharType!.Value);
        Assert.Equal(FieldCharValues.Separate, fieldChars[1].FieldCharType!.Value);
        Assert.Equal(FieldCharValues.End, fieldChars[2].FieldCharType!.Value);
    }

    [Fact]
    public void Footer_right_tab_uses_the_letter_usable_width_on_letter_pages()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.Letter, new DocxOptions());
        using var doc = Open(bytes);
        var tab = doc.MainDocumentPart!.FooterParts.Single().Footer!.Elements<Paragraph>().Single()
            .ParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().Single();
        Assert.Equal(9360, tab.Position!.Value);               // Letter 12240 - 2x1440 margins
    }

    [Fact]
    public void Cadence_continuations_render_stamp_only_paragraphs_in_the_turn_style()
    {
        var (h, v, _) = Sample();
        var rows = new[] { new DisplayRow
        {
            StartMs = 0, EndMs = 24000, DisplayName = "Sam",
            Text = "one two three four five",
            Segments = new[]
            {
                new RowSegment(0, TranscriptSource.Local, 0, 4000, "one", "one", false, false),
                new RowSegment(1, TranscriptSource.Local, 4400, 9000, "two", "two", false, false),
                new RowSegment(2, TranscriptSource.Local, 9400, 14000, "three", "three", false, false),
                new RowSegment(3, TranscriptSource.Local, 14400, 19000, "four", "four", false, false),
                new RowSegment(4, TranscriptSource.Local, 19400, 24000, "five", "five", false, false),
            },
        } };
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v, rows, "relative", "", DocxPageSize.A4,
            new DocxOptions { TimestampIntervalMs = 15000 });
        using var doc = Open(ms.ToArray());
        var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToList();

        Assert.Single(paragraphs, p => p.InnerText == "[00:00] Sam:one two three four");
        var cont = paragraphs.Single(p => p.InnerText == "[00:19]five");
        Assert.Equal("TranscriptTurn", cont.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.Null(cont.ParagraphProperties!.GetFirstChild<SuppressLineNumbers>());   // counts as content
        var runs = cont.Elements<Run>().ToList();
        Assert.Equal(3, runs.Count);
        Assert.NotNull(runs[0].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal("[00:19]", runs[0].InnerText);            // stamp only - the name is not repeated
        Assert.NotNull(runs[1].GetFirstChild<TabChar>());
        Assert.Equal("five", runs[2].InnerText);
    }
}
