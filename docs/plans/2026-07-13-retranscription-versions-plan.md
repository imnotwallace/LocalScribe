# Versioned Re-transcription Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement design §3 of `docs/plans/2026-07-13-meetily-round-design.md`: re-transcribe a finalized session with a different model/language into a NEW version folder (`versions\vN-<model>-<yyyy-MM-dd>\`) while the session-root files stay the immutable v1 original; the new version becomes active on completion; the read view gains a version badge + switcher; editing/Split/export/search all follow the active version; cancel discards only the partial version folder.
**Architecture:** Core grows `SessionRecord.ActiveVersion`/`Versions` (schema v4, migrated by `SessionMigrator`), pure `StoragePaths.VersionDir` + version-aware path overloads, an `EditStore` content-dir seam, ActiveVersion resolution in `SessionProjectionLoader`/`SessionWriter`, and a new `Retranscription\RetranscriptionRunner` that drives the existing VAD→`TranscriptionWorker`→`TranscriptMerger` pipeline from the retained FLAC/WAV legs via `FlacPcmReader`, committing the version with ONE session.json write. One-engine-at-a-time is enforced both ways: the runner probes the live `SessionController` (`State`/`PendingFinalize`) and the controller refuses Start through a new `ExternalEngineBusy` seam. The App adds a Re-transcribe dialog (plain Window, ExportDialog pattern), a "Re-transcribing…" row chip riding the existing `UpsertRowAsync` live-update seam, and the read-view version dropdown persisting through a new `MaintenanceService.SetActiveVersionAsync`.
**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, Whisper.net, CUETools/NAudio (FlacPcmReader), xUnit.

## Global Constraints
- Target branch: `feat/retranscription-versions`, created off master. The design spec `docs/plans/2026-07-13-meetily-round-design.md` is already ON master (it landed with the `7d6c88d` merge), so only THIS plan file (`docs/plans/2026-07-13-retranscription-versions-plan.md`) needs adding to the branch — commit it first (`docs(plans): ...`) if it is not committed yet.
- 0-warning build gate must hold (`-warnaserror` on the final task; no new warnings on any task).
- Tests: xUnit. Filtered run: `dotnet test "<testproj>" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\`
- IMPORTANT: the LocalScribe app may be running and LOCK its bin DLL/exe (MSB3027 copy error — NOT a compile error). ALWAYS build/test with the isolated `-p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` shown in every command below. NEVER kill the user's running app or any npm/tauri process wholesale.
- Never use Unicode emojis in test code or scripts (project rule). Middle dots in NEW C# display strings are written as the `"\u00B7"` escape (existing house style, see `ReadViewViewModel.cs:198`).
- Test projects: App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. App.Tests does NOT project-reference Core.Tests — it `<Compile Include>`-links `LiveTestDoubles.cs`, `FakeTranscriptionEngine.cs`, `ManualUtcTimeProvider.cs`, `FakeCaptureDeviceEnumerator.cs`, so `FakeEngineFactory`, `AmplitudeSpeechModel`, `GatedEngineFactory` (internal), `LiveTestDoubles.MakeController/Options`, and `ManualUtcTimeProvider` are all directly usable in App.Tests. App.Tests also has `AppServiceFakes.cs` (`FakeSettingsService`, `FakeUiErrorReporter`, `FakeRecycleBin`).
- The Core suite has 2 known fixture failures (pre-existing, unrelated). Treat "Core green" as "no NEW failures".
- Commit style: `feat(core)`/`feat(app)`/`test(core)`/`test(app)`/`docs(...)`. Every commit message MUST end with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```
- Line anchors below are grounded @ master `7d6c88d` (which includes the feat/cpu-threads-quantized-weights merge: `BackendPlan.CpuThreads`, `ModelFileResolver`, `SessionRecord.WeightsFile`, canonical model names in `ModelPaths.AvailableModels`); re-verify each anchor's QUOTED context before editing — if it has drifted, locate by the quoted code, not the number. NOTE: this branch merges after record-console-polish this round, so minor drift in LiveViewWindow-adjacent files is expected; none of the files this plan touches overlap that branch except possibly `App.xaml.cs` (wiring region only).
- Cross-plan interface contract (the search/import/console plans consume these EXACT names — do not rename): `SessionRecord.ActiveVersion`, `SessionRecord.Versions`, `TranscriptVersion` (`Id`/`Model`/`Backend`/`CreatedAtUtc`/`VocabularyApplied`, plus additive `Language`/`WeightsFile`), `StoragePaths.VersionDir(string sessionId, string versionId)` + version-aware overloads where `"v1"` resolves to the session root, `SessionProjectionLoader` resolving through ActiveVersion by default with an explicit-version overload, and `Retranscription\RetranscriptionRunner`.
- Evidentiary invariants (LOCKED, design §1): session-root files are the immutable original (v1) — the runner never writes into the root except the session.json commit; a version folder may be deleted ONLY while partial (pre-commit); completed versions are never deleted; no auto-carry of corrections/speaker overlays across versions.

---

