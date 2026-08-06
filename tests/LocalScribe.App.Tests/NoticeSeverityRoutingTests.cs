using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Per-notice severity on the shell InfoBar queue (Tier 1 plan D, T1-5, 2026-08-05).
/// The defect: MainWindow.xaml hardcoded Severity="Error" and SyncInfoBar never re-set it, so
/// "Exported to C:\..." and "Imported \"X\"." rendered red. Severities is a PARALLEL collection
/// rather than a richer element type on Messages because InfoBarErrorReporterTests and
/// MainWindowViewModelTests pin Messages as ObservableCollection&lt;string&gt;.</summary>
public sealed class NoticeSeverityRoutingTests
{
    [Fact]
    public void Report_is_always_an_error_and_plain_Info_is_informational()
    {
        var reporter = new InfoBarErrorReporter(a => a());

        reporter.Report("Delete session", new InvalidOperationException("folder is locked"));
        reporter.Info("Recovered 2 interrupted session(s)");

        Assert.Equal(new[] { "Delete session: folder is locked", "Recovered 2 interrupted session(s)" },
            reporter.Messages);
        Assert.Equal(new[] { NoticeSeverity.Error, NoticeSeverity.Informational }, reporter.Severities);
    }

    [Fact]
    public void An_explicit_severity_rides_the_message_at_the_same_index()
    {
        var reporter = new InfoBarErrorReporter(a => a());

        reporter.Info("Exported to C:\\out.docx", NoticeSeverity.Success);
        reporter.Info("Audio outlasted the transcript", NoticeSeverity.Warning);

        Assert.Equal(reporter.Messages.Count, reporter.Severities.Count);
        Assert.Equal(NoticeSeverity.Success, reporter.Severities[0]);
        Assert.Equal(NoticeSeverity.Warning, reporter.Severities[1]);
    }

    [Fact]
    public void DismissOldest_advances_both_queues_together()
    {
        var reporter = new InfoBarErrorReporter(a => a());
        reporter.DismissOldest();                                  // empty: still no throw
        reporter.Info("first", NoticeSeverity.Success);
        reporter.Report("Second", new InvalidOperationException("boom"));

        reporter.DismissOldest();

        Assert.Equal(new[] { "Second: boom" }, reporter.Messages);
        Assert.Equal(new[] { NoticeSeverity.Error }, reporter.Severities);
    }

    [Fact]
    public void A_reporter_that_only_implements_the_narrow_Info_still_receives_the_message()
    {
        // The widening is a DEFAULT INTERFACE METHOD so that the 24 hand-written fakes and
        // TrayNoticeReporter (a balloon has no severity concept) need no SECOND edit - Plan A
        // already gave every one of them `Info(string message, bool privileged = true)`. Prove the
        // default body forwards rather than swallowing.
        var narrow = new NarrowReporter();
        IUiErrorReporter seam = narrow;

        seam.Info("only the message survives", NoticeSeverity.Success);

        Assert.Equal(new[] { "only the message survives" }, narrow.Seen);
    }

    private sealed class NarrowReporter : IUiErrorReporter
    {
        public List<string> Seen { get; } = new();
        public void Report(string context, Exception ex) => Seen.Add(context + ": " + ex.Message);
        // The trailing `bool privileged = true` is Plan A's shipped interface member - a
        // one-parameter Info(string) does not implement it (CS0535).
        public void Info(string message, bool privileged = true) => Seen.Add(message);
    }
}
