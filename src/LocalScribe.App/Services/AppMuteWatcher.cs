using System;

namespace LocalScribe.App.Services;

/// <summary>Polling lifecycle around <see cref="IAppMuteSignalSource"/> (design 2026-07-11 section
/// 2.2). Privacy rule: ZERO UIA activity while not Recording - Poll() returns before touching the
/// source at all when isRecording() is false, resetting the last reading to Unknown (and raising
/// once) if it was not already Unknown. While Recording, the source is read inside a try/catch as
/// belt-and-braces over the seam's own fail-open contract (a source implementation must never
/// throw, but a hiccup here must still never affect recording). ReadingChanged fires only when the
/// reading differs from the previous one. The 2 s DispatcherTimer that calls Poll() lives in
/// composition (Task 8), NOT here - tests drive Poll() directly.</summary>
public sealed class AppMuteWatcher
{
    private readonly IAppMuteSignalSource _source;
    private readonly Func<bool> _isRecording;
    private AppMuteReading _last = new(AppMuteState.Unknown, null);

    public AppMuteWatcher(IAppMuteSignalSource source, Func<bool> isRecording)
    {
        _source = source;
        _isRecording = isRecording;
    }

    public event Action<AppMuteReading>? ReadingChanged;

    /// <summary>Fires at the END of every Poll() WHILE RECORDING, even when the reading did not
    /// change (unlike <see cref="ReadingChanged"/>). Task 8's VM subscribes this to drive
    /// debounce-EXPIRY re-evaluation: a mismatch must persist several polls before it banners, so an
    /// unchanged Muted reading still needs a later tick to cross the threshold. Deliberately NOT
    /// raised in the not-recording branch (zero UIA/advisory activity while idle).</summary>
    public event Action? Polled;

    public AppMuteReading Last => _last;

    public void Poll()
    {
        if (!_isRecording())
        {
            if (_last.State != AppMuteState.Unknown)
            {
                _last = new(AppMuteState.Unknown, null);
                ReadingChanged?.Invoke(_last);
            }
            return;
        }

        AppMuteReading reading;
        try
        {
            reading = _source.Read();
        }
        catch
        {
            reading = new(AppMuteState.Unknown, null);
        }

        if (!reading.Equals(_last))
        {
            _last = reading;
            ReadingChanged?.Invoke(_last);
        }
        // Single exit point of the recording branch (Task 7 left this deliberately): fires every
        // poll so the VM can re-evaluate debounce expiry on an unchanged reading (Task 8).
        Polled?.Invoke();
    }
}
