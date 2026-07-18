# Console Compact-Mode Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement design §6 (`feat/console-compact-mode`) of `docs/plans/2026-07-18-steno-round-design.md`: the Record console window gains a compact state — the SAME window object collapsed to a ~420×64 always-on-top pill (template swap via visibility triggers, `Topmost` true only while compact) showing the recording dot + elapsed timer, the last finalized live line (single line, end-trimmed, with the listening/warm-up hint carried in while no line exists yet), a mute pill that collapses the mute/device-mute/app-mute-advisory banners to a colored state + tooltip (never lost), Stop, and Expand. Drag-to-move via `DragMove`, position persisted and clamped to the visible virtual screen on restore. Entry points: a Compact toggle in the console's recording header, and a Settings checkbox "collapse the console when recording starts" (**default off**). Stop from the pill restores the full console.

**Architecture:** All testable logic is pure/VM-level; XAML + window geometry are a thin shell. A new `CompactConsoleViewModel` (App) composes the EXISTING `SessionViewModel` (elapsed, state, `IsLocalMuted`, `MicDeviceMuted`, `AppMuteBannerKind/Text`, `MuteLocalCommand`, `StopCommand`) and `TranscriptLinesViewModel` (`Lines`, `ShowListeningHint`) — it duplicates NO state, only derives: `IsCompact` (+ toggle command + the pure `NextCompact` transition rule implementing auto-compact-on-start and restore-on-stop), `LastLineText` (pure `PillLine` over the last `Lines` entry with the listening fallback), and the mute pill's `CompactMuteState`/`MuteTooltip` (pure `MutePill` priority mapping). Settings gains an additive `ConsoleSetting.CompactOnStart` (v3, no schema bump — the `SectionGapMs`/`DocxFooterText` precedent). Geometry lives in `LiveViewWindow` code-behind: enter/exit compact swaps Min/Width/Height + `ResizeMode` + zeroes the FluentWindow caption strip, loads/saves the pill position under a new `WindowStateStore` key `"consoleCompact"` clamped by the EXISTING (already unit-tested) `ScreenClamp` — the exact overlay-pill pattern (`OverlayWindow.xaml.cs:38-50`). `TrayIconHost` and `LiveViewWindow` ctors widen to carry the app's existing `WindowStateStore` singleton.
**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, xUnit.

## Global Constraints

- **Target branch:** `feat/console-compact-mode`, created off master **AFTER `feat/ux-round-2026-07-18` merges** (at plan time master @ 8872bc1 does NOT yet contain the ux-round; the branch MUST NOT be cut until it does). This plan file itself lands on the branch with a `docs(plans): ...` commit if not already there.
- **Merge order for the round (design §1):** 5th of 7 — after `fix/dedup-short-utterance-guard`, `feat/markdown-export`, `feat/deep-link`, `feat/call-detect-advisory`; before `feat/llm-foundation-summaries` and `feat/matter-qa`.
- **Merge reconciliation note:** branch 4 (`feat/call-detect-advisory`) also adds a Settings section. If merge conflicts arise in `src\LocalScribe.Core\Model\Settings.cs` (both branches append a settings record + property near `public PrivacySetting Privacy { get; init; } = new();`), `src\LocalScribe.App\SettingsPage.xaml` (both add UI near the Recording card), or `src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs` (both append commit-properties), the resolution is **both sections coexist** — keep the call-detect additions AND this branch's `ConsoleSetting`/`CompactConsoleOnStart`/checkbox side by side; nothing here is mutually exclusive with detection settings.
- **Anchor grounding:** line anchors are grounded @ the ux-round tip `82546aa` (the round design's stated `7605606` was amended to `82546aa`; the diff touches only `MattersPageViewModel.cs` — none of this plan's files). Re-verify every anchor by its QUOTED context before editing — if drifted (e.g. by the four earlier round branches), locate by the quoted code, not the line number.
- **LOCKED (design §6 + §1):** capture and Start/Stop/Pause semantics unchanged. The pill is UI-only over existing state: every compact control routes through the SAME commands the full console binds (`Session.MuteLocalCommand`, `Session.StopCommand`) — no task below adds any controller call, touches `StartAsync`/`StopAsync`/`PauseAsync` control flow, capture legs, or command CanExecute gates.
- **LOCKED (design §6):** the mute/device-mute/app-mute advisory banners must NEVER be lost in compact — they collapse to a colored state on the mute pill plus a tooltip that carries the banner's exact text. The advisory tier stays advisory: the pill never writes markers; its click IS `MuteLocalCommand` (same as the full banner's button).
- **Verbatim display (locked evidentiary rule):** no filtering/cleanup of transcript text — the pill's last line is the line's verbatim text (single-line collapsed + end-trimmed for layout only; ellipsized visually by `TextTrimming`, full text in its tooltip). Markers surface too.
- **No global hotkeys** (locked project decision) — nothing here adds any. The existing window-scoped Ctrl+Shift+M `KeyBinding` (`LiveViewWindow.xaml:10-16`) keeps working in compact because `Window.InputBindings` are on the window, not the swapped template.
- **Default OFF (design §6):** auto-compact-on-start ships opt-in; a user who never opens Settings sees zero behavior change until they click the new Compact button.
- 0-warning build gate must hold.
- Tests: xUnit. Filtered run: `dotnet test "<testproj>" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\compact-mode\`
- **Full App-suite gate runs (XamlHygieneTests):** `RepoPaths.SolutionRoot()` walks UP from the test assembly directory to find `.git`, so an App-suite run that includes `XamlHygieneTests` MUST NOT use the Temp isolated BaseOutputPath (it sits outside the repo — 5 false failures). Run full App-suite gates with the default repo-internal output path; keep the isolated path for filtered runs. If the default path hits MSB3027 (app running, locked bin), report and wait — never kill processes.
- IMPORTANT: the LocalScribe app may be running and LOCK bin DLLs (MSB3027 copy error — NOT a compile error). Always use the isolated BaseOutputPath above (every command below already appends it); NEVER kill the user's running app or any other process.
- Never use Unicode emojis in test code or scripts (project rule). Every new UI string in this plan is plain ASCII.
- Test projects: App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. App.Tests `<Compile Include>`-links `LiveTestDoubles.cs` (with `GatedEngineFactory`, `LiveTestDoubles.MakeController/Options`) and `FakeTranscriptionEngine.cs` from Core.Tests, so those doubles compile INTO App.Tests. There is NO `InternalsVisibleTo` anywhere in this repo — new members that tests call directly must be `public`.
- Core suite has 2 known pre-existing fixture failures (unrelated); App suite must be fully green.
- Commit style: `feat(core)`/`feat(app)`/`test(...)`/`docs(...)`; every commit message ends with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```

---

### Task 1: Core — `ConsoleSetting.CompactOnStart` on Settings (additive v3)
**Files:**
- Modify `src\LocalScribe.Core\Model\Settings.cs` (property after line 38 `public PrivacySetting Privacy { get; init; } = new();`; record after line 49 `public sealed record PrivacySetting { public bool ExcludeWindowsFromCapture { get; init; } = true; }`).
- Test `tests\LocalScribe.Core.Tests\SettingsTests.cs` (add one `[Fact]` after `DocxFooterText_defaults_and_roundtrips`, which ends at line 185, before the `CleanParent` helper at line 187 — the file already has the `SettingsStore` usings and the temp-path pattern).

**Interfaces:**
- Produces: `public ConsoleSetting Settings.Console { get; init; } = new();` and `public sealed record ConsoleSetting { public bool CompactOnStart { get; init; } }` — default `false` (design §6: the Settings option ships OFF). Additive to schema v3, no bump/migration (the `SectionGapMs`/`DocxFooterText` precedent: absent field loads at the default). JSON shape: `"console": { "compactOnStart": true }` (SettingsStore already writes camelCase, indented).
- Consumes: nothing new. No name collision: `Settings.cs` never references `System.Console`, and consumers access it as `settings.Console`.

