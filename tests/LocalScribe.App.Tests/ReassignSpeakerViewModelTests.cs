using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ReassignSpeakerViewModelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-reassign-vm-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly MaintenanceService _maintenance;
    private readonly FakeReporter _reporter = new();
    private readonly ManualUtcTimeProvider _time = new(T0);

    public ReassignSpeakerViewModelTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths,
            new FakeSettings(new Settings { StorageRoot = _root }), new FakeBin(), _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static SessionMeta MetaWith(params SessionParticipant[] ps)
        => SessionMeta.CreateDefault(AppKind.Webex, T0, self: null) with
        { RemoteCount = 2, Participants = ps };

    private static RowSegment Seg(int seq, TranscriptSource source = TranscriptSource.Remote)
        => new(seq, source, seq * 2000, seq * 2000 + 2000, "text " + seq, "raw " + seq,
            IsCorrected: false, IsPinned: false);

    private ReassignSpeakerViewModel MakeVm(string sessionId, SessionMeta meta, Speakers? speakers,
        params RowSegment[] segments)
        => new(_maintenance, _reporter, sessionId, TranscriptSource.Remote, segments, meta,
            speakers, "relative", T0, "v1");

    [Fact]
    public void Candidates_list_same_side_named_participants_then_unowned_clusters()
    {
        var meta = MetaWith(
            new SessionParticipant { Id = "p-me", Name = "Sam", Side = SourceKind.Local },
            new SessionParticipant { Id = "p-alice", Name = "Alice", Side = SourceKind.Remote, ClusterKey = "Remote:0" },
            new SessionParticipant { Id = "p-bob", Name = "Bob", Side = SourceKind.Remote });
        var speakers = new Speakers
        {
            Names = new Dictionary<string, string>
            { ["Remote:0"] = "cluster zero", ["Remote:1"] = "Unknown caller", ["Local:0"] = "local voice" },
        };

        var vm = MakeVm("s", meta, speakers, Seg(0));

        Assert.Equal(3, vm.Candidates.Count);
        Assert.Equal("Alice", vm.Candidates[0].Display);                       // participants first
        Assert.Equal("Bob", vm.Candidates[1].Display);
        Assert.Equal("Unknown caller (detected voice)", vm.Candidates[2].Display);
        // Remote:0 is Alice-owned (not duplicated); Local:0 is the wrong side (excluded).
        var cluster = Assert.IsType<SpeakerPinTarget.Cluster>(vm.Candidates[2].Target);
        Assert.Equal("Remote:1", cluster.ClusterKey);
        Assert.True(vm.HasCandidates);
    }

    [Fact]
    public void No_participants_and_no_clusters_means_no_candidates()
    {
        var vm = MakeVm("s", MetaWith(), speakers: null, Seg(0));
        Assert.False(vm.HasCandidates);
    }

    [Fact]
    public void Other_stream_segments_are_disabled_and_unchecked()
    {
        var vm = MakeVm("s", MetaWith(), null, Seg(0), Seg(1, TranscriptSource.Local));
        Assert.True(vm.Segments[0].IsEnabled);
        Assert.True(vm.Segments[0].IsChecked);
        Assert.False(vm.Segments[1].IsEnabled);
        Assert.False(vm.Segments[1].IsChecked);
    }

    [Fact]
    public async Task Save_requires_a_candidate_and_a_checked_segment()
    {
        var meta = MetaWith(new SessionParticipant { Id = "p-bob", Name = "Bob", Side = SourceKind.Remote });
        var vm = MakeVm("s", meta, null, Seg(0));

        Assert.False(await vm.SaveAsync(default));                 // no candidate chosen
        Assert.NotEqual("", vm.ValidationMessage);

        vm.SelectedCandidate = vm.Candidates[0];
        vm.Segments[0].IsChecked = false;
        Assert.False(await vm.SaveAsync(default));                 // nothing checked
    }

    [Fact]
    public async Task Save_pins_the_checked_seqs_to_the_chosen_participant()
    {
        string id = "s-pin";
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = T0.AddMinutes(10),
        }, default);
        var meta = MetaWith(new SessionParticipant { Id = "p-bob", Name = "Bob", Side = SourceKind.Remote });
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(meta, default);
        var transcript = new TranscriptStore(_paths.TranscriptJsonl(id));
        await transcript.AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 2000, "a", "Them"), default);
        await transcript.AppendAsync(
            TranscriptLine.Segment(1, TranscriptSource.Remote, 2000, 4000, "b", "Them"), default);

        var vm = MakeVm(id, meta, null, Seg(0), Seg(1));
        vm.SelectedCandidate = vm.Candidates[0];
        vm.Segments[1].IsChecked = false;

        Assert.True(await vm.SaveAsync(default));
        var speakers = await new SpeakersStore(_paths.SpeakersJson(id)).LoadAsync(default);
        Assert.True(speakers!.Assignments["Remote"].ContainsKey("0"));
        Assert.False(speakers.Assignments["Remote"].ContainsKey("1"));   // unchecked seq untouched
    }

    [Fact]
    public void Filter_text_narrows_visible_segments_and_clearing_restores_all()
    {
        var vm = MakeVm("s", MetaWith(), null, Seg(0), Seg(1), Seg(2));
        Assert.Equal(3, vm.VisibleSegments.Count);                 // all shown initially

        vm.FilterText = "TEXT 1";                                  // case-insensitive
        Assert.Single(vm.VisibleSegments);
        Assert.Equal("text 1", vm.VisibleSegments[0].Preview);

        vm.FilterText = "";                                        // cleared -> all again
        Assert.Equal(3, vm.VisibleSegments.Count);
    }

    [Fact]
    public void Ticks_persist_across_filter_changes_so_selections_stack()
    {
        var vm = MakeVm("s", MetaWith(), null, Seg(0), Seg(1), Seg(2));
        vm.DeselectAllShown();                                     // start from nothing (no filter -> all)
        Assert.Equal(0, vm.SelectedCount);

        vm.FilterText = "text 1";
        vm.VisibleSegments[0].IsChecked = true;                    // tick the Seg(1) match
        vm.FilterText = "text 2";                                  // change search; Seg(1) no longer shown
        Assert.DoesNotContain(vm.VisibleSegments, s => s.Preview == "text 1");

        // the earlier tick survived the filter change and is still counted / saveable
        Assert.Contains(vm.Segments, s => s.Preview == "text 1" && s.IsChecked);
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public void Select_and_deselect_all_act_on_the_filtered_set_only()
    {
        var vm = MakeVm("s", MetaWith(), null, Seg(0), Seg(1), Seg(2));
        vm.DeselectAllShown();
        Assert.Equal(0, vm.SelectedCount);

        vm.FilterText = "text 1";
        vm.SelectAllShown();                                       // selects only the filtered match
        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.Segments.Single(s => s.Preview == "text 1").IsChecked);
        Assert.False(vm.Segments.Single(s => s.Preview == "text 0").IsChecked);
    }

    [Fact]
    public void Select_all_skips_disabled_other_stream_lines_and_count_is_enabled_only()
    {
        var vm = MakeVm("s", MetaWith(), null, Seg(0), Seg(1, TranscriptSource.Local));
        Assert.Equal("1 of 1 selected", vm.SelectionSummary);      // Seg(0) checked; Seg(1) disabled, not counted

        vm.DeselectAllShown();
        Assert.Equal("0 of 1 selected", vm.SelectionSummary);

        vm.SelectAllShown();
        Assert.Equal(1, vm.SelectedCount);                         // only the enabled Remote line
        Assert.False(vm.Segments[1].IsChecked);                    // disabled other-stream stays unchecked
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
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) { }
    }
}
