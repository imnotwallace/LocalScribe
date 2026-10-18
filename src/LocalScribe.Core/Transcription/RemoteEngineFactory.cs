using System.Buffers.Binary;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Pipeline;

namespace LocalScribe.Core.Transcription;

/// <summary>Opt-in remote re-transcription (design 2026-10-18). Each engine owns one
/// LocalScribe.Inference.exe process and sends it one "transcribe" request per VAD segment over
/// stdin; the helper does the HTTP call. This class - like everything in Core - holds no network
/// code. The plan's backend and model are irrelevant here: the endpoint's model is the configured
/// one, so ModelName is fixed for the factory's life and the worker's language-lock swap only
/// changes the language a new engine is created with.</summary>
public sealed class RemoteEngineFactory(IAssistantProcessFactory helper, RemoteEndpointConfig endpoint,
    TimeSpan? inactivityTimeout = null) : IEngineFactory
{
    /// <summary>The model every engine from this factory requests - it names the version.</summary>
    public string ModelName => endpoint.Model;

    /// <summary>Where the audio goes, as recorded in the version's provenance.</summary>
    public string EndpointOrigin => RemoteInference.Origin(endpoint.Endpoint);

    public async Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? initialPrompt,
        CancellationToken ct)
    {
        IAssistantProcess proc = await helper.StartAsync(ct);
        return new RemoteTranscriptionEngine(proc, endpoint, language, initialPrompt,
            inactivityTimeout ?? TimeSpan.FromMinutes(5));
    }
}

/// <summary>Failure reported by the remote helper for one segment. Escapes the worker, which fails
/// the run (the partial version is discarded): a segment the server could not transcribe must not
/// silently become a hole in the transcript.</summary>
public sealed class RemoteTranscriptionException(string message) : Exception(message);

/// <summary>One helper process, kept alive across segments (keepAlive requests).</summary>
internal sealed class RemoteTranscriptionEngine(IAssistantProcess proc, RemoteEndpointConfig endpoint,
    string? language, string? initialPrompt, TimeSpan inactivityTimeout) : ITranscriptionEngine
{
    public string ModelName => endpoint.Model;

    /// <summary>A remote server has no weights file this machine can see; the requested model name
    /// stands in, so a weights-change marker still fires if a later engine asks for another.</summary>
    public string WeightsFile => endpoint.Model;

    public async Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct)
    {
        using var reg = ct.Register(proc.Kill);
        var request = new AssistantRequest(AssistantWire.TranscribeOp, ModelPath: "", CtxTokens: 0,
            AssistantWire.RemoteBackend, KeepAlive: true,
            AssistantWire.TranscribePayload(Convert.ToBase64String(WavEncoding.Pcm16Mono16k(segment.Pcm.Span)),
                language, string.IsNullOrEmpty(initialPrompt) ? null : initialPrompt),
            endpoint);
        await proc.WriteRequestLineAsync(AssistantWire.SerializeRequest(request), ct);

        AssistantTranscription? result = null;
        await foreach (var evt in AssistantEventStream.ReadUntilTerminalAsync(proc, inactivityTimeout, ct))
        {
            switch (evt)
            {
                case AssistantTranscription t: result = t; break;
                case AssistantError e:
                    ct.ThrowIfCancellationRequested();   // a kill on cancel surfaces as EOF - report the cancel
                    throw new RemoteTranscriptionException(e.Message);
            }
        }
        return result is null
            ? throw new RemoteTranscriptionException("the remote inference helper returned no transcription")
            : new TranscriptionResult(result.Text, result.Language, result.NoSpeechProb);
    }

    public ValueTask DisposeAsync() => proc.DisposeAsync();
}

/// <summary>The WAV the remote API receives: 16 kHz mono 16-bit PCM, the format every
/// OpenAI-compatible transcription server accepts. Float samples are clamped to [-1, 1].</summary>
public static class WavEncoding
{
    private const int SampleRate = 16000;
    private const int HeaderBytes = 44;

    public static byte[] Pcm16Mono16k(ReadOnlySpan<float> samples)
    {
        int dataBytes = samples.Length * 2;
        var wav = new byte[HeaderBytes + dataBytes];
        Span<byte> h = wav;
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], HeaderBytes - 8 + dataBytes);   // RIFF chunk size
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);                 // fmt chunk size (PCM)
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);                  // PCM
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], 1);                  // mono
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], SampleRate * 2);     // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], 2);                  // block align
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 16);                 // bits per sample
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], dataBytes);
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(h[(HeaderBytes + i * 2)..], (short)Math.Round(clamped * short.MaxValue));
        }
        return wav;
    }
}

/// <summary>A resolved remote re-transcription setup: the factory, or why there is none.</summary>
public sealed record RemoteEngineSetup(RemoteEngineFactory? Factory, string? Problem);
