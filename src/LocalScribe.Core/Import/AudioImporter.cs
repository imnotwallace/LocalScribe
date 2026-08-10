using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LocalScribe.Core.Audio;
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
/// the transcript gets a TranscriptionFailed marker at the failure point, and the session is
/// finalized/resealed as COMPLETE rather than left for recovery to adopt (see SalvageAsync). This
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

    public AudioImporter(StoragePaths paths, Settings settings, IAudioDecoder decoder,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, Func<IClock> clockFactory, TimeProvider machineTime, string appVersion,
        Func<IReadOnlySet<string>>? availableModels = null)
        => (_paths, _settings, _decoder, _engineFactory, _vadModelFactory, _hardware, _clockFactory,
                _machineTime, _appVersion, _availableModels)
         = (paths, settings, decoder, engineFactory, vadModelFactory, hardware, clockFactory,
                machineTime, appVersion, availableModels ?? ModelPaths.AvailableModels);

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

        string workDir = Path.Combine(Path.GetTempPath(), "localscribe-import",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        string? sessionId = null;
        bool legsWritten = false;      // past here a failure is salvageable, not disposable
        // Hoisted decoded-stream truth + channel-mapping state (2026-08-11 review I4) so a
        // faulted catch can stamp the SAME ImportedSource provenance the success path does,
        // rather than leaving DecodedDurationMs/DecodedSampleRate/DecodedChannels at their
        // non-nullable zero default and ChannelMapping at "" - which would serialize as
        // positive claims of zero on a record that simultaneously claims a decoded duration
        // elsewhere. `legs`/`plan` are also how the catch salvages the channel-mapped legs
        // (I1): workDir (where those WAVs live) still exists while the catch runs - the
        // `finally` that deletes workDir only fires AFTER the catch completes.
        long? decodedDurationMs = null;
        int decodedSampleRate = 0;
        int decodedChannels = 0;
        bool durationMismatch = false;
        ChannelMapPlan? plan = null;
        IReadOnlyList<(SourceKind Kind, string WavPath)>? legs = null;
        try
        {
            // ---- Copy: bootstrap at the PINNED recorded date, then archive the original ----
            progress?.Report(ImportStage.Copy);
            var pinnedTime = new PinnedTimeProvider(request.RecordedAtLocal.ToUniversalTime(),
                _machineTime.LocalTimeZone);
            var original = new FileInfo(request.SourcePath);
            if (!original.Exists) throw new FileNotFoundException("Audio file not found.", request.SourcePath);
            var probe = await _decoder.ProbeAsync(request.SourcePath, ct);

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
            };
            var sessionStore = new SessionStore(_paths.SessionJson(sessionId));
            await sessionStore.SaveAsync(
                boot.LiveRecord with { Origin = "imported", ImportedSource = imported }, ct);

            // ---- Decode: decode the ARCHIVED copy (proves the archived bytes decode) ----
            progress?.Report(ImportStage.Decode);
            var decoded = await _decoder.DecodeAsync(copyPath, workDir, ct);
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
                _vadModelFactory, _hardware, _clockFactory(), pinnedTime, _appVersion);
            await runner.RunAsync(new OfflineRunOptions
            {
                ExistingSessionId = sessionId,
                LocalWavPath = legs.FirstOrDefault(l => l.Kind == SourceKind.Local).WavPath,
                RemoteWavPath = legs.FirstOrDefault(l => l.Kind == SourceKind.Remote).WavPath,
                TotalDurationMs = decoded.DurationMs,
            }, ct, transcriptProgress);

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
            await new SessionWriter(_paths, _settings, _machineTime)
                .RegenerateProjectionsAsync(sessionId, ct);
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
                            decodedChannels, durationMismatch, plan!, legs!, ex);
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
    /// recovery scanner will later adopt: mark the transcript at the failure point, persist
    /// whatever channel-mapped legs exist (workDir is still alive - the caller's `finally` deletes
    /// it only after this returns) so Resume/re-transcribe has FLAC to read
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
    /// wrongly adopt. Every await below uses CancellationToken.None.</summary>
    private async Task SalvageAsync(string sessionId, long? decodedDurationMs, int decodedSampleRate,
        int decodedChannels, bool durationMismatch, ChannelMapPlan plan,
        IReadOnlyList<(SourceKind Kind, string WavPath)> legs, Exception cause)
    {
        var transcript = new TranscriptStore(_paths.TranscriptJsonl(sessionId));
        var lines = await transcript.ReadAllAsync(CancellationToken.None);
        long lastMs = lines.Where(l => l.Kind == TranscriptKind.Segment)
                           .Select(l => l.EndMs).DefaultIfEmpty(0).Max();
        await transcript.AppendAsync(TranscriptLine.Marker(
            await transcript.NextSeqAsync(CancellationToken.None), lastMs,
            $"{Markers.TranscriptionFailed}: {SanitizeCauseMessage(cause)}"), CancellationToken.None);

        // Retained audio (same shape as OfflinePipelineRunner's own step 3, spec section 7):
        // "never" must stay empty-handed even mid-salvage - a user who opted out of audio
        // retention must not suddenly get audio kept because a fault happened. Each leg is
        // isolated: the likeliest real cause of the ORIGINAL fault is disk exhaustion, which is
        // exactly what would make THIS write fail too - one leg failing (Create/Write/Read/the
        // per-iteration Dispose can all throw) must not abort the finalization below, or the
        // folder is left with a TranscriptionFailed marker and EndedAtUtc == null: the exact
        // half-finalized shape RecoveryScanner.FindUnendedAsync wrongly adopts as "recovered".
        var retained = new List<SourceKind>();
        if (_settings.AudioRetention != "never")
        {
            foreach (var (kind, wavPath) in legs)
            {
                try
                {
                    using (var sink = AudioSinkFactory.Create(
                        _paths.AudioFile(sessionId, kind, _settings.AudioFormat), _settings.AudioFormat))
                    {
                        foreach (var frame in WavFileFrameReader.ReadFrames(wavPath, kind))
                            sink.Write(frame.Samples);
                    }
                    retained.Add(kind);
                }
                catch { /* this leg did not persist; RetainedAudioSources below omits it */ }
            }
        }

        var sessionStore = new SessionStore(_paths.SessionJson(sessionId));
        if (await sessionStore.ReadAsync(CancellationToken.None) is { } record)
        {
            long durationMs = decodedDurationMs ?? lastMs;
            lines = await transcript.ReadAllAsync(CancellationToken.None);
            await sessionStore.SaveAsync(record with
            {
                // Sources (not just RetainedAudioSources) reflects the channel mapping, exactly
                // as the success path sets it - a split-stereo import that faults mid-transcription
                // must not leave Sources = [Local] while RetainedAudioSources says [Local, Remote].
                Sources = legs.Select(l => l.Kind).ToArray(),
                DurationMs = durationMs,
                EndedAtUtc = record.StartedAtUtc.AddMilliseconds(durationMs),
                SegmentCount = lines.Count(l => l.Kind == TranscriptKind.Segment),
                MarkerCount = lines.Count(l => l.Kind == TranscriptKind.Marker),
                RetainedAudioSources = retained,
                // Decoded-stream provenance, stamped exactly as the success path's Save stage
                // does - leaving these at their non-nullable zero/"" default would serialize as
                // positive claims of no channels/no sample rate on a record that simultaneously
                // claims a decoded duration a few fields up.
                ImportedSource = record.ImportedSource is { } imported
                    ? imported with
                    {
                        DecodedDurationMs = decodedDurationMs ?? 0,
                        DecodedSampleRate = decodedSampleRate,
                        DecodedChannels = decodedChannels,
                        ChannelMapping = MappingLabel(decodedChannels, plan),
                        DurationMismatch = durationMismatch,
                    }
                    : record.ImportedSource,
            }, CancellationToken.None);
        }
        await new SessionWriter(_paths, _settings, _machineTime)
            .RegenerateProjectionsAsync(sessionId, CancellationToken.None);
    }

    /// <summary>Bounds and redacts a fault message before it becomes a permanent, unredactable
    /// transcript.jsonl marker (2026-08-11 review M4): a native/OS exception message can embed a
    /// full local file path (sometimes carrying a username) and can be locale-formatted with
    /// non-ASCII text. This document has no delete path by project rule and gets exported to
    /// opposing parties, so anything path-like is replaced wholesale and any non-ASCII character
    /// is dropped, then the result is bounded to a fixed length - the marker stays meaningful
    /// (exception type + whatever plain text remains) without becoming a leak or an unbounded
    /// blob.</summary>
    private static string SanitizeCauseMessage(Exception cause)
    {
        const int maxLength = 300;
        string text = PathLikeRegex.Replace(cause.Message, "[path removed]");
        text = new string(text.Where(c => c <= 127).ToArray()).Trim();
        if (text.Length == 0) text = cause.GetType().Name;
        return text.Length > maxLength ? text[..maxLength] + "..." : text;
    }

    // Matches an absolute path so it can be redacted wholesale rather than leaving fragments
    // that still read as a path: drive-letter (C:\... or C:/...), UNC (\\server\share\...), or
    // POSIX (/usr/local/bin) - each requires at least one separator PAST the root, so ordinary
    // text like "and/or" or "50/50" (a single slash, no root) never matches.
    private static readonly Regex PathLikeRegex = new(
        @"(?:[A-Za-z]:[\\/]|\\\\)[^\s""']*|/(?:[^\s""'/]+/)+[^\s""']*",
        RegexOptions.Compiled);

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
