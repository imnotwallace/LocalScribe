using System.Threading.Channels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Retranscription;

/// <summary>User inputs for one re-transcription run (design 2026-07-13 section 3.4).</summary>
public sealed record RetranscriptionRequest
{
    public string SessionId { get; init; } = "";
    /// <summary>Canonical model name (e.g. "base.en") - the dialog offers only canonical names
    /// of models on disk (ModelPaths.AvailableModels collapses quantized ggml files); Select
    /// re-canonicalizes defensively, and ModelFileResolver picks the FILE per backend.</summary>
    public string Model { get; init; } = "";
    public string Language { get; init; } = "auto";
    public VadOptions Vad { get; init; } = new();
    public TranscriptionWorkerOptions Worker { get; init; } = new() { LaggingDowngradeEnabled = false };
}

/// <summary>One progress tick for an in-flight re-transcription (2026-07-30). Fraction is 0..1
/// over the WHOLE run (every retained leg counts equally); Elapsed is wall time since the run
/// began; Eta is a rough remaining estimate, null until there is enough signal to project it
/// (&lt;2% done or &lt;1s elapsed). Emitted from the feed loop as audio is processed and once more
/// at 1.0 when transcription drains.</summary>
public sealed record RetranscriptionProgress(string SessionId, double Fraction, TimeSpan Elapsed, TimeSpan? Eta);

/// <summary>Re-transcribes a finalized session's retained legs into a NEW version folder
/// (design 2026-07-13 section 3). Mirrors OfflinePipelineRunner's VAD -> worker -> merger ->
/// writer-loop wiring (including the C1 fault guard) but targets versions\vN-model-date\ instead
/// of bootstrapping a new session. EVIDENTIARY CORE: the session root is the immutable v1 -
/// this class writes into the root ONLY the single session.json commit (Versions entry +
/// ActiveVersion flip, one atomic save). Before that commit the version folder is a partial
/// derived output and cancel/fault deletes it; after it, the version is evidence and is never
/// deleted. Guards (section 3.2): one run at a time; refuses while the live engine is busy
/// (liveEngineBusy probes SessionController State/PendingFinalize; the reverse direction is
/// SessionController.ExternalEngineBusy); refuses un-finalized sessions (a Recording/Finalizing/
/// Recovering session has EndedAtUtc null on disk).</summary>
public sealed class RetranscriptionRunner
{
    private const int FrameSamples = 512;

    private readonly StoragePaths _paths;
    private readonly Func<Settings> _settingsProvider;
    private readonly IEngineFactory _engineFactory;
    private readonly Func<ISpeechProbabilityModel> _vadModelFactory;
    private readonly IHardwareProbe _hardware;
    private readonly Func<IClock> _clockFactory;
    private readonly TimeProvider _time;
    private readonly Func<string?> _liveEngineBusy;
    private readonly Func<IReadOnlySet<string>> _availableModels;
    /// <summary>F2 fix (whole-branch review): runs the commit's read-append-flip session.json
    /// save under the SAME per-session gate the App-side writers (SetActiveVersionAsync, the
    /// diarisation Diarised flip, ...) use, so this runner's commit can never interleave with a
    /// concurrent App-side session.json rewrite and silently drop one or the other's write.
    /// Defaults to running the work inline (no gating) so Core's existing tests, which construct
    /// this class with no App-level gate to share, keep working unchanged; CompositionRoot.Build
    /// wires it to MaintenanceService.RunForSessionAsync in production.</summary>
    private readonly Func<string, Func<CancellationToken, Task>, Task> _runUnderGate;

    private string? _running;
    private CancellationTokenSource? _runCts;

    public RetranscriptionRunner(StoragePaths paths, Func<Settings> settingsProvider,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, Func<IClock> clockFactory, TimeProvider time,
        Func<string?> liveEngineBusy, Func<IReadOnlySet<string>>? availableModels = null,
        Func<string, Func<CancellationToken, Task>, Task>? runUnderGate = null)
        => (_paths, _settingsProvider, _engineFactory, _vadModelFactory, _hardware, _clockFactory,
            _time, _liveEngineBusy, _availableModels, _runUnderGate)
         = (paths, settingsProvider, engineFactory, vadModelFactory, hardware, clockFactory,
            time, liveEngineBusy, availableModels ?? ModelPaths.AvailableModels,
            runUnderGate ?? ((_, work) => work(CancellationToken.None)));

