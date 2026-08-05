namespace LocalScribe.Core.Tests;

/// <summary>Source-text pins for ProcessLoopbackCapture's diagnostic-throttling logic (review
/// round 2, 2026-08-05, on Tier 1 plan A Task 9). ProcessLoopbackCapture activates real WASAPI and
/// cannot be driven in a unit test - PumpLoop and DrainPackets are reachable only through Start(),
/// which blocks on real hardware (see CaptureDiagnosticsTests' class doc comment for the same
/// constraint). A text assertion on the actual source is the only guard available for this
/// class's control flow, the same convention
/// AssistantPublishLayoutTests.Guard_script_lists_every_required_path_verbatim uses for
/// tools/verify-assistant-publish.ps1. If one of these fails after a refactor, re-point the pin -
/// do not delete it and do not delete the throttling it guards.</summary>
public sealed class ProcessLoopbackCaptureSourceTests
{
    private static string Source()
    {
        string? repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx"))) { repo = d.FullName; break; }
        Assert.NotNull(repo);
        return File.ReadAllText(Path.Combine(
            repo!, "src", "LocalScribe.Core", "Audio", "ProcessLoopbackCapture.cs"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    [Fact]
    public void Activation_line_only_logs_when_the_format_actually_changed()
    {
        // I-2 fix round 2 (review round 2, 2026-08-05): round 1's unconditional
        // Diag("activated: " + ActivationInfo) reintroduced the flood I-3 exists to close, through
        // a different door - ActivateAndInitialize also runs on every pump-loop RE-activation, so a
        // persistent post-activation fault re-emitted this line roughly once a second. This pins
        // the fix: log on the first activation (the cache starts null), or when ActivationInfo
        // differs from the last value actually logged - and stays silent otherwise.
        string src = Source();
        Assert.Contains("private string? _lastLoggedActivationInfo;", src);
        Assert.Contains("if (_lastLoggedActivationInfo != ActivationInfo)", src);
        Assert.Contains("_lastLoggedActivationInfo = ActivationInfo;", src);

        // The guard must actually wrap the Diag call (not just exist somewhere else in the file) -
        // and the cache-update must happen INSIDE the guard, or every activation would still log.
        int guardIndex = src.IndexOf("if (_lastLoggedActivationInfo != ActivationInfo)", StringComparison.Ordinal);
        int diagIndex = src.IndexOf("Diag(\"activated: \" + ActivationInfo);", StringComparison.Ordinal);
        int cacheIndex = src.IndexOf("_lastLoggedActivationInfo = ActivationInfo;", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0 && diagIndex > guardIndex && diagIndex - guardIndex < 300,
            "the activated: Diag call must be inside the format-changed guard");
        Assert.True(cacheIndex > diagIndex && cacheIndex - diagIndex < 100,
            "the cache must be updated right after logging, inside the same guard");
    }

    [Fact]
    public void Discontinuity_throttle_resets_on_a_clean_packet_and_on_reconnect()
    {
        // I-3 fix round 2 (review round 2, 2026-08-05): round 1's counter never reset, so after the
        // very first-ever discontinuity in the object's life, a later, genuinely NEW, isolated
        // episode only logged if the cumulative lifetime total happened to land on a multiple of
        // 6000 - a rare discontinuity late in a long session could go completely unlogged.
        // Resetting to 0 on a clean packet (so the next discontinuity is a first occurrence again)
        // and on reconnect (DropClient, belt-and-braces) fixes this; only a SUSTAINED single
        // episode is still sampled at 1-in-6000.
        string src = Source();
        // Exactly 3 writes to the field: the sustained-episode increment (DrainPackets), the
        // clean-packet reset (DrainPackets), and the reconnect reset (DropClient). Pinning the
        // COUNT, not just presence, is what would have caught round 1 (which had only the
        // increment and no reset at all).
        Assert.Equal(1, Occurrences(src, "_discontinuityCount++;"));
        Assert.Equal(2, Occurrences(src, "_discontinuityCount = 0;"));
    }
}
