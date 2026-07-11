# Capture Scope Control — Design (2026-07-12)

## Overview

Three related requests unified into one feature: giving the user real control over
the **remote capture target** — which application's audio (or the full system mix)
LocalScribe records — both **before** a recording (in the Record console) and **live,
during** a recording.

- **Q4** — the console's "Override app" dropdown is a hardcoded, editable list of
  guesses (`CiscoCollabHost`, `Webex`, `Zoom`). Replace it with a **live-refreshing
  picker** of processes currently producing audio, with friendly labels, keeping the
  known names as always-present fallbacks.
- **Q6** — there is no per-session way to force **System mix**; it exists only as a
  persistent Settings default. Make System mix a first-class choice in the console
  that (by being chosen) excludes any per-app target.
- **Q11** — allow switching the remote target **mid-recording** (app ↔ app, or
  app ↔ system mix) without stopping, built on the same picker.

These are one concept — **Remote target** ∈ { `Auto`, `App(image)`, `System mix` } —
surfaced in one control that works at Start and live.

### The CiscoCollabHost/Webex confusion this resolves

Webex renders its call audio in **`CiscoCollabHost.exe`**, not `Webex.exe`
(`RemoteCapturePlanner.cs:22`, a Stage-1 finding). `Webex.exe` is the UI shell;
per-process capturing it yields little/no audio. `AppKindResolver` maps both to
`AppKind.Webex` (`AppKindResolver.cs:15`). The picker shows **friendly, disambiguated
labels** derived from `AppKindResolver`, so the user never has to know this: a known
fallback renders as **"Webex"** (targeting `CiscoCollabHost`), and a live-discovered
process renders as e.g. **"CiscoCollabHost — Webex"**.

## Locked decisions (from brainstorm)

1. **Live scope = full re-target.** During recording the same picker is available;
   the user can switch to any live app OR to System mix at any time.
2. **Confirm only when switching TO system mix.** app↔app and system-mix→app apply
   instantly (like mute/unmute); →system mix shows a one-tap confirm because it starts
   capturing all machine audio (privacy/bleed). Matches the existing bleed advisory.
3. **Q6 collapses into Q4.** "System mix" is an item in the one Remote-target picker;
   choosing it *is* the force, and inherently excludes any app choice. No separate
   checkbox or mode selector.
4. **Friendly labels** via `AppKindResolver`; known fallbacks always present.
5. **Live changes are marker-only.** The start-time `RemoteSnapshot` in `session.json`
   is not rewritten (consistent with the Resume-degrade precedent); an in-transcript
   marker is the evidentiary record of every live switch.
6. **No Call-type (`Medium`) re-seed on a live switch** — `Medium` is a one-time Start
   seed the user can override; the marker records the change.

## Architecture

The composition seam already threads a live `Func<Settings>` through both the
controller and the capture provider (`CompositionRoot.cs:69`):

```csharp
Func<Settings> current = () => micOverride.Apply(remoteOverride.Apply(settingsService.Current));
```

### 1. Generalize `RemoteAppOverride` → `RemoteTargetOverride`

Today `RemoteAppOverride` holds only an app string and can only force `PerProcess`
(`RemoteAppOverride.cs:17-33`). Widen it to carry a full `RemoteSetting` (Mode + App),
the exact twin of `MicOverride` (which already holds a full `MicSetting`,
`MicOverride.cs:10-23`):

```csharp
public sealed class RemoteTargetOverride
{
    private volatile RemoteSetting? _override;                 // null = follow saved Settings
    public RemoteSetting? Override { get => _override; set => _override = value; }
    public Settings Apply(Settings s) => _override is { } r ? s with { Remote = r } : s;
}
```

- The console seeds it from `Settings.Remote` when it opens, sets it as the user picks,
  clears it on Idle (twin of the mic/matter overrides).
- Choosing an app → `RemoteSetting { Mode = PerProcess, App = image }`; choosing System
  mix → `RemoteSetting { Mode = SystemMix }`; Auto → `RemoteSetting { Mode = Auto }`.
- This one change makes Q4 + Q6 work at Start with **zero new Core plumbing** —
  `RemoteCapturePlanner.Plan` already honors all three modes
  (`RemoteCapturePlanner.cs:31-56`).
- Rename touches the pinned tests (`RemoteAppOverrideTests`,
  `RecordingConsoleAppSelectorTests`, `RecordingConsoleViewModelTests`) and
  `CompositionRoot.cs:55,69`.

### 2. Capture-provider seam for explicit targets

`WasapiCaptureSourceProvider.CreateRemote` today re-plans from `_settings().Remote`
(`WasapiCaptureSourceProvider.cs:47-55`) and has no way to be handed an explicit target.
Add an overload:

```csharp
ICaptureSource CreateRemote(IClock clock, RemoteSetting explicitSetting);
```

