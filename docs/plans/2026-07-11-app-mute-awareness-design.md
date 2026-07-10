# App-mute awareness (advisory banners) & Record-console controls — design (2026-07-11)

Supersedes section 3 ("advisory UIA banner") of `docs/plans/2026-07-10-mute-controls-and-echo-dedup-design.md`.
The mute-controls branch merged to master @ 3f4b5c4 shipped Phases A-C (LocalScribe "Mute my side",
device-mute awareness, bidirectional dedup) and the Phase-D spike TOOL only. This design replaces the
planned app-toolbar scraping approach with a strictly better signal discovered during the 2026-07-11
on-box probes, and adds the Record-console control restyle the user requested the same day.

## What changed since the 2026-07-10 design (evidence, all on-box 2026-07-11)

1. **Windows 11 call-mute integration is live on this box for Webex.** During a real Webex call, the
   taskbar microphone tray icon's flyout reads "Muted: Webex / To toggle mute button, press
   Win+Alt+K. / Apps using your microphone: Webex" (user screenshot). The 2026-07-10 research
   statement "no conferencing app tells Windows when you mute yourself inside the app" is therefore
   WRONG for call-mute-integrated apps: Webex reports its in-app mute state to the shell.
2. **The app-side API is `VoipCallCoordinator` (WinRT) — there is no public cross-process READ API.**
   But shell tray `NotifyItemIcon` elements carry their live status text in their UIA `Name` (probe:
   "Steam - synchronizing", "OneDrive - Personal ..."), so the mic tray icon's Name is expected to
   carry the same "Muted: <App>" / "<App> is using your microphone" strings the flyout shows. ONE
   shell-owned element, stable across app updates. (Exact strings to be captured by the revised
   runbook — the one remaining evidence gate.)
3. **WASAPI capture-session mute is a dead end** (probe 2026-07-11): with the user muted in a Teams
   lobby, ms-teams' capture session read `state=Inactive, MUTE=False`. Session mute does not carry
   in-app mute. (Confirms the 2026-07-10 research on the general case.)
4. **Teams released the mic in an empty lobby** (no participants): the tray mic icon disappeared and
   the session went Inactive. Whether Teams registers with call-mute in a REAL meeting (participants
   joined) is unknown — a free runbook check, not a design dependency.
5. **User corrections:** the Webex meeting toolbar NEVER auto-hides on this setup (the old runbook's
   auto-hide steps were misinformed); Teams' own meeting-toolbar DOM ids are session-generated GUIDs
   (probe) — name-matching only, i.e. fragile.

## Locked rules (carried forward, not revisited)

- The app-mute signal is ADVISORY: it never writes transcript markers and never gates recording.
  Markers come only from exact signals — LocalScribe's own mute button/commands and device mute.
- The banner's one-click actions route through the existing `SetLocalMuteAsync`, so any markers they
  produce are caused by the USER'S CLICK (an exact signal), not by the UIA reading.
- Global hotkeys remain dropped (Stage 4 decision). In-app KeyBindings are fine.

## 1. The product story: three tiers, nothing else

LocalScribe shows exactly three mute states, and promises only what each tier can actually deliver:

| Tier | Signal | Reliability | Shipped |
|---|---|---|---|
| 1. LocalScribe's own mute | `SetLocalMuteAsync` (button, banner actions, Ctrl+Shift+M in-app) | exact, always | yes (2026-07-11 merge) |
| 2. Mic device (endpoint) mute | `IEndpointMuteObservable` (headset hardware switch etc.) | exact, always, every app | yes (2026-07-11 merge) |
| 3. Call app's own mute | Win11 call-mute tray signal | only when the app reports it to Windows | THIS design |

Tier 3 has ZERO per-app code. The watcher parses whatever app name the shell reports. Webex lights it
up today; any app that adopts the integration lights up automatically; an app that doesn't (Zoom,
possibly Teams) simply produces no tier-3 state and no banner — the UI never pretends otherwise.
Consciously rejected as inconsistency generators: per-app toolbar scraping (Webex AND Teams), global
keyboard-hook hotkey inference (edge-mirroring drifts, keylogger-shaped, collides with the marker
rule), auto-follow (see section 3).