    /// <summary>The session id of the in-flight run, or null. Drives the Sessions-page
    /// "Re-transcribing..." chip and the controller's ExternalEngineBusy probe.</summary>
    public string? RunningSessionId => Volatile.Read(ref _running);

    /// <summary>After guards pass and the version folder exists - the chip flips on.</summary>
    public event Action<string>? RetranscriptionStarted;
    /// <summary>ALWAYS fires once per RunAsync (success, refusal, fault, or cancel), after
    /// RunningSessionId clears - mirrors SessionFinalizeCompleted's one-event-covers-all shape
    /// so the row upsert re-reads disk truth whatever happened.</summary>
    public event Action<string>? RetranscriptionCompleted;
    /// <summary>User-facing refusal/progress text (the composition routes it to the InfoBar).</summary>
    public event Action<string>? Notice;
    /// <summary>Per-run progress (0..1) with elapsed + rough ETA (2026-07-30), emitted from the
    /// feed loop as audio is processed and once at 1.0 when transcription drains. Fires on a
    /// BACKGROUND thread - the App marshals to the UI; throttled to ~1% steps so it never floods.
    /// A throwing subscriber is swallowed (like RetranscriptionCompleted) so it can never abort a run.</summary>
    public event Action<RetranscriptionProgress>? Progress;

    /// <summary>Cancels the in-flight run (the partial version folder is discarded); no-op when
    /// idle. Callable from ANY dialog instance - the run outlives the dialog that started it.
    /// Carried Minor #1 (whole-branch review): reads via Volatile.Read, matching _running's
    /// pattern, so a cross-thread Cancel can never see a stale null on a weak-memory architecture
    /// (win-arm64) and silently no-op.</summary>
    public void CancelCurrent()
    {
        try { Volatile.Read(ref _runCts)?.Cancel(); }
        catch (ObjectDisposedException) { }              // settled between the read and the call
    }

