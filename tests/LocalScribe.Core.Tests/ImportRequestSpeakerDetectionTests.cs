using LocalScribe.Core.Import;

namespace LocalScribe.Core.Tests;

/// <summary>Import-time speaker detection request shape (design 2026-07-28 section 2).
/// SpeakerDetection/SpeakerCount are private-init: the only way to set them is
/// <see cref="ImportRequest.WithSpeakerDetection"/>, which validates the pair once, together,
/// rather than the two properties independently. Round 1 of task-1 review proved independent
/// per-property validation is unsound - see task-1-report.md for the full analysis - because a
/// record `with` expression runs each named member's init accessor sequentially in written order,
/// so any per-property eager check sees a stale sibling and either rejects a valid pair (forward
/// order) or admits an invalid one (a Declared mode with the count never mentioned at all).
/// Making the properties private-init removes the unsound path entirely rather than patching it.
/// The count validation itself is load-bearing, not defensive: SpeakerClustering.Cluster (Core,
/// in-house clustering, 2026-08-02) treats forcedClusterCount as trusted input - null runs the
/// auto silhouette scan, and 0 or below silently clamps (Math.Clamp floors at 1) to a forced
/// single cluster - so an unvalidated pair would silently do the wrong thing instead of surfacing
/// the request's mistake. These tests keep that unreachable.</summary>
public sealed class ImportRequestSpeakerDetectionTests
{
    private static ImportRequest Base() => new()
    {
        SourcePath = @"C:\x\a.wav",
        Title = "T",
        RecordedAtLocal = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Defaults_to_off_with_no_count()
    {
        var r = Base();
        Assert.Equal(SpeakerDetection.Off, r.SpeakerDetection);
        Assert.Null(r.SpeakerCount);
    }

    [Fact]
    public void Auto_carries_no_count()
    {
        var r = Base().WithSpeakerDetection(SpeakerDetection.Auto);
        Assert.Equal(SpeakerDetection.Auto, r.SpeakerDetection);
        Assert.Null(r.SpeakerCount);
    }

    [Fact]
    public void Declared_accepts_two_or_more()
    {
        var r = Base().WithSpeakerDetection(SpeakerDetection.Declared, 3);
        Assert.Equal(SpeakerDetection.Declared, r.SpeakerDetection);
        Assert.Equal(3, r.SpeakerCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void Declared_rejects_a_count_below_two(int? count)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Base().WithSpeakerDetection(SpeakerDetection.Declared, count));
        Assert.Contains("SpeakerCount", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SpeakerDetection.Off)]
    [InlineData(SpeakerDetection.Auto)]
    public void A_count_without_declared_is_rejected(SpeakerDetection mode)
    {
        Assert.Throws<ArgumentException>(() => Base().WithSpeakerDetection(mode, 2));
    }

    /// <summary>Finding 2 from round 1 of review: a plain object initializer could previously reach
    /// `(Declared, null)` because SpeakerCount was never mentioned, so no accessor ever ran to
    /// catch it. That silently flows into SpeakerClustering.Cluster (Core, in-house clustering,
    /// 2026-08-02) as a null forcedClusterCount - the auto silhouette-scan path - while the
    /// request claims Declared. With the factory as the sole entry
    /// point there is no "count omitted" shape left to construct: the count parameter must be
    /// supplied and is checked every time.</summary>
    [Fact]
    public void Declared_with_the_count_never_mentioned_is_unreachable()
    {
        Assert.Throws<ArgumentException>(() => Base().WithSpeakerDetection(SpeakerDetection.Declared));
    }

    /// <summary>Finding 1 from round 1 of review: independent init-property validation made
    /// `with { SpeakerCount = 3, SpeakerDetection = Declared }` (count-first) throw even though the
    /// final combination is valid, because SpeakerCount's accessor ran first and saw a stale
    /// SpeakerDetection. That hazard cannot arise here: WithSpeakerDetection is the only entry
    /// point and sets both properties from one already-validated pair in a single `with`, so there
    /// is no order for a caller to get wrong. This asserts a round-trip through the factory
    /// survives an unrelated `with` on another member (Title), confirming the private-init
    /// properties are otherwise ordinary record members - only the *combination* of
    /// SpeakerDetection/SpeakerCount is order-sensitive-turned-order-proof, nothing else.</summary>
    [Fact]
    public void Ordering_is_not_a_concern_because_the_factory_is_the_only_entry_point()
    {
        var r = Base().WithSpeakerDetection(SpeakerDetection.Declared, 3) with { Title = "X" };
        Assert.Equal(SpeakerDetection.Declared, r.SpeakerDetection);
        Assert.Equal(3, r.SpeakerCount);
        Assert.Equal("X", r.Title);
    }

    [Fact]
    public void DetectSpeakers_is_a_distinct_stage()
    {
        Assert.NotEqual(ImportStage.Save, ImportStage.DetectSpeakers);
    }
}