## 2. The tray-signal watcher

### 2.1 Signal source seam

```
namespace LocalScribe.App.Services;

/// <summary>One reading of the Windows 11 call-mute tray signal (design 2026-07-11 section 2).
/// Unknown = no integrated call app is reporting (icon absent/unparseable) - the fail-open state.</summary>
public enum AppMuteState { Unknown, Muted, Live }
public readonly record struct AppMuteReading(AppMuteState State, string? AppName);

public interface IAppMuteSignalSource
{
    AppMuteReading Read();   // called on the poll tick; must never throw (fail-open to Unknown)
}
```

- Production implementation `TrayMuteSignalSource` (LocalScribe.App): walks the taskbar tray via
  `System.Windows.Automation` (built into the Windows Desktop SDK — NO FlaUI dependency in product
  assemblies; FlaUI stays confined to `tools/UiaProbe`). Finds the mic `NotifyItemIcon` button and
  hands its `Name` to the parser. Any exception or absence → `Unknown`.
- `TrayTextParser` (pure static, unit-tested): `Name` string → `AppMuteReading`. v1 patterns
  (English; the exact strings come from the runbook evidence and are pinned in tests):
  `"Muted: <App>"` → Muted; `"<App> is using your microphone"` (and the observed flyout variants) →
  Live; anything else → Unknown. Patterns live in this one class only.
- Fake for tests: `FakeAppMuteSignalSource { AppMuteReading Next; }` — same seam pattern as
  `IEndpointMuteObservable`.

### 2.2 Watcher lifecycle

- `AppMuteWatcher` (LocalScribe.App service, owns a `DispatcherTimer`-driven poll like the playback
  Tick pattern): polls `IAppMuteSignalSource.Read()` every 2 s, ONLY while the session controller is
  Recording (subscribes to `StateChanged`; starts on Recording, stops otherwise; no polling while
  Idle/Paused — a paused session records nothing on either side, so a mismatch is meaningless).
- Raises `ReadingChanged(AppMuteReading)` on change; the VM marshals via `_dispatch` (same contract
  as every controller event).

### 2.3 Mismatch logic (pure, unit-tested)

`AppMuteBannerState Evaluate(AppMuteReading reading, bool localMuted)`:

| App signal | LocalScribe side | Banner |
|---|---|---|
| Muted | not muted | "**<App> looks muted — LocalScribe is still recording your side.**" + [Mute my side] |
| Live | muted | "**You are unmuted in <App> — LocalScribe is not recording your side.**" + [Unmute] |
| Unknown, or states agree | — | none |

- Debounce: a mismatch must persist >= 5 s of consecutive readings before the banner shows (normal
  mute choreography must not flicker it); it clears IMMEDIATELY when the mismatch resolves.
  Implemented inside the pure evaluator (fed reading + timestamp), so it is fully unit-testable.
- Banner text always says "looks" for the app side — the signal is advisory by contract.
- The two banners are mutually exclusive by construction (they require opposite `localMuted`).

### 2.4 VM + XAML

- `SessionViewModel`: `AppMuteBannerKind` observable (None/AppMutedButRecording/AppLiveButMuted) +
  `AppMuteBannerText`; the banner's action button binds to the EXISTING `MuteLocalCommand` (both
  directions are a toggle of the current state — no new command). Reset to None in `StartAsync`
  alongside the other flag resets; detach in Dispose. Same named-handler/dispatch pattern as
  `LocalMuteChanged`/`MicDeviceMuteChanged`.
- `LiveViewWindow.xaml`: one warning row (WarningText style) in the existing warning-rows panel,
  visible when `AppMuteBannerKind != None`, with the inline action pill.

### 2.5 Failure containment

- Signal source never throws (fail-open Unknown). Parser is total. Watcher poll wraps Read() in
  try/catch anyway (belt and braces) — a UIA hiccup must never touch recording.
- If the shell changes its tray strings (OS update), the parser degrades to Unknown → banners
  disappear; recording is untouched. The strings live in one class; the runbook procedure re-captures
  them in minutes.

## 3. Rejected alternatives (recorded so they stay rejected)

