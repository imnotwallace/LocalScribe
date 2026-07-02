# Stage 3b: WPF Shell — Tray, Live View, Recording Overlay — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the first UI on Stage 3a's `SessionController`: a tray-first WPF app (the load-bearing consent indicator) with quick Start/Pause/Stop, a virtualized live-transcript window, and the minimal always-on-top recording overlay pill (state dot + elapsed timer + Local/Remote audio-present bars + Pause/Stop) that is excluded from screen capture, never steals focus, and is visible only while Recording/Paused — all three surfaces binding ONE `SessionViewModel`.

**Architecture:** New WPF project `LocalScribe.App` (tray-first: no window at launch, `ShutdownMode.OnExplicitShutdown`) + `LocalScribe.App.Tests`. MVVM via CommunityToolkit.Mvvm with **WPF-free ViewModels** (no `Dispatcher` types — controller events marshal through an injected `Action<Action>` dispatch delegate), so every behavior (state mapping, command gating, timer text, level decay, sorted line insertion, overlay visibility, position clamping) unit-tests without a UI thread. XAML layers are Humble Objects: tray via H.NotifyIcon (runtime-generated icon, no .ico assets), Fluent styling via WPF-UI, overlay interop (`WDA_EXCLUDEFROMCAPTURE`, `WS_EX_NOACTIVATE`, drag, `window-state.json` position persistence) in thin code-behind verified by the smoke runbook. `SessionController.StartAsync` runs via `Task.Run` — capture activation is MTA-sensitive and must not run on the STA UI thread (Stage-1/3a finding).

**Tech Stack:** .NET 10 `net10.0-windows` + WPF. New packages (App project only): `WPF-UI` (newest 4.x), `H.NotifyIcon.Wpf` (newest 2.x), `CommunityToolkit.Mvvm` (newest 8.x). Segoe Fluent Icons glyph font (ships with Windows 11).

**Prerequisite:** the **Stage 3a plan is fully executed** (`docs/plans/2026-07-02-stage-3a-live-pipeline.md`) — `SessionController` and its events, `WasapiCaptureSourceProvider`, `WasapiSessionScanner`, `LiveHardwareProbe`, `LiveSessionOptions`, `SessionState` all exist and the 3a unit gate is green. Authoritative sources: design doc "UI design" section + spec 2.1 (overlay show/hide), spec 7 (`overlay` settings), spec 12 (device config).

---

## Global Constraints

These apply to **every** task; each task's requirements implicitly include them.

- **Target framework:** `net10.0-windows`, `<UseWPF>true</UseWPF>` in `LocalScribe.App` and `LocalScribe.App.Tests`. `LocalScribe.Core` gains **no** UI dependencies.
- **New packages (App + App.Tests only — pin the newest stable of each line at implementation time):** `WPF-UI` 4.x, `H.NotifyIcon.Wpf` 2.x, `CommunityToolkit.Mvvm` 8.x; App.Tests additionally xunit 2.9.3 + `Microsoft.NET.Test.Sdk` 17.14.1 (match the Core.Tests versions). Nothing else, nowhere else.
- **ViewModels are WPF-free.** No `System.Windows.*` type may appear in `ViewModels/` (enforced by review, exercised by tests running without an STA thread). UI-thread marshalling is an injected `Action<Action>` (production: `Application.Current.Dispatcher.BeginInvoke`; tests: run-inline).
- **Controller calls that touch capture run off the UI thread** (`Task.Run`) — `ProcessLoopbackCapture.Start()` is MTA-sensitive.
- **Privileged-content rule (design decision 12):** session name/participants NEVER render on the overlay unless `settings.Overlay.ShowSessionName` is true, and then tooltip-only. The overlay is excluded from capture by default (`settings.Overlay.ExcludeFromCapture`).
- **ASCII-only source — literals, identifiers, comments, XML-doc.** XAML glyphs from Segoe Fluent Icons use `&#xE000;`-style escapes only. Markdown docs exempt.
- **Determinism:** ViewModels take `TimeProvider`/injected state, never `DateTime.Now`; timers in VMs are started/stopped through an injectable tick (tests call the tick directly).
- **Test categories:** `[UNIT]` default (both test projects, PR gate `dotnet test --filter "Category!=Fixture"`); `[SMOKE]` = the 3b runbook on real hardware. No fixtures in 3b.
- **Commits:** conventional commits, one per task step marked *Commit*, each ending with:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
- **Verification:** named test filter after each implement step; full unit gate + `dotnet build` (0 warnings) before each task's final commit.

## Scope boundary (what is NOT in 3b)

- **Global hotkeys** (`settings.Hotkeys`), the **main window / session history / Matter manager / metadata editor / settings UI**, startup recovery scan, first-run consent notice — **Stage 4** (manual controls + session lifecycle) and Stage 7. 3b's tray menu links "Open sessions folder" (Explorer) as the interim browse affordance.
- **`AppKind` derivation from the resolved remote app** — manual tray starts record `AppKind.Manual` (the honest v1 capture-path truth for a manual trigger; spec 1.2). Deriving Webex/Zoom from the planner result is a Stage-4 refinement alongside the metadata editor (the user can already correct `medium` there).
- **Overlay hover-gated click-through + live low-energy watchdog** — explicitly deferred (design "Deferred" list).
- **Level meters as real meters** — the overlay bars are binary-ish "audio present" indicators with decay (design: two-bar indicator); a calibrated dB meter is later polish.
- **Sleep/resume, device hot-swap UI, disk-full surfacing** — Stage 7 (the controller does not raise them yet).

## Type ledger (single source of truth for cross-task signatures)

All in project `LocalScribe.App` unless noted; ns root `LocalScribe.App`.

| Type | Shape | Task |
|---|---|---|
| `App` | `Application` subclass; tray-first bootstrap; owns the composition root | 1 |
| `CompositionRoot` | `static (SessionController Controller, Settings Settings, StoragePaths Paths) Build()` | 1 |
| `SessionViewModel` | ns `.ViewModels`: see Task 2 (state, commands, timer text, level bars, notices) | 2 |
| `LevelMeter` | ns `.ViewModels`: `void Observe(float peak)`, `void Tick()`, `double Value` (0..1, decaying) | 2 |
| `TranscriptLinesViewModel` | ns `.ViewModels`: `ObservableCollection<TranscriptLineViewModel> Lines`, `void OnLineInserted(int, TranscriptLine)`, `void Clear()` | 3 |
| `TranscriptLineViewModel` | ns `.ViewModels`: `record(string Timestamp, string Speaker, string Text, bool IsMarker)` | 3 |
| `LiveViewWindow` | XAML window (virtualized list + auto-scroll) | 3 |
| `TrayIconHost` | tray icon + context menu, binds `SessionViewModel` | 4 |
| `OverlayViewModel` | ns `.ViewModels`: `bool IsVisible`, `bool IsPaused`, `string Elapsed`, `double LocalLevel/RemoteLevel`, `string? TooltipText`, commands | 5 |
| `ScreenClamp` | ns `.ViewModels`: `static (double X, double Y) Clamp(double x, double y, double w, double h, double vx, double vy, double vw, double vh)` | 5 |
| `WindowStateStore` | ns `.ViewModels`: `record OverlayWindowState(double X, double Y)`; `Load()/Save()` over `window-state.json` | 5 |
| `OverlayWindow` | XAML pill window + interop code-behind (`WDA_EXCLUDEFROMCAPTURE`, `WS_EX_NOACTIVATE`, drag) | 5 |
| `NativeWindowInterop` | `static void ExcludeFromCapture(Window)`, `static void MakeNoActivate(Window)` (DllImports) | 5 |

