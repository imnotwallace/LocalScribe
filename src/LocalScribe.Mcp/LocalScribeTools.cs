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
    /// ARE logged), and always return a JSON envelope. Never throws to the SDK. Also surfaces
    /// McpLexicalCatalog.SkippedSessions to stderr ONLY (never in the returned JSON — see
    /// McpSearchResponse's doc comment for why a skipped-session count must stay server-side).</summary>
    private async Task<string> RunAsync(string tool, object argsForAudit,
        Func<Task<(string Json, IReadOnlyList<string> SessionIds, int Count)>> op)
    {
        string argsJson = Json(argsForAudit);
        try
        {
            var (json, sessionIds, count) = await op();
            await audit.AppendAsync(new McpAuditEntry(time.GetUtcNow(), tool, argsJson,
                sessionIds, [], count, json.Length, "ok"), CancellationToken.None);
            WarnIfSkippedSessions();
            return json;
        }
        catch (McpToolException ex)
        {
            await audit.AppendAsync(new McpAuditEntry(time.GetUtcNow(), tool, argsJson,
                [], [], 0, 0, ex.Outcome), CancellationToken.None);
            WarnIfSkippedSessions();
            return Json(new { contract_version = McpCorpus.ContractVersion, error = ex.Message });
        }
        catch (Exception ex)
        {
            await audit.AppendAsync(new McpAuditEntry(time.GetUtcNow(), tool, argsJson,
                [], [], 0, 0, "error"), CancellationToken.None);
            WarnIfSkippedSessions();
            return Json(new { contract_version = McpCorpus.ContractVersion, error = ex.Message });
        }
    }

    private void WarnIfSkippedSessions()
    {
        int skipped = corpus.CatalogSkippedSessions;
        if (skipped > 0)
            Console.Error.WriteLine(
                $"warn: lexical catalog skipped {skipped} session(s) that failed to parse on last refresh");
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
        "around_part_index disambiguates which part of a manually-split segment to center on " +
        "(a search hit anchors on (seq, part_index) for a split segment) — omit it to center " +
        "on the seq's first part. Large spans page via next_cursor — pass it back verbatim to " +
        "continue.")]
    public Task<string> ReadTranscript(
        [Description("Session id from a search hit or list_sessions")] string session_id,
        [Description("First seq to include")] int? from_seq = null,
        [Description("Last seq to include")] int? to_seq = null,
        [Description("Center the read on this seq anchor")] int? around_seq = null,
        [Description("Rows of context each side of around_seq (default 10)")] int context = 10,
        [Description("Continuation cursor from a previous call")] string? cursor = null,
        [Description("Disambiguates which part of a manually-split segment around_seq should " +
            "center on; omitting it centers on the seq's first part")] int? around_part_index = null,
        CancellationToken ct = default)
        => RunAsync("read_transcript",
            new { session_id, from_seq, to_seq, around_seq, context, cursor, around_part_index },
            async () =>
            {
                var r = await corpus.ReadTranscriptAsync(session_id, from_seq, to_seq, around_seq,
                    context, cursor, ct, aroundPartIndex: around_part_index);
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
