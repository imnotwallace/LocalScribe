using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public sealed class MatterDeleterTests : IDisposable
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

    private async Task CreateMatterAsync(string id, string name)
        => await new MatterStore(Paths.MattersDir).CreateAsync(new Matter
        {
            Id = id, Name = name,
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        });

    private async Task WriteSessionTaggedAsync(string id, params string[] matterIds)
    {
        var started = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        await new SessionStore(Paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started, EndedAtUtc = started.AddMinutes(30),
        }, default);
        await new MetadataStore(Paths.MetaJson(id))
            .SaveAsync(new SessionMeta { Title = id, MatterIds = matterIds }, default);
    }

    [Fact]
    public async Task CountReferences_counts_sessions_whose_meta_tags_the_matter()
    {
        await CreateMatterAsync("M-2026-001", "Doe v. State");
        await WriteSessionTaggedAsync("2026-07-01_1000_Webex_a", "M-2026-001");
        await WriteSessionTaggedAsync("2026-07-01_1100_Webex_b", "M-2026-001", "M-2026-002");
        await WriteSessionTaggedAsync("2026-07-01_1200_Webex_c");   // untagged

        var deleter = new MatterDeleter(Paths, new FakeRecycleBin());

        Assert.Equal(2, await deleter.CountReferencesAsync("M-2026-001", default));
        Assert.Equal(0, await deleter.CountReferencesAsync("M-2026-999", default));
    }

    [Fact]
    public async Task Delete_is_blocked_while_referenced_and_names_the_count()
    {
        await CreateMatterAsync("M-2026-001", "Doe v. State");
        await WriteSessionTaggedAsync("2026-07-01_1000_Webex_a", "M-2026-001");
        await WriteSessionTaggedAsync("2026-07-01_1100_Webex_b", "M-2026-001");
        var bin = new FakeRecycleBin();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new MatterDeleter(Paths, bin).DeleteAsync("M-2026-001", default));

        Assert.Contains("2", ex.Message);                                     // the count, for the dialog
        Assert.Empty(bin.Recycled);                                           // nothing recycled
        var index = await new MatterStore(Paths.MattersDir).ListAsync();
        Assert.Single(index.Matters);                                         // index entry intact
    }

    [Fact]
    public async Task Delete_of_unreferenced_matter_recycles_folder_and_removes_index_entry()
    {
        await CreateMatterAsync("M-2026-001", "Keep me");
        await CreateMatterAsync("M-2026-002", "Delete me");
        var bin = new FakeRecycleBin();

        await new MatterDeleter(Paths, bin).DeleteAsync("M-2026-002", default);

        Assert.Equal([Path.Combine(Paths.MattersDir, "M-2026-002")], bin.Recycled);
        var index = await new MatterStore(Paths.MattersDir).ListAsync();
        var entry = Assert.Single(index.Matters);
        Assert.Equal("M-2026-001", entry.Id);
    }

    [Fact]
    public async Task Delete_with_vanished_folder_still_removes_index_entry_without_recycling()
    {
        await CreateMatterAsync("M-2026-001", "Half-state");
        Directory.Delete(Path.Combine(Paths.MattersDir, "M-2026-001"), true);   // external tampering
        var bin = new FakeRecycleBin();

        await new MatterDeleter(Paths, bin).DeleteAsync("M-2026-001", default);  // heals, no throw

        Assert.Empty(bin.Recycled);
        var index = await new MatterStore(Paths.MattersDir).ListAsync();
        Assert.Empty(index.Matters);
    }
}