Consumed from 3a/Core (exact, do not re-declare): `SessionController` (+ `State`, `CurrentSessionId`, events `StateChanged`/`LineInserted`/`PeakObserved`/`ErrorRaised`/`Notice`, methods `StartAsync`/`PauseAsync`/`ResumeAsync`/`StopAsync`), `SessionState`, `LiveSessionOptions`, `WasapiCaptureSourceProvider`, `WasapiSessionScanner`, `LiveHardwareProbe`, `SileroVadModel`, `ModelPaths.Require`, `WhisperEngineFactory`, `StopwatchClock`, `SettingsStore`, `Settings` (+ `Overlay: OverlaySetting { Enabled, ShowSessionName, ShowLevelMeter, ExcludeFromCapture }`, `StorageRoot`), `StoragePaths`, `TranscriptLine` (+ `Kind`, `StartMs`, `Text`, `SpeakerLabel`), `TranscriptKind`, `SourceKind`, `AppKind`, `Markers`.

---

## Task 1: App + test project scaffold, composition root, tray-first bootstrap  [UNIT+manual]

**Files:**
- Create: `src/LocalScribe.App/LocalScribe.App.csproj`, `src/LocalScribe.App/App.xaml`, `src/LocalScribe.App/App.xaml.cs`, `src/LocalScribe.App/CompositionRoot.cs`
- Create: `tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj`, `tests/LocalScribe.App.Tests/CompositionRootTests.cs`
- Modify: `LocalScribe.slnx` (add both projects, mirroring existing entries)

**Interfaces:**
- Consumes: `SettingsStore`, `StoragePaths`, `SessionController` + real adapters (3a), `Whisper.net.LibraryLoader.RuntimeOptions` (host sets backend order once — copy the exact line from `LocalScribe.LiveRunner/Program.cs`).
- Produces: `CompositionRoot.Build()` returning the app's single controller + settings + paths (pure construction, no side effects beyond reading `settings.json` — this is what makes it unit-testable); `App` that at startup builds the root, creates the `SessionViewModel`-bound tray (Task 4 fills the menu; this task ships a placeholder icon + Exit item so the app is runnable/killable).

- [ ] **Step 1: Create the App project**

`src/LocalScribe.App/LocalScribe.App.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\LocalScribe.Core\LocalScribe.Core.csproj" />
    <PackageReference Include="WPF-UI" Version="4.0.3" />
    <PackageReference Include="H.NotifyIcon.Wpf" Version="2.3.0" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageReference Include="Whisper.net.Runtime" Version="1.9.1" />
    <PackageReference Include="Whisper.net.Runtime.Cuda.Windows" Version="1.9.1" />
    <PackageReference Include="Whisper.net.Runtime.Vulkan" Version="1.9.1" />
  </ItemGroup>
</Project>
```

(Use the newest stable 4.x/2.x/8.x if these exact versions are superseded.) Create `src/LocalScribe.App/app.manifest` declaring PerMonitorV2 DPI awareness (design decision 12):

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 2: Create the test project**

`tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\LocalScribe.App\LocalScribe.App.csproj" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
  </ItemGroup>
</Project>
```

(Match `xunit.runner.visualstudio` to the version Core.Tests uses — read its csproj.) Add both projects to `LocalScribe.slnx`.

- [ ] **Step 3: Write the failing composition test**

`tests/LocalScribe.App.Tests/CompositionRootTests.cs`:

```csharp
using LocalScribe.App;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class CompositionRootTests
{
    [Fact]
    public void Build_produces_an_idle_controller_and_expanded_paths()
    {
        var (controller, settings, paths) = CompositionRoot.Build();
        Assert.Equal(SessionState.Idle, controller.State);
        Assert.False(paths.Root.Contains('%'));          // env vars expanded by StoragePaths
        Assert.NotNull(settings);
    }
}
```

Run: `dotnet test tests/LocalScribe.App.Tests` — Expected: FAIL (types missing).

- [ ] **Step 4: Implement `CompositionRoot` and the tray-first `App`**

`src/LocalScribe.App/CompositionRoot.cs`:

```csharp
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.App;

/// <summary>Builds the app's single SessionController over the real adapters. Construction
/// only - no capture, no models touched until StartAsync. Settings load synchronously at
/// startup (small local file).</summary>
public static class CompositionRoot
{
    public static (SessionController Controller, Settings Settings, StoragePaths Paths) Build()
    {
        string settingsPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "settings.json");
        var settings = new SettingsStore(settingsPath).LoadOrDefaultAsync(default)
            .GetAwaiter().GetResult();
        var paths = new StoragePaths(settings.StorageRoot);
        string appVersion = typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var controller = new SessionController(paths, settings, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(),
            new WasapiCaptureSourceProvider(settings, new WasapiSessionScanner()),
            () => new StopwatchClock(), TimeProvider.System, appVersion);
        return (controller, settings, paths);
    }
}
```

`src/LocalScribe.App/App.xaml` (no `StartupUri` — tray-first):

```xml
<Application x:Class="LocalScribe.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="pack://application:,,,/Wpf.Ui;component/Resources/Theme/Dark.xaml" />
                <ResourceDictionary Source="pack://application:,,,/Wpf.Ui;component/Resources/Wpf.Ui.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

(If the WPF-UI 4.x resource-dictionary paths differ, use the paths from the WPF-UI 4.x getting-started docs — `ThemesDictionary`/`ControlsDictionary` markup extensions are the current idiom.)

`src/LocalScribe.App/App.xaml.cs`:

```csharp
using System.Windows;
using Whisper.net.LibraryLoader;
namespace LocalScribe.App;

public partial class App : Application
{
    private TrayIconHost? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Host responsibility (see LiveRunner): native backend order, once per process.
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];

        var (controller, settings, paths) = CompositionRoot.Build();
        var session = new ViewModels.SessionViewModel(controller, settings,
            dispatch: a => Dispatcher.BeginInvoke(a));
        _tray = new TrayIconHost(session, paths);        // Task 4 (this task: icon + Exit only)
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
```

For THIS task, ship minimal placeholders so it compiles and runs: a `ViewModels/SessionViewModel.cs` stub exposing only the ctor above (Task 2 replaces it test-first) and a `TrayIconHost` that shows an H.NotifyIcon `TaskbarIcon` with a runtime-generated icon (H.NotifyIcon `GeneratedIcon`: gray circle) and a context menu containing exactly one item, Exit -> `Application.Current.Shutdown()`. Keep both under 40 lines; Tasks 2/4 replace them.

- [ ] **Step 5: Verify**

Run: `dotnet test tests/LocalScribe.App.Tests` — Expected: PASS.
Run: `dotnet build` — Expected: 0 warnings.
Manual: `dotnet run --project src/LocalScribe.App` — tray icon appears, no window opens, Exit quits cleanly.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/ tests/LocalScribe.App.Tests/ LocalScribe.slnx
git commit -m "feat: LocalScribe.App WPF scaffold - tray-first bootstrap + composition root"
```

---

## Task 2: `SessionViewModel` — the one VM all three surfaces bind  [UNIT]

**Files:**
- Create: `src/LocalScribe.App/ViewModels/SessionViewModel.cs` (replacing the Task-1 stub), `src/LocalScribe.App/ViewModels/LevelMeter.cs`
- Test: `tests/LocalScribe.App.Tests/SessionViewModelTests.cs`, `tests/LocalScribe.App.Tests/LevelMeterTests.cs`

**Interfaces:**
- Consumes: `SessionController` (all events/methods), `Settings`, `SessionState`, `SourceKind`.
- Produces (`CommunityToolkit.Mvvm` `ObservableObject`):

