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
```

**Call-site form B/C/D use (memorise this — it is the whole API surface):**

```csharp
_log.Write(DiagnosticLevels.Warn, "capture", "Local leg stalled - no frames",
    $"gapMs={gap} device={DiagnosticRedaction.Mark(id)}");
```

Four rules are load-bearing in that one line. Each of the four cost Plan A a fix round; the first
cost it three, one of them a Critical.

1. **Mark the VARIABLE part only, never the fixed prefix.** `gapMs=` and `device=` are fixed text and
   stay bare; `id` is the only part that could carry an identifier, so only `id` is wrapped. Marking
   a fixed literal, an enum name or a bare integer *destroys* it on disk at the default setting and
   misleads a reader into thinking something was hidden when nothing was. See **section 1a** — it is
   not optional reading, because `Apply()` is a **no-op** on anything the call site did not mark.
2. **`Level` and `Source` are serialised RAW and are NEVER redacted.** `Write()` builds
   `new DiagnosticEntry(time.GetUtcNow(), level, source, Apply(message, keep), Apply(detail, keep))`
   (`DiagnosticLog.cs:97-99`) and serialises the record as-is (`:155`) — neither `Mark()` nor
   `Apply()` ever runs on those two fields. Anything interpolated into `source` reaches
   `diag-*.jsonl` verbatim at the default `IncludeTranscriptText = false`, and a `Mark()`ed source
   would land on disk carrying literal `<<`/`>>`. **`source` must be a compile-time constant
   subsystem tag** — never a session id, path, name or any caller-supplied value.
3. **Use the `DiagnosticLevels` constants for `level`,** not bare strings:
   `DiagnosticLevels.Error` / `.Warn` / `.Info` / `.Debug` (`DiagnosticLevels.cs:9-12`). Every shipped
   call site uses them. Bare literals still *work* — `Rank()` trims and lowercases — but an
   unrecognised value ranks as **info** rather than being dropped, so a typo degrades silently
   instead of failing. There is no fifth level; do not invent one.
4. **Throttle any `Write()` reachable from a frame loop or other high-frequency path.** Gate it on a
   monotonic `Environment.TickCount64` window (the shipped interval is
   `DiagnosticThrottleIntervalMs = 30_000`, reasoned at `ProcessLoopbackCapture.cs:112-124`), or on a
   value actually *changing* (`_lastLoggedActivationInfo != ActivationInfo`, `:258`). Never emit per
   frame, per packet or per iteration: the discontinuity flag alone reaches ~100/s, which is over a
   million lines across a 3-hour recording into a file nothing ever prunes. **Count-based gates were
   tried twice and rejected twice** — a counter reset by any intervening clean packet or successful
   iteration fires on *every* event under a real intermittent pattern; a lifetime counter swallows an
   isolated blip. Only wall-clock time bounds the rate under every pattern.

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
| **ADDED 2026-08-05, REVISED 2026-08-06 (F19):** `tsUtc` is serialised by **`DiagnosticTimestampConverter`** (`Core/Diagnostics/`) as `2026-08-05T09:30:00.123Z` — the trailing-`Z` shape every evidentiary `*AtUtc` field in `session.json`/`meta.json` uses, **with milliseconds kept**, and **not** `...+00:00` | One timestamp shape across the files a support engineer reads side by side. Fixed at merge on purpose: B/C/D append to the same monthly files, so changing it later means a mid-file format change. **Do NOT "simplify" this to `UtcIso8601Converter`** — that was tried and reverted. It truncates to whole seconds, which it earns from a companion field (`durationMs`/`startMs`/`endMs`) a diagnostic line does not have; `RequeueForRetry` can append a retried entry behind a later one, and the ruling that this is non-corrupting rests on `tsUtc` being re-sortable, which whole seconds void. `.fff` not `.FFF`, so the field is fixed-width and a string sort is a chronological sort. `Read` accepts all three historical shapes. Do not add a second, higher-precision timestamp field. |

**Choosing `level` when the source encodes severity in TEXT (ADDED 2026-08-05, F3).** A Core seam
that reports through a bare `Action<string>` cannot carry a level, and widening such a seam is a
public-API change. The shipped answer is to branch **at the sink**, on the fixed message prefix the
Core side composes: `CompositionRoot.CaptureDiagnosticLevel` maps `"capture error"` and
`"device invalidated"` to `Error`, `"data discontinuity"` to `Warn`, and everything else to `Info`
(`StringComparison.Ordinal`). This matters more than it looks: `Write()` latches `LastError` only for
rank 0, so a fault written at `info` can **never** reach Settings' "Copy last error", and `Write()`
returns early when `Rank(level) > Rank(cfg.Level)`, so at `Level="warn"` an info-level fault is
dropped from the file entirely. A prefix mapping is a cross-file coupling and **must be pinned on
both sides** — `ProcessLoopbackCaptureSourceTests` pins the literals in Core,
`CompositionRootTests` pins the mapping in App.

### 1a. `DiagnosticRedaction` — public API B/C/D must call, not an internal detail of the sink

**Create:** `src/LocalScribe.Core/Diagnostics/DiagnosticRedaction.cs` (Plan A). Plans B, C and D
**call** it. It is part of this contract, not an implementation detail you may ignore.

`Settings.Logging.IncludeTranscriptText` promises the user the log does not carry transcript text.
That promise can only be MECHANICAL if the potentially-privileged part of a line is *delimited at
the source*: `Write()` runs `DiagnosticRedaction.Apply(message, keep)` / `Apply(detail, keep)`, and
`Apply()` returns the text **unchanged** at `DiagnosticRedaction.cs:53` when it contains no `<<`.

> **Redaction is a NO-OP on any text the call site did not wrap in `Mark()`.** A raw session id in a
> `detail` is not "protected by the switch". It is a leak, and the switch will never see it.

| Member | Use |
|---|---|
| `Mark(value)` | Wrap a possibly-privileged **variable** part before it reaches `Write`. Neutralises any delimiter the value already contained. |
| `ForException(ex)` | The **required** `detail` form for an exception. |
| `Apply(text, includeTranscriptText)` | Strip or redact. `Write` calls it for the log copy; a **display** boundary calls it with `includeTranscriptText: true`. |

**A session id is NOT opaque.** `SessionId.New()` mints `yyyy-MM-dd_HHmm_{App}_{Slug(title)}`
(`SessionId.cs:11-12`), so an id **embeds the session title** — i.e. the matter or client name.
Before logging ANY value, ask whether it can carry a session id, a session title, a participant
name, a path built from a title, or transcript text. If it can, `Mark()` it. Plan A found six leaks
of this one class through six different doors (`Info` messages, `Report` contexts, `Notice` strings,
a `Detail` field, `ExternalEngineBusy`, and the log's own drain-failure path).

**Exceptions go to `detail` as `DiagnosticRedaction.ForException(ex)` — NEVER `ex.ToString()`, `{ex}`
or `{ex.Message}`.** `ForException` marks *each* exception's message and neutralises *each*
exception's own stack, walking the `InnerException` chain (a wrapped exception's fault site lives in
the inner stack; the outer stack points at the catch site). `ex.ToString()` is explicitly rejected at
`DiagnosticRedaction.cs:79-80`: it embeds inner-exception messages inline with no way to mark them.
A **raw** stack trace is also unsafe for a second reason — C# renders async-lambda and nested
local-function frames with **doubled** angle brackets (`<>c.<<Outer>b__1_0>d.MoveNext()`), a literal
unterminated `<<`. `Apply()` fails **closed** on an unterminated marker, so a raw stack redacts every
frame after that one at the DEFAULT setting. Measured on this build, not assumed.

**Do NOT mark what is not privileged.** Fixed literals, enum names, HRESULTs, type names and bare
counts stay bare. **Watch this inverse as hard as the leak** — Plan A over-corrected three separate
times, each one destroying the signal instead of protecting it (stack traces eaten by an
unterminated marker, a bare integer count rendered `[redacted]`, and an exception message handled
wrongly). Marking a non-secret misleads a reader into thinking something was hidden when nothing
was. The shipped precedents are `SessionDiagnosticsRecorder`'s unmarked `"(none)"`
(`SessionDiagnosticsRecorder.cs:51-54`) and `StartupOrchestrator`'s `privileged: false`
(`StartupOrchestrator.cs:38-43`).

**Mark at the source, strip at the single display boundary.** When a string reaches BOTH a user
surface and the log, the call site marks the variable part and the *display* boundary — not the log
— removes the delimiters with `Apply(text, includeTranscriptText: true)`. That call is
**unconditional** and independent of `Settings.Logging.IncludeTranscriptText`: the user must see the
id either way, and only the LOG copy is governed by the switch. `Apply()` is a no-op on any string
with no marker, so every other string stays byte-identical. Two shipped instances:

| Marked at | Stripped at | Sink that keeps the marked copy |
|---|---|---|
| `StartupOrchestrator.cs:49`, `MattersPageViewModel.cs:399` (`Report` contexts) | `InfoBarErrorReporter.cs:53`, `TrayNoticeReporter.cs:32` | the reporters' own `log?.Write(...)` |
| `CompositionRoot.cs:198` (`ExternalEngineBusy` interpolates a session id into a `Notice`) | `SessionViewModel.cs:184` (the ONE `controller.Notice` display handler) | `SessionDiagnosticsRecorder.Notice`, subscribed to the SAME event |

Any new B/C/D string on a both-surfaces path must name its strip boundary the same way. Without the
mark the id reaches disk unredacted; without the strip a literal `<<`/`>>` reaches the user.

### 1b. Exit-path flushes are BOUNDED — `ShutdownFlush.Timeout`

**Create:** `src/LocalScribe.App/Services/ShutdownFlush.cs` (Plan A) —
`public static class ShutdownFlush { public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2); }`

`FlushAsync`'s `CancellationToken` is **accepted for call-site symmetry and deliberately NOT
honoured** (`DiagnosticLog.cs:112-115`: `public Task FlushAsync(CancellationToken ct) => Kick();` —
abandoning a drain mid-exit is exactly how the last line before a crash gets lost). **The caller
bounds the wait, and every exit-path flush MUST.**

```csharp
// App.OnExit-style blocking backstop (App.xaml.cs:1219)
try { _log?.FlushAsync(CancellationToken.None).Wait(ShutdownFlush.Timeout); } catch { }

