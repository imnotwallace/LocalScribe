using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MetadataEditorViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-metaed-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeReporter _reporter = new();
    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;

    public MetadataEditorViewModelTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths,
            new FakeSettings(new Settings { StorageRoot = _root }), new NoopBin(), _time);
        // A REAL controller over the 3a fakes: the live-gate test needs a genuine
        // CurrentSessionId (SessionViewModel.cs:30 is a controller passthrough).
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        _session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Matter_passthroughs_save_list_and_load_through_maintenance()
    {
        await _maintenance.SaveMatterAsync(new Matter
        {
            Id = "M-2026-001", Name = "Estate of Alpha", Reference = "EST-1",
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        Assert.True(File.Exists(_paths.MatterJson("M-2026-001")));
        var index = await _maintenance.ListMattersAsync(CancellationToken.None);
        Assert.Equal("M-2026-001", Assert.Single(index.Matters).Id);
        var back = await _maintenance.LoadMatterAsync("M-2026-001", CancellationToken.None);
        Assert.Equal("Estate of Alpha", back!.Name);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }

    private sealed class NoopBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }
}
