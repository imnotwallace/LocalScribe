namespace LocalScribe.Core.Model;

/// <summary>One run of MACHINE-GENERATED samples inside a retained audio leg (Tier 1 T1-7, spec
/// 2026-08-05 :148-153). AlignedAudioWriter zero-fills every clock gap and appends zeros to the
/// session end, and before this record nothing anywhere said where. A SHA-256 that seals the file
/// without this list certifies synthetic silence as original recorded audio - worse than no hash at
/// all, because it converts an absence of evidence into a false positive assertion.
/// Sample offsets, NOT milliseconds: the writer's arithmetic is exact in samples and a rounded ms
/// range would not identify the bytes it claims to describe. Divide by ManifestFile.SampleRate for
/// a readable time.</summary>
public sealed record FabricatedSpan
{
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    /// <summary>"clock-gap" (AlignedAudioWriter.Write filled a capture gap - a pause, a dropout or
    /// clock jitter) or "end-pad" (PadToMs appended zeros out to the stop instant so the file spans
    /// the whole session). A trailing pad and a mid-call dropout mean very different things to a
    /// reader, so they are never merged into one bucket.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>What ONE retained leg's writer fabricated, handed from SessionController to
/// ManifestBuilder at finalize (Tier 1 T1-7). Positional because it is a two-field carrier with no
/// serialization contract of its own - only its Spans reach manifest.json.</summary>
public sealed record FabricatedSilenceRecord(int SampleRate, IReadOnlyList<FabricatedSpan> Spans);
