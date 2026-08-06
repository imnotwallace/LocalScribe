# Tier 1D: Reachability and shipping - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make failures on the two evidentiary actions visible where the user is looking (T1-5), let a solicitor get quoted text out of a transcript with an attributable citation (T1-9), and make the product obtainable and updatable by a build script, a signed installer, CI and an in-app component downloader that keeps the zero-network property provable by grep (T1-10).

**Architecture:** Three independent seams. (1) Notices grow a `NoticeSeverity` and every modal gains a dialog-local InfoBar bound to a `StatusMessage`/`StatusIsError` pair - the proven `SplitSpeakersWindow` shape - so a failure lands in the window the user is actually looking at; `Application.MainWindow` is assigned for real so `CenterOwner` dialogs centre on the shell rather than on the recording pill. (2) Copy payloads are composed by a pure `TranscriptCitation` in Core and written to the clipboard by the read-view window; row prose keeps its existing virtualised `TextBlock`/`SegmentText` rendering untouched. (3) Every byte that comes off the network comes off it inside `LocalScribe.Fetch.exe`, a stdio child spawned on explicit user action, following the `ProcessDiarisationHelper` pattern - `LocalScribe.App` and `LocalScribe.Core` stay at zero network matches and a test pins that.

**THE BOTH-SURFACES RULE (Tasks 3, 4, 5 and 8 all obey it, without exception).** A dialog-local
InfoBar is an ADDITION to the shell reporter, never a replacement for it. Every failure path that
gains a `ShowStatus(..., isError: true)` keeps its `IUiErrorReporter.Report(context, ex)` call as
well, and the read view's two child dialogs get an adapter that TEES rather than one that
redirects. Two reasons, both load-bearing: a dialog-local bar dies with the dialog, so a failure
the user wants to report afterwards would be unrecoverable; and Plan A attaches the diagnostic log
behind `InfoBarErrorReporter`, so anything that bypasses the shell reporter is never recorded at
all. The correction and speaker-reassign dialogs are the read view's evidentiary WRITE paths - a
failed correction that leaves no trace anywhere is the worst version of this bug, not the mildest.

**Tech Stack:** C# / .NET 10, WPF (+ Wpf.Ui 4.0.3), CommunityToolkit.Mvvm 8.4.0, xUnit 2.9.3, PowerShell 7, Velopack, GitHub Actions.

## Amendments in execution (2026-08-06/07)

Recorded as they were found, per the standing ruling that plan-vs-tree disagreements are fixed
and amended without asking. Branch: `feat/tier1d-reachability-2026-08-06`, cut from `master` at
`6ea4801` (Plans A, B **and** C merged - this plan was written against a master carrying only A).

- **Every App-layer line anchor in this plan is STALE** and was re-anchored by CONTENT. Measured
  drift `7fbfc79` -> `6ea4801`: `ReadViewWindow.xaml.cs` 962->1040, `App.xaml.cs` 1248->1324,
  `ReadViewViewModel.cs` 1061->1104, `SessionViewModel.cs` 415->457,
  `ExportDialogViewModel.cs` 229->235, `ExportDialog.xaml` 63->65. Concretely: Task 2's dialog
  `Owner` sites are :463/:554/:796 (not :390/:481/:723) and `TrayIconHost.OpenMainWindow` is
  :164 (not :136); Task 3's `_isBusy` is :52, `OnIsBusyChanged` :117, `ExportAsync` :122 and the
  button row :60-63.
- **Task 3 Step 1, cancellation fact: VACUOUS AS WRITTEN.** See the inline note at that fact.
- **Baseline.** Core 1329 / App 1093 / Mcp 6 = **2428** on `6ea4801`, not the 2251 in Global
  Constraints. A full `--no-incremental` build emits **7** unique CS8602, all in
  `DocxRendererTests` (a build echoes each warning twice, which is where "14" came from).
- **Task 13's build.ps1 NEVER BUNDLES FFMPEG.** Found by running the script and looking at the
  output: the packaged app had no `ffmpeg\` directory, so `FfmpegLocator` returns null on every
  installed machine and Import is permanently greyed out. That is the exact shipped-to-a-stranger
  failure the 2026-08-06 packaging design note was written to prevent - decision 1 says to bundle
  it and the plan's script simply has no such step. Added as step 8b (127.5 MB, `ffplay.exe`
  excluded, `LICENSE.txt` kept). A green build said nothing about this.
- **Task 13's stray-file gate is too strict.** It failed on `onnxruntime.lib` and
  `onnxruntime_providers_shared.lib` - two 2 KB LINK-TIME import libraries the ORT package copies
  to output. Measured: the same publish emitted ZERO loose `.dll` files, so
  `IncludeNativeLibrariesForSelfExtract` had plainly worked. Now excludes `.lib` as well as
  `.pdb`.
- **Task 13's build.ps1 comment names `PackableVersion`, which its own `ShippingScriptTests`
  forbids.** Same self-contradiction class as the zero-network comment rule. Reworded.
- **build.ps1 needs `-ModelsDir` / `-FfmpegDir`** (defaulting to `LOCALSCRIBE_MODELS` /
  `LOCALSCRIBE_FFMPEG`). A worktree has no `models\` of its own, so the build died at step 8 on
  nine "missing" files that were all present a directory away.
- **`ComponentPin` had no `License` field** (Tasks 11/12), but the design note's decision 5
  requires the licence to surface in the UI at download time - "shipping Gemma weights silently is
  a licensing question, not a technical one". Added, written by `fetch-models.ps1`, shown per row.
- **The published-layout test is missing from the plan entirely.** The design note calls it "the
  deliverable, not the downloader" (decision 4). Added as `PublishedLayoutTests`, and it copies the
  published tree OUT of the repo first - probing it in place would find `LocalScribe.slnx` two
  levels up and the walk-up would rescue every miss.
- **FOUR** known flakes now, not two or three: the plan's two, plus
  `ProcessAssistantHelperTests.Cancel_kills_the_stub_promptly`, plus
  `SessionControllerTests.Faulted_stop_never_pads_retained_audio` (observed once under concurrent
  load 2026-08-07, passed in isolation and on the next full run; this branch touches nothing under
  `Live/`). Original note follows:
- **Three** known flakes were expected, not two: the plan's two plus
  `ProcessAssistantHelperTests.Cancel_kills_the_stub_promptly`. Note also that the plan spells the
  second one `..._the_current_matter_not_the_stale_one`; the test on `master` is
  `MetadataEditorViewModelTests.Delete_after_editor_retag_decrements_the_one`.

## Global Constraints

Copied verbatim from the shared contract, section 8:

- **Build/test:** `dotnet build` / `dotnet test` against `F:\LocalScribe\LocalScribe.slnx`. A running
  `LocalScribe.App.exe` locks `Core.dll` -> `MSB3027`. Close it; **never blanket-kill processes** -
  target the specific PID.
- **Test baseline.** This plan branches from a `master` that already has **Plan A merged**, so the
  pre-Plan-A figure (Core 1186 / App 984 / Mcp 6 = 2176, measured 2026-08-05) is history. Plan A
  added 3 Core and 4 App test files, measuring Core **1220** / App **1025** / Mcp **6** = **2251** on
  its branch tip. **Re-measure at your own branch point rather than trusting either number.**
  **Judge regressions by failing test NAME, never by count** - and note that two App tests are
  pre-existing flaky under concurrent-assembly load, pass in isolation and are byte-identical to
  `master`: `AssistantQaServiceTests.Dispose_racing_an_in_flight_ask_cancels_it_and_persists_nothing`
  and
  `MetadataEditorViewModelTests.Delete_after_editor_retag_decrements_the_current_matter_not_the_stale_one`.
  Never "fix" a passing suite to match a predicted count. Fixture-gated tests
  (`Category=Fixture`) need model weights and private corpora and are excluded.
- **ASCII source files.** Non-ASCII in string literals MUST be `\u` escapes; Fluent glyphs follow
  `TrayIconHost.cs:188-191`. The Edit tool silently converts escapes to literal glyphs - byte-scan
  every touched file before committing (zero bytes > 127, CRLF intact).
- **Stage files by name.** Never `git add -A` / `git add .` / `git commit -a`, never `git clean` -
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
- **Shared contract:** `docs/superpowers/specs/2026-08-05-tier1-shared-contract.md`, FIXED and
  **created by Plan A** (`2026-08-05-tier1a-diagnosability.md`), which must merge first. This plan
  touches it only through section 5's `InfoBarErrorReporter` / `TrayNoticeReporter` optional log
  sink - **preserve those `log?.Write(...)` calls verbatim** when editing either reporter.

Additionally, specific to this plan:

- **Branch:** `feat/tier1d-tier-d-reachability-and-shipping-2026-08-05`.
- **Depends on Plan A** (`2026-08-05-tier1a-diagnosability.md`) for `IDiagnosticLog` and for
  `src/Directory.Build.props`. Task 13 **READS** that props file's `<Version>` element; it must not
  modify or recreate it (see Task 13 Step 3, "Confirm the version source - and change NOTHING"). If Plan
  A has landed, `InfoBarErrorReporter`'s primary constructor already reads
  `(Action<Action> dispatch, IDiagnosticLog? log = null)` and its `Report`/`Info` bodies already
  carry `log?.Write(...)` calls - **preserve them verbatim**. If Plan A has not landed there is no
  `log` parameter and no such call. Task 1 says exactly what to do in each case.
- **THE ZERO-NETWORK GREP IS A PRODUCT PROPERTY, NOT A STYLE RULE.** A grep for
  `System.Net|HttpClient|Socket|WebRequest|Dns` over `src/LocalScribe.App` and `src/LocalScribe.Core`
  returns zero matches today (obj/ and bin/ excluded - the SDK's generated `GlobalUsings.g.cs`
  contains `global using System.Net.Http;` and is not source). Task 9 pins it with a test.
  **The pattern matches COMMENTS too.** Do not write the words `HttpClient`, `System.Net`, `Socket`,
  `WebRequest`, `Dns` or `UpdateManager` in any comment or string under those two projects, not even
  to say the code does not use them. Say "the network stack", "the fetch helper" or "Velopack's
  updater type" instead. This bites Tasks 10, 11, 12 **and 13** - Task 13 adds a comment to
  `App.xaml.cs`, which is inside the scanned tree, and the natural wording for it names the updater
  type outright.
- **NEVER use an isolated `BaseOutputPath`.** An earlier draft appended
  `-p:BaseOutputPath=<Temp>\localscribe-isobin\tier1d\` to filtered runs so a running
  `LocalScribe.App.exe` could not cause `MSB3027`, carving out the filters that read repo source.
  **That policy is withdrawn and the flag is removed from every command in this plan** - the
  carve-out was too easy to get wrong, and getting it wrong fails tests for a reason that looks
  nothing like its cause. `RepoPaths.SolutionRoot()` walks up from `AppContext.BaseDirectory`
  looking for `.git` (`XamlHygieneTests.cs:14-23`), so a Temp output path outside the repo makes
  every `RepoPaths`-anchored test - `XamlHygieneTests`, `ShellOwnerWiringTests`,
  `NoNetworkInAppOrCoreTests`, `ShippingScriptTests` - fail outright or validate the wrong tree
  (MEASURED 2026-08-05: the flag alone fails all 7 `XamlHygieneTests`). If you hit `MSB3027`, close
  the one running `LocalScribe.App.exe` - never blanket-kill processes.
- **No STA/dispatcher harness exists in `tests/LocalScribe.App.Tests`.** Window code-behind is
  permanently untestable here. Every decidable piece goes in a WPF-free ViewModel or static helper;
  where a task genuinely lands in code-behind, it says so and pins the wiring with a source-text
  assertion in the `XamlHygieneTests.AppIcon_ExistsAndIsWiredInCsproj` style instead of pretending.
- **XAML hygiene is test-enforced.** `XamlHygieneTests` rejects any `#RRGGBB` literal under
  `src/LocalScribe.App` and requires `TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}"`
  on every window/page root. Only these shared keys exist: `MutedText`, `WarningText`, `FieldLabel`,
  `FieldRow`, `Note`, `SectionCard`, `PillButton`, `PillToggleButton`.
- **`ui:InfoBar` (Wpf.Ui 4.0.3) has no `Closed` CLR event and its `Severity` is an enum DP that
  cannot bind a bool.** New dialog-local bars therefore use `IsClosable="False"`,
  `IsOpen="{Binding HasStatus, Mode=OneWay}"` and drive `Severity` from a `Style` + `DataTrigger` -
  the `SplitSpeakersWindow.xaml:83-100` shape. Only MainWindow's dismissible queue-backed bar uses
  the `DependencyPropertyDescriptor` hook.
- **`[ObservableProperty]` equality-gates a same-value set.** A repeated identical notice never
  re-raises `PropertyChanged`. Task 6 depends on knowing this.

---

## File Structure

**Created:**

- `src/LocalScribe.App/Services/NoticeSeverity.cs` - the four-state level a queued shell notice
  carries; the only thing that lets `SyncInfoBar` stop rendering successes red.
- `src/LocalScribe.Core/Projection/TranscriptCitation.cs` - pure composer for both clipboard
  payloads; the single place the citation string shape is defined.
- `src/LocalScribe.App/Services/IComponentFetchHelper.cs` - the process-boundary seam for the
  download child, plus the typed stdout line records; the whole reason the fetch client is testable.
- `src/LocalScribe.App/Services/ComponentFetchClient.cs` - parses the child's JSONL, reports
  progress, throws on error; contains no process code so it is unit-testable over a fake helper.
- `src/LocalScribe.App/Services/ProcessComponentFetchHelper.cs` - the humble Process object that
  spawns `LocalScribe.Fetch.exe`; not unit-tested, exactly like `ProcessDiarisationHelper`.
- `src/LocalScribe.App/Services/ComponentCatalog.cs` - loads the machine-derived
  `models/component-manifest.json` pin list; owns nothing but deserialisation.
- `src/LocalScribe.App/Services/ComponentProbe.cs` - turns the existing availability probes into
  installed/missing rows; the only place that knows how a component is detected.
- `src/LocalScribe.App/ViewModels/ComponentsPanelViewModel.cs` - WPF-free VM behind the Settings
  "Components" card: rows, download command, progress, cancellation.
- `src/LocalScribe.Fetch/LocalScribe.Fetch.csproj` + `src/LocalScribe.Fetch/Program.cs` - the ONLY
  project in the solution permitted to touch the network; a stdio child with one job per run.
- `build.ps1` - the whole publish in the required order, with the `tools/verify-*.ps1` guards as
  gates and Velopack packaging; degrades to unsigned with a loud warning.
- `.github/workflows/ci.yml` - `dotnet build` + the model-free suite on push/PR, plus a manual
  fixture job.
- `tests/LocalScribe.App.Tests/NoticeSeverityRoutingTests.cs`
- `tests/LocalScribe.App.Tests/ShellOwnerWiringTests.cs`
- `tests/LocalScribe.App.Tests/ExportDialogStatusTests.cs`
- `tests/LocalScribe.App.Tests/DialogLocalStatusTests.cs`
- `tests/LocalScribe.App.Tests/ReadViewStatusTests.cs`
- `tests/LocalScribe.App.Tests/SessionNoticeTests.cs`
- `tests/LocalScribe.Core.Tests/TranscriptCitationTests.cs`
- `tests/LocalScribe.App.Tests/ReadViewCopyTests.cs`
- `tests/LocalScribe.App.Tests/NoNetworkInAppOrCoreTests.cs`
- `tests/LocalScribe.App.Tests/ComponentFetchClientTests.cs`
- `tests/LocalScribe.App.Tests/ComponentProbeTests.cs`
- `tests/LocalScribe.App.Tests/ComponentsPanelViewModelTests.cs`
- `tests/LocalScribe.App.Tests/ShippingScriptTests.cs`

**Modified:**

- `src/LocalScribe.App/Services/IUiErrorReporter.cs` - gains a severity-carrying `Info` overload as
  a DEFAULT INTERFACE METHOD so the 24 test fakes keep compiling untouched.
- `src/LocalScribe.App/Services/InfoBarErrorReporter.cs` - gains a `Severities` queue kept in
  lockstep with `Messages`; `Messages`' element type is unchanged because two tests pin it.
- `src/LocalScribe.App/MainWindow.xaml` / `MainWindow.xaml.cs` - the hardcoded `Severity="Error"`
  becomes the design-time default and `SyncInfoBar` sets it per message.
- `tests/LocalScribe.App.Tests/AppServiceFakes.cs` - `FakeUiErrorReporter` records severities.
- `src/LocalScribe.App/TrayIconHost.cs` - assigns and clears `Application.Current.MainWindow`.
- `src/LocalScribe.App/App.xaml.cs` - dialog `Owner`s go through a closed-window guard; the
  Components panel and fetch client are wired into `SettingsPageViewModel`.
- `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs` - dialog-local status, busy indicator,
  a real `CancellationTokenSource` and a Cancel command replacing four `CancellationToken.None`s.
- `src/LocalScribe.App/ExportDialog.xaml` - the status InfoBar, the busy row and the Stop button.
- `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs`,
  `src/LocalScribe.App/ViewModels/RetranscribeDialogViewModel.cs` - the same status pair.
- `src/LocalScribe.App/ImportDialog.xaml`, `src/LocalScribe.App/RetranscribeDialog.xaml` - their bars.
- `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs` - a general dialog-local status pair, a
  TEE reporter for the three editor dialogs it builds, a public `LoadedVersionId`, and the pure
  row-selection helper the copy commands use.
- `src/LocalScribe.App/ReadViewWindow.xaml` - the status InfoBar, `SelectionMode="Extended"`, the
  two copy context-menu items and their key bindings.
- `src/LocalScribe.App/ReadViewWindow.xaml.cs` - the two copy commands and the clipboard write.
- `src/LocalScribe.App/ViewModels/SessionViewModel.cs` - a persistent notice pair driven off the
  unconditional raise path, and a failed `StartAsync` that is no longer silent.
- `src/LocalScribe.App/LiveViewWindow.xaml` - the persistent notice InfoBar in the warning row.
- `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs` - hosts `ComponentsPanelViewModel`.
- `src/LocalScribe.App/SettingsPage.xaml` - the "Components" card.
- `src/LocalScribe.App/LocalScribe.App.csproj` - the Velopack package reference.
- `LocalScribe.slnx` - the new `LocalScribe.Fetch` project.
- `tools/fetch-models.ps1` - a `-WriteComponentManifest` switch that emits the pin list the
  in-app downloader reads, so no SHA-256 is ever hand-typed into C#.
- `tools/verify-diarizer.ps1` - the forbidden-beside-the-app list, corrected so a real shipped
  layout can pass it (the exe beside the app is REQUIRED, and App's own `onnxruntime.dll` is
  flattened there by every RID publish).

---

## Task 1: `NoticeSeverity` - stop rendering successes red

`MainWindow.xaml:17` hardcodes `Severity="Error"` and `MainWindow.xaml.cs:134-139` never re-sets it,
so all 32 `_errors.Info(...)` call sites - including `"Exported to C:\..."` - render as red errors.

**Files:**
- Create: `src/LocalScribe.App/Services/NoticeSeverity.cs`
- Create: `tests/LocalScribe.App.Tests/NoticeSeverityRoutingTests.cs`
- Modify: `src/LocalScribe.App/Services/IUiErrorReporter.cs:7-11`
- Modify: `src/LocalScribe.App/Services/InfoBarErrorReporter.cs:10-23`
- Modify: `src/LocalScribe.App/MainWindow.xaml:16-17`
- Modify: `src/LocalScribe.App/MainWindow.xaml.cs:134-139`
- Modify: `tests/LocalScribe.App.Tests/AppServiceFakes.cs:26-32`

**Interfaces:**
- Consumes: nothing from earlier tasks. If Plan A has landed,
  `InfoBarErrorReporter(Action<Action> dispatch, IDiagnosticLog? log = null)`.
- Produces:
  - `LocalScribe.App.Services.NoticeSeverity { Informational, Success, Warning, Error }`
  - `IUiErrorReporter.Info(string message, NoticeSeverity severity)` - a DEFAULT INTERFACE METHOD
    whose default body is `Info(message)`.
  - `InfoBarErrorReporter.Severities : ObservableCollection<NoticeSeverity>` - same length and same
    index as `Messages`.
  - `FakeUiErrorReporter.InfoSeverities : List<NoticeSeverity>` - same length/index as `Infos`.
  Tasks 3, 4 and 12 call `Info(message, NoticeSeverity.Success)`.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/NoticeSeverityRoutingTests.cs`:

```csharp
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Per-notice severity on the shell InfoBar queue (Tier 1 plan D, T1-5, 2026-08-05).
/// The defect: MainWindow.xaml hardcoded Severity="Error" and SyncInfoBar never re-set it, so
/// "Exported to C:\..." and "Imported \"X\"." rendered red. Severities is a PARALLEL collection
/// rather than a richer element type on Messages because InfoBarErrorReporterTests and
/// MainWindowViewModelTests pin Messages as ObservableCollection&lt;string&gt;.</summary>
public sealed class NoticeSeverityRoutingTests
{
    [Fact]
    public void Report_is_always_an_error_and_plain_Info_is_informational()
    {
        var reporter = new InfoBarErrorReporter(a => a());

        reporter.Report("Delete session", new InvalidOperationException("folder is locked"));
        reporter.Info("Recovered 2 interrupted session(s)");

        Assert.Equal(new[] { "Delete session: folder is locked", "Recovered 2 interrupted session(s)" },
            reporter.Messages);
        Assert.Equal(new[] { NoticeSeverity.Error, NoticeSeverity.Informational }, reporter.Severities);
    }

    [Fact]
    public void An_explicit_severity_rides_the_message_at_the_same_index()
    {
        var reporter = new InfoBarErrorReporter(a => a());

        reporter.Info("Exported to C:\\out.docx", NoticeSeverity.Success);
        reporter.Info("Audio outlasted the transcript", NoticeSeverity.Warning);

        Assert.Equal(reporter.Messages.Count, reporter.Severities.Count);
        Assert.Equal(NoticeSeverity.Success, reporter.Severities[0]);
        Assert.Equal(NoticeSeverity.Warning, reporter.Severities[1]);
    }

    [Fact]
    public void DismissOldest_advances_both_queues_together()
    {
        var reporter = new InfoBarErrorReporter(a => a());
        reporter.DismissOldest();                                  // empty: still no throw
        reporter.Info("first", NoticeSeverity.Success);
        reporter.Report("Second", new InvalidOperationException("boom"));

        reporter.DismissOldest();

        Assert.Equal(new[] { "Second: boom" }, reporter.Messages);
        Assert.Equal(new[] { NoticeSeverity.Error }, reporter.Severities);
    }

    [Fact]
    public void A_reporter_that_only_implements_the_narrow_Info_still_receives_the_message()
    {
        // The widening is a DEFAULT INTERFACE METHOD so that the 24 hand-written fakes and
        // TrayNoticeReporter (a balloon has no severity concept) need no SECOND edit - Plan A
        // already gave every one of them `Info(string message, bool privileged = true)`. Prove the
        // default body forwards rather than swallowing.
        var narrow = new NarrowReporter();
        IUiErrorReporter seam = narrow;

        seam.Info("only the message survives", NoticeSeverity.Success);

        Assert.Equal(new[] { "only the message survives" }, narrow.Seen);
    }

    private sealed class NarrowReporter : IUiErrorReporter
    {
        public List<string> Seen { get; } = new();
        public void Report(string context, Exception ex) => Seen.Add(context + ": " + ex.Message);
        // The trailing `bool privileged = true` is Plan A's shipped interface member - a
        // one-parameter Info(string) does not implement it (CS0535).
        public void Info(string message, bool privileged = true) => Seen.Add(message);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~NoticeSeverityRoutingTests" --nologo
```

Expected: FAIL to compile - `CS0246: The type or namespace name 'NoticeSeverity' could not be found`
and `CS1061: 'InfoBarErrorReporter' does not contain a definition for 'Severities'`.

- [ ] **Step 3: Create the enum**

Create `src/LocalScribe.App/Services/NoticeSeverity.cs`:

```csharp
namespace LocalScribe.App.Services;

/// <summary>How a queued shell notice renders in MainWindow's InfoBar (Tier 1 plan D, T1-5,
/// 2026-08-05). Mirrors Wpf.Ui.Controls.InfoBarSeverity's four members BY NAME but is declared
/// here rather than reusing that type: IUiErrorReporter and InfoBarErrorReporter are WPF-free by
/// design (see InfoBarErrorReporter's own doc comment), and taking a Wpf.Ui type into that seam
/// would drag WPF into all 24 test fakes that implement the interface. MainWindow maps this to
/// the control enum at the ONE place it renders - SyncInfoBar.
/// REJECTED: a bool isError. The bar has four states, and the defect being fixed is precisely
/// that a two-state model (hardcoded Error, never re-set) painted every success red.</summary>
public enum NoticeSeverity { Informational, Success, Warning, Error }
```

- [ ] **Step 4: Widen the interface with a default interface method**

**This is an APPEND, not a file replacement.** Plan A already rewrote
`src/LocalScribe.App/Services/IUiErrorReporter.cs` - both the member list and a ~30-line doc block
recording the redaction rules. Add ONLY the new default interface method; do not touch the existing
`Report`/`Info` members, and do not shorten the doc comment.

The existing members after Plan A are:

```csharp
    void Report(string context, Exception ex);
    void Info(string message, bool privileged = true);
```

**`privileged` is load-bearing** (Plan A fix round 2): `Info` marks its message wholesale by default
so a caller-composed string carrying a participant name or a session title cannot reach
`diagnostics\` at the default `Settings.Logging.IncludeTranscriptText = false`. Deleting that
parameter re-opens a leak Plan A paid a fix round to close, and breaks all 24 test fakes, which
already spell it `Info(string message, bool privileged = true)`.

Append inside the existing interface:

```csharp
    /// <summary>Info with an explicit bar colour (Tier 1 plan D, T1-5, 2026-08-05). A DEFAULT
    /// INTERFACE METHOD on purpose: 26 types implement this interface (2 production, 24 test
    /// fakes) and only InfoBarErrorReporter can do anything with a severity - a tray balloon has
    /// no such concept. A default that DISCARDS the severity lets every other implementer stay
    /// untouched and keeps its existing assertions green.
    /// REJECTED: an abstract second overload (26 edits, 24 of them meaningless) and changing
    /// Info's existing signature (breaks all 32 production call sites in one commit).</summary>
    void Info(string message, NoticeSeverity severity) => Info(message);
```

**The two overloads do not collide.** `Info(string, bool = true)` and `Info(string, NoticeSeverity)`
differ in the second parameter's type, and `Info("x")` still binds unambiguously to the `bool`
overload via its default. The DIM body calling `Info(message)` therefore routes through the
marked-by-default path, which is what it should do.

- [ ] **Step 5: Add the parallel severity queue**

In `src/LocalScribe.App/Services/InfoBarErrorReporter.cs`, keep the primary-constructor parameter
list EXACTLY as it stands (`Action<Action> dispatch, IDiagnosticLog? log = null` after Plan A).

**These are SURGICAL edits, not a class-body replacement.** Plan A's `Report` and `Info` bodies each
carry logic that must survive verbatim - a structured `log?.Write(...)`, a display-side
`DiagnosticRedaction.Apply(...)` strip, and the `privileged ?` ternary. Rewriting the bodies loses
them silently.

**1. Add the parallel queue and the shared `Add`,** leaving `Messages` where it is:

```csharp
    /// <summary>Severity of Messages[i], at the SAME index and always the SAME length (Tier 1
    /// plan D, T1-5, 2026-08-05). A parallel collection rather than making Messages hold a
    /// record: MainWindow.xaml.cs:37/131/136-138 and MainWindowViewModel.cs:14 consume this
    /// class CONCRETELY and InfoBarErrorReporterTests asserts Messages against a string[], so
    /// changing the element type breaks pinned tests for no user-visible gain. Lockstep is
    /// maintained in exactly two places - Add and DismissOldest.</summary>
    public ObservableCollection<NoticeSeverity> Severities { get; } = [];

