// tests/LocalScribe.App.Tests/PlaybackClockCorrectorTests.cs
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>
/// Unit tests for <see cref="PlaybackClockCorrector"/> against the on-box probe matrix
/// (probe-verified 2026-07-11): Windows Media Foundation reports a constant per-file forward
/// clock offset after any seek+Play on our (pre-fix) FLAC files, caused by an 8192-byte
/// PADDING metadata block. Paused seeks read back exact; the offset appears within ~300ms of
/// Play and is retained across a subsequent Pause; a fresh paused Seek reads exact again; the
/// NEXT Play re-applies the already-learned offset after a single jumped reading.
/// </summary>
public sealed class PlaybackClockCorrectorTests
{
    [Fact]
    public void Paused_seek_reads_back_exact()
    {
        var c = new PlaybackClockCorrector();
        c.OnLoaded();
        c.OnSeek(targetMs: 34202, wallMs: 0);

        // Paused readback: not playing, no jump learned yet -> exact passthrough.
        long corrected = c.Correct(rawMs: 34202, wallMs: 0, isPlaying: false);

        Assert.Equal(34202, corrected);
    }

    [Fact]
    public void Play_after_seek_jumps_then_corrects_to_the_wallclock_estimate_then_the_learned_offset()
    {
        // Measured trace (probe 2026-07-11, leg with true offset 3753ms): seek to 34202ms,
        // paused-exact, then Play. MF's raw readback jumps to 38278 / 38443 / 38606 at a
        // 150ms tick cadence. Wall-clock stamps below are chosen so the SECOND (confirming)
        // jumped reading's error lands on the probe's measured constant offset (3753ms) -
        // i.e. this reproduces the exact numbers quoted in the design.
        var c = new PlaybackClockCorrector();
        c.OnLoaded();
        c.OnSeek(targetMs: 34202, wallMs: 0);
        c.OnPlay(currentPositionMs: 34202, wallMs: 0);

        // First jumped reading: err (3738) >= 1500ms and no offset is learned yet, so the
        // corrector must NOT hand back the raw jumped value - it returns the wall-clock
        // estimate (baseTarget + elapsed wall time) instead.
        long first = c.Correct(rawMs: 38278, wallMs: 338, isPlaying: true);
        Assert.Equal(34540, first);                    // 34202 + 338, NOT anywhere near 38278
        Assert.True(first < 35000);

        // Second reading confirms the jump (its error is within 300ms of the first error) ->
        // offset (3753ms) is learned and applied from here on.
        long second = c.Correct(rawMs: 38443, wallMs: 488, isPlaying: true);
        Assert.Equal(34690, second);
        Assert.True(second < 35000);

        // Third reading: now applying unconditionally (raw - learnedOffset).
        long third = c.Correct(rawMs: 38606, wallMs: 638, isPlaying: true);
        Assert.Equal(34853, third);
        Assert.True(third < 35000);
    }

    [Fact]
    public void Offset_persists_across_pause()
    {
        var c = new PlaybackClockCorrector();
        c.OnLoaded();
        c.OnSeek(targetMs: 34202, wallMs: 0);
        c.OnPlay(currentPositionMs: 34202, wallMs: 0);
        c.Correct(38278, 338, true);                    // pending
        c.Correct(38443, 488, true);                    // confirms + learns offset 3753

        c.OnPause();
        // Probe: pause after playing retains the offset - a static raw readback of 38972
        // (paused) still corrects down by the learned 3753ms offset.
        long corrected = c.Correct(rawMs: 38972, wallMs: 999_999, isPlaying: false);

        Assert.Equal(35219, corrected);                 // 38972 - 3753, exact
    }

