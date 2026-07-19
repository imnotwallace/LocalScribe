namespace LocalScribe.Core.Live;

/// <summary>The kind of capture-session transition <see cref="CallActivityWatcher"/> observed.</summary>
public enum CallAppActivityKind { Started, Stopped }

/// <summary>One capture-endpoint audio-session transition (design 2026-07-18 section 5.1): an app
/// began (Started) or ceased (Stopped) actively recording from a capture endpoint. Exe is the
/// EXTENSIONLESS process image (Process.ProcessName via the scanner - "CiscoCollabHost", not
/// "CiscoCollabHost.exe"); Timestamp is the poll tick that observed the change, from the injected
/// TimeProvider (so the call-end debounce math is deterministic in tests).</summary>
public sealed record CallAppActivity(string Exe, int Pid, CallAppActivityKind Kind, DateTimeOffset Timestamp);

/// <summary>Poll-and-diff over ACTIVE capture-endpoint audio sessions (design 2026-07-18 section
/// 5.1 - the Windows analog of Steno's mic-monitor poll-diff, in-process, no helper needed). The
/// WASAPI walk hides behind the injected IAudioSessionScanner seam (production:
/// WasapiSessionScanner over DataFlow.Capture), so the poller/diff logic is fully unit-tested
/// with fakes. Poll() is driven EXTERNALLY - the App's 1.5 s DispatcherTimer, the AppMuteWatcher
/// lifecycle pattern; tests call it directly. FAIL-OPEN by locked rule: a scanner error traces,
/// skips the tick, keeps the previous baseline (never fabricating Stopped events, which could
/// fire a false call-end advisory), and can never affect capture. ADVISORY-ONLY consumer
/// contract: subscribers may only surface offers - nothing downstream of Activity may
/// start/stop/pause capture or write markers. Single-threaded by contract (UI-thread timer).</summary>
public sealed class CallActivityWatcher
{
    private readonly IAudioSessionScanner _scanner;
    private readonly TimeProvider _time;
    private Dictionary<uint, string> _previous = new();

    public CallActivityWatcher(IAudioSessionScanner scanner, TimeProvider time)
        => (_scanner, _time) = (scanner, time);

    public event Action<CallAppActivity>? Activity;

    /// <summary>Distinct exe images in the last successful scan - the call-end advisor's arming
    /// input (which allowlisted apps were live when recording started).</summary>
    public IReadOnlyCollection<string> ActiveExes
        => _previous.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public void Poll()
    {
        IReadOnlyList<AudioSessionInfo> scanned;
        try { scanned = _scanner.Scan(); }
        catch (Exception ex)
        {
            // Fail-open (TrayMuteSignalSource's contract): log + skip. The next successful poll
            // diffs against the PRE-error baseline, so a transient COM hiccup never looks like
            // every call ending at once.
            System.Diagnostics.Trace.WriteLine($"call-detect scan skipped: {ex.Message}");
            return;
        }
        var now = _time.GetUtcNow();
        var current = new Dictionary<uint, string>();
        foreach (var s in scanned) current[s.Pid] = s.ProcessName;
        foreach (var (pid, exe) in current)
            if (!_previous.ContainsKey(pid))
                Activity?.Invoke(new CallAppActivity(exe, (int)pid, CallAppActivityKind.Started, now));
        foreach (var (pid, exe) in _previous)
            if (!current.ContainsKey(pid))
                Activity?.Invoke(new CallAppActivity(exe, (int)pid, CallAppActivityKind.Stopped, now));
        _previous = current;
    }

    /// <summary>Clears the diff baseline (master toggle flipped off - polling stops while
    /// disabled). Re-enabling then re-reports the then-active sessions as fresh Starts (the
    /// policy's cooldown dedups offers) instead of diffing against a stale world.</summary>
    public void Reset() => _previous = new();
}
