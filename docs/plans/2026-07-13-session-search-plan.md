# Cross-Session Search Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement design spec §2 (docs/plans/2026-07-13-meetily-round-design.md): a self-healing persisted cross-session search index in Core (`Search\` namespace) over corrected text + original machine text + speaker names, surfaced in the App three ways — a new Search page (nav-rail item between Sessions and Matters, with facets and click-through into the read view), content matching in the Sessions quick filter (debounced, one snippet line per matched row), and a Ctrl+F find bar in the read view over the visible corrected text. Search never mutates any session data; original-text matches are labelled "(matches original text)"; corrections never hide content from search.

**Architecture:** Core gains `Search\SearchModels.cs` (cache DTOs + query/result records), `SearchIndexStore` (schema-stamped `<storageRoot>\index\search-index.json` via AtomicFile; corrupt/newer → null → silent full rebuild), `SearchIndexBuilder` (derives one session's entry through `SessionProjectionLoader.LoadAsync` — the same pipeline the read view and exporters use, so the index follows the ACTIVE version automatically — plus freshness stamps: last-write ticks of the active version's transcript.jsonl/edits.json/speakers.json + root meta.json + the active-version id), `SearchQueryEngine` (pure: case-insensitive substring, multi-word AND across a session, hit-count-then-recency ranking, ±60-char snippets, original-only labelling, speaker-name fallback hits, matter/date/app facets), and `SearchIndexService` (in-memory index; `InitializeAsync` self-heals against the cache — stale/missing re-derived, orphans dropped, unreadable sessions skipped+logged via `SessionSkipped`; `ReindexSessionAsync` for incremental updates; debounced cache rewrite + `FlushAsync`). The App layer adds one seam — `MaintenanceService.SessionContentChanged` raised after every gated session mutation (meta save, archive flip, corrections, splits, pins, diarisation, recovery, re-render, version switch, delete) — and `App.xaml.cs` wires it plus `SessionController.SessionFinalizeCompleted` (and the retranscription/import completion seams) to `ReindexSessionAsync`; the index is built off the UI thread after the startup recovery scan. Three UI surfaces: `SearchPage`/`SearchPageViewModel` (cards with snippet rows; click → `ReadViewWindow.ShowFindAt(seq, term)`), `SessionsPageViewModel` content filter (optional `SearchIndexService` ctor param + `SessionRowViewModel.ContentSnippet`), and `ReadViewViewModel` find-bar state (`ReadRow.IsFindMatch`/`IsCurrentFindMatch` row tints, "2/7" status, Enter/Shift+Enter/Escape).

**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, System.Text.Json, xUnit.

## Global Constraints

- Target branch: `feat/session-search`, created off master AFTER `feat/record-console-polish`, `feat/retranscription-versions`, and `feat/audio-import` merge — this feature merges LAST (design §1: search must index active versions and imported sessions). The design spec `docs/plans/2026-07-13-meetily-round-design.md` is already on master; THIS plan file (`docs/plans/2026-07-13-session-search-plan.md`) must be added to the branch in Task 1's commit.
- 0-warning build gate must hold (`-warnaserror` builds clean).
- Tests: xUnit. Filtered run: `dotnet test "<testproj>" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\`
- IMPORTANT: the LocalScribe app may be running and LOCK its bin DLLs (MSB3027 copy error — NOT a compile error). ALWAYS build/test with the isolated `-p:BaseOutputPath` above (every command below already appends it). NEVER kill the user's running app.
- Never use Unicode emojis in test code or scripts (project rule). Non-emoji escapes like `…` (ellipsis) are fine and are written as `\u` escapes to keep source ASCII (house style, see `Markers.cs`).
- Test projects: App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. Core.Tests has a global `<Using Include="Xunit" />` + ImplicitUsings and its test classes sit in the GLOBAL namespace (see `SessionProjectionLoaderTests.cs`); App.Tests files use explicit `using Xunit;` + `namespace LocalScribe.App.Tests;`. App.Tests does NOT project-reference Core.Tests — it `<Compile Include>`-links `LiveTestDoubles.cs` (with `LiveTestDoubles.MakeController/Options`) so those doubles compile INTO App.Tests.
- Per-test-file private fakes are the convention in App.Tests (each file declares its own `FakeSettings`/`NoopBin`/`FakeReporter` etc. so it compiles standalone) — the new test files below follow it.
- Commit style: `feat(core)`/`feat(app)`/`test(...)`/`docs(...)`. Every commit message MUST end with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```
- **Anchor drift warning:** line anchors below are grounded @ master 7d6c88d, but THREE branches merge before this one, so anchors in `SessionRecord`/`StoragePaths`/`SessionsPage.xaml`/`SessionsPageViewModel.cs`/`ReadViewViewModel.cs`/`App.xaml.cs` WILL have drifted. Locate every edit by the QUOTED CODE, never by line number. Cross-branch types written against the agreed contract (treat as existing when this branch is built):
```csharp
// SessionRecord (schema v4): public string ActiveVersion { get; init; } = "v1";
//                            public IReadOnlyList<TranscriptVersion> Versions { get; init; } = [];
// public sealed record TranscriptVersion { string Id; string Model; string? WeightsFile; string Backend; string Language; DateTimeOffset CreatedAtUtc; bool VocabularyApplied; }  // all { get; init; } — construct with object initializers only
// StoragePaths ("v1" resolves to the session root):
//   public string VersionDir(string id, string versionId);
//   public string TranscriptJsonl(string id, string versionId);
//   public string EditsJson(string id, string versionId);
//   public string SpeakersJson(string id, string versionId);
// SessionProjectionLoader.LoadAsync(StoragePaths, Settings, TimeProvider, string, CancellationToken)
//   — unchanged 5-arg signature, follows ActiveVersion automatically; LoadedProjection gained a
//   trailing property: string VersionId.
// Core Retranscription\RetranscriptionRunner: public event Action<string>? RetranscriptionCompleted;
// MaintenanceService.SetActiveVersionAsync(sessionId, versionId, ct) : Task<bool>
// SessionRecord.Origin ("recorded"/"imported", audio-import branch): no special handling — imported
//   sessions are normal finalized sessions.
```
- Evidentiary rules (design §1, locked): search READS only — no task below writes to any session file. The one new persisted file is the derived, self-healing `<storageRoot>\index\search-index.json` (safe to delete). Corrections never hide content from search; original-text matches are labelled.

---

### Task 1: Core — `StoragePaths.SearchIndexJson`, search models, `SearchIndexStore`
**Files:**
- Modify `src\LocalScribe.Core\Storage\StoragePaths.cs` (add one getter after the `MatterJson` line).
- New `src\LocalScribe.Core\Search\SearchModels.cs`.
- New `src\LocalScribe.Core\Search\SearchIndexStore.cs`.
- Test (new) `tests\LocalScribe.Core.Tests\SearchIndexStoreTests.cs`.
- Also add this plan file `docs\plans\2026-07-13-session-search-plan.md` in the commit.

**Interfaces:**
- Consumes: `AtomicFile`/`JsonFile` (atomic writes, shared JSON options), `SchemaGuard` (version read/reject pattern), `StoragePaths.Root`.
- Produces:
```csharp
// StoragePaths
public string SearchIndexJson { get; }   // <Root>\index\search-index.json
// namespace LocalScribe.Core.Search
public sealed record SearchLine(int Seq, int PartIndex, long StartMs, string Text, string? OriginalText, string Speaker);
public sealed record SearchFreshnessStamps { long TranscriptTicks; long EditsTicks; long SpeakersTicks; long MetaTicks; }   // init-props
public sealed record SearchSessionEntry { string SessionId; string Title; IReadOnlyList<string> MatterIds; DateTimeOffset StartedAtUtc; int? UtcOffsetMinutes; string App; IReadOnlyList<string> Participants; string VersionId = "v1"; SearchFreshnessStamps Stamps; IReadOnlyList<SearchLine> Lines; }   // init-props
public sealed record SearchIndexCache { int SchemaVersion = 1; IReadOnlyList<SearchSessionEntry> Sessions; }   // init-props
public sealed record SearchQuery(string Text, string? MatterId = null, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, string? App = null);
public sealed record SearchHit(int Seq, int PartIndex, long StartMs, string Speaker, string Snippet, string MatchedTerm, bool MatchesOriginalOnly, bool IsSpeakerNameMatch);
public sealed record SearchResult(SearchSessionEntry Session, IReadOnlyList<SearchHit> Hits, int HitCount);
public sealed class SearchIndexStore(StoragePaths paths)
{
    public const int Version = 1;
    public Task<SearchIndexCache?> LoadAsync(CancellationToken ct);   // null on missing/corrupt/newer
    public Task SaveAsync(SearchIndexCache cache, CancellationToken ct);
}
```

