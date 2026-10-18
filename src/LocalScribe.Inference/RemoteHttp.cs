using System.Net;
using System.Net.Http.Headers;

namespace LocalScribe.Inference;

/// <summary>A remote endpoint failed in a way the user should be told about. The message names the
/// endpoint ORIGIN and the HTTP status, never the API key or the request body. Transient = worth
/// retrying (a timeout, an unreachable host, 429/502/503/504) as opposed to a refusal.</summary>
internal sealed class RemoteInferenceException(string message, bool transient = false) : Exception(message)
{
    public bool Transient { get; } = transient;
}

/// <summary>The helper's HTTP plumbing, shared by chat and transcription.</summary>
internal static class RemoteHttp
{
    /// <summary>Per-request time limit. Below the App's 5-minute inactivity watchdog, so a hung
    /// request fails here with a named reason before the App kills the process for silence.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Server certificates are validated by the OS, as for any HTTPS client - there is
    /// deliberately no override. Redirects are NOT followed: a redirect would re-send the API key
    /// and the user's data to an address the user never configured.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
        => new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,   // per request, via RequestTimeout - streams may run long
        };

    /// <summary>path starts "/v1/". A base URL that already ends in /v1 - the form OpenAI-style
    /// clients usually ask for - is accepted too, rather than becoming /v1/v1/... and a 404.</summary>
    public static Uri Url(RemoteEndpoint remote, string path)
    {
        string root = remote.Endpoint.TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3];
        return new Uri(root + path);
    }

    public static void Authorize(HttpRequestMessage request, RemoteEndpoint remote)
    {
        if (!string.IsNullOrWhiteSpace(remote.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", remote.ApiKey);
    }

    /// <summary>Throws RemoteInferenceException for any non-2xx response, carrying the status and
    /// a short excerpt of the server's own error text (which is what says WHY - a wrong model name,
    /// a bad key).</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, RemoteEndpoint remote,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body = "";
        try { body = (await response.Content.ReadAsStringAsync(ct)).Trim(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        if (body.Length > 300) body = body[..300] + "...";
        string redirect = (int)response.StatusCode is >= 300 and < 400
            ? " (the endpoint redirected; redirects are not followed - use the final address)"
            : "";
        throw new RemoteInferenceException(
            $"{remote.Origin} answered HTTP {(int)response.StatusCode} "
            + $"{response.ReasonPhrase}{redirect}{(body.Length > 0 ? ": " + body : "")}",
            IsTransient(response.StatusCode));
    }

    /// <summary>Worth one more try: the server or the network had a moment, not a refusal.</summary>
    public static bool IsTransient(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    public static RemoteInferenceException Unreachable(RemoteEndpoint remote, Exception ex)
        => new($"could not reach {remote.Origin}: {ex.Message}", transient: true);

    public static RemoteInferenceException TimedOut(RemoteEndpoint remote)
        => new($"{remote.Origin} sent nothing for "
            + $"{RequestTimeout.TotalMinutes:0} minutes", transient: true);
}