### Task 1: Core model — `TranscriptVersion`, `SessionRecord.ActiveVersion`/`Versions`, schema v4 migration
**Files:**
- Modify `src\LocalScribe.Core\Model\SessionRecord.cs` (SchemaVersion default at line 8; insert two properties after line 38 `public DeviceSnapshot Devices { get; init; } = new();`; append `TranscriptVersion` + `TranscriptVersions` after the file's last record, line 66).
- Modify `src\LocalScribe.Core\Storage\SessionStore.cs` (line 8 `public const int Version = 3;`).
- Modify `src\LocalScribe.Core\Storage\SessionMigrator.cs` (doc comment line 7; `Migrate` body lines 21–32; add `MigrateV3ToV4`).
- Test `tests\LocalScribe.Core.Tests\SessionMigratorTests.cs` (add one `[Fact]`; update the v3 pins at lines 29, 52, 89, 96, 116, 122).
- Test `tests\LocalScribe.Core.Tests\SessionStoreTests.cs` (add one `[Fact]`; update pins at lines 45, 66, 84).
- Test `tests\LocalScribe.Core.Tests\SessionCatalogTests.cs` (update the pin at line 111 `Assert.Contains("\"schemaVersion\": 3", rewritten);`).

**Interfaces:**
- Produces (cross-plan contract):
```csharp
public sealed record SessionRecord { ...existing...; public string ActiveVersion { get; init; } = "v1"; public IReadOnlyList<TranscriptVersion> Versions { get; init; } = []; }
public sealed record TranscriptVersion { public string Id { get; init; } = ""; public string Model { get; init; } = ""; public string? WeightsFile { get; init; } public string Backend { get; init; } = ""; public string Language { get; init; } = ""; public DateTimeOffset CreatedAtUtc { get; init; } public bool VocabularyApplied { get; init; } }
public static class TranscriptVersions { public const string Root = "v1"; public static string ShortId(string versionId); public static int Number(string versionId); public static string NewId(int number, string model, DateOnly date); }
```
(`TranscriptVersion.Id` is the FULL version-folder name, e.g. `"v2-base.en-2026-07-13"`, and doubles as the `ActiveVersion` value; `ShortId` maps it to the display/badge form `"v2"`. `Language` and `WeightsFile` (the exact ggml weights file that produced the version — the same provenance field `SessionRecord.WeightsFile` now records at root) are additive extras beyond the contract minimum — contract fields keep their exact names.)
- Consumes: `SchemaGuard.ReadVersion/RejectIfNewer`, `LocalScribeJson.Options` (camelCase — wire names `activeVersion`/`versions`), existing `SessionMigrator` chain.

Steps:
- [ ] **Write the failing tests.** Append inside `SessionMigratorTests` (before the closing brace) in `tests\LocalScribe.Core.Tests\SessionMigratorTests.cs`:
```csharp
    [Fact]
    public void V3_to_v4_defaults_activeVersion_v1_and_empty_versions()
    {
        var v3 = JsonNode.Parse(@"{""schemaVersion"":3,""id"":""x"",""app"":""Webex"",
            ""startedAtUtc"":""2026-07-02T14:32:05Z"",""durationMs"":0,""sources"":[],
            ""model"":"""",""backend"":"""",""language"":""auto"",""retainedAudioSources"":[],
            ""appVersion"":""0.1.0""}")!.AsObject();
        var r = SessionMigrator.Migrate(v3, self: null);
        Assert.Equal(4, r.Session.SchemaVersion);
        Assert.Equal("v1", r.Session.ActiveVersion);
        Assert.Empty(r.Session.Versions);
        Assert.Null(r.SynthesizedMeta);          // v3 -> v4 synthesizes nothing (additive fields)
    }
```
  and append inside `SessionStoreTests` in `tests\LocalScribe.Core.Tests\SessionStoreTests.cs`:
```csharp
    [Fact]
    public async Task Roundtrips_activeVersion_and_versions_at_v4()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "session.json");
        try
        {
            var versioned = Sample() with
            {
                ActiveVersion = "v2-base.en-2026-07-13",
                Versions = new[]
                {
                    new TranscriptVersion
                    {
                        Id = "v2-base.en-2026-07-13", Model = "base.en", Backend = "CPU",
                        Language = "en", WeightsFile = "ggml-base.en-q8_0.bin",
                        CreatedAtUtc = new DateTimeOffset(2026, 7, 13, 6, 0, 0, TimeSpan.Zero),
                        VocabularyApplied = true,
                    },
                },
            };
            var store = new SessionStore(path);
            await store.SaveAsync(versioned, default);
            var back = await store.ReadAsync(default);

            Assert.Equal(4, back!.SchemaVersion);
            Assert.Equal("v2-base.en-2026-07-13", back.ActiveVersion);
            var v = Assert.Single(back.Versions);
            Assert.Equal("base.en", v.Model);
            Assert.Equal("CPU", v.Backend);
            Assert.Equal("ggml-base.en-q8_0.bin", v.WeightsFile);
            Assert.True(v.VocabularyApplied);
            Assert.Equal("v2", TranscriptVersions.ShortId(v.Id));
            Assert.Equal(2, TranscriptVersions.Number(v.Id));

            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"activeVersion\": \"v2-base.en-2026-07-13\"", json);
            Assert.Contains("\"vocabularyApplied\": true", json);
            Assert.Contains("\"weightsFile\": \"ggml-base.en-q8_0.bin\"", json);
        }
        finally { CleanParent(path); }
    }
```
- [ ] **Run and see them FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~V3_to_v4|FullyQualifiedName~Roundtrips_activeVersion" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS1061: 'SessionRecord' does not contain a definition for 'ActiveVersion'` (and `Versions`, `TranscriptVersion`, `TranscriptVersions`).
- [ ] **Add the fields + records.** In `src\LocalScribe.Core\Model\SessionRecord.cs` replace line 8 (`public int SchemaVersion { get; init; } = 3;`) with:
```csharp
    public int SchemaVersion { get; init; } = 4;
```
  Immediately after line 38 (`public DeviceSnapshot Devices { get; init; } = new();`) insert:
```csharp

    /// <summary>Which transcript the app reads/edits/exports (design 2026-07-13 section 3):
    /// "v1" = the immutable session-root original; otherwise a TranscriptVersion.Id resolving to
    /// versions\&lt;id&gt;\. Root truth fields above (Model/Backend/Language/SegmentCount/...)
    /// always describe the ORIGINAL v1 run - per-version actuals live in the Versions entries.</summary>
    public string ActiveVersion { get; init; } = "v1";

    /// <summary>Completed re-transcriptions, oldest first (v2, v3, ...). The root v1 has no
    /// entry here. An entry is written in the SAME session.json save that flips ActiveVersion
    /// (the run's single commit point), so a listed version is always a complete folder.</summary>
    public IReadOnlyList<TranscriptVersion> Versions { get; init; } = [];
```
  Append at the end of the file (after the closing brace of `RemoteSnapshot`, line 66):
```csharp

/// <summary>One completed re-transcription (design 2026-07-13 section 3.1). Id is the FULL
/// version-folder name under versions\ ("v2-base.en-2026-07-13") and doubles as the
/// SessionRecord.ActiveVersion value, so StoragePaths.VersionDir stays a pure join.</summary>
public sealed record TranscriptVersion
{
    public string Id { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>The exact ggml weights file that produced this version (mirrors
    /// SessionRecord.WeightsFile - Model alone no longer determines the file; ModelFileResolver
    /// picks quantized variants per backend). Null = no segment was ever transcribed.</summary>
    public string? WeightsFile { get; init; }
    public string Backend { get; init; } = "";
    public string Language { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    /// <summary>True when the run's Whisper initial prompt carried global/matter vocabulary
    /// terms (design 2026-07-13 section 3.2).</summary>
    public bool VocabularyApplied { get; init; }
}

/// <summary>Pure helpers over version ids. A static class (not computed properties on
/// TranscriptVersion) so nothing extra serializes into session.json.</summary>
public static class TranscriptVersions
{
    /// <summary>The session-root pseudo-version: never has a folder or a Versions entry.</summary>
    public const string Root = "v1";

    /// <summary>"v2-base.en-2026-07-13" -> "v2" (badge/footer display form).</summary>
    public static string ShortId(string versionId)
    {
        int i = versionId.IndexOf('-');
        return i < 0 ? versionId : versionId[..i];
    }

    /// <summary>The monotonic number inside a version id; 1 for "v1" or anything unparseable
    /// (an unparseable folder name then never blocks NewId's max+1 numbering).</summary>
    public static int Number(string versionId)
    {
        string shortId = ShortId(versionId);
        return shortId.Length > 1 && shortId[0] == 'v'
            && int.TryParse(shortId.AsSpan(1), out int n) ? n : 1;
    }

    public static string NewId(int number, string model, DateOnly date)
        => $"v{number}-{model}-{date:yyyy-MM-dd}";
}
```
- [ ] **Bump the store version.** In `src\LocalScribe.Core\Storage\SessionStore.cs` replace line 8 (`public const int Version = 3;`) with:
```csharp
    public const int Version = 4;
```
- [ ] **Add the v3→v4 hop.** In `src\LocalScribe.Core\Storage\SessionMigrator.cs` replace lines 21–32 (the block from `if (version <= 1)` through `raw["schemaVersion"] = 3;`) with:
```csharp
        if (version <= 1)
        {
            MigrateV1ToV2(raw);
            version = 2;
        }
        if (version == 2)
        {
            synthesized = MigrateV2ToV3(raw, self);
            version = 3;
        }
        if (version == 3)
        {
            MigrateV3ToV4(raw);
            version = 4;
        }

        raw["schemaVersion"] = 4;
```
  In the class doc comment (line 7) change `v1 -> v2 -> v3` to `v1 -> v2 -> v3 -> v4`. Append after `MigrateV2ToV3` (before the class's closing brace):
```csharp

    /// <summary>v3 -> v4 (design 2026-07-13 section 3.1): versioned re-transcription. Old
    /// sessions read as activeVersion "v1" (the session root) with no recorded versions -
    /// exactly the typed defaults, written explicitly so a v4 file is self-describing.</summary>
    private static void MigrateV3ToV4(JsonObject o)
    {
        o["activeVersion"] = "v1";
        o["versions"] = new JsonArray();
    }
```
- [ ] **Update the existing v3 pins (they now assert v4).** All by quoted text, not line number:
  - `SessionMigratorTests.cs`: the three `Assert.Equal(3, r.Session.SchemaVersion);` (lines 29, 52, 89) → `Assert.Equal(4, r.Session.SchemaVersion);`; in `Rejects_future_version` (line 96) change `{\"schemaVersion\":4}` → `{\"schemaVersion\":5}`; in `Store_migrates_v2_folder_and_writes_meta_json` change `Assert.Equal(3, migrated!.SchemaVersion);` (line 116) → `4` and `Assert.Contains("\"schemaVersion\": 3", rewritten);` (line 122) → `"\"schemaVersion\": 4"`. Rename `Already_v3_returns_no_synthesized_meta`'s assertion only (the fact name may stay; it still proves no meta is synthesized).
  - `SessionStoreTests.cs`: in `Roundtrips_all_fields_at_v3` change `Assert.Equal(3, back!.SchemaVersion);` (line 45) → `4` (optionally rename the fact `..._at_v4`); in `Written_json_uses_spec_shape` change `Assert.Contains("\"schemaVersion\": 3", json);` (line 66) → `"\"schemaVersion\": 4"` and ADD `Assert.Contains("\"activeVersion\": \"v1\"", json);` beneath it; in `Rejects_newer_schema_version` change `{\"schemaVersion\":4}` (line 84) → `{\"schemaVersion\":5}`.
  - `SessionCatalogTests.cs`: change `Assert.Contains("\"schemaVersion\": 3", rewritten);` (line 111) → `"\"schemaVersion\": 4"`.
- [ ] **Run tests and see PASS.** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionMigratorTests|FullyQualifiedName~SessionStoreTests|FullyQualifiedName~SessionCatalogTests|FullyQualifiedName~JsonConventionsTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: all pass (JsonConventionsTests proves the shared serializer conventions still hold).
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Model/SessionRecord.cs src/LocalScribe.Core/Storage/SessionStore.cs src/LocalScribe.Core/Storage/SessionMigrator.cs tests/LocalScribe.Core.Tests/SessionMigratorTests.cs tests/LocalScribe.Core.Tests/SessionStoreTests.cs tests/LocalScribe.Core.Tests/SessionCatalogTests.cs
git commit -m "feat(core): SessionRecord.ActiveVersion + Versions (schema v4) with v3->v4 migration

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---
### Task 2: `StoragePaths.VersionDir` + version-aware path overloads
**Files:**
- Modify `src\LocalScribe.Core\Storage\StoragePaths.cs` (insert after line 24 `public string SummaryMd(string id) => Path.Combine(SessionDir(id), "summary.md");`).
- Test `tests\LocalScribe.Core.Tests\StoragePathsTests.cs` (add one `[Fact]`).

**Interfaces:**
- Produces (cross-plan contract; all pure joins):
```csharp
public string VersionsDir(string id);                                  // <session>\versions
public string VersionDir(string id, string versionId);                 // "v1" -> SessionDir(id); else <session>\versions\<versionId>
public string TranscriptJsonl(string id, string versionId);
public string EditsJson(string id, string versionId);
public string SpeakersJson(string id, string versionId);
public string TranscriptMd(string id, string versionId);
public string TranscriptTxt(string id, string versionId);
```
- Consumes: `TranscriptVersions.Root` (Task 1).

Steps:
- [ ] **Write the failing test.** Append inside `StoragePathsTests` in `tests\LocalScribe.Core.Tests\StoragePathsTests.cs`:
```csharp
    [Fact]
    public void Version_paths_resolve_v1_to_the_session_root_and_others_under_versions()
    {
        var p = new StoragePaths(@"C:\Data\LocalScribe");
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\versions", p.VersionsDir("s1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1", p.VersionDir("s1", "v1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\versions\v2-base.en-2026-07-13",
            p.VersionDir("s1", "v2-base.en-2026-07-13"));
        // "v1" overloads are byte-identical to the root getters (the pre-versioning layout).
        Assert.Equal(p.TranscriptJsonl("s1"), p.TranscriptJsonl("s1", "v1"));
        Assert.Equal(p.EditsJson("s1"), p.EditsJson("s1", "v1"));
        Assert.Equal(p.SpeakersJson("s1"), p.SpeakersJson("s1", "v1"));
        Assert.Equal(p.TranscriptMd("s1"), p.TranscriptMd("s1", "v1"));
        Assert.Equal(p.TranscriptTxt("s1"), p.TranscriptTxt("s1", "v1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\versions\v2-base.en-2026-07-13\transcript.jsonl",
            p.TranscriptJsonl("s1", "v2-base.en-2026-07-13"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\versions\v2-base.en-2026-07-13\edits.json",
            p.EditsJson("s1", "v2-base.en-2026-07-13"));
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Version_paths_resolve" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS1061: 'StoragePaths' does not contain a definition for 'VersionsDir'`.
- [ ] **Implement.** In `src\LocalScribe.Core\Storage\StoragePaths.cs`, immediately after line 24 (`public string SummaryMd(string id) => Path.Combine(SessionDir(id), "summary.md");`) insert:
```csharp

    // Versioned re-transcription (design 2026-07-13 section 3.1). "v1" resolves to the session
    // root, so every version-aware overload below degenerates to the pre-versioning layout for
    // un-versioned sessions - callers can always go through these.
    public string VersionsDir(string id) => Path.Combine(SessionDir(id), "versions");
    public string VersionDir(string id, string versionId)
        => versionId == TranscriptVersions.Root ? SessionDir(id) : Path.Combine(VersionsDir(id), versionId);
    public string TranscriptJsonl(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "transcript.jsonl");
    public string EditsJson(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "edits.json");
    public string SpeakersJson(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "speakers.json");
    public string TranscriptMd(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "transcript.md");
    public string TranscriptTxt(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "transcript.txt");
```
  (`TranscriptVersions` is in `LocalScribe.Core.Model`, already imported at line 2.)
- [ ] **Run tests and see PASS.** Same filter — expected: 1 passed. Then `--filter "FullyQualifiedName~StoragePathsTests"` to prove no regression.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Storage/StoragePaths.cs tests/LocalScribe.Core.Tests/StoragePathsTests.cs
git commit -m "feat(core): StoragePaths.VersionDir + version-aware transcript/edits/speakers/projection paths

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: `SessionArchiver` — recurse into subfolders so .zip exports carry `versions\`
**Files:**
- Modify `src\LocalScribe.Core\Storage\SessionArchiver.cs` (the enumeration + entry-name lines 15–20).
- Test `tests\LocalScribe.Core.Tests\SessionArchiverTests.cs` (add one `[Fact]`).

**Interfaces:**
- Produces: no signature change — `AddSessionFolderAsync` now includes files in subfolders, entry names are `/`-separated relative paths. (Verified at master: it enumerates TOP-LEVEL only, so the design's ".zip includes all versions by construction" claim needs this fix.)
- Consumes: existing `IsAudio`, `entryPrefix` semantics (unchanged for top-level files, so `MaintenanceService` zip exports keep their exact entry names for un-versioned sessions).

Steps:
- [ ] **Write the failing test.** Append inside `SessionArchiverTests` in `tests\LocalScribe.Core.Tests\SessionArchiverTests.cs`:
```csharp
    [Fact]
    public async Task Subfolder_files_are_archived_with_forward_slash_relative_paths()
    {
        string dir = Seed(("session.json", "{}"u8.ToArray()));
        string vdir = Path.Combine(dir, "versions", "v2-base.en-2026-07-13");
        Directory.CreateDirectory(vdir);
        File.WriteAllBytes(Path.Combine(vdir, "transcript.jsonl"), new byte[16]);
        File.WriteAllBytes(Path.Combine(vdir, "edits.json"), "{}"u8.ToArray());

        string dest = Path.Combine(_root, "versions.zip");
        using (var fs = new FileStream(dest, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            await SessionArchiver.AddSessionFolderAsync(zip, dir, "s1/", CancellationToken.None);

        using var read = ZipFile.OpenRead(dest);
        Assert.Equal(new[]
        {
            "s1/session.json",
            "s1/versions/v2-base.en-2026-07-13/edits.json",
            "s1/versions/v2-base.en-2026-07-13/transcript.jsonl",
        }, read.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
```
- [ ] **Run it and see it FAIL.** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Subfolder_files_are_archived" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected FAIL (assert, not build): actual entries contain only `s1/session.json` (top-level enumeration drops the versions subtree).
- [ ] **Implement.** In `src\LocalScribe.Core\Storage\SessionArchiver.cs` replace lines 15–20:
```csharp
        foreach (string file in Directory.EnumerateFiles(sessionDir).OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(file);
            var level = IsAudio(name) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var entry = zip.CreateEntry(entryPrefix + name, level);
```
  with:
```csharp
        // Versioned re-transcription (design 2026-07-13 section 3.3): archive the WHOLE folder
        // tree so versions\vN-...\ rides along. Entry names are '/'-relative paths (zip
        // convention); a top-level file's relative path IS its file name, so pre-versioning
        // archives are byte-identical in shape.
        foreach (string file in Directory.EnumerateFiles(sessionDir, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetRelativePath(sessionDir, file).Replace('\\', '/');
            var level = IsAudio(name) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var entry = zip.CreateEntry(entryPrefix + name, level);
```
  (`IsAudio` uses `EndsWith`, so a relative path still matches.)
- [ ] **Run tests and see PASS.** Same filter — 1 passed. Then `--filter "FullyQualifiedName~SessionArchiverTests"` — all pass (the existing three facts pin the unchanged top-level behavior).
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Storage/SessionArchiver.cs tests/LocalScribe.Core.Tests/SessionArchiverTests.cs
git commit -m "feat(core): SessionArchiver recurses into subfolders so zip exports include versions/

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: `EditStore` content-dir seam (edits/speakers/jsonl per version; session/meta stay root)
**Files:**
- Modify `src\LocalScribe.Core\Storage\EditStore.cs` (ctor + path properties, lines 10–19).
- Test: new file `tests\LocalScribe.Core.Tests\EditStoreVersionTests.cs` (self-contained; does not depend on `EditStoreTests` internals).

**Interfaces:**
- Produces: `public EditStore(string sessionDir, TimeProvider time, string? contentDir = null)` — `contentDir` (default null = sessionDir, i.e. v1) owns `edits.json`/`speakers.json`/`transcript.jsonl`; `session.json`/`meta.json` ALWAYS resolve from `sessionDir` (finalized-gate + Edited flip are session-level). Every existing 2-arg call site keeps compiling unchanged.
- Consumes: nothing new; callers resolve the active version dir via `StoragePaths.VersionDir` (Tasks 5–6).

Steps:
- [ ] **Write the failing test.** Create `tests\LocalScribe.Core.Tests\EditStoreVersionTests.cs`:
```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public sealed class EditStoreVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    public EditStoreVersionTests() => _paths = new StoragePaths(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private const string Vid = "v2-base.en-2026-07-13";

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
            DurationMs = 60000, Model = "small.en", Backend = "CPU", Language = "en",
            ActiveVersion = Vid,
            Versions = new[] { new TranscriptVersion { Id = Vid, Model = "base.en", Backend = "CPU", Language = "en" } },
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Seed", Medium = Medium.Webex }, default);
        // Root machine transcript has seq 0 ONLY; the version transcript has seq 0 AND 1 -
        // proving below which jsonl the seq validation reads.
        var rootT = new TranscriptStore(_paths.TranscriptJsonl(id));
        await rootT.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Root words.", "Me"), default);
        var vT = new TranscriptStore(_paths.TranscriptJsonl(id, Vid));
        await vT.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "V2 words.", "Me"), default);
        await vT.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 1000, 2000, "V2 more.", "Me"), default);
        return id;
    }

    [Fact]
    public async Task ContentDir_routes_edits_and_seq_validation_to_the_version_folder()
    {
        string id = await SeedVersionedAsync();
        var store = new EditStore(_paths.SessionDir(id), TimeProvider.System,
            contentDir: _paths.VersionDir(id, Vid));

        // seq 1 exists ONLY in the version transcript: validating against the root would throw.
        await store.ApplyTextCorrectionAsync(1, "V2 corrected.", default);

        Assert.True(File.Exists(_paths.EditsJson(id, Vid)));
        Assert.False(File.Exists(_paths.EditsJson(id)));            // root edits untouched
        var edits = await store.LoadAsync(default);
        Assert.Equal("V2 corrected.", edits!.Corrections["1"].Text);
        // The finalized gate still read the ROOT session.json (it lives nowhere else).
        var meta = await new MetadataStore(_paths.MetaJson(id)).LoadAsync(default);
        Assert.True(meta!.Edited);                                   // Edited flip is session-level
    }

    [Fact]
    public async Task Default_contentDir_is_the_session_root_v1()
    {
        string id = await SeedVersionedAsync();
        var store = new EditStore(_paths.SessionDir(id), TimeProvider.System);
        await store.ApplyTextCorrectionAsync(0, "Root corrected.", default);
        Assert.True(File.Exists(_paths.EditsJson(id)));
        Assert.False(File.Exists(_paths.EditsJson(id, Vid)));
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~EditStoreVersionTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS1739: The best overload for 'EditStore' does not have a parameter named 'contentDir'`.
- [ ] **Implement.** In `src\LocalScribe.Core\Storage\EditStore.cs` replace lines 10–19:
```csharp
    public const int Version = 1;
    private readonly string _dir;
    private readonly TimeProvider _time;
    public EditStore(string sessionDir, TimeProvider time) => (_dir, _time) = (sessionDir, time);

    private string EditsPath => Path.Combine(_dir, "edits.json");
    private string SpeakersPath => Path.Combine(_dir, "speakers.json");
    private string SessionPath => Path.Combine(_dir, "session.json");
    private string MetaPath => Path.Combine(_dir, "meta.json");
    private string JsonlPath => Path.Combine(_dir, "transcript.jsonl");
```
  with:
```csharp
    public const int Version = 1;
    private readonly string _dir;
    private readonly string _contentDir;
    private readonly TimeProvider _time;

    /// <summary>sessionDir owns the session-level truth (session.json finalized-gate, meta.json
    /// Edited flip); contentDir owns the per-version transcript content (edits.json /
    /// speakers.json / transcript.jsonl). Null contentDir = the session root, i.e. v1 - every
    /// pre-versioning call site keeps its exact behavior. Callers resolve the ACTIVE version's
    /// dir via StoragePaths.VersionDir (design 2026-07-13 section 3.3: editing always operates
    /// on one version's content).</summary>
    public EditStore(string sessionDir, TimeProvider time, string? contentDir = null)
        => (_dir, _time, _contentDir) = (sessionDir, time, contentDir ?? sessionDir);

    private string EditsPath => Path.Combine(_contentDir, "edits.json");
    private string SpeakersPath => Path.Combine(_contentDir, "speakers.json");
    private string SessionPath => Path.Combine(_dir, "session.json");
    private string MetaPath => Path.Combine(_dir, "meta.json");
    private string JsonlPath => Path.Combine(_contentDir, "transcript.jsonl");
```
- [ ] **Run tests and see PASS.** Same filter — 2 passed. Then `--filter "FullyQualifiedName~EditStoreTests|FullyQualifiedName~EditStoreSplitTests"` — all pass (2-arg ctor behavior unchanged).
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Storage/EditStore.cs tests/LocalScribe.Core.Tests/EditStoreVersionTests.cs
git commit -m "feat(core): EditStore content-dir seam so edits/speakers/jsonl resolve per transcript version

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: `SessionProjectionLoader` resolves ActiveVersion (+ explicit-version overload); `SessionWriter` renders into the active version's folder
**Files:**
- Modify `src\LocalScribe.Core\Storage\SessionProjectionLoader.cs` (the `LoadedProjection` record lines 12–23; `LoadAsync` lines 27–41 and the return at 78–79).
- Modify `src\LocalScribe.Core\Storage\SessionWriter.cs` (`RegenerateProjectionsAsync` lines 19–28).
- Test `tests\LocalScribe.Core.Tests\SessionProjectionLoaderTests.cs` (add a version-seeding helper + two `[Fact]`s).

**Interfaces:**
- Produces (cross-plan contract):
```csharp
public sealed record LoadedProjection(..., string VersionId);   // appended as the LAST positional parameter
public static Task<LoadedProjection> LoadAsync(StoragePaths paths, Settings settings, TimeProvider time, string sessionId, CancellationToken ct);                     // unchanged signature; now follows session.ActiveVersion
public static Task<LoadedProjection> LoadAsync(StoragePaths paths, Settings settings, TimeProvider time, string sessionId, string? versionId, CancellationToken ct);  // explicit version; null = follow ActiveVersion; unknown non-v1 id throws InvalidOperationException
```
  `LoadedProjection.Header` carries the RESOLVED version's Model/Backend (root fields for v1). `SessionWriter.RegenerateProjectionsAsync(sessionId, ct)` (signature unchanged) writes `transcript.md`/`.txt` into `VersionDir(sessionId, loaded.VersionId)` and `session.txt` ALWAYS at the root (session-level metadata, not transcript content).
- Consumes: Tasks 1–2, `EditStore(..., contentDir:)` (Task 4).

Steps:
- [ ] **Write the failing tests.** Append inside `SessionProjectionLoaderTests` in `tests\LocalScribe.Core.Tests\SessionProjectionLoaderTests.cs`:
```csharp
    private const string Vid = "v2-tiny.en-2026-07-13";

    /// <summary>Layers a completed v2 on top of SeedAsync's session: its own jsonl (different
    /// text), empty edits, a Versions entry, and ActiveVersion flipped - the exact on-disk shape
    /// RetranscriptionRunner commits.</summary>
    private static async Task SeedVersionAsync(StoragePaths paths, string id)
    {
        Directory.CreateDirectory(paths.VersionDir(id, Vid));
        var t = new TranscriptStore(paths.TranscriptJsonl(id, Vid));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Second pass hello.", "Me"), default);
        await JsonFile.WriteAsync(paths.EditsJson(id, Vid), new Edits(), default);
        var store = new SessionStore(paths.SessionJson(id));
        var session = (await store.ReadAsync(default))!;
        await store.SaveAsync(session with
        {
            ActiveVersion = Vid,
            Versions = new[] { new TranscriptVersion { Id = Vid, Model = "tiny.en", Backend = "CPU", Language = "en" } },
        }, default);
    }

    [Fact]
    public async Task LoadAsync_follows_activeVersion_and_the_explicit_overload_pins_a_version()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1");
            await SeedVersionAsync(paths, "s1");
            var time = new ManualUtcTimeProvider(T0);

            var active = await SessionProjectionLoader.LoadAsync(paths, new Settings(), time, "s1", default);
            Assert.Equal(Vid, active.VersionId);
            Assert.Equal("tiny.en", active.Header.Model);            // the VERSION's actuals
            Assert.Equal("CPU", active.Header.Backend);
            Assert.Single(active.Rows);
            Assert.Equal("Second pass hello.", active.Rows[0].Text);

            var original = await SessionProjectionLoader.LoadAsync(paths, new Settings(), time, "s1", "v1", default);
            Assert.Equal("v1", original.VersionId);
            Assert.Equal("small.en", original.Header.Model);         // root truth untouched
            Assert.Equal(3, original.Rows.Count);
            Assert.Equal("Hello there.", original.Rows[0].Text);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SessionProjectionLoader.LoadAsync(paths, new Settings(), time, "s1", "v9-nope-2026-01-01", default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Writer_renders_into_the_active_versions_folder_and_never_touches_root_projections()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1");
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            await writer.RegenerateProjectionsAsync("s1", default);          // v1 render (baseline)
            byte[] rootMd = await File.ReadAllBytesAsync(paths.TranscriptMd("s1"));

            await SeedVersionAsync(paths, "s1");
            await writer.RegenerateProjectionsAsync("s1", default);          // now active = v2

            Assert.True(File.Exists(paths.TranscriptMd("s1", Vid)));
            Assert.True(File.Exists(paths.TranscriptTxt("s1", Vid)));
            string vMd = await File.ReadAllTextAsync(paths.TranscriptMd("s1", Vid));
            Assert.Contains("Second pass hello.", vMd);
            Assert.Contains("tiny.en/CPU", vMd);                              // version header actuals
            // v1 (the immutable original): its rendered projection is bit-for-bit untouched.
            Assert.Equal(rootMd, await File.ReadAllBytesAsync(paths.TranscriptMd("s1")));
            Assert.True(File.Exists(paths.SessionTxt("s1")));                 // session.txt stays root
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
```
- [ ] **Run and see FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~LoadAsync_follows_activeVersion|FullyQualifiedName~Writer_renders_into_the_active" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS1061: 'LoadedProjection' does not contain a definition for 'VersionId'` (and no 6-arg `LoadAsync` overload).
- [ ] **Extend the record.** In `SessionProjectionLoader.cs` replace the last line of the `LoadedProjection` record declaration (line 23, `    SessionTextView TextView);`) with:
```csharp
    SessionTextView TextView,
    string VersionId);
```
- [ ] **Restructure `LoadAsync`.** Replace lines 27–41 (from `public static async Task<LoadedProjection> LoadAsync(` through the `var edits = ...` line) with:
```csharp
    public static Task<LoadedProjection> LoadAsync(StoragePaths paths, Settings settings,
        TimeProvider time, string sessionId, CancellationToken ct)
        => LoadAsync(paths, settings, time, sessionId, versionId: null, ct);

    /// <summary>Explicit-version overload (design 2026-07-13 section 3). versionId null follows
    /// session.ActiveVersion; "v1" is the session root; any other id must be recorded in
    /// session.Versions - a caller naming a version explicitly must fail loud rather than
    /// silently read a different transcript.</summary>
    public static async Task<LoadedProjection> LoadAsync(StoragePaths paths, Settings settings,
        TimeProvider time, string sessionId, string? versionId, CancellationToken ct)
    {
        var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(ct)
                      ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
        string resolved = versionId ?? session.ActiveVersion;
        TranscriptVersion? version = null;
        if (resolved != TranscriptVersions.Root)
            version = session.Versions.FirstOrDefault(v => v.Id == resolved)
                ?? throw new InvalidOperationException(
                    $"transcript version '{resolved}' is not recorded in session.json for {sessionId}");
        // The session's own recorded offset (spec 1.2) keeps projections deterministic and
        // faithful to where the session happened; machine zone only for pre-v3 records.
        var startedLocal = session.UtcOffsetMinutes is int offsetMin
            ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : session.StartedAtUtc.ToLocalTime();
        var meta = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(ct)
                   ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
        var lines = await new TranscriptStore(paths.TranscriptJsonl(sessionId, resolved)).ReadAllAsync(ct);
        var speakers = await new SpeakersStore(paths.SpeakersJson(sessionId, resolved)).LoadAsync(ct);
        var edits = await new EditStore(paths.SessionDir(sessionId), time,
            contentDir: paths.VersionDir(sessionId, resolved)).LoadAsync(ct);
```
  Then replace the header construction (lines 61–62, `var header = new TranscriptHeader(meta.Title, session.App.ToString(), startedLocal,` / `            session.DurationMs, session.Model, session.Backend);`) with:
```csharp
        var header = new TranscriptHeader(meta.Title, session.App.ToString(), startedLocal,
            session.DurationMs, version?.Model ?? session.Model, version?.Backend ?? session.Backend);
```
  And replace the return (lines 78–79) with:
```csharp
        return new LoadedProjection(session, meta, lines, speakers, edits, mattersById, matterDisplays,
            startedLocal, rows, header, view, resolved);
```
- [ ] **Version-aware writer.** In `src\LocalScribe.Core\Storage\SessionWriter.cs` replace `RegenerateProjectionsAsync` (lines 19–28):
```csharp
    public async Task RegenerateProjectionsAsync(string sessionId, CancellationToken ct)
    {
        var loaded = await SessionProjectionLoader.LoadAsync(_paths, _settings, _time, sessionId, ct);
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptMd(sessionId),
            MarkdownRenderer.Render(loaded.Header, loaded.Rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptTxt(sessionId),
            PlainTextRenderer.Render(loaded.Header, loaded.Rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.SessionTxt(sessionId),
            SessionTextRenderer.Render(loaded.TextView), ct);
    }
```
  with:
```csharp
    public async Task RegenerateProjectionsAsync(string sessionId, CancellationToken ct)
    {
        var loaded = await SessionProjectionLoader.LoadAsync(_paths, _settings, _time, sessionId, ct);
        // Versioned sessions (design 2026-07-13 section 3.1): the transcript projections land
        // INSIDE the active version's folder ("v1" resolves to the session root, preserving the
        // pre-versioning layout byte-for-byte). session.txt is session-level metadata, not
        // transcript content - it always stays at the root. An INACTIVE version's rendered files
        // are never touched, so the v1 originals are immutable while v2+ is active.
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptMd(sessionId, loaded.VersionId),
            MarkdownRenderer.Render(loaded.Header, loaded.Rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptTxt(sessionId, loaded.VersionId),
            PlainTextRenderer.Render(loaded.Header, loaded.Rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.SessionTxt(sessionId),
            SessionTextRenderer.Render(loaded.TextView), ct);
    }
```
- [ ] **Run tests and see PASS.** Same filter — 2 passed. Then run the byte-identity guards: `--filter "FullyQualifiedName~SessionProjectionLoaderTests|FullyQualifiedName~SessionWriterTests|FullyQualifiedName~RendererTests"` — all pass (the v1 default path must stay byte-identical; `Writer_renders_golden_projections` is the pin).
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Storage/SessionProjectionLoader.cs src/LocalScribe.Core/Storage/SessionWriter.cs tests/LocalScribe.Core.Tests/SessionProjectionLoaderTests.cs
git commit -m "feat(core): SessionProjectionLoader resolves ActiveVersion (+ explicit overload); writer renders into the version folder

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---
### Task 6: `MaintenanceService` active-version plumbing + `SetActiveVersionAsync` + .docx footer version note + Split-speakers active-version read
**Files:**
- Modify `src\LocalScribe.App\Services\MaintenanceService.cs`: add `ActiveVersionAsync` helper + `SetActiveVersionAsync` (after `SetArchivedAsync`, line 107); thread the active version into `SaveTextCorrectionsAsync` (line 121), `SaveTranscriptEditsAsync` (line 138), `SaveSpeakerPinsAsync` (line 208) + `MintClusterKeyAsync` (lines 250–254), `RemoveSpeakerPinsAsync` (line 239), `SaveDiarisationAsync` (line 319); compose the footer version note in `ExportDocxAsync` (lines 565–566).
- Modify `src\LocalScribe.App\ViewModels\SplitSpeakersViewModel.cs` (line 207 transcript read).
- Test: new file `tests\LocalScribe.App.Tests\MaintenanceServiceVersionsTests.cs` (self-contained).

**Interfaces:**
- Produces: `public Task<bool> MaintenanceService.SetActiveVersionAsync(string sessionId, string versionId, CancellationToken ct)` — false when session.json is gone; throws `ArgumentException` for an id that is neither `"v1"` nor a recorded `Versions` entry; no-op-true when already active; persists under the per-session gate (the read-view switcher, Task 11, consumes this). `private Task<string> ActiveVersionAsync(string sessionId, CancellationToken ct)`.
- Consumes: Tasks 1–5 (`ActiveVersion`, `VersionDir`, `EditStore(..., contentDir:)`, `LoadedProjection.VersionId`, `TranscriptVersions.ShortId/Root`).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\MaintenanceServiceVersionsTests.cs`:
```csharp
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
```
- [ ] **Run and see FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~MaintenanceServiceVersionsTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS1061: 'MaintenanceService' does not contain a definition for 'SetActiveVersionAsync'`.
- [ ] **Add the helper + `SetActiveVersionAsync`.** In `MaintenanceService.cs`, immediately after `SetArchivedAsync`'s closing (line 107, `        }, ct);`) insert:
```csharp

    /// <summary>The session's active transcript version id ("v1" = the session root). The
    /// version-content operations below (edits/speakers/transcript reads) resolve through this
    /// so editing and Split speakers always operate on the ACTIVE version (design 2026-07-13
    /// section 3.3). Callers hold the per-session gate, so the read cannot interleave with
    /// SetActiveVersionAsync's write.</summary>
    private async Task<string> ActiveVersionAsync(string sessionId, CancellationToken ct)
        => (await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(ct))?.ActiveVersion ?? "v1";

    /// <summary>Persist which transcript version the session reads/edits/exports (design
    /// 2026-07-13 section 3.4: the read-view switcher). Gated per session like every other
    /// session.json rewrite; validates against the recorded Versions list so a stale caller can
    /// never point ActiveVersion at a folder that was never committed. No projection regen: each
    /// version keeps its own rendered files, written when it was created/last edited.</summary>
    public Task<bool> SetActiveVersionAsync(string sessionId, string versionId, CancellationToken ct)
        => RunForSessionAsync(sessionId, async inner =>
        {
            var store = new SessionStore(paths.SessionJson(sessionId));
            var session = await store.ReadAsync(inner);
            if (session is null) return false;
            if (versionId != TranscriptVersions.Root && session.Versions.All(v => v.Id != versionId))
                throw new ArgumentException(
                    $"unknown transcript version '{versionId}' for {sessionId}.", nameof(versionId));
            if (session.ActiveVersion == versionId) return true;
            await store.SaveAsync(session with { ActiveVersion = versionId }, inner);
            return true;
        }, ct);
```
- [ ] **Thread the active version through the edit paths.** Each edit is locate-by-quote (line numbers shift as you go):
  1. `SaveTextCorrectionsAsync` — replace `bool changed = await new EditStore(paths.SessionDir(sessionId), time)` (line 121) and its following line with:
```csharp
            string vid = await ActiveVersionAsync(sessionId, inner);
            bool changed = await new EditStore(paths.SessionDir(sessionId), time,
                    contentDir: paths.VersionDir(sessionId, vid))
                .ApplyTextEditsAsync(corrections, reverts, inner);
```
  2. `SaveTranscriptEditsAsync` — replace `var store = new EditStore(paths.SessionDir(sessionId), time);` (line 138) with:
```csharp
            var store = new EditStore(paths.SessionDir(sessionId), time,
                contentDir: paths.VersionDir(sessionId, await ActiveVersionAsync(sessionId, inner)));
```
  3. `SaveSpeakerPinsAsync` — insert `string vid = await ActiveVersionAsync(sessionId, inner);` immediately after its `if (!File.Exists(paths.SessionJson(sessionId))) return false;` (line 178); replace `await new EditStore(paths.SessionDir(sessionId), time)` (line 208) and its `.ReassignSpeakersAsync(...)` line with:
```csharp
            await new EditStore(paths.SessionDir(sessionId), time,
                    contentDir: paths.VersionDir(sessionId, vid))
                .ReassignSpeakersAsync(seqs, source, clusterKey, inner);
```
     and change the `MintClusterKeyAsync` call (line 200) to `await MintClusterKeyAsync(sessionId, vid, source, meta!, inner);`.
  4. `MintClusterKeyAsync` — widen the signature (lines 250–251) to `private async Task<string> MintClusterKeyAsync(string sessionId, string versionId, TranscriptSource source, SessionMeta meta, CancellationToken ct)` and replace its speakers read (line 253) with `var speakers = await new SpeakersStore(paths.SpeakersJson(sessionId, versionId)).LoadAsync(ct) ?? new Speakers();`.
  5. `RemoveSpeakerPinsAsync` — replace `bool changed = await new EditStore(paths.SessionDir(sessionId), time)` (line 239) and its following line with:
```csharp
            bool changed = await new EditStore(paths.SessionDir(sessionId), time,
                    contentDir: paths.VersionDir(sessionId, await ActiveVersionAsync(sessionId, inner)))
                .RemoveSpeakerPinsAsync(seqs, source, inner);
```
  6. `SaveDiarisationAsync` — replace `var store = new SpeakersStore(paths.SpeakersJson(sessionId));` (line 319) with:
```csharp
            var store = new SpeakersStore(paths.SpeakersJson(sessionId,
                await ActiveVersionAsync(sessionId, inner)));
```
- [ ] **Footer version note.** In `ExportDocxAsync` replace (lines 565–566):
```csharp
            DocxRenderer.Write(fs, loaded.Header, loaded.TextView, loaded.Rows, settings.Current.Timestamps,
                settings.Current.DocxFooterText, pageSize, options);
```
  with:
```csharp
            // Versioned session (design 2026-07-13 section 3.3): the footer must state which
            // transcript version this document renders. Composed HERE, where footerText already
            // composes, so DocxRenderer stays a pure serializer.
            string versionNote =
                $"Transcript version {TranscriptVersions.ShortId(loaded.VersionId)} ({loaded.Header.Model})";
            string footerText = loaded.VersionId == TranscriptVersions.Root
                ? settings.Current.DocxFooterText
                : string.IsNullOrEmpty(settings.Current.DocxFooterText)
                    ? versionNote
                    : settings.Current.DocxFooterText + " - " + versionNote;
            DocxRenderer.Write(fs, loaded.Header, loaded.TextView, loaded.Rows, settings.Current.Timestamps,
                footerText, pageSize, options);
```
- [ ] **Split speakers follows the active version.** In `src\LocalScribe.App\ViewModels\SplitSpeakersViewModel.cs` replace line 207:
```csharp
                var lines = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(token);
```
  with:
```csharp
                // Versioned re-transcription (design 2026-07-13 section 3.3): the cluster-to-line
                // mapping must read the ACTIVE version's machine transcript (the audio legs are
                // version-independent; the committed speakers.json below already routes through
                // MaintenanceService's active-version resolution).
                var lines = await new TranscriptStore(
                    _paths.TranscriptJsonl(sessionId, session.ActiveVersion)).ReadAllAsync(token);
```
  (`session` is in scope — loaded at line 200. Automated coverage for this line is indirect — the storage behavior is pinned by this task's tests and Task 4/5; the dialog path itself is manual smoke #10 — declared in Self-review.)
- [ ] **Run tests and see PASS.** Same filter — 4 passed. Then prove no regression on every touched surface: `--filter "FullyQualifiedName~MaintenanceServiceTests|FullyQualifiedName~MaintenanceServiceEditingTests|FullyQualifiedName~MaintenanceServiceEditorTests|FullyQualifiedName~MaintenanceServiceDiarisationTests|FullyQualifiedName~MaintenanceServiceLoadItemTests|FullyQualifiedName~SplitSpeakersViewModelTests|FullyQualifiedName~SplitSpeakersClusterKeyTests|FullyQualifiedName~SplitSpeakersPickerTests"` — all pass (v1 sessions resolve `vid == "v1"`, byte-identical paths).
- [ ] **Commit.**
```
git add src/LocalScribe.App/Services/MaintenanceService.cs src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs tests/LocalScribe.App.Tests/MaintenanceServiceVersionsTests.cs
git commit -m "feat(app): editing/Split/export resolve the active transcript version + SetActiveVersionAsync + docx footer version note

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: `SessionController.ExternalEngineBusy` — the reverse one-engine-at-a-time guard
**Files:**
- Modify `src\LocalScribe.Core\Live\SessionController.cs` (add the property after line 154 `public string? FinalizingSessionId => _finalizing?.Id;`; add the guard after the not-Idle refusal, lines 323–327).
- Test `tests\LocalScribe.Core.Tests\SessionControllerTests.cs` (add one `[Fact]`).

**Interfaces:**
- Produces: `public Func<string?>? SessionController.ExternalEngineBusy { get; set; }` — returns a user-facing refusal reason while another engine owner (the re-transcription runner) is busy, else null. Read under `_gate` at the top of `StartAsync`; a non-null reason refuses Start exactly like the not-Idle branch (Notice + null, State stays Idle, no session folder).
- Consumes: existing `Notice`, `_gate`, Start refusal pattern. (Why a settable property, not a ctor param: the runner is constructed AFTER the controller in `CompositionRoot.Build` and probes the controller for ITS guard — a ctor param cannot express the cycle. Precedent: `MaintenanceService.StartupScanTask` is the same set-by-composition seam.)

Steps:
- [ ] **Write the failing test.** Append inside `SessionControllerTests` (before the closing brace) in `tests\LocalScribe.Core.Tests\SessionControllerTests.cs`:
```csharp
    [Fact]
    public async Task StartAsync_refuses_while_an_external_engine_owner_is_busy()
    {
        var (c, _, _, clock) = LiveTestDoubles.MakeController(_root);
        var notices = new List<string>();
        c.Notice += n => { lock (notices) notices.Add(n); };
        c.ExternalEngineBusy = () =>
            "Cannot start recording - a re-transcription is still running.";

        string? refused = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        Assert.Null(refused);
        Assert.Equal(SessionState.Idle, c.State);
        Assert.Contains(notices, n => n.Contains("re-transcription"));

        // Guard released -> Start succeeds (the seam is a probe, not a latch).
        c.ExternalEngineBusy = () => null;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);
        clock.ElapsedMs = 1000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~StartAsync_refuses_while_an_external" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS1061: 'SessionController' does not contain a definition for 'ExternalEngineBusy'`.
- [ ] **Add the seam.** In `SessionController.cs`, immediately after line 154 (`public string? FinalizingSessionId => _finalizing?.Id;`) insert:
```csharp

    /// <summary>One-engine-at-a-time, reverse direction (design 2026-07-13 section 3.2): set
    /// once by the composition root after the offline RetranscriptionRunner exists (the runner
    /// is constructed AFTER this controller and probes it for the forward direction, so a ctor
    /// parameter cannot express the cycle - same set-by-composition seam as
    /// MaintenanceService.StartupScanTask). Returns a user-facing refusal reason while another
    /// engine owner is busy, else null. Read under _gate at the top of StartAsync. Best-effort
    /// against user-level concurrency: like the existing await-PendingFinalize guard, it closes
    /// the seconds-apart double-engine cases, not a same-instant theoretical race.</summary>
    public Func<string?>? ExternalEngineBusy { get; set; }
```
- [ ] **Refuse in StartAsync.** Immediately after the not-Idle refusal block (lines 323–327, ending `            }` after `return null;`) insert:
```csharp

            // One-engine-at-a-time (design 2026-07-13 section 3.2): an offline re-transcription
            // holds a whisper engine; starting a live session on top would double-load the model
            // (VRAM) and break the locked one-engine rule. Refuse exactly like the not-Idle
            // branch above - Notice + null, nothing created, State stays Idle.
            if (ExternalEngineBusy?.Invoke() is string engineBusy)
            {
                Notice?.Invoke(engineBusy);
                return null;
            }
```
- [ ] **Run tests and see PASS.** Same filter — 1 passed. Then `--filter "FullyQualifiedName~SessionControllerTests"` to prove no regression (no existing test sets the seam, and null-default means every existing path is untouched).
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs
git commit -m "feat(core): SessionController.ExternalEngineBusy seam refuses Start while re-transcription owns the engine

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: `Retranscription\RetranscriptionRunner` — the Core orchestrator
**Files:**
- New `src\LocalScribe.Core\Retranscription\RetranscriptionRunner.cs` (new namespace folder — cross-plan contract name).
- Test: new file `tests\LocalScribe.Core.Tests\RetranscriptionRunnerTests.cs`.

**Interfaces:**
- Produces (cross-plan contract):
```csharp
namespace LocalScribe.Core.Retranscription;

public sealed record RetranscriptionRequest
{
    public string SessionId { get; init; } = "";
    public string Model { get; init; } = "";            // canonical model name; must be on disk (the dialog feeds canonical entries)
    public string Language { get; init; } = "auto";
    public VadOptions Vad { get; init; } = new();
    public TranscriptionWorkerOptions Worker { get; init; } = new();
}

public sealed class RetranscriptionRunner
{
    public RetranscriptionRunner(StoragePaths paths, Func<Settings> settingsProvider,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, Func<IClock> clockFactory, TimeProvider time,
        Func<string?> liveEngineBusy, Func<IReadOnlySet<string>>? availableModels = null);
    public string? RunningSessionId { get; }             // null when idle (one run at a time)
    public event Action<string>? RetranscriptionStarted;   // sessionId; after guards pass, folder created
    public event Action<string>? RetranscriptionCompleted; // sessionId; ALWAYS (success/refusal/fault/cancel), after RunningSessionId clears
    public event Action<string>? Notice;                   // user-facing refusal/progress text
    public Task<string?> RunAsync(RetranscriptionRequest request, CancellationToken ct); // new version id; null = refused (Notice raised); throws on fault; OperationCanceledException on cancel
    public void CancelCurrent();                           // cancels the in-flight run; no-op when idle
}
```
- Consumes: `OfflinePipelineRunner`'s exact pipeline pieces (`SileroVadSegmenter`, `TranscriptionWorker`, `TranscriptMerger`, `TranscriptStore`, the C1 fault-guard pattern), `FlacPcmReader.ReadMono16k` (FLAC/WAV leg → float[] 16 kHz mono), `BackendSelector.Select`, `VocabularyProvider.BuildInitialPrompt`, `SessionWriter.RegenerateProjectionsAsync` (Task 5 — renders the new ACTIVE version), `StoragePaths` version paths (Task 2), `TranscriptVersions.NewId/Number` (Task 1), `JsonFile.WriteAsync` (empty `Edits`), and `TranscribedSegment.WeightsFile` (weights provenance - tracked per segment in the writer loop and persisted into the version entry, mirroring the live path's `PersistFinalAsync` and `OfflinePipelineRunner`).
- Commit-point semantics (LOCKED): everything up to the single session.json save (Versions entry + ActiveVersion flip in ONE write) is a PARTIAL version — cancel/fault deletes the folder; after that write the version is evidence and is NEVER deleted (projection render runs post-commit with `CancellationToken.None`). Root files are never written except that one session.json save.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\RetranscriptionRunnerTests.cs`:
```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Retranscription;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

public sealed class RetranscriptionRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 13, 6, 0, 0, TimeSpan.Zero));
    public RetranscriptionRunnerTests()
    { _paths = new StoragePaths(Path.Combine(_root, "store")); Directory.CreateDirectory(_root); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static readonly VadOptions TestVad = new()
    { Threshold = 0.5f, MinSpeechMs = 64, MinSilenceMs = 64, SpeechPadMs = 0, MaxSegmentMs = 15000 };

    private static void WriteBurstWav(string path, params (int SilenceMs, int SpeechMs)[] pattern)
    {
        using var sink = new WavSink(path);
        foreach (var (silence, speech) in pattern)
        {
            sink.Write(new float[16 * silence]);
            var burst = new float[16 * speech];
            for (int i = 0; i < burst.Length; i++)
                burst[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * i / 16000.0));
            sink.Write(burst);
        }
        sink.Write(new float[16 * 1000]);                          // trailing second of silence
    }

    /// <summary>A finalized session with one retained WAV leg (FlacPcmReader reads .wav too, so
    /// no FLAC fixture dependency) and a root machine transcript of ONE segment.</summary>
    private async Task<string> SeedFinalizedAsync(string id = "2026-07-10_1000_Webex_seed", bool ended = true)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero),
            EndedAtUtc = ended ? new DateTimeOffset(2026, 7, 10, 2, 1, 0, TimeSpan.Zero) : null,
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            DurationMs = 60000, Model = "small.en", Backend = "CUDA", Language = "en",
            Sources = [SourceKind.Local], RetainedAudioSources = [SourceKind.Local],
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Seed", Medium = Medium.Webex }, default);
        var t = new TranscriptStore(_paths.TranscriptJsonl(id));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Original words.", "Me"), default);
        WriteBurstWav(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav), (200, 1500));
        return id;
    }

    private RetranscriptionRunner MakeRunner(Settings? settings = null, IEngineFactory? engine = null,
        Func<string?>? liveBusy = null)
        => new(_paths, () => settings ?? new Settings(), engine ?? new FakeEngineFactory(),
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(), _time, liveBusy ?? (() => null),
            () => new HashSet<string> { "base.en", "tiny.en" });

    private RetranscriptionRequest Request(string id, string model = "base.en")
        => new() { SessionId = id, Model = model, Language = "en", Vad = TestVad };

    [Fact]
    public async Task Run_creates_v2_with_fresh_edits_flips_activeVersion_and_never_touches_root_content()
    {
        string id = await SeedFinalizedAsync();
        byte[] rootJsonl = await File.ReadAllBytesAsync(_paths.TranscriptJsonl(id));
        var runner = MakeRunner();
        var completed = new List<string>();
        runner.RetranscriptionCompleted += cid => { lock (completed) completed.Add(cid); };

        string? vid = await runner.RunAsync(Request(id), CancellationToken.None);

        string expected = TranscriptVersions.NewId(2, "base.en", DateOnly.FromDateTime(_time.GetLocalNow().Date));
        Assert.Equal(expected, vid);
        Assert.Null(runner.RunningSessionId);
        Assert.Equal(new[] { id }, completed.ToArray());

        // Version folder: its own machine transcript (>= 1 segment from the burst), a fresh
        // EMPTY edits.json, rendered projections with the version's header actuals.
        var vLines = await new TranscriptStore(_paths.TranscriptJsonl(id, vid!)).ReadAllAsync(default);
        Assert.Contains(vLines, l => l.Kind == TranscriptKind.Segment);
        var vEdits = await new EditStore(_paths.SessionDir(id), TimeProvider.System,
            contentDir: _paths.VersionDir(id, vid!)).LoadAsync(default);
        Assert.NotNull(vEdits);
        Assert.Empty(vEdits!.Corrections);
        Assert.False(File.Exists(_paths.SpeakersJson(id, vid!)));      // absent until Split
        Assert.Contains("base.en/CPU", await File.ReadAllTextAsync(_paths.TranscriptMd(id, vid!)));

        // session.json commit: entry + flip; root truth fields still describe v1.
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(vid, session!.ActiveVersion);
        var entry = Assert.Single(session.Versions);
        Assert.Equal("base.en", entry.Model);
        Assert.Equal("ggml-base.en.bin", entry.WeightsFile);    // FakeTranscriptionEngine's default file
        Assert.Equal("CPU", entry.Backend);
        Assert.Equal("en", entry.Language);
        Assert.False(entry.VocabularyApplied);                          // no vocabulary configured
        Assert.Equal(_time.GetUtcNow(), entry.CreatedAtUtc);
        Assert.Equal("small.en", session.Model);                        // root record untouched

        // Evidentiary: the v1 machine transcript is bit-for-bit untouched; no root edits appear.
        Assert.Equal(rootJsonl, await File.ReadAllBytesAsync(_paths.TranscriptJsonl(id)));
        Assert.False(File.Exists(_paths.EditsJson(id)));
    }

    [Fact]
    public async Task Second_run_is_monotonic_v3_and_appends_to_versions()
    {
        string id = await SeedFinalizedAsync();
        var runner = MakeRunner();
        string? v2 = await runner.RunAsync(Request(id), CancellationToken.None);
        string? v3 = await runner.RunAsync(Request(id, model: "tiny.en"), CancellationToken.None);

        Assert.StartsWith("v2-", v2);
        Assert.StartsWith("v3-tiny.en-", v3);
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(v3, session!.ActiveVersion);
        Assert.Equal(new[] { v2, v3 }, session.Versions.Select(v => v.Id).ToArray());
    }

    [Fact]
    public async Task Refuses_unfinalized_sessions_a_busy_live_engine_and_a_missing_model()
    {
        var notices = new List<string>();

        string pending = await SeedFinalizedAsync("2026-07-10_1100_Webex_pending", ended: false);
        var runner = MakeRunner();
        runner.Notice += n => { lock (notices) notices.Add(n); };
        Assert.Null(await runner.RunAsync(Request(pending), CancellationToken.None));
        Assert.Contains(notices, n => n.Contains("finalized"));
        Assert.False(Directory.Exists(_paths.VersionsDir(pending)));

        string id = await SeedFinalizedAsync();
        var busyRunner = MakeRunner(liveBusy: () => "A recording is in progress - stop it first.");
        busyRunner.Notice += n => { lock (notices) notices.Add(n); };
        Assert.Null(await busyRunner.RunAsync(Request(id), CancellationToken.None));
        Assert.Contains(notices, n => n.Contains("recording is in progress"));

        var runner2 = MakeRunner();
        runner2.Notice += n => { lock (notices) notices.Add(n); };
        Assert.Null(await runner2.RunAsync(Request(id, model: "large-v3"), CancellationToken.None));
        Assert.Contains(notices, n => n.Contains("not downloaded"));
        Assert.False(Directory.Exists(_paths.VersionsDir(id)));        // no folder from any refusal
    }

    [Fact]
    public async Task One_run_at_a_time_and_cancel_discards_only_the_partial_folder()
    {
        string id = await SeedFinalizedAsync();
        var gated = new GatedEngineFactory();
        var runner = MakeRunner(engine: gated);
        var notices = new List<string>();
        runner.Notice += n => { lock (notices) notices.Add(n); };

        var run = runner.RunAsync(Request(id), CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => runner.RunningSessionId == id, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => Directory.Exists(_paths.VersionsDir(id)), TimeSpan.FromSeconds(10)));

        // Second concurrent run refuses (one re-transcription at a time).
        Assert.Null(await runner.RunAsync(Request(id), CancellationToken.None));
        Assert.Contains(notices, n => n.Contains("already running"));

        runner.CancelCurrent();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(15)));
        gated.CreateGate.Set();                                        // release the parked engine build

        // The partial folder is discarded; session.json never gained an entry; the slot is free.
        Assert.False(Directory.Exists(_paths.VersionDir(id,
            TranscriptVersions.NewId(2, "base.en", DateOnly.FromDateTime(_time.GetLocalNow().Date)))));
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("v1", session!.ActiveVersion);
        Assert.Empty(session.Versions);
        Assert.Null(runner.RunningSessionId);
    }

    [Fact]
    public async Task Applies_current_vocabulary_as_prompt_bias_and_records_it()
    {
        string id = await SeedFinalizedAsync();
        var engine = new FakeEngineFactory();
        var runner = MakeRunner(
            settings: new Settings { Vocabulary = new Vocabulary { Terms = ["LocalScribe", "Webex"] } },
            engine: engine);

        string? vid = await runner.RunAsync(Request(id), CancellationToken.None);

        Assert.NotNull(vid);
        Assert.Contains("LocalScribe", engine.LastInitialPrompt);
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.True(Assert.Single(session!.Versions).VocabularyApplied);
    }
}
```
- [ ] **Run and see FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~RetranscriptionRunnerTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS0246: The type or namespace name 'Retranscription' could not be found` (namespace/type not yet created).
- [ ] **Implement the runner.** Create `src\LocalScribe.Core\Retranscription\RetranscriptionRunner.cs`:
```csharp
using System.Threading.Channels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Retranscription;

/// <summary>User inputs for one re-transcription run (design 2026-07-13 section 3.4).</summary>
public sealed record RetranscriptionRequest
{
    public string SessionId { get; init; } = "";
    /// <summary>Canonical model name (e.g. "base.en") - the dialog offers only canonical names
    /// of models on disk (ModelPaths.AvailableModels collapses quantized ggml files); Select
    /// re-canonicalizes defensively, and ModelFileResolver picks the FILE per backend.</summary>
    public string Model { get; init; } = "";
    public string Language { get; init; } = "auto";
    public VadOptions Vad { get; init; } = new();
    public TranscriptionWorkerOptions Worker { get; init; } = new();
}

/// <summary>Re-transcribes a finalized session's retained legs into a NEW version folder
/// (design 2026-07-13 section 3). Mirrors OfflinePipelineRunner's VAD -> worker -> merger ->
/// writer-loop wiring (including the C1 fault guard) but targets versions\vN-model-date\ instead
/// of bootstrapping a new session. EVIDENTIARY CORE: the session root is the immutable v1 -
/// this class writes into the root ONLY the single session.json commit (Versions entry +
/// ActiveVersion flip, one atomic save). Before that commit the version folder is a partial
/// derived output and cancel/fault deletes it; after it, the version is evidence and is never
/// deleted. Guards (section 3.2): one run at a time; refuses while the live engine is busy
/// (liveEngineBusy probes SessionController State/PendingFinalize; the reverse direction is
/// SessionController.ExternalEngineBusy); refuses un-finalized sessions (a Recording/Finalizing/
/// Recovering session has EndedAtUtc null on disk).</summary>
public sealed class RetranscriptionRunner
{
    private const int FrameSamples = 512;

    private readonly StoragePaths _paths;
    private readonly Func<Settings> _settingsProvider;
    private readonly IEngineFactory _engineFactory;
    private readonly Func<ISpeechProbabilityModel> _vadModelFactory;
    private readonly IHardwareProbe _hardware;
    private readonly Func<IClock> _clockFactory;
    private readonly TimeProvider _time;
    private readonly Func<string?> _liveEngineBusy;
    private readonly Func<IReadOnlySet<string>> _availableModels;

    private string? _running;
    private CancellationTokenSource? _runCts;

    public RetranscriptionRunner(StoragePaths paths, Func<Settings> settingsProvider,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, Func<IClock> clockFactory, TimeProvider time,
        Func<string?> liveEngineBusy, Func<IReadOnlySet<string>>? availableModels = null)
        => (_paths, _settingsProvider, _engineFactory, _vadModelFactory, _hardware, _clockFactory,
            _time, _liveEngineBusy, _availableModels)
         = (paths, settingsProvider, engineFactory, vadModelFactory, hardware, clockFactory,
            time, liveEngineBusy, availableModels ?? ModelPaths.AvailableModels);

    /// <summary>The session id of the in-flight run, or null. Drives the Sessions-page
    /// "Re-transcribing..." chip and the controller's ExternalEngineBusy probe.</summary>
    public string? RunningSessionId => Volatile.Read(ref _running);

    /// <summary>After guards pass and the version folder exists - the chip flips on.</summary>
    public event Action<string>? RetranscriptionStarted;
    /// <summary>ALWAYS fires once per RunAsync (success, refusal, fault, or cancel), after
    /// RunningSessionId clears - mirrors SessionFinalizeCompleted's one-event-covers-all shape
    /// so the row upsert re-reads disk truth whatever happened.</summary>
    public event Action<string>? RetranscriptionCompleted;
    /// <summary>User-facing refusal/progress text (the composition routes it to the InfoBar).</summary>
    public event Action<string>? Notice;

    /// <summary>Cancels the in-flight run (the partial version folder is discarded); no-op when
    /// idle. Callable from ANY dialog instance - the run outlives the dialog that started it.</summary>
    public void CancelCurrent()
    {
        try { _runCts?.Cancel(); }
        catch (ObjectDisposedException) { }              // settled between the read and the call
    }

    public async Task<string?> RunAsync(RetranscriptionRequest request, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, request.SessionId, null) is not null)
        {
            Notice?.Invoke("A re-transcription is already running - wait for it to finish.");
            try { RetranscriptionCompleted?.Invoke(request.SessionId); } catch { }
            return null;
        }
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runCts = runCts;
        try
        {
            return await RunCoreAsync(request, runCts.Token);
        }
        finally
        {
            _runCts = null;
            Volatile.Write(ref _running, null);
            // A throwing subscriber must never mask the run's own outcome (same wrap as
            // SessionFinalizeCompleted).
            try { RetranscriptionCompleted?.Invoke(request.SessionId); } catch { }
        }
    }

    private async Task<string?> RunCoreAsync(RetranscriptionRequest request, CancellationToken ct)
    {
        string id = request.SessionId;

        if (_liveEngineBusy() is string busy) { Notice?.Invoke(busy); return null; }

        var sessionStore = new SessionStore(_paths.SessionJson(id));
        var session = await sessionStore.ReadAsync(ct);
        if (session is null)
        {
            Notice?.Invoke("Session not found - it may have been deleted.");
            return null;
        }
        if (session.EndedAtUtc is null)
        {
            Notice?.Invoke("This session is not finalized yet (recording, finalizing, or "
                + "recovering) - re-transcription needs a finalized session.");
            return null;
        }

        var settings = _settingsProvider();
        var available = _availableModels();
        // Reuse the live selector verbatim: an explicit model is CANONICALIZED
        // (ModelFileResolver.CanonicalName - a quantized pick like "small.en-q8_0" selects its
        // canonical model; the FILE is picked per backend at engine creation), and a non-English
        // language strips the ".en" suffix to the multilingual weights (spec section 3).
        var (plan, _) = BackendSelector.Select(_hardware.Probe(),
            settings with { Model = request.Model, Language = request.Language }, available);
        if (!available.Contains(plan.ModelName))
        {
            Notice?.Invoke($"Model '{plan.ModelName}' is not downloaded. Run tools/fetch-models.ps1 "
                + "or pick another model.");
            return null;
        }

        var legs = ResolveLegs(id, session.RetainedAudioSources);
        if (legs.Count == 0)
        {
            Notice?.Invoke("No retained audio found for this session - nothing to re-transcribe.");
            return null;
        }

        string versionId = TranscriptVersions.NewId(NextVersionNumber(session), plan.ModelName,
            DateOnly.FromDateTime(_time.GetLocalNow().Date));
        string versionDir = _paths.VersionDir(id, versionId);
        Directory.CreateDirectory(versionDir);
        RetranscriptionStarted?.Invoke(id);

        bool committed = false;
        try
        {
            // CURRENT global + matter vocabulary as prompt bias (design section 3.2) - the same
            // load-skip-missing shape SessionController.StartAsync uses.
            var meta = await new MetadataStore(_paths.MetaJson(id)).LoadAsync(ct);
            IReadOnlyList<string> matterIds = meta?.MatterIds ?? [];
            var mattersById = new Dictionary<string, Matter>();
            var matterStore = new MatterStore(_paths.MattersDir);
            foreach (string mid in matterIds)
            {
                var m = await matterStore.LoadAsync(mid, ct);
                if (m is not null) mattersById[mid] = m;
            }
            string prompt = new VocabularyProvider(settings.Vocabulary, mattersById)
                .BuildInitialPrompt(matterIds);
            bool vocabularyApplied = prompt.Length > 0;

            var clock = _clockFactory();
            var language = new LanguageResolver(request.Language);
            var worker = new TranscriptionWorker(_engineFactory, plan, language, clock,
                request.Worker with { InitialPrompt = vocabularyApplied ? prompt : null });
            var merger = new TranscriptMerger(new TranscriptStore(_paths.TranscriptJsonl(id, versionId)));
            await merger.InitializeAsync(ct);

            // events -> single writer loop (event handlers must not await) - OfflinePipelineRunner's shape.
            var outbox = Channel.CreateUnbounded<object>();
            string? lastModel = null;
            string? lastWeightsFile = null;                     // exact ggml file (provenance)
            worker.SegmentTranscribed += ts => outbox.Writer.TryWrite(ts);
            worker.MarkerRaised += m => outbox.Writer.TryWrite(m);

            var writerLoop = Task.Run(async () =>
            {
                long lastEndMs = 0;
                await foreach (object item in outbox.Reader.ReadAllAsync(ct))
                {
                    if (item is TranscribedSegment ts)
                    {
                        var line = await merger.AppendSegmentAsync(ts, ct);
                        lastEndMs = Math.Max(lastEndMs, line.EndMs);
                        lastModel = ts.ModelName;
                        lastWeightsFile = ts.WeightsFile;
                    }
                    else if (item is string marker)
                    {
                        await merger.AppendMarkerAsync(marker, lastEndMs, ct);
                    }
                }
            }, ct);

            // Pool thread: the real engine ctor is a multi-second synchronous model load (same
            // reason SessionController.StartAsync wraps it).
            var workerLoop = Task.Run(() => worker.RunAsync(ct), CancellationToken.None);

            // C1 fault guard (see OfflinePipelineRunner): a faulted worker leaves the bounded
            // queue reader-less - cancel a feed-only token so EnqueueAsync aborts promptly; the
            // ORIGINAL exception is recovered by awaiting workerLoop below, never masked.
            using var feedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = workerLoop.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Cancel(),
                feedCts, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            bool faulted = false;
            try
            {
                try
                {
                    foreach (var (path, kind) in legs)
                    {
                        var segmenter = new SileroVadSegmenter(kind, request.Vad, _vadModelFactory());
                        await foreach (var segment in segmenter.SegmentAsync(
                            ToAsync(Frames(FlacPcmReader.ReadMono16k(path), kind)), feedCts.Token))
                            await worker.EnqueueAsync(segment, feedCts.Token);
                    }
                }
                catch (OperationCanceledException) when (feedCts.IsCancellationRequested
                                                         && !ct.IsCancellationRequested)
                {
                    // The worker faulted and the C1 guard aborted the feed; the real exception
                    // surfaces from `await workerLoop` below. A caller cancel (ct) rethrows.
                }
                finally { worker.Complete(); }
                await workerLoop;                                   // queue drained (spec 2.1 flush)
            }
            catch { faulted = true; throw; }
            finally
            {
                outbox.Writer.TryComplete();
                if (faulted) { try { await writerLoop; } catch { } }
                else await writerLoop;
            }

            // Per-version isolation (design section 3.3): a fresh EMPTY edits.json; speakers.json
            // stays absent until Split runs against this version. No auto-carry, ever.
            await JsonFile.WriteAsync(_paths.EditsJson(id, versionId), new Edits(), ct);

            // COMMIT - one session.json save appends the entry AND flips ActiveVersion, so a
            // listed version is always a complete folder and a crash can never half-commit.
            var entry = new TranscriptVersion
            {
                Id = versionId,
                Model = lastModel ?? plan.ModelName,
                // Exact file that ran (null: nothing transcribed) - the same weights provenance
                // SessionController.PersistFinalAsync and OfflinePipelineRunner record at root.
                WeightsFile = lastWeightsFile,
                Backend = plan.Backend.ToString().ToUpperInvariant(),
                Language = language.Locked ?? request.Language,
                CreatedAtUtc = _time.GetUtcNow(),
                VocabularyApplied = vocabularyApplied,
            };
            var current = await sessionStore.ReadAsync(CancellationToken.None)
                          ?? throw new InvalidOperationException($"session.json vanished for {id}");
            await sessionStore.SaveAsync(current with
            {
                ActiveVersion = versionId,
                Versions = current.Versions.Append(entry).ToList(),
            }, CancellationToken.None);
            committed = true;

            // Rendered copies inside the version folder (design section 3.1): the default loader
            // path now resolves ActiveVersion = this version, so the plain regen writes
            // transcript.md/.txt into versionDir + refreshes root session.txt. Post-commit and
            // non-cancellable: the version is already evidence; a crash here only costs derived
            // .md/.txt, regenerated on the next edit/regenerate-all.
            await new SessionWriter(_paths, settings, _time)
                .RegenerateProjectionsAsync(id, CancellationToken.None);
            return versionId;
        }
        catch when (!committed)
        {
            // Cancel or fault BEFORE the commit: the folder is a partial derived output, not yet
            // evidence (design section 1) - discard it. Root files were never touched. A
            // completed (committed) version deliberately has NO delete path here or anywhere.
            try { Directory.Delete(versionDir, recursive: true); } catch { }
            throw;
        }
    }

    /// <summary>Retained legs actually on disk, Local first (the live pipeline's feed order):
    /// preferred-format probe matching PlaybackViewModel.Resolve/SplitSpeakersViewModel.ProbeLeg
    /// - FLAC first, WAV fallback, so pre-format-change sessions still resolve.</summary>
    private List<(string Path, SourceKind Kind)> ResolveLegs(string id, IReadOnlyList<SourceKind> retained)
    {
        var legs = new List<(string, SourceKind)>();
        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
        {
            if (!retained.Contains(kind)) continue;
            string flac = _paths.AudioFile(id, kind, AudioFormat.Flac);
            string wav = _paths.AudioFile(id, kind, AudioFormat.Wav);
            if (File.Exists(flac)) legs.Add((flac, kind));
            else if (File.Exists(wav)) legs.Add((wav, kind));
        }
        return legs;
    }

    /// <summary>max+1 over BOTH the recorded Versions and any folders already under versions\ -
    /// an orphaned partial folder (crash before its cancel-cleanup) is skipped past, never
    /// reused and never deleted (it is unreferenced junk, not evidence; left for the user).</summary>
    private int NextVersionNumber(SessionRecord session)
    {
        int max = 1;
        foreach (var v in session.Versions) max = Math.Max(max, TranscriptVersions.Number(v.Id));
        string versionsDir = _paths.VersionsDir(session.Id);
        if (Directory.Exists(versionsDir))
            foreach (string dir in Directory.EnumerateDirectories(versionsDir))
                max = Math.Max(max, TranscriptVersions.Number(Path.GetFileName(dir)));
        return max + 1;
    }

    /// <summary>Pre-decoded 16 kHz mono PCM -> 512-sample AudioFrames with sample-counted
    /// StartMs - the exact emission contract of WavFileFrameReader (trailing partial window
    /// dropped), so the same VAD/worker/merger pipeline runs unchanged over a FLAC leg.</summary>
    private static IEnumerable<AudioFrame> Frames(float[] samples, SourceKind kind)
    {
        long emitted = 0;
        for (int i = 0; i + FrameSamples <= samples.Length; i += FrameSamples)
        {
            yield return new AudioFrame(kind, emitted * 1000 / 16000, samples[i..(i + FrameSamples)]);
            emitted += FrameSamples;
        }
    }

    private static async IAsyncEnumerable<AudioFrame> ToAsync(IEnumerable<AudioFrame> frames)
    {
        foreach (var f in frames) yield return f;
        await Task.CompletedTask;
    }
}
```
  (If `AudioFrame`'s constructor parameter order differs from `new AudioFrame(source, startMs, samples)`, mirror `WavFileFrameReader.ReadFrames` line 36 exactly — that call is the contract.)
- [ ] **Run tests and see PASS.** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~RetranscriptionRunnerTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: 5 passed. Then run the neighbors it composes: `--filter "FullyQualifiedName~OfflinePipelineRunnerTests|FullyQualifiedName~TranscriptionWorkerTests|FullyQualifiedName~FlacPcmReaderTests"` — all pass.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Retranscription/RetranscriptionRunner.cs tests/LocalScribe.Core.Tests/RetranscriptionRunnerTests.cs
git commit -m "feat(core): RetranscriptionRunner - versioned re-transcription over the offline pipeline with guards + cancel cleanup

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---
### Task 9: `RetranscribeDialogViewModel` (WPF-free) + shared `LanguageChoice.All`
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs` (hoist the curated language list onto the `LanguageChoice` record: the record declaration at line 14, the list at lines 247–272).
- New `src\LocalScribe.App\ViewModels\RetranscribeDialogViewModel.cs`.
- Test: new file `tests\LocalScribe.App.Tests\RetranscribeDialogViewModelTests.cs`.

**Interfaces:**
- Produces:
```csharp
public sealed partial class RetranscribeDialogViewModel : ObservableObject, IDisposable
{
    public RetranscribeDialogViewModel(string sessionId, MaintenanceService maintenance,
        RetranscriptionRunner runner, Func<IReadOnlySet<string>> availableModels,
        IUiErrorReporter errors, Action<Action> dispatch);
    public IReadOnlyList<string> ModelChoices { get; }             // CANONICAL names of models ON DISK (quantized files collapse), Ordinal-sorted, NO "auto"
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }  // LanguageChoice.All
    public string? SelectedModel { get; set; }                     // [ObservableProperty]
    public string Language { get; set; }                           // [ObservableProperty], default "auto"
    public bool IsRunning { get; }                                 // [ObservableProperty]; tracks runner state via events
    public string CurrentVersionDisplay { get; }                   // [ObservableProperty]; set by LoadAsync
    public IAsyncRelayCommand StartCommand { get; }                // gated: SelectedModel != null && !IsRunning
    public IRelayCommand CancelRunCommand { get; }                 // gated: IsRunning; delegates to runner.CancelCurrent
    public Task LoadAsync(CancellationToken ct);                   // current-version info line
    public event Action? Closed;                                   // raised (dispatched) only on SUCCESS
}
public sealed record LanguageChoice(string Code, string Name) { public static IReadOnlyList<LanguageChoice> All { get; } }
```
- Consumes: `RetranscriptionRunner` (Task 8 — `RunAsync`/`CancelCurrent`/`RunningSessionId`/`RetranscriptionStarted`/`RetranscriptionCompleted`), `MaintenanceService.LoadSessionItemAsync`, `TranscriptVersions.ShortId`. The dialog is closable while the run continues (design §3.4): the run's completion Info lands on the app-level `IUiErrorReporter`, not the window; Cancel works from ANY later dialog instance because the CTS lives in the runner.

Steps:
- [ ] **Hoist the language list.** In `SettingsPageViewModel.cs` replace line 14:
```csharp
public sealed record LanguageChoice(string Code, string Name);
```
  with:
```csharp
public sealed record LanguageChoice(string Code, string Name)
{
    /// <summary>Auto-detect + common Whisper languages (a curated subset of the ~99 Whisper
    /// supports). Shared by the Settings page and the Re-transcribe dialog (design 2026-07-13
    /// section 3.4) so the two pickers can never drift.</summary>
    public static IReadOnlyList<LanguageChoice> All { get; } =
    [
        new("auto", "Auto-detect"),
        new("en", "English"),
        new("es", "Spanish"),
        new("zh", "Chinese"),
        new("hi", "Hindi"),
        new("ar", "Arabic"),
        new("fr", "French"),
        new("de", "German"),
        new("pt", "Portuguese"),
        new("ru", "Russian"),
        new("it", "Italian"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("vi", "Vietnamese"),
        new("nl", "Dutch"),
        new("pl", "Polish"),
        new("tr", "Turkish"),
        new("uk", "Ukrainian"),
        new("id", "Indonesian"),
        new("th", "Thai"),
    ];
}
```
  and replace the property at lines 247–272 (`/// <summary>Auto-detect + common Whisper languages ...` through the closing `];`) with:
```csharp
    /// <summary>See LanguageChoice.All - shared with the Re-transcribe dialog.</summary>
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } = LanguageChoice.All;
```
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\RetranscribeDialogViewModelTests.cs`:
```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Retranscription;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using LocalScribe.Core.Transcription;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class RetranscribeDialogViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-retrans-dialog-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public RetranscribeDialogViewModelTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void WriteBurstWav(string path)
    {
        using var sink = new WavSink(path);
        sink.Write(new float[16 * 300]);
        var burst = new float[16 * 1500];
        for (int i = 0; i < burst.Length; i++)
            burst[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * i / 16000.0));
        sink.Write(burst);
        sink.Write(new float[16 * 1000]);
    }

    private async Task<string> SeedFinalizedAsync()
    {
        string id = "2026-07-10_1000_Webex_seed";
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 1, 0, TimeSpan.Zero),
            DurationMs = 60000, Model = "small.en", Backend = "CUDA", Language = "en",
            Sources = [SourceKind.Local], RetainedAudioSources = [SourceKind.Local],
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Seed", Medium = Medium.Webex }, default);
        WriteBurstWav(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav));
        return id;
    }

    private (RetranscribeDialogViewModel Vm, RetranscriptionRunner Runner, MaintenanceService Maint,
        FakeUiErrorReporter Errors) Make(string sessionId, IReadOnlySet<string>? models = null)
    {
        var settings = new Settings();
        var maint = new MaintenanceService(_paths, new FakeSettingsService(settings),
            new FakeRecycleBin(), TimeProvider.System);
        var modelSet = models ?? new HashSet<string> { "base.en", "tiny.en" };
        var runner = new RetranscriptionRunner(_paths, () => settings, new FakeEngineFactory(),
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 13, 6, 0, 0, TimeSpan.Zero)),
            liveEngineBusy: () => null, availableModels: () => modelSet);
        var errors = new FakeUiErrorReporter();
        var vm = new RetranscribeDialogViewModel(sessionId, maint, runner, () => modelSet,
            errors, dispatch: a => a());
        return (vm, runner, maint, errors);
    }

    [Fact]
    public async Task ModelChoices_list_only_disk_models_and_gate_Start()
    {
        string id = await SeedFinalizedAsync();
        var (vm, _, _, _) = Make(id);
        Assert.Equal(new[] { "base.en", "tiny.en" }, vm.ModelChoices);   // Ordinal-sorted, no "auto"
        Assert.Equal("base.en", vm.SelectedModel);
        Assert.Equal("auto", vm.Language);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.CancelRunCommand.CanExecute(null));
        vm.Dispose();

        var (empty, _, _, _) = Make(id, models: new HashSet<string>());
        Assert.Empty(empty.ModelChoices);
        Assert.Null(empty.SelectedModel);
        Assert.False(empty.StartCommand.CanExecute(null));               // nothing on disk -> no Start
        empty.Dispose();
    }

    [Fact]
    public async Task LoadAsync_shows_the_current_version_line()
    {
        string id = await SeedFinalizedAsync();
        var (vm, _, _, _) = Make(id);
        await vm.LoadAsync(CancellationToken.None);
        Assert.Contains("v1", vm.CurrentVersionDisplay);
        Assert.Contains("small.en", vm.CurrentVersionDisplay);
        vm.Dispose();
    }

    [Fact]
    public async Task Start_runs_to_a_committed_version_infos_and_closes()
    {
        string id = await SeedFinalizedAsync();
        var (vm, _, _, errors) = Make(id);
        vm.Language = "en";
        bool closed = false;
        vm.Closed += () => closed = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.False(vm.IsRunning);
        Assert.Contains(errors.Infos, m => m.Contains("v2"));
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.StartsWith("v2-base.en-", session!.ActiveVersion);
        vm.Dispose();
    }
}
```
- [ ] **Run and see FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~RetranscribeDialogViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS0246: The type or namespace name 'RetranscribeDialogViewModel' could not be found`.
- [ ] **Implement the VM.** Create `src\LocalScribe.App\ViewModels\RetranscribeDialogViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Retranscription;
namespace LocalScribe.App.ViewModels;

/// <summary>WPF-free VM behind the plain-Window Re-transcribe dialog (design 2026-07-13 section
/// 3.4). Model picker = CANONICAL names of models actually on disk - ModelPaths.AvailableModels
/// collapses quantized ggml files (ggml-{name}-q8_0.bin) to their canonical name and
/// ModelFileResolver picks the file per backend at engine creation - and never "auto" (an
/// explicit re-run should be an explicit choice); language defaults to auto-detect. Start hands the run to the SHARED
/// RetranscriptionRunner and the dialog may close while it runs - completion lands on the
/// app-level reporter, the row chip rides the runner events, and Cancel works from any later
/// dialog instance because the cancellation lives in the runner, not here.</summary>
public sealed partial class RetranscribeDialogViewModel : ObservableObject, IDisposable
{
    private readonly string _sessionId;
    private readonly MaintenanceService _maintenance;
    private readonly RetranscriptionRunner _runner;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private bool _disposed;

    public RetranscribeDialogViewModel(string sessionId, MaintenanceService maintenance,
        RetranscriptionRunner runner, Func<IReadOnlySet<string>> availableModels,
        IUiErrorReporter errors, Action<Action> dispatch)
    {
        (_sessionId, _maintenance, _runner, _errors, _dispatch)
            = (sessionId, maintenance, runner, errors, dispatch);
        // availableModels = ModelPaths.AvailableModels in production: CANONICAL model names
        // (quantized files collapse via ModelFileResolver.CanonicalName), so every pick here is
        // a name BackendSelector.Select accepts and the runner's presence gate recognizes.
        ModelChoices = availableModels().OrderBy(m => m, StringComparer.Ordinal).ToList();
        SelectedModel = ModelChoices.FirstOrDefault();
        IsRunning = runner.RunningSessionId is not null;
        StartCommand = new AsyncRelayCommand(StartAsync, () => SelectedModel is not null && !IsRunning);
        CancelRunCommand = new RelayCommand(_runner.CancelCurrent, () => IsRunning);
        // A run started from ANOTHER dialog instance (or settling while this one is open) must
        // flip the gates here too. Named handlers so Dispose can detach - the runner is
        // app-lifetime and must not root closed dialogs.
        _runner.RetranscriptionStarted += OnRunnerActivity;
        _runner.RetranscriptionCompleted += OnRunnerActivity;
    }

    public IReadOnlyList<string> ModelChoices { get; }
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } = LanguageChoice.All;

    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private string _language = "auto";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _currentVersionDisplay = "";

    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelRunCommand { get; }
    /// <summary>Raised (dispatched) only on SUCCESS - the window closes itself; refusals and
    /// faults leave the dialog open with the reason on the reporter.</summary>
    public event Action? Closed;

    partial void OnSelectedModelChanged(string? value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        CancelRunCommand.NotifyCanExecuteChanged();
    }

    private void OnRunnerActivity(string _)
        => _dispatch(() => IsRunning = _runner.RunningSessionId is not null);

    /// <summary>The "Current: vN - model - date" info line (design section 3.4).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var item = await _maintenance.LoadSessionItemAsync(_sessionId, ct);
            if (item is null) return;
            var s = item.Session;
            var active = s.Versions.FirstOrDefault(v => v.Id == s.ActiveVersion);
            string line = active is null
                ? $"Current transcript: v1 \u00B7 {s.Model} \u00B7 {s.Backend}"
                : $"Current transcript: {TranscriptVersions.ShortId(active.Id)} \u00B7 {active.Model} "
                  + $"\u00B7 {active.CreatedAtUtc:yyyy-MM-dd}";
            _dispatch(() => CurrentVersionDisplay = line);
        }
        catch (Exception ex) { _errors.Report("Load session versions", ex); }
    }

    private async Task StartAsync()
    {
        string model = SelectedModel!;
        IsRunning = true;
        try
        {
            // Task.Run: the run is CPU-heavy (decode + whisper) and this VM's dispatch is the UI
            // thread; the runner owns cancellation (CancelCurrent), so no token is passed here.
            string? versionId = await Task.Run(() => _runner.RunAsync(new RetranscriptionRequest
            { SessionId = _sessionId, Model = model, Language = Language }, CancellationToken.None));
            if (versionId is not null)
            {
                _errors.Info($"Re-transcription complete - {TranscriptVersions.ShortId(versionId)} "
                    + "is now the active transcript.");
                _dispatch(() => Closed?.Invoke());
            }
            // null = refused: the runner already raised the reason through its Notice wiring.
        }
        catch (OperationCanceledException)
        {
            _errors.Info("Re-transcription cancelled - the partial version was discarded; "
                + "the session is unchanged.");
        }
        catch (Exception ex) { _errors.Report("Re-transcribe", ex); }
        finally { IsRunning = _runner.RunningSessionId is not null; }
    }

    /// <summary>Detaches the runner subscriptions (the only external-object subscriptions this
    /// VM makes) - same leak rule as MetadataEditorViewModel.Dispose. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runner.RetranscriptionStarted -= OnRunnerActivity;
        _runner.RetranscriptionCompleted -= OnRunnerActivity;
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — 3 passed. Then `--filter "FullyQualifiedName~SettingsPageViewModelTests"` — all pass (the language list moved, values identical).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/RetranscribeDialogViewModel.cs src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs tests/LocalScribe.App.Tests/RetranscribeDialogViewModelTests.cs
git commit -m "feat(app): RetranscribeDialogViewModel (disk-models picker, language, start/cancel) + shared LanguageChoice.All

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: `SessionRowViewModel.IsRetranscribing` + `SessionsPageViewModel` probe/command/event
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SessionRowViewModel.cs` (5th optional ctor param, lines 59–61; property after `IsRecovering`, line 54; set in ctor after line 87 `IsFinalizing = isFinalizing;`).
- Modify `src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs` (ctor lines 102–107 + field; row builds in `LoadAsync` lines 165–171, `RefreshRowAsync` lines 382–383, `UpsertRowAsync` lines 408–409; new command near line 82 + guard method after `RequestExport`, line 309).
- Test `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs` (add two `[Fact]`s).

