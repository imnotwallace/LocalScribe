// tests/LocalScribe.Core.Tests/EditStoreTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;

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
}
