// src/LocalScribe.App/ViewModels/PlaybackViewModel.cs
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.ViewModels;

/// <summary>Transport state for the read view (design section 5). WPF-free. Leg resolution:
/// the session's RetainedAudioSources says which legs to look for, the settings AudioFormat
/// is only a preference - the actual on-disk file decides (both flac and wav are probed per
/// leg, because sessions may predate a format change). No legs on disk -> IsAvailable=false
/// and the window hides the transport. Position is polled by the window's ~150 ms
/// DispatcherTimer via Tick(); tests call Tick() directly (same pattern as SessionViewModel).</summary>
public sealed partial class PlaybackViewModel : ObservableObject, IDisposable
{
    private readonly IDualAudioPlayer _player;
    private readonly Action<Action> _dispatch;
    private readonly Func<long> _wallClock;

    /// <summary>Corrects Windows Media Foundation's constant post-seek clock offset on
    /// pre-fix FLAC legs (probe-verified 2026-07-11 - see <see cref="PlaybackClockCorrector"/>).
    /// Read-side only; never touches the underlying audio files.</summary>
    private readonly PlaybackClockCorrector _corrector = new();

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _endReached;
    /// <summary>True while the user is interacting with the seek slider (drag / track-click /
    /// arrow keys); suppresses <see cref="Tick"/> so the ~150 ms poll cannot snap the thumb
    /// back mid-interaction. Set by the window's input handlers.</summary>
    [ObservableProperty] private bool _isScrubbing;
    [ObservableProperty] private long _positionMs;
    /// <summary>TwoWay-bound to the seek Slider's Value (design: Stage 5.4 smoke-fix). Kept
    /// deliberately separate from <see cref="PositionMs"/>: WPF's Slider runs its OWN class
    /// handlers (track-click IsMoveToPointEnabled, arrow/Page/Home/End key command bindings)
    /// BEFORE our XAML instance handlers, and those class handlers mark the routed event
    /// Handled - so a OneWay PositionMs binding plus Preview*/KeyDown instance handlers could
    /// never see those gestures. A TwoWay binding on this property fires for every WPF-side
    /// Value change regardless of routed-event Handled state, so it is the only reliable seek
    /// trigger. VM code must NEVER set this raw - always via <see cref="SyncSlider"/> - or the
    /// echo would be interpreted as a user-initiated seek.</summary>
    [ObservableProperty] private long _sliderValueMs;
    [ObservableProperty] private long _durationMs;
    [ObservableProperty] private string _positionDisplay = "00:00";
    [ObservableProperty] private string _durationDisplay = "00:00";
    [ObservableProperty] private bool _localMuted;
    [ObservableProperty] private bool _remoteMuted;
    [ObservableProperty] private double _localVolume = 1.0;
    [ObservableProperty] private double _remoteVolume = 1.0;
    [ObservableProperty] private bool _hasLocalLeg;
    [ObservableProperty] private bool _hasRemoteLeg;
    /// <summary>Index of the transcript row currently "now playing" (design 4.1); mirrored here
    /// from ReadViewViewModel.PlayingSectionIndex so the transport layer sees the same value.
    /// -1 when no section is current.</summary>
    [ObservableProperty] private int _playingIndex = -1;

