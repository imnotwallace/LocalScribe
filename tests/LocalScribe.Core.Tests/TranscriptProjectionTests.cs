using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Vocabulary;

public class TranscriptProjectionTests
{
    private static TranscriptProjection Sut(IVocabularyProvider? vocab = null) =>
        new(vocab ?? new VocabularyProvider(new Vocabulary(), new Dictionary<string, Matter>()), new NoOpDedup());

    private static SessionMeta Meta(int local = 2, int remote = 2, params SessionParticipant[] participants) =>
        new() { LocalCount = local, RemoteCount = remote, Participants = participants };

    private static TranscriptLine Seg(int seq, long startMs, long endMs, string text,
        TranscriptSource source = TranscriptSource.Local)
        => TranscriptLine.Segment(seq, source, startMs, endMs, text, source == TranscriptSource.Local ? "Me" : "Them");

    private static SessionParticipant Named(string id, string name, SourceKind side) =>
        new() { Id = id, Name = name, Side = side, Kind = ParticipantKind.Named };

    private static IReadOnlyList<DisplayRow> Build(
        IReadOnlyList<TranscriptLine> lines, Speakers? speakers, Edits? edits, SessionMeta meta) =>
        Sut().Build(lines, speakers, edits, meta);

    private static Edits WithInsertions(params (string Id, long StartMs, string Text, string? Pid)[] ins)
        => new()
        {
            SchemaVersion = 2,
            Insertions = ins.ToDictionary(i => i.Id, i => new InsertedLine
            {
                Text = i.Text, StartMs = i.StartMs, SpeakerParticipantId = i.Pid,
                AddedAtUtc = DateTimeOffset.Parse("2026-08-11T10:00:00Z"),
            }),
        };

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

    [Fact]
    public void Build_surfaces_the_number_of_segments_dedup_suppressed()
    {
        // Tier 1 T1-8 (spec 2026-08-05 :161-166): PhantomBleedDedup removes content from EVERY
        // visible surface including exports, and the delta was computed and discarded. An export
        // that silently omits segments is the omission that reads as concealment in
        // cross-examination.
        // The text must clear PhantomBleedOptions' short-utterance floor (MinAutoSuppressChars 12 /
        // MinAutoSuppressTokens 3, design 2026-07-18 section 2) or NOTHING is suppressed and this
        // fact silently asserts nothing: "hello there" normalizes to 11 chars / 2 tokens and is
        // exempt by design. With no RmsDb on either line, pass 2 cannot fire at all, so it is
        // pass 1 - the LOCAL copy of a near-simultaneous identical REMOTE - that is hidden, on the
        // text-only bar (TextOnlyMinSimilarity 0.975).
        var lines = new[]
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Hello there, good morning.", "Me"),
            TranscriptLine.Segment(1, TranscriptSource.Remote, 0, 1000, "Hello there, good morning.", "Them"),
        };
        // Sut() is this file's own factory - it pairs a real VocabularyProvider with NoOpDedup, so
        // this one test constructs its own projection with the REAL PhantomBleedDedup; that is the
        // whole point of the fact and the only reason not to use Sut() here.
        var projection = new TranscriptProjection(
            new VocabularyProvider(new Vocabulary(), new Dictionary<string, Matter>()),
            new PhantomBleedDedup());

        var rows = projection.Build(lines, null, null, new SessionMeta(), 5000, out int suppressed);

