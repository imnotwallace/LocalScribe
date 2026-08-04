using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LocalScribe.Core.Assistant;
namespace LocalScribe.Core.Projection;

/// <summary>Page size for an exported .docx. Chosen from the machine locale AT the export call site
/// (RegionInfo) and passed in - the ONLY machine-locale dependence, scoped to page size (spec 11.2).</summary>
public enum DocxPageSize { A4, Letter }

/// <summary>Serializes a .docx transcript projection (spec 11.2, design 2026-08-02 item 6) into a
/// stream, in the courtroom layout: each turn is one paragraph in the named TranscriptTurn style -
/// bold "[00:00] Name:" label, tab, text - with a hanging indent so wrapped lines align at a text
/// column auto-sized from the longest label. Same render model as MarkdownRenderer (TranscriptHeader
/// + rows) plus the SessionTextView metadata block (user-curated participants, NEVER speakers.json
/// clusters) and a NON-OPTIONAL machine-generated-accuracy disclaimer. All TEXT is invariant-culture;
/// markers render italic in the text column. Rows arrive pre-resolved from TranscriptProjection.Build
/// - this never re-runs vocabulary/edits/dedup/NameResolver.</summary>
public static class DocxRenderer
{
    // Word page dimensions are in twips (1/1440 inch). A4 = 210x297mm; Letter = 8.5x11in.
    private const int A4WidthTwips = 11906, A4HeightTwips = 16838;
    private const int LetterWidthTwips = 12240, LetterHeightTwips = 15840;
    // Courtroom page geometry (design 2026-08-02 item 6): 1" margins, 0.5" header/footer.
    private const int MarginTwips = 1440, HeaderFooterTwips = 720;
    // Text column sizing: estimated bold-11pt character advance plus label padding, clamped to
    // [1.5", 3.0"] so short labels still get a real gutter and marathon names cannot eat the page
    // (overlong labels overrun one line gracefully; the hanging indent keeps wrapped lines aligned).
    private const int TwipsPerLabelChar = 120, LabelPadTwips = 240;
    private const int MinTextColTwips = 2160, MaxTextColTwips = 4320;

    /// <summary>Always-on continuation trigger (design 2026-08-03 section 8): ~10-11 rendered
    /// lines at 11pt Arial in the text column, so a (cont'd) label lands near the top of
    /// essentially every page. This is what makes the STYLEREF running head reliable.</summary>
    public const int ContinuationMaxChars = 900;

    public static DocxPageSize PageSizeForRegion(RegionInfo region)
        => region.TwoLetterISORegionName is "US" or "CA" ? DocxPageSize.Letter : DocxPageSize.A4;

