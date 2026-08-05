# Tier 1 shared contract — FIXED, do not redesign

Plan A **creates** everything below. Plans B, C and D **consume** it and must use these exact names
and signatures. This block is the "Interfaces: Consumes" section for every task in B/C/D that logs.

These decisions were made against eight verified traps in the research. Each carries the trap it
answers. **An implementer must not "improve" any of these** — every one of them is load-bearing.

---

## 1. `IDiagnosticLog` — the seam B/C/D write into

**Create:** `src/LocalScribe.Core/Diagnostics/DiagnosticLog.cs`

```csharp
namespace LocalScribe.Core.Diagnostics;

/// <summary>One diagnostic line. DERIVED data, never evidence - see StoragePaths.DiagnosticsDir.
/// Message and Detail are redacted per Settings.Logging.IncludeTranscriptText before they reach
/// disk; a caller may pass transcript-bearing text and MUST be able to trust that switch.</summary>
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
```

**Call-site form B/C/D use (memorise this — it is the whole API surface):**

```csharp
_log.Write("warn", "capture", "Local leg stalled - no frames", $"gapMs={gap} device={id}");
```

### Fixed implementation decisions

| Decision | Why (trap answered) |
|---|---|
| Modelled on `McpAuditLog.cs` — `FileMode.Append`, `FileShare.ReadWrite \| FileShare.Delete`, one JSON line per entry, **calendar-month** rotation | It is this repo's only append-only log and it works. Size-based rolling has no precedent and would make this the first *deleting* writer in a codebase whose core rule is append-only. |
| **AMENDED 2026-08-05:** a **single-writer chained drain**, not `McpAuditLog`'s `SemaphoreSlim` | The original wording mandated the semaphore by analogy. That was wrong: `McpAuditLog.AppendAsync` is `async` and can await a gate, whereas `IDiagnosticLog.Write` is **`void` fire-and-forget** and structurally cannot. `FlushAsync` also needs a handle to await, which a semaphore does not give it. Plans B/C/D consume only `Write`/`FlushAsync`, so this changes nothing for them. |
| **Zero IO in the constructor.** `Directory.CreateDirectory` happens inside the drain, exactly as `McpAuditLog.AppendAsync` does | Trap 7: `CompositionRootTests.cs:16` calls the real `CompositionRoot.Build()`, so ctor-time IO would create folders in the developer's actual `%USERPROFILE%\LocalScribe` on every test run. |
| Folder is `diagnostics\`, files are `diag-yyyyMM.jsonl` — **not** `logs\` and **not** `*.log` | Trap 6: `.gitignore` already contains `[Ll]og/`, `[Ll]ogs/` and `*.log`. A `logs\` folder created during a test run silently vanishes from `git status`. |
| Honours the **existing** `Settings.Logging.Level` and `Settings.Logging.IncludeTranscriptText` | Trap 8 / Absent 4: `LoggingSetting` (`Settings.cs:29,67`) is declared, documented in `docs/specs/localscribe-specs.md:871`, and read by **zero** production code. No schema bump is needed — this closes a documented gap rather than inventing a knob. |
| Settings are read through an injected `Func<LoggingSetting>`, not a captured value | `SettingsService` swaps the settings reference on save; a captured value would pin the level at startup. |
| `TimeProvider` injected, never `DateTime.Now` | House convention (`McpAuditLog.cs:14`). |

---

## 2. `StoragePaths.DiagnosticsDir`

**Modify:** `src/LocalScribe.Core/Storage/StoragePaths.cs` — add beside `McpAuditDir`, pure getter, no IO.

```csharp
/// <summary>Diagnostic log (Tier 1 plan A, 2026-08-05): DERIVED, safe to delete wholesale -
/// never evidence (same standing as search-index.json). One JSONL file per calendar month.
/// Deliberately named diagnostics\ rather than logs\ because .gitignore already swallows
/// [Ll]ogs/ and *.log, which would hide a stray test artefact from git status.</summary>
public string DiagnosticsDir => Path.Combine(Root, "diagnostics");
```

---

## 3. Version — two separate strings, deliberately

**Trap 1 is the whole reason this is split.** `CompositionRoot.cs:67` reads
`Assembly.GetName().Version`, which is the **assembly** version and ignores
`AssemblyInformationalVersionAttribute` entirely; MSBuild also strips any `+sha` suffix before
deriving `AssemblyVersion`. That string flows into `SessionBootstrap.cs:42` →
`SessionRecord.AppVersion` → **every session.json ever written**, which is append-only evidentiary
data that cannot be edited afterwards.

So:

| String | Source | Goes to |
|---|---|---|
| `AppComposition.AppVersion` | `Assembly.GetName().Version?.ToString(3)` — **unchanged code**, now yielding a real number instead of `1.0.0` | `session.json`, consent records. Stays numeric and short. |
| `AppComposition.BuildInfo` (**new**) | `AssemblyInformationalVersionAttribute`, e.g. `0.9.0+g1628935` | Diagnostic log header, Settings "About" line, support copy-paste. Never enters `session.json`. |

**Modify:** `src/LocalScribe.App/CompositionRoot.cs` — `AppComposition` is a **positional `sealed record` with 20 members** (`:21-41`) and there is exactly **one** construction site (`:175-178`). Adding members means editing both.

### 3a. `AppComposition.Log` — ADDED 2026-08-05, the single instance

Plan A adds **two** members, not one:

```csharp
string BuildInfo,       // diagnostics/Settings display only; never enters session.json
DiagnosticLog Log       // the one process-wide sink; see section 1
```

**The `Log` member is declared CONCRETE (`DiagnosticLog`), not `IDiagnosticLog`** — Plan A's Settings
"Copy last error" command reads `LastError`, which is on the class, not the interface. This is a
widening: `DiagnosticLog` *is* an `IDiagnosticLog`, so every consumer in Plans B, C and D still takes
the parameter as `IDiagnosticLog? log = null` and is unaffected. Member order matches Plan A's
declaration; `AppComposition` has exactly one construction site and every consumer reaches members by
name, so order is cosmetic.

**This is the only defined way to reach the log.** There is exactly one instance and it is reached as
`comp.Log` — from `App.OnStartup`, from the `SessionController` construction at
`CompositionRoot.cs:85-89`, and from every consumer in Plans B, C and D.

A plan must **never** say "whatever Plan A called its local". A local in `CompositionRoot.Build()` is
not in scope in `App.OnStartup` and vice versa; only the record member bridges them.

**Construction-order constraint:** the log must be constructed early in `Build()` — before the
services that take it — but must still do **zero IO** in its constructor (trap 7), so early
construction is safe.

**Create:** `src/Directory.Build.props` — scoped to `src/` so it reaches the eight shipping projects
and leaves `tests/` and `tools/` untouched (trap 2: a repo-root `Directory.Build.props` would
silently apply to all 13 csproj files including `tools/generate-icon` and `tools/UiaProbe`).
The git-SHA step needs an `Exec` with `ContinueOnError` and a no-`.git` fallback; **the repo has zero
precedent for shelling out during build**, so Plan A writes that target verbatim including guards.

---

## 4. `UnhandledExceptionRecorder` — the WPF-free policy class

**Create:** `src/LocalScribe.App/Services/UnhandledExceptionRecorder.cs`

Trap 9: `App.xaml.cs` and `TrayIconHost.cs` have **no test coverage at all** (105 test files, no
`AppTests.cs`/`TrayIconHostTests.cs`). Every tested App-layer service is a WPF-free extracted class.
So the policy is extracted and tested; the dispatcher lambda becomes one line. This is the
`StopConfirmToastGuard` precedent, whose extraction rationale is recorded at `App.xaml.cs:864-874`.

```csharp
/// <summary>Records a dispatcher-unhandled exception and notifies the user, replacing the
/// swallow-everything handler (App.xaml.cs:50-55). Handle() returns the value to assign to
/// DispatcherUnhandledExceptionEventArgs.Handled and MUST return true on EVERY path including
/// when logging or reporting themselves throw - the original comment explains that an unhandled
/// AsyncRelayCommand fault kills the whole tray app, and that crash can land mid-recording.</summary>
public sealed class UnhandledExceptionRecorder(Action<Exception> log, Action<Exception> notify)
{
    public bool Handle(Exception ex);
}
```

**Wiring constraint (trap 3):** the handler is registered at `App.xaml.cs:55`, which is 35 lines
before `CompositionRoot.Build()` (`:90`), 121 lines before `InfoBarErrorReporter` exists (`:176`) and
~760 lines before `_tray` exists (`:818`). Use the house null-conditional field-capture solution
already documented at `App.xaml.cs:180-183` and used at `:1058` (`_tray?.ShowNotice(m)`) — the
recorder field is read as `_recorder?.Handle(ex) ?? true`, so the handler is safe from line 55 and
upgrades itself once Build() has run.

---

## 5. `InfoBarErrorReporter` gains an optional log sink — it is NOT decorated

**Trap 4:** `InfoBarErrorReporter` is consumed **concretely**, not through `IUiErrorReporter`:
`MainWindowViewModel.cs:14` declares `public InfoBarErrorReporter Errors { get; }` and
`MainWindow.xaml.cs:37,131,136-138` reads `.Messages` / `.DismissOldest()` directly. A logging
**decorator** at `App.xaml.cs:176` will not compile.

**Fixed approach:** add an optional log-sink parameter defaulted `null`, so every existing test
construction site keeps compiling and `MainWindowViewModel` keeps the concrete type.

```csharp
public sealed class InfoBarErrorReporter(Action<Action> dispatch, IDiagnosticLog? log = null)
    : IUiErrorReporter