    // Severities FIRST, Messages second: MainWindow.SyncInfoBar runs off
    // Messages.CollectionChanged and reads Severities[0] in that same turn, so the severity for
    // the new head must already be in place when the message lands.
    //
    // Add does NO logging (Tier 1 plan D, 2026-08-05). REJECTED: moving Plan A's log?.Write calls
    // in here to share them - Report and Info write DIFFERENT payloads on purpose (a four-argument
    // structured line with DiagnosticRedaction.ForException(ex) as the detail, versus a
    // three-argument info line with the marked message), and Add only ever sees the already
    // concatenated "context: message" string, which makes ForException's per-exception marking and
    // per-exception stack neutralisation structurally unreachable and puts the raw ex.Message in
    // diagnostics unmarked. Add is also called from INSIDE dispatch; the durable record must not
    // depend on the dispatcher ever running.
    private void Add(string message, NoticeSeverity severity)
    {
        Severities.Add(severity);
        Messages.Add(message);
    }
```

**2. Change ONLY the final `dispatch(...)` line of each existing method.** Keep everything above it
byte-identical:

```csharp
    public void Report(string context, Exception ex)
    {
        // ... Plan A's log?.Write(DiagnosticLevels.Error, "ui", context,
        //     DiagnosticRedaction.ForException(ex)) and the `shown` display strip stay EXACTLY
        //     as they are ...
        dispatch(() => Add(shown + ": " + ex.Message, NoticeSeverity.Error));
    }

    public void Info(string message, bool privileged = true)
    {
        // ... Plan A's log?.Write(DiagnosticLevels.Info, "ui",
        //     privileged ? DiagnosticRedaction.Mark(message) : message) stays EXACTLY as it is ...
        dispatch(() => Add(message, NoticeSeverity.Informational));
    }

    public void Info(string message, NoticeSeverity severity)
    {
        // The severity overload is the one NEW body. It logs the same way Info(string, bool) does -
        // marked by default - and maps the severity onto the level vocabulary the shared contract
        // defines. REJECTED: a two-arm `severity == NoticeSeverity.Error ? "error" : "info"` - it
        // writes a Warning notice at "info", so a user who sets Settings.Logging.Level to "warn" to
        // cut noise SILENTLY loses every warning the app raised. Losing warnings is the opposite of
        // what that setting is for.
        string level = severity switch
        {
            NoticeSeverity.Error => DiagnosticLevels.Error,
            NoticeSeverity.Warning => DiagnosticLevels.Warn,
            _ => DiagnosticLevels.Info,
        };
        log?.Write(level, "ui", DiagnosticRedaction.Mark(message));
        dispatch(() => Add(message, severity));
    }
```

**3. Extend `DismissOldest` to pop both collections:**

```csharp
    public void DismissOldest()
    {
        if (Messages.Count == 0) return;
        Severities.RemoveAt(0);
        Messages.RemoveAt(0);
    }
```

Keep the `DiagnosticRedaction.Mark(message)` wrapper on every `Info` path - it is what makes
`Settings.Logging.IncludeTranscriptText` effective over notice text that can carry a matter title or
a participant name - and keep every `log?.Write(...)` OUTSIDE `dispatch`.

- [ ] **Step 6: Set the severity where the bar is rendered**

`src/LocalScribe.App/MainWindow.xaml:17` - the hardcoded severity becomes the design-time default:

```xml
        <ui:InfoBar x:Name="ErrorBar" DockPanel.Dock="Top" Margin="12,12,12,0"
                    Title="LocalScribe" Severity="Informational" IsClosable="True" IsOpen="False" />
```

`src/LocalScribe.App/MainWindow.xaml.cs` - replace `SyncInfoBar` (`:134-139`) and add the map
beside it:

```csharp
    private void SyncInfoBar()
    {
        var messages = _vm.Errors.Messages;
        var severities = _vm.Errors.Severities;
        ErrorBar.Message = messages.Count > 0 ? messages[0] : string.Empty;
        // T1-5 (2026-08-05): Severity was hardcoded Error in XAML and never re-set HERE, so every
        // success - "Exported to C:\...", "Imported \"X\"." - rendered red. Set it BEFORE IsOpen
        // so the user never sees the previous message's colour flash under the new text.
        ErrorBar.Severity = severities.Count > 0 ? Map(severities[0]) : InfoBarSeverity.Informational;
        ErrorBar.IsOpen = messages.Count > 0;
    }

    /// <summary>The one crossing point between the WPF-free NoticeSeverity and Wpf.Ui's control
    /// enum. Kept here, not on the enum, so Services/ stays WPF-free.</summary>
    private static InfoBarSeverity Map(NoticeSeverity severity) => severity switch
    {
        NoticeSeverity.Success => InfoBarSeverity.Success,
        NoticeSeverity.Warning => InfoBarSeverity.Warning,
        NoticeSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };
```

`MainWindow.xaml.cs` already has `using Wpf.Ui.Controls;` (the `InfoBar.IsOpenProperty` descriptor
hook at `:42-44` needs it). Add `using LocalScribe.App.Services;` if it is not already present.

- [ ] **Step 7: Teach the shared fake to record severities**

In `tests/LocalScribe.App.Tests/AppServiceFakes.cs`, replace `FakeUiErrorReporter` (`:26-32`):

```csharp
public sealed class FakeUiErrorReporter : IUiErrorReporter
{
    public readonly List<(string Context, Exception Ex)> Reports = new();
    public readonly List<string> Infos = new();
    /// <summary>Severity of Infos[i], same index (Tier 1 plan D, T1-5). The 23 per-file private
    /// reporter fakes deliberately stay narrow - they already carry Plan A's
    /// Info(string message, bool privileged = true), and the interface's default method forwards
    /// the severity overload to that one for them, so their existing Infos assertions are
    /// unaffected and none of them needs a SECOND edit.</summary>
    public readonly List<NoticeSeverity> InfoSeverities = new();

