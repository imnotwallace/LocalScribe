namespace LocalScribe.App.Services;

public enum AppMuteBannerKind { None, AppMutedButRecording, AppLiveButMuted }

/// <summary>Pure debounced mismatch evaluator (design 2026-07-11 section 2.3). A mismatch must
/// persist >= 5 s of consecutive readings before it banners (normal mute choreography must not
/// flicker it); agreement or Unknown clears IMMEDIATELY. Fed a caller-supplied clock so it is
/// fully unit-testable.</summary>
public sealed class AppMuteBannerEvaluator
{
    private const long DebounceMs = 5000;
    private AppMuteBannerKind _pending = AppMuteBannerKind.None;
    private long _pendingSinceMs;
    private AppMuteBannerKind _current = AppMuteBannerKind.None;

    public AppMuteBannerKind Evaluate(AppMuteReading reading, bool localMuted, long nowMs)
    {
        var kind = reading.State switch
        {
            AppMuteState.Muted when !localMuted => AppMuteBannerKind.AppMutedButRecording,
            AppMuteState.Live when localMuted => AppMuteBannerKind.AppLiveButMuted,
            _ => AppMuteBannerKind.None,
        };
        if (kind == AppMuteBannerKind.None)
        {
            _pending = AppMuteBannerKind.None;
            return _current = AppMuteBannerKind.None;         // agreement/Unknown: clear instantly
        }
        if (kind != _pending) { _pending = kind; _pendingSinceMs = nowMs; }
        if (_current != kind && nowMs - _pendingSinceMs >= DebounceMs) _current = kind;
        return _current;
    }
}
