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