- **Global keyboard hook mirroring app hotkeys (Ctrl+Shift+M / Alt+A):** mirrors toggle EDGES, not
  state — one missed event (mute clicked with the mouse, push-to-talk, combo pressed with no active
  call) inverts the sync silently and permanently; requires a WH_KEYBOARD_LL hook (keylogger-shaped,
  AV-hostile, interacts with the Covenant Eyes agent on this box); auto-muting from an inference
  violates the exact-signals marker rule. Rejected 2026-07-11 after discussion with the user.
- **App-toolbar UIA scraping (Webex/Teams):** per-app selector maintenance, Teams ids are
  session-generated GUIDs, localization-fragile. Superseded by the tray signal; NOT kept as a v2
  fallback — if Webex's integration ever regresses, tier 3 goes dark for Webex rather than us
  building a scraper.
- **WASAPI session mute:** probe-verified dead (2026-07-11).
- **`VoipCallCoordinator` direct read:** no public cross-process API.
- **Auto-follow (LocalScribe mutes itself when the app mutes):** deferred, NOT designed. It would
  re-open the locked advisory-only rule (auto-action + markers from a scraped signal). Revisit only
  after the banner has proven signal reliability across real calls, as an explicit opt-in with its
  own design pass.

## 4. Record-console controls: Webex-style pills + in-app hotkey

- One shared pill button style in App resources (Wpf.Ui-based): rounded ~16 px corners, horizontal
  `SymbolIcon` + label, subtle border, theme-aware hover/pressed.
- Record console (`LiveViewWindow.xaml`): Pause/Resume pill (`Pause24`/`Play24` follows state), Stop
  pill (`Stop24`, red-tinted hover/press like Webex's leave control), Mute my side pill (`Mic24`;
  while muted: `MicOff24`, filled/accent "engaged" look, label flips to "Unmute" — replaces the
  current text-only DataTrigger flip, keeps the same commands/bindings).
- Read view transport row restyled with the same pill style (Play/Pause, Stop, leg mutes with mic
  icons) for consistency. Styling only — zero VM/logic changes anywhere in this section.
- `Ctrl+Shift+M` KeyBinding on the Record console window (`LiveViewWindow`) → `MuteLocalCommand`,
  mirroring Teams muscle memory IN-APP only (fires only when the LocalScribe window has focus; the
  command's existing CanExecute gates it to Recording/Paused). NOT bound in the read view (its mute
  buttons are playback-leg mutes, a different concept). Tooltip on the pill: "Mute my side
  (Ctrl+Shift+M)". No global registration of any kind (locked).

## 5. Evidence gate & sequencing

1. **Runbook v2 first** (`docs/plans/2026-07-10-uia-mute-spike-runbook.md`, rewritten 2026-07-11):
   one real Webex call; capture the tray icon element muted + unmuted (`dotnet run --project
   tools/UiaProbe -- CiscoCollabHost explorer`); note the exact Name strings; optional Teams
   mid-meeting flyout check. The WATCHER implementation plan is written only after these strings are
   in hand (they parameterize `TrayTextParser` and its tests).
2. **Section 4 (pills + hotkey) is NOT evidence-gated** — it can be planned and implemented
   immediately after the in-flight seek-fix branch merges (single-implementer discipline).
3. The seek-clock fix (`fix/mf-flac-seek-clock`) is independent and merges on its own review.

## Testing strategy

- `TrayTextParser`: pure fact table — every captured string variant + garbage + empty → expected
  readings (pinned from runbook evidence).
- Mismatch evaluator: fact table over (reading, localMuted, elapsed) incl. debounce edges (4.9 s no
  banner, 5 s banner, immediate clear, Unknown never banners, agreement never banners).
- Watcher lifecycle: fake source + fake timer/controller-state — polls only while Recording; stops on
  Stop/Pause; ReadingChanged only on change.
- VM: fake source end-to-end — banner kind/text set via dispatch, action button executes
  MuteLocalCommand, reset on StartAsync, detach on Dispose.
- XAML/build gate as usual (0 warnings; App suite green).
- The `TrayMuteSignalSource` UIA walk itself is smoke-only (like the device-mute endpoint path):
  verified via the runbook procedure, not unit tests.
