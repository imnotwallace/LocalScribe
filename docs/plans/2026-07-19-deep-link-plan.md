# Deep-Link Implementation Plan (`feat/deep-link`)
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement design §4 (`feat/deep-link`) of `docs/plans/2026-07-18-steno-round-design.md`: the `localscribe://record/start[?name=<text>]` and `localscribe://record/stop` URL scheme — per-user HKCU registration, second-instance argv forwarding over a named pipe (OS IPC, zero-network posture holds), a pure never-throwing Core `DeepLinkParser` with Steno's sanitization contract adopted wholesale, and routing that runs `start` through the EXACT same command path as a manual start (consent posture unchanged, Record console auto-opens, sanitized `name` prefills the session title) while `stop` NEVER stops directly — it shows a no-activate confirm toast (**[Stop recording] [Keep recording]**) and only the explicit click stops. Start-while-recording and stop-while-idle each surface a notification toast and change nothing.
**Architecture:** Core gains one pure static boundary class (`DeepLinkParser` + the typed `DeepLinkResult` union, new `LocalScribe.Core.DeepLink` namespace) and one additive option (`LiveSessionOptions.Title`, flowing into `SessionBootstrap.StartAsync`'s EXISTING `title` parameter — the audio-import title path, so meta.Title and the folder-id slug agree by construction). The App layer gains: `DeepLinkChannel` (named-pipe argv forwarding beside the existing `SingleInstance` mutex guard — the guard is REUSED, not duplicated), `DeepLinkRegistrar` (HKCU humble object on the `RegistryLaunchAtLogin` pattern, pure `RegistrationValues` helper for tests), pure `DeepLinkRouter` (parse result + `SessionState` → typed decision), pure `ToastPlacement` (bottom-right work-area math + dismiss-interval decision), and the **LOCKED reusable toast primitive `AdvisoryToastWindow`** (plain WPF Window — never FluentWindow — frameless, `Topmost`, no-activate via the existing `NativeWindowInterop.MakeNoActivate`, auto-dismiss). `App.xaml.cs` wires it all: registration at startup, second-instance forward-and-exit, first-instance pipe server (dispatch-wrapped like `SingleInstance`'s activate callback), launch-argv handling deferred to ApplicationIdle. Deep-link start executes `SessionViewModel.StartCommand` (the one manual path — its `Idle` CanExecute gate is the race authority) after setting a new one-shot `SessionViewModel.PendingStartTitle`.
**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui (theme resources only — the toast is a PLAIN Window), System.IO.Pipes, Microsoft.Win32 registry, xUnit.

## Global Constraints

