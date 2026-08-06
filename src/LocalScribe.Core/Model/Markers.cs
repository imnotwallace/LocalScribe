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
    /// <summary>Format: {0} = previous weights file, {1} = new weights file. Written whenever an
    /// engine recreation (VRAM-OOM floor fall, ladder downgrade, language-lock swap) loads a
    /// DIFFERENT file than the one that produced prior segments - a mid-session weights change
    /// is evidence and must never be silent (review finding 2026-07-13). Renders with the same
    /// arrow glyph as PinnedMicUnavailable.</summary>
    public const string TranscriptionWeightsChanged = "transcription weights changed: {0} → {1}";
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

    // Audio import (design 2026-07-13 section 4): decode-truth degradation is surfaced in the
    // transcript, never silent. {0}/{1} in ImportedDurationMismatch are h:mm:ss / m:ss durations
    // (claimed, decoded); {0} in ImportedDownmixed is the decoded channel count (2 for a stereo
    // file the user did not declare as one-party-per-channel; more for a multichannel source).
    public const string ImportedDurationMismatch =
        "imported audio duration mismatch: container claimed {0}, decoded {1}";
    public const string ImportedDownmixed =
        "imported audio downmixed to mono: source had {0} channels";

    // Import-time speaker detection (design 2026-07-28 section 5). Only the outcomes that leave no
    // other trace are marked: on success speakers.json + SessionRecord.Diarised ARE the record, so
    // a marker would be redundant clutter. {0} in SpeakerDetectionFailed is the failure detail.
    public const string SpeakerDetectionFailed =
        "speaker detection did not complete: {0}. The transcript and audio are unaffected.";
    public const string SpeakerDetectionOneVoice =
        "speaker detection found only one voice; no speaker labels were applied.";
    public const string SpeakerDetectionNoAudio =
        "speaker detection could not run: no retained audio leg for this session.";

    // Crash recovery re-derive (Tier 1B design 2026-08-05, T1-2). {0} = the end of the retained
    // audio, {1} = the end of the last transcript line, both h:mm:ss. Written ONLY when the audio
    // genuinely outlasts the transcript - the marker rule is that an outcome leaving no other trace
    // gets a marker, and a silent duration correction leaves none. It is not clutter on the normal
    // path: a clean stop pads audio to the stop instant (AlignedAudioWriter.PadToMs), so the two
    // agree and no marker is written.
    public const string RecoveredAudioBeyondTranscript =
        "recovered session: retained audio runs to {0} but the transcript stops at {1} - "
        + "the remainder was never transcribed; use Re-transcribe to recover it";

    // Capture abandoned after the restart budget (Tier 1B design 2026-08-05, T1-4a).
    // {0} = "microphone" | "remote", {1} = the attempt count. Written ONCE per leg, and only after
    // CaptureRestartLimit rebuilds have each been followed by silence. Distinct from
    // AudioDeviceChanged, which says "this leg died and we are reconnecting it": this one says we
    // have stopped trying, which is the fact a reader months later actually needs - the tail of the
    // recording has no audio from that side, and AlignedAudioWriter.PadToMs will have silence-filled
    // the file to full length so nothing else on disk says so.
    public const string CaptureNotRecovered =
        "capture did not come back for the {0} stream after {1} reconnection attempts - "
        + "the remainder of this session has no {0} audio";

    // Capture-health faults (Tier 1B design 2026-08-05, T1-4b). {0} = "microphone" | "remote".
    // Written when a leg's AUDIO WRITE loop faults - disk full, or a device removed mid-write.
    // Recorded because it leaves no other trace: the leg's file simply stops growing, and on a
    // clean Stop AlignedAudioWriter.PadToMs then silence-fills it to the full session length, so
    // the file looks exactly the right size while holding fabricated silence for the whole tail.
    public const string AudioCaptureFailed =
        "audio recording stopped for the {0} stream - the remainder of this session has no {0} audio";

    // Low disk space during a live recording (Tier 1B design 2026-08-05, T1-4c). No placeholder:
    // the exact byte count is a diagnostic detail, and a marker is EVIDENCE - the fact that the
    // recording ran while the disk was nearly full is what matters to a reader months later.
    // Written once per crossing; DiskSpaceGuard re-arms if the user frees space and it drops again.
    public const string LowDiskSpace =
        "low disk space while recording - the remainder of this session may be incomplete";

    // System sleep (Tier 1B design 2026-08-05, T1-4d). PausedSystemSleep above has been DECLARED
    // since Stage 2b with no writer anywhere; this round gives it one. {0} is the WALL-CLOCK gap
    // (h:mm:ss) the machine spent suspended - the session clock is monotonic and simply does not
    // advance across a suspend, so without this the transcript would show a pause and a resume
    // three seconds apart for a call that was interrupted for half an hour.
    public const string ResumedAfterSleep = "resumed after system sleep: {0} was not recorded";
}
