using System.Collections.Concurrent;
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

    /// <summary>Save meta.json (the ONLY file user metadata edits touch - spec 1.2/1.4), then
    /// regenerate projections under the same per-session gate with a FRESH SessionWriter built
    /// from settings.Current (so timestamp-style etc. reflect the latest save), then apply the
    /// matter-tag delta computed against previousMatterIds to the index.</summary>
    public async Task SaveMetaAsync(string sessionId, SessionMeta meta,
        IReadOnlyCollection<string> previousMatterIds, CancellationToken ct)
    {
        await RunForSessionAsync(sessionId, async inner =>
        {
            await new MetadataStore(paths.MetaJson(sessionId)).SaveAsync(meta, inner);
            await new SessionWriter(paths, settings.Current, time)
                .RegenerateProjectionsAsync(sessionId, inner);
            return true;
        }, ct);

        var added = meta.MatterIds.Except(previousMatterIds, StringComparer.Ordinal).ToList();
        var removed = previousMatterIds.Except(meta.MatterIds, StringComparer.Ordinal).ToList();
        if (added.Count > 0 || removed.Count > 0)
            await ApplyTagDeltaLockedAsync(added, removed, ct);
    }

    /// <summary>Whole-session delete to the Recycle Bin (design 3.4) - the caller has already
    /// closed any open read views (WindowRegistry.CloseAllFor) so no handle blocks the recycle.
    /// The delete runs under the session's gate; the index decrement follows.</summary>
    public async Task DeleteSessionAsync(string sessionId, IReadOnlyCollection<string> taggedMatterIds,
        CancellationToken ct)
    {
        await RunForSessionAsync(sessionId, async inner =>
        {
            await new SessionDeleter(paths, recycleBin).DeleteAsync(sessionId, inner);
            return true;
        }, ct);
        if (taggedMatterIds.Count > 0)
            await ApplyTagDeltaLockedAsync([], taggedMatterIds, ct);
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

    public Task<MattersIndex> ListMattersAsync(CancellationToken ct)
        => new MatterStore(paths.MattersDir).ListAsync(ct);

    public Task<Matter?> LoadMatterAsync(string matterId, CancellationToken ct)
        => new MatterStore(paths.MattersDir).LoadAsync(matterId, ct);

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
