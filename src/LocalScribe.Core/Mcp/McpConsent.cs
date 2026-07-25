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

/// <summary>Shared JSON option sets for the MCP surface: snake_case throughout. Line is single-line
/// (no indentation) - used for the append-only audit log (design: one JSON object per line), first
/// consumed by a later task.</summary>
public static class McpJsonOptions
{
    public static readonly JsonSerializerOptions Line = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };
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
    /// Any read/parse failure yields disabled - fail closed, never fail open.</summary>
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