    public async Task<string?> RunAsync(RetranscriptionRequest request, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, request.SessionId, null) is not null)
        {
            Notice?.Invoke("A re-transcription is already running - wait for it to finish.");
            try { RetranscriptionCompleted?.Invoke(request.SessionId); } catch { }
            return null;
        }
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Volatile.Write(ref _runCts, runCts);
        try
        {
            return await RunCoreAsync(request, runCts.Token);
        }
        finally
        {
            Volatile.Write(ref _runCts, null);
            Volatile.Write(ref _running, null);
            // A throwing subscriber must never mask the run's own outcome (same wrap as
            // SessionFinalizeCompleted).
            try { RetranscriptionCompleted?.Invoke(request.SessionId); } catch { }
        }
    }

    private async Task<string?> RunCoreAsync(RetranscriptionRequest request, CancellationToken ct)
    {
        string id = request.SessionId;

        if (_liveEngineBusy() is string busy) { Notice?.Invoke(busy); return null; }

        var sessionStore = new SessionStore(_paths.SessionJson(id));
        var session = await sessionStore.ReadAsync(ct);
        if (session is null)
        {
            Notice?.Invoke("Session not found - it may have been deleted.");
            return null;
        }
        if (session.EndedAtUtc is null)
        {
            Notice?.Invoke("This session is not finalized yet (recording, finalizing, or "
                + "recovering) - re-transcription needs a finalized session.");
            return null;
        }

        var settings = _settingsProvider();
        var available = _availableModels();
        // Reuse the live selector verbatim: an explicit model is CANONICALIZED
        // (ModelFileResolver.CanonicalName - a quantized pick like "small.en-q8_0" selects its
        // canonical model; the FILE is picked per backend at engine creation), and a non-English
        // language strips the ".en" suffix to the multilingual weights (spec section 3).
        var (plan, _) = BackendSelector.Select(_hardware.Probe(),
            settings with { Model = request.Model, Language = request.Language }, available);
        if (!available.Contains(plan.ModelName))
        {
            Notice?.Invoke($"Model '{plan.ModelName}' is not downloaded. Run tools/fetch-models.ps1 "
                + "or pick another model.");
            return null;
        }

        var legs = ResolveLegs(id, session.RetainedAudioSources);
        if (legs.Count == 0)
        {
            Notice?.Invoke("No retained audio found for this session - nothing to re-transcribe.");
            return null;
        }

        string versionId = TranscriptVersions.NewId(NextVersionNumber(session), plan.ModelName,
            DateOnly.FromDateTime(_time.GetLocalNow().Date));
        string versionDir = _paths.VersionDir(id, versionId);
        Directory.CreateDirectory(versionDir);
        RetranscriptionStarted?.Invoke(id);

        // Progress (2026-07-31): driven by TRANSCRIPTION completion, not the VAD feed. The feed
        // races ahead to fill the bounded worker queue (QueueCapacity=64) in seconds, then blocks on
        // EnqueueAsync backpressure - so feed position froze the old bar at ~queue-depth and the ETA
        // was extrapolated from that initial burst (a 21-min run showed ~10% / "~28s left" then sat
        // there for the whole transcription). Instead: denominator = sum of each retained leg's
        // ACTUAL decoded duration (header read, not session.DurationMs which drifts for
        // recovered/imported sessions); numerator = the end position of the last TRANSCRIBED segment
        // per source. Legs feed sequentially (Local then Remote) and the single worker transcribes
        // FIFO, so per-source max-end is monotonic and reaches the total (bar the final leg's trailing
        // silence, which the force(1.0) at drain covers).
        var legDurationsMs = new Dictionary<SourceKind, long>();
        foreach (var (path, kind) in legs) legDurationsMs[kind] = FlacPcmReader.DurationMs(path);
        long totalAudioMs = legDurationsMs.Values.Sum();
        var transcribedEndMs = new Dictionary<SourceKind, long>();   // per-source max transcribed end
        var runStart = _time.GetUtcNow();
        double lastEmittedFraction = -1;
        void EmitProgress(double fraction, bool force = false)
        {
            fraction = Math.Clamp(fraction, 0, 1);
            if (!force && fraction - lastEmittedFraction < 0.01) return;
            lastEmittedFraction = fraction;
            TimeSpan elapsed = _time.GetUtcNow() - runStart;
            TimeSpan? eta = fraction is > 0.02 and < 1.0 && elapsed > TimeSpan.FromSeconds(1)
                ? elapsed * ((1 - fraction) / fraction)
                : null;
            try { Progress?.Invoke(new RetranscriptionProgress(id, fraction, elapsed, eta)); }
            catch { /* a throwing subscriber must never abort the run */ }
        }

        bool committed = false;
        try
        {
            // CURRENT global + matter vocabulary as prompt bias (design section 3.2) - the same
            // load-skip-missing shape SessionController.StartAsync uses.
            var meta = await new MetadataStore(_paths.MetaJson(id)).LoadAsync(ct);
            IReadOnlyList<string> matterIds = meta?.MatterIds ?? [];
            var mattersById = new Dictionary<string, Matter>();
            var matterStore = new MatterStore(_paths.MattersDir);
            foreach (string mid in matterIds)
            {
                var m = await matterStore.LoadAsync(mid, ct);
                if (m is not null) mattersById[mid] = m;
            }
            string prompt = new VocabularyProvider(settings.Vocabulary, mattersById)
                .BuildInitialPrompt(matterIds);
            bool vocabularyApplied = prompt.Length > 0;

            var clock = _clockFactory();
            var language = new LanguageResolver(request.Language);
            var worker = new TranscriptionWorker(_engineFactory, plan, language, clock,
                request.Worker with { InitialPrompt = vocabularyApplied ? prompt : null });
            var merger = new TranscriptMerger(new TranscriptStore(_paths.TranscriptJsonl(id, versionId)));
            await merger.InitializeAsync(ct);

            // events -> single writer loop (event handlers must not await) - OfflinePipelineRunner's shape.
            var outbox = Channel.CreateUnbounded<object>();
            string? lastModel = null;
            string? lastWeightsFile = null;                     // exact ggml file (provenance)
            worker.SegmentTranscribed += ts =>
            {
                outbox.Writer.TryWrite(ts);
                // Progress numerator (2026-07-31): advance this source's transcribed end, then sum
                // every source's progress (clamped to its leg duration) over the total. SegmentTranscribed
                // fires sequentially from the single worker loop, and the terminal force(1.0) runs only
                // after `await workerLoop`, so no locking is needed around lastEmittedFraction.
                var src = ts.Audio.Source;
                if (!transcribedEndMs.TryGetValue(src, out long cur) || ts.Audio.EndMs > cur)
                    transcribedEndMs[src] = ts.Audio.EndMs;
                long pos = 0;
                foreach (var (k, endMs) in transcribedEndMs)
                    pos += legDurationsMs.TryGetValue(k, out long d) ? Math.Min(endMs, d) : endMs;
                EmitProgress(totalAudioMs > 0 ? (double)pos / totalAudioMs : 0);
            };
            worker.MarkerRaised += m => outbox.Writer.TryWrite(m);

            var writerLoop = Task.Run(async () =>
            {
                long lastEndMs = 0;
                await foreach (object item in outbox.Reader.ReadAllAsync(ct))
                {
                    if (item is TranscribedSegment ts)
                    {
                        var line = await merger.AppendSegmentAsync(ts, ct);
                        lastEndMs = Math.Max(lastEndMs, line.EndMs);
                        lastModel = ts.ModelName;
                        lastWeightsFile = ts.WeightsFile;
                    }
                    else if (item is string marker)
                    {
                        await merger.AppendMarkerAsync(marker, lastEndMs, ct);
                    }
                }
            }, ct);

            // Pool thread: the real engine ctor is a multi-second synchronous model load (same
            // reason SessionController.StartAsync wraps it).
            var workerLoop = Task.Run(() => worker.RunAsync(ct), CancellationToken.None);

            // C1 fault guard (see OfflinePipelineRunner): a faulted worker leaves the bounded
            // queue reader-less - cancel a feed-only token so EnqueueAsync aborts promptly; the
            // ORIGINAL exception is recovered by awaiting workerLoop below, never masked.
            using var feedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = workerLoop.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Cancel(),
                feedCts, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            bool faulted = false;
            try
            {
                try
                {
                    foreach (var (path, kind) in legs)
                    {
                        float[] samples = FlacPcmReader.ReadMono16k(path);
                        var segmenter = new SileroVadSegmenter(kind, request.Vad, _vadModelFactory());
                        await foreach (var segment in segmenter.SegmentAsync(
                            ToAsync(Frames(samples, kind)), feedCts.Token))
                            await worker.EnqueueAsync(segment, feedCts.Token);
                    }
                }
                catch (OperationCanceledException) when (feedCts.IsCancellationRequested
                                                         && !ct.IsCancellationRequested)
                {
                    // The worker faulted and the C1 guard aborted the feed; the real exception
                    // surfaces from `await workerLoop` below. A caller cancel (ct) rethrows.
                }
                finally { worker.Complete(); }
                await workerLoop;                                   // queue drained (spec 2.1 flush)
                EmitProgress(1.0, force: true);                     // transcription complete
            }
            catch { faulted = true; throw; }
            finally
            {
                outbox.Writer.TryComplete();
                if (faulted) { try { await writerLoop; } catch { } }
                else await writerLoop;
            }

            // Per-version isolation (design section 3.3): a fresh EMPTY edits.json; speakers.json
            // stays absent until Split runs against this version. No auto-carry, ever.
            await JsonFile.WriteAsync(_paths.EditsJson(id, versionId), new Edits(), ct);

            // COMMIT - one session.json save appends the entry AND flips ActiveVersion, so a
            // listed version is always a complete folder and a crash can never half-commit.
            // 2026-08-11: measured from the loaded runtime, request kept beside it on a divergence.
            var versionBackend = BackendRecord.For(plan.Backend, WhisperRuntimeBackend.Loaded);
            var entry = new TranscriptVersion
            {
                Id = versionId,
                Model = lastModel ?? plan.ModelName,
                // Exact file that ran (null: nothing transcribed) - the same weights provenance
                // SessionController.PersistFinalAsync and OfflinePipelineRunner record at root.
                WeightsFile = lastWeightsFile,
                Backend = versionBackend.Backend,
                BackendRequested = versionBackend.Requested,
                Language = language.Locked ?? request.Language,
                CreatedAtUtc = _time.GetUtcNow(),
                VocabularyApplied = vocabularyApplied,
            };
            // F2 fix (whole-branch review): this read-append-flip runs under the injected
            // per-session gate so it can never interleave with a concurrent App-side session.json
            // writer (SetActiveVersionAsync, the diarisation Diarised flip, ...) - see
            // _runUnderGate's doc. Non-cancellable by design (CancellationToken.None): the version
            // folder is complete and about to become evidence; the commit itself must not be
            // abandoned partway.
            await _runUnderGate(id, async _ =>
            {
                var current = await sessionStore.ReadAsync(CancellationToken.None)
                              ?? throw new InvalidOperationException($"session.json vanished for {id}");
                await sessionStore.SaveAsync(current with
                {
                    ActiveVersion = versionId,
                    Versions = current.Versions.Append(entry).ToList(),
                }, CancellationToken.None);
                committed = true;
            });

            // Rendered copies inside the version folder (design section 3.1): the default loader
            // path now resolves ActiveVersion = this version, so the plain regen writes
            // transcript.md/.txt into versionDir + refreshes root session.txt. Post-commit and
            // non-cancellable: the version is already evidence; a crash here only costs derived
            // .md/.txt, regenerated on the next edit/regenerate-all.
            await new SessionWriter(_paths, settings, _time)
                .RegenerateProjectionsAsync(id, CancellationToken.None);
            return versionId;
        }
        catch when (!committed)
        {
            // Cancel or fault BEFORE the commit: the folder is a partial derived output, not yet
            // evidence (design section 1) - discard it. Root files were never touched. A
            // completed (committed) version deliberately has NO delete path here or anywhere.
            try { Directory.Delete(versionDir, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>Retained legs actually on disk, Local first (the live pipeline's feed order):
    /// preferred-format probe matching PlaybackViewModel.Resolve/SplitSpeakersViewModel.ProbeLeg
    /// - FLAC first, WAV fallback, so pre-format-change sessions still resolve.</summary>
    private List<(string Path, SourceKind Kind)> ResolveLegs(string id, IReadOnlyList<SourceKind> retained)
    {
        var legs = new List<(string, SourceKind)>();
        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
        {
            if (!retained.Contains(kind)) continue;
            string flac = _paths.AudioFile(id, kind, AudioFormat.Flac);
            string wav = _paths.AudioFile(id, kind, AudioFormat.Wav);
            if (File.Exists(flac)) legs.Add((flac, kind));
            else if (File.Exists(wav)) legs.Add((wav, kind));
        }
        return legs;
    }

    /// <summary>max+1 over BOTH the recorded Versions and any folders already under versions\ -
    /// an orphaned partial folder (crash before its cancel-cleanup) is skipped past, never
    /// reused and never deleted (it is unreferenced junk, not evidence; left for the user).</summary>
    private int NextVersionNumber(SessionRecord session)
    {
        int max = 1;
        foreach (var v in session.Versions) max = Math.Max(max, TranscriptVersions.Number(v.Id));
        string versionsDir = _paths.VersionsDir(session.Id);
        if (Directory.Exists(versionsDir))
            foreach (string dir in Directory.EnumerateDirectories(versionsDir))
                max = Math.Max(max, TranscriptVersions.Number(Path.GetFileName(dir)));
        return max + 1;
    }

    /// <summary>Pre-decoded 16 kHz mono PCM -> 512-sample AudioFrames with sample-counted
    /// StartMs - the exact emission contract of WavFileFrameReader (trailing partial window
    /// dropped), so the same VAD/worker/merger pipeline runs unchanged over a FLAC leg.</summary>
    private static IEnumerable<AudioFrame> Frames(float[] samples, SourceKind kind)
    {
        long emitted = 0;
        for (int i = 0; i + FrameSamples <= samples.Length; i += FrameSamples)
        {
            yield return new AudioFrame(kind, emitted * 1000 / 16000, samples[i..(i + FrameSamples)]);
            emitted += FrameSamples;
        }
    }

    private static async IAsyncEnumerable<AudioFrame> ToAsync(IEnumerable<AudioFrame> frames)
    {
        foreach (var f in frames) yield return f;
        await Task.CompletedTask;
    }
}
