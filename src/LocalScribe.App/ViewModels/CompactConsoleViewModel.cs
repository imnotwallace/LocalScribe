using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
namespace LocalScribe.App.ViewModels;

/// <summary>The compact pill's mute-button visual state (design 2026-07-18 section 6): the full
/// console's mute/device-mute/app-mute-advisory banners collapse to a colored state + tooltip -
/// never lost (locked rule). Task 4's XAML maps each value to a distinct theme brush.</summary>
public enum CompactMuteState { Normal, Muted, DeviceMuted, AppMuteAdvisory }

/// <summary>Compact-mode state for the Record console (design 2026-07-18 section 6): the SAME
/// window collapsed to a ~420x64 always-on-top pill. UI-only over EXISTING state (locked rule -
/// capture and Start/Stop/Pause semantics unchanged): this VM duplicates nothing, it only derives
/// pill surfaces from the shared SessionViewModel + TranscriptLinesViewModel, and every pill
/// control binds the SAME commands the full console binds (MuteLocalCommand / StopCommand).
/// WPF-free; in production it lives as long as the hide-on-close singleton console window.</summary>
public sealed partial class CompactConsoleViewModel : ObservableObject, IDisposable
{
    /// <summary>Pill geometry (design section 6: ~420x64). Consts so the window code-behind and
    /// the clamp math can never drift from the XAML template's layout.</summary>
    public const double PillWidth = 420;
    public const double PillHeight = 64;
    /// <summary>The console's EXISTING empty-state line (record-console-polish round, section 5
    /// item 1) - the pill's warm-up/"preparing" surface: while Recording with zero lines (model
    /// still warming up, or nobody has spoken), the last-line slot shows this instead of blank.</summary>
    public const string ListeningText = "Listening - transcript appears a few seconds after speech.";

    private readonly SessionViewModel _session;
    private readonly TranscriptLinesViewModel _lines;
    private readonly ISettingsService _settings;
    // Named (not lambdas) so Dispose can detach them - the session/lines VMs are the shared,
    // app-lifetime instances every console surface binds (the SessionViewModel precedent).
    private readonly PropertyChangedEventHandler _onSessionChanged;
    private readonly PropertyChangedEventHandler _onLinesChanged;
    private readonly NotifyCollectionChangedEventHandler _onLineListChanged;
    private SessionState _lastState;
    private bool _disposed;

    /// <summary>True while the console renders as the compact pill. The window binds Topmost and
    /// both templates' visibility to this; the code-behind swaps geometry on its flips.</summary>
    [ObservableProperty] private bool _isCompact;
    /// <summary>The last finalized live line, single-line end-trimmed (see PillLine); the
    /// listening/warm-up hint while Recording with no line yet; "" otherwise.</summary>
    [ObservableProperty] private string _lastLineText = "";
    /// <summary>The mute pill's visual state (see MutePill). Advisory states carry the full
    /// banner's meaning in MuteTooltip - the banner is collapsed, never lost (locked rule).</summary>
    [ObservableProperty] private CompactMuteState _muteState = CompactMuteState.Normal;
    /// <summary>Tooltip for the mute pill; for the app-mute advisory it is the banner's EXACT text.</summary>
    [ObservableProperty] private string _muteTooltip = "Mute my side (Ctrl+Shift+M)";

    /// <summary>Compact toggle - bound by BOTH entry points (the header's Compact button and the
    /// pill's Expand button).</summary>
    public IRelayCommand ToggleCompactCommand { get; }

