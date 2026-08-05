using System.Text.Json.Nodes;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

/// <summary>The on-disk diagnostic log (Tier 1 plan A, 2026-08-05, spec item T1-1). Modelled on
/// McpAuditLog - the repo's only append-only log - down to FileMode.Append, FileShare.ReadWrite |
/// FileShare.Delete, one JSON line per entry and CALENDAR-MONTH rotation. Size-based rolling was
/// REJECTED: it would make this the first DELETING writer in a codebase whose core rule is
/// append-only, and the log is small.</summary>
public sealed class DiagnosticLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 9, 30, 0, TimeSpan.Zero);

    // NOT created by the ctor: the no-IO-in-the-constructor test asserts this path does not exist.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-diaglog-" + Guid.NewGuid().ToString("N"));
    private LoggingSetting _logging = new();

    private StoragePaths Paths => new(_root);
    private DiagnosticLog MakeLog(ManualUtcTimeProvider time) => new(Paths, time, () => _logging);
    private string File202608 => Path.Combine(Paths.DiagnosticsDir, "diag-202608.jsonl");

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Construction_touches_no_disk()
    {
        _ = MakeLog(new ManualUtcTimeProvider(T0));
        // CompositionRootTests.cs:16 calls the REAL CompositionRoot.Build(), which builds one of
        // these - ctor-time IO would create folders in the developer's actual
        // %USERPROFILE%\LocalScribe on every test run. Directory.CreateDirectory lives in the
        // drain, exactly as McpAuditLog.AppendAsync does.
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Flushing_an_empty_log_writes_nothing_and_never_throws()
    {
        await MakeLog(new ManualUtcTimeProvider(T0)).FlushAsync(default);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Writes_one_camel_case_json_line_per_entry_into_the_monthly_file()
    {
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        log.Write("warn", "capture", "Local leg stalled - no frames", "gapMs=4200");
        log.Write("info", "session", "State Recording");
        await log.FlushAsync(default);

        var lines = await File.ReadAllLinesAsync(File202608);
        Assert.Equal(2, lines.Length);                       // one line per entry, order preserved
        var first = JsonNode.Parse(lines[0])!.AsObject();
        Assert.Equal("2026-08-05T09:30:00+00:00", first["tsUtc"]!.GetValue<string>());
        Assert.Equal("warn", first["level"]!.GetValue<string>());
        Assert.Equal("capture", first["source"]!.GetValue<string>());
        Assert.Equal("Local leg stalled - no frames", first["message"]!.GetValue<string>());
        Assert.Equal("gapMs=4200", first["detail"]!.GetValue<string>());
        // A null detail is omitted entirely rather than written as null (LocalScribeJson's
        // WhenWritingNull convention), so a support file stays readable. ContainsKey (not the
        // indexer) is the only way to tell "omitted" from "present and JSON null" apart -
        // JsonNode's indexer returns a null reference for BOTH, which made the previous
        // Assert.Null(...) form here vacuous (I-1, fix round 1, 2026-08-05).
        Assert.False(JsonNode.Parse(lines[1])!.AsObject().ContainsKey("detail"));
    }

    [Fact]
    public async Task Files_rotate_on_the_entry_calendar_month_not_the_drain_time()
    {
        var time = new ManualUtcTimeProvider(new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero));
        var log = MakeLog(time);
        log.Write("info", "session", "august line");
        time.Set(new DateTimeOffset(2026, 9, 1, 0, 0, 30, TimeSpan.Zero));
        log.Write("info", "session", "september line");
        await log.FlushAsync(default);          // ONE drain spanning two months

        Assert.Contains("august line",
            await File.ReadAllTextAsync(Path.Combine(Paths.DiagnosticsDir, "diag-202608.jsonl")));
        Assert.Contains("september line",
            await File.ReadAllTextAsync(Path.Combine(Paths.DiagnosticsDir, "diag-202609.jsonl")));
    }

    [Fact]
    public async Task The_level_gate_is_re_read_from_the_settings_func_on_every_write()
    {
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        _logging = new LoggingSetting { Level = "error" };
        log.Write("info", "session", "quiet");
        log.Write("error", "session", "loud");
        await log.FlushAsync(default);
        Assert.Contains("loud", Assert.Single(await File.ReadAllLinesAsync(File202608)));

        // SettingsService SWAPS the settings reference on save, so a value captured at
        // construction would pin the level at startup - the func must be re-invoked per Write.
        _logging = new LoggingSetting { Level = "debug" };
        log.Write("debug", "session", "now audible");
        await log.FlushAsync(default);
        Assert.Equal(2, (await File.ReadAllLinesAsync(File202608)).Length);
    }

    [Fact]
    public async Task Transcript_bearing_text_is_redacted_at_every_level_when_the_switch_is_off()
    {
        _logging = new LoggingSetting { Level = "debug" };      // nothing gated out by level
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        string privileged = DiagnosticRedaction.Mark("the witness never signed that document");
        foreach (string level in new[] { "error", "warn", "info", "debug" })
            log.Write(level, "session", "Segment rejected", "seq=7 text=" + privileged);
        await log.FlushAsync(default);

        string text = await File.ReadAllTextAsync(File202608);
        Assert.Equal(4, (await File.ReadAllLinesAsync(File202608)).Length);
        Assert.DoesNotContain("never signed", text);           // the promise Logging made in v1
        Assert.Contains("seq=7", text);                        // the diagnostic value survives
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public async Task Turning_IncludeTranscriptText_on_keeps_the_content_and_strips_the_markers()
    {
        _logging = new LoggingSetting { IncludeTranscriptText = true };
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        log.Write("info", "session", "Segment rejected",
            "seq=7 text=" + DiagnosticRedaction.Mark("hello there"));
        await log.FlushAsync(default);

        string text = await File.ReadAllTextAsync(File202608);
        Assert.Contains("seq=7 text=hello there", text);
        Assert.DoesNotContain("<<", text);
        Assert.DoesNotContain("[redacted]", text);
    }

    [Fact]
    public async Task Appending_tolerates_a_concurrent_reader()
    {
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        log.Write("info", "session", "one");
        await log.FlushAsync(default);
        // FileShare.ReadWrite | FileShare.Delete, McpAuditLog's flags: a user reading the file (or
        // Explorer previewing it) must never block the writer.
        using var reader = new FileStream(File202608, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        log.Write("info", "session", "two");
        await log.FlushAsync(default);
        Assert.Equal(2, (await File.ReadAllLinesAsync(File202608)).Length);
    }

    [Fact]
    public async Task A_failed_drain_re_queues_entries_for_a_retry_that_later_succeeds()
    {
        // I-2, fix round 1, 2026-08-05: a transient sharing violation (AV scanner, Explorer
        // preview, a locked file) must not destroy the batch that failed to land - it must be
        // retried on the NEXT drain. FileMode.Create + FileShare.None is a genuine OS-level
        // sharing violation, not a fake: DrainAsync's own FileMode.Append open will fail exactly
        // as it would against a real locking process.
        Directory.CreateDirectory(Paths.DiagnosticsDir);
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        // The lock is acquired BEFORE Write(): Write() kicks its drain in the background
        // immediately (not on FlushAsync), so locking AFTER Write() would race the test's own
        // FileStream open against that background attempt. Locking first makes the very first
        // drain attempt deterministically hit the sharing violation - no race, no flake.
        // FileShare.None also blocks a read from this same process, so no in-lock read assertion
        // is possible here - the only observable proof is what happens once the lock is released.
        using (new FileStream(File202608, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            log.Write("info", "session", "will retry");
            await log.FlushAsync(default);           // the append attempt must fail and swallow
        }
        // Lock released: the re-queued entry must still be there for the next drain to land.
        await log.FlushAsync(default);
        Assert.Contains("will retry", await File.ReadAllTextAsync(File202608));
    }

    [Fact]
    public async Task One_failing_month_group_does_not_block_another_in_the_same_batch()
    {
        var time = new ManualUtcTimeProvider(new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero));
        var log = MakeLog(time);
        Directory.CreateDirectory(Paths.DiagnosticsDir);
        string augustFile = Path.Combine(Paths.DiagnosticsDir, "diag-202608.jsonl");
        string septemberFile = Path.Combine(Paths.DiagnosticsDir, "diag-202609.jsonl");
        // Lock acquired BEFORE either Write(): Write() kicks its drain in the background
        // immediately, so the lock must already be held when the FIRST (august-only) drain
        // attempt fires, or the test's own FileStream open would race it (see the sibling test's
        // comment for the failure mode this avoids).
        using (new FileStream(augustFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            log.Write("info", "session", "august line");
            time.Set(new DateTimeOffset(2026, 9, 1, 0, 0, 30, TimeSpan.Zero));
            log.Write("info", "session", "september line");
            // ONE drain chain, two month groups: locking august's file must not stop september's
            // sibling group in the same batch from landing (the try moved INSIDE the foreach).
            await log.FlushAsync(default);
        }
        Assert.Contains("september line", await File.ReadAllTextAsync(septemberFile));

        // The locked august entry was not lost either - it re-drains once the lock is gone.
        await log.FlushAsync(default);
        Assert.Contains("august line", await File.ReadAllTextAsync(augustFile));
    }

    [Fact]
    public async Task A_failed_drain_records_itself_as_the_last_error()
    {
        // "Copy last error" (Task 11) must be able to surface a write failure even though the
        // failure happened inside the log's own drain, not in caller code (I-2).
        Directory.CreateDirectory(Paths.DiagnosticsDir);
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        // Lock acquired BEFORE Write() for the same reason as the sibling retry test: Write()'s
        // drain fires in the background immediately, not on FlushAsync.
        using (new FileStream(File202608, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            log.Write("info", "session", "will fail to land");
            await log.FlushAsync(default);
        }

        var last = log.LastError;
        Assert.NotNull(last);
        Assert.Equal("error", last!.Level);
        Assert.Equal("diagnostics", last.Source);
        // Fix round 1 (2026-08-05): the path itself is redacted at the default setting (see
        // The_drain_failure_path_is_redacted_by_default_but_the_exception_type_survives below
        // for the full contract) - this test only needs the exception TYPE to still be present.
        Assert.Contains(nameof(IOException), last.Detail);
    }

    [Fact]
    public async Task The_drain_failure_path_is_redacted_by_default_but_the_exception_type_survives()
    {
        // Coordinator fix round 1, IMPORTANT finding 2 (2026-08-05): `file` embeds
        // {StorageRoot}\diagnostics\diag-YYYYMM.jsonl, and StorageRoot is USER-CHOSEN - a
        // solicitor who names it after a client must never have that name reach "Copy last
        // error"'s clipboard text merely because the log itself failed to write. _root here
        // stands in for a matter-shaped root (this fixture's own temp path is unique per test
        // run, so its presence/absence in Detail is a genuine signal, not a coincidence).
        Directory.CreateDirectory(Paths.DiagnosticsDir);
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        using (new FileStream(File202608, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            log.Write("info", "session", "will fail to land");
            await log.FlushAsync(default);
        }
        string redactedDetail = log.LastError!.Detail!;
        Assert.Contains(nameof(IOException), redactedDetail);   // the diagnostic signal survives
        Assert.Contains("[redacted]", redactedDetail);
        Assert.DoesNotContain(_root, redactedDetail);            // the user-chosen root does not

        // The user's own opt-in restores the path for genuine debugging - never a permanent loss.
        _logging = new LoggingSetting { IncludeTranscriptText = true };
        using (new FileStream(File202608, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            log.Write("info", "session", "will fail to land again");
            await log.FlushAsync(default);
        }
        Assert.Contains(_root, log.LastError!.Detail);
    }

    [Fact]
    public async Task LastError_holds_only_the_most_recent_error_and_is_already_redacted()
    {
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        Assert.Null(log.LastError);
        log.Write("warn", "capture", "just a warning");
        Assert.Null(log.LastError);                            // warnings are not errors
        log.Write("error", "export", "first failure", DiagnosticRedaction.Mark("privileged"));
        log.Write("error", "export", "second failure");
        await log.FlushAsync(default);

        var last = log.LastError!;
        Assert.Equal("second failure", last.Message);
        Assert.Equal("export", last.Source);
        // The stored entry is the REDACTED one, so Settings' "Copy last error" cannot put
        // privileged text on the clipboard by going round the log file.
        log.Write("error", "export", "third", DiagnosticRedaction.Mark("privileged"));
        Assert.Equal("[redacted]", log.LastError!.Detail);
    }

    [Fact]
    public void Write_never_throws_even_when_the_settings_func_does()
    {
        var log = new DiagnosticLog(Paths, new ManualUtcTimeProvider(T0),
            () => throw new InvalidOperationException("settings gone"));
        log.Write("error", "session", "still fine");           // must not propagate
    }
}
