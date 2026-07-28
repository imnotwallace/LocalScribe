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

/// <summary>Split-speakers hydration (design 2026-07-28 task 7): reopening the dialog on an
/// already-diarised session must rebuild the naming rows from the committed speakers.json with NO
/// engine call, and confirming a rename on those rows must go through
/// MaintenanceService.RenameSpeakersAsync rather than the full SaveDiarisationAsync/SpeakersMerge
/// commit path.
///
/// Every test here drives the canonical QUEUED dispatch fake, not the synchronous <c>a =&gt; a()</c>
/// SplitSpeakersViewModelTests uses. Hydration publishes rows AND their voiceprint chips inside
/// LoadAsync's single <c>_dispatch(() =&gt; Apply(...))</c> turn; a synchronous fake collapses every
/// dispatch into its call site and would make a second-turn stamp completely invisible (the
/// assistant-surfaces round shipped exactly that class of ordering bug behind a synchronous
/// fake).</summary>
public sealed class SplitSpeakersHydrationTests : IDisposable
{
    private const string EmbedMethod = "campplus";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_svmhy_{Guid.NewGuid():N}");

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

    /// <summary><see cref="Calls"/> is the whole point of this file: hydration must reach ZERO
    /// engine invocations. A test that only counted Clusters would pass against the pre-hydration
    /// behaviour the moment someone pressed Run.</summary>
    private sealed class FakeEngine : IDiarisationEngine
    {
        public int Calls { get; private set; }
        public DiarisationResult Next { get; set; } = new([new DiarisedSegment(0, 2000, 0)], 1, "fake");
        /// <summary>Set to make the next run behave like a user-pressed Cancel (RunAsync swallows
        /// OperationCanceledException and publishes nothing at all).</summary>
        public bool CancelNext { get; set; }

        public Task<DiarisationResult> DiariseAsync(DiarisationRequest r, IProgress<double> p, CancellationToken ct)
        {
            Calls++;
            if (CancelNext) throw new OperationCanceledException();
            p.Report(1.0);
            return Task.FromResult(Next);
        }
    }