// async exit path, e.g. the tray Exit menu item (TrayIconHost.cs:134-135)
Task flush = _log?.FlushAsync(CancellationToken.None) ?? Task.CompletedTask;
await Task.WhenAny(flush, Task.Delay(ShutdownFlush.Timeout));
```

**Never `await FlushAsync(...)` unbounded, and never rely on the token to bound it.** Plan A round 1
shipped the tray Exit flush unbounded: against a wedged drain (dead disk, vanished network path,
antivirus holding the file) that line never completes, so `Application.Current.Shutdown()` never
runs, so `OnExit` — the backstop — never runs either, and the user is left with a tray process only
Task Manager can end. `ShutdownFlush.Timeout` exists as **one shared constant** because the two sites
carried independent literals and had already drifted once. A B/C/D exit sequence that moves a flush
behind a seam must carry the *bound* with it, not just the await.

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

**Trap 1 is the whole reason this is split.** `CompositionRoot.cs:116` reads
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

**Modify:** `src/LocalScribe.App/CompositionRoot.cs` — `AppComposition` is a **positional `sealed record`**
and there is exactly **one** construction site. Adding members means editing both. **Anchors below are
against the MERGED tree** (Plan A landed, so it has the 20 members the pre-Plan-A contract counted plus
the two section 3a prescribes): **22 members** at `:28-50`, construction site at `:218-221`.

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

**This is the only defined way to reach the log.** There is exactly one instance. It is built as the
`Build()` local `log` (`CompositionRoot.cs:137`) and handed to the seams constructed below it inside
that method — the capture provider's `diagnostic:` sink at `:164-165` (the `SessionController`
construction spans `:155-166`) and the diariser. **Everywhere outside `Build()` it is reached as
`comp.Log`** — from `App.OnStartup` (`App.xaml.cs:107`, `:218`, `:895`, `:1131`) and from every
consumer in Plans B, C and D.

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
`StopConfirmToastGuard` precedent, whose extraction rationale is recorded at `App.xaml.cs:910-918`.

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

**Wiring constraint (trap 3):** in the **merged tree** the handler is registered at `App.xaml.cs:67`,
which is 35 lines before `CompositionRoot.Build()` (`:102`), ~151 lines before `InfoBarErrorReporter`
exists (`:218`) and ~820 lines before `_tray` exists (`:885`). Use the house null-conditional
field-capture solution — the `_recorder` field and its rationale are at `App.xaml.cs:34-36`, the same
shape `_log` uses at `:29-33` and `_tray?.ShowNotice(m)` uses at `:1126`. The field is read as
`_recorder?.Handle(ex.Exception) ?? true`, so the handler is safe from the line it is registered on
and upgrades itself once `Build()` has run.

---

## 5. `InfoBarErrorReporter` gains an optional log sink — it is NOT decorated

**Trap 4:** `InfoBarErrorReporter` is consumed **concretely**, not through `IUiErrorReporter`:
`MainWindowViewModel.cs:14` declares `public InfoBarErrorReporter Errors { get; }` and
`MainWindow.xaml.cs:37,131,136-138` reads `.Messages` / `.DismissOldest()` directly. A logging
**decorator** at the construction site (`App.xaml.cs:218` in the merged tree) will not compile.

**Fixed approach:** add an optional log-sink parameter defaulted `null`, so every existing test
construction site keeps compiling and `MainWindowViewModel` keeps the concrete type.

```csharp
public sealed class InfoBarErrorReporter(Action<Action> dispatch, IDiagnosticLog? log = null)
    : IUiErrorReporter
