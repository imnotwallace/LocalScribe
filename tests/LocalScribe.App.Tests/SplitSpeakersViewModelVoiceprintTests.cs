using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Voiceprint behaviour of the Split-speakers dialog VM (voiceprint design 2026-07-25,
/// Task 11): matter-pool suggestion chips, accept/dismiss, the opt-in global search, and the
/// confirm-time provenance + enrollment plumbing.
///
/// Every test here drives a QUEUED dispatch fake, not the synchronous <c>a =&gt; a()</c> the older
/// SplitSpeakersViewModelTests uses. RunAsync's publish is a single Dispatcher.BeginInvoke turn
/// that must carry the results, the assignments, the Clusters rows AND their suggestions together;
/// a synchronous fake collapses every dispatch into its call site and would make an
/// out-of-order/second-turn suggestion apply completely invisible (the assistant-surfaces round
/// shipped exactly that class of stamp-ordering bug behind a synchronous fake).</summary>
public sealed class SplitSpeakersViewModelVoiceprintTests : IDisposable
{
    private const string EmbedMethod = "campplus";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_svmvp_{Guid.NewGuid():N}");

    /// <summary>Records dispatched actions and runs them only when explicitly pumped, one turn at
    /// a time - the ordering microscope these tests need.</summary>
    private sealed class QueuedDispatch
    {
        private readonly Queue<Action> _queue = new();
        public Action<Action> Dispatch => a => _queue.Enqueue(a);
        public bool PumpOne()
        {
            if (_queue.Count == 0) return false;
            _queue.Dequeue()();
            return true;
        }
        public void Pump() { while (PumpOne()) { } }
    }

    private sealed class FakeEngine : IDiarisationEngine
    {
        public DiarisationResult Next { get; set; } = new([new DiarisedSegment(0, 2000, 0)], 1, "fake");
        public bool? LastEmitEmbeddings { get; private set; }

        public Task<DiarisationResult> DiariseAsync(DiarisationRequest r, IProgress<double> p, CancellationToken ct)
        {
            LastEmitEmbeddings = r.EmitEmbeddings;
            p.Report(1.0);
            return Task.FromResult(Next);
        }
    }

    // Mirrors SplitSpeakersViewModelTests.MakeFinalizedSession (a finalized Remote-only session
    // with a retained leg), plus the two things this file needs: meta.MatterIds and pre-seeded
    // participant slots (a participant that OWNS "Remote:0" is what forces SpeakersMerge's
    // collision remap in the enrollment test).
    // remoteCount defaults to 2 because a source is only OFFERED when its declared count is > 1;
    // a run that then finds a single cluster just lights the (unused here) count-mismatch panel.
    private (MaintenanceService Svc, StoragePaths Paths, string Id, FakeEngine Engine) MakeSession(
        int remoteCount = 2,
        IReadOnlyList<string>? matterIds = null,
        IReadOnlyList<SessionParticipant>? participants = null)
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Remote],
        }, default).GetAwaiter().GetResult();
        new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            LocalCount = 1,
            RemoteCount = remoteCount,
            MatterIds = matterIds ?? [],
            Participants = participants ?? [],
        }, default).GetAwaiter().GetResult();

        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "hi", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Local, 1000, 2000, "there", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(3, TranscriptSource.Remote, 0, 1000, "hello", "Them"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(4, TranscriptSource.Remote, 1000, 2000, "world", "Them"), default).GetAwaiter().GetResult();
        File.WriteAllBytes(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac), [1, 2, 3]);

        var settings = new FakeSettingsService(new Settings());
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(), TimeProvider.System);
        return (svc, paths, id, new FakeEngine());
    }

    private static SplitSpeakersViewModel MakeVm(
        MaintenanceService svc, StoragePaths paths, FakeEngine engine,
        QueuedDispatch dispatcher, IUiErrorReporter reporter)
    {
        int n = 0;
        return new SplitSpeakersViewModel(
            engine, svc, paths, new FakeSettingsService(new Settings()), reporter,
            dispatcher.Dispatch, TimeProvider.System, fileName => fileName,
            new PeopleStore(paths.PeopleJson),
            async (ids, ct) =>
            {
                var store = new MatterStore(paths.MattersDir);
                var list = new List<Matter>();
                foreach (var matterId in ids)
                    if (await store.LoadAsync(matterId, ct) is { } m) list.Add(m);
                return list;
            },
            new VoiceprintEnrollmentService(paths, TimeProvider.System, () => $"e{++n}"));
    }

    private static Person MakePerson(string id, string name, float[] vector) => new()
    {
        Id = id,
        Name = name,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        Voiceprint =
        [
            new VoiceprintEnrollment
            {
                Id = id + "-seed",
                Embedding = vector,
                Method = EmbedMethod,
                SourceSessionId = "older-session",
                SourceClusterKey = "Remote:0",
                EnrolledAtUtc = DateTimeOffset.UnixEpoch,
            },
        ],
    };

    private static Task SavePeopleAsync(StoragePaths paths, params Person[] people)
        => new PeopleStore(paths.PeopleJson).SaveAsync(new PeopleRegistry { People = people }, default);

    private static Task SaveMatterAsync(StoragePaths paths, string matterId, params RosterMember[] roster)
        => new MatterStore(paths.MattersDir).SaveAsync(new Matter
        {
            Id = matterId,
            Name = matterId,
            DateCreatedUtc = DateTimeOffset.UnixEpoch,
            Roster = roster,
        }, default);

    private static DiarisationResult OneCluster(float[] embedding, bool withEmbeddings = true) =>
        new([new DiarisedSegment(0, 1000, 0)], 1, "fake",
            withEmbeddings ? new Dictionary<string, float[]> { ["0"] = embedding } : null,
            withEmbeddings ? EmbedMethod : null);

    private static DiarisationResult TwoClusters(float[] a, float[] b) =>
        new([new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fake",
            new Dictionary<string, float[]> { ["0"] = a, ["1"] = b }, EmbedMethod);

    private static async Task<SplitSpeakersViewModel> LoadedVmAsync(
        MaintenanceService svc, StoragePaths paths, FakeEngine engine,
        QueuedDispatch dispatcher, IUiErrorReporter reporter)
    {
        var vm = MakeVm(svc, paths, engine, dispatcher, reporter);
        await vm.LoadAsync("s1", default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        return vm;
    }

    [Fact]
    public async Task Run_populates_matter_pool_suggestion_on_row()
    {
        var (svc, paths, id, engine) = MakeSession(matterIds: ["m1"]);
        await SavePeopleAsync(paths, MakePerson("p1", "Sarah Chen", [1f, 0f, 0f]));
        await SaveMatterAsync(paths, "m1", new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p1" });

        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, reporter);
        engine.Next = OneCluster([1f, 0f, 0f]);

        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(engine.LastEmitEmbeddings);   // embeddings are requested from the helper

        // Atomic-publish invariant: no dispatch turn may ever expose a row without its suggestion.
        // Pumping one turn at a time is the whole point of the queued fake.
        while (dispatcher.PumpOne())
            foreach (var row in vm.Clusters)
                Assert.Equal("p1", row.Suggestion?.PersonId);

        var only = Assert.Single(vm.Clusters);
        Assert.Equal("Remote:0", only.ClusterKey);
        Assert.Equal("Sarah Chen", only.Suggestion!.PersonName);
        Assert.Equal(1.0, only.Suggestion.Score, 3);
        Assert.Equal("Remote Speaker 1", only.Name);   // suggest-only: the name is NOT auto-filled
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task No_embeddings_means_no_suggestions_and_no_error()
    {
        var (svc, paths, id, engine) = MakeSession(matterIds: ["m1"]);
        await SavePeopleAsync(paths, MakePerson("p1", "Sarah Chen", [1f, 0f, 0f]));
        await SaveMatterAsync(paths, "m1", new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p1" });

        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, reporter);
        engine.Next = OneCluster([1f, 0f, 0f], withEmbeddings: false);   // old helper / EmitEmbeddings unsupported

        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Null(Assert.Single(vm.Clusters).Suggestion);
        Assert.Empty(reporter.Reports);
        Assert.Empty(reporter.Infos);
    }

    [Fact]
    public async Task Unreadable_people_json_degrades_to_no_suggestions()
    {
        var (svc, paths, id, engine) = MakeSession(matterIds: ["m1"]);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PeopleJson)!);
        await File.WriteAllTextAsync(paths.PeopleJson, "{ not json");

        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, reporter);
        engine.Next = OneCluster([1f, 0f, 0f]);

        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Single(vm.Clusters);                  // the run still published normally
        Assert.Null(vm.Clusters[0].Suggestion);
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task Accept_fills_name_and_clears_chip_and_records_person()
    {
        var (svc, paths, id, engine) = MakeSession(matterIds: ["m1"]);
        await SavePeopleAsync(paths, MakePerson("p1", "Sarah Chen", [1f, 0f, 0f]));
        await SaveMatterAsync(paths, "m1", new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p1" });

        var dispatcher = new QueuedDispatch();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, new FakeUiErrorReporter());
        engine.Next = OneCluster([1f, 0f, 0f]);
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var row = Assert.Single(vm.Clusters);
        row.AcceptSuggestionCommand.Execute(null);

        Assert.Equal("Sarah Chen", row.Name);
        Assert.Equal("p1", row.AcceptedPersonId);
        Assert.Equal(1.0, row.AcceptedScore!.Value, 3);
        Assert.Null(row.Suggestion);

        // Editing the name after accepting BREAKS the person link - provenance and enrollment may
        // only ever describe what the user actually accepted.
        row.Name = "Somebody Else";
        Assert.Null(row.AcceptedPersonId);
        Assert.Null(row.AcceptedScore);
    }

    [Fact]
    public async Task Dismiss_clears_the_chip_without_recording_anything()
    {
        var (svc, paths, id, engine) = MakeSession(matterIds: ["m1"]);
        await SavePeopleAsync(paths, MakePerson("p1", "Sarah Chen", [1f, 0f, 0f]));
        await SaveMatterAsync(paths, "m1", new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p1" });

        var dispatcher = new QueuedDispatch();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, new FakeUiErrorReporter());
        engine.Next = OneCluster([1f, 0f, 0f]);
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var row = Assert.Single(vm.Clusters);
        row.DismissSuggestionCommand.Execute(null);

        Assert.Null(row.Suggestion);
        Assert.Null(row.AcceptedPersonId);
        Assert.Equal("Remote Speaker 1", row.Name);
    }

    [Fact]
    public async Task SearchAllPeople_matches_against_global_registry()
    {
        // The person is on NO matter roster, so the default (matter-pool) pass must stay silent.
        var (svc, paths, id, engine) = MakeSession();
        await SavePeopleAsync(paths, MakePerson("p2", "Global Person", [1f, 0f, 0f]));

        var dispatcher = new QueuedDispatch();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, new FakeUiErrorReporter());
        engine.Next = OneCluster([1f, 0f, 0f]);
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var row = Assert.Single(vm.Clusters);
        Assert.Null(row.Suggestion);         // opt-in only: never reached by the default pass

        await vm.SearchAllPeopleCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal("p2", row.Suggestion!.PersonId);
        Assert.Equal("Remote Speaker 1", row.Name);   // still suggest-only
    }

    [Fact]
    public async Task SearchAllPeople_leaves_accepted_and_already_named_rows_alone()
    {
        var (svc, paths, id, engine) = MakeSession(remoteCount: 2, matterIds: ["m1"]);
        await SavePeopleAsync(paths, MakePerson("p1", "Sarah Chen", [1f, 0f, 0f]));
        await SaveMatterAsync(paths, "m1", new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p1" });

        var dispatcher = new QueuedDispatch();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, new FakeUiErrorReporter());
        engine.Next = TwoClusters([1f, 0f, 0f], [1f, 0f, 0f]);
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(2, vm.Clusters.Count);
        vm.Clusters[0].AcceptSuggestionCommand.Execute(null);      // accepted row
        vm.Clusters[1].DismissSuggestionCommand.Execute(null);
        vm.Clusters[1].Name = "Typed Name";                        // user-named row

        await vm.SearchAllPeopleCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Null(vm.Clusters[0].Suggestion);
        Assert.Equal("Sarah Chen", vm.Clusters[0].Name);
        Assert.Equal("p1", vm.Clusters[0].AcceptedPersonId);
        Assert.Null(vm.Clusters[1].Suggestion);
        Assert.Equal("Typed Name", vm.Clusters[1].Name);
    }

    [Fact]
    public async Task Confirm_passes_provenance_results_and_enrolls_under_the_remapped_key()
    {
        // A participant slot already OWNS "Remote:0", so SpeakersMerge must remap this run's fresh
        // "Remote:0" to "Remote:1" - and every downstream write (names, provenance, embeddings.json,
        // and the enrollment request) has to follow the key that actually landed.
        var (svc, paths, id, engine) = MakeSession(
            matterIds: ["m1"],
            participants:
            [
                new SessionParticipant
                {
                    Id = "pp1", Name = "Someone Else", Side = SourceKind.Remote, ClusterKey = "Remote:0",
                },
            ]);
        await SavePeopleAsync(paths, MakePerson("p1", "Sarah Chen", [1f, 0f, 0f]));
        await SaveMatterAsync(paths, "m1", new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p1" });

        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, reporter);
        engine.Next = OneCluster([1f, 0f, 0f]);
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        vm.Clusters[0].AcceptSuggestionCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Empty(reporter.Reports);

        var speakers = await new SpeakersStore(paths.SpeakersJson(id, TranscriptVersions.Root)).LoadAsync(default);
        Assert.Equal("Sarah Chen", speakers!.Names["Remote:1"]);
        var provenance = speakers.SuggestionProvenance["Remote:1"];
        Assert.Equal("p1", provenance.PersonId);
        Assert.Equal(1.0, provenance.Score, 3);

        // resultsBySource reached the write gate: embeddings.json exists, keyed post-remap.
        var embeddings = await new ClusterEmbeddingsStore(
            paths.EmbeddingsJson(id, TranscriptVersions.Root)).LoadAsync(default);
        Assert.True(embeddings!.Entries.ContainsKey("Remote:1"));

        // The enrollment read the POST-remap key. Had the VM sent the raw "Remote:0", the vector
        // lookup in embeddings.json would have missed and nothing would have enrolled at all.
        var registry = await new PeopleStore(paths.PeopleJson).LoadAsync(default);
        var person = Assert.Single(registry!.People);
        Assert.Equal(2, person.Voiceprint.Count);            // seeded + the new one
        Assert.Equal("Remote:1", person.Voiceprint[^1].SourceClusterKey);
        Assert.Equal(id, person.Voiceprint[^1].SourceSessionId);
    }

    [Fact]
    public async Task Confirm_enrolls_a_roster_linked_name_once_even_with_remember_voice()
    {
        // Orthogonal vector: no suggestion at all, so this exercises the roster-link rule only.
        // RememberVoice is ALSO ticked - the row must still enroll exactly once, to the linked
        // person, and must not mint a duplicate person of the same name.
        var (svc, paths, id, engine) = MakeSession(matterIds: ["m1"]);
        await SavePeopleAsync(paths, MakePerson("p1", "Sarah Chen", [1f, 0f, 0f]));
        await SaveMatterAsync(paths, "m1", new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p1" });

        var dispatcher = new QueuedDispatch();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, new FakeUiErrorReporter());
        engine.Next = OneCluster([0f, 1f, 0f]);
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var row = Assert.Single(vm.Clusters);
        Assert.Null(row.Suggestion);
        row.Name = "Sarah Chen";
        row.RememberVoice = true;

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var registry = await new PeopleStore(paths.PeopleJson).LoadAsync(default);
        var person = Assert.Single(registry!.People);
        Assert.Equal("p1", person.Id);
        Assert.Equal(2, person.Voiceprint.Count);   // seeded + exactly ONE new enrollment
    }

    [Fact]
    public async Task RememberVoice_creates_new_person_on_confirm_but_not_for_a_default_named_row()
    {
        var (svc, paths, id, engine) = MakeSession(remoteCount: 2);

        var dispatcher = new QueuedDispatch();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, new FakeUiErrorReporter());
        engine.Next = TwoClusters([1f, 0f, 0f], [0f, 1f, 0f]);
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        vm.Clusters[0].Name = "New Person";
        vm.Clusters[0].RememberVoice = true;
        vm.Clusters[1].RememberVoice = true;   // still "Remote Speaker 2" - must NOT enroll

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var registry = await new PeopleStore(paths.PeopleJson).LoadAsync(default);
        var person = Assert.Single(registry!.People);
        Assert.Equal("New Person", person.Name);
        Assert.Single(person.Voiceprint);
        Assert.Equal("Remote:0", person.Voiceprint[0].SourceClusterKey);
    }

    [Fact]
    public async Task Confirm_without_any_identity_enrolls_nothing()
    {
        var (svc, paths, id, engine) = MakeSession();

        var dispatcher = new QueuedDispatch();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, new FakeUiErrorReporter());
        engine.Next = OneCluster([1f, 0f, 0f]);
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var speakers = await new SpeakersStore(paths.SpeakersJson(id, TranscriptVersions.Root)).LoadAsync(default);
        Assert.Contains(SourceKind.Remote, speakers!.DiarisedSources);   // the diarisation still committed
        Assert.Empty(speakers.SuggestionProvenance);
        Assert.Null(await new PeopleStore(paths.PeopleJson).LoadAsync(default));   // no registry written
    }

    [Fact]
    public async Task Confirm_does_not_enroll_a_source_that_was_deselected_after_running()
    {
        // Both sides run, then Local is deselected. Local's rows stay in Clusters, but the commit
        // covers Remote only: nothing about Local is re-asserted or re-keyed, and its (pre-existing,
        // deliberately stale) embeddings.json entry is carried over untouched. Enrolling from that
        // row would attach a vector the user never confirmed in this pass.
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));
        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Local, SourceKind.Remote],
        }, default).GetAwaiter().GetResult();
        new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { LocalCount = 2, RemoteCount = 2 }, default).GetAwaiter().GetResult();
        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        await jsonl.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "hi", "Me"), default);
        await jsonl.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Remote, 0, 1000, "hello", "Them"), default);
        File.WriteAllBytes(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), [1, 2, 3]);
        File.WriteAllBytes(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac), [1, 2, 3]);
        // A stale Local:0 vector already on disk - the only thing an out-of-commit enrollment
        // could latch onto.
        await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id, TranscriptVersions.Root)).SaveAsync(
            new ClusterEmbeddings
            {
                Method = EmbedMethod,
                ExtractedAtUtc = DateTimeOffset.UnixEpoch,
                Entries = new Dictionary<string, float[]> { ["Local:0"] = [0f, 0f, 1f] },
            }, default);

        var svc = new MaintenanceService(paths, new FakeSettingsService(new Settings()),
            new FakeRecycleBin(), TimeProvider.System);
        var engine = new FakeEngine();
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher, new FakeUiErrorReporter());
        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Equal(SourceKind.Local, vm.Sources[0].Source);
        vm.Sources[0].Selected = true;
        vm.Sources[1].Selected = true;
        engine.Next = OneCluster([1f, 0f, 0f]);
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var localRow = vm.Clusters.Single(c => c.Source == SourceKind.Local);
        localRow.Name = "Deselected Person";
        localRow.RememberVoice = true;
        vm.Sources[0].Selected = false;   // Local drops out of the commit

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Null(await new PeopleStore(paths.PeopleJson).LoadAsync(default));
    }

    [Fact]
    public async Task Accept_and_a_later_edit_both_raise_PropertyChanged_for_binding_visible_state()
    {
        // Task 12 review fix (Finding 2): AcceptedPersonId/AcceptedScore/IsDefaultNamed drive the
        // XAML "linked" indicator and the Remember-voice enable state. Before the fix these raised
        // no PropertyChanged at all, so a WPF binding would show a stale "linked" badge after the
        // link was broken by an edit - a silent UI bug this test pins directly against the row's
        // own change-notification stream (no window needed).
        var (svc, paths, id, engine) = MakeSession(matterIds: ["m1"]);
        await SavePeopleAsync(paths, MakePerson("p1", "Sarah Chen", [1f, 0f, 0f]));
        await SaveMatterAsync(paths, "m1", new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p1" });

        var dispatcher = new QueuedDispatch();
        var vm = await LoadedVmAsync(svc, paths, engine, dispatcher, new FakeUiErrorReporter());
        engine.Next = OneCluster([1f, 0f, 0f]);
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var row = Assert.Single(vm.Clusters);
        Assert.True(row.IsDefaultNamed);

        var changed = new List<string>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        row.AcceptSuggestionCommand.Execute(null);
        Assert.Contains(nameof(ClusterRowViewModel.AcceptedPersonId), changed);
        Assert.Contains(nameof(ClusterRowViewModel.AcceptedScore), changed);
        Assert.Contains(nameof(ClusterRowViewModel.IsDefaultNamed), changed);   // Name flipped off its default
        Assert.False(row.IsDefaultNamed);

        changed.Clear();
        row.Name = "Somebody Else";   // breaks the link
        Assert.Null(row.AcceptedPersonId);
        Assert.Null(row.AcceptedScore);
        Assert.Contains(nameof(ClusterRowViewModel.AcceptedPersonId), changed);
        Assert.Contains(nameof(ClusterRowViewModel.AcceptedScore), changed);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
