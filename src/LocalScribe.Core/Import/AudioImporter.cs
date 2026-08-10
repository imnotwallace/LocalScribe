using System.Globalization;
using System.Security.Cryptography;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.Core.Import;

/// <summary>Import-time speaker detection (design 2026-07-28). <c>Off</c> runs no diarisation pass
/// at all and is the record default, so every pre-existing caller behaves exactly as before.
/// <c>Auto</c> maps to DiarisationRequest.ForcedClusterCount = null (sherpa threshold clustering);
/// <c>Declared</c> maps to ForcedClusterCount = SpeakerCount.</summary>
public enum SpeakerDetection { Off, Auto, Declared }

/// <summary>One import job (design 2026-07-13 section 4.4). RecordedAtLocal is when the call
/// HAPPENED (user-editable; defaults from the container media-creation tag, then file timestamps)
/// - it drives the session id and StartedAtUtc so list ordering is by recording time.</summary>
public sealed record ImportRequest
{
    public required string SourcePath { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset RecordedAtLocal { get; init; }
    public IReadOnlyList<string> MatterIds { get; init; } = [];
    public StereoMapping Stereo { get; init; } = StereoMapping.Downmix;
    /// <summary>Per-import model override (canonical name from the dialog picker); null = use the
    /// global Settings.Model. Design 2026-07-24.</summary>
    public string? Model { get; init; }
    /// <summary>Per-import language override ("auto" = auto-detect); null = global Settings.Language.</summary>
    public string? Language { get; init; }

    /// <summary>Import-time speaker detection mode (design 2026-07-28). Off = no diarisation pass.
    /// Set through <see cref="WithSpeakerDetection"/>, which is the only way to reach a Declared
    /// request - see that method for why this is not a public init property.</summary>
    public SpeakerDetection SpeakerDetection { get; private init; } = SpeakerDetection.Off;

    /// <summary>The declared voice count; non-null only when SpeakerDetection is Declared. Set
    /// through <see cref="WithSpeakerDetection"/> - see that method for why this is not a public
    /// init property.</summary>
    public int? SpeakerCount { get; private init; }

    /// <summary>The only way to set speaker detection on a request. Both properties are
    /// private-init because validating them independently is impossible: C# runs each named
    /// member's init accessor sequentially in written order, so any per-property eager check sees
    /// a stale sibling and either rejects a valid pair or admits an invalid one depending on the
    /// order the caller happened to write. Validating the pair once, here, is order-independent by
    /// construction and leaves no shape - including "Declared with the count never mentioned" -
    /// that can reach the pipeline unchecked.
    ///
    /// This is load-bearing, not defensive: the in-house clusterer (SpeakerClustering.Cluster,
    /// 2026-08-02) treats null as auto and clamps any forced value into 1..segment-count, so an
    /// unvalidated 0 (clamped into 1..the reliable-segment count) would silently become a forced
    /// SINGLE-cluster run while the request claims it forced a specific speaker count.</summary>
    public ImportRequest WithSpeakerDetection(SpeakerDetection mode, int? count = null)
    {
        if (mode == SpeakerDetection.Declared)
        {
            if (count is not int n || n < 2)
                throw new ArgumentException(
                    "SpeakerCount must be 2 or more when SpeakerDetection is Declared.", nameof(count));
        }
        else if (count is not null)
        {
            throw new ArgumentException(
                $"SpeakerCount must be null when SpeakerDetection is {mode}.", nameof(count));
        }
        return this with { SpeakerDetection = mode, SpeakerCount = count };
    }
}

/// <summary>The staged-progress vocabulary (design 2026-07-13 section 4.4): reported once at the
/// START of each stage. DetectSpeakers (design 2026-07-28) is reported by the App-layer runner
/// AFTER ImportAsync returns, so the observed order is Copy -> Decode -> Transcribe -> Save ->
/// DetectSpeakers.</summary>
public enum ImportStage { Copy, Decode, Transcribe, Save, DetectSpeakers }

/// <summary>Payload for the &gt;1 percent decoded-vs-claimed Continue/Cancel gate.</summary>
public sealed record DurationMismatchInfo(long ClaimedDurationMs, long DecodedDurationMs);

/// <summary>Orchestrates design 2026-07-13 section 4: copy-original+hash into source\ -> decode
/// (decoded-stream truth) -> duration-mismatch gate -> channel mapping -> transcription via the
/// existing OfflinePipelineRunner INTO the pre-created folder (which also writes the FLAC legs
/// from the mapped mono WAVs, exactly like a recorded session) -> finalize session.json
/// (Origin/ImportedSource, decoded duration) -> re-render projections. A failure BEFORE the audio
/// legs exist (nothing written yet, including a declined duration-mismatch gate) deletes the
/// partial session folder - an unfinished import is a derived output, not evidence; the original
/// file is never touched. Owner decision 2026-08-11: once the legs exist, a fatal failure instead
/// SALVAGES the session - the archived source, decoded legs and every transcribed segment survive,
/// the transcript gets a TranscriptionFailed marker at the failure point IF transcription is what
/// failed (a fault after the runner returned is a finalization failure and makes no such claim -
/// 2026-08-11 final review C2), and the session is finalized/resealed as COMPLETE rather than left
/// for recovery to adopt (see SalvageAsync). This
/// mirrors the live worker's "audio is never dropped" ruling (2026-07-02); the import path was the
/// outlier. KNOWN behavior: a hard crash mid-import (process killed, no exception to catch) still
/// leaves an un-ended folder that the startup recovery scan finalizes as a recovered (possibly
/// empty) session - the same semantics as a crashed live recording; the user deletes it like any
/// other row.</summary>
public sealed class AudioImporter
{
    private readonly StoragePaths _paths;
    private readonly Settings _settings;
    private readonly IAudioDecoder _decoder;
    private readonly IEngineFactory _engineFactory;
    private readonly Func<ISpeechProbabilityModel> _vadModelFactory;
    private readonly IHardwareProbe _hardware;
    private readonly Func<IClock> _clockFactory;
    private readonly TimeProvider _machineTime;
    private readonly string _appVersion;
    private readonly Func<IReadOnlySet<string>> _availableModels;
    /// <summary>Free-space query seam (2026-08-11 coordinator review round 1): real callers get
    /// DriveInfo; tests inject a fake so a constrained-disk scenario is hermetic (no real disk
    /// needs to actually run low). Null means "cannot be determined" - the space check for that
    /// volume is SKIPPED, never fatal.</summary>
    private readonly Func<string, long?> _volumeFreeBytes;
    /// <summary>Task 7 (2026-08-11): optional, same nullable-tail shape as SessionWriter/
    /// MaintenanceService - forwarded to the OfflinePipelineRunner this class constructs
    /// internally, so the import worker's ErrorRaised codes (VRAM_OOM/RTF_LAGGING/
    /// MODEL_DOWNGRADED/MODEL_DOWNGRADE_FLOOR/MODEL_DOWNLOAD_FAILED/BACKEND_INIT_FAILED) finally
    /// reach the diagnostic log instead of vanishing - this is the path a mid-import downgrade
    /// used to leave unexplained. Null (every pre-existing caller, incl. Core's own tests) keeps
    /// today's silent behaviour.</summary>
    private readonly IDiagnosticLog? _log;