```

Do **not** change `MainWindowViewModel.Errors` to the interface — `Messages`/`DismissOldest` are
pinned by `MainWindowViewModelTests` and `InfoBarErrorReporterTests`.

`TrayNoticeReporter` takes the same optional parameter for the same reason.

### 5a. The `IUiErrorReporter` surface — `Info` carries a `privileged` flag

```csharp
public interface IUiErrorReporter
{
    void Report(string context, Exception ex);
    void Info(string message, bool privileged = true);
}
```

That trailing parameter is **load-bearing and it is on the interface**, not just the implementations
(`IUiErrorReporter.cs:34`). Consequences for B/C/D:

- **Every test double that implements `IUiErrorReporter` must carry it** —
  `public void Info(string message, bool privileged = true)`. A one-parameter `Info(string)` does not
  implement the member and fails with **CS0535**. All 24 shipped fakes already spell it that way
  (`AppServiceFakes.cs:32` plus 23 per-file fakes), so a plan that *replaces* one of those listings
  with the old shape breaks the build; a plan that only *adds* to them does not.
- **`Info` marks the message WHOLESALE by DEFAULT.** Both implementations write
  `privileged ? DiagnosticRedaction.Mark(message) : message` (`InfoBarErrorReporter.cs:65`,
  `TrayNoticeReporter.cs:45`). This is deliberate: an `Info` string is composed by its caller and
  routinely carries a roster member's real name, a session title or an export path built from one.
  Defaulting to marked keeps that failure mode closed for the twenty-odd existing call sites and for
  the new one that lands most rounds. Existing one-argument calls such as
  `_errors.Info(report.Summarize(row.Title))` still compile and still get the protection — do **not**
  "simplify" one to `privileged: false`.
- **`privileged: false` is a narrow, call-site-justified opt-out** (`IUiErrorReporter.cs:17-30`): an
  explicit assertion, written and justified in a comment *at the call site*, that the message is
  fixed text plus non-identifying values only (a count, an enum name, a program-defined token) —
  never a name, a title, a path, or free text the caller only partially controls. There is exactly
  **one** in the codebase: `StartupOrchestrator.cs:42-43`'s
  `Info($"Recovered {n} interrupted session(s)", privileged: false)`, because marking a bare count
  would render it `[redacted]` on disk at the default setting and destroy the very value spec item
  T1-1 asks for.
- **`Report` contexts stay literal** — except where a call site needs a variable part, in which case
  it wraps **only that part** in `DiagnosticRedaction.Mark`. Two shipped sites do
  (`StartupOrchestrator.cs:49`, `MattersPageViewModel.cs:399`). Both reporters then strip the marker
  for display with `Apply(context, includeTranscriptText: true)` (`InfoBarErrorReporter.cs:53`,
  `TrayNoticeReporter.cs:32`), so the InfoBar and the tray balloon are byte-identical to before and
  only the LOG copy is governed by the switch — this is section 1a's mark-at-source /
  strip-at-display pattern. **Never cite `"Tag session " + sessionId` as an example of a fixed
  literal**; a version of that rule which did exactly that shipped the Critical it warned against.
- `Report` and `Info` write **different payloads on purpose**: `Report` writes a four-argument
  structured line (`Write(DiagnosticLevels.Error, "ui", context, DiagnosticRedaction.ForException(ex))`),
  `Info` writes a three-argument one at info level. They cannot be collapsed into a single shared
  helper that only sees `context + ": " + ex.Message` — `ForException`'s per-exception marking and
  stack neutralisation become structurally unreachable and the raw `ex.Message` lands unmarked. Both
  also log **before** dispatching: the durable record must not depend on the dispatcher or on a
  window the user may never open.

---

## 6. Settings "Open diagnostics folder" uses the **pinned** paths, not live settings

**Trap 5:** the storage root is restart-pinned in `CompositionRoot.cs:115`
(`// once; restart-required`) but re-read live in `SettingsPageViewModel.cs:262-266` for the MCP
audit button. The log is written under the **pinned** root for the life of the process, so the
diagnostics command must use the already-injected optional `StoragePaths? paths` parameter
(`SettingsPageViewModel.cs:203`, wired from `App.xaml.cs:327` as `paths: comp.Paths`) — **not**
re-resolve from `_settings.Current` the way the MCP button does. That parameter is nullable and null
in most unit tests, so the command must degrade gracefully.

