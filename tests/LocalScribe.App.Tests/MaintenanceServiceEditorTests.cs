// tests/LocalScribe.App.Tests/MaintenanceServiceEditorTests.cs
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Shared fixture builder for the transcript-editor MaintenanceService tests (Task 9),
/// modeled on MaintenanceServiceDiarisationTests.MakeFinalizedSession: a finalized session.json +
/// meta.json + a one-line transcript.jsonl (one Remote segment, seq 3, 15000..17000ms,
/// "First. Second."), wired to a real MaintenanceService over a temp StoragePaths root.</summary>
internal static class EditorHarness
{
    public static async Task<(MaintenanceService Svc, StoragePaths Paths, string SessionId, string Root)>
        NewSessionWithRemoteSegmentAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_editor_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Remote],
        }, default);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta { RemoteCount = 1 }, default);
        await new TranscriptStore(paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "First. Second.", "Them"),
            default);

        var settings = new FakeSettingsService(new Settings { AudioRetention = "keep" });
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(), TimeProvider.System);
        return (svc, paths, id, root);
    }
}

public sealed class MaintenanceServiceEditorTests : IDisposable
{
    private readonly List<string> _roots = [];

    [Fact]
    public async Task SaveTranscriptEdits_PersistsSplit_AndRegensProjection()
    {
        var (svc, paths, sid, root) = await EditorHarness.NewSessionWithRemoteSegmentAsync();
        _roots.Add(root);
        var batch = new TranscriptEditBatch(
            Corrections: new Dictionary<int, string>(),
            CorrectionReverts: [],
            Splits:
            [
                new SplitEdit(3, TranscriptSource.Remote,
                [
                    new SplitPartEdit("First.", 15000, false, null, null),
                    new SplitPartEdit("Second.", 16000, true, null, null),
                ]),
            ],
            SplitReverts: []);

        bool changed = await svc.SaveTranscriptEditsAsync(sid, batch, "v1", CancellationToken.None);

        Assert.True(changed);
        var edits = await new EditStore(paths.SessionDir(sid), TimeProvider.System)
            .LoadAsync(CancellationToken.None);
        Assert.True(edits!.Splits.ContainsKey("3"));
        // regen ran: transcript.md now shows both halves as separate turns is asserted in the
        // read-view VM test (Task 10); here assert the overlay + that meta.Edited flipped.
        var meta = await new MetadataStore(paths.MetaJson(sid)).LoadAsync(CancellationToken.None);
        Assert.True(meta!.Edited);
    }

    [Fact]
    public async Task SaveTranscriptEdits_NoOpBatch_ReturnsFalse()
    {
        var (svc, _, sid, root) = await EditorHarness.NewSessionWithRemoteSegmentAsync();
        _roots.Add(root);
        bool changed = await svc.SaveTranscriptEditsAsync(sid,
            new TranscriptEditBatch(new Dictionary<int, string>(), [], [], []), "v1", CancellationToken.None);
        Assert.False(changed);
    }

    public void Dispose() { try { foreach (var root in _roots) Directory.Delete(root, true); } catch { } }
}