Steps:
- [ ] **Write the failing test.** In `tests\LocalScribe.Core.Tests\SettingsTests.cs`, insert after the closing brace of `DocxFooterText_defaults_and_roundtrips` (line 185) and before `private static void CleanParent`:
```csharp

    [Fact]
    public async Task Console_compact_on_start_defaults_off_and_roundtrips()
    {
        // Design 2026-07-18 section 6: "collapse console when recording starts" ships DEFAULT OFF.
        // Additive v3 field (SectionGapMs/DocxFooterText precedent) - no schema bump, absent field
        // loads at the default.
        Assert.False(new Settings().Console.CompactOnStart);

        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            await new SettingsStore(path).SaveAsync(
                new Settings { Console = new ConsoleSetting { CompactOnStart = true } }, default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"compactOnStart\": true", json);
            var back = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.True(back.Console.CompactOnStart);
        }
        finally { CleanParent(path); }
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Console_compact_on_start" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\compact-mode\` — expected: `error CS1061: 'Settings' does not contain a definition for 'Console'` (plus `error CS0246: The type or namespace name 'ConsoleSetting' could not be found` at the initializer).
- [ ] **Add the property.** In `src\LocalScribe.Core\Model\Settings.cs`, the record currently ends:
```csharp
    /// <summary>v3 (Stage 4, design section 2): capture exclusion for transcript-bearing windows.</summary>
    public PrivacySetting Privacy { get; init; } = new();
}
```
Replace with:
```csharp
    /// <summary>v3 (Stage 4, design section 2): capture exclusion for transcript-bearing windows.</summary>
    public PrivacySetting Privacy { get; init; } = new();
    /// <summary>v3 (Steno round, design 2026-07-18 section 6): Record-console behavior. Additive -
    /// existing v3 files without it load at the defaults, so no schema bump/migration is required
    /// (the SectionGapMs precedent).</summary>
    public ConsoleSetting Console { get; init; } = new();
}
```
- [ ] **Add the record.** In the same file, the trailing record block currently ends:
```csharp
public sealed record PrivacySetting { public bool ExcludeWindowsFromCapture { get; init; } = true; }
```
Replace with:
```csharp
public sealed record PrivacySetting { public bool ExcludeWindowsFromCapture { get; init; } = true; }
/// <summary>Record-console options (design 2026-07-18 section 6). CompactOnStart: collapse the
/// console to the compact always-on-top pill when recording starts - DEFAULT OFF (opt-in).</summary>
public sealed record ConsoleSetting { public bool CompactOnStart { get; init; } }
```
- [ ] **Run tests and see PASS.** Same filter as above — expected: 1 passed. Then run the whole class to prove no regression: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SettingsTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\compact-mode\`.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Model/Settings.cs tests/LocalScribe.Core.Tests/SettingsTests.cs
git commit -m "feat(core): additive ConsoleSetting.CompactOnStart (default off) for console compact mode

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: App — `CompactConsoleViewModel` (compact state, auto-compact, pill derivations) + clamp pins
**Files:**
- Create `src\LocalScribe.App\ViewModels\CompactConsoleViewModel.cs`.
- Create `tests\LocalScribe.App.Tests\CompactConsoleViewModelTests.cs`.
- Modify `tests\LocalScribe.App.Tests\ScreenClampTests.cs` (append two pin tests before the closing brace at line 37 — the existing helper already covers the design's clamp cases for a 220×56 pill; these rows pin the exact 420×64 compact-pill cases the new caller relies on: monitor-removed, partially-off, negative-origin multi-monitor).

**Interfaces:**
- Produces (all `public` — no InternalsVisibleTo in this repo, tests call them):
  - `public enum CompactMuteState { Normal, Muted, DeviceMuted, AppMuteAdvisory }` — the mute pill's four visual states (design §6: normal / muted / device-mute-advisory / app-mute-advisory, distinct brush keys mapped in Task 4's XAML).
  - `public sealed partial class CompactConsoleViewModel : ObservableObject, IDisposable` with ctor `(SessionViewModel session, TranscriptLinesViewModel lines, ISettingsService settings)`; observable `bool IsCompact`, `string LastLineText`, `CompactMuteState MuteState`, `string MuteTooltip`; `IRelayCommand ToggleCompactCommand`; consts `PillWidth = 420`, `PillHeight = 64`, `ListeningText` (the console's exact existing empty-state string — the "Preparing/warm-up carried into the pill" surface: no `Preparing` state exists in this app; the listening hint IS the console's warm-up indicator since the record-console-polish round).
  - `public static bool NextCompact(SessionState prev, SessionState next, bool current, bool compactOnStart)` — the WHOLE compact-transition rule: Idle→Recording returns `current || compactOnStart` (auto-compact honors the setting); any state that is not Recording/Paused returns `false` (Stop from the pill restores the full console — Finalizing already restores, so the finished session is seen full-size); otherwise unchanged (Pause/Resume keep the pill: mute/resume semantics stay available there).
  - `public static (CompactMuteState State, string Tooltip) MutePill(bool localMuted, bool deviceMuted, AppMuteBannerKind advisoryKind, string advisoryText)` — priority mapping: deliberate mute > device mute > tray advisory > normal (mirrors `SessionViewModel`'s own suppression order: the controller already clears `MicDeviceMuted` while deliberately muted, and the advisory evaluator already clears on agreement). The advisory tooltip IS the banner's exact `AppMuteBannerText` — never lost (locked rule).
  - `public static string PillLine(TranscriptLineViewModel? last, bool listening)` — listening fallback, else `""` for no line, else the verbatim line as `"Speaker: text"` (markers have `Speaker == ""` → text only), newlines collapsed to spaces + `TrimEnd()` (single line, end-trimmed).
- Consumes (reuse-only, NO duplicated state): `SessionViewModel.State/Elapsed/IsLocalMuted/MicDeviceMuted/AppMuteBannerKind/AppMuteBannerText` via `PropertyChanged`; `TranscriptLinesViewModel.Lines` (`CollectionChanged`) + `ShowListeningHint` (`PropertyChanged`); `ISettingsService.Current.Console.CompactOnStart` (Task 1), read live at each transition. Elapsed is NOT mirrored — Task 4's XAML binds `Session.Elapsed` directly.
- Lifetime: named handlers + idempotent `Dispose` (the `SessionViewModel`/`LiveViewWindow` precedent); in production the VM lives as long as the hide-on-close singleton console window.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\CompactConsoleViewModelTests.cs`:
```csharp
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class CompactConsoleViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-compact-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (CompactConsoleViewModel Compact, SessionViewModel Session, TranscriptLinesViewModel Lines)
        MakeVms(SessionController controller, bool compactOnStart)
    {
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var settings = new FakeSettingsService(new Settings
        { Console = new ConsoleSetting { CompactOnStart = compactOnStart } });
        var lines = new TranscriptLinesViewModel(controller, settings, a => a());
        return (new CompactConsoleViewModel(session, lines, settings), session, lines);
    }

    [Theory]
    [InlineData(SessionState.Idle, SessionState.Recording, false, true, true)]        // opt-in auto-compact
    [InlineData(SessionState.Idle, SessionState.Recording, false, false, false)]      // default OFF stays full
    [InlineData(SessionState.Recording, SessionState.Paused, true, false, true)]      // pause keeps the pill
    [InlineData(SessionState.Paused, SessionState.Recording, true, false, true)]      // resume keeps the pill
    [InlineData(SessionState.Recording, SessionState.Finalizing, true, true, false)]  // stop restores full...
    [InlineData(SessionState.Finalizing, SessionState.Idle, true, true, false)]       // ...and idle stays full
    [InlineData(SessionState.Recording, SessionState.Recording, true, false, true)]   // no transition: unchanged
    public void NextCompact_encodes_the_whole_transition_rule(
        SessionState prev, SessionState next, bool current, bool optIn, bool expected)
        => Assert.Equal(expected, CompactConsoleViewModel.NextCompact(prev, next, current, optIn));

    [Fact]
    public void MutePill_maps_states_with_deliberate_mute_winning()
    {
        // Design 2026-07-18 section 6 (locked): advisory banners collapse to a colored pill state
        // plus a tooltip - NEVER lost. Priority mirrors the full console's own suppression order.
        Assert.Equal((CompactMuteState.Normal, "Mute my side (Ctrl+Shift+M)"),
            CompactConsoleViewModel.MutePill(false, false, AppMuteBannerKind.None, ""));

        var muted = CompactConsoleViewModel.MutePill(true, false, AppMuteBannerKind.None, "");
        Assert.Equal(CompactMuteState.Muted, muted.State);
        Assert.Contains("not being recorded", muted.Tooltip);

        var device = CompactConsoleViewModel.MutePill(false, true, AppMuteBannerKind.None, "");
        Assert.Equal(CompactMuteState.DeviceMuted, device.State);
        Assert.Contains("microphone device is muted", device.Tooltip);

        var advisory = CompactConsoleViewModel.MutePill(false, false,
            AppMuteBannerKind.AppMutedButRecording,
            "Webex looks muted - LocalScribe is still recording your side.");
        Assert.Equal(CompactMuteState.AppMuteAdvisory, advisory.State);
        Assert.Equal("Webex looks muted - LocalScribe is still recording your side.",
            advisory.Tooltip);                                   // the exact banner text, never lost

        Assert.Equal(CompactMuteState.Muted,
            CompactConsoleViewModel.MutePill(true, true, AppMuteBannerKind.AppLiveButMuted, "x").State);
        Assert.Equal(CompactMuteState.DeviceMuted,
            CompactConsoleViewModel.MutePill(false, true, AppMuteBannerKind.AppLiveButMuted, "x").State);
    }

    [Fact]
    public void PillLine_renders_a_single_end_trimmed_line()
    {
        Assert.Equal(CompactConsoleViewModel.ListeningText,
            CompactConsoleViewModel.PillLine(null, listening: true));       // warm-up carried into the pill
        Assert.Equal("", CompactConsoleViewModel.PillLine(null, listening: false));
        Assert.Equal("Sam: Hello there",
            CompactConsoleViewModel.PillLine(new TranscriptLineViewModel("00:05", "Sam", "Hello there  ", false), false));
        // Markers carry no speaker in the line VM - text only (verbatim; markers surface too).
        Assert.Equal("recording paused",
            CompactConsoleViewModel.PillLine(new TranscriptLineViewModel("00:09", "", "recording paused", true), false));
        // Any embedded newline collapses: the pill is a SINGLE line, end-trimmed (layout only).
        Assert.Equal("Sam: line one line two",
            CompactConsoleViewModel.PillLine(new TranscriptLineViewModel("00:12", "Sam", "line one\r\nline two\n", false), false));
    }

    [Fact]
    public async Task Auto_compact_on_start_honors_the_setting_and_stop_restores_full()
    {
        // GatedEngineFactory holds the engine build closed so no transcript line can land - the
        // listening fallback (the pill's warm-up surface) is observable and deterministic.
        var gated = new GatedEngineFactory();
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var (compact, session, _) = MakeVms(controller, compactOnStart: true);
        Assert.False(compact.IsCompact);                          // idle: always the full console

        await session.StartCommand.ExecuteAsync(null);
        Assert.True(compact.IsCompact);                           // opt-in honored on Idle->Recording
        Assert.Equal(CompactConsoleViewModel.ListeningText, compact.LastLineText);

        gated.CreateGate.Set();                                   // release the engine before pausing/stopping
        await session.PauseResumeCommand.ExecuteAsync(null);      // pause
        Assert.True(compact.IsCompact);                           // pause keeps the pill
        await session.StopCommand.ExecuteAsync(null);
        Assert.False(compact.IsCompact);                          // stop from the pill restores the full console
        session.Dispose(); compact.Dispose();
    }

    [Fact]
    public async Task Default_off_start_stays_full_and_the_toggle_flips_both_ways()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var (compact, session, _) = MakeVms(controller, compactOnStart: false);
        await session.StartCommand.ExecuteAsync(null);
        Assert.False(compact.IsCompact);                          // DEFAULT OFF (design section 6)

        compact.ToggleCompactCommand.Execute(null);               // header Compact button
        Assert.True(compact.IsCompact);
        compact.ToggleCompactCommand.Execute(null);               // pill Expand button
        Assert.False(compact.IsCompact);

        compact.ToggleCompactCommand.Execute(null);
        await session.StopCommand.ExecuteAsync(null);
        Assert.False(compact.IsCompact);                          // stop ALWAYS restores the full console
        session.Dispose(); compact.Dispose();
    }

    [Fact]
    public async Task Constructed_mid_recording_applies_the_auto_compact_choice()
    {
        // Production timing: the app opens the console ON Idle->Recording, so the hide-on-close
        // singleton window (and this VM) can be constructed AFTER the transition already happened -
        // the ctor must evaluate auto-compact against the CURRENT state, not only future events.
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        await session.StartCommand.ExecuteAsync(null);

        var settings = new FakeSettingsService(new Settings
        { Console = new ConsoleSetting { CompactOnStart = true } });
        var lines = new TranscriptLinesViewModel(controller, settings, a => a());
        var compact = new CompactConsoleViewModel(session, lines, settings);
        Assert.True(compact.IsCompact);

        await session.StopCommand.ExecuteAsync(null);
        Assert.False(compact.IsCompact);
        session.Dispose(); compact.Dispose();
    }

    [Fact]
    public void Last_line_follows_the_live_list()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var (compact, _, lines) = MakeVms(controller, compactOnStart: false);
        Assert.Equal("", compact.LastLineText);                   // idle + empty: no hint, no line

        lines.Lines.Add(new TranscriptLineViewModel("00:05", "Sam", "first line", false));
        Assert.Equal("Sam: first line", compact.LastLineText);
        lines.Lines.Add(new TranscriptLineViewModel("00:09", "", "recording paused", true));
        Assert.Equal("recording paused", compact.LastLineText);   // markers surface too (verbatim rule)
    }

    [Fact]
    public async Task Mute_pill_follows_the_sessions_mute_state()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var (compact, session, _) = MakeVms(controller, compactOnStart: false);
        Assert.Equal(CompactMuteState.Normal, compact.MuteState);

        await session.StartCommand.ExecuteAsync(null);
        await session.MuteLocalCommand.ExecuteAsync(null);
        // LocalMuteChanged marshals through the synchronous dispatch, but the controller raises it
        // from the mute call's own continuation - bound the wait on the observable effect.
        Assert.True(SpinWait.SpinUntil(() => compact.MuteState == CompactMuteState.Muted, TimeSpan.FromSeconds(2)),
            "mute never reached the compact pill");
        Assert.Contains("unmute", compact.MuteTooltip, StringComparison.OrdinalIgnoreCase);

        await session.MuteLocalCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => compact.MuteState == CompactMuteState.Normal, TimeSpan.FromSeconds(2)),
            "unmute never reached the compact pill");
        await session.StopCommand.ExecuteAsync(null);
        session.Dispose(); compact.Dispose();
    }
}
```
- [ ] **Add the clamp pins.** In `tests\LocalScribe.App.Tests\ScreenClampTests.cs`, insert before the class's closing brace (after `Negative_virtual_origin_multimonitor_is_respected`, which ends at line 36):
```csharp

    // Compact-console pill (design 2026-07-18 section 6): pin the exact clamp behavior the 420x64
    // pill's restore path relies on (Task 4 loads the remembered "consoleCompact" position through
    // this SAME helper, the overlay-pill precedent). PASS immediately - these pin existing behavior
    // for the new caller; a future ScreenClamp change that breaks the pill now fails loudly here.
    [Theory]
    [InlineData(2500, 400, 1500, 400)]      // saved on a since-removed right monitor -> pulled inside (1920-420)
    [InlineData(1800, 1050, 1500, 1016)]    // partially off bottom-right -> fully visible (1080-64)
    [InlineData(-60, -20, 0, 0)]            // partially off top-left -> snapped to the origin
    public void Compact_pill_restore_clamps_into_the_virtual_screen(double x, double y, double ex, double ey)
    {
        var (cx, cy) = ScreenClamp.Clamp(x, y, 420, 64, 0, 0, 1920, 1080);
        Assert.Equal(ex, cx);
        Assert.Equal(ey, cy);
    }

    [Fact]
    public void Compact_pill_negative_origin_multimonitor_position_is_preserved()
    {
        // Left-of-primary monitor: virtual screen origin (-1920, 0), span 3840x1080. A valid
        // remembered pill position on the left monitor must NOT be dragged onto the primary.
        var (cx, cy) = ScreenClamp.Clamp(-1500, 900, 420, 64, -1920, 0, 3840, 1080);
        Assert.Equal(-1500, cx);
        Assert.Equal(900, cy);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~CompactConsole" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\compact-mode\` — expected: `error CS0246: The type or namespace name 'CompactConsoleViewModel' could not be found` (plus CS0246 on `CompactMuteState`). (The build failure takes the whole test assembly down, so the clamp pins cannot run yet either.)
- [ ] **Write the implementation.** Create `src\LocalScribe.App\ViewModels\CompactConsoleViewModel.cs`:
```csharp
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
namespace LocalScribe.App.ViewModels;

