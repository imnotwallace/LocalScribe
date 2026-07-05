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

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _endReached;
    /// <summary>True while the user is interacting with the seek slider (drag / track-click /
    /// arrow keys); suppresses <see cref="Tick"/> so the ~150 ms poll cannot snap the thumb
    /// back mid-interaction. Set by the window's input handlers.</summary>
    [ObservableProperty] private bool _isScrubbing;
    [ObservableProperty] private long _positionMs;
    [ObservableProperty] private long _durationMs;
    [ObservableProperty] private string _positionDisplay = "00:00";
    [ObservableProperty] private string _durationDisplay = "00:00";
    [ObservableProperty] private bool _localMuted;
    [ObservableProperty] private bool _remoteMuted;
    [ObservableProperty] private double _localVolume = 1.0;
    [ObservableProperty] private double _remoteVolume = 1.0;
    [ObservableProperty] private bool _hasLocalLeg;
    [ObservableProperty] private bool _hasRemoteLeg;

    /// <summary>Bound to the transport button so the caption tracks VM state, not an
    /// imperative poke in the click handler (design 4.1).</summary>
    public string PlayPauseCaption => IsPlaying ? "Pause" : "Play";

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseCaption));

    public IRelayCommand PlayPauseCommand { get; }
    public IRelayCommand StopCommand { get; }

    public PlaybackViewModel(IDualAudioPlayer player, Action<Action> dispatch)
    {
        (_player, _dispatch) = (player, dispatch);
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
    }

    /// <summary>Driven by the window's ~150 ms DispatcherTimer; tests call it directly. While the
    /// user is scrubbing (drag / track-click / arrow keys) polling is suppressed so the timer
    /// cannot snap the thumb back mid-interaction.</summary>
    public void Tick()
    {
        if (IsScrubbing) return;
        PositionMs = _player.PositionMs;
        PositionDisplay = Format(PositionMs);
    }

    public void Seek(long ms)
    {
        _player.SeekMs(ms);
        PositionMs = ms;                         // reflect immediately, independent of the poll
        PositionDisplay = Format(ms);
        EndReached = false;                       // a manual seek exits "held at end"; Play should
                                                    // resume from here, not replay from zero
    }

    private void PlayPause()
    {
        if (IsPlaying) { _player.Pause(); IsPlaying = false; }
        else
        {
            if (EndReached)                          // replay from the top after end-of-media
            {
                _player.SeekMs(0);
                PositionMs = 0;
                PositionDisplay = Format(0);
                EndReached = false;
            }
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
        PositionMs = 0;
        PositionDisplay = Format(0);
        IsPlaying = false;
        EndReached = false;
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
