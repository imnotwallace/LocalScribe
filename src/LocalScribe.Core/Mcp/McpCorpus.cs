using System.Collections.Concurrent;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
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
    public const int MaxReadChars = 15_000;

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
        return new McpSearchResponse(ContractVersion, catalog.LastRefreshUtc, flat.Count, hits);
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
            .OrderByDescending(e => e.StartedAtUtc)
            .ThenBy(e => e.SessionId, StringComparer.Ordinal).ToList();
        var page = filtered.Skip(offset).Take(limit).Select(e => new McpSessionDto(
            e.SessionId, e.Title, DateLocal(e), e.App,
            e.MatterIds.Select(id => MatterLabel(id, labels)).ToList(),
            e.Lines.Count > 0 ? e.Lines[^1].StartMs : null,
            File.Exists(paths.SummariesJson(e.SessionId)))).ToList();
        return new McpSessionListResponse(ContractVersion, catalog.LastRefreshUtc,
            filtered.Count, page);
    }

    // Concurrency-safe cache: the dictionary itself is thread-safe via ConcurrentDictionary.
    // Duplicate concurrent loads for the same session are benign (loads are pure reads; stored
    // values are equivalent). The mtime check is what keeps a changed sidecar from being served stale.
    private readonly ConcurrentDictionary<string, (long Ticks, SemanticSidecar? Sidecar)> _sidecarCache = [];

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
        try
        {
            // Deliberate CancellationToken.None: a cancelled client request must not kill the
            // warm helper process mid-batch (same discipline as SemanticIndexService.QueryAsync).
            batch = await client.EmbedAsync("query", [query], CancellationToken.None);
        }
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

    /// <summary>Same denied-vs-missing contract as VisibleAsync's callers: a session that is not
    /// in the consent-visible set throws the identical NotFoundMessage a truly missing session
    /// would - existence must never leak via a different error.</summary>
    private async Task RequireVisibleAsync(string sessionId, CancellationToken ct)
    {
        var (_, visible) = await VisibleAsync(ct);
        if (!visible.ContainsKey(sessionId))
            throw new McpToolException(McpConsentFilter.NotFoundMessage, "denied");
    }

    /// <summary>One addressable read unit: either a marker row verbatim, or a single transcript
    /// segment (never a whole grouped same-speaker DisplayRow - SectionGrouper merges contiguous
    /// same-speaker turns per Settings.SectionGapMs, which would make seq ranges/cursors address
    /// a many-segment block instead of the individual line a caller asked for).</summary>
    private readonly record struct ReadUnit(bool IsMarker, long StartMs, long EndMs,
        string? Speaker, string Text, int Seq);

    private static IReadOnlyList<ReadUnit> FlattenToUnits(IReadOnlyList<DisplayRow> rows)
    {
        var units = new List<ReadUnit>();
        foreach (var row in rows)
        {
            if (row.IsMarker) { units.Add(new ReadUnit(true, row.StartMs, row.EndMs, null, row.Text, -1)); continue; }
            foreach (var seg in row.Segments)
                units.Add(new ReadUnit(false, seg.StartMs, seg.EndMs, row.DisplayName, seg.ProjectedText, seg.Seq));
        }
        return units;
    }

    /// <summary>Quoting surface for the corpus: rows carry the corrected/displayed text (already
    /// vocabulary + edits applied by TranscriptProjection), the active version's real speaker
    /// display names, and marker rows inline exactly as the read view shows them. Pages by a
    /// char budget so a long call doesn't dump the whole transcript in one response; the cursor
    /// is pinned to the version it was minted against so an intervening edit/re-transcription
    /// can never silently splice rows from two different versions into one paged read.
    /// persistMigration:false - this is a read-only server; it must never write-migrate a legacy
    /// session it only read (see SessionProjectionLoader.LoadAsync doc).</summary>
    public async Task<McpReadResponse> ReadTranscriptAsync(string sessionId, int? fromSeq,
        int? toSeq, int? aroundSeq, int context, string? cursor, CancellationToken ct,
        int maxChars = MaxReadChars)
    {
        await RequireVisibleAsync(sessionId, ct);
        var proj = await SessionProjectionLoader.LoadAsync(paths, settings, time, sessionId,
            persistMigration: false, ct: ct);
        var units = FlattenToUnits(proj.Rows);

        bool UnitInSeqRange(ReadUnit u) =>
            !u.IsMarker && (fromSeq is null || u.Seq >= fromSeq) && (toSeq is null || u.Seq <= toSeq);

        int start = 0, endExclusive = units.Count;
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
            for (int i = 0; i < units.Count; i++)
                if (!units[i].IsMarker && units[i].Seq == a) { center = i; break; }
            if (center < 0) throw new McpToolException($"seq {a} not found in transcript", "error");
            start = Math.Max(0, center - context);
            endExclusive = Math.Min(units.Count, center + context + 1);
        }
        else if (fromSeq is not null || toSeq is not null)
        {
            int first = -1, last = -1;
            for (int i = 0; i < units.Count; i++)
                if (UnitInSeqRange(units[i])) { if (first < 0) first = i; last = i; }
            if (first < 0) { start = 0; endExclusive = 0; }
            else { start = first; endExclusive = last + 1; }
        }

        var outRows = new List<McpTranscriptRowDto>();
        int chars = 0;
        int i2 = start;
        for (; i2 < endExclusive; i2++)
        {
            var u = units[i2];
            if (outRows.Count > 0 && chars + u.Text.Length > maxChars) break;
            chars += u.Text.Length;
            outRows.Add(u.IsMarker
                ? new McpTranscriptRowDto("marker", null, u.StartMs, u.EndMs, null, u.Text)
                : new McpTranscriptRowDto("speech", u.Seq, u.StartMs, u.EndMs, u.Speaker, u.Text));
        }
        string? next = i2 < endExclusive ? $"{proj.VersionId}:{i2}" : null;
        return new McpReadResponse(ContractVersion, sessionId, proj.VersionId, outRows, next);
    }

    /// <summary>Newest generated summary (assistant\summaries.json is append-only, newest-last by
    /// CreatedAt) with its model/backend provenance - callers need to know whether the content
    /// they are quoting came from a stale version or a CUDA-fell-to-CPU run.</summary>
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
}