/// <summary>The compact pill's mute-button visual state (design 2026-07-18 section 6): the full
/// console's mute/device-mute/app-mute-advisory banners collapse to a colored state + tooltip -
/// never lost (locked rule). Task 4's XAML maps each value to a distinct theme brush.</summary>
public enum CompactMuteState { Normal, Muted, DeviceMuted, AppMuteAdvisory }

/// <summary>Compact-mode state for the Record console (design 2026-07-18 section 6): the SAME
/// window collapsed to a ~420x64 always-on-top pill. UI-only over EXISTING state (locked rule -
/// capture and Start/Stop/Pause semantics unchanged): this VM duplicates nothing, it only derives
/// pill surfaces from the shared SessionViewModel + TranscriptLinesViewModel, and every pill
/// control binds the SAME commands the full console binds (MuteLocalCommand / StopCommand).
/// WPF-free; in production it lives as long as the hide-on-close singleton console window.</summary>
public sealed partial class CompactConsoleViewModel : ObservableObject, IDisposable
{
    /// <summary>Pill geometry (design section 6: ~420x64). Consts so the window code-behind and
    /// the clamp math can never drift from the XAML template's layout.</summary>
    public const double PillWidth = 420;
    public const double PillHeight = 64;
    /// <summary>The console's EXISTING empty-state line (record-console-polish round, section 5
    /// item 1) - the pill's warm-up/"preparing" surface: while Recording with zero lines (model
    /// still warming up, or nobody has spoken), the last-line slot shows this instead of blank.</summary>
    public const string ListeningText = "Listening - transcript appears a few seconds after speech.";

