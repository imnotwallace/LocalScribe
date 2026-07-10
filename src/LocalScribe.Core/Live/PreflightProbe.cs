using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Live;

/// <summary>Spec 12.3 pre-flight peak probe: run a source for ~1 s and report the peak
/// amplitude so a silent endpoint (all-zeros loopback, muted/dead mic) is caught BEFORE an
/// un-repeatable call is recorded. Warn-only: the caller raises SILENT_SOURCE (spec 8.2) and
/// proceeds. Starts/stops the source; the caller owns disposal.</summary>
public static class PreflightProbe
{
    /// <summary>About -80 dBFS. A dead endpoint delivers exact zeros; real room noise on a
    /// live mic sits well above this. Conservative on purpose - false silence warnings before
    /// a legal call would train the user to ignore the warning.</summary>
    public const float SilencePeakThreshold = 1e-4f;

    public static async Task<float> MeasurePeakAsync(ICaptureSource source, TimeSpan window, CancellationToken ct)
    {
        float peak = 0f;
        void OnFrame(AudioFrame f)
        {
            for (int i = 0; i < f.Samples.Length; i++)
            {
                float a = Math.Abs(f.Samples[i]);
                if (a > peak) peak = a;       // benign race: monotonic max, torn floats impossible on x64
            }
        }

        source.FrameAvailable += OnFrame;
        bool started = false;
        try
        {
            source.Start();
            started = true;
            await Task.Delay(window, ct);
        }
        finally
        {
            if (started) source.Stop();       // a cancelled probe must not leave the source running
            source.FrameAvailable -= OnFrame;
        }
        return peak;
    }

    /// <summary>Accumulates the peak of a leg's first <paramref name="graceMs"/> of REAL captured
    /// audio (fed from SessionController's per-frame PeakObserved), so a dead/all-zeros endpoint is
    /// caught without a pre-capture throwaway probe delaying capture. Not thread-safe on its own;
    /// SessionController serializes feeds under its silent-gate. Returns true from Feed exactly once,
    /// when the window first closes, iff the window stayed below SilencePeakThreshold.</summary>
    public sealed class StartPeakWindow
    {
        private readonly int _graceMs;
        private long _startMs = -1;
        private float _peak;
        private bool _decided;

        public StartPeakWindow(int graceMs) => _graceMs = graceMs;

        /// <summary>Feed one frame's peak at session-clock nowMs. Returns true the first time the
        /// grace window elapses AND the accumulated peak never reached speech level (silent leg).</summary>
        public bool Feed(float peak, long nowMs)
        {
            if (_decided) return false;
            if (_startMs < 0) _startMs = nowMs;
            if (peak > _peak) _peak = peak;
            if (nowMs - _startMs < _graceMs) return false;
            _decided = true;
            return _peak < SilencePeakThreshold;
        }
    }
}
