namespace LocalScribe.Core.Live;

/// <summary>Pure state machine behind the per-leg frame-arrival watchdog (Tier 1B design
/// 2026-08-05, T1-4a): detects a capture leg that has stopped producing ANY frames at all.
///
/// THE HOLE IT FILLS: SilentLegMonitor detects sustained NO SPEECH, but it is driven from
/// PeakObserved, which LiveSourcePipeline emits from inside the frame loop - one call per arriving
/// frame. Zero frames therefore means zero calls, so a WASAPI stream that dies mid-session (device
/// unplugged, endpoint invalidated, driver reset) is structurally invisible to it: the leg simply
/// stops calling OnData, AlignedAudioWriter silence-fills the gap on the next frame that never
/// comes, and PadToMs makes the file look the right length at Stop. Nothing anywhere says the
/// microphone died forty minutes ago.
///
/// Extracted rather than inlined for the reason SilentLegMonitor records at its own :11-19:
/// FakeCaptureSource replays every frame SYNCHRONOUSLY inside Start() and FakeClock never advances
/// by itself, so an end-to-end controller test can neither starve a leg nor time one out. This class
/// is unit-tested directly; SessionController owns all threading and does the locking (frames arrive
/// on the capture thread, Tick comes from the App's 150 ms DispatcherTimer). NOT thread-safe on its
/// own - by contract, exactly like SilentLegMonitor.</summary>
public sealed class FrameArrivalWatchdog
{
    private readonly long _graceMs;
    private long _lastFrameMs;
    private bool _stalled;

    /// <param name="graceMs">How long a leg may produce NO frames before it is called stalled.</param>
    /// <param name="startMs">Seeded to the session clock at leg start, BEFORE the first frame - so a
    /// leg that never produces a single frame still measures from a real timestamp, not from 0.</param>
    public FrameArrivalWatchdog(long graceMs, long startMs)
    {
        _graceMs = graceMs;
        _lastFrameMs = startMs;
    }

    /// <summary>True while this leg is currently flagged as stalled.</summary>
    public bool Stalled => _stalled;

    /// <summary>Call for every frame observed on this leg while Recording. Returns true EXACTLY
    /// once - when this frame clears a raised stall - so the caller can report the recovery; false
    /// on every ordinary frame.</summary>
    public bool OnFrame(long nowMs)
    {
        _lastFrameMs = nowMs;
        if (!_stalled) return false;
        _stalled = false;
        return true;
    }

    /// <summary>External tick (the App's existing 150 ms DispatcherTimer, via
    /// SessionController.PollCaptureHealth - never a Timer inside Core, per the CallActivityWatcher
    /// rule). Returns true EXACTLY once, the first tick on which the grace window has been exceeded
    /// with no frame since; false forever after while still stalled, so the caller reports once and
    /// attempts one restart rather than hammering.</summary>
    public bool Tick(long nowMs)
    {
        if (_stalled) return false;
        if (nowMs - _lastFrameMs <= _graceMs) return false;   // a negative delta is <= grace: never trips
        _stalled = true;
        return true;
    }

    /// <summary>Re-arms from now and drops any flag - called wherever a FRESH leg starts. Every one
    /// of those sites is wired by Task 8: StartAsync's seed, ResumeAsync, SetLocalMuteAsync's UNMUTE
    /// branch, SetRemoteCaptureAsync's live re-target, and the watchdog's own restart. Returns
    /// whether the leg was flagged at reset time, so the caller can raise the matching "recovered"
    /// notification: every stall report must have exactly one clear, or a banner driven off the pair
    /// stays stuck showing a dead leg that has already been replaced (the SilentLegMonitor.Reset
    /// rule).</summary>
    public bool Reset(long nowMs)
    {
        bool wasStalled = _stalled;
        _lastFrameMs = nowMs;
        _stalled = false;
        return wasStalled;
    }

    /// <summary>Collapses the remaining grace so the NEXT Tick trips - for a source that has
    /// self-reported its death (ICaptureHealthObservable), where there is nothing left to wait for.
    /// Raises nothing itself: Tick stays the single place a stall is DECIDED, so the report and the
    /// restart keep running on the controller's tick under its own lock.
    ///
    /// Inert while already stalled, and that is the whole point. REJECTED: reusing
    /// Reset(now - grace - 1), which was this plan's first draft - Reset CLEARS _stalled, so a leg
    /// already flagged and already reported would be silently un-flagged, then reported and MARKED a
    /// SECOND time on the next tick, and its matching CaptureRecovered would be swallowed. A
    /// duplicate "audio device changed" marker in an evidentiary transcript is a false record of a
    /// second outage that never happened.</summary>
    public void ForceStale(long nowMs)
    {
        if (_stalled) return;
        long rewound = nowMs - _graceMs - 1;
        if (rewound < _lastFrameMs) _lastFrameMs = rewound;
    }
}
