using System.Globalization;
namespace LocalScribe.Core.Live;

/// <summary>Disk-space policy for live recording (Tier 1B design 2026-08-05, T1-4c). Pure: the
/// DriveInfo call is a delegate seam on SessionController, so this class holds no IO and no clock.
///
/// WHY A HARD REFUSAL AND NOT A WARNING. Filling the disk mid-call faults the audio write loop; the
/// remainder of the recording is then lost whichever way we handle it, and (because
/// AlignedAudioWriter.PadToMs silence-fills to the stop instant) the file still looks the right
/// length. Losing a call at minute 40 is strictly worse than refusing it at minute 0, when the user
/// can free space and start again. REJECTED: warn-only, which converts a preventable refusal into an
/// unrecoverable evidentiary loss.
///
/// WHY 2 GiB. Retained audio is 16 kHz mono 16-bit per leg: 32 kB/s raw, two legs = 64 kB/s, so a
/// WAV session costs ~230 MB/hour and a FLAC one roughly half that (speech compresses ~50%). 2 GiB
/// is therefore about 9 hours of the WORST case (two-leg WAV) - comfortably past any single call,
/// with room for the transcript, the projections and Windows itself. The 1 GiB warn floor leaves
/// about 4 hours of that worst case after the banner appears, which is enough time to act without
/// nagging on a normally-full laptop.</summary>
public sealed class DiskSpaceGuard
{
    public const long DefaultStartFloorBytes = 2L * 1024 * 1024 * 1024;
    public const long DefaultWarnFloorBytes = 1L * 1024 * 1024 * 1024;

    private readonly long _warnFloorBytes;
    private bool _warned;

    public DiskSpaceGuard(long warnFloorBytes) => _warnFloorBytes = warnFloorBytes;

    /// <summary>A user-facing refusal reason, or null to permit the recording. A null
    /// <paramref name="freeBytes"/> means the probe could not measure (UNC path, unmapped root, a
    /// DriveInfo throw) and ALWAYS permits: refusing on a guess would block the primary use case,
    /// and the mid-session warning plus Task 9's audio-write marker still cover the real failure.</summary>
    public static string? RefusalFor(long? freeBytes, long floorBytes)
    {
        if (freeBytes is not { } free || free >= floorBytes) return null;
        return string.Format(CultureInfo.InvariantCulture,
            "Not enough free disk space to record: {0} MB free, {1} MB needed. "
            + "Free some space on the drive holding your LocalScribe folder and start again.",
            free / (1024 * 1024), floorBytes / (1024 * 1024));
    }

    /// <summary>Mid-session poll. Returns true EXACTLY once per crossing from "enough" to "low", so
    /// the caller marks and warns once rather than on every tick. Recovering above the floor
    /// re-arms it: a second dip is a new fact. An unmeasurable reading changes nothing at all -
    /// a failed probe must never look like a recovery.</summary>
    public bool OnPoll(long? freeBytes)
    {
        if (freeBytes is not { } free) return false;
        if (free >= _warnFloorBytes) { _warned = false; return false; }
        if (_warned) return false;
        _warned = true;
        return true;
    }
}