    /// <summary>Bound to the transport button so the caption tracks VM state, not an
    /// imperative poke in the click handler (design 4.1).</summary>
    public string PlayPauseCaption => IsPlaying ? "Pause" : "Play";

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseCaption));

    /// <summary>True whenever a SliderValueMs write is the VM echoing its own position (Tick,
    /// Seek, Stop, replay, MediaEnded) rather than a genuine WPF-side user gesture; guards
    /// <see cref="OnSliderValueMsChanged"/> from re-entering <see cref="Seek"/> on its own echo.</summary>
    private bool _syncingSlider;

    /// <summary>The ONLY place VM code may assign <see cref="SliderValueMs"/>; raw assignment
    /// would be indistinguishable from a user drag/click/keypress and would re-enter Seek.</summary>
    private void SyncSlider(long ms)
    {
        _syncingSlider = true;
        SliderValueMs = ms;
        _syncingSlider = false;
    }

    /// <summary>Fires for every WPF-side Value change on the TwoWay-bound slider - track click,
    /// arrow/Page/Home/End keys, and thumb-drag deltas - regardless of whether Slider's own class
    /// handlers marked the routed event Handled (they run before our instance handlers and always
    /// do for track-click/keyboard). Commits immediately unless this is the VM's own echo
    /// (<see cref="_syncingSlider"/>) or the user is mid-drag (<see cref="IsScrubbing"/>, released
    /// via the window's DragCompleted handler instead).</summary>
    partial void OnSliderValueMsChanged(long value)
    {
        if (_syncingSlider || IsScrubbing) return;
        Seek(value);
    }

    public IRelayCommand PlayPauseCommand { get; }
    public IRelayCommand StopCommand { get; }

    public PlaybackViewModel(IDualAudioPlayer player, Action<Action> dispatch,
        Func<long>? wallClock = null)
    {
        (_player, _dispatch) = (player, dispatch);
        _wallClock = wallClock ?? (() => Environment.TickCount64);
        PlayPauseCommand = new RelayCommand(PlayPause, () => IsAvailable);
        StopCommand = new RelayCommand(Stop, () => IsAvailable);
        player.MediaReady += () => _dispatch(() =>
        {
            DurationMs = player.DurationMs;
            DurationDisplay = Format(DurationMs);
        });
        player.MediaEnded += () => _dispatch(() =>
        {
            IsPlaying = false;
            EndReached = true;
            PositionMs = DurationMs;                 // hold at the end; do NOT rewind
            PositionDisplay = Format(DurationMs);
            SyncSlider(DurationMs);
        });
    }

    public void Resolve(StoragePaths paths, string sessionId,
        IReadOnlyList<SourceKind> retained, AudioFormat preferredFormat)
    {
        string? Probe(SourceKind kind)
        {
            if (!retained.Contains(kind)) return null;
            string preferred = paths.AudioFile(sessionId, kind, preferredFormat);
            if (File.Exists(preferred)) return preferred;
            var other = preferredFormat == AudioFormat.Flac ? AudioFormat.Wav : AudioFormat.Flac;
            string alternate = paths.AudioFile(sessionId, kind, other);
            return File.Exists(alternate) ? alternate : null;
        }

        string? local = Probe(SourceKind.Local);
        string? remote = Probe(SourceKind.Remote);
        HasLocalLeg = local is not null;
        HasRemoteLeg = remote is not null;
        IsAvailable = local is not null || remote is not null;
        PlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        if (IsAvailable) _player.Load(local, remote);
        _corrector.OnLoaded();
    }

    /// <summary>Windows Media Foundation can misreport MediaPlayer.Position for the app's small
    /// near-silent local FLAC legs: after a seek-then-Play, a readback can come back wildly past
    /// the file's own NaturalDuration (probe-verified 2026-07-06: ~54s reported on a 23s file).
    /// MF also truncates FLAC NaturalDuration to whole seconds, so a legitimate position can
    /// slightly exceed DurationMs (~+600ms observed) - that overshoot must be tolerated, not
    /// treated as corrupt.</summary>
    private const long DurationToleranceMs = 2000;

    /// <summary>Driven by the window's ~150 ms DispatcherTimer; tests call it directly. While the
    /// user is scrubbing (drag / track-click / arrow keys) polling is suppressed so the timer
    /// cannot snap the thumb back mid-interaction.</summary>
    public void Tick()
    {
        if (IsScrubbing) return;
        var p = _corrector.Correct(_player.PositionMs, _wallClock(), IsPlaying);
        if (p < 0 || (DurationMs > 0 && p > DurationMs + DurationToleranceMs))
            return;                               // insane readback; keep last-known-good position
        PositionMs = DurationMs > 0 ? Math.Min(p, DurationMs) : p;   // pin small MF overshoot to duration
        PositionDisplay = Format(PositionMs);
        SyncSlider(PositionMs);
    }

    public void Seek(long ms)
    {
        if (DurationMs > 0) ms = Math.Clamp(ms, 0, DurationMs);   // transcript timestamps can exceed
                                                                    // the retained audio; land at
                                                                    // end-of-media, don't seek past it
        _corrector.OnSeek(ms, _wallClock());
        _player.SeekMs(ms);
        PositionMs = ms;                         // reflect immediately, independent of the poll
        PositionDisplay = Format(ms);
        EndReached = false;                       // a manual seek exits "held at end"; Play should
                                                    // resume from here, not replay from zero
        SyncSlider(PositionMs);                   // moves the bar for programmatic seeks (JumpToSection)
    }

    private void PlayPause()
    {
        if (IsPlaying) { _player.Pause(); IsPlaying = false; _corrector.OnPause(); }
        else
        {
            if (EndReached)                          // replay from the top after end-of-media
            {
                _player.SeekMs(0);
                _corrector.OnSeek(0, _wallClock());   // replay's rewind is a real seek: the probe
                                                       // never showed the offset SURVIVES a seek-to-0,
                                                       // so stop applying; the learned-offset fast
                                                       // path re-latches in one jumped reading
                PositionMs = 0;
                PositionDisplay = Format(0);
                EndReached = false;
                SyncSlider(0);
            }
            _corrector.OnPlay(PositionMs, _wallClock());   // exact pos when replayed above; the
                                                            // resumed value otherwise
            _player.Play();
            IsPlaying = true;
        }
    }

    /// <summary>Transport Stop/Restart: pause, rewind to 0, and clear playing/end state so the
    /// button returns to "Play" (design 4.1).</summary>
    public void Stop()
    {
        _player.Pause();
        _player.SeekMs(0);
        _corrector.OnSeek(0, _wallClock());       // Stop's rewind is a seek-to-0: MF reads it
                                                    // back exact, same as a manual paused Seek(0).
                                                    // (No OnPause needed - OnSeek supersedes it.)
        PositionMs = 0;
        PositionDisplay = Format(0);
        IsPlaying = false;
        EndReached = false;
        SyncSlider(0);
    }

    partial void OnLocalMutedChanged(bool value) => _player.SetLegMuted(local: true, muted: value);
    partial void OnRemoteMutedChanged(bool value) => _player.SetLegMuted(local: false, muted: value);
    partial void OnLocalVolumeChanged(double value) => _player.SetLegVolume(local: true, volume: value);
    partial void OnRemoteVolumeChanged(double value) => _player.SetLegVolume(local: false, volume: value);

    private static string Format(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    }

    public void Dispose() => _player.Dispose();
}