Steps:
- [ ] **Create the branch and add the plan.** `git checkout master && git pull && git checkout -b feat/session-search`, then copy this plan file into `docs\plans\2026-07-13-session-search-plan.md` (it is committed in this task's commit below).
- [ ] **Write the failing test.** Create `tests\LocalScribe.Core.Tests\SearchIndexStoreTests.cs` (global namespace, per Core.Tests convention):
```csharp
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

public sealed class SearchIndexStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    public SearchIndexStoreTests() => _paths = new StoragePaths(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static SearchSessionEntry Entry(string id) => new()
    {
        SessionId = id, Title = "Client call", MatterIds = new[] { "M-1" },
        StartedAtUtc = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
        UtcOffsetMinutes = 480, App = "Webex", Participants = new[] { "Sam", "Jane" },
        VersionId = "v1",
        Stamps = new SearchFreshnessStamps
        { TranscriptTicks = 111, EditsTicks = 222, SpeakersTicks = 0, MetaTicks = 333 },
        Lines = new[] { new SearchLine(0, 0, 1500, "we spoke to ACME Corp", "we spoke to acme", "Sam") },
    };

    [Fact]
    public async Task Save_then_load_round_trips_under_the_index_folder()
    {
        var store = new SearchIndexStore(_paths);
        await store.SaveAsync(new SearchIndexCache { Sessions = new[] { Entry("s-1") } }, CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "index", "search-index.json"), _paths.SearchIndexJson);
        Assert.True(File.Exists(_paths.SearchIndexJson));

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        var entry = Assert.Single(loaded!.Sessions);
        Assert.Equal("s-1", entry.SessionId);
        Assert.Equal("Client call", entry.Title);
        Assert.Equal(new[] { "M-1" }, entry.MatterIds);
        Assert.Equal(480, entry.UtcOffsetMinutes);
        Assert.Equal("Webex", entry.App);
        Assert.Equal(new[] { "Sam", "Jane" }, entry.Participants);
        Assert.Equal("v1", entry.VersionId);
        Assert.Equal(111L, entry.Stamps.TranscriptTicks);
        Assert.Equal(0L, entry.Stamps.SpeakersTicks);
        var line = Assert.Single(entry.Lines);
        Assert.Equal(0, line.Seq);
        Assert.Equal(1500L, line.StartMs);
        Assert.Equal("we spoke to ACME Corp", line.Text);
        Assert.Equal("we spoke to acme", line.OriginalText);
        Assert.Equal("Sam", line.Speaker);
    }

    [Fact]
    public async Task Missing_corrupt_and_newer_schema_caches_all_load_as_null()
    {
        var store = new SearchIndexStore(_paths);
        Assert.Null(await store.LoadAsync(CancellationToken.None));                 // missing

        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SearchIndexJson)!);
        await File.WriteAllTextAsync(_paths.SearchIndexJson, "{ not json !!!");
        Assert.Null(await store.LoadAsync(CancellationToken.None));                 // corrupt -> silent rebuild

        await File.WriteAllTextAsync(_paths.SearchIndexJson, "{\"schemaVersion\": 99, \"sessions\": []}");
        Assert.Null(await store.LoadAsync(CancellationToken.None));                 // newer schema -> rebuild ours
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SearchIndexStoreTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\` — expected: `error CS0246: The type or namespace name 'LocalScribe.Core.Search' could not be found` (and `SearchIndexStore`/`SearchSessionEntry` unresolved).
- [ ] **Add `SearchIndexJson` to StoragePaths.** In `src\LocalScribe.Core\Storage\StoragePaths.cs`, locate (line anchors WILL have drifted — the retranscription branch adds `VersionDir`/versioned overloads to this class; find by quoted code):
```csharp
    public string MattersIndexJson => Path.Combine(MattersDir, "matters.json");
    public string MatterJson(string matterId) => Path.Combine(MattersDir, matterId, "matter.json");
```
and insert immediately after:
```csharp

    /// <summary>Persisted cross-session search cache (design 2026-07-13 section 2.1): DERIVED,
    /// self-healing, safe to delete - never evidence. Lives under its own index\ folder beside
    /// sessions\ and matters\.</summary>
    public string SearchIndexJson => Path.Combine(Root, "index", "search-index.json");
```
- [ ] **Create the models.** New file `src\LocalScribe.Core\Search\SearchModels.cs`:
```csharp
// src/LocalScribe.Core/Search/SearchModels.cs
namespace LocalScribe.Core.Search;

/// <summary>One indexed transcript line (design 2026-07-13 section 2.1): a projected segment of the
/// session's ACTIVE version, in display order. Text is the displayed corrected text (post
/// vocabulary + edits overlay + split expansion); OriginalText is the machine original, stored ONLY
/// where a human correction (edits.json) made it differ - the "(matches original text)" rule.
/// PartIndex disambiguates split children sharing a Seq. Speaker is the resolved display name
/// (NameResolver output for the row the segment rendered into).</summary>
public sealed record SearchLine(int Seq, int PartIndex, long StartMs, string Text,
    string? OriginalText, string Speaker);

/// <summary>Freshness stamps (design 2.1): last-write ticks of the ACTIVE version's
/// transcript.jsonl / edits.json / speakers.json ("v1" resolves to the session root) plus the root
/// meta.json. 0 = file absent. Value equality (record) is the staleness test; the active-version id
/// is stored beside these on the entry.</summary>
public sealed record SearchFreshnessStamps
{
    public long TranscriptTicks { get; init; }
    public long EditsTicks { get; init; }
    public long SpeakersTicks { get; init; }
    public long MetaTicks { get; init; }
}

/// <summary>One session's index entry: session-level fields (id, title, matter ids, date, source
/// app, participant names, active-version id) + per-line entries + freshness stamps.</summary>
public sealed record SearchSessionEntry
{
    public string SessionId { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<string> MatterIds { get; init; } = [];
    public DateTimeOffset StartedAtUtc { get; init; }
    /// <summary>The session's own recorded offset (null for pre-v3 records) so result cards can
    /// render the same session-local date every other surface shows.</summary>
    public int? UtcOffsetMinutes { get; init; }
    public string App { get; init; } = "";
    /// <summary>Named participants from meta.json - speaker-name matching covers these even when
    /// a participant never has a resolved line (design 2.1: participants + overlay names).</summary>
    public IReadOnlyList<string> Participants { get; init; } = [];
    public string VersionId { get; init; } = "v1";
    public SearchFreshnessStamps Stamps { get; init; } = new();
    public IReadOnlyList<SearchLine> Lines { get; init; } = [];
}

/// <summary>The persisted cache shape at storageRoot\index\search-index.json (SchemaGuard-stamped).</summary>
public sealed record SearchIndexCache
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<SearchSessionEntry> Sessions { get; init; } = [];
}

/// <summary>A query: free text (whitespace-split into AND terms) + optional facets. FromUtc is
/// inclusive, ToUtc exclusive (callers pass day+1 for an inclusive "To" day); App compares
/// case-insensitively against SearchSessionEntry.App.</summary>
public sealed record SearchQuery(string Text, string? MatterId = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, string? App = null);

/// <summary>One snippet-level hit. Line hits carry the line's Seq/PartIndex/StartMs/Speaker and a
/// ±60-char snippet around the first term occurrence; MatchesOriginalOnly marks a hit found only in
/// the machine original of a corrected line (snippet then comes FROM the original). Speaker-name
/// hits (IsSpeakerNameMatch) snippet the speaker's first line; Seq -1 = a named participant with no
/// resolved line (nothing to scroll to).</summary>
public sealed record SearchHit(int Seq, int PartIndex, long StartMs, string Speaker,
    string Snippet, string MatchedTerm, bool MatchesOriginalOnly, bool IsSpeakerNameMatch);

/// <summary>One matched session: its entry, hits in document order (speaker-name hits appended,
/// ordered by name), and HitCount (= Hits.Count) - the primary rank key.</summary>
public sealed record SearchResult(SearchSessionEntry Session, IReadOnlyList<SearchHit> Hits, int HitCount);
```
- [ ] **Create the store.** New file `src\LocalScribe.Core\Search\SearchIndexStore.cs`:
```csharp
// src/LocalScribe.Core/Search/SearchIndexStore.cs
using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Search;

/// <summary>Load/save for the persisted search cache (design 2026-07-13 section 2.1). Writes are
/// atomic (JsonFile -> AtomicFile) and schema-stamped; loads return null for a missing, corrupt,
/// or newer-schema cache so the service does a SILENT full rebuild - the cache is derived data
/// (matters-index self-heal philosophy), never worth an error and never evidence.</summary>
public sealed class SearchIndexStore(StoragePaths paths)
{
    public const int Version = 1;

    public async Task<SearchIndexCache?> LoadAsync(CancellationToken ct)
    {
        try
        {
            var obj = await SchemaGuard.ReadObjectAsync(paths.SearchIndexJson, ct);
            if (obj is null) return null;                                  // missing
            if (SchemaGuard.ReadVersion(obj) > Version) return null;       // newer app wrote it
            return await JsonFile.ReadAsync<SearchIndexCache>(paths.SearchIndexJson, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }                                             // corrupt -> silent rebuild
    }

    public Task SaveAsync(SearchIndexCache cache, CancellationToken ct)
        => JsonFile.WriteAsync(paths.SearchIndexJson, cache with { SchemaVersion = Version }, ct);
}
```
- [ ] **Run tests and see PASS.** Same filter as above — expected: 2 passed. Then run `--filter "FullyQualifiedName~StoragePathsTests"` to confirm the StoragePaths addition broke nothing.
- [ ] **Commit.**
```
git add docs/plans/2026-07-13-session-search-plan.md src/LocalScribe.Core/Storage/StoragePaths.cs src/LocalScribe.Core/Search/SearchModels.cs src/LocalScribe.Core/Search/SearchIndexStore.cs tests/LocalScribe.Core.Tests/SearchIndexStoreTests.cs
git commit -m "feat(core): search-index models + schema-stamped persisted cache store

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Core — `SearchQueryEngine` (pure query semantics + snippets)
**Files:**
- New `src\LocalScribe.Core\Search\SearchQueryEngine.cs`.
- Test (new) `tests\LocalScribe.Core.Tests\SearchQueryEngineTests.cs`.

**Interfaces:**
- Consumes: Task 1's records (`SearchSessionEntry`, `SearchLine`, `SearchQuery`, `SearchHit`, `SearchResult`).
- Produces:
```csharp
public static class SearchQueryEngine
{
    public const int SnippetRadius = 60;
    public static IReadOnlyList<SearchResult> Run(IEnumerable<SearchSessionEntry> sessions, SearchQuery query);
    public static string Snippet(string text, int matchIndex, int matchLength);   // ±60 chars, "…" ellipses
}
```

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\SearchQueryEngineTests.cs`:
```csharp
using LocalScribe.Core.Search;

public class SearchQueryEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);

    private static SearchSessionEntry Session(string id, DateTimeOffset started, SearchLine[] lines,
        string app = "Webex", string[]? matterIds = null, string[]? participants = null)
        => new()
        {
            SessionId = id, Title = "T-" + id, MatterIds = matterIds ?? [],
            StartedAtUtc = started, App = app, Participants = participants ?? [],
            VersionId = "v1", Lines = lines,
        };

    [Fact]
    public void Empty_or_whitespace_query_returns_nothing()
    {
        var s = Session("s-1", T0, [new SearchLine(0, 0, 0, "anything", null, "Sam")]);
        Assert.Empty(SearchQueryEngine.Run([s], new SearchQuery("")));
        Assert.Empty(SearchQueryEngine.Run([s], new SearchQuery("   ")));
    }

    [Fact]
    public void Single_term_matches_case_insensitively_over_corrected_text()
    {
        var s = Session("s-1", T0,
            [new SearchLine(4, 0, 61_000, "We discussed the RETAINER agreement.", null, "Sam")]);
        var r = Assert.Single(SearchQueryEngine.Run([s], new SearchQuery("retainer")));
        Assert.Equal("s-1", r.Session.SessionId);
        Assert.Equal(1, r.HitCount);
        var hit = Assert.Single(r.Hits);
        Assert.Equal(4, hit.Seq);
        Assert.Equal(61_000L, hit.StartMs);
        Assert.Equal("Sam", hit.Speaker);
        Assert.False(hit.MatchesOriginalOnly);
        Assert.False(hit.IsSpeakerNameMatch);
        Assert.Contains("RETAINER", hit.Snippet);
        Assert.Equal("retainer", hit.MatchedTerm);
    }

    [Fact]
    public void Multi_word_query_is_AND_across_the_session()
    {
        var both = Session("s-both", T0, [
            new SearchLine(0, 0, 0, "the retainer was signed", null, "Sam"),
            new SearchLine(1, 0, 5_000, "the hearing is on Monday", null, "Jane"),
        ]);
        var onlyOne = Session("s-one", T0.AddHours(1),
            [new SearchLine(0, 0, 0, "the retainer was signed", null, "Sam")]);
        var r = Assert.Single(SearchQueryEngine.Run([both, onlyOne], new SearchQuery("retainer hearing")));
        Assert.Equal("s-both", r.Session.SessionId);
        Assert.Equal(2, r.Hits.Count);                      // one hit per matched line, document order
        Assert.Equal(0, r.Hits[0].Seq);
        Assert.Equal(1, r.Hits[1].Seq);
    }

    [Fact]
    public void Results_rank_by_hit_count_then_recency()
    {
        var twoHitsOld = Session("s-old2", T0, [
            new SearchLine(0, 0, 0, "acme called", null, "Sam"),
            new SearchLine(1, 0, 9_000, "acme called back", null, "Sam"),
        ]);
        var oneHitNew = Session("s-new1", T0.AddDays(2),
            [new SearchLine(0, 0, 0, "acme wrote", null, "Sam")]);
        var oneHitOld = Session("s-old1", T0.AddDays(1),
            [new SearchLine(0, 0, 0, "acme again", null, "Sam")]);
        var results = SearchQueryEngine.Run([oneHitOld, oneHitNew, twoHitsOld], new SearchQuery("acme"));
        Assert.Equal(new[] { "s-old2", "s-new1", "s-old1" },
            results.Select(r => r.Session.SessionId).ToArray());
    }

    [Fact]
    public void Original_text_only_matches_are_labelled_and_snippet_comes_from_the_original()
    {
        var s = Session("s-corr", T0,
            [new SearchLine(1, 0, 2_000, "the corrected words", "the orignal words", "Sam")]);

        var original = Assert.Single(
            Assert.Single(SearchQueryEngine.Run([s], new SearchQuery("orignal"))).Hits);
        Assert.True(original.MatchesOriginalOnly);
        Assert.Contains("orignal", original.Snippet);

        var corrected = Assert.Single(
            Assert.Single(SearchQueryEngine.Run([s], new SearchQuery("corrected"))).Hits);
        Assert.False(corrected.MatchesOriginalOnly);
        Assert.Contains("corrected", corrected.Snippet);
    }

    [Fact]
    public void Speaker_name_only_matches_snippet_the_speakers_first_line()
    {
        var s = Session("s-spk", T0, [
            new SearchLine(0, 0, 0, "good morning", null, "Jane Doe"),
            new SearchLine(2, 0, 7_000, "the first jane line is above", null, "Sam"),
        ], participants: ["Sam", "Jane Doe", "Silent Bob"]);

        // "jane" is in line 2's TEXT and in a speaker name: the text hit wins; no speaker hit added.
        var textHit = Assert.Single(Assert.Single(
            SearchQueryEngine.Run([s], new SearchQuery("jane"))).Hits);
        Assert.False(textHit.IsSpeakerNameMatch);
        Assert.Equal(2, textHit.Seq);

        // "doe" appears ONLY as a speaker name: one speaker hit, snippeted with Jane's first line.
        var spkHit = Assert.Single(Assert.Single(
            SearchQueryEngine.Run([s], new SearchQuery("doe"))).Hits);
        Assert.True(spkHit.IsSpeakerNameMatch);
        Assert.Equal("Jane Doe", spkHit.Speaker);
        Assert.Equal(0, spkHit.Seq);
        Assert.Contains("good morning", spkHit.Snippet);

        // A named participant who never spoke still satisfies the term; Seq -1 = nothing to open to.
        var silent = Assert.Single(Assert.Single(
            SearchQueryEngine.Run([s], new SearchQuery("silent"))).Hits);
        Assert.True(silent.IsSpeakerNameMatch);
        Assert.Equal(-1, silent.Seq);
        Assert.Equal("", silent.Snippet);
    }

    [Fact]
    public void Facets_filter_by_matter_date_range_and_app()
    {
        var a = Session("s-a", T0, [new SearchLine(0, 0, 0, "acme", null, "Sam")],
            app: "Webex", matterIds: ["M-1"]);
        var b = Session("s-b", T0.AddDays(5), [new SearchLine(0, 0, 0, "acme", null, "Sam")],
            app: "Teams", matterIds: ["M-2"]);

        Assert.Equal(new[] { "s-a" }, SearchQueryEngine.Run([a, b],
            new SearchQuery("acme", MatterId: "M-1")).Select(r => r.Session.SessionId).ToArray());
        Assert.Equal(new[] { "s-b" }, SearchQueryEngine.Run([a, b],
            new SearchQuery("acme", App: "teams")).Select(r => r.Session.SessionId).ToArray());
        Assert.Equal(new[] { "s-b" }, SearchQueryEngine.Run([a, b],
            new SearchQuery("acme", FromUtc: T0.AddDays(1), ToUtc: T0.AddDays(6)))
            .Select(r => r.Session.SessionId).ToArray());
        Assert.Empty(SearchQueryEngine.Run([a, b], new SearchQuery("acme", ToUtc: T0)));   // exclusive upper
    }

    [Fact]
    public void Snippet_is_60_chars_around_the_match_with_ellipses()
    {
        string text = new string('x', 70) + "needle" + new string('y', 70);
        string s = SearchQueryEngine.Snippet(text, 70, 6);
        Assert.StartsWith("…", s);
        Assert.EndsWith("…", s);
        Assert.Contains("needle", s);
        Assert.Equal(1 + 60 + 6 + 60 + 1, s.Length);        // ellipsis + radius + match + radius + ellipsis

        Assert.Equal("hello needle world", SearchQueryEngine.Snippet("hello needle world", 6, 6));
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SearchQueryEngineTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\` — expected: `error CS0103: The name 'SearchQueryEngine' does not exist in the current context`.
- [ ] **Implement the engine.** New file `src\LocalScribe.Core\Search\SearchQueryEngine.cs`:
```csharp
// src/LocalScribe.Core/Search/SearchQueryEngine.cs
namespace LocalScribe.Core.Search;

/// <summary>Pure query semantics (design 2026-07-13 section 2.1): case-insensitive substring over
/// corrected text, original machine text, and speaker names (line speakers + session participants);
/// multiple words = AND across a session; ranked by hit count, then recency, then id (deterministic
/// tail). One hit per matched LINE (the earliest term occurrence chooses the snippet); a term found
/// nowhere in any line falls back to speaker names - a term satisfied by NEITHER fails the whole
/// session (AND). Matches found only in original machine text are labelled (MatchesOriginalOnly) -
/// corrections never hide content from search. No IO, no mutation.</summary>
public static class SearchQueryEngine
{
    public const int SnippetRadius = 60;

    public static IReadOnlyList<SearchResult> Run(IEnumerable<SearchSessionEntry> sessions, SearchQuery query)
    {
        string[] terms = (query.Text ?? "").Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];

        var results = new List<SearchResult>();
        foreach (var s in sessions)
        {
            if (!PassesFacets(s, query)) continue;
            var hits = MatchSession(s, terms);
            if (hits is not null) results.Add(new SearchResult(s, hits, hits.Count));
        }
        return results
            .OrderByDescending(r => r.HitCount)
            .ThenByDescending(r => r.Session.StartedAtUtc)
            .ThenBy(r => r.Session.SessionId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>±SnippetRadius chars around [matchIndex, matchIndex+matchLength); "…" marks
    /// truncation at either end (design 2.1: ±60 chars around the first hit in a line).</summary>
    public static string Snippet(string text, int matchIndex, int matchLength)
    {
        int start = Math.Max(0, matchIndex - SnippetRadius);
        int end = Math.Min(text.Length, matchIndex + matchLength + SnippetRadius);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }

    private static bool PassesFacets(SearchSessionEntry s, SearchQuery q)
    {
        if (q.MatterId is { } m && !s.MatterIds.Contains(m, StringComparer.Ordinal)) return false;
        if (q.FromUtc is { } from && s.StartedAtUtc < from) return false;
        if (q.ToUtc is { } to && s.StartedAtUtc >= to) return false;      // exclusive upper bound
        if (q.App is { } app && !string.Equals(s.App, app, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Null when any term is satisfied by neither line text nor a speaker name (AND).</summary>
    private static IReadOnlyList<SearchHit>? MatchSession(SearchSessionEntry s, string[] terms)
    {
        var satisfiedByText = new bool[terms.Length];
        var hits = new List<SearchHit>();

        foreach (var line in s.Lines)
        {
            // Earliest occurrence across terms picks the snippet; corrected text beats original.
            int firstTextIdx = -1, firstTextLen = 0; string firstTextTerm = "";
            int firstOrigIdx = -1, firstOrigLen = 0; string firstOrigTerm = "";
            for (int i = 0; i < terms.Length; i++)
            {
                int ti = line.Text.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase);
                if (ti >= 0)
                {
                    satisfiedByText[i] = true;
                    if (firstTextIdx < 0 || ti < firstTextIdx)
                    { firstTextIdx = ti; firstTextLen = terms[i].Length; firstTextTerm = terms[i]; }
                }
                int oi = line.OriginalText?.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) ?? -1;
                if (oi >= 0)
                {
                    satisfiedByText[i] = true;
                    if (firstOrigIdx < 0 || oi < firstOrigIdx)
                    { firstOrigIdx = oi; firstOrigLen = terms[i].Length; firstOrigTerm = terms[i]; }
                }
            }
            if (firstTextIdx >= 0)
                hits.Add(new SearchHit(line.Seq, line.PartIndex, line.StartMs, line.Speaker,
                    Snippet(line.Text, firstTextIdx, firstTextLen), firstTextTerm,
                    MatchesOriginalOnly: false, IsSpeakerNameMatch: false));
            else if (firstOrigIdx >= 0)
                hits.Add(new SearchHit(line.Seq, line.PartIndex, line.StartMs, line.Speaker,
                    Snippet(line.OriginalText!, firstOrigIdx, firstOrigLen), firstOrigTerm,
                    MatchesOriginalOnly: true, IsSpeakerNameMatch: false));
        }

        // Terms unmatched by any line fall back to speaker names (line speakers + participants).
        // "Speaker-name-only" (design 2.1): the term matched no line text at all in this session.
        var speakerNames = s.Lines.Select(l => l.Speaker)
            .Concat(s.Participants)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        for (int i = 0; i < terms.Length; i++)
        {
            if (satisfiedByText[i]) continue;
            bool any = false;
            foreach (string name in speakerNames)
            {
                if (name.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) < 0) continue;
                any = true;
                var firstLine = s.Lines.FirstOrDefault(
                    l => string.Equals(l.Speaker, name, StringComparison.Ordinal));
                hits.Add(firstLine is not null
                    ? new SearchHit(firstLine.Seq, firstLine.PartIndex, firstLine.StartMs, name,
                        Snippet(firstLine.Text, 0, 0), terms[i],
                        MatchesOriginalOnly: false, IsSpeakerNameMatch: true)
                    : new SearchHit(-1, 0, 0, name, "", terms[i],
                        MatchesOriginalOnly: false, IsSpeakerNameMatch: true));
            }
            if (!any) return null;                            // this term matched nothing -> AND fails
        }
        return hits;
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 8 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Search/SearchQueryEngine.cs tests/LocalScribe.Core.Tests/SearchQueryEngineTests.cs
git commit -m "feat(core): SearchQueryEngine - AND terms, ranking, snippets, original-text labelling, speaker fallback

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Core — `SearchIndexBuilder` (entry derivation + freshness stamps)
**Files:**
- New `src\LocalScribe.Core\Search\SearchIndexBuilder.cs`.
- Test (new) `tests\LocalScribe.Core.Tests\SearchIndexBuilderTests.cs`.

**Interfaces:**
- Consumes: `SessionProjectionLoader.LoadAsync` (unchanged 5-arg signature; follows `ActiveVersion`; `LoadedProjection.VersionId` is the cross-branch trailing property), `DisplayRow`/`RowSegment` (`ProjectedText`, `RawText`, `IsCorrected`, `PartIndex`), `StoragePaths` versioned path overloads (`TranscriptJsonl(id, versionId)` etc. — "v1" resolves to the session root), `SessionMeta.Participants`.
- Produces:
```csharp
public static class SearchIndexBuilder
{
    public static Task<SearchSessionEntry> BuildEntryAsync(StoragePaths paths, Settings settings,
        TimeProvider time, string sessionId, CancellationToken ct);          // throws on unreadable
    public static SearchFreshnessStamps ComputeStamps(StoragePaths paths, string sessionId, string versionId);
}
```

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\SearchIndexBuilderTests.cs`:
```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

public sealed class SearchIndexBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
    public SearchIndexBuilderTests() => _paths = new StoragePaths(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static readonly DateTimeOffset Started = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Finalized Webex session at UTC+8: two Local segments (seq 1 corrected via the
    /// EditStore overlay), one Remote segment, one marker; named participants Sam/Jane.</summary>
    private async Task SeedAsync(string id)
    {
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = Started, EndedAtUtc = Started.AddMinutes(10),
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480, DurationMs = 600_000,
            Model = "small.en", Backend = "cuda", Language = "en",
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "Client call", MatterIds = new[] { "M-2026-001" },
            Participants = new[]
            {
                new SessionParticipant { Id = "p1", Name = "Sam", Side = SourceKind.Local, IsSelf = true },
                new SessionParticipant { Id = "p2", Name = "Jane", Side = SourceKind.Remote },
            },
        }, default);
        var t = new TranscriptStore(_paths.TranscriptJsonl(id));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1500,
            "we spoke to the client this morning", "Me"), default);
        await t.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 1600, 3000,
            "the orignal words", "Me"), default);
        await t.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Remote, 3200, 4200,
            "sounds good", "Them"), default);
        await t.AppendAsync(TranscriptLine.Marker(3, 4200, Markers.AudioDeviceChanged), default);
        await new EditStore(_paths.SessionDir(id), _time)
            .ApplyTextCorrectionAsync(1, "the corrected words", default);
    }

    [Fact]
    public async Task Entry_derives_corrected_text_original_only_where_corrected_and_skips_markers()
    {
        await SeedAsync("s-1");
        var entry = await SearchIndexBuilder.BuildEntryAsync(_paths, new Settings(), _time, "s-1", default);

        Assert.Equal("s-1", entry.SessionId);
        Assert.Equal("Client call", entry.Title);
        Assert.Equal(new[] { "M-2026-001" }, entry.MatterIds);
        Assert.Equal(Started, entry.StartedAtUtc);
        Assert.Equal(480, entry.UtcOffsetMinutes);
        Assert.Equal("Webex", entry.App);
        Assert.Equal(new[] { "Sam", "Jane" }, entry.Participants);
        Assert.Equal("v1", entry.VersionId);

        Assert.Equal(3, entry.Lines.Count);                              // marker (seq 3) excluded
        var l0 = entry.Lines[0];
        Assert.Equal(0, l0.Seq);
        Assert.Equal("we spoke to the client this morning", l0.Text);
        Assert.Null(l0.OriginalText);                                    // uncorrected -> no original stored
        Assert.Equal("Sam", l0.Speaker);                                 // lone named Local participant
        var l1 = entry.Lines[1];
        Assert.Equal(1, l1.Seq);
        Assert.Equal("the corrected words", l1.Text);
        Assert.Equal("the orignal words", l1.OriginalText);              // stored only where a correction differs
        var l2 = entry.Lines[2];
        Assert.Equal("Jane", l2.Speaker);
        Assert.Equal(3200L, l2.StartMs);
    }

    [Fact]
    public async Task Stamps_are_last_write_ticks_of_the_stamped_files_with_zero_for_absent()
    {
        await SeedAsync("s-2");
        var entry = await SearchIndexBuilder.BuildEntryAsync(_paths, new Settings(), _time, "s-2", default);
        Assert.Equal(File.GetLastWriteTimeUtc(_paths.TranscriptJsonl("s-2")).Ticks, entry.Stamps.TranscriptTicks);
        Assert.Equal(File.GetLastWriteTimeUtc(_paths.EditsJson("s-2")).Ticks, entry.Stamps.EditsTicks);
        Assert.Equal(0L, entry.Stamps.SpeakersTicks);                    // speakers.json absent -> 0
        Assert.Equal(File.GetLastWriteTimeUtc(_paths.MetaJson("s-2")).Ticks, entry.Stamps.MetaTicks);
        Assert.Equal(entry.Stamps, SearchIndexBuilder.ComputeStamps(_paths, "s-2", "v1"));
    }

    [Fact]
    public async Task Split_children_index_as_separate_lines_and_are_not_labelled_corrections()
    {
        await SeedAsync("s-3");
        await new EditStore(_paths.SessionDir("s-3"), _time).ApplySplitAsync(0, TranscriptSource.Local,
            new[]
            {
                new SplitPart { Text = "we spoke to the client", StartMs = 0 },
                new SplitPart { Text = "this morning", StartMs = 700, DerivedStart = true },
            }, default);
        var entry = await SearchIndexBuilder.BuildEntryAsync(_paths, new Settings(), _time, "s-3", default);
        var parts = entry.Lines.Where(l => l.Seq == 0).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal(0, parts[0].PartIndex);
        Assert.Equal("we spoke to the client", parts[0].Text);
        Assert.Equal(1, parts[1].PartIndex);
        Assert.Equal(700L, parts[1].StartMs);
        Assert.Equal("this morning", parts[1].Text);
        Assert.All(parts, p => Assert.Null(p.OriginalText));             // a split is not a correction
    }

    [Fact]
    public async Task Entry_follows_the_active_version()
    {
        // Cross-branch surface (feat/retranscription-versions): ActiveVersion + versioned paths.
        // If that branch's version-id format differs from the "v2-<model>-<date>" folder id used
        // here, substitute ITS id format - the assertions only require that the ACTIVE version's
        // transcript is what gets indexed and stamped.
        await SeedAsync("s-4");
        const string v2 = "v2-small.en-2026-07-13";
        var store = new SessionStore(_paths.SessionJson("s-4"));
        var record = await store.ReadAsync(default);
        await store.SaveAsync(record! with
        {
            ActiveVersion = v2,
            Versions = new[]
            {
                new TranscriptVersion
                {
                    Id = v2, Model = "small.en", Backend = "cuda", Language = "en",
                    CreatedAtUtc = Started.AddDays(1),
                },
            },
        }, default);
        var t2 = new TranscriptStore(_paths.TranscriptJsonl("s-4", v2));
        await t2.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 900,
            "completely retranscribed text", "Me"), default);

        var entry = await SearchIndexBuilder.BuildEntryAsync(_paths, new Settings(), _time, "s-4", default);
        Assert.Equal(v2, entry.VersionId);
        var line = Assert.Single(entry.Lines);
        Assert.Equal("completely retranscribed text", line.Text);
        Assert.Equal(File.GetLastWriteTimeUtc(_paths.TranscriptJsonl("s-4", v2)).Ticks,
            entry.Stamps.TranscriptTicks);
        Assert.Equal(0L, entry.Stamps.EditsTicks);                       // v2 starts with no edits overlay
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SearchIndexBuilderTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\` — expected: `error CS0103: The name 'SearchIndexBuilder' does not exist in the current context`.
- [ ] **Implement the builder.** New file `src\LocalScribe.Core\Search\SearchIndexBuilder.cs`:
```csharp
// src/LocalScribe.Core/Search/SearchIndexBuilder.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Search;

