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

    [Fact]
    public void Owned_cluster_uses_participant_name_over_speakers_names()
    {
        var speakers = new Speakers
        {
            Names = new Dictionary<string, string> { ["Remote:2"] = "Remote Speaker 3" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["17"] = "Remote:2" } },
        };
        var meta = Meta(1, 2, new SessionParticipant
        { Id = "p-bob", Name = "Bob Barrister", Side = SourceKind.Remote, ClusterKey = "Remote:2" });
        Assert.Equal("Bob Barrister",
            NameResolver.Resolve(Seg(17, TranscriptSource.Remote, "Them"), speakers, meta));
    }

    [Fact]
    public void Renaming_the_owning_participant_relabels_lines_without_touching_speakers_json()
    {
        var speakers = new Speakers
        {
            Names = new Dictionary<string, string> { ["Remote:2"] = "Bob Barrister" },  // stale overlay
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["17"] = "Remote:2" } },
        };
        var renamed = Meta(1, 2, new SessionParticipant
        { Id = "p-bob", Name = "Robert Barrister", Side = SourceKind.Remote, ClusterKey = "Remote:2" });

        // The meta-side rename wins immediately; speakers.json was never rewritten.
        Assert.Equal("Robert Barrister",
            NameResolver.Resolve(Seg(17, TranscriptSource.Remote, "Them"), speakers, renamed));
        Assert.Equal("Bob Barrister", speakers.Names["Remote:2"]);
    }

    [Fact]
    public void Unnamed_owner_falls_through_to_speakers_names_then_derived_label()
    {
        var assignments = new Dictionary<string, Dictionary<string, string>>
        { ["Remote"] = new() { ["19"] = "Remote:3" } };
        var meta = Meta(1, 2, new SessionParticipant
        { Id = "p-u1", Name = "", Side = SourceKind.Remote, Kind = ParticipantKind.Unnamed, ClusterKey = "Remote:3" });

        // An Unnamed slot has no name to project (design 5.2: unnamed slots render "Speaker N"),
        // so the overlay tier keeps working...
        var withOverlay = new Speakers
        { Names = new Dictionary<string, string> { ["Remote:3"] = "Remote Speaker 4" }, Assignments = assignments };
        Assert.Equal("Remote Speaker 4",
            NameResolver.Resolve(Seg(19, TranscriptSource.Remote, "Them"), withOverlay, meta));

        // ...and with no overlay name the derived per-cluster label appears.
        var withoutOverlay = new Speakers { Assignments = assignments };
        Assert.Equal("Speaker 3",
            NameResolver.Resolve(Seg(19, TranscriptSource.Remote, "Them"), withoutOverlay, meta));
    }

    [Fact]
    public void Ownership_applies_only_to_lines_with_a_resolved_assignment()
    {
        // A dangling ClusterKey (no assignment for this line) must not label anything - the
        // line falls through to the declared-count/baseline tiers exactly as before.
        var meta = Meta(1, 2, new SessionParticipant
        { Id = "p-bob", Name = "Bob Barrister", Side = SourceKind.Remote, ClusterKey = "Remote:2" });
        Assert.Equal("Them", NameResolver.Resolve(Seg(5, TranscriptSource.Remote, "Them"), null, meta));
    }

    [Fact]
    public void Unnamed_only_side_with_declared_one_falls_to_baseline()
    {
        // declared==1 satisfied by a single UNNAMED slot: there is no name to project - the
        // pre-5.4 code would have returned the slot's empty Name. Baseline is the honest label.
        var meta = Meta(1, 1, new SessionParticipant
        { Id = "p-u1", Name = "", Side = SourceKind.Remote, Kind = ParticipantKind.Unnamed });
        Assert.Equal("Them", NameResolver.Resolve(Seg(5, TranscriptSource.Remote, "Them"), null, meta));
    }

    [Fact]
    public void Two_named_slots_with_declared_one_fall_to_baseline_not_arbitrary_first()
    {
        // Inconsistent meta (declared==1 but two Named slots on the side): never attribute
        // lines to whichever slot happens to be listed first - evidentiary honesty over guessing.
        var meta = Meta(1, 1,
            new SessionParticipant { Id = "p-a", Name = "Alice Client", Side = SourceKind.Remote },
            new SessionParticipant { Id = "p-b", Name = "Bea Witness", Side = SourceKind.Remote });
        Assert.Equal("Them", NameResolver.Resolve(Seg(5, TranscriptSource.Remote, "Them"), null, meta));
    }

    [Fact]
    public void Unnamed_slots_do_not_block_the_lone_named_slot_when_declared_is_one()
    {
        // Only Kind==Named slots count toward "exactly one": a stray Unnamed row must not
        // suppress the lone named participant's label while declared is still 1.
        var meta = Meta(1, 1,
            new SessionParticipant { Id = "p-a", Name = "Alice Client", Side = SourceKind.Remote },
            new SessionParticipant { Id = "p-u1", Name = "", Side = SourceKind.Remote, Kind = ParticipantKind.Unnamed });
        Assert.Equal("Alice Client", NameResolver.Resolve(Seg(5, TranscriptSource.Remote, "Them"), null, meta));
    }
}
