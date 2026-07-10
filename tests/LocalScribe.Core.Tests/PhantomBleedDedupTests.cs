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
    public void Remote_outside_the_near_window_is_never_hidden()
    {
        // Retires the pre-2026-07-10 "remotes are never hidden, period" invariant: design
        // section 4 now hides a remote echo of the user's own kept speech on RMS evidence.
        // What remains unconditionally true: a remote far from any local in time is untouchable.
        var local = Seg(TranscriptSource.Local, 0, 1000, 4000, "same words", -10.0);
        var remote = Seg(TranscriptSource.Remote, 1, 10000, 13000, "same words", -40.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, remote }).Count);
    }

    [Fact]
    public void SplitChild_IsNeverSuppressed()
    {
        var remote = new ProjectedSegment(
            TranscriptLine.Segment(1, TranscriptSource.Remote, 1000, 2000, "identical words here", "Them"), "identical words here");
        var localChild = new ProjectedSegment(
            TranscriptLine.Segment(2, TranscriptSource.Local, 1000, 2000, "identical words here", "Me"),
            "identical words here", IsSplitChild: true, PartIndex: 0);

        var kept = new PhantomBleedDedup().Filter(new[] { remote, localChild });
        Assert.Contains(localChild, kept);   // exempt despite matching the remote
    }

    [Fact]
    public void A_distinct_short_local_contained_in_a_longer_remote_is_kept()
    {
        // A genuine local remark whose words happen to be a token-substring of a longer
        // near-simultaneous remote line is NOT a bleed - hiding it would flip attribution.
        //
        // Adversarial finding (2026-07-11 review): this RMS shape (local quieter than remote by
        // >= MinRmsGapDb) ALSO satisfies pass 2's (IsEchoOfLocal) independent
        // containment-symmetric check once pass 1 correctly stops hiding the local - Similarity()
        // is symmetric (Math.Max of two symmetric metrics), so pass 1's old buggy RMS check
        // (lr <= rr - gap) and pass 2's check (|lr - rr| >= gap) are mathematically equivalent
        // whenever local is the quieter side: no RMS values can satisfy one without the other for
        // a symmetrically-containment-matched pair. That is the SEPARATE, pre-existing,
        // deliberately excluded "pass-2 fragment-shadowing" issue (pending a user decision - do
        // not touch IsEchoOfLocal/pass-2 here). Measured: before this fix, Filter() kept only
        // [Remote] (local wrongly hidden by pass 1 - the bug this item fixes); after this fix,
        // Filter() keeps only [Local] (pass 1 correctly stops hiding it, but pass 2 now
        // independently hides the remote via the identical symmetric containment match - the
        // excluded bug, unchanged by this fix either way). So this test asserts the in-scope
        // invariant only: the local fragment itself must never be the side silently swallowed by
        // IsBleedOf's containment swap.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "so the auth changes last night broke prod", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1100, 2400, "the auth changes last night", -30.0);
        var kept = new PhantomBleedDedup().Filter(new[] { remote, local });
        Assert.Contains(kept, s => s.Source == TranscriptSource.Local);
    }

    [Fact]
    public void A_distinct_short_local_contained_in_a_longer_remote_is_kept_with_missing_rms()
    {
        // Same shape on the null-RMS text-only path: containment 1.0 must not clear the
        // 0.975 bar for a fragment - only a container-side (padded echo) local may.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "so the auth changes last night broke prod", null);
        var local = Seg(TranscriptSource.Local, 1, 1100, 2400, "the auth changes last night", null);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Theory]
    [InlineData("Hello, World!", "hello world", 1.0)]
    [InlineData("abcd", "abxd", 0.75)]
    [InlineData("", "", 1.0)]
    public void Normalized_similarity(string a, string b, double expected)
        => Assert.Equal(expected, TextDistance.NormalizedSimilarity(a, b), 2);

    [Fact]
    public void Remote_echo_of_the_users_own_speech_is_hidden()
    {
        // The observed 2026-07-10 session: the user's voice came back on the REMOTE leg (a second
        // device in the meeting), transcribed with extra tokens. Containment + RMS gap hides it.
        var local = Seg(TranscriptSource.Local, 0, 1000, 3000, "So I'm gonna be testing sound.", -20.0);
        var echo = Seg(TranscriptSource.Remote, 1, 1200, 3400,
            "Okay, so I'm gonna be testing sound. I'm testing, I'm testing.", -28.0);
        var kept = new PhantomBleedDedup().Filter(new[] { local, echo });
        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Local, only.Source);
    }

    [Fact]
    public void Remote_with_comparable_energy_is_never_hidden()
    {
        // A genuine remote speaker repeating the user's words has comparable energy - keep it.
        var local = Seg(TranscriptSource.Local, 0, 1000, 3000, "So I'm gonna be testing sound.", -20.0);
        var remote = Seg(TranscriptSource.Remote, 1, 1200, 3400,
            "Okay, so I'm gonna be testing sound. I'm testing, I'm testing.", -21.0);   // 1 dB gap
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, remote }).Count);
    }

    [Fact]
    public void Remote_direction_requires_rms_evidence_no_text_only_fallback()
    {
        // Near-identical (sim ~0.972: above the 0.85 pass-2 bar, below the 0.975 pass-1
        // text-only bar - byte-identical text would be hidden by frozen pass-1 before pass 2
        // ever ran). With no RMS on either side pass 2 must refuse: no text-only fallback
        // in the remote direction.
        var local = Seg(TranscriptSource.Local, 0, 1000, 3000, "I pushed the auth changes last night", null);
        var echo = Seg(TranscriptSource.Remote, 1, 1200, 3400, "I pushed the auth change last night", null);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, echo }).Count);   // both kept
    }

    [Fact]
    public void A_pair_can_never_vanish_entirely()
    {
        // Identical text both legs with the LOCAL quieter: pass 1 hides the local as a bleed of
        // the remote; pass 2 must then NOT also hide the remote (it only checks KEPT locals).
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "I pushed the auth changes last night.", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1150, 4100, "I pushed the auth changes last night.", -31.5);
        var kept = new PhantomBleedDedup().Filter(new[] { remote, local });
        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Remote, only.Source);
    }

    [Fact]
    public void Garbled_echo_pair_stays_visible_documented_limitation()
    {
        // Observed pair 3: no safe text gate catches "hold on to my name" vs "Hold on my mind."
        var local = Seg(TranscriptSource.Local, 0, 8000, 9000, "hold on to my name", -20.0);
        var garbled = Seg(TranscriptSource.Remote, 1, 8100, 9200, "Hold on my mind.", -28.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, garbled }).Count);
    }

    [Fact]
    public void Corrected_remote_echo_is_exempt_from_hiding()
    {
        var local = Seg(TranscriptSource.Local, 0, 1000, 3000, "So I'm gonna be testing sound.", -20.0);
        var echo = Seg(TranscriptSource.Remote, 1, 1200, 3400,
            "Okay, so I'm gonna be testing sound. I'm testing, I'm testing.", -28.0) with { Corrected = true };
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, echo }).Count);
    }

    [Fact]
    public void Corrected_but_organically_kept_local_still_anchors_the_echo_hide()
    {
        // The correction exemption protects a local that pass 1 WOULD have hidden; it does not
        // turn every corrected local into a shield for its echo. This local survives on its own
        // evidence (it is the louder copy), so its corrected flag changes nothing: echo hidden.
        var local = Seg(TranscriptSource.Local, 0, 1000, 3000, "So I'm gonna be testing sound.", -20.0) with { Corrected = true };
        var echo = Seg(TranscriptSource.Remote, 1, 1200, 3400,
            "Okay, so I'm gonna be testing sound. I'm testing, I'm testing.", -28.0);
        var kept = new PhantomBleedDedup().Filter(new[] { local, echo });
        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Local, only.Source);
    }
}
