// src/LocalScribe.Core/Audio/MonoResampler16k.cs
using NAudio.Dsp;
namespace LocalScribe.Core.Audio;

/// <summary>Resamples mono float input at an arbitrary rate to 16 kHz mono.</summary>
public sealed class MonoResampler16k
{
    private readonly WdlResampler _resampler = new();
    private readonly int _inputRate;

    public MonoResampler16k(int inputSampleRate)
    {
        _inputRate = inputSampleRate;
        // NAudio 2.2.1: SetMode has 5 params (interp, filtercnt, sinc, sinc_size, sinc_interpsize).
        // sinc=false so sinc_size/sinc_interpsize are unused; pass 0.
        _resampler.SetMode(true, 2, false, 0, 0);
        // NAudio 2.2.1: SetFilterParms requires (filterpos, filterq); no zero-arg overload.
        // Using standard WDL defaults: filterpos=0.693f, filterq=0.707f.
        _resampler.SetFilterParms(0.693f, 0.707f);
        _resampler.SetFeedMode(true);                      // input-driven
        _resampler.SetRates(_inputRate, WavSink.SampleRate);
    }

    public float[] Process(ReadOnlySpan<float> monoInput)
    {
        int needed = _resampler.ResamplePrepare(monoInput.Length, 1,
            out float[] inBuf, out int inOffset);
        int toCopy = Math.Min(needed, monoInput.Length);
        for (int i = 0; i < toCopy; i++) inBuf[inOffset + i] = monoInput[i];

        var outBuf = new float[(int)(monoInput.Length *
            ((double)WavSink.SampleRate / _inputRate) + 16)];   // generous output buffer
        int written = _resampler.ResampleOut(outBuf, 0, toCopy, outBuf.Length, 1);

        var result = new float[written];
        Array.Copy(outBuf, result, written);
        return result;
    }
}
