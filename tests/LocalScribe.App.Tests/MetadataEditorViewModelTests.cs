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

    private MetadataEditorViewModel MakeEditor()
        => new(_maintenance, _session, _reporter, dispatch: a => a(), _time);

    /// <summary>Rows are minted through Task 15's LOCKED surface (ctor + OnNavigatedToAsync +
    /// Rows) so this file never guesses SessionRowViewModel's own ctor.</summary>
    private async Task<SessionRowViewModel> RowAsync(string id)
    {
        var page = new SessionsPageViewModel(_maintenance, _session, new WindowRegistry(),
            _reporter, dispatch: a => a(), _time, revealInExplorer: _ => { });
        await page.OnNavigatedToAsync();
        return page.Rows.Single(r => r.Id == id);
    }

    private async Task WriteSessionAsync(string id, string title,
        IReadOnlyList<string>? matterIds = null, bool ended = true)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        var started = new DateTimeOffset(2026, 7, 2, 1, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = ended ? started.AddMinutes(30) : null,
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = ended ? 1_800_000 : 0,
            Model = "small.en", Backend = "cuda", Language = "en",
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        { Title = title, MatterIds = matterIds ?? [] }, CancellationToken.None);
    }

    private async Task WriteMatterAsync(string id, string name, bool archived = false,
        IReadOnlyList<RosterMember>? roster = null)
        => await _maintenance.SaveMatterAsync(new Matter
        {
            Id = id, Name = name, Archived = archived, Roster = roster ?? [],
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

    // NOTE (deviation from brief, test-file-only): reads via _maintenance.ListMattersAsync
    // (gated) instead of a bare `new MatterStore(...)` - a raw unguarded read here raced
    // AtomicFile's write-then-move on matters.json (Windows can throw a sharing/access error
    // on the writer's File.Move when a reader has the destination open at that instant), which
    // is exactly the hazard MaintenanceService.ListMattersAsync's _indexGate now closes.
    private int SessionCount(string matterId)
        => _maintenance.ListMattersAsync(CancellationToken.None)
           .GetAwaiter().GetResult().Matters.Single(m => m.Id == matterId).SessionCount;

    [Fact]
    public async Task Title_commit_autosaves_meta_v2_and_regenerates_session_txt()
    {
        await WriteSessionAsync("s-title", "Old title");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-title"));
        Assert.True(ed.IsEditable);

        ed.Title = "Estate call - corrected";
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));

        string raw = await File.ReadAllTextAsync(_paths.MetaJson("s-title"));
        Assert.Contains("\"schemaVersion\": 2", raw);
        Assert.Contains("Estate call - corrected", raw);
        // SaveMetaAsync regenerates projections before returning (Task 9), and the
        // indicator only lights after it returns - session.txt is already fresh here.
        Assert.Contains("Estate call - corrected",
            await File.ReadAllTextAsync(_paths.SessionTxt("s-title")));
    }

    [Fact]
    public async Task Metadata_save_never_flips_Edited_or_LastEditedAtUtc()
    {
        await WriteSessionAsync("s-flags", "Before");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-flags"));

        ed.Title = "After";
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));

        var back = await new MetadataStore(_paths.MetaJson("s-flags")).LoadAsync(CancellationToken.None);
        Assert.Equal("After", back!.Title);
        Assert.False(back.Edited);                          // design 3.3: EditStore stays the flags' only writer
        Assert.Null(back.LastEditedAtUtc);
        string raw = await File.ReadAllTextAsync(_paths.MetaJson("s-flags"));
        Assert.Contains("\"edited\": false", raw);
        Assert.DoesNotContain("lastEditedAtUtc", raw);      // null is omitted (WhenWritingNull)
    }

    [Fact]
    public async Task Tag_toggle_moves_matter_session_counts_both_ways()
    {
        await WriteSessionAsync("s-tags", "Tagged");
        await WriteMatterAsync("M-2026-001", "Estate of Alpha");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-tags"));
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 1, TimeSpan.FromSeconds(10)));

        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single(o => o.Id == "M-2026-001"));
        Assert.True(SpinWait.SpinUntil(() => SessionCount("M-2026-001") == 1, TimeSpan.FromSeconds(10)));
        var meta = await new MetadataStore(_paths.MetaJson("s-tags")).LoadAsync(CancellationToken.None);
        Assert.Equal(["M-2026-001"], meta!.MatterIds);

        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single(o => o.Id == "M-2026-001"));
        Assert.True(SpinWait.SpinUntil(() => SessionCount("M-2026-001") == 0, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Archived_matters_are_offered_only_when_ShowArchivedMatters()
    {
        await WriteSessionAsync("s-arch", "S");
        await WriteMatterAsync("M-2026-001", "Active");
        await WriteMatterAsync("M-2026-002", "Old", archived: true);
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-arch"));
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 1, TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(ed.MatterOptions, o => o.Id == "M-2026-002");

        ed.ShowArchivedMatters = true;                      // display-only rebuild, never a save
        Assert.Equal(2, ed.MatterOptions.Count);
        Assert.Contains(ed.MatterOptions, o => o.Id == "M-2026-002" && o.Archived);
    }

    [Fact]
    public async Task Roster_pick_copies_id_and_name_into_the_snapshot()
    {
        await WriteMatterAsync("M-2026-001", "Estate of Alpha", roster:
            [new RosterMember { Id = "p-alice-client", Name = "Alice Client", Role = "Client" }]);
        await WriteSessionAsync("s-roster", "S", matterIds: ["M-2026-001"]);
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-roster"));
        Assert.True(SpinWait.SpinUntil(() => ed.RosterPicks.Count == 1, TimeSpan.FromSeconds(10)));

        await ed.AddFromRosterAsync("M-2026-001", "p-alice-client");
        var p = Assert.Single(ed.Participants);
        Assert.Equal("p-alice-client", p.Id);               // COPIED, provenance only
        Assert.Equal("Alice Client", p.Name);
        Assert.Equal("Client", p.Role);
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));
        var meta = await new MetadataStore(_paths.MetaJson("s-roster")).LoadAsync(CancellationToken.None);
        var saved = Assert.Single(meta!.Participants);
        Assert.Equal(("p-alice-client", "Alice Client"), (saved.Id, saved.Name));
    }

    [Fact]
    public async Task Free_text_mints_session_scoped_p_slug_with_collision_suffix()
    {
        await WriteSessionAsync("s-free", "S");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-free"));

        ed.AddFreeText("Bob Witness", SourceKind.Remote);
        ed.AddFreeText("Bob Witness", SourceKind.Local);    // collides within THIS session's ids
        Assert.Equal("p-bob-witness", ed.Participants[0].Id);
        Assert.Equal("p-bob-witness-2", ed.Participants[1].Id);
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));
        var meta = await new MetadataStore(_paths.MetaJson("s-free")).LoadAsync(CancellationToken.None);
        Assert.Equal(2, meta!.Participants.Count);
        Assert.Equal(SourceKind.Local, meta.Participants[1].Side);
    }

    [Fact]
    public async Task Add_to_roster_and_session_writes_the_matter_and_the_snapshot()
    {
        // Existing member forces the mint to collide in the MATTER's scope (design 4.2),
        // proving the id was minted against roster ids, not the session's participant ids.
        await WriteMatterAsync("M-2026-001", "Estate of Alpha", roster:
            [new RosterMember { Id = "p-dan-expert", Name = "Dan Expert" }]);
        await WriteSessionAsync("s-add", "S", matterIds: ["M-2026-001"]);
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-add"));

        await ed.AddToRosterAndSessionAsync("M-2026-001", "Dan Expert", SourceKind.Remote);

        var matter = await new MatterStore(_paths.MattersDir).LoadAsync("M-2026-001", CancellationToken.None);
        Assert.Equal(2, matter!.Roster.Count);
        Assert.Contains(matter.Roster, m => m.Id == "p-dan-expert-2" && m.Name == "Dan Expert");
        var p = Assert.Single(ed.Participants);
        Assert.Equal("p-dan-expert-2", p.Id);
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));
        var meta = await new MetadataStore(_paths.MetaJson("s-add")).LoadAsync(CancellationToken.None);
        Assert.Equal("p-dan-expert-2", Assert.Single(meta!.Participants).Id);
    }

    [Fact]
    public async Task Pending_recovery_row_is_locked_and_saves_nothing()
    {
        await WriteSessionAsync("s-open", "Open", ended: false);   // endedAtUtc null (design 3.1)
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-open"));

        Assert.False(ed.IsEditable);
        Assert.Equal("Available after recovery completes.", ed.LockHint);
        ed.Title = "must not persist";
        var meta = await new MetadataStore(_paths.MetaJson("s-open")).LoadAsync(CancellationToken.None);
        Assert.Equal("Open", meta!.Title);                  // gated save never ran
        Assert.False(ed.SavedIndicator);
        Assert.Empty(_reporter.Errors);
    }

    [Fact]
    public async Task Live_session_is_locked_while_recording_but_other_rows_stay_editable()
    {
        await WriteSessionAsync("s-other", "Other finalized");
        await _session.StartCommand.ExecuteAsync(null);     // real fake-backed recording
        string liveId = _session.CurrentSessionId!;
        var ed = MakeEditor();

        ed.Attach(await RowAsync(liveId));
        Assert.False(ed.IsEditable);                        // design 3.3: live session locked

        ed.Attach(await RowAsync("s-other"));               // ...but only THAT session
        Assert.True(ed.IsEditable);

        await _session.StopCommand.ExecuteAsync(null);      // finalize -> endedAtUtc set
        ed.Attach(await RowAsync(liveId));                  // fresh row after finalize
        Assert.True(ed.IsEditable);
    }

    [Fact]
    public async Task SavedIndicator_clears_two_seconds_later_via_Tick()
    {
        await WriteSessionAsync("s-tick", "T");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-tick"));
        ed.Title = "T2";
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));

        _time.Set(new DateTimeOffset(2026, 7, 3, 9, 0, 1, 900, TimeSpan.Zero));
        ed.Tick();
        Assert.True(ed.SavedIndicator);                     // 1.9s: still showing
        _time.Set(new DateTimeOffset(2026, 7, 3, 9, 0, 2, 0, TimeSpan.Zero));
        ed.Tick();
        Assert.False(ed.SavedIndicator);                    // exactly 2s: cleared
    }

    [Fact]
    public async Task Failed_save_reports_and_reverts_the_editor_copy()
    {
        await WriteSessionAsync("s-fail", "Original");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-fail"));

        File.Delete(_paths.MetaJson("s-fail"));
        Directory.CreateDirectory(_paths.MetaJson("s-fail"));   // meta.json is now a DIRECTORY: write throws

        ed.Title = "Doomed";
        Assert.True(SpinWait.SpinUntil(() => _reporter.Errors.Count > 0, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => ed.Title == "Original", TimeSpan.FromSeconds(10)));
        Assert.False(ed.SavedIndicator);
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