    public void Report(string context, Exception ex) => Reports.Add((context, ex));
    // Plan A's shipped member - the trailing `bool privileged = true` is required to implement the
    // interface (CS0535 without it) and every existing one-argument Info("...") call still binds
    // here via the default.
    public void Info(string message, bool privileged = true) => Info(message, NoticeSeverity.Informational);
    public void Info(string message, NoticeSeverity severity)
    {
        Infos.Add(message);
        InfoSeverities.Add(severity);
    }
}
```

- [ ] **Step 8: Run the test and confirm it passes**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~NoticeSeverityRoutingTests|FullyQualifiedName~InfoBarErrorReporterTests|FullyQualifiedName~MainWindowViewModelTests" --nologo
```

Expected: PASS - the 4 new facts plus the 2 pre-existing `InfoBarErrorReporterTests` facts, which
must be untouched.

- [ ] **Step 9: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/Services/NoticeSeverity.cs src/LocalScribe.App/Services/IUiErrorReporter.cs src/LocalScribe.App/Services/InfoBarErrorReporter.cs src/LocalScribe.App/MainWindow.xaml src/LocalScribe.App/MainWindow.xaml.cs tests/LocalScribe.App.Tests/AppServiceFakes.cs tests/LocalScribe.App.Tests/NoticeSeverityRoutingTests.cs
git commit -m "feat(ui): per-notice severity so the shell InfoBar stops painting successes red"
```

---

## Task 2: assign `Application.MainWindow` so `CenterOwner` centres on the shell

`Application.MainWindow` is never assigned anywhere in the repo, so WPF auto-assigns the first
`Window` constructed on the UI thread - `OverlayWindow` at `App.xaml.cs:947` (or `ConsentDialog` at
`:109` on first run). Export, Import and Re-transcribe all set `Owner = MainWindow` and are
`WindowStartupLocation="CenterOwner"`, so they centre on the recording pill, which need never have
been shown.

**Rejected: routing `Owner` through `WindowRegistry`.** Its own doc comment declares it "WPF-free
(stores close Actions only)" and it is keyed by SESSION id, so it has no slot for an app-level
manager window. Adding a `Window` member would falsify that contract for one caller's convenience.

**Files:**
- Modify: `src/LocalScribe.App/TrayIconHost.cs:136-145`
- Modify: `src/LocalScribe.App/App.xaml.cs:390`, `:481`, `:723`
- Create: `tests/LocalScribe.App.Tests/ShellOwnerWiringTests.cs`

**Interfaces:**
- Consumes: `RepoPaths.SolutionRoot()` and `RepoPaths.AppXaml(string relative)` - `public static`
  helpers already living in `tests/LocalScribe.App.Tests/XamlHygieneTests.cs`.
- Produces: `App.ShellOwner()` - `private Window? ShellOwner()` on `App`; no later task calls it.

**This is window plumbing.** There is no STA harness in this suite (`grep` for `STAThread` across
`tests/LocalScribe.App.Tests` returns nothing), so the behaviour cannot be executed by a test. The
test below pins the WIRING as source text - the `XamlHygieneTests.AppIcon_ExistsAndIsWiredInCsproj`
precedent - so a later refactor cannot silently drop half of it. Runtime confirmation is a smoke
item, listed at the end of this plan.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/ShellOwnerWiringTests.cs`:

```csharp
using System.IO;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins the Application.MainWindow wiring as SOURCE TEXT (Tier 1 plan D, T1-5,
/// 2026-08-05). Application.MainWindow was never assigned, so WPF auto-assigned it to the first
/// Window constructed - OverlayWindow (App.xaml.cs:947) - and the three CenterOwner dialogs
/// centred on the recording pill. The fix is two lines in TrayIconHost plus a closed-window
/// guard in App, none of which can be executed here: this suite has no STA/dispatcher harness,
/// so no test can construct a Window at all. A source-text assertion is the honest instrument -
/// the same one XamlHygieneTests.AppIcon_ExistsAndIsWiredInCsproj uses on the csproj - and it
/// stops a refactor dropping the assignment or, worse, the clear-on-close half.</summary>
public sealed class ShellOwnerWiringTests
{
    private static string Read(string relative)
        => File.ReadAllText(RepoPaths.AppXaml(relative));

    [Fact]
    public void Tray_assigns_the_shell_as_Application_MainWindow_when_it_opens_it()
    {
        string tray = Read("TrayIconHost.cs");
        Assert.Contains("Application.Current.MainWindow = _main;", tray);
    }

    [Fact]
    public void Tray_clears_Application_MainWindow_when_the_shell_closes()
    {
        // The shell GENUINELY closes and is re-created (TrayIconHost's own doc comment). A closed
        // Window left as Owner makes the next ShowDialog throw InvalidOperationException, so the
        // clear is not optional tidiness - it is the half that keeps Export openable after the
        // user closes the manager window once.
        string tray = Read("TrayIconHost.cs");
        Assert.Contains("Application.Current.MainWindow = null;", tray);
    }

    [Fact]
    public void No_dialog_takes_the_raw_MainWindow_as_its_Owner_any_more()
    {
        // Owner = MainWindow was the defect. Every site must go through ShellOwner(), which
        // returns null for an unloaded or closed window rather than handing WPF a dead Owner.
        string app = Read("App.xaml.cs");
        Assert.DoesNotContain("{ Owner = MainWindow }", app);
        Assert.Contains("Window? ShellOwner()", app);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(app, @"Owner = ShellOwner\(\)").Count);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ShellOwnerWiringTests" --nologo
```

Expected: 3 failures - `Assert.Contains() Failure: Sub-string not found` for the two
`Application.Current.MainWindow` assertions, and `Assert.DoesNotContain() Failure` for
`{ Owner = MainWindow }`.

- [ ] **Step 3: Assign and clear the shell window in the tray host**

In `src/LocalScribe.App/TrayIconHost.cs`, replace `OpenMainWindow` (`:136-145`):

```csharp
    public void OpenMainWindow()
    {
        if (_main is null)
        {
            _main = _mainWindowFactory();
            // T1-5 (2026-08-05): nothing in the app ever assigned Application.MainWindow, so WPF
            // auto-assigned it to the first Window constructed - the OverlayWindow recording pill
            // (App.xaml.cs:947) - and every CenterOwner dialog centred on a window that need
            // never have been SHOWN. Assign the real shell here, and null it below: this window
            // genuinely closes and re-creates, and a CLOSED window left as Owner makes the next
            // ShowDialog throw InvalidOperationException. ShutdownMode is OnExplicitShutdown
            // (App.xaml:5), so assigning MainWindow changes nothing about shutdown.
            Application.Current.MainWindow = _main;
            _main.Closed += (_, _) =>
            {
                _main = null;
                Application.Current.MainWindow = null;
            };
        }
        _main.Show();
        _main.Activate();
    }
```

`TrayIconHost.cs` already has `using System.Windows;` (it calls `MessageBox.Show` and
`Application.Current.Shutdown()`), so no new using is needed.

- [ ] **Step 4: Guard the three dialog Owners**

In `src/LocalScribe.App/App.xaml.cs`, add this method to the `App` class, immediately after
`OnStartup` closes (anywhere at class scope is fine - put it beside `OnExit`):

```csharp
    /// <summary>The window a modal dialog should centre on, or null (Tier 1 plan D, T1-5,
    /// 2026-08-05). Application.MainWindow is now assigned by TrayIconHost.OpenMainWindow and
    /// nulled on close, but a dialog can still be raised while the shell is closed - the read
    /// view and the Record console both open Export directly. Handing WPF a closed Window as
    /// Owner throws InvalidOperationException, so an unloaded one degrades to null and the
    /// dialog falls back to CenterScreen. IsLoaded (not IsVisible): a shell minimised to the
    /// tray is still a valid owner.
    /// REJECTED: Window.GetWindow(this) - that is the in-PAGE idiom (Pages/MattersPage.xaml.cs:260)
    /// and there is no visual to walk up from inside these App-level factories.</summary>
    private Window? ShellOwner() => MainWindow is { IsLoaded: true } shell ? shell : null;
```

Then change all three construction sites:

- `:390` `new RetranscribeDialog(retransVm) { Owner = MainWindow }.ShowDialog();`
  -> `new RetranscribeDialog(retransVm) { Owner = ShellOwner() }.ShowDialog();`
- `:481` `new ExportDialog(exportVm) { Owner = MainWindow }.ShowDialog();`
  -> `new ExportDialog(exportVm) { Owner = ShellOwner() }.ShowDialog();`
- `:723` `new ImportDialog(importVm) { Owner = MainWindow }.ShowDialog();`
  -> `new ImportDialog(importVm) { Owner = ShellOwner() }.ShowDialog();`

- [ ] **Step 5: Run the test and confirm it passes**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ShellOwnerWiringTests" --nologo
```
Expected: PASS (3 facts).

- [ ] **Step 6: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/TrayIconHost.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/ShellOwnerWiringTests.cs
git commit -m "fix(ui): assign Application.MainWindow so modal dialogs centre on the shell"
```

---

## Task 3: Export dialog - a local InfoBar, a busy indicator and a real `CancellationTokenSource`

`ExportDialogViewModel` has no dialog-local InfoBar: both its success `Info` and its failure
`Report` go to the shell reporter, i.e. MainWindow's InfoBar, which this separate window cannot
show - the exact invisible-feedback trap the read view and Split-speakers were already fixed for.
All four export calls pass `CancellationToken.None` (`ExportDialogViewModel.cs:185-194`), so a
multi-gigabyte zip cannot be stopped.

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs:118-204`
- Modify: `src/LocalScribe.App/ExportDialog.xaml:1-9`, `:58-61` (the file is 63 lines; the button
  row is `:58-61` and `:62-63` are the closing root `StackPanel` and `Window` tags)
- Create: `tests/LocalScribe.App.Tests/ExportDialogStatusTests.cs`

**Interfaces:**
- Consumes: `NoticeSeverity` (Task 1).
- Produces, on `ExportDialogViewModel`:
  - `string? StatusMessage`, `bool StatusIsError`, `bool HasStatus` (`=> StatusMessage is not null`)
  - `void ShowStatus(string message, bool isError)`
  - `IRelayCommand StopCommand`
  - `bool IsBusy` (already existed)
  The constructor signature is UNCHANGED:
  `ExportDialogViewModel(string sessionId, string sessionTitle, MaintenanceService maintenance, ISettingsService settings, Func<SavePathRequest, string?> pickSavePath, Action<string> revealFile, IUiErrorReporter errors, Action<Action> dispatch)`.
  Task 4 copies this exact status shape into two more view models.

**Progress is INDETERMINATE, deliberately.** None of the four `MaintenanceService.Export*Async`
methods exposes a fraction, and the three textual ones are a single synchronous string build with
no natural progress signal. Threading `IProgress<double>` through four service methods, the zip
archiver and three renderers to manufacture one is a separate change; a fabricated fraction would
be worse than an honest spinner. The user problem being fixed - a dialog that looks frozen with no
way out - is answered by the busy row plus a working Stop.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/ExportDialogStatusTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Dialog-local feedback plus a real CancellationTokenSource on the export dialog
/// (Tier 1 plan D, T1-5, 2026-08-05). Before this, every export outcome rendered on MainWindow's
/// InfoBar - a window this separate dialog cannot show - and all four export calls passed
/// CancellationToken.None, so a multi-gigabyte .zip could not be stopped.</summary>
public sealed class ExportDialogStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-expstat-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>A finalized on-disk session with one turn - enough for every export format to
    /// produce a real file, and small enough that the export completes inside the test.</summary>
    private async Task<(MaintenanceService Svc, FakeUiErrorReporter Errors)> MakeAsync()
    {
        var paths = new StoragePaths(_root);
        var settings = new FakeSettingsService(new Settings { StorageRoot = _root });
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero)));
        Directory.CreateDirectory(paths.SessionDir("s1"));
        await new SessionStore(paths.SessionJson("s1")).SaveAsync(new SessionRecord
        {
            Id = "s1", App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, default);
        await new MetadataStore(paths.MetaJson("s1")).SaveAsync(
            new SessionMeta { Title = "Doe intake" }, default);
        await new TranscriptStore(paths.TranscriptJsonl("s1")).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 4000, "hello", "Me"), default);
        return (svc, new FakeUiErrorReporter());
    }

    [Fact]
    public async Task A_failure_lands_in_the_dialogs_own_bar_not_only_the_shell()
    {
        var (svc, errors) = await MakeAsync();
        // A directory as the destination makes the output FileStream open throw.
        string bad = Path.Combine(_root, "a-directory");
        Directory.CreateDirectory(bad);
        var vm = new ExportDialogViewModel("s1", "Doe intake", svc, new FakeSettingsService(),
            _ => bad, _ => { }, errors, a => a()) { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(vm.HasStatus);
        Assert.True(vm.StatusIsError);
        Assert.NotNull(vm.StatusMessage);
        Assert.NotEmpty(errors.Reports);            // still queued for the shell and Plan A's log
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_success_reports_Success_severity_to_the_shell_and_leaves_no_error_status()
    {
        var (svc, errors) = await MakeAsync();
        string dest = Path.Combine(_root, "out.md");
        var vm = new ExportDialogViewModel("s1", "Doe intake", svc, new FakeSettingsService(),
            _ => dest, _ => { }, errors, a => a()) { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(dest));
        Assert.False(vm.StatusIsError);
        Assert.Equal(new[] { NoticeSeverity.Success }, errors.InfoSeverities);
    }

    [Fact]
    public async Task A_cancelled_save_as_clears_any_stale_status_and_reports_nothing()
    {
        var (svc, errors) = await MakeAsync();
        var vm = new ExportDialogViewModel("s1", "Doe intake", svc, new FakeSettingsService(),
            _ => null, _ => { }, errors, a => a()) { Format = ExportFormat.Markdown };
        vm.ShowStatus("stale from a previous attempt", isError: true);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.False(vm.HasStatus);                 // cleared at the START of the attempt
        Assert.Empty(errors.Reports);
        Assert.Empty(errors.Infos);
    }

    [Fact]
    public async Task Stop_before_the_export_starts_cancels_it_and_is_reported_as_information()
    {
        // Cancelling is a USER ACTION, not a fault: no red bar and no shell Report (the
        // ImportDialogViewModel precedent at :287 says the same for import). The pickSavePath
        // seam is the one synchronous point inside ExportAsync where a test can press Stop.
        var (svc, errors) = await MakeAsync();
        string dest = Path.Combine(_root, "cancelled.md");
        ExportDialogViewModel? vm = null;
        vm = new ExportDialogViewModel("s1", "Doe intake", svc, new FakeSettingsService(),
            _ => { vm!.StopCommand.Execute(null); return dest; }, _ => { }, errors, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        // AMENDED IN EXECUTION (2026-08-06). The three assertions this fact originally carried
        // (!StatusIsError, no Reports, !IsBusy) are ALL equally true of a completely SUCCESSFUL
        // export, so the fact passed whether or not Stop did anything - it would have stayed
        // green with the four CancellationToken.None arguments left exactly as they were. Proved
        // by reverting ExportMarkdownAsync's `ct` to CancellationToken.None: the original three
        // assertions still passed, the two below fail. Vacuous-green is the defect class this
        // whole round exists to remove, so the fact now asserts the cancel ARM ran and that no
        // file survives.
        Assert.Equal("Export cancelled - no file was written.", vm.StatusMessage);
        Assert.False(File.Exists(dest));
        Assert.False(vm.StatusIsError);
        Assert.Empty(errors.Reports);
        Assert.Empty(errors.Infos);                 // no "Exported to ..." success notice either
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Stop_is_disabled_until_an_export_is_actually_running()
    {
        var vm = new ExportDialogViewModel("s1", "T",
            new MaintenanceService(new StoragePaths(_root), new FakeSettingsService(),
                new FakeRecycleBin(), TimeProvider.System),
            new FakeSettingsService(), _ => null, _ => { }, new FakeUiErrorReporter(), a => a());

        Assert.False(vm.StopCommand.CanExecute(null));
        vm.IsBusy = true;
        Assert.True(vm.StopCommand.CanExecute(null));
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ExportDialogStatusTests" --nologo
```

Expected: FAIL to compile - `CS1061: 'ExportDialogViewModel' does not contain a definition for
'HasStatus'` (and the same for `StatusIsError`, `StatusMessage`, `ShowStatus`, `StopCommand`).

- [ ] **Step 3: Add the status pair and the cancellation source**

In `src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs`, add the field beside the others
(after `_dispatch`, `:26`):

```csharp
    private CancellationTokenSource? _cts;
```

Add these members immediately after the `_isBusy` observable property (`:48`):

```csharp
    /// <summary>Dialog-local feedback, bound to THIS window's own InfoBar (Tier 1 plan D, T1-5,
    /// 2026-08-05, copying the SplitSpeakersWindow shape): the shared IUiErrorReporter renders on
    /// MainWindow's InfoBar, which this separate dialog cannot show - so a range-validation
    /// refusal or a failed write looked completely silent here. Null = no status; cleared at the
    /// start of every export attempt. The shell reporter is still called as well: the dialog
    /// CLOSES on success, so the success notice needs somewhere that outlives it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    /// <summary>True renders the status InfoBar as Error; false as Informational.</summary>
    [ObservableProperty] private bool _statusIsError;

    /// <summary>The status InfoBar's IsOpen binds here (a computed OneWay flag, since IsOpen
    /// cannot bind a null-check directly).</summary>
    public bool HasStatus => StatusMessage is not null;

    /// <summary>Public because ExportDialogStatusTests seeds a stale status to prove the next
    /// attempt clears it (there is no InternalsVisibleTo in this repo).</summary>
    public void ShowStatus(string message, bool isError) =>
        _dispatch(() => { StatusMessage = message; StatusIsError = isError; });

    private void ClearStatus() =>
        _dispatch(() => { StatusMessage = null; StatusIsError = false; });

    /// <summary>Stops an export in flight (Tier 1 plan D, T1-5). Before this, all four export
    /// calls passed CancellationToken.None, so a multi-gigabyte .zip of a long call could not be
    /// stopped at all. MaintenanceService.ExportWithOutputCleanupAsync already deletes the
    /// partially written output on failure, so a cancelled export leaves no half file behind.</summary>
    public IRelayCommand StopCommand { get; }
```

In the constructor, replace the last line (`ExportCommand = new AsyncRelayCommand(...)`, `:40`) with:

```csharp
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
```

Extend the busy reaction (`:113`):

```csharp
    partial void OnIsBusyChanged(bool value)
    {
        ExportCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
```

- [ ] **Step 4: Thread the token and the status through `ExportAsync`**

In `ExportAsync`, replace the opening (`:118-122`):

```csharp
    private async Task ExportAsync()
    {
        ClearStatus();
        // One CTS per attempt, disposed in the using: reusing a cancelled source would make every
        // later export in this same dialog start already-cancelled.
        using var cts = new CancellationTokenSource();
        _cts = cts;
        CancellationToken ct = cts.Token;
        IsBusy = true;
        try
        {
```

Replace every `CancellationToken.None` argument in the body with `ct`: `ResolveExcerptAsync`
(`:140-141`), `FilenameTokensAsync` (`:153`), `ExportSessionArchiveAsync` (`:185`),
`ExportMarkdownAsync` (`:188`), `ExportTextAsync` (`:191`), `ExportDocxAsync` (`:194`).
`PersistChoicesAsync` keeps `CancellationToken.None` - a successful export must persist the user's
choices even if Stop is pressed a moment too late.

Replace the success tail and the catch/finally (`:197-204`):

```csharp
            _errors.Info("Exported to " + dest, NoticeSeverity.Success);
            _revealFile(dest);
            await PersistChoicesAsync();
            _dispatch(() => Closed?.Invoke());
        }
        catch (OperationCanceledException)
        {
            // The user pressed Stop. NOT a failure: no red bar and no shell Report (the
            // ImportDialogViewModel precedent, :287). The partial output file is already gone -
            // MaintenanceService.ExportWithOutputCleanupAsync deletes what it created.
            ShowStatus("Export cancelled - no file was written.", isError: false);
        }
        catch (Exception ex)
        {
            // BOTH surfaces: the dialog stays open on failure, so the bar the user is looking at
            // must carry the reason; the shell queue keeps it after the dialog is dismissed and
            // is where Plan A's diagnostic log picks it up.
            ShowStatus(ex.Message, isError: true);
            _errors.Report("Export", ex);
        }
        finally
        {
            _cts = null;
            IsBusy = false;
        }
    }
```

No new using is needed: `CancellationTokenSource` arrives with implicit usings and
`NoticeSeverity` is in `LocalScribe.App.Services`, already imported at `:4`.

- [ ] **Step 5: Add the bar, the busy row and Stop to the dialog**

In `src/LocalScribe.App/ExportDialog.xaml`, add two namespace aliases to the root element beside
`xmlns:vm`:

```xml
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        xmlns:local="clr-namespace:LocalScribe.App"
```

Add the inverse-bool converter beside `BoolToVis` in `<Window.Resources>`
(`InverseBooleanConverter` lives at `src/LocalScribe.App/InverseBooleanConverter.cs` in the
`LocalScribe.App` namespace, NOT `LocalScribe.App.ViewModels`):

```xml
        <local:InverseBooleanConverter x:Key="InverseBool" />
```

Insert immediately after `<StackPanel Margin="16">`:

```xml
        <!-- Dialog-local feedback (Tier 1 plan D, T1-5, 2026-08-05): range refusals, write
             failures and cancellation surface HERE, not on MainWindow's InfoBar this separate
             dialog cannot show (the same trap the read-view save fix and the Split-speakers fix
             already answered). IsClosable=False: state-driven, cleared on the next attempt.
             Severity rides a Style+DataTrigger because InfoBar.Severity is an enum DP that
             cannot bind a bool. -->
        <ui:InfoBar Margin="0,0,0,8" IsClosable="False"
                    IsOpen="{Binding HasStatus, Mode=OneWay}"
                    Message="{Binding StatusMessage}">
            <ui:InfoBar.Style>
                <Style TargetType="{x:Type ui:InfoBar}">
                    <Setter Property="Severity" Value="Informational" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding StatusIsError}" Value="True">
                            <Setter Property="Severity" Value="Error" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </ui:InfoBar.Style>
        </ui:InfoBar>
```

Replace the button row (`:58-61` - the `<StackPanel Orientation="Horizontal" ...>` through its
`</StackPanel>`; do NOT touch `:62-63`, which close the root `StackPanel` and the `Window`) with:

```xml
        <!-- Indeterminate, deliberately (see the plan's Task 3 note): no export path exposes a
             fraction. The bar exists so a multi-gigabyte .zip does not look like a frozen dialog,
             and Stop exists so it can actually be abandoned. -->
        <ProgressBar Height="4" Margin="0,12,0,0" IsIndeterminate="True">
            <ProgressBar.Style>
                <Style TargetType="ProgressBar">
                    <Setter Property="Visibility" Value="Collapsed" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsBusy}" Value="True">
                            <Setter Property="Visibility" Value="Visible" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </ProgressBar.Style>
        </ProgressBar>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="Export..." IsDefault="True" Command="{Binding ExportCommand}" Margin="0,0,8,0" MinWidth="90" />
            <!-- Two separate buttons, not one that changes role: IsCancel=True closes the window
                 on Esc whatever is bound to it, so a single button would abandon the dialog
                 mid-write instead of cancelling the export. -->
            <Button Content="Stop" Command="{Binding StopCommand}" Margin="0,0,8,0" MinWidth="90">
                <Button.Style>
                    <Style TargetType="Button">
                        <Setter Property="Visibility" Value="Collapsed" />
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsBusy}" Value="True">
                                <Setter Property="Visibility" Value="Visible" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </Button.Style>
            </Button>
            <Button Content="Close" IsCancel="True" MinWidth="90"
                    IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBool}}" />
        </StackPanel>
```

- [ ] **Step 6: Run the tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ExportDialogStatusTests|FullyQualifiedName~ExportDialogViewModelTests" --nologo
```
Expected: PASS - the 5 new facts plus every pre-existing `ExportDialogViewModelTests` fact. Those
older tests use narrow private reporter fakes, so the `NoticeSeverity.Success` argument reaches them
through the interface's default body and their `Assert.Single(rep.Infos)` assertions are unaffected.

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/ExportDialog.xaml tests/LocalScribe.App.Tests/ExportDialogStatusTests.cs
git commit -m "feat(export): dialog-local status, busy indicator and a real cancellation source"
```

---

## Task 4: the same dialog-local bar on Import and Re-transcribe

`ImportDialog` and `RetranscribeDialog` have no InfoBar either - both route success and failure to
the shell reporter. Both already have progress bars and cancellation, so this task adds only the
status pair and the bar. The wording of the status is copied from what each already sends the shell,
so nothing new is invented.

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs:282-298`
- Modify: `src/LocalScribe.App/ViewModels/RetranscribeDialogViewModel.cs:130`, `:145-156`
- Modify: `src/LocalScribe.App/ImportDialog.xaml`, `src/LocalScribe.App/RetranscribeDialog.xaml`
- Create: `tests/LocalScribe.App.Tests/DialogLocalStatusTests.cs`

**Interfaces:**
- Consumes: `NoticeSeverity` (Task 1).
- Produces, identically on BOTH view models (the shape Task 3 established on
  `ExportDialogViewModel` and `SplitSpeakersViewModel.cs:254-275` established before that):
  `string? StatusMessage`, `bool StatusIsError`, `bool HasStatus`,
  `void ShowStatus(string message, bool isError)`.
  No later task consumes these.

The three copies are DELIBERATE duplication, matching the existing two copies
(`SplitSpeakersViewModel`, `ReadViewViewModel`). Do not extract a shared `DialogStatus` class: the
house convention is per-VM observable state, and a shared `ObservableObject` child would need its
own `PropertyChanged` re-broadcast to make `IsOpen="{Binding HasStatus}"` work from the window's
DataContext.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.App.Tests/DialogLocalStatusTests.cs`:

```csharp
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The dialog-local status pair on the import and re-transcribe dialogs (Tier 1 plan D,
/// T1-5, 2026-08-05). Both routed every outcome to MainWindow's InfoBar, which a separate modal
/// cannot show. These facts pin only the OBSERVABLE contract the two windows bind to - the
/// end-to-end runs live in ImportDialogViewModelTests and RetranscribeDialogViewModelTests, which
/// already own the heavy fixtures.</summary>
public sealed class DialogLocalStatusTests
{
    [Fact]
    public void Import_status_starts_empty_and_HasStatus_follows_the_message()
    {
        var vm = new ImportDialogViewModel(
            new NullDecoder(), (req, p, tp, dp, confirm, ct) => Task.FromResult("s1"),
            maintenance: null!, availableModels: () => new HashSet<string> { "base.en" },
            pickOpenPath: _ => null, confirmMismatch: _ => Task.FromResult(true),
            new FakeUiErrorReporter(), dispatch: a => a(), TimeProvider.System);

        Assert.False(vm.HasStatus);
        Assert.Null(vm.StatusMessage);

        vm.ShowStatus("Imported \"Doe intake\".", isError: false);
        Assert.True(vm.HasStatus);
        Assert.False(vm.StatusIsError);

        vm.ShowStatus("ffmpeg is not installed.", isError: true);
        Assert.True(vm.StatusIsError);
    }

    private sealed class NullDecoder : LocalScribe.Core.Import.IAudioDecoder
    {
        public Task<LocalScribe.Core.Import.AudioProbeResult> ProbeAsync(string path, CancellationToken ct)
            => Task.FromResult(new LocalScribe.Core.Import.AudioProbeResult());
        public Task<LocalScribe.Core.Import.DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct)
            => throw new NotSupportedException("this fact never decodes");
    }
}
```

Append to `tests/LocalScribe.App.Tests/ImportDialogViewModelTests.cs` (it already owns `MakeVm`,
`FakeDecoder` and `RecordingErrors2`):

```csharp
    [Fact]
    public async Task A_failed_import_puts_the_reason_in_the_dialogs_own_bar()
    {
        var (vm, decoder, errors) = MakeVm(
            runner: (req, p, tp, dp, confirm, ct) => throw new InvalidOperationException("decode failed"),
            pickedPath: "C:\\audio\\call.wav");
        await vm.PickFileCommand.ExecuteAsync(null);

        await vm.StartCommand.ExecuteAsync(null);

        // The dialog STAYS OPEN on failure, so the reason must be where the user is looking.
        Assert.True(vm.HasStatus);
        Assert.True(vm.StatusIsError);
        Assert.Contains("decode failed", vm.StatusMessage);
        Assert.NotEmpty(errors.Reports);          // and still queued for the shell / Plan A's log
    }
```

Append to `tests/LocalScribe.App.Tests/RetranscribeDialogViewModelTests.cs` (it already owns
`Make` and `SeedFinalizedAsync`):

```csharp
    [Fact]
    public async Task A_refused_run_puts_the_refusal_in_the_dialogs_own_bar()
    {
        // No models on disk: Start is gated and the VM refuses. That refusal previously rendered
        // on MainWindow's InfoBar - invisible from this modal.
        string id = await SeedFinalizedAsync();
        var (vm, _, _, _) = Make(id, models: new HashSet<string>());

        Assert.False(vm.HasModels);
        vm.ShowStatus("No transcription models are installed.", isError: true);
        Assert.True(vm.HasStatus);
        Assert.True(vm.StatusIsError);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~DialogLocalStatusTests|FullyQualifiedName~A_failed_import_puts_the_reason|FullyQualifiedName~A_refused_run_puts_the_refusal" --nologo
```

Expected: FAIL to compile - `CS1061: 'ImportDialogViewModel' does not contain a definition for
'HasStatus'`, and the same for `RetranscribeDialogViewModel`.

- [ ] **Step 3: Add the pair to `ImportDialogViewModel`**

In `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs`, add beside the other observable
properties (after `_transcribeProgressText`, `:165`):

```csharp
    /// <summary>Dialog-local feedback, bound to THIS window's own InfoBar (Tier 1 plan D, T1-5,
    /// 2026-08-05, the SplitSpeakersWindow shape). The shared IUiErrorReporter renders on
    /// MainWindow's InfoBar, which this separate modal cannot show - so a decode failure or a
    /// missing-ffmpeg refusal looked silent HERE, which is exactly where the user is looking.
    /// Null = no status; cleared at the start of each pick/start attempt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    /// <summary>True renders the status InfoBar as Error; false as Informational.</summary>
    [ObservableProperty] private bool _statusIsError;

    /// <summary>The status InfoBar's IsOpen binds here (a computed OneWay flag, since IsOpen
    /// cannot bind a null-check directly).</summary>
    public bool HasStatus => StatusMessage is not null;

    /// <summary>Public because DialogLocalStatusTests drives it directly (no InternalsVisibleTo
    /// in this repo).</summary>
    public void ShowStatus(string message, bool isError) =>
        _dispatch(() => { StatusMessage = message; StatusIsError = isError; });

    private void ClearStatus() =>
        _dispatch(() => { StatusMessage = null; StatusIsError = false; });
```

Add `ClearStatus();` as the first statement of `PickFileAsync` and of `StartAsync`, and mirror each
existing shell call in the outcome block (`:282-289`):

```csharp
            _errors.Info($"Imported \"{request.Title}\".", NoticeSeverity.Success);
            ShowStatus($"Imported \"{request.Title}\".", isError: false);
```
```csharp
            _errors.Info("Import cancelled - the partial session was discarded; the original file is untouched.");
            ShowStatus("Import cancelled - the partial session was discarded; the original file is untouched.",
                isError: false);
```
```csharp
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
            _errors.Report("Import audio", ex);
        }
```

Also mirror the `catch` at `:225`:

```csharp
        catch (Exception ex)
        {
            ShowStatus(ex.Message, isError: true);
            _errors.Report("Reading audio file", ex);
        }
```

Add `using LocalScribe.App.Services;` if the file does not already have it (it does - it takes
`IUiErrorReporter`).

- [ ] **Step 4: Add the pair to `RetranscribeDialogViewModel`**

In `src/LocalScribe.App/ViewModels/RetranscribeDialogViewModel.cs`, add this block beside the other
observable properties:

```csharp
    /// <summary>Dialog-local feedback, bound to THIS window's own InfoBar (Tier 1 plan D, T1-5,
    /// 2026-08-05, the SplitSpeakersWindow shape). The shared IUiErrorReporter renders on
    /// MainWindow's InfoBar, which this separate modal cannot show - so a refused or failed
    /// re-transcription looked silent HERE, which is exactly where the user is looking.
    /// Null = no status; cleared at the start of each run attempt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    /// <summary>True renders the status InfoBar as Error; false as Informational.</summary>
    [ObservableProperty] private bool _statusIsError;

    /// <summary>The status InfoBar's IsOpen binds here (a computed OneWay flag, since IsOpen
    /// cannot bind a null-check directly).</summary>
    public bool HasStatus => StatusMessage is not null;

    /// <summary>Public because DialogLocalStatusTests and RetranscribeDialogViewModelTests drive
    /// it directly (no InternalsVisibleTo in this repo).</summary>
    public void ShowStatus(string message, bool isError) =>
        _dispatch(() => { StatusMessage = message; StatusIsError = isError; });

    private void ClearStatus() =>
        _dispatch(() => { StatusMessage = null; StatusIsError = false; });
```

Then mirror its four existing shell calls. Every `_errors.*` argument below is the CURRENT text
byte-for-byte - the only change to the existing lines is the added `NoticeSeverity.Success`
argument on the one success `Info`:

```csharp
        catch (Exception ex)                                     // :130, LoadAsync
        {
            ShowStatus(ex.Message, isError: true);
            _errors.Report("Load session versions", ex);
        }
```
```csharp
                _errors.Info($"Re-transcription complete - {TranscriptVersions.ShortId(versionId)} "  // :145-146
                    + "is now the active transcript.", NoticeSeverity.Success);
                ShowStatus($"Re-transcription complete - {TranscriptVersions.ShortId(versionId)} "
                    + "is now the active transcript.", isError: false);
```
```csharp
            _errors.Info("Re-transcription cancelled - the partial version was discarded; "       // :153-154
                + "the session is unchanged.");
            ShowStatus("Re-transcription cancelled - the partial version was discarded; "
                + "the session is unchanged.", isError: false);
```
```csharp
        catch (Exception ex) { ShowStatus(ex.Message, isError: true); _errors.Report("Re-transcribe", ex); }  // :156
```

Add `ClearStatus();` as the first statement of `StartAsync` (`:133`), before `IsRunning = true;`.

- [ ] **Step 5: Add both InfoBars**

Both were checked: `ImportDialog.xaml:4` ALREADY declares
`xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"`; `RetranscribeDialog.xaml` does NOT, so add it
there only (as the fourth attribute of the root `<Window>`, matching ImportDialog's ordering).

Then insert this block as the FIRST child of each dialog's outermost content panel. Both panels are
`<StackPanel Margin="16" ...>` (`ImportDialog.xaml:10`, `RetranscribeDialog.xaml:9`), so **no
`DockPanel.Dock` attribute is needed on either bar** - do not add one:

```xml
        <!-- Dialog-local feedback (Tier 1 plan D, T1-5, 2026-08-05): failures and cancellations
             surface HERE, not on MainWindow's InfoBar this separate modal cannot show.
             IsClosable=False: state-driven, cleared at the start of the next attempt. Severity
             rides a Style+DataTrigger because InfoBar.Severity is an enum DP that cannot bind a
             bool. -->
        <ui:InfoBar Margin="0,0,0,8" IsClosable="False"
                    IsOpen="{Binding HasStatus, Mode=OneWay}"
                    Message="{Binding StatusMessage}">
            <ui:InfoBar.Style>
                <Style TargetType="{x:Type ui:InfoBar}">
                    <Setter Property="Severity" Value="Informational" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding StatusIsError}" Value="True">
                            <Setter Property="Severity" Value="Error" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </ui:InfoBar.Style>
        </ui:InfoBar>
```

- [ ] **Step 6: Run the tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~DialogLocalStatusTests|FullyQualifiedName~ImportDialogViewModelTests|FullyQualifiedName~RetranscribeDialogViewModelTests|FullyQualifiedName~ImportDialogSpeakerDetectionTests" --nologo
```
Expected: PASS - the 3 new facts plus every pre-existing fact in those three classes.

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs src/LocalScribe.App/ViewModels/RetranscribeDialogViewModel.cs src/LocalScribe.App/ImportDialog.xaml src/LocalScribe.App/RetranscribeDialog.xaml tests/LocalScribe.App.Tests/DialogLocalStatusTests.cs tests/LocalScribe.App.Tests/ImportDialogViewModelTests.cs tests/LocalScribe.App.Tests/RetranscribeDialogViewModelTests.cs
git commit -m "feat(ui): dialog-local status bars on the import and re-transcribe dialogs"
```

---

## Task 5: the read view's own status bar, reused by its two child dialogs

`ReadViewWindow` already owns a working dialog-local InfoBar (`ReadViewWindow.xaml:31-36`) - but it
is bound to `SaveError` and titled "Couldn't save your edits", so it can only carry one kind of
message. The two dialogs the read view parents (`CorrectTextDialog`, `ReassignSpeakerDialog`) still
route to the shell reporter - `CorrectTextViewModel.cs:95` `_reporter.Report("Save text
corrections", ex)` and `ReassignSpeakerViewModel.cs:173` `_reporter.Report("Reassign speaker", ex)`,
both fed the shell reporter by `ReadViewViewModel`'s three editor factories. Giving the read view a GENERAL status
pair makes those two an S-effort subset of T1-5 - they need no bar of their own - and Task 8 reuses
the same bar for clipboard failures.

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs:56-68` (the status pair), `:160-170`
  (build the tee reporter in the constructor) and `:910-985` (the THREE editor factories -
  `CreateCorrectionEditor` `:919`, `CreateReassignEditor` `:928`, `CreateReassignClusterEditor`
  `:981` - each currently passes `_reporter`)
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml:30-37`
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml.cs:548-590` - `ClearStatus()` before each
  `ShowDialog()` ONLY. The window does NOT construct the editor view models and passes no reporter
  argument; it calls `_vm.CreateCorrectionEditor(...)` / `_vm.CreateReassignEditor(...)` /
  `_vm.CreateReassignClusterEditor(...)` and wraps the result in a dialog.
- Create: `tests/LocalScribe.App.Tests/ReadViewStatusTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces, on `ReadViewViewModel`:
  - `string? StatusMessage`, `bool StatusIsError`, `bool HasStatus`
  - `void ShowStatus(string message, bool isError)` and `void ClearStatus()` - **both public**,
    because `ReadViewWindow`'s code-behind calls them when a child dialog or the clipboard fails.
  - `IUiErrorReporter DialogReporter` - the TEE handed to the three editor factories, and the one
    Task 8's clipboard `catch` uses. Reports land on this window's status bar AND on the shell
    queue (see the Architecture section's both-surfaces rule).
  Task 8 calls `ShowStatus` and `DialogReporter`.
- `SaveError`/`HasSaveError` are LEFT ALONE. They are a separate, titled bar pinned by
  `ReadViewEditModeTests`; folding them together would rewrite tests for no user gain.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/ReadViewStatusTests.cs`:

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

/// <summary>A GENERAL dialog-local status bar on the read view (Tier 1 plan D, T1-5, 2026-08-05).
/// The window already had a SaveError bar, but it is titled "Couldn't save your edits" and can
/// only carry that one kind of message - so the correction and reassign dialogs it parents still
/// reported to MainWindow's InfoBar, which they cannot show. Task 8's clipboard failures land
/// here too.</summary>
public sealed class ReadViewStatusTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-status-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (ReadViewViewModel Vm, FakeUiErrorReporter Shell) MakeVm()
    {
        var paths = new StoragePaths(_root);
        var settings = new FakeSettingsService(new Settings { StorageRoot = _root });
        var maintenance = new MaintenanceService(paths, settings, new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero)));
        var shell = new FakeUiErrorReporter();
        return (new ReadViewViewModel(maintenance, paths, settings, shell,
            new SilentPlayer(), dispatch: a => a(), TimeProvider.System), shell);
    }

    [Fact]
    public void The_child_dialog_reporter_TEES_to_the_bar_and_the_shell_never_replacing_it()
    {
        // The correction and speaker-reassign dialogs are the read view's evidentiary WRITE
        // paths. A failed write must reach BOTH surfaces: this window's bar (the dialog is what
        // the user is looking at) AND the shell queue, which outlives the dialog and is where
        // Plan A's diagnostic log picks it up. An adapter that only called ShowStatus would make
        // a failed correction unrecordable and unreportable.
        var (vm, shell) = MakeVm();

        vm.DialogReporter.Report("Save text corrections", new IOException("file is locked"));

        Assert.True(vm.StatusIsError);
        Assert.Contains("file is locked", vm.StatusMessage);
        var (context, ex) = Assert.Single(shell.Reports);
        Assert.Equal("Save text corrections", context);
        Assert.Equal("file is locked", ex.Message);
    }

    [Fact]
    public void Status_starts_empty_and_tracks_message_and_severity()
    {
        var (vm, _) = MakeVm();
        Assert.False(vm.HasStatus);
        Assert.Null(vm.StatusMessage);

        vm.ShowStatus("Copied 3 turns with citations.", isError: false);
        Assert.True(vm.HasStatus);
        Assert.False(vm.StatusIsError);
        Assert.Equal("Copied 3 turns with citations.", vm.StatusMessage);

        vm.ShowStatus("Couldn't save the correction: file is locked", isError: true);
        Assert.True(vm.StatusIsError);

        vm.ClearStatus();
        Assert.False(vm.HasStatus);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public void The_save_error_bar_is_untouched_and_stays_independent()
    {
        // SaveError is a SEPARATE, titled bar pinned by ReadViewEditModeTests. Setting one must
        // not move the other, or an edit failure would be silently replaced by a copy notice.
        var (vm, _) = MakeVm();
        vm.SaveError = "Couldn't save your transcript edits: disk full";
        vm.ShowStatus("Copied 1 turn.", isError: false);

        Assert.True(vm.HasSaveError);
        Assert.True(vm.HasStatus);
        Assert.NotEqual(vm.SaveError, vm.StatusMessage);
    }

    private sealed class SilentPlayer : IDualAudioPlayer
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
        public void Dispose() { MediaReady = null; MediaEnded = null; }
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ReadViewStatusTests" --nologo
```

Expected: FAIL to compile - `CS1061: 'ReadViewViewModel' does not contain a definition for
'HasStatus'` (and the same for `DialogReporter`).

- [ ] **Step 3: Add the pair to the read-view VM**

In `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`, add immediately after `HasSaveError`
(`:67`):

```csharp
    /// <summary>GENERAL dialog-local status for this window (Tier 1 plan D, T1-5, 2026-08-05),
    /// separate from SaveError above: that bar is titled "Couldn't save your edits" and carries
    /// exactly that. This one carries everything else that must be visible from the read view -
    /// a failed correction or speaker reassign in the two child dialogs (which previously
    /// reported to MainWindow's InfoBar, invisible from here) and Task 8's copy outcomes.
    /// Null = no status. REJECTED: folding SaveError into this pair - ReadViewEditModeTests pins
    /// its title and independence, and an edit failure must not be silently replaced by a copy
    /// notice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    /// <summary>True renders the status InfoBar as Error; false as Informational.</summary>
    [ObservableProperty] private bool _statusIsError;

    /// <summary>The status InfoBar's IsOpen binds here (a computed OneWay flag, since IsOpen
    /// cannot bind a null-check directly).</summary>
    public bool HasStatus => StatusMessage is not null;

    /// <summary>PUBLIC on purpose: ReadViewWindow's code-behind calls this when a child dialog
    /// or a Clipboard.SetText fails - both live in window code that this suite cannot execute,
    /// so the decidable state has to be reachable from the VM's public surface.</summary>
    public void ShowStatus(string message, bool isError) =>
        _dispatch(() => { StatusMessage = message; StatusIsError = isError; });

    public void ClearStatus() =>
        _dispatch(() => { StatusMessage = null; StatusIsError = false; });
```

- [ ] **Step 4: Add the second bar to the window**

In `src/LocalScribe.App/ReadViewWindow.xaml`, insert immediately after the existing SaveError
`ui:InfoBar` (`:36`):

```xml
        <!-- General read-view status (Tier 1 plan D, T1-5, 2026-08-05): the correction and
             reassign dialogs this window parents used to report to MainWindow's InfoBar, which
             they cannot show, and Task 8's copy outcomes land here too. A SECOND bar rather than
             a widened SaveError bar: that one is titled "Couldn't save your edits" and its
             independence is pinned by ReadViewEditModeTests. IsClosable=False, Severity via
             Style+DataTrigger (InfoBar.Severity is an enum DP that cannot bind a bool). -->
        <ui:InfoBar DockPanel.Dock="Top" Margin="0,0,0,8" IsClosable="False"
                    IsOpen="{Binding HasStatus, Mode=OneWay}"
                    Message="{Binding StatusMessage}">
            <ui:InfoBar.Style>
                <Style TargetType="{x:Type ui:InfoBar}">
                    <Setter Property="Severity" Value="Informational" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding StatusIsError}" Value="True">
                            <Setter Property="Severity" Value="Error" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </ui:InfoBar.Style>
        </ui:InfoBar>
```

- [ ] **Step 5: Point the three editor factories at a TEE reporter**

**The window does not construct these view models.** `ReadViewWindow.xaml.cs:548-590` only calls
`_vm.CreateCorrectionEditor(...)`, `_vm.CreateReassignEditor(...)` and
`_vm.CreateReassignClusterEditor(...)` and wraps the result in a dialog - there is no reporter
argument anywhere in that file. The three constructions live on the VM
(`ReadViewViewModel.cs:919`, `:928`, `:981`), and all three currently pass `_reporter`, the shell
reporter. The change therefore belongs in `ReadViewViewModel.cs`.

`CorrectTextViewModel` and `ReassignSpeakerViewModel` each take an `IUiErrorReporter`. Do NOT change
their signatures. Add a nested adapter and one field to
`src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`, beside the status pair from Step 3:

```csharp
    /// <summary>The reporter handed to the three editor dialogs this VM builds (Tier 1 plan D,
    /// T1-5, 2026-08-05). They previously took the shell reporter outright, so a failed
    /// correction or speaker reassign rendered on MainWindow's InfoBar - a window the user is not
    /// looking at and may not even have open.
    /// It TEES, it does not REPLACE: these two dialogs are the read view's evidentiary WRITE
    /// paths, and a failed write that reaches only a transient dialog bar is recorded nowhere -
    /// not in the shell queue the user can still read after the dialog closes, and not in Plan A's
    /// diagnostic log, which is attached behind InfoBarErrorReporter. See the plan's
    /// both-surfaces rule.
    /// REJECTED: widening the dialog VMs' signatures - CorrectTextViewModelTests and
    /// ReassignSpeakerViewModelTests are already correct against the interface, and nothing about
    /// those VMs needs to know where a bar lives.</summary>
    public IUiErrorReporter DialogReporter { get; }

    private sealed class TeeStatusReporter(ReadViewViewModel vm, IUiErrorReporter shell)
        : IUiErrorReporter
    {
        public void Report(string context, Exception ex)
        {
            vm.ShowStatus(context + ": " + ex.Message, isError: true);
            shell.Report(context, ex);
        }

        // Forward `privileged` through rather than dropping it (Plan A's shipped signature) -
        // teeing a privileged Info to the shell with the flag lost would silently UNMARK it, and
        // the shell's copy is the one that reaches diagnostics\.
        public void Info(string message, bool privileged = true)
        {
            vm.ShowStatus(message, isError: false);
            shell.Info(message, privileged);
        }

        // This overload's signature must match the interface member EXACTLY - adding a `privileged`
        // parameter here would make it a different method that no longer overrides the default
        // interface method, so an interface-typed caller would silently fall through to the DIM
        // body and lose the severity. `privileged` belongs on the Info(string, bool) member above;
        // the shell's own severity overload marks by default.
        public void Info(string message, NoticeSeverity severity)
        {
            vm.ShowStatus(message, severity == NoticeSeverity.Error);
            shell.Info(message, severity);
        }
    }
```

Assign it in the constructor, immediately after the existing tuple assignment (`:165`) so
`_reporter` and `_dispatch` are already set:

```csharp
        DialogReporter = new TeeStatusReporter(this, _reporter);
```

Then replace the `_reporter` argument with `DialogReporter` at exactly three sites, changing
nothing else on those lines:

- `:919` `return new CorrectTextViewModel(_maintenance, _reporter, SessionId, segments,`
  -> `return new CorrectTextViewModel(_maintenance, DialogReporter, SessionId, segments,`
- `:928` `return new ReassignSpeakerViewModel(_maintenance, _reporter, SessionId,`
  -> `return new ReassignSpeakerViewModel(_maintenance, DialogReporter, SessionId,`
- `:981` `return new ReassignSpeakerViewModel(_maintenance, _reporter, SessionId,`
  -> `return new ReassignSpeakerViewModel(_maintenance, DialogReporter, SessionId,`

Leave every OTHER `_reporter.Report(...)` in this file alone (`:101`, `:597`, `:614`, `:1041`,
`:1057`) - those are the VM's own load/refresh failures, which already have the SaveError bar or
belong on the shell.

Finally, in `src/LocalScribe.App/ReadViewWindow.xaml.cs`, add `_vm.ClearStatus();` immediately
before each `dialog.ShowDialog()` in `CorrectTextAsync` (`:554`), `ReassignSpeakerAsync` (`:564`)
and `ReassignClusterAsync` (`:583`), so a stale message never sits under a fresh attempt. That is
the ONLY edit this task makes to the window's code-behind.

`ReadViewViewModel.cs` already has `using LocalScribe.App.Services;` (`:6`), so `NoticeSeverity`
and `IUiErrorReporter` are both in scope.

- [ ] **Step 6: Run the tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ReadViewStatusTests|FullyQualifiedName~ReadViewEditModeTests|FullyQualifiedName~CorrectTextViewModelTests|FullyQualifiedName~ReassignSpeakerViewModelTests" --nologo
```
Expected: PASS - the 3 new facts plus every pre-existing fact in those three classes.

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/ReadViewViewModel.cs src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/ReadViewWindow.xaml.cs tests/LocalScribe.App.Tests/ReadViewStatusTests.cs
git commit -m "feat(readview): a general status bar, reused by the correction and reassign dialogs"
```

---

## Task 6: a failed Start is no longer invisible

`SessionViewModel.StartAsync` calls `_controller.StartAsync` with no try/catch, so a refusal - no
models on disk, a dead microphone, an engine already busy - propagates out of the
`AsyncRelayCommand` to the dispatcher, where it was swallowed. `LastNotice` exists but is bound in
NO XAML file anywhere; the only live notice surface is a tray balloon, which Focus Assist
suppresses.

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SessionViewModel.cs:49-50`, `:172`, `:307`, `:326-338`
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml:365-400`
- Create: `tests/LocalScribe.App.Tests/SessionNoticeTests.cs`

**Interfaces:**
- Consumes: `LiveTestDoubles.MakeController(string root, ...)` and `LiveTestDoubles.Options()`
  (linked into App.Tests via the csproj); `FakeProvider.ThrowOnNextMicCreate`.
- Produces, on `SessionViewModel`:
  - `string? NoticeText`, `bool NoticeIsError`, `bool HasNotice` (`=> NoticeText is not null`)
  - `IRelayCommand DismissNoticeCommand`
  `LastNotice` and `NoticeRaised` are LEFT IN PLACE - `TrayIconHost` subscribes to `NoticeRaised`
  and nothing else may change about the balloon.
- No later task consumes these.

**THE TRAP THIS TASK EXISTS AROUND:** `[ObservableProperty]` equality-gates a same-value set, so a
second identical notice never re-raises `PropertyChanged`. `TrayIconHost.cs:160-163` records this
verbatim and is the reason `NoticeRaised` exists at all. A bar bound naively to `LastNotice` would
silently fail to re-open after a repeat. `RaiseNotice` below nulls first, deliberately.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/SessionNoticeTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>A persistent notice surface on the live session VM (Tier 1 plan D, T1-5,
/// 2026-08-05). Before this, a failed StartAsync threw out of the AsyncRelayCommand into the
/// dispatcher handler that swallowed everything, and the only live notice surface in the product
/// was a tray balloon Focus Assist suppresses. LastNotice existed but was bound in no XAML at
/// all.</summary>
public sealed class SessionNoticeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-sessnotice-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task A_failed_start_is_surfaced_instead_of_thrown()
    {
        var (controller, provider, _, _) = LiveTestDoubles.MakeController(_root);
        provider.ThrowOnNextMicCreate = true;      // one-shot: CreateMic throws "mic gone"
        var vm = new SessionViewModel(controller, new Settings { StorageRoot = _root },
            dispatch: a => a(), startOptions: LiveTestDoubles.Options());

        await vm.StartCommand.ExecuteAsync(null);   // must NOT throw out of the command

        Assert.True(vm.HasNotice);
        Assert.True(vm.NoticeIsError);
        Assert.Equal(SessionState.Idle, vm.State);
    }

    [Fact]
    public void A_repeated_identical_notice_re_opens_a_dismissed_bar()
    {
        // THE trap (TrayIconHost.cs:160-163): [ObservableProperty] gates PropertyChanged on
        // equality, so a naive bar bound to a same-valued property would stay shut on a repeat.
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings { StorageRoot = _root },
            dispatch: a => a(), startOptions: LiveTestDoubles.Options());
        int opens = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.HasNotice)) opens++; };