    private readonly SessionViewModel _session;
    private readonly TranscriptLinesViewModel _lines;
    private readonly ISettingsService _settings;
    // Named (not lambdas) so Dispose can detach them - the session/lines VMs are the shared,
    // app-lifetime instances every console surface binds (the SessionViewModel precedent).
    private readonly PropertyChangedEventHandler _onSessionChanged;
    private readonly PropertyChangedEventHandler _onLinesChanged;
    private readonly NotifyCollectionChangedEventHandler _onLineListChanged;
    private SessionState _lastState;
    private bool _disposed;

    /// <summary>True while the console renders as the compact pill. The window binds Topmost and
    /// both templates' visibility to this; the code-behind swaps geometry on its flips.</summary>
    [ObservableProperty] private bool _isCompact;
    /// <summary>The last finalized live line, single-line end-trimmed (see PillLine); the
    /// listening/warm-up hint while Recording with no line yet; "" otherwise.</summary>
    [ObservableProperty] private string _lastLineText = "";
    /// <summary>The mute pill's visual state (see MutePill). Advisory states carry the full
    /// banner's meaning in MuteTooltip - the banner is collapsed, never lost (locked rule).</summary>
    [ObservableProperty] private CompactMuteState _muteState = CompactMuteState.Normal;
    /// <summary>Tooltip for the mute pill; for the app-mute advisory it is the banner's EXACT text.</summary>
    [ObservableProperty] private string _muteTooltip = "Mute my side (Ctrl+Shift+M)";

    /// <summary>Compact toggle - bound by BOTH entry points (the header's Compact button and the
    /// pill's Expand button).</summary>
    public IRelayCommand ToggleCompactCommand { get; }

    public CompactConsoleViewModel(SessionViewModel session, TranscriptLinesViewModel lines,
        ISettingsService settings)
    {
        (_session, _lines, _settings) = (session, lines, settings);
        ToggleCompactCommand = new RelayCommand(() => IsCompact = !IsCompact);
        _lastState = session.State;
        // The console window (and this VM) is constructed lazily - in production often AFTER the
        // open-console-on-start hook already flipped State to Recording - so the auto-compact
        // decision is also evaluated against the CURRENT state, not only future transitions.
        IsCompact = NextCompact(SessionState.Idle, session.State, false,
            settings.Current.Console.CompactOnStart);

        _onSessionChanged = (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SessionViewModel.State):
                    var next = _session.State;
                    IsCompact = NextCompact(_lastState, next, IsCompact,
                        _settings.Current.Console.CompactOnStart);
                    _lastState = next;
                    break;
                case nameof(SessionViewModel.IsLocalMuted):
                case nameof(SessionViewModel.MicDeviceMuted):
                case nameof(SessionViewModel.AppMuteBannerKind):
                case nameof(SessionViewModel.AppMuteBannerText):
                    RefreshMutePill();
                    break;
            }
        };
        _onLinesChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptLinesViewModel.ShowListeningHint))
                RefreshLastLine();
        };
        _onLineListChanged = (_, _) => RefreshLastLine();
        session.PropertyChanged += _onSessionChanged;
        lines.PropertyChanged += _onLinesChanged;
        lines.Lines.CollectionChanged += _onLineListChanged;
        RefreshMutePill();
        RefreshLastLine();
    }

    /// <summary>The WHOLE compact-transition rule (design 2026-07-18 section 6), pure: on
    /// Idle->Recording the pill opens if it already was open or the user opted into
    /// collapse-on-start (DEFAULT OFF); leaving the live states (Stop -> Finalizing/Idle) ALWAYS
    /// restores the full console, so the finished session is reviewed full-size; Pause/Resume keep
    /// whatever the user chose (mute/resume semantics stay reachable on the pill).</summary>
    public static bool NextCompact(SessionState prev, SessionState next, bool current, bool compactOnStart)
        => prev == SessionState.Idle && next == SessionState.Recording ? current || compactOnStart
         : next is SessionState.Recording or SessionState.Paused ? current
         : false;

    /// <summary>Priority mapping for the mute pill (design section 6, locked: banners collapse to
    /// a colored state + tooltip - NEVER lost). Deliberate mute wins (it is the user's own choice
    /// and the full console's banners already yield to it: the controller suppresses device-mute
    /// reporting while deliberately muted, and the advisory evaluator clears on agreement), then
    /// the device-mute fact, then the tray ADVISORY (tooltip = the banner's exact text). The pill
    /// itself stays advisory-safe: its click routes through MuteLocalCommand, never a marker.</summary>
    public static (CompactMuteState State, string Tooltip) MutePill(
        bool localMuted, bool deviceMuted, AppMuteBannerKind advisoryKind, string advisoryText)
    {
        if (localMuted)
            return (CompactMuteState.Muted,
                "Your side is muted - not being recorded. Click to unmute (Ctrl+Shift+M).");
        if (deviceMuted)
            return (CompactMuteState.DeviceMuted,
                "Your microphone device is muted - nothing is being recorded from it.");
        if (advisoryKind != AppMuteBannerKind.None)
            return (CompactMuteState.AppMuteAdvisory, advisoryText);
        return (CompactMuteState.Normal, "Mute my side (Ctrl+Shift+M)");
    }

    /// <summary>The pill's last-line text, pure: the listening/warm-up hint while the live list is
    /// in its Recording-and-empty window; else the last line VERBATIM (locked evidentiary rule: no
    /// content filtering) - "Speaker: text" for segments, bare text for markers (their Speaker is
    /// "") - collapsed to a SINGLE line and end-trimmed for the 64px layout only. Visual overflow
    /// is ellipsized by the XAML's TextTrimming with the full text in a tooltip.</summary>
    public static string PillLine(TranscriptLineViewModel? last, bool listening)
    {
        if (listening) return ListeningText;
        if (last is null) return "";
        string text = last.Text.ReplaceLineEndings(" ").TrimEnd();
        return string.IsNullOrEmpty(last.Speaker) ? text : $"{last.Speaker}: {text}";
    }

    private void RefreshMutePill()
        => (MuteState, MuteTooltip) = MutePill(_session.IsLocalMuted, _session.MicDeviceMuted,
            _session.AppMuteBannerKind, _session.AppMuteBannerText);

    private void RefreshLastLine()
        => LastLineText = PillLine(_lines.Lines.Count > 0 ? _lines.Lines[^1] : null,
            _lines.ShowListeningHint);

    /// <summary>Detaches the ctor's subscriptions from the shared app-lifetime VMs (the
    /// SessionViewModel Dispose precedent). Idempotent - a second Dispose() is a safe no-op.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.PropertyChanged -= _onSessionChanged;
        _lines.PropertyChanged -= _onLinesChanged;
        _lines.Lines.CollectionChanged -= _onLineListChanged;
    }
}
```
- [ ] **Run tests and see PASS.** Same `~CompactConsole` filter — expected: 8 passed (the 7-row theory counts as 7 cases; total displayed test count 14). Then run the clamp pins: `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ScreenClampTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\compact-mode\` — expected: all passed (the new rows PASS immediately by design — they pin existing behavior for the new caller).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/CompactConsoleViewModel.cs tests/LocalScribe.App.Tests/CompactConsoleViewModelTests.cs tests/LocalScribe.App.Tests/ScreenClampTests.cs
git commit -m "feat(app): CompactConsoleViewModel - compact state, auto-compact rule, pill line + mute-state mapping

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: App — Settings checkbox "collapse the console when recording starts"
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs` (insert one commit-property after the `OverlayExcludeFromCapture` property block, lines 371–379).
- Modify `src\LocalScribe.App\SettingsPage.xaml` — the REAL settings form (the UserControl at the project root; `Pages\SettingsPage.xaml` is only a thin Page wrapper around it): add one `CheckBox` at the end of the Recording card (after the audio-retention row closing at line 89).
- Test `tests\LocalScribe.App.Tests\SettingsPageViewModelTests.cs` (add one `[Fact]` inside the existing class, reusing the in-file `MakeVm` and `_settings`).

