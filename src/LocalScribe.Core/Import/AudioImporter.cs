using System.Globalization;
using System.Security.Cryptography;
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
    /// This is load-bearing, not defensive: SherpaDiarisationRunner.cs:23 branches on
    /// `forcedClusterCount is int k && k > 0`, so a null or 0 count would silently take the AUTO
    /// threshold-clustering path while the request claims it forced a specific speaker count.</summary>
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
/// (Origin/ImportedSource, decoded duration) -> re-render projections. Import is ATOMIC: any
/// failure, cancellation, or a declined gate deletes the partial session folder (design section 1
/// - an unfinished import is a derived output, not evidence; the original file is never touched).
/// KNOWN behavior: a hard crash mid-import leaves an un-ended folder that the startup recovery
/// scan finalizes as a recovered (possibly empty) session - the same semantics as a crashed live
/// recording; the user deletes it like any other row.</summary>
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

            bool mismatch = false;
            if (probe.ClaimedDurationMs is long claimed && claimed > 0
                && Math.Abs(decoded.DurationMs - claimed) * 100 > claimed)   // > 1 percent
            {
                // Design 4.1: pause AFTER Decode with a Continue/Cancel gate; continuing records a
                // transcript marker; declining is a cancel (the partial folder is deleted below).
                if (!await confirmDurationMismatch(new DurationMismatchInfo(claimed, decoded.DurationMs)))
                    throw new OperationCanceledException("import declined at the duration-mismatch gate");
                mismatch = true;
            }

            var plan = ChannelMapper.Plan(decoded.Channels, request.Stereo);
            var legs = await Task.Run(
                () => ChannelMapper.WriteLegs(decoded.PcmWavPath, plan, workDir, ct), ct);

            // Markers BEFORE transcription: TranscriptMerger.InitializeAsync continues the seq
            // after existing lines, and the Save-stage recount below fixes MarkerCount.
            var transcript = new TranscriptStore(_paths.TranscriptJsonl(sessionId));
            if (mismatch)
                await transcript.AppendAsync(TranscriptLine.Marker(
                    await transcript.NextSeqAsync(ct), 0,
                    string.Format(CultureInfo.InvariantCulture, Markers.ImportedDurationMismatch,
                        FormatDuration(probe.ClaimedDurationMs!.Value), FormatDuration(decoded.DurationMs))), ct);
            if (plan.DownmixedMultichannel)
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
                    DurationMismatch = mismatch,
                },
            }, ct);
            await new SessionWriter(_paths, _settings, _machineTime)
                .RegenerateProjectionsAsync(sessionId, ct);
            return sessionId;
        }
        catch
        {
            if (sessionId is not null)
                try { Directory.Delete(_paths.SessionDir(sessionId), recursive: true); } catch { }
            throw;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
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