/// <summary>Derives one session's index entry through the SAME load pipeline the read view and the
/// exporters use (SessionProjectionLoader), so the indexed text is exactly the displayed corrected
/// text (vocabulary + edits overlay + split expansion) and the entry follows the session's ACTIVE
/// version automatically (design 2026-07-13 section 2.1). Marker lines are excluded (marker-text
/// search is a design section 1 non-goal). OriginalText is stored only where a HUMAN correction
/// (edits.json, RowSegment.IsCorrected) made it differ - the vocabulary pass alone does not store
/// an original, matching the spec's "where a correction differs" rule. Read-only: throws on an
/// unreadable session; the caller (SearchIndexService) skips + logs it, never blocking others.</summary>
public static class SearchIndexBuilder
{
    public static async Task<SearchSessionEntry> BuildEntryAsync(StoragePaths paths, Settings settings,
        TimeProvider time, string sessionId, CancellationToken ct)
    {
        var loaded = await SessionProjectionLoader.LoadAsync(paths, settings, time, sessionId, ct);
        var lines = new List<SearchLine>();
        foreach (var row in loaded.Rows)
        {
            if (row.IsMarker) continue;                       // marker text is out of scope (design 1)
            foreach (var seg in row.Segments)
                lines.Add(new SearchLine(seg.Seq, seg.PartIndex, seg.StartMs,
                    Text: seg.ProjectedText,
                    OriginalText: seg.IsCorrected
                        && !string.Equals(seg.RawText, seg.ProjectedText, StringComparison.Ordinal)
                        ? seg.RawText : null,
                    Speaker: row.DisplayName ?? ""));
        }
        return new SearchSessionEntry
        {
            SessionId = sessionId,
            Title = loaded.Meta.Title,
            MatterIds = loaded.Meta.MatterIds,
            StartedAtUtc = loaded.Session.StartedAtUtc,
            UtcOffsetMinutes = loaded.Session.UtcOffsetMinutes,
            App = loaded.Session.App.ToString(),
            Participants = loaded.Meta.Participants
                .Where(p => !string.IsNullOrEmpty(p.Name)).Select(p => p.Name).ToList(),
            VersionId = loaded.VersionId,
            Stamps = ComputeStamps(paths, sessionId, loaded.VersionId),
            Lines = lines,
        };
    }

    /// <summary>Freshness stamps (design 2.1): last-write ticks of the ACTIVE version's
    /// transcript.jsonl / edits.json / speakers.json ("v1" resolves to the session root via the
    /// versioned StoragePaths overloads) plus the root meta.json. 0 = file absent.</summary>
    public static SearchFreshnessStamps ComputeStamps(StoragePaths paths, string sessionId, string versionId)
        => new()
        {
            TranscriptTicks = LastWriteTicks(paths.TranscriptJsonl(sessionId, versionId)),
            EditsTicks = LastWriteTicks(paths.EditsJson(sessionId, versionId)),
            SpeakersTicks = LastWriteTicks(paths.SpeakersJson(sessionId, versionId)),
            MetaTicks = LastWriteTicks(paths.MetaJson(sessionId)),
        };

    private static long LastWriteTicks(string path)
        => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed. (If `Entry_follows_the_active_version` fails on the version-id FORMAT only, adapt the `v2` constant to the merged branch's actual id format per the in-test comment — the behavioral assertions stay.)
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Search/SearchIndexBuilder.cs tests/LocalScribe.Core.Tests/SearchIndexBuilderTests.cs
git commit -m "feat(core): SearchIndexBuilder derives per-session entries via SessionProjectionLoader + freshness stamps

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Core — `SearchIndexService` (self-healing init, incremental re-index, debounced persist)
**Files:**
- New `src\LocalScribe.Core\Search\SearchIndexService.cs`.
- Test (new) `tests\LocalScribe.Core.Tests\SearchIndexServiceTests.cs`.

**Interfaces:**
- Consumes: `SearchIndexStore` (Task 1), `SearchIndexBuilder` (Task 3), `SearchQueryEngine` (Task 2), `SessionStore.ReadAsync(selfForMigration: null, ct)` (cheap ActiveVersion read, no identity fabrication — SessionCatalog's rule), `StoragePaths.SessionsDir/SessionDir`, `SessionRecord.ActiveVersion` (cross-branch).
- Produces:
```csharp
public sealed class SearchIndexService
{
    public SearchIndexService(StoragePaths paths, Func<Settings> settings, TimeProvider time,
        int saveDebounceMs = 2000);
    public bool IsReady { get; }
    public event Action? ReadyChanged;                       // fired once InitializeAsync completes
    public event Action<string, Exception>? SessionSkipped;  // unreadable session: skipped + logged
    public Task InitializeAsync(CancellationToken ct);       // load cache + self-heal + persist if changed
    public Task ReindexSessionAsync(string sessionId, CancellationToken ct);   // catches everything
    public Task FlushAsync(CancellationToken ct);            // force the debounced cache rewrite
    public IReadOnlyList<SearchResult> Query(SearchQuery query);   // thread-safe; empty when not ready
}
```

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\SearchIndexServiceTests.cs`:
```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

public sealed class SearchIndexServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    public SearchIndexServiceTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private SearchIndexService MakeService()
        => new(_paths, () => new Settings(), TimeProvider.System, saveDebounceMs: 0);

    private async Task SeedSessionAsync(string id, string text, DateTimeOffset? started = null)
    {
        var t0 = started ?? new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = t0, EndedAtUtc = t0.AddMinutes(5),
            DurationMs = 300_000,
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta { Title = "T-" + id }, default);
        await new TranscriptStore(_paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, text, "Me"), default);
    }

    [Fact]
    public async Task Initialize_builds_from_disk_persists_the_cache_and_flips_ready()
    {
        await SeedSessionAsync("s-1", "the quick brown fox");
        await SeedSessionAsync("s-2", "totally different words");
        var svc = MakeService();
        bool readyFired = false;
        svc.ReadyChanged += () => readyFired = true;
        Assert.False(svc.IsReady);
        Assert.Empty(svc.Query(new SearchQuery("fox")));                  // not ready -> empty, never throws

        await svc.InitializeAsync(CancellationToken.None);

        Assert.True(svc.IsReady);
        Assert.True(readyFired);
        var r = Assert.Single(svc.Query(new SearchQuery("fox")));
        Assert.Equal("s-1", r.Session.SessionId);
        Assert.True(File.Exists(_paths.SearchIndexJson));                 // cache persisted
    }

    [Fact]
    public async Task Initialize_reuses_fresh_cache_entries_and_rederives_stale_ones()
    {
        await SeedSessionAsync("s-1", "the quick brown fox");
        var first = MakeService();
        await first.InitializeAsync(CancellationToken.None);

        // Tamper the CACHE only (stamps stay fresh): a second Initialize must trust it - the
        // observable proof that fresh entries are reused without re-deriving from session files.
        var store = new SearchIndexStore(_paths);
        var cache = await store.LoadAsync(CancellationToken.None);
        await store.SaveAsync(cache! with
        {
            Sessions = cache.Sessions.Select(s => s with { Title = "TAMPERED" }).ToList(),
        }, CancellationToken.None);
        var second = MakeService();
        await second.InitializeAsync(CancellationToken.None);
        Assert.Equal("TAMPERED",
            Assert.Single(second.Query(new SearchQuery("fox"))).Session.Title);

        // Touch meta.json (what any real edit does): the stamp mismatch makes the entry stale, so
        // the next Initialize re-derives from disk truth and the tampering vanishes.
        await new MetadataStore(_paths.MetaJson("s-1"))
            .SaveAsync(new SessionMeta { Title = "Real title" }, default);
        var third = MakeService();
        await third.InitializeAsync(CancellationToken.None);
        Assert.Equal("Real title",
            Assert.Single(third.Query(new SearchQuery("fox"))).Session.Title);
    }

    [Fact]
    public async Task Initialize_drops_orphans_skips_unreadable_sessions_and_rebuilds_a_corrupt_cache()
    {
        await SeedSessionAsync("s-ok", "indexable content");
        Directory.CreateDirectory(_paths.SessionDir("s-bad"));            // unreadable session.json
        await File.WriteAllTextAsync(_paths.SessionJson("s-bad"), "{ not json");
        await new SearchIndexStore(_paths).SaveAsync(new SearchIndexCache  // orphan cache entry
        {
            Sessions = new[]
            {
                new SearchSessionEntry
                {
                    SessionId = "s-gone", Title = "Ghost",
                    Lines = new[] { new SearchLine(0, 0, 0, "ghost content", null, "Sam") },
                },
            },
        }, CancellationToken.None);

        var svc = MakeService();
        var skipped = new List<string>();
        svc.SessionSkipped += (id, _) => skipped.Add(id);
        await svc.InitializeAsync(CancellationToken.None);

        Assert.Single(svc.Query(new SearchQuery("indexable")));           // healthy session indexed
        Assert.Empty(svc.Query(new SearchQuery("ghost")));                // orphan dropped
        Assert.Equal(new[] { "s-bad" }, skipped.ToArray());               // unreadable: skipped + logged

        await File.WriteAllTextAsync(_paths.SearchIndexJson, "!!!! not json");
        var rebuilt = MakeService();
        await rebuilt.InitializeAsync(CancellationToken.None);            // corrupt cache: silent rebuild
        Assert.Single(rebuilt.Query(new SearchQuery("indexable")));
    }

    [Fact]
    public async Task Reindex_updates_removes_and_flush_persists()
    {
        await SeedSessionAsync("s-1", "first words");
        var svc = MakeService();
        await svc.InitializeAsync(CancellationToken.None);
        Assert.Empty(svc.Query(new SearchQuery("appended")));

        await new TranscriptStore(_paths.TranscriptJsonl("s-1")).AppendAsync(
            TranscriptLine.Segment(1, TranscriptSource.Local, 2000, 3000,
                "appended after finalize", "Me"), default);
        await svc.ReindexSessionAsync("s-1", CancellationToken.None);
        Assert.Single(svc.Query(new SearchQuery("appended")));

        Directory.Delete(_paths.SessionDir("s-1"), recursive: true);
        await svc.ReindexSessionAsync("s-1", CancellationToken.None);
        Assert.Empty(svc.Query(new SearchQuery("appended")));             // dropped from memory

        await svc.FlushAsync(CancellationToken.None);                     // force the debounced rewrite
        var persisted = await new SearchIndexStore(_paths).LoadAsync(CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Empty(persisted!.Sessions);                                // removal persisted
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SearchIndexServiceTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\` — expected: `error CS0246: The type or namespace name 'SearchIndexService' could not be found`.
- [ ] **Implement the service.** New file `src\LocalScribe.Core\Search\SearchIndexService.cs`:
```csharp
// src/LocalScribe.Core/Search/SearchIndexService.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Search;

/// <summary>The in-memory cross-session search index (design 2026-07-13 section 2.1). Owns one
/// entry per session, seeded by InitializeAsync from the persisted cache with per-session
/// self-healing (fresh stamps -> entry reused; stale/missing -> re-derived via SessionProjection-
/// Loader; orphans dropped; unreadable sessions skipped + surfaced on SessionSkipped, never
/// blocking others; corrupt/newer cache -> silent full rebuild). Incremental updates ride
/// ReindexSessionAsync (the App wires the live-update seams to it); cache rewrites are debounced
/// (saveDebounceMs, tests pass 0) and forceable via FlushAsync. Query is pure and thread-safe over
/// a snapshot. READ-ONLY over session folders - the sole write target is the derived cache file.</summary>
public sealed class SearchIndexService
{
    private readonly StoragePaths _paths;
    private readonly Func<Settings> _settings;
    private readonly TimeProvider _time;
    private readonly int _saveDebounceMs;
    private readonly SearchIndexStore _store;
    private readonly object _lock = new();                       // guards _entries + _pendingSaveCts
    private readonly Dictionary<string, SearchSessionEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reindexGate = new(1, 1);     // serializes disk-deriving work
    private readonly SemaphoreSlim _saveGate = new(1, 1);        // serializes cache writes
    private CancellationTokenSource? _pendingSaveCts;
    private volatile bool _isReady;

    public SearchIndexService(StoragePaths paths, Func<Settings> settings, TimeProvider time,
        int saveDebounceMs = 2000)
    {
        (_paths, _settings, _time, _saveDebounceMs) = (paths, settings, time, saveDebounceMs);
        _store = new SearchIndexStore(paths);
    }

    /// <summary>False until InitializeAsync completes - the App surfaces this as "indexing...".</summary>
    public bool IsReady => _isReady;

    /// <summary>Fired (once) when IsReady flips true. May fire on a background thread.</summary>
    public event Action? ReadyChanged;

    /// <summary>An unreadable session was skipped (design 2.3) - the host logs it; never thrown,
    /// never blocking other sessions' results.</summary>
    public event Action<string, Exception>? SessionSkipped;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _reindexGate.WaitAsync(ct);
        try
        {
            var cache = await _store.LoadAsync(ct);              // null = missing/corrupt/newer
            var cached = (cache?.Sessions ?? [])
                .ToDictionary(s => s.SessionId, StringComparer.Ordinal);
            var fresh = new Dictionary<string, SearchSessionEntry>(StringComparer.Ordinal);
            bool changed = cache is null;
            if (Directory.Exists(_paths.SessionsDir))
            {
                foreach (string dir in Directory.EnumerateDirectories(_paths.SessionsDir))
                {
                    ct.ThrowIfCancellationRequested();
                    string id = Path.GetFileName(dir);
                    var entry = await DeriveIfStaleAsync(id, cached.GetValueOrDefault(id), ct);
                    if (entry is null) { changed |= cached.ContainsKey(id); continue; }   // skipped
                    fresh[id] = entry;
                    changed |= !ReferenceEquals(entry, cached.GetValueOrDefault(id));
                }
            }
            changed |= cached.Keys.Any(k => !fresh.ContainsKey(k));   // orphans dropped
            lock (_lock)
            {
                _entries.Clear();
                foreach (var kv in fresh) _entries[kv.Key] = kv.Value;
            }
            _isReady = true;
            try { ReadyChanged?.Invoke(); } catch { }
            if (changed) await SaveNowAsync(ct);
        }
        finally { _reindexGate.Release(); }
    }

    /// <summary>Single-session incremental re-index on the live-update seams (finalize, edit save,
    /// re-render, re-transcribe/version switch, import, recovery, delete). Re-derives uncondition-
    /// ally (an event means something changed); a gone/unreadable session drops out of the index.
    /// Fire-and-forget safe: catches everything (failures surface on SessionSkipped only).</summary>
    public async Task ReindexSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await _reindexGate.WaitAsync(ct);
            try
            {
                var entry = Directory.Exists(_paths.SessionDir(sessionId))
                    ? await DeriveIfStaleAsync(sessionId, cachedEntry: null, ct)   // null cache -> force derive
                    : null;
                lock (_lock)
                {
                    if (entry is null) _entries.Remove(sessionId);
                    else _entries[sessionId] = entry;
                }
                ScheduleSave();
            }
            finally { _reindexGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { try { SessionSkipped?.Invoke(sessionId, ex); } catch { } }
    }

    /// <summary>Pure in-memory query over a snapshot (SearchQueryEngine semantics). Safe from any
    /// thread; before InitializeAsync completes the index is simply empty.</summary>
    public IReadOnlyList<SearchResult> Query(SearchQuery query)
    {
        List<SearchSessionEntry> snapshot;
        lock (_lock) snapshot = _entries.Values.ToList();
        return SearchQueryEngine.Run(snapshot, query);
    }

    /// <summary>Cancels any pending debounced save and writes the cache NOW (tests; also usable at
    /// shutdown). Idempotent - writing an unchanged snapshot is harmless (derived data).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        lock (_lock) { _pendingSaveCts?.Cancel(); _pendingSaveCts = null; }
        await SaveNowAsync(ct);
    }

    /// <summary>Reuses the cached entry when its freshness stamps AND active-version id still match
    /// disk; re-derives otherwise. Null = the session is unreadable right now (skipped + logged) or
    /// has no session.json. The record read never fabricates identity (selfForMigration: null,
    /// SessionCatalog's rule).</summary>
    private async Task<SearchSessionEntry?> DeriveIfStaleAsync(string id, SearchSessionEntry? cachedEntry,
        CancellationToken ct)
    {
        try
        {
            var record = await new SessionStore(_paths.SessionJson(id))
                .ReadAsync(selfForMigration: null, ct);
            if (record is null) return null;                              // no session.json: not indexable
            string versionId = record.ActiveVersion;
            var stamps = SearchIndexBuilder.ComputeStamps(_paths, id, versionId);
            if (cachedEntry is not null && cachedEntry.VersionId == versionId
                && cachedEntry.Stamps == stamps)
                return cachedEntry;                                       // fresh: reuse, no projection load
            return await SearchIndexBuilder.BuildEntryAsync(_paths, _settings(), _time, id, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            try { SessionSkipped?.Invoke(id, ex); } catch { }
            return null;
        }
    }

    /// <summary>Debounced cache rewrite (design 2.1): every schedule supersedes the previous one;
    /// the write itself is serialized and best-effort - a failed cache write is never fatal (the
    /// self-heal rebuilds next launch).</summary>
    private void ScheduleSave()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _pendingSaveCts?.Cancel();
            _pendingSaveCts = cts = new CancellationTokenSource();
        }
        _ = DebouncedSaveAsync(cts.Token);
    }

    private async Task DebouncedSaveAsync(CancellationToken ct)
    {
        try
        {
            if (_saveDebounceMs > 0) await Task.Delay(_saveDebounceMs, ct);
            await SaveNowAsync(ct);
        }
        catch (OperationCanceledException) { }    // superseded by a newer save (or shutdown)
        catch { }                                  // derived cache: best-effort by design
    }

