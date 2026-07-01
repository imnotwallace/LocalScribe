using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;

public class NameResolverTests
{
    private static SessionMeta Meta(int local, int remote, params SessionParticipant[] ps) => new()
    { LocalCount = local, RemoteCount = remote, Participants = ps };

    private static TranscriptLine Seg(int seq, TranscriptSource src, string label) =>
        TranscriptLine.Segment(seq, src, 0, 1, "text", label);

    [Fact]
    public void Assignment_to_named_cluster_wins()
    {
        var speakers = new Speakers
        {
            Names = new Dictionary<string, string> { ["Remote:2"] = "Bob" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["17"] = "Remote:2" } },
        };
        Assert.Equal("Bob", NameResolver.Resolve(Seg(17, TranscriptSource.Remote, "Them"),
            speakers, Meta(1, 2)));
    }

    [Fact]
    public void Assignment_to_unnamed_cluster_renders_speaker_n()
    {
        var speakers = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["19"] = "Remote:3" } },
        };
        Assert.Equal("Speaker 3", NameResolver.Resolve(Seg(19, TranscriptSource.Remote, "Them"),
            speakers, Meta(1, 2)));
    }

    [Fact]
    public void Single_declared_participant_supplies_the_name_without_diarisation()
    {
        var meta = Meta(1, 1,
            new SessionParticipant { Id = "p-a", Name = "Alice Client", Side = SourceKind.Remote });
        Assert.Equal("Alice Client", NameResolver.Resolve(Seg(5, TranscriptSource.Remote, "Them"),
            speakers: null, meta));
    }

    [Fact]
    public void Multi_declared_side_falls_through_to_baseline_even_with_one_listed_participant()
    {
        var meta = Meta(1, 2,
            new SessionParticipant { Id = "p-a", Name = "Alice Client", Side = SourceKind.Remote });
        Assert.Equal("Them", NameResolver.Resolve(Seg(5, TranscriptSource.Remote, "Them"),
            speakers: null, meta));
    }

    [Fact]
    public void Terminal_fallback_is_baseline_label_then_derived()
    {
        Assert.Equal("Me", NameResolver.Resolve(Seg(1, TranscriptSource.Local, "Me"), null, Meta(2, 2)));
        // empty label -> derived from source
        Assert.Equal("Them", NameResolver.Resolve(Seg(2, TranscriptSource.Remote, ""), null, Meta(2, 2)));
    }
}
