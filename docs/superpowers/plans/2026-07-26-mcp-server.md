# MCP Server (LocalScribe.Mcp) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A standalone, strictly read-only MCP server exe (stdio transport) that lets MCP clients search (lexical + semantic) and read the LocalScribe transcript corpus, gated by a per-matter consent allowlist and an append-only audit log.

**Architecture:** All corpus logic lives in `LocalScribe.Core` under a new `Mcp/` folder (consent store/filter, audit log, read-only lexical catalog, corpus façade returning DTOs), fully testable with fakes. A thin new `src/LocalScribe.Mcp` console exe hosts it on the official `ModelContextProtocol` C# SDK with `[McpServerToolType]` tools. The App gains a Settings "MCP Access" section that writes `mcp/consent.json`. Spec: `docs/superpowers/specs/2026-07-26-mcp-server-design.md`.

**Tech Stack:** net10.0-windows, C#, xUnit, `ModelContextProtocol` NuGet (official C# MCP SDK, prerelease channel), Microsoft.Extensions.Hosting, WPF (Settings section only).

## Global Constraints

- **Read-only firewall:** no task may add code that writes sessions, edits, speakers, matters, `index/search-index.json`, or `index/semantic/*.vec`. The ONLY writes allowed from server code paths are `mcp/consent.json` (App side) and `mcp/audit/*.jsonl` (server side).
- **Fail closed:** absent/corrupt/disabled `mcp/consent.json` ⇒ every tool denied with exactly `MCP access not enabled in LocalScribe Settings`.
- **Multi-matter rule (privilege-safe):** a session tagged with multiple matters is visible only if EVERY one of its `MatterIds` is allowlisted; sessions with empty `MatterIds` are visible only if `allow_unassigned` is true.
- **Stdio purity:** the Mcp exe writes protocol frames only to stdout; all logging goes to stderr.
- **Denied reads are indistinguishable from missing:** `read_transcript`/`get_summary` on a non-visible session return the same error text `not found or not exposed`.
- **JSON naming:** all MCP-facing JSON (consent, audit, tool responses) uses `JsonNamingPolicy.SnakeCaseLower`.
- **Contract:** every tool response envelope carries `contract_version: 1`.
- All corpus file reads open with `FileShare.ReadWrite | FileShare.Delete`.
- Existing suites must stay green: `dotnet test tests/LocalScribe.Core.Tests` and `dotnet test tests/LocalScribe.App.Tests`. No Unicode emojis in any test script.
- Commit after every task with the shown message; append the standard co-author trailer used in this repo.

---

### Task 1: Consent document, store, and filter (Core)

**Files:**
- Modify: `src/LocalScribe.Core/Storage/StoragePaths.cs` (add MCP paths)
- Create: `src/LocalScribe.Core/Mcp/McpConsent.cs`
- Test: `tests/LocalScribe.Core.Tests/McpConsentTests.cs`

**Interfaces:**
- Consumes: `StoragePaths` (existing).
- Produces: `StoragePaths.McpDir`, `StoragePaths.McpConsentJson`, `StoragePaths.McpAuditDir`; `McpConsentDocument { SchemaVersion, Enabled, AllowedMatterIds, AllowUnassigned, UpdatedUtc }`; `McpConsentStore(StoragePaths paths)` with `Task<McpConsentDocument> ReadCurrentAsync(CancellationToken)` (mtime-cached, fail closed) and `Task SaveAsync(McpConsentDocument, CancellationToken)` (atomic); `static McpConsentFilter.SessionVisible(SearchSessionEntry, McpConsentDocument) : bool`; `McpToolException(string message, string outcome)`.

- [ ] **Step 1: Write the failing tests**

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpConsentTests"`
Expected: FAIL — `McpConsentStore` / `McpConsentDocument` / `McpConsentFilter` do not exist.

- [ ] **Step 3: Implement**

In `StoragePaths.cs`, next to `SearchIndexJson`:

```csharp
public string McpDir => Path.Combine(Root, "mcp");
public string McpConsentJson => Path.Combine(McpDir, "consent.json");
public string McpAuditDir => Path.Combine(McpDir, "audit");
```

`src/LocalScribe.Core/Mcp/McpConsent.cs`:

