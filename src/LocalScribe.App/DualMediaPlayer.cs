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

    // Windows Media Foundation can misreport MediaPlayer.Position on the app's small
    // near-silent local FLAC legs: after a seek-then-Play, the local leg's Position has been
    // observed to read back ~54s on a ~23s file (exceeding its own NaturalDuration) while the
    // remote leg reads back correctly (probe-verified 2026-07-05/06, 4/4 reproducible). Last
    // known-good position so a fully-insane readback (both legs, or no second leg to fall back
    // to) still returns something sane instead of the corrupted value. Reset by SeekMs so a
    // post-seek fallback reflects the seek, not a stale pre-seek position.
    private long _lastPositionMs;

    public event Action? MediaReady;
    public event Action? MediaEnded;

    // The first existing leg is the primary: it drives duration, position, and transport events.
    private MediaPlayer Primary => _hasLocal ? _localPlayer : _remotePlayer;
    // The other leg, when both legs exist - used as a fallback reader when Primary misreports.
    private MediaPlayer? Secondary => _hasLocal && _hasRemote ? _remotePlayer : null;

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
        _lastPositionMs = ms;                     // so a post-seek insane readback falls back to this
    }

    public void SetLegMuted(bool local, bool muted)
        => (local ? _localPlayer : _remotePlayer).IsMuted = muted;

    public void SetLegVolume(bool local, double volume)
        => (local ? _localPlayer : _remotePlayer).Volume = volume;   // MediaPlayer.Volume is 0.0..1.0

    public long DurationMs => Primary.NaturalDuration.HasTimeSpan
        ? (long)Primary.NaturalDuration.TimeSpan.TotalMilliseconds
        : 0;

    // See the _lastPositionMs field comment above for the root cause. Prefer the primary leg's
    // reported position; if it is insane, fall back to the other leg (when one exists); if
    // neither is sane, hold the last known-good value rather than surface the corrupted one.
    public long PositionMs
    {
        get
        {
            long duration = DurationMs;
            long primaryMs = (long)Primary.Position.TotalMilliseconds;
            if (IsSanePosition(primaryMs, duration)) return _lastPositionMs = primaryMs;

            long? secondaryMs = (long?)Secondary?.Position.TotalMilliseconds;
            if (secondaryMs is { } s && IsSanePosition(s, duration)) return _lastPositionMs = s;

            return _lastPositionMs;               // both legs insane (or no second leg); last-known-good
        }
    }

    private static bool IsSanePosition(long ms, long durationMs)
        => ms >= 0 && (durationMs <= 0 || ms <= durationMs + 2000);

    public void Dispose()
    {
        _localPlayer.Close();
        _remotePlayer.Close();
    }
}