        vm.RaiseNotice("Recording is degraded to the system mix.", isError: false);
        vm.DismissNoticeCommand.Execute(null);
        vm.RaiseNotice("Recording is degraded to the system mix.", isError: false);

        Assert.True(vm.HasNotice);                 // the SAME text re-opened the bar
        Assert.True(opens >= 3);                   // open, dismiss, re-open
    }

    [Fact]
    public void Dismiss_clears_both_halves_so_the_next_notice_starts_clean()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings { StorageRoot = _root },
            dispatch: a => a(), startOptions: LiveTestDoubles.Options());

        vm.RaiseNotice("Couldn't start recording: mic gone", isError: true);
        vm.DismissNoticeCommand.Execute(null);

        Assert.False(vm.HasNotice);
        Assert.False(vm.NoticeIsError);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SessionNoticeTests" --nologo
```

Expected: FAIL to compile - `CS1061: 'SessionViewModel' does not contain a definition for
'HasNotice'`.

**The `ThrowOnNextMicCreate` lever is the right one and it was verified, not assumed.**
`SessionController.StartAsync` (`:376`) opens the mic at `:456`
`(micSource, var micSnap) = _captureProvider.CreateMic(clock);` INSIDE the inner `try`, and that
`try`'s `catch` (`:663-685`) is a best-effort partial-start cleanup that ends in a bare `throw;`
(`:684`) - so the failure propagates out of `StartAsync` rather than degrading to the remote leg
alone. Its own comment records that `State` never leaves `Idle` on that path, which is what the
third assertion pins. `LiveTestDoubles.MakeController`'s default `availableModels` is
`{ "base.en", "tiny.en" }` (`LiveTestDoubles.cs:224`), which the default auto-plan resolves to, so
the earlier model-presence fail-fast (`:430-434`, which returns `null` with a `Notice` instead of
throwing) does NOT fire and execution really does reach `CreateMic`. Do not swap the lever.

- [ ] **Step 3: Add the notice pair and the unconditional raise**

In `src/LocalScribe.App/ViewModels/SessionViewModel.cs`, add after `_lastNotice` (`:50`):

```csharp
    /// <summary>The live console's PERSISTENT notice (Tier 1 plan D, T1-5, 2026-08-05). Distinct
    /// from LastNotice, which is bound in NO XAML anywhere and exists only to feed the tray
    /// balloon - and a balloon is suppressed outright by Focus Assist, which is how a failed
    /// Start became completely invisible. Set only through RaiseNotice below.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _noticeText;

    /// <summary>True renders the console notice InfoBar as Error; false as Informational.</summary>
    [ObservableProperty] private bool _noticeIsError;

    /// <summary>The console notice InfoBar's IsOpen binds here.</summary>
    public bool HasNotice => NoticeText is not null;

    /// <summary>PUBLIC so SessionNoticeTests can drive the equality-gate case directly, and so
    /// any future console-side caller uses the ONE correct path.
    /// The null-first assignment is load-bearing, not defensive tidying: [ObservableProperty]
    /// equality-gates a same-value set (TrayIconHost.cs:160-163 records the trap verbatim - it is
    /// exactly why NoticeRaised exists), so re-raising the SAME text after the user dismissed the
    /// bar would raise no PropertyChanged and the bar would stay shut. Severity is set FIRST so
    /// the bar never opens for one dispatcher turn wearing the previous message's colour.
    /// REJECTED: binding IsOpen straight to LastNotice - that IS the trap, not the fix.</summary>
    public void RaiseNotice(string text, bool isError)
    {
        NoticeIsError = isError;
        NoticeText = null;
        NoticeText = text;
    }
```

Add the command declaration beside the other commands and construct it in the constructor:

```csharp
    /// <summary>Dismisses the console notice. This bar IS closable (unlike the state-driven
    /// dialog bars): a notice records something that already happened, so only the user can
    /// decide it has been read.</summary>
    public IRelayCommand DismissNoticeCommand { get; }
```
```csharp
        DismissNoticeCommand = new RelayCommand(() =>
            _dispatch(() => { NoticeText = null; NoticeIsError = false; }));
```

Extend the controller-notice subscription (`:172`) - keep both existing assignments untouched:

```csharp
        controller.Notice += n => _dispatch(() =>
        {
            LastNotice = n;
            NoticeRaised?.Invoke(n);
            RaiseNotice(n, isError: false);     // advisory - the controller's own Notices are informational
        });
```

Extend the `SwitchRemoteTargetAsync` catch (`:335`):

```csharp
            _dispatch(() =>
            {
                LastNotice = ex.Message;
                NoticeRaised?.Invoke(ex.Message);
                RaiseNotice(ex.Message, isError: true);
            });
```

- [ ] **Step 4: Stop throwing out of Start**

Wrap the controller call in `StartAsync` (`:307`):

```csharp
        string? id;
        try
        {
            id = await Task.Run(() => _controller.StartAsync(options, CancellationToken.None));
        }
        catch (Exception ex)
        {
            // T1-5 (2026-08-05): this call had NO try/catch, so a refused Start - no models on
            // disk, a dead microphone, a re-transcription holding the engine - threw out of the
            // AsyncRelayCommand onto the dispatcher, where App.xaml.cs:55 swallowed it and the
            // user saw a Record button that simply did nothing. Surface it on the console bar AND
            // on the balloon; leave State alone (the controller has already unwound to Idle).
            _dispatch(() =>
            {
                LastNotice = ex.Message;
                NoticeRaised?.Invoke(ex.Message);
                RaiseNotice(ex.Message, isError: true);
            });
            return;
        }
        if (id is not null) _startedAt = _time.GetUtcNow();
```

- [ ] **Step 5: Bind it into the live console**

In `src/LocalScribe.App/LiveViewWindow.xaml`, insert as the FIRST child of the warning-row
`StackPanel` (`:365`, immediately after its opening tag):

```xml
                    <!-- Persistent notice (Tier 1 plan D, T1-5, 2026-08-05). A failed Start, a
                         capture-scope refusal and every controller Notice previously surfaced
                         ONLY as a tray balloon, which Focus Assist suppresses outright - so on a
                         locked-down machine they were invisible. IsClosable=True because a notice
                         records something that already happened; the VM's RaiseNotice nulls the
                         text first so a REPEATED identical notice still re-opens a dismissed bar
                         (the [ObservableProperty] equality gate, TrayIconHost.cs:160-163).
                         Severity rides a Style+DataTrigger - InfoBar.Severity is an enum DP that
                         cannot bind a bool. -->
                    <ui:InfoBar Margin="0,0,0,8" IsClosable="True"
                                IsOpen="{Binding Session.HasNotice, Mode=OneWay}"
                                Message="{Binding Session.NoticeText}">
                        <ui:InfoBar.Style>
                            <Style TargetType="{x:Type ui:InfoBar}">
                                <Setter Property="Severity" Value="Informational" />
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding Session.NoticeIsError}" Value="True">
                                        <Setter Property="Severity" Value="Error" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </ui:InfoBar.Style>
                    </ui:InfoBar>
```

`IsClosable="True"` flips `IsOpen` to false on the user's click without telling the VM - the same
Wpf.Ui 4.0.3 behaviour `MainWindow` works around with a `DependencyPropertyDescriptor`. That is
acceptable HERE and must not be "fixed": `RaiseNotice`'s null-first assignment re-raises
`PropertyChanged` on the next notice, so the bar re-opens even for identical text.
`DismissNoticeCommand` exists for a future explicit dismiss affordance and for the test above.

The `ui:` namespace is already declared at `LiveViewWindow.xaml:4`.

- [ ] **Step 6: Run the tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SessionNoticeTests|FullyQualifiedName~SessionViewModel|FullyQualifiedName~XamlHygieneTests" --nologo
```
**No isolated `BaseOutputPath` on this command** (no command in this plan uses one - see Global Constraints): the filter includes
`XamlHygieneTests`, which reads repo source through `RepoPaths.SolutionRoot()` and would walk past
a Temp output path - see the Global Constraints note.

Expected: PASS - the 3 new facts, every pre-existing `SessionViewModel*` fact, and the XAML hygiene
gate (the new markup uses only theme brushes via `ui:InfoBar` defaults - no `#RRGGBB` literal).

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/SessionViewModel.cs src/LocalScribe.App/LiveViewWindow.xaml tests/LocalScribe.App.Tests/SessionNoticeTests.cs
git commit -m "feat(live): persistent console notice so a failed Start is no longer invisible"
```

---

## Task 7: `TranscriptCitation` - the pure clipboard payload composer

The citation string is one shape and must exist in exactly one place. It composes only from values
`MetadataFormat`/`ExportProvenance` already produce, and it reuses the two existing timestamp
formatters rather than inventing a third.

**Files:**
- Create: `src/LocalScribe.Core/Projection/TranscriptCitation.cs`
- Create: `tests/LocalScribe.Core.Tests/TranscriptCitationTests.cs`

**Interfaces:**
- Consumes (both already exist, unchanged):
  - `LocalScribe.Core.Assistant.AssistantCitationFormat.Format(long startMs) : string` - canonical
    zero-padded `HH:MM:SS`, invariant, TRUNCATED never rounded ("a rounded-up anchor could point
    past the segment start").
  - `LocalScribe.Core.Model.TranscriptVersions.ShortId(string versionId) : string` - `"v2-base.en-
    2026-07-13"` -> `"v2"`; `ShortId("v1")` returns `"v1"`, so originals need no special case.
  - `LocalScribe.Core.Projection.DisplayRow` - `IsMarker`, `StartMs`, `DisplayName`, `Text`.
- Produces:
  - `TranscriptCitation.Nl : string` - `"\r\n"`.
  - `TranscriptCitation.Format(DisplayRow row, string sessionTitle, DateTimeOffset startedAtLocal, string versionId) : string`
  - `TranscriptCitation.PlainText(IReadOnlyList<DisplayRow> rows) : string`
  - `TranscriptCitation.WithCitations(IReadOnlyList<DisplayRow> rows, string sessionTitle, DateTimeOffset startedAtLocal, string versionId) : string`
  Task 8 calls `PlainText` and `WithCitations`.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.Core.Tests/TranscriptCitationTests.cs` (Core.Tests has a global
`<Using Include="Xunit" />` - do NOT write `using Xunit;` here):

```csharp
using LocalScribe.Core.Projection;

namespace LocalScribe.Core.Tests;

