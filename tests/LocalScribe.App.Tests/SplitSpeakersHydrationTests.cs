using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Projection;
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
        // Lock-guarded: this stands in for WPF's Dispatcher, whose BeginInvoke is thread-safe from
        // any thread. A fire-and-forget load's pool-thread continuation can enqueue here while the
        // test thread is inside Pump/PumpOne; a plain Queue<Action> corrupts under that concurrent
        // access. Dequeue under the lock, invoke outside it so a re-entrant dispatch cannot deadlock.
        private readonly object _gate = new();
        private readonly Queue<Action> _queue = new();
        public Action<Action> Dispatch => a => { lock (_gate) _queue.Enqueue(a); };
        public bool PumpOne()
        {
            Action next;
            lock (_gate)
            {
                if (_queue.Count == 0) return false;
                next = _queue.Dequeue();
            }
            next();
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
        MaintenanceService svc, StoragePaths paths, FakeEngine engine, QueuedDispatch dispatcher)
    {
        int n = 0;
        return new SplitSpeakersViewModel(
            engine, svc, paths, new FakeSettingsService(new Settings()),
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
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        foreach (var s in vm.Sources) s.Selected = true;   // Local has no assignment

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.False(speakers!.Names.ContainsKey("Local:0"));
        // The refusal surfaces in THIS dialog's own status (2026-08-02 smoke: the shared reporter
        // renders on MainWindow's InfoBar, invisible from this separate window).
        Assert.NotNull(vm.StatusMessage);
        Assert.False(vm.StatusIsError);
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
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        Assert.Equal(4, vm.Clusters.Count);          // both sides hydrated

        // Task 3 auto-selects every hydrated source (both, here); deselect Local so only Remote runs.
        vm.Sources[0].Selected = false;
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

        Assert.NotNull(vm.StatusMessage);            // refusal shown in the dialog itself
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
        // Task 3 auto-selects every hydrated source (both, here); deselect Local so only Remote is ticked.
        vm.Sources[0].Selected = false;
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
        var vm = MakeVm(svc, paths, engine, dispatcher);

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
        Assert.False(vm.StatusIsError);
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
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Equal(2, vm.Clusters.Count);
        Assert.All(vm.Clusters, c => Assert.Null(c.Suggestion));
        Assert.False(vm.StatusIsError);
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

    [Fact]
    public async Task A_pins_only_session_hydrates_no_rows()
    {
        // Fix round 1, I1. EditStore.ReassignSpeakersAsync writes speakers.Assignments[source][seq]
        // for a MANUAL PIN - no diarisation, no Names entry, no DiarisedSources
        // (EditStore.cs:120-130). Keying hydration off Assignments alone built phantom rows here:
        // labelled with materialised defaults that contradict the read view, passing the never-run
        // precondition that used to refuse the confirm, and then taking the rename path where every
        // key absent from Names is skipped - so the confirm wrote nothing and said nothing.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["3"] = "Remote:0" } },
            Pinned = new Dictionary<string, List<string>> { ["Remote"] = ["3"] },
            // No Names, no DiarisedSources, no Method: this is a pin, not a diarisation.
        }, default);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Empty(vm.Clusters);
        Assert.Single(vm.Sources);        // still offered, so Run is available
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task A_diarised_source_that_also_carries_pins_still_hydrates()
    {
        // The other half of I1: the gate must key on DiarisedSources, not on "has no pins". A
        // partly-pinned DIARISED source is completely ordinary and must still hydrate every row.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(new Speakers
        {
            Names = new Dictionary<string, string>
            { ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["3"] = "Remote:0", ["4"] = "Remote:1" } },
            Pinned = new Dictionary<string, List<string>> { ["Remote"] = ["3"] },
            DiarisedSources = [SourceKind.Remote],
            Method = "sherpa",
            DiarisedAtUtc = DateTimeOffset.UnixEpoch,
        }, default);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Equal(2, vm.Clusters.Count);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task Confirm_is_disabled_until_a_source_is_ticked()
    {
        // Fix round 1, I2. ConfirmAsync returns without saving when nothing is ticked. Task 3
        // auto-selects the hydrated source on open, so this test explicitly deselects it to exercise
        // "nothing ticked" directly, then re-ticks it via the checkbox to prove CanConfirm is still
        // re-poked when the per-source checkbox moves (which mutates SplitSourceOption.Selected, not a
        // VM property), not merely on load.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.Equal(2, vm.Clusters.Count);
        Assert.True(vm.ConfirmCommand.CanExecute(null));    // task 3: hydration auto-selected the source
        vm.Sources[0].Selected = false;
        Assert.False(vm.ConfirmCommand.CanExecute(null));   // rows, but nothing ticked

        bool raised = false;
        vm.ConfirmCommand.CanExecuteChanged += (_, _) => raised = true;
        vm.Sources[0].Selected = true;

        Assert.True(raised);                                // the checkbox re-poked the command
        Assert.True(vm.ConfirmCommand.CanExecute(null));
        vm.Sources[0].Selected = false;
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_rename_that_changes_nothing_says_so_instead_of_sitting_silent()
    {
        // Fix round 1, I3. RenameSpeakersAsync legitimately returns false when the names on disk
        // already match, and the DiarisationSaved event is gated on that - so without an Info the
        // user presses Confirm and gets no event, no message and a dialog that just sits there.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;

        bool saved = false;
        vm.DiarisationSaved += _ => saved = true;
        await vm.ConfirmCommand.ExecuteAsync(null);   // nothing was edited
        dispatcher.Pump();

        Assert.False(saved);
        // The acknowledgment must be visible in THIS window (2026-08-02 smoke: it went to
        // MainWindow's InfoBar and Confirm/Save looked like a dead button).
        Assert.Equal("Speaker names were already up to date.", vm.StatusMessage);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public async Task A_rename_enrollment_failure_survives_the_acknowledgment()
    {
        // Review fix: on the rename-only path too, the trailing acknowledgment must not
        // overwrite the voiceprint-failure status EnrollConfirmedVoicesAsync just showed.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        await SeedEmbeddingsAsync(paths, id, ("Remote:0", [1f, 0f, 0f]));
        Directory.CreateDirectory(paths.PeopleJson);   // people.json save will throw
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        vm.Clusters[0].Name = "Sarah Chen";            // typed name +
        vm.Clusters[0].RememberVoice = true;           // consent -> enrollment attempted

        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.True(vm.StatusIsError);
        Assert.Contains("Voiceprints could not be saved", vm.StatusMessage);
    }

    [Fact]
    public async Task Confirming_with_no_source_ticked_says_so()
    {
        // ExecuteAsync bypasses CanExecute, which is exactly how this state reached the silent
        // early return before I2 gated the button. Task 3 auto-selects the hydrated source on open,
        // so this test explicitly deselects it to still exercise the no-source-ticked path.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = false;
        vm.Clusters[0].Name = "Sarah Chen";

        await vm.ConfirmCommand.ExecuteAsync(null);   // no source ticked
        dispatcher.Pump();

        Assert.NotNull(vm.StatusMessage);             // said in the dialog, not on MainWindow
        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Remote Speaker 1", s!.Names["Remote:0"]);   // nothing was written
    }

    [Fact]
    public async Task Hydration_reads_the_ACTIVE_versions_overlay_not_the_session_root()
    {
        // Fix round 1, M4 - invariant 3 was unpinned. Every other test here runs on the root
        // pseudo-version, where SpeakersJson(id) and SpeakersJson(id, ActiveVersion) resolve to the
        // SAME file, so swapping hydration to the 1-arg overload would have passed all of them.
        // That is precisely the class of defect the F1 fix exists for: a re-transcription that
        // moved ActiveVersion must not leave the dialog naming clusters out of a stale overlay -
        // and _versionId, which the write path commits to, comes from that same ActiveVersion.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        // The root overlay is a decoy: it must never be the one that hydrates.
        await SeedCommittedDiarisationAsync(paths, id);

        const string v2 = "v2-base.en-2026-07-28";
        var store = new SessionStore(paths.SessionJson(id));
        var session = await store.ReadAsync(default);
        await store.SaveAsync(session! with
        {
            ActiveVersion = v2,
            Versions = [new TranscriptVersion { Id = v2, Model = "base.en", CreatedAtUtc = DateTimeOffset.UnixEpoch }],
        }, default);

        Directory.CreateDirectory(paths.VersionDir(id, v2));
        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id, v2));
        await jsonl.AppendAsync(TranscriptLine.Segment(7, TranscriptSource.Remote, 0, 1000, "v2 line", "Them"), default);
        await new SpeakersStore(paths.SpeakersJson(id, v2)).SaveAsync(new Speakers
        {
            Names = new Dictionary<string, string> { ["Remote:0"] = "V2 Speaker" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["7"] = "Remote:0" } },
            DiarisedSources = [SourceKind.Remote],
            Method = "sherpa",
            DiarisedAtUtc = DateTimeOffset.UnixEpoch,
        }, default);

        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        var row = Assert.Single(vm.Clusters);              // v2 has ONE cluster; the root has two
        Assert.Equal("V2 Speaker", row.Name);
        Assert.Contains("v2 line", row.PreviewLines);

        // ...and the rename lands in v2's overlay, leaving the root's alone.
        vm.Sources[0].Selected = true;
        row.Name = "Sarah Chen";
        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        var v2Speakers = await new SpeakersStore(paths.SpeakersJson(id, v2)).LoadAsync(default);
        Assert.Equal("Sarah Chen", v2Speakers!.Names["Remote:0"]);
        var rootSpeakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Remote Speaker 1", rootSpeakers!.Names["Remote:0"]);
    }

    [Fact]
    public async Task A_Session_Details_rename_after_diarisation_survives_a_hydrated_confirm()
    {
        // design 2026-07-29 follow-up 1 - the reviewer's deterministic repro. Local was diarised and
        // participant p1 owns Local:0. p1 is then renamed in Session Details, so meta.Name
        // ("Sarah Chen-Smith") diverges from the speakers.json overlay ("Sarah Chen") while ownership
        // (ClusterKey) stays intact - the read view already renders the new name via the owner tier.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 1, retained: [SourceKind.Local], localCount: 2);

        await new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            LocalCount = 2,
            Participants =
            [
                new SessionParticipant
                { Id = "p1", Name = "Sarah Chen-Smith", Side = SourceKind.Local, ClusterKey = "Local:0" },
            ],
        }, default);
        await new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(new Speakers
        {
            Names = new Dictionary<string, string>
            { ["Local:0"] = "Sarah Chen", ["Local:1"] = "Local Speaker 2" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Local"] = new() { ["1"] = "Local:0", ["2"] = "Local:1" } },
            DiarisedSources = [SourceKind.Local],
            Method = "sherpa",
            DiarisedAtUtc = DateTimeOffset.UnixEpoch,
        }, default);

        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        // The fix: the hydrated row shows the effective (owner) name, not the stale overlay.
        var row = vm.Clusters.Single(c => c.ClusterKey == "Local:0");
        Assert.Equal("Sarah Chen-Smith", row.Name);
        Assert.Equal(0, engine.Calls);

        // Confirm the untouched hydration. Select manually - auto-select is follow-up 2 (task 3).
        vm.Sources.Single(s => s.Source == SourceKind.Local).Selected = true;
        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        // Ownership re-asserted (not cleared), overlay converged, transcript NOT reverted.
        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal("Local:0", meta!.Participants.Single(p => p.Id == "p1").ClusterKey);
        var sp = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Sarah Chen-Smith", sp!.Names["Local:0"]);
        Assert.Equal("Sarah Chen-Smith", NameResolver.Resolve(
            TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "hi", "Me"), sp, meta));
    }

    [Fact]
    public async Task Hydration_auto_selects_the_committed_source_so_confirm_is_enabled()
    {
        // design 2026-07-29 follow-up 2: a dialog reopened purely to rename must have Confirm enabled
        // without the user first ticking a source - the whole point of the rename-hydration path.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.True(vm.Sources.Single(s => s.Source == SourceKind.Remote).Selected);
        Assert.True(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task Only_the_hydrated_source_is_auto_selected_not_a_merely_retained_one()
    {
        // Both legs retained, but only Remote was diarised. Auto-select ticks Remote and leaves Local
        // (offered by the source gate, but with no hydrated rows) unticked.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Local, SourceKind.Remote], localCount: 2);
        await SeedCommittedDiarisationAsync(paths, id);   // Remote only
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.True(vm.Sources.Single(s => s.Source == SourceKind.Remote).Selected);
        Assert.False(vm.Sources.Single(s => s.Source == SourceKind.Local).Selected);
    }

    [Fact]
    public async Task A_never_diarised_load_selects_no_source_and_leaves_confirm_disabled()
    {
        // No speakers.json committed: hydration builds no rows, nothing to auto-select, Confirm stays
        // disabled (CanConfirm requires Clusters AND a ticked source).
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.All(vm.Sources, s => Assert.False(s.Selected));
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