    // Mirrors SplitSpeakersViewModelTests.MakeFinalizedSession (:39-72), plus meta.MatterIds for
    // the hydrated-suggestion test. Remote seq 3 = "hello" @0ms, seq 4 = "world" @1000ms; Local
    // seq 1 = "hi" @0ms, seq 2 = "there" @1000ms.
    private (MaintenanceService Svc, StoragePaths Paths, string Id, FakeEngine Engine) MakeFinalizedSession(
        int remoteCount, IReadOnlyList<SourceKind> retained, int localCount = 1,
        IReadOnlyList<string>? matterIds = null)
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = retained,
        }, default).GetAwaiter().GetResult();
        new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            LocalCount = localCount,
            RemoteCount = remoteCount,
            MatterIds = matterIds ?? [],
        }, default).GetAwaiter().GetResult();

        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "hi", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Local, 1000, 2000, "there", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(3, TranscriptSource.Remote, 0, 1000, "hello", "Them"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(4, TranscriptSource.Remote, 1000, 2000, "world", "Them"), default).GetAwaiter().GetResult();
        if (retained.Contains(SourceKind.Remote))
            File.WriteAllBytes(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac), [1, 2, 3]);
        if (retained.Contains(SourceKind.Local))
            File.WriteAllBytes(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), [1, 2, 3]);

        var svc = new MaintenanceService(paths, new FakeSettingsService(new Settings()),
            new FakeRecycleBin(), TimeProvider.System);
        return (svc, paths, id, new FakeEngine());
    }

    private static SplitSpeakersViewModel MakeVm(
        MaintenanceService svc, StoragePaths paths, FakeEngine engine, QueuedDispatch dispatcher,
        IUiErrorReporter? reporter = null)
    {
        int n = 0;
        return new SplitSpeakersViewModel(
            engine, svc, paths, new FakeSettingsService(new Settings()),
            reporter ?? new FakeUiErrorReporter(),
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

    /// <summary>Seeds an already-committed diarisation the way the import-time detection step
    /// leaves one: two clusters with default labels, assignments over the two Remote segments.</summary>
    private static Task SeedCommittedDiarisationAsync(StoragePaths paths, string id)
        => new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(new Speakers
        {
            Names = new Dictionary<string, string>
            { ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["3"] = "Remote:0", ["4"] = "Remote:1" } },
            DiarisedSources = [SourceKind.Remote],
            Method = "sherpa",
            DiarisedAtUtc = DateTimeOffset.UnixEpoch,
        }, default);

    /// <summary>Both sides committed - the only shape in which "one source ran, the other is still
    /// hydrated" could ever be attempted.</summary>
    private static Task SeedBothSidesCommittedAsync(StoragePaths paths, string id)
        => new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(new Speakers
        {
            Names = new Dictionary<string, string>
            {
                ["Local:0"] = "Local Speaker 1", ["Local:1"] = "Local Speaker 2",
                ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2",
            },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            {
                ["Local"] = new() { ["1"] = "Local:0", ["2"] = "Local:1" },
                ["Remote"] = new() { ["3"] = "Remote:0", ["4"] = "Remote:1" },
            },
            DiarisedSources = [SourceKind.Local, SourceKind.Remote],
            Method = "sherpa",
            DiarisedAtUtc = DateTimeOffset.UnixEpoch,
        }, default);

    private static Task SeedEmbeddingsAsync(StoragePaths paths, string id, params (string Key, float[] Vector)[] entries)
        => new ClusterEmbeddingsStore(paths.EmbeddingsJson(id)).SaveAsync(new ClusterEmbeddings
        {
            Method = EmbedMethod,
            ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = entries.ToDictionary(e => e.Key, e => e.Vector, StringComparer.Ordinal),
        }, default);

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

    [Fact]
    public async Task Load_populates_clusters_from_disk_without_calling_the_engine()
    {
        // THE regression this task exists to prevent. Before hydration, Clusters was populated only
        // inside RunAsync's publish dispatch, so reopening the dialog to rename a speaker re-ran the
        // whole diarisation - minutes of CPU to type a name. Asserting the ABSENCE of an engine call
        // is the point; a test that only checked Clusters.Count would pass against the old behaviour
        // the moment someone pressed Run.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Equal(0, engine.Calls);
        Assert.Equal(2, vm.Clusters.Count);
        Assert.Equal("Remote Speaker 1", vm.Clusters[0].Name);
        Assert.Equal("Remote:0", vm.Clusters[0].ClusterKey);
        Assert.Equal(SourceKind.Remote, vm.Clusters[0].Source);
    }

    [Fact]
    public async Task Hydrated_rows_carry_previews_and_a_snippet_offset()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        // MakeFinalizedSession seeds Remote seq 3 = "hello" @0ms and seq 4 = "world" @1000ms. A
        // hydrated row has no DiarisedSegment list, so SnippetStartMs has to come from the earliest
        // assigned transcript line - which is what the play button seeks to.
        Assert.Contains("hello", vm.Clusters[0].PreviewLines);
        Assert.Equal(0, vm.Clusters[0].SnippetStartMs);
        Assert.Contains("world", vm.Clusters[1].PreviewLines);
        Assert.Equal(1000, vm.Clusters[1].SnippetStartMs);
    }

    [Fact]
    public async Task A_hydrated_rename_persists_without_running_the_engine()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        vm.Clusters[0].Name = "Sarah Chen";

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(0, engine.Calls);
        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Sarah Chen", s!.Names["Remote:0"]);
        Assert.Equal("Remote Speaker 2", s.Names["Remote:1"]);   // untouched row keeps its label
    }

    [Fact]
    public async Task A_hydrated_rename_does_not_restamp_the_diarisation()
    {
        // The whole reason a rename must not travel through SaveDiarisationAsync: that path stamps
        // Method/DiarisedAtUtc from the commit and re-runs SpeakersMerge over keys it treats as
        // FRESH. Nothing about the run changed, so nothing about the run may be rewritten.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        vm.Clusters[0].Name = "Sarah Chen";

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal(DateTimeOffset.UnixEpoch, s!.DiarisedAtUtc);
        Assert.Equal("sherpa", s.Method);
        Assert.Equal("Remote:0", s.Assignments["Remote"]["3"]);
    }

    [Fact]
    public async Task A_hydrated_rename_leaves_embeddings_json_untouched()
    {
        // Locked invariant: a hydrated row carries no vectors, so a rename must never re-derive
        // embeddings.json - it would either wipe the entries or rewrite them from nothing. The
        // ClusterEmbeddings contract (ClusterEmbeddings.cs:3-7) is that an entry can never point at
        // a different voice than speakers.json does, and only a real run can re-establish that.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        await SeedEmbeddingsAsync(paths, id, ("Remote:0", [1f, 0f, 0f]), ("Remote:1", [0f, 1f, 0f]));
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        vm.Clusters[0].Name = "Sarah Chen";

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var embeddings = await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id)).LoadAsync(default);
        Assert.Equal(2, embeddings!.Entries.Count);
        Assert.Equal([1f, 0f, 0f], embeddings.Entries["Remote:0"]);
        Assert.Equal(EmbedMethod, embeddings.Method);
        Assert.Equal(DateTimeOffset.UnixEpoch, embeddings.ExtractedAtUtc);
    }

    [Fact]
    public async Task A_fresh_run_after_hydration_still_uses_the_full_commit_path()
    {
        // Hydration must not turn a real re-diarise into a rename: a fresh run has segments and
        // embeddings, and its commit has to go through SaveDiarisationAsync/SpeakersMerge.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fresh-run");

        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();
        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(1, engine.Calls);
        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("fresh-run", s!.Method);   // restamped, unlike a rename
    }

    [Fact]
    public async Task A_cancelled_run_after_hydration_confirms_as_a_rename()
    {
        // load -> Run -> cancel is the one sequence where a run STARTED but published nothing.
        // RunAsync accumulates into locals and only swaps _resultBySource inside its publish turn
        // (SplitSpeakersViewModel.cs:631), so a cancel leaves the hydrated state exactly as loaded -
        // and the confirm must still be a rename, not a commit of a run that never finished.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        engine.CancelNext = true;

        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(1, engine.Calls);
        Assert.Equal(2, vm.Clusters.Count);          // the hydrated rows survived the cancel
        vm.Clusters[0].Name = "Sarah Chen";

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Sarah Chen", s!.Names["Remote:0"]);
        Assert.Equal("sherpa", s.Method);            // rename path: nothing restamped
        Assert.Equal(DateTimeOffset.UnixEpoch, s.DiarisedAtUtc);
    }

    [Fact]
    public async Task An_undiarised_session_hydrates_nothing()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Empty(vm.Clusters);
        Assert.Single(vm.Sources);              // still offered, so Run is available
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task Hydrated_rows_are_never_visible_without_their_state()
    {
        // Atomic-publish invariant: pump one turn at a time and assert no turn ever exposes a
        // half-built Clusters collection. Hydration publishes inside LoadAsync's single
        // _dispatch(() => Apply(loaded)) turn - keep it that way.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        while (dispatcher.PumpOne())
            Assert.True(vm.Clusters.Count is 0 or 2);
        Assert.Equal(2, vm.Clusters.Count);
    }

    [Fact]
    public async Task Confirm_is_still_refused_when_a_selected_source_was_never_run_or_hydrated()
    {
        // The precondition at SplitSpeakersViewModel.cs:843 must survive: a selected source with no
        // assignment must not persist an incomplete "diarised" commit.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Local, SourceKind.Remote], localCount: 2);
        await SeedCommittedDiarisationAsync(paths, id);   // Remote only
        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = MakeVm(svc, paths, engine, dispatcher, reporter);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        foreach (var s in vm.Sources) s.Selected = true;   // Local has no assignment

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.False(speakers!.Names.ContainsKey("Local:0"));
        Assert.NotEmpty(reporter.Infos);
    }

    [Fact]
    public async Task A_run_replaces_hydrated_state_wholesale_so_a_mixed_confirm_is_unreachable()
    {
        // The rename-only discriminator is "no selected source has a DiarisationResult". That is
        // only sound because a run and a hydration can never coexist: RunAsync REPLACES
        // _resultBySource AND _assignmentBySource wholesale and rebuilds Clusters from scratch
        // (SplitSpeakersViewModel.cs:631-638), so running one side of a two-side hydration discards
        // the other side's hydrated rows rather than leaving a half-fresh/half-hydrated commit that
        // SpeakersMerge would then remap a pinned key out from under.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Local, SourceKind.Remote], localCount: 2);
        await SeedBothSidesCommittedAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = MakeVm(svc, paths, engine, dispatcher, reporter);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        Assert.Equal(4, vm.Clusters.Count);          // both sides hydrated

        vm.Sources[1].Selected = true;               // Remote only
        engine.Next = new DiarisationResult(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fresh-run");
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        // Local's hydrated rows are gone: the run rebuilt Clusters from its own results only.
        Assert.Equal(2, vm.Clusters.Count);
        Assert.All(vm.Clusters, c => Assert.Equal(SourceKind.Remote, c.Source));

        // ...so selecting Local as well now hits the never-run precondition rather than smuggling a
        // hydrated source into a fresh commit.
        vm.Sources[0].Selected = true;
        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.NotEmpty(reporter.Infos);
        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("sherpa", s!.Method);           // refused: nothing was committed at all
    }

    [Fact]
    public async Task A_hydrated_rename_re_asserts_nothing_about_a_deselected_source()
    {
        // Confirm means the same thing on both paths: "re-assert the SELECTED sources, and leave a
        // deselected source's speakers.json names exactly as they were" (the rule documented at
        // SplitSpeakersViewModel.cs:894-898, enforced on the fresh path by SpeakersMerge's reSources
        // filter). RenameSpeakersAsync applies no source filter of its own - it renames any key the
        // overlay already has - so the VM has to scope the map it hands over. Without that, a
        // deselected source's row could land a name while its provenance and enrollment (which ARE
        // scoped) did not, leaving an accepted machine suggestion indistinguishable from a
        // hand-typed name (Speakers.cs:19-21 forbids exactly that).
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Local, SourceKind.Remote], localCount: 2);
        await SeedBothSidesCommittedAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Equal(SourceKind.Local, vm.Sources[0].Source);
        vm.Sources[1].Selected = true;                                   // Remote only
        vm.Clusters.Single(c => c.ClusterKey == "Remote:0").Name = "Sarah Chen";
        vm.Clusters.Single(c => c.ClusterKey == "Local:0").Name = "Not Committed";

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Sarah Chen", s!.Names["Remote:0"]);
        Assert.Equal("Local Speaker 1", s.Names["Local:0"]);
    }

    [Fact]
    public async Task Hydrated_rows_carry_voiceprint_suggestions_from_the_persisted_embeddings()
    {
        // embeddings.json is keyed by the FULL post-remap clusterKey ("Remote:0") and carries its
        // own Method (ClusterEmbeddings.cs:3-7), unlike a run's DiarisationResult.ClusterEmbeddings
        // (bare "0"), so the hydrated pass hands the persisted entries to VoiceprintMatcher with NO
        // key composition at all. Still suggest-only: the chip appears, the name does not change.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Remote], matterIds: ["m1"]);
        await SeedCommittedDiarisationAsync(paths, id);
        await SeedEmbeddingsAsync(paths, id, ("Remote:0", [1f, 0f, 0f]));
        await new PeopleStore(paths.PeopleJson).SaveAsync(
            new PeopleRegistry { People = [MakePerson("p1", "Sarah Chen", [1f, 0f, 0f])] }, default);
        await new MatterStore(paths.MattersDir).SaveAsync(new Matter
        {
            Id = "m1",
            Name = "m1",
            DateCreatedUtc = DateTimeOffset.UnixEpoch,
            Roster = [new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p1" }],
        }, default);

        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = MakeVm(svc, paths, engine, dispatcher, reporter);

        await vm.LoadAsync(id, default);
        // Atomic publish: no turn may ever expose a row without its chip already stamped on.
        while (dispatcher.PumpOne())
            foreach (var row in vm.Clusters)
                Assert.Equal(row.ClusterKey == "Remote:0" ? "p1" : null, row.Suggestion?.PersonId);

        Assert.Equal(0, engine.Calls);
        Assert.Equal("p1", vm.Clusters[0].Suggestion!.PersonId);
        Assert.Equal("Sarah Chen", vm.Clusters[0].Suggestion!.PersonName);
        Assert.Equal("Remote Speaker 1", vm.Clusters[0].Name);   // suggest-only: never auto-filled
        Assert.Null(vm.Clusters[1].Suggestion);                  // no vector on disk for Remote:1
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task Missing_embeddings_hydrate_rows_with_no_chips_and_no_error()
    {
        // Suggestions are advisory (SplitSpeakersViewModel.cs:73-76): a session diarised before
        // embeddings existed still has to hydrate its naming rows normally.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Remote], matterIds: ["m1"]);
        await SeedCommittedDiarisationAsync(paths, id);
        await new PeopleStore(paths.PeopleJson).SaveAsync(
            new PeopleRegistry { People = [MakePerson("p1", "Sarah Chen", [1f, 0f, 0f])] }, default);

        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = MakeVm(svc, paths, engine, dispatcher, reporter);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Equal(2, vm.Clusters.Count);
        Assert.All(vm.Clusters, c => Assert.Null(c.Suggestion));
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task Remember_voice_enrolls_a_hydrated_row_even_when_no_name_changed()
    {
        // Enrollment must not ride on whether speakers.json happened to change. Reopening a session
        // whose speakers were named in an EARLIER confirm and ticking "Remember voice" is a
        // confirm-time consent act in its own right, and the rename write path reports "wrote
        // nothing" for it (the name is already correct) - so gating enrollment on that flag would
        // silently drop the one thing the user actually asked for. The fresh path has no such
        // gate: SaveDiarisationAsync always writes, so it always enrolls.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(new Speakers
        {
            Names = new Dictionary<string, string>
            { ["Remote:0"] = "Sarah Chen", ["Remote:1"] = "Remote Speaker 2" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["3"] = "Remote:0", ["4"] = "Remote:1" } },
            DiarisedSources = [SourceKind.Remote],
            Method = "sherpa",
            DiarisedAtUtc = DateTimeOffset.UnixEpoch,
        }, default);
        await SeedEmbeddingsAsync(paths, id, ("Remote:0", [1f, 0f, 0f]));

        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Equal("Sarah Chen", vm.Clusters[0].Name);     // the committed name beat the default label
        Assert.False(vm.Clusters[0].IsDefaultNamed);
        vm.Sources[0].Selected = true;
        vm.Clusters[0].RememberVoice = true;                 // the ONLY change in this confirm

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var registry = await new PeopleStore(paths.PeopleJson).LoadAsync(default);
        // Named explicitly: gating the enrollment on the rename write path's "wrote" flag leaves no
        // people.json at all, and a bare null-forgiving deref would report that as a
        // NullReferenceException rather than as the dropped consent act it is.
        Assert.NotNull(registry);
        var person = Assert.Single(registry.People);
        Assert.Equal("Sarah Chen", person.Name);
        Assert.Equal("Remote:0", Assert.Single(person.Voiceprint).SourceClusterKey);
        // ...and the still-default-labelled row enrolled nothing: a "Remote Speaker 2" voiceprint
        // identifies nobody.
        Assert.Equal("sherpa", (await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default))!.Method);
    }

    [Fact]
    public async Task SearchAllPeople_matches_hydrated_rows_against_the_persisted_embeddings()
    {
        // Hydration ENABLES this button where it used to be dead: CanSearchAllPeople only asks for
        // Clusters.Count > 0, which was never true on a hydrated load before this task. Matching
        // still fanned out over _resultBySource, which hydration deliberately leaves empty - so the
        // opt-in global search would have been clickable and silently done nothing.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        await SeedEmbeddingsAsync(paths, id, ("Remote:0", [1f, 0f, 0f]));
        // On NO matter roster, so the default (matter-scoped) hydrated pass must stay silent.
        await new PeopleStore(paths.PeopleJson).SaveAsync(
            new PeopleRegistry { People = [MakePerson("p2", "Global Person", [1f, 0f, 0f])] }, default);

        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        Assert.Null(vm.Clusters[0].Suggestion);          // opt-in only: never reached by default

        await vm.SearchAllPeopleCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(0, engine.Calls);
        Assert.Equal("p2", vm.Clusters[0].Suggestion!.PersonId);
        Assert.Equal("Remote Speaker 1", vm.Clusters[0].Name);   // still suggest-only
        Assert.Null(vm.Clusters[1].Suggestion);                  // no vector on disk for Remote:1
    }

    [Fact]
    public async Task A_fresh_runs_vectors_beat_the_persisted_ones_in_a_global_search()
    {
        // Cluster ids restart at 0 every run (THE REMAP RULE), so once a run has republished the
        // rows the persisted entries can be describing a completely different voice under the same
        // "Remote:0" key. The run's own results must win.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        await SeedEmbeddingsAsync(paths, id, ("Remote:0", [1f, 0f, 0f]));   // the OLD voice
        await new PeopleStore(paths.PeopleJson).SaveAsync(
            new PeopleRegistry { People = [MakePerson("p2", "Global Person", [1f, 0f, 0f])] }, default);

        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(
            [new DiarisedSegment(0, 1000, 0)], 1, "fresh-run",
            new Dictionary<string, float[]> { ["0"] = [0f, 1f, 0f] }, EmbedMethod);   // a DIFFERENT voice
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        await vm.SearchAllPeopleCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var row = Assert.Single(vm.Clusters);
        Assert.Equal("Remote:0", row.ClusterKey);   // same key, different human
        Assert.Null(row.Suggestion);
    }

    [Fact]
    public async Task Nothing_enrolls_and_no_provenance_is_written_until_the_user_confirms()
    {
        // Confirm remains the voiceprint consent gate. Hydration surfaces a chip from data the
        // import wrote automatically, so merely OPENING the dialog must record nothing at all.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Remote], matterIds: ["m1"]);
        await SeedCommittedDiarisationAsync(paths, id);
        await SeedEmbeddingsAsync(paths, id, ("Remote:0", [1f, 0f, 0f]));
        await new PeopleStore(paths.PeopleJson).SaveAsync(
            new PeopleRegistry { People = [MakePerson("p1", "Sarah Chen", [1f, 0f, 0f])] }, default);
        await new MatterStore(paths.MattersDir).SaveAsync(new Matter
        {
            Id = "m1",
            Name = "m1",
            DateCreatedUtc = DateTimeOffset.UnixEpoch,
            Roster = [new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p1" }],
        }, default);

        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        var registry = await new PeopleStore(paths.PeopleJson).LoadAsync(default);
        Assert.Single(Assert.Single(registry!.People).Voiceprint);   // the seeded enrollment ONLY
        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Empty(s!.SuggestionProvenance);

        // Accepting the chip and confirming IS the consent act - now provenance lands, keyed to the
        // clusterKey that was already on disk (no remap: nothing was re-keyed).
        vm.Clusters[0].AcceptSuggestionCommand.Execute(null);
        vm.Sources[0].Selected = true;
        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Sarah Chen", s!.Names["Remote:0"]);
        Assert.Equal("p1", s.SuggestionProvenance["Remote:0"].PersonId);
        registry = await new PeopleStore(paths.PeopleJson).LoadAsync(default);
        Assert.Equal(2, Assert.Single(registry!.People).Voiceprint.Count);
        Assert.Equal("Remote:0", registry.People[0].Voiceprint[^1].SourceClusterKey);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
