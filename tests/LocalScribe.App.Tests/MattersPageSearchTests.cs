// tests/LocalScribe.App.Tests/MattersPageSearchTests.cs
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.4 5.3 roll-out: the Matters manager's left list gains a search box filtering
/// by Name + Reference + Id (OrdinalIgnoreCase), composing with the existing ShowArchived toggle.</summary>
public sealed class MattersPageSearchTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-matters-search-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakeReporter _reporter = new();
    private readonly FakeBin _bin = new();
    private readonly MaintenanceService _maintenance;

    public MattersPageSearchTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths,
            new FakeSettings(new Settings { StorageRoot = _root }), _bin,
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero)));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private MattersPageViewModel MakeVm()
        => new(_maintenance, new MatterDeleter(_paths, _bin), new WindowRegistry(),
            _reporter, dispatch: a => a());

    private Task SeedAsync(string id, string name, string? reference = null, bool archived = false)
        => _maintenance.SaveMatterAsync(new Matter
        { Id = id, Name = name, Reference = reference, Archived = archived }, CancellationToken.None);

    [Fact]
    public async Task Search_filters_matters_by_name_reference_and_id()
    {
        await SeedAsync("M-20260701-001", "Estate of Alpha", reference: "EST-9");
        await SeedAsync("M-20260701-002", "Beta Trust");
        var vm = MakeVm();
        await vm.RefreshAsync();
        Assert.Equal(2, vm.Matters.Count);

        vm.SearchText = "alpha";                              // name, case-insensitive
        Assert.Equal("M-20260701-001", Assert.Single(vm.Matters).Id);

        vm.SearchText = "est-9";                              // reference
        Assert.Equal("M-20260701-001", Assert.Single(vm.Matters).Id);

        vm.SearchText = "0701-002";                           // id fragment
        Assert.Equal("M-20260701-002", Assert.Single(vm.Matters).Id);

        vm.SearchText = "";                                   // cleared: full list back
        Assert.Equal(2, vm.Matters.Count);
        Assert.Empty(_reporter.Errors);
    }

    [Fact]
    public async Task Search_composes_with_ShowArchived()
    {
        await SeedAsync("M-20260701-001", "New Estate");
        await SeedAsync("M-20260701-002", "Old Estate", archived: true);
        var vm = MakeVm();
        await vm.RefreshAsync();

        vm.SearchText = "estate";                             // matches both...
        Assert.Equal("M-20260701-001", Assert.Single(vm.Matters).Id);   // ...archived stays gated

        vm.ShowArchived = true;                               // toggle composes with the search
        Assert.Equal(2, vm.Matters.Count);

        vm.SearchText = "old";
        Assert.Equal("M-20260701-002", Assert.Single(vm.Matters).Id);
        Assert.Empty(_reporter.Errors);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
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
