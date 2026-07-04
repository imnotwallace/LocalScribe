namespace LocalScribe.App.Services;

/// <summary>Tracks open per-session windows by session id (design section 2/5, Stage 4 plan
/// prerequisite; extended in Stage 5 Task 9's review fix to allow more than one window kind per
/// session). Two jobs: let a session delete close every window open for that session FIRST so no
/// audio file handle blocks the recycle (CloseAllFor), and let newly opened read views cascade-
/// offset from the count of already-open sessions (OpenCount). A session id maps to a LIST of
/// close actions - e.g. a ReadViewWindow AND a SplitSpeakersWindow can both be open for the same
/// session at once, and both must be tracked so CloseAllFor releases both file handles. Registering
/// a second window for an id does NOT evict the first. IsOpen/OpenCount key off distinct session
/// ids, unchanged from the original one-window contract. WPF-free (stores close Actions only);
/// mutated from the UI thread, but guarded anyway so it is safe to observe from anywhere.</summary>
public sealed class WindowRegistry
{
    private readonly Dictionary<string, List<Action>> _open = new(StringComparer.Ordinal);

    /// <summary>Number of distinct sessions with at least one tracked window open (for cascade
    /// placement).</summary>
    public int OpenCount { get { lock (_open) return _open.Count; } }

    /// <summary>Track an open window for the session, storing the action that closes it. A
    /// second Register for the same session id APPENDS rather than replacing - a session can have
    /// both a read view and a Split-speakers dialog open at once, and neither may evict the
    /// other's close action.</summary>
    public void Register(string sessionId, Action close)
    {
        lock (_open)
        {
            if (_open.TryGetValue(sessionId, out var list)) list.Add(close);
            else _open[sessionId] = [close];
        }
    }

    /// <summary>Stop tracking ONE window - called by that window's own Closed handler, passing
    /// the same delegate it registered with (reference identity picks the right entry out of
    /// possibly several for this session). Does NOT invoke the close action (the window is
    /// already closing). Leaves any other windows still tracked for the same session untouched.</summary>
    public void Unregister(string sessionId, Action close)
    {
        lock (_open)
        {
            if (!_open.TryGetValue(sessionId, out var list)) return;
            list.Remove(close);
            if (list.Count == 0) _open.Remove(sessionId);
        }
    }

    /// <summary>Close-before-delete: invoke every tracked close action for the session (if any),
    /// then untrack all of them. Removing before invoking makes each window's own Closed-
    /// >Unregister a harmless no-op and prevents re-entrancy.</summary>
    public void CloseAllFor(string sessionId)
    {
        List<Action>? actions;
        lock (_open) { if (!_open.Remove(sessionId, out actions)) return; }
        foreach (var close in actions) close();
    }

    /// <summary>True when at least one window for the session is currently open.</summary>
    public bool IsOpen(string sessionId) { lock (_open) return _open.ContainsKey(sessionId); }
}
