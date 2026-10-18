// Remote transcription helper: calls an OpenAI-compatible /v1/audio/transcriptions HTTP
// endpoint (e.g. a self-hosted faster-whisper or whisper.cpp server). Sends base64-encoded
// WAV audio per segment, receives verbose_json with text + detected language.
//
// All network code for remote transcription lives here — LocalScribe.App and Core never
// touch HttpClient for transcription. The NoNetworkInAppOrCoreTests grep only covers those
// two projects; Inference is allowed to use HttpClient (RemoteInferenceProvider already does).
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalScribe.Core.Transcription;

namespace LocalScribe.Inference;

/// <summary>HTTP client for a remote Whisper-compatible transcription API.
/// Configurable endpoint, API key, model name, and timeout. Lazy connection on first call.</summary>
internal sealed class RemoteTranscriptionHelper
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly string? _model;
    private readonly TimeSpan _timeout;

    /// <summary>Create a remote transcription helper. Does NOT connect — the first TranscribeAsync
    /// call probes the endpoint on-demand.</summary>
    /// <param name="endpoint">Base URL (e.g. "http://localhost:8080").</param>
    /// <param name="apiKey">Optional Bearer token.</param>
    /// <param name="model">Model name to send in the request (e.g. "large-v3-turbo").</param>
    /// <param name="timeout">Request timeout (default 300s for long audio).</param>
    public RemoteTranscriptionHelper(string endpoint, string? apiKey, string? model, TimeSpan timeout)
    {
        _endpoint = endpoint.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        _timeout = timeout;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };

        _http = new HttpClient(handler);
        _http.Timeout = timeout;
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>Transcribe a single audio segment by sending its base64-encoded WAV to the remote API.</summary>
    /// <param name="audioBase64">Base64-encoded 16 kHz mono WAV bytes.</param>
    /// <param name="language">Language code (e.g. "en") or null for auto-detect.</param>
    /// <param name="temperature">Temperature for decoding (typically 0.0 for transcription).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transcription result with text, detected language, and no-speech probability.</returns>
    public async Task<TranscriptionResult> TranscribeAsync(
        string audioBase64, string? language, double temperature, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model ?? "",
            ["language"] = string.IsNullOrWhiteSpace(language) ? (JsonNode?)null : language,
            ["temperature"] = temperature,
            ["response_format"] = "verbose_json",
            ["audio"] = audioBase64,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(body,
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _http.PostAsync($"{_endpoint}/v1/audio/transcriptions", content, ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new RemoteTranscriptionException(
                $"Remote transcription failed at {_endpoint}: HTTP {(int)response.StatusCode} {response.StatusCode} ({errorBody.Trim()}). "
                + "Check that the endpoint is running and the model name is correct.");
        }

        var jsonResponse = await response.Content.ReadAsStringAsync(ct);
        return ParseResult(jsonResponse, sw.Elapsed);
    }

    /// <summary>Health-check the remote endpoint: GET /v1/models. Returns true if the endpoint responds.</summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{_endpoint}/v1/models", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Parse the verbose_json response from the remote Whisper API.</summary>
    private static TranscriptionResult ParseResult(string jsonResponse, TimeSpan elapsed)
    {
        var parsed = JsonNode.Parse(jsonResponse)!;

        var text = parsed["text"]?.GetValue<string>() ?? "";
        var language = parsed["language"]?.GetValue<string>();
        var noSpeechProb = parsed["no_speech_prob"]?.GetValue<double?>();

        return new TranscriptionResult(text, language, noSpeechProb);
    }
}

/// <summary>Exception thrown when the remote transcription API returns an error.</summary>
public sealed class RemoteTranscriptionException : Exception
{
    public RemoteTranscriptionException(string message) : base(message) { }
    public RemoteTranscriptionException(string message, Exception inner) : base(message, inner) { }
}
