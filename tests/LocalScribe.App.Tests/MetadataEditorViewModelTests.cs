using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
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
    public async Task Explicit_save_persists_meta_v2_and_regenerates_session_txt()
    {
        await WriteSessionAsync("s-title", "Old title");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-title"));
        Assert.True(ed.IsEditable);

        ed.Title = "Estate call - corrected";
        Assert.True(ed.IsDirty);
        await ed.SaveCommand.ExecuteAsync(null);

        string raw = await File.ReadAllTextAsync(_paths.MetaJson("s-title"));
        Assert.Contains("\"schemaVersion\": 2", raw);
        Assert.Contains("Estate call - corrected", raw);
        // SaveMetaAsync regenerates projections before returning (Task 9), and ExecuteAsync
        // only completes after it returns - session.txt is already fresh here.
        Assert.Contains("Estate call - corrected",
            await File.ReadAllTextAsync(_paths.SessionTxt("s-title")));
        Assert.False(ed.IsDirty);
    }

    [Fact]
    public async Task Metadata_save_never_flips_Edited_or_LastEditedAtUtc()
    {
        await WriteSessionAsync("s-flags", "Before");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-flags"));

        ed.Title = "After";
        await ed.SaveCommand.ExecuteAsync(null);

        var back = await new MetadataStore(_paths.MetaJson("s-flags")).LoadAsync(CancellationToken.None);
        Assert.Equal("After", back!.Title);
        Assert.False(back.Edited);                          // design 3.3: EditStore stays the flags' only writer
        Assert.Null(back.LastEditedAtUtc);
        string raw = await File.ReadAllTextAsync(_paths.MetaJson("s-flags"));
        Assert.Contains("\"edited\": false", raw);
        Assert.DoesNotContain("lastEditedAtUtc", raw);      // null is omitted (WhenWritingNull)
    }

    [Fact]
    public async Task Saved_fires_once_with_session_id_on_explicit_save()
    {
        await WriteSessionAsync("s-saved", "Before");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-saved"));
        Assert.True(ed.IsEditable);

        var saved = new List<string>();
        ed.Saved += id => saved.Add(id);

        ed.Title = "After";
        Assert.Empty(saved);                                // editing alone never fires it
        await ed.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "s-saved" }, saved.ToArray()); // fires exactly once, with the id
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
        Assert.Equal(0, SessionCount("M-2026-001"));        // buffered until the explicit Save
        await ed.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, SessionCount("M-2026-001"));
        var meta = await new MetadataStore(_paths.MetaJson("s-tags")).LoadAsync(CancellationToken.None);
        Assert.Equal(["M-2026-001"], meta!.MatterIds);

        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single(o => o.Id == "M-2026-001"));
        await ed.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, SessionCount("M-2026-001"));
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
        Assert.True(ed.IsDirty);
        await ed.SaveCommand.ExecuteAsync(null);
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
        await ed.SaveCommand.ExecuteAsync(null);
        var meta = await new MetadataStore(_paths.MetaJson("s-free")).LoadAsync(CancellationToken.None);
        Assert.Equal(2, meta!.Participants.Count);
        Assert.Equal(SourceKind.Local, meta.Participants[1].Side);
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
        Assert.False(ed.IsDirty);                           // locked rows never even dirty
        Assert.False(ed.SaveCommand.CanExecute(null));
        await ed.SaveCommand.ExecuteAsync(null);            // belt-and-braces guard: no-op
        var meta = await new MetadataStore(_paths.MetaJson("s-open")).LoadAsync(CancellationToken.None);
        Assert.Equal("Open", meta!.Title);                  // gated save never ran
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
    public async Task Dispose_unsubscribes_from_session_state_and_is_idempotent()
    {
        // Isolates RecomputeEditable's State-driven (liveLocked) branch from the
        // IsPendingRecovery branch: force the on-disk record to look FINALIZED while the
        // controller still reports this session as the current (Recording) one, so a bare
        // State flip - not a change in IsPendingRecovery - is what would flip IsEditable.
        var ed = MakeEditor();
        await _session.StartCommand.ExecuteAsync(null);
        string liveId = _session.CurrentSessionId!;
        var store = new SessionStore(_paths.SessionJson(liveId));
        var rec = await store.ReadAsync(CancellationToken.None);
        await store.SaveAsync(rec! with { EndedAtUtc = rec.StartedAtUtc.AddMinutes(1) }, CancellationToken.None);

        ed.Attach(await RowAsync(liveId));
        Assert.False(ed.IsEditable);          // locked: row.Id == CurrentSessionId, State == Recording

        ed.Dispose();
        ed.Dispose();                         // idempotent - must not throw

        _session.State = SessionState.Idle;   // would flip IsEditable to true if still subscribed
        Assert.False(ed.IsEditable);          // Dispose unsubscribed - no longer reacts
    }

    [Fact]
    public async Task Failed_save_reports_and_keeps_the_edits_dirty_for_retry()
    {
        await WriteSessionAsync("s-fail", "Original");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-fail"));

        File.Delete(_paths.MetaJson("s-fail"));
        Directory.CreateDirectory(_paths.MetaJson("s-fail"));   // meta.json is now a DIRECTORY: write throws

        ed.Title = "Doomed";
        await ed.SaveCommand.ExecuteAsync(null);

        Assert.Single(_reporter.Errors);
        Assert.Equal("Doomed", ed.Title);                   // explicit-save failure KEEPS the edits
        Assert.True(ed.IsDirty);                            // still pending: retry or Discard
    }

    // ---- F1 (Stage4 review): no silent metadata loss from a stale load-time snapshot --------

    /// <summary>Builds one page (so the editor and the row actions act on the SAME row object)
    /// over the shared maintenance service.</summary>
    private SessionsPageViewModel MakePage()
        => new(_maintenance, _session, new WindowRegistry(), _reporter,
               dispatch: a => a(), _time, revealInExplorer: _ => { });

    [Fact]
    public async Task Archive_after_editor_title_edit_keeps_both_the_title_and_archived()
    {
        await WriteMatterAsync("M-2026-001", "Estate");
        await WriteSessionAsync("s-arch-edit", "Old title", matterIds: ["M-2026-001"]);
        var page = MakePage();
        await page.OnNavigatedToAsync();
        var row = page.Rows.Single(r => r.Id == "s-arch-edit");
        var ed = MakeEditor();
        ed.Attach(row);
        Assert.True(ed.IsEditable);

        ed.Title = "Corrected title";
        await ed.SaveCommand.ExecuteAsync(null);

        // Archiving the SAME row must read the CURRENT meta, not the stale row snapshot,
        // so the just-saved title is preserved.
        await page.ToggleArchiveCommand.ExecuteAsync(row);

        var onDisk = await new MetadataStore(_paths.MetaJson("s-arch-edit")).LoadAsync(CancellationToken.None);
        Assert.NotNull(onDisk);
        Assert.Equal("Corrected title", onDisk!.Title);   // NOT reverted by the archive
        Assert.True(onDisk.Archived);
        Assert.Equal(["M-2026-001"], onDisk.MatterIds);   // tags preserved
        Assert.False(onDisk.Edited);                      // archive never flips the correction flags
        Assert.Null(onDisk.LastEditedAtUtc);
        Assert.Empty(_reporter.Errors);
    }

    [Fact]
    public async Task Delete_after_editor_retag_decrements_the_current_matter_not_the_stale_one()
    {
        await WriteMatterAsync("M-2026-001", "Alpha");
        await WriteMatterAsync("M-2026-002", "Beta");
        await WriteSessionAsync("s-retag", "S");        // built with NO tags: the row snapshot is []
        var page = MakePage();
        page.ConfirmDeleteRequested += (_, confirm) => confirm();
        await page.OnNavigatedToAsync();
        var row = page.Rows.Single(r => r.Id == "s-retag");
        var ed = MakeEditor();
        ed.Attach(row);
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 2, TimeSpan.FromSeconds(10)));

        // Tag M-001 and commit (count -> 1), then untag it, tag M-002, and commit again
        // (M-001 -> 0, M-002 -> 1). The row snapshot's MatterIds stay [] throughout.
        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single(o => o.Id == "M-2026-001"));
        await ed.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, SessionCount("M-2026-001"));
        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single(o => o.Id == "M-2026-001"));
        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single(o => o.Id == "M-2026-002"));
        await ed.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, SessionCount("M-2026-002"));
        Assert.Equal(0, SessionCount("M-2026-001"));

        await page.DeleteSessionCommand.ExecuteAsync(row);

        // The decrement must target M-002 (the CURRENT tag), not the stale row snapshot's [].
        Assert.Equal(0, SessionCount("M-2026-002"));
        Assert.Equal(0, SessionCount("M-2026-001"));
        Assert.Empty(_reporter.Errors);
    }

    [Fact]
    public async Task Reattaching_the_same_row_after_a_save_reflects_the_last_saved_meta()
    {
        await WriteSessionAsync("s-reattach", "Old");
        await WriteSessionAsync("s-other", "Other");
        var page = MakePage();
        await page.OnNavigatedToAsync();
        var row = page.Rows.Single(r => r.Id == "s-reattach");
        var other = page.Rows.Single(r => r.Id == "s-other");
        var ed = MakeEditor();

        ed.Attach(row);
        ed.Title = "Corrected";
        await ed.SaveCommand.ExecuteAsync(null);

        ed.Attach(other);                 // move away...
        ed.Attach(row);                   // ...and back to the SAME (still stale) row object
        Assert.Equal("Corrected", ed.Title);   // re-seeded from the last SAVED meta, not row.Item.Meta

        ed.Description = "notes";          // a later field edit must not revert the title
        await ed.SaveCommand.ExecuteAsync(null);
        var onDisk = await new MetadataStore(_paths.MetaJson("s-reattach")).LoadAsync(CancellationToken.None);
        Assert.Equal("Corrected", onDisk!.Title);
        Assert.Equal("notes", onDisk.Description);
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
