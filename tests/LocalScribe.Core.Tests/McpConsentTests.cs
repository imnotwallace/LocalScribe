using LocalScribe.Core.Mcp;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

public sealed class McpConsentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Absent_consent_file_reads_as_disabled()
    {
        var doc = await new McpConsentStore(Paths).ReadCurrentAsync(default);
        Assert.False(doc.Enabled);
        Assert.Empty(doc.AllowedMatterIds);
    }

    [Fact]
    public async Task Corrupt_consent_file_reads_as_disabled()
    {
        Directory.CreateDirectory(Paths.McpDir);
        await File.WriteAllTextAsync(Paths.McpConsentJson, "{not json");
        var doc = await new McpConsentStore(Paths).ReadCurrentAsync(default);
        Assert.False(doc.Enabled);
    }

    [Fact]
    public async Task Save_then_read_roundtrips_snake_case()
    {
        var store = new McpConsentStore(Paths);
        await store.SaveAsync(new McpConsentDocument
        {
            Enabled = true,
            AllowedMatterIds = ["m-001"],
            AllowUnassigned = true,
            UpdatedUtc = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero),
        }, default);
        var json = await File.ReadAllTextAsync(Paths.McpConsentJson);
        Assert.Contains("\"allowed_matter_ids\"", json);
        Assert.Contains("\"allow_unassigned\"", json);
        var doc = await store.ReadCurrentAsync(default);
        Assert.True(doc.Enabled);
        Assert.Equal(["m-001"], doc.AllowedMatterIds);
    }

    [Fact]
    public async Task External_rewrite_is_picked_up_on_next_read()
    {
        var store = new McpConsentStore(Paths);
        await store.SaveAsync(new McpConsentDocument { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
        _ = await store.ReadCurrentAsync(default);
        // Simulate the App revoking from another process: rewrite with a newer mtime.
        var other = new McpConsentStore(Paths);
        await other.SaveAsync(new McpConsentDocument { Enabled = false }, default);
        File.SetLastWriteTimeUtc(Paths.McpConsentJson, DateTime.UtcNow.AddSeconds(5));
        var doc = await store.ReadCurrentAsync(default);
        Assert.False(doc.Enabled);
    }

    private static SearchSessionEntry Entry(params string[] matterIds)
        => new() { SessionId = "s1", MatterIds = matterIds };

    [Fact]
    public void Disabled_consent_hides_everything()
        => Assert.False(McpConsentFilter.SessionVisible(Entry("m-001"),
            new McpConsentDocument { Enabled = false, AllowedMatterIds = ["m-001"] }));

    [Fact]
    public void Session_visible_only_when_all_matters_allowlisted()
    {
        var consent = new McpConsentDocument { Enabled = true, AllowedMatterIds = ["m-001"] };
        Assert.True(McpConsentFilter.SessionVisible(Entry("m-001"), consent));
        Assert.False(McpConsentFilter.SessionVisible(Entry("m-002"), consent));
        Assert.False(McpConsentFilter.SessionVisible(Entry("m-001", "m-002"), consent)); // partial => hidden
    }

    [Fact]
    public void Unassigned_sessions_ride_the_toggle()
    {
        Assert.False(McpConsentFilter.SessionVisible(Entry(),
            new McpConsentDocument { Enabled = true }));
        Assert.True(McpConsentFilter.SessionVisible(Entry(),
            new McpConsentDocument { Enabled = true, AllowUnassigned = true }));
    }
}
