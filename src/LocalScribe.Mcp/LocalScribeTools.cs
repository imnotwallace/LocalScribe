using System.ComponentModel;
using System.Text.Json;
using LocalScribe.Core.Mcp;
using ModelContextProtocol.Server;

namespace LocalScribe.Mcp;

[McpServerToolType]
public sealed class LocalScribeTools(McpCorpus corpus, McpAuditLog audit, TimeProvider time)
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, McpJsonOptions.Line);

    /// <summary>Uniform wrapper: run the op, audit the outcome (ok/denied/error/cancelled — every
    /// outcome is logged), and ALWAYS return a JSON envelope. Never throws to the SDK — not even
    /// if the audit write itself fails (disk full, permissions, roaming-profile hiccup): the
    /// response envelope is computed first and is unconditionally what gets returned; the audit
    /// append is attempted afterward and any failure there is caught, reported to stderr, and
    /// swallowed (see TryAuditAsync). Also surfaces McpLexicalCatalog.SkippedSessions to stderr
    /// ONLY (never in the returned JSON — see McpSearchResponse's doc comment for why a
    /// skipped-session count must stay server-side).</summary>
    private async Task<string> RunAsync(string tool, object argsForAudit,
        IReadOnlyList<string> matterIdsForAudit,
        Func<Task<(string Json, IReadOnlyList<string> SessionIds, int Count)>> op)
    {
        string argsJson = Json(argsForAudit);
        string resultJson;
        IReadOnlyList<string> sessionIds;
        int count;
        int chars;
        string outcome;

        try
        {
            var (json, ids, c) = await op();
            resultJson = json;
            sessionIds = ids;
            count = c;
            chars = json.Length;
            outcome = "ok";
        }
        catch (OperationCanceledException)
        {
            sessionIds = [];
            count = 0;
            chars = 0;
            outcome = "cancelled";
            resultJson = Json(new
            {
                contract_version = McpCorpus.ContractVersion,
                error = "The call was cancelled before it completed.",
            });
        }
        catch (McpToolException ex)
        {
            sessionIds = [];
            count = 0;
            chars = 0;
            outcome = ex.Outcome;
            resultJson = Json(new { contract_version = McpCorpus.ContractVersion, error = ex.Message });
        }
        catch (Exception ex)
        {
            sessionIds = [];
            count = 0;
            chars = 0;
            outcome = "error";
            resultJson = Json(new { contract_version = McpCorpus.ContractVersion, error = ex.Message });
        }

        await TryAuditAsync(new McpAuditEntry(time.GetUtcNow(), tool, argsJson,
            sessionIds, matterIdsForAudit, count, chars, outcome));
        WarnIfSkippedSessions();
        return resultJson;
    }

    /// <summary>Fix 1: the audit append is a second write path that can throw (disk full,
    /// permissions, roaming-profile hiccup — AppendAsync does Directory.CreateDirectory + open +
    /// write). RunAsync's no-throw-to-the-SDK guarantee must hold even when THIS throws, so the
    /// failure is caught here, reported to stderr (never stdout — stdio purity), and swallowed.
    /// The already-computed response envelope is unaffected either way.</summary>
    private async Task TryAuditAsync(McpAuditEntry entry)
    {
        try
        {
            await audit.AppendAsync(entry, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warn: mcp audit log append failed: {ex.Message}");
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
        "A hit with is_speaker_name_match=true means the query matched a PARTICIPANT'S NAME, not " +
        "transcript text - its snippet is just that speaker's first line (unrelated to the query) " +
        "and its seq may be -1 (no addressable line); never quote it as a text match, and don't " +
        "pass a -1 seq to read_transcript's around_seq. Dates are yyyy-MM-dd (to_date inclusive).")]
    public Task<string> SearchTranscripts(
        [Description("Keyword or phrase to find")] string query,
        [Description("Restrict to one matter id")] string? matter_id = null,
        [Description("Earliest session date, yyyy-MM-dd")] string? from_date = null,
        [Description("Latest session date, yyyy-MM-dd")] string? to_date = null,
        [Description("Restrict to a source app, e.g. webex")] string? app = null,
        [Description("Max hits, 1-50 (default 10)")] int limit = 10,
        CancellationToken ct = default)
        => RunAsync("search_transcripts", new { query, matter_id, from_date, to_date, app, limit },
            MatterIdsFor(matter_id),
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
            MatterIdsFor(matter_id),
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
            [],
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
            MatterIdsFor(matter_id),
            async () =>
            {
                var r = await corpus.ListSessionsAsync(matter_id, from_date, to_date, app, offset, limit, ct);
                return (Json(r), r.Sessions.Select(s => s.SessionId).ToList(), r.Sessions.Count);
            });

    [McpServerTool(Name = "list_matters"), Description(
        "List the matters the user has exposed to MCP (id, name, reference, session count).")]
    public Task<string> ListMatters(CancellationToken ct = default)
        => RunAsync("list_matters", new { }, [], async () =>
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
        => RunAsync("get_summary", new { session_id }, [], async () =>
        {
            var r = await corpus.GetSummaryAsync(session_id, ct);
            return (Json(r), [session_id], 1);
        });

    /// <summary>Fix 4: the audit's matter_ids column records what the caller ASKED for (the
    /// matter_id facet, if supplied), never what the results happened to contain.</summary>
    private static IReadOnlyList<string> MatterIdsFor(string? matterId)
        => matterId is null ? [] : [matterId];
}
