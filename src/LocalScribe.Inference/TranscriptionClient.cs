using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace LocalScribe.Inference;

/// <summary>OpenAI-compatible POST /v1/audio/transcriptions for ONE VAD segment: a multipart upload
/// of the WAV plus model, language (when locked), prompt (the session vocabulary, when any) and
/// response_format=verbose_json, which is what carries the detected language and the no-speech
/// probabilities the worker's language lock and hallucination gate need.
///
/// A long re-transcription is hundreds of requests, so a TRANSIENT failure (timeout, unreachable,
/// 429/502/503/504) is retried with backoff before the run is failed. A refusal (401, 404, 400 -
/// a wrong key, endpoint or model) is not: retrying cannot fix it.</summary>
internal sealed class TranscriptionClient(HttpClient http, RemoteEndpoint remote, Func<int, TimeSpan>? backoff = null)
{
    public const int MaxAttempts = 3;

    private readonly Func<int, TimeSpan> _backoff = backoff ?? (attempt => TimeSpan.FromSeconds(2 * attempt));

    /// <param name="onRetry">Called with the attempt number that failed, before waiting - the
    /// helper turns it into a progress event so the App's inactivity watchdog sees activity.</param>
    public async Task<Transcription> TranscribeAsync(byte[] wav, string? language, string? prompt,
        Action<int>? onRetry, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await SendOnceAsync(wav, language, prompt, ct); }
            catch (RemoteInferenceException ex) when (ex.Transient && attempt < MaxAttempts)
            {
                onRetry?.Invoke(attempt);
                await Task.Delay(_backoff(attempt), ct);
            }
        }
    }

    private async Task<Transcription> SendOnceAsync(byte[] wav, string? language, string? prompt,
        CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "segment.wav");
        form.Add(new StringContent(remote.Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        if (!string.IsNullOrEmpty(language)) form.Add(new StringContent(language), "language");
        if (!string.IsNullOrEmpty(prompt)) form.Add(new StringContent(prompt), "prompt");

        using var request = new HttpRequestMessage(HttpMethod.Post, RemoteHttp.Url(remote, "/v1/audio/transcriptions"))
        {
            Content = form,
        };
        RemoteHttp.Authorize(request, remote);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RemoteHttp.RequestTimeout);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw RemoteHttp.TimedOut(remote); }
        catch (HttpRequestException ex) { throw RemoteHttp.Unreachable(remote, ex); }

        using (response)
        {
            await RemoteHttp.EnsureSuccessAsync(response, remote, ct);
            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
    }

    /// <summary>verbose_json: top-level text + language; per-segment no_speech_prob, of which the
    /// MINIMUM is kept (the WhisperNetEngine rule: one confident speech segment must win). A plain
    /// {"text":...} answer still parses - it just carries no language or no-speech signal.</summary>
    internal static Transcription Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException)
        {
            throw new RemoteInferenceException("the transcription endpoint did not return JSON");
        }
        string text = (HelperWire.Str(root?["text"]) ?? "").Trim();
        double? minNoSpeech = null;
        if (root?["segments"] is JsonArray segments)
            foreach (JsonNode? segment in segments)
                if (HelperWire.Dbl(segment?["no_speech_prob"]) is { } p)
                    minNoSpeech = minNoSpeech is null ? p : Math.Min(minNoSpeech.Value, p);
        return new Transcription(text, WhisperLanguages.ToCode(HelperWire.Str(root?["language"])), minNoSpeech);
    }
}