    private async Task SaveNowAsync(CancellationToken ct)
    {
        SearchIndexCache snapshot;
        lock (_lock)
            snapshot = new SearchIndexCache
            {
                Sessions = _entries.Values
                    .OrderBy(e => e.SessionId, StringComparer.Ordinal).ToList(),   // deterministic
            };
        await _saveGate.WaitAsync(ct);
        try { await _store.SaveAsync(snapshot, ct); }
        finally { _saveGate.Release(); }
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed. Then run the whole Core suite to prove nothing regressed: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\` (the 2 known fixture fails are pre-existing).
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Search/SearchIndexService.cs tests/LocalScribe.Core.Tests/SearchIndexServiceTests.cs
git commit -m "feat(core): SearchIndexService - self-healing persisted index + incremental re-index + debounced cache

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: App — `MaintenanceService.SessionContentChanged` seam on every session mutation
**Files:**
- Modify `src\LocalScribe.App\Services\MaintenanceService.cs` (event + raise helper near the top of the class; raise-on-success in `SaveMetaAsync`, `SetArchivedAsync`, `SaveTextCorrectionsAsync`, `SaveTranscriptEditsAsync`, `SaveSpeakerPinsAsync`, `RemoveSpeakerPinsAsync`, `SaveDiarisationAsync` (4-arg), `DeleteSessionAsync`, `RecoverAllAsync`, `RegenerateEachAsync`, and the cross-branch `SetActiveVersionAsync`).
- Test (new) `tests\LocalScribe.App.Tests\MaintenanceServiceContentChangedTests.cs`.

**Interfaces:**
- Consumes: every existing gated write path in `MaintenanceService` (bodies unchanged — only wrapped/annotated), `SetActiveVersionAsync` (cross-branch contract `Task<bool>`).
- Produces: `public event Action<string>? MaintenanceService.SessionContentChanged;` — raised with the session id AFTER a successful gated write, OUTSIDE the per-session gate (so a handler may re-enter `RunForSessionAsync` for the same id); never raised for no-op/skipped writes; a throwing subscriber never faults the caller.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\MaintenanceServiceContentChangedTests.cs`:
```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MaintenanceServiceContentChangedTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-content-changed-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly MaintenanceService _svc;
    private readonly List<string> _raised = [];

    public MaintenanceServiceContentChangedTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
        _svc = new MaintenanceService(_paths, new FakeSettings(new Settings()), new NoopBin(),
            TimeProvider.System);
        _svc.SessionContentChanged += id => { lock (_raised) _raised.Add(id); };
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // Per-file fakes, byte-identical to SessionsPageViewModelTests' so this file compiles standalone.
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

    private async Task SeedAsync(string id, bool ended = true)
    {
        var t0 = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = t0,
            EndedAtUtc = ended ? t0.AddMinutes(5) : null, DurationMs = ended ? 300_000 : 0,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id))
            .SaveAsync(new SessionMeta { Title = id }, CancellationToken.None);
        await new TranscriptStore(_paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "hello world", "Me"),
            CancellationToken.None);
    }

    [Fact]
    public async Task Correction_save_raises_once_and_a_noop_batch_is_silent()
    {
        await SeedAsync("s-1");
        bool changed = await _svc.SaveTextCorrectionsAsync("s-1",
            new Dictionary<int, string> { [0] = "hello corrected world" }, reverts: [],
            CancellationToken.None);
        Assert.True(changed);
        Assert.Equal(new[] { "s-1" }, _raised.ToArray());

        _raised.Clear();
        bool noop = await _svc.SaveTextCorrectionsAsync("s-1",
            new Dictionary<int, string>(), reverts: new[] { 99 }, CancellationToken.None);
        Assert.False(noop);
        Assert.Empty(_raised);                                            // nothing changed -> no re-index
    }

    [Fact]
    public async Task Meta_save_raises_and_a_deleted_session_is_silent()
    {
        await SeedAsync("s-3");
        var meta = await new MetadataStore(_paths.MetaJson("s-3")).LoadAsync(CancellationToken.None);
        await _svc.SaveMetaAsync("s-3", meta! with { Title = "Renamed" }, previousMatterIds: [],
            CancellationToken.None);
        Assert.Equal(new[] { "s-3" }, _raised.ToArray());

        _raised.Clear();
        File.Delete(_paths.SessionJson("s-3"));                           // the delete-race guard path
        await _svc.SaveMetaAsync("s-3", meta with { Title = "Again" }, previousMatterIds: [],
            CancellationToken.None);
        Assert.Empty(_raised);                                            // skipped save -> silent
    }

    [Fact]
    public async Task Archive_flip_and_delete_raise_with_the_session_id()
    {
        await SeedAsync("s-2");
        await _svc.SetArchivedAsync("s-2", archived: true, CancellationToken.None);
        Assert.Equal(new[] { "s-2" }, _raised.ToArray());

        _raised.Clear();
        await _svc.SetArchivedAsync("s-2", archived: true, CancellationToken.None);   // already archived
        Assert.Empty(_raised);                                            // no write -> silent

        await _svc.DeleteSessionAsync("s-2", CancellationToken.None);
        Assert.Equal(new[] { "s-2" }, _raised.ToArray());
    }

    [Fact]
    public async Task Recovery_and_regenerate_raise_per_session()
    {
        await SeedAsync("s-a");
        await SeedAsync("s-crash", ended: false);

        await _svc.RecoverAllAsync(CancellationToken.None);
        Assert.Equal(new[] { "s-crash" }, _raised.ToArray());             // only the recovered one

        _raised.Clear();
        await _svc.RegenerateAllAsync(progress: null, CancellationToken.None);
        Assert.Equal(new[] { "s-a", "s-crash" },
            _raised.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~MaintenanceServiceContentChangedTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\` — expected: `error CS1061: 'MaintenanceService' does not contain a definition for 'SessionContentChanged'`.
- [ ] **Add the event + helper.** In `MaintenanceService.cs`, immediately after the field `private readonly SemaphoreSlim _indexGate = new(1, 1);   // serializes ALL matters.json writes` insert:
```csharp

    /// <summary>Search-index live-update seam (design 2026-07-13 section 2.1): raised with the
    /// session id AFTER any gated write that can change what the session's search entry derives
    /// from - meta save (title/tags/participants), archive flip, corrections, splits, speaker pins,
    /// diarisation, recovery, projection re-render (vocabulary may have changed), version switch,
    /// and delete (the re-index then drops the entry). Raised OUTSIDE the per-session gate so a
    /// handler may re-enter RunForSessionAsync for the same id; never raised for a no-op/skipped
    /// write. Wrapped like SessionFinalizeCompleted: a throwing subscriber must never fault the
    /// calling command.</summary>
    public event Action<string>? SessionContentChanged;

    private void RaiseSessionContentChanged(string sessionId)
    {
        try { SessionContentChanged?.Invoke(sessionId); } catch { }
    }
```
- [ ] **SaveMetaAsync.** Replace:
```csharp
        if (!wrote) return;                         // deleted mid-save: no write, so no index delta
```
with:
```csharp
        if (!wrote) return;                         // deleted mid-save: no write, so no index delta
        RaiseSessionContentChanged(sessionId);      // title/matters/participants feed the search index
```
- [ ] **SetArchivedAsync.** Replace the whole method (quoted below as at master; only the wrapper + the no-op return value change — the inner writes are byte-identical):
```csharp
    public Task SetArchivedAsync(string sessionId, bool archived, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            var current = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(inner);
            if (current is null || current.Archived == archived) return true;
            await new MetadataStore(paths.MetaJson(sessionId))
                .SaveAsync(current with { Archived = archived }, inner);
            await new SessionWriter(paths, settings.Current, time)
                .RegenerateProjectionsAsync(sessionId, inner);
            return true;
        }, ct);
```
with:
```csharp
    public async Task SetArchivedAsync(string sessionId, bool archived, CancellationToken ct)
    {
        bool wrote = await RunForSessionAsync(sessionId, async inner =>
        {
            var current = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(inner);
            if (current is null || current.Archived == archived) return false;
            await new MetadataStore(paths.MetaJson(sessionId))
                .SaveAsync(current with { Archived = archived }, inner);
            await new SessionWriter(paths, settings.Current, time)
                .RegenerateProjectionsAsync(sessionId, inner);
            return true;
        }, ct);
        if (wrote) RaiseSessionContentChanged(sessionId);    // meta.json stamp changed
    }
```
(The callers only see `Task`, unchanged; the inner bool was never observed before.)
- [ ] **SaveTextCorrectionsAsync.** Replace the method's expression-bodied header/footer only — the inner lambda is byte-identical. Replace:
```csharp
    public Task<bool> SaveTextCorrectionsAsync(string sessionId,
        IReadOnlyDictionary<int, string> corrections, IReadOnlyCollection<int> reverts,
        CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
```
with:
```csharp
    public async Task<bool> SaveTextCorrectionsAsync(string sessionId,
        IReadOnlyDictionary<int, string> corrections, IReadOnlyCollection<int> reverts,
        CancellationToken ct)
    {
        bool changed = await RunForSessionAsync(sessionId, async inner =>
```
and replace its closing:
```csharp
            return changed;
        }, ct);
```
with:
```csharp
            return changed;
        }, ct);
        if (changed) RaiseSessionContentChanged(sessionId);
        return changed;
    }
```
CAUTION: the inner lambda's local is ALSO named `changed` — rename the INNER one to `wrote` while wrapping (three occurrences inside the lambda: `bool changed = await new EditStore...` → `bool wrote = ...`; `if (changed)` → `if (wrote)`; `return changed;` → `return wrote;`) so the wrapper's `changed` does not collide.
- [ ] **SaveTranscriptEditsAsync.** Same wrap. Replace:
```csharp
    public Task<bool> SaveTranscriptEditsAsync(string sessionId, TranscriptEditBatch batch, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
```
with:
```csharp
    public async Task<bool> SaveTranscriptEditsAsync(string sessionId, TranscriptEditBatch batch,
        CancellationToken ct)
    {
        bool changed = await RunForSessionAsync(sessionId, async inner =>
```
rename the inner lambda's `changed` local to `wrote` (five occurrences: declaration, two `|=`, the `= true` in the splits loop, the `if (changed)` regen guard, and `return changed;`), then replace its closing `}, ct);` with:
```csharp
        }, ct);
        if (changed) RaiseSessionContentChanged(sessionId);
        return changed;
    }
```
- [ ] **SaveSpeakerPinsAsync.** Same wrap (its inner lambda returns `false` for a deleted session, `true` after the pin write). Replace:
```csharp
    public Task<bool> SaveSpeakerPinsAsync(string sessionId, TranscriptSource source,
        IReadOnlyCollection<int> seqs, SpeakerPinTarget target, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
```
with:
```csharp
    public async Task<bool> SaveSpeakerPinsAsync(string sessionId, TranscriptSource source,
        IReadOnlyCollection<int> seqs, SpeakerPinTarget target, CancellationToken ct)
    {
        bool wrote = await RunForSessionAsync(sessionId, async inner =>
```
and its closing:
```csharp
            return true;
        }, ct);
```
with:
```csharp
            return true;
        }, ct);
        if (wrote) RaiseSessionContentChanged(sessionId);
        return wrote;
    }
```
(No inner local collides here — the lambda has no `wrote`/`changed` local.)
- [ ] **RemoveSpeakerPinsAsync.** Same wrap as SaveTextCorrectionsAsync (inner local IS named `changed` — rename it to `wrote`, two occurrences plus the `if`/`return`). Wrapper:
```csharp
    public async Task<bool> RemoveSpeakerPinsAsync(string sessionId, TranscriptSource source,
        IReadOnlyCollection<int> seqs, CancellationToken ct)
    {
        bool changed = await RunForSessionAsync(sessionId, async inner =>
        { ... existing body with the inner local renamed wrote ... }, ct);
        if (changed) RaiseSessionContentChanged(sessionId);
        return changed;
    }
```
(Write it out in full when editing — the inner body is the existing 8 lines verbatim, only the local renamed.)
- [ ] **SaveDiarisationAsync (4-arg overload).** Replace its header:
```csharp
    public Task<IReadOnlyDictionary<string, string>> SaveDiarisationAsync(
        string sessionId, DiarisationCommit commit,
        IReadOnlyDictionary<string, string>? participantClusterKeys, CancellationToken ct) =>
        RunForSessionAsync<IReadOnlyDictionary<string, string>>(sessionId, async inner =>
```
with:
```csharp
    public async Task<IReadOnlyDictionary<string, string>> SaveDiarisationAsync(
        string sessionId, DiarisationCommit commit,
        IReadOnlyDictionary<string, string>? participantClusterKeys, CancellationToken ct)
    {
        var remap = await RunForSessionAsync<IReadOnlyDictionary<string, string>>(sessionId, async inner =>
```
and its closing:
```csharp
            return result.FreshKeyRemap;
        }, ct);
```
with:
```csharp
            return result.FreshKeyRemap;
        }, ct);
        // Speakers overlay/ownership changed (or the session vanished mid-run - the re-index then
        // simply drops the entry). Unconditional: cheaper than threading a wrote flag out.
        RaiseSessionContentChanged(sessionId);
        return remap;
    }
```
(The 3-arg back-compat overload delegates to this one — no separate edit.)
- [ ] **DeleteSessionAsync.** Replace its closing:
```csharp
        if (tags.Count > 0)
            await ApplyTagDeltaLockedAsync([], tags, ct);
    }
```
with:
```csharp
        if (tags.Count > 0)
            await ApplyTagDeltaLockedAsync([], tags, ct);
        RaiseSessionContentChanged(sessionId);      // the re-index drops the deleted session's entry
    }
```
- [ ] **RecoverAllAsync.** Replace:
```csharp
                if (did) { recovered.Add(id); onRecovered?.Invoke(id); }
```
with:
```csharp
                if (did) { recovered.Add(id); onRecovered?.Invoke(id); RaiseSessionContentChanged(id); }
```
- [ ] **RegenerateEachAsync.** Replace (inside its `try`):
```csharp
                await RunForSessionAsync(id, async inner =>
                {
                    await new SessionWriter(paths, settings.Current, time)
                        .RegenerateProjectionsAsync(id, inner);
                    return true;
                }, ct);
```
with:
```csharp
                await RunForSessionAsync(id, async inner =>
                {
                    await new SessionWriter(paths, settings.Current, time)
                        .RegenerateProjectionsAsync(id, inner);
                    return true;
                }, ct);
                // A re-render re-applies the CURRENT vocabulary, which the index bakes into its
                // corrected text - so bulk regenerate and matter cascades must re-index too (the
                // freshness stamps alone cannot see a vocabulary change; design 2.1 stamp set).
                RaiseSessionContentChanged(id);
```
- [ ] **SetActiveVersionAsync (cross-branch — body unknown at plan time).** Locate `SetActiveVersionAsync` in `MaintenanceService.cs` (it ships in feat/retranscription-versions with contract `Task<bool> SetActiveVersionAsync(string sessionId, string versionId, CancellationToken ct)`; `grep -n "SetActiveVersionAsync" src/LocalScribe.App/Services/MaintenanceService.cs`). Apply this body-agnostic transformation: rename the existing method to `private ... SetActiveVersionCoreAsync` (body byte-identical), then add:
```csharp
    /// <summary>Version-switch wrapper (this branch): the active version determines WHICH
    /// transcript/edits/speakers the search index derives from, so a successful switch re-indexes
    /// the session (design 2026-07-13 section 2.1).</summary>
    public async Task<bool> SetActiveVersionAsync(string sessionId, string versionId, CancellationToken ct)
    {
        bool switched = await SetActiveVersionCoreAsync(sessionId, versionId, ct);
        if (switched) RaiseSessionContentChanged(sessionId);
        return switched;
    }
```
If no method by that exact name exists post-merge, find the version-switch write path (search for `ActiveVersion` writes in MaintenanceService) and apply the same raise-on-success rule there instead.
- [ ] **Run tests and see PASS.** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~MaintenanceServiceContentChangedTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\` — expected: 4 passed. Then run the FULL App suite (`--filter` omitted) to prove the wraps changed no behavior (MaintenanceServiceTests/EditingTests/DiarisationTests all still green).
- [ ] **Commit.**
```
git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceContentChangedTests.cs
git commit -m "feat(app): SessionContentChanged seam on every gated session mutation

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: App — Read-view find state (`ReadViewViewModel` + `ReadRow` flags)
**Files:**
- Modify `src\LocalScribe.App\ViewModels\ReadRow.cs` (two observable flags).
- Modify `src\LocalScribe.App\ViewModels\ReadViewViewModel.cs` (find-bar state region after `JumpToSection`; one line each in `EnterEditMode` and `ApplyRows`).
- Test (new) `tests\LocalScribe.App.Tests\ReadViewFindTests.cs`.

**Interfaces:**
- Consumes: existing `Rows` (`ReadRow.Data.Text` — the VISIBLE corrected text), `IsEditMode`/`CanEdit`, `ApplyRows` reload path.
- Produces:
```csharp
// ReadRow
[ObservableProperty] bool IsFindMatch;         // row contains the find text
[ObservableProperty] bool IsCurrentFindMatch;  // the "2/7" current row
// ReadViewViewModel
[ObservableProperty] bool IsFindOpen;
[ObservableProperty] string FindText;          // recompute-on-change
[ObservableProperty] string FindStatus;        // "" / "0/0" / "2/7"
[ObservableProperty] int CurrentFindRowIndex;  // -1 = none
public void OpenFind(string? initialText = null);   // no-op in Edit mode
public void CloseFind();
public void FindNext();                        // wraps
public void FindPrevious();                    // wraps
public int RowIndexOfSeq(int seq);             // -1 when hidden/absent
public void MoveFindTo(int rowIndex);          // search-page click-through targeting
```

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\ReadViewFindTests.cs`:
```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ReadViewFindTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-find-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakeSettings _settings;
    private readonly FakeReporter _reporter = new();
    private readonly LocalScribe.Core.Tests.ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly MaintenanceService _maintenance;

    public ReadViewFindTests()
    {
        _paths = new StoragePaths(_root);
        _settings = new FakeSettings(new Settings { StorageRoot = _root });
        _maintenance = new MaintenanceService(_paths, _settings, new FakeBin(), _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ReadViewViewModel MakeVm()
        => new(_maintenance, _paths, _settings, _reporter, new FakePlayer(), dispatch: a => a(), _time);

    /// <summary>Rows after load: [0] Sam (seq 0+1 grouped; seq 1 corrected), [1] Jane (seq 2),
    /// [2] marker. "morning" hits rows 0 and 1; "device" hits the marker row; "orignal" exists
    /// only in seq 1's machine RAW text (not visible).</summary>
    private async Task WriteFixtureSessionAsync(string id)
    {
        var started = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(10), DurationMs = 600_000,
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            Model = "small.en", Backend = "cuda", Language = "en",
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "Find fixture",
            Participants = new[]
            {
                new SessionParticipant { Id = "p1", Name = "Sam", Side = SourceKind.Local, IsSelf = true },
                new SessionParticipant { Id = "p2", Name = "Jane", Side = SourceKind.Remote },
            },
        }, CancellationToken.None);
        var t = new TranscriptStore(_paths.TranscriptJsonl(id));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1500,
            "we spoke to the client this morning", "Me"), CancellationToken.None);
        await t.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 1600, 3000,
            "the orignal words", "Me"), CancellationToken.None);
        await t.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Remote, 3200, 4200,
            "sounds good to me this morning", "Them"), CancellationToken.None);
        await t.AppendAsync(TranscriptLine.Marker(3, 4200, Markers.AudioDeviceChanged),
            CancellationToken.None);
        await new EditStore(_paths.SessionDir(id), _time)
            .ApplyTextCorrectionAsync(1, "the corrected words", CancellationToken.None);
    }

    [Fact]
    public async Task Find_counts_visible_corrected_text_and_wraps_with_next_previous()
    {
        await WriteFixtureSessionAsync("find-1");
        var vm = MakeVm();
        await vm.LoadAsync("find-1", CancellationToken.None);
        Assert.Empty(_reporter.Errors);
        Assert.Equal(3, vm.Rows.Count);

        vm.OpenFind();
        Assert.True(vm.IsFindOpen);
        Assert.Equal("", vm.FindStatus);

        vm.FindText = "morning";
        Assert.Equal("1/2", vm.FindStatus);
        Assert.Equal(0, vm.CurrentFindRowIndex);
        vm.FindNext();
        Assert.Equal("2/2", vm.FindStatus);
        Assert.Equal(1, vm.CurrentFindRowIndex);
        vm.FindNext();                                                    // wraps forward
        Assert.Equal("1/2", vm.FindStatus);
        vm.FindPrevious();                                                // wraps backward
        Assert.Equal("2/2", vm.FindStatus);

        vm.FindText = "orignal";                                          // machine RAW text: not visible
        Assert.Equal("0/0", vm.FindStatus);
        vm.FindText = "corrected";                                        // the visible corrected text IS
        Assert.Equal("1/1", vm.FindStatus);
        Assert.Equal(0, vm.CurrentFindRowIndex);

        vm.FindText = "device";                                           // marker rows are visible text too
        Assert.Equal("1/1", vm.FindStatus);
        Assert.Equal(2, vm.CurrentFindRowIndex);
    }

    [Fact]
    public async Task Find_flags_rows_and_close_clears_them()
    {
        await WriteFixtureSessionAsync("find-2");
        var vm = MakeVm();
        await vm.LoadAsync("find-2", CancellationToken.None);

        vm.OpenFind("morning");
        Assert.True(vm.Rows[0].IsFindMatch);
        Assert.True(vm.Rows[0].IsCurrentFindMatch);
        Assert.True(vm.Rows[1].IsFindMatch);
        Assert.False(vm.Rows[1].IsCurrentFindMatch);
        Assert.False(vm.Rows[2].IsFindMatch);

        vm.FindNext();
        Assert.False(vm.Rows[0].IsCurrentFindMatch);
        Assert.True(vm.Rows[1].IsCurrentFindMatch);

        vm.CloseFind();
        Assert.False(vm.IsFindOpen);
        Assert.All(vm.Rows, r => { Assert.False(r.IsFindMatch); Assert.False(r.IsCurrentFindMatch); });
        Assert.Equal(-1, vm.CurrentFindRowIndex);
        Assert.Equal("", vm.FindStatus);
        Assert.Equal("morning", vm.FindText);                             // kept for a quick re-open
    }

    [Fact]
    public async Task Find_survives_a_rows_reload_and_edit_mode_closes_it()
    {
        await WriteFixtureSessionAsync("find-3");
        var vm = MakeVm();
        await vm.LoadAsync("find-3", CancellationToken.None);

        vm.OpenFind("morning");
        Assert.Equal("1/2", vm.FindStatus);
        await vm.ReloadRowsAsync(CancellationToken.None);                 // rows are NEW objects
        Assert.Equal("1/2", vm.FindStatus);
        Assert.True(vm.Rows[0].IsFindMatch);                              // flags re-stamped on new rows

        vm.EnterEditMode();
        Assert.True(vm.IsEditMode);
        Assert.False(vm.IsFindOpen);                                      // entering Edit closes the bar
        vm.OpenFind();
        Assert.False(vm.IsFindOpen);                                      // and re-opening is refused
        vm.CancelEdit();
    }

    [Fact]
    public async Task RowIndexOfSeq_and_MoveFindTo_target_the_snippet_row()
    {
        await WriteFixtureSessionAsync("find-4");
        var vm = MakeVm();
        await vm.LoadAsync("find-4", CancellationToken.None);

        Assert.Equal(0, vm.RowIndexOfSeq(1));                             // seq 1 lives in the grouped row 0
        Assert.Equal(1, vm.RowIndexOfSeq(2));
        Assert.Equal(-1, vm.RowIndexOfSeq(99));

        vm.OpenFind("morning");                                           // matches rows 0 and 1
        vm.MoveFindTo(1);
        Assert.Equal(1, vm.CurrentFindRowIndex);
        Assert.Equal("2/2", vm.FindStatus);

        vm.MoveFindTo(2);                                                 // row 2 is not a match: unchanged
        Assert.Equal(1, vm.CurrentFindRowIndex);
    }

    // Per-file fakes (App.Tests convention), byte-identical to ReadViewViewModelTests'.
    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }

    private sealed class FakeBin : IRecycleBin
    {
        public void SendToRecycleBin(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) { }
    }

    private sealed class FakePlayer : IDualAudioPlayer
    {
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public event Action? MediaReady;
        public event Action? MediaEnded;
        public void Load(string? localPath, string? remotePath) { }
        public void Play() { }
        public void Pause() { }
        public void SeekMs(long ms) => PositionMs = ms;
        public void SetLegMuted(bool local, bool muted) { }
        public void SetLegVolume(bool local, double volume) { }
        public void Dispose() { }
        public void RaiseReady() => MediaReady?.Invoke();
        public void RaiseEnded() => MediaEnded?.Invoke();
    }
}
```
(NOTE: if the compiler flags the unused `MediaReady`/`MediaEnded` events with a warning in THIS file, add `public void Raise() { MediaReady?.Invoke(); MediaEnded?.Invoke(); }` — the `RaiseReady`/`RaiseEnded` methods above already reference both, mirroring ReadViewViewModelTests, so no warning is expected.)
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ReadViewFindTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\` — expected: `error CS1061: 'ReadViewViewModel' does not contain a definition for 'OpenFind'` (and `IsFindMatch` on `ReadRow`).
- [ ] **Add the ReadRow flags.** In `src\LocalScribe.App\ViewModels\ReadRow.cs`, immediately after `[ObservableProperty] private bool _isNowPlaying;` insert:
```csharp

    /// <summary>Ctrl+F find-bar flags (design 2026-07-13 section 2.2 surface 3): row-level match
    /// tint + the distinct current-match tint, mirroring IsNowPlaying's decoupled-from-selection
    /// pattern. Stamped exclusively by ReadViewViewModel's find recompute.</summary>
    [ObservableProperty] private bool _isFindMatch;
    [ObservableProperty] private bool _isCurrentFindMatch;
