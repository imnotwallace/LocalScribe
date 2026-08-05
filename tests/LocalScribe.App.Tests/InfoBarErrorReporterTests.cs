using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class InfoBarErrorReporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-infobar-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Report_and_Info_enqueue_through_dispatch_in_order()
    {
        var pending = new List<Action>();
        var reporter = new InfoBarErrorReporter(pending.Add);

        reporter.Report("Delete session", new InvalidOperationException("folder is locked"));
        reporter.Info("Recovered 2 interrupted session(s)");
        Assert.Empty(reporter.Messages);                   // marshaled via dispatch, never inline

        pending.ForEach(a => a());
        Assert.Equal(new[]
        {
            "Delete session: folder is locked",
            "Recovered 2 interrupted session(s)",
        }, reporter.Messages);
    }

    [Fact]
    public void DismissOldest_advances_the_queue_and_is_safe_when_empty()
    {
        var reporter = new InfoBarErrorReporter(a => a());
        reporter.DismissOldest();                          // empty queue: no throw
        reporter.Info("first");
        reporter.Info("second");
        reporter.DismissOldest();
        Assert.Equal(new[] { "second" }, reporter.Messages);
    }

    [Fact]
    public void Every_report_and_info_also_reaches_the_diagnostic_log_when_one_is_supplied()
    {
        var log = new FakeDiagnosticLog();
        var reporter = new InfoBarErrorReporter(a => a(), log);

        reporter.Report("Delete session", new InvalidOperationException("folder is locked"));
        reporter.Info("Recovered 2 interrupted session(s)");

        Assert.Equal(2, log.Entries.Count);
        // A LITERAL Report context ("Export", "Delete session") goes to the log bare - see
        // An_id_bearing_Report_context_is_redacted_at_the_default_setting below for the two call
        // sites that mark a variable part instead (fix round 1, 2026-08-05, Critical finding).
        Assert.Equal(("error", "ui", "Delete session"),
            (log.Entries[0].Level, log.Entries[0].Source, log.Entries[0].Message));
        // The user sees the MESSAGE only; the stack belongs in the file, marked so the
        // IncludeTranscriptText switch governs the exception text.
        Assert.Contains("System.InvalidOperationException", log.Entries[0].Detail!);
        Assert.Contains(DiagnosticRedaction.Open, log.Entries[0].Detail!);
        // An Info message is caller-composed and CAN carry party-identifying text, so it reaches
        // Write() MARKED. FakeDiagnosticLog records what Write() was handed; the marker only turns
        // into [redacted] inside the real DiagnosticLog - see the next fact.
        Assert.Equal(("info", "ui", DiagnosticRedaction.Mark("Recovered 2 interrupted session(s)")),
            (log.Entries[1].Level, log.Entries[1].Source, log.Entries[1].Message));
    }

    [Fact]
    public async Task An_Info_message_carrying_a_participant_name_is_redacted_at_the_default_setting()
    {
        // The defect this fact exists to prevent. VERIFIED live Info call sites compose privileged
        // identifiers straight into the string: MetadataEditorViewModel.cs:369
        // ($"{member.Name} is already a participant." - a real person from the matter roster),
        // ExportDialogViewModel.cs:197 ("Exported to " + dest, where dest comes from the filename
        // template and embeds the session title, i.e. the matter/client name), ImportDialogViewModel.cs:282
        // and VocabularyEditorViewModel.cs:71,90 (custom vocabulary
        // terms are BY DESIGN the names of parties and witnesses). Unmarked, all of those would sit
        // in diagnostics\diag-yyyyMM.jsonl at the DEFAULT settings - an undeclared copy of
        // privileged identifiers outside every retention and purge path. Note the smoke check
        // (Task 12) would NOT catch this: these are names, not utterances.
        var paths = new StoragePaths(_root);
        var log = new DiagnosticLog(paths, new ManualUtcTimeProvider(
            new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero)), () => new LoggingSetting());

        new InfoBarErrorReporter(a => a(), log).Info("Ms Roe is already a participant.");
        await log.FlushAsync(default);

        string text = await File.ReadAllTextAsync(
            Path.Combine(paths.DiagnosticsDir, "diag-202608.jsonl"));
        Assert.DoesNotContain("Ms Roe", text);
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public async Task An_id_bearing_Report_context_is_redacted_at_the_default_setting()
    {
        // Fix round 1 (2026-08-05, Critical finding): a Report CONTEXT is meant to stay a fixed
        // literal, but two VERIFIED call sites concatenate a session id instead -
        // StartupOrchestrator.cs ("Recovery of session " + id) and MattersPageViewModel.cs
        // ("Tag session " + sessionId) - and SessionId.cs mints yyyy-MM-dd_HHmm_{App}_{Slug(title)},
        // so the id itself carries the session TITLE, i.e. the matter/client name. Unmarked, that
        // name would sit in diagnostics\diag-yyyyMM.jsonl at the DEFAULT settings AND be latched
        // into DiagnosticLog.LastError, which Settings' "Copy last error" hands to whoever the
        // user pastes it to.
        var paths = new StoragePaths(_root);
        var log = new DiagnosticLog(paths, new ManualUtcTimeProvider(
            new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero)), () => new LoggingSetting());
        string id = "2026-08-05_1430_Webex_smith-v-jones-settlement-call";
        var reporter = new InfoBarErrorReporter(a => a(), log);

        // Mirrors the call-site fix exactly: mark ONLY the variable part.
        reporter.Report("Recovery of session " + DiagnosticRedaction.Mark(id),
            new InvalidOperationException("torn"));
        await log.FlushAsync(default);

        string text = await File.ReadAllTextAsync(
            Path.Combine(paths.DiagnosticsDir, "diag-202608.jsonl"));
        Assert.DoesNotContain("smith-v-jones-settlement-call", text);
        Assert.Contains("[redacted]", text);
        // The user-visible text is byte-identical to what it was before this fix: the marker is
        // stripped for display, never shown as "<<"/">>", regardless of the log-only setting above.
        Assert.Equal(new[] { "Recovery of session " + id + ": torn" }, reporter.Messages);
    }

    [Fact]
    public void The_log_line_is_written_even_if_the_message_is_never_dispatched()
    {
        // The InfoBar queue is drained by a window that may never open (shutdown, tray-only run),
        // so the log write must happen BEFORE the dispatch, not inside it.
        var log = new FakeDiagnosticLog();
        var reporter = new InfoBarErrorReporter(_ => { }, log);   // dispatch that never runs
        reporter.Report("Export", new IOException("no space"));
        Assert.Single(log.Entries);
        Assert.Empty(reporter.Messages);
    }

    [Fact]
    public void A_reporter_built_without_a_log_still_works()
        => new InfoBarErrorReporter(a => a()).Info("no sink, no throw");
}
