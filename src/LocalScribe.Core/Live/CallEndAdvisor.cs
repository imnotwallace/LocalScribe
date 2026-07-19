namespace LocalScribe.Core.Live;

/// <summary>Call-end advisory state machine (design 2026-07-18 section 5.4). Armed at
/// Idle-&gt;Recording with the exes to watch; a watched capture session going inactive starts a 3 s
/// quiet window (a return within the window cancels - Steno-verified: Zoom/Teams/Webex in-call
/// software mute keeps the OS capture stream open, so mute never even produces a Stopped event);
/// ShouldAdvise turns true exactly once per arm when the window elapses. ADVISORY-ONLY by locked
/// rule: this class DECIDES - it never stops, pauses, or pads anything and never writes markers;
/// the App renders a toast whose [Stop recording] is a human click through the normal Stop
/// command. Pure state + explicit timestamps, so every transition is unit-testable; the clock
/// ticks arrive from the same 1.5 s poll that feeds the watcher (two ticks span the window).
/// Single-threaded by contract (UI-thread timer + State subscription).</summary>
public sealed class CallEndAdvisor
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly HashSet<string> _watched = new(StringComparer.Ordinal);   // ExeKey form
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);      // watched exes currently active
    private DateTimeOffset? _quietSince;
    private bool _advised;

    /// <summary>Idle-&gt;Recording. Watch the applied per-process target when there is one; else
    /// (Auto/system mix) the allowlisted apps live on capture endpoints right now. An empty
    /// watched set means no call-end advisory this session - honest silence over guessing.</summary>
    public void Arm(string? perProcessApp, IReadOnlyCollection<string> activeCaptureExes,
        IReadOnlyList<string> allowlist)
    {
        Disarm();
        if (!string.IsNullOrEmpty(perProcessApp))
        {
            _watched.Add(CallDetectionPolicy.ExeKey(perProcessApp));
        }
        else
        {
            foreach (var exe in activeCaptureExes)
                if (allowlist.Any(a => CallDetectionPolicy.ExeKey(a) == CallDetectionPolicy.ExeKey(exe)))
                    _watched.Add(CallDetectionPolicy.ExeKey(exe));
        }
        foreach (var exe in activeCaptureExes)
            if (_watched.Contains(CallDetectionPolicy.ExeKey(exe)))
                _live.Add(CallDetectionPolicy.ExeKey(exe));
    }

    /// <summary>Recording left (stop, fault, finalize) - nothing pending survives.</summary>
    public void Disarm()
    {
        _watched.Clear();
        _live.Clear();
        _quietSince = null;
        _advised = false;
    }

    public void Observe(CallAppActivity a)
    {
        if (_watched.Count == 0 || _advised) return;
        string key = CallDetectionPolicy.ExeKey(a.Exe);
        if (!_watched.Contains(key)) return;
        if (a.Kind == CallAppActivityKind.Started)
        {
            _live.Add(key);
            _quietSince = null;                 // session returned inside the window: cancel
        }
        else
        {
            _live.Remove(key);
            if (_live.Count == 0 && _quietSince is null)
                _quietSince = a.Timestamp;      // ALL watched apps quiet: the window opens
        }
    }

    /// <summary>One-shot per arm: true when a quiet window has lasted the full debounce.</summary>
    public bool ShouldAdvise(DateTimeOffset now)
    {
        if (_advised || _quietSince is not { } quiet) return false;
        if (now - quiet < Debounce) return false;
        _advised = true;
        return true;
    }
}