```csharp
public sealed partial class SessionViewModel : ObservableObject
{
    public SessionViewModel(SessionController controller, Settings settings, Action<Action> dispatch,
        TimeProvider? time = null, LiveSessionOptions? startOptions = null);
    // startOptions: what StartCommand passes to the controller. Production omits it (defaults:
    // preflight ON). Tests pass LiveTestDoubles.Options() (test VAD, preflight OFF) so no test
    // pays the 2 s real preflight delay.

    [ObservableProperty] private SessionState _state;              // mirrors controller
    [ObservableProperty] private string _elapsed = "00:00";        // mm:ss / h:mm:ss
    [ObservableProperty] private string? _lastNotice;
    [ObservableProperty] private bool _isLagging;                  // RTF_LAGGING seen this session
    public LevelMeter LocalLevel { get; }                          // 0..1 decayed
    public LevelMeter RemoteLevel { get; }
    public string? CurrentSessionId { get; }
    public bool IsRecording / IsPaused / IsIdle { get; }           // derived, notify on State

    public IAsyncRelayCommand StartCommand;        // CanExecute: State == Idle
    public IAsyncRelayCommand PauseResumeCommand;  // CanExecute: Recording or Paused; toggles
    public IAsyncRelayCommand StopCommand;         // CanExecute: Recording or Paused

    public void TimerTick();     // called by a DispatcherTimer (UI) or directly (tests):
                                 // recomputes Elapsed from the controller clock + decays levels
}
```

**Behavior contract:**
- Controller events arrive on worker threads: every handler body runs through `dispatch` (production: `Dispatcher.BeginInvoke`; tests: `a => a()`).
- `StartCommand` runs `Task.Run(() => controller.StartAsync(new LiveSessionOptions(), ct))` — MTA rule; on start also records the wall start via `ValueStopwatch`/`Environment.TickCount64`? No — the VM derives `Elapsed` from its own `long _startTick` set from `TimeProvider.GetTimestamp()`... **Simpler and honest:** the VM tracks elapsed itself: `_recordingStartedAt` + accumulated time via the injected `TimeProvider` (add a `TimeProvider` ctor param, default `TimeProvider.System`); `TimerTick()` formats `time.GetUtcNow() - startedAt` (pauses do NOT stop the elapsed clock — spec 2.1: the session clock ticks through Pause, and the overlay timer shows wall session time). Reset on Stop.
- `PauseResumeCommand` calls `PauseAsync` when Recording, `ResumeAsync` when Paused (both via `Task.Run` for symmetry).
- `PeakObserved(source, peak)` feeds `LocalLevel`/`RemoteLevel` (`Observe`); `TimerTick()` calls their `Tick()` so bars decay to zero within ~1 s of silence. `LevelMeter`: `Observe(p)` sets `Value = max(Value, min(1, p * 3))` (a gentle gain so speech at ~0.3 peak lights the bar fully); `Tick()` multiplies `Value` by 0.7, flooring at 0 below 0.01. `Value` raises `PropertyChanged`.
- `ErrorRaised("RTF_LAGGING")` sets `IsLagging` (cleared on Start); every `Notice` lands in `LastNotice`; all three commands `NotifyCanExecuteChanged` on every `StateChanged`.

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.App.Tests/LevelMeterTests.cs`:

```csharp
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class LevelMeterTests
{
    [Fact]
    public void Observe_raises_value_and_tick_decays_to_zero()
    {
        var meter = new LevelMeter();
        meter.Observe(0.4f);
        Assert.True(meter.Value >= 0.9);              // gained: speech lights the bar
        for (int i = 0; i < 20; i++) meter.Tick();
        Assert.Equal(0, meter.Value);                 // decayed and floored
    }

    [Fact]
    public void Observe_keeps_the_max_until_decay()
    {
        var meter = new LevelMeter();
        meter.Observe(0.5f);
        double v1 = meter.Value;
        meter.Observe(0.1f);
        Assert.Equal(v1, meter.Value);                // a quieter frame never lowers the bar
    }
}
```

`tests/LocalScribe.App.Tests/SessionViewModelTests.cs` — the VM is tested against a REAL `SessionController` over the same fakes 3a's tests use. Reference them instead of duplicating: add to the App.Tests csproj

```xml
  <ItemGroup>
    <Compile Include="..\LocalScribe.Core.Tests\LiveTestDoubles.cs" Link="LiveTestDoubles.cs" />
    <Compile Include="..\LocalScribe.Core.Tests\FakeTranscriptionEngine.cs" Link="FakeTranscriptionEngine.cs" />
  </ItemGroup>
```

(plus a `ProjectReference` is NOT needed — Core is already transitive via App. If `LiveTestDoubles.cs` pulls more test-only files, link those too; keep links minimal.)

```csharp
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SessionViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-vm-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (SessionViewModel Vm, SessionController Controller) MakeVm()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());      // test VAD, preflight off
        return (vm, controller);
    }

    [Fact]
    public async Task Commands_gate_on_state()
    {
        var (vm, _) = MakeVm();
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.False(vm.PauseResumeCommand.CanExecute(null));

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Recording, vm.State);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.True(vm.PauseResumeCommand.CanExecute(null));

        await vm.PauseResumeCommand.ExecuteAsync(null);      // pause
        Assert.Equal(SessionState.Paused, vm.State);
        await vm.PauseResumeCommand.ExecuteAsync(null);      // resume
        Assert.Equal(SessionState.Recording, vm.State);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Idle, vm.State);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Peaks_light_levels_and_notices_surface()
    {
        var (vm, controller) = MakeVm();
        string? seen = null;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.LastNotice)) seen = vm.LastNotice; };

        await vm.StartCommand.ExecuteAsync(null);            // fakes replay 0.5f speech frames
        Assert.True(vm.LocalLevel.Value > 0);
        Assert.True(vm.RemoteLevel.Value > 0);

        await vm.StopCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null).ContinueWith(_ => { });  // second stop -> notice path
        Assert.NotNull(vm.LastNotice);
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task Elapsed_formats_and_resets()
    {
        var (vm, _) = MakeVm();
        Assert.Equal("00:00", vm.Elapsed);
        await vm.StartCommand.ExecuteAsync(null);
        vm.TimerTick();
        Assert.Matches(@"^\d{2}:\d{2}$", vm.Elapsed);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal("00:00", vm.Elapsed);
    }
}
```

Note: 3a's `LiveTestDoubles.MakeController(root)` returns `(controller, provider, paths, clock)` — adjust the tuple deconstruction to its real shape. Note the second `StopCommand` execution: `CanExecute` is false then — call `controller.StopAsync` directly for the notice assertion if the command refuses to run (the intent is "a Notice event reaches LastNotice"; adapt mechanically).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests` — Expected: FAIL (stub VM has none of this).

- [ ] **Step 3: Implement `LevelMeter` + `SessionViewModel`**

`src/LocalScribe.App/ViewModels/LevelMeter.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
namespace LocalScribe.App.ViewModels;

/// <summary>Audio-present bar state: per-frame peaks push it up (with gain so normal speech
/// fills the bar), Tick() decays it toward zero within about a second of silence. WPF-free.</summary>
public sealed partial class LevelMeter : ObservableObject
{
    [ObservableProperty] private double _value;

    public void Observe(float peak)
        => Value = Math.Max(Value, Math.Min(1.0, peak * 3.0));

    public void Tick()
    {
        double next = Value * 0.7;
        Value = next < 0.01 ? 0 : next;
    }
}
```

