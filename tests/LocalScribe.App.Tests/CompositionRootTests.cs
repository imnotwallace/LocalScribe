using LocalScribe.App;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class CompositionRootTests
{
    [Fact]
    public void Build_produces_an_idle_controller_and_expanded_paths()
    {
        var (controller, settings, paths) = CompositionRoot.Build();
        Assert.Equal(SessionState.Idle, controller.State);
        Assert.False(paths.Root.Contains('%'));          // env vars expanded by StoragePaths
        Assert.NotNull(settings);
    }
}
