using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class MatterStoreTests
{
    private static Matter Sample() => new()
    {
        Id = "M-2026-014",
        Name = "Doe v. State",
        Reference = "CR-2026-014",
        Description = "Custody / bail proceedings.",
        DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
        Roster = new[]
        {
            new RosterMember { Id = "p-self", Name = "Sam", Role = "Attorney" },
            new RosterMember { Id = "p-alice", Name = "Alice Client", Role = "Client" },
        },
        Vocabulary = new Vocabulary { Terms = new[] { "arraignment" }, Corrections = new Dictionary<string, string> { ["auth"] = "OAuth" } },
    };

    [Fact]
    public async Task Create_writes_matter_and_index_entry()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            var store = new MatterStore(dir);
            await store.CreateAsync(Sample());

            var loaded = await store.LoadAsync("M-2026-014");
            Assert.Equal("Doe v. State", loaded!.Name);
            Assert.Equal("OAuth", loaded.Vocabulary.Corrections["auth"]);
            Assert.Equal(2, loaded.Roster.Count);

            var index = await store.ListAsync();
            Assert.Single(index.Matters);
            Assert.Equal("M-2026-014", index.Matters[0].Id);
            Assert.Equal(0, index.Matters[0].SessionCount);          // recompute deferred to Stage 4
        }
        finally { CleanRoot(dir); }
    }

    [Fact]
    public async Task Save_upserts_existing_index_entry_without_duplicating()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            var store = new MatterStore(dir);
            await store.CreateAsync(Sample());
            await store.SaveAsync(Sample() with { Name = "Doe v. State (renamed)" });

            var index = await store.ListAsync();
            Assert.Single(index.Matters);                            // still one entry
            Assert.Equal("Doe v. State (renamed)", index.Matters[0].Name);
        }
        finally { CleanRoot(dir); }
    }

    [Fact]
    public async Task List_on_empty_store_returns_empty_index()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            var index = await new MatterStore(dir).ListAsync();
            Assert.Empty(index.Matters);
        }
        finally { CleanRoot(dir); }
    }

    private static void CleanRoot(string mattersDir)
    {
        string? root = Path.GetDirectoryName(mattersDir);
        if (root is not null && Directory.Exists(root)) Directory.Delete(root, true);
    }
}