**Interfaces:**
- Produces: `public bool SettingsPageViewModel.CompactConsoleOnStart` — the standard read-from-`Current`, `Commit(s => s with { ... })`, `OnPropertyChanged()` auto-save pattern (the `OverlayEnabled` shape), writing `Settings.Console.CompactOnStart` (Task 1).
- Consumes: existing `Commit`/`LastSave` chain (`SettingsPageViewModel.cs:404-414`), `ISettingsService.Current`.
- The banned-names reflection pin (`RecordingIndicator`/`Hotkey`/`AutoDetect`, `SettingsPageViewModelTests.cs:246-248`) is unaffected — the new name matches none of them.
- Read-at-Start-time semantics: `CompactConsoleViewModel` reads `Current.Console.CompactOnStart` live at each Idle→Recording transition, so the checkbox needs no restart/apply note.

Steps:
- [ ] **Write the failing test.** Append inside `SettingsPageViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\SettingsPageViewModelTests.cs`:
```csharp
    [Fact]
    public async Task Compact_console_on_start_commits_through_settings()
    {
        // Design 2026-07-18 section 6: the collapse-on-start option ships DEFAULT OFF and
        // auto-saves through the same Commit/LastSave chain as every other field.
        var vm = MakeVm();
        Assert.False(vm.CompactConsoleOnStart);

        vm.CompactConsoleOnStart = true;
        await vm.LastSave;
        Assert.True(_settings.Current.Console.CompactOnStart);
        Assert.True(vm.CompactConsoleOnStart);

        vm.CompactConsoleOnStart = false;
        await vm.LastSave;
        Assert.False(_settings.Current.Console.CompactOnStart);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~Compact_console_on_start_commits" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\compact-mode\` — expected: `error CS1061: 'SettingsPageViewModel' does not contain a definition for 'CompactConsoleOnStart'`.
- [ ] **Add the property.** In `src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs`, the `OverlayExcludeFromCapture` property currently reads (lines 371–379):
```csharp
    public bool OverlayExcludeFromCapture
    {
        get => _settings.Current.Overlay.ExcludeFromCapture;
        set
        {
            Commit(s => s with { Overlay = s.Overlay with { ExcludeFromCapture = value } });
            OnPropertyChanged();
        }
    }
```
Immediately after its closing brace insert:
```csharp

    /// <summary>Design 2026-07-18 section 6: collapse the Record console to the compact
    /// always-on-top pill when recording starts. DEFAULT OFF (opt-in). Read live by
    /// CompactConsoleViewModel at each Start, so no restart/apply note is needed.</summary>
    public bool CompactConsoleOnStart
    {
        get => _settings.Current.Console.CompactOnStart;
        set
        {
            Commit(s => s with { Console = s.Console with { CompactOnStart = value } });
            OnPropertyChanged();
        }
    }
```
- [ ] **Add the checkbox.** In `src\LocalScribe.App\SettingsPage.xaml`, the Recording card currently ends (lines 85–91):
```xml
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Audio retention" Style="{StaticResource FieldLabel}" />
                        <TextBlock Text="{Binding AudioRetentionDisplay, Mode=OneWay}"
                                   VerticalAlignment="Center" />
                    </StackPanel>
                </StackPanel>
            </ui:Card>
```
Replace with:
```xml
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Audio retention" Style="{StaticResource FieldLabel}" />
                        <TextBlock Text="{Binding AudioRetentionDisplay, Mode=OneWay}"
                                   VerticalAlignment="Center" />
                    </StackPanel>
                    <!-- Console compact mode (design 2026-07-18 section 6). DEFAULT OFF; read at
                         each Start, so it applies to the next recording with no restart. -->
                    <CheckBox Content="Collapse the record console to a compact pill when recording starts"
                              IsChecked="{Binding CompactConsoleOnStart}" Margin="0,4,0,4" />
                </StackPanel>
            </ui:Card>
```
- [ ] **Run tests and see PASS.** Same filter — expected: 1 passed. Then run the whole class to prove no regression (the banned-names pin lives here): `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SettingsPageViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\compact-mode\`.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs src/LocalScribe.App/SettingsPage.xaml tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs
git commit -m "feat(app): Settings checkbox - collapse the record console to a compact pill on start (default off)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: App — compact template + geometry on `LiveViewWindow`, `WindowStateStore` wiring
**Files:**
- Modify `src\LocalScribe.App\LiveViewWindow.xaml` (four edits: window attrs, root wrap, compact pill template, header Compact button).
- Modify `src\LocalScribe.App\LiveViewWindow.xaml.cs` (ctor widened + compact geometry members; anchors: usings 1–9, record line 19, fields 21–26, ctor 28–52, `OnClosing` 119–123).
- Modify `src\LocalScribe.App\TrayIconHost.cs` (ctor widened to carry `WindowStateStore`; anchors: fields 20–28, ctor 30–41, `OpenLiveView` 110–115).
- Modify `src\LocalScribe.App\App.xaml.cs` (one call-site line, ~476).
- No new unit test (window geometry/XAML rendering are not unit-tested here — the testable logic all landed in Tasks 1–3). The gate is: 0-warning build + full App + Core suites green (incl. `XamlHygieneTests`) + the manual smoke below.

**Interfaces:**
- Consumes: `Compact.IsCompact`/`LastLineText`/`MuteState`/`MuteTooltip`/`ToggleCompactCommand` + `CompactConsoleViewModel.PillWidth/PillHeight` (Task 2); existing `Session.Elapsed`/`Session.State`/`Session.IsLocalMuted`/`Session.MuteLocalCommand`/`Session.StopCommand` (bound exactly as the full console binds them — UI-only locked rule); existing `WindowStateStore` (`Load`/`Save`, new key `"consoleCompact"`, position-only like the overlay's) + `ScreenClamp.Clamp` + `SystemParameters.VirtualScreen*` (the `OverlayWindow.xaml.cs:38-50` pattern); theme brushes already used in this window (`ControlFillColorSecondaryBrush`, `SystemFillColorCriticalBrush`/`-BackgroundBrush`, `AccentFillColorDefaultBrush`, `TextOnAccentFillColorPrimaryBrush`) plus `SystemFillColorCautionBackgroundBrush` (precedented in `SettingsPage.xaml:38`).
- Produces: `LiveViewContext` record widens to 4 components (`..., CompactConsoleViewModel Compact` — declared in and consumed only by `LiveViewWindow`; verify with a search that no other file references `LiveViewContext` before editing); `LiveViewWindow` ctor gains a trailing `WindowStateStore stateStore` parameter; `TrayIconHost` ctor gains a `WindowStateStore windowState` parameter before `mainWindowFactory` (no test constructs `TrayIconHost` — verified).
- Window-chrome note: `LiveViewWindow` is a Wpf.Ui `FluentWindow` (`ExtendsContentIntoTitleBar="True"`), so a caption strip spans the top of the window; at 64px tall it would swallow clicks on the pill's upper half. Entering compact therefore zeroes `WindowChrome.CaptionHeight` (null-guarded; restored on exit).

Steps:
- [ ] **Edit 1 — window attributes: bind Topmost to compact.** In `LiveViewWindow.xaml` replace lines 5–6:
```xml
        Title="LocalScribe - Record console" Height="520" Width="640" MinHeight="300" MinWidth="420"
        WindowBackdropType="None" ExtendsContentIntoTitleBar="True">