    public AudioImporter(StoragePaths paths, Settings settings, IAudioDecoder decoder,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, Func<IClock> clockFactory, TimeProvider machineTime, string appVersion,
        Func<IReadOnlySet<string>>? availableModels = null, Func<string, long?>? volumeFreeBytes = null,
        IDiagnosticLog? log = null)
        => (_paths, _settings, _decoder, _engineFactory, _vadModelFactory, _hardware, _clockFactory,
                _machineTime, _appVersion, _availableModels, _volumeFreeBytes, _log)
         = (paths, settings, decoder, engineFactory, vadModelFactory, hardware, clockFactory,
                machineTime, appVersion, availableModels ?? ModelPaths.AvailableModels,
                volumeFreeBytes ?? DefaultVolumeFreeBytes, log);

    public async Task<string> ImportAsync(ImportRequest request, IProgress<ImportStage>? progress,
        Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct,
        IProgress<TranscriptionProgress>? transcriptProgress = null)
    {
        var runSettings = _settings with
        {
            Model = request.Model ?? _settings.Model,
            Language = request.Language ?? _settings.Language,
        };

        // Fail-fast presence gate (design 2026-07-24 section 4): refuse an uninstalled model before
        // any copy/decode/folder work. Mirrors RetranscriptionRunner's gate; resolves through the SAME
        // override BackendSelector applies (a non-English + ".en" model strips to multilingual weights).
        {
            var available = _availableModels();
            var (gatePlan, _) = BackendSelector.Select(_hardware.Probe(), runSettings, available);
            if (!available.Contains(gatePlan.ModelName))
            {
                string picked = runSettings.Model;
                string hint = picked.EndsWith(".en", StringComparison.Ordinal) && gatePlan.ModelName == picked[..^3]
                    ? $" '{picked}' is English-only; for {runSettings.Language} choose a multilingual model such as large-v3-turbo."
                    : " Run tools/fetch-models.ps1 or pick another model.";
                throw new InvalidOperationException(
                    $"The transcription model '{gatePlan.ModelName}' is not downloaded.{hint}");
            }
        }

        // Pre-flight before any work: a storage root or TEMP volume that cannot be created or
        // written produced an UnauthorizedAccessException from deep inside ImportAsync (owner
        // log, 2026-08-07), after the user had already waited through the file picker.
        // Writability ONLY here - it needs no probe data, is cheap, and belongs before any
        // session folder exists. The SPACE check (below, after Probe - 2026-08-11 coordinator
        // review round 1) needs real magnitudes (claimed duration/channels/format) that are not
        // known yet at this point, and checking it here would mean checking the wrong volume too:
        // the decoded WAV and leg WAVs land on TEMP, not the storage root.
        EnsureWritable(_paths.SessionsDir, "storage", "Check the storage location in Settings, "
            + "and that the drive is connected and you have permission to write to it.");
        EnsureWritable(Path.GetTempPath(), "temp",
            "Check your system TEMP folder's permissions and that its drive is connected.");

        string workDir = Path.Combine(Path.GetTempPath(), "localscribe-import",
            Guid.NewGuid().ToString("N"));
        string? sessionId = null;
        bool legsWritten = false;      // past here a failure is salvageable, not disposable
        // 2026-08-11 final whole-branch review (C1/C2): SalvageAsync was written assuming the
        // runner faulted BEFORE it finished, and later tasks added throwing call sites AFTER it
        // returns (the Save stage below, RegenerateProjectionsAsync, and the runner's own
        // retained-audio/finalize steps). Salvage cannot infer that from side effects, so it is
        // TRACKED here: set on the single line after `await runner.RunAsync(...)` returns
        // normally, and nowhere else. It answers two different questions salvage must not guess
        // at - "did transcription actually fail?" (the marker) and "is session.json's Language
        // already the runner's resolved value?" - and it is deliberately NOT used to decide
        // whether a leg file on disk is real: an occupied destination is real audio on EVERY
        // path (see the leg loop in SalvageAsync).
        bool transcriptionCompleted = false;
        // Hoisted decoded-stream truth + channel-mapping state (2026-08-11 review I4) so a
        // faulted catch can stamp the SAME ImportedSource provenance the success path does,
        // rather than leaving DecodedDurationMs/DecodedSampleRate/DecodedChannels at their
        // non-nullable zero default and ChannelMapping at "" - which would serialize as
        // positive claims of zero on a record that simultaneously claims a decoded duration
        // elsewhere. `legs`/`plan` are also how the catch salvages the channel-mapped legs
        // (I1): workDir (where those WAVs live) still exists while the catch runs - the
        // `finally` that deletes workDir only fires AFTER the catch completes.
        //
        // 2026-08-11 final review M-a: decodedDurationMs is a plain long, not long?. It is read
        // ONLY by the salvage call below, which is gated on legsWritten - and legsWritten is set
        // after the decode block has already assigned it, so "decoded but unknown" is not a
        // reachable state. It used to be long? with two DIFFERENT dead fallbacks downstream
        // (`?? lastMs` and `?? 0`), i.e. dead code encoding two answers to an impossible question.
        long decodedDurationMs = 0;
        int decodedSampleRate = 0;
        int decodedChannels = 0;
        bool durationMismatch = false;
        ChannelMapPlan? plan = null;
        IReadOnlyList<(SourceKind Kind, string WavPath)>? legs = null;
        try
        {
            Directory.CreateDirectory(workDir);   // INSIDE the try: the finally must be able to clean it up

            // ---- Copy: bootstrap at the PINNED recorded date, then archive the original ----
            progress?.Report(ImportStage.Copy);
            var pinnedTime = new PinnedTimeProvider(request.RecordedAtLocal.ToUniversalTime(),
                _machineTime.LocalTimeZone);
            var original = new FileInfo(request.SourcePath);
            if (!original.Exists) throw new FileNotFoundException("Audio file not found.", request.SourcePath);
            var probe = await _decoder.ProbeAsync(request.SourcePath, ct);

            // ---- Space check: NOW real magnitudes are known (claimed duration/channels/format)
            // and no session folder exists yet, so a refusal here still leaves the filesystem
            // clean apart from the already-writable, harmless empty workDir (2026-08-11
            // coordinator review round 1: checked per VOLUME the writes actually land on, not one
            // blended guess against only the storage root - see EstimateSpaceNeeds/EnsureSpace).
            var (storageNeed, tempNeed) = EstimateSpaceNeeds(original.Length, probe, request.Stereo);
            EnsureSpace(_paths.SessionsDir, workDir, storageNeed, tempNeed);

            var boot = await SessionBootstrap.StartAsync(_paths, _settings, AppKind.Manual,
                [SourceKind.Local], new DeviceSnapshot(), pinnedTime, _appVersion, ct,
                request.MatterIds, request.Title);
            sessionId = boot.Id;

            Directory.CreateDirectory(_paths.SourceDir(sessionId));
            string copyPath = _paths.SourceFile(sessionId, original.Name);
            string sha256 = await CopyWithSha256Async(request.SourcePath, copyPath, ct);
            // Mirror the original's timestamps onto the archived copy (chain of custody); they are
            // ALSO recorded in session.json below, which is the evidentiary record.
            File.SetCreationTimeUtc(copyPath, original.CreationTimeUtc);
            File.SetLastWriteTimeUtc(copyPath, original.LastWriteTimeUtc);

            var imported = new ImportedSourceInfo
            {
                FileName = original.Name, Sha256 = sha256, FileSizeBytes = original.Length,
                ContainerFormat = probe.FormatName,
                FileCreatedUtc = original.CreationTimeUtc, FileModifiedUtc = original.LastWriteTimeUtc,
                MediaCreatedUtc = probe.MediaCreatedUtc, ClaimedDurationMs = probe.ClaimedDurationMs,
                // Known from the probe alone, before Decode even runs - stamped here (not in the
                // Decoded* block below) so it survives on BOTH the success and salvage paths via
                // the `imported with {...}` that follows, without extra plumbing (2026-08-11).
                AudioStreamIndex = probe.AudioStreamIndex,
            };
            var sessionStore = new SessionStore(_paths.SessionJson(sessionId));
            await sessionStore.SaveAsync(
                boot.LiveRecord with { Origin = "imported", ImportedSource = imported }, ct);

            // ---- Decode: decode the ARCHIVED copy (proves the archived bytes decode) ----
            progress?.Report(ImportStage.Decode);
            var decoded = await _decoder.DecodeAsync(copyPath, probe, workDir, ct);
            decodedDurationMs = decoded.DurationMs;
            decodedSampleRate = decoded.SampleRate;
            decodedChannels = decoded.Channels;

            if (probe.ClaimedDurationMs is long claimed && claimed > 0
                && Math.Abs(decoded.DurationMs - claimed) * 100 > claimed)   // > 1 percent
            {
                // Design 4.1: pause AFTER Decode with a Continue/Cancel gate; continuing records a
                // transcript marker; declining is a cancel (the partial folder is deleted below).
                if (!await confirmDurationMismatch(new DurationMismatchInfo(claimed, decoded.DurationMs)))
                    throw new OperationCanceledException("import declined at the duration-mismatch gate");
                durationMismatch = true;
            }

            plan = ChannelMapper.Plan(decoded.Channels, request.Stereo);
            legs = await Task.Run(
                () => ChannelMapper.WriteLegs(decoded.PcmWavPath, plan, workDir, ct), ct);
            legsWritten = true;      // past here a failure is salvageable, not disposable

            // Markers BEFORE transcription: TranscriptMerger.InitializeAsync continues the seq
            // after existing lines, and the Save-stage recount below fixes MarkerCount.
            var transcript = new TranscriptStore(_paths.TranscriptJsonl(sessionId));
            if (durationMismatch)
                await transcript.AppendAsync(TranscriptLine.Marker(
                    await transcript.NextSeqAsync(ct), 0,
                    string.Format(CultureInfo.InvariantCulture, Markers.ImportedDurationMismatch,
                        FormatDuration(probe.ClaimedDurationMs!.Value), FormatDuration(decoded.DurationMs))), ct);
            if (plan.Downmixed)
                await transcript.AppendAsync(TranscriptLine.Marker(
                    await transcript.NextSeqAsync(ct), 0,
                    string.Format(CultureInfo.InvariantCulture, Markers.ImportedDownmixed,
                        decoded.Channels)), ct);

            // ---- Transcribe (the runner also writes the retained FLAC legs from the mono WAVs) ----
            progress?.Report(ImportStage.Transcribe);
            var runner = new OfflinePipelineRunner(_paths, runSettings, _engineFactory,
                _vadModelFactory, _hardware, _clockFactory(), pinnedTime, _appVersion, _log);
            await runner.RunAsync(new OfflineRunOptions
            {
                ExistingSessionId = sessionId,
                LocalWavPath = legs.FirstOrDefault(l => l.Kind == SourceKind.Local).WavPath,
                RemoteWavPath = legs.FirstOrDefault(l => l.Kind == SourceKind.Remote).WavPath,
                TotalDurationMs = decoded.DurationMs,
            }, ct, transcriptProgress);
            transcriptionCompleted = true;   // every segment is in; only finalization can fail now

            // ---- Save: decoded-truth duration + full recount + provenance completion ----
            // The `record with {...}` below preserves every runner-finalized field it does not
            // name - including WeightsFile (7d6c88d), the exact ggml file that transcribed this
            // import: the same evidentiary provenance a live session records.
            progress?.Report(ImportStage.Save);
            var lines = await transcript.ReadAllAsync(ct);
            var record = await sessionStore.ReadAsync(ct)
                ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
            await sessionStore.SaveAsync(record with
            {
                Sources = legs.Select(l => l.Kind).ToArray(),
                DurationMs = decoded.DurationMs,                     // decoded truth, not last-speech
                EndedAtUtc = record.StartedAtUtc.AddMilliseconds(decoded.DurationMs),
                SegmentCount = lines.Count(l => l.Kind == TranscriptKind.Segment),
                MarkerCount = lines.Count(l => l.Kind == TranscriptKind.Marker),
                ImportedSource = imported with
                {
                    DecodedDurationMs = decoded.DurationMs,
                    DecodedSampleRate = decoded.SampleRate,
                    DecodedChannels = decoded.Channels,
                    ChannelMapping = MappingLabel(decoded.Channels, plan),
                    DurationMismatch = durationMismatch,
                },
            }, ct);
            // Task 9 (2026-08-11): seal the retained leg into manifest.json. OfflinePipelineRunner's
            // own finalize (inside runner.RunAsync above) already sealed it once for this session,
            // so this second seal is a carry-forward (size+mtime match) rather than a re-hash -
            // measured below. Kept here anyway so this call site is correct on its own, independent
            // of what its callee happens to do.
            await new SessionWriter(_paths, _settings, _machineTime)
                .RegenerateProjectionsAsync(sessionId, ct, sealAudio: true);
            return sessionId;
        }
        catch (Exception ex)
        {
            // Owner decision 2026-08-11: an import must never destroy work. Once the audio legs
            // exist, everything transcribed so far plus the archived source is worth more than a
            // clean slate - the same "audio is never dropped" ruling (2026-07-02) the live worker
            // already honours. Only a failure BEFORE any audio is written leaves nothing to keep.
            if (sessionId is not null)
            {
                if (legsWritten && ex is not OperationCanceledException)
                {
                    try
                    {
                        await SalvageAsync(sessionId, decodedDurationMs, decodedSampleRate,
                            decodedChannels, durationMismatch, transcriptionCompleted,
                            runSettings.Language, plan!, legs!, ex);
                    }
                    catch { /* salvage is best-effort; never mask the original fault */ }
                }
                else
                {
                    try { Directory.Delete(_paths.SessionDir(sessionId), recursive: true); } catch { }
                }
            }
            throw;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    /// <summary>Turn a faulted import into a COMPLETE, valid session rather than a folder the
    /// recovery scanner will later adopt: mark the transcript at the failure point when
    /// transcription is what failed, persist whatever channel-mapped legs are not ALREADY on disk
    /// (workDir is still alive - the caller's `finally` deletes it only after this returns) so
    /// Resume/re-transcribe has FLAC to read
    /// (RetranscriptionRunner gates on File.Exists over _paths.AudioFile - the entire basis for
    /// salvaging rather than deleting), stamp the SAME decoded-provenance/Sources the success path
    /// records, finalize with EndedAtUtc set, recount, and regenerate projections (which reseals
    /// manifest.json last).
    ///
    /// Deliberately ignores the import's own CancellationToken (2026-08-11 review I2): reaching
    /// salvage only proves the ORIGINAL fault was not a cancellation - it does NOT prove the
    /// token is still live. A disk-full IOException racing a user Cancel (or app shutdown) must
    /// not abort THIS, the finalization of an evidentiary record, at its first await and leave
    /// the same half-finalized (EndedAtUtc == null) folder RecoveryScanner.FindUnendedAsync would
    /// wrongly adopt. Every await below uses CancellationToken.None.
    ///
    /// EVERY disk operation before the finalization pair (the marker append included - it is the
    /// FIRST disk write salvage attempts, and disk exhaustion is exactly as likely to break it as
    /// the leg writes) is independently best-effort (2026-08-11 review I1 round 2): each is its
    /// own try/catch with no early return, so control reaches the finalization pair - the
    /// session.json save that carries EndedAtUtc, then RegenerateProjectionsAsync - BY
    /// CONSTRUCTION, regardless of how many earlier steps failed, rather than by luck of
    /// ordering.
    ///
    /// 2026-08-11 final whole-branch review: this method used to assume the runner faulted BEFORE
    /// it finished. It does not get to assume that - <paramref name="transcriptionCompleted"/> is
    /// the caller's record of whether runner.RunAsync actually returned, and it governs the two
    /// claims that would otherwise be false on a finalization-stage fault (the "transcription
    /// failed" marker, and whose Language value session.json should carry).</summary>
    /// <param name="transcriptionCompleted">True when runner.RunAsync RETURNED and the fault came
    /// from a later step (the Save stage, RegenerateProjectionsAsync). Never inferred here.</param>
    /// <param name="language">The per-import effective language (runSettings.Language). Bootstrap
    /// stamped app-level Settings.Language on session.json and only the runner's own finalize ever
    /// corrected it, so a salvaged import used to record a language it was never asked for.</param>
    private async Task SalvageAsync(string sessionId, long decodedDurationMs, int decodedSampleRate,
        int decodedChannels, bool durationMismatch, bool transcriptionCompleted, string language,
        ChannelMapPlan plan, IReadOnlyList<(SourceKind Kind, string WavPath)> legs, Exception cause)
    {
        var transcript = new TranscriptStore(_paths.TranscriptJsonl(sessionId));

        long lastMs = 0;
        try
        {
            var lines = await transcript.ReadAllAsync(CancellationToken.None);
            lastMs = lines.Where(l => l.Kind == TranscriptKind.Segment)
                          .Select(l => l.EndMs).DefaultIfEmpty(0).Max();
        }
        catch { /* fall back to lastMs = 0 - still enough to finalize below */ }

        // 2026-08-11 final review C2: the marker asserts "transcription failed". When the runner
        // RETURNED, every segment was transcribed and the fault came from finalization (the Save
        // stage, RegenerateProjectionsAsync) - writing it then permanently states that a complete,
        // correct transcript failed at its last segment, in an APPEND-ONLY document with no delete
        // path that gets rendered into a .docx served on opposing parties. No marker is written on
        // that path: silence is not a false claim, and inventing new marker wording for a
        // finalization fault is a spec change, not a bug fix. The diagnostic log is where the
        // finalization failure belongs.
        if (!transcriptionCompleted)
        {
            try
            {
                // The marker carries only the exception TYPE name (2026-08-11 review M4 round 2),
                // not its free-form Message: a native/OS exception message can embed a full local
                // path (sometimes with a username in it) with no reliable way to redact it - a
                // path can contain spaces, so a whitespace-bounded pattern leaves fragments
                // un-redacted, and this document has no delete path and gets exported to opposing
                // parties. The full message belongs in the diagnostic log (which already has
                // redaction rules), not here. GetType().Name is structurally incapable of carrying
                // a path and is always ASCII.
                await transcript.AppendAsync(TranscriptLine.Marker(
                    await transcript.NextSeqAsync(CancellationToken.None), lastMs,
                    $"{Markers.TranscriptionFailed}: {cause.GetType().Name}"), CancellationToken.None);
            }
            catch { /* best-effort; finalization below still must run */ }
        }

        // Retained audio (same shape as OfflinePipelineRunner's own step 3, spec section 7):
        // "never" must stay empty-handed even mid-salvage - a user who opted out of audio
        // retention must not suddenly get audio kept because a fault happened. Each leg is
        // isolated: the likeliest real cause of the ORIGINAL fault is disk exhaustion, which is
        // exactly what would make THIS write fail too - one leg failing (Create/Write/Read/the
        // per-iteration Dispose can all throw) must not abort the finalization below.
        var retained = new List<SourceKind>();
        if (_settings.AudioRetention != "never")
        {
            foreach (var (kind, wavPath) in legs)
            {
                string dest = _paths.AudioFile(sessionId, kind, _settings.AudioFormat);
                // 2026-08-11 final review C1 (CRITICAL): salvage must never open a writer over a
                // leg file it did not itself create. Three reachable faults leave a COMPLETE leg
                // already on disk - the runner's own retained-audio loop failing on the SECOND
                // leg, its finalize/RegenerateProjectionsAsync throwing, and the importer's Save
                // stage throwing - and in the last two the bytes were already hashed into
                // manifest.json. MEASURED against the pinned CUETools.Codecs.FLAKE 1.0.5: the
                // writer neither throws on an occupied path nor truncates at construction, it
                // ZEROES the file on the first Write, and the guarded catch below cannot undo
                // that. On a full disk (the likeliest cause of the original fault, and exactly
                // what makes this rewrite fail too) a complete 25 MB evidentiary leg became an
                // 87-byte FLAC header that File.Exists still answers true for - which
                // ManifestBuilder then re-hashed and sealed as the audio of record, so Verify
                // integrity passed on destroyed audio.
                //
                // An occupied destination is therefore treated as already-persisted audio: keep
                // it, count it in RetainedAudioSources (RetranscriptionRunner gates re-transcribe
                // on that list, so omitting it would kill the recovery route salvage exists to
                // preserve), and do not rewrite, delete or re-hash it. Deliberately NOT gated on
                // transcriptionCompleted: whatever put a file there, it is not ours to destroy.
                // Deliberately NOT validated by reading it either - a torn FLAC HANGS FlakeReader
                // (measured in this repo 2026-08-06), so a "verify before keeping" probe would
                // hang the salvage of the very session it was meant to protect.
                if (File.Exists(dest)) { retained.Add(kind); continue; }
                try
                {
                    using (var sink = AudioSinkFactory.Create(dest, _settings.AudioFormat))
                    {
                        foreach (var frame in WavFileFrameReader.ReadFrames(wavPath, kind))
                            sink.Write(frame.Samples);
                    }
                    retained.Add(kind);
                }
                catch { /* this leg did not persist; RetainedAudioSources below omits it */ }
            }
        }

        int segmentCount = 0, markerCount = 0;
        try
        {
            var lines = await transcript.ReadAllAsync(CancellationToken.None);
            segmentCount = lines.Count(l => l.Kind == TranscriptKind.Segment);
            markerCount = lines.Count(l => l.Kind == TranscriptKind.Marker);
        }
        catch { /* recount best-effort; finalize with whatever is known */ }

        // ---- Finalization: MUST run regardless of anything above (I1) ----
        var sessionStore = new SessionStore(_paths.SessionJson(sessionId));
        if (await sessionStore.ReadAsync(CancellationToken.None) is { } record)
        {
            long durationMs = decodedDurationMs;
            await sessionStore.SaveAsync(record with
            {
                // Sources (not just RetainedAudioSources) reflects the channel mapping, exactly
                // as the success path sets it - a split-stereo import that faults mid-transcription
                // must not leave Sources = [Local] while RetainedAudioSources says [Local, Remote].
                Sources = legs.Select(l => l.Kind).ToArray(),
                DurationMs = durationMs,
                EndedAtUtc = record.StartedAtUtc.AddMilliseconds(durationMs),
                SegmentCount = segmentCount,
                MarkerCount = markerCount,
                RetainedAudioSources = retained,
                // 2026-08-11 final review I1: bootstrap stamped APP-LEVEL Settings.Language on
                // session.json; the per-import override lives only in runSettings, and the ONLY
                // code that ever corrected the record was the runner's own finalize. A salvaged
                // import explicitly requested as "es" therefore recorded Language = "en" - not an
                // absent claim like Model/Backend (which stay empty and are omitted by the
                // renderers) but a positive WRONG one. When the runner DID finalize, its value is
                // strictly better (resolver.Locked - what the engine actually detected/locked), so
                // that one is kept rather than overwritten with the request.
                Language = transcriptionCompleted ? record.Language : language,
                // Decoded-stream provenance, stamped exactly as the success path's Save stage
                // does - leaving these at their non-nullable zero/"" default would serialize as
                // positive claims of no channels/no sample rate on a record that simultaneously
                // claims a decoded duration a few fields up.
                ImportedSource = record.ImportedSource is { } imported
                    ? imported with
                    {
                        DecodedDurationMs = decodedDurationMs,
                        DecodedSampleRate = decodedSampleRate,
                        DecodedChannels = decodedChannels,
                        ChannelMapping = MappingLabel(decodedChannels, plan),
                        DurationMismatch = durationMismatch,
                    }
                    : record.ImportedSource,
            }, CancellationToken.None);
        }
        // Task 9 (2026-08-11): a salvaged session is exactly the one a user will scrutinise (it
        // faulted mid-import), so its retained leg must be sealed too - this is the ONLY finalize
        // call on the salvage path (RunAsync faulted before reaching its own), so this is a real
        // first-time hash, not a carry-forward.
        await new SessionWriter(_paths, _settings, _machineTime)
            .RegenerateProjectionsAsync(sessionId, CancellationToken.None, sealAudio: true);
    }

    private static async Task<string> CopyWithSha256Async(string sourcePath, string destPath,
        CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var dst = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buf = new byte[1 << 16];
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, n);
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Fail fast and legibly when a destination cannot be written at all - cheap, needs
    /// no probe data, and belongs before any work (2026-08-11 coordinator review round 1: split
    /// out of the combined writability+space check, and now called for BOTH the storage root and
    /// the TEMP volume, since workDir and its leg WAVs land on TEMP, not storage). The probe
    /// write and its cleanup are two SEPARATE guarded steps: a delete failure (a Defender scan
    /// locking a just-created file is real) must never be misreported as "cannot be written to"
    /// when the write itself succeeded, and cleanup is best-effort but always attempted.</summary>
    private static void EnsureWritable(string dir, string what, string hint)
    {
        string probe = "";
        try
        {
            Directory.CreateDirectory(dir);
            probe = Path.Combine(dir, $".ls-write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The {what} folder '{dir}' cannot be written to. {hint}", ex);
        }
        finally
        {
            if (probe.Length > 0) { try { File.Delete(probe); } catch { } }
        }
    }

    /// <summary>Per-volume space estimate from what Probe actually knows (2026-08-11 coordinator
    /// review round 1). The prior sourceLength*2 blended guess both OVER-refused WAV sources -
    /// FfmpegAudioDecoder.DecodeAsync short-circuits a .wav input and writes no new decoded bytes
    /// at all, PcmWavPath IS the archived copy - and UNDER-refused small, highly compressed
    /// sources that ffmpeg decodes to pcm_s16le at the stream's NATIVE rate, often many times the
    /// compressed size. Storage gets the archived copy (exact, certain whenever the source's
    /// length is known) plus what the retained leg(s) actually cost once OfflinePipelineRunner
    /// writes them into the SAME session folder as FLAC (the default AudioFormat - design
    /// 2026-07-13 section 7); TEMP gets the decoded WAV (zero for a WAV pass-through source) plus
    /// the SAME 16 kHz mono leg(s) ChannelMapper writes, as uncompressed PCM.
    ///
    /// 2026-08-11 coordinator review round 2: the storage-side leg allowance used to be a flat
    /// +20% of sourceLengthBytes - a percentage of the (often already-compressed) ARCHIVED COPY,
    /// which has no relationship to what a duration-based FLAC leg actually costs. A 30-minute
    /// call at typical speech bitrate compresses to roughly 15-30 MB, while its 16 kHz mono FLAC
    /// leg is roughly 25-35 MB (more with two legs) - several times the old margin, in the
    /// direction that let the import proceed and then run out of storage mid-FLAC-write. Fixed by
    /// deriving the storage leg allowance the SAME way as the temp leg term - duration x 16000 x 2
    /// bytes x leg count (legPcmBytes, computed once and reused for both volumes) - times a FLAC
    /// compression guess: 0.6 is an ESTIMATE, not measured; 16 kHz mono speech FLAC typically
    /// lands around 45-65% of its PCM size, so this is a representative middle value, not the
    /// best case. Missing or implausible Probe data (duration/channels &lt;= 0) leaves BOTH the
    /// storage leg allowance and the TEMP need at 0 - the same under-refuse guard as before,
    /// falling back to skipping that part of the estimate rather than to any percentage.</summary>
    private static (long StorageNeedBytes, long TempNeedBytes) EstimateSpaceNeeds(
        long sourceLengthBytes, AudioProbeResult probe, StereoMapping stereo)
    {
        long storageNeed = sourceLengthBytes <= 0 ? 0 : sourceLengthBytes;   // the archived copy: exact

        long tempNeed = 0;
        if (probe.ClaimedDurationMs is long ms && ms > 0
            && probe.ClaimedChannels is int channels && channels > 0)
        {
            double seconds = ms / 1000.0;
            bool isWavSource = string.Equals(probe.FormatName, "wav", StringComparison.OrdinalIgnoreCase);
            if (!isWavSource && probe.ClaimedSampleRate is int sampleRate && sampleRate > 0)
                tempNeed += (long)(seconds * sampleRate * channels * 2);   // ffmpeg -> pcm_s16le

            int legCount = channels == 2 && stereo != StereoMapping.Downmix ? 2 : 1;
            long legPcmBytes = (long)(seconds * 16000 * 2) * legCount;     // 16 kHz mono leg(s), PCM
            tempNeed += legPcmBytes;                                       // TEMP: the WAV leg(s), exact
            storageNeed += (long)(legPcmBytes * 0.6);                      // STORAGE: the SAME leg(s), as FLAC
        }
        return (storageNeed, tempNeed);
    }

    /// <summary>Checks the estimate against the volumes it will actually land on. Summed and
    /// checked ONCE when storage and TEMP resolve to the same drive (2026-08-11 coordinator
    /// review round 1), so a shared volume is never double-counted as having each need's full
    /// amount available independently.</summary>
    private void EnsureSpace(string storageDir, string tempDir, long storageNeed, long tempNeed)
    {
        string? storageRoot = SafeRoot(storageDir);
        string? tempRoot = SafeRoot(tempDir);
        if (storageRoot is not null && tempRoot is not null
            && string.Equals(storageRoot, tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            CheckVolume(storageDir, storageNeed + tempNeed, "the storage/TEMP drive");
            return;
        }
        CheckVolume(storageDir, storageNeed, "the storage drive");
        CheckVolume(tempDir, tempNeed, "the TEMP drive");
    }

    private void CheckVolume(string dir, long needBytes, string label)
    {
        if (needBytes <= 0) return;
        if (_volumeFreeBytes(dir) is not long free) return;   // can't be determined: skip, never fatal
        if (free < needBytes)
            throw new InvalidOperationException(
                $"Not enough free space on {label} to import this file: about "
                + $"{needBytes / (1024 * 1024)} MB is needed.");
    }

    private static string? SafeRoot(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)); }
        catch { return null; }
    }

    /// <summary>Real free-space query. Null means "cannot be determined" for ANY reason - an
    /// unmappable root (ArgumentException from the DriveInfo constructor, e.g. a UNC path) or a
    /// volume that answers IsReady but then throws reading AvailableFreeSpace (2026-08-11
    /// coordinator review round 1: a mapped network drive dropping mid-check is the real case
    /// this widens for) - and the caller SKIPS the space check rather than treating either as
    /// fatal.</summary>
    private static long? DefaultVolumeFreeBytes(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string MappingLabel(int decodedChannels, ChannelMapPlan plan) => decodedChannels switch
    {
        <= 1 => "mono",
        2 when plan.Legs.Count == 2 => plan.Legs[0].Channel == 0 ? "split" : "split-swapped",
        2 => "downmix",
        _ => "downmix-multichannel",
    };

    private static string FormatDuration(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>The recorded-date pin: GetUtcNow() is frozen at the user-chosen instant so
    /// SessionBootstrap derives the id/StartedAtUtc from when the call HAPPENED; LocalTimeZone is
    /// the real machine zone so session.json's UtcOffsetMinutes is DST-resolved for that historic
    /// date (legally meaningful) and TimeZoneId stays a real zone id.</summary>
    private sealed class PinnedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