/// <summary>The read view's two clipboard payloads (Tier 1 plan D, T1-9, 2026-08-05). The
/// citation shape is defined ONCE, here, and composes only from values MetadataFormat /
/// ExportProvenance already produce. Pure and Core-side so it is testable without any window -
/// the App suite has no STA harness, so a payload built in code-behind would be untestable.</summary>
public sealed class TranscriptCitationTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 14, 9, 0, 0, TimeSpan.Zero);

    private static DisplayRow Turn(long startMs, string? name, string text)
        => new() { StartMs = startMs, EndMs = startMs + 4000, DisplayName = name, Text = text };

    private static DisplayRow Marker(long startMs, string text)
        => new() { IsMarker = true, StartMs = startMs, EndMs = startMs, Text = text };

    [Fact]
    public void A_citation_is_quote_speaker_stamp_session_and_version()
    {
        string cite = TranscriptCitation.Format(
            Turn(2_472_000, "J. Smith", "I never signed that."),
            "R v Smith call", Start, "v2-large-v3-turbo-2026-07-14");

        Assert.Equal(
            "\"I never signed that.\" - J. Smith, 00:41:12, R v Smith call of 2026-07-14 (transcript v2)",
            cite);
    }

    [Fact]
    public void An_original_transcript_cites_as_v1_with_no_special_casing()
    {
        // TranscriptVersions.ShortId("v1") returns "v1" - the same call handles both.
        string cite = TranscriptCitation.Format(Turn(0, "Me", "Morning."), "Doe intake", Start, "v1");
        Assert.EndsWith("(transcript v1)", cite);
    }

    [Fact]
    public void The_stamp_is_truncated_never_rounded()
    {
        // AssistantCitationFormat's locked rule: a rounded-up anchor could point PAST the segment
        // start, so 41:12.900 cites as 00:41:12, not 00:41:13.
        string cite = TranscriptCitation.Format(Turn(2_472_900, "J. Smith", "x"), "T", Start, "v1");
        Assert.Contains("00:41:12", cite);
        Assert.DoesNotContain("00:41:13", cite);
    }

    [Fact]
    public void An_unnamed_turn_drops_the_speaker_clause_rather_than_citing_an_empty_name()
    {
        string cite = TranscriptCitation.Format(Turn(1000, null, "unattributed"), "T", Start, "v1");
        Assert.Equal("\"unattributed\" - 00:00:01, T of 2026-07-14 (transcript v1)", cite);
    }

    [Fact]
    public void Plain_text_is_the_turn_text_verbatim_one_row_per_line_with_crlf()
    {
        string text = TranscriptCitation.PlainText(
            [Turn(0, "Me", "one"), Turn(5000, "Them", "two")]);

        Assert.Equal("one\r\ntwo", text);
    }

    [Fact]
    public void Markers_are_skipped_by_both_payloads()
    {
        // Extended selection means a marker row CAN be inside SelectedItems even though the row
        // context menu is suppressed for markers (ReadViewWindow.xaml.cs:542-546). A marker is
        // machine bookkeeping, not evidence a solicitor quotes.
        DisplayRow[] rows = [Turn(0, "Me", "one"), Marker(1000, "Recording paused"), Turn(5000, "Me", "two")];

        Assert.Equal("one\r\ntwo", TranscriptCitation.PlainText(rows));
        string cited = TranscriptCitation.WithCitations(rows, "T", Start, "v1");
        Assert.DoesNotContain("Recording paused", cited);
    }

    [Fact]
    public void Multiple_citations_are_blank_line_separated_in_row_order()
    {
        string cited = TranscriptCitation.WithCitations(
            [Turn(0, "Me", "one"), Turn(5000, "Them", "two")], "T", Start, "v1");

        Assert.Equal(
            "\"one\" - Me, 00:00:00, T of 2026-07-14 (transcript v1)\r\n\r\n"
            + "\"two\" - Them, 00:00:05, T of 2026-07-14 (transcript v1)",
            cited);
    }

    [Fact]
    public void An_empty_or_marker_only_selection_yields_an_empty_string_not_a_stray_separator()
    {
        Assert.Equal("", TranscriptCitation.PlainText([]));
        Assert.Equal("", TranscriptCitation.WithCitations([Marker(0, "Recording paused")], "T", Start, "v1"));
    }

    [Fact]
    public void Row_text_is_emitted_verbatim_and_is_never_trimmed_or_reflowed()
    {
        // Transcripts are evidence: a copy path may not silently normalise what it copies.
        const string awkward = "  spaced   out  ";
        Assert.Equal(awkward, TranscriptCitation.PlainText([Turn(0, "Me", awkward)]));
        Assert.Contains("\"" + awkward + "\"", TranscriptCitation.Format(Turn(0, "Me", awkward), "T", Start, "v1"));
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~TranscriptCitationTests" --nologo
```

Expected: FAIL to compile - `CS0103: The name 'TranscriptCitation' does not exist in the current
context`.

- [ ] **Step 3: Write the composer**

Create `src/LocalScribe.Core/Projection/TranscriptCitation.cs`:

```csharp
using System.Globalization;
using System.Linq;
using System.Text;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.Projection;

/// <summary>The read view's two clipboard payloads (Tier 1 plan D, T1-9, 2026-08-05). Pure and
/// Core-side on purpose: the App test suite has no STA/dispatcher harness, so anything composed
/// inside ReadViewWindow's code-behind would be permanently untestable - the window keeps only
/// the Clipboard.SetText call.
///
/// The citation shape is:
///   "&lt;text&gt;" - &lt;speaker&gt;, &lt;HH:MM:SS&gt;, &lt;title&gt; of &lt;yyyy-MM-dd&gt; (transcript v&lt;n&gt;)
/// Every component already exists elsewhere in the product, and NONE of them is re-derived here:
/// the stamp is AssistantCitationFormat.Format (the canonical anchor - truncated, never rounded,
/// because a rounded-up anchor could point past the segment start), and the version is
/// TranscriptVersions.ShortId, the same short form MetadataFormat.VersionLine prints in exports.
/// REJECTED: TimestampFormat.Stamp - it emits mm:ss below one hour and follows the user's
/// relative/wallclock preference, so two solicitors quoting the same turn would produce two
/// different anchors. A citation must be stable.
///
/// Rows arrive pre-resolved from TranscriptProjection.Build and their Text is emitted VERBATIM -
/// never trimmed, wrapped or reflowed. Transcripts are evidence and a copy path is not allowed to
/// tidy one.</summary>
public static class TranscriptCitation
{
    /// <summary>CRLF: the clipboard's consumers here are Word, Outlook and Windows tooling - the
    /// same reasoning that put CRLF in PlainTextRenderer.Write.</summary>
    public const string Nl = "\r\n";

    /// <summary>One row as an attributable quotation. A marker row has no speaker and no
    /// evidentiary text; callers filter markers out before reaching here, and the two batch
    /// helpers below do it for them.</summary>
    public static string Format(DisplayRow row, string sessionTitle, DateTimeOffset startedAtLocal,
        string versionId)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(row.Text).Append("\" - ");
        // An unnamed turn drops the clause entirely rather than citing an empty name: an
        // unattributed line is honest, a line attributed to "" is not.
        if (!string.IsNullOrEmpty(row.DisplayName)) sb.Append(row.DisplayName).Append(", ");
        sb.Append(AssistantCitationFormat.Format(row.StartMs)).Append(", ");
        sb.Append(sessionTitle).Append(" of ");
        sb.Append(startedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sb.Append(" (transcript ").Append(TranscriptVersions.ShortId(versionId)).Append(')');
        return sb.ToString();
    }

    /// <summary>"Copy text": the turn text alone, one row per line. No speaker and no stamp -
    /// this is literally the TEXT; attribution is what the other command is for.</summary>
    public static string PlainText(IReadOnlyList<DisplayRow> rows)
        => string.Join(Nl, rows.Where(Quotable).Select(r => r.Text));

    /// <summary>"Copy with citation": one citation per selected row, in row order, separated by a
    /// blank line so each survives being pasted into a numbered paragraph on its own.</summary>
    public static string WithCitations(IReadOnlyList<DisplayRow> rows, string sessionTitle,
        DateTimeOffset startedAtLocal, string versionId)
        => string.Join(Nl + Nl, rows.Where(Quotable)
            .Select(r => Format(r, sessionTitle, startedAtLocal, versionId)));

    /// <summary>Markers are machine bookkeeping ("Recording paused"), not evidence anyone quotes.
    /// Extended selection means one CAN be inside SelectedItems even though the row context menu
    /// is suppressed over markers (ReadViewWindow.xaml.cs:542-546), so both payloads filter.</summary>
    private static bool Quotable(DisplayRow row) => !row.IsMarker;
}
```

- [ ] **Step 4: Run the test and confirm it passes**

```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~TranscriptCitationTests" --nologo
```
Expected: PASS (9 facts).

- [ ] **Step 5: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Core/Projection/TranscriptCitation.cs tests/LocalScribe.Core.Tests/TranscriptCitationTests.cs
git commit -m "feat(readview): TranscriptCitation, the one place the citation shape is defined"
```

---

## Task 8: selectable read-view rows plus Copy text / Copy with citation

Read rows render into non-selectable `TextBlock`s (`ReadViewWindow.xaml:501-541`), `RowList` sets no
`SelectionMode` (WPF's default `Single`), and there is no copy affordance anywhere in the read view.
A solicitor cannot get a quotation out of the app except by exporting the whole transcript.

### The WPF approach, and what it is chosen over

**Chosen: `SelectionMode="Extended"` on the existing virtualised `ListView`, plus two commands over
`SelectedItems` and two key bindings.** Row prose keeps its current `TextBlock` +
`SegmentText` rendering byte-for-byte. Selection granularity is the TURN, which is also the unit a
citation attributes, so the two features agree.

Rejected, each for a concrete reason:

- **One `FlowDocumentScrollViewer` over the whole transcript.** A `FlowDocument` paginates its
  entire content, so every row materialises - this list is `VirtualizingPanel.IsVirtualizing="True"`
  with `VirtualizationMode="Recycling"` and `ScrollUnit="Pixel"` precisely because a long call holds
  thousands of rows. This is the option virtualisation forbids outright.
- **A read-only `TextBox`/`RichTextBox` per row.** Perfectly affordable under recycling (only the
  ~30 realised rows exist), but it DESTROYS `SegmentText` - the attached behaviour that owns
  `TextBlock.Inlines` and gives each segment a hover `[mm:ss]` tooltip, a double-click seek and a
  now-playing tint. Those are shipped navigation features (ITEM 5), and trading them for character
  selection is a regression, not a fix.
- **The `TextEditorWrapper` reflection trick** that enables selection on a `TextBlock` by reaching
  into `System.Windows.Documents.TextEditor`. It binds the product to a private WPF type.
- **Character-level selection is therefore NOT delivered by this task**, and the plan says so
  rather than implying otherwise: row-granular copy solves the stated user problem (get the words
  out, attributably) without giving up virtualisation or segment navigation. A solicitor can copy
  a whole turn but still cannot select a phrase inside one. This is a DELIBERATE, DOCUMENTED
  scope reduction against T1-9, not an oversight - **record it in
  `docs/superpowers/specs/2026-08-05-tier1-hardening-design.md` as an explicit Tier-2 follow-up
  when this task lands**, so the gap is written down somewhere the next round reads rather than
  left implied by a rejection list inside a merged plan.

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs:143` (expose the loaded version)
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml:422-431` (selection mode + key bindings),
  `:442-500` (two context-menu items)
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml.cs:71-95`, `:113-132` (two commands)
- Create: `tests/LocalScribe.App.Tests/ReadViewCopyTests.cs`

**Interfaces:**
- Consumes: `TranscriptCitation.PlainText` / `.WithCitations` (Task 7);
  `ReadViewViewModel.ShowStatus(string, bool)` and `ReadViewViewModel.DialogReporter` (Task 5);
  `ReadViewViewModel.Title`, `.StartedAtLocal` (both already public); the `WindowProxy`
  `BindingProxy` already declared at `ReadViewWindow.xaml:23`.
- Produces:
  - `ReadViewViewModel.LoadedVersionId : string` - read-only property over the existing
    `_loadedVersionId` field.
  - `ReadViewViewModel.RowsForCopy(ReadRow? clicked, IReadOnlyList<ReadRow> selected) : IReadOnlyList<DisplayRow>`
    - the pure selection rule.
  - `ReadViewWindow.CopyTextCommand : IRelayCommand<ReadRow>` and
    `ReadViewWindow.CopyWithCitationCommand : IRelayCommand<ReadRow>`.
  No later task consumes these.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/ReadViewCopyTests.cs`:

```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The read view's copy selection rule (Tier 1 plan D, T1-9, 2026-08-05). Only the
/// DECIDABLE half is testable: RowsForCopy is a pure method on the WPF-free VM, while the
/// Clipboard.SetText call and the ListView SelectedItems read stay in window code this suite
/// cannot execute (no STA harness). The payload itself is pinned by
/// TranscriptCitationTests in Core.</summary>
public sealed class ReadViewCopyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-copy-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ReadViewViewModel MakeVm()
    {
        var paths = new StoragePaths(_root);
        var settings = new FakeSettingsService(new Settings { StorageRoot = _root });
        var maintenance = new MaintenanceService(paths, settings, new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero)));
        return new ReadViewViewModel(maintenance, paths, settings, new FakeUiErrorReporter(),
            new SilentPlayer(), dispatch: a => a(), TimeProvider.System);
    }

    private static ReadRow Row(long startMs, string name, string text)
        => new(new DisplayRow { StartMs = startMs, EndMs = startMs + 1000, DisplayName = name, Text = text });

    [Fact]
    public void A_right_click_inside_the_selection_copies_the_whole_selection()
    {
        var vm = MakeVm();
        var a = Row(0, "Me", "one");
        var b = Row(5000, "Them", "two");

        var picked = vm.RowsForCopy(clicked: b, selected: [a, b]);

        Assert.Equal(new[] { "one", "two" }, picked.Select(r => r.Text));
    }

    [Fact]
    public void A_right_click_OUTSIDE_the_selection_copies_only_the_clicked_row()
    {
        // WPF does not re-select on right-click, so the clicked row can be outside SelectedItems.
        // Copying the invisible selection instead of what was clicked would be a silent surprise.
        var vm = MakeVm();
        var a = Row(0, "Me", "one");
        var b = Row(5000, "Them", "two");
        var c = Row(9000, "Me", "three");

        var picked = vm.RowsForCopy(clicked: c, selected: [a, b]);

        Assert.Equal(new[] { "three" }, picked.Select(r => r.Text));
    }

    [Fact]
    public void A_keyboard_copy_with_no_clicked_row_falls_back_to_the_selection()
    {
        var vm = MakeVm();
        var a = Row(0, "Me", "one");

        var picked = vm.RowsForCopy(clicked: null, selected: [a]);

        Assert.Equal(new[] { "one" }, picked.Select(r => r.Text));
    }

    [Fact]
    public void Nothing_clicked_and_nothing_selected_yields_nothing_to_copy()
    {
        var vm = MakeVm();
        Assert.Empty(vm.RowsForCopy(clicked: null, selected: []));
    }

    [Fact]
    public void Selection_order_follows_the_transcript_not_the_click_order()
    {
        // Ctrl-clicking bottom-up must still cite in transcript order - a quotation block that
        // reorders the record is exactly what an evidentiary product must not produce.
        var vm = MakeVm();
        var a = Row(0, "Me", "one");
        var b = Row(5000, "Them", "two");

        var picked = vm.RowsForCopy(clicked: null, selected: [b, a]);

        Assert.Equal(new[] { "one", "two" }, picked.Select(r => r.Text));
    }

    [Fact]
    public void The_loaded_version_is_readable_for_the_citation()
    {
        var vm = MakeVm();
        Assert.Equal(TranscriptVersions.Root, vm.LoadedVersionId);   // "v1" before any load
    }

    private sealed class SilentPlayer : IDualAudioPlayer
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
        public void Dispose() { MediaReady = null; MediaEnded = null; }
    }
}
```

Add `using System.Linq;` to the file's using block (the assertions call `.Select`).

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ReadViewCopyTests" --nologo
```

Expected: FAIL to compile - `CS1061: 'ReadViewViewModel' does not contain a definition for
'RowsForCopy'` and the same for `LoadedVersionId`.

- [ ] **Step 3: Add the version accessor and the selection rule**

In `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`, add immediately after the
`_loadedVersionId` field declaration (`:143`):

```csharp
    /// <summary>The transcript version the current Rows were projected from. PUBLIC because a
    /// copied quotation must name the version it came from - a correction landing in v2 changes
    /// the words, and a citation that does not say which version was quoted is not checkable
    /// (Tier 1 plan D, T1-9, 2026-08-05).</summary>
    public string LoadedVersionId => _loadedVersionId;

    /// <summary>Which rows a copy command acts on (Tier 1 plan D, T1-9, 2026-08-05). Pure and on
    /// the VM, not in the window, because this suite has no STA harness and a rule buried in
    /// code-behind would be untestable.
    ///
    /// WPF does NOT move the selection on a right-click, so the clicked row can sit outside
    /// SelectedItems - copying the invisible selection in that case would be a silent surprise,
    /// so the clicked row wins unless it is part of the selection. A keyboard copy passes null
    /// and falls back to the selection outright.
    ///
    /// The result is re-ordered by StartMs, never by click order: a quotation block that reorders
    /// the record is exactly what an evidentiary product must not emit.</summary>
    public IReadOnlyList<DisplayRow> RowsForCopy(ReadRow? clicked, IReadOnlyList<ReadRow> selected)
    {
        var rows = clicked is not null && !selected.Contains(clicked)
            ? [clicked]
            : selected.ToList();
        return rows.Select(r => r.Data).OrderBy(d => d.StartMs).ToList();
    }
```

`ReadViewViewModel.cs` already imports `System.Linq` and `LocalScribe.Core.Projection`.

- [ ] **Step 4: Add the two window commands**

In `src/LocalScribe.App/ReadViewWindow.xaml.cs`, declare beside the other row commands (`:71-95`):

```csharp
    /// <summary>Row copy commands (Tier 1 plan D, T1-9, 2026-08-05). On the WINDOW, like every
    /// other WindowProxy row command, because they read RowList.SelectedItems and call
    /// Clipboard.SetText - both WPF. The two decidable halves are elsewhere and are tested:
    /// ReadViewViewModel.RowsForCopy picks the rows, TranscriptCitation composes the payload.</summary>
    public IRelayCommand<ReadRow> CopyTextCommand { get; }
    public IRelayCommand<ReadRow> CopyWithCitationCommand { get; }
```

Construct them in the constructor BEFORE `InitializeComponent()` (`:113-132`), closing over the
`vm` parameter, not the not-yet-assigned `_vm` field:

```csharp
        CopyTextCommand = new RelayCommand<ReadRow>(row => Copy(vm, row, withCitation: false));
        CopyWithCitationCommand = new RelayCommand<ReadRow>(row => Copy(vm, row, withCitation: true));
```

Add the helper as a private method on the window:

```csharp
    /// <summary>Composes the payload off the VM and writes it to the clipboard. Clipboard.SetText
    /// can throw COMException when another process holds the clipboard open (a known Windows
    /// behaviour, not an exotic one), so it is guarded and the failure goes through the VM's
    /// DialogReporter - which TEES to this window's status bar AND to the shell queue, per the
    /// plan's both-surfaces rule. A bar-only failure would be recorded nowhere once the read view
    /// closes, including in Plan A's diagnostic log.</summary>
    private void Copy(ReadViewViewModel vm, ReadRow? clicked, bool withCitation)
    {
        var rows = vm.RowsForCopy(clicked, RowList.SelectedItems.Cast<ReadRow>().ToList());
        string payload = withCitation
            ? TranscriptCitation.WithCitations(rows, vm.Title, vm.StartedAtLocal, vm.LoadedVersionId)
            : TranscriptCitation.PlainText(rows);
        if (payload.Length == 0)
        {
            vm.ShowStatus("Select one or more turns to copy.", isError: false);
            return;
        }
        try
        {
            Clipboard.SetText(payload);
            int count = rows.Count(r => !r.IsMarker);
            vm.ShowStatus(count == 1 ? "Copied 1 turn." : "Copied " + count + " turns.", isError: false);
        }
        catch (Exception ex)
        {
            // BOTH surfaces, via the tee built in Task 5: the bar renders
            // "Couldn't copy to the clipboard: <reason>" and the same failure is queued for the
            // shell, where Plan A's diagnostic log picks it up.
            vm.DialogReporter.Report("Couldn't copy to the clipboard", ex);
        }
    }
```

Add `using LocalScribe.Core.Projection;` and `using System.Windows;` to `ReadViewWindow.xaml.cs` if
either is missing (`System.Linq` and `System.Windows.Controls` are already there).

- [ ] **Step 5: Wire the list, the menu and the keys**

In `src/LocalScribe.App/ReadViewWindow.xaml`, add to the `RowList` `ListView` element (`:422-431`):

```xml
                  SelectionMode="Extended"
```

and, as a child of the `ListView`:

```xml
            <!-- Ctrl+C copies the words; Ctrl+Shift+C copies them with an attributable citation.
                 Routed through WindowProxy (declared at :23, Data assigned to the window in the
                 ctor at ReadViewWindow.xaml.cs:145) - NOT ElementName=Self. An InputBinding is a
                 Freezable and is in neither the visual nor the logical tree, so ElementName
                 resolution depends on the Freezable inheritance context reaching the window's
                 NameScope; when that link is not established the binding fails SILENTLY and the
                 gesture simply does nothing. There is no STA harness in this suite, so no test can
                 catch a silent no-op - hence the mechanism this file already proves for its
                 Style.Setter ContextMenu. A null CommandParameter means "no clicked row", which
                 RowsForCopy resolves to the current selection. -->
            <ListView.InputBindings>
                <KeyBinding Key="C" Modifiers="Control"
                            Command="{Binding Data.CopyTextCommand, Source={StaticResource WindowProxy}}" />
                <KeyBinding Key="C" Modifiers="Control+Shift"
                            Command="{Binding Data.CopyWithCitationCommand, Source={StaticResource WindowProxy}}" />
            </ListView.InputBindings>
```

Add two entries to the row `ContextMenu` (inside the `ItemContainerStyle` `Setter.Value`, after the
`Reassign all of this speaker...` item and before the existing `<Separator />`):

```xml
                                <Separator />
                                <MenuItem Header="Copy text"
                                          Command="{Binding Data.CopyTextCommand, Source={StaticResource WindowProxy}}"
                                          CommandParameter="{Binding}"
                                          InputGestureText="Ctrl+C" />
                                <MenuItem Header="Copy with citation"
                                          Command="{Binding Data.CopyWithCitationCommand, Source={StaticResource WindowProxy}}"
                                          CommandParameter="{Binding}"
                                          InputGestureText="Ctrl+Shift+C" />
```

Neither item sets `IsEnabled="{Binding HasSegments}"` - unlike the four editing items above, copying
a live row that has no per-segment overlay yet is perfectly meaningful, and `OnRowContextMenuOpening`
(`ReadViewWindow.xaml.cs:542-546`) already suppresses the whole menu over marker rows.

**Do NOT bind the row highlight to `SelectedIndex` or write `SelectedIndex` from the VM.** The
"now playing" tint lives on each row's own `IsNowPlaying` flag precisely because binding it to
selection meant the VM and the user's click wrote the same property, discarding a real selection
every ~150 ms (`ReadViewWindow.xaml:404-409`). Extended selection makes that regression easier to
reintroduce, not harder.

- [ ] **Step 6: Run the tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo
```
Expected: PASS - the App project in full (no isolated `BaseOutputPath`: `XamlHygieneTests` walks up
from the output directory to `.git` and produces 5 false failures from a Temp path).

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/ReadViewViewModel.cs src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/ReadViewWindow.xaml.cs tests/LocalScribe.App.Tests/ReadViewCopyTests.cs
git commit -m "feat(readview): extended row selection with Copy text and Copy with citation"
```

---

## Task 9: pin the zero-network property with a test, BEFORE anything can break it

A grep for `System.Net|HttpClient|Socket|WebRequest|Dns` across `src/LocalScribe.App` and
`src/LocalScribe.Core` returns zero matches today, and that mechanical checkability is the
product's most valuable privacy claim. Tasks 10-12 add a downloader and Task 13 adds a comment that
is one careless word away from breaking the pin. This test lands FIRST so the property is guarded
while it is still true.

**Files:**
- Create: `tests/LocalScribe.App.Tests/NoNetworkInAppOrCoreTests.cs`

**Interfaces:**
- Consumes: `RepoPaths.SolutionRoot()` (`tests/LocalScribe.App.Tests/XamlHygieneTests.cs`).
- Produces: nothing consumed by later tasks. Tasks 10, 11, 12 **and 13** must keep it green.

**`obj/` and `bin/` MUST be excluded.** The SDK generates
`obj/<config>/<tfm>/LocalScribe.Core.GlobalUsings.g.cs` containing
`global using System.Net.Http;` from `ImplicitUsings`. That is not source, it is not a network call,
and a scan that trips over it would be useless.

**The pattern matches COMMENTS.** Nothing written into these two projects may contain the words
being searched for - not even to state that the code does not use them. Say "the network stack",
"the fetch helper" or "Velopack's updater type". This constrains every comment in Tasks 10, 11, 12
**and 13**: Task 13 adds a comment to `App.xaml.cs`, which is inside the scanned tree, and the
obvious wording for it names the banned updater type outright. Task 13's Step 7 re-runs this class
for exactly that reason.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/NoNetworkInAppOrCoreTests.cs`:

```csharp
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The zero-network property, pinned (Tier 1 plan D, T1-10, 2026-08-05). A grep for
/// the network-stack namespaces and types over the two shipping projects a user actually runs
/// returns ZERO matches, and that is checkable by anyone in one command - it is the strongest
/// form the product's privacy claim can take. Tier 1D adds a component downloader, which lives
/// in a SEPARATE helper executable spawned on explicit user action (the ProcessDiarisationHelper
/// pattern), precisely so this stays at zero. An in-process HTTP client is REJECTED regardless
/// of convenience.
///
/// obj/ and bin/ are excluded because the SDK writes
/// obj/&lt;cfg&gt;/&lt;tfm&gt;/LocalScribe.Core.GlobalUsings.g.cs containing a generated global
/// using for the HTTP namespace (a consequence of ImplicitUsings, not of any call). Generated
/// output is not source and tripping over it would make this test worthless.
///
/// UpdateManager is pinned alongside: Velopack is referenced for INSTALL hooks only, and the
/// spec's out-of-scope list rules out in-process auto-update. Constructing an updater would be
/// the first line of network code back into the app.</summary>
public sealed class NoNetworkInAppOrCoreTests
{
    private static readonly Regex Forbidden = new(
        @"System\.Net|HttpClient|Socket|WebRequest|\bDns\b|UpdateManager", RegexOptions.Compiled);

    private static IEnumerable<string> ShippingSources(string projectFolder)
    {
        string root = Path.Combine(RepoPaths.SolutionRoot(), "src", projectFolder);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)
                     && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("LocalScribe.App")]
    [InlineData("LocalScribe.Core")]
    public void No_shipping_source_file_names_the_network_stack(string projectFolder)
    {
        var hits = new List<string>();
        foreach (string file in ShippingSources(projectFolder))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (Forbidden.IsMatch(lines[i]))
                    hits.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.True(hits.Count == 0,
            $"{projectFolder} must contain no network-stack references. Found {hits.Count}:"
            + Environment.NewLine + string.Join(Environment.NewLine, hits)
            + Environment.NewLine
            + "The downloader belongs in src/LocalScribe.Fetch, spawned as a stdio child. "
            + "If this fired on a COMMENT, reword it - the claim is grep-checkable, so the grep "
            + "must stay clean of the words themselves.");
    }

    [Fact]
    public void The_scan_actually_covers_a_meaningful_number_of_files()
    {
        // A guard on the guard: if a path change silently made ShippingSources enumerate nothing,
        // the two facts above would pass vacuously and the property would be unprotected.
        Assert.True(ShippingSources("LocalScribe.App").Count() > 50);
        Assert.True(ShippingSources("LocalScribe.Core").Count() > 100);
    }
}
```

- [ ] **Step 2: Run it and confirm it PASSES**

This is the one test in this plan that must be green on first run - it pins an existing property
rather than driving a new one.

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~NoNetworkInAppOrCoreTests" --nologo
```
Expected: PASS (3 facts: two theory cases plus the coverage guard).

- [ ] **Step 3: Prove the guard actually fires**

Temporarily add this line to the end of `src/LocalScribe.Core/Import/FfmpegLocator.cs`:

```csharp
// scratch: HttpClient
```

Re-run the command from Step 2. Expected: FAIL, with the message naming
`FfmpegLocator.cs:<n>: // scratch: HttpClient`. **Remove the line** and re-run to confirm green
again. A pinning test that has never been seen to fail is not a pin.

- [ ] **Step 4: Commit**

```powershell
cd F:\LocalScribe
git status --short
```
Expected: exactly one untracked file, `tests/LocalScribe.App.Tests/NoNetworkInAppOrCoreTests.cs`
(the scratch line from Step 3 must already be gone).

```bash
cd F:/LocalScribe
git add tests/LocalScribe.App.Tests/NoNetworkInAppOrCoreTests.cs
git commit -m "test(privacy): pin the zero-network property over App and Core"
```

---

## Task 10: `LocalScribe.Fetch` - the only project allowed to touch the network

A stdio child, one job per run, spawned on explicit user action. It mirrors
`tools/fetch-models.ps1`'s two guarantees exactly: retry-with-backoff and RESUME
(`Get-RemoteFile`), and fail-closed SHA-256 verification that DELETES a mismatching file
(`Assert-Sha256`).

**Files:**
- Create: `src/LocalScribe.Fetch/LocalScribe.Fetch.csproj`
- Create: `src/LocalScribe.Fetch/Program.cs`
- Modify: `LocalScribe.slnx`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces - THE WIRE CONTRACT Task 11 parses. One JSON object on stdin, stdin then closed:
  ```json
  {"url":"https://...","destPath":"C:\\...\\models\\ggml-medium.en.bin","sha256":"<64 hex>","expectedBytes":1533763059}
  ```
  Zero or more JSONL lines on stdout, then exactly one terminal line:
  ```json
  {"type":"progress","bytes":1048576,"totalBytes":1533763059}
  {"type":"result","path":"C:\\...\\models\\ggml-medium.en.bin"}
  {"type":"error","message":"SHA256 mismatch for ggml-medium.en.bin - file deleted"}
  ```
  Exit code `0` on success, `1` on a download/verify failure, `2` on a malformed job.
  Property names are camelCase.

This project is a humble process boundary and has no unit tests, exactly like
`LocalScribe.Diarizer`. Task 11 tests the parser against a fake helper; the real child is exercised
by the smoke item at the end of this plan.

- [ ] **Step 1: Write the failing test**

The test lives in the App suite because that is where `RepoPaths` is. Append to
`tests/LocalScribe.App.Tests/NoNetworkInAppOrCoreTests.cs`:

```csharp
    [Fact]
    public void The_fetch_helper_is_a_separate_project_and_is_the_ONLY_one_that_may_use_the_network()
    {
        // The constraint is architectural, so assert the architecture: a project that exists, is
        // in the solution, and is not referenced by App or Core (a ProjectReference would drag
        // its dependency graph back into the very assemblies this class protects).
        string root = RepoPaths.SolutionRoot();
        Assert.True(File.Exists(Path.Combine(root, "src", "LocalScribe.Fetch", "LocalScribe.Fetch.csproj")));
        Assert.Contains("LocalScribe.Fetch", File.ReadAllText(Path.Combine(root, "LocalScribe.slnx")));

        foreach (string proj in new[] { "LocalScribe.App", "LocalScribe.Core" })
        {
            string csproj = File.ReadAllText(
                Path.Combine(root, "src", proj, proj + ".csproj"));
            Assert.DoesNotContain("LocalScribe.Fetch", csproj);
        }
    }
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~The_fetch_helper_is_a_separate_project" --nologo
```
Expected: FAIL - `Assert.True() Failure: Expected: True, Actual: False` on the csproj existence
check.

- [ ] **Step 3: Create the project file**

Create `src/LocalScribe.Fetch/LocalScribe.Fetch.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AssemblyName>LocalScribe.Fetch</AssemblyName>
    <RootNamespace>LocalScribe.Fetch</RootNamespace>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>

  <!-- Tier 1 plan D, T1-10 (2026-08-05): the ONE project in this solution permitted to reach the
       network, and the reason a grep over LocalScribe.App and LocalScribe.Core stays at zero
       matches - see tests/LocalScribe.App.Tests/NoNetworkInAppOrCoreTests.cs.

       DELIBERATELY has no ProjectReference to LocalScribe.Core, and neither App nor Core
       references it: a reference in either direction would put this assembly's dependency graph
       back inside the assemblies whose emptiness is the product's privacy claim. The wire
       contract (one JSON job on stdin, JSONL on stdout) is duplicated by hand on the App side
       instead - the same trade LocalScribe.Diarizer already makes, and for the same reason.

       No PackageReference at all: the whole job is done with the base class library. -->
</Project>
```

Register it in `LocalScribe.slnx` beside the other `src/` projects, matching the existing element
shape in that file exactly (open the file and copy the `LocalScribe.Diarizer` line, changing only
the path and name).

- [ ] **Step 4: Write the helper**

Create `src/LocalScribe.Fetch/Program.cs`:

```csharp
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalScribe.Fetch;

/// <summary>One download, read as a single JSON object on stdin (Tier 1 plan D, T1-10,
/// 2026-08-05). ExpectedBytes comes from the pin manifest, so the helper knows when a resumed
/// file is already complete without asking the server.</summary>
public sealed record FetchJob(string Url, string DestPath, string Sha256, long ExpectedBytes);

public sealed record ProgressLine(string Type, long Bytes, long TotalBytes);
public sealed record ResultLine(string Type, string Path);
public sealed record ErrorLine(string Type, string Message);

/// <summary>The component downloader, out of process (Tier 1 plan D, T1-10, 2026-08-05).
///
/// It is a separate executable, not a class in the app, because a grep for the network stack over
/// LocalScribe.App and LocalScribe.Core must keep returning zero matches - that is the product's
/// privacy claim in its most checkable form, and it is worth an extra process. The app spawns
/// this on an explicit Download click and never otherwise, following the same stdio-child shape
/// as LocalScribe.Diarizer (ProcessDiarisationHelper: job on stdin, one JSON line per event on
/// stdout, whole process tree killed on cancel).
///
/// Behaviour is a deliberate port of tools/fetch-models.ps1, the repo's only existing download
/// code, so the two cannot drift: Get-RemoteFile's retry-with-backoff and RESUME (large model
/// blobs get throttled and dropped, and restarting a 2.5 GB transfer from zero on every blip is
/// not acceptable) and Assert-Sha256's FAIL-CLOSED verification, which deletes a mismatching file
/// rather than leaving it where the app's presence probe would count it as installed.</summary>
public static class Program
{
    private const int MaxAttempts = 4;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> Main()
    {
        // The parent writes one job and closes stdin, so ReadToEnd terminates - the same
        // handshake ProcessDiarisationHelper uses.
        string jobLine = await Console.In.ReadToEndAsync();
        FetchJob? job;
        try { job = JsonSerializer.Deserialize<FetchJob>(jobLine, Json); }
        catch (Exception ex) { Emit(new ErrorLine("error", "bad job: " + ex.Message)); return 2; }
        // `job.Sha256 is not { Length: 64 }` and NOT `job.Sha256.Length != 64`: FetchJob's Sha256
        // is a non-nullable string, but that annotation is a COMPILE-TIME claim only - System.Text
        // .Json leaves it null when the property is absent from the JSON. A job carrying url and
        // destPath but no sha256 would then die of an unhandled NullReferenceException OUTSIDE the
        // try below, printing a stack trace instead of the one guarantee the wire contract makes
        // for a malformed job - and that guarantee is what makes a verification-free download
        // impossible.
        if (job is null || string.IsNullOrWhiteSpace(job.Url) || string.IsNullOrWhiteSpace(job.DestPath)
            || job.Sha256 is not { Length: 64 })
        {
            Emit(new ErrorLine("error", "bad job: url, destPath and a 64-character sha256 are required"));
            return 2;
        }

        try
        {
            await DownloadAsync(job);
            Emit(new ResultLine("result", job.DestPath));
            return 0;
        }
        catch (Exception ex)
        {
            Emit(new ErrorLine("error", ex.Message));
            return 1;
        }
    }

    private static async Task DownloadAsync(FetchJob job)
    {
        string? dir = Path.GetDirectoryName(job.DestPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Timeout.InfiniteTimeSpan: the default 100 s ceiling applies to the WHOLE response
        // including the body, so a multi-gigabyte model would abort mid-stream on any connection.
        // Stall protection is the parent's job - it kills the process tree on cancel.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        for (int attempt = 1; ; attempt++)
        {
            try { await OneAttemptAsync(http, job); break; }
            catch (Exception) when (attempt < MaxAttempts)
            {
                // Get-RemoteFile's backoff, capped at 30 s. Whatever bytes landed stay on disk and
                // the next attempt resumes from them.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))));
            }
        }

        // Assert-Sha256, fail closed: a corrupt or tampered blob is DELETED and the job fails,
        // never left where ComponentProbe would report it installed.
        byte[] hash;
        await using (var fs = File.OpenRead(job.DestPath)) hash = await SHA256.HashDataAsync(fs);
        string actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, job.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(job.DestPath);
            throw new InvalidDataException(
                "SHA256 mismatch for " + Path.GetFileName(job.DestPath) + " - file deleted");
        }
    }

    private static async Task OneAttemptAsync(HttpClient http, FetchJob job)
    {
        long have = File.Exists(job.DestPath) ? new FileInfo(job.DestPath).Length : 0;
        if (job.ExpectedBytes > 0 && have >= job.ExpectedBytes) return;   // already complete

        using var request = new HttpRequestMessage(HttpMethod.Get, job.Url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        // 416 on a resume request is the "already complete" signal, not a failure - the same
        // case Get-RemoteFile documents and discards.
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) return;
        response.EnsureSuccessStatusCode();

        // A server that IGNORES the range header answers 200 with the whole body. Appending that
        // to the partial file would silently concatenate two copies into a file whose hash then
        // fails for a reason no one could diagnose - so only a real 206 appends.
        bool append = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        long total = job.ExpectedBytes > 0
            ? job.ExpectedBytes
            : (response.Content.Headers.ContentLength ?? 0) + (append ? have : 0);

        await using var body = await response.Content.ReadAsStreamAsync();
        await using var file = new FileStream(job.DestPath,
            append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

        long written = append ? have : 0;
        long lastPercent = -1;
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            written += read;
            // One line per whole percent, NOT per chunk: at 80 KB a chunk a 2.5 GB model would
            // emit ~32,000 stdout lines and the parent marshals every one onto the UI thread.
            long percent = total > 0 ? written * 100 / total : 0;
            if (percent != lastPercent)
            {
                lastPercent = percent;
                Emit(new ProgressLine("progress", written, total));
            }
        }
    }

    private static void Emit<T>(T line)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(line, Json));
        Console.Out.Flush();
    }
}
```

- [ ] **Step 5: Build it and confirm the wire contract by hand**

```powershell
cd F:\LocalScribe
dotnet build src\LocalScribe.Fetch\LocalScribe.Fetch.csproj -c Debug --nologo
```
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

Then prove the malformed-job path without touching the network:

```powershell
'{"url":"","destPath":"","sha256":"","expectedBytes":0}' |
  & "F:\LocalScribe\src\LocalScribe.Fetch\bin\Debug\net10.0-windows\LocalScribe.Fetch.exe"
"exit=$LASTEXITCODE"
```
Expected output: one line
`{"type":"error","message":"bad job: url, destPath and a 64-character sha256 are required"}`
followed by `exit=2`.

Then the ABSENT-sha256 case, which is the one the null-safe pattern above exists for - a job whose
`sha256` property is missing entirely rather than empty:

```powershell
'{"url":"https://x/y","destPath":"C:\\t\\y"}' |
  & "F:\LocalScribe\src\LocalScribe.Fetch\bin\Debug\net10.0-windows\LocalScribe.Fetch.exe"
"exit=$LASTEXITCODE"
```
Expected: the SAME `bad job: ...` line and `exit=2` - **not** a NullReferenceException stack trace.
Neither probe touches the network: both fail the job guard before `DownloadAsync` is reached.

- [ ] **Step 6: Run the pin tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~NoNetworkInAppOrCoreTests" --nologo
```
Expected: PASS (4 facts). The new project is NOT scanned by the two theory cases - they enumerate
`src/LocalScribe.App` and `src/LocalScribe.Core` only, which is the entire point.

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git status --short
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every modified file; the two new `src/LocalScribe.Fetch/` files show as untracked.

```bash
cd F:/LocalScribe
git add src/LocalScribe.Fetch/LocalScribe.Fetch.csproj src/LocalScribe.Fetch/Program.cs LocalScribe.slnx tests/LocalScribe.App.Tests/NoNetworkInAppOrCoreTests.cs
git commit -m "feat(fetch): out-of-process component downloader with resume and fail-closed SHA"
```

---

## Task 11: the App side - pin manifest, component probe and the JSONL client

Three small pieces: a machine-derived pin list (so no SHA-256 is ever hand-typed into C#), a probe
that turns the EXISTING availability checks into installed/missing rows, and a parser for the
child's stdout.

**Files:**
- Modify: `tools/fetch-models.ps1` (a `-WriteComponentManifest` switch)
- Create: `src/LocalScribe.App/Services/ComponentCatalog.cs`
- Create: `src/LocalScribe.App/Services/ComponentProbe.cs`
- Create: `src/LocalScribe.App/Services/IComponentFetchHelper.cs`
- Create: `src/LocalScribe.App/Services/ComponentFetchClient.cs`
- Create: `src/LocalScribe.App/Services/ProcessComponentFetchHelper.cs`
- Create: `tests/LocalScribe.App.Tests/ComponentProbeTests.cs`
- Create: `tests/LocalScribe.App.Tests/ComponentFetchClientTests.cs`

**Interfaces:**
- Consumes (all already exist, unchanged):
  - `LocalScribe.App.Services.DiarisationAvailability.Probe(Func<string,string> resolveModel, string exePath) : string?`
  - `LocalScribe.Core.Import.FfmpegLocator.FindToolsDir() : string?` and `.MissingMessage`
  - `LocalScribe.Core.Assistant.AssistantHelperLocator.FindExe() : string?` and `.MissingMessage`
  - `LocalScribe.Core.Storage.JsonFile.ReadAsync<T>(string path, CancellationToken ct) : Task<T?>`
- Produces:
  - `ComponentPin(string Id, string Name, string File, string Url, string Sha256, long Bytes)`
  - `ComponentManifest(int SchemaVersion, IReadOnlyList<ComponentPin> Components)`
  - `ComponentCatalog.LoadAsync(string modelsRoot, CancellationToken ct) : Task<IReadOnlyList<ComponentPin>>`
  - `ComponentState(string Id, string Name, bool Installed, long Bytes, string? Detail, ComponentPin? Pin)`
  - `ComponentProbe(Func<string,string> resolveModel, Func<string?> findFfmpeg, Func<string?> findAssistant, string diarizerExe, Func<string,long?> fileBytes)`
    with `IReadOnlyList<ComponentState> Probe(IReadOnlyList<ComponentPin> pins)`
  - `IComponentFetchHelper.RunAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct) : Task<int>`
  - `ComponentFetchProgress(long Bytes, long TotalBytes)` with `double Fraction`
  - `ComponentFetchClient(IComponentFetchHelper helper)` with
    `FetchAsync(ComponentPin pin, string destPath, IProgress<ComponentFetchProgress> progress, CancellationToken ct) : Task`
  Task 12 consumes `ComponentCatalog`, `ComponentProbe`, `ComponentState` and `ComponentFetchClient`.

**COMMENT CONSTRAINT:** none of these files may contain the words the Task 9 regex looks for. Write
"the fetch helper" and "the network stack".

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.App.Tests/ComponentProbeTests.cs`:

```csharp
using System.IO;
using System.Linq;
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Installed/missing state for the Settings Components panel (Tier 1 plan D, T1-10,
/// 2026-08-05). Every probe already existed - this class only assembles them into rows, so it is
/// built entirely from injected delegates and never touches the developer's real machine.</summary>
public sealed class ComponentProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-comp-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static ComponentPin Pin(string id, string file, long bytes)
        => new(id, id, file, "https://example.invalid/" + file, new string('a', 64), bytes);

    /// <summary>presentFiles is a plain string[] rather than a params array: every call site here
    /// passes it BY NAME, and C# forbids a named argument in the expanded form of a params
    /// parameter (a bare `presentFiles: "x"` is CS1503, string to string[]).</summary>
    private ComponentProbe Make(bool ffmpeg = false, bool assistant = false,
        string[]? presentFiles = null)
    {
        var present = new HashSet<string>(presentFiles ?? [], StringComparer.OrdinalIgnoreCase);
        return new ComponentProbe(
            resolveModel: name => Path.Combine(_root, name),
            findFfmpeg: () => ffmpeg ? Path.Combine(_root, "ffmpeg") : null,
            findAssistant: () => assistant ? Path.Combine(_root, "assistant", "x.exe") : null,
            diarizerExe: Path.Combine(_root, "LocalScribe.Diarizer.exe"),
            fileBytes: path => present.Contains(Path.GetFileName(path)) ? 1234L : null);
    }

    [Fact]
    public void A_pinned_model_present_on_disk_reports_installed_with_its_real_size()
    {
        var rows = Make(presentFiles: ["ggml-medium.en.bin"])
            .Probe([Pin("whisper-medium-en", "ggml-medium.en.bin", 999)]);

        var row = Assert.Single(rows.Where(r => r.Id == "whisper-medium-en"));
        Assert.True(row.Installed);
        Assert.Equal(1234, row.Bytes);          // measured, not the manifest's figure
    }

    [Fact]
    public void A_pinned_model_absent_reports_missing_with_the_manifest_size_so_the_user_can_budget()
    {
        var rows = Make().Probe([Pin("whisper-medium-en", "ggml-medium.en.bin", 999)]);

        var row = Assert.Single(rows.Where(r => r.Id == "whisper-medium-en"));
        Assert.False(row.Installed);
        Assert.Equal(999, row.Bytes);
        Assert.NotNull(row.Pin);                // downloadable: the panel shows a Download button
    }

    [Fact]
    public void Ffmpeg_the_diarizer_and_the_assistant_are_probe_only_rows_with_no_pin()
    {
        // These three ship in the installer or via tools/fetch-ffmpeg.ps1 - there is no pinned
        // blob to fetch, so the panel must show state and a remedy, never a Download button that
        // cannot work.
        var rows = Make().Probe([]);

        foreach (string id in new[] { "ffmpeg", "diarizer", "assistant" })
        {
            var row = Assert.Single(rows.Where(r => r.Id == id));
            Assert.False(row.Installed);
            Assert.Null(row.Pin);
            Assert.False(string.IsNullOrWhiteSpace(row.Detail));   // a remedy, not a blank cell
        }
    }

    [Fact]
    public void A_present_helper_reports_installed_and_carries_no_remedy_text()
    {
        var rows = Make(ffmpeg: true).Probe([]);

        var ffmpeg = Assert.Single(rows.Where(r => r.Id == "ffmpeg"));
        Assert.True(ffmpeg.Installed);
        Assert.Null(ffmpeg.Detail);
    }

    [Fact]
    public void The_assistant_needs_BOTH_its_helper_and_its_model_before_it_reports_installed()
    {
        // build.ps1 publishes the helper into the installer but does NOT bundle its ~2.5 GB chat
        // model, so on a clean machine the exe is present and the feature cannot answer anything.
        // A row that reported "installed" off the exe alone would be a green light on a dead
        // feature, and smoke item 6 would assert something that cannot pass.
        var chat = Pin(ComponentProbe.AssistantChatPinId, "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2_500_000_000);

        var row = Assert.Single(Make(assistant: true).Probe([chat]).Where(r => r.Id == "assistant"));

        Assert.False(row.Installed);
        Assert.Contains("model", row.Detail);         // says WHICH half is missing
        Assert.Contains(chat.Name, row.Detail);       // and names the row that fixes it
    }

    [Fact]
    public void The_assistant_reports_installed_once_helper_and_model_are_both_present()
    {
        var chat = Pin(ComponentProbe.AssistantChatPinId, "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2_500_000_000);

        var row = Assert.Single(
            Make(assistant: true, presentFiles: ["Qwen3-4B-Instruct-2507-Q4_K_M.gguf"])
                .Probe([chat]).Where(r => r.Id == "assistant"));

        Assert.True(row.Installed);
        Assert.Null(row.Detail);
    }

    [Fact]
    public void An_empty_manifest_still_yields_the_three_probe_only_rows()
    {
        // A build that shipped without component-manifest.json must still render a useful panel.
        Assert.Equal(3, Make().Probe([]).Count);
    }
}
```

Create `tests/LocalScribe.App.Tests/ComponentFetchClientTests.cs`:

```csharp
using System.Text.Json;
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The parser for the fetch helper's stdout (Tier 1 plan D, T1-10, 2026-08-05). Split
/// out of the process object exactly the way SherpaHelperDiariser is split out of
/// ProcessDiarisationHelper: the wire contract is testable over a scripted fake, and the real
/// child process is smoke-only.</summary>
public sealed class ComponentFetchClientTests
{
    private static readonly ComponentPin Pin =
        new("m", "Medium", "ggml-medium.en.bin", "https://example.invalid/m.bin", new string('a', 64), 400);

    private sealed class ScriptedHelper(int exitCode, params string[] lines) : IComponentFetchHelper
    {
        public string? JobJson;
        public Task<int> RunAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct)
        {
            JobJson = jobJson;
            foreach (string line in lines) onStdoutLine(line);
            return Task.FromResult(exitCode);
        }
    }

    private sealed class Collector : IProgress<ComponentFetchProgress>
    {
        public List<ComponentFetchProgress> Seen { get; } = new();
        public void Report(ComponentFetchProgress value) => Seen.Add(value);
    }

    [Fact]
    public async Task The_job_is_serialized_with_the_camelCase_names_the_helper_expects()
    {
        var helper = new ScriptedHelper(0, "{\"type\":\"result\",\"path\":\"C:\\\\x\"}");
        await new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", new Collector(), default);

        var job = JsonDocument.Parse(helper.JobJson!).RootElement;
        Assert.Equal("https://example.invalid/m.bin", job.GetProperty("url").GetString());
        Assert.Equal("C:\\x", job.GetProperty("destPath").GetString());
        Assert.Equal(new string('a', 64), job.GetProperty("sha256").GetString());
        Assert.Equal(400, job.GetProperty("expectedBytes").GetInt64());
    }

    [Fact]
    public async Task Progress_lines_are_forwarded_as_a_fraction()
    {
        var progress = new Collector();
        var helper = new ScriptedHelper(0,
            "{\"type\":\"progress\",\"bytes\":100,\"totalBytes\":400}",
            "{\"type\":\"progress\",\"bytes\":400,\"totalBytes\":400}",
            "{\"type\":\"result\",\"path\":\"C:\\\\x\"}");

        await new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", progress, default);

        Assert.Equal(2, progress.Seen.Count);
        Assert.Equal(0.25, progress.Seen[0].Fraction, 3);
        Assert.Equal(1.0, progress.Seen[1].Fraction, 3);
    }

    [Fact]
    public async Task An_error_line_becomes_an_exception_carrying_the_helpers_own_message()
    {
        var helper = new ScriptedHelper(1,
            "{\"type\":\"error\",\"message\":\"SHA256 mismatch for m.bin - file deleted\"}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", new Collector(), default));

        Assert.Contains("SHA256 mismatch", ex.Message);
    }

    [Fact]
    public async Task A_nonzero_exit_with_no_error_line_still_fails_rather_than_reporting_success()
    {
        // Fail closed: a helper that dies without saying why must NOT leave the panel showing a
        // component as installed.
        var helper = new ScriptedHelper(9);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", new Collector(), default));

        Assert.Contains("9", ex.Message);
    }

    [Fact]
    public async Task A_zero_exit_with_no_result_line_fails_too()
    {
        var helper = new ScriptedHelper(0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", new Collector(), default));
    }

    [Fact]
    public async Task An_unparseable_line_is_ignored_rather_than_failing_the_download()
    {
        // The child writes only JSON, but a native runtime warning could still reach stdout.
        // Losing a 2.5 GB download to a stray line would be absurd.
        var helper = new ScriptedHelper(0, "warning: something", "{\"type\":\"result\",\"path\":\"C:\\\\x\"}");

        await new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", new Collector(), default);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ComponentProbeTests|FullyQualifiedName~ComponentFetchClientTests" --nologo
```
Expected: FAIL to compile - `CS0246: The type or namespace name 'ComponentPin' could not be found`
(and `ComponentProbe`, `IComponentFetchHelper`, `ComponentFetchClient`,
`ComponentFetchProgress`).

- [ ] **Step 3: Teach `fetch-models.ps1` to emit the pin manifest**

In `tools/fetch-models.ps1`, add a switch to the `param(...)` block:

```powershell
    # Tier 1 plan D, T1-10 (2026-08-05): write models/component-manifest.json - the url + sha256
    # + byte size of every model the IN-APP downloader may fetch. Resolved from each file's
    # Hugging Face LFS POINTER (raw/main), which carries both "oid sha256:<hex>" and
    # "size <bytes>", so nothing has to be downloaded to produce the pins and no SHA-256 is ever
    # hand-typed into C#. Assert-Sha256 then enforces the same pin fail-closed on the app side.
    [switch] $WriteComponentManifest
```

Add this function beside `Get-HfPinnedSha256`:

```powershell
# Returns BOTH values the pin manifest needs from one LFS pointer fetch.
function Get-HfPin {
    param([string] $PointerUrl)
    $resp = Invoke-WebRequest -Uri $PointerUrl
    $text = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { [string]$resp.Content }
    if ($text -notmatch 'oid sha256:([0-9a-fA-F]{64})') {
        throw "no sha256 oid in LFS pointer at $PointerUrl - wrong path, or the file is not LFS-tracked"
    }
    $sha = $Matches[1].ToLowerInvariant()
    if ($text -notmatch 'size (\d+)') { throw "no size in LFS pointer at $PointerUrl" }
    return @{ Sha256 = $sha; Bytes = [long]$Matches[1] }
}
```

Append at the end of the script:

```powershell
if ($WriteComponentManifest) {
    # Only HF-LFS-backed blobs appear here. ffmpeg, the diarizer helper and the assistant helper
    # EXECUTABLE are NOT downloadable in-app: ffmpeg comes from tools/fetch-ffmpeg.ps1 and the two
    # helpers ship in the installer, so the panel shows them as probe-only rows with a remedy
    # instead of a Download button that could not work.
    #
    # The assistant's WEIGHTS are a different matter and they ARE pinned here. build.ps1 publishes
    # the assistant helper into the installer but deliberately does NOT bundle its ~2.5 GB chat
    # model or the ~300 MB embedding model - the same reason large-v3-turbo is not bundled. Without
    # these two pins a clean install would show the assistant as present and answer nothing, with
    # no in-app route to obtain the weights at all.
    #
    # Repo is per-entry: these three blobs live in three different Hugging Face repositories, so a
    # single hardcoded base URL (which is what this block first had) could only ever pin whisper.
    $pins = @(
        @{ Id = 'whisper-large-v3-turbo'; Name = 'Whisper large-v3-turbo'
           File = 'ggml-large-v3-turbo.bin'; Repo = 'ggerganov/whisper.cpp' }
        @{ Id = 'whisper-large-v3-turbo-q5'; Name = 'Whisper large-v3-turbo (q5_0)'
           File = 'ggml-large-v3-turbo-q5_0.bin'; Repo = 'ggerganov/whisper.cpp' }
        @{ Id = 'whisper-medium-en'; Name = 'Whisper medium.en'
           File = 'ggml-medium.en.bin'; Repo = 'ggerganov/whisper.cpp' }
        @{ Id = 'whisper-medium-en-q5'; Name = 'Whisper medium.en (q5_0)'
           File = 'ggml-medium.en-q5_0.bin'; Repo = 'ggerganov/whisper.cpp' }
        # MUST stay id 'assistant-chat' - ComponentProbe.AssistantChatPinId reads this id to decide
        # whether the assistant row is really usable, rather than naming the .gguf in C#.
        @{ Id = 'assistant-chat'; Name = 'Assistant model (Qwen3-4B-Instruct-2507 Q4_K_M)'
           File = 'Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
           Repo = 'lmstudio-community/Qwen3-4B-Instruct-2507-GGUF' }
        @{ Id = 'assistant-embedding'; Name = 'Semantic search model (EmbeddingGemma-300m Q8_0)'
           File = 'embeddinggemma-300M-Q8_0.gguf'; Repo = 'ggml-org/embeddinggemma-300M-GGUF' }
    )
    $entries = @()
    foreach ($p in $pins) {
        Write-Host "pin: $($p.File)"
        $pin = Get-HfPin -PointerUrl "https://huggingface.co/$($p.Repo)/raw/main/$($p.File)"
        Write-Host "  sha256 $($pin.Sha256)  bytes $($pin.Bytes)"
        $entries += [ordered]@{
            id = $p.Id; name = $p.Name; file = $p.File
            url = "https://huggingface.co/$($p.Repo)/resolve/main/$($p.File)"
            sha256 = $pin.Sha256; bytes = $pin.Bytes
        }
    }
    $path = Join-Path $models 'component-manifest.json'
    [ordered]@{ schemaVersion = 1; components = $entries } |
        ConvertTo-Json -Depth 4 | Set-Content -Path $path -Encoding utf8
    Write-Host "component manifest -> $path ($($entries.Count) entries)"
}
```

- [ ] **Step 4: Write the catalog and the probe**

Create `src/LocalScribe.App/Services/ComponentCatalog.cs`:

```csharp
using System.IO;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Services;

/// <summary>One downloadable component, pinned (Tier 1 plan D, T1-10, 2026-08-05). Sha256 and
/// Bytes are MACHINE-DERIVED by tools/fetch-models.ps1 -WriteComponentManifest from each file's
/// Hugging Face LFS pointer, never hand-typed - a mistyped pin would fail closed and delete a
/// perfectly good multi-gigabyte download with no way for the user to tell why.</summary>
public sealed record ComponentPin(string Id, string Name, string File, string Url,
    string Sha256, long Bytes);

/// <summary>models/component-manifest.json, written by tools/fetch-models.ps1 and copied beside
/// the binary by build.ps1.</summary>
public sealed record ComponentManifest(int SchemaVersion, IReadOnlyList<ComponentPin> Components);

/// <summary>Loads the pin list (Tier 1 plan D, T1-10, 2026-08-05). Absence is NOT an error: a
/// build that shipped without the manifest simply offers no downloads, and the Components panel
/// still renders every probe-only row. Fail-soft here, fail-CLOSED in the helper - a missing pin
/// list must not become a download with no verification.</summary>
public static class ComponentCatalog
{
    public const string FileName = "component-manifest.json";

    public static async Task<IReadOnlyList<ComponentPin>> LoadAsync(string modelsRoot,
        CancellationToken ct)
    {
        string path = Path.Combine(modelsRoot, FileName);
        if (!System.IO.File.Exists(path)) return [];
        try
        {
            var manifest = await JsonFile.ReadAsync<ComponentManifest>(path, ct);
            // A newer schema is IGNORED, not mangled: this list only ever adds a Download button,
            // so degrading to "no downloads offered" is safe, unlike a store over evidence.
            return manifest is { SchemaVersion: 1 } ? manifest.Components : [];
        }
        catch (Exception) { return []; }
    }
}
```

Create `src/LocalScribe.App/Services/ComponentProbe.cs`:

```csharp
using System.IO;
using System.Linq;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Import;

namespace LocalScribe.App.Services;

/// <summary>One row of the Settings Components panel (Tier 1 plan D, T1-10, 2026-08-05). Pin is
/// null for a component that is not downloadable in-app - the panel then shows Detail as the
/// remedy instead of a Download button that could not work.</summary>
public sealed record ComponentState(string Id, string Name, bool Installed, long Bytes,
    string? Detail, ComponentPin? Pin);

/// <summary>Assembles installed/missing state for every component the product depends on
/// (Tier 1 plan D, T1-10, 2026-08-05). It INVENTS no detection: every probe below already
/// existed and is reached through an injected delegate, both so the panel and the feature agree
/// about what "installed" means and so this class never reads the developer's real machine
/// during a test run.
///
/// ffmpeg, the diarizer helper and the assistant helper are PROBE-ONLY ROWS. ffmpeg comes from
/// tools/fetch-ffmpeg.ps1 and the two helpers are published by build.ps1 into the installer, so
/// there is no pinned blob to fetch for the ROW itself and offering a Download button on it would
/// be a lie. The assistant's WEIGHTS are pinned separately and appear as their own downloadable
/// rows - see AssistantChatPinId below.</summary>
public sealed class ComponentProbe(
    Func<string, string> resolveModel,
    Func<string?> findFfmpeg,
    Func<string?> findAssistant,
    string diarizerExe,
    Func<string, long?> fileBytes)
{
    /// <summary>The manifest id of the assistant's chat model (Tier 1 plan D, T1-10, 2026-08-05).
    /// tools/fetch-models.ps1 -WriteComponentManifest writes this id; the assistant row looks the
    /// pin up by it rather than naming a .gguf here, so a model swap is a one-line change in the
    /// script and not a silent disagreement between the panel and the fetch tooling.</summary>
    public const string AssistantChatPinId = "assistant-chat";

    public IReadOnlyList<ComponentState> Probe(IReadOnlyList<ComponentPin> pins)
    {
        var rows = new List<ComponentState>();

        foreach (var pin in pins)
        {
            long? bytes = fileBytes(resolveModel(pin.File));
            // Installed rows show the MEASURED size; missing rows show the manifest figure, so a
            // user can decide whether to spend it before starting.
            rows.Add(new ComponentState(pin.Id, pin.Name, bytes is > 0, bytes ?? pin.Bytes,
                Detail: null, Pin: pin));
        }

        string? ffmpeg = findFfmpeg();
        rows.Add(new ComponentState("ffmpeg", "ffmpeg / ffprobe (audio import)",
            ffmpeg is not null, 0,
            ffmpeg is null ? FfmpegLocator.MissingMessage : null, Pin: null));

        // DiarisationAvailability.Probe returns a user-facing reason or null; it also covers the
        // two sherpa models, so this single row answers "can Split Speakers run at all".
        string? diarisation = DiarisationAvailability.Probe(resolveModel, diarizerExe);
        rows.Add(new ComponentState("diarizer", "Speaker detection (diarizer + models)",
            diarisation is null, 0, diarisation, Pin: null));

        // The assistant needs BOTH halves. build.ps1 publishes the helper into the installer, but
        // its ~2.5 GB chat model is deliberately NOT bundled - that is precisely what this panel
        // exists for - so probing the exe ALONE would paint a green "installed" row for a feature
        // that cannot answer a single question on a clean machine. The model is located through
        // the PIN, never a file name hardcoded here.
        string? assistant = findAssistant();
        var chatPin = pins.FirstOrDefault(p => p.Id == AssistantChatPinId);
        bool chatModel = chatPin is not null && fileBytes(resolveModel(chatPin.File)) is > 0;
        rows.Add(new ComponentState("assistant", "Assistant helper",
            assistant is not null && chatModel, 0,
            AssistantDetail(assistant, chatPin, chatModel), Pin: null));

        return rows;
    }

    /// <summary>The assistant row's remedy text - never null while something is missing, so the
    /// panel never shows a blank cell beside a red row. Says WHICH half is absent: "the helper is
    /// missing" and "the model is missing" have completely different fixes, and only one of them
    /// is a button in this panel.</summary>
    private static string? AssistantDetail(string? helper, ComponentPin? chatPin, bool chatModel)
    {
        if (helper is null) return AssistantHelperLocator.MissingMessage;
        if (chatModel) return null;
        return chatPin is null
            ? "The assistant helper is installed but its language model is not, and this build "
              + "carries no component list - run tools\\fetch-models.ps1 -Assistant."
            : "The assistant helper is installed but its language model is not - download \""
              + chatPin.Name + "\" below.";
    }

    /// <summary>Production file-size probe: absent, unreadable or ZERO-BYTE all read as absent -
    /// a truncated download is not a usable component, which is the same test
    /// DiarisationAvailability and tools/verify-*.ps1 already apply.</summary>
    public static long? MeasureFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0 ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
```

- [ ] **Step 5: Write the seam, the client and the process object**

Create `src/LocalScribe.App/Services/IComponentFetchHelper.cs`:

```csharp
namespace LocalScribe.App.Services;

/// <summary>The process boundary for component downloads (Tier 1 plan D, T1-10, 2026-08-05),
/// shaped exactly like IDiarisationHelper: the caller hands over one serialized job and receives
/// the child's stdout line by line. The seam exists so ComponentFetchClient's parsing is testable
/// against a scripted fake while the real child stays a humble, untested process object.</summary>
public interface IComponentFetchHelper
{
    Task<int> RunAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct);
}
```

Create `src/LocalScribe.App/Services/ComponentFetchClient.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalScribe.App.Services;

/// <summary>Download progress: bytes so far out of the pinned total.</summary>
public sealed record ComponentFetchProgress(long Bytes, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)Bytes / TotalBytes, 0, 1) : 0;
}

/// <summary>Drives one component download over the out-of-process fetch helper (Tier 1 plan D,
/// T1-10, 2026-08-05) and turns its JSONL stdout into progress reports or an exception.
///
/// This is the SherpaHelperDiariser half of the pattern: all of the contract, none of the
/// process. It FAILS CLOSED - a missing result line, a non-zero exit or an error line all throw,
/// so the panel can never mark a component installed on the strength of a helper that merely
/// stopped talking.</summary>
public sealed class ComponentFetchClient(IComponentFetchHelper helper)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task FetchAsync(ComponentPin pin, string destPath,
        IProgress<ComponentFetchProgress> progress, CancellationToken ct)
    {
        string job = JsonSerializer.Serialize(new
        {
            url = pin.Url, destPath, sha256 = pin.Sha256, expectedBytes = pin.Bytes,
        }, Json);

        bool sawResult = false;
        string? error = null;
        int exit = await helper.RunAsync(job, line =>
        {
            try
            {
                var root = JsonDocument.Parse(line).RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "progress":
                        progress.Report(new ComponentFetchProgress(
                            root.GetProperty("bytes").GetInt64(),
                            root.GetProperty("totalBytes").GetInt64()));
                        break;
                    case "result": sawResult = true; break;
                    case "error": error = root.GetProperty("message").GetString(); break;
                }
            }
            catch (Exception)
            {
                // The child writes only JSON, but a native runtime warning could still land on
                // stdout. Losing a multi-gigabyte download to one stray line would be absurd.
            }
        }, ct);

        if (error is not null) throw new InvalidOperationException(error);
        if (exit != 0)
            throw new InvalidOperationException(
                "The download helper exited with code " + exit + " without reporting a reason.");
        if (!sawResult)
            throw new InvalidOperationException(
                "The download helper finished without confirming the file was written.");
    }
}
```

Create `src/LocalScribe.App/Services/ProcessComponentFetchHelper.cs`:

```csharp
using System.Diagnostics;

namespace LocalScribe.App.Services;

/// <summary>Production IComponentFetchHelper (Tier 1 plan D, T1-10, 2026-08-05): spawns
/// LocalScribe.Fetch.exe, writes the job as one JSON line to its stdin, and forwards each stdout
/// line to the caller. A near-copy of ProcessDiarisationHelper, deliberately - including killing
/// the whole child process TREE on cancellation, because a plain Kill() signals only the
/// immediate child.
///
/// Spawned ONLY from an explicit Download click. This class is the entire reason the two
/// shipping assemblies stay clean under the grep in NoNetworkInAppOrCoreTests: the transfer runs
/// in another executable that this one merely starts and stops. A humble object at the process
/// boundary - not unit-tested; ComponentFetchClientTests covers the wire contract over a fake
/// helper instead.</summary>
public sealed class ProcessComponentFetchHelper(string exePath) : IComponentFetchHelper
{
    public async Task<int> RunAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start the download helper");
        await using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* best-effort: the process may have exited between the check and the kill */ }
        });

        await proc.StandardInput.WriteAsync(jobJson);
        proc.StandardInput.Close();

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            onStdoutLine(line);

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
}
```

- [ ] **Step 6: Run the tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ComponentProbeTests|FullyQualifiedName~ComponentFetchClientTests|FullyQualifiedName~NoNetworkInAppOrCoreTests" --nologo
```
**No isolated `BaseOutputPath` on this command** (no command in this plan uses one - see Global Constraints): the filter includes
`NoNetworkInAppOrCoreTests`, which reads repo source through `RepoPaths.SolutionRoot()`.

Expected: PASS (7 + 6 + 4 facts). **If `NoNetworkInAppOrCoreTests` fails, a comment in one of the
five new App files names a forbidden word** - reword it; do not weaken the regex.

- [ ] **Step 7: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add tools/fetch-models.ps1 src/LocalScribe.App/Services/ComponentCatalog.cs src/LocalScribe.App/Services/ComponentProbe.cs src/LocalScribe.App/Services/IComponentFetchHelper.cs src/LocalScribe.App/Services/ComponentFetchClient.cs src/LocalScribe.App/Services/ProcessComponentFetchHelper.cs tests/LocalScribe.App.Tests/ComponentProbeTests.cs tests/LocalScribe.App.Tests/ComponentFetchClientTests.cs
git commit -m "feat(components): pin manifest, availability probe and the fetch wire client"
```

---

## Task 12: the Settings "Components" panel

**Files:**
- Create: `src/LocalScribe.App/ViewModels/ComponentsPanelViewModel.cs`
- Create: `tests/LocalScribe.App.Tests/ComponentsPanelViewModelTests.cs`
- Modify: `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs:189-199`, `:256`
- Modify: `src/LocalScribe.App/SettingsPage.xaml:410` (a new card before the "App" card)
- Modify: `src/LocalScribe.App/App.xaml.cs:252-284`

**Interfaces:**
- Consumes: `ComponentCatalog.LoadAsync`, `ComponentProbe`, `ComponentState`, `ComponentPin`,
  `ComponentFetchClient`, `ComponentFetchProgress` (Task 11); `NoticeSeverity` (Task 1);
  the `QueuedDispatch` fake (copy it verbatim into the new test file - house convention is a
  per-file copy, `DiarisationEngineGateTests.cs:14-42`).
- Produces:
  - `ComponentRow` - `Id`, `Name`, `Installed`, `SizeText`, `Detail`, `CanDownload`,
    `Progress` (0..1), `ProgressText`, `IsDownloading`.
  - `ComponentsPanelViewModel(Func<CancellationToken, Task<IReadOnlyList<ComponentPin>>> loadPins, ComponentProbe probe, Func<ComponentPin, string> destPathFor, ComponentFetchClient fetch, IUiErrorReporter errors, Action<Action> dispatch)`
    with `ObservableCollection<ComponentRow> Rows`, `Task LastLoad`, `Task LastDownload`,
    `IAsyncRelayCommand<ComponentRow> DownloadCommand`, `IRelayCommand<ComponentRow> CancelCommand`,
    `IAsyncRelayCommand RefreshCommand`.
  - `SettingsPageViewModel.Components : ComponentsPanelViewModel?` - nullable, null in unit tests.
  No later task consumes these.

`LastLoad`/`LastDownload` follow the `SettingsPageViewModel.LastSave` precedent
(`:180-182`): production fire-and-forgets, tests await so nothing is in flight when they assert.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/ComponentsPanelViewModelTests.cs`:

```csharp
using System.IO;
using System.Linq;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Records dispatched actions and runs them only when explicitly pumped, one turn at a
/// time. Deliberately duplicated per file (house convention: no cross-file test helper) -
/// mirrors DiarisationEngineGateTests.cs:21-42. Lock-guarded because a fire-and-forget load's
/// pool-thread continuation can enqueue while the test thread is inside Pump; dequeue under the
/// lock, invoke outside it so a re-entrant dispatch cannot deadlock.</summary>
sealed class ComponentsQueuedDispatch
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();
    public Action<Action> Dispatch => a => { lock (_gate) _queue.Enqueue(a); };
    public bool PumpOne()
    {
        Action next;
        lock (_gate)
        {
            if (_queue.Count == 0) return false;
            next = _queue.Dequeue();
        }
        next();
        return true;
    }
    public void Pump() { while (PumpOne()) { } }
}

