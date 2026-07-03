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