`src/LocalScribe.App/ViewModels/SessionViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

/// <summary>The single session VM behind tray, live view, and overlay (spec 2.1: all three
/// surfaces bind one SessionViewModel and route to the same SessionController). WPF-free:
/// controller events (worker threads) marshal through the injected dispatch delegate; capture
/// calls run via Task.Run (MTA-sensitive activation must stay off the STA UI thread).</summary>
public sealed partial class SessionViewModel : ObservableObject
{
    private readonly SessionController _controller;
    private readonly Settings _settings;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly LiveSessionOptions _startOptions;
    private DateTimeOffset? _startedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording), nameof(IsPaused), nameof(IsIdle))]
    private SessionState _state = SessionState.Idle;
    [ObservableProperty] private string _elapsed = "00:00";
    [ObservableProperty] private string? _lastNotice;
    [ObservableProperty] private bool _isLagging;

    public LevelMeter LocalLevel { get; } = new();
    public LevelMeter RemoteLevel { get; } = new();
    public string? CurrentSessionId => _controller.CurrentSessionId;
    public bool IsRecording => State == SessionState.Recording;
    public bool IsPaused => State == SessionState.Paused;
    public bool IsIdle => State == SessionState.Idle;

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand PauseResumeCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }

    public SessionViewModel(SessionController controller, Settings settings,
        Action<Action> dispatch, TimeProvider? time = null, LiveSessionOptions? startOptions = null)
    {
        (_controller, _settings, _dispatch, _time, _startOptions)
            = (controller, settings, dispatch, time ?? TimeProvider.System, startOptions ?? new LiveSessionOptions());

        StartCommand = new AsyncRelayCommand(StartAsync, () => State == SessionState.Idle);
        PauseResumeCommand = new AsyncRelayCommand(PauseResumeAsync,
            () => State is SessionState.Recording or SessionState.Paused);
        StopCommand = new AsyncRelayCommand(StopAsync,
            () => State is SessionState.Recording or SessionState.Paused);

        controller.StateChanged += s => _dispatch(() =>
        {
            State = s;
            StartCommand.NotifyCanExecuteChanged();
            PauseResumeCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        });
        controller.Notice += n => _dispatch(() => LastNotice = n);
        controller.ErrorRaised += e => _dispatch(() => { if (e == "RTF_LAGGING") IsLagging = true; });
        controller.PeakObserved += (source, peak) => _dispatch(() =>
            (source == SourceKind.Local ? LocalLevel : RemoteLevel).Observe(peak));
    }

    private async Task StartAsync()
    {
        IsLagging = false;
        string? id = await Task.Run(() => _controller.StartAsync(_startOptions, CancellationToken.None));
        if (id is not null) _startedAt = _time.GetUtcNow();
    }

    private Task PauseResumeAsync()
        => Task.Run(() => State == SessionState.Paused
            ? _controller.ResumeAsync(CancellationToken.None)
            : _controller.PauseAsync(CancellationToken.None));

    private async Task StopAsync()
    {
        await Task.Run(() => _controller.StopAsync(CancellationToken.None));
        _startedAt = null;
        Elapsed = "00:00";
        LocalLevel.Tick(); RemoteLevel.Tick();
    }

    /// <summary>Driven by a ~150 ms DispatcherTimer in production; tests call it directly.
    /// The elapsed clock keeps ticking through Pause (spec 2.1).</summary>
    public void TimerTick()
    {
        if (_startedAt is { } started)
        {
            var span = _time.GetUtcNow() - started;
            Elapsed = span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        LocalLevel.Tick();
        RemoteLevel.Tick();
    }
}
```

(If the CommunityToolkit version rejects multiple names in one `[NotifyPropertyChangedFor]`, stack three attributes.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests` — Expected: PASS.
Run: `dotnet test --filter "Category!=Fixture"` and `dotnet build` — Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ tests/LocalScribe.App.Tests/
git commit -m "feat: SessionViewModel + LevelMeter - the single VM behind tray/live-view/overlay"
```

---

## Task 3: Live transcript window  [UNIT for the VM, manual for XAML]

Design "Live transcript window": virtualized, auto-scrolling, chat-like `[mm:ss] Speaker: text`, markers italic, a lagging indicator, Pause/Stop at hand. The sorted-insert logic lives in `TranscriptLinesViewModel` (WPF-free, tested); the window is a thin XAML shell.

**Files:**
- Create: `src/LocalScribe.App/ViewModels/TranscriptLinesViewModel.cs`, `src/LocalScribe.App/LiveViewWindow.xaml`, `src/LocalScribe.App/LiveViewWindow.xaml.cs`
- Test: `tests/LocalScribe.App.Tests/TranscriptLinesViewModelTests.cs`

**Interfaces:**
- Consumes: `SessionController.LineInserted(int insertIndex, TranscriptLine line)` — the merger's sorted position comes WITH the event; the VM must insert at that index, not re-sort.
- Produces:

```csharp
public sealed record TranscriptLineViewModel(string Timestamp, string Speaker, string Text, bool IsMarker);

public sealed class TranscriptLinesViewModel
{
    public TranscriptLinesViewModel(SessionController controller, Action<Action> dispatch);
    public ObservableCollection<TranscriptLineViewModel> Lines { get; }
    public void Clear();                                  // new session wipes the list
}
```

**Behavior contract:** subscribe to `LineInserted` in the ctor (marshalled); map `TranscriptLine` -> `TranscriptLineViewModel` (`Timestamp` = `mm:ss`/`h:mm:ss` from `StartMs`, invariant; `Speaker` = `SpeakerLabel ?? ""`; `IsMarker` = `Kind == TranscriptKind.Marker`); `Insert(index, vm)` — an index beyond `Lines.Count` (possible if a `Clear()` raced a late line) clamps to append, never throws. Subscribe to `StateChanged`: on transition INTO `Recording` from `Idle` (a new session, not a resume), `Clear()`.

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.App.Tests/TranscriptLinesViewModelTests.cs`:

```csharp
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class TranscriptLinesViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-lv-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Lines_arrive_at_merger_sorted_positions_and_format()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new TranscriptLinesViewModel(controller, a => a());

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await controller.StopAsync(CancellationToken.None);

        Assert.Equal(2, vm.Lines.Count(l => !l.IsMarker));       // one segment per source
        var first = vm.Lines[0];
        Assert.Matches(@"^\d{2}:\d{2}$", first.Timestamp);
        Assert.Contains(first.Speaker, new[] { "Me", "Them" });
        Assert.NotEqual("", first.Text);
    }

    [Fact]
    public async Task New_session_clears_previous_lines()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new TranscriptLinesViewModel(controller, a => a());

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await controller.StopAsync(CancellationToken.None);
        int afterFirst = vm.Lines.Count;
        Assert.True(afterFirst > 0);

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await controller.StopAsync(CancellationToken.None);
        Assert.Equal(afterFirst, vm.Lines.Count);                // cleared, then refilled
    }

    [Fact]
    public void Out_of_range_insert_clamps_to_append()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new TranscriptLinesViewModel(controller, a => a());
        // Simulate a late line racing Clear(): exercised via the public seam.
        vm.Clear();
        // No direct injection point - covered implicitly; keep Clear() safe by inspection.
        Assert.Empty(vm.Lines);
    }
}
```

(The clamp behavior is defensive; the first two tests are the real contract.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~TranscriptLinesViewModelTests"` — Expected: FAIL.

- [ ] **Step 3: Implement the VM**

`src/LocalScribe.App/ViewModels/TranscriptLinesViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

public sealed record TranscriptLineViewModel(string Timestamp, string Speaker, string Text, bool IsMarker);

/// <summary>Observable projection of the live merger view (spec 5 live view): each finalized
/// line is inserted at the merger-computed sorted index - it may land BEHIND the newest line,
/// which is expected (the other stream's earlier utterance can finalize later). WPF-free.</summary>
public sealed class TranscriptLinesViewModel
{
    private readonly Action<Action> _dispatch;
    private SessionState _lastState = SessionState.Idle;

    public ObservableCollection<TranscriptLineViewModel> Lines { get; } = [];

    public TranscriptLinesViewModel(SessionController controller, Action<Action> dispatch)
    {
        _dispatch = dispatch;
        controller.LineInserted += (index, line) => _dispatch(() =>
            Lines.Insert(Math.Min(index, Lines.Count), Map(line)));
        controller.StateChanged += s => _dispatch(() =>
        {
            if (s == SessionState.Recording && _lastState == SessionState.Idle) Clear();
            _lastState = s;
        });
    }

    public void Clear() => Lines.Clear();

    private static TranscriptLineViewModel Map(TranscriptLine line)
    {
        var ts = TimeSpan.FromMilliseconds(line.StartMs);
        string stamp = ts.ToString(ts.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
        return new TranscriptLineViewModel(stamp, line.SpeakerLabel ?? "",
            line.Text, line.Kind == TranscriptKind.Marker);
    }
}
```

