using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Services;

/// <summary>Outcome of a launch/on-demand recovery scan (design 7.1): which sessions were
/// actually recovered, and per-id failures that were collected instead of aborting the rest.</summary>
public sealed record RecoveryScanResult(IReadOnlyList<string> RecoveredIds,
    IReadOnlyList<(string Id, string Error)> Failures);

/// <summary>Outcome of <see cref="MaintenanceService.PurgeVoiceprintDataAsync"/> (voiceprint design
/// 2026-07-25, fix round 1 finding 2): mirrors RecoveryScanResult's per-id failure-collection
/// pattern above - a session gate can throw (a malformed speakers.json or a forward-versioned one),
/// and one corrupt session must never abort the rest of the purge or the People enrollment strip.
/// SessionsTouched counts only sessions that actually had something deleted/cleared; Failures is
/// empty on a fully clean run. Task 13 (not yet written) is the intended consumer.</summary>
public sealed record VoiceprintPurgeResult(int SessionsTouched,
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

    /// <summary>Search-index live-update seam (design 2026-07-13 section 2.1): raised with the
    /// session id AFTER any gated write that can change what the session's search entry derives
    /// from - meta save (title/tags/participants), archive flip, corrections, splits, speaker pins,
    /// diarisation, recovery, projection re-render (vocabulary may have changed), version switch,
    /// and delete (the re-index then drops the entry). Raised OUTSIDE the per-session gate so a
    /// handler may re-enter RunForSessionAsync for the same id; never raised for a no-op/skipped
    /// write. Wrapped like SessionFinalizeCompleted: a throwing subscriber must never fault the
    /// calling command.</summary>
    public event Action<string>? SessionContentChanged;

    private void RaiseSessionContentChanged(string sessionId)
    {
        try { SessionContentChanged?.Invoke(sessionId); } catch { }
    }

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
        RaiseSessionContentChanged(sessionId);      // title/matters/participants feed the search index

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
    public async Task SetArchivedAsync(string sessionId, bool archived, CancellationToken ct)
    {
        bool wrote = await RunForSessionAsync(sessionId, async inner =>
        {
            var current = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(inner);
            if (current is null || current.Archived == archived) return false;
            await new MetadataStore(paths.MetaJson(sessionId))
                .SaveAsync(current with { Archived = archived }, inner);
            await new SessionWriter(paths, settings.Current, time)
                .RegenerateProjectionsAsync(sessionId, inner);
            return true;
        }, ct);
        if (wrote) RaiseSessionContentChanged(sessionId);    // meta.json stamp changed
    }

    /// <summary>F1 defense-in-depth (whole-branch review fix wave): every content-write below
    /// takes the AUTHORED version explicitly from its caller instead of re-resolving ActiveVersion
    /// at write time - re-resolving at write time let a version switched (via the read-view
    /// dropdown) or completed (a background re-transcription landing mid-edit) between load and
    /// Save silently redirect seq-keyed corrections/pins into the WRONG version's overlay (v1 and
    /// every vN number seqs from 0, so EnsureSegmentsAsync/ApplyTextEditsAsync never notice the
    /// mismatch). This validates the caller-supplied versionId against the CURRENT on-disk
    /// Versions list - read under the same per-session gate hold the write itself runs under, so
    /// the validation cannot itself go stale mid-call - and throws loudly rather than silently
    /// falling back to root when the id names a version this session never actually recorded.</summary>
    private static void EnsureKnownVersion(string sessionId, string versionId, SessionRecord session)
    {
        if (versionId != TranscriptVersions.Root && session.Versions.All(v => v.Id != versionId))
            throw new ArgumentException(
                $"unknown transcript version '{versionId}' for {sessionId}.", nameof(versionId));
    }

    /// <summary>Persist which transcript version the session reads/edits/exports (design
    /// 2026-07-13 section 3.4: the read-view switcher). Gated per session like every other
    /// session.json rewrite; validates against the recorded Versions list so a stale caller can
    /// never point ActiveVersion at a folder that was never committed. No projection regen: each
    /// version keeps its own rendered files, written when it was created/last edited. Returns
    /// (Ok, Wrote): Ok = the session exists and the target is now active (true even on a no-op);
    /// Wrote = session.json actually changed - false when the target was already active, so the
    /// wrapper can honour SessionContentChanged's "never on a no-op" contract (B2-4).</summary>
    private Task<(bool Ok, bool Wrote)> SetActiveVersionCoreAsync(string sessionId, string versionId, CancellationToken ct)
        => RunForSessionAsync<(bool Ok, bool Wrote)>(sessionId, async inner =>
        {
            var store = new SessionStore(paths.SessionJson(sessionId));
            var session = await store.ReadAsync(inner);
            if (session is null) return (Ok: false, Wrote: false);
            if (versionId != TranscriptVersions.Root && session.Versions.All(v => v.Id != versionId))
                throw new ArgumentException(
                    $"unknown transcript version '{versionId}' for {sessionId}.", nameof(versionId));
            if (session.ActiveVersion == versionId) return (Ok: true, Wrote: false);   // valid no-op
            await store.SaveAsync(session with { ActiveVersion = versionId }, inner);
            return (Ok: true, Wrote: true);
        }, ct);

    /// <summary>Version-switch wrapper (search-index seam, design 2026-07-13 section 2.1): the
    /// active version determines WHICH transcript/edits/speakers the search index derives from, so
    /// a switch that ACTUALLY writes re-indexes the session. An already-active no-op still returns
    /// true (the target is active) but raises nothing - SessionContentChanged is never raised for a
    /// no-op (B2-4: a spurious raise cost an idempotent search re-derive + cache rewrite).</summary>
    public async Task<bool> SetActiveVersionAsync(string sessionId, string versionId, CancellationToken ct)
    {
        var (ok, wrote) = await SetActiveVersionCoreAsync(sessionId, versionId, ct);
        if (wrote) RaiseSessionContentChanged(sessionId);
        return ok;
    }

    /// <summary>Batched text-correction save from the read view (Stage 6.1). SaveMetaAsync's
    /// shape: per-session gate -> session.json read (delete-race guard + F1 version validation) ->
    /// ONE EditStore batch write (which itself enforces finalized-only + seq-exists and flips
    /// meta.Edited) -> ONE projection regen under the same gate hold. No matters-index delta (tags
    /// unchanged). <paramref name="versionId"/> is the version the caller AUTHORED the correction
    /// against (e.g. ReadViewViewModel's loaded VersionId) - never re-resolved from disk here, so a
    /// version switch/completion between load and Save cannot redirect the write (F1 whole-branch
    /// review fix; see EnsureKnownVersion's doc). Returns false without writing when the session
    /// was deleted mid-edit or the batch was a no-op (nothing to regen either way).</summary>
    public async Task<bool> SaveTextCorrectionsAsync(string sessionId,
        IReadOnlyDictionary<int, string> corrections, IReadOnlyCollection<int> reverts,
        string versionId, CancellationToken ct)
    {
        bool changed = await RunForSessionAsync(sessionId, async inner =>
        {
            var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(inner);
            if (session is null) return false;
            EnsureKnownVersion(sessionId, versionId, session);
            bool wrote = await new EditStore(paths.SessionDir(sessionId), time,
                    contentDir: paths.VersionDir(sessionId, versionId))
                .ApplyTextEditsAsync(corrections, reverts, inner);
            if (wrote)
                await new SessionWriter(paths, settings.Current, time)
                    .RegenerateProjectionsAsync(sessionId, inner);
            return wrote;
        }, ct);
        if (changed) RaiseSessionContentChanged(sessionId);
        return changed;
    }

    /// <summary>The one write path for an Edit-mode save (design §3.4): apply text corrections and
    /// split overlays (and their reverts) to edits.json under the per-session gate, then ONE
    /// projection regen. Whole-section speaker pins go through SaveSpeakerPinsAsync separately (the
    /// editor VM calls it), keeping this method's writes confined to edits.json.
    /// <paramref name="versionId"/> is the version the whole edit session was authored against (F1
    /// fix - see EnsureKnownVersion's doc for why this must never be re-resolved here). Returns
    /// false when the session was deleted mid-save or the whole batch was a no-op.</summary>
    public async Task<bool> SaveTranscriptEditsAsync(string sessionId, TranscriptEditBatch batch,
        string versionId, CancellationToken ct)
    {
        bool changed = await RunForSessionAsync(sessionId, async inner =>
        {
            var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(inner);
            if (session is null) return false;
            EnsureKnownVersion(sessionId, versionId, session);
            var store = new EditStore(paths.SessionDir(sessionId), time,
                contentDir: paths.VersionDir(sessionId, versionId));
            bool wrote = false;

            // Corrections first (splits clear a seq's correction, so ordering is safe either way).
            if (batch.Corrections.Count > 0 || batch.CorrectionReverts.Count > 0)
                wrote |= await store.ApplyTextEditsAsync(batch.Corrections, batch.CorrectionReverts, inner);

            foreach (int seq in batch.SplitReverts)
                wrote |= await store.RemoveSplitAsync(seq, inner);

            foreach (var s in batch.Splits)
            {
                var parts = s.Parts.Select(p => new SplitPart
                {
                    Text = p.Text, StartMs = p.StartMs, DerivedStart = p.DerivedStart,
                    SpeakerParticipantId = p.SpeakerParticipantId, SpeakerClusterKey = p.SpeakerClusterKey,
                }).ToList();
                await store.ApplySplitAsync(s.Seq, s.Source, parts, inner);
                wrote = true;
            }

            if (wrote)
                await new SessionWriter(paths, settings.Current, time).RegenerateProjectionsAsync(sessionId, inner);
            return wrote;
        }, ct);
        if (changed) RaiseSessionContentChanged(sessionId);
        return changed;
    }

    /// <summary>Batched speaker pin from the read view (Stage 6.1, design section 1.4). Write
    /// order mirrors SaveDiarisationAsync: speakers.json (truth) FIRST via the EditStore batch
    /// pin, then participant ClusterKey ownership into meta.json when a fresh key was minted for
    /// a cluster-less participant (meta is re-read from disk first so the batch pin's meta.Edited
    /// flip survives the full-overwrite ownership save), then ONE projection regen - all under the
    /// per-session gate.
    /// A crash between the pin write and the ownership write leaves the pin rendering
    /// "Speaker N" until re-pinned (benign, documented design quirk). Minted keys avoid every
    /// key referenced by speakers.Names, the source's assignments, and participant-owned keys,
    /// so a fresh identity can never collide with a different voice.
    /// <paramref name="versionId"/> is the version the caller authored the pin against (F1 fix -
    /// see EnsureKnownVersion's doc for why this must never be re-resolved from disk here).</summary>
    public async Task<bool> SaveSpeakerPinsAsync(string sessionId, TranscriptSource source,
        IReadOnlyCollection<int> seqs, SpeakerPinTarget target, string versionId, CancellationToken ct)
    {
        bool wrote = await RunForSessionAsync(sessionId, async inner =>
        {
            var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(inner);
            if (session is null) return false;
            EnsureKnownVersion(sessionId, versionId, session);
            string vid = versionId;

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
        if (wrote) RaiseSessionContentChanged(sessionId);
        return wrote;
    }

    /// <summary>Gated unpin (Stage 6.1): EditStore removes pin+assignment for actually-pinned
    /// seqs only (diarised assignments survive), then one regen when anything changed.
    /// <paramref name="versionId"/> is the version the caller authored the unpin against (F1 fix -
    /// see EnsureKnownVersion's doc for why this must never be re-resolved from disk here).</summary>
    public async Task<bool> RemoveSpeakerPinsAsync(string sessionId, TranscriptSource source,
        IReadOnlyCollection<int> seqs, string versionId, CancellationToken ct)
    {
        bool changed = await RunForSessionAsync(sessionId, async inner =>
        {
            var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(inner);
            if (session is null) return false;
            EnsureKnownVersion(sessionId, versionId, session);
            bool wrote = await new EditStore(paths.SessionDir(sessionId), time,
                    contentDir: paths.VersionDir(sessionId, versionId))
                .RemoveSpeakerPinsAsync(seqs, source, inner);
            if (wrote)
                await new SessionWriter(paths, settings.Current, time)
                    .RegenerateProjectionsAsync(sessionId, inner);
            return wrote;
        }, ct);
        if (changed) RaiseSessionContentChanged(sessionId);
        return changed;
    }

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
    /// but never rewritten. Delegates to the 5-arg overload below with participantClusterKeys:
    /// null, which is exactly what "meta.json untouched" means there. <paramref name="versionId"/>
    /// is the version the caller authored the commit against (F1 fix).</summary>
    public Task<IReadOnlyDictionary<string, string>> SaveDiarisationAsync(
        string sessionId, DiarisationCommit commit, string versionId, CancellationToken ct) =>
        SaveDiarisationAsync(sessionId, commit, versionId, participantClusterKeys: null, ct);

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
    /// ownership, passes through untouched. <c>null</c> = legacy caller (the 4-arg overload
    /// above): meta.json's Participants list is left completely untouched.
    /// <paramref name="versionId"/> is the version the caller authored this commit against - the
    /// same version SplitSpeakersViewModel read the cluster-to-line map from at dialog load - and
    /// is never re-resolved from disk here (F1 fix: see EnsureKnownVersion's doc for why a
    /// re-transcription completing mid-dialog must not silently redirect the write).
    /// 5-arg back-compat overload (Task 9, voiceprint design 2026-07-25): no per-cluster
    /// embeddings, so embeddings.json is left exactly as-is. Delegates to the 6-arg overload with
    /// resultsBySource: null.</summary>
    public Task<IReadOnlyDictionary<string, string>> SaveDiarisationAsync(
        string sessionId, DiarisationCommit commit, string versionId,
        IReadOnlyDictionary<string, string>? participantClusterKeys, CancellationToken ct) =>
        SaveDiarisationAsync(sessionId, commit, versionId, participantClusterKeys,
            resultsBySource: null, ct);

    /// <summary><paramref name="resultsBySource"/> (Task 9, voiceprint design 2026-07-25): the raw
    /// per-source DiarisationResult objects this commit was built from, keyed the same as
    /// commit.Assignments ("Local"/"Remote"). <c>null</c> is the legacy-caller degrade path (the
    /// 5-arg overload's default): embeddings.json is left completely untouched, no read or write at
    /// all - diarisation completes identically to before this task.
    /// When non-null, the commit's versionId's embeddings.json is ALWAYS re-derived (fix round 1,
    /// finding 3): entries belonging to a source NOT in commit.Sources are carried over from the
    /// existing file untouched (a re-diarised Remote must never wipe Local's embeddings); entries
    /// belonging to a commit.Sources source are dropped and, ONLY when resultsBySource carries a
    /// result for that EXACT source key, replaced by that result's ClusterEmbeddings (when any)
    /// keyed "{Source}:{clusterId}" TRANSLATED THROUGH this same merge's FreshKeyRemap (the remap
    /// rule - cluster ids restart at 0 every run, so a raw pre-remap key can point at a different
    /// voice than the one speakers.json ends up naming). A result for a source NOT in
    /// commit.Sources is ignored entirely (fix round 1, finding 1): FreshKeyRemap is only ever
    /// computed for commit.Sources, so writing such a result under its raw keys could land AFTER,
    /// and overwrite, the just-preserved correct entries for that same source - reachable when a
    /// source RAN in this pass but was deselected before Confirm (Task 11's SplitSpeakersViewModel
    /// keeps results for every source it ran, keyed wider than commit.Sources). A commit that
    /// re-asserts identity for its sources but yields no fresh embeddings for them still drops
    /// those sources' stale entries (fix round 1, finding 3: they would otherwise keep naming a
    /// different voice than speakers.json does, per the ClusterEmbeddings invariant); if nothing
    /// survives once dropped, the file is deleted rather than persisted as an empty shell (chosen
    /// over writing an empty Entries dict because an on-disk-but-empty embeddings.json is
    /// indistinguishable from a corrupt one to a naive reader, and every other path in this class
    /// already treats "file absent" as the canonical "nothing here" state). Method is the LAST
    /// non-null EmbeddingMethod seen across the results actually used, falling back to the existing
    /// file's Method when none of this run's used results carry one; ExtractedAtUtc is
    /// time.GetUtcNow().</summary>
    public async Task<IReadOnlyDictionary<string, string>> SaveDiarisationAsync(
        string sessionId, DiarisationCommit commit, string versionId,
        IReadOnlyDictionary<string, string>? participantClusterKeys,
        IReadOnlyDictionary<string, DiarisationResult>? resultsBySource, CancellationToken ct)
    {
        var remap = await RunForSessionAsync<IReadOnlyDictionary<string, string>>(sessionId, async inner =>
        {
            var validate = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(inner);
            if (validate is null)
                return new Dictionary<string, string>();            // deleted mid-run guard
            EnsureKnownVersion(sessionId, versionId, validate);

            // 1) merge into speakers.json (pin- and ownership-preserving) and save FIRST
            //    (source of truth). Owned keys come from the CURRENT meta.json under this
            //    same gate, not a caller snapshot.
            var metaStore = new MetadataStore(paths.MetaJson(sessionId));
            var meta = await metaStore.LoadAsync(inner);
            var owned = meta?.Participants
                .Where(p => !string.IsNullOrEmpty(p.ClusterKey))
                .Select(p => p.ClusterKey!)
                .ToList() ?? [];
            var store = new SpeakersStore(paths.SpeakersJson(sessionId, versionId));
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

            // 1c) per-cluster embeddings (voiceprint design 2026-07-25, fix round 1 findings 1+3):
            //     DERIVED sidecar, keyed by the keys that actually landed in speakers.json (remap
            //     applied - see doc comment above). Sources not in this commit keep their existing
            //     entries untouched; this commit's sources' stale entries are ALWAYS dropped (even
            //     when no fresh embeddings replace them) because a re-diarised source's un-reasserted
            //     stale entry would otherwise name a different voice than speakers.json now does.
            //     resultsBySource == null is the only "leave the file exactly as-is" path (legacy
            //     caller, no run information at all).
            if (resultsBySource is not null)
            {
                var embStore = new ClusterEmbeddingsStore(paths.EmbeddingsJson(sessionId, versionId));
                var existingEmb = await embStore.LoadAsync(inner);
                var entries = new Dictionary<string, float[]>();
                var rePrefixesEmb = commit.Sources.Select(s => s.ToString() + ":").ToList();
                if (existingEmb is not null)
                    foreach (var (k, v) in existingEmb.Entries)
                        if (!rePrefixesEmb.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
                            entries[k] = v;
                string method = existingEmb?.Method ?? "";
                // Only a source THIS commit actually re-diarised has a FreshKeyRemap entry - a
                // result for a source NOT in commit.Sources (finding 1) is ignored entirely rather
                // than written under its raw, unremapped keys.
                var commitSourceKeys = commit.Sources.Select(s => s.ToString()).ToHashSet(StringComparer.Ordinal);
                foreach (var (sourceKey, dr) in resultsBySource)
                {
                    if (!commitSourceKeys.Contains(sourceKey)) continue;
                    if (dr.ClusterEmbeddings is null) continue;
                    method = dr.EmbeddingMethod ?? method;
                    foreach (var (clusterId, vec) in dr.ClusterEmbeddings)
                    {
                        var rawKey = $"{sourceKey}:{clusterId}";
                        var finalKey = result.FreshKeyRemap.TryGetValue(rawKey, out var nk) ? nk : rawKey;
                        entries[finalKey] = vec;
                    }
                }
                if (entries.Count > 0)
                    await embStore.SaveAsync(new ClusterEmbeddings
                    { Method = method, ExtractedAtUtc = time.GetUtcNow(), Entries = entries }, inner);
                else
                    embStore.Delete();
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
        // Speakers overlay/ownership changed (or the session vanished mid-run - the re-index then
        // simply drops the entry). Unconditional: cheaper than threading a wrote flag out.
        RaiseSessionContentChanged(sessionId);
        return remap;
    }

    /// <summary>Rename already-committed diarisation clusters (design 2026-07-28 task 4): writes
    /// ONLY speakers.json Names + SuggestionProvenance, participant ClusterKey ownership, and the
    /// projections. Used when a user reopens Split Speakers on a diarised session and types a name,
    /// so renaming never costs a second diarisation run.
    ///
    /// Deliberately NOT routed through SaveDiarisationAsync/SpeakersMerge. Merge exists to protect
    /// pinned and participant-owned clusterKeys from a FRESH run by remapping colliding fresh keys
    /// to unused ids; on a rename the "fresh" keys ARE the existing keys, so a pinned key present in
    /// the commit would collide with itself and be remapped away, duplicating one voice across two
    /// rows. A rename also must not restamp Method/DiarisedAtUtc, must not re-derive embeddings.json
    /// (the vectors describe the run, not the label), and must not flip Diarised (already true).
    ///
    /// <paramref name="names"/> is clusterKey -> display name; keys absent from the existing overlay
    /// are ignored rather than invented. <paramref name="provenance"/> is merged in the same shape.
    /// Never flips meta.Edited/LastEditedAtUtc (reserved for manual corrections).
    /// <paramref name="versionId"/> is validated against the session's recorded versions and is
    /// never re-resolved from disk (the F1 fix - see EnsureKnownVersion).
    /// Returns false (writing nothing) when the session or its speakers overlay is absent.
    ///
    /// <paramref name="participantClusterKeys"/> maps participant Id -> clusterKey, and carries BOTH
    /// ownership rules, scoped by <paramref name="sources"/> exactly as
    /// <see cref="SaveDiarisationAsync(string, DiarisationCommit, string, IReadOnlyDictionary{string, string}?, IReadOnlyDictionary{string, DiarisationResult}?, CancellationToken)"/>
    /// scopes by <c>commit.Sources</c>: a participant named in the map takes that key, and a
    /// participant whose existing ClusterKey belongs to an IN-SCOPE source but is NOT re-asserted
    /// has it CLEARED to null. No remap translation either way - these keys already landed.
    ///
    /// The clear is not optional bookkeeping, it is the evidentiary half (fix round 1, C1).
    /// NameResolver.ResolveClusterKey (NameResolver.cs:62-74) ranks the participant-ownership tier
    /// AHEAD of speakers.Names, so an owner left behind after the user renames that cluster to
    /// something else keeps overriding the rendered transcript: speakers.json would say "Ms Chen"
    /// while the read view, exports and search index all still say "Barrister". The two ways in are
    /// a rename onto FREE TEXT (no candidate matches, so the map is empty for that key) and a
    /// rename onto a DIFFERENT candidate (which would otherwise leave two participants claiming one
    /// cluster, with NameResolver picking by list order).
    ///
    /// Therefore <paramref name="participantClusterKeys"/> must be passed ALWAYS, possibly EMPTY -
    /// an empty map means "these sources re-assert no ownership at all", which is a real
    /// instruction, not a no-op. Only <c>null</c> means "leave meta.json's Participants completely
    /// untouched" (the legacy/no-ownership-semantics caller), matching SaveDiarisationAsync's own
    /// null contract. <paramref name="sources"/> bounds the clear so the other side's ownership -
    /// and any owner of a source this confirm does not cover - always passes through untouched.</summary>
    public async Task<bool> RenameSpeakersAsync(string sessionId, string versionId,
        IReadOnlyList<SourceKind> sources,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, string>? participantClusterKeys,
        IReadOnlyDictionary<string, SuggestionProvenanceEntry>? provenance,
        CancellationToken ct)
    {
        bool wrote = await RunForSessionAsync(sessionId, async inner =>
        {
            var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(inner);
            if (session is null) return false;
            EnsureKnownVersion(sessionId, versionId, session);

            var store = new SpeakersStore(paths.SpeakersJson(sessionId, versionId));
            var existing = await store.LoadAsync(inner);
            if (existing is null) return false;      // nothing committed here to rename

            // Only rename keys the overlay already knows: a stale VM row must never invent a
            // cluster that no assignment points at.
            var mergedNames = new Dictionary<string, string>(existing.Names, StringComparer.Ordinal);
            bool changed = false;
            foreach (var (key, name) in names)
            {
                if (!mergedNames.ContainsKey(key)) continue;
                if (string.Equals(mergedNames[key], name, StringComparison.Ordinal)) continue;
                mergedNames[key] = name;
                changed = true;
            }

            var mergedProvenance = new Dictionary<string, SuggestionProvenanceEntry>(
                existing.SuggestionProvenance, StringComparer.Ordinal);
            if (provenance is not null)
                foreach (var (key, entry) in provenance)
                {
                    if (!mergedNames.ContainsKey(key)) continue;
                    // Equality-gate exactly like the Names loop above: SuggestionProvenanceEntry is a
                    // record, so == is value equality. Without this, resubmitting an unchanged
                    // provenance map writes the file and raises SessionContentChanged for a no-op,
                    // which the event's contract at :40-47 forbids.
                    if (mergedProvenance.TryGetValue(key, out var already) && already == entry) continue;
                    mergedProvenance[key] = entry;
                    changed = true;
                }

            if (changed)
                await store.SaveAsync(
                    existing with { Names = mergedNames, SuggestionProvenance = mergedProvenance }, inner);

            // Participant ClusterKey ownership - see the doc comment above for why BOTH rules are
            // here. No FreshKeyRemap translation: these keys are the ones already on disk, not
            // pre-merge fresh keys.
            //
            // `is not null`, NOT `{ Count: > 0 }` (fix round 1, C1). The Count test made the clear
            // branch unreachable: a rename onto FREE TEXT matches no candidate and therefore always
            // arrives with an EMPTY map, which is precisely the case where a stale owner has to be
            // released. Only null means "leave meta.json alone".
            if (participantClusterKeys is not null)
            {
                var metaStore = new MetadataStore(paths.MetaJson(sessionId));
                var meta = await metaStore.LoadAsync(inner);
                if (meta is not null)
                {
                    // Scoped exactly as SaveDiarisationAsync scopes its own clear by commit.Sources
                    // (:494): only an owner whose key belongs to a source THIS confirm re-asserts is
                    // eligible to be released. The other side's ownership passes through untouched.
                    var inScopePrefixes = sources.Select(s => s.ToString() + ":").ToList();
                    var updated = meta.Participants.Select(p =>
                    {
                        if (participantClusterKeys.TryGetValue(p.Id, out var key))
                            return p with { ClusterKey = key };
                        if (p.ClusterKey is string ck &&
                            inScopePrefixes.Any(prefix => ck.StartsWith(prefix, StringComparison.Ordinal)))
                            return p with { ClusterKey = null };
                        return p;
                    }).ToList();
                    if (!updated.SequenceEqual(meta.Participants))   // records: value equality
                    {
                        await metaStore.SaveAsync(meta with { Participants = updated }, inner);
                        changed = true;
                    }
                }
            }

            if (!changed) return false;

            await new SessionWriter(paths, settings.Current, time)
                .RegenerateProjectionsAsync(sessionId, inner);
            return true;
        }, ct);

        if (wrote) RaiseSessionContentChanged(sessionId);   // names feed the search index + read view
        return wrote;
    }

    /// <summary>Global voiceprint purge (voiceprint design 2026-07-25): deletes every session's
    /// embeddings.json (root version AND every versions\* dir), clears every SuggestionProvenance
    /// map, and strips all People enrollments. Deletes ONLY derived biometric data - audio,
    /// transcripts, and speaker NAMES are never touched (evidentiary firewall); people themselves
    /// survive with their Name, only Voiceprint is emptied. Each session's work runs under its own
    /// existing per-session gate, like every other write in this class.
    /// Per-id resilient (fix round 1, finding 2 - mirrors <see cref="RecoverAllAsync"/>'s
    /// RecoveryScanResult pattern): a malformed speakers.json throws JsonException, and a
    /// forward-versioned one throws NotSupportedException, straight out of SpeakersStore.LoadAsync.
    /// Retrospective voiceprint deletion is a first-class user requirement, so one corrupt session
    /// must never abort the rest of the sweep NOR skip the People enrollment strip below - the most
    /// identifying biometric data in the product must never survive a "purge" just because an
    /// unrelated session's speakers.json is unreadable. Failures are collected per session id
    /// instead; only cancellation propagates.</summary>
    public async Task<VoiceprintPurgeResult> PurgeVoiceprintDataAsync(CancellationToken ct)
    {
        int touched = 0;
        var failures = new List<(string Id, string Error)>();
        if (Directory.Exists(paths.SessionsDir))
        {
            // Fix round 1, finding E(a): Directory.EnumerateDirectories is lazy - its first
            // MoveNext() (i.e. entering the foreach below) can throw OUTSIDE any per-session try,
            // e.g. a Directory.Exists-then-delete TOCTOU or an enumeration-level IO error. That
            // would skip the whole sweep AND the People strip below, which this method's own doc
            // comment claims never happens. Materializing eagerly, inside its own try, turns any
            // such failure into an ordinary collected failure instead - the People strip after
            // this block is then always reached (short of real cancellation, which still
            // propagates immediately like every other method in this class).
            List<string> dirs;
            try { dirs = Directory.EnumerateDirectories(paths.SessionsDir).ToList(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(("<sessions>", ex.Message));
                dirs = [];
            }

            foreach (var dir in dirs)
            {
                var sessionId = Path.GetFileName(dir);
                try
                {
                    bool any = await RunForSessionAsync(sessionId, async inner =>
                    {
                        bool didAny = false;
                        var versionIds = new List<string> { TranscriptVersions.Root };
                        var versionsDir = paths.VersionsDir(sessionId);
                        if (Directory.Exists(versionsDir))
                            versionIds.AddRange(
                                Directory.EnumerateDirectories(versionsDir).Select(Path.GetFileName)!);
                        foreach (var versionId in versionIds)
                        {
                            var embStore = new ClusterEmbeddingsStore(paths.EmbeddingsJson(sessionId, versionId));
                            if (File.Exists(paths.EmbeddingsJson(sessionId, versionId)))
                            { embStore.Delete(); didAny = true; }

                            var spStore = new SpeakersStore(paths.SpeakersJson(sessionId, versionId));
                            var speakers = await spStore.LoadAsync(inner);
                            if (speakers is not null && speakers.SuggestionProvenance.Count > 0)
                            {
                                await spStore.SaveAsync(speakers with
                                { SuggestionProvenance = new Dictionary<string, SuggestionProvenanceEntry>() }, inner);
                                didAny = true;
                            }
                        }
                        return didAny;
                    }, ct);
                    if (any) touched++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { failures.Add((sessionId, ex.Message)); }
            }
        }
        // ALWAYS strips People enrollments, even when one or more sessions above failed (finding 2
        // (a): this must never be sequenced such that a per-session exception skips it).
        // Fix round 1, finding E(b): PeopleStore.LoadAsync can itself throw (a corrupt or
        // forward-versioned people.json, same JsonException/NotSupportedException shape as a
        // per-session speakers.json failure above) - that must be reported as a failure entry, not
        // thrown, which would otherwise discard the already-collected per-session touched
        // count/failures computed above (the exact regression the per-session try/catch pattern
        // was introduced to prevent).
        var peopleStore = new PeopleStore(paths.PeopleJson);
        PeopleRegistry? registry = null;
        try { registry = await peopleStore.LoadAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add(("people.json", ex.Message));
        }
        if (registry is not null && registry.People.Any(p => p.Voiceprint.Count > 0))
            await peopleStore.SaveAsync(PeopleRegistryOps.ClearAllVoiceprints(registry), ct);
        return new VoiceprintPurgeResult(touched, failures);
    }

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
        RaiseSessionContentChanged(sessionId);      // the re-index drops the deleted session's entry
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
                if (did) { recovered.Add(id); onRecovered?.Invoke(id); RaiseSessionContentChanged(id); }
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
                // A re-render re-applies the CURRENT vocabulary, which the index bakes into its
                // corrected text - so bulk regenerate and matter cascades must re-index too (the
                // freshness stamps alone cannot see a vocabulary change; design 2.1 stamp set).
                RaiseSessionContentChanged(id);
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
    /// projection under the session gate; page size is the ONE machine-locale dependence (RegionInfo).
    /// A non-null excerpt (design 2026-08-04 section 8) filters rows via ExcerptSelector.Select
    /// BEFORE rendering and stamps the ACTUAL selected span onto provenance - null exports the
    /// complete transcript, unchanged.</summary>
    public Task ExportDocxAsync(string sessionId, string destPath, ExportOptions options,
        ExcerptRange? excerpt, CancellationToken ct)
        => ExportWithOutputCleanupAsync(destPath, markCreated => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                throw new InvalidOperationException("The session no longer exists.");
            var loaded = await SessionProjectionLoader.LoadAsync(paths, settings.Current, time, sessionId, ct: inner);
            var summary = await LoadSummaryAsync(sessionId, options, loaded, inner);
            var pageSize = DocxRenderer.PageSizeForRegion(RegionInfo.CurrentRegion);
            var rows = excerpt is null ? loaded.Rows : ExcerptSelector.Select(loaded.Rows, excerpt);
            var provenance = ProvenanceFor(loaded) with { ExcerptSpan = SpanLabel(rows, excerpt, loaded) };
            // ReadWrite (not Write): DocumentFormat.OpenXml's package model reads back from the
            // stream while building the OPC zip structure, so Write-only throws
            // OpenXmlPackageException("The stream was not opened for reading.").
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            markCreated();
            DocxRenderer.Write(fs, loaded.Header, loaded.TextView, provenance, summary,
                rows, settings.Current.Timestamps, pageSize, options);
            return true;
        }, ct));

    /// <summary>Export one session as a formatted .md transcript (design 2026-07-18 section 3).
    /// Line-for-line mirror of ExportDocxAsync: session gate, output-file-only cleanup on failure,
    /// shared SessionProjectionLoader read, and the IDENTICAL ProvenanceFor composition - including
    /// the same excerpt row-filtering (design 2026-08-04 section 8): a non-null excerpt narrows
    /// rows and provenance identically. The document is rendered BEFORE the output stream opens,
    /// so a projection/render failure leaves a pre-existing Save-As target intact (markCreated
    /// contract). UTF-8 without BOM.</summary>
    public Task ExportMarkdownAsync(string sessionId, string destPath, ExportOptions options,
        ExcerptRange? excerpt, CancellationToken ct)
        => ExportWithOutputCleanupAsync(destPath, markCreated => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                throw new InvalidOperationException("The session no longer exists.");
            var loaded = await SessionProjectionLoader.LoadAsync(paths, settings.Current, time, sessionId, ct: inner);
            var summary = await LoadSummaryAsync(sessionId, options, loaded, inner);
            var rows = excerpt is null ? loaded.Rows : ExcerptSelector.Select(loaded.Rows, excerpt);
            var provenance = ProvenanceFor(loaded) with { ExcerptSpan = SpanLabel(rows, excerpt, loaded) };
            string markdown = MarkdownRenderer.Write(loaded.Header, loaded.TextView,
                provenance, summary, rows, settings.Current.Timestamps, options);
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            markCreated();
            await fs.WriteAsync(Encoding.UTF8.GetBytes(markdown), inner);   // GetBytes emits no BOM
            return true;
        }, ct));

    /// <summary>Export one session as a formatted .txt transcript (design 2026-08-04 section 3).
    /// Line-for-line mirror of ExportMarkdownAsync: session gate, output-file-only cleanup on
    /// failure, shared SessionProjectionLoader read, and the IDENTICAL ProvenanceFor composition -
    /// including the same excerpt row-filtering (design 2026-08-04 section 8): a non-null excerpt
    /// narrows rows and provenance identically. The document is rendered BEFORE the output stream
    /// opens, so a projection/render failure leaves a pre-existing Save-As target intact
    /// (markCreated contract). UTF-8 without BOM; PlainTextRenderer.Write supplies the CRLF line
    /// endings.</summary>
    public Task ExportTextAsync(string sessionId, string destPath, ExportOptions options,
        ExcerptRange? excerpt, CancellationToken ct)
        => ExportWithOutputCleanupAsync(destPath, markCreated => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                throw new InvalidOperationException("The session no longer exists.");
            var loaded = await SessionProjectionLoader.LoadAsync(paths, settings.Current, time, sessionId, ct: inner);
            var summary = await LoadSummaryAsync(sessionId, options, loaded, inner);
            var rows = excerpt is null ? loaded.Rows : ExcerptSelector.Select(loaded.Rows, excerpt);
            var provenance = ProvenanceFor(loaded) with { ExcerptSpan = SpanLabel(rows, excerpt, loaded) };
            string text = PlainTextRenderer.Write(loaded.Header, loaded.TextView,
                provenance, summary, rows, settings.Current.Timestamps, options);
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            markCreated();
            await fs.WriteAsync(Encoding.UTF8.GetBytes(text), inner);   // GetBytes emits no BOM
            return true;
        }, ct));

    /// <summary>Compose the export-only provenance block (design 2026-08-03 section 1). Composed
    /// HERE, where footerText used to compose, so the renderers stay pure serializers. Shared by
    /// ALL THREE textual formats so they can never disagree about provenance. Public static: tests
    /// drive the mapping directly (no InternalsVisibleTo in this repo - the
    /// RecordingConsoleViewModel.PreflightLine precedent), since neither renderer surfaces most
    /// of these fields yet (InProgress/AudioFileName/AudioSha256 are Task 8's to render).</summary>
    public static ExportProvenance ProvenanceFor(LoadedProjection loaded)
        => new()
        {
            VersionId = loaded.VersionId,
            Model = loaded.Header.Model,
            Backend = loaded.Header.Backend,
            AudioFileName = loaded.Session.ImportedSource?.FileName,
            AudioSha256 = loaded.Session.ImportedSource?.Sha256,
            InProgress = loaded.Session.EndedAtUtc is null,
        };

    /// <summary>The excerpt span label (design 2026-08-04 section 8): the ACTUAL outward-snapped
    /// span of the selected rows, not the requested range - reporting the request over
    /// outward-snapped content would be a small lie in an evidentiary document. Null for a
    /// complete transcript.</summary>
    private static string? SpanLabel(IReadOnlyList<DisplayRow> rows, ExcerptRange? excerpt,
        LoadedProjection loaded)
    {
        if (excerpt is null) return null;
        (long fromMs, long toMs) = ExcerptSelector.ActualSpan(rows);
        long durationMs = Math.Max(loaded.Session.DurationMs,
            loaded.Rows.Count > 0 ? loaded.Rows.Max(r => r.EndMs) : 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"{Hms(fromMs)}-{Hms(toMs)} of {Hms(durationMs)}");
    }

    /// <summary>HH:MM:SS, but with UNBOUNDED hours (design 2026-08-04 section 8 review finding 1):
    /// TimeSpan's own "hh" custom specifier is the Hours COMPONENT (0-23, days split off
    /// separately), so a 25-hour span would silently print "01:00:00" with no exception - a
    /// 24h-wrapped total is exactly the small lie the excerpt banner exists to prevent, since the
    /// "of TOTAL" figure is what a reader uses to judge how much of the record they're missing.
    /// (long)TotalHours never wraps and is never smaller than the true elapsed time; minutes/
    /// seconds stay the normal 0-59 components, so the shape is unchanged for any call under 24h.</summary>
    private static string Hms(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return string.Create(CultureInfo.InvariantCulture,
            $"{(long)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}");
    }

    /// <summary>Latest-summary seam (design 2026-08-04 section 7). A settable property, not a
    /// constructor parameter: this is a primary-constructor class whose four parameters are
    /// repeated in every test construction, and a fifth would break all of them (the
    /// StartupScanTask precedent above). Bound by the composition root to the SINGLE composed
    /// SummaryStore - never a second store (house rule). Null = no summary, which is what every
    /// unit test gets for free.</summary>
    public Func<string, CancellationToken, Task<SummaryVersion?>>? LatestSummaryProvider { get; set; }

    /// <summary>The newest summary version, or null. summaries.json is APPEND-ONLY and
    /// newest-LAST, so this is versions[^1] - the same pick App.xaml.cs already makes for the
    /// summary-status provider and the matter-summary sources. A named helper rather than an
    /// inline expression in the composition root so the choice is testable.</summary>
    public static SummaryVersion? Latest(IReadOnlyList<SummaryVersion> versions)
        => versions.Count > 0 ? versions[^1] : null;

    /// <summary>Compose the export summary block (design 2026-08-04 section 7). Staleness is
    /// EXPORTED and LABELLED - never silently dropped, never silently passed off as current.
    /// Two independent conditions, because the Stale flag alone misses the case where a summary
    /// is current against its own transcript version while the export renders a different one.
    /// sessionOffset (not ToLocalTime) keeps the rendered timestamp deterministic: Round 1 pinned
    /// page size as the ONE machine-locale dependence in an export. Public static so tests drive
    /// the mapping directly - the ProvenanceFor precedent (no InternalsVisibleTo in this repo).</summary>
    public static ExportSummary? SummaryFor(SummaryVersion? version, string renderedVersionId,
        TimeSpan sessionOffset)
    {
        if (version is null || string.IsNullOrWhiteSpace(version.ContentMarkdown)) return null;
        var notices = new List<string>();
        if (version.Stale)
            notices.Add("OUT OF DATE: the transcript changed after this summary was generated.");
        if (!string.Equals(version.SourceTranscriptVersion, renderedVersionId, StringComparison.Ordinal))
            notices.Add(string.Create(CultureInfo.InvariantCulture,
                $"Generated against transcript {version.SourceTranscriptVersion}; this document is {renderedVersionId}."));
        return new ExportSummary
        {
            ContentMarkdown = version.ContentMarkdown,
            ProvenanceLine = string.Create(CultureInfo.InvariantCulture,
                $"generated {version.CreatedAt.ToOffset(sessionOffset):yyyy-MM-dd HH:mm}, "
                + $"{version.Model.File} ({version.Model.Backend.ToUpperInvariant()})"),
            StaleNotice = notices.Count == 0 ? null : string.Join(" ", notices),
        };
    }

    /// <summary>Resolve the summary for one export: honours options.IncludeSummary (opt-in,
    /// default OFF) and a null LatestSummaryProvider. Called inside the session gate by the three
    /// textual export methods.</summary>
    private async Task<ExportSummary?> LoadSummaryAsync(string sessionId, ExportOptions options,
        LoadedProjection loaded, CancellationToken ct)
    {
        if (!options.IncludeSummary || LatestSummaryProvider is null) return null;
        var version = await LatestSummaryProvider(sessionId, ct);
        return SummaryFor(version, loaded.VersionId, loaded.StartedLocal.Offset);
    }

    /// <summary>Filename-template tokens for one session (design 2026-08-04 section 6). Loaded
    /// under the session gate because {date}/{matter}/{version} live in the projection, which the
    /// export dialog does not hold - it has only a session id and a title. Called once, before
    /// Save-As. Invariant-culture date/time by construction, like every other exported string.</summary>
    public Task<IReadOnlyDictionary<string, string>> FilenameTokensAsync(string sessionId,
        CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            var loaded = await SessionProjectionLoader.LoadAsync(paths, settings.Current, time, sessionId, ct: inner);
            return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = loaded.Meta.Title,
                ["date"] = loaded.StartedLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["time"] = loaded.StartedLocal.ToString("HHmm", CultureInfo.InvariantCulture),
                ["matter"] = loaded.MatterDisplays.Count > 0 ? loaded.MatterDisplays[0] : "",
                ["version"] = loaded.VersionId,
                ["id"] = sessionId,
            };
        }, ct);

    /// <summary>Parse and validate an excerpt range (design 2026-08-04 section 8). Lives HERE,
    /// not in the view model: the dialog has only a session id and a title - neither the session's
    /// local start (wallclock mode) nor its duration (bounds). Called BEFORE the Save-As picker so
    /// the user learns about a bad range before choosing a destination. One parsing
    /// implementation, in the only place that holds the truth, directly unit-testable without a VM.
    ///
    /// This is a SEPARATE gate acquisition from the export that follows, so the projection loads
    /// twice. Accepted: the resolved range is a pair of millisecond offsets, which stays
    /// meaningful against a transcript that grew between the two loads (a live session), and the
    /// export always re-derives its rows from its own fresh load. Holding the gate across a modal
    /// Save-As would block the capture pipeline.</summary>
    public Task<ExcerptRange> ResolveExcerptAsync(string sessionId, string fromText, string toText,
        CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                throw new InvalidOperationException("The session no longer exists.");
            var loaded = await SessionProjectionLoader.LoadAsync(paths, settings.Current, time, sessionId, ct: inner);
            string mode = settings.Current.Timestamps;
            // A live session has DurationMs 0 until it finalizes; fall back to the rows so a
            // mid-recording excerpt is still bounded by something real.
            long durationMs = Math.Max(loaded.Session.DurationMs,
                loaded.Rows.Count > 0 ? loaded.Rows.Max(r => r.EndMs) : 0);

            long from = 0, to = durationMs;
            if (!string.IsNullOrWhiteSpace(fromText)
                && !TimestampParser.TryParse(fromText, mode, loaded.StartedLocal, out from))
                throw new InvalidOperationException($"'{fromText}' is not a time this transcript uses.");
            if (!string.IsNullOrWhiteSpace(toText)
                && !TimestampParser.TryParse(toText, mode, loaded.StartedLocal, out to))
                throw new InvalidOperationException($"'{toText}' is not a time this transcript uses.");
            if (from >= to)
                throw new InvalidOperationException("The excerpt's start must come before its end.");
            if (from < 0 || to > durationMs)
                throw new InvalidOperationException("That range falls outside the recording.");

            var range = new ExcerptRange(from, to);
            if (ExcerptSelector.Select(loaded.Rows, range).Count == 0)
                throw new InvalidOperationException("That range contains no transcript content.");
            return range;
        }, ct);

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
