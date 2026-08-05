using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The startup/background half of the IUiErrorReporter seam (Tier 1 plan A, 2026-08-05).
/// A tray balloon is suppressed outright by Focus Assist, so the log line is the only durable
/// record of a recovery failure - it must be written whether or not the balloon is seen.</summary>
public sealed class TrayNoticeReporterTests
{
    [Fact]
    public void Report_and_Info_notify_and_log()
    {
        var notices = new List<string>();
        var log = new FakeDiagnosticLog();
        var reporter = new TrayNoticeReporter(notices.Add, log);

        // "Startup scan" is a genuinely FIXED literal (StartupOrchestrator.cs's catch-all path) -
        // fix round 1 (2026-08-05, Critical finding) replaced the previous id-bearing example
        // here ("Recovery of session s1"), which pinned the exact leak it claimed to rule out: a
        // session id is not opaque, it embeds the session TITLE (SessionId.cs mints
        // yyyy-MM-dd_HHmm_{App}_{Slug(title)}). See
        // An_id_bearing_Report_context_is_redacted_at_the_default_setting below for that case.
        reporter.Report("Startup scan", new InvalidOperationException("torn"));
        reporter.Info("Recovered 2 interrupted session(s)");

        // The existing balloon format is PINNED by StartupOrchestratorTests - unchanged here.
        Assert.Equal(new[] { "Startup scan: torn", "Recovered 2 interrupted session(s)" },
            notices);
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(("error", "startup"), (log.Entries[0].Level, log.Entries[0].Source));
        Assert.Equal(("info", "startup"), (log.Entries[1].Level, log.Entries[1].Source));
        // A LITERAL Report context goes to the log bare; the Info MESSAGE is caller-composed and
        // reaches the log MARKED. StartupOrchestrator's recovery summary rides this path (Task 8),
        // and Plan B adds more callers.
        Assert.Equal("Startup scan", log.Entries[0].Message);
        Assert.Equal(DiagnosticRedaction.Mark("Recovered 2 interrupted session(s)"),
            log.Entries[1].Message);
    }

    [Fact]
    public async Task An_id_bearing_Report_context_is_redacted_at_the_default_setting()
    {
        // Mirrors InfoBarErrorReporterTests' fact of the same name (fix round 1, 2026-08-05,
        // Critical finding). StartupOrchestrator.cs's own recovery-failure path is exactly this
        // shape: "Recovery of session " + DiagnosticRedaction.Mark(id), where id carries the
        // session TITLE (the matter/client name). Drives a REAL DiagnosticLog to real disk.
        string root = Path.Combine(Path.GetTempPath(), "ls-tray-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePaths(root);
            var log = new DiagnosticLog(paths, new ManualUtcTimeProvider(
                new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero)), () => new LoggingSetting());
            string id = "2026-08-05_1430_Webex_smith-v-jones-settlement-call";
            var notices = new List<string>();
            var reporter = new TrayNoticeReporter(notices.Add, log);

            reporter.Report("Recovery of session " + DiagnosticRedaction.Mark(id),
                new InvalidOperationException("torn"));
            await log.FlushAsync(default);

            string text = await File.ReadAllTextAsync(
                Path.Combine(paths.DiagnosticsDir, "diag-202608.jsonl"));
            Assert.DoesNotContain("smith-v-jones-settlement-call", text);
            Assert.Contains("[redacted]", text);
            // Same balloon text as before this fix - the marker never reaches notify().
            Assert.Equal(new[] { "Recovery of session " + id + ": torn" }, notices);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void A_reporter_built_without_a_log_still_notifies()
    {
        var notices = new List<string>();
        new TrayNoticeReporter(notices.Add).Info("hello");
        Assert.Single(notices);
    }
}
