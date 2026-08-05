using LocalScribe.App.Services;
using LocalScribe.Core.Diagnostics;
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

        reporter.Report("Recovery of session s1", new InvalidOperationException("torn"));
        reporter.Info("Recovered 2 interrupted session(s)");

        // The existing balloon format is PINNED by StartupOrchestratorTests - unchanged here.
        Assert.Equal(new[] { "Recovery of session s1: torn", "Recovered 2 interrupted session(s)" },
            notices);
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(("error", "startup"), (log.Entries[0].Level, log.Entries[0].Source));
        Assert.Equal(("info", "startup"), (log.Entries[1].Level, log.Entries[1].Source));
        // Same rule as InfoBarErrorReporter: the Report CONTEXT is a fixed literal and goes bare;
        // the Info MESSAGE is caller-composed and reaches the log MARKED. StartupOrchestrator's
        // recovery summary rides this path (Task 8), and Plan B adds more callers.
        Assert.Equal("Recovery of session s1", log.Entries[0].Message);
        Assert.Equal(DiagnosticRedaction.Mark("Recovered 2 interrupted session(s)"),
            log.Entries[1].Message);
    }

    [Fact]
    public void A_reporter_built_without_a_log_still_notifies()
    {
        var notices = new List<string>();
        new TrayNoticeReporter(notices.Add).Info("hello");
        Assert.Single(notices);
    }
}
