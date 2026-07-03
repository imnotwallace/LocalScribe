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

    private MattersPageViewModel MakeVm()
        => new(_paths, _maintenance, new MatterDeleter(_paths, _bin), _reporter,
               dispatch: a => a(), _time);

    /// <summary>Finalized v3 session folder fixture: session.json + meta.json + one JSONL
    /// segment. Deliberately does NOT render projections - cascade tests use the absence of
    /// session.txt as the no-cascade signal.</summary>
    private async Task WriteFinalizedSessionAsync(string id, IReadOnlyList<string> matterIds)
    {
        var started = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(10), DurationMs = 600_000,
            TimeZoneId = "UTC", UtcOffsetMinutes = 0,
            Model = "small.en", Backend = "cuda", Language = "en",
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "Fixture session", MatterIds = matterIds,
        }, CancellationToken.None);
        await new TranscriptStore(_paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1500, "hello there", "Me"),
            CancellationToken.None);
    }

    [Fact]
    public async Task Create_requires_a_name()
    {
        var vm = MakeVm();
        vm.NewMatterName = "   ";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        Assert.Empty(vm.Matters);
        Assert.Contains(_reporter.Infos,
            m => m.Contains("name is required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_mints_sequential_year_ids_and_stamps_DateCreatedUtc()
    {
        var vm = MakeVm();
        vm.NewMatterName = "First";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        vm.NewMatterName = "Second";
        await vm.CreateMatterCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "M-2026-001", "M-2026-002" },
            vm.Matters.Select(m => m.Id).ToArray());
        var first = await new MatterStore(_paths.MattersDir).LoadAsync("M-2026-001", CancellationToken.None);
        Assert.Equal(_time.GetUtcNow(), first!.DateCreatedUtc);
        Assert.Equal("M-2026-002", vm.SelectedMatterId);   // create selects the new matter
        Assert.True(vm.HasSelection);
        Assert.Equal("Second", vm.EditName);
        Assert.Equal("", vm.NewMatterName);
    }

    [Fact]
    public async Task Archived_matters_collapse_under_ShowArchived_toggle()
    {
        await _maintenance.SaveMatterAsync(new Matter { Id = "M-2026-001", Name = "Visible" }, CancellationToken.None);
        await _maintenance.SaveMatterAsync(new Matter { Id = "M-2026-002", Name = "Hidden", Archived = true }, CancellationToken.None);
        var vm = MakeVm();
        await vm.RefreshAsync();
        Assert.Equal("Visible", Assert.Single(vm.Matters).Name);   // default: archived collapsed
        vm.ShowArchived = true;
        Assert.Equal(2, vm.Matters.Count);
        vm.ShowArchived = false;
        Assert.Equal("Visible", Assert.Single(vm.Matters).Name);
    }

    [Fact]
    public async Task Tagged_sessions_sublist_and_jump_event()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Tagged";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-tagged", new[] { id });
        await WriteFinalizedSessionAsync("s-other", Array.Empty<string>());

        await vm.SelectAsync(id);
        var item = Assert.Single(vm.TaggedSessions);
        Assert.Equal("s-tagged", item.SessionId);
        Assert.Equal("Fixture session", item.Title);
        Assert.Equal("2026-07-01 09:00", item.DateDisplay);   // session-offset date (UTC+0 fixture)

        string? jumped = null;
        vm.JumpToSessionRequested += sid => jumped = sid;
        vm.JumpToSession(item.SessionId);
        Assert.Equal("s-tagged", jumped);
    }

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
