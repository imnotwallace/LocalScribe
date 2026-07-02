using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class LevelMeterTests
{
    [Fact]
    public void Observe_raises_value_and_tick_decays_to_zero()
    {
        var meter = new LevelMeter();
        meter.Observe(0.4f);
        Assert.True(meter.Value >= 0.9);              // gained: speech lights the bar
        for (int i = 0; i < 20; i++) meter.Tick();
        Assert.Equal(0, meter.Value);                 // decayed and floored
    }

    [Fact]
    public void Observe_keeps_the_max_until_decay()
    {
        var meter = new LevelMeter();
        meter.Observe(0.5f);
        double v1 = meter.Value;
        meter.Observe(0.1f);
        Assert.Equal(v1, meter.Value);                // a quieter frame never lowers the bar
    }
}
