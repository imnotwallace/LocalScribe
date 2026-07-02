// src/LocalScribe.Core/Transcription/WhisperNetEngine.cs
using LocalScribe.Core.Pipeline;
using Whisper.net;
namespace LocalScribe.Core.Transcription;

/// <summary>Razor-thin Whisper.net adapter. No decisions here: model path, language, and
/// prompt arrive resolved; failures map to the seam's exception types.</summary>
public sealed class WhisperNetEngine : ITranscriptionEngine
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    public string ModelName { get; }

    public WhisperNetEngine(string modelPath, string modelName, string? language, string? initialPrompt)
    {
        ModelName = modelName;
        _factory = WhisperFactory.FromPath(modelPath);
        var builder = _factory.CreateBuilder();
        builder = language is null or "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        if (!string.IsNullOrEmpty(initialPrompt)) builder = builder.WithPrompt(initialPrompt);
        _processor = builder.Build();
    }

    public async Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct)
    {
        var parts = new List<string>();
        string? lang = null;
        try
        {
            await foreach (var s in _processor.ProcessAsync(segment.Pcm.ToArray(), ct))
            {
                if (!string.IsNullOrWhiteSpace(s.Text)) parts.Add(s.Text.Trim());
                lang ??= s.Language;
            }
        }
        catch (Exception ex) when (LooksLikeVramOom(ex))
        {
            throw new VramOutOfMemoryException($"whisper backend OOM on {ModelName}", ex);
        }
        return new TranscriptionResult(string.Join(" ", parts), lang, NoSpeechProb: null);
    }

    private static bool LooksLikeVramOom(Exception ex)
        => ex.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("CUDA", StringComparison.OrdinalIgnoreCase)
              && ex.Message.Contains("alloc", StringComparison.OrdinalIgnoreCase);

    public ValueTask DisposeAsync()
    {
        _processor.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