```
with:
```xml
        Title="LocalScribe - Record console" Height="520" Width="640" MinHeight="300" MinWidth="420"
        WindowBackdropType="None" ExtendsContentIntoTitleBar="True"
        Topmost="{Binding Compact.IsCompact}">
```
- [ ] **Edit 2 — wrap the root: template swap host.** Replace line 17:
```xml
    <DockPanel TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}">
```
with (the inheritable-foreground marker moves to the NEW root so `PageAndWindowRoots_SetInheritableForeground` keeps meaning what it says; the DockPanel body's indentation is deliberately left as-is — XAML is indentation-insensitive and the smaller diff is easier to review):
```xml
    <Grid TextElement.Foreground="{DynamicResource TextFillColorPrimaryBrush}">
        <!-- Compact mode (design 2026-07-18 section 6): SAME window, template swap - the full
             console collapses and the pill below shows while Compact.IsCompact. -->
        <DockPanel>
            <DockPanel.Style>
                <Style TargetType="DockPanel">
                    <Setter Property="Visibility" Value="Visible" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding Compact.IsCompact}" Value="True">
                            <Setter Property="Visibility" Value="Collapsed" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </DockPanel.Style>
```
- [ ] **Edit 3 — header Compact button.** The pill-button row's mute button currently closes (lines 304–305 — this `</Button>` + `</StackPanel>` pair is unique in the file):
```xml
                    </Button>
                    </StackPanel>
```
Replace with:
```xml
                    </Button>
                    <Button Style="{StaticResource PillButton}"
                            Command="{Binding Compact.ToggleCompactCommand}"
                            ToolTip="Compact mode - collapse to a small always-on-top pill">
                        <StackPanel Orientation="Horizontal">
                            <ui:SymbolIcon Symbol="ArrowMinimize24" FontSize="18" Margin="0,0,6,0" />
                            <TextBlock Text="Compact" VerticalAlignment="Center" />
                        </StackPanel>
                    </Button>
                    </StackPanel>
```
- [ ] **Edit 4 — compact pill template + close the root.** The file currently ends (lines 424–425 — the final `</DockPanel>` + `</ui:FluentWindow>` pair is unique):
```xml
    </DockPanel>
</ui:FluentWindow>
```
Replace with:
```xml
        </DockPanel>
        <!-- The compact pill (~420x64, design 2026-07-18 section 6). UI-ONLY over existing state
             (locked): dot/elapsed mirror the header, the last-line slot is Compact.LastLineText
             (listening/warm-up hint while empty), and mute/stop route through the SAME
             Session.MuteLocalCommand / Session.StopCommand the full console binds. The mute
             button's color states collapse the mute/device-mute/app-mute-advisory banners -
             tooltip carries the banner text, never lost. DragMove + position persistence live in
             code-behind (CompactPill_MouseLeftButtonDown). -->
        <Border MouseLeftButtonDown="CompactPill_MouseLeftButtonDown"
                CornerRadius="14" Background="{DynamicResource ControlFillColorSecondaryBrush}"
                Padding="10,0">
            <Border.Style>
                <Style TargetType="Border">
                    <Setter Property="Visibility" Value="Collapsed" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding Compact.IsCompact}" Value="True">
                            <Setter Property="Visibility" Value="Visible" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </Border.Style>
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <Ellipse Grid.Column="0" Width="10" Height="10" VerticalAlignment="Center" Margin="0,0,8,0">
                    <Ellipse.Style>
                        <Style TargetType="Ellipse">
                            <Setter Property="Fill" Value="{DynamicResource SystemFillColorCautionBrush}" />
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Session.State}" Value="Recording">
                                    <Setter Property="Fill" Value="{DynamicResource SystemFillColorCriticalBrush}" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Ellipse.Style>
                </Ellipse>
                <TextBlock Grid.Column="1" Text="{Binding Session.Elapsed}" FontFamily="Consolas"
                           VerticalAlignment="Center" Margin="0,0,10,0" />
                <TextBlock Grid.Column="2" Text="{Binding Compact.LastLineText}"
                           ToolTip="{Binding Compact.LastLineText}"
                           TextTrimming="CharacterEllipsis" VerticalAlignment="Center" Margin="0,0,10,0" />
                <Button Grid.Column="3" Command="{Binding Session.MuteLocalCommand}"
                        ToolTip="{Binding Compact.MuteTooltip}" Focusable="False"
                        Width="34" Height="30" Margin="0,0,4,0" Padding="0">
                    <Button.Style>
                        <!-- BasedOn keeps the Wpf.Ui implicit button look; the triggers layer the
                             four mute-pill states over it (distinct theme brushes per state). -->
                        <Style TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Compact.MuteState}" Value="Muted">
                                    <Setter Property="Background" Value="{DynamicResource AccentFillColorDefaultBrush}" />
                                    <Setter Property="Foreground" Value="{DynamicResource TextOnAccentFillColorPrimaryBrush}" />
                                </DataTrigger>
                                <DataTrigger Binding="{Binding Compact.MuteState}" Value="DeviceMuted">
                                    <Setter Property="Background" Value="{DynamicResource SystemFillColorCriticalBackgroundBrush}" />
                                    <Setter Property="BorderBrush" Value="{DynamicResource SystemFillColorCriticalBrush}" />
                                </DataTrigger>
                                <DataTrigger Binding="{Binding Compact.MuteState}" Value="AppMuteAdvisory">
                                    <Setter Property="Background" Value="{DynamicResource SystemFillColorCautionBackgroundBrush}" />
                                    <Setter Property="BorderBrush" Value="{DynamicResource SystemFillColorCautionBrush}" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Button.Style>
                    <ui:SymbolIcon FontSize="16">
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
                </Button>
                <Button Grid.Column="4" Command="{Binding Session.StopCommand}" ToolTip="Stop recording"
                        Focusable="False" Width="34" Height="30" Margin="0,0,4,0" Padding="0">
                    <ui:SymbolIcon Symbol="Stop24" FontSize="16" />
                </Button>
                <Button Grid.Column="5" Command="{Binding Compact.ToggleCompactCommand}"
                        ToolTip="Expand the console" Focusable="False" Width="34" Height="30" Padding="0">
                    <ui:SymbolIcon Symbol="ArrowMaximize24" FontSize="16" />
                </Button>
            </Grid>
        </Border>
    </Grid>
</ui:FluentWindow>
```
- [ ] **Code-behind: widen the ctor and add the geometry members.** In `src\LocalScribe.App\LiveViewWindow.xaml.cs`:
  1. Add `using System.Windows.Input;` to the using block (between `using System.Windows.Controls;` and `using System.Windows.Media;`).
  2. Replace the context record (line 19):
```csharp
    public sealed record LiveViewContext(SessionViewModel Session, TranscriptLinesViewModel Lines, RecordingConsoleViewModel Console);
