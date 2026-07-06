// tests/LocalScribe.App.Tests/MetadataEditorMatterPickerTests.cs
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.4 5.3: searchable matter picker. MatterSearchText filters MatterOptions by
/// Name + Reference + Id (OrdinalIgnoreCase Contains); an empty search lists ACTIVE matters only,
/// a non-empty search REVEALS matching archived matters (this replaces the retired
/// ShowArchivedMatters checkbox); _selectedMatterIds stays the selection truth, so a tagged
/// matter filtered out of the results is never dropped. Tag persistence is Group A's explicit
/// Save only - the picker adds no new write path.</summary>
public sealed class MetadataEditorMatterPickerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-matter-picker-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeReporter _reporter = new();
    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;

    public MetadataEditorMatterPickerTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths,
            new FakeSettings(new Settings { StorageRoot = _root }), new NoopBin(), _time);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        _session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private MetadataEditorViewModel MakeEditor()
        => new(_maintenance, _session, _reporter, dispatch: a => a(), _time, confirm: _ => true);

    /// <summary>Rows are minted through the locked page surface (ctor + OnNavigatedToAsync +
    /// Rows), never SessionRowViewModel's own ctor - same idiom as MetadataEditorViewModelTests.</summary>
    private async Task<SessionRowViewModel> RowAsync(string id)
    {
        var page = new SessionsPageViewModel(_maintenance, _session, new WindowRegistry(),
            _reporter, dispatch: a => a(), _time, revealInExplorer: _ => { });
        await page.OnNavigatedToAsync();
        return page.Rows.Single(r => r.Id == id);
    }

    private async Task WriteSessionAsync(string id, string title)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        var started = new DateTimeOffset(2026, 7, 2, 1, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(30),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
            Model = "small.en", Backend = "cuda", Language = "en",
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        { Title = title, MatterIds = [] }, CancellationToken.None);
    }

    private Task WriteMatterAsync(string id, string name, string? reference = null, bool archived = false)
        => _maintenance.SaveMatterAsync(new Matter
        {
            Id = id, Name = name, Reference = reference, Archived = archived,
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

    // Gated read (never a bare MatterStore) - same hazard note as MetadataEditorViewModelTests.
    private int SessionCount(string matterId)
        => _maintenance.ListMattersAsync(CancellationToken.None)
           .GetAwaiter().GetResult().Matters.Single(m => m.Id == matterId).SessionCount;

    [Fact]
    public async Task Search_filters_options_by_name_reference_and_id()
    {
        await WriteMatterAsync("M-20260701-001", "Estate of Alpha", reference: "EST-9");
        await WriteMatterAsync("M-20260701-002", "Beta Trust");
        await WriteMatterAsync("M-20260701-003", "Gamma Conveyance");
        await WriteSessionAsync("s-search", "S");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-search"));
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 3, TimeSpan.FromSeconds(10)));

        ed.MatterSearchText = "alpha";                        // name, case-insensitive
        Assert.Equal(["M-20260701-001"], ed.MatterOptions.Select(o => o.Id).ToArray());

        ed.MatterSearchText = "est-9";                        // reference
        Assert.Equal(["M-20260701-001"], ed.MatterOptions.Select(o => o.Id).ToArray());

        ed.MatterSearchText = "20260701-003";                 // id fragment
        Assert.Equal(["M-20260701-003"], ed.MatterOptions.Select(o => o.Id).ToArray());

        ed.MatterSearchText = "";                             // cleared: full active list back
        Assert.Equal(3, ed.MatterOptions.Count);
        Assert.Empty(_reporter.Errors);
    }

    [Fact]
    public async Task Search_reveals_matching_archived_matters_with_suffix()
    {
        await WriteMatterAsync("M-20260701-001", "Active Estate");
        await WriteMatterAsync("M-20260701-002", "Old Estate", archived: true);
        await WriteSessionAsync("s-arch", "S");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-arch"));
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 1, TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(ed.MatterOptions, o => o.Id == "M-20260701-002");   // empty search: active only

        ed.MatterSearchText = "estate";                       // search REVEALS archived matches
        Assert.Equal(2, ed.MatterOptions.Count);
        var old = ed.MatterOptions.Single(o => o.Id == "M-20260701-002");
        Assert.True(old.Archived);
        Assert.EndsWith("(archived)", old.Display, StringComparison.Ordinal);
        Assert.Empty(_reporter.Errors);
    }

    [Fact]
    public async Task Selected_matter_filtered_out_of_results_stays_tagged_and_saves()
    {
        await WriteMatterAsync("M-20260701-001", "Estate of Alpha");
        await WriteSessionAsync("s-keep", "S");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-keep"));
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 1, TimeSpan.FromSeconds(10)));

        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single());   // tag - BUFFERED (Group A)
        Assert.Equal("M-20260701-001", Assert.Single(ed.TaggedMatters).Id);

        ed.MatterSearchText = "zzz";                          // excludes the tagged matter
        Assert.Empty(ed.MatterOptions);                       // hidden from RESULTS...
        Assert.Equal("M-20260701-001", Assert.Single(ed.TaggedMatters).Id);   // ...never untagged

        await ed.SaveCommand.ExecuteAsync(null);              // explicit commit persists the hidden tag
        Assert.True(SpinWait.SpinUntil(() => SessionCount("M-20260701-001") == 1, TimeSpan.FromSeconds(10)));
        var meta = await new MetadataStore(_paths.MetaJson("s-keep")).LoadAsync(CancellationToken.None);
        Assert.Equal(["M-20260701-001"], meta!.MatterIds);
        Assert.Empty(_reporter.Errors);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }

    private sealed class NoopBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }
}
