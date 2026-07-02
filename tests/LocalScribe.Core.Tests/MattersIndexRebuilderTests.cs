using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public sealed class MattersIndexRebuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>Writes matter.json directly WITHOUT touching the index - an orphan, exactly the
    /// crash-window half-state MatterStore.cs:6-7 documents.</summary>
    private async Task WriteMatterFolderOnlyAsync(string id, string name, bool archived = false)
        => await JsonFile.WriteAsync(Paths.MatterJson(id), new Matter
        {
            SchemaVersion = MatterStore.Version,
            Id = id,
            Name = name,
            Archived = archived,
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        }, default);

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
    public async Task Rebuild_adopts_orphan_matter_folders_missing_from_index()
    {
        await WriteMatterFolderOnlyAsync("M-2026-001", "Doe v. State");     // never indexed

        var rebuilt = await new MattersIndexRebuilder(Paths).RebuildAsync(default);

        var entry = Assert.Single(rebuilt.Matters);
        Assert.Equal("M-2026-001", entry.Id);
        Assert.Equal("Doe v. State", entry.Name);
        Assert.Equal(0, entry.SessionCount);

        var onDisk = await new MatterStore(Paths.MattersDir).ListAsync();   // written, not just returned
        Assert.Single(onDisk.Matters);
    }

    [Fact]
    public async Task Rebuild_drops_index_entries_whose_folder_vanished()
    {
        var store = new MatterStore(Paths.MattersDir);
        await store.CreateAsync(new Matter
        {
            Id = "M-2026-001", Name = "Kept",
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await store.CreateAsync(new Matter
        {
            Id = "M-2026-002", Name = "Vanished",
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        });
        Directory.Delete(Path.Combine(Paths.MattersDir, "M-2026-002"), true);   // external tampering

        var rebuilt = await new MattersIndexRebuilder(Paths).RebuildAsync(default);

        var entry = Assert.Single(rebuilt.Matters);
        Assert.Equal("M-2026-001", entry.Id);
    }

    [Fact]
    public async Task Rebuild_recomputes_session_counts_from_metas_and_preserves_archived()
    {
        await WriteMatterFolderOnlyAsync("M-2026-001", "Tagged twice");
        await WriteMatterFolderOnlyAsync("M-2026-002", "Untagged, archived", archived: true);
        await WriteSessionTaggedAsync("2026-07-01_1000_Webex_a", "M-2026-001");
        await WriteSessionTaggedAsync("2026-07-01_1100_Webex_b", "M-2026-001");
        await WriteSessionTaggedAsync("2026-07-01_1200_Webex_c");            // no matter

        // Seed a stale index (wrong count, wrong archived) to prove rebuild overwrites it.
        await JsonFile.WriteAsync(Paths.MattersIndexJson, new MattersIndex
        {
            SchemaVersion = MatterStore.Version,
            Matters = new[]
            {
                new MattersIndexEntry { Id = "M-2026-001", Name = "Tagged twice", SessionCount = 99 },
            },
        }, default);

        var rebuilt = await new MattersIndexRebuilder(Paths).RebuildAsync(default);

        Assert.Equal(2, rebuilt.Matters.Count);
        Assert.Equal(2, rebuilt.Matters.Single(e => e.Id == "M-2026-001").SessionCount);
        Assert.False(rebuilt.Matters.Single(e => e.Id == "M-2026-001").Archived);
        Assert.Equal(0, rebuilt.Matters.Single(e => e.Id == "M-2026-002").SessionCount);
        Assert.True(rebuilt.Matters.Single(e => e.Id == "M-2026-002").Archived);   // from matter.json
    }

    [Fact]
    public async Task Rebuild_on_empty_root_yields_empty_index()
    {
        var rebuilt = await new MattersIndexRebuilder(Paths).RebuildAsync(default);
        Assert.Empty(rebuilt.Matters);
    }

    [Fact]
    public async Task ApplyTagDelta_increments_decrements_and_floors_at_zero()
    {
        var store = new MatterStore(Paths.MattersDir);
        await store.CreateAsync(new Matter
        {
            Id = "M-2026-001", Name = "A",
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await store.CreateAsync(new Matter
        {
            Id = "M-2026-002", Name = "B",
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        });

        var rebuilder = new MattersIndexRebuilder(Paths);
        await rebuilder.ApplyTagDeltaAsync(addedMatterIds: ["M-2026-001"], removedMatterIds: [], default);
        await rebuilder.ApplyTagDeltaAsync(addedMatterIds: ["M-2026-001"], removedMatterIds: ["M-2026-002"], default);

        var index = await store.ListAsync();
        Assert.Equal(2, index.Matters.Single(e => e.Id == "M-2026-001").SessionCount);
        Assert.Equal(0, index.Matters.Single(e => e.Id == "M-2026-002").SessionCount);   // floored, not -1
    }

    [Fact]
    public async Task ApplyTagDelta_ignores_ids_missing_from_index()
    {
        var store = new MatterStore(Paths.MattersDir);
        await store.CreateAsync(new Matter
        {
            Id = "M-2026-001", Name = "A",
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        });

        // Unknown ids must not throw and must not invent entries - RebuildAsync is the self-heal.
        await new MattersIndexRebuilder(Paths)
            .ApplyTagDeltaAsync(addedMatterIds: ["M-2026-999"], removedMatterIds: ["M-2026-888"], default);

        var index = await store.ListAsync();
        var entry = Assert.Single(index.Matters);
        Assert.Equal("M-2026-001", entry.Id);
        Assert.Equal(0, entry.SessionCount);
    }
}
