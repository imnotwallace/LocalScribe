using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

public class NameResolverOverrideTests
{
    private static TranscriptLine Seg() =>
        TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "hi", "Them");

    [Fact]
    public void ParticipantOverride_ReturnsParticipantName()
    {
        var meta = SessionMeta.CreateDefault(AppKind.Manual, DateTimeOffset.UtcNow, self: null) with
        {
            Participants =
            [
                new SessionParticipant { Id = "p-2", Name = "Ms. Adams", Side = SourceKind.Remote,
                    Kind = ParticipantKind.Named },
            ],
        };
        Assert.Equal("Ms. Adams", NameResolver.Resolve(Seg(), speakers: null, meta,
            participantIdOverride: "p-2", clusterKeyOverride: null));
    }

    [Fact]
    public void ClusterOverride_ResolvesViaNamesOverlay()
    {
        var meta = SessionMeta.CreateDefault(AppKind.Manual, DateTimeOffset.UtcNow, self: null);
        var speakers = new Speakers
        {
            Names = new Dictionary<string, string> { ["Remote:4"] = "Detected Voice B" },
        };
        Assert.Equal("Detected Voice B", NameResolver.Resolve(Seg(), speakers, meta,
            participantIdOverride: null, clusterKeyOverride: "Remote:4"));
    }

    [Fact]
    public void NoOverride_MatchesLegacyResolve()
    {
        var meta = SessionMeta.CreateDefault(AppKind.Manual, DateTimeOffset.UtcNow, self: null);
        Assert.Equal(NameResolver.Resolve(Seg(), null, meta),
            NameResolver.Resolve(Seg(), null, meta, null, null));
    }
}
