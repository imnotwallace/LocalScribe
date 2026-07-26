using LocalScribe.Core.Mcp;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public sealed class McpAuditLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static McpAuditEntry Entry(string tool = "search_transcripts", string outcome = "ok")
        => new(new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero), tool,
            "{\"query\":\"settlement\"}", ["s1"], ["m-001"], 3, 1200, outcome);

    [Fact]
    public async Task Appends_one_snake_case_json_line_per_call_to_monthly_file()
    {
        var time = new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero));
        var log = new McpAuditLog(Paths, time);
        await log.AppendAsync(Entry(), default);
        await log.AppendAsync(Entry(outcome: "denied"), default);
        string file = Path.Combine(Paths.McpAuditDir, "audit-202607.jsonl");
        var lines = await File.ReadAllLinesAsync(file);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"tool\":\"search_transcripts\"", lines[0]);
        Assert.Contains("\"result_chars\":1200", lines[0]);
        Assert.Contains("\"outcome\":\"denied\"", lines[1]);
        Assert.DoesNotContain("\n", lines[0]); // one line per entry
    }

    [Fact]
    public async Task Monthly_rotation_uses_the_time_provider()
    {
        var time = new ManualUtcTimeProvider(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        await new McpAuditLog(Paths, time).AppendAsync(Entry(), default);
        Assert.True(File.Exists(Path.Combine(Paths.McpAuditDir, "audit-202608.jsonl")));
    }

    [Fact]
    public async Task Append_tolerates_concurrent_reader()
    {
        var time = new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero));
        var log = new McpAuditLog(Paths, time);
        await log.AppendAsync(Entry(), default);
        string file = Path.Combine(Paths.McpAuditDir, "audit-202607.jsonl");
        using var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await log.AppendAsync(Entry(), default); // must not throw while a reader holds the file
        Assert.Equal(2, (await File.ReadAllLinesAsync(file)).Length);
    }
}
