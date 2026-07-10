# App-Mute Awareness & Console Pills Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle the Record-console/read-view controls as Webex-style icon pills with an in-app Ctrl+Shift+M mute hotkey (Phase 1, ungated), and add the tier-3 advisory app-mute banner driven by the Windows 11 call-mute tray signal (Phase 2, HARD-GATED on runbook string capture).

**Architecture:** Per `docs/plans/2026-07-11-app-mute-awareness-design.md` (READ IT FIRST — it carries the probe evidence, the three-tier product story, and the rejected-alternatives list). Phase 1 is pure XAML styling + one KeyBinding. Phase 2 adds: a pure `TrayTextParser`, an `IAppMuteSignalSource` seam (production = `System.Windows.Automation` tray walk; NO FlaUI in product assemblies), a pure debounced `AppMuteBannerEvaluator`, a poll-gated `AppMuteWatcher`, and one warning-row banner with a one-click action that routes through the EXISTING exact-signal mute command.

**Tech Stack:** C#/.NET 10, WPF + Wpf.Ui 4.0.3 (`SymbolIcon`/`SymbolRegular`), System.Windows.Automation (UIAutomationClient, part of the Windows Desktop SDK), CommunityToolkit.Mvvm, xUnit.

## Context for a fresh session

- Work on a NEW branch `app-mute-awareness` off master. PRE-FLIGHT: `fix/mf-flac-seek-clock` must already be merged to master — verify baselines FIRST: `dotnet test tests/LocalScribe.Core.Tests` → **447 passing + 2 KNOWN fixture fails** (`Der_within_baseline_plus_epsilon`, `Golden_pair_wer_stays_at_baseline`); `dotnet test tests/LocalScribe.App.Tests` → **391 passing**; build 0 warnings. If Core shows 446/App 384 the seek-fix branch is NOT merged — STOP and resolve with the user before starting (Phase 1 touches the same App project).
- A running `LocalScribe.App.exe` locks Core.dll → MSB3027 copy error (not a compile error) — report, never kill processes. Do NOT use `--artifacts-path` unless an actual MSB3027 occurs (it relocates output and breaks fixture-path resolution → spurious fixture failures).
- Start a FRESH SDD ledger section at `.superpowers/sdd/progress.md` (current content ends with the completed mute-controls run — archive or append a new header).
- Harness facts: `tests/LocalScribe.App.Tests/SessionViewModelTests.cs` constructs the VM with `LiveTestDoubles.MakeController(_root)` + `dispatch: a => a()` — mirror existing tests' exact ctor usage (the VM ctor has grown; copy a neighboring test's construction). Tests reading persisted state after `StopAsync` must `await controller.PendingFinalize`.
- `SessionViewModel` already has: `IsLocalMuted`, `MicDeviceMuted`, `MuteLocalCommand` (`AsyncRelayCommand`, CanExecute = Recording|Paused), the named-handler + `_dispatch` marshal + Dispose-detach pattern, and `StartAsync` flag resets — Phase 2 mirrors that pattern exactly.
- `LiveViewWindow.xaml` currently has: a controls row (Pause/Resume, Stop, "Mute my side" button with a `Style.Triggers` Content flip), and a warning-rows StackPanel (SemiBold muted state line, MicSilent/RemoteSilent banners, device-muted banner). Locate ALL edit points by CONTENT, not line numbers.
- Locked rules (verbatim from the design): the app-mute signal is ADVISORY — never writes transcript markers, never gates recording; banner actions route through `SetLocalMuteAsync` so markers come from the user's click; global hotkeys stay dropped (in-app KeyBinding only).

## Global Constraints