```
- [ ] **Add the find region to ReadViewViewModel.** In `src\LocalScribe.App\ViewModels\ReadViewViewModel.cs`, immediately after the `JumpToSection` method's closing brace insert:
```csharp

    // ---- Ctrl+F find bar (design 2026-07-13 section 2.2 surface 3) ---------------------------
    // Searches the VISIBLE corrected text of the loaded version only (Rows[i].Data.Text - the
    // projected text: vocabulary + edits overlay + splits). Machine RAW text is deliberately not
    // searched here (that is the cross-session index's job, with its original-text labelling);
    // marker rows ARE searched - this is find-on-page over what the reader can see.

    [ObservableProperty] private bool _isFindOpen;
    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private string _findStatus = "";
    [ObservableProperty] private int _currentFindRowIndex = -1;
    private readonly List<int> _findMatchRows = new();

    partial void OnFindTextChanged(string value) => RecomputeFindMatches(moveToFirst: true);

    partial void OnCurrentFindRowIndexChanged(int oldValue, int newValue)
    {
        if (oldValue >= 0 && oldValue < Rows.Count) Rows[oldValue].IsCurrentFindMatch = false;
        if (newValue >= 0 && newValue < Rows.Count) Rows[newValue].IsCurrentFindMatch = true;
        UpdateFindStatus();
    }

    /// <summary>Opens the find bar. No-op in Edit mode (the bar searches the READ list only).
    /// With initialText (the search page's click-through term) the text change recomputes matches;
    /// re-opening with the same text recomputes explicitly so flags land on the current rows.</summary>
    public void OpenFind(string? initialText = null)
    {
        if (IsEditMode) return;
        IsFindOpen = true;
        if (initialText is not null && initialText != FindText) FindText = initialText;
        else RecomputeFindMatches(moveToFirst: _findMatchRows.Count == 0);
    }

    public void CloseFind()
    {
        IsFindOpen = false;
        foreach (var r in Rows) { r.IsFindMatch = false; r.IsCurrentFindMatch = false; }
        _findMatchRows.Clear();
        CurrentFindRowIndex = -1;
        FindStatus = "";
        // FindText is deliberately kept so Ctrl+F re-opens on the same term.
    }

    public void FindNext()
    {
        if (_findMatchRows.Count == 0) return;
        int pos = _findMatchRows.IndexOf(CurrentFindRowIndex);
        CurrentFindRowIndex = _findMatchRows[(pos + 1) % _findMatchRows.Count];   // pos -1 -> first
    }

    public void FindPrevious()
    {
        if (_findMatchRows.Count == 0) return;
        int pos = _findMatchRows.IndexOf(CurrentFindRowIndex);
        CurrentFindRowIndex = _findMatchRows[pos <= 0 ? _findMatchRows.Count - 1 : pos - 1];
    }

    /// <summary>Index of the read-list row whose grouped turn contains the seq; -1 when the seq is
    /// dedup-hidden or absent. The first row containing the seq is the scroll target (split parts
    /// of one seq can group into different rows; the first is fine for targeting).</summary>
    public int RowIndexOfSeq(int seq)
    {
        for (int i = 0; i < Rows.Count; i++)
            if (Rows[i].Data.Segments.Any(s => s.Seq == seq)) return i;
        return -1;
    }

    /// <summary>Points the current match at the given row (search-page click-through). A target row
    /// that is not itself a match - e.g. an original-text-only hit whose corrected text no longer
    /// contains the term - leaves the current match untouched (the window still scrolls to it).</summary>
    public void MoveFindTo(int rowIndex)
    {
        if (_findMatchRows.Contains(rowIndex)) { CurrentFindRowIndex = rowIndex; return; }
        int after = _findMatchRows.FirstOrDefault(i => i > rowIndex, -1);
        if (after >= 0) CurrentFindRowIndex = after;
    }

    private void RecomputeFindMatches(bool moveToFirst)
    {
        foreach (var r in Rows) { r.IsFindMatch = false; r.IsCurrentFindMatch = false; }
        _findMatchRows.Clear();
        string needle = FindText.Trim();
        if (!IsFindOpen || needle.Length == 0)
        {
            CurrentFindRowIndex = -1;
            FindStatus = "";
            return;
        }
        for (int i = 0; i < Rows.Count; i++)
            if (Rows[i].Data.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                _findMatchRows.Add(i);
                Rows[i].IsFindMatch = true;
            }
        int current = -1;
        if (_findMatchRows.Count > 0)
            current = !moveToFirst && _findMatchRows.Contains(CurrentFindRowIndex)
                ? CurrentFindRowIndex
                : _findMatchRows[0];
        if (CurrentFindRowIndex == current)
        {
            // Unchanged index: the property setter won't fire, so re-stamp + refresh explicitly.
            if (current >= 0) Rows[current].IsCurrentFindMatch = true;
            UpdateFindStatus();
        }
        else CurrentFindRowIndex = current;
    }

    private void UpdateFindStatus()
        => FindStatus = _findMatchRows.Count == 0
            ? (FindText.Trim().Length == 0 || !IsFindOpen ? "" : "0/0")
            : $"{_findMatchRows.IndexOf(CurrentFindRowIndex) + 1}/{_findMatchRows.Count}";
```
- [ ] **Close find on entering Edit mode.** In `EnterEditMode`, replace:
```csharp
    public void EnterEditMode()
    {
        if (!CanEdit || IsEditMode) return;
        EditSections.Clear();
```
with:
```csharp
    public void EnterEditMode()
    {
        if (!CanEdit || IsEditMode) return;
        CloseFind();                          // the find bar searches the read list only (design 2.2)
        EditSections.Clear();
```
- [ ] **Recompute after every rows rebuild.** In `ApplyRows`, replace:
```csharp
        Rows.Clear();
        foreach (var r in view.Rows) Rows.Add(new ReadRow(r));
        RestoreNowPlaying();
```
with:
```csharp
        Rows.Clear();
        foreach (var r in view.Rows) Rows.Add(new ReadRow(r));
        RestoreNowPlaying();
        if (IsFindOpen) RecomputeFindMatches(moveToFirst: false);   // flags live on the NEW rows
```
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed. Then run `--filter "FullyQualifiedName~ReadViewViewModelTests|FullyQualifiedName~ReadViewEditModeTests"` to prove the reload/edit paths regressed nothing.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/ReadRow.cs src/LocalScribe.App/ViewModels/ReadViewViewModel.cs tests/LocalScribe.App.Tests/ReadViewFindTests.cs
git commit -m "feat(app): read-view find state - matches, 2/7 status, wrap, reload survival

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: App — Read-view find bar UI (Ctrl+F, Enter/Shift+Enter/Esc, highlights, `ShowFindAt`)
**Files:**
- Modify `src\LocalScribe.App\ReadViewWindow.xaml` (find-bar Border after the header StackPanel; two ItemContainerStyle triggers).
- Modify `src\LocalScribe.App\ReadViewWindow.xaml.cs` (four commands, VM PropertyChanged subscription, Ctrl+F override, find-box key handler, `ShowFindAt` + pending-target, OnClosed unsubscribe).
- No new unit test (window code-behind + XAML are not unit-tested here — Task 6 tested the VM). The gate is: 0-warning build, full App+Core suites green, and the manual smoke below.

**Interfaces:**
- Consumes: Task 6's VM find API (`OpenFind`/`CloseFind`/`FindNext`/`FindPrevious`/`FindStatus`/`IsFindOpen`/`CurrentFindRowIndex`/`RowIndexOfSeq`/`MoveFindTo`), `ReadRow.IsFindMatch`/`IsCurrentFindMatch`.
- Produces: `public void ReadViewWindow.ShowFindAt(int seq, string term)` (callable before the initial load completes — the target is stashed and applied after `LoadAsync`); window commands `OpenFindCommand`/`FindNextCommand`/`FindPreviousCommand`/`CloseFindCommand` (`IRelayCommand`).

Steps:
- [ ] **Add the find-bar XAML.** In `ReadViewWindow.xaml`, locate the header StackPanel's END (quoted with its follower so the insertion point is unambiguous):
```xml
            <TextBlock Text="System-audio mix was active for part of this session (degraded capture marker present)."
                       Style="{StaticResource WarningText}"
                       Visibility="{Binding HasDegradedMarker, Converter={StaticResource BoolToVis}}" />
        </StackPanel>
        <Border DockPanel.Dock="Bottom" Margin="0,8,0,0"
```
and insert BETWEEN `</StackPanel>` and `<Border DockPanel.Dock="Bottom"`:
```xml
        <!-- Ctrl+F find bar (design 2026-07-13 section 2.2 surface 3): searches the VISIBLE
             corrected text of the loaded version. Buttons bind window commands via ElementName=Self
             (direct children, same pattern as the Edit/Save header buttons); Enter/Shift+Enter/Esc
             are code-behind PreviewKeyDown on the box (KeyBindings outside the visual tree resolve
             neither ElementName nor DataContext reliably - the OnSegmentTextBoxPreviewKeyDown
             precedent). -->
        <Border DockPanel.Dock="Top" Margin="0,0,0,8" Padding="8,4" CornerRadius="4"
                Background="{DynamicResource ControlFillColorSecondaryBrush}"
                Visibility="{Binding IsFindOpen, Converter={StaticResource BoolToVis}}">
            <DockPanel>
                <TextBlock DockPanel.Dock="Left" Text="Find" VerticalAlignment="Center" Margin="0,0,8,0" />
                <Button DockPanel.Dock="Right" Content="Close" Margin="4,0,0,0"
                        Command="{Binding CloseFindCommand, ElementName=Self}" />
                <Button DockPanel.Dock="Right" Content="Next" Margin="4,0,0,0"
                        ToolTip="Next match (Enter)"
                        Command="{Binding FindNextCommand, ElementName=Self}" />
                <Button DockPanel.Dock="Right" Content="Previous" Margin="8,0,0,0"
                        ToolTip="Previous match (Shift+Enter)"
                        Command="{Binding FindPreviousCommand, ElementName=Self}" />
                <TextBlock DockPanel.Dock="Right" Text="{Binding FindStatus}"
                           VerticalAlignment="Center" Margin="8,0,0,0" />
                <TextBox x:Name="FindBox" VerticalContentAlignment="Center"
                         Text="{Binding FindText, UpdateSourceTrigger=PropertyChanged}"
                         PreviewKeyDown="OnFindBoxPreviewKeyDown" />
            </DockPanel>
        </Border>
```
- [ ] **Add the row tints.** In `ReadViewWindow.xaml`'s RowList `ItemContainerStyle`, locate:
```xml
                        <DataTrigger Binding="{Binding IsNowPlaying}" Value="True">
                            <Setter Property="Background">
                                <Setter.Value>
                                    <SolidColorBrush Color="{DynamicResource SystemAccentColor}" Opacity="0.28" />
                                </Setter.Value>
                            </Setter>
                        </DataTrigger>
```
and insert immediately AFTER it (later triggers win, so find tints beat the now-playing tint when both apply):
```xml
                        <!-- Find-bar highlights (design 2026-07-13 section 2.2): every match gets a
                             faint accent tint; the CURRENT match a strong one. Declared after
                             IsNowPlaying so an overlapping row shows its find state. Theme resources
                             only (no ARGB literals - XamlHygiene). -->
                        <DataTrigger Binding="{Binding IsFindMatch}" Value="True">
                            <Setter Property="Background">
                                <Setter.Value>
                                    <SolidColorBrush Color="{DynamicResource SystemAccentColor}" Opacity="0.14" />
                                </Setter.Value>
                            </Setter>
                        </DataTrigger>
                        <DataTrigger Binding="{Binding IsCurrentFindMatch}" Value="True">
                            <Setter Property="Background">
                                <Setter.Value>
                                    <SolidColorBrush Color="{DynamicResource SystemAccentColor}" Opacity="0.45" />
                                </Setter.Value>
                            </Setter>
                        </DataTrigger>
```
- [ ] **Add the window commands.** In `ReadViewWindow.xaml.cs`, immediately after:
```csharp
    public IRelayCommand EnterEditCommand { get; }
    public IAsyncRelayCommand SaveEditsCommand { get; }
    public IRelayCommand CancelEditCommand { get; }
```
insert:
```csharp

    // Find-bar commands (design 2026-07-13 section 2.2 surface 3). Direct header/bar children bind
    // them via ElementName=Self; like the Edit commands they close over the `vm` ctor PARAMETER,
    // not the not-yet-assigned _vm field.
    public IRelayCommand OpenFindCommand { get; }
    public IRelayCommand FindNextCommand { get; }
    public IRelayCommand FindPreviousCommand { get; }
    public IRelayCommand CloseFindCommand { get; }
```
- [ ] **Construct them + subscribe.** In the ctor, replace:
```csharp
        CancelEditCommand = new RelayCommand(vm.CancelEdit);
        InitializeComponent();
```
with:
```csharp
        CancelEditCommand = new RelayCommand(vm.CancelEdit);
        OpenFindCommand = new RelayCommand(() => vm.OpenFind());
        FindNextCommand = new RelayCommand(vm.FindNext);
        FindPreviousCommand = new RelayCommand(vm.FindPrevious);
        CloseFindCommand = new RelayCommand(vm.CloseFind);
        InitializeComponent();
```
then replace:
```csharp
        _vm.Playback.PropertyChanged += OnPlaybackPropertyChanged;
```
with:
```csharp
        _vm.Playback.PropertyChanged += OnPlaybackPropertyChanged;
        // Find bar: focus the box when it opens; auto-scroll the read list to the current match.
        // Per-session window that genuinely closes - OnClosed MUST unsubscribe (house rule).
        _vm.PropertyChanged += OnVmPropertyChanged;
```
- [ ] **Apply a pending find target after load.** Replace the Loaded handler:
```csharp
        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync(_sessionId, CancellationToken.None);
            if (_vm.Playback.IsAvailable && !_tick.IsEnabled) _tick.Start(); // fast path if already published
        };
```
with:
```csharp
        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync(_sessionId, CancellationToken.None);
            if (_vm.Playback.IsAvailable && !_tick.IsEnabled) _tick.Start(); // fast path if already published
            if (_pendingFindTarget is { } t)                 // search-page click landed before load
            {
                ApplyFindTarget(t.Seq, t.Term);
                _pendingFindTarget = null;
            }
        };
