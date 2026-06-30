// src/LocalScribe.Core/Audio/PcmConverter.cs
using System.Buffers.Binary;
namespace LocalScribe.Core.Audio;

public static class PcmConverter
{
    public static float[] Int16BytesToFloat(ReadOnlySpan<byte> bytes)
    {
        int n = bytes.Length / 2;
        var outp = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2));
            outp[i] = s / 32768f;
        }
        return outp;
    }

    public static float[] StereoToMono(ReadOnlySpan<float> interleaved)
    {
        int n = interleaved.Length / 2;
        var outp = new float[n];
        for (int i = 0; i < n; i++)
            outp[i] = (interleaved[i * 2] + interleaved[i * 2 + 1]) * 0.5f;
        return outp;
    }

    public static byte[] FloatToInt16Bytes(ReadOnlySpan<float> samples)
    {
        var outp = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            short s = (short)Math.Round(clamped * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(outp.AsSpan(i * 2, 2), s);
        }
        return outp;
    }
}
