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
        // Pass 1 refuses via the containment direction guard (the hidden local is the shorter
        // side); pass 2 refuses via the time-coverage guard (2026-07-11 user decision): the
        // local anchor covers only 1300/3000 = 43% of the remote's span, under
        // EchoTimeCoverageMin - an echo of the same sound would be nearly coextensive.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "so the auth changes last night broke prod", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1100, 2400, "the auth changes last night", -30.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void A_louder_local_fragment_can_never_shadow_a_longer_remote_line()
    {
        // The review panel's executed Critical scenario, now closed by the time-coverage
        // guard (user decision 2026-07-11): the genuine remote line survives; only the
        // true bleed copy (identical text, quieter, coextensive) is hidden.
        var r = Seg(TranscriptSource.Remote, 0, 1000, 4000, "I pushed the auth changes last night", -18.0);
        var bleed = Seg(TranscriptSource.Local, 1, 1150, 4100, "I pushed the auth changes last night", -31.5);
        var fragment = Seg(TranscriptSource.Local, 2, 1200, 2500, "the auth changes last night", -10.0);
        var kept = new PhantomBleedDedup().Filter(new[] { r, bleed, fragment });
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, s => s.Source == TranscriptSource.Remote);              // full line survives
        Assert.DoesNotContain(kept, s => ReferenceEquals(s, bleed));                  // true bleed hidden
    }

    [Fact]
    public void A_louder_remote_fragment_can_never_shadow_a_longer_local_line()
    {
        // Mirror of the panel scenario in pass 1: a genuine long LOCAL utterance must not
        // be hidden because a louder remote interjection repeats some of its words -
        // the fragment covers only 43% of the local's span (an echo would be coextensive).
        var local = Seg(TranscriptSource.Local, 0, 1000, 4000, "so the auth changes last night broke prod", -30.0);
        var remoteFragment = Seg(TranscriptSource.Remote, 1, 1200, 2500, "the auth changes last night", -18.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, remoteFragment }).Count);
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

    [Fact]
    public void Floor_short_genuine_reply_survives_pass1_despite_qualifying_rms_gap()
    {
        // Steno-round design 2026-07-18 section 2: whole-string similarity has no length floor, so
        // a genuine brief reply coextensive with a similar short remote line was hidden whenever
        // the 3 dB gap held (identical text, sim 1.0, local 13.5 dB quieter here). Normalized
        // "yes exactly" = 11 chars / 2 tokens - below BOTH floors (12 chars / 3 tokens) - so
        // pass 1 must never auto-suppress it, regardless of similarity or RMS evidence.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2200, "Yes, exactly.", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1050, 2250, "Yes, exactly.", -31.5);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Floor_short_genuine_reply_survives_pass1_missing_rms_identical_text()
    {
        // Missing-RMS path: identical text (sim 1.0) clears the 0.975 text-only bar, so before
        // the floor this pair lost the local copy with NO energy evidence at all. Below either
        // floor -> never auto-suppressed on the text-only path either.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2200, "Yes, exactly.", null);
        var local = Seg(TranscriptSource.Local, 1, 1050, 2250, "Yes, exactly.", null);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Floor_boundary_at_exactly_12_chars_3_tokens_pass1_still_suppresses()
    {
        // Boundary semantics mirror the containment floor's strict less-than (< 12 || < 3):
        // normalized "call me back" is EXACTLY 12 chars and 3 tokens - at-or-above both floors -
        // so it stays ELIGIBLE and the quieter coextensive copy is still hidden as a true bleed.
        // Pins today's behavior; guards the floor against drifting to at-or-below (<=) semantics.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2500, "Call me back.", -18.0);
        var bleed = Seg(TranscriptSource.Local, 1, 1050, 2550, "Call me back.", -31.5);
        var kept = new PhantomBleedDedup().Filter(new[] { remote, bleed });
        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Remote, only.Source);
    }

    [Fact]
    public void Floor_boundary_11_chars_3_tokens_is_exempt_in_pass1()
    {
        // Normalized "yes ok sure" = 11 chars / 3 tokens: the token count meets its floor but the
        // char count sits ONE below its own - below EITHER floor exempts, so both copies stay.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2200, "Yes, OK, sure.", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1050, 2250, "Yes, OK, sure.", -31.5);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Floor_boundary_18_chars_2_tokens_is_exempt_in_pass1()
    {
        // Normalized "absolutely correct" = 18 chars / 2 tokens: chars clear their floor but the
        // token count sits one below its own - below EITHER floor exempts, so both copies stay.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2600, "Absolutely correct.", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1050, 2650, "Absolutely correct.", -31.5);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Floor_measures_normalized_text_not_raw()
    {
        // Raw "No, no, no!!!" is 13 chars / 3 whitespace-separated words - past both floors if
        // measured raw - but normalizes to "no no no" (8 chars / 3 tokens). The guard must
        // measure the NORMALIZED text (design 2026-07-18 section 2), so the char floor exempts it.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2200, "No, no, no!!!", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1050, 2250, "No, no, no!!!", -31.5);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Floor_short_genuine_remote_reply_survives_pass2_despite_qualifying_rms_gap()
    {
        // Pass 2 (the echo-of-own-voice direction) had the same missing floor: a genuine short
        // remote reply repeating the user's short line, 8 dB apart and coextensive, was hidden.
        // Normalized "yes exactly" = 11 chars / 2 tokens - below both floors - so the remote must
        // never be auto-suppressed. (The louder local is not a pass-1 bleed - the RMS direction
        // is wrong - so it anchors pass 2; only the floor stands between the remote and a hide.)
        var local = Seg(TranscriptSource.Local, 0, 1000, 2200, "Yes, exactly.", -20.0);
        var remote = Seg(TranscriptSource.Remote, 1, 1050, 2250, "Yes, exactly.", -28.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, remote }).Count);
    }

    [Fact]
    public void Floor_short_pair_missing_rms_survives_pass2_identical_text()
    {
        // With no RMS on either side pass 2 already refuses (no text-only fallback in the remote
        // direction - existing locked behavior). Pinned here for the short-pair shape so BOTH
        // defenses - the RMS requirement and the new floor - stand between an identical short
        // pair and a remote-side hide. (Pass 1 keeps the local via Task 1's floor.)
        var local = Seg(TranscriptSource.Local, 0, 1000, 2200, "Yes, exactly.", null);
        var remote = Seg(TranscriptSource.Remote, 1, 1050, 2250, "Yes, exactly.", null);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, remote }).Count);
    }

    [Fact]
    public void Floor_boundary_at_exactly_12_chars_3_tokens_pass2_still_suppresses()
    {
        // Same boundary as pass 1: normalized "call me back" is exactly 12 chars / 3 tokens, so
        // the remote copy stays ELIGIBLE and the coextensive quieter remote echo is still hidden
        // on RMS evidence. Pins strict less-than semantics on the pass-2 side too.
        var local = Seg(TranscriptSource.Local, 0, 1000, 2500, "Call me back.", -20.0);
        var echo = Seg(TranscriptSource.Remote, 1, 1050, 2550, "Call me back.", -28.0);
        var kept = new PhantomBleedDedup().Filter(new[] { local, echo });
        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Local, only.Source);
    }
}
