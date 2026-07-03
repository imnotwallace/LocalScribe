using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class InfoBarErrorReporterTests
{
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
}