- No Unicode emojis in tests or code. Build 0 warnings. Core stays WPF-free (ALL Phase-2 code lives in LocalScribe.App / App.Tests; nothing touches LocalScribe.Core).
- No FlaUI/UIA references in product assemblies except `System.Windows.Automation` inside `TrayMuteSignalSource` (tools/UiaProbe keeps FlaUI).
- Suite gate after EVERY task: no NEW failures beyond the 2 known Core fixtures; run both suites normally; commit after each task.
- Banner copy (exact, `<App>` substituted from the signal): mismatch-1 `"<App> looks muted - LocalScribe is still recording your side."` + action label `"Mute my side"`; mismatch-2 `"You are unmuted in <App> - LocalScribe is not recording your side."` + action label `"Unmute"`.
- Debounce: mismatch must persist >= 5000 ms before showing; clears immediately on resolution. Poll cadence 2000 ms, only while Recording.
- **PHASE 2 HARD GATE:** Task 4 requires the captured tray strings from the runbook (`docs/plans/2026-07-10-uia-mute-spike-runbook.md` rev 2026-07-11). If the user has not supplied them, STOP after Phase 1 and report — do not invent patterns.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/LocalScribe.App/App.xaml` (or the shared resource dictionary it merges) | app-wide styles | Modify: +`PillButton` style |
| `src/LocalScribe.App/LiveViewWindow.xaml` | Record console | Modify: pill controls, Ctrl+Shift+M KeyBinding, app-mute banner row |
| `src/LocalScribe.App/ReadViewWindow.xaml` | read view transport | Modify: pill restyle only |
| `src/LocalScribe.App/Services/AppMuteSignal.cs` | seam + reading types | Create |
| `src/LocalScribe.App/Services/TrayTextParser.cs` | Name string → reading | Create |
| `src/LocalScribe.App/Services/TrayMuteSignalSource.cs` | UIA tray walk (smoke-only) | Create |
| `src/LocalScribe.App/Services/AppMuteBannerEvaluator.cs` | pure mismatch + debounce | Create |
| `src/LocalScribe.App/Services/AppMuteWatcher.cs` | poll lifecycle + event | Create |
| `src/LocalScribe.App/ViewModels/SessionViewModel.cs` | banner state + action | Modify |
| `src/LocalScribe.App/App.xaml.cs` (composition) | wire watcher + timer | Modify |
| `tests/LocalScribe.App.Tests/TrayTextParserTests.cs` | parser facts | Create |
| `tests/LocalScribe.App.Tests/AppMuteBannerEvaluatorTests.cs` | evaluator facts | Create |
| `tests/LocalScribe.App.Tests/AppMuteWatcherTests.cs` | lifecycle facts | Create |
| `tests/LocalScribe.App.Tests/SessionViewModelTests.cs` | VM banner facts | Modify |
| `docs/specs/localscribe-specs.md` | spec deltas | Modify (final task) |

---

## Phase 1 — Console pills + in-app hotkey (UNGATED)

### Task 1: Shared pill style + Record-console pills

**Files:**
- Modify: `src/LocalScribe.App/App.xaml` (locate the application resources / merged dictionary where shared styles like `BoolToVis` live; add alongside)
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml`

**Interfaces:**
- Produces: `Style x:Key="PillButton" TargetType="Button"` and `Style x:Key="PillToggleEngaged"` (documented below) consumed by Tasks 2 and 8.

- [ ] **Step 1: Verify the Wpf.Ui symbol names compile.** The intended icons: `Pause24`, `Play24`, `Stop24`, `Mic24`, `MicOff24` (all `Wpf.Ui.Controls.SymbolRegular` members). Verification: add them in Step 3's XAML and build — a wrong name is a compile-time XAML error. Known-good precedent: `SymbolRegular.Record24` (verified on this exact Wpf.Ui 4.0.3 in Stage 5.4). If a name is missing, pick the nearest same-glyph variant (`Mic20`/`Mic28`, `MicOff20`/`MicOff28`, `RecordStop24` for stop) and note the substitution in your report.

- [ ] **Step 2: Add the shared styles** in `App.xaml`'s resources (exact XAML; adjust only the resource-section placement to match the file's existing organization):

```xml
        <!-- Webex-style pill control (design 2026-07-11 section 4): rounded icon+label button.
             Theme-aware via DynamicResource theme brushes; red-tint feedback is opt-in via
             PillDanger on the Stop control. -->
        <Style x:Key="PillButton" TargetType="Button">
            <Setter Property="Padding" Value="14,6" />
            <Setter Property="Margin" Value="4,0" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="BorderBrush" Value="{DynamicResource ControlElevationBorderBrush}" />
            <Setter Property="Background" Value="{DynamicResource ControlFillColorDefaultBrush}" />
            <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Bd" CornerRadius="16"
                                Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}"
                                Padding="{TemplateBinding Padding}">
                            <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Center" />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background"
                                        Value="{DynamicResource ControlFillColorSecondaryBrush}" />
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="Bd" Property="Background"
                                        Value="{DynamicResource ControlFillColorTertiaryBrush}" />
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.5" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
```

If `ControlElevationBorderBrush`/`ControlFillColor*Brush` are not resolvable at runtime in this app's Wpf.Ui theme dictionaries (verify: they are standard Wpf.Ui/WinUI brush keys), substitute the nearest brushes already used by this app's styles and note it.

- [ ] **Step 3: Restyle the Record-console controls row** in `LiveViewWindow.xaml`. Locate the row with the Pause/Resume, Stop, and "Mute my side" buttons. Replace the three button definitions (KEEP every existing `Command=` binding and the `Session.IsLocalMuted` trigger semantics; this is visual-only):

