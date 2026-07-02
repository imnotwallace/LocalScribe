using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

namespace LocalScribe.Core.Tests;

/// <summary>Deterministic VAD stand-in: speech prob 1.0 when any sample in the window is
/// non-zero, else 0.0. Lets tests drive VadCore with amplitude-shaped fake frames.</summary>
public sealed class AmplitudeSpeechModel : ISpeechProbabilityModel
{
    public float SpeechProbability(ReadOnlySpan<float> window)
    {
        for (int i = 0; i < window.Length; i++)
            if (window[i] != 0f) return 1f;
        return 0f;
    }
    public void Reset() { }
}

/// <summary>IEngineFactory over the existing FakeTranscriptionEngine. Promoted from
/// TranscriptionWorkerTests' former ScriptedFactory (renamed here so 2b and Stage-3a tests
/// share one fake instead of two near-duplicates): any per-plan engine construction is still
/// supported via the Func&lt;BackendPlan, ITranscriptionEngine&gt; constructor, and the
/// parameterless/transcribe-func constructor adds a default that echoes segment identity so
/// LiveSourcePipeline assertions can tie output lines back to input audio.</summary>
public sealed class FakeEngineFactory : IEngineFactory
{
    public readonly List<(BackendPlan Plan, string? Language)> Created = new();
    private readonly Func<BackendPlan, ITranscriptionEngine> _make;
    public int CreateCalls => Created.Count;

    public FakeEngineFactory(Func<AudioSegment, TranscriptionResult>? transcribe = null)
        : this(plan => new FakeTranscriptionEngine(plan.ModelName, transcribe ?? (s =>
            new TranscriptionResult($"{s.Source} {s.StartMs}-{s.EndMs}", "en", 0.0))))
    {
    }

    public FakeEngineFactory(Func<BackendPlan, ITranscriptionEngine> make) => _make = make;

    public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language,
        string? initialPrompt, CancellationToken ct)
    {
        Created.Add((plan, language));
        return Task.FromResult(_make(plan));
    }
}