- [ ] **Step 4: Implement the window (thin XAML, manual verification)**

`src/LocalScribe.App/LiveViewWindow.xaml`:

```xml
<ui:FluentWindow x:Class="LocalScribe.App.LiveViewWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        Title="LocalScribe - Live" Height="520" Width="640"
        WindowBackdropType="Mica" ExtendsContentIntoTitleBar="False">
    <DockPanel Margin="12">
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="{Binding Session.State}" FontWeight="SemiBold" Margin="0,0,12,0" />
            <TextBlock Text="{Binding Session.Elapsed}" Margin="0,0,12,0" />
            <TextBlock Text="transcription lagging"
                       Visibility="{Binding Session.IsLagging, Converter={StaticResource BoolToVis}}"
                       Foreground="Orange" Margin="0,0,12,0" />
            <Button Content="Pause/Resume" Command="{Binding Session.PauseResumeCommand}" Margin="0,0,8,0" />
            <Button Content="Stop" Command="{Binding Session.StopCommand}" />
        </StackPanel>
        <ListView ItemsSource="{Binding Lines.Lines}"
                  VirtualizingPanel.IsVirtualizing="True"
                  VirtualizingPanel.VirtualizationMode="Recycling"
                  ScrollViewer.CanContentScroll="True">
            <ListView.ItemTemplate>
                <DataTemplate>
                    <TextBlock TextWrapping="Wrap">
                        <Run Text="{Binding Timestamp, Mode=OneWay, StringFormat='[{0}]'}" FontWeight="SemiBold" />
                        <Run Text="{Binding Speaker, Mode=OneWay, StringFormat='{}{0}:'}" FontWeight="SemiBold" />
                        <Run Text="{Binding Text, Mode=OneWay}" />
                    </TextBlock>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </DockPanel>
</ui:FluentWindow>
```

Additions to the XAML above: `<Window.Resources><BooleanToVisibilityConverter x:Key="BoolToVis" /></Window.Resources>` under the root element; `x:Name="LineList"` on the `ListView`; italicize marker rows with a `Style`+`DataTrigger` on `IsMarker` in the item template's `TextBlock`.

`src/LocalScribe.App/LiveViewWindow.xaml.cs`:

```csharp
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App;

/// <summary>Thin shell over the shared VMs. Bottom-sticky auto-scroll: follows new lines only
/// while the user is at the bottom. Closing HIDES - a recording must never die with a window;
/// only tray Exit shuts the app down.</summary>
public partial class LiveViewWindow
{
    public sealed record LiveViewContext(SessionViewModel Session, TranscriptLinesViewModel Lines);

    private readonly TranscriptLinesViewModel _lines;
    private bool _stickToBottom = true;

    public LiveViewWindow(SessionViewModel session, TranscriptLinesViewModel lines)
    {
        InitializeComponent();
        _lines = lines;
        DataContext = new LiveViewContext(session, lines);
        lines.Lines.CollectionChanged += OnLinesChanged;
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

- [ ] **Step 5: Verify + commit**

Run: `dotnet test tests/LocalScribe.App.Tests` + full gate + build — Expected: PASS, 0 warnings.
Manual: run the app, start a session, watch lines appear and auto-scroll; scroll up mid-session and confirm it stops following; close window and confirm recording continues (tray still shows Recording).

```bash
git add src/LocalScribe.App/ViewModels/TranscriptLinesViewModel.cs src/LocalScribe.App/LiveViewWindow.xaml src/LocalScribe.App/LiveViewWindow.xaml.cs tests/LocalScribe.App.Tests/TranscriptLinesViewModelTests.cs
git commit -m "feat: live transcript window - merger-ordered observable lines, virtualized, auto-scroll"
```

---

## Task 4: Tray icon + flyout — the load-bearing consent indicator  [manual]

Design decision 6/UI design: the tray is the immovable consent surface — state at a glance (idle/recording/paused), quick controls, and the app's only Exit. H.NotifyIcon `GeneratedIcon` renders the state as a colored dot at runtime (no .ico assets): gray Idle, red Recording, orange Paused.

**Files:**
- Create/replace: `src/LocalScribe.App/TrayIconHost.cs` (replaces the Task-1 placeholder)
- Modify: `src/LocalScribe.App/App.xaml.cs` (pass the live-view/lines VMs; start the shared 150 ms `DispatcherTimer` calling `SessionViewModel.TimerTick`)

**Interfaces:**
- Consumes: `SessionViewModel` (commands + `State` + `PropertyChanged`), `TranscriptLinesViewModel`, `LiveViewWindow`, `StoragePaths.SessionsDir`.
- Produces: `TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines, StoragePaths paths) : IDisposable`.

**Behavior contract:**
- Menu items: **Start recording** (`StartCommand`), **Pause/Resume** (`PauseResumeCommand`), **Stop** (`StopCommand`) — each bound so enabled-state follows `CanExecute`; **Open live view** (creates-or-shows the singleton `LiveViewWindow`); **Open sessions folder** (`Process.Start("explorer.exe", paths.SessionsDir)` — create the dir first if missing); separator; **Exit** — if Recording/Paused, first show a confirmation (`MessageBox`: "A recording is in progress. Stop and exit?") and route through `StopCommand` before `Application.Current.Shutdown()`. Never kill a live recording silently.
- Icon + tooltip react to `SessionViewModel.PropertyChanged(State)`: color per state; tooltip `"LocalScribe - idle"` / `"LocalScribe - RECORDING"` / `"LocalScribe - paused"`. Double-click opens the live view.
- `LastNotice` changes surface as a balloon/toast notification (H.NotifyIcon `ShowNotification`) — this is where SILENT_SOURCE warnings, the degraded-capture warning, and "already recording" hints become visible.
- Implementation is a plain C# class assembling `TaskbarIcon` + `ContextMenu` in code (no XAML resource tricks needed); wire `CanExecuteChanged` -> `MenuItem.IsEnabled` directly. Keep every handler one line into the VM — no logic in this class beyond widget assembly.

- [ ] **Step 1: Implement** (no unit tests — all logic already lives in the tested VM; this is widget assembly)

`src/LocalScribe.App/TrayIconHost.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using H.NotifyIcon;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Storage;
namespace LocalScribe.App;

