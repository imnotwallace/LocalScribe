using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Dialog-local feedback plus a real CancellationTokenSource on the export dialog
/// (Tier 1 plan D, T1-5, 2026-08-05). Before this, every export outcome rendered on MainWindow's
/// InfoBar - a window this separate dialog cannot show - and all four export calls passed
/// CancellationToken.None, so a multi-gigabyte .zip could not be stopped.</summary>
public sealed class ExportDialogStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-expstat-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>A finalized on-disk session with one turn - enough for every export format to
    /// produce a real file, and small enough that the export completes inside the test.</summary>
    private async Task<(MaintenanceService Svc, FakeUiErrorReporter Errors)> MakeAsync()
    {
        var paths = new StoragePaths(_root);
        var settings = new FakeSettingsService(new Settings { StorageRoot = _root });
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero)));
        Directory.CreateDirectory(paths.SessionDir("s1"));
        await new SessionStore(paths.SessionJson("s1")).SaveAsync(new SessionRecord
        {
            Id = "s1", App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, default);
        await new MetadataStore(paths.MetaJson("s1")).SaveAsync(
            new SessionMeta { Title = "Doe intake" }, default);
        await new TranscriptStore(paths.TranscriptJsonl("s1")).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 4000, "hello", "Me"), default);
        return (svc, new FakeUiErrorReporter());
    }

    [Fact]
    public async Task A_failure_lands_in_the_dialogs_own_bar_not_only_the_shell()
    {
        var (svc, errors) = await MakeAsync();
        // A directory as the destination makes the output FileStream open throw.
        string bad = Path.Combine(_root, "a-directory");
        Directory.CreateDirectory(bad);
        var vm = new ExportDialogViewModel("s1", "Doe intake", svc, new FakeSettingsService(),
            _ => bad, _ => { }, errors, a => a()) { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(vm.HasStatus);
        Assert.True(vm.StatusIsError);
        Assert.NotNull(vm.StatusMessage);
        Assert.NotEmpty(errors.Reports);            // still queued for the shell and Plan A's log
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_success_reports_Success_severity_to_the_shell_and_leaves_no_error_status()
    {
        var (svc, errors) = await MakeAsync();
        string dest = Path.Combine(_root, "out.md");
        var vm = new ExportDialogViewModel("s1", "Doe intake", svc, new FakeSettingsService(),
            _ => dest, _ => { }, errors, a => a()) { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(dest));
        Assert.False(vm.StatusIsError);
        Assert.Equal(new[] { NoticeSeverity.Success }, errors.InfoSeverities);
    }

    [Fact]
    public async Task A_cancelled_save_as_clears_any_stale_status_and_reports_nothing()
    {
        var (svc, errors) = await MakeAsync();
        var vm = new ExportDialogViewModel("s1", "Doe intake", svc, new FakeSettingsService(),
            _ => null, _ => { }, errors, a => a()) { Format = ExportFormat.Markdown };
        vm.ShowStatus("stale from a previous attempt", isError: true);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.False(vm.HasStatus);                 // cleared at the START of the attempt
        Assert.Empty(errors.Reports);
        Assert.Empty(errors.Infos);
    }

    [Fact]
    public async Task Stop_before_the_export_starts_cancels_it_and_is_reported_as_information()
    {
        // Cancelling is a USER ACTION, not a fault: no red bar and no shell Report (the
        // ImportDialogViewModel precedent says the same for import). The pickSavePath
        // seam is the one synchronous point inside ExportAsync where a test can press Stop.
        var (svc, errors) = await MakeAsync();
        string dest = Path.Combine(_root, "cancelled.md");
        ExportDialogViewModel? vm = null;
        vm = new ExportDialogViewModel("s1", "Doe intake", svc, new FakeSettingsService(),
            _ => { vm!.StopCommand.Execute(null); return dest; }, _ => { }, errors, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        // AMENDED from the plan (2026-08-06). As written, this fact asserted only
        // !StatusIsError + no Reports + !IsBusy - every one of which is ALSO true of a
        // completely successful export, so it would have passed even if the token were still
        // CancellationToken.None and Stop did nothing at all. That is the exact class of
        // green-but-vacuous test this round exists to remove. These three assertions are what
        // make it fail when cancellation is a no-op: the cancel arm ran, no file was written,
        // and the destination is genuinely absent rather than a truncated stub.
        Assert.Equal("Export cancelled - no file was written.", vm.StatusMessage);
        Assert.False(File.Exists(dest));
        Assert.False(vm.StatusIsError);
        Assert.Empty(errors.Reports);
        Assert.Empty(errors.Infos);                 // no "Exported to ..." success notice either
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Stop_is_disabled_until_an_export_is_actually_running()
    {
        var vm = new ExportDialogViewModel("s1", "T",
            new MaintenanceService(new StoragePaths(_root), new FakeSettingsService(),
                new FakeRecycleBin(), TimeProvider.System),
            new FakeSettingsService(), _ => null, _ => { }, new FakeUiErrorReporter(), a => a());

        Assert.False(vm.StopCommand.CanExecute(null));
        vm.IsBusy = true;
        Assert.True(vm.StopCommand.CanExecute(null));
    }
}
