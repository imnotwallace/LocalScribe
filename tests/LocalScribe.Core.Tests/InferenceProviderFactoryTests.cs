using LocalScribe.Inference;

namespace LocalScribe.Core.Tests;

public class InferenceProviderFactoryTests
{
    [Fact]
    public void CreateLocal_ReturnsWorkingProvider()
    {
        // Local provider is created but does NOT load the model yet (lazy load).
        // The engine factory is only called on first InferAsync.
        var provider = InferenceProviderFactory.CreateLocal(() => new object());

        Assert.IsAssignableFrom<IInferenceProvider>(provider);
    }

    [Fact]
    public async Task CreateLocal_HealthCheck_ReturnsHealthy()
    {
        var provider = InferenceProviderFactory.CreateLocal(() => new object());

        var health = await provider.HealthCheckAsync();
        Assert.True(health.IsHealthy);
        Assert.Empty(health.AvailableModels);
        Assert.Null(health.Error);
    }

    [Fact]
    public async Task CreateLocal_DisposeAsync_NoError()
    {
        var provider = InferenceProviderFactory.CreateLocal(() => new object());

        // Dispose on an unloaded provider (model never loaded) should be a no-op.
        await provider.DisposeAsync();
    }

    [Fact]
    public void CreateRemote_ReturnsWorkingProvider()
    {
        // Task 3 complete: CreateRemote convenience method works.
        var provider = InferenceProviderFactory.CreateRemote(
            endpoint: "http://localhost:8080",
            apiKey: null,
            model: "example-model",
            timeout: TimeSpan.FromSeconds(30));

        Assert.IsAssignableFrom<IInferenceProvider>(provider);
    }
}
