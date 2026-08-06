using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>A GENERAL dialog-local status bar on the read view (Tier 1 plan D, T1-5, 2026-08-05).
/// The window already had a SaveError bar, but it is titled "Couldn't save your edits" and can
/// only carry that one kind of message - so the correction and reassign dialogs it parents still
/// reported to MainWindow's InfoBar, which they cannot show. Task 8's clipboard failures land
/// here too.</summary>
public sealed class ReadViewStatusTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-status-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (ReadViewViewModel Vm, FakeUiErrorReporter Shell) MakeVm()
    {
        var paths = new StoragePaths(_root);
        var settings = new FakeSettingsService(new Settings { StorageRoot = _root });
        var maintenance = new MaintenanceService(paths, settings, new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero)));
        var shell = new FakeUiErrorReporter();
        return (new ReadViewViewModel(maintenance, paths, settings, shell,
            new SilentPlayer(), dispatch: a => a(), TimeProvider.System), shell);
    }

    [Fact]
    public void The_child_dialog_reporter_TEES_to_the_bar_and_the_shell_never_replacing_it()
    {
        // The correction and speaker-reassign dialogs are the read view's evidentiary WRITE
        // paths. A failed write must reach BOTH surfaces: this window's bar (the dialog is what
        // the user is looking at) AND the shell queue, which outlives the dialog and is where
        // Plan A's diagnostic log picks it up. An adapter that only called ShowStatus would make
        // a failed correction unrecordable and unreportable.
        var (vm, shell) = MakeVm();

        vm.DialogReporter.Report("Save text corrections", new IOException("file is locked"));

        Assert.True(vm.StatusIsError);
        Assert.Contains("file is locked", vm.StatusMessage);
        var (context, ex) = Assert.Single(shell.Reports);
        Assert.Equal("Save text corrections", context);
        Assert.Equal("file is locked", ex.Message);
    }

    [Fact]
    public void Status_starts_empty_and_tracks_message_and_severity()
    {
        var (vm, _) = MakeVm();
        Assert.False(vm.HasStatus);
        Assert.Null(vm.StatusMessage);

        vm.ShowStatus("Copied 3 turns with citations.", isError: false);
        Assert.True(vm.HasStatus);
        Assert.False(vm.StatusIsError);
        Assert.Equal("Copied 3 turns with citations.", vm.StatusMessage);

        vm.ShowStatus("Couldn't save the correction: file is locked", isError: true);
        Assert.True(vm.StatusIsError);

        vm.ClearStatus();
        Assert.False(vm.HasStatus);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public void The_save_error_bar_is_untouched_and_stays_independent()
    {
        // SaveError is a SEPARATE, titled bar pinned by ReadViewEditModeTests. Setting one must
        // not move the other, or an edit failure would be silently replaced by a copy notice.
        var (vm, _) = MakeVm();
        vm.SaveError = "Couldn't save your transcript edits: disk full";
        vm.ShowStatus("Copied 1 turn.", isError: false);

        Assert.True(vm.HasSaveError);
        Assert.True(vm.HasStatus);
        Assert.NotEqual(vm.SaveError, vm.StatusMessage);
    }

    private sealed class SilentPlayer : IDualAudioPlayer
    {
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public event Action? MediaReady;
        public event Action? MediaEnded;
        public void Load(string? localPath, string? remotePath) { }
        public void Play() { }
        public void Pause() { }
        public void SeekMs(long ms) => PositionMs = ms;
        public void SetLegMuted(bool local, bool muted) { }
        public void SetLegVolume(bool local, double volume) { }
        public void Dispose() { MediaReady = null; MediaEnded = null; }
    }
}
