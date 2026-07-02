using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
namespace LocalScribe.Core.Vad;

/// <summary>Razor-thin Silero VAD v5 ONNX adapter (Humble Object - no logic here).
/// Graph: input [1,576] f32 (64-sample carry-over context + 512-sample window, per the
/// reference OnnxWrapper - bare 512 inputs score real speech near zero), state [2,1,128] f32,
/// sr [1] i64 -> output [1,1], stateN.</summary>
public sealed class SileroVadModel : ISpeechProbabilityModel, IDisposable
{
    private const int ContextSamples = 64;                     // v5 @ 16 kHz
    private readonly InferenceSession _session;
    private float[] _state = new float[2 * 1 * 128];
    private float[] _context = new float[ContextSamples];
    private static readonly long[] SrValue = { 16000 };

    public SileroVadModel(string onnxPath) => _session = new InferenceSession(onnxPath);

    public float SpeechProbability(ReadOnlySpan<float> window)
    {
        var buffer = new float[ContextSamples + window.Length];
        _context.CopyTo(buffer, 0);
        window.CopyTo(buffer.AsSpan(ContextSamples));
        var input = new DenseTensor<float>(buffer, new[] { 1, buffer.Length });
        var state = new DenseTensor<float>(_state, new[] { 2, 1, 128 });
        var sr = new DenseTensor<long>(SrValue, new[] { 1 });

        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input", input),
            NamedOnnxValue.CreateFromTensor("state", state),
            NamedOnnxValue.CreateFromTensor("sr", sr),
        });

        float prob = results.First(r => r.Name == "output").AsEnumerable<float>().First();
        _state = results.First(r => r.Name == "stateN").AsEnumerable<float>().ToArray();
        _context = buffer[^ContextSamples..];
        return prob;
    }

    public void Reset()
    {
        _state = new float[2 * 1 * 128];
        _context = new float[ContextSamples];
    }

    public void Dispose() => _session.Dispose();
}