/// <summary>The Settings Components panel (Tier 1 plan D, T1-10, 2026-08-05): installed/missing
/// state, size, and a Download button that runs the out-of-process fetch helper with progress
/// and resume. Every collaborator is injected, so this never reads the developer's real machine
/// or starts a real process.</summary>
public sealed class ComponentsPanelViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-comppanel-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly ComponentPin MediumPin =
        new("whisper-medium-en", "Whisper medium.en", "ggml-medium.en.bin",
            "https://example.invalid/m.bin", new string('a', 64), 1_000_000);

    private sealed class ScriptedHelper : IComponentFetchHelper
    {
        public List<string> Lines = ["{\"type\":\"result\",\"path\":\"C:\\\\x\"}"];
        public int ExitCode;
        public int Runs;
        public Task<int> RunAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct)
        {
            Runs++;
            foreach (string line in Lines) onStdoutLine(line);
            return Task.FromResult(ExitCode);
        }
    }

    private (ComponentsPanelViewModel Vm, ComponentsQueuedDispatch D, ScriptedHelper H, FakeUiErrorReporter E)
        MakeVm(bool mediumPresent = false, IReadOnlyList<ComponentPin>? pins = null)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mediumPresent) present.Add("ggml-medium.en.bin");
        var probe = new ComponentProbe(
            resolveModel: name => Path.Combine(_root, name),
            findFfmpeg: () => null, findAssistant: () => null,
            diarizerExe: Path.Combine(_root, "LocalScribe.Diarizer.exe"),
            fileBytes: p => present.Contains(Path.GetFileName(p)) ? 42L : null);
        var helper = new ScriptedHelper();
        var dispatch = new ComponentsQueuedDispatch();
        var errors = new FakeUiErrorReporter();
        var vm = new ComponentsPanelViewModel(
            loadPins: _ => Task.FromResult(pins ?? (IReadOnlyList<ComponentPin>)[MediumPin]),
            probe, destPathFor: pin => Path.Combine(_root, pin.File),
            new ComponentFetchClient(helper), errors, dispatch.Dispatch);
        return (vm, dispatch, helper, errors);
    }

    [Fact]
    public async Task Rows_show_installed_state_a_human_size_and_a_download_button_only_where_a_pin_exists()
    {
        var (vm, d, _, _) = MakeVm();
        await vm.LastLoad;
        d.Pump();

        var medium = Assert.Single(vm.Rows.Where(r => r.Id == "whisper-medium-en"));
        Assert.False(medium.Installed);
        Assert.True(medium.CanDownload);
        Assert.Equal("1.0 MB", medium.SizeText);

        var assistant = Assert.Single(vm.Rows.Where(r => r.Id == "assistant"));
        Assert.False(assistant.CanDownload);            // probe-only: no pinned blob to fetch
        Assert.False(string.IsNullOrWhiteSpace(assistant.Detail));
    }

    [Fact]
    public async Task An_installed_component_offers_no_download()
    {
        var (vm, d, _, _) = MakeVm(mediumPresent: true);
        await vm.LastLoad;
        d.Pump();

        var medium = Assert.Single(vm.Rows.Where(r => r.Id == "whisper-medium-en"));
        Assert.True(medium.Installed);
        Assert.False(medium.CanDownload);
    }

    [Fact]
    public async Task A_download_reports_progress_and_re_probes_so_the_row_flips_to_installed()
    {
        var (vm, d, helper, errors) = MakeVm();
        await vm.LastLoad;
        d.Pump();
        var row = vm.Rows.First(r => r.Id == "whisper-medium-en");
        helper.Lines =
        [
            "{\"type\":\"progress\",\"bytes\":500000,\"totalBytes\":1000000}",
            "{\"type\":\"result\",\"path\":\"C:\\\\x\"}",
        ];

        await vm.DownloadCommand.ExecuteAsync(row);
        d.Pump();

        Assert.Equal(1, helper.Runs);
        Assert.False(row.IsDownloading);
        Assert.Equal(new[] { NoticeSeverity.Success }, errors.InfoSeverities);
    }

    [Fact]
    public async Task A_failed_download_reports_the_helpers_reason_and_leaves_the_row_not_installed()
    {
        var (vm, d, helper, errors) = MakeVm();
        await vm.LastLoad;
        d.Pump();
        var row = vm.Rows.First(r => r.Id == "whisper-medium-en");
        helper.ExitCode = 1;
        helper.Lines = ["{\"type\":\"error\",\"message\":\"SHA256 mismatch for m.bin - file deleted\"}"];

        await vm.DownloadCommand.ExecuteAsync(row);
        d.Pump();

        Assert.False(row.Installed);
        Assert.False(row.IsDownloading);
        Assert.Contains(errors.Reports, r => r.Ex.Message.Contains("SHA256 mismatch"));
    }

    [Fact]
    public async Task A_probe_only_row_cannot_be_downloaded_even_if_the_command_is_invoked()
    {
        // Belt and braces: the button is hidden, but a bound command must refuse anyway rather
        // than spawn a helper with a null url.
        var (vm, d, helper, _) = MakeVm();
        await vm.LastLoad;
        d.Pump();

        await vm.DownloadCommand.ExecuteAsync(vm.Rows.First(r => r.Id == "assistant"));

        Assert.Equal(0, helper.Runs);
    }

    [Fact]
    public async Task A_build_with_no_pin_manifest_still_renders_the_probe_only_rows()
    {
        var (vm, d, _, _) = MakeVm(pins: []);
        await vm.LastLoad;
        d.Pump();

        Assert.Equal(3, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.False(r.CanDownload));
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ComponentsPanelViewModelTests" --nologo
```
Expected: FAIL to compile - `CS0246: The type or namespace name 'ComponentsPanelViewModel' could
not be found`.

- [ ] **Step 3: Write the view model**

Create `src/LocalScribe.App/ViewModels/ComponentsPanelViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;

namespace LocalScribe.App.ViewModels;

/// <summary>One row of the Settings Components panel (Tier 1 plan D, T1-10, 2026-08-05).
/// CanDownload is false for a probe-only component (ffmpeg, the diarizer, the assistant helper):
/// those arrive with the installer or via tools/fetch-ffmpeg.ps1, so Detail carries the remedy
/// and no button is offered.</summary>
public sealed partial class ComponentRow(string id, string name, ComponentPin? pin) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public ComponentPin? Pin { get; } = pin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _installed;

    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloading;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";

    public bool CanDownload => Pin is not null && !Installed && !IsDownloading;
}