**Interfaces:**
- Produces: `SessionRowViewModel(..., bool isFinalizing = false, bool isRetranscribing = false)` + `public bool IsRetranscribing { get; }` (drives the chip); `SessionsPageViewModel` ctor gains a FINAL optional param `Func<string?>? retranscribingSessionId = null` (every existing call site keeps compiling); `public IRelayCommand<SessionRowViewModel> RetranscribeSessionCommand { get; }`; `public event Action<string>? RetranscribeRequested;` (the window layer opens the dialog).
- Consumes: `RetranscriptionRunner.RunningSessionId` via the injected probe (wired in Task 13); existing `RequestExport` guard pattern, `_errors.Info`, `UpsertRowAsync` seam (the chip flips on/off through the same in-place upsert the Finalizing chip uses — no new collection machinery).

Steps:
- [ ] **Write the failing tests.** Append inside `SessionsPageViewModelTests` in `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs`:
```csharp
    [Fact]
    public async Task Rows_flag_retranscribing_from_the_injected_probe_and_clear_on_upsert()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-a", t, 480), Meta("Alpha"));
        await WriteSessionAsync(Rec("s-b", t.AddHours(1), 480), Meta("Bravo"));

        string? running = "s-a";
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var errors = new RecordingErrors();
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(), errors,
            dispatch: a => a(), TimeProvider.System, revealInExplorer: _ => { },
            retranscribingSessionId: () => running);
        await vm.OnNavigatedToAsync();

        Assert.True(vm.Rows.Single(r => r.Id == "s-a").IsRetranscribing);
        Assert.False(vm.Rows.Single(r => r.Id == "s-b").IsRetranscribing);

        running = null;                                   // the run settled
        await vm.UpsertRowAsync("s-a");                   // the completion wiring's upsert
        Assert.False(vm.Rows.Single(r => r.Id == "s-a").IsRetranscribing);
        Assert.Empty(errors.Reports);
        session.Dispose();
    }

    [Fact]
    public async Task RequestRetranscribe_guards_pending_and_running_rows_and_raises_for_finalized()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-done", t, 480), Meta("Done"));
        await WriteSessionAsync(Rec("s-pending", t.AddHours(1), 480, ended: false), Meta("Pending"));
        await WriteSessionAsync(Rec("s-running", t.AddHours(2), 480), Meta("Running"));

        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(),
            new RecordingErrors(), dispatch: a => a(), TimeProvider.System,
            revealInExplorer: _ => { }, retranscribingSessionId: () => "s-running");
        await vm.OnNavigatedToAsync();
        var requested = new List<string>();
        vm.RetranscribeRequested += requested.Add;

        vm.RetranscribeSessionCommand.Execute(vm.Rows.Single(r => r.Id == "s-pending"));
        vm.RetranscribeSessionCommand.Execute(vm.Rows.Single(r => r.Id == "s-running"));
        Assert.Empty(requested);                          // both refused with an Info, no event

        vm.RetranscribeSessionCommand.Execute(vm.Rows.Single(r => r.Id == "s-done"));
        Assert.Equal(new[] { "s-done" }, requested.ToArray());
        session.Dispose();
    }
```
- [ ] **Run and see FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~Rows_flag_retranscribing|FullyQualifiedName~RequestRetranscribe_guards" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS1739` on `retranscribingSessionId:` (and `CS1061` on `IsRetranscribing`/`RetranscribeSessionCommand`).
- [ ] **Widen `SessionRowViewModel`.** Replace the ctor signature (lines 59–61):
```csharp
    public SessionRowViewModel(SessionListItem item, TimeProvider time,
        IReadOnlyDictionary<string, (string? Reference, string Name)>? matterLookup = null,
        bool isFinalizing = false)
```
  with:
