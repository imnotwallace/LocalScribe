using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Vocabulary;

public class TranscriptProjectionTests
{
    private static TranscriptProjection Sut(IVocabularyProvider? vocab = null) =>
        new(vocab ?? new VocabularyProvider(new Vocabulary(), new Dictionary<string, Matter>()), new NoOpDedup());

    private static SessionMeta Meta(int local = 2, int remote = 2, params SessionParticipant[] ps) =>
        new() { LocalCount = local, RemoteCount = remote, Participants = ps };

    [Fact]
    public void Consecutive_same_speaker_segments_group_into_one_turn()
    {
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Morning.", "Me"),
            TranscriptLine.Segment(1, TranscriptSource.Local, 1000, 2000, "Quick recap.", "Me"),
            TranscriptLine.Segment(2, TranscriptSource.Remote, 2000, 3000, "Sure.", "Them"),
        };
        var rows = Sut().Build(lines, speakers: null, edits: null, Meta());
        Assert.Equal(2, rows.Count);
        Assert.Equal("Me", rows[0].DisplayName);
        Assert.Equal("Morning. Quick recap.", rows[0].Text);   // space-joined
        Assert.Equal("Them", rows[1].DisplayName);
    }

    [Fact]
    public void Markers_sort_into_timeline_by_startMs_and_break_grouping()
    {
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "a", "Me"),
            TranscriptLine.Marker(1, 1500, Markers.AudioDeviceChanged),
            TranscriptLine.Segment(2, TranscriptSource.Local, 2000, 3000, "b", "Me"),
        };
        var rows = Sut().Build(lines, null, null, Meta());
        Assert.Equal(3, rows.Count);                            // marker splits the two "Me" turns
        Assert.False(rows[0].IsMarker);
        Assert.True(rows[1].IsMarker);
        Assert.Equal("audio device changed", rows[1].Text);
        Assert.False(rows[2].IsMarker);
    }

    [Fact]
    public void Display_order_is_startMs_then_local_before_remote()
    {
        // Remote finalized first (lower seq) but starts later; Local starts earlier.
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Remote, 500, 1500, "remote", "Them"),
            TranscriptLine.Segment(1, TranscriptSource.Local, 0, 400, "local", "Me"),
        };
        var rows = Sut().Build(lines, null, null, Meta());
        Assert.Equal("Me", rows[0].DisplayName);                // startMs 0 first
        Assert.Equal("Them", rows[1].DisplayName);
    }

    [Fact]
    public void Vocabulary_then_edits_supersede_with_human_winning()
    {
        var global = new Vocabulary { Corrections = new Dictionary<string, string> { ["auth"] = "OAuth" } };
        var vocab = new VocabularyProvider(global, new Dictionary<string, Matter>());
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "the auth change", "Them"),
            TranscriptLine.Segment(1, TranscriptSource.Remote, 1000, 2000, "the auth change", "Them"),
        };
        var edits = new Edits { Corrections = new Dictionary<string, Correction> { ["1"] = new() { Text = "HUMAN EDIT" } } };
        var rows = Sut(vocab).Build(lines, null, edits, Meta(remote: 2));

        // seq 0: vocabulary applied -> "the OAuth change"; seq 1: human edit wins verbatim.
        // Both are "Them" so they group: "the OAuth change HUMAN EDIT"
        Assert.Single(rows);
        Assert.Equal("the OAuth change HUMAN EDIT", rows[0].Text);
    }

    [Fact]
    public void Single_declared_participant_name_flows_through()
    {
        var meta = Meta(1, 1, new SessionParticipant { Id = "p", Name = "Alice Client", Side = SourceKind.Remote });
        var lines = new[] { TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "hi", "Them") };
        var rows = Sut().Build(lines, null, null, meta);
        Assert.Equal("Alice Client", rows[0].DisplayName);
    }
}