```xml
                    <Button Style="{StaticResource PillButton}"
                            Command="{Binding Session.PauseResumeCommand}">
                        <StackPanel Orientation="Horizontal">
                            <ui:SymbolIcon FontSize="18" Margin="0,0,6,0">
                                <ui:SymbolIcon.Style>
                                    <Style TargetType="ui:SymbolIcon">
                                        <Setter Property="Symbol" Value="Pause24" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding Session.State}" Value="Paused">
                                                <Setter Property="Symbol" Value="Play24" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </ui:SymbolIcon.Style>
                            </ui:SymbolIcon>
                            <TextBlock VerticalAlignment="Center">
                                <TextBlock.Style>
                                    <Style TargetType="TextBlock">
                                        <Setter Property="Text" Value="Pause" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding Session.State}" Value="Paused">
                                                <Setter Property="Text" Value="Resume" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </TextBlock.Style>
                            </TextBlock>
                        </StackPanel>
                    </Button>
```

Adapt the exact command/property names to the file's existing bindings (e.g. if the current button binds a different pause command name or the current Content is a single "Pause/Resume" caption, keep ITS command and let the label/icon flip on `Session.State` as above — the VM already exposes the state the old button's CanExecute used). Stop pill (same pattern, red-tint triggers added inline):

```xml
                    <Button Command="{Binding Session.StopCommand}">
                        <Button.Style>
                            <Style TargetType="Button" BasedOn="{StaticResource PillButton}">
                                <Style.Triggers>
                                    <Trigger Property="IsMouseOver" Value="True">
                                        <Setter Property="Background" Value="#33C42B1C" />
                                        <Setter Property="BorderBrush" Value="#C42B1C" />
                                    </Trigger>
                                </Style.Triggers>
                            </Style>
                        </Button.Style>
                        <StackPanel Orientation="Horizontal">
                            <ui:SymbolIcon Symbol="Stop24" FontSize="18" Margin="0,0,6,0" />
                            <TextBlock Text="Stop" VerticalAlignment="Center" />
                        </StackPanel>
                    </Button>
```

Mute pill (replaces the existing text-flip button; engaged look while muted; tooltip carries the Task-3 hotkey):

```xml
                    <Button Command="{Binding Session.MuteLocalCommand}"
                            ToolTip="Mute my side (Ctrl+Shift+M)">
                        <Button.Style>
                            <Style TargetType="Button" BasedOn="{StaticResource PillButton}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding Session.IsLocalMuted}" Value="True">
                                        <Setter Property="Background" Value="{DynamicResource AccentFillColorDefaultBrush}" />
                                        <Setter Property="Foreground" Value="{DynamicResource TextOnAccentFillColorPrimaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Button.Style>
                        <StackPanel Orientation="Horizontal">
                            <ui:SymbolIcon FontSize="18" Margin="0,0,6,0">
                                <ui:SymbolIcon.Style>
                                    <Style TargetType="ui:SymbolIcon">
                                        <Setter Property="Symbol" Value="Mic24" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding Session.IsLocalMuted}" Value="True">
                                                <Setter Property="Symbol" Value="MicOff24" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </ui:SymbolIcon.Style>
                            </ui:SymbolIcon>
                            <TextBlock VerticalAlignment="Center">
                                <TextBlock.Style>
                                    <Style TargetType="TextBlock">
                                        <Setter Property="Text" Value="Mute my side" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding Session.IsLocalMuted}" Value="True">
                                                <Setter Property="Text" Value="Unmute" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </TextBlock.Style>
                            </TextBlock>
                        </StackPanel>
                    </Button>
```

(Verify the `ui:` xmlns is already declared in this window — it is used elsewhere; add `xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"` only if missing.)

- [ ] **Step 4: Build + full App suite.** `dotnet build LocalScribe.slnx` → 0 warnings (XAML compiles; wrong symbol names fail here). `dotnet test tests/LocalScribe.App.Tests` → 391 passing (styling must not break any VM test). `dotnet test tests/LocalScribe.Core.Tests` → 447 + 2 known.
- [ ] **Step 5: Commit.** `git commit -m "feat(app): Webex-style pill controls on the Record console"`

### Task 2: Read-view transport pills

**Files:**
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml`

**Interfaces:**
- Consumes: `PillButton` style (Task 1). Existing `Playback.*` bindings (`PlayPauseCommand`, `StopCommand`, `PlayPauseCaption`, `LocalMuted`, `RemoteMuted`) — KEEP them all.

- [ ] **Step 1: Locate the transport row** (Play, Stop, "Mute local", "Mute remote" buttons + sliders). Restyle the four buttons with `PillButton` exactly as in Task 1's pattern: Play/Pause pill (icon flips on `{Binding Playback.IsPlaying}`: `Play24` default, `Pause24` when True; label binds the EXISTING `Playback.PlayPauseCaption`), Stop pill (`Stop24`, same red-tint style as Task 1), Mute local / Mute remote pills (`Mic24`→`MicOff24` + accent engaged style via `Playback.LocalMuted`/`Playback.RemoteMuted` — these are the playback-leg mutes; their DataTriggers bind those existing properties). Sliders and volume controls unchanged. All `Command=`/`Click=` wiring byte-identical to what is there now.
- [ ] **Step 2: Build + both suites** (same gates as Task 1 Step 4).
- [ ] **Step 3: Commit.** `git commit -m "feat(app): pill transport controls on the read view"`

### Task 3: Ctrl+Shift+M in-app hotkey

**Files:**
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml`