```csharp
    public SessionRowViewModel(SessionListItem item, TimeProvider time,
        IReadOnlyDictionary<string, (string? Reference, string Name)>? matterLookup = null,
        bool isFinalizing = false, bool isRetranscribing = false)
```
  After line 54 (`public bool IsRecovering => IsPendingRecovery && !IsFinalizing;`) insert:
```csharp
    /// <summary>True while the shared RetranscriptionRunner is generating a new transcript
    /// version for THIS session (design 2026-07-13 section 3.4). Drives the "Re-transcribing..."
    /// chip; flips on/off through the same UpsertRowAsync in-place seam as IsFinalizing.</summary>
    public bool IsRetranscribing { get; }
```
  After line 87 (`IsFinalizing = isFinalizing;`) insert:
```csharp
        IsRetranscribing = isRetranscribing;
```
- [ ] **Thread the probe + command through `SessionsPageViewModel`.** Add the field beneath `private readonly Action<string> _revealInExplorer;` (line 32):
```csharp
    private readonly Func<string?>? _retranscribingSessionId;
```
  Replace the ctor signature (lines 102–104):
```csharp
    public SessionsPageViewModel(MaintenanceService maintenance, SessionViewModel session,
        WindowRegistry registry, IUiErrorReporter errors, Action<Action> dispatch,
        TimeProvider time, Action<string> revealInExplorer)
```
  with:
```csharp
    public SessionsPageViewModel(MaintenanceService maintenance, SessionViewModel session,
        WindowRegistry registry, IUiErrorReporter errors, Action<Action> dispatch,
        TimeProvider time, Action<string> revealInExplorer,
        Func<string?>? retranscribingSessionId = null)
```
  and in the ctor body (after the tuple assignment, line 107) add:
```csharp
        _retranscribingSessionId = retranscribingSessionId;
        RetranscribeSessionCommand = new RelayCommand<SessionRowViewModel>(RequestRetranscribe);
```
  Declare the command + event after `ExportSessionCommand` (line 82) / the `ExportRequested` doc block (line 88):
```csharp
    public IRelayCommand<SessionRowViewModel> RetranscribeSessionCommand { get; }

    /// <summary>Raised with the session id from the action bar / row context menu's
    /// "Re-transcribe..." item (design 2026-07-13 section 3.4); the window layer owns the shared
    /// RetranscribeDialog. Guarded like the export flow (live-recording, pending-recovery) plus
    /// the one-run-at-a-time chip state.</summary>
    public event Action<string>? RetranscribeRequested;
```
  In `LoadAsync`'s `_dispatch` block replace the row build (lines 165–171):
```csharp
                string? finalizingId = _session.FinalizingSessionId;
                _all = result.Sessions
                    .OrderByDescending(s => s.Session.StartedAtUtc)
                    .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new SessionRowViewModel(s, _time, MatterLookup,
                        isFinalizing: s.Id == finalizingId))
                    .ToList();
```
  with:
```csharp
                string? finalizingId = _session.FinalizingSessionId;
                string? retranscribingId = _retranscribingSessionId?.Invoke();
                _all = result.Sessions
                    .OrderByDescending(s => s.Session.StartedAtUtc)
                    .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new SessionRowViewModel(s, _time, MatterLookup,
                        isFinalizing: s.Id == finalizingId,
                        isRetranscribing: s.Id == retranscribingId))
                    .ToList();
```
  In `RefreshRowAsync` replace the row build (lines 382–383) with:
```csharp
                list[i] = new SessionRowViewModel(item, _time, MatterLookup,
                    isFinalizing: sessionId == _session.FinalizingSessionId,
                    isRetranscribing: sessionId == _retranscribingSessionId?.Invoke());
```
  In `UpsertRowAsync` replace the row build (lines 408–409) with:
```csharp
                var newRow = new SessionRowViewModel(item, _time, MatterLookup,
                    isFinalizing: sessionId == _session.FinalizingSessionId,
                    isRetranscribing: sessionId == _retranscribingSessionId?.Invoke());
```
  After `RequestExport` (its closing brace, line 309) insert:
```csharp

    /// <summary>Guarded exactly like RequestExport (design 2026-07-13 section 3.2): a live
    /// recording and a pending-recovery row are refused with an actionable Info; a row already
    /// being re-transcribed is refused (one run at a time). The runner re-checks every guard
    /// against disk truth, so this is UX, not the enforcement.</summary>
    private void RequestRetranscribe(SessionRowViewModel? row)
    {
        if (row is null) return;
        if (row.Id == _session.CurrentSessionId
            && _session.State is SessionState.Recording or SessionState.Paused or SessionState.Finalizing)
        {
            _errors.Info("Cannot re-transcribe: this session is recording. Stop the recording first.");
            return;
        }
        if (row.IsPendingRecovery)
        {
            _errors.Info("Cannot re-transcribe: this session is still being recovered. Try again once recovery completes.");
            return;
        }
        if (row.IsRetranscribing)
        {
            _errors.Info("A re-transcription of this session is already running.");
            return;
        }
        RetranscribeRequested?.Invoke(row.Id);
    }
```
- [ ] **Run tests and see PASS.** Same filter — 2 passed. Then `--filter "FullyQualifiedName~SessionsPageViewModelTests|FullyQualifiedName~SessionRowMatterChipsTests|FullyQualifiedName~SessionRowSourceTests"` — all pass (existing 2/3/4-arg row call sites and the 7-arg VM call sites compile via the defaults).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SessionRowViewModel.cs src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs
git commit -m "feat(app): Re-transcribing row flag + RetranscribeSessionCommand with export-style guards

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 11: Read view — version badge + switcher (persists ActiveVersion, reloads projection/edits/speakers)
**Files:**
- Modify `src\LocalScribe.App\ViewModels\ReadViewViewModel.cs` (new `VersionOption` record above the class; collections + switch method; move `ModelBackendFooter` assignment from `Apply` line 198 into `ApplyRows` after line 228).
- Test: new file `tests\LocalScribe.App.Tests\ReadViewVersionSwitchTests.cs`.

**Interfaces:**
- Produces: `public sealed record VersionOption(string Id, string Label);` (`LocalScribe.App.ViewModels`); on `ReadViewViewModel`: `ObservableCollection<VersionOption> VersionOptions`, `VersionOption? SelectedVersionOption` (user pick triggers the switch; programmatic sync is guarded), `bool HasVersions` (dropdown visibility — a v1-only session shows the plain footer only), `public Task SwitchVersionAsync(string versionId, CancellationToken ct)`.
- Consumes: `MaintenanceService.SetActiveVersionAsync` (Task 6), `ReloadRowsAsync` (existing — deliberately NOT `LoadAsync`: playback must not re-resolve, `DualMediaPlayer.Load` re-subscribes per call), `TranscriptVersions.ShortId/Root`, `LoadedView.Session.ActiveVersion/Versions` (already carried).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\ReadViewVersionSwitchTests.cs`:
```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ReadViewVersionSwitchTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-versions-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public ReadViewVersionSwitchTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class NoopDualPlayer : IDualAudioPlayer
    {
        public void Load(string? localPath, string? remotePath) { }
        public void Play() { }
        public void Pause() { }
        public void SeekMs(long ms) { }
        public void SetLegMuted(bool local, bool muted) { }
        public void SetLegVolume(bool local, double volume) { }
        public long PositionMs => 0;
        public long DurationMs => 0;
        public event Action? MediaReady { add { } remove { } }
        public event Action? MediaEnded { add { } remove { } }
        public void Dispose() { }
    }

    private const string Vid = "v2-tiny.en-2026-07-13";

    private async Task<string> SeedAsync(bool withVersion)
    {
        string id = "2026-07-10_1000_Webex_seed";
        Directory.CreateDirectory(_paths.SessionDir(id));
        var record = new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 1, 0, TimeSpan.Zero),
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            DurationMs = 60000, Model = "small.en", Backend = "CUDA", Language = "en",
        };
        if (withVersion)
        {
            Directory.CreateDirectory(_paths.VersionDir(id, Vid));
            var vT = new TranscriptStore(_paths.TranscriptJsonl(id, Vid));
            await vT.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "V2 words.", "Me"), default);
            await JsonFile.WriteAsync(_paths.EditsJson(id, Vid), new Edits(), default);
            record = record with
            {
                ActiveVersion = Vid,
                Versions = new[] { new TranscriptVersion { Id = Vid, Model = "tiny.en", Backend = "CPU", Language = "en" } },
            };
        }
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(record, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Seed", Medium = Medium.Webex }, default);
        var rootT = new TranscriptStore(_paths.TranscriptJsonl(id));
        await rootT.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Root words.", "Me"), default);
        return id;
    }

    private ReadViewViewModel MakeVm()
        => new(new MaintenanceService(_paths, new FakeSettingsService(), new FakeRecycleBin(),
                TimeProvider.System),
            _paths, new FakeSettingsService(), new FakeUiErrorReporter(), new NoopDualPlayer(),
            dispatch: a => a(), TimeProvider.System);

    [Fact]
    public async Task Load_shows_the_active_versions_rows_badge_and_footer()
    {
        string id = await SeedAsync(withVersion: true);
        var vm = MakeVm();
        await vm.LoadAsync(id, CancellationToken.None);

        Assert.Equal("V2 words.", vm.Rows.Single(r => !r.Data.IsMarker).Data.Text);
        Assert.True(vm.HasVersions);
        Assert.Equal(new[] { "v1 \u00B7 small.en", "v2 \u00B7 tiny.en" },
            vm.VersionOptions.Select(o => o.Label).ToArray());
        Assert.Equal(Vid, vm.SelectedVersionOption?.Id);
        Assert.Equal("tiny.en \u00B7 CPU", vm.ModelBackendFooter);

        // The programmatic selection sync must NOT have written anything.
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(Vid, session!.ActiveVersion);
        vm.Dispose();
    }

    [Fact]
    public async Task Switching_persists_activeVersion_and_reloads_rows_and_footer()
    {
        string id = await SeedAsync(withVersion: true);
        var vm = MakeVm();
        await vm.LoadAsync(id, CancellationToken.None);

        await vm.SwitchVersionAsync("v1", CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("v1", session!.ActiveVersion);                    // persisted
        Assert.Equal("Root words.", vm.Rows.Single(r => !r.Data.IsMarker).Data.Text);
        Assert.Equal("v1", vm.SelectedVersionOption?.Id);
        Assert.Equal("small.en \u00B7 CUDA", vm.ModelBackendFooter);
        vm.Dispose();
    }

    [Fact]
    public async Task Unversioned_session_hides_the_switcher()
    {
        string id = await SeedAsync(withVersion: false);
        var vm = MakeVm();
        await vm.LoadAsync(id, CancellationToken.None);
        Assert.False(vm.HasVersions);
        Assert.Equal("small.en \u00B7 CUDA", vm.ModelBackendFooter);   // footer moved, value unchanged
        vm.Dispose();
    }
}
```
- [ ] **Run and see FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ReadViewVersionSwitchTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS1061: 'ReadViewViewModel' does not contain a definition for 'HasVersions'` (and `VersionOptions`/`SelectedVersionOption`/`SwitchVersionAsync`).
- [ ] **Implement.** In `ReadViewViewModel.cs`:
  1. Above the class declaration (line 21) insert:
```csharp
/// <summary>One entry in the read-view version dropdown (design 2026-07-13 section 3.4):
/// Id is "v1" or a TranscriptVersion.Id; Label is the badge form: short id, middle dot, model.</summary>
public sealed record VersionOption(string Id, string Label);

```
  2. After the `_canEdit` observable property (line 57) insert:
```csharp

    /// <summary>Version badge + switcher (design 2026-07-13 section 3.4). Rebuilt by every
    /// ApplyRows under _syncingVersions so the programmatic selection never re-triggers a
    /// switch; a USER pick flows through OnSelectedVersionOptionChanged -> SwitchVersionAsync.</summary>
    public ObservableCollection<VersionOption> VersionOptions { get; } = new();
    [ObservableProperty] private VersionOption? _selectedVersionOption;
    [ObservableProperty] private bool _hasVersions;
    private bool _syncingVersions;

    partial void OnSelectedVersionOptionChanged(VersionOption? value)
    {
        if (_syncingVersions || value is null || !IsLoaded) return;
        _ = SwitchVersionAsync(value.Id, CancellationToken.None);     // fire-and-forget; catches inside
    }

    /// <summary>Persists ActiveVersion then reloads rows/edits/speakers/badges from disk via the
    /// gated ReloadRowsAsync - deliberately NOT LoadAsync: playback must not re-resolve
    /// (DualMediaPlayer.Load re-subscribes per call) and the audio legs are version-independent
    /// (design section 3.3). Public so tests and the dropdown share one deterministic path.</summary>
    public async Task SwitchVersionAsync(string versionId, CancellationToken ct)
    {
        try
        {
            if (await _maintenance.SetActiveVersionAsync(SessionId, versionId, ct))
                await ReloadRowsAsync(ct);
        }
        catch (Exception ex) { _reporter.Report("Switch transcript version", ex); }
    }
```
  3. In `Apply` DELETE line 198 (`ModelBackendFooter = $"{view.Session.Model} \u00B7 {view.Session.Backend}";   // middle dot`) — the footer is version-dependent and now lives in `ApplyRows` (called by `Apply` on the next line region, so first-load behavior is identical).
  4. In `ApplyRows`, after the `CanEdit = view.Session.EndedAtUtc is not null;` line (line 228) insert:
```csharp
        // Version badge + switcher + footer (design 2026-07-13 section 3.4): options are v1 (the
        // root original) + every recorded version; the footer shows the ACTIVE version's actuals.
        _syncingVersions = true;
        try
        {
            var session = view.Session;
            VersionOptions.Clear();
            VersionOptions.Add(new VersionOption(TranscriptVersions.Root, $"v1 \u00B7 {session.Model}"));
            foreach (var v in session.Versions)
                VersionOptions.Add(new VersionOption(v.Id,
                    $"{TranscriptVersions.ShortId(v.Id)} \u00B7 {v.Model}"));
            HasVersions = session.Versions.Count > 0;
            SelectedVersionOption = VersionOptions.FirstOrDefault(o => o.Id == session.ActiveVersion)
                ?? VersionOptions[0];
            var active = session.Versions.FirstOrDefault(v => v.Id == session.ActiveVersion);
            ModelBackendFooter = active is null
                ? $"{session.Model} \u00B7 {session.Backend}"
                : $"{active.Model} \u00B7 {active.Backend}";
        }
        finally { _syncingVersions = false; }
```
- [ ] **Run tests and see PASS.** Same filter — 3 passed. Then `--filter "FullyQualifiedName~ReadViewViewModelTests|FullyQualifiedName~ReadViewEditModeTests|FullyQualifiedName~ReadViewSpeakerChoicesTests|FullyQualifiedName~RosterChangedTests"` — all pass (footer relocation is value-identical for v1 sessions; nothing else observed `Apply` internals).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/ReadViewViewModel.cs tests/LocalScribe.App.Tests/ReadViewVersionSwitchTests.cs
git commit -m "feat(app): read-view version badge + switcher persisting ActiveVersion via SetActiveVersionAsync

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 12: Session Details entry — `MetadataEditorViewModel.RetranscribeCommand` + `RetranscribeRequested`
**Files:**
- Modify `src\LocalScribe.App\ViewModels\MetadataEditorViewModel.cs` (command declaration after line 139 `public IRelayCommand DiariseCommand { get; }`; event after line 155's `DiariseRequested` block; ctor init after line 187 `DiariseCommand = new RelayCommand(RequestDiarise, CanDiarise);`; notify in `Attach` after line 269 `DiariseCommand.NotifyCanExecuteChanged();`; guard method after `RequestDiarise`, line 283).
- Test: new file `tests\LocalScribe.App.Tests\MetadataEditorRetranscribeTests.cs`.

**Interfaces:**
- Produces: `public IRelayCommand MetadataEditorViewModel.RetranscribeCommand { get; }` (CanExecute: `_row is not null && !_row.IsPendingRecovery` — deliberately NO `IsDirty` gate: the run reads saved disk truth, never this editor's buffer, unlike Split's disk-count dependency); `public event Action<string>? RetranscribeRequested;`.
- Consumes: existing `_row`, `Attach` notify cluster, `DiariseCommand`/`DiariseRequested` as the exact pattern.

Steps:
- [ ] **Write the failing test.** Create `tests\LocalScribe.App.Tests\MetadataEditorRetranscribeTests.cs`:
```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MetadataEditorRetranscribeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-editor-retrans-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public MetadataEditorRetranscribeTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
        Directory.CreateDirectory(_paths.MattersDir);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static SessionRowViewModel Row(string id, bool ended)
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var rec = new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = t,
            EndedAtUtc = ended ? t.AddMinutes(1) : null,
            UtcOffsetMinutes = 480, TimeZoneId = "Singapore Standard Time",
            DurationMs = 60000, Model = "small.en", Backend = "CPU", Language = "en",
        };
        var meta = new SessionMeta { Title = "T", Medium = Medium.Webex };
        return new SessionRowViewModel(new SessionListItem(id, rec, meta), TimeProvider.System);
    }

    [Fact]
    public void Retranscribe_gates_on_an_attached_finalized_row_and_raises_with_the_id()
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettingsService(),
            new FakeRecycleBin(), TimeProvider.System);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var vm = new MetadataEditorViewModel(maintenance, session, new FakeUiErrorReporter(),
            a => a(), TimeProvider.System, confirm: _ => true);
        var requested = new List<string>();
        vm.RetranscribeRequested += requested.Add;

        Assert.False(vm.RetranscribeCommand.CanExecute(null));          // no row attached

        vm.Attach(Row("s-pending", ended: false));
        Assert.False(vm.RetranscribeCommand.CanExecute(null));          // pending-recovery row

        vm.Attach(Row("s-done", ended: true));
        Assert.True(vm.RetranscribeCommand.CanExecute(null));
        vm.RetranscribeCommand.Execute(null);
        Assert.Equal(new[] { "s-done" }, requested.ToArray());

        vm.Dispose();
        session.Dispose();
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~MetadataEditorRetranscribeTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\` — expected: `error CS1061: 'MetadataEditorViewModel' does not contain a definition for 'RetranscribeRequested'` (and `RetranscribeCommand`).
- [ ] **Implement (mirror the Diarise pattern exactly).** In `MetadataEditorViewModel.cs`:
  1. After line 139 (`public IRelayCommand DiariseCommand { get; }`) insert:
```csharp
    // Versioned re-transcription (design 2026-07-13 section 3.4): Session Details is the third
    // entry point beside the Sessions action bar and row context menu. Same disable-for-pending
    // gate as DiariseCommand; deliberately NO IsDirty gate - the run reads saved disk truth
    // (vocabulary from the SAVED matter tags), never this editor's buffer.
    public IRelayCommand RetranscribeCommand { get; }
```
  2. After the `DiariseRequested` event (line 155) insert:
```csharp

    /// <summary>Raised with the session id when "Re-transcribe..." is invoked on a finalized
    /// (non-pending) row; the window layer (App.xaml.cs' openSessionDetails factory) owns the
    /// shared RetranscribeDialog.</summary>
    public event Action<string>? RetranscribeRequested;
```
  3. In the ctor, after line 187 (`DiariseCommand = new RelayCommand(RequestDiarise, CanDiarise);`) insert:
```csharp
        RetranscribeCommand = new RelayCommand(RequestRetranscribe,
            () => _row is not null && !_row.IsPendingRecovery);
```
  4. In `Attach`, after line 269 (`DiariseCommand.NotifyCanExecuteChanged();`) insert:
```csharp
        RetranscribeCommand.NotifyCanExecuteChanged();
```
  5. After `RequestDiarise`'s closing brace (line 283) insert:
```csharp

    /// <summary>Belt-and-braces early return in addition to the CanExecute gate - same defense
    /// as RequestDiarise against a stale invocation racing an Attach.</summary>
    private void RequestRetranscribe()
    {
        if (_row is null || _row.IsPendingRecovery) return;
        RetranscribeRequested?.Invoke(_row.Id);
    }
```
- [ ] **Run tests and see PASS.** Same filter — 1 passed. Then `--filter "FullyQualifiedName~MetadataEditor"` — all existing editor suites pass.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/MetadataEditorViewModel.cs tests/LocalScribe.App.Tests/MetadataEditorRetranscribeTests.cs
git commit -m "feat(app): Session Details Re-transcribe command + request event (Diarise pattern)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 13: XAML surfaces + composition wiring + gate + manual smoke
**Files:**
- New `src\LocalScribe.App\RetranscribeDialog.xaml` + `src\LocalScribe.App\RetranscribeDialog.xaml.cs` (plain Window — the ExportDialog pattern; NOT a FluentWindow, per the Mica startup gotcha in project memory).
- Modify `src\LocalScribe.App\Pages\SessionsPage.xaml` (action bar after the `Export...` button, lines 59–61; context menu after the `Export...` item, lines 119–121; status chip after the Finalizing chip, lines 207–211).
- Modify `src\LocalScribe.App\ReadViewWindow.xaml` (header WrapPanel, after the `DurationDisplay` TextBlock, line 37).
- Modify `src\LocalScribe.App\SessionDetailsWindow.xaml` (Details tab, after the `Archived` CheckBox, lines 135–136).
- Modify `src\LocalScribe.App\CompositionRoot.cs` (`AppComposition` record lines 16–29; `Build()` after the `maintenance` line 80; return statement lines 103–105).
- Modify `src\LocalScribe.App\App.xaml.cs` (hoist `openRetranscribe` after `openSplitSpeakers`, line 197; subscribe inside `openSessionDetails` after line 227; `sessionsVm` ctor args lines 136–145).
- No new unit test (App.xaml.cs composition + XAML rendering are not unit-tested here). The "test" is: 0-warning build, full App+Core suites green (incl. `XamlHygieneTests`), plus the precise manual smoke below.

**Interfaces:**
- Consumes: everything produced in Tasks 6–12.
- Produces: `AppComposition` gains a final positional field `LocalScribe.Core.Retranscription.RetranscriptionRunner Retranscription`; both one-engine-at-a-time directions wired in `CompositionRoot.Build`.

Steps:
- [ ] **Create the dialog window.** `src\LocalScribe.App\RetranscribeDialog.xaml`:
```xml
<Window x:Class="LocalScribe.App.RetranscribeDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Re-transcribe session" Width="440" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize">
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
    </Window.Resources>
    <StackPanel Margin="16">
        <TextBlock Text="{Binding CurrentVersionDisplay}" Opacity="0.8" Margin="0,0,0,8" />
        <TextBlock Text="Model (downloaded models only)" FontWeight="SemiBold" Margin="0,0,0,4" />
        <ComboBox ItemsSource="{Binding ModelChoices}" SelectedItem="{Binding SelectedModel}"
                  Margin="0,0,0,8" />
        <TextBlock Text="Language" FontWeight="SemiBold" Margin="0,0,0,4" />
        <ComboBox ItemsSource="{Binding LanguageChoices}" DisplayMemberPath="Name"
                  SelectedValuePath="Code" SelectedValue="{Binding Language}" Margin="0,0,0,8" />
        <TextBlock TextWrapping="Wrap" Opacity="0.7" Margin="0,0,0,8"
                   Text="The original transcript is never modified. The new version is stored beside it and becomes the active transcript when it completes; you can switch back any time from the transcript view." />
        <TextBlock FontStyle="Italic" Margin="0,0,0,8"
                   Text="Re-transcribing... you can close this dialog; the session row tracks progress."
                   Visibility="{Binding IsRunning, Converter={StaticResource BoolToVis}}" />
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Start" IsDefault="True" Command="{Binding StartCommand}"
                    Margin="0,0,8,0" MinWidth="90" />
            <Button Content="Cancel run" Command="{Binding CancelRunCommand}"
                    Margin="0,0,8,0" MinWidth="90"
                    ToolTip="Stops the running re-transcription and discards its partial version" />
            <Button Content="Close" IsCancel="True" MinWidth="90" />
        </StackPanel>
    </StackPanel>
</Window>
```
  `src\LocalScribe.App\RetranscribeDialog.xaml.cs`:
```csharp
using System.Windows;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App;

public partial class RetranscribeDialog : Window
{
    public RetranscribeDialog(RetranscribeDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += Close;                                  // run succeeded -> close the dialog
        Loaded += (_, _) => _ = vm.LoadAsync(CancellationToken.None);
        Closed += (_, _) => vm.Dispose();                    // detach the runner subscriptions
    }
}
```
- [ ] **Sessions page surfaces.** In `SessionsPage.xaml`:
  1. After the action-bar `Export...` button (lines 59–61) insert:
```xml
            <ui:Button Content="Re-transcribe..." Margin="0,0,8,0"
                       IsEnabled="{Binding HasSelection}"
                       Command="{Binding RetranscribeSessionCommand}" CommandParameter="{Binding SelectedRow}" />
```
  2. After the context-menu `Export...` item (lines 119–121) insert:
```xml
                                <MenuItem Header="Re-transcribe..."
                                          Command="{Binding Data.RetranscribeSessionCommand, Source={StaticResource VmProxy}}"
                                          CommandParameter="{Binding}" />
```
  3. After the Finalizing chip's `</Border>` (line 211) insert:
```xml
                                <Border Style="{StaticResource Chip}"
                                        ToolTip="A new transcript version is being generated; the row updates itself when it completes"
                                        Visibility="{Binding IsRetranscribing, Converter={StaticResource BoolToVis}}">
                                    <TextBlock Text="Re-transcribing..." FontStyle="Italic" />
                                </Border>
```
- [ ] **Read-view badge.** In `ReadViewWindow.xaml`, after the `DurationDisplay` TextBlock (line 37) insert:
```xml
                <ComboBox ItemsSource="{Binding VersionOptions}" DisplayMemberPath="Label"
                          SelectedItem="{Binding SelectedVersionOption}"
                          MinWidth="130" Margin="0,0,12,4" VerticalAlignment="Center"
                          ToolTip="Transcript version. Switching changes which transcript this session shows, edits, and exports; the original v1 is always preserved."
                          Visibility="{Binding HasVersions, Converter={StaticResource BoolToVis}}" />
```
- [ ] **Session Details button.** In `SessionDetailsWindow.xaml`, after the `Archived` CheckBox (lines 135–136) insert:
```xml
                        <!-- Versioned re-transcription (design 2026-07-13 3.4): opens the shared
                             re-transcribe dialog; disabled for a pending/in-progress row via the
                             command's CanExecute. -->
                        <ui:Button Content="Re-transcribe..." Appearance="Secondary"
                                   HorizontalAlignment="Left" Margin="4,12,0,0"
                                   Command="{Binding RetranscribeCommand}"
                                   ToolTip="Generate a new transcript version with a different model or language; the original is preserved" />
```
- [ ] **Composition.** In `CompositionRoot.cs`:
  1. Add `using LocalScribe.Core.Retranscription;` to the usings block (after `using LocalScribe.Core.Model;`, line 6).
  2. In the `AppComposition` record (lines 16–29) append a final positional parameter after `IAudioSessionScanner Scanner`:
```csharp
    IAudioSessionScanner Scanner,
    RetranscriptionRunner Retranscription);
```
  3. In `Build()`, after `var maintenance = new MaintenanceService(...)` (line 80) insert:
```csharp

        // Versioned re-transcription (design 2026-07-13 section 3.2): shares the controller's
        // engine-factory/VAD/probe adapters. BOTH one-engine-at-a-time directions are wired here:
        // the runner probes the live controller (forward), and the controller refuses Start
        // while the runner owns the engine (reverse, via the settable seam - the runner is
        // constructed after the controller, so a ctor param cannot express the cycle).
        var retranscription = new RetranscriptionRunner(paths, current, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(), () => new StopwatchClock(), TimeProvider.System,
            liveEngineBusy: () => controller.State != SessionState.Idle
                ? "Cannot re-transcribe while a recording is in progress - stop the recording first."
                : !controller.PendingFinalize.IsCompleted
                    ? "The previous recording is still finalizing its transcript - try again in a moment."
                    : null);
        controller.ExternalEngineBusy = () => retranscription.RunningSessionId is string rid
            ? $"Cannot start recording - a re-transcription ({rid}) is still running."
            : null;