```
- [ ] **Add the handlers + `ShowFindAt`.** Immediately after the `OnManageSpeakers` method insert:
```csharp

    // ---- Ctrl+F find bar (design 2026-07-13 section 2.2 surface 3) ----------------------------

    private (int Seq, string Term)? _pendingFindTarget;

    /// <summary>Ctrl+F opens the find bar. A window-level override rather than an InputBinding:
    /// KeyBindings sit outside the visual tree, where neither ElementName=Self nor the VM
    /// DataContext reliably resolves (the OnSegmentTextBoxPreviewKeyDown precedent).</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.F
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            _vm.OpenFind();
            e.Handled = true;
        }
    }

    /// <summary>Enter = next, Shift+Enter = previous, Esc = close (design 2.2). Code-behind on the
    /// box because it is a direct child (compiler-wired), unlike Style.Setter-nested elements.</summary>
    private void OnFindBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { _vm.CloseFind(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Shift)
        { _vm.FindPrevious(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        { _vm.FindNext(); e.Handled = true; }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReadViewViewModel.IsFindOpen) && _vm.IsFindOpen)
            // The bar only just became visible - focus on the next dispatcher turn.
            Dispatcher.BeginInvoke(() => { FindBox.Focus(); FindBox.SelectAll(); });
        else if (e.PropertyName == nameof(ReadViewViewModel.CurrentFindRowIndex)
            && _vm.CurrentFindRowIndex >= 0 && _vm.CurrentFindRowIndex < _vm.Rows.Count)
            RowList.ScrollIntoView(_vm.Rows[_vm.CurrentFindRowIndex]);
    }

    /// <summary>Search-page click-through (design 2026-07-13 section 2.2): open the find bar on the
    /// clicked hit's term and scroll to the row containing the segment. Callable before the initial
    /// LoadAsync has finished - the target is stashed and applied right after load.</summary>
    public void ShowFindAt(int seq, string term)
    {
        if (!_vm.IsLoaded) { _pendingFindTarget = (seq, term); return; }
        ApplyFindTarget(seq, term);
    }

    private void ApplyFindTarget(int seq, string term)
    {
        _vm.OpenFind(term);
        int row = _vm.RowIndexOfSeq(seq);
        if (row >= 0)
        {
            _vm.MoveFindTo(row);
            // Scroll to the target row even when it is not itself a find match (an original-text-
            // only hit: the corrected text no longer contains the term, so the bar shows 0/0 -
            // truthful - but the reader still lands on the right segment).
            RowList.ScrollIntoView(_vm.Rows[row]);
        }
    }
```
- [ ] **Unsubscribe on close.** In `OnClosed`, replace:
```csharp
        _vm.Playback.PropertyChanged -= OnPlaybackPropertyChanged;
```
with:
```csharp
        _vm.Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
```
- [ ] **Build 0-warning + full App/Core suites green.**
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\
```
Expected: build 0 warnings; App suite green incl. `XamlHygieneTests` (the tints use `SystemAccentColor` via DynamicResource — no ARGB literal; the window root's `TextElement.Foreground` marker is untouched); Core green (2 known fixture fails pre-existing).
- [ ] **Manual smoke (WPF).** Launch the app, open a transcript with several sections:
  1. Ctrl+F → bar appears below the header, box focused. Type a word visible in the transcript → status shows "1/N", all matching rows faintly tinted, the current one strongly tinted, list scrolled to it.
  2. Enter cycles forward and wraps past the last match; Shift+Enter cycles backward and wraps; the Previous/Next buttons do the same.
  3. Esc closes the bar and clears every tint; Ctrl+F re-opens with the previous term still in the box and matches restored.
  4. Enter Edit mode with the bar open → it closes; Ctrl+F while editing does nothing; Cancel back to read mode → Ctrl+F works again.
  5. On a session with a corrected line, search a word that exists only in the machine ORIGINAL → "0/0" (the visible corrected text is what's searched).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/ReadViewWindow.xaml.cs
git commit -m "feat(app): read-view Ctrl+F find bar - highlights, wrap navigation, ShowFindAt targeting

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: App — Sessions quick filter consults the index (snippet line under the title)
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SessionRowViewModel.cs` (become `partial ObservableObject`; add `ContentSnippet`).
- Modify `src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs` (optional `searchIndex`/debounce ctor params; debounced content query; `PassesFilters` OR-branch; snippet stamping in `ApplyFilters`/`UpsertRowAsync`).
- Modify `src\LocalScribe.App\Pages\SessionsPage.xaml` (Title column → template column with the snippet line).
- Test (new) `tests\LocalScribe.App.Tests\SessionsPageContentFilterTests.cs`.

**Interfaces:**
- Consumes: `SearchIndexService.Query`/`InitializeAsync` (Task 4), existing `PassesFilters`/`ApplyFilters`/`UpsertRowAsync`, `LiveTestDoubles.MakeController/Options` (linked into App.Tests).
- Produces: `SessionsPageViewModel` ctor gains `SearchIndexService? searchIndex = null, int contentSearchDebounceMs = 250` (optional — every existing call site keeps compiling); `internal Task? SessionsPageViewModel.ContentFilterTask` (test seam); `[ObservableProperty] string? SessionRowViewModel.ContentSnippet`.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\SessionsPageContentFilterTests.cs`:
```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SessionsPageContentFilterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-content-filter-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public SessionsPageContentFilterTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class RecordingErrors : IUiErrorReporter
    {
        public List<string> Reports { get; } = [];
        public void Report(string context, Exception ex) => Reports.Add(context + ": " + ex.Message);
        public void Info(string message) { }
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

    private async Task WriteSessionAsync(string id, string title, string text, DateTimeOffset started)
    {
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(5), DurationMs = 300_000,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = title, Medium = Medium.Webex }, CancellationToken.None);
        await new TranscriptStore(_paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, text, "Me"),
            CancellationToken.None);
    }

    private async Task<(SessionsPageViewModel Vm, SearchIndexService? Index, RecordingErrors Errors)>
        MakeVmAsync(bool withIndex = true)
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var errors = new RecordingErrors();
        SearchIndexService? index = null;
        if (withIndex)
        {
            index = new SearchIndexService(_paths, () => new Settings(), TimeProvider.System,
                saveDebounceMs: 0);
            await index.InitializeAsync(CancellationToken.None);
        }
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(), errors,
            dispatch: a => a(), TimeProvider.System, revealInExplorer: _ => { },
            searchIndex: index, contentSearchDebounceMs: 0);
        return (vm, index, errors);
    }

    [Fact]
    public async Task Content_match_keeps_the_row_visible_with_one_snippet_line()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-a", "Alpha", "we discussed the retainer agreement", t);
        await WriteSessionAsync("s-b", "Bravo", "totally unrelated content", t.AddHours(1));
        var (vm, _, errors) = await MakeVmAsync();
        await vm.OnNavigatedToAsync();

        vm.FilterText = "retainer";                        // matches NO title
        Assert.Empty(vm.Rows);                             // the instant title pass hides everything
        await (vm.ContentFilterTask ?? Task.CompletedTask);

        var row = Assert.Single(vm.Rows);                  // the content match re-surfaces s-a
        Assert.Equal("s-a", row.Id);
        Assert.NotNull(row.ContentSnippet);
        Assert.Contains("retainer", row.ContentSnippet);
        Assert.StartsWith("Me:", row.ContentSnippet);      // speaker-prefixed snippet
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Clearing_the_filter_clears_snippets_and_title_matches_still_work()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-a", "Alpha", "we discussed the retainer agreement", t);
        await WriteSessionAsync("s-b", "Bravo", "totally unrelated content", t.AddHours(1));
        var (vm, _, _) = await MakeVmAsync();
        await vm.OnNavigatedToAsync();

        vm.FilterText = "retainer";
        await (vm.ContentFilterTask ?? Task.CompletedTask);
        Assert.Single(vm.Rows);

        vm.FilterText = "";
        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Null(r.ContentSnippet));

        vm.FilterText = "Bravo";                           // pure title match: instant
        Assert.Contains(vm.Rows, r => r.Id == "s-b");
        await (vm.ContentFilterTask ?? Task.CompletedTask);
        var bravo = vm.Rows.Single(r => r.Id == "s-b");
        Assert.Null(bravo.ContentSnippet);                 // "Bravo" is nowhere in transcript content
    }

    [Fact]
    public async Task Without_an_index_title_filtering_is_unchanged()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-a", "Alpha", "we discussed the retainer agreement", t);
        var (vm, _, errors) = await MakeVmAsync(withIndex: false);
        await vm.OnNavigatedToAsync();

        vm.FilterText = "retainer";
        Assert.Null(vm.ContentFilterTask);                 // no index -> no content query scheduled
        Assert.Empty(vm.Rows);                             // title-only behavior, exactly as before
        vm.FilterText = "Alph";
        Assert.Single(vm.Rows);
        Assert.Empty(errors.Reports);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SessionsPageContentFilterTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\` — expected: `error CS1739: The best overload for '.ctor' does not have a parameter named 'searchIndex'` (and `ContentSnippet`/`ContentFilterTask` unresolved).
- [ ] **Make `SessionRowViewModel` observable + add the snippet.** In `SessionRowViewModel.cs`, add `using CommunityToolkit.Mvvm.ComponentModel;` to the usings, then replace:
```csharp
public sealed class SessionRowViewModel
{
```
with:
```csharp
public sealed partial class SessionRowViewModel : ObservableObject
{
    /// <summary>The ONE mutable field on this otherwise-immutable row (design 2026-07-13 section
    /// 2.2 surface 2): the single content-match snippet line the Sessions quick filter shows under
    /// the title when the filter text matched this session's transcript content. Stamped
    /// exclusively by SessionsPageViewModel's content-filter pass; null hides the line. Every
    /// display STRING above stays computed-once (a refresh still replaces the whole object).</summary>
    [ObservableProperty] private string? _contentSnippet;

```
- [ ] **Widen the SessionsPageViewModel ctor.** Add `using LocalScribe.Core.Search;` to the usings. Replace:
```csharp
    public SessionsPageViewModel(MaintenanceService maintenance, SessionViewModel session,
        WindowRegistry registry, IUiErrorReporter errors, Action<Action> dispatch,
        TimeProvider time, Action<string> revealInExplorer)
    {
        (_maintenance, _registry, _errors, _dispatch, _time, _revealInExplorer)
            = (maintenance, registry, errors, dispatch, time, revealInExplorer);
```
with:
```csharp
    public SessionsPageViewModel(MaintenanceService maintenance, SessionViewModel session,
        WindowRegistry registry, IUiErrorReporter errors, Action<Action> dispatch,
        TimeProvider time, Action<string> revealInExplorer,
        SearchIndexService? searchIndex = null, int contentSearchDebounceMs = 250)
    {
        (_maintenance, _registry, _errors, _dispatch, _time, _revealInExplorer)
            = (maintenance, registry, errors, dispatch, time, revealInExplorer);
        (_searchIndex, _contentSearchDebounceMs) = (searchIndex, contentSearchDebounceMs);
```
and add the fields after `private IReadOnlyList<SessionRowViewModel> _all = [];`:
```csharp
    // Sessions quick-filter content matching (design 2026-07-13 section 2.2 surface 2). Optional:
    // compositions without an index (existing tests) keep the exact pre-search behavior.
    private readonly SearchIndexService? _searchIndex;
    private readonly int _contentSearchDebounceMs;
    private Dictionary<string, string> _contentMatches = new(StringComparer.Ordinal);   // id -> snippet
    private CancellationTokenSource? _contentSearchCts;

    /// <summary>Test seam: the in-flight debounced content query, if any. Null when no index is
    /// composed or the filter is empty.</summary>
    internal Task? ContentFilterTask { get; private set; }
```
- [ ] **Schedule the content query on filter change.** Replace:
```csharp
    partial void OnFilterTextChanged(string value) => ApplyFilters();
```
with:
```csharp
    partial void OnFilterTextChanged(string value)
    {
        ApplyFilters();                        // instant title/metadata pass - unchanged behavior
        ScheduleContentFilter(value);          // debounced index consult (design 2026-07-13 2.2)
    }
```
- [ ] **Extend `PassesFilters`.** Replace:
```csharp
        if (FilterText.Length > 0
            && !row.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase)) return false;
```
with:
```csharp
        if (FilterText.Length > 0
            && !row.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            && !_contentMatches.ContainsKey(row.Id)) return false;      // content match rescues the row
```
- [ ] **Stamp snippets in `ApplyFilters`.** Replace:
```csharp
        string? keepId = SelectedRow?.Id;
        Rows.Clear();
        foreach (var row in _all)
            if (PassesFilters(row)) Rows.Add(row);
        SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId);
```
with:
```csharp
        string? keepId = SelectedRow?.Id;
        Rows.Clear();
        foreach (var row in _all)
        {
            // Content-snippet stamping rides the same pass (design 2026-07-13 2.2): matched rows
            // show one snippet line under the title; everything else shows none.
            row.ContentSnippet = _contentMatches.TryGetValue(row.Id, out string? snip) ? snip : null;
            if (PassesFilters(row)) Rows.Add(row);
        }
        SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId);
```
- [ ] **Stamp in `UpsertRowAsync` too** (it bypasses ApplyFilters by design). Replace:
```csharp
                var newRow = new SessionRowViewModel(item, _time, MatterLookup,
                    isFinalizing: sessionId == _session.FinalizingSessionId);
                var list = _all.ToList();
```
with:
```csharp
                var newRow = new SessionRowViewModel(item, _time, MatterLookup,
                    isFinalizing: sessionId == _session.FinalizingSessionId);
                newRow.ContentSnippet = _contentMatches.TryGetValue(sessionId, out string? snip) ? snip : null;
                var list = _all.ToList();
```
- [ ] **Add the debounced query machinery.** Insert at the end of the class (immediately before its closing brace, after `RemoveRowInPlace`):
```csharp

    /// <summary>Debounced content consult of the search index (design 2026-07-13 section 2.2
    /// surface 2, ~250 ms). Each keystroke supersedes the previous query (CTS reset); an empty
    /// filter clears the match set immediately. The query itself runs off-thread (Task.Run) and
    /// marshals its result through _dispatch; PassesFilters then ORs the match set into the same
    /// filter pass, so title/metadata filtering behavior is unchanged. Stale matches from the
    /// previous text may keep a row visible for one debounce interval - replaced, never additive.</summary>
    private void ScheduleContentFilter(string filterText)
    {
        if (_searchIndex is null) return;                  // compositions without an index: old behavior
        _contentSearchCts?.Cancel();
        var cts = _contentSearchCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(filterText))
        {
            ContentFilterTask = null;
            if (_contentMatches.Count == 0) return;
            _contentMatches = new(StringComparer.Ordinal);
            ApplyFilters();
            return;
        }
        ContentFilterTask = RunContentFilterAsync(filterText, cts.Token);
    }

    private async Task RunContentFilterAsync(string filterText, CancellationToken ct)
    {
        try
        {
            if (_contentSearchDebounceMs > 0) await Task.Delay(_contentSearchDebounceMs, ct);
            var results = await Task.Run(() => _searchIndex!.Query(new SearchQuery(filterText)), ct);
            if (ct.IsCancellationRequested) return;
            _dispatch(() =>
            {
                if (ct.IsCancellationRequested) return;    // a newer keystroke superseded this query
                _contentMatches = results.ToDictionary(r => r.Session.SessionId,
                    r => r.Hits.Count > 0 ? FormatContentSnippet(r.Hits[0]) : "",
                    StringComparer.Ordinal);
                ApplyFilters();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _errors.Report("Searching sessions", ex); }
    }

    /// <summary>"Speaker: ...snippet..." plus the original-text label when the hit lives only in
    /// the machine original (design 2.1: corrections never hide content from search).</summary>
    private static string FormatContentSnippet(LocalScribe.Core.Search.SearchHit hit)
        => (hit.Speaker.Length > 0 ? hit.Speaker + ": " : "") + hit.Snippet
           + (hit.MatchesOriginalOnly ? " (matches original text)" : "");
```
- [ ] **Snippet line in the grid.** In `SessionsPage.xaml`, replace the Title column:
```xml
                <DataGridTextColumn Header="Title" Width="2*" MinWidth="150" Binding="{Binding Title}">
                    <DataGridTextColumn.ElementStyle>
                        <Style TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
                            <Setter Property="TextTrimming" Value="CharacterEllipsis" />
                            <Setter Property="ToolTip" Value="{Binding Title}" />
                        </Style>
                    </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>
```
with:
```xml
                <!-- Template column (was DataGridTextColumn) so a content-matched row can show ONE
                     snippet line under the title (design 2026-07-13 section 2.2 surface 2).
                     SortMemberPath preserves Title sorting; the snippet TextBlock collapses when
                     ContentSnippet is null (the non-matched/idle state). -->
                <DataGridTemplateColumn Header="Title" Width="2*" MinWidth="150" SortMemberPath="Title">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <StackPanel VerticalAlignment="Center">
                                <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis"
                                           ToolTip="{Binding Title}" />
                                <TextBlock Text="{Binding ContentSnippet}" FontStyle="Italic" Opacity="0.7"
                                           TextTrimming="CharacterEllipsis"
                                           ToolTip="{Binding ContentSnippet}">
                                    <TextBlock.Style>
                                        <Style TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding ContentSnippet}" Value="{x:Null}">
                                                    <Setter Property="Visibility" Value="Collapsed" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </TextBlock.Style>
                                </TextBlock>
                            </StackPanel>
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
```
- [ ] **Run tests and see PASS.** Same filter — expected: 3 passed. Then run the whole `SessionsPageViewModelTests` class plus `SessionsPageMatterFilterSearchTests|SessionsPageMatterLabelsTests` to prove the ctor widening + ApplyFilters/PassesFilters edits regressed nothing.
- [ ] **Build 0-warning + full App suite.**
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\
```
Expected: 0 warnings, all green (incl. `XamlHygieneTests` — the new column adds no ARGB literal and the page root marker is untouched).
- [ ] **Manual smoke (WPF).** (The index is not composed into the app until Task 10 — smoke ONLY the unchanged behavior now, re-smoke content matching in Task 10 item 5.)
  1. Sessions page: title filtering, matter filter, Show archived all behave exactly as before; no snippet lines appear.
  2. Sort by the Title column header still works (template column with SortMemberPath).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SessionRowViewModel.cs src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs src/LocalScribe.App/Pages/SessionsPage.xaml tests/LocalScribe.App.Tests/SessionsPageContentFilterTests.cs
git commit -m "feat(app): Sessions quick filter consults the search index with a snippet line per matched row

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: App — `SearchPageViewModel` (query box, facets, result cards, click-through event)
**Files:**
- New `src\LocalScribe.App\ViewModels\SearchPageViewModel.cs`.
- Test (new) `tests\LocalScribe.App.Tests\SearchPageViewModelTests.cs`.

**Interfaces:**
- Consumes: `SearchIndexService` (Task 4: `Query`, `IsReady`, `ReadyChanged`), `MaintenanceService.ListMattersAsync` (facet labels), `MatterFilterOption` (existing record in `SessionsPageViewModel.cs`), `TimestampFormat.Stamp` (relative stamps), `AppKind` enum (app facet values), `IUiErrorReporter`, dispatch seam, `TimeProvider.LocalTimeZone` (date-facet day boundaries).
- Produces:
```csharp
public sealed record SearchSnippetRow(string SessionId, int Seq, string MatchedTerm, string Stamp,
    string Speaker, string Snippet, bool MatchesOriginalOnly)
{ public string StampDisplay { get; } public string SnippetDisplay { get; } }
public sealed record SearchResultCard(string SessionId, string Title, string DateDisplay,
    string App, string MattersDisplay, IReadOnlyList<SearchSnippetRow> Snippets);
public sealed partial class SearchPageViewModel : ObservableObject
{
    public SearchPageViewModel(SearchIndexService index, MaintenanceService maintenance,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time, int debounceMs = 250);
    public ObservableCollection<SearchResultCard> Results { get; }
    public ObservableCollection<MatterFilterOption> MatterOptions { get; }
    public IReadOnlyList<MatterFilterOption> AppOptions { get; }        // "All apps" + AppKind names
    [ObservableProperty] string QueryText; string? MatterFilterId; string? AppFilterId;
    [ObservableProperty] DateTime? FromDate; DateTime? ToDate;
    [ObservableProperty] bool IsIndexing; bool ShowNoQuery; bool ShowNoResults;
    public IRelayCommand<SearchSnippetRow> OpenSnippetCommand { get; }
    public event Action<string, int, string>? OpenSnippetRequested;     // (sessionId, seq, term)
    public Task OnNavigatedToAsync();                                   // refresh matter facet options
    internal Task? PendingSearch { get; }                               // test seam
}
```

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\SearchPageViewModelTests.cs`:
```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SearchPageViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-search-page-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public SearchPageViewModelTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class RecordingErrors : IUiErrorReporter
    {
        public List<string> Reports { get; } = [];
        public void Report(string context, Exception ex) => Reports.Add(context + ": " + ex.Message);
        public void Info(string message) { }
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

    /// <summary>Pins the local zone to UTC so date-facet day boundaries are deterministic.</summary>
    private sealed class UtcZoneTimeProvider : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private async Task WriteSessionAsync(string id, string title, DateTimeOffset started,
        AppKind app = AppKind.Webex, string[]? matterIds = null, params string[] texts)
    {
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = app, StartedAtUtc = started, EndedAtUtc = started.AddMinutes(5),
            DurationMs = 300_000,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = title, MatterIds = matterIds ?? [] }, CancellationToken.None);
        var store = new TranscriptStore(_paths.TranscriptJsonl(id));
        for (int i = 0; i < texts.Length; i++)
            await store.AppendAsync(TranscriptLine.Segment(i, TranscriptSource.Local,
                i * 5000, i * 5000 + 1000, texts[i], "Me"), CancellationToken.None);
    }

    private async Task<(SearchPageViewModel Vm, SearchIndexService Index, RecordingErrors Errors)>
        MakeVmAsync(bool initialize = true)
    {
        var index = new SearchIndexService(_paths, () => new Settings(), TimeProvider.System,
            saveDebounceMs: 0);
        if (initialize) await index.InitializeAsync(CancellationToken.None);
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var errors = new RecordingErrors();
        var vm = new SearchPageViewModel(index, maintenance, errors, dispatch: a => a(),
            new UtcZoneTimeProvider(), debounceMs: 0);
        return (vm, index, errors);
    }

    [Fact]
    public async Task Query_produces_ranked_cards_with_clickable_snippet_rows()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-two", "Two hits", t, texts: new[] { "acme first line", "acme second line" });
        await WriteSessionAsync("s-one", "One hit", t.AddDays(1), texts: new[] { "acme only once" });
        var (vm, _, errors) = await MakeVmAsync();

        vm.QueryText = "acme";
        await (vm.PendingSearch ?? Task.CompletedTask);

        Assert.False(vm.ShowNoQuery);
        Assert.False(vm.ShowNoResults);
        Assert.Equal(new[] { "s-two", "s-one" }, vm.Results.Select(c => c.SessionId).ToArray());
        var card = vm.Results[0];
        Assert.Equal("Two hits", card.Title);
        Assert.Equal("Webex", card.App);
        Assert.Equal(2, card.Snippets.Count);
        var snip = card.Snippets[0];
        Assert.Contains("acme first line", snip.Snippet);
        Assert.Equal("acme", snip.MatchedTerm);
        Assert.Equal("[00:00]", snip.StampDisplay);
        Assert.Equal(0, snip.Seq);
        Assert.False(snip.MatchesOriginalOnly);
        Assert.DoesNotContain("(matches original text)", snip.SnippetDisplay);

        (string, int, string)? opened = null;
        vm.OpenSnippetRequested += (sid, seq, term) => opened = (sid, seq, term);
        vm.OpenSnippetCommand.Execute(snip);
        Assert.NotNull(opened);
        Assert.Equal(("s-two", 0, "acme"), opened!.Value);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Facets_narrow_by_matter_app_and_date_range()
    {
        var t = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        await new MatterStore(_paths.MattersDir).SaveAsync(
            new Matter { Id = "M-1", Name = "Acme Litigation" }, CancellationToken.None);
        await WriteSessionAsync("s-w", "Webex one", t, app: AppKind.Webex,
            matterIds: new[] { "M-1" }, texts: new[] { "shared term" });
        await WriteSessionAsync("s-t", "Teams one", t.AddDays(3), app: AppKind.Teams,
            matterIds: new[] { "M-2" }, texts: new[] { "shared term" });
        var (vm, _, _) = await MakeVmAsync();
        await vm.OnNavigatedToAsync();
        Assert.Contains(vm.MatterOptions, o => o.Id == "M-1");            // facet options from the index
        Assert.Contains(vm.AppOptions, o => o.Id == "Teams");             // AppKind names + "All apps"
        Assert.Contains(vm.AppOptions, o => o.Id is null);

        vm.QueryText = "shared";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal(2, vm.Results.Count);

        vm.AppFilterId = "Teams";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal("s-t", Assert.Single(vm.Results).SessionId);

        vm.AppFilterId = null;
        vm.MatterFilterId = "M-1";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal("s-w", Assert.Single(vm.Results).SessionId);

        vm.MatterFilterId = null;
        vm.FromDate = new DateTime(2026, 6, 3);            // UTC-pinned zone: day boundaries are UTC
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal("s-t", Assert.Single(vm.Results).SessionId);

        vm.FromDate = null;
        vm.ToDate = new DateTime(2026, 6, 1);              // "To" includes the whole picked day
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal("s-w", Assert.Single(vm.Results).SessionId);
    }

    [Fact]
    public async Task Empty_query_and_no_result_states()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "Alpha", t, texts: new[] { "hello world" });
        var (vm, _, _) = await MakeVmAsync();

        Assert.True(vm.ShowNoQuery);
        vm.QueryText = "zzzznothing";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.False(vm.ShowNoQuery);
        Assert.True(vm.ShowNoResults);
        Assert.Empty(vm.Results);

        vm.QueryText = "";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.True(vm.ShowNoQuery);
        Assert.False(vm.ShowNoResults);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task Indexing_state_clears_on_ReadyChanged_and_the_pending_query_reruns()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "Alpha", t, texts: new[] { "needle in here" });
        var (vm, index, _) = await MakeVmAsync(initialize: false);

        Assert.True(vm.IsIndexing);
        vm.QueryText = "needle";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Empty(vm.Results);                          // index not built yet
        Assert.False(vm.ShowNoResults);                    // "indexing..." is not "no results"

        await index.InitializeAsync(CancellationToken.None);   // fires ReadyChanged -> re-query
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.False(vm.IsIndexing);
        Assert.Single(vm.Results);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SearchPageViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\` — expected: `error CS0246: The type or namespace name 'SearchPageViewModel' could not be found`.
- [ ] **Implement the VM.** New file `src\LocalScribe.App\ViewModels\SearchPageViewModel.cs`:
```csharp
// src/LocalScribe.App/ViewModels/SearchPageViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Search;
namespace LocalScribe.App.ViewModels;

