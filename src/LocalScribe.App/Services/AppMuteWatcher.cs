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
    }
}
