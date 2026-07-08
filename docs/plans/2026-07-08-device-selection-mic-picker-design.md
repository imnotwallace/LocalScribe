# Device Selection — Microphone Picker + Remote-App Availability (design)

- **Status:** Validated design (brainstorm session 2026-07-08). Fills the spec §12 "device-config
  (remote mode picker + mic pin)" gap: the `mic.mode = pinned` model exists but capture never
  honors it and there is no device enumeration; and the remote-app picker is hidden outside
  per-process mode.
- **Companion:** `docs/specs/localscribe-specs.md` (§12 device config; §mic snapshot; the
  `pinned microphone unavailable → default` marker). Spec deltas in §9 below.
- **Delivery:** one branch, subagent-driven TDD, per-task + whole-branch review, `--no-ff` merge,
  GUI + hardware smoke runbook.

## 0. Scope

| In | Out (deferred / non-goals) |
|---|---|
| Enumerate active input (capture) devices (`Id` + friendly `Name`) | Raw CsWin32 device interop (use the already-referenced NAudio) |
| Capture HONORS `Mic.Mode == Pinned`: open the mic by `Id` | Live device-swap / hot-plug re-bind mid-recording (Stage 7 hardening) |
| Pinned mic gone → fall back to Communications default + emit the spec §12 marker | Silent rebind of a pin (forbidden — always the marker) |
| **Settings** persistent mic pin (dropdown: follow-default + devices) | A "Refresh devices" button (enumerate when the picker opens) |
| **Record console** per-session mic override (new `MicOverride` seam, reverts on Idle) | Per-session mic persisted to settings.json (override is session-only) |
| Remote-app picker visible in **Auto + Per-process** (hidden only in System-mix) | Output/render device selection; loopback device choice |
| Explicitly chosen remote app captures THAT app for the session | Changing the remote *mode* from the console (stays in Settings) |

**Constraints (binding):**
- **Never silently rebind a pin.** A pinned mic that is absent at Start falls back to the
  Communications default AND writes the `pinned microphone unavailable → default` transcript marker
  (spec §12), so the evidentiary record shows the fall-back happened.
- **Honest snapshot.** `SessionRecord.Devices.Mic` (`MicSnapshot { Mode, Id, Name }`) records the
  device actually captured, not the intended config.
- **Core stays WPF-free.** Enumeration + capture live in `Core`; the pickers are App VMs over an
  injected `ICaptureDeviceEnumerator`.
- Settings auto-save on field commit (no Save button) via `ISettingsService.SaveAsync`; capture
  pulls fresh settings at every Start through the composed `current()` (`CompositionRoot.cs:62`).
- Zero-warning build; xunit; no Unicode emojis in tests.

## 1. Device enumeration (Core)

New humble-object in `src/LocalScribe.Core/Live/`, mirroring `WasapiSessionScanner`
(`WasapiSessionScanner.cs:17-40`):

```
public sealed record AudioDeviceInfo(string Id, string Name);

public interface ICaptureDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> ListInputDevices();   // active capture endpoints
}

public sealed class WasapiCaptureDeviceEnumerator : ICaptureDeviceEnumerator
{
    // new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
    // -> AudioDeviceInfo(d.ID, d.FriendlyName)
}
```

NAudio 2.2.1 is already referenced (`Core.csproj:16`) and used for `MMDeviceEnumerator`
(`MicCaptureSource.cs:28`, `WasapiSessionScanner.cs`). WPF-free; the interface lets VM tests inject
a deterministic fake device list. An enumeration exception returns an empty list (the picker then
offers only "follow default").

## 2. Capture honors a pinned mic (Core)

- **`MicCaptureSource`** (`src/LocalScribe.Core/Audio/MicCaptureSource.cs:25-31`) gains a by-ID open
  path: given a device `Id`, `new MMDeviceEnumerator().GetDevice(id)` instead of
  `GetDefaultAudioEndpoint(...Communications)`. It reports the opened device's `FriendlyName` (+ Id)
  and whether it fell back.
