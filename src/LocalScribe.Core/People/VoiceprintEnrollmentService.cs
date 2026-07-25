using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.People;

/// <summary>One confirm-time enrollment request: exactly one of <see cref="PersonId"/> /
/// <see cref="NewPersonName"/> is meaningful. A request with neither (or a clusterKey with no
/// captured embedding) is skipped silently - never a throw, never a partial write.</summary>
public sealed record ClusterEnrollmentRequest(string ClusterKey, string? PersonId, string? NewPersonName);

/// <summary>Result of a batch backfill scan. Skipped counts sessions the scan could not process
/// at all (missing session/speakers/meta data, or a per-session failure) - not sessions that were
/// simply already covered (embeddings.json present) or had nothing eligible to enroll.</summary>
public sealed record BackfillReport(int SessionsScanned, int Enrolled, int Skipped);

/// <summary>Enrollment orchestration (voiceprint design 2026-07-25). Confirm-time enrollment
/// copies vectors out of the session's embeddings.json; backfill extracts them via the embed op
/// for sessions diarised before embeddings existed. Enrollment is the consent gate: only
/// clusters the user explicitly confirmed to a person - or, for backfill, clusters a participant
/// slot already durably owns AND that resolve to a known person - ever enroll. Backfill never
/// creates a Person: FindByName only, EnsurePerson is never called from this path.
///
/// Embedding-copy guarantee: every vector handed to <see cref="PeopleRegistryOps.Enroll"/> here
/// is either (a) a value freshly deserialized out of embeddings.json by
/// <see cref="ClusterEmbeddingsStore.LoadAsync"/> (confirm path), or (b) a fresh array returned
/// by <see cref="IEmbeddingEngine.EmbedAsync"/> for this call only (backfill path). Neither is a
/// reference any other in-memory object can still hold or later mutate, and PeopleStore.SaveAsync
/// immediately serializes it into people.json's own bytes on disk - so a later per-session purge
/// or re-diarisation of the SOURCE session can never reach back and invalidate the copy sitting
/// in the registry. PeopleRegistryOps.Enroll itself does NOT clone the array it's given; the
/// guarantee holds only because this service never hands it a shared/aliased array.
///
/// COLLISION HAZARD (fix round 1, finding B; widened at final review, finding I1): backfill's
/// person resolution below falls back to <see cref="PeopleRegistryOps.FindByName"/> - an
/// exact-ordinal match against ANY existing Person - when the participant's name is not on any of
/// the session's matter rosters. Two humans who share a display name, or a participant whose name
/// happens to equal an unrelated global Person's name, will grow the WRONG person's voiceprint
/// here, silently, with no user act ever linking them. This is accepted by design (plan-mandated,
/// matches the Split dialog's own exact name-match rule) - not a defect to "fix" - but it is a real
/// hazard a future reader touching this path must understand. The roster route carries the SAME
/// name-collision hazard wherever a roster member has no explicit <c>PersonId</c> (see
/// <see cref="RosterPersonResolver"/>); it is narrower only in that the name must appear on a
/// matter this session belongs to.</summary>
public sealed class VoiceprintEnrollmentService(StoragePaths paths, TimeProvider time, Func<string> newId)
{
    public async Task EnrollFromConfirmAsync(
        string sessionId, string versionId,
        IReadOnlyList<ClusterEnrollmentRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return;
        var embeddings = await new ClusterEmbeddingsStore(paths.EmbeddingsJson(sessionId, versionId)).LoadAsync(ct);
        if (embeddings is null || embeddings.Entries.Count == 0) return;

        var store = new PeopleStore(paths.PeopleJson);
        // Fix (final-minors round, finding 1 - mirrors BackfillScanAsync's fix round 1, finding A):
        // this snapshot is used ONLY to resolve requests - an explicit PersonId's existence, and
        // EnsurePerson minting/finding a Person for a typed NewPersonName (unlike backfill, this
        // path DOES create people) - never mutated and written back directly. SplitSpeakersWindow
        // is non-modal (App.xaml.cs .Show()), so a Split-speakers Confirm can run while Settings'
        // global purge (MaintenanceService.PurgeVoiceprintDataAsync) is also in flight. `produced`
        // collects the enrollments THIS confirm actually generates (and `minted` any brand-new
        // Person it had to create); they are applied onto a FRESH reload of people.json taken
        // immediately before the terminal save (see below), so a purge that lands anywhere during
        // this call is honoured rather than silently reverted by a stale in-memory snapshot.
        var registry = await store.LoadAsync(ct) ?? new PeopleRegistry();
        var produced = new List<(string PersonId, VoiceprintEnrollment Enrollment)>();
        var minted = new List<Person>();   // brand-new Persons EnsurePerson created for THIS call

        foreach (var request in requests)
        {
            if (!embeddings.Entries.TryGetValue(request.ClusterKey, out var vector)) continue;

            string personId;
            if (request.PersonId is not null)
            {
                // Fix round 1, finding C: a stale PersonId (the linked Person was deleted since)
                // must be skipped BEFORE minting an enrollment id or calling Enroll - Enroll's
                // Update silently no-ops on an unknown id, so without this check the request would
                // look "processed" (an id consumed) while nothing was ever recorded.
                if (!PeopleRegistryOps.Exists(registry, request.PersonId)) continue;
                personId = request.PersonId;
            }
            else if (request.NewPersonName is not null)
            {
                Person person;
                int before = registry.People.Count;
                (registry, person) = PeopleRegistryOps.EnsurePerson(
                    registry, request.NewPersonName, newId, time.GetUtcNow());
                if (registry.People.Count > before) minted.Add(person);   // actually new, not a name match
                personId = person.Id;
            }
            else
            {
                continue;   // neither PersonId nor NewPersonName set - skip silently
            }

            produced.Add((personId, new VoiceprintEnrollment
            {
                Id = newId(),
                Embedding = vector,             // freshly deserialized by LoadAsync above - see class doc
                Method = embeddings.Method,
                SourceSessionId = sessionId,
                SourceClusterKey = request.ClusterKey,
                EnrolledAtUtc = time.GetUtcNow(),
            }));
        }

        if (produced.Count == 0) return;

        // Reload FRESH immediately before the terminal save and apply only what THIS confirm
        // produced, onto whatever the registry looks like RIGHT NOW - not the snapshot taken at
        // the top of this call. A concurrent purge that cleared voiceprints mid-confirm is
        // reflected in `fresh` and stays honoured; this confirm's own legitimate enrollments (and
        // any Person it had to mint for a typed name) still land on top of it.
        var fresh = await store.LoadAsync(ct) ?? new PeopleRegistry();
        foreach (var person in minted)
            if (!PeopleRegistryOps.Exists(fresh, person.Id))
                fresh = fresh with { People = [.. fresh.People, person] };
        foreach (var (personId, enrollment) in produced)
            if (PeopleRegistryOps.Exists(fresh, personId))   // defensive: person could vanish entirely too
                fresh = PeopleRegistryOps.Enroll(fresh, personId, enrollment);
        await store.SaveAsync(fresh, ct);
    }

