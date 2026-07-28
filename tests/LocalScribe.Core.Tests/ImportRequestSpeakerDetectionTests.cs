using LocalScribe.Core.Import;

namespace LocalScribe.Core.Tests;

/// <summary>Import-time speaker detection request shape (design 2026-07-28 section 2).
/// The count validation is load-bearing, not defensive: SherpaDiarisationRunner.cs:23 branches on
/// `forcedClusterCount is int k && k > 0`, so an unvalidated 0 would silently take the AUTO
/// threshold path while the dialog claimed it forced a count. These tests keep that unreachable.</summary>
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
        var r = Base() with { SpeakerDetection = SpeakerDetection.Auto };
        Assert.Equal(SpeakerDetection.Auto, r.SpeakerDetection);
        Assert.Null(r.SpeakerCount);
    }

    [Fact]
    public void Declared_accepts_two_or_more()
    {
        var r = Base() with { SpeakerDetection = SpeakerDetection.Declared, SpeakerCount = 3 };
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
            Base() with { SpeakerDetection = SpeakerDetection.Declared, SpeakerCount = count });
        Assert.Contains("SpeakerCount", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SpeakerDetection.Off)]
    [InlineData(SpeakerDetection.Auto)]
    public void A_count_without_declared_is_rejected(SpeakerDetection mode)
    {
        Assert.Throws<ArgumentException>(() =>
            Base() with { SpeakerDetection = mode, SpeakerCount = 2 });
    }

    [Fact]
    public void DetectSpeakers_is_a_distinct_stage()
    {
        Assert.NotEqual(ImportStage.Save, ImportStage.DetectSpeakers);
    }
}
