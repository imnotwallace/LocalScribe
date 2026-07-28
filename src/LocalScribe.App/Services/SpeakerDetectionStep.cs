// src/LocalScribe.App/Services/SpeakerDetectionStep.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Services;

/// <summary>How one import-time detection pass ended.</summary>
public enum SpeakerDetectionResult
{
    /// <summary>Two or more clusters were committed to speakers.json.</summary>
    Committed,
    /// <summary>Exactly one (or zero) voices found - nothing committed, marker written.</summary>
    OneVoice,
    /// <summary>No retained audio leg to read (AudioRetention "never", or the leg is gone).</summary>
    NoAudio,
    /// <summary>The helper exe or a sherpa model was missing at run time.</summary>
    Unavailable,
    /// <summary>The pass threw. The import itself is untouched and still valid.</summary>
    Failed,
    /// <summary>The user cancelled. The import is kept; nothing is recorded.</summary>
    Cancelled,
}

public sealed record SpeakerDetectionOutcome(SpeakerDetectionResult Result, int ClusterCount);

/// <summary>The post-import speaker-detection phase (design 2026-07-28 section 3). Deliberately
/// runs AFTER AudioImporter.ImportAsync has returned, in the App layer:
///
/// - AudioImporter.cs:205-210 deletes the ENTIRE session folder on any throw inside its try. A
///   DiarisationException raised in there would destroy a fully transcribed, fully provenanced
///   import. Running afterwards makes that structurally impossible.
/// - The Save-stage `record with` at AudioImporter.cs:185 operates on a snapshot read at :183, so
///   anything writing session.json in between is clobbered - including Diarised.
/// - MaintenanceService lives in the WPF assembly, so Core could not call the commit path anyway.
///
/// The diarise call runs OUTSIDE the per-session gate: it is minutes of CPU and
/// RunForSessionAsync is a SemaphoreSlim(1,1) that every other writer for this session queues on.
/// Reads happen under the gate, the engine runs outside it, and SaveDiarisationAsync takes the gate
/// itself.</summary>
public sealed class SpeakerDetectionStep(
    IDiarisationEngine engine,
    MaintenanceService maintenance,
    StoragePaths paths,
    ISettingsService settings,
    Func<string, string> resolveModel,
    string diarizerExePath,
    TimeProvider time)
{
    public async Task<SpeakerDetectionOutcome> RunAsync(string sessionId, SpeakerDetection mode,
        int? speakerCount, IProgress<double>? progress, CancellationToken ct)
    {
        if (mode == SpeakerDetection.Off)
            throw new ArgumentOutOfRangeException(nameof(mode),
                "SpeakerDetectionStep must not be invoked for SpeakerDetection.Off.");

        int? forced = mode == SpeakerDetection.Declared ? speakerCount : null;

        try
        {
            // Re-check availability: the dialog gated at open, but the exe could have gone in the
            // interval, and a missing one throws Win32Exception rather than DiarisationException.
            if (DiarisationAvailability.Probe(resolveModel, diarizerExePath) is string unavailable)
            {
                await MarkAsync(sessionId, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    Markers.SpeakerDetectionFailed, unavailable), ct);
                await WriteDeclaredCountAsync(sessionId, mode, speakerCount, ct);
                return new SpeakerDetectionOutcome(SpeakerDetectionResult.Unavailable, 0);
            }

            // --- read phase, under the gate ---
            var loaded = await maintenance.RunForSessionAsync(sessionId, async inner =>
            {
                var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(inner)
                    ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
                var lines = await new TranscriptStore(
                    paths.TranscriptJsonl(sessionId, session.ActiveVersion)).ReadAllAsync(inner);
                string? leg = AudioLegProbe.Resolve(paths, sessionId, SourceKind.Local,
                    session.RetainedAudioSources, settings.Current.AudioFormat);
                return (session.ActiveVersion, lines, leg);
            }, ct);

            if (loaded.leg is null)
            {
                await MarkAsync(sessionId, Markers.SpeakerDetectionNoAudio, ct);
                await WriteDeclaredCountAsync(sessionId, mode, speakerCount, ct);
                return new SpeakerDetectionOutcome(SpeakerDetectionResult.NoAudio, 0);
            }

            // --- diarise, OUTSIDE the gate ---
            var request = new DiarisationRequest(
                loaded.leg, SourceKind.Local,
                resolveModel(DiarisationModels.Segmentation),
                resolveModel(DiarisationModels.Embedding),
                forced,
                // Emit embeddings so embeddings.json lands during the import and the voiceprint
                // suggestion chips work when Split Speakers opens - without a second pass.
                EmitEmbeddings: true);

            var result = await engine.DiariseAsync(request, progress ?? NullProgress.Instance, ct);
            var assignment = ClusterAssigner.Assign(loaded.lines, result.Segments, SourceKind.Local);

            // A collapse to one cluster is exactly what the untuned 0.5f threshold
            // (SherpaDiarisationRunner.cs:26) did on the only run on record. Labelling the whole
            // call "Local Speaker 1" is not an improvement over "Me", so commit nothing - and mark
            // it, because without a commit Diarised stays false and nothing else records the run.
            if (assignment.ClusterKeys.Count <= 1)
            {
                await MarkAsync(sessionId, Markers.SpeakerDetectionOneVoice, ct);
                await WriteDeclaredCountAsync(sessionId, mode, speakerCount, ct);
                return new SpeakerDetectionOutcome(
                    SpeakerDetectionResult.OneVoice, assignment.ClusterKeys.Count);
            }

            // --- commit ---
            var names = assignment.ClusterKeys.ToDictionary(
                key => key,
                key => DefaultSpeakerLabels.For(SourceKind.Local, ParseClusterId(key)),
                StringComparer.Ordinal);

            var commit = new DiarisationCommit(
                [SourceKind.Local],
                new Dictionary<string, IReadOnlyDictionary<string, string>>
                { [SourceKind.Local.ToString()] = assignment.SeqToClusterKey },
                names,
                result.Method,
                time.GetUtcNow());

            await maintenance.SaveDiarisationAsync(sessionId, commit, loaded.ActiveVersion,
                // No participant ownership: the import never names anyone. Passing an empty map
                // (not null) keeps the meta.json ownership pass on its normal branch.
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, DiarisationResult>(StringComparer.Ordinal)
                { [SourceKind.Local.ToString()] = result },
                ct);

            // Constraint 5: Declared(n) writes n even here - the user's assertion, not whatever
            // count this particular run actually landed on (a real engine forced at N can still
            // commit fewer clusters than N when two voices sound alike), because n is what
            // pre-configures the force-N retry button. Auto writes the truthful committed count.
            int localCount = mode == SpeakerDetection.Declared && speakerCount is int declaredCount
                ? declaredCount
                : assignment.ClusterKeys.Count;
            await WriteLocalCountAsync(sessionId, localCount, ct);
            return new SpeakerDetectionOutcome(
                SpeakerDetectionResult.Committed, assignment.ClusterKeys.Count);
        }
        catch (OperationCanceledException)
        {
            // A choice, not a degradation. The import is already complete and valid.
            return new SpeakerDetectionOutcome(SpeakerDetectionResult.Cancelled, 0);
        }
        catch (Exception ex)
        {
            // Deliberately broad. A missing helper exe throws Win32Exception straight out of
            // ProcessDiarisationHelper.cs:33 - SherpaHelperDiariser.cs:47 does not catch it - so
            // catching only DiarisationException would let it escape and fault the whole import.
            try
            {
                await MarkAsync(sessionId, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    Markers.SpeakerDetectionFailed, ex.Message), CancellationToken.None);
                await WriteDeclaredCountAsync(sessionId, mode, speakerCount, CancellationToken.None);
            }
            catch { /* the marker is best-effort: never turn a detection fault into an import fault */ }
            return new SpeakerDetectionOutcome(SpeakerDetectionResult.Failed, 0);
        }
    }

    private static int ParseClusterId(string clusterKey)
    {
        int idx = clusterKey.IndexOf(':');
        return idx >= 0 && idx + 1 < clusterKey.Length
            && int.TryParse(clusterKey[(idx + 1)..], out int id) ? id : 0;
    }

    /// <summary>Append a marker AND correct session.json's MarkerCount. AudioImporter.cs:185-200
    /// recounts markers during the Save stage; detection runs after Save, so a bare append would
    /// leave the count stale by one.</summary>
    private Task MarkAsync(string sessionId, string text, CancellationToken ct)
        => maintenance.RunForSessionAsync(sessionId, async inner =>
        {
            var store = new TranscriptStore(paths.TranscriptJsonl(sessionId));
            await store.AppendAsync(
                TranscriptLine.Marker(await store.NextSeqAsync(inner), 0, text), inner);

            var lines = await store.ReadAllAsync(inner);
            var sessionStore = new SessionStore(paths.SessionJson(sessionId));
            if (await sessionStore.ReadAsync(inner) is { } session)
                await sessionStore.SaveAsync(
                    session with { MarkerCount = lines.Count(l => l.Kind == TranscriptKind.Marker) },
                    inner);
            return true;
        }, ct);

    /// <summary>Declared(n) is written even on the failure paths: the user asserted it, and it
    /// pre-configures the force-N button for a manual retry in Split Speakers.</summary>
    private Task WriteDeclaredCountAsync(string sessionId, SpeakerDetection mode, int? count,
        CancellationToken ct)
        => mode == SpeakerDetection.Declared && count is int n
            ? WriteLocalCountAsync(sessionId, n, ct)
            : Task.CompletedTask;

    private Task WriteLocalCountAsync(string sessionId, int count, CancellationToken ct)
        => maintenance.RunForSessionAsync(sessionId, async inner =>
        {
            var store = new MetadataStore(paths.MetaJson(sessionId));
            var meta = await store.LoadAsync(inner);
            if (meta is null || meta.LocalCount == count) return false;
            // Never flip Edited/LastEditedAtUtc - reserved for manual transcript corrections.
            await store.SaveAsync(meta with { LocalCount = count }, inner);
            return true;
        }, ct);

    private sealed class NullProgress : IProgress<double>
    {
        public static readonly NullProgress Instance = new();
        public void Report(double value) { }
    }
}
