using System.Text.Json;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Storage;
using ModelContextProtocol.Client;

namespace LocalScribe.Mcp.Tests;

/// <summary>Wire-level tests: drive the REAL LocalScribe.Mcp.exe (via `dotnet LocalScribe.Mcp.dll`)
/// over stdio through the pinned ModelContextProtocol 2.0.0-rc.1 client. In-process unit tests
/// elsewhere already cover McpCorpus logic directly; these are the only tests that prove the
/// actual handshake -> list-tools -> call-tool round trip works, and that stdout stays pure JSON-RPC
/// (any stray byte on stdout would break MCP's initialize handshake, so a passing Initialize test
/// IS the stdout-purity proof).</summary>
public sealed class McpWireTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private McpClient? _client;

    public async Task InitializeAsync()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Settlement call", "m-001",
            new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero), "webex",
            "we agreed the settlement figure is forty thousand");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);

        _client = await StartClientAsync();
    }

    private async Task<McpClient> StartClientAsync()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Mcp.dll");
        Assert.True(File.Exists(dll), $"server dll not found at {dll}");
        return await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet",
            Arguments = [dll, "--storage-root", _root],
            Name = "localscribe-wire-test",
        }));
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { /* best-effort temp cleanup */ }
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client!.CallToolAsync(tool, args!);
        string text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Single().Text;
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Initialize_lists_exactly_the_six_contract_tools()
    {
        var tools = await _client!.ListToolsAsync();
        Assert.Equal(
            new[] { "get_summary", "list_matters", "list_sessions", "read_transcript",
                    "search_transcripts", "search_transcripts_semantic" },
            tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Search_then_read_round_trip_over_real_stdio()
    {
        var search = await CallAsync("search_transcripts",
            new() { ["query"] = "settlement" });
        Assert.Equal(1, search.GetProperty("contract_version").GetInt32());
        var hit = search.GetProperty("hits")[0];
        Assert.Equal("s1", hit.GetProperty("session_id").GetString());

        var read = await CallAsync("read_transcript",
            new() { ["session_id"] = "s1", ["around_seq"] = hit.GetProperty("seq").GetInt32(),
                    ["context"] = 2 });
        Assert.Contains("settlement figure",
            read.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("text").GetString())
                .Aggregate("", (a, b) => a + " " + b));
    }

    [Fact]
    public async Task Revocation_mid_conversation_denies_uniformly_and_audits()
    {
        // Same live client/session as construction - consent is re-read from disk on every call
        // (McpConsentStore.ReadCurrentAsync, no caching), so this proves revocation takes effect
        // mid-conversation without restarting the server or the client.
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument { Enabled = false }, default);
        var r = await CallAsync("search_transcripts", new() { ["query"] = "settlement" });
        Assert.Equal("MCP access not enabled in LocalScribe Settings",
            r.GetProperty("error").GetString());
        string auditDir = Paths.McpAuditDir;
        Assert.True(Directory.Exists(auditDir));
        string audit = await File.ReadAllTextAsync(Directory.GetFiles(auditDir, "*.jsonl").Single());
        Assert.Contains("\"outcome\":\"denied\"", audit);
        Assert.DoesNotContain("forty thousand", audit); // transcript text NEVER in the audit log
    }

    /// <summary>Carries over a Critical fix from Task 8 that had no dedicated home at the time:
    /// LocalScribeTools.RunAsync computes the response envelope FIRST and always returns it; the
    /// audit append happens afterward in TryAuditAsync, whose failure is caught, written to
    /// stderr, and swallowed. Proven here by making the audit directory itself unwritable (pre-
    /// creating "&lt;root&gt;/mcp/audit" as a FILE, so McpAuditLog.AppendAsync's
    /// Directory.CreateDirectory(paths.McpAuditDir) throws) and then asserting a normal tool call
    /// still returns a non-error envelope.</summary>
    [Fact]
    public async Task Tool_call_still_succeeds_when_the_audit_log_cannot_be_written()
    {
        if (_client is not null) await _client.DisposeAsync();
        string mcpDir = Path.Combine(_root, "mcp");
        Directory.CreateDirectory(mcpDir);
        string auditPath = Path.Combine(mcpDir, "audit");
        await File.WriteAllTextAsync(auditPath, "not a directory");
        Assert.True(File.Exists(auditPath));
        Assert.False(Directory.Exists(auditPath));

        _client = await StartClientAsync();
        var r = await CallAsync("search_transcripts", new() { ["query"] = "settlement" });

        Assert.Equal(1, r.GetProperty("contract_version").GetInt32());
        var hit = r.GetProperty("hits")[0];
        Assert.Equal("s1", hit.GetProperty("session_id").GetString());
        var probe = default(JsonElement);
        Assert.False(r.TryGetProperty("error", out probe));

        // The audit path is still a file, not a directory - the append genuinely never landed.
        Assert.True(File.Exists(auditPath));
        Assert.False(Directory.Exists(auditPath));
    }
}