    public static void Write(Stream output, TranscriptHeader header, SessionTextView meta,
        ExportProvenance provenance, ExportSummary? summary, IReadOnlyList<DisplayRow> rows,
        string timestampsMode, DocxPageSize pageSize, ExportOptions options)
    {
        using var doc = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = mainPart.Document.AppendChild(new Body());

        int textCol = TextColumnTwips(rows, options, timestampsMode, header.StartedAtLocal);
        AddStyles(mainPart, textCol);

        // Metadata header block.
        body.AppendChild(Heading(meta.Title));
        body.AppendChild(MetaLine("App", header.App));
        body.AppendChild(MetaLine("Date", MetadataFormat.DateLine(meta)));
        body.AppendChild(MetaLine("Matter(s)",
            meta.Matters.Count == 0 ? "(none)" : string.Join(", ", meta.Matters)));
        body.AppendChild(MetaLine("Participants",
            meta.Participants.Count == 0 ? "(none)" : string.Join(", ", meta.Participants)));
        body.AppendChild(MetaLine("Medium", meta.Medium));
        if (!string.IsNullOrEmpty(meta.Description)) body.AppendChild(MetaLine("Description", meta.Description));
        body.AppendChild(MetaLine("Transcript version", MetadataFormat.VersionLine(provenance)));
        if (!string.IsNullOrEmpty(provenance.AudioFileName))
            body.AppendChild(MetaLine("Audio", provenance.AudioFileName));
        if (!string.IsNullOrEmpty(provenance.AudioSha256))
            body.AppendChild(MetaLine("Audio SHA-256", provenance.AudioSha256));
        string speakers = MetadataFormat.SpeakersHeard(rows);
        if (speakers.Length > 0) body.AppendChild(MetaLine("Speakers heard", speakers));
        if (provenance.InProgress) body.AppendChild(InProgressLine());
        if (provenance.ExcerptSpan is { } excerptSpan)
        {
            body.AppendChild(MetaLine("Excerpt", excerptSpan));
            body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
                new Run(new RunProperties(new Bold()), MakeText(ExportNotices.ExcerptNotice))));
        }
        if (summary is not null) AppendSummary(body, summary);
        body.AppendChild(DisclaimerLine());
        // Spacer before the turns - suppressed like the rest of the header so line 1 is content.
        body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers())));

        foreach (var row in rows)
        {
            if (row.IsMarker)
            {
                if (options.IncludeMarkers) body.AppendChild(MarkerLine(row.Text, textCol));
                continue;
            }
            // Cadence chunking (design 2026-08-03 section 8): chunk 0 is the normal turn; later
            // chunks are (cont'd) continuation paragraphs that repeat the name so a reader
            // flipping to a mid-turn page still sees who is speaking. maxChars is ALWAYS on
            // (ContinuationMaxChars); the intervalMs trigger stays behind the timestamps checkbox.
            var chunks = TimestampCadence.Chunk(row,
                options.IncludeTimestamps ? options.TimestampIntervalMs : 0, ContinuationMaxChars);
            var label = TurnLabel(row, options, timestampsMode, header.StartedAtLocal);
            body.AppendChild(TurnParagraph(label, chunks[0].Text));
            for (int i = 1; i < chunks.Count; i++)
                body.AppendChild(TurnParagraph(
                    label with
                    {
                        Stamp = options.IncludeTimestamps
                            ? "[" + TimestampFormat.Stamp(chunks[i].StampMs, timestampsMode,
                                header.StartedAtLocal) + "] "
                            : "",
                        Suffix = " (cont'd):",
                    },
                    chunks[i].Text));
        }

        // Per-page footer + locale page size in section properties (sectPr MUST be the last child
        // of body). design 2026-08-03 section 2: {transcript name} at the left margin, "Page N of
        // M" at a right tab on the usable width. The cached "1"/"1" results are the placeholders
        // Word replaces when it paginates. Field instruction text is invariant by construction.
        (int w, int h) = pageSize == DocxPageSize.Letter
            ? (LetterWidthTwips, LetterHeightTwips) : (A4WidthTwips, A4HeightTwips);
        int usableWidth = w - 2 * MarginTwips;
        var footerPart = mainPart.AddNewPart<FooterPart>();
        footerPart.Footer = new Footer(new Paragraph(
            new ParagraphProperties(
                new Tabs(new TabStop { Val = TabStopValues.Right, Position = usableWidth })),
            new Run(MakeText(meta.Title)),
            new Run(new TabChar()),
            new Run(MakeText("Page ")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(MakeText("1")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End }),
            new Run(MakeText(" of ")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" NUMPAGES ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(MakeText("1")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
        string footerId = mainPart.GetIdOfPart(footerPart);

        // Running head (design 2026-08-03 section 3): matter + date at the left margin, the
        // current speaker at a right tab. The speaker is a STYLEREF field, not text we compose -
        // only Word knows where its own page breaks fall. The left half IS composed here, because
        // STYLEREF cannot truncate and a long matter would otherwise collide with the speaker.
        // In-progress export (design 2026-08-03 section 11): a bold notice paragraph is prepended
        // ahead of the matter/date/STYLEREF paragraph, which is otherwise untouched - same right
        // tab stop, same bottom border, same content either way. A time-range excerpt (design
        // 2026-08-04 section 8) prepends a second bold notice paragraph the same way; when a
        // session is both mid-recording and excerpted the two stack, in-progress first.
        var headerPart = mainPart.AddNewPart<HeaderPart>();
        var headerParagraphs = new List<Paragraph>();
        if (provenance.InProgress)
            headerParagraphs.Add(new Paragraph(
                new Run(new RunProperties(new Bold()), MakeText(ExportNotices.InProgressNotice))));
        if (provenance.ExcerptSpan is not null)
            headerParagraphs.Add(new Paragraph(
                new Run(new RunProperties(new Bold()), MakeText(ExportNotices.ExcerptNotice))));
        headerParagraphs.Add(RunningHeadParagraph(header, meta, usableWidth));
        headerPart.Header = new Header(headerParagraphs.ToArray());
        string headerId = mainPart.GetIdOfPart(headerPart);

        // Page 1 carries the metadata block, which names everything the running head would - so
        // the head is suppressed there via TitlePg + an EMPTY first-page header.
        var firstHeaderPart = mainPart.AddNewPart<HeaderPart>();
        firstHeaderPart.Header = new Header(new Paragraph());
        string firstHeaderId = mainPart.GetIdOfPart(firstHeaderPart);

        body.AppendChild(new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = headerId },
            new HeaderReference { Type = HeaderFooterValues.First, Id = firstHeaderId },
            new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
            // Same part id as Default: TitlePg suppresses the page-1 footer unless a First
            // reference exists, and page 1 must still show "Page 1 of N".
            new FooterReference { Type = HeaderFooterValues.First, Id = footerId },
            new PageSize { Width = (UInt32Value)(uint)w, Height = (UInt32Value)(uint)h },
            // Explicit margins make the tab geometry predictable (sectPr schema order: pgSz, pgMar).
            new PageMargin
            {
                Top = MarginTwips, Right = (uint)MarginTwips,
                Bottom = MarginTwips, Left = (uint)MarginTwips,
                Header = (uint)HeaderFooterTwips, Footer = (uint)HeaderFooterTwips, Gutter = 0U,
            },
            // Courtroom line numbers (design 2026-08-03 section 5): every line numbered for
            // page:line citation; restart per page, counting transcript content only (header
            // paragraphs carry SuppressLineNumbers).
            new LineNumberType { CountBy = 1, Restart = LineNumberRestartValues.NewPage },
            new TitlePage()));
    }

    /// <summary>The matter/date/STYLEREF running-head paragraph (design 2026-08-03 section 3),
    /// extracted so the in-progress notice paragraph (design 2026-08-03 section 11) can be
    /// prepended ahead of it without touching its content, right tab stop, or bottom border.</summary>
    private static Paragraph RunningHeadParagraph(TranscriptHeader header, SessionTextView meta, int usableWidth)
        => new(
            new ParagraphProperties(
                // CT_PPrBase child order (final whole-branch review): pBdr(9) precedes tabs(11) in
                // the ECMA-376 sequence. The OpenXml SDK accepts either order and all tests pass
                // either way, but Word can flag the document as corrupt on open. Do NOT reorder to
                // match Microsoft Learn's pPr reference page - it lists children ALPHABETICALLY,
                // not in schema order, and "fixing" this against that page reintroduces the bug.
                new ParagraphBorders(new BottomBorder
                { Val = BorderValues.Single, Size = 4U, Space = 4U, Color = "auto" }),
                new Tabs(new TabStop { Val = TabStopValues.Right, Position = usableWidth })),
            new Run(MakeText(HeaderLeft(header, meta))),
            new Run(new TabChar()),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            // The field argument is the style NAME ("Transcript Speaker"), never the styleId
            // ("TranscriptSpeaker"). Word's field parser only ever sees w:name - the ID is an
            // internal token it never exposes - so an ID argument resolves to nothing and every
            // page from 2 on shows "Error! No text of specified style in document." once Word
            // paginates. Do NOT "tidy" this back to the ID.
            new Run(new FieldCode(" STYLEREF \"Transcript Speaker\" ")
            { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            // Cached result deliberately left empty, unlike the footer's PAGE/NUMPAGES fields two
            // blocks above: STYLEREF has no meaningful placeholder before Word has paginated (it
            // is not a running count like "1"). Word recalculates it during pagination on open and
            // again before printing, so this is blank only in non-repaginating consumers such as a
            // Google Docs import or a Pandoc-class converter that reads cached field results.
            // Caps here too: STYLEREF returns the stored name, and the body shows it in caps via
            // the character style, so the head must match.
            new Run(new RunProperties(new Caps()), MakeText("")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End }));

    /// <summary>The composed left half of the running head: first matter (or the title when the
    /// session is untagged) and the start date. Composed rather than STYLEREF'd because a long
    /// matter must be truncatable - STYLEREF cannot truncate (design 2026-08-03 section 3).</summary>
    private static string HeaderLeft(TranscriptHeader header, SessionTextView meta)
    {
        const int MaxChars = 60;
        string left = meta.Matters.Count > 0 ? meta.Matters[0] : meta.Title;
        if (left.Length > MaxChars)
        {
            // Matter names are free text a user typed - back off one more code unit whenever the
            // slice would land inside a surrogate pair (index 59 on a high surrogate). Cutting a
            // pair leaves an unpaired high surrogate, and XmlWriter throws ArgumentException:
            // Invalid surrogate pair at save time instead of producing a file.
            int cut = MaxChars - 1;
            if (char.IsHighSurrogate(left[cut - 1])) cut--;
            left = left[..cut] + "\u2026";
        }
        return left + " \u00B7 "
            + header.StartedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>O(n) pre-pass (design 2026-08-02 item 6): size the text column off the longest turn
    /// label. Continuation labels now repeat the name plus " (cont'd)" (design 2026-08-03
    /// section 8), so - unlike the old stamp-only continuation - they are no longer guaranteed
    /// narrower than a full label and must be measured too.</summary>
    private static int TextColumnTwips(IReadOnlyList<DisplayRow> rows, ExportOptions options,
        string timestampsMode, DateTimeOffset startedAtLocal)
    {
        int longest = 0;
        foreach (var row in rows)
            if (!row.IsMarker)
            {
                var label = TurnLabel(row, options, timestampsMode, startedAtLocal);
                // A close approximation, not an exact bound: a continuation repeats the base
                // label plus " (cont'd)" (design 2026-08-03 section 8), but TimestampFormat
                // widens the relative stamp from mm:ss to h:mm:ss past the 1h mark
                // (TimestampFormat.cs:15), so a turn that starts under an hour and continues past
                // it yields a label a couple of chars wider than this measurement. Under-reserving
                // by a few twips just overruns one line gracefully (see MinTextColTwips/
                // MaxTextColTwips above) - not worth widening every row's measurement over.
                longest = Math.Max(longest, label.Length + " (cont'd)".Length);
            }
        return Math.Clamp(longest * TwipsPerLabelChar + LabelPadTwips, MinTextColTwips, MaxTextColTwips);
    }

    /// <summary>The three pieces of a turn label, kept separate because only the NAME may carry
    /// the TranscriptSpeaker character style - STYLEREF in the page header returns that run's text
    /// verbatim (design 2026-08-03 section 3). Length is what TextColumnTwips measures.</summary>
    private readonly record struct TurnLabelParts(string Stamp, string Name, string Suffix)
    {
        public int Length => Stamp.Length + Name.Length + Suffix.Length;
    }

    private static TurnLabelParts TurnLabel(DisplayRow row, ExportOptions options, string timestampsMode,
        DateTimeOffset startedAtLocal)
        => new(
            options.IncludeTimestamps
                ? "[" + TimestampFormat.Stamp(row.StartMs, timestampsMode, startedAtLocal) + "] "
                : "",
            row.DisplayName ?? "",
            ":");

    /// <summary>Bold stamp -> styled name -> bold suffix -> tab -> text. The TranscriptTurn style
    /// carries the hanging indent and tab stop, so a recipient can retune the whole document by
    /// editing one style in Word.</summary>
    private static Paragraph TurnParagraph(TurnLabelParts label, string text)
    {
        var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "TranscriptTurn" }));
        if (label.Stamp.Length > 0)
            p.AppendChild(new Run(new RunProperties(new Bold()), MakeText(label.Stamp)));
        if (label.Name.Length > 0)
            p.AppendChild(new Run(
                new RunProperties(new RunStyle { Val = "TranscriptSpeaker" }), MakeText(label.Name)));
        if (label.Suffix.Length > 0)
            p.AppendChild(new Run(new RunProperties(new Bold()), MakeText(label.Suffix)));
        p.AppendChild(new Run(new TabChar()));
        p.AppendChild(new Run(MakeText(text)));
        return p;
    }

    private static Paragraph MarkerLine(string text, int textCol)
        => new(new ParagraphProperties(
                new Indentation { Left = textCol.ToString(CultureInfo.InvariantCulture) }),
            new Run(new RunProperties(new Italic()), MakeText("[" + text + "]")));

    private static void AddStyles(MainDocumentPart mainPart, int textCol)
    {
        string col = textCol.ToString(CultureInfo.InvariantCulture);
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            // Arial pinned at the document default (design 2026-08-03 section 4), NOT on the turn
            // style: with no rFonts and no theme part Word fell back to Times New Roman, and that
            // fallback reached headings, footer, header and line numbers too. Size stays at 11pt -
            // TextColumnTwips' character-advance arithmetic depends on it.
            new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
                new FontSize { Val = "22" }, new FontSizeComplexScript { Val = "22" }))),
            new Style(
                new StyleName { Val = "Transcript Turn" },
                new StyleParagraphProperties(
                    // CT_PPrBase child order (final whole-branch review): the ECMA-376 schema
                    // sequence for these elements is widowControl(6) -> tabs(11) -> spacing(22) ->
                    // ind(23). The SDK accepts any order and all tests pass regardless, but Word can
                    // flag the document as corrupt on open. Do NOT "verify" against Microsoft
                    // Learn's pPr reference page - it lists children ALPHABETICALLY, not in schema
                    // order, and reordering to match it reintroduces the bug.
                    new WidowControl(),
                    new Tabs(new TabStop { Val = TabStopValues.Left, Position = textCol }),
                    // 6pt (120 twentieths of a point) after each turn.
                    new SpacingBetweenLines { After = "120" },
                    new Indentation { Left = col, Hanging = col }))
            { Type = StyleValues.Paragraph, StyleId = "TranscriptTurn" }
            ,
            // Pure CHARACTER style (design 2026-08-03 sections 3-4). Two hard requirements:
            // (1) type=character, never linked - Word's STYLEREF cannot see a linked style applied
            //     to part of a paragraph, and this is applied to a run inside the turn paragraph;
            // (2) Caps as a FORMAT, never an uppercased string - STYLEREF returns the underlying
            //     text, so uppercasing the data would destroy the real name in the document body
            //     to achieve a display effect.
            new Style(
                new StyleName { Val = "Transcript Speaker" },
                new StyleRunProperties(new Bold(), new Caps()))
            { Type = StyleValues.Character, StyleId = "TranscriptSpeaker" });
    }

    private static Text MakeText(string s) => new(s) { Space = SpaceProcessingModeValues.Preserve };
    private static Paragraph Heading(string title)
        => new(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Bold(), new FontSize { Val = "32" }), MakeText(title)));
    private static Paragraph MetaLine(string label, string value)
        => new(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Bold()), MakeText(label + ": ")), new Run(MakeText(value)));
    /// <summary>Bold, above the disclaimer. Page 1 suppresses the running head, so this line is
    /// what covers page 1; the header covers pages 2+ (design 2026-08-03 section 11).</summary>
    private static Paragraph InProgressLine()
        => new(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Bold()), MakeText(ExportNotices.InProgressNotice)));

    /// <summary>The summary section (design 2026-08-04 section 7): heading, the LOCKED
    /// AssistantPrompts.DraftLabel, provenance, an optional stale notice, then the content.
    /// EVERY paragraph suppresses line numbers - Round 1's numbering counts transcript content
    /// only, and a numbered summary would silently renumber the whole transcript.</summary>
    private static void AppendSummary(Body body, ExportSummary summary)
    {
        body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Bold(), new FontSize { Val = "24" }),
                MakeText(ExportNotices.SummaryHeading))));
        body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Italic()), MakeText(AssistantPrompts.DraftLabel))));
        body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
            new Run(new RunProperties(new Italic()), MakeText(summary.ProvenanceLine))));
        if (summary.StaleNotice is { } staleNotice)
            body.AppendChild(new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
                new Run(new RunProperties(new Bold()), MakeText(staleNotice))));
        foreach (var p in SummaryContentParagraphs(summary.ContentMarkdown))
            body.AppendChild(p);
    }

    /// <summary>A deliberately MINIMAL line-level markdown transform. AssistantPrompts prescribes
    /// exactly four "##" headers with bullet bodies, so line-level covers the real output shape.
    /// There is NO inline parsing: "**bold**" stays literal. A half-working inline parser is worse
    /// than none, and this limit is documented rather than left as a mystery.</summary>
    private static IEnumerable<Paragraph> SummaryContentParagraphs(string markdown)
    {
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                yield return new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
                    new Run(new RunProperties(new Bold()), MakeText(line.TrimStart('#').TrimStart())));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal)
                     || line.StartsWith("* ", StringComparison.Ordinal))
            {
                // CT_PPrBase schema order: suppressLineNumbers(8) precedes ind(23). The SDK
                // accepts any order and tests pass; Word calls the file corrupt. Use the XSD,
                // NOT Microsoft Learn's alphabetical pPr page.
                yield return new Paragraph(
                    new ParagraphProperties(
                        new SuppressLineNumbers(),
                        new Indentation { Left = "360", Hanging = "360" }),
                    new Run(MakeText("\u2022 " + line[2..])));
            }
            else
            {
                yield return new Paragraph(new ParagraphProperties(new SuppressLineNumbers()),
                    new Run(MakeText(line)));
            }
        }
    }

    /// <summary>Italic disclaimer closed by a thin 0.5pt rule (design 2026-08-02 item 6) that
    /// separates the unnumbered metadata block from the numbered transcript body.</summary>
    private static Paragraph DisclaimerLine()
        => new(new ParagraphProperties(
                new SuppressLineNumbers(),
                new ParagraphBorders(new BottomBorder
                { Val = BorderValues.Single, Size = 4U, Space = 4U, Color = "auto" })),
            new Run(new RunProperties(new Italic()), MakeText(ExportNotices.Disclaimer)));
}
