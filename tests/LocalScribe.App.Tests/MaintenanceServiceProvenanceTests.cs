using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins MaintenanceService.ProvenanceFor's mapping (design 2026-08-03 section 1). Task 8
/// renders these fields into the exported metadata block next; today neither renderer looks at
/// `provenance` at all, so nothing else in the suite would catch a swapped Model/Backend/VersionId,
/// an inverted InProgress, or an audio field defaulted to "" instead of left null. Distinct literal
/// Model/Backend/VersionId values below (never substrings of one another) so a field swap fails
/// loudly rather than by coincidence lining up.</summary>
public sealed class MaintenanceServiceProvenanceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-maint-provenance-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public MaintenanceServiceProvenanceTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Root (v1) session only - no re-transcription needed, ProvenanceFor's VersionId
    /// mapping is exercised separately by MaintenanceServiceVersionsTests' active-version tests.
    /// No transcript.jsonl is written (mirrors WriteUnendedSessionAsync in MaintenanceServiceTests):
    /// the loader tolerates an absent transcript, and an absent one is exactly the in-progress case.</summary>
    private async Task<LoadedProjection> SeedAndLoadAsync(string id, DateTimeOffset? endedAtUtc,
        ImportedSourceInfo? importedSource)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = endedAtUtc,
            TimeZoneId = "UTC", UtcOffsetMinutes = 0,
            Model = "alpha-model", Backend = "bravo-backend", Language = "en",
            ImportedSource = importedSource,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "T" }, CancellationToken.None);
        return await SessionProjectionLoader.LoadAsync(
            _paths, new Settings(), TimeProvider.System, id, ct: CancellationToken.None);
    }

    [Fact]
    public async Task Model_backend_and_version_id_land_in_the_matching_fields()
    {
        var loaded = await SeedAndLoadAsync("s-mapping",
            new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero), importedSource: null);

        var provenance = MaintenanceService.ProvenanceFor(loaded);

        Assert.Equal("alpha-model", provenance.Model);
        Assert.Equal("bravo-backend", provenance.Backend);
        Assert.Equal(TranscriptVersions.Root, provenance.VersionId);   // "v1" - distinct from both above
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
        Assert.Null(provenance.AudioSha256);
    }

    [Fact]
    public async Task Imported_session_carries_the_source_file_name_and_hash()
    {
        var loaded = await SeedAndLoadAsync("s-imported",
            new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero),
            importedSource: new ImportedSourceInfo { FileName = "call.mp3", Sha256 = "deadbeef" });

        var provenance = MaintenanceService.ProvenanceFor(loaded);

        Assert.Equal("call.mp3", provenance.AudioFileName);
        Assert.Equal("deadbeef", provenance.AudioSha256);
    }

    [Fact]
    public async Task An_unsealed_session_carries_no_hashes_but_still_carries_the_accuracy_tier()
    {
        // The tier comes from the model NAME through the catalog, so it is available even for a
        // session recorded long before integrity manifests existed. Hashes are not.
        var loaded = await SeedAndLoadAsync("s-unsealed",
            new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero), importedSource: null);

        var provenance = MaintenanceService.ProvenanceFor(loaded);

        Assert.Null(provenance.TranscriptSha256);
        Assert.Empty(provenance.RecordedAudio);
        Assert.Equal("", provenance.ModelAccuracy);      // "alpha-model" is not in the catalog
    }}