        Assert.Equal(1, suppressed);                     // the bled Local copy of the Remote line
        Assert.Single(rows);
        // The five-argument overload still exists and still returns the identical rows.
        Assert.Equal(rows.Count, projection.Build(lines, null, null, new SessionMeta()).Count);
      }

    [Fact]
    public void An_inserted_line_becomes_a_row_where_no_machine_segment_exists()
    {
        var lines = new[] { Seg(seq: 0, startMs: 0, endMs: 1_000, "Machine text.") };
        var rows = Build(lines, speakers: null, WithInsertions(("i000001", 50_000, "Off-mic answer.", null)),
            Meta());

        var inserted = Assert.Single(rows, r => r.Text == "Off-mic answer.");
        Assert.False(inserted.IsMarker);
        Assert.Equal(50_000, inserted.StartMs);
        // Zero length: a human estimate is a point, not a measured span.
        Assert.Equal(50_000, inserted.EndMs);
        var seg = Assert.Single(inserted.Segments);
        Assert.Equal("i000001", seg.InsertionId);
    }

    [Fact]
    public void An_inserted_line_resolves_its_participant_name()
    {
        var meta = Meta(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)]);
        var rows = Build([Seg(0, 0, 1_000, "Machine.")], null,
            WithInsertions(("i000001", 50_000, "Off-mic answer.", "p-1")), meta);

        Assert.Equal("Jane Okafor", Assert.Single(rows, r => r.Text == "Off-mic answer.").DisplayName);
    }

    [Fact]
    public void An_inserted_line_with_no_participant_renders_Unattributed()
    {
        var rows = Build([Seg(0, 0, 1_000, "Machine.")], null,
            WithInsertions(("i000001", 50_000, "Inaudible passage.", null)), Meta());

        Assert.Equal(InsertedLineLabels.Unattributed,
            Assert.Single(rows, r => r.Text == "Inaudible passage.").DisplayName);
    }

    [Fact]
    public void An_inserted_line_whose_participant_no_longer_exists_renders_Unattributed()
    {
        var rows = Build([Seg(0, 0, 1_000, "Machine.")], null,
            WithInsertions(("i000001", 50_000, "Orphaned.", "p-deleted")), Meta());

        Assert.Equal(InsertedLineLabels.Unattributed,
            Assert.Single(rows, r => r.Text == "Orphaned.").DisplayName);
    }

    [Fact]
    public void An_inserted_line_sorts_BEFORE_a_machine_row_at_an_identical_start()
    {
        // Both gestures are positional: "Add line here..." on a row seeds that row's start, and the
        // edit-mode "+" seeds a boundary. "Insert at N" means "before what is at N".
        var lines = new[] { Seg(seq: 0, startMs: 50_000, endMs: 51_000, "Machine text.") };
        var rows = Build(lines, null, WithInsertions(("i000001", 50_000, "Human text.", null)), Meta());

        var texts = rows.Where(r => !r.IsMarker).Select(r => r.Text).ToList();
        Assert.Equal(["Human text.", "Machine text."], texts);
    }

    [Fact]
    public void Two_insertions_at_the_same_start_order_by_id()
    {
        var rows = Build([Seg(0, 0, 1_000, "Machine.")], null,
            WithInsertions(("i000002", 50_000, "Second.", null), ("i000001", 50_000, "First.", null)),
            Meta());

        var texts = rows.Where(r => !r.IsMarker && r.StartMs == 50_000).SelectMany(
            r => r.Segments.Select(s => s.ProjectedText)).ToList();
        Assert.Equal(["First.", "Second."], texts);
    }

    [Fact]
    public void An_inserted_line_is_never_counted_as_a_suppressed_segment()
    {
        // suppressedSegmentCount feeds the "Omitted:" export line, an anti-concealment disclosure
        // about MACHINE omissions. A human addition is not one.
        var rows = Sut().Build(
            [Seg(0, 0, 1_000, "Machine.")], null,
            WithInsertions(("i000001", 50_000, "Human.", null)), Meta(), 5_000,
            out int suppressed);

        Assert.Equal(0, suppressed);
        Assert.Contains(rows, r => r.Text == "Human.");
    }

    [Fact]
    public void An_inserted_line_is_never_marked_pinned_by_a_machine_pin_on_seq_0()
    {
        // Machine seq starts at 0 (TranscriptStore.NextSeqAsync), so a placeholder Seq on an inserted
        // segment would collide with a REAL pinned line. Source is System, which speakers.json never
        // keys, and the projection skips the pin lookup for inserted rows outright.
        var speakers = new Speakers
        {
            Pinned = new Dictionary<string, List<string>> { ["Local"] = ["0"] },
        };
        var rows = Build([Seg(0, 0, 1_000, "Machine.", TranscriptSource.Local)], speakers,
            WithInsertions(("i000001", 50_000, "Human.", null)), Meta());

        var seg = Assert.Single(Assert.Single(rows, r => r.Text == "Human.").Segments);
        Assert.False(seg.IsPinned);
        Assert.False(seg.IsCorrected);
        Assert.False(seg.IsSplitChild);
        Assert.Equal(TranscriptSource.System, seg.Source);
    }

    [Fact]
    public void An_inserted_line_merges_into_an_adjacent_same_speaker_section()
    {
        var meta = Meta(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)]);
        var speakers = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new Dictionary<string, string> { ["0"] = "Remote:1" } },
            Names = new Dictionary<string, string> { ["Remote:1"] = "Jane Okafor" },
        };
        var rows = Build([Seg(0, 10_000, 11_000, "and then I left.", TranscriptSource.Remote)],
            speakers, WithInsertions(("i000001", 10_000, "I locked the door", "p-1")), meta);

        var row = Assert.Single(rows, r => !r.IsMarker);
        Assert.Equal("I locked the door and then I left.", row.Text);
        Assert.Equal(2, row.Segments.Count);
        Assert.Equal("i000001", row.Segments[0].InsertionId);
        Assert.Null(row.Segments[1].InsertionId);
    }

    [Fact]
    public void No_insertions_produces_byte_identical_rows_to_no_edits_at_all()
    {
        var lines = new[] { Seg(0, 0, 1_000, "A."), Seg(1, 2_000, 3_000, "B.") };
        var withEmpty = Build(lines, null, new Edits { SchemaVersion = 1 }, Meta());
        var withNull = Build(lines, null, null, Meta());

        // DisplayRow's record-generated Equals compares Segments (IReadOnlyList<RowSegment>) by
        // reference, not content, so a plain Assert.Equal(withNull, withEmpty) never passes for any
        // row with segments - a pre-existing gap unrelated to insertions. Compare the scalar fields
        // and the segment content (RowSegment itself has proper value equality) separately instead.
        Assert.Equal(withNull.Count, withEmpty.Count);
        for (int i = 0; i < withNull.Count; i++)
        {
            var a = withNull[i];
            var b = withEmpty[i];
            Assert.Equal((a.IsMarker, a.StartMs, a.EndMs, a.DisplayName, a.Text),
                (b.IsMarker, b.StartMs, b.EndMs, b.DisplayName, b.Text));
            Assert.Equal(a.Segments, b.Segments);
        }
    }

    [Fact]
    public void An_inserted_row_segment_carries_its_participant_id_for_the_editor_to_preselect()
    {
        var meta = Meta(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)]);
        var rows = Build([Seg(0, 0, 1_000, "Machine.")], null,
            WithInsertions(("i000001", 50_000, "Off-mic answer.", "p-1")), meta);

        var seg = Assert.Single(Assert.Single(rows, r => r.Text == "Off-mic answer.").Segments);
        Assert.Equal("p-1", seg.SpeakerParticipantId);
        Assert.Null(seg.SpeakerClusterKey);   // never a clusterKey - a different key space entirely
    }
}
