using System.Diagnostics;
using System.Text.Json;
using System.Threading;
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
    /// <summary>Every SDK wait in this file (process spawn + initialize handshake, list-tools,
    /// tool calls) is bounded by this deadline via <see cref="WithDeadlineAsync{T}"/>. If the
    /// spawned server starts but stalls, the affected test fails with a named TimeoutException
    /// instead of hanging forever and wedging CI. 30s is long enough to tolerate a cold-machine
    /// process spawn + handshake without being flaky, short enough to fail fast otherwise.</summary>
    private const int WireTimeoutSeconds = 30;

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
        return await WithDeadlineAsync(
            ct => McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Command = "dotnet",
                Arguments = [dll, "--storage-root", _root],
                Name = "localscribe-wire-test",
            }), cancellationToken: ct),
            "McpClient.CreateAsync (server spawn + initialize handshake)");
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { /* best-effort temp cleanup */ }
    }

    /// <summary>Bounds a single SDK await with <see cref="WireTimeoutSeconds"/> so a stalled
    /// process/handshake/call fails the test with a clear, named message instead of hanging.
    /// Overloaded for both Task-returning (McpClient.CreateAsync) and ValueTask-returning
    /// (ListToolsAsync, CallToolAsync) SDK members.</summary>
    private static async Task<T> WithDeadlineAsync<T>(Func<CancellationToken, Task<T>> operation,
        string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(WireTimeoutSeconds));
        try
        {
            return await operation(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{what} did not complete within {WireTimeoutSeconds}s (hang guard tripped).");
        }
    }

    private static async Task<T> WithDeadlineAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        string what)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(WireTimeoutSeconds));
        try
        {
            return await operation(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{what} did not complete within {WireTimeoutSeconds}s (hang guard tripped).");
        }
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await WithDeadlineAsync(
            ct => _client!.CallToolAsync(tool, args, cancellationToken: ct),
            $"CallToolAsync(\"{tool}\")");
        string text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Single().Text;
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Initialize_lists_exactly_the_six_contract_tools()
    {
        var tools = await WithDeadlineAsync(
            ct => _client!.ListToolsAsync(cancellationToken: ct), "ListToolsAsync");
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

    /// <summary>Fix 1 (review): the "no transcript text in the audit log" guarantee must be proven
    /// on the path where it can actually fail - a SUCCESSFUL call that genuinely returns real
    /// fixture content. A denied call (see
    /// <see cref="Revocation_mid_conversation_denies_uniformly_and_audits"/>) can never contain
    /// transcript text structurally, since McpCorpus.VisibleAsync throws before the transcript is
    /// ever read and the entry is written with empty session ids and zero counts - so asserting
    /// absence only after a denial is tautological and would not catch a regression that started
    /// recording result snippets on the success path. This test drives a successful
    /// search_transcripts AND a successful read_transcript (the read especially returns full
    /// transcript rows), then confirms the audit file has real "ok" entries for both tools while
    /// still never containing the transcript text itself.</summary>
    [Fact]
    public async Task Successful_calls_are_audited_without_recording_transcript_text()
    {
        var search = await CallAsync("search_transcripts", new() { ["query"] = "settlement" });
        Assert.Equal(1, search.GetProperty("contract_version").GetInt32());
        var hit = search.GetProperty("hits")[0];
        Assert.Equal("s1", hit.GetProperty("session_id").GetString());

        var read = await CallAsync("read_transcript",
            new() { ["session_id"] = "s1", ["around_seq"] = hit.GetProperty("seq").GetInt32(),
                    ["context"] = 2 });
        string readText = read.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("text").GetString())
            .Aggregate("", (a, b) => a + " " + b);
        // Sanity: the tool call genuinely returned the real fixture text over the wire - otherwise
        // the absence-from-the-audit-log assertion below would be checking nothing meaningful.
        Assert.Contains("forty thousand", readText);

        string auditDir = Paths.McpAuditDir;
        Assert.True(Directory.Exists(auditDir));
        string audit = await File.ReadAllTextAsync(Directory.GetFiles(auditDir, "*.jsonl").Single());

        // Prove the file actually holds populated, successful entries for both calls - not an
        // empty file and not just the denied-path shape - so the DoesNotContain below is meaningful.
        Assert.Contains("\"tool\":\"search_transcripts\"", audit);
        Assert.Contains("\"tool\":\"read_transcript\"", audit);
        Assert.Contains("\"outcome\":\"ok\"", audit);

        // The actual guarantee under test: even though both calls above returned real transcript
        // content to the client, none of it made it into the audit log.
        Assert.DoesNotContain("forty thousand", audit);
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
        if (_client is not null)
        {
            // Guard: hand the field off to a local before disposing. If this dispose throws,
            // _client is already null, so IAsyncLifetime.DisposeAsync's later
            // "if (_client is not null) await _client.DisposeAsync()" is skipped instead of
            // disposing the same (already-throwing) client a second time and turning this test's
            // real failure into a confusing double-dispose error.
            var clientToDispose = _client;
            _client = null;
            await clientToDispose.DisposeAsync();
        }
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

    /// <summary>Fix 6 (review): a truncated --storage-root (the flag with no value following - it's
    /// the last argument) must fail the process loudly with a non-zero exit and a clear STDERR
    /// message, never silently fall back to the settings file's root and start serving a different
    /// corpus than the one the user intended. Spawns its own process directly (not through
    /// McpClient, which expects a successful handshake) so it can observe the exit code.</summary>
    [Fact]
    public async Task Truncated_storage_root_argument_fails_the_process_loudly()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Mcp.dll");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--storage-root");   // deliberately no value follows

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        bool exited = proc.WaitForExit(WireTimeoutSeconds * 1000);
        Assert.True(exited, "process did not exit for a truncated --storage-root argument");

        string stderr = await stderrTask;
        _ = await stdoutTask;
        Assert.NotEqual(0, proc.ExitCode);
        Assert.Contains("--storage-root", stderr, StringComparison.Ordinal);
    }
}
