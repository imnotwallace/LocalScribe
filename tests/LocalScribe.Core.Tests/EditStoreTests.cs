// tests/LocalScribe.Core.Tests/EditStoreTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;

public class EditStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 15, 0, 0, TimeSpan.Zero);

    // Set by NewFinalizedSessionAsync/NewUnfinalizedSessionAsync so tests that use those helpers
    // can reach meta.json without threading the directory through every assertion; deleted in
    // Dispose(). Tests that manage their own `dir` local (the pre-existing pattern below) never
    // touch this field, so their manual try/finally cleanup is unaffected.
    private string _dir = "";

    private static async Task<string> FinalizedSessionDirAsync(long durationMs = 0)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await new SessionStore(Path.Combine(dir, "session.json")).SaveAsync(new SessionRecord
        {
            Id = "s", App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = T0.AddMinutes(30),
            DurationMs = durationMs,
        }, default);
        await new MetadataStore(Path.Combine(dir, "meta.json")).SaveAsync(
            SessionMeta.CreateDefault(AppKind.Webex, T0, self: null), default);
        var transcript = new TranscriptStore(Path.Combine(dir, "transcript.jsonl"));
        await transcript.AppendAsync(
            TranscriptLine.Segment(17, TranscriptSource.Remote, 85000, 89000, "the arraignment is thursday", "Them"), default);
        await transcript.AppendAsync(TranscriptLine.Marker(18, 90000, Markers.PausedByUser), default);
        return dir;
    }

    /// <summary>Finalized-session EditStore for the insertions tests: same fixture as
    /// FinalizedSessionDirAsync, with the session duration set and the directory captured in
    /// _dir for the one test that needs to re-read meta.json directly.</summary>
    private async Task<EditStore> NewFinalizedSessionAsync(long durationMs)
    {
        _dir = await FinalizedSessionDirAsync(durationMs);
        return new EditStore(_dir, new ManualUtcTimeProvider(T0));
    }

    /// <summary>A live (never finalized) session: session.json with EndedAtUtc null, no meta.json
    /// or transcript.jsonl - EnsureFinalizedAsync throws before either would be read.</summary>
    private async Task<EditStore> NewUnfinalizedSessionAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        await new SessionStore(Path.Combine(_dir, "session.json")).SaveAsync(new SessionRecord
        {
            Id = "s", App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = null,   // live
        }, default);
        return new EditStore(_dir, new ManualUtcTimeProvider(T0));
    }

    public void Dispose()
    {
        if (_dir.Length > 0 && Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Text_correction_writes_edits_json_and_marks_meta_edited()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var time = new ManualUtcTimeProvider(T0.AddMinutes(45));
            var store = new EditStore(dir, time);
            await store.ApplyTextCorrectionAsync(17, "The arraignment is on Thursday.", default);

            var edits = await store.LoadAsync(default);
            Assert.Equal("The arraignment is on Thursday.", edits!.Corrections["17"].Text);
            Assert.Equal(T0.AddMinutes(45), edits.Corrections["17"].EditedAtUtc);

            var meta = await new MetadataStore(Path.Combine(dir, "meta.json")).LoadAsync(default);
            Assert.True(meta!.Edited);
            Assert.Equal(T0.AddMinutes(45), meta.LastEditedAtUtc);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Reassign_writes_pinned_assignment_in_speakers_json()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await store.ReassignSpeakerAsync(17, TranscriptSource.Remote, "Remote:2", default);

            var speakers = await new SpeakersStore(Path.Combine(dir, "speakers.json")).LoadAsync(default);
            Assert.Equal("Remote:2", speakers!.Assignments["Remote"]["17"]);
            Assert.Contains("17", speakers.Pinned["Remote"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Correcting_a_nonexistent_seq_throws_before_writing()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.ApplyTextCorrectionAsync(99, "x", default));
            Assert.False(File.Exists(Path.Combine(dir, "edits.json")));   // nothing written
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Correcting_a_marker_line_throws()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.ApplyTextCorrectionAsync(18, "x", default));   // seq 18 is a marker
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Reassign_with_wrong_source_stream_throws()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(                   // seq 17 is Remote, not Local
                () => store.ReassignSpeakerAsync(17, TranscriptSource.Local, "Local:1", default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Editing_a_live_session_throws()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            await new SessionStore(Path.Combine(dir, "session.json")).SaveAsync(new SessionRecord
            {
                Id = "s", App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = null,   // live
            }, default);

            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ApplyTextCorrectionAsync(1, "x", default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Batch_apply_and_revert_land_in_one_edits_json_state()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await store.ApplyTextCorrectionAsync(17, "first pass", default);

            bool changed = await store.ApplyTextEditsAsync(
                corrections: new Dictionary<int, string>(),
                reverts: new[] { 17 }, default);

            Assert.True(changed);
            var edits = await store.LoadAsync(default);
            Assert.False(edits!.Corrections.ContainsKey("17"));   // overlay entry removed; JSONL untouched
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Batch_correction_writes_and_flips_meta_edited_once()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var time = new ManualUtcTimeProvider(T0.AddMinutes(5));
            var store = new EditStore(dir, time);
            bool changed = await store.ApplyTextEditsAsync(
                new Dictionary<int, string> { [17] = "The arraignment is on Thursday." },
                reverts: Array.Empty<int>(), default);

            Assert.True(changed);
            var edits = await store.LoadAsync(default);
            Assert.Equal("The arraignment is on Thursday.", edits!.Corrections["17"].Text);
            var meta = await new MetadataStore(Path.Combine(dir, "meta.json")).LoadAsync(default);
            Assert.True(meta!.Edited);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Empty_or_whitespace_correction_is_rejected_before_writing()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyTextEditsAsync(
                new Dictionary<int, string> { [17] = "   " }, Array.Empty<int>(), default));
            Assert.False(File.Exists(Path.Combine(dir, "edits.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Seq_in_both_corrections_and_reverts_is_rejected()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyTextEditsAsync(
                new Dictionary<int, string> { [17] = "x" }, new[] { 17 }, default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Reverting_a_never_corrected_seq_is_a_quiet_noop()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            bool changed = await store.ApplyTextEditsAsync(
                new Dictionary<int, string>(), new[] { 17 }, default);

            Assert.False(changed);
            Assert.False(File.Exists(Path.Combine(dir, "edits.json")));   // nothing written
            var meta = await new MetadataStore(Path.Combine(dir, "meta.json")).LoadAsync(default);
            Assert.False(meta!.Edited);                                    // no phantom edit flag
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Batch_correcting_a_marker_seq_throws()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyTextEditsAsync(
                new Dictionary<int, string> { [18] = "x" }, Array.Empty<int>(), default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Batch_reassign_pins_every_seq_to_the_cluster()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            // Fixture has only seq 17 on Remote; add a second Remote segment for the batch.
            await new TranscriptStore(Path.Combine(dir, "transcript.jsonl")).AppendAsync(
                TranscriptLine.Segment(19, TranscriptSource.Remote, 91000, 93000, "and bail", "Them"), default);

            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await store.ReassignSpeakersAsync(new[] { 17, 19 }, TranscriptSource.Remote, "Remote:3", default);

            var speakers = await new SpeakersStore(Path.Combine(dir, "speakers.json")).LoadAsync(default);
            Assert.Equal("Remote:3", speakers!.Assignments["Remote"]["17"]);
            Assert.Equal("Remote:3", speakers.Assignments["Remote"]["19"]);
            Assert.Contains("17", speakers.Pinned["Remote"]);
            Assert.Contains("19", speakers.Pinned["Remote"]);
            var meta = await new MetadataStore(Path.Combine(dir, "meta.json")).LoadAsync(default);
            Assert.True(meta!.Edited);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Reassign_rejects_a_clusterKey_from_the_wrong_source()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.ReassignSpeakersAsync(new[] { 17 }, TranscriptSource.Remote, "Local:0", default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Remove_pin_restores_fallback_but_never_touches_diarised_assignments()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            await new TranscriptStore(Path.Combine(dir, "transcript.jsonl")).AppendAsync(
                TranscriptLine.Segment(19, TranscriptSource.Remote, 91000, 93000, "and bail", "Them"), default);

            // Seed: seq 17 pinned; seq 19 diarised (assignment WITHOUT a pin).
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await store.ReassignSpeakersAsync(new[] { 17 }, TranscriptSource.Remote, "Remote:1", default);
            var speakersStore = new SpeakersStore(Path.Combine(dir, "speakers.json"));
            var seeded = await speakersStore.LoadAsync(default);
            var assignments = seeded!.Assignments.ToDictionary(
                kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
            assignments["Remote"]["19"] = "Remote:0";
            await speakersStore.SaveAsync(seeded with { Assignments = assignments }, default);

            bool changed = await store.RemoveSpeakerPinsAsync(new[] { 17, 19 }, TranscriptSource.Remote, default);

            Assert.True(changed);
            var after = await speakersStore.LoadAsync(default);
            Assert.False(after!.Assignments["Remote"].ContainsKey("17"));   // pin + assignment gone
            Assert.DoesNotContain("17", after.Pinned["Remote"]);
            Assert.Equal("Remote:0", after.Assignments["Remote"]["19"]);    // diarised entry survives
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Remove_pin_with_no_speakers_file_is_a_quiet_noop()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            Assert.False(await store.RemoveSpeakerPinsAsync(new[] { 17 }, TranscriptSource.Remote, default));
            Assert.False(File.Exists(Path.Combine(dir, "speakers.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Edits_roundtrip_carries_insertions()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            var edits = new Edits
            {
                SchemaVersion = 2,
                Insertions = new Dictionary<string, InsertedLine>
                {
                    ["i000001"] = new InsertedLine
                    {
                        Text = "The witness said something off-mic here.",
                        StartMs = 61_500,
                        SpeakerParticipantId = "p-1",
                        AddedAtUtc = DateTimeOffset.Parse("2026-08-11T10:00:00Z"),
                    },
                },
            };
            string path = Path.Combine(dir, "roundtrip-edits.json");
            await JsonFile.WriteAsync(path, edits, default);
            var back = await JsonFile.ReadAsync<Edits>(path, default);

            Assert.NotNull(back);
            var line = Assert.Single(back!.Insertions).Value;
            Assert.Equal("The witness said something off-mic here.", line.Text);
            Assert.Equal(61_500, line.StartMs);
            Assert.Equal("p-1", line.SpeakerParticipantId);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Edits_default_has_an_empty_insertions_map_not_null()
    {
        string dir = await FinalizedSessionDirAsync();
        try
        {
            string path = Path.Combine(dir, "legacy-edits.json");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"corrections\":{}}");
            var back = await JsonFile.ReadAsync<Edits>(path, default);

            Assert.NotNull(back);
            Assert.Empty(back!.Insertions);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteVersion_is_1_without_insertions_and_2_with_them()
    {
        Assert.Equal(1, EditStore.WriteVersion(new Dictionary<string, InsertedLine>()));
        Assert.Equal(2, EditStore.WriteVersion(new Dictionary<string, InsertedLine>
        {
            ["i000001"] = new InsertedLine { Text = "x", StartMs = 0 },
        }));
    }

    [Fact]
    public void MintId_takes_the_high_water_mark_over_the_surviving_keys()
    {
        Assert.Equal("i000001", InsertedLineLabels.MintId([], 0));
        Assert.Equal("i000002", InsertedLineLabels.MintId(["i000001"], 1));
        // The mark wins when every key has been reverted away.
        Assert.Equal("i000004", InsertedLineLabels.MintId([], 3));
        // The keys are a self-healing FLOOR when the mark is stale or absent.
        Assert.Equal("i000006", InsertedLineLabels.MintId(["i000005"], 0));
        Assert.Equal("i000002", InsertedLineLabels.MintId(["i000001", "hand-edited"], 0));
    }

    [Fact]
    public async Task ApplyInsertions_mints_ids_and_writes_schema_version_2()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        bool wrote = (await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Off-mic answer.", 61_500, "p-1"),
             new EditStore.InsertedLineWrite(null, "Second one.", 62_000, null)],
            [], sessionDurationMs: 600_000, default)).Wrote;

        Assert.True(wrote);
        var edits = await store.LoadAsync(default);
        Assert.Equal(2, edits!.SchemaVersion);
        Assert.Equal(["i000001", "i000002"], edits.Insertions.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("Off-mic answer.", edits.Insertions["i000001"].Text);
        Assert.Equal("p-1", edits.Insertions["i000001"].SpeakerParticipantId);
        Assert.Null(edits.Insertions["i000002"].SpeakerParticipantId);
    }

    [Fact]
    public async Task ApplyInsertions_updates_an_existing_id_in_place()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "First text.", 1_000, null)], [], 600_000, default);
        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite("i000001", "Corrected text.", 2_000, "p-2")], [], 600_000, default);

        var edits = await store.LoadAsync(default);
        var only = Assert.Single(edits!.Insertions);
        Assert.Equal("i000001", only.Key);
        Assert.Equal("Corrected text.", only.Value.Text);
        Assert.Equal(2_000, only.Value.StartMs);
        Assert.Equal("p-2", only.Value.SpeakerParticipantId);
    }

    [Fact]
    public async Task ApplyInsertions_revert_removes_the_entry_and_drops_back_to_version_1()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Typo.", 1_000, null)], [], 600_000, default);

        bool wrote = (await store.ApplyInsertionsAsync([], ["i000001"], 600_000, default)).Wrote;

        Assert.True(wrote);
        var edits = await store.LoadAsync(default);
        Assert.Empty(edits!.Insertions);
        // The file no longer carries anything an older build would drop, so it is readable again.
        Assert.Equal(1, edits.SchemaVersion);
    }

    [Fact]
    public async Task ApplyInsertions_reverting_an_unknown_id_is_a_quiet_no_op()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        Assert.False((await store.ApplyInsertionsAsync([], ["i999999"], 600_000, default)).Wrote);
    }

    [Fact]
    public async Task ApplyInsertions_rejects_blank_text()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "   ", 1_000, null)], [], 600_000, default));
    }

    [Fact]
    public async Task ApplyInsertions_rejects_a_negative_start()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Text.", -1, null)], [], 600_000, default));
    }

    [Fact]
    public async Task ApplyInsertions_rejects_a_start_past_the_session_duration()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Text.", 600_001, null)], [], 600_000, default));
    }

    [Fact]
    public async Task ApplyInsertions_allows_any_start_when_the_duration_is_unknown()
    {
        // A crash-recovered session can carry DurationMs 0 because finalize never computed one.
        // Refusing every insertion there would block exactly the evidentiary repair this feature is
        // for, so the upper bound is enforced only when it is KNOWN - the same "none is not unknown"
        // distinction ManifestFile.FabricatedSilenceKnown draws.
        var store = await NewFinalizedSessionAsync(durationMs: 0);
        Assert.True((await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Text.", 5_000_000, null)], [], 0, default)).Wrote);
    }

    [Fact]
    public async Task ApplyInsertions_does_NOT_require_the_start_to_land_inside_a_segment()
    {
        // The whole point: the machine heard nothing there. This must not inherit the split fence.
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);   // fixture segments are near 0
        Assert.True((await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Silence filled by a person.", 599_000, null)],
            [], 600_000, default)).Wrote);
    }

    [Fact]
    public async Task Corrections_and_splits_still_reject_an_absent_seq()
    {
        // The insertion path bypasses EnsureSegmentAsync/EnsureSegmentsAsync. It must not LOOSEN them.
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ApplyTextCorrectionAsync(9_999, "nope", default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ApplyTextEditsAsync(new Dictionary<int, string> { [9_999] = "nope" }, [], default));
    }

    [Fact]
    public async Task ApplyInsertions_requires_a_finalized_session()
    {
        var store = await NewUnfinalizedSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Text.", 1_000, null)], [], 600_000, default));
    }

    [Fact]
    public async Task ApplyInsertions_flips_meta_Edited()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Text.", 1_000, null)], [], 600_000, default);
        var meta = await new MetadataStore(Path.Combine(_dir, "meta.json")).LoadAsync(default);
        Assert.True(meta!.Edited);
    }

    [Fact]
    public async Task Reverting_and_inserting_in_ONE_batch_never_reuses_the_revoked_id()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "First.", 1_000, null)], [], 600_000, default);

        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Different act.", 2_000, null)],
            ["i000001"], 600_000, default);

        var edits = await store.LoadAsync(default);
        var only = Assert.Single(edits!.Insertions);
        Assert.Equal("i000002", only.Key);
        Assert.Equal("Different act.", only.Value.Text);
    }

    [Fact]
    public async Task Reverting_then_inserting_in_a_LATER_call_never_reuses_the_revoked_id()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "First.", 1_000, null)], [], 600_000, default);
        await store.ApplyInsertionsAsync([], ["i000001"], 600_000, default);

        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Different act.", 2_000, null)], [], 600_000, default);

        Assert.Equal("i000002", Assert.Single((await store.LoadAsync(default))!.Insertions).Key);
    }

    [Fact]
    public async Task The_high_water_mark_survives_a_file_that_drops_back_to_schema_version_1()
    {
        // Reverting the only insertion returns the file to v1 (nothing an older build could drop),
        // but the mark must persist or the NEXT insertion reuses the revoked id.
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "First.", 1_000, null)], [], 600_000, default);
        await store.ApplyInsertionsAsync([], ["i000001"], 600_000, default);

        var edits = await store.LoadAsync(default);
        Assert.Equal(1, edits!.SchemaVersion);
        Assert.Empty(edits.Insertions);
        Assert.Equal(1, edits.HighestInsertionOrdinal);
    }

    [Fact]
    public async Task ApplyInsertions_preserves_AddedAtUtc_on_an_in_place_update()
    {
        var time = new ManualUtcTimeProvider(T0);
        string dir = await FinalizedSessionDirAsync(600_000);
        var store = new EditStore(dir, time);
        try
        {
            await store.ApplyInsertionsAsync(
                [new EditStore.InsertedLineWrite(null, "First text.", 1_000, null)], [], 600_000, default);

            time.Set(T0.AddHours(1));   // clock advances before the person retypes their own line
            await store.ApplyInsertionsAsync(
                [new EditStore.InsertedLineWrite("i000001", "Corrected text.", 2_000, "p-2")], [], 600_000, default);

            var edits = await store.LoadAsync(default);
            var only = Assert.Single(edits!.Insertions);
            Assert.Equal("Corrected text.", only.Value.Text);
            Assert.Equal(2_000, only.Value.StartMs);
            Assert.Equal(T0, only.Value.AddedAtUtc);   // unchanged - the line was not newly ADDED
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplyInsertions_rejects_a_negative_start_even_when_the_duration_is_unknown()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 0);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Text.", -1, null)], [], 0, default));
    }

    // ---- Whole-branch review, 2026-08-11 --------------------------------------------------------

    /// <summary>CRITICAL 2's enabling contract: the ids the writes landed under, index-aligned, so
    /// a caller can make its own retry idempotent.</summary>
    [Fact]
    public async Task ApplyInsertions_returns_the_ids_its_writes_landed_under()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        var minted = await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "First.", 1_000, null),
             new EditStore.InsertedLineWrite(null, "Second.", 2_000, null)],
            [], 600_000, default);

        Assert.True(minted.Wrote);
        Assert.Equal(["i000001", "i000002"], minted.Ids);

        // Re-submitting under those ids UPDATES rather than adds - the retry-safety property.
        var again = await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(minted.Ids[0], "First, retyped.", 1_000, null)],
            [], 600_000, default);

        Assert.Equal(["i000001"], again.Ids);
        var edits = await store.LoadAsync(default);
        Assert.Equal(2, edits!.Insertions.Count);
        Assert.Equal("First, retyped.", edits.Insertions["i000001"].Text);
    }

    /// <summary>MINOR 9: an explicit Id that is no longer in the map used to be written back with a
    /// fresh AddedAtUtc, resurrecting a REVERTED human act under an id manifest.json has already
    /// disclosed as a different act - and doing it behind the high-water mark that exists to stop
    /// exactly that reuse. Refused loudly instead.</summary>
    /// <summary>Final review, 2026-08-11/12 - the residual duplicate-line window CRITICAL 2's fix
    /// left open: MarkEditedAsync runs AFTER the edits.json write but still INSIDE
    /// ApplyInsertionsAsync, and it can throw (a forward-versioned meta.json, an IO error,
    /// cancellation). Before this fix the minted id was stamped only on the RETURN value, so a
    /// throw there meant the caller never learned the id, and the retry the save-error message
    /// invites minted a SECOND insertion holding the same words. A MISSING meta.json does not
    /// reproduce this - MarkEditedAsync returns silently when meta is null - so this forces the
    /// throw via a meta.json newer than MetadataStore supports.</summary>
    [Fact]
    public async Task ApplyInsertions_stamps_the_minted_id_even_when_MarkEditedAsync_throws()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await File.WriteAllTextAsync(Path.Combine(_dir, "meta.json"), "{\"schemaVersion\":3}", default);

        IReadOnlyList<string>? mintedIds = null;
        await Assert.ThrowsAsync<NotSupportedException>(() => store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "Off-mic answer.", 1_000, null)],
            [], 600_000, default, ids => mintedIds = ids));

        // The id escaped the throw - this is the whole point of the fix.
        Assert.Equal(["i000001"], mintedIds);
        var edits = await store.LoadAsync(default);
        Assert.Equal("Off-mic answer.", Assert.Single(edits!.Insertions).Value.Text);

        // Simulate the retry the save-error message invites: the caller stamps the id it received
        // back onto the pending line before resubmitting, so it lands as an in-place UPDATE rather
        // than minting a second insertion of the same words. Restore a valid meta.json first so the
        // retry's own MarkEditedAsync can succeed.
        await new MetadataStore(Path.Combine(_dir, "meta.json"))
            .SaveAsync(SessionMeta.CreateDefault(AppKind.Webex, T0, self: null), default);
        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(mintedIds![0], "Off-mic answer, corrected.", 1_000, null)],
            [], 600_000, default);

        var after = await store.LoadAsync(default);
        var only = Assert.Single(after!.Insertions);
        Assert.Equal("i000001", only.Key);
        Assert.Equal("Off-mic answer, corrected.", only.Value.Text);
    }

    [Fact]
    public async Task ApplyInsertions_refuses_to_resurrect_a_reverted_id()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, "First.", 1_000, null)], [], 600_000, default);
        await store.ApplyInsertionsAsync([], ["i000001"], 600_000, default);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite("i000001", "Resurrected.", 1_000, null)],
            [], 600_000, default));

        var edits = await store.LoadAsync(default);
        Assert.Empty(edits!.Insertions);
        Assert.Equal(1, edits.HighestInsertionOrdinal);   // the mark is intact, so i000002 is next
    }

    [Fact]
    public async Task ApplyInsertions_refuses_an_id_that_never_existed()
    {
        var store = await NewFinalizedSessionAsync(durationMs: 600_000);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite("i000042", "From nowhere.", 1_000, null)],
            [], 600_000, default));
        Assert.Null(await store.LoadAsync(default));      // nothing was written at all
    }
}
