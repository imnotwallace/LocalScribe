// Remote transcription engine: wraps RemoteTranscriptionHelper to implement
// ITranscriptionEngine, so the transcription worker pipeline can use it
// interchangeably with WhisperNetEngine.
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Transcription;

namespace LocalScribe.Inference;

/// <summary>Remote Whisper transcription engine implementing ITranscriptionEngine.
/// Delegates all transcription to RemoteTranscriptionHelper via HTTP. Model name
/// and weights file are the model name sent to the API (evidentiary provenance).</summary>
internal sealed class RemoteTranscriptionEngine : ITranscriptionEngine
{
    private readonly RemoteTranscriptionHelper _helper;
    private readonly string _model;
    private readonly string? _initialPrompt;

    /// <summary>Create a remote transcription engine. Does NOT connect — first TranscribeAsync
    /// call probes the endpoint on-demand.</summary>
    /// <param name="endpoint">Remote API base URL.</param>
    /// <param name="apiKey">Optional API key.</param>
    /// <param name="model">Model name to send to the API.</param>
    /// <param name="timeout">Request timeout.</param>
    /// <param name="initialPrompt">Initial vocabulary prompt (remote Whisper APIs may ignore this,
    /// but it is stored for provenance consistency with the local engine).</param>
    public RemoteTranscriptionEngine(
        string endpoint, string? apiKey, string model, TimeSpan timeout, string? initialPrompt)
    {
        _helper = new RemoteTranscriptionHelper(endpoint, apiKey, model, timeout);
        _model = model;
        _initialPrompt = initialPrompt;
    }

    public string ModelName => _model;

    /// <summary>Provenance: the model name sent to the remote API. Equivalent to WeightsFile
    /// for the local engine (exact model that ran).</summary>
    public string WeightsFile => _model;

    public async Task<TranscriptionResult> TranscribeAsync(AudioSegment segment, CancellationToken ct)
    {
        // Convert the audio segment to base64-encoded 16 kHz mono WAV.
        // The segment samples are already 16 kHz mono float32 (from VAD segmentation).
        var pcm = segment.Pcm.ToArray();
        var wavBase64 = EncodeWav(pcm);
        return await _helper.TranscribeAsync(wavBase64, null, 0.0, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _helper.Dispose();
        await ValueTask.CompletedTask;
    }

    /// <summary>Convert float32 samples (16 kHz mono) to base64-encoded WAV bytes.
    /// Produces a 44-byte WAV header + 16-bit PCM data.</summary>
    private static string EncodeWav(float[] samples)
    {
        // 16-bit PCM: each sample → 2 bytes (short, little-endian)
        var pcmBytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = samples[i] < -1.0f ? (short)(-32768) :
                      samples[i] > 1.0f ? (short)32767 :
                      (short)(samples[i] * 32767.0f);
            pcmBytes[i * 2] = (byte)(s & 0xFF);
            pcmBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return EncodeWavBytes(pcmBytes);
    }

    /// <summary>Encode raw 16-bit PCM bytes as a base64-encoded WAV file.</summary>
    private static string EncodeWavBytes(byte[] pcmBytes)
    {
        var dataLen = pcmBytes.Length;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8);

        // WAV header (44 bytes total):
        // Offset 0:  "RIFF" (4)
        // Offset 4:  file size - 8 (4) = dataLen + 28
        // Offset 8:  "WAVE" (4)
        // Offset 12: "fmt " (4)
        // Offset 16: 16 (fmt chunk size, PCM)
        // Offset 20: 1   (PCM format, 2 bytes)
        // Offset 22: 1   (mono, 2 bytes)
        // Offset 24: 16000 (sample rate, 4 bytes)
        // Offset 28: 32000 (byte rate = 16000 * 2, 4 bytes)
        // Offset 32: 2   (block align = 1 ch * 2 bytes, 2 bytes)
        // Offset 34: 16  (bits per sample, 2 bytes)
        // Offset 36: "data" (4)
        // Offset 40: dataLen (4)
        // Offset 44: pcmBytes (dataLen)

        bw.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        bw.Write((uint)(dataLen + 28));
        bw.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
        bw.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(16000);
        bw.Write(32000);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        bw.Write((uint)dataLen);
        bw.Write(pcmBytes);

        return Convert.ToBase64String(ms.ToArray());
    }
}
