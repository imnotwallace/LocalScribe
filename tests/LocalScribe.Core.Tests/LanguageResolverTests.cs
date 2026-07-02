using LocalScribe.Core.Transcription;

public class LanguageResolverTests
{
    [Fact]
    public void Fixed_setting_locks_immediately()
    {
        var r = new LanguageResolver("de");
        Assert.True(r.IsLocked);
        Assert.Equal("de", r.Locked);
        Assert.False(r.UseEnglishOnlyModel);
    }

    [Fact]
    public void Auto_locks_on_majority_of_first_three_detections()
    {
        var r = new LanguageResolver("auto");
        Assert.False(r.IsLocked);
        r.Observe("en");
        r.Observe("de");
        Assert.False(r.IsLocked);
        r.Observe("en");
        Assert.True(r.IsLocked);
        Assert.Equal("en", r.Locked);
        Assert.True(r.UseEnglishOnlyModel);
    }

    [Fact]
    public void Null_detections_do_not_consume_probe_slots()
    {
        var r = new LanguageResolver("auto");
        r.Observe(null);
        r.Observe(null);
        r.Observe("en");
        r.Observe("en");
        Assert.False(r.IsLocked);                       // only 2 real observations so far
        r.Observe("en");
        Assert.True(r.IsLocked);
    }

    [Fact]
    public void Observations_after_lock_are_ignored()
    {
        var r = new LanguageResolver("auto", probeCount: 1);
        r.Observe("en");
        Assert.True(r.IsLocked);
        r.Observe("de");
        Assert.Equal("en", r.Locked);                   // locked stays locked (v1 non-goal)
    }
}