- [ ] **Step 1: Add the KeyBinding** to the window (top-level, next to any existing `Window.InputBindings`; create the element if absent):

```xml
    <Window.InputBindings>
        <!-- In-app mirror of Teams' mute hotkey (design 2026-07-11 section 4). Window-scoped
             only: global hotkeys remain dropped (Stage 4 lock); the command's CanExecute
             already gates to Recording/Paused. -->
        <KeyBinding Modifiers="Ctrl+Shift" Key="M"
                    Command="{Binding Session.MuteLocalCommand}" />
    </Window.InputBindings>
```

If this window is a Wpf.Ui `FluentWindow`/`ui:FluentWindow`, the element name follows the actual root tag — attach `InputBindings` to whatever the root element is.

- [ ] **Step 2: Build + App suite** (same gates). KeyBindings are not unit-testable headlessly — the verification is the build plus the user smoke item recorded in the final task.
- [ ] **Step 3: Commit.** `git commit -m "feat(app): Ctrl+Shift+M in-app mute hotkey on the Record console"`

---

## Phase 2 — Tray-signal watcher + advisory banner (HARD-GATED)

### Task 4: GATE — pin the captured tray strings

**STATUS: GATE CLEARED 2026-07-11.** The runbook capture ran during a real Webex call (dumps
`uia-dump-20260711-091553/091613/091641.txt`, unmuted/muted/unmuted). Evidence recorded below —
Task 5 consumes these values; no further action or STOP applies. Findings better than designed
for: BOTH states carry an explicit `"Microphone "` first-line prefix, so the parser needs no
"using your microphone" heuristics at all.

| State | Tray icon UIA Name (first line; full Name continues with the flyout body) |
|---|---|
| Webex live (unmuted) | `Microphone Unmuted: Webex` |
| Webex muted | `Microphone Muted: Webex` |
| No app capturing | element ABSENT (confirmed in the 08:31 no-call dump) — fail-open Unknown |

Full multi-line Name (muted example, verbatim; line breaks may arrive as \n or \r\n — trim `\r`):

```
Microphone Muted: Webex
To toggle mute button, press Win+Alt+K.

Apps using your microphone:
Webex
```

Selector facts: the element is `[Button] id='SystemTrayIcon' class='SystemTray.AccentButton'`
inside the taskbar tray — but `id='SystemTrayIcon'` is SHARED by other tray buttons (Location
privacy, input indicator, network), so the discriminator is the Name prefix `"Microphone "`,
NOT the AutomationId, and not the class alone.

### Task 5: `TrayTextParser` + `AppMuteSignal` types

**Files:**
- Create: `src/LocalScribe.App/Services/AppMuteSignal.cs`
- Create: `src/LocalScribe.App/Services/TrayTextParser.cs`
- Create: `tests/LocalScribe.App.Tests/TrayTextParserTests.cs`

**Interfaces:**
- Produces: `enum AppMuteState { Unknown, Muted, Live }`; `readonly record struct AppMuteReading(AppMuteState State, string? AppName)`; `interface IAppMuteSignalSource { AppMuteReading Read(); }`; `static AppMuteReading TrayTextParser.Parse(string? trayIconName)`.

- [ ] **Step 1: Types** (`AppMuteSignal.cs`):

```csharp
namespace LocalScribe.App.Services;

/// <summary>One reading of the Windows 11 call-mute tray signal (design 2026-07-11 section 2).
/// Unknown is the fail-open state: no integrated call app is reporting, the icon is absent, or
/// the text did not parse. The signal is ADVISORY - it never writes markers, never gates
/// recording (locked rule).</summary>
public enum AppMuteState { Unknown, Muted, Live }

public readonly record struct AppMuteReading(AppMuteState State, string? AppName);

/// <summary>Seam over the tray read so the watcher/VM are testable without UIA. Read() must
/// never throw - implementations fail open to Unknown.</summary>
public interface IAppMuteSignalSource
{
    AppMuteReading Read();
}
```

- [ ] **Step 2: Failing tests** — a fact table pinning the Task-4 captured strings verbatim:

