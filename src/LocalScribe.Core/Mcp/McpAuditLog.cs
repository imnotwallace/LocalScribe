using System.Text;
using System.Text.Json;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Mcp;

/// <summary>What has ever left via MCP: one JSON line per tool call, including denied ones.
/// Never contains returned transcript text — args and counts only. Monthly files, no pruning
/// (keep-everything posture).</summary>
public sealed record McpAuditEntry(DateTimeOffset TsUtc, string Tool, string ArgsJson,
    IReadOnlyList<string> SessionIds, IReadOnlyList<string> MatterIds,
    int ResultCount, int ResultChars, string Outcome);

public sealed class McpAuditLog(StoragePaths paths, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AppendAsync(McpAuditEntry entry, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.McpAuditDir);
        string file = Path.Combine(paths.McpAuditDir,
            $"audit-{time.GetUtcNow():yyyyMM}.jsonl");
        string line = JsonSerializer.Serialize(entry, McpJsonOptions.Line) + Environment.NewLine;
        await _gate.WaitAsync(ct);
        try
        {
            await using var s = new FileStream(file, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            await s.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
        }
        finally { _gate.Release(); }
    }
}
