using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Storage;

public class VoiceprintEnrollmentServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Directory.CreateTempSubdirectory("lsvoiceenroll").FullName;
    private readonly StoragePaths _paths;

    public VoiceprintEnrollmentServiceTests() => _paths = new StoragePaths(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // ---- seeding helpers, all through the real stores ----

    private async Task SeedSessionAsync(string id, IReadOnlyList<SessionParticipant>? participants = null,
        IReadOnlyList<string>? matterIds = null)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = T0.AddMinutes(5),
            DurationMs = 300000, Model = "small.en", Backend = "CPU",
            Sources = [SourceKind.Local, SourceKind.Remote],
        }, default);
        if (participants is not null || matterIds is not null)
        {
            await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
            {
                Title = "Test session",
                MatterIds = matterIds ?? [],
                Participants = participants ?? [],
            }, default);
        }
    }

    private Task SeedEmbeddingsAsync(string id, string versionId, IReadOnlyDictionary<string, float[]> entries, string method = "campplus-zh-en")
        => new ClusterEmbeddingsStore(_paths.EmbeddingsJson(id, versionId)).SaveAsync(new ClusterEmbeddings
        {
            Method = method, ExtractedAtUtc = T0, Entries = entries,
        }, default);

    private Task SeedSpeakersAsync(string id, string versionId, IReadOnlyDictionary<string, Dictionary<string, string>> assignments)
        => new SpeakersStore(_paths.SpeakersJson(id, versionId)).SaveAsync(new Speakers
        {
            Assignments = assignments,
        }, default);

    private Task SeedMatterAsync(string matterId, IReadOnlyList<RosterMember> roster)
        => new MatterStore(_paths.MattersDir).SaveAsync(new Matter
        {
            Id = matterId, Name = "Test matter", DateCreatedUtc = T0, Roster = roster,
        }, default);

    private Task SeedPeopleAsync(params Person[] people)
        => new PeopleStore(_paths.PeopleJson).SaveAsync(new PeopleRegistry { People = people }, default);

    private Task<PeopleRegistry?> LoadPeopleAsync()
        => new PeopleStore(_paths.PeopleJson).LoadAsync(default);

    private sealed class FakeEmbeddingEngine : IEmbeddingEngine
    {
        public readonly List<EmbedRequest> Requests = [];
        public Func<EmbedRequest, EmbedResult>? Respond;

        public Task<EmbedResult> EmbedAsync(EmbedRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Respond?.Invoke(request) ?? new EmbedResult([1f, 2f], "campplus-zh-en"));
        }
    }

    private VoiceprintEnrollmentService MakeService()
        => new(_paths, new ManualUtcTimeProvider(T0), () => "new-id");

    // ---------------------------------------------------------------------
    // EnrollFromConfirmAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Confirm_enrolls_existing_person_by_id()
    {
        await SeedSessionAsync("s1");
        await SeedEmbeddingsAsync("s1", "v1", new Dictionary<string, float[]> { ["Remote:0"] = [1f, 2f, 3f] });
        await SeedPeopleAsync(new Person { Id = "p1", Name = "Alice", CreatedUtc = T0 });

        var svc = MakeService();
        await svc.EnrollFromConfirmAsync("s1", "v1",
            [new ClusterEnrollmentRequest("Remote:0", "p1", null)], default);

        var registry = await LoadPeopleAsync();
        var p1 = registry!.People.Single(p => p.Id == "p1");
        Assert.Single(p1.Voiceprint);
        var e = p1.Voiceprint[0];
        Assert.Equal(new float[] { 1f, 2f, 3f }, e.Embedding);
        Assert.Equal("campplus-zh-en", e.Method);
        Assert.Equal("s1", e.SourceSessionId);
        Assert.Equal("Remote:0", e.SourceClusterKey);
        Assert.Equal(T0, e.EnrolledAtUtc);
    }

    [Fact]
    public async Task Confirm_creates_person_for_new_name()
    {
        await SeedSessionAsync("s1");
        await SeedEmbeddingsAsync("s1", "v1", new Dictionary<string, float[]> { ["Remote:0"] = [4f, 5f] });

        var svc = MakeService();
        await svc.EnrollFromConfirmAsync("s1", "v1",
            [new ClusterEnrollmentRequest("Remote:0", null, "Zed")], default);

        var registry = await LoadPeopleAsync();
        Assert.NotNull(registry);
        var zed = registry!.People.Single(p => p.Name == "Zed");
        Assert.Single(zed.Voiceprint);
        Assert.Equal(new float[] { 4f, 5f }, zed.Voiceprint[0].Embedding);
    }

    [Fact]
    public async Task Confirm_skips_cluster_without_embedding()
    {
        await SeedSessionAsync("s1");
        await SeedEmbeddingsAsync("s1", "v1", new Dictionary<string, float[]> { ["Remote:0"] = [1f, 2f] });
        await SeedPeopleAsync(new Person { Id = "p1", Name = "Alice", CreatedUtc = T0 });

        var svc = MakeService();
        // Remote:9 has no embedding entry -> silently skipped, no throw, no write.
        await svc.EnrollFromConfirmAsync("s1", "v1",
            [new ClusterEnrollmentRequest("Remote:9", "p1", null)], default);

        var registry = await LoadPeopleAsync();
        var p1 = registry!.People.Single(p => p.Id == "p1");
        Assert.Empty(p1.Voiceprint);
        Assert.Single(registry.People);
    }

    [Fact]
    public async Task Confirm_skips_request_with_neither_personid_nor_newname()
    {
        await SeedSessionAsync("s1");
        await SeedEmbeddingsAsync("s1", "v1", new Dictionary<string, float[]> { ["Remote:0"] = [1f, 2f] });
        await SeedPeopleAsync(new Person { Id = "p1", Name = "Alice", CreatedUtc = T0 });

        var svc = MakeService();
        await svc.EnrollFromConfirmAsync("s1", "v1",
            [new ClusterEnrollmentRequest("Remote:0", null, null)], default);

        var registry = await LoadPeopleAsync();
        Assert.Single(registry!.People);
        Assert.Empty(registry.People[0].Voiceprint);
    }

    [Fact]
    public async Task Confirm_applies_multiple_requests_in_one_call()
    {
        await SeedSessionAsync("s1");
        await SeedEmbeddingsAsync("s1", "v1", new Dictionary<string, float[]>
        {
            ["Remote:0"] = [1f, 2f],
            ["Local:0"] = [3f, 4f],
        });
        await SeedPeopleAsync(new Person { Id = "p1", Name = "Alice", CreatedUtc = T0 });

        var svc = MakeService();
        await svc.EnrollFromConfirmAsync("s1", "v1",
            [
                new ClusterEnrollmentRequest("Remote:0", "p1", null),
                new ClusterEnrollmentRequest("Local:0", null, "Zed"),
            ], default);

        var registry = await LoadPeopleAsync();
        Assert.Equal(2, registry!.People.Count);
        Assert.Single(registry.People.Single(p => p.Id == "p1").Voiceprint);
        Assert.Single(registry.People.Single(p => p.Name == "Zed").Voiceprint);
    }

    [Fact]
    public async Task Confirm_skips_request_with_unknown_person_id()
    {
        // Fix round 1, finding C: a stale/unknown PersonId must never mint an enrollment id or
        // silently "enroll" into nothing - it must be skipped before the embed/newId cost, and
        // must never disturb processing of the OTHER requests in the same batch.
        await SeedSessionAsync("s1");
        await SeedEmbeddingsAsync("s1", "v1", new Dictionary<string, float[]>
        {
            ["Remote:0"] = [1f, 2f],
            ["Local:0"] = [3f, 4f],
        });
        await SeedPeopleAsync(new Person { Id = "p1", Name = "Alice", CreatedUtc = T0 });

        int idCalls = 0;
        var svc = new VoiceprintEnrollmentService(_paths, new ManualUtcTimeProvider(T0),
            () => { idCalls++; return "new-id-" + idCalls; });

        await svc.EnrollFromConfirmAsync("s1", "v1",
            [
                new ClusterEnrollmentRequest("Remote:0", "ghost-id", null),   // stale/unknown PersonId
                new ClusterEnrollmentRequest("Local:0", "p1", null),          // valid
            ], default);

        // Only the valid request should ever mint an enrollment id - the stale one must never
        // reach the point of calling newId()/Enroll at all.
        Assert.Equal(1, idCalls);

        var registry = await LoadPeopleAsync();
        Assert.Single(registry!.People);      // no orphan/ghost person created
        var p1 = registry.People.Single(p => p.Id == "p1");
        Assert.Single(p1.Voiceprint);
        Assert.Equal(new float[] { 3f, 4f }, p1.Voiceprint[0].Embedding);
    }

    [Fact]
    public async Task Confirm_enrollment_survives_source_embeddings_purge()
    {
        // Proves the embedding-copy guarantee: once enrolled, deleting the SOURCE session's
        // embeddings.json (simulating a purge) must not affect the copy sitting in people.json.
        await SeedSessionAsync("s1");
        await SeedEmbeddingsAsync("s1", "v1", new Dictionary<string, float[]> { ["Remote:0"] = [7f, 8f, 9f] });
        await SeedPeopleAsync(new Person { Id = "p1", Name = "Alice", CreatedUtc = T0 });

        var svc = MakeService();
        await svc.EnrollFromConfirmAsync("s1", "v1",
            [new ClusterEnrollmentRequest("Remote:0", "p1", null)], default);

        // Simulate a purge of the source session's embeddings.
        new ClusterEmbeddingsStore(_paths.EmbeddingsJson("s1", "v1")).Delete();
        Assert.False(File.Exists(_paths.EmbeddingsJson("s1", "v1")));

        // Reload people.json fresh (a brand new store instance forces a real deserialization,
        // not a reference to anything held in memory) and confirm the vector is still intact.
        var registry = await LoadPeopleAsync();
        var e = registry!.People.Single(p => p.Id == "p1").Voiceprint.Single();
        Assert.Equal(new float[] { 7f, 8f, 9f }, e.Embedding);
    }

    // ---------------------------------------------------------------------
    // BackfillScanAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Backfill_enrolls_owned_person_linked_cluster_via_engine()
    {
        await SeedSessionAsync("s1",
            participants: [new SessionParticipant { Id = "sp1", Name = "Sarah", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
            matterIds: ["M-1"]);
        await SeedMatterAsync("M-1", [new RosterMember { Id = "r1", Name = "Sarah", PersonId = "p1" }]);
        await SeedSpeakersAsync("s1", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0", ["1"] = "Remote:0" },
        });
        var transcript = new TranscriptStore(_paths.TranscriptJsonl("s1", "v1"));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Hi.", "Sarah"), default);
        await transcript.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Remote, 1000, 2500, "There.", "Sarah"), default);
        // Fix round 1, finding D distractors: none of these are in the "Remote:0" cluster's seq
        // set, so none should ever reach the embed request - a broken implementation that embedded
        // every line (ignoring the seq/cluster filter, or the Kind filter) would pull one of these
        // in and fail the range assertions below.
        await transcript.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Local, 2500, 3000, "Me.", "Me"), default);            // wrong source
        await transcript.AppendAsync(TranscriptLine.Marker(3, 3000, "Marker event"), default);                                        // not a segment
        await transcript.AppendAsync(TranscriptLine.Segment(4, TranscriptSource.Remote, 3000, 3500, "Unassigned.", "?"), default);    // unassigned Remote segment
        await SeedPeopleAsync(new Person { Id = "p1", Name = "Sarah", CreatedUtc = T0 });

        var engine = new FakeEmbeddingEngine();
        var svc = MakeService();
        var report = await svc.BackfillScanAsync(engine, "model.bin",
            (sessionId, side) => side == SourceKind.Remote ? @"C:\fake\remote.flac" : null, default);

        Assert.Equal(1, report.SessionsScanned);
        Assert.Equal(1, report.Enrolled);
        Assert.Equal(0, report.Skipped);

        Assert.Single(engine.Requests);
        var req = engine.Requests[0];
        Assert.Equal(@"C:\fake\remote.flac", req.FlacPath);
        Assert.Equal("model.bin", req.EmbeddingModelPath);
        Assert.Equal(2, req.Ranges.Count);
        Assert.Contains(req.Ranges, r => r.StartMs == 0 && r.EndMs == 1000);
        Assert.Contains(req.Ranges, r => r.StartMs == 1000 && r.EndMs == 2500);
        Assert.DoesNotContain(req.Ranges, r => r.StartMs == 2500 && r.EndMs == 3000);   // Local distractor excluded
        Assert.DoesNotContain(req.Ranges, r => r.StartMs == 3000 && r.EndMs == 3500);   // unassigned Remote distractor excluded
        Assert.DoesNotContain(req.Ranges, r => r.StartMs == 3000 && r.EndMs == 3000);   // marker distractor excluded

        var registry = await LoadPeopleAsync();
        var p1 = registry!.People.Single(p => p.Id == "p1");
        Assert.Single(p1.Voiceprint);
        Assert.Equal(new float[] { 1f, 2f }, p1.Voiceprint[0].Embedding);
        Assert.Equal("campplus-zh-en", p1.Voiceprint[0].Method);
        Assert.Equal("s1", p1.Voiceprint[0].SourceSessionId);
        Assert.Equal("Remote:0", p1.Voiceprint[0].SourceClusterKey);
    }

    [Fact]
    public async Task Backfill_skips_sessions_that_already_have_embeddings()
    {
        await SeedSessionAsync("s1",
            participants: [new SessionParticipant { Id = "sp1", Name = "Sarah", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
            matterIds: ["M-1"]);
        await SeedMatterAsync("M-1", [new RosterMember { Id = "r1", Name = "Sarah", PersonId = "p1" }]);
        await SeedSpeakersAsync("s1", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0" },
        });
        await SeedEmbeddingsAsync("s1", "v1", new Dictionary<string, float[]> { ["Remote:0"] = [1f, 2f] });

        var engine = new FakeEmbeddingEngine();
        var svc = MakeService();
        var report = await svc.BackfillScanAsync(engine, "model.bin", (_, _) => @"C:\fake\remote.flac", default);

        Assert.Equal(1, report.SessionsScanned);
        Assert.Equal(0, report.Skipped);
        Assert.Equal(0, report.Enrolled);
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task Backfill_skips_participant_with_no_resolvable_person()
    {
        // ClusterKey is owned, but the name matches no roster PersonId and no existing Person:
        // per the consent framing, backfill must NEVER invent an identity.
        await SeedSessionAsync("s1",
            participants: [new SessionParticipant { Id = "sp1", Name = "Ghost", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
            matterIds: ["M-1"]);
        await SeedMatterAsync("M-1", [new RosterMember { Id = "r1", Name = "SomeoneElse", PersonId = "p1" }]);
        await SeedSpeakersAsync("s1", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0" },
        });
        var transcript = new TranscriptStore(_paths.TranscriptJsonl("s1", "v1"));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Hi.", "Ghost"), default);

        var engine = new FakeEmbeddingEngine();
        var svc = MakeService();
        var report = await svc.BackfillScanAsync(engine, "model.bin", (_, _) => @"C:\fake\remote.flac", default);

        Assert.Equal(1, report.SessionsScanned);
        Assert.Equal(0, report.Enrolled);
        Assert.Empty(engine.Requests);

        var registry = await LoadPeopleAsync();
        // No person was invented for "Ghost".
        Assert.True(registry is null || registry.People.All(p => p.Name != "Ghost"));
    }

    [Fact]
    public async Task Backfill_counts_corrupt_session_as_skipped_and_continues_scan()
    {
        // s1 has a matter.json with a forward schema version -> MatterStore.LoadAsync throws
        // NotSupportedException while resolving the roster; that must count as Skipped, not
        // abort the whole scan.
        await SeedSessionAsync("s1",
            participants: [new SessionParticipant { Id = "sp1", Name = "Sarah", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
            matterIds: ["M-BAD"]);
        Directory.CreateDirectory(Path.Combine(_paths.MattersDir, "M-BAD"));
        await File.WriteAllTextAsync(Path.Combine(_paths.MattersDir, "M-BAD", "matter.json"), "{\"schemaVersion\":99}");
        await SeedSpeakersAsync("s1", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0" },
        });

        await SeedSessionAsync("s2",
            participants: [new SessionParticipant { Id = "sp2", Name = "Bob", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
            matterIds: ["M-1"]);
        await SeedMatterAsync("M-1", [new RosterMember { Id = "r1", Name = "Bob", PersonId = "p1" }]);
        await SeedSpeakersAsync("s2", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0" },
        });
        var transcript = new TranscriptStore(_paths.TranscriptJsonl("s2", "v1"));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Hi.", "Bob"), default);
        await SeedPeopleAsync(new Person { Id = "p1", Name = "Bob", CreatedUtc = T0 });

        var engine = new FakeEmbeddingEngine();
        var svc = MakeService();
        var report = await svc.BackfillScanAsync(engine, "model.bin", (_, _) => @"C:\fake\remote.flac", default);

        Assert.Equal(2, report.SessionsScanned);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.Enrolled);
    }

    [Fact]
    public async Task Backfill_enrolls_via_global_name_fallback_when_no_roster_link()
    {
        // Fix round 1, finding B: the ONLY resolution route here is PeopleRegistryOps.FindByName -
        // no matter/roster at all, so rosterByName is empty and the ternary MUST fall through to
        // the global-name fallback. Previously untested (both enrolling tests seeded a roster
        // PersonId, so this path never actually ran under test).
        await SeedSessionAsync("s1",
            participants: [new SessionParticipant { Id = "sp1", Name = "Carol", Side = SourceKind.Remote, ClusterKey = "Remote:0" }]);
        await SeedSpeakersAsync("s1", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0" },
        });
        var transcript = new TranscriptStore(_paths.TranscriptJsonl("s1", "v1"));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Hi.", "Carol"), default);
        await SeedPeopleAsync(new Person { Id = "p-carol", Name = "Carol", CreatedUtc = T0 });

        var engine = new FakeEmbeddingEngine();
        var svc = MakeService();
        var report = await svc.BackfillScanAsync(engine, "model.bin", (_, _) => @"C:\fake\remote.flac", default);

        Assert.Equal(1, report.Enrolled);
        Assert.Single(engine.Requests);
        var registry = await LoadPeopleAsync();
        var carol = registry!.People.Single(p => p.Id == "p-carol");
        Assert.Single(carol.Voiceprint);
    }

    [Fact]
    public async Task Backfill_prefers_roster_personid_over_global_name_fallback()
    {
        // Fix round 1, finding B: when BOTH routes resolve - a roster PersonId AND a global Person
        // sharing the same exact name - and they point at DIFFERENT people, the roster link must
        // win (it is checked first in the ternary). This also pins the documented name-collision
        // hazard: an unrelated global Person named "Dana" must NOT receive this voiceprint.
        await SeedSessionAsync("s1",
            participants: [new SessionParticipant { Id = "sp1", Name = "Dana", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
            matterIds: ["M-1"]);
        await SeedMatterAsync("M-1", [new RosterMember { Id = "r1", Name = "Dana", PersonId = "p-roster" }]);
        await SeedSpeakersAsync("s1", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0" },
        });
        var transcript = new TranscriptStore(_paths.TranscriptJsonl("s1", "v1"));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Hi.", "Dana"), default);
        await SeedPeopleAsync(
            new Person { Id = "p-roster", Name = "Dana", CreatedUtc = T0 },
            new Person { Id = "p-wrong-dana", Name = "Dana", CreatedUtc = T0 });   // unrelated namesake

        var engine = new FakeEmbeddingEngine();
        var svc = MakeService();
        var report = await svc.BackfillScanAsync(engine, "model.bin", (_, _) => @"C:\fake\remote.flac", default);

        Assert.Equal(1, report.Enrolled);
        var registry = await LoadPeopleAsync();
        Assert.Single(registry!.People.Single(p => p.Id == "p-roster").Voiceprint);
        Assert.Empty(registry.People.Single(p => p.Id == "p-wrong-dana").Voiceprint);
    }

    [Fact]
    public async Task Backfill_resolves_a_roster_member_that_carries_no_person_id_by_name()
    {
        // Final whole-branch review, finding I1: nothing in the product writes RosterMember.PersonId,
        // so the roster route was unreachable and this scan ALWAYS fell through to the global
        // FindByName fallback its own doc comment calls a rare edge case. The roster map now
        // resolves an unlinked member by exact-ordinal name through the shared
        // RosterPersonResolver, the same rule the Split dialog's pool and confirm rules use.
        //
        // HONEST DISCLOSURE: by construction this cannot discriminate against the pre-fix code -
        // the roster member's name and the participant's name are necessarily the same string, so
        // the global fallback resolves to the same Person. It is a regression pin on the resolved
        // identity, not a proof of which route resolved it (RosterPersonResolverTests pins that).
        await SeedSessionAsync("s1",
            participants: [new SessionParticipant { Id = "sp1", Name = "Frank", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
            matterIds: ["M-1"]);
        await SeedMatterAsync("M-1", [new RosterMember { Id = "r1", Name = "Frank" }]);   // no PersonId
        await SeedSpeakersAsync("s1", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0" },
        });
        var transcript = new TranscriptStore(_paths.TranscriptJsonl("s1", "v1"));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Hi.", "Frank"), default);
        await SeedPeopleAsync(new Person { Id = "p-frank", Name = "Frank", CreatedUtc = T0 });

        var engine = new FakeEmbeddingEngine();
        var svc = MakeService();
        var report = await svc.BackfillScanAsync(engine, "model.bin", (_, _) => @"C:\fake\remote.flac", default);

        Assert.Equal(1, report.Enrolled);
        var registry = await LoadPeopleAsync();
        Assert.Single(registry!.People.Single(p => p.Id == "p-frank").Voiceprint);
    }

    [Fact]
    public async Task Backfill_skips_participant_whose_roster_personid_is_stale()
    {
        // Fix round 1, finding C: the roster PersonId points at a Person that does not (or no
        // longer) exist in people.json. Must be skipped BEFORE the embed engine is ever called -
        // not embedded and then discarded by Enroll's no-op.
        await SeedSessionAsync("s1",
            participants: [new SessionParticipant { Id = "sp1", Name = "Sarah", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
            matterIds: ["M-1"]);
        await SeedMatterAsync("M-1", [new RosterMember { Id = "r1", Name = "Sarah", PersonId = "deleted-person" }]);
        await SeedSpeakersAsync("s1", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0" },
        });
        var transcript = new TranscriptStore(_paths.TranscriptJsonl("s1", "v1"));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Hi.", "Sarah"), default);
        // No SeedPeopleAsync at all: "deleted-person" resolves to nothing in the registry.

        var engine = new FakeEmbeddingEngine();
        var svc = MakeService();
        var report = await svc.BackfillScanAsync(engine, "model.bin", (_, _) => @"C:\fake\remote.flac", default);

        Assert.Equal(0, report.Enrolled);
        Assert.Empty(engine.Requests);   // the embed engine must never be called for a stale personId
    }

    [Fact]
    public async Task Backfill_terminal_save_honors_a_concurrent_purge_mid_scan()
    {
        // Fix round 1, finding A: BackfillScanAsync snapshots the registry near the start and
        // (pre-fix) wrote that stale snapshot back at the end - silently reverting any purge that
        // landed in between. Simulates MaintenanceService.PurgeVoiceprintDataAsync racing in via
        // the fake engine's Respond hook, which fires exactly "after this scan's embed call, before
        // its terminal save" - the same window the finding describes.
        await SeedSessionAsync("s1",
            participants: [new SessionParticipant { Id = "sp1", Name = "Sarah", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
            matterIds: ["M-1"]);
        await SeedMatterAsync("M-1", [new RosterMember { Id = "r1", Name = "Sarah", PersonId = "p1" }]);
        await SeedSpeakersAsync("s1", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0" },
        });
        var transcript = new TranscriptStore(_paths.TranscriptJsonl("s1", "v1"));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Hi.", "Sarah"), default);

        // p1 already has a PRE-EXISTING voiceprint from before this scan started.
        await SeedPeopleAsync(new Person
        {
            Id = "p1", Name = "Sarah", CreatedUtc = T0,
            Voiceprint = [new VoiceprintEnrollment
            {
                Id = "pre-existing", Embedding = [9f, 9f], Method = "campplus-zh-en",
                SourceSessionId = "s0", SourceClusterKey = "Remote:0", EnrolledAtUtc = T0,
            }],
        });

        var engine = new FakeEmbeddingEngine
        {
            Respond = _ =>
            {
                // Out-of-band concurrent purge: clears people.json's enrollments (simulating
                // MaintenanceService.PurgeVoiceprintDataAsync) mid-scan, via a brand-new PeopleStore
                // instance - not anything this test's service instance holds in memory.
                var store = new PeopleStore(_paths.PeopleJson);
                var registry = store.LoadAsync(default).GetAwaiter().GetResult()!;
                store.SaveAsync(PeopleRegistryOps.ClearAllVoiceprints(registry), default).GetAwaiter().GetResult();
                return new EmbedResult([1f, 2f], "campplus-zh-en");
            },
        };
        var svc = MakeService();
        var report = await svc.BackfillScanAsync(engine, "model.bin", (_, _) => @"C:\fake\remote.flac", default);

        Assert.Equal(1, report.Enrolled);

        var registryAfter = await LoadPeopleAsync();
        var p1After = registryAfter!.People.Single(p => p.Id == "p1");
        // The concurrent purge's deletion of the PRE-EXISTING vector must STAY deleted...
        Assert.DoesNotContain(p1After.Voiceprint, e => e.Id == "pre-existing");
        // ...but the enrollment THIS SCAN legitimately produced must still land.
        Assert.Single(p1After.Voiceprint);
        Assert.Equal(new float[] { 1f, 2f }, p1After.Voiceprint[0].Embedding);
    }

    [Fact]
    public async Task Backfill_propagates_cancellation_instead_of_swallowing()
    {
        await SeedSessionAsync("s1",
            participants: [new SessionParticipant { Id = "sp1", Name = "Sarah", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
            matterIds: ["M-1"]);
        await SeedMatterAsync("M-1", [new RosterMember { Id = "r1", Name = "Sarah", PersonId = "p1" }]);
        await SeedSpeakersAsync("s1", "v1", new Dictionary<string, Dictionary<string, string>>
        {
            ["Remote"] = new() { ["0"] = "Remote:0" },
        });
        var transcript = new TranscriptStore(_paths.TranscriptJsonl("s1", "v1"));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Hi.", "Sarah"), default);
        // Fix round 1, finding C: "p1" must actually exist in people.json now, or the new
        // stale-personId guard skips the participant BEFORE ever reaching the engine - which
        // would defeat this test's whole point (proving cancellation propagates OUT of the engine
        // call, not that the participant got skipped for an unrelated reason).
        await SeedPeopleAsync(new Person { Id = "p1", Name = "Sarah", CreatedUtc = T0 });

        var engine = new FakeEmbeddingEngine { Respond = _ => throw new OperationCanceledException() };
        var svc = MakeService();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            svc.BackfillScanAsync(engine, "model.bin", (_, _) => @"C:\fake\remote.flac", default));
    }
}