    public async Task<BackfillReport> BackfillScanAsync(
        IEmbeddingEngine engine, string embeddingModelPath,
        Func<string, SourceKind, string?> resolveLeg, CancellationToken ct)
    {
        int scanned = 0, enrolled = 0, skipped = 0;
        if (!Directory.Exists(paths.SessionsDir)) return new BackfillReport(0, 0, 0);

        var peopleStore = new PeopleStore(paths.PeopleJson);
        // Fix round 1, finding A: this snapshot is used ONLY to resolve names/roster links to a
        // personId (a read query) - it is never mutated and never written back directly. Backfill
        // never creates a Person (see class doc), so the set of People it can see here cannot
        // shrink-then-need-to-grow across the scan; only Voiceprint contents can change underneath
        // it (e.g. a concurrent MaintenanceService.PurgeVoiceprintDataAsync). `produced` collects
        // the enrollments THIS scan actually generates; they are applied onto a FRESH reload of
        // people.json taken immediately before the terminal save (see below), so a purge that
        // lands anywhere during this arbitrarily-long scan (one embed call per participant, across
        // every session) is honoured rather than silently reverted by a stale in-memory snapshot.
        var registry = await peopleStore.LoadAsync(ct) ?? new PeopleRegistry();
        var produced = new List<(string PersonId, VoiceprintEnrollment Enrollment)>();

        foreach (var dir in Directory.EnumerateDirectories(paths.SessionsDir))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;
            var sessionId = Path.GetFileName(dir);
            try
            {
                var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(ct);
                if (session is null) { skipped++; continue; }
                var versionId = session.ActiveVersion;

                // Already covered by the normal (confirm-time) path - nothing to backfill here,
                // and this is not a failure, so it does not count against Skipped.
                if (File.Exists(paths.EmbeddingsJson(sessionId, versionId))) continue;

                var speakers = await new SpeakersStore(paths.SpeakersJson(sessionId, versionId)).LoadAsync(ct);
                var meta = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(ct);
                if (speakers is null || meta is null) { skipped++; continue; }

                // Person resolution: participant.Name -> matter roster PersonId (exact ordinal),
                // else an EXISTING Person of that name. Never creates a Person here - backfill
                // must not invent identities.
                // Final review finding I1: this map was built from RosterMember.PersonId alone, and
                // nothing writes one - so it was ALWAYS empty and every resolution fell through to
                // the global FindByName fallback the class doc calls a rare edge case. It now goes
                // through the shared RosterPersonResolver (explicit PersonId first, exact-ordinal
                // Person name second), the same rule the Split dialog's pool and confirm rules use.
                var rosterMembers = new List<RosterMember>();
                foreach (var matterId in meta.MatterIds)
                {
                    var matter = await new MatterStore(paths.MattersDir).LoadAsync(matterId, ct);
                    if (matter is not null) rosterMembers.AddRange(matter.Roster);
                }
                var rosterByName = RosterPersonResolver.LinkByName(rosterMembers, registry);

                IReadOnlyList<TranscriptLine>? lines = null;

                foreach (var p in meta.Participants)
                {
                    if (p.ClusterKey is null || string.IsNullOrWhiteSpace(p.Name)) continue;

                    string? personId = rosterByName.TryGetValue(p.Name, out var viaRoster)
                        ? viaRoster
                        : PeopleRegistryOps.FindByName(registry, p.Name)?.Id;
                    if (personId is null) continue;   // no resolvable identity - never invent one
                    // Fix round 1, finding C: a roster PersonId can point at a Person that was
                    // deleted since the roster was written. Skip BEFORE the embed call - never
                    // spend a real embed op (and consume a newId()) on an enrollment that Enroll's
                    // no-op would silently discard anyway.
                    if (!PeopleRegistryOps.Exists(registry, personId)) continue;

                    int colon = p.ClusterKey.IndexOf(':');
                    if (colon < 0) continue;
                    string sourceKey = p.ClusterKey[..colon];
                    if (!speakers.Assignments.TryGetValue(sourceKey, out var bySeq)) continue;

                    var seqs = bySeq.Where(kv => kv.Value == p.ClusterKey)
                                    .Select(kv => int.Parse(kv.Key))
                                    .ToHashSet();
                    if (seqs.Count == 0) continue;

                    lines ??= await new TranscriptStore(paths.TranscriptJsonl(sessionId, versionId)).ReadAllAsync(ct);
                    // Fix round 1, finding D: Kind == Segment makes explicit at the call site what
                    // was previously only true BY INVARIANT (seq is a single global counter per
                    // transcript file, so no marker/other-source line could ever collide with an
                    // assigned segment's seq) - a future change to that invariant (e.g. per-source
                    // seq numbering) would otherwise silently start embedding non-segment lines.
                    var ranges = lines.Where(l => l.Kind == TranscriptKind.Segment && seqs.Contains(l.Seq))
                                       .Select(l => new EmbedRange(l.StartMs, l.EndMs))
                                       .ToList();
                    if (ranges.Count == 0) continue;

                    if (!Enum.TryParse<SourceKind>(sourceKey, out var side)) continue;
                    var legPath = resolveLeg(sessionId, side);
                    if (legPath is null) continue;

                    var embed = await engine.EmbedAsync(new EmbedRequest(legPath, ranges, embeddingModelPath), ct);
                    produced.Add((personId, new VoiceprintEnrollment
                    {
                        Id = newId(),
                        Embedding = embed.Embedding,   // fresh from this call - see class doc
                        Method = embed.Method,
                        SourceSessionId = sessionId,
                        SourceClusterKey = p.ClusterKey,
                        EnrolledAtUtc = time.GetUtcNow(),
                    }));
                    enrolled++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped++;   // per-session failure: never abort the scan
            }
        }

        if (produced.Count > 0)
        {
            // Fix round 1, finding A: reload FRESH immediately before the terminal save and apply
            // only the enrollments THIS scan produced, onto whatever the registry looks like RIGHT
            // NOW - not the snapshot taken at the top of this (arbitrarily long) scan. A concurrent
            // purge that cleared voiceprints mid-scan is reflected in `fresh` and stays honoured;
            // this scan's own legitimate enrollments still land on top of it.
            var fresh = await peopleStore.LoadAsync(ct) ?? new PeopleRegistry();
            foreach (var (personId, enrollment) in produced)
                if (PeopleRegistryOps.Exists(fresh, personId))   // defensive: person could vanish entirely too
                    fresh = PeopleRegistryOps.Enroll(fresh, personId, enrollment);
            await peopleStore.SaveAsync(fresh, ct);
        }
        return new BackfillReport(scanned, enrolled, skipped);
    }
}
