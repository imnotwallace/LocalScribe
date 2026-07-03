using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MaintenanceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-maint-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (MaintenanceService Svc, StoragePaths Paths) MakeService()
    {
        var paths = new StoragePaths(_root);
        var svc = new MaintenanceService(paths, new FakeSettingsService(), new NoopRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero)));
        return (svc, paths);
    }

    /// <summary>A finalized on-disk session fixture: valid v3 session.json + meta.json, no
    /// transcript.jsonl (TranscriptStore reads a missing file as empty - projections render
    /// with zero rows, which is all these orchestration tests need).</summary>
    private static async Task WriteFinalizedSessionAsync(StoragePaths paths, string id, string title,
        IReadOnlyList<string>? matterIds = null)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, CancellationToken.None);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = title, MatterIds = matterIds ?? [] }, CancellationToken.None);
    }

    private static async Task WriteUnendedSessionAsync(StoragePaths paths, string id)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 2, 0, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0,          // EndedAtUtc stays null: unended
        }, CancellationToken.None);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Interrupted" }, CancellationToken.None);
    }

    [Fact]
    public async Task RunForSessionAsync_serializes_concurrent_work_on_one_id_but_not_across_ids()
    {
        var (svc, _) = MakeService();
        bool firstEntered = false, secondEntered = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> first = svc.RunForSessionAsync("s-one", async _ =>
        { firstEntered = true; await release.Task; return 1; }, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => firstEntered, TimeSpan.FromSeconds(5)));

        Task<int> second = svc.RunForSessionAsync("s-one", _ =>
        { secondEntered = true; return Task.FromResult(2); }, CancellationToken.None);

        // Interleaving proof: while the first work HOLDS the gate, the second must not enter.
        Assert.False(SpinWait.SpinUntil(() => secondEntered, TimeSpan.FromMilliseconds(200)));

        // Per-id, not global: a different session id runs to completion while s-one is held.
        Assert.Equal(3, await svc.RunForSessionAsync("s-two", _ => Task.FromResult(3), CancellationToken.None));

        release.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);                       // ran only after the first released
        Assert.True(secondEntered);
    }

    [Fact]
    public async Task SaveMetaAsync_regenerates_projections_and_applies_tag_delta()
    {
        var (svc, paths) = MakeService();
        const string id = "2026-07-03_0100_Webex_alpha";
        await WriteFinalizedSessionAsync(paths, id, "Old title");
        await new MatterStore(paths.MattersDir).CreateAsync(new Matter
        { Id = "M-2026-001", Name = "Estate of Alpha",
          DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero) });

        var updated = new SessionMeta { Title = "Estate call - corrected", MatterIds = ["M-2026-001"] };
        await svc.SaveMetaAsync(id, updated, previousMatterIds: [], CancellationToken.None);

        // Regen ran with the NEW meta: session.txt carries the new title (fresh SessionWriter
        // from settings.Current - the projection pipeline SessionWriter.cs:19-75).
        string sessionTxt = await File.ReadAllTextAsync(paths.SessionTxt(id));
        Assert.Contains("Estate call - corrected", sessionTxt);
        Assert.True(File.Exists(paths.TranscriptMd(id)));

        // Tag delta ([M-2026-001] added, nothing removed) hit the index: SessionCount 0 -> 1.
        var index = await new MatterStore(paths.MattersDir).ListAsync(CancellationToken.None);
        Assert.Equal(1, Assert.Single(index.Matters, m => m.Id == "M-2026-001").SessionCount);

        // And the delta is symmetric: untag it again and the count returns to 0.
        await svc.SaveMetaAsync(id, updated with { MatterIds = [] },
            previousMatterIds: ["M-2026-001"], CancellationToken.None);
        index = await new MatterStore(paths.MattersDir).ListAsync(CancellationToken.None);
        Assert.Equal(0, Assert.Single(index.Matters, m => m.Id == "M-2026-001").SessionCount);
    }

    [Fact]
    public async Task RecoverAllAsync_recovers_unended_sessions_and_isolates_failures()
    {
        var (svc, paths) = MakeService();
        // Finalized session: the scan must not touch it.
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0100_Webex_done", "Done");
        // Unended session: recoverable.
        const string open = "2026-07-03_0200_Webex_open";
        await WriteUnendedSessionAsync(paths, open);
        // Unended session engineered to FAIL: transcript.jsonl exists as a DIRECTORY, so the
        // recovery-marker append (TranscriptStore.AppendAsync) throws on Windows.
        const string broken = "2026-07-03_0300_Webex_broken";
        await WriteUnendedSessionAsync(paths, broken);
        Directory.CreateDirectory(paths.TranscriptJsonl(broken));

        var result = await svc.RecoverAllAsync(CancellationToken.None);

        Assert.Equal([open], result.RecoveredIds);
        var failure = Assert.Single(result.Failures);        // reported, not thrown, not aborting
        Assert.Equal(broken, failure.Id);
        Assert.False(string.IsNullOrEmpty(failure.Error));

        // The recovered session really finalized (RecoverIfNeededAsync semantics).
        var record = await new SessionStore(paths.SessionJson(open)).ReadAsync(CancellationToken.None);
        Assert.True(record!.Recovered);
        Assert.NotNull(record.EndedAtUtc);
        Assert.True(File.Exists(paths.SessionTxt(open)));

        // The untouched finalized session was not re-marked.
        var done = await new SessionStore(paths.SessionJson("2026-07-03_0100_Webex_done"))
            .ReadAsync(CancellationToken.None);
        Assert.False(done!.Recovered);
    }

    [Fact]
    public async Task CascadeMatterAsync_regenerates_only_tagged_sessions_and_reports_progress()
    {
        var (svc, paths) = MakeService();
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0100_Webex_tagged", "Tagged",
            matterIds: ["M-2026-001"]);
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0130_Webex_untagged", "Untagged");
        var progress = new ImmediateProgress();

        await svc.CascadeMatterAsync("M-2026-001", progress, CancellationToken.None);

        Assert.True(File.Exists(paths.SessionTxt("2026-07-03_0100_Webex_tagged")));
        Assert.False(File.Exists(paths.SessionTxt("2026-07-03_0130_Webex_untagged")));
        Assert.Equal([1], progress.Reports);                 // one tagged session -> one report
    }

    [Fact]
    public async Task RegenerateAllAsync_touches_every_session_and_counts_up()
    {
        var (svc, paths) = MakeService();
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0100_Webex_a", "A");
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0130_Webex_b", "B");
        var progress = new ImmediateProgress();

        await svc.RegenerateAllAsync(progress, CancellationToken.None);

        Assert.True(File.Exists(paths.SessionTxt("2026-07-03_0100_Webex_a")));
        Assert.True(File.Exists(paths.SessionTxt("2026-07-03_0130_Webex_b")));
        Assert.Equal([1, 2], progress.Reports);              // monotonic completed-count
    }

    /// <summary>Synchronous IProgress: Progress&lt;T&gt; posts to a SynchronizationContext and
    /// would race the assertions; this records inline, deterministically.</summary>
    private sealed class ImmediateProgress : IProgress<int>
    {
        public readonly List<int> Reports = new();
        public void Report(int value) { lock (Reports) Reports.Add(value); }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public Settings Current { get; set; } = new();
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }

    private sealed class NoopRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
    }
}
