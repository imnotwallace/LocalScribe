using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.4 5.3 roll-out: the Sessions-page matter FILTER becomes searchable.
/// MatterFilterSearchText narrows MatterFilterOptions (Name + Reference + Id, OrdinalIgnoreCase);
/// single-select grid semantics (MatterFilterId -> ApplyFilters, null = all) are unchanged, the
/// sentinels stay offered, and the current selection is never dropped by a search.</summary>
public sealed class SessionsPageMatterFilterSearchTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-sessions-filter-search-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly MaintenanceService _maintenance;

    public SessionsPageMatterFilterSearchTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
        _maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

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

    private sealed class NoopReporter : IUiErrorReporter
    {
        public void Report(string context, Exception ex) { }
        public void Info(string message) { }
    }

    private SessionsPageViewModel MakeVm()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        return new SessionsPageViewModel(_maintenance, session, new WindowRegistry(),
            new NoopReporter(), dispatch: a => a(), TimeProvider.System, revealInExplorer: _ => { });
    }

    private Task SeedMatterAsync(string id, string? reference, string name)
        => _maintenance.SaveMatterAsync(new Matter { Id = id, Name = name, Reference = reference },
            CancellationToken.None);

    private async Task SeedSessionTaggedToAsync(string sessionId, params string[] matterIds)
    {
        Directory.CreateDirectory(_paths.SessionDir(sessionId));
        var started = new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(sessionId)).SaveAsync(new SessionRecord
        {
            Id = sessionId, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(10), DurationMs = 600_000,
            TimeZoneId = "UTC", UtcOffsetMinutes = 0,
            Model = "small", Backend = "cpu", Language = "en",
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(sessionId)).SaveAsync(new SessionMeta
        {
            Title = "Tagged session", MatterIds = matterIds,
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Filter_search_narrows_options_by_name_reference_and_id()
    {
        await SeedMatterAsync("M-20260705-001", "REF1", "Estate of Alpha");
        await SeedMatterAsync("M-20260705-002", null, "Beta Trust");
        await SeedSessionTaggedToAsync("s-1", "M-20260705-001");
        await SeedSessionTaggedToAsync("s-2", "M-20260705-002");
        var vm = MakeVm();
        await vm.OnNavigatedToAsync();
        Assert.Equal(4, vm.MatterFilterOptions.Count);        // All + No matter + 2 matters

        vm.MatterFilterSearchText = "alpha";                  // name
        Assert.Equal(new[] { "All matters", "No matter", "M-20260705-001-REF1 Estate of Alpha" },
            vm.MatterFilterOptions.Select(o => o.Label).ToArray());   // sentinels ALWAYS offered

        vm.MatterFilterSearchText = "ref1";                   // reference
        Assert.Contains(vm.MatterFilterOptions, o => o.Id == "M-20260705-001");
        Assert.DoesNotContain(vm.MatterFilterOptions, o => o.Id == "M-20260705-002");

        vm.MatterFilterSearchText = "0705-002";               // id fragment
        Assert.Contains(vm.MatterFilterOptions, o => o.Id == "M-20260705-002");
        Assert.DoesNotContain(vm.MatterFilterOptions, o => o.Id == "M-20260705-001");

        vm.MatterFilterSearchText = "";                       // cleared: full list back
        Assert.Equal(4, vm.MatterFilterOptions.Count);
    }

    [Fact]
    public async Task Selected_filter_survives_a_search_that_excludes_it()
    {
        await SeedMatterAsync("M-20260705-001", null, "Estate of Alpha");
        await SeedMatterAsync("M-20260705-002", null, "Beta Trust");
        await SeedSessionTaggedToAsync("s-1", "M-20260705-001");
        await SeedSessionTaggedToAsync("s-2", "M-20260705-002");
        var vm = MakeVm();
        await vm.OnNavigatedToAsync();

        vm.MatterFilterId = "M-20260705-001";                 // single-select grid filter (unchanged)
        Assert.Equal(new[] { "s-1" }, vm.Rows.Select(r => r.Id).ToArray());

        vm.MatterFilterSearchText = "zzz";                    // matches nothing
        Assert.Contains(vm.MatterFilterOptions, o => o.Id == "M-20260705-001");   // selected stays listed
        Assert.DoesNotContain(vm.MatterFilterOptions, o => o.Id == "M-20260705-002");
        Assert.Equal("M-20260705-001", vm.MatterFilterId);    // selection NOT reset by search
        Assert.Equal(new[] { "s-1" }, vm.Rows.Select(r => r.Id).ToArray());   // grid untouched by option search
    }
}
