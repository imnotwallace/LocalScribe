using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.3 Task 2: the Matter filter dropdown labels resolve id -> (ref, name) via a
/// matters-catalog lookup loaded on refresh, instead of showing the raw matter id.</summary>
public sealed class SessionsPageMatterLabelsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-sessions-matter-labels-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly MaintenanceService _maintenance;

    public SessionsPageMatterLabelsTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
        _maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // Local fakes, byte-identical to SessionsPageViewModelTests's so both test files compile
    // standalone whichever order they land in.
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

    private SessionsPageViewModel MakeVm()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        return new SessionsPageViewModel(_maintenance, session, new WindowRegistry(),
            new RecordingErrors(), dispatch: a => a(), TimeProvider.System, revealInExplorer: _ => { });
    }

    private sealed class RecordingErrors : IUiErrorReporter
    {
        public void Report(string context, Exception ex) { }
        public void Info(string message) { }
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
    public async Task Matter_filter_labels_use_id_ref_name()
    {
        await SeedMatterAsync(id: "M-20260705-001", reference: "REF1", name: "Test 1");
        await SeedMatterAsync(id: "M-20260705-002", reference: null, name: "No Ref Matter");
        await SeedSessionTaggedToAsync("s-tagged", "M-20260705-001", "M-20260705-002");
        var vm = MakeVm();

        await vm.OnNavigatedToAsync();

        var labels = vm.MatterFilterOptions.Select(o => o.Label).ToArray();
        Assert.Contains("M-20260705-001-REF1 Test 1", labels);
        Assert.Contains("M-20260705-002 No Ref Matter", labels);      // no ref -> id + name only
        Assert.Contains("All matters", labels);
        Assert.Contains("No matter", labels);
    }
}