```
with:
```csharp
    public sealed record LiveViewContext(SessionViewModel Session, TranscriptLinesViewModel Lines,
        RecordingConsoleViewModel Console, CompactConsoleViewModel Compact);
```
  3. Replace the fields + ctor opening (lines 21–33):
```csharp
    private readonly TranscriptLinesViewModel _lines;
    private readonly ISettingsService _settings;
    private readonly RecordingConsoleViewModel _console;
    private bool _stickToBottom = true;
    private bool _hwndReady;
    private readonly DispatcherTimer _remoteTargetPoll = new() { Interval = TimeSpan.FromSeconds(2) };

    public LiveViewWindow(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, ISettingsService settings)
    {
        InitializeComponent();
        (_lines, _settings, _console) = (lines, settings, console);
        DataContext = new LiveViewContext(session, lines, console);
```
with:
```csharp
    private readonly TranscriptLinesViewModel _lines;
    private readonly ISettingsService _settings;
    private readonly RecordingConsoleViewModel _console;
    // Compact mode (design 2026-07-18 section 6): VM + placement store. The VM (like this
    // hide-on-close singleton and its Changed subscription) intentionally lives for the app
    // lifetime, so it is never disposed here.
    private readonly CompactConsoleViewModel _compact;
    private readonly WindowStateStore _stateStore;
    private Rect? _normalBounds;
    private WindowState _normalWindowState = WindowState.Normal;
    private double _normalCaptionHeight = -1;
    private bool _stickToBottom = true;
    private bool _hwndReady;
    private readonly DispatcherTimer _remoteTargetPoll = new() { Interval = TimeSpan.FromSeconds(2) };

    public LiveViewWindow(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, ISettingsService settings, WindowStateStore stateStore)
    {
        InitializeComponent();
        (_lines, _settings, _console, _stateStore) = (lines, settings, console, stateStore);
        _compact = new CompactConsoleViewModel(session, lines, settings);
        DataContext = new LiveViewContext(session, lines, console, _compact);
        // Geometry rides the VM's IsCompact flips (auto-compact-on-start included): the XAML
        // triggers swap the templates, this swaps the window shell around them.
        _compact.PropertyChanged += OnCompactChanged;
        if (_compact.IsCompact) EnterCompactLayout();   // constructed mid-recording with the option on
```
  4. Replace `OnClosing` (lines 119–123):
```csharp
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;                       // hide, never close
        Hide();
    }
```
with:
```csharp
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;                       // hide, never close
        if (_compact.IsCompact)                // remember the pill spot across hide/show
            _stateStore.Save("consoleCompact", new WindowPlacement(Left, Top));
        Hide();
    }
```
  5. Insert the compact geometry block immediately after the replaced `OnClosing` (before `private static ScrollViewer? FindScrollViewer`):
```csharp

    // ---- Compact mode geometry (design 2026-07-18 section 6). UI-only: nothing in here touches
    // capture or session state; the VM owns WHEN, this owns only the window shell. ----

    private void OnCompactChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CompactConsoleViewModel.IsCompact)) return;
        if (_compact.IsCompact) EnterCompactLayout(); else ExitCompactLayout();
    }

    private void EnterCompactLayout()
    {
        // Remember the full-console geometry. Normalize a maximized window first: Left/Top/
        // Width/Height of a maximized window report its RESTORE bounds, not what is on screen.
        _normalWindowState = WindowState;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        _normalBounds = new Rect(Left, Top, Width, Height);
        // FluentWindow keeps a WindowChrome whose caption strip would swallow clicks on the top
        // half of a 64px pill - zero it while compact (restored on exit). Null-guarded: if the
        // library ever stops attaching one, compact still works, just with a caption-drag strip.
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            _normalCaptionHeight = chrome.CaptionHeight;
            chrome.CaptionHeight = 0;
        }
        ResizeMode = ResizeMode.NoResize;
        (MinWidth, MinHeight) = (CompactConsoleViewModel.PillWidth, CompactConsoleViewModel.PillHeight);
        (Width, Height) = (CompactConsoleViewModel.PillWidth, CompactConsoleViewModel.PillHeight);
        // Remembered pill position, clamped to the visible virtual screen (a monitor may be gone
        // since last run - the overlay pill's exact restore pattern); NaN falls back to top-right.
        var saved = _stateStore.Load("consoleCompact");
        var (x, y) = ScreenClamp.Clamp(saved?.X ?? double.NaN, saved?.Y ?? double.NaN,
            CompactConsoleViewModel.PillWidth, CompactConsoleViewModel.PillHeight,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        (Left, Top) = (x, y);
    }

    private void ExitCompactLayout()
    {
        _stateStore.Save("consoleCompact", new WindowPlacement(Left, Top));
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome && _normalCaptionHeight >= 0)
            chrome.CaptionHeight = _normalCaptionHeight;
        ResizeMode = ResizeMode.CanResize;
        (MinWidth, MinHeight) = (420, 300);    // the XAML-authored minimums (window element line 5)
        if (_normalBounds is { } b)
            (Left, Top, Width, Height) = (b.X, b.Y, b.Width, b.Height);
        WindowState = _normalWindowState;
    }

    // Drag-to-move on the pill background (buttons handle their own mouse-down, so they never
    // reach this). DragMove returns when the drag ends - persist the spot right there.
    private void CompactPill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        DragMove();
        _stateStore.Save("consoleCompact", new WindowPlacement(Left, Top));
    }
```
- [ ] **Wire the store through `TrayIconHost`.** In `src\LocalScribe.App\TrayIconHost.cs`:
  1. After the field `private readonly Func<MainWindow> _mainWindowFactory;` (line 26) insert:
```csharp
    private readonly WindowStateStore _windowState;
```
  2. Replace the ctor signature + assignments (lines 30–41):
```csharp
    public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, StoragePaths paths,
        ISettingsService settingsService, Func<MainWindow> mainWindowFactory)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(mainWindowFactory);
        (_session, _lines, _console, _paths, _settingsService, _mainWindowFactory) =
            (session, lines, console, paths, settingsService, mainWindowFactory);
```
with:
```csharp
    public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, StoragePaths paths,
        ISettingsService settingsService, WindowStateStore windowState,
        Func<MainWindow> mainWindowFactory)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(windowState);
        ArgumentNullException.ThrowIfNull(mainWindowFactory);
        (_session, _lines, _console, _paths, _settingsService, _windowState, _mainWindowFactory) =
            (session, lines, console, paths, settingsService, windowState, mainWindowFactory);
```
  3. Replace the window construction in `OpenLiveView` (line 112):
```csharp
        _liveView ??= new LiveViewWindow(_session, _lines, _console, _settingsService);
```
with:
```csharp
        _liveView ??= new LiveViewWindow(_session, _lines, _console, _settingsService, _windowState);
