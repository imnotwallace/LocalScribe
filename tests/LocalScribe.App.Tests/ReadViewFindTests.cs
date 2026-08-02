using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ReadViewFindTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-find-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakeSettings _settings;
    private readonly FakeReporter _reporter = new();
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly MaintenanceService _maintenance;

    public ReadViewFindTests()
    {
        _paths = new StoragePaths(_root);
        _settings = new FakeSettings(new Settings { StorageRoot = _root });
        _maintenance = new MaintenanceService(_paths, _settings, new FakeBin(), _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ReadViewViewModel MakeVm()
        => new(_maintenance, _paths, _settings, _reporter, new FakePlayer(), dispatch: a => a(), _time);

    /// <summary>Rows after load: [0] Sam (seq 0+1 grouped; seq 1 corrected), [1] Jane (seq 2),
    /// [2] marker. "morning" hits rows 0 and 1; "device" hits the marker row; "orignal" exists
    /// only in seq 1's machine RAW text (not visible).</summary>
    private async Task WriteFixtureSessionAsync(string id)
    {
        var started = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(10), DurationMs = 600_000,
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            Model = "small.en", Backend = "cuda", Language = "en",
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "Find fixture",
            Participants = new[]
            {
                new SessionParticipant { Id = "p1", Name = "Sam", Side = SourceKind.Local, IsSelf = true },
                new SessionParticipant { Id = "p2", Name = "Jane", Side = SourceKind.Remote },
            },
        }, CancellationToken.None);
        var t = new TranscriptStore(_paths.TranscriptJsonl(id));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1500,
            "we spoke to the client this morning", "Me"), CancellationToken.None);
        await t.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 1600, 3000,
            "the orignal words", "Me"), CancellationToken.None);
        await t.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Remote, 3200, 4200,
            "sounds good to me this morning", "Them"), CancellationToken.None);
        await t.AppendAsync(TranscriptLine.Marker(3, 4200, Markers.AudioDeviceChanged),
            CancellationToken.None);
        await new EditStore(_paths.SessionDir(id), _time)
            .ApplyTextCorrectionAsync(1, "the corrected words", CancellationToken.None);
    }

    [Fact]
    public async Task Find_counts_visible_corrected_text_and_wraps_with_next_previous()
    {
        await WriteFixtureSessionAsync("find-1");
        var vm = MakeVm();
        await vm.LoadAsync("find-1", CancellationToken.None);
        Assert.Empty(_reporter.Errors);
        Assert.Equal(3, vm.Rows.Count);

        vm.OpenFind();
        Assert.True(vm.IsFindOpen);
        Assert.Equal("", vm.FindStatus);

        vm.FindText = "morning";
        Assert.Equal("1/2", vm.FindStatus);
        Assert.Equal(0, vm.CurrentFindRowIndex);
        vm.FindNext();
        Assert.Equal("2/2", vm.FindStatus);
        Assert.Equal(1, vm.CurrentFindRowIndex);
        vm.FindNext();                                                    // wraps forward
        Assert.Equal("1/2", vm.FindStatus);
        vm.FindPrevious();                                                // wraps backward
        Assert.Equal("2/2", vm.FindStatus);

        vm.FindText = "orignal";                                          // machine RAW text: not visible
        Assert.Equal("0/0", vm.FindStatus);
        vm.FindText = "corrected";                                        // the visible corrected text IS
        Assert.Equal("1/1", vm.FindStatus);
        Assert.Equal(0, vm.CurrentFindRowIndex);

        vm.FindText = "device";                                           // marker rows are visible text too
        Assert.Equal("1/1", vm.FindStatus);
        Assert.Equal(2, vm.CurrentFindRowIndex);
    }

    [Fact]
    public async Task Find_flags_rows_and_close_clears_them()
    {
        await WriteFixtureSessionAsync("find-2");
        var vm = MakeVm();
        await vm.LoadAsync("find-2", CancellationToken.None);

        vm.OpenFind("morning");
        Assert.True(vm.Rows[0].IsFindMatch);
        Assert.True(vm.Rows[0].IsCurrentFindMatch);
        Assert.True(vm.Rows[1].IsFindMatch);
        Assert.False(vm.Rows[1].IsCurrentFindMatch);
        Assert.False(vm.Rows[2].IsFindMatch);

        vm.FindNext();
        Assert.False(vm.Rows[0].IsCurrentFindMatch);
        Assert.True(vm.Rows[1].IsCurrentFindMatch);

        vm.CloseFind();
        Assert.False(vm.IsFindOpen);
        Assert.All(vm.Rows, r => { Assert.False(r.IsFindMatch); Assert.False(r.IsCurrentFindMatch); });
        Assert.Equal(-1, vm.CurrentFindRowIndex);
        Assert.Equal("", vm.FindStatus);
        Assert.Equal("morning", vm.FindText);                             // kept for a quick re-open
    }

    [Fact]
    public async Task Find_survives_a_rows_reload_and_stays_open_across_edit_mode()
    {
        await WriteFixtureSessionAsync("find-3");
        var vm = MakeVm();
        await vm.LoadAsync("find-3", CancellationToken.None);

        vm.OpenFind("morning");
        Assert.Equal("1/2", vm.FindStatus);
        await vm.ReloadRowsAsync(CancellationToken.None);                 // rows are NEW objects
        Assert.Equal("1/2", vm.FindStatus);
        Assert.True(vm.Rows[0].IsFindMatch);                              // flags re-stamped on new rows

        vm.EnterEditMode();                                               // item 1: bar SURVIVES edit
        Assert.True(vm.IsEditMode);
        Assert.True(vm.IsFindOpen);
        Assert.Equal("1/2", vm.FindStatus);                               // recomputed in section space
        Assert.Equal(0, vm.CurrentFindRowIndex);                          // EditSections index now
        Assert.True(vm.EditSections[0].IsFindMatch);
        Assert.True(vm.EditSections[0].IsCurrentFindMatch);
        Assert.True(vm.EditSections[1].IsFindMatch);
        Assert.False(vm.EditSections[1].IsCurrentFindMatch);

        vm.CancelEdit();                                                  // back to Rows space
        Assert.True(vm.IsFindOpen);
        Assert.Empty(vm.EditSections);
        Assert.Equal("1/2", vm.FindStatus);
        Assert.True(vm.Rows[0].IsFindMatch);
        Assert.True(vm.Rows[0].IsCurrentFindMatch);
    }

    [Fact]
    public async Task OpenFind_works_while_editing_and_counts_skip_marker_rows()
    {
        await WriteFixtureSessionAsync("find-5");
        var vm = MakeVm();
        await vm.LoadAsync("find-5", CancellationToken.None);

        vm.EnterEditMode();
        vm.OpenFind("device");                       // matches ONLY the marker row's text
        Assert.True(vm.IsFindOpen);                  // the old IsEditMode guard is gone
        Assert.Equal("0/0", vm.FindStatus);          // markers are not editable: excluded here
        Assert.Equal(-1, vm.CurrentFindRowIndex);

        vm.FindText = "morning";
        Assert.Equal("1/2", vm.FindStatus);          // both non-marker sections match
        vm.CancelEdit();
        Assert.Equal("1/2", vm.FindStatus);          // read mode sees the same two rows

        vm.FindText = "device";                      // and the marker match is back in read mode
        Assert.Equal("1/1", vm.FindStatus);
        Assert.Equal(2, vm.CurrentFindRowIndex);
    }

    [Fact]
    public async Task Edit_transitions_keep_the_current_match_position_via_row_identity()
    {
        await WriteFixtureSessionAsync("find-6");
        var vm = MakeVm();
        await vm.LoadAsync("find-6", CancellationToken.None);

        vm.OpenFind("morning");
        vm.FindNext();                               // read space: current = row 1 (Jane)
        Assert.Equal(1, vm.CurrentFindRowIndex);

        vm.EnterEditMode();                          // mapped via ReferenceEquals(section.Row, row.Data)
        Assert.Equal(1, vm.CurrentFindRowIndex);     // section 1 wraps the same DisplayRow instance
        Assert.Equal("2/2", vm.FindStatus);

        vm.CancelEdit();                             // mapped back the same way
        Assert.Equal(1, vm.CurrentFindRowIndex);
        Assert.Equal("2/2", vm.FindStatus);
    }

    [Fact]
    public async Task Save_recomputes_matches_on_the_reloaded_rows_in_read_space()
    {
        await WriteFixtureSessionAsync("find-7");
        var vm = MakeVm();
        await vm.LoadAsync("find-7", CancellationToken.None);

        vm.OpenFind("morning");
        vm.EnterEditMode();
        var section = vm.EditSections[0];
        section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal);
        section.Segments[0].EditedText = "we spoke to the client this evening";  // kills row 0's match

        await vm.SaveEditsAsync(CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        Assert.False(vm.IsEditMode);
        Assert.True(vm.IsFindOpen);                                   // bar survived the save too
        Assert.Equal("1/1", vm.FindStatus);                           // only Jane's reloaded row matches
        Assert.True(vm.Rows[1].IsFindMatch);
        Assert.False(vm.Rows[0].IsFindMatch);
    }

    [Fact]
    public async Task RowIndexOfSeq_and_MoveFindTo_target_the_snippet_row()
    {
        await WriteFixtureSessionAsync("find-4");
        var vm = MakeVm();
        await vm.LoadAsync("find-4", CancellationToken.None);

        Assert.Equal(0, vm.RowIndexOfSeq(1));                             // seq 1 lives in the grouped row 0
        Assert.Equal(1, vm.RowIndexOfSeq(2));
        Assert.Equal(-1, vm.RowIndexOfSeq(99));

        vm.OpenFind("morning");                                           // matches rows 0 and 1
        vm.MoveFindTo(1);
        Assert.Equal(1, vm.CurrentFindRowIndex);
        Assert.Equal("2/2", vm.FindStatus);

        vm.MoveFindTo(2);                                                 // row 2 is not a match: unchanged
        Assert.Equal(1, vm.CurrentFindRowIndex);
    }

    // Per-file fakes (App.Tests convention), byte-identical to ReadViewViewModelTests'.
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
        public void SendToRecycleBin(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) { }
    }

    private sealed class FakePlayer : IDualAudioPlayer
    {
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public event Action? MediaReady;
        public event Action? MediaEnded;
        public void Load(string? localPath, string? remotePath) { }
        public void Play() { }
        public void Pause() { }
        public void SeekMs(long ms) => PositionMs = ms;
        public void SetLegMuted(bool local, bool muted) { }
        public void SetLegVolume(bool local, double volume) { }
        public void Dispose() { }
        public void RaiseReady() => MediaReady?.Invoke();
        public void RaiseEnded() => MediaEnded?.Invoke();
    }
}
