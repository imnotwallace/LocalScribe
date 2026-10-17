using LocalScribe.Inference;

namespace LocalScribe.Core.Tests;

public class InferenceProviderFactoryTests
{
    [Fact]
    public void Create_RequiresNoInstantiation()
    {
        // Static factory — no new needed.
        _ = Assert.Throws<NotImplementedException>(()
            => InferenceProviderFactory.Create(InferenceProviderType.Remote));
    }

    [Fact]
    public void Create_Local_ReturnsWorkingProvider()
    {
        // Local provider is created but does NOT load the model yet (lazy load).
        var provider = InferenceProviderFactory.Create(
            InferenceProviderType.Local,
            modelPath: @"C:\fake\model.gguf",
            ctxTokens: 4096,
            backend: "cpu",
            progress: _ => { });

        Assert.IsAssignableFrom<IInferenceProvider>(provider);
    }

    [Fact]
    public async Task Create_Local_HealthCheck_ReturnsHealthy()
    {
        var provider = InferenceProviderFactory.Create(
            InferenceProviderType.Local,
            modelPath: @"C:\fake\model.gguf",
            ctxTokens: 4096,
            backend: "cpu",
            progress: _ => { });

        var health = await provider.HealthCheckAsync();
        Assert.True(health.IsHealthy);
        Assert.Empty(health.AvailableModels);
        Assert.Null(health.Error);
    }

    [Fact]
    public async Task Create_Local_DisposeAsync_NoError()
    {
        var provider = InferenceProviderFactory.Create(
            InferenceProviderType.Local,
            modelPath: @"C:\fake\model.gguf",
            ctxTokens: 4096,
            backend: "cpu",
            progress: _ => { });

        // Dispose on an unloaded provider (model never loaded) should be a no-op.
        await provider.DisposeAsync();
    }

    [Fact]
    public void Create_Remote_ThrowsWithSignatureMessage()
    {
        var ex = Assert.Throws<NotImplementedException>(()
            => InferenceProviderFactory.Create(InferenceProviderType.Remote));
        Assert.Contains("Remote options not passed via this signature", ex.Message);
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

    [Fact]
    public void Create_UnknownType_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(()
            => InferenceProviderFactory.Create((InferenceProviderType)99));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public void Create_NullProgress_UsesDefaultCallback()
    {
        // Null progress should not throw; it defaults to a no-op lambda.
        var provider = InferenceProviderFactory.Create(
            InferenceProviderType.Local,
            modelPath: @"C:\fake\model.gguf",
            ctxTokens: 4096,
            backend: "cpu",
            progress: null);

        var health = Assert.IsAssignableFrom<IInferenceProvider>(provider);
        Assert.NotNull(health);
    }
}
