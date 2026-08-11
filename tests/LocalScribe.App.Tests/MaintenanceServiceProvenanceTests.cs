using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins MaintenanceService.ProvenanceFor's mapping (design 2026-08-03 section 1; trimmed
/// Part 3, design 2026-08-11 section 3.3). The record now carries only what the lean header
/// renders - AudioFileName, HumanLayer, InProgress, ExcerptSpan - so this suite covers exactly
/// those: an inverted InProgress, and an audio field defaulted to "" instead of left null. The
/// Model/Backend/VersionId/AppVersion/WeightsFile/hash mapping these tests used to pin moved to
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
            // Two DISTINCT literals so a field swap fails loudly rather than lining up by
            // coincidence (Tier 1 T1-8).
            AppVersion = "charlie-version", WeightsFile = "delta-weights.bin",
            ImportedSource = importedSource,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "T" }, CancellationToken.None);
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
}
