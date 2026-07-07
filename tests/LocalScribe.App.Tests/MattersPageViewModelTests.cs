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

    private MattersPageViewModel MakeVm(WindowRegistry? windows = null,
        Func<SavePathRequest, string?>? pickSavePath = null, Action<string>? revealFile = null)
        => new(_maintenance, new MatterDeleter(_paths, _bin), windows ?? new WindowRegistry(),
            _reporter, pickSavePath ?? (_ => null), revealFile ?? (_ => { }), dispatch: a => a());

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
    public async Task Create_mints_sequential_day_ids_and_stamps_DateCreatedUtc()
    {
        var vm = MakeVm();
        vm.NewMatterName = "First";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        vm.NewMatterName = "Second";
        await vm.CreateMatterCommand.ExecuteAsync(null);

        // Per-day matter ids (M-yyyyMMdd-NNN): _time is fixed at 2026-07-03.
        Assert.Equal(new[] { "M-20260703-001", "M-20260703-002" },
            vm.Matters.Select(m => m.Id).ToArray());
        var first = await new MatterStore(_paths.MattersDir).LoadAsync("M-20260703-001", CancellationToken.None);
        Assert.Equal(_time.GetUtcNow(), first!.DateCreatedUtc);
        Assert.Equal("M-20260703-002", vm.SelectedMatterId);   // create selects the new matter
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
    public async Task Tagged_sessions_sublist_reflects_matter_tags()
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
    }

    [Fact]
    public void OpenTaggedSession_raises_OpenSessionDetailsRequested()
    {
        var vm = MakeVm();
        string? asked = null;
        vm.OpenSessionDetailsRequested += id => asked = id;
        vm.JumpToSession("sess-9");                         // the tagged-session "Open" entry point
        Assert.Equal("sess-9", asked);
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

    [Fact]
    public async Task Commit_name_change_cascades_projections_after_save()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Old Name";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-cascade", new[] { id });
        Assert.False(File.Exists(_paths.SessionTxt("s-cascade")));   // pre-condition: no render yet

        await vm.SelectAsync(id);
        vm.EditName = "New Name";
        await vm.CommitDetailCommand.ExecuteAsync(null);

        Assert.Empty(_reporter.Errors);
        var reloaded = await new MatterStore(_paths.MattersDir).LoadAsync(id, CancellationToken.None);
        Assert.Equal("New Name", reloaded!.Name);                    // truth saved
        string sessionTxt = await File.ReadAllTextAsync(_paths.SessionTxt("s-cascade"));
        Assert.Contains("New Name", sessionTxt);                     // cascade re-rendered live name
        Assert.Equal("", vm.CascadeStatus);                          // status cleared when done
    }

    [Fact]
    public async Task Commit_reference_change_cascades_too()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Refd";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-ref-cascade", new[] { id });

        await vm.SelectAsync(id);
        vm.EditReference = "REF-42";
        await vm.CommitDetailCommand.ExecuteAsync(null);

        Assert.Empty(_reporter.Errors);
        string sessionTxt = await File.ReadAllTextAsync(_paths.SessionTxt("s-ref-cascade"));
        Assert.Contains("Refd (REF-42)", sessionTxt);
    }

    [Fact]
    public async Task Description_and_archived_commits_save_but_do_not_cascade()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Quiet";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-quiet", new[] { id });

        await vm.SelectAsync(id);
        vm.EditDescription = "Background notes";
        await vm.CommitDetailCommand.ExecuteAsync(null);
        vm.EditArchived = true;
        await vm.CommitDetailCommand.ExecuteAsync(null);

        Assert.Empty(_reporter.Errors);
        Assert.False(File.Exists(_paths.SessionTxt("s-quiet")));     // a cascade would have created it
        var reloaded = await new MatterStore(_paths.MattersDir).LoadAsync(id, CancellationToken.None);
        Assert.Equal("Background notes", reloaded!.Description);
        Assert.True(reloaded.Archived);
    }

    [Fact]
    public async Task Adding_a_matter_term_persists_and_does_not_cascade()
    {
        var matter = await _maintenance.CreateMatterAsync("Doe v. State", CancellationToken.None);
        await WriteFinalizedSessionAsync("s-tagged", new[] { matter.Id });   // tagged, no projection on disk
        var vm = MakeVm();
        await vm.RefreshAsync();
        await vm.SelectAsync(matter.Id);

        vm.Vocabulary.NewTerm = "arraignment";
        await vm.Vocabulary.AddTermCommand.ExecuteAsync(null);

        var reloaded = await new MatterStore(_paths.MattersDir).LoadAsync(matter.Id, CancellationToken.None);
        Assert.Contains("arraignment", reloaded!.Vocabulary.Terms);
        // No cascade on a vocab edit: the tagged session's projection was never rendered.
        Assert.False(File.Exists(_paths.SessionTxt("s-tagged")));
    }

    [Fact]
    public async Task Rerender_tagged_regenerates_the_tagged_sessions_projections()
    {
        var matter = await _maintenance.CreateMatterAsync("Doe v. State", CancellationToken.None);
        await WriteFinalizedSessionAsync("s-tagged", new[] { matter.Id });
        var vm = MakeVm();
        await vm.RefreshAsync();
        await vm.SelectAsync(matter.Id);

        await vm.RerenderTaggedAsync();

        Assert.True(File.Exists(_paths.SessionTxt("s-tagged")));   // cascade rendered it
        Assert.Equal("", vm.CascadeStatus);                        // status cleared at the end
    }

    [Fact]
    public async Task Commit_with_blank_name_is_rejected_and_reverted()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Keep Me";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;

        await vm.SelectAsync(id);
        vm.EditName = "  ";
        await vm.CommitDetailCommand.ExecuteAsync(null);

        Assert.Contains(_reporter.Infos, m => m.Contains("name is required", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Keep Me", vm.EditName);                        // reverted to loaded truth
        var reloaded = await new MatterStore(_paths.MattersDir).LoadAsync(id, CancellationToken.None);
        Assert.Equal("Keep Me", reloaded!.Name);
    }

    [Fact]
    public async Task Roster_add_mints_participant_ids_with_collision_suffix()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Roster";
        await vm.CreateMatterCommand.ExecuteAsync(null);

        vm.NewMemberName = "Jane Doe";
        vm.NewMemberRole = "Counsel";
        await vm.AddMemberCommand.ExecuteAsync(null);
        vm.NewMemberName = "Jane Doe";                               // same name -> "-2" suffix
        await vm.AddMemberCommand.ExecuteAsync(null);

        Assert.Empty(_reporter.Errors);
        Assert.Equal(new[] { "p-jane-doe", "p-jane-doe-2" }, vm.Roster.Select(m => m.Id).ToArray());
        Assert.Equal("Counsel", vm.Roster[0].Role);
        Assert.Equal("", vm.NewMemberName);                          // input cleared after add
        var reloaded = await new MatterStore(_paths.MattersDir)
            .LoadAsync(vm.SelectedMatterId!, CancellationToken.None);
        Assert.Equal(2, reloaded!.Roster.Count);                     // persisted through maintenance save
    }

    [Fact]
    public async Task Roster_edits_never_cascade_and_never_touch_sessions()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Estate of Doe";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-roster", new[] { id });
        byte[] metaBefore = await File.ReadAllBytesAsync(_paths.MetaJson("s-roster"));

        await vm.SelectAsync(id);
        vm.NewMemberName = "Jane Doe";
        await vm.AddMemberCommand.ExecuteAsync(null);
        await vm.RenameMemberAsync("p-jane-doe", "Jane Q. Doe");
        Assert.Equal("Jane Q. Doe", Assert.Single(vm.Roster).Name);
        await vm.RemoveMemberAsync("p-jane-doe");
        Assert.Empty(vm.Roster);

        Assert.Empty(_reporter.Errors);
        // No cascade: session.txt was never rendered, and roster edits must not render it.
        Assert.False(File.Exists(_paths.SessionTxt("s-roster")));
        // Session truth untouched: participants are snapshot copies (design 4.4).
        Assert.Equal(metaBefore, await File.ReadAllBytesAsync(_paths.MetaJson("s-roster")));
        var reloaded = await new MatterStore(_paths.MattersDir).LoadAsync(id, CancellationToken.None);
        Assert.Empty(reloaded!.Roster);
    }

    [Fact]
    public async Task Delete_blocked_while_sessions_reference_the_matter()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Referenced";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-refd", new[] { id });

        await vm.SelectAsync(id);
        await vm.DeleteMatterCommand.ExecuteAsync(null);

        Assert.Contains(_reporter.Infos, m => m.Contains("1 session") && m.Contains("Archive"));
        Assert.True(File.Exists(_paths.MatterJson(id)));             // nothing deleted
        Assert.Empty(_bin.Recycled);
        Assert.Empty(_reporter.Errors);                              // blocked is Info, not an error
    }

    [Fact]
    public async Task Delete_empty_matter_recycles_folder_and_refreshes()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Empty";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;

        await vm.SelectAsync(id);
        await vm.DeleteMatterCommand.ExecuteAsync(null);

        Assert.Empty(_reporter.Errors);
        Assert.Contains(Path.Combine(_paths.MattersDir, id), _bin.Recycled);
        Assert.Empty(vm.Matters);                                    // index entry gone + list refreshed
        Assert.False(vm.HasSelection);
    }

    [Fact]
    public async Task Repair_index_adopts_orphan_matter_folders()
    {
        // matter.json on disk, no index entry (the documented crash window; MatterStore.cs:5-7).
        await new MatterStore(_paths.MattersDir).SaveAsync(
            new Matter { Id = "M-2026-009", Name = "Orphan" }, CancellationToken.None);
        File.Delete(_paths.MattersIndexJson);

        var vm = MakeVm();
        await vm.RefreshAsync();
        Assert.Empty(vm.Matters);
        await vm.RepairIndexCommand.ExecuteAsync(null);
        Assert.Contains(vm.Matters, m => m.Id == "M-2026-009" && m.Name == "Orphan");
    }

    [Fact]
    public async Task Untag_removes_only_the_selected_tag_with_a_minus_one_delta()
    {
        await _maintenance.SaveMatterAsync(new Matter { Id = "M-2026-001", Name = "Alpha" }, CancellationToken.None);
        await _maintenance.SaveMatterAsync(new Matter { Id = "M-2026-002", Name = "Beta" }, CancellationToken.None);
        await WriteFinalizedSessionAsync("s-both", new[] { "M-2026-001", "M-2026-002" });
        await WriteFinalizedSessionAsync("s-keep", new[] { "M-2026-001" });
        await _maintenance.RebuildIndexAsync(CancellationToken.None);   // index counts = disk truth: Alpha 2, Beta 1

        var vm = MakeVm();
        await vm.RefreshAsync();
        await vm.SelectAsync("M-2026-001");
        Assert.Equal(2, vm.TaggedSessions.Count);

        await vm.UntagSessionAsync("s-both");

        Assert.Empty(_reporter.Errors);
        var meta = await new MetadataStore(_paths.MetaJson("s-both")).LoadAsync(CancellationToken.None);
        Assert.Equal(new[] { "M-2026-002" }, meta!.MatterIds);          // ONLY the selected matter removed
        var index = await _maintenance.ListMattersAsync(CancellationToken.None);
        Assert.Equal(1, index.Matters.Single(m => m.Id == "M-2026-001").SessionCount);  // -1 delta applied
        Assert.Equal(1, index.Matters.Single(m => m.Id == "M-2026-002").SessionCount);  // other matter untouched
        Assert.Equal("s-keep", Assert.Single(vm.TaggedSessions).SessionId);             // sublist refreshed + reselected
        Assert.Equal(1, vm.Matters.Single(m => m.Id == "M-2026-001").SessionCount);     // card "N session(s)" updated
    }

    [Fact]
    public async Task Untag_noops_when_not_tagged_and_never_double_decrements()
    {
        await _maintenance.SaveMatterAsync(new Matter { Id = "M-2026-001", Name = "Alpha" }, CancellationToken.None);
        await WriteFinalizedSessionAsync("s-keep", new[] { "M-2026-001" });
        await WriteFinalizedSessionAsync("s-once", new[] { "M-2026-001" });
        await _maintenance.RebuildIndexAsync(CancellationToken.None);   // count = 2

        var vm = MakeVm();
        await vm.RefreshAsync();
        await vm.SelectAsync("M-2026-001");

        await vm.UntagSessionAsync("s-once");
        await vm.UntagSessionAsync("s-once");      // double-click race: already untagged -> no write, no delta
        await vm.UntagSessionAsync("s-gone");      // session.json never existed -> silent no-op, never throws

        Assert.Empty(_reporter.Errors);
        var index = await _maintenance.ListMattersAsync(CancellationToken.None);
        Assert.Equal(1, index.Matters.Single(m => m.Id == "M-2026-001").SessionCount);  // ONE decrement, not two
        Assert.Equal("s-keep", Assert.Single(vm.TaggedSessions).SessionId);
    }

    [Fact]
    public async Task Untag_refused_while_any_window_is_open_for_that_session()
    {
        await _maintenance.SaveMatterAsync(new Matter { Id = "M-2026-001", Name = "Alpha" }, CancellationToken.None);
        await WriteFinalizedSessionAsync("s-open", new[] { "M-2026-001" });
        await _maintenance.RebuildIndexAsync(CancellationToken.None);   // count = 1

        var registry = new WindowRegistry();
        var vm = MakeVm(registry);
        await vm.RefreshAsync();
        await vm.SelectAsync("M-2026-001");

        Action close = () => { };
        registry.Register("s-open", close);        // a Session Details window is open for s-open
        Assert.False(vm.CanUntag("s-open"));
        await vm.UntagSessionAsync("s-open");

        Assert.Contains(_reporter.Infos, m => m.Contains("Close it first", StringComparison.Ordinal));
        var meta = await new MetadataStore(_paths.MetaJson("s-open")).LoadAsync(CancellationToken.None);
        Assert.Contains("M-2026-001", meta!.MatterIds);                 // still tagged: nothing written
        var index = await _maintenance.ListMattersAsync(CancellationToken.None);
        Assert.Equal(1, index.Matters.Single(m => m.Id == "M-2026-001").SessionCount);  // no delta

        registry.Unregister("s-open", close);      // window closed -> guard lifts
        Assert.True(vm.CanUntag("s-open"));
        await vm.UntagSessionAsync("s-open");
        Assert.Empty(_reporter.Errors);
        meta = await new MetadataStore(_paths.MetaJson("s-open")).LoadAsync(CancellationToken.None);
        Assert.DoesNotContain("M-2026-001", meta!.MatterIds);           // now proceeds normally
    }

    [Fact]
    public async Task Untag_refused_while_window_open_never_raises_SessionUntagged()
    {
        await _maintenance.SaveMatterAsync(new Matter { Id = "M-2026-001", Name = "Alpha" }, CancellationToken.None);
        await WriteFinalizedSessionAsync("s-open", new[] { "M-2026-001" });
        await _maintenance.RebuildIndexAsync(CancellationToken.None);   // count = 1

        var registry = new WindowRegistry();
        var vm = MakeVm(registry);
        await vm.RefreshAsync();
        await vm.SelectAsync("M-2026-001");
        var raised = new List<string>();
        vm.SessionUntagged += id => raised.Add(id);

        Action close = () => { };
        registry.Register("s-open", close);        // a Session Details window is open for s-open

        await vm.UntagSessionAsync("s-open");

        Assert.Empty(raised);                                           // refusal path never fires the event
        Assert.Contains(_reporter.Infos, m => m.Contains("Close it first", StringComparison.Ordinal));
        var meta = await new MetadataStore(_paths.MetaJson("s-open")).LoadAsync(CancellationToken.None);
        Assert.Contains("M-2026-001", meta!.MatterIds);                 // still tagged: nothing written
        var index = await _maintenance.ListMattersAsync(CancellationToken.None);
        Assert.Equal(1, index.Matters.Single(m => m.Id == "M-2026-001").SessionCount);  // no delta
    }

    [Fact]
    public async Task Untag_raises_SessionUntagged_only_when_a_tag_was_actually_removed()
    {
        await _maintenance.SaveMatterAsync(new Matter { Id = "M-2026-001", Name = "Alpha" }, CancellationToken.None);
        await WriteFinalizedSessionAsync("s-evt", new[] { "M-2026-001" });
        await _maintenance.RebuildIndexAsync(CancellationToken.None);

        var vm = MakeVm();
        await vm.RefreshAsync();
        await vm.SelectAsync("M-2026-001");
        var raised = new List<string>();
        vm.SessionUntagged += id => raised.Add(id);

        await vm.UntagSessionAsync("s-evt");
        Assert.Equal(new[] { "s-evt" }, raised);       // fired exactly once, with the session id

        await vm.UntagSessionAsync("s-evt");           // no-op: nothing removed on disk
        Assert.Equal(new[] { "s-evt" }, raised);       // grid wiring must NOT refresh on a no-op
    }

    [Fact]
    public async Task Export_matter_archive_writes_zip_and_reveals()
    {
        await new MatterStore(_paths.MattersDir).SaveAsync(new Matter { Id = "M-1", Name = "Acme" }, default);
        await WriteFinalizedSessionAsync("s1", new[] { "M-1" });

        string dest = Path.Combine(_root, "out", "acme.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        string? revealed = null;
        var vm = MakeVm(pickSavePath: _ => dest, revealFile: p => revealed = p);
        await vm.RefreshAsync();
        await vm.SelectAsync("M-1");

        await vm.ExportMatterArchiveAsync();

        Assert.Empty(_reporter.Errors);
        Assert.True(File.Exists(dest));
        Assert.Equal(dest, revealed);
        Assert.False(vm.IsExporting);
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