- **Target branch:** `feat/deep-link`, created off master AFTER the in-flight `feat/ux-round-2026-07-18` branch merges. Line anchors below are grounded @ master `7605606` (the post-merge HEAD carrying the Steno-round design docs); **re-verify every anchor by its quoted context before editing — if a line number drifted, locate by the quoted code, not the number.** The design spec `docs/plans/2026-07-18-steno-round-design.md` is already on master; THIS plan (`docs/plans/2026-07-19-deep-link-plan.md`) is committed to the branch first (`docs(plans): deep-link implementation plan` + trailer) if not already there.
- **Merge order for the round:** `feat/deep-link` merges THIRD of seven (after `fix/dedup-short-utterance-guard` and `feat/markdown-export`; before `feat/call-detect-advisory`, which REUSES this branch's `AdvisoryToastWindow`).
- **LOCKED CONTRACT — `AdvisoryToastWindow` (Task 5):** `feat/call-detect-advisory` consumes this primitive verbatim. The public shape MUST land exactly as: `public AdvisoryToastWindow(string title, string body, IReadOnlyList<ToastAction> actions, int autoDismissSeconds)` with `public sealed record ToastAction(string Caption, Action OnClick)`, in `src\LocalScribe.App\Views\AdvisoryToastWindow.xaml(.cs)`. Do not rename, reshape, or relocate it.
- **WPF-UI FluentWindow startup-rendering gotcha (project memory, BINDING):** on this Win11 box a Mica `FluentWindow` shown before the message pump is running renders INVISIBLE (app "won't open", only a tray icon). `AdvisoryToastWindow` is therefore a PLAIN WPF `Window` (the `OverlayWindow`/`ConsentDialog` precedent), and every toast in this plan is shown only from dispatcher-marshalled callbacks or the ApplicationIdle-deferred launch-arg handler — i.e. strictly after the pump is up.
- **Evidentiary rules (design §1, locked):** deep links can NEVER bypass the consent flow (`start` routes through `SessionViewModel.StartCommand`; the first-run consent modal runs earlier in `OnStartup` than both the pipe server start and the ApplicationIdle launch-arg handling, and a declined consent shuts down before either exists). Deep links NEVER write markers. `stop` never stops silently — only the explicit **[Stop recording]** click stops, and auto-dismiss of the confirm toast means "keep recording" (the safe default). Detection/notification is advisory-only.
- **Query strings are never logged (Steno's sanitization contract, design §4):** `DeepLinkParser` returns only fixed constant reason strings and the sanitized name; the wiring logs `decision.Reason` ONLY — never the URL, never the query. No task below adds any logging of raw deep-link input.
- **Zero-network posture:** the second-instance channel is a named pipe (OS IPC), `PipeOptions.CurrentUserOnly` on BOTH ends. No sockets anywhere.
- **Single-instance mechanism EXISTS and is reused (plan-time check resolved):** `SingleInstance.TryAcquire("LocalScribe", ...)` in `src\LocalScribe.App\Services\SingleInstance.cs` already holds a named mutex `Local\LocalScribe` + an activate event, wired in `App.xaml.cs` `OnStartup` step (1). This plan does NOT add a second mutex — it adds the argv pipe (`DeepLinkChannel`) beside it; a second instance with a `localscribe://` arg forwards it over the pipe and exits, a second instance without one keeps the existing `SignalExisting` activate behavior unchanged.
- **HKCU only, never elevates:** registration writes `HKCU\Software\Classes\localscribe` (`URL Protocol` empty string value + `shell\open\command` = `"<exe>" "%1"`), idempotent every launch, best-effort (failure leaves deep links dark, never blocks startup).
- 0-warning build gate must hold.
- Tests: xUnit. Filtered run: `dotnet test "<testproj>" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\`
- **Full App-suite gate runs (XamlHygieneTests):** `RepoPaths.SolutionRoot()` walks UP from the test assembly directory to find `.git`, so an App-suite run that includes `XamlHygieneTests` MUST NOT use the Temp isolated BaseOutputPath (it sits outside the repo — 5 false failures). Run full App-suite gates with the default repo-internal output path; keep the isolated path for filtered runs. If the default path hits MSB3027 (app running, locked bin), report and wait — never kill processes.
- IMPORTANT: the LocalScribe app may be running and LOCK bin DLLs (MSB3027 copy error — NOT a compile error). Always use the isolated BaseOutputPath above (every command below already appends it); NEVER kill the user's running app or any other process.
- Never use Unicode emojis in test code or scripts (project rule). Non-ASCII characters needed by parser tests (accented letters) are written as `\uXXXX` escapes so test source stays ASCII.
- Test projects: App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. App.Tests `<Compile Include>`-links `LiveTestDoubles.cs` (with `LiveTestDoubles.MakeController/Options`, `FakeEngineFactory`, `FakeProvider`) plus `FakeTranscriptionEngine.cs`, so those doubles compile INTO App.Tests and are directly usable there. There is NO `InternalsVisibleTo` anywhere in this repo (verified) — new members that tests call directly must be `public`.
- Core suite has 2 known pre-existing fixture failures (unrelated); App suite must be fully green.
- Commit style: `feat(core)`/`feat(app)`/`test(...)`/`docs(...)`; every commit message ends with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```

---

### Task 1: Core — `DeepLinkParser` + typed `DeepLinkResult` (pure, never throws)
**Files:**
- Create `src\LocalScribe.Core\DeepLink\DeepLinkParser.cs` (new folder + namespace `LocalScribe.Core.DeepLink`; both types in this one file).
- Create `tests\LocalScribe.Core.Tests\DeepLinkParserTests.cs`.

**Interfaces:**
- Produces:
  - `public abstract record DeepLinkResult` — closed union via nested derived records: `DeepLinkResult.StartRecording(string? SanitizedName)` | `DeepLinkResult.StopRecording()` | `DeepLinkResult.Invalid(string Reason)`. Record value equality makes test assertions exact.
  - `public static DeepLinkResult DeepLinkParser.Parse(string url)` — pure, NEVER throws (null/garbage → `Invalid`); allowlist is exactly `record/start` (optional `name` query param) and `record/stop`; scheme/host/path/query-key compare case-insensitively; a single trailing `/` on the path is tolerated; unknown query params are ignored (and, like all query content, never logged — `Invalid.Reason` values are fixed constants that never echo input).
  - Name sanitization (Steno's contract): keep Unicode letters, combining marks, digits, and exactly `. , ( ) @ & ' ! + # -`; every other char → space; whitespace runs collapsed to one space; trimmed; capped at 120 chars (then end-trimmed); empty after sanitize → `null`. `%2B`/literal `+` decode to a LITERAL plus (`Uri.UnescapeDataString` semantics — `+` is an allowlisted kept char, deliberately not treated as a space).
- Consumes: nothing (BCL only: `System.Uri`, `System.Globalization`, `System.Text`).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\DeepLinkParserTests.cs`:
```csharp
using LocalScribe.Core.DeepLink;
using Xunit;

namespace LocalScribe.Core.Tests;

public class DeepLinkParserTests
{
    // Design 2026-07-18 section 4: the parser is an untrusted-input boundary (a drive-by webpage
    // can invoke a registered scheme). Steno's contract adopted wholesale: never throws, typed
    // reject with a fixed reason, two-verb allowlist, sanitized name, query never logged.

    [Fact]
    public void Valid_start_without_name_parses()
        => Assert.Equal(new DeepLinkResult.StartRecording(null),
            DeepLinkParser.Parse("localscribe://record/start"));

    [Fact]
    public void Valid_start_with_name_returns_the_decoded_name()
        => Assert.Equal(new DeepLinkResult.StartRecording("Client intake"),
            DeepLinkParser.Parse("localscribe://record/start?name=Client%20intake"));

    [Fact]
    public void Valid_stop_parses_and_ignores_any_query()
    {
        Assert.Equal(new DeepLinkResult.StopRecording(),
            DeepLinkParser.Parse("localscribe://record/stop"));
        // Unknown/extra params are ignored, never logged, never a reject reason.
        Assert.Equal(new DeepLinkResult.StopRecording(),
            DeepLinkParser.Parse("localscribe://record/stop?name=x&foo=bar"));
    }

    [Fact]
    public void Scheme_host_path_and_query_key_are_case_insensitive()
    {
        Assert.Equal(new DeepLinkResult.StopRecording(),
            DeepLinkParser.Parse("LOCALSCRIBE://RECORD/STOP"));
        Assert.Equal(new DeepLinkResult.StartRecording("hi"),
            DeepLinkParser.Parse("LocalScribe://Record/Start?NAME=hi"));
    }

    [Fact]
    public void Trailing_slash_is_tolerated()
        => Assert.Equal(new DeepLinkResult.StartRecording(null),
            DeepLinkParser.Parse("localscribe://record/start/"));

    [Fact]
    public void Name_keeps_the_allowlisted_punctuation_and_unicode_letters()
    {
        // Kept: letters/marks/digits + . , ( ) @ & ' ! + # -   Dropped (to space): the colon.
        Assert.Equal(new DeepLinkResult.StartRecording("Smith v. Jones (depo) @Court #142 A&B O'Neil! -1"),
            DeepLinkParser.Parse(
                "localscribe://record/start?name=Smith%20v.%20Jones%20(depo)%20%40Court%20%23142%3A%20A%26B%20O'Neil!%20-1"));
        // Accented letters survive (Unicode letter categories, written as escapes - project rule).
        Assert.Equal(new DeepLinkResult.StartRecording("Café Müller"),
            DeepLinkParser.Parse("localscribe://record/start?name=Caf%C3%A9%20M%C3%BCller"));
    }

    [Fact]
    public void Plus_is_a_kept_literal_never_a_space()
        // Steno contract: '+' is on the keep list; Uri.UnescapeDataString semantics (no
        // application/x-www-form-urlencoded plus-to-space rewriting).
        => Assert.Equal(new DeepLinkResult.StartRecording("one+two"),
            DeepLinkParser.Parse("localscribe://record/start?name=one+two"));

    [Fact]
    public void Control_chars_and_injection_shapes_become_collapsed_spaces()
    {
        Assert.Equal(new DeepLinkResult.StartRecording("a b c d"),
            DeepLinkParser.Parse("localscribe://record/start?name=a%09b%00c%0D%0Ad"));
        var r = Assert.IsType<DeepLinkResult.StartRecording>(DeepLinkParser.Parse(
            "localscribe://record/start?name=..%2F..%2Fetc%2Fpasswd%22%3B%20DROP%20TABLE"));
        // Slashes, quotes, and semicolons never survive into a session title.
        Assert.Equal(".. .. etc passwd DROP TABLE", r.SanitizedName);
        Assert.DoesNotContain("/", r.SanitizedName);
        Assert.DoesNotContain("\"", r.SanitizedName);
        Assert.DoesNotContain(";", r.SanitizedName);
    }

    [Fact]
    public void Overlong_name_is_capped_at_120_chars()
    {
        var r = Assert.IsType<DeepLinkResult.StartRecording>(DeepLinkParser.Parse(
            "localscribe://record/start?name=" + new string('a', 200)));
        Assert.Equal(new string('a', 120), r.SanitizedName);
    }

    [Fact]
    public void Name_that_sanitizes_to_nothing_becomes_null()
        // Only dropped chars (slashes) -> spaces -> collapse -> empty -> null.
        => Assert.Equal(new DeepLinkResult.StartRecording(null),
            DeepLinkParser.Parse("localscribe://record/start?name=%2F%2F%2F"));

    [Theory]
    [InlineData("https://record/start")]                    // wrong scheme
    [InlineData("localscribe://transcript/start")]          // unknown host
    [InlineData("localscribe://record/pause")]              // verb not on the allowlist
    [InlineData("localscribe://record")]                    // no verb
    [InlineData("localscribe://record/start/extra")]        // extra path segment
    [InlineData("localscribe:record/start")]                // opaque form, no authority
    [InlineData("not a url")]
    [InlineData("")]
    public void Everything_off_the_allowlist_is_a_typed_reject(string url)
    {
        var invalid = Assert.IsType<DeepLinkResult.Invalid>(DeepLinkParser.Parse(url));
        Assert.False(string.IsNullOrWhiteSpace(invalid.Reason));
        // The reason is a fixed constant - it never echoes the input (query-never-logged contract).
        if (url.Length > 0) Assert.DoesNotContain(url, invalid.Reason);
    }

    [Fact]
    public void Parse_never_throws_even_on_null()
        => Assert.IsType<DeepLinkResult.Invalid>(DeepLinkParser.Parse(null!));
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~DeepLinkParserTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\` — expected: `error CS0234: The type or namespace name 'DeepLink' does not exist in the namespace 'LocalScribe.Core'`.
- [ ] **Write the implementation.** Create `src\LocalScribe.Core\DeepLink\DeepLinkParser.cs`:
```csharp
using System.Globalization;
using System.Text;

namespace LocalScribe.Core.DeepLink;

/// <summary>Typed outcome of parsing a localscribe:// URL (design 2026-07-18 section 4). A closed
/// union: the private base ctor means the only cases are the nested records below, so routing
/// switches are exhaustive by construction.</summary>
public abstract record DeepLinkResult
{
    private DeepLinkResult() { }

    /// <summary>record/start. SanitizedName is the SANITIZED name= value (Steno's contract,
    /// see DeepLinkParser), or null when absent or empty-after-sanitize.</summary>
    public sealed record StartRecording(string? SanitizedName) : DeepLinkResult;

    /// <summary>record/stop. Carries nothing - the App side must CONFIRM before stopping
    /// (a registered scheme is drive-by-invokable; stopping evidence is never silent).</summary>
    public sealed record StopRecording : DeepLinkResult;

    /// <summary>Anything off the allowlist. Reason is one of a FIXED set of constant strings -
    /// it never echoes the URL or query (query strings are never logged, design section 4).</summary>
    public sealed record Invalid(string Reason) : DeepLinkResult;
}

/// <summary>Pure parser for the localscribe:// scheme - an UNTRUSTED-INPUT boundary (any webpage
/// can invoke a registered scheme). Never throws; allowlist is exactly record/start (optional
/// name=) and record/stop; scheme/host/path/query-key are case-insensitive; one trailing '/' is
/// tolerated. Sanitization (Steno's shortcut-url contract adopted wholesale): keep Unicode
/// letters/marks/digits plus . , ( ) @ &amp; ' ! + # - ; other chars become spaces; whitespace
/// collapses; trimmed; capped at 120 chars; empty -&gt; null. '+' is a KEPT literal (percent-decoding
/// only - no form-encoding plus-to-space). Callers must never log the raw URL or query.</summary>
public static class DeepLinkParser
{
    private const string KeptPunctuation = ".,()@&'!+#-";

    public static DeepLinkResult Parse(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return new DeepLinkResult.Invalid("empty url");
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return new DeepLinkResult.Invalid("unparseable url");
            if (!string.Equals(uri.Scheme, "localscribe", StringComparison.OrdinalIgnoreCase))
                return new DeepLinkResult.Invalid("wrong scheme");
            if (!string.Equals(uri.Host, "record", StringComparison.OrdinalIgnoreCase))
                return new DeepLinkResult.Invalid("unknown host");

            string path = uri.AbsolutePath.TrimEnd('/');
            if (string.Equals(path, "/start", StringComparison.OrdinalIgnoreCase))
                return new DeepLinkResult.StartRecording(SanitizeName(QueryValue(uri.Query, "name")));
            if (string.Equals(path, "/stop", StringComparison.OrdinalIgnoreCase))
                return new DeepLinkResult.StopRecording();
            return new DeepLinkResult.Invalid("unknown action");
        }
        catch
        {
            // Never throws (untrusted boundary): any Uri/decoding surprise is a typed reject.
            return new DeepLinkResult.Invalid("parser fault");
        }
    }

    /// <summary>First value for <paramref name="key"/> (case-insensitive) in a raw ?a=b&amp;c=d
    /// query, percent-decoded via Uri.UnescapeDataString ('+' stays literal - it is on the keep
    /// list). Null when absent. Unknown params are ignored. Nothing here is ever logged.</summary>
    private static string? QueryValue(string query, string key)
    {
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string k = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
            return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    private static string? SanitizeName(string? raw)
    {
        if (raw is null) return null;
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            bool keep = char.IsLetterOrDigit(c)
                || cat is UnicodeCategory.NonSpacingMark
                       or UnicodeCategory.SpacingCombiningMark
                       or UnicodeCategory.EnclosingMark
                || KeptPunctuation.Contains(c);
            sb.Append(keep ? c : ' ');
        }
        // Collapse ALL whitespace runs (incl. the spaces we just substituted) + trim in one pass.
        string joined = string.Join(' ',
            sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (joined.Length > 120) joined = joined[..120].TrimEnd();
        return joined.Length == 0 ? null : joined;
    }
}
```
- [ ] **Run tests and see PASS.** Same filter as above — expected: 13 passed (12 facts/theories, the theory contributing 8 cases → 20 total test cases reported; all green).
- [ ] **Commit.**
```
git add src/LocalScribe.Core/DeepLink/DeepLinkParser.cs tests/LocalScribe.Core.Tests/DeepLinkParserTests.cs
git commit -m "feat(core): DeepLinkParser - typed never-throwing localscribe:// boundary with Steno's sanitization contract

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Core — `LiveSessionOptions.Title` flows into the existing bootstrap title path
**Files:**
- Modify `src\LocalScribe.Core\Live\SessionController.cs` (two edits: the `LiveSessionOptions` record around lines 28–31, and the `SessionBootstrap.StartAsync` call around lines 466–468).
- Test `tests\LocalScribe.Core.Tests\SessionControllerTests.cs` (add one `[Fact]` inside the existing class, which already has `_root` and uses `LiveTestDoubles.MakeController`).

**Interfaces:**
- Produces: `public string? LiveSessionOptions.Title { get; init; }` (default null) — a Start-time session title. Flows into `SessionBootstrap.StartAsync`'s EXISTING optional `title` parameter (`SessionBootstrap.cs:16`, added by the audio-import round), which seeds BOTH `meta.Title` and the folder-id slug; null/blank keeps the default `"{App} - {local start}"` title, so every existing caller is byte-for-byte unchanged in behavior.
- Consumes: `SessionBootstrap.StartAsync(..., IReadOnlyList<string>? matterIds = null, string? title = null)` — no change to `SessionBootstrap` itself.

Steps:
- [ ] **Write the failing test.** Append inside `SessionControllerTests` (before the closing brace) in `tests\LocalScribe.Core.Tests\SessionControllerTests.cs`:
```csharp
    [Fact]
    public async Task Start_title_option_seeds_meta_title()
    {
        // Design 2026-07-18 section 4: a deep link's sanitized name= prefills the session title.
        // The option rides SessionBootstrap.StartAsync's EXISTING title parameter (the audio-import
        // path), so meta.Title and the folder-id slug agree by construction; MetadataStore is
        // fully qualified to avoid depending on this file's using block.
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(
            LiveTestDoubles.Options() with { Title = "Client intake (deep link)" },
            CancellationToken.None);
        Assert.NotNull(id);

        var meta = await new LocalScribe.Core.Storage.MetadataStore(paths.MetaJson(id!))
            .LoadAsync(CancellationToken.None);
        Assert.Equal("Client intake (deep link)", meta!.Title);

        clock.ElapsedMs = 1000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Start_title_option" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\` — expected: `error CS0117: 'LiveSessionOptions' does not contain a definition for 'Title'`.
- [ ] **Add the option.** In `src\LocalScribe.Core\Live\SessionController.cs`, the `LiveSessionOptions` record currently contains (lines 28–31):
```csharp
    /// <summary>Matters this recording is pre-tagged with (Stage 6.2). Seeds meta.MatterIds at
    /// Start and biases the Whisper initial prompt with those matters' vocabulary terms. Empty =
    /// record-first-classify-later (the default); the picker on the Record console is a convenience.</summary>
    public IReadOnlyList<string> MatterIds { get; init; } = [];
```
Immediately after that property (before the `SilentLegGraceMs` doc comment) insert:
```csharp

    /// <summary>Optional Start-time session title (design 2026-07-18 section 4: a deep link's
    /// SANITIZED name= prefills the title). Flows into SessionBootstrap.StartAsync's existing
    /// title parameter - the audio-import title path - seeding BOTH meta.Title and the folder-id
    /// slug. Null/blank keeps the default "{App} - {local start}" title, so every existing caller
    /// is unchanged.</summary>
    public string? Title { get; init; }
```
- [ ] **Pass it through.** In the same file, `StartAsync`'s bootstrap call currently reads (lines 466–468):
```csharp
                var boot = await SessionBootstrap.StartAsync(_paths, settings, app,
                    [SourceKind.Local, SourceKind.Remote], devices, _time, _appVersion, ct,
                    options.MatterIds);
```
Replace with:
```csharp
                var boot = await SessionBootstrap.StartAsync(_paths, settings, app,
                    [SourceKind.Local, SourceKind.Remote], devices, _time, _appVersion, ct,
                    options.MatterIds, options.Title);
```
- [ ] **Run tests and see PASS.** Same filter — expected: 1 passed. Then run the whole class to prove no regression: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionControllerTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\`.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs
git commit -m "feat(core): LiveSessionOptions.Title rides the existing bootstrap title path

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: App — `DeepLinkChannel` (second-instance argv forwarding over a named pipe)
**Files:**
- Create `src\LocalScribe.App\Services\DeepLinkChannel.cs`.
- Create `tests\LocalScribe.App.Tests\DeepLinkChannelTests.cs` (real in-process pipe round-trips — named pipes are headless-testable, unlike WPF windows).

**Interfaces:**
- Produces:
  - `public static string DeepLinkChannel.PipeNameFor(string userToken)` → `"LocalScribe.DeepLink." + userToken` (pure, tested).
  - `public static string DeepLinkChannel.CurrentUserPipeName()` → `PipeNameFor(<current user SID>)` — per-user name so two Windows users on one machine never fight (mirrors `SingleInstance`'s `Local\` scoping rationale).
  - `public static DeepLinkChannel StartServer(string pipeName, Action<string> onLine)` — first-instance listener; `onLine` fires ON THE BACKGROUND LISTENER THREAD (callers dispatch-wrap, exactly the `SingleInstance.TryAcquire` callback contract). Fail-open loop: a malformed/silent/crashed client can never kill the listener.
  - `public static bool DeepLinkChannel.TrySend(string pipeName, string line, int timeoutMs = 3000)` — second-instance path; never throws, false when no holder is reachable.
  - `IDisposable`: `Dispose()` cancels the listener and joins the thread (bounded — the read path has a 2 s hostile-client timeout).
- Consumes: `System.IO.Pipes` with `PipeOptions.CurrentUserOnly` on BOTH ends (same-user enforcement at the OS level — no ACL code needed), `System.Security.Principal.WindowsIdentity`.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\DeepLinkChannelTests.cs`:
```csharp
using System.IO.Pipes;
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public class DeepLinkChannelTests
{
    // Guid-unique names: xUnit runs classes in parallel and pipe names are machine-global.
    private static string UniqueName() => "LocalScribe.DeepLink.test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void PipeNameFor_composes_the_per_user_name()
        => Assert.Equal("LocalScribe.DeepLink.S-1-5-21-x", DeepLinkChannel.PipeNameFor("S-1-5-21-x"));

    [Fact]
    public void Round_trips_one_argv_line_from_a_second_instance()
    {
        string name = UniqueName();
        var received = new List<string>();
        using var gate = new ManualResetEventSlim(false);
        using var server = DeepLinkChannel.StartServer(name,
            line => { lock (received) received.Add(line); gate.Set(); });

        Assert.True(DeepLinkChannel.TrySend(name, "localscribe://record/start?name=x"),
            "TrySend must reach the listening first instance");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(5)), "server never received the forwarded line");
        lock (received) Assert.Equal("localscribe://record/start?name=x", Assert.Single(received));
    }

    [Fact]
    public void TrySend_returns_false_when_no_server_is_listening()
        // Never throws - the second instance exits either way (the SignalExisting contract).
        => Assert.False(DeepLinkChannel.TrySend(UniqueName(), "localscribe://record/stop", timeoutMs: 250));

    [Fact]
    public void Server_survives_a_client_that_connects_and_writes_nothing()
    {
        string name = UniqueName();
        var received = new List<string>();
        using var gate = new ManualResetEventSlim(false);
        using var server = DeepLinkChannel.StartServer(name,
            line => { lock (received) received.Add(line); gate.Set(); });

        // A hostile/broken client: connect, write nothing, vanish. The listener's bounded read
        // (2 s) plus the fail-open loop must bring the pipe back up for the next real client.
        using (var silent = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly))
            silent.Connect(3000);

        Assert.True(DeepLinkChannel.TrySend(name, "localscribe://record/stop", timeoutMs: 8000),
            "a silent client must not wedge the listener");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(10)), "server never recovered after the silent client");
        lock (received) Assert.Equal("localscribe://record/stop", Assert.Single(received));
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~DeepLinkChannelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\` — expected: `error CS0103: The name 'DeepLinkChannel' does not exist in the current context` (the compiler may report CS0246 for the type form — either way the build fails on the missing type).
- [ ] **Write the implementation.** Create `src\LocalScribe.App\Services\DeepLinkChannel.cs`:
```csharp
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace LocalScribe.App.Services;

/// <summary>Second-instance deep-link forwarding (design 2026-07-18 section 4), living BESIDE the
/// existing SingleInstance mutex guard (which stays the instance arbiter): the FIRST instance
/// listens on a per-user named pipe; a second instance launched by the OS for a localscribe:// URL
/// TrySend()s its argv line and exits. OS IPC, not a socket - the zero-network posture holds -
/// and PipeOptions.CurrentUserOnly on BOTH ends makes the OS enforce same-user access (no ACL
/// code). onLine fires ON THE BACKGROUND LISTENER THREAD - callers pass a dispatch-wrapped
/// action, exactly the SingleInstance.TryAcquire callback contract. Fail-open: a malformed,
/// silent, or crashed client logs nothing and can never kill the listener; the read is bounded
/// (2 s) so a hostile connect-and-hold client cannot wedge Dispose either.</summary>
public sealed class DeepLinkChannel : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _listener;

    private DeepLinkChannel(string pipeName, Action<string> onLine)
    {
        _listener = new Thread(() => Listen(pipeName, onLine))
        { IsBackground = true, Name = "LocalScribe.DeepLinkChannel" };
        _listener.Start();
    }

    /// <summary>Pure name composition, split out for tests.</summary>
    public static string PipeNameFor(string userToken) => "LocalScribe.DeepLink." + userToken;

    /// <summary>Per-user pipe name (SID-suffixed): two Windows users on one machine each run
    /// their own LocalScribe without fighting over the channel - the SingleInstance "Local\"
    /// scoping rationale applied to pipes (which have no Local\ namespace).</summary>
    public static string CurrentUserPipeName()
        => PipeNameFor(WindowsIdentity.GetCurrent().User?.Value ?? "anonymous");

    public static DeepLinkChannel StartServer(string pipeName, Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        return new DeepLinkChannel(pipeName, onLine);
    }

    /// <summary>Second-instance path: connect, write the one argv line, exit. False when no
    /// holder is reachable (or anything else goes wrong) - the caller exits either way, so
    /// failure here must never throw (the SignalExisting contract).</summary>
    public static bool TrySend(string pipeName, string line, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Utf8NoBom) { AutoFlush = true };
            writer.WriteLine(line);
            return true;
        }
        catch { return false; }
    }

    private void Listen(string pipeName, Action<string> onLine)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                using var reader = new StreamReader(server, Encoding.UTF8);
                // Bounded read: a client that connects and never writes gets 2 s, then the loop
                // re-listens (TimeoutException lands in the generic catch). Cancellation (Dispose)
                // surfaces as OperationCanceledException and ends the loop.
                string? line = reader.ReadLineAsync(_cts.Token).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(line)) onLine(line);
            }
            catch (OperationCanceledException) { break; }
            catch { /* fail-open: next client gets a fresh pipe; nothing is logged (no URL leaks) */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Join();   // bounded: WaitForConnectionAsync honors the token; reads time out at 2 s
        _cts.Dispose();
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.App/Services/DeepLinkChannel.cs tests/LocalScribe.App.Tests/DeepLinkChannelTests.cs
git commit -m "feat(app): DeepLinkChannel - per-user named-pipe argv forwarding beside the SingleInstance guard

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: App — pure `DeepLinkRouter` decision + `DeepLinkRegistrar` (HKCU, humble)
**Files:**
- Create `src\LocalScribe.App\Services\DeepLinkRouter.cs` (router + `DeepLinkActionKind` + `DeepLinkDecision`).
- Create `src\LocalScribe.App\Services\DeepLinkRegistrar.cs`.
- Create `tests\LocalScribe.App.Tests\DeepLinkRouterTests.cs` and `tests\LocalScribe.App.Tests\DeepLinkRegistrarTests.cs`.

**Interfaces:**
- Produces:
  - `public enum DeepLinkActionKind { StartRecording, ConfirmStop, NotifyAlreadyRecording, NotifyNotRecording, Ignore }`
  - `public sealed record DeepLinkDecision(DeepLinkActionKind Kind, string? Title = null, string? Reason = null)`
  - `public static DeepLinkDecision DeepLinkRouter.Route(DeepLinkResult result, SessionState state)` — pure policy (design §4 semantics): start+Idle → `StartRecording` carrying the sanitized title; start+anything-else (Recording/Paused/Finalizing) → `NotifyAlreadyRecording`; stop+Recording/Paused → `ConfirmStop` (NEVER a direct stop); stop+Idle/Finalizing → `NotifyNotRecording`; `Invalid` → `Ignore` carrying the parser's fixed reason (the only thing the wiring may log).
  - `public static (string ProtocolLabel, string OpenCommand) DeepLinkRegistrar.RegistrationValues(string exePath)` — pure, tested: `("URL:LocalScribe deep link", "\"<exe>\" \"%1\"")`.
  - `public static void DeepLinkRegistrar.EnsureRegistered(string? exePath)` — humble object on the `RegistryLaunchAtLogin` precedent (registry is untestable headless; the smoke runbook verifies): writes `HKCU\Software\Classes\localscribe` default = ProtocolLabel, `URL Protocol` = `""`, and `shell\open\command` default = OpenCommand. Idempotent (plain overwrites of identical values), never elevates (HKCU is always writable by the user), best-effort (any failure swallowed — deep links stay dark, startup never blocks), no-op on a null/empty exe path.
- Consumes: `LocalScribe.Core.DeepLink.DeepLinkResult` (Task 1), `LocalScribe.Core.Live.SessionState`, `Microsoft.Win32.Registry`.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\DeepLinkRouterTests.cs`:
```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.DeepLink;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.App.Tests;

public class DeepLinkRouterTests
{
    // Design 2026-07-18 section 4 semantics, pure and exhaustive. The stop verb NEVER routes to a
    // direct stop - only ever to a confirm decision (evidentiary rule: a drive-by webpage can
    // invoke a registered scheme; stopping a recording must not be silently triggerable).

    [Fact]
    public void Start_while_idle_starts_and_carries_the_sanitized_title()
    {
        Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.StartRecording, Title: "Client intake"),
            DeepLinkRouter.Route(new DeepLinkResult.StartRecording("Client intake"), SessionState.Idle));
        Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.StartRecording, Title: null),
            DeepLinkRouter.Route(new DeepLinkResult.StartRecording(null), SessionState.Idle));
    }

    [Theory]
    [InlineData(SessionState.Recording)]
    [InlineData(SessionState.Paused)]
    [InlineData(SessionState.Finalizing)]
    public void Start_while_busy_only_notifies(SessionState state)
        => Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.NotifyAlreadyRecording),
            DeepLinkRouter.Route(new DeepLinkResult.StartRecording("x"), state));

    [Theory]
    [InlineData(SessionState.Recording)]
    [InlineData(SessionState.Paused)]
    public void Stop_while_active_asks_for_confirmation_never_stops(SessionState state)
        => Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.ConfirmStop),
            DeepLinkRouter.Route(new DeepLinkResult.StopRecording(), state));

    [Theory]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Finalizing)]
    public void Stop_while_not_recording_only_notifies(SessionState state)
        => Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.NotifyNotRecording),
            DeepLinkRouter.Route(new DeepLinkResult.StopRecording(), state));

    [Fact]
    public void Invalid_is_ignored_and_carries_only_the_fixed_reason()
        => Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.Ignore, Reason: "wrong scheme"),
            DeepLinkRouter.Route(new DeepLinkResult.Invalid("wrong scheme"), SessionState.Idle));
}
```
Create `tests\LocalScribe.App.Tests\DeepLinkRegistrarTests.cs`:
```csharp
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public class DeepLinkRegistrarTests
{
    // The registry write itself is a humble object (RegistryLaunchAtLogin precedent) verified by
    // the smoke runbook; the VALUE composition is pure and pinned here: the exe path AND %1 are
    // both quoted, so paths with spaces and URLs survive shell argument splitting intact.

    [Fact]
    public void RegistrationValues_quote_the_exe_and_the_url_placeholder()
    {
        var (label, command) = DeepLinkRegistrar.RegistrationValues(
            @"C:\Program Files\LocalScribe\LocalScribe.App.exe");
        Assert.Equal("URL:LocalScribe deep link", label);
        Assert.Equal("\"C:\\Program Files\\LocalScribe\\LocalScribe.App.exe\" \"%1\"", command);
    }

    [Fact]
    public void EnsureRegistered_is_a_safe_no_op_on_a_missing_exe_path()
    {
        // Best-effort contract: never throws, never blocks startup.
        DeepLinkRegistrar.EnsureRegistered(null);
        DeepLinkRegistrar.EnsureRegistered("");
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~DeepLinkRouterTests|FullyQualifiedName~DeepLinkRegistrarTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\` — expected: `error CS0246: The type or namespace name 'DeepLinkDecision' could not be found` (plus CS0103 on `DeepLinkRouter`/`DeepLinkRegistrar`).
- [ ] **Write the router.** Create `src\LocalScribe.App\Services\DeepLinkRouter.cs`:
```csharp
using LocalScribe.Core.DeepLink;
using LocalScribe.Core.Live;

namespace LocalScribe.App.Services;

/// <summary>What the wiring should do with a routed deep link (design 2026-07-18 section 4).</summary>
public enum DeepLinkActionKind
{
    /// <summary>Run the EXACT manual start path (SessionViewModel.StartCommand) with Title prefilled.</summary>
    StartRecording,
    /// <summary>Show the confirm toast ([Stop recording] [Keep recording]); ONLY the explicit
    /// click stops - the deep link itself never does (evidentiary rule).</summary>
    ConfirmStop,
    /// <summary>start while a session is active/finalizing: notification toast, no action.</summary>
    NotifyAlreadyRecording,
    /// <summary>stop while idle/finalizing: notification toast, no action.</summary>
    NotifyNotRecording,
    /// <summary>Invalid link: do nothing; Reason (a fixed parser constant) is the ONLY loggable
    /// artifact - the URL and query are never logged.</summary>
    Ignore,
}

public sealed record DeepLinkDecision(DeepLinkActionKind Kind, string? Title = null, string? Reason = null);

/// <summary>Pure deep-link policy: parse result + session state -> decision. The state read is a
/// snapshot; the executing side re-checks the command's own CanExecute gate, which stays the
/// authority if a manual action raced the dispatch.</summary>
public static class DeepLinkRouter
{
    public static DeepLinkDecision Route(DeepLinkResult result, SessionState state) => result switch
    {
        DeepLinkResult.StartRecording s when state == SessionState.Idle
            => new(DeepLinkActionKind.StartRecording, Title: s.SanitizedName),
        DeepLinkResult.StartRecording => new(DeepLinkActionKind.NotifyAlreadyRecording),
        DeepLinkResult.StopRecording when state is SessionState.Recording or SessionState.Paused
            => new(DeepLinkActionKind.ConfirmStop),
        DeepLinkResult.StopRecording => new(DeepLinkActionKind.NotifyNotRecording),
        DeepLinkResult.Invalid i => new(DeepLinkActionKind.Ignore, Reason: i.Reason),
        _ => new(DeepLinkActionKind.Ignore, Reason: "unknown result"),
    };
}
```
- [ ] **Write the registrar.** Create `src\LocalScribe.App\Services\DeepLinkRegistrar.cs`:
```csharp
using Microsoft.Win32;

namespace LocalScribe.App.Services;

/// <summary>Per-user localscribe:// scheme registration (design 2026-07-18 section 4) - the
/// unpackaged-app pattern: HKCU\Software\Classes\localscribe with the empty "URL Protocol" value
/// plus shell\open\command = "&lt;exe&gt;" "%1". Humble Object (RegistryLaunchAtLogin precedent):
/// registry access is untestable headless, so the write path stays one-line-per-value and the
/// smoke runbook verifies it; RegistrationValues is the pure, tested composition. Idempotent
/// (identical overwrites every launch), NEVER elevates (HKCU only), best-effort - any failure is
/// swallowed and deep links simply stay dark; startup is never blocked.</summary>
public static class DeepLinkRegistrar
{
    public const string SchemeKeyPath = @"Software\Classes\localscribe";

    /// <summary>Pure value composition: the exe path AND the %1 URL placeholder are BOTH quoted
    /// so paths with spaces and argument splitting can never mangle the forwarded URL.</summary>
    public static (string ProtocolLabel, string OpenCommand) RegistrationValues(string exePath)
        => ("URL:LocalScribe deep link", "\"" + exePath + "\" \"%1\"");

    public static void EnsureRegistered(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        try
        {
            var (label, command) = RegistrationValues(exePath);
            using var key = Registry.CurrentUser.CreateSubKey(SchemeKeyPath);
            key.SetValue(null, label);
            key.SetValue("URL Protocol", "");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, command);
        }
        catch { /* best-effort per the class contract */ }
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 7 passed (the theories expand to 7 cases plus the three facts → 10 total test cases reported; all green).
- [ ] **Commit.**
```
git add src/LocalScribe.App/Services/DeepLinkRouter.cs src/LocalScribe.App/Services/DeepLinkRegistrar.cs tests/LocalScribe.App.Tests/DeepLinkRouterTests.cs tests/LocalScribe.App.Tests/DeepLinkRegistrarTests.cs
git commit -m "feat(app): pure DeepLinkRouter policy + HKCU DeepLinkRegistrar (humble, never elevates)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: App — `ToastPlacement` (pure) + the LOCKED `AdvisoryToastWindow` primitive
**Files:**
- Create `src\LocalScribe.App\ViewModels\ToastPlacement.cs` (beside `ScreenClamp.cs` — the same pure-geometry-helper precedent).
- Create `src\LocalScribe.App\Views\AdvisoryToastWindow.xaml` + `src\LocalScribe.App\Views\AdvisoryToastWindow.xaml.cs` (new `Views\` folder; WPF's SDK globbing picks both up — no csproj edit).
- Create `tests\LocalScribe.App.Tests\ToastPlacementTests.cs`.

**Interfaces:**
- Produces (LOCKED CONTRACT — `feat/call-detect-advisory` reuses this verbatim; see Global Constraints):
  - `public AdvisoryToastWindow(string title, string body, IReadOnlyList<ToastAction> actions, int autoDismissSeconds)` — plain WPF `Window` in namespace `LocalScribe.App.Views`.
  - `public sealed record ToastAction(string Caption, Action OnClick)` — declared in `AdvisoryToastWindow.xaml.cs`. A click runs `OnClick` then closes the toast; zero actions = pure notification.
  - Window behavior: frameless (`WindowStyle=None`), `Topmost`, no-activate (`ShowActivated=False` + `Focusable=False` in XAML, `WS_EX_NOACTIVATE`+`WS_EX_TOOLWINDOW` via the EXISTING `NativeWindowInterop.MakeNoActivate` source hook in `OnSourceInitialized` — the `OverlayWindow` pattern), bottom-right of the PRIMARY work area (`SystemParameters.WorkArea`, positioned on `Loaded` when `ActualHeight` is real), auto-dismiss `DispatcherTimer` when `autoDismissSeconds > 0` (`<= 0` = sticky). PLAIN Window, never FluentWindow/Mica (Global Constraints gotcha); theme brushes only (`ApplicationBackgroundBrush` fill — the `ConsentDialog` precedent — + `ControlFillColorSecondaryBrush` border + the `TextElement.Foreground` marker), so `XamlHygieneTests.ShippedXaml_HasNoDisallowedHardcodedBrushes` (which scans ALL App XAML recursively) stays green.
  - `public static (double Left, double Top) ToastPlacement.BottomRight(double workLeft, double workTop, double workWidth, double workHeight, double toastWidth, double toastHeight, double margin = 16)` — pure placement math, clamped so an oversized toast pins to the work-area origin rather than off-screen.
  - `public static TimeSpan? ToastPlacement.DismissInterval(int autoDismissSeconds)` — pure dismiss decision: `> 0` → the interval, `<= 0` → null (no timer).
- Consumes: `NativeWindowInterop.MakeNoActivate` (existing), `SystemParameters.WorkArea`, Wpf.Ui theme resources (dynamic — the app-wide dictionaries resolve them; Wpf.Ui's implicit Button style restyles the action buttons for free).
- Testing split (WPF windows are not unit-testable headlessly): ALL decision logic (where, and whether/when to dismiss) lives in `ToastPlacement` and is fully unit-tested; the window itself is a humble shell verified by build + XAML hygiene + the Task 6 smoke — the same viewmodel-not-window pattern every App.Tests file follows.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\ToastPlacementTests.cs`:
```csharp
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public class ToastPlacementTests
{
    // The toast window is a humble shell (WPF windows are not unit-testable headlessly - the
    // ScreenClamp/OverlayWindow precedent); ALL of its decision logic - placement + dismiss -
    // lives here, pure and exact.

    [Fact]
    public void BottomRight_sits_inside_the_work_area_with_the_default_margin()
        // 1920x1040 primary work area (1080 minus a 40px taskbar), 380x120 toast:
        // Left = 1920 - 380 - 16, Top = 1040 - 120 - 16.
        => Assert.Equal((1524d, 904d), ToastPlacement.BottomRight(0, 0, 1920, 1040, 380, 120));

    [Fact]
    public void BottomRight_respects_a_work_area_that_does_not_start_at_origin()
        // Multi-monitor: primary work area at (100, 50), 1000x700.
        => Assert.Equal((704d, 614d), ToastPlacement.BottomRight(100, 50, 1000, 700, 380, 120));

    [Fact]
    public void BottomRight_clamps_an_oversized_toast_to_the_work_area_origin()
        // A toast larger than the work area must pin to the origin, never land off-screen.
        => Assert.Equal((0d, 0d), ToastPlacement.BottomRight(0, 0, 300, 200, 380, 240));

    [Fact]
    public void DismissInterval_maps_seconds_to_a_timer_interval_or_none()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), ToastPlacement.DismissInterval(8));
        Assert.Null(ToastPlacement.DismissInterval(0));     // sticky: no timer at all
        Assert.Null(ToastPlacement.DismissInterval(-3));
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ToastPlacementTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\` — expected: `error CS0103: The name 'ToastPlacement' does not exist in the current context`.
- [ ] **Write the pure helper.** Create `src\LocalScribe.App\ViewModels\ToastPlacement.cs`:
```csharp
namespace LocalScribe.App.ViewModels;

/// <summary>Pure decision logic for AdvisoryToastWindow (design 2026-07-18 sections 4/5.3), split
/// out because WPF windows are not unit-testable headlessly (the ScreenClamp precedent): WHERE the
/// toast goes (bottom-right of the primary work area, clamped on-screen) and WHETHER it auto-
/// dismisses. The window itself is a humble shell over these two functions.</summary>
public static class ToastPlacement
{
    /// <summary>Bottom-right corner of the given work area with a margin, clamped to the work-area
    /// origin so an oversized toast can never land off-screen. Callers pass
    /// SystemParameters.WorkArea (the PRIMARY work area - taskbar already excluded).</summary>
    public static (double Left, double Top) BottomRight(double workLeft, double workTop,
        double workWidth, double workHeight, double toastWidth, double toastHeight,
        double margin = 16)
        => (Math.Max(workLeft, workLeft + workWidth - toastWidth - margin),
            Math.Max(workTop, workTop + workHeight - toastHeight - margin));

    /// <summary>Auto-dismiss decision: positive seconds -> the timer interval; zero/negative ->
    /// null (sticky toast, no timer). For the stop-confirm toast, dismissal without a click means
    /// "keep recording" - the safe default (evidentiary rule, design section 4).</summary>
    public static TimeSpan? DismissInterval(int autoDismissSeconds)
        => autoDismissSeconds > 0 ? TimeSpan.FromSeconds(autoDismissSeconds) : null;
}
```
- [ ] **Write the window XAML.** Create `src\LocalScribe.App\Views\AdvisoryToastWindow.xaml`:
```xml
<Window x:Class="LocalScribe.App.Views.AdvisoryToastWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="380" SizeToContent="Height"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ShowActivated="False" Focusable="False"
        ResizeMode="NoResize">
    <!-- PLAIN Window on purpose (LOCKED): a Mica FluentWindow shown before the message pump is up
         renders INVISIBLE on this box (project memory). Theme brushes only - the recursive
         XamlHygiene hardcoded-brush scan covers this file. ApplicationBackgroundBrush gives a
         SOLID themed backdrop (the ConsentDialog precedent) so the toast reads over any app. -->
    <Border CornerRadius="8" Background="{DynamicResource ApplicationBackgroundBrush}"
            BorderBrush="{DynamicResource ControlFillColorSecondaryBrush}" BorderThickness="1"
            TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}"
            Padding="14,10">
        <StackPanel>
            <TextBlock x:Name="TitleText" FontWeight="SemiBold" TextWrapping="Wrap" Margin="0,0,0,4" />
            <TextBlock x:Name="BodyText" TextWrapping="Wrap" />
            <StackPanel x:Name="ActionsPanel" Orientation="Horizontal" HorizontalAlignment="Right"
                        Margin="0,10,0,0" />
        </StackPanel>
    </Border>
</Window>
```
- [ ] **Write the code-behind.** Create `src\LocalScribe.App\Views\AdvisoryToastWindow.xaml.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App.Views;

/// <summary>One toast button: Caption is the visible label; OnClick runs on the UI thread when
/// clicked, and the toast closes itself afterwards regardless.</summary>
public sealed record ToastAction(string Caption, Action OnClick);

/// <summary>The advisory toast primitive (design 2026-07-18 section 4; REUSED by section 5's
/// call-detect offer toast - the ctor + ToastAction shape is a LOCKED cross-branch contract).
/// A PLAIN WPF Window - never FluentWindow/Mica (pre-pump invisible-Mica gotcha, project memory) -
/// frameless, Topmost, and NO-ACTIVATE (ShowActivated/Focusable false in XAML plus
/// WS_EX_NOACTIVATE via NativeWindowInterop.MakeNoActivate, the OverlayWindow pattern), so it can
/// never steal focus from a live call. Bottom-right of the primary work area via the pure, tested
/// ToastPlacement helper; auto-dismisses after autoDismissSeconds (&lt;= 0 = sticky). Callers show
/// it only after the message pump is up (dispatcher-marshalled paths only). ADVISORY ONLY: a toast
/// never writes markers and never gates recording - actions route through the same shared commands
/// the user would click (locked rule, design section 1).</summary>
public partial class AdvisoryToastWindow : Window
{
    private readonly DispatcherTimer? _dismiss;

    public AdvisoryToastWindow(string title, string body, IReadOnlyList<ToastAction> actions,
        int autoDismissSeconds)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        foreach (var action in actions)
        {
            var button = new Button
            { Content = action.Caption, Focusable = false, Margin = new Thickness(8, 0, 0, 0) };
            button.Click += (_, _) => { try { action.OnClick(); } finally { Close(); } };
            ActionsPanel.Children.Add(button);
        }
        if (ToastPlacement.DismissInterval(autoDismissSeconds) is { } interval)
        {
            _dismiss = new DispatcherTimer { Interval = interval };
            _dismiss.Tick += (_, _) => Close();
            _dismiss.Start();
        }
        // Position on Loaded: with SizeToContent="Height", ActualHeight is only real after the
        // first layout pass. SystemParameters.WorkArea = the PRIMARY work area (design 5.3).
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            (Left, Top) = ToastPlacement.BottomRight(wa.Left, wa.Top, wa.Width, wa.Height,
                ActualWidth, ActualHeight);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowInterop.MakeNoActivate(this);   // WS_EX_NOACTIVATE + TOOLWINDOW (existing helper)
    }

    protected override void OnClosed(EventArgs e)
    {
        _dismiss?.Stop();
        base.OnClosed(e);
    }
}
```
- [ ] **Run tests and see PASS, and the window compiles clean.** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ToastPlacementTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\` — expected: 4 passed. Then prove the new XAML/window builds warning-free: `dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\` — expected: 0 warnings, 0 errors. Then run the hygiene guard over the new XAML: `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~XamlHygieneTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\` — expected: all green (the recursive hardcoded-brush scan now covers `Views\AdvisoryToastWindow.xaml`, which uses theme resources only).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/ToastPlacement.cs src/LocalScribe.App/Views/AdvisoryToastWindow.xaml src/LocalScribe.App/Views/AdvisoryToastWindow.xaml.cs tests/LocalScribe.App.Tests/ToastPlacementTests.cs
git commit -m "feat(app): AdvisoryToastWindow primitive (plain no-activate Window) + pure ToastPlacement

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: App — `SessionViewModel.PendingStartTitle` + full deep-link wiring in `App.xaml.cs`
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SessionViewModel.cs` (one property + one edit inside `StartAsync`).
- Modify `src\LocalScribe.App\App.xaml.cs` (five edits: field, second-instance forward, registration, handler + pipe server, launch-arg handling, OnExit disposal).
- Test `tests\LocalScribe.App.Tests\SessionViewModelTests.cs` (add one `[Fact]`; the class already has `_root`, `using LocalScribe.Core.Model;` for `Settings`, and links `LiveTestDoubles`).

**Interfaces:**
- Produces: `public string? SessionViewModel.PendingStartTitle { get; set; }` — one-shot: the NEXT `StartAsync` consumes it into `LiveSessionOptions.Title` (Task 2) and clears it; EVERY Start attempt clears it (even a refused one), so a stale deep-link name can never attach to a later unrelated manual session. Set/read on the UI thread only (the deep-link handler is dispatcher-marshalled; `StartAsync`'s setup runs on the UI thread before its `Task.Run`).
- Consumes: `DeepLinkParser.Parse` (Task 1), `DeepLinkRouter.Route`/`DeepLinkActionKind` (Task 4), `DeepLinkChannel` (Task 3), `DeepLinkRegistrar.EnsureRegistered` (Task 4), `AdvisoryToastWindow`/`ToastAction` (Task 5), existing `SingleInstance`, `session.StartCommand`/`StopCommand` (the EXACT manual paths — consent posture and the Idle→Recording console-auto-open hook in `App.xaml.cs` apply unchanged), `comp.Controller.State`.
- Wiring guarantees (design §4 + §1): consent is never bypassed (the first-run consent modal runs before the pipe server exists and before ApplicationIdle; declining shuts down first). Every toast/spawned command runs on the dispatcher AFTER the pump is up. `stop` never stops without the explicit **[Stop recording]** click; the confirm toast auto-dismisses to "keep recording" after 30 s. Invalid links log the parser's fixed reason ONLY — never the URL or query. Race authority: the commands' own CanExecute gates are re-checked at execution time.

Steps:
- [ ] **Write the failing test.** Append inside `SessionViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\SessionViewModelTests.cs`:
```csharp
    [Fact]
    public async Task PendingStartTitle_prefills_the_session_title_once()
    {
        // Design 2026-07-18 section 4: the deep-link handler sets this, then runs the EXACT manual
        // StartCommand path - so the sanitized name lands as meta.Title through the same
        // LiveSessionOptions the console uses, and it is one-shot (cleared by the Start attempt).
        // MetadataStore is fully qualified to avoid depending on this file's using block.
        var (controller, _, paths, clock) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        vm.PendingStartTitle = "Client intake (deep link)";

        await vm.StartCommand.ExecuteAsync(null);
        string? id = controller.CurrentSessionId;
        Assert.NotNull(id);
        Assert.Null(vm.PendingStartTitle);            // one-shot: consumed by this Start
        var meta = await new LocalScribe.Core.Storage.MetadataStore(paths.MetaJson(id!))
            .LoadAsync(CancellationToken.None);
        Assert.Equal("Client intake (deep link)", meta!.Title);

        clock.ElapsedMs = 1000;
        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;
        vm.Dispose();
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~PendingStartTitle" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\` — expected: `error CS1061: 'SessionViewModel' does not contain a definition for 'PendingStartTitle'`.
- [ ] **Add the property.** In `src\LocalScribe.App\ViewModels\SessionViewModel.cs`, immediately after the `PreviewEnginePlan` property, which currently reads (around line 105):
```csharp
    public BackendPlan PreviewEnginePlan => _controller.PreviewEnginePlan;
```
insert:
```csharp

    /// <summary>One-shot Start-time title (design 2026-07-18 section 4): the deep-link handler
    /// puts a SANITIZED name= here, then executes the same StartCommand a human clicks; the next
    /// StartAsync consumes it into LiveSessionOptions.Title (meta.Title + the folder-id slug) and
    /// clears it. EVERY Start attempt clears it - even a refused one - so a stale deep-link name
    /// can never attach to a later unrelated manual session. UI-thread only (the handler is
    /// dispatcher-marshalled; StartAsync's setup runs on the UI thread before its Task.Run).</summary>
    public string? PendingStartTitle { get; set; }
```
- [ ] **Consume it in `StartAsync`.** In the same file, `StartAsync` currently composes options and starts (around lines 289–292):
```csharp
        var options = _matterIdsProvider is null
            ? _startOptions
            : _startOptions with { MatterIds = _matterIdsProvider() };
        string? id = await Task.Run(() => _controller.StartAsync(options, CancellationToken.None));
```
Replace with:
```csharp
        var options = _matterIdsProvider is null
            ? _startOptions
            : _startOptions with { MatterIds = _matterIdsProvider() };
        // Deep link (design 2026-07-18 section 4): one-shot title prefill, cleared on EVERY Start
        // attempt so a stale deep-link name never attaches to a later manual session.
        if (PendingStartTitle is { } pendingTitle)
        {
            options = options with { Title = pendingTitle };
            PendingStartTitle = null;
        }
        string? id = await Task.Run(() => _controller.StartAsync(options, CancellationToken.None));
```
- [ ] **Run the new test and see PASS.** Same filter — expected: 1 passed. Then the whole class: `--filter "FullyQualifiedName~SessionViewModelTests"` — all green.
- [ ] **Commit the VM half.**
```
git add src/LocalScribe.App/ViewModels/SessionViewModel.cs tests/LocalScribe.App.Tests/SessionViewModelTests.cs
git commit -m "feat(app): one-shot PendingStartTitle on SessionViewModel for the deep-link start path

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```
- [ ] **Wire `App.xaml.cs` — Edit A (field).** The fields currently open with (lines 12–13):
```csharp
    private SingleInstance? _singleInstance;
    private TrayIconHost? _tray;
```
Replace with:
```csharp
    private SingleInstance? _singleInstance;
    // Deep-link pipe listener (design 2026-07-18 section 4): first instance only; a second
    // instance TrySend()s its localscribe:// argv and exits. Disposed in OnExit.
    private DeepLinkChannel? _deepLink;
    private TrayIconHost? _tray;
```
- [ ] **Edit B (second-instance forward).** The single-instance guard currently reads (lines 45–54):
```csharp
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
```
Replace with:
```csharp
        _singleInstance = SingleInstance.TryAcquire(InstanceName,
            onActivateRequested: () => Dispatcher.BeginInvoke(() => _tray?.OpenMainWindow()));
        if (_singleInstance is null)
        {
            // Deep link (design 2026-07-18 section 4): when the OS launched this second instance
            // for a localscribe:// URL, forward the argv line to the running holder over the
            // per-user pipe and exit. No deep-link arg (or an unreachable pipe) falls back to the
            // original activate ping. Return values intentionally discarded: reachable holder or
            // not, this instance exits either way (TrySend/SignalExisting never throw).
            string? forwarded = Array.Find(e.Args,
                a => a.StartsWith("localscribe://", StringComparison.OrdinalIgnoreCase));
            if (forwarded is null
                || !DeepLinkChannel.TrySend(DeepLinkChannel.CurrentUserPipeName(), forwarded))
                _ = SingleInstance.SignalExisting(InstanceName);
            Shutdown();
            return;
        }

        // Deep-link scheme registration (design 2026-07-18 section 4): per-user HKCU
        // Software\Classes\localscribe - idempotent every launch, NEVER elevates, best-effort
        // (a registry failure leaves deep links dark; startup is never blocked).
        DeepLinkRegistrar.EnsureRegistered(Environment.ProcessPath);
```
- [ ] **Edit C (handler + pipe server).** The advisory app-mute timer wiring currently ends with (lines 517–520):
```csharp
        _appMuteTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromSeconds(2) };
        _appMuteTimer.Tick += (_, _) => appMuteWatcher.Poll();
        _appMuteTimer.Start();
```
Immediately after `_appMuteTimer.Start();` insert:
```csharp

        // Deep-link routing (design 2026-07-18 section 4). This handler runs ONLY on the
        // dispatcher (pipe lines are BeginInvoke-marshalled below; the launch-arg call sits inside
        // the ApplicationIdle block), so every toast shows after the message pump is up - and the
        // toast is a PLAIN Window anyway (the FluentWindow pre-pump invisible-Mica gotcha).
        // start runs the EXACT manual path: consent posture unchanged (the first-run consent modal
        // already ran above, before this server exists), and the Idle->Recording hook above opens
        // the Record console. stop NEVER stops here - only the explicit toast click does
        // (evidentiary rule); the confirm toast auto-dismisses to "keep recording" after 30 s.
        // The commands' own CanExecute gates are the race authority if a manual action lands
        // between the router's State read and execution. Invalid links log the parser's FIXED
        // reason only - the URL and query are NEVER logged (Steno's sanitization contract).
        Action<string> handleDeepLink = url =>
        {
            var decision = DeepLinkRouter.Route(
                LocalScribe.Core.DeepLink.DeepLinkParser.Parse(url), comp.Controller.State);
            switch (decision.Kind)
            {
                case DeepLinkActionKind.StartRecording:
                    session.PendingStartTitle = decision.Title;
                    if (session.StartCommand.CanExecute(null)) session.StartCommand.Execute(null);
                    else session.PendingStartTitle = null;        // raced: drop the one-shot title
                    break;
                case DeepLinkActionKind.ConfirmStop:
                    new Views.AdvisoryToastWindow("Stop recording?",
                        "A deep link asked LocalScribe to stop this recording. Nothing stops unless you choose to.",
                        new[]
                        {
                            new Views.ToastAction("Stop recording", () =>
                            {
                                if (session.StopCommand.CanExecute(null))
                                    session.StopCommand.Execute(null);
                            }),
                            new Views.ToastAction("Keep recording", () => { }),
                        }, autoDismissSeconds: 30).Show();
                    break;
                case DeepLinkActionKind.NotifyAlreadyRecording:
                    new Views.AdvisoryToastWindow("Already recording",
                        "A deep link asked LocalScribe to start recording, but a session is already in progress. Nothing changed.",
                        [], autoDismissSeconds: 8).Show();
                    break;
                case DeepLinkActionKind.NotifyNotRecording:
                    new Views.AdvisoryToastWindow("Not recording",
                        "A deep link asked LocalScribe to stop recording, but no recording is in progress.",
                        [], autoDismissSeconds: 8).Show();
                    break;
                default:
                    System.Diagnostics.Trace.WriteLine("deep link rejected: " + decision.Reason);
                    break;
            }
        };
        // First-instance pipe server. OS IPC (named pipe, CurrentUserOnly), not a socket - the
        // zero-network posture holds. onLine fires on the channel's background listener thread,
        // so it is dispatch-wrapped exactly like SingleInstance's activate callback.
        _deepLink = DeepLinkChannel.StartServer(DeepLinkChannel.CurrentUserPipeName(),
            onLine: url => Dispatcher.BeginInvoke(() => handleDeepLink(url)));
```
- [ ] **Edit D (launch-arg handling).** The deferred main-window open currently reads (lines 529–537):
```csharp
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Watch a persistent HWND so light/dark tracks the OS for the whole session,
            // regardless of which transient windows are open. The overlay lives the whole
            // session (shown/hidden, never closed); ensure its handle exists before watching.
            new System.Windows.Interop.WindowInteropHelper(_overlay!).EnsureHandle();
            SystemThemeWatcher.Watch(_overlay!);
            _tray?.OpenMainWindow();
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
```
Replace with:
```csharp
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Watch a persistent HWND so light/dark tracks the OS for the whole session,
            // regardless of which transient windows are open. The overlay lives the whole
            // session (shown/hidden, never closed); ensure its handle exists before watching.
            new System.Windows.Interop.WindowInteropHelper(_overlay!).EnsureHandle();
            SystemThemeWatcher.Watch(_overlay!);
            _tray?.OpenMainWindow();
            // Deep link on a COLD launch (the OS started this very instance for a localscribe://
            // URL): handled here at ApplicationIdle - after consent, after the tray/console exist,
            // and strictly after the message pump is up (FluentWindow gotcha + toast contract).
            string? launchLink = Array.Find(e.Args,
                a => a.StartsWith("localscribe://", StringComparison.OrdinalIgnoreCase));
            if (launchLink is not null) handleDeepLink(launchLink);
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
```
- [ ] **Edit E (shutdown).** `OnExit` currently reads (lines 572–580):
```csharp
    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownCts.Cancel();                   // stop an in-flight startup scan politely
        _timer?.Stop();
        _appMuteTimer?.Stop();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
```
Replace with:
```csharp
    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownCts.Cancel();                   // stop an in-flight startup scan politely
        _timer?.Stop();
        _appMuteTimer?.Stop();
        _tray?.Dispose();
        _deepLink?.Dispose();                    // join the pipe listener (bounded, see channel)
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
```
- [ ] **Full gate: 0-warning build + both suites.** Run:
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\deep-link\
```
Expected: build 0 warnings; App suite fully green (incl. the new DeepLink/Toast tests and `XamlHygieneTests`); Core suite green except the 2 known pre-existing fixture failures.
- [ ] **Manual smoke (WPF + registry + browser — not unit-testable).** Launch the app once (registration runs), then:
  1. **Registry:** `reg query HKCU\Software\Classes\localscribe /s` shows `URL Protocol` (empty), the label, and `shell\open\command` = `"<exe>" "%1"`. Launch the app a second time — values unchanged (idempotent), no elevation prompt ever.
  2. **Start from a browser (app running, idle):** navigate to `localscribe://record/start?name=Smith%20v.%20Jones%20(depo)` — browser hands off; NO second LocalScribe window/instance appears; recording starts through the normal path; the Record console opens; the finished session's title reads "Smith v. Jones (depo)".
  3. **Dirty name:** `localscribe://record/start?name=..%2F..%2Fx%22%3B` while idle — starts; title contains no slashes/quotes/semicolons.
  4. **Start while recording:** invoke the start link again mid-recording — an "Already recording" toast appears bottom-right, auto-dismisses in ~8 s, recording untouched.
  5. **Stop confirm:** `localscribe://record/stop` while recording — the "Stop recording?" toast appears bottom-right with [Stop recording] [Keep recording]; while it is up, keep typing in another app — focus is NEVER stolen (no-activate). Click [Keep recording] → recording continues. Repeat and click [Stop recording] → the session stops through the normal path. Repeat once more and let it sit 30 s → auto-dismiss, recording continues (dismiss = keep).
  6. **Stop while idle:** the stop link while idle — "Not recording" toast, nothing else.
  7. **Cold launch:** quit LocalScribe fully (tray Exit), then click the start link — the app launches (consent already granted → no dialog), main window appears, recording starts, console opens.
  8. **Invalid links:** `localscribe://record/pause` and `localscribe://evil` — nothing visible happens, app unaffected.
  9. **Themes:** flip Windows light/dark — toast background/border/text stay readable in both.
- [ ] **Commit the wiring.**
```
git add src/LocalScribe.App/App.xaml.cs
git commit -m "feat(app): deep-link wiring - registration, second-instance forward, pipe server, toast-confirmed stop

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-review

**(a) Spec coverage — every design §4 sentence maps to tasks:**
- Scheme + per-user HKCU registration, idempotent at startup, unpackaged-app pattern → **Task 4** (`DeepLinkRegistrar`, `URL Protocol` empty value + quoted `shell\open\command`) + **Task 6 Edit B** (runs every launch, first instance only, never elevates, best-effort).
- Single-instance routing, "if a mechanism exists, reuse it" → resolved at plan time: `SingleInstance` (mutex `Local\LocalScribe` + activate event) EXISTS and stays the arbiter; **Task 3** adds only the argv pipe (`DeepLinkChannel`, `PipeOptions.CurrentUserOnly`, per-user SID-suffixed name), **Task 6 Edit B** forwards-and-exits with the `SignalExisting` fallback for non-deep-link second launches. Named pipe = OS IPC, no sockets (zero-network posture stated in Global Constraints).
- Parser: pure static Core `DeepLinkParser`, never throws, typed reject + reason, exact two-verb allowlist, Steno sanitization (keep-list, space substitution, collapse, trim, 120 cap, empty→null), query never logged → **Task 1**, with the full required matrix: valid start with/without name, dirty names (control chars, injection shapes, overlong), wrong scheme/host/path, malformed URIs (incl. the opaque no-authority form and null), casing, plus-literal policy, trailing slash, stop-ignores-query. Reason strings are fixed constants; a test asserts input is never echoed.
- `start` semantics: exact manual command path (consent as configured — the first-run consent modal precedes both the pipe server and ApplicationIdle handling; declining shuts down first), console opens (the existing Idle→Recording hook), name prefills the session title → **Task 2** (`LiveSessionOptions.Title` riding `SessionBootstrap`'s existing title parameter) + **Task 6** (`PendingStartTitle` one-shot + `StartCommand`). Start-while-recording → notification toast, no action (**Task 4** router + **Task 6** handler; the CanExecute re-check closes the router-read race).
- `stop` semantics: NEVER stops directly; confirm toast **[Stop recording] [Keep recording]**, no-activate, only the explicit click stops, auto-dismiss = keep (safe default); stop-while-idle → notification toast → **Task 4** router (`ConfirmStop` is the ONLY stop-adjacent decision) + **Task 6** handler + **Task 5** window.
- LOCKED toast contract: `public AdvisoryToastWindow(string title, string body, IReadOnlyList<ToastAction> actions, int autoDismissSeconds)` + `public sealed record ToastAction(string Caption, Action OnClick)` in `src\LocalScribe.App\Views\AdvisoryToastWindow.xaml(.cs)` → **Task 5**, verbatim, flagged in Global Constraints for the `feat/call-detect-advisory` reuse. Plain Window (FluentWindow gotcha), `Topmost`, `ShowActivated=False`/`Focusable=False` + `WS_EX_NOACTIVATE` source hook (existing `NativeWindowInterop.MakeNoActivate`), bottom-right primary work area, constructor-configurable auto-dismiss. Non-UI logic (placement + dismiss decision) extracted to pure, fully tested `ToastPlacement` — the App.Tests viewmodel-not-window pattern.
- §1 binding rules re-checked: no task writes markers, touches transcript content, gates recording, or adds logging of deep-link input; degradation (busy/idle/invalid) is always surfaced via toast or fixed-reason trace, never silent action.
**(b) Placeholder scan:** no TBD / "similar to" / elided bodies anywhere — every step carries the complete test code, the complete implementation, quoted current-code anchors for every modification (`SessionController.cs` 28–31/466–468, `SessionViewModel.cs` ~105/289–292, `App.xaml.cs` 12–13/45–54/517–520/529–537/572–580, all quoted from the pre-branch tree; re-verify by content per Global Constraints), exact run commands with the isolated `deep-link` BaseOutputPath, and expected FAIL/PASS outputs.
**(c) Type consistency across tasks:** `DeepLinkParser.Parse(string) : DeepLinkResult` (T1) → `DeepLinkRouter.Route(DeepLinkResult, SessionState) : DeepLinkDecision` (T4) → `decision.Title : string?` → `SessionViewModel.PendingStartTitle : string?` (T6) → `LiveSessionOptions.Title : string?` (T2) → `SessionBootstrap.StartAsync(..., string? title)` (existing). `DeepLinkChannel.StartServer(string, Action<string>)`/`TrySend(string, string, int) : bool` (T3) match the T6 call sites; `onLine` thread contract is dispatch-wrapped at the call site. `AdvisoryToastWindow(string, string, IReadOnlyList<ToastAction>, int)` (T5) is called in T6 with `new[] { new ToastAction(...) }` (array → `IReadOnlyList<T>`) and `[]` (collection expression, the repo's established C# 12 usage). All members tests touch are `public` (no `InternalsVisibleTo` — verified); `MetadataStore`/`Settings`/`LiveTestDoubles`/`FakeClock.ElapsedMs`/`controller.PendingFinalize` usages in tests match the shapes read from `MetadataStore.cs`, `LiveTestDoubles.cs`, and the existing `SessionViewModelTests`/`SessionControllerTests` idioms; fully-qualified `LocalScribe.Core.Storage.MetadataStore`/`LocalScribe.Core.DeepLink.DeepLinkParser` avoid any dependence on unverified using blocks. New XAML uses theme resources only, so the recursive `XamlHygieneTests` brush scan and the untouched root-marker list both stay green.
