using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Search.Semantic;

public sealed class SemanticChunkerTests
{
    private static RowSegment Seg(int seq, string text, long startMs = 0, long endMs = 1000,
        int partIndex = 0)
        => new(seq, TranscriptSource.Local, startMs, endMs, text, text,
            IsCorrected: false, IsPinned: false, IsSplitChild: false, PartIndex: partIndex);

    private static DisplayRow Row(string? speaker, params RowSegment[] segs) => new()
    { IsMarker = false, DisplayName = speaker, Segments = segs, Text = "" };

    private static DisplayRow Marker() => new() { IsMarker = true, Text = "marker" };

    [Fact]
    public void Short_transcript_becomes_one_chunk_with_speaker_prefixes()
    {
        var chunks = SemanticChunker.Chunk(
            [Row("Alice", Seg(0, "hello there", 0, 900)),
             Row("Bob", Seg(1, "hi Alice", 1000, 1900))]);
        var c = Assert.Single(chunks);
        Assert.Equal(0, c.StartSeq);
        Assert.Equal(0, c.StartMs);
        Assert.Equal(1, c.EndSeq);
        Assert.Equal(1900, c.EndMs);
        Assert.Contains("Alice: hello there", c.Text);
        Assert.Contains("Bob: hi Alice", c.Text);
    }

    [Fact]
    public void Speaker_prefix_appears_only_on_speaker_change_within_a_chunk()
    {
        var c = Assert.Single(SemanticChunker.Chunk(
            [Row("Alice", Seg(0, "first"), Seg(1, "second"))]));
        Assert.Equal(1, c.Text.Split("Alice:").Length - 1);   // one prefix, not two
    }

    [Fact]
    public void Packing_splits_at_target_and_overlaps_one_segment()
    {
        string body = new string('x', 400);
        var chunks = SemanticChunker.Chunk(
            [Row("A", Seg(0, body), Seg(1, body), Seg(2, body))]);
        Assert.Equal(2, chunks.Count);
        // chunk 0 = segs 0..1 (800 chars fits before the third breaks the 700 target after seg 0? no:
        // 400 fits, +400 = 800 > 700 -> chunk 0 = seg 0 only... see math note below)
        // Deterministic assertion instead of narrating the math:
        Assert.Equal(0, chunks[0].StartSeq);
        // one-segment overlap: the next chunk STARTS at the previous chunk's last segment
        Assert.Equal(chunks[0].EndSeq, chunks[1].StartSeq);
        Assert.Equal(2, chunks[^1].EndSeq);                    // tail is covered
    }

    [Fact]
    public void Single_oversized_segment_becomes_its_own_chunk_untruncated()
    {
        string huge = new string('y', 3000);
        var chunks = SemanticChunker.Chunk([Row("A", Seg(0, huge), Seg(1, "tail"))]);
        Assert.Contains(huge, chunks[0].Text);
        Assert.Equal(0, chunks[0].StartSeq);
        Assert.Equal(0, chunks[0].EndSeq);                     // alone in its chunk
    }

    [Fact]
    public void Markers_and_empty_segments_are_excluded()
    {
        var chunks = SemanticChunker.Chunk(
            [Marker(), Row("A", Seg(0, "  "), Seg(1, "real content")), Marker()]);
        var c = Assert.Single(chunks);
        Assert.Equal(1, c.StartSeq);
        Assert.DoesNotContain("marker", c.Text);
    }

    [Fact]
    public void Empty_rows_produce_no_chunks()
    {
        Assert.Empty(SemanticChunker.Chunk([]));
        Assert.Empty(SemanticChunker.Chunk([Marker()]));
    }

    [Fact]
    public void Split_children_keep_their_part_index_anchor()
    {
        var chunks = SemanticChunker.Chunk(
            [Row("A", Seg(5, "part two text", 2000, 3000, partIndex: 1))]);
        var c = Assert.Single(chunks);
        Assert.Equal(5, c.StartSeq);
        Assert.Equal(1, c.StartPartIndex);
        Assert.Equal(2000, c.StartMs);
    }

    // ---- IMPORTANT 6 (whole-branch review, 2026-08-11) -----------------------------------------

    private static RowSegment Inserted(string id, string text, long startMs)
        => new(Seq: 0, TranscriptSource.System, startMs, startMs, text, text,
            IsCorrected: false, IsPinned: false, InsertionId: id);

    /// <summary>StartSeq/EndSeq are the Related-discussion click-through anchor and the range
    /// lexical hits are deduped against. A human-authored segment carries a placeholder Seq of 0
    /// and LEADS any merged row it joins, so reading first.Seq anchored the chunk at seq 0 - the
    /// top of the transcript - and widened the dedup range to swallow machine seq 0.</summary>
    [Fact]
    public void An_inserted_segment_leading_a_row_never_becomes_the_chunk_anchor()
    {
        var chunks = SemanticChunker.Chunk(
            [Row("Alice", Inserted("i000001", "human line", 4000), Seg(7, "machine line", 4000, 5000))]);

        var c = Assert.Single(chunks);
        Assert.Equal(7, c.StartSeq);
        Assert.Equal(7, c.EndSeq);
        Assert.Equal(4000, c.StartMs);            // the span itself is unchanged
        Assert.Contains("human line", c.Text);    // ... and the human's words are still embedded
    }

    [Fact]
    public void An_inserted_segment_trailing_a_row_never_becomes_the_end_anchor()
    {
        var chunks = SemanticChunker.Chunk(
            [Row("Alice", Seg(7, "machine line", 4000, 5000), Inserted("i000001", "human line", 5000))]);

        var c = Assert.Single(chunks);
        Assert.Equal(7, c.StartSeq);
        Assert.Equal(7, c.EndSeq);
    }

    /// <summary>An all-human chunk has no machine seq to anchor on. -1 covers no lexical seq, and
    /// the click-through's seq &lt; 0 gate opens the read view without a false scroll.</summary>
    [Fact]
    public void A_chunk_of_only_inserted_lines_anchors_at_minus_one()
    {
        var c = Assert.Single(SemanticChunker.Chunk(
            [Row("Unattributed", Inserted("i000001", "human line", 4000))]));

        Assert.Equal(-1, c.StartSeq);
        Assert.Equal(-1, c.EndSeq);
        Assert.Equal(0, c.StartPartIndex);
        Assert.Contains("human line", c.Text);
    }
}
