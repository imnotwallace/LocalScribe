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

    [Fact]
    public void DisplayRow_carries_end_of_its_last_segment()
    {
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "a", "Me"),
            TranscriptLine.Segment(1, TranscriptSource.Local, 1000, 2500, "b", "Me"),
            TranscriptLine.Marker(2, 3000, Markers.AudioDeviceChanged),
        };
        var rows = Sut().Build(lines, speakers: null, edits: null, Meta());
        Assert.Equal(2, rows.Count);
        Assert.Equal(2500, rows[0].EndMs);   // running end of the merged "Me" turn
        Assert.Equal(3000, rows[1].EndMs);   // marker EndMs == its atMs
    }

    [Fact]
    public void Equal_startMs_breaks_ties_by_source_rank_local_remote_system()
    {
        // Talk-over: Local + Remote + a System marker all at the SAME startMs, fed out of order.
        // The tie-break must order Local (0) before Remote (1) before System (2), independent of seq.
        var lines = new[]
        {
            TranscriptLine.Segment(2, TranscriptSource.Remote, 1000, 2000, "r", "Them"),
            TranscriptLine.Marker(1, 1000, Markers.AudioDeviceChanged),   // System, same startMs
            TranscriptLine.Segment(0, TranscriptSource.Local, 1000, 2000, "l", "Me"),
        };
        var rows = Sut().Build(lines, null, null, Meta());
        Assert.Equal(3, rows.Count);
        Assert.Equal("Me", rows[0].DisplayName);      // Local rank 0
        Assert.False(rows[0].IsMarker);
        Assert.Equal("Them", rows[1].DisplayName);    // Remote rank 1
        Assert.False(rows[1].IsMarker);
        Assert.True(rows[2].IsMarker);                // System rank 2, last
    }

    [Fact]
    public void Same_speaker_gap_at_or_above_threshold_starts_new_section()
    {
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "before break", "Me"),
            TranscriptLine.Segment(1, TranscriptSource.Local, 6000, 7000, "after break", "Me"),
        };
        var rows = Sut().Build(lines, null, null, Meta());   // default gap 5000; 6000-1000 = 5000 -> split

        Assert.Equal(2, rows.Count);
        Assert.Equal("before break", rows[0].Text);
        Assert.Equal("after break", rows[1].Text);

        // Display-only: the projection layer has no store reference and never mutates the source
        // (transcript.jsonl is never touched); the input records are unchanged.
        Assert.Equal(2, lines.Length);
        Assert.Equal("before break", lines[0].Text);
        Assert.Equal("after break", lines[1].Text);
    }

    [Fact]
    public void Rows_carry_constituent_segments_with_projected_and_raw_text()
    {
        var global = new Vocabulary { Corrections = new Dictionary<string, string> { ["auth"] = "OAuth" } };
        var vocab = new VocabularyProvider(global, new Dictionary<string, Matter>());
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "the auth change", "Them"),
            TranscriptLine.Segment(1, TranscriptSource.Remote, 1000, 2000, "second line", "Them"),
        };
        var edits = new Edits { Corrections = new Dictionary<string, Correction> { ["1"] = new() { Text = "HUMAN EDIT" } } };

        var rows = Sut(vocab).Build(lines, null, edits, Meta(remote: 2));

        Assert.Single(rows);
        Assert.Equal(2, rows[0].Segments.Count);
        Assert.Equal("the OAuth change", rows[0].Segments[0].ProjectedText);   // vocabulary applied
        Assert.Equal("the auth change", rows[0].Segments[0].RawText);          // machine original kept
        Assert.False(rows[0].Segments[0].IsCorrected);
        Assert.Equal("HUMAN EDIT", rows[0].Segments[1].ProjectedText);
        Assert.Equal("second line", rows[0].Segments[1].RawText);
        Assert.True(rows[0].Segments[1].IsCorrected);
        Assert.True(rows[0].HasCorrection);
    }

    [Fact]
    public void Pinned_seqs_flag_their_row_segments()
    {
        var speakers = new Speakers
        {
            Names = new Dictionary<string, string> { ["Remote:0"] = "Alice" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["0"] = "Remote:0", ["1"] = "Remote:0" } },
            Pinned = new Dictionary<string, List<string>> { ["Remote"] = new() { "1" } },
        };
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "a", "Them"),
            TranscriptLine.Segment(1, TranscriptSource.Remote, 1000, 2000, "b", "Them"),
        };

        var rows = Sut().Build(lines, speakers, null, Meta(remote: 2));

        Assert.Single(rows);
        Assert.False(rows[0].Segments[0].IsPinned);    // diarised, not pinned
        Assert.True(rows[0].Segments[1].IsPinned);     // manually pinned
        Assert.True(rows[0].HasPin);
    }

    [Fact]
    public void Corrected_local_segment_is_never_dedup_hidden()   // spec 6.1 step 4: human correction beats dedup-hide
    {
        // A Local segment that IS a phantom bleed of the Remote (identical text, 11 dB quieter,
        // near-simultaneous). Without a correction it is hidden; WITH a human correction it must survive.
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Remote, 1000, 3000, "I pushed the auth changes last night.", "Them", rmsDb: -20.0),
            TranscriptLine.Segment(1, TranscriptSource.Local, 1200, 3100, "I pushed the auth changes last night.", "Me", rmsDb: -31.0),
        };
        var vocab = new VocabularyProvider(new Vocabulary(), new Dictionary<string, Matter>());
        var meta = Meta(1, 1);

        // Baseline: no edits -> the quieter Local bleed is hidden (only the Remote survives).
        var noEdit = new TranscriptProjection(vocab, new PhantomBleedDedup()).Build(lines, null, null, meta);
        Assert.Single(noEdit, r => !r.IsMarker);

        // With a human correction on the Local seq -> it must NOT be dedup-hidden (correction beats dedup-hide).
        var edits = new Edits
        {
            Corrections = new Dictionary<string, Correction>
            { ["1"] = new() { Text = "I pushed the auth changes last night." } },
        };
        var withEdit = new TranscriptProjection(vocab, new PhantomBleedDedup()).Build(lines, null, edits, meta);
        Assert.Equal(2, withEdit.Count(r => !r.IsMarker));   // both survive
    }
}
