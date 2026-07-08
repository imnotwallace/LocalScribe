// tests/LocalScribe.App.Tests/ReadViewEditModeTests.cs
using System.IO;
using System.Linq;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Task 13: read-view Edit-mode orchestration (enter/save/cancel) on
/// ReadViewViewModel. Split-save-reload round trip against a real temp session -
/// mirrors ReadViewViewModelTests' harness construction exactly.</summary>
public sealed class ReadViewEditModeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-edit-vm-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakeSettings _settings;
    private readonly FakeReporter _reporter = new();
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly MaintenanceService _maintenance;
    private readonly FakePlayer _player = new();

    public ReadViewEditModeTests()
    {
        _paths = new StoragePaths(_root);
        _settings = new FakeSettings(new Settings { StorageRoot = _root });
        _maintenance = new MaintenanceService(_paths, _settings, new FakeBin(), _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ReadViewViewModel MakeVm()
        => new(_maintenance, _paths, _settings, _reporter, _player, dispatch: a => a(), _time);

    /// <summary>Finalized session with one Remote segment seq 3 ("First. Second." 15000..17000).</summary>
    private async Task WriteFixtureSessionAsync(string id)
    {
        var started = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(10), DurationMs = 600_000,
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            Model = "small.en", Backend = "cuda", Language = "en",
            RetainedAudioSources = new[] { SourceKind.Remote },
            Devices = new DeviceSnapshot
            {
                Remote = new RemoteSnapshot { Mode = RemoteMode.PerProcess, FellBackToSystemMix = false },
            },
        }, CancellationToken.None);

        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "Edit-mode session",
            Participants = new[]
            {
                new SessionParticipant { Id = "p-jane-doe", Name = "Jane", Side = SourceKind.Remote },
            },
        }, CancellationToken.None);

        var transcript = new TranscriptStore(_paths.TranscriptJsonl(id));
        await transcript.AppendAsync(TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000,
            "First. Second.", "Them"), CancellationToken.None);
    }

    [Fact]
    public async Task EnterEditMode_gated_on_finalized_session()
    {
        var started = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson("live-1")).SaveAsync(new SessionRecord
        {
            Id = "live-1", App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = null, DurationMs = 0,
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            Model = "small.en", Backend = "cuda", Language = "en",
        }, CancellationToken.None);

        var vm = MakeVm();
        await vm.LoadAsync("live-1", CancellationToken.None);
        Assert.False(vm.CanEdit);

        vm.EnterEditMode();
        Assert.False(vm.IsEditMode);
        Assert.Empty(vm.EditSections);
    }

    [Fact]
    public async Task Split_save_reload_round_trip_persists_split_and_exits_edit_mode()
    {
        await WriteFixtureSessionAsync("edit-1");
        var vm = MakeVm();
        await vm.LoadAsync("edit-1", CancellationToken.None);
        Assert.True(vm.CanEdit);

        vm.EnterEditMode();
        Assert.True(vm.IsEditMode);
        Assert.Empty(_reporter.Errors);

        var section = vm.EditSections.Single(s => !s.Row.IsMarker);
        section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal);
        section.SplitSegment(section.Segments[0], caret: 6);

        await vm.SaveEditsAsync(CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        Assert.False(vm.IsEditMode);
        Assert.Empty(vm.EditSections);

        // Reload happened: rows now show the split - two Remote turns for seq 3.
        var segments = vm.Rows.SelectMany(r => r.Data.Segments).Where(s => s.Seq == 3).ToList();
        Assert.Equal(2, segments.Count);

        var edits = await new EditStore(_paths.SessionDir("edit-1"), _time).LoadAsync(CancellationToken.None);
        Assert.True(edits!.Splits.ContainsKey("3"));
    }

    [Fact]
    public async Task WholeSegment_speaker_selection_pins_on_save()
    {
        await WriteFixtureSessionAsync("edit-pin");
        var vm = MakeVm();
        await vm.LoadAsync("edit-pin", CancellationToken.None);
        Assert.True(vm.CanEdit);

        vm.EnterEditMode();
        var section = vm.EditSections.Single(s => !s.Row.IsMarker);
        section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal,
            remoteChoices: vm.SpeakerChoicesForSource(TranscriptSource.Remote),
            localChoices: vm.SpeakerChoicesForSource(TranscriptSource.Local));

        var seg = Assert.Single(section.Segments);
        Assert.False(seg.IsSplitChild);
        // Same participant WriteFixtureSessionAsync declares (Remote, Name "Jane"); mirrors what
        // the real ComboBox's SelectedItem binding would assign from SpeakerChoicesForSource.
        var jane = seg.SpeakerChoices.Single(c => c.ParticipantId == "p-jane-doe");
        seg.Speaker = jane;

        await vm.SaveEditsAsync(CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        Assert.False(vm.IsEditMode);

        var speakers = await new SpeakersStore(_paths.SpeakersJson("edit-pin")).LoadAsync(CancellationToken.None);
        Assert.NotNull(speakers);
        Assert.Contains("3", speakers!.Pinned["Remote"]);
        Assert.True(speakers.Assignments["Remote"].ContainsKey("3"));

        // The pinned cluster key is minted for Jane (no prior ClusterKey) and persisted onto the
        // participant so subsequent pins/reassignments reuse it (mirrors SaveSpeakerPinsAsync's
        // documented ownership-write contract).
        var meta = await new MetadataStore(_paths.MetaJson("edit-pin")).LoadAsync(CancellationToken.None);
        var participant = meta!.Participants.Single(p => p.Id == "p-jane-doe");
        Assert.NotNull(participant.ClusterKey);
        Assert.Equal(participant.ClusterKey, speakers.Assignments["Remote"]["3"]);
    }

    // GUI smoke: a pinned line was permanently "stuck" - the dropdown had no way back to the
    // automatic Me/Them baseline. The new "Automatic (Me / Them)" choice removes the pin.
    [Fact]
    public async Task Automatic_choice_removes_an_existing_pin_on_save()
    {
        await WriteFixtureSessionAsync("edit-unpin");
        var vm = MakeVm();
        await vm.LoadAsync("edit-unpin", CancellationToken.None);

        // First pin seq 3 to Jane, save.
        vm.EnterEditMode();
        var section = vm.EditSections.Single(s => !s.Row.IsMarker);
        section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal,
            remoteChoices: vm.SpeakerChoicesForSource(TranscriptSource.Remote),
            localChoices: vm.SpeakerChoicesForSource(TranscriptSource.Local));
        var seg = Assert.Single(section.Segments);
        seg.Speaker = seg.SpeakerChoices.Single(c => c.ParticipantId == "p-jane-doe");
        await vm.SaveEditsAsync(CancellationToken.None);

        var pinned = await new SpeakersStore(_paths.SpeakersJson("edit-unpin")).LoadAsync(CancellationToken.None);
        Assert.Contains("3", pinned!.Pinned["Remote"]);                 // sanity: it IS pinned now

        // Now un-assign via "Automatic (Me / Them)".
        vm.EnterEditMode();
        var section2 = vm.EditSections.Single(s => !s.Row.IsMarker);
        section2.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal,
            remoteChoices: vm.SpeakerChoicesForSource(TranscriptSource.Remote),
            localChoices: vm.SpeakerChoicesForSource(TranscriptSource.Local));
        var seg2 = Assert.Single(section2.Segments);
        seg2.Speaker = seg2.SpeakerChoices.Single(c => c.IsUnassign);
        await vm.SaveEditsAsync(CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        var after = await new SpeakersStore(_paths.SpeakersJson("edit-unpin")).LoadAsync(CancellationToken.None);
        Assert.False(after!.Pinned.TryGetValue("Remote", out var pins) && pins.Contains("3"));
        Assert.False(after.Assignments.TryGetValue("Remote", out var asg) && asg.ContainsKey("3"));
    }

    [Fact]
    public async Task Unchanged_speaker_choice_does_not_pin()
    {
        await WriteFixtureSessionAsync("edit-nopin");
        var vm = MakeVm();
        await vm.LoadAsync("edit-nopin", CancellationToken.None);

        vm.EnterEditMode();
        var section = vm.EditSections.Single(s => !s.Row.IsMarker);
        section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal,
            remoteChoices: vm.SpeakerChoicesForSource(TranscriptSource.Remote),
            localChoices: vm.SpeakerChoicesForSource(TranscriptSource.Local));
        // Leave Speaker at its BeginEdit default (null / "(unchanged)"): ToPinTarget() is null,
        // so SaveEditsAsync's pin loop must skip this segment entirely - speakers.json stays absent.

        await vm.SaveEditsAsync(CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        var speakers = await new SpeakersStore(_paths.SpeakersJson("edit-nopin")).LoadAsync(CancellationToken.None);
        Assert.Null(speakers);
    }

    [Fact]
    public async Task RefreshRosterAsync_in_edit_mode_refreshes_choices_without_losing_in_progress_edits()
    {
        // Task 17 live roster sync: this is the "must never lose edits" half of the contract - a
        // roster save landing while a section is mid-edit re-threads fresh SpeakerChoices without
        // discarding the user's in-progress text.
        await WriteFixtureSessionAsync("edit-roster");
        var vm = MakeVm();
        await vm.LoadAsync("edit-roster", CancellationToken.None);

        vm.EnterEditMode();
        var section = vm.EditSections.Single(s => !s.Row.IsMarker);
        section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal,
            remoteChoices: vm.SpeakerChoicesForSource(TranscriptSource.Remote),
            localChoices: vm.SpeakerChoicesForSource(TranscriptSource.Local));
        var seg = Assert.Single(section.Segments);
        Assert.DoesNotContain(seg.SpeakerChoices, c => c.ParticipantId == "p-alice");
        seg.EditedText = "In-progress edit the user is still typing.";

        // Simulate Session Details committing a roster change (adds "Alice", Remote) - the same
        // meta.json write MetadataEditorViewModel.SaveAsync performs, just without the window.
        var metaStore = new MetadataStore(_paths.MetaJson("edit-roster"));
        var meta = (await metaStore.LoadAsync(CancellationToken.None))!;
        await metaStore.SaveAsync(meta with
        {
            Participants = meta.Participants.Append(
                new SessionParticipant { Id = "p-alice", Name = "Alice", Side = SourceKind.Remote }).ToArray(),
        }, CancellationToken.None);

        await vm.RefreshRosterAsync(CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        // Still editing, same section/segment objects, in-progress text untouched.
        Assert.True(vm.IsEditMode);
        var stillSection = Assert.Single(vm.EditSections);
        var stillSeg = Assert.Single(stillSection.Segments);
        Assert.Same(seg, stillSeg);
        Assert.Equal("In-progress edit the user is still typing.", stillSeg.EditedText);
        // The dropdown's candidate list now offers the newly added participant.
        Assert.Contains(stillSeg.SpeakerChoices, c => c.ParticipantId == "p-alice");
    }

    [Fact]
    public async Task RefreshRosterAsync_outside_edit_mode_reloads_rows_from_disk()
    {
        // Task 17: outside Edit mode there is no in-progress state to protect, so a full
        // ReloadRowsAsync is safe and also picks up ParticipantDisplays.
        await WriteFixtureSessionAsync("edit-roster-readmode");
        var vm = MakeVm();
        await vm.LoadAsync("edit-roster-readmode", CancellationToken.None);
        Assert.False(vm.IsEditMode);
        Assert.DoesNotContain(vm.ParticipantDisplays, d => d.StartsWith("Alice"));

        var metaStore = new MetadataStore(_paths.MetaJson("edit-roster-readmode"));
        var meta = (await metaStore.LoadAsync(CancellationToken.None))!;
        await metaStore.SaveAsync(meta with
        {
            Participants = meta.Participants.Append(
                new SessionParticipant { Id = "p-alice", Name = "Alice", Side = SourceKind.Remote }).ToArray(),
        }, CancellationToken.None);

        await vm.RefreshRosterAsync(CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        Assert.False(vm.IsEditMode);
        Assert.Contains(vm.ParticipantDisplays, d => d.StartsWith("Alice"));
    }

    [Fact]
    public async Task CancelEdit_drops_sections_without_writing()
    {
        await WriteFixtureSessionAsync("edit-cancel");
        var vm = MakeVm();
        await vm.LoadAsync("edit-cancel", CancellationToken.None);

        vm.EnterEditMode();
        var section = vm.EditSections.Single(s => !s.Row.IsMarker);
        section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal);
        section.SplitSegment(section.Segments[0], caret: 6);

        vm.CancelEdit();

        Assert.False(vm.IsEditMode);
        Assert.Empty(vm.EditSections);
        var edits = await new EditStore(_paths.SessionDir("edit-cancel"), _time).LoadAsync(CancellationToken.None);
        Assert.False(edits?.Splits.ContainsKey("3") ?? false);
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
        public void SendToRecycleBin(string path)
        {
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

    private sealed class FakePlayer : IDualAudioPlayer
    {
        public string? LoadedLocal, LoadedRemote;
        public bool LoadCalled;
        public int LoadCount { get; private set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public event Action? MediaReady;
        public event Action? MediaEnded;
        public void Load(string? localPath, string? remotePath)
        {
            LoadCalled = true;
            LoadCount++;
            (LoadedLocal, LoadedRemote) = (localPath, remotePath);
        }
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
