# Stage 4 Implementation Plan: Main Window - Session/Matter Manager, Settings, Consent

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver LocalScribe's first main window - the two-level Matter/Session
manager, metadata editor, full settings UI, session read view with audio
playback, first-run consent notice, startup recovery scan, and matters-index
self-heal - per the approved design in
`docs/plans/2026-07-03-stage-4-manager-design.md` (binding; read it first).

**Architecture:** Core (LocalScribe.Core) gains the query/maintenance layer the
UI needs: session enumeration (SessionCatalog), index rebuild
(MattersIndexRebuilder), recovery scan (RecoveryScanner), recycle-bin deleters,
id generators, and a Func<Settings> seam so recording settings resolve at
Start. The App layer gains a NavigationView MainWindow (Sessions / Matters /
Settings pages), per-session ReadViewWindows, and two services all disk
mutation funnels through: MaintenanceService (per-session single-flight +
serialized index writes) and SettingsService (the app's first settings
mutation path). All ViewModels stay WPF-free per 3b conventions.

**Tech Stack:** net10.0-windows WPF, WPF-UI 4.0.3 (FluentWindow/Mica/
NavigationView/InfoBar), H.NotifyIcon 2.3.0, CommunityToolkit.Mvvm 8.4.0,
xunit. No new NuGet packages (recycle bin via SHFileOperationW P/Invoke; audio
via in-box System.Windows.Media.MediaPlayer).

## Global Constraints

Every task's requirements implicitly include all of these (from the design
spec and the Stage 3b conventions):

- ViewModels and Services are WPF-free: no `System.Windows.*` in
  `src/LocalScribe.App/ViewModels/` or `src/LocalScribe.App/Services/`;
  UI marshaling only via injected `Action<Action>` dispatch.
- Time only via injected `TimeProvider` - never `DateTime.Now`/`UtcNow` in
  code under test. Timers are `Tick()` methods driven by the production
  DispatcherTimer; tests call `Tick()` directly.
- ASCII-only source including comments; glyphs as `\u` escapes. No Unicode
  emojis anywhere (binding user rule, especially test scripts).
- Tests: `[UNIT]`-style xunit, no STA thread required, temp-dir-per-test-class
  with IDisposable cleanup, `SpinWait.SpinUntil` on observable effects (never
  `Thread.Sleep`). PR gate: `dotnet test --filter "Category!=Fixture"` green.
- 0-warning builds (`dotnet build` reports 0 code warnings). Conventional
  commits (`feat:` / `test:` / `fix:` / `docs:`).
- Evidentiary invariants: `transcript.jsonl` is never rewritten; user edits
  touch only `meta.json` / `matter.json`; no content delete/hide/redact
  anywhere; whole-session folder delete to the Recycle Bin is the only
  session-data deletion; metadata saves never flip `Edited`/`LastEditedAtUtc`.
- Schema changes are additive; readers reject-higher/migrate-lower via the
  existing SchemaGuard pattern. window-state.json is exempt (unversioned
  throwaway UI state).
- Recording stays matter-agnostic: nothing is ever required before Start.
- Not in scope (design section 1.2): full-text search, correction editing,
  vocabulary UI, export, global hotkeys, folder renames, storage-root
  migration, transcript-synced audio, auto-detect, diarisation UI.

## File Structure

New Core files (`src/LocalScribe.Core/`):

| File | Responsibility |
|---|---|
| `Storage/SessionCatalog.cs` | Enumerate session folders -> `SessionCatalogResult` (newest-first, unreadable counted, migration-tolerant) |
| `Storage/MattersIndexRebuilder.cs` | Full index rebuild (orphan adoption, count recompute) + incremental tag deltas |
| `Storage/RecoveryScanner.cs` | Find sessions with `endedAtUtc == null` for the launch scan |
| `Storage/IRecycleBin.cs` | Recycle-bin seam (interface only in Core) |
| `Storage/SessionDeleter.cs` | Whole-session folder delete via `IRecycleBin` |
| `Storage/MatterDeleter.cs` | Reference-count check + blocked-while-referenced matter delete |
| `Storage/MatterIdGenerator.cs` | `M-{yyyy}-{NNN}` minting (index + folder collision-safe) |
| `Storage/ParticipantId.cs` | `p-{slug}` minting with `-2`/`-3` collision suffixes |
| `Live/AppKindResolver.cs` | Process image -> `AppKind` mapping for manual-start derivation |

Modified Core files: `Model/SessionMeta.cs`, `Model/Matter.cs`,
`Model/MattersIndex.cs`, `Model/Settings.cs` (+ `ConsentSetting`,
`PrivacySetting`), `Storage/MetadataStore.cs` (v2), `Storage/MatterStore.cs`
(v2), `Storage/SettingsStore.cs` (v3), `Storage/SettingsMigrator.cs`,
`Live/SessionController.cs` + `Audio/WasapiCaptureSourceProvider.cs`
(`Func<Settings>` seam).

New App files (`src/LocalScribe.App/`):

| File | Responsibility |
|---|---|
| `Services/MaintenanceService.cs` | ALL disk mutation from the UI: per-session single-flight queue, serialized index ops, regen/cascade/recover/delete |
| `Services/SettingsService.cs` | `ISettingsService`: current settings + save + change events |
| `Services/SingleInstance.cs` | Named mutex + activation signal |
| `Services/WindowRegistry.cs` | Open read-view tracking (close-before-delete) |
| `Services/IUiErrorReporter.cs` | Error surfacing seam (InfoBar + tray balloon impls) |
| `Services/ShellRecycleBin.cs` | `IRecycleBin` via SHFileOperationW (FOF_ALLOWUNDO) |
| `Services/IDualAudioPlayer.cs` | Audio transport seam (VM-testable) |
| `Services/RegistryLaunchAtLogin.cs` | HKCU Run key wiring for launchAtLogin |
| `CaptureExclusion.cs` | Apply WDA_EXCLUDEFROMCAPTURE per privacy setting |
| `DualMediaPlayer.cs` | Two synchronized `MediaPlayer`s behind `IDualAudioPlayer` |
| `MainWindow.xaml(.cs)` | FluentWindow + NavigationView shell (closable, geometry-remembered, capture-excluded) |
| `Pages/SessionsPage.xaml(.cs)` | Session list + detail pane host |
| `Pages/MattersPage.xaml(.cs)` | Matter list + editor + roster + tagged sessions |
| `Pages/SettingsPage.xaml(.cs)` | Grouped settings UI |
| `ReadViewWindow.xaml(.cs)` | Per-session read-only transcript + audio transport |
| `ConsentDialog.xaml(.cs)` | First-run consent notice (modal, pre-tray) |
| `ViewModels/MainWindowViewModel.cs` | Navigation + InfoBar message queue |
| `ViewModels/SessionsPageViewModel.cs` + `SessionRowViewModel.cs` | List, filters, badges, refresh triggers |
| `ViewModels/MetadataEditorViewModel.cs` | Auto-save metadata editor + gates |
| `ViewModels/MattersPageViewModel.cs` (+ editor VM) | Matter CRUD, roster, cascade triggers |
| `ViewModels/ReadViewViewModel.cs` | Projection-parity transcript rows + badges |
| `ViewModels/PlaybackViewModel.cs` | Dual-leg transport state over `IDualAudioPlayer` |
| `ViewModels/SettingsPageViewModel.cs` | Settings groups -> `ISettingsService.SaveAsync` |
| `ViewModels/ConsentViewModel.cs` | Accept/decline consent flow |

Modified App files: `App.xaml.cs` (startup order: single-instance -> consent ->
composition -> tray -> background scan), `CompositionRoot.cs` (returns
`ISettingsService`), `TrayIconHost.cs` (Open LocalScribe entry, double-click
retarget), `LiveViewWindow.xaml.cs` (capture exclusion),
`OverlayWindow.xaml.cs` (keyed WindowStateStore),
`ViewModels/WindowStateStore.cs` (keyed schema).

Docs: `docs/specs/localscribe-specs.md` (five queued amendments, design
section 10), `docs/plans/2026-07-03-stage-4-smoke-runbook.md` (C1-C9).

## Task Order and Dependencies

Tasks 1-8 (Core) are independent of the App tasks and mostly of each other;
implement in order anyway. Task 9 (MaintenanceService) consumes Tasks 4-7.
Task 10 (settings seam) touches Core + CompositionRoot. Tasks 11-14 build the
shell; 15-17 the Sessions page; 18-20 Matters + read view + audio; 21-22
settings + consent; 23-24 startup wiring; 25 docs. Each task ends green:
`dotnet build` 0 warnings + `dotnet test --filter "Category!=Fixture"` pass.

---

### Task 1: Archived flags: meta.json v2, matter.json v2, matters index v2

**Files:**
- Modify: `src/LocalScribe.Core/Model/SessionMeta.cs` (record body, ~line 9 + after line 21)
- Modify: `src/LocalScribe.Core/Storage/MetadataStore.cs` (whole file - Version const + write-migrating LoadAsync)
- Modify: `src/LocalScribe.Core/Model/Matter.cs` (~line 15 + after line 22)
- Modify: `src/LocalScribe.Core/Model/MattersIndex.cs` (~line 6, entry record ~line 15)
- Modify: `src/LocalScribe.Core/Storage/MatterStore.cs` (whole file - Version const + write-migrating LoadAsync + Archived in UpsertIndexAsync)
- Test: `tests/LocalScribe.Core.Tests/MetadataStoreTests.cs` (extend), `tests/LocalScribe.Core.Tests/MatterStoreTests.cs` (extend)

**Interfaces:**
- Consumes: existing `SchemaGuard.ReadObjectAsync/ReadVersion/RejectIfNewer`, `JsonFile.ReadAsync/WriteAsync` (src/LocalScribe.Core/Storage/), existing record shapes read above.
- Produces (later tasks compile against these exactly): `SessionMeta.Archived : bool` (init), `Matter.Archived : bool` (init), `MattersIndexEntry.Archived : bool` (init), `MetadataStore.Version == 2`, `MatterStore.Version == 2`. `MetadataStore.LoadAsync` / `MatterStore.LoadAsync` write-migrate v1 files to v2 on load (SessionCatalog, MaintenanceService, and the Matters page rely on migrate-on-read).

#### Cycle A: meta.json v2

- [ ] **Write failing tests** - append these three tests to `tests/LocalScribe.Core.Tests/MetadataStoreTests.cs`, inside `public class MetadataStoreTests`, immediately before the existing `private static void CleanParent` helper:

```csharp
    [Fact]
    public async Task Archived_roundtrips_at_v2()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "meta.json");
        try
        {
            var store = new MetadataStore(path);
            await store.SaveAsync(new SessionMeta { Title = "Archived one", Archived = true }, default);

            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"schemaVersion\": 2", json);
            Assert.Contains("\"archived\": true", json);

            var back = await store.LoadAsync(default);
            Assert.True(back!.Archived);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task V1_meta_loads_with_archived_false_and_rewrites_at_v2()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "meta.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"title\":\"Old intake\"}");

            var back = await new MetadataStore(path).LoadAsync(default);
            Assert.False(back!.Archived);                   // missing field -> false
            Assert.Equal(2, back.SchemaVersion);
            Assert.Equal("Old intake", back.Title);         // v1 content preserved

            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"schemaVersion\": 2", json);  // write-migrated on load
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Rejects_newer_meta_version()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "meta.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":3}");
            await Assert.ThrowsAsync<NotSupportedException>(
                () => new MetadataStore(path).LoadAsync(default));
        }
        finally { CleanParent(path); }
    }
```

- [ ] **Run and see it fail**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~MetadataStoreTests"` - expect build failure `CS0117: 'SessionMeta' does not contain a definition for 'Archived'` (and CS1061 on `back.Archived`).

- [ ] **Implement**: in `src/LocalScribe.Core/Model/SessionMeta.cs` change line 9 from `public int SchemaVersion { get; init; } = 1;` to `public int SchemaVersion { get; init; } = 2;`, and insert after line 21 (`public DateTimeOffset? LastEditedAtUtc { get; init; }`):

```csharp
    /// <summary>v2 (Stage 4): hidden from default views only - nothing leaves disk (design 1).</summary>
    public bool Archived { get; init; }
```

Then replace the entire contents of `src/LocalScribe.Core/Storage/MetadataStore.cs` with:

```csharp
using LocalScribe.Core.Model;

namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes meta.json (spec section 1.4) - the only file user edits touch.
/// v2 adds archived (additive); a v1 file loads with Archived=false and is write-migrated
/// to v2 on load (reject-higher/migrate-lower, schema-version policy).</summary>
public sealed class MetadataStore
{
    public const int Version = 2;
    private readonly string _path;
    public MetadataStore(string metaJsonPath) => _path = metaJsonPath;

    public Task SaveAsync(SessionMeta meta, CancellationToken ct)
        => JsonFile.WriteAsync(_path, meta with { SchemaVersion = Version }, ct);

    public async Task<SessionMeta?> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;
        int v = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(v, Version, "meta.json");
        var meta = await JsonFile.ReadAsync<SessionMeta>(_path, ct);
        if (meta is not null && v < Version)
        {
            meta = meta with { SchemaVersion = Version };
            await SaveAsync(meta, ct);      // write-migrate: additive fields stay at defaults
        }
        return meta;
    }
}
```

- [ ] **Run to green**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~MetadataStoreTests"` - expect all MetadataStoreTests PASS (6 tests).
- [ ] **Commit**: `git add src/LocalScribe.Core/Model/SessionMeta.cs src/LocalScribe.Core/Storage/MetadataStore.cs tests/LocalScribe.Core.Tests/MetadataStoreTests.cs && git commit -m "feat: meta.json v2 - additive archived flag, v1 write-migrates on load"`

#### Cycle B: matter.json v2 + matters.json v2

- [ ] **Write failing tests** - append these four tests to `tests/LocalScribe.Core.Tests/MatterStoreTests.cs`, inside `public class MatterStoreTests`, immediately before the existing `private static void CleanRoot` helper:

```csharp
    [Fact]
    public async Task Archived_roundtrips_into_matter_and_index_entry()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            var store = new MatterStore(dir);
            await store.CreateAsync(Sample() with { Archived = true });

            var loaded = await store.LoadAsync("M-2026-014");
            Assert.True(loaded!.Archived);
            Assert.Equal(2, loaded.SchemaVersion);

            var index = await store.ListAsync();
            Assert.Single(index.Matters);
            Assert.True(index.Matters[0].Archived);          // carried into the index entry

            await store.SaveAsync(Sample() with { Archived = false });
            index = await store.ListAsync();
            Assert.False(index.Matters[0].Archived);         // un-archive propagates too
        }
        finally { CleanRoot(dir); }
    }

    [Fact]
    public async Task V1_matter_loads_with_archived_false_and_rewrites_at_v2()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            string matterDir = Path.Combine(dir, "M-2025-001");
            Directory.CreateDirectory(matterDir);
            await File.WriteAllTextAsync(Path.Combine(matterDir, "matter.json"),
                "{\"schemaVersion\":1,\"id\":\"M-2025-001\",\"name\":\"Old matter\"}");

            var store = new MatterStore(dir);
            var back = await store.LoadAsync("M-2025-001");
            Assert.False(back!.Archived);                    // missing field -> false
            Assert.Equal(2, back.SchemaVersion);
            Assert.Equal("Old matter", back.Name);

            string json = await File.ReadAllTextAsync(Path.Combine(matterDir, "matter.json"));
            Assert.Contains("\"schemaVersion\": 2", json);   // write-migrated on load

            var index = await store.ListAsync();             // migration save also upserts the index
            Assert.Single(index.Matters);
            Assert.False(index.Matters[0].Archived);
        }
        finally { CleanRoot(dir); }
    }

    [Fact]
    public async Task V1_index_loads_entries_with_archived_false()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "matters.json"),
                "{\"schemaVersion\":1,\"matters\":[{\"id\":\"M-2025-001\",\"name\":\"Old\",\"sessionCount\":3}]}");

            var index = await new MatterStore(dir).ListAsync();
            Assert.Single(index.Matters);
            Assert.False(index.Matters[0].Archived);
            Assert.Equal(3, index.Matters[0].SessionCount);
        }
        finally { CleanRoot(dir); }
    }

    [Fact]
    public async Task Rejects_newer_matter_version()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "matters");
        try
        {
            string matterDir = Path.Combine(dir, "M-2026-001");
            Directory.CreateDirectory(matterDir);
            await File.WriteAllTextAsync(Path.Combine(matterDir, "matter.json"), "{\"schemaVersion\":3}");
            await Assert.ThrowsAsync<NotSupportedException>(
                () => new MatterStore(dir).LoadAsync("M-2026-001"));
        }
        finally { CleanRoot(dir); }
    }
```

- [ ] **Run and see it fail**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~MatterStoreTests"` - expect build failure `CS0117: 'Matter' does not contain a definition for 'Archived'` (and CS1061 on `Matters[0].Archived`).

- [ ] **Implement**: in `src/LocalScribe.Core/Model/Matter.cs` change line 15 from `public int SchemaVersion { get; init; } = 1;` to `public int SchemaVersion { get; init; } = 2;` and insert after line 22 (`public Vocabulary Vocabulary { get; init; } = new();`):

```csharp
    /// <summary>v2 (Stage 4): hidden from default lists only; does NOT cascade to sessions (design 4.1).</summary>
    public bool Archived { get; init; }
```

In `src/LocalScribe.Core/Model/MattersIndex.cs` change line 6 from `public int SchemaVersion { get; init; } = 1;` to `public int SchemaVersion { get; init; } = 2;` and insert after `public int SessionCount { get; init; }` in `MattersIndexEntry`:

```csharp
    /// <summary>v2 (Stage 4): mirrors matter.json Archived for list rendering (design section 8).</summary>
    public bool Archived { get; init; }
```

Then replace the entire contents of `src/LocalScribe.Core/Storage/MatterStore.cs` with:

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>CRUD over matters (spec section 1.5). Owns matter.json files and the matters.json index.
/// v2 adds archived on matter + index entry (additive; v1 reads as false, matter.json
/// write-migrates on load). SessionCount is persisted as given; recompute against session
/// matterIds is Stage 4's MattersIndexRebuilder. matter.json + index are two atomic writes
/// with a crash window in between: a matter can be missing from ListAsync until its next
/// save. The Stage 4 index rebuild is the self-heal.</summary>
public sealed class MatterStore
{
    public const int Version = 2;
    private readonly string _mattersDir;
    public MatterStore(string mattersDir) => _mattersDir = mattersDir;

    private string IndexPath => Path.Combine(_mattersDir, "matters.json");
    private string MatterPath(string id) => Path.Combine(_mattersDir, id, "matter.json");

    public Task CreateAsync(Matter matter, CancellationToken ct = default) => SaveAsync(matter, ct);

    public async Task SaveAsync(Matter matter, CancellationToken ct = default)
    {
        await JsonFile.WriteAsync(MatterPath(matter.Id), matter with { SchemaVersion = Version }, ct);
        await UpsertIndexAsync(matter, ct);
    }

    public async Task<Matter?> LoadAsync(string matterId, CancellationToken ct = default)
    {
        var obj = await SchemaGuard.ReadObjectAsync(MatterPath(matterId), ct);
        if (obj is null) return null;
        int v = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(v, Version, "matter.json");
        var matter = await JsonFile.ReadAsync<Matter>(MatterPath(matterId), ct);
        if (matter is not null && v < Version)
        {
            matter = matter with { SchemaVersion = Version };
            await SaveAsync(matter, ct);    // write-migrate; SaveAsync also (re)upserts the index entry
        }
        return matter;
    }

    public async Task<MattersIndex> ListAsync(CancellationToken ct = default)
    {
        var obj = await SchemaGuard.ReadObjectAsync(IndexPath, ct);
        if (obj is null) return new MattersIndex();
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "matters.json");
        // No write-migration here: a v1 index reads with Archived=false and is rewritten
        // at v2 by the next upsert or the Stage 4 rebuild - ListAsync stays read-only.
        return await JsonFile.ReadAsync<MattersIndex>(IndexPath, ct) ?? new MattersIndex();
    }

    private async Task UpsertIndexAsync(Matter matter, CancellationToken ct)
    {
        var index = await ListAsync(ct);
        var entries = index.Matters.ToList();
        int existing = entries.FindIndex(e => e.Id == matter.Id);
        var entry = new MattersIndexEntry
        {
            Id = matter.Id,
            Name = matter.Name,
            Reference = matter.Reference,
            SessionCount = existing >= 0 ? entries[existing].SessionCount : 0,
            Archived = matter.Archived,
        };
        if (existing >= 0) entries[existing] = entry; else entries.Add(entry);
        await JsonFile.WriteAsync(IndexPath, index with { SchemaVersion = Version, Matters = entries }, ct);
    }
}
```

- [ ] **Run to green**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~MatterStoreTests"` - expect all MatterStoreTests PASS (7 tests).
- [ ] **Full gate**: `dotnet test --filter "Category!=Fixture"` - expect PASS, 0 warnings (guards against anything else asserting the old versions).
- [ ] **Commit**: `git add src/LocalScribe.Core/Model/Matter.cs src/LocalScribe.Core/Model/MattersIndex.cs src/LocalScribe.Core/Storage/MatterStore.cs tests/LocalScribe.Core.Tests/MatterStoreTests.cs && git commit -m "feat: matter.json + matters index v2 - archived flag carried into index entries"`

---

### Task 2: Settings v3: consentNotice + privacy

**Files:**
- Modify: `src/LocalScribe.Core/Model/Settings.cs` (SchemaVersion default line 7, two new properties, two new records at end)
- Modify: `src/LocalScribe.Core/Storage/SettingsStore.cs` (line 9: `Version = 2` -> `3`; doc comment line 5)
- Modify: `src/LocalScribe.Core/Storage/SettingsMigrator.cs` (add v2->v3 block after the v1 block)
- Test: `tests/LocalScribe.Core.Tests/SettingsTests.cs` (existing tests updated for v3 + two new tests; full replacement below)

**Interfaces:**
- Consumes: `SchemaGuard`, `JsonFile`, `LocalScribeJson.Options`, existing `SettingsStore.LoadOrDefaultAsync` migrate-and-resave flow (SettingsStore.cs:16-30).
- Produces (locked - ISettingsService/consent dialog/privacy toggle tasks compile against these exactly):

```csharp
// on Settings:
public ConsentSetting? ConsentNotice { get; init; }
public PrivacySetting Privacy { get; init; } = new();
// new records (src/LocalScribe.Core/Model/Settings.cs):
public sealed record ConsentSetting { public DateTimeOffset AcknowledgedAtUtc { get; init; } public string AppVersion { get; init; } = ""; }
public sealed record PrivacySetting { public bool ExcludeWindowsFromCapture { get; init; } = true; }
// SettingsStore.Version == 3
```

- [ ] **Write failing tests** - replace the entire contents of `tests/LocalScribe.Core.Tests/SettingsTests.cs` with (existing five tests updated to v3, two new tests added):

```csharp
// tests/LocalScribe.Core.Tests/SettingsTests.cs
using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SettingsTests
{
    [Fact]
    public async Task Fresh_install_returns_keep_default_and_v3_shape()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(3, s.SchemaVersion);
            Assert.Equal("keep", s.AudioRetention);
            Assert.Equal(AudioFormat.Flac, s.AudioFormat);
            Assert.False(s.AutoDetect.Enabled);
            Assert.True(s.Overlay.ExcludeFromCapture);
            Assert.True(s.Privacy.ExcludeWindowsFromCapture);   // v3 default
            Assert.Null(s.ConsentNotice);                       // absence = not yet acknowledged
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Roundtrips_v3_with_spec_wire_values()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            await new SettingsStore(path).SaveAsync(new Settings(), default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"audioRetention\": \"keep\"", json);
            Assert.Contains("\"audioFormat\": \"flac\"", json);
            Assert.Contains("\"backend\": \"auto\"", json);
            Assert.Contains("\"mode\": \"followDefault\"", json);   // mic
            Assert.Contains("\"startStop\": \"Ctrl+Alt+R\"", json);
            Assert.Contains("\"excludeWindowsFromCapture\": true", json);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public void Migration_v1_to_v3_chain_preserves_retention_flips_autodetect_adds_sections()
    {
        var v1 = JsonNode.Parse(@"{
            ""schemaVersion"": 1,
            ""storageRoot"": ""%USERPROFILE%/LocalScribe"",
            ""audioRetention"": ""days:30"",
            ""model"": ""auto"", ""backend"": ""auto"", ""language"": ""auto"",
            ""autoDetect"": { ""enabled"": true, ""apps"": [""Teams"",""Zoom"",""Webex""] },
            ""hotkeys"": { ""startStop"": ""Ctrl+Alt+R"", ""pause"": ""Ctrl+Alt+P"" },
            ""timestamps"": ""relative"", ""recordingIndicator"": true, ""launchAtLogin"": true,
            ""logging"": { ""level"": ""info"", ""includeTranscriptText"": false }
        }")!.AsObject();

        var s = SettingsMigrator.Migrate(v1);
        Assert.Equal(3, s.SchemaVersion);
        Assert.Equal("days:30", s.AudioRetention);      // preserved, NOT flipped to keep
        Assert.False(s.AutoDetect.Enabled);             // flipped by v1->v2
        Assert.Equal(AudioFormat.Flac, s.AudioFormat);  // v2 addition at default
        Assert.True(s.Overlay.ExcludeFromCapture);      // v2 addition at default
        Assert.Equal(RemoteMode.Auto, s.Remote.Mode);   // v2 addition at default
        Assert.True(s.Privacy.ExcludeWindowsFromCapture);   // v3 addition at default
        Assert.Null(s.ConsentNotice);                   // migration never fabricates consent
        Assert.Equal("Ctrl+Alt+R", s.Hotkeys.StartStop);    // v1 field survives the chain
    }

    [Fact]
    public async Task Store_migrates_v1_file_on_load_and_rewrites_v3()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"audioRetention\":\"never\"}");
            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(3, s.SchemaVersion);
            Assert.Equal("never", s.AudioRetention);
            Assert.Contains("\"schemaVersion\": 3", await File.ReadAllTextAsync(path));
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Store_migrates_v2_file_adds_privacy_leaves_consent_absent_and_rewrites_v3()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,
                "{\"schemaVersion\":2,\"audioRetention\":\"days:30\",\"autoDetect\":{\"enabled\":false}}");

            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(3, s.SchemaVersion);
            Assert.Equal("days:30", s.AudioRetention);          // v2 content preserved
            Assert.True(s.Privacy.ExcludeWindowsFromCapture);   // additive default
            Assert.Null(s.ConsentNotice);                       // never synthesized

            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"schemaVersion\": 3", json);
            Assert.DoesNotContain("consentNotice", json);       // field-absence = unacknowledged
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task ConsentNotice_roundtrips_and_is_omitted_when_null()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var acknowledged = new Settings
            {
                ConsentNotice = new ConsentSetting
                {
                    AcknowledgedAtUtc = new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero),
                    AppVersion = "0.4.0",
                },
            };
            await store.SaveAsync(acknowledged, default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"acknowledgedAtUtc\": \"2026-07-03T10:00:00Z\"", json);
            Assert.Contains("\"appVersion\": \"0.4.0\"", json);

            var back = await store.LoadOrDefaultAsync(default);
            Assert.Equal(acknowledged.ConsentNotice, back.ConsentNotice);

            await store.SaveAsync(new Settings(), default);
            Assert.DoesNotContain("consentNotice", await File.ReadAllTextAsync(path));
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Rejects_newer_settings_version()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":4}");
            await Assert.ThrowsAsync<NotSupportedException>(
                () => new SettingsStore(path).LoadOrDefaultAsync(default));
        }
        finally { CleanParent(path); }
    }

    private static void CleanParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
```

- [ ] **Run and see it fail**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SettingsTests"` - expect build failure `CS0246: The type or namespace name 'ConsentSetting' could not be found` and `CS1061: 'Settings' does not contain a definition for 'Privacy'`.

- [ ] **Implement model**: in `src/LocalScribe.Core/Model/Settings.cs`:
  - Line 4 doc comment: change `schema v2` to `schema v3`.
  - Line 7: `public int SchemaVersion { get; init; } = 2;` -> `public int SchemaVersion { get; init; } = 3;`
  - Insert after line 24 (`public LoggingSetting Logging { get; init; } = new();`), still inside the `Settings` record:

```csharp
    /// <summary>v3 (Stage 4, design 6.3): null until the first-run notice is accepted;
    /// detection is field-absence, not file-absence. Migration never fabricates this.</summary>
    public ConsentSetting? ConsentNotice { get; init; }
    /// <summary>v3 (Stage 4, design section 2): capture exclusion for transcript-bearing windows.</summary>
    public PrivacySetting Privacy { get; init; } = new();
```

  - Append at the end of the file, after the `LoggingSetting` record on line 33:

```csharp
public sealed record ConsentSetting { public DateTimeOffset AcknowledgedAtUtc { get; init; } public string AppVersion { get; init; } = ""; }
public sealed record PrivacySetting { public bool ExcludeWindowsFromCapture { get; init; } = true; }
```

- [ ] **Implement store + migrator**: in `src/LocalScribe.Core/Storage/SettingsStore.cs` change line 9 `public const int Version = 2;` -> `public const int Version = 3;` and the line-5 doc comment `migrates v1` -> `migrates v1/v2`. Then replace the entire contents of `src/LocalScribe.Core/Storage/SettingsMigrator.cs` with:

```csharp
// src/LocalScribe.Core/Storage/SettingsMigrator.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>settings.json migration chain (spec section 7).
/// v1 -> v2: adds new sections at defaults, disables auto-detect, preserves an explicitly
/// stored audioRetention. v2 -> v3 (Stage 4): adds privacy at defaults; consentNotice is
/// deliberately NOT synthesized - field absence means "not yet acknowledged" (design 6.3).
/// Deserializes into the typed model so the shared options apply on re-save
/// (migration re-serialization rule).</summary>
public static class SettingsMigrator
{
    public static Settings Migrate(JsonObject raw)
    {
        int v = SchemaGuard.ReadVersion(raw);
        SchemaGuard.RejectIfNewer(v, SettingsStore.Version, "settings.json");

        if (v <= 1)
        {
            raw["audioFormat"] ??= "flac";
            raw["self"] ??= new JsonObject { ["name"] = "" };
            raw["remote"] ??= new JsonObject { ["mode"] = "auto" };
            raw["mic"] ??= new JsonObject { ["mode"] = "followDefault" };
            raw["overlay"] ??= new JsonObject
            {
                ["enabled"] = true, ["showSessionName"] = false,
                ["showLevelMeter"] = true, ["excludeFromCapture"] = true,
            };
            raw["vocabulary"] ??= new JsonObject { ["terms"] = new JsonArray(), ["corrections"] = new JsonObject() };

            if (raw["autoDetect"] is JsonObject ad) ad["enabled"] = false;
            else raw["autoDetect"] = new JsonObject { ["enabled"] = false, ["apps"] = new JsonArray("Teams", "Zoom", "Webex") };

            // audioRetention preserved verbatim (fresh installs never reach the migrator).
            raw["schemaVersion"] = 2;
        }
        if (v <= 2)
        {
            raw["privacy"] ??= new JsonObject { ["excludeWindowsFromCapture"] = true };
            // consentNotice intentionally absent: migration must not fabricate an acknowledgment.
            raw["schemaVersion"] = 3;
        }
        return raw.Deserialize<Settings>(LocalScribeJson.Options)!;
    }
}
```

- [ ] **Run to green**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SettingsTests"` - expect all 7 SettingsTests PASS.
- [ ] **Full gate**: `dotnet test --filter "Category!=Fixture"` - expect PASS, 0 warnings (JsonConventionsTests and app-layer settings consumers must be unaffected - all changes additive).
- [ ] **Commit**: `git add src/LocalScribe.Core/Model/Settings.cs src/LocalScribe.Core/Storage/SettingsStore.cs src/LocalScribe.Core/Storage/SettingsMigrator.cs tests/LocalScribe.Core.Tests/SettingsTests.cs && git commit -m "feat: settings v3 - consentNotice + privacy.excludeWindowsFromCapture with v1->v3 migration chain"`

---

### Task 3: MatterIdGenerator + ParticipantId

**Files:**
- Modify: `src/LocalScribe.Core/Storage/SessionId.cs` (lines 25-44: add a fallback overload to `Slug` - the existing slug logic is reused, not duplicated)
- Create: `src/LocalScribe.Core/Storage/ParticipantId.cs`
- Create: `src/LocalScribe.Core/Storage/MatterIdGenerator.cs`
- Test: `tests/LocalScribe.Core.Tests/ParticipantIdTests.cs` (new), `tests/LocalScribe.Core.Tests/MatterIdGeneratorTests.cs` (new)

**Interfaces:**
- Consumes: `SessionId.Slug` / `SessionId.EnsureUnique` (src/LocalScribe.Core/Storage/SessionId.cs:15-44), `MattersIndex` / `MattersIndexEntry` (Task 1 shapes).
- Produces (locked - Matters-page and metadata-editor ViewModels compile against these exactly):

```csharp
public static class MatterIdGenerator { public static string Next(MattersIndex index, string mattersDir, int year); }
public static class ParticipantId { public static string Mint(string name, IReadOnlyCollection<string> existingIds); }
// plus new overload other code may use: SessionId.Slug(string text, string fallback)
```

#### Cycle A: ParticipantId (+ Slug fallback overload)

- [ ] **Write failing tests** - create `tests/LocalScribe.Core.Tests/ParticipantIdTests.cs` with:

```csharp
using LocalScribe.Core.Storage;

public class ParticipantIdTests
{
    [Fact]
    public void Mints_ascii_slug_with_p_prefix()
        => Assert.Equal("p-alice-client", ParticipantId.Mint("Alice Client", []));

    [Fact]
    public void Trims_lowercases_and_collapses_separators()
        => Assert.Equal("p-alice-obrien", ParticipantId.Mint("  Alice   O'Brien ", []));

    [Fact]
    public void Collision_appends_2_then_3()
    {
        Assert.Equal("p-alice-client-2",
            ParticipantId.Mint("Alice Client", ["p-alice-client"]));
        Assert.Equal("p-alice-client-3",
            ParticipantId.Mint("Alice Client", ["p-alice-client", "p-alice-client-2"]));
    }

    [Fact]
    public void Non_ascii_only_name_slugs_to_person()
    {
        // "\u5F20\u4F1F" (CJK name) has no ASCII letters/digits at all.
        Assert.Equal("p-person", ParticipantId.Mint("\u5F20\u4F1F", []));
        Assert.Equal("p-person", ParticipantId.Mint("###", []));
        Assert.Equal("p-person-2", ParticipantId.Mint("\u5F20\u4F1F", ["p-person"]));
    }

    [Fact]
    public void P_self_is_reserved_and_never_minted()
    {
        // Even with no existing ids, a name slugging to "self" must not claim p-self
        // (SessionBootstrap.cs:24 mints p-self for the user themselves).
        Assert.Equal("p-self-2", ParticipantId.Mint("Self", []));
        Assert.Equal("p-self-2", ParticipantId.Mint("SELF", []));
    }
}
```

- [ ] **Run and see it fail**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ParticipantIdTests"` - expect build failure `CS0103: The name 'ParticipantId' does not exist in the current context`.

- [ ] **Implement**: in `src/LocalScribe.Core/Storage/SessionId.cs` replace lines 25-26:

```csharp
    public static string Slug(string text)
    {
```

with:

```csharp
    public static string Slug(string text) => Slug(text, "session");

    /// <summary>Same ASCII slug with a caller-chosen fallback for texts containing no ASCII
    /// letters/digits (ParticipantId uses "person"; session ids keep "session").</summary>
    public static string Slug(string text, string fallback)
    {
```

and replace line 43 `return slug.Length == 0 ? "session" : slug;` with `return slug.Length == 0 ? fallback : slug;`. Then create `src/LocalScribe.Core/Storage/ParticipantId.cs`:

```csharp
namespace LocalScribe.Core.Storage;

/// <summary>Roster-member / session-participant id minting (design 4.2, spec section 1.5):
/// "p-{ascii-slug}" with -2/-3 suffixes on collision; uniqueness is scoped to the owning
/// matter roster or session participant list (the ids passed in). "p-self" is reserved by
/// SessionBootstrap and is never minted here. Names with no ASCII letters/digits slug to
/// "p-person" - the snapshot Name keeps the real (possibly non-ASCII) spelling.</summary>
public static class ParticipantId
{
    public static string Mint(string name, IReadOnlyCollection<string> existingIds)
    {
        string candidate = "p-" + SessionId.Slug(name, "person");
        return SessionId.EnsureUnique(candidate, id => id == "p-self" || existingIds.Contains(id));
    }
}
```

- [ ] **Run to green**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ParticipantIdTests"` - expect 5 PASS.
- [ ] **Guard the overload refactor**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SessionBootstrapTests"` - expect PASS (session-id slugging behavior unchanged).
- [ ] **Commit**: `git add src/LocalScribe.Core/Storage/SessionId.cs src/LocalScribe.Core/Storage/ParticipantId.cs tests/LocalScribe.Core.Tests/ParticipantIdTests.cs && git commit -m "feat: ParticipantId minting - reserved p-self, person fallback slug, scoped -2/-3 collisions"`

#### Cycle B: MatterIdGenerator

- [ ] **Write failing tests** - create `tests/LocalScribe.Core.Tests/MatterIdGeneratorTests.cs` (temp dir per test class, IDisposable cleanup per house convention):

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public sealed class MatterIdGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_midgen_{Guid.NewGuid():N}");
    private readonly string _mattersDir;

    public MatterIdGeneratorTests()
    {
        _mattersDir = Path.Combine(_root, "matters");
        Directory.CreateDirectory(_mattersDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static MattersIndex Index(params string[] ids) => new()
    {
        Matters = ids.Select(id => new MattersIndexEntry { Id = id, Name = id }).ToList(),
    };

    [Fact]
    public void Empty_index_and_dir_mints_001()
        => Assert.Equal("M-2026-001", MatterIdGenerator.Next(Index(), _mattersDir, 2026));

    [Fact]
    public void Increments_past_the_years_max()
        => Assert.Equal("M-2026-004",
            MatterIdGenerator.Next(Index("M-2026-001", "M-2026-003"), _mattersDir, 2026));

    [Fact]
    public void Year_rollover_restarts_at_001()
        => Assert.Equal("M-2026-001",
            MatterIdGenerator.Next(Index("M-2025-007", "M-2025-008"), _mattersDir, 2026));

    [Fact]
    public void Orphan_folder_missing_from_index_is_skipped()
    {
        // Folder exists but the index lost its entry (the crash window MatterStore.cs
        // documents): the id must not be reissued - it doubles as the folder name.
        Directory.CreateDirectory(Path.Combine(_mattersDir, "M-2026-001"));
        Assert.Equal("M-2026-002", MatterIdGenerator.Next(Index(), _mattersDir, 2026));
    }

    [Fact]
    public void Increments_until_both_index_and_folder_are_free()
    {
        Directory.CreateDirectory(Path.Combine(_mattersDir, "M-2026-002"));
        Assert.Equal("M-2026-003",
            MatterIdGenerator.Next(Index("M-2026-001"), _mattersDir, 2026));
    }

    [Fact]
    public void Malformed_and_foreign_ids_are_ignored()
        => Assert.Equal("M-2026-001",
            MatterIdGenerator.Next(Index("M-2026-xyz", "CASE-9", "M-2026-"), _mattersDir, 2026));
}
```

- [ ] **Run and see it fail**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~MatterIdGeneratorTests"` - expect build failure `CS0103: The name 'MatterIdGenerator' does not exist in the current context`.

- [ ] **Implement** - create `src/LocalScribe.Core/Storage/MatterIdGenerator.cs`:

```csharp
using System.Globalization;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Matter id minting (design 4.2, spec section 1.5): "M-{yyyy}-{NNN}", sequential
/// within the year as max(existing NNN in the index)+1, then incrementing NNN until BOTH
/// the index id and the matters/&lt;id&gt;/ folder are free - the id doubles as the folder
/// name, and an orphan folder outside the index (MatterStore's documented crash window)
/// must never be reissued. Invariant culture: ids are stable across machine calendars.</summary>
public static class MatterIdGenerator
{
    public static string Next(MattersIndex index, string mattersDir, int year)
    {
        string prefix = string.Create(CultureInfo.InvariantCulture, $"M-{year:D4}-");
        int max = 0;
        foreach (var entry in index.Matters)
        {
            if (entry.Id.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(entry.Id.AsSpan(prefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int n)
                && n > max)
            {
                max = n;
            }
        }
        for (int nnn = max + 1; ; nnn++)
        {
            string candidate = string.Create(CultureInfo.InvariantCulture, $"{prefix}{nnn:D3}");
            if (index.Matters.All(e => e.Id != candidate)
                && !Directory.Exists(Path.Combine(mattersDir, candidate)))
            {
                return candidate;
            }
        }
    }
}
```

- [ ] **Run to green**: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~MatterIdGeneratorTests"` - expect 6 PASS.
- [ ] **Full gate**: `dotnet test --filter "Category!=Fixture"` - expect PASS, 0 warnings.
- [ ] **Commit**: `git add src/LocalScribe.Core/Storage/MatterIdGenerator.cs tests/LocalScribe.Core.Tests/MatterIdGeneratorTests.cs && git commit -m "feat: MatterIdGenerator - year-scoped sequential ids skipping index and folder collisions"`

---

### Task 4: SessionCatalog (migration-tolerant session enumeration)

**Files:**
- Create: `src/LocalScribe.Core/Storage/SessionCatalog.cs` (also defines `SessionListItem`, `SessionCatalogResult`)
- Test: `tests/LocalScribe.Core.Tests/SessionCatalogTests.cs`

**Interfaces:**
- Consumes (existing source): `SessionStore.ReadAsync(SessionParticipant? selfForMigration, CancellationToken)` (SessionStore.cs:17 - write-migrates v1/v2 -> v3 and synthesizes meta.json), `MetadataStore.LoadAsync(CancellationToken)`, `SessionMeta.CreateDefault(AppKind, DateTimeOffset, SessionParticipant?)`, `StoragePaths.SessionsDir/SessionJson/MetaJson`.
- Produces (locked - Tasks 5, 7, and the App group's MaintenanceService compile against these exactly):
  ```csharp
  public sealed record SessionListItem(string Id, SessionRecord Session, SessionMeta Meta);
  public sealed record SessionCatalogResult(IReadOnlyList<SessionListItem> Sessions, int UnreadableCount);
  public sealed class SessionCatalog(StoragePaths paths) { public Task<SessionCatalogResult> ListAsync(CancellationToken ct); }
  ```

The meta fallback must replicate SessionWriter.RegenerateProjectionsAsync exactly (SessionWriter.cs:19-30): compute `startedLocal` from the session's stored `UtcOffsetMinutes` (machine-local only when null, i.e. pre-v3), then `MetadataStore.LoadAsync(...) ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null)`. All reads use `selfForMigration: null` - never fabricate today's identity into old sessions (design 3.1).

- [ ] Write the failing test file `tests/LocalScribe.Core.Tests/SessionCatalogTests.cs`:

  ```csharp
  using LocalScribe.Core.Model;
  using LocalScribe.Core.Storage;

  public sealed class SessionCatalogTests : IDisposable
  {
      private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
      private StoragePaths Paths => new(_root);

      public void Dispose()
      {
          if (Directory.Exists(_root)) Directory.Delete(_root, true);
      }

      private async Task WriteSessionAsync(string id, DateTimeOffset startedUtc,
          AppKind app = AppKind.Webex, int? utcOffsetMinutes = null)
          => await new SessionStore(Paths.SessionJson(id)).SaveAsync(new SessionRecord
          {
              Id = id,
              App = app,
              StartedAtUtc = startedUtc,
              EndedAtUtc = startedUtc.AddMinutes(30),
              UtcOffsetMinutes = utcOffsetMinutes,
              DurationMs = 1_800_000,
          }, default);

      [Fact]
      public async Task Lists_sessions_newest_first_with_meta()
      {
          await WriteSessionAsync("2026-06-30_0900_Webex_old", new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero));
          await WriteSessionAsync("2026-07-01_1000_Webex_new", new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
          await new MetadataStore(Paths.MetaJson("2026-07-01_1000_Webex_new"))
              .SaveAsync(new SessionMeta { Title = "Bail hearing prep" }, default);

          var result = await new SessionCatalog(Paths).ListAsync(default);

          Assert.Equal(0, result.UnreadableCount);
          Assert.Equal(2, result.Sessions.Count);
          Assert.Equal("2026-07-01_1000_Webex_new", result.Sessions[0].Id);   // newest first (StartedAtUtc desc)
          Assert.Equal("2026-06-30_0900_Webex_old", result.Sessions[1].Id);
          Assert.Equal("Bail hearing prep", result.Sessions[0].Meta.Title);
      }

      [Fact]
      public async Task Missing_meta_falls_back_to_CreateDefault_in_session_offset()
      {
          // UTC+8 offset makes the local display hour differ from UTC - proves the fallback
          // uses the session's stored offset exactly like SessionWriter (SessionWriter.cs:25-29).
          await WriteSessionAsync("2026-07-01_1800_Webex_x",
              new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero), utcOffsetMinutes: 480);

          var result = await new SessionCatalog(Paths).ListAsync(default);

          var item = Assert.Single(result.Sessions);
          Assert.Equal("Webex \u2014 2026-07-01 18:00", item.Meta.Title);
          Assert.Empty(item.Meta.Participants);                   // self: null - never fabricated
          Assert.False(File.Exists(Paths.MetaJson(item.Id)));     // fallback is in-memory, not written
      }

      [Fact]
      public async Task Unreadable_folders_are_counted_not_thrown()
      {
          await WriteSessionAsync("2026-07-01_1000_Webex_good", new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
          Directory.CreateDirectory(Paths.SessionDir("no-session-json"));           // session.json absent
          Directory.CreateDirectory(Paths.SessionDir("corrupt"));
          await File.WriteAllTextAsync(Paths.SessionJson("corrupt"), "{ not json"); // parse throws
          Directory.CreateDirectory(Paths.SessionDir("future"));
          await File.WriteAllTextAsync(Paths.SessionJson("future"), "{\"schemaVersion\":99}"); // reject-higher throws

          var result = await new SessionCatalog(Paths).ListAsync(default);

          Assert.Equal(3, result.UnreadableCount);
          var item = Assert.Single(result.Sessions);
          Assert.Equal("2026-07-01_1000_Webex_good", item.Id);
      }

      [Fact]
      public async Task V2_folder_is_write_migrated_without_fabricated_self()
      {
          string id = "2026-06-01_1000_Teams_old";
          Directory.CreateDirectory(Paths.SessionDir(id));
          await File.WriteAllTextAsync(Paths.SessionJson(id), @"{
              ""schemaVersion"": 2,
              ""id"": ""2026-06-01_1000_Teams_old"",
              ""app"": ""Teams"",
              ""startedAtUtc"": ""2026-06-01T10:00:00Z"",
              ""endedAtUtc"": ""2026-06-01T10:30:00Z"",
              ""durationMs"": 1800000,
              ""sources"": [""Local"", ""Remote""],
              ""model"": ""small.en"",
              ""backend"": ""CPU"",
              ""language"": ""en"",
              ""retainedAudioSources"": [""Local"", ""Remote""],
              ""title"": ""Old session"",
              ""segmentCount"": 10,
              ""markerCount"": 1
          }");

          var result = await new SessionCatalog(Paths).ListAsync(default);

          Assert.Equal(0, result.UnreadableCount);
          var item = Assert.Single(result.Sessions);
          Assert.Equal(3, item.Session.SchemaVersion);                        // rewritten at v3

          // meta.json synthesized ON DISK, WITHOUT self (selfForMigration: null - design 3.1).
          var meta = await new MetadataStore(Paths.MetaJson(id)).LoadAsync(default);
          Assert.NotNull(meta);
          Assert.Equal("Old session", meta!.Title);
          Assert.Empty(meta.Participants);

          string rewritten = await File.ReadAllTextAsync(Paths.SessionJson(id));
          Assert.Contains("\"schemaVersion\": 3", rewritten);
          Assert.DoesNotContain("\"title\"", rewritten);                      // title relocated to meta.json
      }

      [Fact]
      public async Task Empty_or_missing_sessions_dir_yields_empty_result()
      {
          var result = await new SessionCatalog(Paths).ListAsync(default);    // root never created
          Assert.Empty(result.Sessions);
          Assert.Equal(0, result.UnreadableCount);

          Directory.CreateDirectory(Paths.SessionsDir);                        // exists but empty
          result = await new SessionCatalog(Paths).ListAsync(default);
          Assert.Empty(result.Sessions);
          Assert.Equal(0, result.UnreadableCount);
      }
  }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionCatalogTests"` - expected FAILURE: build error `CS0246: The type or namespace name 'SessionCatalog' could not be found` (test project does not compile yet).

- [ ] Minimal implementation - create `src/LocalScribe.Core/Storage/SessionCatalog.cs` with exactly this content:

  ```csharp
  // src/LocalScribe.Core/Storage/SessionCatalog.cs
  using LocalScribe.Core.Model;
  namespace LocalScribe.Core.Storage;

  /// <summary>One session row for the manager list: folder id + system truth + user meta (design 3.1).</summary>
  public sealed record SessionListItem(string Id, SessionRecord Session, SessionMeta Meta);

  /// <summary>Enumeration result: readable sessions newest-first plus the count of skipped
  /// unreadable folders (surfaced as a footer note, never thrown - design 3.1).</summary>
  public sealed record SessionCatalogResult(IReadOnlyList<SessionListItem> Sessions, int UnreadableCount);

  /// <summary>Enumerates storageRoot/sessions/* through the existing stores - no sessions index
  /// file, files stay the truth (design 3.1). Reads are the migration event for old roots
  /// (SessionStore write-migrates v1/v2 -> v3 and synthesizes meta.json); every read passes
  /// selfForMigration: null - never fabricate today's identity into old sessions. Callers that
  /// need serialization against recovery/finalize route calls through the maintenance service's
  /// per-session queue (design 7.3); this class is mechanism only.</summary>
  public sealed class SessionCatalog(StoragePaths paths)
  {
      public async Task<SessionCatalogResult> ListAsync(CancellationToken ct)
      {
          if (!Directory.Exists(paths.SessionsDir))
              return new SessionCatalogResult([], 0);

          var items = new List<SessionListItem>();
          int unreadable = 0;
          foreach (string dir in Directory.EnumerateDirectories(paths.SessionsDir))
          {
              ct.ThrowIfCancellationRequested();
              string id = Path.GetFileName(dir);
              try
              {
                  var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(selfForMigration: null, ct);
                  if (session is null) { unreadable++; continue; }    // session.json absent

                  // Same fallback SessionWriter.RegenerateProjectionsAsync uses (SessionWriter.cs:19-30):
                  // the session's own recorded offset; machine zone only for pre-v3 records (null offset).
                  var startedLocal = session.UtcOffsetMinutes is int offsetMin
                      ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
                      : session.StartedAtUtc.ToLocalTime();
                  var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(ct)
                             ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
                  items.Add(new SessionListItem(id, session, meta));
              }
              catch (Exception ex) when (ex is not OperationCanceledException)
              {
                  unreadable++;   // corrupt / future-schema / IO-failed folder: counted, never thrown
              }
          }
          return new SessionCatalogResult(
              items.OrderByDescending(i => i.Session.StartedAtUtc).ToList(), unreadable);
      }
  }
  ```

- [ ] Run it again: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionCatalogTests"` - expected PASS (5 tests).
- [ ] Full gate: `dotnet test --filter "Category!=Fixture"` (all green) and `dotnet build` (0 warnings).
- [ ] Commit: `git add src/LocalScribe.Core/Storage/SessionCatalog.cs tests/LocalScribe.Core.Tests/SessionCatalogTests.cs && git commit -m "feat: SessionCatalog - migration-tolerant session enumeration, newest-first, unreadable counted"`

### Task 5: MattersIndexRebuilder (index self-heal + incremental tag deltas)

**Files:**
- Create: `src/LocalScribe.Core/Storage/MattersIndexRebuilder.cs`
- Test: `tests/LocalScribe.Core.Tests/MattersIndexRebuilderTests.cs`

**Interfaces:**
- Consumes: `SessionCatalog.ListAsync` (Task 4); `Matter.Archived` + `MattersIndexEntry.Archived` and `MatterStore.Version` == 2 (Task 2 model additions); `MatterStore.ListAsync/LoadAsync/CreateAsync` (MatterStore.cs); `JsonFile.WriteAsync`; `StoragePaths.MattersDir/MattersIndexJson/MatterJson`.
- Produces (locked - the Matters-page VM and MaintenanceService compile against this exactly):
  ```csharp
  public sealed class MattersIndexRebuilder(StoragePaths paths)
  {
      public Task<MattersIndex> RebuildAsync(CancellationToken ct);
      public Task ApplyTagDeltaAsync(IReadOnlyCollection<string> addedMatterIds, IReadOnlyCollection<string> removedMatterIds, CancellationToken ct);
  }
  ```

Semantics (design 4.3): rebuild = the index becomes exactly the set of loadable `matters/*/matter.json` files - orphan adoption (folder present, index entry missing) and vanished-folder drop (entry present, folder gone) both fall out of rebuilding from the folder scan. `SessionCount` recomputed by scanning all session metas' `MatterIds` via SessionCatalog; `Archived` preserved from matter.json. An unreadable matter.json is skipped this rebuild (its next successful `MatterStore.SaveAsync` re-adds it). Callers serialize through the maintenance service; single-instance guard removes the cross-process race.

**Cycle 1 - RebuildAsync:**

- [ ] Write the failing test file `tests/LocalScribe.Core.Tests/MattersIndexRebuilderTests.cs`:

  ```csharp
  using LocalScribe.Core.Model;
  using LocalScribe.Core.Storage;

  public sealed class MattersIndexRebuilderTests : IDisposable
  {
      private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
      private StoragePaths Paths => new(_root);

      public void Dispose()
      {
          if (Directory.Exists(_root)) Directory.Delete(_root, true);
      }

      /// <summary>Writes matter.json directly WITHOUT touching the index - an orphan, exactly the
      /// crash-window half-state MatterStore.cs:6-7 documents.</summary>
      private async Task WriteMatterFolderOnlyAsync(string id, string name, bool archived = false)
          => await JsonFile.WriteAsync(Paths.MatterJson(id), new Matter
          {
              SchemaVersion = MatterStore.Version,
              Id = id,
              Name = name,
              Archived = archived,
              DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
          }, default);

      private async Task WriteSessionTaggedAsync(string id, params string[] matterIds)
      {
          var started = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
          await new SessionStore(Paths.SessionJson(id)).SaveAsync(new SessionRecord
          {
              Id = id, App = AppKind.Webex, StartedAtUtc = started, EndedAtUtc = started.AddMinutes(30),
          }, default);
          await new MetadataStore(Paths.MetaJson(id))
              .SaveAsync(new SessionMeta { Title = id, MatterIds = matterIds }, default);
      }

      [Fact]
      public async Task Rebuild_adopts_orphan_matter_folders_missing_from_index()
      {
          await WriteMatterFolderOnlyAsync("M-2026-001", "Doe v. State");     // never indexed

          var rebuilt = await new MattersIndexRebuilder(Paths).RebuildAsync(default);

          var entry = Assert.Single(rebuilt.Matters);
          Assert.Equal("M-2026-001", entry.Id);
          Assert.Equal("Doe v. State", entry.Name);
          Assert.Equal(0, entry.SessionCount);

          var onDisk = await new MatterStore(Paths.MattersDir).ListAsync();   // written, not just returned
          Assert.Single(onDisk.Matters);
      }

      [Fact]
      public async Task Rebuild_drops_index_entries_whose_folder_vanished()
      {
          var store = new MatterStore(Paths.MattersDir);
          await store.CreateAsync(new Matter
          {
              Id = "M-2026-001", Name = "Kept",
              DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
          });
          await store.CreateAsync(new Matter
          {
              Id = "M-2026-002", Name = "Vanished",
              DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
          });
          Directory.Delete(Path.Combine(Paths.MattersDir, "M-2026-002"), true);   // external tampering

          var rebuilt = await new MattersIndexRebuilder(Paths).RebuildAsync(default);

          var entry = Assert.Single(rebuilt.Matters);
          Assert.Equal("M-2026-001", entry.Id);
      }

      [Fact]
      public async Task Rebuild_recomputes_session_counts_from_metas_and_preserves_archived()
      {
          await WriteMatterFolderOnlyAsync("M-2026-001", "Tagged twice");
          await WriteMatterFolderOnlyAsync("M-2026-002", "Untagged, archived", archived: true);
          await WriteSessionTaggedAsync("2026-07-01_1000_Webex_a", "M-2026-001");
          await WriteSessionTaggedAsync("2026-07-01_1100_Webex_b", "M-2026-001");
          await WriteSessionTaggedAsync("2026-07-01_1200_Webex_c");            // no matter

          // Seed a stale index (wrong count, wrong archived) to prove rebuild overwrites it.
          await JsonFile.WriteAsync(Paths.MattersIndexJson, new MattersIndex
          {
              SchemaVersion = MatterStore.Version,
              Matters = new[]
              {
                  new MattersIndexEntry { Id = "M-2026-001", Name = "Tagged twice", SessionCount = 99 },
              },
          }, default);

          var rebuilt = await new MattersIndexRebuilder(Paths).RebuildAsync(default);

          Assert.Equal(2, rebuilt.Matters.Count);
          Assert.Equal(2, rebuilt.Matters.Single(e => e.Id == "M-2026-001").SessionCount);
          Assert.False(rebuilt.Matters.Single(e => e.Id == "M-2026-001").Archived);
          Assert.Equal(0, rebuilt.Matters.Single(e => e.Id == "M-2026-002").SessionCount);
          Assert.True(rebuilt.Matters.Single(e => e.Id == "M-2026-002").Archived);   // from matter.json
      }

      [Fact]
      public async Task Rebuild_on_empty_root_yields_empty_index()
      {
          var rebuilt = await new MattersIndexRebuilder(Paths).RebuildAsync(default);
          Assert.Empty(rebuilt.Matters);
      }
  }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~MattersIndexRebuilderTests"` - expected FAILURE: build error `CS0246: 'MattersIndexRebuilder' could not be found`.

- [ ] Minimal implementation - create `src/LocalScribe.Core/Storage/MattersIndexRebuilder.cs`:

  ```csharp
  // src/LocalScribe.Core/Storage/MattersIndexRebuilder.cs
  using LocalScribe.Core.Model;
  namespace LocalScribe.Core.Storage;

  /// <summary>The Stage 4 index self-heal promised by MatterStore.cs:5-7 (design 4.3). Rebuild
  /// makes matters.json exactly the set of loadable matters/*/matter.json files (orphans adopted,
  /// vanished folders dropped) with SessionCount recomputed from all session metas' matterIds and
  /// Archived taken from matter.json. Between rebuilds, ApplyTagDeltaAsync keeps counts current
  /// incrementally. All calls are serialized by the maintenance service (design 7.3); the
  /// single-instance guard (design 7.2) removes the cross-process race.</summary>
  public sealed class MattersIndexRebuilder(StoragePaths paths)
  {
      public async Task<MattersIndex> RebuildAsync(CancellationToken ct)
      {
          // Recompute tag counts from the metas (migration-tolerant reads via the catalog).
          var counts = new Dictionary<string, int>(StringComparer.Ordinal);
          var catalog = await new SessionCatalog(paths).ListAsync(ct);
          foreach (var item in catalog.Sessions)
              foreach (string mid in item.Meta.MatterIds)
                  counts[mid] = counts.TryGetValue(mid, out int n) ? n + 1 : 1;

          var store = new MatterStore(paths.MattersDir);
          var entries = new List<MattersIndexEntry>();
          if (Directory.Exists(paths.MattersDir))
          {
              foreach (string dir in Directory.EnumerateDirectories(paths.MattersDir))
              {
                  ct.ThrowIfCancellationRequested();
                  string id = Path.GetFileName(dir);   // id doubles as the folder name (design 4.2)
                  Matter? matter;
                  try { matter = await store.LoadAsync(id, ct); }
                  catch (Exception ex) when (ex is not OperationCanceledException)
                  {
                      continue;   // unreadable matter.json: skipped this rebuild; next save re-adds it
                  }
                  if (matter is null) continue;         // folder without matter.json: nothing to adopt
                  entries.Add(new MattersIndexEntry
                  {
                      Id = id,
                      Name = matter.Name,
                      Reference = matter.Reference,
                      Archived = matter.Archived,
                      SessionCount = counts.TryGetValue(id, out int n) ? n : 0,
                  });
              }
          }

          var rebuilt = new MattersIndex
          {
              SchemaVersion = MatterStore.Version,
              Matters = entries.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(),   // deterministic
          };
          await JsonFile.WriteAsync(paths.MattersIndexJson, rebuilt, ct);              // atomic
          return rebuilt;
      }
  }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~MattersIndexRebuilderTests"` - expected PASS (4 tests).
- [ ] Commit: `git add src/LocalScribe.Core/Storage/MattersIndexRebuilder.cs tests/LocalScribe.Core.Tests/MattersIndexRebuilderTests.cs && git commit -m "feat: MattersIndexRebuilder - orphan adoption, vanished-folder drop, count recompute"`

**Cycle 2 - ApplyTagDeltaAsync:**

- [ ] Append these tests to `tests/LocalScribe.Core.Tests/MattersIndexRebuilderTests.cs` (before the closing brace of the class):

  ```csharp
      [Fact]
      public async Task ApplyTagDelta_increments_decrements_and_floors_at_zero()
      {
          var store = new MatterStore(Paths.MattersDir);
          await store.CreateAsync(new Matter
          {
              Id = "M-2026-001", Name = "A",
              DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
          });
          await store.CreateAsync(new Matter
          {
              Id = "M-2026-002", Name = "B",
              DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
          });

          var rebuilder = new MattersIndexRebuilder(Paths);
          await rebuilder.ApplyTagDeltaAsync(addedMatterIds: ["M-2026-001"], removedMatterIds: [], default);
          await rebuilder.ApplyTagDeltaAsync(addedMatterIds: ["M-2026-001"], removedMatterIds: ["M-2026-002"], default);

          var index = await store.ListAsync();
          Assert.Equal(2, index.Matters.Single(e => e.Id == "M-2026-001").SessionCount);
          Assert.Equal(0, index.Matters.Single(e => e.Id == "M-2026-002").SessionCount);   // floored, not -1
      }

      [Fact]
      public async Task ApplyTagDelta_ignores_ids_missing_from_index()
      {
          var store = new MatterStore(Paths.MattersDir);
          await store.CreateAsync(new Matter
          {
              Id = "M-2026-001", Name = "A",
              DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
          });

          // Unknown ids must not throw and must not invent entries - RebuildAsync is the self-heal.
          await new MattersIndexRebuilder(Paths)
              .ApplyTagDeltaAsync(addedMatterIds: ["M-2026-999"], removedMatterIds: ["M-2026-888"], default);

          var index = await store.ListAsync();
          var entry = Assert.Single(index.Matters);
          Assert.Equal("M-2026-001", entry.Id);
          Assert.Equal(0, entry.SessionCount);
      }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~MattersIndexRebuilderTests"` - expected FAILURE: build error `CS1061: 'MattersIndexRebuilder' does not contain a definition for 'ApplyTagDeltaAsync'`.

- [ ] Add the method to `src/LocalScribe.Core/Storage/MattersIndexRebuilder.cs` (after `RebuildAsync`, before the closing brace of the class):

  ```csharp
      /// <summary>Incremental +/-1 SessionCount adjustments after a tag/untag/delete (design 4.3).
      /// Counts floor at 0; ids absent from the index are ignored (RebuildAsync is the self-heal).
      /// Read-modify-write mirrors MatterStore.UpsertIndexAsync; callers serialize (design 7.3).</summary>
      public async Task ApplyTagDeltaAsync(IReadOnlyCollection<string> addedMatterIds,
          IReadOnlyCollection<string> removedMatterIds, CancellationToken ct)
      {
          var index = await new MatterStore(paths.MattersDir).ListAsync(ct);
          var entries = index.Matters.ToList();
          for (int i = 0; i < entries.Count; i++)
          {
              int delta = addedMatterIds.Count(id => id == entries[i].Id)
                        - removedMatterIds.Count(id => id == entries[i].Id);
              if (delta != 0)
                  entries[i] = entries[i] with { SessionCount = Math.Max(0, entries[i].SessionCount + delta) };
          }
          await JsonFile.WriteAsync(paths.MattersIndexJson,
              index with { SchemaVersion = MatterStore.Version, Matters = entries }, ct);
      }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~MattersIndexRebuilderTests"` - expected PASS (6 tests).
- [ ] Full gate: `dotnet test --filter "Category!=Fixture"` (all green) and `dotnet build` (0 warnings).
- [ ] Commit: `git add src/LocalScribe.Core/Storage/MattersIndexRebuilder.cs tests/LocalScribe.Core.Tests/MattersIndexRebuilderTests.cs && git commit -m "feat: MattersIndexRebuilder.ApplyTagDeltaAsync - incremental counts with zero floor"`

### Task 6: RecoveryScanner (find unended sessions for the startup scan)

**Files:**
- Create: `src/LocalScribe.Core/Storage/RecoveryScanner.cs`
- Test: `tests/LocalScribe.Core.Tests/RecoveryScannerTests.cs`

**Interfaces:**
- Consumes: `SessionStore.ReadAsync(SessionParticipant?, CancellationToken)`, `StoragePaths.SessionsDir/SessionJson`.
- Produces (locked - MaintenanceService.RecoverAllAsync iterates these ids into `SessionWriter.RecoverIfNeededAsync`):
  ```csharp
  public sealed class RecoveryScanner(StoragePaths paths) { public Task<IReadOnlyList<string>> FindUnendedAsync(CancellationToken ct); }
  ```

Semantics (design 7.1): a session needs recovery iff its session.json reads OK and `EndedAtUtc == null`. Reads use `selfForMigration: null` and may be the first migration event for an old root - that is accepted and by design. Unreadable folders are skipped silently here: they are the catalog's unreadable-count concern (design 3.1), not recovery's.

- [ ] Write the failing test file `tests/LocalScribe.Core.Tests/RecoveryScannerTests.cs`:

  ```csharp
  using LocalScribe.Core.Model;
  using LocalScribe.Core.Storage;

  public sealed class RecoveryScannerTests : IDisposable
  {
      private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
      private StoragePaths Paths => new(_root);

      public void Dispose()
      {
          if (Directory.Exists(_root)) Directory.Delete(_root, true);
      }

      private async Task WriteSessionAsync(string id, DateTimeOffset? endedUtc, bool recovered = false)
          => await new SessionStore(Paths.SessionJson(id)).SaveAsync(new SessionRecord
          {
              Id = id,
              App = AppKind.Webex,
              StartedAtUtc = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
              EndedAtUtc = endedUtc,
              Recovered = recovered,
          }, default);

      [Fact]
      public async Task Finds_only_sessions_with_null_EndedAtUtc()
      {
          await WriteSessionAsync("2026-07-01_1000_Webex_crashed", endedUtc: null);
          await WriteSessionAsync("2026-07-01_1100_Webex_done",
              endedUtc: new DateTimeOffset(2026, 7, 1, 11, 30, 0, TimeSpan.Zero));
          await WriteSessionAsync("2026-07-01_1200_Webex_recovered",
              endedUtc: new DateTimeOffset(2026, 7, 1, 12, 30, 0, TimeSpan.Zero), recovered: true);

          var ids = await new RecoveryScanner(Paths).FindUnendedAsync(default);

          Assert.Equal(["2026-07-01_1000_Webex_crashed"], ids);   // finalized + already-recovered ignored
      }

      [Fact]
      public async Task Tolerates_unreadable_folders_without_throwing()
      {
          await WriteSessionAsync("2026-07-01_1000_Webex_crashed", endedUtc: null);
          Directory.CreateDirectory(Paths.SessionDir("no-session-json"));
          Directory.CreateDirectory(Paths.SessionDir("corrupt"));
          await File.WriteAllTextAsync(Paths.SessionJson("corrupt"), "{ not json");

          var ids = await new RecoveryScanner(Paths).FindUnendedAsync(default);

          Assert.Equal(["2026-07-01_1000_Webex_crashed"], ids);   // unreadable = catalog's concern, not recovery's
      }

      [Fact]
      public async Task Missing_sessions_dir_yields_empty_list()
      {
          var ids = await new RecoveryScanner(Paths).FindUnendedAsync(default);
          Assert.Empty(ids);
      }
  }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~RecoveryScannerTests"` - expected FAILURE: build error `CS0246: 'RecoveryScanner' could not be found`.

- [ ] Minimal implementation - create `src/LocalScribe.Core/Storage/RecoveryScanner.cs`:

  ```csharp
  // src/LocalScribe.Core/Storage/RecoveryScanner.cs
  namespace LocalScribe.Core.Storage;

  /// <summary>Finds sessions the startup recovery scan must finalize: session.json reads OK and
  /// EndedAtUtc == null (design 7.1). Reads use selfForMigration: null and may be the first
  /// migration event for an old root - accepted. Unreadable folders are skipped silently: they
  /// are the catalog's unreadable-count concern (design 3.1), not recovery's. Callers route the
  /// per-session RecoverIfNeededAsync calls through the maintenance service's per-session queue.</summary>
  public sealed class RecoveryScanner(StoragePaths paths)
  {
      public async Task<IReadOnlyList<string>> FindUnendedAsync(CancellationToken ct)
      {
          if (!Directory.Exists(paths.SessionsDir)) return [];

          var unended = new List<string>();
          foreach (string dir in Directory.EnumerateDirectories(paths.SessionsDir))
          {
              ct.ThrowIfCancellationRequested();
              string id = Path.GetFileName(dir);
              try
              {
                  var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(selfForMigration: null, ct);
                  if (session is not null && session.EndedAtUtc is null) unended.Add(id);
              }
              catch (Exception ex) when (ex is not OperationCanceledException)
              {
                  // Skipped: unreadable folders surface via SessionCatalog.UnreadableCount instead.
              }
          }
          unended.Sort(StringComparer.Ordinal);   // deterministic scan order
          return unended;
      }
  }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~RecoveryScannerTests"` - expected PASS (3 tests).
- [ ] Full gate: `dotnet test --filter "Category!=Fixture"` (all green) and `dotnet build` (0 warnings).
- [ ] Commit: `git add src/LocalScribe.Core/Storage/RecoveryScanner.cs tests/LocalScribe.Core.Tests/RecoveryScannerTests.cs && git commit -m "feat: RecoveryScanner - find unended sessions for the startup recovery scan"`

### Task 7: Recycle-bin seam + SessionDeleter + MatterDeleter + ShellRecycleBin

**Files:**
- Create: `src/LocalScribe.Core/Storage/IRecycleBin.cs`
- Create: `src/LocalScribe.Core/Storage/SessionDeleter.cs`
- Create: `src/LocalScribe.Core/Storage/MatterDeleter.cs`
- Create: `src/LocalScribe.App/Services/ShellRecycleBin.cs` (first file in the new Services/ folder; App project, NOT Core - Core stays shell-free)
- Test: `tests/LocalScribe.Core.Tests/SessionDeleterTests.cs`, `tests/LocalScribe.Core.Tests/MatterDeleterTests.cs`

**Interfaces:**
- Consumes: `SessionCatalog.ListAsync` (Task 4), `MatterStore.ListAsync/CreateAsync` + `MatterStore.Version` (MatterStore.cs), `JsonFile.WriteAsync`, `StoragePaths.SessionDir/MattersDir/MattersIndexJson/MetaJson/SessionJson`.
- Produces (locked - MaintenanceService.DeleteSessionAsync and the Matters-page VM compile against these exactly):
  ```csharp
  public interface IRecycleBin { void SendToRecycleBin(string path); }
  public sealed class SessionDeleter(StoragePaths paths, IRecycleBin bin) { public Task DeleteAsync(string sessionId, CancellationToken ct); }
  public sealed class MatterDeleter(StoragePaths paths, IRecycleBin bin) { public Task<int> CountReferencesAsync(string matterId, CancellationToken ct); public Task DeleteAsync(string matterId, CancellationToken ct); }
  public sealed class ShellRecycleBin : IRecycleBin   // App project; LocalScribe.App.Services
  ```

Evidentiary invariants (design 3.4/4.1): whole-session-folder recycle is the ONLY session-data deletion in the product; matter delete is organizational-data-only and throws `InvalidOperationException` while any session meta references the matter. Both go to the Recycle Bin - never a permanent unlink.

**Cycle 1 - IRecycleBin + SessionDeleter:**

- [ ] Write the failing test file `tests/LocalScribe.Core.Tests/SessionDeleterTests.cs`:

  ```csharp
  using LocalScribe.Core.Model;
  using LocalScribe.Core.Storage;

  public sealed class SessionDeleterTests : IDisposable
  {
      private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
      private StoragePaths Paths => new(_root);

      public void Dispose()
      {
          if (Directory.Exists(_root)) Directory.Delete(_root, true);
      }

      private sealed class FakeRecycleBin : IRecycleBin
      {
          public List<string> Recycled { get; } = [];
          public void SendToRecycleBin(string path) => Recycled.Add(path);
      }

      [Fact]
      public async Task Delete_sends_the_whole_session_folder_to_the_recycle_bin()
      {
          string id = "2026-07-01_1000_Webex_x";
          await new SessionStore(Paths.SessionJson(id)).SaveAsync(new SessionRecord
          {
              Id = id, App = AppKind.Webex,
              StartedAtUtc = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
              EndedAtUtc = new DateTimeOffset(2026, 7, 1, 10, 30, 0, TimeSpan.Zero),
          }, default);
          var bin = new FakeRecycleBin();

          await new SessionDeleter(Paths, bin).DeleteAsync(id, default);

          Assert.Equal([Paths.SessionDir(id)], bin.Recycled);   // the FOLDER, exactly once
      }

      [Fact]
      public async Task Delete_of_missing_folder_throws_DirectoryNotFound_and_recycles_nothing()
      {
          var bin = new FakeRecycleBin();
          await Assert.ThrowsAsync<DirectoryNotFoundException>(
              () => new SessionDeleter(Paths, bin).DeleteAsync("no-such-session", default));
          Assert.Empty(bin.Recycled);
      }
  }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionDeleterTests"` - expected FAILURE: build error `CS0246: 'IRecycleBin' could not be found` (and `SessionDeleter`).

- [ ] Minimal implementation - create `src/LocalScribe.Core/Storage/IRecycleBin.cs`:

  ```csharp
  // src/LocalScribe.Core/Storage/IRecycleBin.cs
  namespace LocalScribe.Core.Storage;

  /// <summary>Seam over the OS recycle operation so Core stays shell-free and the deleters are
  /// unit-testable with a recording fake. The real implementation (SHFileOperationW) lives in the
  /// App project (Services/ShellRecycleBin.cs) and is exercised only by the smoke runbook.</summary>
  public interface IRecycleBin
  {
      /// <summary>Sends the file or directory at <paramref name="path"/> to the Windows Recycle
      /// Bin (recoverable - design 3.4). Throws on failure; never permanently unlinks.</summary>
      void SendToRecycleBin(string path);
  }
  ```

  and create `src/LocalScribe.Core/Storage/SessionDeleter.cs`:

  ```csharp
  // src/LocalScribe.Core/Storage/SessionDeleter.cs
  namespace LocalScribe.Core.Storage;

  /// <summary>Whole-session delete: the session FOLDER goes to the Recycle Bin, never a permanent
  /// unlink (design 3.4). This is the ONLY deletion of session/transcript data in the product
  /// (evidentiary invariant, spec 1.1/1.6). Policy lives in the caller (MaintenanceService):
  /// close open read views first (audio file handles), refuse live/pending-recovery sessions,
  /// then ApplyTagDeltaAsync for the tagged matters. This class is mechanism only.</summary>
  public sealed class SessionDeleter(StoragePaths paths, IRecycleBin bin)
  {
      public Task DeleteAsync(string sessionId, CancellationToken ct)
      {
          ct.ThrowIfCancellationRequested();
          string dir = paths.SessionDir(sessionId);
          if (!Directory.Exists(dir))
              throw new DirectoryNotFoundException($"Session folder not found: {dir}");
          bin.SendToRecycleBin(dir);
          return Task.CompletedTask;
      }
  }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionDeleterTests"` - expected PASS (2 tests).
- [ ] Commit: `git add src/LocalScribe.Core/Storage/IRecycleBin.cs src/LocalScribe.Core/Storage/SessionDeleter.cs tests/LocalScribe.Core.Tests/SessionDeleterTests.cs && git commit -m "feat: recycle-bin seam + SessionDeleter - whole-folder recycle, missing folder throws"`

**Cycle 2 - MatterDeleter:**

- [ ] Write the failing test file `tests/LocalScribe.Core.Tests/MatterDeleterTests.cs`:

  ```csharp
  using LocalScribe.Core.Model;
  using LocalScribe.Core.Storage;

  public sealed class MatterDeleterTests : IDisposable
  {
      private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
      private StoragePaths Paths => new(_root);

      public void Dispose()
      {
          if (Directory.Exists(_root)) Directory.Delete(_root, true);
      }

      private sealed class FakeRecycleBin : IRecycleBin
      {
          public List<string> Recycled { get; } = [];
          public void SendToRecycleBin(string path) => Recycled.Add(path);
      }

      private async Task CreateMatterAsync(string id, string name)
          => await new MatterStore(Paths.MattersDir).CreateAsync(new Matter
          {
              Id = id, Name = name,
              DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
          });

      private async Task WriteSessionTaggedAsync(string id, params string[] matterIds)
      {
          var started = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
          await new SessionStore(Paths.SessionJson(id)).SaveAsync(new SessionRecord
          {
              Id = id, App = AppKind.Webex, StartedAtUtc = started, EndedAtUtc = started.AddMinutes(30),
          }, default);
          await new MetadataStore(Paths.MetaJson(id))
              .SaveAsync(new SessionMeta { Title = id, MatterIds = matterIds }, default);
      }

      [Fact]
      public async Task CountReferences_counts_sessions_whose_meta_tags_the_matter()
      {
          await CreateMatterAsync("M-2026-001", "Doe v. State");
          await WriteSessionTaggedAsync("2026-07-01_1000_Webex_a", "M-2026-001");
          await WriteSessionTaggedAsync("2026-07-01_1100_Webex_b", "M-2026-001", "M-2026-002");
          await WriteSessionTaggedAsync("2026-07-01_1200_Webex_c");   // untagged

          var deleter = new MatterDeleter(Paths, new FakeRecycleBin());

          Assert.Equal(2, await deleter.CountReferencesAsync("M-2026-001", default));
          Assert.Equal(0, await deleter.CountReferencesAsync("M-2026-999", default));
      }

      [Fact]
      public async Task Delete_is_blocked_while_referenced_and_names_the_count()
      {
          await CreateMatterAsync("M-2026-001", "Doe v. State");
          await WriteSessionTaggedAsync("2026-07-01_1000_Webex_a", "M-2026-001");
          await WriteSessionTaggedAsync("2026-07-01_1100_Webex_b", "M-2026-001");
          var bin = new FakeRecycleBin();

          var ex = await Assert.ThrowsAsync<InvalidOperationException>(
              () => new MatterDeleter(Paths, bin).DeleteAsync("M-2026-001", default));

          Assert.Contains("2", ex.Message);                                     // the count, for the dialog
          Assert.Empty(bin.Recycled);                                           // nothing recycled
          var index = await new MatterStore(Paths.MattersDir).ListAsync();
          Assert.Single(index.Matters);                                         // index entry intact
      }

      [Fact]
      public async Task Delete_of_unreferenced_matter_recycles_folder_and_removes_index_entry()
      {
          await CreateMatterAsync("M-2026-001", "Keep me");
          await CreateMatterAsync("M-2026-002", "Delete me");
          var bin = new FakeRecycleBin();

          await new MatterDeleter(Paths, bin).DeleteAsync("M-2026-002", default);

          Assert.Equal([Path.Combine(Paths.MattersDir, "M-2026-002")], bin.Recycled);
          var index = await new MatterStore(Paths.MattersDir).ListAsync();
          var entry = Assert.Single(index.Matters);
          Assert.Equal("M-2026-001", entry.Id);
      }

      [Fact]
      public async Task Delete_with_vanished_folder_still_removes_index_entry_without_recycling()
      {
          await CreateMatterAsync("M-2026-001", "Half-state");
          Directory.Delete(Path.Combine(Paths.MattersDir, "M-2026-001"), true);   // external tampering
          var bin = new FakeRecycleBin();

          await new MatterDeleter(Paths, bin).DeleteAsync("M-2026-001", default);  // heals, no throw

          Assert.Empty(bin.Recycled);
          var index = await new MatterStore(Paths.MattersDir).ListAsync();
          Assert.Empty(index.Matters);
      }
  }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~MatterDeleterTests"` - expected FAILURE: build error `CS0246: 'MatterDeleter' could not be found`.

- [ ] Minimal implementation - create `src/LocalScribe.Core/Storage/MatterDeleter.cs`:

  ```csharp
  // src/LocalScribe.Core/Storage/MatterDeleter.cs
  namespace LocalScribe.Core.Storage;

  /// <summary>Matter delete (design 4.1): BLOCKED while any session meta references the matter
  /// (throws InvalidOperationException naming the count - the dialog suggests archiving instead).
  /// Unreferenced delete recycles matters/&lt;id&gt;/ and removes the index entry. This deletes
  /// organizational data only: blocked-while-referenced guarantees no session content references
  /// it, so the evidentiary invariant (spec 1.1 - coarse whole-session delete is the only
  /// session-data deletion) is untouched. Callers serialize via the maintenance service.</summary>
  public sealed class MatterDeleter(StoragePaths paths, IRecycleBin bin)
  {
      public async Task<int> CountReferencesAsync(string matterId, CancellationToken ct)
      {
          var catalog = await new SessionCatalog(paths).ListAsync(ct);
          return catalog.Sessions.Count(s => s.Meta.MatterIds.Contains(matterId));
      }

      public async Task DeleteAsync(string matterId, CancellationToken ct)
      {
          int references = await CountReferencesAsync(matterId, ct);
          if (references > 0)
              throw new InvalidOperationException(
                  $"Matter '{matterId}' is referenced by {references} session(s) and cannot be deleted; archive it instead.");

          // Recycle the folder when present; a vanished folder (crash-window half-state,
          // MatterStore.cs:6-7) still gets its index entry healed away below.
          string dir = Path.Combine(paths.MattersDir, matterId);
          if (Directory.Exists(dir)) bin.SendToRecycleBin(dir);

          // Read-modify-write of matters.json, mirroring MatterStore.UpsertIndexAsync
          // (MatterStore.cs:41-55): ListAsync + direct JsonFile write stamped with the Version.
          var index = await new MatterStore(paths.MattersDir).ListAsync(ct);
          var entries = index.Matters.Where(e => e.Id != matterId).ToList();
          await JsonFile.WriteAsync(paths.MattersIndexJson,
              index with { SchemaVersion = MatterStore.Version, Matters = entries }, ct);
      }
  }
  ```

- [ ] Run it: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~MatterDeleterTests"` - expected PASS (4 tests).
- [ ] Commit: `git add src/LocalScribe.Core/Storage/MatterDeleter.cs tests/LocalScribe.Core.Tests/MatterDeleterTests.cs && git commit -m "feat: MatterDeleter - blocked-while-referenced delete, folder recycle + index entry removal"`

**Cycle 3 - ShellRecycleBin (real implementation, App project, NO unit test):**

This is a shell side effect (it moves real files into the user's Recycle Bin), so it deliberately gets NO unit test - a test would either mutate the developer's Recycle Bin or mock away the only thing worth testing. It is verified by the Stage 4 smoke runbook's delete-to-recycle-bin step (design 9: "delete flow ... recycle call seam mocked" for units; the real recycle is a B-series GUI item). No new NuGet packages: raw SHFileOperationW P/Invoke, matching the existing hand-rolled interop style (NativeWindowInterop.cs).

- [ ] Create `src/LocalScribe.App/Services/ShellRecycleBin.cs` (this creates the Services/ folder - the App group's WPF-free services land beside it):

  ```csharp
  // src/LocalScribe.App/Services/ShellRecycleBin.cs
  using System.IO;
  using System.Runtime.InteropServices;
  using LocalScribe.Core.Storage;

  namespace LocalScribe.App.Services;

  /// <summary>The real IRecycleBin: sends a file or folder to the Windows Recycle Bin via
  /// SHFileOperationW with FOF_ALLOWUNDO (recoverable - design 3.4, never a permanent unlink).
  /// Shell side effect: NO unit test by design; verified by the Stage 4 smoke runbook's
  /// delete-to-recycle-bin step. Lives in the App project so Core stays shell-free.</summary>
  public sealed class ShellRecycleBin : IRecycleBin
  {
      private const uint FO_DELETE = 0x0003;
      private const ushort FOF_ALLOWUNDO = 0x0040;        // recycle, do not unlink
      private const ushort FOF_NOCONFIRMATION = 0x0010;   // our own dialog already confirmed
      private const ushort FOF_SILENT = 0x0004;           // no shell progress UI

      public void SendToRecycleBin(string path)
      {
          // SHFileOperationW requires an absolute path; relative paths are resolved against an
          // unspecified directory. StoragePaths roots are already full paths; normalize anyway.
          string full = Path.GetFullPath(path);
          var op = new SHFILEOPSTRUCTW
          {
              wFunc = FO_DELETE,
              // pFrom is a double-null-terminated list of paths. The string marshaller appends
              // one terminating null; the explicit "\0" below supplies the second.
              pFrom = full + "\0",
              fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
          };
          int result = SHFileOperationW(ref op);
          if (result != 0)
              throw new IOException(
                  $"Recycle of '{full}' failed (SHFileOperationW returned 0x{result:X})");
          if (op.fAnyOperationsAborted != 0)
              throw new IOException($"Recycle of '{full}' was aborted by the shell");
      }

      [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
      private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

      // Sequential layout matches the 64-bit SHFILEOPSTRUCTW (the struct is only packed on
      // 32-bit Windows headers; this app ships x64-only, same as the whisper native deps).
      [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
      private struct SHFILEOPSTRUCTW
      {
          public IntPtr hwnd;
          public uint wFunc;
          [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
          [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
          public ushort fFlags;
          public int fAnyOperationsAborted;   // Win32 BOOL
          public IntPtr hNameMappings;
          [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
      }
  }
  ```

- [ ] Build the App project: `dotnet build src/LocalScribe.App/LocalScribe.App.csproj` - expected: build succeeds with 0 warnings. (No test run for this file - see the smoke-runbook note above.)
- [ ] Add a line item to the Stage 4 smoke runbook checklist (the docs group's runbook task owns the file; if `docs/plans/2026-07-03-stage-4-smoke-runbook.md` does not exist yet, leave this note in the commit body instead): "B: delete a scratch session from the manager -> confirm the folder appears in the Windows Recycle Bin and is restorable."
- [ ] Full gate: `dotnet test --filter "Category!=Fixture"` (all green) and `dotnet build` (0 warnings).
- [ ] Commit: `git add src/LocalScribe.App/Services/ShellRecycleBin.cs && git commit -m "feat: ShellRecycleBin - SHFileOperationW recycle implementation (no unit test; smoke-runbook gated)"`

---

### Task 8: AppKindResolver + derive AppKind at manual Start

**Files:**
- Create: `src/LocalScribe.Core/Live/AppKindResolver.cs`
- Create: `tests/LocalScribe.Core.Tests/AppKindResolverTests.cs`
- Modify: `src/LocalScribe.Core/Live/SessionController.cs` (StartAsync, lines 165-170: the `CreateRemote` snapshot / `SessionBootstrap.StartAsync` call site)
- Modify: `tests/LocalScribe.Core.Tests/SessionControllerTests.cs` (append four tests before the class's closing brace, after `Failed_start_disposes_created_sources_and_stays_idle`, line 156)

**Interfaces:**
- Consumes: `RemoteSnapshot { RemoteMode Mode; string? App; bool FellBackToSystemMix }` (`src/LocalScribe.Core/Model/SessionRecord.cs:48-53`); `RemoteCapturePlanner.Plan` (`RemoteCapturePlanner.cs:25-50`) whose `RemotePlan.App` carries the matched process image for per-process plans AND for full-mix fallbacks (`Fallback(...)` at `RemoteCapturePlanner.cs:34,37,45,47` passes the matched/requested image, or null when nothing matched), and which `WasapiCaptureSourceProvider.CreateRemote` snapshots verbatim into `RemoteSnapshot.App` (`WasapiCaptureSourceProvider.cs:24-32`); `SessionBootstrap.StartAsync(StoragePaths, Settings, AppKind app, ...)` (`SessionBootstrap.cs:13`) — the `app` argument flows into `session.json` App, the folder id (`SessionId.New`), and `SessionMeta.CreateDefault`'s Medium mapping (`SessionMeta.cs:25-32`), so deriving BEFORE bootstrap covers all three at once (design 7.4: folder ids embed whatever App was resolved at creation); test doubles `LiveTestDoubles.MakeController/Options` and `FakeProvider.RemoteSnapshot` (`tests/LocalScribe.Core.Tests/LiveTestDoubles.cs:97-98,128-145`).
- Produces: `public static class AppKindResolver { public static AppKind FromProcessImage(string? processImage); }` in namespace `LocalScribe.Core.Live` (locked interface — Task 24's UI strings and any later consumer compile against exactly this).

Key decision encoded here (from the design + planner source): the controller derives only when the snapshot's `App` is a planner-matched image — i.e. `Mode == PerProcess`, or `FellBackToSystemMix == true` (fallbacks expose the matched image via `RemotePlan.App`). An explicitly pinned systemMix has `FellBackToSystemMix == false` and its `App` is the raw user-setting passthrough (`RemoteCapturePlanner.cs:27-28`), never a matched image — it stays `Manual`. `FromProcessImage(null)`/unknown returns `Manual`, so unresolved cases stay `Manual` for free.

- [ ] **Write the failing pure-mapping test.** Create `tests/LocalScribe.Core.Tests/AppKindResolverTests.cs` with exactly:

```csharp
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>Pure mapping table for the Stage 4 AppKind derivation (design 7.4). Images are the
/// extensionless process names the planner sees (AudioSessionInfo), matched case-insensitively
/// by containment - the same matching style RemoteCapturePlanner itself uses.</summary>
public sealed class AppKindResolverTests
{
    [Theory]
    [InlineData("CiscoCollabHost", AppKind.Webex)]     // Stage-1 finding: Webex renders here
    [InlineData("Webex", AppKind.Webex)]
    [InlineData("webex", AppKind.Webex)]               // case-insensitive
    [InlineData("CiscoCollabHost.exe", AppKind.Webex)] // containment tolerates a stray extension
    [InlineData("Zoom", AppKind.Zoom)]
    [InlineData("ZOOM", AppKind.Zoom)]
    [InlineData("ms-teams", AppKind.Teams)]
    [InlineData("Teams", AppKind.Teams)]
    [InlineData("msedgewebview2", AppKind.Browser)]    // Teams' webview counts as Browser (locked)
    [InlineData("chrome", AppKind.Browser)]
    [InlineData("msedge", AppKind.Browser)]
    [InlineData("firefox", AppKind.Browser)]
    [InlineData("brave", AppKind.Browser)]
    [InlineData("opera", AppKind.Browser)]
    [InlineData("Spotify", AppKind.Manual)]            // unknown image -> Manual
    [InlineData("", AppKind.Manual)]
    [InlineData(null, AppKind.Manual)]
    public void FromProcessImage_maps_known_images(string? image, AppKind expected)
        => Assert.Equal(expected, AppKindResolver.FromProcessImage(image));
}
```

- [ ] **Run it and watch it fail to compile.** `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AppKindResolverTests"` — expected: build failure `error CS0246: The type or namespace name 'AppKindResolver' could not be found`.
- [ ] **Implement the resolver.** Create `src/LocalScribe.Core/Live/AppKindResolver.cs` with exactly:

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Live;

/// <summary>Maps a planner-resolved process image to the AppKind recorded in session.json
/// (design 7.4 - the Stage 3b deferral). Same containment matching as RemoteCapturePlanner:
/// extensionless image names, case-insensitive. Null/unknown resolves to Manual so the caller
/// never has to special-case an unresolved plan. NOTE: "msedgewebview2" is checked in the
/// Browser bucket (locked mapping) - a Teams webview render session is a Browser capture
/// characteristic-wise, and the dedicated "ms-teams" image is what identifies real Teams.</summary>
public static class AppKindResolver
{
    public static AppKind FromProcessImage(string? processImage)
    {
        if (string.IsNullOrWhiteSpace(processImage)) return AppKind.Manual;
        if (Has(processImage, "CiscoCollabHost") || Has(processImage, "Webex")) return AppKind.Webex;
        if (Has(processImage, "Zoom")) return AppKind.Zoom;
        if (Has(processImage, "msedgewebview2") || Has(processImage, "chrome")
            || Has(processImage, "msedge") || Has(processImage, "firefox")
            || Has(processImage, "brave") || Has(processImage, "opera")) return AppKind.Browser;
        if (Has(processImage, "ms-teams") || Has(processImage, "Teams")) return AppKind.Teams;
        return AppKind.Manual;
    }

    private static bool Has(string image, string name)
        => image.Contains(name, StringComparison.OrdinalIgnoreCase);
}
```

(Browser is checked before Teams so `msedgewebview2` can never be shadowed by a future Teams alias; with today's name sets the two buckets share no substrings, so this ordering is defensive, not load-bearing.)
- [ ] **Run it green.** `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AppKindResolverTests"` — expected: PASS, 17 test cases.
- [ ] **Commit.** `git add src/LocalScribe.Core/Live/AppKindResolver.cs tests/LocalScribe.Core.Tests/AppKindResolverTests.cs && git commit -m "feat: AppKindResolver - map planner process images to AppKind"`
- [ ] **Write the failing controller-level tests.** In `tests/LocalScribe.Core.Tests/SessionControllerTests.cs`, insert the following four tests immediately after the `Failed_start_disposes_created_sources_and_stays_idle` method (line 156, before the class's closing brace):

```csharp
    [Fact]
    public async Task Manual_start_derives_app_from_per_process_plan()
    {
        // FakeProvider's default RemoteSnapshot is PerProcess on "CiscoCollabHost"
        // (LiveTestDoubles.cs:97-98) - exactly a resolved Webex plan.
        var (c, _, paths, _) = LiveTestDoubles.MakeController(_root);
        var options = LiveTestDoubles.Options() with { App = AppKind.Manual };

        string? id = await c.StartAsync(options, CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        Assert.Equal(AppKind.Manual, options.App);          // caller's options stay Manual
        Assert.Contains("_Webex_", id!);                    // folder id embeds the derived app
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(AppKind.Webex, record!.App);           // session.json App derived
        var meta = await new MetadataStore(paths.MetaJson(id!)).LoadAsync(CancellationToken.None);
        Assert.Equal(Medium.Webex, meta!.Medium);           // CreateDefault maps AppKind -> Medium
        Assert.StartsWith("Webex", meta.Title);             // default title derived too
    }

    [Fact]
    public async Task Manual_start_with_explicit_system_mix_stays_manual()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
        // User-pinned systemMix: FellBackToSystemMix=false and App is the raw setting
        // passthrough (RemoteCapturePlanner.cs:27-28), NOT a matched image - never derive.
        provider.RemoteSnapshot = new RemoteSnapshot
        { Mode = RemoteMode.SystemMix, App = "Webex", FellBackToSystemMix = false };

        string? id = await c.StartAsync(
            LiveTestDoubles.Options() with { App = AppKind.Manual }, CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(AppKind.Manual, record!.App);
    }

    [Fact]
    public async Task Manual_start_derives_from_full_mix_fallback_that_exposes_the_matched_image()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
        // Planner matched chrome but forced full mix (shared-audio image): the fallback still
        // exposes the matched image through RemotePlan.App -> RemoteSnapshot.App.
        provider.RemoteSnapshot = new RemoteSnapshot
        { Mode = RemoteMode.SystemMix, App = "chrome", FellBackToSystemMix = true };

        string? id = await c.StartAsync(
            LiveTestDoubles.Options() with { App = AppKind.Manual }, CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(AppKind.Browser, record!.App);
    }

    [Fact]
    public async Task Non_manual_start_never_rederives()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
        provider.RemoteSnapshot = new RemoteSnapshot
        { Mode = RemoteMode.SystemMix, App = "chrome", FellBackToSystemMix = true };

        // Options() defaults App to Webex - an explicit user choice must be honored verbatim.
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(AppKind.Webex, record!.App);
    }
```

- [ ] **Run them and watch the two derivation tests fail.** `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SessionControllerTests"` — expected: `Manual_start_derives_app_from_per_process_plan` fails at `Assert.Contains("_Webex_", id!)` (the id embeds `_Manual_`), `Manual_start_derives_from_full_mix_fallback...` fails at `Assert.Equal(AppKind.Browser, ...)` (actual Manual). The two guard tests (`..._stays_manual`, `Non_manual_...`) already PASS — they pin behavior that must survive the change. Existing 8 tests stay green.
- [ ] **Implement the hook.** In `src/LocalScribe.Core/Live/SessionController.cs`, replace lines 165-170:

```csharp
                (micSource, var micSnap) = _captureProvider.CreateMic(clock);
                (remoteSource, var remoteSnap) = _captureProvider.CreateRemote(clock);
                var devices = new DeviceSnapshot { Mic = micSnap, Remote = remoteSnap };

                var boot = await SessionBootstrap.StartAsync(_paths, _settings, options.App,
                    [SourceKind.Local, SourceKind.Remote], devices, _time, _appVersion, ct);
```

with:

```csharp
                (micSource, var micSnap) = _captureProvider.CreateMic(clock);
                (remoteSource, var remoteSnap) = _captureProvider.CreateRemote(clock);
                var devices = new DeviceSnapshot { Mic = micSnap, Remote = remoteSnap };

                // Stage 4 (design 7.4): a manual Start derives AppKind from the planner-resolved
                // remote image BEFORE bootstrap, so session.json App, the folder id, and the
                // default meta Title/Medium (SessionMeta.CreateDefault) all agree. Derive only
                // when RemoteSnapshot.App is a planner-MATCHED image: a per-process plan, or a
                // full-mix fallback (which exposes the matched image via RemotePlan.App). An
                // explicitly pinned systemMix has FellBackToSystemMix=false and its App is the
                // raw user setting - never derived. Unknown/null images resolve to Manual, so
                // unresolved plans stay Manual. Non-manual options are always honored verbatim.
                AppKind app = options.App;
                if (options.App == AppKind.Manual
                    && (remoteSnap.Mode == RemoteMode.PerProcess || remoteSnap.FellBackToSystemMix))
                    app = AppKindResolver.FromProcessImage(remoteSnap.App);

                var boot = await SessionBootstrap.StartAsync(_paths, _settings, app,
                    [SourceKind.Local, SourceKind.Remote], devices, _time, _appVersion, ct);
```

No other change: the finalize path (`StopAsync` line 413, `s.LiveRecord with { ... }`) preserves the bootstrapped `App` because `App` is not among the `with` mutations, and meta Medium already derived inside `SessionBootstrap.StartAsync` via `SessionMeta.CreateDefault(app, ...)` (`SessionBootstrap.cs:25`).
- [ ] **Run the controller tests green.** `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SessionControllerTests"` — expected: PASS, 12 tests.
- [ ] **Full gate + commit.** `dotnet test --filter "Category!=Fixture"` — expected: all green, 0 warnings on build. Then `git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs && git commit -m "feat: derive AppKind from resolved remote capture target at manual Start"`

### Task 9: MaintenanceService (per-session single-flight maintenance/write serialization)

Prerequisite: Tasks 1-7 merged — this task compiles against `SessionCatalog`, `MattersIndexRebuilder`, `RecoveryScanner`, `SessionDeleter`, `IRecycleBin` (all `LocalScribe.Core.Storage`, locked interfaces) and the additive schema fields.

**Files:**
- Create: `src/LocalScribe.App/Services/ISettingsService.cs` (new `Services` folder; the interface lives here so MaintenanceService can consume it now — Task 10 supplies the implementation)
- Create: `src/LocalScribe.App/Services/MaintenanceService.cs`
- Create: `tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs`

**Interfaces:**
- Consumes (locked, from Tasks 1-7): `SessionCatalog(StoragePaths paths).ListAsync(CancellationToken) : Task<SessionCatalogResult>`; `SessionCatalogResult(IReadOnlyList<SessionListItem> Sessions, int UnreadableCount)`; `SessionListItem(string Id, SessionRecord Session, SessionMeta Meta)`; `MattersIndexRebuilder(StoragePaths paths).RebuildAsync(CancellationToken) : Task<MattersIndex>` and `.ApplyTagDeltaAsync(IReadOnlyCollection<string> addedMatterIds, IReadOnlyCollection<string> removedMatterIds, CancellationToken) : Task`; `RecoveryScanner(StoragePaths paths).FindUnendedAsync(CancellationToken) : Task<IReadOnlyList<string>>`; `SessionDeleter(StoragePaths paths, IRecycleBin bin).DeleteAsync(string sessionId, CancellationToken) : Task`; `IRecycleBin { void SendToRecycleBin(string path); }`. From existing source: `MetadataStore(string metaJsonPath).SaveAsync(SessionMeta, CancellationToken)` (`MetadataStore.cs:12-13`); `SessionWriter(StoragePaths, Settings, TimeProvider)` with `.RegenerateProjectionsAsync(string, CancellationToken)` and `.RecoverIfNeededAsync(string, CancellationToken) : Task<bool>` (`SessionWriter.cs:16-17,19,77`); `StoragePaths` getters (`StoragePaths.cs`).
- Produces (locked — later UI tasks and Task 24 compile against these exactly):

```csharp
namespace LocalScribe.App.Services;
public interface ISettingsService { Settings Current { get; } event Action<Settings, Settings>? Changed; Task SaveAsync(Settings updated, CancellationToken ct); }
public sealed record RecoveryScanResult(IReadOnlyList<string> RecoveredIds, IReadOnlyList<(string Id, string Error)> Failures);
public sealed class MaintenanceService(StoragePaths paths, ISettingsService settings, IRecycleBin recycleBin, TimeProvider time)
{
    public Task<T> RunForSessionAsync<T>(string sessionId, Func<CancellationToken, Task<T>> work, CancellationToken ct);
    public Task<SessionCatalogResult> ListSessionsAsync(CancellationToken ct);
    public Task SaveMetaAsync(string sessionId, SessionMeta meta, IReadOnlyCollection<string> previousMatterIds, CancellationToken ct);
    public Task DeleteSessionAsync(string sessionId, IReadOnlyCollection<string> taggedMatterIds, CancellationToken ct);
    public Task<RecoveryScanResult> RecoverAllAsync(CancellationToken ct);
    public Task<MattersIndex> RebuildIndexAsync(CancellationToken ct);
    public Task CascadeMatterAsync(string matterId, IProgress<int>? progress, CancellationToken ct);
    public Task RegenerateAllAsync(IProgress<int>? progress, CancellationToken ct);
}
```

- [ ] **Create the settings-service interface (WPF-free, no test — pure contract).** Create `src/LocalScribe.App/Services/ISettingsService.cs` with exactly:

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.App.Services;

/// <summary>The app's single mutable Settings seam (design 6.2). Current is always a coherent
/// immutable snapshot; SaveAsync persists atomically via SettingsStore, swaps Current, then
/// raises Changed(old, new). Implemented by SettingsService (Task 10); MaintenanceService and
/// the Stage 4 ViewModels consume only this interface. WPF-free by house rule.</summary>
public interface ISettingsService
{
    Settings Current { get; }
    event Action<Settings, Settings>? Changed;
    Task SaveAsync(Settings updated, CancellationToken ct);
}
```

- [ ] **Write the failing single-flight test.** Create `tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs` with exactly (the fixture helpers and fakes below are used by every later cycle in this task):

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MaintenanceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-maint-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (MaintenanceService Svc, StoragePaths Paths) MakeService()
    {
        var paths = new StoragePaths(_root);
        var svc = new MaintenanceService(paths, new FakeSettingsService(), new NoopRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero)));
        return (svc, paths);
    }

    /// <summary>A finalized on-disk session fixture: valid v3 session.json + meta.json, no
    /// transcript.jsonl (TranscriptStore reads a missing file as empty - projections render
    /// with zero rows, which is all these orchestration tests need).</summary>
    private static async Task WriteFinalizedSessionAsync(StoragePaths paths, string id, string title,
        IReadOnlyList<string>? matterIds = null)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, CancellationToken.None);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = title, MatterIds = matterIds ?? [] }, CancellationToken.None);
    }

    private static async Task WriteUnendedSessionAsync(StoragePaths paths, string id)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 2, 0, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0,          // EndedAtUtc stays null: unended
        }, CancellationToken.None);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Interrupted" }, CancellationToken.None);
    }

    [Fact]
    public async Task RunForSessionAsync_serializes_concurrent_work_on_one_id_but_not_across_ids()
    {
        var (svc, _) = MakeService();
        bool firstEntered = false, secondEntered = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> first = svc.RunForSessionAsync("s-one", async _ =>
        { firstEntered = true; await release.Task; return 1; }, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => firstEntered, TimeSpan.FromSeconds(5)));

        Task<int> second = svc.RunForSessionAsync("s-one", _ =>
        { secondEntered = true; return Task.FromResult(2); }, CancellationToken.None);

        // Interleaving proof: while the first work HOLDS the gate, the second must not enter.
        Assert.False(SpinWait.SpinUntil(() => secondEntered, TimeSpan.FromMilliseconds(200)));

        // Per-id, not global: a different session id runs to completion while s-one is held.
        Assert.Equal(3, await svc.RunForSessionAsync("s-two", _ => Task.FromResult(3), CancellationToken.None));

        release.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);                       // ran only after the first released
        Assert.True(secondEntered);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public Settings Current { get; set; } = new();
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }

    private sealed class NoopRecycleBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
    }
}
```

- [ ] **Run it and watch it fail to compile.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceTests"` — expected: build failure `error CS0246: The type or namespace name 'MaintenanceService' could not be found`.
- [ ] **Implement the single-flight core.** Create `src/LocalScribe.App/Services/MaintenanceService.cs` with exactly:

```csharp
using System.Collections.Concurrent;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Services;

/// <summary>The one app-level owner of all disk mutation from the UI (design 7.3): projection
/// re-renders behind a per-session single-flight queue, index writes behind one dedicated gate,
/// recovery-scan orchestration, cascades, bulk regenerate. ViewModels never call SessionWriter
/// directly. WPF-free by house rule; unit-testable headless.</summary>
public sealed class MaintenanceService(StoragePaths paths, ISettingsService settings,
    IRecycleBin recycleBin, TimeProvider time)
{
    // Per-session gates are created on first touch and kept for the process lifetime - a
    // Stage 4 manager touches at most a few hundred ids, so unbounded growth is a non-issue.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new();
    private readonly SemaphoreSlim _indexGate = new(1, 1);   // serializes ALL matters.json writes

    /// <summary>Per-session single-flight: an edit, a finalize regen, a migrating read, and a
    /// cascade can never interleave writes inside one session folder (design 7.3).</summary>
    public async Task<T> RunForSessionAsync<T>(string sessionId, Func<CancellationToken, Task<T>> work,
        CancellationToken ct)
    {
        var gate = _sessionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await work(ct); }
        finally { gate.Release(); }
    }
}
```

- [ ] **Run the single-flight test green.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceTests"` — expected: PASS, 1 test.
- [ ] **Commit.** `git add src/LocalScribe.App/Services/ISettingsService.cs src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs && git commit -m "feat: MaintenanceService per-session single-flight queue + ISettingsService contract"`
- [ ] **Write the failing SaveMeta test.** Add to `MaintenanceServiceTests` (after the single-flight test):

```csharp
    [Fact]
    public async Task SaveMetaAsync_regenerates_projections_and_applies_tag_delta()
    {
        var (svc, paths) = MakeService();
        const string id = "2026-07-03_0100_Webex_alpha";
        await WriteFinalizedSessionAsync(paths, id, "Old title");
        await new MatterStore(paths.MattersDir).CreateAsync(new Matter
        { Id = "M-2026-001", Name = "Estate of Alpha",
          DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero) });

        var updated = new SessionMeta { Title = "Estate call - corrected", MatterIds = ["M-2026-001"] };
        await svc.SaveMetaAsync(id, updated, previousMatterIds: [], CancellationToken.None);

        // Regen ran with the NEW meta: session.txt carries the new title (fresh SessionWriter
        // from settings.Current - the projection pipeline SessionWriter.cs:19-75).
        string sessionTxt = await File.ReadAllTextAsync(paths.SessionTxt(id));
        Assert.Contains("Estate call - corrected", sessionTxt);
        Assert.True(File.Exists(paths.TranscriptMd(id)));

        // Tag delta ([M-2026-001] added, nothing removed) hit the index: SessionCount 0 -> 1.
        var index = await new MatterStore(paths.MattersDir).ListAsync(CancellationToken.None);
        Assert.Equal(1, Assert.Single(index.Matters, m => m.Id == "M-2026-001").SessionCount);

        // And the delta is symmetric: untag it again and the count returns to 0.
        await svc.SaveMetaAsync(id, updated with { MatterIds = [] },
            previousMatterIds: ["M-2026-001"], CancellationToken.None);
        index = await new MatterStore(paths.MattersDir).ListAsync(CancellationToken.None);
        Assert.Equal(0, Assert.Single(index.Matters, m => m.Id == "M-2026-001").SessionCount);
    }
```

- [ ] **Run it and watch it fail.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceTests"` — expected: build failure `error CS1061: 'MaintenanceService' does not contain a definition for 'SaveMetaAsync'`.
- [ ] **Implement SaveMeta + the other simple locked methods.** In `src/LocalScribe.App/Services/MaintenanceService.cs`, add inside the class, after `RunForSessionAsync`:

```csharp
    public Task<SessionCatalogResult> ListSessionsAsync(CancellationToken ct)
        => new SessionCatalog(paths).ListAsync(ct);

    /// <summary>Save meta.json (the ONLY file user metadata edits touch - spec 1.2/1.4), then
    /// regenerate projections under the same per-session gate with a FRESH SessionWriter built
    /// from settings.Current (so timestamp-style etc. reflect the latest save), then apply the
    /// matter-tag delta computed against previousMatterIds to the index.</summary>
    public async Task SaveMetaAsync(string sessionId, SessionMeta meta,
        IReadOnlyCollection<string> previousMatterIds, CancellationToken ct)
    {
        await RunForSessionAsync(sessionId, async inner =>
        {
            await new MetadataStore(paths.MetaJson(sessionId)).SaveAsync(meta, inner);
            await new SessionWriter(paths, settings.Current, time)
                .RegenerateProjectionsAsync(sessionId, inner);
            return true;
        }, ct);

        var added = meta.MatterIds.Except(previousMatterIds, StringComparer.Ordinal).ToList();
        var removed = previousMatterIds.Except(meta.MatterIds, StringComparer.Ordinal).ToList();
        if (added.Count > 0 || removed.Count > 0)
            await ApplyTagDeltaLockedAsync(added, removed, ct);
    }

    /// <summary>Whole-session delete to the Recycle Bin (design 3.4) - the caller has already
    /// closed any open read views (WindowRegistry.CloseAllFor) so no handle blocks the recycle.
    /// The delete runs under the session's gate; the index decrement follows.</summary>
    public async Task DeleteSessionAsync(string sessionId, IReadOnlyCollection<string> taggedMatterIds,
        CancellationToken ct)
    {
        await RunForSessionAsync(sessionId, async inner =>
        {
            await new SessionDeleter(paths, recycleBin).DeleteAsync(sessionId, inner);
            return true;
        }, ct);
        if (taggedMatterIds.Count > 0)
            await ApplyTagDeltaLockedAsync([], taggedMatterIds, ct);
    }

    public async Task<MattersIndex> RebuildIndexAsync(CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { return await new MattersIndexRebuilder(paths).RebuildAsync(ct); }
        finally { _indexGate.Release(); }
    }

    private async Task ApplyTagDeltaLockedAsync(IReadOnlyCollection<string> added,
        IReadOnlyCollection<string> removed, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { await new MattersIndexRebuilder(paths).ApplyTagDeltaAsync(added, removed, ct); }
        finally { _indexGate.Release(); }
    }
```

- [ ] **Run it green.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceTests"` — expected: PASS, 2 tests.
- [ ] **Commit.** `git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs && git commit -m "feat: MaintenanceService meta save + projection regen + index tag delta"`
- [ ] **Write the failing recovery-orchestration test.** Add to `MaintenanceServiceTests`:

```csharp
    [Fact]
    public async Task RecoverAllAsync_recovers_unended_sessions_and_isolates_failures()
    {
        var (svc, paths) = MakeService();
        // Finalized session: the scan must not touch it.
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0100_Webex_done", "Done");
        // Unended session: recoverable.
        const string open = "2026-07-03_0200_Webex_open";
        await WriteUnendedSessionAsync(paths, open);
        // Unended session engineered to FAIL: transcript.jsonl exists as a DIRECTORY, so the
        // recovery-marker append (TranscriptStore.AppendAsync) throws on Windows.
        const string broken = "2026-07-03_0300_Webex_broken";
        await WriteUnendedSessionAsync(paths, broken);
        Directory.CreateDirectory(paths.TranscriptJsonl(broken));

        var result = await svc.RecoverAllAsync(CancellationToken.None);

        Assert.Equal([open], result.RecoveredIds);
        var failure = Assert.Single(result.Failures);        // reported, not thrown, not aborting
        Assert.Equal(broken, failure.Id);
        Assert.False(string.IsNullOrEmpty(failure.Error));

        // The recovered session really finalized (RecoverIfNeededAsync semantics).
        var record = await new SessionStore(paths.SessionJson(open)).ReadAsync(CancellationToken.None);
        Assert.True(record!.Recovered);
        Assert.NotNull(record.EndedAtUtc);
        Assert.True(File.Exists(paths.SessionTxt(open)));

        // The untouched finalized session was not re-marked.
        var done = await new SessionStore(paths.SessionJson("2026-07-03_0100_Webex_done"))
            .ReadAsync(CancellationToken.None);
        Assert.False(done!.Recovered);
    }
```

- [ ] **Run it and watch it fail.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceTests"` — expected: build failure `error CS1061: 'MaintenanceService' does not contain a definition for 'RecoverAllAsync'`.
- [ ] **Implement RecoverAllAsync + the result record.** In `src/LocalScribe.App/Services/MaintenanceService.cs`: add above the class declaration (after the usings/namespace, before the `/// <summary>` of the class):

```csharp
/// <summary>Outcome of a launch/on-demand recovery scan (design 7.1): which sessions were
/// actually recovered, and per-id failures that were collected instead of aborting the rest.</summary>
public sealed record RecoveryScanResult(IReadOnlyList<string> RecoveredIds,
    IReadOnlyList<(string Id, string Error)> Failures);
```

and add inside the class, after `DeleteSessionAsync`:

```csharp
    /// <summary>Recovery scan (design 7.1): every session.json with EndedAtUtc == null gets
    /// SessionWriter.RecoverIfNeededAsync under its own per-session gate. Idempotent (the writer
    /// re-checks EndedAtUtc); per-id failures are collected, never thrown out - one corrupt
    /// folder must not strand the other interrupted sessions unrecovered. Cancellation is the
    /// only exception that propagates.</summary>
    public async Task<RecoveryScanResult> RecoverAllAsync(CancellationToken ct)
    {
        var unended = await new RecoveryScanner(paths).FindUnendedAsync(ct);
        var recovered = new List<string>();
        var failures = new List<(string Id, string Error)>();
        foreach (string id in unended)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                bool did = await RunForSessionAsync(id,
                    inner => new SessionWriter(paths, settings.Current, time)
                        .RecoverIfNeededAsync(id, inner), ct);
                if (did) recovered.Add(id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add((id, ex.Message)); }
        }
        return new RecoveryScanResult(recovered, failures);
    }
```

- [ ] **Run it green.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceTests"` — expected: PASS, 3 tests.
- [ ] **Commit.** `git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs && git commit -m "feat: MaintenanceService recovery-scan orchestration with per-id failure isolation"`
- [ ] **Write the failing cascade/bulk-regenerate tests.** Add to `MaintenanceServiceTests` (plus the tiny synchronous progress double — `Progress<T>` marshals via SynchronizationContext and is nondeterministic in tests):

```csharp
    [Fact]
    public async Task CascadeMatterAsync_regenerates_only_tagged_sessions_and_reports_progress()
    {
        var (svc, paths) = MakeService();
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0100_Webex_tagged", "Tagged",
            matterIds: ["M-2026-001"]);
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0130_Webex_untagged", "Untagged");
        var progress = new ImmediateProgress();

        await svc.CascadeMatterAsync("M-2026-001", progress, CancellationToken.None);

        Assert.True(File.Exists(paths.SessionTxt("2026-07-03_0100_Webex_tagged")));
        Assert.False(File.Exists(paths.SessionTxt("2026-07-03_0130_Webex_untagged")));
        Assert.Equal([1], progress.Reports);                 // one tagged session -> one report
    }

    [Fact]
    public async Task RegenerateAllAsync_touches_every_session_and_counts_up()
    {
        var (svc, paths) = MakeService();
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0100_Webex_a", "A");
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0130_Webex_b", "B");
        var progress = new ImmediateProgress();

        await svc.RegenerateAllAsync(progress, CancellationToken.None);

        Assert.True(File.Exists(paths.SessionTxt("2026-07-03_0100_Webex_a")));
        Assert.True(File.Exists(paths.SessionTxt("2026-07-03_0130_Webex_b")));
        Assert.Equal([1, 2], progress.Reports);              // monotonic completed-count
    }

    /// <summary>Synchronous IProgress: Progress&lt;T&gt; posts to a SynchronizationContext and
    /// would race the assertions; this records inline, deterministically.</summary>
    private sealed class ImmediateProgress : IProgress<int>
    {
        public readonly List<int> Reports = new();
        public void Report(int value) { lock (Reports) Reports.Add(value); }
    }
```

- [ ] **Run them and watch them fail.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceTests"` — expected: build failure `error CS1061: 'MaintenanceService' does not contain a definition for 'CascadeMatterAsync'`.
- [ ] **Implement cascade + bulk regenerate.** In `src/LocalScribe.App/Services/MaintenanceService.cs`, add inside the class after `RebuildIndexAsync`:

```csharp
    /// <summary>Matter rename cascade (design 4.4): regenerate the projections of every session
    /// whose meta tags this matter, each under its own per-session gate. Truth files untouched -
    /// session.txt resolves matter Name (Reference) live at render time.</summary>
    public async Task CascadeMatterAsync(string matterId, IProgress<int>? progress, CancellationToken ct)
    {
        var catalog = await ListSessionsAsync(ct);
        var targets = catalog.Sessions
            .Where(s => s.Meta.MatterIds.Contains(matterId, StringComparer.Ordinal))
            .Select(s => s.Id).ToList();
        await RegenerateEachAsync(targets, progress, ct);
    }

    /// <summary>Bulk regenerate (Settings page maintenance button, design 6.1): every catalog
    /// session re-renders with the CURRENT settings (timestamp style, vocabulary, ...).</summary>
    public async Task RegenerateAllAsync(IProgress<int>? progress, CancellationToken ct)
    {
        var catalog = await ListSessionsAsync(ct);
        await RegenerateEachAsync(catalog.Sessions.Select(s => s.Id).ToList(), progress, ct);
    }

    private async Task RegenerateEachAsync(IReadOnlyList<string> sessionIds, IProgress<int>? progress,
        CancellationToken ct)
    {
        var failures = new List<Exception>();
        int done = 0;
        foreach (string id in sessionIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await RunForSessionAsync(id, async inner =>
                {
                    await new SessionWriter(paths, settings.Current, time)
                        .RegenerateProjectionsAsync(id, inner);
                    return true;
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Collected, not fatal mid-loop: one broken folder must not stop the rest
                // (design 7.5 - the caller surfaces the aggregate via InfoBar/balloon).
                failures.Add(new InvalidOperationException($"regenerate failed for {id}: {ex.Message}", ex));
            }
            progress?.Report(++done);
        }
        if (failures.Count > 0)
            throw new AggregateException("one or more sessions failed to regenerate", failures);
    }
```

- [ ] **Run the whole class green.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MaintenanceServiceTests"` — expected: PASS, 5 tests.
- [ ] **Full gate + commit.** `dotnet test --filter "Category!=Fixture"` — expected: all green, 0 warnings on build. Then `git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs && git commit -m "feat: MaintenanceService cascade + bulk regenerate with progress and failure collection"`

### Task 10: ISettingsService implementation + Func<Settings> seam through SessionController/CompositionRoot

**Files:**
- Create: `src/LocalScribe.App/Services/SettingsService.cs`
- Create: `tests/LocalScribe.App.Tests/SettingsServiceTests.cs`
- Modify: `src/LocalScribe.Core/Live/SessionController.cs` (field line 40, constructor lines 91-98, StartAsync `_settings` uses, Session class lines 55-80, StopAsync lines 421+424 — post-Task-8 numbering shifts the StartAsync region by about +9 lines; anchors given below are exact text)
- Modify: `src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs` (full-file replacement, 33 lines)
- Modify: `src/LocalScribe.App/CompositionRoot.cs` (full-file replacement)
- Modify: `src/LocalScribe.App/App.xaml.cs` (lines 26-28 and 35)
- Modify: `tests/LocalScribe.App.Tests/CompositionRootTests.cs` (lines 16-19 only)
- Modify: `tests/LocalScribe.Core.Tests/SessionControllerTests.cs` (append one seam test)

**Interfaces:**
- Consumes: `ISettingsService` (Task 9); `SettingsStore` (`SettingsStore.cs:7-31`, `SaveAsync` stamps `SchemaVersion = Version` — Version becomes 3 via Task 1/2's bump and this code references the const so it tracks); existing `SessionController` internals read in Task 8; `WasapiCaptureSourceProvider` (`WasapiCaptureSourceProvider.cs:10-33`); `RemoteCapturePlanner.Plan(scanner.Scan(), settings.Remote)`; test doubles `FakeProvider`/`FakeEngineFactory`/`AmplitudeSpeechModel`/`ManualUtcTimeProvider` and the retained-audio assertion pattern from `Retention_never_skips_audio_files` (`SessionControllerTests.cs:64-74`: `File.Exists(paths.AudioFile(...))` false + `record.RetainedAudioSources` empty).
- Produces (locked — Task 24 and all later UI tasks compile against these exactly):
  - `public sealed class SettingsService : ISettingsService` with constructor `SettingsService(string settingsJsonPath, Settings initial)`;
  - `SessionController(StoragePaths paths, Func<Settings> settingsProvider, IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware, ICaptureSourceProvider captureProvider, Func<IClock> clockFactory, TimeProvider time, string appVersion)` plus the existing `Settings` overload delegating via `() => settings`;
  - `WasapiCaptureSourceProvider(Func<Settings> settingsProvider, IAudioSessionScanner scanner)` (primary constructor) plus the existing `Settings` overload;
  - `CompositionRoot.Build() : (SessionController Controller, ISettingsService Settings, StoragePaths Paths)`.

(The seam signatures above are the locked contract; `SessionController` keeps a classic constructor pair rather than a literal C# primary constructor to avoid renaming every `_field` use in the 440-line file — call sites are byte-for-byte identical either way.)

- [ ] **Write the failing SettingsService test.** Create `tests/LocalScribe.App.Tests/SettingsServiceTests.cs` with exactly:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ls-settings-" + Guid.NewGuid().ToString("N"));
    public SettingsServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public async Task SaveAsync_persists_swaps_current_and_raises_changed_with_old_and_new()
    {
        string path = Path.Combine(_dir, "settings.json");
        var initial = new Settings();
        var svc = new SettingsService(path, initial);
        Assert.Same(initial, svc.Current);

        Settings? oldSeen = null, newSeen = null;
        svc.Changed += (o, n) => { oldSeen = o; newSeen = n; };

        var updated = initial with { AudioRetention = "never", Timestamps = "wallclock" };
        await svc.SaveAsync(updated, CancellationToken.None);

        Assert.Equal("never", svc.Current.AudioRetention);   // Current swapped
        Assert.Same(initial, oldSeen);                       // Changed carries the OLD snapshot...
        Assert.Same(svc.Current, newSeen);                   // ...and the NEW one (post-swap)
        Assert.Equal("wallclock", newSeen!.Timestamps);

        // Persisted atomically via SettingsStore: a fresh reader sees the saved values.
        var reloaded = await new SettingsStore(path).LoadOrDefaultAsync(CancellationToken.None);
        Assert.Equal("never", reloaded.AudioRetention);
        Assert.Equal("wallclock", reloaded.Timestamps);
    }

    [Fact]
    public async Task SaveAsync_stamps_the_current_schema_version_on_the_held_snapshot()
    {
        string path = Path.Combine(_dir, "settings.json");
        var svc = new SettingsService(path, new Settings());
        await svc.SaveAsync(new Settings { Language = "en" }, CancellationToken.None);
        // Current must equal what a reload sees - including SchemaVersion (SettingsStore stamps
        // the file; the service stamps the in-memory snapshot to match).
        Assert.Equal(SettingsStore.Version, svc.Current.SchemaVersion);
        Assert.Equal("en", svc.Current.Language);
    }
}
```

- [ ] **Run it and watch it fail to compile.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsServiceTests"` — expected: build failure `error CS0246: The type or namespace name 'SettingsService' could not be found`.
- [ ] **Implement SettingsService.** Create `src/LocalScribe.App/Services/SettingsService.cs` with exactly:

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.Services;

/// <summary>The app's single mutable Settings holder (design 6.2, first mutation path).
/// SaveAsync persists via SettingsStore (atomic write), swaps Current, then raises
/// Changed(old, new). Thread-safety is reference-swap + event ONLY: Settings is an immutable
/// record, so any reader of Current always sees one coherent snapshot; there is no lock and no
/// torn state by construction. Consumers that must react subscribe to Changed or re-read
/// Current at their next natural decision point (SessionController does so at StartAsync).</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly SettingsStore _store;
    private volatile Settings _current;

    public SettingsService(string settingsJsonPath, Settings initial)
        => (_store, _current) = (new SettingsStore(settingsJsonPath), initial);

    public Settings Current => _current;
    public event Action<Settings, Settings>? Changed;

    public async Task SaveAsync(Settings updated, CancellationToken ct)
    {
        // Stamp the version the store writes, so the in-memory snapshot equals a reload.
        var stamped = updated with { SchemaVersion = SettingsStore.Version };
        await _store.SaveAsync(stamped, ct);
        var old = _current;
        _current = stamped;              // swap only after the disk write succeeded
        Changed?.Invoke(old, stamped);
    }
}
```

- [ ] **Run it green.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsServiceTests"` — expected: PASS, 2 tests.
- [ ] **Commit.** `git add src/LocalScribe.App/Services/SettingsService.cs tests/LocalScribe.App.Tests/SettingsServiceTests.cs && git commit -m "feat: SettingsService - persist via SettingsStore, swap Current, raise Changed(old,new)"`
- [ ] **Write the failing controller seam test.** Append to `tests/LocalScribe.Core.Tests/SessionControllerTests.cs` (after the Task 8 additions, before the class's closing brace):

```csharp
    [Fact]
    public async Task StartAsync_reads_the_current_settings_not_a_construction_snapshot()
    {
        // Design 6.2 seam: AudioRetention flips to "never" AFTER construction but BEFORE Start.
        // The session must create no audio writers - same observable effect the existing
        // Retention_never_skips_audio_files test pins for a construction-time "never".
        var paths = new StoragePaths(_root);
        var provider = new FakeProvider();
        var clock = new FakeClock();
        Settings current = new();                            // retention "keep" at construction
        var c = new SessionController(paths, () => current, new FakeEngineFactory(),
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            provider, () => clock,
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 2, 6, 0, 0, TimeSpan.Zero)), "0.4.0");

        current = new Settings { AudioRetention = "never" }; // the swap SettingsService.SaveAsync performs

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(paths.AudioFile(id!, SourceKind.Local, AudioFormat.Flac)));
        Assert.False(File.Exists(paths.AudioFile(id!, SourceKind.Remote, AudioFormat.Flac)));
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Empty(record!.RetainedAudioSources);
    }
```

- [ ] **Run it and watch it fail to compile.** `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SessionControllerTests"` — expected: build failure `error CS1660: Cannot convert lambda expression to type 'Settings' because it is not a delegate type` (no `Func<Settings>` constructor exists yet).
- [ ] **Convert SessionController to the Func<Settings> seam.** In `src/LocalScribe.Core/Live/SessionController.cs`, apply these exact edits:

  1. Field (line 40): replace `    private readonly Settings _settings;` with `    private readonly Func<Settings> _settingsProvider;`
  2. Constructor (lines 91-98): replace the whole existing constructor with:

```csharp
    public SessionController(StoragePaths paths, Func<Settings> settingsProvider,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion)
        => (_paths, _settingsProvider, _engineFactory, _vadModelFactory, _hardware, _captureProvider,
            _clockFactory, _time, _appVersion)
         = (paths, settingsProvider, engineFactory, vadModelFactory, hardware, captureProvider,
            clockFactory, time, appVersion);

    /// <summary>Convenience overload: a fixed Settings snapshot. Keeps every pre-Stage-4 call
    /// site and test compiling unchanged; production passes a live provider (design 6.2) so
    /// per-session inputs resolve at StartAsync, not at construction.</summary>
    public SessionController(StoragePaths paths, Settings settings, IEngineFactory engineFactory,
        Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware,
        ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion)
        : this(paths, () => settings, engineFactory, vadModelFactory, hardware, captureProvider,
            clockFactory, time, appVersion)
    {
    }
```

  3. Session snapshot field: inside the private `Session` class, after `public required StrongBox<string?> LastModel;` add:

```csharp
        // Start-time Settings snapshot (design 6.2): finalize (StopAsync) renders and records
        // with the settings the session STARTED under, even if a save lands mid-session.
        public required Settings Settings;
```

  4. Resolve at Start: in `StartAsync`, immediately after `var clock = _clockFactory();` add:

```csharp
            // Design 6.2 settings seam: per-session inputs resolve NOW, not at construction.
            var settings = _settingsProvider();
```

  5. Replace every `_settings` use in `StartAsync` with the local `settings` (anchors, post-Task-8 text):
     - `var boot = await SessionBootstrap.StartAsync(_paths, _settings, app,` -> `var boot = await SessionBootstrap.StartAsync(_paths, settings, app,`
     - `var plan = BackendSelector.Select(_hardware.Probe(), _settings);` -> `... , settings);`
     - `var language = new LanguageResolver(_settings.Language);` -> `new LanguageResolver(settings.Language);`
     - `string prompt = new VocabularyProvider(_settings.Vocabulary, new Dictionary<string, Matter>())` -> `new VocabularyProvider(settings.Vocabulary, ...)`
     - `if (_settings.AudioRetention != "never")` -> `if (settings.AudioRetention != "never")`
     - both audio-writer lines: `_paths.AudioFile(boot.Id, SourceKind.Local, _settings.AudioFormat), _settings.AudioFormat)` -> `..., settings.AudioFormat), settings.AudioFormat)` (and the same for the `SourceKind.Remote` line — four `_settings.AudioFormat` occurrences total)
  6. Session construction: in the `_session = new Session { ... }` initializer add `Settings = settings,` after `Retained = retained,`.
  7. StopAsync finalize (anchors at old lines 421 and 424):
     - `Language = s.Language.Locked ?? _settings.Language,` -> `Language = s.Language.Locked ?? s.Settings.Language,`
     - `await new SessionWriter(_paths, _settings, _time).RegenerateProjectionsAsync(s.Id, ct);` -> `await new SessionWriter(_paths, s.Settings, _time).RegenerateProjectionsAsync(s.Id, ct);`

- [ ] **Convert WasapiCaptureSourceProvider to the same seam.** Replace the entire contents of `src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs` with:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using NAudio.CoreAudioApi;
namespace LocalScribe.Core.Live;

/// <summary>Thin adapter (Humble Object) over the real WASAPI sources. Re-plans the remote on
/// every call (a Resume leg re-scans - the meeting app may have changed); the caller snapshots
/// the FIRST leg's result into session.json. Settings resolve through the injected provider at
/// capture-plan time (design 6.2): a settings save between sessions takes effect at the next
/// Start/Resume without rebuilding the provider. Pinned-mic mode is a Stage 7 concern: 3a
/// always follows the Communications default and records that honestly.</summary>
public sealed class WasapiCaptureSourceProvider(Func<Settings> settingsProvider,
    IAudioSessionScanner scanner) : ICaptureSourceProvider
{
    /// <summary>Convenience overload: a fixed Settings snapshot (pre-Stage-4 call sites/tests).</summary>
    public WasapiCaptureSourceProvider(Settings settings, IAudioSessionScanner scanner)
        : this(() => settings, scanner)
    {
    }

    public (ICaptureSource Source, MicSnapshot Snapshot) CreateMic(IClock clock)
    {
        var mic = new MicCaptureSource(clock, Role.Communications);
        return (mic, new MicSnapshot { Mode = MicMode.FollowDefault, Name = mic.DeviceName });
    }

    public (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock)
    {
        var plan = RemoteCapturePlanner.Plan(scanner.Scan(), settingsProvider().Remote);
        ICaptureSource source = plan.Mode == RemoteMode.PerProcess
            ? new ProcessLoopbackCapture(plan.Pid!.Value, clock)
            : ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock);
        return (source, new RemoteSnapshot
        { Mode = plan.Mode, App = plan.App, FellBackToSystemMix = plan.FellBackToSystemMix });
    }
}
```

- [ ] **Run Core tests green.** `dotnet test tests/LocalScribe.Core.Tests --filter "Category!=Fixture"` — expected: PASS including the new seam test and all 12 pre-existing+Task-8 SessionController tests (the `Settings` overload keeps `LiveTestDoubles.MakeController` compiling untouched).
- [ ] **Commit.** `git add src/LocalScribe.Core/Live/SessionController.cs src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs && git commit -m "feat: SessionController/WasapiCaptureSourceProvider resolve settings at Start via Func<Settings> seam"`
- [ ] **Rewire CompositionRoot to return the service.** Replace the entire contents of `src/LocalScribe.App/CompositionRoot.cs` with:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.App;

/// <summary>Builds the app's single SessionController over the real adapters, plus the
/// SettingsService that owns the mutable Settings from here on (design 6.2). Construction
/// only - no capture, no models touched until StartAsync. Settings load synchronously at
/// startup (small local file). Task 24 completes the Stage 4 wiring (MainWindow,
/// MaintenanceService, recovery scan); here Build only swaps the raw Settings record for the
/// ISettingsService that owns it.</summary>
public static class CompositionRoot
{
    public static (SessionController Controller, ISettingsService Settings, StoragePaths Paths) Build()
    {
        string settingsPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "settings.json");
        // Build() runs inline from App.OnStartup, i.e. on the WPF UI thread under a
        // DispatcherSynchronizationContext. Core's storage helpers await with no
        // ConfigureAwait(false), so a plain "LoadOrDefaultAsync(...).GetAwaiter().GetResult()"
        // here would deadlock whenever settings.json exists and the read completes async: the
        // continuation would try to post back to this same UI thread, which is already blocked
        // in GetResult(). Task.Run moves the whole async call onto a pool thread where
        // SynchronizationContext.Current is null, so its continuations never try to post back
        // here - GetResult() then only blocks until the pool work finishes.
        var loaded = Task.Run(() => new SettingsStore(settingsPath).LoadOrDefaultAsync(default))
            .GetAwaiter().GetResult();

        // SettingsService FIRST: everything downstream resolves settings through it.
        // StoragePaths deliberately snapshots the root ONCE - a storage-root change is
        // restart-required by design (6.2), never a live re-point.
        var settingsService = new SettingsService(settingsPath, loaded);
        var paths = new StoragePaths(settingsService.Current.StorageRoot);
        string appVersion = typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var controller = new SessionController(paths, () => settingsService.Current,
            new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(),
            new WasapiCaptureSourceProvider(() => settingsService.Current, new WasapiSessionScanner()),
            () => new StopwatchClock(), TimeProvider.System, appVersion);
        return (controller, settingsService, paths);
    }
}
```

  (Note: the `using LocalScribe.Core.Model;` line is dropped — no type from it is referenced any more; keeping it would be an unused-using warning and the build is warning-gated.)
- [ ] **Update the two call sites minimally.** In `src/LocalScribe.App/App.xaml.cs` replace lines 26-28:

```csharp
        var (controller, settings, paths) = CompositionRoot.Build();
        var session = new ViewModels.SessionViewModel(controller, settings,
            dispatch: a => Dispatcher.BeginInvoke(a));
```

with:

```csharp
        var (controller, settingsService, paths) = CompositionRoot.Build();
        // SessionViewModel still takes a plain Settings snapshot; live propagation of saves
        // into the VMs is Task 24's wiring - Stage 4 policy is next-Start effect anyway (6.2).
        var session = new ViewModels.SessionViewModel(controller, settingsService.Current,
            dispatch: a => Dispatcher.BeginInvoke(a));
```

and replace line 35 `        _overlayVm = new ViewModels.OverlayViewModel(session, settings);` with `        _overlayVm = new ViewModels.OverlayViewModel(session, settingsService.Current);`
- [ ] **Update CompositionRootTests to the new tuple.** In `tests/LocalScribe.App.Tests/CompositionRootTests.cs` replace lines 16-19:

```csharp
        var (controller, settings, paths) = CompositionRoot.Build();
        Assert.Equal(SessionState.Idle, controller.State);
        Assert.False(paths.Root.Contains('%'));          // env vars expanded by StoragePaths
        Assert.NotNull(settings);
```

with:

```csharp
        var (controller, settingsService, paths) = CompositionRoot.Build();
        Assert.Equal(SessionState.Idle, controller.State);
        Assert.False(paths.Root.Contains('%'));          // env vars expanded by StoragePaths
        Assert.NotNull(settingsService.Current);
```

  No other edit in this file: the deadlock regression `TaskRun_wrap_breaks_the_sync_over_async_UI_deadlock` (`CompositionRootTests.cs:50`) and the best-effort integration smoke (`CompositionRootTests.cs:117`) pin the exact load expression Build still uses (`Task.Run(() => new SettingsStore(settingsPath).LoadOrDefaultAsync(default)).GetAwaiter().GetResult()`), which this task deliberately did not change — they must stay green untouched.
- [ ] **Run App tests green (deadlock regression included).** `dotnet test tests/LocalScribe.App.Tests --filter "Category!=Fixture"` — expected: PASS, including `CompositionRootTests.TaskRun_wrap_breaks_the_sync_over_async_UI_deadlock`, `CompositionRootTests.Settings_load_expression_does_not_deadlock_under_a_single_threaded_sync_context`, both `SettingsServiceTests`, all 5 `MaintenanceServiceTests`, and the pre-existing VM tests (the `SessionViewModel(controller, Settings, ...)` signature is unchanged).
- [ ] **Full gate + commit.** `dotnet build` (expected: 0 warnings) then `dotnet test --filter "Category!=Fixture"` (expected: all green). Then `git add src/LocalScribe.App/CompositionRoot.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/CompositionRootTests.cs && git commit -m "feat: CompositionRoot builds SettingsService first and returns ISettingsService"`

---

### Task 11: Keyed WindowStateStore

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/WindowStateStore.cs` (full rework of the 31-line file)
- Modify: `src/LocalScribe.App/OverlayWindow.xaml.cs` (line 38 `_stateStore.Load()`, line 49 `_stateStore.Save(Left, Top)`)
- Test: `tests/LocalScribe.App.Tests/WindowStateStoreTests.cs` (full rewrite - the old `Load()`/`Save(x,y)` API is removed)

**Interfaces:**
- Consumes: nothing new (existing `WindowStateStore(string path)` ctor shape kept, so `App.xaml.cs:38` compiles unchanged).
- Produces (locked - Task 14 uses key `"main"`, the read-view task uses `"readViewDefault"`, overlay uses `"overlay"`):
  - `public sealed record WindowPlacement(double X, double Y, double? Width = null, double? Height = null);` (namespace `LocalScribe.App.ViewModels`)
  - `public WindowPlacement? Load(string key)` / `public void Save(string key, WindowPlacement placement)` on `WindowStateStore`.
- File format produced: `{"windows":{"overlay":{"x":..,"y":..},"main":{"x":..,"y":..,"width":..,"height":..}}}` (camelCase, null width/height omitted). Legacy pre-Stage-4 root `{"X":123.5,"Y":67.25}` shape-detects on read as the `"overlay"` entry. No schemaVersion - throwaway UI state, every failure is null/ignored (design section 8 exemption).

Steps:

- [ ] Replace `tests/LocalScribe.App.Tests/WindowStateStoreTests.cs` with the new-API tests (this deliberately breaks compilation - the API is being reshaped, so red-via-compile is the expected first state):

```csharp
using System.IO;
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class WindowStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "ls-ws-" + Guid.NewGuid().ToString("N"), "window-state.json");
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_path)!, true); } catch { } }

    [Fact]
    public void Keyed_roundtrip_with_size()
    {
        new WindowStateStore(_path).Save("main", new WindowPlacement(10.5, 20.25, 1080, 720));
        Assert.Equal(new WindowPlacement(10.5, 20.25, 1080, 720),
            new WindowStateStore(_path).Load("main"));
    }

    [Fact]
    public void Keyed_roundtrip_position_only()
    {
        new WindowStateStore(_path).Save("overlay", new WindowPlacement(123.5, 67.25));
        Assert.Equal(new WindowPlacement(123.5, 67.25),
            new WindowStateStore(_path).Load("overlay"));
    }

    [Fact]
    public void Save_preserves_other_keys()
    {
        var store = new WindowStateStore(_path);
        store.Save("overlay", new WindowPlacement(1, 2));
        store.Save("main", new WindowPlacement(3, 4, 800, 600));
        Assert.Equal(new WindowPlacement(1, 2), store.Load("overlay"));
        Assert.Equal(new WindowPlacement(3, 4, 800, 600), store.Load("main"));
    }

    [Fact]
    public void Legacy_bare_xy_shape_detects_as_overlay()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Exact output of the pre-Stage-4 writer: Serialize(new State(x, y)) with default options.
        File.WriteAllText(_path, "{\"X\":123.5,\"Y\":67.25}");
        var store = new WindowStateStore(_path);
        Assert.Equal(new WindowPlacement(123.5, 67.25), store.Load("overlay"));
        Assert.Null(store.Load("main"));                   // legacy file only knows the overlay
    }

    [Fact]
    public void Save_over_legacy_file_keeps_the_migrated_overlay_entry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"X\":123.5,\"Y\":67.25}");
        var store = new WindowStateStore(_path);
        store.Save("main", new WindowPlacement(3, 4, 800, 600));   // read-modify-write folds legacy in
        Assert.Equal(new WindowPlacement(123.5, 67.25), store.Load("overlay"));
        Assert.Equal(new WindowPlacement(3, 4, 800, 600), store.Load("main"));
    }

    [Fact]
    public void Absent_corrupt_or_unknown_key_returns_null()
    {
        var store = new WindowStateStore(_path);
        Assert.Null(store.Load("overlay"));                // absent file
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{not json");
        Assert.Null(store.Load("overlay"));                // corrupt: throwaway file never throws
        File.WriteAllText(_path, "{\"windows\":{\"overlay\":{\"x\":1,\"y\":2}}}");
        Assert.Null(store.Load("main"));                   // unknown key
        File.WriteAllText(_path, "{}");
        Assert.Null(store.Load("overlay"));                // neither keyed nor legacy shape
    }
}
```

- [ ] Run `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~WindowStateStoreTests"` - expected failure: compile errors (`CS0246: The type or namespace name 'WindowPlacement' could not be found`, `CS1501`/`CS1503` on the new `Load`/`Save` shapes).

- [ ] Replace `src/LocalScribe.App/ViewModels/WindowStateStore.cs` with:

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace LocalScribe.App.ViewModels;

/// <summary>Remembered window geometry: X/Y always, Width/Height only for resizable windows
/// (the overlay pill saves position only).</summary>
public sealed record WindowPlacement(double X, double Y, double? Width = null, double? Height = null);

/// <summary>Volatile per-window placement (spec 7: throwaway window-state.json, NOT settings,
/// deliberately no schemaVersion - design section 8 exemption). Keyed map
/// {"windows":{"overlay":{"x":..,"y":..},"main":{"x":..,"y":..,"width":..,"height":..}}};
/// a legacy pre-Stage-4 bare {x,y} root shape-detects on read as the "overlay" entry.
/// Any failure is null/ignored - this file is never truth, never worth an error.</summary>
public sealed class WindowStateStore(string path)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Placement(double X, double Y, double? Width = null, double? Height = null);

    // One shape reads both formats: keyed files bind Windows, legacy files bind X/Y.
    private sealed record FileShape(
        Dictionary<string, Placement>? Windows = null, double? X = null, double? Y = null);

    public WindowPlacement? Load(string key)
    {
        var map = ReadMap();
        return map is not null && map.TryGetValue(key, out var p)
            ? new WindowPlacement(p.X, p.Y, p.Width, p.Height) : null;
    }

    public void Save(string key, WindowPlacement placement)
    {
        try
        {
            // Read-modify-write so saving one window's placement never drops another's
            // (and folds a legacy bare {x,y} file into the keyed map as "overlay").
            var map = ReadMap() ?? new Dictionary<string, Placement>(StringComparer.Ordinal);
            map[key] = new Placement(placement.X, placement.Y, placement.Width, placement.Height);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new FileShape(map), JsonOpts));
        }
        catch { /* volatile state - losing it costs one re-drag */ }
    }

    private Dictionary<string, Placement>? ReadMap()
    {
        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), JsonOpts);
            if (shape?.Windows is { } keyed)
                return new Dictionary<string, Placement>(keyed, StringComparer.Ordinal);
            if (shape is { X: { } lx, Y: { } ly })     // legacy bare {x,y}: the overlay's position
                return new Dictionary<string, Placement>(StringComparer.Ordinal)
                { ["overlay"] = new Placement(lx, ly) };
            return null;
        }
        catch { return null; }
    }
}
```

- [ ] Update the two `OverlayWindow.xaml.cs` call sites (namespace `LocalScribe.App.ViewModels` is already imported there):
  - Line 38: `var pos = _stateStore.Load();` becomes `var pos = _stateStore.Load("overlay");` (the following `pos?.X ?? double.NaN` / `pos?.Y ?? double.NaN` lines compile unchanged against `WindowPlacement?`).
  - Line 49: `_stateStore.Save(Left, Top);` becomes `_stateStore.Save("overlay", new WindowPlacement(Left, Top));`

- [ ] Run `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~WindowStateStoreTests"` - expected: 6 tests PASS.
- [ ] Run `dotnet build LocalScribe.slnx` - expected: 0 warnings, and `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` - expected: all green (nothing else consumed the old API besides OverlayWindow).
- [ ] Commit: `git add src/LocalScribe.App/ViewModels/WindowStateStore.cs src/LocalScribe.App/OverlayWindow.xaml.cs tests/LocalScribe.App.Tests/WindowStateStoreTests.cs && git commit -m "feat: keyed WindowStateStore (overlay/main) with legacy shape-detect migration"`

---

### Task 12: Single-instance guard

**Files:**
- Create: `src/LocalScribe.App/Services/SingleInstance.cs`
- Test: `tests/LocalScribe.App.Tests/SingleInstanceTests.cs`

**Interfaces:**
- Consumes: nothing (pure `System.Threading`; `System.Threading` is an implicit using on net10.0).
- Produces (locked - Task 14 wires it into `App.OnStartup`):
  - `public sealed class SingleInstance : IDisposable { public static SingleInstance? TryAcquire(string name, Action onActivateRequested); public static bool SignalExisting(string name); }`
  - Contract: `Local\<name>` named mutex (per-session, therefore per-user - design 7.2) + `Local\<name>-activate` named auto-reset event; a background thread waits on the event and invokes `onActivateRequested` ON THAT THREAD - callers must pass a dispatch-wrapped action (Task 14 passes `() => Dispatcher.BeginInvoke(...)`). `Dispose` stops the wait thread and closes both handles so the name is immediately re-acquirable.

Steps:

- [ ] Write `tests/LocalScribe.App.Tests/SingleInstanceTests.cs` (unique per-test kernel-object names via Guid - xunit runs test classes in parallel and stale names must never collide across runs):

```csharp
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SingleInstanceTests
{
    private static string UniqueName() => "ls-si-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void First_acquire_succeeds_and_second_returns_null()
    {
        string name = UniqueName();
        using var first = SingleInstance.TryAcquire(name, () => { });
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name, () => { }));
    }

    [Fact]
    public void SignalExisting_fires_the_holders_callback()
    {
        string name = UniqueName();
        int fired = 0;
        using var first = SingleInstance.TryAcquire(name, () => Interlocked.Increment(ref fired));
        Assert.NotNull(first);

        Assert.True(SingleInstance.SignalExisting(name));
        // Observable effect, never Thread.Sleep: the callback runs on the guard's wait thread.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref fired) >= 1, TimeSpan.FromSeconds(5)),
            "activate callback did not fire within 5s");
    }

    [Fact]
    public void SignalExisting_returns_false_when_no_instance_holds_the_name()
    {
        Assert.False(SingleInstance.SignalExisting(UniqueName()));
    }

    [Fact]
    public void Dispose_releases_the_name_so_reacquire_succeeds()
    {
        string name = UniqueName();
        var first = SingleInstance.TryAcquire(name, () => { });
        Assert.NotNull(first);
        first!.Dispose();

        using var second = SingleInstance.TryAcquire(name, () => { });
        Assert.NotNull(second);
    }
}
```

- [ ] Run `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~SingleInstanceTests"` - expected failure: `CS0246: The type or namespace name 'SingleInstance' could not be found`.

- [ ] Create `src/LocalScribe.App/Services/SingleInstance.cs`:

```csharp
namespace LocalScribe.App.Services;

/// <summary>Per-user single-instance guard (design 7.2): Stage 4 makes matters.json
/// read-modify-write load-bearing, and two instances could double-record. The first instance
/// owns a named mutex and parks a background thread on a named activate event; a second
/// instance signals that event (SignalExisting) and exits. The activate callback runs ON THE
/// BACKGROUND WAIT THREAD - callers pass a dispatch-wrapped action (e.g. Dispatcher.BeginInvoke)
/// so the callback itself never blocks this thread.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly ManualResetEvent _stop = new(initialState: false);
    private readonly Thread _waiter;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivateRequested)
    {
        (_mutex, _activate) = (mutex, activate);
        _waiter = new Thread(() =>
        {
            // WaitAny returns the LOWEST signaled index on ties, so _stop (index 0) always
            // wins over a pending activate and Dispose deterministically ends the loop.
            while (WaitHandle.WaitAny([_stop, _activate]) == 1)
                onActivateRequested();
        })
        { IsBackground = true, Name = "LocalScribe.SingleInstance" };
        _waiter.Start();
    }

    /// <summary>Null when another instance already holds the name. "Local\" scopes the kernel
    /// objects to this logon session, so two Windows users on one machine can each run
    /// LocalScribe without fighting over the guard.</summary>
    public static SingleInstance? TryAcquire(string name, Action onActivateRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivateRequested);
        var mutex = new Mutex(initiallyOwned: true, "Local\\" + name, out bool createdNew);
        if (!createdNew) { mutex.Dispose(); return null; }
        var activate = new EventWaitHandle(initialState: false, EventResetMode.AutoReset,
            "Local\\" + name + "-activate");
        return new SingleInstance(mutex, activate, onActivateRequested);
    }

    /// <summary>Second-instance path: ping the holder's activate event so it raises its main
    /// window. False when no holder exists (or the event is inaccessible) - the caller exits
    /// either way, so failure here must never throw.</summary>
    public static bool SignalExisting(string name)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting("Local\\" + name + "-activate", out var handle))
                return false;
            using (handle) handle.Set();
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _stop.Set();
        _waiter.Join();   // callback is dispatch-wrapped (non-blocking), so this cannot hang
        // ReleaseMutex is thread-affine (ownership was taken on TryAcquire's caller thread);
        // if Dispose runs on another thread it throws - swallowed, because closing the last
        // handle below destroys the named kernel object regardless, which is exactly what
        // makes a re-acquire succeed.
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
        _activate.Dispose();
        _stop.Dispose();
    }
}
```

- [ ] Run `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~SingleInstanceTests"` - expected: 4 tests PASS.
- [ ] Run `dotnet build LocalScribe.slnx` - expected: 0 warnings.
- [ ] Commit: `git add src/LocalScribe.App/Services/SingleInstance.cs tests/LocalScribe.App.Tests/SingleInstanceTests.cs && git commit -m "feat: single-instance guard (named mutex + activate signal, per-user)"`

App wiring (TryAcquire at startup / SignalExisting-and-exit / activate opens MainWindow) lands in Task 14 - it needs the main window to exist as the activation target.

---

### Task 13: Capture-exclusion policy service

**Files:**
- Modify: `src/LocalScribe.App/NativeWindowInterop.cs` (add `WDA_NONE` const near line 10 and an `IncludeInCapture` method after `ExcludeFromCapture`, line 22)
- Create: `src/LocalScribe.App/CaptureExclusion.cs` (code-behind helper - `System.Windows` allowed, deliberately NOT under ViewModels/)
- Create: `src/LocalScribe.App/Services/CaptureExclusionPolicy.cs` (WPF-free pure decision logic)
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml.cs` (ctor gains `ISettingsService`; apply on SourceInitialized; re-apply on Changed)
- Modify: `src/LocalScribe.App/TrayIconHost.cs` (ctor line 25 gains `ISettingsService`; `OpenLiveView` line 97 passes it)
- Modify: `src/LocalScribe.App/App.xaml.cs` (line 30: pass the settings service into `TrayIconHost`)
- Test: `tests/LocalScribe.App.Tests/CaptureExclusionPolicyTests.cs`

**Interfaces:**
- Consumes: `ISettingsService` (locked: `Settings Current { get; }`, `event Action<Settings, Settings>? Changed`) - interface + concrete implementation + an `ISettingsService settingsService` local in `App.OnStartup` are produced by Task 10 (settings plumbing); if Task 10's merged wiring named that local differently, apply the App.xaml.cs edit below to that identifier. Also consumes `Settings.Privacy` / `PrivacySetting.ExcludeWindowsFromCapture` (locked model additions, Tasks 1/2).
- Produces (Task 14's MainWindow and the read-view task compile against these):
  - `public static class CaptureExclusion { public static void Apply(System.Windows.Window window, bool exclude); }` (namespace `LocalScribe.App`)
  - `public static class CaptureExclusionPolicy { public static bool ShouldReapply(Settings oldSettings, Settings newSettings); }` (namespace `LocalScribe.App.Services`)
  - `NativeWindowInterop.IncludeInCapture(Window window)` (WDA_NONE)
  - `LiveViewWindow(SessionViewModel session, TranscriptLinesViewModel lines, ISettingsService settings)` (new ctor shape)
  - `TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines, StoragePaths paths, ISettingsService settingsService)` (intermediate shape; Task 14 appends one more parameter)

Testability note, stated up front: `SetWindowDisplayAffinity` requires a real HWND, so `CaptureExclusion.Apply` and the window wiring CANNOT be unit-tested headlessly. The helper is kept one-line-thin, the only decision logic (when to re-apply) is extracted into the WPF-free `CaptureExclusionPolicy` and unit-tested, and end-to-end verification is a Stage 4 smoke-runbook item (share the screen in a real Webex call, confirm the live view / main window vanish from the share, flip the Privacy toggle, confirm they appear).

Steps:

- [ ] Write `tests/LocalScribe.App.Tests/CaptureExclusionPolicyTests.cs`:

```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class CaptureExclusionPolicyTests
{
    [Fact]
    public void Reapply_only_when_the_privacy_toggle_actually_changed()
    {
        var on = new Settings { Privacy = new PrivacySetting { ExcludeWindowsFromCapture = true } };
        var off = new Settings { Privacy = new PrivacySetting { ExcludeWindowsFromCapture = false } };

        Assert.True(CaptureExclusionPolicy.ShouldReapply(on, off));
        Assert.True(CaptureExclusionPolicy.ShouldReapply(off, on));
        Assert.False(CaptureExclusionPolicy.ShouldReapply(off, off));
        // Unrelated settings churn (e.g. timestamps style) must never touch the HWND.
        Assert.False(CaptureExclusionPolicy.ShouldReapply(on, on with { Timestamps = "wallclock" }));
    }
}
```

- [ ] Run `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~CaptureExclusionPolicyTests"` - expected failure: `CS0246: 'CaptureExclusionPolicy' could not be found`.

- [ ] Create `src/LocalScribe.App/Services/CaptureExclusionPolicy.cs`:

```csharp
using LocalScribe.Core.Model;
namespace LocalScribe.App.Services;

/// <summary>Pure decision half of transcript-window capture exclusion (the one-line interop
/// half is src/LocalScribe.App/CaptureExclusion.cs): re-apply display affinity only when the
/// Privacy toggle actually changed, so unrelated settings saves never touch the HWND.</summary>
public static class CaptureExclusionPolicy
{
    public static bool ShouldReapply(Settings oldSettings, Settings newSettings)
        => oldSettings.Privacy.ExcludeWindowsFromCapture
           != newSettings.Privacy.ExcludeWindowsFromCapture;
}
```

- [ ] Run `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~CaptureExclusionPolicyTests"` - expected: PASS.
- [ ] Commit: `git add src/LocalScribe.App/Services/CaptureExclusionPolicy.cs tests/LocalScribe.App.Tests/CaptureExclusionPolicyTests.cs && git commit -m "feat: capture-exclusion re-apply decision policy (WPF-free, tested)"`

- [ ] Edit `src/LocalScribe.App/NativeWindowInterop.cs`: after line 10 (`private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;`) add:

```csharp
    private const uint WDA_NONE = 0x0;
```

  and after the `ExcludeFromCapture` method (line 22) add:

```csharp
    /// <summary>WDA_NONE: undo ExcludeFromCapture - the window becomes visible to screen
    /// shares/recordings again (the Privacy toggle was turned off).</summary>
    public static void IncludeInCapture(Window window)
        => SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WDA_NONE);
```

- [ ] Create `src/LocalScribe.App/CaptureExclusion.cs`:

```csharp
using System.Windows;
namespace LocalScribe.App;

/// <summary>One-line policy shim over NativeWindowInterop for transcript-bearing windows
/// (design section 2: MainWindow, read views and the live view are capture-excluded by
/// default, governed by settings.Privacy.ExcludeWindowsFromCapture; OverlayWindow keeps its
/// own OverlaySetting.ExcludeFromCapture, unchanged). Must run after the HWND exists
/// (OnSourceInitialized or later). NOT headlessly unit-testable - SetWindowDisplayAffinity
/// needs a real HWND - so verification is a Stage 4 smoke-runbook item; the pure
/// decide-to-reapply logic lives in Services/CaptureExclusionPolicy and IS unit-tested.</summary>
public static class CaptureExclusion
{
    public static void Apply(Window window, bool exclude)
    {
        if (exclude) NativeWindowInterop.ExcludeFromCapture(window);
        else NativeWindowInterop.IncludeInCapture(window);
    }
}
```

- [ ] Replace `src/LocalScribe.App/LiveViewWindow.xaml.cs` with (adds the settings ctor param, `OnSourceInitialized` apply, `Changed` re-apply; `OnLinesChanged`/`OnContentRendered`/`OnClosing`/`FindScrollViewer` bodies are byte-identical to the current file):

```csharp
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
namespace LocalScribe.App;

/// <summary>Thin shell over the shared VMs. Bottom-sticky auto-scroll: follows new lines only
/// while the user is at the bottom. Closing HIDES - a recording must never die with a window;
/// only tray Exit shuts the app down. Capture-excluded per settings.Privacy (design section 2);
/// this is a hide-on-close singleton that lives for the app lifetime, so the Changed
/// subscription is intentionally never removed.</summary>
public partial class LiveViewWindow
{
    public sealed record LiveViewContext(SessionViewModel Session, TranscriptLinesViewModel Lines);

    private readonly TranscriptLinesViewModel _lines;
    private readonly ISettingsService _settings;
    private bool _stickToBottom = true;
    private bool _hwndReady;

    public LiveViewWindow(SessionViewModel session, TranscriptLinesViewModel lines,
        ISettingsService settings)
    {
        InitializeComponent();
        (_lines, _settings) = (lines, settings);
        DataContext = new LiveViewContext(session, lines);
        lines.Lines.CollectionChanged += OnLinesChanged;
        settings.Changed += OnSettingsChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndReady = true;
        CaptureExclusion.Apply(this, _settings.Current.Privacy.ExcludeWindowsFromCapture);
    }

    // ISettingsService.Changed carries no thread contract; marshal to the UI thread before
    // touching the HWND. _hwndReady guards a save landing before the window was first shown.
    private void OnSettingsChanged(Settings oldSettings, Settings newSettings)
    {
        if (!CaptureExclusionPolicy.ShouldReapply(oldSettings, newSettings)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_hwndReady)
                CaptureExclusion.Apply(this, newSettings.Privacy.ExcludeWindowsFromCapture);
        });
    }

    private void OnLinesChanged(object? _, NotifyCollectionChangedEventArgs e)
    {
        if (_stickToBottom && _lines.Lines.Count > 0)
            LineList.ScrollIntoView(_lines.Lines[^1]);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (FindScrollViewer(LineList) is { } sv)
            sv.ScrollChanged += (_, args) =>
                _stickToBottom = args.VerticalOffset >= args.ExtentHeight - args.ViewportHeight - 2;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;                       // hide, never close
        Hide();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } deep) return deep;
        }
        return null;
    }
}
```

- [ ] Edit `src/LocalScribe.App/TrayIconHost.cs`:
  - Add `using LocalScribe.App.Services;` to the usings block (after line 11 `using LocalScribe.Core.Storage;`).
  - Fields (line 23 area): after `private readonly StoragePaths _paths;` add `private readonly ISettingsService _settingsService;`
  - Ctor (line 25): signature becomes `public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines, StoragePaths paths, ISettingsService settingsService)`; add `ArgumentNullException.ThrowIfNull(settingsService);` alongside the existing null checks and change the tuple assignment (line 30) to `(_session, _lines, _paths, _settingsService) = (session, lines, paths, settingsService);`
  - `OpenLiveView` (line 97): `_liveView ??= new LiveViewWindow(_session, _lines);` becomes `_liveView ??= new LiveViewWindow(_session, _lines, _settingsService);`
- [ ] Edit `src/LocalScribe.App/App.xaml.cs` line 30: `_tray = new TrayIconHost(session, lines, paths);` becomes `_tray = new TrayIconHost(session, lines, paths, settingsService);` (the `settingsService` local is Task 10's).
- [ ] Run `dotnet build LocalScribe.slnx` - expected: 0 warnings; then `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` - expected: all green (no headless test constructs LiveViewWindow or TrayIconHost - they need STA; behavior verification is the smoke runbook's).
- [ ] Commit: `git add src/LocalScribe.App/NativeWindowInterop.cs src/LocalScribe.App/CaptureExclusion.cs src/LocalScribe.App/LiveViewWindow.xaml.cs src/LocalScribe.App/TrayIconHost.cs src/LocalScribe.App/App.xaml.cs && git commit -m "feat: capture-exclude live view per Privacy setting (WDA toggle helper)"`

Smoke-runbook items owed by this task (recorded for the Stage 4 runbook, GUI-only, not unit-testable): live view absent from a Webex screen share with the toggle on; visible after turning the Privacy toggle off (no restart); overlay behavior unchanged under its own `OverlaySetting.ExcludeFromCapture`.

---

### Task 14: MainWindow shell + navigation + tray retarget + IUiErrorReporter

**Files:**
- Create: `src/LocalScribe.App/Services/IUiErrorReporter.cs`, `src/LocalScribe.App/Services/InfoBarErrorReporter.cs` (both WPF-free)
- Create: `src/LocalScribe.App/ViewModels/MainWindowViewModel.cs` (WPF-free)
- Create: `src/LocalScribe.App/Pages/SessionsPage.xaml` + `.xaml.cs`, `src/LocalScribe.App/Pages/MattersPage.xaml` + `.xaml.cs`, `src/LocalScribe.App/Pages/SettingsPage.xaml` + `.xaml.cs` (empty shells - filled by Tasks 15-21)
- Create: `src/LocalScribe.App/MainWindow.xaml` + `.xaml.cs`
- Modify: `src/LocalScribe.App/TrayIconHost.cs` (fields near line 23; ctor line 25; double-click line 34; `BuildMenu` line 43-44; new `OpenMainWindow` after `OpenLiveView` ~line 100 - all line refs are pre-Task-13-numbering anchors, use the quoted code as the match)
- Modify: `src/LocalScribe.App/App.xaml.cs` (single-instance guard at top of `OnStartup`; shared WindowStateStore; MainWindow factory; `OnExit` dispose)
- Test: `tests/LocalScribe.App.Tests/InfoBarErrorReporterTests.cs`, `tests/LocalScribe.App.Tests/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `WindowPlacement` + `WindowStateStore.Load("main")/Save("main", ...)` (Task 11); `SingleInstance` (Task 12); `CaptureExclusion.Apply` + `CaptureExclusionPolicy.ShouldReapply` + `TrayIconHost(..., ISettingsService)` shape (Task 13); `ISettingsService` (locked, Task 10); `ScreenClamp.Clamp(double x, double y, double w, double h, double vx, double vy, double vw, double vh)` (existing, `src/LocalScribe.App/ViewModels/ScreenClamp.cs:7`). WPF-UI 4.0.3 API verified against the installed package's XML docs: `NavigationView.Navigate(Type, object)`, routed `SelectionChanged` (delegate `Wpf.Ui.Controls.TypedEventHandler<NavigationView, RoutedEventArgs>`), `NavigationViewItem.TargetPageType`, `InfoBar.IsOpenProperty` (InfoBar has NO CLR Closed event in 4.0.3 - hence the DependencyPropertyDescriptor hook below); no page-provider service is registered, so NavigationView's default activator instantiates pages via their parameterless ctors.
- Produces (locked + consumed by Tasks 15-21):
  - `public interface IUiErrorReporter { void Report(string context, Exception ex); void Info(string message); }` (namespace `LocalScribe.App.Services`)
  - `public sealed class InfoBarErrorReporter(Action<Action> dispatch) : IUiErrorReporter { public ObservableCollection<string> Messages { get; } public void DismissOldest(); }`
  - `public sealed partial class MainWindowViewModel : ObservableObject { public MainWindowViewModel(InfoBarErrorReporter errors); public InfoBarErrorReporter Errors { get; } public string SelectedSection { get; set; } }` (default `"Sessions"`)
  - `public partial class MainWindow { public MainWindow(MainWindowViewModel vm, WindowStateStore stateStore, ISettingsService settings); }` - genuinely closable, geometry key `"main"`, capture-excluded per Task 13
  - `TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines, StoragePaths paths, ISettingsService settingsService, Func<MainWindow> mainWindowFactory)` + `public void OpenMainWindow()`
  - Page shells `LocalScribe.App.Pages.SessionsPage` / `MattersPage` / `SettingsPage` (parameterless ctors - Tasks 15-21 fill their content and may hand them VMs via a page provider later)

Steps - cycle 1, InfoBar error reporter (headless):

- [ ] Write `tests/LocalScribe.App.Tests/InfoBarErrorReporterTests.cs`:

```csharp
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class InfoBarErrorReporterTests
{
    [Fact]
    public void Report_and_Info_enqueue_through_dispatch_in_order()
    {
        var pending = new List<Action>();
        var reporter = new InfoBarErrorReporter(pending.Add);

        reporter.Report("Delete session", new InvalidOperationException("folder is locked"));
        reporter.Info("Recovered 2 interrupted session(s)");
        Assert.Empty(reporter.Messages);                   // marshaled via dispatch, never inline

        pending.ForEach(a => a());
        Assert.Equal(new[]
        {
            "Delete session: folder is locked",
            "Recovered 2 interrupted session(s)",
        }, reporter.Messages);
    }

    [Fact]
    public void DismissOldest_advances_the_queue_and_is_safe_when_empty()
    {
        var reporter = new InfoBarErrorReporter(a => a());
        reporter.DismissOldest();                          // empty queue: no throw
        reporter.Info("first");
        reporter.Info("second");
        reporter.DismissOldest();
        Assert.Equal(new[] { "second" }, reporter.Messages);
    }
}
```

- [ ] Run `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~InfoBarErrorReporterTests"` - expected failure: `CS0246: 'InfoBarErrorReporter' could not be found`.

- [ ] Create `src/LocalScribe.App/Services/IUiErrorReporter.cs`:

```csharp
namespace LocalScribe.App.Services;

/// <summary>Per-command error surfacing seam (design 7.5): manager/editor commands catch and
/// Report(context, ex); background operations (scan, rebuild, cascades) Info(...) their
/// outcomes. Nothing relies on the globally-swallowed DispatcherUnhandledException. Stage 7
/// attaches real logging behind this seam.</summary>
public interface IUiErrorReporter
{
    void Report(string context, Exception ex);
    void Info(string message);
}
```

- [ ] Create `src/LocalScribe.App/Services/InfoBarErrorReporter.cs`:

```csharp
using System.Collections.ObjectModel;
namespace LocalScribe.App.Services;

/// <summary>IUiErrorReporter surfacing into MainWindow's InfoBar (design 7.5). WPF-free: the
/// queue is plain ObservableCollection state; Report/Info may be called from any thread and
/// marshal through the injected dispatch (the UI thread in the app, an inline runner in
/// tests). MainWindow mirrors Messages[0] into the InfoBar and calls DismissOldest when the
/// user closes it; the collection outlives any single MainWindow instance, so errors queued
/// while the window is closed appear on next open.</summary>
public sealed class InfoBarErrorReporter(Action<Action> dispatch) : IUiErrorReporter
{
    public ObservableCollection<string> Messages { get; } = [];

    public void Report(string context, Exception ex)
        => dispatch(() => Messages.Add(context + ": " + ex.Message));

    public void Info(string message) => dispatch(() => Messages.Add(message));

    public void DismissOldest()
    {
        if (Messages.Count > 0) Messages.RemoveAt(0);
    }
}
```

- [ ] Run `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~InfoBarErrorReporterTests"` - expected: 2 tests PASS.
- [ ] Commit: `git add src/LocalScribe.App/Services/IUiErrorReporter.cs src/LocalScribe.App/Services/InfoBarErrorReporter.cs tests/LocalScribe.App.Tests/InfoBarErrorReporterTests.cs && git commit -m "feat: IUiErrorReporter seam + InfoBar-backed queue reporter"`

Cycle 2, MainWindowViewModel (headless):

- [ ] Write `tests/LocalScribe.App.Tests/MainWindowViewModelTests.cs`:

```csharp
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Defaults_to_sessions_and_raises_change_notifications()
    {
        var vm = new MainWindowViewModel(new InfoBarErrorReporter(a => a()));
        Assert.Equal("Sessions", vm.SelectedSection);      // design section 2: Sessions is default

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SelectedSection = "Matters";
        Assert.Equal("Matters", vm.SelectedSection);
        Assert.Contains(nameof(MainWindowViewModel.SelectedSection), raised);
    }

    [Fact]
    public void Exposes_the_shared_error_queue()
    {
        var errors = new InfoBarErrorReporter(a => a());
        var vm = new MainWindowViewModel(errors);
        Assert.Same(errors, vm.Errors);
    }
}
```

- [ ] Run `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~MainWindowViewModelTests"` - expected failure: `CS0246: 'MainWindowViewModel' could not be found`.

- [ ] Create `src/LocalScribe.App/ViewModels/MainWindowViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
namespace LocalScribe.App.ViewModels;

/// <summary>WPF-free state for the manager shell (design section 2): the selected nav section
/// (MainWindow mirrors NavigationView selection into it; page VMs read it) and the InfoBar
/// error queue. This VM is a singleton across MainWindow RE-CREATIONS - the window genuinely
/// closes and TrayIconHost builds a fresh one per open - so section choice and queued errors
/// survive a close/reopen.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public InfoBarErrorReporter Errors { get; }

    [ObservableProperty]
    private string _selectedSection = "Sessions";

    public MainWindowViewModel(InfoBarErrorReporter errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors;
    }
}
```

- [ ] Run `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~MainWindowViewModelTests"` - expected: 2 tests PASS.
- [ ] Commit: `git add src/LocalScribe.App/ViewModels/MainWindowViewModel.cs tests/LocalScribe.App.Tests/MainWindowViewModelTests.cs && git commit -m "feat: MainWindowViewModel (nav section + error queue, WPF-free)"`

Cycle 3, XAML shell + tray retarget + App wiring (window/XAML behavior is NOT headlessly testable - it goes to the Stage 4 smoke runbook; the compile itself plus the green existing suite is the gate here):

- [ ] Create `src/LocalScribe.App/Pages/SessionsPage.xaml`:

```xml
<Page x:Class="LocalScribe.App.Pages.SessionsPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Title="Sessions">
    <Grid>
        <!-- Shell only: the session browser + metadata editor land with the Sessions-page task. -->
        <TextBlock Text="Sessions" FontSize="20" Margin="16" />
    </Grid>
</Page>
```

  and `src/LocalScribe.App/Pages/SessionsPage.xaml.cs`:

```csharp
namespace LocalScribe.App.Pages;

/// <summary>Empty shell hosted by MainWindow's NavigationView; content lands with the
/// Sessions-page task. Parameterless ctor is load-bearing: no page-provider service is
/// registered, so NavigationView's default activator constructs pages reflectively.</summary>
public partial class SessionsPage
{
    public SessionsPage() => InitializeComponent();
}
```

- [ ] Create `src/LocalScribe.App/Pages/MattersPage.xaml` + `.xaml.cs` and `src/LocalScribe.App/Pages/SettingsPage.xaml` + `.xaml.cs` - identical to SessionsPage except: `x:Class="LocalScribe.App.Pages.MattersPage"`, `Title="Matters"`, `Text="Matters"`, class `MattersPage`, comment "the Matters-page task" (and `SettingsPage`/`Settings`/"the Settings-page task" respectively).

- [ ] Create `src/LocalScribe.App/MainWindow.xaml`:

```xml
<ui:FluentWindow x:Class="LocalScribe.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        xmlns:pages="clr-namespace:LocalScribe.App.Pages"
        Title="LocalScribe" Height="720" Width="1080" MinHeight="400" MinWidth="640"
        WindowBackdropType="Mica" ExtendsContentIntoTitleBar="False">
    <DockPanel>
        <ui:InfoBar x:Name="ErrorBar" DockPanel.Dock="Top" Margin="12,12,12,0"
                    Title="LocalScribe" Severity="Error" IsClosable="True" IsOpen="False" />
        <ui:NavigationView x:Name="RootNav" PaneDisplayMode="Left" OpenPaneLength="180"
                           IsBackButtonVisible="Collapsed" IsPaneToggleVisible="False"
                           SelectionChanged="OnNavSelectionChanged">
            <ui:NavigationView.MenuItems>
                <ui:NavigationViewItem Content="Sessions" Tag="Sessions"
                                       TargetPageType="{x:Type pages:SessionsPage}">
                    <ui:NavigationViewItem.Icon>
                        <ui:SymbolIcon Symbol="History24" />
                    </ui:NavigationViewItem.Icon>
                </ui:NavigationViewItem>
                <ui:NavigationViewItem Content="Matters" Tag="Matters"
                                       TargetPageType="{x:Type pages:MattersPage}">
                    <ui:NavigationViewItem.Icon>
                        <ui:SymbolIcon Symbol="Folder24" />
                    </ui:NavigationViewItem.Icon>
                </ui:NavigationViewItem>
            </ui:NavigationView.MenuItems>
            <ui:NavigationView.FooterMenuItems>
                <ui:NavigationViewItem Content="Settings" Tag="Settings"
                                       TargetPageType="{x:Type pages:SettingsPage}">
                    <ui:NavigationViewItem.Icon>
                        <ui:SymbolIcon Symbol="Settings24" />
                    </ui:NavigationViewItem.Icon>
                </ui:NavigationViewItem>
            </ui:NavigationView.FooterMenuItems>
        </ui:NavigationView>
    </DockPanel>
</ui:FluentWindow>
```

- [ ] Create `src/LocalScribe.App/MainWindow.xaml.cs`:

```csharp
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using Wpf.Ui.Controls;
namespace LocalScribe.App;

/// <summary>Stage 4 manager shell (design section 2): FluentWindow + NavigationView hosting
/// the Sessions/Matters/Settings pages. GENUINELY closable - unlike the live view and overlay
/// (hide-on-close: a recording must never die with a window), nothing depends on this window
/// staying alive, and TrayIconHost re-creates it on demand. Humble object: section state and
/// the error queue live in the tested WPF-free MainWindowViewModel/InfoBarErrorReporter; this
/// class only mirrors them into WPF-UI controls.</summary>
public partial class MainWindow
{
    private readonly MainWindowViewModel _vm;
    private readonly WindowStateStore _stateStore;
    private readonly ISettingsService _settings;
    private bool _hwndReady;

    public MainWindow(MainWindowViewModel vm, WindowStateStore stateStore, ISettingsService settings)
    {
        InitializeComponent();
        (_vm, _stateStore, _settings) = (vm, stateStore, settings);
        DataContext = vm;

        vm.Errors.Messages.CollectionChanged += OnMessagesChanged;
        _settings.Changed += OnSettingsChanged;
        // WPF-UI 4.0.3's InfoBar exposes no Closed CLR event; the close button just flips
        // IsOpen false. DependencyPropertyDescriptor is the version-safe hook to advance the
        // queue on user dismissal (design 7.5).
        DependencyPropertyDescriptor
            .FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar))
            .AddValueChanged(ErrorBar, OnErrorBarIsOpenChanged);
        SyncInfoBar();                                     // errors queued while closed show now
        Loaded += (_, _) => RootNav.Navigate(typeof(Pages.SessionsPage));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndReady = true;
        CaptureExclusion.Apply(this, _settings.Current.Privacy.ExcludeWindowsFromCapture);
        if (_stateStore.Load("main") is { } p)
        {
            // Restore size before clamping so the clamp sees the real extents; reject
            // degenerate sizes from a hand-edited file (throwaway state, never trusted).
            if (p.Width is { } w && w >= MinWidth) Width = w;
            if (p.Height is { } h && h >= MinHeight) Height = h;
            (Left, Top) = ScreenClamp.Clamp(p.X, p.Y, Width, Height,
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        }
    }

    // Genuinely closable: no e.Cancel, no Hide (contrast LiveViewWindow/OverlayWindow).
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _stateStore.Save("main", new WindowPlacement(Left, Top, Width, Height));
    }

    protected override void OnClosed(EventArgs e)
    {
        // The VM and the settings service outlive this window (a fresh window is created per
        // open): unhook, or every reopen would leak its predecessor via these subscriptions.
        _vm.Errors.Messages.CollectionChanged -= OnMessagesChanged;
        _settings.Changed -= OnSettingsChanged;
        DependencyPropertyDescriptor
            .FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar))
            .RemoveValueChanged(ErrorBar, OnErrorBarIsOpenChanged);
        base.OnClosed(e);
    }

    private void OnNavSelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem { Tag: string tag })
            _vm.SelectedSection = tag;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => SyncInfoBar();

    // User clicked the InfoBar close button: drop the shown message; SyncInfoBar (via the
    // resulting CollectionChanged) then re-opens the bar if more messages are queued.
    private void OnErrorBarIsOpenChanged(object? sender, EventArgs e)
    {
        if (!ErrorBar.IsOpen && _vm.Errors.Messages.Count > 0) _vm.Errors.DismissOldest();
    }

    private void SyncInfoBar()
    {
        var messages = _vm.Errors.Messages;
        ErrorBar.Message = messages.Count > 0 ? messages[0] : string.Empty;
        ErrorBar.IsOpen = messages.Count > 0;
    }

    private void OnSettingsChanged(Settings oldSettings, Settings newSettings)
    {
        if (!CaptureExclusionPolicy.ShouldReapply(oldSettings, newSettings)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_hwndReady)
                CaptureExclusion.Apply(this, newSettings.Privacy.ExcludeWindowsFromCapture);
        });
    }
}
```

- [ ] Run `dotnet build LocalScribe.slnx` - expected: PASS, 0 warnings (XAML + code-behind compile; nothing constructs MainWindow yet).

- [ ] Edit `src/LocalScribe.App/TrayIconHost.cs` (anchors quoted from the current file; Task 13 already added `_settingsService`):
  - Fields: after `private LiveViewWindow? _liveView;` (line 23) add:

```csharp
    private readonly Func<MainWindow> _mainWindowFactory;
    private MainWindow? _main;
```

  - Ctor: signature becomes `public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines, StoragePaths paths, ISettingsService settingsService, Func<MainWindow> mainWindowFactory)`; add `ArgumentNullException.ThrowIfNull(mainWindowFactory);` and extend the tuple assignment to `(_session, _lines, _paths, _settingsService, _mainWindowFactory) = (session, lines, paths, settingsService, mainWindowFactory);`
  - Double-click (line 34): `_icon.TrayMouseDoubleClick += (_, _) => OpenLiveView();` becomes:

```csharp
        _icon.TrayMouseDoubleClick += (_, _) => OpenMainWindow();   // retargeted to the manager (design section 2)
```

  - `BuildMenu` (line 43-44): directly after `var menu = new ContextMenu();` and BEFORE `menu.Items.Add(Bound("Start recording", _session.StartCommand));` insert:

```csharp
        menu.Items.Add(Item("Open LocalScribe", (_, _) => OpenMainWindow()));
        menu.Items.Add(new Separator());
```

  - After the `OpenLiveView` method (line 95-100) add:

```csharp
    /// <summary>Unlike the live view (hide-on-close singleton), the main window GENUINELY
    /// closes - so the field RE-CREATES after a close. The Closed hook is the closed-flag:
    /// it nulls the field on the UI thread before another click can observe it, so a stale
    /// (closed, un-Show()-able) instance is never reused.</summary>
    public void OpenMainWindow()
    {
        if (_main is null)
        {
            _main = _mainWindowFactory();
            _main.Closed += (_, _) => _main = null;
        }
        _main.Show();
        _main.Activate();
    }
```

- [ ] Edit `src/LocalScribe.App/App.xaml.cs`:
  - Fields (line 7-10 area): add `private Services.SingleInstance? _singleInstance;`
  - Top of `OnStartup`, immediately after `base.OnStartup(e);` (line 14), insert:

```csharp
        // Single-instance guard (design 7.2): Stage 4 makes matters.json read-modify-write
        // load-bearing and two instances could double-record. The second instance pings the
        // holder (activate -> open the manager) and exits before building anything. The
        // callback is dispatch-wrapped here, as SingleInstance requires: it fires on the
        // guard's background wait thread.
        _singleInstance = Services.SingleInstance.TryAcquire("LocalScribe",
            onActivateRequested: () => Dispatcher.BeginInvoke(() => _tray?.OpenMainWindow()));
        if (_singleInstance is null)
        {
            Services.SingleInstance.SignalExisting("LocalScribe");
            Shutdown();
            return;
        }
```

  - Replace the tray + overlay construction block (currently the `_tray = new TrayIconHost(session, lines, paths, settingsService);` line from Task 13 through `_overlay = new OverlayWindow(_overlayVm, new ViewModels.WindowStateStore(stateStorePath));` at line 38) with:

```csharp
        // One WindowStateStore serves overlay + main (keyed entries in window-state.json;
        // spec 7: throwaway UI state, NOT settings). Lives next to settings.json.
        string stateStorePath = System.IO.Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "window-state.json");
        var windowState = new ViewModels.WindowStateStore(stateStorePath);

        // Manager shell (design section 2): the VM is a singleton (the error queue survives
        // close/reopen) but the WINDOW is re-created per open - MainWindow genuinely closes,
        // and TrayIconHost owns the lazily re-creating field.
        var errors = new Services.InfoBarErrorReporter(a => Dispatcher.BeginInvoke(a));
        var mainVm = new ViewModels.MainWindowViewModel(errors);
        _tray = new TrayIconHost(session, lines, paths, settingsService,
            mainWindowFactory: () => new MainWindow(mainVm, windowState, settingsService));

        // Overlay singleton (design decision 12): shown/hidden - never closed - as
        // OverlayViewModel.IsVisible flips with State.
        _overlayVm = new ViewModels.OverlayViewModel(session, settings);
        _overlay = new OverlayWindow(_overlayVm, windowState);
```

  - `OnExit` (line 51-56): after `_tray?.Dispose();` add `_singleInstance?.Dispose();`
- [ ] Run `dotnet build LocalScribe.slnx` - expected: PASS, 0 warnings.
- [ ] Run the full gate `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` - expected: all green (all 4 new headless tests from cycles 1-2 included; window/XAML/tray/single-instance-activation behavior is smoke-runbook territory).
- [ ] Commit: `git add src/LocalScribe.App tests/LocalScribe.App.Tests && git commit -m "feat: MainWindow shell (NavigationView + InfoBar), tray retarget, single-instance wiring"`

Smoke-runbook items owed by this task (GUI-only, recorded for the Stage 4 runbook task): tray menu shows "Open LocalScribe" first and double-click opens the manager (live view still reachable via its own item); MainWindow closes for real and reopens fresh from the tray with section and queued InfoBar errors intact; geometry (position AND size) survives close/reopen and clamps back on-screen after a monitor change; a second `LocalScribe.exe` exits immediately and raises the first instance's MainWindow; MainWindow absent from a Webex screen share until the Privacy toggle is turned off.

---

### Task 15: SessionsPageViewModel + session list UI

**Files:**
- Create: `src/LocalScribe.App/ViewModels/SessionRowViewModel.cs`
- Create: `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs`
- Modify: `src/LocalScribe.App/Pages/SessionsPage.xaml` (replace Task 14's empty shell)
- Modify: `src/LocalScribe.App/Pages/SessionsPage.xaml.cs` (replace Task 14's empty shell)
- Test: `tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs`

**Interfaces:**
- Consumes (locked, namespace `LocalScribe.App.Services`): `MaintenanceService` (`Task<SessionCatalogResult> ListSessionsAsync(ct)`, `Task SaveMetaAsync(string sessionId, SessionMeta meta, IReadOnlyCollection<string> previousMatterIds, ct)`; ctor `(StoragePaths, ISettingsService, IRecycleBin, TimeProvider)` per Task 9 (locked) — the test helper `MakeVm()` is the single construction point, using local `FakeSettings`/`NoopBin` fakes that mirror Task 16's verbatim), `ISettingsService` (Task 9), `IRecycleBin` (Task 7, `LocalScribe.Core.Storage`), `SessionListItem(string Id, SessionRecord Session, SessionMeta Meta)`, `SessionCatalogResult(IReadOnlyList<SessionListItem> Sessions, int UnreadableCount)`, `IUiErrorReporter`, `WindowRegistry`.
- Consumes (existing): `SessionViewModel` (State/PropertyChanged), `SessionState` (LocalScribe.Core.Live), `SessionRecord` v3 / `SessionMeta` v2 (with `Archived`) / enums (LocalScribe.Core.Model), `SessionStore`/`MetadataStore`/`StoragePaths` (Core.Storage, tests only), `LiveTestDoubles` (LocalScribe.Core.Tests).
- Produces (G5 contract, exact): `SessionRowViewModel` with `Id/Title/AppMedium/DateDisplay/DurationDisplay/IsRecovered/IsEdited/IsDiarised/IsSystemMix/IsArchived/IsPendingRecovery/MatterIds/Item`; `SessionsPageViewModel : ObservableObject` with `Rows`, `[ObservableProperty] selectedRow/filterText/matterFilterId/showArchived/unreadableCount/isScanning`, `IAsyncRelayCommand RefreshCommand`, `event Action<string>? OpenReadViewRequested`, `Task OnNavigatedToAsync()`, ctor `(MaintenanceService, SessionViewModel, WindowRegistry, IUiErrorReporter, Action<Action> dispatch, TimeProvider time, Action<string> revealInExplorer)`.
- Produces (additive, this task): `SessionRowViewModel.DateTooltip` + `SystemMixTooltip` (spec 3.2 tooltips), `SessionsPageViewModel.ToggleArchiveCommand : IAsyncRelayCommand<SessionRowViewModel>`, `OpenReadViewCommand`/`RevealInExplorerCommand : IRelayCommand<SessionRowViewModel>`, `MatterFilterOption(string? Id, string Label)`, `MatterFilterOptions : ObservableCollection<MatterFilterOption>`, `const string NoMatterSentinel = "(none)"`, `bool IsReadViewOpen(string sessionId)` (registry passthrough for the window layer and Task 17's delete flow), `SessionsPage` (WPF Page; Task 14's MainWindow NavigationView hosts it, and Task 24 constructs the VMs and injects the ctor delegates).
- `revealInExplorer` receives the SESSION ID; the composition root (Task 24) wires `id => shellReveal(paths.SessionDir(id))` via `StoragePaths` — the VM stays filesystem-free, the path is still built via StoragePaths as spec 3.2 requires.

- [ ] **Step 1 — write the failing tests.** Create `tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SessionsPageViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-sessions-page-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public SessionsPageViewModelTests()
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

    // Local fakes for MaintenanceService's Task 9 ctor, byte-identical to Task 16's so both
    // test files compile standalone whichever task lands first.
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

    /// <summary>Single construction point: real MaintenanceService over a temp root, real
    /// SessionViewModel over the live test doubles, synchronous dispatch.</summary>
    private (SessionsPageViewModel Vm, SessionViewModel Session, RecordingErrors Errors, List<string> Revealed) MakeVm()
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var errors = new RecordingErrors();
        var revealed = new List<string>();
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(), errors,
            dispatch: a => a(), TimeProvider.System, revealInExplorer: revealed.Add);
        return (vm, session, errors, revealed);
    }

    private static SessionRecord Rec(string id, DateTimeOffset startedUtc, int? offsetMin,
        long durationMs = 60_000, bool ended = true, bool recovered = false, bool diarised = false,
        RemoteMode remoteMode = RemoteMode.PerProcess, bool fellBack = false, AppKind app = AppKind.Webex)
        => new()
        {
            Id = id, App = app, StartedAtUtc = startedUtc,
            EndedAtUtc = ended ? startedUtc.AddMilliseconds(durationMs) : null,
            TimeZoneId = offsetMin is null ? null : "Singapore Standard Time",
            UtcOffsetMinutes = offsetMin, DurationMs = durationMs,
            Model = "small", Backend = "cpu", Language = "en",
            Diarised = diarised, SegmentCount = 1, Recovered = recovered,
            Devices = new DeviceSnapshot
            { Remote = new RemoteSnapshot { Mode = remoteMode, FellBackToSystemMix = fellBack } },
        };

    private static SessionMeta Meta(string title, bool archived = false, bool edited = false,
        Medium medium = Medium.Webex, params string[] matterIds)
        => new() { Title = title, Medium = medium, MatterIds = matterIds, Archived = archived, Edited = edited };

    private async Task WriteSessionAsync(SessionRecord session, SessionMeta meta)
    {
        Directory.CreateDirectory(_paths.SessionDir(session.Id));
        await new SessionStore(_paths.SessionJson(session.Id)).SaveAsync(session, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(session.Id)).SaveAsync(meta, CancellationToken.None);
    }

    [Fact]
    public async Task Load_orders_newest_first_and_maps_display_fields()
    {
        // s-old: stored +480 offset (session-local, NOT machine zone); 754 s -> mm:ss.
        var oldStart = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-old", oldStart, offsetMin: 480, durationMs: 754_000),
            Meta("Client call", medium: Medium.Webex));
        // s-new: pre-v3 null offset -> machine-local fallback; 3725 s -> h:mm:ss.
        var newStart = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-new", newStart, offsetMin: null, durationMs: 3_725_000, app: AppKind.Manual),
            Meta("Phone conference", medium: Medium.Phone));

        var (vm, _, errors, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        Assert.Empty(errors.Reports);
        Assert.Equal(new[] { "s-new", "s-old" }, vm.Rows.Select(r => r.Id).ToArray());

        var oldRow = vm.Rows.Single(r => r.Id == "s-old");
        Assert.Equal("2026-06-01 10:00", oldRow.DateDisplay);           // 02:00Z + 8 h
        Assert.Equal("12:34", oldRow.DurationDisplay);
        Assert.Equal("Webex", oldRow.AppMedium);                        // app == medium collapses

        var newRow = vm.Rows.Single(r => r.Id == "s-new");
        string machineLocal = newStart.ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(machineLocal, newRow.DateDisplay);                 // null offset -> ToLocalTime
        Assert.Equal("1:02:05", newRow.DurationDisplay);
        Assert.Equal("Manual / Phone", newRow.AppMedium);
    }

    [Fact]
    public async Task Pending_recovery_row_has_blank_duration_and_flag()
    {
        await WriteSessionAsync(
            Rec("s-crash", new DateTimeOffset(2026, 6, 3, 4, 0, 0, TimeSpan.Zero), 480, ended: false),
            Meta("Interrupted"));
        var (vm, _, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        var row = vm.Rows.Single();
        Assert.True(row.IsPendingRecovery);
        Assert.Equal("", row.DurationDisplay);
    }

    [Fact]
    public async Task Filters_recompute_rows_from_cached_list()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-1", t, 480), Meta("Client call about the merger", matterIds: "M-2026-001"));
        await WriteSessionAsync(Rec("s-2", t.AddHours(1), 480), Meta("Webex with counsel"));
        await WriteSessionAsync(Rec("s-3", t.AddHours(2), 480), Meta("Old archived brief", archived: true));

        var (vm, _, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();
        Assert.Equal(2, vm.Rows.Count);                                 // archived hidden by default

        vm.FilterText = "MERGER";                                       // case-insensitive contains
        Assert.Equal("s-1", vm.Rows.Single().Id);
        vm.FilterText = "";
        Assert.Equal(2, vm.Rows.Count);

        vm.MatterFilterId = "M-2026-001";
        Assert.Equal("s-1", vm.Rows.Single().Id);
        vm.MatterFilterId = SessionsPageViewModel.NoMatterSentinel;     // empty MatterIds only
        Assert.Equal("s-2", vm.Rows.Single().Id);
        vm.MatterFilterId = null;

        vm.SelectedRow = vm.Rows.Single(r => r.Id == "s-2");
        vm.ShowArchived = true;
        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("s-2", vm.SelectedRow?.Id);                        // selection survives rebuild

        Assert.Equal(new string?[] { null, SessionsPageViewModel.NoMatterSentinel, "M-2026-001" },
            vm.MatterFilterOptions.Select(o => o.Id).ToArray());
    }

    [Fact]
    public async Task Badge_mapping_covers_chosen_and_fallback_system_mix()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-chosen", t, 480, remoteMode: RemoteMode.SystemMix), Meta("A"));
        await WriteSessionAsync(Rec("s-fallback", t.AddHours(1), 480, fellBack: true), Meta("B"));
        await WriteSessionAsync(Rec("s-clean", t.AddHours(2), 480, recovered: true, diarised: true),
            Meta("C", edited: true));

        var (vm, _, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        Assert.True(vm.Rows.Single(r => r.Id == "s-chosen").IsSystemMix);   // chosen mode counts (3.2)
        Assert.True(vm.Rows.Single(r => r.Id == "s-fallback").IsSystemMix); // degraded fallback counts
        var clean = vm.Rows.Single(r => r.Id == "s-clean");
        Assert.False(clean.IsSystemMix);
        Assert.True(clean.IsRecovered);
        Assert.True(clean.IsEdited);
        Assert.True(clean.IsDiarised);
    }

    [Fact]
    public async Task State_reaching_idle_triggers_refresh()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-first", t, 480), Meta("First"));
        var (vm, session, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();
        Assert.Single(vm.Rows);

        await WriteSessionAsync(Rec("s-second", t.AddHours(1), 480), Meta("Second"));
        session.State = SessionState.Recording;    // simulate controller-driven transitions
        session.State = SessionState.Idle;         // finalize just happened -> refresh (3.1)

        SpinWait.SpinUntil(() => vm.Rows.Count == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public async Task Archive_toggle_saves_meta_without_flipping_edited()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-arch", t, 480), Meta("To archive", matterIds: "M-2026-001"));
        var (vm, _, errors, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        await vm.ToggleArchiveCommand.ExecuteAsync(vm.Rows.Single());
        Assert.Empty(errors.Reports);
        Assert.Empty(vm.Rows);                                          // archived + ShowArchived off

        var onDisk = await new MetadataStore(_paths.MetaJson("s-arch")).LoadAsync(CancellationToken.None);
        Assert.NotNull(onDisk);
        Assert.True(onDisk!.Archived);
        Assert.False(onDisk.Edited);                                    // metadata saves never flip these
        Assert.Null(onDisk.LastEditedAtUtc);
        Assert.Equal(new[] { "M-2026-001" }, onDisk.MatterIds);         // tags untouched

        vm.ShowArchived = true;
        Assert.True(vm.Rows.Single().IsArchived);
    }

    [Fact]
    public async Task Reveal_and_open_read_view_row_actions()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-ok", t, 480), Meta("Done"));
        await WriteSessionAsync(Rec("s-pending", t.AddHours(1), 480, ended: false), Meta("Pending"));
        var (vm, _, _, revealed) = MakeVm();
        await vm.OnNavigatedToAsync();

        var opened = new List<string>();
        vm.OpenReadViewRequested += opened.Add;

        vm.RevealInExplorerCommand.Execute(vm.Rows.Single(r => r.Id == "s-ok"));
        Assert.Equal(new[] { "s-ok" }, revealed);                       // delegate gets the session id

        vm.OpenReadViewCommand.Execute(vm.Rows.Single(r => r.Id == "s-ok"));
        vm.OpenReadViewCommand.Execute(vm.Rows.Single(r => r.Id == "s-pending"));  // inert (3.1)
        Assert.Equal(new[] { "s-ok" }, opened);
    }

    [Fact]
    public async Task Unreadable_folders_surface_in_footer_count()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-good", t, 480), Meta("Good"));
        string junk = Path.Combine(_paths.SessionsDir, "not-a-session");
        Directory.CreateDirectory(junk);
        File.WriteAllText(Path.Combine(junk, "stray.txt"), "no session.json here");

        var (vm, _, errors, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        Assert.Single(vm.Rows);
        Assert.Equal(1, vm.UnreadableCount);
        Assert.Empty(errors.Reports);                                   // counted, not error-reported
    }
}
```

- [ ] **Step 2 — run, expect compile failure.** `dotnet test tests/LocalScribe.App.Tests --filter "Category!=Fixture"` — expected: build error `CS0246: The type or namespace name 'SessionsPageViewModel' could not be found` (and the same for `SessionRowViewModel`).

- [ ] **Step 3 — implement the row VM.** Create `src/LocalScribe.App/ViewModels/SessionRowViewModel.cs`:

```csharp
using System.Globalization;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;

namespace LocalScribe.App.ViewModels;

/// <summary>Immutable presentation row over one catalog entry (design 3.2). All display strings
/// are computed once at construction; a refresh rebuilds rows rather than mutating them.</summary>
public sealed class SessionRowViewModel
{
    public string Id { get; }
    public string Title { get; }
    public string AppMedium { get; }
    public string DateDisplay { get; }
    public string DateTooltip { get; }
    public string DurationDisplay { get; }
    public bool IsRecovered { get; }
    public bool IsEdited { get; }
    public bool IsDiarised { get; }
    public bool IsSystemMix { get; }
    public string SystemMixTooltip { get; }
    public bool IsArchived { get; }
    public bool IsPendingRecovery { get; }
    public IReadOnlyList<string> MatterIds { get; }
    public SessionListItem Item { get; }

    public SessionRowViewModel(SessionListItem item, TimeProvider time)
    {
        Item = item;
        var session = item.Session;
        var meta = item.Meta;

        Id = item.Id;
        Title = meta.Title;
        string app = session.App.ToString();
        string medium = meta.Medium.ToString();
        AppMedium = string.Equals(app, medium, StringComparison.OrdinalIgnoreCase)
            ? app : app + " / " + medium;

        // Session-local wall time exactly as SessionWriter renders projections (spec 1.2):
        // the stored DST-resolved offset when present; machine zone only for pre-v3 records.
        var startedLocal = session.UtcOffsetMinutes is int offsetMin
            ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : session.StartedAtUtc.ToLocalTime();
        DateDisplay = startedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var viewerLocal = TimeZoneInfo.ConvertTime(session.StartedAtUtc, time.LocalTimeZone);
        DateTooltip = "Your local time: "
            + viewerLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        // 3.1: endedAtUtc == null means the recovery scan has not finalized this session yet.
        IsPendingRecovery = session.EndedAtUtc is null;
        var span = TimeSpan.FromMilliseconds(session.DurationMs);
        DurationDisplay = IsPendingRecovery
            ? ""
            : span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

        IsRecovered = session.Recovered;
        IsEdited = meta.Edited;
        IsDiarised = session.Diarised;
        // 3.2: chosen system-mix has identical bleed characteristics to a fallback - both badge.
        IsSystemMix = session.Devices.Remote.Mode == RemoteMode.SystemMix
                      || session.Devices.Remote.FellBackToSystemMix;
        SystemMixTooltip = session.Devices.Remote.Mode == RemoteMode.SystemMix
            ? "System mix was the selected capture mode; other app audio may be included"
            : "Per-app capture fell back to system mix; other app audio may be included";
        IsArchived = meta.Archived;
        MatterIds = meta.MatterIds;
    }
}
```

- [ ] **Step 4 — implement the page VM.** Create `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;

namespace LocalScribe.App.ViewModels;

/// <summary>One entry in the Matter filter ComboBox. Id: null = all sessions,
/// SessionsPageViewModel.NoMatterSentinel = untagged sessions, otherwise a matter id.</summary>
public sealed record MatterFilterOption(string? Id, string Label);

/// <summary>Sessions page (design 3.1/3.2): catalog listing via MaintenanceService, in-memory
/// filtering over a cached full list, deterministic refresh triggers (navigation, RefreshCommand,
/// SessionViewModel.State reaching Idle). WPF-free; UI mutations marshal via the injected dispatch.</summary>
public sealed partial class SessionsPageViewModel : ObservableObject
{
    public const string NoMatterSentinel = "(none)";

    private readonly MaintenanceService _maintenance;
    private readonly WindowRegistry _registry;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly Action<string> _revealInExplorer;
    private IReadOnlyList<SessionRowViewModel> _all = [];

    public ObservableCollection<SessionRowViewModel> Rows { get; } = [];
    public ObservableCollection<MatterFilterOption> MatterFilterOptions { get; } = [];

    [ObservableProperty] private SessionRowViewModel? _selectedRow;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string? _matterFilterId;
    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private int _unreadableCount;
    /// <summary>Set by the startup recovery-scan wiring (Task 24, around Task 23's
    /// orchestrator); this VM only exposes it.</summary>
    [ObservableProperty] private bool _isScanning;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<SessionRowViewModel> ToggleArchiveCommand { get; }
    public IRelayCommand<SessionRowViewModel> RevealInExplorerCommand { get; }
    public IRelayCommand<SessionRowViewModel> OpenReadViewCommand { get; }

    /// <summary>Raised with the session id on row double-click/Open; the window layer owns
    /// creating or re-activating the ReadViewWindow (and registering it in WindowRegistry).</summary>
    public event Action<string>? OpenReadViewRequested;

    /// <summary>revealInExplorer receives the SESSION ID; the composition root maps it to
    /// StoragePaths.SessionDir(id) and shells out, keeping this VM filesystem- and shell-free.</summary>
    public SessionsPageViewModel(MaintenanceService maintenance, SessionViewModel session,
        WindowRegistry registry, IUiErrorReporter errors, Action<Action> dispatch,
        TimeProvider time, Action<string> revealInExplorer)
    {
        (_maintenance, _registry, _errors, _dispatch, _time, _revealInExplorer)
            = (maintenance, registry, errors, dispatch, time, revealInExplorer);

        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        ToggleArchiveCommand = new AsyncRelayCommand<SessionRowViewModel>(ToggleArchiveAsync);
        RevealInExplorerCommand = new RelayCommand<SessionRowViewModel>(RevealInExplorer);
        OpenReadViewCommand = new RelayCommand<SessionRowViewModel>(RequestOpenReadView);

        // 3.1 refresh trigger: State reaching Idle means a finalize just completed and a new
        // folder is on disk. PropertyChanged only fires on actual change, so landing on Idle
        // is exactly the transition of interest. Execute is fire-and-forget; LoadAsync
        // catches everything, so nothing can escape as an unobserved exception.
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.State) && session.State == SessionState.Idle)
                RefreshCommand.Execute(null);
        };
    }

    public Task OnNavigatedToAsync() => LoadAsync();

    /// <summary>Registry passthrough for the window layer and the Task 17 delete flow.</summary>
    public bool IsReadViewOpen(string sessionId) => _registry.IsOpen(sessionId);

    private async Task LoadAsync()
    {
        try
        {
            var result = await _maintenance.ListSessionsAsync(CancellationToken.None);
            var rows = result.Sessions
                .OrderByDescending(s => s.Session.StartedAtUtc)
                .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                .Select(s => new SessionRowViewModel(s, _time))
                .ToList();
            _dispatch(() =>
            {
                _all = rows;
                UnreadableCount = result.UnreadableCount;
                RebuildMatterOptions();
                ApplyFilters();
            });
        }
        catch (Exception ex) { _errors.Report("Loading sessions", ex); }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilters();
    partial void OnMatterFilterIdChanged(string? value) => ApplyFilters();
    partial void OnShowArchivedChanged(bool value) => ApplyFilters();

    /// <summary>Recomputes Rows from the cached full list (3.2: in-memory filters only).</summary>
    private void ApplyFilters()
    {
        string? keepId = SelectedRow?.Id;
        Rows.Clear();
        foreach (var row in _all)
        {
            if (!ShowArchived && row.IsArchived) continue;
            if (FilterText.Length > 0
                && !row.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase)) continue;
            if (MatterFilterId == NoMatterSentinel)
            {
                if (row.MatterIds.Count > 0) continue;
            }
            else if (MatterFilterId is { } matterId && !row.MatterIds.Contains(matterId))
            {
                continue;
            }
            Rows.Add(row);
        }
        SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId);
    }

    private void RebuildMatterOptions()
    {
        string? current = MatterFilterId;
        MatterFilterOptions.Clear();
        MatterFilterOptions.Add(new MatterFilterOption(null, "All matters"));
        MatterFilterOptions.Add(new MatterFilterOption(NoMatterSentinel, "No matter"));
        foreach (string id in _all.SelectMany(r => r.MatterIds)
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            MatterFilterOptions.Add(new MatterFilterOption(id, id));
        if (current is not null && MatterFilterOptions.All(o => o.Id != current))
            MatterFilterId = null;   // stale filter (matter no longer tagged anywhere) -> All
    }

    /// <summary>Flips meta.Archived through the maintenance queue. previousMatterIds = current
    /// (tags unchanged, so matter sessionCounts stay put). Never flips Edited/LastEditedAtUtc.</summary>
    private async Task ToggleArchiveAsync(SessionRowViewModel? row)
    {
        if (row is null || row.IsPendingRecovery) return;    // 3.1: pending rows are inert
        try
        {
            var updated = row.Item.Meta with { Archived = !row.Item.Meta.Archived };
            await _maintenance.SaveMetaAsync(row.Id, updated, row.Item.Meta.MatterIds,
                CancellationToken.None);
            await LoadAsync();                               // 3.1: refresh after any edit
        }
        catch (Exception ex) { _errors.Report("Archiving session", ex); }
    }

    private void RevealInExplorer(SessionRowViewModel? row)
    {
        if (row is null) return;
        try { _revealInExplorer(row.Id); }
        catch (Exception ex) { _errors.Report("Opening session folder", ex); }
    }

    private void RequestOpenReadView(SessionRowViewModel? row)
    {
        if (row is null || row.IsPendingRecovery) return;    // 3.1: pending rows are inert
        OpenReadViewRequested?.Invoke(row.Id);
    }
}
```

- [ ] **Step 5 — run, expect PASS.** `dotnet test tests/LocalScribe.App.Tests --filter "Category!=Fixture"` — all `SessionsPageViewModelTests` green, no existing tests broken.

- [ ] **Step 6 — commit the VM.**
```
git add src/LocalScribe.App/ViewModels/SessionRowViewModel.cs src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs
git commit -m "feat(app): sessions page viewmodel - catalog rows, filters, badges, refresh-on-idle"
```

- [ ] **Step 7 — session list UI.** Replace the entire contents of `src/LocalScribe.App/Pages/SessionsPage.xaml` (Task 14's empty shell; hosted in MainWindow's NavigationView (Task 14), VM injection wired by Task 24 via its page provider — until Task 24 lands, in-app navigation to this page fails at runtime because the parameterless shell ctor is gone; the gate here is compile + tests. Virtualization settings match LiveViewWindow.xaml):

```xml
<Page x:Class="LocalScribe.App.Pages.SessionsPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Title="Sessions">
    <Page.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
        <Style x:Key="Chip" TargetType="Border">
            <Setter Property="CornerRadius" Value="8" />
            <Setter Property="Padding" Value="6,1" />
            <Setter Property="Margin" Value="0,0,4,0" />
            <Setter Property="Background" Value="#22808080" />
        </Style>
    </Page.Resources>
    <Grid Margin="12">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*" MinWidth="360" />
            <ColumnDefinition Width="Auto" />
            <ColumnDefinition Width="380" />
        </Grid.ColumnDefinitions>
        <DockPanel Grid.Column="0">
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBox Width="220" Margin="0,0,8,0" VerticalAlignment="Center"
                     Text="{Binding FilterText, UpdateSourceTrigger=PropertyChanged}" />
            <ComboBox Width="180" Margin="0,0,8,0" VerticalAlignment="Center"
                      ItemsSource="{Binding MatterFilterOptions}"
                      DisplayMemberPath="Label" SelectedValuePath="Id"
                      SelectedValue="{Binding MatterFilterId}" />
            <CheckBox Content="Show archived" VerticalAlignment="Center" Margin="0,0,8,0"
                      IsChecked="{Binding ShowArchived}" />
            <Button Content="Refresh" Command="{Binding RefreshCommand}" />
            <TextBlock Text="checking for interrupted sessions..." Margin="12,0,0,0"
                       VerticalAlignment="Center" FontStyle="Italic"
                       Visibility="{Binding IsScanning, Converter={StaticResource BoolToVis}}" />
        </StackPanel>
        <TextBlock DockPanel.Dock="Bottom" Margin="0,8,0,0" Opacity="0.7"
                   Text="{Binding UnreadableCount, StringFormat='{}{0} unreadable folder(s) skipped'}">
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding UnreadableCount}" Value="0">
                            <Setter Property="Visibility" Value="Collapsed" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>
        <ListView ItemsSource="{Binding Rows}"
                  SelectedItem="{Binding SelectedRow}"
                  VirtualizingPanel.IsVirtualizing="True"
                  VirtualizingPanel.VirtualizationMode="Recycling"
                  ScrollViewer.CanContentScroll="True"
                  MouseDoubleClick="OnRowDoubleClick">
            <ListView.ContextMenu>
                <ContextMenu>
                    <MenuItem Header="Open"
                              Command="{Binding OpenReadViewCommand}"
                              CommandParameter="{Binding SelectedRow}" />
                    <MenuItem Header="Reveal in Explorer"
                              Command="{Binding RevealInExplorerCommand}"
                              CommandParameter="{Binding SelectedRow}" />
                    <MenuItem Header="Archive / Unarchive"
                              Command="{Binding ToggleArchiveCommand}"
                              CommandParameter="{Binding SelectedRow}" />
                </ContextMenu>
            </ListView.ContextMenu>
            <ListView.View>
                <GridView>
                    <GridViewColumn Header="Title" Width="260"
                                    DisplayMemberBinding="{Binding Title}" />
                    <GridViewColumn Header="App" Width="120"
                                    DisplayMemberBinding="{Binding AppMedium}" />
                    <GridViewColumn Header="Date" Width="130">
                        <GridViewColumn.CellTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding DateDisplay}"
                                           ToolTip="{Binding DateTooltip}" />
                            </DataTemplate>
                        </GridViewColumn.CellTemplate>
                    </GridViewColumn>
                    <GridViewColumn Header="Duration" Width="90"
                                    DisplayMemberBinding="{Binding DurationDisplay}" />
                    <GridViewColumn Width="300">
                        <GridViewColumn.CellTemplate>
                            <DataTemplate>
                                <StackPanel Orientation="Horizontal">
                                    <Border Style="{StaticResource Chip}"
                                            Visibility="{Binding IsPendingRecovery, Converter={StaticResource BoolToVis}}">
                                        <TextBlock Text="Recovering..." FontStyle="Italic" />
                                    </Border>
                                    <Border Style="{StaticResource Chip}"
                                            ToolTip="Recovered after an interruption; duration is transcript-derived, not wall-clock"
                                            Visibility="{Binding IsRecovered, Converter={StaticResource BoolToVis}}">
                                        <TextBlock Text="Recovered" />
                                    </Border>
                                    <Border Style="{StaticResource Chip}"
                                            Visibility="{Binding IsEdited, Converter={StaticResource BoolToVis}}">
                                        <TextBlock Text="Edited" />
                                    </Border>
                                    <Border Style="{StaticResource Chip}"
                                            Visibility="{Binding IsDiarised, Converter={StaticResource BoolToVis}}">
                                        <TextBlock Text="Diarised" />
                                    </Border>
                                    <Border Style="{StaticResource Chip}"
                                            ToolTip="{Binding SystemMixTooltip}"
                                            Visibility="{Binding IsSystemMix, Converter={StaticResource BoolToVis}}">
                                        <TextBlock Text="System mix" />
                                    </Border>
                                    <Border Style="{StaticResource Chip}"
                                            Visibility="{Binding IsArchived, Converter={StaticResource BoolToVis}}">
                                        <TextBlock Text="Archived" />
                                    </Border>
                                </StackPanel>
                            </DataTemplate>
                        </GridViewColumn.CellTemplate>
                    </GridViewColumn>
                </GridView>
            </ListView.View>
        </ListView>
        </DockPanel>
        <GridSplitter Grid.Column="1" Width="4" HorizontalAlignment="Center"
                      VerticalAlignment="Stretch" />
        <!-- Grid.Column="2" is reserved for the metadata detail pane: Task 16 inserts a
             ScrollViewer x:Name="DetailPane" here (it renders empty until then). -->
    </Grid>
</Page>
```

Replace the entire contents of `src/LocalScribe.App/Pages/SessionsPage.xaml.cs` (Humble Object: DataContext wiring, navigation-refresh, and the double-click relay only):

```csharp
using System.Windows.Controls;
using System.Windows.Input;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App.Pages;

/// <summary>Thin code-behind for the Sessions page. Constructed by the composition root with
/// its VM (never via a navigation URI, so the non-default ctor is fine). Loaded fires on every
/// navigation into view, which is exactly the 3.1 "page navigation" refresh trigger; LoadAsync
/// catches all exceptions, so the async-void Loaded lambda cannot throw.</summary>
public partial class SessionsPage : Page
{
    private readonly SessionsPageViewModel _vm;

    public SessionsPage(SessionsPageViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await vm.OnNavigatedToAsync();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.OpenReadViewCommand.Execute(_vm.SelectedRow);
}
```

- [ ] **Step 8 — build + full gate.** `dotnet build src/LocalScribe.App` — expected: 0 warnings, 0 errors. Then `dotnet test --filter "Category!=Fixture"` — expected: full suite green.

- [ ] **Step 9 — commit the UI.**
```
git add src/LocalScribe.App/Pages/SessionsPage.xaml src/LocalScribe.App/Pages/SessionsPage.xaml.cs
git commit -m "feat(app): sessions page list UI - virtualized columns, badge chips, filter bar"
```

---

### Task 16: MetadataEditorViewModel + detail pane

The auto-save metadata editor over the Sessions page's `SelectedRow` (design 3.3). Edits meta.json ONLY; every committed change saves immediately (no Save button); metadata saves never flip `Edited`/`LastEditedAtUtc`; the editor locks for the live session and for rows awaiting recovery; participants are snapshot copies (roster pick copies id+name; free text mints a session-scoped `p-` id; add-to-roster mints in the MATTER's scope, writes the matter, then snapshots).

**Files:**
- Create: `src/LocalScribe.App/ViewModels/MetadataEditorViewModel.cs`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (append three matter passthroughs after `RebuildIndexAsync`)
- Modify: `src/LocalScribe.App/Pages/SessionsPage.xaml` + `src/LocalScribe.App/Pages/SessionsPage.xaml.cs` (Task 15 filled Task 14's shells; this task fills the right detail-pane column and extends the page ctor)
- Modify: the composition point that constructs `new SessionsPage(...)` (owned by Task 24's App wiring - see the merge step)
- Test: `tests/LocalScribe.App.Tests/MetadataEditorViewModelTests.cs`

**Interfaces:**
- Consumes (locked): `MaintenanceService(StoragePaths, ISettingsService, IRecycleBin, TimeProvider)` with `SaveMetaAsync(string, SessionMeta, IReadOnlyCollection<string>, CancellationToken)` and its private `_indexGate` / primary-ctor `paths` (Task 9); `IUiErrorReporter` (Task 14); `WindowRegistry()` (Task 9 group, used only by tests to build the page VM); `ParticipantId.Mint(string, IReadOnlyCollection<string>)` (Task 3, `LocalScribe.Core.Storage`); `MatterStore` v2 `ListAsync`/`LoadAsync`/`SaveAsync` + `Matter.Archived`/`MattersIndexEntry.Archived` (Tasks 1-2); `SessionMeta` v2 incl. `Archived` (Task 1); `SessionViewModel.State`/`CurrentSessionId`/`PropertyChanged` (existing, `src/LocalScribe.App/ViewModels/SessionViewModel.cs:21-30`); `SessionState` (`src/LocalScribe.Core/Live/SessionController.cs:13`); G5 Task 15 shapes: `SessionRowViewModel` (`Id`, `IsPendingRecovery`, `Item : SessionListItem`) and `SessionsPageViewModel` ctor `(MaintenanceService, SessionViewModel, WindowRegistry, IUiErrorReporter, Action<Action>, TimeProvider, Action<string>)` + `Rows` + `SelectedRow` + `OnNavigatedToAsync()` (tests mint rows through this locked surface only); `LiveTestDoubles.MakeController/Options` + `ManualUtcTimeProvider` (already Compile-linked into App.Tests, `LocalScribe.App.Tests.csproj`).
- Produces (locked - Task 17 and the page wiring compile against these exactly):

```csharp
namespace LocalScribe.App.ViewModels;
public sealed record MatterOption(string Id, string Display, bool Archived, bool IsSelected);
public sealed record RosterPick(string MatterId, string MemberId, string Display);
public sealed record MediumOption(Medium Value, string Display);
public sealed class ParticipantRow { public ParticipantRow(SessionParticipant snapshot); public SessionParticipant Snapshot { get; } public string Id { get; } public string Name { get; } public SourceKind Side { get; } public string? Role { get; } public bool IsSelf { get; } public string SideDisplay { get; } }
public sealed partial class MetadataEditorViewModel : ObservableObject
{
    public MetadataEditorViewModel(MaintenanceService maintenance, SessionViewModel session,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time);
    public void Attach(SessionRowViewModel? row);
    public void Tick();                                  // clears SavedIndicator 2s after a save
    public Task AddFromRosterAsync(string matterId, string rosterMemberId);
    public void AddFreeText(string name, SourceKind side);
    public Task AddToRosterAndSessionAsync(string matterId, string name, SourceKind side);
    public void Remove(ParticipantRow row);
    // observable: Title, Description, SelectedMedium, LocalCount, RemoteCount, Archived,
    // ShowArchivedMatters, SavedIndicator, IsEditable, LockHint, SelectedRosterPick,
    // NewParticipantName, NewParticipantIsRemote, RosterTargetMatterId
    // collections: MatterOptions, TaggedMatters, RosterPicks, Participants; static MediumOptions
    // commands: ToggleMatterCommand, AddFromRosterCommand, AddFreeTextCommand,
    //           AddToRosterAndSessionCommand, RemoveParticipantCommand
}
// added to MaintenanceService (namespace LocalScribe.App.Services):
public Task<MattersIndex> ListMattersAsync(CancellationToken ct);
public Task<Matter?> LoadMatterAsync(string matterId, CancellationToken ct);
public Task SaveMatterAsync(Matter matter, CancellationToken ct);   // Task 18 declares the same method - first task merged wins, the code below is byte-identical in intent; keep ONE copy
```

Design contract: spec 1.2/1.4 writer split (only meta.json/matter.json are touched by user edits); design 3.3 (auto-save, no Edited flip, live/recovery locks, roster-copy semantics); design 4.2 (id minting scopes).

#### Cycle 1 - MaintenanceService matter passthroughs

The editor's ctor is locked without `StoragePaths` or `MatterStore`, so matter access goes through the maintenance service (design 7.3: all disk mutation from the UI routes through it; the matters.json upsert is serialized on the same `_indexGate` as `RebuildIndexAsync`/tag deltas).

- [ ] **Write the failing test.** Create `tests/LocalScribe.App.Tests/MetadataEditorViewModelTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MetadataEditorViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-metaed-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeReporter _reporter = new();
    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;

    public MetadataEditorViewModelTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths,
            new FakeSettings(new Settings { StorageRoot = _root }), new NoopBin(), _time);
        // A REAL controller over the 3a fakes: the live-gate test needs a genuine
        // CurrentSessionId (SessionViewModel.cs:30 is a controller passthrough).
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        _session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Matter_passthroughs_save_list_and_load_through_maintenance()
    {
        await _maintenance.SaveMatterAsync(new Matter
        {
            Id = "M-2026-001", Name = "Estate of Alpha", Reference = "EST-1",
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        Assert.True(File.Exists(_paths.MatterJson("M-2026-001")));
        var index = await _maintenance.ListMattersAsync(CancellationToken.None);
        Assert.Equal("M-2026-001", Assert.Single(index.Matters).Id);
        var back = await _maintenance.LoadMatterAsync("M-2026-001", CancellationToken.None);
        Assert.Equal("Estate of Alpha", back!.Name);
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
```

- [ ] **Run it and watch it fail.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MetadataEditorViewModelTests"` - expected: build failure `error CS1061: 'MaintenanceService' does not contain a definition for 'SaveMatterAsync'`.
- [ ] **Implement the passthroughs.** In `src/LocalScribe.App/Services/MaintenanceService.cs`, append inside the class body, immediately after the `RebuildIndexAsync` method:

```csharp
    public Task<MattersIndex> ListMattersAsync(CancellationToken ct)
        => new MatterStore(paths.MattersDir).ListAsync(ct);

    public Task<Matter?> LoadMatterAsync(string matterId, CancellationToken ct)
        => new MatterStore(paths.MattersDir).LoadAsync(matterId, ct);

    /// <summary>Persists a matter (matter.json + matters.json index upsert) under the same
    /// lock that serializes RebuildIndexAsync/ApplyTagDelta index writes (design 4.3: ALL
    /// index writes serialized). Returns only after the index upsert completed. Task 18
    /// declares this same method - whichever task merges second drops its duplicate copy.</summary>
    public async Task SaveMatterAsync(Matter matter, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { await new MatterStore(paths.MattersDir).SaveAsync(matter, ct); }
        finally { _indexGate.Release(); }
    }
```

- [ ] **Run it green.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MetadataEditorViewModelTests"` - expected: PASS, 1 test.
- [ ] **Commit.** `git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MetadataEditorViewModelTests.cs && git commit -m "feat: MaintenanceService matter passthroughs serialized on the index gate"`

#### Cycle 2 - the editor ViewModel (auto-save, gates, participants, indicator)

- [ ] **Write the failing tests.** Add to `MetadataEditorViewModelTests.cs`, inside the class, before the `FakeSettings` nested class:

```csharp
    private MetadataEditorViewModel MakeEditor()
        => new(_maintenance, _session, _reporter, dispatch: a => a(), _time);

    /// <summary>Rows are minted through Task 15's LOCKED surface (ctor + OnNavigatedToAsync +
    /// Rows) so this file never guesses SessionRowViewModel's own ctor.</summary>
    private async Task<SessionRowViewModel> RowAsync(string id)
    {
        var page = new SessionsPageViewModel(_maintenance, _session, new WindowRegistry(),
            _reporter, dispatch: a => a(), _time, revealInExplorer: _ => { });
        await page.OnNavigatedToAsync();
        return page.Rows.Single(r => r.Id == id);
    }

    private async Task WriteSessionAsync(string id, string title,
        IReadOnlyList<string>? matterIds = null, bool ended = true)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        var started = new DateTimeOffset(2026, 7, 2, 1, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = ended ? started.AddMinutes(30) : null,
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = ended ? 1_800_000 : 0,
            Model = "small.en", Backend = "cuda", Language = "en",
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        { Title = title, MatterIds = matterIds ?? [] }, CancellationToken.None);
    }

    private async Task WriteMatterAsync(string id, string name, bool archived = false,
        IReadOnlyList<RosterMember>? roster = null)
        => await _maintenance.SaveMatterAsync(new Matter
        {
            Id = id, Name = name, Archived = archived, Roster = roster ?? [],
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

    private int SessionCount(string matterId)
        => new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None)
           .GetAwaiter().GetResult().Matters.Single(m => m.Id == matterId).SessionCount;

    [Fact]
    public async Task Title_commit_autosaves_meta_v2_and_regenerates_session_txt()
    {
        await WriteSessionAsync("s-title", "Old title");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-title"));
        Assert.True(ed.IsEditable);

        ed.Title = "Estate call - corrected";
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));

        string raw = await File.ReadAllTextAsync(_paths.MetaJson("s-title"));
        Assert.Contains("\"schemaVersion\": 2", raw);
        Assert.Contains("Estate call - corrected", raw);
        // SaveMetaAsync regenerates projections before returning (Task 9), and the
        // indicator only lights after it returns - session.txt is already fresh here.
        Assert.Contains("Estate call - corrected",
            await File.ReadAllTextAsync(_paths.SessionTxt("s-title")));
    }

    [Fact]
    public async Task Metadata_save_never_flips_Edited_or_LastEditedAtUtc()
    {
        await WriteSessionAsync("s-flags", "Before");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-flags"));

        ed.Title = "After";
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));

        var back = await new MetadataStore(_paths.MetaJson("s-flags")).LoadAsync(CancellationToken.None);
        Assert.Equal("After", back!.Title);
        Assert.False(back.Edited);                          // design 3.3: EditStore stays the flags' only writer
        Assert.Null(back.LastEditedAtUtc);
        string raw = await File.ReadAllTextAsync(_paths.MetaJson("s-flags"));
        Assert.Contains("\"edited\": false", raw);
        Assert.DoesNotContain("lastEditedAtUtc", raw);      // null is omitted (WhenWritingNull)
    }

    [Fact]
    public async Task Tag_toggle_moves_matter_session_counts_both_ways()
    {
        await WriteSessionAsync("s-tags", "Tagged");
        await WriteMatterAsync("M-2026-001", "Estate of Alpha");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-tags"));
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 1, TimeSpan.FromSeconds(10)));

        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single(o => o.Id == "M-2026-001"));
        Assert.True(SpinWait.SpinUntil(() => SessionCount("M-2026-001") == 1, TimeSpan.FromSeconds(10)));
        var meta = await new MetadataStore(_paths.MetaJson("s-tags")).LoadAsync(CancellationToken.None);
        Assert.Equal(["M-2026-001"], meta!.MatterIds);

        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single(o => o.Id == "M-2026-001"));
        Assert.True(SpinWait.SpinUntil(() => SessionCount("M-2026-001") == 0, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Archived_matters_are_offered_only_when_ShowArchivedMatters()
    {
        await WriteSessionAsync("s-arch", "S");
        await WriteMatterAsync("M-2026-001", "Active");
        await WriteMatterAsync("M-2026-002", "Old", archived: true);
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-arch"));
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 1, TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(ed.MatterOptions, o => o.Id == "M-2026-002");

        ed.ShowArchivedMatters = true;                      // display-only rebuild, never a save
        Assert.Equal(2, ed.MatterOptions.Count);
        Assert.Contains(ed.MatterOptions, o => o.Id == "M-2026-002" && o.Archived);
    }

    [Fact]
    public async Task Roster_pick_copies_id_and_name_into_the_snapshot()
    {
        await WriteMatterAsync("M-2026-001", "Estate of Alpha", roster:
            [new RosterMember { Id = "p-alice-client", Name = "Alice Client", Role = "Client" }]);
        await WriteSessionAsync("s-roster", "S", matterIds: ["M-2026-001"]);
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-roster"));
        Assert.True(SpinWait.SpinUntil(() => ed.RosterPicks.Count == 1, TimeSpan.FromSeconds(10)));

        await ed.AddFromRosterAsync("M-2026-001", "p-alice-client");
        var p = Assert.Single(ed.Participants);
        Assert.Equal("p-alice-client", p.Id);               // COPIED, provenance only
        Assert.Equal("Alice Client", p.Name);
        Assert.Equal("Client", p.Role);
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));
        var meta = await new MetadataStore(_paths.MetaJson("s-roster")).LoadAsync(CancellationToken.None);
        var saved = Assert.Single(meta!.Participants);
        Assert.Equal(("p-alice-client", "Alice Client"), (saved.Id, saved.Name));
    }

    [Fact]
    public async Task Free_text_mints_session_scoped_p_slug_with_collision_suffix()
    {
        await WriteSessionAsync("s-free", "S");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-free"));

        ed.AddFreeText("Bob Witness", SourceKind.Remote);
        ed.AddFreeText("Bob Witness", SourceKind.Local);    // collides within THIS session's ids
        Assert.Equal("p-bob-witness", ed.Participants[0].Id);
        Assert.Equal("p-bob-witness-2", ed.Participants[1].Id);
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));
        var meta = await new MetadataStore(_paths.MetaJson("s-free")).LoadAsync(CancellationToken.None);
        Assert.Equal(2, meta!.Participants.Count);
        Assert.Equal(SourceKind.Local, meta.Participants[1].Side);
    }

    [Fact]
    public async Task Add_to_roster_and_session_writes_the_matter_and_the_snapshot()
    {
        // Existing member forces the mint to collide in the MATTER's scope (design 4.2),
        // proving the id was minted against roster ids, not the session's participant ids.
        await WriteMatterAsync("M-2026-001", "Estate of Alpha", roster:
            [new RosterMember { Id = "p-dan-expert", Name = "Dan Expert" }]);
        await WriteSessionAsync("s-add", "S", matterIds: ["M-2026-001"]);
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-add"));

        await ed.AddToRosterAndSessionAsync("M-2026-001", "Dan Expert", SourceKind.Remote);

        var matter = await new MatterStore(_paths.MattersDir).LoadAsync("M-2026-001", CancellationToken.None);
        Assert.Equal(2, matter!.Roster.Count);
        Assert.Contains(matter.Roster, m => m.Id == "p-dan-expert-2" && m.Name == "Dan Expert");
        var p = Assert.Single(ed.Participants);
        Assert.Equal("p-dan-expert-2", p.Id);
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));
        var meta = await new MetadataStore(_paths.MetaJson("s-add")).LoadAsync(CancellationToken.None);
        Assert.Equal("p-dan-expert-2", Assert.Single(meta!.Participants).Id);
    }

    [Fact]
    public async Task Pending_recovery_row_is_locked_and_saves_nothing()
    {
        await WriteSessionAsync("s-open", "Open", ended: false);   // endedAtUtc null (design 3.1)
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-open"));

        Assert.False(ed.IsEditable);
        Assert.Equal("Available after recovery completes.", ed.LockHint);
        ed.Title = "must not persist";
        var meta = await new MetadataStore(_paths.MetaJson("s-open")).LoadAsync(CancellationToken.None);
        Assert.Equal("Open", meta!.Title);                  // gated save never ran
        Assert.False(ed.SavedIndicator);
        Assert.Empty(_reporter.Errors);
    }

    [Fact]
    public async Task Live_session_is_locked_while_recording_but_other_rows_stay_editable()
    {
        await WriteSessionAsync("s-other", "Other finalized");
        await _session.StartCommand.ExecuteAsync(null);     // real fake-backed recording
        string liveId = _session.CurrentSessionId!;
        var ed = MakeEditor();

        ed.Attach(await RowAsync(liveId));
        Assert.False(ed.IsEditable);                        // design 3.3: live session locked

        ed.Attach(await RowAsync("s-other"));               // ...but only THAT session
        Assert.True(ed.IsEditable);

        await _session.StopCommand.ExecuteAsync(null);      // finalize -> endedAtUtc set
        ed.Attach(await RowAsync(liveId));                  // fresh row after finalize
        Assert.True(ed.IsEditable);
    }

    [Fact]
    public async Task SavedIndicator_clears_two_seconds_later_via_Tick()
    {
        await WriteSessionAsync("s-tick", "T");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-tick"));
        ed.Title = "T2";
        Assert.True(SpinWait.SpinUntil(() => ed.SavedIndicator, TimeSpan.FromSeconds(10)));

        _time.Set(new DateTimeOffset(2026, 7, 3, 9, 0, 1, 900, TimeSpan.Zero));
        ed.Tick();
        Assert.True(ed.SavedIndicator);                     // 1.9s: still showing
        _time.Set(new DateTimeOffset(2026, 7, 3, 9, 0, 2, 0, TimeSpan.Zero));
        ed.Tick();
        Assert.False(ed.SavedIndicator);                    // exactly 2s: cleared
    }

    [Fact]
    public async Task Failed_save_reports_and_reverts_the_editor_copy()
    {
        await WriteSessionAsync("s-fail", "Original");
        var ed = MakeEditor();
        ed.Attach(await RowAsync("s-fail"));

        File.Delete(_paths.MetaJson("s-fail"));
        Directory.CreateDirectory(_paths.MetaJson("s-fail"));   // meta.json is now a DIRECTORY: write throws

        ed.Title = "Doomed";
        Assert.True(SpinWait.SpinUntil(() => _reporter.Errors.Count > 0, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => ed.Title == "Original", TimeSpan.FromSeconds(10)));
        Assert.False(ed.SavedIndicator);
    }
```

- [ ] **Run it and watch it fail.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MetadataEditorViewModelTests"` - expected: build failure `error CS0246: The type or namespace name 'MetadataEditorViewModel' could not be found`.
- [ ] **Implement the ViewModel.** Create `src/LocalScribe.App/ViewModels/MetadataEditorViewModel.cs` with exactly:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.ViewModels;

public sealed record MatterOption(string Id, string Display, bool Archived, bool IsSelected);
public sealed record RosterPick(string MatterId, string MemberId, string Display);
public sealed record MediumOption(Medium Value, string Display);

/// <summary>Immutable list row over a session-participant snapshot. Carrying the whole record
/// (not just the five display fields) keeps reserved fields like ClusterKey lossless when the
/// editor rebuilds meta.Participants from these rows.</summary>
public sealed class ParticipantRow
{
    public ParticipantRow(SessionParticipant snapshot) => Snapshot = snapshot;
    public SessionParticipant Snapshot { get; }
    public string Id => Snapshot.Id;
    public string Name => Snapshot.Name;
    public SourceKind Side => Snapshot.Side;
    public string? Role => Snapshot.Role;
    public bool IsSelf => Snapshot.IsSelf;
    public string SideDisplay => Side == SourceKind.Local ? "Local" : "Remote";
}

/// <summary>The Sessions page's detail-pane editor (design 3.3). Edits meta.json ONLY, via
/// MaintenanceService.SaveMetaAsync; auto-saves on every committed change (no Save button);
/// NEVER flips Edited/LastEditedAtUtc (they flow through from the last-saved meta untouched);
/// locks for the live session and for rows awaiting recovery. WPF-free; timers are Tick().</summary>
public sealed partial class MetadataEditorViewModel : ObservableObject
{
    private static readonly TimeSpan SavedIndicatorDuration = TimeSpan.FromSeconds(2);

    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;

    private SessionRowViewModel? _row;
    private SessionMeta _savedMeta = new();                 // last successfully saved copy (revert target)
    private IReadOnlyList<MattersIndexEntry> _matterEntries = [];
    private readonly List<string> _selectedMatterIds = new();
    // Tag set as of the last SUCCESSFUL save, per session - SaveMetaAsync computes its
    // incremental sessionCount delta against this (design 3.3). Guarded by its own lock:
    // Attach runs on the UI thread, PersistAsync on the save chain.
    private readonly Dictionary<string, string[]> _lastSavedTags = new(StringComparer.Ordinal);
    private Task _saveChain = Task.CompletedTask;           // serializes saves; deltas stay ordered
    private DateTimeOffset _savedIndicatorUntil;
    private bool _loading;

    public static IReadOnlyList<MediumOption> MediumOptions { get; } =
        Enum.GetValues<Medium>()
            .Select(m => new MediumOption(m, m == Medium.InPerson ? "In-person" : m.ToString()))
            .ToArray();

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private Medium _selectedMedium = Medium.Other;
    [ObservableProperty] private int _localCount = 1;
    [ObservableProperty] private int _remoteCount = 1;
    [ObservableProperty] private bool _archived;
    [ObservableProperty] private bool _showArchivedMatters;
    [ObservableProperty] private bool _savedIndicator;
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private string _lockHint = "";
    [ObservableProperty] private RosterPick? _selectedRosterPick;
    [ObservableProperty] private string _newParticipantName = "";
    [ObservableProperty] private bool _newParticipantIsRemote = true;
    [ObservableProperty] private string? _rosterTargetMatterId;

    public ObservableCollection<MatterOption> MatterOptions { get; } = new();
    public ObservableCollection<MatterOption> TaggedMatters { get; } = new();
    public ObservableCollection<RosterPick> RosterPicks { get; } = new();
    public ObservableCollection<ParticipantRow> Participants { get; } = new();

    public IRelayCommand<MatterOption> ToggleMatterCommand { get; }
    public IAsyncRelayCommand AddFromRosterCommand { get; }
    public IRelayCommand AddFreeTextCommand { get; }
    public IAsyncRelayCommand AddToRosterAndSessionCommand { get; }
    public IRelayCommand<ParticipantRow> RemoveParticipantCommand { get; }

    public MetadataEditorViewModel(MaintenanceService maintenance, SessionViewModel session,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time)
    {
        (_maintenance, _session, _errors, _dispatch, _time)
            = (maintenance, session, errors, dispatch, time);

        ToggleMatterCommand = new RelayCommand<MatterOption>(ToggleMatter);
        AddFromRosterCommand = new AsyncRelayCommand(
            () => SelectedRosterPick is { } pick
                ? AddFromRosterAsync(pick.MatterId, pick.MemberId)
                : Task.CompletedTask);
        AddFreeTextCommand = new RelayCommand(() =>
        {
            AddFreeText(NewParticipantName,
                NewParticipantIsRemote ? SourceKind.Remote : SourceKind.Local);
            NewParticipantName = "";
        });
        AddToRosterAndSessionCommand = new AsyncRelayCommand(async () =>
        {
            if (RosterTargetMatterId is not { Length: > 0 } matterId) return;
            await AddToRosterAndSessionAsync(matterId, NewParticipantName,
                NewParticipantIsRemote ? SourceKind.Remote : SourceKind.Local);
            NewParticipantName = "";
        });
        RemoveParticipantCommand = new RelayCommand<ParticipantRow>(r => { if (r is not null) Remove(r); });

        // SessionViewModel raises State changes already marshaled through ITS dispatch
        // (SessionViewModel.cs:56-62), so this handler runs on the UI thread.
        session.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(SessionViewModel.State)) RecomputeEditable(); };
    }

    /// <summary>Bound to SessionsPageViewModel.SelectedRow by the page code-behind. Loads the
    /// editable copy from the row's meta snapshot; null detaches and disables the pane.</summary>
    public void Attach(SessionRowViewModel? row)
    {
        _row = row;
        _loading = true;
        try
        {
            _savedMeta = row?.Item.Meta ?? new SessionMeta();
            if (row is not null)
            {
                lock (_lastSavedTags)
                {
                    // Only seed on first sight: a re-attach must keep the delta base at the
                    // last SAVE, not the possibly stale listing snapshot.
                    if (!_lastSavedTags.ContainsKey(row.Id))
                        _lastSavedTags[row.Id] = _savedMeta.MatterIds.ToArray();
                }
            }
            LoadFieldsFromSaved();
        }
        finally { _loading = false; }
        SavedIndicator = false;
        RecomputeEditable();
        if (row is not null) _ = RefreshMatterDataAsync();
        else { MatterOptions.Clear(); TaggedMatters.Clear(); RosterPicks.Clear(); }
    }

    /// <summary>Driven by a ~250 ms DispatcherTimer in production; tests call it directly.</summary>
    public void Tick()
    {
        if (SavedIndicator && _time.GetUtcNow() >= _savedIndicatorUntil) SavedIndicator = false;
    }

    /// <summary>Roster pick COPIES the member's id and name into the session snapshot -
    /// provenance only, never a live link (design 3.3). Side defaults to Remote (roster
    /// members are other people); remove-and-re-add corrects a wrong side.</summary>
    public async Task AddFromRosterAsync(string matterId, string rosterMemberId)
    {
        try
        {
            var matter = await _maintenance.LoadMatterAsync(matterId, CancellationToken.None);
            var member = matter?.Roster.FirstOrDefault(m => m.Id == rosterMemberId);
            if (member is null)
            { _dispatch(() => _errors.Info("That roster member no longer exists.")); return; }
            _dispatch(() =>
            {
                if (Participants.Any(p => p.Id == member.Id))
                { _errors.Info($"{member.Name} is already a participant."); return; }
                Participants.Add(new ParticipantRow(new SessionParticipant
                { Id = member.Id, Name = member.Name, Side = SourceKind.Remote, Role = member.Role }));
                QueueSave();
            });
        }
        catch (Exception ex) { _dispatch(() => _errors.Report("Adding participant", ex)); }
    }

    /// <summary>Free-text participant: id minted against THIS session's participant ids
    /// (session-scoped, design 4.2).</summary>
    public void AddFreeText(string name, SourceKind side)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        string id = ParticipantId.Mint(trimmed, Participants.Select(p => p.Id).ToArray());
        Participants.Add(new ParticipantRow(new SessionParticipant
        { Id = id, Name = trimmed, Side = side }));
        QueueSave();
    }

    /// <summary>Inline person add (design L547-550): mint against the MATTER's roster ids,
    /// write through to the matter's roster, then snapshot id+name into this session.</summary>
    public async Task AddToRosterAndSessionAsync(string matterId, string name, SourceKind side)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        try
        {
            var matter = await _maintenance.LoadMatterAsync(matterId, CancellationToken.None);
            if (matter is null)
            { _dispatch(() => _errors.Info("Tag a matter before adding to its roster.")); return; }
            string id = ParticipantId.Mint(trimmed, matter.Roster.Select(m => m.Id).ToArray());
            var member = new RosterMember { Id = id, Name = trimmed };
            await _maintenance.SaveMatterAsync(
                matter with { Roster = matter.Roster.Append(member).ToArray() },
                CancellationToken.None);
            _dispatch(() =>
            {
                Participants.Add(new ParticipantRow(new SessionParticipant
                { Id = id, Name = trimmed, Side = side }));
                RosterPicks.Add(new RosterPick(matterId, id, $"{trimmed} ({matter.Name})"));
                QueueSave();
            });
        }
        catch (Exception ex) { _dispatch(() => _errors.Report("Adding to roster", ex)); }
    }

    public void Remove(ParticipantRow row)
    {
        if (Participants.Remove(row)) QueueSave();
    }

    partial void OnTitleChanged(string value) => QueueSave();
    partial void OnDescriptionChanged(string value) => QueueSave();
    partial void OnSelectedMediumChanged(Medium value) => QueueSave();
    partial void OnArchivedChanged(bool value) => QueueSave();
    partial void OnLocalCountChanged(int value)
    { if (value < 1) { LocalCount = 1; return; } QueueSave(); }
    partial void OnRemoteCountChanged(int value)
    { if (value < 1) { RemoteCount = 1; return; } QueueSave(); }
    // Display-only filter (design 4.1: non-persisted, per-pane UI state) - never a save.
    partial void OnShowArchivedMattersChanged(bool value) => RebuildMatterOptions();

    private void ToggleMatter(MatterOption? option)
    {
        if (option is null || _loading) return;
        if (!_selectedMatterIds.Remove(option.Id)) _selectedMatterIds.Add(option.Id);
        RebuildMatterOptions();
        QueueSave();
        _ = RefreshMatterDataAsync();                       // roster picks follow the tagged set
    }

    /// <summary>Snapshot the fields on the caller (UI) thread, then append the write to the
    /// save chain. The chain serializes saves so each SaveMetaAsync sees the delta base left
    /// by the previous one; per-session ordering on disk is Task 9's single-flight queue.</summary>
    private void QueueSave()
    {
        if (_loading || _row is null || !IsEditable) return;
        var row = _row;
        var meta = BuildMeta();
        _saveChain = _saveChain.ContinueWith(_ => PersistAsync(row, meta),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private SessionMeta BuildMeta() => _savedMeta with
    {
        // Edited / LastEditedAtUtc / Summary* flow through from the last-saved meta UNTOUCHED:
        // metadata saves never flip the correction flags (design 3.3, evidentiary invariant).
        Title = Title,
        Description = Description,
        Medium = SelectedMedium,
        MatterIds = _selectedMatterIds.ToArray(),
        Participants = Participants.Select(p => p.Snapshot).ToArray(),
        LocalCount = LocalCount,
        RemoteCount = RemoteCount,
        Archived = Archived,
    };

    private async Task PersistAsync(SessionRowViewModel row, SessionMeta meta)
    {
        string[] previous;
        lock (_lastSavedTags)
            previous = _lastSavedTags.TryGetValue(row.Id, out var p) ? p : [];
        try
        {
            await _maintenance.SaveMetaAsync(row.Id, meta, previous, CancellationToken.None)
                .ConfigureAwait(false);
            lock (_lastSavedTags) _lastSavedTags[row.Id] = meta.MatterIds.ToArray();
            _dispatch(() =>
            {
                if (!ReferenceEquals(_row, row)) return;    // user moved on mid-save
                _savedMeta = meta;
                SavedIndicator = true;
                _savedIndicatorUntil = _time.GetUtcNow() + SavedIndicatorDuration;
            });
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                _errors.Report("Saving session details", ex);
                if (ReferenceEquals(_row, row)) RevertToSaved();
            });
        }
    }

    private void RevertToSaved()
    {
        _loading = true;
        try { LoadFieldsFromSaved(); }
        finally { _loading = false; }
        SavedIndicator = false;
    }

    private void LoadFieldsFromSaved()
    {
        Title = _savedMeta.Title;
        Description = _savedMeta.Description;
        SelectedMedium = _savedMeta.Medium;
        LocalCount = _savedMeta.LocalCount;
        RemoteCount = _savedMeta.RemoteCount;
        Archived = _savedMeta.Archived;
        _selectedMatterIds.Clear();
        _selectedMatterIds.AddRange(_savedMeta.MatterIds);
        Participants.Clear();
        foreach (var p in _savedMeta.Participants) Participants.Add(new ParticipantRow(p));
        RebuildMatterOptions();
    }

    private void RecomputeEditable()
    {
        bool liveLocked = _row is not null
            && _row.Id == _session.CurrentSessionId
            && _session.State is SessionState.Recording or SessionState.Paused or SessionState.Finalizing;
        IsEditable = _row is not null && !_row.IsPendingRecovery && !liveLocked;
        LockHint = _row is null || IsEditable ? ""
            : _row.IsPendingRecovery ? "Available after recovery completes."
            : "Available when recording stops.";
    }

    private async Task RefreshMatterDataAsync()
    {
        try
        {
            var index = await _maintenance.ListMattersAsync(CancellationToken.None);
            // Roster picks: union of the TAGGED matters' rosters (design 3.3).
            var picks = new List<RosterPick>();
            foreach (string mid in _selectedMatterIds.ToArray())
            {
                var matter = await _maintenance.LoadMatterAsync(mid, CancellationToken.None);
                if (matter is null) continue;
                foreach (var member in matter.Roster)
                    picks.Add(new RosterPick(mid, member.Id, $"{member.Name} ({matter.Name})"));
            }
            _dispatch(() =>
            {
                _matterEntries = index.Matters;
                RebuildMatterOptions();
                RosterPicks.Clear();
                foreach (var p in picks) RosterPicks.Add(p);
            });
        }
        catch (Exception ex) { _dispatch(() => _errors.Report("Loading matters", ex)); }
    }

    private void RebuildMatterOptions()
    {
        MatterOptions.Clear();
        TaggedMatters.Clear();
        foreach (var e in _matterEntries)
        {
            bool selected = _selectedMatterIds.Contains(e.Id);
            string display = string.IsNullOrEmpty(e.Reference) ? e.Name : $"{e.Name} ({e.Reference})";
            if (e.Archived) display += " (archived)";
            var option = new MatterOption(e.Id, display, e.Archived, selected);
            // Archived matters are OFFERED only under the toggle (design 4.1); a hidden
            // selected tag stays tagged - _selectedMatterIds is the truth, not this list.
            if (!e.Archived || ShowArchivedMatters) MatterOptions.Add(option);
            if (selected) TaggedMatters.Add(option);
        }
        if (RosterTargetMatterId is null || !_selectedMatterIds.Contains(RosterTargetMatterId))
            RosterTargetMatterId = _selectedMatterIds.Count > 0 ? _selectedMatterIds[0] : null;
    }
}
```

- [ ] **Run it green.** `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MetadataEditorViewModelTests"` - expected: PASS, 11 tests.
- [ ] **Commit.** `git add src/LocalScribe.App/ViewModels/MetadataEditorViewModel.cs tests/LocalScribe.App.Tests/MetadataEditorViewModelTests.cs && git commit -m "feat: MetadataEditorViewModel - auto-save meta editor with live/recovery locks"`

#### Cycle 3 - the detail pane XAML + page wiring (view layer, no VM changes)

- [ ] **Insert the pane.** In `src/LocalScribe.App/Pages/SessionsPage.xaml`: (a) if the root element does not already declare it, add `xmlns:vm="clr-namespace:LocalScribe.App.ViewModels"`; (b) if the page resources do not already contain one, add `<BooleanToVisibilityConverter x:Key="BoolToVis" />`; (c) locate the root Grid's right-most column - the column Task 15 reserved for the detail pane (its `Grid.Column` index is shown as `2` below; use the actual index, and delete any placeholder element Task 15 left there) - and insert this element as the last child of that root Grid:

```xml
<!-- Metadata editor detail pane (design 3.3). DataContext = MetadataEditorViewModel, set in
     code-behind. Auto-save on commit: TextBoxes bind LostFocus, so there is no Save button. -->
<ScrollViewer x:Name="DetailPane" Grid.Column="2" VerticalScrollBarVisibility="Auto">
    <StackPanel Margin="12">
        <DockPanel>
            <TextBlock DockPanel.Dock="Right" Text="Saved" Foreground="Gray"
                       Visibility="{Binding SavedIndicator, Converter={StaticResource BoolToVis}}" />
            <TextBlock Text="Details" FontWeight="SemiBold" />
        </DockPanel>
        <TextBlock Text="{Binding LockHint}" Foreground="Gray" Margin="0,4,0,0" />
        <StackPanel IsEnabled="{Binding IsEditable}">
            <TextBlock Text="Title" Margin="0,12,0,4" />
            <TextBox Text="{Binding Title, UpdateSourceTrigger=LostFocus}" />
            <TextBlock Text="Description" Margin="0,12,0,4" />
            <TextBox Text="{Binding Description, UpdateSourceTrigger=LostFocus}"
                     AcceptsReturn="True" MinLines="3" TextWrapping="Wrap" />
            <TextBlock Text="Medium" Margin="0,12,0,4" />
            <ComboBox ItemsSource="{x:Static vm:MetadataEditorViewModel.MediumOptions}"
                      DisplayMemberPath="Display" SelectedValuePath="Value"
                      SelectedValue="{Binding SelectedMedium}" />
            <DockPanel Margin="0,12,0,4">
                <CheckBox DockPanel.Dock="Right" Content="Show archived"
                          IsChecked="{Binding ShowArchivedMatters}" />
                <TextBlock Text="Matters" />
            </DockPanel>
            <ItemsControl ItemsSource="{Binding MatterOptions}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <CheckBox Content="{Binding Display}" IsChecked="{Binding IsSelected, Mode=OneWay}"
                                  Command="{Binding DataContext.ToggleMatterCommand,
                                            RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                  CommandParameter="{Binding}" />
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <TextBlock Text="Participants" Margin="0,12,0,4" />
            <ItemsControl ItemsSource="{Binding Participants}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <DockPanel Margin="0,2,0,2">
                            <Button DockPanel.Dock="Right" Content="Remove"
                                    Command="{Binding DataContext.RemoveParticipantCommand,
                                              RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                    CommandParameter="{Binding}" />
                            <TextBlock VerticalAlignment="Center">
                                <Run Text="{Binding Name, Mode=OneWay}" />
                                <Run Text="{Binding SideDisplay, Mode=OneWay}" Foreground="Gray" />
                                <Run Text="{Binding Role, Mode=OneWay}" Foreground="Gray" />
                            </TextBlock>
                        </DockPanel>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <DockPanel Margin="0,8,0,0">
                <Button DockPanel.Dock="Right" Content="Add"
                        Command="{Binding AddFromRosterCommand}" Margin="8,0,0,0" />
                <ComboBox ItemsSource="{Binding RosterPicks}" DisplayMemberPath="Display"
                          SelectedItem="{Binding SelectedRosterPick}" />
            </DockPanel>
            <DockPanel Margin="0,8,0,0">
                <Button DockPanel.Dock="Right" Content="Add to roster + session"
                        Command="{Binding AddToRosterAndSessionCommand}" Margin="8,0,0,0" />
                <Button DockPanel.Dock="Right" Content="Add"
                        Command="{Binding AddFreeTextCommand}" Margin="8,0,0,0" />
                <CheckBox DockPanel.Dock="Right" Content="Remote" VerticalAlignment="Center"
                          IsChecked="{Binding NewParticipantIsRemote}" Margin="8,0,0,0" />
                <TextBox Text="{Binding NewParticipantName, UpdateSourceTrigger=PropertyChanged}" />
            </DockPanel>
            <DockPanel Margin="0,4,0,0">
                <TextBlock Text="Roster target:" VerticalAlignment="Center" Margin="0,0,8,0" />
                <ComboBox ItemsSource="{Binding TaggedMatters}" DisplayMemberPath="Display"
                          SelectedValuePath="Id" SelectedValue="{Binding RosterTargetMatterId}" />
            </DockPanel>
            <UniformGrid Columns="2" Margin="0,12,0,0">
                <StackPanel Margin="0,0,6,0">
                    <TextBlock Text="Local speakers" Margin="0,0,0,4" />
                    <TextBox Text="{Binding LocalCount, UpdateSourceTrigger=LostFocus}" />
                </StackPanel>
                <StackPanel Margin="6,0,0,0">
                    <TextBlock Text="Remote speakers" Margin="0,0,0,4" />
                    <TextBox Text="{Binding RemoteCount, UpdateSourceTrigger=LostFocus}" />
                </StackPanel>
            </UniformGrid>
            <CheckBox Content="Archived (hidden from the default list)"
                      IsChecked="{Binding Archived}" Margin="0,12,0,0" />
        </StackPanel>
    </StackPanel>
</ScrollViewer>
```

- [ ] **Wire the code-behind.** In `src/LocalScribe.App/Pages/SessionsPage.xaml.cs`, extend the ctor Task 15 produced with a second parameter `MetadataEditorViewModel editor` (keep Task 15's body unchanged) and append at the end of the ctor:

```csharp
        DetailPane.DataContext = editor;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionsPageViewModel.SelectedRow))
                editor.Attach(vm.SelectedRow);
        };
        editor.Attach(vm.SelectedRow);
        // Drives the 2s Saved-indicator countdown; the VM stays timer-free (house rule).
        var editorTick = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(250) };
        editorTick.Tick += (_, _) => editor.Tick();
        editorTick.Start();
        // Pages are rebuilt per MainWindow open (design section 2) - stop the tick with the
        // page so closed windows do not accumulate live timers.
        Unloaded += (_, _) => editorTick.Stop();
        Loaded += (_, _) => { if (!editorTick.IsEnabled) editorTick.Start(); };
```

(`vm` is Task 15's existing ctor parameter name for the `SessionsPageViewModel`; if it named it differently, use that name.)
- [ ] **Merge point with the MainWindow composition (owned by Task 24).** Where the composition root constructs the Sessions page (Task 24's App wiring builds `new SessionsPage(sessionsPageViewModel, ...)` inside the MainWindow page provider), build the editor from the SAME `MaintenanceService`, `SessionViewModel`, `IUiErrorReporter`, and dispatcher delegate already in scope there and pass it as the new second argument: `new SessionsPage(sessionsPageViewModel, new MetadataEditorViewModel(maintenance, sessionViewModel, errors, dispatch, TimeProvider.System))` (identifier names as they exist at that composition point). If Task 24 has not landed yet, no call site exists and this step is a no-op — Task 24 constructs the page with both VMs.
- [ ] **Full gate.** `dotnet build` - expected: 0 warnings, 0 errors. Then `dotnet test --filter "Category!=Fixture"` - expected: PASS, no failures.
- [ ] **Commit.** `git add src/LocalScribe.App/Pages/SessionsPage.xaml src/LocalScribe.App/Pages/SessionsPage.xaml.cs src/LocalScribe.App && git commit -m "feat: Sessions page metadata detail pane bound to the auto-save editor"`

---

### Task 17: Whole-session delete flow

Implements design section 3.4: DeleteSessionCommand on the Sessions page with the recording/pending-recovery guards, a close-windows-BEFORE-recycle ordering guarantee, matter-count decrement via the locked `MaintenanceService.DeleteSessionAsync`, and the one confirmation dialog in the window layer.

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs` (Task 15 file - exact insertions below)
- Modify: `src/LocalScribe.App/Pages/SessionsPage.xaml` and `src/LocalScribe.App/Pages/SessionsPage.xaml.cs` (Task 15's page - dialog + row action)
- Create (test): `tests/LocalScribe.App.Tests/DeleteFlowTests.cs`

**Interfaces:**

Consumes (locked, do not redefine):
- `MaintenanceService.DeleteSessionAsync(string sessionId, IReadOnlyCollection<string> taggedMatterIds, CancellationToken ct)`, `SaveMetaAsync(...)`, `ListSessionsAsync(ct)`, `ListMattersAsync(ct)` (src/LocalScribe.App/Services/MaintenanceService.cs; ListMattersAsync is Task 16's passthrough)
- `WindowRegistry.Register(string sessionId, Action close)` / `CloseAllFor(string sessionId)`; `IUiErrorReporter.Info/Report` (LocalScribe.App.Services)
- `SessionViewModel.State` (`LocalScribe.Core.Live.SessionState`) and `SessionViewModel.CurrentSessionId`
- Task 15's `SessionRowViewModel` (Id/Title/DateDisplay/DurationDisplay/MatterIds/IsPendingRecovery) and `SessionsPageViewModel` (locked ctor, `Rows`, `RefreshCommand`, `OnNavigatedToAsync`)
- `IRecycleBin` (Task 7, `LocalScribe.Core.Storage`): `public interface IRecycleBin { void SendToRecycleBin(string path); }`
- `MaintenanceService` ctor (Task 9, locked): `(StoragePaths paths, ISettingsService settings, IRecycleBin recycleBin, TimeProvider time)`

Produces (G5 contract, exact):
```csharp
public sealed record DeleteConfirmation(string Title, string DateDisplay, string DurationDisplay, IReadOnlyList<string> MatterNames);
public IAsyncRelayCommand<SessionRowViewModel> DeleteSessionCommand { get; }   // on SessionsPageViewModel
public event Action<DeleteConfirmation, Action>? ConfirmDeleteRequested;       // window layer shows dialog, invokes the Action on confirm
```
Event contract (documented on the event): the handler invokes the confirm `Action` SYNCHRONOUSLY before returning if the user confirms. This lets the command await the whole close-recycle-refresh flow (`ExecuteAsync` completes only when the delete is done) and is exactly what a modal `MessageBox.Show` gives the window layer for free.

**Steps:**

- [ ] **Alignment check (read-only).** Open Task 15's landed `SessionsPageViewModel.cs` and confirm these members exist with these exact names (they are the shared-field contract this task's insertions compile against; Task 15 declares them, this task only reads them):
```csharp
private readonly MaintenanceService _maintenance;
private readonly WindowRegistry _registry;
private readonly IUiErrorReporter _errors;
```
Two things Task 15 deliberately does NOT provide: it never stores its `SessionViewModel` ctor parameter in a field (it only subscribes to it in the ctor - insertion 2 below adds the `_session` field), and it keeps no matters-index cache (the delete flow reads matter display names fresh through Task 16's `ListMattersAsync` passthrough - insertion 5). Also confirm the MaintenanceService/IRecycleBin signatures under "Consumes" above against the Task 9/Task 7 files. Every construction in this task's tests goes through one `CreateMaintenance` helper and one `RecordingRecycleBin` class, so a signature drift is a two-line fix in the test file and touches nothing else.

- [ ] **Write the failing tests** - create `tests/LocalScribe.App.Tests/DeleteFlowTests.cs`:
```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class DeleteFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-del-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    // Shared ordered event log: the WindowRegistry close action and the fake recycle bin both
    // append here, so a single list proves close-before-recycle ordering.
    private readonly List<string> _events = new();

    public DeleteFlowTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ---- doubles -------------------------------------------------------------------------

    private sealed class RecordingRecycleBin : IRecycleBin
    {
        private readonly List<string> _events;
        public RecordingRecycleBin(List<string> events) => _events = events;
        public void SendToRecycleBin(string path)
        {
            _events.Add("recycle:" + Path.GetFileName(path));
            // Emulate the shell move so a post-delete refresh over the REAL store sees it gone.
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FakeErrors : IUiErrorReporter
    {
        public readonly List<string> Infos = new();
        public readonly List<(string Context, Exception Ex)> Reports = new();
        public void Report(string context, Exception ex) => Reports.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }

    // Local fake for MaintenanceService's Task 9 ctor, byte-identical to Task 16's so both
    // test files compile standalone whichever task lands first.
    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }

    // ---- wiring --------------------------------------------------------------------------

    // Single construction point for the service under test (Task 9's locked ctor).
    private MaintenanceService CreateMaintenance(IRecycleBin bin)
        => new(_paths, new FakeSettings(new Settings()), bin, TimeProvider.System);

    private (SessionsPageViewModel Page, SessionViewModel Session, WindowRegistry Registry,
             FakeErrors Errors, MaintenanceService Maintenance) MakePage()
    {
        var maintenance = CreateMaintenance(new RecordingRecycleBin(_events));
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var registry = new WindowRegistry();
        var errors = new FakeErrors();
        var page = new SessionsPageViewModel(maintenance, session, registry, errors,
            dispatch: a => a(), time: TimeProvider.System, revealInExplorer: _ => { });
        return (page, session, registry, errors, maintenance);
    }

    // ---- fixtures (real stores over the temp root) ----------------------------------------

    private async Task<string> AddSessionAsync(string id, string title, DateTimeOffset? endedAtUtc)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 1, 2, 0, 0, TimeSpan.Zero),
            EndedAtUtc = endedAtUtc,
            TimeZoneId = "AUS Eastern Standard Time", UtcOffsetMinutes = 600,
            DurationMs = endedAtUtc is null ? 0 : 30 * 60 * 1000,
            Model = "small.en", Backend = "cpu",
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = title }, CancellationToken.None);
        return id;
    }

    private Task AddMatterAsync(string id, string name, string? reference)
        => new MatterStore(_paths.MattersDir).SaveAsync(new Matter
        {
            Id = id, Name = name, Reference = reference,
            DateCreatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

    private async Task<int> MatterCountAsync(string matterId)
        => (await new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None))
            .Matters.Single(m => m.Id == matterId).SessionCount;

    // ---- tests ---------------------------------------------------------------------------

    [Fact]
    public async Task Confirmed_delete_closes_windows_first_then_recycles_then_refreshes()
    {
        await AddMatterAsync("M-2026-001", "Smith v Jones", "REF-1");
        string id = await AddSessionAsync("s-one", "Webex call one",
            endedAtUtc: new DateTimeOffset(2026, 7, 1, 2, 30, 0, TimeSpan.Zero));
        var (page, _, registry, errors, maintenance) = MakePage();

        // Tag through the real service so matters.json sessionCount becomes 1 first.
        var meta = (await new MetadataStore(_paths.MetaJson(id)).LoadAsync(CancellationToken.None))!;
        await maintenance.SaveMetaAsync(id, meta with { MatterIds = ["M-2026-001"] },
            previousMatterIds: [], CancellationToken.None);
        Assert.Equal(1, await MatterCountAsync("M-2026-001"));

        registry.Register(id, () => _events.Add("close:" + id));   // an "open read view"
        DeleteConfirmation? payload = null;
        page.ConfirmDeleteRequested += (p, confirm) => { payload = p; confirm(); };  // scripted auto-confirm

        await page.OnNavigatedToAsync();
        var row = page.Rows.Single(r => r.Id == id);
        await page.DeleteSessionCommand.ExecuteAsync(row);

        // Order is the contract: read views closed BEFORE the recycle call (design 3.4).
        Assert.Equal(new[] { "close:" + id, "recycle:" + id }, _events);
        Assert.NotNull(payload);
        Assert.Equal("Webex call one", payload!.Title);
        Assert.Equal(row.DateDisplay, payload.DateDisplay);
        Assert.Equal(row.DurationDisplay, payload.DurationDisplay);
        Assert.Equal(new[] { "Smith v Jones (REF-1)" }, payload.MatterNames);
        Assert.False(Directory.Exists(_paths.SessionDir(id)));
        Assert.DoesNotContain(page.Rows, r => r.Id == id);          // refreshed
        Assert.Equal(0, await MatterCountAsync("M-2026-001"));      // decremented on disk
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Declined_confirmation_deletes_nothing()
    {
        string id = await AddSessionAsync("s-keep", "Keep me",
            endedAtUtc: new DateTimeOffset(2026, 7, 1, 2, 30, 0, TimeSpan.Zero));
        var (page, _, _, _, _) = MakePage();
        page.ConfirmDeleteRequested += (_, _) => { };               // user pressed Cancel/No

        await page.OnNavigatedToAsync();
        await page.DeleteSessionCommand.ExecuteAsync(page.Rows.Single(r => r.Id == id));

        Assert.Empty(_events);
        Assert.True(Directory.Exists(_paths.SessionDir(id)));
        Assert.Contains(page.Rows, r => r.Id == id);
    }

    [Fact]
    public async Task Live_session_delete_is_refused_and_folder_untouched()
    {
        var (page, session, _, errors, _) = MakePage();
        bool asked = false;
        page.ConfirmDeleteRequested += (_, confirm) => { asked = true; confirm(); };

        await session.StartCommand.ExecuteAsync(null);              // real controller over fakes
        try
        {
            string liveId = session.CurrentSessionId!;
            await page.OnNavigatedToAsync();                        // live folder is on disk already
            var row = page.Rows.Single(r => r.Id == liveId);

            await page.DeleteSessionCommand.ExecuteAsync(row);

            Assert.False(asked);                                    // never even asked
            Assert.Empty(_events);                                  // no close, no recycle
            Assert.True(Directory.Exists(_paths.SessionDir(liveId)));
            Assert.Contains(errors.Infos,
                m => m.Contains("recording", StringComparison.OrdinalIgnoreCase));
        }
        finally { await session.StopCommand.ExecuteAsync(null); }
    }

    [Fact]
    public async Task Pending_recovery_delete_is_refused_and_folder_untouched()
    {
        string id = await AddSessionAsync("s-interrupted", "Interrupted", endedAtUtc: null);
        var (page, _, _, errors, _) = MakePage();
        bool asked = false;
        page.ConfirmDeleteRequested += (_, confirm) => { asked = true; confirm(); };

        await page.OnNavigatedToAsync();
        var row = page.Rows.Single(r => r.Id == id);
        Assert.True(row.IsPendingRecovery);

        await page.DeleteSessionCommand.ExecuteAsync(row);

        Assert.False(asked);
        Assert.Empty(_events);
        Assert.True(Directory.Exists(_paths.SessionDir(id)));
        Assert.Contains(errors.Infos,
            m => m.Contains("recover", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Run** `dotnet test tests/LocalScribe.App.Tests --filter "Category!=Fixture"` - expected FAILURE: the test project does not compile - `CS0246: The type or namespace name 'DeleteConfirmation' could not be found` and `CS1061: 'SessionsPageViewModel' does not contain a definition for 'DeleteSessionCommand'` / `'ConfirmDeleteRequested'`.

- [ ] **Implement** - insertions into `src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs` (Task 15's file; the file already has `using CommunityToolkit.Mvvm.Input;`, `using LocalScribe.Core.Live;`, and `using LocalScribe.Core.Model;` - add any of these that are missing).

  Insertion 1 - at namespace level, directly ABOVE the `SessionsPageViewModel` class declaration:
```csharp
/// <summary>Payload for the whole-session delete confirmation dialog (design 3.4). MatterNames
/// are resolved from a fresh matters-index read; dangling ids render raw, matching SessionWriter.</summary>
public sealed record DeleteConfirmation(
    string Title, string DateDisplay, string DurationDisplay, IReadOnlyList<string> MatterNames);
```

  Insertion 2 - inside the class, directly after the existing `private readonly MaintenanceService _maintenance;` field (Task 15 subscribes to the SessionViewModel but never keeps it; the live-session guard below needs it):
```csharp
private readonly SessionViewModel _session;
```

  Insertion 3 - inside the class, directly after the existing `public IAsyncRelayCommand RefreshCommand { get; }` property:
```csharp
public IAsyncRelayCommand<SessionRowViewModel> DeleteSessionCommand { get; }

/// <summary>Raised instead of deleting directly. Contract: the window-layer handler shows a
/// MODAL confirmation and invokes the Action synchronously (before returning) if the user
/// confirms; the command then awaits close -> recycle -> refresh, so ExecuteAsync completes
/// only when the whole flow is done. No subscriber means no delete - never delete silently.</summary>
public event Action<DeleteConfirmation, Action>? ConfirmDeleteRequested;
```

  Insertion 4 - in the constructor body, directly after the existing `RefreshCommand = ...` assignment:
```csharp
_session = session;
DeleteSessionCommand = new AsyncRelayCommand<SessionRowViewModel>(DeleteSessionAsync);
```

  Insertion 5 - at the end of the class body, after the existing private methods:
```csharp
private async Task DeleteSessionAsync(SessionRowViewModel? row)
{
    if (row is null) return;

    // Live check FIRST: a live session also has endedAtUtc == null so it would otherwise fall
    // into the pending-recovery message; "stop the recording" is the actionable instruction.
    if (row.Id == _session.CurrentSessionId
        && _session.State is SessionState.Recording or SessionState.Paused or SessionState.Finalizing)
    {
        _errors.Info("Cannot delete: this session is recording. Stop the recording first.");
        return;
    }
    if (row.IsPendingRecovery)
    {
        _errors.Info("Cannot delete: this session is still being recovered. Try again once recovery completes.");
        return;
    }

    var handler = ConfirmDeleteRequested;
    if (handler is null) return;

    // Matter display names come fresh from the index (Task 16's ListMattersAsync passthrough);
    // a read failure degrades to raw ids rather than blocking the confirmation dialog.
    IReadOnlyList<MattersIndexEntry> mattersIndex;
    try { mattersIndex = (await _maintenance.ListMattersAsync(CancellationToken.None)).Matters; }
    catch { mattersIndex = []; }

    var matterNames = row.MatterIds.Select(mid =>
        mattersIndex.FirstOrDefault(m => m.Id == mid) is { } entry
            ? (string.IsNullOrEmpty(entry.Reference) ? entry.Name : $"{entry.Name} ({entry.Reference})")
            : mid).ToList();

    bool confirmed = false;
    handler(new DeleteConfirmation(row.Title, row.DateDisplay, row.DurationDisplay, matterNames),
        () => confirmed = true);
    if (!confirmed) return;

    try
    {
        // Order is load-bearing (design 3.4): close any open read views FIRST so their audio
        // file handles are released, or the shell recycle can fail on a sharing violation.
        _registry.CloseAllFor(row.Id);
        await _maintenance.DeleteSessionAsync(row.Id, row.MatterIds, CancellationToken.None);
    }
    catch (Exception ex)
    {
        _errors.Report("Delete session", ex);
    }
    await RefreshCommand.ExecuteAsync(null);   // refresh even after a failure - show disk truth
}
```

- [ ] **Run** `dotnet test tests/LocalScribe.App.Tests --filter "Category!=Fixture"` - expected: all 4 DeleteFlowTests PASS (plus the existing App tests stay green).

- [ ] **Commit:**
```
git add src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs tests/LocalScribe.App.Tests/DeleteFlowTests.cs
git commit -m "feat(sessions): whole-session delete command - guards, confirm event, close-before-recycle order"
```

- [ ] **Window layer - dialog choice and wiring.** Choice: `System.Windows.MessageBox`, not WPF-UI's ContentDialog. Rationale, having read TrayIconHost.cs lines 55-81: the tray's stop-and-exit confirmation already uses `MessageBox.Show(..., MessageBoxButton.YesNo, MessageBoxImage.Warning)`, so this is the established confirmation idiom in the codebase; ContentDialog in a NavigationView page needs a `ContentPresenter` dialog host plus an async `ShowAsync`, which would break the synchronous-confirm event contract for zero visual gain. Accepted trade-off: buttons read "Yes"/"No" rather than "Delete"/"Cancel"; the "Delete session?" caption plus the explicit body sentence keep it unambiguous, and No is the default button so Enter never deletes.

  Insertion into `src/LocalScribe.App/Pages/SessionsPage.xaml.cs` (Task 15's code-behind; add `using System.Windows;` if not present - code-behind is outside the WPF-free zone). In the constructor, directly after the existing DataContext/ViewModel assignment (Task 15 stores the VM in `_vm`):
```csharp
_vm.ConfirmDeleteRequested += OnConfirmDeleteRequested;
```
  And as a new private method in the same class:
```csharp
/// <summary>Modal confirm per design 3.4; invokes onConfirmed synchronously on Yes, which is
/// exactly the contract SessionsPageViewModel.ConfirmDeleteRequested documents.</summary>
private void OnConfirmDeleteRequested(DeleteConfirmation payload, Action onConfirmed)
{
    string matters = payload.MatterNames.Count == 0 ? "(none)" : string.Join(", ", payload.MatterNames);
    string message = payload.Title + "\n" + payload.DateDisplay + "   " + payload.DurationDisplay
        + "\nMatters: " + matters + "\n\n"
        + "This sends the entire session folder - audio, transcript, and metadata - to the Windows Recycle Bin.";
    if (MessageBox.Show(message, "Delete session?", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
        onConfirmed();
}
```

  Insertion into `src/LocalScribe.App/Pages/SessionsPage.xaml` - in the row-actions panel of the list's item template (Task 15's 3.2 row actions: open read view / reveal / archive), append as the last action button:
```xml
<Button Margin="4,0,0,0" Content="Delete"
        Command="{Binding DataContext.DeleteSessionCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
        CommandParameter="{Binding}"
        ToolTip="Send this session's folder to the Recycle Bin" />
```
  (`AncestorType=ItemsControl` matches whatever ListView/ListBox Task 15 used, since both derive from ItemsControl and its DataContext is the page VM. The command itself no-ops with an explanatory Info for live/pending-recovery rows, satisfying 3.4's refusal requirement even before any per-row IsEnabled styling.)

- [ ] **Run** the full gate and warning check:
```
dotnet build
dotnet test --filter "Category!=Fixture"
```
  Expected: build reports 0 warnings; full solution test run PASS. (The dialog itself is window-layer glue and is exercised by the Stage 4 smoke runbook's delete-to-recycle-bin step, per design section 9.)

- [ ] **Commit:**
```
git add src/LocalScribe.App/Pages/SessionsPage.xaml src/LocalScribe.App/Pages/SessionsPage.xaml.cs
git commit -m "feat(sessions): delete row action + Recycle Bin confirmation dialog (MessageBox, tray-consistent)"
```

---

### Task 18: MattersPageViewModel + UI

**Files:**
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (created in Task 9; append `SaveMatterAsync` inside the class body)
- Create: `src/LocalScribe.App/ViewModels/MattersPageViewModel.cs`
- Create: `src/LocalScribe.App/Pages/MattersPage.xaml` + `src/LocalScribe.App/Pages/MattersPage.xaml.cs`
- Modify: `src/LocalScribe.App/MainWindow.xaml` (created in the MainWindow task; Matters NavigationViewItem wiring)
- Test: `tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs`

**Interfaces:**
- Consumes: `MaintenanceService(StoragePaths, ISettingsService, IRecycleBin, TimeProvider)` with `ListSessionsAsync`, `CascadeMatterAsync`, `RebuildIndexAsync` (Task 9); `MatterDeleter(StoragePaths, IRecycleBin)` with `CountReferencesAsync`/`DeleteAsync` (DeleteAsync recycles the matter folder AND removes the index entry, throws `InvalidOperationException` when referenced - Task 9 group); `MatterIdGenerator.Next(MattersIndex, string, int)`; `ParticipantId.Mint(string, IReadOnlyCollection<string>)`; `IUiErrorReporter`; `ISettingsService`; `Matter.Archived` / `MattersIndexEntry.Archived` and MatterStore v2 whose `SaveAsync` upsert copies `Archived` into the index entry (Task 2); `ManualUtcTimeProvider` (linked from Core.Tests).
- Produces: `MaintenanceService.SaveMatterAsync(Matter matter, CancellationToken ct)` (**Task 9 owners must merge this addition** - see step 2); `public sealed partial class MattersPageViewModel` with ctor `(StoragePaths paths, MaintenanceService maintenance, MatterDeleter deleter, IUiErrorReporter reporter, Action<Action> dispatch, TimeProvider time)`, `Task RefreshAsync()`, `Task SelectAsync(string? matterId)`, `Task RenameMemberAsync(string memberId, string newName)`, `Task RemoveMemberAsync(string memberId)`, `void JumpToSession(string sessionId)`, `event Action<string>? JumpToSessionRequested`, commands `CreateMatterCommand`/`CommitDetailCommand`/`AddMemberCommand`/`DeleteMatterCommand`/`RepairIndexCommand`; `public sealed record TaggedSessionItem(string SessionId, string Title, string DateDisplay)`; `MattersPage(MattersPageViewModel vm)` page (MainWindow task consumes).

Design contract: spec sections 4.1-4.4. Roster edits NEVER touch sessions and NEVER cascade (participants are snapshots); matter Name/Reference changes cascade projections AFTER the save because session.txt resolves matter names live (SessionWriter.cs:38-44).

#### Cycle 1 - MaintenanceService.SaveMatterAsync (serialized on the index lock)

- [ ] Write the failing test. Create `tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MattersPageViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-matters-vm-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakeSettings _settings;
    private readonly FakeBin _bin = new();
    private readonly FakeReporter _reporter = new();
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero));
    private readonly MaintenanceService _maintenance;

    public MattersPageViewModelTests()
    {
        _paths = new StoragePaths(_root);
        _settings = new FakeSettings(new Settings { StorageRoot = _root });
        _maintenance = new MaintenanceService(_paths, _settings, _bin, _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task SaveMatterAsync_persists_matter_and_index_entry_before_returning()
    {
        await _maintenance.SaveMatterAsync(new Matter
        {
            Id = "M-2026-001", Name = "N", Reference = "REF-9", Archived = true,
        }, CancellationToken.None);

        Assert.True(File.Exists(_paths.MatterJson("M-2026-001")));
        var index = await new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None);
        var entry = Assert.Single(index.Matters);
        Assert.Equal("M-2026-001", entry.Id);
        Assert.Equal("REF-9", entry.Reference);
        Assert.True(entry.Archived);   // MatterStore v2 (Task 2) copies Archived into the entry
    }

    [Fact]
    public async Task SaveMatterAsync_serializes_concurrent_index_writes()
    {
        // matters.json upsert is read-modify-write; without the index lock, parallel saves
        // lose entries. All 8 must land.
        var matters = Enumerable.Range(1, 8)
            .Select(i => new Matter { Id = $"M-2026-{i:000}", Name = $"Matter {i}" }).ToList();
        await Task.WhenAll(matters.Select(m => _maintenance.SaveMatterAsync(m, CancellationToken.None)));

        var index = await new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None);
        Assert.Equal(8, index.Matters.Count);
        foreach (var m in matters) Assert.True(File.Exists(_paths.MatterJson(m.Id)));
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        {
            var old = Current;
            Current = updated;
            Changed?.Invoke(old, updated);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBin : IRecycleBin
    {
        public List<string> Recycled { get; } = new();
        public void SendToRecycleBin(string path)
        {
            Recycled.Add(path);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }
}
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` - expected FAIL: `error CS1061: 'MaintenanceService' does not contain a definition for 'SaveMatterAsync'`.
- [ ] Minimal implementation. In `src/LocalScribe.App/Services/MaintenanceService.cs`, append inside the class body (after `RebuildIndexAsync`):

```csharp
    /// <summary>Persists a matter (matter.json + matters.json index upsert) under the same
    /// lock that serializes RebuildIndexAsync/ApplyTagDelta index writes (design 4.3: ALL
    /// index writes serialized). Returns only after the index upsert completed. Added by
    /// Task 18 - Task 9 owners merge: if the class already has an index-serialization
    /// SemaphoreSlim under another name, use that field here instead of adding a second one.</summary>
    public async Task SaveMatterAsync(Matter matter, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try { await new MatterStore(paths.MattersDir).SaveAsync(matter, ct); }
        finally { _indexGate.Release(); }
    }
```

If (and only if) Task 9's class has no index-serialization semaphore yet, also add the field `private readonly SemaphoreSlim _indexGate = new(1, 1);` and route `RebuildIndexAsync`'s index write through it (Task 9 owners reconcile - the behavioral contract is "SaveMatterAsync and RebuildIndexAsync cannot interleave index writes"). Add `using LocalScribe.Core.Model;` / `using LocalScribe.Core.Storage;` if not already present.
- [ ] Run it again: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` - expected PASS (2 tests).
- [ ] Commit: `git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs && git commit -m "feat: MaintenanceService.SaveMatterAsync serialized on the matters index lock"`

#### Cycle 2 - list / ShowArchived / create / select / tagged sessions / jump

- [ ] Write the failing tests. Add to `MattersPageViewModelTests.cs` (inside the class, before the fakes):

```csharp
    private MattersPageViewModel MakeVm()
        => new(_paths, _maintenance, new MatterDeleter(_paths, _bin), _reporter,
               dispatch: a => a(), _time);

    /// <summary>Finalized v3 session folder fixture: session.json + meta.json + one JSONL
    /// segment. Deliberately does NOT render projections - cascade tests use the absence of
    /// session.txt as the no-cascade signal.</summary>
    private async Task WriteFinalizedSessionAsync(string id, IReadOnlyList<string> matterIds)
    {
        var started = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(10), DurationMs = 600_000,
            TimeZoneId = "UTC", UtcOffsetMinutes = 0,
            Model = "small.en", Backend = "cuda", Language = "en",
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "Fixture session", MatterIds = matterIds,
        }, CancellationToken.None);
        await new TranscriptStore(_paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1500, "hello there", "Me"),
            CancellationToken.None);
    }

    [Fact]
    public async Task Create_requires_a_name()
    {
        var vm = MakeVm();
        vm.NewMatterName = "   ";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        Assert.Empty(vm.Matters);
        Assert.Contains(_reporter.Infos,
            m => m.Contains("name is required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_mints_sequential_year_ids_and_stamps_DateCreatedUtc()
    {
        var vm = MakeVm();
        vm.NewMatterName = "First";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        vm.NewMatterName = "Second";
        await vm.CreateMatterCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "M-2026-001", "M-2026-002" },
            vm.Matters.Select(m => m.Id).ToArray());
        var first = await new MatterStore(_paths.MattersDir).LoadAsync("M-2026-001", CancellationToken.None);
        Assert.Equal(_time.GetUtcNow(), first!.DateCreatedUtc);
        Assert.Equal("M-2026-002", vm.SelectedMatterId);   // create selects the new matter
        Assert.True(vm.HasSelection);
        Assert.Equal("Second", vm.EditName);
        Assert.Equal("", vm.NewMatterName);
    }

    [Fact]
    public async Task Archived_matters_collapse_under_ShowArchived_toggle()
    {
        await _maintenance.SaveMatterAsync(new Matter { Id = "M-2026-001", Name = "Visible" }, CancellationToken.None);
        await _maintenance.SaveMatterAsync(new Matter { Id = "M-2026-002", Name = "Hidden", Archived = true }, CancellationToken.None);
        var vm = MakeVm();
        await vm.RefreshAsync();
        Assert.Equal("Visible", Assert.Single(vm.Matters).Name);   // default: archived collapsed
        vm.ShowArchived = true;
        Assert.Equal(2, vm.Matters.Count);
        vm.ShowArchived = false;
        Assert.Equal("Visible", Assert.Single(vm.Matters).Name);
    }

    [Fact]
    public async Task Tagged_sessions_sublist_and_jump_event()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Tagged";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-tagged", new[] { id });
        await WriteFinalizedSessionAsync("s-other", Array.Empty<string>());

        await vm.SelectAsync(id);
        var item = Assert.Single(vm.TaggedSessions);
        Assert.Equal("s-tagged", item.SessionId);
        Assert.Equal("Fixture session", item.Title);
        Assert.Equal("2026-07-01 09:00", item.DateDisplay);   // session-offset date (UTC+0 fixture)

        string? jumped = null;
        vm.JumpToSessionRequested += sid => jumped = sid;
        vm.JumpToSession(item.SessionId);
        Assert.Equal("s-tagged", jumped);
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` - expected FAIL: `error CS0246: The type or namespace name 'MattersPageViewModel' could not be found`.
- [ ] Minimal implementation. Create `src/LocalScribe.App/ViewModels/MattersPageViewModel.cs`:

```csharp
// src/LocalScribe.App/ViewModels/MattersPageViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.ViewModels;

/// <summary>One row of the selected matter's tagged-sessions sublist (design 4.1, the
/// two-level organizer's second level).</summary>
public sealed record TaggedSessionItem(string SessionId, string Title, string DateDisplay);

/// <summary>Matters page: CRUD + roster editor + tagged-sessions organizer (design section 4).
/// WPF-free; every disk mutation routes through MaintenanceService (design 7.3). Roster edits
/// never touch sessions and never cascade - session participants are snapshots (design 4.4).</summary>
public sealed partial class MattersPageViewModel : ObservableObject
{
    private readonly StoragePaths _paths;
    private readonly MaintenanceService _maintenance;
    private readonly MatterDeleter _deleter;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private MattersIndex _index = new();
    private Matter? _loaded;                        // detail truth as last loaded/saved

    public ObservableCollection<MattersIndexEntry> Matters { get; } = new();
    public ObservableCollection<RosterMember> Roster { get; } = new();
    public ObservableCollection<TaggedSessionItem> TaggedSessions { get; } = new();

    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private string _newMatterName = "";
    [ObservableProperty] private string? _selectedMatterId;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editReference = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private bool _editArchived;
    [ObservableProperty] private string _newMemberName = "";
    [ObservableProperty] private string _newMemberRole = "";
    [ObservableProperty] private string _cascadeStatus = "";   // "" = no cascade running

    /// <summary>Raised by JumpToSession; MainWindow navigates to the Sessions page and
    /// selects/opens the session.</summary>
    public event Action<string>? JumpToSessionRequested;

    public IAsyncRelayCommand CreateMatterCommand { get; }

    public MattersPageViewModel(StoragePaths paths, MaintenanceService maintenance,
        MatterDeleter deleter, IUiErrorReporter reporter, Action<Action> dispatch, TimeProvider time)
    {
        (_paths, _maintenance, _deleter, _reporter, _dispatch, _time)
            = (paths, maintenance, deleter, reporter, dispatch, time);
        CreateMatterCommand = new AsyncRelayCommand(CreateMatterAsync);
    }

    partial void OnShowArchivedChanged(bool value) => ApplyFilter();

    public async Task RefreshAsync()
    {
        try
        {
            _index = await new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None);
            ApplyFilter();
        }
        catch (Exception ex) { _reporter.Report("List matters", ex); }
    }

    private void ApplyFilter() => _dispatch(() =>
    {
        Matters.Clear();
        foreach (var e in _index.Matters
                     .Where(e => ShowArchived || !e.Archived)
                     .OrderBy(e => e.Id, StringComparer.Ordinal))
            Matters.Add(e);
    });

    public async Task SelectAsync(string? matterId)
    {
        SelectedMatterId = matterId;
        if (matterId is null) { _loaded = null; HasSelection = false; return; }
        try
        {
            var loaded = await new MatterStore(_paths.MattersDir).LoadAsync(matterId, CancellationToken.None);
            if (loaded is null) { _loaded = null; HasSelection = false; return; }
            _loaded = loaded;
            var sessions = await _maintenance.ListSessionsAsync(CancellationToken.None);
            _dispatch(() =>
            {
                EditName = loaded.Name;
                EditReference = loaded.Reference ?? "";
                EditDescription = loaded.Description ?? "";
                EditArchived = loaded.Archived;
                Roster.Clear();
                foreach (var m in loaded.Roster) Roster.Add(m);
                TaggedSessions.Clear();
                foreach (var s in sessions.Sessions.Where(s => s.Meta.MatterIds.Contains(matterId)))
                    TaggedSessions.Add(new TaggedSessionItem(s.Id, s.Meta.Title, DateDisplay(s.Session)));
                HasSelection = true;
            });
        }
        catch (Exception ex) { _reporter.Report("Open matter", ex); }
    }

    public void JumpToSession(string sessionId) => JumpToSessionRequested?.Invoke(sessionId);

    // Session-offset date, same fallback chain as SessionWriter (machine zone only pre-v3).
    private static string DateDisplay(SessionRecord session)
    {
        var local = session.UtcOffsetMinutes is int offsetMin
            ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : session.StartedAtUtc.ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private async Task CreateMatterAsync()
    {
        try
        {
            string name = NewMatterName.Trim();
            if (name.Length == 0) { _reporter.Info("Matter name is required."); return; }
            var index = await new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None);
            string id = MatterIdGenerator.Next(index, _paths.MattersDir, _time.GetUtcNow().Year);
            var matter = new Matter { Id = id, Name = name, DateCreatedUtc = _time.GetUtcNow() };
            await _maintenance.SaveMatterAsync(matter, CancellationToken.None);
            NewMatterName = "";
            await RefreshAsync();
            await SelectAsync(id);
        }
        catch (Exception ex) { _reporter.Report("Create matter", ex); }
    }
}
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` - expected PASS (6 tests).
- [ ] Commit: `git add src/LocalScribe.App/ViewModels/MattersPageViewModel.cs tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs && git commit -m "feat: matters page VM - list, show-archived filter, create, select, organizer sublist"`

#### Cycle 3 - detail auto-save + cascade rules (spec 4.4)

- [ ] Write the failing tests. Add to `MattersPageViewModelTests.cs`:

```csharp
    [Fact]
    public async Task Commit_name_change_cascades_projections_after_save()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Old Name";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-cascade", new[] { id });
        Assert.False(File.Exists(_paths.SessionTxt("s-cascade")));   // pre-condition: no render yet

        await vm.SelectAsync(id);
        vm.EditName = "New Name";
        await vm.CommitDetailCommand.ExecuteAsync(null);

        Assert.Empty(_reporter.Errors);
        var reloaded = await new MatterStore(_paths.MattersDir).LoadAsync(id, CancellationToken.None);
        Assert.Equal("New Name", reloaded!.Name);                    // truth saved
        string sessionTxt = await File.ReadAllTextAsync(_paths.SessionTxt("s-cascade"));
        Assert.Contains("New Name", sessionTxt);                     // cascade re-rendered live name
        Assert.Equal("", vm.CascadeStatus);                          // status cleared when done
    }

    [Fact]
    public async Task Commit_reference_change_cascades_too()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Refd";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-ref-cascade", new[] { id });

        await vm.SelectAsync(id);
        vm.EditReference = "REF-42";
        await vm.CommitDetailCommand.ExecuteAsync(null);

        Assert.Empty(_reporter.Errors);
        string sessionTxt = await File.ReadAllTextAsync(_paths.SessionTxt("s-ref-cascade"));
        Assert.Contains("Refd (REF-42)", sessionTxt);
    }

    [Fact]
    public async Task Description_and_archived_commits_save_but_do_not_cascade()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Quiet";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-quiet", new[] { id });

        await vm.SelectAsync(id);
        vm.EditDescription = "Background notes";
        await vm.CommitDetailCommand.ExecuteAsync(null);
        vm.EditArchived = true;
        await vm.CommitDetailCommand.ExecuteAsync(null);

        Assert.Empty(_reporter.Errors);
        Assert.False(File.Exists(_paths.SessionTxt("s-quiet")));     // a cascade would have created it
        var reloaded = await new MatterStore(_paths.MattersDir).LoadAsync(id, CancellationToken.None);
        Assert.Equal("Background notes", reloaded!.Description);
        Assert.True(reloaded.Archived);
    }

    [Fact]
    public async Task Commit_with_blank_name_is_rejected_and_reverted()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Keep Me";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;

        await vm.SelectAsync(id);
        vm.EditName = "  ";
        await vm.CommitDetailCommand.ExecuteAsync(null);

        Assert.Contains(_reporter.Infos, m => m.Contains("name is required", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Keep Me", vm.EditName);                        // reverted to loaded truth
        var reloaded = await new MatterStore(_paths.MattersDir).LoadAsync(id, CancellationToken.None);
        Assert.Equal("Keep Me", reloaded!.Name);
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` - expected FAIL: `error CS1061: 'MattersPageViewModel' does not contain a definition for 'CommitDetailCommand'`.
- [ ] Minimal implementation. In `MattersPageViewModel.cs`, add below `public IAsyncRelayCommand CreateMatterCommand { get; }`:

```csharp
    public IAsyncRelayCommand CommitDetailCommand { get; }
```

In the constructor, after `CreateMatterCommand = new AsyncRelayCommand(CreateMatterAsync);`, add:

```csharp
        CommitDetailCommand = new AsyncRelayCommand(CommitDetailAsync);
```

After the `CreateMatterAsync` method, add:

```csharp
    private async Task CommitDetailAsync()
    {
        if (_loaded is null) return;
        try
        {
            string name = EditName.Trim();
            if (name.Length == 0)
            {
                _reporter.Info("Matter name is required.");
                EditName = _loaded.Name;                             // revert to loaded truth
                return;
            }
            string? reference = string.IsNullOrWhiteSpace(EditReference) ? null : EditReference.Trim();
            string? description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription;
            // Matter names/references resolve LIVE in session.txt renders (SessionWriter),
            // so those two fields - and only those - trigger the projection cascade AFTER
            // the save (design 4.4). Description/Archived are matter-local.
            bool cascade = name != _loaded.Name || reference != _loaded.Reference;
            bool changed = cascade || description != _loaded.Description || EditArchived != _loaded.Archived;
            if (!changed) return;                                    // auto-save fires on every commit; no-op when clean

            var updated = _loaded with
            {
                Name = name, Reference = reference, Description = description, Archived = EditArchived,
            };
            await _maintenance.SaveMatterAsync(updated, CancellationToken.None);
            _loaded = updated;
            await RefreshAsync();
            if (cascade)
            {
                _dispatch(() => CascadeStatus = "Re-rendering tagged sessions...");
                var progress = new InlineProgress(n => _dispatch(() => CascadeStatus =
                    string.Create(CultureInfo.InvariantCulture, $"Re-rendering tagged sessions... {n} done")));
                await _maintenance.CascadeMatterAsync(updated.Id, progress, CancellationToken.None);
                _dispatch(() => CascadeStatus = "");
            }
        }
        catch (Exception ex) { _reporter.Report("Save matter", ex); }
    }

    /// <summary>Synchronous IProgress: Progress&lt;T&gt; posts to a captured SyncContext, which
    /// is nondeterministic headless - this reports inline and marshals via the dispatch seam.</summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` - expected PASS (10 tests).
- [ ] Commit: `git add -A && git commit -m "feat: matter detail auto-save on commit; name/reference cascade after save (spec 4.4)"`

#### Cycle 4 - roster editor (never touches sessions, never cascades)

- [ ] Write the failing tests. Add to `MattersPageViewModelTests.cs`:

```csharp
    [Fact]
    public async Task Roster_add_mints_participant_ids_with_collision_suffix()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Roster";
        await vm.CreateMatterCommand.ExecuteAsync(null);

        vm.NewMemberName = "Jane Doe";
        vm.NewMemberRole = "Counsel";
        await vm.AddMemberCommand.ExecuteAsync(null);
        vm.NewMemberName = "Jane Doe";                               // same name -> "-2" suffix
        await vm.AddMemberCommand.ExecuteAsync(null);

        Assert.Empty(_reporter.Errors);
        Assert.Equal(new[] { "p-jane-doe", "p-jane-doe-2" }, vm.Roster.Select(m => m.Id).ToArray());
        Assert.Equal("Counsel", vm.Roster[0].Role);
        Assert.Equal("", vm.NewMemberName);                          // input cleared after add
        var reloaded = await new MatterStore(_paths.MattersDir)
            .LoadAsync(vm.SelectedMatterId!, CancellationToken.None);
        Assert.Equal(2, reloaded!.Roster.Count);                     // persisted through maintenance save
    }

    [Fact]
    public async Task Roster_edits_never_cascade_and_never_touch_sessions()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Estate of Doe";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-roster", new[] { id });
        byte[] metaBefore = await File.ReadAllBytesAsync(_paths.MetaJson("s-roster"));

        await vm.SelectAsync(id);
        vm.NewMemberName = "Jane Doe";
        await vm.AddMemberCommand.ExecuteAsync(null);
        await vm.RenameMemberAsync("p-jane-doe", "Jane Q. Doe");
        Assert.Equal("Jane Q. Doe", Assert.Single(vm.Roster).Name);
        await vm.RemoveMemberAsync("p-jane-doe");
        Assert.Empty(vm.Roster);

        Assert.Empty(_reporter.Errors);
        // No cascade: session.txt was never rendered, and roster edits must not render it.
        Assert.False(File.Exists(_paths.SessionTxt("s-roster")));
        // Session truth untouched: participants are snapshot copies (design 4.4).
        Assert.Equal(metaBefore, await File.ReadAllBytesAsync(_paths.MetaJson("s-roster")));
        var reloaded = await new MatterStore(_paths.MattersDir).LoadAsync(id, CancellationToken.None);
        Assert.Empty(reloaded!.Roster);
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` - expected FAIL: `error CS1061 ... 'AddMemberCommand'`.
- [ ] Minimal implementation. In `MattersPageViewModel.cs`, add below `CommitDetailCommand`:

```csharp
    public IAsyncRelayCommand AddMemberCommand { get; }
```

In the constructor, after the `CommitDetailCommand` line:

```csharp
        AddMemberCommand = new AsyncRelayCommand(AddMemberAsync);
```

After `CommitDetailAsync` (before `InlineProgress`), add:

```csharp
    private async Task AddMemberAsync()
    {
        if (_loaded is null) return;
        try
        {
            string name = NewMemberName.Trim();
            if (name.Length == 0) { _reporter.Info("Member name is required."); return; }
            string? role = string.IsNullOrWhiteSpace(NewMemberRole) ? null : NewMemberRole.Trim();
            string id = ParticipantId.Mint(name, _loaded.Roster.Select(m => m.Id).ToList());
            var roster = _loaded.Roster.Append(new RosterMember { Id = id, Name = name, Role = role }).ToList();
            await SaveRosterAsync(_loaded with { Roster = roster });
            _dispatch(() => { NewMemberName = ""; NewMemberRole = ""; });
        }
        catch (Exception ex) { _reporter.Report("Add roster member", ex); }
    }

    public async Task RenameMemberAsync(string memberId, string newName)
    {
        if (_loaded is null) return;
        try
        {
            string name = newName.Trim();
            if (name.Length == 0) { _reporter.Info("Member name is required."); return; }
            if (_loaded.Roster.FirstOrDefault(m => m.Id == memberId) is not { } member || member.Name == name)
                return;                                              // unknown id or unchanged: no write
            var roster = _loaded.Roster.Select(m => m.Id == memberId ? m with { Name = name } : m).ToList();
            await SaveRosterAsync(_loaded with { Roster = roster });
        }
        catch (Exception ex) { _reporter.Report("Rename roster member", ex); }
    }

    public async Task RemoveMemberAsync(string memberId)
    {
        if (_loaded is null) return;
        try
        {
            var roster = _loaded.Roster.Where(m => m.Id != memberId).ToList();
            if (roster.Count == _loaded.Roster.Count) return;
            await SaveRosterAsync(_loaded with { Roster = roster });
        }
        catch (Exception ex) { _reporter.Report("Remove roster member", ex); }
    }

    /// <summary>Roster saves go through the same serialized maintenance save but deliberately
    /// trigger NO cascade (design 4.4): session participants are snapshot copies and no
    /// projection ever reads the roster - renames only change future picks.</summary>
    private async Task SaveRosterAsync(Matter updated)
    {
        await _maintenance.SaveMatterAsync(updated, CancellationToken.None);
        _loaded = updated;
        _dispatch(() =>
        {
            Roster.Clear();
            foreach (var m in updated.Roster) Roster.Add(m);
        });
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` - expected PASS (12 tests).
- [ ] Commit: `git add -A && git commit -m "feat: matter roster editor - add/rename/remove; snapshot rule: no cascade, sessions untouched"`

#### Cycle 5 - delete (Recycle Bin, blocked-while-referenced) + repair index

- [ ] Write the failing tests. Add to `MattersPageViewModelTests.cs`:

```csharp
    [Fact]
    public async Task Delete_blocked_while_sessions_reference_the_matter()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Referenced";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;
        await WriteFinalizedSessionAsync("s-refd", new[] { id });

        await vm.SelectAsync(id);
        await vm.DeleteMatterCommand.ExecuteAsync(null);

        Assert.Contains(_reporter.Infos, m => m.Contains("1 session") && m.Contains("Archive"));
        Assert.True(File.Exists(_paths.MatterJson(id)));             // nothing deleted
        Assert.Empty(_bin.Recycled);
        Assert.Empty(_reporter.Errors);                              // blocked is Info, not an error
    }

    [Fact]
    public async Task Delete_empty_matter_recycles_folder_and_refreshes()
    {
        var vm = MakeVm();
        vm.NewMatterName = "Empty";
        await vm.CreateMatterCommand.ExecuteAsync(null);
        string id = Assert.Single(vm.Matters).Id;

        await vm.SelectAsync(id);
        await vm.DeleteMatterCommand.ExecuteAsync(null);

        Assert.Empty(_reporter.Errors);
        Assert.Contains(Path.Combine(_paths.MattersDir, id), _bin.Recycled);
        Assert.Empty(vm.Matters);                                    // index entry gone + list refreshed
        Assert.False(vm.HasSelection);
    }

    [Fact]
    public async Task Repair_index_adopts_orphan_matter_folders()
    {
        // matter.json on disk, no index entry (the documented crash window; MatterStore.cs:5-7).
        await new MatterStore(_paths.MattersDir).SaveAsync(
            new Matter { Id = "M-2026-009", Name = "Orphan" }, CancellationToken.None);
        File.Delete(_paths.MattersIndexJson);

        var vm = MakeVm();
        await vm.RefreshAsync();
        Assert.Empty(vm.Matters);
        await vm.RepairIndexCommand.ExecuteAsync(null);
        Assert.Contains(vm.Matters, m => m.Id == "M-2026-009" && m.Name == "Orphan");
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` - expected FAIL: `error CS1061 ... 'DeleteMatterCommand'`.
- [ ] Minimal implementation. In `MattersPageViewModel.cs`, add below `AddMemberCommand`:

```csharp
    public IAsyncRelayCommand DeleteMatterCommand { get; }
    public IAsyncRelayCommand RepairIndexCommand { get; }
```

In the constructor, after the `AddMemberCommand` line:

```csharp
        DeleteMatterCommand = new AsyncRelayCommand(DeleteMatterAsync);
        RepairIndexCommand = new AsyncRelayCommand(RepairIndexAsync);
```

After `RemoveMemberAsync`, add:

```csharp
    private async Task DeleteMatterAsync()
    {
        if (_loaded is null) return;
        string id = _loaded.Id;
        try
        {
            // Organizational-data deletion only (design 4.1): blocked-while-referenced
            // guarantees no session content references the matter, so the evidentiary
            // invariant (whole-session delete as the only session-data deletion) holds.
            int count = await _deleter.CountReferencesAsync(id, CancellationToken.None);
            if (count > 0)
            {
                _reporter.Info(string.Create(CultureInfo.InvariantCulture,
                    $"Cannot delete this matter: {count} session(s) are tagged to it. Archive it instead."));
                return;
            }
            await _deleter.DeleteAsync(id, CancellationToken.None);
            await SelectAsync(null);
            await RefreshAsync();
        }
        catch (Exception ex) { _reporter.Report("Delete matter", ex); }
    }

    private async Task RepairIndexAsync()
    {
        try
        {
            await _maintenance.RebuildIndexAsync(CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception ex) { _reporter.Report("Repair matters index", ex); }
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~MattersPageViewModelTests"` - expected PASS (15 tests).
- [ ] Commit: `git add -A && git commit -m "feat: matter delete (recycle bin, blocked-while-referenced) + repair-index command"`

#### Cycle 6 - Matters page UI (humble XAML shell)

- [ ] Create `src/LocalScribe.App/Pages/MattersPage.xaml`:

```xml
<Page x:Class="LocalScribe.App.Pages.MattersPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Title="Matters">
    <Page.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
    </Page.Resources>
    <Grid Margin="12">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="320" />
            <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>

        <DockPanel Grid.Column="0" Margin="0,0,12,0">
            <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
                <TextBox Width="180" Margin="0,0,8,0"
                         Text="{Binding NewMatterName, UpdateSourceTrigger=PropertyChanged}" />
                <Button Content="New matter" Click="OnCreateMatter" />
            </StackPanel>
            <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
                <CheckBox Content="Show archived" IsChecked="{Binding ShowArchived}" Margin="0,0,12,0" />
                <Button Content="Repair index" Click="OnRepairIndex" />
            </StackPanel>
            <ListView x:Name="MatterList" ItemsSource="{Binding Matters}"
                      SelectionChanged="OnMatterSelected"
                      VirtualizingPanel.IsVirtualizing="True"
                      VirtualizingPanel.VirtualizationMode="Recycling">
                <ListView.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Margin="0,2">
                            <StackPanel Orientation="Horizontal">
                                <TextBlock FontWeight="SemiBold" Text="{Binding Name, Mode=OneWay}" Margin="0,0,8,0" />
                                <TextBlock Text="archived" FontSize="11" Foreground="Orange" VerticalAlignment="Center"
                                           Visibility="{Binding Archived, Converter={StaticResource BoolToVis}}" />
                            </StackPanel>
                            <TextBlock FontSize="11" Opacity="0.7">
                                <Run Text="{Binding Id, Mode=OneWay}" />
                                <Run Text="{Binding Reference, Mode=OneWay}" />
                                <Run Text="{Binding SessionCount, Mode=OneWay, StringFormat='{}{0} session(s)'}" />
                            </TextBlock>
                        </StackPanel>
                    </DataTemplate>
                </ListView.ItemTemplate>
            </ListView>
        </DockPanel>

        <ScrollViewer Grid.Column="1"
                      Visibility="{Binding HasSelection, Converter={StaticResource BoolToVis}}">
            <StackPanel>
                <TextBlock Text="Name" FontWeight="SemiBold" />
                <TextBox Text="{Binding EditName, UpdateSourceTrigger=PropertyChanged}"
                         LostFocus="OnDetailCommit" Margin="0,0,0,8" />
                <TextBlock Text="Reference" FontWeight="SemiBold" />
                <TextBox Text="{Binding EditReference, UpdateSourceTrigger=PropertyChanged}"
                         LostFocus="OnDetailCommit" Margin="0,0,0,8" />
                <TextBlock Text="Description" FontWeight="SemiBold" />
                <TextBox Text="{Binding EditDescription, UpdateSourceTrigger=PropertyChanged}"
                         LostFocus="OnDetailCommit" AcceptsReturn="True" TextWrapping="Wrap"
                         MinHeight="60" Margin="0,0,0,8" />
                <CheckBox Content="Archived" IsChecked="{Binding EditArchived}"
                          Click="OnDetailCommit" Margin="0,0,0,8" />
                <TextBlock Text="{Binding CascadeStatus}" Foreground="Orange" Margin="0,0,0,8" />

                <TextBlock Text="Roster" FontWeight="SemiBold" Margin="0,8,0,4" />
                <ItemsControl ItemsSource="{Binding Roster}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <StackPanel Orientation="Horizontal" Margin="0,2">
                                <TextBox Width="180" Text="{Binding Name, Mode=OneWay}" Tag="{Binding Id}"
                                         LostFocus="OnMemberRename" Margin="0,0,8,0" />
                                <TextBlock Text="{Binding Role, Mode=OneWay}" VerticalAlignment="Center" Margin="0,0,8,0" />
                                <Button Content="Remove" Tag="{Binding Id}" Click="OnMemberRemove" />
                            </StackPanel>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
                <StackPanel Orientation="Horizontal" Margin="0,4,0,8">
                    <TextBox Width="140" Text="{Binding NewMemberName, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,8,0" />
                    <TextBox Width="100" Text="{Binding NewMemberRole, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,8,0" />
                    <Button Content="Add member" Click="OnAddMember" />
                </StackPanel>

                <TextBlock Text="Tagged sessions" FontWeight="SemiBold" Margin="0,8,0,4" />
                <ItemsControl ItemsSource="{Binding TaggedSessions}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <StackPanel Orientation="Horizontal" Margin="0,2">
                                <TextBlock Text="{Binding Title, Mode=OneWay}" VerticalAlignment="Center" Margin="0,0,8,0" />
                                <TextBlock Text="{Binding DateDisplay, Mode=OneWay}" Opacity="0.7"
                                           VerticalAlignment="Center" Margin="0,0,8,0" />
                                <Button Content="Open" Tag="{Binding SessionId}" Click="OnJumpToSession" />
                            </StackPanel>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>

                <Button Content="Delete matter" Click="OnDeleteMatter"
                        HorizontalAlignment="Left" Margin="0,16,0,0" />
            </StackPanel>
        </ScrollViewer>
    </Grid>
</Page>
```

- [ ] Create `src/LocalScribe.App/Pages/MattersPage.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
namespace LocalScribe.App.Pages;

/// <summary>Humble shell over MattersPageViewModel: routes control events to VM commands.
/// The single delete confirmation dialog (design 4.1) is the only view-side decision here;
/// the referenced-block itself is VM logic via MatterDeleter.</summary>
public partial class MattersPage : Page
{
    private readonly MattersPageViewModel _vm;

    public MattersPage(MattersPageViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.RefreshAsync();          // deterministic refresh on navigation (design 3.1)
    }

    private void OnCreateMatter(object sender, RoutedEventArgs e) => _vm.CreateMatterCommand.Execute(null);
    private void OnRepairIndex(object sender, RoutedEventArgs e) => _vm.RepairIndexCommand.Execute(null);
    private void OnDetailCommit(object sender, RoutedEventArgs e) => _vm.CommitDetailCommand.Execute(null);
    private void OnAddMember(object sender, RoutedEventArgs e) => _vm.AddMemberCommand.Execute(null);

    private async void OnMatterSelected(object sender, SelectionChangedEventArgs e)
        => await _vm.SelectAsync((MatterList.SelectedItem as MattersIndexEntry)?.Id);

    private async void OnMemberRename(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.Tag is string memberId)
            await _vm.RenameMemberAsync(memberId, box.Text);
    }

    private async void OnMemberRemove(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string memberId)
            await _vm.RemoveMemberAsync(memberId);
    }

    private void OnJumpToSession(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string sessionId)
            _vm.JumpToSession(sessionId);
    }

    private void OnDeleteMatter(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Delete this matter? Its folder goes to the Recycle Bin. Sessions are never deleted by this action.",
            "Delete matter", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes) _vm.DeleteMatterCommand.Execute(null);
    }
}
```

- [ ] Wire into MainWindow (merge point with the MainWindow task). In `src/LocalScribe.App/MainWindow.xaml`, the NavigationView's Matters item must be `<ui:NavigationViewItem Content="Matters" TargetPageType="{x:Type pages:MattersPage}" />` (with `xmlns:pages="clr-namespace:LocalScribe.App.Pages"`); in the MainWindow composition, construct `new MattersPage(mattersPageViewModel)` with a `MattersPageViewModel` built from the CompositionRoot's `StoragePaths`, `MaintenanceService`, `new MatterDeleter(paths, recycleBin)`, `IUiErrorReporter`, the dispatcher `Action<Action>`, and `TimeProvider.System`; subscribe `vm.JumpToSessionRequested += id => <navigate to Sessions page and select id>` using the Sessions page's existing selection API from its task. If the MainWindow task already registered a Matters placeholder page, replace it with this page.
- [ ] Build gate: `dotnet build` - expected: Build succeeded, 0 Warning(s).
- [ ] Full gate: `dotnet test --filter "Category!=Fixture"` - expected: all green.
- [ ] Commit: `git add -A && git commit -m "feat: matters page UI - master list, detail/roster editors, organizer, delete confirm"`

---

### Task 19: ReadViewWindow + ReadViewViewModel

**Files:**
- Modify: `src/LocalScribe.App/Services/WindowRegistry.cs` (created in Task 9 group; append `OpenCount` inside the class body)
- Create: `src/LocalScribe.App/ViewModels/ReadViewPlacement.cs`
- Create: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`
- Create: `src/LocalScribe.App/ReadViewWindow.xaml` + `src/LocalScribe.App/ReadViewWindow.xaml.cs`
- Test: `tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs`

**Interfaces:**
- Consumes: `MaintenanceService.RunForSessionAsync<T>(string, Func<CancellationToken, Task<T>>, CancellationToken)` (Task 9); `WindowRegistry` (Task 9 group); `WindowStateStore.Load(string key)` / `Save(string key, WindowPlacement)` and `WindowPlacement(double X, double Y, double? Width, double? Height)` (Task 11); `ISettingsService`; `IUiErrorReporter`; `Settings.Privacy.ExcludeWindowsFromCapture` (Task 1/2); `NativeWindowInterop.ExcludeFromCapture(Window)` (existing; Task 13 owns the immediate-reapply plumbing for already-open windows - at merge, if Task 13 exposed a shared apply helper, prefer it over the direct call in this window); existing Core: `SessionStore`, `MetadataStore`, `TranscriptStore`, `SpeakersStore`, `EditStore`, `MatterStore`, `TranscriptProjection`, `VocabularyProvider`, `PhantomBleedDedup`, `NameResolver` (via projection), `DisplayRow`, `TimestampFormat`, `Markers.DegradedSystemAudioLoopback` (note: Markers lives at `src/LocalScribe.Core/Model/Markers.cs`), `SessionMeta.CreateDefault`.
- Produces: `WindowRegistry.OpenCount` (**Task 9-group owners must merge this additive member**); `public static class ReadViewPlacement { public const double CascadeOffsetPx = 24; public static (double X, double Y, double? Width, double? Height) Next(WindowPlacement? saved, int alreadyOpenCount, double windowWidth, double windowHeight, double vx, double vy, double vw, double vh); }`; `public sealed partial class ReadViewViewModel` with ctor `(MaintenanceService maintenance, StoragePaths paths, ISettingsService settings, IUiErrorReporter reporter, Action<Action> dispatch, TimeProvider time)` (Task 20 extends this ctor - see Task 20), `Task LoadAsync(string sessionId, CancellationToken ct)`, `ObservableCollection<DisplayRow> Rows`, header/badge properties listed below; `ReadViewWindow(ReadViewViewModel vm, string sessionId, WindowRegistry registry, WindowStateStore stateStore, ISettingsService settings)` (the Sessions page task's "open read view" action consumes this ctor, after checking `registry.IsOpen(sessionId)` and activating instead of duplicating).

Design contract: spec section 5. The body renders DisplayRows from the canonical TranscriptProjection - the same pipeline as the file renders. Known deliberate divergence: the 3b live view renders raw merger lines with NO projection pass, so a read view can legitimately differ from what was seen live - tests must not assume live-view parity. Read-only: no edit affordances anywhere.

#### Cycle 1 - WindowRegistry.OpenCount + ReadViewPlacement (pure placement math)

- [ ] Write the failing tests. Create `tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ReadViewViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-vm-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakeSettings _settings;
    private readonly FakeReporter _reporter = new();
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly MaintenanceService _maintenance;

    public ReadViewViewModelTests()
    {
        _paths = new StoragePaths(_root);
        _settings = new FakeSettings(new Settings { StorageRoot = _root });
        _maintenance = new MaintenanceService(_paths, _settings, new FakeBin(), _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void WindowRegistry_OpenCount_tracks_register_and_unregister()
    {
        var reg = new WindowRegistry();
        Assert.Equal(0, reg.OpenCount);
        reg.Register("a", () => { });
        reg.Register("b", () => { });
        Assert.Equal(2, reg.OpenCount);
        reg.Unregister("a");
        Assert.Equal(1, reg.OpenCount);
        reg.Unregister("b");
        Assert.Equal(0, reg.OpenCount);
    }

    [Fact]
    public void Placement_cascades_24px_per_open_view_and_carries_saved_size()
    {
        var p = ReadViewPlacement.Next(new WindowPlacement(100, 80, 800, 600), alreadyOpenCount: 2,
            windowWidth: 800, windowHeight: 600, vx: 0, vy: 0, vw: 1920, vh: 1080);
        Assert.Equal(148, p.X);
        Assert.Equal(128, p.Y);
        Assert.Equal(800, p.Width);
        Assert.Equal(600, p.Height);
    }

    [Fact]
    public void Placement_without_saved_state_uses_clamp_fallback_then_cascades()
    {
        var first = ReadViewPlacement.Next(null, 0, 720, 560, 0, 0, 1920, 1080);
        Assert.Equal(1920 - 720 - 16, first.X);                      // ScreenClamp fallback: top-right
        Assert.Equal(16, first.Y);
        Assert.Null(first.Width);

        var second = ReadViewPlacement.Next(null, 1, 720, 560, 0, 0, 1920, 1080);
        Assert.Equal(1200, second.X);                                // fallback + 24, clamped to vw - w
        Assert.Equal(40, second.Y);
    }

    [Fact]
    public void Placement_clamps_offscreen_saved_positions()
    {
        var p = ReadViewPlacement.Next(new WindowPlacement(5000, -900), 0, 720, 560, 0, 0, 1920, 1080);
        Assert.Equal(1200, p.X);                                     // 1920 - 720
        Assert.Equal(0, p.Y);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        {
            var old = Current;
            Current = updated;
            Changed?.Invoke(old, updated);
            return Task.CompletedTask;
        }
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
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }
}
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"` - expected FAIL: `error CS1061: 'WindowRegistry' does not contain a definition for 'OpenCount'` (and CS0246 for ReadViewPlacement).
- [ ] Minimal implementation, part 1. In `src/LocalScribe.App/Services/WindowRegistry.cs`, append inside the class body:

```csharp
    /// <summary>Total registered open windows across all sessions. Drives the read-view
    /// +24px cascade and the "last read view closed writes readViewDefault" rule (design
    /// section 2). Added by Task 19 - Task 9-group owners merge: the body assumes the
    /// canonical Dictionary&lt;string, List&lt;Action&gt;&gt; _closers guarded by _gate; keep
    /// your existing field names when merging.</summary>
    public int OpenCount
    {
        get { lock (_gate) { return _closers.Values.Sum(list => list.Count); } }
    }
```

- [ ] Minimal implementation, part 2. Create `src/LocalScribe.App/ViewModels/ReadViewPlacement.cs`:

```csharp
// src/LocalScribe.App/ViewModels/ReadViewPlacement.cs
namespace LocalScribe.App.ViewModels;

/// <summary>Pure placement math for read-view windows (design section 2): the remembered
/// "readViewDefault" placement, cascaded +24px per already-open read view, clamped to the
/// virtual screen via the existing ScreenClamp. WPF-free and unit-testable; the window
/// supplies the virtual-screen metrics.</summary>
public static class ReadViewPlacement
{
    public const double CascadeOffsetPx = 24;

    public static (double X, double Y, double? Width, double? Height) Next(
        WindowPlacement? saved, int alreadyOpenCount, double windowWidth, double windowHeight,
        double vx, double vy, double vw, double vh)
    {
        double baseX, baseY;
        if (saved is null)
            (baseX, baseY) = ScreenClamp.Clamp(double.NaN, double.NaN,
                windowWidth, windowHeight, vx, vy, vw, vh);          // fallback: top-right with margin
        else
            (baseX, baseY) = (saved.X, saved.Y);

        double offset = CascadeOffsetPx * alreadyOpenCount;
        var (x, y) = ScreenClamp.Clamp(baseX + offset, baseY + offset,
            windowWidth, windowHeight, vx, vy, vw, vh);
        return (x, y, saved?.Width, saved?.Height);
    }
}
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"` - expected PASS (4 tests).
- [ ] Commit: `git add -A && git commit -m "feat: WindowRegistry.OpenCount + read-view cascade placement math"`

#### Cycle 2 - ReadViewViewModel: projection-parity load

- [ ] Write the failing test. Add to `ReadViewViewModelTests.cs` (inside the class, before the fakes):

```csharp
    private ReadViewViewModel MakeVm()
        => new(_maintenance, _paths, _settings, _reporter, dispatch: a => a(), _time);

    /// <summary>Finalized v3 Webex session at UTC+8 with: a tagged matter carrying a
    /// vocabulary correction ("acme" -> "ACME Corp"), two consecutive Local segments (the
    /// second corrected via the EditStore overlay, which also flips meta.Edited), one Remote
    /// segment, and the degraded-system-audio marker. RetainedAudioSources set for Task 20.</summary>
    private async Task WriteFixtureSessionAsync(string id)
    {
        await new MatterStore(_paths.MattersDir).SaveAsync(new Matter
        {
            Id = "M-2026-001", Name = "Acme Litigation", Reference = "REF-7",
            Vocabulary = new Vocabulary
            {
                Corrections = new Dictionary<string, string> { ["acme"] = "ACME Corp" },
            },
        }, CancellationToken.None);

        var started = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(10), DurationMs = 600_000,
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            Model = "small.en", Backend = "cuda", Language = "en",
            RetainedAudioSources = new[] { SourceKind.Local, SourceKind.Remote },
            Devices = new DeviceSnapshot
            {
                Remote = new RemoteSnapshot { Mode = RemoteMode.PerProcess, FellBackToSystemMix = true },
            },
        }, CancellationToken.None);

        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "Client call", MatterIds = new[] { "M-2026-001" },
            Participants = new[]
            {
                new SessionParticipant { Id = "p-self", Name = "Sam", Side = SourceKind.Local, IsSelf = true },
                new SessionParticipant { Id = "p-jane-doe", Name = "Jane", Side = SourceKind.Remote },
            },
        }, CancellationToken.None);

        var transcript = new TranscriptStore(_paths.TranscriptJsonl(id));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1500,
            "we spoke to acme this morning", "Me"), CancellationToken.None);
        await transcript.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 1600, 3000,
            "the orignal words", "Me"), CancellationToken.None);
        await transcript.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Remote, 3200, 4200,
            "sounds good", "Them"), CancellationToken.None);
        await transcript.AppendAsync(TranscriptLine.Marker(3, 4200,
            Markers.DegradedSystemAudioLoopback), CancellationToken.None);

        // Non-destructive correction overlay for seq 1 (also flips meta.Edited).
        await new EditStore(_paths.SessionDir(id), _time)
            .ApplyTextCorrectionAsync(1, "the corrected words", CancellationToken.None);
    }

    [Fact]
    public async Task Load_builds_projection_rows_matching_the_file_renders()
    {
        await WriteFixtureSessionAsync("read-1");
        var vm = MakeVm();
        await vm.LoadAsync("read-1", CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        Assert.True(vm.IsLoaded);

        // Grouping: two consecutive Local "Sam" segments merge into one row; then Jane; then marker.
        Assert.Equal(3, vm.Rows.Count);
        var samRow = vm.Rows[0];
        Assert.False(samRow.IsMarker);
        Assert.Equal("Sam", samRow.DisplayName);                     // declared single Local participant
        Assert.Contains("ACME Corp", samRow.Text);                   // matter vocabulary applied
        Assert.Contains("the corrected words", samRow.Text);         // edits overlay wins verbatim
        Assert.DoesNotContain("orignal", samRow.Text);
        Assert.Equal("Jane", vm.Rows[1].DisplayName);
        Assert.True(vm.Rows[2].IsMarker);
        Assert.Equal(Markers.DegradedSystemAudioLoopback, vm.Rows[2].Text);

        // Parity proof: the FILE render produced by SessionWriter shows the same projected text.
        await new SessionWriter(_paths, _settings.Current, _time)
            .RegenerateProjectionsAsync("read-1", CancellationToken.None);
        string fileRender = await File.ReadAllTextAsync(_paths.TranscriptTxt("read-1"));
        Assert.Contains("ACME Corp", fileRender);
        Assert.Contains("the corrected words", fileRender);
        Assert.DoesNotContain("orignal", fileRender);
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"` - expected FAIL: `error CS0246: The type or namespace name 'ReadViewViewModel' could not be found`.
- [ ] Minimal implementation. Create `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`:

```csharp
// src/LocalScribe.App/ViewModels/ReadViewViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.App.ViewModels;

/// <summary>Read-only session view (design section 5). Rows come from the canonical
/// TranscriptProjection - the same pipeline as transcript.md/.txt and session.txt. The load
/// pipeline mirrors SessionWriter.RegenerateProjectionsAsync (load order, meta fallback,
/// vocabulary provider construction) so what the window shows is what the files say. Known
/// deliberate divergence: the 3b live view renders raw merger lines with no projection pass,
/// so this view may differ from what was seen live. WPF-free; all reads run inside the
/// maintenance per-session queue so a load cannot interleave with recovery or a cascade.</summary>
public sealed partial class ReadViewViewModel : ObservableObject
{
    private readonly MaintenanceService _maintenance;
    private readonly StoragePaths _paths;
    private readonly ISettingsService _settings;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;

    [ObservableProperty] private bool _isLoaded;

    public ObservableCollection<DisplayRow> Rows { get; } = new();
    public string SessionId { get; private set; } = "";
    public string TimestampsMode { get; private set; } = "relative";   // read by the window's stamp converter
    public DateTimeOffset StartedAtLocal { get; private set; }

    public ReadViewViewModel(MaintenanceService maintenance, StoragePaths paths,
        ISettingsService settings, IUiErrorReporter reporter, Action<Action> dispatch, TimeProvider time)
        => (_maintenance, _paths, _settings, _reporter, _dispatch, _time)
            = (maintenance, paths, settings, reporter, dispatch, time);

    private sealed record LoadedView(SessionRecord Session, SessionMeta Meta,
        IReadOnlyList<string> MatterDisplays, IReadOnlyList<DisplayRow> Rows,
        bool HasDegraded, DateTimeOffset StartedLocal);

    public async Task LoadAsync(string sessionId, CancellationToken ct)
    {
        SessionId = sessionId;
        try
        {
            var settings = _settings.Current;
            var view = await _maintenance.RunForSessionAsync(sessionId, async token =>
            {
                // Mirrors SessionWriter.RegenerateProjectionsAsync exactly: load order, the
                // session-offset local time, the CreateDefault meta fallback (self: null),
                // matter resolution, and the VocabularyProvider construction.
                var session = await new SessionStore(_paths.SessionJson(sessionId)).ReadAsync(token)
                              ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
                var startedLocal = session.UtcOffsetMinutes is int offsetMin
                    ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
                    : session.StartedAtUtc.ToLocalTime();
                var meta = await new MetadataStore(_paths.MetaJson(sessionId)).LoadAsync(token)
                           ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
                var lines = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(token);
                var speakers = await new SpeakersStore(_paths.SpeakersJson(sessionId)).LoadAsync(token);
                var edits = await new EditStore(_paths.SessionDir(sessionId), _time).LoadAsync(token);

                var matterStore = new MatterStore(_paths.MattersDir);
                var mattersById = new Dictionary<string, Matter>();
                var matterDisplays = new List<string>();
                foreach (string mid in meta.MatterIds)
                {
                    var m = await matterStore.LoadAsync(mid, token);
                    if (m is null) { matterDisplays.Add(mid); continue; }
                    mattersById[mid] = m;
                    matterDisplays.Add(string.IsNullOrEmpty(m.Reference) ? m.Name : $"{m.Name} ({m.Reference})");
                }

                var projection = new TranscriptProjection(
                    new VocabularyProvider(settings.Vocabulary, mattersById), new PhantomBleedDedup());
                var rows = projection.Build(lines, speakers, edits, meta);

                // Mid-session degradation exists only as a transcript marker (design 3.2/5) -
                // the list badge cannot see it, so the read view surfaces it.
                bool degraded = lines.Any(l =>
                    l.Kind == TranscriptKind.Marker && l.Text == Markers.DegradedSystemAudioLoopback);

                return new LoadedView(session, meta, matterDisplays, rows, degraded, startedLocal);
            }, ct);

            _dispatch(() => Apply(view, settings));
        }
        catch (Exception ex) { _reporter.Report("Open read view", ex); }
    }

    private void Apply(LoadedView view, Settings settings)
    {
        TimestampsMode = settings.Timestamps;
        StartedAtLocal = view.StartedLocal;
        Rows.Clear();
        foreach (var r in view.Rows) Rows.Add(r);
        IsLoaded = true;
    }
}
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"` - expected PASS (5 tests).
- [ ] Commit: `git add -A && git commit -m "feat: ReadViewViewModel - projection-parity load through the maintenance session queue"`

#### Cycle 3 - header, badges, degraded-marker notice, fallbacks, error path

- [ ] Write the failing tests. Add to `ReadViewViewModelTests.cs`:

```csharp
    [Fact]
    public async Task Header_badges_and_footer_come_from_session_truth()
    {
        await WriteFixtureSessionAsync("read-2");
        var vm = MakeVm();
        await vm.LoadAsync("read-2", CancellationToken.None);

        Assert.Equal("Client call", vm.Title);
        Assert.Equal("2026-07-01 17:00", vm.DateDisplay);            // 09:00Z at the session's UTC+8
        Assert.Equal("10:00", vm.DurationDisplay);
        Assert.Equal("Acme Litigation (REF-7)", Assert.Single(vm.MatterDisplays));
        Assert.Contains("Sam (Local)", vm.ParticipantDisplays);
        Assert.Contains("Jane (Remote)", vm.ParticipantDisplays);
        Assert.False(vm.Recovered);
        Assert.True(vm.Edited);                                      // EditStore flipped meta.Edited
        Assert.True(vm.SystemMix);                                   // FellBackToSystemMix in fixture
        Assert.True(vm.HasDegradedMarker);                           // marker text equals the constant
        Assert.Equal("small.en \u00B7 cuda", vm.ModelBackendFooter);
        Assert.Equal("relative", vm.TimestampsMode);
    }

    [Fact]
    public async Task SystemMix_badge_also_true_for_explicitly_chosen_systemMix()
    {
        await WriteFixtureSessionAsync("read-mix");
        var store = new SessionStore(_paths.SessionJson("read-mix"));
        var session = await store.ReadAsync(CancellationToken.None);
        await store.SaveAsync(session! with
        {
            Devices = new DeviceSnapshot
            {
                Remote = new RemoteSnapshot { Mode = RemoteMode.SystemMix, FellBackToSystemMix = false },
            },
        }, CancellationToken.None);

        var vm = MakeVm();
        await vm.LoadAsync("read-mix", CancellationToken.None);
        Assert.True(vm.SystemMix);                                   // chosen == fallback for the badge (design 3.2)
    }

    [Fact]
    public async Task Missing_meta_falls_back_to_CreateDefault_like_SessionWriter()
    {
        await WriteFixtureSessionAsync("read-3");
        File.Delete(_paths.MetaJson("read-3"));

        var vm = MakeVm();
        await vm.LoadAsync("read-3", CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        Assert.Equal("Webex \u2014 2026-07-01 17:00", vm.Title);     // CreateDefault at session-local time
        Assert.False(vm.Edited);
    }

    [Fact]
    public async Task Missing_session_reports_and_stays_unloaded()
    {
        var vm = MakeVm();
        await vm.LoadAsync("nope", CancellationToken.None);
        Assert.False(vm.IsLoaded);
        var (context, ex) = Assert.Single(_reporter.Errors);
        Assert.Equal("Open read view", context);
        Assert.IsType<InvalidOperationException>(ex);
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"` - expected FAIL: `error CS1061 ... 'Title'`.
- [ ] Minimal implementation. In `ReadViewViewModel.cs`, add below `[ObservableProperty] private bool _isLoaded;`:

```csharp
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _dateDisplay = "";
    [ObservableProperty] private string _durationDisplay = "";
    [ObservableProperty] private bool _recovered;
    [ObservableProperty] private bool _edited;
    [ObservableProperty] private bool _systemMix;
    [ObservableProperty] private bool _hasDegradedMarker;
    [ObservableProperty] private string _modelBackendFooter = "";
```

Below `public ObservableCollection<DisplayRow> Rows { get; } = new();` add:

```csharp
    public ObservableCollection<string> MatterDisplays { get; } = new();
    public ObservableCollection<string> ParticipantDisplays { get; } = new();
```

Replace the `Apply` method with:

```csharp
    private void Apply(LoadedView view, Settings settings)
    {
        Title = view.Meta.Title;
        DateDisplay = view.StartedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var span = TimeSpan.FromMilliseconds(view.Session.DurationMs);
        DurationDisplay = span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss",
            CultureInfo.InvariantCulture);
        Recovered = view.Session.Recovered;
        Edited = view.Meta.Edited;
        // Same rule as the Task 15 list badge: chosen systemMix has identical bleed
        // characteristics to a fallback (design 3.2).
        SystemMix = view.Session.Devices.Remote.Mode == RemoteMode.SystemMix
                    || view.Session.Devices.Remote.FellBackToSystemMix;
        HasDegradedMarker = view.HasDegraded;
        ModelBackendFooter = $"{view.Session.Model} \u00B7 {view.Session.Backend}";   // middle dot
        TimestampsMode = settings.Timestamps;
        StartedAtLocal = view.StartedLocal;
        MatterDisplays.Clear();
        foreach (string m in view.MatterDisplays) MatterDisplays.Add(m);
        ParticipantDisplays.Clear();
        foreach (var p in view.Meta.Participants)
            ParticipantDisplays.Add(string.IsNullOrEmpty(p.Role)
                ? $"{p.Name} ({p.Side})" : $"{p.Name} ({p.Role}, {p.Side})");        // SessionWriter's format
        Rows.Clear();
        foreach (var r in view.Rows) Rows.Add(r);
        IsLoaded = true;
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"` - expected PASS (9 tests).
- [ ] Commit: `git add -A && git commit -m "feat: read view header, badges, degraded-marker notice, meta fallback, error surfacing"`

#### Cycle 4 - ReadViewWindow (capture-excluded, cascaded placement, registry lifecycle)

- [ ] Create `src/LocalScribe.App/ReadViewWindow.xaml`:

```xml
<ui:FluentWindow x:Class="LocalScribe.App.ReadViewWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        xmlns:local="clr-namespace:LocalScribe.App"
        Title="{Binding Title}" Height="560" Width="720"
        WindowBackdropType="Mica" ExtendsContentIntoTitleBar="False">
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
        <local:ReadViewStampConverter x:Key="Stamp" />
    </Window.Resources>
    <!-- Read-only by design (corrections are Stage 6): every element below is a display
         control; there are deliberately NO edit affordances anywhere in this window. -->
    <DockPanel Margin="12">
        <StackPanel DockPanel.Dock="Top" Margin="0,0,0,8">
            <TextBlock Text="{Binding Title}" FontSize="16" FontWeight="SemiBold" />
            <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                <TextBlock Text="{Binding DateDisplay}" Margin="0,0,12,0" />
                <TextBlock Text="{Binding DurationDisplay}" Margin="0,0,12,0" />
                <TextBlock Text="Recovered" Foreground="Orange" Margin="0,0,8,0"
                           ToolTip="Recovered after an interruption; duration is transcript-derived, not wall-clock"
                           Visibility="{Binding Recovered, Converter={StaticResource BoolToVis}}" />
                <TextBlock Text="Edited" Foreground="Orange" Margin="0,0,8,0"
                           Visibility="{Binding Edited, Converter={StaticResource BoolToVis}}" />
                <TextBlock Text="System mix" Foreground="Orange" Margin="0,0,8,0"
                           ToolTip="Remote audio came from the system-wide mix; other apps' audio may be present"
                           Visibility="{Binding SystemMix, Converter={StaticResource BoolToVis}}" />
            </StackPanel>
            <ItemsControl ItemsSource="{Binding MatterDisplays}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding Mode=OneWay}" Opacity="0.8" Margin="0,0,8,0" />
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <ItemsControl ItemsSource="{Binding ParticipantDisplays}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding Mode=OneWay}" Opacity="0.8" Margin="0,0,8,0" />
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <TextBlock Text="System-audio mix was active for part of this session (degraded capture marker present)."
                       Foreground="Orange" TextWrapping="Wrap" Margin="0,4,0,0"
                       Visibility="{Binding HasDegradedMarker, Converter={StaticResource BoolToVis}}" />
        </StackPanel>
        <TextBlock DockPanel.Dock="Bottom" Text="{Binding ModelBackendFooter}" Opacity="0.6" Margin="0,8,0,0" />
        <ListView x:Name="RowList" ItemsSource="{Binding Rows}"
                  VirtualizingPanel.IsVirtualizing="True"
                  VirtualizingPanel.VirtualizationMode="Recycling"
                  ScrollViewer.CanContentScroll="True">
            <ListView.ItemTemplate>
                <DataTemplate>
                    <TextBlock TextWrapping="Wrap">
                        <TextBlock.Style>
                            <Style TargetType="TextBlock">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding IsMarker}" Value="True">
                                        <Setter Property="FontStyle" Value="Italic" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </TextBlock.Style>
                        <Run Text="{Binding StartMs, Mode=OneWay, Converter={StaticResource Stamp}, StringFormat='[{0}]'}" FontWeight="SemiBold" />
                        <Run Text="{Binding DisplayName, Mode=OneWay, StringFormat='{}{0}:'}" FontWeight="SemiBold" />
                        <Run Text="{Binding Text, Mode=OneWay}" />
                    </TextBlock>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </DockPanel>
</ui:FluentWindow>
```

- [ ] Create `src/LocalScribe.App/ReadViewWindow.xaml.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Projection;
namespace LocalScribe.App;

/// <summary>Formats DisplayRow.StartMs per the settings snapshot the VM loaded with, using
/// the canonical TimestampFormat (same stamps as the file renders). The window assigns Vm
/// before rows render (LoadAsync completes before Rows populate).</summary>
public sealed class ReadViewStampConverter : IValueConverter
{
    public ReadViewViewModel? Vm { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Vm is not null && value is long ms
            ? TimestampFormat.Stamp(ms, Vm.TimestampsMode, Vm.StartedAtLocal)
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>One instance per opened session (design section 2/5). Genuinely closes (nothing
/// depends on it - unlike the live view's hide-on-close). Registered in WindowRegistry so
/// session delete can close read views first and release audio handles. Capture-excluded by
/// default per settings.Privacy (design section 2; Task 13 owns live re-apply for open
/// windows). Placement: "readViewDefault" written by the LAST closed read view; new windows
/// cascade +24px per already-open read view, screen-clamped.</summary>
public partial class ReadViewWindow
{
    private readonly ReadViewViewModel _vm;
    private readonly string _sessionId;
    private readonly WindowRegistry _registry;
    private readonly WindowStateStore _stateStore;
    private readonly ISettingsService _settings;
    private readonly int _openAtCreation;

    public ReadViewWindow(ReadViewViewModel vm, string sessionId, WindowRegistry registry,
        WindowStateStore stateStore, ISettingsService settings)
    {
        InitializeComponent();
        (_vm, _sessionId, _registry, _stateStore, _settings) = (vm, sessionId, registry, stateStore, settings);
        DataContext = vm;
        ((ReadViewStampConverter)Resources["Stamp"]).Vm = vm;
        _openAtCreation = registry.OpenCount;                        // count BEFORE registering this window
        registry.Register(sessionId, Close);
        Loaded += async (_, _) => await _vm.LoadAsync(_sessionId, CancellationToken.None);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_settings.Current.Privacy.ExcludeWindowsFromCapture)
            NativeWindowInterop.ExcludeFromCapture(this);

        var saved = _stateStore.Load("readViewDefault");
        var p = ReadViewPlacement.Next(saved, _openAtCreation, Width, Height,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        (Left, Top) = (p.X, p.Y);
        if (p.Width is double w) Width = w;
        if (p.Height is double h) Height = h;
    }

    protected override void OnClosed(EventArgs e)
    {
        _registry.Unregister(_sessionId);
        if (_registry.OpenCount == 0)                                // last closed read view writes the default
            _stateStore.Save("readViewDefault", new WindowPlacement(Left, Top, Width, Height));
        base.OnClosed(e);
    }
}
```

- [ ] Build gate: `dotnet build` - expected: Build succeeded, 0 Warning(s).
- [ ] Full gate: `dotnet test --filter "Category!=Fixture"` - expected: all green. Window behavior itself (capture exclusion inside a real Webex share, cascade placement on a real desktop, registry-driven close on delete) is GUI-only: it goes on the Stage 4 smoke runbook, not unit tests.
- [ ] Commit: `git add -A && git commit -m "feat: ReadViewWindow - capture-excluded, cascaded keyed placement, registry lifecycle"`

---

### Task 20: Audio playback

**Files:**
- Create: `src/LocalScribe.App/Services/IDualAudioPlayer.cs`
- Create: `src/LocalScribe.App/ViewModels/PlaybackViewModel.cs`
- Create: `src/LocalScribe.App/DualMediaPlayer.cs` (class `MediaPlayerDualAudioPlayer`)
- Modify: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs` (ctor + Apply; from Task 19)
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml` + `.xaml.cs` (transport bar, 150 ms tick; from Task 19)
- Test: `tests/LocalScribe.App.Tests/PlaybackViewModelTests.cs`; Modify: `tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs`

**Interfaces:**
- Consumes: `StoragePaths.AudioFile(string, SourceKind, AudioFormat)` (existing); `SessionRecord.RetainedAudioSources`; `Settings.AudioFormat`; Task 19's `ReadViewViewModel`/`ReadViewWindow`.
- Produces (exact locked shape): `public interface IDualAudioPlayer : IDisposable { void Load(string? localPath, string? remotePath); void Play(); void Pause(); void SeekMs(long ms); void SetLegMuted(bool local, bool muted); long PositionMs { get; } long DurationMs { get; } event Action? MediaReady; event Action? MediaEnded; }`; `public sealed partial class PlaybackViewModel : ObservableObject, IDisposable` with ctor `(IDualAudioPlayer player, Action<Action> dispatch)`, `void Resolve(StoragePaths paths, string sessionId, IReadOnlyList<SourceKind> retained, AudioFormat preferredFormat)`, `void Tick()`, `void Seek(long ms)`, `IRelayCommand PlayPauseCommand`, observables `IsAvailable`/`IsPlaying`/`PositionMs`/`DurationMs`/`PositionDisplay`/`DurationDisplay`/`LocalMuted`/`RemoteMuted`, `bool HasLocalLeg`/`HasRemoteLeg`; `MediaPlayerDualAudioPlayer : IDualAudioPlayer`; final `ReadViewViewModel` ctor `(MaintenanceService, StoragePaths, ISettingsService, IUiErrorReporter, IDualAudioPlayer, Action<Action>, TimeProvider)` with `PlaybackViewModel Playback { get; }` (CompositionRoot/Sessions-page open path passes `new MediaPlayerDualAudioPlayer()`).

Design contract: spec section 5 audio playback - both legs play together as a pair; per-leg mute isolates a side; missing legs degrade to whichever file exists; no audio at all hides the transport. The VM stays WPF-free behind the IDualAudioPlayer seam; the MediaPlayer implementation is deliberately thin and untestable headless.

#### Cycle 1 - IDualAudioPlayer seam + PlaybackViewModel (scripted-fake tests)

- [ ] Write the failing tests. Create `tests/LocalScribe.App.Tests/PlaybackViewModelTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class PlaybackViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-playback-vm-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakePlayer _player = new();

    public PlaybackViewModelTests() => _paths = new StoragePaths(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private PlaybackViewModel MakeVm() => new(_player, dispatch: a => a());

    private void WriteAudio(string sessionId, SourceKind kind, AudioFormat format)
    {
        Directory.CreateDirectory(_paths.SessionDir(sessionId));
        File.WriteAllBytes(_paths.AudioFile(sessionId, kind, format), new byte[] { 1 });
    }

    [Fact]
    public void Resolve_probes_disk_per_leg_and_prefers_the_settings_format()
    {
        // Local exists in the preferred format; remote predates a format change (wav only).
        WriteAudio("s-audio", SourceKind.Local, AudioFormat.Flac);
        WriteAudio("s-audio", SourceKind.Remote, AudioFormat.Wav);

        var vm = MakeVm();
        vm.Resolve(_paths, "s-audio", new[] { SourceKind.Local, SourceKind.Remote }, AudioFormat.Flac);

        Assert.True(vm.IsAvailable);
        Assert.True(vm.HasLocalLeg);
        Assert.True(vm.HasRemoteLeg);
        Assert.Equal(_paths.AudioFile("s-audio", SourceKind.Local, AudioFormat.Flac), _player.LoadedLocal);
        Assert.Equal(_paths.AudioFile("s-audio", SourceKind.Remote, AudioFormat.Wav), _player.LoadedRemote);
        Assert.True(vm.PlayPauseCommand.CanExecute(null));
    }

    [Fact]
    public void Resolve_with_one_retained_leg_degrades_to_that_leg()
    {
        WriteAudio("s-one", SourceKind.Remote, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-one", new[] { SourceKind.Remote }, AudioFormat.Flac);

        Assert.True(vm.IsAvailable);
        Assert.False(vm.HasLocalLeg);
        Assert.True(vm.HasRemoteLeg);
        Assert.Null(_player.LoadedLocal);
    }

    [Fact]
    public void Resolve_without_any_files_hides_the_transport()
    {
        // Retention "never" / files gone: retained list says Local but nothing is on disk.
        var vm = MakeVm();
        vm.Resolve(_paths, "s-none", new[] { SourceKind.Local }, AudioFormat.Flac);

        Assert.False(vm.IsAvailable);
        Assert.False(_player.LoadCalled);                            // never load a missing file
        Assert.False(vm.PlayPauseCommand.CanExecute(null));
    }

    [Fact]
    public void PlayPause_toggles_and_drives_both_legs_via_the_player()
    {
        WriteAudio("s-pp", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-pp", new[] { SourceKind.Local }, AudioFormat.Flac);

        vm.PlayPauseCommand.Execute(null);
        Assert.True(vm.IsPlaying);
        Assert.Contains("Play", _player.Calls);
        vm.PlayPauseCommand.Execute(null);
        Assert.False(vm.IsPlaying);
        Assert.Contains("Pause", _player.Calls);
    }

    [Fact]
    public void MediaReady_publishes_duration_and_MediaEnded_stops()
    {
        WriteAudio("s-dur", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-dur", new[] { SourceKind.Local }, AudioFormat.Flac);

        _player.DurationMs = 65_000;
        _player.RaiseReady();
        Assert.Equal(65_000, vm.DurationMs);
        Assert.Equal("01:05", vm.DurationDisplay);                   // mm:ss under an hour

        vm.PlayPauseCommand.Execute(null);
        _player.RaiseEnded();
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public void Long_durations_render_h_mm_ss()
    {
        WriteAudio("s-long", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-long", new[] { SourceKind.Local }, AudioFormat.Flac);

        _player.DurationMs = 3_665_000;                              // 1:01:05
        _player.RaiseReady();
        Assert.Equal("1:01:05", vm.DurationDisplay);
        _player.PositionMs = 3_600_000;
        vm.Tick();
        Assert.Equal("1:00:00", vm.PositionDisplay);
    }

    [Fact]
    public void Tick_polls_position_and_Seek_forwards_to_the_player()
    {
        WriteAudio("s-seek", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-seek", new[] { SourceKind.Local }, AudioFormat.Flac);

        _player.PositionMs = 42_000;
        vm.Tick();                                                   // 150 ms timer pattern; tests call directly
        Assert.Equal(42_000, vm.PositionMs);
        Assert.Equal("00:42", vm.PositionDisplay);

        vm.Seek(90_000);
        Assert.Contains("Seek:90000", _player.Calls);
        Assert.Equal(90_000, vm.PositionMs);
    }

    [Fact]
    public void Per_leg_mute_toggles_route_to_the_right_leg()
    {
        WriteAudio("s-mute", SourceKind.Local, AudioFormat.Flac);
        WriteAudio("s-mute", SourceKind.Remote, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-mute", new[] { SourceKind.Local, SourceKind.Remote }, AudioFormat.Flac);

        vm.LocalMuted = true;
        Assert.Contains("Mute:local:True", _player.Calls);
        vm.RemoteMuted = true;
        Assert.Contains("Mute:remote:True", _player.Calls);
        vm.LocalMuted = false;
        Assert.Contains("Mute:local:False", _player.Calls);
    }

    [Fact]
    public void Dispose_disposes_the_player()
    {
        var vm = MakeVm();
        vm.Dispose();
        Assert.Contains("Dispose", _player.Calls);
    }

    private sealed class FakePlayer : IDualAudioPlayer
    {
        public string? LoadedLocal, LoadedRemote;
        public bool LoadCalled;
        public List<string> Calls { get; } = new();
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public event Action? MediaReady;
        public event Action? MediaEnded;

        public void Load(string? localPath, string? remotePath)
        {
            LoadCalled = true;
            (LoadedLocal, LoadedRemote) = (localPath, remotePath);
            Calls.Add("Load");
        }

        public void Play() => Calls.Add("Play");
        public void Pause() => Calls.Add("Pause");
        public void SeekMs(long ms) { PositionMs = ms; Calls.Add($"Seek:{ms}"); }
        public void SetLegMuted(bool local, bool muted) => Calls.Add($"Mute:{(local ? "local" : "remote")}:{muted}");
        public void Dispose() => Calls.Add("Dispose");
        public void RaiseReady() => MediaReady?.Invoke();
        public void RaiseEnded() => MediaEnded?.Invoke();
    }
}
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~PlaybackViewModelTests"` - expected FAIL: `error CS0246: The type or namespace name 'IDualAudioPlayer' could not be found`.
- [ ] Minimal implementation, part 1. Create `src/LocalScribe.App/Services/IDualAudioPlayer.cs` (WPF-free seam - exact locked shape):

```csharp
// src/LocalScribe.App/Services/IDualAudioPlayer.cs
namespace LocalScribe.App.Services;

/// <summary>Dual-leg audio transport seam (design section 5): local + remote legs play
/// together as a pair so the user hears the conversation, not one side. Keeps
/// PlaybackViewModel WPF-free; the production implementation wraps two
/// System.Windows.Media.MediaPlayer instances (DualMediaPlayer.cs), tests script a fake.</summary>
public interface IDualAudioPlayer : IDisposable
{
    void Load(string? localPath, string? remotePath);
    void Play();
    void Pause();
    void SeekMs(long ms);
    void SetLegMuted(bool local, bool muted);
    long PositionMs { get; }
    long DurationMs { get; }
    event Action? MediaReady;
    event Action? MediaEnded;
}
```

- [ ] Minimal implementation, part 2. Create `src/LocalScribe.App/ViewModels/PlaybackViewModel.cs`:

```csharp
// src/LocalScribe.App/ViewModels/PlaybackViewModel.cs
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.ViewModels;

/// <summary>Transport state for the read view (design section 5). WPF-free. Leg resolution:
/// the session's RetainedAudioSources says which legs to look for, the settings AudioFormat
/// is only a preference - the actual on-disk file decides (both flac and wav are probed per
/// leg, because sessions may predate a format change). No legs on disk -> IsAvailable=false
/// and the window hides the transport. Position is polled by the window's ~150 ms
/// DispatcherTimer via Tick(); tests call Tick() directly (same pattern as SessionViewModel).</summary>
public sealed partial class PlaybackViewModel : ObservableObject, IDisposable
{
    private readonly IDualAudioPlayer _player;
    private readonly Action<Action> _dispatch;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private long _positionMs;
    [ObservableProperty] private long _durationMs;
    [ObservableProperty] private string _positionDisplay = "00:00";
    [ObservableProperty] private string _durationDisplay = "00:00";
    [ObservableProperty] private bool _localMuted;
    [ObservableProperty] private bool _remoteMuted;
    [ObservableProperty] private bool _hasLocalLeg;
    [ObservableProperty] private bool _hasRemoteLeg;

    public IRelayCommand PlayPauseCommand { get; }

    public PlaybackViewModel(IDualAudioPlayer player, Action<Action> dispatch)
    {
        (_player, _dispatch) = (player, dispatch);
        PlayPauseCommand = new RelayCommand(PlayPause, () => IsAvailable);
        player.MediaReady += () => _dispatch(() =>
        {
            DurationMs = player.DurationMs;
            DurationDisplay = Format(DurationMs);
        });
        player.MediaEnded += () => _dispatch(() =>
        {
            IsPlaying = false;
            Tick();
        });
    }

    public void Resolve(StoragePaths paths, string sessionId,
        IReadOnlyList<SourceKind> retained, AudioFormat preferredFormat)
    {
        string? Probe(SourceKind kind)
        {
            if (!retained.Contains(kind)) return null;
            string preferred = paths.AudioFile(sessionId, kind, preferredFormat);
            if (File.Exists(preferred)) return preferred;
            var other = preferredFormat == AudioFormat.Flac ? AudioFormat.Wav : AudioFormat.Flac;
            string alternate = paths.AudioFile(sessionId, kind, other);
            return File.Exists(alternate) ? alternate : null;
        }

        string? local = Probe(SourceKind.Local);
        string? remote = Probe(SourceKind.Remote);
        HasLocalLeg = local is not null;
        HasRemoteLeg = remote is not null;
        IsAvailable = local is not null || remote is not null;
        PlayPauseCommand.NotifyCanExecuteChanged();
        if (IsAvailable) _player.Load(local, remote);
    }

    /// <summary>Driven by the window's ~150 ms DispatcherTimer; tests call it directly.</summary>
    public void Tick()
    {
        PositionMs = _player.PositionMs;
        PositionDisplay = Format(PositionMs);
    }

    public void Seek(long ms)
    {
        _player.SeekMs(ms);
        Tick();
    }

    private void PlayPause()
    {
        if (IsPlaying) { _player.Pause(); IsPlaying = false; }
        else { _player.Play(); IsPlaying = true; }
    }

    partial void OnLocalMutedChanged(bool value) => _player.SetLegMuted(local: true, muted: value);
    partial void OnRemoteMutedChanged(bool value) => _player.SetLegMuted(local: false, muted: value);

    private static string Format(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    }

    public void Dispose() => _player.Dispose();
}
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~PlaybackViewModelTests"` - expected PASS (9 tests).
- [ ] Commit: `git add -A && git commit -m "feat: IDualAudioPlayer seam + PlaybackViewModel - disk-probed legs, transport, per-leg mute"`

#### Cycle 2 - wire playback into ReadViewViewModel

- [ ] Write the failing tests. Add to `tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Load_resolves_playback_legs_from_retained_sources()
    {
        await WriteFixtureSessionAsync("read-audio");                // RetainedAudioSources = Local+Remote
        File.WriteAllBytes(_paths.AudioFile("read-audio", SourceKind.Local, AudioFormat.Flac), new byte[] { 1 });
        File.WriteAllBytes(_paths.AudioFile("read-audio", SourceKind.Remote, AudioFormat.Wav), new byte[] { 1 });

        var vm = MakeVm();
        await vm.LoadAsync("read-audio", CancellationToken.None);

        Assert.True(vm.Playback.IsAvailable);
        Assert.Equal(_paths.AudioFile("read-audio", SourceKind.Local, AudioFormat.Flac), _player.LoadedLocal);
        Assert.Equal(_paths.AudioFile("read-audio", SourceKind.Remote, AudioFormat.Wav), _player.LoadedRemote);
    }

    [Fact]
    public async Task Load_without_audio_files_hides_the_transport()
    {
        await WriteFixtureSessionAsync("read-noaudio");              // retained says both, disk has neither
        var vm = MakeVm();
        await vm.LoadAsync("read-noaudio", CancellationToken.None);

        Assert.True(vm.IsLoaded);
        Assert.False(vm.Playback.IsAvailable);
        Assert.False(_player.LoadCalled);
    }
```

Then make the harness compile against the new ctor: in `ReadViewViewModelTests`, add the field

```csharp
    private readonly FakePlayer _player = new();
```

replace `MakeVm` with

```csharp
    private ReadViewViewModel MakeVm()
        => new(_maintenance, _paths, _settings, _reporter, _player, dispatch: a => a(), _time);
```

and add this nested fake next to the other fakes at the bottom of the class:

```csharp
    private sealed class FakePlayer : IDualAudioPlayer
    {
        public string? LoadedLocal, LoadedRemote;
        public bool LoadCalled;
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public event Action? MediaReady;
        public event Action? MediaEnded;
        public void Load(string? localPath, string? remotePath)
        {
            LoadCalled = true;
            (LoadedLocal, LoadedRemote) = (localPath, remotePath);
        }
        public void Play() { }
        public void Pause() { }
        public void SeekMs(long ms) => PositionMs = ms;
        public void SetLegMuted(bool local, bool muted) { }
        public void Dispose() { }
        public void RaiseReady() => MediaReady?.Invoke();
        public void RaiseEnded() => MediaEnded?.Invoke();
    }
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"` - expected FAIL: `error CS1729: 'ReadViewViewModel' does not contain a constructor that takes 7 arguments`.
- [ ] Minimal implementation. In `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`:
  - Change the class declaration to `public sealed partial class ReadViewViewModel : ObservableObject, IDisposable`.
  - Add below `public DateTimeOffset StartedAtLocal { get; private set; }`:

```csharp
    /// <summary>Dual-leg audio transport (design section 5). Created eagerly so window
    /// bindings are stable; IsAvailable stays false until LoadAsync resolves real files.</summary>
    public PlaybackViewModel Playback { get; }
```

  - Replace the constructor with:

```csharp
    public ReadViewViewModel(MaintenanceService maintenance, StoragePaths paths,
        ISettingsService settings, IUiErrorReporter reporter, IDualAudioPlayer player,
        Action<Action> dispatch, TimeProvider time)
    {
        (_maintenance, _paths, _settings, _reporter, _dispatch, _time)
            = (maintenance, paths, settings, reporter, dispatch, time);
        Playback = new PlaybackViewModel(player, dispatch);
    }
```

  - In `Apply`, insert immediately before `IsLoaded = true;`:

```csharp
        Playback.Resolve(_paths, SessionId, view.Session.RetainedAudioSources, settings.AudioFormat);
```

  - Add at the end of the class:

```csharp
    public void Dispose() => Playback.Dispose();
```

- [ ] Run it: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests"` - expected PASS (11 tests). Also run `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~PlaybackViewModelTests"` - still PASS.
- [ ] Commit: `git add -A && git commit -m "feat: read view resolves dual-leg playback from retained sources at load"`

#### Cycle 3 - MediaPlayerDualAudioPlayer + transport bar UI

MediaPlayerDualAudioPlayer is deliberately THIN and is not unit-testable headless: System.Windows.Media.MediaPlayer needs the Windows media stack, real decodable files, and a pumping dispatcher for MediaOpened/MediaEnded. It is verified by the Stage 4 smoke runbook (read view + dual-leg audio over the 5 real Webex sessions; per-leg mute; FLAC via the OS Media Foundation decoder), not by tests.

- [ ] Create `src/LocalScribe.App/DualMediaPlayer.cs`:

```csharp
// src/LocalScribe.App/DualMediaPlayer.cs
using System.Windows.Media;
using LocalScribe.App.Services;
namespace LocalScribe.App;

/// <summary>IDualAudioPlayer over two System.Windows.Media.MediaPlayer instances (design
/// section 5): Play/Pause/Position are mirrored on both so the two legs stay a pair; the
/// second player is engaged only when both legs exist. Minor drift on very long sessions is
/// accepted for v1. FLAC/WAV decode via Media Foundation (Windows 10+ decodes FLAC natively).
/// Deliberately thin and NOT unit-tested: MediaPlayer requires the OS media stack, real
/// files, and a pumping dispatcher - verified via the Stage 4 smoke runbook instead.
/// Construct on the UI thread (MediaPlayer is dispatcher-affine).</summary>
public sealed class MediaPlayerDualAudioPlayer : IDualAudioPlayer
{
    private readonly MediaPlayer _localPlayer = new();
    private readonly MediaPlayer _remotePlayer = new();
    private bool _hasLocal, _hasRemote;

    public event Action? MediaReady;
    public event Action? MediaEnded;

    // The first existing leg is the primary: it drives duration, position, and transport events.
    private MediaPlayer Primary => _hasLocal ? _localPlayer : _remotePlayer;

    public void Load(string? localPath, string? remotePath)
    {
        _hasLocal = localPath is not null;
        _hasRemote = remotePath is not null;
        if (!_hasLocal && !_hasRemote) return;                       // VM never calls Load like this; belt-and-braces

        Primary.MediaOpened += (_, _) => MediaReady?.Invoke();
        Primary.MediaEnded += (_, _) => MediaEnded?.Invoke();
        if (_hasLocal) _localPlayer.Open(new Uri(localPath!));
        if (_hasRemote) _remotePlayer.Open(new Uri(remotePath!));
    }

    public void Play()
    {
        if (_hasLocal) _localPlayer.Play();
        if (_hasRemote) _remotePlayer.Play();
    }

    public void Pause()
    {
        if (_hasLocal) _localPlayer.Pause();
        if (_hasRemote) _remotePlayer.Pause();
    }

    public void SeekMs(long ms)
    {
        var position = TimeSpan.FromMilliseconds(ms);
        if (_hasLocal) _localPlayer.Position = position;             // seeked as a pair (design section 5)
        if (_hasRemote) _remotePlayer.Position = position;
    }

    public void SetLegMuted(bool local, bool muted)
        => (local ? _localPlayer : _remotePlayer).IsMuted = muted;

    public long PositionMs => (long)Primary.Position.TotalMilliseconds;

    public long DurationMs => Primary.NaturalDuration.HasTimeSpan
        ? (long)Primary.NaturalDuration.TimeSpan.TotalMilliseconds
        : 0;

    public void Dispose()
    {
        _localPlayer.Close();
        _remotePlayer.Close();
    }
}
```

- [ ] Add the transport bar. In `src/LocalScribe.App/ReadViewWindow.xaml`, insert directly BEFORE the `<TextBlock DockPanel.Dock="Bottom" Text="{Binding ModelBackendFooter}" ...` line (docked panels stack bottom-up, so the footer stays lowest):

```xml
        <Border DockPanel.Dock="Bottom" Margin="0,8,0,0"
                Visibility="{Binding Playback.IsAvailable, Converter={StaticResource BoolToVis}}">
            <StackPanel Orientation="Horizontal">
                <Button x:Name="PlayPauseButton" Content="Play" Click="OnPlayPause" Margin="0,0,8,0" />
                <TextBlock Text="{Binding Playback.PositionDisplay}" VerticalAlignment="Center" Margin="0,0,4,0" />
                <Slider x:Name="SeekSlider" Width="240" Minimum="0" VerticalAlignment="Center" Margin="0,0,4,0"
                        Thumb.DragStarted="OnSeekDragStarted" Thumb.DragCompleted="OnSeekDragCompleted" />
                <TextBlock Text="{Binding Playback.DurationDisplay}" VerticalAlignment="Center" Margin="0,0,12,0" />
                <ToggleButton Content="Mute local" IsChecked="{Binding Playback.LocalMuted}" Margin="0,0,4,0"
                              ToolTip="Silence the local (my microphone) leg"
                              Visibility="{Binding Playback.HasLocalLeg, Converter={StaticResource BoolToVis}}" />
                <ToggleButton Content="Mute remote" IsChecked="{Binding Playback.RemoteMuted}"
                              ToolTip="Silence the remote (other party) leg"
                              Visibility="{Binding Playback.HasRemoteLeg, Converter={StaticResource BoolToVis}}" />
            </StackPanel>
        </Border>
```

- [ ] Wire the tick + transport handlers. In `src/LocalScribe.App/ReadViewWindow.xaml.cs`:
  - Add `using System.Windows.Threading;` to the usings.
  - Add fields below `private readonly int _openAtCreation;`:

```csharp
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _seekDragging;
```

  - Replace the `Loaded += ...` line in the constructor with:

```csharp
        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync(_sessionId, CancellationToken.None);
            if (_vm.Playback.IsAvailable) _tick.Start();             // same ~150 ms pattern as the live view timer
        };
        _tick.Tick += (_, _) =>
        {
            _vm.Playback.Tick();
            SeekSlider.Maximum = Math.Max(1, _vm.Playback.DurationMs);
            if (!_seekDragging) SeekSlider.Value = _vm.Playback.PositionMs;
        };
```

  - Add these handlers after `OnSourceInitialized`:

```csharp
    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        _vm.Playback.PlayPauseCommand.Execute(null);
        PlayPauseButton.Content = _vm.Playback.IsPlaying ? "Pause" : "Play";
    }

    private void OnSeekDragStarted(object sender, RoutedEventArgs e) => _seekDragging = true;

    private void OnSeekDragCompleted(object sender, RoutedEventArgs e)
    {
        _seekDragging = false;
        _vm.Playback.Seek((long)SeekSlider.Value);
    }
```

  - In `OnClosed`, insert as the FIRST two lines (before `_registry.Unregister(_sessionId);`):

```csharp
        _tick.Stop();
        _vm.Dispose();                                               // releases both MediaPlayer file handles
```

- [ ] Update the read-view open path (merge point with the Sessions page task): wherever `new ReadViewViewModel(...)` is constructed for a window, pass `new MediaPlayerDualAudioPlayer()` as the `player` argument (one player per window; the window's VM disposal owns it).
- [ ] Build gate: `dotnet build` - expected: Build succeeded, 0 Warning(s).
- [ ] Full gate: `dotnet test --filter "Category!=Fixture"` - expected: all green.
- [ ] Smoke runbook note (for the Stage 4 runbook task): add steps "open a read view on a real Webex session; Play - both legs audible together; mute Local, then Remote - each isolates one side; seek - both legs jump together; session with one retained leg degrades; session with no audio shows no transport; last-closed read view remembers placement; second read view opens +24px offset".
- [ ] Commit: `git add -A && git commit -m "feat: dual MediaPlayer playback implementation + read-view transport bar"`

---

### Task 21: SettingsPageViewModel + UI

**Decision recorded (mic device picking):** I read `src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs` and `src/LocalScribe.Core/Live/WasapiSessionScanner.cs`. The scanner enumerates **render** audio sessions (PIDs of apps playing sound), not capture devices, and `WasapiCaptureSourceProvider.CreateMic` always follows the Windows Communications default ("Pinned-mic mode is a Stage 7 concern" per its own doc comment). **No clean capture-device enumeration API exists in the codebase**, so per the assignment's fallback: the Mic group is a **read-only display** — follow-default shows "Follows the Windows Communications default"; a pinned settings.json value shows "Pinned: {name}" with a note that pinning takes effect in a later stage — and **device picking is deferred to the smoke runbook as a known limitation** (recorded in Task 25's C9).

**Not exposed (design 6.1, pinned by a reflection test):** `recordingIndicator` (tray consent indicator is immovable), `hotkeys` (dropped, design 1.1), `autoDetect` (disabled seam), `vocabulary` (Stage 6).

**Files:**
- Create: `src/LocalScribe.App/Services/ILaunchAtLogin.cs`
- Create: `src/LocalScribe.App/Services/RegistryLaunchAtLogin.cs`
- Create: `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs`
- Create: `src/LocalScribe.App/SettingsPage.xaml` + `src/LocalScribe.App/SettingsPage.xaml.cs` (flat layout, matching the existing `LiveViewWindow.xaml` / `OverlayWindow.xaml` placement)
- Create: `tests/LocalScribe.App.Tests/AppServiceFakes.cs`
- Create: `tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs`

**Interfaces:**
- *Consumes:* `ISettingsService` (Task 10, locked); `MaintenanceService(StoragePaths, ISettingsService, IRecycleBin, TimeProvider)` + `RegenerateAllAsync(IProgress<int>?, CancellationToken)` (locked); `IUiErrorReporter` (locked); `IRecycleBin` (locked); `Settings.Privacy`/`PrivacySetting` (Task 1/2, locked); `SyncProviderCheck.ResolvesUnderSyncProvider(string, out string?)` (existing, `src/LocalScribe.Core/Storage/SyncProviderCheck.cs:9`); `ModelPaths.ModelsRoot` (existing, `src/LocalScribe.Core/Transcription/ModelPaths.cs:8`); `StoragePaths(string)` (existing).
- *Produces:*
```csharp
// src/LocalScribe.App/Services/ILaunchAtLogin.cs
public interface ILaunchAtLogin { bool IsEnabled(); void SetEnabled(bool on); }
public sealed class RegistryLaunchAtLogin : ILaunchAtLogin;   // HKCU Run key; untested headless (runbook C9)

// src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs
public sealed partial class SettingsPageViewModel : ObservableObject
{
    public SettingsPageViewModel(ISettingsService settings, MaintenanceService maintenance,
        ILaunchAtLogin launchAtLogin, Func<string?> pickFolder, Action<string> openFolder,
        IUiErrorReporter errors, Action<Action> dispatch, string? modelsRoot = null);
    public Task LastSave { get; }                       // last SaveAsync round-trip (tests await it)
}
// The Main-window task composes it with pickFolder = Microsoft.Win32.OpenFolderDialog (code-behind),
// openFolder = p => Process.Start("explorer.exe", p), launchAtLogin = new RegistryLaunchAtLogin().

// src/LocalScribe.App/SettingsPage.xaml.cs
public partial class SettingsPage : System.Windows.Controls.UserControl
{ public SettingsPage(ViewModels.SettingsPageViewModel vm); }   // hosted by MainWindow's NavigationView

// tests/LocalScribe.App.Tests/AppServiceFakes.cs (reused by Tasks 22/23)
public sealed class FakeSettingsService : ISettingsService;
public sealed class FakeUiErrorReporter : IUiErrorReporter;
public sealed class FakeRecycleBin : IRecycleBin;
public sealed class FakeLaunchAtLogin : ILaunchAtLogin;
```

**Steps:**

- [ ] Write the shared fakes file `tests/LocalScribe.App.Tests/AppServiceFakes.cs` (full contents):

```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Model;

namespace LocalScribe.App.Tests;

/// <summary>Synchronous ISettingsService: SaveAsync swaps Current and raises Changed inline,
/// so VM commits are deterministic in tests (no SpinWait needed on Current).</summary>
public sealed class FakeSettingsService : ISettingsService
{
    public FakeSettingsService(Settings? initial = null) => Current = initial ?? new Settings();
    public Settings Current { get; private set; }
    public int SaveCount { get; private set; }
    public event Action<Settings, Settings>? Changed;

    public Task SaveAsync(Settings updated, CancellationToken ct)
    {
        var old = Current;
        Current = updated;
        SaveCount++;
        Changed?.Invoke(old, updated);
        return Task.CompletedTask;
    }
}

public sealed class FakeUiErrorReporter : IUiErrorReporter
{
    public readonly List<(string Context, Exception Ex)> Reports = new();
    public readonly List<string> Infos = new();
    public void Report(string context, Exception ex) => Reports.Add((context, ex));
    public void Info(string message) => Infos.Add(message);
}

public sealed class FakeRecycleBin : IRecycleBin
{
    public readonly List<string> Recycled = new();
    public void SendToRecycleBin(string path) => Recycled.Add(path);
}

public sealed class FakeLaunchAtLogin : ILaunchAtLogin
{
    public bool Enabled = true;
    public readonly List<bool> SetCalls = new();
    public bool IsEnabled() => Enabled;
    public void SetEnabled(bool on) { Enabled = on; SetCalls.Add(on); }
}
```

- [ ] Write the failing tests for the Storage + Recording + Transcription groups, `tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs` (full contents; the Identity/Privacy/App facts are added in a later cycle to this same file):

```csharp
using System.IO;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SettingsPageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-setvm-" + Guid.NewGuid().ToString("N"));
    private FakeSettingsService _settings = new();
    private readonly FakeUiErrorReporter _errors = new();
    private readonly FakeLaunchAtLogin _launch = new();
    private string? _pickResult;

    public SettingsPageViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(Path.Combine(_root, "storage", "sessions"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private SettingsPageViewModel MakeVm(Settings? initial = null)
    {
        if (initial is not null) _settings = new FakeSettingsService(initial);
        var maintenance = new Services.MaintenanceService(
            new StoragePaths(Path.Combine(_root, "storage")), _settings, new FakeRecycleBin(),
            TimeProvider.System);
        return new SettingsPageViewModel(_settings, maintenance, _launch,
            pickFolder: () => _pickResult, openFolder: _ => { }, _errors,
            dispatch: a => a(), modelsRoot: Path.Combine(_root, "models"));
    }

    [Fact]
    public async Task Pick_folder_stores_the_literal_path_and_flags_restart_required()
    {
        var vm = MakeVm();
        _pickResult = Path.Combine(_root, "new-home");
        vm.PickStorageRootCommand.Execute(null);
        await vm.LastSave;
        Assert.Equal(_pickResult, _settings.Current.StorageRoot);   // literal, never re-tokenized
        Assert.Equal(_pickResult, vm.StorageRoot);
        Assert.True(vm.RestartRequired);
        Assert.Contains("restart", vm.RestartRequiredNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelled_pick_saves_nothing()
    {
        var vm = MakeVm();
        _pickResult = null;
        vm.PickStorageRootCommand.Execute(null);
        await vm.LastSave;
        Assert.Equal(0, _settings.SaveCount);
        Assert.False(vm.RestartRequired);
    }

    [Fact]
    public void Sync_provider_warning_fires_only_under_a_known_provider()
    {
        var warned = MakeVm(new Settings { StorageRoot = Path.Combine(_root, "OneDrive", "LocalScribe") });
        Assert.NotNull(warned.SyncProviderWarning);
        Assert.Contains("OneDrive", warned.SyncProviderWarning);
        var clean = MakeVm(new Settings { StorageRoot = Path.Combine(_root, "plain") });
        Assert.Null(clean.SyncProviderWarning);
    }

    [Fact]
    public async Task Regenerate_all_projections_runs_and_resets_state()
    {
        var vm = MakeVm(new Settings { StorageRoot = Path.Combine(_root, "storage") });
        await vm.RegenerateAllProjectionsCommand.ExecuteAsync(null);
        Assert.False(vm.IsRegenerating);
        Assert.Empty(_errors.Reports);
    }

    [Fact]
    public async Task Recording_fields_commit_and_carry_the_next_start_note()
    {
        var vm = MakeVm();
        vm.AudioFormat = AudioFormat.Wav;
        await vm.LastSave;
        vm.RemoteMode = RemoteMode.SystemMix;
        await vm.LastSave;
        Assert.Equal(AudioFormat.Wav, _settings.Current.AudioFormat);
        Assert.Equal(RemoteMode.SystemMix, _settings.Current.Remote.Mode);
        Assert.Contains("next Start", vm.RecordingApplyNote);
        Assert.Equal(new[] { AudioFormat.Flac, AudioFormat.Wav }, vm.AudioFormatChoices);
        Assert.Equal(new[] { RemoteMode.Auto, RemoteMode.PerProcess, RemoteMode.SystemMix }, vm.RemoteModeChoices);
    }

    [Fact]
    public void Mic_and_retention_are_read_only_displays()
    {
        var follow = MakeVm();
        Assert.Contains("Communications default", follow.MicDisplay);
        var pinned = MakeVm(new Settings { Mic = new MicSetting { Mode = MicMode.Pinned, Name = "Shure MV7" } });
        Assert.Contains("Shure MV7", pinned.MicDisplay);
        Assert.Contains("Keep everything", follow.AudioRetentionDisplay);
        var legacy = MakeVm(new Settings { AudioRetention = "days:30" });
        Assert.Contains("days:30", legacy.AudioRetentionDisplay);
    }

    [Fact]
    public async Task Model_choices_enumerate_only_installed_ggml_files_plus_auto()
    {
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-tiny.en.bin"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-small.bin"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(_root, "models", "silero_vad.onnx"), "x");   // not a whisper model
        var vm = MakeVm();
        Assert.Equal(new[] { "auto", "small", "tiny.en" }, vm.ModelChoices);
        vm.Model = "tiny.en";
        await vm.LastSave;
        Assert.Equal("tiny.en", _settings.Current.Model);
    }

    [Fact]
    public async Task Backend_and_language_commit_and_blank_language_normalizes_to_auto()
    {
        var vm = MakeVm();
        Assert.Equal("auto", vm.Language);
        vm.Backend = Backend.Cpu;
        await vm.LastSave;
        vm.Language = "  ";
        await vm.LastSave;
        Assert.Equal(Backend.Cpu, _settings.Current.Backend);
        Assert.Equal("auto", _settings.Current.Language);
        vm.Language = "en";
        await vm.LastSave;
        Assert.Equal("en", _settings.Current.Language);
    }
}
```

- [ ] Run it and confirm the failure is the missing types: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsPageViewModelTests"` — expected: **build error CS0246** (`SettingsPageViewModel`, `ILaunchAtLogin` not found).

- [ ] Implement `src/LocalScribe.App/Services/ILaunchAtLogin.cs` (full contents):

```csharp
namespace LocalScribe.App.Services;

/// <summary>Launch-at-login seam (design 6.1, App group). The registry implementation is a
/// Humble Object (RegistryLaunchAtLogin) verified by the smoke runbook, not unit tests;
/// SettingsPageViewModel is tested against a fake.</summary>
public interface ILaunchAtLogin
{
    bool IsEnabled();
    void SetEnabled(bool on);
}
```

- [ ] Implement `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs` (full contents — WPF-free: no System.Windows usings):

```csharp
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;

namespace LocalScribe.App.ViewModels;

/// <summary>Settings page VM (design 6.1/6.2). WPF-free. Every committed change goes through
/// ISettingsService.SaveAsync (Current with { ... }) - auto-save on field commit, no Save
/// button. Deliberately NOT exposed (design 6.1): recordingIndicator (the tray consent
/// indicator is immovable), hotkeys (dropped, design 1.1), autoDetect (disabled seam),
/// vocabulary (Stage 6) - a reflection test pins their absence. The Mic group is a read-only
/// display: the codebase has no capture-device enumeration (WasapiSessionScanner enumerates
/// RENDER sessions; pinning is a Stage 7 concern per WasapiCaptureSourceProvider).</summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly MaintenanceService _maintenance;
    private readonly ILaunchAtLogin _launchAtLogin;
    private readonly Func<string?> _pickFolder;
    private readonly Action<string> _openFolder;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly string _initialRoot;

    [ObservableProperty] private bool _restartRequired;
    [ObservableProperty] private bool _isRegenerating;
    [ObservableProperty] private int _regenerateProgress;

    /// <summary>The last SaveAsync round-trip. Production fire-and-forgets (failures surface
    /// via IUiErrorReporter); tests await it so no commit is in flight when they assert.</summary>
    public Task LastSave { get; private set; } = Task.CompletedTask;

    public SettingsPageViewModel(ISettingsService settings, MaintenanceService maintenance,
        ILaunchAtLogin launchAtLogin, Func<string?> pickFolder, Action<string> openFolder,
        IUiErrorReporter errors, Action<Action> dispatch, string? modelsRoot = null)
    {
        (_settings, _maintenance, _launchAtLogin, _pickFolder, _openFolder, _errors, _dispatch)
            = (settings, maintenance, launchAtLogin, pickFolder, openFolder, errors, dispatch);
        _initialRoot = settings.Current.StorageRoot;
        ModelChoices = BuildModelChoices(modelsRoot ?? ModelPaths.ModelsRoot);

        PickStorageRootCommand = new RelayCommand(PickStorageRoot);
        OpenStorageRootCommand = new RelayCommand(
            () => _openFolder(new StoragePaths(_settings.Current.StorageRoot).Root));
        RegenerateAllProjectionsCommand = new AsyncRelayCommand(RegenerateAllAsync, () => !IsRegenerating);
    }

    // ---------- Storage ----------
    public string StorageRoot => _settings.Current.StorageRoot;
    public IRelayCommand PickStorageRootCommand { get; }
    public IRelayCommand OpenStorageRootCommand { get; }
    public IAsyncRelayCommand RegenerateAllProjectionsCommand { get; }

    public string RestartRequiredNote { get; } =
        "The storage root change takes effect after a restart. No data is migrated: existing "
        + "sessions stay in the old root and will drop out of the list.";

    public string? SyncProviderWarning
        => SyncProviderCheck.ResolvesUnderSyncProvider(
               new StoragePaths(_settings.Current.StorageRoot).Root, out string? provider)
           ? $"This folder is under {provider}: audio and transcripts would sync off this machine."
           : null;

    private void PickStorageRoot()
    {
        string? picked = _pickFolder();
        if (string.IsNullOrWhiteSpace(picked)) return;
        // Picking always stores the LITERAL path (design 6.1); a %VAR% form survives only
        // while the stored value is left untouched.
        Commit(_settings.Current with { StorageRoot = picked });
        RestartRequired = !string.Equals(picked, _initialRoot, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(StorageRoot));
        OnPropertyChanged(nameof(SyncProviderWarning));
    }

    private async Task RegenerateAllAsync()
    {
        IsRegenerating = true;
        RegenerateProgress = 0;
        try
        {
            await _maintenance.RegenerateAllAsync(
                new DispatchedProgress(_dispatch, n => RegenerateProgress = n), CancellationToken.None);
        }
        catch (Exception ex) { _errors.Report("Regenerate all projections", ex); }
        finally { IsRegenerating = false; RegenerateAllProjectionsCommand.NotifyCanExecuteChanged(); }
    }

    /// <summary>IProgress that marshals via the injected dispatch (never Progress&lt;T&gt;,
    /// which captures SynchronizationContext - VMs must stay WPF-free and test-deterministic).</summary>
    private sealed class DispatchedProgress(Action<Action> dispatch, Action<int> apply) : IProgress<int>
    {
        public void Report(int value) => dispatch(() => apply(value));
    }

    // ---------- Recording (design 6.2: applies at the NEXT Start) ----------
    public string RecordingApplyNote { get; } = "Recording settings apply at the next Start.";

    public IReadOnlyList<AudioFormat> AudioFormatChoices { get; } = [AudioFormat.Flac, AudioFormat.Wav];
    public AudioFormat AudioFormat
    {
        get => _settings.Current.AudioFormat;
        set { Commit(_settings.Current with { AudioFormat = value }); OnPropertyChanged(); }
    }

    public IReadOnlyList<RemoteMode> RemoteModeChoices { get; } =
        [RemoteMode.Auto, RemoteMode.PerProcess, RemoteMode.SystemMix];
    public RemoteMode RemoteMode
    {
        get => _settings.Current.Remote.Mode;
        set
        {
            Commit(_settings.Current with { Remote = _settings.Current.Remote with { Mode = value } });
            OnPropertyChanged();
        }
    }

    public string MicDisplay => _settings.Current.Mic.Mode == MicMode.Pinned
        ? "Pinned: " + (_settings.Current.Mic.Name ?? "(unnamed device)")
        : "Follows the Windows Communications default";
    public string MicNote { get; } =
        "Microphone device picking is not available yet. Recording follows the Windows "
        + "Communications default; a pinned device configured in settings.json is shown here "
        + "but takes effect in a later stage.";

    public string AudioRetentionDisplay
    {
        get
        {
            string v = _settings.Current.AudioRetention;
            return v is "keep" or "forever"
                ? "Keep everything (audio is never auto-deleted)"
                : "Migrated policy: " + v + " (retention editing is not exposed)";
        }
    }

    // ---------- Transcription ----------
    public IReadOnlyList<string> ModelChoices { get; }
    public string Model
    {
        get => _settings.Current.Model;
        set { Commit(_settings.Current with { Model = value }); OnPropertyChanged(); }
    }

    public IReadOnlyList<Backend> BackendChoices { get; } =
        [Backend.Auto, Backend.Cuda, Backend.Vulkan, Backend.Cpu];
    public Backend Backend
    {
        get => _settings.Current.Backend;
        set { Commit(_settings.Current with { Backend = value }); OnPropertyChanged(); }
    }

    public string Language
    {
        get => _settings.Current.Language;
        set
        {
            Commit(_settings.Current with
            { Language = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim() });
            OnPropertyChanged();
        }
    }

    /// <summary>"auto" + only the models actually on disk (design 6.1: an absent model cannot
    /// be selected; model-download UX is Stage 7). Engine files are ggml-{name}.bin
    /// (WhisperEngineFactory).</summary>
    private static IReadOnlyList<string> BuildModelChoices(string modelsRoot)
    {
        var choices = new List<string> { "auto" };
        try
        {
            if (Directory.Exists(modelsRoot))
                choices.AddRange(Directory.EnumerateFiles(modelsRoot, "ggml-*.bin")
                    .Select(f => Path.GetFileNameWithoutExtension(f)["ggml-".Length..])
                    .OrderBy(n => n, StringComparer.Ordinal));
        }
        catch (IOException) { }              // unreadable models dir -> "auto" only
        return choices;
    }

    // ---------- Identity (snapshotted into FUTURE sessions only - SessionBootstrap) ----------
    public string IdentityNote { get; } =
        "Your name and role are snapshotted into future sessions when they start; existing "
        + "sessions are never rewritten.";
    public string SelfName
    {
        get => _settings.Current.Self.Name;
        set
        {
            Commit(_settings.Current with { Self = _settings.Current.Self with { Name = value } });
            OnPropertyChanged();
        }
    }
    public string SelfRole
    {
        get => _settings.Current.Self.Role ?? "";
        set
        {
            Commit(_settings.Current with
            { Self = _settings.Current.Self with { Role = string.IsNullOrWhiteSpace(value) ? null : value } });
            OnPropertyChanged();
        }
    }

    // ---------- Privacy ----------
    public bool ExcludeWindowsFromCapture
    {
        get => _settings.Current.Privacy.ExcludeWindowsFromCapture;
        set
        {
            Commit(_settings.Current with
            { Privacy = _settings.Current.Privacy with { ExcludeWindowsFromCapture = value } });
            OnPropertyChanged();
        }
    }

    public bool OverlayEnabled
    {
        get => _settings.Current.Overlay.Enabled;
        set
        {
            Commit(_settings.Current with { Overlay = _settings.Current.Overlay with { Enabled = value } });
            OnPropertyChanged();
        }
    }
    public bool OverlayShowSessionName
    {
        get => _settings.Current.Overlay.ShowSessionName;
        set
        {
            Commit(_settings.Current with
            { Overlay = _settings.Current.Overlay with { ShowSessionName = value } });
            OnPropertyChanged();
        }
    }
    public bool OverlayShowLevelMeter
    {
        get => _settings.Current.Overlay.ShowLevelMeter;
        set
        {
            Commit(_settings.Current with
            { Overlay = _settings.Current.Overlay with { ShowLevelMeter = value } });
            OnPropertyChanged();
        }
    }
    public bool OverlayExcludeFromCapture
    {
        get => _settings.Current.Overlay.ExcludeFromCapture;
        set
        {
            Commit(_settings.Current with
            { Overlay = _settings.Current.Overlay with { ExcludeFromCapture = value } });
            OnPropertyChanged();
        }
    }

    public string LoggingRedactionNote { get; } =
        "Transcript text is redacted from logs by default (logging arrives in Stage 7).";

    // ---------- App ----------
    public bool LaunchAtLogin
    {
        get => _settings.Current.LaunchAtLogin;
        set
        {
            try { _launchAtLogin.SetEnabled(value); }
            catch (Exception ex) { _errors.Report("Launch at login", ex); }
            Commit(_settings.Current with { LaunchAtLogin = value });
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> TimestampChoices { get; } = ["relative", "wallclock"];
    public string Timestamps
    {
        get => _settings.Current.Timestamps;
        set { Commit(_settings.Current with { Timestamps = value }); OnPropertyChanged(); }
    }

    private void Commit(Settings updated) => LastSave = CommitAsync(updated);

    private async Task CommitAsync(Settings updated)
    {
        try { await _settings.SaveAsync(updated, CancellationToken.None); }
        catch (Exception ex) { _errors.Report("Saving settings", ex); }
    }
}
```

- [ ] Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsPageViewModelTests"` — expected: **all 8 facts PASS**.
- [ ] Commit: `git add -A && git commit -m "feat: settings page VM - storage, recording, transcription groups (auto-save via ISettingsService)"`

- [ ] Append the Identity/Privacy/App/dropped-surface facts to `tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs` (insert before the closing brace of the class):

```csharp
    [Fact]
    public async Task Identity_commits_and_blank_role_normalizes_to_null()
    {
        var vm = MakeVm();
        vm.SelfName = "Sam";
        await vm.LastSave;
        vm.SelfRole = "  ";
        await vm.LastSave;
        Assert.Equal("Sam", _settings.Current.Self.Name);
        Assert.Null(_settings.Current.Self.Role);
        vm.SelfRole = "Attorney";
        await vm.LastSave;
        Assert.Equal("Attorney", _settings.Current.Self.Role);
    }

    [Fact]
    public async Task Privacy_toggles_commit_to_privacy_and_overlay_settings()
    {
        var vm = MakeVm();
        Assert.True(vm.ExcludeWindowsFromCapture);              // default true (design section 2)
        vm.ExcludeWindowsFromCapture = false;
        await vm.LastSave;
        vm.OverlayShowSessionName = true;
        await vm.LastSave;
        vm.OverlayExcludeFromCapture = false;
        await vm.LastSave;
        vm.OverlayShowLevelMeter = false;
        await vm.LastSave;
        vm.OverlayEnabled = false;
        await vm.LastSave;
        Assert.False(_settings.Current.Privacy.ExcludeWindowsFromCapture);
        Assert.True(_settings.Current.Overlay.ShowSessionName);
        Assert.False(_settings.Current.Overlay.ExcludeFromCapture);
        Assert.False(_settings.Current.Overlay.ShowLevelMeter);
        Assert.False(_settings.Current.Overlay.Enabled);
        Assert.Contains("redacted", vm.LoggingRedactionNote);
    }

    [Fact]
    public async Task Launch_at_login_drives_the_seam_and_persists()
    {
        var vm = MakeVm();
        vm.LaunchAtLogin = false;
        await vm.LastSave;
        Assert.Equal(new[] { false }, _launch.SetCalls);
        Assert.False(_settings.Current.LaunchAtLogin);
        vm.Timestamps = "wallclock";
        await vm.LastSave;
        Assert.Equal("wallclock", _settings.Current.Timestamps);
    }

    [Fact]
    public void Vm_exposes_no_dropped_setting_surfaces()
    {
        // Design 6.1: recordingIndicator, hotkeys, autoDetect, vocabulary are NOT exposed.
        var names = typeof(SettingsPageViewModel).GetProperties().Select(p => p.Name).ToArray();
        foreach (string banned in new[] { "RecordingIndicator", "Hotkey", "AutoDetect", "Vocabulary" })
            Assert.DoesNotContain(names, n => n.Contains(banned, StringComparison.OrdinalIgnoreCase));
    }
```

- [ ] Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SettingsPageViewModelTests"` — expected: **all 12 facts PASS** (the VM from the previous cycle already implements these groups; this cycle pins them).
- [ ] Commit: `git add -A && git commit -m "test: settings VM identity/privacy/app groups + dropped-surface reflection guard"`

- [ ] Implement `src/LocalScribe.App/Services/RegistryLaunchAtLogin.cs` (full contents; Humble Object — no unit test, verified by runbook C9; `Microsoft.Win32.Registry` is not System.Windows, so the Services WPF-free rule holds):

```csharp
using Microsoft.Win32;

namespace LocalScribe.App.Services;

/// <summary>HKCU Run-key launch-at-login (design 6.1: launchAtLogin WIRED in Stage 4).
/// Humble Object: registry access is untestable headless, so this stays one-line-per-branch
/// and the smoke runbook (C9) verifies it; SettingsPageViewModel is tested against a fake.</summary>
public sealed class RegistryLaunchAtLogin : ILaunchAtLogin
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LocalScribe";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public void SetEnabled(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (on) key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
```

- [ ] Create `src/LocalScribe.App/SettingsPage.xaml` (full contents):

```xml
<UserControl x:Class="LocalScribe.App.SettingsPage"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
        <Style x:Key="HideWhenNull" TargetType="TextBlock">
            <Setter Property="TextWrapping" Value="Wrap" />
            <Setter Property="Foreground" Value="Orange" />
            <Setter Property="Margin" Value="0,4,0,0" />
            <Style.Triggers>
                <DataTrigger Binding="{Binding SyncProviderWarning}" Value="{x:Null}">
                    <Setter Property="Visibility" Value="Collapsed" />
                </DataTrigger>
            </Style.Triggers>
        </Style>
        <Style x:Key="Note" TargetType="TextBlock">
            <Setter Property="TextWrapping" Value="Wrap" />
            <Setter Property="Opacity" Value="0.7" />
            <Setter Property="Margin" Value="0,4,0,0" />
        </Style>
        <Style x:Key="FieldLabel" TargetType="TextBlock">
            <Setter Property="VerticalAlignment" Value="Center" />
            <Setter Property="Margin" Value="0,0,8,0" />
            <Setter Property="MinWidth" Value="140" />
        </Style>
        <Style x:Key="FieldRow" TargetType="StackPanel">
            <Setter Property="Orientation" Value="Horizontal" />
            <Setter Property="Margin" Value="0,4,0,4" />
        </Style>
    </UserControl.Resources>
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Margin="16" MaxWidth="640" HorizontalAlignment="Left">

            <GroupBox Header="Storage" Padding="8" Margin="0,0,0,12">
                <StackPanel>
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Storage root" Style="{StaticResource FieldLabel}" />
                        <TextBlock Text="{Binding StorageRoot, Mode=OneWay}" VerticalAlignment="Center"
                                   Margin="0,0,8,0" />
                        <Button Content="Choose folder..." Command="{Binding PickStorageRootCommand}"
                                Margin="0,0,8,0" />
                        <Button Content="Open folder" Command="{Binding OpenStorageRootCommand}" />
                    </StackPanel>
                    <TextBlock Style="{StaticResource HideWhenNull}"
                               Text="{Binding SyncProviderWarning, Mode=OneWay}" />
                    <Border Background="#33FF8C00" CornerRadius="4" Padding="8" Margin="0,6,0,0"
                            Visibility="{Binding RestartRequired, Converter={StaticResource BoolToVis}}">
                        <TextBlock Text="{Binding RestartRequiredNote, Mode=OneWay}" TextWrapping="Wrap" />
                    </Border>
                    <StackPanel Style="{StaticResource FieldRow}">
                        <Button Content="Regenerate all projections"
                                Command="{Binding RegenerateAllProjectionsCommand}" Margin="0,0,8,0" />
                        <StackPanel Orientation="Horizontal"
                                    Visibility="{Binding IsRegenerating, Converter={StaticResource BoolToVis}}">
                            <ProgressBar Width="120" Height="6" IsIndeterminate="True"
                                         VerticalAlignment="Center" Margin="0,0,8,0" />
                            <TextBlock Text="{Binding RegenerateProgress, Mode=OneWay}"
                                       VerticalAlignment="Center" />
                            <TextBlock Text=" sessions re-rendered" VerticalAlignment="Center" />
                        </StackPanel>
                    </StackPanel>
                </StackPanel>
            </GroupBox>

            <GroupBox Header="Recording" Padding="8" Margin="0,0,0,12">
                <StackPanel>
                    <TextBlock Text="{Binding RecordingApplyNote, Mode=OneWay}" Style="{StaticResource Note}" />
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Audio format" Style="{StaticResource FieldLabel}" />
                        <ComboBox ItemsSource="{Binding AudioFormatChoices}"
                                  SelectedItem="{Binding AudioFormat}" MinWidth="140" />
                    </StackPanel>
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Remote capture" Style="{StaticResource FieldLabel}" />
                        <ComboBox ItemsSource="{Binding RemoteModeChoices}"
                                  SelectedItem="{Binding RemoteMode}" MinWidth="140" />
                    </StackPanel>
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Microphone" Style="{StaticResource FieldLabel}" />
                        <TextBlock Text="{Binding MicDisplay, Mode=OneWay}" VerticalAlignment="Center" />
                    </StackPanel>
                    <TextBlock Text="{Binding MicNote, Mode=OneWay}" Style="{StaticResource Note}" />
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Audio retention" Style="{StaticResource FieldLabel}" />
                        <TextBlock Text="{Binding AudioRetentionDisplay, Mode=OneWay}"
                                   VerticalAlignment="Center" />
                    </StackPanel>
                </StackPanel>
            </GroupBox>

            <GroupBox Header="Transcription" Padding="8" Margin="0,0,0,12">
                <StackPanel>
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Model" Style="{StaticResource FieldLabel}" />
                        <ComboBox ItemsSource="{Binding ModelChoices}" SelectedItem="{Binding Model}"
                                  MinWidth="140" />
                    </StackPanel>
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Backend" Style="{StaticResource FieldLabel}" />
                        <ComboBox ItemsSource="{Binding BackendChoices}" SelectedItem="{Binding Backend}"
                                  MinWidth="140" />
                    </StackPanel>
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Language" Style="{StaticResource FieldLabel}" />
                        <TextBox Text="{Binding Language}" MinWidth="140" />
                    </StackPanel>
                </StackPanel>
            </GroupBox>

            <GroupBox Header="Identity" Padding="8" Margin="0,0,0,12">
                <StackPanel>
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Your name" Style="{StaticResource FieldLabel}" />
                        <TextBox Text="{Binding SelfName}" MinWidth="200" />
                    </StackPanel>
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Your role" Style="{StaticResource FieldLabel}" />
                        <TextBox Text="{Binding SelfRole}" MinWidth="200" />
                    </StackPanel>
                    <TextBlock Text="{Binding IdentityNote, Mode=OneWay}" Style="{StaticResource Note}" />
                </StackPanel>
            </GroupBox>

            <GroupBox Header="Privacy" Padding="8" Margin="0,0,0,12">
                <StackPanel>
                    <CheckBox Content="Hide LocalScribe windows from screen shares and recordings"
                              IsChecked="{Binding ExcludeWindowsFromCapture}" Margin="0,4,0,4" />
                    <CheckBox Content="Show the recording overlay pill"
                              IsChecked="{Binding OverlayEnabled}" Margin="0,4,0,4" />
                    <CheckBox Content="Hide the overlay from screen shares"
                              IsChecked="{Binding OverlayExcludeFromCapture}" Margin="0,4,0,4" />
                    <CheckBox Content="Show the session name in the overlay tooltip"
                              IsChecked="{Binding OverlayShowSessionName}" Margin="0,4,0,4" />
                    <CheckBox Content="Show the overlay level meter"
                              IsChecked="{Binding OverlayShowLevelMeter}" Margin="0,4,0,4" />
                    <TextBlock Text="{Binding LoggingRedactionNote, Mode=OneWay}"
                               Style="{StaticResource Note}" />
                </StackPanel>
            </GroupBox>

            <GroupBox Header="App" Padding="8" Margin="0,0,0,12">
                <StackPanel>
                    <CheckBox Content="Start LocalScribe when you sign in to Windows"
                              IsChecked="{Binding LaunchAtLogin}" Margin="0,4,0,4" />
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Timestamps" Style="{StaticResource FieldLabel}" />
                        <ComboBox ItemsSource="{Binding TimestampChoices}"
                                  SelectedItem="{Binding Timestamps}" MinWidth="140" />
                    </StackPanel>
                </StackPanel>
            </GroupBox>

        </StackPanel>
    </ScrollViewer>
</UserControl>
```

- [ ] Create `src/LocalScribe.App/SettingsPage.xaml.cs` (full contents):

```csharp
namespace LocalScribe.App;

/// <summary>Humble shell for the Settings page - pure XAML assembly over the tested
/// SettingsPageViewModel. Hosted by MainWindow's NavigationView.</summary>
public partial class SettingsPage
{
    public SettingsPage(ViewModels.SettingsPageViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
```

- [ ] Build + full gate: `dotnet build LocalScribe.slnx` (expected: 0 warnings, 0 errors) then `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` (expected: all green).
- [ ] Commit: `git add -A && git commit -m "feat: settings page XAML + HKCU Run-key launch-at-login (runbook-verified)"`

---

### Task 22: First-run consent dialog

Text reuses the README "Privacy" section draft language (README.md lines 74-83) verbatim in substance, ASCII-normalized (em dashes become "-"). Integration into the startup sequence (shown modally BEFORE tray creation when `settings.Current.ConsentNotice is null`) is wired in Task 24; this task delivers the tested VM and the window.

**Files:**
- Create: `src/LocalScribe.App/ViewModels/ConsentViewModel.cs`
- Create: `src/LocalScribe.App/ConsentDialog.xaml` + `src/LocalScribe.App/ConsentDialog.xaml.cs`
- Create: `tests/LocalScribe.App.Tests/ConsentViewModelTests.cs`

**Interfaces:**
- *Consumes:* `ISettingsService` (Task 10, locked); `ConsentSetting` (Task 1/2, locked: `AcknowledgedAtUtc`, `AppVersion`); `FakeSettingsService` (Task 21); `ManualUtcTimeProvider` (existing linked test source, `tests/LocalScribe.Core.Tests/ManualUtcTimeProvider.cs`).
- *Produces:*
```csharp
// src/LocalScribe.App/ViewModels/ConsentViewModel.cs
public sealed partial class ConsentViewModel : ObservableObject
{
    public ConsentViewModel(ISettingsService settings, TimeProvider time, string appVersion);
    public string Title { get; }
    public string SummaryText { get; }
    public string ResponsibilityText { get; }
    public IAsyncRelayCommand AcceptCommand { get; }   // generated by [RelayCommand]
    public IRelayCommand DeclineCommand { get; }       // generated by [RelayCommand]
    public event Action<bool>? Closed;                 // accepted: true after persist, false on decline
}
// src/LocalScribe.App/ConsentDialog.xaml.cs
public partial class ConsentDialog : Wpf.Ui.Controls.FluentWindow
{ public ConsentDialog(ViewModels.ConsentViewModel vm); }   // ShowDialog() == true only on Accept
```

**Steps:**

- [ ] Write the failing test `tests/LocalScribe.App.Tests/ConsentViewModelTests.cs` (full contents):

```csharp
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ConsentViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 3, 4, 0, 0, TimeSpan.Zero);

    private static (ConsentViewModel Vm, FakeSettingsService Settings, List<bool> Closed) Make()
    {
        var settings = new FakeSettingsService();
        var vm = new ConsentViewModel(settings, new ManualUtcTimeProvider(Now), "0.4.0");
        var closed = new List<bool>();
        vm.Closed += closed.Add;
        return (vm, settings, closed);
    }

    [Fact]
    public async Task Accept_persists_time_and_version_then_closes_accepted()
    {
        var (vm, settings, closed) = Make();
        Assert.Null(settings.Current.ConsentNotice);            // fresh install: notice due

        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.NotNull(settings.Current.ConsentNotice);
        Assert.Equal(Now, settings.Current.ConsentNotice!.AcknowledgedAtUtc);
        Assert.Equal("0.4.0", settings.Current.ConsentNotice.AppVersion);
        Assert.Equal(new[] { true }, closed);
        // "Never shown again": the App gate is exactly ConsentNotice != null on the next launch.
        Assert.True(settings.Current.ConsentNotice is not null);
    }

    [Fact]
    public void Decline_persists_nothing_and_closes_declined()
    {
        var (vm, settings, closed) = Make();
        vm.DeclineCommand.Execute(null);
        Assert.Equal(0, settings.SaveCount);
        Assert.Null(settings.Current.ConsentNotice);            // next launch shows the notice again
        Assert.Equal(new[] { false }, closed);
    }

    [Fact]
    public void Text_carries_the_local_summary_and_the_legal_responsibility_statement()
    {
        var (vm, _, _) = Make();
        Assert.Contains("never leave your computer", vm.SummaryText);
        Assert.Contains("Recording others is your responsibility", vm.ResponsibilityText);
        Assert.Contains("two-party / all-party consent", vm.ResponsibilityText);
        Assert.Contains("disclosing the recording to the other participants is up to you",
            vm.ResponsibilityText);
    }
}
```

- [ ] Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ConsentViewModelTests"` — expected: **build error CS0246** (`ConsentViewModel` not found).

- [ ] Implement `src/LocalScribe.App/ViewModels/ConsentViewModel.cs` (full contents — WPF-free):

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;

namespace LocalScribe.App.ViewModels;

/// <summary>First-run consent notice VM (design 6.3). WPF-free. The wording reuses the README
/// "Privacy" draft language. Accept persists consentNotice { acknowledgedAtUtc, appVersion } as
/// an additive settings field, then raises Closed(true); Decline raises Closed(false) and
/// persists nothing - the App layer shuts down. Detection is field-absence
/// (settings.Current.ConsentNotice is null), never file-absence; Record is never re-gated
/// after acceptance (manual-only start remains the consent posture).</summary>
public sealed partial class ConsentViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly string _appVersion;

    /// <summary>Raised exactly once: true after the acknowledgment is persisted, false on
    /// decline. The dialog closes on it; the App shuts down on false.</summary>
    public event Action<bool>? Closed;

    public ConsentViewModel(ISettingsService settings, TimeProvider time, string appVersion)
        => (_settings, _time, _appVersion) = (settings, time, appVersion);

    public string Title { get; } = "Before you record";

    public string SummaryText { get; } =
        "LocalScribe records your microphone and the meeting's audio, transcribes them with a "
        + "locally-run model, and stores everything on this machine. Audio and transcripts "
        + "never leave your computer; nothing is uploaded anywhere. A visible tray indicator "
        + "(and an on-screen overlay) shows whenever recording is active.";

    public string ResponsibilityText { get; } =
        "Recording others is your responsibility. Many jurisdictions require the consent of "
        + "some or all parties before a conversation may be recorded (two-party / all-party "
        + "consent). LocalScribe makes the recording state obvious but cannot enforce the law "
        + "or obtain consent for you - disclosing the recording to the other participants is "
        + "up to you.";

    [RelayCommand]
    private async Task AcceptAsync()
    {
        await _settings.SaveAsync(_settings.Current with
        {
            ConsentNotice = new ConsentSetting
            { AcknowledgedAtUtc = _time.GetUtcNow(), AppVersion = _appVersion },
        }, CancellationToken.None);
        Closed?.Invoke(true);
    }

    [RelayCommand]
    private void Decline() => Closed?.Invoke(false);
}
```

- [ ] Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ConsentViewModelTests"` — expected: **3 facts PASS**.
- [ ] Commit: `git add -A && git commit -m "feat: first-run consent VM - persist ack {time, version}, decline persists nothing"`

- [ ] Create `src/LocalScribe.App/ConsentDialog.xaml` (full contents):

```xml
<ui:FluentWindow x:Class="LocalScribe.App.ConsentDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        Title="LocalScribe - Before you record" Width="540" SizeToContent="Height"
        WindowBackdropType="Mica" ExtendsContentIntoTitleBar="False"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize" Topmost="True">
    <StackPanel Margin="24">
        <TextBlock Text="{Binding Title}" FontSize="20" FontWeight="SemiBold" Margin="0,0,0,12" />
        <TextBlock Text="{Binding SummaryText}" TextWrapping="Wrap" Margin="0,0,0,16" />
        <Border Background="#33FF8C00" CornerRadius="6" Padding="12" Margin="0,0,0,20">
            <TextBlock Text="{Binding ResponsibilityText}" TextWrapping="Wrap" FontWeight="SemiBold" />
        </Border>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Decline and exit" Command="{Binding DeclineCommand}" Margin="0,0,8,0" />
            <Button Content="I understand - continue" Command="{Binding AcceptCommand}" />
        </StackPanel>
    </StackPanel>
</ui:FluentWindow>
```

- [ ] Create `src/LocalScribe.App/ConsentDialog.xaml.cs` (full contents):

```csharp
using LocalScribe.App.ViewModels;

namespace LocalScribe.App;

/// <summary>Humble shell: shown modally from App.OnStartup BEFORE the tray exists (design 6.3).
/// ShowDialog() returns true only on Accept; Decline AND closing via the title bar both read as
/// not-accepted (DialogResult stays false/null), so the App shuts down - a dismissed notice is
/// never treated as consent.</summary>
public partial class ConsentDialog
{
    public ConsentDialog(ConsentViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += accepted =>
        {
            DialogResult = accepted;
            Close();
        };
    }
}
```

- [ ] Build + gate: `dotnet build LocalScribe.slnx` (expected: 0 warnings) then `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` (expected: all green).
- [ ] Commit: `git add -A && git commit -m "feat: consent dialog FluentWindow - modal, decline-or-dismiss reads as not accepted"`

---

### Task 23: Recovery scan + index rebuild startup wiring + notices

**Notice-surface decision:** I read `TrayIconHost.OnNoticeRaised` (`src/LocalScribe.App/TrayIconHost.cs:110`, a private `_icon.ShowNotification("LocalScribe", notice)`) and `SessionViewModel.NoticeRaised` (`src/LocalScribe.App/ViewModels/SessionViewModel.cs:42`, which is fed by controller Notices). The recovery scan is app-level, not session-level, so raising a fake controller Notice through SessionViewModel would be a lie. **Chosen: a thin tray hook** — `TrayIconHost.ShowNotice(string)` delegating to the exact same `_icon.ShowNotification` call the notice pipeline uses. Same balloon surface, no fake session event.

**ScanCompleted signal decision:** a small, fully-testable `StartupOrchestrator` (WPF-free, delegate-injected so a TaskCompletionSource-gated fake needs no MaintenanceService subclassing) exposing `Task ScanCompleted`; **plus** the additive `MaintenanceService.StartupScanTask` property the assignment offers, set by App.OnStartup to the running scan — that is the cross-group seam SessionsPageViewModel awaits (`await maintenance.StartupScanTask ?? Task.CompletedTask`) to clear its "checking for interrupted sessions..." banner. Additive only; no locked member is renamed or reshaped.

**Files:**
- Create: `src/LocalScribe.App/Services/StartupOrchestrator.cs`
- Create: `src/LocalScribe.App/Services/TrayNoticeReporter.cs`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs` (add one additive property; file created by the earlier MaintenanceService task)
- Modify: `src/LocalScribe.App/TrayIconHost.cs` (add `ShowNotice`, immediately after `OnNoticeRaised` at ~line 110)
- Create: `tests/LocalScribe.App.Tests/StartupOrchestratorTests.cs`

**Interfaces:**
- *Consumes:* `MaintenanceService.RecoverAllAsync(CancellationToken) : Task<RecoveryScanResult>` and `RebuildIndexAsync(CancellationToken) : Task<MattersIndex>` (locked); `RecoveryScanResult(IReadOnlyList<string> RecoveredIds, IReadOnlyList<(string Id, string Error)> Failures)` (locked); `IUiErrorReporter` (locked); `SessionViewModel` + `LiveTestDoubles` (existing, for the non-blocking-Start proof); `FakeUiErrorReporter` (Task 21).
- *Produces:*
```csharp
// src/LocalScribe.App/Services/StartupOrchestrator.cs
public sealed class StartupOrchestrator
{
    public StartupOrchestrator(Func<CancellationToken, Task<RecoveryScanResult>> recoverAll,
        Func<CancellationToken, Task> rebuildIndex, IUiErrorReporter errors, Action<string> notify);
    public Task ScanCompleted { get; }              // completes when scan + rebuild are done (even on fault)
    public Task RunAsync(CancellationToken ct);
}
// src/LocalScribe.App/Services/TrayNoticeReporter.cs
public sealed class TrayNoticeReporter(Action<string> notify) : IUiErrorReporter;
// MaintenanceService (additive): public Task? StartupScanTask { get; set; }
// TrayIconHost (additive):       public void ShowNotice(string notice);
```

**Steps:**

- [ ] Write the failing tests `tests/LocalScribe.App.Tests/StartupOrchestratorTests.cs` (full contents):

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class StartupOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-startup-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static RecoveryScanResult Result(string[] recovered, params (string Id, string Error)[] failures)
        => new(recovered, failures);

    [Fact]
    public async Task Recovered_sessions_notify_once_and_rebuild_runs_after_the_scan()
    {
        var order = new List<string>();
        var notices = new List<string>();
        var orchestrator = new StartupOrchestrator(
            recoverAll: _ => { order.Add("scan"); return Task.FromResult(Result(new[] { "a", "b" })); },
            rebuildIndex: _ => { order.Add("rebuild"); return Task.CompletedTask; },
            new FakeUiErrorReporter(), notices.Add);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "scan", "rebuild" }, order);       // design 4.3: rebuild AFTER the scan
        Assert.Equal(new[] { "Recovered 2 interrupted session(s)" }, notices);
        Assert.True(orchestrator.ScanCompleted.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Nothing_recovered_means_no_balloon_but_rebuild_still_runs()
    {
        var notices = new List<string>();
        bool rebuilt = false;
        var orchestrator = new StartupOrchestrator(
            _ => Task.FromResult(Result(Array.Empty<string>())),
            _ => { rebuilt = true; return Task.CompletedTask; },
            new FakeUiErrorReporter(), notices.Add);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Empty(notices);
        Assert.True(rebuilt);
    }

    [Fact]
    public async Task Per_session_failures_are_reported_individually_not_swallowed()
    {
        var errors = new FakeUiErrorReporter();
        bool rebuilt = false;
        var orchestrator = new StartupOrchestrator(
            _ => Task.FromResult(Result(new[] { "ok-1" }, ("bad-1", "torn file"), ("bad-2", "locked"))),
            _ => { rebuilt = true; return Task.CompletedTask; },
            errors, _ => { });

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(2, errors.Reports.Count);
        Assert.Contains(errors.Reports, r => r.Context.Contains("bad-1") && r.Ex.Message.Contains("torn file"));
        Assert.Contains(errors.Reports, r => r.Context.Contains("bad-2") && r.Ex.Message.Contains("locked"));
        Assert.True(rebuilt);                                   // failures never stop the rebuild
        Assert.True(orchestrator.ScanCompleted.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_faulted_scan_is_reported_and_ScanCompleted_still_completes()
    {
        var errors = new FakeUiErrorReporter();
        var orchestrator = new StartupOrchestrator(
            _ => throw new IOException("storage offline"),
            _ => Task.CompletedTask,
            errors, _ => { });

        await orchestrator.RunAsync(CancellationToken.None);    // must not throw

        Assert.Single(errors.Reports);
        Assert.True(orchestrator.ScanCompleted.IsCompleted);    // the sessions page banner always clears
    }

    [Fact]
    public async Task Start_is_never_blocked_by_a_slow_scan()
    {
        // TaskCompletionSource-gated fake: the scan is "in flight" until we say otherwise.
        var gate = new TaskCompletionSource<RecoveryScanResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = new StartupOrchestrator(
            _ => gate.Task, _ => Task.CompletedTask, new FakeUiErrorReporter(), _ => { });
        Task scan = orchestrator.RunAsync(CancellationToken.None);

        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());

        await vm.StartCommand.ExecuteAsync(null);               // recording while the scan hangs
        Assert.Equal(SessionState.Recording, vm.State);
        Assert.False(orchestrator.ScanCompleted.IsCompleted);
        await vm.StopCommand.ExecuteAsync(null);

        gate.SetResult(Result(Array.Empty<string>()));
        await scan;
        Assert.True(orchestrator.ScanCompleted.IsCompletedSuccessfully);
    }

    [Fact]
    public void TrayNoticeReporter_formats_context_and_message_into_the_notify_sink()
    {
        var notices = new List<string>();
        var reporter = new TrayNoticeReporter(notices.Add);
        reporter.Report("Recovery of session x", new InvalidOperationException("torn"));
        reporter.Info("hello");
        Assert.Equal(new[] { "Recovery of session x: torn", "hello" }, notices);
    }
}
```

- [ ] Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~StartupOrchestratorTests"` — expected: **build error CS0246** (`StartupOrchestrator`, `TrayNoticeReporter` not found).

- [ ] Implement `src/LocalScribe.App/Services/StartupOrchestrator.cs` (full contents — WPF-free):

```csharp
namespace LocalScribe.App.Services;

/// <summary>Startup background sequence (design 7.1/4.3): recovery scan first, index rebuild
/// strictly AFTER it. Runs as a background task kicked off post-tray-up (Task 24); NEVER blocks
/// Start or the UI - it merely reads/writes through MaintenanceService's per-session queue.
/// Delegate-injected (not MaintenanceService itself) so tests gate it on a
/// TaskCompletionSource. Recovered count -> one tray balloon via notify; per-session failures
/// -> IUiErrorReporter.Report each, never swallowed, never fatal to the rebuild. ScanCompleted
/// always completes (even on fault/cancel) - the Sessions page "checking for interrupted
/// sessions..." banner must always clear.</summary>
public sealed class StartupOrchestrator
{
    private readonly Func<CancellationToken, Task<RecoveryScanResult>> _recoverAll;
    private readonly Func<CancellationToken, Task> _rebuildIndex;
    private readonly IUiErrorReporter _errors;
    private readonly Action<string> _notify;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupOrchestrator(Func<CancellationToken, Task<RecoveryScanResult>> recoverAll,
        Func<CancellationToken, Task> rebuildIndex, IUiErrorReporter errors, Action<string> notify)
        => (_recoverAll, _rebuildIndex, _errors, _notify) = (recoverAll, rebuildIndex, errors, notify);

    public Task ScanCompleted => _done.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var result = await _recoverAll(ct);
            if (result.RecoveredIds.Count > 0)
                _notify($"Recovered {result.RecoveredIds.Count} interrupted session(s)");
            foreach ((string id, string error) in result.Failures)
                _errors.Report("Recovery of session " + id, new InvalidOperationException(error));
            await _rebuildIndex(ct);        // design 4.3: launch rebuild runs AFTER the scan
        }
        catch (OperationCanceledException) { }   // app shutting down mid-scan - nothing to report
        catch (Exception ex) { _errors.Report("Startup scan", ex); }
        finally { _done.TrySetResult(); }
    }
}
```

- [ ] Implement `src/LocalScribe.App/Services/TrayNoticeReporter.cs` (full contents — WPF-free):

```csharp
namespace LocalScribe.App.Services;

/// <summary>IUiErrorReporter for startup/background work (design 7.5: background operations
/// surface via tray balloon, not an InfoBar). WPF-free: App injects a dispatcher-marshaled
/// TrayIconHost.ShowNotice hook as the notify sink.</summary>
public sealed class TrayNoticeReporter(Action<string> notify) : IUiErrorReporter
{
    public void Report(string context, Exception ex) => notify(context + ": " + ex.Message);
    public void Info(string message) => notify(message);
}
```

- [ ] Modify `src/LocalScribe.App/Services/MaintenanceService.cs`: inside `public sealed class MaintenanceService`, directly below its constructor/fields, add this additive property (no locked member changes):

```csharp
    /// <summary>Set by App.OnStartup to the in-flight startup scan (StartupOrchestrator.RunAsync).
    /// SessionsPageViewModel awaits it (null-coalesced to Task.CompletedTask) to clear the
    /// "checking for interrupted sessions..." banner; null in compositions with no startup scan
    /// (unit tests). Additive - not part of the locked Stage 4 surface.</summary>
    public Task? StartupScanTask { get; set; }
```

- [ ] Modify `src/LocalScribe.App/TrayIconHost.cs`: directly below `OnNoticeRaised` (line 110), add:

```csharp
    /// <summary>Thin app-level hook into the same balloon surface OnNoticeRaised uses - lets
    /// startup/background work (recovery scan, index rebuild failures) surface tray notices
    /// without faking a controller Notice through SessionViewModel.</summary>
    public void ShowNotice(string notice) => _icon.ShowNotification("LocalScribe", notice);
```

- [ ] Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~StartupOrchestratorTests"` — expected: **6 facts PASS**.
- [ ] Full gate: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` — expected: all green.
- [ ] Commit: `git add -A && git commit -m "feat: startup recovery-scan orchestration - balloon on recover, per-id failure reports, rebuild after scan, Start never blocked"`

---

### Task 24: Startup integration + OnExit

Stitches the final `App.xaml.cs` startup order — (1) single-instance guard (Task 12's API, wired by Task 14, carried into the final contents), (2) `CompositionRoot.Build()` -> `AppComposition`, (3) consent gate (Task 22), (4) page-VM construction + read-view opening + tray/MainWindow factory (Tasks 14-21), (5) overlay + timer as in 3b, (6) main window shown at launch, (7) startup-scan kickoff (Task 23) with the Sessions page's `IsScanning` banner, (8) OnExit disposal — plus the final `CompositionRoot.cs` over the Task 10 seam and the page-provider widening of Task 14's `MainWindow`.

Deliberately ABSENT from this task: `src/LocalScribe.App/Services/SingleInstance.cs` and `tests/LocalScribe.App.Tests/SingleInstanceTests.cs` are Task 12's files (locked surface: `TryAcquire(string, Action) : SingleInstance?`, `bool SignalExisting(string)`, kernel names `Local\<name>` / `Local\<name>-activate`), and Task 14 already wired TryAcquire + the SignalExisting-and-exit path into `App.OnStartup`. This task only consumes them.

**Files:**
- Modify: `src/LocalScribe.App/CompositionRoot.cs` (complete final contents below)
- Modify: `src/LocalScribe.App/App.xaml.cs` (complete final contents below)
- Create: `src/LocalScribe.App/Services/StaticPageProvider.cs` (page-VM injection bridge for NavigationView)
- Modify: `src/LocalScribe.App/MainWindow.xaml.cs` (Task 14's file — the ctor gains the page provider; exact edits below)
- Modify: `src/LocalScribe.App/Pages/SettingsPage.xaml.cs` (Task 14's shell — becomes the host for Task 21's Settings UserControl; exact contents below)
- Modify: `tests/LocalScribe.App.Tests/CompositionRootTests.cs` (first test re-destructures against `AppComposition`)

**Interfaces:**
- *Consumes (locked, exact — from the producing tasks):*
  - Task 12: `SingleInstance.TryAcquire(string name, Action onActivateRequested) : SingleInstance?`; `SingleInstance.SignalExisting(string name) : bool`; `Dispose()`.
  - Task 10: `SettingsService(string settingsJsonPath, Settings initial)`; `SessionController(StoragePaths, Func<Settings>, IEngineFactory, Func<ISpeechProbabilityModel>, IHardwareProbe, ICaptureSourceProvider, Func<IClock>, TimeProvider, string)`; `WasapiCaptureSourceProvider(Func<Settings>, IAudioSessionScanner)`.
  - Task 7: `ShellRecycleBin()` (LocalScribe.App.Services); `MatterDeleter(StoragePaths, IRecycleBin)` (LocalScribe.Core.Storage).
  - Task 9: `MaintenanceService(StoragePaths, ISettingsService, IRecycleBin, TimeProvider)`.
  - Task 11: `WindowStateStore(string path)` with keyed `Load`/`Save`.
  - Task 14: `MainWindow(MainWindowViewModel, WindowStateStore, ISettingsService)` (widened below with a fourth parameter); `TrayIconHost(SessionViewModel, TranscriptLinesViewModel, StoragePaths, ISettingsService, Func<MainWindow> mainWindowFactory)` + `OpenMainWindow()`; `InfoBarErrorReporter(Action<Action> dispatch)`; `MainWindowViewModel(InfoBarErrorReporter)`.
  - Tasks 15-17: `SessionsPageViewModel(MaintenanceService, SessionViewModel, WindowRegistry, IUiErrorReporter, Action<Action> dispatch, TimeProvider time, Action<string> revealInExplorer)` + `IsScanning` + `RefreshCommand` + `event Action<string>? OpenReadViewRequested`; `MetadataEditorViewModel(MaintenanceService, SessionViewModel, IUiErrorReporter, Action<Action> dispatch, TimeProvider time)`; `Pages.SessionsPage(SessionsPageViewModel, MetadataEditorViewModel)` (Task 17's delete-confirm dialog lives in the page code-behind — nothing to wire here).
  - Task 18: `MattersPageViewModel(StoragePaths, MaintenanceService, MatterDeleter, IUiErrorReporter, Action<Action> dispatch, TimeProvider time)` + `event Action<string>? JumpToSessionRequested`; `Pages.MattersPage(MattersPageViewModel)`.
  - Tasks 19-20: `ReadViewViewModel(MaintenanceService, StoragePaths, ISettingsService, IUiErrorReporter, IDualAudioPlayer, Action<Action>, TimeProvider)` + `Dispose()`; `ReadViewWindow(ReadViewViewModel, string sessionId, WindowRegistry, WindowStateStore, ISettingsService)`; `MediaPlayerDualAudioPlayer()` (one per window; the VM's Dispose owns it).
  - Task 21: `SettingsPageViewModel(ISettingsService, MaintenanceService, ILaunchAtLogin, Func<string?> pickFolder, Action<string> openFolder, IUiErrorReporter, Action<Action> dispatch, string? modelsRoot = null)`; `RegistryLaunchAtLogin()`; `SettingsPage(SettingsPageViewModel)` (UserControl, namespace `LocalScribe.App`).
  - Task 22: `ConsentViewModel(ISettingsService, TimeProvider, string appVersion)`; `ConsentDialog(ConsentViewModel)` — `ShowDialog() == true` only on Accept.
  - Task 23: `StartupOrchestrator(Func<CancellationToken, Task<RecoveryScanResult>> recoverAll, Func<CancellationToken, Task> rebuildIndex, IUiErrorReporter errors, Action<string> notify)` + `ScanCompleted` + `RunAsync(ct)`; `TrayNoticeReporter(Action<string>)`; `TrayIconHost.ShowNotice(string)`; `MaintenanceService.StartupScanTask`.
  - WPF-UI 4.0.3, verified against the installed packages' XML docs: `Wpf.Ui.Abstractions.INavigationViewPageProvider { object? GetPage(Type pageType); }` and `NavigationView.SetPageProviderService(INavigationViewPageProvider)` — the supported way to make `TargetPageType` navigation resolve OUR page instances instead of the parameterless-ctor default activator (Tasks 15-21 gave every page a VM-taking ctor). `Wpf.Ui.Abstractions` flows transitively from the `WPF-UI` 4.0.3 package reference.
- *Produces:*
```csharp
// src/LocalScribe.App/CompositionRoot.cs
public sealed record AppComposition(SessionController Controller, ISettingsService Settings,
    StoragePaths Paths, MaintenanceService Maintenance, WindowRegistry Windows,
    IRecycleBin RecycleBin, string AppVersion);
public static class CompositionRoot { public static AppComposition Build(); }
// src/LocalScribe.App/Services/StaticPageProvider.cs
public sealed class StaticPageProvider : INavigationViewPageProvider
{ public StaticPageProvider(IReadOnlyDictionary<Type, object> pages); public object? GetPage(Type pageType); }
// src/LocalScribe.App/MainWindow.xaml.cs - the final widened ctor (Task 14's three parameters plus the provider):
// public MainWindow(MainWindowViewModel vm, WindowStateStore stateStore, ISettingsService settings,
//     INavigationViewPageProvider pageProvider)
// src/LocalScribe.App/Pages/SettingsPage.xaml.cs - the shell's parameterless ctor is replaced by:
// public SettingsPage(ViewModels.SettingsPageViewModel vm)   // hosts Task 21's UserControl
// src/LocalScribe.App/App.xaml.cs - the final composed App (startup order in the preamble); no new public surface.
```

**Steps:**

- [ ] **Create `src/LocalScribe.App/Services/StaticPageProvider.cs`** (full contents — depends only on `Wpf.Ui.Abstractions`, no WPF types, so it sits in Services within the house rule):

```csharp
using Wpf.Ui.Abstractions;

namespace LocalScribe.App.Services;

/// <summary>INavigationViewPageProvider over a fixed Type-to-instance map. MainWindow hands it
/// to NavigationView (SetPageProviderService), so TargetPageType navigation resolves the pages
/// App.OnStartup built WITH their ViewModels instead of reflecting over parameterless ctors
/// (Tasks 15-21 gave every page a VM-taking ctor). One provider per MainWindow open: pages are
/// re-created per window - a WPF element cannot be re-hosted across windows - while the VMs
/// inside them are singletons, so page state survives close/reopen.</summary>
public sealed class StaticPageProvider : INavigationViewPageProvider
{
    private readonly IReadOnlyDictionary<Type, object> _pages;

    public StaticPageProvider(IReadOnlyDictionary<Type, object> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _pages = pages;
    }

    /// <summary>Null for unknown types: NavigationView then surfaces its own failure instead
    /// of this class masking a wiring mistake with a bogus page.</summary>
    public object? GetPage(Type pageType) => _pages.TryGetValue(pageType, out var page) ? page : null;
}
```

- [ ] **Widen Task 14's `src/LocalScribe.App/MainWindow.xaml.cs`** with the page provider. Two exact edits. (The solution stops compiling here — Task 14's App.xaml.cs factory still calls the 3-arg ctor — and compiles again after the App.xaml.cs replacement below; land the steps from here to there as one unit.)
  1. Add `using Wpf.Ui.Abstractions;` directly below the existing `using Wpf.Ui.Controls;`.
  2. Replace the ctor opening:

```csharp
    public MainWindow(MainWindowViewModel vm, WindowStateStore stateStore, ISettingsService settings)
    {
        InitializeComponent();
        (_vm, _stateStore, _settings) = (vm, stateStore, settings);
```

with:

```csharp
    public MainWindow(MainWindowViewModel vm, WindowStateStore stateStore, ISettingsService settings,
        INavigationViewPageProvider pageProvider)
    {
        InitializeComponent();
        // Tasks 15-21 gave the pages VM-taking ctors, so NavigationView's default
        // parameterless-ctor activator can no longer construct them; the provider (built per
        // window open by App.OnStartup with the real VMs) resolves TargetPageType navigation,
        // including the Loaded-time Navigate(typeof(Pages.SessionsPage)) below.
        RootNav.SetPageProviderService(pageProvider);
        (_vm, _stateStore, _settings) = (vm, stateStore, settings);
```

  Nothing else in the file changes.

- [ ] **Replace `src/LocalScribe.App/Pages/SettingsPage.xaml.cs`** (Task 14's shell) with the complete final contents — the Settings navigation target hosts Task 21's UserControl (`Pages/SettingsPage.xaml` itself is untouched; its placeholder Grid is replaced at construction):

```csharp
namespace LocalScribe.App.Pages;

/// <summary>Navigation host for the Settings section. Task 21 built the real Settings UI as
/// the LocalScribe.App.SettingsPage UserControl (deliberate name reuse in a different
/// namespace); this page stays the type MainWindow.xaml's TargetPageType names - so the
/// provider-returned instance type always matches the requested page type - and simply hosts
/// the UserControl as its Content. The parameterless shell ctor is gone along with the
/// default activator that needed it: StaticPageProvider constructs this page.</summary>
public partial class SettingsPage
{
    public SettingsPage(ViewModels.SettingsPageViewModel vm)
    {
        InitializeComponent();
        Content = new LocalScribe.App.SettingsPage(vm);
    }
}
```

- [ ] **Replace `src/LocalScribe.App/CompositionRoot.cs`** with the complete final contents. The settings-load expression stays byte-identical to today's (both deadlock-regression tests in `CompositionRootTests` pin it); `SettingsService` is constructed per Task 10's locked ctor `(string settingsJsonPath, Settings initial)`; the Task 10 `Func<Settings>` seam and the Stage 4 services complete the graph:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.App;

/// <summary>Everything App.OnStartup and MainWindow need, built once. StoragePaths is
/// constructed exactly once from the settings loaded at startup - a storageRoot change is
/// restart-required by design (design 6.2); everything else resolves settings live via
/// ISettingsService.Current.</summary>
public sealed record AppComposition(
    SessionController Controller,
    ISettingsService Settings,
    StoragePaths Paths,
    MaintenanceService Maintenance,
    WindowRegistry Windows,
    IRecycleBin RecycleBin,
    string AppVersion);

/// <summary>Builds the app's object graph over the real adapters. Construction only - no
/// capture, no models touched until StartAsync. Settings load synchronously at startup
/// (small local file).</summary>
public static class CompositionRoot
{
    public static AppComposition Build()
    {
        string settingsPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "settings.json");
        // Build() runs inline from App.OnStartup, i.e. on the WPF UI thread under a
        // DispatcherSynchronizationContext. Core's storage helpers await with no
        // ConfigureAwait(false), so a plain "LoadOrDefaultAsync(...).GetAwaiter().GetResult()"
        // here would deadlock whenever settings.json exists and the read completes async: the
        // continuation would try to post back to this same UI thread, which is already blocked
        // in GetResult(). Task.Run moves the whole async call onto a pool thread where
        // SynchronizationContext.Current is null, so its continuations never try to post back
        // here - GetResult() then only blocks until the pool work finishes.
        var loaded = Task.Run(() => new SettingsStore(settingsPath).LoadOrDefaultAsync(default))
            .GetAwaiter().GetResult();

        // SettingsService FIRST (Task 10's locked ctor: the settings PATH plus the loaded
        // snapshot) - everything downstream resolves settings through it.
        var settingsService = new SettingsService(settingsPath, loaded);
        var paths = new StoragePaths(settingsService.Current.StorageRoot);   // once; restart-required
        string appVersion = typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        Func<Settings> current = () => settingsService.Current;              // Task 10 seam

        var controller = new SessionController(paths, current, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(),
            new WasapiCaptureSourceProvider(current, new WasapiSessionScanner()),
            () => new StopwatchClock(), TimeProvider.System, appVersion);

        var recycleBin = new ShellRecycleBin();
        var maintenance = new MaintenanceService(paths, settingsService, recycleBin, TimeProvider.System);
        return new AppComposition(controller, settingsService, paths, maintenance,
            new WindowRegistry(), recycleBin, appVersion);
    }
}
```

- [ ] Update `tests/LocalScribe.App.Tests/CompositionRootTests.cs` lines 13-20 — replace the first test's body (the two deadlock-regression tests below it stay byte-identical):

```csharp
    [Fact]
    public void Build_produces_an_idle_controller_and_expanded_paths()
    {
        var comp = CompositionRoot.Build();
        Assert.Equal(SessionState.Idle, comp.Controller.State);
        Assert.False(comp.Paths.Root.Contains('%'));     // env vars expanded by StoragePaths
        Assert.NotNull(comp.Settings.Current);
        Assert.NotNull(comp.Maintenance);
        Assert.NotNull(comp.Windows);
        Assert.False(string.IsNullOrEmpty(comp.AppVersion));
    }
```

- [ ] **Replace `src/LocalScribe.App/App.xaml.cs`** with the complete final contents (`App.xaml` itself is unchanged — `ShutdownMode="OnExplicitShutdown"` already suits the modal-consent-then-maybe-shutdown flow). This subsumes Task 10's call-site edit, Task 14's single-instance/factory wiring, and adds the page-VM graph:

```csharp
using System.Windows;
using LocalScribe.App.Services;
using LocalScribe.Core.Storage;
using Whisper.net.LibraryLoader;
namespace LocalScribe.App;

public partial class App : Application
{
    private const string InstanceName = "LocalScribe";

    private SingleInstance? _singleInstance;
    private TrayIconHost? _tray;
    private OverlayWindow? _overlay;
    private ViewModels.OverlayViewModel? _overlayVm;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private readonly CancellationTokenSource _shutdownCts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Safety net: CommunityToolkit's AsyncRelayCommand (AwaitAndThrowIfFailed) rethrows a
        // faulted Stop/Pause command's exception back on the dispatcher. Without this handler
        // that becomes an unhandled exception that crashes the whole tray app. Stage 7 can add
        // real logging here; for now, swallow it - the per-command try/catch (see TrayIconHost
        // Exit handler) is the primary path for surfacing errors to the user.
        DispatcherUnhandledException += (_, ex) => { ex.Handled = true; };

        // Host responsibility (see LiveRunner): native backend order, once per process.
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];

        // (1) Single-instance guard (design 7.2, Task 12's exact API): the second instance
        // pings the holder and exits before building anything. The activate callback fires on
        // the guard's background wait thread, so it is dispatch-wrapped as SingleInstance
        // requires.
        _singleInstance = SingleInstance.TryAcquire(InstanceName,
            onActivateRequested: () => Dispatcher.BeginInvoke(() => _tray?.OpenMainWindow()));
        if (_singleInstance is null)
        {
            // Return value intentionally discarded: reachable holder or not, this instance
            // exits either way (SignalExisting never throws, by Task 12's contract).
            _ = SingleInstance.SignalExisting(InstanceName);
            Shutdown();
            return;
        }

        // (2) Composition root (Task 10 seam inside): the controller and capture provider
        // resolve settings via Func<Settings> at StartAsync, so a save applies at the NEXT
        // Start. Held in a local so every closure below captures a non-null graph.
        var comp = CompositionRoot.Build();

        // (3) First-run consent (design 6.3, Task 22): modal, BEFORE any tray/overlay/window
        // exists. Detection is field-absence, not file-absence; Decline (or dismissing the
        // dialog) shuts the app down without persisting anything.
        if (comp.Settings.Current.ConsentNotice is null)
        {
            var consentVm = new ViewModels.ConsentViewModel(
                comp.Settings, TimeProvider.System, comp.AppVersion);
            if (new ConsentDialog(consentVm).ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        // (4) Live-session VMs (3b) + Stage 4 page VMs, all sharing one dispatch seam.
        // SessionViewModel still takes a plain Settings snapshot; Stage 4 policy is
        // next-Start effect anyway (design 6.2).
        Action<Action> dispatch = a => Dispatcher.BeginInvoke(a);
        var session = new ViewModels.SessionViewModel(comp.Controller, comp.Settings.Current,
            dispatch);
        var lines = new ViewModels.TranscriptLinesViewModel(comp.Controller, dispatch);

        // One WindowStateStore serves overlay + main + read views (keyed entries in
        // window-state.json; spec 7: throwaway UI state, NOT settings).
        string stateStorePath = System.IO.Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "window-state.json");
        var windowState = new ViewModels.WindowStateStore(stateStorePath);

        // Singleton VMs: the error queue and every page's state survive MainWindow
        // close/reopen (the WINDOW is re-created per open; these are not).
        var errors = new InfoBarErrorReporter(dispatch);
        var mainVm = new ViewModels.MainWindowViewModel(errors);
        var sessionsVm = new ViewModels.SessionsPageViewModel(comp.Maintenance, session,
            comp.Windows, errors, dispatch, TimeProvider.System,
            revealInExplorer: id =>
            {
                // Same shell-out TrayIconHost's "Open sessions folder" uses; the path is
                // built via StoragePaths (spec 3.2), never assembled by the VM.
                string dir = comp.Paths.SessionDir(id);
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", dir);
            });
        var editorVm = new ViewModels.MetadataEditorViewModel(comp.Maintenance, session,
            errors, dispatch, TimeProvider.System);
        var mattersVm = new ViewModels.MattersPageViewModel(comp.Paths, comp.Maintenance,
            new MatterDeleter(comp.Paths, comp.RecycleBin), errors, dispatch, TimeProvider.System);
        var settingsVm = new ViewModels.SettingsPageViewModel(comp.Settings, comp.Maintenance,
            new RegistryLaunchAtLogin(),
            pickFolder: () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                { Title = "Choose the LocalScribe storage folder" };
                return dialog.ShowDialog() == true ? dialog.FolderName : null;
            },
            openFolder: p => System.Diagnostics.Process.Start("explorer.exe", p),
            errors, dispatch);

        // Read views (Tasks 19/20): one window per session id; a second request activates the
        // existing window instead of duplicating. WindowRegistry keeps the close hooks for the
        // delete flow (Task 17); this map adds the activate half the registry does not carry.
        var readViews = new Dictionary<string, ReadViewWindow>(StringComparer.Ordinal);
        Action<string> openReadView = sessionId =>
        {
            if (readViews.TryGetValue(sessionId, out var existing))
            {
                existing.Activate();
                return;
            }
            var readVm = new ViewModels.ReadViewViewModel(comp.Maintenance, comp.Paths,
                comp.Settings, errors, new MediaPlayerDualAudioPlayer(), dispatch,
                TimeProvider.System);
            var window = new ReadViewWindow(readVm, sessionId, comp.Windows, windowState,
                comp.Settings);
            readViews[sessionId] = window;
            window.Closed += (_, _) => { readViews.Remove(sessionId); readVm.Dispose(); };
            window.Show();
        };
        sessionsVm.OpenReadViewRequested += openReadView;
        // Matters-page "Open" jump: concretely, the session's read view. In-list selection is
        // a Sessions-page navigation concern MainWindow does not expose; the read view IS the
        // session, which is what the organizer jump is for (design 4.1).
        mattersVm.JumpToSessionRequested += openReadView;

        // Tray with the re-creating MainWindow factory (Task 14's 5-arg ctor; MainWindow
        // widened by this task). Pages are humble shells built fresh per window open - a WPF
        // element cannot be re-hosted across windows - around the singleton VMs above.
        _tray = new TrayIconHost(session, lines, comp.Paths, comp.Settings,
            mainWindowFactory: () => new MainWindow(mainVm, windowState, comp.Settings,
                new StaticPageProvider(new Dictionary<Type, object>
                {
                    [typeof(Pages.SessionsPage)] = new Pages.SessionsPage(sessionsVm, editorVm),
                    [typeof(Pages.MattersPage)] = new Pages.MattersPage(mattersVm),
                    [typeof(Pages.SettingsPage)] = new Pages.SettingsPage(settingsVm),
                })));

        // (5) Overlay singleton (design decision 12): shown/hidden - never closed - as
        // OverlayViewModel.IsVisible flips with State. Timer wiring as in 3b.
        _overlayVm = new ViewModels.OverlayViewModel(session, comp.Settings.Current);
        _overlay = new OverlayWindow(_overlayVm, windowState);
        _overlayVm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ViewModels.OverlayViewModel.IsVisible)) return;
            if (_overlayVm.IsVisible) _overlay.Show(); else _overlay.Hide();
        };

        _timer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => session.TimerTick();
        _timer.Start();

        // (6) Stage 4: the manager window is the launch surface (the tray remains the consent
        // surface and the only Exit; MainWindow genuinely closes and reopens from the tray).
        _tray.OpenMainWindow();

        // (7) Startup scan (Task 23): recovery scan, then index rebuild, AFTER the tray is up
        // so balloons have somewhere to land; never blocks Start or the UI. The Sessions page
        // shows its "checking for interrupted sessions..." banner until ScanCompleted, which
        // completes even on fault/cancel - the banner always clears.
        Action<string> notify = m => Dispatcher.BeginInvoke(() => _tray?.ShowNotice(m));
        var orchestrator = new StartupOrchestrator(
            recoverAll: ct => comp.Maintenance.RecoverAllAsync(ct),
            rebuildIndex: ct => comp.Maintenance.RebuildIndexAsync(ct),
            new TrayNoticeReporter(notify),
            notify);
        sessionsVm.IsScanning = true;
        comp.Maintenance.StartupScanTask = orchestrator.RunAsync(_shutdownCts.Token);
        _ = orchestrator.ScanCompleted.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            sessionsVm.IsScanning = false;
            sessionsVm.RefreshCommand.Execute(null);   // recovered rows re-list finalized (3.1)
        }), TaskScheduler.Default);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownCts.Cancel();                   // stop an in-flight startup scan politely
        _timer?.Stop();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] Build + full gate: `dotnet build LocalScribe.slnx` (expected: 0 warnings, 0 errors) then `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` — expected: all green, including the updated `CompositionRootTests` (`Build_produces_an_idle_controller_and_expanded_paths` now asserting against `AppComposition`; both deadlock-regression tests untouched and passing) and Task 12's `SingleInstanceTests` (untouched — this task creates no single-instance code).
- [ ] **Testing statement (required by this task):** a startup-ORDER test for `App.xaml.cs` itself is not feasible at VM level — `OnStartup` is WPF lifecycle code (STA, real dispatcher, real tray). The order (single-instance -> consent-before-tray -> tray/pages -> scan-after-tray) is covered by the smoke runbook launch checks: C1 (consent precedes tray), C7 (single-instance activation), C8 (scan balloon after tray-up). Everything `OnStartup` merely composes is unit-tested where it is produced: `SingleInstance` in Task 12, `ConsentViewModel` in Task 22, `StartupOrchestrator`/`TrayNoticeReporter` in Task 23, the page VMs in Tasks 15-21, and `CompositionRoot.Build` above. `StaticPageProvider`, the widened `MainWindow` ctor, and the Settings page host are window-layer glue exercised by the runbook's navigation checks.
- [ ] Commit: `git add -A && git commit -m "feat: startup integration - consent gate before tray, page-VM injection via NavigationView page provider, scan kickoff with sessions-page banner, OnExit disposal"`

---

### Task 25: Docs — specs.md amendments + Stage 4 smoke runbook

Documentation debt from the approved design (design doc section 10 queues exactly five specs amendments) plus the Stage 4 smoke runbook. No code. Two separate `docs:` commits.

**Files:**
- Modify: `docs/specs/localscribe-specs.md` (line hints below from the current file)
- Create: `docs/plans/2026-07-03-stage-4-smoke-runbook.md`

**Interfaces:** none (documentation only). Consumes the design doc section 10 list and the Stage 3b runbook's structure (numbered items, per-item steps/expected/record, dated results section).

**Steps:**

- [ ] **Amendment 4 (schema-version policy — v2->v3 participants).** In `docs/specs/localscribe-specs.md` lines 26-30, the current bullet reads:

> **`session.json` v2→v3 migration:** the user-owned fields move out to a synthesised `meta.json` (§1.4): `title` copies across (then drops from `session.json`), `participants = [self from settings, if any]`, `description = ""`, `medium = app`, `matterIds = []`, `summaryRef = null`. ...

  Replace only the `participants` clause so the bullet reads:

```markdown
- **`session.json` v2→v3 migration:** the user-owned fields move out to a synthesised
  `meta.json` (§1.4): `title` copies across (then drops from `session.json`),
  `participants = []`, `description = ""`, `medium = app`, `matterIds = []`,
  `summaryRef = null`. Migration **never fabricates identity** (2026-07-03 refinement,
  supersedes the earlier "self from settings, if any"): every Stage 4 read path passes
  `selfForMigration: null`, because who was on an old call is not something today's
  `settings.self` knows — the self participant is injected only at recording time by
  SessionBootstrap. `session.json` keeps only system-derived fields and
  gains a `devices` snapshot (§1.2/§12) defaulted to `unknown/legacy` for pre-v3 records.
```

  Also update line 22 (`All 2026-07-02 schema changes are **additive**...`) to `All 2026-07-02 and 2026-07-03 schema changes are **additive** and migrate-on-load; ...`, and append one new bullet at the end of that policy list:

```markdown
- **2026-07-03 additive bumps (Stage 4):** `meta.json` v1→v2, `matter.json` v1→v2, and the
  matters index v1→v2 each add `archived: false`; `settings.json` v2→v3 adds `privacy`
  at its default (`excludeWindowsFromCapture: true`) — `consentNotice` stays absent until
  the user accepts the first-run notice (§7). Nothing else changes.
```

- [ ] **Amendment 1 (section 1.4 — Edited-flag scope) + Amendment 5a (meta archived).** In the section 1.4 example (lines 188-207), change `"schemaVersion": 1,` to `"schemaVersion": 2,` and insert `"archived": false,` after the `"remoteCount": 1,` line. Then replace the closing bullet (lines 233-234), currently:

> `edited`/`lastEditedAtUtc` — flags that any user edit (metadata, correction, or pinned reassignment) has occurred, for UI/audit display.

  with:

```markdown
- `edited`/`lastEditedAtUtc` — flag that a **transcript-content edit** — a text correction
  (§1.6) or a pinned speaker reassignment (§1.3) — has occurred, for UI/audit display.
  (2026-07-03 refinement, supersedes "any user edit": plain metadata edits — title,
  description, medium, matter tags, participants, counts, archived — do **not** flip these
  flags; `EditStore.MarkEditedAsync` remains their only writer.)
- `archived` — v2 (2026-07-03, additive): hides the session from default list views behind a
  "show archived" toggle. Organizational only — nothing leaves disk, no content is affected.
```

- [ ] **Amendment 5b (section 1.5 — matter/index archived).** In the `matter.json` example (lines 243-257): `"schemaVersion": 1,` -> `"schemaVersion": 2,` and insert `"archived": false,` after the `"dateCreatedUtc"` line. In the matters-index example (lines 268-275): `"schemaVersion": 1,` -> `"schemaVersion": 2,` and change the entry line to:

```json
    { "id": "M-2026-014", "name": "Doe v. State", "reference": "CR-2026-014", "sessionCount": 3, "archived": false }
```

  Then add one bullet after the roster/vocabulary bullets (after line 265):

```markdown
- `archived` (matter.json v2 + index v2, 2026-07-03, additive): archived matters leave the
  default matter list and pickers behind a "show archived" toggle; archiving a matter never
  cascades to its sessions, and existing tags keep rendering normally.
```

- [ ] **Amendment 2 (section 6.2 — snapshot names).** Lines 508-516 currently contain the sentence:

> It carries the human-readable metadata block — session name, matter(s), participants, date/time, medium, description, and summary (if present) — resolving ids→names live from the current rosters at render time.

  Replace that sentence with:

```markdown
It carries the human-readable metadata block — session name, matter(s), participants,
date/time, medium, description, and summary (if present). **Participant names render from
the session's own `meta.json` snapshot** (§1.4/§10) — never resolved live from Matter
rosters — so a later roster rename cannot silently alter an old privileged record
(2026-07-03 refinement; supersedes the earlier "resolved live from the current rosters"
wording, and applies to every projection: list, read view, `session.txt`). Matter
**names/references** are the one live resolution: `session.txt` renders "Name (Reference)"
from the matter store at render time, and a Matter rename triggers a background projection
re-render of that matter's tagged sessions.
```

- [ ] **Amendment 3 (section 7 — retention read-only, new settings rows, hotkeys note).** Three edits:
  1. In the JSON example (lines 521-542): `"schemaVersion": 2,` -> `"schemaVersion": 3,` and change the last two lines before `}` from `"logging": { "level": "info", "includeTranscriptText": false }` to:

```json
  "logging": { "level": "info", "includeTranscriptText": false },
  "privacy": { "excludeWindowsFromCapture": true },
  "consentNotice": null
```

  2. In the `audioRetention` table row (line 547), append before the closing `|`:

```markdown
The Stage 4 settings UI shows the effective policy **read-only** ("Keep everything" by default; a migrated `never`/`days:N`/`afterDiarisation` value renders as its own text); the auto-delete opt-ins are deliberately not exposed in any UI (never-propose-audio-auto-deletion decision, 2026-07-03).
```

  3. After the `logging` table row (line 561), add three rows:

```markdown
| `hotkeys` | Retained in the schema but **unwired and not exposed in any UI** — global hotkeys dropped 2026-07-03 (defaults collide with Webex's global Ctrl+Alt+P and Teams/Webex in-app Ctrl+Alt+R; see Stage 4 design 1.1). |
| `privacy` | `{ excludeWindowsFromCapture: bool }` (default `true`) — v3, additive. Applies `WDA_EXCLUDEFROMCAPTURE` to all transcript-bearing windows (main window, read views, live view); the overlay keeps its own `overlay.excludeFromCapture`. |
| `consentNotice` | `null`/absent \| `{ acknowledgedAtUtc, appVersion }` — v3, additive. First-run consent acknowledgment; absent means the consent notice shows at next launch. Acceptance never re-gates Record (manual-only start remains the consent posture). |
```

  Also append to the v1→v2 migration bullet block at the end of section 7 (after line 566):

```markdown
- **v2→v3 migration (2026-07-03):** additive only — add `privacy` at its default
  (`excludeWindowsFromCapture: true`); `consentNotice` stays absent until the user accepts
  the first-run notice. An explicitly stored `audioRetention` value remains preserved as-is.
```

- [ ] Verify the edits landed and nothing contradicts them: `grep -n "selfForMigration\|archived\|consentNotice\|excludeWindowsFromCapture\|read-only" docs/specs/localscribe-specs.md` — expected: hits in the schema policy, 1.4, 1.5, 6.2, and 7; and `grep -n "self from settings, if any\|resolving ids→names live" docs/specs/localscribe-specs.md` — expected: **no matches**.
- [ ] Commit: `git add docs/specs/localscribe-specs.md && git commit -m "docs: specs amendments from Stage 4 design - edited-flag scope, snapshot participant names, retention read-only + privacy/consent rows, migration participants=[], archived flags"`

- [ ] Create `docs/plans/2026-07-03-stage-4-smoke-runbook.md` (full contents):

```markdown
# Stage 4 smoke runbook - session/Matter manager on real hardware

Prereqs: models fetched; Stage 3b B-series previously passed on this box; the 5 real Webex
sessions from earlier smokes present under the storage root.
Run: `dotnet run --project src/LocalScribe.App`

Known limitation carried into this runbook: microphone DEVICE PICKING is not in the Stage 4
settings UI (no capture-device enumeration API exists yet; the Mic group is a read-only
display). C9 verifies the display, not a picker.

## C1 - First-run consent (fresh %APPDATA%, accept + decline paths)
Steps: close the app; rename `%APPDATA%\LocalScribe` aside (do NOT delete - it holds real
settings); launch.
Expected: the consent dialog appears BEFORE any tray icon exists; it shows the local-recording
summary and the prominent "Recording others is your responsibility" statement. Decline path:
click "Decline and exit" -> the app exits, no tray icon ever appeared, and settings.json (if
written at all) has NO consentNotice field. Relaunch -> dialog shows again (detection is
field-absence). Accept path: click "I understand - continue" -> tray appears, settings.json
gains `consentNotice.acknowledgedAtUtc` + `appVersion`. Relaunch -> NO dialog, straight to the
main window. Closing the dialog with the title-bar X must behave as decline.
Record: pass/fail, the acknowledgedAtUtc value, then RESTORE the original %APPDATA% folder.

## C2 - Session list over the 5 real Webex sessions
Steps: launch; open the Sessions page. Then drop a junk folder into `sessions/`
(`mkdir sessions\zz-junk` + a garbage `session.json` containing `not json`), refresh
(navigate away and back).
Expected: all 5 real sessions list newest-first; dates render in each session's stored
offset; App/Medium = Webex; badges correct (no System-mix badge on clean per-process
sessions); durations match the 3a/3b smokes. With the junk folder present: the list still
loads and a footer note reads "1 unreadable folder" - visible, not silent, not blocking.
Record: pass/fail + row count + footer text. Delete the junk folder afterwards (it contains
no session data - it was never a session).

## C3 - Edit/tag round-trip -> session.txt shows new title + matter
Steps: BEFORE editing, hash the truth files of one session:
`Get-FileHash sessions\<id>\transcript.jsonl, sessions\<id>\session.json`. In the detail
pane: change the title, tag a matter (create one inline if none), commit fields.
Expected: a subtle "Saved" indicator per committed field (no Save button); meta.json changes;
`session.txt` re-renders with the NEW title and "Matter: Name (Reference)"; transcript.md
header shows the new title. Re-hash: transcript.jsonl and session.json are BYTE-IDENTICAL
(evidentiary invariant - user edits touch meta.json only). meta.json `edited` stays false
(metadata edits never flip the Edited flag).
Record: pass/fail + before/after hashes.

## C4 - Matter create/roster/archive + repair index
Steps: Matters page: create a matter (note the minted id, e.g. M-2026-001), add two roster
members (one with a role), archive it, toggle "show archived", un-archive. Then corrupt the
index: edit `matters/matters.json` and set the matter's sessionCount to 99; click
"Repair index".
Expected: minted id follows M-{yyyy}-{NNN}; roster members get p-<slug> ids; archived matter
leaves the default list and the Sessions-page Matter filter, reappears under "show archived";
repair recomputes sessionCount back to truth and adopts/drops nothing unexpectedly.
Record: pass/fail + minted matter id + repaired count.

## C5 - Read view + dual-leg audio + capture exclusion INSIDE a Webex screen share (primary use case)
Steps: open a read view for a real Webex session; play audio; toggle Local/Remote mutes; seek.
Then start a real Webex meeting, share the FULL screen, and look at the shared preview with
the main window, the read view, and the live view all open.
Expected: transcript rows are grouped by speaker with timestamps per settings; markers inline;
model/backend in the footer; QA fields nowhere. Audio: both legs play together (hear the
conversation); muting Local isolates the remote leg and vice versa; seek keeps the legs
paired. In the share: ALL LocalScribe windows are INVISIBLE in the shared/recorded view while
visible locally. Flip Settings > Privacy > exclude-from-capture OFF -> windows become visible
in the share (restart windows if the implementation applies it on open).
Record: pass/fail per sub-check (rows, audio pairing, mutes, exclusion on, exclusion off).

## C6 - Delete-to-Recycle-Bin (verify restorable)
Steps: record a 10-second THROWAWAY scratch session (never one of the 5 real ones). Delete it
from the Sessions page; read the confirmation dialog; confirm. Open the Windows Recycle Bin,
restore the folder, refresh the list.
Expected: the dialog shows title, date, duration, matter tags, and states audio + transcript +
metadata are all included; after confirm the folder is GONE from sessions/ and PRESENT in the
Recycle Bin (not permanently unlinked); restore brings the row back after refresh; any open
read view of that session was closed before the delete.
Record: pass/fail + confirmation that restore round-tripped.

## C7 - Single-instance activation
Steps: with the app running and the main window minimized, run
`dotnet run --project src/LocalScribe.App` again from a second terminal.
Expected: the second process exits on its own (no second tray icon, no error), and the FIRST
instance's main window is restored and brought to the foreground.
Record: pass/fail + observed activation latency.

## C8 - Recovery scan (kill mid-recording, relaunch, balloon + badge)
Steps: start a scratch recording; note the app PID (`Get-Process LocalScribe.App`); kill THAT
PID ONLY (`Stop-Process -Id <pid> -Force` - target the specific process, never a blanket
process-name kill). Relaunch.
Expected: the Sessions page shows "checking for interrupted sessions..." briefly; a tray
balloon "Recovered 1 interrupted session(s)" appears; the row flips from "Recovering..." to
normal with the Recovered badge; duration is transcript-derived (badge tooltip says so); the
transcript ends with the recovered-session marker; Start remains available the whole time.
Record: pass/fail + balloon text + recovered session id.

## C9 - Settings round-trip incl. launch-at-login + restart-required root change
Steps: Settings page: set audio format = wav, language = en, self name; verify each commits
(check settings.json). Toggle launch-at-login off then on; after each toggle run
`reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v LocalScribe`.
Verify the Mic group is a read-only display (known limitation, header note above). Pick a new
storage root via the folder picker; then pick a folder under OneDrive.
Expected: settings.json reflects every commit; the Run-key value disappears/reappears with the
toggle; root change shows the restart-required note and the list does NOT change until
restart; after restart the list is empty (new root) and the old sessions are untouched in the
old root (point the root back afterwards); the OneDrive pick shows the sync-provider warning;
the stored root is the LITERAL picked path (no %VAR% re-tokenizing).
Record: pass/fail per sub-check + the reg query outputs.

Record results (pass/fail + notes) inline here, per run, dated.

---

## Results

(none yet)
```

- [ ] Commit: `git add docs/plans/2026-07-03-stage-4-smoke-runbook.md && git commit -m "docs: Stage 4 smoke runbook C1-C9 - consent, list, edit round-trip, matters, read view + share exclusion, recycle delete, single instance, recovery, settings"`
