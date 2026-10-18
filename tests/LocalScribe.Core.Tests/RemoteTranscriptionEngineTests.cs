using System.Buffers.Binary;
using System.Text.Json.Nodes;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

namespace LocalScribe.Core.Tests;

/// <summary>The Core half of remote re-transcription (design 2026-10-18): one helper process per
/// engine, one "transcribe" request per segment over its stdin. No HTTP here - that is the
/// helper's job (InferenceHelperTests).</summary>
public class RemoteTranscriptionEngineTests
{
    /// <summary>Answers every transcribe request with the scripted events (the helper's side of
    /// the wire), recording each request line.</summary>
    internal sealed class ScriptedHelper(Func<AssistantRequest, IEnumerable<AssistantEvent>> script)
        : IAssistantProcessFactory, IAssistantProcess
    {
        private readonly Queue<string> _pending = new();
        public List<AssistantRequest> Requests { get; } = [];
        public int Starts { get; private set; }
        public bool Disposed { get; private set; }

        public Task<IAssistantProcess> StartAsync(CancellationToken ct) { Starts++; return Task.FromResult<IAssistantProcess>(this); }

        public Task WriteRequestLineAsync(string requestJson, CancellationToken ct)
        {
            var request = AssistantWire.ParseRequestLine(requestJson)!;
            Requests.Add(request);
            foreach (var evt in script(request)) _pending.Enqueue(AssistantWire.SerializeEvent(evt));
            return Task.CompletedTask;
        }

        public Task<string?> ReadEventLineAsync(CancellationToken ct)
            => Task.FromResult(_pending.Count > 0 ? _pending.Dequeue() : null);

        public void Kill() { }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static readonly RemoteEndpointConfig Endpoint = new("https://inference.example", "large-v3", "k-1");

    private static AudioSegment Segment(params float[] pcm) => new(SourceKind.Local, 0, 1000, pcm);

    [Fact]
    public async Task One_request_per_segment_carries_audio_language_prompt_and_endpoint()
    {
        var helper = new ScriptedHelper(_ =>
            [new AssistantTranscription("hello", "en", 0.2), new AssistantDone("remote", 0, 0, "large-v3")]);
        var factory = new RemoteEngineFactory(helper, Endpoint);
        await using var engine = await factory.CreateAsync(new BackendPlan(Backend.Cpu, "large-v3"),
            "en", "Acme, Zorblat", default);

        var result = await engine.TranscribeAsync(Segment(0f, 0.5f), default);

        Assert.Equal(new TranscriptionResult("hello", "en", 0.2), result);
        var sent = Assert.Single(helper.Requests);
        Assert.Equal(AssistantWire.TranscribeOp, sent.Op);
        Assert.True(sent.KeepAlive);
        Assert.Equal(Endpoint, sent.Remote);
        var payload = JsonNode.Parse(sent.PayloadJson)!;
        Assert.Equal("en", payload["language"]!.GetValue<string>());
        Assert.Equal("Acme, Zorblat", payload["prompt"]!.GetValue<string>());
        Assert.Equal(WavEncoding.Pcm16Mono16k([0f, 0.5f]),
            Convert.FromBase64String(payload["audioWav"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Engine_reuses_its_process_across_segments_and_names_the_configured_model()
    {
        var helper = new ScriptedHelper(_ => [new AssistantTranscription("x", null, null), new AssistantDone("remote", 0, 0)]);
        var factory = new RemoteEngineFactory(helper, Endpoint);
        var engine = await factory.CreateAsync(new BackendPlan(Backend.Cpu, "ignored"), null, null, default);
        await engine.TranscribeAsync(Segment(0f), default);
        await engine.TranscribeAsync(Segment(0f), default);
        await engine.DisposeAsync();

        Assert.Equal(1, helper.Starts);
        Assert.Equal(2, helper.Requests.Count);
        Assert.True(helper.Disposed);
        Assert.Equal("large-v3", engine.ModelName);
        Assert.Equal("large-v3", engine.WeightsFile);
        Assert.Equal("https://inference.example", factory.EndpointOrigin);
        Assert.Null(JsonNode.Parse(helper.Requests[0].PayloadJson)!["language"]);   // unlocked: server detects
    }

    [Fact]
    public async Task A_helper_error_fails_the_segment_with_its_message()
    {
        var helper = new ScriptedHelper(_ => [new AssistantError("REMOTE_FAILED: https://inference.example answered HTTP 401")]);
        await using var engine = await new RemoteEngineFactory(helper, Endpoint)
            .CreateAsync(new BackendPlan(Backend.Cpu, "m"), null, null, default);

        var ex = await Assert.ThrowsAsync<RemoteTranscriptionException>(() => engine.TranscribeAsync(Segment(0f), default));
        Assert.Contains("HTTP 401", ex.Message);
    }

    [Fact]
    public void Wav_header_describes_16kHz_mono_16bit_pcm_with_correct_sizes()
    {
        byte[] wav = WavEncoding.Pcm16Mono16k([0f, 1f, -1f, 2f]);
        Assert.Equal(44 + 8, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));        // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));        // mono
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        short[] samples = Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2))).ToArray();
        Assert.Equal(new short[] { 0, short.MaxValue, -short.MaxValue, short.MaxValue }, samples);   // 2.0 clamps
    }
}
