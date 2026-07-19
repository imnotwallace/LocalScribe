using System.IO;
using DocumentFormat.OpenXml.Packaging;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MaintenanceServiceVersionsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-maint-versions-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public MaintenanceServiceVersionsTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string Vid = "v2-tiny.en-2026-07-13";

    private MaintenanceService MakeService(Settings? settings = null)
        => new(_paths, new FakeSettingsService(settings ?? new Settings()),
            new FakeRecycleBin(), TimeProvider.System);

    /// <summary>Root (v1) session with seq 0 "Root words."; completed v2 (active) whose jsonl
    /// has seq 0 "V2 words." - the exact shape RetranscriptionRunner commits.</summary>
    private async Task<string> SeedVersionedAsync()
    {
        string id = "2026-07-10_1000_Webex_seed";
        Directory.CreateDirectory(_paths.SessionDir(id));
        Directory.CreateDirectory(_paths.VersionDir(id, Vid));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 1, 0, TimeSpan.Zero),
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            DurationMs = 60000, Model = "small.en", Backend = "CUDA", Language = "en",
            ActiveVersion = Vid,
            Versions = new[] { new TranscriptVersion { Id = Vid, Model = "tiny.en", Backend = "CPU", Language = "en" } },
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Seed", Medium = Medium.Webex }, default);
        var rootT = new TranscriptStore(_paths.TranscriptJsonl(id));
        await rootT.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Root words.", "Me"), default);
        var vT = new TranscriptStore(_paths.TranscriptJsonl(id, Vid));
        await vT.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "V2 words.", "Me"), default);
        await JsonFile.WriteAsync(_paths.EditsJson(id, Vid), new Edits(), default);
        return id;
    }

    [Fact]
    public async Task SaveTextCorrections_targets_the_active_versions_edits_and_projections()
    {
        string id = await SeedVersionedAsync();
        var svc = MakeService();

        bool changed = await svc.SaveTextCorrectionsAsync(id,
            new Dictionary<int, string> { [0] = "V2 corrected." }, [], Vid, CancellationToken.None);

        Assert.True(changed);
        var vEdits = await new EditStore(_paths.SessionDir(id), TimeProvider.System,
            contentDir: _paths.VersionDir(id, Vid)).LoadAsync(default);
        Assert.Equal("V2 corrected.", vEdits!.Corrections["0"].Text);
        Assert.False(File.Exists(_paths.EditsJson(id)));                 // root v1 edits untouched
        Assert.Contains("V2 corrected.", await File.ReadAllTextAsync(_paths.TranscriptMd(id, Vid)));
        Assert.False(File.Exists(_paths.TranscriptMd(id)));              // root projection untouched
    }

    [Fact]
    public async Task SpeakerPins_write_the_active_versions_speakers_json()
    {
        string id = await SeedVersionedAsync();
        var svc = MakeService();

        bool pinned = await svc.SaveSpeakerPinsAsync(id, TranscriptSource.Local, [0],
            new SpeakerPinTarget.Cluster("Local:0"), Vid, CancellationToken.None);

        Assert.True(pinned);
        Assert.True(File.Exists(_paths.SpeakersJson(id, Vid)));
        Assert.False(File.Exists(_paths.SpeakersJson(id)));              // root untouched

        bool removed = await svc.RemoveSpeakerPinsAsync(id, TranscriptSource.Local, [0], Vid, CancellationToken.None);
        Assert.True(removed);
    }

    [Fact]
    public async Task SetActiveVersion_persists_validates_and_noops()
    {
        string id = await SeedVersionedAsync();
        var svc = MakeService();

        Assert.True(await svc.SetActiveVersionAsync(id, "v1", CancellationToken.None));
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("v1", session!.ActiveVersion);

        Assert.True(await svc.SetActiveVersionAsync(id, Vid, CancellationToken.None));   // back
        Assert.True(await svc.SetActiveVersionAsync(id, Vid, CancellationToken.None));   // idempotent

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SetActiveVersionAsync(id, "v9-nope-2026-01-01", CancellationToken.None));
        Assert.False(await svc.SetActiveVersionAsync("no-such-session", "v1", CancellationToken.None));
    }

    // ---- F1 (BLOCKING, whole-branch review): writes must target the version the content was
    // AUTHORED against, not whatever ActiveVersion happens to be on disk at write time. ----

    [Fact]
    public async Task SaveTextCorrections_targets_the_AUTHORED_version_even_when_ActiveVersion_raced_to_v1()
    {
        // The deterministic bleed path (F1 test 1): a user editing v2 (or a background
        // re-transcription completing mid-edit) can leave ActiveVersion pointing at v1 by the time
        // Save actually lands. Before the fix, the old signature re-resolved ActiveVersion at
        // write time (v1) and wrote the "V2" correction into ROOT v1's edits.json instead - a
        // silent misattribution across two files that both number seqs from 0.
        string id = await SeedVersionedAsync();
        var svc = MakeService();
        Assert.True(await svc.SetActiveVersionAsync(id, "v1", CancellationToken.None));   // simulate the race

        bool changed = await svc.SaveTextCorrectionsAsync(id,
            new Dictionary<int, string> { [0] = "V2 corrected despite the race." }, [],
            Vid, CancellationToken.None);

        Assert.True(changed);
        var vEdits = await new EditStore(_paths.SessionDir(id), TimeProvider.System,
            contentDir: _paths.VersionDir(id, Vid)).LoadAsync(default);
        Assert.Equal("V2 corrected despite the race.", vEdits!.Corrections["0"].Text);
        Assert.False(File.Exists(_paths.EditsJson(id)));       // ROOT v1 edits.json: unchanged/absent

        // Sanity: the race was real - ActiveVersion genuinely reads v1 on disk right now, so the
        // assertions above prove the write targeted v2 BECAUSE of the explicit versionId argument,
        // not because ActiveVersion secretly still pointed at v2.
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("v1", session!.ActiveVersion);
    }

    [Fact]
    public async Task SaveDiarisation_targets_the_AUTHORED_version_even_when_ActiveVersion_raced_to_v1()
    {
        // F1 test 2 (the diarisation path, SplitSpeakersViewModel.ConfirmAsync's shape): pins
        // authored against v2 must land in v2's speakers.json even when on-disk ActiveVersion has
        // flipped to v1 by confirm time.
        string id = await SeedVersionedAsync();
        var svc = MakeService();
        Assert.True(await svc.SetActiveVersionAsync(id, "v1", CancellationToken.None));   // simulate the race

        var commit = new DiarisationCommit(
            [SourceKind.Local],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["Local"] = new Dictionary<string, string> { ["0"] = "Local:0" } },
            new Dictionary<string, string> { ["Local:0"] = "Local Speaker 1" },
            "sherpa", DateTimeOffset.UnixEpoch);

        await svc.SaveDiarisationAsync(id, commit, Vid, CancellationToken.None);

        Assert.True(File.Exists(_paths.SpeakersJson(id, Vid)));
        Assert.False(File.Exists(_paths.SpeakersJson(id)));    // ROOT v1 speakers.json: absent
        var speakers = await new SpeakersStore(_paths.SpeakersJson(id, Vid)).LoadAsync(default);
        Assert.Equal("Local:0", speakers!.Assignments["Local"]["0"]);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("v1", session!.ActiveVersion);             // the race was real
    }

    [Fact]
    public async Task Content_write_refuses_an_unknown_versionId_rather_than_silently_writing_to_root()
    {
        // F1 test 3 (defense-in-depth): a versionId that is neither "v1" nor a recorded Versions
        // entry must be refused loudly (EnsureKnownVersion), never silently redirected to root -
        // e.g. the target of a lost update that raced session.json's Versions list.
        string id = await SeedVersionedAsync();
        var svc = MakeService();
        const string bogus = "v9-nope-2026-01-01";

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveTextCorrectionsAsync(id, new Dictionary<int, string> { [0] = "x" }, [],
                bogus, CancellationToken.None));

        Assert.False(File.Exists(_paths.EditsJson(id)));           // no silent fallback write to root
        Assert.False(File.Exists(_paths.EditsJson(id, bogus)));    // and no write into a bogus folder
    }

    [Fact]
    public async Task ExportDocx_footer_names_the_active_version_and_model()
    {
        string id = await SeedVersionedAsync();
        var svc = MakeService(new Settings { DocxFooterText = "PRIVILEGED" });
        string dest = Path.Combine(_root, "out.docx");

        await svc.ExportDocxAsync(id, dest, new DocxOptions(), CancellationToken.None);

        using var doc = WordprocessingDocument.Open(dest, false);
        string footer = doc.MainDocumentPart!.FooterParts.Single().Footer!.InnerText;
        Assert.Contains("PRIVILEGED", footer);
        Assert.Contains("v2", footer);
        Assert.Contains("tiny.en", footer);

        // v1-active session: the footer is EXACTLY the configured text (no version note).
        await svc.SetActiveVersionAsync(id, "v1", CancellationToken.None);
        string dest1 = Path.Combine(_root, "out-v1.docx");
        await svc.ExportDocxAsync(id, dest1, new DocxOptions(), CancellationToken.None);
        using var doc1 = WordprocessingDocument.Open(dest1, false);
        Assert.Equal("PRIVILEGED", doc1.MainDocumentPart!.FooterParts.Single().Footer!.InnerText);
    }

    [Fact]
    public async Task ExportMarkdown_footer_names_the_active_version_and_model()
    {
        // The markdown mirror must compose the SAME versioned footer ExportDocxAsync does
        // (Transcript version <short> (<model>)), and read the ACTIVE version's transcript.
        string id = await SeedVersionedAsync();
        var svc = MakeService(new Settings { DocxFooterText = "PRIVILEGED" });
        string dest = Path.Combine(_root, "out.md");

        await svc.ExportMarkdownAsync(id, dest, new DocxOptions(), CancellationToken.None);
        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains("V2 words.", md);                                  // active v2, not root
        Assert.EndsWith("---\n\nPRIVILEGED - Transcript version v2 (tiny.en)\n", md);

        // v1-active session: the footer is EXACTLY the configured text (no version note).
        await svc.SetActiveVersionAsync(id, "v1", CancellationToken.None);
        string dest1 = Path.Combine(_root, "out-v1.md");
        await svc.ExportMarkdownAsync(id, dest1, new DocxOptions(), CancellationToken.None);
        string md1 = await File.ReadAllTextAsync(dest1);
        Assert.Contains("Root words.", md1);
        Assert.EndsWith("---\n\nPRIVILEGED\n", md1);
    }
}
