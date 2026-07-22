// src/LocalScribe.Core/Transcription/WhisperNetEngine.cs
using LocalScribe.Core.Pipeline;
using Whisper.net;
namespace LocalScribe.Core.Transcription;

/// <summary>Razor-thin Whisper.net adapter. No decisions here: model path, language, prompt,
/// and thread count arrive resolved; failures map to the seam's exception types.</summary>
public sealed class WhisperNetEngine : ITranscriptionEngine
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    public string ModelName { get; }
    public string WeightsFile { get; }

    public WhisperNetEngine(string modelPath, string modelName, string? language, string? initialPrompt,
        int? threads = null)
    {
        ModelName = modelName;
        WeightsFile = Path.GetFileName(modelPath);
        _factory = WhisperFactory.FromPath(modelPath);
        var builder = _factory.CreateBuilder();
        builder = language is null or "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        if (!string.IsNullOrEmpty(initialPrompt)) builder = builder.WithPrompt(initialPrompt);
        if (threads is > 0) builder = builder.WithThreads(threads.Value);
        _processor = builder.Build();
    }

    public async Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct)
    {
        var parts = new List<string>();
        string? lang = null;
        double? minNoSpeech = null;
        try
        {
            await foreach (var s in _processor.ProcessAsync(segment.Pcm.ToArray(), ct))
            {
                if (!string.IsNullOrWhiteSpace(s.Text)) parts.Add(s.Text.Trim());
                lang ??= s.Language;
                // Track the MINIMUM no-speech probability across whisper.cpp segments (finding
                // I1). Conservative on purpose: every segment must look like non-speech before
                // the worker's hallucination gate (>= threshold) can drop real evidence, so one
                // confident speech segment among several must win. Stays null when no segments
                // were yielded (the empty-text gate already covers that case).
                double p = s.NoSpeechProbability;
                minNoSpeech = minNoSpeech is null ? p : Math.Min(minNoSpeech.Value, p);
            }
        }
        catch (Exception ex) when (LooksLikeVramOom(ex))
        {
            throw new VramOutOfMemoryException($"whisper backend OOM on {ModelName}", ex);
        }
        return new TranscriptionResult(string.Join(" ", parts), lang, NoSpeechProb: minNoSpeech);
    }

    private static bool LooksLikeVramOom(Exception ex)
        => ex.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("CUDA", StringComparison.OrdinalIgnoreCase)
              && ex.Message.Contains("alloc", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        // Whisper.net's WhisperProcessor.Dispose() THROWS ("Cannot dispose while processing, please
        // use DisposeAsync instead.") when a chunk is still in flight - its processingSemaphore is
        // held until whisper.cpp returns, which can't be interrupted instantly. That is exactly the
        // teardown state when a long import/recording is cancelled mid-transcription: the worker's
        // `finally { await engine.DisposeAsync(); }` ran while the current chunk was still decoding.
        // The sync throw then SUPERSEDED the propagating OperationCanceledException (exception from a
        // finally wins), so a clean cancel surfaced as a raw error toast. DisposeAsync() awaits the
        // in-flight chunk (processingSemaphore.WaitAsync) before disposing, so cancel-then-teardown
        // is clean and the caller's OperationCanceledException survives.
        await _processor.DisposeAsync();
        _factory.Dispose();
    }
}