    [Fact]
    public void New_paused_seek_resets_to_exact_and_next_play_applies_the_learned_offset_after_one_reading()
    {
        var c = new PlaybackClockCorrector();
        c.OnLoaded();
        c.OnSeek(34202, 0);
        c.OnPlay(34202, 0);
        c.Correct(38278, 338, true);                     // pending
        c.Correct(38443, 488, true);                     // learns offset 3753, now applying

        // A brand-new paused seek must read back exact again (offset stops APPLYING, even
        // though it stays learned for next time).
        c.OnSeek(targetMs: 10_000, wallMs: 10_000);
        long pausedReadback = c.Correct(rawMs: 10_000, wallMs: 10_000, isPlaying: false);
        Assert.Equal(10_000, pausedReadback);

        // Resuming play at the new position: the FIRST jumped reading already matches the
        // previously learned offset (3753, within the 500ms confirm band), so it corrects
        // immediately - no second reading needed this time.
        c.OnPlay(currentPositionMs: 10_000, wallMs: 10_000);
        long resumed = c.Correct(rawMs: 10_000 + 3753 + 60, wallMs: 10_060, isPlaying: true);
        Assert.Equal(10_060, resumed);                   // corrected back to the true position
    }

    [Fact]
    public void Padding_free_file_never_jumps_and_never_learns_an_offset()
    {
        // A WAV (or a padding-free FLAC, post fix-A) trace: raw tracks wall-clock elapsed time
        // closely, no >=1500ms error ever appears, so Correct must always pass the raw value
        // through untouched and never latch an offset.
        var c = new PlaybackClockCorrector();
        c.OnLoaded();
        c.OnSeek(5_000, 0);
        c.OnPlay(5_000, 0);

        Assert.Equal(5_150, c.Correct(5_150, 150, true));
        Assert.Equal(5_300, c.Correct(5_300, 300, true));
        Assert.Equal(5_460, c.Correct(5_460, 460, true));   // small noise (10ms), still passthrough
    }

    [Fact]
    public void Replay_seek_to_zero_stops_applying_until_one_jumped_reading_reconfirms()
    {
        // Review Important 1: the VM's replay-from-end branch rewinds via a real seek-to-0, and
        // the probe only establishes that a PAUSED SEEK reads back exact - never that a
        // seek-to-0+Play re-manifests the learned offset. So after learning, OnSeek(0)+OnPlay(0)
        // must stop APPLYING: a non-jumped raw passes through untouched, and a single jumped
        // reading within 500ms of the learned offset re-applies it (fast path).
        var c = new PlaybackClockCorrector();
        c.OnLoaded();
        c.OnSeek(10_000, 0);
        c.OnPlay(10_000, 0);
        c.Correct(12_100, 100, true);                   // err 2000, pending
        c.Correct(12_200, 200, true);                   // confirms, learns 2000, applying

        c.OnSeek(0, 1_000);                              // replay rewind
        c.OnPlay(0, 1_000);

        // Exact readback after the rewind: must NOT be corrected down by the stale offset
        // (raw 150 - 2000 would clamp to 0 and pin the display at 00:00).
        Assert.Equal(150, c.Correct(rawMs: 150, wallMs: 1_100, isPlaying: true));

        // One jumped reading whose error (2100) is within 500ms of the learned 2000 offset
        // re-applies it immediately - replay loses nothing if the offset does re-manifest.
        Assert.Equal(300, c.Correct(rawMs: 2_300, wallMs: 1_200, isPlaying: true));
    }

    [Fact]
    public void Jump_threshold_boundary_err_1499_passes_through_and_1500_learns()
    {
        // Review Important 2a: pin JumpThresholdMs at exactly 1500 - err 1499 is poll noise
        // (raw passthrough, nothing pended), err 1500 on two consecutive readings learns.
        var c = new PlaybackClockCorrector();
        c.OnLoaded();
        c.OnSeek(10_000, 0);
        c.OnPlay(10_000, 0);

        Assert.Equal(11_599, c.Correct(11_599, 100, true));   // err 1499: passthrough

        Assert.Equal(10_200, c.Correct(11_700, 200, true));   // err 1500: pending, wall estimate
        Assert.Equal(10_300, c.Correct(11_800, 300, true));   // err 1500 again: learns, applies

        // Now applying (raw - 1500) unconditionally. Had the corrector NOT learned at the
        // previous reading, this err-1600 reading would confirm 1600 instead and return 10400.
        Assert.Equal(10_500, c.Correct(12_000, 400, true));
    }