/// <summary>The Settings "Components" panel (Tier 1 plan D, T1-10, 2026-08-05): what is
/// installed, how big it is, and a Download button that runs the OUT-OF-PROCESS fetch helper
/// with progress and resume.
///
/// Every collaborator is a delegate or an injected object - the pin loader, the probe, the
/// destination resolver, the fetch client - so this VM never reads the machine and never starts
/// a process during a test run, and so nothing here has to know that a download happens in
/// another executable at all.
///
/// LastLoad / LastDownload follow the SettingsPageViewModel.LastSave precedent: production
/// fire-and-forgets and surfaces failures through IUiErrorReporter; tests await them so no work
/// is in flight when they assert.</summary>
public sealed partial class ComponentsPanelViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ComponentPin>>> _loadPins;
    private readonly ComponentProbe _probe;
    private readonly Func<ComponentPin, string> _destPathFor;
    private readonly ComponentFetchClient _fetch;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private IReadOnlyList<ComponentPin> _pins = [];

    public ComponentsPanelViewModel(
        Func<CancellationToken, Task<IReadOnlyList<ComponentPin>>> loadPins,
        ComponentProbe probe, Func<ComponentPin, string> destPathFor,
        ComponentFetchClient fetch, IUiErrorReporter errors, Action<Action> dispatch)
    {
        (_loadPins, _probe, _destPathFor, _fetch, _errors, _dispatch)
            = (loadPins, probe, destPathFor, fetch, errors, dispatch);
        DownloadCommand = new AsyncRelayCommand<ComponentRow>(DownloadAsync);
        CancelCommand = new RelayCommand<ComponentRow>(Cancel);
        RefreshCommand = new AsyncRelayCommand(() => LastLoad = ReloadAsync());
        LastLoad = ReloadAsync();
    }

    public ObservableCollection<ComponentRow> Rows { get; } = [];

    /// <summary>The last pin-load + probe round trip. Production fire-and-forgets; tests await
    /// it so the rows exist before they assert (the LastSave precedent).</summary>
    public Task LastLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The last download round trip, same contract as LastLoad.</summary>
    public Task LastDownload { get; private set; } = Task.CompletedTask;

    public IAsyncRelayCommand<ComponentRow> DownloadCommand { get; }
    public IRelayCommand<ComponentRow> CancelCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }

    private async Task ReloadAsync()
    {
        try
        {
            _pins = await _loadPins(CancellationToken.None);
            var states = _probe.Probe(_pins);
            _dispatch(() =>
            {
                Rows.Clear();
                foreach (var s in states) Rows.Add(ToRow(s));
            });
        }
        catch (Exception ex) { _errors.Report("Reading installed components", ex); }
    }

    private static ComponentRow ToRow(ComponentState s) => new(s.Id, s.Name, s.Pin)
    {
        Installed = s.Installed,
        SizeText = FormatSize(s.Bytes),
        Detail = s.Detail,
    };

    /// <summary>Invariant culture and one decimal: the panel is a size the user compares against
    /// free disk space, not a precise figure, and it must render identically on every machine.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "-";
        double mb = bytes / 1_000_000.0;
        return mb >= 1000
            ? string.Create(CultureInfo.InvariantCulture, $"{mb / 1000:0.0} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{mb:0.0} MB");
    }

    private Task DownloadAsync(ComponentRow? row) => LastDownload = RunDownloadAsync(row);

    private async Task RunDownloadAsync(ComponentRow? row)
    {
        // A probe-only row has no pin: refuse rather than start a helper with nothing to fetch.
        // The button is hidden for these, but a bound command must never rely on that.
        if (row?.Pin is not { } pin || row.Installed || row.IsDownloading) return;

        var cts = new CancellationTokenSource();
        lock (_running) _running[row.Id] = cts;
        _dispatch(() => { row.IsDownloading = true; row.Progress = 0; row.ProgressText = "0%"; });
        try
        {
            var progress = new Progress<ComponentFetchProgress>(p => _dispatch(() =>
            {
                row.Progress = p.Fraction;
                row.ProgressText = string.Create(CultureInfo.InvariantCulture,
                    $"{(int)Math.Round(p.Fraction * 100)}%");
            }));
            await _fetch.FetchAsync(pin, _destPathFor(pin), progress, cts.Token);
            _errors.Info("Installed " + pin.Name + ".", NoticeSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            // A user cancel. The partial file stays on disk ON PURPOSE - the helper RESUMES from
            // it on the next attempt, which is the whole reason it sends a range request.
            _errors.Info("Download cancelled - " + pin.Name + " will resume from where it stopped.");
        }
        catch (Exception ex) { _errors.Report("Downloading " + pin.Name, ex); }
        finally
        {
            lock (_running) _running.Remove(row.Id);
            cts.Dispose();
            _dispatch(() => { row.IsDownloading = false; row.Progress = 0; row.ProgressText = ""; });
            // Re-probe rather than assume: the helper deletes a hash-mismatched file, so
            // "the call returned" is not the same fact as "the component is installed".
            LastLoad = ReloadAsync();
        }
    }

    private void Cancel(ComponentRow? row)
    {
        if (row is null) return;
        lock (_running) { if (_running.TryGetValue(row.Id, out var cts)) cts.Cancel(); }
    }
}
```

- [ ] **Step 4: Host it on the settings page VM**

In `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs`, add a trailing optional constructor
parameter (after `engineBusy`, `:199`) and the property:

```csharp
        ComponentsPanelViewModel? components = null)
```
```csharp
        Components = components;
```
```csharp
    /// <summary>The Settings "Components" panel (Tier 1 plan D, T1-10, 2026-08-05). NULLABLE and
    /// null in unit tests, the same shape as the other optional collaborators on this VM: it
    /// spawns a helper process on demand, and no unit test should be able to reach that by
    /// accident. The XAML card binds Visibility to it via a null check.</summary>
    public ComponentsPanelViewModel? Components { get; }
```

- [ ] **Step 5: Add the card**

In `src/LocalScribe.App/SettingsPage.xaml`, insert immediately BEFORE the final "App" card
(`:410`):

```xml
            <!-- Components (Tier 1 plan D, T1-10, 2026-08-05): what is installed, how big it is,
                 and a Download for the pinned models. Collapsed entirely when the panel VM is
                 absent (unit tests, and any host that did not wire the fetch helper). -->
            <ui:Card Style="{StaticResource SectionCard}"
                     Visibility="{Binding Components, Converter={StaticResource NullToCollapsed}}">
                <StackPanel>
                    <TextBlock Text="Components" FontWeight="SemiBold" Margin="0,0,0,8" />
                    <TextBlock Style="{StaticResource Note}"
                               Text="Downloads run in a separate helper program, started only when you press Download. Each file is checked against a pinned SHA-256 and deleted if it does not match. An interrupted download resumes." />
                    <ItemsControl ItemsSource="{Binding Components.Rows}" Margin="0,8,0,0">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <!-- 2-column Grid, NOT a horizontal StackPanel: inside one, a
                                     TextBlock is measured with infinite width so TextWrapping is
                                     inert and the trailing button clips off the edge (the fix
                                     recorded at LiveViewWindow.xaml:382-386). -->
                                <Grid Margin="0,0,0,10">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="Auto" />
                                    </Grid.ColumnDefinitions>
                                    <StackPanel Grid.Column="0">
                                        <TextBlock Text="{Binding Name}" TextWrapping="Wrap" />
                                        <TextBlock Style="{StaticResource MutedText}" TextWrapping="Wrap">
                                            <Run Text="{Binding SizeText, Mode=OneWay}" />
                                        </TextBlock>
                                        <TextBlock Text="{Binding Detail}" Style="{StaticResource Note}"
                                                   TextWrapping="Wrap"
                                                   Visibility="{Binding Detail, Converter={StaticResource NullToCollapsed}}" />
                                        <ProgressBar Height="4" Margin="0,4,0,0" Maximum="1"
                                                     Value="{Binding Progress, Mode=OneWay}"
                                                     Visibility="{Binding IsDownloading, Converter={StaticResource BoolToVis}}" />
                                    </StackPanel>
                                    <StackPanel Grid.Column="1" Orientation="Horizontal"
                                                VerticalAlignment="Center" Margin="8,0,0,0">
                                        <TextBlock Text="Installed" Style="{StaticResource MutedText}"
                                                   VerticalAlignment="Center" Margin="0,0,8,0"
                                                   Visibility="{Binding Installed, Converter={StaticResource BoolToVis}}" />
                                        <TextBlock Text="{Binding ProgressText}" VerticalAlignment="Center"
                                                   Margin="0,0,8,0"
                                                   Visibility="{Binding IsDownloading, Converter={StaticResource BoolToVis}}" />
                                        <Button Content="Download" MinWidth="90"
                                                Command="{Binding DataContext.Components.DownloadCommand,
                                                                  RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                CommandParameter="{Binding}"
                                                Visibility="{Binding CanDownload, Converter={StaticResource BoolToVis}}" />
                                        <Button Content="Cancel" MinWidth="90"
                                                Command="{Binding DataContext.Components.CancelCommand,
                                                                  RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                CommandParameter="{Binding}"
                                                Visibility="{Binding IsDownloading, Converter={StaticResource BoolToVis}}" />
                                    </StackPanel>
                                </Grid>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </StackPanel>
            </ui:Card>