Follow the `OpenMcpAuditFolderCommand` shape (`SettingsPageViewModel.cs:262-266`): `CreateDirectory`,
then call the injected `Action<string> _openFolder`. Commands here are
`public IRelayCommand X { get; }` assigned in the constructor, **not** `[RelayCommand]`-generated.
The shipped result is `OpenDiagnosticsFolderCommand` at `:269-284`, declared at `:1274`.
Both diagnostics commands are wrapped in a `try`/`catch` that reports at **`Info`** and never via
`Report` (F6, final whole-branch review): `Report` writes rank 0, and `DiagnosticLog` latches
`LastError` on every rank-0 entry, so an error-level report would destroy the very entry the user
opened the page to hand over. A new diagnostics command in B/C/D must follow the same rule.

**Absent 8 — CLOSED by Plan A.** `SettingsPageViewModelTests.MakeVm` used to pass
`openFolder: _ => { }`, a discarding no-op; it now passes a **capturing** fake
(`SettingsPageViewModelTests.cs:19-23,64`). B/C/D can assert against it as-is and must not revert it.

---

## 7. Redaction contract — testable, and it closes a promise

The log's doc-comment states its redaction rule the way `McpAuditLog.cs:7-9` does
("Never contains returned transcript text - args and counts only").

**Required test:** seed a transcript-bearing exception message, write it at every level, and assert
the persisted line does not contain it when `IncludeTranscriptText` is false. Note what that test
proves and what it does not: it proves the *switch* works on a **marked** value. It cannot prove a
call site remembered to mark — see section 1a. The switch is a promise about marked text, not a
filter over everything a caller passes.

