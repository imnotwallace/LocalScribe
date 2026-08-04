// tests/LocalScribe.Core.Tests/DocxRendererTests.cs
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
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

    private static byte[] Render(string mode, DocxPageSize size, ExportOptions opts,
        ExportProvenance? provenance = null, ExportSummary? summary = null)
    {
        var (h, v, r) = Sample();
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v, provenance ?? new ExportProvenance(), summary, r, mode, size, opts);
        return ms.ToArray();   // valid even after the document disposed/closed the stream
    }

    private static WordprocessingDocument Open(byte[] bytes)
        => WordprocessingDocument.Open(new MemoryStream(bytes), false);

    // Small independent fixture for the summary-section tests below (Task 9), matching the
    // shape PlainTextRendererWriteTests/MarkdownRendererWriteTests use - kept separate from
    // Sample() because those tests do not need the Weekly Sync/marker/two-speaker shape.
    private static TranscriptHeader Header() =>
        new("Doe intake", "Webex", Started, 1_800_000, "large-v3-turbo", "cuda");

    private static SessionTextView Meta() =>
        new("Doe intake", ["Doe v Roe (2026/014)"], ["Sam (Counsel)"], Started,
            Started.AddMinutes(30), 1_800_000, "Webex", "", Summary: null);

    private static DisplayRow Turn(long startMs, long endMs, string name, string text) =>
        new() { StartMs = startMs, EndMs = endMs, DisplayName = name, Text = text };

    private static ExportSummary Summary(string? stale = null) => new()
    {
        ContentMarkdown = "## Summary\nThey agreed to file.\n\n## Key topics\n- costs\n",
        ProvenanceLine = "generated 2026-08-01 14:22, Qwen3-4B-Instruct-2507.gguf (CUDA)",
        StaleNotice = stale,
    };

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
    public void Rendered_document_passes_open_xml_schema_validation()
    {
        // Regression guard (final whole-branch review finding): no OpenXmlValidator call existed
        // anywhere in this repo, which is how a schema-invalid CT_PPrBase child element order
        // (ParagraphBorders/Tabs swapped in RunningHeadParagraph; Tabs/SpacingBetweenLines/
        // Indentation ahead of WidowControl in the TranscriptTurn style) shipped past ~490 lines of
        // docx tests that only ever asserted element VALUES, never structural validity. The SDK
        // builds and saves either ordering without complaint - and Word CAN open either ordering -
        // but Word can also flag the document as corrupt, and Office2019 schema validation is the
        // only thing in this repo that would have caught it before Word did.
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
        using var doc = Open(bytes);

        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(doc).ToList();

        string detail = string.Join(Environment.NewLine,
            errors.Select(e => $"{e.Path?.XPath} [{e.ErrorType}] {e.Description}"));
        Assert.True(errors.Count == 0,
            $"OpenXml schema validation found {errors.Count} error(s):{Environment.NewLine}{detail}");
    }

    [Fact]
    public void Renders_metadata_disclaimer_marker_footer_and_a4_pagesize()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;
        string text = main.Document!.Body!.InnerText;

        Assert.Contains("Weekly Sync", text);
        Assert.Contains("Participants: Sam, Bob (Counsel)", text);
        Assert.Contains("Matter(s): Acme (2026-014)", text);
        Assert.Contains(ExportNotices.Disclaimer, text);
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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
        using var doc = Open(bytes);

        // The named style carries the geometry so recipients can retune it in Word.
        // TextColumnTwips reserves room for the " (cont'd)" a continuation of this row would add
        // (design 2026-08-03 section 8), so even these short sample labels clear the 1.5" floor:
        // "[00:01] Sam:" (12 chars) + " (cont'd)" (9) = 21 -> 21*120 + 240 = 2760, unclamped.
        var style = TurnStyle(doc);
        var ind = style.StyleParagraphProperties!.GetFirstChild<Indentation>()!;
        Assert.Equal("2760", ind.Left!.Value);
        Assert.Equal("2760", ind.Hanging!.Value);   // hanging == left: wrapped lines align at the column
        var tab = style.StyleParagraphProperties!.GetFirstChild<Tabs>()!.Elements<TabStop>().Single();
        Assert.Equal(TabStopValues.Left, tab.Val!.Value);
        Assert.Equal(2760, tab.Position!.Value);

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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
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
            new ExportOptions { IncludeTimestamps = false, IncludeMarkers = false });
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
        // "Sam:" (4 chars) + " (cont'd)" (9) = 13 -> 13*120 + 240 = 1800, still under the floor.
        Assert.Equal("2160", TurnStyle(doc).StyleParagraphProperties!
            .GetFirstChild<Indentation>()!.Left!.Value);      // name-only labels still get the floor
        Assert.Equal(12240u, body.GetFirstChild<SectionProperties>()!
            .GetFirstChild<PageSize>()!.Width!.Value);        // Letter width in twips
    }

    [Fact]
    public void Text_column_scales_with_the_longest_label_and_clamps_at_three_inches()
    {
        var (h, v, _) = Sample();

        // "[00:01] Jane Smith:" = 19 chars + " (cont'd)" (9) = 28 -> 28*120 + 240 = 3600 twips
        // (2.5", unclamped). TextColumnTwips measures the continuation FORM of every label now
        // (design 2026-08-03 section 8), so the base label alone is 9 chars short of this total.
        var mid = new[] { new DisplayRow
        { StartMs = 1000, DisplayName = "Jane Smith", Text = "Yes." } };
        using var ms1 = new MemoryStream();
        DocxRenderer.Write(ms1, h, v, new ExportProvenance(), null, mid, "relative", DocxPageSize.A4, new ExportOptions());
        using var doc1 = Open(ms1.ToArray());
        Assert.Equal("3600",
            TurnStyle(doc1).StyleParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);

        // 53-char label + " (cont'd)" (9) = 62 -> 7680 twips, clamped to the 3.0" ceiling (4320).
        var longRow = new[] { new DisplayRow { StartMs = 1000,
            DisplayName = "Ms. Alexandra Fitzgerald-Whitmore de la Vega", Text = "Present." } };
        using var ms2 = new MemoryStream();
        DocxRenderer.Write(ms2, h, v, new ExportProvenance(), null, longRow, "relative", DocxPageSize.A4, new ExportOptions());
        using var doc2 = Open(ms2.ToArray());
        Assert.Equal("4320",
            TurnStyle(doc2).StyleParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);
    }

    [Fact]
    public void Markers_render_italic_in_the_text_column()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
        using var doc = Open(bytes);
        var marker = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText == "[audio device changed]");
        // Textcol here is the Sam/Bob sample's continuation-inclusive width (2760, see
        // Turn_paragraphs_reference_the_turn_style_with_bold_label_tab_text_runs), not the floor -
        // the marker just indents to whatever column the turns settled on.
        Assert.Equal("2760", marker.ParagraphProperties!.GetFirstChild<Indentation>()!.Left!.Value);
        Assert.NotNull(marker.Elements<Run>().Single().RunProperties?.GetFirstChild<Italic>());
    }

    [Fact]
    public void Page_margins_are_explicit_one_inch_with_half_inch_header_footer()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
        using var doc = Open(bytes);
        var ln = doc.MainDocumentPart!.Document!.Body!.GetFirstChild<SectionProperties>()!
            .GetFirstChild<LineNumberType>()!;

        Assert.Equal(1, ln.CountBy!.Value);
        Assert.Equal(LineNumberRestartValues.NewPage, ln.Restart!.Value);
    }

    [Fact]
    public void Line_numbering_suppresses_headers_and_counts_content_only()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
        using var doc = Open(bytes);
        var disclaimer = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Single(p => p.InnerText == ExportNotices.Disclaimer);
        var border = disclaimer.ParagraphProperties!.GetFirstChild<ParagraphBorders>()!
            .GetFirstChild<BottomBorder>()!;
        Assert.Equal(BorderValues.Single, border.Val!.Value);
        Assert.Equal(4u, border.Size!.Value);                  // eighths of a point -> 0.5pt rule
        Assert.NotNull(disclaimer.Elements<Run>().Single().RunProperties?.GetFirstChild<Italic>());
    }

    [Fact]
    public void Footer_pairs_the_text_with_page_and_numpages_fields_at_a_right_tab_on_the_usable_width()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
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
        byte[] bytes = Render("relative", DocxPageSize.Letter, new ExportOptions());
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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(),
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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
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
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
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
        DocxRenderer.Write(ms, h, v2, new ExportProvenance(), null, Array.Empty<DisplayRow>(),
            "relative", DocxPageSize.A4, new ExportOptions());
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
        DocxRenderer.Write(ms, h, v2, new ExportProvenance(), null, Array.Empty<DisplayRow>(),
            "relative", DocxPageSize.A4, new ExportOptions());
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
        DocxRenderer.Write(ms, h, v2, new ExportProvenance(), null, Array.Empty<DisplayRow>(),
            "relative", DocxPageSize.A4, new ExportOptions());
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

        var ex = Record.Exception(() => DocxRenderer.Write(ms, h, v2, new ExportProvenance(), null,
            Array.Empty<DisplayRow>(), "relative", DocxPageSize.A4, new ExportOptions()));
        Assert.Null(ex);

        using var doc = Open(ms.ToArray());
        string headerText = DefaultHeader(doc).InnerText;
        Assert.Contains(new string('C', 58) + "\u2026", headerText);
        Assert.DoesNotContain(emoji, headerText);
    }

    [Fact]
    public void Cadence_continuations_render_stamp_name_and_contd_suffix_in_the_turn_style()
    {
        // Time-triggered split (TimestampIntervalMs=15000; the 23-char row text never crosses
        // ContinuationMaxChars). Task 9 (design 2026-08-03 section 8): the continuation label now
        // repeats the name with a " (cont'd)" suffix, so a reader landing on this paragraph's page
        // still knows who is speaking - it is no longer a bare stamp.
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
        DocxRenderer.Write(ms, h, v, new ExportProvenance(), null, rows, "relative", DocxPageSize.A4,
            new ExportOptions { TimestampIntervalMs = 15000 });
        using var doc = Open(ms.ToArray());
        var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToList();

        Assert.Single(paragraphs, p => p.InnerText == "[00:00] Sam:one two three four");
        var cont = paragraphs.Single(p => p.InnerText == "[00:19] Sam (cont'd):five");
        Assert.Equal("TranscriptTurn", cont.ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.Null(cont.ParagraphProperties!.GetFirstChild<SuppressLineNumbers>());   // counts as content
        var runs = cont.Elements<Run>().ToList();
        // Stamp / name / suffix / tab / text - the same five-run shape as a normal turn label
        // (design 2026-08-03 sections 3, 8), so only the name run carries TranscriptSpeaker and
        // the running head never shows "(cont'd)".
        Assert.Equal(5, runs.Count);
        Assert.NotNull(runs[0].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal("[00:19] ", runs[0].InnerText);
        Assert.Equal("TranscriptSpeaker", runs[1].RunProperties?.GetFirstChild<RunStyle>()?.Val?.Value);
        Assert.Equal("Sam", runs[1].InnerText);
        Assert.NotNull(runs[2].RunProperties?.GetFirstChild<Bold>());
        Assert.Equal(" (cont'd):", runs[2].InnerText);
        Assert.NotNull(runs[3].GetFirstChild<TabChar>());
        Assert.Equal("five", runs[4].InnerText);
    }

    [Fact]
    public void Continuation_paragraphs_repeat_the_speaker_name_with_contd()
    {
        // A turn can run for pages with the name only at the top, so flipping to a page mid-turn
        // left the reader with no attribution. "(cont'd)" sits OUTSIDE the styled name run so the
        // running head shows the name alone (design 2026-08-03 sections 3, 8).
        var h = new TranscriptHeader("Long Turn", "Teams", Started, 600000, "small.en", "CUDA");
        var v = new SessionTextView("Long Turn", [], ["Sam"], Started, Started.AddMinutes(10),
            600000, "Teams", "", null);
        var segments = Enumerable.Range(0, 40)
            .Select(i => new RowSegment(i, TranscriptSource.Local, i * 1000L, i * 1000L + 900,
                new string('w', 100), new string('w', 100), false, false))
            .ToList();
        var rows = new[]
        {
            new DisplayRow
            {
                StartMs = 0, DisplayName = "Sam",
                Text = string.Join(" ", segments.Select(s => s.ProjectedText)),
                Segments = segments,
            },
        };

        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, h, v, new ExportProvenance(), null, rows, "relative",
            DocxPageSize.A4, new ExportOptions());
        using var doc = Open(ms.ToArray());

        var contd = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>()
            .Where(p => p.InnerText.Contains("(cont'd)", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(contd);

        // The name run is styled; "(cont'd):" is not - otherwise the running head would read
        // "SAM (CONT'D)".
        var styled = contd[0].Elements<Run>()
            .Where(r => r.RunProperties?.GetFirstChild<RunStyle>()?.Val?.Value == "TranscriptSpeaker");
        Assert.Equal("Sam", Assert.Single(styled).InnerText);
    }

    [Fact]
    public void Metadata_block_carries_duration_version_and_speakers_heard()
    {
        // Version/model provenance moved OFF the footer and up here, stated once (design
        // 2026-08-03 sections 2, 6).
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance
            {
                VersionId = "v2-large-v3-turbo-2026-08-01",
                Model = "large-v3-turbo",
                Backend = "cuda",
            });
        using var doc = Open(bytes);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.Contains("Date: 2026-06-30 14:32 - 15:09 (37 min)", text);
        Assert.Contains("Transcript version: v2 \u00B7 large-v3-turbo \u00B7 cuda", text);
        Assert.Contains("Speakers heard: Sam, Bob", text);
    }

    [Fact]
    public void Audio_provenance_renders_for_an_imported_session_and_is_absent_otherwise()
    {
        // ImportedSourceInfo already carries FileName + a Sha256 computed at copy time. Recorded
        // sessions have no hash and hashing their FLAC on every export is out of scope.
        byte[] imported = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance { AudioFileName = "call.m4a", AudioSha256 = "abc123" });
        using (var doc = Open(imported))
        {
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Audio: call.m4a", text);
            Assert.Contains("Audio SHA-256: abc123", text);
        }

        byte[] recorded = Render("relative", DocxPageSize.A4, new ExportOptions());
        using (var doc = Open(recorded))
        {
            string text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.DoesNotContain("Audio:", text);
            Assert.DoesNotContain("Audio SHA-256:", text);
        }
    }

    [Fact]
    public void In_progress_export_is_labelled_in_the_block_and_on_every_page()
    {
        // Exported mid-recording the document is materially weaker than the same session after
        // Stop: diarisation has not run, so speakers are the generic Local/Remote split, and the
        // transcript is incomplete. Every page says so - the header covers pages 2+, the metadata
        // block covers page 1 (where the header is suppressed) (design 2026-08-03 section 11).
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(),
            new ExportProvenance { InProgress = true });
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;

        Assert.Contains(ExportNotices.InProgressNotice, main.Document!.Body!.InnerText);

        string defaultId = main.Document.Body!.GetFirstChild<SectionProperties>()!
            .Elements<HeaderReference>()
            .Single(h => h.Type!.Value == HeaderFooterValues.Default).Id!.Value!;
        Assert.Contains(ExportNotices.InProgressNotice,
            ((HeaderPart)main.GetPartById(defaultId)).Header!.InnerText);
    }

    [Fact]
    public void Finalised_export_carries_no_in_progress_notice()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());
        using var doc = Open(bytes);
        var main = doc.MainDocumentPart!;

        Assert.DoesNotContain(ExportNotices.InProgressNotice, main.Document!.Body!.InnerText);

        string defaultId = main.Document.Body!.GetFirstChild<SectionProperties>()!
            .Elements<HeaderReference>()
            .Single(h => h.Type!.Value == HeaderFooterValues.Default).Id!.Value!;
        Assert.DoesNotContain(ExportNotices.InProgressNotice,
            ((HeaderPart)main.GetPartById(defaultId)).Header!.InnerText);
    }

    [Fact]
    public void Summary_renders_under_the_heading_with_the_locked_draft_label()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(), summary: Summary());
        using var doc = Open(bytes);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.Contains(ExportNotices.SummaryHeading, text);
        Assert.Contains(LocalScribe.Core.Assistant.AssistantPrompts.DraftLabel, text);
        Assert.Contains("generated 2026-08-01 14:22", text);
        Assert.Contains("They agreed to file.", text);
    }

    [Fact]
    public void A_null_summary_renders_no_summary_section()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions());   // summary defaults to null
        using var doc = Open(bytes);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.DoesNotContain(ExportNotices.SummaryHeading, text);
        Assert.DoesNotContain(LocalScribe.Core.Assistant.AssistantPrompts.DraftLabel, text);
    }

    [Fact]
    public void The_stale_notice_renders_when_present()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(),
            summary: Summary("OUT OF DATE: the transcript changed after this summary was generated."));
        using var doc = Open(bytes);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.Contains("OUT OF DATE", text);
    }

    [Fact]
    public void Summary_gets_a_scope_notice_when_the_transcript_is_excerpted()
    {
        // Fix 2 (whole-branch review): IncludeSummary and ExcerptRange are orthogonal options -
        // a user can tick both, and without this notice a reader cannot tell whether the summary
        // describes the excerpt or the whole session.
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(),
            provenance: new ExportProvenance { ExcerptSpan = "00:12:30-00:18:45 of 01:47:12" },
            summary: Summary());
        using var doc = Open(bytes);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.Contains(ExportNotices.SummaryCoversMoreThanExcerpt, text);
    }

    [Fact]
    public void Summary_carries_no_scope_notice_when_not_excerpted()
    {
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(), summary: Summary());
        using var doc = Open(bytes);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.DoesNotContain(ExportNotices.SummaryCoversMoreThanExcerpt, text);
    }

    [Fact]
    public void Summary_scope_notice_stacks_with_a_stale_notice()
    {
        // Independent of StaleNotice: a summary can be BOTH stale AND out of scope at once.
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(),
            provenance: new ExportProvenance { ExcerptSpan = "00:12:30-00:18:45 of 01:47:12" },
            summary: Summary("OUT OF DATE: the transcript changed after this summary was generated."));
        using var doc = Open(bytes);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.Contains("OUT OF DATE", text);
        Assert.Contains(ExportNotices.SummaryCoversMoreThanExcerpt, text);
    }

    [Fact]
    public void The_summary_scope_notice_paragraph_is_bold_and_suppresses_line_numbers()
    {
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(),
            new ExportProvenance { ExcerptSpan = "00:00:00-00:00:04 of 00:30:00" }, Summary(),
            [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var paragraphs = doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().ToList();
        var notice = paragraphs.Single(p => p.InnerText.Contains(ExportNotices.SummaryCoversMoreThanExcerpt));

        Assert.NotNull(notice.ParagraphProperties?.SuppressLineNumbers);
        Assert.NotNull(notice.Descendants<Run>().First().RunProperties?.Bold);
    }

    [Fact]
    public void Every_summary_paragraph_suppresses_line_numbers()
    {
        // Round 1's line numbering counts TRANSCRIPT CONTENT ONLY. Miss this and inserting a
        // summary silently renumbers the whole transcript, invalidating every page:line citation
        // into a document that looks unchanged (design 2026-08-04 section 7).
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(), new ExportProvenance(), Summary(),
            [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart!.Document.Body!;
        var paragraphs = body.Elements<Paragraph>().ToList();

        int headingIndex = paragraphs.FindIndex(p => p.InnerText.Contains(ExportNotices.SummaryHeading));
        int disclaimerIndex = paragraphs.FindIndex(p => p.InnerText.Contains(ExportNotices.Disclaimer));
        Assert.True(headingIndex >= 0);
        Assert.True(headingIndex < disclaimerIndex);   // summary sits ABOVE the closing rule

        for (int i = headingIndex; i < disclaimerIndex; i++)
            Assert.NotNull(paragraphs[i].ParagraphProperties?.SuppressLineNumbers);
    }

    [Fact]
    public void A_summary_does_not_change_the_transcripts_own_line_numbering()
    {
        static int NumberedParagraphs(ExportSummary? summary)
        {
            using var ms = new MemoryStream();
            DocxRenderer.Write(ms, Header(), Meta(), new ExportProvenance(), summary,
                [Turn(0, 4000, "Sam", "hello"), Turn(5000, 9000, "Bob", "hi")], "relative",
                DocxPageSize.A4, new ExportOptions());
            ms.Position = 0;
            using var doc = WordprocessingDocument.Open(ms, false);
            return doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>()
                .Count(p => p.ParagraphProperties?.SuppressLineNumbers is null);
        }

        Assert.Equal(NumberedParagraphs(null), NumberedParagraphs(Summary()));
    }

    [Fact]
    public void Summary_section_with_a_bullet_paragraph_passes_open_xml_schema_validation()
    {
        // Trap 2 (design 2026-08-04 section 7 / task-9 brief): the bullet paragraph's pPr carries
        // BOTH SuppressLineNumbers(8) and Indentation(23) - the SDK accepts either order and the
        // element-value tests above would still pass, but Word calls the file corrupt if they are
        // swapped. Rendered_document_passes_open_xml_schema_validation above never exercises this
        // paragraph (it renders with summary: null), so this is a dedicated regression guard.
        byte[] bytes = Render("relative", DocxPageSize.A4, new ExportOptions(),
            summary: Summary() with { ContentMarkdown = "## Key topics\n- costs\n- timeline\n" });
        using var doc = Open(bytes);

        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(doc).ToList();

        string detail = string.Join(Environment.NewLine,
            errors.Select(e => $"{e.Path?.XPath} [{e.ErrorType}] {e.Description}"));
        Assert.True(errors.Count == 0,
            $"OpenXml schema validation found {errors.Count} error(s):{Environment.NewLine}{detail}");
    }

    [Fact]
    public void Markdown_content_gets_a_line_level_transform_and_no_inline_parsing()
    {
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(), new ExportProvenance(),
            Summary() with { ContentMarkdown = "## Key topics\n- costs\n**bold** stays literal\n" },
            [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        string text = doc.MainDocumentPart!.Document.Body!.InnerText;

        Assert.Contains("Key topics", text);
        Assert.DoesNotContain("## Key topics", text);      // heading marker consumed
        Assert.Contains("\u2022 costs", text);              // bullet rendered
        Assert.DoesNotContain("- costs", text);
        Assert.Contains("**bold** stays literal", text);   // NO inline parsing, documented limit
    }

    [Fact]
    public void An_excerpt_renders_the_notice_on_page_1_and_in_the_pages_2_plus_header()
    {
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(),
            new ExportProvenance { ExcerptSpan = "00:12:30-00:18:45 of 01:47:12" }, null,
            [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var main = doc.MainDocumentPart!;

        Assert.Contains("00:12:30-00:18:45 of 01:47:12", main.Document.Body!.InnerText);
        Assert.Contains(ExportNotices.ExcerptNotice, main.Document.Body!.InnerText);

        // The DEFAULT header part (pages 2+) carries it; the FIRST-page part stays empty.
        var sectPr = main.Document.Body!.Elements<SectionProperties>().Single();
        string defaultId = sectPr.Elements<HeaderReference>()
            .Single(h => h.Type is not null && h.Type.Value == HeaderFooterValues.Default).Id!;
        var defaultHeader = (HeaderPart)main.GetPartById(defaultId);
        Assert.Contains(ExportNotices.ExcerptNotice, defaultHeader.Header!.InnerText);
    }

    [Fact]
    public void A_complete_transcript_renders_no_excerpt_notice_anywhere()
    {
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(), new ExportProvenance(), null,
            [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        Assert.DoesNotContain("EXCERPT", doc.MainDocumentPart!.Document.Body!.InnerText);
    }

    [Fact]
    public void Excerpt_and_in_progress_stack_as_two_header_paragraphs_ahead_of_the_running_head()
    {
        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(),
            new ExportProvenance { InProgress = true, ExcerptSpan = "00:00:00-00:00:04 of 00:30:00" },
            null, [Turn(0, 4000, "Sam", "hello")], "relative", DocxPageSize.A4, new ExportOptions());

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var main = doc.MainDocumentPart!;
        var sectPr = main.Document.Body!.Elements<SectionProperties>().Single();
        string defaultId = sectPr.Elements<HeaderReference>()
            .Single(h => h.Type is not null && h.Type.Value == HeaderFooterValues.Default).Id!;
        var paragraphs = ((HeaderPart)main.GetPartById(defaultId)).Header!.Elements<Paragraph>().ToList();

        Assert.Equal(3, paragraphs.Count);
        Assert.Contains(ExportNotices.InProgressNotice, paragraphs[0].InnerText);
        Assert.Contains(ExportNotices.ExcerptNotice, paragraphs[1].InnerText);
        Assert.Contains("STYLEREF", paragraphs[2].InnerXml);        // the running head, untouched
    }

    [Fact]
    public void A_document_with_summary_excerpt_and_in_progress_all_at_once_is_schema_valid()
    {
        // Task 14 (whole-round verification): three stacked header paragraphs plus a new body
        // section is the shape most likely to trip Word's pPr child ordering, and the OpenXML SDK
        // accepts an invalid order SILENTLY. "Hardest possible document": in-progress + excerpt
        // notices stacked ahead of the running head, a summary section (heading/draft-label/
        // provenance/stale-notice/bulleted content) ahead of the disclaimer rule, AND a genuine
        // cadence-driven (cont'd) continuation paragraph in the transcript body - the Sam row below
        // carries Segments spanning >15s so TimestampCadence.Chunk actually splits it (a plain
        // Turn() row carries no Segments and TimestampCadence never chunks one - see
        // Cadence_continuations_render_stamp_name_and_contd_suffix_in_the_turn_style above).
        var segments = new[]
        {
            new RowSegment(0, TranscriptSource.Local, 0, 4000, "one", "one", false, false),
            new RowSegment(1, TranscriptSource.Local, 4400, 9000, "two", "two", false, false),
            new RowSegment(2, TranscriptSource.Local, 9400, 14000, "three", "three", false, false),
            new RowSegment(3, TranscriptSource.Local, 14400, 19000, "four", "four", false, false),
            new RowSegment(4, TranscriptSource.Local, 19400, 24000, "five", "five", false, false),
        };
        var rows = new[]
        {
            new DisplayRow
            {
                StartMs = 0, EndMs = 24000, DisplayName = "Sam",
                Text = "one two three four five", Segments = segments,
            },
            Turn(25000, 29000, "Bob", "hi"),
        };

        using var ms = new MemoryStream();
        DocxRenderer.Write(ms, Header(), Meta(),
            new ExportProvenance
            {
                InProgress = true,
                ExcerptSpan = "00:00:00-00:00:04 of 00:30:00",
                AudioFileName = "intake.m4a",
                AudioSha256 = "abc123",
            },
            Summary("OUT OF DATE: the transcript changed after this summary was generated."),
            rows, "relative", DocxPageSize.A4, new ExportOptions { TimestampIntervalMs = 15000 });

        ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(doc).ToList();

        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => e.Description + " @ " + e.Path?.XPath)));

        // Self-review guard (task-14 brief): confirm this document actually stacks all three
        // notices, the summary and a real continuation paragraph, so a future change that silently
        // drops one of them while staying schema-valid still fails this test rather than passing
        // vacuously.
        var main = doc.MainDocumentPart!;
        string bodyText = main.Document!.Body!.InnerText;
        Assert.Contains(ExportNotices.InProgressNotice, bodyText);
        Assert.Contains(ExportNotices.ExcerptNotice, bodyText);
        Assert.Contains(ExportNotices.SummaryHeading, bodyText);
        Assert.Contains("OUT OF DATE", bodyText);
        Assert.Contains("(cont'd)", bodyText);

        var sectPr = main.Document.Body!.Elements<SectionProperties>().Single();
        string defaultId = sectPr.Elements<HeaderReference>()
            .Single(h => h.Type is not null && h.Type.Value == HeaderFooterValues.Default).Id!;
        var headerParagraphs =
            ((HeaderPart)main.GetPartById(defaultId)).Header!.Elements<Paragraph>().ToList();
        Assert.Equal(3, headerParagraphs.Count);
        Assert.Contains(ExportNotices.InProgressNotice, headerParagraphs[0].InnerText);
        Assert.Contains(ExportNotices.ExcerptNotice, headerParagraphs[1].InnerText);
    }
}
