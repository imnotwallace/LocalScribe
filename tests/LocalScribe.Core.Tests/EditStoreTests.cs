// tests/LocalScribe.Core.Tests/EditStoreTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class EditStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 15, 0, 0, TimeSpan.Zero);

    private static async Task<string> FinalizedSessionDirAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await new SessionStore(Path.Combine(dir, "session.json")).SaveAsync(new SessionRecord
        {
            Id = "s", App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = T0.AddMinutes(30),
        }, default);
        await new MetadataStore(Path.Combine(dir, "meta.json")).SaveAsync(
            SessionMeta.CreateDefault(AppKind.Webex, T0, self: null), default);
        var transcript = new TranscriptStore(Path.Combine(dir, "transcript.jsonl"));
        await transcript.AppendAsync(
            TranscriptLine.Segment(17, TranscriptSource.Remote, 85000, 89000, "the arraignment is thursday", "Them"), default);
        await transcript.AppendAsync(TranscriptLine.Marker(18, 90000, Markers.PausedByUser), default);
        return dir;
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
}
