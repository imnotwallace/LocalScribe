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

    /// <summary>Server-side diagnostics only — a count of sessions the catalog failed to parse on
    /// its last refresh. Deliberately NOT part of any client-facing response (see McpSearchResponse's
    /// doc comment): unparseable sessions have unknowable matter tags, so the count can't be scoped
    /// to the consent-visible set. Callers should log this to stderr, never return it to a tool.</summary>
    public int CatalogSkippedSessions => catalog.SkippedSessions;

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

    // Concurrency-safe cache (McpCorpus is a singleton serving overlapping calls), keyed on
    // session id + meta.json's last-write ticks so a session that starts building successfully
    // again (or whose meta.json changes) is never served a stale attribution. Skipped sessions
    // are normally zero, so this is nearly always a no-op - same spirit as _sidecarCache below.
    private readonly ConcurrentDictionary<string, (long Ticks, bool Visible)> _unreadableCache = [];

    /// <summary>Attributes each session McpLexicalCatalog failed to build against the CURRENT
    /// consent document, by reading its meta.json STANDALONE (independent of the rest of the
    /// failed build) - see McpDtos.cs's doc comment on McpSearchResponse for the full reasoning.
    /// persistMigration:false is mandatory here: this is a read-only server, and this path must
    /// never write-migrate a legacy meta.json it only read for attribution.</summary>
    private async Task<int> UnreadableSessionsAsync(McpConsentDocument consent, CancellationToken ct)
    {
        int count = 0;
        foreach (string id in catalog.SkippedSessionIds)
        {
            ct.ThrowIfCancellationRequested();
            string metaPath = paths.MetaJson(id);
            long ticks = File.Exists(metaPath) ? File.GetLastWriteTimeUtc(metaPath).Ticks : 0;
            if (ticks == 0) continue; // meta.json itself missing - unattributable, excluded

            if (_unreadableCache.TryGetValue(id, out var cached) && cached.Ticks == ticks)
            { if (cached.Visible) count++; continue; }

            SessionMeta? meta;
            try { meta = await new MetadataStore(metaPath).LoadAsync(persistMigration: false, ct); }
            catch (OperationCanceledException) { throw; }
            catch { meta = null; } // meta.json unreadable - unattributable, excluded

            bool visible = meta is not null && McpConsentFilter.SessionVisible(
                new SearchSessionEntry { SessionId = id, MatterIds = meta.MatterIds }, consent);
            _unreadableCache[id] = (ticks, visible);
            if (visible) count++;
        }
        return count;
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
        var (doc, visible) = await VisibleAsync(ct);
        var q = BuildQuery(query, matterId, fromDate, toDate, app);
        var results = SearchQueryEngine.Run(visible.Values, q);
        var labels = await MatterIndexAsync(ct);
        var flat = results.SelectMany(r => r.Hits.Select(h => (r.Session, Hit: h))).ToList();
        var hits = flat.Take(limit).Select(x => new McpSearchHitDto(
            x.Session.SessionId, x.Session.Title, DateLocal(x.Session), x.Session.App,
            x.Session.MatterIds.Select(id => MatterLabel(id, labels)).ToList(),
            x.Hit.Speaker, x.Hit.Seq, x.Hit.PartIndex, x.Hit.StartMs,
            x.Hit.Snippet, x.Hit.MatchesOriginalOnly, x.Hit.IsSpeakerNameMatch)).ToList();
        int unreadable = await UnreadableSessionsAsync(doc, ct);
        return new McpSearchResponse(ContractVersion, catalog.LastRefreshUtc, flat.Count, unreadable, hits);
    }

    public async Task<McpSessionListResponse> ListSessionsAsync(string? matterId,
        string? fromDate, string? toDate, string? app, int offset, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
        offset = Math.Max(0, offset);
        var (doc, visible) = await VisibleAsync(ct);
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
        int unreadable = await UnreadableSessionsAsync(doc, ct);
        return new McpSessionListResponse(ContractVersion, catalog.LastRefreshUtc,
            filtered.Count, unreadable, page);
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
        var (doc, visible) = await VisibleAsync(ct);
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
        int unreadable = await UnreadableSessionsAsync(doc, ct);
        return new McpSemanticResponse(ContractVersion, catalog.LastRefreshUtc,
            new McpCoverage(visible.Count, covered, stale, unreadable), hits);
    }

    public async Task<McpMattersResponse> ListMattersAsync(CancellationToken ct)
    {
        var (doc, visible) = await VisibleAsync(ct);
        var index = await matters.ListAsync(ct);
        var allowed = index.Matters
            .Where(m => doc.AllowedMatterIds.Contains(m.Id, StringComparer.Ordinal))
            .Select(m => new McpMatterDto(m.Id, m.Name, m.Reference,
                // Count from the VISIBLE set, not the corpus-wide index entry: the latter would
                // leak the existence of a session hidden by the multi-matter consent rule (a
                // session tagged with both an allowed and a non-allowed matter is hidden
                // entirely - see McpConsentFilter - so it must not be counted here either).
                visible.Values.Count(e => e.MatterIds.Contains(m.Id, StringComparer.Ordinal))))
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
    /// a many-segment block instead of the individual line a caller asked for). PartIndex
    /// distinguishes a manually-split segment's parts (design §2.2): one machine Seq can become
    /// several RowSegments that share the Seq and differ only by PartIndex, so it must travel
    /// alongside Seq everywhere a unit is addressed - null for markers, same as Seq's -1 sentinel
    /// means "no seq".</summary>
    private readonly record struct ReadUnit(bool IsMarker, long StartMs, long EndMs,
        string? Speaker, string Text, int Seq, int? PartIndex);

    private static IReadOnlyList<ReadUnit> FlattenToUnits(IReadOnlyList<DisplayRow> rows)
    {
        var units = new List<ReadUnit>();
        foreach (var row in rows)
        {
            if (row.IsMarker) { units.Add(new ReadUnit(true, row.StartMs, row.EndMs, null, row.Text, -1, null)); continue; }
            foreach (var seg in row.Segments)
                units.Add(new ReadUnit(false, seg.StartMs, seg.EndMs, row.DisplayName, seg.ProjectedText,
                    seg.Seq, seg.PartIndex));
        }
        return units;
    }

    /// <summary>Quoting surface for the corpus: rows carry the corrected/displayed text (already
    /// vocabulary + edits applied by TranscriptProjection), the active version's real speaker
    /// display names, and marker rows inline exactly as the read view shows them. Pages by a
    /// char budget so a long call doesn't dump the whole transcript in one response; the cursor
    /// is pinned to the version it was minted against so an intervening edit/re-transcription
    /// can never silently splice rows from two different versions into one paged read.
    /// aroundPartIndex disambiguates a manually-split seq (design §2.2): when given, centers on
    /// the unit whose (Seq, PartIndex) both match (a mismatch after a matching seq reports the
    /// missing part specifically, never the generic "seq not found"); when omitted, centers on
    /// the first unit with that seq exactly as before this parameter existed - existing callers
    /// are unaffected.
    /// persistMigration:false - this is a read-only server; it must never write-migrate a legacy
    /// session it only read (see SessionProjectionLoader.LoadAsync doc).</summary>
    public async Task<McpReadResponse> ReadTranscriptAsync(string sessionId, int? fromSeq,
        int? toSeq, int? aroundSeq, int context, string? cursor, CancellationToken ct,
        int? aroundPartIndex = null, int maxChars = MaxReadChars)
    {
        await RequireVisibleAsync(sessionId, ct);
        var proj = await SessionProjectionLoader.LoadAsync(paths, settings, time, sessionId,
            persistMigration: false, ct: ct);
        var units = FlattenToUnits(proj.Rows);

        bool UnitInSeqRange(ReadUnit u) =>
            !u.IsMarker && (fromSeq is null || u.Seq >= fromSeq) && (toSeq is null || u.Seq <= toSeq);

        // Compute the requested span from around_seq / from_seq / to_seq FIRST, independent of a
        // supplied cursor. A cursor only ever moves the start of an already-bounded span forward;
        // it must never widen endExclusive back out to the end of the transcript (that was the
        // paging bug: a second page of a bounded read used to run to the end of the session).
        int start = 0, endExclusive = units.Count;
        if (aroundSeq is int a)
        {
            int center = -1;
            if (aroundPartIndex is int p)
            {
                for (int i = 0; i < units.Count; i++)
                    if (!units[i].IsMarker && units[i].Seq == a && units[i].PartIndex == p) { center = i; break; }
                if (center < 0)
                {
                    bool seqExists = units.Any(u => !u.IsMarker && u.Seq == a);
                    throw new McpToolException(seqExists
                        ? $"seq {a} part {p} not found in transcript (seq {a} exists but has no part {p})"
                        : $"seq {a} not found in transcript", "error");
                }
            }
            else
            {
                // Existing behavior, unchanged: no part requested - center on the first unit with
                // this seq (a split seq's part 0, since parts are emitted in ascending order).
                for (int i = 0; i < units.Count; i++)
                    if (!units[i].IsMarker && units[i].Seq == a) { center = i; break; }
                if (center < 0) throw new McpToolException($"seq {a} not found in transcript", "error");
            }
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

        // A supplied cursor overrides ONLY start, resuming inside whatever span was just computed
        // above (or, when no range was requested, paging to the end exactly as before this fix).
        if (cursor is not null)
        {
            int colon = cursor.LastIndexOf(':');
            if (colon < 0 || cursor[..colon] != proj.VersionId
                || !int.TryParse(cursor[(colon + 1)..], out start))
                throw new McpToolException(
                    "cursor invalid or transcript version changed; restart the read", "error");
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
                ? new McpTranscriptRowDto("marker", null, null, u.StartMs, u.EndMs, null, u.Text)
                : new McpTranscriptRowDto("speech", u.Seq, u.PartIndex, u.StartMs, u.EndMs, u.Speaker, u.Text));
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
