// tests/LocalScribe.Core.Tests/DocxRendererTests.cs
using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
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

    [Fact]
    public void Renders_metadata_disclaimer_turns_footer_and_a4_pagesize()
    {
        byte[] bytes = Render("relative", "PRIVILEGED & CONFIDENTIAL", DocxPageSize.A4, new DocxOptions());
        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var main = doc.MainDocumentPart!;
        string text = main.Document!.Body!.InnerText;

        Assert.Contains("Weekly Sync", text);
        Assert.Contains("Participants: Sam (Local), Bob (Remote)", text);
        Assert.Contains("Matter(s): Acme (2026-014)", text);
        Assert.Contains(DocxRenderer.Disclaimer, text);
        Assert.Contains("[00:01] Sam: ", text);
        Assert.Contains("Morning everyone.", text);
        Assert.Contains("[audio device changed]", text);

        Assert.Equal("PRIVILEGED & CONFIDENTIAL", main.FooterParts.Single().Footer!.InnerText);
        var pageSize = main.Document.Body!.GetFirstChild<SectionProperties>()!.GetFirstChild<PageSize>()!;
        Assert.Equal(11906u, pageSize.Width!.Value);          // A4 width in twips
    }

    [Fact]
    public void Toggles_off_omit_timestamps_and_markers_letter_pagesize()
    {
        byte[] bytes = Render("relative", "F", DocxPageSize.Letter,
            new DocxOptions { IncludeTimestamps = false, IncludeMarkers = false });
        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        string text = doc.MainDocumentPart!.Document!.Body!.InnerText;

        Assert.DoesNotContain("[00:01]", text);
        Assert.DoesNotContain("audio device changed", text);
        Assert.Contains("Sam: ", text);                       // turn label present, no stamp
        var pageSize = doc.MainDocumentPart.Document.Body!.GetFirstChild<SectionProperties>()!
            .GetFirstChild<PageSize>()!;
        Assert.Equal(12240u, pageSize.Width!.Value);          // Letter width in twips
    }

    [Fact]
    public void PageSizeForRegion_maps_US_CA_to_letter_else_A4()
    {
        Assert.Equal(DocxPageSize.Letter, DocxRenderer.PageSizeForRegion(new RegionInfo("US")));
        Assert.Equal(DocxPageSize.Letter, DocxRenderer.PageSizeForRegion(new RegionInfo("CA")));
        Assert.Equal(DocxPageSize.A4, DocxRenderer.PageSizeForRegion(new RegionInfo("GB")));
        Assert.Equal(DocxPageSize.A4, DocxRenderer.PageSizeForRegion(new RegionInfo("SG")));
    }
}
