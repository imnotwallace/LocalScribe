using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

/// <summary>Scripted engine for worker/merger/runner tests. Either a per-segment function
/// or a FIFO of outcomes (results or exceptions to throw).</summary>
public sealed class FakeTranscriptionEngine : ITranscriptionEngine
{
    private readonly Func<AudioSegment, TranscriptionResult>? _fn;
    private readonly Queue<object>? _script;           // TranscriptionResult | Exception
    public string ModelName { get; }
    public string WeightsFile { get; }                 // defaults to the plain f16 file name
    public int Calls { get; private set; }

    public FakeTranscriptionEngine(string modelName, Func<AudioSegment, TranscriptionResult> fn,
        string? weightsFile = null)
        => (ModelName, _fn, WeightsFile) = (modelName, fn, weightsFile ?? $"ggml-{modelName}.bin");

    public FakeTranscriptionEngine(string modelName, IEnumerable<object> script,
        string? weightsFile = null)
        => (ModelName, _script, WeightsFile) = (modelName, new Queue<object>(script), weightsFile ?? $"ggml-{modelName}.bin");

    public Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct)
    {
        Calls++;
        if (_fn is not null) return Task.FromResult(_fn(segment));
        object next = _script!.Dequeue();
        if (next is Exception ex) throw ex;
        return Task.FromResult((TranscriptionResult)next);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