routing through `RemoteCapturePlanner.Plan(scan, explicitSetting)`. Needed because the
live swap must build a source for a specific requested target rather than whatever the
ambient settings resolve to.

### 3. Controller live seam

Mirror the existing one-leg hot-swap `SetLocalMuteAsync` (`SessionController.cs:718-791`)
and the remote-leg rebuild in `ResumeAsync` (`:644-705`):

```csharp
Task SetRemoteCaptureAsync(RemoteSetting target, CancellationToken ct);
```

## Data & control model (Section 1)

- **Remote target** ∈ { `Auto`, `App(image)`, `System mix` } is the single concept.
- **At Start:** the console's app combo becomes the **Remote target** picker. Items:
  `Auto — detect the call app` / live apps / known fallbacks / `System mix — everything`.
  The old `ShowAppSelector` gating is removed (System mix is just an item now).
- **Live:** the same picker is offered as "Change target". A selection calls
  `SetRemoteCaptureAsync(target)` **and** updates the `RemoteTargetOverride` so a
  later Pause/Resume keeps the new target.
- **Labels:** known fallbacks render friendly — **"Webex"** (→ `CiscoCollabHost`) and
  **"Zoom"** — always present even when not live. Live-discovered processes render as
  `ProcessName` with a resolved suffix when recognized (`CiscoCollabHost — Webex`).
  FullMix apps (Teams/browsers) are shown but annotated **"(captured as system mix)"**
  since `RemoteCapturePlanner` forces that (`RemoteCapturePlanner.cs:28-29,42-44`).
- **Known-targets table:** replace the bare `SuggestedPerProcessApps`
  (`RemoteCapturePlanner.cs:20`) with a small shared table of `(Friendly, Image)`
  pairs (`Webex → CiscoCollabHost`, `Zoom → Zoom`) so labels/fallbacks are testable and
  single-sourced. Add `AppKindResolver.FriendlyName(image)` for live-item labels.

## Live leg-swap mechanics & failure (Section 2)

`SetRemoteCaptureAsync` under the controller `_gate`, requires `State == Recording`:

1. **Build first, commit second.** Create the new `ICaptureSource` via
   `CreateRemote(clock, target)`. If WASAPI activation throws (`COMException` from
   `ProcessLoopbackCapture.Start` → `ActivateAudioInterfaceAsync`,
   `ProcessLoopbackCapture.cs:93-105`), abort: current leg untouched, surface an error,
   **no marker**, revert the picker selection. (Same fail-safe as Resume/unmute.)
2. On success: `s.Remote.StopLegAndFlushAsync()` (VAD flush keeps trailing words) →
   `s.Remote.StartLeg(newSource, …)` on the same pipeline, so the retained FLAC +
   transcript stay continuous (`LiveSourcePipeline.cs:33-118`; `_audioWriter` is a
   readonly field untouched by leg swaps).
3. `AlignedAudioWriter` silence-pads the sub-second WASAPI re-activation gap
   (`AlignedAudioWriter.cs:21-52`) — sample-aligned, no desync (identical to Pause/Resume).
4. Reset the remote `SilentLegMonitor` so a stale "no speech" banner doesn't fire.
5. **If Paused:** the App only updates the `RemoteTargetOverride` (no leg is started while
   paused). No separate stash and **no `ResumeAsync` change** are needed: `ResumeAsync`
   already rebuilds the remote leg via `_captureProvider.CreateRemote` reading
   `_settings().Remote`, which resolves through the composed `Func<Settings>` and therefore
   reflects the updated override automatically. The `RemoteTargetOverride` is the single
   source of truth for the session's current remote target: live changes update it **and**
   hot-swap; paused changes update it only; Start/Resume adopt it for free.
6. **Idempotent:** if the requested target already matches the running leg, no-op (like
   `SetLocalMuteAsync`).
7. **Degrade semantics:** a manual switch TO system mix is a deliberate scope change,
   distinct from the auto-degrade path that sets `RemoteDegraded` +
   `DegradedSystemAudioLoopback` (`SessionController.cs:658-668`). Track separately so
   the degrade marker isn't double-emitted; the recovery direction (→ per-process),
   which today is intentionally unmarked, becomes a genuine new marked event.

## Markers & evidentiary (Section 3)

New constants in `Markers.cs` (all lowercase, matching the existing set):

```csharp
public const string CaptureTargetSystemMix = "capture target changed to full system mix (all machine audio)";
public const string CaptureTargetAppPrefix = "capture target changed to app-only";   // emitted as ": Webex"
```

The emit uses the **actually-resolved** `RemotePlan` so the marker never lies:

- explicit app, captured cleanly → `capture target changed to app-only: Webex`
- explicit system mix → `capture target changed to full system mix (all machine audio)`
- app that fell back (not active / Teams-browser) → reuse the planner's `Notice`, e.g.
  `requested app 'Zoom' has no active render session; capturing full system mix`

