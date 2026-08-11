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
    /// <summary><paramref name="settings"/> is trailing-optional (Task 7, design 2026-08-11 section
    /// 2.3): every existing call site keeps its own FakeSettingsService untouched, while a caller
    /// that needs to observe how many times SaveTranscriptEditsAsync reached
    /// RegenerateProjectionsAsync can pass a CountingSettingsService instead - see that class's
    /// doc for why settings.Current is a precise counting seam for that method.</summary>
    public static async Task<(MaintenanceService Svc, StoragePaths Paths, string SessionId, string Root)>
        NewSessionWithRemoteSegmentAsync(ISettingsService? settings = null)
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

        var svc = new MaintenanceService(paths,
            settings ?? new FakeSettingsService(new Settings { AudioRetention = "keep" }),
            new FakeRecycleBin(), TimeProvider.System);
        return (svc, paths, id, root);
    }
}

/// <summary>Counts reads of Current (Task 7, design 2026-08-11 section 2.3): a precise
/// regeneration-count seam for SaveTranscriptEditsAsync specifically. Reading that method's body
/// (MaintenanceService.cs) shows settings.Current is touched exactly once - at
/// `new SessionWriter(paths, settings.Current, time)`, immediately before
/// RegenerateProjectionsAsync - and nowhere else in the method; EditStore/SessionStore/
/// MetadataStore never take ISettingsService at all. So CurrentReads after one
/// SaveTranscriptEditsAsync call is exactly the number of times that method reached
/// RegenerateProjectionsAsync, including a hypothetical duplicate call the shared
/// SessionContentChanged event (raised once per OUTER method call, regardless of how many times
/// the INNER lambda regenerated) could not distinguish from a single call. There is no
/// ISessionWriter seam to fake directly (SessionWriter is a concrete sealed class, constructed
/// inline, and this repo deliberately has no InternalsVisibleTo - see ManifestBuilder.HashAsync's
/// doc), so this is the closest exact seam: ISettingsService is the constructor-injected
/// dependency every SessionWriter construction site already reads its second argument from.</summary>
internal sealed class CountingSettingsService(Settings current) : ISettingsService
{
    private Settings _current = current;
    public int CurrentReads;
    public Settings Current { get { CurrentReads++; return _current; } }
    public event Action<Settings, Settings>? Changed;

