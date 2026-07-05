using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SessionsPageViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-sessions-page-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public SessionsPageViewModelTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class RecordingErrors : IUiErrorReporter
    {
        public List<string> Reports { get; } = [];
        public void Report(string context, Exception ex) => Reports.Add(context + ": " + ex.Message);
        public void Info(string message) { }
    }

    // Local fakes for MaintenanceService's Task 9 ctor, byte-identical to Task 16's so both
    // test files compile standalone whichever task lands first.
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

    /// <summary>Single construction point: real MaintenanceService over a temp root, real
    /// SessionViewModel over the live test doubles, synchronous dispatch.</summary>
    private (SessionsPageViewModel Vm, SessionViewModel Session, RecordingErrors Errors, List<string> Revealed) MakeVm()
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var errors = new RecordingErrors();
        var revealed = new List<string>();
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(), errors,
            dispatch: a => a(), TimeProvider.System, revealInExplorer: revealed.Add);
        return (vm, session, errors, revealed);
    }

    private static SessionRecord Rec(string id, DateTimeOffset startedUtc, int? offsetMin,
        long durationMs = 60_000, bool ended = true, bool recovered = false, bool diarised = false,
        RemoteMode remoteMode = RemoteMode.PerProcess, bool fellBack = false, AppKind app = AppKind.Webex)
        => new()
        {
            Id = id, App = app, StartedAtUtc = startedUtc,
            EndedAtUtc = ended ? startedUtc.AddMilliseconds(durationMs) : null,
            TimeZoneId = offsetMin is null ? null : "Singapore Standard Time",
            UtcOffsetMinutes = offsetMin, DurationMs = durationMs,
            Model = "small", Backend = "cpu", Language = "en",
            Diarised = diarised, SegmentCount = 1, Recovered = recovered,
            Devices = new DeviceSnapshot
            { Remote = new RemoteSnapshot { Mode = remoteMode, FellBackToSystemMix = fellBack } },
        };

    private static SessionMeta Meta(string title, bool archived = false, bool edited = false,
        Medium medium = Medium.Webex, params string[] matterIds)
        => new() { Title = title, Medium = medium, MatterIds = matterIds, Archived = archived, Edited = edited };

    private async Task WriteSessionAsync(SessionRecord session, SessionMeta meta)
    {
        Directory.CreateDirectory(_paths.SessionDir(session.Id));
        await new SessionStore(_paths.SessionJson(session.Id)).SaveAsync(session, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(session.Id)).SaveAsync(meta, CancellationToken.None);
    }

    [Fact]
    public async Task Load_orders_newest_first_and_maps_display_fields()
    {
        // s-old: stored +480 offset (session-local, NOT machine zone); 754 s -> mm:ss.
        var oldStart = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-old", oldStart, offsetMin: 480, durationMs: 754_000),
            Meta("Client call", medium: Medium.Webex));
        // s-new: pre-v3 null offset -> machine-local fallback; 3725 s -> h:mm:ss.
        var newStart = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-new", newStart, offsetMin: null, durationMs: 3_725_000, app: AppKind.Manual),
            Meta("Phone conference", medium: Medium.Phone));

        var (vm, _, errors, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        Assert.Empty(errors.Reports);
        Assert.Equal(new[] { "s-new", "s-old" }, vm.Rows.Select(r => r.Id).ToArray());

        var oldRow = vm.Rows.Single(r => r.Id == "s-old");
        Assert.Equal("2026-06-01 10:00", oldRow.DateDisplay);           // 02:00Z + 8 h
        Assert.Equal("12:34", oldRow.DurationDisplay);
        Assert.Equal("Webex", oldRow.AppMedium);                        // app == medium collapses

        var newRow = vm.Rows.Single(r => r.Id == "s-new");
        string machineLocal = newStart.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(machineLocal, newRow.DateDisplay);                 // null offset -> ToLocalTime
        Assert.Equal("1:02:05", newRow.DurationDisplay);
        Assert.Equal("Manual / Phone", newRow.AppMedium);
    }

    [Fact]
    public async Task Pending_recovery_row_has_blank_duration_and_flag()
    {
        await WriteSessionAsync(
            Rec("s-crash", new DateTimeOffset(2026, 6, 3, 4, 0, 0, TimeSpan.Zero), 480, ended: false),
            Meta("Interrupted"));
        var (vm, _, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        var row = vm.Rows.Single();
        Assert.True(row.IsPendingRecovery);
        Assert.Equal("", row.DurationDisplay);
    }

    [Fact]
    public async Task Filters_recompute_rows_from_cached_list()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-1", t, 480), Meta("Client call about the merger", matterIds: "M-2026-001"));
        await WriteSessionAsync(Rec("s-2", t.AddHours(1), 480), Meta("Webex with counsel"));
        await WriteSessionAsync(Rec("s-3", t.AddHours(2), 480), Meta("Old archived brief", archived: true));

        var (vm, _, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();
        Assert.Equal(2, vm.Rows.Count);                                 // archived hidden by default

        vm.FilterText = "MERGER";                                       // case-insensitive contains
        Assert.Equal("s-1", vm.Rows.Single().Id);
        vm.FilterText = "";
        Assert.Equal(2, vm.Rows.Count);

        vm.MatterFilterId = "M-2026-001";
        Assert.Equal("s-1", vm.Rows.Single().Id);
        vm.MatterFilterId = SessionsPageViewModel.NoMatterSentinel;     // empty MatterIds only
        Assert.Equal("s-2", vm.Rows.Single().Id);
        vm.MatterFilterId = null;

        vm.SelectedRow = vm.Rows.Single(r => r.Id == "s-2");
        vm.ShowArchived = true;
        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("s-2", vm.SelectedRow?.Id);                        // selection survives rebuild

        Assert.Equal(new string?[] { null, SessionsPageViewModel.NoMatterSentinel, "M-2026-001" },
            vm.MatterFilterOptions.Select(o => o.Id).ToArray());
    }

    [Fact]
    public async Task Badge_mapping_covers_chosen_and_fallback_system_mix()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-chosen", t, 480, remoteMode: RemoteMode.SystemMix), Meta("A"));
        await WriteSessionAsync(Rec("s-fallback", t.AddHours(1), 480, fellBack: true), Meta("B"));
        await WriteSessionAsync(Rec("s-clean", t.AddHours(2), 480, recovered: true, diarised: true),
            Meta("C", edited: true));

        var (vm, _, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        Assert.True(vm.Rows.Single(r => r.Id == "s-chosen").IsSystemMix);   // chosen mode counts (3.2)
        Assert.True(vm.Rows.Single(r => r.Id == "s-fallback").IsSystemMix); // degraded fallback counts
        var clean = vm.Rows.Single(r => r.Id == "s-clean");
        Assert.False(clean.IsSystemMix);
        Assert.True(clean.IsRecovered);
        Assert.True(clean.IsEdited);
        Assert.True(clean.IsDiarised);
    }

    [Fact]
    public async Task State_reaching_idle_triggers_refresh()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-first", t, 480), Meta("First"));
        var (vm, session, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();
        Assert.Single(vm.Rows);

        await WriteSessionAsync(Rec("s-second", t.AddHours(1), 480), Meta("Second"));
        session.State = SessionState.Recording;    // simulate controller-driven transitions
        session.State = SessionState.Idle;         // finalize just happened -> refresh (3.1)

        SpinWait.SpinUntil(() => vm.Rows.Count == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task Archive_toggle_saves_meta_without_flipping_edited()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-arch", t, 480), Meta("To archive", matterIds: "M-2026-001"));
        var (vm, _, errors, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        await vm.ToggleArchiveCommand.ExecuteAsync(vm.Rows.Single());
        Assert.Empty(errors.Reports);
        Assert.Empty(vm.Rows);                                          // archived + ShowArchived off

        var onDisk = await new MetadataStore(_paths.MetaJson("s-arch")).LoadAsync(CancellationToken.None);
        Assert.NotNull(onDisk);
        Assert.True(onDisk!.Archived);
        Assert.False(onDisk.Edited);                                    // metadata saves never flip these
        Assert.Null(onDisk.LastEditedAtUtc);
        Assert.Equal(new[] { "M-2026-001" }, onDisk.MatterIds);         // tags untouched

        vm.ShowArchived = true;
        Assert.True(vm.Rows.Single().IsArchived);
    }

    [Fact]
    public async Task Reveal_and_open_read_view_row_actions()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-ok", t, 480), Meta("Done"));
        await WriteSessionAsync(Rec("s-pending", t.AddHours(1), 480, ended: false), Meta("Pending"));
        var (vm, _, _, revealed) = MakeVm();
        await vm.OnNavigatedToAsync();

        var opened = new List<string>();
        vm.OpenReadViewRequested += opened.Add;

        vm.RevealInExplorerCommand.Execute(vm.Rows.Single(r => r.Id == "s-ok"));
        Assert.Equal(new[] { "s-ok" }, revealed);                       // delegate gets the session id

        vm.OpenReadViewCommand.Execute(vm.Rows.Single(r => r.Id == "s-ok"));
        vm.OpenReadViewCommand.Execute(vm.Rows.Single(r => r.Id == "s-pending"));  // inert (3.1)
        Assert.Equal(new[] { "s-ok" }, opened);
    }

    [Fact]
    public async Task OpenSessionDetailsCommand_raises_request_with_row_id()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-1", t, 480), Meta("Details target"));
        var (vm, _, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        string? asked = null;
        vm.OpenSessionDetailsRequested += id => asked = id;
        var row = vm.Rows.Single(r => r.Id == "s-1");
        vm.OpenSessionDetailsCommand.Execute(row);
        Assert.Equal("s-1", asked);
    }

    [Fact]
    public async Task OpenSessionDetailsCommand_allows_pending_recovery_rows()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-pending-detail", t, 480, ended: false), Meta("Pending"));
        var (vm, _, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        string? asked = null;
        vm.OpenSessionDetailsRequested += id => asked = id;
        var row = vm.Rows.Single(r => r.Id == "s-pending-detail");
        Assert.True(row.IsPendingRecovery);
        vm.OpenSessionDetailsCommand.Execute(row);
        // Unlike OpenReadView, details editing is allowed for any row - the editor's own
        // IsEditable gate handles the in-progress/locked case.
        Assert.Equal("s-pending-detail", asked);
    }

    [Fact]
    public async Task Unreadable_folders_surface_in_footer_count()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-good", t, 480), Meta("Good"));
        string junk = Path.Combine(_paths.SessionsDir, "not-a-session");
        Directory.CreateDirectory(junk);
        File.WriteAllText(Path.Combine(junk, "stray.txt"), "no session.json here");

        var (vm, _, errors, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        Assert.Single(vm.Rows);
        Assert.Equal(1, vm.UnreadableCount);
        Assert.Empty(errors.Reports);                                   // counted, not error-reported
    }

    // Task 6: HasSelection gates the action-bar buttons (IsEnabled binding). It must be false with
    // no selection, flip true when a row is selected, and raise PropertyChanged both ways so the
    // bound IsEnabled refreshes.
    [Fact]
    public async Task HasSelection_reflects_selection_and_notifies_both_ways()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-1", t, 480), Meta("Selectable"));
        var (vm, _, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        Assert.False(vm.HasSelection);                                  // nothing selected after load

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedRow = vm.Rows.Single();                             // false -> true
        Assert.True(vm.HasSelection);
        Assert.Contains(nameof(SessionsPageViewModel.HasSelection), raised);

        raised.Clear();
        vm.SelectedRow = null;                                         // true -> false
        Assert.False(vm.HasSelection);
        Assert.Contains(nameof(SessionsPageViewModel.HasSelection), raised);
    }
}