```
- [ ] **Update the composition call-site.** In `src\LocalScribe.App\App.xaml.cs` the tray construction currently reads (lines 476–477; `windowState` is already in scope from line 116):
```csharp
        _tray = new TrayIconHost(session, lines, console, comp.Paths, comp.Settings,
            mainWindowFactory: () => new MainWindow(mainVm, windowState, comp.Settings,
```
Replace the first line with:
```csharp
        _tray = new TrayIconHost(session, lines, console, comp.Paths, comp.Settings, windowState,
            mainWindowFactory: () => new MainWindow(mainVm, windowState, comp.Settings,
```
- [ ] **Build 0-warning + full App/Core suites green.** Run:
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\compact-mode\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\compact-mode\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\compact-mode\
```
Expected: build 0 warnings (if `ArrowMinimize24`/`ArrowMaximize24` are somehow absent from this Wpf.Ui version's `SymbolRegular`, the build fails loudly — substitute the nearest arrow-minimize/maximize member the enum DOES define; do not ship a text-glyph fallback silently); App suite all green — including `XamlHygieneTests` (the inheritable-foreground marker still present exactly once on the new root Grid; no hardcoded ARGB brushes — every compact brush is a Fluent theme resource; `Fluent.Shared.xaml` untouched); Core green (2 known fixture fails pre-existing and unrelated).
- [ ] **Manual smoke (WPF — not unit-testable).** Launch the app, open the Record console, start a recording, then:
  1. **Toggle:** header "Compact" collapses to a ~420×64 pill; the pill's expand button restores the full console at its previous position/size (including from maximized). Toggle both ways repeatedly — no drift.
  2. **Topmost only while compact:** the pill stays over other windows; after expanding, the full console does NOT stay always-on-top.
  3. **Pill contents:** red dot + running elapsed; the last transcript line updates as you speak (before any line: "Listening - transcript appears a few seconds after speech."); long lines ellipsize with the full text in the tooltip.
  4. **Drag + persistence:** drag the pill by its background (buttons still click, including in the pill's TOP half — the caption-strip zeroing); collapse-expand-collapse and hide/reopen — the pill returns to where it was dragged. With a saved position from a disconnected monitor (edit `window-state.json`'s `consoleCompact` to e.g. x=9999), the pill re-enters clamped on-screen.
  5. **Mute pill states:** click mute → accent-colored pill + "Unmute" tooltip; unmute → normal. Mute the mic DEVICE in Windows → the pill turns critical-tinted with the device-mute text. On a real Webex call, mute in Webex while recording unmuted ≥5 s → the pill turns caution-tinted and its tooltip carries the exact advisory banner line (the banner is collapsed, never lost); expanding shows the same banner in full.
  6. **Stop from the pill:** the full console restores immediately (Finalizing visible full-size), and the recording finalizes exactly as a full-console stop does.
  7. **Auto-compact setting:** Settings > Recording > tick the new checkbox → next Start collapses the console on open; untick (default) → Start opens the full console. Ctrl+Shift+M still toggles mute while compact.
  8. **Themes:** flip Windows light/dark — pill background, mute-state tints, and text stay readable in both.
- [ ] **Commit.**
```
git add src/LocalScribe.App/LiveViewWindow.xaml src/LocalScribe.App/LiveViewWindow.xaml.cs src/LocalScribe.App/TrayIconHost.cs src/LocalScribe.App/App.xaml.cs
git commit -m "feat(app): console compact mode - 420x64 topmost pill template, drag + clamped position persistence

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-review

**(a) Spec coverage — every design §6 clause maps to tasks:**
- "Same window object, template swap (visual state / DataTrigger), `Topmost` only while compact, ~420×64" → **Task 4** (root Grid hosting both templates switched by `DataTrigger` on `Compact.IsCompact`; `Topmost="{Binding Compact.IsCompact}"`; `PillWidth/PillHeight` consts 420/64 shared by VM, XAML geometry, and clamp math).
- "Recording dot + elapsed; last **finalized** live line (single line, end-trimmed); mute pill (banners collapse to a colored state + tooltip — never lost); stop; expand" → **Task 2** (`PillLine` — the line source is the SAME `TranscriptLinesViewModel.Lines` the full console renders, which is rebuilt per finalized merger line, so "last finalized" holds by construction; `MutePill` four-state mapping with the advisory tooltip = the banner's exact text) + **Task 4** (pill markup binding `Session.Elapsed`, `Session.MuteLocalCommand`, `Session.StopCommand`, `Compact.ToggleCompactCommand` — zero duplicated state or new commands).
- "Drag-to-move (`DragMove`), position persisted, clamped to a visible work area on restore (multi-monitor safety)" → **Task 4** (`CompactPill_MouseLeftButtonDown` → `DragMove` + save; `"consoleCompact"` key in the existing `WindowStateStore`; restore via the existing, tested `ScreenClamp` against `SystemParameters.VirtualScreen*` — the exact overlay-pill precedent) + **Task 2**'s pin tests (monitor-removed, partially-off, negative-origin multi-monitor at the pill's 420×64).
- "Entry: a Compact toggle in the console header; Settings option 'collapse console when recording starts' (**default off**). Stop from the pill restores the full console" → **Task 4 Edit 3** (header button), **Task 1 + Task 3** (setting, default false, round-trip + commit tests), **Task 2** (`NextCompact`: opt-in Idle→Recording; any non-live state → false).
- "Warm-up indicator carries into the pill" → **Task 2** `PillLine(listening: ShowListeningHint)` reuses the console's exact existing empty-state surface (encoded fact: no literal "Preparing" state exists in this app — the listening hint IS the console's warm-up indicator since the record-console-polish round).
- Steno pattern "coexist-don't-take-over" → hide-on-close, no-focus-steal `Focusable="False"` pill buttons; "stop is the new pause" NOT adopted (pause/mute keep their own semantics — `NextCompact` keeps the pill through Pause).
- Locked rules re-checked: no task touches `SessionController`, capture legs, Start/Stop/Pause flow, or CanExecute gates; the pill writes no markers (its mute click IS `MuteLocalCommand`); transcript text is verbatim (layout-only single-line collapse, tooltip carries it whole); no global hotkeys added; default OFF means zero behavior change for a user who never opts in.

**(b) Placeholder scan:** no TBD / "similar to Task N" / stub bodies anywhere — every step carries full test code, full implementation code, and quotes the exact current code being replaced (anchors re-grounded against the working tree at `82546aa`: `Settings.cs:38/49`, `SettingsTests.cs:185-187`, `SettingsPageViewModel.cs:371-379`, `SettingsPage.xaml:85-91`, `ScreenClampTests.cs:36-37`, `LiveViewWindow.xaml:5-6/17/304-305/424-425`, `LiveViewWindow.xaml.cs:19/21-33/119-123`, `TrayIconHost.cs:26/30-41/112`, `App.xaml.cs:476-477`). Every run command names its exact filter, the isolated `compact-mode` BaseOutputPath, and the expected failure/pass output. The two knowingly non-red-first test steps are labeled as such (the ScreenClamp rows are pins of existing behavior for a new caller).

**(c) Type consistency across tasks:** `ConsoleSetting.CompactOnStart : bool` (Task 1) → read by `CompactConsoleViewModel.NextCompact(SessionState, SessionState, bool, bool) : bool` via `ISettingsService.Current.Console` (Task 2) and by `SettingsPageViewModel.CompactConsoleOnStart : bool` via the `Commit` chain (Task 3). `MutePill(bool, bool, AppMuteBannerKind, string) : (CompactMuteState, string)` consumes `SessionViewModel.IsLocalMuted/MicDeviceMuted : bool` and `AppMuteBannerKind/AppMuteBannerText` (existing, `AppMuteBannerKind` lives in `LocalScribe.App.Services` — the VM file adds that using); its tuple deconstructs onto `MuteState : CompactMuteState` / `MuteTooltip : string`, bound in Task 4 by enum-name `DataTrigger` values ("Muted"/"DeviceMuted"/"AppMuteAdvisory"). `PillLine(TranscriptLineViewModel?, bool) : string` matches `Lines : ObservableCollection<TranscriptLineViewModel>` and `ShowListeningHint : bool` (both existing/public — tests add to `Lines` directly). `WindowStateStore.Load/Save(string, WindowPlacement)` and `ScreenClamp.Clamp(...) : (double, double)` are existing signatures (position-only `WindowPlacement(Left, Top)`, the overlay precedent). `LiveViewContext` widens to 4 components consumed only by this window's own bindings; `LiveViewWindow`/`TrayIconHost` ctor widenings have exactly one call site each (`TrayIconHost.OpenLiveView`, `App.xaml.cs:476`) and no test constructs either type (verified). All members tests touch are `public` (no InternalsVisibleTo — verified); `GatedEngineFactory`/`LiveTestDoubles`/`FakeSettingsService` are already reachable from App.Tests. All new UI strings are ASCII; no emojis anywhere.

**(d) Deliberate scope notes:** the silent-leg warnings (`MicSilent`/`RemoteSilent`) are NOT collapsed onto the pill — design §6 names exactly the mute/device-mute/app-mute banner family for the pill's colored states; the silence warnings remain a full-console surface (one click away via Expand) and can be promoted in a later round if smoke shows they are missed. The pill keeps the recording dot's existing Recording/Paused coloring rather than adding a pause button — Pause stays a full-console/tray/overlay action, per the design's pill content list.