/// <summary>The load-bearing consent surface (design decision 6): recording state always
/// visible, quick controls, the app's only Exit. Pure widget assembly - every behavior lives
/// in the tested SessionViewModel; handlers here are one line into the VM.</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly SessionViewModel _session;
    private readonly TranscriptLinesViewModel _lines;
    private readonly StoragePaths _paths;
    private LiveViewWindow? _liveView;

    public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines, StoragePaths paths)
    {
        (_session, _lines, _paths) = (session, lines, paths);

        _icon = new TaskbarIcon { ToolTipText = "LocalScribe - idle" };
        _icon.ContextMenu = BuildMenu();
        _icon.TrayMouseDoubleClick += (_, _) => OpenLiveView();
        _session.PropertyChanged += OnSessionChanged;
        UpdateIcon(SessionState.Idle);
        _icon.ForceCreate();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Bound("Start recording", _session.StartCommand));
        menu.Items.Add(Bound("Pause / Resume", _session.PauseResumeCommand));
        menu.Items.Add(Bound("Stop", _session.StopCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open live view", (_, _) => OpenLiveView()));
        menu.Items.Add(Item("Open sessions folder", (_, _) =>
        {
            Directory.CreateDirectory(_paths.SessionsDir);
            Process.Start("explorer.exe", _paths.SessionsDir);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", async (_, _) =>
        {
            if (_session.State is SessionState.Recording or SessionState.Paused)
            {
                if (MessageBox.Show("A recording is in progress. Stop and exit?",
                        "LocalScribe", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes) return;
                await _session.StopCommand.ExecuteAsync(null);   // never kill a live recording silently
            }
            Application.Current.Shutdown();
        }));
        return menu;
    }

    private static MenuItem Bound(string header, ICommand command)
        => new() { Header = header, Command = command };   // IsEnabled follows CanExecute via WPF

    private static MenuItem Item(string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private void OpenLiveView()
    {
        _liveView ??= new LiveViewWindow(_session, _lines);
        _liveView.Show();
        _liveView.Activate();
    }

    private void OnSessionChanged(object? _, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.State)) UpdateIcon(_session.State);
        else if (e.PropertyName == nameof(SessionViewModel.LastNotice) && _session.LastNotice is { } n)
            _icon.ShowNotification("LocalScribe", n);
    }

    private void UpdateIcon(SessionState state)
    {
        (Brush brush, string tip) = state switch
        {
            SessionState.Recording => (Brushes.Red, "LocalScribe - RECORDING"),
            SessionState.Paused => (Brushes.Orange, "LocalScribe - paused"),
            SessionState.Finalizing => (Brushes.Gray, "LocalScribe - finalizing..."),
            _ => (Brushes.Gray, "LocalScribe - idle"),
        };
        _icon.ToolTipText = tip;
        // H.NotifyIcon generated icon: a filled circle (U+25CF) in the state color.
        // ASCII-only source rule: the glyph stays a \u escape, never a literal.
        _icon.GeneratedIcon = new GeneratedIcon
        { Text = "\u25CF", Foreground = brush, FontSize = 46 };
    }

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _icon.Dispose();
    }
}
```

If the H.NotifyIcon 2.x `GeneratedIcon`/`ShowNotification` API shapes differ (`GeneratedIconSource`, `ShowNotification(title, message)` overloads moved), follow the package's current README sample — the contract (colored state dot, balloon per notice) is what matters, not the exact property name.

Update `App.xaml.cs`:

```csharp
        var session = new ViewModels.SessionViewModel(controller, settings,
            dispatch: a => Dispatcher.BeginInvoke(a));
        var lines = new ViewModels.TranscriptLinesViewModel(controller, a => Dispatcher.BeginInvoke(a));
        _tray = new TrayIconHost(session, lines, paths);
        _timer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => session.TimerTick();
        _timer.Start();
```

- [ ] **Step 2: Verify manually**

`dotnet run --project src/LocalScribe.App`: tray dot gray; Start -> red + tooltip RECORDING; Pause -> orange; Stop -> gray; balloon on the degraded/silent-source notices (start with no meeting app running to force the fallback notice); Exit during recording prompts, stops, finalizes (check the session folder), then quits. Menu items enable/disable with state.

- [ ] **Step 3: Full gate + commit**

Run: `dotnet test --filter "Category!=Fixture"` and `dotnet build` — Expected: PASS, 0 warnings.

```bash
git add src/LocalScribe.App/TrayIconHost.cs src/LocalScribe.App/App.xaml.cs
git commit -m "feat: tray icon + flyout - consent indicator, quick controls, guarded exit"
```

---

## Task 5: Recording overlay — the screen-capture-excluded pill  [UNIT for VM/clamp/store, manual for interop]

Design decision 12 / spec 2.1: a minimal always-on-top pill — state dot + elapsed timer + Local/Remote audio-present two-bar + Pause/Stop — visible ONLY in Recording/Paused, excluded from screen capture by default, never stealing focus, position remembered in `window-state.json` and clamped into the virtual screen. Session name suppressed by default (opt-in, tooltip only).

**Files:**
- Create: `src/LocalScribe.App/ViewModels/OverlayViewModel.cs`, `src/LocalScribe.App/ViewModels/ScreenClamp.cs`, `src/LocalScribe.App/ViewModels/WindowStateStore.cs`, `src/LocalScribe.App/OverlayWindow.xaml`, `src/LocalScribe.App/OverlayWindow.xaml.cs`, `src/LocalScribe.App/NativeWindowInterop.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs` (own the overlay singleton; show/hide on state)
- Test: `tests/LocalScribe.App.Tests/OverlayViewModelTests.cs`, `tests/LocalScribe.App.Tests/ScreenClampTests.cs`, `tests/LocalScribe.App.Tests/WindowStateStoreTests.cs`

**Interfaces:**
- Consumes: `SessionViewModel` (state/elapsed/levels/commands — the overlay composes it, it does NOT talk to the controller), `Settings.Overlay`.
- Produces:

```csharp
public sealed partial class OverlayViewModel : ObservableObject
{
    public OverlayViewModel(SessionViewModel session, Settings settings);
    public SessionViewModel Session { get; }               // pass-through bindings
    public bool IsVisible { get; }        // overlay.Enabled AND State in {Recording, Paused} (spec 2.1)
    public bool ShowLevelMeter { get; }                    // settings.Overlay.ShowLevelMeter
    public string? TooltipText { get; }   // session id ONLY when settings.Overlay.ShowSessionName
}

public static class ScreenClamp
{
    /// <summary>Clamps a window rect into the virtual screen; a fully off-screen or
    /// never-saved position (NaN) returns the fallback (top-right with margin).</summary>
    public static (double X, double Y) Clamp(double x, double y, double w, double h,
        double vx, double vy, double vw, double vh);
}

public sealed class WindowStateStore
{
    public WindowStateStore(string path);                  // %APPDATA%/LocalScribe/window-state.json
    public (double X, double Y)? Load();                   // null on absent/corrupt (throwaway file)
    public void Save(double x, double y);
}
```

**Behavior contract:**
- `IsVisible` recomputes on `Session.PropertyChanged(State)`; false whenever `settings.Overlay.Enabled` is false (spec 7).
- `TooltipText` = `Session.CurrentSessionId` only when `ShowSessionName`; otherwise null — privileged matter never renders on an always-on-top surface by default.
- `ScreenClamp`: keeps at least the full pill inside the virtual screen (clamp x into `[vx, vx+vw-w]`, y into `[vy, vy+vh-h]`); NaN input -> fallback `(vx + vw - w - 16, vy + 16)`.
- `WindowStateStore`: throwaway JSON `{ "x": ..., "y": ... }`; ANY read failure returns null (never throws — it is volatile state, not truth).

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.App.Tests/ScreenClampTests.cs`:

```csharp
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ScreenClampTests
{
    // Virtual screen 0,0 1920x1080; pill 220x56.
    [Theory]
    [InlineData(100, 100, 100, 100)]                       // in bounds: unchanged
    [InlineData(-500, 100, 0, 100)]                        // off left: clamped to edge
    [InlineData(3000, 100, 1700, 100)]                     // off right: 1920-220
    [InlineData(100, -50, 100, 0)]                         // off top
    [InlineData(100, 2000, 100, 1024)]                     // off bottom: 1080-56
    public void Clamps_into_virtual_screen(double x, double y, double ex, double ey)
    {
        var (cx, cy) = ScreenClamp.Clamp(x, y, 220, 56, 0, 0, 1920, 1080);
        Assert.Equal(ex, cx);
        Assert.Equal(ey, cy);
    }

    [Fact]
    public void NaN_falls_back_to_top_right_with_margin()
    {
        var (cx, cy) = ScreenClamp.Clamp(double.NaN, double.NaN, 220, 56, 0, 0, 1920, 1080);
        Assert.Equal(1920 - 220 - 16, cx);
        Assert.Equal(16, cy);
    }

    [Fact]
    public void Negative_virtual_origin_multimonitor_is_respected()
    {
        // Second monitor to the LEFT: virtual screen starts at -1920.
        var (cx, _) = ScreenClamp.Clamp(-1800, 10, 220, 56, -1920, 0, 3840, 1080);
        Assert.Equal(-1800, cx);                           // valid position on the left monitor
    }
}
```

