using System.Text;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Assistant;

/// <summary>Per-session summarization orchestration (design 2026-07-18 section 7.4).
/// Gate (visibly queued behind recording) -> projection (ACTIVE version, corrections applied
/// - the same SessionProjectionLoader pipeline docx/search use) -> input shaping (roster
/// kept, timestamps stripped) -> fits-check at 80% of the 32k operating budget with
/// worst-case 2 chars/token -> single call or map-reduce (map output capped, hierarchical
/// reduce max depth 2, then an HONEST too-long error) -> append-only persistence with full
/// provenance (model file + sha256 + the backend ACTUALLY used). Empty output persists
/// NOTHING. The helper never writes files; this service owns persistence via SummaryStore.</summary>
public sealed class SummarizationService(
    StoragePaths paths,
    Func<Settings> settings,
    TimeProvider time,
    IAssistantJobRunner runner,
    SummaryStore store,
    AssistantGate gate,
    AssistantManifestCache models,
    Func<string, CancellationToken, Task<LoadedProjection>>? loadProjection = null)
{
    /// <summary>Map jobs run at a fixed mid-size ctx: big enough for meaty chunks, small
    /// enough to stay GPU-resident per the design 7.2 KV sizing math.</summary>
    public const int MapCtxTokens = 16384;

    private readonly Func<string, CancellationToken, Task<LoadedProjection>> _loadProjection =
        loadProjection ?? ((sessionId, ct)
            => SessionProjectionLoader.LoadAsync(paths, settings(), time, sessionId, ct));

    private readonly object _jobLock = new();
    private CancellationTokenSource? _activeJobCts;

    public async Task<SummaryVersion> SummarizeAsync(string sessionId,
        Action<AssistantEvent>? onEvent, Action<string>? onWaiting, CancellationToken ct)
    {
        using var lease = await gate.EnterAsync(onWaiting, ct);
        // Reverse direction of "one heavy engine at a time" (design 7.1): publish a linked CTS
        // for THIS running job only AFTER the lease is acquired, so a job still queued in
        // EnterAsync (not yet past the gate) never owns _activeJobCts - only the single job
        // that is actually running the engine does. The gate serializes execution, so at most
        // one job is ever running past this point; a single field is therefore safe.
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_jobLock) { _activeJobCts = jobCts; }
        var jobCt = jobCts.Token;
        try
        {
            var manifest = await models.GetAsync(jobCt);
            var pick = settings().Assistant.Model;
            var model = (pick is not null ? manifest.Installed.FirstOrDefault(m => m.CanonicalName == pick) : null)
                ?? manifest.DefaultModel
                ?? throw new AssistantException(
                    "No assistant model is installed - see Settings > Assistant for fetch instructions.");

            // The helper's cuda-fell-to-cpu progress event (design 2026-07-23 section 5) is
            // provenance, not just UI: any fall across the job chain (map-reduce spawns one
            // helper per chunk) marks the whole version.
            bool cudaFell = false;
            Action<AssistantEvent> watchEvents = evt =>
            {
                if (evt is AssistantProgress p && p.Phase == AssistantWire.CudaFellPhase) cudaFell = true;
                onEvent?.Invoke(evt);
            };

            var loaded = await _loadProjection(sessionId, jobCt);
            var roster = loaded.Rows.Where(r => !r.IsMarker && r.DisplayName is not null)
                .Select(r => r.DisplayName!).Distinct().ToList();
            string preamble = AssistantInputShaper.BuildSpeakerPreamble(roster);
            string transcript = AssistantInputShaper.BuildTranscriptText(loaded.Rows);
            if (transcript.Length == 0)
                throw new AssistantException("This session has no transcript content to summarize.");

            string singlePrompt = AssistantPrompts.BuildSummaryPrompt(preamble, transcript);
            int est = TokenBudget.EstimateTokens(singlePrompt.Length);
            (string content, AssistantDone done) =
                !TokenBudget.NeedsChunking(est + TokenBudget.OutputReserveTokens, TokenBudget.MaxCtxTokens)
                    ? await RunJobAsync(model, singlePrompt, TokenBudget.JobCtxTokens(est),
                        TokenBudget.OutputReserveTokens, watchEvents, jobCt)
                    : await MapReduceAsync(model, preamble, transcript, watchEvents, jobCt);

            if (string.IsNullOrWhiteSpace(content))
                throw new AssistantException("The model returned no content - nothing was saved.");

            var existing = await store.LoadAsync(sessionId, jobCt);
            var version = new SummaryVersion(
                Id: $"s{existing.Count + 1}",
                CreatedAt: time.GetUtcNow(),
                SourceTranscriptVersion: loaded.VersionId,
                Model: new AssistantModelRef(Path.GetFileName(model.FilePath), model.Sha256, done.Backend),
                PromptVersion: AssistantPrompts.PromptVersion,
                ContentMarkdown: content.Trim(),
                Stale: false, CudaFellToCpu: cudaFell);
            await store.AppendAsync(sessionId, version, jobCt);
            return version;
        }
        finally
        {
            lock (_jobLock) { if (ReferenceEquals(_activeJobCts, jobCts)) _activeJobCts = null; }
        }
    }

    /// <summary>Reverse direction of the one-heavy-engine rule (design 7.1): a recording START
    /// cancels the in-flight summarize job (if any) so the assistant yields the engine to live
    /// transcription. Non-blocking and off-thread by construction (CancelAfter schedules on the
    /// pool), so it is safe to call from SessionController.StateChanged - a worker-thread event
    /// that must not be blocked or re-entered. The cancelled job throws OperationCanceledException
    /// BEFORE any persist, so nothing is saved (a recoverable draft is the only loss).</summary>
    public void CancelForRecording()
    {
        CancellationTokenSource? cts;
        lock (_jobLock) { cts = _activeJobCts; }
        if (cts is null) return;
        try { cts.CancelAfter(TimeSpan.Zero); }   // pool-scheduled; never runs proc.Kill on the caller thread
        catch (ObjectDisposedException) { }        // job completed between the read and here - nothing to cancel
    }

    private async Task<(string, AssistantDone)> MapReduceAsync(AssistantModelInfo model,
        string preamble, string transcript, Action<AssistantEvent>? onEvent, CancellationToken ct)
    {
        var chunks = SplitIntoChunks(transcript, TokenBudget.ChunkBudgetChars(MapCtxTokens));
        var outputs = new List<string>();
        for (int i = 0; i < chunks.Count; i++)
        {
            onEvent?.Invoke(new AssistantProgress("map", i + 1, chunks.Count));
            var (text, _) = await RunJobAsync(model,
                AssistantPrompts.BuildMapPrompt(preamble, chunks[i], i + 1, chunks.Count),
                MapCtxTokens, TokenBudget.MapOutputCapTokens, onEvent, ct);
            outputs.Add(text);
        }

        // Hierarchical reduce, max depth 2 (design 7.4), then an honest too-long error.
        for (int depth = 1; depth <= TokenBudget.MaxReduceDepth; depth++)
        {
            string reducePrompt = AssistantPrompts.BuildReducePrompt(preamble, outputs);
            int est = TokenBudget.EstimateTokens(reducePrompt.Length);
            if (!TokenBudget.NeedsChunking(est + TokenBudget.OutputReserveTokens, TokenBudget.MaxCtxTokens))
            {
                onEvent?.Invoke(new AssistantProgress("reduce", depth, depth));
                return await RunJobAsync(model, reducePrompt, TokenBudget.JobCtxTokens(est),
                    TokenBudget.OutputReserveTokens, onEvent, ct);
            }
            // Batch the notes into groups that fit, reduce each, then loop one level deeper.
            var batches = new List<List<string>>();
            var current = new List<string>();
            int chars = 0;
            int budget = TokenBudget.ChunkBudgetChars(TokenBudget.MaxCtxTokens);
            foreach (var o in outputs)
            {
                if (current.Count > 0 && chars + o.Length > budget)
                { batches.Add(current); current = []; chars = 0; }
                current.Add(o);
                chars += o.Length;
            }
            if (current.Count > 0) batches.Add(current);
            if (batches.Count <= 1) break;   // cannot shrink further - fall through to the error
            var next = new List<string>();
            for (int b = 0; b < batches.Count; b++)
            {
                onEvent?.Invoke(new AssistantProgress("reduce", b + 1, batches.Count));
                var (text, _) = await RunJobAsync(model,
                    AssistantPrompts.BuildReducePrompt(preamble, batches[b]),
                    TokenBudget.MaxCtxTokens, TokenBudget.MapOutputCapTokens, onEvent, ct);
                next.Add(text);
            }
            outputs = next;
        }
        throw new AssistantException(
            "This session is too long for the configured model - the summary cannot be generated.");
    }

    private async Task<(string Text, AssistantDone Done)> RunJobAsync(AssistantModelInfo model,
        string prompt, int ctxTokens, int maxOutputTokens, Action<AssistantEvent>? onEvent, CancellationToken ct)
    {
        var request = new AssistantRequest("summarize", model.FilePath, ctxTokens, "auto",
            KeepAlive: false, AssistantWire.PromptPayload(prompt, maxOutputTokens));
        var sb = new StringBuilder();
        AssistantDone? done = null;
        await foreach (var evt in runner.RunAsync(request, ct))
        {
            onEvent?.Invoke(evt);
            switch (evt)
            {
                case AssistantChunk c: sb.Append(c.Text); break;
                case AssistantDone d: done = d; break;
                case AssistantError e: throw new AssistantException(e.Message);
            }
        }
        return done is null
            ? throw new AssistantException("assistant helper ended without a result")
            : (sb.ToString(), done);
    }

    /// <summary>Line-boundary chunking; a single line larger than the budget is hard-split
    /// (never an over-budget chunk - the gate must hold). Public: tests drive it directly.</summary>
    public static IReadOnlyList<string> SplitIntoChunks(string text, int chunkBudgetChars)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            while (line.Length > chunkBudgetChars)
            {
                if (sb.Length > 0) { chunks.Add(sb.ToString()); sb.Clear(); }
                chunks.Add(line[..chunkBudgetChars]);
                line = line[chunkBudgetChars..];
            }
            if (sb.Length > 0 && sb.Length + line.Length + 1 > chunkBudgetChars)
            { chunks.Add(sb.ToString()); sb.Clear(); }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }
}
