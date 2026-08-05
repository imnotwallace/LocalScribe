# Tier 1A: Diagnosability — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make LocalScribe's failures observable. Today `DispatcherUnhandledException` sets `Handled = true` with the comment "for now, swallow it", `Settings.Logging` is declared and read by zero production code, there is no log file anywhere in the product, and no csproj sets a version — so every `session.json` ever written records `AppVersion` `"1.0.0"`. This plan ships a real version plus a git SHA, an on-disk append-only diagnostic log, a record-and-notify dispatcher handler, log call sites for the things that actually go wrong, and a Settings surface that shows the build and hands the user the last error.

**Architecture:** One Core seam, `IDiagnosticLog`, with a fire-and-forget `Write` (never throws, and never blocks on IO — the enqueue takes an uncontended lock and returns) feeding a queue that a single chained background drain appends to a monthly JSONL file — the `McpAuditLog` shape, which is this repo's only append-only log, carrying SHARED-CONTRACT's 2026-08-05 amendment that the drain is a **single-writer chain** rather than `McpAuditLog`'s `SemaphoreSlim` (a `void` fire-and-forget `Write` cannot await a gate, and `FlushAsync` needs a handle to await). Everything WPF-coupled is a one-line lambda into a WPF-free extracted class (`UnhandledExceptionRecorder`, `SessionDiagnosticsRecorder`), because `App.xaml.cs` and `TrayIconHost.cs` have no test coverage at all. The two `IUiErrorReporter` implementations gain an optional log sink rather than a decorator, because `InfoBarErrorReporter` is consumed concretely by `MainWindowViewModel`.

**Tech Stack:** C# / .NET 10, WPF (+ Wpf.Ui), CommunityToolkit.Mvvm, MSBuild, System.Text.Json, xUnit.

**This plan ships first and alone.** Plans B, C and D all write into the seam it defines.

## Global Constraints

- **Build/test:** `dotnet build` / `dotnet test` against `F:\LocalScribe\LocalScribe.slnx`. A running
  `LocalScribe.App.exe` locks `Core.dll` → `MSB3027`. Close it; **never blanket-kill processes** —
  target the specific PID.
- **Test baseline (measured 2026-08-05, `--filter "Category!=Fixture"`):** Core **1186/1186**, App
  **984/984**, Mcp **6/6** = **2176**, zero failures, zero skips. **Judge regressions by failing test
  NAME, never by count.** Fixture-gated tests (`Category=Fixture`) need model weights and private
  corpora and are excluded.
- **ASCII source files.** Non-ASCII in string literals MUST be `\u` escapes; Fluent glyphs follow
  `TrayIconHost.cs:188-191`. The Edit tool silently converts escapes to literal glyphs — byte-scan
  every touched file before committing (zero bytes > 127, CRLF intact).
- **Stage files by name.** Never `git add -A` / `git add .` / `git commit -a`, never `git clean` —
  `tools/diar-eval/`, `.ai-code-review/` and `.claude/` are deliberately untracked.
- **Comment idiom:** name the design doc/date, state the REJECTED alternative and why, CAPITALISE the
  load-bearing word, use ` - ` not an em-dash. Roughly a third of `App.xaml.cs` is comment; match it.
- **Service shape:** `public sealed class X(deps) : IY` primary constructors; delegates (`Func`/
  `Action`) rather than concrete services wherever a test needs to gate them; `TimeProvider` always
  injected, never `DateTime.Now`.
- **Additive settings need no schema bump.** Add the property with a default and the sentence
  "Additive - existing v3 files without it load at this default (the SectionGapMs precedent), so no
  schema bump/migration is required." `SchemaVersion` has stayed 3 across six additive rounds.
- **Tests:** xunit `[Fact]`, `public sealed class XTests : IDisposable`, GUID temp root named
  `ls-<slug>-<guid>` under `Path.GetTempPath()`, swallow-everything `Dispose`. App.Tests writes
  `using Xunit;` explicitly; Core.Tests has `<Using Include="Xunit" />` and must not.
- **Transcripts are legal evidence.** No path may drop, reorder or silently rewrite content.
- **Spec:** `docs/superpowers/specs/2026-08-05-tier1-hardening-design.md`.
- **Shared contract:** every "SHARED-CONTRACT section N" reference in this plan means
  `docs/superpowers/specs/2026-08-05-tier1-shared-contract.md`. It is FIXED - Plans B, C and D read
  it as their Consumes block, so a change here breaks them. **This plan CREATES everything in it.**

Additional constraints for THIS plan:

- **Branch:** `feat/tier1a-tier-a-diagnosability-2026-08-05`. Create it from `master` before Task 1.
- **The diagnostic log is DERIVED data, never evidence.** It lives in its own `diagnostics\` folder
  under the storage root, is safe to delete wholesale, and **must never contain transcript text**
  unless the user explicitly turns `Settings.Logging.IncludeTranscriptText` on. A log under the
  storage root that captured transcript content would be an undeclared, unmanaged copy of privileged
  evidence sitting outside every retention and purge path.
- **`diagnostics\`, not `logs\`.** `.gitignore` already contains `[Ll]og/`, `[Ll]ogs/` and `*.log`, so
  a stray `logs\` folder created during a test run silently vanishes from `git status`.
- **Zero IO in any constructor added by this plan.** `CompositionRootTests.cs:16` calls the REAL
  `CompositionRoot.Build()`, so ctor-time IO would create folders in the developer's actual
  `%USERPROFILE%\LocalScribe` on every test run. `Directory.CreateDirectory` happens inside the drain,
  exactly as `McpAuditLog.AppendAsync` does.
- **The .NET 10 SDK already stamps a SHA and you must suppress it.** MEASURED 2026-08-05: with SDK
  10.0.302, a bare `<Version>0.9.0</Version>` produced
  `AssemblyInformationalVersionAttribute("0.9.0+4ddb7d47ab606d0be0fa2c6b644c9f4aaab77bf5")` with no
  custom target at all - the SDK's built-in source-link sets `SourceRevisionId`. Adding the plan's
  own `+g<short sha>` on top produced the double suffix `0.9.0+g4ddb7d4.4ddb7d47ab...`. The props
  file therefore sets `IncludeSourceRevisionInInformationalVersion=false`. Do not remove that line.
- **Test-run paths.** Filtered runs append
  `-p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\`. A FULL
  App-suite run must NOT use it: `XamlHygieneTests`/`RepoPaths.SolutionRoot()` walks up from
  `AppContext.BaseDirectory` looking for `.git`, and the Temp path sits outside the repo (5 false
  failures).
- **`App.xaml.cs` and `TrayIconHost.cs` have ZERO test coverage** (105 test files, no `AppTests.cs`,
  no `TrayIconHostTests.cs`). Every policy this plan adds is extracted into a WPF-free class and
  unit-tested; the remaining one-line wiring is pinned by source-text assertions in
  `DiagnosticsWiringTests`, the same way `XamlHygieneTests.AppIcon_ExistsAndIsWiredInCsproj` asserts
  on raw csproj text.

---

## File Structure

**Created:**
- `src/Directory.Build.props` — scoped to `src/` so it reaches the eight shipping projects and leaves `tests/` and `tools/` untouched. Sets `<Version>` and stamps the git SHA into `InformationalVersion`.
- `src/LocalScribe.Core/Diagnostics/DiagnosticLog.cs` — `DiagnosticEntry`, `IDiagnosticLog` and the queue + single-background-drain implementation. The one file Plans B/C/D write into.
- `src/LocalScribe.Core/Diagnostics/DiagnosticLevels.cs` — the four level names and their ranking against `Settings.Logging.Level`.
- `src/LocalScribe.Core/Diagnostics/DiagnosticRedaction.cs` — the `<<...>>` privileged-content markers, the redactor that honours `Settings.Logging.IncludeTranscriptText`, and the exception formatter every call site uses.
- `src/LocalScribe.Core/Audio/IDiagnosticSource.cs` — "a capture source that can explain what it did"; implemented by `ProcessLoopbackCapture`, whose `Diagnostic` event has existed since the Stage-1 spike with exactly one subscriber outside the app.
- `src/LocalScribe.Core/Audio/CaptureDiagnostics.cs` — attaches a sink to a source that has one; no-ops for sources that do not.
- `src/LocalScribe.App/Services/UnhandledExceptionRecorder.cs` — the WPF-free record-and-notify policy behind the dispatcher handler.
- `src/LocalScribe.App/Services/SessionDiagnosticsRecorder.cs` — turns `SessionController`'s existing events into diagnostic lines (session start/stop/finalize, transcription downgrades, capture fallbacks).
- `tests/LocalScribe.App.Tests/BuildVersionTests.cs`
- `tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs`
- `tests/LocalScribe.App.Tests/UnhandledExceptionRecorderTests.cs`
- `tests/LocalScribe.App.Tests/SessionDiagnosticsRecorderTests.cs`
- `tests/LocalScribe.App.Tests/TrayNoticeReporterTests.cs`
- `tests/LocalScribe.Core.Tests/DiagnosticLogTests.cs`
- `tests/LocalScribe.Core.Tests/DiagnosticRedactionTests.cs`
- `tests/LocalScribe.Core.Tests/CaptureDiagnosticsTests.cs`

**Modified:**
- `src/LocalScribe.Core/Storage/StoragePaths.cs:84-89` — gains `DiagnosticsDir` beside `McpAuditDir`, pure getter, no IO.
- `src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs:37,79-81` — declares `IDiagnosticSource`; the event itself is unchanged.
- `src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs:12-30,47-65` — optional diagnostic sink, attached to both remote-capture paths.
- `src/LocalScribe.Core/Diarisation/SherpaHelperDiariser.cs:1,5,47,81` — optional log; records helper exit codes. (MEASURED against HEAD: line 5 is the class declaration, line 47 is `int exit = await helper.RunAsync(...)` and line 81 is `int exit = await helper.RunEmbedAsync(...)`. Lines 49-53 and 82-86 are the two `throw new DiarisationException` guards — a log line placed there would run on the FAILURE path only.)
- `src/LocalScribe.App/CompositionRoot.cs:21-41,66-67,85-89,138,175-178` — `BuildInfo` and `Log` members, the `DiagnosticLog` construction, and the two sinks wired at their construction sites.
- `src/LocalScribe.App/App.xaml.cs:13-39,41-58,88-91,174-183,252-284,812-827,1054-1064,1132-1144` — the private-field region (Tasks 5 and 6 both add a field there), recorder field + one-line handler, session recorder subscriptions, reporter sinks, settings wiring, the startup-orchestrator construction (the `notify` lambda at `:1058` is deliberately UNCHANGED), exit flush.
- `src/LocalScribe.App/Services/StartupOrchestrator.cs:3-10,16,19-21,30-31` — the recovered-count summary moves from the raw `notify` sink onto `IUiErrorReporter.Info`, so it reaches the log exactly once; the now-unread `notify` seam is removed.
- `src/LocalScribe.App/TrayIconHost.cs:20-49,78-104` — optional log; awaits `FlushAsync` before `Shutdown()`.
- `src/LocalScribe.App/Services/InfoBarErrorReporter.cs:10-17` — optional log sink parameter, defaulted null.
- `src/LocalScribe.App/Services/TrayNoticeReporter.cs:6-9` — the same.
- `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs:1-15,190-257,259-263` — build stamp, diagnostics folder command, copy-last-error command.
- `src/LocalScribe.App/SettingsPage.xaml:410-421` — version line and the two buttons in the "App" card.
- `tests/LocalScribe.App.Tests/AppServiceFakes.cs` — gains the shared `FakeDiagnosticLog`.
- `tests/LocalScribe.App.Tests/CompositionRootTests.cs:13-25` — asserts the two version strings and the log.
- `tests/LocalScribe.App.Tests/InfoBarErrorReporterTests.cs` — log-sink facts, including the real-`DiagnosticLog` proof that a participant name in an `Info` message never reaches disk at the default setting.
- `tests/LocalScribe.App.Tests/StartupOrchestratorTests.cs` — five construction sites lose the `notify` argument; the summary assertions move onto the reporter fake; one new fact pins one log line per recovery failure.
- `tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs:30-54` — the `openFolder` fake becomes CAPTURING, plus five new facts (matching Task 11 Step 1 and Task 12 Step 1).
- `tests/LocalScribe.Core.Tests/StoragePathsTests.cs` — `DiagnosticsDir` fact.
- `tests/LocalScribe.Core.Tests/SherpaHelperDiariserTests.cs` — helper-exit-code facts.

---

## Task 1: `src/Directory.Build.props` — a real version and the git SHA

**Files:**
- Create: `src/Directory.Build.props`
- Test: `tests/LocalScribe.App.Tests/BuildVersionTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: MSBuild `$(Version)` = `0.9.0` for the eight projects under `src/`, hence
  `Assembly.GetName().Version?.ToString(3)` == `"0.9.0"`; and
  `AssemblyInformationalVersionAttribute` == `"0.9.0+g<7 hex chars>"` in a git checkout, `"0.9.0"` in
  a source drop with no `.git`. Task 5 turns the second into `AppComposition.BuildInfo`.

There is **no** `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
`global.json` or `NuGet.config` anywhere in this repo today, and no csproj sets any version property.
The file goes in `src/`, **not** the repo root: MSBuild walks up from each project directory and stops
at the first match, so a root-level file would silently apply to all 13 csproj files including
`tools/generate-icon` and `tools/UiaProbe`. `tests/` keeps the SDK default version, which is correct -
test assemblies are not shipped.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/BuildVersionTests.cs`:

```csharp
using System.IO;
using System.Reflection;
using LocalScribe.App;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins the version stamp (Tier 1 plan A, 2026-08-05, spec item T1-1). TWO strings and
/// both matter: the NUMERIC assembly version is what CompositionRoot.cs:67 turns into
/// SessionRecord.AppVersion in every session.json - append-only evidentiary data that read
/// "1.0.0" (the SDK default) on every session ever recorded before this round - while the
/// InformationalVersion carries the git SHA for support. Reading src/Directory.Build.props as TEXT
/// follows XamlHygieneTests.AppIcon_ExistsAndIsWiredInCsproj, which asserts on raw csproj text the
/// same way; there is no other way to pin an MSBuild property from a test.</summary>
public sealed class BuildVersionTests
{
    private static string PropsPath()
        => Path.Combine(RepoPaths.SolutionRoot(), "src", "Directory.Build.props");

    [Fact]
    public void Src_props_sets_the_version_and_suppresses_the_sdk_source_revision()
    {
        Assert.True(File.Exists(PropsPath()), "missing " + PropsPath());
        string props = File.ReadAllText(PropsPath());
        Assert.Contains("<Version>0.9.0</Version>", props);
        // MEASURED 2026-08-05 on SDK 10.0.302: the SDK's built-in source-link already appends
        // "+<40-char sha>" to InformationalVersion with NO custom target, so without this
        // suppression the stamp came out as "0.9.0+g4ddb7d4.4ddb7d47ab606d0..." - two SHAs, one
        // of them full length. The plan's own short-sha stamp is the one we keep.
        Assert.Contains(
            "<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>",
            props);
    }

    [Fact]
    public void The_props_file_is_scoped_to_src_and_not_the_repo_root()
    {
        // A repo-root Directory.Build.props would silently apply to all 13 csproj files, including
        // tools/generate-icon and tools/UiaProbe. MSBuild stops at the FIRST match walking up, so
        // keeping the only copy under src/ is what scopes it to the eight shipping projects.
        Assert.False(File.Exists(Path.Combine(RepoPaths.SolutionRoot(), "Directory.Build.props")));
        Assert.False(File.Exists(Path.Combine(RepoPaths.SolutionRoot(), "Directory.Build.targets")));
    }

    [Fact]
    public void The_app_assembly_reports_the_real_numeric_version()
        => Assert.Equal("0.9.0", typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3));

    [Fact]
    public void The_core_assembly_is_stamped_from_the_same_props_file()
        => Assert.Equal("0.9.0",
            typeof(LocalScribe.Core.Storage.StoragePaths).Assembly.GetName().Version?.ToString(3));

    [Fact]
    public void The_informational_version_carries_an_optional_short_git_sha()
    {
        string? info = typeof(CompositionRoot).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(info));
        // TWO legal shapes and the test must accept both: a git checkout stamps "0.9.0+g1628935";
        // a source drop with no .git falls back to a bare "0.9.0" (MEASURED both ways 2026-08-05).
        Assert.Matches(@"^0\.9\.0(\+g[0-9a-f]{7})?$", info!);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~BuildVersionTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: **5 tests, 4 failing.** `Src_props_sets_the_version_and_suppresses_the_sdk_source_revision`
fails with `missing F:\LocalScribe\src\Directory.Build.props`; the two numeric-version facts
(`The_app_assembly_reports_the_real_numeric_version`,
`The_core_assembly_is_stamped_from_the_same_props_file`) fail with
`Assert.Equal() Failure: Expected: 0.9.0, Actual: 1.0.0`;
`The_informational_version_carries_an_optional_short_git_sha` fails because the SDK default `1.0.0`
does not match the `^0\.9\.0(\+g[0-9a-f]{7})?$` pattern. Only
`The_props_file_is_scoped_to_src_and_not_the_repo_root` passes.

- [ ] **Step 3: Create the props file**

Create `src/Directory.Build.props` **exactly** as follows. This repo has ZERO precedent for shelling
out during a build, so every guard below is load-bearing and was measured, not guessed:

```xml
<Project>

  <!-- Version stamp (Tier 1 plan A, 2026-08-05, spec item T1-1). Scoped to src\ ON PURPOSE:
       MSBuild walks UP from each project and stops at the first Directory.Build.props, so this
       file reaches the eight shipping projects under src\ and never touches tests\ or tools\
       (tools\generate-icon and tools\UiaProbe are build-time utilities, not products). There is
       no parent props file in this repo, so nothing is imported here.

       0.9.0 is deliberately pre-1.0: the product ships behind an installer only after Tier 1D.
       The NUMERIC version is what Assembly.GetName().Version yields and therefore what lands in
       every session.json AppVersion field - evidentiary, append-only data - so it stays short. -->
  <PropertyGroup>
    <Version>0.9.0</Version>

    <!-- MEASURED 2026-08-05 (SDK 10.0.302): the SDK's built-in source-link sets SourceRevisionId
         from git and appends "+<40-char sha>" to InformationalVersion all by itself. Left on, it
         stacks with the short sha stamped below and yields "0.9.0+g4ddb7d4.4ddb7d47ab606d0...".
         REJECTED: dropping our own target and living with the SDK's full sha - 40 hex characters
         in a Settings "About" line and in every support paste-in is unreadable, and the value
         cannot be shortened once the SDK has appended it. -->
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
  </PropertyGroup>

  <!-- Stamps "<Version>+g<short sha>" into AssemblyInformationalVersionAttribute. Runs before the
       two SDK targets that consume the property. Every attribute below is a guard:
         Condition (on the target)  - projects that suppress assembly-info generation are skipped.
         Condition (on the Exec)    - a SOURCE DROP with no .git never even invokes git. Exists()
                                      is true for a directory AND for a file, which is what a
                                      linked git worktree has at its root - this repo is worked in
                                      worktrees, so the file form must keep working.
         ConsoleToMSBuild           - captures stdout into a property (there is no other way).
         ContinueOnError            - git missing from PATH must not fail the build.
         IgnoreExitCode             - a non-zero git exit (or cmd's 9009 for "not found") must not
                                      even raise a WARNING; the ExitCode output below is checked
                                      explicitly instead.
       MEASURED both ways 2026-08-05: with .git present the build stamps "0.9.0+g4ddb7d4"; with
       .git removed, and again with a nonexistent git executable, it stamps a bare "0.9.0" with
       0 warnings and 0 errors. -->
  <Target Name="StampGitShaIntoInformationalVersion"
          BeforeTargets="GetAssemblyVersion;GenerateAssemblyInfo"
          Condition="'$(GenerateAssemblyInfo)' != 'false'">
    <Exec Command="git -C &quot;$(MSBuildThisFileDirectory).&quot; rev-parse --short=7 HEAD"
          Condition="Exists('$(MSBuildThisFileDirectory)..\.git')"
          ConsoleToMSBuild="true"
          StandardOutputImportance="low"
          StandardErrorImportance="low"
          ContinueOnError="true"
          IgnoreExitCode="true">
      <Output TaskParameter="ConsoleOutput" PropertyName="_LsGitShaOutput" />
      <Output TaskParameter="ExitCode" PropertyName="_LsGitShaExit" />
    </Exec>
    <PropertyGroup>
      <InformationalVersion Condition="'$(_LsGitShaExit)' == '0' and '$(_LsGitShaOutput)' != ''">$(Version)+g$(_LsGitShaOutput.Trim())</InformationalVersion>
      <InformationalVersion Condition="'$(InformationalVersion)' == ''">$(Version)</InformationalVersion>
    </PropertyGroup>
  </Target>

</Project>
```

Note the `"$(MSBuildThisFileDirectory)."` form in the command: `MSBuildThisFileDirectory` ends with a
backslash, and `"F:\LocalScribe\src\"` would escape the closing quote under `cmd`. The trailing `.`
makes it `"F:\LocalScribe\src\."`, which is a valid directory and quote-safe.

- [ ] **Step 4: Run the test and confirm it passes**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~BuildVersionTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: **Passed! - Failed: 0, Passed: 5**. If the build reports MSB3027, `LocalScribe.App.exe` is
running and holding `Core.dll` — close that one process (never blanket-kill) and re-run.

To see the stamp with your own eyes:

```powershell
cd F:\LocalScribe
Select-String -Path "src\LocalScribe.App\obj\Debug\net10.0-windows\LocalScribe.App.AssemblyInfo.cs" -Pattern "InformationalVersion"
```

- [ ] **Step 5: Commit**

```bash
cd F:/LocalScribe
git add src/Directory.Build.props tests/LocalScribe.App.Tests/BuildVersionTests.cs
git commit -m "build: real 0.9.0 version plus git-sha InformationalVersion for src projects"
```

---

## Task 2: `StoragePaths.DiagnosticsDir`

**Files:**
- Modify: `src/LocalScribe.Core/Storage/StoragePaths.cs:84-89` (add after `McpAuditDir`)
- Test: `tests/LocalScribe.Core.Tests/StoragePathsTests.cs` (add one fact)

**Interfaces:**
- Consumes: nothing.
- Produces: `StoragePaths.DiagnosticsDir` — `string`, pure getter, `Path.Combine(Root, "diagnostics")`,
  no IO. Task 4 (`DiagnosticLog`), Task 11 (`OpenDiagnosticsFolderCommand`) and Plans B/C/D use it.

- [ ] **Step 1: Write the failing test**

Add this fact to `tests/LocalScribe.Core.Tests/StoragePathsTests.cs` (the class is in the GLOBAL
namespace and the csproj supplies `using Xunit`, so add no usings):

```csharp
    [Fact]
    public void Diagnostics_live_in_their_own_derived_folder_beside_sessions_and_matters()
    {
        var p = new StoragePaths(@"C:\Data\LocalScribe");
        Assert.Equal(@"C:\Data\LocalScribe\diagnostics", p.DiagnosticsDir);
        // Deliberately NOT "logs" (Tier 1 plan A, 2026-08-05): .gitignore already swallows
        // [Ll]og/, [Ll]ogs/ and *.log, so a logs\ folder created during a test run would vanish
        // from git status and a stray artefact could never be noticed.
        Assert.DoesNotContain("log", p.DiagnosticsDir, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 2: Run it and confirm it fails**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Diagnostics_live_in_their_own_derived_folder" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: FAIL to build — `error CS1061: 'StoragePaths' does not contain a definition for 'DiagnosticsDir'`.

- [ ] **Step 3: Add the getter**

In `src/LocalScribe.Core/Storage/StoragePaths.cs`, immediately after the `McpAuditDir` line
(`public string McpAuditDir => Path.Combine(McpDir, "audit");`), add:

```csharp
    /// <summary>Diagnostic log (Tier 1 plan A, 2026-08-05): DERIVED, safe to delete wholesale -
    /// never evidence (same standing as search-index.json). One JSONL file per calendar month.
    /// Deliberately named diagnostics\ rather than logs\ because .gitignore already swallows
    /// [Ll]ogs/ and *.log, which would hide a stray test artefact from git status.</summary>
    public string DiagnosticsDir => Path.Combine(Root, "diagnostics");
```

- [ ] **Step 4: Run the test and confirm it passes**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~StoragePathsTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: all `StoragePathsTests` pass, including the new one.

- [ ] **Step 5: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Storage/StoragePaths.cs tests/LocalScribe.Core.Tests/StoragePathsTests.cs
git commit -m "feat(diagnostics): StoragePaths.DiagnosticsDir (derived, never evidence)"
```

---

## Task 3: `DiagnosticRedaction` and `DiagnosticLevels`

**Files:**
- Create: `src/LocalScribe.Core/Diagnostics/DiagnosticRedaction.cs`,
  `src/LocalScribe.Core/Diagnostics/DiagnosticLevels.cs`
- Test: `tests/LocalScribe.Core.Tests/DiagnosticRedactionTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces, all in namespace `LocalScribe.Core.Diagnostics`:
  - `DiagnosticRedaction.Open` (`const string` = `"<<"`), `.Close` (`">>"`), `.Placeholder` (`"[redacted]"`)
  - `DiagnosticRedaction.Mark(string? value) : string` — wraps a value as privileged content, first
    NEUTRALISING any `<<`/`>>` the value already contains (see Step 4: without that, marked content
    carrying a literal `>>` leaks its tail past the redactor)
  - `DiagnosticRedaction.Apply(string? text, bool includeTranscriptText) : string?` — strips markers when true, replaces marked runs with `[redacted]` when false
  - `DiagnosticRedaction.ForException(Exception ex) : string` — the detail string every exception call site passes
  - `DiagnosticLevels.Error` / `.Warn` / `.Info` / `.Debug` (`const string`, lower case) and `DiagnosticLevels.Rank(string? level) : int` (error 0 … debug 3, unknown 2)

This is the whole redaction contract, and it is the reason `IncludeTranscriptText` can be trusted:
privileged content is **delimited by the caller**, so the switch has something precise to act on. The
exception formatter marks every exception MESSAGE (which can quote arbitrary data) and leaves the
stack trace unmarked (type and method names only) - so stack traces survive at the default settings,
which is what makes the log useful.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/DiagnosticRedactionTests.cs`:

```csharp
using LocalScribe.Core.Diagnostics;

namespace LocalScribe.Core.Tests;

/// <summary>The diagnostic log's redaction contract (Tier 1 plan A, 2026-08-05). A log under the
/// storage root that captured transcript content would be an undeclared copy of privileged
/// evidence sitting outside every retention and purge path, so Settings.Logging.
/// IncludeTranscriptText has to mean something mechanical. It does: callers DELIMIT anything that
/// could be content with Mark(), and Apply() is the only thing that ever unwraps it.</summary>
public sealed class DiagnosticRedactionTests
{
    [Fact]
    public void Unmarked_text_is_untouched_in_both_directions()
    {
        Assert.Equal("gapMs=4200 device=id-headset",
            DiagnosticRedaction.Apply("gapMs=4200 device=id-headset", includeTranscriptText: false));
        Assert.Equal("gapMs=4200 device=id-headset",
            DiagnosticRedaction.Apply("gapMs=4200 device=id-headset", includeTranscriptText: true));
    }

    [Fact]
    public void Marked_runs_are_replaced_when_the_switch_is_off_and_unwrapped_when_it_is_on()
    {
        string text = "seq=7 text=" + DiagnosticRedaction.Mark("I never signed that document");
        Assert.Equal("seq=7 text=[redacted]",
            DiagnosticRedaction.Apply(text, includeTranscriptText: false));
        Assert.Equal("seq=7 text=I never signed that document",
            DiagnosticRedaction.Apply(text, includeTranscriptText: true));
    }

    [Fact]
    public void Several_marked_runs_in_one_line_are_all_handled()
    {
        string text = DiagnosticRedaction.Mark("alpha") + " | " + DiagnosticRedaction.Mark("beta");
        Assert.Equal("[redacted] | [redacted]", DiagnosticRedaction.Apply(text, false));
        Assert.Equal("alpha | beta", DiagnosticRedaction.Apply(text, true));
    }

    [Fact]
    public void An_unterminated_marker_fails_CLOSED_and_redacts_to_the_end()
    {
        // A message that happens to contain "<<" (or a truncated one) must never leak the tail.
        // REJECTED: treating an unterminated marker as literal text - that is the exact shape a
        // truncated exception message takes, which is when leaking matters most.
        Assert.Equal("head [redacted]", DiagnosticRedaction.Apply("head <<runaway content", false));
    }

    [Fact]
    public void Marked_content_containing_the_close_delimiter_does_not_leak_its_tail()
    {
        // The one case where the marker scheme could fail OPEN. Without neutralisation Mark()
        // produced "<<a >> b>>", Apply found the FIRST ">>" at index 4, emitted [redacted] and then
        // appended " b>>" LITERALLY - privileged tail on disk at the default setting. Real inputs
        // that carry ">>": quoted email levels, XML/JSON fragments, C++ template text in an
        // exception message. Mark() neutralises BOTH delimiters before wrapping.
        Assert.Equal("[redacted]", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a >> b"), false));
        Assert.Equal("[redacted]", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a << b"), false));
        // ODD-LENGTH RUNS - the case that broke the FIRST attempt at this fix. A pairwise
        // .Replace(">>", "> >") is non-overlapping and left-to-right, and its replacement string
        // ENDS in ">", so ">>>" became "> >" + ">" = "> >>" - the delimiter re-formed at the join
        // and the tail leaked exactly as before. A THIRD-LEVEL EMAIL QUOTE (">>>") is that input
        // verbatim. Spacing every bracket INDIVIDUALLY cannot re-form a pair at any run length,
        // which is why Mark() does that rather than looping the pairwise replace.
        Assert.Equal("[redacted]", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a >>> b"), false));
        Assert.Equal("[redacted]", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark(">>>> quoted"), false));
        Assert.Equal("[redacted]", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a <<< b"), false));
        // The documented COST, asserted exactly so nobody "tidies" it later: with the switch ON,
        // EVERY angle bracket comes back with a trailing space, so a bracket that was already
        // followed by a space yields TWO. That is not a bug - it is the neutralisation being
        // idempotent by construction. This log is DERIVED diagnostics, never evidence, so altered
        // punctuation in a diagnostic message is acceptable where a leak is not.
        Assert.Equal("a > >  b", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a >> b"), true));
        Assert.Equal("a > > >  b", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a >>> b"), true));
        Assert.Equal("a < <  b", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a << b"), true));
    }

    [Fact]
    public void Null_and_empty_survive_unchanged()
    {
        Assert.Null(DiagnosticRedaction.Apply(null, false));
        Assert.Equal("", DiagnosticRedaction.Apply("", true));
    }

    [Fact]
    public void ForException_marks_every_message_and_leaves_the_stack_readable()
    {
        Exception caught;
        try
        {
            try { throw new InvalidOperationException("witness said I never signed that"); }
            catch (Exception inner) { throw new ApplicationException("save failed", inner); }
        }
        catch (Exception ex) { caught = ex; }

        string detail = DiagnosticRedaction.ForException(caught);
        Assert.Contains("System.ApplicationException", detail);
        Assert.Contains("System.InvalidOperationException", detail);   // inner types are kept
        Assert.Contains("ForException_marks_every_message", detail);    // the stack IS present

        string redacted = DiagnosticRedaction.Apply(detail, includeTranscriptText: false)!;
        Assert.DoesNotContain("never signed", redacted);                // BOTH messages are gone
        Assert.DoesNotContain("save failed", redacted);
        Assert.Contains("System.ApplicationException", redacted);       // ...types and stack stay
        Assert.Contains("ForException_marks_every_message", redacted);
    }

    [Fact]
    public void Levels_rank_from_error_to_debug_and_unknown_reads_as_info()
    {
        Assert.Equal(0, DiagnosticLevels.Rank(DiagnosticLevels.Error));
        Assert.Equal(1, DiagnosticLevels.Rank(DiagnosticLevels.Warn));
        Assert.Equal(2, DiagnosticLevels.Rank(DiagnosticLevels.Info));
        Assert.Equal(3, DiagnosticLevels.Rank(DiagnosticLevels.Debug));
        Assert.Equal(2, DiagnosticLevels.Rank("  INFO "));   // settings.json is hand-editable
        Assert.Equal(2, DiagnosticLevels.Rank("verbose"));   // unknown -> the documented default
        Assert.Equal(2, DiagnosticLevels.Rank(null));
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~DiagnosticRedactionTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: FAIL to build — `error CS0246: The type or namespace name 'DiagnosticRedaction' could not be found`.

- [ ] **Step 3: Create `DiagnosticLevels`**

Create `src/LocalScribe.Core/Diagnostics/DiagnosticLevels.cs`:

```csharp
namespace LocalScribe.Core.Diagnostics;

/// <summary>The four level names Settings.Logging.Level has documented since v1
/// (docs/specs/localscribe-specs.md:871: "error|warn|info|debug") and their ordering. Read by
/// DiagnosticLog.Write, which is the FIRST production code ever to read that setting - the record
/// existed from v1 with zero readers (Tier 1 plan A, 2026-08-05).</summary>
public static class DiagnosticLevels
{
    public const string Error = "error";
    public const string Warn = "warn";
    public const string Info = "info";
    public const string Debug = "debug";

    /// <summary>Lower is more severe. An unrecognised value ranks as info - settings.json is
    /// hand-editable and a typo must degrade to the documented default, never silence the log
    /// (rank 0 would have been fail-quiet) and never flood it (rank 3).</summary>
    public static int Rank(string? level) => (level ?? "").Trim().ToLowerInvariant() switch
    {
        Error => 0,
        Warn => 1,
        Info => 2,
        Debug => 3,
        _ => 2,
    };
}
```

- [ ] **Step 4: Create `DiagnosticRedaction`**

Create `src/LocalScribe.Core/Diagnostics/DiagnosticRedaction.cs`:

```csharp
using System.Text;

namespace LocalScribe.Core.Diagnostics;

/// <summary>Privileged-content markers for the diagnostic log (Tier 1 plan A, 2026-08-05).
/// Settings.Logging.IncludeTranscriptText promises the user that the log does not carry transcript
/// text; that promise can only be MECHANICAL if the potentially-privileged part of a line is
/// delimited, so every call site wraps such a value in Mark(...) and Apply() is the only code that
/// ever unwraps it. REJECTED: dropping the whole Detail field when the switch is off - stack traces
/// live in Detail, and a diagnostic log with no stack traces at its DEFAULT setting is the log we
/// already had (i.e. none). REJECTED: pattern-sniffing for "natural language" - unimplementable,
/// and a guess that silently fails is worse than no guard at all.</summary>
public static class DiagnosticRedaction
{
    public const string Open = "<<";
    public const string Close = ">>";
    public const string Placeholder = "[redacted]";

    /// <summary>Wraps a value the caller believes MAY carry privileged content, NEUTRALISING any
    /// delimiter the value already contains. That neutralisation is the whole reason this is not a
    /// one-line concatenation: REJECTED plain <c>Open + value + Close</c> - Mark("a >> b") produced
    /// "&lt;&lt;a >> b>>", Apply() matched the FIRST close at index 4, emitted [redacted] and then
    /// appended " b>>" literally, putting the privileged TAIL on disk at the default setting. Email
    /// quote levels, XML/JSON fragments and C++ template text in exception messages all carry ">>".
    /// ALSO REJECTED, and the reason this spaces every bracket rather than each PAIR:
    /// <c>.Replace(Close, "> ")</c> is non-overlapping and left-to-right, so ">>>" becomes "> >>" -
    /// which re-creates the delimiter and leaks the tail again. A third-level email quote (">>>")
    /// is exactly that input. Spacing every angle bracket individually is idempotent by
    /// construction: no ">" can be followed by another ">", so no Close can survive at any run
    /// length. The cost is one space per angle bracket when IncludeTranscriptText is ON; this log
    /// is DERIVED diagnostics, never evidence, so that trade is one-way.</summary>
    public static string Mark(string? value) => Open
        + (value ?? "").Replace(">", "> ", StringComparison.Ordinal)
                       .Replace("<", "< ", StringComparison.Ordinal)
        + Close;

    /// <summary>Strips the markers when transcript text is allowed, replaces each marked run with
    /// [redacted] when it is not. An UNTERMINATED marker redacts to the end of the string - fail
    /// CLOSED, because a truncated message is exactly when leaking matters most.</summary>
    public static string? Apply(string? text, bool includeTranscriptText)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!text.Contains(Open, StringComparison.Ordinal)) return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf(Open, i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, open - i);
            int close = text.IndexOf(Close, open + Open.Length, StringComparison.Ordinal);
            int contentStart = open + Open.Length;
            int contentEnd = close < 0 ? text.Length : close;
            if (includeTranscriptText) sb.Append(text, contentStart, contentEnd - contentStart);
            else sb.Append(Placeholder);
            i = close < 0 ? text.Length : close + Close.Length;
        }
        return sb.ToString();
    }

    /// <summary>The Detail string every exception call site passes to IDiagnosticLog.Write: type
    /// names and the stack UNMARKED (they carry no content), every MESSAGE marked (a message can
    /// quote a file path, a transcript line or a user's own words). REJECTED: ex.ToString() - it
    /// embeds inner-exception messages inline with no way to mark them.</summary>
    public static string ForException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var sb = new StringBuilder();
        Exception? e = ex;
        // Depth cap: a hand-built cyclic InnerException chain would otherwise spin forever, and a
        // logger must never be the thing that hangs the app it is diagnosing.
        for (int depth = 0; e is not null && depth < 5; e = e.InnerException, depth++)
        {
            if (depth > 0) sb.Append(" ---> ");
            sb.Append(e.GetType().FullName).Append(": ").Append(Mark(e.Message));
        }
        if (ex.StackTrace is { Length: > 0 } stack) sb.Append(Environment.NewLine).Append(stack);
        return sb.ToString();
    }
}
```

- [ ] **Step 5: Run the tests and confirm they pass**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~DiagnosticRedactionTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: **Passed! - Failed: 0, Passed: 8**.

- [ ] **Step 6: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Diagnostics/DiagnosticRedaction.cs src/LocalScribe.Core/Diagnostics/DiagnosticLevels.cs tests/LocalScribe.Core.Tests/DiagnosticRedactionTests.cs
git commit -m "feat(diagnostics): redaction markers and level ranking"
```

