using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Diagnostics;

/// <summary>One diagnostic line. DERIVED data, never evidence - see StoragePaths.DiagnosticsDir.
/// Message and Detail are redacted per Settings.Logging.IncludeTranscriptText before they reach
/// disk; a caller may pass transcript-bearing text (wrapped in DiagnosticRedaction.Mark) and MUST
/// be able to trust that switch.</summary>
public sealed record DiagnosticEntry(DateTimeOffset TsUtc, string Level, string Source,
    string Message, string? Detail);

/// <summary>Fire-and-forget diagnostic sink. Write() NEVER throws and never blocks on IO - the
/// enqueue takes an uncontended lock and returns. It is called from a DispatcherUnhandledException
/// handler, from capture frame loops and from finally blocks, none of which can tolerate an await
/// or a fault. Entries are queued and drained by a single chained background writer; FlushAsync
/// drains on the exit path.</summary>
public interface IDiagnosticLog
{
    /// <param name="level">"error" | "warn" | "info" | "debug" - compared against
    /// Settings.Logging.Level, which is finally read by production code.</param>
    /// <param name="source">Stable short subsystem tag, e.g. "capture", "session", "export".</param>
    void Write(string level, string source, string message, string? detail = null);

    /// <summary>Drains the queue. Awaited by App.OnExit and by the tray Exit path. Never throws.</summary>
    Task FlushAsync(CancellationToken ct);
}

/// <summary>camelCase, one line, nulls omitted - the storage-layer convention (LocalScribeJson).
/// McpAuditLog's snake_case is MCP WIRE style and deliberately not followed here: this file is
/// read by whoever is supporting the user, beside camelCase session.json and meta.json.
///
/// F19 (final whole-branch review, 2026-08-05): tsUtc goes through the SAME UtcIso8601Converter as
/// every evidentiary *AtUtc field in session.json and meta.json, so a support engineer reading a
/// diagnostic line beside a session record sees one timestamp shape, not two. It used to serialise
/// as System.Text.Json's default round-trip form ("2026-08-05T09:30:00.0548089+00:00"). Decided
/// NOW, at merge, precisely because Plans B/C/D append to these same monthly files: changing the
/// format later would produce a mid-file format change, which is worse than either form.
///
/// COST, stated plainly: that converter TRUNCATES sub-second precision (it formats
/// "yyyy-MM-ddTHH:mm:ssZ"), so milliseconds are lost. Within-file ORDER is unaffected - the drain
/// appends entries in queue order and never sorts - but two lines in the same second now carry
/// equal tsUtc, so a reader re-sorting a file whose lines were re-queued after a failed drain
/// cannot separate them by timestamp alone and must fall back to file order. That is the same
/// trade the spec already made for every evidentiary timestamp (see UtcIso8601Converter's own
/// doc), and consistency with the files this log sits beside was ruled the higher value.</summary>
internal static class DiagnosticJson
{
    internal static readonly JsonSerializerOptions Line = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new UtcIso8601Converter() },
    };
}

