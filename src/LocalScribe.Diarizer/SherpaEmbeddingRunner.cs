using SherpaOnnx;

namespace LocalScribe.Diarizer;

// Humble object over sherpa-onnx SpeakerEmbeddingExtractor (voiceprint design 2026-07-25).
// Same CAM++ model file the diarisation clustering uses; loaded once per helper invocation.
// API surface confirmed empirically by reflecting org.k2fsa.sherpa.onnx 1.13.3's sherpa-onnx.dll:
// SpeakerEmbeddingExtractor.CreateStream()/Compute(OnlineStream)/Dispose(); OnlineStream is
// IDisposable with AcceptWaveform(int, float[])/InputFinished() -- matches the plan's guess exactly.
internal sealed class SherpaEmbeddingRunner : IDisposable
{
    private readonly SpeakerEmbeddingExtractor _extractor;

    public SherpaEmbeddingRunner(string embModelPath)
    {
        var config = new SpeakerEmbeddingExtractorConfig();
        config.Model = embModelPath;
        config.NumThreads = 1;
        _extractor = new SpeakerEmbeddingExtractor(config);
    }

    public float[] Compute(float[] samples16kMono)
    {
        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(16000, samples16kMono);
        stream.InputFinished();
        return _extractor.Compute(stream);
    }

    public void Dispose() => _extractor.Dispose();
}