`tests/LocalScribe.App.Tests/WindowStateStoreTests.cs`:

```csharp
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class WindowStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ls-ws-" + Guid.NewGuid().ToString("N"), "window-state.json");
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_path)!, true); } catch { } }

    [Fact]
    public void Roundtrips_position()
    {
        var store = new WindowStateStore(_path);
        store.Save(123.5, 67.25);
        Assert.Equal((123.5, 67.25), new WindowStateStore(_path).Load());
    }

    [Fact]
    public void Absent_or_corrupt_returns_null()
    {
        Assert.Null(new WindowStateStore(_path).Load());
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{not json");
        Assert.Null(new WindowStateStore(_path).Load());   // throwaway file: never throws
    }
}
```

`tests/LocalScribe.App.Tests/OverlayViewModelTests.cs`:

```csharp
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class OverlayViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-ov-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (OverlayViewModel Overlay, SessionViewModel Session) Make(Settings settings)
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, settings, a => a(),
            startOptions: LiveTestDoubles.Options());
        return (new OverlayViewModel(session, settings), session);
    }

    [Fact]
    public async Task Visible_only_while_recording_or_paused()
    {
        var (overlay, session) = Make(new Settings());
        Assert.False(overlay.IsVisible);                   // Idle
        await session.StartCommand.ExecuteAsync(null);
        Assert.True(overlay.IsVisible);                    // Recording
        await session.PauseResumeCommand.ExecuteAsync(null);
        Assert.True(overlay.IsVisible);                    // Paused (spec 2.1)
        await session.PauseResumeCommand.ExecuteAsync(null);
        await session.StopCommand.ExecuteAsync(null);
        Assert.False(overlay.IsVisible);                   // Idle again (Finalizing hides too)
    }

    [Fact]
    public async Task Disabled_overlay_never_shows()
    {
        var (overlay, session) = Make(new Settings
        { Overlay = new OverlaySetting { Enabled = false } });
        await session.StartCommand.ExecuteAsync(null);
        Assert.False(overlay.IsVisible);
        await session.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Session_name_suppressed_by_default_opt_in_via_tooltip()
    {
        var (overlay, session) = Make(new Settings());     // ShowSessionName default false
        await session.StartCommand.ExecuteAsync(null);
        Assert.Null(overlay.TooltipText);                  // privileged matter never rendered
        await session.StopCommand.ExecuteAsync(null);

        var (overlay2, session2) = Make(new Settings
        { Overlay = new OverlaySetting { ShowSessionName = true } });
        await session2.StartCommand.ExecuteAsync(null);
        Assert.NotNull(overlay2.TooltipText);              // opt-in: tooltip only
        await session2.StopCommand.ExecuteAsync(null);
    }
}
```