/// <summary>One snippet row on a search-result card (design 2026-07-13 section 2.2 surface 1):
/// timestamp + speaker + snippet, plus the "(matches original text)" label when the hit lives only
/// in the machine original. Seq/MatchedTerm are the click-through payload (open the read view
/// scrolled to the segment with the term in the find bar); Seq -1 = a speaker-name hit with no
/// spoken line (opens the read view without targeting).</summary>
public sealed record SearchSnippetRow(string SessionId, int Seq, string MatchedTerm, string Stamp,
    string Speaker, string Snippet, bool MatchesOriginalOnly)
{
    public string StampDisplay => Stamp.Length == 0 ? "" : "[" + Stamp + "]";
    public string SnippetDisplay => MatchesOriginalOnly
        ? Snippet + "  (matches original text)" : Snippet;
}

/// <summary>One session card: header fields + its snippet rows, in hit order.</summary>
public sealed record SearchResultCard(string SessionId, string Title, string DateDisplay,
    string App, string MattersDisplay, IReadOnlyList<SearchSnippetRow> Snippets);

/// <summary>Search page (design 2026-07-13 section 2.2 surface 1): debounced (~250 ms) live query
/// over SearchIndexService with matter/date-range/app facets; results as session cards holding
/// snippet rows; empty states for "no query yet" and "no results"; an "indexing..." state while the
/// cold-cache build runs (ReadyChanged re-runs the pending query the moment the index is up).
/// WPF-free; UI mutations marshal via the injected dispatch. Stamps are always session-relative
/// (mm:ss) - deterministic, independent of the wallclock timestamps setting.</summary>
public sealed partial class SearchPageViewModel : ObservableObject
{
    private readonly SearchIndexService _index;
    private readonly MaintenanceService _maintenance;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly int _debounceMs;
    private CancellationTokenSource? _searchCts;
    private readonly Dictionary<string, (string? Reference, string Name)> _matterLookup =
        new(StringComparer.Ordinal);

    public ObservableCollection<SearchResultCard> Results { get; } = [];
    public ObservableCollection<MatterFilterOption> MatterOptions { get; } = [];

    /// <summary>App facet: null = all. AppKind's names are the complete source-app vocabulary
    /// (SearchSessionEntry.App is AppKind.ToString()).</summary>
    public IReadOnlyList<MatterFilterOption> AppOptions { get; } =
        new[] { new MatterFilterOption(null, "All apps") }
            .Concat(Enum.GetNames<AppKind>().Select(n => new MatterFilterOption(n, n)))
            .ToList();

    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private string? _matterFilterId;
    [ObservableProperty] private string? _appFilterId;
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private bool _showNoQuery = true;
    [ObservableProperty] private bool _showNoResults;

    /// <summary>(sessionId, seq, matchedTerm) - the window layer opens/activates the ReadViewWindow
    /// and calls ShowFindAt(seq, term); seq &lt; 0 just opens.</summary>
    public event Action<string, int, string>? OpenSnippetRequested;
    public IRelayCommand<SearchSnippetRow> OpenSnippetCommand { get; }

    /// <summary>Test seam: the in-flight debounced query, if any.</summary>
    internal Task? PendingSearch { get; private set; }

    public SearchPageViewModel(SearchIndexService index, MaintenanceService maintenance,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time, int debounceMs = 250)
    {
        (_index, _maintenance, _errors, _dispatch, _time, _debounceMs)
            = (index, maintenance, errors, dispatch, time, debounceMs);
        OpenSnippetCommand = new RelayCommand<SearchSnippetRow>(row =>
        {
            if (row is not null) OpenSnippetRequested?.Invoke(row.SessionId, row.Seq, row.MatchedTerm);
        });
        IsIndexing = !index.IsReady;
        // ReadyChanged may fire from the background InitializeAsync: marshal, then re-run the
        // current query so a search typed during "indexing..." resolves the moment the index is up.
        // Both this VM and the index live for the app's lifetime - no unsubscribe needed.
        index.ReadyChanged += () => _dispatch(() =>
        {
            IsIndexing = !_index.IsReady;
            ScheduleSearch();
        });
    }

    partial void OnQueryTextChanged(string value) => ScheduleSearch();
    partial void OnMatterFilterIdChanged(string? value) => ScheduleSearch();
    partial void OnAppFilterIdChanged(string? value) => ScheduleSearch();
    partial void OnFromDateChanged(DateTime? value) => ScheduleSearch();
    partial void OnToDateChanged(DateTime? value) => ScheduleSearch();

    /// <summary>Page-navigation refresh: matter facet options from the matters index (degrading to
    /// an empty list on a fault - the raw-id facet still works), mirroring SessionsPage's rule that
    /// a secondary-index fault never blocks the page. Catches everything (Loaded is async void).</summary>
    public async Task OnNavigatedToAsync()
    {
        IsIndexing = !_index.IsReady;
        try
        {
            var matters = await _maintenance.ListMattersAsync(CancellationToken.None);
            _dispatch(() =>
            {
                _matterLookup.Clear();
                foreach (var m in matters.Matters) _matterLookup[m.Id] = (m.Reference, m.Name);
                string? current = MatterFilterId;
                MatterOptions.Clear();
                MatterOptions.Add(new MatterFilterOption(null, "All matters"));
                foreach (var m in matters.Matters)
                    MatterOptions.Add(new MatterFilterOption(m.Id, MatterLabel(m.Id)));
                if (current is not null && MatterOptions.All(o => o.Id != current))
                    MatterFilterId = null;              // stale selection -> All
                else if (MatterFilterId != current)
                    MatterFilterId = current;           // re-assert: a bound ComboBox can null on Clear()
            });
        }
        catch (Exception ex) { _errors.Report("Loading matters", ex); }
    }

