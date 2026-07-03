using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class DeleteFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-del-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    // Shared ordered event log: the WindowRegistry close action and the fake recycle bin both
    // append here, so a single list proves close-before-recycle ordering.
    private readonly List<string> _events = new();

    public DeleteFlowTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ---- doubles -------------------------------------------------------------------------

    private sealed class RecordingRecycleBin : IRecycleBin
    {
        private readonly List<string> _events;
        public RecordingRecycleBin(List<string> events) => _events = events;
        public void SendToRecycleBin(string path)
        {
            _events.Add("recycle:" + Path.GetFileName(path));
            // Emulate the shell move so a post-delete refresh over the REAL store sees it gone.
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FakeErrors : IUiErrorReporter
    {
        public readonly List<string> Infos = new();
        public readonly List<(string Context, Exception Ex)> Reports = new();
        public void Report(string context, Exception ex) => Reports.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }

    // Local fake for MaintenanceService's Task 9 ctor, byte-identical to Task 16's so both
    // test files compile standalone whichever task lands first.
    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }

    // ---- wiring --------------------------------------------------------------------------

    // Single construction point for the service under test (Task 9's locked ctor).
    private MaintenanceService CreateMaintenance(IRecycleBin bin)
        => new(_paths, new FakeSettings(new Settings()), bin, TimeProvider.System);

    private (SessionsPageViewModel Page, SessionViewModel Session, WindowRegistry Registry,
             FakeErrors Errors, MaintenanceService Maintenance) MakePage()
    {
        var maintenance = CreateMaintenance(new RecordingRecycleBin(_events));
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var registry = new WindowRegistry();
        var errors = new FakeErrors();
        var page = new SessionsPageViewModel(maintenance, session, registry, errors,
            dispatch: a => a(), time: TimeProvider.System, revealInExplorer: _ => { });
        return (page, session, registry, errors, maintenance);
    }

    // ---- fixtures (real stores over the temp root) ----------------------------------------

    private async Task<string> AddSessionAsync(string id, string title, DateTimeOffset? endedAtUtc)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 1, 2, 0, 0, TimeSpan.Zero),
            EndedAtUtc = endedAtUtc,
            TimeZoneId = "AUS Eastern Standard Time", UtcOffsetMinutes = 600,
            DurationMs = endedAtUtc is null ? 0 : 30 * 60 * 1000,
            Model = "small.en", Backend = "cpu",
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = title }, CancellationToken.None);
        return id;
    }

    private Task AddMatterAsync(string id, string name, string? reference)
        => new MatterStore(_paths.MattersDir).SaveAsync(new Matter
        {
            Id = id, Name = name, Reference = reference,
            DateCreatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

    private async Task<int> MatterCountAsync(string matterId)
        => (await new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None))
            .Matters.Single(m => m.Id == matterId).SessionCount;

    // ---- tests ---------------------------------------------------------------------------

    [Fact]
    public async Task Confirmed_delete_closes_windows_first_then_recycles_then_refreshes()
    {
        await AddMatterAsync("M-2026-001", "Smith v Jones", "REF-1");
        string id = await AddSessionAsync("s-one", "Webex call one",
            endedAtUtc: new DateTimeOffset(2026, 7, 1, 2, 30, 0, TimeSpan.Zero));
        var (page, _, registry, errors, maintenance) = MakePage();

        // Tag through the real service so matters.json sessionCount becomes 1 first.
        var meta = (await new MetadataStore(_paths.MetaJson(id)).LoadAsync(CancellationToken.None))!;
        await maintenance.SaveMetaAsync(id, meta with { MatterIds = ["M-2026-001"] },
            previousMatterIds: [], CancellationToken.None);
        Assert.Equal(1, await MatterCountAsync("M-2026-001"));

        registry.Register(id, () => _events.Add("close:" + id));   // an "open read view"
        DeleteConfirmation? payload = null;
        page.ConfirmDeleteRequested += (p, confirm) => { payload = p; confirm(); };  // scripted auto-confirm

        await page.OnNavigatedToAsync();
        var row = page.Rows.Single(r => r.Id == id);
        await page.DeleteSessionCommand.ExecuteAsync(row);

        // Order is the contract: read views closed BEFORE the recycle call (design 3.4).
        Assert.Equal(new[] { "close:" + id, "recycle:" + id }, _events);
        Assert.NotNull(payload);
        Assert.Equal("Webex call one", payload!.Title);
        Assert.Equal(row.DateDisplay, payload.DateDisplay);
        Assert.Equal(row.DurationDisplay, payload.DurationDisplay);
        Assert.Equal(new[] { "Smith v Jones (REF-1)" }, payload.MatterNames);
        Assert.False(Directory.Exists(_paths.SessionDir(id)));
        Assert.DoesNotContain(page.Rows, r => r.Id == id);          // refreshed
        Assert.Equal(0, await MatterCountAsync("M-2026-001"));      // decremented on disk
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Declined_confirmation_deletes_nothing()
    {
        string id = await AddSessionAsync("s-keep", "Keep me",
            endedAtUtc: new DateTimeOffset(2026, 7, 1, 2, 30, 0, TimeSpan.Zero));
        var (page, _, _, _, _) = MakePage();
        page.ConfirmDeleteRequested += (_, _) => { };               // user pressed Cancel/No

        await page.OnNavigatedToAsync();
        await page.DeleteSessionCommand.ExecuteAsync(page.Rows.Single(r => r.Id == id));

        Assert.Empty(_events);
        Assert.True(Directory.Exists(_paths.SessionDir(id)));
        Assert.Contains(page.Rows, r => r.Id == id);
    }

    [Fact]
    public async Task Live_session_delete_is_refused_and_folder_untouched()
    {
        var (page, session, _, errors, _) = MakePage();
        bool asked = false;
        page.ConfirmDeleteRequested += (_, confirm) => { asked = true; confirm(); };

        await session.StartCommand.ExecuteAsync(null);              // real controller over fakes
        try
        {
            string liveId = session.CurrentSessionId!;
            await page.OnNavigatedToAsync();                        // live folder is on disk already
            var row = page.Rows.Single(r => r.Id == liveId);

            await page.DeleteSessionCommand.ExecuteAsync(row);

            Assert.False(asked);                                    // never even asked
            Assert.Empty(_events);                                  // no close, no recycle
            Assert.True(Directory.Exists(_paths.SessionDir(liveId)));
            Assert.Contains(errors.Infos,
                m => m.Contains("recording", StringComparison.OrdinalIgnoreCase));
        }
        finally { await session.StopCommand.ExecuteAsync(null); }
    }

    [Fact]
    public async Task Pending_recovery_delete_is_refused_and_folder_untouched()
    {
        string id = await AddSessionAsync("s-interrupted", "Interrupted", endedAtUtc: null);
        var (page, _, _, errors, _) = MakePage();
        bool asked = false;
        page.ConfirmDeleteRequested += (_, confirm) => { asked = true; confirm(); };

        await page.OnNavigatedToAsync();
        var row = page.Rows.Single(r => r.Id == id);
        Assert.True(row.IsPendingRecovery);

        await page.DeleteSessionCommand.ExecuteAsync(row);

        Assert.False(asked);
        Assert.Empty(_events);
        Assert.True(Directory.Exists(_paths.SessionDir(id)));
        Assert.Contains(errors.Infos,
            m => m.Contains("recover", StringComparison.OrdinalIgnoreCase));
    }
}
