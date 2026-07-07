using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MatterSelectionOverrideTests
{
    [Fact]
    public void Default_is_empty_and_set_round_trips()
    {
        var seam = new MatterSelectionOverride();
        Assert.Empty(seam.MatterIds);
        seam.MatterIds = new[] { "M-1", "M-2" };
        Assert.Equal(new[] { "M-1", "M-2" }, seam.MatterIds);
        seam.MatterIds = [];
        Assert.Empty(seam.MatterIds);
    }
}
