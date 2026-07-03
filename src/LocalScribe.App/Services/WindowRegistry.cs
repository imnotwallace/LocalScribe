namespace LocalScribe.App.Services;

/// <summary>Tracks open read-view windows by session id (design section 2/5, Stage 4 plan
/// prerequisite). Two jobs: let a session delete close its read view(s) FIRST so no audio
/// file handle blocks the recycle (CloseAllFor), and let newly opened read views cascade-offset
/// from the count of already-open windows (OpenCount). One window per session - IsOpen gates
/// activate-existing versus open-new. WPF-free (stores close Actions only); mutated from the UI
/// thread, but guarded anyway so it is safe to observe from anywhere.</summary>
public sealed class WindowRegistry
{
    private readonly Dictionary<string, Action> _open = new(StringComparer.Ordinal);

    /// <summary>Number of read-view windows currently open (for cascade placement).</summary>
    public int OpenCount { get { lock (_open) return _open.Count; } }

    /// <summary>Track an open read view for the session, storing the action that closes it.
    /// A second Register for the same session replaces the first (there is only ever one window
    /// per session - opening again activates the existing one).</summary>
    public void Register(string sessionId, Action close)
    { lock (_open) _open[sessionId] = close; }

    /// <summary>Stop tracking - called by a window's own Closing handler. Does NOT invoke the
    /// close action (the window is already closing).</summary>
    public void Unregister(string sessionId)
    { lock (_open) _open.Remove(sessionId); }

    /// <summary>Close-before-delete: invoke the tracked close action for the session (if any),
    /// then untrack. Removing before invoking makes the window's own Closing->Unregister a
    /// harmless no-op and prevents re-entrancy.</summary>
    public void CloseAllFor(string sessionId)
    {
        Action? close;
        lock (_open) { if (!_open.Remove(sessionId, out close)) return; }
        close();
    }

    /// <summary>True when a read view for the session is currently open.</summary>
    public bool IsOpen(string sessionId) { lock (_open) return _open.ContainsKey(sessionId); }
}
