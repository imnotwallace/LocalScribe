using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Services;

/// <summary>Outcome of a launch/on-demand recovery scan (design 7.1): which sessions were
/// actually recovered, and per-id failures that were collected instead of aborting the rest.</summary>
public sealed record RecoveryScanResult(IReadOnlyList<string> RecoveredIds,
    IReadOnlyList<(string Id, string Error)> Failures);

/// <summary>The one app-level owner of all disk mutation from the UI (design 7.3): projection
/// re-renders behind a per-session single-flight queue, index writes behind one dedicated gate,
/// recovery-scan orchestration, cascades, bulk regenerate. ViewModels never call SessionWriter
/// directly. WPF-free by house rule; unit-testable headless.</summary>
public sealed class MaintenanceService(StoragePaths paths, ISettingsService settings,
    IRecycleBin recycleBin, TimeProvider time)
{
    // Per-session gates are created on first touch and kept for the process lifetime - a
    // Stage 4 manager touches at most a few hundred ids, so unbounded growth is a non-issue.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new();
    private readonly SemaphoreSlim _indexGate = new(1, 1);   // serializes ALL matters.json writes

    /// <summary>Set by App.OnStartup to the in-flight startup scan (StartupOrchestrator.RunAsync).
    /// SessionsPageViewModel awaits it (null-coalesced to Task.CompletedTask) to clear the
    /// "checking for interrupted sessions..." banner; null in compositions with no startup scan
    /// (unit tests). Additive - not part of the locked Stage 4 surface.</summary>
    public Task? StartupScanTask { get; set; }

    /// <summary>Per-session single-flight: an edit, a finalize regen, a migrating read, and a
    /// cascade can never interleave writes inside one session folder (design 7.3).</summary>
    public async Task<T> RunForSessionAsync<T>(string sessionId, Func<CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        var gate = _sessionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await work(ct); }
        finally { gate.Release(); }
    }

    public Task<SessionCatalogResult> ListSessionsAsync(CancellationToken ct)
        => new SessionCatalog(paths).ListAsync(ct);

    /// <summary>Id-first single-session load for the Session Details window (Stage 5.2). Reads one
    /// session.json + meta.json exactly as SessionCatalog.ListAsync does per entry; returns null when
    /// session.json is absent. Serialized per session id against concurrent writers.</summary>
    public Task<SessionListItem?> LoadSessionItemAsync(string sessionId, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(selfForMigration: null, inner);
            if (session is null) return null;
            var startedLocal = session.UtcOffsetMinutes is int offsetMin
                ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
                : session.StartedAtUtc.ToLocalTime();
            var meta = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(inner)
                       ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
            return new SessionListItem(sessionId, session, meta);
        }, ct);

    /// <summary>Save meta.json (the ONLY file user metadata edits touch - spec 1.2/1.4), then
    /// regenerate projections under the same per-session gate with a FRESH SessionWriter built
    /// from settings.Current (so timestamp-style etc. reflect the latest save), then apply the
    /// matter-tag delta computed against previousMatterIds to the index.</summary>
    public async Task SaveMetaAsync(string sessionId, SessionMeta meta,
        IReadOnlyCollection<string> previousMatterIds, CancellationToken ct)
    {
        bool wrote = await RunForSessionAsync(sessionId, async inner =>
        {
            // A queued editor save can land AFTER a whole-session delete (design 3.4): skip it
            // rather than resurrect sessions/<id>/ with an orphan meta.json (which would surface
            // as an UnreadableCount folder outside the Recycle Bin). session.json is the truth
            // file; its absence means the folder is gone. One guard covers all SaveMetaAsync callers.
            if (!File.Exists(paths.SessionJson(sessionId))) return false;
            await new MetadataStore(paths.MetaJson(sessionId)).SaveAsync(meta, inner);
            await new SessionWriter(paths, settings.Current, time)
                .RegenerateProjectionsAsync(sessionId, inner);
            return true;
        }, ct);
        if (!wrote) return;                         // deleted mid-save: no write, so no index delta

        var added = meta.MatterIds.Except(previousMatterIds, StringComparer.Ordinal).ToList();
        var removed = previousMatterIds.Except(meta.MatterIds, StringComparer.Ordinal).ToList();
        if (added.Count > 0 || removed.Count > 0)
            await ApplyTagDeltaLockedAsync(added, removed, ct);
    }

    /// <summary>Flip meta.json's Archived flag under the session gate (design 3.1). Reads the
    /// CURRENT meta and rewrites ONLY Archived, so a stale caller snapshot can never revert a
    /// concurrent editor save (e.g. a just-typed Title). Regenerates projections like SaveMetaAsync;
    /// matter tags are unchanged, so there is no index delta. Never flips Edited/LastEditedAtUtc.
    /// No-ops when the session folder/meta is gone or already at the requested state.</summary>
    public Task SetArchivedAsync(string sessionId, bool archived, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            var current = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(inner);
            if (current is null || current.Archived == archived) return true;
            await new MetadataStore(paths.MetaJson(sessionId))
                .SaveAsync(current with { Archived = archived }, inner);
            await new SessionWriter(paths, settings.Current, time)
                .RegenerateProjectionsAsync(sessionId, inner);
            return true;
        }, ct);

    /// <summary>The session's active transcript version id ("v1" = the session root). The
    /// version-content operations below (edits/speakers/transcript reads) resolve through this
    /// so editing and Split speakers always operate on the ACTIVE version (design 2026-07-13
    /// section 3.3). Callers hold the per-session gate, so the read cannot interleave with
    /// SetActiveVersionAsync's write.</summary>
    private async Task<string> ActiveVersionAsync(string sessionId, CancellationToken ct)
        => (await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(ct))?.ActiveVersion ?? "v1";

    /// <summary>Persist which transcript version the session reads/edits/exports (design
    /// 2026-07-13 section 3.4: the read-view switcher). Gated per session like every other
    /// session.json rewrite; validates against the recorded Versions list so a stale caller can
    /// never point ActiveVersion at a folder that was never committed. No projection regen: each
    /// version keeps its own rendered files, written when it was created/last edited.</summary>
    public Task<bool> SetActiveVersionAsync(string sessionId, string versionId, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            var store = new SessionStore(paths.SessionJson(sessionId));
            var session = await store.ReadAsync(inner);
            if (session is null) return false;
            if (versionId != TranscriptVersions.Root && session.Versions.All(v => v.Id != versionId))
                throw new ArgumentException(
                    $"unknown transcript version '{versionId}' for {sessionId}.", nameof(versionId));
            if (session.ActiveVersion == versionId) return true;
            await store.SaveAsync(session with { ActiveVersion = versionId }, inner);
            return true;
        }, ct);

    /// <summary>Batched text-correction save from the read view (Stage 6.1). SaveMetaAsync's
    /// shape: per-session gate -> session.json delete-race guard -> ONE EditStore batch write
    /// (which itself enforces finalized-only + seq-exists and flips meta.Edited) -> ONE
    /// projection regen under the same gate hold. No matters-index delta (tags unchanged).
    /// Returns false without writing when the session was deleted mid-edit or the batch was a
    /// no-op (nothing to regen either way).</summary>
    public Task<bool> SaveTextCorrectionsAsync(string sessionId,
        IReadOnlyDictionary<int, string> corrections, IReadOnlyCollection<int> reverts,
        CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId))) return false;
            string vid = await ActiveVersionAsync(sessionId, inner);
            bool changed = await new EditStore(paths.SessionDir(sessionId), time,
                    contentDir: paths.VersionDir(sessionId, vid))
                .ApplyTextEditsAsync(corrections, reverts, inner);
            if (changed)
                await new SessionWriter(paths, settings.Current, time)
                    .RegenerateProjectionsAsync(sessionId, inner);
            return changed;
        }, ct);

    /// <summary>The one write path for an Edit-mode save (design §3.4): apply text corrections and
    /// split overlays (and their reverts) to edits.json under the per-session gate, then ONE
    /// projection regen. Whole-section speaker pins go through SaveSpeakerPinsAsync separately (the
    /// editor VM calls it), keeping this method's writes confined to edits.json. Returns false when
    /// the session was deleted mid-save or the whole batch was a no-op.</summary>
    public Task<bool> SaveTranscriptEditsAsync(string sessionId, TranscriptEditBatch batch, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId))) return false;
            var store = new EditStore(paths.SessionDir(sessionId), time,
                contentDir: paths.VersionDir(sessionId, await ActiveVersionAsync(sessionId, inner)));
            bool changed = false;

            // Corrections first (splits clear a seq's correction, so ordering is safe either way).
            if (batch.Corrections.Count > 0 || batch.CorrectionReverts.Count > 0)
                changed |= await store.ApplyTextEditsAsync(batch.Corrections, batch.CorrectionReverts, inner);

            foreach (int seq in batch.SplitReverts)
                changed |= await store.RemoveSplitAsync(seq, inner);

            foreach (var s in batch.Splits)
            {
                var parts = s.Parts.Select(p => new SplitPart
                {
                    Text = p.Text, StartMs = p.StartMs, DerivedStart = p.DerivedStart,
                    SpeakerParticipantId = p.SpeakerParticipantId, SpeakerClusterKey = p.SpeakerClusterKey,
                }).ToList();
                await store.ApplySplitAsync(s.Seq, s.Source, parts, inner);
                changed = true;
            }

            if (changed)
                await new SessionWriter(paths, settings.Current, time).RegenerateProjectionsAsync(sessionId, inner);
            return changed;
        }, ct);

    /// <summary>Batched speaker pin from the read view (Stage 6.1, design section 1.4). Write
    /// order mirrors SaveDiarisationAsync: speakers.json (truth) FIRST via the EditStore batch
    /// pin, then participant ClusterKey ownership into meta.json when a fresh key was minted for
    /// a cluster-less participant (meta is re-read from disk first so the batch pin's meta.Edited
    /// flip survives the full-overwrite ownership save), then ONE projection regen - all under the
    /// per-session gate.
    /// A crash between the pin write and the ownership write leaves the pin rendering
    /// "Speaker N" until re-pinned (benign, documented design quirk). Minted keys avoid every
    /// key referenced by speakers.Names, the source's assignments, and participant-owned keys,
    /// so a fresh identity can never collide with a different voice.</summary>
    public Task<bool> SaveSpeakerPinsAsync(string sessionId, TranscriptSource source,
        IReadOnlyCollection<int> seqs, SpeakerPinTarget target, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId))) return false;
            string vid = await ActiveVersionAsync(sessionId, inner);

            var metaStore = new MetadataStore(paths.MetaJson(sessionId));
            var meta = await metaStore.LoadAsync(inner);

            string clusterKey;
            SessionParticipant? mintedFor = null;
            switch (target)
            {
                case SpeakerPinTarget.Cluster c:
                    clusterKey = c.ClusterKey;
                    break;
                case SpeakerPinTarget.Participant p:
                    var participant = meta?.Participants.FirstOrDefault(x => x.Id == p.ParticipantId)
                        ?? throw new ArgumentException(
                            $"no participant '{p.ParticipantId}' in meta.json.", nameof(target));
                    if (participant.ClusterKey is string ownedKey)
                    {
                        clusterKey = ownedKey;
                    }
                    else
                    {
                        clusterKey = await MintClusterKeyAsync(sessionId, vid, source, meta!, inner);
                        mintedFor = participant;
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target));
            }

            await new EditStore(paths.SessionDir(sessionId), time,
                    contentDir: paths.VersionDir(sessionId, vid))
                .ReassignSpeakersAsync(seqs, source, clusterKey, inner);

            if (mintedFor is not null)
            {
                // Re-load meta AFTER ReassignSpeakersAsync: its MarkEditedAsync just flipped
                // Edited/LastEditedAtUtc on disk, and MetadataStore.SaveAsync is a full overwrite,
                // so persisting ownership off the pre-pin snapshot would silently revert that flip.
                // Reading the fresh copy under the same gate keeps the first-edit flip intact.
                var fresh = await metaStore.LoadAsync(inner);
                if (fresh is not null)
                {
                    var updated = fresh.Participants
                        .Select(x => x.Id == mintedFor.Id ? x with { ClusterKey = clusterKey } : x)
                        .ToList();
                    await metaStore.SaveAsync(fresh with { Participants = updated }, inner);
                }
            }

            await new SessionWriter(paths, settings.Current, time)
                .RegenerateProjectionsAsync(sessionId, inner);
            return true;
        }, ct);

    /// <summary>Gated unpin (Stage 6.1): EditStore removes pin+assignment for actually-pinned
    /// seqs only (diarised assignments survive), then one regen when anything changed.</summary>
    public Task<bool> RemoveSpeakerPinsAsync(string sessionId, TranscriptSource source,
        IReadOnlyCollection<int> seqs, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId))) return false;
            bool changed = await new EditStore(paths.SessionDir(sessionId), time,
                    contentDir: paths.VersionDir(sessionId, await ActiveVersionAsync(sessionId, inner)))
                .RemoveSpeakerPinsAsync(seqs, source, inner);
            if (changed)
                await new SessionWriter(paths, settings.Current, time)
                    .RegenerateProjectionsAsync(sessionId, inner);
            return changed;
        }, ct);

    /// <summary>Smallest unused per-source cluster id across speakers.json (Names keys + the
    /// source's assignment values) and meta participant-owned keys - max seen id + 1, the same
    /// allocation ceiling SpeakersMerge uses for collision remaps.</summary>
    private async Task<string> MintClusterKeyAsync(string sessionId, string versionId, TranscriptSource source,
        SessionMeta meta, CancellationToken ct)
    {
        var speakers = await new SpeakersStore(paths.SpeakersJson(sessionId, versionId)).LoadAsync(ct)
            ?? new Speakers();
        string prefix = source + ":";
        int maxId = -1;
        void Consider(string key)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) return;
            if (int.TryParse(key.AsSpan(prefix.Length), out int id)) maxId = Math.Max(maxId, id);
        }
        foreach (var k in speakers.Names.Keys) Consider(k);
        if (speakers.Assignments.TryGetValue(source.ToString(), out var bySeq))
            foreach (var k in bySeq.Values) Consider(k);
        foreach (var p in meta.Participants)
            if (p.ClusterKey is string ck) Consider(ck);
        return prefix + (maxId + 1);
    }

    /// <summary>Back-compat overload (Stage 5 Task 7 shape): no ownership-persistence semantics -
    /// meta.json's Participants list is still READ (to gather owned/protected keys for the merge)
    /// but never rewritten. Delegates to the 4-arg overload below with participantClusterKeys:
    /// null, which is exactly what "meta.json untouched" means there.</summary>
    public Task<IReadOnlyDictionary<string, string>> SaveDiarisationAsync(
        string sessionId, DiarisationCommit commit, CancellationToken ct) =>
        SaveDiarisationAsync(sessionId, commit, participantClusterKeys: null, ct);

    /// <summary>The one write path for diarisation (Stage 5 Task 7 + Stage 5.4 sections 5.2/C2):
    /// merge a fresh <see cref="DiarisationCommit"/> into speakers.json (pin- AND
    /// ownership-preserving via SpeakersMerge), persist participant ClusterKey ownership into
    /// meta.json, flip session.Diarised, then regenerate projections - all under the same
    /// per-session gate SaveMetaAsync/SetArchivedAsync use. Participant-owned clusterKeys read
    /// from meta.json are protected like pins: a colliding fresh key is remapped away so a
    /// different voice can never be re-bound under a key a named identity owns. Write order
    /// matters: speakers.json (source of truth) FIRST, then ownership, then the Diarised flag,
    /// then projections - so a crash between steps never advertises a diarisation whose overlay
    /// didn't land. Never flips meta.json Edited/LastEditedAtUtc (reserved for manual
    /// corrections) and NEVER deletes/touches audio for any AudioRetention value - the retained
    /// legs are primary evidence (no SessionDeleter, no IRecycleBin, no per-source removal here,
    /// ever).
    /// <paramref name="participantClusterKeys"/> maps participant Id -> the run's RAW (pre-remap)
    /// clusterKey chosen at confirm time; the collision remap computed by THIS SAME merge is
    /// applied before the value is written, so ownership always points at the key that actually
    /// landed in speakers.json. Participants are rewritten from the meta already loaded above
    /// under this gate (not a caller snapshot), so a stale VM snapshot can never resurrect old
    /// fields - only ClusterKey changes: a re-asserted slot gets its (remapped) key; a
    /// re-diarised source's un-reasserted stale ownership is cleared (cluster ids restart at 0
    /// per run, so keeping it could mislabel a different voice - pinned lines keep their labels
    /// regardless via pin-preserved speakers.Names); everything else, including the other side's
    /// ownership, passes through untouched. <c>null</c> = legacy caller (the 3-arg overload
    /// above): meta.json's Participants list is left completely untouched.</summary>
    public Task<IReadOnlyDictionary<string, string>> SaveDiarisationAsync(
        string sessionId, DiarisationCommit commit,
        IReadOnlyDictionary<string, string>? participantClusterKeys, CancellationToken ct) =>
        RunForSessionAsync<IReadOnlyDictionary<string, string>>(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                return new Dictionary<string, string>();            // deleted mid-run guard

            // 1) merge into speakers.json (pin- and ownership-preserving) and save FIRST
            //    (source of truth). Owned keys come from the CURRENT meta.json under this
            //    same gate, not a caller snapshot.
            var metaStore = new MetadataStore(paths.MetaJson(sessionId));
            var meta = await metaStore.LoadAsync(inner);
            var owned = meta?.Participants
                .Where(p => !string.IsNullOrEmpty(p.ClusterKey))
                .Select(p => p.ClusterKey!)
                .ToList() ?? [];
            var store = new SpeakersStore(paths.SpeakersJson(sessionId,
                await ActiveVersionAsync(sessionId, inner)));
            var existing = await store.LoadAsync(inner);
            var result = SpeakersMerge.Merge(existing, commit, owned);
            await store.SaveAsync(result.Speakers, inner);

            // 1b) participant ClusterKey ownership (Stage 5.4 C2) - see doc comment above. meta
            //     was already loaded (same gate hold) so there is no staleness risk re-reading it.
            if (participantClusterKeys is not null && meta is not null)
            {
                var rePrefixes = commit.Sources.Select(s => s.ToString() + ":").ToList();
                var updated = meta.Participants.Select(p =>
                {
                    if (participantClusterKeys.TryGetValue(p.Id, out var chosen))
                        return p with
                        {
                            ClusterKey = result.FreshKeyRemap.TryGetValue(chosen, out var remapped)
                                ? remapped : chosen,
                        };
                    if (p.ClusterKey is string ck &&
                        rePrefixes.Any(prefix => ck.StartsWith(prefix, StringComparison.Ordinal)))
                        return p with { ClusterKey = null };
                    return p;
                }).ToList();
                if (!updated.SequenceEqual(meta.Participants))   // records: value equality
                    await metaStore.SaveAsync(meta with { Participants = updated }, inner);
            }

            // 2) flip session.Diarised (mirror the RecoverIfNeededAsync rewrite pattern).
            var sessionStore = new SessionStore(paths.SessionJson(sessionId));
            var session = await sessionStore.ReadAsync(inner);
            if (session is not null && !session.Diarised)
                await sessionStore.SaveAsync(session with { Diarised = true }, inner);

            // 3) re-render projections with the new speaker names + ownership.
            // NOTE: NO audio deletion here for any AudioRetention value (evidentiary firewall).
            await new SessionWriter(paths, settings.Current, time).RegenerateProjectionsAsync(sessionId, inner);
            return result.FreshKeyRemap;
        }, ct);

    /// <summary>Whole-session delete to the Recycle Bin (design 3.4) - the caller has already
    /// closed any open read views (WindowRegistry.CloseAllFor) so no handle blocks the recycle.
    /// The delete runs under the session's gate; the index decrement follows. The tag set is read
    /// from the CURRENT meta.json under the same gate (not a stale caller snapshot), so the
    /// sessionCount decrement targets the matters this session is actually tagged to right now.</summary>
    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        IReadOnlyList<string> tags = await RunForSessionAsync(sessionId, async inner =>
        {
            var meta = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(inner);
            var current = (meta?.MatterIds ?? []).ToList();
            await new SessionDeleter(paths, recycleBin).DeleteAsync(sessionId, inner);
            return (IReadOnlyList<string>)current;
        }, ct);
        if (tags.Count > 0)
            await ApplyTagDeltaLockedAsync([], tags, ct);
    }

    /// <summary>Recovery scan (design 7.1): every session.json with EndedAtUtc == null gets
    /// SessionWriter.RecoverIfNeededAsync under its own per-session gate. Idempotent (the writer
    /// re-checks EndedAtUtc); per-id failures are collected, never thrown out - one corrupt
    /// folder must not strand the other interrupted sessions unrecovered. Cancellation is the
    /// only exception that propagates.</summary>
    public async Task<RecoveryScanResult> RecoverAllAsync(CancellationToken ct,
        Action<string>? onRecovered = null)
    {
        var unended = await new RecoveryScanner(paths).FindUnendedAsync(ct);
        var recovered = new List<string>();
        var failures = new List<(string Id, string Error)>();
        foreach (string id in unended)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool did = await RunForSessionAsync(id,
                    inner => new SessionWriter(paths, settings.Current, time)
                        .RecoverIfNeededAsync(id, inner), ct);
                // Design 2026-07-12 section 3: notify per recovered id so a long startup scan can
                // update the Sessions list one row at a time. Fires from this scan's background
                // thread; the App-layer wiring (App.xaml.cs) marshals it through the UI dispatcher.
                if (did) { recovered.Add(id); onRecovered?.Invoke(id); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add((id, ex.Message)); }
        }
        return new RecoveryScanResult(recovered, failures);
    }

    public async Task<MattersIndex> RebuildIndexAsync(CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { return await new MattersIndexRebuilder(paths).RebuildAsync(ct); }
        finally { _indexGate.Release(); }
    }

    /// <summary>Gated (not just the writes): AtomicFile's write-then-move onto matters.json
    /// (design 4.3) is not safe against a concurrent open read handle on Windows - a reader
    /// racing the rename can make File.Move throw (sharing violation/access denied) instead of
    /// the rename simply losing the race. Routing this read through the same _indexGate as
    /// every writer removes that window; SaveMatterAsync/ApplyTagDeltaLockedAsync never call
    /// back into this method, so there is no re-entrancy risk.</summary>
    public async Task<MattersIndex> ListMattersAsync(CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { return await new MatterStore(paths.MattersDir).ListAsync(ct); }
        finally { _indexGate.Release(); }
    }

    /// <summary>Gated for the same reason as ListMattersAsync above.</summary>
    public async Task<Matter?> LoadMatterAsync(string matterId, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { return await new MatterStore(paths.MattersDir).LoadAsync(matterId, ct); }
        finally { _indexGate.Release(); }
    }

    /// <summary>Persists a matter (matter.json + matters.json index upsert) under the same
    /// lock that serializes RebuildIndexAsync/ApplyTagDelta index writes (design 4.3: ALL
    /// index writes serialized). Returns only after the index upsert completed. Task 18
    /// declares this same method - whichever task merges second drops its duplicate copy.</summary>
    public async Task SaveMatterAsync(Matter matter, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { await new MatterStore(paths.MattersDir).SaveAsync(matter, ct); }
        finally { _indexGate.Release(); }
    }

    /// <summary>Mint + persist a new matter atomically under _indexGate: reads the index, mints
    /// the next M-YYYYMMDD-NNN id against it, and saves - all inside ONE gate hold, so a rapid
    /// double-invoke cannot read the same index twice and mint a duplicate id (design 4.2/4.3).
    /// Calls MatterStore directly (not SaveMatterAsync) to avoid re-entering the non-reentrant
    /// _indexGate. The id date and DateCreatedUtc come from the injected TimeProvider.</summary>
    public async Task<Matter> CreateMatterAsync(string name, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try
        {
            var store = new MatterStore(paths.MattersDir);
            var index = await store.ListAsync(ct);
            var now = time.GetUtcNow();
            string id = MatterIdGenerator.Next(index, paths.MattersDir, DateOnly.FromDateTime(now.UtcDateTime));
            var matter = new Matter { Id = id, Name = name, DateCreatedUtc = now };
            await store.SaveAsync(matter, ct);
            return matter;
        }
        finally { _indexGate.Release(); }
    }

    /// <summary>Matter delete under _indexGate (mirrors SaveMatterAsync): the whole matters.json
    /// read+write - the blocked-while-referenced guard, Recycle-Bin folder removal, and index
    /// entry removal - runs serialized against every other index writer (design 4.3/7.3).
    /// MatterDeleter uses bare stores (no _indexGate), so there is no re-entrancy. Throws
    /// InvalidOperationException (via MatterDeleter) when sessions still reference the matter.</summary>
    public async Task DeleteMatterAsync(string matterId, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { await new MatterDeleter(paths, recycleBin).DeleteAsync(matterId, ct); }
        finally { _indexGate.Release(); }
    }

    private async Task ApplyTagDeltaLockedAsync(IReadOnlyCollection<string> added,
        IReadOnlyCollection<string> removed, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { await new MattersIndexRebuilder(paths).ApplyTagDeltaAsync(added, removed, ct); }
        finally { _indexGate.Release(); }
    }

    /// <summary>Matter rename cascade (design 4.4): regenerate the projections of every session
    /// whose meta tags this matter, each under its own per-session gate. Truth files untouched -
    /// session.txt resolves matter Name (Reference) live at render time.</summary>
    public async Task CascadeMatterAsync(string matterId, IProgress<int>? progress, CancellationToken ct)
    {
        var catalog = await ListSessionsAsync(ct);
        var targets = catalog.Sessions
            .Where(s => s.Meta.MatterIds.Contains(matterId, StringComparer.Ordinal))
            .Select(s => s.Id).ToList();
        await RegenerateEachAsync(targets, progress, ct);
    }

    /// <summary>Bulk regenerate (Settings page maintenance button, design 6.1): every catalog
    /// session re-renders with the CURRENT settings (timestamp style, vocabulary, ...).</summary>
    public async Task RegenerateAllAsync(IProgress<int>? progress, CancellationToken ct)
    {
        var catalog = await ListSessionsAsync(ct);
        await RegenerateEachAsync(catalog.Sessions.Select(s => s.Id).ToList(), progress, ct);
    }

    private async Task RegenerateEachAsync(IReadOnlyList<string> sessionIds, IProgress<int>? progress,
        CancellationToken ct)
    {
        var failures = new List<Exception>();
        int done = 0;
        foreach (string id in sessionIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunForSessionAsync(id, async inner =>
                {
                    await new SessionWriter(paths, settings.Current, time)
                        .RegenerateProjectionsAsync(id, inner);
                    return true;
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Collected, not fatal mid-loop: one broken folder must not stop the rest
                // (design 7.5 - the caller surfaces the aggregate via InfoBar/balloon).
                failures.Add(new InvalidOperationException($"regenerate failed for {id}: {ex.Message}", ex));
            }
            progress?.Report(++done);
        }
        if (failures.Count > 0)
            throw new AggregateException("one or more sessions failed to regenerate", failures);
    }

    /// <summary>Export one session folder as a .zip (design 3.2). Held under the session gate so the
    /// archive never captures a half-written re-render. On failure/cancel, deletes the OUTPUT file
    /// only - never anything under storageRoot.</summary>
    public Task ExportSessionArchiveAsync(string sessionId, string destPath, CancellationToken ct)
        => ExportWithOutputCleanupAsync(destPath, markCreated => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                throw new InvalidOperationException("The session no longer exists.");
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            markCreated();
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            await SessionArchiver.AddSessionFolderAsync(zip, paths.SessionDir(sessionId), "", inner);
            return true;
        }, ct));

    /// <summary>Export one session as a formatted .docx transcript (design 3.3). Reads the shared
    /// projection under the session gate; page size is the ONE machine-locale dependence (RegionInfo).</summary>
    public Task ExportDocxAsync(string sessionId, string destPath, DocxOptions options, CancellationToken ct)
        => ExportWithOutputCleanupAsync(destPath, markCreated => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                throw new InvalidOperationException("The session no longer exists.");
            var loaded = await SessionProjectionLoader.LoadAsync(paths, settings.Current, time, sessionId, inner);
            var pageSize = DocxRenderer.PageSizeForRegion(RegionInfo.CurrentRegion);
            // ReadWrite (not Write): DocumentFormat.OpenXml's package model reads back from the
            // stream while building the OPC zip structure, so Write-only throws
            // OpenXmlPackageException("The stream was not opened for reading.").
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            markCreated();
            // Versioned session (design 2026-07-13 section 3.3): the footer must state which
            // transcript version this document renders. Composed HERE, where footerText already
            // composes, so DocxRenderer stays a pure serializer.
            string versionNote =
                $"Transcript version {TranscriptVersions.ShortId(loaded.VersionId)} ({loaded.Header.Model})";
            string footerText = loaded.VersionId == TranscriptVersions.Root
                ? settings.Current.DocxFooterText
                : string.IsNullOrEmpty(settings.Current.DocxFooterText)
                    ? versionNote
                    : settings.Current.DocxFooterText + " - " + versionNote;
            DocxRenderer.Write(fs, loaded.Header, loaded.TextView, loaded.Rows, settings.Current.Timestamps,
                footerText, pageSize, options);
            return true;
        }, ct));

    /// <summary>Result of a matter zip: how many sessions were archived vs skipped (live-recording /
    /// pending-recovery / deleted mid-export). Surfaced in the completion Info message.</summary>
    public sealed record MatterExportResult(int Added, int Skipped);

    /// <summary>Export every finalized session tagged with a matter into one .zip (design 3.2): snapshot
    /// the tagged list, add a root matter.json, then gate-and-add one session at a time (gate released
    /// between sessions). Unfinalized (live/pending-recovery, EndedAtUtc null) sessions are skipped and
    /// reported. Determinate IProgress&lt;int&gt; (1..target-count) + cancellation; on failure/cancel,
    /// deletes the OUTPUT file only.</summary>
    public async Task<MatterExportResult> ExportMatterArchiveAsync(string matterId, string destPath,
        IProgress<int>? progress, CancellationToken ct)
    {
        var catalog = await ListSessionsAsync(ct);
        var targets = catalog.Sessions
            .Where(s => s.Meta.MatterIds.Contains(matterId, StringComparer.Ordinal))
            .ToList();
        int added = 0, skipped = 0, done = 0;
        try
        {
            using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // Root matter.json snapshot (design 3.2). Read under _indexGate - like every other
                // matter-file read (LoadMatterAsync) - so a concurrent SaveMatterAsync (Phase 6.2
                // vocab/roster edit) File.Move cannot race this read handle. ReadAllBytes opens+reads+
                // closes fully under the gate, so no handle outlives the lock.
                byte[]? matterBytes = null;
                await _indexGate.WaitAsync(ct);
                try
                {
                    string matterJson = paths.MatterJson(matterId);
                    if (File.Exists(matterJson)) matterBytes = await File.ReadAllBytesAsync(matterJson, ct);
                }
                finally { _indexGate.Release(); }
                if (matterBytes is not null)
                {
                    var entry = zip.CreateEntry("matter.json", CompressionLevel.Optimal);
                    using var dst = entry.Open();
                    await dst.WriteAsync(matterBytes, ct);
                }

                foreach (var item in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.Session.EndedAtUtc is null) { skipped++; progress?.Report(++done); continue; }

                    bool wrote = await RunForSessionAsync(item.Id, async inner =>
                    {
                        if (!File.Exists(paths.SessionJson(item.Id))) return false;   // deleted mid-export
                        await SessionArchiver.AddSessionFolderAsync(zip, paths.SessionDir(item.Id),
                            item.Id + "/", inner);
                        return true;
                    }, ct);
                    if (wrote) added++; else skipped++;
                    progress?.Report(++done);
                }
            }
        }
        catch
        {
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { /* best effort */ }
            throw;
        }
        return new MatterExportResult(added, skipped);
    }

    private static async Task ExportWithOutputCleanupAsync(string destPath, Func<Action, Task> export)
    {
        // Only delete output THIS export created: if the pre-check / projection load throws before the
        // FileStream opens, a pre-existing file the user chose to overwrite in Save-As is left intact
        // (whole-phase review Minor). storageRoot is never touched on any path.
        bool created = false;
        try { await export(() => created = true); }
        catch
        {
            if (created) { try { if (File.Exists(destPath)) File.Delete(destPath); } catch { /* best effort */ } }
            throw;
        }
    }
}