```csharp
public sealed class TrayTextParserTests
{
    // Captured 2026-07-11 during a real Webex call (uia-dump-20260711-091553/091613/091641.txt).
    private const string MutedFull = "Microphone Muted: Webex\nTo toggle mute button, press Win+Alt+K.\n\nApps using your microphone:\nWebex";
    private const string LiveFull = "Microphone Unmuted: Webex\nTo toggle mute button, press Win+Alt+K.\n\nApps using your microphone:\nWebex";

    [Theory]
    [InlineData(MutedFull, AppMuteState.Muted, "Webex")]
    [InlineData(LiveFull, AppMuteState.Live, "Webex")]
    // First line alone must also parse (the flyout body below it is not load-bearing):
    [InlineData("Microphone Muted: Webex", AppMuteState.Muted, "Webex")]
    [InlineData("Microphone Unmuted: Webex", AppMuteState.Live, "Webex")]
    // CRLF tolerance (UIA may deliver \r\n):
    [InlineData("Microphone Muted: Webex\r\nTo toggle mute button, press Win+Alt+K.", AppMuteState.Muted, "Webex")]
    // A different integrated app must flow through as its own name:
    [InlineData("Microphone Muted: Teams", AppMuteState.Muted, "Teams")]
    // Robustness:
    [InlineData("", AppMuteState.Unknown, null)]
    [InlineData(null, AppMuteState.Unknown, null)]
    [InlineData("Volume: 43%", AppMuteState.Unknown, null)]
    [InlineData("Steam - synchronizing", AppMuteState.Unknown, null)]
    [InlineData("Privacy Location in use by:\nWebex", AppMuteState.Unknown, null)]
    public void Parses_tray_icon_names(string? name, AppMuteState state, string? app)
    {
        var r = TrayTextParser.Parse(name);
        Assert.Equal(state, r.State);
        Assert.Equal(app, r.AppName);
    }
}
```

- [ ] **Step 3: Verify RED** (compile error: types missing), then implement `TrayTextParser` — take the FIRST line of the input (split on '\n', `TrimEnd('\r')`, trim): if it starts with `"Microphone Muted: "` → Muted, AppName = the remainder trimmed; if it starts with `"Microphone Unmuted: "` → Live, AppName = the remainder trimmed; anything else (or null/empty/blank first line or empty app name) → Unknown. The two prefix constants live in this class only, annotated `// captured 2026-07-11, runbook rev 2026-07-11 (uia-dump-20260711-0916xx)`.
- [ ] **Step 4: GREEN + both suites.** `dotnet test tests/LocalScribe.App.Tests --filter TrayTextParserTests` → all pass; full suites → no new failures.
- [ ] **Step 5: Commit.** `git commit -m "feat(app): tray call-mute text parser (evidence-pinned)"`

### Task 6: `AppMuteBannerEvaluator` (pure mismatch + debounce)

**Files:**
- Create: `src/LocalScribe.App/Services/AppMuteBannerEvaluator.cs`
- Create: `tests/LocalScribe.App.Tests/AppMuteBannerEvaluatorTests.cs`

**Interfaces:**
- Produces: `enum AppMuteBannerKind { None, AppMutedButRecording, AppLiveButMuted }`; `sealed class AppMuteBannerEvaluator { AppMuteBannerKind Evaluate(AppMuteReading reading, bool localMuted, long nowMs); }`.

- [ ] **Step 1: Failing tests:**

```csharp
public sealed class AppMuteBannerEvaluatorTests
{
    private static AppMuteReading Muted() => new(AppMuteState.Muted, "Webex");
    private static AppMuteReading Live() => new(AppMuteState.Live, "Webex");
    private static AppMuteReading Unknown() => new(AppMuteState.Unknown, null);

    [Fact]
    public void Mismatch_shows_only_after_the_debounce()
    {
        var e = new AppMuteBannerEvaluator();
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Muted(), localMuted: false, nowMs: 0));
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Muted(), false, 4999));
        Assert.Equal(AppMuteBannerKind.AppMutedButRecording, e.Evaluate(Muted(), false, 5000));
    }

    [Fact]
    public void Resolution_clears_immediately()
    {
        var e = new AppMuteBannerEvaluator();
        e.Evaluate(Muted(), false, 0);
        e.Evaluate(Muted(), false, 6000);                       // shown
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Muted(), true, 6100));   // user muted LS: agree now
    }

    [Fact]
    public void Unknown_never_banners_and_resets_pending()
    {
        var e = new AppMuteBannerEvaluator();
        e.Evaluate(Muted(), false, 0);
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Unknown(), false, 4000));
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Muted(), false, 4500)); // pending restarted
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Muted(), false, 9000)); // 4.5s into NEW window
        Assert.Equal(AppMuteBannerKind.AppMutedButRecording, e.Evaluate(Muted(), false, 9500));
    }

    [Fact]
    public void Opposite_mismatch_direction_banners_after_its_own_debounce()
    {
        var e = new AppMuteBannerEvaluator();
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Live(), localMuted: true, 0));
        Assert.Equal(AppMuteBannerKind.AppLiveButMuted, e.Evaluate(Live(), true, 5000));
    }

    [Fact]
    public void Direction_flip_restarts_the_debounce()
    {
        var e = new AppMuteBannerEvaluator();
        e.Evaluate(Muted(), false, 0);
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Live(), true, 3000));   // flipped mid-window
        Assert.Equal(AppMuteBannerKind.None, e.Evaluate(Live(), true, 7999));   // 4999 into new window
        Assert.Equal(AppMuteBannerKind.AppLiveButMuted, e.Evaluate(Live(), true, 8000));
    }
}
```

