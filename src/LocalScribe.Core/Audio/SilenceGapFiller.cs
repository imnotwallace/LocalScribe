// src/LocalScribe.Core/Audio/SilenceGapFiller.cs
namespace LocalScribe.Core.Audio;

/// <summary>
/// Pure time-alignment math for a gappy capture stream (per-process loopback).
/// The device reports a monotonically advancing sample position even across silence;
/// we insert exactly the missing frames so the written stream tracks that timeline.
/// </summary>
public static class SilenceGapFiller
{
    /// <summary>Silence frames to insert before a packet whose device position is
    /// <paramref name="devicePosFrames"/>, given we have written <paramref name="writtenFrames"/>
    /// frames so far. Both are measured from the stream's start anchor. Clamped to >= 0.</summary>
    public static long SilenceFramesBefore(long writtenFrames, long devicePosFrames)
        => Math.Max(0, devicePosFrames - writtenFrames);

    /// <summary>A zero-filled mono buffer of <paramref name="frames"/> samples (empty if &lt;= 0).</summary>
    public static float[] SilenceFrame(long frames)
        => frames <= 0 ? Array.Empty<float>() : new float[frames];
}