- **`WasapiCaptureSourceProvider.CreateMic`** (`WasapiCaptureSourceProvider.cs:21-25`) branches:
  `settings.Mic.Mode == Pinned && Mic.Id present && Id is in the live device list` → open by Id and
  set `MicSnapshot { Mode = Pinned, Id, Name }`; otherwise open the Communications default and set
  `MicSnapshot { Mode = FollowDefault, Name }`.
- **Pin-unavailable → marker.** When `Mode == Pinned` but the `Id` is not among the current input
  devices, `CreateMic` opens the default (honest `MicSnapshot.Mode = FollowDefault`) AND signals the
  `pinned microphone unavailable → default` marker for Start, via the same seam the degraded
  system-audio marker uses (`Markers.*` + the live pipeline's marker writer). Never a silent rebind.
- The provider re-resolves settings at every Start/Resume (`WasapiCaptureSourceProvider.cs:9-11`), so
  a saved pin (or a per-session override — §3) reaches the next leg with no new event wiring.

## 3. Per-session mic override (App)

New `src/LocalScribe.App/Services/MicOverride.cs`, twin of `RemoteAppOverride`
(`Services/RemoteAppOverride.cs`):

```
public sealed class MicOverride
{
    // set by the console's mic picker; cleared on Idle (session end). Holding a full MicSetting
    // (not just a device) lets a session override the persistent pin BACK to follow-default too.
    public MicSetting? Override { get; set; }

    public Settings Apply(Settings s) => Override is { } m
        ? s with { Mic = m }
        : s;   // no override -> the persistent Settings pin (or follow-default) stands
}
```

The console picker sets `Override` to the chosen `MicSetting` (a `Pinned{id,name}` for a device, or
`FollowDefault` for "follow"); untouched, it stays `null` and the Settings pin stands. Idle clears it.

Composition layers both overrides (`CompositionRoot.cs:62`):
`Func<Settings> current = () => micOverride.Apply(remoteOverride.Apply(settingsService.Current));`
Both overrides revert on Idle (mirrors `RemoteAppOverride`'s idle revert,
`RecordingConsoleViewModel.cs:168-183`), so the session-only choice never leaks into the next session
or into settings.json.

## 4. Settings UI — persistent pin (App)

Replace the read-only mic row (`SettingsPage.xaml:78-82`, VM `MicDisplay`/`MicNote`) with a picker in
`SettingsPageViewModel` (`ViewModels/SettingsPageViewModel.cs`):

- `IReadOnlyList<MicChoice> MicChoices` = a leading **"Windows Communications default (follow)"**
  (`FollowDefault`, null id) + one per enumerated device. Built from the injected
  `ICaptureDeviceEnumerator` at construction / when the page opens.
- `MicChoice SelectedMic` (two-way): selecting a device commits `Mic = { Mode = Pinned, Id, Name }`;
  selecting "follow" commits `Mic = { Mode = FollowDefault }`. Uses the existing auto-save commit
  path (`SettingsPageViewModel.Commit → CommitAsync`, `:343-355`).
- **Absent pin.** If the saved `Mic.Mode == Pinned` but `Mic.Id` is not in the live list, prepend a
  synthetic disabled-looking choice showing `"{Name} (not connected)"` and keep it selected — the pin
  is never silently dropped; capture's own fallback (§2) handles the actual absence at Start.
- `SettingsPage.xaml`: swap the `TextBlock` for a `ComboBox` (`DisplayMemberPath` = the label),
  mirroring the existing `RemoteMode` combo two rows up.

## 5. Record console UI — per-session (App)

In `RecordingConsoleViewModel` (`ViewModels/RecordingConsoleViewModel.cs`) + `LiveViewWindow.xaml`:

- **Mic override picker.** A `MicChoices`/`SelectedMic` pair (same list as Settings, incl. "follow
  default") seeded from `Settings.Mic`; on change it sets `MicOverride.Override` to the chosen
  `MicSetting` (a device pin, or `FollowDefault`); reverts on Idle. Bound as a `ComboBox` beside the
  existing app selector. `MicSummary` (`:63-65`) reflects it.
- **Remote-app availability.** `ShowAppSelector` (`:43`) changes from `Mode == PerProcess` to
  `Mode != SystemMix` — visible in **Auto + Per-process**, hidden only in full system-mix.
- **Chosen app captures that app.** `RemoteAppOverride.Apply` (`RemoteAppOverride.cs:22-26`) changes
  from "swap `Remote.App` only when `Mode == PerProcess`" to: **when an override app is set, return
  `s with { Remote = { Mode = PerProcess, App = override } }`** regardless of the base mode — so
  picking an app in Auto captures exactly that app for the session (with the existing per-process
  system-mix fallback if it produces no audio). No override → the base mode (incl. Auto's
  auto-detect) stands. `RemoteSummary` (`:46-61`) updates to show the chosen app.

## 6. Data flow & persistence

- `settings.json` (`%APPDATA%/LocalScribe/settings.json`, schema v3) already carries
  `mic: { mode, id, name }` and `remote: { mode, app }` (`Settings.cs:42-43`) — no schema bump.
- Settings save → `ISettingsService.Changed`; `RecordingConsoleViewModel` already subscribes
  (`:86`, `:185-195`), so both summaries refresh live when the persistent pin changes.
- Capture pulls the composed `current()` at each Start, so a pin or per-session override reaches the
  next leg without any new event (the only Core change needed is `CreateMic` reading `settings.Mic`).

## 7. Error handling

- **Enumeration throws / no devices:** the picker shows only "follow default"; capture uses the
  Communications default. Never crash the Settings page or console.
- **Pinned device absent at Start:** fall back to default + emit the spec §12 marker (§2). The
  Settings/console picker shows the pin as "(not connected)" but keeps it.
- **A device unplugged mid-recording:** OUT of scope (Stage 7 device-swap); this feature only
  resolves the device at Start.

## 8. Testing

- **Core (unit):** `WasapiCaptureDeviceEnumerator` is thin over NAudio (smoke-verified on hardware);
  the enumeration *interface* is faked for VM tests. `CreateMic` branch: pinned-id-present → opens by
  id, snapshot `Pinned`; pinned-id-absent → default, snapshot `FollowDefault` + marker signaled;
  follow-default unchanged. `MicCaptureSource` by-id fallback logic with a fake enumerator.
- **App (VM):** `SettingsPageViewModel` — select device pins `{Pinned,id,name}`; select follow →
  `FollowDefault`; absent saved pin surfaces "(not connected)" and stays selected; enumeration
  failure → only "follow". `MicOverride.Apply` (override → pinned; none → passthrough) + idle revert.
  `RemoteAppOverride.Apply` — app override forces `PerProcess` from an Auto base; no override
  passthrough. `RecordingConsoleViewModel` — `ShowAppSelector` true in Auto, false in SystemMix; mic
  override set/cleared; summaries.
- **Hardware smoke (user):** pin a real mic in Settings → record → confirm THAT mic is captured
  (`session.json` `devices.mic`); unplug the pinned mic → record → confirm fall-back to default + the
  `pinned microphone unavailable → default` marker; per-session console mic override + revert on Stop;
  remote-app picker visible + effective in Auto.

## 9. Spec deltas (`docs/specs/localscribe-specs.md`)

- §12 device config: the mic picker now EXISTS (Settings persistent pin + console per-session
  override) and capture honors `mic.mode = pinned` by opening the device by `id`, with the
  documented `pinned microphone unavailable → default` fall-back marker.
- Remote-app selection is available in Auto + Per-process (not only Per-process); an explicitly
  chosen app captures that app for the session.
- No settings.json schema change (`mic`/`remote` shapes unchanged).

## 10. Out of scope (named)

Raw CsWin32 device interop; live device hot-plug/swap re-bind mid-recording (Stage 7); a manual
"refresh devices" control; output/render/loopback device selection; changing the remote *mode* from
the console; persisting a per-session mic override to settings.