- [ ] **Step 2: Verify RED**, then implement:

```csharp
namespace LocalScribe.App.Services;

public enum AppMuteBannerKind { None, AppMutedButRecording, AppLiveButMuted }

/// <summary>Pure debounced mismatch evaluator (design 2026-07-11 section 2.3). A mismatch must
/// persist >= 5 s of consecutive readings before it banners (normal mute choreography must not
/// flicker it); agreement or Unknown clears IMMEDIATELY. Fed a caller-supplied clock so it is
/// fully unit-testable.</summary>
public sealed class AppMuteBannerEvaluator
{
    private const long DebounceMs = 5000;
    private AppMuteBannerKind _pending = AppMuteBannerKind.None;
    private long _pendingSinceMs;
    private AppMuteBannerKind _current = AppMuteBannerKind.None;

    public AppMuteBannerKind Evaluate(AppMuteReading reading, bool localMuted, long nowMs)
    {
        var kind = reading.State switch
        {
            AppMuteState.Muted when !localMuted => AppMuteBannerKind.AppMutedButRecording,
            AppMuteState.Live when localMuted => AppMuteBannerKind.AppLiveButMuted,
            _ => AppMuteBannerKind.None,
        };
        if (kind == AppMuteBannerKind.None)
        {
            _pending = AppMuteBannerKind.None;
            return _current = AppMuteBannerKind.None;         // agreement/Unknown: clear instantly
        }
        if (kind != _pending) { _pending = kind; _pendingSinceMs = nowMs; }
        if (_current != kind && nowMs - _pendingSinceMs >= DebounceMs) _current = kind;
        return _current;
    }
}
```

- [ ] **Step 3: GREEN + both suites.**
- [ ] **Step 4: Commit.** `git commit -m "feat(app): debounced app-mute mismatch evaluator"`

### Task 7: `AppMuteWatcher` + `TrayMuteSignalSource`

**Files:**
- Create: `src/LocalScribe.App/Services/AppMuteWatcher.cs`
- Create: `src/LocalScribe.App/Services/TrayMuteSignalSource.cs`
- Create: `tests/LocalScribe.App.Tests/AppMuteWatcherTests.cs`

**Interfaces:**
- Consumes: `IAppMuteSignalSource` (Task 5).
- Produces: `sealed class AppMuteWatcher { AppMuteWatcher(IAppMuteSignalSource source, Func<bool> isRecording); event Action<AppMuteReading>? ReadingChanged; void Poll(); AppMuteReading Last { get; } }`. The 2 s DispatcherTimer lives in composition (Task 8), NOT in this class — tests drive `Poll()` directly.

- [ ] **Step 1: Failing tests** (fake source = tiny inline class implementing `IAppMuteSignalSource` with a settable `Next` reading):

```csharp
public sealed class AppMuteWatcherTests
{
    private sealed class FakeSource : IAppMuteSignalSource
    {
        public AppMuteReading Next = new(AppMuteState.Unknown, null);
        public int Reads;
        public AppMuteReading Read() { Reads++; return Next; }
    }

    [Fact]
    public void Polls_only_while_recording()
    {
        var src = new FakeSource();
        bool recording = false;
        var w = new AppMuteWatcher(src, () => recording);
        w.Poll();
        Assert.Equal(0, src.Reads);                             // not recording: no UIA touch at all
        recording = true;
        w.Poll();
        Assert.Equal(1, src.Reads);
    }

    [Fact]
    public void Raises_only_on_change_and_resets_to_unknown_when_not_recording()
    {
        var src = new FakeSource();
        bool recording = true;
        var w = new AppMuteWatcher(src, () => recording);
        var events = new List<AppMuteReading>();
        w.ReadingChanged += events.Add;

        src.Next = new(AppMuteState.Muted, "Webex");
        w.Poll(); w.Poll();                                     // second poll: same value, no event
        Assert.Single(events);

        recording = false;
        w.Poll();                                               // leaving Recording resets to Unknown
        Assert.Equal(2, events.Count);
        Assert.Equal(AppMuteState.Unknown, events[^1].State);
    }
}
```

