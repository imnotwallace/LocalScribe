namespace LocalScribe.Core.Import;

/// <summary>Container-level CLAIMS (ffprobe / WAV header) plus the file's own timestamps, for the
/// import dialog preview, the recorded-date default, and the decoded-vs-claimed duration
/// cross-check (design 2026-07-13 section 4.1). Every Claimed* field is a claim, never truth.</summary>
public sealed record AudioProbeResult
{
    public string FormatName { get; init; } = "";          // ffprobe format_name / "wav"
    public long FileSizeBytes { get; init; }
    public long? ClaimedDurationMs { get; init; }
    public int? ClaimedChannels { get; init; }
    public int? ClaimedSampleRate { get; init; }
    public DateTimeOffset? MediaCreatedUtc { get; init; }  // container media-creation tag, if any
    public DateTimeOffset? FileCreatedUtc { get; init; }
    public DateTimeOffset? FileModifiedUtc { get; init; }

    /// <summary>Index of the chosen stream AMONG THE AUDIO STREAMS (the n in "-map 0:a:n"), or
    /// null when the file has no audio. Recorded because ffprobe and ffmpeg pick independently:
    /// ffprobe took the first audio stream while the decode carried no -map and let ffmpeg choose
    /// its own best (most channels), so on a multi-track body-worn file the recorded channels,
    /// sample rate and duration gate described a stream that was never decoded (2026-08-11). The
    /// decoder must be handed THIS value and force the SAME stream with -map so probe and decode
    /// always agree.</summary>
    public int? AudioStreamIndex { get; init; }
}

/// <summary>The decode result: PcmWavPath is PCM WAV at the stream's NATIVE rate/channel count
/// (for .wav inputs it is the INPUT path itself, opened read-only - never modified). SampleRate/
/// Channels/DurationMs are read from the decoder's own OUTPUT, never the source container
/// (decoded-stream truth, the verified Meetily bug class).</summary>
public sealed record DecodedAudio
{
    public string PcmWavPath { get; init; } = "";
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>Probe + decode seam so AudioImporter's unit tests run on a fake with no FFmpeg on
/// disk; FfmpegAudioDecoder is the production implementation (one fixture test drives it against
/// a real tiny MP3 - design section 4.5).</summary>
public interface IAudioDecoder
{
    Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct);

    /// <summary>Decodes the SAME stream <paramref name="probe"/> already described - the caller
    /// (AudioImporter) probes once and passes that result here rather than the decoder re-probing,
    /// which would spawn a second ffprobe over a large source for no reason. Passing probe also
    /// lets the decoder force the exact stream it reported (2026-08-11): without it, ffprobe and
    /// ffmpeg can pick different streams on a multi-track file and the recorded channels/sample
    /// rate/duration gate would describe audio that was never decoded.</summary>
    Task<DecodedAudio> DecodeAsync(string path, AudioProbeResult probe, string workDir, CancellationToken ct);
}
