using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MattersPageViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-matters-vm-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakeSettings _settings;
    private readonly FakeBin _bin = new();
    private readonly FakeReporter _reporter = new();
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero));
    private readonly MaintenanceService _maintenance;

    public MattersPageViewModelTests()
    {
        _paths = new StoragePaths(_root);
        _settings = new FakeSettings(new Settings { StorageRoot = _root });
        _maintenance = new MaintenanceService(_paths, _settings, _bin, _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task SaveMatterAsync_persists_matter_and_index_entry_before_returning()
    {
        await _maintenance.SaveMatterAsync(new Matter
        {
            Id = "M-2026-001", Name = "N", Reference = "REF-9", Archived = true,
        }, CancellationToken.None);

        Assert.True(File.Exists(_paths.MatterJson("M-2026-001")));
        var index = await new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None);
        var entry = Assert.Single(index.Matters);
        Assert.Equal("M-2026-001", entry.Id);
        Assert.Equal("REF-9", entry.Reference);
        Assert.True(entry.Archived);   // MatterStore v2 (Task 2) copies Archived into the entry
    }

    [Fact]
    public async Task SaveMatterAsync_serializes_concurrent_index_writes()
    {
        // matters.json upsert is read-modify-write; without the index lock, parallel saves
        // lose entries. All 8 must land.
        var matters = Enumerable.Range(1, 8)
            .Select(i => new Matter { Id = $"M-2026-{i:000}", Name = $"Matter {i}" }).ToList();
        await Task.WhenAll(matters.Select(m => _maintenance.SaveMatterAsync(m, CancellationToken.None)));

        var index = await new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None);
        Assert.Equal(8, index.Matters.Count);
        foreach (var m in matters) Assert.True(File.Exists(_paths.MatterJson(m.Id)));
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
        public List<string> Recycled { get; } = new();
        public void SendToRecycleBin(string path)
        {
            Recycled.Add(path);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }
}
