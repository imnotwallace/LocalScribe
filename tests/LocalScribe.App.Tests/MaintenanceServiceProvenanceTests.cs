using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins MaintenanceService.ProvenanceFor's mapping (design 2026-08-03 section 1; trimmed
/// Part 3, design 2026-08-11 section 3.3). The record now carries only what the lean header
/// renders - AudioFileName, HumanLayer, InProgress, ExcerptSpan - and this suite covers three of
/// the four: AudioFileName (null for a recorded session, the file name for an import), InProgress
/// (inverted), and the HumanLayer half that actually chooses an evidentiary sentence, asserted
/// THROUGH MetadataFormat.DisclaimerFor rather than on the raw counts. ExcerptSpan is not composed
/// by ProvenanceFor at all - the three export methods stamp it via `with` - so it is pinned by
/// MaintenanceServiceTests' real-ExcerptRange export tests instead.
///
/// The DisclaimerFor pair below is the coverage that was missing when a fully automatic import
/// could claim "reviewed and corrected by a person" (whole-branch review finding 1): every
/// disclaimer assertion lived either in Core renderer tests (which build ExportProvenance by hand)
/// or on the UNTOUCHED variant, so nothing ever ran real on-disk speakers.json through
/// ProvenanceFor into the sentence it selects.
///
/// The Model/Backend/VersionId/AppVersion/WeightsFile/hash mapping these tests used to pin moved to
/// session.json/meta.json/manifest.json, which the .zip bundle copies verbatim; nothing here reads
/// them any more.</summary>
public sealed class MaintenanceServiceProvenanceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-maint-provenance-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public MaintenanceServiceProvenanceTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Root (v1) session only - these tests never switch versions, so everything lands in
    /// the session root that StoragePaths.VersionDir("v1") resolves to. (ProvenanceFor had a
    /// VersionId mapping until Part 3 trimmed the record; it does not any more, so there is nothing
    /// version-specific left here to cover.)
    /// No transcript.jsonl is written (mirrors WriteUnendedSessionAsync in MaintenanceServiceTests):
    /// the loader tolerates an absent transcript, and an absent one is exactly the in-progress case.
    /// <paramref name="speakerNames"/> seeds speakers.json's Names map (clusterKey -> name); null
    /// writes no speakers.json at all, which is the pre-diarisation state.</summary>
    private async Task<LoadedProjection> SeedAndLoadAsync(string id, DateTimeOffset? endedAtUtc,
        ImportedSourceInfo? importedSource,
        IReadOnlyDictionary<string, string>? speakerNames = null)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = endedAtUtc,
            TimeZoneId = "UTC", UtcOffsetMinutes = 0,
            Model = "alpha-model", Backend = "bravo-backend", Language = "en",
            // Two DISTINCT literals so a field swap fails loudly rather than lining up by
            // coincidence (Tier 1 T1-8).
            AppVersion = "charlie-version", WeightsFile = "delta-weights.bin",
            ImportedSource = importedSource,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "T" }, CancellationToken.None);
        if (speakerNames is not null)
            await new SpeakersStore(_paths.SpeakersJson(id, TranscriptVersions.Root)).SaveAsync(
                new Speakers { Names = speakerNames }, CancellationToken.None);
        return await SessionProjectionLoader.LoadAsync(
            _paths, new Settings(), TimeProvider.System, id, ct: CancellationToken.None);
    }

    [Fact]
    public async Task InProgress_is_true_exactly_when_EndedAtUtc_is_null()
    {
        var live = await SeedAndLoadAsync("s-live", endedAtUtc: null, importedSource: null);
        var finished = await SeedAndLoadAsync("s-done",
            new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero), importedSource: null);

        Assert.True(MaintenanceService.ProvenanceFor(live).InProgress);
        Assert.False(MaintenanceService.ProvenanceFor(finished).InProgress);
    }

    [Fact]
    public async Task Recorded_session_leaves_audio_fields_null_not_empty_string()
    {
        var loaded = await SeedAndLoadAsync("s-recorded",
            new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero), importedSource: null);

        var provenance = MaintenanceService.ProvenanceFor(loaded);

        Assert.Null(provenance.AudioFileName);
    }

    [Fact]
    public async Task Imported_session_carries_the_source_file_name()
    {
        var loaded = await SeedAndLoadAsync("s-imported",
            new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero),
            importedSource: new ImportedSourceInfo { FileName = "call.mp3", Sha256 = "deadbeef" });

        var provenance = MaintenanceService.ProvenanceFor(loaded);

        Assert.Equal("call.mp3", provenance.AudioFileName);
    }

    [Fact]
    public async Task Auto_detected_speaker_labels_never_claim_a_person_reviewed_the_transcript()
    {
        // The DEFAULT import path end to end (whole-branch review finding 1): "Detect
        // automatically" is ImportDialogViewModel's preselected choice, SpeakerDetectionStep
        // commits DefaultSpeakerLabels.For(...) for every cluster it finds, and nobody ever opened
        // the transcript. Exactly these bytes land in speakers.json, and the served .docx used to
        // say "reviewed and corrected by a person" over them.
        var loaded = await SeedAndLoadAsync("s-auto",
            new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero), importedSource: null,
            speakerNames: new Dictionary<string, string>(StringComparer.Ordinal)
            { ["Local:0"] = "Local Speaker 1", ["Local:1"] = "Local Speaker 2" });

        var provenance = MaintenanceService.ProvenanceFor(loaded);

        Assert.Equal(0, provenance.HumanLayer!.SpeakerNames);
        Assert.Equal(ExportNotices.Disclaimer, MetadataFormat.DisclaimerFor(provenance.HumanLayer));
    }

    [Fact]
    public async Task A_speaker_a_person_named_does_flip_the_disclaimer()
    {
        // The converse, and the reason the count is not simply deleted: renaming a cluster IS a
        // person's judgement rendered into the body of the document, so it must still disclose.
        // One machine default alongside it, so this fails if the fix over-corrects to "never count".
        var loaded = await SeedAndLoadAsync("s-named",
            new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero), importedSource: null,
            speakerNames: new Dictionary<string, string>(StringComparer.Ordinal)
            { ["Local:0"] = "Local Speaker 1", ["Local:1"] = "Ms Chen" });

        var provenance = MaintenanceService.ProvenanceFor(loaded);

        Assert.Equal(1, provenance.HumanLayer!.SpeakerNames);
        Assert.Equal(ExportNotices.DisclaimerEdited, MetadataFormat.DisclaimerFor(provenance.HumanLayer));
    }
}