```csharp
using System.Text.Json;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Mcp;

/// <summary>Consent contract for MCP exposure (spec 2026-07-26). Absent file == disabled:
/// exposure of the privileged corpus is opt-in, never a default.</summary>
public sealed record McpConsentDocument
{
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; }
    public IReadOnlyList<string> AllowedMatterIds { get; init; } = [];
    public bool AllowUnassigned { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>Thrown by corpus operations; Outcome is the audit outcome ("denied" | "error").</summary>
public sealed class McpToolException(string message, string outcome) : Exception(message)
{
    public string Outcome { get; } = outcome;
}

public sealed class McpConsentStore(StoragePaths paths)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private McpConsentDocument? _cached;
    private long _cachedTicks = -1;

    /// <summary>Re-checked on every tool call so unticking a matter revokes mid-conversation.
    /// Any read/parse failure yields disabled — fail closed, never fail open.</summary>
    public async Task<McpConsentDocument> ReadCurrentAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(paths.McpConsentJson)) return Disabled();
            long ticks = File.GetLastWriteTimeUtc(paths.McpConsentJson).Ticks;
            if (_cached is not null && ticks == _cachedTicks) return _cached;
            await using var s = new FileStream(paths.McpConsentJson, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var doc = await JsonSerializer.DeserializeAsync<McpConsentDocument>(s, Json, ct)
                      ?? Disabled();
            if (doc.SchemaVersion > 1) doc = Disabled();
            (_cached, _cachedTicks) = (doc, ticks);
            return doc;
        }
        catch (OperationCanceledException) { throw; }
        catch { return Disabled(); }
    }

    public async Task SaveAsync(McpConsentDocument doc, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.McpDir);
        string tmp = paths.McpConsentJson + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(doc, Json), ct);
        File.Move(tmp, paths.McpConsentJson, overwrite: true);
        _cachedTicks = -1;
    }

    private static McpConsentDocument Disabled() => new();
}

public static class McpConsentFilter
{
    public const string NotEnabledMessage = "MCP access not enabled in LocalScribe Settings";
    public const string NotFoundMessage = "not found or not exposed";

    /// <summary>Privilege-safe polarity: a multi-matter session is visible only if EVERY matter
    /// it is tagged with is allowlisted; an unassigned session only via the explicit toggle.</summary>
    public static bool SessionVisible(SearchSessionEntry entry, McpConsentDocument consent)
    {
        if (!consent.Enabled) return false;
        if (entry.MatterIds.Count == 0) return consent.AllowUnassigned;
        return entry.MatterIds.All(m => consent.AllowedMatterIds.Contains(m, StringComparer.Ordinal));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpConsentTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Run the full Core suite, then commit**

Run: `dotnet test tests/LocalScribe.Core.Tests`
Expected: PASS (plus the two pre-existing known failures if present on this machine — compare against master before blaming your change).

```bash
git add src/LocalScribe.Core/Storage/StoragePaths.cs src/LocalScribe.Core/Mcp/McpConsent.cs tests/LocalScribe.Core.Tests/McpConsentTests.cs
git commit -m "feat(mcp): consent document, fail-closed store, and all-matters-allowlisted filter"
```

---

### Task 2: Append-only audit log (Core)

**Files:**
- Create: `src/LocalScribe.Core/Mcp/McpAuditLog.cs`
- Test: `tests/LocalScribe.Core.Tests/McpAuditLogTests.cs`

**Interfaces:**
- Consumes: `StoragePaths.McpAuditDir` (Task 1), `TimeProvider`.
- Produces: `McpAuditEntry(DateTimeOffset TsUtc, string Tool, string ArgsJson, IReadOnlyList<string> SessionIds, IReadOnlyList<string> MatterIds, int ResultCount, int ResultChars, string Outcome)`; `McpAuditLog(StoragePaths paths, TimeProvider time)` with `Task AppendAsync(McpAuditEntry entry, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Storage;

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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpAuditLogTests"`
Expected: FAIL — `McpAuditLog` does not exist.

- [ ] **Step 3: Implement**

```csharp
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
```

`McpConsentStore.Json` is `WriteIndented = true` (right for the consent file); the audit line must be single-line, so also add this helper to `McpConsent.cs` in this task:

```csharp
public static class McpJsonOptions
{
    /// <summary>Single-line snake_case for audit lines and tool responses.</summary>
    public static readonly JsonSerializerOptions Line = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpAuditLogTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Mcp/McpAuditLog.cs src/LocalScribe.Core/Mcp/McpConsent.cs tests/LocalScribe.Core.Tests/McpAuditLogTests.cs
git commit -m "feat(mcp): append-only monthly audit log (denied calls logged, text never)"
```

---

### Task 3: Test session seeder extraction + read-only lexical catalog (Core)

**Files:**
- Create: `tests/LocalScribe.Core.Tests/TestSessionSeeder.cs` (extracted from `SearchIndexServiceTests.cs`)
- Modify: `tests/LocalScribe.Core.Tests/SearchIndexServiceTests.cs` (call the extracted seeder)
- Create: `src/LocalScribe.Core/Mcp/McpLexicalCatalog.cs`
- Test: `tests/LocalScribe.Core.Tests/McpLexicalCatalogTests.cs`

**Interfaces:**
- Consumes: `SearchIndexStore.LoadAsync` (read-only seed), `SearchIndexBuilder.BuildEntryAsync` / `ComputeStamps`, `ManualUtcTimeProvider` (existing test helper).
- Produces: `McpLexicalCatalog(StoragePaths paths, Settings settings, TimeProvider time, TimeSpan? refreshInterval = null)` with `Task<IReadOnlyDictionary<string, SearchSessionEntry>> GetEntriesAsync(CancellationToken)`, `DateTimeOffset LastRefreshUtc { get; }`, `int SkippedSessions { get; }`; test-side `TestSessionSeeder.WriteBasicSession(StoragePaths paths, string sessionId, string title, string? matterId, DateTimeOffset startedUtc, string app, params string[] lines)` and `TestSessionSeeder.EnsureMatter(StoragePaths paths, string matterId, string name, string? reference = null)`.

- [ ] **Step 1: Extract the seeder**

Open `tests/LocalScribe.Core.Tests/SearchIndexServiceTests.cs`. It already contains private helpers that write a session folder (session.json, transcript.jsonl, meta.json, ...) under a temp `StoragePaths` root — `SearchIndexService` tests could not pass without them. Move those helper methods verbatim into a new `internal static class TestSessionSeeder` in `tests/LocalScribe.Core.Tests/TestSessionSeeder.cs`, and update `SearchIndexServiceTests` to call them via `TestSessionSeeder.`. Then add the two canonical wrappers below (adapting their bodies to whatever the extracted helpers take — the WRAPPER signatures are the contract later tasks compile against):

```csharp
internal static class TestSessionSeeder
{
    // ... moved private helpers become internal here ...

    /// <summary>Canonical wrapper used by Mcp tests: one session, one speaker per line,
    /// line i gets StartMs = i * 1000 and Seq = i.</summary>
    internal static void WriteBasicSession(StoragePaths paths, string sessionId, string title,
        string? matterId, DateTimeOffset startedUtc, string app, params string[] lines)
    { /* delegate to the moved helpers */ }

    /// <summary>Creates matters/matters.json + matters/{id}/matter.json via MatterStore.</summary>
    internal static void EnsureMatter(StoragePaths paths, string matterId, string name,
        string? reference = null)
        => new LocalScribe.Core.Storage.MatterStore(paths.MattersDir)
            .CreateAsync(new LocalScribe.Core.Model.Matter { Id = matterId, Name = name, Reference = reference })
            .GetAwaiter().GetResult();
}
```

(If `Matter`'s namespace differs, follow the compiler; `MatterStore` ctor takes the matters DIR, not `StoragePaths`.)

- [ ] **Step 2: Verify no regression from the extraction**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SearchIndexServiceTests"`
Expected: PASS, same count as before the extraction.

Commit the pure refactor:

```bash
git add tests/LocalScribe.Core.Tests/TestSessionSeeder.cs tests/LocalScribe.Core.Tests/SearchIndexServiceTests.cs
git commit -m "test(mcp): extract shared TestSessionSeeder from SearchIndexServiceTests"
```

- [ ] **Step 3: Write the failing catalog tests**

```csharp
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

public sealed class McpLexicalCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Builds_entries_from_disk_without_touching_the_cache_file()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call one", null, T0, "webex",
            "we discussed the settlement figure");
        var catalog = new McpLexicalCatalog(Paths, new Settings { StorageRoot = _root },
            new ManualUtcTimeProvider(T0));
        var entries = await catalog.GetEntriesAsync(default);
        Assert.True(entries.ContainsKey("s1"));
        Assert.False(File.Exists(Paths.SearchIndexJson)); // read-only: never writes the App's cache
    }

    [Fact]
    public async Task Reuses_a_fresh_cache_entry_and_leaves_the_cache_byte_identical()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call one", null, T0, "webex", "hello");
        // Seed the cache the way the App would: build + save via the real store.
        var entry = await SearchIndexBuilder.BuildEntryAsync(Paths,
            new Settings { StorageRoot = _root }, TimeProvider.System, "s1", default);
        await new SearchIndexStore(Paths).SaveAsync(
            new SearchIndexCache { Sessions = [entry] }, default);
        byte[] before = await File.ReadAllBytesAsync(Paths.SearchIndexJson);

        var catalog = new McpLexicalCatalog(Paths, new Settings { StorageRoot = _root },
            new ManualUtcTimeProvider(T0));
        var entries = await catalog.GetEntriesAsync(default);
        Assert.True(entries.ContainsKey("s1"));
        Assert.Equal(before, await File.ReadAllBytesAsync(Paths.SearchIndexJson));
    }

    [Fact]
    public async Task New_session_appears_after_the_refresh_window()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call one", null, T0, "webex", "hello");
        var time = new ManualUtcTimeProvider(T0);
        var catalog = new McpLexicalCatalog(Paths, new Settings { StorageRoot = _root }, time,
            refreshInterval: TimeSpan.FromSeconds(10));
        Assert.Single(await catalog.GetEntriesAsync(default));

        TestSessionSeeder.WriteBasicSession(Paths, "s2", "Call two", null, T0.AddMinutes(1), "webex", "world");
        Assert.Single(await catalog.GetEntriesAsync(default)); // inside throttle window: stale snapshot
        time.Set(T0.AddSeconds(11));
        Assert.Equal(2, (await catalog.GetEntriesAsync(default)).Count);
    }

    [Fact]
    public async Task Corrupt_session_is_skipped_and_counted_not_fatal()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call one", null, T0, "webex", "hello");
        Directory.CreateDirectory(Path.Combine(Paths.SessionsDir, "s2"));
        await File.WriteAllTextAsync(Paths.SessionJson("s2"), "{corrupt");
        var catalog = new McpLexicalCatalog(Paths, new Settings { StorageRoot = _root },
            new ManualUtcTimeProvider(T0));
        var entries = await catalog.GetEntriesAsync(default);
        Assert.Single(entries);
        Assert.Equal(1, catalog.SkippedSessions);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpLexicalCatalogTests"`
Expected: FAIL — `McpLexicalCatalog` does not exist.

- [ ] **Step 5: Implement**

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Mcp;

/// <summary>Read-only sibling of SearchIndexService for the standalone MCP server: builds the
/// in-memory lexical index from disk (using index/search-index.json as a read-only SEED), and
/// refreshes on query with an mtime short-circuit. NEVER writes the cache — self-heal writes
/// stay App-only (spec: read-only enforcement is structural).</summary>
public sealed class McpLexicalCatalog(StoragePaths paths, Settings settings, TimeProvider time,
    TimeSpan? refreshInterval = null)
{
    private readonly TimeSpan _interval = refreshInterval ?? TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, SearchSessionEntry> _entries = [];
    private bool _cacheSeedLoaded;
    private IReadOnlyDictionary<string, SearchSessionEntry>? _cacheSeed;

    public DateTimeOffset LastRefreshUtc { get; private set; } = DateTimeOffset.MinValue;
    public int SkippedSessions { get; private set; }

    public async Task<IReadOnlyDictionary<string, SearchSessionEntry>> GetEntriesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (now - LastRefreshUtc < _interval) return _entries;

            if (!_cacheSeedLoaded)
            {
                _cacheSeedLoaded = true;
                var cache = await new SearchIndexStore(paths).LoadAsync(ct);
                _cacheSeed = cache?.Sessions.ToDictionary(s => s.SessionId);
            }

            var next = new Dictionary<string, SearchSessionEntry>();
            int skipped = 0;
            if (Directory.Exists(paths.SessionsDir))
            {
                foreach (string dir in Directory.EnumerateDirectories(paths.SessionsDir))
                {
                    ct.ThrowIfCancellationRequested();
                    string id = Path.GetFileName(dir);
                    var known = _entries.GetValueOrDefault(id) ?? _cacheSeed?.GetValueOrDefault(id);
                    try
                    {
                        if (known is not null &&
                            SearchIndexBuilder.ComputeStamps(paths, id, known.VersionId) == known.Stamps)
                        { next[id] = known; continue; }
                        next[id] = await SearchIndexBuilder.BuildEntryAsync(paths, settings, time, id, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { skipped++; }
                }
            }
            (_entries, SkippedSessions, LastRefreshUtc) = (next, skipped, now);
            return _entries;
        }
        finally { _gate.Release(); }
    }
}
```

Note: `SearchFreshnessStamps` is a record — `==` is value equality. If `ComputeStamps`'s signature needs the settings/time arguments in this codebase's actual shape, follow the compiler; the verified signature is `ComputeStamps(StoragePaths paths, string sessionId, string versionId)`.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpLexicalCatalogTests"`
Expected: PASS (4 tests).

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Mcp/McpLexicalCatalog.cs tests/LocalScribe.Core.Tests/McpLexicalCatalogTests.cs
git commit -m "feat(mcp): read-only lexical catalog with refresh-on-query mtime throttle"
```

---

### Task 4: Move ProcessAssistantHelper from App to Core

**Files:**
- Create: `src/LocalScribe.Core/Assistant/ProcessAssistantHelper.cs` (moved)
- Delete: `src/LocalScribe.App/Services/ProcessAssistantHelper.cs`
- Modify: every App usage site (`App.xaml.cs` and any other `new Services.ProcessAssistantHelper(...)` / `using` references — find them with `grep -rn "ProcessAssistantHelper" src/`)

**Interfaces:**
- Produces: `LocalScribe.Core.Assistant.ProcessAssistantHelper(string exePath, string? arguments = null) : IAssistantProcessFactory` — byte-identical behavior, new namespace. The Mcp exe (Task 8) needs this because it cannot reference the WPF App project.

- [ ] **Step 1: Move the file**

Move `src/LocalScribe.App/Services/ProcessAssistantHelper.cs` to `src/LocalScribe.Core/Assistant/ProcessAssistantHelper.cs`. Change only the namespace (`LocalScribe.App.Services` → `LocalScribe.Core.Assistant`) and keep the class body identical. It is pure `System.Diagnostics.Process` code with no WPF dependency (verified), so Core can host it.

- [ ] **Step 2: Fix all references**

Run: `grep -rn "ProcessAssistantHelper" src/ tests/`
Update each site (e.g. `App.xaml.cs:992` uses `new Services.ProcessAssistantHelper(exe)`) to the new namespace. Remove now-unused `using`s.

- [ ] **Step 3: Build + full suites**

Run: `dotnet build LocalScribe.slnx && dotnet test tests/LocalScribe.Core.Tests && dotnet test tests/LocalScribe.App.Tests`
Expected: build clean, both suites at their pre-task pass counts.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor(mcp): move ProcessAssistantHelper to Core so the Mcp exe can spawn the embed helper"
```

---

### Task 5: DTOs + corpus façade part 1 — lexical search, list_sessions, list_matters (Core)

**Files:**
- Create: `src/LocalScribe.Core/Mcp/McpDtos.cs`
- Create: `src/LocalScribe.Core/Mcp/McpCorpus.cs`
- Test: `tests/LocalScribe.Core.Tests/McpCorpusSearchTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3 types; `SearchQueryEngine.Run/PassesFacets`, `MatterStore.ListAsync`, `StoragePaths.SummariesJson`.
- Produces: the DTO records below and `McpCorpus` with:
  - ctor `McpCorpus(StoragePaths paths, Settings settings, TimeProvider time, McpConsentStore consent, McpLexicalCatalog catalog, SemanticIndexStore semanticStore, MatterStore matters, SummaryStore summaries, IMcpEmbeddingProvider embeddingProvider)`
  - `Task<McpSearchResponse> SearchAsync(string query, string? matterId, string? fromDate, string? toDate, string? app, int limit, CancellationToken ct)`
  - `Task<McpSessionListResponse> ListSessionsAsync(string? matterId, string? fromDate, string? toDate, string? app, int offset, int limit, CancellationToken ct)`
  - `Task<McpMattersResponse> ListMattersAsync(CancellationToken ct)`
  - `interface IMcpEmbeddingProvider { Task<(IEmbeddingClient Client, string Method)> GetAsync(CancellationToken ct); }` (used in Task 6; declared now so the ctor is final)

- [ ] **Step 1: Write `McpDtos.cs`**

```csharp
namespace LocalScribe.Core.Mcp;

// Serialized with McpJsonOptions.Line (snake_case). ContractVersion == 1 on every envelope.
public sealed record McpSearchHitDto(string SessionId, string Title, string DateLocal, string App,
    IReadOnlyList<string> Matters, string Speaker, int Seq, int PartIndex, long StartMs,
    string Snippet, bool MatchesOriginalOnly);
public sealed record McpSearchResponse(int ContractVersion, DateTimeOffset IndexAsOfUtc,
    int SkippedSessions, int TotalHits, IReadOnlyList<McpSearchHitDto> Hits);

public sealed record McpCoverage(int SessionsEligible, int SessionsCovered, int StaleCount);
public sealed record McpSemanticHitDto(string SessionId, string Title, string DateLocal, string App,
    IReadOnlyList<string> Matters, int StartSeq, int StartPartIndex, long StartMs, float Score,
    string Snippet);
public sealed record McpSemanticResponse(int ContractVersion, DateTimeOffset IndexAsOfUtc,
    McpCoverage Coverage, IReadOnlyList<McpSemanticHitDto> Hits);

public sealed record McpTranscriptRowDto(string Kind, int? Seq, long StartMs, long EndMs,
    string? Speaker, string Text);
public sealed record McpReadResponse(int ContractVersion, string SessionId, string VersionId,
    IReadOnlyList<McpTranscriptRowDto> Rows, string? NextCursor);

public sealed record McpSessionDto(string SessionId, string Title, string DateLocal, string App,
    IReadOnlyList<string> Matters, long? ApproxDurationMs, bool HasSummary);
public sealed record McpSessionListResponse(int ContractVersion, DateTimeOffset IndexAsOfUtc,
    int Total, IReadOnlyList<McpSessionDto> Sessions);

public sealed record McpMatterDto(string Id, string Name, string? Reference, int SessionCount);
public sealed record McpMattersResponse(int ContractVersion, IReadOnlyList<McpMatterDto> Matters);

public sealed record McpSummaryResponse(int ContractVersion, string SessionId,
    string ContentMarkdown, DateTimeOffset CreatedAt, string ModelFile, string Backend,
    bool CudaFellToCpu, bool Stale, string SourceTranscriptVersion);
```

- [ ] **Step 2: Write the failing tests**

```csharp
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class McpCorpusSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class NoEmbeddings : IMcpEmbeddingProvider
    {
        public Task<(IEmbeddingClient, string)> GetAsync(CancellationToken ct)
            => throw new McpToolException("semantic unavailable: no embed helper in test", "error");
    }

    private McpCorpus Corpus()
    {
        var settings = new Settings { StorageRoot = _root };
        var time = new ManualUtcTimeProvider(T0.AddDays(1));
        return new McpCorpus(Paths, settings, time,
            new McpConsentStore(Paths),
            new McpLexicalCatalog(Paths, settings, time),
            new SemanticIndexStore(Paths),
            new MatterStore(Paths.MattersDir),
            new SummaryStore(Paths),
            new NoEmbeddings());
    }

    private async Task SeedTwoMattersAsync()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones", "2026-14");
        TestSessionSeeder.EnsureMatter(Paths, "m-002", "Estate of Brown");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Settlement call", "m-001", T0, "webex",
            "we agreed the settlement figure is forty thousand");
        TestSessionSeeder.WriteBasicSession(Paths, "s2", "Estate call", "m-002", T0.AddHours(2), "webex",
            "the settlement of the estate remains open");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"], UpdatedUtc = T0 }, default);
    }

    [Fact]
    public async Task Search_denied_when_consent_absent()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", null, T0, "webex", "hello");
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => Corpus().SearchAsync("hello", null, null, null, null, 10, default));
        Assert.Equal("denied", ex.Outcome);
        Assert.Equal(McpConsentFilter.NotEnabledMessage, ex.Message);
    }

    [Fact]
    public async Task Search_only_sees_allowlisted_matters()
    {
        await SeedTwoMattersAsync();
        var r = await Corpus().SearchAsync("settlement", null, null, null, null, 10, default);
        Assert.All(r.Hits, h => Assert.Equal("s1", h.SessionId));
        Assert.True(r.TotalHits >= 1);
        Assert.Contains("settlement", r.Hits[0].Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, r.Hits[0].Seq);
    }

    [Fact]
    public async Task Search_limit_caps_flattened_hits()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex",
            "alpha beta", "alpha gamma", "alpha delta");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
        var r = await Corpus().SearchAsync("alpha", null, null, null, null, 2, default);
        Assert.Equal(2, r.Hits.Count);
        Assert.Equal(3, r.TotalHits); // honest total even when capped
    }

    [Fact]
    public async Task ListSessions_filters_facets_and_flags_summaries()
    {
        await SeedTwoMattersAsync();
        Directory.CreateDirectory(Paths.AssistantDir("s1"));
        await File.WriteAllTextAsync(Paths.SummariesJson("s1"), "{}");
        var r = await Corpus().ListSessionsAsync(null, null, null, null, 0, 20, default);
        var s = Assert.Single(r.Sessions);
        Assert.Equal("s1", s.SessionId);
        Assert.True(s.HasSummary);
    }

    [Fact]
    public async Task ListMatters_shows_only_allowlisted()
    {
        await SeedTwoMattersAsync();
        var r = await Corpus().ListMattersAsync(default);
        var m = Assert.Single(r.Matters);
        Assert.Equal("m-001", m.Id);
        Assert.Equal("Smith v Jones", m.Name);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpCorpusSearchTests"`
Expected: FAIL — `McpCorpus` / `IMcpEmbeddingProvider` do not exist.

- [ ] **Step 4: Implement `McpCorpus.cs` (search/list half)**

```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Mcp;

/// <summary>Resolves the embed client lazily (manifest hash-verify is expensive; the server
/// must start instantly). Throws McpToolException("semantic unavailable: ...", "error") when
/// the helper exe or embedding model is missing.</summary>
public interface IMcpEmbeddingProvider
{
    Task<(IEmbeddingClient Client, string Method)> GetAsync(CancellationToken ct);
}

/// <summary>The one façade every MCP tool calls. Consent is re-read per call (revocation is
/// live) and applied BEFORE any engine runs — non-visible sessions never reach ranking, so
/// nothing leaks via scores, counts, or coverage.</summary>
public sealed class McpCorpus(StoragePaths paths, Settings settings, TimeProvider time,
    McpConsentStore consent, McpLexicalCatalog catalog, SemanticIndexStore semanticStore,
    MatterStore matters, SummaryStore summaries, IMcpEmbeddingProvider embeddingProvider)
{
    public const int ContractVersion = 1;
    public const int MaxSearchLimit = 50;
    public const int MaxListLimit = 100;

    private async Task<(McpConsentDocument Consent,
        IReadOnlyDictionary<string, SearchSessionEntry> Visible)> VisibleAsync(CancellationToken ct)
    {
        var doc = await consent.ReadCurrentAsync(ct);
        if (!doc.Enabled)
            throw new McpToolException(McpConsentFilter.NotEnabledMessage, "denied");
        var all = await catalog.GetEntriesAsync(ct);
        var visible = all.Where(kv => McpConsentFilter.SessionVisible(kv.Value, doc))
                         .ToDictionary(kv => kv.Key, kv => kv.Value);
        return (doc, visible);
    }

    internal static SearchQuery BuildQuery(string text, string? matterId, string? fromDate,
        string? toDate, string? app)
    {
        DateTimeOffset? from = null, to = null;
        if (fromDate is not null)
        {
            if (!DateTimeOffset.TryParse(fromDate + "T00:00:00Z", out var f))
                throw new McpToolException($"invalid from_date '{fromDate}' (expected yyyy-MM-dd)", "error");
            from = f;
        }
        if (toDate is not null)
        {
            if (!DateTimeOffset.TryParse(toDate + "T00:00:00Z", out var t))
                throw new McpToolException($"invalid to_date '{toDate}' (expected yyyy-MM-dd)", "error");
            to = t.AddDays(1); // engine upper bound is exclusive; make to_date inclusive of that day
        }
        return new SearchQuery(text, matterId, from, to, app);
    }

    private async Task<IReadOnlyDictionary<string, MattersIndexEntry>> MatterIndexAsync(CancellationToken ct)
        => (await matters.ListAsync(ct)).Matters.ToDictionary(m => m.Id);

    /// <summary>Mirrors SearchPageViewModel.MatterLabel: "{id}-{ref} {name}" / "{id} {name}".</summary>
    internal static string MatterLabel(string id, IReadOnlyDictionary<string, MattersIndexEntry> index)
        => index.TryGetValue(id, out var m)
            ? string.IsNullOrEmpty(m.Reference) ? $"{id} {m.Name}" : $"{id}-{m.Reference} {m.Name}"
            : id;

    private static string DateLocal(SearchSessionEntry e)
        => (e.UtcOffsetMinutes is int off
            ? e.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(off))
            : e.StartedAtUtc).ToString("yyyy-MM-dd HH:mm");

    public async Task<McpSearchResponse> SearchAsync(string query, string? matterId,
        string? fromDate, string? toDate, string? app, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new McpToolException("query must be non-empty", "error");
        limit = Math.Clamp(limit, 1, MaxSearchLimit);
        var (_, visible) = await VisibleAsync(ct);
        var q = BuildQuery(query, matterId, fromDate, toDate, app);
        var results = SearchQueryEngine.Run(visible.Values, q);
        var labels = await MatterIndexAsync(ct);
        var flat = results.SelectMany(r => r.Hits.Select(h => (r.Session, Hit: h))).ToList();
        var hits = flat.Take(limit).Select(x => new McpSearchHitDto(
            x.Session.SessionId, x.Session.Title, DateLocal(x.Session), x.Session.App,
            x.Session.MatterIds.Select(id => MatterLabel(id, labels)).ToList(),
            x.Hit.Speaker, x.Hit.Seq, x.Hit.PartIndex, x.Hit.StartMs,
            x.Hit.Snippet, x.Hit.MatchesOriginalOnly)).ToList();
        return new McpSearchResponse(ContractVersion, catalog.LastRefreshUtc,
            catalog.SkippedSessions, flat.Count, hits);
    }

    public async Task<McpSessionListResponse> ListSessionsAsync(string? matterId,
        string? fromDate, string? toDate, string? app, int offset, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
        offset = Math.Max(0, offset);
        var (_, visible) = await VisibleAsync(ct);
        var q = BuildQuery("*", matterId, fromDate, toDate, app); // text unused by PassesFacets
        var labels = await MatterIndexAsync(ct);
        var filtered = visible.Values.Where(e => SearchQueryEngine.PassesFacets(e, q))
            .OrderByDescending(e => e.StartedAtUtc).ToList();
        var page = filtered.Skip(offset).Take(limit).Select(e => new McpSessionDto(
            e.SessionId, e.Title, DateLocal(e), e.App,
            e.MatterIds.Select(id => MatterLabel(id, labels)).ToList(),
            e.Lines.Count > 0 ? e.Lines[^1].StartMs : null,
            File.Exists(paths.SummariesJson(e.SessionId)))).ToList();
        return new McpSessionListResponse(ContractVersion, catalog.LastRefreshUtc,
            filtered.Count, page);
    }

    public async Task<McpMattersResponse> ListMattersAsync(CancellationToken ct)
    {
        var doc = await consent.ReadCurrentAsync(ct);
        if (!doc.Enabled)
            throw new McpToolException(McpConsentFilter.NotEnabledMessage, "denied");
        var index = await matters.ListAsync(ct);
        var allowed = index.Matters
            .Where(m => doc.AllowedMatterIds.Contains(m.Id, StringComparer.Ordinal))
            .Select(m => new McpMatterDto(m.Id, m.Name, m.Reference, m.SessionCount))
            .ToList();
        return new McpMattersResponse(ContractVersion, allowed);
    }
}
```

(If `SearchQueryEngine.PassesFacets` is `internal`, widen it to `public` — it is already the shared facet authority for `SemanticQueryEngine`; check its current modifier first.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpCorpusSearchTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Mcp/McpDtos.cs src/LocalScribe.Core/Mcp/McpCorpus.cs tests/LocalScribe.Core.Tests/McpCorpusSearchTests.cs
git commit -m "feat(mcp): corpus facade - consent-gated lexical search, list_sessions, list_matters"
```

---

### Task 6: Corpus façade part 2 — semantic search with coverage honesty (Core)

**Files:**
- Modify: `src/LocalScribe.Core/Mcp/McpCorpus.cs`
- Test: `tests/LocalScribe.Core.Tests/McpCorpusSemanticTests.cs`

**Interfaces:**
- Consumes: `SemanticIndexStore.LoadAsync/ListSessionIds`, `SemanticQueryEngine.Run(float[] queryVector, IReadOnlyDictionary<string,SearchSessionEntry> metadata, IReadOnlyDictionary<string,SemanticSidecar> sidecars, SearchQuery query, IReadOnlyList<SearchResult> lexicalResults)`, `IEmbeddingClient.EmbedAsync("query", texts, ct)`, `SearchIndexBuilder.ComputeStamps`.
- Produces: `Task<McpSemanticResponse> McpCorpus.SearchSemanticAsync(string query, string? matterId, string? fromDate, string? toDate, string? app, int limit, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

Reuse `FakeEmbeddingClient` behavior: the existing fake lives nested inside `SemanticIndexServiceTests` — do NOT move it (that file is not ours to churn); write a local fake here. Sidecars are seeded through the real `SemanticIndexStore`.

```csharp
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class McpCorpusSemanticTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FixedEmbeddings(string method) : IMcpEmbeddingProvider, IEmbeddingClient
    {
        public Task<(IEmbeddingClient, string)> GetAsync(CancellationToken ct)
            => Task.FromResult(((IEmbeddingClient)this, method));
        public Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts, CancellationToken ct)
            => Task.FromResult(new EmbeddingBatch(texts.Select(_ => new[] { 1f, 0f }).ToList(), method));
        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnavailableEmbeddings : IMcpEmbeddingProvider
    {
        public Task<(IEmbeddingClient, string)> GetAsync(CancellationToken ct)
            => throw new McpToolException("semantic unavailable: embedding model not installed", "error");
    }

    private McpCorpus Corpus(IMcpEmbeddingProvider embeddings)
    {
        var settings = new Settings { StorageRoot = _root };
        var time = new ManualUtcTimeProvider(T0.AddDays(1));
        return new McpCorpus(Paths, settings, time, new McpConsentStore(Paths),
            new McpLexicalCatalog(Paths, settings, time), new SemanticIndexStore(Paths),
            new MatterStore(Paths.MattersDir), new SummaryStore(Paths), embeddings);
    }

    private async Task<SearchSessionEntry> SeedSessionWithSidecarAsync(string method,
        bool staleStamps = false)
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Settlement call", "m-001", T0, "webex",
            "we agreed the settlement figure");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
        var entry = await SearchIndexBuilder.BuildEntryAsync(Paths,
            new Settings { StorageRoot = _root }, TimeProvider.System, "s1", default);
        var stamps = staleStamps ? new SearchFreshnessStamps { TranscriptTicks = 1 } : entry.Stamps;
        await new SemanticIndexStore(Paths).SaveAsync("s1", new SemanticSidecar(
            method, entry.VersionId, stamps, 2,
            [new SemanticChunk(0, 0, 0, 0, 1000, "we agreed the settlement figure")],
            [new[] { 1f, 0f }]), default);
        return entry;
    }

    [Fact]
    public async Task Semantic_hit_comes_back_with_anchor_score_and_full_coverage()
    {
        await SeedSessionWithSidecarAsync("fake@2");
        var r = await Corpus(new FixedEmbeddings("fake@2"))
            .SearchSemanticAsync("settlement number", null, null, null, null, 10, default);
        var hit = Assert.Single(r.Hits);
        Assert.Equal("s1", hit.SessionId);
        Assert.Equal(0, hit.StartSeq);
        Assert.True(hit.Score > 0.9f); // identical unit vectors
        Assert.Equal(new McpCoverage(1, 1, 0), r.Coverage);
    }

    [Fact]
    public async Task Stale_sidecar_counts_in_coverage_honesty()
    {
        await SeedSessionWithSidecarAsync("fake@2", staleStamps: true);
        var r = await Corpus(new FixedEmbeddings("fake@2"))
            .SearchSemanticAsync("settlement number", null, null, null, null, 10, default);
        Assert.Equal(new McpCoverage(1, 1, 1), r.Coverage); // covered but stale
    }

    [Fact]
    public async Task Method_mismatch_is_stale_not_a_match_source()
    {
        await SeedSessionWithSidecarAsync("other-model@2");
        var r = await Corpus(new FixedEmbeddings("fake@2"))
            .SearchSemanticAsync("settlement number", null, null, null, null, 10, default);
        Assert.Empty(r.Hits); // incomparable vectors are never scanned
        Assert.Equal(new McpCoverage(1, 1, 1), r.Coverage);
    }

    [Fact]
    public async Task Non_allowlisted_sessions_never_reach_the_ranker_or_coverage()
    {
        await SeedSessionWithSidecarAsync("fake@2");
        TestSessionSeeder.EnsureMatter(Paths, "m-002", "Estate of Brown");
        TestSessionSeeder.WriteBasicSession(Paths, "s2", "Estate call", "m-002", T0.AddHours(1),
            "webex", "estate settlement talk");
        var r = await Corpus(new FixedEmbeddings("fake@2"))
            .SearchSemanticAsync("settlement", null, null, null, null, 10, default);
        Assert.All(r.Hits, h => Assert.Equal("s1", h.SessionId));
        Assert.Equal(1, r.Coverage.SessionsEligible); // s2 invisible even to the denominator
    }

    [Fact]
    public async Task Missing_helper_surfaces_as_clear_error()
    {
        await SeedSessionWithSidecarAsync("fake@2");
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus(new UnavailableEmbeddings())
                .SearchSemanticAsync("settlement", null, null, null, null, 10, default));
        Assert.StartsWith("semantic unavailable", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpCorpusSemanticTests"`
Expected: FAIL — `SearchSemanticAsync` does not exist.

- [ ] **Step 3: Implement in `McpCorpus.cs`**

```csharp
private readonly Dictionary<string, (long Ticks, SemanticSidecar? Sidecar)> _sidecarCache = [];

private async Task<SemanticSidecar?> SidecarAsync(string sessionId, CancellationToken ct)
{
    string file = paths.SemanticSidecarFile(sessionId);
    long ticks = File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0;
    if (_sidecarCache.TryGetValue(sessionId, out var c) && c.Ticks == ticks) return c.Sidecar;
    var sc = ticks == 0 ? null : await semanticStore.LoadAsync(sessionId, ct);
    _sidecarCache[sessionId] = (ticks, sc);
    return sc;
}

public async Task<McpSemanticResponse> SearchSemanticAsync(string query, string? matterId,
    string? fromDate, string? toDate, string? app, int limit, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(query))
        throw new McpToolException("query must be non-empty", "error");
    limit = Math.Clamp(limit, 1, MaxSearchLimit);
    var (_, visible) = await VisibleAsync(ct);
    var (client, method) = await embeddingProvider.GetAsync(ct);
    var q = BuildQuery(query, matterId, fromDate, toDate, app);

    // Coverage over the VISIBLE set only — nothing leaks via the denominator.
    int covered = 0, stale = 0;
    var comparable = new Dictionary<string, SemanticSidecar>();
    foreach (var (id, entry) in visible)
    {
        ct.ThrowIfCancellationRequested();
        var sc = await SidecarAsync(id, ct);
        if (sc is null) continue;
        covered++;
        bool fresh = sc.Method == method && sc.VersionId == entry.VersionId && sc.Stamps == entry.Stamps;
        if (!fresh) { stale++; }
        if (sc.Method == method) comparable[id] = sc; // stale-but-comparable still scans (like the UI)
    }

    EmbeddingBatch batch;
    try { batch = await client.EmbedAsync("query", [query], CancellationToken.None); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    { throw new McpToolException($"semantic unavailable: query embed failed ({ex.Message})", "error"); }

    var results = SemanticQueryEngine.Run(batch.Embeddings[0], visible, comparable, q, []);
    var labels = await MatterIndexAsync(ct);
    var hits = results.SelectMany(r => r.Hits.Select(h => (r.Session, Hit: h)))
        .Take(limit)
        .Select(x => new McpSemanticHitDto(x.Session.SessionId, x.Session.Title,
            DateLocal(x.Session), x.Session.App,
            x.Session.MatterIds.Select(id => MatterLabel(id, labels)).ToList(),
            x.Hit.StartSeq, x.Hit.StartPartIndex, x.Hit.StartMs, x.Hit.Score, x.Hit.Snippet))
        .ToList();
    return new McpSemanticResponse(ContractVersion, catalog.LastRefreshUtc,
        new McpCoverage(visible.Count, covered, stale), hits);
}
```

Note the deliberate `CancellationToken.None` on the embed call — same discipline as `SemanticIndexService.QueryAsync`: a cancelled client request must not kill the warm helper mid-batch.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpCorpusSemanticTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Mcp/McpCorpus.cs tests/LocalScribe.Core.Tests/McpCorpusSemanticTests.cs
git commit -m "feat(mcp): semantic search over visible sidecars with coverage honesty"
```

---

### Task 7: Corpus façade part 3 — read_transcript spans + get_summary (Core)

**Files:**
- Modify: `src/LocalScribe.Core/Mcp/McpCorpus.cs`
- Test: `tests/LocalScribe.Core.Tests/McpCorpusReadTests.cs`

**Interfaces:**
- Consumes: `SessionProjectionLoader.LoadAsync(paths, settings, time, sessionId, ct)` → `LoadedProjection.Rows` (`DisplayRow { IsMarker, StartMs, EndMs, DisplayName, Text, Segments }`, `RowSegment.Seq/PartIndex`), `SummaryStore.LoadAsync`.
- Produces:
  - `Task<McpReadResponse> ReadTranscriptAsync(string sessionId, int? fromSeq, int? toSeq, int? aroundSeq, int context, string? cursor, CancellationToken ct)` — cursor format `"{versionId}:{nextRowIndex}"`, char budget `MaxReadChars = 15_000`.
  - `Task<McpSummaryResponse> GetSummaryAsync(string sessionId, CancellationToken ct)` — newest summary version.

- [ ] **Step 1: Write the failing tests**

```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class McpCorpusReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class NoEmbeddings : IMcpEmbeddingProvider
    {
        public Task<(LocalScribe.Core.Search.Semantic.IEmbeddingClient, string)> GetAsync(CancellationToken ct)
            => throw new McpToolException("semantic unavailable", "error");
    }

    private McpCorpus Corpus()
    {
        var settings = new Settings { StorageRoot = _root };
        var time = new ManualUtcTimeProvider(T0.AddDays(1));
        return new McpCorpus(Paths, settings, time, new McpConsentStore(Paths),
            new McpLexicalCatalog(Paths, settings, time), new SemanticIndexStore(Paths),
            new MatterStore(Paths.MattersDir), new SummaryStore(Paths), new NoEmbeddings());
    }

    private async Task SeedAsync(int lineCount = 6)
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex",
            Enumerable.Range(0, lineCount).Select(i => $"line number {i}").ToArray());
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
    }

    [Fact]
    public async Task Reads_a_seq_range_with_speaker_names()
    {
        await SeedAsync();
        var r = await Corpus().ReadTranscriptAsync("s1", 1, 3, null, 0, null, default);
        Assert.All(r.Rows.Where(x => x.Kind == "speech"),
            x => Assert.InRange(x.Seq!.Value, 1, 3));
        Assert.Contains(r.Rows, x => x.Text.Contains("line number 2"));
        Assert.All(r.Rows.Where(x => x.Kind == "speech"), x => Assert.False(string.IsNullOrEmpty(x.Speaker)));
        Assert.Null(r.NextCursor);
    }

    [Fact]
    public async Task Around_seq_returns_the_context_window()
    {
        await SeedAsync();
        var r = await Corpus().ReadTranscriptAsync("s1", null, null, 3, 1, null, default);
        var seqs = r.Rows.Where(x => x.Seq is not null).Select(x => x.Seq!.Value).ToList();
        Assert.Equal([2, 3, 4], seqs);
    }

    [Fact]
    public async Task Char_cap_pages_with_a_version_pinned_cursor()
    {
        await SeedAsync(lineCount: 40);
        // Tiny cap via the test seam so we don't need 15k chars of fixture text.
        var r = await Corpus().ReadTranscriptAsync("s1", null, null, null, 0, null, default,
            maxChars: 60);
        Assert.True(r.Rows.Count < 40);
        Assert.NotNull(r.NextCursor);
        Assert.StartsWith(r.VersionId + ":", r.NextCursor);
        var r2 = await Corpus().ReadTranscriptAsync("s1", null, null, null, 0, r.NextCursor, default,
            maxChars: 60);
        Assert.NotEqual(r.Rows[0].Seq, r2.Rows[0].Seq);
    }

    [Fact]
    public async Task Cursor_from_a_different_version_is_rejected()
    {
        await SeedAsync();
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus().ReadTranscriptAsync("s1", null, null, null, 0, "vOLD:2", default));
        Assert.Contains("version changed", ex.Message);
    }

    [Fact]
    public async Task Hidden_session_read_is_indistinguishable_from_missing()
    {
        await SeedAsync();
        TestSessionSeeder.WriteBasicSession(Paths, "s2", "Hidden", null, T0, "webex", "secret");
        var exHidden = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus().ReadTranscriptAsync("s2", null, null, null, 0, null, default));
        var exMissing = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus().ReadTranscriptAsync("nope", null, null, null, 0, null, default));
        Assert.Equal(exMissing.Message, exHidden.Message);
        Assert.Equal(McpConsentFilter.NotFoundMessage, exHidden.Message);
    }

    [Fact]
    public async Task GetSummary_returns_newest_version_with_provenance()
    {
        await SeedAsync();
        var store = new SummaryStore(Paths);
        await store.AppendAsync("s1", new SummaryVersion("v-a", T0, "v1",
            new AssistantModelRef("qwen.gguf", "aa", "cuda"), 2, "OLD", Stale: true), default);
        await store.AppendAsync("s1", new SummaryVersion("v-b", T0.AddHours(1), "v1",
            new AssistantModelRef("qwen.gguf", "aa", "cpu"), 2, "NEW summary", Stale: false,
            CudaFellToCpu: true), default);
        var r = await Corpus().GetSummaryAsync("s1", default);
        Assert.Equal("NEW summary", r.ContentMarkdown);
        Assert.True(r.CudaFellToCpu);
        Assert.Equal("cpu", r.Backend);
    }

    [Fact]
    public async Task GetSummary_on_summaryless_session_says_so()
    {
        await SeedAsync();
        var ex = await Assert.ThrowsAsync<McpToolException>(() => Corpus().GetSummaryAsync("s1", default));
        Assert.Contains("no summary", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpCorpusReadTests"`
Expected: FAIL — `ReadTranscriptAsync` / `GetSummaryAsync` do not exist.

- [ ] **Step 3: Implement in `McpCorpus.cs`**

```csharp
public const int MaxReadChars = 15_000;

private async Task RequireVisibleAsync(string sessionId, CancellationToken ct)
{
    var (_, visible) = await VisibleAsync(ct);
    if (!visible.ContainsKey(sessionId))
        throw new McpToolException(McpConsentFilter.NotFoundMessage, "denied");
}

public async Task<McpReadResponse> ReadTranscriptAsync(string sessionId, int? fromSeq,
    int? toSeq, int? aroundSeq, int context, string? cursor, CancellationToken ct,
    int maxChars = MaxReadChars)
{
    await RequireVisibleAsync(sessionId, ct);
    var proj = await SessionProjectionLoader.LoadAsync(paths, settings, time, sessionId, ct);
    var rows = proj.Rows;

    // add `using LocalScribe.Core.Projection;` to McpCorpus.cs for DisplayRow
    int RowSeq(DisplayRow r) => r.Segments.Count > 0 ? r.Segments[0].Seq : -1;
    bool RowInSeqRange(DisplayRow r) =>
        !r.IsMarker && r.Segments.Any(s =>
            (fromSeq is null || s.Seq >= fromSeq) && (toSeq is null || s.Seq <= toSeq));

    int start = 0, endExclusive = rows.Count;
    if (cursor is not null)
    {
        int colon = cursor.LastIndexOf(':');
        if (colon < 0 || cursor[..colon] != proj.VersionId
            || !int.TryParse(cursor[(colon + 1)..], out start))
            throw new McpToolException(
                "cursor invalid or transcript version changed; restart the read", "error");
    }
    else if (aroundSeq is int a)
    {
        int center = -1;
        for (int i = 0; i < rows.Count; i++)
            if (!rows[i].IsMarker && rows[i].Segments.Any(s => s.Seq == a)) { center = i; break; }
        if (center < 0) throw new McpToolException($"seq {a} not found in transcript", "error");
        start = Math.Max(0, center - context);
        endExclusive = Math.Min(rows.Count, center + context + 1);
    }
    else if (fromSeq is not null || toSeq is not null)
    {
        int first = -1, last = -1;
        for (int i = 0; i < rows.Count; i++)
            if (RowInSeqRange(rows[i])) { if (first < 0) first = i; last = i; }
        if (first < 0) { start = 0; endExclusive = 0; }
        else { start = first; endExclusive = last + 1; }
    }

    var outRows = new List<McpTranscriptRowDto>();
    int chars = 0;
    int i2 = start;
    for (; i2 < endExclusive; i2++)
    {
        var row = rows[i2];
        if (outRows.Count > 0 && chars + row.Text.Length > maxChars) break;
        chars += row.Text.Length;
        outRows.Add(row.IsMarker
            ? new McpTranscriptRowDto("marker", null, row.StartMs, row.EndMs, null, row.Text)
            : new McpTranscriptRowDto("speech", RowSeq(row), row.StartMs, row.EndMs,
                row.DisplayName, row.Text));
    }
    string? next = i2 < endExclusive ? $"{proj.VersionId}:{i2}" : null;
    return new McpReadResponse(ContractVersion, sessionId, proj.VersionId, outRows, next);
}

public async Task<McpSummaryResponse> GetSummaryAsync(string sessionId, CancellationToken ct)
{
    await RequireVisibleAsync(sessionId, ct);
    var versions = await summaries.LoadAsync(sessionId, ct);
    if (versions.Count == 0)
        throw new McpToolException("no summary for this session", "error");
    var v = versions.OrderBy(x => x.CreatedAt).Last();
    return new McpSummaryResponse(ContractVersion, sessionId, v.ContentMarkdown, v.CreatedAt,
        v.Model.File, v.Model.Backend, v.CudaFellToCpu, v.Stale, v.SourceTranscriptVersion);
}
```

(`maxChars` is a test seam with the production default; the MCP tool layer never passes it.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~McpCorpusReadTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Run the full Core suite, then commit**

Run: `dotnet test tests/LocalScribe.Core.Tests`
Expected: PASS at baseline + all new Mcp tests.

```bash
git add src/LocalScribe.Core/Mcp/McpCorpus.cs tests/LocalScribe.Core.Tests/McpCorpusReadTests.cs
git commit -m "feat(mcp): read_transcript spans with version-pinned cursor + get_summary provenance"
```

---

### Task 8: The LocalScribe.Mcp exe (SDK host, tools, audit wiring)

**Files:**
- Create: `src/LocalScribe.Mcp/LocalScribe.Mcp.csproj`
- Create: `src/LocalScribe.Mcp/Program.cs`
- Create: `src/LocalScribe.Mcp/LocalScribeTools.cs`
- Create: `src/LocalScribe.Mcp/LazyEmbeddingProvider.cs`
- Modify: `LocalScribe.slnx` (add the project under the `/src/` folder)

**Interfaces:**
- Consumes: everything from Tasks 1–7; `AssistantModelManifest.LoadAsync(string modelsRoot, ct)` + `AssistantManifestCache`; `AssistantHelperLocator.FindExe()`; `ModelPaths.ModelsRoot`; `AssistantEmbeddingClient(factory, modelPath, dim, inactivityTimeout)`; `EmbeddingMethod.For(modelPath, dim)`; `ProcessAssistantHelper` (Task 4).
- Produces: `LocalScribe.Mcp.exe --storage-root <path>` speaking MCP over stdio with six tools named exactly `search_transcripts`, `search_transcripts_semantic`, `read_transcript`, `list_sessions`, `list_matters`, `get_summary`; every response is a JSON text block, errors as `{"contract_version":1,"error":"..."}`.

- [ ] **Step 1: Create the project and add packages**

`src/LocalScribe.Mcp/LocalScribe.Mcp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LocalScribe.Mcp</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\LocalScribe.Core\LocalScribe.Core.csproj" />
  </ItemGroup>
</Project>
```

Then pin the SDK packages at the CURRENT versions (do not hand-pick from memory):

Run: `dotnet add src/LocalScribe.Mcp package ModelContextProtocol --prerelease && dotnet add src/LocalScribe.Mcp package Microsoft.Extensions.Hosting`

Add to `LocalScribe.slnx` inside the `/src/` folder, matching the existing entry style:

```xml
<Project Path="src/LocalScribe.Mcp/LocalScribe.Mcp.csproj" />
```

- [ ] **Step 2: Write `LazyEmbeddingProvider.cs`**

```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Transcription;

namespace LocalScribe.Mcp;

/// <summary>Resolves the embed helper on FIRST semantic call, not at startup: manifest load
/// hash-verifies the GGUF (seconds) and the server must come up instantly for lexical/read
/// tools. 90s idle reclaim (vs the App's 5min): MCP queries are bursty, and the shorter
/// window keeps any two-warm-helpers overlap with a running App brief (spec: Concurrency).</summary>
public sealed class LazyEmbeddingProvider : IMcpEmbeddingProvider, IAsyncDisposable
{
    public const int SemanticDim = 256; // mirrors App.SemanticDim — the corpus-wide sidecar dim

    private readonly AssistantManifestCache _manifest =
        new(ct => AssistantModelManifest.LoadAsync(ModelPaths.ModelsRoot, ct));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (IEmbeddingClient Client, string Method)? _resolved;

    public async Task<(IEmbeddingClient Client, string Method)> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_resolved is { } r) return r;
            var manifest = await _manifest.GetAsync(ct);
            if (manifest.EmbeddingModel is not { } model)
                throw new McpToolException(
                    "semantic unavailable: embedding model not installed (run tools/fetch-models.ps1)", "error");
            if (AssistantHelperLocator.FindExe() is not string exe)
                throw new McpToolException(
                    "semantic unavailable: " + AssistantHelperLocator.MissingMessage, "error");
            var client = new AssistantEmbeddingClient(new ProcessAssistantHelper(exe),
                model.FilePath, SemanticDim, TimeSpan.FromSeconds(90));
            _resolved = (client, EmbeddingMethod.For(model.FilePath, SemanticDim));
            return _resolved.Value;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_resolved is { } r) await r.Client.DisposeAsync();
    }
}
```

- [ ] **Step 3: Write `LocalScribeTools.cs`**

```csharp
using System.ComponentModel;
using System.Text.Json;
using LocalScribe.Core.Mcp;
using ModelContextProtocol.Server;

namespace LocalScribe.Mcp;

[McpServerToolType]
public sealed class LocalScribeTools(McpCorpus corpus, McpAuditLog audit, TimeProvider time)
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, McpJsonOptions.Line);

    /// <summary>Uniform wrapper: run the op, audit the outcome (ok/denied/error — denied calls
    /// ARE logged), and always return a JSON envelope. Never throws to the SDK.</summary>
    private async Task<string> RunAsync(string tool, object argsForAudit,
        Func<Task<(string Json, IReadOnlyList<string> SessionIds, int Count)>> op)
    {
        string argsJson = Json(argsForAudit);
        try
        {
            var (json, sessionIds, count) = await op();
            await audit.AppendAsync(new McpAuditEntry(time.GetUtcNow(), tool, argsJson,
                sessionIds, [], count, json.Length, "ok"), CancellationToken.None);
            return json;
        }
        catch (McpToolException ex)
        {
            await audit.AppendAsync(new McpAuditEntry(time.GetUtcNow(), tool, argsJson,
                [], [], 0, 0, ex.Outcome), CancellationToken.None);
            return Json(new { contract_version = McpCorpus.ContractVersion, error = ex.Message });
        }
        catch (Exception ex)
        {
            await audit.AppendAsync(new McpAuditEntry(time.GetUtcNow(), tool, argsJson,
                [], [], 0, 0, "error"), CancellationToken.None);
            return Json(new { contract_version = McpCorpus.ContractVersion, error = ex.Message });
        }
    }

    [McpServerTool(Name = "search_transcripts"), Description(
        "Lexical keyword search over the exposed LocalScribe transcripts. Returns hits with " +
        "session_id + seq anchors and short snippets; quote from read_transcript, not snippets. " +
        "Dates are yyyy-MM-dd (to_date inclusive).")]
    public Task<string> SearchTranscripts(
        [Description("Keyword or phrase to find")] string query,
        [Description("Restrict to one matter id")] string? matter_id = null,
        [Description("Earliest session date, yyyy-MM-dd")] string? from_date = null,
        [Description("Latest session date, yyyy-MM-dd")] string? to_date = null,
        [Description("Restrict to a source app, e.g. webex")] string? app = null,
        [Description("Max hits, 1-50 (default 10)")] int limit = 10,
        CancellationToken ct = default)
        => RunAsync("search_transcripts", new { query, matter_id, from_date, to_date, app, limit },
            async () =>
            {
                var r = await corpus.SearchAsync(query, matter_id, from_date, to_date, app, limit, ct);
                return (Json(r), r.Hits.Select(h => h.SessionId).Distinct().ToList(), r.Hits.Count);
            });

    [McpServerTool(Name = "search_transcripts_semantic"), Description(
        "Related-discussion (semantic) search over the exposed transcripts — finds passages " +
        "about a topic even when the words differ. Check the coverage block: results may be " +
        "partial if sidecars are missing or stale.")]
    public Task<string> SearchTranscriptsSemantic(
        [Description("Topic or question to find related discussion for")] string query,
        [Description("Restrict to one matter id")] string? matter_id = null,
        [Description("Earliest session date, yyyy-MM-dd")] string? from_date = null,
        [Description("Latest session date, yyyy-MM-dd")] string? to_date = null,
        [Description("Restrict to a source app, e.g. webex")] string? app = null,
        [Description("Max hits, 1-50 (default 10)")] int limit = 10,
        CancellationToken ct = default)
        => RunAsync("search_transcripts_semantic", new { query, matter_id, from_date, to_date, app, limit },
            async () =>
            {
                var r = await corpus.SearchSemanticAsync(query, matter_id, from_date, to_date, app, limit, ct);
                return (Json(r), r.Hits.Select(h => h.SessionId).Distinct().ToList(), r.Hits.Count);
            });

    [McpServerTool(Name = "read_transcript"), Description(
        "Read a span of one exposed transcript (corrected text, active version, real speaker " +
        "names, marker rows inline). Select by from_seq/to_seq, or around_seq + context. " +
        "Large spans page via next_cursor — pass it back verbatim to continue.")]
    public Task<string> ReadTranscript(
        [Description("Session id from a search hit or list_sessions")] string session_id,
        [Description("First seq to include")] int? from_seq = null,
        [Description("Last seq to include")] int? to_seq = null,
        [Description("Center the read on this seq anchor")] int? around_seq = null,
        [Description("Rows of context each side of around_seq (default 10)")] int context = 10,
        [Description("Continuation cursor from a previous call")] string? cursor = null,
        CancellationToken ct = default)
        => RunAsync("read_transcript", new { session_id, from_seq, to_seq, around_seq, context, cursor },
            async () =>
            {
                var r = await corpus.ReadTranscriptAsync(session_id, from_seq, to_seq, around_seq,
                    context, cursor, ct);
                return (Json(r), [session_id], r.Rows.Count);
            });

    [McpServerTool(Name = "list_sessions"), Description(
        "List exposed sessions (id, title, date, matters, source app, approximate duration, " +
        "whether a summary exists), newest first. Dates are yyyy-MM-dd.")]
    public Task<string> ListSessions(
        [Description("Restrict to one matter id")] string? matter_id = null,
        [Description("Earliest session date, yyyy-MM-dd")] string? from_date = null,
        [Description("Latest session date, yyyy-MM-dd")] string? to_date = null,
        [Description("Restrict to a source app, e.g. webex")] string? app = null,
        [Description("Skip this many sessions (paging)")] int offset = 0,
        [Description("Max sessions, 1-100 (default 20)")] int limit = 20,
        CancellationToken ct = default)
        => RunAsync("list_sessions", new { matter_id, from_date, to_date, app, offset, limit },
            async () =>
            {
                var r = await corpus.ListSessionsAsync(matter_id, from_date, to_date, app, offset, limit, ct);
                return (Json(r), r.Sessions.Select(s => s.SessionId).ToList(), r.Sessions.Count);
            });

    [McpServerTool(Name = "list_matters"), Description(
        "List the matters the user has exposed to MCP (id, name, reference, session count).")]
    public Task<string> ListMatters(CancellationToken ct = default)
        => RunAsync("list_matters", new { }, async () =>
        {
            var r = await corpus.ListMattersAsync(ct);
            return (Json(r), [], r.Matters.Count);
        });

    [McpServerTool(Name = "get_summary"), Description(
        "Get the newest assistant-generated summary of an exposed session, with provenance " +
        "(model file, backend, stale flag).")]
    public Task<string> GetSummary(
        [Description("Session id")] string session_id,
        CancellationToken ct = default)
        => RunAsync("get_summary", new { session_id }, async () =>
        {
            var r = await corpus.GetSummaryAsync(session_id, ct);
            return (Json(r), [session_id], 1);
        });
}
```

- [ ] **Step 4: Write `Program.cs`**

```csharp
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;
using LocalScribe.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// stdio MCP: stdout belongs to the protocol. ALL logging goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

string? rootArg = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--storage-root") rootArg = args[i + 1];

// Load the user's real settings (projection behavior must match the App); override
// only the storage root when --storage-root is passed.
string settingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "LocalScribe", "settings.json");
var settings = await new SettingsStore(settingsPath).LoadOrDefaultAsync(default);
if (rootArg is not null) settings = settings with { StorageRoot = rootArg };

var paths = new StoragePaths(settings.StorageRoot);
var time = TimeProvider.System;
var embeddings = new LazyEmbeddingProvider();
var corpus = new McpCorpus(paths, settings, time,
    new McpConsentStore(paths),
    new McpLexicalCatalog(paths, settings, time),
    new SemanticIndexStore(paths),
    new MatterStore(paths.MattersDir),
    new LocalScribe.Core.Assistant.SummaryStore(paths),
    embeddings);

builder.Services.AddSingleton(corpus);
builder.Services.AddSingleton(new McpAuditLog(paths, time));
builder.Services.AddSingleton<TimeProvider>(time);
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "localscribe", Version =
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
    })
    .WithStdioServerTransport()
    .WithTools<LocalScribeTools>();

var host = builder.Build();
await using (embeddings)
    await host.RunAsync();
```

Note: the SDK's option/registration names drift between prereleases (`ServerInfo` vs `Implementation`, `WithTools<T>()` vs `WithToolsFromAssembly()`). If the pinned version differs, adapt THIS file to the SDK — do not change the tool class or Core.

- [ ] **Step 5: Build and hand-check startup**

Run: `dotnet build src/LocalScribe.Mcp`
Expected: build clean.

Run (PowerShell — proves it starts, serves nothing to stdout unprompted, and exits on stdin EOF):
`'' | dotnet run --project src/LocalScribe.Mcp -- --storage-root $env:TEMP\ls-mcp-smoke 2>$null`
Expected: exits without output and without hanging.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Mcp LocalScribe.slnx
git commit -m "feat(mcp): LocalScribe.Mcp stdio server exe - six read-only tools on the official C# SDK"
```

---

### Task 9: Wire-level test project (real exe over stdio)

**Files:**
- Create: `tests/LocalScribe.Mcp.Tests/LocalScribe.Mcp.Tests.csproj`
- Create: `tests/LocalScribe.Mcp.Tests/McpWireTests.cs`
- Modify: `LocalScribe.slnx` (add under `/tests/`)

**Interfaces:**
- Consumes: the built `LocalScribe.Mcp.dll` (via ProjectReference output), `TestSessionSeeder` (linked source file), SDK client API (`McpClient`/`McpClientFactory` + `StdioClientTransport` — adapt names to the pinned SDK version).
- Produces: proof that initialize → list-tools → call-tool works over real stdio (stdout purity is implicit: any stray stdout byte breaks the protocol handshake).

- [ ] **Step 1: Create the test project**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\LocalScribe.Mcp\LocalScribe.Mcp.csproj" />
    <ProjectReference Include="..\..\src\LocalScribe.Core\LocalScribe.Core.csproj" />
    <Compile Include="..\LocalScribe.Core.Tests\TestSessionSeeder.cs" Link="TestSessionSeeder.cs" />
    <Compile Include="..\LocalScribe.Core.Tests\ManualUtcTimeProvider.cs" Link="ManualUtcTimeProvider.cs" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```

Run `dotnet add tests/LocalScribe.Mcp.Tests package ModelContextProtocol --prerelease` (client API), and add the slnx entry:

```xml
<Project Path="tests/LocalScribe.Mcp.Tests/LocalScribe.Mcp.Tests.csproj" />
```

(If `TestSessionSeeder` depends on other helpers in Core.Tests, link those files the same way rather than duplicating them.)

- [ ] **Step 2: Write the wire tests**

```csharp
using System.Text.Json;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Storage;
using ModelContextProtocol.Client;

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

        string dll = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Mcp.dll");
        Assert.True(File.Exists(dll), $"server dll not found at {dll}");
        _client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet",
            Arguments = [dll, "--storage-root", _root],
            Name = "localscribe-wire-test",
        }));
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client!.CallToolAsync(tool, args);
        // Extract the single text content block; adapt member names to the pinned SDK version.
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
            tools.Select(t => t.Name).OrderBy(n => n).ToArray());
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
}
```

- [ ] **Step 3: Run the wire tests**

Run: `dotnet test tests/LocalScribe.Mcp.Tests`
Expected: PASS (3 tests). If content-block type names fail to compile, fix against the pinned SDK's `Protocol` namespace (this is the one place version drift is expected — keep the assertions identical).

- [ ] **Step 4: Commit**

```bash
git add tests/LocalScribe.Mcp.Tests LocalScribe.slnx
git commit -m "test(mcp): wire-level tests - real exe over stdio, revocation + audit + no-text-in-audit"
```

---

### Task 10: App Settings "MCP Access" section

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs`
- Modify: `src/LocalScribe.App/SettingsPage.xaml`
- Modify: `src/LocalScribe.App/App.xaml.cs` (VM construction — pass the new ctor args)
- Test: `tests/LocalScribe.App.Tests/SettingsMcpAccessTests.cs`

**Interfaces:**
- Consumes: `McpConsentStore`, `MatterStore.ListAsync`, existing `Commit`/`ISettingsService` pattern, existing `Func<string,bool> confirm` seam style (as used by the voiceprint purge).
- Produces: VM members `McpEnabled : bool`, `McpAllowUnassigned : bool`, `ObservableCollection<McpMatterToggle> McpMatters`, `McpConfigSnippet : string`, `CopyMcpSnippetCommand`, `OpenMcpAuditFolderCommand`; `sealed partial class McpMatterToggle { string Id; string Label; bool IsAllowed; }`.

- [ ] **Step 1: Write the failing App tests**

Follow the existing SettingsPageViewModel test style in `tests/LocalScribe.App.Tests` (find it with `grep -rln "SettingsPageViewModel" tests/LocalScribe.App.Tests`) for how the VM is constructed with fakes; add:

```csharp
// tests/LocalScribe.App.Tests/SettingsMcpAccessTests.cs
// Construct the VM against a temp storage root exactly like the existing
// SettingsPageViewModel tests do, passing:
//   confirmMcpEnable: a Func<string,bool> fake you control
// Cases:
[Fact] // 1
public async Task Enabling_mcp_requires_confirm_and_writes_consent_json()
{
    // confirm => true; set vm.McpEnabled = true; flush async commits;
    // assert File.Exists(paths.McpConsentJson) and enabled == true in the JSON.
}

[Fact] // 2
public async Task Declining_the_confirm_leaves_mcp_disabled_and_writes_nothing()
{
    // confirm => false; set vm.McpEnabled = true;
    // assert vm.McpEnabled reverted to false and no consent.json exists.
}

[Fact] // 3
public async Task Ticking_a_matter_updates_allowed_matter_ids()
{
    // seed two matters via MatterStore; enable (confirm true);
    // tick McpMatters[0].IsAllowed = true; assert allowed_matter_ids == [that id].
}

[Fact] // 4
public async Task Snippet_contains_exe_path_and_storage_root()
{
    // assert vm.McpConfigSnippet contains "LocalScribe.Mcp.exe" and the storage root,
    // and parses as JSON with mcpServers.localscribe.args == ["--storage-root", root].
}

[Fact] // 5
public async Task Disabling_writes_enabled_false_but_keeps_the_allowlist()
{
    // enable + tick a matter; then vm.McpEnabled = false (no confirm needed to disable);
    // assert enabled == false AND allowed_matter_ids still contains the matter
    // (re-enabling must not silently re-expose MORE than before — list is preserved,
    //  but exposure is off).
}
```

Write these five as real tests (the comments above are the required behaviors, not placeholders to defer — each body follows the construction pattern of the neighboring settings tests).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsMcpAccessTests"`
Expected: FAIL — new VM members do not exist.

- [ ] **Step 3: Implement the VM additions**

In `SettingsPageViewModel`, following the existing section pattern:

- New ctor params: `Func<string, bool> confirmMcpEnable` and reuse of the already-available settings service (storage root comes from `_settings.Current.StorageRoot`; build `new McpConsentStore(new StoragePaths(root))` and `new MatterStore(new StoragePaths(root).MattersDir)` on demand so a storage-root change is picked up).
- Load current consent + matters in the VM's existing async init path; populate `McpMatters` with `Label` built like `MattersPageViewModel` labels (`Name` + optional `Reference`), `IsAllowed` from the consent doc.
- `McpEnabled` setter: turning ON runs `confirmMcpEnable(McpEnableWarning)` first — on false, revert the field without saving. Turning OFF saves immediately, preserving `AllowedMatterIds`. All saves go through one private `SaveConsentAsync()` that snapshots VM state into a `McpConsentDocument { UpdatedUtc = DateTimeOffset.UtcNow }` and calls `McpConsentStore.SaveAsync`.
- `McpMatterToggle.IsAllowed` setter and `McpAllowUnassigned` setter both trigger `SaveConsentAsync()`.
- Warning text constant:

```csharp
public const string McpEnableWarning =
    "Expose selected matters to MCP clients?\n\n" +
    "Apps you register (for example Claude Desktop) will be able to search and read the " +
    "transcripts of the matters you tick below. Everything stays on this computer and is " +
    "read-only, but what those apps do with text they read is outside LocalScribe's control. " +
    "Every read is recorded in the audit log.";
```

- Snippet (computed property):

```csharp
public string McpConfigSnippet
{
    get
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Mcp.exe");
        string root = new StoragePaths(_settings.Current.StorageRoot).Root;
        var doc = new { mcpServers = new { localscribe = new {
            command = exe, args = new[] { "--storage-root", root } } } };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }
}
```

- `CopyMcpSnippetCommand` → injected `Action<string>` clipboard seam (default `Clipboard.SetText` wired in App.xaml.cs); `OpenMcpAuditFolderCommand` → `Process.Start("explorer.exe", auditDir)` after `Directory.CreateDirectory(auditDir)`.
- Wire the new ctor args in `App.xaml.cs` using the same `MessageBox.Show(..., MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes` pattern as the voiceprint purge confirm (native prompt — deliberately not a FluentWindow; the spec's one deliberate consent moment).

- [ ] **Step 4: Add the XAML section**

In `SettingsPage.xaml`, after the Assistant card, matching the existing card style:

```xml
<ui:Card Style="{StaticResource SectionCard}">
    <StackPanel>
        <TextBlock Text="MCP Access" FontWeight="SemiBold" Margin="0,0,0,8" />
        <TextBlock Style="{StaticResource Note}"
                   Text="Let MCP clients (for example Claude Desktop) search and read selected matters. Read-only, fully local, every read audited. Nothing is exposed until you enable this and tick matters." />
        <CheckBox Content="Enable MCP access" IsChecked="{Binding McpEnabled}" Margin="0,8,0,4" />
        <CheckBox Content="Include sessions not assigned to any matter"
                  IsChecked="{Binding McpAllowUnassigned}" Margin="0,0,0,8"
                  IsEnabled="{Binding McpEnabled}" />
        <TextBlock Text="Exposed matters" FontWeight="SemiBold" Margin="0,4,0,4" />
        <ItemsControl ItemsSource="{Binding McpMatters}" IsEnabled="{Binding McpEnabled}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <CheckBox Content="{Binding Label}" IsChecked="{Binding IsAllowed}" Margin="0,2,0,2" />
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
        <TextBlock Text="Client registration" FontWeight="SemiBold" Margin="0,12,0,4" />
        <TextBox Text="{Binding McpConfigSnippet, Mode=OneWay}" IsReadOnly="True"
                 FontFamily="Consolas" TextWrapping="Wrap" AcceptsReturn="True" MaxHeight="140" />
        <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
            <ui:Button Content="Copy config" Command="{Binding CopyMcpSnippetCommand}" Margin="0,0,8,0" />
            <ui:Button Content="Open audit log folder" Command="{Binding OpenMcpAuditFolderCommand}" />
        </StackPanel>
        <TextBlock Style="{StaticResource Note}" Margin="0,8,0,0"
                   Text="Claude Desktop: paste into claude_desktop_config.json. Claude Code: claude mcp add localscribe -- &lt;exe&gt; --storage-root &lt;root&gt;." />
    </StackPanel>
</ui:Card>
```

(Adapt `ui:Button` vs `Button` to whatever the surrounding cards use.)

- [ ] **Step 5: Run tests + full App suite**

Run: `dotnet test tests/LocalScribe.App.Tests`
Expected: PASS at baseline + 5 new tests.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App tests/LocalScribe.App.Tests/SettingsMcpAccessTests.cs
git commit -m "feat(mcp): Settings MCP Access section - consent allowlist, confirm-gated enable, config snippet"
```

---

### Task 11: Publish guard + smoke runbook

**Files:**
- Create: `tools/verify-mcp-publish.ps1`
- Create: `docs/plans/2026-07-26-mcp-smoke-runbook.md`

**Interfaces:**
- Consumes: publish layout precedent (`tools/verify-assistant-publish.ps1` pattern).
- Produces: guard script asserting `LocalScribe.Mcp.exe` beside the App; the user-facing smoke runbook.

- [ ] **Step 1: Write the guard script** (no Unicode emojis)

```powershell
# tools/verify-mcp-publish.ps1 - asserts the MCP server exe landed beside the App.
param([Parameter(Mandatory = $true)][string] $PublishDir)

$missing = @()
foreach ($rel in @('LocalScribe.Mcp.exe')) {
    $p = Join-Path $PublishDir $rel
    if (-not (Test-Path $p) -or (Get-Item $p).Length -eq 0) { $missing += $rel }
}
if ($missing.Count -gt 0) {
    Write-Error ("MCP publish layout incomplete. Missing/empty: " + ($missing -join ', '))
    exit 1
}
Write-Host "MCP publish layout OK."
```

- [ ] **Step 2: Write the smoke runbook**

`docs/plans/2026-07-26-mcp-smoke-runbook.md` — publish steps (`dotnet publish src/LocalScribe.Mcp -c Release -o <app publish dir>` then `tools/verify-mcp-publish.ps1 -PublishDir <dir>`), then the manual checks from the spec's Testing section, each as a checkbox:

1. Register in Claude Desktop via the Settings snippet; server appears and lists 6 tools.
2. End-to-end: "find where we discussed <topic> and quote it" — hits cite session + seq; read_transcript quote matches the UI's read view (corrected text, real speaker names).
3. Consent: with MCP disabled, every tool returns the uniform denial; enable + tick one matter; other matters invisible in list_matters/search; untick mid-conversation revokes on the next call.
4. Audit: `mcp/audit/audit-YYYYMM.jsonl` gained one line per call including the denied ones; no transcript text in the file.
5. Semantic during recording: start a recording in the App, run a semantic MCP query — it answers (one-shot helper respawn) and the App keeps recording cleanly.
6. App closed entirely: all six tools still work against the storage root.
7. Missing embedding model (temporarily rename `models/assistant-manifest.json`): semantic tool errors clearly, lexical unaffected.

- [ ] **Step 3: Commit**

```bash
git add tools/verify-mcp-publish.ps1 docs/plans/2026-07-26-mcp-smoke-runbook.md
git commit -m "docs(mcp): publish guard script + manual smoke runbook"
```

---

## Final gate (after all tasks)

Run all three suites and the build:

```
dotnet build LocalScribe.slnx
dotnet test tests/LocalScribe.Core.Tests
dotnet test tests/LocalScribe.App.Tests
dotnet test tests/LocalScribe.Mcp.Tests
```

Expected: build clean; Core/App at their pre-round baselines plus the new tests; Mcp.Tests green. Then use superpowers:finishing-a-development-branch.
