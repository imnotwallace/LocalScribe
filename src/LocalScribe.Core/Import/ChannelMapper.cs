using LocalScribe.Core.Audio;
using NAudio.Wave;
namespace LocalScribe.Core.Import;

/// <summary>The import dialog's stereo answer (design 2026-07-13 section 4.3). Downmix is the
/// default and the "No/unsure" answer; Split = L is me / R is the other party; SplitSwapped =
/// the swap control. The DECODED channel count always wins: Plan(1, Split) is still one mono leg.</summary>
public enum StereoMapping { Downmix, Split, SplitSwapped }

/// <summary>One output leg: which side it becomes and which decoded channel feeds it
/// (null = the average of ALL decoded channels).</summary>
public sealed record LegPlan(SourceKind Kind, int? Channel);

/// <summary>DownmixedMultichannel flags a &gt;2-channel source that was averaged to mono - the
/// importer surfaces it as Markers.ImportedDownmixed (degradation is never silent).</summary>
public sealed record ChannelMapPlan(IReadOnlyList<LegPlan> Legs, bool DownmixedMultichannel);

/// <summary>Pure channel-mapping planner + streaming leg writer (design 2026-07-13 section 4.3):
/// decoded PCM WAV (native rate/channels) becomes one or two 16 kHz mono WAV legs, each resampled
/// with its own stateful MonoResampler16k and written through WavSink - the exact frame format
/// WavFileFrameReader and the retained-audio step already consume.</summary>
public static class ChannelMapper
{
    public static ChannelMapPlan Plan(int decodedChannels, StereoMapping stereo)
    {
        if (decodedChannels == 2 && stereo != StereoMapping.Downmix)
        {
            bool swap = stereo == StereoMapping.SplitSwapped;
            return new ChannelMapPlan(
                [new LegPlan(SourceKind.Local, swap ? 1 : 0), new LegPlan(SourceKind.Remote, swap ? 0 : 1)],
                DownmixedMultichannel: false);
        }
        return new ChannelMapPlan([new LegPlan(SourceKind.Local, null)],
            DownmixedMultichannel: decodedChannels > 2);
    }

    public static IReadOnlyList<(SourceKind Kind, string WavPath)> WriteLegs(
        string decodedWavPath, ChannelMapPlan plan, string workDir, CancellationToken ct)
    {
        using var reader = new AudioFileReader(decodedWavPath);      // float samples, interleaved
        int channels = reader.WaveFormat.Channels;
        int rate = reader.WaveFormat.SampleRate;
        // The header's declared sample count (float samples). For a native-WAV import this IS the
        // decoder's "truth" (design 4.2: the data chunk is the stream), so the duration-mismatch
        // gate cannot catch a lying header - NAudio just returns fewer samples at physical EOF and
        // the legs come up short with no error. We cross-check the tally below (whole-branch M-2).
        long expectedFloats = reader.Length / sizeof(float);

        var legs = new List<(SourceKind Kind, string WavPath)>();
        var sinks = new List<WavSink>();
        var resamplers = new List<MonoResampler16k?>();
        long readFloats = 0;
        try
        {
            foreach (var leg in plan.Legs)
            {
                string path = Path.Combine(workDir,
                    leg.Kind == SourceKind.Local ? "local-16k.wav" : "remote-16k.wav");
                legs.Add((leg.Kind, path));
                sinks.Add(new WavSink(path));
                resamplers.Add(rate == 16000 ? null : new MonoResampler16k(rate));
            }

            var buf = new float[rate * channels];                    // ~1 s per read
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                readFloats += n;
                int frames = n / channels;
                for (int i = 0; i < plan.Legs.Count; i++)
                {
                    float[] mono = SelectChannel(buf.AsSpan(0, frames * channels), channels,
                        plan.Legs[i].Channel, frames);
                    sinks[i].Write(resamplers[i] is { } r ? r.Process(mono) : mono);
                }
            }
        }
        finally
        {
            foreach (var s in sinks) s.Dispose();                    // finalize WAV headers always
        }

        // Never write short legs silently: if the header declared materially more audio than the
        // stream actually held (> 1 percent, matching the decoded-vs-claimed duration gate), fail
        // loud so the import aborts atomically rather than recording truncated evidence.
        if (expectedFloats > 0 && (expectedFloats - readFloats) * 100 > expectedFloats)
        {
            long declaredMs = expectedFloats / channels * 1000 / rate;
            long actualMs = readFloats / channels * 1000 / rate;
            throw new InvalidDataException(
                $"WAV data is truncated: the header declares {declaredMs} ms of audio but only " +
                $"{actualMs} ms is present. The file may be corrupt or incompletely written.");
        }
        return legs;
    }

    private static float[] SelectChannel(ReadOnlySpan<float> interleaved, int channels,
        int? channel, int frames)
    {
        var mono = new float[frames];
        if (channel is int c)
        {
            for (int f = 0; f < frames; f++) mono[f] = interleaved[f * channels + c];
        }
        else
        {
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++) sum += interleaved[f * channels + ch];
                mono[f] = sum / channels;
            }
        }
        return mono;
    }
}