Written via `s.Outbox.Writer.TryWrite(new MarkerAt(msg, s.Clock.ElapsedMs))`, exactly
like `OnDeviceMuteChanged`. The start-time `RemoteSnapshot` in `session.json` is **not**
rewritten, so the Sessions "Source" column keeps showing the start source; mid-session
switches live in the transcript markers only.

## UI (Section 4) — all in `LiveViewWindow.xaml` (the one console window)

- **At Start (idle):** replace the app-selector block (`LiveViewWindow.xaml:36-51`) with
  the **Remote target** dropdown bound to `Console.RemoteTargetOptions` /
  `Console.SelectedRemoteTarget`. Keep the "Applies to this recording only" note.
  Default selection is `Auto`. Plain (non-editable) dropdown — this removes today's
  free-text box and the Cisco guesswork. **Open decision:** whether to keep a free-text
  "Other…" escape hatch for arbitrary process images (see Open Questions).
- **During recording:** a dedicated row under the Pause/Stop/Mute toolbar
  (`LiveViewWindow.xaml:96-186`) — `Remote: CiscoCollabHost — Webex   [ Change target ▾ ]`
  — using the same live-refreshing list. Non–system-mix picks apply instantly;
  → System mix pops the confirm dialog first. Use a 2-column Grid (text `*`, control
  `Auto`) so it doesn't clip at `MinWidth=420`, mirroring the app-mute banner fix
  (`LiveViewWindow.xaml:207-224`).
- **Live refresh:** while the console window is visible, poll `IAudioSessionScanner.Scan()`
  on a background timer **~every 2 s** (plus an immediate refresh on window-activate /
  dropdown-open), marshaled to the UI via the injected dispatcher; polling stops when the
  window is hidden. Dedup by process name (`OrdinalIgnoreCase`), friendly-labelled,
  fallbacks pinned. `Scan()` (`WasapiSessionScanner.cs:17-40`) already returns
  `(Pid, ProcessName)` for active render sessions and must run off the UI thread.
- **Confirm dialog:** plain WPF dialog (shown post-pump on user action, so no FluentWindow
  startup gotcha) — *"Capturing full system mix records ALL machine audio — other apps,
  notifications, both sides through your speakers. A marker will be added to the
  transcript. Continue?"* → **[Switch to system mix]** / **[Cancel]**.

### Wiring

- Inject the single `WasapiSessionScanner` (today constructed inline at
  `CompositionRoot.cs:74` and passed only to the capture provider) into
  `RecordingConsoleViewModel` as `IAudioSessionScanner` (hoist to a shared variable; its
  ctor is pinned by tests, so add the param and update the fakes + composition root).
- A `RemoteScopeCommand` (or the picker's selection handler) on `SessionViewModel` calls
  `_controller.SetRemoteCaptureAsync(...)`, mirroring `MuteLocalCommand`
  (`SessionViewModel.cs:90-274`).

## Testing (Section 5)

- **Core:** `RemoteTargetOverride.Apply` (Auto / PerProcess / SystemMix / null-identity);
  `AppKindResolver.FriendlyName`; the shared `(Friendly, Image)` known-targets table.
- **`SessionController.SetRemoteCaptureAsync`:** requires `Recording`; **build-before-commit**
  (a throwing fake capture source leaves the old leg running with **no marker**); emits the
  correct marker per resolved plan (app / system-mix / fallback); **Paused → stashes and
  adopts on Resume**; idempotent no-op when target unchanged; resets the remote silent
  monitor; degrade marker not double-emitted.
- **`RecordingConsoleViewModel`:** options built from a fake scanner (friendly labels,
  dedup, fallbacks pinned, FullMix annotated); live-refresh updates the list;
  → System mix routes through confirm; other picks are instant; selection reverts on
  build failure.
- **Update pinned tests** for the `RemoteAppOverride → RemoteTargetOverride` rename +
  the new picker.

## Out of scope

- Auto-following OS window focus to pick the app (rejected earlier; the console picker
  is the explicit surface).
- Rewriting the start-time `RemoteSnapshot` / re-seeding Call type on a live switch.
- Toolbar/keyboard-hook scraping of app state.

## Open questions (for spec review)

1. **Free-text escape hatch.** Keep an "Other…" entry that accepts an arbitrary process
   image (today's editable-combo capability) for the rare app that isn't currently making
   sound and isn't a known fallback? Default in this design: **no** (rely on live list +
   fallbacks), but easy to add back.
2. **Live refresh cadence.** 2 s poll while visible + refresh-on-open — acceptable, or
   prefer refresh-on-open only (less disk churn, more staleness)?
3. **Marker wording.** Confirm the two marker strings read well in an evidentiary
   transcript.