This matters beyond hygiene: a diagnostic log under the storage root that captures transcript content
becomes an undeclared, unmanaged copy of privileged evidence, sitting outside every retention and
purge path.

---

## 8. What every plan's Global Constraints section must carry

Copy verbatim into all four plans:

- **Build/test:** `dotnet build` / `dotnet test` against `F:\LocalScribe\LocalScribe.slnx`. A running
  `LocalScribe.App.exe` locks `Core.dll` → `MSB3027`. Close it; **never blanket-kill processes** —
  target the specific PID.
- **Test baseline (`--filter "Category!=Fixture"`).** Fixture-gated tests (`Category=Fixture`) need
  model weights and private corpora and are excluded.
  - **Pre-Plan-A (measured 2026-08-05):** Core **1186**, App **984**, Mcp **6** = **2176**. This is
    the baseline Plan A itself branched from; it is history now.
  - **Post-Plan-A, i.e. what B/C/D branch from (measured 2026-08-05 on the Plan A branch tip):**
    Core **1220**, App **1025**, Mcp **6** = **2251**. Plan A added 4 Core and 7 App test files.
    Re-measure on your own branch point rather than trusting this number.
  - **Judge regressions by failing test NAME, never by count.** Two App tests are **pre-existing
    flaky under concurrent-assembly load** — both pass in isolation and both are byte-identical to
    `master`, so a run reporting App 1024/1025 or 1023/1025 with only these names is green:
    `AssistantQaServiceTests.Dispose_racing_an_in_flight_ask_cancels_it_and_persists_nothing` and
    `MetadataEditorViewModelTests.Delete_after_editor_retag_decrements_the_current_matter_not_the_stale_one`.
    Never "fix" a passing suite to match a predicted count.
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
