using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public sealed class SessionDeleterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public void SendToRecycleBin(string path) => Recycled.Add(path);
    }

    [Fact]
    public async Task Delete_sends_the_whole_session_folder_to_the_recycle_bin()
    {
        string id = "2026-07-01_1000_Webex_x";
        await new SessionStore(Paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 1, 10, 30, 0, TimeSpan.Zero),
        }, default);
        var bin = new FakeRecycleBin();

        await new SessionDeleter(Paths, bin).DeleteAsync(id, default);

        Assert.Equal([Paths.SessionDir(id)], bin.Recycled);   // the FOLDER, exactly once
    }

    [Fact]
    public async Task Delete_of_missing_folder_throws_DirectoryNotFound_and_recycles_nothing()
    {
        var bin = new FakeRecycleBin();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => new SessionDeleter(Paths, bin).DeleteAsync("no-such-session", default));
        Assert.Empty(bin.Recycled);
    }
}
