// src/LocalScribe.App/Services/IDualAudioPlayer.cs
namespace LocalScribe.App.Services;

/// <summary>Dual-leg audio transport seam (design section 5): local + remote legs play
/// together as a pair so the user hears the conversation, not one side. Keeps
/// PlaybackViewModel WPF-free; the production implementation wraps two
/// System.Windows.Media.MediaPlayer instances (DualMediaPlayer.cs), tests script a fake.</summary>
public interface IDualAudioPlayer : IDisposable
{
    void Load(string? localPath, string? remotePath);
    void Play();
    void Pause();
    void SeekMs(long ms);
    void SetLegMuted(bool local, bool muted);
    long PositionMs { get; }
    long DurationMs { get; }
    event Action? MediaReady;
    event Action? MediaEnded;
}