    public Task SaveAsync(Settings updated, CancellationToken ct)
    {
        var old = _current;
        _current = updated;
        Changed?.Invoke(old, updated);
        return Task.CompletedTask;
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

    // ---- Task 7: the App write path for human-authored transcript lines (design 2026-08-11
    // section 2.3). Adapted from the task-7 brief onto this file's existing EditorHarness rather
    // than the brief's assumed _maintenance/_paths/_time/_regenerationCount instance fields, which
    // do not exist in this file's per-test-tuple fixture shape:
    //   - FinalizedSessionAsync() -> EditorHarness.NewSessionWithRemoteSegmentAsync() (session "s1"
    //     with one Remote segment at seq 3, not seq 0 - the correction below targets seq 3).
    //   - _maintenance / _paths / _time -> the harness tuple's svc / paths, and TimeProvider.System
    //     (already this file's convention for a read-back EditStore).
    //   - _regenerationCount -> settings.CurrentReads from a CountingSettingsService passed into
    //     the harness (see that class's doc, above EditorHarness, for why counting reads of
    //     ISettingsService.Current is a precise per-call regeneration count for this method).

    [Fact]
    public async Task SaveTranscriptEdits_writes_an_insertion_and_regenerates_once()
    {
        var settings = new CountingSettingsService(new Settings { AudioRetention = "keep" });
        var (svc, paths, sid, root) = await EditorHarness.NewSessionWithRemoteSegmentAsync(settings);
        _roots.Add(root);
        var batch = new TranscriptEditBatch(new Dictionary<int, string>(), [], [], [])
        {
            Insertions = [new InsertedLineEdit(null, "Off-mic answer.", 50_000, "p-1")],
        };

        Assert.True(await svc.SaveTranscriptEditsAsync(sid, batch, "v1", default));

        var edits = await new EditStore(paths.SessionDir(sid), TimeProvider.System).LoadAsync(default);
        var only = Assert.Single(edits!.Insertions);
        Assert.Equal("i000001", only.Key);
        Assert.Equal("Off-mic answer.", only.Value.Text);
        // The reseal choke point is NOT universal (SpeakerDetectionStep.MarkAsync skips it), so this
        // path's regeneration is asserted rather than assumed - and asserted to happen exactly once.
        Assert.Equal(1, settings.CurrentReads);
    }

    [Fact]
    public async Task SaveTranscriptEdits_updates_an_existing_insertion()
    {
        var (svc, paths, sid, root) = await EditorHarness.NewSessionWithRemoteSegmentAsync();
        _roots.Add(root);
        var empty = new Dictionary<int, string>();
        await svc.SaveTranscriptEditsAsync(sid,
            new TranscriptEditBatch(empty, [], [], [])
            { Insertions = [new InsertedLineEdit(null, "Typo.", 1_000, null)] }, "v1", default);

        await svc.SaveTranscriptEditsAsync(sid,
            new TranscriptEditBatch(empty, [], [], [])
            { Insertions = [new InsertedLineEdit("i000001", "Fixed.", 2_000, null)] }, "v1", default);

        var edits = await new EditStore(paths.SessionDir(sid), TimeProvider.System).LoadAsync(default);
        Assert.Equal("Fixed.", Assert.Single(edits!.Insertions).Value.Text);
    }

    [Fact]
    public async Task SaveTranscriptEdits_reverts_an_insertion()
    {
        var (svc, paths, sid, root) = await EditorHarness.NewSessionWithRemoteSegmentAsync();
        _roots.Add(root);
        var empty = new Dictionary<int, string>();
        await svc.SaveTranscriptEditsAsync(sid,
            new TranscriptEditBatch(empty, [], [], [])
            { Insertions = [new InsertedLineEdit(null, "Typo.", 1_000, null)] }, "v1", default);

        Assert.True(await svc.SaveTranscriptEditsAsync(sid,
            new TranscriptEditBatch(empty, [], [], []) { InsertionReverts = ["i000001"] }, "v1", default));

        var edits = await new EditStore(paths.SessionDir(sid), TimeProvider.System).LoadAsync(default);
        Assert.Empty(edits!.Insertions);
    }

    [Fact]
    public async Task SaveTranscriptEdits_with_an_empty_insertion_batch_stays_a_no_op()
    {
        var settings = new CountingSettingsService(new Settings { AudioRetention = "keep" });
        var (svc, _, sid, root) = await EditorHarness.NewSessionWithRemoteSegmentAsync(settings);
        _roots.Add(root);

        Assert.False(await svc.SaveTranscriptEditsAsync(sid,
            new TranscriptEditBatch(new Dictionary<int, string>(), [], [], []), "v1", default));
        Assert.Equal(0, settings.CurrentReads);
    }

    [Fact]
    public async Task An_insertion_and_a_correction_in_one_batch_write_once_and_regenerate_once()
    {
        var settings = new CountingSettingsService(new Settings { AudioRetention = "keep" });
        var (svc, paths, sid, root) = await EditorHarness.NewSessionWithRemoteSegmentAsync(settings);
        _roots.Add(root);
        // seq 3 is the harness's one Remote segment (see EditorHarness's doc) - the brief's example
        // used seq 0, which does not exist in this file's fixture.
        var batch = new TranscriptEditBatch(
            new Dictionary<int, string> { [3] = "Corrected machine text." }, [], [], [])
        {
            Insertions = [new InsertedLineEdit(null, "Off-mic answer.", 50_000, null)],
        };

        Assert.True(await svc.SaveTranscriptEditsAsync(sid, batch, "v1", default));

        var edits = await new EditStore(paths.SessionDir(sid), TimeProvider.System).LoadAsync(default);
        Assert.Single(edits!.Corrections);
        Assert.Single(edits.Insertions);
        Assert.Equal(2, edits.SchemaVersion);
        Assert.Equal(1, settings.CurrentReads);
    }

    public void Dispose() { try { foreach (var root in _roots) Directory.Delete(root, true); } catch { } }
}
