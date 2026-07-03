namespace LocalScribe.App.Services;

/// <summary>Per-user single-instance guard (design 7.2): Stage 4 makes matters.json
/// read-modify-write load-bearing, and two instances could double-record. The first instance
/// owns a named mutex and parks a background thread on a named activate event; a second
/// instance signals that event (SignalExisting) and exits. The activate callback runs ON THE
/// BACKGROUND WAIT THREAD - callers pass a dispatch-wrapped action (e.g. Dispatcher.BeginInvoke)
/// so the callback itself never blocks this thread.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly ManualResetEvent _stop = new(initialState: false);
    private readonly Thread _waiter;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivateRequested)
    {
        (_mutex, _activate) = (mutex, activate);
        _waiter = new Thread(() =>
        {
            // WaitAny returns the LOWEST signaled index on ties, so _stop (index 0) always
            // wins over a pending activate and Dispose deterministically ends the loop.
            while (WaitHandle.WaitAny([_stop, _activate]) == 1)
                onActivateRequested();
        })
        { IsBackground = true, Name = "LocalScribe.SingleInstance" };
        _waiter.Start();
    }

    /// <summary>Null when another instance already holds the name. "Local\" scopes the kernel
    /// objects to this logon session, so two Windows users on one machine can each run
    /// LocalScribe without fighting over the guard.</summary>
    public static SingleInstance? TryAcquire(string name, Action onActivateRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivateRequested);
        var mutex = new Mutex(initiallyOwned: true, "Local\\" + name, out bool createdNew);
        if (!createdNew) { mutex.Dispose(); return null; }
        var activate = new EventWaitHandle(initialState: false, EventResetMode.AutoReset,
            "Local\\" + name + "-activate");
        return new SingleInstance(mutex, activate, onActivateRequested);
    }

    /// <summary>Second-instance path: ping the holder's activate event so it raises its main
    /// window. False when no holder exists (or the event is inaccessible) - the caller exits
    /// either way, so failure here must never throw.</summary>
    public static bool SignalExisting(string name)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting("Local\\" + name + "-activate", out var handle))
                return false;
            using (handle) handle.Set();
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _stop.Set();
        _waiter.Join();   // callback is dispatch-wrapped (non-blocking), so this cannot hang
        // ReleaseMutex is thread-affine (ownership was taken on TryAcquire's caller thread);
        // if Dispose runs on another thread it throws - swallowed, because closing the last
        // handle below destroys the named kernel object regardless, which is exactly what
        // makes a re-acquire succeed.
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        _activate.Dispose();
        _stop.Dispose();
    }
}