---

## Task 4: `IDiagnosticLog` and `DiagnosticLog`

**Files:**
- Create: `src/LocalScribe.Core/Diagnostics/DiagnosticLog.cs`
- Test: `tests/LocalScribe.Core.Tests/DiagnosticLogTests.cs` (create)

**Interfaces:**
- Consumes: `StoragePaths.DiagnosticsDir` (Task 2); `DiagnosticLevels.Rank`, `DiagnosticRedaction.Apply`/`.Mark` (Task 3); `LocalScribe.Core.Model.LoggingSetting` (existing, `Settings.cs:67`, `{ string Level = "info"; bool IncludeTranscriptText }`).
- Produces, all in namespace `LocalScribe.Core.Diagnostics`:
  - `public sealed record DiagnosticEntry(DateTimeOffset TsUtc, string Level, string Source, string Message, string? Detail)`
  - `public interface IDiagnosticLog { void Write(string level, string source, string message, string? detail = null); Task FlushAsync(CancellationToken ct); }`
  - `public sealed class DiagnosticLog(StoragePaths paths, TimeProvider time, Func<LoggingSetting> settings) : IDiagnosticLog` with the extra concrete member `public DiagnosticEntry? LastError { get; }`

**This interface is FIXED.** Plans B, C and D call exactly
`_log.Write("warn", "capture", "Local leg stalled - no frames", $"gapMs={gap} device={id}");`
Do not add parameters, do not make `Write` async, do not make it throw.

**The drain is a single-writer chain, and that is the contract, not a deviation from it.**
`SHARED-CONTRACT.md` section 1's fixed-decisions table originally mandated `McpAuditLog`'s
`SemaphoreSlim` by analogy; it was **AMENDED 2026-08-05** to a single-writer chained drain, because
`McpAuditLog.AppendAsync` is `async` and can await a gate whereas `IDiagnosticLog.Write` is `void`
fire-and-forget and structurally cannot, and because `FlushAsync` needs a handle to await, which a
semaphore does not give it. Everything else in that table is unchanged and still binding:
`FileMode.Append`, `FileShare.ReadWrite | FileShare.Delete`, one JSON line per entry, calendar-month
rotation, zero IO in the constructor, `Func<LoggingSetting>` rather than a captured value, injected
`TimeProvider`. B/C/D consume only `Write`/`FlushAsync`, so the amendment changes nothing for them.

`Settings` is read through the injected `Func<LoggingSetting>` and never captured: `SettingsService`
swaps the whole settings reference on save, so a captured value would pin the level at startup.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/DiagnosticLogTests.cs`:

```csharp
using System.Text.Json.Nodes;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

