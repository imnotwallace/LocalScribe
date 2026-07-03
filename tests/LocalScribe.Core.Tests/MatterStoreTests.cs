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

    [Fact]
    public async Task Archived_roundtrips_into_matter_and_index_entry()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            var store = new MatterStore(dir);
            await store.CreateAsync(Sample() with { Archived = true });

            var loaded = await store.LoadAsync("M-2026-014");
            Assert.True(loaded!.Archived);
            Assert.Equal(2, loaded.SchemaVersion);

            var index = await store.ListAsync();
            Assert.Single(index.Matters);
            Assert.True(index.Matters[0].Archived);          // carried into the index entry

            await store.SaveAsync(Sample() with { Archived = false });
            index = await store.ListAsync();
            Assert.False(index.Matters[0].Archived);         // un-archive propagates too
        }
        finally { CleanRoot(dir); }
    }

    [Fact]
    public async Task V1_matter_loads_with_archived_false_and_rewrites_at_v2()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            string matterDir = Path.Combine(dir, "M-2025-001");
            Directory.CreateDirectory(matterDir);
            await File.WriteAllTextAsync(Path.Combine(matterDir, "matter.json"),
                "{\"schemaVersion\":1,\"id\":\"M-2025-001\",\"name\":\"Old matter\"}");

            var store = new MatterStore(dir);
            var back = await store.LoadAsync("M-2025-001");
            Assert.False(back!.Archived);                    // missing field -> false
            Assert.Equal(2, back.SchemaVersion);
            Assert.Equal("Old matter", back.Name);

            string json = await File.ReadAllTextAsync(Path.Combine(matterDir, "matter.json"));
            Assert.Contains("\"schemaVersion\": 2", json);   // write-migrated on load

            var index = await store.ListAsync();             // migration save also upserts the index
            Assert.Single(index.Matters);
            Assert.False(index.Matters[0].Archived);
        }
        finally { CleanRoot(dir); }
    }

    [Fact]
    public async Task V1_index_loads_entries_with_archived_false()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "matters.json"),
                "{\"schemaVersion\":1,\"matters\":[{\"id\":\"M-2025-001\",\"name\":\"Old\",\"sessionCount\":3}]}");

            var index = await new MatterStore(dir).ListAsync();
            Assert.Single(index.Matters);
            Assert.False(index.Matters[0].Archived);
            Assert.Equal(3, index.Matters[0].SessionCount);
        }
        finally { CleanRoot(dir); }
    }

    [Fact]
    public async Task Rejects_newer_matter_version()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            string matterDir = Path.Combine(dir, "M-2026-001");
            Directory.CreateDirectory(matterDir);
            await File.WriteAllTextAsync(Path.Combine(matterDir, "matter.json"), "{\"schemaVersion\":3}");
            await Assert.ThrowsAsync<NotSupportedException>(
                () => new MatterStore(dir).LoadAsync("M-2026-001"));
        }
        finally { CleanRoot(dir); }
    }

    private static void CleanRoot(string mattersDir)
    {
        string? root = Path.GetDirectoryName(mattersDir);
        if (root is not null && Directory.Exists(root)) Directory.Delete(root, true);
    }
}