- [ ] **Step 2: Verify RED**, then implement `AppMuteWatcher`: field `_last` (init Unknown); `Poll()`: if `!isRecording()` → if `_last.State != Unknown` set `_last = new(AppMuteState.Unknown, null)` and raise; return (source NOT read). Else read inside try/catch (catch → Unknown reading; belt-and-braces over the seam's own contract), compare to `_last`, raise `ReadingChanged` only on change. `Last` exposes `_last`.
- [ ] **Step 3: Implement `TrayMuteSignalSource`** (smoke-only; NOT unit-tested — like the device-mute endpoint path):

```csharp
using System.Windows.Automation;
namespace LocalScribe.App.Services;

/// <summary>Reads the Windows 11 call-mute tray signal (design 2026-07-11 section 2.1): finds
/// the taskbar mic NotifyItemIcon and parses its UIA Name. Fail-open by contract: any absence,
/// walk failure, or unparseable text is an Unknown reading - a UIA hiccup must never affect
/// recording. Smoke-verified via the runbook procedure; the parser carries the unit tests.</summary>
public sealed class TrayMuteSignalSource : IAppMuteSignalSource
{
    public AppMuteReading Read()
    {
        try
        {
            var tray = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
            if (tray is null) return new(AppMuteState.Unknown, null);
            var buttons = tray.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement b in buttons)
            {
                var reading = TrayTextParser.Parse(b.Current.Name);
                if (reading.State != AppMuteState.Unknown) return reading;
            }
            return new(AppMuteState.Unknown, null);
        }
        catch
        {
            return new(AppMuteState.Unknown, null);
        }
    }
}
```

(Task-4 evidence, `uia-dump-20260711-0916xx`: the element is a Button, `class='SystemTray.AccentButton'`, `id='SystemTrayIcon'` — but that id is SHARED by the Location/input/network tray buttons, so this loop-all-buttons-and-parse approach is exactly right: the parser's `"Microphone "` first-line prefix is the discriminator. Keep the walk as written; if `Shell_TrayWnd` yields no buttons on some Windows build, widen to the taskbar window the dump shows and cite the dump in the comment.)

- [ ] **Step 4: GREEN + both suites.**
- [ ] **Step 5: Commit.** `git commit -m "feat(app): app-mute watcher + tray signal source (fail-open)"`

### Task 8: VM banner + one-click action + composition wiring

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SessionViewModel.cs`
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml`
- Modify: `src/LocalScribe.App/App.xaml.cs` (or wherever SessionViewModel + window timers are composed — locate by content)
- Modify: `tests/LocalScribe.App.Tests/SessionViewModelTests.cs`

**Interfaces:**
- Consumes: `AppMuteWatcher` (Task 7), `AppMuteBannerEvaluator` (Task 6), existing `MuteLocalCommand`.
- Produces: `SessionViewModel.AppMuteBannerKind` (observable `AppMuteBannerKind`), `AppMuteBannerText` (observable string), `AppMuteActionLabel` (observable string).

- [ ] **Step 1: Failing VM test** (mirror the file's harness; drive the watcher via a fake source and manual `Poll()`; the VM takes the watcher as a new optional ctor parameter — copy a neighboring test's ctor call and add it):

```csharp
    [Fact]
    public async Task App_mute_mismatch_banners_after_debounce_and_action_resolves_it()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var src = new FakeAppMuteSignalSource();                 // add this tiny fake next to the test
        var watcher = new AppMuteWatcher(src, () => controller.State == SessionState.Recording);
        long now = 0;
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options(), appMuteWatcher: watcher, wallClock: () => now);
        await vm.StartCommand.ExecuteAsync(null);

        src.Next = new AppMuteReading(AppMuteState.Muted, "Webex");
        watcher.Poll();                                          // t=0: mismatch begins
        Assert.Equal(AppMuteBannerKind.None, vm.AppMuteBannerKind);
        now = 6000;
        watcher.Poll();                                          // same reading; VM re-evaluates on poll tick
        Assert.Equal(AppMuteBannerKind.AppMutedButRecording, vm.AppMuteBannerKind);
        Assert.Contains("Webex looks muted", vm.AppMuteBannerText);
        Assert.Equal("Mute my side", vm.AppMuteActionLabel);

        await vm.MuteLocalCommand.ExecuteAsync(null);            // the banner's action = existing command
        now = 6200;
        watcher.Poll();
        Assert.Equal(AppMuteBannerKind.None, vm.AppMuteBannerKind);   // resolution clears immediately

        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;
        vm.Dispose();
    }
```

Adapt the ctor-parameter names to the real signature you add (`appMuteWatcher`, `wallClock` — nullable, default null = feature dormant so every existing test compiles unchanged; if the seek-fix already added a `wallClock` param to PlaybackViewModel only, add a separate one here).

- [ ] **Step 2: Verify RED**, then implement in SessionViewModel: `[ObservableProperty]` for the three surface properties; a named handler `_onAppMuteReading = _ => _dispatch(ReevaluateAppMuteBanner);` subscribed to `watcher.ReadingChanged` in the ctor (skip wiring when the watcher param is null); `ReevaluateAppMuteBanner()` runs the evaluator with `(watcher.Last, IsLocalMuted, wallClock())`, sets kind/text/action-label (exact copy strings from Global Constraints, `<App>` = `watcher.Last.AppName ?? "the call app"`); ALSO call `ReevaluateAppMuteBanner` from the `_onLocalMuteChanged` dispatch body (muting/unmuting resolves a mismatch without waiting for the next poll) AND on every `ReadingChanged`. IMPORTANT: the evaluator needs re-evaluation on each poll even when the reading did not change (debounce expiry) — so the watcher must also expose a `Polled` event, or simpler: raise `ReadingChanged` semantics stay, and the VM ALSO subscribes a `Polled` event added to the watcher (`event Action? Polled;` raised at the end of every Poll() while recording). Add `Polled` to Task 7's watcher (one line) and drive `ReevaluateAppMuteBanner` from it. Reset the three properties in `StartAsync` alongside the other flag resets; detach both handlers in Dispose.
- [ ] **Step 3: XAML** — add to the warning-rows StackPanel in `LiveViewWindow.xaml` (after the device-muted banner):

```xml
                    <StackPanel Orientation="Horizontal"
                                Visibility="{Binding Session.AppMuteBannerVisible, Converter={StaticResource BoolToVis}}">
                        <TextBlock Text="{Binding Session.AppMuteBannerText}"
                                   Style="{StaticResource WarningText}" TextWrapping="Wrap"
                                   VerticalAlignment="Center" />
                        <Button Style="{StaticResource PillButton}" Margin="8,0,0,0"
                                Command="{Binding Session.MuteLocalCommand}"
                                Content="{Binding Session.AppMuteActionLabel}" />
                    </StackPanel>
```

Add `public bool AppMuteBannerVisible => AppMuteBannerKind != AppMuteBannerKind.None;` (notify it whenever the kind changes — `partial void OnAppMuteBannerKindChanged(...) => OnPropertyChanged(nameof(AppMuteBannerVisible));`).

- [ ] **Step 4: Composition** — where the app composes the session VM and windows (App.xaml.cs / CompositionRoot; locate by content): construct `new AppMuteWatcher(new TrayMuteSignalSource(), () => controller.State == SessionState.Recording)`, pass to the VM, and start a `DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }` whose Tick calls `watcher.Poll()` — created after the main window exists (UIA walk from the UI thread is fine; Poll is cheap and fail-open). Follow the app's existing singleton-wiring pattern.
- [ ] **Step 5: GREEN + both suites** (Core 447+2 known, App 391 + your new tests).
- [ ] **Step 6: Commit.** `git commit -m "feat(app): advisory app-mute banner with one-click resolve (tray signal, tier 3)"`

### Task 9: Docs + spec delta + smoke list

**Files:**
- Modify: `docs/specs/localscribe-specs.md`

- [ ] **Step 1: Spec deltas** (match the document's real headings/voice — read it first): section 8.x console indicators gains the tier-3 advisory banner (both directions, exact copy strings, 5 s debounce, 2 s poll, fail-open Unknown, "never writes markers / never gates recording" restated with a cross-ref to the section 2.1 exact-signals rule and the marker table); section 2.1 gains one sentence noting the call-app mute tier and that its actions route through the user's own click. State the three-tier model in one table mirroring the design doc.
- [ ] **Step 2: Build solution once** (docs-only proof: 0 warnings, no code churn). Commit: `git commit -m "docs(spec): tier-3 advisory app-mute banner + console pills"`
- [ ] **Step 3: Record the user smoke list in the SDD ledger** (not a code change): (1) pills render correctly light+dark, disabled look while Idle, red-tint Stop hover; (2) Ctrl+Shift+M toggles mute with the console focused, does nothing while Idle, never fires when Webex/Teams has focus; (3) during a real Webex call: mute in Webex → banner appears after ~5 s → click [Mute my side] → banner clears + muted state line appears; unmute both, then mute LocalScribe only → the inverse banner appears → [Unmute] clears it; (4) with no call running, no banner ever appears and the console behaves as before.

---

## Self-Review notes

- **Design coverage:** design section 4 → Tasks 1-3; section 2.1 → Tasks 5+7; 2.2 → Task 7; 2.3 → Task 6; 2.4 → Task 8; 2.5 → Tasks 5/7 (fail-open paths); section 5 sequencing → the Phase-2 gate (Task 4) + pre-flight; spec deltas → Task 9. Section 3 (rejected alternatives) requires no code by definition.
- **Type consistency:** `AppMuteState`/`AppMuteReading`/`IAppMuteSignalSource` (Task 5) consumed by Tasks 6-8 with identical shapes; `AppMuteBannerKind` (Task 6) consumed by Task 8; watcher signature (Task 7) matches Task 8's test usage incl. the `Polled` event Task 8 adds (noted in both tasks).
- **Known judgment calls encoded:** watcher never reads the source while not Recording (privacy: zero UIA activity outside sessions); banner action reuses `MuteLocalCommand` (toggle semantics cover both directions); `Polled` event added for debounce-expiry re-evaluation; Phase-2 STOP if Task 4's strings are missing.