    private void ScheduleSearch()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        PendingSearch = RunSearchAsync(cts.Token);
    }

    private async Task RunSearchAsync(CancellationToken ct)
    {
        try
        {
            if (_debounceMs > 0) await Task.Delay(_debounceMs, ct);
            string text = QueryText;
            bool hasQuery = !string.IsNullOrWhiteSpace(text);
            var query = new SearchQuery(text, MatterFilterId, FacetFromUtc(), FacetToUtc(), AppFilterId);
            IReadOnlyList<SearchResult> results = hasQuery
                ? await Task.Run(() => _index.Query(query), ct)
                : [];
            if (ct.IsCancellationRequested) return;
            _dispatch(() =>
            {
                if (ct.IsCancellationRequested) return;   // superseded by a newer keystroke/facet
                Results.Clear();
                foreach (var r in results) Results.Add(ToCard(r));
                ShowNoQuery = !hasQuery;
                ShowNoResults = hasQuery && Results.Count == 0 && !IsIndexing;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _errors.Report("Search", ex); }
    }

    private SearchResultCard ToCard(SearchResult r)
    {
        // Session-local date, same fallback rule as every other surface (spec 1.2).
        var startedLocal = r.Session.UtcOffsetMinutes is int offsetMin
            ? r.Session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : r.Session.StartedAtUtc.ToLocalTime();
        string matters = string.Join(", ", r.Session.MatterIds.Select(MatterLabel));
        var rows = r.Hits.Select(h => new SearchSnippetRow(
            r.Session.SessionId, h.Seq, h.MatchedTerm,
            h.Seq >= 0 ? TimestampFormat.Stamp(h.StartMs, "relative", startedLocal) : "",
            h.Speaker, h.Snippet, h.MatchesOriginalOnly)).ToList();
        return new SearchResultCard(r.Session.SessionId, r.Session.Title,
            startedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            r.Session.App, matters, rows);
    }

    /// <summary>`{id}-{ref} {name}` / `{id} {name}` / raw id - SessionsPageViewModel.MatterLabel's
    /// exact format, duplicated here because that one is private to its VM.</summary>
    private string MatterLabel(string id)
    {
        if (_matterLookup.TryGetValue(id, out var m))
            return m.Reference is { Length: > 0 } r ? $"{id}-{r} {m.Name}" : $"{id} {m.Name}";
        return id;
    }

    // Date facets: the picked day is interpreted in the viewer's zone (TimeProvider.LocalTimeZone,
    // test-pinnable); From = that day's start (inclusive), To = the NEXT day's start (exclusive
    // upper bound in SearchQueryEngine), so "To" includes the whole picked day.
    private DateTimeOffset? FacetFromUtc() => FromDate is { } d ? LocalDayStartUtc(d) : null;
    private DateTimeOffset? FacetToUtc() => ToDate is { } d ? LocalDayStartUtc(d.AddDays(1)) : null;

    private DateTimeOffset LocalDayStartUtc(DateTime day)
    {
        var local = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, _time.LocalTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SearchPageViewModel.cs tests/LocalScribe.App.Tests/SearchPageViewModelTests.cs
git commit -m "feat(app): SearchPageViewModel - debounced query, facets, ranked cards, click-through event

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: App — Search page XAML, nav-rail item, composition wiring, hygiene roots (final gate)
**Files:**
- New `src\LocalScribe.App\Pages\SearchPage.xaml` + `src\LocalScribe.App\Pages\SearchPage.xaml.cs`.
- Modify `src\LocalScribe.App\MainWindow.xaml` (nav item between Sessions and Matters).
- Modify `src\LocalScribe.App\MainWindow.xaml.cs` (`SectionPageType` switch).
- Modify `src\LocalScribe.App\App.xaml.cs` (index construction + `SessionSkipped` trace, `sessionsVm` searchIndex arg, `searchVm` + page-provider entry, snippet click-through, reindex wiring, background init after the startup scan, cross-branch retranscription/import wiring).
- Modify `tests\LocalScribe.App.Tests\XamlHygieneTests.cs` (add SearchPage.xaml to the foreground-marker roots).
- No new unit test (App.xaml.cs composition + XAML are not unit-tested). The gate: hygiene tests, 0-warning build, full App+Core suites, and the manual smoke below.

**Interfaces:**
- Consumes: `SearchIndexService` (Task 4), `SessionContentChanged` (Task 5), `ReadViewWindow.ShowFindAt` (Task 7), `SessionsPageViewModel(searchIndex:)` (Task 8), `SearchPageViewModel` + `OpenSnippetRequested` (Task 9), existing `comp.*`, `dispatch`, `_shutdownCts`, `orchestrator.ScanCompleted`, `StaticPageProvider`, cross-branch `RetranscriptionRunner.RetranscriptionCompleted` + the audio-import completion seam.
- Produces: no new public types beyond the page.

Steps:
- [ ] **Create the page code-behind.** New file `src\LocalScribe.App\Pages\SearchPage.xaml.cs`:
```csharp
using System.Windows.Controls;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App.Pages;

/// <summary>Thin code-behind for the Search page (design 2026-07-13 section 2.2 surface 1).
/// Constructed by the composition root with its VM (never via a navigation URI), mirroring
/// SessionsPage: Loaded refreshes the matter facet options; OnNavigatedToAsync catches all its
/// own exceptions, so the async-void Loaded lambda cannot throw.</summary>
public partial class SearchPage : Page
{
    private readonly SearchPageViewModel _vm;

    public SearchPage(SearchPageViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => _ = _vm.OnNavigatedToAsync();
    }
}
```
- [ ] **Create the page XAML.** New file `src\LocalScribe.App\Pages\SearchPage.xaml`:
```xml
<Page x:Class="LocalScribe.App.Pages.SearchPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
      xmlns:local="clr-namespace:LocalScribe.App"
      Title="Search">
    <Page.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
        <!-- Snippet buttons live inside a nested DataTemplate whose DataContext is the snippet row;
             the command lives on the PAGE VM - same Freezable-proxy route as SessionsPage. -->
        <local:BindingProxy x:Key="VmProxy" Data="{Binding}" />
    </Page.Resources>
    <Grid Margin="12" TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}">
        <DockPanel>
            <!-- Query box + facet row (design 2026-07-13 section 2.2 surface 1). -->
            <WrapPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
                <ui:TextBox Width="260" Margin="0,0,8,8" VerticalAlignment="Center"
                            PlaceholderText="Search all sessions..."
                            Text="{Binding QueryText, UpdateSourceTrigger=PropertyChanged}" />
                <ComboBox Width="200" Margin="0,0,8,8" VerticalAlignment="Center"
                          ItemsSource="{Binding MatterOptions}" DisplayMemberPath="Label"
                          SelectedValuePath="Id" SelectedValue="{Binding MatterFilterId}" />
                <ComboBox Width="140" Margin="0,0,8,8" VerticalAlignment="Center"
                          ItemsSource="{Binding AppOptions}" DisplayMemberPath="Label"
                          SelectedValuePath="Id" SelectedValue="{Binding AppFilterId}" />
                <TextBlock Text="From" VerticalAlignment="Center" Margin="0,0,4,8" />
                <DatePicker SelectedDate="{Binding FromDate}" Margin="0,0,8,8" VerticalAlignment="Center" />
                <TextBlock Text="To" VerticalAlignment="Center" Margin="0,0,4,8" />
                <DatePicker SelectedDate="{Binding ToDate}" Margin="0,0,8,8" VerticalAlignment="Center" />
                <TextBlock Text="indexing..." FontStyle="Italic" VerticalAlignment="Center" Margin="4,0,0,8"
                           Visibility="{Binding IsIndexing, Converter={StaticResource BoolToVis}}" />
            </WrapPanel>
            <Grid>
                <!-- Result cards: one per matched session, snippet rows inside (design 2.2). -->
                <ListView ItemsSource="{Binding Results}"
                          ScrollViewer.HorizontalScrollBarVisibility="Disabled"
                          VirtualizingPanel.IsVirtualizing="True"
                          VirtualizingPanel.VirtualizationMode="Recycling"
                          VirtualizingPanel.ScrollUnit="Pixel"
                          ScrollViewer.CanContentScroll="True">
                    <ListView.ItemContainerStyle>
                        <Style TargetType="ListViewItem">
                            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                            <Setter Property="Margin" Value="0,0,0,10" />
                            <Setter Property="Focusable" Value="False" />
                        </Style>
                    </ListView.ItemContainerStyle>
                    <ListView.ItemTemplate>
                        <DataTemplate>
                            <Border CornerRadius="6" Padding="10" BorderThickness="1"
                                    Background="{DynamicResource ControlFillColorDefaultBrush}"
                                    BorderBrush="{DynamicResource ControlStrokeColorDefaultBrush}">
                                <StackPanel>
                                    <WrapPanel Orientation="Horizontal">
                                        <TextBlock Text="{Binding Title}" FontWeight="SemiBold" Margin="0,0,12,2" />
                                        <TextBlock Text="{Binding DateDisplay}" Opacity="0.7" Margin="0,0,12,2" />
                                        <TextBlock Text="{Binding App}" Opacity="0.7" Margin="0,0,12,2" />
                                        <TextBlock Text="{Binding MattersDisplay}" Opacity="0.7" Margin="0,0,0,2" />
                                    </WrapPanel>
                                    <ItemsControl ItemsSource="{Binding Snippets}">
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate>
                                                <Button HorizontalAlignment="Stretch"
                                                        HorizontalContentAlignment="Stretch"
                                                        Background="Transparent" BorderThickness="0"
                                                        Padding="2" Cursor="Hand"
                                                        ToolTip="Open the transcript at this line"
                                                        Command="{Binding Data.OpenSnippetCommand, Source={StaticResource VmProxy}}"
                                                        CommandParameter="{Binding}">
                                                    <TextBlock TextWrapping="Wrap">
                                                        <Run Text="{Binding StampDisplay, Mode=OneWay}" />
                                                        <Run Text="{Binding Speaker, Mode=OneWay}" FontWeight="SemiBold" />
                                                        <Run Text="{Binding SnippetDisplay, Mode=OneWay}" />
                                                    </TextBlock>
                                                </Button>
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                </StackPanel>
                            </Border>
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>
                <!-- Empty states (design 2.2): no query yet / no results. -->
                <TextBlock HorizontalAlignment="Center" VerticalAlignment="Center" TextAlignment="Center"
                           Style="{StaticResource MutedText}"
                           Text="Search every session's transcript - corrected text, original text, and speaker names."
                           Visibility="{Binding ShowNoQuery, Converter={StaticResource BoolToVis}}" />
                <TextBlock HorizontalAlignment="Center" VerticalAlignment="Center" TextAlignment="Center"
                           Style="{StaticResource MutedText}"
                           Text="No matches. Try fewer words or different facets."
                           Visibility="{Binding ShowNoResults, Converter={StaticResource BoolToVis}}" />
            </Grid>
        </DockPanel>
    </Grid>
</Page>
```
- [ ] **Nav-rail item.** In `MainWindow.xaml`, between the Sessions and Matters items — replace:
```xml
                <ui:NavigationViewItem Content="Sessions" Tag="Sessions"
                                       TargetPageType="{x:Type pages:SessionsPage}">
                    <ui:NavigationViewItem.Icon>
                        <ui:SymbolIcon Symbol="History24" />
                    </ui:NavigationViewItem.Icon>
                </ui:NavigationViewItem>
                <ui:NavigationViewItem Content="Matters" Tag="Matters"
```
with:
```xml
                <ui:NavigationViewItem Content="Sessions" Tag="Sessions"
                                       TargetPageType="{x:Type pages:SessionsPage}">
                    <ui:NavigationViewItem.Icon>
                        <ui:SymbolIcon Symbol="History24" />
                    </ui:NavigationViewItem.Icon>
                </ui:NavigationViewItem>
                <!-- Cross-session search (design 2026-07-13 section 2.2): between Sessions and
                     Matters per the spec's nav order. -->
                <ui:NavigationViewItem Content="Search" Tag="Search"
                                       TargetPageType="{x:Type pages:SearchPage}">
                    <ui:NavigationViewItem.Icon>
                        <ui:SymbolIcon Symbol="Search24" />
                    </ui:NavigationViewItem.Icon>
                </ui:NavigationViewItem>
                <ui:NavigationViewItem Content="Matters" Tag="Matters"
```
- [ ] **Section mapping.** In `MainWindow.xaml.cs`, replace:
```csharp
    private static Type SectionPageType(string section) => section switch
    {
        "Matters" => typeof(Pages.MattersPage),
        "Settings" => typeof(Pages.SettingsPage),
        _ => typeof(Pages.SessionsPage),
    };
```
with:
```csharp
    private static Type SectionPageType(string section) => section switch
    {
        "Search" => typeof(Pages.SearchPage),
        "Matters" => typeof(Pages.MattersPage),
        "Settings" => typeof(Pages.SettingsPage),
        _ => typeof(Pages.SessionsPage),
    };
```
- [ ] **Construct the index at composition.** In `App.xaml.cs`, immediately after `var comp = CompositionRoot.Build();` insert:
```csharp

        // Cross-session search (design 2026-07-13 section 2): ONE in-memory index over the same
        // storage root, fed by the persisted self-healing cache. Built in the background after the
        // startup scan (step 7 below); queries before that see IsReady=false ("indexing...").
        // Construction does no IO. A skipped (unreadable) session is logged, never surfaced as an
        // error - it re-indexes on its next content change or the next launch.
        var searchIndex = new LocalScribe.Core.Search.SearchIndexService(
            comp.Paths, () => comp.Settings.Current, TimeProvider.System);
        searchIndex.SessionSkipped += (id, ex) => System.Diagnostics.Trace.WriteLine(
            $"search index skipped session {id}: {ex.Message}");
```
- [ ] **Thread the index into the Sessions VM.** Replace the `sessionsVm` construction's closing (quote the full statement when editing; only the last line changes):
```csharp
                System.Diagnostics.Process.Start("explorer.exe", dir);
            });
```
with:
```csharp
                System.Diagnostics.Process.Start("explorer.exe", dir);
            },
            searchIndex: searchIndex);
```
- [ ] **Wire the re-index seams.** Immediately after the existing line
```csharp
        comp.Controller.SessionFinalizeCompleted += id => dispatch(() => _ = sessionsVm.UpsertRowAsync(id));
```
insert:
```csharp
        // Search-index live updates (design 2026-07-13 section 2.1): a finalized recording and any
        // gated content mutation (edit save, pins, diarisation, recovery, re-render, version
        // switch, delete) re-index just that session. ReindexSessionAsync catches everything and
        // needs no dispatcher (the index is lock-guarded), so bare fire-and-forget is safe.
        comp.Controller.SessionFinalizeCompleted += id => _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);
        comp.Maintenance.SessionContentChanged += id => _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);
```
- [ ] **Search VM + page provider + click-through.** After the `mattersVm` construction insert:
```csharp
        var searchVm = new ViewModels.SearchPageViewModel(searchIndex, comp.Maintenance, errors,
            dispatch, TimeProvider.System);
```
In the `StaticPageProvider` dictionary, replace:
```csharp
                    [typeof(Pages.SessionsPage)] = new Pages.SessionsPage(sessionsVm),
                    [typeof(Pages.MattersPage)] = new Pages.MattersPage(mattersVm),
```
with:
```csharp
                    [typeof(Pages.SessionsPage)] = new Pages.SessionsPage(sessionsVm),
                    [typeof(Pages.SearchPage)] = new Pages.SearchPage(searchVm),
                    [typeof(Pages.MattersPage)] = new Pages.MattersPage(mattersVm),
```
Then, immediately after `sessionsVm.OpenReadViewRequested += openReadView;` insert (it must sit AFTER `openReadView`/`readViews` are declared — a lambda cannot reference a later local):
```csharp

        // Search-page click-through (design 2026-07-13 section 2.2): open or re-activate the read
        // view, then target the clicked hit's segment with its matched term so the window scrolls
        // there with the find bar showing the match. Seq < 0 = a speaker-name hit with no spoken
        // line - nothing to scroll to, so just open. Raised from OpenSnippetCommand on the UI
        // thread, so the readViews map read is safe here.
        searchVm.OpenSnippetRequested += (sessionId, seq, term) =>
        {
            openReadView(sessionId);
            if (seq >= 0 && readViews.TryGetValue(sessionId, out var window))
                window.ShowFindAt(seq, term);
        };
```
- [ ] **Background index build after the startup scan.** Immediately after the existing `orchestrator.ScanCompleted.ContinueWith(...)` block (the one clearing `IsScanning`) insert:
```csharp
        // Search-index build (design 2026-07-13 section 2.3): OFF the UI thread, after the recovery
        // scan so just-recovered sessions index in their finalized form. A cold cache shows the
        // Search page's "indexing..." state until IsReady flips (ReadyChanged re-runs any pending
        // query). Best-effort by design: per-session failures surface on SessionSkipped (Trace),
        // and a derived cache must never fault startup. No exit-time flush - the debounced write
        // plus the self-healing load cover an exit mid-debounce.
        _ = orchestrator.ScanCompleted.ContinueWith(async _ =>
        {
            try { await searchIndex.InitializeAsync(_shutdownCts.Token); }
            catch (OperationCanceledException) { }    // shutdown mid-build: self-heals next launch
            catch { }
        }, TaskScheduler.Default);
```
- [ ] **Cross-branch seams (bodies unknown at plan time — locate by grep, wire the same one line).**
  1. Retranscription: `grep -rn "RetranscriptionCompleted" src/LocalScribe.App src/LocalScribe.Core` — find where the composed `RetranscriptionRunner` instance lives (CompositionRoot member or an App.xaml.cs local from feat/retranscription-versions) and subscribe beside the other wiring above:
```csharp
        // Re-transcription completion re-indexes the session (its new version is now active).
        <runnerInstance>.RetranscriptionCompleted += id => _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);
```
  (If completion already flows through `MaintenanceService.SetActiveVersionAsync`, Task 5's wrapper makes this a harmless double re-index — wire it anyway; `ReindexSessionAsync` is idempotent.)
  2. Audio import: `grep -n "UpsertRowAsync" src/LocalScribe.App/App.xaml.cs` — the import-completion wiring from feat/audio-import calls `sessionsVm.UpsertRowAsync(id)` for the new session; add the same `_ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);` beside it. If the import flow instead finalizes through `SessionContentChanged`-raising MaintenanceService paths, no extra line is needed — verify by running an import smoke (item 7 below) and confirm the imported session is searchable without a restart.
- [ ] **Hygiene roots.** In `tests\LocalScribe.App.Tests\XamlHygieneTests.cs`, replace:
```csharp
            "MainWindow.xaml",
            Path.Combine("Pages", "SessionsPage.xaml"),
            Path.Combine("Pages", "MattersPage.xaml"),
```
with:
```csharp
            "MainWindow.xaml",
            Path.Combine("Pages", "SessionsPage.xaml"),
            Path.Combine("Pages", "SearchPage.xaml"),
            Path.Combine("Pages", "MattersPage.xaml"),
```
- [ ] **Build 0-warning + BOTH full suites green.**
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\session-search\
```
Expected: build 0 warnings; App suite all green incl. `XamlHygieneTests` (SearchPage.xaml carries the `TextElement.Foreground` marker on its root Grid, uses only Fluent theme resources — no ARGB literals, no keyless app-global TextBlock style); Core green (2 known fixture fails pre-existing).
- [ ] **Manual smoke (WPF — the composition is not unit-testable).** Launch the app:
  1. Nav rail order reads Record, Sessions, **Search**, Matters (+ Settings in the footer); clicking Search opens the page with the query box, matter/app dropdowns, From/To date pickers, and the "no query yet" empty state.
  2. Cold cache: close the app, delete `<storageRoot>\index\search-index.json`, relaunch, open Search immediately → "indexing..." shows briefly, then typing a word spoken in an old session produces cards with `[mm:ss] Speaker snippet` rows; the UI never blocks.
  3. Click a snippet row → the read view opens scrolled to that segment, find bar open on the matched term, row highlighted. Click a second snippet from the SAME session → the existing window re-targets (no duplicate window).
  4. Facets: pick a matter → only its sessions remain; pick an app; set From/To dates; a nonsense query shows "No matches...".
  5. Sessions page: type a spoken-only word into "Filter sessions..." → after ~¼ s the matching session's row appears with an italic snippet line under the title; clearing the box removes the snippet lines and restores the full list; title filtering is otherwise unchanged.
  6. Evidentiary rule: in a read view, correct a word (Edit → change → Save). Search the OLD word → the hit shows with "(matches original text)"; search the NEW word → a normal hit. Nothing was hidden.
  7. Live update: record a short session, Stop; once the row flips from "Finalizing...", search a word you spoke → it hits WITHOUT a restart or manual refresh. (If the audio-import feature is merged: import a small file and confirm it becomes searchable the same way.)
  8. Restart the app → Search is warm (no visible "indexing..." beyond a blink; the cache was reused).
- [ ] **Commit.**
```
git add src/LocalScribe.App/Pages/SearchPage.xaml src/LocalScribe.App/Pages/SearchPage.xaml.cs src/LocalScribe.App/MainWindow.xaml src/LocalScribe.App/MainWindow.xaml.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/XamlHygieneTests.cs
git commit -m "feat(app): Search page + nav item + search-index composition and live-update wiring

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-review

**(a) Spec coverage — every design §2 requirement maps to a task:**
- §2.1 `SearchIndexService` in-memory index, per-line entries (segment id, timestamp, corrected text, original machine text only where a correction differs, speaker name) + session-level fields (id, title, matter ids, date, source app) + active-version indexing → **Task 1** (models), **Task 3** (derivation via `SessionProjectionLoader`, so read view/export/search share one pipeline and version-following), **Task 4** (service).
- §2.1 `SearchIndexStore` persisted cache at `<storageRoot>\index\search-index.json`, AtomicFile, schema-stamped, freshness stamps (active version's transcript/edits/speakers + root meta + active-version id), self-heal on load (stale/missing re-derived, orphans dropped, corrupt → silent full rebuild) → **Task 1** (store + stamps schema), **Task 3** (`ComputeStamps`), **Task 4** (`InitializeAsync` self-heal, proven by the tamper/stale/orphan/corrupt tests).
- §2.1 incremental updates on the live-update seams + debounced cache rewrite → **Task 5** (`SessionContentChanged` on meta save, archive, corrections, splits, pins, diarisation, recovery, re-render, version switch, delete), **Task 4** (`ReindexSessionAsync` + debounce + `FlushAsync`), **Task 10** (wiring: finalize event, content event, retranscription event, import seam, background init after the recovery scan).
- §2.1 query semantics (case-insensitive substring over corrected text + original machine text + speaker names incl. participants; multi-word AND per session; rank by hit count then recency; snippet ±60 chars around the first hit in a line; speaker-name-only matches snippet the speaker's first line; original-only labelled; matter/date-range/app facets) → **Task 2**, one test per rule.
- §2.2 surface 1 Search page (nav item between Sessions and Matters, query box, facet row, session cards with snippet rows, click opens ReadViewWindow scrolled + highlighted, both empty states, off-UI-thread build + "indexing…" state) → **Task 9** (VM + states + click event), **Task 10** (XAML, nav, wiring), **Task 7** (`ShowFindAt` targeting).
- §2.2 surface 2 Sessions quick filter (index consult debounced ~250 ms, one snippet line under matched rows, title/metadata filtering unchanged) → **Task 8** (`PassesFilters` OR-branch is the ONLY filter change; the no-index composition is regression-tested).
- §2.2 surface 3 Ctrl+F find bar (match count "2/7", Enter/Shift+Enter/Esc + buttons, highlighted matches, auto-scroll to current, visible corrected text of the loaded version only) → **Task 6** (VM state), **Task 7** (bar UI, keys, tints, scroll).
- §2.3 errors & state (unreadable session skipped + logged, never blocking; index off the UI thread; cold-cache "indexing…") → **Task 4** (`SessionSkipped`, skip test), **Task 10** (Trace wiring, post-scan background build), **Task 9** (IsIndexing/ReadyChanged test).
- §1/§7 binding rules: no task writes session data (the sole write target is the derived cache under `index\`); original-text matches labelled in the engine (Task 2), Sessions snippet (Task 8 `FormatContentSnippet`), and Search cards (Task 9 `SnippetDisplay`); nothing hides content — the dedup exemption for corrected lines already lives upstream in the shared projection this index derives from.
- Deliberate, documented interpretation points (called out in-plan where they bite): row-granular (not substring-granular) highlight tints; Ctrl+F searches ALL visible row text incl. markers (find-on-page; the marker-text non-goal binds the cross-session INDEX, whose builder excludes markers); `OriginalText` stored only for edits.json corrections (spec letter), so vocabulary-pass originals are not searchable; vocabulary changes without a re-render leave entries stale by the design's chosen stamp set — mitigated by re-indexing on the re-render seam; speaker-name hits for never-spoke participants carry Seq -1 (open-without-target); search-page stamps are always relative.

**(b) Placeholder scan:** no TBD / "add error handling" / "similar to Task N" — every step carries full test code, full implementation code, and quoted current-code anchors read from master @ 7d6c88d (`StoragePaths.cs` MatterJson block, `MaintenanceService.cs` per-method bodies, `SessionsPageViewModel.cs` ctor/ApplyFilters/PassesFilters/OnFilterTextChanged/UpsertRowAsync, `SessionRowViewModel.cs` class header, `ReadRow.cs` IsNowPlaying, `ReadViewViewModel.cs` EnterEditMode/ApplyRows, `ReadViewWindow.xaml` header-end + IsNowPlaying trigger, `ReadViewWindow.xaml.cs` commands/ctor/Loaded/OnClosed, `SessionsPage.xaml` Title column, `MainWindow.xaml` nav items, `MainWindow.xaml.cs` SectionPageType, `App.xaml.cs` comp/sessionsVm/finalize-wiring/page-provider/openReadView/ScanCompleted, `XamlHygieneTests.cs` roots). The three knowingly body-unknown cross-branch edits (`SetActiveVersionAsync` wrap, `RetranscriptionCompleted` subscription, import-completion line) are specified as exact, body-agnostic transformations with grep locators and fallbacks — the honest maximum available at plan time, flagged for the executor.
**(c) Type consistency across tasks:** `SearchSessionEntry`/`SearchLine`/`SearchFreshnessStamps`/`SearchIndexCache`/`SearchQuery`/`SearchHit`/`SearchResult` (Task 1) are consumed byte-identically by Tasks 2–4 and 8–9; `SearchIndexStore.LoadAsync : Task<SearchIndexCache?>` (Task 1) ↔ Task 4's null-means-rebuild; `SearchIndexBuilder.BuildEntryAsync(StoragePaths, Settings, TimeProvider, string, CancellationToken)` + `ComputeStamps(StoragePaths, string, string)` (Task 3) ↔ Task 4's calls; `SearchIndexService` ctor `(StoragePaths, Func<Settings>, TimeProvider, int saveDebounceMs = 2000)` ↔ App wiring `(comp.Paths, () => comp.Settings.Current, TimeProvider.System)` (Task 10) and test `saveDebounceMs: 0` (Tasks 4/8/9); `Query(SearchQuery) : IReadOnlyList<SearchResult>` ↔ Tasks 8/9; `SessionContentChanged : event Action<string>` (Task 5) ↔ Task 10's `id => _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token)`; `ReadViewWindow.ShowFindAt(int, string)` (Task 7) ↔ Task 9/10's `(string, int, string)` OpenSnippet payload (seq ≥ 0 guard at the call site); `SessionsPageViewModel` optional params keep every existing call site compiling (verified: `SessionsPageViewModelTests`, `SessionsPageMatterFilterSearchTests`, `SessionsPageMatterLabelsTests`, `DeleteFlowTests`, and App.xaml.cs — the last updated in Task 10); `SessionRowViewModel : ObservableObject` is additive (no existing member changed); wrapped `MaintenanceService` methods keep their exact public signatures (`Task`/`Task<bool>`/`Task<IReadOnlyDictionary<string,string>>`), with the one deliberate inner-local rename (`changed` → `wrote`) called out where the wrapper would otherwise collide. All good.