    public CompactConsoleViewModel(SessionViewModel session, TranscriptLinesViewModel lines,
        ISettingsService settings)
    {
        (_session, _lines, _settings) = (session, lines, settings);
        ToggleCompactCommand = new RelayCommand(() => IsCompact = !IsCompact);
        _lastState = session.State;
        // The console window (and this VM) is constructed lazily - in production often AFTER the
        // open-console-on-start hook already flipped State to Recording - so the auto-compact
        // decision is also evaluated against the CURRENT state, not only future transitions.
        IsCompact = NextCompact(SessionState.Idle, session.State, false,
            settings.Current.Console.CompactOnStart);

        _onSessionChanged = (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SessionViewModel.State):
                    var next = _session.State;
                    IsCompact = NextCompact(_lastState, next, IsCompact,
                        _settings.Current.Console.CompactOnStart);
                    _lastState = next;
                    break;
                case nameof(SessionViewModel.IsLocalMuted):
                case nameof(SessionViewModel.MicDeviceMuted):
                case nameof(SessionViewModel.AppMuteBannerKind):
                case nameof(SessionViewModel.AppMuteBannerText):
                    RefreshMutePill();
                    break;
            }
        };
        _onLinesChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptLinesViewModel.ShowListeningHint))
                RefreshLastLine();
        };
        _onLineListChanged = (_, _) => RefreshLastLine();
        session.PropertyChanged += _onSessionChanged;
        lines.PropertyChanged += _onLinesChanged;
        lines.Lines.CollectionChanged += _onLineListChanged;
        RefreshMutePill();
        RefreshLastLine();
    }

    /// <summary>The WHOLE compact-transition rule (design 2026-07-18 section 6), pure: on
    /// Idle->Recording the pill opens if it already was open or the user opted into
    /// collapse-on-start (DEFAULT OFF); leaving the live states (Stop -> Finalizing/Idle) ALWAYS
    /// restores the full console, so the finished session is reviewed full-size; Pause/Resume keep
    /// whatever the user chose (mute/resume semantics stay reachable on the pill).</summary>
    public static bool NextCompact(SessionState prev, SessionState next, bool current, bool compactOnStart)
        => prev == SessionState.Idle && next == SessionState.Recording ? current || compactOnStart
         : next is SessionState.Recording or SessionState.Paused ? current
         : false;

    /// <summary>Priority mapping for the mute pill (design section 6, locked: banners collapse to
    /// a colored state + tooltip - NEVER lost). The APP-MUTE advisory wins FIRST: the full console
    /// renders it UNCONDITIONALLY of IsLocalMuted (LiveViewWindow.xaml binds the banner Grid's
    /// Visibility to Session.AppMuteBannerVisible alone, no mute-state gate), and
    /// AppMuteBannerEvaluator.Evaluate only ever raises AppLiveButMuted while localMuted==true - so
    /// checking localMuted first would permanently mask that advisory behind the generic Muted
    /// state (the evidentiary-loss bug this fixes: the pill would silently drop the one banner that
    /// says "you are live in the call but LocalScribe is not recording your side"). Deliberate mute
    /// then wins over the device-mute fact (the controller already suppresses device-mute reporting
    /// while deliberately muted), then Normal. Note: the pill's mute ICON stays bound to
    /// Session.IsLocalMuted separately in XAML (MicOff when muted), so surfacing the advisory
    /// color+text while also muted still shows the muted icon - "muted AND here's the advisory" is
    /// the correct combined signal. The pill itself stays advisory-safe: its click routes through
    /// MuteLocalCommand, never a marker.</summary>
    public static (CompactMuteState State, string Tooltip) MutePill(
        bool localMuted, bool deviceMuted, AppMuteBannerKind advisoryKind, string advisoryText)
    {
        if (advisoryKind != AppMuteBannerKind.None)
            return (CompactMuteState.AppMuteAdvisory, advisoryText);
        if (localMuted)
            return (CompactMuteState.Muted,
                "Your side is muted - not being recorded. Click to unmute (Ctrl+Shift+M).");
        if (deviceMuted)
            return (CompactMuteState.DeviceMuted,
                "Your microphone device is muted - nothing is being recorded from it.");
        return (CompactMuteState.Normal, "Mute my side (Ctrl+Shift+M)");
    }

    /// <summary>The pill's last-line text, pure: the listening/warm-up hint while the live list is
    /// in its Recording-and-empty window; else the last line VERBATIM (locked evidentiary rule: no
    /// content filtering) - "Speaker: text" for segments, bare text for markers (their Speaker is
    /// "") - collapsed to a SINGLE line and end-trimmed for the 64px layout only. Visual overflow
    /// is ellipsized by the XAML's TextTrimming with the full text in a tooltip.</summary>
    public static string PillLine(TranscriptLineViewModel? last, bool listening)
    {
        if (listening) return ListeningText;
        if (last is null) return "";
        string text = last.Text.ReplaceLineEndings(" ").TrimEnd();
        return string.IsNullOrEmpty(last.Speaker) ? text : $"{last.Speaker}: {text}";
    }

    private void RefreshMutePill()
        => (MuteState, MuteTooltip) = MutePill(_session.IsLocalMuted, _session.MicDeviceMuted,
            _session.AppMuteBannerKind, _session.AppMuteBannerText);

    private void RefreshLastLine()
        => LastLineText = PillLine(_lines.Lines.Count > 0 ? _lines.Lines[^1] : null,
            _lines.ShowListeningHint);

    /// <summary>Detaches the ctor's subscriptions from the shared app-lifetime VMs (the
    /// SessionViewModel Dispose precedent). Idempotent - a second Dispose() is a safe no-op.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.PropertyChanged -= _onSessionChanged;
        _lines.PropertyChanged -= _onLinesChanged;
        _lines.Lines.CollectionChanged -= _onLineListChanged;
    }
}