    [Fact]
    public void Consecutive_confirm_boundary_301_apart_does_not_learn_but_299_apart_does()
    {
        // Review Important 2b: pin ConsecutiveConfirmToleranceMs at exactly 300.
        // Two consecutive jumped errors 301ms apart must NOT latch an offset; the second
        // becomes the new pending, and a third reading consistent with IT learns.
        var far = new PlaybackClockCorrector();
        far.OnLoaded();
        far.OnSeek(10_000, 0);
        far.OnPlay(10_000, 0);
        Assert.Equal(10_100, far.Correct(12_100, 100, true));  // err 2000: pending
        Assert.Equal(10_200, far.Correct(12_501, 200, true));  // err 2301, 301 apart: NOT learned
        // err 2551 is 250 from the new pending (2301) -> learns 2551 -> returns expected 10300.
        // Had 2301 been (wrongly) learned above, this would return 12851 - 2301 = 10550.
        Assert.Equal(10_300, far.Correct(12_851, 300, true));

        // Errors 299ms apart DO latch the offset on the second reading.
        var near = new PlaybackClockCorrector();
        near.OnLoaded();
        near.OnSeek(10_000, 0);
        near.OnPlay(10_000, 0);
        Assert.Equal(10_100, near.Correct(12_100, 100, true)); // err 2000: pending
        Assert.Equal(10_200, near.Correct(12_499, 200, true)); // err 2299, 299 apart: learns
        // Applying (raw - 2299). Had it NOT learned, err 2551 (252 from pending 2299) would
        // confirm 2551 instead and return the expected 10300.
        Assert.Equal(10_552, near.Correct(12_851, 300, true));
    }

    [Fact]
    public void Learned_match_boundary_501_off_reconfirms_but_499_off_fast_reapplies()
    {
        // Review Important 2c: pin LearnedMatchToleranceMs at exactly 500. After a seek stops
        // the correction applying, a jumped error 501ms away from the learned offset must go
        // back through the two-reading confirmation; 499ms away fast-reapplies immediately.
        var far = new PlaybackClockCorrector();
        far.OnLoaded();
        far.OnSeek(10_000, 0);
        far.OnPlay(10_000, 0);
        far.Correct(12_100, 100, true);                  // pending
        far.Correct(12_200, 200, true);                  // learns 2000, applying
        far.OnSeek(20_000, 1_000);                        // stops applying, keeps learned
        far.OnPlay(20_000, 1_000);
        // err 2501 is 501 from the learned 2000: NOT the fast path - first reading returns the
        // wall-clock estimate (a fast reapply would have returned 22601 - 2000 = 20601).
        Assert.Equal(20_100, far.Correct(22_601, 1_100, true));

        var near = new PlaybackClockCorrector();
        near.OnLoaded();
        near.OnSeek(10_000, 0);
        near.OnPlay(10_000, 0);
        near.Correct(12_100, 100, true);
        near.Correct(12_200, 200, true);                 // learns 2000, applying
        near.OnSeek(20_000, 1_000);
        near.OnPlay(20_000, 1_000);
        // err 2499 is 499 from the learned 2000: fast-reapplies on this single reading,
        // correcting by the LEARNED 2000 (not the fresh 2499) -> 22599 - 2000 = 20599.
        Assert.Equal(20_599, near.Correct(22_599, 1_100, true));
    }

    [Fact]
    public void Correct_never_returns_a_negative_position()
    {
        var c = new PlaybackClockCorrector();
        c.OnLoaded();
        c.OnSeek(0, 0);
        c.OnPlay(0, 0);
        c.Correct(4_000, 200, true);                        // pending (err 3800 >= 1500)
        c.Correct(4_050, 350, true);                         // confirms, learns ~3800-3850, applies

        long corrected = c.Correct(rawMs: 100, wallMs: 351, isPlaying: true);   // raw below the offset

        Assert.True(corrected >= 0);
    }
}
