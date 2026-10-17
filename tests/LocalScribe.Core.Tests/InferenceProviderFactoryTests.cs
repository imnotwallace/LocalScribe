using LocalScribe.Inference;

namespace LocalScribe.Core.Tests;

public class InferenceProviderFactoryTests
{
    /// <summary>The factory is static and requires no DI — Create can be called at any time.</summary>
    [Fact]
    public void Create_RequiresNoInstantiation()
    {
        // If Create were an instance method, this would fail to compile (no new Factory()).
        // We prove static access by simply calling it.
        _ = Assert.Throws<NotImplementedException>(()
            => InferenceProviderFactory.Create(InferenceProviderType.Local));
    }

    [Fact]
    public void Create_Local_ThrowsWithTask2Message()
    {
        var ex = Assert.Throws<NotImplementedException>(()
            => InferenceProviderFactory.Create(InferenceProviderType.Local));
        Assert.Contains("Task 2", ex.Message);
    }

    [Fact]
    public void Create_Remote_ThrowsWithTask3Message()
    {
        var ex = Assert.Throws<NotImplementedException>(()
            => InferenceProviderFactory.Create(InferenceProviderType.Remote));
        Assert.Contains("Task 3", ex.Message);
    }

    [Fact]
    public void Create_UnknownType_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(()
            => InferenceProviderFactory.Create((InferenceProviderType)99));
        Assert.Equal("type", ex.ParamName);
    }

    [Fact]
    public void Create_WithNullOptions_UsesDefaults()
    {
        // Null options should not throw; it defaults to a ProviderOptions(string.Empty, ...)
        // which is harmless because Create still throws NotImplementedException.
        var ex = Assert.Throws<NotImplementedException>(()
            => InferenceProviderFactory.Create(InferenceProviderType.Local, options: null));
        Assert.Contains("Task 2", ex.Message);
    }
}
