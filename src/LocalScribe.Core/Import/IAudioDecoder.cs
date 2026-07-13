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
    Task<DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct);
}