```

`ItemsControl` is NOT virtualised, which is correct here: this list is fixed at NINE rows - the six
pinned blobs `tools/fetch-models.ps1 -WriteComponentManifest` writes (four whisper models plus the
assistant chat and embedding models) and the three probe-only rows.
`NullToCollapsed` is `src/LocalScribe.App/NullToCollapsedConverter.cs`; add it (and `BoolToVis` if
absent) to `SettingsPage.xaml`'s `<UserControl.Resources>` in the same form the file already uses
for its other converters.

- [ ] **Step 6: Wire it in the composition root**

In `src/LocalScribe.App/App.xaml.cs`, immediately BEFORE the `new ViewModels.SettingsPageViewModel(`
call (`:252`):

```csharp
        // Components panel (Tier 1 plan D, T1-10, 2026-08-05). The fetch helper is resolved beside
        // this app's own base directory, the same way the diarizer is - build.ps1 publishes it
        // there. If it is absent the panel still lists state and remedies; only Download fails,
        // and it fails visibly through the reporter.
        var componentsVm = new ViewModels.ComponentsPanelViewModel(
            loadPins: ct => Services.ComponentCatalog.LoadAsync(
                LocalScribe.Core.Transcription.ModelPaths.ModelsRoot, ct),
            probe: new Services.ComponentProbe(
                LocalScribe.Core.Transcription.ModelPaths.Resolve,
                LocalScribe.Core.Import.FfmpegLocator.FindToolsDir,
                LocalScribe.Core.Assistant.AssistantHelperLocator.FindExe,
                System.IO.Path.Combine(AppContext.BaseDirectory, "LocalScribe.Diarizer.exe"),
                Services.ComponentProbe.MeasureFile),
            destPathFor: pin => LocalScribe.Core.Transcription.ModelPaths.Resolve(pin.File),
            fetch: new Services.ComponentFetchClient(new Services.ProcessComponentFetchHelper(
                System.IO.Path.Combine(AppContext.BaseDirectory, "LocalScribe.Fetch.exe"))),
            errors, dispatch);
```

Add `components: componentsVm,` as the last argument of the `SettingsPageViewModel` construction.

- [ ] **Step 7: Run the tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo
```
Expected: PASS - the App project in full, including `XamlHygieneTests` (the new card uses only
`SectionCard`, `MutedText` and `Note` from the shared dictionary, and no `#RRGGBB` literal) and
`NoNetworkInAppOrCoreTests` (the new VM's comments say "helper program", never a forbidden word).

- [ ] **Step 8: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add src/LocalScribe.App/ViewModels/ComponentsPanelViewModel.cs src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs src/LocalScribe.App/SettingsPage.xaml src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/ComponentsPanelViewModelTests.cs
git commit -m "feat(settings): Components panel with installed state, size and resumable download"
```

---

## Task 13: `build.ps1` - the whole publish, gated, packaged and (optionally) signed

Nothing in the repo builds a shippable artefact today. The three helper processes are published by
hand from command lines that live only in a smoke runbook, and the `tools/verify-*.ps1` guards that
exist to catch a bad publish are never actually run by anything.

### External prerequisite: the code-signing certificate

Signing needs a certificate **the owner must supply**; there is nothing to commit for it. Once a
code-signing certificate is installed in the current user's store, obtain its thumbprint and hand it
to the build:

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Subject, Thumbprint, NotAfter
$env:LOCALSCRIBE_SIGN_THUMBPRINT = '<40 hex characters, no spaces>'
```

The build then passes this to Velopack, which shells out to `signtool`:

```
--signParams "/sha1 <thumbprint> /fd sha256 /tr http://timestamp.digicert.com /td sha256"
```

**With no thumbprint the build still succeeds**, unsigned, with a loud warning - so CI works before
a certificate exists and an unsigned artefact can never be mistaken for a signed one.

### The publish ORDER, and why it is not negotiable

1. `dotnet build` the solution - fail fast before anything is published.
2. `dotnet test --filter "Category!=Fixture"` - the model-free gate.
3. Publish **App** self-contained into `<out>\app`.
4. Publish **Diarizer** self-contained SINGLE-FILE into a STAGING folder, run
   `verify-diarizer.ps1` while `<out>\app` is still clean, assert the staging folder holds exactly
   one file, and only THEN copy the exe beside the app.
5. Publish **Assistant** as a FOLDER into `<out>\app\assistant`, gate with
   `verify-assistant-publish.ps1`.
6. Publish **Mcp** into `<out>\app\mcp`, gate with `verify-mcp-publish.ps1`.
7. Publish **Fetch** self-contained single-file beside the app.
8. Copy the bundled models (tiny + base, both f16 and q8_0, plus Silero VAD, the two sherpa models
   and `component-manifest.json`) into `<out>\app\models`.
9. `vpk pack`.

**Publish step 4 needs the layout guard CORRECTED before it can be used as a gate, and this task's
Step 5 does that.** `tools/verify-diarizer.ps1:26-30` currently forbids three names beside the app -
`onnxruntime.dll`, `sherpa-onnx-c-api.dll` and `LocalScribe.Diarizer.exe` - and TWO of those three
are wrong against a real shipped layout:

- `LocalScribe.Diarizer.exe` beside the app is REQUIRED, not forbidden: `CompositionRoot.cs:219`
  is `Path.Combine(AppContext.BaseDirectory, "LocalScribe.Diarizer.exe")`. Leaving it on the list
  means the shipped layout permanently violates the repo's own guard, the guard can never be
  re-run on a finished build, and the next person to run it gets a false failure whose natural fix
  is to delete the gate.
- `onnxruntime.dll` beside the app is EXPECTED in a RID-specific publish. **Measured 2026-08-05**
  by publishing `src/LocalScribe.Diarizer -r win-x64 --self-contained true` without
  `PublishSingleFile`: the output contained `onnxruntime.dll`, `sherpa-onnx.dll` and
  `sherpa-onnx-c-api.dll` at the ROOT and **no `runtimes/` folder at all** - a RID publish flattens
  `runtimes/<rid>/native/` into the output directory. `LocalScribe.App` references
  `Microsoft.ML.OnnxRuntime 1.22.0` (`LocalScribe.Core.csproj:18`), so step 3's app publish puts
  App's OWN `onnxruntime.dll` in `$appDir`. The guard as written would fail EVERY build at step 4,
  on the app's own correct file.

This task's Step 5 therefore reduces `$forbiddenBesideApp` to `sherpa-onnx-c-api.dll` alone - the one name that
is unambiguously sherpa's, that App can never legitimately produce, and that co-travels with every
sherpa payload (so it still catches the whole-folder dev copy the guard was written for). The
ordering is KEPT: the guard runs while `$appDir` holds nothing but the app publish, so any sherpa
payload it finds can only have come from the app publish itself - a resurrected `ProjectReference`,
exactly the regression `LocalScribe.App.csproj:24-46` warns about. Run it later and a subsequent
copy step would mask or mis-attribute the failure. The script also adds the check the guard cannot
express - that the staging folder contains nothing but the exe.

**Step 8 bundles tiny and base only.** `verify-import-models.ps1` checks for the LARGE models and is
therefore NOT a default gate - it runs only under `-WithLargeModels`. Bundling large-v3-turbo would
add ~4.3 GB to the installer, which is precisely what the in-app Components panel exists to avoid.

**Files:**
- Create: `build.ps1`
- Modify: `src/LocalScribe.App/LocalScribe.App.csproj`
- Modify: `src/LocalScribe.App/App.xaml.cs:44` (the Velopack install hook)
- Modify: `tools/verify-diarizer.ps1:26-30`, `:58` (the layout guard's forbidden list and its
  failure text)
- Create: `tests/LocalScribe.App.Tests/ShippingScriptTests.cs`

`src/Directory.Build.props` is **read but NOT modified** by this task - see Step 3.

**Interfaces:**
- Consumes: `src/Directory.Build.props` with the literal `<Version>0.9.0</Version>` element Plan A
  writes (shared contract, section 3, and Plan A's `BuildVersionTests` pins that exact string).
- Produces: `build.ps1` parameters `-Configuration`, `-OutDir`, `-CertThumbprint`,
  `-WithLargeModels`, `-SkipTests`. Task 14 calls none of them - CI runs `dotnet` directly - but the
  test below pins the ordering.

- [ ] **Step 1: Write the failing test**

Create `tests/LocalScribe.App.Tests/ShippingScriptTests.cs`:

```csharp
using System.IO;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins build.ps1's load-bearing ORDER as source text (Tier 1 plan D, T1-10,
/// 2026-08-05). A build script cannot be executed from a unit test - it publishes four projects
/// and needs a network for the Velopack tool - but two facts about it are worth more than the
/// rest of it put together, and both are silently reversible by a well-meaning edit:
///
/// (1) verify-diarizer.ps1 must run while the app directory holds NOTHING BUT THE APP PUBLISH -
///     i.e. before any helper payload is copied in. That guard asserts sherpa-onnx-c-api.dll is
///     absent from the app directory (sherpa's ORT 1.24.4 would shadow App's 1.22 and break
///     Silero VAD), so running it at that one point means any hit can only have come from the app
///     publish itself - a resurrected ProjectReference. Run it later and a copy step masks or
///     mis-attributes the failure.
/// (2) The signing path must DEGRADE to an unsigned build rather than failing, so CI works
///     before a certificate exists.</summary>
public sealed class ShippingScriptTests
{
    private static string Script()
        => File.ReadAllText(Path.Combine(RepoPaths.SolutionRoot(), "build.ps1"));

    [Fact]
    public void The_build_script_exists_and_chains_every_publish_guard()
    {
        string s = Script();
        foreach (string guard in new[]
                 { "verify-diarizer.ps1", "verify-assistant-publish.ps1", "verify-mcp-publish.ps1" })
            Assert.Contains(guard, s);
    }

    [Fact]
    public void The_diarizer_guard_runs_before_the_helper_is_copied_beside_the_app()
    {
        string s = Script();
        int guard = s.IndexOf("verify-diarizer.ps1", StringComparison.Ordinal);
        int copy = s.IndexOf("Copy-Item $diarizerExe", StringComparison.Ordinal);
        Assert.True(guard >= 0 && copy >= 0);
        Assert.True(guard < copy,
            "verify-diarizer.ps1 asserts sherpa's loose payload is ABSENT from the app directory, "
            + "so it must run while that directory holds only the app publish - before any helper "
            + "is copied into it, so a hit can only mean the app publish itself produced it.");
    }

    [Fact]
    public void An_absent_certificate_warns_loudly_and_still_produces_an_unsigned_build()
    {
        string s = Script();
        Assert.Contains("UNSIGNED", s);
        Assert.Contains("LOCALSCRIBE_SIGN_THUMBPRINT", s);
        Assert.Contains("--signParams", s);
    }

    [Fact]
    public void The_large_models_guard_is_opt_in_so_a_tiny_base_bundle_is_not_a_failure()
    {
        string s = Script();
        Assert.Contains("verify-import-models.ps1", s);
        Assert.Contains("WithLargeModels", s);
    }

    [Fact]
    public void The_model_free_test_filter_is_the_gate()
    {
        Assert.Contains("Category!=Fixture", Script());
    }

    [Fact]
    public void Both_single_file_publishes_bundle_their_natives_instead_of_leaving_them_loose()
    {
        // PublishSingleFile ALONE leaves native dependencies loose beside the exe, and both the
        // diarizer and the fetch helper are copied out of their staging folder BY EXE ONLY. Drop
        // the flag from either and the shipped helper cannot start - the diarizer half surfaces as
        // a DiarisationException, the fetch half as "the download helper exited with code N", and
        // neither says the real reason. There must be exactly one flag per single-file publish.
        string s = Script();
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            s, @"-p:IncludeNativeLibrariesForSelfExtract=true").Count);
        Assert.Contains("$strayFetch", s);      // and the assertion that proves it took effect
    }

    [Fact]
    public void The_package_version_is_shape_checked_so_an_unevaluated_property_cannot_reach_vpk()
    {
        // build.ps1 reads Directory.Build.props with [xml], which does NOT evaluate MSBuild
        // property functions. An emptiness guard cannot catch a literal "$(Version)" - it is
        // non-empty, therefore truthy - so the guard must check the SHAPE. Without this, a
        // packVersion that is not SemVer only fails at the manual packaging run, deep inside vpk.
        string s = Script();
        Assert.Contains(@"'^\d+\.\d+\.\d+$'", s);
        Assert.DoesNotContain("PackableVersion", s);
    }
}
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ShippingScriptTests" --nologo
```
Expected: FAIL - `DirectoryNotFoundException` / `FileNotFoundException` for `build.ps1`.

- [ ] **Step 3: Confirm the version source - and change NOTHING**

Plan A created `src/Directory.Build.props` with a literal `<Version>0.9.0</Version>` and an
`<InformationalVersion>` carrying the git SHA. Velopack's `--packVersion` must be a plain SemVer,
which `<Version>` already is, so build.ps1 reads that element directly in Step 6.

**Do NOT add a `<PackableVersion>$(Version)</PackableVersion>` property.** It was in an earlier
draft of this plan and it is a trap: build.ps1 reads the props file with `[xml]`, and raw XML
parsing does not evaluate MSBuild property functions, so `$version` would come back as the LITERAL
string `$(Version)`. A non-empty literal is truthy, so an `if (-not $version)` guard cannot catch
it, and `vpk pack --packVersion '$(Version)'` would fail deep inside the packager on a value that
is not SemVer. Reading Plan A's plain-text `<Version>` element removes the indirection entirely.

Verify the element is there before continuing:

```powershell
Select-String -Path "F:\LocalScribe\src\Directory.Build.props" -Pattern "<Version>"
```
Expected: one hit, `<Version>0.9.0</Version>`. If Plan A has not landed, stop - this task depends
on it.

- [ ] **Step 4: Add the Velopack install hook**

Add to `src/LocalScribe.App/LocalScribe.App.csproj`'s existing `<ItemGroup>` of package references:

```xml
    <PackageReference Include="Velopack" Version="0.0.1298" />
```

Add as the FIRST statement of `OnStartup` in `src/LocalScribe.App/App.xaml.cs` (before
`base.OnStartup(e)`):

```csharp
        // Velopack install/uninstall hooks (Tier 1 plan D, T1-10, 2026-08-05). MUST run before
        // anything else: on an install or uninstall run the host passes a hook argument, and this
        // call performs the shortcut/registry work and exits the process - anything above it
        // would execute during a silent install.
        //
        // Local hooks ONLY. This product never constructs Velopack's updater type: the spec's
        // out-of-scope list rules out in-process auto-update, and the zero-network pin test
        // enforces that by name. Installing is an explicit user act with an installer the user
        // ran; a program that phones home on its own is a different thing.
        //
        // The wording above is DELIBERATELY indirect - naming that type here would itself fail
        // NoNetworkInAppOrCoreTests, which scans every .cs under src/LocalScribe.App including
        // comments. Do not "clarify" it by spelling the class name out.
        //
        // REJECTED: a custom Program.Main with <StartupObject>. The WPF SDK generates the entry
        // point from App.xaml's ApplicationDefinition, and replacing it means suppressing that
        // generation - a lot of build surgery to move one statement a few microseconds earlier.
        Velopack.VelopackApp.Build().Run();
```

- [ ] **Step 5: Correct the diarizer layout guard so a shipped build can actually pass it**

In `tools/verify-diarizer.ps1`, replace the `$forbiddenBesideApp` block (`:26-30`):

```powershell
# Absent from the APP directory. Their presence is the ORT 1.24.4-over-1.22.0 collision.
#
# Tier 1 plan D, T1-10 (2026-08-05): this list was three names and two of them were wrong against
# a real shipped layout, so build.ps1 could never have used this guard as a gate.
#   - LocalScribe.Diarizer.exe was REMOVED. It is not a collision, it is the REQUIRED layout:
#     CompositionRoot.cs:219 resolves the helper at Path.Combine(AppContext.BaseDirectory,
#     "LocalScribe.Diarizer.exe"). The single-file publish carries its natives INSIDE the exe
#     (-p:IncludeNativeLibrariesForSelfExtract=true), so the exe beside the app is safe and the
#     loose DLLs are the actual hazard.
#   - onnxruntime.dll was REMOVED. Measured 2026-08-05: a RID-specific publish FLATTENS
#     runtimes/<rid>/native/ into the output root and emits no runtimes/ folder at all, and
#     LocalScribe.App references Microsoft.ML.OnnxRuntime 1.22.0, so App's OWN onnxruntime.dll
#     legitimately sits beside the app in every published build. A name-based absence check cannot
#     tell App's 1.22 from sherpa's 1.24.4 and would fail every build on the correct file.
# What is left is the one name that is unambiguously sherpa's and that App can never produce. It is
# a COMPLETE discriminator, not a weakening: the same measured publish emitted onnxruntime.dll,
# sherpa-onnx.dll and sherpa-onnx-c-api.dll together, so the whole-folder dev copy this guard was
# written for still trips it. REJECTED: sniffing onnxruntime.dll's FileVersion for "1.22.x" - it
# adds a second thing to maintain on every ORT bump for no extra scenario caught.
$forbiddenBesideApp = @(
    'sherpa-onnx-c-api.dll'
)
```

And replace the last line of its failure block (`:58`), which no longer describes the rule:

```powershell
    Write-Host "The helper's loose native payload must never be flattened into the app directory."
    Write-Host "The single-file LocalScribe.Diarizer.exe itself SHOULD be there - CompositionRoot"
    Write-Host "resolves it at AppContext.BaseDirectory - and is deliberately not checked here."
```

Confirm the guard still refuses a genuinely bad layout before trusting it:

```powershell
cd F:\LocalScribe
$bad = Join-Path $env:TEMP ("ls-guard-" + [guid]::NewGuid())
New-Item -ItemType Directory -Force $bad, "$bad\app" | Out-Null
# Set-Content CREATES the file - never New-Item -Force on a file, which truncates an existing one.
# Non-empty on purpose: the guard's $required check rejects a zero-length exe.
Set-Content "$bad\LocalScribe.Diarizer.exe" 'x'
Copy-Item "$bad\LocalScribe.Diarizer.exe" "$bad\app\sherpa-onnx-c-api.dll"
.\tools\verify-diarizer.ps1 -PublishDir $bad -AppDir "$bad\app"; "exit=$LASTEXITCODE"
Remove-Item "$bad\app\sherpa-onnx-c-api.dll"
Copy-Item "$bad\LocalScribe.Diarizer.exe" "$bad\app\LocalScribe.Diarizer.exe"
.\tools\verify-diarizer.ps1 -PublishDir $bad -AppDir "$bad\app"; "exit=$LASTEXITCODE"
Remove-Item -Recurse -Force $bad
```
Expected: the first run FAILS (`exit=1`) naming `sherpa-onnx-c-api.dll`; the second PASSES
(`exit=0`) with the exe beside the app, which is the shipped layout.

- [ ] **Step 6: Write the build script**

Create `build.ps1`:

```powershell
# build.ps1 - the whole shippable build (Tier 1 plan D, T1-10, 2026-08-05).
#
# Publishes the four processes in the ONE order that works, runs every existing
# tools/verify-*.ps1 layout guard as a gate, bundles the small models, and packages the result
# with Velopack. Signing is optional and degrades LOUDLY rather than failing, so this script
# works in CI and on a machine with no certificate.
#
#   .\build.ps1                                   # unsigned, tiny+base models
#   .\build.ps1 -CertThumbprint <40 hex>          # signed
#   .\build.ps1 -WithLargeModels                  # also bundle + verify large-v3-turbo/medium.en
#   .\build.ps1 -SkipTests                        # local iteration only; CI never passes this
param(
    [string] $Configuration = 'Release',
    [string] $OutDir = (Join-Path $PSScriptRoot 'publish'),
    # Falls back to the env var so CI can supply it as a secret without it reaching a command line.
    [string] $CertThumbprint = $env:LOCALSCRIBE_SIGN_THUMBPRINT,
    [switch] $WithLargeModels,
    [switch] $SkipTests
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$rid = 'win-x64'
$appDir   = Join-Path $OutDir 'app'
$stageDir = Join-Path $OutDir 'stage'
$relDir   = Join-Path $OutDir 'releases'

function Step($text) { Write-Host ""; Write-Host "=== $text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "FAIL: $text" -ForegroundColor Red; exit 1 }

# A running LocalScribe.App.exe LOCKS Core.dll and the build dies with MSB3027, which reads like a
# compile error and is not one. Say so plainly rather than letting the user guess. Never kill it -
# that is a standing rule in this repo, and the user may be recording.
$running = Get-Process -Name 'LocalScribe.App' -ErrorAction SilentlyContinue
if ($running) {
    Fail "LocalScribe.App.exe is running (PID $($running.Id -join ', ')) and holds a lock on Core.dll. Close it and re-run."
}

Remove-Item -Recurse -Force $OutDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $appDir, $stageDir, $relDir | Out-Null

Step "1/9 build"
dotnet build (Join-Path $repo 'LocalScribe.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { Fail "solution build failed" }

Step "2/9 test (model-free gate)"
if ($SkipTests) {
    Write-Host "  SKIPPED by -SkipTests - never use this for a build you intend to ship."
} else {
    dotnet test (Join-Path $repo 'LocalScribe.slnx') -c $Configuration --filter "Category!=Fixture" --nologo
    if ($LASTEXITCODE -ne 0) { Fail "the model-free suite is not green - nothing is published" }
}

Step "3/9 publish app"
dotnet publish (Join-Path $repo 'src\LocalScribe.App') -c $Configuration -r $rid --self-contained true -o $appDir --nologo
if ($LASTEXITCODE -ne 0) { Fail "app publish failed" }

Step "4/9 publish diarizer (single-file, self-contained) and gate it"
$diarStage = Join-Path $stageDir 'diarizer'
# IncludeNativeLibrariesForSelfExtract is LOAD-BEARING, not an optimisation: PublishSingleFile
# alone still drops onnxruntime.dll and sherpa-onnx-c-api.dll LOOSE beside the exe, and copying
# those next to the app would shadow App's own ORT 1.22 with sherpa's 1.24.4 and break Silero VAD
# (LocalScribe.App.csproj's long comment calls that "actively unsafe").
dotnet publish (Join-Path $repo 'src\LocalScribe.Diarizer') -c $Configuration -r $rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $diarStage --nologo
if ($LASTEXITCODE -ne 0) { Fail "diarizer publish failed" }

# Gate BEFORE the copy: verify-diarizer.ps1 asserts the sherpa payload is ABSENT from the app
# directory, so it can only pass while that directory is still clean.
& (Join-Path $repo 'tools\verify-diarizer.ps1') -PublishDir $diarStage -AppDir $appDir
if ($LASTEXITCODE -ne 0) { Fail "diarizer layout guard failed" }

# The half that guard cannot express: prove the single-file publish bundled its natives instead of
# leaving them loose. Anything beyond the exe (and its .pdb) here IS the collision.
$stray = Get-ChildItem $diarStage -File |
    Where-Object { $_.Name -ne 'LocalScribe.Diarizer.exe' -and $_.Extension -ne '.pdb' }
if ($stray) {
    Fail ("the diarizer publish left loose files beside the exe - IncludeNativeLibrariesForSelfExtract " +
          "did not take effect: " + ($stray.Name -join ', '))
}

$diarizerExe = Join-Path $diarStage 'LocalScribe.Diarizer.exe'
Copy-Item $diarizerExe -Destination $appDir -Force   # CompositionRoot.cs resolves it beside the app

Step "5/9 publish assistant (FOLDER) and gate it"
$assistantDir = Join-Path $appDir 'assistant'
# A FOLDER publish, deliberately not single-file: LLamaSharp probes its own
# runtimes/<rid>/native/<variant>/ layout relative to the helper's directory, and a single-file
# self-extract lands the natives where that probe never looks - which is how the first deployment
# of this helper shipped broken.
dotnet publish (Join-Path $repo 'src\LocalScribe.Assistant') -c $Configuration -r $rid --self-contained true -o $assistantDir --nologo
if ($LASTEXITCODE -ne 0) { Fail "assistant publish failed" }
& (Join-Path $repo 'tools\verify-assistant-publish.ps1') -PublishDir $assistantDir
if ($LASTEXITCODE -ne 0) { Fail "assistant layout guard failed" }

Step "6/9 publish mcp and gate it"
$mcpDir = Join-Path $appDir 'mcp'
dotnet publish (Join-Path $repo 'src\LocalScribe.Mcp') -c $Configuration -r $rid --self-contained true -o $mcpDir --nologo
if ($LASTEXITCODE -ne 0) { Fail "mcp publish failed" }
& (Join-Path $repo 'tools\verify-mcp-publish.ps1') -PublishDir $mcpDir
if ($LASTEXITCODE -ne 0) { Fail "mcp layout guard failed" }

Step "7/9 publish the component fetch helper"
$fetchStage = Join-Path $stageDir 'fetch'
# IncludeNativeLibrariesForSelfExtract is LOAD-BEARING here for the same reason it is at step 4:
# PublishSingleFile ALONE leaves a self-contained publish's native dependencies LOOSE beside the
# exe, and only the exe is copied out of the staging folder below. Without the flag the shipped
# helper cannot start, EVERY Download fails, and the user sees "The download helper exited with
# code N" from ComponentFetchClient with nothing to act on.
dotnet publish (Join-Path $repo 'src\LocalScribe.Fetch') -c $Configuration -r $rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $fetchStage --nologo
if ($LASTEXITCODE -ne 0) { Fail "fetch helper publish failed" }

# Same stray-file assertion as step 4, and for the same reason: only the exe is copied out, so
# anything else left in the staging folder is a file the shipped helper will look for and not find.
$strayFetch = Get-ChildItem $fetchStage -File |
    Where-Object { $_.Name -ne 'LocalScribe.Fetch.exe' -and $_.Extension -ne '.pdb' }
if ($strayFetch) {
    Fail ("the fetch helper publish left loose files beside the exe - IncludeNativeLibrariesForSelfExtract " +
          "did not take effect: " + ($strayFetch.Name -join ', '))
}

Copy-Item (Join-Path $fetchStage 'LocalScribe.Fetch.exe') -Destination $appDir -Force

Step "8/9 bundle models"
$modelsOut = Join-Path $appDir 'models'
New-Item -ItemType Directory -Force $modelsOut | Out-Null
$modelsIn = Join-Path $repo 'models'
# tiny + base ONLY (both f16 for CUDA and q8_0 for CPU/Vulkan, per ModelFileResolver), plus the
# VAD and the two sherpa models. large-v3-turbo and medium.en are ~4.3 GB and the assistant's two
# GGUFs are another ~2.8 GB; all six are deliberately NOT bundled - that is exactly what the in-app
# Components panel is for, and tools/fetch-models.ps1 -WriteComponentManifest pins every one of
# them so the panel can fetch them.
#
# assistant-manifest.json IS bundled even though its weights are not, and that is not an
# inconsistency: LocalScribe.Core.Assistant.AssistantModelManifest.LoadAsync reads
# models/assistant-manifest.json to learn each model's file name, nativeCtx and pinned sha256, and
# without it a model the user downloads through the Components panel would sit on disk unusable -
# an empty manifest means "no models installed" (design 7.7, features off with an explainer).
$bundled = @(
    'silero_vad.onnx'
    'ggml-tiny.en.bin'; 'ggml-tiny.en-q8_0.bin'
    'ggml-base.en.bin'; 'ggml-base.en-q8_0.bin'
    '3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
    'component-manifest.json'
    'assistant-manifest.json'
)
if ($WithLargeModels) {
    $bundled += @('ggml-large-v3-turbo.bin'; 'ggml-large-v3-turbo-q5_0.bin'
                  'ggml-medium.en.bin'; 'ggml-medium.en-q5_0.bin')
}
$missing = @()
foreach ($name in $bundled) {
    $src = Join-Path $modelsIn $name
    if (Test-Path $src) { Copy-Item $src -Destination $modelsOut -Force } else { $missing += $name }
}
# The segmentation model ships as a FOLDER (tar extraction layout), not a loose file.
$seg = Join-Path $modelsIn 'sherpa-onnx-pyannote-segmentation-3-0'
if (Test-Path $seg) { Copy-Item $seg -Destination $modelsOut -Recurse -Force } else { $missing += 'sherpa-onnx-pyannote-segmentation-3-0/' }
if ($missing.Count -gt 0) {
    Fail ("models missing from $modelsIn - run tools\fetch-models.ps1 (and " +
          "tools\fetch-models.ps1 -WriteComponentManifest): " + ($missing -join ', '))
}
if ($WithLargeModels) {
    # Opt-in ONLY: this guard checks for the large weights, which the default tiny+base bundle
    # deliberately omits, so running it unconditionally would fail every normal build.
    & (Join-Path $repo 'tools\verify-import-models.ps1') -ModelsDir $modelsOut
    if ($LASTEXITCODE -ne 0) { Fail "bundled large-model guard failed" }
}

Step "9/9 package (Velopack)"
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Fail "the Velopack CLI is not installed. Install it once with: dotnet tool install -g vpk"
}
# Read Plan A's LITERAL <Version> element. [xml] parsing does NOT evaluate MSBuild property
# functions, so any property whose value is an expression - a <PackableVersion>$(Version)</...>,
# say - would come back as the literal string "$(Version)", which is non-empty and therefore
# truthy, sails past an emptiness guard, and reaches vpk as a package version that is not SemVer.
# Hence the SHAPE check below rather than a presence check: it rejects an unevaluated expression, a
# "+sha" build-metadata suffix and a typo, all loudly, before anything is packaged.
[xml] $props = Get-Content (Join-Path $repo 'src\Directory.Build.props')
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ }) -join ''
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    Fail ("src\Directory.Build.props <Version> must be a plain three-part SemVer for --packVersion; " +
          "got '$version'. InformationalVersion carries the +sha suffix - Velopack must not see it.")
}

$vpkArgs = @(
    'pack'
    '--packId', 'LocalScribe'
    '--packVersion', $version
    '--packDir', $appDir
    '--mainExe', 'LocalScribe.App.exe'
    '--packTitle', 'LocalScribe'
    '--outputDir', $relDir
    '--icon', (Join-Path $repo 'src\LocalScribe.App\Assets\LocalScribe.ico')
)
if ($CertThumbprint) {
    # signtool is shelled out to by Velopack; the timestamp URL keeps the signature valid after the
    # certificate expires, which for a product a solicitor installs once and keeps is the point.
    $vpkArgs += @('--signParams',
        "/sha1 $CertThumbprint /fd sha256 /tr http://timestamp.digicert.com /td sha256")
    Write-Host "  signing with certificate $CertThumbprint"
} else {
    Write-Host ""
    Write-Host "  ******************************************************************" -ForegroundColor Yellow
    Write-Host "  *  WARNING: building UNSIGNED.                                   *" -ForegroundColor Yellow
    Write-Host "  *  Windows SmartScreen will warn every user who runs the setup,  *" -ForegroundColor Yellow
    Write-Host "  *  and nothing proves the installer came from you.               *" -ForegroundColor Yellow
    Write-Host "  *  Supply a certificate thumbprint to sign:                      *" -ForegroundColor Yellow
    Write-Host "  *    Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert         *" -ForegroundColor Yellow
    Write-Host "  *    .\build.ps1 -CertThumbprint <40 hex>                        *" -ForegroundColor Yellow
    Write-Host "  *  or set LOCALSCRIBE_SIGN_THUMBPRINT.                           *" -ForegroundColor Yellow
    Write-Host "  ******************************************************************" -ForegroundColor Yellow
    Write-Host ""
}
& vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { Fail "Velopack packaging failed" }

Write-Host ""
Write-Host "DONE -> $relDir" -ForegroundColor Green
if (-not $CertThumbprint) { Write-Host "  (this build is UNSIGNED)" -ForegroundColor Yellow }
exit 0
```

- [ ] **Step 7: Run the tests and confirm they pass**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ShippingScriptTests|FullyQualifiedName~NoNetworkInAppOrCoreTests" --nologo
```
Expected: PASS (7 + 4 facts). `NoNetworkInAppOrCoreTests` is re-run HERE on purpose: Step 4 adds
the only comment this plan writes into `src/LocalScribe.App` after that pin landed, and its
subject - Velopack's updater type - is one of the names the regex forbids.

- [ ] **Step 8: Run the script once, unsigned, and confirm it produces an installer**

```powershell
cd F:\LocalScribe
dotnet tool install -g vpk
.\build.ps1 -SkipTests
```
Expected: nine `===` steps, the yellow UNSIGNED banner, and `DONE -> F:\LocalScribe\publish\releases`
containing a `LocalScribe-win-Setup.exe`. If step 4 or step 7 fails on its stray-file check, the SDK
dropped the native DLLs loose - re-check `IncludeNativeLibrariesForSelfExtract` on that publish
before touching the guard.

- [ ] **Step 9: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git status --short
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file. `publish/` must NOT appear - `.gitignore:188` already covers it.

```bash
cd F:/LocalScribe
git add build.ps1 tools/verify-diarizer.ps1 src/LocalScribe.App/LocalScribe.App.csproj src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/ShippingScriptTests.cs
git commit -m "build: build.ps1 with gated publishes, Velopack packaging and optional signing"
```

---

## Task 14: GitHub Actions CI

There is no `.github/` directory anywhere in the repo, so nothing has ever built or tested this
solution outside a developer's machine.

**Files:**
- Create: `.github/workflows/ci.yml`
- Modify: `tests/LocalScribe.App.Tests/ShippingScriptTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing consumed by later tasks.

**Two constraints govern this file.** `XamlHygieneTests.RepoPaths.SolutionRoot()` walks up from the
test output directory looking for `.git`, so the checkout must NOT use `fetch-depth: 0`-less
sparse or archive modes that omit it (the default `actions/checkout` is fine) and the run must NOT
use an isolated `BaseOutputPath`. And the whole point of `--filter "Category!=Fixture"` is that the
five fixture-gated classes need model weights and private, never-committed corpora - they can never
run on a hosted runner, so they get a manual job that a human triggers on a machine that has them.

- [ ] **Step 1: Write the failing test**

Append to `tests/LocalScribe.App.Tests/ShippingScriptTests.cs`:

```csharp
    [Fact]
    public void CI_builds_and_runs_the_model_free_suite_on_push_with_fixtures_kept_manual()
    {
        string wf = File.ReadAllText(Path.Combine(
            RepoPaths.SolutionRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains("windows-latest", wf);            // net10.0-windows + WPF: no other runner works
        Assert.Contains("dotnet build", wf);
        Assert.Contains("Category!=Fixture", wf);
        // The fixture suite needs model weights and privileged audio that are never committed, so
        // it can only ever be a MANUAL run on a machine that has them.
        Assert.Contains("workflow_dispatch", wf);
        Assert.Contains("Category=Fixture", wf);
    }
```

- [ ] **Step 2: Run it and confirm it fails**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~CI_builds_and_runs_the_model_free_suite" --nologo
```
Expected: FAIL - `DirectoryNotFoundException` for `.github\workflows`.

- [ ] **Step 3: Write the workflow**

Create `.github/workflows/ci.yml`:

```yaml
# Tier 1 plan D, T1-10 (2026-08-05). The first CI this repo has had.
#
# windows-latest is not a preference: every project targets net10.0-windows, the app is WPF, and
# the capture stack is WASAPI.
#
# The model-free suite is the gate. The five [Trait("Category","Fixture")] classes need Whisper
# weights, a diarisation corpus and privileged audio - none of which is committed, and none of
# which may ever be - so they get a manual job instead, run by a human on a machine that has them.
name: ci

on:
  push:
  pull_request:
  workflow_dispatch:

jobs:
  build-and-test:
    runs-on: windows-latest
    steps:
      # Default checkout depth is fine, but the .git DIRECTORY must exist: XamlHygieneTests walks
      # up from the test output folder looking for it, and validates the wrong tree without it.
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore
        run: dotnet restore LocalScribe.slnx

      - name: Build
        run: dotnet build LocalScribe.slnx -c Release --no-restore --nologo

      # No isolated BaseOutputPath here, deliberately: an output path outside the repo makes
      # XamlHygieneTests walk PAST the checkout and produce five false failures.
      - name: Test (model-free)
        run: dotnet test LocalScribe.slnx -c Release --no-build --filter "Category!=Fixture" --nologo

  fixtures:
    # Manual only. These tests need assets that are deliberately absent from the repository, so
    # they would fail on every hosted runner for a reason that says nothing about the code.
    if: github.event_name == 'workflow_dispatch'
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Fetch dev models
        run: pwsh tools/fetch-models.ps1

      - name: Test (fixture-gated)
        run: dotnet test LocalScribe.slnx -c Release --filter "Category=Fixture" --nologo
```

- [ ] **Step 4: Run the test and confirm it passes**

```
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ShippingScriptTests" --nologo
```
Expected: PASS (8 facts - the 7 from Task 13 plus this one).

- [ ] **Step 5: Run the whole suite one last time**

```
dotnet test F:\LocalScribe\LocalScribe.slnx --filter "Category!=Fixture"
```
Expected: PASS. Core 1195/1195 (1186 + 9 `TranscriptCitationTests`), App 1042/1042 (984 + 58 new
facts), Mcp 6/6. The 58 are: `NoticeSeverityRoutingTests` 4, `ShellOwnerWiringTests` 3,
`ExportDialogStatusTests` 5, `DialogLocalStatusTests` 1 + one appended fact each in
`ImportDialogViewModelTests` and `RetranscribeDialogViewModelTests` (3), `ReadViewStatusTests` 3,
`SessionNoticeTests` 3, `ReadViewCopyTests` 6, `NoNetworkInAppOrCoreTests` 4 (two theory cases, the
coverage guard, the fetch-project fact), `ComponentProbeTests` 7, `ComponentFetchClientTests` 6,
`ComponentsPanelViewModelTests` 6, `ShippingScriptTests` 8 - twelve new classes and two extended.
**Judge by failing test NAME, not by count** - the arithmetic above is an estimate and the baseline
rule is the authority.

- [ ] **Step 6: Byte-scan and commit**

```powershell
cd F:\LocalScribe
git status --short
git diff --name-only | ForEach-Object { $b=[IO.File]::ReadAllBytes($_); $n=($b | Where-Object {$_ -gt 127}).Count; "$n  $_" }
```
Expected: `0` for every file.

```bash
cd F:/LocalScribe
git add .github/workflows/ci.yml tests/LocalScribe.App.Tests/ShippingScriptTests.cs
git commit -m "ci: build and the model-free suite on push, fixtures on manual dispatch"
```

---

## Smoke items (a static suite cannot settle any of these)

Run after the branch is merged, on a real machine, and record the outcome in the round's memory
file.

1. **Owner (T1-5, Task 2).** Launch cold, do NOT open the manager window, start a recording so the
   pill is up, then open Export from the Record console. It must centre on the shell if the shell is
   open and on screen otherwise - never on the pill. Then close the manager window and open Export
   again from a read view: it must open, not throw.
2. **Severity (T1-5, Task 1).** Export a session and confirm the "Exported to ..." bar is GREEN,
   then force a failure (pick a read-only folder) and confirm the next one is RED.
3. **Failed Start (T1-5, Task 6).** Unplug or disable the pinned microphone, press Record, and
   confirm the console shows a red notice bar - with Focus Assist ON, so the tray balloon is
   suppressed and the bar is the only surface.
4. **Cancel an export (T1-5, Task 3).** Export a long session as `.zip`, press Stop mid-write, and
   confirm no partial file is left at the destination.
5. **Copy with citation (T1-9, Task 8).** Select three turns in a read view with Ctrl+click, then
   press **Ctrl+C** with the list focused and confirm the plain text arrives on the clipboard -
   `InputBinding` is a `Freezable` outside both trees and a mis-resolved command binding fails
   SILENTLY, and no test in this suite can catch a dead gesture. Then press **Ctrl+Shift+C**, paste
   into Word, and check every quotation is attributed, in transcript order, with the right version.
   Then scroll a 2-hour transcript top to bottom and confirm scrolling is no slower than before
   (virtualisation intact).
6. **Install (T1-10, Task 13).** Run `.\build.ps1` on a clean machine, install the produced setup,
   and confirm: the app launches, Split Speakers runs (the diarizer resolved), and import works if
   ffmpeg is present. Then check Settings -> Components: the **Assistant helper** row must read as
   NOT installed with the detail "the assistant helper is installed but its language model is not
   - download ... below", because the installer deliberately ships the helper without its ~2.5 GB
   model. Download "Assistant model (Qwen3-4B-Instruct-2507 Q4_K_M)" from that panel, press
   Refresh, confirm the row flips to Installed, and only THEN confirm the assistant answers (which
   also proves `assistant-manifest.json` was bundled - without it a downloaded GGUF loads as "no
   models installed").
7. **Download a component (T1-10, Task 12).** With `large-v3-turbo` absent, open Settings ->
   Components, press Download, and confirm progress advances. Kill the network mid-download,
   confirm the failure is reported, then press Download again and confirm it RESUMES rather than
   restarting from zero.
   **Installed means PRESENT, not VERIFIED.** `ComponentProbe` is a presence-and-size probe, so
   corrupting a downloaded file still reads as installed after Refresh - and because
   `ComponentRow.CanDownload` is `Pin is not null && !Installed && !IsDownloading`, that row then
   offers no Download button at all, so there is no way to re-fetch over it from the UI. Do not
   try: an earlier draft of this item asked for exactly that and it is unreachable. (A one-byte
   edit also leaves the length unchanged, so even an invoked command would hit the helper's
   `have >= ExpectedBytes` short-circuit, skip the transfer, and delete a file the user could not
   then re-obtain.) The fail-closed hash check is exercised by the interrupted download above and
   pinned by `ComponentFetchClientTests`. A "Reinstall" affordance is the Tier-2 follow-up.
8. **The grep, by hand (T1-10, Task 9).** From the repo root, run
   `git grep -nE "System\.Net|HttpClient|Socket|WebRequest|Dns" -- src/LocalScribe.App src/LocalScribe.Core`
   and confirm it prints nothing. This is the claim; it should be checkable in one command by
   someone who does not trust the test.