```

Do **not** change `MainWindowViewModel.Errors` to the interface — `Messages`/`DismissOldest` are
pinned by `MainWindowViewModelTests` and `InfoBarErrorReporterTests`.

`TrayNoticeReporter` takes the same optional parameter for the same reason.

---

## 6. Settings "Open diagnostics folder" uses the **pinned** paths, not live settings

**Trap 5:** the storage root is restart-pinned in `CompositionRoot.cs:66`
(`// once; restart-required`) but re-read live in `SettingsPageViewModel.cs:249-251` for the MCP
audit button. The log is written under the **pinned** root for the life of the process, so the
diagnostics command must use the already-injected optional `StoragePaths? paths` parameter
(`SettingsPageViewModel.cs:195`, wired from `App.xaml.cs:266` as `paths: comp.Paths`) — **not**
re-resolve from `_settings.Current` the way the MCP button does. That parameter is nullable and null
in most unit tests, so the command must degrade gracefully.

Follow the `OpenMcpAuditFolderCommand` shape (`SettingsPageViewModel.cs:246-252`): `CreateDirectory`,
then call the injected `Action<string> _openFolder`. Commands here are
`public IRelayCommand X { get; }` assigned in the constructor, **not** `[RelayCommand]`-generated.

**Absent 8:** `SettingsPageViewModelTests.MakeVm` passes `openFolder: _ => { }` — a discarding no-op
(`SettingsPageViewModelTests.cs:52`). Plan A must change that fake to a **capturing** one before it
can assert anything about the new command.

---

## 7. Redaction contract — testable, and it closes a promise

The log's doc-comment states its redaction rule the way `McpAuditLog.cs:7-9` does
("Never contains returned transcript text - args and counts only").

**Required test:** seed a transcript-bearing exception message, write it at every level, and assert
the persisted line does not contain it when `IncludeTranscriptText` is false.

This matters beyond hygiene: a diagnostic log under the storage root that captures transcript content
becomes an undeclared, unmanaged copy of privileged evidence, sitting outside every retention and
purge path.

---

## 8. What every plan's Global Constraints section must carry

Copy verbatim into all four plans:

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
