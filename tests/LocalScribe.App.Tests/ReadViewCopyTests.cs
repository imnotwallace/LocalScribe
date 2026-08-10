using System.IO;
using System.Linq;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The read view's copy selection rule (Tier 1 plan D, T1-9, 2026-08-05). Only the
/// DECIDABLE half is testable: RowsForCopy is a pure method on the WPF-free VM, while the
/// Clipboard.SetText call and the ListView SelectedItems read stay in window code this suite
/// cannot execute (no STA harness). The payload itself is pinned by
/// TranscriptCitationTests in Core.</summary>
public sealed class ReadViewCopyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-copy-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ReadViewViewModel MakeVm()
    {
        var paths = new StoragePaths(_root);
        var settings = new FakeSettingsService(new Settings { StorageRoot = _root });
        var maintenance = new MaintenanceService(paths, settings, new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero)));
        return new ReadViewViewModel(maintenance, paths, settings, new FakeUiErrorReporter(),
            new SilentPlayer(), dispatch: a => a(), TimeProvider.System);
    }

    private static ReadRow Row(long startMs, string name, string text)
        => new(new DisplayRow { StartMs = startMs, EndMs = startMs + 1000, DisplayName = name, Text = text });

    [Fact]
    public void A_right_click_inside_the_selection_copies_the_whole_selection()
    {
        var vm = MakeVm();
        var a = Row(0, "Me", "one");
        var b = Row(5000, "Them", "two");

        var picked = vm.RowsForCopy(clicked: b, selected: [a, b]);

        Assert.Equal(new[] { "one", "two" }, picked.Select(r => r.Text));
    }

    [Fact]
    public void A_right_click_OUTSIDE_the_selection_copies_only_the_clicked_row()
    {
        // WPF does not re-select on right-click, so the clicked row can be outside SelectedItems.
        // Copying the invisible selection instead of what was clicked would be a silent surprise.
        var vm = MakeVm();
        var a = Row(0, "Me", "one");
        var b = Row(5000, "Them", "two");
        var c = Row(9000, "Me", "three");

        var picked = vm.RowsForCopy(clicked: c, selected: [a, b]);

        Assert.Equal(new[] { "three" }, picked.Select(r => r.Text));
    }

    [Fact]
    public void A_keyboard_copy_with_no_clicked_row_falls_back_to_the_selection()
    {
        var vm = MakeVm();
        var a = Row(0, "Me", "one");

        var picked = vm.RowsForCopy(clicked: null, selected: [a]);

        Assert.Equal(new[] { "one" }, picked.Select(r => r.Text));
    }

    [Fact]
    public void Nothing_clicked_and_nothing_selected_yields_nothing_to_copy()
    {
        var vm = MakeVm();
        Assert.Empty(vm.RowsForCopy(clicked: null, selected: []));
    }

    [Fact]
    public void Selection_order_follows_the_transcript_not_the_click_order()
    {
        // Ctrl-clicking bottom-up must still cite in transcript order - a quotation block that
        // reorders the record is exactly what an evidentiary product must not produce.
        var vm = MakeVm();
        var a = Row(0, "Me", "one");
        var b = Row(5000, "Them", "two");

        var picked = vm.RowsForCopy(clicked: null, selected: [b, a]);

        Assert.Equal(new[] { "one", "two" }, picked.Select(r => r.Text));
    }

    [Fact]
    public void The_loaded_version_is_readable_for_the_citation()
    {
        var vm = MakeVm();
        Assert.Equal(TranscriptVersions.Root, vm.LoadedVersionId);   // "v1" before any load
    }

    private sealed class SilentPlayer : IDualAudioPlayer
    {
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        // Explicit empty accessors, not field-like events: this double never RAISES either, and a
        // field-like event that is only ever assigned is CS0414 ("assigned but never used").
        // Accessor-based events have no backing field, so there is nothing to warn about and
        // nothing for Dispose to null.
        public event Action? MediaReady { add { } remove { } }
        public event Action? MediaEnded { add { } remove { } }
        public void Load(string? localPath, string? remotePath) { }
        public void Play() { }
        public void Pause() { }
        public void SeekMs(long ms) => PositionMs = ms;
        public void SetLegMuted(bool local, bool muted) { }
        public void SetLegVolume(bool local, double volume) { }
        public void Dispose() { }
    }
}