```
  4. Extend the return (lines 103–105) with the new final argument:
```csharp
        return new AppComposition(controller, settingsService, paths, maintenance,
            new WindowRegistry(), recycleBin, appVersion, diarisation, remoteOverride, matterSelection,
            micOverride, deviceEnumerator, scanner, retranscription);
```
  (If `CompositionRootTests` constructs `AppComposition` positionally, append a runner built over the same fakes there too — locate by the `new AppComposition(` quote.)
- [ ] **App wiring.** In `App.xaml.cs`:
  1. Immediately after `openSplitSpeakers`'s closing `};` (line 197) insert:
```csharp

        // Versioned re-transcription (design 2026-07-13 section 3.4): a fresh VM + plain Window
        // per request (short-lived, same pattern as openExport). Hoisted above openSessionDetails
        // because the details editor's RetranscribeRequested must reference it (a lambda cannot
        // reference a local declared later - same ordering rule as openSplitSpeakers). The dialog
        // may close while the run continues; a re-opened dialog shows the in-flight state.
        Action<string> openRetranscribe = sessionId =>
        {
            var retransVm = new ViewModels.RetranscribeDialogViewModel(sessionId, comp.Maintenance,
                comp.Retranscription, LocalScribe.Core.Transcription.ModelPaths.AvailableModels,
                errors, dispatch);
            new RetranscribeDialog(retransVm) { Owner = MainWindow }.ShowDialog();
        };
        sessionsVm.RetranscribeRequested += openRetranscribe;
        // Row chip + read-view refresh: Started flips the "Re-transcribing..." chip on through
        // the same in-place upsert seam the finalize path uses; Completed (success, refusal,
        // fault, or cancel) flips it off and re-reads disk truth (ActiveVersion may have
        // changed). NotifyRosterChanged reuses the existing per-session read-view refresh
        // channel (RosterChanged -> RefreshRosterAsync -> gated reload) so an open read view
        // picks up the new active version's rows + badge without a reopen; its Edit mode
        // deliberately survives untouched (RefreshRosterAsync's documented contract).
        comp.Retranscription.RetranscriptionStarted += id => dispatch(() => _ = sessionsVm.UpsertRowAsync(id));
        comp.Retranscription.RetranscriptionCompleted += id => dispatch(() =>
        {
            _ = sessionsVm.UpsertRowAsync(id);
            comp.Windows.NotifyRosterChanged(id);
        });
        comp.Retranscription.Notice += m => dispatch(() => errors.Info(m));
```
  NOTE: `sessionsVm.RetranscribeRequested += openRetranscribe;` must sit AFTER `sessionsVm` is constructed — if this insertion point (line 197) precedes the `sessionsVm` declaration in your checkout, keep `openRetranscribe` + the `comp.Retranscription.*` wiring here and move ONLY the `sessionsVm.RetranscribeRequested += openRetranscribe;` line down beside `sessionsVm.ExportRequested += openExport;` (line 289). At master `7d6c88d` (App.xaml.cs untouched by the cpu-threads merge), `sessionsVm` is declared at line 136, before this point, so the block works as written.
  2. Inside `openSessionDetails`, after `detailEditor.DiariseRequested += openSplitSpeakers;` (line 227) insert:
```csharp
            detailEditor.RetranscribeRequested += openRetranscribe;
```
  3. In the `sessionsVm` construction (lines 136–145), add the probe argument after `revealInExplorer: ...`'s closing `}` (before the `);`):
```csharp
            ,
            retranscribingSessionId: () => comp.Retranscription.RunningSessionId
```
- [ ] **Build 0-warning + full App/Core suites green.** Run:
```
dotnet build "LocalScribe.slnx" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\retranscription-versions\
```
  Expected: build 0 warnings; App suite all green including `XamlHygieneTests` (the new XAML adds no hardcoded ARGB brush, no keyless `<Style TargetType="TextBlock">`, no `TextFillColorPrimaryBrush` literal; the dialog is a plain Window exactly like `ExportDialog.xaml`, which already passes); Core suite green (2 known fixture fails only).
- [ ] **Manual smoke (WPF — not unit-testable here).** Launch the app, then:
  1. Sessions page: select a finalized session with retained audio → action-bar "Re-transcribe…" → dialog shows "Current transcript: v1 · <model> …", the model list contains ONLY downloaded models (no "auto"), language defaults to Auto-detect.
  2. Press Start → the row shows a "Re-transcribing…" chip with no scroll/selection jump; when it completes the chip clears WITHOUT a manual Refresh and an InfoBar message names the new version.
  3. Open the transcript (read view): the text is the NEW version; the header shows the version dropdown ("v2 · <model>" selected); the footer shows the new model · backend.
  4. Switch the dropdown to "v1 · <model>": text flips to the original; close and reopen the read view → still v1 (ActiveVersion persisted). Switch back to v2.
  5. Edit mode on v2: correct one line, Save → "(edited)" shows. Switch to v1 → the correction is NOT there (per-version isolation); switch back to v2 → it is.
  6. Start a re-transcription, CLOSE the dialog while it runs → the chip stays and the run completes (InfoBar). Re-open the dialog mid-run on the same session → it shows "Re-transcribing…" with Start disabled; press "Cancel run" → InfoBar says cancelled, the chip clears, and the session folder has NO partial `versions\` folder left; the session still opens at its previous version.
  7. One-engine-at-a-time: while a re-transcription runs, press Record → refused with a notice; while recording, invoke Re-transcribe on any row → refused with a notice.
  8. Export a versioned session as .zip → the archive contains `versions/v2-.../transcript.jsonl` etc.; export as .docx → the page footer reads the configured footer plus "Transcript version v2 (<model>)".
  9. Session Details: the "Re-transcribe…" button under Archived opens the same dialog; it is greyed for a "Recovering…" row. Row context menu "Re-transcribe…" also opens it.
  10. Split speakers on a v2-active session with a splittable leg: run + confirm → the speaker labels apply to the v2 transcript; switch to v1 → v1's labels are unchanged.
- [ ] **Commit.**
```
git add src/LocalScribe.App/RetranscribeDialog.xaml src/LocalScribe.App/RetranscribeDialog.xaml.cs src/LocalScribe.App/Pages/SessionsPage.xaml src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/SessionDetailsWindow.xaml src/LocalScribe.App/CompositionRoot.cs src/LocalScribe.App/App.xaml.cs
git commit -m "feat(app): re-transcribe dialog + entry points, Re-transcribing chip, read-view badge wiring, engine-guard composition

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---
## Self-review

**(a) Spec coverage — every design §3 requirement maps to a task:**
- §3.1 storage (root = immutable v1; `versions\vN-<model>-<yyyy-MM-dd>\` with own `transcript.jsonl`, fresh empty `edits.json`, `speakers.json` absent-until-Split, rendered projections; `session.json` gains `activeVersion` + `versions[]` with id/model/backend/createdAt/vocabulary-applied; `SessionMigrator` bumps the schema, old sessions read as v1 + empty list) → **Task 1** (model + v4 migration), **Task 2** (paths), **Task 8** (folder creation, empty edits, projections, single-write commit; root-untouched pinned by test).
- §3.2 engine (`RetranscriptionRunner` drives the existing offline pipeline from the FLAC/WAV legs via `FlacPcmReader`, current global+matter vocabulary as prompt bias; guards: blocked for Recording/Finalizing/Recovering sessions — enforced as the on-disk `EndedAtUtc is null` check, which is exactly the state all three of those lifecycle phases share — one-engine-at-a-time BOTH directions, one re-transcription at a time; cancel discards the partial folder) → **Task 7** (controller's reverse guard reusing the existing Start-refusal seam; the forward direction reuses the existing exclusivity signals `State`/`PendingFinalize`, the same seam `StartAsync` itself awaits) + **Task 8** (runner + guards + cancel + vocabulary, each pinned by a test).
- §3.3 semantics (per-version corrections/speaker overlays, no auto-carry; editing + Split operate on the ACTIVE version; export .zip carries all versions, .docx renders the active version with a version-note footer; playback version-independent) → **Task 4** (EditStore content-dir), **Task 5** (loader/writer resolution), **Task 6** (MaintenanceService edit/pin/diarise routing + `SetActiveVersionAsync` + docx footer + SplitSpeakers transcript read), **Task 3** (zip recursion — the design's "by construction" claim was verified FALSE at master, `SessionArchiver` was top-level-only, so this plan fixes it), **Task 11** (switch reloads rows WITHOUT re-resolving playback).
- §3.4 UI (entries in Sessions action bar + row context menu + Session Details; dialog with current-version info, disk-models-only picker, language selector, Start/Cancel, closable-without-cancelling; "Re-transcribing…" chip via the live-autoupdate seam; completion flips active; read-view badge "v2 · small.en" + dropdown that persists and reloads) → **Task 9** (dialog VM), **Task 10** (chip flag + command/guards), **Task 11** (badge + switcher), **Task 12** (Session Details command), **Task 13** (XAML + wiring + smoke 1–10).
- §3.5 testing (version folder creation/naming; ActiveVersion resolution through `SessionProjectionLoader` for read view/export; migrator round-trip; guards incl. concurrent; cancel cleanup; per-version isolation of edits and speaker overlays; docx footer note) → Tasks 1, 5, 6, 8, 10, 11 test lists; search-follows-active is the search plan's concern (it consumes the loader overload produced here).
- §1/§7 evidentiary + anti-patterns: the runner's single-commit-point structure makes "cancel deletes only a PARTIAL folder" a code-shape guarantee (`catch when (!committed)`), completed versions have NO delete path anywhere, root content immutability is pinned bit-for-bit in `Run_creates_v2_...`, nothing auto-expires, and re-transcription is additive — never destructive (the Meetily anti-pattern this section exists to avoid).
- Cross-plan contract: every name matches the required list verbatim (`ActiveVersion`, `Versions`, `TranscriptVersion{Id,Model,Backend,CreatedAtUtc,VocabularyApplied}` + additive `Language`, `StoragePaths.VersionDir` + overloads with `"v1"` → root, loader default-follows-active + explicit overload, `Retranscription\RetranscriptionRunner`). **No §3 requirement is left unmapped.**

**(b) Placeholder scan:** every step carries real, grounded code — anchors read from master `7d6c88d`: `SessionRecord.cs` 8/38/66, `SessionStore.cs` 8, `SessionMigrator.cs` 7/21–32, `StoragePaths.cs` 24, `SessionArchiver.cs` 15–20, `EditStore.cs` 10–19, `SessionProjectionLoader.cs` 12–23/27–41/61–62/78–79, `SessionWriter.cs` 19–28, `SessionController.cs` 154/323–327, `MaintenanceService.cs` 107/121/138/178/200/208/239/250–254/319/565–566, `SplitSpeakersViewModel.cs` 200/207, `SettingsPageViewModel.cs` 14/247–272, `SessionRowViewModel.cs` 54/59–61/87, `SessionsPageViewModel.cs` 32/82/88/102–107/165–171/309/382–383/408–409, `ReadViewViewModel.cs` 21/57/198/228, `MetadataEditorViewModel.cs` 139/155/187/269/283, `SessionsPage.xaml` 59–61/119–121/207–211, `ReadViewWindow.xaml` 37, `SessionDetailsWindow.xaml` 135–136, `CompositionRoot.cs` 6/16–29/80/103–105, `App.xaml.cs` 136–145/197/227/289. Existing-test pins to update are enumerated exactly (`SessionMigratorTests` 29/52/89/96/116/122, `SessionStoreTests` 45/66/84, `SessionCatalogTests` 111). Test helpers were verified to exist before reuse: `FakeEngineFactory`/`AmplitudeSpeechModel`/`GatedEngineFactory`/`LiveTestDoubles`/`ManualUtcTimeProvider` (Core.Tests, compile-linked into App.Tests), `StaticHardwareProbe`/`FakeClock`/`WavSink` (production Core), `FakeSettingsService`/`FakeUiErrorReporter`/`FakeRecycleBin` (AppServiceFakes), `SeedAsync`/`T0` (SessionProjectionLoaderTests), `Rec`/`Meta`/`WriteSessionAsync`/`MakeVm`-style construction (SessionsPageViewModelTests). Two deliberate near-contingencies are flagged inline rather than hidden: the `AudioFrame` ctor-order note in Task 8 (mirror `WavFileFrameReader` line 36 — the quoted contract) and the `CompositionRootTests` positional-ctor note in Task 13. No "TBD"/"similar to"/"add error handling here" anywhere.

**(c) Type consistency across tasks:** `ActiveVersion` is `string` (default `"v1"`) and `Versions` is `IReadOnlyList<TranscriptVersion>` (Task 1), consumed by the loader (Task 5), MaintenanceService (Task 6), the runner's commit (Task 8: `Versions = current.Versions.Append(entry).ToList()` — a `List<TranscriptVersion>` satisfies `IReadOnlyList`), and the read view (Task 11). `TranscriptVersions.Root` is the one `"v1"` constant used by `StoragePaths.VersionDir` (Task 2), the loader (Task 5), MaintenanceService (Task 6), and the runner. `EditStore(string, TimeProvider, string? contentDir = null)` (Task 4) is called 3-arg by the loader (Task 5), MaintenanceService (Task 6), and the version tests, while every pre-existing 2-arg site compiles via the default. `LoadedProjection.VersionId` (`string`, appended last positionally) is consumed by `SessionWriter` (Task 5) and the docx footer (Task 6); the loader is the record's only constructor. `SetActiveVersionAsync(string, string, CancellationToken) : Task<bool>` (Task 6) is consumed by `SwitchVersionAsync` (Task 11). `ExternalEngineBusy`/`liveEngineBusy` are both `Func<string?>` (reason-or-null) on both sides of the guard (Tasks 7, 8, 13). `RetranscriptionRunner.RunAsync(RetranscriptionRequest, CancellationToken) : Task<string?>` and `RunningSessionId : string?` (Task 8) are consumed by the dialog VM (Task 9: `Func<IReadOnlySet<string>>` matches `ModelPaths.AvailableModels`'s signature exactly) and the Sessions probe (`Func<string?>` — Tasks 10, 13). `SessionRowViewModel`'s new 5th param `bool isRetranscribing = false` and `SessionsPageViewModel`'s new final optional `Func<string?>? retranscribingSessionId = null` keep every existing call site (2/3/4-arg rows; 7-arg page VM in tests and `App.xaml.cs`) compiling until Task 13 threads the real probe. `VersionOption(string Id, string Label)` is built and consumed only inside the read view + its XAML `DisplayMemberPath="Label"`/`SelectedItem`. `LanguageChoice.All` keeps `SettingsPageViewModel.LanguageChoices`' type (`IReadOnlyList<LanguageChoice>`) byte-compatible. All good.

**Declared deviations & accepted risks (for the reviewer):**
1. **Schema bump follows the design (§3.1 "SessionMigrator bumps the schema") over the additive-fields precedent** (`MicSnapshot.FellBackToDefault` shipped without a bump): a v4 file's `activeVersion` changes WHICH transcript older code would display — a pre-versioning build silently showing v1 while the user corrected v2 is an evidentiary hazard, so `RejectIfNewer` refusing old readers is the point of the bump. Consequence: the first catalog list after upgrade write-migrates every old session.json to v4 (the established "reads are the migration event" behavior, per `SessionCatalog`'s own doc comment).
2. **The docx version note is composed in `MaintenanceService.ExportDocxAsync`, not inside `DocxRenderer`** — the renderer stays a pure serializer and the note joins `DocxFooterText` where that string already composes; the behavior is still pinned by an end-to-end footer test (Task 6).
3. **`SplitSpeakersViewModel` line-207 change has no dedicated unit test** (its VM harness needs an `IDiarisationEngine` double this plan doesn't otherwise touch); the storage-level behavior is pinned by Tasks 4–6 tests and the dialog path is smoke #10.
4. **`SaveDiarisationAsync`'s version routing is covered by the shared `ActiveVersionAsync` helper + existing diarisation suites** (which run v1 sessions through the identical `"v1"` path), not a new versioned-diarise test — `DiarisationCommit`/`SpeakersMerge` fixtures were out of scope to re-derive here.
5. **The one-engine guard is best-effort against a same-instant race** (runner claiming its slot while `StartAsync` is mid-prologue): both checks sit at each side's entry, matching the exactness of the existing `PendingFinalize` await; a perfect mutex across Core components was judged not worth the coupling for two user-initiated actions.
6. **`FlacPcmReader.ReadMono16k` loads a whole leg into memory** (~230 MB/hour of audio) — the same cost profile Split-speakers diarisation already accepts on this codebase; noted, not changed.
7. **Root truth fields (`Model`/`Backend`/`Language`/`SegmentCount`) keep describing v1 forever** — deliberate (the root record is the original's record); per-version actuals live in the `Versions` entries, and the read-view footer/docx footer/badge all read those.
8. **An orphaned partial folder from a hard crash (cancel-cleanup never ran) is skipped by the monotonic numbering and never auto-deleted** — nothing in this round expires or deletes anything unattended (design §7); it is unreferenced junk visible only in Explorer.
9. **Re-grounded @ `7d6c88d` (feat/cpu-threads-quantized-weights merge):** `TranscriptVersion` additionally records `WeightsFile` — the exact ggml file that ran (quantization is now a file-level detail `ModelFileResolver` picks per backend) — with the same null-means-nothing-transcribed semantics as `SessionRecord.WeightsFile`, and the runner mirrors the live path's per-segment `ts.WeightsFile` tracking (`PersistFinalAsync`/`OfflinePipelineRunner` parity), so a re-transcribed version carries the full evidentiary provenance chain. The dialog's model picker speaks CANONICAL names for free (`ModelPaths.AvailableModels` canonicalizes via `ModelFileResolver.CanonicalName`), and `BackendSelector.Select` re-canonicalizes explicit picks defensively. `BackendPlan.CpuThreads` needs no plan changes: `Select` attaches it and `WhisperEngineFactory` consumes `EffectiveThreads` transparently through the same `BackendPlan` the runner already passes around.
