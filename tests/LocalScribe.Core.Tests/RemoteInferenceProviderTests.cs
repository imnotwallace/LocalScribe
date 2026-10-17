using LocalScribe.Core.Assistant;
using LocalScribe.Inference;
using System.Net;

namespace LocalScribe.Core.Tests;

/// <summary>Canned HttpMessageHandler that returns a fixed response body with a given status code.</summary>
internal sealed class CannedHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody),
        };
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

public class RemoteInferenceProviderTests
{
    /// <summary>HealthCheck returns IsHealthy=true and extracts model IDs from the /v1/models response.</summary>
    [Fact]
    public async Task HealthCheck_ReturnsModelsFromEndpoint()
    {
        var modelsJson = """{"data":[{"id":"example-model"},{"id":"another-model"}]}""";
        var handler = new CannedHandler(modelsJson);
        var provider = new RemoteInferenceProvider("http://localhost:8080", null, "example-model", TimeSpan.FromSeconds(10), handler);

        var health = await provider.HealthCheckAsync();

        Assert.True(health.IsHealthy);
        Assert.Equal(2, health.AvailableModels.Count);
        Assert.Contains("example-model", health.AvailableModels);
        Assert.Contains("another-model", health.AvailableModels);
        Assert.Null(health.Error);
    }

    /// <summary>HealthCheck returns IsHealthy=false when the endpoint returns a non-200 status.</summary>
    [Fact]
    public async Task HealthCheck_ReturnsUnhealthy_OnHttpError()
    {
        var handler = new CannedHandler("", HttpStatusCode.BadGateway);
        var provider = new RemoteInferenceProvider("http://localhost:8080", null, null, TimeSpan.FromSeconds(10), handler);

        var health = await provider.HealthCheckAsync();

        Assert.False(health.IsHealthy);
        Assert.Contains("502", health.Error!);
    }

    /// <summary>HealthCheck returns IsHealthy=false when the endpoint is unreachable.</summary>
    [Fact]
    public async Task HealthCheck_ReturnsUnhealthy_OnNetworkError()
    {
        var handler = new FailingHandler();
        var provider = new RemoteInferenceProvider("http://localhost:9999", null, null, TimeSpan.FromSeconds(1), handler);

        var health = await provider.HealthCheckAsync();

        Assert.False(health.IsHealthy);
        Assert.NotNull(health.Error);
        Assert.Contains("Connection failed", health.Error);
    }

    /// <summary>InferAsync accumulates SSE chunks into a single text result.</summary>
    [Fact]
    public async Task InferAsync_AccumulatesSseChunks()
    {
        var sseStream = """
            data: {"choices":[{"delta":{"content":"Hello"}}]}
            data: {"choices":[{"delta":{"content":" world"}}]}
            data: {"choices":[{"delta":{"content":"!"}}]}
            data: [DONE]
            """;
        var handler = new CannedHandler(sseStream);
        var provider = new RemoteInferenceProvider("http://localhost:8080", null, "example-model", TimeSpan.FromSeconds(10), handler);

        var result = await provider.InferAsync(new InferenceRequest(
            Messages: [new Message("user", "Say hello")],
            Temperature: 0.7,
            MaxTokens: 100,
            Options: null!),
            CancellationToken.None);

        Assert.Equal("Hello world!", result.Text);
        Assert.Equal("Remote", result.ProviderType);
        Assert.Equal("http://localhost:8080", result.ProviderInfo);
    }

    /// <summary>InferAsync sends ApiKey in the Authorization header.</summary>
    [Fact]
    public async Task InferAsync_SendsApiKeyInHeader()
    {
        var handler = new CapturingHandler("data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\ndata: [DONE]");
        var provider = new RemoteInferenceProvider("http://localhost:8080", "my-secret-key", "example-model", TimeSpan.FromSeconds(10), handler);

        await provider.InferAsync(new InferenceRequest(
            Messages: [new Message("user", "test")],
            Temperature: 0,
            MaxTokens: 10,
            Options: null!),
            CancellationToken.None);

        // Verify the request had the Authorization header.
        var request = handler.LastRequest!;
        Assert.True(request.Headers.Authorization != null);
        Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
        Assert.Equal("my-secret-key", request.Headers.Authorization.Parameter);
    }

    /// <summary>InferAsync throws AssistantException on 5xx HTTP error.</summary>
    [Fact]
    public async Task InferAsync_ThrowsOn5xx()
    {
        var handler = new CannedHandler("Internal Server Error", HttpStatusCode.InternalServerError);
        var provider = new RemoteInferenceProvider("http://localhost:8080", null, "example-model", TimeSpan.FromSeconds(10), handler);

        var ex = await Assert.ThrowsAsync<AssistantException>(() =>
            provider.InferAsync(new InferenceRequest(
                Messages: [new Message("user", "test")],
                Temperature: 0, MaxTokens: 10, Options: null!),
                CancellationToken.None));

        Assert.Contains("http://localhost:8080", ex.Message);
        Assert.Contains("500", ex.Message);
    }

    /// <summary>InferAsync throws AssistantException on 4xx HTTP error.</summary>
    [Fact]
    public async Task InferAsync_ThrowsOn4xx()
    {
        var handler = new CannedHandler("model not found", HttpStatusCode.NotFound);
        var provider = new RemoteInferenceProvider("http://localhost:8080", null, "example-model", TimeSpan.FromSeconds(10), handler);

        var ex = await Assert.ThrowsAsync<AssistantException>(() =>
            provider.InferAsync(new InferenceRequest(
                Messages: [new Message("user", "test")],
                Temperature: 0, MaxTokens: 10, Options: null!),
                CancellationToken.None));

        Assert.Contains("http://localhost:8080", ex.Message);
        Assert.Contains("404", ex.Message);
    }

    /// <summary>ChatML wrapping: the remote provider wraps messages in ChatML format.</summary>
    [Fact]
    public async Task InferAsync_WrapsMessagesInChatML()
    {
        var handler = new CapturingHandler("data: [DONE]");
        var provider = new RemoteInferenceProvider("http://localhost:8080", null, "example-model", TimeSpan.FromSeconds(10), handler);

        await provider.InferAsync(new InferenceRequest(
            Messages:
            [
                new Message("user", "What is AI?"),
                new Message("assistant", "AI is artificial intelligence."),
                new Message("user", "Give a definition."),
            ],
            Temperature: 0.5,
            MaxTokens: 50,
            Options: null!),
            CancellationToken.None);

        // Verify the request body contains ChatML markers.
        var body = handler.LastRequestBody!;
        Assert.Contains("<|begin_of_text|>", body);
        Assert.Contains("<|start_header_id|>user<|end_header_id|>", body);
        Assert.Contains("<|start_header_id|>assistant<|end_header_id|>", body);
        Assert.Contains("<|eot_id|>", body);
    }

    /// <summary>DisposeAsync closes the HttpClient.</summary>
    [Fact]
    public async Task DisposeAsync_NoError()
    {
        var handler = new CannedHandler("data: [DONE]");
        var provider = new RemoteInferenceProvider("http://localhost:8080", null, "example-model", TimeSpan.FromSeconds(10), handler);

        await provider.DisposeAsync();
        // If DisposeAsync threw, the test would fail.
    }
}

/// <summary>HttpMessageHandler that always throws a connection error.</summary>
internal sealed class FailingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => throw new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable);
}

/// <summary>HttpMessageHandler that captures the request body for verification.</summary>
internal sealed class CapturingHandler(string responseBody) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastRequestBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody),
        };
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}
