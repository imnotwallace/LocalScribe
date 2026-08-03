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

    private static byte[] Render(string mode, DocxPageSize size, DocxOptions opts,
        ExportProvenance? provenance = null)
    {
        var (h, v, r) = Sample();
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v, provenance ?? new ExportProvenance(), r, mode, size, opts);
        return ms.ToArray();   // valid even after the document disposed/closed the stream
    }

    private static WordprocessingDocument Open(byte[] bytes)
        => WordprocessingDocument.Open(new MemoryStream(bytes), false);

    private static Style TurnStyle(WordprocessingDocument doc)
        => doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!.Elements<Style>()
            .Single(s => s.StyleId == "TranscriptTurn");

    /// <summary>The default (non-first-page) running head, for HeaderLeft coverage - HeaderLeft
    /// is private, so its truncation/fallback/surrogate rules are asserted through the rendered
    /// header text rather than by making it public.</summary>
    private static Header DefaultHeader(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart!;
        var sect = main.Document!.Body!.GetFirstChild<SectionProperties>()!;
        string id = sect.Elements<HeaderReference>()
            .Single(h => h.Type!.Value == HeaderFooterValues.Default).Id!.Value!;
        return ((HeaderPart)main.GetPartById(id)).Header!;
    }

    [Fact]
    public void Renders_metadata_disclaimer_marker_footer_and_a4_pagesize()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;
        string text = main.Document!.Body!.InnerText;

        Assert.Contains("Weekly Sync", text);
        Assert.Contains("Participants: Sam, Bob (Counsel)", text);
        Assert.Contains("Matter(s): Acme (2026-014)", text);
        Assert.Contains(DocxRenderer.Disclaimer, text);
        Assert.Contains("Morning everyone.", text);
        Assert.Contains("[audio device changed]", text);

        // design 2026-08-03 section 2: the footer is the transcript name, not a settings string.
        Assert.StartsWith("Weekly Sync", main.FooterParts.Single().Footer!.InnerText);
        var pageSize = main.Document.Body!.GetFirstChild<SectionProperties>()!.GetFirstChild<PageSize>()!;
        Assert.Equal(11906u, pageSize.Width!.Value);          // A4 width in twips
    }

    [Fact]
    public void Turn_paragraphs_reference_the_turn_style_with_bold_label_tab_text_runs()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
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
        // Label is stamp / name / suffix runs (design 2026-08-03 sections 3-4) so only the name run
        // carries TranscriptSpeaker - a combined run would put the stamp and colon in Task 6's
        // STYLEREF running head.
        var runs = turn.Elements<Run>().ToList();
        Assert.Equal(5, runs.Count);
        Assert.NotNull(runs[0].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal("[00:01] ", runs[0].InnerText);
        Assert.Equal("TranscriptSpeaker", runs[1].RunProperties?.GetFirstChild<RunStyle>()?.Val?.Value);
        Assert.Equal("Sam", runs[1].InnerText);
        Assert.NotNull(runs[2].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal(":", runs[2].InnerText);
        Assert.NotNull(runs[3].GetFirstChild<TabChar>());
        Assert.Equal("Morning everyone.", runs[4].InnerText);
    }

    [Fact]
    public void Speaker_style_is_a_pure_character_style_carrying_caps()
    {
        // MUST be type=character, never a linked paragraph+character style: Word will not see a
        // linked style applied to only part of a paragraph, and the speaker name is exactly that -
        // a run inside the turn paragraph. STYLEREF in the page header (Task 6) depends on this.
        // Caps is a FORMAT, never an uppercased string: STYLEREF returns the underlying text, so
        // uppercasing the data would destroy the real name to achieve a display effect
        // (design 2026-08-03 sections 3, 4).
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var style = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!.Elements<Style>()
            .Single(s => s.StyleId == "TranscriptSpeaker");

        Assert.Equal(StyleValues.Character, style.Type!.Value);
        Assert.NotNull(style.StyleRunProperties!.GetFirstChild<Caps>());
    }

    [Fact]
    public void Only_the_speaker_name_carries_the_speaker_style_not_the_stamp_or_colon()
    {
        // STYLEREF returns the styled run's text verbatim, so a combined "[00:01] Sam:" run would
        // put the stamp and colon in the running head. Three runs: stamp, name, colon.
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var turn = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText.StartsWith("[00:01] Sam:", StringComparison.Ordinal));

        var styled = turn.Elements<Run>()
            .Where(r => r.RunProperties?.GetFirstChild<RunStyle>()?.Val?.Value == "TranscriptSpeaker")
            .ToList();
        Assert.Equal("Sam", Assert.Single(styled).InnerText);
    }

    [Fact]
    public void Toggles_off_render_bold_name_only_labels_and_drop_markers_letter_pagesize()
    {
        byte[] bytes = Render("relative", DocxPageSize.Letter,
            new DocxOptions { IncludeTimestamps = false, IncludeMarkers = false });
        using var doc = Open(bytes);
        var body = doc.MainDocumentPart!.Document!.Body!;

        Assert.DoesNotContain("[00:01]", body.InnerText);
        Assert.DoesNotContain("audio device changed", body.InnerText);
        var turn = body.Elements<Paragraph>()
            .Single(p => p.InnerText.StartsWith("Sam:", StringComparison.Ordinal));
        // No stamp run when timestamps are off; name and suffix runs remain (stamp-less label,
        // same geometry) - only the name carries TranscriptSpeaker (design 2026-08-03 section 3).
        var runs = turn.Elements<Run>().ToList();
        Assert.Equal("TranscriptSpeaker", runs[0].RunProperties?.GetFirstChild<RunStyle>()?.Val?.Value);
        Assert.Equal("Sam", runs[0].InnerText);
        Assert.NotNull(runs[1].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal(":", runs[1].InnerText);
        Assert.NotNull(runs[2].GetFirstChild<TabChar>());
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
        DocxRenderer.Write(ms1, h, v, new ExportProvenance(), mid, "relative", DocxPageSize.A4, new DocxOptions());
        using var doc1 = Open(ms1.ToArray());
        Assert.Equal("3600",
            TurnStyle(doc1).StyleParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);

        // 53-char label -> 6600 twips, clamped to the 3.0" ceiling (4320).
        var longRow = new[] { new DisplayRow { StartMs = 1000,
            DisplayName = "Ms. Alexandra Fitzgerald-Whitmore de la Vega", Text = "Present." } };
        using var ms2 = new MemoryStream();
        DocxRenderer.Write(ms2, h, v, new ExportProvenance(), longRow, "relative", DocxPageSize.A4, new DocxOptions());
        using var doc2 = Open(ms2.ToArray());
        Assert.Equal("4320",
            TurnStyle(doc2).StyleParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);
    }

    [Fact]
    public void Markers_render_italic_in_the_text_column()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var marker = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText == "[audio device changed]");
        Assert.Equal("2160", marker.ParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);
        Assert.NotNull(marker.Elements<Run>().Single().RunProperties?.GetFirstChild<Italic>());
    }

    [Fact]
    public void Page_margins_are_explicit_one_inch_with_half_inch_header_footer()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
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
    public void Doc_defaults_pin_the_body_size_and_the_arial_face()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var rPr = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!
            .DocDefaults!.RunPropertiesDefault!.RunPropertiesBaseStyle!;
        Assert.Equal("22", rPr.FontSize!.Val!.Value);          // 11pt in half-points
        Assert.NotNull(rPr.RunFonts);                          // Arial pinned at document default (design 2026-08-03)
        Assert.Equal("Arial", rPr.RunFonts.Ascii!.Value);
    }

    [Fact]
    public void Document_defaults_pin_arial_on_every_script_slot()
    {
        // Word was rendering Times New Roman - never a choice. AddStyles pinned a font SIZE but
        // no face and there is no theme part, so Word fell back (design 2026-08-03 section 4).
        // DocDefaults (not the turn style) so headings, footer, header, markers and line numbers
        // all inherit.
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var fonts = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!
            .GetFirstChild<DocDefaults>()!.RunPropertiesDefault!.RunPropertiesBaseStyle!
            .GetFirstChild<RunFonts>()!;

        Assert.Equal("Arial", fonts.Ascii!.Value);
        Assert.Equal("Arial", fonts.HighAnsi!.Value);
        Assert.Equal("Arial", fonts.ComplexScript!.Value);
    }

    [Fact]
    public void Turn_style_spaces_turns_apart_and_controls_widows()
    {
        // 6pt after each turn: turns were back-to-back paragraphs with zero spacing, which read as
        // one dense wall. WidowControl keeps >=2 lines together so a speaker label cannot strand
        // alone at a page bottom; KeepLines is deliberately NOT used - it would push a whole
        // multi-page turn onto a new page (design 2026-08-03 section 4).
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var props = TurnStyle(doc).StyleParagraphProperties!;

        Assert.Equal("120", props.GetFirstChild<SpacingBetweenLines>()!.After!.Value);   // 120/20 = 6pt
        Assert.NotNull(props.GetFirstChild<WidowControl>());
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
    public void Every_line_is_numbered_for_page_line_citation()
    {
        // Page:line citation ("12:5") needs every line numbered, not every fifth. Restart-per-page
        // is unchanged. The fixed 25-lines-per-page deposition grid is deliberately NOT adopted -
        // it would force exact line spacing (design 2026-08-03 section 5).
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var ln = doc.MainDocumentPart!.Document!.Body!.GetFirstChild<SectionProperties>()!
            .GetFirstChild<LineNumberType>()!;

        Assert.Equal(1, ln.CountBy!.Value);
        Assert.Equal(LineNumberRestartValues.NewPage, ln.Restart!.Value);
    }

    [Fact]
    public void Line_numbering_suppresses_headers_and_counts_content_only()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var body = doc.MainDocumentPart!.Document!.Body!;

        // Numbering restarts per page (Every_line_is_numbered_for_page_line_citation tests the
        // CountBy value). This test focuses on suppression: every paragraph BEFORE the first turn
        // (title..disclaimer + spacer) suppresses numbering; turns and markers never do - they are
        // numbered transcript content.
        var ln = body.GetFirstChild<SectionProperties>()!.GetFirstChild<LineNumberType>()!;
        Assert.Equal(LineNumberRestartValues.NewPage, ln.Restart!.Value);

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
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
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
    public void Footer_pairs_the_text_with_page_and_numpages_fields_at_a_right_tab_on_the_usable_width()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var footer = doc.MainDocumentPart!.FooterParts.Single().Footer!;
        var par = footer.Elements<Paragraph>().Single();

        var tab = par.ParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().Single();
        Assert.Equal(TabStopValues.Right, tab.Val!.Value);
        Assert.Equal(9026, tab.Position!.Value);               // A4 11906 - 2x1440 margins

        // design 2026-08-03 section 2: {transcript name} + "Page N of M" - both fields paired.
        Assert.StartsWith("Weekly Sync", footer.InnerText);
        Assert.Equal(new[] { " PAGE ", " NUMPAGES " },
            par.Descendants<FieldCode>().Select(f => f.Text).ToList());
        var fieldChars = par.Descendants<FieldChar>().ToList();
        Assert.Equal(6, fieldChars.Count);                     // (begin/separate/end) x2 fields
        Assert.Equal(FieldCharValues.Begin, fieldChars[0].FieldCharType!.Value);
        Assert.Equal(FieldCharValues.Separate, fieldChars[1].FieldCharType!.Value);
        Assert.Equal(FieldCharValues.End, fieldChars[2].FieldCharType!.Value);
        Assert.Equal(FieldCharValues.Begin, fieldChars[3].FieldCharType!.Value);
        Assert.Equal(FieldCharValues.Separate, fieldChars[4].FieldCharType!.Value);
        Assert.Equal(FieldCharValues.End, fieldChars[5].FieldCharType!.Value);
    }

    [Fact]
    public void Footer_right_tab_uses_the_letter_usable_width_on_letter_pages()
    {
        byte[] bytes = Render("relative", DocxPageSize.Letter, new DocxOptions());
        using var doc = Open(bytes);
        var tab = doc.MainDocumentPart!.FooterParts.Single().Footer!.Elements<Paragraph>().Single()
            .ParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().Single();
        Assert.Equal(9360, tab.Position!.Value);               // Letter 12240 - 2x1440 margins
    }

    [Fact]
    public void Footer_carries_the_title_and_a_page_x_of_y_field()
    {
        // design 2026-08-03 section 2: footer is exactly {transcript name} + Page N of M. The
        // privilege string and the model description are gone - version provenance moved up into
        // the metadata block, where it is stated once instead of on every page.
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions(),
            new ExportProvenance { VersionId = "v2-large-v3-turbo-2026-08-01", Model = "large-v3-turbo" });
        using var doc = Open(bytes);
        var footer = doc.MainDocumentPart!.FooterParts.First().Footer!;

        Assert.StartsWith("Weekly Sync", footer.InnerText);
        Assert.DoesNotContain("PRIVILEGED", footer.InnerText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("large-v3-turbo", footer.InnerText);

        var codes = footer.Descendants<FieldCode>().Select(f => f.Text.Trim()).ToList();
        Assert.Contains("PAGE", codes);
        Assert.Contains("NUMPAGES", codes);
        Assert.Contains("of", footer.InnerText);
    }

    [Fact]
    public void Page_header_pairs_the_matter_and_date_with_a_styleref_running_head()
    {
        // design 2026-08-03 section 3. Word searches the current page top-to-bottom for the style
        // and, if it finds none, searches BACKWARD from the top of the page to the start of the
        // document - so a page holding only continuation text still resolves to whoever is
        // speaking. That fallback is the whole point of the running head.
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;
        var sect = main.Document!.Body!.GetFirstChild<SectionProperties>()!;

        string defaultId = sect.Elements<HeaderReference>()
            .Single(h => h.Type!.Value == HeaderFooterValues.Default).Id!.Value!;
        var header = ((HeaderPart)main.GetPartById(defaultId)).Header!;

        Assert.Contains("Acme (2026-014)", header.InnerText);
        Assert.Contains("2026-06-30", header.InnerText);
        // The field argument is the style NAME ("Transcript Speaker"), not the styleId
        // ("TranscriptSpeaker") - Word's STYLEREF parser only ever resolves w:name.
        Assert.Contains("STYLEREF \"Transcript Speaker\"",
            string.Concat(header.Descendants<FieldCode>().Select(f => f.Text)));
    }

    [Fact]
    public void First_page_suppresses_the_header_but_keeps_the_footer()
    {
        // The metadata block already names everything on page 1, so the running head there is
        // noise. TitlePg is what suppresses it - but TitlePg ALSO drops the page-1 footer unless a
        // first-page FooterReference is supplied, and page 1 must still show "Page 1 of N".
        // Pointing it at the SAME footer part id is what keeps them identical.
        byte[] bytes = Render("relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;
        var sect = main.Document!.Body!.GetFirstChild<SectionProperties>()!;

        Assert.NotNull(sect.GetFirstChild<TitlePage>());

        string firstHeaderId = sect.Elements<HeaderReference>()
            .Single(h => h.Type!.Value == HeaderFooterValues.First).Id!.Value!;
        Assert.Equal("", ((HeaderPart)main.GetPartById(firstHeaderId)).Header!.InnerText);

        string defaultFooterId = sect.Elements<FooterReference>()
            .Single(f => f.Type!.Value == HeaderFooterValues.Default).Id!.Value!;
        string firstFooterId = sect.Elements<FooterReference>()
            .Single(f => f.Type!.Value == HeaderFooterValues.First).Id!.Value!;
        Assert.Equal(defaultFooterId, firstFooterId);
    }

    [Fact]
    public void Header_left_truncates_a_matter_name_over_sixty_chars_with_an_ellipsis()
    {
        // HeaderLeft (design 2026-08-03 section 3): STYLEREF cannot truncate, so the composed
        // left half must - to 60 chars total, the 60th being a trailing ellipsis.
        var (h, v, _) = Sample();
        string longMatter = new string('A', 65);
        var v2 = v with { Matters = new[] { longMatter } };
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v2, new ExportProvenance(), Array.Empty<DisplayRow>(),
            "relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(ms.ToArray());
        string headerText = DefaultHeader(doc).InnerText;

        Assert.Contains(new string('A', 59) + "\u2026", headerText);
        Assert.DoesNotContain(new string('A', 60), headerText);   // the 60th 'A' was cut, not kept
    }

    [Fact]
    public void Header_left_keeps_a_matter_name_at_exactly_sixty_chars_whole()
    {
        // The boundary itself must NOT truncate - only strictly-over-60 does.
        var (h, v, _) = Sample();
        string exact = new string('B', 60);
        var v2 = v with { Matters = new[] { exact } };
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v2, new ExportProvenance(), Array.Empty<DisplayRow>(),
            "relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(ms.ToArray());
        string headerText = DefaultHeader(doc).InnerText;

        Assert.Contains(exact, headerText);
        Assert.DoesNotContain("\u2026", headerText);
    }

    [Fact]
    public void Header_left_falls_back_to_the_title_when_the_session_has_no_matters()
    {
        var (h, v, _) = Sample();
        var v2 = v with { Matters = Array.Empty<string>() };
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v2, new ExportProvenance(), Array.Empty<DisplayRow>(),
            "relative", DocxPageSize.A4, new DocxOptions());
        using var doc = Open(ms.ToArray());
        string headerText = DefaultHeader(doc).InnerText;

        Assert.Contains(v.Title, headerText);
    }

    [Fact]
    public void Header_left_backs_off_a_truncation_that_would_split_a_surrogate_pair()
    {
        // A matter name where the naive cut point (index 59) lands inside a surrogate pair must
        // NOT throw - XmlWriter rejects an unpaired high surrogate with ArgumentException at save
        // time, which is worse than a blank/truncated header (design 2026-08-03 section 3 review).
        var (h, v, _) = Sample();
        string emoji = "\U0001F600";   // one astral char = two UTF-16 code units
        string straddling = new string('C', 58) + emoji + new string('D', 10);
        var v2 = v with { Matters = new[] { straddling } };
        using var ms = new MemoryStream();

        var ex = Record.Exception(() => DocxRenderer.Write(ms, h, v2, new ExportProvenance(),
            Array.Empty<DisplayRow>(), "relative", DocxPageSize.A4, new DocxOptions()));
        Assert.Null(ex);

        using var doc = Open(ms.ToArray());
        string headerText = DefaultHeader(doc).InnerText;
        Assert.Contains(new string('C', 58) + "\u2026", headerText);
        Assert.DoesNotContain(emoji, headerText);
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
        DocxRenderer.Write(ms, h, v, new ExportProvenance(), rows, "relative", DocxPageSize.A4,
            new DocxOptions { TimestampIntervalMs = 15000 });
        using var doc = Open(ms.ToArray());
        var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToList();

        Assert.Single(paragraphs, p => p.InnerText == "[00:00] Sam:one two three four");
        var cont = paragraphs.Single(p => p.InnerText == "[00:19]five");
        Assert.Equal("TranscriptTurn", cont.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.Null(cont.ParagraphProperties!.GetFirstChild<SuppressLineNumbers>());   // counts as content
        var runs = cont.Elements<Run>().ToList();
        // Stamp / tab / text (design 2026-08-03 section 3): name and suffix are both empty for a
        // continuation, and TurnParagraph guards all three parts on Length > 0, so no dead empty
        // run is emitted - Task 9 gives continuations a real name (and suffix) later.
        Assert.Equal(3, runs.Count);
        Assert.NotNull(runs[0].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal("[00:19]", runs[0].InnerText);            // stamp only - the name is not repeated
        Assert.NotNull(runs[1].GetFirstChild<TabChar>());
        Assert.Equal("five", runs[2].InnerText);
    }
}
