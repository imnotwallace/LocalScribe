using System.IO;
using DocumentFormat.OpenXml.Packaging;
using LocalScribe.App.Services;
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
            new Dictionary<int, string> { [0] = "V2 corrected." }, [], CancellationToken.None);

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
            new SpeakerPinTarget.Cluster("Local:0"), CancellationToken.None);

        Assert.True(pinned);
        Assert.True(File.Exists(_paths.SpeakersJson(id, Vid)));
        Assert.False(File.Exists(_paths.SpeakersJson(id)));              // root untouched

        bool removed = await svc.RemoveSpeakerPinsAsync(id, TranscriptSource.Local, [0], CancellationToken.None);
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
}