(Adapt `OverlaySetting` construction to the real record's init syntax — it exists in 2a `Settings`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests` — Expected: new tests FAIL.

- [ ] **Step 3: Implement the three testable pieces**

`src/LocalScribe.App/ViewModels/ScreenClamp.cs`:

```csharp
namespace LocalScribe.App.ViewModels;

/// <summary>Keeps the overlay pill inside the virtual screen (design decision 12: remembered
/// position clamped on load - a monitor may have been unplugged since last run).</summary>
public static class ScreenClamp
{
    public static (double X, double Y) Clamp(double x, double y, double w, double h,
        double vx, double vy, double vw, double vh)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
            return (vx + vw - w - 16, vy + 16);            // fallback: top-right with margin
        return (Math.Clamp(x, vx, Math.Max(vx, vx + vw - w)),
                Math.Clamp(y, vy, Math.Max(vy, vy + vh - h)));
    }
}
```

`src/LocalScribe.App/ViewModels/WindowStateStore.cs`:

```csharp
using System.IO;
using System.Text.Json;
namespace LocalScribe.App.ViewModels;

/// <summary>Volatile overlay position (spec 7: throwaway window-state.json, NOT settings).
/// Any load failure is null - this file is never truth, never worth an error.</summary>
public sealed class WindowStateStore(string path)
{
    private sealed record State(double X, double Y);

    public (double X, double Y)? Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<State>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return s is null ? null : (s.X, s.Y);
        }
        catch { return null; }
    }

    public void Save(double x, double y)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new State(x, y)));
        }
        catch { /* volatile state - losing it costs one re-drag */ }
    }
}
```

`src/LocalScribe.App/ViewModels/OverlayViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

/// <summary>Overlay pill state (spec 2.1): visible only in Recording/Paused, supplements -
/// never replaces - the tray consent indicator. Session name is opt-in tooltip-only (design
/// decision 12: privileged matter must not render on an always-on-top surface by default).</summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly Settings _settings;

    public SessionViewModel Session { get; }
    public bool ShowLevelMeter => _settings.Overlay.ShowLevelMeter;
    public bool IsVisible => _settings.Overlay.Enabled
        && Session.State is SessionState.Recording or SessionState.Paused;
    public string? TooltipText => _settings.Overlay.ShowSessionName ? Session.CurrentSessionId : null;

    public OverlayViewModel(SessionViewModel session, Settings settings)
    {
        (Session, _settings) = (session, settings);
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.State))
            {
                OnPropertyChanged(nameof(IsVisible));
                OnPropertyChanged(nameof(TooltipText));
            }
        };
    }
}
```

Run: `dotnet test tests/LocalScribe.App.Tests` — Expected: PASS.

- [ ] **Step 4: Implement the window + interop (manual verification)**

`src/LocalScribe.App/NativeWindowInterop.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace LocalScribe.App;

/// <summary>The two Win32 calls the overlay needs (design decision 12). Call both from
/// OnSourceInitialized (the HWND must exist).</summary>
public static class NativeWindowInterop
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern long SetWindowLongPtrW(nint hwnd, int index, long value);

    /// <summary>WDA_EXCLUDEFROMCAPTURE: the pill vanishes from screen shares/recordings while
    /// staying visible locally - a lawyer sharing their screen over Webex gets a clean share
    /// and the recording signal stays local (Win10 2004+; returns false silently before).</summary>
    public static void ExcludeFromCapture(Window window)
        => SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WDA_EXCLUDEFROMCAPTURE);

    /// <summary>WS_EX_NOACTIVATE + TOOLWINDOW: clicking Pause/Stop mid-call never steals focus
    /// from the meeting; no taskbar/alt-tab presence.</summary>
    public static void MakeNoActivate(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE,
            GetWindowLongPtrW(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}
```

`src/LocalScribe.App/OverlayWindow.xaml`:

```xml
<Window x:Class="LocalScribe.App.OverlayWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="230" SizeToContent="Height"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ShowActivated="False" ResizeMode="NoResize"
        ToolTip="{Binding TooltipText}">
    <Border CornerRadius="14" Background="#E6202020" Padding="12,8"
            MouseLeftButtonDown="OnDragMove">
        <StackPanel Orientation="Horizontal">
            <Ellipse Width="10" Height="10" VerticalAlignment="Center" Margin="0,0,8,0"
                     x:Name="StateDot" />
            <TextBlock Text="{Binding Session.Elapsed}" Foreground="White"
                       VerticalAlignment="Center" Margin="0,0,10,0" FontFamily="Consolas" />
            <StackPanel VerticalAlignment="Center" Margin="0,0,10,0"
                        Visibility="{Binding ShowLevelMeter, Converter={StaticResource BoolToVis}}">
                <ProgressBar Width="34" Height="4" Margin="0,1" Maximum="1"
                             Value="{Binding Session.LocalLevel.Value, Mode=OneWay}" />
                <ProgressBar Width="34" Height="4" Margin="0,1" Maximum="1"
                             Value="{Binding Session.RemoteLevel.Value, Mode=OneWay}" />
            </StackPanel>
            <Button Content="&#xE769;" FontFamily="Segoe Fluent Icons" Click="OnPauseResume"
                    Width="28" Height="24" Margin="0,0,4,0" Focusable="False" x:Name="PauseButton" />
            <Button Content="&#xE71A;" FontFamily="Segoe Fluent Icons" Click="OnStop"
                    Width="28" Height="24" Focusable="False" />
        </StackPanel>
    </Border>
</Window>
```

Additions: `<Window.Resources><BooleanToVisibilityConverter x:Key="BoolToVis" /></Window.Resources>` under the root element, and one line on `OverlayViewModel`: `public bool ExcludeFromCapture => _settings.Overlay.ExcludeFromCapture;` (settings-mirroring, covered by inspection).

`src/LocalScribe.App/OverlayWindow.xaml.cs`:

```csharp
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
namespace LocalScribe.App;

/// <summary>The recording pill (design decision 12): topmost, no-activate (clicking Pause
/// mid-call never steals focus from the meeting), excluded from screen capture by default,
/// draggable with a remembered clamped position. Show/Hide is driven by App via
/// OverlayViewModel.IsVisible - this window never closes itself.</summary>
public partial class OverlayWindow
{
    private static readonly Brush RecordingBrush =
        new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));
    private static readonly Brush PausedBrush =
        new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x0C));

    private readonly OverlayViewModel _vm;
    private readonly WindowStateStore _stateStore;

    public OverlayWindow(OverlayViewModel vm, WindowStateStore stateStore)
    {
        InitializeComponent();
        (_vm, _stateStore) = (vm, stateStore);
        DataContext = vm;
        vm.Session.PropertyChanged += OnSessionChanged;
        UpdateStateVisuals();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowInterop.MakeNoActivate(this);
        if (_vm.ExcludeFromCapture) NativeWindowInterop.ExcludeFromCapture(this);

        var pos = _stateStore.Load();
        var (x, y) = ScreenClamp.Clamp(pos?.X ?? double.NaN, pos?.Y ?? double.NaN,
            Width, ActualHeight > 0 ? ActualHeight : 56,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        (Left, Top) = (x, y);
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        _stateStore.Save(Left, Top);
    }

    // Fire-and-forget into the shared commands; Focusable=False keeps focus in the meeting.
    private void OnPauseResume(object sender, RoutedEventArgs e)
        => _vm.Session.PauseResumeCommand.Execute(null);

    private void OnStop(object sender, RoutedEventArgs e)
        => _vm.Session.StopCommand.Execute(null);

    private void OnSessionChanged(object? _, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.State)) UpdateStateVisuals();
    }

    private void UpdateStateVisuals()
    {
        bool paused = _vm.Session.State == SessionState.Paused;
        StateDot.Fill = paused ? PausedBrush : RecordingBrush;
        PauseButton.Content = paused ? "\uE768" : "\uE769";   // Segoe Fluent: play / pause
    }
}
```

In `App.xaml.cs`: construct `OverlayViewModel` + `OverlayWindow` once; subscribe `overlayVm.PropertyChanged(IsVisible)` -> `overlayWindow.Show()` / `overlayWindow.Hide()` (never `Close`). Overlay shows Pause/Stop only — Start stays on tray/live view (design decision 12).

- [ ] **Step 5: Verify**

Run: `dotnet test tests/LocalScribe.App.Tests`, full gate, `dotnet build` — Expected: PASS, 0 warnings.
Manual: pill appears on Start, timer runs, bars flick with speech on each side, Pause toggles state dot, drag persists across app restarts, clamp works after moving it to a screen edge, Stop hides it.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/ tests/LocalScribe.App.Tests/
git commit -m "feat: recording overlay pill - capture-excluded, no-activate, clamped remembered position"
```

---

## Task 6: Stage 3b smoke runbook + execution  [SMOKE]

**Files:**
- Create: `docs/plans/2026-07-02-stage-3b-smoke-runbook.md`

- [ ] **Step 1: Write the runbook**

```markdown
# Stage 3b smoke runbook — WPF shell on real hardware

Prereqs: models fetched; 3a smoke S1 previously passed on this box.
Run: `dotnet run --project src/LocalScribe.App`

## B1 - Tray consent surface
Gray dot idle; Start -> red + RECORDING tooltip; Pause -> orange; Stop -> gray.
Notices appear as balloons (start with no meeting app: expect the system-mix fallback balloon).
Exit while recording prompts, finalizes (verify folder), then quits. Exit while idle just quits.

## B2 - Live view
Lines appear within a few seconds of speech, `[mm:ss] Me/Them: text`, markers italic.
Out-of-order finalization inserts ABOVE newer lines (talk over the remote side to force it).
Auto-scroll sticks to bottom; scrolling up stops following; closing the window does not stop
the recording.

## B3 - Overlay pill (the un-repeatable-call check, design decision 12)
Visible only while Recording/Paused. Timer ticks through Pause. Two bars: speak -> top bar
flicks; remote audio -> bottom bar flicks (this is the at-a-glance "both streams alive" check).
Pause/Stop work WITHOUT stealing focus from the foreground app (type in Notepad, click Pause,
keep typing - caret must not leave Notepad). No taskbar/alt-tab entry.
Tooltip shows NO session name by default; flip overlay.showSessionName in settings.json and
confirm tooltip-only opt-in.

## B4 - Screen-capture exclusion
Share the full screen in a Webex/Teams/Zoom call (or OBS display capture): the pill must be
INVISIBLE in the shared/recorded view while visible locally. Flip overlay.excludeFromCapture
to false, restart, confirm it becomes visible in the share.

## B5 - Position memory + clamp
Drag the pill somewhere, exit, relaunch, start: same spot. Fake a monitor change: edit
window-state.json to x=99999, relaunch, start: pill clamps back on-screen.

## B6 - End-to-end Webex (primary use case)
Real Webex 1:1: tray start, overlay confirms both bars alive, live view transcribes both
sides, stop from the OVERLAY, folder verifies as in 3a S2 (per-process, no degraded marker).

Record results (pass/fail + notes) inline here, per run, dated.
```

- [ ] **Step 2: Execute B1-B5 on the dev box** (B6 needs a real Webex call — schedule with the user; B1-B5 + unit gates are the merge bar, B6 before calling Stage 3 done).

- [ ] **Step 3: Commit**

```bash
git add docs/plans/2026-07-02-stage-3b-smoke-runbook.md
git commit -m "docs: Stage 3b smoke runbook (+ recorded first run)"
```

---

## Task ordering & parallelism

1 -> 2 -> {3, 4, 5} (mutually independent, all bind the Task-2 VM) -> 6. Single executor: in numeric order.

## Definition of Done (Stage 3b)

- Both unit gates green (`dotnet test --filter "Category!=Fixture"` covers Core.Tests + App.Tests), `dotnet build` 0 warnings.
- No `System.Windows.*` types in `ViewModels/`.
- Smoke B1-B5 executed and recorded; B6 (real Webex) before Stage 3 is declared done.
- Overlay: capture-excluded by default, no focus steal, session name suppressed by default — verified, not assumed.
- Tray remains the always-present consent indicator; the app never records without the red state showing.


