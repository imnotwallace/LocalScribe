using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class AppMuteBannerEvaluatorTests
{
    private static AppMuteReading Muted() => new(AppMuteState.Muted, "Webex");
    private static AppMuteReading Live() => new(AppMuteState.Live, "Webex");
    private static AppMuteReading Unknown() => new(AppMuteState.Unknown, null);

    [Fact]
    public void Mismatch_shows_only_after_the_debounce()
    {
        var e = new AppMuteBannerEvaluator();
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Muted(), localMuted: false, nowMs: 0));
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Muted(), false, 4999));
        Assert.Equal(AppMuteBannerKind.AppMutedButRecording, e.Evaluate(Muted(), false, 5000));
    }

    [Fact]
    public void Resolution_clears_immediately()
    {
        var e = new AppMuteBannerEvaluator();
        e.Evaluate(Muted(), false, 0);
        e.Evaluate(Muted(), false, 6000);                       // shown
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Muted(), true, 6100));   // user muted LS: agree now
    }

    [Fact]
    public void Unknown_never_banners_and_resets_pending()
    {
        var e = new AppMuteBannerEvaluator();
        e.Evaluate(Muted(), false, 0);
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Unknown(), false, 4000));
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Muted(), false, 4500)); // pending restarted
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Muted(), false, 9000)); // 4.5s into NEW window
        Assert.Equal(AppMuteBannerKind.AppMutedButRecording, e.Evaluate(Muted(), false, 9500));
    }

    [Fact]
    public void Opposite_mismatch_direction_banners_after_its_own_debounce()
    {
        var e = new AppMuteBannerEvaluator();
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Live(), localMuted: true, 0));
        Assert.Equal(AppMuteBannerKind.AppLiveButMuted, e.Evaluate(Live(), true, 5000));
    }

    [Fact]
    public void Direction_flip_restarts_the_debounce()
    {
        var e = new AppMuteBannerEvaluator();
        e.Evaluate(Muted(), false, 0);
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Live(), true, 3000));   // flipped mid-window
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Live(), true, 7999));   // 4999 into new window
        Assert.Equal(AppMuteBannerKind.AppLiveButMuted, e.Evaluate(Live(), true, 8000));
    }

    [Fact]
    public void Direction_flip_while_a_banner_is_showing_clears_it_immediately_then_debounces_the_opposite()
    {
        var e = new AppMuteBannerEvaluator();
        e.Evaluate(Muted(), false, 0);
        Assert.Equal(AppMuteBannerKind.AppMutedButRecording, e.Evaluate(Muted(), false, 5000)); // shown
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Live(), true, 5100));                   // opposite mismatch: old banner clears NOW (not stale for 5s)
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Live(), true, 10099));                  // 4999 into the new window
        Assert.Equal(AppMuteBannerKind.AppLiveButMuted, e.Evaluate(Live(), true, 10100));       // opposite banners after ITS own 5s
    }
}
