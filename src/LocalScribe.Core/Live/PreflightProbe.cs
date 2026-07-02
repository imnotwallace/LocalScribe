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
}
