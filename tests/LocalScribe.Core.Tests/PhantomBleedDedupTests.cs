using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class PhantomBleedDedupTests
{
    private static ProjectedSegment Seg(TranscriptSource src, int seq, long startMs, long endMs,
        string text, double? rmsDb) =>
        new(TranscriptLine.Segment(seq, src, startMs, endMs, text,
            src == TranscriptSource.Local ? "Me" : "Them", rmsDb: rmsDb), text);

    [Fact]
    public void Quieter_matching_local_copy_is_hidden()
    {
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "I pushed the auth changes last night.", -18.0);
        var bleed = Seg(TranscriptSource.Local, 1, 1150, 4100, "I pushed the auth changes last night", -31.5);
        var kept = new PhantomBleedDedup().Filter(new[] { remote, bleed });

        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Remote, only.Source);
    }

    [Fact]
    public void Comparable_energy_is_never_suppressed_even_with_matching_text()
    {
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "yes exactly", -20.0);
        var local = Seg(TranscriptSource.Local, 1, 1100, 3900, "yes exactly", -21.0);   // 1 dB gap only
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Distinct_words_are_never_suppressed()
    {
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "the hearing moved to Thursday", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1000, 4000, "okay I will tell the client", -30.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Far_apart_in_time_is_never_suppressed()
    {
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2000, "same words here", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 10_000, 11_000, "same words here", -30.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Missing_rms_uses_the_stricter_text_only_bar()
    {
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "I pushed the auth changes last night.", null);
        var nearMatch = Seg(TranscriptSource.Local, 1, 1100, 4000, "I pushed the auth change last night", null);
        var exact = Seg(TranscriptSource.Local, 2, 1100, 4000, "I pushed the auth changes last night.", null);

        var kept = new PhantomBleedDedup().Filter(new[] { remote, nearMatch, exact });
        // exact copy (sim 1.0) hidden; near-match (~0.9 < 0.92) kept without energy evidence
        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, s => s.Seq == 2);
        Assert.Contains(kept, s => s.Seq == 1);
    }

    [Fact]
    public void Remote_segments_are_never_hidden()
    {
        var loud = Seg(TranscriptSource.Local, 0, 1000, 4000, "same words", -10.0);
        var quiet = Seg(TranscriptSource.Remote, 1, 1000, 4000, "same words", -40.0);
        var kept = new PhantomBleedDedup().Filter(new[] { loud, quiet });
        Assert.Contains(kept, s => s.Source == TranscriptSource.Remote);   // quiet remote survives
    }

    [Theory]
    [InlineData("Hello, World!", "hello world", 1.0)]
    [InlineData("abcd", "abxd", 0.75)]
    [InlineData("", "", 1.0)]
    public void Normalized_similarity(string a, string b, double expected)
        => Assert.Equal(expected, TextDistance.NormalizedSimilarity(a, b), 2);
}
