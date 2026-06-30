using System.Diagnostics;
namespace LocalScribe.Core.Audio;

public interface IClock { long ElapsedMs { get; } }

/// <summary>Production clock: monotonic ms since construction (QPC-backed via Stopwatch).</summary>
public sealed class StopwatchClock : IClock
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public long ElapsedMs => _sw.ElapsedMilliseconds;
}

/// <summary>Test double: caller sets the time.</summary>
public sealed class FakeClock : IClock { public long ElapsedMs { get; set; } }
