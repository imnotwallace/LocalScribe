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
