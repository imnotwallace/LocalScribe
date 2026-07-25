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
/// guarantee holds only because this service never hands it a shared/aliased array.</summary>
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
        var registry = await store.LoadAsync(ct) ?? new PeopleRegistry();
        bool changed = false;

        foreach (var request in requests)
        {
            if (!embeddings.Entries.TryGetValue(request.ClusterKey, out var vector)) continue;

            string personId;
            if (request.PersonId is not null)
            {
                personId = request.PersonId;
            }
            else if (request.NewPersonName is not null)
            {
                Person person;
                (registry, person) = PeopleRegistryOps.EnsurePerson(
                    registry, request.NewPersonName, newId, time.GetUtcNow());
                personId = person.Id;
            }
            else
            {
                continue;   // neither PersonId nor NewPersonName set - skip silently
            }

            registry = PeopleRegistryOps.Enroll(registry, personId, new VoiceprintEnrollment
            {
                Id = newId(),
                Embedding = vector,             // freshly deserialized by LoadAsync above - see class doc
                Method = embeddings.Method,
                SourceSessionId = sessionId,
                SourceClusterKey = request.ClusterKey,
                EnrolledAtUtc = time.GetUtcNow(),
            });
            changed = true;
        }

        if (changed) await store.SaveAsync(registry, ct);   // one load, one save for the whole batch
    }

    public async Task<BackfillReport> BackfillScanAsync(
        IEmbeddingEngine engine, string embeddingModelPath,
        Func<string, SourceKind, string?> resolveLeg, CancellationToken ct)
    {
        int scanned = 0, enrolled = 0, skipped = 0;
        if (!Directory.Exists(paths.SessionsDir)) return new BackfillReport(0, 0, 0);

        var peopleStore = new PeopleStore(paths.PeopleJson);
        var registry = await peopleStore.LoadAsync(ct) ?? new PeopleRegistry();
        bool changed = false;

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
                var rosterByName = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var matterId in meta.MatterIds)
                {
                    var matter = await new MatterStore(paths.MattersDir).LoadAsync(matterId, ct);
                    if (matter is null) continue;
                    foreach (var m in matter.Roster)
                        if (m.PersonId is not null && !rosterByName.ContainsKey(m.Name))
                            rosterByName[m.Name] = m.PersonId;
                }

                IReadOnlyList<TranscriptLine>? lines = null;

                foreach (var p in meta.Participants)
                {
                    if (p.ClusterKey is null || string.IsNullOrWhiteSpace(p.Name)) continue;

                    string? personId = rosterByName.TryGetValue(p.Name, out var viaRoster)
                        ? viaRoster
                        : PeopleRegistryOps.FindByName(registry, p.Name)?.Id;
                    if (personId is null) continue;   // no resolvable identity - never invent one

                    int colon = p.ClusterKey.IndexOf(':');
                    if (colon < 0) continue;
                    string sourceKey = p.ClusterKey[..colon];
                    if (!speakers.Assignments.TryGetValue(sourceKey, out var bySeq)) continue;

                    var seqs = bySeq.Where(kv => kv.Value == p.ClusterKey)
                                    .Select(kv => int.Parse(kv.Key))
                                    .ToHashSet();
                    if (seqs.Count == 0) continue;

                    lines ??= await new TranscriptStore(paths.TranscriptJsonl(sessionId, versionId)).ReadAllAsync(ct);
                    var ranges = lines.Where(l => seqs.Contains(l.Seq))
                                       .Select(l => new EmbedRange(l.StartMs, l.EndMs))
                                       .ToList();
                    if (ranges.Count == 0) continue;

                    if (!Enum.TryParse<SourceKind>(sourceKey, out var side)) continue;
                    var legPath = resolveLeg(sessionId, side);
                    if (legPath is null) continue;

                    var embed = await engine.EmbedAsync(new EmbedRequest(legPath, ranges, embeddingModelPath), ct);
                    registry = PeopleRegistryOps.Enroll(registry, personId, new VoiceprintEnrollment
                    {
                        Id = newId(),
                        Embedding = embed.Embedding,   // fresh from this call - see class doc
                        Method = embed.Method,
                        SourceSessionId = sessionId,
                        SourceClusterKey = p.ClusterKey,
                        EnrolledAtUtc = time.GetUtcNow(),
                    });
                    changed = true;
                    enrolled++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped++;   // per-session failure: never abort the scan
            }
        }

        if (changed) await peopleStore.SaveAsync(registry, ct);   // one load, one save for the whole scan
        return new BackfillReport(scanned, enrolled, skipped);
    }
}
