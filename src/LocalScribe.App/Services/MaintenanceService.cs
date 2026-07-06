using System.Collections.Concurrent;
using System.IO;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
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

    /// <summary>The one write path for diarisation (Stage 5 Task 7 + Stage 5.4 section 5.2):
    /// merge a fresh <see cref="DiarisationCommit"/> into speakers.json (pin- AND
    /// ownership-preserving via SpeakersMerge), flip session.Diarised, then regenerate
    /// projections - all under the same per-session gate SaveMetaAsync/SetArchivedAsync use.
    /// Participant-owned clusterKeys read from meta.json are protected like pins: a colliding
    /// fresh key is remapped away so a different voice can never be re-bound under a key a
    /// named identity owns. meta.json itself is READ-ONLY here - owned keys never change in a
    /// merge, and fresh-key ClusterKey fix-ups are the Split-confirm flow's job via the
    /// RETURNED remap (old fresh key -> final key; empty when nothing collided or the session
    /// was deleted mid-run). Write order matters: speakers.json (source of truth) FIRST, then
    /// the Diarised flag, then projections - so a crash between steps never advertises a
    /// diarisation whose overlay didn't land. Never flips meta.json Edited/LastEditedAtUtc
    /// (reserved for manual corrections) and NEVER deletes/touches audio for any
    /// AudioRetention value - the retained legs are primary evidence (no SessionDeleter,
    /// no IRecycleBin, no per-source removal here, ever).</summary>
    public Task<IReadOnlyDictionary<string, string>> SaveDiarisationAsync(
        string sessionId, DiarisationCommit commit, CancellationToken ct) =>
        RunForSessionAsync<IReadOnlyDictionary<string, string>>(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                return new Dictionary<string, string>();            // deleted mid-run guard

            // 1) merge into speakers.json (pin- and ownership-preserving) and save FIRST
            //    (source of truth). Owned keys come from the CURRENT meta.json under this
            //    same gate, not a caller snapshot.
            var meta = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(inner);
            var owned = meta?.Participants
                .Where(p => !string.IsNullOrEmpty(p.ClusterKey))
                .Select(p => p.ClusterKey!)
                .ToList() ?? [];
            var store = new SpeakersStore(paths.SpeakersJson(sessionId));
            var existing = await store.LoadAsync(inner);
            var result = SpeakersMerge.Merge(existing, commit, owned);
            await store.SaveAsync(result.Speakers, inner);

            // 2) flip session.Diarised (mirror the RecoverIfNeededAsync rewrite pattern).
            var sessionStore = new SessionStore(paths.SessionJson(sessionId));
            var session = await sessionStore.ReadAsync(inner);
            if (session is not null && !session.Diarised)
                await sessionStore.SaveAsync(session with { Diarised = true }, inner);

            // 3) re-render projections with the new speaker names.
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
    public async Task<RecoveryScanResult> RecoverAllAsync(CancellationToken ct)
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
                if (did) recovered.Add(id);
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
}
