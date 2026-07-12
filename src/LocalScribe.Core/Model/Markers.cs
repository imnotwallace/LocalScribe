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
    public const string TranscriptionFailed = "transcription failed";
    public const string LocalMuted = "microphone muted by user";
    public const string LocalUnmuted = "microphone unmuted";
    public const string MicDeviceMuted = "microphone device muted";
    public const string MicDeviceUnmuted = "microphone device unmuted";

    // Capture Scope Control (design 2026-07-12 section 3). "by user" marks these as DELIBERATE
    // live switches (parallel to PausedByUser / LocalMuted), distinguishing them from the
    // involuntary DegradedSystemAudioLoopback that the per-app->system-mix fallback reuses.
    public const string RemoteCaptureChangedSystemMix = "remote capture changed to full system mix by user (all machine audio)";
    public const string RemoteCaptureChangedPerApp    = "remote capture changed to per-app by user: {0}";

    // Capture Scope Control fail-safe (design 2026-07-12 section 2): a live re-target whose WASAPI
    // activation fails in StartLeg/Start() - AFTER the old leg is already torn down - first degrades
    // to full system mix (DegradedSystemAudioLoopback). Only if THAT system-mix fallback ALSO fails
    // to start (essentially never - whole-machine loopback) is the remote leg stopped and this
    // written, so an evidentiary transcript records the loss instead of silently dropping remote
    // audio. No "by user" - this is an involuntary failure, not a deliberate scope change.
    public const string RemoteCaptureLost = "remote capture stopped: the new target and the system-mix fallback both failed to start";
}
