// src/LocalScribe.App/Services/PlaybackClockCorrector.cs
namespace LocalScribe.App.Services;

/// <summary>
/// Corrects a constant per-file forward clock offset that Windows Media Foundation
/// (System.Windows.Media.MediaPlayer) reports after any seek+Play on our (pre-fix) FLAC
/// files: probe-verified 2026-07-11, +3753ms on one leg / +2741ms on the other, offset ~=
/// metadataBytes / avgAudioByteRate, caused by the 8192-byte PADDING metadata block
/// CUETools.Codecs.FLAKE's FlakeWriter emitted by default. The audio itself is not offset -
/// only the reported Position clock is wrong. <see cref="LocalScribe.Core.Audio.FlacAudioSink"/>
/// no longer writes that PADDING block, so this corrector exists purely to keep pre-existing
/// evidentiary session audio (which LocalScribe NEVER rewrites) displaying a correct on-screen
/// clock. Read-side only, WPF-free, and a pure state machine so it is fully unit-testable.
///
/// Probe behavior matrix this class implements:
///  - Paused seek -> readback EXACT.
///  - Play after seek -> readback jumps by the constant offset within ~300ms and stays offset
///    while playing.
///  - Pause after playing -> the offset is RETAINED (a static paused readback still corrects).
///  - A new paused Seek -> exact again.
///  - Seek while playing -> offset persists.
///  - Play from 0 without any seek -> no offset (never observed on this box).
/// </summary>
public sealed class PlaybackClockCorrector
{
    /// <summary>Minimum raw-vs-expected error, in ms, before a reading is treated as "jumped"
    /// rather than normal poll noise.</summary>
    private const long JumpThresholdMs = 1500;

    /// <summary>How close a new error has to land to an already-learned offset to be accepted
    /// as "the same, known offset" (single-reading fast path).</summary>
    private const long LearnedMatchToleranceMs = 500;

    /// <summary>How close two CONSECUTIVE unconfirmed jumped errors have to be to each other to
    /// be accepted as a genuine, stable offset (first-time learning).</summary>
    private const long ConsecutiveConfirmToleranceMs = 300;

    private long? _learnedOffsetMs;
    private bool _applying;
    private long _baseTargetMs;
    private long _baseWallMs;

    /// <summary>The error observed on the most recent unconfirmed jumped reading, awaited a
    /// second consecutive confirming reading before an offset is learned for the first time.
    /// Reset whenever a non-jumped reading is seen, or the base target/wall changes.</summary>
    private long? _pendingFirstErrMs;

    /// <summary>Resets all state for a newly loaded file. Nothing is carried over between
    /// files - each file's padding (and therefore offset, if any) is independent.</summary>
    public void OnLoaded()
    {
        _learnedOffsetMs = null;
        _applying = false;
        _baseTargetMs = 0;
        _baseWallMs = 0;
        _pendingFirstErrMs = null;
    }

    /// <summary>A paused seek lands MF at an exact readback (probe-verified), so correction
    /// stops applying until a new jump is (re-)observed. The learned offset itself is NOT
    /// forgotten - it is a property of the file, not of any one seek - so the next Play can
    /// reuse it immediately (see <see cref="Correct"/>'s single-reading fast path).</summary>
    public void OnSeek(long targetMs, long wallMs)
    {
        _baseTargetMs = targetMs;
        _baseWallMs = wallMs;
        _applying = false;
        _pendingFirstErrMs = null;
    }

    /// <summary>Re-anchors the wall-clock estimate to the position play resumed from. Does NOT
    /// touch <see cref="_applying"/> or the learned offset: resuming a simple pause/play toggle
    /// (no intervening seek) must keep applying an already-active correction without waiting to
    /// re-detect it.</summary>
    public void OnPlay(long currentPositionMs, long wallMs)
    {
        _baseTargetMs = currentPositionMs;
        _baseWallMs = wallMs;
        _pendingFirstErrMs = null;   // any pending confirmation was relative to the old base
    }

    /// <summary>Probe: pausing does not un-learn or stop applying the offset - a static
    /// readback taken while paused keeps correcting by the same amount.</summary>
    public void OnPause()
    {
        // Intentional no-op: _applying and _learnedOffsetMs are retained as-is.
    }

    /// <summary>Returns the corrected position for a raw player readback.</summary>
    /// <param name="rawMs">The player's raw, possibly MF-offset, Position.</param>
    /// <param name="wallMs">Caller's wall clock at the moment of this reading (matches the
    /// clock passed to <see cref="OnSeek"/>/<see cref="OnPlay"/>).</param>
    /// <param name="isPlaying">Whether the player is currently playing.</param>
    public long Correct(long rawMs, long wallMs, bool isPlaying)
    {
        if (_applying)
            return Clamp(rawMs - (_learnedOffsetMs ?? 0));

        if (!isPlaying)
        {
            // Paused and not (yet) applying a correction: MF reads back exact here.
            return rawMs;
        }

        long expected = _baseTargetMs + (wallMs - _baseWallMs);
        long err = rawMs - expected;

        if (err < JumpThresholdMs)
        {
            _pendingFirstErrMs = null;   // normal/noise reading; forget any pending confirmation
            return rawMs;
        }

        // A jumped reading. If we already know this file's offset, a single reading whose
        // error matches it closely enough is proof enough - no need to re-confirm.
        if (_learnedOffsetMs is long learned && Math.Abs(err - learned) <= LearnedMatchToleranceMs)
        {
            _applying = true;
            _pendingFirstErrMs = null;
            return Clamp(rawMs - learned);
        }

        // No offset learned yet: require two consecutive jumped readings whose errors agree,
        // to rule out a one-off glitch, before latching a brand-new offset.
        if (_pendingFirstErrMs is long first && Math.Abs(err - first) <= ConsecutiveConfirmToleranceMs)
        {
            _learnedOffsetMs = err;
            _applying = true;
            _pendingFirstErrMs = null;
            return Clamp(rawMs - err);
        }

        // First (unconfirmed) jumped reading: don't hand back the jumped raw value - estimate
        // from the wall clock instead, and wait for the next reading to confirm.
        _pendingFirstErrMs = err;
        return Clamp(expected);
    }

    private static long Clamp(long ms) => Math.Max(0, ms);
}