/// <summary>Append-only diagnostic log (Tier 1 plan A, 2026-08-05, spec item T1-1): one JSONL file
/// per calendar month under diagnostics\, no pruning (the McpAuditLog keep-everything posture) -
/// the whole folder is DERIVED and safe to delete wholesale, so nothing needs to prune it.
///
/// Never contains transcript text unless Settings.Logging.IncludeTranscriptText is on; see
/// DiagnosticRedaction. Bypasses AtomicFile deliberately: AtomicFile rewrites WHOLE files (tmp +
/// move) and has no append, so routing a log through it would rewrite the month's file on every
/// line. McpAuditLog made the same call.
///
/// Writes are queued and drained by ONE chained background task - the single-writer form
/// SHARED-CONTRACT section 1 was AMENDED to on 2026-08-05. REJECTED: McpAuditLog's SemaphoreSlim
/// gate, which that table originally mandated by analogy - McpAuditLog.AppendAsync is async and can
/// await a gate, whereas this Write() is VOID fire-and-forget and structurally cannot, and
/// FlushAsync needs a handle to await, which a semaphore does not give it. The chain is the
/// single-writer guarantee. The lock below is taken only to swap the chain head, never held across
/// IO, so Write() still returns without waiting on the disk.
/// </summary>
public sealed class DiagnosticLog(StoragePaths paths, TimeProvider time, Func<LoggingSetting> settings)
    : IDiagnosticLog
{
    // REJECTED (I-2, fix round 1, 2026-08-05): unbounded re-queue of a failed batch. A
    // persistent failure (a permanently invalid DiagnosticsDir, a drive gone missing) would
    // otherwise grow the queue forever - the one component whose job is recording what is going
    // wrong would itself become the unbounded memory leak and BE the outage. 2000 is generous
    // headroom over one drain's realistic batch (single digits to low hundreds of entries even
    // under a busy capture session) while still bounding the worst case; entries beyond the cap
    // are dropped rather than blocking Write(), which must never block on IO or on backpressure.
    private const int MaxRequeuedEntries = 2000;

    private readonly ConcurrentQueue<DiagnosticEntry> _queue = new();
    private readonly object _pumpGate = new();
    private Task _pump = Task.CompletedTask;
    private DiagnosticEntry? _lastError;

    /// <summary>The most recent error-level entry this process recorded, ALREADY redacted, or null
    /// when nothing has failed. Public and concrete (not on IDiagnosticLog): Settings' "Copy last
    /// error" is the only consumer and it holds the concrete type through AppComposition.</summary>
    public DiagnosticEntry? LastError => Volatile.Read(ref _lastError);

    public void Write(string level, string source, string message, string? detail = null)
    {
        try
        {
            var cfg = settings() ?? new LoggingSetting();
            if (DiagnosticLevels.Rank(level) > DiagnosticLevels.Rank(cfg.Level)) return;
            bool keep = cfg.IncludeTranscriptText;
            // Redact at WRITE time, not drain time: the switch that was in force when the line was
            // produced is the one that governs it, and it makes the in-memory LastError safe too.
            var entry = new DiagnosticEntry(time.GetUtcNow(), level, source,
                DiagnosticRedaction.Apply(message, keep) ?? "",
                DiagnosticRedaction.Apply(detail, keep));
            if (DiagnosticLevels.Rank(level) == 0) Volatile.Write(ref _lastError, entry);
            _queue.Enqueue(entry);
            Kick();
        }
        catch
        {
            // A diagnostic sink must NEVER be the thing that breaks the app it is diagnosing -
            // this method is called from a DispatcherUnhandledException handler and from finally
            // blocks, where a throw would be fatal or would mask the original failure.
        }
    }

    /// <summary>Drains everything queued before the call. The CancellationToken is accepted for
    /// call-site symmetry and deliberately NOT honoured: abandoning a drain mid-exit is exactly how
    /// the last line before a crash gets lost. App.OnExit bounds the wait instead.</summary>
    public Task FlushAsync(CancellationToken ct) => Kick();

    private Task Kick()
    {
        lock (_pumpGate)
        {
            // Chain onto the previous pump so there is only ever ONE writer touching the file, and
            // so a Flush queued after N writes observes all N of them.
            _pump = _pump.ContinueWith(_ => DrainAsync(), TaskScheduler.Default).Unwrap();
            return _pump;
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            var batch = new List<DiagnosticEntry>();
            while (_queue.TryDequeue(out var entry)) batch.Add(entry);
            if (batch.Count == 0) return;              // no queue, no folder - see the ctor rule

            // Grouped by the ENTRY's month, not the drain clock: a line written at 23:59:59 on
            // the 31st belongs in that month's file even if the drain lands a second later.
            //
            // The per-group try is INSIDE this loop, not around it (I-2, fix round 1,
            // 2026-08-05): a sharing violation on August's file must not take a same-batch
            // September write down with it. A failed group is re-queued (bounded, see
            // MaxRequeuedEntries) so the NEXT drain - not a retry loop here, which could spin
            // against a hard failure and delay every caller chained after it on the pump - gets
            // another chance once the disk recovers.
            foreach (var month in batch.GroupBy(
                         e => e.TsUtc.ToString("yyyyMM", CultureInfo.InvariantCulture)))
            {
                var entries = month.ToList();
                string file = Path.Combine(paths.DiagnosticsDir, "diag-" + month.Key + ".jsonl");
                try
                {
                    Directory.CreateDirectory(paths.DiagnosticsDir);
                    var sb = new StringBuilder();
                    foreach (var e in entries)
                        sb.Append(JsonSerializer.Serialize(e, DiagnosticJson.Line))
                          .Append(Environment.NewLine);
                    await using var s = new FileStream(file, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    await s.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Same rule as Write: a full disk, a locked file or a deleted storage root
                    // must cost the diagnostic line's TIMELY delivery, never the session - but
                    // unlike the old bare swallow, the line itself is not lost (RequeueForRetry)
                    // and the failure is not invisible (RecordDrainFailure), so a permanently
                    // misconfigured DiagnosticsDir no longer silently logs nothing forever.
                    RequeueForRetry(entries);
                    RecordDrainFailure(ex, file);
                }
            }
        }
        catch
        {
            // Belt-and-braces around the per-group try above: FlushAsync's contract is "Never
            // throws" (see IDiagnosticLog), and this outer guard is the backstop for anything
            // outside the per-month block itself (e.g. TryDequeue, GroupBy) - it is not expected
            // to fire, and unlike the per-month catch it does not know which entries to
            // re-queue, so it deliberately does not attempt to (nothing here is known-lost: the
            // per-month catch already owns re-queueing for the one failure mode this method
            // actually expects).
        }
    }

    private void RequeueForRetry(List<DiagnosticEntry> entries)
    {
        try
        {
            foreach (var e in entries)
            {
                if (_queue.Count >= MaxRequeuedEntries) break;
                _queue.Enqueue(e);
            }
        }
        catch
        {
            // Never let the recovery path for a failed drain itself become a second fault.
        }
    }

    private void RecordDrainFailure(Exception ex, string file)
    {
        try
        {
            // A synthetic entry, deliberately NOT routed through Write(): Write() enqueues onto
            // the same queue this drain just failed to empty, so calling it here risks looping
            // the failing path back on itself. This entry is visible via LastError only - it is
            // not itself queued for disk, because the disk is precisely what just failed.
            //
            // Fix round 1 (2026-08-05, coordinator IMPORTANT finding 2): `file` is
            // {StorageRoot}\diagnostics\diag-YYYYMM.jsonl, and StorageRoot is USER-CHOSEN - a
            // solicitor who names it after a client (e.g. "D:\Matters\Smith v Jones\
            // LocalScribe") would otherwise have that name land straight on the clipboard via
            // "Copy last error" the moment the log itself fails to write, of all things. Mark it
            // and apply the SAME gate Write() uses (redact at the moment an entry is produced,
            // not at drain time) so the default keeps it out. The exception TYPE NAME stays
            // unmarked: it is the actual diagnostic signal, and marking it would repeat the
            // over-redaction this plan has already been walked back from twice.
            bool keep = (settings() ?? new LoggingSetting()).IncludeTranscriptText;
            string pathInfo = DiagnosticRedaction.Apply(DiagnosticRedaction.Mark(file), keep) ?? "";
            var entry = new DiagnosticEntry(time.GetUtcNow(), DiagnosticLevels.Error, "diagnostics",
                "Diagnostic log write failed", $"{ex.GetType().Name}: path={pathInfo}");
            Volatile.Write(ref _lastError, entry);
        }
        catch
        {
            // Same rule as Write(): recording that logging failed must never itself throw.
        }
    }
}