/// <summary>The on-disk diagnostic log (Tier 1 plan A, 2026-08-05, spec item T1-1). Modelled on
/// McpAuditLog - the repo's only append-only log - down to FileMode.Append, FileShare.ReadWrite |
/// FileShare.Delete, one JSON line per entry and CALENDAR-MONTH rotation. Size-based rolling was
/// REJECTED: it would make this the first DELETING writer in a codebase whose core rule is
/// append-only, and the log is small.</summary>
public sealed class DiagnosticLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 9, 30, 0, TimeSpan.Zero);

    // NOT created by the ctor: the no-IO-in-the-constructor test asserts this path does not exist.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-diaglog-" + Guid.NewGuid().ToString("N"));
    private LoggingSetting _logging = new();

    private StoragePaths Paths => new(_root);
    private DiagnosticLog MakeLog(ManualUtcTimeProvider time) => new(Paths, time, () => _logging);
    private string File202608 => Path.Combine(Paths.DiagnosticsDir, "diag-202608.jsonl");

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Construction_touches_no_disk()
    {
        _ = MakeLog(new ManualUtcTimeProvider(T0));
        // CompositionRootTests.cs:16 calls the REAL CompositionRoot.Build(), which builds one of
        // these - ctor-time IO would create folders in the developer's actual
        // %USERPROFILE%\LocalScribe on every test run. Directory.CreateDirectory lives in the
        // drain, exactly as McpAuditLog.AppendAsync does.
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Flushing_an_empty_log_writes_nothing_and_never_throws()
    {
        await MakeLog(new ManualUtcTimeProvider(T0)).FlushAsync(default);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Writes_one_camel_case_json_line_per_entry_into_the_monthly_file()
    {
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        log.Write("warn", "capture", "Local leg stalled - no frames", "gapMs=4200");
        log.Write("info", "session", "State Recording");
        await log.FlushAsync(default);

        var lines = await File.ReadAllLinesAsync(File202608);
        Assert.Equal(2, lines.Length);                       // one line per entry, order preserved
        var first = JsonNode.Parse(lines[0])!.AsObject();
        Assert.Equal("2026-08-05T09:30:00+00:00", first["tsUtc"]!.GetValue<string>());
        Assert.Equal("warn", first["level"]!.GetValue<string>());
        Assert.Equal("capture", first["source"]!.GetValue<string>());
        Assert.Equal("Local leg stalled - no frames", first["message"]!.GetValue<string>());
        Assert.Equal("gapMs=4200", first["detail"]!.GetValue<string>());
        // A null detail is omitted entirely rather than written as null (LocalScribeJson's
        // WhenWritingNull convention), so a support file stays readable.
        Assert.Null(JsonNode.Parse(lines[1])!.AsObject()["detail"]);
    }

    [Fact]
    public async Task Files_rotate_on_the_entry_calendar_month_not_the_drain_time()
    {
        var time = new ManualUtcTimeProvider(new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero));
        var log = MakeLog(time);
        log.Write("info", "session", "august line");
        time.Set(new DateTimeOffset(2026, 9, 1, 0, 0, 30, TimeSpan.Zero));
        log.Write("info", "session", "september line");
        await log.FlushAsync(default);          // ONE drain spanning two months

        Assert.Contains("august line",
            await File.ReadAllTextAsync(Path.Combine(Paths.DiagnosticsDir, "diag-202608.jsonl")));
        Assert.Contains("september line",
            await File.ReadAllTextAsync(Path.Combine(Paths.DiagnosticsDir, "diag-202609.jsonl")));
    }

    [Fact]
    public async Task The_level_gate_is_re_read_from_the_settings_func_on_every_write()
    {
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        _logging = new LoggingSetting { Level = "error" };
        log.Write("info", "session", "quiet");
        log.Write("error", "session", "loud");
        await log.FlushAsync(default);
        Assert.Contains("loud", Assert.Single(await File.ReadAllLinesAsync(File202608)));

        // SettingsService SWAPS the settings reference on save, so a value captured at
        // construction would pin the level at startup - the func must be re-invoked per Write.
        _logging = new LoggingSetting { Level = "debug" };
        log.Write("debug", "session", "now audible");
        await log.FlushAsync(default);
        Assert.Equal(2, (await File.ReadAllLinesAsync(File202608)).Length);
    }

    [Fact]
    public async Task Transcript_bearing_text_is_redacted_at_every_level_when_the_switch_is_off()
    {
        _logging = new LoggingSetting { Level = "debug" };      // nothing gated out by level
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        string privileged = DiagnosticRedaction.Mark("the witness never signed that document");
        foreach (string level in new[] { "error", "warn", "info", "debug" })
            log.Write(level, "session", "Segment rejected", "seq=7 text=" + privileged);
        await log.FlushAsync(default);

        string text = await File.ReadAllTextAsync(File202608);
        Assert.Equal(4, (await File.ReadAllLinesAsync(File202608)).Length);
        Assert.DoesNotContain("never signed", text);           // the promise Logging made in v1
        Assert.Contains("seq=7", text);                        // the diagnostic value survives
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public async Task Turning_IncludeTranscriptText_on_keeps_the_content_and_strips_the_markers()
    {
        _logging = new LoggingSetting { IncludeTranscriptText = true };
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        log.Write("info", "session", "Segment rejected",
            "seq=7 text=" + DiagnosticRedaction.Mark("hello there"));
        await log.FlushAsync(default);

        string text = await File.ReadAllTextAsync(File202608);
        Assert.Contains("seq=7 text=hello there", text);
        Assert.DoesNotContain("<<", text);
        Assert.DoesNotContain("[redacted]", text);
    }

    [Fact]
    public async Task Appending_tolerates_a_concurrent_reader()
    {
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        log.Write("info", "session", "one");
        await log.FlushAsync(default);
        // FileShare.ReadWrite | FileShare.Delete, McpAuditLog's flags: a user reading the file (or
        // Explorer previewing it) must never block the writer.
        using var reader = new FileStream(File202608, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        log.Write("info", "session", "two");
        await log.FlushAsync(default);
        Assert.Equal(2, (await File.ReadAllLinesAsync(File202608)).Length);
    }

    [Fact]
    public async Task LastError_holds_only_the_most_recent_error_and_is_already_redacted()
    {
        var log = MakeLog(new ManualUtcTimeProvider(T0));
        Assert.Null(log.LastError);
        log.Write("warn", "capture", "just a warning");
        Assert.Null(log.LastError);                            // warnings are not errors
        log.Write("error", "export", "first failure", DiagnosticRedaction.Mark("privileged"));
        log.Write("error", "export", "second failure");
        await log.FlushAsync(default);

        var last = log.LastError!;
        Assert.Equal("second failure", last.Message);
        Assert.Equal("export", last.Source);
        // The stored entry is the REDACTED one, so Settings' "Copy last error" cannot put
        // privileged text on the clipboard by going round the log file.
        log.Write("error", "export", "third", DiagnosticRedaction.Mark("privileged"));
        Assert.Equal("[redacted]", log.LastError!.Detail);
    }

    [Fact]
    public void Write_never_throws_even_when_the_settings_func_does()
    {
        var log = new DiagnosticLog(Paths, new ManualUtcTimeProvider(T0),
            () => throw new InvalidOperationException("settings gone"));
        log.Write("error", "session", "still fine");           // must not propagate
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~DiagnosticLogTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: FAIL to build — `error CS0246: The type or namespace name 'DiagnosticLog' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/LocalScribe.Core/Diagnostics/DiagnosticLog.cs`:

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Diagnostics;

/// <summary>One diagnostic line. DERIVED data, never evidence - see StoragePaths.DiagnosticsDir.
/// Message and Detail are redacted per Settings.Logging.IncludeTranscriptText before they reach
/// disk; a caller may pass transcript-bearing text (wrapped in DiagnosticRedaction.Mark) and MUST
/// be able to trust that switch.</summary>
public sealed record DiagnosticEntry(DateTimeOffset TsUtc, string Level, string Source,
    string Message, string? Detail);

/// <summary>Fire-and-forget diagnostic sink. Write() NEVER throws and never blocks on IO - the
/// enqueue takes an uncontended lock and returns. It is called from a DispatcherUnhandledException
/// handler, from capture frame loops and from finally blocks, none of which can tolerate an await
/// or a fault. Entries are queued and drained by a single chained background writer; FlushAsync
/// drains on the exit path.</summary>
public interface IDiagnosticLog
{
    /// <param name="level">"error" | "warn" | "info" | "debug" - compared against
    /// Settings.Logging.Level, which is finally read by production code.</param>
    /// <param name="source">Stable short subsystem tag, e.g. "capture", "session", "export".</param>
    void Write(string level, string source, string message, string? detail = null);

    /// <summary>Drains the queue. Awaited by App.OnExit and by the tray Exit path. Never throws.</summary>
    Task FlushAsync(CancellationToken ct);
}

/// <summary>camelCase, one line, nulls omitted - the storage-layer convention (LocalScribeJson).
/// McpAuditLog's snake_case is MCP WIRE style and deliberately not followed here: this file is
/// read by whoever is supporting the user, beside camelCase session.json and meta.json.</summary>
internal static class DiagnosticJson
{
    internal static readonly JsonSerializerOptions Line = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>Append-only diagnostic log (Tier 1 plan A, 2026-08-05, spec item T1-1): one JSONL file
/// per calendar month under diagnostics\, no pruning (the McpAuditLog keep-everything posture) -
/// the whole folder is DERIVED and safe to delete wholesale, so nothing needs to prune it.
///
/// Never contains transcript text unless Settings.Logging.IncludeTranscriptText is on; see
/// DiagnosticRedaction. Bypasses AtomicFile deliberately: AtomicFile rewrites WHOLE files (tmp +
/// move) and has no append, so routing a log through it would rewrite the month's file on every
/// line. McpAuditLog made the same call.
///
/// Writes are queued and drained by ONE chained background task - the single-writer form
/// SHARED-CONTRACT section 1 was AMENDED to on 2026-08-05. REJECTED: McpAuditLog's SemaphoreSlim
/// gate, which that table originally mandated by analogy - McpAuditLog.AppendAsync is async and can
/// await a gate, whereas this Write() is VOID fire-and-forget and structurally cannot, and
/// FlushAsync needs a handle to await, which a semaphore does not give it. The chain is the
/// single-writer guarantee. The lock below is taken only to swap the chain head, never held across
/// IO, so Write() still returns without waiting on the disk.
/// </summary>
public sealed class DiagnosticLog(StoragePaths paths, TimeProvider time, Func<LoggingSetting> settings)
    : IDiagnosticLog
{
    private readonly ConcurrentQueue<DiagnosticEntry> _queue = new();
    private readonly object _pumpGate = new();
    private Task _pump = Task.CompletedTask;
    private DiagnosticEntry? _lastError;

    /// <summary>The most recent error-level entry this process recorded, ALREADY redacted, or null
    /// when nothing has failed. Public and concrete (not on IDiagnosticLog): Settings' "Copy last
    /// error" is the only consumer and it holds the concrete type through AppComposition.</summary>
    public DiagnosticEntry? LastError => Volatile.Read(ref _lastError);

    public void Write(string level, string source, string message, string? detail = null)
    {
        try
        {
            var cfg = settings() ?? new LoggingSetting();
            if (DiagnosticLevels.Rank(level) > DiagnosticLevels.Rank(cfg.Level)) return;
            bool keep = cfg.IncludeTranscriptText;
            // Redact at WRITE time, not drain time: the switch that was in force when the line was
            // produced is the one that governs it, and it makes the in-memory LastError safe too.
            var entry = new DiagnosticEntry(time.GetUtcNow(), level, source,
                DiagnosticRedaction.Apply(message, keep) ?? "",
                DiagnosticRedaction.Apply(detail, keep));
            if (DiagnosticLevels.Rank(level) == 0) Volatile.Write(ref _lastError, entry);
            _queue.Enqueue(entry);
            Kick();
        }
        catch
        {
            // A diagnostic sink must NEVER be the thing that breaks the app it is diagnosing -
            // this method is called from a DispatcherUnhandledException handler and from finally
            // blocks, where a throw would be fatal or would mask the original failure.
        }
    }

    /// <summary>Drains everything queued before the call. The CancellationToken is accepted for
    /// call-site symmetry and deliberately NOT honoured: abandoning a drain mid-exit is exactly how
    /// the last line before a crash gets lost. App.OnExit bounds the wait instead.</summary>
    public Task FlushAsync(CancellationToken ct) => Kick();

    private Task Kick()
    {
        lock (_pumpGate)
        {
            // Chain onto the previous pump so there is only ever ONE writer touching the file, and
            // so a Flush queued after N writes observes all N of them.
            _pump = _pump.ContinueWith(_ => DrainAsync(), TaskScheduler.Default).Unwrap();
            return _pump;
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            var batch = new List<DiagnosticEntry>();
            while (_queue.TryDequeue(out var entry)) batch.Add(entry);
            if (batch.Count == 0) return;                  // no queue, no folder - see the ctor rule
            Directory.CreateDirectory(paths.DiagnosticsDir);
            // Grouped by the ENTRY's month, not the drain clock: a line written at 23:59:59 on the
            // 31st belongs in that month's file even if the drain lands a second later.
            foreach (var month in batch.GroupBy(
                         e => e.TsUtc.ToString("yyyyMM", CultureInfo.InvariantCulture)))
            {
                var sb = new StringBuilder();
                foreach (var e in month)
                    sb.Append(JsonSerializer.Serialize(e, DiagnosticJson.Line))
                      .Append(Environment.NewLine);
                string file = Path.Combine(paths.DiagnosticsDir, "diag-" + month.Key + ".jsonl");
                await using var s = new FileStream(file, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                await s.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), CancellationToken.None);
            }
        }
        catch
        {
            // Same rule as Write: a full disk, a locked file or a deleted storage root must cost
            // the diagnostic line, never the session.
        }
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~DiagnosticLogTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: **Passed! - Failed: 0, Passed: 10**.

- [ ] **Step 5: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Diagnostics/DiagnosticLog.cs tests/LocalScribe.Core.Tests/DiagnosticLogTests.cs
git commit -m "feat(diagnostics): append-only monthly DiagnosticLog behind IDiagnosticLog"
```

---

## Task 5: `AppComposition.BuildInfo` and `AppComposition.Log`

**Files:**
- Modify: `src/LocalScribe.App/CompositionRoot.cs:1-12` (usings), `:21-41` (the record), `:66-67` (after `paths`), `:175-178` (the one construction site)
- Modify: `src/LocalScribe.App/App.xaml.cs:13-39` (a field), `:88-91` (after `Build()`)
- Test: `tests/LocalScribe.App.Tests/CompositionRootTests.cs:13-25` (extend), `tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs` (create)

**Interfaces:**
- Consumes: `DiagnosticLog` (Task 4), `StoragePaths.DiagnosticsDir` (Task 2), the `InformationalVersion` from Task 1.
- Produces:
  - `AppComposition.BuildInfo : string` — e.g. `"0.9.0+g1628935"`. Diagnostic log header, Settings About line, support copy-paste. **Never enters `session.json`.**
  - `AppComposition.Log : DiagnosticLog` — the process-wide sink, and **the only defined way to reach
    it** (SHARED-CONTRACT section 3a, ADDED 2026-08-05). There is exactly ONE instance: the `log`
    local built in `CompositionRoot.Build()` and passed straight into the record. Inside `Build()`
    that local is in scope and is what Task 9's two wirings use; **everywhere else - `App.OnStartup`,
    `App.OnExit`, and every consumer in Plans B, C and D - it is reached as `comp.Log`.** No plan may
    say "whatever Plan A called its local": a local in `Build()` is not in scope in `OnStartup`, and
    only the record member bridges the two.
    Declared CONCRETE (`DiagnosticLog`, not `IDiagnosticLog`) so Task 11 can read `LastError`, which
    is deliberately not on the interface. This is a WIDENING of the contract's `IDiagnosticLog Log`
    and costs its consumers nothing: `DiagnosticLog` IS an `IDiagnosticLog`, so every B/C/D field,
    parameter or property typed `IDiagnosticLog` takes `comp.Log` unchanged.
  - `App._log : IDiagnosticLog?` — the field Tasks 6, 8 and 10 read.
- `AppComposition.AppVersion` is **unchanged code** and stays numeric: it flows to `SessionBootstrap.cs:42` → `SessionRecord.AppVersion` → every `session.json`, which is append-only evidentiary data that cannot be edited afterwards.

`AppComposition` is a positional `sealed record` with 20 members and exactly ONE construction site,
which passes positional arguments only. **Append the two new members at the END** — a member inserted
mid-list silently shifts everything after it and compiles only when the types happen to differ.
TWO members, not one: SHARED-CONTRACT section 3a (ADDED 2026-08-05) fixes both `Log` and `BuildInfo`
on the record, and Plans B, C and D are written against `comp.Log`.

- [ ] **Step 1: Write the failing tests**

Extend `tests/LocalScribe.App.Tests/CompositionRootTests.cs` — inside
`Build_produces_an_idle_controller_and_expanded_paths`, after the existing
`Assert.False(string.IsNullOrEmpty(comp.AppVersion));` line, add:

```csharp
        // Tier 1 plan A (2026-08-05): TWO version strings, deliberately. AppVersion is the numeric
        // one that lands in every session.json; BuildInfo carries the git SHA and never does.
        Assert.Equal("0.9.0", comp.AppVersion);
        Assert.False(string.IsNullOrEmpty(comp.BuildInfo));
        Assert.StartsWith(comp.AppVersion, comp.BuildInfo);
        Assert.NotNull(comp.Log);
        Assert.Null(comp.Log.LastError);                 // nothing has failed during Build()
```

Then create `tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs`:

```csharp
using System.IO;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Source-text pins for the diagnostics wiring in App.xaml.cs and TrayIconHost.cs (Tier 1
/// plan A, 2026-08-05). Those two files have NO unit coverage at all - 105 test files, no
/// AppTests.cs, no TrayIconHostTests.cs - and every policy this round adds is already extracted
/// into a WPF-free tested class. What is left is one-line wiring, and a text assertion is the only
/// guard available for it; XamlHygieneTests.AppIcon_ExistsAndIsWiredInCsproj asserts on raw csproj
/// text the same way. If one of these fails after a refactor, re-point the pin - do not delete it
/// and do not delete the wiring.</summary>
public sealed class DiagnosticsWiringTests
{
    private static string App() => File.ReadAllText(RepoPaths.AppXaml("App.xaml.cs"));

    [Fact]
    public void Startup_records_the_build_stamp_as_the_first_diagnostic_line()
    {
        string app = App();
        Assert.Contains("_log = comp.Log;", app);
        Assert.Contains("\"LocalScribe started\"", app);
        Assert.Contains("\"build=\" + comp.BuildInfo", app);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~DiagnosticsWiringTests|FullyQualifiedName~CompositionRootTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: FAIL to build — `error CS1061: 'AppComposition' does not contain a definition for 'BuildInfo'`.

- [ ] **Step 3: Add the two members to `AppComposition`**

In `src/LocalScribe.App/CompositionRoot.cs`, add to the top of the using block:

```csharp
using System.Reflection;
using LocalScribe.Core.Diagnostics;
```

Extend the record's doc comment with a second `<param>` tag (the `<param name="Embedding">` tag
already there is the precedent for documenting the one member whose identity is surprising):

```csharp
/// <param name="BuildInfo">The SECOND version string (Tier 1 plan A, 2026-08-05): the assembly's
/// InformationalVersion, e.g. "0.9.0+g1628935". Goes to the diagnostic log header, the Settings
/// About line and support copy-paste. Deliberately NOT <see cref="AppVersion"/>, which is the
/// numeric assembly version and is written into every session.json - append-only evidentiary data
/// that must stay short and stable.</param>
```

Then change the record's closing line from `AssistantGate AssistantGate);` to:

```csharp
    AssistantGate AssistantGate,
    string BuildInfo,
    DiagnosticLog Log);
```

- [ ] **Step 4: Build the two values and pass them**

In `CompositionRoot.Build()`, immediately after the existing `appVersion` line (`:67`), add:

```csharp
        // Tier 1 plan A (2026-08-05): a SECOND version string, deliberately not folded into
        // appVersion above. Assembly.GetName().Version is the ASSEMBLY version and ignores
        // AssemblyInformationalVersionAttribute entirely - MSBuild strips any "+sha" suffix before
        // deriving it - so the two are genuinely different values. REJECTED: changing the line
        // above to read the informational version, because that string flows to
        // SessionBootstrap.cs:42 -> SessionRecord.AppVersion -> every session.json, which is
        // append-only evidentiary data that cannot be edited afterwards.
        string buildInfo = typeof(CompositionRoot).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? appVersion;
        // Diagnostic log (Tier 1 plan A): built HERE rather than in App.OnStartup so the seams
        // constructed below - the capture provider and the diarisation helper - can be handed the
        // same sink. ZERO IO in the ctor (Directory.CreateDirectory lives in the drain), which is
        // what keeps CompositionRootTests from creating folders in the developer's real
        // %USERPROFILE%\LocalScribe on every test run. The settings func is re-invoked per write:
        // SettingsService swaps the reference on save, so a captured value would pin the level.
        // This local is THE process-wide instance - it is returned as AppComposition.Log at the
        // bottom of this method, and everything outside Build() reaches it as comp.Log
        // (SHARED-CONTRACT section 3a). REJECTED: a second sink for any consumer - two logs would
        // interleave two chained drains over one file.
        var log = new DiagnosticLog(paths, TimeProvider.System, () => settingsService.Current.Logging);
```

Then change the return statement's last line from
`summaries, summarizer, assistantModels, assistantChat, assistantGate);` to:

```csharp
            summaries, summarizer, assistantModels, assistantChat, assistantGate, buildInfo, log);
```

- [ ] **Step 5: Hold the log in an `App` field and write the header line**

In `src/LocalScribe.App/App.xaml.cs`, add a field beside the other private fields (after
`private readonly CancellationTokenSource _shutdownCts = new();`):

```csharp
    // Diagnostic sink (Tier 1 plan A, 2026-08-05). A FIELD, not a local, because OnExit is a
    // separate method and the dispatcher handler registered at the very top of OnStartup reads it
    // before CompositionRoot.Build() has run - null-conditional everywhere, the same shape _tray
    // uses (see the comment at the mainVm construction below).
    private LocalScribe.Core.Diagnostics.IDiagnosticLog? _log;
```

Immediately after `var comp = CompositionRoot.Build();` add:

```csharp
        // The log is live from here on. This first line is the file's header: it stamps the build
        // into every month's file, which is the value support asks for first.
        _log = comp.Log;
        _log.Write(LocalScribe.Core.Diagnostics.DiagnosticLevels.Info, "app",
            "LocalScribe started", "build=" + comp.BuildInfo);
```

- [ ] **Step 6: Run the tests and confirm they pass**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~DiagnosticsWiringTests|FullyQualifiedName~CompositionRootTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/CompositionRoot.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/CompositionRootTests.cs tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs
git commit -m "feat(diagnostics): AppComposition.BuildInfo and Log, header line at startup"
```

---

## Task 6: `UnhandledExceptionRecorder` replaces the swallow

**Files:**
- Create: `src/LocalScribe.App/Services/UnhandledExceptionRecorder.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs:50-55` (the handler), `:13-39` (a field), `:174-183` (assign the recorder)
- Test: `tests/LocalScribe.App.Tests/UnhandledExceptionRecorderTests.cs` (create), `tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs` (add one fact)

**Interfaces:**
- Consumes: `IDiagnosticLog.Write` and `DiagnosticLevels.Error` (Tasks 3-4), `DiagnosticRedaction.ForException` (Task 3), `AppComposition.Log` (Task 5), and the existing `InfoBarErrorReporter.Messages` collection — deliberately NOT `Report(context, ex)`, see Step 4.
- Produces: `public sealed class UnhandledExceptionRecorder(Action<Exception> log, Action<Exception> notify)` with `public bool Handle(Exception ex)`. No other task consumes it.

The handler is registered at `App.xaml.cs:55`, which is 35 lines before `CompositionRoot.Build()`,
121 lines before `InfoBarErrorReporter` exists and ~760 lines before `_tray` exists. It therefore
reads a FIELD that is null until the graph is built — the house null-conditional field capture
already documented at `App.xaml.cs:180-183` and used at `:1058` (`_tray?.ShowNotice(m)`).

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.App.Tests/UnhandledExceptionRecorderTests.cs`:

```csharp
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The record-and-notify policy behind DispatcherUnhandledException (Tier 1 plan A,
/// 2026-08-05), extracted WPF-free so it can be tested at all - App.xaml.cs has no test coverage,
/// and every tested App-layer service is an extracted class (the StopConfirmToastGuard precedent,
/// rationale recorded at App.xaml.cs:864-874).</summary>
public sealed class UnhandledExceptionRecorderTests
{
    [Fact]
    public void Handle_logs_then_notifies_with_the_same_exception_and_marks_it_handled()
    {
        var logged = new List<Exception>();
        var notified = new List<Exception>();
        var recorder = new UnhandledExceptionRecorder(logged.Add, notified.Add);
        var boom = new InvalidOperationException("stop faulted");

        Assert.True(recorder.Handle(boom));
        Assert.Same(boom, Assert.Single(logged));
        Assert.Same(boom, Assert.Single(notified));
    }

    [Fact]
    public void A_throwing_log_still_notifies_and_still_returns_true()
    {
        var notified = new List<Exception>();
        var recorder = new UnhandledExceptionRecorder(
            _ => throw new IOException("disk full"), notified.Add);

        // Each side is independently guarded: a failing LOG must not cost the user the NOTICE.
        Assert.True(recorder.Handle(new InvalidOperationException("x")));
        Assert.Single(notified);
    }

    [Fact]
    public void A_throwing_notify_still_returns_true_after_the_log_ran()
    {
        var logged = new List<Exception>();
        var recorder = new UnhandledExceptionRecorder(
            logged.Add, _ => throw new InvalidOperationException("no window yet"));

        Assert.True(recorder.Handle(new InvalidOperationException("x")));
        Assert.Single(logged);
    }

    [Fact]
    public void Both_sides_throwing_still_returns_true()
    {
        // The value returned here becomes DispatcherUnhandledExceptionEventArgs.Handled. Returning
        // false - even once, even on the "logging itself is broken" path - lets an unhandled
        // AsyncRelayCommand fault kill the whole tray app, and that crash can land MID-RECORDING.
        var recorder = new UnhandledExceptionRecorder(
            _ => throw new IOException("disk full"), _ => throw new InvalidOperationException("no ui"));
        Assert.True(recorder.Handle(new InvalidOperationException("x")));
    }
}
```

Task 7 appends one more fact to this file — `A_dispatcher_exception_leaves_exactly_one_error_line_and_it_names_the_dispatcher`.
It belongs there, not here: it drives a REAL `InfoBarErrorReporter` over a REAL `DiagnosticLog`, and
the two-argument reporter constructor it needs does not exist until Task 7 Step 3.

Add this fact to `tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs`:

```csharp
    [Fact]
    public void Dispatcher_exceptions_are_recorded_not_swallowed()
    {
        string app = App();
        Assert.Contains("ex.Handled = _recorder?.Handle(ex.Exception) ?? true;", app);
        // The line this round exists to delete. Its comment said "Stage 7 can add real logging
        // here; for now, swallow it" - this IS that round.
        Assert.DoesNotContain("DispatcherUnhandledException += (_, ex) => { ex.Handled = true; };", app);
        // ONE error line per dispatcher exception. notify enqueues straight onto the InfoBar queue
        // rather than calling errors.Report(...), which after Task 7 has its own log sink and would
        // write a SECOND error entry at source "ui" - and steal LastError from the dispatcher line.
        Assert.Contains("errors.Messages.Add(\"Unexpected error: \" + ex.Message)", app);
        Assert.DoesNotContain("errors.Report(\"Unexpected error\"", app);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~UnhandledExceptionRecorderTests|FullyQualifiedName~DiagnosticsWiringTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: FAIL to build — `error CS0246: The type or namespace name 'UnhandledExceptionRecorder' could not be found`.

- [ ] **Step 3: Write the recorder**

Create `src/LocalScribe.App/Services/UnhandledExceptionRecorder.cs`:

```csharp
namespace LocalScribe.App.Services;

/// <summary>Records a dispatcher-unhandled exception and notifies the user, replacing the
/// swallow-everything handler that stood at App.xaml.cs:50-55 since Stage 3 (Tier 1 plan A,
/// 2026-08-05, spec item T1-1). Handle() returns the value to assign to
/// DispatcherUnhandledExceptionEventArgs.Handled and MUST return true on EVERY path - including
/// when logging or reporting themselves throw - because the original comment is still true: an
/// unhandled AsyncRelayCommand fault (AwaitAndThrowIfFailed rethrows a faulted Stop/Pause command
/// on the dispatcher) kills the whole tray app, and that crash can land mid-recording.
///
/// Delegate-injected and WPF-free so it is testable: App.xaml.cs itself has no test coverage at
/// all, and every tested App-layer service is an extracted class - the StopConfirmToastGuard
/// precedent, whose extraction rationale is recorded at App.xaml.cs:864-874.</summary>
public sealed class UnhandledExceptionRecorder(Action<Exception> log, Action<Exception> notify)
{
    public bool Handle(Exception ex)
    {
        // TWO independent try blocks, not one around both: a failing log must not cost the user
        // the notice, and a failing notice (no window yet, shutting down) must not cost the log
        // line. REJECTED: one try - the second side would be skipped whenever the first threw,
        // which is precisely the situation worth recording.
        try { log(ex); } catch { }
        try { notify(ex); } catch { }
        return true;
    }
}
```

- [ ] **Step 4: Wire the one-line handler**

In `src/LocalScribe.App/App.xaml.cs`, add a field beside `_log` (Task 5):

```csharp
    // Tier 1 plan A (2026-08-05): assigned after CompositionRoot.Build() and after the InfoBar
    // reporter exists, ~120 lines below the handler that reads it. Null-conditional until then.
    private Services.UnhandledExceptionRecorder? _recorder;
```

Replace the handler at `:50-55` (the comment block plus the one-line lambda) with:

```csharp
        // Safety net: CommunityToolkit's AsyncRelayCommand (AwaitAndThrowIfFailed) rethrows a
        // faulted Stop/Pause command's exception back on the dispatcher. Without this handler that
        // becomes an unhandled exception that crashes the whole tray app.
        // Tier 1 plan A (2026-08-05) replaces the old "for now, swallow it" with record-and-notify.
        // The handler is registered HERE - 35 lines before CompositionRoot.Build(), 121 before the
        // InfoBar reporter exists - so it reads a FIELD that is null until the graph is built: the
        // house null-conditional field capture (see the _tray note below and _tray?.ShowNotice at
        // the startup-scan block). Until then, and if the recorder is somehow null, Handled still
        // becomes true - a crash here can land mid-recording.
        DispatcherUnhandledException += (_, ex) => { ex.Handled = _recorder?.Handle(ex.Exception) ?? true; };
```

Then, immediately after `var errors = new InfoBarErrorReporter(dispatch);` (`:176`), add:

```csharp
        // Upgrade the dispatcher handler now that both sinks exist (Tier 1 plan A). The log gets
        // the type, every inner message (marked) and the stack; the user gets the one-line message
        // the InfoBar already shows for every other command failure.
        //
        // notify enqueues DIRECTLY rather than calling errors.Report(...), and that is the whole
        // point of these five lines. Task 7 gives `errors` its own log sink, so Report would write
        // a SECOND error entry - same exception, source "ui", message "Unexpected error" - and
        // because DiagnosticLog latches LastError on every error-level entry, the "ui" line would
        // land last and Settings' "Copy last error" would hand support the LESS specific of the
        // two. One exception, ONE line, at source "dispatcher". REJECTED: a
        // Report-without-logging overload on IUiErrorReporter - a second method on a two-method
        // seam, for one caller. The string below reproduces Report's user-visible format
        // (context + ": " + ex.Message) exactly, and MainWindow.xaml.cs:37,131,136-138 already
        // reads .Messages directly, so this is established surface, not a reach-in.
        _recorder = new Services.UnhandledExceptionRecorder(
            log: ex => comp.Log.Write(LocalScribe.Core.Diagnostics.DiagnosticLevels.Error,
                "dispatcher", "Unhandled dispatcher exception",
                LocalScribe.Core.Diagnostics.DiagnosticRedaction.ForException(ex)),
            notify: ex => dispatch(() => errors.Messages.Add("Unexpected error: " + ex.Message)));
```

`dispatch` is the same `Action<Action>` already in scope at `:176` and handed to
`new InfoBarErrorReporter(dispatch, comp.Log)` on the line above, so the marshalling is identical to
`Report`'s.

- [ ] **Step 5: Run the tests and confirm they pass**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~UnhandledExceptionRecorderTests|FullyQualifiedName~DiagnosticsWiringTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: **Passed! - Failed: 0, Passed: 6** (4 recorder facts + 2 wiring facts).

- [ ] **Step 6: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/Services/UnhandledExceptionRecorder.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/UnhandledExceptionRecorderTests.cs tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs
git commit -m "feat(diagnostics): record and surface dispatcher exceptions instead of swallowing"
```

---

## Task 7: Optional log sinks on the two `IUiErrorReporter` implementations

**Files:**
- Modify: `src/LocalScribe.App/Services/InfoBarErrorReporter.cs:10-17`, `src/LocalScribe.App/Services/TrayNoticeReporter.cs:6-9`, `src/LocalScribe.App/Services/IUiErrorReporter.cs:1-11` (one doc sentence), `src/LocalScribe.App/App.xaml.cs:176` and `:1063`
- Modify: `tests/LocalScribe.App.Tests/AppServiceFakes.cs` (add `FakeDiagnosticLog`), `tests/LocalScribe.App.Tests/InfoBarErrorReporterTests.cs` (becomes `IDisposable` with a temp root), `tests/LocalScribe.App.Tests/UnhandledExceptionRecorderTests.cs` (becomes `IDisposable`; gains the one-line-per-dispatcher-exception fact)
- Test: `tests/LocalScribe.App.Tests/TrayNoticeReporterTests.cs` (create)

**Interfaces:**
- Consumes: `IDiagnosticLog` (Task 4), `DiagnosticLevels`, `DiagnosticRedaction.ForException` (Task 3), `AppComposition.Log` (Task 5).
- Produces:
  - `InfoBarErrorReporter(Action<Action> dispatch, IDiagnosticLog? log = null)` — `Messages` and `DismissOldest()` unchanged.
  - `TrayNoticeReporter(Action<string> notify, IDiagnosticLog? log = null)`.
  - `FakeDiagnosticLog` in `AppServiceFakes.cs`: `public readonly List<(string Level, string Source, string Message, string? Detail)> Entries`, `public int Flushes { get; }`. Tasks 8 and 11 use it.

**A decorator will not compile here.** `InfoBarErrorReporter` is consumed CONCRETELY:
`MainWindowViewModel.cs:14` declares `public InfoBarErrorReporter Errors { get; }` and
`MainWindow.xaml.cs:37,131,136-138` reads `.Messages` / `.DismissOldest()` directly. The optional
parameter defaulted `null` also keeps every existing test construction site compiling.

- [ ] **Step 1: Write the failing tests**

Add to `tests/LocalScribe.App.Tests/AppServiceFakes.cs` (it already carries the four shared App
fakes; add `using LocalScribe.Core.Diagnostics;` to its using block):

```csharp
/// <summary>Records diagnostic lines in memory. Lives in this shared file rather than being
/// re-declared per test class - the "no cross-file test helper" convention covers fakes ONE class
/// needs, and four separate classes need this one (Tier 1 plan A, 2026-08-05). Flushes counts
/// FlushAsync calls so an exit-path test can prove the flush happened.</summary>
public sealed class FakeDiagnosticLog : IDiagnosticLog
{
    public readonly List<(string Level, string Source, string Message, string? Detail)> Entries = new();
    public int Flushes { get; private set; }

    public void Write(string level, string source, string message, string? detail = null)
        => Entries.Add((level, source, message, detail));

    public Task FlushAsync(CancellationToken ct) { Flushes++; return Task.CompletedTask; }
}
```

In `tests/LocalScribe.App.Tests/InfoBarErrorReporterTests.cs`, the using block becomes exactly:

```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;
```

(`System.IO` is NOT listed: `LocalScribe.App.Tests.csproj:5` sets `<ImplicitUsings>enable</ImplicitUsings>`,
so `System.IO`, `System.Linq` and `System.Threading.Tasks` are already in scope.) Change the class
declaration from `public sealed class InfoBarErrorReporterTests` to
`public sealed class InfoBarErrorReporterTests : IDisposable`, then add the house temp-root pair and
these facts — the second one drives a REAL `DiagnosticLog` on to real disk:

```csharp
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-infobar-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Every_report_and_info_also_reaches_the_diagnostic_log_when_one_is_supplied()
    {
        var log = new FakeDiagnosticLog();
        var reporter = new InfoBarErrorReporter(a => a(), log);

        reporter.Report("Delete session", new InvalidOperationException("folder is locked"));
        reporter.Info("Recovered 2 interrupted session(s)");

        Assert.Equal(2, log.Entries.Count);
        // The Report CONTEXT is a fixed literal at every verified call site ("Export", "Delete
        // session", "Tag session " + sessionId), so it goes to the log bare.
        Assert.Equal(("error", "ui", "Delete session"),
            (log.Entries[0].Level, log.Entries[0].Source, log.Entries[0].Message));
        // The user sees the MESSAGE only; the stack belongs in the file, marked so the
        // IncludeTranscriptText switch governs the exception text.
        Assert.Contains("System.InvalidOperationException", log.Entries[0].Detail!);
        Assert.Contains(DiagnosticRedaction.Open, log.Entries[0].Detail!);
        // An Info message is caller-composed and CAN carry party-identifying text, so it reaches
        // Write() MARKED. FakeDiagnosticLog records what Write() was handed; the marker only turns
        // into [redacted] inside the real DiagnosticLog - see the next fact.
        Assert.Equal(("info", "ui", DiagnosticRedaction.Mark("Recovered 2 interrupted session(s)")),
            (log.Entries[1].Level, log.Entries[1].Source, log.Entries[1].Message));
    }

    [Fact]
    public async Task An_Info_message_carrying_a_participant_name_is_redacted_at_the_default_setting()
    {
        // The defect this fact exists to prevent. VERIFIED live Info call sites compose privileged
        // identifiers straight into the string: MetadataEditorViewModel.cs:369
        // ($"{member.Name} is already a participant." - a real person from the matter roster),
        // ExportDialogViewModel.cs:197 ("Exported to " + dest, where dest comes from the filename
        // template and embeds the session title, i.e. the matter/client name),
        // ImportDialogViewModel.cs:282 and VocabularyEditorViewModel.cs:71,90 (custom vocabulary
        // terms are BY DESIGN the names of parties and witnesses). Unmarked, all of those would sit
        // in diagnostics\diag-yyyyMM.jsonl at the DEFAULT settings - an undeclared copy of
        // privileged identifiers outside every retention and purge path. Note the smoke check
        // (Task 12) would NOT catch this: these are names, not utterances.
        var paths = new StoragePaths(_root);
        var log = new DiagnosticLog(paths, new ManualUtcTimeProvider(
            new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero)), () => new LoggingSetting());

        new InfoBarErrorReporter(a => a(), log).Info("Ms Roe is already a participant.");
        await log.FlushAsync(default);

        string text = await File.ReadAllTextAsync(
            Path.Combine(paths.DiagnosticsDir, "diag-202608.jsonl"));
        Assert.DoesNotContain("Ms Roe", text);
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public void The_log_line_is_written_even_if_the_message_is_never_dispatched()
    {
        // The InfoBar queue is drained by a window that may never open (shutdown, tray-only run),
        // so the log write must happen BEFORE the dispatch, not inside it.
        var log = new FakeDiagnosticLog();
        var reporter = new InfoBarErrorReporter(_ => { }, log);   // dispatch that never runs
        reporter.Report("Export", new IOException("no space"));
        Assert.Single(log.Entries);
        Assert.Empty(reporter.Messages);
    }

    [Fact]
    public void A_reporter_built_without_a_log_still_works()
        => new InfoBarErrorReporter(a => a()).Info("no sink, no throw");
```

`ManualUtcTimeProvider` needs no using at all: `LocalScribe.App.Tests.csproj` links its source
(`<Compile Include="..\LocalScribe.Core.Tests\ManualUtcTimeProvider.cs" Link="ManualUtcTimeProvider.cs" />`)
and the class sits in the GLOBAL namespace. `LoggingSetting` comes from `LocalScribe.Core.Model`,
which is why that using is in the block above.

The user-visible InfoBar text is UNCHANGED by this: `Messages` still receives the raw `message`.
Only the log copy is marked. The two pre-existing facts in this file assert on `Messages` and keep
passing untouched.

Create `tests/LocalScribe.App.Tests/TrayNoticeReporterTests.cs`:

```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Diagnostics;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The startup/background half of the IUiErrorReporter seam (Tier 1 plan A, 2026-08-05).
/// A tray balloon is suppressed outright by Focus Assist, so the log line is the only durable
/// record of a recovery failure - it must be written whether or not the balloon is seen.</summary>
public sealed class TrayNoticeReporterTests
{
    [Fact]
    public void Report_and_Info_notify_and_log()
    {
        var notices = new List<string>();
        var log = new FakeDiagnosticLog();
        var reporter = new TrayNoticeReporter(notices.Add, log);

        reporter.Report("Recovery of session s1", new InvalidOperationException("torn"));
        reporter.Info("Recovered 2 interrupted session(s)");

        // The existing balloon format is PINNED by StartupOrchestratorTests - unchanged here.
        Assert.Equal(new[] { "Recovery of session s1: torn", "Recovered 2 interrupted session(s)" },
            notices);
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(("error", "startup"), (log.Entries[0].Level, log.Entries[0].Source));
        Assert.Equal(("info", "startup"), (log.Entries[1].Level, log.Entries[1].Source));
        // Same rule as InfoBarErrorReporter: the Report CONTEXT is a fixed literal and goes bare;
        // the Info MESSAGE is caller-composed and reaches the log MARKED. StartupOrchestrator's
        // recovery summary rides this path (Task 8), and Plan B adds more callers.
        Assert.Equal("Recovery of session s1", log.Entries[0].Message);
        Assert.Equal(DiagnosticRedaction.Mark("Recovered 2 interrupted session(s)"),
            log.Entries[1].Message);
    }

    [Fact]
    public void A_reporter_built_without_a_log_still_notifies()
    {
        var notices = new List<string>();
        new TrayNoticeReporter(notices.Add).Info("hello");
        Assert.Single(notices);
    }
}
```

Finally, append one fact to `tests/LocalScribe.App.Tests/UnhandledExceptionRecorderTests.cs` (created
in Task 6). It lands HERE because the two-argument `InfoBarErrorReporter` it needs only exists from
Step 3 below. That file's using block becomes exactly:

```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;
```

Change its class declaration to
`public sealed class UnhandledExceptionRecorderTests : IDisposable`, and add the house temp-root
pair at the top of the class:

```csharp
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-unhandled-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task A_dispatcher_exception_leaves_exactly_one_error_line_and_it_names_the_dispatcher()
    {
        // The cross-task seam Tasks 6 and 7 create together, and the reason App.xaml.cs's notify
        // lambda enqueues instead of calling errors.Report(...): this Step gives
        // InfoBarErrorReporter its OWN log sink, so a Report on the dispatcher path would write a
        // SECOND error entry at source "ui" - and DiagnosticLog latches LastError on every
        // error-level entry, so the "ui" line would win and Settings' "Copy last error" would hand
        // support the less specific of the two. Drives the REAL classes, wired exactly as
        // App.xaml.cs wires them. If it fails, re-read that lambda - do not relax the assertion.
        var paths = new StoragePaths(_root);
        var log = new DiagnosticLog(paths, new ManualUtcTimeProvider(
            new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero)), () => new LoggingSetting());
        var errors = new InfoBarErrorReporter(a => a(), log);
        var recorder = new UnhandledExceptionRecorder(
            log: ex => log.Write(DiagnosticLevels.Error, "dispatcher",
                "Unhandled dispatcher exception", DiagnosticRedaction.ForException(ex)),
            notify: ex => errors.Messages.Add("Unexpected error: " + ex.Message));

        Assert.True(recorder.Handle(new InvalidOperationException("stop faulted")));
        await log.FlushAsync(default);

        Assert.Equal("dispatcher", log.LastError!.Source);
        Assert.Equal("Unhandled dispatcher exception", log.LastError!.Message);
        string[] lines = await File.ReadAllLinesAsync(
            Path.Combine(paths.DiagnosticsDir, "diag-202608.jsonl"));
        Assert.Single(lines);                                  // ONE line, not two
        // ...and the user still sees the exact string Report would have produced.
        Assert.Equal(new[] { "Unexpected error: stop faulted" }, errors.Messages);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~InfoBarErrorReporterTests|FullyQualifiedName~TrayNoticeReporterTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: FAIL to build — `error CS1729: 'InfoBarErrorReporter' does not contain a constructor that takes 2 arguments`.

- [ ] **Step 3: Add the sink to `InfoBarErrorReporter`**

Replace the body of `src/LocalScribe.App/Services/InfoBarErrorReporter.cs` (keep the existing class
doc comment, append the second paragraph):

```csharp
using System.Collections.ObjectModel;
using LocalScribe.Core.Diagnostics;
namespace LocalScribe.App.Services;

/// <summary>IUiErrorReporter surfacing into MainWindow's InfoBar (design 7.5). WPF-free: the
/// queue is plain ObservableCollection state; Report/Info may be called from any thread and
/// marshal through the injected dispatch (the UI thread in the app, an inline runner in
/// tests). MainWindow mirrors Messages[0] into the InfoBar and calls DismissOldest when the
/// user closes it; the collection outlives any single MainWindow instance, so errors queued
/// while the window is closed appear on next open.
///
/// Tier 1 plan A (2026-08-05): the optional log sink is a PARAMETER, not a decorator - this class
/// is consumed concretely (MainWindowViewModel.cs:14 declares InfoBarErrorReporter Errors, and
/// MainWindow.xaml.cs reads .Messages/.DismissOldest()), so a decorator at the App.xaml.cs
/// construction site would not compile. Defaulted null so every existing test keeps building.
///
/// The Report CONTEXT reaches the log bare - every verified call site passes a fixed literal
/// ("Export", "Delete session", "Tag session " + sessionId). The Info MESSAGE reaches it MARKED,
/// because callers compose party-identifying text into it: MetadataEditorViewModel.cs:369 puts a
/// roster member's real NAME in it and ExportDialogViewModel.cs:197 puts a destination path built
/// from the session title (the matter/client name) in it. Unmarked, both would land in
/// diagnostics\ at the DEFAULT settings - an undeclared copy of privileged identifiers outside
/// every retention and purge path.</summary>
public sealed class InfoBarErrorReporter(Action<Action> dispatch, IDiagnosticLog? log = null)
    : IUiErrorReporter
{
    public ObservableCollection<string> Messages { get; } = [];

    public void Report(string context, Exception ex)
    {
        // Log BEFORE dispatching: the queue is drained by a window that may never open, and the
        // durable record must not depend on the user seeing the InfoBar.
        log?.Write(DiagnosticLevels.Error, "ui", context, DiagnosticRedaction.ForException(ex));
        dispatch(() => Messages.Add(context + ": " + ex.Message));
    }

    public void Info(string message)
    {
        // MARKED - see the class comment. The InfoBar itself still shows the raw message below;
        // only the log copy is delimited, so Settings.Logging.IncludeTranscriptText governs it.
        // REJECTED: marking at the CALL SITES instead - there are twenty-odd of them across six
        // view models, a new one lands most rounds, and forgetting the wrapper is silent.
        log?.Write(DiagnosticLevels.Info, "ui", DiagnosticRedaction.Mark(message));
        dispatch(() => Messages.Add(message));
    }

    public void DismissOldest()
    {
        if (Messages.Count > 0) Messages.RemoveAt(0);
    }
}
```

- [ ] **Step 4: Add the sink to `TrayNoticeReporter`**

Replace `src/LocalScribe.App/Services/TrayNoticeReporter.cs` entirely:

```csharp
using LocalScribe.Core.Diagnostics;
namespace LocalScribe.App.Services;

/// <summary>IUiErrorReporter for startup/background work (design 7.5: background operations
/// surface via tray balloon, not an InfoBar). WPF-free: App injects a dispatcher-marshaled
/// TrayIconHost.ShowNotice hook as the notify sink.
///
/// Tier 1 plan A (2026-08-05): same optional log sink as InfoBarErrorReporter, and it matters more
/// here - Focus Assist suppresses tray balloons outright, so for a recovery failure the log line
/// can be the ONLY record that survives. Same marking rule too: fixed-literal Report contexts go
/// bare, caller-composed Info messages go MARKED.</summary>
public sealed class TrayNoticeReporter(Action<string> notify, IDiagnosticLog? log = null)
    : IUiErrorReporter
{
    public void Report(string context, Exception ex)
    {
        log?.Write(DiagnosticLevels.Error, "startup", context, DiagnosticRedaction.ForException(ex));
        notify(context + ": " + ex.Message);
    }

    public void Info(string message)
    {
        // MARKED for the same reason as InfoBarErrorReporter.Info - an IUiErrorReporter.Info string
        // is composed by its caller and can carry a name, a title or a file path. The balloon text
        // below is unchanged; only the log copy is delimited.
        log?.Write(DiagnosticLevels.Info, "startup", DiagnosticRedaction.Mark(message));
        notify(message);
    }
}
```

- [ ] **Step 5: Update the interface doc and the two construction sites**

In `src/LocalScribe.App/Services/IUiErrorReporter.cs`, replace the last sentence of the doc comment
("Stage 7 attaches real logging behind this seam.") with:

```csharp
/// implementations write every Report/Info to the diagnostic log (Tier 1 plan A, 2026-08-05), and
/// the dispatcher exception is now recorded too - UnhandledExceptionRecorder. Both implementations
/// log the Info MESSAGE marked as privileged (DiagnosticRedaction.Mark) and the Report CONTEXT
/// bare: a context is a fixed literal at every call site, an Info string is composed by its caller
/// and routinely carries a participant name, a session title or an export path. Keep contexts
/// literal - if you ever need a name in one, mark it at that call site.</summary>
```

Also amend the preceding sentence "Nothing relies on the globally-swallowed
DispatcherUnhandledException" to "Nothing relies on the dispatcher handler for correctness; both".

In `src/LocalScribe.App/App.xaml.cs`, change `:176` to:

```csharp
        var errors = new InfoBarErrorReporter(dispatch, comp.Log);
```

and the `TrayNoticeReporter` construction inside the `StartupOrchestrator` call (`:1063`) to:

```csharp
            new TrayNoticeReporter(notify, comp.Log),
```

- [ ] **Step 6: Run the tests and confirm they pass**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~InfoBarErrorReporterTests|FullyQualifiedName~TrayNoticeReporterTests|FullyQualifiedName~UnhandledExceptionRecorderTests|FullyQualifiedName~StartupOrchestratorTests|FullyQualifiedName~MainWindowViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: all pass. `StartupOrchestratorTests` and `MainWindowViewModelTests` are in the filter
deliberately — they pin the balloon string format and the concrete `Errors` property type, and
neither changes here: `TrayNoticeReporter` still calls `notify(message)` with the RAW string, so
`TrayNoticeReporter_formats_context_and_message_into_the_notify_sink` is untouched by the marking.

- [ ] **Step 7: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/Services/InfoBarErrorReporter.cs src/LocalScribe.App/Services/TrayNoticeReporter.cs src/LocalScribe.App/Services/IUiErrorReporter.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/AppServiceFakes.cs tests/LocalScribe.App.Tests/InfoBarErrorReporterTests.cs tests/LocalScribe.App.Tests/TrayNoticeReporterTests.cs tests/LocalScribe.App.Tests/UnhandledExceptionRecorderTests.cs
git commit -m "feat(diagnostics): optional log sink on both IUiErrorReporter implementations"
```

---

## Task 8: `SessionDiagnosticsRecorder` — session lifecycle, downgrades, recovery

**Files:**
- Create: `src/LocalScribe.App/Services/SessionDiagnosticsRecorder.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs:88-91` (subscriptions, just after the header line), `:1059-1064` (drop the orchestrator's trailing `notify` argument; the lambda at `:1058` is deliberately UNCHANGED)
- Modify: `src/LocalScribe.App/Services/StartupOrchestrator.cs:3-10` (class doc), `:16` and `:19-21` (the `notify` seam goes), `:30-31` (the summary moves onto `IUiErrorReporter.Info`)
- Test: `tests/LocalScribe.App.Tests/SessionDiagnosticsRecorderTests.cs` (create), `tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs` (add one fact), `tests/LocalScribe.App.Tests/StartupOrchestratorTests.cs` (five construction sites, two assertions, one new fact)

**Interfaces:**
- Consumes: `IDiagnosticLog`, `DiagnosticLevels` (Tasks 3-4); `FakeDiagnosticLog` and the log-bearing `TrayNoticeReporter` (Task 7); the EXISTING `SessionController` events `StateChanged` (`Action<SessionState>`), `ErrorRaised` (`Action<string>`), `Notice` (`Action<string>`), `SessionFinalizeCompleted` (`Action<string>`), and the properties `CurrentSessionId` / `FinalizingSessionId` (both `string?`).
- Produces: `public sealed class SessionDiagnosticsRecorder(IDiagnosticLog log, Func<string?> sessionId)` with `void StateChanged(SessionState state)`, `void ErrorRaised(string code)`, `void Notice(string message)`, `void FinalizeCompleted(string finalizedSessionId)`.

**No Core change is needed to log a transcription downgrade.** `TranscriptionWorker` raises
`"VRAM_OOM"` (`TranscriptionWorker.cs:108`) and `"RTF_LAGGING"` (`:128`) on its `ErrorRaised` event,
and `SessionController.cs:516` re-raises them verbatim (`worker.ErrorRaised += e => ErrorRaised?.Invoke(e);`).

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.App.Tests/SessionDiagnosticsRecorderTests.cs`:

```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Session lifecycle and transcription-downgrade logging (Tier 1 plan A, 2026-08-05,
/// spec item T1-1: "session start/stop/recovery, transcription downgrades"). The methods are
/// called directly here and wired to SessionController's events in App.xaml.cs - the
/// StartupOrchestrator/StopConfirmToastGuard shape, because App.xaml.cs has no coverage.</summary>
public sealed class SessionDiagnosticsRecorderTests
{
    private static (SessionDiagnosticsRecorder Rec, FakeDiagnosticLog Log) Make(string? id = "s-1")
    {
        var log = new FakeDiagnosticLog();
        return (new SessionDiagnosticsRecorder(log, () => id), log);
    }

    [Fact]
    public void Every_state_change_is_an_info_line_carrying_the_session_id()
    {
        var (rec, log) = Make();
        rec.StateChanged(SessionState.Recording);
        rec.StateChanged(SessionState.Finalizing);

        Assert.Equal(new[] { "State Recording", "State Finalizing" },
            log.Entries.Select(e => e.Message).ToArray());
        Assert.All(log.Entries, e => Assert.Equal("info", e.Level));
        Assert.All(log.Entries, e => Assert.Equal("session", e.Source));
        Assert.All(log.Entries, e => Assert.Equal("session=s-1", e.Detail));
    }

    [Fact]
    public void The_session_id_is_read_per_call_not_captured()
    {
        // CurrentSessionId is null again by the time Idle arrives, and null before Start - a
        // captured value would mislabel every line either side of a session.
        var (rec, log) = Make(id: null);
        rec.StateChanged(SessionState.Idle);
        Assert.Equal("session=(none)", Assert.Single(log.Entries).Detail);
    }

    [Fact]
    public void Transcription_downgrades_are_warnings_that_name_the_cause()
    {
        var (rec, log) = Make();
        rec.ErrorRaised("VRAM_OOM");
        rec.ErrorRaised("RTF_LAGGING");
        rec.ErrorRaised("TRANSCRIPTION_FAILED");
        rec.ErrorRaised("SOMETHING_NEW");

        Assert.All(log.Entries, e => Assert.Equal("warn", e.Level));
        Assert.Contains("VRAM", log.Entries[0].Message);
        Assert.Contains("lag", log.Entries[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audio still recording", log.Entries[2].Message);
        // An unknown code must still be recorded verbatim rather than dropped - Plan C adds more.
        Assert.Contains("SOMETHING_NEW", log.Entries[3].Message);
        Assert.All(log.Entries, e => Assert.Contains("code=", e.Detail!));
    }

    [Fact]
    public void Controller_notices_are_logged_as_written()
    {
        // These are FIXED operator strings from SessionController (e.g. the per-process capture
        // fallback at :590). They carry no transcript text, which is why they can be logged whole.
        var (rec, log) = Make();
        rec.Notice("Per-process capture unavailable - recording full system audio for the remote stream (possible bleed; use headphones).");
        var only = Assert.Single(log.Entries);
        Assert.Equal("info", only.Level);
        Assert.StartsWith("Per-process capture unavailable", only.Message);
    }

    [Fact]
    public void Finalize_completion_names_the_session_it_finished()
    {
        // FinalizeCompleted fires from a background drain AFTER the controller is Idle again, so
        // it takes the id as an ARGUMENT rather than reading the live probe.
        var (rec, log) = Make(id: null);
        rec.FinalizeCompleted("s-42");
        var only = Assert.Single(log.Entries);
        Assert.Equal("Finalize completed", only.Message);
        Assert.Equal("session=s-42", only.Detail);
    }
}
```

Add this fact to `tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs`:

```csharp
    [Fact]
    public void The_session_recorder_is_subscribed_to_all_four_controller_events()
    {
        string app = App();
        Assert.Contains("comp.Controller.StateChanged += sessionDiag.StateChanged;", app);
        Assert.Contains("comp.Controller.ErrorRaised += sessionDiag.ErrorRaised;", app);
        Assert.Contains("comp.Controller.Notice += sessionDiag.Notice;", app);
        Assert.Contains("comp.Controller.SessionFinalizeCompleted += sessionDiag.FinalizeCompleted;", app);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SessionDiagnosticsRecorderTests|FullyQualifiedName~DiagnosticsWiringTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: FAIL to build — `error CS0246: The type or namespace name 'SessionDiagnosticsRecorder' could not be found`.

- [ ] **Step 3: Write the recorder**

Create `src/LocalScribe.App/Services/SessionDiagnosticsRecorder.cs`:

```csharp
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Live;
namespace LocalScribe.App.Services;

/// <summary>Turns SessionController's EXISTING event surface into diagnostic lines (Tier 1 plan A,
/// 2026-08-05, spec item T1-1: "session start/stop/recovery, transcription downgrades"). No Core
/// change was needed for the downgrades: TranscriptionWorker raises "VRAM_OOM"
/// (TranscriptionWorker.cs:108) and "RTF_LAGGING" (:128), and SessionController re-raises them
/// verbatim (:516) - they were simply never recorded anywhere.
///
/// WPF-free, and it exposes plain METHODS rather than subscribing itself: App.OnStartup does the
/// four "+=" lines and tests call the methods directly, which is the only way any of this gets
/// coverage (App.xaml.cs has none). The session id is read through a delegate, never captured -
/// CurrentSessionId is null again by the time Idle arrives.
///
/// NEVER logs transcript text: the controller's Notice strings are fixed operator messages and no
/// segment text passes through here, so nothing on this path needs a redaction marker.</summary>
public sealed class SessionDiagnosticsRecorder(IDiagnosticLog log, Func<string?> sessionId)
{
    public void StateChanged(SessionState state)
        => log.Write(DiagnosticLevels.Info, "session", "State " + state, Where());

    public void ErrorRaised(string code)
        => log.Write(DiagnosticLevels.Warn, "session", code switch
        {
            "VRAM_OOM" => "Transcription downgraded - VRAM exhausted",
            "RTF_LAGGING" => "Transcription downgraded - sustained lag behind realtime",
            "TRANSCRIPTION_FAILED" => "Live transcription stopped - audio still recording",
            "SILENT_SOURCE" => "A capture leg went silent",
            // Unknown codes are recorded verbatim, never dropped: Plans B and C add more of them,
            // and an unrecognised code is exactly the one worth seeing in a support file.
            _ => "Session error " + code,
        }, "code=" + code + " " + Where());

    public void Notice(string message) => log.Write(DiagnosticLevels.Info, "session", message, Where());

    /// <summary>Fires from the background finalize drain, AFTER the controller is Idle again - so
    /// the id arrives as an argument rather than from the live probe, which is null by then.</summary>
    public void FinalizeCompleted(string finalizedSessionId)
        => log.Write(DiagnosticLevels.Info, "session", "Finalize completed",
            "session=" + finalizedSessionId);

    private string Where() => "session=" + (sessionId() ?? "(none)");
}
```

- [ ] **Step 4: Subscribe it, and log the recovery scan**

In `src/LocalScribe.App/App.xaml.cs`, immediately after the `_log.Write(... "LocalScribe started" ...)`
line added in Task 5, add:

```csharp
        // Session lifecycle + transcription downgrades (Tier 1 plan A). Four subscriptions onto
        // events that already existed and that nothing durable ever recorded.
        var sessionDiag = new Services.SessionDiagnosticsRecorder(comp.Log,
            () => comp.Controller.CurrentSessionId ?? comp.Controller.FinalizingSessionId);
        comp.Controller.StateChanged += sessionDiag.StateChanged;
        comp.Controller.ErrorRaised += sessionDiag.ErrorRaised;
        comp.Controller.Notice += sessionDiag.Notice;
        comp.Controller.SessionFinalizeCompleted += sessionDiag.FinalizeCompleted;
```

**Leave the `notify` lambda at `:1058` exactly as it is** —
`Action<string> notify = m => Dispatcher.BeginInvoke(() => _tray?.ShowNotice(m));`. Adding a log
write to it would DOUBLE-log everything the reporter already logs: after Task 7,
`TrayNoticeReporter.Report` calls `notify(context + ": " + ex.Message)` as its LAST statement, so
every per-session recovery failure and every "Startup scan" fault would produce an `error`/`startup`
line from the reporter's sink AND an `info`/`startup` duplicate from the lambda — downgrading a
failure to info-severity noise in the same file support will read.

The summary line is the only thing that genuinely bypasses the reporter, so move IT onto the
reporter instead. In `src/LocalScribe.App/Services/StartupOrchestrator.cs`, change `RunAsync`'s
recovered-count line (`:30-31`) from `_notify($"Recovered ...")` to:

```csharp
            if (result.RecoveredIds.Count > 0)
                // Tier 1 plan A (2026-08-05): through the REPORTER, not the raw notify sink.
                // TrayNoticeReporter.Info still calls notify(message), so the balloon text is
                // unchanged - but the summary now also reaches the diagnostic log, on the same
                // path as the per-session failures below and with no duplicate. REJECTED: logging
                // inside App.xaml.cs's notify lambda - Report() calls notify() too, so every
                // failure would have been written twice, once as error and once as info.
                _errors.Info($"Recovered {result.RecoveredIds.Count} interrupted session(s)");
```

`_notify` then has no remaining reader, so remove the field, the constructor parameter and the
tuple element — the orchestrator ends with exactly ONE output seam, which is what made the
double-log possible to overlook in the first place. The class doc's
"Recovered count -> one tray balloon via notify" clause becomes
"Recovered count -> one tray balloon via IUiErrorReporter.Info (TrayNoticeReporter forwards it to
the balloon and to the diagnostic log)". The declaration becomes:

```csharp
    public StartupOrchestrator(Func<CancellationToken, Task<RecoveryScanResult>> recoverAll,
        Func<CancellationToken, Task> rebuildIndex, IUiErrorReporter errors)
        => (_recoverAll, _rebuildIndex, _errors) = (recoverAll, rebuildIndex, errors);
```

In `src/LocalScribe.App/App.xaml.cs`, drop the trailing `notify,` argument from the
`new StartupOrchestrator(...)` call (`:1059-1064`) so it ends
`new TrayNoticeReporter(notify, comp.Log));`. The `notify` local itself stays — it is what that
reporter forwards to.

In `tests/LocalScribe.App.Tests/StartupOrchestratorTests.cs`, five construction sites lose their
trailing notify argument. Three are a pure deletion — `errors, _ => { })` becomes `errors)` in
`Per_session_failures_are_reported_individually_not_swallowed` (`:60`) and
`A_faulted_scan_is_reported_and_ScanCompleted_still_completes` (`:78`), and
`new FakeUiErrorReporter(), _ => { })` becomes `new FakeUiErrorReporter())` in
`Start_is_never_blocked_by_a_slow_scan` (`:93`). The other two also move their summary assertion
onto the reporter fake, and are rewritten in full:

```csharp
    [Fact]
    public async Task Recovered_sessions_notify_once_and_rebuild_runs_after_the_scan()
    {
        var order = new List<string>();
        var errors = new FakeUiErrorReporter();
        var orchestrator = new StartupOrchestrator(
            recoverAll: _ => { order.Add("scan"); return Task.FromResult(Result(new[] { "a", "b" })); },
            rebuildIndex: _ => { order.Add("rebuild"); return Task.CompletedTask; },
            errors);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "scan", "rebuild" }, order);       // design 4.3: rebuild AFTER the scan
        // Tier 1 plan A (2026-08-05): the summary rides IUiErrorReporter.Info now, not a raw notify
        // sink. TrayNoticeReporter.Info forwards it to the balloon AND to the diagnostic log, so
        // there is ONE path and one log line where a second sink would have produced two.
        Assert.Equal(new[] { "Recovered 2 interrupted session(s)" }, errors.Infos);
        Assert.True(orchestrator.ScanCompleted.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Nothing_recovered_means_no_balloon_but_rebuild_still_runs()
    {
        var errors = new FakeUiErrorReporter();
        bool rebuilt = false;
        var orchestrator = new StartupOrchestrator(
            _ => Task.FromResult(Result(Array.Empty<string>())),
            _ => { rebuilt = true; return Task.CompletedTask; },
            errors);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Empty(errors.Infos);                             // no summary means no balloon
        Assert.True(rebuilt);
    }
```

`TrayNoticeReporter_formats_context_and_message_into_the_notify_sink` (`:110-118`) is untouched: it
constructs the reporter directly, and the log parameter is optional.

Finally add one fact proving the failure path is not doubled:

```csharp
    [Fact]
    public async Task A_recovery_failure_produces_exactly_one_log_line()
    {
        // TrayNoticeReporter.Report both logs AND notifies (notify is its last statement), so any
        // second sink on the notify side turns one failure into two lines at two severities. This
        // is the fact that fails if App.xaml.cs's notify lambda ever grows a log write again.
        var log = new FakeDiagnosticLog();
        var notices = new List<string>();
        var orchestrator = new StartupOrchestrator(
            _ => Task.FromResult(Result(Array.Empty<string>(), ("bad-1", "torn file"))),
            _ => Task.CompletedTask,
            new TrayNoticeReporter(notices.Add, log));

        await orchestrator.RunAsync(CancellationToken.None);

        var only = Assert.Single(log.Entries);
        Assert.Equal(("error", "startup", "Recovery of session bad-1"),
            (only.Level, only.Source, only.Message));
        Assert.Equal(new[] { "Recovery of session bad-1: torn file" }, notices);
    }
```

- [ ] **Step 5: Run the tests and confirm they pass**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SessionDiagnosticsRecorderTests|FullyQualifiedName~DiagnosticsWiringTests|FullyQualifiedName~StartupOrchestratorTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: **Passed! - Failed: 0, Passed: 15** (5 session-recorder facts + 3 wiring facts + the 7
`StartupOrchestratorTests` facts, i.e. its 6 pre-existing ones plus
`A_recovery_failure_produces_exactly_one_log_line`).

- [ ] **Step 6: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/Services/SessionDiagnosticsRecorder.cs src/LocalScribe.App/Services/StartupOrchestrator.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/SessionDiagnosticsRecorderTests.cs tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs tests/LocalScribe.App.Tests/StartupOrchestratorTests.cs
git commit -m "feat(diagnostics): log session lifecycle, downgrades and the recovery scan"
```

---

## Task 9: Capture diagnostics and helper-process exits

**Files:**
- Create: `src/LocalScribe.Core/Audio/IDiagnosticSource.cs`, `src/LocalScribe.Core/Audio/CaptureDiagnostics.cs`
- Modify: `src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs:37,79-81`, `src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs:12-30,47-65`, `src/LocalScribe.Core/Diarisation/SherpaHelperDiariser.cs:1,5,47,81`, `src/LocalScribe.App/CompositionRoot.cs:85-89,138`
- Test: `tests/LocalScribe.Core.Tests/CaptureDiagnosticsTests.cs` (create), `tests/LocalScribe.Core.Tests/SherpaHelperDiariserTests.cs` (add two facts and one fake)

**Interfaces:**
- Consumes: `IDiagnosticLog`, `DiagnosticLevels` (Tasks 3-4); the `log` local built in `CompositionRoot.Build()` (Task 5 Step 4). Both wirings in Step 9 are INSIDE `Build()`, which is the only scope where that local is reachable — and it is the very instance returned as `AppComposition.Log`, so these two sinks and every `comp.Log` consumer share ONE log (SHARED-CONTRACT section 3a). Outside `Build()` there is no local: use `comp.Log`.
- Produces:
  - `public interface IDiagnosticSource { event Action<string>? Diagnostic; }` in `LocalScribe.Core.Audio`; implemented by `ProcessLoopbackCapture`.
  - `public static class CaptureDiagnostics { public static ICaptureSource Attach(ICaptureSource source, Action<string>? sink); }` — returns the same instance so call sites read as a wrap.
  - `WasapiCaptureSourceProvider(Func<Settings> settingsProvider, IAudioSessionScanner scanner, ICaptureDeviceEnumerator? deviceEnumerator = null, Action<string>? diagnostic = null)`.
  - `SherpaHelperDiariser(IDiarisationHelper helper, IDiagnosticLog? log = null)`.

`ProcessLoopbackCapture.Diagnostic` has existed since the Stage-1 spike and is subscribed by exactly
one place in the whole solution: `SpikeRunner/Program.cs:55`, a console harness. Activation-format
fallbacks and mid-session re-establishment have therefore been invisible in the shipping app.

- [ ] **Step 1: Write the failing capture test**

Create `tests/LocalScribe.Core.Tests/CaptureDiagnosticsTests.cs`:

```csharp
using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Tests;

/// <summary>Attaching a diagnostic sink to a capture source that has one (Tier 1 plan A,
/// 2026-08-05). ProcessLoopbackCapture has raised these lines since the Stage-1 spike and only
/// SpikeRunner/Program.cs:55 ever subscribed - the shipping app never saw an activation fallback
/// or a device-invalidated recovery. ProcessLoopbackCapture itself cannot be unit-tested (it
/// activates real WASAPI), so the SEAM is tested here over a fake that raises on demand.</summary>
public sealed class CaptureDiagnosticsTests
{
    /// <summary>A capture source that can talk. RaiseDiagnostic drives the event synchronously -
    /// the house fake shape (explicit RaiseXxx, never an assertion inside the fake).</summary>
    private sealed class TalkingSource : ICaptureSource, IDiagnosticSource
    {
        public SourceKind Source => SourceKind.Remote;
        public event Action<AudioFrame>? FrameAvailable;
        public event Action<string>? Diagnostic;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void RaiseDiagnostic(string message) => Diagnostic?.Invoke(message);
        public void RaiseFrame(AudioFrame frame) => FrameAvailable?.Invoke(frame);
    }

    private sealed class SilentSource : ICaptureSource
    {
        public SourceKind Source => SourceKind.Local;
        public event Action<AudioFrame>? FrameAvailable;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void RaiseFrame(AudioFrame frame) => FrameAvailable?.Invoke(frame);
    }

    [Fact]
    public void Attach_forwards_every_diagnostic_line_and_returns_the_same_instance()
    {
        var lines = new List<string>();
        var source = new TalkingSource();

        var returned = CaptureDiagnostics.Attach(source, lines.Add);

        Assert.Same(source, returned);
        source.RaiseDiagnostic("activation fell back to native format 48000/2");
        source.RaiseDiagnostic("re-established after AUDCLNT_E_DEVICE_INVALIDATED");
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("activation fell back", lines[0]);
    }

    [Fact]
    public void Attach_no_ops_for_a_source_with_nothing_to_say()
    {
        // MicCaptureSource has no Diagnostic event, which is exactly why this is a SEPARATE
        // interface rather than a member of ICaptureSource.
        var source = new SilentSource();
        Assert.Same(source, CaptureDiagnostics.Attach(source, _ => { }));
    }

    [Fact]
    public void Attach_no_ops_for_a_null_sink()
    {
        var source = new TalkingSource();
        CaptureDiagnostics.Attach(source, null);
        source.RaiseDiagnostic("nobody listening");   // must not throw
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~CaptureDiagnosticsTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: FAIL to build — `error CS0246: The type or namespace name 'IDiagnosticSource' could not be found`.

- [ ] **Step 3: Create the interface and the attach helper**

Create `src/LocalScribe.Core/Audio/IDiagnosticSource.cs`:

```csharp
namespace LocalScribe.Core.Audio;

/// <summary>A capture source that can explain what it did (Tier 1 plan A, 2026-08-05).
/// ProcessLoopbackCapture has raised these lines since the Stage-1 spike - activation format
/// fallbacks, device-invalidated recovery - but the ONLY subscriber in the solution was
/// SpikeRunner/Program.cs:55, a console harness, so none of it was visible in the shipping app.
/// Deliberately separate from ICaptureSource: MicCaptureSource has nothing to say, and widening
/// the capture contract for one implementation would force an empty event onto every source and
/// every test double.</summary>
public interface IDiagnosticSource
{
    event Action<string>? Diagnostic;
}
```

Create `src/LocalScribe.Core/Audio/CaptureDiagnostics.cs`:

```csharp
namespace LocalScribe.Core.Audio;

/// <summary>Attaches a diagnostic sink to a capture source that has one (Tier 1 plan A,
/// 2026-08-05). Returns the SAME instance so a call site reads as a wrap:
/// <c>var s = CaptureDiagnostics.Attach(new ProcessLoopbackCapture(pid, clock), _diagnostic);</c>
/// Nothing is ever unsubscribed: the sink outlives every source (it is the process-wide log) and
/// the source is disposed with the leg.</summary>
public static class CaptureDiagnostics
{
    public static ICaptureSource Attach(ICaptureSource source, Action<string>? sink)
    {
        if (sink is not null && source is IDiagnosticSource diagnostic) diagnostic.Diagnostic += sink;
        return source;
    }
}
```

- [ ] **Step 4: Declare the interface on `ProcessLoopbackCapture` and run the capture test**

In `src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs`, change line 37 from
`public sealed class ProcessLoopbackCapture : ICaptureSource` to:

```csharp
public sealed class ProcessLoopbackCapture : ICaptureSource, IDiagnosticSource
```

and replace the `Diagnostic` event's doc comment (`:79`) with:

```csharp
    /// <summary>Best-effort diagnostics (activation fallback, recovery, capture errors). Subscribed
    /// by SpikeRunner and - since Tier 1 plan A, 2026-08-05 - by the app's diagnostic log, via
    /// WasapiCaptureSourceProvider's sink.</summary>
```

The event itself is unchanged: its signature is already exactly `IDiagnosticSource`'s.

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~CaptureDiagnosticsTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: **Passed! - Failed: 0, Passed: 3**.

- [ ] **Step 5: Thread the sink through the capture provider**

`WasapiCaptureSourceProvider`'s own doc comment calls it a Humble Object, and it is exercised by the
LiveRunner smoke rather than unit tests — the three lines below are the untested wiring the tested
`Attach` exists to make safe.

In `src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs`, add the field and parameter:

```csharp
    private readonly ICaptureDeviceEnumerator _devices;
    /// <summary>Diagnostic sink (Tier 1 plan A, 2026-08-05): attached to every remote source that
    /// can talk. Null in the pre-Stage-4 call sites and in tests - Attach then no-ops.</summary>
    private readonly Action<string>? _diagnostic;

    public WasapiCaptureSourceProvider(Func<Settings> settingsProvider, IAudioSessionScanner scanner,
        ICaptureDeviceEnumerator? deviceEnumerator = null, Action<string>? diagnostic = null)
    {
        _settings = settingsProvider;
        _scanner = scanner;
        _devices = deviceEnumerator ?? new WasapiCaptureDeviceEnumerator();
        _diagnostic = diagnostic;
    }
```

In BOTH `CreateRemote` overloads, replace the `ICaptureSource source = plan.Mode == ...` assignment
with the attached form (the ternary is unchanged; only the wrap is new):

```csharp
        ICaptureSource source = CaptureDiagnostics.Attach(
            plan.Mode == RemoteMode.PerProcess
                ? new ProcessLoopbackCapture(plan.Pid!.Value, clock)
                : ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock),
            _diagnostic);
```

The 2-argument convenience overload (`: this(() => settings, scanner)`) keeps compiling unchanged
because both new parameters are optional.

- [ ] **Step 6: Write the failing helper-exit tests**

Add to `tests/LocalScribe.Core.Tests/SherpaHelperDiariserTests.cs` — a private fake at the bottom of
the class and two facts (the file has no namespace and no `using Xunit;`; add
`using LocalScribe.Core.Diagnostics;` at the top):

```csharp
    /// <summary>Records diagnostic lines. Mirrors AppServiceFakes.FakeDiagnosticLog on the App
    /// side; duplicated here rather than shared because Core.Tests has no shared-fakes file (house
    /// convention: no cross-file test helper).</summary>
    private sealed class RecordingLog : IDiagnosticLog
    {
        public readonly List<(string Level, string Source, string Message)> Entries = new();
        public void Write(string level, string source, string message, string? detail = null)
            => Entries.Add((level, source, message));
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_helper_crash_is_logged_as_a_warning_naming_its_exit_code()
    {
        // Spec item T1-1 lists "helper process exits". Today a crashed diarizer surfaces only as a
        // dialog the user has already dismissed by the time they ask for help.
        var log = new RecordingLog();
        var engine = new SherpaHelperDiariser(new FakeHelper(3, "{\"progress\":0.1}"), log);

        await Assert.ThrowsAsync<DiarisationException>(
            () => engine.DiariseAsync(Req(), new Progress<double>(_ => { }), default));

        var entry = Assert.Single(log.Entries);
        Assert.Equal("warn", entry.Level);
        Assert.Equal("diarizer", entry.Source);
        Assert.Contains("code 3", entry.Message);
    }

    [Fact]
    public async Task A_clean_run_logs_at_debug_so_the_default_level_drops_it()
    {
        // A voiceprint backfill runs hundreds of these; at the default "info" level a clean exit
        // must not flood the file, and DiagnosticLog gates it out before it is ever queued.
        var log = new RecordingLog();
        var helper = new FakeHelper(0,
            "{\"segments\":[{\"startMs\":0,\"endMs\":1000,\"cluster\":0}],\"clusterCount\":2,\"method\":\"sherpa\"}");

        await new SherpaHelperDiariser(helper, log).DiariseAsync(Req(), new Progress<double>(_ => { }), default);

        Assert.Equal("debug", Assert.Single(log.Entries).Level);
    }
```

- [ ] **Step 7: Run them and confirm they fail**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SherpaHelperDiariserTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: FAIL to build — `error CS1729: 'SherpaHelperDiariser' does not contain a constructor that takes 2 arguments`.

- [ ] **Step 8: Log the helper exit codes**

In `src/LocalScribe.Core/Diarisation/SherpaHelperDiariser.cs`, add to the using block:

```csharp
using System.Globalization;
using LocalScribe.Core.Diagnostics;
```

Change the class declaration to:

```csharp
public sealed class SherpaHelperDiariser(IDiarisationHelper helper, IDiagnosticLog? log = null)
    : IDiarisationEngine, IEmbeddingEngine
```

In `DiariseAsync`, immediately after line 47's
`int exit = await helper.RunAsync(job, OnLine, ct);` and **BEFORE** the `if (error is not null)`
guard on line 49 — so the line is written on the SUCCESS path and the failure path alike; the two
`throw new DiarisationException` guards on `:49-53` are exactly where it must NOT go — add:

```csharp
        // Tier 1 plan A (2026-08-05, spec item T1-1: "helper process exits"). A clean exit is
        // DEBUG - dropped at the default "info" level, because a voiceprint backfill runs hundreds
        // of these - while a non-zero exit is a WARN: that is the shape a missing model file or a
        // native sherpa crash takes, and today it survives only as a dialog message.
        log?.Write(exit == 0 ? DiagnosticLevels.Debug : DiagnosticLevels.Warn, "diarizer",
            "Diarisation helper exited with code " + exit.ToString(CultureInfo.InvariantCulture),
            "source=" + request.Source);
```

In `EmbedAsync`, immediately after line 81's
`int exit = await helper.RunEmbedAsync(job, OnLine, ct);` and again BEFORE the
`if (error is not null)` guard on line 82, add:

```csharp
        // Same rule as DiariseAsync above (Tier 1 plan A): clean at debug, non-zero at warn.
        log?.Write(exit == 0 ? DiagnosticLevels.Debug : DiagnosticLevels.Warn, "diarizer",
            "Embed helper exited with code " + exit.ToString(CultureInfo.InvariantCulture),
            "job=embed");
```

- [ ] **Step 9: Wire both sinks in `CompositionRoot`**

In `src/LocalScribe.App/CompositionRoot.cs`, change the capture-provider argument inside the
`new SessionController(...)` call from
`new WasapiCaptureSourceProvider(current, scanner, deviceEnumerator),` to:

```csharp
            // Tier 1 plan A (2026-08-05): the per-process loopback's own diagnostics finally have
            // a subscriber in the app - activation fallbacks and device-invalidated recovery were
            // visible only to the SpikeRunner console harness before this. INFO, not debug: these
            // lines are rare and are exactly what a "the other side was not recorded" report needs.
            new WasapiCaptureSourceProvider(current, scanner, deviceEnumerator,
                diagnostic: m => log.Write(DiagnosticLevels.Info, "capture", m)),
```

and the diariser construction (`:138`) to:

```csharp
        var diarisation = new SherpaHelperDiariser(new ProcessDiarisationHelper(diarizerExe), log);
```

- [ ] **Step 10: Run both test classes and confirm they pass**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~CaptureDiagnosticsTests|FullyQualifiedName~SherpaHelperDiariserTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: all pass, including the pre-existing `SherpaHelperDiariserTests` facts unchanged.

- [ ] **Step 11: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Audio/IDiagnosticSource.cs src/LocalScribe.Core/Audio/CaptureDiagnostics.cs src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs src/LocalScribe.Core/Live/WasapiCaptureSourceProvider.cs src/LocalScribe.Core/Diarisation/SherpaHelperDiariser.cs src/LocalScribe.App/CompositionRoot.cs tests/LocalScribe.Core.Tests/CaptureDiagnosticsTests.cs tests/LocalScribe.Core.Tests/SherpaHelperDiariserTests.cs
git commit -m "feat(diagnostics): capture diagnostics and diariser helper exit codes reach the log"
```

---

## Task 10: `FlushAsync` on the exit paths

**Files:**
- Modify: `src/LocalScribe.App/App.xaml.cs:1132-1144` (`OnExit`), `src/LocalScribe.App/TrayIconHost.cs:20-49` (field + ctor), `:78-104` (the Exit handler), `:818-827` in `App.xaml.cs` (the tray construction)
- Test: `tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs` (add two facts)

**Interfaces:**
- Consumes: `IDiagnosticLog.FlushAsync(CancellationToken)` (Task 4), `App._log` (Task 5), `AppComposition.Log`.
- Produces: `TrayIconHost(..., Func<MainWindow> mainWindowFactory, IDiagnosticLog? log = null)` — a trailing optional parameter, so the existing 8-argument call site keeps compiling until it is updated in this task.

`Application.Current.Shutdown()` in the tray Exit handler is the only route into `App.OnExit`, so
`OnExit` is reached by every real exit — but it is a `void` method that cannot await. The tray handler
is genuinely `async`, so it awaits properly, and `OnExit` keeps a BOUNDED blocking flush as the
backstop for the other shutdown routes (consent decline, second-instance bail, and Plan B's
`SessionEnding`).

- [ ] **Step 1: Write the failing pins**

Add to `tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs`:

```csharp
    private static string Tray() => File.ReadAllText(RepoPaths.AppXaml("TrayIconHost.cs"));

    [Fact]
    public void OnExit_drains_the_diagnostic_queue_with_a_bounded_wait()
    {
        string app = App();
        Assert.Contains("_log?.FlushAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(2))", app);
    }

    [Fact]
    public void The_tray_exit_awaits_the_flush_before_shutting_down()
    {
        string tray = Tray();
        int flush = tray.IndexOf("_log?.FlushAsync", StringComparison.Ordinal);
        int shutdown = tray.IndexOf("Application.Current.Shutdown();", StringComparison.Ordinal);
        Assert.True(flush > 0, "the tray Exit handler must flush the diagnostic log");
        Assert.True(shutdown > flush, "the flush must be awaited BEFORE Shutdown()");
    }
```

- [ ] **Step 2: Run them and confirm they fail**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~DiagnosticsWiringTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: 2 failures — `Assert.Contains() Failure: Sub-string not found` and
`the tray Exit handler must flush the diagnostic log`.

- [ ] **Step 3: Flush in `App.OnExit`**

In `src/LocalScribe.App/App.xaml.cs`, add to `OnExit` immediately before `_tray?.Dispose();`:

```csharp
        // Tier 1 plan A (2026-08-05): drain the diagnostic queue so the last lines before an exit -
        // including the ones a crash-then-exit just wrote - reach disk. BOUNDED Wait, never an
        // unbounded one: the drain runs on TaskScheduler.Default and posts nothing back to this UI
        // thread, so it cannot deadlock today, and the 2 s ceiling means a future change that DID
        // capture context would cost a slow exit rather than a hung one. The tray Exit path awaits
        // the same flush properly; this is the backstop for every other route into OnExit.
        try { _log?.FlushAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(2)); } catch { }
```

- [ ] **Step 4: Flush in the tray Exit handler**

In `src/LocalScribe.App/TrayIconHost.cs`, add to the using block:

```csharp
using LocalScribe.Core.Diagnostics;
```

Add the field beside `_openExport`:

```csharp
    // Tier 1 plan A (2026-08-05): the tray is the app's ONLY Exit and its handler is genuinely
    // async, so the diagnostic flush can be awaited here rather than blocked on in App.OnExit.
    // Optional so the existing construction site and any future test double stay valid.
    private readonly IDiagnosticLog? _log;
```

Extend the constructor signature and the tuple assignment:

```csharp
    public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, StoragePaths paths,
        ISettingsService settingsService, WindowStateStore windowState,
        Action<string, string>? openExport,
        Func<MainWindow> mainWindowFactory,
        IDiagnosticLog? log = null)
```

```csharp
        (_session, _lines, _console, _paths, _settingsService, _windowState, _openExport, _mainWindowFactory, _log) =
            (session, lines, console, paths, settingsService, windowState, openExport, mainWindowFactory, log);
```

In the Exit menu item, insert immediately before `Application.Current.Shutdown();` (after the existing
`catch` block):

```csharp
            // Tier 1 plan A: a real await, on the app's only Exit, before the process starts
            // tearing down. App.OnExit keeps a bounded blocking flush as the backstop for the
            // other shutdown routes.
            try { await (_log?.FlushAsync(CancellationToken.None) ?? Task.CompletedTask); } catch { }
```

In `src/LocalScribe.App/App.xaml.cs`, extend the tray construction so the closing line reads:

```csharp
                })),
            log: comp.Log);
```

(that is: the existing `mainWindowFactory:` argument's closing `)))` gains a trailing comma and the
new named argument follows it).

- [ ] **Step 5: Run the pins and confirm they pass**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~DiagnosticsWiringTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: **Passed! - Failed: 0, Passed: 5**.

- [ ] **Step 6: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/App.xaml.cs src/LocalScribe.App/TrayIconHost.cs tests/LocalScribe.App.Tests/DiagnosticsWiringTests.cs
git commit -m "feat(diagnostics): flush the diagnostic log on both exit paths"
```

---

## Task 11: Settings — version line, "Open diagnostics folder", "Copy last error"

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs:1-15` (usings), `:154-167` (fields), `:190-257` (ctor), `:259-263` (command properties), `src/LocalScribe.App/SettingsPage.xaml:410-421`, `src/LocalScribe.App/App.xaml.cs:252-284`
- Test: `tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs:30-54` (make the `openFolder` fake CAPTURING) plus five new facts

**Interfaces:**
- Consumes: `AppComposition.BuildInfo`, `AppComposition.Log` (Task 5), `DiagnosticLog.LastError` (Task 4), `StoragePaths.DiagnosticsDir` (Task 2), the existing `Action<string> _openFolder` seam and the existing optional `StoragePaths? paths` ctor parameter.
- Produces on `SettingsPageViewModel`: three new trailing optional ctor parameters
  `string? buildInfo = null, Func<DiagnosticEntry?>? lastError = null, Action<string>? copyToClipboard = null`;
  and the members `public string AppVersionLine { get; }`, `public string LastErrorText { get; }`,
  `public string NoErrorsNote { get; }`, `public IRelayCommand OpenDiagnosticsFolderCommand { get; }`,
  `public IRelayCommand CopyLastErrorCommand { get; }`.

**Use the PINNED paths, not live settings.** The storage root is restart-pinned in
`CompositionRoot.cs:66` (`// once; restart-required`) and the log writes under THAT root for the life
of the process, so re-resolving from `_settings.Current` the way `OpenMcpAuditFolderCommand` does
would open an empty folder under a just-changed, not-yet-effective root. The injected optional
`StoragePaths? paths` parameter (`:195`, wired from `App.xaml.cs:266` as `paths: comp.Paths`) is the
right source, and it is null in most unit tests, so the command must degrade gracefully.

Nothing in the app displays a version today — this is net-new UI.

- [ ] **Step 1: Make the test fake CAPTURING and write the failing tests**

In `tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs`, add
`using LocalScribe.Core.Diagnostics;` to the using block, add two collecting fields beside
`_pickResult`:

```csharp
    // Tier 1 plan A (2026-08-05): CAPTURING, not discarding. MakeVm passed `openFolder: _ => { }`
    // until this round, which is why no test ever asserted anything about a folder command -
    // including the OpenMcpAuditFolderCommand this one is modelled on.
    private readonly List<string> _openedFolders = new();
    private readonly List<string> _copied = new();
```

and replace `MakeVm` with:

```csharp
    private SettingsPageViewModel MakeVm(Settings? initial = null,
        Func<string?>? assistantHelperProbe = null,
        StoragePaths? paths = null,
        string? buildInfo = null,
        Func<DiagnosticEntry?>? lastError = null)
    {
        initial ??= new Settings();
        // Hermetic isolation (review finding): the VM ctor unconditionally runs LoadMcpAsync,
        // which reads mcp/consent.json and matters/matters.json off StorageRoot. A default
        // Settings().StorageRoot resolves to the REAL %USERPROFILE%/LocalScribe, so an unrelated
        // test that doesn't care about StorageRoot would otherwise touch the developer's real
        // legal-transcript matter index (same class of machine-dependence AssistantHelperLocator.
        // FindExe is guarded against elsewhere). Give every test an isolated temp root unless it
        // deliberately picked its own (e.g. the OneDrive sync-provider test).
        if (initial.StorageRoot == new Settings().StorageRoot)
            initial = initial with { StorageRoot = Path.Combine(_root, "storage") };
        _settings = new FakeSettingsService(initial);
        var maintenance = new Services.MaintenanceService(
            new StoragePaths(Path.Combine(_root, "storage")), _settings, new FakeRecycleBin(),
            TimeProvider.System);
        return new SettingsPageViewModel(_settings, maintenance, _launch,
            pickFolder: () => _pickResult, openFolder: _openedFolders.Add, _errors,
            dispatch: a => a(), _devices, modelsRoot: Path.Combine(_root, "models"),
            // Deterministic default (Task 5 review finding 2): without this, an unspecified probe
            // falls through to the real AssistantHelperLocator.FindExe() and the real filesystem
            // (including the repo tools\assistant\ dev fallback), making the suite machine-dependent.
            assistantHelperProbe: assistantHelperProbe ?? (() => null),
            paths: paths,
            buildInfo: buildInfo,
            lastError: lastError,
            copyToClipboard: _copied.Add);
    }
```

Then add these facts:

```csharp
    [Fact]
    public void Open_diagnostics_folder_creates_and_opens_the_PINNED_diagnostics_dir()
    {
        // Deliberately NOT _settings.Current.StorageRoot (which OpenMcpAuditFolderCommand uses):
        // the log writes under the root CompositionRoot pinned at startup, so after a storage-root
        // change the live value points at a folder the log has never written to.
        var pinned = new StoragePaths(Path.Combine(_root, "pinned"));
        var vm = MakeVm(paths: pinned);

        vm.OpenDiagnosticsFolderCommand.Execute(null);

        Assert.True(Directory.Exists(pinned.DiagnosticsDir));
        Assert.Equal(new[] { pinned.DiagnosticsDir }, _openedFolders);
    }

    [Fact]
    public void Open_diagnostics_folder_is_inert_when_no_paths_were_injected()
    {
        // paths is an OPTIONAL ctor parameter and null in most unit tests; the command must
        // degrade to a no-op rather than throw a NullReferenceException at the user.
        MakeVm().OpenDiagnosticsFolderCommand.Execute(null);
        Assert.Empty(_openedFolders);
    }

    [Fact]
    public void The_version_line_shows_the_build_stamp()
    {
        Assert.Equal("LocalScribe 0.9.0+g1628935", MakeVm(buildInfo: "0.9.0+g1628935").AppVersionLine);
        // No stamp injected (unit tests, and any future composition that forgets it): say so
        // rather than render "LocalScribe " with a blank where the version should be.
        Assert.Contains("development", MakeVm().AppVersionLine);
    }

    [Fact]
    public void Copy_last_error_copies_the_build_stamp_together_with_the_recorded_error()
    {
        var entry = new DiagnosticEntry(new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero),
            "error", "dispatcher", "Unhandled dispatcher exception",
            "System.IO.IOException: [redacted]");
        var vm = MakeVm(buildInfo: "0.9.0+g1628935", lastError: () => entry);

        vm.CopyLastErrorCommand.Execute(null);

        string copied = Assert.Single(_copied);
        Assert.Contains("0.9.0+g1628935", copied);            // support needs the build first
        Assert.Contains("2026-08-05T09:30:00", copied);
        Assert.Contains("dispatcher: Unhandled dispatcher exception", copied);
        Assert.Contains("[redacted]", copied);                 // already redacted by DiagnosticLog
    }

    [Fact]
    public void Copy_last_error_says_so_when_nothing_has_failed()
    {
        MakeVm(buildInfo: "0.9.0").CopyLastErrorCommand.Execute(null);
        Assert.Contains("No errors", Assert.Single(_copied));
    }
```

- [ ] **Step 2: Run them and confirm they fail**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SettingsPageViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: FAIL to build — `error CS1739: The best overload for 'SettingsPageViewModel' does not have a parameter named 'buildInfo'`.

- [ ] **Step 3: Extend the view model**

In `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs`, add to the using block:

```csharp
using System.Globalization;
using LocalScribe.Core.Diagnostics;
```

Add the three fields beside `_engineBusy`:

```csharp
    // --- Diagnostics (Tier 1 plan A, 2026-08-05). All optional: a composition that does not pass
    // them gets an inert About line and a no-op folder command rather than a half-wired one.
    private readonly string? _buildInfo;
    private readonly Func<DiagnosticEntry?>? _lastError;
    private readonly Action<string> _copyToClipboard;
```

Extend the constructor signature (append after `Func<string?>? engineBusy = null`):

```csharp
        Func<string?>? engineBusy = null,
        string? buildInfo = null, Func<DiagnosticEntry?>? lastError = null,
        Action<string>? copyToClipboard = null)
```

Add to the constructor body, beside the other assignments:

```csharp
        (_buildInfo, _lastError) = (buildInfo, lastError);
        // A SEPARATE clipboard seam from copyMcpSnippetToClipboard, which is named for its one
        // call site and is pinned under that name by the MCP tests. Both are the same
        // Clipboard.SetText in App.xaml.cs; renaming the older one would churn tests for nothing.
        _copyToClipboard = copyToClipboard ?? (_ => { });
```

Add the two commands after the `OpenMcpAuditFolderCommand` assignment:

```csharp
        OpenDiagnosticsFolderCommand = new RelayCommand(() =>
        {
            // The PINNED paths, deliberately UNLIKE OpenMcpAuditFolderCommand above:
            // CompositionRoot builds StoragePaths exactly once ("once; restart-required") and the
            // diagnostic log writes under THAT root for the life of the process, so re-resolving
            // from _settings.Current would open an empty folder under a storage root that has been
            // chosen but not yet restarted into. _paths is optional and null in most unit tests -
            // degrade to a no-op rather than throw at the user.
            if (_paths is null) return;
            Directory.CreateDirectory(_paths.DiagnosticsDir);
            _openFolder(_paths.DiagnosticsDir);
        });
        CopyLastErrorCommand = new RelayCommand(() => _copyToClipboard(LastErrorText));
```

Add the public surface beside the other command properties:

```csharp
    // ---------- Diagnostics (Tier 1 plan A, 2026-08-05) ----------
    public IRelayCommand OpenDiagnosticsFolderCommand { get; }
    public IRelayCommand CopyLastErrorCommand { get; }

    /// <summary>The About line. BuildInfo, not AppVersion: the SHA is the whole point of showing a
    /// version to a user who is about to report something. Nothing in the app displayed any
    /// version before this round.</summary>
    public string AppVersionLine => "LocalScribe " + (_buildInfo ?? "(development build)");

    public string NoErrorsNote { get; } = "No errors have been recorded since LocalScribe started.";

    /// <summary>Support paste-in text: the build stamp plus the most recent error the diagnostic
    /// log recorded this run. Composed HERE rather than in App.xaml.cs so it is testable. The entry
    /// is ALREADY redacted - DiagnosticLog applies Settings.Logging.IncludeTranscriptText before an
    /// entry is stored - so nothing privileged can reach the clipboard by this route.</summary>
    public string LastErrorText
    {
        get
        {
            if (_lastError?.Invoke() is not { } e)
                return AppVersionLine + Environment.NewLine + NoErrorsNote;
            return AppVersionLine + Environment.NewLine
                + e.TsUtc.ToString("O", CultureInfo.InvariantCulture) + " [" + e.Level + "] "
                + e.Source + ": " + e.Message
                + (string.IsNullOrEmpty(e.Detail) ? "" : Environment.NewLine + e.Detail);
        }
    }
```

- [ ] **Step 4: Run the tests and confirm they pass**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SettingsPageViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\tier1a\
```

Expected: every `SettingsPageViewModelTests` fact passes, including the pre-existing ones and
`Vm_exposes_no_dropped_setting_surfaces` (the new property names contain none of
`RecordingIndicator` / `Hotkey` / `AutoDetect`).

- [ ] **Step 5: Add the UI to the "App" card**

In `src/LocalScribe.App/SettingsPage.xaml`, inside the LAST `ui:Card` (the one whose header is
`"App"`), after the existing Timestamps `StackPanel`, add:

```xml
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Version" Style="{StaticResource FieldLabel}" />
                        <TextBlock Text="{Binding AppVersionLine, Mode=OneWay}"
                                   VerticalAlignment="Center" />
                    </StackPanel>
                    <TextBlock Text="Diagnostics are written to a diagnostics folder under the storage root. They never contain transcript text unless you turn that on in settings.json."
                               Style="{StaticResource Note}" TextWrapping="Wrap" Margin="0,8,0,0" />
                    <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
                        <Button Content="Open diagnostics folder"
                                Command="{Binding OpenDiagnosticsFolderCommand}" Margin="0,0,8,0" />
                        <Button Content="Copy last error" Command="{Binding CopyLastErrorCommand}" />
                    </StackPanel>
```

Only `FieldRow`, `FieldLabel` and `Note` are used, all of which
`XamlHygieneTests.SharedDictionary_DeclaresRequiredKeys` already pins. No hex colours are introduced
(`ShippedXaml_HasNoDisallowedHardcodedBrushes`), and the root `ScrollViewer` already carries the
inheritable-foreground marker.

- [ ] **Step 6: Wire the three arguments in `App.xaml.cs`**

In the `new ViewModels.SettingsPageViewModel(...)` call, replace the final argument line
`copyMcpSnippetToClipboard: text => Clipboard.SetText(text));` with:

```csharp
            copyMcpSnippetToClipboard: text => Clipboard.SetText(text),
            // Tier 1 plan A (2026-08-05): the About line and the support copy. comp.Log is the
            // CONCRETE DiagnosticLog here precisely so LastError is reachable - the write-only
            // consumers all take IDiagnosticLog.
            buildInfo: comp.BuildInfo,
            lastError: () => comp.Log.LastError,
            copyToClipboard: text => Clipboard.SetText(text));
```

- [ ] **Step 7: Run the full App suite (no isolated output path)**

```powershell
cd F:\LocalScribe
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo
```

Expected: green. This run must NOT use the isolated `BaseOutputPath` — `XamlHygieneTests` walks up
from `AppContext.BaseDirectory` to find `.git`, and the Temp path sits outside the repo (5 false
failures).

- [ ] **Step 8: Commit**

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs src/LocalScribe.App/SettingsPage.xaml src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs
git commit -m "feat(diagnostics): Settings shows the build and hands over the last error"
```

---

## Task 12: Whole-round verification

**Files:** none changed unless a check fails.

**Interfaces:** none.

- [ ] **Step 1: Whole-suite run**

```powershell
cd F:\LocalScribe
dotnet test LocalScribe.slnx --filter "Category!=Fixture"
```

Expected: **Core 1186 + 24 new = 1210** (8 redaction + 10 log + 3 capture + 1 paths + 2 diariser),
**App 984 + 32 new = 1016** (5 version + 5 wiring + 5 recorder + 5 session + 2 tray + 4 infobar +
5 settings + 1 startup-orchestrator), **Mcp 6** = **2232**, zero failures.
Judge by NAME: any failing name that is not one of this round's new tests is a regression from this
round. The counts are a sanity check only.

- [ ] **Step 2: Prove the log actually appears, by hand**

```powershell
cd F:\LocalScribe
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo
```

Then launch the built `LocalScribe.App.exe`, let it reach the tray, open Settings, confirm the
version line reads `LocalScribe 0.9.0+g<sha>`, click **Open diagnostics folder**, and confirm the
folder contains `diag-<yyyyMM>.jsonl` whose first line is the `LocalScribe started` entry with
`"detail":"build=0.9.0+g<sha>"`. Exit through the tray. If the build reports MSB3027 the app is
already running: close that one process, never blanket-kill.

- [ ] **Step 3: Prove the redaction promise on the real file**

```powershell
cd F:\LocalScribe
$dir = Join-Path $env:USERPROFILE "LocalScribe\diagnostics"
Select-String -Path (Join-Path $dir "*.jsonl") -Pattern "<<" | Measure-Object | Select-Object -Property Count
```

Expected: `Count : 0`. A surviving `<<` marker means a line reached disk without passing through
`DiagnosticRedaction.Apply`, which is the one defect in this round that would matter legally.

Then confirm the positive half — that `IUiErrorReporter.Info` messages are actually being marked,
not merely marker-free:

```powershell
cd F:\LocalScribe
$dir = Join-Path $env:USERPROFILE "LocalScribe\diagnostics"
Select-String -Path (Join-Path $dir "*.jsonl") -Pattern '"source":"ui"' | Select-Object -First 5
```

Expected: every `"source":"ui"` line whose `"level"` is `"info"` reads `"message":"[redacted]"` at
the default setting. That is BY DESIGN, not a bug: those strings are composed by view models that
routinely interpolate a participant name, a session title or an export path
(`MetadataEditorViewModel.cs:369`, `ExportDialogViewModel.cs:197`). `"level":"error"` lines keep
their fixed-literal context ("Export", "Delete session") in full.

- [ ] **Step 4: Whole-branch ASCII byte-scan**

```powershell
cd F:\LocalScribe
$files = git diff --name-only master...HEAD
foreach ($f in $files) {
  if (Test-Path $f) {
    $b = [IO.File]::ReadAllBytes($f)
    $n = ($b | Where-Object { $_ -gt 127 }).Count
    if ($n -gt 0) { "NON-ASCII ($n bytes): $f" }
  }
}
"scan complete"
```

Expected: only `scan complete`. Markdown docs under `docs/` are exempt; **source files are not**. A
`.cs`, `.xaml` or `.props` file reporting non-ASCII means an escape was converted to a literal glyph —
restore the `\uXXXX` form.

- [ ] **Step 5: Confirm line endings survived**

```powershell
cd F:\LocalScribe
git diff --stat master...HEAD
git diff --check master...HEAD
```

Expected: no whitespace errors, and a plausible changed-file list — no file showing as wholly
rewritten, which would indicate a CRLF/LF flip.

- [ ] **Step 6: Confirm the source-drop fallback still holds**

```powershell
cd F:\LocalScribe
# Check each guard SEPARATELY. A single combined pattern with a match COUNT is brittle here -
# the props file documents each guard in a comment as well as using it, so the combined pattern
# matches seven lines, not four, and a reader "correcting" the count would be chasing a phantom.
foreach ($g in "IncludeSourceRevisionInInformationalVersion", "ContinueOnError", "IgnoreExitCode", "Exists\(") {
    $hits = @(Select-String -Path "src\Directory.Build.props" -Pattern $g)
    "{0,-46} {1}" -f $g, $(if ($hits.Count -gt 0) { "PRESENT ($($hits.Count) line(s))" } else { "*** MISSING ***" })
}
```

Expected: all four report PRESENT. Counts per guard will vary (each is both used and documented) and
do not matter - presence does. Without these four a machine with no `git`, or a zipped source drop,
either fails the build or stamps a 40-character SHA.

- [ ] **Step 7: Commit anything the checks changed**

```bash
cd F:/LocalScribe
git status --short
```

Expected: clean. If a check forced a fix, stage the named files and commit with a
`fix(diagnostics): ...` message.

---

## Post-Implementation

Once all 12 tasks are green:

1. **Request code review** — use `superpowers:requesting-code-review`.
2. **Merge before Plans B, C and D start.** Those three all write into `IDiagnosticLog`, and a seam
   that is still moving cannot be consumed by three parallel branches. This is the one plan in the
   round with that constraint.
3. **Smoke checklist for the user** (the spec's section 7 items assigned to Plan A):
   - Record a short session, stop it, exit through the tray. The month's `diag-*.jsonl` should carry
     `LocalScribe started`, `State Recording`, `State Finalizing`, `State Idle` and
     `Finalize completed`, in that order, each with the session id.
   - Turn `logging.level` to `"debug"` in `%APPDATA%\LocalScribe\settings.json` while the app is
     running, save, then trigger any command. The next lines should include debug entries **without
     a restart** — that proves the level is read live rather than captured at startup.
   - Force a failure (rename the models folder, then Start). The InfoBar should show the message AND
     the file should carry an `"error"` line with the exception type and stack. Then click
     **Copy last error** in Settings and paste it somewhere: it must begin with the build stamp.
   - Confirm the diagnostics folder is under the storage root, is deletable while the app runs
     (`FileShare.Delete`), and that the app keeps writing afterwards.
   - Grep the whole folder for any fragment of what was said on the call. There must be none.
   - Grep it for the IDENTIFIERS too, not only the utterances — the matter name, a participant's
     name from the roster, a custom vocabulary term, and the session title as it appears in an
     export filename. Those are what `IUiErrorReporter.Info` call sites interpolate
     (`MetadataEditorViewModel.cs:369`, `ExportDialogViewModel.cs:197`,
     `VocabularyEditorViewModel.cs:71,90`), and a name is not a fragment of speech — the utterance
     grep above would sail straight past one. Every `"source":"ui"` / `"source":"startup"` info line
     should read `"message":"[redacted]"` at the default setting.
4. **What Plans B, C and D inherit:** `_log.Write(level, source, message, detail)` with the source
   tags already in use — `app`, `dispatcher`, `session`, `capture`, `startup`, `ui`, `diarizer`. New
   subsystems add their own short tag; nobody changes the signature.
   - **The instance is `comp.Log`** (SHARED-CONTRACT section 3a), or an `IDiagnosticLog` parameter
     threaded down from it. There is exactly one, and no plan may refer to "whatever Plan A called
     its local" — a local in `CompositionRoot.Build()` is not in scope in `App.OnStartup`.
   - **Wrap caller-composed text in `DiagnosticRedaction.Mark(...)`.** Fixed literals and
     `key=value` diagnostics go bare; anything that interpolates a name, a title, a path or
     transcript text is marked, so `Settings.Logging.IncludeTranscriptText` actually governs it.
     `DiagnosticRedaction.ForException` already marks every exception message for you.
