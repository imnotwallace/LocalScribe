using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public sealed class RecoveryScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task WriteSessionAsync(string id, DateTimeOffset? endedUtc, bool recovered = false)
        => await new SessionStore(Paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id,
            App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
            EndedAtUtc = endedUtc,
            Recovered = recovered,
        }, default);

    [Fact]
    public async Task Finds_only_sessions_with_null_EndedAtUtc()
    {
        await WriteSessionAsync("2026-07-01_1000_Webex_crashed", endedUtc: null);
        await WriteSessionAsync("2026-07-01_1100_Webex_done",
            endedUtc: new DateTimeOffset(2026, 7, 1, 11, 30, 0, TimeSpan.Zero));
        await WriteSessionAsync("2026-07-01_1200_Webex_recovered",
            endedUtc: new DateTimeOffset(2026, 7, 1, 12, 30, 0, TimeSpan.Zero), recovered: true);

        var ids = await new RecoveryScanner(Paths).FindUnendedAsync(default);

        Assert.Equal(["2026-07-01_1000_Webex_crashed"], ids);   // finalized + already-recovered ignored
    }

    [Fact]
    public async Task Tolerates_unreadable_folders_without_throwing()
    {
        await WriteSessionAsync("2026-07-01_1000_Webex_crashed", endedUtc: null);
        Directory.CreateDirectory(Paths.SessionDir("no-session-json"));
        Directory.CreateDirectory(Paths.SessionDir("corrupt"));
        await File.WriteAllTextAsync(Paths.SessionJson("corrupt"), "{ not json");

        var ids = await new RecoveryScanner(Paths).FindUnendedAsync(default);

        Assert.Equal(["2026-07-01_1000_Webex_crashed"], ids);   // unreadable = catalog's concern, not recovery's
    }

    [Fact]
    public async Task Missing_sessions_dir_yields_empty_list()
    {
        var ids = await new RecoveryScanner(Paths).FindUnendedAsync(default);
        Assert.Empty(ids);
    }
}
