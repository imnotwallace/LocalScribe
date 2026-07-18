# Call-Detect Advisory Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement design §5 (`feat/call-detect-advisory`, all four sub-items) of `docs/plans/2026-07-18-steno-round-design.md`: (5.1) a Core `CallActivityWatcher` that polls ACTIVE capture-endpoint audio sessions every 1.5 s and diffs them into `CallAppActivity` Started/Stopped events, (5.2) a pure `CallDetectionPolicy` (master toggle, case-insensitive exe allowlist with defaults `CiscoCollabHost.exe`/`webex.exe`/`ms-teams.exe`/`Zoom.exe`, own-process exclusion, recording/console-armed suppression, 60 s per-exe cooldown), (5.3) an offer toast "Call detected — \<App>" with **[Start recording] [Dismiss]** (15 s auto-dismiss) whose Start routes through the SAME manual-start path with the detected exe applied via the existing `RemoteTargetOverride` seam, and (5.4) a call-end advisory ("Call appears to have ended — stop recording?" **[Stop recording] [Keep recording]**) behind a pure 3 s debounce that a session-return cancels. Plus a Settings section: master toggle (default ON) + allowlist editor (add/remove/reset-to-defaults).
**Architecture:** Core gets four additive pieces beside the existing capture stack: `CallDetectSetting` on `Settings` (additive v3, the `SectionGapMs` precedent); `WasapiSessionScanner` gains a `DataFlow` ctor parameter so the SAME NAudio endpoint walk that already feeds the remote-target picker (`DataFlow.Render`) also enumerates capture endpoints (`DataFlow.Capture` = apps actively recording from the mic — the call signal); `CallActivityWatcher` (poll-diff over the injected `IAudioSessionScanner` seam, external `Poll()` exactly like `AppMuteWatcher`, fail-open on scanner errors); pure `CallDetectionPolicy` + a `CallEndAdvisor` debounce state machine. The App layer adds a WPF-free `CallDetectionCoordinator` (policy inputs, per-exe cooldown ledger, advisor arm/disarm; raises `OfferRequested`/`CallEndAdvised` and NOTHING else), a `RecordingConsoleViewModel.ApplyDetectedTarget` that goes through the picker's own `SelectedRemoteTarget` setter (so the override seam and the console UI agree), `TrayIconHost.IsLiveViewVisible` (console-armed input), the Settings page section, and `App.xaml.cs` wiring: a 1.5 s `DispatcherTimer` started inside the existing ApplicationIdle block (pump-up gotcha) driving `Poll()`+`OnTick()`, with both toasts rendered by the deep-link branch's `AdvisoryToastWindow` (consumed by contract, never redefined).
**Tech Stack:** C#/.NET 10, WPF, NAudio.CoreAudioApi (already in Core), CommunityToolkit.Mvvm, Wpf.Ui, xUnit.

## Global Constraints

