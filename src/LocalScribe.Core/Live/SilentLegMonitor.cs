namespace LocalScribe.Core.Live;

/// <summary>Pure state machine behind the sustained-no-speech "silent leg" monitor (Fix #2: a
/// wrong capture endpoint - e.g. a Communications-default device that isn't the user's real mic -
/// records a noise floor with no speech; VAD correctly emits zero segments, so the Start-time peak
/// probe (which only catches DEAD/all-zero endpoints) never sees a problem and the user gets a
/// silent recording with no warning). Tracks, for one leg, when the last real transcript segment
/// arrived; each per-frame peak checks whether the grace window has elapsed since then with no
/// segment - if so it flags exactly once, and a later segment clears it exactly once.
///
/// Extracted as its own clock/thread-free class (rather than inlined per-source fields on
/// SessionController, per the task-7 brief) because SessionController's real fakes
/// (FakeCaptureSource) emit every frame of a leg SYNCHRONOUSLY inside StartLeg - by the time
/// StartAsync returns there are no more peaks left to drive, so an end-to-end test can never
/// observe a peak firing AFTER a clock advance. Testing this boundary condition in isolation
/// removes that race entirely. SessionController still owns all threading: PeakObserved runs on
/// the capture thread, segment inserts run on the writer-loop thread, so the controller guards
/// its calls into an instance of this class with a lock (see SessionController._silentGate) -
/// this class itself is NOT thread-safe on its own.</summary>
public sealed class SilentLegMonitor
{
    private readonly long _graceMs;
    private long _lastSegmentMs;
    private bool _flagged;

    public SilentLegMonitor(long graceMs, long startMs)
    {
        _graceMs = graceMs;
        _lastSegmentMs = startMs;
    }

    /// <summary>True while this leg is currently flagged as silent (no segment has cleared it
    /// yet).</summary>
    public bool Flagged => _flagged;

    /// <summary>Call on every PeakObserved for this leg while Recording. Returns true exactly
    /// once - the moment the grace window is first exceeded with no segment since - so the
    /// caller can raise SilentLegDetected; every subsequent peak while still flagged returns
    /// false (persistent, not re-raised).</summary>
    public bool OnPeak(long nowMs)
    {
        if (_flagged) return false;
        if (nowMs - _lastSegmentMs <= _graceMs) return false;
        _flagged = true;
        return true;
    }

    /// <summary>Call whenever a transcript segment for this leg is inserted. Always advances the
    /// last-segment clock (the healthy path just keeps the window from ever elapsing). Returns
    /// true exactly once - when this segment clears an existing flag - so the caller can raise
    /// SilentLegCleared; returns false on every segment that arrives before any flag is raised.</summary>
    public bool OnSegment(long nowMs)
    {
        _lastSegmentMs = nowMs;
        if (!_flagged) return false;
        _flagged = false;
        return true;
    }

    /// <summary>Resume (spec: the grace window restarts on a fresh leg): reseeds the last-segment
    /// time to now and clears any flag, exactly as if the leg had just started.</summary>
    public void Reset(long nowMs)
    {
        _lastSegmentMs = nowMs;
        _flagged = false;
    }
}
