using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

/// <summary>The single session VM behind tray, live view, and overlay (spec 2.1: all three
/// surfaces bind one SessionViewModel and route to the same SessionController). WPF-free:
/// controller events (worker threads) marshal through the injected dispatch delegate; capture
/// calls run via Task.Run (MTA-sensitive activation must stay off the STA UI thread).</summary>
public sealed partial class SessionViewModel : ObservableObject, IDisposable
{
    private readonly SessionController _controller;
    private readonly Settings _settings;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly LiveSessionOptions _startOptions;
    private readonly Func<IReadOnlyList<string>>? _matterIdsProvider;
    private DateTimeOffset? _startedAt;
    private bool _disposed;

    // Task 8 / Fix #2: named (not lambdas) so Dispose can detach them - _controller is the
    // shared, app-lifetime SessionController these come from.
    private readonly Action<SourceKind> _onSilentLegDetected;
    private readonly Action<SourceKind> _onSilentLegCleared;
    // Task 2 (mute controls): same named-handler/Dispose-detach pattern as the silent-leg pair
    // above - _controller is the shared, app-lifetime SessionController.
    private readonly Action<bool> _onLocalMuteChanged;
    // Task 5 (device-mute banner): same named-handler/Dispose-detach pattern as _onLocalMuteChanged.
    private readonly Action<bool> _onMicDeviceMuteChanged;
    // Task 8 (Phase 2 advisory app-mute banner, design 2026-07-11): the ADVISORY Win11 tray
    // call-mute signal. Optional and dormant when null (every existing caller/test constructs the
    // VM without it). The evaluator debounces mismatches; the two watcher handlers are named so
    // Dispose can detach them. _wallClock is a monotonic ms clock for the evaluator's debounce
    // (Environment.TickCount64 in production) - deliberately NOT _time (the evaluator needs a plain
    // long delta, not a TimeProvider), matching the design's caller-supplied clock seam.
    private readonly AppMuteWatcher? _appMuteWatcher;
    private readonly Func<long> _wallClock;
    private readonly AppMuteBannerEvaluator _appMuteEvaluator = new();
    private readonly Action<AppMuteReading>? _onAppMuteReading;
    private readonly Action? _onAppMutePolled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording), nameof(IsPaused), nameof(IsIdle))]
    private SessionState _state = SessionState.Idle;
    [ObservableProperty] private string _elapsed = "00:00";
    [ObservableProperty] private string? _lastNotice;
    [ObservableProperty] private bool _isLagging;
    /// <summary>Task 8 / Fix #2: true while SessionController has flagged 15s of no transcript
    /// segment from the microphone leg (a wrong/muted capture device) - persistent until a
    /// segment arrives or Resume clears it (SilentLegCleared).</summary>
    [ObservableProperty] private bool _micSilent;
    /// <summary>Same as <see cref="MicSilent"/> but for the remote (system/app) capture leg.</summary>
    [ObservableProperty] private bool _remoteSilent;
    /// <summary>True while the user's own side is muted (design 2026-07-10 section 1 - "Mute my
    /// side"): mirrors <see cref="SessionController.LocalMuted"/>, kept in sync via
    /// LocalMuteChanged and reset on every new Start.</summary>
    [ObservableProperty] private bool _isLocalMuted;
    /// <summary>True while the local capture device itself is muted at the OS/endpoint level
    /// (design 2026-07-10 section 2): mirrors <see cref="SessionController.MicDeviceMuteChanged"/>,
    /// kept in sync via that event and reset on every new Start. Distinct from
    /// <see cref="IsLocalMuted"/> (the user's deliberate in-LocalScribe mute).</summary>
    [ObservableProperty] private bool _micDeviceMuted;
    /// <summary>Task 8 (advisory app-mute banner): which mismatch, if any, the debounced tray
    /// signal is currently surfacing. Never gates recording and never writes a marker - the
    /// one-click action routes through the existing MuteLocalCommand, so any marker comes from the
    /// user's click, not from this signal (locked rule, design 2026-07-11).</summary>
    [ObservableProperty] private AppMuteBannerKind _appMuteBannerKind = AppMuteBannerKind.None;
    /// <summary>Human-readable banner line for the current <see cref="AppMuteBannerKind"/> ("" when None).</summary>
    [ObservableProperty] private string _appMuteBannerText = "";
    /// <summary>Label for the banner's one-click button ("Mute my side" / "Unmute"; "" when None).</summary>
    [ObservableProperty] private string _appMuteActionLabel = "";
    /// <summary>True while a banner is showing - the XAML binds the warning row's Visibility to this.</summary>
    public bool AppMuteBannerVisible => AppMuteBannerKind != AppMuteBannerKind.None;
    partial void OnAppMuteBannerKindChanged(AppMuteBannerKind value)
        => OnPropertyChanged(nameof(AppMuteBannerVisible));

    public LevelMeter LocalLevel { get; } = new();
    public LevelMeter RemoteLevel { get; } = new();
    public string? CurrentSessionId => _controller.CurrentSessionId;
    public bool IsRecording => State == SessionState.Recording;
    public bool IsPaused => State == SessionState.Paused;
    public bool IsIdle => State == SessionState.Idle;

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand PauseResumeCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IAsyncRelayCommand MuteLocalCommand { get; }

    /// <summary>Fires on every controller Notice, even if the text is identical to the last one
    /// (unlike PropertyChanged(LastNotice), which [ObservableProperty] gates on equality). Lets
    /// a consent-relevant balloon (e.g. the full-mix bleed/privacy warning) re-show on a repeat.</summary>
    public event Action<string>? NoticeRaised;

    public SessionViewModel(SessionController controller, Settings settings,
        Action<Action> dispatch, TimeProvider? time = null, LiveSessionOptions? startOptions = null,
        Func<IReadOnlyList<string>>? matterIdsProvider = null,
        AppMuteWatcher? appMuteWatcher = null, Func<long>? wallClock = null)
    {
        (_controller, _settings, _dispatch, _time, _startOptions, _matterIdsProvider)
            = (controller, settings, dispatch, time ?? TimeProvider.System,
               startOptions ?? new LiveSessionOptions(), matterIdsProvider);
        _appMuteWatcher = appMuteWatcher;
        // Monotonic ms since boot when none supplied - matches the evaluator's nowMs delta
        // semantics (design 2026-07-11 section 2.3); tests inject a controllable clock instead.
        _wallClock = wallClock ?? (() => Environment.TickCount64);

        StartCommand = new AsyncRelayCommand(StartAsync, () => State == SessionState.Idle);
        PauseResumeCommand = new AsyncRelayCommand(PauseResumeAsync,
            () => State is SessionState.Recording or SessionState.Paused);
        StopCommand = new AsyncRelayCommand(StopAsync,
            () => State is SessionState.Recording or SessionState.Paused);
        MuteLocalCommand = new AsyncRelayCommand(ToggleMuteAsync,
            () => State is SessionState.Recording or SessionState.Paused);

        controller.StateChanged += s => _dispatch(() =>
        {
            State = s;
            StartCommand.NotifyCanExecuteChanged();
            PauseResumeCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            MuteLocalCommand.NotifyCanExecuteChanged();
            // Fix 2 (spec 8.3: the advisory banner is NEVER shown while Idle/Paused). Previously it
            // cleared only lazily on the next 2 s poll, so after Pause a now-false "still recording
            // your side" line lingered for up to ~2 s. On leaving Recording, CLEAR the banner
            // eagerly - do NOT recompute (the watcher's Last is still the stale pre-transition
            // reading) - and reset the evaluator so a Pause/Resume or fast Stop/Start re-debounces
            // from scratch (also closes the previously-known sub-2s stop/restart false-grace edge).
            // Dormant-safe: setting the three surface props is harmless when already cleared, and the
            // evaluator Reset() is guarded on the watcher exactly like ReevaluateAppMuteBanner.
            if (s != SessionState.Recording)
            {
                AppMuteBannerKind = AppMuteBannerKind.None;
                AppMuteBannerText = "";
                AppMuteActionLabel = "";
                if (_appMuteWatcher is not null) _appMuteEvaluator.Reset();
            }
        });
        controller.Notice += n => _dispatch(() => { LastNotice = n; NoticeRaised?.Invoke(n); });
        controller.ErrorRaised += e => _dispatch(() => { if (e == "RTF_LAGGING") IsLagging = true; });
        controller.PeakObserved += (source, peak) => _dispatch(() =>
            (source == SourceKind.Local ? LocalLevel : RemoteLevel).Observe(peak));

        _onSilentLegDetected = kind => _dispatch(() =>
        { if (kind == SourceKind.Local) MicSilent = true; else RemoteSilent = true; });
        _onSilentLegCleared = kind => _dispatch(() =>
        { if (kind == SourceKind.Local) MicSilent = false; else RemoteSilent = false; });
        controller.SilentLegDetected += _onSilentLegDetected;
        controller.SilentLegCleared += _onSilentLegCleared;

        _onLocalMuteChanged = muted => _dispatch(() =>
        {
            IsLocalMuted = muted;
            // The controller suppresses device-mute warnings while deliberately muted (Task 5's
            // OnDeviceMuteChanged guard) but does not retroactively clear a banner already
            // showing - clear it here so the two banners never render simultaneously; an
            // already-muted device re-surfaces via the unmute hook's initial DeviceMuted read.
            if (muted) MicDeviceMuted = false;
            // Task 8: muting/unmuting resolves an app-mute mismatch immediately - re-evaluate now
            // rather than waiting for the next 2 s poll (the evaluator clears agreement instantly).
            ReevaluateAppMuteBanner();
        });
        controller.LocalMuteChanged += _onLocalMuteChanged;

        _onMicDeviceMuteChanged = muted => _dispatch(() => MicDeviceMuted = muted);
        controller.MicDeviceMuteChanged += _onMicDeviceMuteChanged;

        // Task 8: wire the ADVISORY app-mute watcher only when present (dormant otherwise). Both a
        // changed reading AND every poll tick while recording drive a re-evaluation: ReadingChanged
        // catches state flips, Polled catches debounce EXPIRY on an unchanged reading (the mismatch
        // must persist >= 5 s before it banners, so an unchanged Muted reading still needs a later
        // poll to cross the threshold). Both marshal through _dispatch like every other handler.
        if (appMuteWatcher is not null)
        {
            _onAppMuteReading = _ => _dispatch(ReevaluateAppMuteBanner);
            _onAppMutePolled = () => _dispatch(ReevaluateAppMuteBanner);
            appMuteWatcher.ReadingChanged += _onAppMuteReading;
            appMuteWatcher.Polled += _onAppMutePolled;
        }
    }

    /// <summary>Task 8: runs the pure debounced evaluator over the latest tray reading and the
    /// user's in-app mute state, then projects the result onto the three banner surface properties.
    /// No-op when the watcher is absent (feature dormant). ADVISORY only - never writes a marker,
    /// never gates recording; the action button routes through MuteLocalCommand so any marker comes
    /// from the user's click.</summary>
    private void ReevaluateAppMuteBanner()
    {
        if (_appMuteWatcher is null) return;
        var reading = _appMuteWatcher.Last;
        var kind = _appMuteEvaluator.Evaluate(reading, IsLocalMuted, _wallClock());
        string app = reading.AppName ?? "the call app";
        switch (kind)
        {
            case AppMuteBannerKind.AppMutedButRecording:
                AppMuteBannerText = $"{app} looks muted - LocalScribe is still recording your side.";
                AppMuteActionLabel = "Mute my side";
                break;
            case AppMuteBannerKind.AppLiveButMuted:
                AppMuteBannerText = $"You are unmuted in {app} - LocalScribe is not recording your side.";
                AppMuteActionLabel = "Unmute";
                break;
            default:
                AppMuteBannerText = "";
                AppMuteActionLabel = "";
                break;
        }
        AppMuteBannerKind = kind;
    }

    /// <summary>Detaches the SilentLegDetected/Cleared/LocalMuteChanged/MicDeviceMuteChanged
    /// subscriptions taken in the ctor - _controller is the shared, app-lifetime SessionController,
    /// so an undetached subscription would root every SessionViewModel instance that ever attaches
    /// to it. Idempotent - a second Dispose() is a safe no-op.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.SilentLegDetected -= _onSilentLegDetected;
        _controller.SilentLegCleared -= _onSilentLegCleared;
        _controller.LocalMuteChanged -= _onLocalMuteChanged;
        _controller.MicDeviceMuteChanged -= _onMicDeviceMuteChanged;
        // Task 8: detach the app-mute watcher handlers taken in the ctor - the watcher is
        // app-lifetime (composed once beside the shared controller), so an undetached handler
        // would root this VM. Only wired when the watcher was supplied, so guard on both.
        if (_appMuteWatcher is not null)
        {
            if (_onAppMuteReading is not null) _appMuteWatcher.ReadingChanged -= _onAppMuteReading;
            if (_onAppMutePolled is not null) _appMuteWatcher.Polled -= _onAppMutePolled;
        }
    }

    private async Task StartAsync()
    {
        IsLagging = false;
        // Final-review Finding 1: MicSilent/RemoteSilent are only ever cleared by a
        // SilentLegCleared event, but this VM is app-lifetime while SessionController creates a
        // FRESH SilentLegMonitor per session - a leg flagged at the end of session 1 would leave
        // the banner stuck showing from t=0 of session 2 (whose fresh monitor never flags, so it
        // never clears). Reset both explicitly on every new Start.
        MicSilent = false;
        RemoteSilent = false;
        // Task 2: same stale-flag-from-a-prior-session hazard as MicSilent/RemoteSilent above -
        // this VM is app-lifetime while SessionController hands out a fresh session per Start,
        // so a mute left on at the end of session 1 (SetLocalMuteAsync is per-SESSION state on
        // the Core side) must not carry a false "muted" banner into session 2's t=0.
        IsLocalMuted = false;
        // Task 5: same stale-flag-from-a-prior-session hazard as IsLocalMuted above - the device
        // could still be muted at the end of session 1 (MicDeviceMuteChanged is per-SESSION
        // reporting on the Core side, guarded by session identity), so a stuck true must not carry
        // a false device-mute banner into session 2's t=0.
        MicDeviceMuted = false;
        // Task 8: clear any advisory app-mute banner left over from a prior session (the watcher
        // resets its own Last to Unknown once recording stops, but reset the surface eagerly so
        // session 2 opens clean regardless of poll timing).
        AppMuteBannerKind = AppMuteBannerKind.None;
        AppMuteBannerText = "";
        AppMuteActionLabel = "";
        var options = _matterIdsProvider is null
            ? _startOptions
            : _startOptions with { MatterIds = _matterIdsProvider() };
        string? id = await Task.Run(() => _controller.StartAsync(options, CancellationToken.None));
        if (id is not null) _startedAt = _time.GetUtcNow();
    }

    private Task PauseResumeAsync()
        => Task.Run(() => State == SessionState.Paused
            ? _controller.ResumeAsync(CancellationToken.None)
            : _controller.PauseAsync(CancellationToken.None));

    private Task ToggleMuteAsync()
        => Task.Run(() => _controller.SetLocalMuteAsync(!_controller.LocalMuted, CancellationToken.None));

    private async Task StopAsync()
    {
        await Task.Run(() => _controller.StopAsync(CancellationToken.None));
        _startedAt = null;
        Elapsed = "00:00";
        LocalLevel.Tick(); RemoteLevel.Tick();
    }

    /// <summary>Driven by a ~150 ms DispatcherTimer in production; tests call it directly.
    /// The elapsed clock keeps ticking through Pause (spec 2.1).</summary>
    public void TimerTick()
    {
        if (_startedAt is { } started)
        {
            var span = _time.GetUtcNow() - started;
            Elapsed = span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        LocalLevel.Tick();
        RemoteLevel.Tick();
    }
}
