namespace LocalScribe.Core.Model;

/// <summary>Canonical in-transcript marker messages (spec section 8.1). The arrow in
/// <see cref="PinnedMicUnavailable"/> is written as a \u escape so this source file stays ASCII;
/// the rendered string is the spec glyph. Only RecoveredSession is emitted in Stage 2a (recovery);
/// the rest are raised by Stage 2b / Stage 7 and shared from here.</summary>
public static class Markers
{
    public const string AudioDeviceChanged = "audio device changed";
    public const string PausedSystemSleep = "paused: system sleep";
    public const string Resumed = "resumed";
    public const string PausedByUser = "paused by user";
    public const string DegradedSystemAudioLoopback = "degraded: system-audio loopback";
    public const string PinnedMicUnavailable = "pinned microphone unavailable \u2192 default";
    public const string TranscriptionLagging = "transcription lagging";
    public const string RecoveredSession = "recovered session";
}
