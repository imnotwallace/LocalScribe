using System.Text.RegularExpressions;

namespace LocalScribe.Core.Assistant;

/// <summary>Backend-provenance truth source (design 2026-07-23 section 5): llama.cpp's
/// authoritative "load_tensors: offloaded N/M layers to GPU" log line, captured by the helper
/// during load. Line PRESENT with N==M &gt; 0 -&gt; a real GPU run; partial or absent -&gt; CPU.
/// Pure text -&gt; value; the helper owns capture, this owns interpretation. Tested against the
/// two real captured logs in tests/Fixtures (a CUDA 37/37 run and a 100% CPU run).</summary>
public static partial class LlamaOffloadLog
{
    [GeneratedRegex(@"load_tensors: offloaded (\d+)/(\d+) layers to GPU")]
    private static partial Regex OffloadLine();

    /// <summary>Last match wins: the buffer is reset per load, but a single load can in
    /// principle log more than once - the final line is the settled assignment.</summary>
    public static (int Offloaded, int Total)? FindOffload(string logText)
    {
        Match? last = null;
        foreach (Match m in OffloadLine().Matches(logText)) last = m;
        return last is null ? null
            : (int.Parse(last.Groups[1].Value), int.Parse(last.Groups[2].Value));
    }

    /// <summary>The design section 5 rule: Backend = "cuda" iff offloaded == total &amp;&amp; total &gt; 0.
    /// A mixed run is NOT a GPU run.</summary>
    public static bool IsFullGpu((int Offloaded, int Total)? offload)
        => offload is { } o && o.Offloaded == o.Total && o.Total > 0;
}
