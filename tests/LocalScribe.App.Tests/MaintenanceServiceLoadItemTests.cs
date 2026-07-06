using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MaintenanceServiceLoadItemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-loaditem-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (MaintenanceService Svc, StoragePaths Paths) MakeService()
    {
        var paths = new StoragePaths(_root);
        var svc = new MaintenanceService(paths, new FakeSettingsService(), new FakeRecycleBin(),
            TimeProvider.System);
        return (svc, paths);
    }

    /// <summary>A finalized on-disk session fixture: valid v3 session.json + meta.json (mirrors
    /// MaintenanceServiceTests.WriteFinalizedSessionAsync).</summary>
    private static async Task WriteFinalizedSessionAsync(StoragePaths paths, string id, string title)
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
            new SessionMeta { Title = title, MatterIds = [] }, CancellationToken.None);
    }

    [Fact]
    public async Task LoadSessionItemAsync_returns_the_session_by_id()
    {
        var (svc, paths) = MakeService();
        const string id = "2026-07-03_0100_Webex_hearing";
        await WriteFinalizedSessionAsync(paths, id, "Hearing");

        var item = await svc.LoadSessionItemAsync(id, CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal(id, item!.Id);
        Assert.Equal("Hearing", item.Meta.Title);

        Assert.Null(await svc.LoadSessionItemAsync("does-not-exist", CancellationToken.None));
    }

    /// <summary>Contract: an absent session.json returns null (the other case above), but a
    /// PRESENT-but-malformed session.json must THROW, not silently return null - a corrupt record
    /// and a deleted one are distinguishable for this evidentiary product (unlike
    /// SessionCatalog.ListAsync's best-effort bulk catch-and-count).</summary>
    [Fact]
    public async Task LoadSessionItemAsync_throws_when_session_json_is_malformed()
    {
        var (svc, paths) = MakeService();
        const string id = "2026-07-03_0200_Webex_corrupt";
        Directory.CreateDirectory(paths.SessionDir(id));
        await File.WriteAllTextAsync(paths.SessionJson(id), "{ not valid json !!", CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(
            () => svc.LoadSessionItemAsync(id, CancellationToken.None));
    }
}
