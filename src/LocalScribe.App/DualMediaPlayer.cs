// src/LocalScribe.App/DualMediaPlayer.cs
using System.Windows.Media;
using LocalScribe.App.Services;
namespace LocalScribe.App;

/// <summary>IDualAudioPlayer over two System.Windows.Media.MediaPlayer instances (design
/// section 5): Play/Pause/Position are mirrored on both so the two legs stay a pair; the
/// second player is engaged only when both legs exist. Minor drift on very long sessions is
/// accepted for v1. FLAC/WAV decode via Media Foundation (Windows 10+ decodes FLAC natively).
/// Deliberately thin and NOT unit-tested: MediaPlayer requires the OS media stack, real
/// files, and a pumping dispatcher - verified via the Stage 4 smoke runbook instead.
/// Construct on the UI thread (MediaPlayer is dispatcher-affine).</summary>
public sealed class MediaPlayerDualAudioPlayer : IDualAudioPlayer
{
    private readonly MediaPlayer _localPlayer = new();
    private readonly MediaPlayer _remotePlayer = new();
    private bool _hasLocal, _hasRemote;

    public event Action? MediaReady;
    public event Action? MediaEnded;

    // The first existing leg is the primary: it drives duration, position, and transport events.
    private MediaPlayer Primary => _hasLocal ? _localPlayer : _remotePlayer;

    public void Load(string? localPath, string? remotePath)
    {
        _hasLocal = localPath is not null;
        _hasRemote = remotePath is not null;
        if (!_hasLocal && !_hasRemote) return;                       // VM never calls Load like this; belt-and-braces

        Primary.MediaOpened += (_, _) => MediaReady?.Invoke();
        Primary.MediaEnded += (_, _) => MediaEnded?.Invoke();
        if (_hasLocal) _localPlayer.Open(new Uri(localPath!));
        if (_hasRemote) _remotePlayer.Open(new Uri(remotePath!));
    }

    public void Play()
    {
        if (_hasLocal) _localPlayer.Play();
        if (_hasRemote) _remotePlayer.Play();
    }

    public void Pause()
    {
        if (_hasLocal) _localPlayer.Pause();
        if (_hasRemote) _remotePlayer.Pause();
    }

    public void SeekMs(long ms)
    {
        var position = TimeSpan.FromMilliseconds(ms);
        if (_hasLocal) _localPlayer.Position = position;             // seeked as a pair (design section 5)
        if (_hasRemote) _remotePlayer.Position = position;
    }

    public void SetLegMuted(bool local, bool muted)
        => (local ? _localPlayer : _remotePlayer).IsMuted = muted;

    public long PositionMs => (long)Primary.Position.TotalMilliseconds;

    public long DurationMs => Primary.NaturalDuration.HasTimeSpan
        ? (long)Primary.NaturalDuration.TimeSpan.TotalMilliseconds
        : 0;

    public void Dispose()
    {
        _localPlayer.Close();
        _remotePlayer.Close();
    }
}