- **Target branch:** `feat/call-detect-advisory`, created off master AFTER `feat/ux-round-2026-07-18` merges. **Merge order for the round: 4th of 7** — after `fix/dedup-short-utterance-guard`, `feat/markdown-export`, and `feat/deep-link`, before `feat/console-compact-mode` and the two assistant branches. **This branch DEPENDS ON `feat/deep-link`** for the toast primitive (contract below) — do not start it until deep-link is on master. The design spec `docs/plans/2026-07-18-steno-round-design.md` is already on master; only THIS plan (`docs/plans/2026-07-19-call-detect-advisory-plan.md`) needs adding to the branch (a `docs(plans): ...` commit) if it is not there yet.
- **AdvisoryToastWindow CONTRACT (produced by `feat/deep-link` — consume EXACTLY, do not redefine):** `src\LocalScribe.App\Views\AdvisoryToastWindow.xaml(.cs)` with `public AdvisoryToastWindow(string title, string body, IReadOnlyList<ToastAction> actions, int autoDismissSeconds)` and `public sealed record ToastAction(string Caption, Action OnClick)` — a plain Window (not Mica — the WPF-UI startup-rendering gotcha), Topmost, no-activate, bottom-right of the work area, auto-dismiss. This plan only constructs it and calls `.Show()`; it adds no toast types of its own. If the merged contract differs in any detail, adapt the two call sites in Task 8 to the ACTUAL merged surface — never re-declare `ToastAction`.
- **LOCKED rules (restated from design §1/§5 + project memory; every task honors them):** detection is **advisory-only** — it never auto-starts, never auto-stops, never auto-pauses a recording; it **never writes markers**; it **never gates or delays capture** (no task touches `StartAsync` control flow, capture legs, or command CanExecute gates); the **consent flow is unchanged** (the toast's Start runs the same command path as the tray/console Start button); the watcher is **fail-open** — any COM/enumeration error logs, skips the tick, and can never affect capture (the `TrayMuteSignalSource` contract).
- **Line anchors are grounded @ `82546aa` (the tip of `feat/ux-round-2026-07-18`; the round brief's `7605606` exists but is a superseded pre-amend version of the same docs commit — the quoted code is identical).** Three earlier round branches merge before this one and `feat/deep-link` ALSO edits `App.xaml.cs` (single-instance argv forwarding) and creates `Views\` — so **re-verify every anchor's quoted context before editing; if drifted, locate by the quoted code, not the number.**
- **Capture-stack safety:** the only existing capture-stack file touched is `WasapiSessionScanner.cs`, and only to parameterize the endpoint direction — the parameterless ctor keeps `DataFlow.Render` and byte-identical behavior for every existing caller (`CompositionRoot` constructs `new WasapiSessionScanner()` unchanged).
- **Smoke item (design §5.2, binding):** the real Webex **capture**-session owner exe must be verified during the Task 8 manual smoke (the render owner is `CiscoCollabHost`; the mic-capture owner may differ) and the `CallDetectSetting` defaults adjusted in a follow-up commit if it does. The allowlist matcher strips `.exe` and ignores case, so the design's exe-name spellings and the scanner's extensionless images always meet.
- 0-warning build gate must hold.
- Tests: xUnit. Filtered run: `dotnet test "<testproj>" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\`
- **Full App-suite gate runs (XamlHygieneTests):** `RepoPaths.SolutionRoot()` walks UP from the test assembly directory to find `.git`, so an App-suite run that includes `XamlHygieneTests` MUST NOT use the Temp isolated BaseOutputPath (it sits outside the repo — 5 false failures). Run full App-suite gates with the default repo-internal output path; keep the isolated path for filtered runs. If the default path hits MSB3027 (app running, locked bin), report and wait — never kill processes.
- IMPORTANT: the LocalScribe app may be running and LOCK bin DLLs (MSB3027 copy error — NOT a compile error). Always use the isolated BaseOutputPath above (every command below already appends it); NEVER kill the user's running app or any other process.
- Never use Unicode emojis in test code or scripts (project rule). Every UI string in this plan is plain ASCII.
- Test projects: App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. Core.Tests files carry no namespace (file-level classes, global `Using Include="Xunit"`); App.Tests files use `namespace LocalScribe.App.Tests;`. `ManualUtcTimeProvider` (Core.Tests, global namespace) is `<Compile Include>`-linked into App.Tests, so BOTH suites can use it. There is NO `InternalsVisibleTo` anywhere in this repo (verified) — new members that tests call directly must be `public`.
- Commit style: `feat(core)`/`feat(app)`/`test(...)`/`docs(...)`; every commit message ends with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```
- Core suite has 2 known pre-existing fixture failures (unrelated); App suite must be fully green.

---

### Task 1: Core — `CallDetectSetting` on `Settings` (additive v3)
**Files:**
- Modify `src\LocalScribe.Core\Model\Settings.cs` (property after line 38 `public PrivacySetting Privacy { get; init; } = new();`; record after line 49 `public sealed record PrivacySetting { ... }`).
- Test `tests\LocalScribe.Core.Tests\SettingsTests.cs` (append three `[Fact]`s before the closing brace, reusing the file's existing `CleanParent` helper).

**Interfaces:**
- Produces: `public CallDetectSetting Settings.CallDetect { get; init; } = new();` and `public sealed record CallDetectSetting { public bool Enabled { get; init; } = true; public IReadOnlyList<string> Apps { get; init; } = ["CiscoCollabHost.exe", "webex.exe", "ms-teams.exe", "Zoom.exe"]; }`. Additive v3 field (the `SectionGapMs`/`DocxFooterText` precedent): no schema bump, no migration change — an existing settings.json without the member loads the defaults. Serialization is reflection-based camelCase via `JsonFile` (no source-gen registration exists to update).
- Deliberately does NOT touch the dormant `AutoDetectSetting` (line 44): its `Enabled=false` is pinned by the v1→v2 migration tests (`SettingsTests.cs:18,62`), and its `Apps` hold friendly names, not exe images. The new advisory tier is a NEW surface with a NEW name; the Settings-page reflection test banning "AutoDetect" surfaces (`SettingsPageViewModelTests.cs:247`) stays green because "CallDetect" does not contain that substring.
- Default ON is safe by the locked rule: the setting only ever gates an offer TOAST — nothing downstream of it can start, stop, pause, gate, or mark a recording.

Steps:
- [ ] **Write the failing tests.** Append inside `SettingsTests` (before the closing brace) in `tests\LocalScribe.Core.Tests\SettingsTests.cs`:
```csharp
    [Fact]
    public async Task Fresh_install_call_detect_defaults_on_with_the_known_call_apps()
    {
        // Design 2026-07-18 section 5.2: master toggle DEFAULT ON; allowlist defaults to the four
        // known call apps in the design's exe-name spelling (the extensionless-image matching lives
        // in CallDetectionPolicy.ExeKey, Task 3 - settings keep the human-readable form). Additive
        // v3 field (SectionGapMs precedent): no schema bump, absence loads defaults. Default-ON is
        // safe by the locked rule - the toggle only gates an advisory offer toast, never capture.
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.True(s.CallDetect.Enabled);
            Assert.Equal(new[] { "CiscoCollabHost.exe", "webex.exe", "ms-teams.exe", "Zoom.exe" },
                s.CallDetect.Apps);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Existing_v3_file_without_call_detect_loads_the_on_default()
    {
        // A settings.json saved BEFORE this branch has no callDetect member: the record default
        // must stand without any migration (field-absence semantics, the DocxFooterText precedent).
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":3,\"audioRetention\":\"keep\"}");
            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.True(s.CallDetect.Enabled);
            Assert.Equal(4, s.CallDetect.Apps.Count);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Roundtrips_call_detect_wire_values()
    {
        // camelCase wire shape like every other section ("callDetect": { "enabled": ..., "apps":
        // [...] }). Asserted via the section name + a default entry - "enabled": true alone would
        // also match the overlay section.
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            await new SettingsStore(path).SaveAsync(new Settings(), default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"callDetect\"", json);
            Assert.Contains("\"CiscoCollabHost.exe\"", json);
        }
        finally { CleanParent(path); }
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~call_detect" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\` — expected: `error CS1061: 'Settings' does not contain a definition for 'CallDetect'`.
- [ ] **Add the property.** In `src\LocalScribe.Core\Model\Settings.cs`, immediately after line 38 (`public PrivacySetting Privacy { get; init; } = new();`) and before the record's closing brace, insert:
```csharp
    /// <summary>v3 (design 2026-07-18 section 5.2): the call-detection advisory's master toggle +
    /// exe allowlist. Additive - existing v3 files without it load at this default (the
    /// SectionGapMs precedent), so no schema bump/migration is required. Default ON is safe by the
    /// locked rule: detection is ADVISORY-ONLY (an offer toast) - it never starts/stops/pauses
    /// capture and never writes markers. Distinct from the dormant AutoDetectSetting above (a
    /// disabled v1 seam pinned off by the migration tests, friendly-name-shaped) - that record is
    /// deliberately left untouched.</summary>
    public CallDetectSetting CallDetect { get; init; } = new();
```
- [ ] **Add the record.** At the end of the same file, after line 49 (`public sealed record PrivacySetting { public bool ExcludeWindowsFromCapture { get; init; } = true; }`), append:
```csharp
/// <summary>Call-detection advisory config (design 2026-07-18 section 5.2). Apps hold exe-file
/// spellings ("webex.exe") for readability; matching strips the extension and ignores case
/// (CallDetectionPolicy.ExeKey, Task 3) because WASAPI session images arrive EXTENSIONLESS
/// (Process.ProcessName). Browsers are excluded by default (addable). The real Webex
/// capture-session owner exe is verified during smoke and these defaults adjusted if it differs
/// (Global Constraints).</summary>
public sealed record CallDetectSetting
{
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Apps { get; init; } =
        ["CiscoCollabHost.exe", "webex.exe", "ms-teams.exe", "Zoom.exe"];
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 3 passed. Then run the whole class to prove the migration/roundtrip suite still holds: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SettingsTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\`.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Model/Settings.cs tests/LocalScribe.Core.Tests/SettingsTests.cs
git commit -m "feat(core): CallDetectSetting - advisory call-detection toggle + exe allowlist (additive v3)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Core — capture-endpoint scan direction + `CallActivityWatcher` (poll-diff)
**Files:**
- Modify `src\LocalScribe.Core\Live\WasapiSessionScanner.cs` (parameterize the endpoint direction; lines 15–21).
- New `src\LocalScribe.Core\Live\CallActivityWatcher.cs`.
- New test `tests\LocalScribe.Core.Tests\CallActivityWatcherTests.cs` (SDK-style project — new files compile automatically).

**Interfaces:**
- Produces:
  - `public WasapiSessionScanner(DataFlow flow)` — the SAME NAudio walk over the chosen direction; the existing parameterless ctor delegates to `DataFlow.Render` so every current caller (`CompositionRoot.cs:72`, LiveRunner/SpikeRunner) is behavior-identical. `DataFlow.Capture` enumerates apps actively RECORDING from microphones — the call signal (design §5.1). Humble Object like the render path: exercised by the Task 8 smoke, not unit tests.
  - `public enum CallAppActivityKind { Started, Stopped }` and `public sealed record CallAppActivity(string Exe, int Pid, CallAppActivityKind Kind, DateTimeOffset Timestamp)` — `Exe` is the extensionless image (`AudioSessionInfo.ProcessName`), `Timestamp` the observing poll tick from the injected `TimeProvider`.
  - `public sealed class CallActivityWatcher` — ctor `(IAudioSessionScanner scanner, TimeProvider time)`; `public event Action<CallAppActivity>? Activity;`; `public void Poll()` (externally driven — the `AppMuteWatcher` lifecycle pattern: the 1.5 s DispatcherTimer lives in Task 8's composition, tests call `Poll()` directly); `public IReadOnlyCollection<string> ActiveExes { get; }` (distinct exes of the last successful scan — the call-end arming input); `public void Reset()` (clears the diff baseline when the master toggle flips off).
  - Diff is keyed **per PID** (a second process of the same exe still reports). Fail-open: a scanner throw traces + skips the tick and keeps the PRE-error baseline — it never fabricates Stopped events (which could fire a false call-end advisory).
- Consumes: `IAudioSessionScanner`/`AudioSessionInfo` (existing, `WasapiSessionScanner.cs:6-9` / `RemoteCapturePlanner.cs:5`), `System.TimeProvider`.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\CallActivityWatcherTests.cs`:
```csharp
using LocalScribe.Core.Live;

public class CallActivityWatcherTests
{
    // Design 2026-07-18 section 5.1: poll-and-diff over ACTIVE capture-endpoint sessions, the
    // Windows analog of Steno's mic-monitor. The WASAPI walk hides behind IAudioSessionScanner,
    // so every diff branch is deterministic here; the real DataFlow.Capture walk is smoke-only
    // (Humble Object, like WasapiSessionScanner's render path).

    private sealed class ScriptedScanner : IAudioSessionScanner
    {
        public List<AudioSessionInfo> Active { get; } = new();
        public bool Throw;
        public IReadOnlyList<AudioSessionInfo> Scan()
            => Throw ? throw new InvalidOperationException("COM enumeration failed") : Active.ToList();
    }

    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 10, 0, 0, TimeSpan.Zero);

    private static (CallActivityWatcher Watcher, ScriptedScanner Scanner, ManualUtcTimeProvider Time,
        List<CallAppActivity> Events) Make()
    {
        var scanner = new ScriptedScanner();
        var time = new ManualUtcTimeProvider(T0);
        var watcher = new CallActivityWatcher(scanner, time);
        var events = new List<CallAppActivity>();
        watcher.Activity += events.Add;
        return (watcher, scanner, time, events);
    }

    [Fact]
    public void First_poll_reports_each_active_session_as_started_with_the_tick_timestamp()
    {
        var (watcher, scanner, _, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "CiscoCollabHost"));
        scanner.Active.Add(new AudioSessionInfo(202, "chrome"));
        watcher.Poll();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e is { Exe: "CiscoCollabHost", Pid: 101, Kind: CallAppActivityKind.Started });
        Assert.Contains(events, e => e is { Exe: "chrome", Pid: 202, Kind: CallAppActivityKind.Started });
        Assert.All(events, e => Assert.Equal(T0, e.Timestamp));
    }

    [Fact]
    public void Unchanged_set_raises_nothing()
    {
        var (watcher, scanner, time, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "CiscoCollabHost"));
        watcher.Poll();
        events.Clear();
        time.Set(T0 + TimeSpan.FromSeconds(1.5));
        watcher.Poll();
        Assert.Empty(events);
    }

    [Fact]
    public void A_session_leaving_reports_stopped_with_the_observing_ticks_timestamp()
    {
        var (watcher, scanner, time, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "CiscoCollabHost"));
        watcher.Poll();
        events.Clear();
        scanner.Active.Clear();
        time.Set(T0 + TimeSpan.FromSeconds(3));
        watcher.Poll();
        var e = Assert.Single(events);
        Assert.Equal(("CiscoCollabHost", 101, CallAppActivityKind.Stopped, T0 + TimeSpan.FromSeconds(3)),
            (e.Exe, e.Pid, e.Kind, e.Timestamp));
    }

    [Fact]
    public void Scanner_error_skips_the_tick_and_never_fabricates_stopped_events()
    {
        // Fail-open (locked rule): a COM hiccup must not look like every call ending at once - a
        // fabricated Stopped would arm the 3 s call-end debounce off a transient error. The next
        // successful poll diffs against the PRE-error baseline, so a session that genuinely
        // survived the hiccup raises nothing.
        var (watcher, scanner, time, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "CiscoCollabHost"));
        watcher.Poll();
        events.Clear();
        scanner.Throw = true;
        watcher.Poll();
        Assert.Empty(events);                                     // error tick: no events at all
        scanner.Throw = false;
        time.Set(T0 + TimeSpan.FromSeconds(3));
        watcher.Poll();
        Assert.Empty(events);                                     // survived the hiccup: still no diff
        Assert.Contains("CiscoCollabHost", watcher.ActiveExes);
    }

    [Fact]
    public void Diff_is_per_pid_so_a_second_process_of_the_same_exe_still_reports()
    {
        var (watcher, scanner, _, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "ms-teams"));
        watcher.Poll();
        events.Clear();
        scanner.Active.Add(new AudioSessionInfo(102, "ms-teams"));
        watcher.Poll();
        var e = Assert.Single(events);
        Assert.Equal((102, CallAppActivityKind.Started), (e.Pid, e.Kind));
    }

    [Fact]
    public void Reset_clears_the_baseline_so_the_next_poll_rereports_started()
    {
        // Master toggle off -> Reset + no polling; toggling back on re-reports the then-active
        // sessions as fresh Starts (policy cooldown dedups offers) instead of diffing stale state.
        var (watcher, scanner, _, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "Zoom"));
        watcher.Poll();
        events.Clear();
        watcher.Reset();
        Assert.Empty(watcher.ActiveExes);
        watcher.Poll();
        var e = Assert.Single(events);
        Assert.Equal(("Zoom", CallAppActivityKind.Started), (e.Exe, e.Kind));
    }

    [Fact]
    public void ActiveExes_deduplicates_images_across_pids()
    {
        var (watcher, scanner, _, _) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "ms-teams"));
        scanner.Active.Add(new AudioSessionInfo(102, "ms-teams"));
        scanner.Active.Add(new AudioSessionInfo(103, "Zoom"));
        watcher.Poll();
        Assert.Equal(2, watcher.ActiveExes.Count);
        Assert.Contains("ms-teams", watcher.ActiveExes);
        Assert.Contains("Zoom", watcher.ActiveExes);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~CallActivityWatcher" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\` — expected: `error CS0246: The type or namespace name 'CallActivityWatcher' could not be found` (plus CS0246 on `CallAppActivity`/`CallAppActivityKind`).
- [ ] **Parameterize the scanner's direction.** In `src\LocalScribe.Core\Live\WasapiSessionScanner.cs`, the class currently opens (lines 15–21):
```csharp
public sealed class WasapiSessionScanner : IAudioSessionScanner
{
    public IReadOnlyList<AudioSessionInfo> Scan()
    {
        var enumerator = new MMDeviceEnumerator();
        var active = new List<AudioSessionInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
```
Replace with:
```csharp
public sealed class WasapiSessionScanner : IAudioSessionScanner
{
    private readonly DataFlow _flow;

    /// <summary>Render endpoints - the original behavior for the remote-target picker/planner
    /// scan. Every existing caller uses this ctor and is behavior-identical.</summary>
    public WasapiSessionScanner() : this(DataFlow.Render) { }

    /// <summary>The same walk over the chosen endpoint direction. DataFlow.Capture enumerates the
    /// apps actively RECORDING from microphones - the call-detection signal (design 2026-07-18
    /// section 5.1): an allowlisted app opening the mic means a call is starting. One
    /// parameterized scanner instead of a parallel capture copy, so the two directions can never
    /// drift.</summary>
    public WasapiSessionScanner(DataFlow flow) => _flow = flow;

    public IReadOnlyList<AudioSessionInfo> Scan()
    {
        var enumerator = new MMDeviceEnumerator();
        var active = new List<AudioSessionInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(_flow, DeviceState.Active))
```
(The rest of the method — session state filter, PID resolve, process-name resolve, per-session try/catch — is untouched.)
- [ ] **Create the watcher.** New file `src\LocalScribe.Core\Live\CallActivityWatcher.cs`:
```csharp
namespace LocalScribe.Core.Live;

/// <summary>The kind of capture-session transition <see cref="CallActivityWatcher"/> observed.</summary>
public enum CallAppActivityKind { Started, Stopped }

/// <summary>One capture-endpoint audio-session transition (design 2026-07-18 section 5.1): an app
/// began (Started) or ceased (Stopped) actively recording from a capture endpoint. Exe is the
/// EXTENSIONLESS process image (Process.ProcessName via the scanner - "CiscoCollabHost", not
/// "CiscoCollabHost.exe"); Timestamp is the poll tick that observed the change, from the injected
/// TimeProvider (so the call-end debounce math is deterministic in tests).</summary>
public sealed record CallAppActivity(string Exe, int Pid, CallAppActivityKind Kind, DateTimeOffset Timestamp);

/// <summary>Poll-and-diff over ACTIVE capture-endpoint audio sessions (design 2026-07-18 section
/// 5.1 - the Windows analog of Steno's mic-monitor poll-diff, in-process, no helper needed). The
/// WASAPI walk hides behind the injected IAudioSessionScanner seam (production:
/// WasapiSessionScanner over DataFlow.Capture), so the poller/diff logic is fully unit-tested
/// with fakes. Poll() is driven EXTERNALLY - the App's 1.5 s DispatcherTimer, the AppMuteWatcher
/// lifecycle pattern; tests call it directly. FAIL-OPEN by locked rule: a scanner error traces,
/// skips the tick, keeps the previous baseline (never fabricating Stopped events, which could
/// fire a false call-end advisory), and can never affect capture. ADVISORY-ONLY consumer
/// contract: subscribers may only surface offers - nothing downstream of Activity may
/// start/stop/pause capture or write markers. Single-threaded by contract (UI-thread timer).</summary>
public sealed class CallActivityWatcher
{
    private readonly IAudioSessionScanner _scanner;
    private readonly TimeProvider _time;
    private Dictionary<uint, string> _previous = new();

    public CallActivityWatcher(IAudioSessionScanner scanner, TimeProvider time)
        => (_scanner, _time) = (scanner, time);

    public event Action<CallAppActivity>? Activity;

    /// <summary>Distinct exe images in the last successful scan - the call-end advisor's arming
    /// input (which allowlisted apps were live when recording started).</summary>
    public IReadOnlyCollection<string> ActiveExes
        => _previous.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public void Poll()
    {
        IReadOnlyList<AudioSessionInfo> scanned;
        try { scanned = _scanner.Scan(); }
        catch (Exception ex)
        {
            // Fail-open (TrayMuteSignalSource's contract): log + skip. The next successful poll
            // diffs against the PRE-error baseline, so a transient COM hiccup never looks like
            // every call ending at once.
            System.Diagnostics.Trace.WriteLine($"call-detect scan skipped: {ex.Message}");
            return;
        }
        var now = _time.GetUtcNow();
        var current = new Dictionary<uint, string>();
        foreach (var s in scanned) current[s.Pid] = s.ProcessName;
        foreach (var (pid, exe) in current)
            if (!_previous.ContainsKey(pid))
                Activity?.Invoke(new CallAppActivity(exe, (int)pid, CallAppActivityKind.Started, now));
        foreach (var (pid, exe) in _previous)
            if (!current.ContainsKey(pid))
                Activity?.Invoke(new CallAppActivity(exe, (int)pid, CallAppActivityKind.Stopped, now));
        _previous = current;
    }

    /// <summary>Clears the diff baseline (master toggle flipped off - polling stops while
    /// disabled). Re-enabling then re-reports the then-active sessions as fresh Starts (the
    /// policy's cooldown dedups offers) instead of diffing against a stale world.</summary>
    public void Reset() => _previous = new();
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 7 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Live/WasapiSessionScanner.cs src/LocalScribe.Core/Live/CallActivityWatcher.cs tests/LocalScribe.Core.Tests/CallActivityWatcherTests.cs
git commit -m "feat(core): CallActivityWatcher - 1.5s poll-diff over capture-endpoint sessions (fail-open)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Core — `CallDetectionPolicy` (pure offer decision)
**Files:**
- New `src\LocalScribe.Core\Live\CallDetectionPolicy.cs`.
- New test `tests\LocalScribe.Core.Tests\CallDetectionPolicyTests.cs`.

**Interfaces:**
- Produces:
  - `public sealed record CallDetectionSnapshot(bool Enabled, IReadOnlyList<string> Allowlist, int OwnPid, bool RecordingActive, bool ConsoleArmed, IReadOnlyDictionary<string, DateTimeOffset> LastOfferedAt, DateTimeOffset Now)` — everything the decision needs, captured by the caller (design §5.2's snapshot). `LastOfferedAt` is keyed by `ExeKey`.
  - `public sealed record CallDetectionDecision(bool Offer, string? IgnoreReason)` with `public static readonly CallDetectionDecision Offered` and `public static CallDetectionDecision Ignore(string reason)`.
  - `public static class CallDetectionPolicy` with `public static readonly TimeSpan OfferCooldown = TimeSpan.FromSeconds(60);` (Steno's `MIC_NOTIFICATION_DEBOUNCE_MS`), `public static string ExeKey(string exe)` (trim, strip ONE trailing `.exe` case-insensitively, lower-invariant — the shared normalizer for allowlist matching, the cooldown ledger, and Task 4's watched-set), and `public static CallDetectionDecision Decide(CallAppActivity activity, CallDetectionSnapshot snap)` — pure, exhaustively unit-tested. Ordered checks: not-a-Start → toggle off → own PID → not allowlisted → recording active → console armed → per-exe cooldown (`Now - last < OfferCooldown` suppresses; at exactly 60 s it offers again) → `Offered`.
- Consumes: `CallAppActivity`/`CallAppActivityKind` (Task 2). Advisory-only by construction: the policy returns a value; it holds no state and calls nothing.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\CallDetectionPolicyTests.cs`:
```csharp
using LocalScribe.Core.Live;

public class CallDetectionPolicyTests
{
    // Design 2026-07-18 section 5.2. Pure function: every suppression branch is driven directly.

    private static readonly DateTimeOffset Now = new(2026, 7, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] Defaults =
        ["CiscoCollabHost.exe", "webex.exe", "ms-teams.exe", "Zoom.exe"];

    private static CallAppActivity Started(string exe, int pid = 100)
        => new(exe, pid, CallAppActivityKind.Started, Now);

    private static CallDetectionSnapshot Snap(
        bool enabled = true, IReadOnlyList<string>? allowlist = null, int ownPid = 4242,
        bool recording = false, bool consoleArmed = false,
        IReadOnlyDictionary<string, DateTimeOffset>? lastOfferedAt = null, DateTimeOffset? now = null)
        => new(enabled, allowlist ?? Defaults, ownPid, recording, consoleArmed,
            lastOfferedAt ?? new Dictionary<string, DateTimeOffset>(), now ?? Now);

    [Fact]
    public void Allowlisted_start_offers_and_matching_ignores_case_and_extension()
    {
        // The scanner reports EXTENSIONLESS images (Process.ProcessName); the settings defaults
        // keep the design's ".exe" spelling - ExeKey folds both sides, both directions.
        Assert.True(CallDetectionPolicy.Decide(Started("CiscoCollabHost"), Snap()).Offer);
        Assert.True(CallDetectionPolicy.Decide(Started("WEBEX"), Snap()).Offer);
        Assert.True(CallDetectionPolicy.Decide(Started("zoom.EXE"), Snap()).Offer);
        Assert.False(CallDetectionPolicy.Decide(Started("chrome"), Snap()).Offer);   // browsers: excluded by default
        Assert.False(CallDetectionPolicy.Decide(Started("obs64"), Snap()).Offer);    // any non-listed mic user
    }

    [Fact]
    public void Stopped_events_never_offer()
    {
        var stopped = new CallAppActivity("webex", 100, CallAppActivityKind.Stopped, Now);
        var d = CallDetectionPolicy.Decide(stopped, Snap());
        Assert.False(d.Offer);
        Assert.NotNull(d.IgnoreReason);
    }

    [Fact]
    public void Master_toggle_off_ignores_everything()
    {
        Assert.False(CallDetectionPolicy.Decide(Started("webex"), Snap(enabled: false)).Offer);
    }

    [Fact]
    public void Own_process_is_always_excluded()
    {
        // LocalScribe's own mic capture is an active capture session too - it must never
        // self-offer (belt: the recording-active check also covers it; braces: pid).
        Assert.False(CallDetectionPolicy.Decide(Started("webex", pid: 4242), Snap(ownPid: 4242)).Offer);
    }

    [Fact]
    public void Active_session_or_armed_console_suppresses_offers()
    {
        Assert.False(CallDetectionPolicy.Decide(Started("webex"), Snap(recording: true)).Offer);
        Assert.False(CallDetectionPolicy.Decide(Started("webex"), Snap(consoleArmed: true)).Offer);
        Assert.True(CallDetectionPolicy.Decide(Started("webex"), Snap()).Offer);   // both clear -> offer
    }

    [Fact]
    public void Cooldown_suppresses_within_60s_and_reopens_at_the_boundary_per_exe()
    {
        var offered = new Dictionary<string, DateTimeOffset> { ["webex"] = Now };   // ExeKey form
        Assert.False(CallDetectionPolicy.Decide(Started("webex"),
            Snap(lastOfferedAt: offered, now: Now + TimeSpan.FromSeconds(59))).Offer);
        Assert.True(CallDetectionPolicy.Decide(Started("webex"),
            Snap(lastOfferedAt: offered, now: Now + TimeSpan.FromSeconds(60))).Offer);   // >= cooldown
        // Per-exe: another allowlisted app is not blocked by webex's ledger entry.
        Assert.True(CallDetectionPolicy.Decide(Started("Zoom"),
            Snap(lastOfferedAt: offered, now: Now + TimeSpan.FromSeconds(1))).Offer);
    }

    [Fact]
    public void ExeKey_trims_lowercases_and_strips_a_single_exe_suffix()
    {
        Assert.Equal("webex", CallDetectionPolicy.ExeKey("  WebEx.EXE "));
        Assert.Equal("ms-teams", CallDetectionPolicy.ExeKey("ms-teams"));
        Assert.Equal("ciscocollabhost", CallDetectionPolicy.ExeKey("CiscoCollabHost.exe"));
        Assert.Equal("app.exe", CallDetectionPolicy.ExeKey("app.exe.exe"));   // only the final suffix strips
    }

    [Fact]
    public void Every_ignore_carries_a_reason_and_offers_carry_none()
    {
        Assert.Null(CallDetectionPolicy.Decide(Started("webex"), Snap()).IgnoreReason);
        Assert.NotNull(CallDetectionPolicy.Decide(Started("chrome"), Snap()).IgnoreReason);
        Assert.NotNull(CallDetectionPolicy.Decide(Started("webex"), Snap(enabled: false)).IgnoreReason);
        Assert.NotNull(CallDetectionPolicy.Decide(Started("webex"), Snap(recording: true)).IgnoreReason);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~CallDetectionPolicy" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\` — expected: `error CS0246: The type or namespace name 'CallDetectionSnapshot' could not be found` (plus CS0103 on `CallDetectionPolicy`).
- [ ] **Create the policy.** New file `src\LocalScribe.Core\Live\CallDetectionPolicy.cs`:
```csharp
namespace LocalScribe.Core.Live;

/// <summary>Everything the offer decision needs, captured by the caller at decision time (design
/// 2026-07-18 section 5.2): master toggle, exe allowlist (Settings spelling, ".exe" tolerated),
/// own PID (LocalScribe's mic capture must never self-offer), recording-active + console-armed
/// suppression inputs, the per-exe last-offered ledger (ExeKey-keyed), and the clock.</summary>
public sealed record CallDetectionSnapshot(
    bool Enabled,
    IReadOnlyList<string> Allowlist,
    int OwnPid,
    bool RecordingActive,
    bool ConsoleArmed,
    IReadOnlyDictionary<string, DateTimeOffset> LastOfferedAt,
    DateTimeOffset Now);

/// <summary>Offer or Ignore(reason). The reason is diagnostics-only text - it is never rendered
/// to the user and never logged with any transcript content.</summary>
public sealed record CallDetectionDecision(bool Offer, string? IgnoreReason)
{
    public static readonly CallDetectionDecision Offered = new(true, null);
    public static CallDetectionDecision Ignore(string reason) => new(false, reason);
}

/// <summary>PURE offer policy for the call-detection advisory (design 2026-07-18 section 5.2).
/// ADVISORY-ONLY by locked rule: this returns a value and touches nothing - starting a recording
/// stays a human click on the offer toast, which runs the same manual-start command path as any
/// other Start. Holds no state; the caller owns the cooldown ledger.</summary>
public static class CallDetectionPolicy
{
    /// <summary>Per-exe re-offer cooldown (Steno's MIC_NOTIFICATION_DEBOUNCE_MS).</summary>
    public static readonly TimeSpan OfferCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Canonical allowlist/ledger key: trim, strip ONE trailing ".exe" (any case), then
    /// lower-invariant. WASAPI session images arrive EXTENSIONLESS (Process.ProcessName) while the
    /// Settings defaults keep the design's exe-name spelling - both forms must meet, both ways.
    /// Shared by the policy, the coordinator's ledger, and CallEndAdvisor's watched-set so the
    /// three can never disagree on identity.</summary>
    public static string ExeKey(string exe)
    {
        string k = exe.Trim();
        if (k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) k = k[..^4];
        return k.ToLowerInvariant();
    }

    public static CallDetectionDecision Decide(CallAppActivity activity, CallDetectionSnapshot snap)
    {
        if (activity.Kind != CallAppActivityKind.Started)
            return CallDetectionDecision.Ignore("not a session start");
        if (!snap.Enabled)
            return CallDetectionDecision.Ignore("call detection is off");
        if (activity.Pid == snap.OwnPid)
            return CallDetectionDecision.Ignore("own process");
        string key = ExeKey(activity.Exe);
        if (!snap.Allowlist.Any(a => ExeKey(a) == key))
            return CallDetectionDecision.Ignore("not in the allowlist");
        if (snap.RecordingActive)
            return CallDetectionDecision.Ignore("a recording session is active");
        if (snap.ConsoleArmed)
            return CallDetectionDecision.Ignore("the Record console is already open");
        if (snap.LastOfferedAt.TryGetValue(key, out var last) && snap.Now - last < OfferCooldown)
            return CallDetectionDecision.Ignore("per-app cooldown");
        return CallDetectionDecision.Offered;
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 8 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Live/CallDetectionPolicy.cs tests/LocalScribe.Core.Tests/CallDetectionPolicyTests.cs
git commit -m "feat(core): pure CallDetectionPolicy - allowlist, suppression, 60s per-exe cooldown

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Core — `CallEndAdvisor` (3 s debounce state machine)
**Files:**
- New `src\LocalScribe.Core\Live\CallEndAdvisor.cs`.
- New test `tests\LocalScribe.Core.Tests\CallEndAdvisorTests.cs`.

**Interfaces:**
- Produces `public sealed class CallEndAdvisor` with `public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);` and four members:
  - `public void Arm(string? perProcessApp, IReadOnlyCollection<string> activeCaptureExes, IReadOnlyList<string> allowlist)` — called at Idle→Recording. Watched set (ExeKey form): the applied per-process target when there is one, else the allowlist ∩ the capture exes live at that moment (Auto/system-mix sessions). Empty watched set = no call-end advisory this session (nothing to watch — honest silence, no guessing).
  - `public void Observe(CallAppActivity a)` — a watched exe's `Stopped` removes it from the live set; when the live set empties, the quiet clock starts at `a.Timestamp`; a watched `Started` re-adds and CANCELS a pending quiet window (design §5.4: session-return within the window cancels). Non-watched exes are ignored.
  - `public bool ShouldAdvise(DateTimeOffset now)` — true exactly once per arm, when a quiet window has lasted `>= Debounce`; false before, false again after (one-shot — one call, one advisory).
  - `public void Disarm()` — recording left; clears everything.
- Consumes: `CallAppActivity` (Task 2), `CallDetectionPolicy.ExeKey` (Task 3). Design §5.4 signal note (Steno-verified, encoded in the doc comment + smoke): Zoom/Teams/Webex **in-call software mute keeps the OS capture stream open** — no `Stopped` event fires, so mute can never trigger this. The advisor DECIDES only; stopping stays a human click. It never writes markers.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\CallEndAdvisorTests.cs`:
```csharp
using LocalScribe.Core.Live;

public class CallEndAdvisorTests
{
    // Design 2026-07-18 section 5.4: recorded target's capture session goes inactive -> 3 s
    // debounce -> end-advisory DECISION (the toast + any stopping stay human actions in the App).
    // In-call software mute never fires this by the nature of the signal: the OS capture stream
    // stays open, so no Stopped event ever reaches Observe (verified in the Task 8 smoke).

    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] Defaults =
        ["CiscoCollabHost.exe", "webex.exe", "ms-teams.exe", "Zoom.exe"];

    private static CallAppActivity Started(string exe, DateTimeOffset at)
        => new(exe, 100, CallAppActivityKind.Started, at);
    private static CallAppActivity Stopped(string exe, DateTimeOffset at)
        => new(exe, 100, CallAppActivityKind.Stopped, at);

    [Fact]
    public void Per_process_target_stop_advises_once_after_the_debounce()
    {
        var advisor = new CallEndAdvisor();
        advisor.Arm("CiscoCollabHost", new[] { "CiscoCollabHost" }, Defaults);
        advisor.Observe(Stopped("CiscoCollabHost", T0));
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(2.9)));   // inside the window
        Assert.True(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(3)));      // boundary: advise
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(10)));    // one-shot per arm
    }

    [Fact]
    public void Session_return_within_the_window_cancels_and_a_later_stop_rearms()
    {
        var advisor = new CallEndAdvisor();
        advisor.Arm("webex", new[] { "webex" }, Defaults);
        advisor.Observe(Stopped("webex", T0));
        advisor.Observe(Started("webex", T0 + TimeSpan.FromSeconds(1)));      // brief blip, call continues
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(10)));
        advisor.Observe(Stopped("webex", T0 + TimeSpan.FromSeconds(20)));     // the real hang-up
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(22)));
        Assert.True(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(23)));
    }

    [Fact]
    public void Auto_arm_watches_only_allowlisted_active_exes()
    {
        // An Auto/system-mix session has no per-process target: watch the allowlisted apps that
        // were live at record start. A browser's capture session ending is not a call signal.
        var advisor = new CallEndAdvisor();
        advisor.Arm(null, new[] { "CiscoCollabHost", "chrome" }, Defaults);
        advisor.Observe(Stopped("chrome", T0));
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(5)));     // not watched
        advisor.Observe(Stopped("CiscoCollabHost", T0 + TimeSpan.FromSeconds(6)));
        Assert.True(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void All_watched_exes_must_be_quiet_before_the_clock_starts()
    {
        var advisor = new CallEndAdvisor();
        advisor.Arm(null, new[] { "CiscoCollabHost", "Zoom" }, Defaults);
        advisor.Observe(Stopped("Zoom", T0));
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(10)));    // Webex still live
        advisor.Observe(Stopped("CiscoCollabHost", T0 + TimeSpan.FromSeconds(12)));
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(14)));    // clock started at 12s
        Assert.True(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void Exe_matching_strips_extension_and_case_between_settings_and_images()
    {
        // Arm with the SETTINGS spelling, observe the SCANNER spelling.
        var advisor = new CallEndAdvisor();
        advisor.Arm("webex.exe", Array.Empty<string>(), Defaults);
        advisor.Observe(Stopped("WEBEX", T0));
        Assert.True(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Disarm_clears_a_pending_window_and_empty_watch_never_advises()
    {
        var advisor = new CallEndAdvisor();
        advisor.Arm("Zoom", new[] { "Zoom" }, Defaults);
        advisor.Observe(Stopped("Zoom", T0));
        advisor.Disarm();                                                     // recording ended first
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(10)));

        var idle = new CallEndAdvisor();
        idle.Arm(null, new[] { "chrome" }, Defaults);                         // nothing allowlisted live
        idle.Observe(Stopped("chrome", T0));
        Assert.False(idle.ShouldAdvise(T0 + TimeSpan.FromSeconds(10)));       // honest silence
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~CallEndAdvisor" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\` — expected: `error CS0246: The type or namespace name 'CallEndAdvisor' could not be found`.
- [ ] **Create the advisor.** New file `src\LocalScribe.Core\Live\CallEndAdvisor.cs`:
```csharp
namespace LocalScribe.Core.Live;

/// <summary>Call-end advisory state machine (design 2026-07-18 section 5.4). Armed at
/// Idle-&gt;Recording with the exes to watch; a watched capture session going inactive starts a 3 s
/// quiet window (a return within the window cancels - Steno-verified: Zoom/Teams/Webex in-call
/// software mute keeps the OS capture stream open, so mute never even produces a Stopped event);
/// ShouldAdvise turns true exactly once per arm when the window elapses. ADVISORY-ONLY by locked
/// rule: this class DECIDES - it never stops, pauses, or pads anything and never writes markers;
/// the App renders a toast whose [Stop recording] is a human click through the normal Stop
/// command. Pure state + explicit timestamps, so every transition is unit-testable; the clock
/// ticks arrive from the same 1.5 s poll that feeds the watcher (two ticks span the window).
/// Single-threaded by contract (UI-thread timer + State subscription).</summary>
public sealed class CallEndAdvisor
{
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly HashSet<string> _watched = new(StringComparer.Ordinal);   // ExeKey form
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);      // watched exes currently active
    private DateTimeOffset? _quietSince;
    private bool _advised;

    /// <summary>Idle-&gt;Recording. Watch the applied per-process target when there is one; else
    /// (Auto/system mix) the allowlisted apps live on capture endpoints right now. An empty
    /// watched set means no call-end advisory this session - honest silence over guessing.</summary>
    public void Arm(string? perProcessApp, IReadOnlyCollection<string> activeCaptureExes,
        IReadOnlyList<string> allowlist)
    {
        Disarm();
        if (!string.IsNullOrEmpty(perProcessApp))
        {
            _watched.Add(CallDetectionPolicy.ExeKey(perProcessApp));
        }
        else
        {
            foreach (var exe in activeCaptureExes)
                if (allowlist.Any(a => CallDetectionPolicy.ExeKey(a) == CallDetectionPolicy.ExeKey(exe)))
                    _watched.Add(CallDetectionPolicy.ExeKey(exe));
        }
        foreach (var exe in activeCaptureExes)
            if (_watched.Contains(CallDetectionPolicy.ExeKey(exe)))
                _live.Add(CallDetectionPolicy.ExeKey(exe));
    }

    /// <summary>Recording left (stop, fault, finalize) - nothing pending survives.</summary>
    public void Disarm()
    {
        _watched.Clear();
        _live.Clear();
        _quietSince = null;
        _advised = false;
    }

    public void Observe(CallAppActivity a)
    {
        if (_watched.Count == 0 || _advised) return;
        string key = CallDetectionPolicy.ExeKey(a.Exe);
        if (!_watched.Contains(key)) return;
        if (a.Kind == CallAppActivityKind.Started)
        {
            _live.Add(key);
            _quietSince = null;                 // session returned inside the window: cancel
        }
        else
        {
            _live.Remove(key);
            if (_live.Count == 0 && _quietSince is null)
                _quietSince = a.Timestamp;      // ALL watched apps quiet: the window opens
        }
    }

    /// <summary>One-shot per arm: true when a quiet window has lasted the full debounce.</summary>
    public bool ShouldAdvise(DateTimeOffset now)
    {
        if (_advised || _quietSince is not { } quiet) return false;
        if (now - quiet < Debounce) return false;
        _advised = true;
        return true;
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 6 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Live/CallEndAdvisor.cs tests/LocalScribe.Core.Tests/CallEndAdvisorTests.cs
git commit -m "feat(core): CallEndAdvisor - 3s debounced call-end decision, return-cancels, one-shot

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: App — `CallDetectionCoordinator` (testable glue)
**Files:**
- New `src\LocalScribe.App\Services\CallDetectionCoordinator.cs`.
- New test `tests\LocalScribe.App.Tests\CallDetectionCoordinatorTests.cs`.

**Interfaces:**
- Produces `public sealed class CallDetectionCoordinator` (WPF-free; single-threaded by contract — every call arrives on the UI thread from the Task 8 timer + State subscription):
  - ctor `(Func<CallDetectSetting> setting, Func<bool> recordingActive, Func<bool> consoleArmed, int ownPid, TimeProvider time)`.
  - `public event Action<string>? OfferRequested;` — the detected exe image, raised only on a policy `Offer`; the ledger records the offer time under `ExeKey` first.
  - `public event Action? CallEndAdvised;` — the advisor's one-shot, gated on `recordingActive()` at tick time.
  - `public void OnActivity(CallAppActivity activity)` — feeds the advisor's `Observe`, then runs `CallDetectionPolicy.Decide` over a snapshot assembled from the live seams.
  - `public void OnTick()` — debounce-expiry check (`ShouldAdvise(now)`), every poll tick.
  - `public void OnRecordingStarted(string? perProcessApp, IReadOnlyCollection<string> activeCaptureExes)` / `public void OnRecordingStopped()` — advisor `Arm`/`Disarm` pass-throughs (allowlist from the live setting).
- ADVISORY-ONLY restated: the coordinator raises events and NOTHING else — it holds no reference to the controller, the session VM, or any command, so it structurally cannot start/stop/pause/gate/mark anything.
- Consumes: `CallDetectSetting` (Task 1), `CallAppActivity`/`CallDetectionPolicy`/`CallDetectionSnapshot`/`CallEndAdvisor` (Tasks 2–4), `System.TimeProvider`. `ManualUtcTimeProvider` is already `<Compile Include>`-linked into App.Tests (`LocalScribe.App.Tests.csproj` — verified).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\CallDetectionCoordinatorTests.cs`:
```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class CallDetectionCoordinatorTests
{
    // Design 2026-07-18 sections 5.2-5.4 glue: the coordinator assembles policy snapshots from
    // live seams, owns the per-exe offer ledger, and arms/disarms the call-end advisor. It raises
    // events ONLY - by construction it cannot start/stop/pause/gate/mark anything (locked rule).

    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 10, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public CallDetectSetting Setting = new();
        public bool Recording;
        public bool ConsoleArmed;
        public readonly ManualUtcTimeProvider Time = new(T0);
        public readonly List<string> Offers = new();
        public int EndAdvised;
        public readonly CallDetectionCoordinator Coordinator;

        public Harness()
        {
            Coordinator = new CallDetectionCoordinator(() => Setting, () => Recording,
                () => ConsoleArmed, ownPid: 4242, Time);
            Coordinator.OfferRequested += Offers.Add;
            Coordinator.CallEndAdvised += () => EndAdvised++;
        }
    }

    private static CallAppActivity Started(string exe, int pid, DateTimeOffset at)
        => new(exe, pid, CallAppActivityKind.Started, at);
    private static CallAppActivity Stopped(string exe, int pid, DateTimeOffset at)
        => new(exe, pid, CallAppActivityKind.Stopped, at);

    [Fact]
    public void Allowlisted_start_offers_once_then_the_ledger_cooldown_suppresses()
    {
        var h = new Harness();
        h.Coordinator.OnActivity(Started("CiscoCollabHost", 100, T0));
        Assert.Equal(new[] { "CiscoCollabHost" }, h.Offers);

        h.Time.Set(T0 + TimeSpan.FromSeconds(30));
        h.Coordinator.OnActivity(Started("CiscoCollabHost", 101, h.Time.GetUtcNow()));   // new pid, same exe
        Assert.Single(h.Offers);                                    // inside the 60 s cooldown

        h.Time.Set(T0 + TimeSpan.FromSeconds(61));
        h.Coordinator.OnActivity(Started("CiscoCollabHost", 102, h.Time.GetUtcNow()));
        Assert.Equal(2, h.Offers.Count);                            // cooldown elapsed: re-offer
    }

    [Fact]
    public void Recording_console_armed_and_toggle_off_all_suppress_offers()
    {
        var h = new Harness();
        h.Recording = true;
        h.Coordinator.OnActivity(Started("webex", 100, T0));
        h.Recording = false;
        h.ConsoleArmed = true;
        h.Coordinator.OnActivity(Started("webex", 100, T0));
        h.ConsoleArmed = false;
        h.Setting = h.Setting with { Enabled = false };
        h.Coordinator.OnActivity(Started("webex", 100, T0));
        Assert.Empty(h.Offers);

        h.Setting = h.Setting with { Enabled = true };              // live toggle: next event offers
        h.Coordinator.OnActivity(Started("webex", 100, T0));
        Assert.Equal(new[] { "webex" }, h.Offers);
    }

    [Fact]
    public void Own_pid_never_offers()
    {
        var h = new Harness();
        h.Coordinator.OnActivity(Started("webex", 4242, T0));       // our own capture session
        Assert.Empty(h.Offers);
    }

    [Fact]
    public void End_advice_fires_once_after_the_debounce_only_while_recording()
    {
        var h = new Harness();
        h.Recording = true;
        h.Coordinator.OnRecordingStarted("CiscoCollabHost", Array.Empty<string>());
        h.Coordinator.OnActivity(Stopped("CiscoCollabHost", 100, T0));
        h.Time.Set(T0 + TimeSpan.FromSeconds(1.5));
        h.Coordinator.OnTick();
        Assert.Equal(0, h.EndAdvised);                              // window still open
        h.Time.Set(T0 + TimeSpan.FromSeconds(3));
        h.Coordinator.OnTick();
        Assert.Equal(1, h.EndAdvised);                              // debounce elapsed
        h.Coordinator.OnTick();
        Assert.Equal(1, h.EndAdvised);                              // one-shot per arm
    }

    [Fact]
    public void Session_return_cancels_and_recording_stop_disarms()
    {
        var h = new Harness();
        h.Recording = true;
        h.Coordinator.OnRecordingStarted("webex", Array.Empty<string>());
        h.Coordinator.OnActivity(Stopped("webex", 100, T0));
        h.Coordinator.OnActivity(Started("webex", 100, T0 + TimeSpan.FromSeconds(1)));   // blip
        h.Time.Set(T0 + TimeSpan.FromSeconds(10));
        h.Coordinator.OnTick();
        Assert.Equal(0, h.EndAdvised);

        h.Coordinator.OnActivity(Stopped("webex", 100, h.Time.GetUtcNow()));
        h.Recording = false;
        h.Coordinator.OnRecordingStopped();                          // user stopped first
        h.Time.Set(T0 + TimeSpan.FromSeconds(20));
        h.Coordinator.OnTick();
        Assert.Equal(0, h.EndAdvised);                               // disarmed: nothing pending
    }

    [Fact]
    public void Auto_mode_arms_from_the_allowlisted_active_exes()
    {
        var h = new Harness();
        h.Recording = true;
        h.Coordinator.OnRecordingStarted(null, new[] { "CiscoCollabHost", "chrome" });
        h.Coordinator.OnActivity(Stopped("chrome", 300, T0));        // not allowlisted: not watched
        h.Time.Set(T0 + TimeSpan.FromSeconds(5));
        h.Coordinator.OnTick();
        Assert.Equal(0, h.EndAdvised);
        h.Coordinator.OnActivity(Stopped("CiscoCollabHost", 100, h.Time.GetUtcNow()));
        h.Time.Set(T0 + TimeSpan.FromSeconds(8));
        h.Coordinator.OnTick();
        Assert.Equal(1, h.EndAdvised);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~CallDetectionCoordinator" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\` — expected: `error CS0246: The type or namespace name 'CallDetectionCoordinator' could not be found`.
- [ ] **Create the coordinator.** New file `src\LocalScribe.App\Services\CallDetectionCoordinator.cs`:
```csharp
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;

namespace LocalScribe.App.Services;

/// <summary>Glue between the Core watcher/policy/advisor and the App's advisory toasts (design
/// 2026-07-18 sections 5.2-5.4). Assembles CallDetectionSnapshots from live seams, owns the
/// per-exe offer ledger (60 s cooldown, recorded only on actual offers), and arms/disarms the
/// call-end advisor across the recording lifecycle. ADVISORY-ONLY by locked rule and by
/// CONSTRUCTION: it raises OfferRequested/CallEndAdvised and nothing else - it holds no
/// controller, session VM, or command reference, so it structurally cannot start, stop, pause,
/// gate, or mark anything. WPF-free; single-threaded by contract (every call arrives on the UI
/// thread from the 1.5 s poll timer and the State subscription in App.xaml.cs).</summary>
public sealed class CallDetectionCoordinator
{
    private readonly Func<CallDetectSetting> _setting;
    private readonly Func<bool> _recordingActive;
    private readonly Func<bool> _consoleArmed;
    private readonly int _ownPid;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _lastOfferedAt = new(StringComparer.Ordinal);
    private readonly CallEndAdvisor _endAdvisor = new();

    public CallDetectionCoordinator(Func<CallDetectSetting> setting, Func<bool> recordingActive,
        Func<bool> consoleArmed, int ownPid, TimeProvider time)
        => (_setting, _recordingActive, _consoleArmed, _ownPid, _time)
            = (setting, recordingActive, consoleArmed, ownPid, time);

    /// <summary>An offer decision for the given exe image (already policy-approved and
    /// ledger-recorded). The subscriber shows the offer toast; ignoring it does nothing, ever.</summary>
    public event Action<string>? OfferRequested;

    /// <summary>The one-shot call-end advisory decision (3 s quiet window elapsed while a
    /// recording session is active). The subscriber shows the stop?-toast; recording continues
    /// until a human clicks Stop.</summary>
    public event Action? CallEndAdvised;

    public void OnActivity(CallAppActivity activity)
    {
        _endAdvisor.Observe(activity);
        var s = _setting();
        var decision = CallDetectionPolicy.Decide(activity, new CallDetectionSnapshot(
            s.Enabled, s.Apps, _ownPid, _recordingActive(), _consoleArmed(),
            _lastOfferedAt, _time.GetUtcNow()));
        if (!decision.Offer) return;
        _lastOfferedAt[CallDetectionPolicy.ExeKey(activity.Exe)] = _time.GetUtcNow();
        OfferRequested?.Invoke(activity.Exe);
    }

    /// <summary>Debounce-expiry check, every poll tick. Gated on recordingActive so a stopped
    /// recording can never trail a late end-advisory toast.</summary>
    public void OnTick()
    {
        if (_recordingActive() && _endAdvisor.ShouldAdvise(_time.GetUtcNow()))
            CallEndAdvised?.Invoke();
    }

    /// <summary>Idle-&gt;Recording: arm the advisor with the applied per-process target (else the
    /// allowlisted capture apps live right now - the watcher's ActiveExes snapshot).</summary>
    public void OnRecordingStarted(string? perProcessApp, IReadOnlyCollection<string> activeCaptureExes)
        => _endAdvisor.Arm(perProcessApp, activeCaptureExes, _setting().Apps);

    public void OnRecordingStopped() => _endAdvisor.Disarm();
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 6 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.App/Services/CallDetectionCoordinator.cs tests/LocalScribe.App.Tests/CallDetectionCoordinatorTests.cs
git commit -m "feat(app): CallDetectionCoordinator - events-only glue over policy, ledger, end advisor

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: App — `RecordingConsoleViewModel.ApplyDetectedTarget` + `TrayIconHost.IsLiveViewVisible`
**Files:**
- Modify `src\LocalScribe.App\ViewModels\RecordingConsoleViewModel.cs` (one method after `OptionFor`, which ends at line 193, before the `PreflightLine` doc comment at line 195).
- Modify `src\LocalScribe.App\TrayIconHost.cs` (one property after `OpenLiveView`, lines 110–115).
- Test `tests\LocalScribe.App.Tests\RecordingConsoleViewModelTests.cs` (one `[Fact]` reusing the in-file `MakeConsole`/`Auto` helpers — the `await session.StartCommand.ExecuteAsync(null)` / `StopCommand` pattern is precedented at lines 140/145).

**Interfaces:**
- Produces:
  - `public void RecordingConsoleViewModel.ApplyDetectedTarget(string exe)` — the offer toast's hand-off: routes through the SAME `SelectedRemoteTarget` setter a manual pick uses (which mirrors into `RemoteTargetOverride` — the seam `SessionController`/capture planning resolve at Start via `CompositionRoot`'s wrapped `Func<Settings>`), reusing the private `OptionFor` (which synthesizes an option for an unknown image). No-op unless `Session.State == SessionState.Idle` — a live session's target is never yanked by a background detection (defense-in-depth; the policy already suppresses offers while recording). Never starts anything itself.
  - `public bool TrayIconHost.IsLiveViewVisible => _liveView?.IsVisible == true;` — the policy's console-armed input. Correct because the live view is a hide-on-close singleton (`LiveViewWindow.OnClosing` cancels + `Hide()`, verified), so `IsVisible` is false whenever the console is not on screen. Not unit-tested (WPF window state); consumed through a `Func<bool>` seam that the coordinator tests already fake.
- Consumes: existing `OptionFor`, `SelectedRemoteTarget` setter, `SessionState` (Core.Live), `_liveView` field (`TrayIconHost.cs:27`).

Steps:
- [ ] **Write the failing test.** Append inside `RecordingConsoleViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\RecordingConsoleViewModelTests.cs`:
```csharp
    [Fact]
    public async Task ApplyDetectedTarget_selects_and_arms_the_override_only_while_idle()
    {
        // Design 2026-07-18 section 5.3: the offer toast's [Start recording] applies the detected
        // app through the SAME picker path a manual click uses, so the override seam and the
        // console UI can never disagree. exe arrives as the extensionless capture image - the
        // RemoteSetting.App convention already used by the render-side picker.
        var (console, _, session, over, _, _, _) = MakeConsole(Auto(null));
        console.ApplyDetectedTarget("CiscoCollabHost");
        Assert.Equal(RemoteMode.PerProcess, console.SelectedRemoteTarget.Setting.Mode);
        Assert.Equal("CiscoCollabHost", console.SelectedRemoteTarget.Setting.App);
        Assert.Equal("CiscoCollabHost", over.Override?.App);        // the seam Start resolves through

        // An image with no picker entry still applies (OptionFor synthesizes the option).
        console.ApplyDetectedTarget("SomeNewCallApp");
        Assert.Equal("SomeNewCallApp", over.Override?.App);

        // Defense-in-depth: while not Idle the call is a no-op - a live session's target is
        // never yanked by a background detection (the live hot-swap stays the picker's own
        // confirm-gated ChangeRemoteTargetCommand path).
        await session.StartCommand.ExecuteAsync(null);
        console.ApplyDetectedTarget("Zoom");
        Assert.Equal("SomeNewCallApp", over.Override?.App);
        Assert.Equal("SomeNewCallApp", console.SelectedRemoteTarget.Setting.App);
        await session.StopCommand.ExecuteAsync(null);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ApplyDetectedTarget" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\` — expected: `error CS1061: 'RecordingConsoleViewModel' does not contain a definition for 'ApplyDetectedTarget'`.
- [ ] **Add the console method.** In `RecordingConsoleViewModel.cs`, `OptionFor` currently ends (lines 192–193):
```csharp
        return RemoteTargetOptions.First(o => o.Setting.Mode == RemoteMode.Auto);
    }
```
Immediately after that closing brace (before the `PreflightLine` doc comment) insert:
```csharp

    /// <summary>One-click hand-off from the call-detect offer toast (design 2026-07-18 section
    /// 5.3): selects the detected app through the SAME SelectedRemoteTarget setter a manual pick
    /// uses, which mirrors into RemoteTargetOverride - so Start adopts it exactly like a user
    /// click and the console UI shows the applied target. ADVISORY-ONLY locked rule: this never
    /// starts anything itself (the toast action invokes the normal StartCommand separately).
    /// No-op unless Idle - a live session's target is never yanked by a background detection
    /// (the live hot-swap stays the confirm-gated ChangeRemoteTargetCommand path).</summary>
    public void ApplyDetectedTarget(string exe)
    {
        if (Session.State != SessionState.Idle) return;
        SelectedRemoteTarget = OptionFor(new RemoteSetting { Mode = RemoteMode.PerProcess, App = exe });
    }
```
- [ ] **Add the tray property.** In `TrayIconHost.cs`, `OpenLiveView` currently reads (lines 110–115):
```csharp
    public void OpenLiveView()
    {
        _liveView ??= new LiveViewWindow(_session, _lines, _console, _settingsService);
        _liveView.Show();
        _liveView.Activate();
    }
```
Immediately after its closing brace insert:
```csharp

    /// <summary>True while the Record console is on screen (the live view is a hide-on-close
    /// singleton, so IsVisible is authoritative). The call-detect policy's console-armed
    /// suppression input (design 2026-07-18 section 5.2): with the console already open the user
    /// is mid-flow toward Start - an offer toast would only duplicate it.</summary>
    public bool IsLiveViewVisible => _liveView?.IsVisible == true;
```
- [ ] **Run tests and see PASS.** Same filter — expected: 1 passed. Then run the whole console suite to prove no regression (the ctor and every existing picker path are untouched): `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~RecordingConsole" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\`.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs src/LocalScribe.App/TrayIconHost.cs tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs
git commit -m "feat(app): ApplyDetectedTarget via the picker's own path + tray IsLiveViewVisible

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: App — Settings section (master toggle + allowlist editor)
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs` (using; ctor init after line 103 `Vocabulary.Load(_settings.Current.Vocabulary);`; a new region after the Privacy region, i.e. after line 382 `+ "Transcript text is redacted from logs by default (logging arrives in Stage 7).";` and before the `// ---------- App ----------` comment at line 384).
- Modify `src\LocalScribe.App\SettingsPage.xaml` (new `ui:Card` between the Privacy card's `</ui:Card>` at line 146 and the Custom-vocabulary card at line 148).
- Test `tests\LocalScribe.App.Tests\SettingsPageViewModelTests.cs` (four `[Fact]`s; the file already has `using LocalScribe.Core.Live;` and `using LocalScribe.Core.Model;` and the in-file `MakeVm`/`_settings` helpers).

**Interfaces:**
- Produces on `SettingsPageViewModel` (all auto-save through the existing `Commit`/`CommitAsync`/`LastSave` chain — no Save button, the house pattern):
  - `public bool CallDetectEnabled { get; set; }` — `Commit(s => s with { CallDetect = s.CallDetect with { Enabled = value } })`.
  - `public ObservableCollection<string> CallDetectApps { get; }` (seeded from `Current.CallDetect.Apps` in the ctor), `[ObservableProperty] string _newCallDetectApp = ""`, `public IRelayCommand AddCallDetectAppCommand`, `public IRelayCommand<string> RemoveCallDetectAppCommand`, `public IRelayCommand ResetCallDetectAppsCommand`, `public string CallDetectNote`. Add trims, ignores whitespace, and dedups via `CallDetectionPolicy.ExeKey` (the policy's own identity — "WEBEX" vs "webex.exe" is one entry); Reset restores `new CallDetectSetting().Apps` (single-sourced defaults, no copy to drift).
  - The reflection test pinning dropped surfaces (`Vm_exposes_no_dropped_setting_surfaces`, line 241–249) stays green: no new property name contains "AutoDetect"/"Hotkey"/"RecordingIndicator".
- Consumes: `CallDetectSetting` (Task 1), `CallDetectionPolicy.ExeKey` (Task 3, via the existing `using LocalScribe.Core.Live;` at line 5), the existing `Commit` chain. New using: `System.Collections.ObjectModel`.

Steps:
- [ ] **Write the failing tests.** Append inside `SettingsPageViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\SettingsPageViewModelTests.cs`:
```csharp
    [Fact]
    public void Call_detect_surface_seeds_from_current_settings()
    {
        var vm = MakeVm();
        Assert.True(vm.CallDetectEnabled);                          // design 5.2: default ON
        Assert.Equal(new[] { "CiscoCollabHost.exe", "webex.exe", "ms-teams.exe", "Zoom.exe" },
            vm.CallDetectApps);
        Assert.Contains("advisory", vm.CallDetectNote, StringComparison.OrdinalIgnoreCase);

        var off = MakeVm(new Settings { CallDetect = new CallDetectSetting { Enabled = false } });
        Assert.False(off.CallDetectEnabled);
    }

    [Fact]
    public async Task Call_detect_toggle_commits_without_touching_the_apps()
    {
        var vm = MakeVm();
        vm.CallDetectEnabled = false;
        await vm.LastSave;
        Assert.False(_settings.Current.CallDetect.Enabled);
        Assert.Equal(4, _settings.Current.CallDetect.Apps.Count);
    }

    [Fact]
    public async Task Call_detect_add_trims_dedups_by_exe_key_and_persists()
    {
        var vm = MakeVm();
        vm.NewCallDetectApp = "  discord.exe ";
        vm.AddCallDetectAppCommand.Execute(null);
        await vm.LastSave;
        Assert.Contains("discord.exe", vm.CallDetectApps);
        Assert.Contains("discord.exe", _settings.Current.CallDetect.Apps);
        Assert.Equal("", vm.NewCallDetectApp);                      // box clears after add

        vm.NewCallDetectApp = "DISCORD";                            // same app, scanner spelling
        vm.AddCallDetectAppCommand.Execute(null);
        await vm.LastSave;
        Assert.Equal(1, vm.CallDetectApps.Count(a => CallDetectionPolicy.ExeKey(a) == "discord"));
        Assert.Equal(5, vm.CallDetectApps.Count);                   // 4 defaults + discord, once

        vm.NewCallDetectApp = "   ";
        vm.AddCallDetectAppCommand.Execute(null);
        Assert.Equal(5, vm.CallDetectApps.Count);                   // whitespace adds nothing
    }

    [Fact]
    public async Task Call_detect_remove_and_reset_persist()
    {
        var vm = MakeVm();
        vm.RemoveCallDetectAppCommand.Execute("webex.exe");
        await vm.LastSave;
        Assert.DoesNotContain("webex.exe", _settings.Current.CallDetect.Apps);
        Assert.Equal(3, _settings.Current.CallDetect.Apps.Count);

        vm.ResetCallDetectAppsCommand.Execute(null);
        await vm.LastSave;
        Assert.Equal(new CallDetectSetting().Apps, _settings.Current.CallDetect.Apps);
        Assert.Equal(4, vm.CallDetectApps.Count);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~Call_detect" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\` — expected: `error CS1061: 'SettingsPageViewModel' does not contain a definition for 'CallDetectEnabled'` (plus CS1061 on the collection/commands).
- [ ] **Add the using.** In `SettingsPageViewModel.cs` the using block currently opens:
```csharp
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
```
Insert `using System.Collections.ObjectModel;` as the FIRST line (before `using System.IO;` — alphabetical within the System group).
- [ ] **Seed in the ctor.** The ctor currently ends (lines 101–104):
```csharp
        Vocabulary = new VocabularyEditorViewModel(
            (v, _) => { Commit(s => s with { Vocabulary = v }); return LastSave; }, errors);
        Vocabulary.Load(_settings.Current.Vocabulary);
    }
```
Replace with:
```csharp
        Vocabulary = new VocabularyEditorViewModel(
            (v, _) => { Commit(s => s with { Vocabulary = v }); return LastSave; }, errors);
        Vocabulary.Load(_settings.Current.Vocabulary);

        // Call detection (design 2026-07-18 section 5.2): seed the editable allowlist and wire
        // the editor commands. Every mutation auto-saves through the same Commit chain.
        CallDetectApps = new ObservableCollection<string>(_settings.Current.CallDetect.Apps);
        AddCallDetectAppCommand = new RelayCommand(AddCallDetectApp);
        RemoveCallDetectAppCommand = new RelayCommand<string>(RemoveCallDetectApp);
        ResetCallDetectAppsCommand = new RelayCommand(ResetCallDetectApps);
    }
```
- [ ] **Add the section.** In `SettingsPageViewModel.cs`, the Privacy region currently ends (lines 381–382):
```csharp
    public string LoggingRedactionNote { get; } =
        "Transcript text is redacted from logs by default (logging arrives in Stage 7).";
```
Immediately after (before the `// ---------- App ----------` comment) insert:
```csharp

    // ---------- Call detection (design 2026-07-18 section 5.2: ADVISORY-ONLY, locked) ----------
    public string CallDetectNote { get; } =
        "When a listed app starts using the microphone, LocalScribe shows an offer toast. "
        + "Detection is advisory-only: it never starts or stops a recording by itself, and "
        + "ignoring the offer does nothing.";

    public bool CallDetectEnabled
    {
        get => _settings.Current.CallDetect.Enabled;
        set
        {
            Commit(s => s with { CallDetect = s.CallDetect with { Enabled = value } });
            OnPropertyChanged();
        }
    }

    /// <summary>The editable exe allowlist ("webex.exe" spelling; matching is case-insensitive
    /// and extension-tolerant via CallDetectionPolicy.ExeKey). Seeded from settings in the ctor;
    /// each add/remove/reset commits the whole list.</summary>
    public ObservableCollection<string> CallDetectApps { get; }

    [ObservableProperty] private string _newCallDetectApp = "";

    public IRelayCommand AddCallDetectAppCommand { get; }
    public IRelayCommand<string> RemoveCallDetectAppCommand { get; }
    public IRelayCommand ResetCallDetectAppsCommand { get; }

    private void AddCallDetectApp()
    {
        string exe = NewCallDetectApp.Trim();
        if (exe.Length == 0) return;
        // Dedup with the policy's own identity: "WEBEX" and "webex.exe" are one entry, so the
        // list can never hold two spellings that the matcher treats as the same app.
        if (CallDetectApps.Any(a => CallDetectionPolicy.ExeKey(a) == CallDetectionPolicy.ExeKey(exe)))
        {
            NewCallDetectApp = "";
            return;
        }
        CallDetectApps.Add(exe);
        CommitCallDetectApps();
        NewCallDetectApp = "";
    }

    private void RemoveCallDetectApp(string? exe)
    {
        if (exe is null || !CallDetectApps.Remove(exe)) return;
        CommitCallDetectApps();
    }

    private void ResetCallDetectApps()
    {
        CallDetectApps.Clear();
        foreach (string a in new CallDetectSetting().Apps)   // single-sourced defaults (Task 1)
            CallDetectApps.Add(a);
        CommitCallDetectApps();
    }

    private void CommitCallDetectApps()
        => Commit(s => s with { CallDetect = s.CallDetect with { Apps = CallDetectApps.ToList() } });
```
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed. Then run the whole class (the reflection ban test `Vm_exposes_no_dropped_setting_surfaces` must stay green): `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SettingsPageViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\`.
- [ ] **Add the XAML card.** In `src\LocalScribe.App\SettingsPage.xaml`, between the Privacy card's closing tag (line 146 `</ui:Card>`) and the Custom-vocabulary card (line 148 `<ui:Card Style="{StaticResource SectionCard}">`), insert:
```xml

            <ui:Card Style="{StaticResource SectionCard}">
                <StackPanel>
                    <TextBlock Text="Call detection" FontWeight="SemiBold" Margin="0,0,0,8" />
                    <CheckBox Content="Offer to record when a call app starts using the microphone"
                              IsChecked="{Binding CallDetectEnabled}" Margin="0,4,0,4" />
                    <TextBlock Text="{Binding CallDetectNote, Mode=OneWay}" Style="{StaticResource Note}"
                               TextWrapping="Wrap" />
                    <TextBlock Text="Watched apps" FontWeight="SemiBold" Margin="0,8,0,4" />
                    <ItemsControl ItemsSource="{Binding CallDetectApps}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <StackPanel Orientation="Horizontal" Margin="0,0,0,2">
                                    <TextBlock Text="{Binding}" VerticalAlignment="Center" MinWidth="220" />
                                    <Button Content="Remove"
                                            Command="{Binding DataContext.RemoveCallDetectAppCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                            CommandParameter="{Binding}" />
                                </StackPanel>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                    <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                        <TextBox MinWidth="220"
                                 Text="{Binding NewCallDetectApp, UpdateSourceTrigger=PropertyChanged}" />
                        <Button Content="Add" Margin="8,0,0,0"
                                Command="{Binding AddCallDetectAppCommand}" />
                        <Button Content="Reset to defaults" Margin="8,0,0,0"
                                Command="{Binding ResetCallDetectAppsCommand}" />
                    </StackPanel>
                </StackPanel>
            </ui:Card>
```
(The markup mirrors the Custom-vocabulary Terms editor two cards down — same ItemsControl/RelativeSource command pattern, theme resources only, no hardcoded brushes, so `XamlHygieneTests` stays green.)
- [ ] **Build clean.** `dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\` — expected: 0 warnings, 0 errors.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs src/LocalScribe.App/SettingsPage.xaml tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs
git commit -m "feat(app): Settings call-detection section - master toggle + allowlist editor with reset

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: App — startup wiring, offer + call-end toasts, shutdown; final gate + manual smoke
**Files:**
- Modify `src\LocalScribe.App\App.xaml.cs` only (four edits below; anchors @ 82546aa — `feat/deep-link` also edits this file, so re-verify every quoted block first).
- No new unit test (pure composition — every decision path underneath is covered by Tasks 2–7; the precedent is the app-mute Task-8 wiring commit). The gate is: 0-warning build + full App + Core suites + the manual smoke below.

**Interfaces:**
- Consumes: `WasapiSessionScanner(DataFlow.Capture)` + `CallActivityWatcher` (Task 2), `CallDetectionCoordinator` (Task 5), `console.ApplyDetectedTarget` + `_tray.IsLiveViewVisible` (Task 6), `Settings.CallDetect` (Task 1), `AdvisoryToastWindow`/`ToastAction` (deep-link CONTRACT, namespace `LocalScribe.App.Views`), `AppKindResolver.FriendlyName` (toast title), `comp.RemoteOverride.Apply(comp.Settings.Current).Remote` (the APPLIED per-session remote setting — the exact expression `RecordingConsoleViewModel.RefreshRemoteTargetsAsync` uses at line 242), `session.StartCommand`/`StopCommand` (the SAME manual-start/stop command path the tray menu binds — consent flow unchanged), the existing `session.PropertyChanged` State-transition pattern (lines 489–497), the existing ApplicationIdle deferred block (pump-up gotcha), `Environment.ProcessId`.
- Produces: one `_callDetectTimer` field; watcher+coordinator wiring; the two toasts. The 1.5 s timer starts INSIDE the ApplicationIdle block, so no toast can ever be constructed before the message pump is up, and it checks the master toggle LIVE on every tick (disabled tick = no scan at all + `Reset()`).

Steps:
- [ ] **Edit 1 — field.** The fields currently read (lines 16–20):
```csharp
    private System.Windows.Threading.DispatcherTimer? _timer;
    // Task 8: separate 2 s timer driving the advisory app-mute tray poll (design 2026-07-11 2.2).
    // Its Poll() is inert until Recording and fail-open, so it may start alongside _timer.
    private System.Windows.Threading.DispatcherTimer? _appMuteTimer;
    private readonly CancellationTokenSource _shutdownCts = new();
```
Replace with:
```csharp
    private System.Windows.Threading.DispatcherTimer? _timer;
    // Task 8: separate 2 s timer driving the advisory app-mute tray poll (design 2026-07-11 2.2).
    // Its Poll() is inert until Recording and fail-open, so it may start alongside _timer.
    private System.Windows.Threading.DispatcherTimer? _appMuteTimer;
    // Call-detect advisory (design 2026-07-18 section 5): the 1.5 s capture-session poll.
    // Started only inside the ApplicationIdle block - after the message pump is up - so an offer
    // toast can never be constructed pre-pump; advisory-only and fail-open like _appMuteTimer.
    private System.Windows.Threading.DispatcherTimer? _callDetectTimer;
    private readonly CancellationTokenSource _shutdownCts = new();
```
- [ ] **Edit 2 — watcher, coordinator, toasts, lifecycle.** The console-open-on-Start block currently reads (lines 487–497):
```csharp
        // Stage 5.4 Phase 3 (design section 6): ANY Start - nav rail, console, or tray - opens the
        // Record console; the overlay pill already follows State via OverlayViewModel.IsVisible.
        // Idle->Recording only: a Resume (Paused->Recording) must not re-activate/steal focus.
        var lastState = LocalScribe.Core.Live.SessionState.Idle;
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ViewModels.SessionViewModel.State)) return;
            if (lastState == LocalScribe.Core.Live.SessionState.Idle
                && session.State == LocalScribe.Core.Live.SessionState.Recording)
                _tray?.OpenLiveView();
            lastState = session.State;
        };
```
Immediately AFTER that block's closing `};` insert:
```csharp

        // Call-detect advisory (design 2026-07-18 section 5). LOCKED rules restated: ADVISORY-
        // ONLY - never auto-start/auto-stop/auto-pause, never writes markers, never gates or
        // delays capture; the consent flow is unchanged (the toast's Start runs the SAME command
        // path as the tray/console Start button); FAIL-OPEN - watcher errors skip a tick and can
        // never affect capture. The watcher polls ACTIVE capture-endpoint sessions (an
        // allowlisted app opening the mic = a call starting) and diffs; the pure policy decides
        // Offer/Ignore; toasts are the ONLY output. The timer itself starts in the
        // ApplicationIdle block below (post-pump).
        var callWatcher = new LocalScribe.Core.Live.CallActivityWatcher(
            new LocalScribe.Core.Live.WasapiSessionScanner(NAudio.CoreAudioApi.DataFlow.Capture),
            TimeProvider.System);
        var callDetect = new Services.CallDetectionCoordinator(
            () => comp.Settings.Current.CallDetect,
            recordingActive: () => comp.Controller.State != LocalScribe.Core.Live.SessionState.Idle,
            consoleArmed: () => _tray?.IsLiveViewVisible == true,
            ownPid: Environment.ProcessId, TimeProvider.System);
        callWatcher.Activity += callDetect.OnActivity;
        callDetect.OfferRequested += exe => dispatch(() =>
        {
            string friendly = LocalScribe.Core.Live.AppKindResolver.FriendlyName(exe) ?? exe;
            new Views.AdvisoryToastWindow($"Call detected - {friendly}",
                "Start a LocalScribe recording of this call?",
                new Views.ToastAction[]
                {
                    new("Start recording", () =>
                    {
                        // The SAME manual-start path as any other Start: console opens, the
                        // detected app lands via the RemoteTargetOverride seam (through the
                        // picker's own setter), StartCommand runs with its normal gates and the
                        // consent flow exactly as configured. Nothing here bypasses capture
                        // planning or writes anything.
                        _tray?.OpenLiveView();
                        console.ApplyDetectedTarget(exe);
                        if (session.StartCommand.CanExecute(null)) session.StartCommand.Execute(null);
                    }),
                    new("Dismiss", () => { }),      // ignore = nothing, ever (design 5.3)
                }, autoDismissSeconds: 15).Show();
        });
        callDetect.CallEndAdvised += () => dispatch(() =>
        {
            new Views.AdvisoryToastWindow("Call appears to have ended - stop recording?",
                "The call app's microphone session went quiet. Recording continues until you stop it.",
                new Views.ToastAction[]
                {
                    new("Stop recording", () =>
                    {
                        // A HUMAN click through the normal Stop command - never automatic
                        // (locked rule); pad-to-session-end and finalize behave exactly as a
                        // console/tray Stop.
                        if (session.StopCommand.CanExecute(null)) session.StopCommand.Execute(null);
                    }),
                    new("Keep recording", () => { }),
                }, autoDismissSeconds: 15).Show();
        });
        // Call-end arming rides the same Idle->Recording transition pattern as the console-open
        // block above (separate subscription, separate lastState - neither handler can perturb
        // the other). Watch the APPLIED per-process target (the same RemoteOverride.Apply
        // expression the console's pre-flight line resolves), else the allowlisted apps live on
        // capture endpoints right now; Idle disarms.
        var callLastState = LocalScribe.Core.Live.SessionState.Idle;
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ViewModels.SessionViewModel.State)) return;
            if (callLastState == LocalScribe.Core.Live.SessionState.Idle
                && session.State == LocalScribe.Core.Live.SessionState.Recording)
            {
                var applied = comp.RemoteOverride.Apply(comp.Settings.Current).Remote;
                callDetect.OnRecordingStarted(
                    applied.Mode == LocalScribe.Core.Model.RemoteMode.PerProcess ? applied.App : null,
                    callWatcher.ActiveExes);
            }
            else if (session.State == LocalScribe.Core.Live.SessionState.Idle)
            {
                callDetect.OnRecordingStopped();
            }
            callLastState = session.State;
        };
```
- [ ] **Edit 3 — start the poll post-pump.** The ApplicationIdle block currently reads (lines 529–537):
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
            // Call-detect poll starts only NOW - the message pump is live, so an offer toast can
            // never be constructed pre-pump (the project's startup-rendering gotcha; the toast is
            // a plain Window by contract, but the rule costs nothing to honor here). The master
            // toggle is respected LIVE: a disabled tick does no scan at all and clears the diff
            // baseline, so re-enabling starts fresh.
            _callDetectTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(1500) };
            _callDetectTimer.Tick += (_, _) =>
            {
                if (!comp.Settings.Current.CallDetect.Enabled) { callWatcher.Reset(); return; }
                callWatcher.Poll();
                callDetect.OnTick();
            };
            _callDetectTimer.Start();
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
```
- [ ] **Edit 4 — shutdown.** `OnExit` currently reads (lines 572–580):
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
        _callDetectTimer?.Stop();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
```
- [ ] **Build 0-warning + full App/Core suites green.** Run:
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\call-detect\
```
Expected: build 0 warnings; App suite fully green (incl. `XamlHygieneTests`); Core suite green except the 2 known pre-existing fixture failures.
- [ ] **Manual smoke (WPF/hardware — not unit-testable).** Launch the app, then:
  1. **Webex capture-owner verification (binding smoke item, Global Constraints):** join a real Webex call, watch which image the offer toast names (or check Windows Settings > Privacy & security > Microphone "currently in use"). If the mic-capture owner is NOT `CiscoCollabHost` (e.g. a different Cisco helper exe), add the real owner to `CallDetectSetting`'s defaults in a follow-up `fix(core)` commit and re-run the Task 1 tests updated to match.
  2. **Offer:** with the app idle and the console closed, join a call → within ~3 s the toast "Call detected - Webex" appears bottom-right, does NOT steal focus from the call, and auto-dismisses after 15 s if ignored. Ignoring changes nothing anywhere (no markers, no session, no settings).
  3. **[Start recording]:** click it → the Record console opens with the detected app selected in the Remote-target picker, and recording starts through the normal path (status header, overlay pill, tray icon all behave exactly like a manual Start).
  4. **Suppression:** while recording, have a second allowlisted app open the mic → no offer. Stop, open the console (idle), trigger again → no offer while the console is open. Close the console, re-trigger within 60 s of the last offer → no offer (cooldown); after 60 s → offers again.
  5. **In-call mute must NOT fire the end advisory (design 5.4, Steno-verified premise):** while recording a call, mute yourself with the app's own mute button for >10 s → NO "call ended" toast.
  6. **Call end:** leave the call while recording → after ~3-4.5 s (3 s debounce + poll granularity) the "Call appears to have ended - stop recording?" toast appears; click **[Keep recording]** → recording continues untouched. Repeat; click **[Stop recording]** → normal stop + finalize. Verify the transcript contains NO detection-related markers in either case.
  7. **Settings:** toggle "Offer to record..." off → no offers on a new call (and Task Manager shows no scan churn); toggle back on → offers resume without a restart. Add/remove an app and Reset to defaults; restart the app → the list persisted.
  8. **Themes:** flip Windows light/dark → the Settings card and both toasts stay readable.
- [ ] **Commit.**
```
git add src/LocalScribe.App/App.xaml.cs
git commit -m "feat(app): call-detect advisory wiring - 1.5s post-pump poll, offer + call-end toasts

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-review

**(a) Spec coverage — every design §5 sub-item (plus the binding §1 rules) maps to tasks:**
- §5.1 `CallActivityWatcher` (1.5 s poll of ACTIVE capture-endpoint sessions across capture devices, PID → image, diff → `CallAppActivity { Exe, Pid, Started|Stopped, Timestamp }`, fail-open like `TrayMuteSignalSource`, enumeration behind an injectable seam so the poller/diff is fully unit-tested) → **Task 2**. The 1.5 s constant lives in the Task 8 timer (the watcher itself is externally polled, the `AppMuteWatcher` lifecycle pattern); the seam is the EXISTING `IAudioSessionScanner` over the SAME NAudio walk the capture stack already uses (`WasapiSessionScanner`, now direction-parameterized — "CoreAudio API already used by the capture stack", verified: `MMDeviceEnumerator`/`AudioSessionManager` via NAudio.CoreAudioApi).
- §5.2 `CallDetectionPolicy` (pure; master toggle default ON; case-insensitive allowlist with the four defaults, browsers excluded-but-addable; own process excluded; suppressed while recording or console armed; 60 s per-exe cooldown) → **Task 3** (every input in the snapshot record, every branch tested incl. the 60 s boundary and `.exe`/extensionless folding) + **Task 1** (the Settings-editable toggle + list) + **Task 5** (the ledger that feeds `LastOfferedAt`, recorded only on real offers) + **Task 6** (`IsLiveViewVisible` = the console-armed input; the hide-on-close singleton makes `IsVisible` authoritative).
- §5.3 offer toast (no-activate plain Topmost window bottom-right, post-pump only, "Call detected — \<App>", [Start recording] [Dismiss], 15 s, ignore = nothing; Start → normal start path with the detected app via the `RemoteAppOverride` seam, consent unchanged, console opens) → **Task 8** (toast construction via the deep-link CONTRACT — window behavior is the contract's responsibility, consumed not redefined; timer started inside ApplicationIdle = the pump-up gotcha honored) + **Task 6** (`ApplyDetectedTarget` through the picker's own `SelectedRemoteTarget` setter → `RemoteTargetOverride` — the widened successor of the design's "RemoteAppOverride" name, per `RemoteTargetOverride.cs`'s own doc comment).
- §5.4 call-end advisory (recording-only; watched app's capture session inactive → 3 s debounce, return-cancels; toast [Stop recording] [Keep recording]; never auto-stop/auto-pause; in-call mute never fires by the nature of the signal) → **Task 4** (pure state machine: boundary at exactly 3 s, one-shot per arm, all-watched-quiet rule, per-process + auto-intersection arming, disarm) + **Task 5** (recording-gated tick expiry, arm/disarm lifecycle) + **Task 8** (toast; Stop = a human click through `session.StopCommand`).
- Settings UI (master toggle + allowlist editor add/remove/reset, existing page pattern) → **Task 7** (auto-save `Commit` chain like every field; ItemsControl editor mirroring the vocabulary card; `ExeKey`-deduped adds; defaults single-sourced from `new CallDetectSetting()`).
- §1 locked rules: restated in Global Constraints AND enforced structurally — the coordinator holds no controller/command reference (Task 5), the policy and advisor return values only (Tasks 3–4), the watcher never touches capture (Task 2), no task edits `SessionController`, capture legs, `StartAsync`, or any CanExecute gate, and nothing anywhere writes a marker. Consent: the toast Start executes the same `StartCommand` the tray menu binds. Fail-open: scanner throw → trace + skip tick + keep baseline (tested).
- Smoke: real-Webex capture-owner verification, no-activate check, in-call-mute negative test, cooldown, toggle-live, persistence — all in Task 8's runbook, matching design §8's smoke list for this branch.

**(b) Placeholder scan:** no TBD / "similar to Task N" / elided bodies anywhere — every step carries full test code, full implementation code, and quotes the exact current code being replaced (grounded @ 82546aa: `Settings.cs` 38/49, `WasapiSessionScanner.cs` 15–21, `RecordingConsoleViewModel.cs` 192–195, `TrayIconHost.cs` 110–115, `SettingsPageViewModel.cs` 1–2/101–104/381–384, `SettingsPage.xaml` 146–148, `App.xaml.cs` 16–20/487–497/529–537/572–580; the round brief's `7605606` is a superseded pre-amend twin of the same docs commit — quoted context is what binds, and Global Constraints orders re-verification after the three earlier branches merge). Every run command names its exact filter, the isolated BaseOutputPath, and the expected failure/pass output.
**(c) Type consistency across tasks:** `AudioSessionInfo(uint Pid, string ProcessName)` (existing) → the watcher casts to the contract's `int Pid` in `CallAppActivity(string Exe, int Pid, CallAppActivityKind Kind, DateTimeOffset Timestamp)` (Task 2) → consumed by `CallDetectionPolicy.Decide(CallAppActivity, CallDetectionSnapshot)` (Task 3), `CallEndAdvisor.Observe(CallAppActivity)` (Task 4), and `CallDetectionCoordinator.OnActivity(CallAppActivity)` (Task 5). `CallDetectionSnapshot.LastOfferedAt : IReadOnlyDictionary<string, DateTimeOffset>` is satisfied by the coordinator's `Dictionary`. `CallDetectSetting` (Task 1) flows `Func<CallDetectSetting>` into the coordinator and `s.CallDetect with { ... }` commits in Task 7; `Apps : IReadOnlyList<string>` matches both the snapshot's `Allowlist` and `Arm`'s `allowlist` parameter, and `CallDetectApps.ToList()` satisfies the init-only property. `ExeKey` is the single identity for the policy match, the cooldown ledger, the advisor's watched-set, and the Settings dedup — four call sites, one definition. `watcher.ActiveExes : IReadOnlyCollection<string>` matches `OnRecordingStarted`'s parameter; `applied.App : string?` matches the `string? perProcessApp`. All new members tests touch are `public` (no InternalsVisibleTo — verified); `ManualUtcTimeProvider` is reachable from BOTH suites (Core.Tests native + the `<Compile Include>` link in `LocalScribe.App.Tests.csproj` — verified); Core test files carry no namespace and App test files use `LocalScribe.App.Tests`, matching each project's convention. The Task 8 wiring uses only fully-qualified names (the file's own style), so no using changes are needed there; `NAudio.CoreAudioApi.DataFlow` is visible to App.xaml.cs because NAudio is a Core dependency and the App project references Core. All good.
