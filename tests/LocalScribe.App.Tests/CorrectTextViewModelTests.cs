using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class CorrectTextViewModelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-correct-vm-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly MaintenanceService _maintenance;
    private readonly FakeReporter _reporter = new();
    private readonly ManualUtcTimeProvider _time = new(T0);

    public CorrectTextViewModelTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths,
            new FakeSettings(new Settings { StorageRoot = _root }), new FakeBin(), _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private async Task WriteSessionAsync(string id)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = T0.AddMinutes(10),
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            SessionMeta.CreateDefault(AppKind.Webex, T0, self: null), default);
        var transcript = new TranscriptStore(_paths.TranscriptJsonl(id));
        await transcript.AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 2000, "hello world", "Them"), default);
        await transcript.AppendAsync(
            TranscriptLine.Segment(1, TranscriptSource.Remote, 2000, 4000, "second bit", "Them"), default);
    }

    private static RowSegment Seg(int seq, string projected, string raw, bool corrected = false)
        => new(seq, TranscriptSource.Remote, seq * 2000, seq * 2000 + 2000, projected, raw, corrected, IsPinned: false);

    private CorrectTextViewModel MakeVm(string sessionId, params RowSegment[] segments)
        => new(_maintenance, _reporter, sessionId, segments, "relative", T0);

    [Fact]
    public void Items_seed_with_projected_text_and_expose_the_machine_original()
    {
        var vm = MakeVm("s", Seg(0, "projected text", "raw text"));
        Assert.Equal("projected text", vm.Items[0].EditedText);
        Assert.Equal("raw text", vm.Items[0].RawText);
        Assert.False(vm.Items[0].IsCorrected);
    }

    [Fact]
    public async Task Unchanged_items_save_nothing_and_report_done()
    {
        await WriteSessionAsync("s1");
        var vm = MakeVm("s1", Seg(0, "hello world", "hello world"));

        Assert.True(await vm.SaveAsync(default));
        Assert.False(File.Exists(Path.Combine(_paths.SessionDir("s1"), "edits.json")));
    }

    [Fact]
    public async Task Changed_text_saves_a_diff_only_batch()
    {
        await WriteSessionAsync("s2");
        var vm = MakeVm("s2", Seg(0, "hello world", "hello world"), Seg(1, "second bit", "second bit"));
        vm.Items[1].EditedText = "second EDITED bit";

        Assert.True(await vm.SaveAsync(default));

        var edits = await new EditStore(_paths.SessionDir("s2"), _time).LoadAsync(default);
        Assert.Single(edits!.Corrections);                        // seq 0 untouched
        Assert.Equal("second EDITED bit", edits.Corrections["1"].Text);
    }

    [Fact]
    public async Task Revert_checkbox_removes_the_overlay_entry()
    {
        await WriteSessionAsync("s3");
        await _maintenance.SaveTextCorrectionsAsync("s3",
            new Dictionary<int, string> { [0] = "was corrected" }, Array.Empty<int>(), default);

        var vm = MakeVm("s3", Seg(0, "was corrected", "hello world", corrected: true));
        vm.Items[0].RevertRequested = true;

        Assert.True(await vm.SaveAsync(default));
        var edits = await new EditStore(_paths.SessionDir("s3"), _time).LoadAsync(default);
        Assert.False(edits!.Corrections.ContainsKey("0"));
    }

    [Fact]
    public async Task Emptied_text_blocks_the_save_with_a_validation_message()
    {
        await WriteSessionAsync("s4");
        var vm = MakeVm("s4", Seg(0, "hello world", "hello world"));
        vm.Items[0].EditedText = "   ";

        Assert.False(await vm.SaveAsync(default));
        Assert.NotEqual("", vm.ValidationMessage);
        Assert.False(File.Exists(Path.Combine(_paths.SessionDir("s4"), "edits.json")));
        Assert.Empty(_reporter.Errors);                            // validation, not an exception
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        {
            var old = Current;
            Current = updated;
            Changed?.Invoke(old, updated);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBin : IRecycleBin
    {
        public void SendToRecycleBin(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) { }
    }
}
